namespace Sockseek.Core.Services;

/// <summary>
/// Bounds both executing and waiting callback work. The outstanding count is
/// reserved before waiting so callers cannot create an unbounded semaphore
/// queue when the protocol supplies no cancellation token.
/// </summary>
internal sealed class BoundedCallbackGate
{
    private readonly SemaphoreSlim concurrency;
    private readonly int capacity;
    private int outstanding;

    public BoundedCallbackGate(int concurrency, int capacity)
    {
        if (concurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        if (capacity < concurrency)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.concurrency = new SemaphoreSlim(concurrency, concurrency);
        this.capacity = capacity;
    }

    public async ValueTask<Lease?> TryEnterAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref outstanding) > capacity)
        {
            Interlocked.Decrement(ref outstanding);
            return null;
        }

        try
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this);
        }
        catch
        {
            Interlocked.Decrement(ref outstanding);
            throw;
        }
    }

    private void Exit()
    {
        concurrency.Release();
        Interlocked.Decrement(ref outstanding);
    }

    internal sealed class Lease(BoundedCallbackGate owner) : IAsyncDisposable, IDisposable
    {
        private BoundedCallbackGate? owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref owner, null)?.Exit();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
