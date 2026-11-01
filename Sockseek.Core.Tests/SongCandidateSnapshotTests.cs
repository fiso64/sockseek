using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Events;

namespace Tests.Core;

[TestClass]
public sealed class SongCandidateSnapshotTests
{
    [TestMethod]
    public void ActiveAttemptProjectsOnlyItsExactCandidatePeerEvidence()
    {
        var song = new SongJob(new SongQuery { Title = "Track" });
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Album\Track.flac"), 128, ".flac");
        var other = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Other\Track.flac"), 128, ".flac");
        var candidate = new FileCandidate(target,
            new SearchPeerSnapshot("Peer", 1, 1_000_000, false));

        song.ActiveCandidate = candidate;
        var active = Snapshot(song, target);
        Assert.AreEqual(1_000_000, active.Candidate?.Peer.UploadSpeed);
        Assert.AreEqual(false, active.Candidate?.Peer.HasFreeUploadSlot);
        Assert.IsNull(song.ResolvedTarget);

        Assert.IsNull(Snapshot(song, other).Candidate);
        song.ActiveCandidate = null;
        Assert.IsNull(Snapshot(song, target).Candidate);

        song.ResolvedTarget = candidate;
        Assert.AreEqual(1_000_000, Snapshot(song, target).Candidate?.Peer.UploadSpeed);
    }

    [TestMethod]
    public void TransferEventsReplaceChangedEvidenceAndPreservePublishedMetadata()
    {
        var events = new DownloadEvents();
        var song = new SongJob(new SongQuery { Title = "Track" });
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Album\Track.flac"), 128, ".flac", bitRate: 320);
        var candidate = new FileCandidate(target, new SearchPeerSnapshot("Peer", 1, 1000, false));
        song.ActiveCandidate = candidate;
        var transfers = new List<TransferSnapshot>();
        events.DownloadStarted += change => transfers.Add(change.Transfer);
        events.DownloadProgress += change => transfers.Add(change.Transfer);
        Guid id = Guid.NewGuid();

        events.RaiseDownloadStarted(id, song, target, "first");
        events.RaiseDownloadProgress(id, song, target, "first", 32, 128);
        song.ActiveCandidate = new FileCandidate(target, new SearchPeerSnapshot("Peer", 2, 2000, true));
        events.RaiseDownloadProgress(id, song, target, "moved", 64, 128);
        song.ActiveCandidate = null;
        events.RaiseDownloadProgress(id, song, target, "moved", 96, 128);
        var corrected = new PeerFileTarget(target.Identity, 256, ".flac", bitRate: 500);
        events.RaiseDownloadProgress(id, song, corrected, "moved", 128, 256);
        var unknown = new PeerFileTarget(target.Identity, null, null);
        events.RaiseDownloadProgress(id, song, unknown, "moved", 192, 256);
        song.ResolvedTarget = candidate;
        events.RaiseDownloadProgress(id, song, target, "moved", 128, 128);

        Assert.AreEqual(7, transfers.Count);
        Assert.AreEqual(1000, transfers[0].Candidate?.Peer.UploadSpeed);
        Assert.AreEqual(false, transfers[0].Candidate?.Peer.HasFreeUploadSlot);
        Assert.AreEqual(0L, transfers[0].BytesTransferred);
        Assert.AreEqual("first", transfers[0].LocalPath);
        Assert.AreEqual(32L, transfers[1].BytesTransferred);
        Assert.AreEqual(2000, transfers[2].Candidate?.Peer.UploadSpeed);
        Assert.AreEqual(true, transfers[2].Candidate?.Peer.HasFreeUploadSlot);
        Assert.AreEqual("moved", transfers[2].LocalPath);
        Assert.IsNull(transfers[3].Candidate);
        Assert.AreEqual(128L, transfers[3].File?.Size);
        Assert.AreEqual(256L, transfers[4].File?.Size);
        Assert.AreEqual(500, transfers[4].File?.BitRate);
        Assert.IsNull(transfers[5].File);
        Assert.AreEqual(1000, transfers[6].Candidate?.Peer.UploadSpeed);
        Assert.AreEqual(128L, transfers[6].File?.Size);
        Assert.IsTrue(transfers.Zip(transfers.Skip(1)).All(pair => pair.First.Revision < pair.Second.Revision));
        // Later captures must neither mutate old peer evidence nor fill in an
        // old unknown size, even when the wire identity remains unchanged.
        Assert.AreEqual(320, transfers[0].File?.BitRate);
        Assert.IsNull(transfers[5].File);
    }

    [TestMethod]
    public void ExactFileSnapshotsPreserveCapturedMetadataAndPathsThroughProgressAndReplay()
    {
        var attributes = new[] { new FileAttributeSnapshot("unknown", -7, 999) };
        var components = new[] { "Disc 1", "Exact.flac" };
        var target = new PeerFileTarget(new PeerFileIdentity("ExactPeer", @"Root\Disc 1\Exact.flac"),
            128, ".flac", bitRate: 320, attributes: attributes);
        var job = new RemoteFileJob(target, new RelativeOutputPath(components));
        var first = (RemoteFileJobSnapshotPayload)CoreSnapshotFactory.CreateExecution(job, 1).Payload;
        attributes[0] = new("changed", 42);
        components[0] = "Changed";
        job.BytesTransferred = 64;
        var latest = (RemoteFileJobSnapshotPayload)CoreSnapshotFactory.CreateExecution(job, 2).Payload;
        Assert.AreEqual(0L, first.File.BytesTransferred);
        Assert.AreEqual(64L, latest.File.BytesTransferred);
        Assert.AreEqual("Disc 1", first.OutputPath.Components[0]);
        Assert.AreEqual(-7, first.Target.Attributes!.Single().Value);
        Assert.AreEqual(999, latest.Target.Attributes!.Single().StableCode);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(first.Target));
        Assert.IsFalse(json.RootElement.TryGetProperty("Username", out _), "Persist exact identity once.");
        Assert.AreEqual("ExactPeer", json.RootElement.GetProperty("Identity").GetProperty("Username").GetString());
        var retained = Sockseek.Core.Planning.SubmissionSpecificationCodec.Deserialize(
            Sockseek.Core.Planning.SubmissionSpecificationCodec.Serialize(
                Sockseek.Core.Planning.SubmissionSpecification.Create(job, new DownloadSettings())));
        var restored = (RemoteFileJob)retained.MaterializeJob();
        Assert.AreEqual(target.Identity, restored.Target.Identity);
        Assert.AreEqual(-7, restored.Target.Attributes!.Single().Value);
        CollectionAssert.AreEqual(new[] { "Disc 1", "Exact.flac" }, restored.OutputPath.Components.ToArray());
    }

    private static TransferSnapshot Snapshot(SongJob song, PeerFileTarget target)
        => CoreSnapshotFactory.CreateDownloadTransfer(
            Guid.NewGuid(), song, target, "output", 1, "InProgress", 64, 128, 1);
}
