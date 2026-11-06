using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Transfers.Downloads.Runtime;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class WorkflowLifetimeCoordinatorTests
{
    [TestMethod]
    public void MultipleRootsRetireTogetherOnlyAfterTheLastRootCompletes()
    {
        Guid workflowId = Guid.NewGuid();
        var first = TerminalSong(workflowId, "First");
        var second = TerminalSong(workflowId, "Second");
        var jobs = new List<Job> { first, second };
        int retirements = 0;
        var coordinator = new WorkflowLifetimeCoordinator(
            _ => jobs,
            _ => false,
            _ => 7,
            (retiredWorkflowId, retiredJobs, settingsVersion) =>
            {
                Assert.AreEqual(workflowId, retiredWorkflowId);
                Assert.AreEqual(2, retiredJobs.Count);
                Assert.AreEqual(7, settingsVersion);
                retirements++;
            });

        var firstLease = coordinator.QueueRoot(first);
        var secondLease = coordinator.QueueRoot(second);

        coordinator.RootCompleted(firstLease);
        Assert.AreEqual(0, retirements);
        Assert.AreEqual(1, coordinator.RetainedGenerationCount);

        coordinator.RootCompleted(secondLease);
        Assert.AreEqual(1, retirements);
        Assert.AreEqual(0, coordinator.RetainedGenerationCount);
    }

    [TestMethod]
    public async Task SameIdSuccessorWaitsForPriorGenerationRetirement()
    {
        Guid workflowId = Guid.NewGuid();
        var first = TerminalSong(workflowId, "First");
        var successor = TerminalSong(workflowId, "Successor");
        IReadOnlyList<Job> currentJobs = [first];
        WorkflowLifetimeCoordinator.WorkflowRootLease? successorLease = null;
        int retirements = 0;
        WorkflowLifetimeCoordinator coordinator = null!;
        coordinator = new WorkflowLifetimeCoordinator(
            _ => currentJobs,
            _ => false,
            _ => retirements + 1,
            (_, _, _) =>
            {
                retirements++;
                if (retirements != 1)
                    return;

                currentJobs = [successor];
                successorLease = coordinator.QueueRoot(successor);
                Assert.IsFalse(successorLease.Generation.Ready.IsCompleted);
            });

        var firstLease = coordinator.QueueRoot(first);
        coordinator.RootCompleted(firstLease);

        Assert.IsNotNull(successorLease);
        await coordinator.WaitUntilReadyAsync(successorLease, CancellationToken.None);
        coordinator.RootCompleted(successorLease);

        Assert.AreEqual(2, retirements);
        Assert.AreEqual(0, coordinator.RetainedGenerationCount);
    }

    [TestMethod]
    public void AwaitingAndResumableStatePreventRetirementUntilExplicitlyClosed()
    {
        Guid workflowId = Guid.NewGuid();
        var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
        {
            WorkflowId = workflowId,
        };
        job.SetAwaitingSelection();
        bool resumable = true;
        int retirements = 0;
        var coordinator = new WorkflowLifetimeCoordinator(
            _ => [job],
            _ => resumable,
            _ => 1,
            (_, _, _) => retirements++);

        var lease = coordinator.QueueRoot(job);
        coordinator.RootCompleted(lease);
        Assert.AreEqual(0, retirements, "Awaiting selection must remain live.");

        job.SetSkipped(JobSkipReason.Manual);
        coordinator.Reevaluate(workflowId);
        Assert.AreEqual(0, retirements, "A terminal manual selection can still be resumable.");

        resumable = false;
        coordinator.Reevaluate(workflowId);
        Assert.AreEqual(1, retirements);
        Assert.AreEqual(0, coordinator.RetainedGenerationCount);
    }

    private static SongJob TerminalSong(Guid workflowId, string title)
    {
        var job = new SongJob(new SongQuery { Title = title })
        {
            WorkflowId = workflowId,
        };
        job.SetDone();
        return job;
    }
}
