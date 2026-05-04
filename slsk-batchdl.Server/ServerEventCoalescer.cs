using System.Collections.Concurrent;

namespace Sldl.Server;

public sealed class ServerEventCoalescer : IDisposable
{
    private readonly Lock gate = new();
    private readonly Action<string, object> publishImmediate;
    private readonly ConcurrentDictionary<Guid, DownloadProgressEventDto> pendingDownloadProgress = [];
    private readonly ConcurrentDictionary<Guid, SearchUpdatedDto> pendingSearchUpdated = [];
    private readonly Timer timer;

    public ServerEventCoalescer(Action<string, object> publishImmediate, TimeSpan? flushInterval = null)
    {
        this.publishImmediate = publishImmediate;
        timer = new Timer(
            _ => Flush(),
            null,
            flushInterval ?? TimeSpan.FromMilliseconds(200),
            flushInterval ?? TimeSpan.FromMilliseconds(200));
    }

    public void Publish(string type, object payload)
    {
        lock (gate)
        {
            if (type == "download.progress" && payload is DownloadProgressEventDto progress)
            {
                pendingDownloadProgress[progress.JobId] = progress;
                return;
            }

            if (type == "search.updated" && payload is SearchUpdatedDto search)
            {
                pendingSearchUpdated[search.JobId] = search;
                return;
            }

            FlushCore();
            publishImmediate(type, payload);
        }
    }

    public void Flush()
    {
        lock (gate)
            FlushCore();
    }

    private void FlushCore()
    {
        foreach (var jobId in pendingDownloadProgress.Keys)
        {
            if (pendingDownloadProgress.TryRemove(jobId, out var progress))
                publishImmediate("download.progress", progress);
        }

        foreach (var jobId in pendingSearchUpdated.Keys)
        {
            if (pendingSearchUpdated.TryRemove(jobId, out var search))
                publishImmediate("search.updated", search);
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        Flush();
    }
}
