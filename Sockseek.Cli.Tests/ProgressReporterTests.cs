using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;

namespace Tests.ProgressReporterTests;

[TestClass]
public class CliProgressReporterTests
{
    [TestMethod]
    public void EventLogger_HandledActivityTypes_AreUnique()
    {
        Assert.AreEqual(
            EventLogger.HandledEventTypes.Count,
            EventLogger.HandledEventTypes.Distinct(StringComparer.Ordinal).Count());
        CollectionAssert.Contains(EventLogger.HandledEventTypes.ToList(), "job.message");
        CollectionAssert.Contains(EventLogger.HandledEventTypes.ToList(), "diagnostic.error");
    }

    [TestMethod]
    public void TerminalLogMarkup_ColorsStatusWithoutColoringJobDetail()
    {
        var markup = CliLogStyle.FormatMainLogContentMarkup(
            "succeeded: artist - song",
            TerminalLogKind.JobSucceeded,
            "succeeded");

        Assert.AreEqual("[green]succeeded[/]: artist - song", markup);
    }

    [TestMethod]
    public void JobStatusPresenter_UsesSplitTerminalState()
    {
        var status = CliJobStatusPresenter.ForSplit(
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ServerJobTerminalOutcome.Skipped,
            ServerJobSkipReason.AlreadyExists);

        Assert.AreEqual("already exists", status.Label);
        Assert.IsTrue(status.IsSuccessful);
    }

    [TestMethod]
    public void JobStatusPresenter_UsesTransferStateForRunningJobs()
    {
        var status = CliJobStatusPresenter.ForSplit(
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Downloading,
            ServerJobTerminalOutcome.None,
            transferState: "Queued");

        Assert.AreEqual("Queued", status.Label);
        Assert.IsTrue(status.IsActive);
    }

    [TestMethod]
    public void LiveSummaryVisibility_IncludesPendingSearchWaitAndRateLimit()
    {
        var pending = Summary(ServerJobLifecycleState.Pending, ServerJobActivityPhase.None);
        var waiting = Summary(ServerJobLifecycleState.Running, ServerJobActivityPhase.WaitingForSearchConcurrency);
        var limited = Summary(ServerJobLifecycleState.Running, ServerJobActivityPhase.SearchRateLimited);

        Assert.IsTrue(CliProgressReporter.ShouldStartLiveRenderingForSummary(
            pending,
            CliJobStatusPresenter.ForSummary(pending)));
        Assert.IsTrue(CliProgressReporter.ShouldStartLiveRenderingForSummary(
            waiting,
            CliJobStatusPresenter.ForSummary(waiting)));
        Assert.IsTrue(CliProgressReporter.ShouldStartLiveRenderingForSummary(
            limited,
            CliJobStatusPresenter.ForSummary(limited)));
    }

    [TestMethod]
    public void TerminalLiveRenderer_SanitizeLiveText_RemovesBidiControls()
    {
        string sanitized = TerminalLiveRenderer.SanitizeLiveText("safe\u202Eevil\u2066text");

        Assert.AreEqual("safeeviltext", sanitized);
    }

    [TestMethod]
    public void RenderProjection_CountsAreOrderIndependentAndExcludeInlineAlbumChildren()
    {
        var workflowId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var album = Job(
            albumId,
            1,
            workflowId,
            ServerJobKind.Album,
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Downloading);
        var child = Job(
            Guid.NewGuid(),
            2,
            workflowId,
            ServerJobKind.Song,
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Downloading,
            parentJobId: albumId);
        var completedSearch = Job(
            Guid.NewGuid(),
            3,
            workflowId,
            ServerJobKind.Search,
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ServerJobTerminalOutcome.Succeeded);
        var state = new DaemonClientStateView(
            null,
            [],
            [child, completedSearch, album],
            [],
            [], null);

        var projection = CliProgressReporter.ProjectState(state);
        var counts = TerminalLiveRenderer.CountRenderState(projection);

        Assert.AreEqual(1, counts.Active);
        Assert.AreEqual(0, counts.Queued);
        Assert.AreEqual(1, counts.Completed);
        Assert.AreEqual(0, counts.Failed);
        Assert.AreEqual(2, projection.JobRecords.Count);
        Assert.AreEqual(1, projection.JobViews.Count);
        Assert.AreEqual(albumId.ToString(), projection.JobViews.Single().Id);
        Assert.AreEqual(child.JobId.ToString(), projection.JobViews.Single().Children.Single().Id);
    }

    [TestMethod]
    public void TerminalLiveRenderer_IdleDependsOnlyOnProjectedActiveAndQueuedWork()
    {
        Assert.IsTrue(TerminalLiveRenderer.IsIdle(
            new TerminalJobCounts(Active: 0, Queued: 0, Completed: 4, Failed: 2)));
        Assert.IsFalse(TerminalLiveRenderer.IsIdle(
            new TerminalJobCounts(Active: 1, Queued: 0, Completed: 0, Failed: 0)));
        Assert.IsFalse(TerminalLiveRenderer.IsIdle(
            new TerminalJobCounts(Active: 0, Queued: 1, Completed: 0, Failed: 0)));
    }

