using Sockseek.Core.Models;
using System.Threading.Channels;

namespace Sockseek.Persistence.Write;

public interface IPersistenceMutationSink
{
    bool TryEnqueue(PersistenceMutation mutation);
}

public sealed class PersistenceInbox : IPersistenceMutationSink
{
    private readonly Channel<PersistenceMutation> critical;
    private readonly Channel<PersistenceMutation> ordinary;
    private readonly Channel<PersistenceMutation> search;
    private readonly Channel<AwaitablePersistenceCommand> commands;
    private readonly Channel<AwaitablePersistenceCommand> backgroundCommands;
    private readonly Dictionary<Guid, TransferPersistenceMutation> progress = [];
    private readonly Dictionary<(bool Transfer, Guid Id), LinkedListNode<PersistenceMutation>> pendingStates = [];
    private readonly LinkedList<PersistenceMutation> stateOrder = [];
    private readonly object ordinaryGate = new();
    // Only the writer retains a failed/cancelled batch. Accepted entries keep
    // their identity and order; producer capacity belongs to the input channels.
    private readonly Queue<PersistenceMutation> retry = new();
    private readonly object progressGate = new();
    private readonly object retryGate = new();
    private readonly object lifecycleGate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private BatchWaiter? batchReady;
    private sealed record BatchWaiter(int Mutations, int SearchResults, int SearchLimit)
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly PersistenceHealth health;
    private readonly IPersistenceMutationObserver? mutationObserver;
    private int criticalDepth;
    private int ordinaryDepth;
    private int searchDepth;
    private int bufferedSearchResultCount;
    private int searchCompletionCount;
    private int completed;
    private int activeAdmissions;

