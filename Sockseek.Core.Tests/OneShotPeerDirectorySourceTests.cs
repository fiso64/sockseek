using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;

namespace Tests;

[TestClass]
public sealed class OneShotPeerDirectorySourceTests
{
    [TestMethod]
    public async Task RetainsOnlyRequestedPublicSubtreeWithExactWireFilenames()
    {
        var source = new OneShotPeerDirectorySource(new FixtureTransport());

        PeerDirectorySnapshot snapshot = await source.RetrieveDirectoryAsync(
            new PeerDirectoryIdentity("Peer", "Root/Music"));

        Assert.IsTrue(snapshot.IsComplete);
        Assert.AreEqual(2, snapshot.Files.Count);
        PeerFileTarget first = snapshot.Files[0];
        Assert.AreEqual(@"Root\Music\one.mp3", first.Filename);
        Assert.AreEqual(320, first.BitRate);
        Assert.AreEqual(180, first.Length);
        CollectionAssert.AreEqual(
            new[] { "BitRate", "Length" },
            first.Attributes!.Take(2).Select(attribute => attribute.Type).ToArray());
        Assert.AreEqual(48_000, first.SampleRate);
        Assert.AreEqual(24, first.BitDepth);
        CollectionAssert.AreEqual(new[] { 0, 1, 0, 1, 4, 4, 5, 5, 73 },
            first.Attributes!.Select(attribute => attribute.StableCode).ToArray());
        Assert.AreEqual(int.MinValue, first.Attributes![^1].Value);
        Assert.AreEqual("Root/Music/Sub/two.flac", snapshot.Files[1].Filename);
    }

    [TestMethod]
    public async Task PreservesControlBearingWirePaths()
    {
        var source = new OneShotPeerDirectorySource(new ControlPathTransport());

        PeerDirectorySnapshot snapshot = await source.RetrieveDirectoryAsync(
            new PeerDirectoryIdentity("Peer", "Root/Mu\0sic"));

        Assert.AreEqual("Root/Mu\0sic/Sub\n\\song\u001B.mp3", snapshot.Files.Single().Filename);
    }

    private sealed class FixtureTransport : IPeerBrowseTransport
    {
        public async Task ReceiveAsync(
            string username,
            IPeerBrowseRowSink sink,
            Action<PeerBrowseTransportProgress>? transportProgress = null,
            Action<PeerBrowseIndexProgress>? indexProgress = null,
            CancellationToken cancellationToken = default)
        {
            await DirectoryAsync("Elsewhere", PeerShareVisibility.Public, "ignored.bin");

            await sink.BeginDirectoryAsync(@"Root\Music", PeerShareVisibility.Public, 1, cancellationToken);
            PeerBrowseWireAttribute[] attributes =
            [
                new(0, 320), new(1, 180), new(0, 900), new(1, 400),
                new(4, 0), new(4, 48_000), new(5, -1), new(5, 24), new(73, int.MinValue),
            ];
            await sink.BeginFileAsync(new PeerBrowseWireFile(1, "one.mp3", 12, "mp3", attributes.Length), cancellationToken);
            foreach (var attribute in attributes) await sink.AddAttributeAsync(attribute, cancellationToken);
            await sink.EndFileAsync(cancellationToken);

            await sink.BeginDirectoryAsync("Root/Music/Sub", PeerShareVisibility.Public, 1, cancellationToken);
            await sink.BeginFileAsync(
                new PeerBrowseWireFile(1, "Root/Music/Sub/two.flac", 34, "flac", 0),
                cancellationToken);
            await sink.EndFileAsync(cancellationToken);

            await DirectoryAsync(@"Root\Music\Secret", PeerShareVisibility.Locked, "locked.bin");
            await sink.CompleteAsync(cancellationToken);

            async Task DirectoryAsync(string path, PeerShareVisibility visibility, string filename)
            {
                await sink.BeginDirectoryAsync(path, visibility, 1, cancellationToken);
                await sink.BeginFileAsync(new PeerBrowseWireFile(1, filename, 1, "bin", 0), cancellationToken);
                await sink.EndFileAsync(cancellationToken);
            }
        }
    }

    private sealed class ControlPathTransport : IPeerBrowseTransport
    {
        public async Task ReceiveAsync(
            string username,
            IPeerBrowseRowSink sink,
            Action<PeerBrowseTransportProgress>? transportProgress = null,
            Action<PeerBrowseIndexProgress>? indexProgress = null,
            CancellationToken cancellationToken = default)
        {
            await sink.BeginDirectoryAsync("Root/Mu\0sic/Sub\n", PeerShareVisibility.Public, 1, cancellationToken);
            await sink.BeginFileAsync(new PeerBrowseWireFile(
                1, "song\u001B.mp3", 1, "mp3", 0), cancellationToken);
            await sink.EndFileAsync(cancellationToken);
            await sink.CompleteAsync(cancellationToken);
        }
    }
}
