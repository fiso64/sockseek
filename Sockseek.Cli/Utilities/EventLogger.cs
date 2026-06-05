using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core;

namespace Sockseek.Cli;

internal sealed class EventLogger
{
    internal static readonly IReadOnlySet<string> HandledEventTypes = JobActivityLogFormatter.HandledEventTypes;

    private readonly ICliBackend _backend;
    private readonly bool _liveMode;
    private readonly JobActivityLogFormatter _formatter = new();

    public EventLogger(ICliBackend backend, bool liveMode)
    {
        _backend = backend;
        _liveMode = liveMode;
    }

    public void Attach()
    {
        _backend.EventReceived += HandleEvent;
    }

    private void HandleEvent(ServerEventEnvelopeDto envelope)
    {
        var entry = _formatter.Format(envelope);
        if (entry == null)
            return;

        Write(entry);
    }

    private void Write(ActivityLogEntry entry)
    {
        var level = entry.Severity == ActivityLogSeverity.Error
            ? LogLevel.Error
            : LogLevel.Information;

        if (_liveMode)
            SockseekLog.LogNonConsole(level, entry.Message, entry.CategoryName);
        else if (entry.Severity == ActivityLogSeverity.Error)
            SockseekLog.Error(entry.Message, categoryName: entry.CategoryName);
        else
            SockseekLog.Info(entry.Message, categoryName: entry.CategoryName);
    }
}
