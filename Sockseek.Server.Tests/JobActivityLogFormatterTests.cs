using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class JobActivityLogFormatterTests
{
    [TestMethod]
    public void Format_AlbumTrackTerminalState_UsesAlbumTrackLogIdentity()
    {
        var formatter = new JobActivityLogFormatter();
        var workflowId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var album = Summary(albumId, 6, workflowId, ServerJobKind.Album, ExpectedJobStatus.Downloading, "Artist Album");
        var song = Summary(songId, 7, workflowId, ServerJobKind.Song, ExpectedJobStatus.Searching, "Artist - Track") with
        {
            ParentJobId = albumId,
        };
        var candidate = new FileCandidateDto(
            new FileCandidateRefDto("local", @"Artist\Album\01. Artist - Track.flac"),
            "local",
            @"Artist\Album\01. Artist - Track.flac",
            new PeerInfoDto("local"),
            Size: 123,
            BitRate: null,
            SampleRate: null,
            Length: null,
            Extension: ".flac",
            Attributes: null);

        formatter.Format(Envelope("job.upserted", album));
        formatter.Format(Envelope("job.upserted", song));
        formatter.Format(Envelope("album.track-download-started", new AlbumTrackDownloadStartedEventDto(
            album,
            new AlbumFolderDto(
                new AlbumFolderRefDto("local", @"Artist\Album"),
                "local",
                @"Artist\Album",
                new PeerInfoDto("local"),
                FileCount: 1,
                AudioFileCount: 1,
                Files: [candidate]),
            [new SongJobPayloadDto(
                new SongQueryDto("Artist", "Track", null, null, null, false),
                CandidateCount: 1,
                DownloadPath: null,
                ResolvedUsername: "local",
                ResolvedFilename: @"Artist\Album\01. Artist - Track.flac",
                ResolvedHasFreeUploadSlot: true,
                ResolvedUploadSpeed: null,
                ResolvedSize: null,
                ResolvedSampleRate: null,
                ResolvedExtension: ".flac",
                ResolvedAttributes: null,
                JobId: songId,
                DisplayId: 7,
                Candidates: null,
                LifecycleState: ServerJobLifecycleState.Pending,
                ActivityPhase: ServerJobActivityPhase.None,
                ActivityUntilUtc: null,
                TerminalOutcome: ServerJobTerminalOutcome.None,
                SkipReason: ServerJobSkipReason.None,
                FailureReason: null,
                FailureMessage: null)])));

        var entry = formatter.Format(Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            7,
            workflowId,
            new SongQueryDto("Artist", "Track", null, null, null, false),
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Succeeded,
            FailureReason: null,
            DownloadPath: @"out\Track.flac",
            ChosenCandidate: candidate)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(@"[6] Album Track: succeeded: Artist Album: 01. Artist - Track.flac", entry.Message);
    }

    [TestMethod]
    public void Format_FailedAlbumTrackTerminalState_IsWarningContext()
    {
        var formatter = new JobActivityLogFormatter();
        var workflowId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var album = Summary(albumId, 6, workflowId, ServerJobKind.Album, ExpectedJobStatus.Downloading, "Artist Album");
        var song = Summary(songId, 7, workflowId, ServerJobKind.Song, ExpectedJobStatus.Searching, "Artist - Track") with
        {
            ParentJobId = albumId,
        };
        var candidate = new FileCandidateDto(
            new FileCandidateRefDto("local", @"Artist\Album\01. Artist - Track.flac"),
            "local",
            @"Artist\Album\01. Artist - Track.flac",
            new PeerInfoDto("local"),
            Size: 123,
            BitRate: null,
            SampleRate: null,
            Length: null,
            Extension: ".flac",
            Attributes: null);

        formatter.Format(Envelope("job.upserted", album));
        formatter.Format(Envelope("job.upserted", song));

        var entry = formatter.Format(Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            7,
            workflowId,
            new SongQueryDto("Artist", "Track", null, null, null, false),
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Failed,
            ServerProtocol.FailureReasons.AllDownloadsFailed,
            DownloadPath: null,
            ChosenCandidate: candidate)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Warning, entry.Level);
        Assert.AreEqual(ActivityLogDisplayKind.Status, entry.Display?.Kind);
        Assert.AreEqual(@"[6] Album Track: failed [All downloads failed]: Artist Album: 01. Artist - Track.flac", entry.Message);
    }

    [TestMethod]
    public void Format_CancelAllAlbumTrackTerminalState_IsDebug()
    {
        var formatter = new JobActivityLogFormatter();
        var workflowId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var album = Summary(albumId, 6, workflowId, ServerJobKind.Album, ExpectedJobStatus.Downloading, "Artist Album");
        var song = Summary(songId, 7, workflowId, ServerJobKind.Song, ExpectedJobStatus.Downloading, "Artist - Track") with
        {
            ParentJobId = albumId,
        };
        var candidate = new FileCandidateDto(
            new FileCandidateRefDto("local", @"Artist\Album\01. Artist - Track.flac"),
            "local",
            @"Artist\Album\01. Artist - Track.flac",
            new PeerInfoDto("local"),
            Size: 123,
            BitRate: null,
            SampleRate: null,
            Length: null,
            Extension: ".flac",
            Attributes: null);

        formatter.Format(Envelope("job.upserted", album));
        formatter.Format(Envelope("job.upserted", song));

        var entry = formatter.Format(Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            7,
            workflowId,
            new SongQueryDto("Artist", "Track", null, null, null, false),
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Cancelled,
            ServerProtocol.FailureReasons.Cancelled,
            DownloadPath: null,
            ChosenCandidate: candidate,
            FailureMessage: null,
            CancellationSource: ServerJobCancellationSource.UserRequestedAllJobs)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Debug, entry.Level);
        Assert.AreEqual(@"[6] Album Track: cancelled: Artist Album: 01. Artist - Track.flac", entry.Message);
    }

    [TestMethod]
    public void Format_PendingJobUpsert_IsDebug()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Song, ExpectedJobStatus.Pending, "Artist - Track");

        var entry = formatter.Format(Envelope("job.upserted", summary));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Debug, entry.Level);
        StringAssert.Contains(entry.Message, "pending");
    }

    [TestMethod]
    public void Format_RunningJobUpsert_DoesNotLogActivityDuplicate()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Album, ExpectedJobStatus.Downloading, "Artist Album");

        var entry = formatter.Format(Envelope("job.upserted", summary));

        Assert.IsNull(entry);
    }

    [TestMethod]
    public void Format_AlbumDownloadStarted_DoesNotLogDuplicateActivityLine()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Album, ExpectedJobStatus.Downloading, "Artist Album");
        var folder = new AlbumFolderDto(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            "local",
            @"Artist\Album",
            new PeerInfoDto("local"),
            FileCount: 0,
            AudioFileCount: 0,
            Files: []);

        var entry = formatter.Format(Envelope("album.download-started", new AlbumDownloadStartedEventDto(summary, folder, [])));

        Assert.IsNull(entry);
    }

    [TestMethod]
    public void Format_AlbumDownloadingActivityChanged_DoesNotLogDuplicateTrackLine()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Album, ExpectedJobStatus.Downloading, "Artist Album");

        var entry = formatter.Format(Envelope("job.activity-changed", new JobActivityChangedEventDto(summary)));

        Assert.IsNull(entry);
    }

    [TestMethod]
    public void Format_RepeatedAlbumTrackDownloadStartedForSameFolder_LogsOnce()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Album, ExpectedJobStatus.Downloading, "Artist Album");
        var retrieving = summary with { ActivityPhase = ServerJobActivityPhase.RetrievingFolder };
        var folder = new AlbumFolderDto(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            "local",
            @"Artist\Album",
            new PeerInfoDto("local"),
            FileCount: 0,
            AudioFileCount: 0,
            Files: []);
        var albumStarted = new AlbumTrackDownloadStartedEventDto(summary, folder, []);

        var first = formatter.Format(Envelope("album.track-download-started", albumStarted));
        formatter.Format(Envelope("job.activity-changed", new JobActivityChangedEventDto(retrieving)));
        var second = formatter.Format(Envelope("album.track-download-started", albumStarted));

        Assert.IsNotNull(first);
        Assert.IsNull(second);
    }

    [TestMethod]
    public void Format_SearchWaitActivityChanged_IsDebug()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Song, ExpectedJobStatus.Searching, "Artist - Track") with
        {
            ActivityPhase = ServerJobActivityPhase.WaitingForSearchConcurrency,
        };

        var entry = formatter.Format(Envelope("job.activity-changed", new JobActivityChangedEventDto(summary)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Debug, entry.Level);
        StringAssert.Contains(entry.Message, "waiting search");
    }

    [TestMethod]
    public void Format_SearchRateLimitedActivityChanged_IsDebug()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Song, ExpectedJobStatus.Searching, "Artist - Track") with
        {
            ActivityPhase = ServerJobActivityPhase.SearchRateLimited,
        };

        var entry = formatter.Format(Envelope("job.activity-changed", new JobActivityChangedEventDto(summary)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Debug, entry.Level);
        StringAssert.Contains(entry.Message, "rate limited");
    }

    [TestMethod]
    public void Format_RunningOnCompleteActivityChanged_IsInformation()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.Song, ExpectedJobStatus.RunningOnComplete, "Artist - Track");

        var entry = formatter.Format(Envelope("job.activity-changed", new JobActivityChangedEventDto(summary)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Information, entry.Level);
        Assert.AreEqual("[8] SongJob: on-complete: Artist - Track", entry.Message);
    }

    [TestMethod]
    public void Format_UserRequestedJobCancelledSummary_IsInformation()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.JobList, ExpectedJobStatus.Failed, "wishlist") with
        {
            TerminalOutcome = ServerJobTerminalOutcome.Cancelled,
            FailureReason = ServerProtocol.FailureReasons.Cancelled,
            CancellationSource = ServerJobCancellationSource.UserRequestedJob,
        };

        var entry = formatter.Format(Envelope("job.upserted", summary));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Information, entry.Level);
        StringAssert.Contains(entry.Message, "cancelled");
    }

    [TestMethod]
    public void Format_InternalEngineCancelledSummary_IsDebug()
    {
        var formatter = new JobActivityLogFormatter();
        var summary = Summary(Guid.NewGuid(), 8, Guid.NewGuid(), ServerJobKind.JobList, ExpectedJobStatus.Failed, "wishlist") with
        {
            TerminalOutcome = ServerJobTerminalOutcome.Cancelled,
            FailureReason = ServerProtocol.FailureReasons.Cancelled,
            CancellationSource = ServerJobCancellationSource.InternalEngine,
        };

        var entry = formatter.Format(Envelope("job.upserted", summary));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Debug, entry.Level);
        StringAssert.Contains(entry.Message, "cancelled");
    }

    [TestMethod]
    public void Format_ParentCancelledSongStateChanged_IsDebug()
    {
        var formatter = new JobActivityLogFormatter();
        var workflowId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var entry = formatter.Format(Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            8,
            workflowId,
            new SongQueryDto("Artist", "Track", null, null, null, false),
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Cancelled,
            ServerProtocol.FailureReasons.Cancelled,
            DownloadPath: null,
            ChosenCandidate: null,
            FailureMessage: null,
            CancellationSource: ServerJobCancellationSource.ParentJob)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(LogLevel.Debug, entry.Level);
        StringAssert.Contains(entry.Message, "cancelled");
    }

    private static ServerEventEnvelopeDto Envelope(string type, object payload)
        => new(
            Sequence: 1,
            Type: type,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Category: ServerEventCatalog.ActivityCategory,
            SnapshotInvalidation: false,
            WorkflowId: null,
            Payload: payload);

    private static JobSummaryDto Summary(Guid id, int displayId, Guid workflowId, ServerJobKind kind, ExpectedJobStatus state, string text)
    {
        var split = Split(state);
        return new(
            id,
            displayId,
            workflowId,
            kind,
            split.LifecycleState,
            split.ActivityPhase,
            ActivityUntilUtc: null,
            split.TerminalOutcome,
            split.SkipReason,
            ItemName: text,
            QueryText: text,
            FailureReason: null,
            FailureMessage: null,
            ParentJobId: null,
            ResultJobId: null,
            SourceJobId: null,
            DiscoveryResultCount: null,
            DiscoveryLockedFileCount: null,
            AppliedAutoProfiles: [],
            AvailableActions: []);
    }

    private static (ServerJobLifecycleState LifecycleState, ServerJobActivityPhase ActivityPhase, ServerJobTerminalOutcome TerminalOutcome, ServerJobSkipReason SkipReason) Split(ExpectedJobStatus state)
        => state switch
        {
            ExpectedJobStatus.Pending => (ServerJobLifecycleState.Pending, ServerJobActivityPhase.None, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.Searching => (ServerJobLifecycleState.Running, ServerJobActivityPhase.Searching, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.Downloading => (ServerJobLifecycleState.Running, ServerJobActivityPhase.Downloading, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.RunningOnComplete => (ServerJobLifecycleState.Running, ServerJobActivityPhase.RunningOnComplete, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.Succeeded => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Succeeded, ServerJobSkipReason.None),
            ExpectedJobStatus.AlreadyExists => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Skipped, ServerJobSkipReason.AlreadyExists),
            ExpectedJobStatus.Skipped => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Skipped, ServerJobSkipReason.Manual),
            ExpectedJobStatus.NotFoundLastTime => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Skipped, ServerJobSkipReason.NotFoundLastTime),
            ExpectedJobStatus.Failed => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Failed, ServerJobSkipReason.None),
            _ => (ServerJobLifecycleState.Running, ServerJobActivityPhase.None, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
        };
}
