using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Snapshots;

namespace Tests.Core;

[TestClass]
public sealed class PeerFileTargetTests
{
    [TestMethod]
    public void Identity_PreservesExactWireSpelling()
    {
        string decomposed = "Cafe\u0301";
        var identity = new PeerFileIdentity(" Peer ", $"Music\\{decomposed}\\Track.FLAC ");

        Assert.AreEqual(" Peer ", identity.Username);
        Assert.AreEqual($"Music\\{decomposed}\\Track.FLAC ", identity.Filename);
        Assert.AreNotEqual(identity.Filename, identity.Filename.Normalize(NormalizationForm.FormC));
    }

    [TestMethod]
    public void Identity_MetadataDoesNotParticipateInIdentityEquality()
    {
        var identity = new PeerFileIdentity("Peer", @"Music\Track.flac");
        var first = new PeerFileTarget(identity, 100, ".flac", bitRate: 900);
        var second = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Music\Track.flac"),
            200,
            ".flac",
            bitRate: 1_200);

        Assert.AreEqual(first.Identity, second.Identity);
        Assert.AreNotEqual(first.Size, second.Size);
    }

    [TestMethod]
    public void Target_CopiesAttributeCollection()
    {
        var source = new List<FileAttributeSnapshot>
        {
            new("Length", 123, 1),
        };
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Music\Track.flac"),
            100,
            ".flac",
            attributes: source);

        source.Add(new FileAttributeSnapshot("BitRate", 900, 0));

        Assert.AreEqual(1, target.Attributes!.Count);
        Assert.AreEqual("Length", target.Attributes[0].Type);
    }

    [TestMethod]
    public void FileCandidate_LiveAndPersistedAdaptersCreateEquivalentTargets()
    {
        var attribute = new Soulseek.FileAttribute(Soulseek.FileAttributeType.Length, 123);
        var file = new Soulseek.File(1, "Music\\Track\u001B.flac", 42, ".flac", [attribute]);
        var response = new Soulseek.SearchResponse("Peer", 7, true, 1000, 0, [file]);
        var live = SoulseekSearchAdapter.ToFileCandidate(response, file);
        var persisted = new FileCandidate(
            new PeerFileTarget(
                new PeerFileIdentity("Peer", "Music\\Track\u001B.flac"),
                42,
                ".flac",
                file.BitRate,
                file.BitDepth,
                file.SampleRate,
                file.Length,
                live.Attributes),
            new SearchPeerSnapshot("Peer", 1, 1000, true));

        Assert.AreEqual(live.Target.Identity, persisted.Target.Identity);
        Assert.AreEqual(live.Target.Size, persisted.Target.Size);
        Assert.AreEqual(live.Target.Extension, persisted.Target.Extension);
        CollectionAssert.AreEqual(
            live.Target.Attributes!.ToArray(),
            persisted.Target.Attributes!.ToArray());
    }

    [TestMethod]
    public void Candidate_RequiresPeerAndTargetUsernameToMatchExactly()
    {
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Music\Track.flac"),
            42,
            ".flac");
        var peer = new SearchPeerSnapshot("peer", 1, 1000, true);

        Assert.ThrowsExactly<ArgumentException>(() => new FileCandidate(target, peer));
    }

    [TestMethod]
    public void Identity_RejectsInvalidLocalInputButAllowsExactRemoteControls()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PeerFileIdentity("", "file"));
        Assert.ThrowsExactly<ArgumentException>(() => new PeerFileIdentity("peer\n", "file"));
        Assert.ThrowsExactly<ArgumentException>(() => new PeerFileIdentity("\ud800", "file"));
        Assert.ThrowsExactly<ArgumentException>(() => new PeerFileIdentity("peer", "file\ud800"));

        var identity = new PeerFileIdentity("peer", "file\0\u001B\n.bin");

        Assert.AreEqual("file\0\u001B\n.bin", identity.Filename);
    }

    [TestMethod]
    public void RemoteDisplayText_PreservesUnicodeAndMakesUnsafeTextVisible()
    {
        foreach ((string input, string expected) in new (string, string)[]
        {
            ("a\0\t\n\u001B\u007F\u0085\u202Eb", "a␀␉␊␛␡<U+0085><U+202E>b"),
            ("Música\\a\u0301\\世界\\😀.flac", "Música\\a\u0301\\世界\\😀.flac"),
            ("\ud800a\udc00", "\ufffda\ufffd"),
            ("😀\u202E世界\U000E0001", "😀<U+202E>世界<U+000E0001>"),
            ("a\0😀\u202E", "a␀😀<U+202E>"),
        })
            Assert.AreEqual(expected, PeerIdentityValidator.ToDisplayText(input));
    }

    [TestMethod]
    public void Identity_DoesNotImposeSockseekOnlyByteCeilings()
    {
        string username = new('u', 2_048);
        string remotePath = new('p', 32 * 1_024);

        var identity = new PeerFileIdentity(username, remotePath);

        Assert.AreEqual(username, identity.Username);
        Assert.AreEqual(remotePath, identity.Filename);
    }

    [TestMethod]
    public void UnknownMetadata_RemainsUnknownWithoutSearchSentinels()
    {
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Folder\Unknown.bin"),
            size: null,
            extension: null);

        Assert.IsNull(target.Size);
        Assert.IsNull(target.Extension);
        Assert.IsNull(target.BitRate);
        Assert.IsNull(target.Attributes);
    }
}
