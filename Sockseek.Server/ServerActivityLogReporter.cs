using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core;

namespace Sockseek.Server;

public sealed class ServerActivityLogReporter
{
    private readonly JobActivityLogFormatter formatter = new();

    public ServerActivityLogReporter(ServerEventBroadcaster broadcaster)
    {
        broadcaster.EventPublished += OnEventPublished;
    }

    private void OnEventPublished(ServerEventEnvelopeDto envelope)
    {
        var entry = formatter.Format(envelope);
        if (entry == null)
            return;

        var level = entry.Severity == ActivityLogSeverity.Error
            ? LogLevel.Error
            : LogLevel.Information;
        SockseekLog.LogNonConsole(level, entry.Message, entry.CategoryName);
    }
}
