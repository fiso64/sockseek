using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        var album = Summary(albumId, 6, workflowId, ServerJobKind.Album, ServerProtocol.JobStates.Downloading, "Artist Album");
        var song = Summary(songId, 7, workflowId, ServerJobKind.Song, ServerProtocol.JobStates.Searching, "Artist - Track") with
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
                State: ServerProtocol.JobStates.Pending,
                FailureReason: null,
                FailureMessage: null)])));

        var entry = formatter.Format(Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            7,
            workflowId,
            new SongQueryDto("Artist", "Track", null, null, null, false),
            ServerProtocol.JobStates.Done,
            FailureReason: null,
            DownloadPath: @"out\Track.flac",
            ChosenCandidate: candidate)));

        Assert.IsNotNull(entry);
        Assert.AreEqual(@"[6] Album Track: succeeded: Artist Album: 01. Artist - Track.flac", entry.Message);
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

    private static JobSummaryDto Summary(Guid id, int displayId, Guid workflowId, ServerJobKind kind, ServerJobState state, string text)
        => new(
            id,
            displayId,
            workflowId,
            kind,
            state,
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
