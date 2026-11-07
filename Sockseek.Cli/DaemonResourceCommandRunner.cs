using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sockseek.Api;

namespace Sockseek.Cli;

/// <summary>Thin daemon client for sharing and generic transfer resources.</summary>
internal static class DaemonResourceCommandRunner
{
    public static async Task<Program.CliExitCode> RunAsync(
        IReadOnlyList<string> args,
        RemoteSettings? remote,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            PrintHelp();
            return Program.CliExitCode.Success;
        }

        if (remote?.IsEnabled != true)
        {
            Console.Error.WriteLine(
                "Input error: This command requires a configured remote URL "
                + "(remote = <url> or --remote <url>); it does not start a temporary daemon.");
            return Program.CliExitCode.UsageError;
        }

        try
        {
            ValidateArguments(args);
            using HttpClient http = remote.CreateHttpClient();
            var api = new SockseekApiClient(http);
            if (Equals(args[0], "share"))
                return await RunShareAsync(api, args, cancellationToken).ConfigureAwait(false);
            if (Equals(args[0], "transfers"))
                return await RunTransfersAsync(api, args, cancellationToken).ConfigureAwait(false);
            return await RunTransferAsync(api, args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            Console.Error.WriteLine(
                ex.Message.StartsWith("Input error:", StringComparison.Ordinal)
                    ? ex.Message
                    : $"Input error: {ex.Message}");
            return Program.CliExitCode.UsageError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Daemon command failed: {ex.Message}");
            return Program.CliExitCode.WorkFailed;
        }
    }

    private static async Task<Program.CliExitCode> RunShareAsync(
        SockseekApiClient api,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        string action = Positional(args, 1) ?? "status";
        if (Equals(action, "status"))
        {
            StateSnapshotDto snapshot = await api
                .GetDaemonSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            DaemonStateDto daemon = snapshot.Daemon
                ?? throw new InvalidDataException(
                    "Daemon snapshot did not contain daemon state.");
            WriteSharing(daemon.Sharing, daemon.Uploads, JsonRequested(args));
            return Program.CliExitCode.Success;
        }
        if (!Equals(action, "scan"))
            return Usage("Expected 'share status' or 'share scan'.");

        if (args.Contains("--cancel", StringComparer.OrdinalIgnoreCase))
        {
            SharingStateDto sharing = await api.GetSharingAsync(cancellationToken).ConfigureAwait(false);
            if (sharing.ActiveScan is null)
                return Usage("There is no active share scan to cancel.");
            ShareScanStateDto scan = await api.CancelShareScanAsync(
                sharing.ActiveScan.ScanId,
                cancellationToken).ConfigureAwait(false);
            Write(scan, JsonRequested(args));
            return Program.CliExitCode.Success;
        }

        StartShareScanResponseDto started = await api.StartShareScanAsync(
            cancellationToken).ConfigureAwait(false);
        Write(started, JsonRequested(args));
        return Program.CliExitCode.Success;
    }

