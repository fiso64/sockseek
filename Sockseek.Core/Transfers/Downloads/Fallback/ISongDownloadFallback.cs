using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed record FallbackTransferDescriptor(
    string SourceReference,
    string OutputPathPrefix);

public interface ISongDownloadFallback
{
    bool CanRun(SongJob song, DownloadSettings settings);

    // Providers may set SongJob.Fallback for retained provenance and report the
    // SearchingFallback/DownloadingFallback/ProcessingFallback activity presets.
    // Set source metadata before transferStarting; transfer identity is immutable.
    Task<JobOutcome?> TryDownloadAsync(
        SongJob song,
        DownloadSettings settings,
        FileManager organizer,
        IJobLog? log,
        CancellationToken ct,
        Action<FallbackTransferDescriptor>? transferStarting = null);
}

public static class SongDownloadFallback
{
    public static ISongDownloadFallback Default { get; } =
        new YtDlpSongDownloadFallback(new YtDlpClient());
}
