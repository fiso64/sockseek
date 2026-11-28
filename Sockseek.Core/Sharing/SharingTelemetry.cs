using System.Diagnostics.Metrics;
using Sockseek.Core.Transfers.Uploads;

namespace Sockseek.Core.Sharing;

/// <summary>Low-cardinality diagnostics for sharing and upload operations.</summary>
public static class SharingTelemetry
{
    public const string MeterName = "Sockseek.Sharing";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> RequestsDropped =
        Meter.CreateCounter<long>("sockseek_share_requests_dropped_total");
    private static readonly Histogram<double> ScanDuration =
        Meter.CreateHistogram<double>("sockseek_share_scan_duration_seconds", "s");
    private static readonly Counter<long> ScanResults =
        Meter.CreateCounter<long>("sockseek_share_scans_total");
    private static readonly Counter<long> UploadBytes =
        Meter.CreateCounter<long>("sockseek_upload_bytes_total", "By");
    private static readonly Counter<long> UploadCompleted =
        Meter.CreateCounter<long>("sockseek_upload_completed_total");
    private static readonly Counter<long> UploadRejected =
        Meter.CreateCounter<long>("sockseek_upload_rejected_total");
    private static readonly Counter<long> UploadDuplicates =
        Meter.CreateCounter<long>("sockseek_upload_duplicates_coalesced_total");

    private static long catalogFiles;
    private static long catalogDirectories;
    private static long catalogBytes;
    private static int uploadActive;
    private static int uploadQueued;
    private static long uploadQueuedBytes;

    static SharingTelemetry()
    {
        Meter.CreateObservableGauge(
            "sockseek_share_catalog_files",
            () => Interlocked.Read(ref catalogFiles));
        Meter.CreateObservableGauge(
            "sockseek_share_catalog_directories",
            () => Interlocked.Read(ref catalogDirectories));
        Meter.CreateObservableGauge(
            "sockseek_share_catalog_bytes",
            () => Interlocked.Read(ref catalogBytes),
            "By");
        Meter.CreateObservableGauge(
            "sockseek_upload_active",
            () => Volatile.Read(ref uploadActive));
        Meter.CreateObservableGauge(
            "sockseek_upload_queued",
            () => Volatile.Read(ref uploadQueued));
        Meter.CreateObservableGauge(
            "sockseek_upload_queued_bytes",
            () => Interlocked.Read(ref uploadQueuedBytes),
            "By");
    }

    public static void UpdateCatalog(ShareCatalogMetadata? metadata)
    {
        Interlocked.Exchange(ref catalogFiles, metadata?.FileCount ?? 0);
        Interlocked.Exchange(ref catalogDirectories, metadata?.DirectoryCount ?? 0);
        Interlocked.Exchange(ref catalogBytes, metadata?.TotalBytes ?? 0);
    }

    public static void UpdateQueue(UploadQueueRuntimeSnapshot snapshot)
    {
        Volatile.Write(ref uploadActive, snapshot.ActiveSlots);
        Volatile.Write(ref uploadQueued, snapshot.QueuedFiles);
        Interlocked.Exchange(ref uploadQueuedBytes, snapshot.QueuedBytes);
    }

    public static void RecordDroppedRequest(string type)
        => RequestsDropped.Add(1, new KeyValuePair<string, object?>("type", type));

    public static void RecordScan(TimeSpan totalDuration)
    {
        ScanDuration.Record(totalDuration.TotalSeconds);
        RecordScanResult("completed");
    }

    public static void RecordScanResult(string result)
        => ScanResults.Add(
            1,
            new KeyValuePair<string, object?>("result", result));

    public static void RecordUploadRejected(UploadAdmissionRejection reason)
        => UploadRejected.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason.ToString()));

    public static void RecordUploadDuplicate() => UploadDuplicates.Add(1);

    public static void RecordUploadTerminal(
        UploadTransferState state,
        long bytes)
    {
        UploadBytes.Add(Math.Max(0, bytes));
        UploadCompleted.Add(
            1,
            new KeyValuePair<string, object?>("outcome", state.ToString()));
    }
}