    private static async Task<Program.CliExitCode> RunTransfersAsync(
        SockseekApiClient api,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        string? action = Positional(args, 1);
        if (Equals(action, "cancel"))
        {
            if (!Enum.TryParse(
                    Option(args, "--direction"),
                    ignoreCase: true,
                    out TransferCommandDirection direction))
            {
                return Usage("'transfers cancel' requires --direction download|upload.");
            }
            TransferCancellationScope scope = Enum.TryParse(
                (Option(args, "--scope") ?? "All").Replace("-", "", StringComparison.Ordinal),
                ignoreCase: true,
                out TransferCancellationScope parsedScope)
                    ? parsedScope
                    : throw new ArgumentException(
                        "Input error: --scope must be all, queued, or in-progress.");
            TransferCommandReceiptDto receipt = await api.CancelTransfersAsync(
                new BulkCancelTransfersRequestDto(
                    direction,
                    scope,
                    Username: Option(args, "--username"),
                    GroupRef: Option(args, "--group-ref")),
                cancellationToken).ConfigureAwait(false);
            WriteReceipt(receipt, JsonRequested(args));
            return Program.CliExitCode.Success;
        }
        if (Equals(action, "archive"))
        {
            TransferCommandReceiptDto receipt = await api.SetTransfersArchivedAsync(
                new ArchiveTransfersRequestDto(
                    Archived: !args.Contains("--restore", StringComparer.OrdinalIgnoreCase),
                    Direction: Option(args, "--direction"),
                    TerminalOutcome: Option(args, "--outcome"),
                    Username: Option(args, "--username"),
                    GroupRef: Option(args, "--group-ref")),
                cancellationToken).ConfigureAwait(false);
            WriteReceipt(receipt, JsonRequested(args));
            return Program.CliExitCode.Success;
        }
        if (action is not null)
            return Usage("Expected 'transfers', 'transfers cancel', or 'transfers archive'.");

        int limit = ParseLimit(Option(args, "--limit"), ServerProtocol.DefaultPageSize);
        var filter = new TransferHistoryFilter(
            Direction: Option(args, "--direction"),
            State: Option(args, "--state"),
            TerminalOutcome: Option(args, "--outcome"),
            Username: Option(args, "--username"),
            Archived: args.Contains("--archived", StringComparer.OrdinalIgnoreCase));
        TransferTimelinePageDto page = await api.GetTransfersPageAsync(
            filter,
            Option(args, "--cursor"),
            limit,
            ct: cancellationToken).ConfigureAwait(false);

        if (JsonRequested(args))
        {
            Write(page, json: true);
            return Program.CliExitCode.Success;
        }

        foreach (TransferStateDto transfer in page.Items)
        {
            Console.WriteLine(
                $"{transfer.TransferId:D}  {transfer.Identity.Direction,-8} "
                + $"{transfer.Status.State,-12} {transfer.Identity.Username ?? "-"}  "
                + $"{transfer.Identity.RemotePath ?? "-"}");
        }
        if (page.Items.Count == 0)
            Console.WriteLine("No transfers matched.");
        if (page.RetainedCoverage.State != TransferRetainedCoverageState.Available)
            Console.WriteLine($"Retained coverage: {page.RetainedCoverage.State} ({page.RetainedCoverage.Reason ?? "unknown"})");
        if (page.NextCursor is not null)
            Console.WriteLine($"Next cursor: {page.NextCursor}");
        return Program.CliExitCode.Success;
    }

