using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core;

namespace Sockseek.Cli;

internal sealed class EventLogger
{
    internal static readonly IReadOnlySet<string> HandledEventTypes = JobActivityLogFormatter.HandledActivityTypes;

    private readonly ICliBackend _backend;
    private readonly CliOutputController _output;
    private readonly bool _includeDiagnosticDetails;
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
        _backend.ActivityReceived += HandleActivity;
    }

    private void HandleStateUpdate(DaemonClientUpdate update)
    {
        if (update.Status != DaemonClientApplyStatus.Applied)
            return;

        foreach (var summary in update.ChangedJobs)
        {
            var entry = _formatter.Format(summary);
            if (entry != null)
                Write(entry);
        }
    }

    private void HandleActivity(ActivityEventDto activity)
    {
        if (activity.Payload is DiagnosticActivityDto && !_includeDiagnosticDetails)
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
