using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.Core;

[TestClass]
public class RateLimitedSemaphoreTests
{
    [TestMethod]
    public void WaitAsync_WhenLimitExhausted_CallsOnWaiting()
    {
        var semaphore = new RateLimitedSemaphore(1, TimeSpan.FromSeconds(10));
        semaphore.WaitAsync().GetAwaiter().GetResult(); // exhaust the permit

        bool called = false;
        using var cts = new CancellationTokenSource();
        var task = semaphore.WaitAsync(onWaiting: () => called = true, cancellationToken: cts.Token);

        Assert.IsTrue(called, "onWaiting should fire when the semaphore is exhausted.");

        cts.Cancel();
        Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await task).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void WaitAsync_WhenPermitAvailable_DoesNotCallOnWaiting()
    {
        var semaphore = new RateLimitedSemaphore(2, TimeSpan.FromSeconds(10));

        bool called = false;
        semaphore.WaitAsync(onWaiting: () => called = true).GetAwaiter().GetResult();

        Assert.IsFalse(called, "onWaiting should not fire when a permit is available.");
    }

    [TestMethod]
    public void WaitAsync_WhenLimitExhausted_OnWaitingCalledOncePerWindow()
    {
        var semaphore = new RateLimitedSemaphore(1, TimeSpan.FromSeconds(10));
        semaphore.WaitAsync().GetAwaiter().GetResult(); // exhaust the permit

        int callCount = 0;
        using var cts = new CancellationTokenSource();
        var task1 = semaphore.WaitAsync(onWaiting: () => callCount++, cancellationToken: cts.Token);
        var task2 = semaphore.WaitAsync(onWaiting: () => callCount++, cancellationToken: cts.Token);

        Assert.AreEqual(1, callCount, "onWaiting should only fire once per rate-limit window.");

        cts.Cancel();
        Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await task1).GetAwaiter().GetResult();
        Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await task2).GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task WaitAsync_WhenCancelled_ThrowsImmediately()
    {
        // Give it 1 permit that takes 10 seconds to replenish.
        var semaphore = new RateLimitedSemaphore(1, TimeSpan.FromSeconds(10));
        
        // Consume the only permit
        await semaphore.WaitAsync();

        using var cts = new CancellationTokenSource();
        var waitTask = semaphore.WaitAsync(cancellationToken: cts.Token);

        // Verify it's actually blocking
        Assert.IsFalse(waitTask.IsCompleted, "WaitAsync should block when the rate limit is exhausted.");

        // Cancel the token
        cts.Cancel();

        // If the fix works, this will throw an OperationCanceledException immediately,
        // rather than hanging for 10 seconds.
        var ex = await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await waitTask);
        Assert.IsNotNull(ex);
    }
}