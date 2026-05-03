using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Server;

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
        song.UpdateState(JobState.Downloading);

        Register(store, song);

        var payload = store.GetJobDetail(song.Id)?.Payload as SongJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(25, payload.BytesTransferred);
        Assert.AreEqual(100, payload.TotalBytes);
        Assert.AreEqual(25d, payload.ProgressPercent);
    }

    [TestMethod]
    public void AggregatePayload_IncludesSongOutcomeCounts()
    {
        var store = new EngineStateStore();
        var aggregate = new AggregateJob(new SongQuery { Artist = "Artist" });
        var s1 = new SongJob(new SongQuery { Title = "One" }); s1.UpdateState(JobState.Done);
        var s2 = new SongJob(new SongQuery { Title = "Two" }); s2.Fail(FailureReason.Other);
        var s3 = new SongJob(new SongQuery { Title = "Three" }); s3.UpdateState(JobState.Downloading);
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
        var j1 = new SongJob(new SongQuery { Title = "One" }); j1.UpdateState(JobState.Done);
        var j2 = new SongJob(new SongQuery { Title = "Two" }); j2.Fail(FailureReason.Other);
        var j3 = new SongJob(new SongQuery { Title = "Three" }); j3.UpdateState(JobState.Searching);
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

    private static void Register(EngineStateStore store, Job job, Job? parent = null)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobRegistered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [job, parent]);
    }
}
