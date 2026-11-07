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
        first.SubmissionId = Guid.NewGuid();
        second.SubmissionId = Guid.NewGuid();
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

        Task firstDone = coordinator.WaitForSubmissionAsync(first.SubmissionId.Value, CancellationToken.None);
        Task secondDone = coordinator.WaitForSubmissionAsync(second.SubmissionId.Value, CancellationToken.None);
        Assert.IsFalse(firstDone.IsCompleted, "A displayed terminal outcome is not execution completion.");
        coordinator.RootCompleted(firstLease);
        Assert.IsTrue(firstDone.IsCompletedSuccessfully);
        Assert.IsFalse(secondDone.IsCompleted, "Unrelated roots do not extend this submission's execution lifetime.");
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

        var cancellation = coordinator.QueueRoot(job);
        job.SetSkipped(JobSkipReason.Manual);
        coordinator.RootCompleted(cancellation);
        Assert.AreEqual(0, retirements, "A terminal manual selection can still be resumable.");

        var completion = coordinator.QueueRoot(job);
        resumable = false;
        coordinator.RootCompleted(completion);
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
