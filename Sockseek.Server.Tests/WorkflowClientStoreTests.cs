using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;

namespace Tests.Server;

[TestClass]
public class WorkflowClientStoreTests
{
    [TestMethod]
    public void Apply_StoresBatchStateAndReturnsRendererSafeEventOrder()
    {
        var store = new WorkflowClientStore();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var job = JobSummary(jobId, workflowId, ServerJobLifecycleState.Running);
        var workflow = new WorkflowSummaryDto(workflowId, "workflow", ServerWorkflowState.Active, [jobId], 1, 0, 0);
        var search = new SearchUpdatedDto(jobId, workflowId, Revision: 2, ResultCount: 5, IsComplete: false);
        var activity = new ServerEventEnvelopeDto(
            12,
            "download.started",
            now,
            "activity",
            SnapshotInvalidation: false,
            workflowId,
            new DownloadStartedEventDto(
                jobId,
                7,
                workflowId,
                new SongQueryDto("Artist", null, "Title", null, null, false),
                Candidate()));
        var progress = new DownloadProgressEventDto(jobId, workflowId, BytesTransferred: 50, TotalBytes: 100);

        var update = store.Apply(new WorkflowUpdateBatchDto(
            Sequence: 1,
            OccurredAtUtc: now,
            WorkflowId: workflowId,
            Workflow: workflow,
            JobUpserts: [job],
            SearchUpdates: [search],
            Progress: [progress],
            Activity: [activity]));

        CollectionAssert.AreEqual(
            new[] { "job.upserted", "workflow.upserted", "search.updated", "download.started", "download.progress" },
            update.Events.Select(e => e.Type).ToArray());
        Assert.AreEqual(job, store.GetJob(jobId));
        Assert.AreEqual(workflow, store.GetWorkflow(workflowId));
        Assert.AreEqual(search, store.GetSearchUpdate(jobId));
        Assert.AreEqual(progress, store.GetDownloadProgress(jobId));
        Assert.IsFalse(update.SequenceGapDetected);
        Assert.AreEqual(workflow, update.Workflow);
        CollectionAssert.AreEqual(new[] { job }, update.JobUpserts.ToArray());
        CollectionAssert.AreEqual(new[] { search }, update.SearchUpdates.ToArray());
        CollectionAssert.AreEqual(new[] { progress }, update.Progress.ToArray());
        CollectionAssert.AreEqual(new[] { activity }, update.Activity.ToArray());
    }

    [TestMethod]
    public void Apply_ReplacesStoredStateWithLatestBatchValues()
    {
        var store = new WorkflowClientStore();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var first = JobSummary(jobId, workflowId, ServerJobLifecycleState.Pending);
        var second = JobSummary(jobId, workflowId, ServerJobLifecycleState.Terminal) with
        {
            TerminalOutcome = ServerJobTerminalOutcome.Succeeded,
        };

        store.Apply(Batch(workflowId, [first]) with { Sequence = 1 });
        store.Apply(Batch(workflowId, [second]) with { Sequence = 2 });

        Assert.AreEqual(ServerJobLifecycleState.Terminal, store.GetJob(jobId)?.LifecycleState);
        Assert.AreEqual(ServerJobTerminalOutcome.Succeeded, store.GetJob(jobId)?.TerminalOutcome);
        Assert.AreEqual(1, store.GetWorkflowJobs(workflowId).Count);
    }

    [TestMethod]
    public void Apply_DetectsSequenceGap()
    {
        var store = new WorkflowClientStore();
        var workflowId = Guid.NewGuid();
        var job = JobSummary(Guid.NewGuid(), workflowId, ServerJobLifecycleState.Running);

        var update = store.Apply(Batch(workflowId, [job]) with { Sequence = 3 });

        Assert.IsTrue(update.SequenceGapDetected);
        Assert.AreEqual(1, update.ExpectedSequence);
        Assert.IsFalse(update.IsStale);
        Assert.AreEqual(job, store.GetJob(job.JobId));
    }

