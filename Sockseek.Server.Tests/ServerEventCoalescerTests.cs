using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class ServerEventCoalescerTests
{
    [TestMethod]
    public void Flush_PublishesOnlyLatestDownloadProgressPerJob()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var jobId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 10, 100));
        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 20, 100));
        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 30, 100));

        Assert.AreEqual(0, published.Count);

        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("download.progress", published[0].Type);
        var progress = (DownloadProgressEventDto)published[0].Payload;
        Assert.AreEqual(jobId, progress.JobId);
        Assert.AreEqual(30, progress.BytesTransferred);
        Assert.AreEqual(100, progress.TotalBytes);
    }

    [TestMethod]
    public void Flush_BatchesBufferedActivityWithLatestProgress()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var jobId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var status = new DownloadStateChangedEventDto(jobId, workflowId, "Queued");

        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 10, 100));
        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 20, 100));
        coalescer.Publish("download.state-changed", status);

        Assert.AreEqual(0, published.Count);

        coalescer.Flush();

        Assert.AreEqual(2, published.Count);
        Assert.AreEqual("download.progress", published[0].Type);
        Assert.AreEqual(20, ((DownloadProgressEventDto)published[0].Payload).BytesTransferred);
        Assert.AreEqual("download.state-changed", published[1].Type);
        Assert.AreSame(status, published[1].Payload);
    }

    [TestMethod]
    public void Flush_PublishesOnlyLatestJobAndWorkflowUpserts()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        coalescer.Publish("job.upserted", JobSummary(jobId, workflowId, ServerJobLifecycleState.Pending));
        coalescer.Publish("job.upserted", JobSummary(jobId, workflowId, ServerJobLifecycleState.Running));
        coalescer.Publish("workflow.upserted", WorkflowSummary(workflowId, active: 1));
        coalescer.Publish("workflow.upserted", WorkflowSummary(workflowId, active: 0));

        Assert.AreEqual(0, published.Count);

        coalescer.Flush();

        Assert.AreEqual(2, published.Count);
        Assert.AreEqual("job.upserted", published[0].Type);
        Assert.AreEqual(ServerJobLifecycleState.Running, ((JobSummaryDto)published[0].Payload).LifecycleState);
        Assert.AreEqual("workflow.upserted", published[1].Type);
        Assert.AreEqual(0, ((WorkflowSummaryDto)published[1].Payload).ActiveJobCount);
    }

    [TestMethod]
    public void Flush_PublishesStateBeforeBufferedActivity()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var workflowId = Guid.NewGuid();

        var jobId = Guid.NewGuid();

        coalescer.Publish("job.upserted", JobSummary(jobId, workflowId));
        coalescer.Publish("song.searching", new SongSearchingEventDto(
            jobId,
            1,
            workflowId,
            new SongQueryDto("Artist", null, "Title", null, null, false)));

        Assert.AreEqual(0, published.Count);

        coalescer.Flush();

        Assert.AreEqual(2, published.Count);
        Assert.AreEqual("job.upserted", published[0].Type);
        Assert.AreEqual("song.searching", published[1].Type);
    }

    [TestMethod]
    public void Flush_PublishesOnlyLatestActiveJobActivityChange()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        coalescer.Publish("job.activity-changed", new JobActivityChangedEventDto(
            JobSummary(jobId, workflowId, ServerJobLifecycleState.Running) with
            {
                ActivityPhase = ServerJobActivityPhase.WaitingForSearchConcurrency,
            }));
        coalescer.Publish("job.activity-changed", new JobActivityChangedEventDto(
            JobSummary(jobId, workflowId, ServerJobLifecycleState.Running) with
            {
                ActivityPhase = ServerJobActivityPhase.Searching,
            }));

        Assert.AreEqual(0, published.Count);

        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("job.activity-changed", published[0].Type);
        Assert.AreEqual(ServerJobActivityPhase.Searching, ((JobActivityChangedEventDto)published[0].Payload).Summary.ActivityPhase);
    }

    [TestMethod]
    public void Publish_TerminalOrNoneActivityChangeSuppressesPendingActivityChange()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        coalescer.Publish("job.activity-changed", new JobActivityChangedEventDto(
            JobSummary(jobId, workflowId, ServerJobLifecycleState.Running) with
            {
                ActivityPhase = ServerJobActivityPhase.Searching,
            }));
        coalescer.Publish("job.activity-changed", new JobActivityChangedEventDto(
            JobSummary(jobId, workflowId, ServerJobLifecycleState.Terminal) with
            {
                ActivityPhase = ServerJobActivityPhase.None,
                TerminalOutcome = ServerJobTerminalOutcome.Failed,
            }));

        coalescer.Flush();

        Assert.AreEqual(0, published.Count);
    }

    [TestMethod]
    public void Flush_PublishesAllStateBeforeBufferedJobActivity()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();

        coalescer.Publish("job.upserted", JobSummary(jobId, workflowId, ServerJobLifecycleState.Running));
        coalescer.Publish("job.upserted", JobSummary(otherJobId, workflowId, ServerJobLifecycleState.Running));
        coalescer.Publish("song.searching", new SongSearchingEventDto(
            jobId,
            1,
            workflowId,
            new SongQueryDto("Artist", null, "Title", null, null, false)));

        Assert.AreEqual(0, published.Count);

        coalescer.Flush();

        Assert.AreEqual(3, published.Count);
        Assert.AreEqual("job.upserted", published[0].Type);
        Assert.AreEqual("job.upserted", published[1].Type);
        Assert.AreEqual("song.searching", published[2].Type);
        CollectionAssert.Contains(
            published.Take(2).Select(item => ((JobSummaryDto)item.Payload).JobId).ToList(),
            jobId);
        CollectionAssert.Contains(
            published.Take(2).Select(item => ((JobSummaryDto)item.Payload).JobId).ToList(),
            otherJobId);
    }

    [TestMethod]
    public void Flush_CollapsesLargeJobUpsertBurstToWorkflowSnapshot()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1),
            maxJobUpsertsPerWorkflow: 2);
        var workflowId = Guid.NewGuid();

        coalescer.Publish("job.upserted", JobSummary(Guid.NewGuid(), workflowId));
        coalescer.Publish("job.upserted", JobSummary(Guid.NewGuid(), workflowId));
        coalescer.Publish("job.upserted", JobSummary(Guid.NewGuid(), workflowId));
        coalescer.Publish("workflow.upserted", WorkflowSummary(workflowId, active: 3));

        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("workflow.upserted", published[0].Type);
        Assert.AreEqual(workflowId, ((WorkflowSummaryDto)published[0].Payload).WorkflowId);
    }

    [TestMethod]
    public void Flush_SuppressesBulkCancellationJobUpserts()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var workflowId = Guid.NewGuid();

        coalescer.Publish("job.upserted", CancelledJobSummary(Guid.NewGuid(), workflowId, ServerJobCancellationSource.ParentJob));
        coalescer.Publish("job.upserted", CancelledJobSummary(Guid.NewGuid(), workflowId, ServerJobCancellationSource.UserRequestedWorkflow));
        coalescer.Publish("job.upserted", CancelledJobSummary(Guid.NewGuid(), workflowId, ServerJobCancellationSource.UserRequestedAllJobs));
        coalescer.Publish("workflow.upserted", WorkflowSummary(workflowId, active: 0));

        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("workflow.upserted", published[0].Type);
    }

    [TestMethod]
    public void Flush_KeepsDirectCancellationJobUpsert()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            items => published.AddRange(items.Select(item => (item.Type, item.Payload))),
            TimeSpan.FromHours(1));
        var workflowId = Guid.NewGuid();

        coalescer.Publish("job.upserted", CancelledJobSummary(Guid.NewGuid(), workflowId, ServerJobCancellationSource.UserRequestedJob));
        coalescer.Publish("workflow.upserted", WorkflowSummary(workflowId, active: 0));

        coalescer.Flush();

        Assert.AreEqual(2, published.Count);
        Assert.AreEqual("job.upserted", published[0].Type);
        Assert.AreEqual(ServerJobCancellationSource.UserRequestedJob, ((JobSummaryDto)published[0].Payload).CancellationSource);
        Assert.AreEqual("workflow.upserted", published[1].Type);
    }

    private static JobSummaryDto JobSummary(
        Guid jobId,
        Guid workflowId,
        ServerJobLifecycleState lifecycleState = ServerJobLifecycleState.Pending)
        => new(
            jobId,
            1,
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

    private static JobSummaryDto CancelledJobSummary(
        Guid jobId,
        Guid workflowId,
        ServerJobCancellationSource cancellationSource)
        => JobSummary(jobId, workflowId, ServerJobLifecycleState.Terminal) with
        {
            TerminalOutcome = ServerJobTerminalOutcome.Cancelled,
            FailureReason = ServerJobFailureReason.Cancelled,
            CancellationSource = cancellationSource,
        };

    private static WorkflowSummaryDto WorkflowSummary(Guid workflowId, int active)
        => new(workflowId, "workflow", ServerWorkflowState.Active, [], active, 0, 0);
}
