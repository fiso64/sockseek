using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.Sharing;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class SharingCatalogTests
{
    [TestMethod]
    public async Task Catalog_RoundTripsExactLookupSearchDirectoryAndBrowse()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await using var reader = await SqliteShareCatalogReader.OpenAsync(fixture.DatabasePath);

        var resolved = await reader.ResolveFileAsync(
            RemotePathKey.Create("music/artist/cafe\u0301 track.FLAC"));
        var search = await reader.SearchAsync("café track", 10);
        var directory = await reader.GetDirectoryAsync(
            RemotePathKey.Create(@"MUSIC\ARTIST"));
        var browse = new List<ShareCatalogBrowseDirectory>();
        await foreach (var item in reader.EnumerateBrowseAsync())
            browse.Add(item);

        Assert.IsNotNull(resolved);
        Assert.AreEqual("Music", resolved.Root.Alias);
        Assert.AreEqual(fixture.RootPath, resolved.Root.LocalPath);
        Assert.AreEqual(fixture.File.RemotePath, resolved.File.RemotePath);
        Assert.AreEqual(1, search.Count);
        Assert.AreEqual(fixture.File.FileId, search[0].FileId);
        Assert.AreEqual(1, directory!.Files.Count);
        Assert.AreEqual(3, browse.Count);
        Assert.IsTrue(browse.Any(x => x.Directory.RemotePath == @"Music\Empty"));
        Assert.AreEqual(0, browse.Single(x => x.Directory.RemotePath == @"Music\Empty").Files.Count);
    }

    [TestMethod]
    public async Task Catalog_SearchTreatsOperatorInputAsTerms()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        await using var reader = await SqliteShareCatalogReader.OpenAsync(fixture.DatabasePath);

        var results = await reader.SearchAsync("\" OR * NOT", 10);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task Catalog_SearchUsesOnlyLosslessSingleTokenExclusionPrefilters()
    {
        await using var fixture = await CatalogFixture.CreateAsync(addSecondFile: true);
        await using var reader = await SqliteShareCatalogReader.OpenAsync(fixture.DatabasePath);

        var singleToken = await reader.SearchAsync(
            "Music Artist",
            ["Second"],
            10);
        var compoundSubstring = await reader.SearchAsync(
            "Music Artist",
            ["Café Track"],
            10);

        Assert.AreEqual(1, singleToken.Count);
        Assert.AreEqual(fixture.File.FileId, singleToken[0].FileId);
        // Compound exclusions are kept for the adapter's exact substring
        // post-filter so the FTS tokenizer cannot create false negatives.
        Assert.AreEqual(2, compoundSubstring.Count);
    }

    [TestMethod]
    public async Task Catalog_DirectoryLookupReturnsEveryFile()
    {
        await using var fixture = await CatalogFixture.CreateAsync(addSecondFile: true);
        await using var reader = await SqliteShareCatalogReader.OpenAsync(fixture.DatabasePath);

        var directory = await reader.GetDirectoryAsync(
            RemotePathKey.Create(@"Music\Artist"));

        Assert.IsNotNull(directory);
        Assert.AreEqual(2, directory.Files.Count);
    }

    [TestMethod]
    public async Task Catalog_RejectsFileDirectoryRemoteIdentityCollision()
    {
        await using var directory = new TemporaryDirectory();
        string databasePath = Path.Combine(directory.Path, "collision.sqlite3");
        await using var builder = await SqliteShareCatalogBuilder.CreateAsync(databasePath);
        var root = CatalogFixture.CreateRoot(directory.Path);
        var catalogDirectory = CatalogFixture.CreateDirectory(1, root, "Artist");
        await builder.AddRootAsync(root);
        await builder.AddDirectoryAsync(catalogDirectory);

        var collidingFile = CatalogFixture.CreateFile(
            1,
            root,
            catalogDirectory,
            relativePath: "Artist",
            remotePath: @"Music\Artist");

        await Assert.ThrowsExactlyAsync<RemotePathCollisionException>(
            async () => await builder.AddFileAsync(collidingFile));
    }

    [TestMethod]
    public async Task Catalog_SchemaMismatchRequestsRebuild()
    {
        await using var fixture = await CatalogFixture.CreateAsync();

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE catalog_metadata SET schema_version = 999;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => _ = await SqliteShareCatalogReader.OpenAsync(fixture.DatabasePath));
    }

    private sealed class CatalogFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory directory;

        private CatalogFixture(
            TemporaryDirectory directory,
            string databasePath,
            string rootPath,
            ShareCatalogFile file)
        {
            this.directory = directory;
            DatabasePath = databasePath;
            RootPath = rootPath;
            File = file;
        }

        public string DatabasePath { get; }
        public string RootPath { get; }
        public ShareCatalogFile File { get; }

        public static async ValueTask<CatalogFixture> CreateAsync(bool addSecondFile = false)
        {
            var directory = new TemporaryDirectory();
            string databasePath = Path.Combine(directory.Path, "catalog.sqlite3");
            string rootPath = Path.Combine(directory.Path, "local-root");
            var root = CreateRoot(rootPath);
            var rootDirectory = CreateDirectory(1, root, "");
            var artistDirectory = CreateDirectory(2, root, "Artist");
            var emptyDirectory = CreateDirectory(3, root, "Empty");
            var file = CreateFile(
                1,
                root,
                artistDirectory,
                @"Artist\Café Track.flac",
                @"Music\Artist\Café Track.flac");

            await using (var builder = await SqliteShareCatalogBuilder.CreateAsync(databasePath))
            {
                await builder.AddRootAsync(root);
                await builder.AddDirectoryAsync(rootDirectory);
                await builder.AddDirectoryAsync(artistDirectory);
                await builder.AddDirectoryAsync(emptyDirectory);
                await builder.AddFileAsync(file);

                if (addSecondFile)
                {
                    await builder.AddFileAsync(CreateFile(
                        2,
                        root,
                        artistDirectory,
                        @"Artist\Second.flac",
                        @"Music\Artist\Second.flac"));
                }

                await builder.CompleteAsync(new ShareCatalogMetadata(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    "settings",
                    3,
                    addSecondFile ? 2 : 1,
                    addSecondFile ? 2468 : 1234,
                    ShareBrowseStatus.Ready,
                    ShareCatalogVersions.BrowseWire,
                    100,
                    new string('A', 64)));
            }

            return new CatalogFixture(directory, databasePath, rootPath, file);
        }

        public static ShareCatalogRoot CreateRoot(string rootPath)
            => new(
                1,
                "Music",
                rootPath,
                RemotePathKey.CreateAlias("Music"));

        public static ShareCatalogDirectory CreateDirectory(
            long id,
            ShareCatalogRoot root,
            string relativePath)
        {
            string remotePath = relativePath.Length == 0
                ? root.Alias
                : $@"{root.Alias}\{relativePath}";
            return new ShareCatalogDirectory(
                id,
                root.RootId,
                relativePath,
                remotePath,
                RemotePathKey.Create(remotePath));
        }

        public static ShareCatalogFile CreateFile(
            long id,
            ShareCatalogRoot root,
            ShareCatalogDirectory directory,
            string relativePath,
            string remotePath)
            => new(
                id,
                root.RootId,
                directory.DirectoryId,
                relativePath,
                remotePath,
                RemotePathKey.Create(remotePath),
                remotePath.Replace('\\', ' '),
                1234,
                DateTimeOffset.UnixEpoch,
                1,
                "flac",
                [new ShareFileAttribute(0, 320)]);

        public ValueTask DisposeAsync() => directory.DisposeAsync();
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sockseek-catalog-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