    private static async Task<Program.CliExitCode> RunTransferAsync(
        SockseekApiClient api,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        string? action = Positional(args, 1);
        string? idValue = Positional(args, 2);
        if ((!Equals(action, "cancel") && !Equals(action, "archive"))
            || !Guid.TryParse(idValue, out Guid transferId))
        {
            return Usage("Expected 'transfer cancel <id>' or 'transfer archive <id>'.");
        }

        if (Equals(action, "archive"))
        {
            TransferCommandReceiptDto receipt = await api.SetTransferArchivedAsync(
                transferId,
                archived: !args.Contains("--restore", StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
            WriteReceipt(receipt, JsonRequested(args));
        }
        else
        {
            TransferStateDto transfer = await api.CancelTransferAsync(
                transferId,
                cancellationToken).ConfigureAwait(false);
            Write(transfer, JsonRequested(args));
        }
        return Program.CliExitCode.Success;
    }

    private static void WriteReceipt(TransferCommandReceiptDto receipt, bool json)
    {
        if (json)
        {
            Write(receipt, json: true);
            return;
        }
        Console.WriteLine(
            $"Resolved {receipt.ResolvedCount:N0}; succeeded {receipt.SucceededCount:N0}; "
            + $"unchanged {receipt.NoOpCount:N0}; rejected {receipt.RejectedCount:N0}; "
            + $"failed {receipt.FailedCount:N0}.");
        if (receipt.Reasons.Count > 0)
        {
            Console.WriteLine("Reasons: " + string.Join(
                ", ",
                receipt.Reasons.Select(reason => $"{reason.Reason}={reason.Count:N0}")));
        }
    }

    private static void WriteSharing(
        SharingStateDto state,
        UploadRuntimeStateDto? uploads,
        bool json)
    {
        if (json)
        {
            JsonSerializerOptions options = SockseekApiJson.CreateSerializerOptions();
            options.WriteIndented = true;
            Console.WriteLine(new JsonObject
            {
                ["Sharing"] = JsonSerializer.SerializeToNode(state, options),
                ["Uploads"] = JsonSerializer.SerializeToNode(uploads, options),
            }.ToJsonString(options));
            return;
        }

        Console.WriteLine(
            $"Sharing: {state.State}"
            + (state.Reason is null ? "" : $" ({state.Reason})"));
        Console.WriteLine($"Aliases: {(state.Aliases.Count == 0 ? "-" : string.Join(", ", state.Aliases))}");
        Console.WriteLine(
            $"Catalog: generation "
            + $"{state.Catalog.GenerationId?.ToString("D") ?? "-"}; "
            + $"{state.Catalog.DirectoryCount:N0} directories, "
            + $"{state.Catalog.FileCount:N0} files, "
            + $"{state.Catalog.TotalBytes:N0} bytes");
        Console.WriteLine(
            $"Upload-blocked peers: {state.UploadBlockedUsernameCount:N0} usernames, "
            + $"{state.UploadBlockedIpAddressCount:N0} IP addresses");
        if (uploads is not null)
        {
            Console.WriteLine(
                $"Uploads: {uploads.State}"
                + (uploads.Reason is null ? "" : $" ({uploads.Reason})")
                + $"; {uploads.ActiveSlots:N0}/{uploads.Slots:N0} active; "
                + $"{uploads.QueuedFiles:N0} queued files, "
                + $"{uploads.QueuedBytes:N0} queued bytes; "
                + (uploads.AcceptingUploads ? "accepting" : "not accepting"));
            Console.WriteLine(
                $"Upload speed cap: "
                + (uploads.SpeedLimitKiBPerSecond is { } speed
                    ? $"{speed:N0} KiB/s"
                    : "unlimited"));
        }
        if (state.ActiveScan is not null)
            Console.WriteLine($"Active scan: {state.ActiveScan.ScanId:D} {state.ActiveScan.Phase}");
        if (state.LastScan is not null)
            Console.WriteLine($"Last scan: {state.LastScan.ScanId:D} {state.LastScan.Phase}");
    }

    private static void Write(object value, bool json)
    {
        if (!json)
        {
            Console.WriteLine(value);
            return;
        }
        JsonSerializerOptions options = SockseekApiJson.CreateSerializerOptions();
        options.WriteIndented = true;
        Console.WriteLine(JsonSerializer.Serialize(value, value.GetType(), options));
    }

    private static string? Positional(IReadOnlyList<string> args, int index)
    {
        int found = -1;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (ConsumesValue(args[i]))
                {
                    i++;
                }
                continue;
            }
            found++;
            if (found == index)
                return args[i];
        }
        return null;
    }

    private static bool ConsumesValue(string argument)
        => argument.Equals("--direction", StringComparison.OrdinalIgnoreCase)
           || argument.Equals("--state", StringComparison.OrdinalIgnoreCase)
           || argument.Equals("--outcome", StringComparison.OrdinalIgnoreCase)
           || argument.Equals("--username", StringComparison.OrdinalIgnoreCase)
           || argument.Equals("--group-ref", StringComparison.OrdinalIgnoreCase)
           || argument.Equals("--cursor", StringComparison.OrdinalIgnoreCase)
           || argument.Equals("--limit", StringComparison.OrdinalIgnoreCase)
           || argument.Equals("--scope", StringComparison.OrdinalIgnoreCase);

