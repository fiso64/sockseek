using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public interface ISongDownloadFallback
{
    Task<JobOutcome?> TryDownloadAsync(
        SongJob song,
        DownloadSettings settings,
        FileManager organizer,
        IJobLog? log,
        CancellationToken ct);
}

public static class SongDownloadFallback
{
    public static ISongDownloadFallback Default { get; } =
        new YtDlpSongDownloadFallback(new YtDlpClient());
}
