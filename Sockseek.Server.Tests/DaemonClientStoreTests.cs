using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;

namespace Tests.Server;

[TestClass]
public class DaemonClientStoreTests
{
    [TestMethod]
    public void SnapshotThenDeltas_ReconstructsStateWithoutActivity()
    {
        var store = new DaemonClientStore();
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var initial = Job(jobId, workflowId, revision: 1, lifecycle: ServerJobLifecycleState.Pending)
            with { Stats = new JobSummaryStatsDto(CandidateCount: 2) };

        store.ApplySnapshot(Snapshot(epoch, 5, workflowId, [initial]));
        var terminal = initial.Lifecycle with
        {
            LifecycleState = ServerJobLifecycleState.Terminal,
            TerminalOutcome = ServerJobTerminalOutcome.Succeeded,
            FailureMessage = null,
        };
        var transfer = Transfer(transferId, jobId, workflowId, revision: 1, bytes: 0);
        var update = store.Apply(Batch(
            epoch,
            previous: 5,
            sequence: 7,
            workflowId,
            new StateDeltaDto(
                null,
                [],
                [new JobDeltaDto(jobId, 2, Lifecycle: terminal,
                    Stats: new JobSummaryStatsDto(CandidateCount: null))],
                [],
                [new TransferDeltaDto(transferId, 1, Added: transfer)],
                [],
                [],
                [],
                [])));

        Assert.AreEqual(DaemonClientApplyStatus.Applied, update.Status);
        Assert.AreEqual(ServerJobLifecycleState.Terminal, store.GetJob(jobId)?.LifecycleState);
        Assert.AreEqual(ServerJobTerminalOutcome.Succeeded, store.GetJob(jobId)?.TerminalOutcome);
        Assert.IsNull(store.GetJob(jobId)?.Stats?.CandidateCount);
        Assert.AreEqual(transferId, store.GetJobTransfers(jobId).Single().TransferId);
        Assert.AreEqual(0, update.Activity.Count);
    }

    [TestMethod]
    public void OverlapAppliesButStaleBatchIsIgnored()
    {
        var store = new DaemonClientStore();
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var initial = Job(jobId, workflowId, revision: 1, lifecycle: ServerJobLifecycleState.Pending);
        store.ApplySnapshot(Snapshot(epoch, 10, workflowId, [initial]));

        var running = initial.Lifecycle with { LifecycleState = ServerJobLifecycleState.Running };
        var overlap = store.Apply(Batch(
            epoch,
            previous: 8,
            sequence: 12,
            workflowId,
            StateDeltaDto.Empty with
            {
                Jobs = [new JobDeltaDto(jobId, 2, Lifecycle: running)],
            }));
        var stale = store.Apply(Batch(
            epoch,
            previous: 8,
            sequence: 10,
            workflowId,
            StateDeltaDto.Empty with
            {
                Jobs = [new JobDeltaDto(jobId, 3, Lifecycle: initial.Lifecycle)],
            }));

        Assert.AreEqual(DaemonClientApplyStatus.Applied, overlap.Status);
        Assert.AreEqual(DaemonClientApplyStatus.IgnoredStale, stale.Status);
        Assert.AreEqual(ServerJobLifecycleState.Running, store.GetJob(jobId)?.LifecycleState);
    }

    [TestMethod]
    public void GapMarksScopeStaleAndDoesNotApplyGappedState()
    {
        var store = new DaemonClientStore();
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var initial = Job(jobId, workflowId, revision: 1, lifecycle: ServerJobLifecycleState.Pending);
        var scope = StateStreamScopeDto.Workflow(workflowId);
        store.ApplySnapshot(Snapshot(epoch, 3, workflowId, [initial]));

        var terminal = initial.Lifecycle with { LifecycleState = ServerJobLifecycleState.Terminal };
        var update = store.Apply(Batch(
            epoch,
            previous: 5,
            sequence: 6,
            workflowId,
            StateDeltaDto.Empty with
            {
                Jobs = [new JobDeltaDto(jobId, 2, Lifecycle: terminal)],
            }));

        Assert.AreEqual(DaemonClientApplyStatus.RecoveryRequired, update.Status);
        Assert.AreEqual(DaemonClientRecoveryReason.SequenceGap, update.RecoveryReason);
        Assert.IsTrue(store.IsStale(scope));
        Assert.AreEqual(ServerJobLifecycleState.Pending, store.GetJob(jobId)?.LifecycleState);
        Assert.AreEqual(3, store.GetPosition(scope)?.Sequence);
    }

