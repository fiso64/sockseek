using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Sockseek.Core;

internal static class BoundedAsync
{
    /// <summary>
    /// Invokes <paramref name="action"/> in source order while retaining at most
    /// <paramref name="maxDegreeOfParallelism"/> incomplete operations. Completion
    /// order and ordering within later asynchronous stages remain unconstrained.
    /// </summary>
    public static async Task ForEachAsync<T>(
        IEnumerable<T> source,
        int maxDegreeOfParallelism,
        Func<T, Task> action)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

        var failures = new ConcurrentQueue<ExceptionDispatchInfo>();
        var active = new List<Task>(maxDegreeOfParallelism);
        foreach (T item in source)
        {
            await WaitForCapacityAsync(active, maxDegreeOfParallelism).ConfigureAwait(false);
            active.Add(RunAsync(item, action, failures));
        }

        await Task.WhenAll(active).ConfigureAwait(false);
        RethrowFirst(failures);
    }

    /// <summary>
    /// Invokes <paramref name="action"/> in asynchronous source order while
    /// retaining at most <paramref name="maxDegreeOfParallelism"/> incomplete
    /// operations. Completion order and ordering within later asynchronous stages
    /// remain unconstrained.
    /// </summary>
    public static async Task ForEachAsync<T>(
        IAsyncEnumerable<T> source,
        int maxDegreeOfParallelism,
        Func<T, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

        var failures = new ConcurrentQueue<ExceptionDispatchInfo>();
        var active = new List<Task>(maxDegreeOfParallelism);
        ExceptionDispatchInfo? producerFailure = null;

        try
        {
            await foreach (T item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await WaitForCapacityAsync(active, maxDegreeOfParallelism).ConfigureAwait(false);
                active.Add(RunAsync(item, action, failures));
            }
        }
        catch (Exception exception)
        {
            producerFailure = ExceptionDispatchInfo.Capture(exception);
        }

        await Task.WhenAll(active).ConfigureAwait(false);
        producerFailure?.Throw();
        RethrowFirst(failures);
    }

    private static async Task WaitForCapacityAsync(
        List<Task> active,
        int capacity)
    {
        RemoveCompleted(active);
        while (active.Count >= capacity)
        {
            await Task.WhenAny(active).ConfigureAwait(false);
            RemoveCompleted(active);
        }
    }

    private static void RemoveCompleted(List<Task> active)
    {
        for (int index = active.Count - 1; index >= 0; index--)
        {
            if (active[index].IsCompleted)
                active.RemoveAt(index);
        }
    }

    private static async Task RunAsync<T>(
        T item,
        Func<T, Task> action,
        ConcurrentQueue<ExceptionDispatchInfo> failures)
    {
        try
        {
            await action(item).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Do not abandon later items merely because one item failed. This retains
            // Task.WhenAll's all-items-run behavior without its one-Task-per-item fan-out.
            failures.Enqueue(ExceptionDispatchInfo.Capture(exception));
        }
    }

    private static void RethrowFirst(ConcurrentQueue<ExceptionDispatchInfo> failures)
    {
        if (failures.TryPeek(out ExceptionDispatchInfo? failure))
            failure.Throw();
    }
}
