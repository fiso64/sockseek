using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Tests.ClientTests;

namespace Tests.Core;

[TestClass]
public sealed class SoulseekPeerBrowseTransportTests
{
    [TestMethod]
    public async Task ReceiveAsync_MaterializesThroughBrowseAsyncAndWritesPublicAndLockedRows()
    {
        var client = new MockSoulseekClient([])
        {
            BrowseProgressSize = 64,
            BrowseResponseOverride = new BrowseResponse(
                [
                    new Soulseek.Directory(
                        "Public",
                        [new Soulseek.File(1, "public.mp3", 10, "mp3")]),
                ],
                [
                    new Soulseek.Directory(
                        "Locked",
                        [new Soulseek.File(
                            1,
                            "locked.flac",
                            20,
                            "flac",
                            [new FileAttribute(FileAttributeType.BitRate, 320)])]),
                ]),
        };
        using var manager = new SoulseekClientManager(new EngineSettings(), client);
        var transport = new SoulseekPeerBrowseTransport(
            manager,
            static _ => Task.CompletedTask);
        await using var sink = new RecordingSink();
        PeerBrowseTransportProgress? received = null;
        PeerBrowseIndexProgress? indexed = null;

        await transport.ReceiveAsync(
            "Peer",
            sink,
            progress => received = progress,
            progress => indexed = progress);

        Assert.AreEqual(1, client.BrowseCallCount);
        Assert.AreEqual(new PeerBrowseTransportProgress(64, 64), received);
        Assert.AreEqual(new PeerBrowseIndexProgress(2, 2, 30), indexed);
        CollectionAssert.AreEqual(
            new[]
            {
                ("Public", PeerShareVisibility.Public, 1),
                ("Locked", PeerShareVisibility.Locked, 1),
            },
            sink.Directories);
        CollectionAssert.AreEqual(
            new[] { "public.mp3", "locked.flac" },
            sink.Files.Select(file => file.Filename).ToArray());
        CollectionAssert.AreEqual(
            new[] { new PeerBrowseWireAttribute((int)FileAttributeType.BitRate, 320) },
            sink.Attributes);
        Assert.IsTrue(sink.Completed);
    }

    private sealed class RecordingSink : IPeerBrowseRowSink
    {
        public List<(string Path, PeerShareVisibility Visibility, int FileCount)> Directories { get; } = [];
        public List<PeerBrowseWireFile> Files { get; } = [];
        public List<PeerBrowseWireAttribute> Attributes { get; } = [];
        public bool Completed { get; private set; }

        public ValueTask BeginDirectoryAsync(
            string wirePath,
            PeerShareVisibility visibility,
            int fileCount,
            CancellationToken cancellationToken = default)
        {
            Directories.Add((wirePath, visibility, fileCount));
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginFileAsync(
            PeerBrowseWireFile file,
            CancellationToken cancellationToken = default)
        {
            Files.Add(file);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddAttributeAsync(
            PeerBrowseWireAttribute attribute,
            CancellationToken cancellationToken = default)
        {
            Attributes.Add(attribute);
            return ValueTask.CompletedTask;
        }

        public ValueTask EndFileAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
