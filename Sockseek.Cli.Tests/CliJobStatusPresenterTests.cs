using Sockseek.Api;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public class CliJobStatusPresenterTests
{
    [TestMethod]
    public void ForSplit_TerminalOutcomeWinsOverActivityAndTransfer()
    {
        var status = CliJobStatusPresenter.ForSplit(
            JobLifecycleState.Terminal,
            JobActivityPhase.Downloading,
            JobTerminalOutcome.PartialSuccess,
            transferPhase: TransferPhase.InProgress);

        Assert.AreEqual("partial", status.Label);
        Assert.AreEqual(ConsoleColor.Yellow, status.Color);
        Assert.IsTrue(status.IsFailed);
    }

    [TestMethod]
    public void ForSplit_TransferStateWinsForActiveSongs()
    {
        var status = CliJobStatusPresenter.ForSplit(
            JobLifecycleState.Running,
            JobActivityPhase.Downloading,
            JobTerminalOutcome.None,
            transferPhase: TransferPhase.QueuedRemotely);

        Assert.AreEqual("queued (R)", status.Label);
        Assert.IsTrue(status.IsActive);
    }

    [TestMethod]
    public void ForSplit_ActivityPhaseWinsBeforeLifecycleFallback()
    {
        var status = CliJobStatusPresenter.ForSplit(
            JobLifecycleState.Running,
            JobActivityPhase.ProcessingSearchResults,
            JobTerminalOutcome.None);

        Assert.AreEqual("processing results", status.Label);
        Assert.IsTrue(status.IsActive);
    }

    [TestMethod]
    [DataRow(JobActivityPhase.Organizing)]
    [DataRow(JobActivityPhase.RunningOnComplete)]
    [DataRow(JobActivityPhase.ProcessingFallback)]
    public void ProcessingOwnsStatusWhileTransportRemainsOpen(JobActivityPhase phase)
    {
        var expected = CliJobStatusPresenter.ForSplit(JobLifecycleState.Running, phase, JobTerminalOutcome.None);
        var actual = CliJobStatusPresenter.ForSplit(JobLifecycleState.Running, phase, JobTerminalOutcome.None,
            transferPhase: TransferPhase.InProgress);
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void ForSplit_RunningOnComplete_UsesOnCompleteLabel()
    {
        var status = CliJobStatusPresenter.ForSplit(
            JobLifecycleState.Running,
            JobActivityPhase.RunningOnComplete,
            JobTerminalOutcome.None);

        Assert.AreEqual("on-complete", status.Label);
        Assert.IsTrue(status.IsActive);
    }

    [TestMethod]
    public void ForSplit_ChildJobsFailed_UsesSpecificFailureLabel()
    {
        var status = CliJobStatusPresenter.ForSplit(
            JobLifecycleState.Terminal,
            JobActivityPhase.None,
            JobTerminalOutcome.Failed,
            failureReason: JobFailureReason.ChildJobsFailed);

        Assert.AreEqual("failed [Child jobs failed]", status.Label);
        Assert.IsTrue(status.IsFailed);
    }
}