    [TestMethod]
    public void EpochChangeDoesNotApplyUntilReplacementSnapshot()
    {
        var store = new DaemonClientStore();
        var firstEpoch = Guid.NewGuid();
        var nextEpoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var initial = Job(jobId, workflowId, revision: 1, lifecycle: ServerJobLifecycleState.Pending);
        store.ApplySnapshot(Snapshot(firstEpoch, 2, workflowId, [initial]));

        var update = store.Apply(Batch(
            nextEpoch,
            previous: 0,
            sequence: 1,
            workflowId,
            StateDeltaDto.Empty with { RemovedJobIds = [jobId] }));
        Assert.AreEqual(DaemonClientRecoveryReason.EpochChanged, update.RecoveryReason);
        Assert.IsNotNull(store.GetJob(jobId));

        store.ApplySnapshot(Snapshot(nextEpoch, 1, workflowId, []));
        Assert.IsFalse(store.IsStale(StateStreamScopeDto.Workflow(workflowId)));
        Assert.IsNull(store.GetJob(jobId));
    }

    [TestMethod]
    public void LiveStateView_IsAtomicAcrossScopes()
    {
        var store = new DaemonClientStore();
        var firstWorkflow = Guid.NewGuid();
        var secondWorkflow = Guid.NewGuid();
        var first = Job(
            Guid.NewGuid(),
            firstWorkflow,
            1,
            ServerJobLifecycleState.Running);
        var second = Job(
            Guid.NewGuid(),
            secondWorkflow,
            1,
            ServerJobLifecycleState.Running);
        store.ApplySnapshot(Snapshot(
            Guid.NewGuid(),
            0,
            firstWorkflow,
            [first]));
        store.ApplySnapshot(Snapshot(
            Guid.NewGuid(),
            0,
            secondWorkflow,
            [second]));

        var bothScopes = store.GetLiveStateView();
        CollectionAssert.AreEquivalent(
            new[] { first.JobId, second.JobId },
            bothScopes.Jobs.Select(job => job.JobId).ToArray());

        store.ApplySnapshot(Snapshot(
            Guid.NewGuid(),
            1,
            firstWorkflow,
            []));
        var replacedScope = store.GetLiveStateView();

        CollectionAssert.AreEqual(
            new[] { second.JobId },
            replacedScope.Jobs.Select(job => job.JobId).ToArray());
    }

    [TestMethod]
    public void QueriesGroupAllHydratedJobsByWorkflowAndKeyTransfersByTransferId()
    {
        var store = new DaemonClientStore();
        var epoch = Guid.NewGuid();
        var firstWorkflow = Guid.NewGuid();
        var secondWorkflow = Guid.NewGuid();
        var firstJob = Job(Guid.NewGuid(), firstWorkflow, 1, ServerJobLifecycleState.Running);
        var secondJob = Job(Guid.NewGuid(), secondWorkflow, 1, ServerJobLifecycleState.Terminal);
        var firstTransfer = Transfer(Guid.NewGuid(), firstJob.JobId, firstWorkflow, 1, 5);
        var secondTransfer = Transfer(Guid.NewGuid(), firstJob.JobId, firstWorkflow, 1, 9);
        store.ApplySnapshot(new StateSnapshotDto(
            StateStreamScopeDto.Daemon,
            new StateStreamPositionDto(epoch, 0),
            DateTimeOffset.UtcNow,
            null,
            [
                new WorkflowStateDto(1, Workflow(firstWorkflow, firstJob.JobId)),
                new WorkflowStateDto(1, Workflow(secondWorkflow, secondJob.JobId)),
            ],
            [firstJob, secondJob],
            [],
            [firstTransfer, secondTransfer]));

        Assert.AreEqual(2, store.GetJobsGroupedByWorkflow().Count);
        Assert.AreEqual(1, store.GetActiveJobs().Count);
        Assert.AreEqual(1, store.GetTerminalJobs().Count);
        Assert.AreEqual(2, store.GetJobTransfers(firstJob.JobId).Count);
        Assert.AreEqual(9, store.GetTransfer(secondTransfer.TransferId)?.Progress.BytesTransferred);
    }

