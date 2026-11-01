using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Server.PeerBrowsing;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class UserBrowseLiveStateTests
{
    [TestMethod]
    public void SnapshotAndDelta_ReconstructOnlyTheBrowseResource()
    {
        var server = new EngineStateStore();
        var batches = new List<StateUpdateBatchDto>();
        server.StateBatchPublished += batch => batches.Add(batch.ToDto());
        Guid browseId = Guid.NewGuid();
        UserBrowseDto queued = Browse(browseId, UserBrowseState.Queued, 1);
        server.UpdateUserBrowse(queued);
        StateSnapshotDto snapshot = server.GetUserBrowseSnapshot(queued);
        UserBrowseDto running = Browse(browseId, UserBrowseState.Running, 2);
        server.UpdateUserBrowse(running);
        server.FlushStateUpdates();

        var client = new DaemonClientStore();
        DaemonClientUpdate snapshotUpdate = client.ApplySnapshot(snapshot);
        DaemonClientUpdate deltaUpdate = client.Apply(batches[^1]);

        Assert.AreEqual(StateStreamScopeDto.UserBrowse(browseId), snapshot.Scope);
        Assert.AreEqual(queued, snapshot.UserBrowse);
        Assert.AreEqual(0, snapshot.Workflows.Count);
        Assert.AreEqual(0, snapshot.Jobs.Count);
        Assert.AreEqual(0, snapshot.Searches.Count);
        Assert.AreEqual(0, snapshot.Transfers.Count);
        Assert.AreEqual(queued, snapshotUpdate.ChangedUserBrowse);
        Assert.AreEqual(running, deltaUpdate.ChangedUserBrowse);
        Assert.AreEqual(running, client.GetUserBrowse(browseId));
    }

    [TestMethod]
    public void Coalescer_PromptlyPublishesTerminalBrowseAndKeepsLatestRevision()
    {
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        Guid epoch = Guid.NewGuid();
        Guid browseId = Guid.NewGuid();
        StateStreamScopeDto scope = StateStreamScopeDto.UserBrowse(browseId);
        global::Tests.Server.StateUpdateCoalescerTests.CaptureUpdate(coalescer, new StateUpdate(
            scope,
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDelta.Empty with { UserBrowse = Browse(browseId, UserBrowseState.Running, 1) },
            []));
        global::Tests.Server.StateUpdateCoalescerTests.CaptureUpdate(coalescer, new StateUpdate(
            scope,
            epoch,
            1,
            2,
            DateTimeOffset.UtcNow,
            StateDelta.Empty with { UserBrowse = Browse(browseId, UserBrowseState.Complete, 2) },
            []));

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(0, published[0].PreviousSequence);
        Assert.AreEqual(2, published[0].Sequence);
        Assert.AreEqual(UserBrowseState.Complete, published[0].State.UserBrowse?.State);
        Assert.AreEqual(2, published[0].State.UserBrowse?.Revision);
    }

    [TestMethod]
    public void ActiveBrowseDoesNotAdvertiseTerminalRetentionExpiry()
    {
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var resource = new PeerBrowseResource(
            Guid.NewGuid(), "local", "Peer", PeerBrowseState.Running,
            PeerBrowsePhase.Receiving, 1, null, 0, 0, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, expiresAt, null, 1);

        Assert.IsNull(UserBrowseDtoMapper.ToDto(resource).ExpiresAt);
        Assert.AreEqual(
            expiresAt,
            UserBrowseDtoMapper.ToDto(resource with
            {
                State = PeerBrowseState.Complete,
                Phase = PeerBrowsePhase.Ready,
                CompletedAt = DateTimeOffset.UtcNow,
            }).ExpiresAt);
    }

    [TestMethod]
    public void BrowseEntryMappingNeverEmitsRawPeerControls()
    {
        BrowseDirectoryEntryDto directory = UserBrowseDtoMapper.ToDto(new PeerBrowseDirectoryEntry(
            1, null, "Mu\0sic", "Root\\Mu\0sic", PeerBrowseEntryVisibility.Public,
            false, 0, 1, 1, 10, 0, true));
        BrowseFileEntryDto file = UserBrowseDtoMapper.ToDto(new PeerBrowseFileEntry(
            2, 1, PeerBrowseEntryVisibility.Public, "song\u001B.mp3", 10, "m\tp3",
            null, null, null, null, null));

        Assert.AreEqual("Mu␀sic", directory.Name);
        Assert.AreEqual("Root\\Mu␀sic", directory.DisplayPath);
        Assert.AreEqual("song␛.mp3", file.File.Name);
        Assert.AreEqual("m␉p3", file.File.Extension);
    }

    [TestMethod]
    public void RemovedBrowseDropsItsRetainedLiveProjectionAndSequence()
    {
        var server = new EngineStateStore();
        Guid browseId = Guid.NewGuid();
        UserBrowseDto browse = Browse(browseId, UserBrowseState.Complete, 1);
        server.UpdateUserBrowse(browse);
        Assert.AreEqual(1, server.GetUserBrowseSnapshot(browse).Position.Sequence);

        server.RemoveUserBrowse(browseId);

        Assert.AreEqual(0, server.GetUserBrowseSnapshot(browse).Position.Sequence);
    }

    private static UserBrowseDto Browse(Guid browseId, UserBrowseState state, long revision)
        => new(
            browseId,
            "Peer",
            state,
            state == UserBrowseState.Complete ? UserBrowsePhase.Ready : UserBrowsePhase.WaitingForPeer,
            0,
            null,
            0,
            0,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddTicks(revision),
            DateTimeOffset.UnixEpoch.AddHours(1),
            null,
            revision);
}
