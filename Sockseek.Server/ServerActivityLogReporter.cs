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

        SockseekLog.Write(entry.Level, entry.Message, categoryName: entry.CategoryName);
    }
}
