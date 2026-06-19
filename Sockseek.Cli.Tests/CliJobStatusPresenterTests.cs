using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.Downloading,
            ServerJobTerminalOutcome.PartialSuccess,
            transferState: "InProgress");

        Assert.AreEqual("partial", status.Label);
        Assert.IsTrue(status.IsFailed);
    }

    [TestMethod]
    public void ForSplit_TransferStateWinsForActiveSongs()
    {
        var status = CliJobStatusPresenter.ForSplit(
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Searching,
            ServerJobTerminalOutcome.None,
            transferState: "Queued, Remotely");

        Assert.AreEqual("queued (R)", status.Label);
        Assert.IsTrue(status.IsActive);
    }

    [TestMethod]
    public void ForSplit_ActivityPhaseWinsBeforeLifecycleFallback()
    {
        var status = CliJobStatusPresenter.ForSplit(
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.ProcessingSearchResults,
            ServerJobTerminalOutcome.None);

        Assert.AreEqual("processing results", status.Label);
        Assert.IsTrue(status.IsActive);
    }

    [TestMethod]
    public void ForSplit_RunningOnComplete_UsesOnCompleteLabel()
    {
        var status = CliJobStatusPresenter.ForSplit(
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.RunningOnComplete,
            ServerJobTerminalOutcome.None);

        Assert.AreEqual("on-complete", status.Label);
        Assert.IsTrue(status.IsActive);
    }

    [TestMethod]
    public void ForSplit_ChildJobsFailed_UsesSpecificFailureLabel()
    {
        var status = CliJobStatusPresenter.ForSplit(
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ServerJobTerminalOutcome.Failed,
            failureReason: ServerProtocol.FailureReasons.ChildJobsFailed);

        Assert.AreEqual("failed [Child jobs failed]", status.Label);
        Assert.IsTrue(status.IsFailed);
    }
}