    private static void ValidateArguments(IReadOnlyList<string> args)
    {
        bool share = Equals(args[0], "share");
        bool transfers = Equals(args[0], "transfers");
        var allowed = share
            ? new HashSet<string>(
                ["--json", "--cancel"],
                StringComparer.OrdinalIgnoreCase)
            : transfers
                ? new HashSet<string>(
                    [
                        "--json",
                        "--direction",
                        "--state",
                        "--outcome",
                        "--username",
                        "--group-ref",
                        "--cursor",
                        "--limit",
                        "--scope",
                        "--restore",
                        "--archived",
                    ],
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    ["--json", "--restore"],
                    StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (!argument.StartsWith('-'))
            {
                positional.Add(argument);
                continue;
            }
            if (!allowed.Contains(argument))
                throw new ArgumentException($"Input error: Unknown option '{argument}'.");
            if (!seen.Add(argument))
                throw new ArgumentException($"Input error: Option '{argument}' was supplied more than once.");
            if (!ConsumesValue(argument))
                continue;
            if (++i >= args.Count || args[i].StartsWith('-'))
                throw new ArgumentException($"Input error: Option '{argument}' requires a value.");
        }

        bool validPositionals = share
            ? positional.Count is 1 or 2
            : transfers
                ? positional.Count is 1 or 2
                : positional.Count == 3;
        if (!validPositionals)
        {
            throw new ArgumentException(
                share
                    ? "Input error: Expected 'share status' or 'share scan'."
                    : transfers
                        ? "Input error: Expected 'transfers', 'transfers cancel', or 'transfers archive'."
                        : "Input error: Expected 'transfer cancel <id>' or 'transfer archive <id>'.");
        }
        if (share
            && seen.Contains("--cancel")
            && (positional.Count != 2 || !Equals(positional[1], "scan")))
        {
            throw new ArgumentException(
                "Input error: --cancel is valid only with 'share scan'.");
        }
        if (transfers)
        {
            string? action = positional.Count > 1 ? positional[1] : null;
            if (seen.Contains("--restore") && !Equals(action, "archive"))
                throw new ArgumentException("Input error: --restore is valid only with 'transfers archive'.");
            if (seen.Contains("--scope") && !Equals(action, "cancel"))
                throw new ArgumentException("Input error: --scope is valid only with 'transfers cancel'.");
            if (seen.Contains("--archived") && action is not null)
                throw new ArgumentException("Input error: --archived is valid only when listing transfers.");
            if (seen.Contains("--group-ref") && !Equals(action, "cancel") && !Equals(action, "archive"))
                throw new ArgumentException("Input error: --group-ref is valid only with 'transfers cancel' or 'transfers archive'.");
            if (seen.Contains("--group-ref") && (!seen.Contains("--username") || !seen.Contains("--direction")))
                throw new ArgumentException("Input error: --group-ref requires --username and --direction.");
        }
        if (!share && !transfers
            && seen.Contains("--restore")
            && !Equals(positional.ElementAtOrDefault(1), "archive"))
        {
            throw new ArgumentException("Input error: --restore is valid only with 'transfer archive'.");
        }
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int ParseLimit(string? value, int fallback)
        => value is null
            ? fallback
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
              && parsed >= 1
                ? parsed
                : throw new ArgumentException("Input error: --limit must be a positive integer.");

    private static bool JsonRequested(IReadOnlyList<string> args)
        => args.Contains("--json", StringComparer.OrdinalIgnoreCase);

    private static bool Equals(string? left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static Program.CliExitCode Usage(string message)
    {
        Console.Error.WriteLine($"Input error: {message}");
        return Program.CliExitCode.UsageError;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Daemon sharing and transfer commands:
              sockseek share status [--remote <url>] [--json]
              sockseek share scan [--remote <url>] [--json]
              sockseek share scan --cancel [--remote <url>] [--json]
              sockseek transfers [--remote <url>] [--direction upload|download] [--state <state>]
                                     [--username <name>] [--archived] [--limit N] [--cursor <cursor>] [--json]
              sockseek transfers cancel --direction upload|download [--scope all|queued|in-progress]
                                       [--username <name>] [--group-ref <ref>]
              sockseek transfers archive [--direction upload|download] [--outcome <outcome>] [--restore]
                                        [--username <name>] [--group-ref <ref>]
              sockseek transfer cancel <id> [--remote <url>] [--json]
              sockseek transfer archive <id> [--restore] [--remote <url>] [--json]

            The remote URL can be set as `remote = <url>` in config; --remote overrides it.
            --group-ref requires both --username and --direction.
            """);
    }
}
