using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Services;
using Sockseek.Core.Sharing;

namespace Tests.Core;

[TestClass]
public sealed class SoulseekBrowseArtifactBuilderTests
{
    [TestMethod]
    public async Task BuildAsync_ProducesProtocolEquivalentFramedBrowseResponse()
    {
        var root = new ShareCatalogRoot(
            1,
            "Music",
            "private",
            RemotePathKey.CreateAlias("Music"));
        var emptyDirectory = new ShareCatalogDirectory(
            1,
            root.RootId,
            "Empty",
            @"Music\Empty",
            RemotePathKey.Create(@"Music\Empty"));
        var musicDirectory = new ShareCatalogDirectory(
            2,
            root.RootId,
            "Artist",
            @"Music\Artist",
            RemotePathKey.Create(@"Music\Artist"));
        var file = new ShareCatalogFile(
            1,
            root.RootId,
            musicDirectory.DirectoryId,
            @"Artist\Track.flac",
            @"Music\Artist\Track.flac",
            RemotePathKey.Create(@"Music\Artist\Track.flac"),
            "music artist track flac",
            1234,
            DateTimeOffset.UnixEpoch,
            1,
            "flac",
            [
                new ShareFileAttribute((int)FileAttributeType.BitRate, 320),
                new ShareFileAttribute((int)FileAttributeType.Length, 180),
            ]);
        var catalog = new FakeCatalogReader(
            [
                new ShareCatalogBrowseDirectory(emptyDirectory, []),
                new ShareCatalogBrowseDirectory(musicDirectory, [file]),
            ]);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-browse-{Guid.NewGuid():N}.bin");

        try
        {
            var artifact = await new SoulseekBrowseArtifactBuilder()
                .BuildAsync(catalog, path);
            byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);
            BrowseResponse parsed = ParseWithSoulseek(bytes);

            Assert.AreEqual(bytes.Length, artifact.Length);
            Assert.AreEqual(bytes.Length - 4, BitConverter.ToInt32(bytes, 0));
            Assert.AreEqual(5, BitConverter.ToInt32(bytes, 4));
            Assert.AreEqual(2, parsed.DirectoryCount);

            var parsedEmpty = parsed.Directories.Single(x => x.Name == @"Music\Empty");
            var parsedMusic = parsed.Directories.Single(x => x.Name == @"Music\Artist");
            Assert.AreEqual(0, parsedEmpty.FileCount);
            Assert.AreEqual(1, parsedMusic.FileCount);
            Assert.AreEqual("Track.flac", parsedMusic.Files.Single().Filename);
            Assert.AreEqual(1234, parsedMusic.Files.Single().Size);
            Assert.AreEqual(320, parsedMusic.Files.Single().BitRate);
        }
        finally
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
    }

    private static BrowseResponse ParseWithSoulseek(byte[] bytes)
    {
        Type factory = typeof(BrowseResponse).Assembly.GetType(
            "Soulseek.Messaging.Messages.BrowseResponseFactory",
            throwOnError: true)!;
        MethodInfo method = factory.GetMethod(
            "FromByteArray",
            BindingFlags.Public | BindingFlags.Static)!;
        return (BrowseResponse)method.Invoke(null, [bytes])!;
    }

    private sealed class FakeCatalogReader : IShareCatalogReader
    {
        private readonly IReadOnlyList<ShareCatalogBrowseDirectory> directories;

        public FakeCatalogReader(IReadOnlyList<ShareCatalogBrowseDirectory> directories)
        {
            this.directories = directories;
            Metadata = new ShareCatalogMetadata(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "settings",
                directories.Count,
                directories.Sum(directory => directory.Files.Count),
                directories.SelectMany(directory => directory.Files).Sum(file => file.SizeBytes),
                ShareBrowseStatus.Ready,
                null,
                null,
                null);
        }

        public ShareCatalogMetadata Metadata { get; }

        public ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ShareCatalogResolvedFile?>(null);

        public ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ShareCatalogFile>>([]);

        public ValueTask<ShareCatalogBrowseDirectory?> GetDirectoryAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ShareCatalogBrowseDirectory?>(null);

        public IAsyncEnumerable<ShareCatalogBrowseDirectory> EnumerateBrowseAsync(
            CancellationToken cancellationToken = default)
            => throw new AssertFailedException(
                "The artifact builder must use the row-streaming browse contract.");

        public async IAsyncEnumerable<ShareCatalogBrowseRow> EnumerateBrowseRowsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ShareCatalogBrowseDirectoryRow(
                    directory.Directory,
                    directory.Files.Count);
                foreach (ShareCatalogFile file in directory.Files)
                    yield return new ShareCatalogBrowseFileRow(file);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
