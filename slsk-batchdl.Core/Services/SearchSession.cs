using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sldl.Core.Models;
using Soulseek;

namespace Sldl.Core.Services;

public sealed class SearchSession
{
    private readonly Lock rawResultsLock = new();
    private readonly List<SearchRawResult> rawResults = [];
    private int _revision;
    private int _lockedFileCount;
    private long _sequence;
    private int _isComplete;

    public ConcurrentDictionary<string, (SearchResponse Response, Soulseek.File File)> Results { get; } = new();

    public int Revision => Volatile.Read(ref _revision);
    public int LockedFileCount => Volatile.Read(ref _lockedFileCount);
    public bool IsComplete => Volatile.Read(ref _isComplete) != 0;

    public event Action<SearchSession, SearchResponse, Soulseek.File>? RawResultReceived;
    public event Action<SearchSession, SearchRawResult>? RawResultAdded;
    public event Action<SearchSession>? Completed;

    public IReadOnlyCollection<(SearchResponse Response, Soulseek.File File)> Snapshot()
        => Results.Values.ToList();

    public IReadOnlyList<SearchRawResult> RawSnapshot(long afterSequence = 0)
    {
        lock (rawResultsLock)
            return rawResults
                .Where(x => x.Sequence > afterSequence)
                .ToList();
    }

    public async IAsyncEnumerable<SearchRawResult> ReadRawResultsAsync(
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<SearchRawResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void OnRawResultAdded(SearchSession _, SearchRawResult result)
        {
            if (result.Sequence > afterSequence)
                channel.Writer.TryWrite(result);
        }

        void OnCompleted(SearchSession _)
            => channel.Writer.TryComplete();

        RawResultAdded += OnRawResultAdded;
        Completed += OnCompleted;

        try
        {
            long lastYielded = afterSequence;
            foreach (var result in RawSnapshot(afterSequence))
            {
                if (result.Sequence <= lastYielded)
                    continue;

                lastYielded = result.Sequence;
                yield return result;
            }

            if (IsComplete)
                channel.Writer.TryComplete();

            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var result))
                {
                    if (result.Sequence <= lastYielded)
                        continue;

                    lastYielded = result.Sequence;
                    yield return result;
                }
            }
        }
        finally
        {
            RawResultAdded -= OnRawResultAdded;
            Completed -= OnCompleted;
            channel.Writer.TryComplete();
        }
    }

    public void AddResponse(SearchResponse response)
    {
        Interlocked.Add(ref _lockedFileCount, response.LockedFileCount);

        if (response.Files.Count == 0)
            return;

        foreach (var file in response.Files)
        {
            if (Results.TryAdd(response.Username + '\\' + file.Filename, (response, file)))
            {
                int revision = Interlocked.Increment(ref _revision);
                long sequence = Interlocked.Increment(ref _sequence);
                var rawResult = new SearchRawResult(sequence, revision, response, file);
                lock (rawResultsLock)
                    rawResults.Add(rawResult);

                RawResultReceived?.Invoke(this, response, file);
                RawResultAdded?.Invoke(this, rawResult);
            }
        }
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _isComplete, 1) == 0)
            Completed?.Invoke(this);
    }
}
