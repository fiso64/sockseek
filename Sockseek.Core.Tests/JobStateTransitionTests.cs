using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Transfers.Downloads.JobTracking;

namespace Tests.Core;

[TestClass]
public class JobStateTransitionTests
{
    [TestMethod]
    public async Task ConcurrentCancellationPublishesCapturedTransitionsInOrderAndRetryResetsAtomically()
    {
        var song = new SongJob(new SongQuery { Title = "Track" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        song.StateChanged += (_, transition) =>
        {
            if (transition.Before.LifecycleState != JobLifecycleState.Pending) return;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var events = new DownloadEvents();
        var changes = new List<JobStateChangedChange>();
        events.JobStateChanged += changes.Add;
        var tracker = new DownloadJobTracker(events);
        tracker.Register(song, null);
        var running = Task.Run(() => song.UpdateActivity(JobActivityPhase.Downloading));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The first observer is blocked before the tracker captures an event.
            // Mutation, snapshots and cancellation must still be independent of it.
            await Task.Run(() =>
            {
                song.Cancel(JobCancellationSource.UserRequestedJob);
                song.SetCancelled(JobCancellationSource.InternalEngine, "stopped", "detail");
                song.UpdateActivity(JobActivityPhase.Downloading);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            var state = song.StateSnapshot;
            Assert.AreEqual(JobLifecycleState.Terminal, state.LifecycleState);
            Assert.AreEqual(JobActivityPhase.None, state.ActivityPhase);
            Assert.AreEqual(JobTerminalOutcome.Cancelled, state.TerminalOutcome);
            Assert.AreEqual(JobCancellationSource.UserRequestedJob, state.CancellationSource);
            var snapshot = CoreSnapshotFactory.CreateExecution(song, state);
            Assert.AreEqual(state.Revision, snapshot.Revision);
            Assert.AreEqual(state.FailureDetail, snapshot.FailureDetail);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.HasCount(3, changes);
        Assert.AreEqual(JobLifecycleState.Running, changes[0].Job.LifecycleState);
        Assert.AreEqual(JobCancellationSource.None, changes[0].Job.CancellationSource);
        Assert.AreEqual(JobCancellationSource.UserRequestedJob, changes[1].Job.CancellationSource);
        Assert.AreEqual(JobTerminalOutcome.Cancelled, changes[2].Job.TerminalOutcome);
        Assert.IsTrue(changes.Zip(changes.Skip(1)).All(pair => pair.First.Job.Revision < pair.Second.Job.Revision));

        song.ResetToPending();
        var reset = song.StateSnapshot;
        Assert.AreEqual(default(JobStateSnapshot), reset with { Revision = 0 });
        song.UpdateActivity(JobActivityPhase.Downloading);
        song.SetDone();
        Assert.AreEqual(JobTerminalOutcome.Succeeded, changes[^1].Job.TerminalOutcome);
        Assert.AreEqual(JobCancellationSource.None, changes[^1].Job.CancellationSource);
    }

    [TestMethod]
    public async Task ConcurrentSnapshotReadsNeverCombineDifferentLifecycleStates()
    {
        var song = new SongJob(new SongQuery { Title = "Track" });
        using var start = new Barrier(2);
        var writer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 200; i++)
            {
                song.ResetToPending();
                song.UpdateActivity(JobActivityPhase.Searching);
                song.SetCancelled(JobCancellationSource.UserRequestedJob, "cancelled", "detail");
            }
        });
        start.SignalAndWait();
        for (int i = 0; i < 1000; i++)
        {
            var snapshot = CoreSnapshotFactory.CreateExecution(song, 1);
            if (snapshot.LifecycleState == JobLifecycleState.Terminal)
            {
                Assert.AreEqual(JobActivityPhase.None, snapshot.ActivityPhase);
                Assert.AreEqual(JobTerminalOutcome.Cancelled, snapshot.TerminalOutcome);
                Assert.AreEqual(JobFailureReason.Cancelled, snapshot.FailureReason);
                Assert.AreEqual(JobCancellationSource.UserRequestedJob, snapshot.CancellationSource);
                Assert.AreEqual("detail", snapshot.FailureDetail);
            }
            else
            {
                Assert.AreEqual(JobTerminalOutcome.None, snapshot.TerminalOutcome);
                Assert.AreEqual(JobFailureReason.None, snapshot.FailureReason);
                Assert.IsNull(snapshot.FailureDetail);
            }
        }
        await writer;
    }

    [TestMethod]
    public void TerminalTransition_PublishesOneCoherentSnapshot()
    {
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        song.UpdateActivity(JobActivityPhase.RunningOnComplete);

        var transitions = new List<JobStateTransition>();
        var propertySnapshots = new List<JobStateSnapshot>();

        song.StateChanged += (_, transition) => transitions.Add(transition);
        song.PropertyChanged += (_, _) => propertySnapshots.Add(song.StateSnapshot);

        song.SetDone();

        Assert.AreEqual(1, transitions.Count);
        var after = transitions[0].After;
        Assert.AreEqual(JobLifecycleState.Terminal, after.LifecycleState);
        Assert.AreEqual(JobActivityPhase.None, after.ActivityPhase);
        Assert.AreEqual(JobTerminalOutcome.Succeeded, after.TerminalOutcome);
        Assert.IsFalse(transitions.Any(transition =>
            transition.After.LifecycleState == JobLifecycleState.Running
            && transition.After.ActivityPhase == JobActivityPhase.None
            && transition.After.TerminalOutcome == JobTerminalOutcome.None));
        Assert.IsTrue(propertySnapshots.All(snapshot =>
            snapshot.LifecycleState == JobLifecycleState.Terminal
            && snapshot.ActivityPhase == JobActivityPhase.None
            && snapshot.TerminalOutcome == JobTerminalOutcome.Succeeded));
    }
}
