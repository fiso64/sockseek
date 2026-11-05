using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class JobRequestMapperTests
{
    [TestMethod]
    public void RemoteDirectoryDraft_RequiresExactlyOneCompleteSourceCase()
    {
        var plan = new DirectoryTransferPlanDto(
            "Root",
            [new DirectoryTransferEntryDto(
                new PeerFileTargetDto("Peer", @"Root\File.bin", 10, ".bin"),
                [])],
            10);

        Assert.ThrowsExactly<ArgumentException>(() =>
            JobRequestMapper.CreateJob(new RemoteDirectoryJobDraftDto()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            JobRequestMapper.CreateJob(new RemoteDirectoryJobDraftDto(Username: "Peer")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            JobRequestMapper.CreateJob(new RemoteDirectoryJobDraftDto("Peer", "Root", plan)));

        var peer = (RemoteDirectoryJob)JobRequestMapper.CreateJob(
            new RemoteDirectoryJobDraftDto("Peer", "Root"));
        var resolved = (RemoteDirectoryJob)JobRequestMapper.CreateJob(
            new RemoteDirectoryJobDraftDto(Plan: plan));

        Assert.IsInstanceOfType<RemoteDirectorySource.PeerDirectory>(peer.Source);
        Assert.IsInstanceOfType<RemoteDirectorySource.Resolved>(resolved.Source);
    }

    [TestMethod]
    public void RemoteDirectoryDraft_SkipsInvalidEntryAndKeepsValidSibling()
    {
        var plan = new DirectoryTransferPlanDto(
            "Root",
            [
                new DirectoryTransferEntryDto(
                    new PeerFileTargetDto("Peer", @"Root\bad.bin", 10, ".bin"),
                    [".."]),
                new DirectoryTransferEntryDto(
                    new PeerFileTargetDto("Peer", @"Root\kept.bin", 20, ".bin"),
                    []),
            ],
            30);

        var job = (RemoteDirectoryJob)JobRequestMapper.CreateJob(
            new RemoteDirectoryJobDraftDto(Plan: plan));
        var source = (RemoteDirectorySource.Resolved)job.Source;

        Assert.AreEqual(1, source.Plan.Entries.Count);
        Assert.AreEqual(@"Root\kept.bin", source.Plan.Entries[0].Target.Filename);
        Assert.AreEqual(20, source.Plan.TotalKnownBytes);
    }

}
