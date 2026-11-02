using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Server;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class HistoricalDirectoryPayloadTests
{
    [TestMethod]
    public void SupplementalFileTargetsResolveFromRetainedSongAndExactFileShapes()
    {
        var target = new PeerFileTarget(new PeerFileIdentity("Art Peer", @"Other\Cover.jpg"), 128, ".jpg");
        var candidate = new FileCandidateSnapshot(target.Username, target.Filename,
            new(target.Username, null, null), 128, null, null, null, null, ".jpg", null);
        var song = new SongJobSnapshotPayload(new(null, null, null, null, null, false),
            null, null, null, SongDownloadSource.None, new(null, 0, null), null);
        foreach ((string kind, JobSnapshotPayload payload) in new (string, JobSnapshotPayload)[]
        {
            ("Song", song with { ResolvedTarget = candidate }),
            ("Song", song with { ExactTarget = target }),
            ("Song", song with { LastAttemptedCandidate = candidate }),
            ("RemoteFile", new RemoteFileJobSnapshotPayload(target, new(["Cover.jpg"]), new(null, 0, null))),
        })
        {
            var selected = HistoricalJobDtoMapper.ToSelectedFileTarget(Job(payload) with { Kind = kind });
            Assert.IsNotNull(selected, kind);
            Assert.AreEqual("Art Peer", selected.Username);
            Assert.AreEqual(@"Other\Cover.jpg", selected.Filename);
            Assert.AreEqual(128L, selected.Size);
            Assert.AreEqual(".jpg", selected.Extension);
        }
        Assert.IsNull(HistoricalJobDtoMapper.ToSelectedFileTarget(Job(song) with { Kind = "Song" }));
    }

    [TestMethod]
    public void PartlyExistingDirectory_PreservesCompletionAndExactBytesInLiveAndRetainedPayloads()
    {
        var first = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Existing.bin"), 64, ".bin");
        var second = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Pending.bin"), 192, ".bin");
        var owner = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(new DirectoryTransferPlan("Root",
            [new DirectoryTransferEntry(first, []), new DirectoryTransferEntry(second, [])])));
        var existing = new RemoteFileJob(first);
        var pending = new RemoteFileJob(second) { BytesTransferred = 48 };
        owner.MaterializeDirectoryChildren([existing, pending]);
        existing.SetAlreadyExists();
        JobSnapshot snapshot = CoreSnapshotFactory.CreateJob(owner, 1);
        var live = (RemoteDirectoryJobPayloadDto)ServerSnapshotMapper.ToJobPayload(snapshot, _ => null);
        var retainedJob = Job(snapshot.Payload);
        var retained = (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(retainedJob);
        foreach (var directory in new[] { live.Directory, retained.Directory })
        {
            Assert.AreEqual(43.75, directory.ProgressPercent);
            Assert.AreEqual(48L, directory.BytesTransferred);
            Assert.AreEqual(256L, directory.TotalKnownBytes);
            Assert.AreEqual(1, directory.SuccessfulFileCount);
        }
        Assert.AreEqual(43.75, ServerSnapshotMapper.ToJobSummaryStats(snapshot)?.ProgressPercent);
        Assert.AreEqual(43.75, HistoricalJobDtoMapper.ToSummary(retainedJob).Stats?.ProgressPercent);
        var unknown = Read(DirectoryPayload(new("transferring", 1, null, 2, 1, 1, 0, 48, 0, 2, 0)));
        Assert.IsNull(unknown.Directory.ProgressPercent);
    }

    [TestMethod]
    public void RetainedResolvedDirectoryPreservesDisplayRootAndKnownOrigin()
    {
        var source = DirectoryPayload() with { DisplayRoot = "Mix CASE", SourceUsername = "Exact Peer" };
        Assert.AreEqual("Exact Peer", Read(source).SourceUsername);
        var payload = Read(source with { SourceUsername = null });
        Assert.AreEqual("Mix CASE", payload.DisplayRoot);
        Assert.IsNull(payload.SourceUsername);
        Assert.IsNull(payload.SourceFolderPath);

        var observed = Read(source with { DisplayRoot = "Head Hunters", SourceFolderPath = @"Artwork\Head Hunters" });
        Assert.AreEqual("Exact Peer", observed.SourceUsername);
        Assert.AreEqual(@"Artwork\Head Hunters", observed.SourceFolderPath);
        Assert.AreEqual("Head Hunters", observed.DisplayRoot);
        Assert.IsNull(Read(DirectoryPayload()).DisplayRoot);
        Assert.AreEqual(@"Music\Album", Read(source with
        {
            SourceKind = RemoteDirectorySourceSnapshotKind.PeerDirectory, SourceUsername = "Peer",
            SourceFolderPath = @"Music\Album", DisplayRoot = @"Music\Album",
        }).DisplayRoot);
    }

    [TestMethod]
    public void RetainedExistingDirectoryFile_HasCompletePayloadAndSummaryProgress()
    {
        var job = Job(DirectoryPayload(new("completed", 1, null, 1, 1, 1, 0, 0, 128, 0)));
        var detail = (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(job);
        Assert.AreEqual(100, detail.Directory.ProgressPercent);
        Assert.AreEqual(100, HistoricalJobDtoMapper.ToSummary(job).Stats?.ProgressPercent);
    }

    [TestMethod]
    public void RetainedDirectorySizeCoverage_DistinguishesUnknownFromEmptyFiles()
    {
        var directory = new DirectoryDownloadStateSnapshot("transferring", 1, null, 1, 0, 0, 0, 0, 0, 1);
        Assert.AreEqual(1, Read(DirectoryPayload(directory)).Directory.UnknownFileSizeCount);
        Assert.AreEqual(0, Read(DirectoryPayload(directory with { UnknownFileSizeCount = 0 })).Directory.UnknownFileSizeCount);
    }

    private static RemoteDirectoryExecutionPayload DirectoryPayload(DirectoryDownloadStateSnapshot? directory = null)
        => new(RemoteDirectorySourceSnapshotKind.Resolved, null, null, null, null,
            directory ?? new("unresolved", null, null, 0, 0, 0, 0, 0, 0, 0));

    private static RemoteDirectoryJobPayloadDto Read(RemoteDirectoryExecutionPayload payload)
        => (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(Job(payload));

    private static PersistedJob Job(JobSnapshotPayload payload)
    {
        var now = DateTimeOffset.UtcNow;
        return new PersistedJob(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
            "RemoteDirectory", "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, null, null, "Directory", now, now, now, 1, RetainedJobPayloadCodec.SchemaVersion,
            RetainedJobPayloadCodec.Capture(payload).Value);
    }
}