    [TestMethod]
    public void AlbumSearchProgress_DoesNotTreatZeroChildrenAsChildProgress()
    {
        var searching = new JobView(
            Guid.NewGuid().ToString(),
            1,
            "Album",
            "Artist - Album",
            "searching",
            DoneChildren: 0,
            TotalChildren: 0,
            DiscoveryRawResultCount: 42);

        Assert.AreEqual(
            " (42)",
            TerminalLiveRenderer.JobProgressAnnotation(searching));
        Assert.AreEqual(
            "",
            TerminalLiveRenderer.JobProgressAnnotation(searching with
            {
                State = "waiting search",
                DiscoveryRawResultCount = null,
            }));
        Assert.AreEqual(
            " [1/2]",
            TerminalLiveRenderer.JobProgressAnnotation(searching with
            {
                State = "downloading tracks",
                DoneChildren = 1,
                TotalChildren = 2,
            }));
    }

    [TestMethod]
    public void RecoverySnapshot_RemovesMissingActiveRowsAndCounts()
    {
        var workflowId = Guid.NewGuid();
        var job = Job(
            Guid.NewGuid(),
            1,
            workflowId,
            ServerJobKind.Search,
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Searching);
        var store = new DaemonClientStore();
        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        var initial = store.ApplySnapshot(Snapshot(Guid.NewGuid(), 2, [job]));

        var before = reporter.Reconcile(store, initial);
        var recovered = store.ApplySnapshot(Snapshot(Guid.NewGuid(), 0, []));
        var after = reporter.Reconcile(store, recovered);

        Assert.AreEqual(1, TerminalLiveRenderer.CountRenderState(before).Active);
        Assert.AreEqual(0, TerminalLiveRenderer.CountRenderState(after).Active);
        Assert.AreEqual(0, after.JobRecords.Count);
        Assert.AreEqual(0, after.JobViews.Count);
        Assert.AreEqual(0, after.RetiredCompleted);
        Assert.AreEqual(0, after.RetiredFailed);
    }

    [TestMethod]
    public void DaemonTerminalRemoval_PreservesObservedSessionFailureWithoutStaleLiveRow()
    {
        var workflowId = Guid.NewGuid();
        var job = Job(
            Guid.NewGuid(),
            1,
            workflowId,
            ServerJobKind.Search,
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Searching);
        var store = new DaemonClientStore();
        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        var epoch = Guid.NewGuid();
        reporter.Reconcile(store, store.ApplySnapshot(Snapshot(epoch, 0, [job])));

        var state = JobStateDto.FromSummary(job, revision: 1);
        var terminalLifecycle = state.Lifecycle with
        {
            LifecycleState = ServerJobLifecycleState.Terminal,
            ActivityPhase = ServerJobActivityPhase.None,
            TerminalOutcome = ServerJobTerminalOutcome.Cancelled,
            CancellationSource = ServerJobCancellationSource.UserRequestedJob,
        };
        var update = store.Apply(new StateUpdateBatchDto(
            StateStreamScopeDto.Daemon,
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                Jobs =
                [
                    new JobDeltaDto(
                        job.JobId,
                        Revision: 2,
                        Lifecycle: terminalLifecycle),
                ],
                RemovedJobIds = [job.JobId],
            },
            []));

        var projection = reporter.Reconcile(store, update);
        var counts = TerminalLiveRenderer.CountRenderState(projection);

        Assert.AreEqual(0, counts.Active);
        Assert.AreEqual(1, counts.Failed);
        Assert.AreEqual(0, projection.JobRecords.Count);
        Assert.AreEqual(0, projection.JobViews.Count);
        Assert.AreEqual(0, store.GetLiveStateView().Jobs.Count);
    }

    private static JobSummaryDto Summary(
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity)
        => new(
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            ServerJobKind.Search,
            lifecycle,
            activity,
            null,
            ServerJobTerminalOutcome.None,
            ServerJobSkipReason.None,
            "item",
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

    private static JobSummaryDto Job(
        Guid jobId,
        int displayId,
        Guid workflowId,
        ServerJobKind kind,
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity,
        ServerJobTerminalOutcome outcome = ServerJobTerminalOutcome.None,
        Guid? parentJobId = null)
        => new(
            jobId,
            displayId,
            workflowId,
            kind,
            lifecycle,
            activity,
            null,
            outcome,
            ServerJobSkipReason.None,
            "item",
            "query",
            null,
            null,
            parentJobId,
            null,
            null,
            null,
            null,
            [],
            []);

    private static StateSnapshotDto Snapshot(
        Guid epoch,
        long sequence,
        IReadOnlyList<JobSummaryDto> jobs)
        => new(
            StateStreamScopeDto.Daemon,
            new StateStreamPositionDto(epoch, sequence),
            DateTimeOffset.UtcNow,
            null,
            [],
            jobs.Select(job => JobStateDto.FromSummary(job, revision: 1)).ToList(),
            [],
            []);
}