    public PersistenceInbox(
        PersistenceWriterOptions options,
        PersistenceHealth health,
        IPersistenceMutationObserver? mutationObserver = null)
    {
        options.Validate();
        Options = options;
        this.health = health;
        this.mutationObserver = mutationObserver;
        // These are facts about work Core has already admitted, not requests
        // for more work. Backpressure here parks execution inside synchronous
        // event/transfer locks. Retain exact structural/terminal facts; compact
        // replaceable state below. Only request commands await queue capacity.
        critical = CreateHistoryChannel();
        ordinary = CreateHistoryChannel();
        search = CreateChannel(options.SearchMutationQueueCapacity);
        commands = Channel.CreateBounded<AwaitablePersistenceCommand>(
            new BoundedChannelOptions(options.CommandQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        backgroundCommands = Channel.CreateBounded<AwaitablePersistenceCommand>(
            new BoundedChannelOptions(options.BackgroundCommandQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    public PersistenceWriterOptions Options { get; }
    public int CriticalDepth => Volatile.Read(ref criticalDepth) + CommandDepth;
    private int PendingStateCount { get { lock (ordinaryGate) return pendingStates.Count; } }
    public int OrdinaryDepth => Volatile.Read(ref ordinaryDepth) + PendingStateCount + BackgroundCommandDepth;
    public int ProgressCount { get { lock (progressGate) return progress.Count; } }
    public int RetryMutationCount { get { lock (retryGate) return retry.Count; } }
    public int BufferedSearchResultCount => Volatile.Read(ref bufferedSearchResultCount);
    internal int SearchDepth => Volatile.Read(ref searchDepth);
    internal bool IsCompleted => Volatile.Read(ref completed) != 0;
    internal int ActiveAdmissionCount => Volatile.Read(ref activeAdmissions);

    /// <summary>
    /// History notifications return without waiting for database capacity.
    /// Raw search input retains its separate bounded collection backpressure.
    /// </summary>
    public bool TryEnqueue(PersistenceMutation mutation)
    {
        // Core event publishers are synchronous. Preserve the same admission
        // owner while allowing asynchronous producers to await capacity directly.
        ValueTask<bool> admission = EnqueueAsync(mutation);
        return admission.IsCompleted ? admission.GetAwaiter().GetResult()
            : admission.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The same admission contract without blocking an asynchronous producer.</summary>
    public async ValueTask<bool> EnqueueAsync(PersistenceMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!TryBeginAdmission())
        {
            mutationObserver?.PermanentlyFailed(
                [mutation],
                new InvalidOperationException("Persistence stopped before the mutation could be accepted."));
            return false;
        }
        try
        {
            if (mutation is SearchResultsPersistenceMutation results)
            {
                // Keep admission lossless while limiting SQLite write-lock time.
                // Retry retains these exact chunks instead of coalescing them.
                for (int offset = 0; offset < results.Results.Count;)
                {
                    int count = Math.Min(Options.SearchResultFlushCount, results.Results.Count - offset);
                    if (offset == 0 && results.Results[0].Sequence == 1)
                        count = Math.Min(count, PersistenceWriterOptions.InitialSearchResultFlushCount);
                    var records = new SearchProjectionInput[count];
                    for (int index = 0; index < count; index++)
                        records[index] = results.Results[offset + index];
                    var chunk = results with
                    {
                        Results = records,
                    };
                    if (!await EnqueueSearchWithBackpressureAsync(chunk).ConfigureAwait(false))
                        return false;
                    offset += count;
                }
                return true;
            }
            if (mutation is SearchCompletionPersistenceMutation)
                return await EnqueueSearchWithBackpressureAsync(mutation).ConfigureAwait(false);
            if (mutation is TransferTerminalPersistenceMutation terminalTransfer)
                return EnqueueHistory(terminalTransfer with
                {
                    Transfer = AbsorbBufferedTransfer(terminalTransfer.Transfer),
                }, isCritical: true);
            if (mutation is TransferPersistenceMutation { Priority: PersistenceMutationPriority.Progress } transferProgress)
                return TrySetProgress(transferProgress);
            if (mutation is TransferPersistenceMutation { Priority: PersistenceMutationPriority.Terminal } terminal)
                return EnqueueHistory(AbsorbBufferedTransfer(terminal), isCritical: true);

            if (mutation.Priority >= PersistenceMutationPriority.Structural)
                return EnqueueHistory(mutation, isCritical: true);

            if (mutation is TransferPersistenceMutation
                or JobPersistenceMutation { DirectoryManifest: null, SubmissionSpecificationJson: null })
            {
                // Cohesive current state is replaceable; definitions/manifests
                // and attempts retain ordered delivery. Transfer headers replace
                // older headers, but byte observations survive for accounting.
                var key = (Transfer: mutation is TransferPersistenceMutation, Id: mutation.EntityId);
                lock (ordinaryGate)
                {
                    if (pendingStates.TryGetValue(key, out var current))
                    {
                        if (mutation is TransferPersistenceMutation incomingTransfer)
                            current.Value = CoalesceTransfer((TransferPersistenceMutation)current.Value, incomingTransfer);
                        else if (mutation.Revision > current.Value.Revision) current.Value = mutation;
                    }
                    else pendingStates.Add(key, stateOrder.AddLast(mutation));
                }
                Signal();
                return true;
            }

            // Manifests and accepted intent are not replaceable headers.
            return EnqueueHistory(mutation, isCritical: false);
        }
        finally
        {
            EndAdmission();
        }
    }

    internal async Task EnqueueCommandAsync(
        AwaitablePersistenceCommand command,
        CancellationToken cancellationToken,
        bool background = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsCompleted)
            throw new InvalidOperationException("Persistence is stopping.");
        try
        {
            await (background ? backgroundCommands : commands).Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            Signal();
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException("Persistence is stopping.", ex);
        }
    }

    // Cancellation can withdraw queued work. Once execution starts, await its
    // durable outcome: callers must not delete a blob or report a rollback while
    // its metadata transaction can still commit.
    internal async Task<T> ExecuteAsync<T>(
        Func<SockseekDbContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        bool background = false,
        bool requiresTransaction = true)
    {
        var command = new AwaitablePersistenceCommand<T>(action, requiresTransaction);
        using var cancellation = cancellationToken.Register(() => command.CancelBeforeStart(cancellationToken));
        await EnqueueCommandAsync(command, cancellationToken, background).ConfigureAwait(false);
        return await command.Task.ConfigureAwait(false);
    }

    internal bool TryDequeueBackgroundCommand(out AwaitablePersistenceCommand? command)
        => backgroundCommands.Reader.TryRead(out command);

    internal bool TryDequeueCommand(out AwaitablePersistenceCommand? command)
    {
        if (!commands.Reader.TryRead(out command))
            return false;
        if (CommandDepth > 0)
            Signal();
        return true;
    }

    internal int CommandDepth => commands.Reader.CanCount ? commands.Reader.Count : 0;
    internal int BackgroundCommandDepth => backgroundCommands.Reader.Count;

    internal async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Wake-ups coalesce. A command can consume the only signal while other
        // admitted work remains, so check runnable queues before parking again.
        // Progress alone deliberately keeps its normal flush cadence.
        if (CriticalDepth > 0 || OrdinaryDepth > 0 || RetryMutationCount > 0 || SearchDepth > 0)
            return;
        await signal.WaitAsync(Options.SearchResultFlushInterval, cancellationToken).ConfigureAwait(false);
    }

    // Drain while gathering so a bounded packet queue can refill up to the
    // file-count target. Holding the batch here needs no second queue or writer.
    // First results keep their immediate admission; history gathers by batch size.
    internal async ValueTask<IReadOnlyList<PersistenceMutation>> ReadBatchAsync(
        bool includeProgress, TimeSpan gathering, TimeProvider clock, CancellationToken cancellationToken)
    {
        var batch = new List<PersistenceMutation>(Options.MaximumBatchSize);
        int searchResultCount = 0, searchResultLimit = Options.SearchResultFlushCount;
        long started = clock.GetTimestamp();
        try
        {
            while (true)
            {
                DrainBatch(batch, includeProgress, ref searchResultCount, ref searchResultLimit);
                TimeSpan remaining = gathering - clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero || IsCompleted || CommandDepth > 0
                    || batch.Count >= Options.MaximumBatchSize || searchResultCount >= searchResultLimit
                    || batch.Any(item => IsInitialSearchBatch(item) || item is SearchCompletionPersistenceMutation)
                    || search.Reader.TryPeek(out var next) && !FitsSearchBatch(next, searchResultCount, searchResultLimit))
                    break;
                if (!await WaitForBatchAsync(remaining, clock, cancellationToken,
                        batch.Count, searchResultCount, searchResultLimit).ConfigureAwait(false))
                {
                    // The deadline also admits sparse arrivals below the target.
                    DrainBatch(batch, includeProgress, ref searchResultCount, ref searchResultLimit);
                    break;
                }
            }
            return batch;
        }
        catch
        {
            // Cancellation during gathering must not forget already drained work.
            RetainForRetry(batch);
            throw;
        }
    }

    private async Task<bool> WaitForBatchAsync(TimeSpan remaining, TimeProvider clock,
        CancellationToken cancellationToken, int mutations, int searchResults, int searchLimit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new BatchWaiter(mutations, searchResults, searchLimit);
        if (IsBatchReady(waiter)) return true;
        Volatile.Write(ref batchReady, waiter);
        try
        {
            if (IsBatchReady(waiter)) waiter.Ready.TrySetResult();
            // Wake on a full input/target, command or shutdown rather than
            // scheduling the consumer for every individual peer response.
            await waiter.Ready.Task.WaitAsync(remaining, clock, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
        finally { Interlocked.CompareExchange(ref batchReady, null, waiter); }
    }

    private bool IsBatchReady(BatchWaiter waiter) => IsCompleted || CommandDepth > 0
        || Volatile.Read(ref searchCompletionCount) > 0
        || SearchDepth >= Options.SearchMutationQueueCapacity
        || waiter.Mutations + Volatile.Read(ref criticalDepth) + Volatile.Read(ref ordinaryDepth)
            + PendingStateCount + RetryMutationCount + SearchDepth >= Options.MaximumBatchSize
        || waiter.SearchResults + BufferedSearchResultCount >= waiter.SearchLimit;

    internal IReadOnlyList<PersistenceMutation> DrainBatch(bool includeProgress = true)
    {
        var batch = new List<PersistenceMutation>(Options.MaximumBatchSize);
        int searchResultCount = 0, searchResultLimit = Options.SearchResultFlushCount;
        DrainBatch(batch, includeProgress, ref searchResultCount, ref searchResultLimit);
        return batch;
    }

    private void DrainBatch(List<PersistenceMutation> batch, bool includeProgress,
        ref int searchResultCount, ref int searchResultLimit)
    {
        // Retry precedes newly admitted terminal/progress state. Otherwise a
        // newer checkpoint can make retained accounting observations stale.
        lock (retryGate)
        {
            while (batch.Count < Options.MaximumBatchSize
                && searchResultCount < searchResultLimit
                && retry.TryPeek(out var item)
                && FitsSearchBatch(item, searchResultCount, searchResultLimit))
            {
                retry.Dequeue();
                batch.Add(item);
                if (item is SearchResultsPersistenceMutation added)
                {
                    searchResultCount += added.Results.Count;
                    if (IsInitialSearchBatch(added))
                        searchResultLimit = Math.Min(searchResultLimit, PersistenceWriterOptions.InitialSearchResultFlushCount);
                }
            }
            if (retry.Count > 0) return;
        }

        while (batch.Count < Options.MaximumBatchSize && critical.Reader.TryRead(out var mutation))
        {
            Interlocked.Decrement(ref criticalDepth);
            batch.Add(mutation);
        }

        lock (ordinaryGate)
        {
            while (batch.Count < Options.MaximumBatchSize && stateOrder.First is { } first)
            {
                stateOrder.RemoveFirst();
                pendingStates.Remove((first.Value is TransferPersistenceMutation, first.Value.EntityId));
                batch.Add(first.Value);
            }
        }

        while (batch.Count < Options.MaximumBatchSize && ordinary.Reader.TryRead(out var mutation))
        {
            Interlocked.Decrement(ref ordinaryDepth);
            batch.Add(mutation);
        }

        while (batch.Count < Options.MaximumBatchSize
            && searchResultCount < searchResultLimit
            && search.Reader.TryPeek(out var nextSearchMutation)
            && FitsSearchBatch(nextSearchMutation, searchResultCount, searchResultLimit)
            && search.Reader.TryRead(out var searchMutation))
        {
            Interlocked.Decrement(ref searchDepth);
            if (searchMutation is SearchResultsPersistenceMutation results)
            {
                Interlocked.Add(ref bufferedSearchResultCount, -results.Results.Count);
                searchResultCount += results.Results.Count;
                // Preserve the original small first write, then gather bulk
                // batches. The source's first sequence identifies this phase;
                // no per-search registry or extra waiting is needed.
                if (IsInitialSearchBatch(results))
                    searchResultLimit = Math.Min(searchResultLimit, PersistenceWriterOptions.InitialSearchResultFlushCount);
            }
            else if (searchMutation is SearchCompletionPersistenceMutation)
                Interlocked.Decrement(ref searchCompletionCount);
            batch.Add(searchMutation);
        }

        if (includeProgress)
        {
            lock (progressGate)
            {
                foreach (var item in progress.Values
                    .OrderBy(item => item.Sequence)
                    .Take(Options.MaximumBatchSize - batch.Count)
                    .ToList())
                {
                    progress.Remove(item.TransferId);
                    batch.Add(item);
                }
            }
        }

        if (CriticalDepth > 0 || OrdinaryDepth > 0 || RetryMutationCount > 0 || SearchDepth > 0
            || includeProgress && ProgressCount > 0)
            Signal();
    }

    // Preserve a whole admitted mutation's failure boundary even when the
    // interactive latency budget is smaller than that one mutation.
    private static bool FitsSearchBatch(PersistenceMutation mutation, int rows, int limit)
        => mutation is not SearchResultsPersistenceMutation results
            || rows == 0 || rows + results.Results.Count <= limit;

    private static bool IsInitialSearchBatch(PersistenceMutation mutation)
        => mutation is SearchResultsPersistenceMutation { Results.Count: > 0 } results
            && results.Results[0].Sequence == 1;

    internal void RetainForRetry(IReadOnlyList<PersistenceMutation> mutations)
    {
        lock (retryGate)
        {
            // Gathering can be cancelled after draining only part of a retry.
            // Restore that prefix before its unconsumed suffix; never merge or
            // evict attempts, manifests or raw-result chunks by entity key.
            var suffix = retry.ToArray();
            retry.Clear();
            foreach (var mutation in mutations.OrderBy(item => item.Sequence))
                retry.Enqueue(mutation);
            foreach (var mutation in suffix)
                retry.Enqueue(mutation);
        }
        Signal();
    }

    public void Complete()
    {
        lock (lifecycleGate)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;
            commands.Writer.TryComplete();
            backgroundCommands.Writer.TryComplete();
            critical.Writer.TryComplete();
            ordinary.Writer.TryComplete();
            search.Writer.TryComplete();
        }
        Signal();
    }

    internal void FailPendingCommands(Exception exception)
    {
        while (commands.Reader.TryRead(out var command))
        {
            command.Fail(exception);
        }
        while (backgroundCommands.Reader.TryRead(out var command))
            command.Fail(exception);
    }

    private bool EnqueueHistory(PersistenceMutation mutation, bool isCritical)
    {
        try
        {
            // Publish the depth before making a row readable. An unblocked
            // writer can otherwise consume it before admission increments it.
            if (isCritical) Interlocked.Increment(ref criticalDepth);
            else Interlocked.Increment(ref ordinaryDepth);
            if (!(isCritical ? critical : ordinary).Writer.TryWrite(mutation))
            {
                if (isCritical) Interlocked.Decrement(ref criticalDepth);
                else Interlocked.Decrement(ref ordinaryDepth);
                throw new ChannelClosedException();
            }
            var job = mutation as JobPersistenceMutation
                ?? (mutation as TransferTerminalPersistenceMutation)?.OwningJob;
            if (isCritical && job is not null)
            {
                lock (ordinaryGate)
                {
                    if (pendingStates.TryGetValue((false, job.JobId), out var buffered) && buffered.Value.Revision <= job.Revision)
                    {
                        stateOrder.Remove(buffered);
                        pendingStates.Remove((false, job.JobId));
                    }
                }
            }
            Signal();
            return true;
        }
        catch (ChannelClosedException exception)
        {
            mutationObserver?.PermanentlyFailed([mutation], exception);
            return false;
        }
    }

    private bool TrySetProgress(TransferPersistenceMutation mutation)
    {
        lock (progressGate)
        {
            if (progress.TryGetValue(mutation.TransferId, out var current))
            {
                if (mutation.Revision > current.Revision)
                    progress[mutation.TransferId] = MergeTransfer(current, mutation);
                else if (mutation.AccountingObservations is { Count: > 0 })
                    progress[mutation.TransferId] = MergeTransfer(mutation, current);
                Signal();
                return true;
            }

            if (progress.Count >= Options.ProgressEntityCapacity)
            {
                // Accounting-bearing progress is compacted per attempt/time
                // bucket and must survive until a terminal cumulative snapshot.
                // Active transfer concurrency is the representation bound.
                if (mutation.AccountingObservations is { Count: > 0 })
                {
                    progress.Add(mutation.TransferId, mutation);
                    Signal();
                    return true;
                }
                health.RecordDroppedProgress();
                return false;
            }

            progress.Add(mutation.TransferId, mutation);
        }
        Signal();
        return true;
    }

    private TransferPersistenceMutation AbsorbBufferedTransfer(TransferPersistenceMutation terminal)
    {
        Guid transferId = terminal.TransferId;
        TransferPersistenceMutation merged = terminal;
        // Critical completion drains first. Carry earlier ordinary observations
        // with it so a final checkpoint cannot make their time buckets stale.
        lock (ordinaryGate)
        {
            if (pendingStates.TryGetValue((true, transferId), out var buffered)
                && buffered.Value.Revision <= terminal.Revision)
            {
                merged = MergeTransfer((TransferPersistenceMutation)buffered.Value, merged);
                stateOrder.Remove(buffered);
                pendingStates.Remove((true, transferId));
            }
        }
        lock (progressGate)
        {
            if (progress.Remove(transferId, out TransferPersistenceMutation? buffered))
                merged = MergeTransfer(buffered, merged);
        }
        return merged;
    }

    internal static TransferPersistenceMutation MergeTransfer(
        TransferPersistenceMutation earlier,
        TransferPersistenceMutation later)
    {
        IReadOnlyList<TransferAccountingObservation>? observations = MergeObservations(
            earlier.AccountingObservations,
            later.AccountingObservations);
        string? relativePath = later.GroupRelativePath ?? earlier.GroupRelativePath;
        if (ReferenceEquals(observations, later.AccountingObservations)
            && relativePath == later.GroupRelativePath)
            return later;
        return later with
        {
            AccountingObservations = observations,
            GroupRelativePath = relativePath,
        };
    }

    private static IReadOnlyList<TransferAccountingObservation>? MergeObservations(
        IReadOnlyList<TransferAccountingObservation>? first,
        IReadOnlyList<TransferAccountingObservation>? second)
    {
        if (first is not { Count: > 0 }) return second;
        if (second is not { Count: > 0 }) return first;
        // Ordinary progress replaces one cumulative observation in the same
        // bucket. Keep resets, retries and reordered samples on the general path.
        if (first.Count == 1 && second.Count == 1)
        {
            var previous = first[0];
            var current = second[0];
            int timeOrder = previous.OccurredAtUtc.CompareTo(current.OccurredAtUtc);
            if (previous.AttemptId == current.AttemptId
                && (previous.Revision == current.Revision
                    || (timeOrder < 0 || timeOrder == 0 && previous.Revision < current.Revision)
                    && previous.CumulativeBytes <= current.CumulativeBytes
                    && AccountingBucket(previous) == AccountingBucket(current)))
                return second;
        }
        TransferAccountingObservation[] ordered = first.Concat(second)
            .GroupBy(item => (item.AttemptId, item.Revision))
            .Select(group => group.Last())
            .ToArray();
        var compact = new List<TransferAccountingObservation>();
        foreach (IGrouping<Guid, TransferAccountingObservation> attempt in ordered
            .GroupBy(item => item.AttemptId))
        {
            TransferAccountingObservation? pending = null;
            long pendingBucket = 0;
            foreach (TransferAccountingObservation observation in attempt
                .OrderBy(item => item.OccurredAtUtc)
                .ThenBy(item => item.Revision))
            {
                long bucket = AccountingBucket(observation);
                if (pending is not null
                    && (bucket != pendingBucket
                        || observation.CumulativeBytes < pending.CumulativeBytes))
                {
                    compact.Add(pending);
                }
                pending = observation;
                pendingBucket = bucket;
            }
            if (pending is not null)
                compact.Add(pending);
        }
        return compact
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Revision)
            .ToArray();
    }

    private static long AccountingBucket(TransferAccountingObservation observation)
    {
        long observedAt = observation.OccurredAtUtc.ToUnixTimeMilliseconds();
        return observedAt - observedAt % PersistenceWriter.AccountingBucketMilliseconds;
    }

    private async ValueTask<bool> EnqueueSearchWithBackpressureAsync(PersistenceMutation mutation)
    {
        try
        {
            await search.Writer.WriteAsync(mutation).ConfigureAwait(false);
            Interlocked.Increment(ref searchDepth);
            if (mutation is SearchResultsPersistenceMutation results)
                Interlocked.Add(ref bufferedSearchResultCount, results.Results.Count);
            else if (mutation is SearchCompletionPersistenceMutation)
                Interlocked.Increment(ref searchCompletionCount);
            Signal();
            return true;
        }
        catch (ChannelClosedException exception)
        {
            mutationObserver?.PermanentlyFailed([mutation], exception);
            return false;
        }
    }

    private bool TryBeginAdmission()
    {
        lock (lifecycleGate)
        {
            if (completed != 0)
                return false;
            Interlocked.Increment(ref activeAdmissions);
            return true;
        }
    }

    private void EndAdmission()
    {
        Interlocked.Decrement(ref activeAdmissions);
        Signal();
    }

    internal static TransferPersistenceMutation CoalesceTransfer(
        TransferPersistenceMutation current,
        TransferPersistenceMutation incoming)
    {
        bool incomingWins = incoming.Revision > current.Revision
            || incoming.Revision == current.Revision && incoming.Priority >= current.Priority;
        return incomingWins ? MergeTransfer(current, incoming) : MergeTransfer(incoming, current);
    }

    private static Channel<PersistenceMutation> CreateChannel(int capacity)
        => Channel.CreateBounded<PersistenceMutation>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private static Channel<PersistenceMutation> CreateHistoryChannel()
        => Channel.CreateUnbounded<PersistenceMutation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private void Signal()
    {
        if (Volatile.Read(ref batchReady) is { } waiter && IsBatchReady(waiter)) waiter.Ready.TrySetResult();
        try
        {
            if (signal.CurrentCount == 0)
                signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

}
