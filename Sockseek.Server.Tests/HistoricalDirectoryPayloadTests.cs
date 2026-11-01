using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Server;
using System.Text.Json;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class HistoricalDirectoryPayloadTests
{
    [TestMethod]
    public void SupplementalFileTargetsResolveFromRetainedSongAndExactFileShapes()
    {
        foreach ((string kind, string json) in new[]
        {
            ("Song", """{"ResolvedTarget":{"Username":"Art Peer","Filename":"Other\\Cover.jpg","Size":128,"Extension":".jpg"}}"""),
            ("Song", """{"ExactTarget":{"Identity":{"Username":"Art Peer","Filename":"Other\\Cover.jpg"},"Size":128,"Extension":".jpg"}}"""),
            ("Song", """{"LastAttemptedCandidate":{"Username":"Art Peer","Filename":"Other\\Cover.jpg","Size":128,"Extension":".jpg"}}"""),
            ("RemoteFile", """{"Target":{"Identity":{"Username":"Art Peer","Filename":"Other\\Cover.jpg"},"Size":128,"Extension":".jpg"}}"""),
        })
        {
            var target = HistoricalJobDtoMapper.ToSelectedFileTarget(Job(json) with { Kind = kind });
            Assert.IsNotNull(target, kind);
            Assert.AreEqual("Art Peer", target.Username);
            Assert.AreEqual(@"Other\Cover.jpg", target.Filename);
            Assert.AreEqual(128L, target.Size);
            Assert.AreEqual(".jpg", target.Extension);
        }
        Assert.IsNull(HistoricalJobDtoMapper.ToSelectedFileTarget(Job("{}") with { Kind = "Song" }));
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
        var retainedJob = Job(JsonSerializer.Serialize(snapshot.Payload, snapshot.Payload.GetType()));
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
        var legacy = Read("""
            {"Directory":{"FileCount":2,"SuccessfulFileCount":1,
              "BytesTransferred":48,"TotalKnownBytes":256}}
            """);
        Assert.AreEqual(18.75, legacy.Directory.ProgressPercent,
            "Legacy partial snapshots lack fulfillment facts; keep the byte fallback without child fan-out.");
        var unknown = Read("""
            {"Directory":{"FileCount":2,"SuccessfulFileCount":1,"Progress":0,
              "BytesTransferred":48,"TotalKnownBytes":0,"UnknownFileSizeCount":2}}
            """);
        Assert.IsNull(unknown.Directory.ProgressPercent);
    }

    [TestMethod]
    public void RetainedResolvedDirectoryPreservesDisplayRootAndKnownPeerWithoutInventingLegacyIdentity()
    {
        var known = Read("""{"SourceKind":"Resolved","DisplayRoot":"Mix CASE","SourceUsername":"Exact Peer"}""");
        Assert.AreEqual("Exact Peer", known.SourceUsername);
        var payload = Read("""{"SourceKind":"Resolved","DisplayRoot":"Mix CASE"}""");
        Assert.AreEqual("Mix CASE", payload.DisplayRoot);
        Assert.IsNull(payload.SourceUsername);
        Assert.IsNull(payload.SourceFolderPath);

        var observed = Read("""
            {"SourceKind":"Resolved","ResolvedPlanSource":{"DisplayRoot":"Head Hunters",
              "SourceDirectory":{"Username":"Exact Peer","FolderPath":"Artwork\\Head Hunters"}}}
            """);
        Assert.AreEqual("Exact Peer", observed.SourceUsername);
        Assert.AreEqual(@"Artwork\Head Hunters", observed.SourceFolderPath);
        Assert.AreEqual("Head Hunters", observed.DisplayRoot);

        Assert.IsNull(Read("""{"SourceKind":"Resolved"}""").DisplayRoot);
        Assert.AreEqual(@"Music\Album", Read("""
            {"SourceKind":"PeerDirectory","DirectorySource":{"Username":"Peer","FolderPath":"Music\\Album"}}
            """).DisplayRoot);
    }

    [TestMethod]
    public void RetainedExistingDirectoryFile_HasCompletePayloadAndSummaryProgress()
    {
        const string payload = """
            {"SourceKind":"Resolved","Directory":{"FileCount":1,"TerminalFileCount":1,
              "SuccessfulFileCount":1,"FailedFileCount":0,"BytesTransferred":0,"TotalKnownBytes":128}}
            """;
        var job = Job(payload);
        var detail = (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(job);
        Assert.AreEqual(100, detail.Directory.ProgressPercent);
        Assert.AreEqual(100, HistoricalJobDtoMapper.ToSummary(job).Stats?.ProgressPercent);
    }

    [TestMethod]
    public void RetainedDirectorySizeCoverage_DoesNotTurnLegacyUnknownIntoZero()
    {
        var legacy = Read("""{"Directory":{"AttemptNumber":1,"FileCount":1,"TotalKnownBytes":0}}""");
        Assert.IsNull(legacy.Directory.UnknownFileSizeCount);
        var unknown = Read("""{"Directory":{"AttemptNumber":1,"FileCount":1,"TotalKnownBytes":0,"UnknownFileSizeCount":1}}""");
        Assert.AreEqual(1, unknown.Directory.UnknownFileSizeCount);
        var empty = Read("""{"Directory":{"AttemptNumber":1,"FileCount":1,"TotalKnownBytes":0,"UnknownFileSizeCount":0}}""");
        Assert.AreEqual(0, empty.Directory.UnknownFileSizeCount);
    }

    private static RemoteDirectoryJobPayloadDto Read(string payload)
        => (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(Job(payload));

    private static PersistedJob Job(string payload)
    {
        var now = DateTimeOffset.UtcNow;
        return new PersistedJob(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
            "RemoteDirectory", "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, null, null, "Directory", now, now, now, 1, 1, payload);
    }
}
