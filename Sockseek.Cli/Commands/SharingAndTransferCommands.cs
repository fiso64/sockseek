using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sockseek.Api;
using Sockseek.Core.Models;

namespace Sockseek.Cli;

/// <summary>Thin daemon client for sharing and generic transfer resources.</summary>
internal static class SharingAndTransferCommands
{
    public static async Task<Program.CliExitCode> RunAsync(
        SockseekApiClient api, ConfiguredCommandInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.CommandArguments;
        ValidateArguments(args);
        if (Equals(args[0], "job"))
        {
            string? action = Positional(args, 1);
            if (!Guid.TryParse(Positional(args, 2), out var id) || !Equals(action, "rerun") && !Equals(action, "retry-directory"))
                return ConfiguredCommandDispatcher.Usage("Expected 'job rerun|retry-directory <id>'.");
            bool exact = args.Contains("--exact", StringComparer.OrdinalIgnoreCase);
            if (exact && !Equals(action, "rerun"))
                return ConfiguredCommandDispatcher.Usage("--exact applies to 'job rerun'.");
            // Like the web UI: read the source again when there is one.
            bool extractAgain = Equals(action, "rerun") && !exact
                && (await api.GetJobDetailAsync(id, cancellationToken))?.RepeatOptions?.CanExtractAgain == true;
            var job = Equals(action, "rerun") ? await api.RunJobAgainAsync(id, cancellationToken, extractAgain)
                : await api.RetryDirectoryAsync(id, cancellationToken);
            if (JsonRequested(args)) Write(job, json: true);
            else Console.WriteLine($"Queued job {job.Display.DisplayId} ({job.JobId}).");
            return Program.CliExitCode.Success;
        }
        if (Equals(args[0], "share"))
            return await RunShareAsync(api, args, cancellationToken).ConfigureAwait(false);
        if (Equals(args[0], "transfers"))
            return await RunTransfersAsync(api, args, cancellationToken).ConfigureAwait(false);
        return await RunTransferAsync(api, args, cancellationToken).ConfigureAwait(false);
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
            return ConfiguredCommandDispatcher.Usage("Expected 'share status' or 'share scan'.");

        if (args.Contains("--cancel", StringComparer.OrdinalIgnoreCase))
        {
            SharingStateDto sharing = await api.GetSharingAsync(cancellationToken).ConfigureAwait(false);
            if (sharing.ActiveScan is null)
                return ConfiguredCommandDispatcher.Usage("There is no active share scan to cancel.");
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
                return ConfiguredCommandDispatcher.Usage("'transfers cancel' requires --direction download|upload.");
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
        if (Equals(action, "remove"))
        {
            TransferCommandReceiptDto receipt = await api.RemoveTransfersAsync(
                new RemoveTransfersRequestDto(
                    Direction: Option(args, "--direction"),
                    TerminalOutcome: Option(args, "--outcome"),
                    Username: Option(args, "--username"),
                    GroupRef: Option(args, "--group-ref")),
                cancellationToken).ConfigureAwait(false);
            WriteReceipt(receipt, JsonRequested(args));
            return Program.CliExitCode.Success;
        }
        if (action is not null)
            return ConfiguredCommandDispatcher.Usage("Expected 'transfers', 'transfers cancel', or 'transfers remove'.");

        int limit = ParseLimit(Option(args, "--limit"), ServerProtocol.DefaultPageSize);
        var filter = new TransferHistoryFilter(
            Direction: Option(args, "--direction"),
            Phase: Option(args, "--phase"),
            TerminalOutcome: Option(args, "--outcome"),
            Username: Option(args, "--username"),
            IncludeRemoved: args.Contains("--include-removed", StringComparer.OrdinalIgnoreCase));
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
                + $"{transfer.Status.Phase,-14} {transfer.Identity.Username ?? "-"}  "
                + $"{transfer.Identity.RemotePath ?? "-"}");
            if (transfer.Status.TerminalOutcome == TransferTerminalOutcome.Failed)
            {
                var reason = transfer.Status.FailureMessage;
                if (string.IsNullOrWhiteSpace(reason) && transfer.Status.FailureReason is not (TransferFailureReason.None or TransferFailureReason.Unknown))
                    reason = transfer.Status.FailureReason.ToString();
                if (!string.IsNullOrWhiteSpace(reason))
                    Console.WriteLine($"    {PeerIdentityValidator.ToDisplayText(reason)}");
            }
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
        if ((!Equals(action, "cancel") && !Equals(action, "remove") && !Equals(action, "retry"))
            || !Guid.TryParse(idValue, out Guid transferId))
        {
            return ConfiguredCommandDispatcher.Usage("Expected 'transfer cancel|retry|remove <id>'.");
        }

        if (Equals(action, "remove"))
        {
            TransferCommandReceiptDto receipt = await api.RemoveTransferAsync(
                transferId,
                cancellationToken).ConfigureAwait(false);
            WriteReceipt(receipt, JsonRequested(args));
        }
        else if (Equals(action, "retry"))
        {
            var job = await api.RetryTransferAsync(transferId, cancellationToken);
            if (JsonRequested(args)) Write(job, json: true);
            else Console.WriteLine($"Retry queued as job {job.Display.DisplayId} ({job.JobId}).");
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
           || argument.Equals("--phase", StringComparison.OrdinalIgnoreCase)
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
                        "--phase",
                        "--outcome",
                        "--username",
                        "--group-ref",
                        "--cursor",
                        "--limit",
                        "--scope",
                        "--include-removed",
                    ],
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    Equals(args[0], "job") ? ["--json", "--exact"] : ["--json"],
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
                        ? "Input error: Expected 'transfers', 'transfers cancel', or 'transfers remove'."
                        : Equals(args[0], "job") ? "Expected 'job rerun|retry-directory <id>'."
                        : "Input error: Expected 'transfer cancel|retry|remove <id>'.");
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
            if (seen.Contains("--scope") && !Equals(action, "cancel"))
                throw new ArgumentException("Input error: --scope is valid only with 'transfers cancel'.");
            if (seen.Contains("--include-removed") && action is not null)
                throw new ArgumentException("Input error: --include-removed is valid only when listing transfers.");
            if (seen.Contains("--group-ref") && !Equals(action, "cancel") && !Equals(action, "remove"))
                throw new ArgumentException("Input error: --group-ref is valid only with 'transfers cancel' or 'transfers remove'.");
            if (seen.Contains("--group-ref") && (!seen.Contains("--username") || !seen.Contains("--direction")))
                throw new ArgumentException("Input error: --group-ref requires --username and --direction.");
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
}
