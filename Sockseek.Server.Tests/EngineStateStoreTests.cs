using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Api;
using Sockseek.Server;
using Soulseek;

namespace Tests.Server;

[TestClass]
public class EngineStateStoreTests
{
    [TestMethod]
    public void SnapshotAndEverySubsequentDelta_MatchCurrentServerProjection()
    {
        var server = new EngineStateStore();
        var client = new DaemonClientStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var scope = StateStreamScopeDto.Workflow(song.WorkflowId);
        server.ReserveWorkflowStream(song.WorkflowId);
        client.ApplySnapshot(server.GetWorkflowSnapshot(song.WorkflowId));
        server.StateBatchPublished += batch =>
        {
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
        store.StateBatchPublished += batch =>
        {
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

        var scope = StateStreamScopeDto.Workflow(first.WorkflowId);
        client.ApplySnapshot(server.GetWorkflowSnapshot(first.WorkflowId));
        var statuses = new List<DaemonClientApplyStatus>();
        server.StateBatchPublished += batch =>
        {
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
        }
        finally
        {
            releaseFirstUpsert.Set();
            await Task.WhenAll(firstCompletion, secondCompletion).WaitAsync(TimeSpan.FromSeconds(5));
        }

        lock (statuses)
        {
            CollectionAssert.AreEqual(
                new[] { DaemonClientApplyStatus.Applied, DaemonClientApplyStatus.Applied },
                statuses.ToArray());
        }
        AssertClientMatchesProjection(server, client, first.WorkflowId);
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
            1, startedAt, Snapshot(song), transfer));
        InvokePrivate(store, "OnTransferAttemptStarted", new TransferAttemptStartedChange(
            2,
            startedAt,
            Snapshot(song),
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
            Snapshot(song),
            transfer,
            attemptId,
            1,
            2));
        var completed = store.GetLiveTransferDetail(transfer.Id);
        Assert.IsNotNull(completed);
        Assert.AreEqual("Completed", completed.LatestAttempt?.State);
        Assert.AreEqual(completedAt, completed.LatestAttempt?.CompletedAtUtc);

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
        store.StateBatchPublished += batches.Add;

        store.UpdateUploadTransfer(upload);
        int afterFirstUpdate = batches.Count;
        store.UpdateUploadTransfer(upload);

        Assert.AreEqual(afterFirstUpdate, batches.Count, "An identical hydration must not emit a duplicate delta.");
        Assert.AreEqual(transferId, store.GetDaemonSnapshot().Transfers.Single().TransferId);

        store.RemoveTerminalTransfer(transferId);
        Assert.AreEqual(0, store.GetDaemonSnapshot().Transfers.Count);
        Assert.IsTrue(batches.Last().State.RemovedTransferIds.Contains(transferId));
    }

    [TestMethod]
    public void TerminalDownload_RemainsLiveUntilPersistenceOwnerExplicitlyRetiresIt()
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
        store.StateBatchPublished += batches.Add;

        typeof(EngineStateStore)
            .GetMethod("OnTerminalTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [terminal, DateTimeOffset.UtcNow]);

        Assert.IsNotNull(store.GetLiveTransfer(transferId));
        Assert.IsFalse(batches.Last().State.RemovedTransferIds.Contains(transferId));

        store.RemoveTerminalTransfer(transferId);
        Assert.IsNull(store.GetLiveTransfer(transferId));
        Assert.IsTrue(batches.Last().State.RemovedTransferIds.Contains(transferId));
    }

    [TestMethod]
    public void WorkflowRetirementPublishesFinalRemovalAndReleasesAllWorkflowIndexes()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var parent = new JobList("workflow") { WorkflowId = song.WorkflowId };
        parent.Add(song);
        var batches = new List<StateUpdateBatchDto>();
        store.StateBatchPublished += batches.Add;

        Register(store, parent);
        Register(store, song, parent);
        Guid transferId = Guid.NewGuid();
        InvokePrivate(store, "OnDownloadStateChanged", new DownloadStateChangedChange(
            3,
            DateTimeOffset.UtcNow,
            Snapshot(song),
            Transfer(song, TransferStates.InProgress) with { Id = transferId }));
        Guid priorEpoch = store.GetWorkflowSnapshot(song.WorkflowId).Position.Epoch;

