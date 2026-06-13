using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class EngineEventDtoAdapterTests
{
    [TestMethod]
    public void Attach_MapsDownloadProgressToSharedServerEventDto()
    {
        var events = new EngineEvents();
        var published = new List<(string Type, object Payload)>();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
        new EngineEventDtoAdapter(SummaryFor, (type, payload) => published.Add((type, payload))).Attach(events);

        Raise(events, "RaiseDownloadProgress", song, 42L, 100L);

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("download.progress", published[0].Type);
        var progress = (DownloadProgressEventDto)published[0].Payload;
        Assert.AreEqual(song.Id, progress.JobId);
        Assert.AreEqual(42, progress.BytesTransferred);
        Assert.AreEqual(100, progress.TotalBytes);
    }

    [TestMethod]
    public void Attach_MapsSongSearchingToSharedServerEventDto()
    {
        var events = new EngineEvents();
        var published = new List<(string Type, object Payload)>();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title", Album = "Album" });
        new EngineEventDtoAdapter(SummaryFor, (type, payload) => published.Add((type, payload))).Attach(events);

        song.UpdateActivity(JobActivityPhase.Searching);
        Raise(events, "RaiseJobStateChanged", song);

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("song.searching", published[0].Type);
        var searching = (SongSearchingEventDto)published[0].Payload;
        Assert.AreEqual(song.Id, searching.JobId);
        Assert.AreEqual("Artist", searching.Query.Artist);
        Assert.AreEqual("Title", searching.Query.Title);
        Assert.AreEqual("Album", searching.Query.Album);
    }

    private static JobSummaryDto SummaryFor(Job job)
        => new(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            EngineStateStore.GetJobKind(job),
            EngineStateStore.ToServerJobLifecycleState(job.LifecycleState),
            EngineStateStore.ToServerJobActivityPhase(job.ActivityPhase),
            job.ActivityUntilUtc,
            EngineStateStore.ToServerJobTerminalOutcome(job.TerminalOutcome),
            job.ItemName,
            job.ToString(noInfo: true),
            EngineStateStore.ToServerFailureReason(job.FailureReason),
            job.FailureMessage,
            null,
            null,
            null,
            job.Discovery?.ResultCount,
            job.Discovery?.LockedFileCount,
            [],
            []);

    private static void Raise(EngineEvents events, string methodName, params object[] args)
    {
        var method = typeof(EngineEvents).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(EngineEvents), methodName);
        method.Invoke(events, args);
    }
}