    [TestMethod]
    public void Apply_IgnoresStaleBatches()
    {
        var store = new WorkflowClientStore();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var newer = JobSummary(jobId, workflowId, ServerJobLifecycleState.Terminal) with
        {
            TerminalOutcome = ServerJobTerminalOutcome.Succeeded,
        };
        var stale = JobSummary(jobId, workflowId, ServerJobLifecycleState.Running);

        store.Apply(Batch(workflowId, [newer]) with { Sequence = 2 });
        var update = store.Apply(Batch(workflowId, [stale]) with { Sequence = 1 });

        Assert.IsTrue(update.IsStale);
        Assert.IsFalse(update.SequenceGapDetected);
        Assert.AreEqual(0, update.Events.Count);
        Assert.AreEqual(ServerJobLifecycleState.Terminal, store.GetJob(jobId)?.LifecycleState);
        Assert.AreEqual(ServerJobTerminalOutcome.Succeeded, store.GetJob(jobId)?.TerminalOutcome);
    }

    [TestMethod]
    public void ApplySnapshot_UpsertsWorkflowAndReturnedJobsWithoutDroppingUnseenJobs()
    {
        var store = new WorkflowClientStore();
        var workflowId = Guid.NewGuid();
        var existing = JobSummary(Guid.NewGuid(), workflowId, ServerJobLifecycleState.Running);
        var returned = JobSummary(Guid.NewGuid(), workflowId, ServerJobLifecycleState.Pending);
        var workflow = new WorkflowSummaryDto(workflowId, "workflow", ServerWorkflowState.Active, [returned.JobId], 1, 0, 0);

        store.Apply(Batch(workflowId, [existing]));
        var update = store.ApplySnapshot(new WorkflowDetailDto(workflow, [returned]));

        CollectionAssert.AreEqual(
            new[] { "job.upserted", "workflow.upserted" },
            update.Events.Select(e => e.Type).ToArray());
        Assert.AreEqual(workflow, store.GetWorkflow(workflowId));
        Assert.AreEqual(2, store.GetWorkflowJobs(workflowId).Count);
    }

    [TestMethod]
    public void ApplySnapshot_CanReplaceKnownWorkflowJobsForCompleteSnapshots()
    {
        var store = new WorkflowClientStore();
        var workflowId = Guid.NewGuid();
        var stale = JobSummary(Guid.NewGuid(), workflowId, ServerJobLifecycleState.Running);
        var current = JobSummary(Guid.NewGuid(), workflowId, ServerJobLifecycleState.Pending);
        var workflow = new WorkflowSummaryDto(workflowId, "workflow", ServerWorkflowState.Active, [current.JobId], 1, 0, 0);

        store.Apply(Batch(workflowId, [stale]));
        store.ApplySnapshot(new WorkflowDetailDto(workflow, [current]), replaceKnownWorkflowJobs: true);

        Assert.IsNull(store.GetJob(stale.JobId));
        Assert.AreEqual(current, store.GetJob(current.JobId));
        Assert.AreEqual(1, store.GetWorkflowJobs(workflowId).Count);
    }

    private static WorkflowUpdateBatchDto Batch(Guid workflowId, IReadOnlyList<JobSummaryDto> jobs)
        => new(
            Sequence: 1,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            WorkflowId: workflowId,
            Workflow: null,
            JobUpserts: jobs,
            SearchUpdates: [],
            Progress: [],
            Activity: []);

    private static JobSummaryDto JobSummary(
        Guid jobId,
        Guid workflowId,
        ServerJobLifecycleState lifecycleState)
        => new(
            jobId,
            7,
            workflowId,
            ServerJobKind.Song,
            lifecycleState,
            ServerJobActivityPhase.None,
            null,
            ServerJobTerminalOutcome.None,
            ServerJobSkipReason.None,
            null,
            "query",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            []);

    private static FileCandidateDto Candidate()
        => new(
            new FileCandidateRefDto("local", "Artist/Album/01. Artist - Title.flac"),
            "local",
            "Artist/Album/01. Artist - Title.flac",
            new PeerInfoDto("local", HasFreeUploadSlot: true, UploadSpeed: 1000),
            100,
            null,
            null,
            null,
            "flac",
            []);
}
