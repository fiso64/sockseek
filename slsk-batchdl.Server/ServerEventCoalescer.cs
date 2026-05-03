using System.Collections.Concurrent;

namespace Sldl.Server;

public sealed class ServerEventCoalescer : IDisposable
{
    private readonly Lock gate = new();
    private readonly Action<string, object> publishImmediate;
    private readonly ConcurrentDictionary<Guid, DownloadProgressEventDto> pendingDownloadProgress = [];
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
    }

    public void Dispose()
    {
        timer.Dispose();
        Flush();
    }
}
