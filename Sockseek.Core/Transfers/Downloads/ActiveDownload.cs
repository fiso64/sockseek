using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Soulseek;

namespace Sockseek.Core.Transfers.Downloads;

// Engine-internal session object for one file download in progress.
// Holds only what the engine needs for an in-flight transfer.
// No progress bar or display logic — those belong in the CLI layer.
internal sealed class ActiveDownload
{
    public Guid TransferId { get; }
    public FileDownloadJob Owner { get; }
    public PeerFileTarget Target { get; }
    public string OutputPath { get; }
    public CancellationTokenSource Cts { get; }
    public Job? ParentJob { get; }

    // Set by the Soulseek client callbacks for live display and command handling.
    public Transfer? Transfer { get; set; }
    public bool UserCancelled { get; set; }
    public bool IsManuallySkipped { get; set; }
    public bool IsStaleCancelled => StaleMaxStaleTimeMs.HasValue;
    public int? StaleMaxStaleTimeMs { get; private set; }

    public ActiveDownload(
        Guid transferId,
        FileDownloadJob owner,
        PeerFileTarget target,
        string outputPath,
        CancellationTokenSource cts,
        Job? parentJob = null)
    {
        TransferId = transferId;
        Owner = owner;
        Target = target;
        OutputPath = outputPath;
        Cts = cts;
        ParentJob = parentJob;
    }

    public ActiveDownload(
        Guid transferId,
        SongJob song,
        FileCandidate candidate,
        string outputPath,
        CancellationTokenSource cts,
        Job? parentJob = null)
        : this(transferId, song, candidate.Target, outputPath, cts, parentJob)
    {
    }

    public void MarkStaleCancelled(int maxStaleTimeMs)
        => StaleMaxStaleTimeMs = maxStaleTimeMs;
}

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
