using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class BoundedStateBatchDispatcherTests
{
    [TestMethod]
    public async Task SlowScope_IsIsolatedAndItsQueueStaysBounded()
    {
        StateStreamScopeDto slowScope = StateStreamScopeDto.ChatRoom(Guid.NewGuid());
        StateStreamScopeDto otherScope = StateStreamScopeDto.ChatRoom(Guid.NewGuid());
        var slowStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var latestSlowSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<StateUpdate>();

        await using var dispatcher = new BoundedStateBatchDispatcher(
            async (batch, cancellationToken) =>
            {
                if (batch.Scope == slowScope)
                {
                    slowStarted.TrySetResult();
                    await releaseSlow.Task.WaitAsync(cancellationToken);
                }
                sent.Enqueue(batch);
                if (batch.Scope == otherScope)
                    otherSent.TrySetResult();
                if (batch.Scope == slowScope && batch.Sequence == 20)
                    latestSlowSent.TrySetResult();
            },
            perScopeCapacity: 3,
            maximumScopes: 8,
            sendTimeout: TimeSpan.FromSeconds(10),
            idleLifetime: TimeSpan.FromSeconds(10));

        try
        {
            Assert.IsTrue(dispatcher.TryPublish(Batch(slowScope, 0, 1)));
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(dispatcher.TryPublish(Batch(otherScope, 0, 1)));
            await otherSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(sent.Any(batch => batch.Scope == otherScope));

            for (int sequence = 2; sequence <= 20; sequence++)
                Assert.IsTrue(dispatcher.TryPublish(
                    Batch(slowScope, sequence - 1, sequence)));

            Assert.IsTrue(
                dispatcher.DroppedBatches > 0,
                "The stalled scope must evict older excess batches.");
            Assert.IsTrue(
                dispatcher.QueuedBatchCount <= 3,
                "The stalled scope exceeded its configured queue capacity.");

            releaseSlow.TrySetResult();
            await latestSlowSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            StateUpdate[] slowBatches = sent
                .Where(batch => batch.Scope == slowScope)
                .ToArray();
            Assert.AreEqual(20, slowBatches[^1].Sequence);
            Assert.IsTrue(
                slowBatches.Zip(slowBatches.Skip(1))
                    .Any(pair => pair.Second.PreviousSequence != pair.First.Sequence),
                "Overflow must produce an observable sequence gap for snapshot recovery.");
        }
        finally
        {
            releaseSlow.TrySetResult();
        }
    }

    private static StateUpdate Batch(
        StateStreamScopeDto scope,
        long previousSequence,
        long sequence)
        => new(
            scope,
            Guid.Parse("7c141fe1-cd3b-4b44-bef6-752b5bc283db"),
            previousSequence,
            sequence,
            DateTimeOffset.UnixEpoch,
            StateDelta.Empty,
            []);
}
