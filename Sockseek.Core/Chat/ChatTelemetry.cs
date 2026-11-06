using System.Diagnostics.Metrics;

namespace Sockseek.Core.Chat;

/// <summary>Low-cardinality chat instrumentation; never tags peer, room, id, or body.</summary>
public static class ChatTelemetry
{
    public const string MeterName = "Sockseek.Chat";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Ingress =
        Meter.CreateCounter<long>("sockseek_chat_ingress_total");
    private static readonly Counter<long> Dropped =
        Meter.CreateCounter<long>("sockseek_chat_ingress_dropped_total");
    private static readonly Counter<long> Persisted =
        Meter.CreateCounter<long>("sockseek_chat_messages_persisted_total");
    private static readonly Counter<long> Acknowledged =
        Meter.CreateCounter<long>("sockseek_chat_private_ack_total");
    private static readonly Counter<long> Sends =
        Meter.CreateCounter<long>("sockseek_chat_sends_total");
    private static readonly Counter<long> InboundResults =
        Meter.CreateCounter<long>("sockseek_chat_inbound_total");
    private static readonly Counter<long> PersistenceFailures =
        Meter.CreateCounter<long>("sockseek_chat_persistence_failures_total");
    private static long ingressDepth;
    private static long joinedRooms;
    private static long desiredRooms;
    private static long unreadNotifications;

    static ChatTelemetry()
    {
        Meter.CreateObservableGauge(
            "sockseek_chat_ingress_queue_depth",
            () => Volatile.Read(ref ingressDepth));
        Meter.CreateObservableGauge(
            "sockseek_chat_joined_rooms",
            () => Volatile.Read(ref joinedRooms));
        Meter.CreateObservableGauge(
            "sockseek_chat_desired_rooms",
            () => Volatile.Read(ref desiredRooms));
        Meter.CreateObservableGauge(
            "sockseek_chat_unread_notifications",
            () => Volatile.Read(ref unreadNotifications));
    }

    public static void RecordIngress(string kind)
        => Ingress.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public static void RecordDropped(string kind, string reason)
        => Dropped.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("reason", reason));

    public static void RecordPersisted(string kind, bool duplicate = false)
        => Persisted.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("result", duplicate ? "duplicate" : "inserted"));

    public static void RecordAcknowledged(string result)
        => Acknowledged.Add(1, new KeyValuePair<string, object?>("result", result));

    public static void RecordSend(string kind, string result)
        => Sends.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("result", result));

    public static void RecordInboundResult(string kind, string result)
        => InboundResults.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("result", result));

    public static void RecordPersistenceFailure(string operation)
        => PersistenceFailures.Add(1,
            new KeyValuePair<string, object?>("operation", operation));

    public static void SetIngressDepth(int value)
        => Volatile.Write(ref ingressDepth, value);

    public static void SetRoomCounts(int joined, int desired)
    {
        Volatile.Write(ref joinedRooms, joined);
        Volatile.Write(ref desiredRooms, desired);
    }

    public static void SetUnreadNotifications(int value)
        => Volatile.Write(ref unreadNotifications, value);
}
