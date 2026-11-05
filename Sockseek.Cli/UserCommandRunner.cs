using Sockseek.Server;
using System.Globalization;
using System.Text.Json;
using Sockseek.Api;
using Sockseek.Core.UserProfiles;

namespace Sockseek.Cli;

/// <summary>Remote profile and filesystem-shaped user-share commands.</summary>
internal static class UserCommandRunner
{
    public static async Task<Program.CliExitCode> RunAsync(
        IReadOnlyList<string> args,
        string? remote,
        string? profileNames,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            PrintHelp();
            return Program.CliExitCode.Success;
        }
        if (string.IsNullOrWhiteSpace(remote))
            return Usage("This command requires a configured remote URL (remote = <url> or --remote <url>).");
        if (!IsWord(args, 0, "user"))
            return Usage("Expected a user command.");

        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            commandCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            using HttpClient http = SockseekApiClient.CreateHttpClient(remote);
            var api = new SockseekApiClient(http);
            return Word(args, 1)?.ToLowerInvariant() switch
            {
                "profile" => await RunProfileAsync(api, args, commandCancellation.Token).ConfigureAwait(false),
                "shares" => await RunSharesAsync(
                    api, remote, args, profileNames, commandCancellation.Token).ConfigureAwait(false),
                "shares-page" => await RunSharesPageAsync(api, args, commandCancellation.Token).ConfigureAwait(false),
                "shares-download" => await RunSharesDownloadAsync(
                    api, args, profileNames, commandCancellation.Token).ConfigureAwait(false),
                "shares-cancel" => await RunSharesCancelAsync(api, args, commandCancellation.Token).ConfigureAwait(false),
                _ => Usage("Expected user profile, shares, shares-page, shares-download, or shares-cancel."),
            };
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            return Program.CliExitCode.Cancelled;
        }
        catch (SockseekApiRequestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Program.CliExitCode.WorkFailed;
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Remote user command failed: {Safe(ex.Message)}");
            return Program.CliExitCode.WorkFailed;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<Program.CliExitCode> RunProfileAsync(
        SockseekApiClient api,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        RequirePositionCount(args, 3, ProfileOptions);
        string username = RequiredPosition(args, 2, "username");
        UserProfileDto profile = await api.GetUserProfileAsync(
            username, Flag(args, "--refresh"), cancellationToken).ConfigureAwait(false);

        if (Json(args))
        {
            WriteJson(profile);
            return Program.CliExitCode.Success;
        }

        Console.WriteLine($"{Safe(profile.Username)}: {Presence(profile.Presence)}");
        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            Console.WriteLine();
            Console.WriteLine(SafeMultiline(profile.Description));
        }

        Console.WriteLine();
        WriteProfileFact("Shares", profile.Statistics, CombineCounts(
            profile.SharedFileCount, "files", profile.SharedDirectoryCount, "directories"));
        WriteProfileFact("Upload speed", profile.Statistics,
            profile.AverageUploadSpeed is { } speed ? $"{FormatBytes(speed)}/s average" : null);
        WriteProfileFact("Upload count", profile.Statistics,
            profile.UploadCount is { } count ? count.ToString("N0", CultureInfo.CurrentCulture) : null);
        WriteProfileFact("Upload capacity", profile.Info,
            JoinFacts(
                profile.UploadSlots is { } slots ? $"{slots:N0} slots" : null,
                profile.QueueLength is { } queue ? $"{queue:N0} queued" : null,
                profile.HasFreeUploadSlot is { } free ? free ? "free slot" : "no free slot" : null));

        string pictureMode = (Option(args, "--picture") ?? "auto").ToLowerInvariant();
        ProfilePictureRenderer.ValidateMode(pictureMode);
        bool suppressPicture = pictureMode == "none"
            || Console.IsOutputRedirected
            || Flag(args, "--no-color")
            || Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        if (!suppressPicture && profile.Picture is not null)
        {
            try
            {
                using UserPictureResponse response = await api.GetUserPictureAsync(
                    username, ct: cancellationToken).ConfigureAwait(false);
                string rendered = await ProfilePictureRenderer.RenderAsync(
                    response, pictureMode, cancellationToken).ConfigureAwait(false);
                if (rendered.Length > 0)
                {
                    Console.WriteLine();
                    try
                    {
                        Console.Write(rendered);
                        if (!rendered.EndsWith('\n')) Console.WriteLine();
                    }
                    finally
                    {
                        Console.Write("\u001b[0m");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Profile picture unavailable: {Safe(ex.Message)}");
            }
        }
        else if (!suppressPicture
                 && profile.PictureSection.State != ResourceSectionState.Available)
        {
            Console.Error.WriteLine("Profile picture unavailable.");
        }

        return Program.CliExitCode.Success;
    }

    private static async Task<Program.CliExitCode> RunSharesAsync(
        SockseekApiClient api,
        string remote,
        IReadOnlyList<string> args,
        string? profileNames,
        CancellationToken cancellationToken)
    {
        RequirePositionCount(args, 3, SharesOptions, allowTransferOptions: true);
        if (!Json(args) && (Console.IsInputRedirected || Console.IsOutputRedirected))
            return Usage("Interactive share browsing requires a terminal; use --json or shares-page instead.");
        string username = RequiredPosition(args, 2, "username");
        UserBrowseDto browse = await api.StartUserBrowseAsync(
            username, Flag(args, "--refresh"), cancellationToken).ConfigureAwait(false);

        try
        {
            browse = await WaitForBrowseAsync(api, remote, browse, !Json(args), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Stopped waiting. Browse {browse.BrowseId:D} continues in the daemon; "
                + "use 'user shares-cancel' to cancel it globally.");
            throw;
        }

        if (Json(args))
        {
            WriteJson(browse);
            return browse.State == UserBrowseState.Complete
                ? Program.CliExitCode.Success
                : Program.CliExitCode.WorkFailed;
        }
        if (browse.State != UserBrowseState.Complete)
        {
            Console.Error.WriteLine(
                $"Browse {browse.BrowseId:D} ended as {State(browse.State)}"
                + (browse.Failure is null ? "." : $": {Safe(browse.Failure.Error)}"));
            return Program.CliExitCode.WorkFailed;
        }
        SubmissionOptionsDto options = BuildSubmissionOptions(
            args, profileNames, interactive: true, SharesOptions);
        var browser = new InteractiveShareBrowser(api, browse, options);
        StartUserShareDownloadsResponseDto? submitted = await browser.RunAsync(cancellationToken)
            .ConfigureAwait(false);
        if (submitted is not null)
        {
            WriteResolution(submitted.Resolution);
            Console.WriteLine($"Started workflow {submitted.Workflow.WorkflowId:D}.");
        }
        return Program.CliExitCode.Success;
    }

    private static async Task<Program.CliExitCode> RunSharesPageAsync(
        SockseekApiClient api,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        RequirePositionCount(args, 3, SharesPageOptions);
        Guid browseId = ParseGuid(RequiredPosition(args, 2, "browse ID"), "browse ID");
        long? parentId = ParseLong(Option(args, "--parent"), "--parent");
        long? filesId = ParseLong(Option(args, "--files"), "--files");
        if (parentId is not null && filesId is not null)
            throw new ArgumentException("--parent and --files are mutually exclusive.");
        int limit = ParseInt(Option(args, "--limit"), "--limit", 1, int.MaxValue,
            filesId is not null ? ServerProtocol.DefaultPageSize : ServerProtocol.DefaultNestedPageSize);
        string? query = Option(args, "--query");
        string? cursor = Option(args, "--cursor");

        if (filesId is { } directoryId)
        {
            PageDto<BrowseFileEntryDto> page = await api.GetUserShareFilesAsync(
                browseId, directoryId, query, cursor, limit, cancellationToken).ConfigureAwait(false);
            if (Json(args)) WriteJson(page);
            else
            {
                foreach (BrowseFileEntryDto file in page.Items)
                    Console.WriteLine($"F\t{file.FileId}\t{FormatBytes(file.File.Size)}\t{Safe(file.File.Name)}");
                WriteNextCursor(page.NextCursor);
            }
        }
        else
        {
            PageDto<BrowseDirectoryEntryDto> page = await api.GetUserShareDirectoriesAsync(
                browseId, parentId, query, recursive: false, cursor, limit, cancellationToken)
                .ConfigureAwait(false);
            if (Json(args)) WriteJson(page);
            else
            {
                foreach (BrowseDirectoryEntryDto directory in page.Items)
                {
                    Console.WriteLine(
                        $"D\t{directory.DirectoryId}\t{directory.RecursiveFileCount:N0}\t"
                        + $"{FormatBytes(directory.RecursiveFileBytes)}\t{Visibility(directory.Visibility)}\t"
                        + Safe(directory.Name));
                }
                WriteNextCursor(page.NextCursor);
            }
        }
        return Program.CliExitCode.Success;
    }

    private static async Task<Program.CliExitCode> RunSharesDownloadAsync(
        SockseekApiClient api,
        IReadOnlyList<string> args,
        string? profileNames,
        CancellationToken cancellationToken)
    {
        RequirePositionCount(args, 3, SharesDownloadOptions, allowTransferOptions: true);
        Guid browseId = ParseGuid(RequiredPosition(args, 2, "browse ID"), "browse ID");
        IReadOnlyList<UserShareSelectionDto> selections = ParseSelections(args);
        SubmissionOptionsDto options = BuildSubmissionOptions(
            args, profileNames, interactive: false, SharesDownloadOptions);

        Guid requestId = Option(args, "--request-id") is { } rawRequestId
            ? ParseGuid(rawRequestId, "--request-id")
            : Guid.NewGuid();
        StartUserShareDownloadsResponseDto response = await api.StartUserShareDownloadsAsync(
            browseId,
            new StartUserShareDownloadsRequestDto(requestId, selections, options),
            cancellationToken).ConfigureAwait(false);
        if (Json(args)) WriteJson(response);
        else
        {
            WriteResolution(response.Resolution);
            Console.WriteLine($"Workflow: {response.Workflow.WorkflowId:D}");
            Console.WriteLine($"Request ID: {requestId:D}");
        }
        return Program.CliExitCode.Success;
    }

    private static async Task<Program.CliExitCode> RunSharesCancelAsync(
        SockseekApiClient api,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        RequirePositionCount(args, 3, SharesCancelOptions);
        Guid browseId = ParseGuid(RequiredPosition(args, 2, "browse ID"), "browse ID");
        UserBrowseDto browse = await api.CancelUserBrowseAsync(browseId, cancellationToken)
            .ConfigureAwait(false);
        Write(browse, Json(args));
        return Program.CliExitCode.Success;
    }

    private static async Task<UserBrowseDto> WaitForBrowseAsync(
        SockseekApiClient api,
        string remote,
        UserBrowseDto browse,
        bool progress,
        CancellationToken cancellationToken)
    {
        if (Terminal(browse.State))
            return browse;

        SockseekLiveClient? live = null;
        try
        {
            live = new SockseekLiveClient(remote);
            using var subscribeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            subscribeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            await live.StartUserBrowseAsync(browse.BrowseId, subscribeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (live is not null) await live.DisposeAsync().ConfigureAwait(false);
            live = null;
        }
        catch
        {
            if (live is not null) await live.DisposeAsync().ConfigureAwait(false);
            live = null;
        }

        try
        {
            TimeSpan delay = TimeSpan.FromMilliseconds(250);
            DateTimeOffset nextPoll = DateTimeOffset.UtcNow;
            while (!Terminal(browse.State))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UserBrowseDto? pushed = live?.Store.GetUserBrowse(browse.BrowseId);
                if (pushed is not null && pushed.Revision >= browse.Revision)
                    browse = pushed;

                if (DateTimeOffset.UtcNow >= nextPoll && !Terminal(browse.State))
                {
                    browse = await api.GetUserBrowseAsync(browse.BrowseId, cancellationToken)
                        .ConfigureAwait(false);
                    nextPoll = DateTimeOffset.UtcNow + delay;
                    delay = TimeSpan.FromMilliseconds(Math.Min(2_000, delay.TotalMilliseconds * 1.5));
                }

                if (progress)
                {
                    Console.Error.Write(
                        $"\rBrowsing {Safe(browse.Username)}: {browse.Phase}, "
                        + $"{browse.DirectoryCount:N0} directories, {browse.FileCount:N0} files   ");
                }
                if (!Terminal(browse.State))
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            if (progress) Console.Error.WriteLine();
            return browse;
        }
        finally
        {
            if (live is not null)
            {
                try { await live.StopUserBrowseAsync(browse.BrowseId, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                await live.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static IReadOnlyList<UserShareSelectionDto> ParseSelections(IReadOnlyList<string> args)
    {
        var selections = new List<UserShareSelectionDto>();
        for (int index = 0; index < args.Count; index++)
        {
            if (TryOptionValue(args, ref index, "--folder", out string? folder))
                selections.Add(new UserShareDirectorySelectionDto(ParsePositiveLong(folder!, "--folder")));
            else if (TryOptionValue(args, ref index, "--file", out string? file))
                selections.Add(new UserShareFileSelectionDto(ParsePositiveLong(file!, "--file")));
        }
        if (selections.Count == 0)
            throw new ArgumentException("At least one --folder or --file selection is required.");
        return selections;
    }

    internal static SubmissionOptionsDto BuildSubmissionOptions(
        IReadOnlyList<string> args,
        string? profileNames,
        bool interactive,
        IReadOnlyDictionary<string, bool> commandOptions)
    {
        IReadOnlyList<string> transferArgs = ExtractTransferArguments(args, commandOptions);
        return new SubmissionOptionsDto(
            ProfileNames: SplitProfileNames(profileNames),
            ProfileContext: new Dictionary<string, bool>
            {
                ["interactive"] = interactive,
                ["progress-json"] = false,
                ["no-progress"] = false,
            },
            DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch(transferArgs)));
    }

    private static IReadOnlyList<string> ExtractTransferArguments(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, bool> commandOptions)
    {
        var result = new List<string>();
        for (int index = 3; index < args.Count; index++)
        {
            string token = args[index];
            string name = OptionName(token);
            if (commandOptions.TryGetValue(name, out bool hasValue))
            {
                if (hasValue && !token.Contains('='))
                {
                    if (++index >= args.Count)
                        throw new ArgumentException($"Option '{name}' requires a value.");
                }
                continue;
            }
            result.Add(token);
        }
        return result;
    }

    private static void RequirePositionCount(
        IReadOnlyList<string> args,
        int required,
        IReadOnlyDictionary<string, bool> commandOptions,
        bool allowTransferOptions = false)
    {
        if (args.Count < required)
            throw new ArgumentException("The command is missing a required argument.");
        for (int index = required; index < args.Count; index++)
        {
            string token = args[index];
            if (!token.StartsWith('-'))
                throw new ArgumentException($"Unexpected argument '{Safe(token)}'.");
            string name = OptionName(token);
            if (commandOptions.TryGetValue(name, out bool hasValue))
            {
                if (hasValue && !token.Contains('='))
                {
                    if (++index >= args.Count || args[index].StartsWith('-'))
                        throw new ArgumentException($"Option '{name}' requires a value.");
                }
                continue;
            }
            if (!allowTransferOptions)
                throw new ArgumentException($"Unknown option '{Safe(name)}'.");

            // The canonical config parser validates the transfer option and its
            // arity when BuildSubmissionOptions extracts this remainder.
            break;
        }
    }

    private static string RequiredPosition(IReadOnlyList<string> args, int index, string name)
        => index < args.Count && !args[index].StartsWith('-')
            ? args[index]
            : throw new ArgumentException($"Missing {name}.");

    private static bool IsWord(IReadOnlyList<string> args, int index, string value)
        => index < args.Count && string.Equals(args[index], value, StringComparison.OrdinalIgnoreCase);

    private static string? Word(IReadOnlyList<string> args, int index)
        => index < args.Count && !args[index].StartsWith('-') ? args[index] : null;

    private static bool Flag(IReadOnlyList<string> args, string name)
        => args.Any(argument => string.Equals(OptionName(argument), name, StringComparison.OrdinalIgnoreCase));

    private static bool Json(IReadOnlyList<string> args) => Flag(args, "--json");

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index < args.Count; index++)
        {
            string token = args[index];
            if (!string.Equals(OptionName(token), name, StringComparison.OrdinalIgnoreCase))
                continue;
            int equals = token.IndexOf('=');
            if (equals >= 0) return token[(equals + 1)..];
            return index + 1 < args.Count ? args[index + 1] : null;
        }
        return null;
    }

    private static bool TryOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string name,
        out string? value)
    {
        string token = args[index];
        if (!string.Equals(OptionName(token), name, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }
        int equals = token.IndexOf('=');
        if (equals >= 0)
            value = token[(equals + 1)..];
        else if (++index < args.Count)
            value = args[index];
        else
            throw new ArgumentException($"Option '{name}' requires a value.");
        return true;
    }

    private static string OptionName(string token)
    {
        int equals = token.IndexOf('=');
        return (equals < 0 ? token : token[..equals]).ToLowerInvariant();
    }

    private static Guid ParseGuid(string value, string name)
        => Guid.TryParse(value, out Guid id) && id != Guid.Empty
            ? id
            : throw new ArgumentException($"{name} must be a non-empty UUID.");

    private static long? ParseLong(string? value, string name)
        => value is null ? null : ParsePositiveLong(value, name);

    private static long ParsePositiveLong(string value, string name)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result)
           && result > 0
            ? result
            : throw new ArgumentException($"{name} must be a positive integer.");

    private static int ParseInt(string? value, string name, int min, int max, int defaultValue)
        => value is null ? defaultValue
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result)
              && result >= min && result <= max
                ? result
                : throw new ArgumentException($"{name} must be between {min} and {max}.");

    private static string[]? SplitProfileNames(string? names)
        => string.IsNullOrWhiteSpace(names)
            ? null
            : names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool Terminal(UserBrowseState state)
        => state is UserBrowseState.Complete or UserBrowseState.Failed or UserBrowseState.Cancelled;

    private static string Safe(string? value)
        => UserProfileText.NormalizeDescription(value).Replace('\n', ' ');

    private static string SafeMultiline(string value)
        => UserProfileText.NormalizeDescription(value);

    private static string Presence(UserProfilePresence value) => value switch
    {
        UserProfilePresence.Online => "online",
        UserProfilePresence.Away => "away",
        UserProfilePresence.Offline => "offline",
        _ => "unknown",
    };

    private static string State(UserBrowseState value) => value.ToString().ToLowerInvariant();
    private static string Visibility(ShareVisibility value) => value.ToString().ToLowerInvariant();

    private static string? CombineCounts(long? first, string firstLabel, long? second, string secondLabel)
        => JoinFacts(
            first is { } firstCount ? $"{firstCount:N0} {firstLabel}" : null,
            second is { } secondCount ? $"{secondCount:N0} {secondLabel}" : null);

    private static string? JoinFacts(params string?[] facts)
    {
        string[] available = facts.Where(fact => !string.IsNullOrWhiteSpace(fact)).OfType<string>().ToArray();
        return available.Length == 0 ? null : string.Join(", ", available);
    }

    private static void WriteProfileFact(
        string label,
        UserProfileSectionDto section,
        string? value)
    {
        string rendered = value ?? section.State switch
        {
            ResourceSectionState.TimedOut => "timed out",
            ResourceSectionState.Unavailable => section.Reason is { Length: > 0 }
                ? $"unavailable ({Safe(section.Reason)})"
                : "unavailable",
            _ => "unknown",
        };
        Console.WriteLine($"{label}: {rendered}");
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static void WriteNextCursor(string? cursor)
    {
        if (cursor is not null)
            Console.WriteLine($"Next cursor: {cursor}");
    }

    internal static void WriteResolution(UserShareResolutionSummaryDto summary)
    {
        Console.WriteLine(
            $"{summary.TotalPublicFiles:N0} files ({FormatBytes(summary.TotalPublicBytes)}) from "
            + $"{summary.CanonicalDirectoryRoots:N0} folder roots and {summary.StandaloneFiles:N0} standalone files");
        if (summary.RedundantSelectionsRemoved > 0)
            Console.WriteLine($"Redundant selections removed: {summary.RedundantSelectionsRemoved:N0}");
        if (summary.LockedBranchesSkipped > 0)
            Console.WriteLine($"Locked branches skipped: {summary.LockedBranchesSkipped:N0}");
        Console.WriteLine($"Output root: {Safe(summary.OutputParent)}");
    }

    private static void Write(object value, bool json)
    {
        if (json) WriteJson(value);
        else Console.WriteLine(value);
    }

    private static void WriteJson(object value)
    {
        JsonSerializerOptions options = SockseekApiJson.CreateSerializerOptions();
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.WriteIndented = true;
        Console.WriteLine(JsonSerializer.Serialize(value, value.GetType(), options));
    }

    private static Program.CliExitCode Usage(string message)
    {
        Console.Error.WriteLine(message.StartsWith("Input error:", StringComparison.Ordinal)
            ? message : $"Input error: {message}");
        return Program.CliExitCode.UsageError;
    }

    private static readonly IReadOnlyDictionary<string, bool> ProfileOptions = Options(
        ("--refresh", false), ("--picture", true),
        ("--json", false), ("--no-color", false));

    private static readonly IReadOnlyDictionary<string, bool> SharesOptions = Options(
        ("--refresh", false), ("--json", false));

    private static readonly IReadOnlyDictionary<string, bool> SharesPageOptions = Options(
        ("--parent", true), ("--files", true), ("--query", true), ("--cursor", true),
        ("--limit", true), ("--json", false));

    private static readonly IReadOnlyDictionary<string, bool> SharesDownloadOptions = Options(
        ("--folder", true), ("--file", true),
        ("--request-id", true), ("--json", false));

    private static readonly IReadOnlyDictionary<string, bool> SharesCancelOptions = Options(
        ("--json", false));

    private static IReadOnlyDictionary<string, bool> Options(
        params (string Name, bool HasValue)[] options)
        => options.ToDictionary(option => option.Name, option => option.HasValue, StringComparer.OrdinalIgnoreCase);

    private static void PrintHelp() => Help.PrintHelp("user");
}
