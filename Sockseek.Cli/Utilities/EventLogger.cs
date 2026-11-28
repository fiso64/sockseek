using Sockseek.Api;

namespace Sockseek.Cli;

internal sealed class EventLogger
{
    internal static readonly IReadOnlySet<string> HandledEventTypes = JobActivityLogFormatter.HandledActivityTypes;

    private readonly ICliBackend _backend;
    private readonly CliOutputController _output;
    private readonly bool _includeDiagnosticDetails;
    private readonly SoulseekSessionLines _session = new();
    private readonly JobActivityLogFormatter _formatter = new();


    public EventLogger(
        ICliBackend backend,
        CliOutputController output,
        bool includeDiagnosticDetails = true)
    {
        _backend = backend;
        _output = output;
        _includeDiagnosticDetails = includeDiagnosticDetails;
    }

    public void Attach()
    {
        _backend.StateUpdated += HandleStateUpdate;
    }

    private void HandleStateUpdate(DaemonClientUpdate update)
    {
        if (update.Status != DaemonClientApplyStatus.Applied)
            return;

        foreach (var transfer in update.ChangedTransfers)
            _formatter.Remember(transfer);
        foreach (var summary in update.ChangedJobs)
            _formatter.Remember(summary);

        // Notes say what happened on the way to this update's states, so they
        // come first when both arrive together.
        foreach (var activity in update.Activity)
            HandleActivity(activity);

        // One update can finish a job together with its children. Print the
        // children first, as they would appear had they arrived one by one.
        IEnumerable<JobSummaryDto> jobs = update.ChangedJobs;
        if (update.ChangedJobs.Count > 1)
        {
            var parents = update.ChangedJobs.ToDictionary(job => job.JobId, job => job.Relationships.ParentJobId);
            jobs = update.ChangedJobs.OrderByDescending(job => Depth(job.JobId, parents));
        }
        foreach (var summary in jobs)
        {
            var entry = _formatter.Format(summary);
            if (entry != null)
                Write(entry);
        }
    }

    // Depth within this update only: a parent that did not change needs no ordering.
    private static int Depth(Guid jobId, Dictionary<Guid, Guid?> parents)
    {
        int depth = 0;
        while (depth < 16 && parents.TryGetValue(jobId, out Guid? parent) && parent is Guid id && parents.ContainsKey(id))
        {
            jobId = id;
            depth++;
        }
        return depth;
    }

    private void WriteSession(string message)
        => _output.WriteOutput(new CliOutputEvent.ProcessLog(
            new TerminalProcessLogLine(LogLevel.Information, "soulseek", message, CliProcessLogPresentation.Decorated)));

    private void HandleActivity(ActivityEventDto activity)
    {
        if (activity.Payload is SessionChangedActivityDto session)
        {
            if (_session.Next(session.Session) is { } line)
                WriteSession(line);
            return;
        }
        if (activity.Payload is DiagnosticActivityDto && !_includeDiagnosticDetails)
            return;
        // Debug notes are for -v, in a terminal and in redirected output alike.
        if (!_includeDiagnosticDetails && activity.Payload is JobMessageActivityDto { Level: "Debug" or "Trace" }
            or WorkflowMessageActivityDto { Level: "Debug" or "Trace" })
            return;

        var entry = _formatter.Format(activity, _backend.ClientStore);
        if (entry == null)
            return;

        Write(entry);
    }

    private void Write(ActivityLogEntry entry)
    {
        CliOutputEvent? context = entry.Display is { } display
            ? new CliOutputEvent.JobLog(
                new TerminalLogLine(TerminalKind(display.Kind), "", display.DisplayId, display.JobType, display.Message, display.Source, display.Highlight, display.ShowInLive),
                entry.Level)
            : null;

        _output.WriteOutput(context ?? new CliOutputEvent.ProcessLog(
            new TerminalProcessLogLine(
                entry.Level,
                entry.CategoryName,
                entry.Message,
                CliProcessLogPresentation.Decorated)));
    }

    private static TerminalLogKind TerminalKind(ActivityLogDisplayKind kind)
        => kind switch
        {
            ActivityLogDisplayKind.Failed => TerminalLogKind.JobFailed,
            ActivityLogDisplayKind.Partial => TerminalLogKind.JobPartial,
            ActivityLogDisplayKind.Cancelled => TerminalLogKind.JobCancelled,
            ActivityLogDisplayKind.Succeeded => TerminalLogKind.JobSucceeded,
            ActivityLogDisplayKind.AlreadyExists => TerminalLogKind.JobAlreadyExists,
            ActivityLogDisplayKind.Skipped => TerminalLogKind.SongSkipped,
            ActivityLogDisplayKind.AlbumTrackSucceeded => TerminalLogKind.AlbumTrackDownloaded,
            ActivityLogDisplayKind.AlbumTrackFailed => TerminalLogKind.AlbumTrackFailed,
            ActivityLogDisplayKind.AlbumTrackSkipped => TerminalLogKind.AlbumTrackSkipped,
            _ => TerminalLogKind.Status,
        };
}

/// <summary>
/// Words the daemon's session changes for a CLI user, for local and remote runs
/// alike. Only changes arrive, so a daemon that was already logged in says
/// nothing. Failures and lost connections are reported by their warnings.
/// </summary>
internal sealed class SoulseekSessionLines
{
    private string? phase;

    /// <summary>The line to print for this status, when it is news.</summary>
    public string? Next(SoulseekClientStatusDto client)
    {
        // Local files involve no account.
        if (client.Username is not { Length: > 0 } user)
            return null;
        string current = client.IsReady ? "ready"
            : client.Flags.Any(flag => flag is "Connecting" or "Connected" or "LoggingIn") ? "connecting"
            : "offline";
        if (current == phase)
            return null;
        phase = current;
        return current switch
        {
            "ready" => $"Logged in as {user}",
            "connecting" => $"Logging in as {user}…",
            _ => null,
        };
    }
}
