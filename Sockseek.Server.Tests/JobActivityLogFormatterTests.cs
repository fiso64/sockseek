using Microsoft.Extensions.Logging;
using Sockseek.Api;

namespace Tests;

[TestClass]
public class JobActivityLogFormatterTests
{
    [TestMethod]
    public void TerminalState_IsFormattedWithoutActivity_AndDeduplicated()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(
            JobLifecycleState.Terminal,
            JobTerminalOutcome.Succeeded);

        var first = formatter.Format(summary);
        var duplicate = formatter.Format(summary);

        Assert.IsNotNull(first);
        StringAssert.Contains(first.Message, "succeeded");
        Assert.AreEqual(ActivityLogDisplayKind.Succeeded, first.Display?.Kind);
        Assert.AreEqual("succeeded", first.Display?.Highlight);
        Assert.IsNull(duplicate);
    }

    [TestMethod]
    [DataRow(JobTerminalOutcome.Succeeded, JobSkipReason.None)]
    [DataRow(JobTerminalOutcome.Skipped, JobSkipReason.None)]
    [DataRow(JobTerminalOutcome.Skipped, JobSkipReason.AlreadyExists)]
    [DataRow(JobTerminalOutcome.Skipped, JobSkipReason.NotFoundLastTime)]
    [DataRow(JobTerminalOutcome.Cancelled, JobSkipReason.None)]
    [DataRow(JobTerminalOutcome.PartialSuccess, JobSkipReason.None)]
    [DataRow(JobTerminalOutcome.Failed, JobSkipReason.None)]
    public void TerminalState_HighlightsOnlyStatusPrefix(
        JobTerminalOutcome outcome,
        JobSkipReason skipReason)
    {
        var entry = new JobActivityLogFormatter().Format(Summary(
            JobLifecycleState.Terminal,
            outcome,
            skipReason: skipReason));

        Assert.IsNotNull(entry?.Display);
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Display.Highlight));
        StringAssert.StartsWith(
            entry.Display.Message,
            $"{entry.Display.Highlight}: ");
        Assert.IsTrue(entry.Display.Message.Length > entry.Display.Highlight.Length + 2);
    }

    [TestMethod]
    public void CompactJobMessage_UsesCurrentStoreRow()
    {
        var summary = Summary();
        var store = StoreWith(summary);
        var formatter = new JobActivityLogFormatter();
        var activity = Activity(
            summary,
            "job.message",
            new JobMessageActivityDto(summary.Display.DisplayId, "Warning", "resolver", "candidate rejected"));

        var entry = formatter.Format(activity, store);

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Warning, entry.Level);
        StringAssert.Contains(entry.Message, "resolver: candidate rejected");
        Assert.AreEqual(summary.Display.DisplayId, entry.Display?.DisplayId);
    }

    [TestMethod]
    public void CompactWorkflowMessage_DoesNotRequireJobState()
    {
        var workflowId = Guid.NewGuid();
        var formatter = new JobActivityLogFormatter();
        var activity = new ActivityEventDto(
            1,
            DateTimeOffset.UtcNow,
            "workflow.message",
            workflowId,
            null,
            null,
            new WorkflowMessageActivityDto("Information", null, "profiles active"));

        var entry = formatter.Format(activity, new DaemonClientStore());

        Assert.IsNotNull(entry);
        StringAssert.Contains(entry.Message, "profiles active");
    }

    [TestMethod]
    public void CompactDiagnostic_PreservesExceptionDetail()
    {
        var summary = Summary();
        var formatter = new JobActivityLogFormatter();
        var activity = Activity(
            summary,
            "diagnostic.error",
            new DiagnosticActivityDto(
                summary.Display.DisplayId,
                "job",
                "failed",
                "System.InvalidOperationException",
                "System.InvalidOperationException: boom\nat Test()",
                "engine"));

        var entry = formatter.Format(activity, StoreWith(summary));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        StringAssert.Contains(entry.Message, "InvalidOperationException");
        StringAssert.Contains(entry.Message, "at Test()");
    }

    [TestMethod]
    public void AlbumChildTerminalState_UsesAlbumFileDisplay()
    {
        var workflowId = Guid.NewGuid();
        var album = Summary(
            jobId: Guid.NewGuid(),
            workflowId: workflowId,
            kind: ServerJobKind.Album);
        var child = Summary(
            jobId: Guid.NewGuid(),
            workflowId: workflowId,
            kind: ServerJobKind.Song,
            parentJobId: album.JobId,
            lifecycle: JobLifecycleState.Terminal,
            outcome: JobTerminalOutcome.Failed);
        var formatter = new JobActivityLogFormatter();

        formatter.Format(album);
        var entry = formatter.Format(child);

        Assert.IsNotNull(entry);
        Assert.AreEqual(JobActivityLogFormatter.AlbumFileJobType, entry.Display?.JobType);
        Assert.AreEqual(ActivityLogDisplayKind.AlbumTrackFailed, entry.Display?.Kind);
    }

    private static DaemonClientStore StoreWith(JobSummaryDto summary)
    {
        var store = new DaemonClientStore();
        store.ApplySnapshot(new StateSnapshotDto(
            StateStreamScopeDto.Workflow(summary.Display.WorkflowId),
            new StateStreamPositionDto(Guid.NewGuid(), 0),
            DateTimeOffset.UtcNow,
            null,
            [],
            [summary with { Revision = 1 }],
            [],
            []));
        return store;
    }

    private static ActivityEventDto Activity(
        JobSummaryDto summary,
        string type,
        ActivityPayloadDto payload)
        => new(
            1,
            DateTimeOffset.UtcNow,
            type,
            summary.Display.WorkflowId,
            summary.JobId,
            null,
            payload);

    private static JobSummaryDto Summary(
        JobLifecycleState lifecycle = JobLifecycleState.Running,
        JobTerminalOutcome outcome = JobTerminalOutcome.None,
        Guid? jobId = null,
        Guid? workflowId = null,
        ServerJobKind kind = ServerJobKind.Search,
        Guid? parentJobId = null,
        JobSkipReason skipReason = JobSkipReason.None)
        => new(jobId ?? Guid.NewGuid(),
            0,
            new(7, workflowId ?? Guid.NewGuid(), kind, "item", "Artist - Title", [], ServerPrintOption.None, null, ServerJobRole.UserRoot, null),
            new(lifecycle, lifecycle == JobLifecycleState.Running
                ? JobActivityPhase.Searching
                : JobActivityPhase.None, null, outcome, skipReason, outcome == JobTerminalOutcome.Failed
                ? JobFailureReason.AllDownloadsFailed
                : null, outcome == JobTerminalOutcome.Failed ? "boom" : null, false, JobCancellationSource.None, []),
            new(null, null),
            new(parentJobId, null, null));
}
