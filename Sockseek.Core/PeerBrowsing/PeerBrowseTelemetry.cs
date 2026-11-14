using System.Diagnostics.Metrics;

namespace Sockseek.Core.PeerBrowsing;

/// <summary>Low-cardinality browse metrics; peer identities and paths are never labels.</summary>
public static class PeerBrowseTelemetry
{
    public const string MeterName = "Sockseek.PeerBrowsing";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Starts =
        Meter.CreateCounter<long>("sockseek_peer_browses_started_total");
    private static readonly Counter<long> Reuses =
        Meter.CreateCounter<long>("sockseek_peer_browses_reused_total");
    private static readonly Counter<long> Terminals =
        Meter.CreateCounter<long>("sockseek_peer_browses_terminal_total");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("sockseek_peer_browse_duration_seconds", "s");
    private static readonly Histogram<long> CompressedBytes =
        Meter.CreateHistogram<long>("sockseek_peer_browse_compressed_bytes", "By");
    private static readonly Histogram<long> Rows =
        Meter.CreateHistogram<long>("sockseek_peer_browse_rows");

    private static long active;
    private static long queued;
    private static long artifactCount;
    private static long artifactBytes;

    static PeerBrowseTelemetry()
    {
        Meter.CreateObservableGauge("sockseek_peer_browse_active", () => Interlocked.Read(ref active));
        Meter.CreateObservableGauge("sockseek_peer_browse_queued", () => Interlocked.Read(ref queued));
        Meter.CreateObservableGauge("sockseek_peer_browse_artifacts", () => Interlocked.Read(ref artifactCount));
        Meter.CreateObservableGauge(
            "sockseek_peer_browse_artifact_bytes",
            () => Interlocked.Read(ref artifactBytes),
            "By");
    }

    public static void RecordStarted()
    {
        Starts.Add(1);
        Interlocked.Increment(ref active);
        Interlocked.Increment(ref queued);
    }

    public static void RecordReuse(string source)
        => Reuses.Add(1, new KeyValuePair<string, object?>("source", source));

    public static void RecordRunning()
        => Interlocked.Decrement(ref queued);

    public static void RecordTerminal(
        PeerBrowseResource? resource,
        bool reachedRunning,
        TimeSpan duration)
    {
        if (!reachedRunning)
            Interlocked.Decrement(ref queued);
        Interlocked.Decrement(ref active);

        string outcome = resource?.State switch
        {
            PeerBrowseState.Complete => "completed",
            PeerBrowseState.Cancelled => "cancelled",
            PeerBrowseState.Failed => "failed",
            _ => "unknown",
        };
        string reason = resource?.Failure?.Code ?? "none";
        Terminals.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("reason", reason));
        Duration.Record(Math.Max(0, duration.TotalSeconds), new KeyValuePair<string, object?>("outcome", outcome));
        if (resource is null)
            return;
        CompressedBytes.Record(Math.Max(0, resource.CompressedBytesReceived));
        Rows.Record(SaturatingAdd(resource.DirectoryCount, resource.FileCount));
    }

    public static void UpdateArtifacts(long count, long bytes)
    {
        Interlocked.Exchange(ref artifactCount, Math.Max(0, count));
        Interlocked.Exchange(ref artifactBytes, Math.Max(0, bytes));
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
