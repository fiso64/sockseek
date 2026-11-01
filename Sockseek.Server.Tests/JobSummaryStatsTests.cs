using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Sockseek.Server;
using Sockseek.Server.Persistence;

namespace Tests.Server;

[TestClass]
public sealed class JobSummaryStatsTests
{
    [TestMethod]
    public void LiveSummariesAndRequestedDetailsAgreeForEveryJobKind()
    {
        var query = new SongQuery { Artist = "Artist", Title = "Track" };
        var albumQuery = new AlbumQuery { Artist = "Artist", Album = "Album" };
        (Job Job, JobSummaryStatsDto? Expected)[] cases =
        [
            (new SongJob(query), new()),
            (new AlbumJob(albumQuery), new(MemberCount: 0, CompletedMemberCount: 0,
                FailedMemberCount: 0, FolderCount: 0, FileCount: 0)),
            (new AggregateJob(query), new(MemberCount: 0, CompletedMemberCount: 0, FailedMemberCount: 0)),
            (new AlbumAggregateJob(albumQuery), new(MemberCount: 0, CompletedMemberCount: 0, FailedMemberCount: 0)),
            (new JobList("List", [new SongJob(query)]), new(MemberCount: 1, CompletedMemberCount: 0, FailedMemberCount: 0)),
            (new RemoteFileJob(new PeerFileTarget(new(" Peer ", @"Share\File.flac"), 123, "flac")), new(KnownBytes: 123)),
            (new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(new(" Peer ", @"Share\Album"))),
                new(MemberCount: 0, CompletedMemberCount: 0, FailedMemberCount: 0, FileCount: 0)),
            (new RetrieveFolderJob(new(" Peer ", @"Share\Album")), new(NewFilesFoundCount: 0)),
            (new SearchJob("files"), null), (new ExtractJob("source"), null),
        ];
        foreach (var (job, expected) in cases)
        {
            var store = new EngineStateStore();
            job.EnsureDisplayId();
            JobSnapshot snapshot = CoreSnapshotFactory.CreateJob(job, 1);
            store.RegisterAcceptedRoot(snapshot, null);
            JobSummaryDto summary = store.GetJobSummary(job.Id)!;
            JobDetailDto detail = store.GetJobDetail(job.Id)!;
            Assert.IsNotNull(detail.Payload);
            Assert.AreEqual(expected, summary.Stats, job.GetType().Name);
            Assert.AreEqual(summary.Stats, detail.Summary.Stats);
        }
    }

    [TestMethod]
    public void SearchModeSurvivesLiveAndRetainedSummaryProjection()
    {
        var search = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            AggregatePresentation = true,
        };
        search.Definition = SearchDefinition.Create(search, new SearchSettings());

        JobSummaryDto live = ServerSnapshotMapper.ToJobSummary(CoreSnapshotFactory.CreateJob(search, 0));
        Assert.AreEqual(ServerSearchDefaultProjectionKind.Track, live.SearchDefaultProjection);
        Assert.AreEqual(true, live.SearchAggregatePresentation);

        JobSummaryDto liveRoundTrip = JobStateDto.FromSummary(live, 1).ToSummary();
        Assert.AreEqual(live.SearchDefaultProjection, liveRoundTrip.SearchDefaultProjection);
        Assert.AreEqual(live.SearchAggregatePresentation, liveRoundTrip.SearchAggregatePresentation);

        var now = DateTimeOffset.UtcNow;
        var retained = new PersistedJob(search.Id, 1, search.WorkflowId, null, null, null,
            "Search", "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, null, null, search.QueryText, now, now, now, 1, 1,
            $$"""{"Definition":{{SearchDefinitionCodec.Serialize(search.Definition)}}}""");
        JobSummaryDto historical = HistoricalJobDtoMapper.ToSummary(retained);
        Assert.AreEqual(live.SearchDefaultProjection, historical.SearchDefaultProjection);
        Assert.AreEqual(live.SearchAggregatePresentation, historical.SearchAggregatePresentation);

        JobSummaryDto malformed = HistoricalJobDtoMapper.ToSummary(retained with { PayloadJson = "{bad" });
        Assert.IsNull(malformed.SearchDefaultProjection);
        Assert.IsNull(malformed.SearchAggregatePresentation);
    }

    [TestMethod]
    public void RetainedListStatsReportCountsAndProgress()
    {
        (string kind, string payload, JobSummaryStatsDto expected)[] cases =
        [
            ("Song", """{"Query":{},"CandidateCount":3}""", new(CandidateCount: 3)),
            ("Album", """{"Query":{},"ResultCount":4,"Directory":{"FileCount":8,"TerminalFileCount":3,"FailedFileCount":1,"BytesTransferred":45,"TotalKnownBytes":100}}""",
                new(MemberCount: 8, CompletedMemberCount: 3, FailedMemberCount: 1, FolderCount: 4, FileCount: 8, ProgressPercent: 45)),
            ("RemoteFile", """{"Target":{"Identity":{"Username":"Peer","Filename":"Music\\Song.mp3"},"Size":123}}""", new(KnownBytes: 123)),
            ("RemoteDirectory", """{"Directory":{"FileCount":9,"TerminalFileCount":4,"FailedFileCount":1,"BytesTransferred":35,"TotalKnownBytes":100}}""",
                new(MemberCount: 9, CompletedMemberCount: 4, FailedMemberCount: 1, FileCount: 9, ProgressPercent: 35)),
            ("Aggregate", """{"Query":{},"SongCount":5,"CompletedSongCount":2,"FailedSongCount":1}""", new(MemberCount: 5, CompletedMemberCount: 2, FailedMemberCount: 1)),
            ("AlbumAggregate", """{"Query":{},"AlbumCount":5,"CompletedAlbumCount":2,"FailedAlbumCount":1}""", new(MemberCount: 5, CompletedMemberCount: 2, FailedMemberCount: 1)),
            ("JobList", """{"Count":6,"CompletedJobCount":3,"FailedJobCount":1}""", new(MemberCount: 6, CompletedMemberCount: 3, FailedMemberCount: 1)),
            ("RetrieveFolder", """{"FolderPath":"Music","Username":"Peer","NewFilesFoundCount":2}""", new(NewFilesFoundCount: 2)),
        ];
        foreach (var (kind, payload, expected) in cases)
        {
            var now = DateTimeOffset.UtcNow;
            var job = new PersistedJob(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
                kind, "Terminal", "None", null, "Succeeded", "None", "None", "None",
                null, null, null, kind, now, now, now, 1, 1, payload);
            Assert.AreEqual(expected, HistoricalJobDtoMapper.ToSummary(job).Stats, kind);
        }
    }
}
