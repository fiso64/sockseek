using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.Core;

[TestClass]
public sealed class BoundedAsyncTests
{
    [TestMethod]
    public async Task ForEachAsync_InvokesInSourceOrderBoundsWorkAndDrainsAfterFailure()
    {
        const int itemCount = 20;
        const int concurrency = 3;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationOrder = new List<int>();
        int attempted = 0;
        int active = 0;
        int maximumActive = 0;

        Task run = BoundedAsync.ForEachAsync(
            Enumerable.Range(0, itemCount),
            concurrency,
            async item =>
            {
                lock (invocationOrder)
                    invocationOrder.Add(item);
                Interlocked.Increment(ref attempted);
                int nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                if (Volatile.Read(ref attempted) >= concurrency)
                    firstWave.TrySetResult();
                try
                {
                    await release.Task;
                    if (item == 0)
                        throw new InvalidOperationException("expected");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (invocationOrder)
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, invocationOrder.ToArray());
        release.SetResult();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run);
        Assert.AreEqual("expected", exception.Message);
        Assert.AreEqual(itemCount, attempted);
        Assert.IsLessThanOrEqualTo(upperBound: concurrency, value: maximumActive);
        lock (invocationOrder)
            CollectionAssert.AreEqual(Enumerable.Range(0, itemCount).ToArray(), invocationOrder.ToArray());
    }

    [TestMethod]
    public async Task AsyncSource_InvokesInSourceOrderBoundsWorkAndDrainsAfterFailure()
    {
        const int itemCount = 20;
        const int concurrency = 3;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationOrder = new List<int>();
        int attempted = 0;
        int active = 0;
        int maximumActive = 0;

        Task run = BoundedAsync.ForEachAsync(
            Items(),
            concurrency,
            async item =>
            {
                lock (invocationOrder)
                    invocationOrder.Add(item);
                Interlocked.Increment(ref attempted);
                int nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                if (Volatile.Read(ref attempted) >= concurrency)
                    firstWave.TrySetResult();
                try
                {
                    await release.Task;
                    if (item == 0)
                        throw new InvalidOperationException("expected");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (invocationOrder)
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, invocationOrder.ToArray());
        release.SetResult();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run);
        Assert.AreEqual("expected", exception.Message);
        Assert.AreEqual(itemCount, attempted);
        Assert.IsLessThanOrEqualTo(upperBound: concurrency, value: maximumActive);
        lock (invocationOrder)
            CollectionAssert.AreEqual(Enumerable.Range(0, itemCount).ToArray(), invocationOrder.ToArray());

        static async IAsyncEnumerable<int> Items()
        {
            await Task.Yield();
            for (int item = 0; item < itemCount; item++)
                yield return item;
        }
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= value)
                return;
        }
        while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
    }
}
