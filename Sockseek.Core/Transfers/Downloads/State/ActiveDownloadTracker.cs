using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Sockseek.Core.Transfers.Downloads.State;

internal sealed class ActiveDownloadTracker
{
    private readonly ConcurrentDictionary<Guid, ActiveDownload> downloads = new();

    public IEnumerable<ActiveDownload> ActiveDownloads => downloads.Values;

    public bool TryAdd(ActiveDownload download)
        => downloads.TryAdd(download.TransferId, download);

    public bool TryGet(Guid transferId, [NotNullWhen(true)] out ActiveDownload? download)
        => downloads.TryGetValue(transferId, out download);

    public bool TryRemove(Guid transferId, [NotNullWhen(true)] out ActiveDownload? download)
        => downloads.TryRemove(transferId, out download);

    public bool Contains(Guid transferId)
        => downloads.ContainsKey(transferId);

    internal int Count => downloads.Count;
}
