using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core;

namespace Sockseek.Cli;

internal sealed class EventLogger
{
    internal static readonly IReadOnlySet<string> HandledEventTypes = JobActivityLogFormatter.HandledEventTypes;

    private readonly ICliBackend _backend;
    private readonly bool _includeDiagnosticDetails;
    private readonly JobActivityLogFormatter _formatter = new();

    public EventLogger(ICliBackend backend, bool includeDiagnosticDetails = true)
    {
        _backend = backend;
        _includeDiagnosticDetails = includeDiagnosticDetails;
    }

    public void Attach()
    {
        _backend.EventReceived += HandleEvent;
    }

    private void HandleEvent(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Type == "diagnostic.error" && !_includeDiagnosticDetails)
            return;

        var entry = _formatter.Format(envelope);
        if (entry == null)
            return;

        Write(entry);
    }

    private void Write(ActivityLogEntry entry)
    {
        var context = entry.Display is { } display
            ? new TerminalLogLine(TerminalKind(display.Kind), "", display.DisplayId, display.JobType, display.Message, display.Source, display.Highlight, display.ShowInLive)
            : null;

        SockseekLog.Write(new SockseekLog.StructuredLogEntry(
            entry.Level,
            entry.CategoryName,
            entry.Message,
            Context: context));
    }

    private static TerminalLogKind TerminalKind(ActivityLogDisplayKind kind)
        => kind switch
        {
            ActivityLogDisplayKind.Failed => TerminalLogKind.JobFailed,
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
