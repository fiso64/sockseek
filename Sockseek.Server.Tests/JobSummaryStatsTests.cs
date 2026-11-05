using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core;
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
            RetainedJobPayloadCodec.Capture(CoreSnapshotFactory.CreateJob(search, 1).Payload).Value);
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
        SongQuerySnapshot songQuery = new(null, null, null, null, null, false);
        AlbumQuerySnapshot albumQuery = new(null, null, null, null, false);
        FileDownloadStateSnapshot file = new(null, 0, null);
        DirectoryDownloadStateSnapshot directory = new("transferring", 1, null, 8, 3, 2, 1, 45, 100, 0, 0.45);
        (string kind, JobSnapshotPayload payload, JobSummaryStatsDto expected)[] cases =
        [
            ("Song", new SongJobSnapshotPayload(songQuery, 3, null, null, SongDownloadSource.None, file, null), new(CandidateCount: 3)),
            ("Album", new AlbumExecutionPayload(albumQuery, 4, null, directory, null),
                new(MemberCount: 8, CompletedMemberCount: 3, FailedMemberCount: 1, FolderCount: 4, FileCount: 8, ProgressPercent: 45)),
            ("RemoteFile", new RemoteFileJobSnapshotPayload(new(new("Peer", @"Music\Song.mp3"), 123, ".mp3"), new(["Song.mp3"]), file), new(KnownBytes: 123)),
            ("RemoteDirectory", new RemoteDirectoryExecutionPayload(RemoteDirectorySourceSnapshotKind.Resolved, null, null, null, null,
                directory with { FileCount = 9, TerminalFileCount = 4, BytesTransferred = 35, Progress = 0.35 }),
                new(MemberCount: 9, CompletedMemberCount: 4, FailedMemberCount: 1, FileCount: 9, ProgressPercent: 35)),
            ("Aggregate", new AggregateExecutionPayload(songQuery, null, new(5, 2, 1, 1)), new(MemberCount: 5, CompletedMemberCount: 2, FailedMemberCount: 1)),
            ("AlbumAggregate", new AlbumAggregateJobSnapshotPayload(albumQuery, new(5, 2, 1, 1), null), new(MemberCount: 5, CompletedMemberCount: 2, FailedMemberCount: 1)),
            ("JobList", new JobListExecutionPayload(new(6, 3, 2, 1)), new(MemberCount: 6, CompletedMemberCount: 3, FailedMemberCount: 1)),
            ("RetrieveFolder", new RetrieveFolderExecutionPayload(new("Peer", "Music"), 2, FolderRetrievalOutcome.Completed, false), new(NewFilesFoundCount: 2)),
        ];
        foreach (var (kind, payload, expected) in cases)
        {
            var now = DateTimeOffset.UtcNow;
            var job = new PersistedJob(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
                kind, "Terminal", "None", null, "Succeeded", "None", "None", "None",
                null, null, null, kind, now, now, now, 1, RetainedJobPayloadCodec.SchemaVersion, RetainedJobPayloadCodec.Capture(payload).Value);
            Assert.AreEqual(expected, HistoricalJobDtoMapper.ToSummary(job).Stats, kind);
        }
    }
}
