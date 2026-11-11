using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Api;
using Sockseek.Server;
using Soulseek;

namespace Tests.Server;

[TestClass]
public class EngineStateStoreTests
{
    [TestMethod]
    public void SubmissionDeletionInvalidatesReadersAndRemovesLiveMembers()
    {
        var store = new EngineStateStore();
        int invalidations = 0;
        store.JobListChanged += () => invalidations++;
        store.RemoveSubmission(Guid.NewGuid());
        Assert.AreEqual(1, invalidations);
        var job = new SearchJob("Source") { SubmissionId = Guid.NewGuid() };
        Register(store, job);
        Assert.IsTrue(store.IsJobAvailable(job.Id));
        store.RemoveSubmission(job.SubmissionId.Value);
        Assert.IsFalse(store.IsJobAvailable(job.Id));
        Assert.IsFalse(store.IsJobAvailable(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task StandalonePublicationDeliversBurstsAndFlushesBeforeDisposal()
    {
        var store = new EngineStateStore();
        var clock = new PublicationClock();
        var batches = new List<StateUpdate>();
        int flushes = 0;
        store.StateBatchPublished += batches.Add;
        store.StateUpdatesFlushed += () => flushes++;
        await using var publication = store.StartStatePublication(clock);
        var root = new JobList("Standalone scope");
        Register(store, root, flush: false);
        var child = new SongJob(new SongQuery { Title = "Pending member" }) { WorkflowId = root.WorkflowId };
        root.Add(child);
        Register(store, child, root, flush: false);
        Assert.AreEqual(0, batches.Count);
        Assert.AreEqual(1, clock.Arms, "A continuing burst must not keep postponing publication.");
        clock.Fire();
        Assert.IsTrue(batches.Any(batch => batch.State.Jobs.Any(row => row.JobId == child.Id)));
        batches.Clear();
        int priorArms = clock.Arms, priorFlushes = flushes;
        store.UpdateUploadTransfer(new UploadTransferSnapshot(Guid.NewGuid(), 1, "Peer", @"Public\queued.bin",
            100, DateTimeOffset.UnixEpoch, UploadTransferState.Queued, UploadFailureReason.None,
            UploadCancellationSource.None, 0, 0, null, null, null));
        Assert.AreEqual(0, batches.Count, "Queued uploads have no daemon packet to trigger delivery.");
        Assert.AreEqual(priorArms + 1, clock.Arms);
        clock.Fire();
        Assert.AreEqual(priorFlushes + 1, flushes, "Typed facts must wake scoped readers without a daemon packet.");
        var last = new SongJob(new SongQuery { Title = "Final member" }) { WorkflowId = root.WorkflowId };
        root.Add(last);
        Register(store, last, root, flush: false);
        Assert.AreEqual(0, batches.Count);
        await publication.DisposeAsync();
        Assert.IsTrue(batches.Any(batch => batch.State.Jobs.Any(row => row.JobId == last.Id)));
        int delivered = batches.Count;
        clock.Fire();
        Assert.AreEqual(delivered, batches.Count);
    }

    private sealed class PublicationClock : TimeProvider
    {
        private ControlledTimer? timer;
        public int Arms => timer!.Arms;
        public void Fire() => timer!.Fire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => timer = new ControlledTimer(callback, state);
        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool pending;
            public int Arms { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                pending = dueTime != Timeout.InfiniteTimeSpan;
                if (pending) Arms++;
                return true;
            }
            public void Fire() { if (pending) { pending = false; callback(state); } }
            public void Dispose() => pending = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [TestMethod]
    public async Task DisplayedJobListTracksItsWindowAndKeepsTerminalRowsAcrossRetirement()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new ServerOptions { Persistence = new() { Enabled = false } });
        using var supervisor = new EngineSupervisor(options);
        var store = supervisor.StateStore;
        await using var events = new ServerEventBroadcaster(store, supervisor, new NoOpHubContext<ServerEventHub>());
        var queries = new Sockseek.Server.Persistence.JobReadService(store, supervisor,
            new Sockseek.Server.Persistence.PersistenceCoordinator(options));
        var roots = Enumerable.Range(0, 4).Select(i => new SearchJob("Root " + i)
            { SubmissionId = Guid.NewGuid() }).ToArray();
        foreach (var root in roots.Take(3)) Register(store, root);
        var query = new JobQuery(null, null, null, null, IncludeAll: false, NewestFirst: true,
            ExcludeKinds: [ServerJobKind.RetrieveFolder, ServerJobKind.Song]);
        Register(store, new SongJob(new SongQuery { Title = "Hidden download" }));
        Register(store, new RetrieveFolderJob(new PeerDirectoryIdentity("Peer", @"Public\Album")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var updates = queries.ObserveJobListAsync(new(query, 2), events, deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());
        CollectionAssert.AreEqual(new[] { roots[2].Id, roots[1].Id }, updates.Current.WindowJobIds!.ToArray());
        Assert.IsNotNull(updates.Current.NextCursor);

        roots[2].SetAwaitingSelection();
        UpdateState(store, roots[2]);
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.IsNull(updates.Current.WindowJobIds);
        var patch = updates.Current.Jobs.Single();
        Assert.AreEqual(roots[2].Id, patch.JobId);
        Assert.IsNull(patch.Added, "Existing rows receive compact changes, not another page.");
        Assert.IsNotNull(patch.Lifecycle);

        Register(store, roots[3]);
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(roots[3].Id, updates.Current.Jobs.Single().Added!.JobId);
        CollectionAssert.AreEqual(new[] { roots[1].Id }, updates.Current.RemovedJobIds.ToArray());
        roots[3].SetDone();
        UpdateState(store, roots[3]);
        store.RetireJobProjections(roots[3].WorkflowId, [roots[3].Id]);
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(ServerJobTerminalOutcome.Succeeded, updates.Current.Jobs.Single().Lifecycle!.TerminalOutcome);
        Assert.AreEqual(0, updates.Current.RemovedJobIds.Count, "Storage retirement cannot remove the displayed result.");

        store.RemoveSubmission(roots[3].SubmissionId!.Value);
        Assert.IsTrue(await updates.MoveNextAsync());
        CollectionAssert.Contains(updates.Current.RemovedJobIds.ToArray(), roots[3].Id);
        CollectionAssert.Contains(updates.Current.Jobs.Select(row => row.JobId).ToArray(), roots[1].Id);

        var expanded = await queries.GetJobListSnapshotAsync(new(query, 3), events, deadline.Token);
        CollectionAssert.AreEqual(new[] { roots[2].Id, roots[1].Id, roots[0].Id }, expanded.WindowJobIds!.ToArray());
        Assert.IsNull(expanded.NextCursor);
        Assert.IsTrue(expanded.Jobs.All(row => row.Added is not null && row.Revision == 0),
            "A fresh scope replaces earlier rows even when their previous revision was higher.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisplayedJobKeepsFinalMembershipWhenRegistrationAndRetirementPrecedeItsNextRead(bool childRetiresFirst)
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new ServerOptions { Persistence = new() { Enabled = false } });
        using var supervisor = new EngineSupervisor(options);
        var store = supervisor.StateStore;
        await using var events = new ServerEventBroadcaster(store, supervisor, new NoOpHubContext<ServerEventHub>());
        var queries = new Sockseek.Server.Persistence.JobReadService(store, supervisor,
            new Sockseek.Server.Persistence.PersistenceCoordinator(options));
        var root = new JobList("Visible list");
        Register(store, root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var updates = queries.ObserveJobsAsync(root.Id, [], events, deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(0, updates.Current.ChildCount);
        var folder = new RetrieveFolderJob(new("Peer", "Exact\\Folder")) { WorkflowId = root.WorkflowId };
        root.Add(folder);
        Register(store, folder, root);
        UpdateStructure(store, root);
        root.SetDone();
        UpdateState(store, root);
        typeof(EngineStateStore).GetMethod("RetireJobProjections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [root.WorkflowId, childRetiresFirst ? new Guid[] { folder.Id, root.Id } : new Guid[] { root.Id, folder.Id }]);
        store.FlushStateUpdates();
        Assert.IsNull(store.GetJobSummary(root.Id));
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(1, updates.Current.ChildCount, "Runtime eviction must not reset the final displayed membership.");
        Assert.AreEqual(1, updates.Current.RetrievalChildCount);
    }

    [TestMethod]
    public async Task OverviewAndDisplayedJobScopePreserveMembershipActionsAndTerminalHandoff()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new ServerOptions { Persistence = new() { Enabled = false } });
        using var supervisor = new EngineSupervisor(options);
        var store = supervisor.StateStore;
        await using var events = new ServerEventBroadcaster(store, supervisor, new NoOpHubContext<ServerEventHub>());
        var queries = new Sockseek.Server.Persistence.JobReadService(store, supervisor,
            new Sockseek.Server.Persistence.PersistenceCoordinator(options));
        var root = new JobList("Visible list");
        var child = new SongJob(new SongQuery { Title = "Visible member" }) { WorkflowId = root.WorkflowId };
        root.Add(child);
        using var childCancellation = new CancellationTokenSource();
        typeof(Job).GetProperty(nameof(Job.Cts))!.SetValue(child, childCancellation);
        Register(store, root);
        Register(store, child, root);
        Assert.AreEqual(2, store.GetDaemonSnapshot().Jobs.Count);
        var overview = store.GetDaemonSnapshot(DaemonStateDetail.Overview);
        Assert.AreEqual(root.Id, overview.Jobs.Single().JobId);
        Assert.AreEqual(0, overview.Transfers.Count);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var updates = queries.ObserveJobsAsync(root.Id, [child.Id], events, deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(1, updates.Current.ChildCount);
        Assert.AreEqual(0, updates.Current.RetrievalChildCount);
        CollectionAssert.AreEquivalent(new[] { root.Id, child.Id }, updates.Current.State.Jobs.Select(row => row.JobId).ToArray());
        var first = updates.Current.State.Jobs.Single(row => row.JobId == child.Id).Added!;
        Assert.AreEqual(store.GetJobSummary(child.Id)!.AvailableActions.Single(), first.Lifecycle.AvailableActions.Single());
        var folder = new RetrieveFolderJob(new("Peer", "Exact\\Folder")) { WorkflowId = root.WorkflowId };
        root.Add(folder);
        Register(store, folder, root);
        Register(store, new SongJob(new SongQuery { Title = "Unrelated workflow" }));
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(2, updates.Current.ChildCount);
        Assert.AreEqual(1, updates.Current.RetrievalChildCount);
        Assert.IsFalse(updates.Current.State.Jobs.Any(row => row.JobId == folder.Id),
            "Undisplayed children invalidate membership without sending their rows.");
        var otherParent = new JobList("Other branch") { WorkflowId = root.WorkflowId };
        Register(store, otherParent);
        Register(store, folder, otherParent);
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(1, updates.Current.ChildCount, "Reparenting updates the former parent's direct count too.");
        Assert.AreEqual(0, updates.Current.RetrievalChildCount);
        child.SetDone();
        UpdateState(store, child);
        typeof(EngineStateStore).GetMethod("RetireJobProjections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [root.WorkflowId, new Guid[] { child.Id }]);
        bool finalSeen = false;
        while (!finalSeen && await updates.MoveNextAsync())
        {
            var final = updates.Current.State.Jobs.SingleOrDefault(row => row.JobId == child.Id);
            finalSeen = final?.Lifecycle?.TerminalOutcome == ServerJobTerminalOutcome.Succeeded
                || final?.Added?.Lifecycle.TerminalOutcome == ServerJobTerminalOutcome.Succeeded;
        }
        Assert.IsTrue(finalSeen, "A final scoped row must survive immediate runtime eviction.");
        Assert.IsNull(store.GetJobSummary(child.Id));
        Assert.AreEqual(ServerJobLifecycleState.Pending, first.Lifecycle.LifecycleState,
            "Earlier reads remain immutable while the current fact becomes terminal.");
        var waiting = updates.MoveNextAsync().AsTask();
        deadline.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);
    }

    [TestMethod]
    public async Task JobTransferObservationSeedsOnlyRequestedJobsAndTracksProgressEvictionAndCancellation()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new ServerOptions { Persistence = new() { Enabled = false } });
        using var supervisor = new EngineSupervisor(options);
        var store = supervisor.StateStore;
        await using var events = new ServerEventBroadcaster(store, supervisor, new NoOpHubContext<ServerEventHub>());
        var queries = new Sockseek.Server.Persistence.JobReadService(store, supervisor,
            new Sockseek.Server.Persistence.PersistenceCoordinator(options));
        var first = new SongJob(new SongQuery { Title = "First" }) { FileSize = 100 };
        var other = new SongJob(new SongQuery { Title = "Other" }) { FileSize = 100 };
        var transfers = new[] { Transfer(first, TransferStates.InProgress), Transfer(other, TransferStates.InProgress) };
        void Publish(SongJob song, TransferSnapshot transfer)
            => InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
                1, DateTimeOffset.UnixEpoch, Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song), transfer));
        Publish(first, transfers[0]);
        Publish(other, transfers[1]);
        using var cancellation = new CancellationTokenSource();
        await using var updates = queries.ObserveJobsAsync(first.Id, [first.Id], events, cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(transfers[0].Id, updates.Current.State.Transfers.Single().Added!.TransferId);
        var next = updates.MoveNextAsync().AsTask();
        Publish(other, transfers[1] with { Revision = 2, BytesTransferred = 25 });
        Publish(first, transfers[0] with { Revision = 2, BytesTransferred = 50 });
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        var progress = updates.Current.State.Transfers.Single();
        Assert.AreEqual(transfers[0].Id, progress.TransferId);
        Assert.IsNull(progress.Added);
        Assert.AreEqual(50L, progress.Progress!.BytesTransferred);
        Publish(first, transfers[0] with { Revision = 3, BytesTransferred = 100,
            TerminalOutcome = TransferSnapshotTerminalOutcome.Succeeded });
        store.RemoveTerminalTransfer(transfers[0].Id);
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(transfers[0].Id, updates.Current.State.RemovedTransferIds.Single());
        Assert.AreEqual(0, store.GetJobTransferSnapshot(new HashSet<Guid> { first.Id }).Count);
        var waiting = updates.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void InvalidUploadSpeedDoesNotPoisonCapturedFactsOrAnotherTransfer(double invalidSpeed)
    {
        var store = new EngineStateStore();
        var queued = new UploadTransferSnapshot(Guid.NewGuid(), 1, "Peer", "Folder\\File.bin", 100,
            DateTimeOffset.UnixEpoch, UploadTransferState.Queued, UploadFailureReason.None,
            UploadCancellationSource.None, 0, 0, null, null, null);
        store.UpdateUploadTransfer(queued);
        var earlier = store.GetLiveTransfer(queued.TransferId)!;
        var running = queued with { Revision = 2, State = UploadTransferState.InProgress, BytesTransferred = 10,
            Attempt = new(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, 10, invalidSpeed), BytesPerSecond = invalidSpeed };
        Assert.ThrowsExactly<OverflowException>(() => store.UpdateUploadTransfer(running));
        Assert.AreEqual(1L, store.GetLiveTransfer(queued.TransferId)!.Revision);
        var other = queued with { TransferId = Guid.NewGuid(), RemotePath = "Folder\\Other.bin" };
        store.UpdateUploadTransfer(other);
        Assert.AreEqual("Folder\\Other.bin", store.GetLiveTransfer(other.TransferId)!.Identity.RemotePath);
        store.UpdateUploadTransfer(running with { BytesPerSecond = 12.9,
            Attempt = running.Attempt! with { BytesPerSecond = 12.9 } });
        var current = store.GetLiveTransfer(queued.TransferId)!;
        Assert.AreEqual(2L, current.Revision);
        Assert.AreEqual(12L, current.Progress.BytesPerSecond);
        Assert.AreEqual("Queued", earlier.Status.State);
    }

    [TestMethod]
    public void TransferTotalsTrackProgressUploadsAndWorkflowVisibilityWithoutDependingOnViewedRows()
    {
        var state = new TransferState();
        Guid workflow = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        var download = new TransferSnapshot(Guid.NewGuid(), TransferSnapshotDirection.Download,
            TransferSnapshotSource.SoulseekPeer, job, workflow, 1, "Peer", "Track.flac", null, null,
            "InProgress", 0, 100, 1, null, BytesPerSecond: 10);
        var upload = new UploadTransferSnapshot(Guid.NewGuid(), 1, "Peer", "Track.flac", 100,
            DateTimeOffset.UnixEpoch, UploadTransferState.Queued, UploadFailureReason.None,
            UploadCancellationSource.None, 0, 0, null, null, null);
        state.ApplyDownload(download, terminal: false);
        state.ApplyUpload(upload);
        Assert.AreEqual(LiveTransferSummaryDto.Empty, state.Summary);
        state.SetWorkflowVisible(workflow, true);
        Assert.AreEqual(new LiveTransferSummaryDto(1, 1, 0, 0, 0, 10, 0), state.Summary);
        LiveTransferSummaryDto earlier = state.Summary;

        upload = upload with { Revision = 2, State = UploadTransferState.InProgress, BytesPerSecond = 7,
            Attempt = new(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, 0, 7) };
        state.ApplyUpload(upload);
        download = download with { Revision = 2, BytesTransferred = 50, BytesPerSecond = 30 };
        state.ApplyDownload(download, terminal: false);
        Assert.AreEqual(download.Id, state.ForJobs(new HashSet<Guid> { job }).Single().TransferId);
        Assert.AreEqual(0, state.ForJobs(new HashSet<Guid> { Guid.NewGuid() }).Count);
        Assert.AreEqual(new LiveTransferSummaryDto(2, 1, 1, 0, 0, 30, 7), state.Summary);
        Assert.AreEqual(10, earlier.DownloadRate, "Previously published totals must not mutate.");

        state.SetWorkflowVisible(workflow, false);
        Assert.AreEqual(new LiveTransferSummaryDto(1, 0, 1, 0, 0, 0, 7), state.Summary);
        Assert.AreEqual(2, state.Active.Count, "Visibility must not retire the underlying transfer facts.");
        state.SetWorkflowVisible(workflow, true);
        download = download with { Revision = 3, State = "Queued, Remotely", BytesPerSecond = 0 };
        state.ApplyDownload(download, terminal: false);
        Assert.AreEqual(new LiveTransferSummaryDto(2, 1, 1, 1, 0, 0, 7), state.Summary);
        Assert.AreEqual("Queued, Remotely", state.Active[download.Id].State);
        download = download with { Revision = 4,
            State = "Failed", TerminalOutcome = TransferSnapshotTerminalOutcome.Failed };
        state.ApplyDownload(download, terminal: false);
        Assert.AreEqual(new LiveTransferSummaryDto(1, 0, 1, 0, 1, 0, 7), state.Summary);
        state.SetWorkflowVisible(workflow, false);
        Assert.AreEqual(0, state.Summary.Failed);
        state.SetWorkflowVisible(workflow, true);
        Assert.AreEqual(1, state.Summary.Failed);
        state.Remove(download.Id);
        state.Remove(upload.TransferId);
        Assert.AreEqual(LiveTransferSummaryDto.Empty, state.Summary);
        Assert.AreEqual(0, state.ForJobs(new HashSet<Guid> { job }).Count);
    }

    [TestMethod]
    public void TerminalTransferCommitRevision_ReplicatesAndSurvivesDaemonRuntimeUpdates()
    {
        var store = new EngineStateStore();
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += batch => batches.Add(batch.ToDto());

        var job = new SongJob(new SongQuery { Title = "Retained history boundary" });
        var empty = store.GetDaemonSnapshot().JobHistoryPosition!;
        Register(store, job);
        var jobs = store.GetDaemonSnapshot().JobHistoryPosition!;
        Assert.IsTrue(jobs.Sequence > empty.Sequence);
        batches.Clear();
        store.NotifyTerminalTransferCommitted();
        store.FlushStateUpdates();
        var committed = store.GetDaemonSnapshot();
        Assert.AreEqual(jobs, committed.JobHistoryPosition,
            "A transfer commit must not make an already-current Jobs read stale.");
        Assert.IsTrue(committed.Position.Sequence > jobs.Sequence);
        Assert.AreEqual(1, store.GetDaemonSnapshot().Daemon!.TerminalTransferCommitRevision);
        Assert.AreEqual(1, batches.Single().State.Daemon!.TerminalTransferCommitRevision);
        Assert.AreEqual(StateStreamScopeKind.Daemon, batches.Single().Scope.Kind);

        DaemonStateDto current = store.GetDaemonSnapshot().Daemon!;
        store.UpdateDaemonRuntime(current.SoulseekClient, current.RestartCount + 1);
        Assert.AreEqual(1, store.GetDaemonSnapshot().Daemon!.TerminalTransferCommitRevision);
        Assert.AreEqual(jobs, store.GetDaemonSnapshot().JobHistoryPosition);
        job.SetDone();
        UpdateState(store, job);
        Assert.IsTrue(store.GetDaemonSnapshot().JobHistoryPosition!.Sequence > jobs.Sequence,
            "Real job changes must still invalidate reads captured before them.");
    }

    [TestMethod]
    public void LiveJobDisplay_UpdatesSubmissionOwnershipAndPreparedSearchPresentation()
    {
        var store = new EngineStateStore();
        var job = new SongJob(new SongQuery { Title = "External submission" });
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += batch => batches.Add(batch.ToDto());
        Register(store, job);

        Guid submissionId = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        job.SubmissionId = submissionId;
        job.SemanticRole = JobSemanticRole.UserRoot;
        job.CreatedAtUtc = createdAt;
        UpdateState(store, job);

        var changed = batches.SelectMany(batch => batch.State.Jobs)
            .Last(delta => delta.JobId == job.Id && delta.Display != null);
        Assert.AreEqual(submissionId, changed.Display!.SubmissionId);
        Assert.AreEqual(ServerJobRole.UserRoot, changed.Display.Role);
        Assert.AreEqual(createdAt, changed.Display.CreatedAtUtc);
        var state = store.GetWorkflowSnapshot(job.WorkflowId).Jobs.Single(row => row.JobId == job.Id);
        Assert.AreEqual(submissionId, state.ToSummary().SubmissionId);
        Assert.AreEqual(ServerJobRole.UserRoot, state.ToSummary().Role);
        Assert.AreEqual(createdAt, state.ToSummary().CreatedAtUtc);

        var search = new SearchJob(new SongQuery { Title = "Track search" })
            { WorkflowId = job.WorkflowId, GroupResults = true };
        Register(store, search);
        Assert.IsNull(store.GetWorkflowSnapshot(job.WorkflowId).Jobs
            .Single(row => row.JobId == search.Id).Display.SearchDefaultProjection);
        Sockseek.Core.Services.JobPreparer.ResolveSemanticSettings(search,
            new Sockseek.Core.Settings.DownloadSettings(),
            Sockseek.Core.Settings.DefaultJobSettingsResolver.Instance,
            Sockseek.Core.Settings.JobSettingsInheritance.None);
        UpdateState(store, search);
        var searchDisplay = batches.SelectMany(batch => batch.State.Jobs)
            .Last(delta => delta.JobId == search.Id && delta.Display is not null).Display!;
        Assert.AreEqual(ServerSearchDefaultProjectionKind.Track, searchDisplay.SearchDefaultProjection);
        Assert.IsTrue(searchDisplay.SearchGroupResults);
        Assert.AreEqual(searchDisplay, store.GetWorkflowSnapshot(job.WorkflowId).Jobs
            .Single(row => row.JobId == search.Id).Display);
    }

    [TestMethod]
    public void SnapshotAndEverySubsequentDelta_MatchCurrentServerProjection()
    {
        var server = new EngineStateStore();
        var client = new DaemonClientStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var scope = StateStreamScopeDto.Workflow(song.WorkflowId);
        server.ReserveWorkflowStream(song.WorkflowId);
        client.ApplySnapshot(server.GetWorkflowSnapshot(song.WorkflowId));
        server.StateBatchPublished += source =>
        {
            var batch = source.ToDto();
            if (batch.Scope == scope)
                Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(batch).Status);
        };

        Register(server, song);
        AssertClientMatchesProjection(server, client, song.WorkflowId);

        song.UpdateActivity(JobActivityPhase.Downloading);
        UpdateState(server, song);
        AssertClientMatchesProjection(server, client, song.WorkflowId);

        DownloadStateChanged(server, song, TransferStates.InProgress);
        AssertClientMatchesProjection(server, client, song.WorkflowId);

        song.SetDone("C:/music/track.mp3");
        UpdateState(server, song);
        AssertClientMatchesProjection(server, client, song.WorkflowId);
    }

    [TestMethod]
    public void InfrastructureFailureKeepsMixedWorkflowBatchesOrderedAndScoped()
    {
        var store = new EngineStateStore();
        var first = new SongJob(new SongQuery { Title = "First" });
        var search = new SearchJob("First search");
        var second = new SongJob(new SongQuery { Title = "Second" }) { WorkflowId = first.WorkflowId };
        var otherSearch = new SearchJob("Second search") { WorkflowId = search.WorkflowId };
        Job[] jobs = [first, search, second, otherSearch];
        foreach (var job in jobs) Register(store, job);
        DiscoveryChanged(store, search);
        DiscoveryChanged(store, otherSearch);
        DownloadStateChanged(store, first, TransferStates.InProgress);
        DownloadStateChanged(store, second, TransferStates.InProgress);
        Guid[] transfers = store.GetActiveTransferSnapshot().Select(transfer => transfer.TransferId).ToArray();
        var daemon = new DaemonClientStore();
        daemon.ApplySnapshot(store.GetDaemonSnapshot());
        var firstClient = new DaemonClientStore();
        firstClient.ApplySnapshot(store.GetWorkflowSnapshot(first.WorkflowId));
        var secondClient = new DaemonClientStore();
        secondClient.ApplySnapshot(store.GetWorkflowSnapshot(search.WorkflowId));
        var clients = new Dictionary<StateStreamScopeDto, DaemonClientStore>
        {
            [StateStreamScopeDto.Daemon] = daemon,
            [StateStreamScopeDto.Workflow(first.WorkflowId)] = firstClient,
            [StateStreamScopeDto.Workflow(search.WorkflowId)] = secondClient,
        };
        var batches = new List<StateUpdateBatchDto>();
        var captured = new List<StateUpdate>();
        store.StateBatchPublished += source =>
        {
            captured.Add(source);
            var batch = source.ToDto();
            batches.Add(batch);
            Assert.AreEqual(DaemonClientApplyStatus.Applied, clients[batch.Scope].Apply(batch).Status);
        };

        store.MarkActiveJobsInfrastructureFailed("connection owner stopped");

        Assert.AreEqual(3, batches.Count);
        Assert.AreEqual(1, batches.Select(batch => batch.OccurredAtUtc).Distinct().Count());
        var global = batches.Single(batch => batch.Scope == StateStreamScopeDto.Daemon);
        CollectionAssert.AreEqual(jobs.Select(job => job.Id).ToArray(), global.State.Jobs.Select(job => job.JobId).ToArray(),
            "Grouping workflow streams must preserve the daemon's interleaved input order.");
        CollectionAssert.AreEquivalent(jobs.Select(job => job.Id).ToArray(), global.State.RemovedJobIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { search.Id, otherSearch.Id }, global.State.RemovedSearchJobIds.ToArray());
        CollectionAssert.AreEquivalent(transfers, global.State.RemovedTransferIds.ToArray());
        foreach (var batch in batches.Where(batch => batch.Scope != StateStreamScopeDto.Daemon))
        {
            CollectionAssert.AreEquivalent(jobs.Where(job => job.WorkflowId == batch.Scope.WorkflowId).Select(job => job.Id).ToArray(),
                batch.State.Jobs.Select(job => job.JobId).ToArray());
            Assert.AreEqual(0, batch.State.RemovedJobIds.Count,
                "Daemon removal must leave final jobs available to their workflow reader.");
            Assert.AreEqual(0, batch.State.RemovedTransferIds.Count);
        }
        Assert.AreEqual(0, daemon.GetJobs().Count);
        Assert.AreEqual(0, daemon.GetTransfers().Count);
        Assert.AreEqual(LiveTransferSummaryDto.Empty, daemon.GetTransferSummary());
        Assert.AreEqual(store.GetDaemonSnapshot().TransferSummary, daemon.GetTransferSummary());
        AssertClientMatchesProjection(store, firstClient, first.WorkflowId);
        AssertClientMatchesProjection(store, secondClient, search.WorkflowId);
        var earlier = captured.Select(update => System.Text.Json.JsonSerializer.Serialize(update.ToDto())).ToArray();
        Register(store, new SongJob(new SongQuery { Title = "Successor" }) { WorkflowId = first.WorkflowId });
        CollectionAssert.AreEqual(earlier, captured.Take(earlier.Length)
            .Select(update => System.Text.Json.JsonSerializer.Serialize(update.ToDto())).ToArray(),
            "Later projections cannot change captured daemon or workflow packets.");
    }

    [TestMethod]
    [TestCategory("Load")]
    [TestCategory("CI")]
    public void ProgressProjectionCoalescesBeforeBuildingImmutableScopedBatches()
    {
        const int updates = 5_000;
        var store = new EngineStateStore();
        var songs = new[]
        {
            new SongJob(new SongQuery { Title = "First" }) { FileSize = updates },
            new SongJob(new SongQuery { Title = "Second" }) { FileSize = updates },
        };
        var directories = songs.Select(song =>
        {
            var target = new PeerFileTarget(new PeerFileIdentity("user", $"{song.Query.Title}.mp3"), updates, ".mp3");
            var directory = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
                new DirectoryTransferPlan(song.Query.Title!, [new DirectoryTransferEntry(target, [])])))
                { WorkflowId = song.WorkflowId };
            directory.MaterializeDirectoryChildren([song]);
            directory.BeginDirectoryTransfer();
            directory.UpdateActivity(JobActivityPhase.Downloading);
            Register(store, directory);
            Register(store, song, directory);
            return directory;
        }).ToArray();
        var transfers = songs.Select(song => Transfer(song, TransferStates.InProgress)).ToArray();
        for (int i = 0; i < songs.Length; i++)
            InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
                i, DateTimeOffset.UnixEpoch, Reference(songs[i]), CoreSnapshotFactory.CreateFileDownloadState(songs[i]), transfers[i]));
        var progress = typeof(EngineStateStore).GetMethod("OnDownloadProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<DownloadProgressedChange>>(store);
        progress(new(0, DateTimeOffset.UnixEpoch, Reference(songs[0]), CoreSnapshotFactory.CreateFileDownloadState(songs[0]), transfers[0]));
        var changes = Enumerable.Range(1, updates).Select(number =>
        {
            int index = (number - 1) % songs.Length;
            songs[index].BytesTransferred = number;
            return new DownloadProgressedChange(number, DateTimeOffset.UnixEpoch.AddMilliseconds(number),
                Reference(songs[index]), CoreSnapshotFactory.CreateFileDownloadState(songs[index]), transfers[index] with { Revision = number + 1, BytesTransferred = number });
        }).ToArray();
        var scopes = songs.Select(song => StateStreamScopeDto.Workflow(song.WorkflowId)).ToArray();
        var previous = new[] { store.GetDaemonSnapshot().Position.Sequence,
            store.GetWorkflowSnapshot(songs[0].WorkflowId).Position.Sequence,
            store.GetWorkflowSnapshot(songs[1].WorkflowId).Position.Sequence };
        var counts = new int[3];
        bool contiguous = true, correctlyScoped = true;
        StateUpdateBatchDto? first = null;
        void Consume(StateUpdateBatchDto batch)
        {
            int index = batch.Scope == StateStreamScopeDto.Daemon ? 0 : batch.Scope == scopes[0] ? 1 : 2;
            contiguous &= batch.PreviousSequence == previous[index];
            previous[index] = batch.Sequence;
            counts[index]++;
            correctlyScoped &= batch.State.Transfers.Count == (index == 0 ? 2 : 1) &&
                (index == 0 || batch.State.Transfers[0].TransferId == transfers[index - 1].Id);
            first ??= batch;
        }
        store.StateBatchPublished += source => Consume(source.ToDto());
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var change in changes) progress(change);
        store.FlushStateUpdates();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"{updates} interleaved progress updates (coalesced at capture): {allocated / 1048576.0:F2} MiB projection allocations.");
        Assert.IsTrue(allocated < 8 * 1024 * 1024,
            "Progress must coalesce before allocating scoped transport packets.");
        CollectionAssert.AreEqual(new[] { 1, 1, 1 }, counts);
        Assert.IsTrue(contiguous);
        Assert.IsTrue(correctlyScoped);
        Assert.IsNotNull(first);
        Assert.AreEqual(updates - 1L, first.State.Transfers.First().Progress!.BytesTransferred,
            "Later updates must not mutate previously published batches.");
        Assert.AreEqual(updates - 1L, store.GetWorkflowSnapshot(songs[0].WorkflowId).Transfers.Single().Progress.BytesTransferred);
        Assert.AreEqual(updates, store.GetWorkflowSnapshot(songs[1].WorkflowId).Transfers.Single().Progress.BytesTransferred);
        foreach (var directory in directories)
        {
            var snapshot = store.GetWorkflowSnapshot(directory.WorkflowId);
            var stats = snapshot.Jobs.Single(row => row.JobId == directory.Id).Stats!;
            Assert.IsTrue(stats.ProgressPercent >= 99, "A burst of progress must also publish its containing job's counters.");
        }
        Assert.AreEqual(new LiveTransferSummaryDto(2, 2, 0, 0, 0, 0, 0), store.GetDaemonSnapshot().TransferSummary);
    }

    [TestMethod]
    public void UnknownWorkflowSnapshots_DoNotRetainStreamState()
    {
        var store = new EngineStateStore();

        for (int i = 0; i < 1_000; i++)
            store.GetWorkflowSnapshot(Guid.NewGuid());

        Assert.AreEqual(0, store.RetainedWorkflowStateCounts.WorkflowStreamEpochs);
        Assert.AreEqual(0, store.RetainedWorkflowStateCounts.WorkflowStreamSequences);
        Assert.AreEqual(0, store.RetainedWorkflowStateCounts.WorkflowStreamReservations);
    }

    [TestMethod]
    public void WorkflowStreamReservation_AlignsInitialSnapshotAndReleasesUnknownScope()
    {
        var store = new EngineStateStore();
        Guid workflowId = Guid.NewGuid();

        store.ReserveWorkflowStream(workflowId);
        Guid initialEpoch = store.GetWorkflowSnapshot(workflowId).Position.Epoch;
        var job = new SearchJob("reserved") { WorkflowId = workflowId };
        StateUpdateBatchDto? firstWorkflowBatch = null;
        store.StateBatchPublished += source =>
        {
            var batch = source.ToDto();
            if (batch.Scope == StateStreamScopeDto.Workflow(workflowId))
                firstWorkflowBatch ??= batch;
        };

        Register(store, job);

        Assert.IsNotNull(firstWorkflowBatch);
        Assert.AreEqual(initialEpoch, firstWorkflowBatch.Epoch);

        InvokePrivate(store, "OnWorkflowRetired", new WorkflowRetiredChange(
            2,
            DateTimeOffset.UtcNow,
            workflowId));
        store.ReleaseWorkflowStreamReservation(workflowId);
        Assert.AreEqual(0, store.RetainedWorkflowStateCounts.WorkflowStreamEpochs);
        Assert.AreEqual(0, store.RetainedWorkflowStateCounts.WorkflowStreamReservations);
    }

    [TestMethod]
    public async Task ConcurrentCompletions_PublishWorkflowBatchesInSequence()
    {
        var server = new EngineStateStore();
        var client = new DaemonClientStore();
        var first = new SongJob(new SongQuery { Artist = "Artist", Title = "First" });
        var second = new SongJob(new SongQuery { Artist = "Artist", Title = "Second" })
        {
            WorkflowId = first.WorkflowId,
        };
        Register(server, first);
        Register(server, second);
        var independent = new SongJob(new SongQuery { Title = "Independent" });
        Register(server, independent);
        var independentClient = new DaemonClientStore();
        independentClient.ApplySnapshot(server.GetWorkflowSnapshot(independent.WorkflowId));

        var scope = StateStreamScopeDto.Workflow(first.WorkflowId);
        client.ApplySnapshot(server.GetWorkflowSnapshot(first.WorkflowId));
        var statuses = new List<DaemonClientApplyStatus>();
        server.StateBatchPublished += source =>
        {
            var batch = source.ToDto();
            if (batch.Scope == StateStreamScopeDto.Workflow(independent.WorkflowId))
                Assert.AreEqual(DaemonClientApplyStatus.Applied, independentClient.Apply(batch).Status);
            if (batch.Scope != scope)
                return;

            lock (statuses)
                statuses.Add(client.Apply(batch).Status);
        };

        using var firstUpsertEntered = new ManualResetEventSlim();
        using var releaseFirstUpsert = new ManualResetEventSlim();
        server.JobUpserted += summary =>
        {
            if (summary.JobId != first.Id)
                return;

            firstUpsertEntered.Set();
            releaseFirstUpsert.Wait(TimeSpan.FromSeconds(5));
        };

        var firstCompletion = Task.Factory.StartNew(
            () => ExecutionCompleted(server, first),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var secondCompletion = Task.CompletedTask;
        try
        {
            Assert.IsTrue(firstUpsertEntered.Wait(TimeSpan.FromSeconds(5)));
            secondCompletion = Task.Factory.StartNew(
                () => ExecutionCompleted(server, second),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await secondCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            ExecutionCompleted(server, independent);
            AssertClientMatchesProjection(server, independentClient, independent.WorkflowId);
            CollectionAssert.AreEqual(new[] { DaemonClientApplyStatus.Applied }, statuses.ToArray(),
                "Capture is already ordered: a blocked job observer must not prevent another source from flushing both completions.");
            AssertClientMatchesProjection(server, client, first.WorkflowId);
        }
        finally
        {
            releaseFirstUpsert.Set();
            await Task.WhenAll(firstCompletion, secondCompletion).WaitAsync(TimeSpan.FromSeconds(5));
        }

        lock (statuses)
        {
            CollectionAssert.AreEqual(
                new[] { DaemonClientApplyStatus.Applied },
                statuses.ToArray());
        }
        AssertClientMatchesProjection(server, client, first.WorkflowId);
    }

    [TestMethod]
    public void ReentrantRegistrationPublishesContiguousDaemonAndWorkflowUpdates()
    {
        var store = new EngineStateStore();
        var first = new SongJob(new SongQuery { Title = "First" });
        var successor = new SongJob(new SongQuery { Title = "Successor" }) { WorkflowId = first.WorkflowId };
        var daemon = new DaemonClientStore();
        var workflow = new DaemonClientStore();
        store.ReserveWorkflowStream(first.WorkflowId);
        daemon.ApplySnapshot(store.GetDaemonSnapshot());
        workflow.ApplySnapshot(store.GetWorkflowSnapshot(first.WorkflowId));
        bool entered = false;
        store.StateBatchPublished += update =>
        {
            var client = update.Scope.Kind == StateStreamScopeKind.Daemon ? daemon : workflow;
            Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(update.ToDto()).Status);
            if (entered) return;
            entered = true;
            Register(store, successor);
        };

        Register(store, first);

        Assert.IsTrue(entered);
        Assert.AreEqual(2, daemon.GetJobs().Count);
        AssertClientMatchesProjection(store, workflow, first.WorkflowId);
        Assert.AreEqual(store.GetDaemonSnapshot().Position, daemon.GetPosition(StateStreamScopeDto.Daemon));
        store.ReleaseWorkflowStreamReservation(first.WorkflowId);
    }

    [TestMethod]
    public void SnapshotDuringPendingProgressRemainsImmutableAndAcceptsTheCoalescedRange()
    {
        var store = new EngineStateStore();
        var job = new RemoteFileJob(new PeerFileTarget(new("Peer", "Unknown.bin"), null, ".bin"));
        Register(store, job);
        var progress = typeof(EngineStateStore).GetMethod("OnDownloadProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<DownloadProgressedChange>>(store);
        Guid transferId = Guid.NewGuid();
        void Progress(long revision, long bytes, long? size)
        {
            job.FileSize = size;
            job.BytesTransferred = bytes;
            progress(new(revision, DateTimeOffset.UnixEpoch, Reference(job),
                CoreSnapshotFactory.CreateFileDownloadState(job),
                CoreSnapshotFactory.CreateDownloadTransfer(transferId, job, job.Target, "attempt.part",
                    revision, "InProgress", bytes, size ?? -1, 1)));
        }
        Progress(1, 32, 128);
        var earlier = store.GetWorkflowSnapshot(job.WorkflowId);
        var client = new DaemonClientStore();
        client.ApplySnapshot(earlier);
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += update => batches.Add(update.ToDto());
        Progress(2, 64, null);
        Assert.AreEqual(0, batches.Count, "Capturing progress must not build or publish a transport batch.");
        store.FlushStateUpdates();
        var batch = batches.Single(update => update.Scope == earlier.Scope);
        Assert.IsTrue(batch.PreviousSequence < earlier.Position.Sequence);
        Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(batch).Status);
        AssertClientMatchesProjection(store, client, job.WorkflowId);
        Assert.AreEqual(32L, earlier.Transfers.Single().Progress.BytesTransferred);
        Assert.AreEqual(128L, earlier.Jobs.Single().Stats!.KnownBytes);
        Assert.IsNull(client.GetJob(job.Id)!.Stats!.KnownBytes);
        int count = batches.Count;
        store.FlushStateUpdates();
        Assert.AreEqual(count, batches.Count, "An unchanged clock tick must not duplicate packets.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetirementCannotEraseNewerUnflushedActivityInTheSameGeneration(bool closeReaderBeforeFlush)
    {
        var store = new EngineStateStore();
        var job = new SongJob(new SongQuery { Title = "Retiring" });
        Register(store, job);
        store.ReserveWorkflowStream(job.WorkflowId);
        var client = new DaemonClientStore();
        client.ApplySnapshot(store.GetWorkflowSnapshot(job.WorkflowId));
        bool appended = false;
        store.StateBatchPublished += update =>
        {
            if (update.Scope != StateStreamScopeDto.Workflow(job.WorkflowId)) return;
            Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(update.ToDto()).Status);
            if (!appended && update.State.RemovedWorkflowIds.Contains(job.WorkflowId))
            {
                appended = true;
                store.PublishActivity("workflow.message", new WorkflowMessageActivityDto("Debug", "test", "first"), job.WorkflowId);
            }
        };
        var retire = typeof(EngineStateStore).GetMethod("OnWorkflowRetired", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<WorkflowRetiredChange>>(store);
        retire(new(2, DateTimeOffset.UnixEpoch, job.WorkflowId));
        Assert.IsTrue(appended);
        if (closeReaderBeforeFlush) store.ReleaseWorkflowStreamReservation(job.WorkflowId);
        store.PublishActivity("workflow.message", new WorkflowMessageActivityDto("Debug", "test", "second"), job.WorkflowId);
        var captured = store.GetWorkflowSnapshot(job.WorkflowId).Position;
        store.FlushStateUpdates();
        Assert.AreEqual(captured,
            client.GetPosition(StateStreamScopeDto.Workflow(job.WorkflowId)));
        if (!closeReaderBeforeFlush) store.ReleaseWorkflowStreamReservation(job.WorkflowId);
        Assert.AreEqual(0, store.RetainedWorkflowStateCounts.WorkflowStreamEpochs);
    }

    [TestMethod]
    public void FileProgressPublishesLearnedSizeAndClearsItWithoutWaitingForCompletion()
    {
        var store = new EngineStateStore();
        var job = new RemoteFileJob(new PeerFileTarget(new("Peer", "Unknown.bin"), null, ".bin"));
        Register(store, job);
        var client = new DaemonClientStore();
        client.ApplySnapshot(store.GetWorkflowSnapshot(job.WorkflowId));
        store.StateBatchPublished += update =>
        {
            if (update.Scope == StateStreamScopeDto.Workflow(job.WorkflowId))
                Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(update.ToDto()).Status);
        };
        Guid transferId = Guid.NewGuid();
        long revision = 0;
        void Progress(long? size)
        {
            job.FileSize = size;
            job.BytesTransferred = 32;
            InvokePrivate(store, "OnDownloadProgress", new DownloadProgressedChange(
                ++revision, DateTimeOffset.UnixEpoch, Reference(job),
                CoreSnapshotFactory.CreateFileDownloadState(job),
                CoreSnapshotFactory.CreateDownloadTransfer(transferId, job, job.Target, "attempt.part",
                    revision, "InProgress", 16, 256, 1)));
        }
        Progress(128);
        var captured = client.GetJob(job.Id)!;
        Assert.AreEqual(128L, captured.Stats!.KnownBytes);
        Assert.AreEqual(store.GetJobSummary(job.Id)!.Stats, captured.Stats);
        var detail = (RemoteFileJobPayloadDto)store.GetJobDetail(job.Id)!.Payload;
        Assert.AreEqual(32L, detail.File.BytesTransferred);
        Assert.AreEqual(128L, detail.File.FileSize);
        Progress(0);
        Assert.AreEqual(0L, client.GetJob(job.Id)!.Stats!.KnownBytes);
        Progress(null);
        Assert.IsNull(client.GetJob(job.Id)!.Stats!.KnownBytes);
        Assert.IsNull(((RemoteFileJobPayloadDto)store.GetJobDetail(job.Id)!.Payload).File.FileSize);
        Assert.AreEqual(128L, captured.Stats.KnownBytes, "Earlier client facts remain immutable.");
    }

    [TestMethod]
    public void SongPayload_IncludesSnapshotProgress()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            BytesTransferred = 25,
            FileSize = 100,
        };
        song.UpdateActivity(JobActivityPhase.Downloading);

        Register(store, song);

        var payload = store.GetJobDetail(song.Id)?.Payload as SongJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(25, payload.File.BytesTransferred);
        Assert.AreEqual(100, payload.File.FileSize);
        Assert.AreEqual(25d, payload.File.ProgressPercent);

        song.BytesTransferred = 75;
        UpdateState(store, song);
        song.FileSize = 200;
        UpdateState(store, song);
        var latest = store.GetJobDetail(song.Id)!.Payload as SongJobPayloadDto;
        Assert.IsNotNull(latest);
        Assert.AreEqual(75, latest.File.BytesTransferred);
        Assert.AreEqual(200, latest.File.FileSize);
        Assert.AreEqual(37.5, latest.File.ProgressPercent);
        Assert.AreEqual(25, payload.File.BytesTransferred,
            "Previously returned detail must remain an immutable observation.");
    }

    [TestMethod]
    public void LiveTransferDetail_ProjectsLatestDownloadAndUploadAttempts()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var transfer = Transfer(song, TransferStates.InProgress) with
        {
            Id = Guid.NewGuid(),
            AttemptCount = 1,
            RequestedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2),
            StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            LastProgressAtUtc = DateTimeOffset.UtcNow,
            BytesPerSecond = 42,
        };
        Guid attemptId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
            1, startedAt, Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song), transfer));
        InvokePrivate(store, "OnTransferAttemptStarted", new TransferAttemptStartedChange(
            2,
            startedAt,
            Reference(song),
            transfer,
            attemptId,
            1,
            1,
            TransferAttemptSource.SoulseekPeer,
            "C:/downloads/Track.mp3.incomplete"));

        var started = store.GetLiveTransferDetail(transfer.Id);
        Assert.IsNotNull(started);
        Assert.AreEqual(1, started.Transfer.Status.AttemptCount);
        Assert.AreEqual(42L, started.Transfer.Progress.BytesPerSecond);
        Assert.AreEqual(100_000, started.Transfer.Identity.PeerUploadSpeed);
        Assert.AreEqual(true, started.Transfer.Identity.PeerHasFreeUploadSlot);
        Assert.AreEqual(transfer.RequestedAtUtc, started.Transfer.Scheduling?.RequestedAtUtc);
        Assert.AreEqual(transfer.StartedAtUtc, started.Transfer.Scheduling?.StartedAtUtc);
        Assert.AreEqual("Track.mp3", started.Transfer.File?.Name);
        Assert.AreEqual(100L, started.Transfer.File?.Size);
        Assert.IsTrue(started.Transfer.Status.AvailableActions.Any(action =>
            action.Kind == ServerResourceActionKind.Cancel));
        Assert.AreEqual("Started", started.LatestAttempt?.State);
        Assert.AreEqual(startedAt, started.LatestAttempt?.StartedAtUtc);

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        InvokePrivate(store, "OnTransferAttemptCompleted", new TransferAttemptCompletedChange(
            3,
            completedAt,
            Reference(song),
            transfer,
            attemptId,
            1,
            2));
        var completed = store.GetLiveTransferDetail(transfer.Id);
        Assert.IsNotNull(completed);
        Assert.AreEqual("Completed", completed.LatestAttempt?.State);
        Assert.AreEqual(completedAt, completed.LatestAttempt?.CompletedAtUtc);
        Guid retryId = Guid.NewGuid();
        InvokePrivate(store, "OnTransferAttemptStarted", new TransferAttemptStartedChange(
            4, completedAt, Reference(song), transfer, retryId, 2, 1,
            TransferAttemptSource.SoulseekPeer, "C:/downloads/Track.mp3.incomplete"));
        // A delayed Started event for an older revision cannot replace its terminal state.
        InvokePrivate(store, "OnTransferAttemptStarted", new TransferAttemptStartedChange(
            2, startedAt, Reference(song), transfer, attemptId, 1, 1,
            TransferAttemptSource.SoulseekPeer, "C:/downloads/Track.mp3.incomplete"));
        var attempts = store.GetLiveTransferAttempts(transfer.Id, 0, 10);
        Assert.IsNotNull(attempts);
        CollectionAssert.AreEqual(new[] { "Completed", "Started" }, attempts.Select(attempt => attempt.State).ToArray());
        Assert.AreEqual(retryId, store.GetLiveTransferDetail(transfer.Id)?.LatestAttempt?.AttemptId);
        Assert.AreEqual(retryId, store.GetLiveTransferAttempts(transfer.Id, 1, 1)!.Single().AttemptId);
        store.RemoveTerminalTransfer(transfer.Id);
        Assert.IsNull(store.GetLiveTransferAttempts(transfer.Id, 0, 10));

        Guid uploadId = Guid.NewGuid();
        Guid uploadAttemptId = Guid.NewGuid();
        store.UpdateUploadTransfer(new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
            uploadId,
            Revision: 4,
            Username: "upload-peer",
            RemotePath: @"Share\Track.mp3",
            SizeBytes: 100,
            RequestedAtUtc: startedAt,
            State: Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress,
            FailureReason: Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
            CancellationSource: Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
            BytesTransferred: 50,
            BytesPerSecond: 25,
            LastProgressAtUtc: completedAt,
            Attempt: new Sockseek.Core.Transfers.Uploads.UploadAttemptSnapshot(
                uploadAttemptId,
                Number: 1,
                StartedAtUtc: startedAt,
                FinishedAtUtc: null,
                BytesTransferred: 50,
                BytesPerSecond: 25),
            FinishedAtUtc: null));

        var upload = store.GetLiveTransferDetail(uploadId);
        Assert.IsNotNull(upload);
        Assert.AreEqual("Upload", upload.Transfer.Identity.Direction);
        Assert.AreEqual(uploadAttemptId, upload.LatestAttempt?.AttemptId);
        Assert.AreEqual("Started", upload.LatestAttempt?.State);
        Assert.AreEqual("upload-peer", upload.LatestAttempt?.SourceUsername);
        Assert.IsNull(upload.Transfer.File, "Genuinely unknown upload metadata remains null.");
        store.UpdateUploadTransfer(new UploadTransferSnapshot(uploadId, 5, "upload-peer", @"Share\Track.mp3",
            100, startedAt, UploadTransferState.Failed, UploadFailureReason.Unavailable,
            UploadCancellationSource.None, 50, 0, completedAt,
            new UploadAttemptSnapshot(uploadAttemptId, 1, startedAt, completedAt, 50, 0), completedAt,
            FailureMessage: "The shared file was removed."));
        upload = store.GetLiveTransferDetail(uploadId)!;
        Assert.AreEqual("The shared file was removed.", upload.Transfer.Status.FailureMessage);
        Assert.AreEqual(upload.Transfer.Status.FailureMessage, upload.LatestAttempt?.FailureMessage);

        foreach (var (reason, wireReason) in new[] {
            (UploadFailureReason.ConnectionFailed, Sockseek.Api.TransferFailureReason.ConnectionFailed),
            (UploadFailureReason.TimedOut, Sockseek.Api.TransferFailureReason.TransferTimedOut),
        })
        {
            var id = Guid.NewGuid();
            store.UpdateUploadTransfer(new UploadTransferSnapshot(id, 1, "upload-peer", @"Share\Failed.mp3",
                100, startedAt, UploadTransferState.Failed, reason, UploadCancellationSource.None,
                0, 0, null, null, completedAt, FailureMessage: "Peer failure details"));
            Assert.AreEqual(wireReason, store.GetLiveTransferDetail(id)!.Transfer.Status.FailureReason);
        }

    }

    [TestMethod]
    public void LiveTransfer_ReplacesChangedFactsButKeepsProgressDeltasCompact()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Title = "Track" });
        Register(store, song);
        var first = Transfer(song, TransferStates.InProgress);
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += batch => batches.Add(batch.ToDto());
        InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
            1, DateTimeOffset.UtcNow, Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song), first));
        var client = new DaemonClientStore();
        client.ApplySnapshot(store.GetWorkflowSnapshot(song.WorkflowId));

        var second = first with
        {
            Revision = first.Revision + 1,
            Candidate = first.Candidate! with
            {
                Peer = first.Candidate!.Peer with
                {
                    UploadSpeed = 2_000_000,
                    HasFreeUploadSlot = false,
                },
            },
        };
        InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
            2, DateTimeOffset.UtcNow, Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song), second));

        var changed = batches.SelectMany(batch => batch.State.Transfers)
            .Last(delta => delta.TransferId == first.Id);
        Assert.IsNotNull(changed.Added);
        Assert.AreEqual(second.Revision, changed.Revision);
        Assert.AreEqual(changed.Revision, changed.Added.Revision);
        Assert.AreEqual(2_000_000, changed.Added.Identity.PeerUploadSpeed);
        Assert.AreEqual(false, changed.Added.Identity.PeerHasFreeUploadSlot);
        var workflowBatch = batches.Last(batch => batch.Scope == StateStreamScopeDto.Workflow(song.WorkflowId));
        Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(workflowBatch).Status);
        Assert.AreEqual(2_000_000, client.GetTransfers().Single(transfer => transfer.TransferId == first.Id).Identity.PeerUploadSpeed);

        var metadata = second with
        {
            Revision = second.Revision + 1,
            File = second.File! with { BitRate = 320, Attributes = [new FileAttributeSnapshot("BitRate", 320)] },
        };
        InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
            3, DateTimeOffset.UtcNow, Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song), metadata));
        workflowBatch = batches.Last(batch => batch.Scope == StateStreamScopeDto.Workflow(song.WorkflowId));
        Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(workflowBatch).Status);
        Assert.AreEqual(320, client.GetTransfers().Single().File?.BitRate);

        var progress = metadata with
        {
            Revision = metadata.Revision + 1,
            BytesTransferred = 50,
            // Snapshots can recreate equivalent attribute arrays; this must
            // not turn every progress tick into a full row replacement.
            File = metadata.File! with { Attributes = [new FileAttributeSnapshot("BitRate", 320)] },
        };
        InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
            4, DateTimeOffset.UtcNow, Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song), progress));
        workflowBatch = batches.Last(batch => batch.Scope == StateStreamScopeDto.Workflow(song.WorkflowId));
        Assert.IsNull(workflowBatch.State.Transfers.Single().Added);
        Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(workflowBatch).Status);
        Assert.AreEqual(50, client.GetTransfers().Single().Progress.BytesTransferred);
        Assert.AreEqual(320, client.GetTransfers().Single().File?.BitRate);
    }

    [TestMethod]
    public void DaemonSnapshot_IncludesPreexistingActiveUploadAndDoesNotDuplicateItsDelta()
    {
        var store = new EngineStateStore();
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        DateTimeOffset started = DateTimeOffset.UnixEpoch.AddHours(1);
        var upload = new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
            transferId,
            Revision: 2,
            Username: "peer",
            RemotePath: @"Share\Track.flac",
            SizeBytes: 100,
            RequestedAtUtc: started.AddSeconds(-1),
            State: Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress,
            FailureReason: Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
            CancellationSource: Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
            BytesTransferred: 25,
            BytesPerSecond: 10,
            LastProgressAtUtc: started,
            Attempt: new Sockseek.Core.Transfers.Uploads.UploadAttemptSnapshot(
                attemptId, 1, started, null, 25, 10),
            FinishedAtUtc: null);
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += batch => batches.Add(batch.ToDto());

        store.UpdateUploadTransfer(upload);
        store.FlushStateUpdates();
        int afterFirstUpdate = batches.Count;
        store.UpdateUploadTransfer(upload);
        store.FlushStateUpdates();

        Assert.AreEqual(afterFirstUpdate, batches.Count, "An identical hydration must not emit a duplicate delta.");
        Assert.AreEqual(transferId, store.GetDaemonSnapshot().Transfers.Single().TransferId);

        store.RemoveTerminalTransfer(transferId);
        store.FlushStateUpdates();
        Assert.AreEqual(0, store.GetDaemonSnapshot().Transfers.Count);
        Assert.IsTrue(batches.Last().State.RemovedTransferIds.Contains(transferId));
    }

    [TestMethod]
    public void TransferGroupSnapshot_IncludesQueuedUploadsAndKeepsExactPeerAndGroupScope()
    {
        var store = new EngineStateStore();
        DateTimeOffset requested = DateTimeOffset.UnixEpoch.AddHours(1);
        void Add(Guid id, string username, string groupRef)
            => store.UpdateUploadTransfer(new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
                id, 1, username, @"Share\Track.bin", 100, requested,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.Queued,
                Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
                Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
                0, 0, null, null, null,
                GroupRef: groupRef,
                GroupDisplayPath: "Share"));
        Guid included = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid older = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Add(included, "peer", "group");
        Add(older, "peer", "group");
        Add(Guid.NewGuid(), "peer", "other-group");
        Add(Guid.NewGuid(), "other-peer", "group");

        Assert.AreEqual(0, store.GetActiveTransferSnapshot().Count);
        var result = store.GetTransferGroupSnapshot(new HashSet<TransferGroupKeyDto>
        {
            new(TransferCommandDirection.Upload, "peer", "group"),
        });
        CollectionAssert.AreEquivalent(new[] { included, older },
            result.Select(item => item.TransferId).ToArray());
        CollectionAssert.AreEquivalent(new[] { included, older },
            store.GetTransferPageCandidates("group", "upload", "peer")
                .Select(item => item.TransferId).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TerminalDownload_RemainsLiveUntilPersistenceOwnerExplicitlyRetiresIt(bool workflowFinishesFirst)
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        Guid transferId = Guid.NewGuid();
        var terminal = Transfer(song, (TransferStates)0) with
        {
            Id = transferId,
            State = "Completed",
            TerminalOutcome = TransferSnapshotTerminalOutcome.Succeeded,
        };
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += batch => batches.Add(batch.ToDto());
        Register(store, song);

        typeof(EngineStateStore)
            .GetMethod("OnTerminalTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [terminal, DateTimeOffset.UtcNow, null, null]);
        store.FlushStateUpdates();

        Assert.IsNotNull(store.GetLiveTransfer(transferId));
        Assert.IsFalse(batches.Last().State.RemovedTransferIds.Contains(transferId));
        Assert.AreEqual(transferId, store.GetDaemonSnapshot().Transfers.Single().TransferId);

        if (workflowFinishesFirst)
        {
            song.SetDone();
            UpdateState(store, song);
            Assert.AreEqual(0, store.GetDaemonSnapshot().Transfers.Count);
            Assert.IsNotNull(store.GetLiveTransfer(transferId), "History handoff still owns this terminal projection.");
            Assert.IsTrue(batches.Last(batch => batch.Scope == StateStreamScopeDto.Daemon)
                .State.RemovedTransferIds.Contains(transferId));
        }
        int batchesBeforeRemoval = batches.Count;

        store.RemoveTerminalTransfer(transferId);
        store.FlushStateUpdates();
        Assert.IsNull(store.GetLiveTransfer(transferId));
        if (workflowFinishesFirst)
            Assert.AreEqual(batchesBeforeRemoval, batches.Count,
                "A later persistence acknowledgement must not remove the same daemon transfer twice.");
        else
            Assert.IsTrue(batches.Last().State.RemovedTransferIds.Contains(transferId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WorkflowRetirementPublishesFinalRemovalAndReleasesAllWorkflowIndexes(bool persistenceOwnsRetirement)
    {
        var store = new EngineStateStore(persistenceOwnsRetirement);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var parent = new JobList("workflow") { WorkflowId = song.WorkflowId };
        parent.Add(song);
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += batch => batches.Add(batch.ToDto());

        Register(store, parent);
        Register(store, song, parent);
        Guid transferId = Guid.NewGuid();
        InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
            3,
            DateTimeOffset.UtcNow,
            Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song),
            Transfer(song, TransferStates.InProgress) with { Id = transferId }));
        Guid priorEpoch = store.GetWorkflowSnapshot(song.WorkflowId).Position.Epoch;
        var beforeRetirement = store.GetDaemonSnapshot().JobHistoryPosition!;

        InvokePrivate(store, "OnWorkflowRetired", new WorkflowRetiredChange(
            4,
            DateTimeOffset.UtcNow,
            song.WorkflowId));
        if (!persistenceOwnsRetirement)
            Assert.IsTrue(store.GetDaemonSnapshot().JobHistoryPosition!.Sequence > beforeRetirement.Sequence,
                "A missed workflow retirement must still make earlier Jobs history stale.");

        if (persistenceOwnsRetirement)
        {
            Assert.IsNotNull(store.GetJobDetail(parent.Id));
            var pendingSuccessor = new SongJob(new SongQuery { Title = "pending successor" }) { WorkflowId = song.WorkflowId };
            Register(store, pendingSuccessor);
            Guid successorTransferId = Guid.NewGuid();
            InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
                5, DateTimeOffset.UtcNow, Reference(pendingSuccessor), CoreSnapshotFactory.CreateFileDownloadState(pendingSuccessor),
                Transfer(pendingSuccessor, TransferStates.InProgress) with { Id = successorTransferId }));
            var client = new DaemonClientStore();
            client.ApplySnapshot(store.GetWorkflowSnapshot(song.WorkflowId));
            var daemonClient = new DaemonClientStore();
            daemonClient.ApplySnapshot(store.GetDaemonSnapshot());
            void ApplyToClient(StateUpdate source)
            {
                var batch = source.ToDto();
                if (batch.Scope == StateStreamScopeDto.Daemon)
                    Assert.AreEqual(DaemonClientApplyStatus.Applied, daemonClient.Apply(batch).Status);
                if (batch.Scope == StateStreamScopeDto.Workflow(song.WorkflowId))
                {
                    var applied = client.Apply(batch);
                    Assert.AreEqual(DaemonClientApplyStatus.Applied, applied.Status,
                        $"epoch={batch.Epoch}, previous={batch.PreviousSequence}, sequence={batch.Sequence}, client={client.GetPosition(batch.Scope)}, reason={applied.RecoveryReason}");
                }
            }
            store.StateBatchPublished += ApplyToClient;

            store.RetireJobProjections(song.WorkflowId, [parent.Id, song.Id]);
            Assert.IsNull(store.GetJobSummary(parent.Id));
            Assert.IsNotNull(store.GetJobSummary(pendingSuccessor.Id));
            Assert.IsNotNull(store.GetLiveTransfer(successorTransferId));
            Assert.AreEqual(priorEpoch, store.GetWorkflowSnapshot(song.WorkflowId).Position.Epoch);
            Assert.IsFalse(batches.Last().State.RemovedWorkflowIds.Contains(song.WorkflowId));
            pendingSuccessor.SetDone();
            UpdateState(store, pendingSuccessor);
            Assert.AreEqual(0, daemonClient.GetLiveStateView().Workflows.Count);

            // A successor may register during publication itself, after the
            // old generation's final removal delta has already been built.
            var reentrantSuccessor = new SearchJob("publication successor") { WorkflowId = song.WorkflowId };
            void RegisterDuringRemoval(StateUpdate batch)
            {
                if (batch.Scope == StateStreamScopeDto.Workflow(song.WorkflowId)
                    && batch.State.RemovedJobIds.Contains(pendingSuccessor.Id))
                    Register(store, reentrantSuccessor);
            }
            store.StateBatchPublished += RegisterDuringRemoval;
            store.RetireJobProjections(song.WorkflowId, [pendingSuccessor.Id]);
            store.StateBatchPublished -= RegisterDuringRemoval;
            Assert.IsNotNull(store.GetJobDetail(reentrantSuccessor.Id));
            Assert.IsNull(store.GetLiveTransfer(successorTransferId));
            Assert.AreEqual(priorEpoch, store.GetWorkflowSnapshot(song.WorkflowId).Position.Epoch);
            Assert.IsTrue(client.GetLiveStateView().Jobs.Any(job => job.JobId == reentrantSuccessor.Id));
            CollectionAssert.AreEquivalent(store.GetDaemonSnapshot().Workflows.Select(row => row.Summary.WorkflowId).ToArray(),
                daemonClient.GetLiveStateView().Workflows.Select(row => row.WorkflowId).ToArray());
            store.RetireJobProjections(song.WorkflowId, [reentrantSuccessor.Id]);
            Assert.AreEqual(0, daemonClient.GetLiveStateView().Jobs.Count);
            Assert.AreEqual(0, daemonClient.GetLiveStateView().Workflows.Count);
            store.StateBatchPublished -= ApplyToClient;
        }

        Assert.IsNull(store.GetJobSummary(song.Id));
        Assert.IsNull(store.GetJobSummary(parent.Id));
        Assert.IsNull(store.GetWorkflowSummary(song.WorkflowId));
        Assert.IsNull(store.GetLiveTransfer(transferId));
        Assert.AreEqual(
            new EngineStateStoreRetainedWorkflowCounts(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            store.RetainedWorkflowStateCounts);

        var removal = batches.First(batch => batch.State.RemovedJobIds.Contains(parent.Id));
        CollectionAssert.AreEquivalent(
            new[] { parent.Id, song.Id },
            removal.State.RemovedJobIds.ToArray());
        CollectionAssert.Contains(removal.State.RemovedTransferIds.ToArray(), transferId);

        var successor = new SearchJob("successor") { WorkflowId = song.WorkflowId };
        Register(store, successor);
        Assert.AreNotEqual(
            priorEpoch,
            store.GetWorkflowSnapshot(song.WorkflowId).Position.Epoch,
            "A reused workflow ID starts a new recoverable stream generation.");
    }

    [TestMethod]
    public void ExactSongPayload_UsesExactTargetWithoutFabricatingSearchEvidence()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            ExactTarget = new PeerFileTarget(
                new PeerFileIdentity(" Peer ", @"Share\File.bin"),
                42,
                ".bin"),
        };

        Register(store, song);

        var payload = store.GetJobDetail(song.Id)?.Payload as SongJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.IsNotNull(payload.ExactTarget);
        Assert.AreEqual(" Peer ", payload.ExactTarget.Username);
        Assert.IsNull(payload.ResolvedUsername);
        Assert.IsNull(payload.ResolvedFilename);
        Assert.IsNull(payload.ResolvedHasFreeUploadSlot);
    }

    [TestMethod]
    public void ResolvedDirectoryPayload_ReportsScalarPlanStateWithoutInliningEntries()
    {
        var store = new EngineStateStore();
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Root\File.bin"),
            42,
            ".bin");
        var plan = new DirectoryTransferPlan("Root", [
            new DirectoryTransferEntry(target, []),
        ]);
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));

        Register(store, job);

        var payload = store.GetJobDetail(job.Id)?.Payload as RemoteDirectoryJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(RemoteDirectorySourceKindDto.Resolved, payload.SourceKind);
        Assert.AreEqual("Peer", payload.SourceUsername);
        Assert.IsNull(payload.SourceFolderPath);
        Assert.AreEqual("planned", payload.Directory.Phase);
        Assert.AreEqual(1, payload.Directory.AttemptNumber);
    }

    [TestMethod]
    public void ResolvedDirectoryPayload_KeepsExactObservedSourcePath()
    {
        var store = new EngineStateStore();
        var source = new PeerDirectoryIdentity("Peer", @"Artwork\Head Hunters");
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Artwork\Head Hunters\Cover.jpg"),
            42, ".jpg");
        var plan = DirectoryTransferPlanner.FromObservedSelection(
            new PeerDirectorySnapshot(source, [target], isComplete: false));
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));

        Register(store, job);

        var payload = store.GetJobDetail(job.Id)?.Payload as RemoteDirectoryJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual("Peer", payload.SourceUsername);
        Assert.AreEqual(@"Artwork\Head Hunters", payload.SourceFolderPath);
        Assert.AreEqual("Head Hunters", payload.DisplayRoot);
    }

    [TestMethod]
    public void DirectoryManifest_PagesSupplementalTargetsAndStateWithoutChildDetailRequests()
    {
        static AlbumFile File(string path) => new(new SongQuery { Title = path },
            SoulseekSearchAdapter.ToFileCandidate(new SearchResponse("ExactPeer", 1, true, 100_000, 0, []),
                new Soulseek.File(1, path, 128, Path.GetExtension(path), [])));
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Album = "Root" });
        var folder = new AlbumFolder("ExactPeer", "Root", [File(@"Root\Track.flac")]);
        album.BeginAlbumTransferAttempt(folder);
        var artwork = album.EnsureSupplementalTrackJobs(Enumerable.Range(0, 501)
            .Select(index => File($@"Other\Artwork\{index:D3}.jpg")).ToArray());
        Register(store, album);
        var queued = store.GetDirectoryManifest(album.Id, 1, 499, 500)!;
        Assert.AreEqual(@"Other\Artwork\500.jpg", queued.Items[^1].Target?.Filename);
        Assert.IsNull(queued.Items[^1].Child);
        foreach (var child in album.TrackJobs) Register(store, child, album);
        artwork[^1].BytesTransferred = 64;
        artwork[^1].UpdateActivity(JobActivityPhase.Downloading);
        UpdateState(store, artwork[^1]);
        var first = store.GetDirectoryManifest(album.Id, 1, -1, 500)!;
        Assert.AreEqual(1, first.EntryCount);
        Assert.AreEqual(501, first.SupplementalEntryCount);
        Assert.AreEqual(500, first.Items.Count);
        Assert.AreEqual(499, first.NextOrdinal);
        Assert.AreEqual(artwork[0].Id, first.Items[1].ChildJobId);
        Assert.AreEqual(@"Other\Artwork\000.jpg", first.Items[1].Target?.Filename);
        var tail = store.GetDirectoryManifest(album.Id, 1, 499, 500)!;
        Assert.AreEqual(2, tail.Items.Count);
        Assert.AreEqual(artwork[^1].Id, tail.Items[^1].ChildJobId);
        Assert.AreEqual(50, tail.Items[^1].File?.ProgressPercent);
        Assert.AreEqual(ServerJobLifecycleState.Running, tail.Items[^1].Child?.LifecycleState);
        Assert.IsNull(tail.NextOrdinal);
        var appended = album.EnsureSupplementalTrackJobs([File(@"Other\New.jpg")]).Single();
        UpdateStructure(store, album);
        Register(store, appended, album);
        var added = store.GetDirectoryManifest(album.Id, 1, 501, 500)!;
        Assert.AreEqual(502, added.SupplementalEntryCount);
        Assert.AreEqual(502, added.Items.Single().Ordinal);
        Assert.AreEqual(appended.Id, added.Items.Single().ChildJobId);
    }

    [TestMethod]
    public void DirectoryManifest_ExposesUnmaterializedFilesThenLiveChildProgress()
    {
        var store = new EngineStateStore();
        var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\File.bin"), 128, ".bin");
        var directory = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Root", [new DirectoryTransferEntry(target, [])])));
        Register(store, directory);
        var before = store.GetDirectoryManifest(directory.Id, null, -1, 1);
        Assert.IsNotNull(before);
        Assert.IsFalse(before.ChildrenMaterialized);
        Assert.AreEqual(@"Root\File.bin", before.Items.Single().Target!.Filename);
        Assert.IsNull(before.Items.Single().ChildJobId);

        var child = new RemoteFileJob(target) { WorkflowId = directory.WorkflowId, FileSize = 128 };
        directory.MaterializeDirectoryChildren([child]);
        directory.BeginDirectoryTransfer();
        directory.UpdateActivity(JobActivityPhase.Downloading);
        UpdateStructure(store, directory);
        Register(store, child, directory);
        child.UpdateActivity(JobActivityPhase.Downloading);
        UpdateState(store, child);
        var initial = store.GetWorkflowSnapshot(directory.WorkflowId);
        var client = new DaemonClientStore();
        client.ApplySnapshot(initial);
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += update => batches.Add(update.ToDto());
        void ApplyUpdates()
        {
            store.FlushStateUpdates();
            foreach (var batch in batches.Where(batch => batch.Scope == initial.Scope))
                Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(batch).Status);
            batches.Clear();
        }
        child.BytesTransferred = 64;
        child.DownloadPath = "/output/final/File.bin";
        var progressTransferId = Guid.NewGuid();
        var reportProgress = typeof(EngineStateStore).GetMethod("OnDownloadProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<DownloadProgressedChange>>(store);
        reportProgress(new DownloadProgressedChange(
            2, DateTimeOffset.UtcNow, Reference(child), CoreSnapshotFactory.CreateFileDownloadState(child), CoreSnapshotFactory.CreateDownloadTransfer(
                progressTransferId, child, target, "/staging/File.part", 1, "InProgress", 32, 256, 1)));
        // Progress alone must update the containing job; a long transfer need
        // not emit another state edge or trigger a detail read before the tick.
        ApplyUpdates();
        Assert.AreEqual(50, client.GetJob(directory.Id)!.Stats!.ProgressPercent);
        Assert.AreEqual(0, initial.Jobs.Single(job => job.JobId == directory.Id).Stats!.ProgressPercent);
        AssertClientMatchesProjection(store, client, directory.WorkflowId);

        var page = store.GetDirectoryManifest(directory.Id, 1, -1, 1);
        Assert.IsNotNull(page);
        Assert.IsTrue(page.ChildrenMaterialized);
        Assert.AreEqual(child.Id, page.Items.Single().ChildJobId);
        Assert.AreEqual(ServerJobLifecycleState.Running, page.Items.Single().Child?.LifecycleState);
        Assert.AreEqual(50, page.Items.Single().File?.ProgressPercent);
        Assert.AreEqual("/output/final/File.bin", page.Items.Single().File?.DownloadPath);
        var childDetail = (RemoteFileJobPayloadDto)store.GetJobDetail(child.Id)!.Payload;
        Assert.AreEqual(64L, childDetail.File.BytesTransferred);
        Assert.AreEqual(128L, childDetail.File.FileSize);
        Assert.AreEqual("/output/final/File.bin", childDetail.File.DownloadPath);
        var transfer = store.GetLiveTransfer(progressTransferId)!;
        Assert.AreEqual(32L, transfer.Progress.BytesTransferred);
        Assert.AreEqual(256L, transfer.Progress.TotalBytes);
        Assert.AreEqual("/staging/File.part", transfer.Status.LocalPath);
        var payload = store.GetJobDetail(directory.Id)?.Payload as RemoteDirectoryJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(64, payload.Directory.BytesTransferred);
        Assert.AreEqual(50, payload.Directory.ProgressPercent);
        var transferId = Guid.NewGuid();
        long transferRevision = 0;
        foreach (string state in new[] { "Queued", "InProgress", "InProgress" })
            InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
                3, DateTimeOffset.UtcNow, Reference(child), CoreSnapshotFactory.CreateFileDownloadState(child), CoreSnapshotFactory.CreateDownloadTransfer(
                    transferId, child, target, "/output/File.bin", ++transferRevision,
                    state, 64, 128, 1)));
        var halfway = store.GetJobSummary(directory.Id)!;
        Assert.AreEqual(50, halfway.Stats!.ProgressPercent);
        Assert.AreEqual(0, halfway.Stats.CompletedMemberCount);
        child.BytesTransferred = 96;
        reportProgress(new DownloadProgressedChange(
            4, DateTimeOffset.UtcNow, Reference(child), CoreSnapshotFactory.CreateFileDownloadState(child),
            CoreSnapshotFactory.CreateDownloadTransfer(progressTransferId, child, target,
                "/staging/File.part", 2, "InProgress", 48, 256, 1)));
        // An HTTP/scoped read between ticks sees the same counters and keeps
        // its captured revision immutable when completion arrives later.
        var readBeforeFlush = store.GetJobSummary(directory.Id)!;
        Assert.AreEqual(75, readBeforeFlush.Stats!.ProgressPercent);
        var snapshotBeforeFlush = store.GetWorkflowSnapshot(directory.WorkflowId);
        Assert.AreEqual(75, snapshotBeforeFlush.Jobs.Single(row => row.JobId == directory.Id).Stats!.ProgressPercent);
        ApplyUpdates();
        Assert.AreEqual(75, client.GetJob(directory.Id)!.Stats!.ProgressPercent);
        child.BytesTransferred = 128;
        child.SetDone();
        UpdateState(store, child);
        UpdateState(store, child);
        ApplyUpdates();
        Assert.AreEqual(100, client.GetJob(directory.Id)!.Stats!.ProgressPercent);
        Assert.AreEqual(1, client.GetJob(directory.Id)!.Stats!.CompletedMemberCount);
        AssertClientMatchesProjection(store, client, directory.WorkflowId);
        payload = store.GetJobDetail(directory.Id)?.Payload as RemoteDirectoryJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(1, payload.Directory.TerminalFileCount);
        Assert.AreEqual(1, payload.Directory.SuccessfulFileCount);
        Assert.AreEqual(0, payload.Directory.FailedFileCount);
        Assert.AreEqual(128, payload.Directory.BytesTransferred);
        Assert.AreEqual(100, store.GetJobSummary(directory.Id)!.Stats!.ProgressPercent);
        Assert.AreEqual(1, store.GetJobSummary(directory.Id)!.Stats!.CompletedMemberCount);
        Assert.AreEqual(50, halfway.Stats.ProgressPercent, "Earlier summary revisions stay immutable.");
        Assert.AreEqual(75, readBeforeFlush.Stats.ProgressPercent);
        Assert.AreEqual(75, snapshotBeforeFlush.Jobs.Single(row => row.JobId == directory.Id).Stats!.ProgressPercent);

        var replacementTarget = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Replacement.bin"), 16, ".bin");
        directory.BeginDirectoryAttempt(new DirectoryTransferPlan("Root", [new DirectoryTransferEntry(replacementTarget, [])]));
        var replacement = new RemoteFileJob(replacementTarget) { WorkflowId = directory.WorkflowId };
        directory.MaterializeDirectoryChildren([replacement]);
        directory.BeginDirectoryTransfer();
        UpdateStructure(store, directory);
        Register(store, replacement, directory);
        replacement.Fail(JobFailureReason.Other);
        UpdateState(store, replacement);
        UpdateState(store, child);
        payload = store.GetJobDetail(directory.Id)?.Payload as RemoteDirectoryJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.Directory.AttemptNumber);
        Assert.AreEqual(1, payload.Directory.FileCount);
        Assert.AreEqual(1, payload.Directory.TerminalFileCount);
        Assert.AreEqual(0, payload.Directory.SuccessfulFileCount);
        Assert.AreEqual(1, payload.Directory.FailedFileCount);
        Assert.AreEqual(0, payload.Directory.BytesTransferred);
        Assert.AreEqual(1, store.GetJobSummary(directory.Id)!.Stats!.FailedMemberCount);
    }

    [TestMethod]
    public void ExistingDirectoryFile_ReportsCompletedProgressWithoutTransferredBytes()
    {
        var store = new EngineStateStore();
        var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\File.bin"), 128, ".bin");
        var other = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Other.bin"), 128, ".bin");
        var directory = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Root", [new DirectoryTransferEntry(target, []), new DirectoryTransferEntry(other, [])])));
        Register(store, directory);
        var child = new RemoteFileJob(target) { WorkflowId = directory.WorkflowId, FileSize = 128 };
        var pending = new RemoteFileJob(other) { WorkflowId = directory.WorkflowId, FileSize = 128 };
        directory.MaterializeDirectoryChildren([child, pending]);
        UpdateStructure(store, directory);
        Register(store, child, directory);
        Register(store, pending, directory);
        child.SetAlreadyExists();
        UpdateState(store, child);

        var payload = store.GetJobDetail(directory.Id)?.Payload as RemoteDirectoryJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(1, payload.Directory.SuccessfulFileCount);
        Assert.AreEqual(0, payload.Directory.BytesTransferred);
        Assert.AreEqual(50, payload.Directory.ProgressPercent);
        pending.BytesTransferred = 64;
        UpdateState(store, pending);
        payload = (RemoteDirectoryJobPayloadDto)store.GetJobDetail(directory.Id)!.Payload!;
        Assert.AreEqual(64, payload.Directory.BytesTransferred);
        Assert.AreEqual(75, payload.Directory.ProgressPercent);
        UpdateState(store, directory);
        Assert.AreEqual(75, ((RemoteDirectoryJobPayloadDto)store.GetJobDetail(directory.Id)!.Payload!).Directory.ProgressPercent);
        pending.SetDone();
        UpdateState(store, pending);
        payload = (RemoteDirectoryJobPayloadDto)store.GetJobDetail(directory.Id)!.Payload!;
        Assert.AreEqual(64, payload.Directory.BytesTransferred);
        Assert.AreEqual(100, payload.Directory.ProgressPercent);
    }

    [TestMethod]
    public void AlbumDirectoryProjection_UsesCurrentAttemptFilesAndTracksSupplementalSizeChanges()
    {
        static AlbumFile File(string path, long size, string extension) => new(
            new SongQuery { Title = path },
            SoulseekSearchAdapter.ToFileCandidate(new SearchResponse("Peer", 1, true, 100_000, 0, []),
                new Soulseek.File(1, path, size, extension, [])));
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Album = "Root" });
        var trackFile = File(@"Root\Track.flac", 10, ".flac");
        var folder = new AlbumFolder("Peer", "Root", [trackFile]);
        var track = album.EnsureTrackJobs(folder).Single();
        album.BeginDirectoryAttempt(new DirectoryTransferPlan("Root", [
            new DirectoryTransferEntry(trackFile.Candidate.Target, []),
        ]));
        Register(store, album);
        var payload = store.GetJobDetail(album.Id)?.Payload as AlbumJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(0, payload.Directory.FileCount);
        Assert.AreEqual(10, payload.Directory.TotalKnownBytes);

        album.MaterializeDirectoryChildren([track]);
        album.BeginDirectoryTransfer();
        UpdateStructure(store, album);
        Register(store, track, album);
        track.FileSize = 10;
        track.BytesTransferred = 5;
        UpdateState(store, track);
        var art = album.EnsureSupplementalTrackJobs([File(@"Artwork\Cover.jpg", 20, ".jpg")]).Single();
        UpdateStructure(store, album);
        Register(store, art, album);
        art.FileSize = 20;
        art.BytesTransferred = 10;
        UpdateState(store, art);
        payload = store.GetJobDetail(album.Id)?.Payload as AlbumJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.Directory.FileCount);
        Assert.AreEqual(30, payload.Directory.TotalKnownBytes);
        Assert.AreEqual(15, payload.Directory.BytesTransferred);
        Assert.AreEqual(50, payload.Directory.ProgressPercent);

        art.FileSize = null;
        UpdateState(store, art);
        payload = (AlbumJobPayloadDto)store.GetJobDetail(album.Id)!.Payload!;
        Assert.AreEqual(10, payload.Directory.TotalKnownBytes);
        Assert.AreEqual(1, payload.Directory.UnknownFileSizeCount);
        art.FileSize = 20;
        UpdateState(store, art);
        payload = (AlbumJobPayloadDto)store.GetJobDetail(album.Id)!.Payload!;
        Assert.AreEqual(30, payload.Directory.TotalKnownBytes);
        Assert.AreEqual(0, payload.Directory.UnknownFileSizeCount);

        track.SetDone();
        art.SetDone();
        UpdateState(store, track);
        UpdateState(store, art);
        var replacementFile = File(@"Larger\Cover.jpg", 100, ".jpg");
        album.BeginAlbumArtTransferAttempt(new AlbumFolder("Peer", "Larger", [replacementFile]), [replacementFile]);
        UpdateStructure(store, album);
        UpdateState(store, art);
        payload = store.GetJobDetail(album.Id)?.Payload as AlbumJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.Directory.AttemptNumber);
        Assert.AreEqual(1, payload.Directory.FileCount);
        Assert.AreEqual(100, payload.Directory.TotalKnownBytes);
        Assert.AreEqual(0, payload.Directory.TerminalFileCount);
        Assert.AreEqual(0, payload.Directory.SuccessfulFileCount);
        Assert.AreEqual(0, payload.Directory.BytesTransferred);
    }

    [TestMethod]
    public void SongPayload_IncludesDownloadSource()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        song.SetDone("C:/music/track.mp3", downloadSource: SongDownloadSource.Fallback);

        Register(store, song);

        var payload = store.GetJobDetail(song.Id)?.Payload as SongJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(ServerSongDownloadSource.Fallback, payload.DownloadSource);
    }

    [TestMethod]
    public void PublicJobEnumsPreserveTheMeaningOfEveryNamedCoreValue()
    {
        AssertMapping<JobSemanticRole, ServerJobRole>(ServerSnapshotMapper.ToServerJobRole);
        AssertMapping<JobLifecycleState, ServerJobLifecycleState>(ServerSnapshotMapper.ToServerJobLifecycleState);
        AssertMapping<JobActivityPhase, ServerJobActivityPhase>(ServerSnapshotMapper.ToServerJobActivityPhase);
        AssertMapping<JobTerminalOutcome, ServerJobTerminalOutcome>(ServerSnapshotMapper.ToServerJobTerminalOutcome);
        AssertMapping<SongDownloadSource, ServerSongDownloadSource>(ServerSnapshotMapper.ToServerSongDownloadSource);
        AssertMapping<JobSkipReason, ServerJobSkipReason>(ServerSnapshotMapper.ToServerJobSkipReason);
        AssertMapping<JobCancellationSource, ServerJobCancellationSource>(ServerSnapshotMapper.ToServerJobCancellationSource);
        AssertMapping<FolderRetrievalOutcome, ServerFolderRetrievalOutcome>(ServerSnapshotMapper.ToServerFolderRetrievalOutcome);
        foreach (JobFailureReason reason in Enum.GetValues<JobFailureReason>())
            Assert.AreEqual(reason == JobFailureReason.None ? null : reason.ToString(),
                ServerSnapshotMapper.ToServerFailureReason(reason)?.ToString());

        static void AssertMapping<TCore, TPublic>(Func<TCore, TPublic> map)
            where TCore : struct, Enum where TPublic : struct, Enum
        {
            foreach (TCore value in Enum.GetValues<TCore>())
                Assert.AreEqual(value.ToString(), map(value).ToString());
        }
    }

    [TestMethod]
    public void JobSummary_ExposesLifecycleActivityAndTerminalOutcome()
    {
        var store = new EngineStateStore();
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        song.UpdateActivity(JobActivityPhase.SearchRateLimited, until);

        Register(store, song);

        var summary = store.GetJobSummary(song.Id);
        Assert.IsNotNull(summary);
        Assert.AreEqual(ServerJobLifecycleState.Running, summary.LifecycleState);
        Assert.AreEqual(ServerJobActivityPhase.SearchRateLimited, summary.ActivityPhase);
        Assert.AreEqual(until, summary.ActivityUntilUtc);
        Assert.AreEqual(ServerJobTerminalOutcome.None, summary.TerminalOutcome);
    }

    [TestMethod]
    public void JobDiscoveryChanged_UpdatesSummaryDiscoveryCounts()
    {
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        JobSummaryDto? published = null;
        store.JobUpserted += summary => published = summary;

        Register(store, album);

        album.Discovery = new DiscoverySummary { RawResultCount = 123, LockedFileCount = 4 };
        DiscoveryChanged(store, album);

        var summary = store.GetJobSummary(album.Id);
        Assert.IsNotNull(summary);
        Assert.AreEqual(123, summary.DiscoveryRawResultCount);
        Assert.AreEqual(4, summary.DiscoveryLockedFileCount);
        Assert.IsNotNull(published);
        Assert.AreEqual(123, published.DiscoveryRawResultCount);
        Assert.AreEqual(4, published.DiscoveryLockedFileCount);
    }

    [TestMethod]
    public void SearchUpdates_CoalesceLargeResponsesAndPublishExactCompletion()
    {
        var store = new EngineStateStore();
        var search = new SearchJob("Artist Album");
        Register(store, search);
        var updates = new List<SearchStateDto>();
        store.SearchUpdated += updates.Add;

        search.Session.AddResponse(new SearchResponse("User", 1, true, 1000, 0,
            Enumerable.Range(0, 2_000).Select(i => new Soulseek.File(1, $@"Artist\Album\Track {i}.mp3", 100, ".mp3")).ToArray()));
        search.Discovery = new DiscoverySummary { RawResultCount = search.ResultCount };
        DiscoveryChanged(store, search);
        search.Session.Complete();
        UpdateState(store, search);

        Assert.IsTrue(updates.Count is > 0 and <= 10, $"A single response generated {updates.Count} search updates.");
        Assert.IsTrue(updates.Any(update => !update.IsComplete), "Clients must see progress before completion.");
        Assert.IsTrue(updates[^1].IsComplete);
        Assert.AreEqual(2_000, updates[^1].ResultCount);
        Assert.AreEqual(search.Revision, updates[^1].Revision);
        var payload = store.GetJobDetail(search.Id)?.Payload as SearchJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2_000, payload.ResultCount);
        Assert.IsTrue(payload.IsComplete);
    }

    [TestMethod]
    public void GetJobs_FiltersByLifecycleAndTerminalOutcome()
    {
        var store = new EngineStateStore();
        var running = new SongJob(new SongQuery { Title = "Running" });
        var done = new SongJob(new SongQuery { Title = "Done" });
        var failed = new SongJob(new SongQuery { Title = "Failed" });
        running.UpdateActivity(JobActivityPhase.Downloading);
        done.SetDone();
        failed.Fail(JobFailureReason.Other);

        Register(store, running);
        Register(store, done);
        Register(store, failed);

        var runningJobs = store.GetJobs(new JobQuery(
            ServerJobLifecycleState.Running,
            TerminalOutcome: null,
            Kind: null,
            WorkflowId: null,
            IncludeAll: true));
        CollectionAssert.AreEquivalent(new[] { running.Id }, runningJobs.Select(job => job.JobId).ToArray());

        var failedJobs = store.GetJobs(new JobQuery(
            ServerJobLifecycleState.Terminal,
            ServerJobTerminalOutcome.Failed,
            Kind: null,
            WorkflowId: null,
            IncludeAll: true));
        CollectionAssert.AreEquivalent(new[] { failed.Id }, failedJobs.Select(job => job.JobId).ToArray());
    }

    [TestMethod]
    public void GroupPayload_IncludesSongOutcomeCounts()
    {
        var store = new EngineStateStore();
        var group = new SongGroupsJob(new SongQuery { Artist = "Artist" });
        var s1 = new SongJob(new SongQuery { Title = "One" }); s1.SetDone();
        var s2 = new SongJob(new SongQuery { Title = "Two" }); s2.Fail(JobFailureReason.Other);
        var s3 = new SongJob(new SongQuery { Title = "Three" }); s3.UpdateActivity(JobActivityPhase.Downloading);
        group.Songs = [s1, s2, s3];

        Register(store, group);

        var payload = store.GetJobDetail(group.Id)?.Payload as SongGroupsJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(3, payload.SongCount);
        Assert.AreEqual(2, payload.CompletedSongCount);
        Assert.AreEqual(1, payload.SucceededSongCount);
        Assert.AreEqual(1, payload.FailedSongCount);
        var firstStats = store.GetJobDetail(group.Id)!.Summary.Stats;
        Assert.IsNotNull(firstStats);
        Assert.AreEqual(3, firstStats.MemberCount);
        Assert.AreEqual(2, firstStats.CompletedMemberCount);
        Assert.AreEqual(1, firstStats.FailedMemberCount);

        // The group snapshot still contains the original running child.
        // A child event must update the group projection without rebuilding
        // that nested snapshot during each detail read.
        s3.SetDone();
        UpdateState(store, s3);
        payload = store.GetJobDetail(group.Id)?.Payload as SongGroupsJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(3, payload.SongCount);
        Assert.AreEqual(3, payload.CompletedSongCount);
        Assert.AreEqual(2, payload.SucceededSongCount);
        Assert.AreEqual(1, payload.FailedSongCount);
        Assert.AreEqual(3, store.GetJobDetail(group.Id)!.Summary.Stats!.CompletedMemberCount);
    }

    [TestMethod]
    public void JobListPayload_IncludesDirectChildOutcomeCounts()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var j1 = new SongJob(new SongQuery { Title = "One" }); j1.SetDone();
        var j2 = new SongJob(new SongQuery { Title = "Two" }); j2.Fail(JobFailureReason.Other);
        var j3 = new SongJob(new SongQuery { Title = "Three" }); j3.UpdateActivity(JobActivityPhase.Searching);
        list.Add(j1);
        list.Add(j2);
        list.Add(j3);

        Register(store, list);

        var payload = store.GetJobDetail(list.Id)?.Payload as JobListPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(3, payload.Count);
        Assert.AreEqual(1, payload.ActiveJobCount);
        Assert.AreEqual(2, payload.CompletedJobCount);
        Assert.AreEqual(1, payload.SucceededJobCount);
        Assert.AreEqual(1, payload.FailedJobCount);
        var stats = store.GetJobDetail(list.Id)!.Summary.Stats;
        Assert.IsNotNull(stats);
        Assert.AreEqual(3, stats.MemberCount);
        Assert.AreEqual(2, stats.CompletedMemberCount);
        Assert.AreEqual(1, stats.FailedMemberCount);
    }

    [TestMethod]
    public void JobListProgress_FollowsAutoProcessedExtractionIntoItsResult()
    {
        var store = new EngineStateStore();
        var extract = new ExtractJob("source.csv", InputType.CSV)
        {
            AutoProcessResult = true,
        };
        var result = new SongJob(new SongQuery { Title = "From source" })
        {
            WorkflowId = extract.WorkflowId,
        };
        var list = new JobList("imports", [extract])
        {
            WorkflowId = extract.WorkflowId,
        };
        Register(store, list);
        Register(store, extract, list);

        extract.SetDone();
        UpdateState(store, extract);
        var waiting = store.GetJobDetail(list.Id)?.Payload as JobListPayloadDto;
        Assert.IsNotNull(waiting);
        Assert.AreEqual(1, waiting.ActiveJobCount);
        Assert.AreEqual(0, waiting.CompletedJobCount);

        extract.Result = result;
        ResultCreated(store, extract, result);
        result.UpdateActivity(JobActivityPhase.Downloading);
        UpdateState(store, result);
        var running = store.GetJobDetail(list.Id)?.Payload as JobListPayloadDto;
        Assert.IsNotNull(running);
        Assert.AreEqual(1, running.ActiveJobCount);
        Assert.AreEqual(0, running.SucceededJobCount);

        result.Fail(JobFailureReason.Other);
        UpdateState(store, result);
        var failed = store.GetJobDetail(list.Id)?.Payload as JobListPayloadDto;
        Assert.IsNotNull(failed);
        Assert.AreEqual(0, failed.ActiveJobCount);
        Assert.AreEqual(1, failed.CompletedJobCount);
        Assert.AreEqual(0, failed.SucceededJobCount);
        Assert.AreEqual(1, failed.FailedJobCount);

        var retained = ServerSnapshotMapper.ToJobListPayloadDto(
            (JobListSnapshotPayload)Snapshot(list).Payload);
        Assert.AreEqual(failed, retained);
    }

    [TestMethod]
    public void JobListProgress_FollowsNestedExtractionResultChanges()
    {
        var store = new EngineStateStore();
        var outer = new ExtractJob("outer", InputType.String);
        var inner = new ExtractJob("inner", InputType.String)
        {
            WorkflowId = outer.WorkflowId,
        };
        var song = new SongJob(new SongQuery { Title = "Nested" })
        {
            WorkflowId = outer.WorkflowId,
        };
        var list = new JobList("nested", [outer])
        {
            WorkflowId = outer.WorkflowId,
        };
        Register(store, list);
        Register(store, outer, list);

        outer.Result = inner;
        outer.SetDone();
        ResultCreated(store, outer, inner);
        inner.Result = song;
        inner.SetDone();
        ResultCreated(store, inner, song);
        var running = store.GetJobDetail(list.Id)?.Payload as JobListPayloadDto;
        Assert.IsNotNull(running);
        Assert.AreEqual(1, running.ActiveJobCount);

        song.SetDone();
        UpdateState(store, song);
        var completed = store.GetJobDetail(list.Id)?.Payload as JobListPayloadDto;
        Assert.IsNotNull(completed);
        Assert.AreEqual(1, completed.CompletedJobCount);
        Assert.AreEqual(1, completed.SucceededJobCount);
    }

    [TestMethod]
    public void JobListSummary_UsesCoreRunningState()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var child = new SongJob(new SongQuery { Title = "One" });
        list.Add(child);

        Register(store, list);
        Register(store, child, list);

        list.UpdateActivity(JobActivityPhase.RunningChildren);
        UpdateState(store, list);

        var summary = store.GetJobSummary(list.Id);
        Assert.IsNotNull(summary);
        Assert.AreEqual(ServerJobLifecycleState.Running, summary.LifecycleState);
        Assert.AreEqual(ServerJobActivityPhase.RunningChildren, summary.ActivityPhase);
        Assert.AreEqual(ServerJobTerminalOutcome.None, summary.TerminalOutcome);
    }

    [TestMethod]
    public void WorkflowSummary_TracksRootsTitleAndCountsAcrossJobUpdates()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var done = new SongJob(new SongQuery { Title = "Done" }) { WorkflowId = list.WorkflowId };
        var failed = new SongJob(new SongQuery { Title = "Failed" }) { WorkflowId = list.WorkflowId };

        Register(store, list);
        Register(store, done, list);
        Register(store, failed, list);

        var initial = store.GetWorkflowSummary(list.WorkflowId);
        Assert.IsNotNull(initial);
        Assert.AreEqual("batch", initial.Title);
        Assert.AreEqual(1, initial.RootJobCount);
        Assert.AreEqual(ServerWorkflowState.Active, initial.State);
        Assert.AreEqual(3, initial.ActiveJobCount);
        Assert.AreEqual(0, initial.CompletedJobCount);
        Assert.AreEqual(0, initial.FailedJobCount);

        done.SetDone();
        UpdateState(store, done);
        failed.Fail(JobFailureReason.Other);
        UpdateState(store, failed);
        list.SetDone();
        UpdateState(store, list);

        var terminal = store.GetWorkflowSummary(list.WorkflowId);
        Assert.IsNotNull(terminal);
        Assert.AreEqual(ServerWorkflowState.Failed, terminal.State);
        Assert.AreEqual(0, terminal.ActiveJobCount);
        Assert.AreEqual(3, terminal.CompletedJobCount);
        Assert.AreEqual(1, terminal.FailedJobCount);
    }

    [TestMethod]
    public void WorkflowSummaryCache_MatchesBruteForceSnapshotAcrossMutations()
    {
        var store = new EngineStateStore();
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = true,
        };
        var list = new JobList("batch") { WorkflowId = extract.WorkflowId };
        var done = new SongJob(new SongQuery { Title = "Done" }) { WorkflowId = extract.WorkflowId };
        var failed = new SongJob(new SongQuery { Title = "Failed" }) { WorkflowId = extract.WorkflowId };
        var projected = new SongJob(new SongQuery { Title = "Projected" }) { WorkflowId = extract.WorkflowId };
        list.Add(done);
        list.Add(failed);
        list.Add(projected);
        extract.Result = list;

        Register(store, extract);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        ResultCreated(store, extract, list);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        Register(store, list);
        Register(store, done, list);
        Register(store, failed, list);
        Register(store, projected, list);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        store.SetSourceJob(done.Id, extract.Id);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        list.ItemName = " ";
        UpdateState(store, list);
        done.ItemName = "Named child";
        UpdateState(store, done);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);
        InvokePrivate(store, "OnJobStateChanged", new JobStateChangedChange(
            2, DateTimeOffset.UtcNow, Execution(done) with { DisplayId = int.MaxValue }));
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);
        list.ItemName = "Renamed batch";
        UpdateState(store, list);
        Register(store, done);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);
        Register(store, done, list);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        done.SetDone();
        UpdateState(store, done);
        failed.Fail(JobFailureReason.Other);
        UpdateState(store, failed);
        ExecutionCompleted(store, projected);
        list.SetDone();
        UpdateState(store, list);
        extract.SetDone();
        UpdateState(store, extract);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);
    }

    [TestMethod]
    public void AlbumGroupsPayload_CountsProducedAlbumDescendants()
    {
        var store = new EngineStateStore();
        var group = new AlbumGroupsJob(new AlbumQuery { Artist = "Artist" });
        var list = new JobList("albums");
        var firstAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "One" });
        var secondAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Two" });
        list.Add(firstAlbum);
        list.Add(secondAlbum);

        Register(store, group);
        Register(store, list, group);
        Register(store, firstAlbum, list);
        Register(store, secondAlbum, list);

        var payload = store.GetJobDetail(group.Id)?.Payload as AlbumGroupsJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.ResultCount);
        Assert.AreEqual(0, payload.CompletedAlbumCount);

        firstAlbum.SetDone();
        UpdateState(store, firstAlbum);
        payload = store.GetJobDetail(group.Id)?.Payload as AlbumGroupsJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(1, payload.CompletedAlbumCount);
        Assert.AreEqual(1, payload.SucceededAlbumCount);
        Assert.AreEqual(0, payload.FailedAlbumCount);

        secondAlbum.Fail(JobFailureReason.Other);
        UpdateState(store, secondAlbum);
        payload = store.GetJobDetail(group.Id)?.Payload as AlbumGroupsJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.CompletedAlbumCount);
        Assert.AreEqual(1, payload.SucceededAlbumCount);
        Assert.AreEqual(1, payload.FailedAlbumCount);
        var stats = store.GetJobDetail(group.Id)!.Summary.Stats;
        Assert.IsNotNull(stats);
        Assert.AreEqual(2, stats.MemberCount);
        Assert.AreEqual(2, stats.CompletedMemberCount);
        Assert.AreEqual(1, stats.FailedMemberCount);
    }

    [TestMethod]
    public void AlbumDetail_ReportsChildCountWithoutInliningTracks()
    {
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        var song1 = new SongJob(new SongQuery { Title = "One" });
        var song2 = new SongJob(new SongQuery { Title = "Two" });
        song1.UpdateActivity(JobActivityPhase.Downloading);
        song2.UpdateActivity(JobActivityPhase.Downloading);

        Register(store, album);
        Register(store, song1, album);
        Register(store, song2, album);

        var detail = store.GetJobDetail(album.Id);
        Assert.IsNotNull(detail);
        Assert.IsInstanceOfType<AlbumJobPayloadDto>(detail.Payload);
        Assert.AreEqual(2, detail.ChildCount);
        CollectionAssert.AreEquivalent(
            new[] { song1.Id, song2.Id },
            store.GetJobs(new JobQuery(null, null, null, null, IncludeAll: true, ParentJobId: album.Id))
                .Select(job => job.JobId)
                .ToArray());
    }


    [TestMethod]
    public void ExtractPayload_ExposesScalarSemanticResultRelationship()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = true,
            Result = list,
        };
        list.WorkflowId = extract.WorkflowId;
        list.Add(new SongJob(new SongQuery { Artist = "Artist", Title = "One" }) { WorkflowId = list.WorkflowId });

        Register(store, extract);

        var payload = store.GetJobDetail(extract.Id)?.Payload as ExtractJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual("input.csv", payload.Input);
        Assert.AreEqual(nameof(InputType.CSV), payload.InputType);
        Assert.AreEqual(list.Id, payload.ResultJobId);
    }

    [TestMethod]
    public void AutoProcessedExtractResult_GetsDisplayIdBeforeRegistration()
    {
        var store = new EngineStateStore();
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = true,
        };
        var result = new JobList("batch") { WorkflowId = extract.WorkflowId };
        extract.Result = result;

        Register(store, extract);
        ResultCreated(store, extract, result);

        var resultSummary = store.GetJobSummary(result.Id);
        Assert.IsNotNull(resultSummary);
        Assert.AreNotEqual(0, resultSummary.DisplayId);
    }

    private static void Register(EngineStateStore store, Job job, Job? parent = null, bool flush = true)
    {
        job.EnsureDisplayId();
        parent?.EnsureDisplayId();
        typeof(EngineStateStore)
            .GetMethod("OnJobRegistered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobRegisteredChange(1, DateTimeOffset.UtcNow, Snapshot(job), parent?.Id, null)]);
        if (flush) store.FlushStateUpdates();
    }

    private static void UpdateStructure(EngineStateStore store, Job job)
        => InvokePrivate(store, "OnJobStructureChanged", new JobStructureChangedChange(
            1, DateTimeOffset.UtcNow, Snapshot(job)));

    private static void UpdateState(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobStateChangedChange(1, DateTimeOffset.UtcNow, Execution(job))]);
        store.FlushStateUpdates();
    }

    private static void DiscoveryChanged(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobDiscoveryChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobDiscoveryChangedChange(1, DateTimeOffset.UtcNow, Execution(job))]);
        store.FlushStateUpdates();
    }

    private static void ResultCreated(EngineStateStore store, ExtractJob job, Job result)
    {
        job.EnsureDisplayId();
        result.EnsureDisplayId();
        typeof(EngineStateStore)
            .GetMethod("OnJobResultCreated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobResultCreatedChange(1, DateTimeOffset.UtcNow, Snapshot(job), Snapshot(result))]);
        store.FlushStateUpdates();
    }

    private static void ExecutionCompleted(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobExecutionCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobExecutionCompletedChange(1, DateTimeOffset.UtcNow, Execution(job))]);
        store.FlushStateUpdates();
    }

    private static void DownloadStateChanged(EngineStateStore store, SongJob song, TransferStates state)
    {
        typeof(EngineStateStore)
            .GetMethod("OnDownloadStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new DownloadStateChangedChange(1, DateTimeOffset.UtcNow, Reference(song), CoreSnapshotFactory.CreateFileDownloadState(song), Transfer(song, state))]);
        store.FlushStateUpdates();
    }

    private static void InvokePrivate(EngineStateStore store, string methodName, CoreChange change)
    {
        typeof(EngineStateStore)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [change]);
        store.FlushStateUpdates();
    }


    private static JobReferenceSnapshot Reference(Job job)
        => CoreSnapshotFactory.CreateJobReference(job, revision: 1);

    private static JobExecutionSnapshot Execution(Job job)
        => CoreSnapshotFactory.CreateExecution(job, revision: 1);

    private static JobSnapshot Snapshot(Job job)
        => CoreSnapshotFactory.CreateJob(job, revision: 1);

    private static TransferSnapshot Transfer(SongJob song, TransferStates state)
    {
        var response = new SearchResponse("user", 1, true, 100_000, 0, []);
        var file = new Soulseek.File(1, $"{song.Query.Title}.mp3", song.FileSize is > 0 ? song.FileSize.Value : 100, ".mp3");
        var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
        return CoreSnapshotFactory.CreateDownloadTransfer(
            Guid.NewGuid(),
            song,
            candidate,
            $"C:/downloads/{song.Query.Title}.mp3",
            revision: 1,
            state: state.ToString(),
            bytesTransferred: song.BytesTransferred,
            totalBytes: song.FileSize is > 0 ? song.FileSize.Value : file.Size,
            attemptCount: 0);
    }

    private static void AssertWorkflowSummaryMatchesBruteForceSnapshot(EngineStateStore store, Guid workflowId)
    {
        var cached = store.GetWorkflowSummary(workflowId);
        var jobs = store.GetJobs(new JobQuery(null, null, null, workflowId, IncludeAll: true));

        Assert.IsNotNull(cached);

        var expected = BuildBruteForceWorkflowSummary(workflowId, jobs);
        Assert.AreEqual(expected.WorkflowId, cached.WorkflowId);
        Assert.AreEqual(expected.Title, cached.Title);
        Assert.AreEqual(expected.State, cached.State);
        Assert.AreEqual(expected.RootJobCount, cached.RootJobCount);
        Assert.AreEqual(expected.ActiveJobCount, cached.ActiveJobCount);
        Assert.AreEqual(expected.FailedJobCount, cached.FailedJobCount);
        Assert.AreEqual(expected.CompletedJobCount, cached.CompletedJobCount);
    }

    private static WorkflowSummaryDto BuildBruteForceWorkflowSummary(Guid workflowId, IReadOnlyList<JobSummaryDto> jobs)
    {
        var ordered = jobs.OrderBy(job => job.DisplayId).ToList();
        string title = ordered.FirstOrDefault(job => !string.IsNullOrWhiteSpace(job.ItemName))?.ItemName
            ?? ordered.First().QueryText
            ?? ordered.First().Kind.ToWireString();

        int active = ordered.Count(job => job.LifecycleState != ServerJobLifecycleState.Terminal);
        int failed = ordered.Count(IsFailed);
        int completed = ordered.Count - active;
        var state = active > 0 ? ServerWorkflowState.Active
            : failed > 0 ? ServerWorkflowState.Failed
            : ServerWorkflowState.Completed;

        return new WorkflowSummaryDto(
            workflowId,
            title,
            state,
            ordered.Count(job => job.ParentJobId == null),
            active,
            failed,
            completed);
    }

    private static bool IsFailed(JobSummaryDto job)
        => job.TerminalOutcome is ServerJobTerminalOutcome.Failed
            or ServerJobTerminalOutcome.Cancelled
            or ServerJobTerminalOutcome.PartialSuccess
            || (job.TerminalOutcome == ServerJobTerminalOutcome.Skipped
                && job.SkipReason != ServerJobSkipReason.AlreadyExists);

    private static void AssertClientMatchesProjection(
        EngineStateStore server,
        DaemonClientStore client,
        Guid workflowId)
    {
        var expected = server.GetWorkflowSnapshot(workflowId);
        Assert.AreEqual(expected.Position, client.GetPosition(expected.Scope));
        CollectionAssert.AreEqual(
            expected.Workflows.Select(row => row.Summary).ToArray(),
            client.GetWorkflows().Where(row => row.WorkflowId == workflowId).ToArray());
        CollectionAssert.AreEqual(
            expected.Jobs.Select(row => row.ToSummary()).OrderBy(row => row.JobId).ToArray(),
            client.GetWorkflowJobs(workflowId).OrderBy(row => row.JobId).ToArray());
        CollectionAssert.AreEqual(
            expected.Transfers.OrderBy(row => row.TransferId).ToArray(),
            client.GetTransfers()
                .Where(row => row.Identity.WorkflowId == workflowId)
                .OrderBy(row => row.TransferId)
                .ToArray());
        foreach (var search in expected.Searches)
            Assert.AreEqual(search, client.GetSearchState(search.JobId));
    }
}
