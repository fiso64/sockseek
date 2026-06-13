using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Api;
using Sockseek.Server;
using Soulseek;

namespace Tests.Server;

[TestClass]
public class EngineStateStoreTests
{
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
        Assert.AreEqual(25, payload.BytesTransferred);
        Assert.AreEqual(100, payload.TotalBytes);
        Assert.AreEqual(25d, payload.ProgressPercent);
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
    public void AlbumDetail_TracksReflectCurrentTransferState()
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

        // Fire transfer state after registration — the cached record payload won't have it
        DownloadStateChanged(store, song1, TransferStates.InProgress);
        DownloadStateChanged(store, song2, TransferStates.Queued | TransferStates.Remotely);

        var tracks = (store.GetJobDetail(album.Id)?.Payload as AlbumJobPayloadDto)?.Tracks;
        Assert.IsNotNull(tracks);
        Assert.AreEqual(2, tracks.Count);
        Assert.AreEqual(TransferStates.InProgress.ToString(), tracks[0].TransferState);
        Assert.AreEqual((TransferStates.Queued | TransferStates.Remotely).ToString(), tracks[1].TransferState);
    }


    [TestMethod]
    public void ResultDraft_RoundTripsSourceMutationProvenance()
    {
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
        {
            ItemNumber = 2,
            LineNumber = 4,
            SourceMutation = SourceMutation.ClearCsvRow("input.csv", 4, 2, 3),
        };
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = false,
            Result = album,
        };

        Register(store, extract);

        var payload = store.GetJobDetail(extract.Id)?.Payload as ExtractJobPayloadDto;
        Assert.IsNotNull(payload);
        var draft = payload.ResultDraft as AlbumJobDraftDto;
        Assert.IsNotNull(draft);
        Assert.IsNotNull(draft.Provenance);
        Assert.AreEqual(2, draft.Provenance.ItemNumber);
        Assert.AreEqual(4, draft.Provenance.LineNumber);
        Assert.AreEqual(nameof(SourceMutationKind.ClearCsvRow), draft.Provenance.SourceMutation?.Kind);
        Assert.AreEqual("input.csv", draft.Provenance.SourceMutation?.Source);
        Assert.AreEqual(3, draft.Provenance.SourceMutation?.CsvColumnCount);

        var roundTripped = JobRequestMapper.CreateJob(draft);
        Assert.AreEqual(2, roundTripped.ItemNumber);
        Assert.AreEqual(4, roundTripped.LineNumber);
        Assert.AreEqual(SourceMutationKind.ClearCsvRow, roundTripped.SourceMutation?.Kind);
        Assert.AreEqual("input.csv", roundTripped.SourceMutation?.Source);
        Assert.AreEqual(3, roundTripped.SourceMutation?.CsvColumnCount);
    }

    private static void Register(EngineStateStore store, Job job, Job? parent = null)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobRegistered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [job, parent]);
    }

    private static void UpdateState(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [job]);
    }

    private static void DownloadStateChanged(EngineStateStore store, SongJob song, TransferStates state)
    {
        typeof(EngineStateStore)
            .GetMethod("OnDownloadStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [song, state]);
    }
}
