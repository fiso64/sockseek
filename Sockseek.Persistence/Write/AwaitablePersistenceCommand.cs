namespace Sockseek.Persistence.Write;

// The action may run again after rollback. Keep external effects outside it;
// the completion task is the boundary after which its SQL is durable.
internal abstract class AwaitablePersistenceCommand
{
    private int state;
    internal long QueuedAt { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
    internal System.Diagnostics.ActivityContext TraceContext { get; } =
        System.Diagnostics.Activity.Current?.Context ?? default;
    internal virtual bool RequiresTransaction => true;
    internal bool TryStart() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
    internal void CancelBeforeStart(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            Fail(new OperationCanceledException(cancellationToken));
    }
    internal abstract Task ApplyAsync(SockseekDbContext context, CancellationToken cancellationToken);
    internal abstract void Complete();
    internal abstract void Fail(Exception exception);
}

internal sealed class AwaitablePersistenceCommand<TResult>(
    Func<SockseekDbContext, CancellationToken, Task<TResult>> apply,
    bool requiresTransaction = true)
    : AwaitablePersistenceCommand
{
    private readonly TaskCompletionSource<TResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TResult? result;

    public Task<TResult> Task => completion.Task;
    internal override bool RequiresTransaction => requiresTransaction;

    internal override async Task ApplyAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
        => result = await apply(context, cancellationToken).ConfigureAwait(false);

    internal override void Complete()
        => completion.TrySetResult(result!);

    internal override void Fail(Exception exception)
    {
        if (exception is OperationCanceledException cancelled)
            completion.TrySetCanceled(cancelled.CancellationToken);
        else
            completion.TrySetException(exception);
    }
}
