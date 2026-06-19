using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.Core;

[TestClass]
public class JobStateTransitionTests
{
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
