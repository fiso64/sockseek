using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Api;
using Sockseek.Core.Diagnostics;

namespace Sockseek.Server;

/// <summary>
/// Bounded per-scope transport dispatch. A slow SignalR group cannot block or
/// accumulate work for another scope; dropped batches are recovered through
/// the live protocol's normal sequence-gap snapshot path.
/// </summary>
public sealed class BoundedStateBatchDispatcher : IAsyncDisposable
{
    private readonly Func<StateUpdate, CancellationToken, Task> send;
    private readonly int perScopeCapacity;
    private readonly int maximumScopes;
    private readonly TimeSpan sendTimeout;
    private readonly TimeSpan idleLifetime;
    private readonly ILogger<BoundedStateBatchDispatcher> logger;
    private readonly RepeatedWarningGate warningGate = new();
    private readonly ConcurrentDictionary<StateStreamScopeDto, ScopeSender> senders = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly object creationGate = new();
    private long droppedBatches;
    private long failedSends;
    private int disposed;

    public BoundedStateBatchDispatcher(
        Func<StateUpdate, CancellationToken, Task> send,
        int perScopeCapacity = 256,
        int maximumScopes = 2_048,
        TimeSpan? sendTimeout = null,
        TimeSpan? idleLifetime = null,
        ILogger<BoundedStateBatchDispatcher>? logger = null)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.logger = logger ?? NullLogger<BoundedStateBatchDispatcher>.Instance;
        if (perScopeCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(perScopeCapacity));
        if (maximumScopes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumScopes));
        this.sendTimeout = sendTimeout ?? TimeSpan.FromSeconds(5);
        this.idleLifetime = idleLifetime ?? TimeSpan.FromMinutes(1);
        if (this.sendTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sendTimeout));
        if (this.idleLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleLifetime));
        this.perScopeCapacity = perScopeCapacity;
        this.maximumScopes = maximumScopes;
    }

    public long DroppedBatches => Interlocked.Read(ref droppedBatches);
    public long FailedSends => Interlocked.Read(ref failedSends);
    public int ActiveScopeCount => senders.Count;
    public int QueuedBatchCount => senders.Values.Sum(sender => sender.Depth);

    public bool TryPublish(StateUpdate batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.Scope.Validate();
        while (Volatile.Read(ref disposed) == 0)
        {
            if (!senders.TryGetValue(batch.Scope, out ScopeSender? sender))
            {
                lock (creationGate)
                {
                    if (!senders.TryGetValue(batch.Scope, out sender))
                    {
                        if (senders.Count >= maximumScopes)
                        {
                            Interlocked.Increment(ref droppedBatches);
                            return false;
                        }
                        sender = new ScopeSender(this, batch.Scope, perScopeCapacity);
                        if (!senders.TryAdd(batch.Scope, sender))
                            continue;
                        sender.Start();
                    }
                }
            }

            switch (sender.TryEnqueue(batch))
            {
                case EnqueueResult.Accepted:
                    return true;
                case EnqueueResult.ReplacedOlder:
                    Interlocked.Increment(ref droppedBatches);
                    return true;
                case EnqueueResult.Retired:
                    Remove(sender);
                    break;
            }
        }

        Interlocked.Increment(ref droppedBatches);
        return false;
    }

    private async Task SendAsync(StateUpdate batch)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(sendTimeout);
        try
        {
            await send(batch, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Interlocked.Increment(ref failedSends);
            if (!lifetime.IsCancellationRequested
                && warningGate.TryAcquire(out long suppressedCount))
                ServerLogMessages.LiveStateSendTimedOut(logger, suppressedCount);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failedSends);
            if (warningGate.TryAcquire(out long suppressedCount))
                ServerLogMessages.LiveStateSendFailed(logger, ex, suppressedCount);
        }
    }

    private void Remove(ScopeSender sender)
        => senders.TryRemove(
            new KeyValuePair<StateStreamScopeDto, ScopeSender>(sender.Scope, sender));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifetime.Cancel();
        ScopeSender[] active = senders.Values.ToArray();
        foreach (ScopeSender sender in active)
            sender.Stop();
        try { await Task.WhenAll(active.Select(sender => sender.Worker)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        senders.Clear();
        lifetime.Dispose();
    }

    private enum EnqueueResult
    {
        Accepted,
        ReplacedOlder,
        Retired,
    }

    private sealed class ScopeSender(
        BoundedStateBatchDispatcher owner,
        StateStreamScopeDto scope,
        int capacity)
    {
        private readonly Queue<StateUpdate> queue = [];
        private readonly SemaphoreSlim signal = new(0, 1);
        private readonly object gate = new();
        private bool retired;
        private Task? worker;

        public StateStreamScopeDto Scope { get; } = scope;
        public Task Worker => worker ?? Task.CompletedTask;
        public int Depth { get { lock (gate) return queue.Count; } }

        public void Start()
            => worker = Task.Run(RunAsync, CancellationToken.None);

        public EnqueueResult TryEnqueue(StateUpdate batch)
        {
            lock (gate)
            {
                if (retired)
                    return EnqueueResult.Retired;
                if (queue.Count >= capacity)
                {
                    // Keep the most recent state. The evicted batch creates a
                    // sequence gap, which makes the client hydrate a snapshot.
                    queue.Dequeue();
                    queue.Enqueue(batch);
                    return EnqueueResult.ReplacedOlder;
                }
                queue.Enqueue(batch);
                if (queue.Count == 1 && signal.CurrentCount == 0)
                    signal.Release();
                return EnqueueResult.Accepted;
            }
        }

        public void Stop()
        {
            lock (gate)
                retired = true;
            try
            {
                if (signal.CurrentCount == 0)
                    signal.Release();
            }
            catch (ObjectDisposedException) { }
        }

        private async Task RunAsync()
        {
            try
            {
                while (!owner.lifetime.IsCancellationRequested)
                {
                    bool ready = await signal.WaitAsync(
                        owner.idleLifetime,
                        owner.lifetime.Token).ConfigureAwait(false);
                    if (!ready)
                    {
                        lock (gate)
                        {
                            if (queue.Count != 0)
                                continue;
                            retired = true;
                        }
                        owner.Remove(this);
                        return;
                    }

                    while (!owner.lifetime.IsCancellationRequested)
                    {
                        StateUpdate? batch;
                        lock (gate)
                            batch = queue.Count == 0 ? null : queue.Dequeue();
                        if (batch is null)
                            break;
                        await owner.SendAsync(batch).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested) { }
            finally
            {
                lock (gate)
                {
                    retired = true;
                    queue.Clear();
                }
                owner.Remove(this);
                signal.Dispose();
            }
        }
    }
}
