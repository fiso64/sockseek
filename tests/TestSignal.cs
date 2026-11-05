namespace Tests;

// Capture the next notification before checking state, so a change between the
// check and await cannot be lost. Timeouts are watchdogs, never synchronization.
internal sealed class TestSignal : IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource next = NewCompletion();
    private Action? unsubscribe;

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Pulse()
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            previous = next;
            next = NewCompletion();
        }
        previous.TrySetResult();
    }

    public TestSignal Subscribe<T>(Action<Action<T>> add, Action<Action<T>> remove)
    {
        void OnChanged(T _) => Pulse();
        add(OnChanged);
        unsubscribe += () => remove(OnChanged);
        return this;
    }

    public Task WaitUntilAsync(Func<bool> condition, string message = "Expected state was not reached.", int timeoutMs = 5000)
        => WaitUntilAsync(() => Task.FromResult(condition()), message, timeoutMs);

    public async Task WaitUntilAsync(Func<Task<bool>> condition, string message = "Expected state was not reached.", int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            Task changed;
            lock (gate) changed = next.Task;
            if (await condition())
                return;
            try { await changed.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(message);
            }
        }
    }

    public void Dispose() => unsubscribe?.Invoke();
}