    [TestMethod]
    public void ComponentReplacement_ClearsNullableFields_AndRejectsStaleEntityRevision()
    {
        var store = new DaemonClientStore();
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var failed = Job(jobId, workflowId, 3, ServerJobLifecycleState.Terminal) with
        {
            Lifecycle = Job(jobId, workflowId, 3, ServerJobLifecycleState.Terminal).Lifecycle with
            {
                TerminalOutcome = ServerJobTerminalOutcome.Failed,
                FailureReason = ServerProtocol.FailureReasons.Other,
                FailureMessage = "old failure",
                FailureDetail = "old detail",
            },
        };
        store.ApplySnapshot(Snapshot(epoch, 1, workflowId, [failed]));

        var cleared = failed.Lifecycle with
        {
            LifecycleState = ServerJobLifecycleState.Running,
            TerminalOutcome = ServerJobTerminalOutcome.None,
            FailureReason = null,
            FailureMessage = null,
            FailureDetail = null,
        };
        store.Apply(Batch(
            epoch,
            1,
            2,
            workflowId,
            StateDeltaDto.Empty with
            {
                Jobs = [new JobDeltaDto(jobId, 4, Lifecycle: cleared)],
            }));
        store.Apply(Batch(
            epoch,
            2,
            3,
            workflowId,
            StateDeltaDto.Empty with
            {
                Jobs = [new JobDeltaDto(jobId, 2, Lifecycle: failed.Lifecycle)],
            }));

        var current = store.GetJob(jobId);
        Assert.IsNotNull(current);
        Assert.AreEqual(ServerJobLifecycleState.Running, current.LifecycleState);
        Assert.IsNull(current.FailureReason);
        Assert.IsNull(current.FailureMessage);
        Assert.IsNull(current.FailureDetail);
    }

    [TestMethod]
    public void TerminalTransfer_RemainsInChangedRowsWhenRemovedFromLiveStore()
    {
        var store = new DaemonClientStore();
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var job = Job(Guid.NewGuid(), workflowId, 1, ServerJobLifecycleState.Running);
        var transfer = Transfer(Guid.NewGuid(), job.JobId, workflowId, 1, 25);
        store.ApplySnapshot(new StateSnapshotDto(
            StateStreamScopeDto.Workflow(workflowId),
            new StateStreamPositionDto(epoch, 1),
            DateTimeOffset.UtcNow,
            null,
            [new WorkflowStateDto(1, Workflow(workflowId, job.JobId))],
            [job],
            [],
            [transfer]));
        var terminal = transfer with
        {
            Revision = 2,
            Status = transfer.Status with { State = "Completed", IsTerminal = true },
            Progress = transfer.Progress with { BytesTransferred = 100 },
        };

        var update = store.Apply(Batch(
            epoch,
            1,
            2,
            workflowId,
            StateDeltaDto.Empty with
            {
                Transfers =
                [
                    new TransferDeltaDto(
                        transfer.TransferId,
                        2,
                        Status: terminal.Status,
                        Progress: terminal.Progress),
                ],
                RemovedTransferIds = [transfer.TransferId],
            }));

        Assert.IsNull(store.GetTransfer(transfer.TransferId));
        Assert.AreEqual(1, update.ChangedTransfers.Count);
        Assert.IsTrue(update.ChangedTransfers[0].Status.IsTerminal);
        Assert.AreEqual(100, update.ChangedTransfers[0].Progress.BytesTransferred);
    }

    private static StateSnapshotDto Snapshot(
        Guid epoch,
        long sequence,
        Guid workflowId,
        IReadOnlyList<JobStateDto> jobs)
        => new(
            StateStreamScopeDto.Workflow(workflowId),
            new StateStreamPositionDto(epoch, sequence),
            DateTimeOffset.UtcNow,
            null,
            jobs.Count == 0 ? [] : [new WorkflowStateDto(1, Workflow(workflowId, jobs[0].JobId))],
            jobs,
            [],
            []);

    private static StateUpdateBatchDto Batch(
        Guid epoch,
        long previous,
        long sequence,
        Guid workflowId,
        StateDeltaDto state)
        => new(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            previous,
            sequence,
            DateTimeOffset.UtcNow,
            state,
            []);

    private static JobStateDto Job(
        Guid jobId,
        Guid workflowId,
        long revision,
        ServerJobLifecycleState lifecycle)
        => JobStateDto.FromSummary(
            new JobSummaryDto(
                jobId,
                1,
                workflowId,
                ServerJobKind.Song,
                lifecycle,
                ServerJobActivityPhase.None,
                null,
                ServerJobTerminalOutcome.None,
                ServerJobSkipReason.None,
                "job",
                "query",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                []),
            revision);

    private static WorkflowSummaryDto Workflow(Guid workflowId, Guid rootJobId)
        => new(workflowId, "workflow", ServerWorkflowState.Active, 1, 1, 0, 0);

    private static TransferStateDto Transfer(
        Guid transferId,
        Guid jobId,
        Guid workflowId,
        long revision,
        long bytes)
        => new(
            transferId,
            revision,
            new TransferIdentityFieldsDto(
                jobId,
                workflowId,
                "Download",
                "SoulseekPeer",
                "user",
                "music\\track.mp3",
                "candidate"),
            new TransferStatusFieldsDto("InProgress", "track.mp3", 1, false),
            new TransferProgressFieldsDto(bytes, 100));
}
