using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

internal sealed class StaleDownloadMonitor
{
    private readonly IDownloadRegistry registry;
    private readonly TimeProvider timeProvider;

    public StaleDownloadMonitor(IDownloadRegistry registry, TimeProvider? timeProvider = null)
    {
        this.registry = registry;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int CancelStaleDownloads()
    {
        var now = timeProvider.GetUtcNow();
        var activeDownloads = new List<(string Filename, ActiveDownload Download, int MaxStaleTimeMs)>();
        // A peer usually serves one file at a time, so activity on one transfer keeps
        // queued siblings from the same user alive.
        var latestActivityByUser = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filename, download) in registry.Downloads)
        {
            if (download == null)
            {
                registry.Downloads.TryRemove(filename, out _);
                continue;
            }

            if (download.ObserveTransferSnapshot(now))
                download.Song.LastActivityTime = now.LocalDateTime;

            if (!download.LastTransferActivityUtc.HasValue)
                continue;

            var username = download.Candidate.Username;
            if (!latestActivityByUser.TryGetValue(username, out var latestActivity)
                || download.LastTransferActivityUtc.Value > latestActivity)
            {
                latestActivityByUser[username] = download.LastTransferActivityUtc.Value;
            }

            activeDownloads.Add((filename, download, download.Song.Config?.Search.MaxStaleTime ?? 30_000));
        }

        var cancelled = 0;
        foreach (var (filename, download, maxStaleTimeMs) in activeDownloads)
        {
            if (!latestActivityByUser.TryGetValue(download.Candidate.Username, out var latestUserActivity))
                continue;

            if ((now - latestUserActivity).TotalMilliseconds < maxStaleTimeMs)
                continue;

            SockseekLog.Jobs.Debug($"Cancelling stale download: {download.Song}");
            cancelled++;
            try { download.Song.Cts?.Cancel(); } catch { }
            try { download.Cts.Cancel(); } catch { }
            registry.Downloads.TryRemove(filename, out _);
        }

        return cancelled;
    }
}