        InvokePrivate(store, "OnWorkflowRetired", new WorkflowRetiredChange(
            4,
            DateTimeOffset.UtcNow,
            song.WorkflowId));

        Assert.IsNull(store.GetJobSummary(song.Id));
        Assert.IsNull(store.GetJobSummary(parent.Id));
        Assert.IsNull(store.GetWorkflowSummary(song.WorkflowId));
        Assert.IsNull(store.GetLiveTransfer(transferId));
        Assert.AreEqual(
            new EngineStateStoreRetainedWorkflowCounts(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            store.RetainedWorkflowStateCounts);

        var removal = batches.Last(batch =>
            batch.State.RemovedWorkflowIds.Contains(song.WorkflowId));
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
        Assert.IsNull(payload.SourceUsername);
        Assert.IsNull(payload.SourceFolderPath);
        Assert.AreEqual("planned", payload.Directory.Phase);
        Assert.AreEqual(1, payload.Directory.AttemptNumber);
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
    public void AggregatePayload_IncludesSongOutcomeCounts()
    {
        var store = new EngineStateStore();
        var aggregate = new AggregateJob(new SongQuery { Artist = "Artist" });
        var s1 = new SongJob(new SongQuery { Title = "One" }); s1.SetDone();
        var s2 = new SongJob(new SongQuery { Title = "Two" }); s2.Fail(JobFailureReason.Other);
        var s3 = new SongJob(new SongQuery { Title = "Three" }); s3.UpdateActivity(JobActivityPhase.Downloading);
        aggregate.Songs.Add(s1);
        aggregate.Songs.Add(s2);
        aggregate.Songs.Add(s3);

        Register(store, aggregate);

        var payload = store.GetJobDetail(aggregate.Id)?.Payload as AggregateJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(3, payload.SongCount);
        Assert.AreEqual(2, payload.CompletedSongCount);
        Assert.AreEqual(1, payload.SucceededSongCount);
        Assert.AreEqual(1, payload.FailedSongCount);
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
    public void AlbumAggregatePayload_CountsProducedAlbumDescendants()
    {
        var store = new EngineStateStore();
        var aggregate = new AlbumAggregateJob(new AlbumQuery { Artist = "Artist" });
        var list = new JobList("albums");
        var firstAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "One" });
        var secondAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Two" });
        list.Add(firstAlbum);
        list.Add(secondAlbum);

        Register(store, aggregate);
        Register(store, list, aggregate);
        Register(store, firstAlbum, list);
        Register(store, secondAlbum, list);

        var payload = store.GetJobDetail(aggregate.Id)?.Payload as AlbumAggregateJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.ResultCount);
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

    private static void Register(EngineStateStore store, Job job, Job? parent = null)
    {
        job.EnsureDisplayId();
        parent?.EnsureDisplayId();
        typeof(EngineStateStore)
            .GetMethod("OnJobRegistered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobRegisteredChange(1, DateTimeOffset.UtcNow, Snapshot(job), parent?.Id, null)]);
    }

    private static void UpdateState(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobStateChangedChange(1, DateTimeOffset.UtcNow, Snapshot(job))]);
    }

    private static void DiscoveryChanged(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobDiscoveryChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobDiscoveryChangedChange(1, DateTimeOffset.UtcNow, Snapshot(job))]);
    }

    private static void ResultCreated(EngineStateStore store, ExtractJob job, Job result)
    {
        job.EnsureDisplayId();
        result.EnsureDisplayId();
        typeof(EngineStateStore)
            .GetMethod("OnJobResultCreated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobResultCreatedChange(1, DateTimeOffset.UtcNow, Snapshot(job), Snapshot(result))]);
    }

    private static void ExecutionCompleted(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobExecutionCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobExecutionCompletedChange(1, DateTimeOffset.UtcNow, Snapshot(job))]);
    }

    private static void DownloadStateChanged(EngineStateStore store, SongJob song, TransferStates state)
    {
        typeof(EngineStateStore)
            .GetMethod("OnDownloadStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new DownloadStateChangedChange(1, DateTimeOffset.UtcNow, Snapshot(song), Transfer(song, state))]);
    }

    private static void InvokePrivate(EngineStateStore store, string methodName, CoreChange change)
        => typeof(EngineStateStore)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [change]);

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
