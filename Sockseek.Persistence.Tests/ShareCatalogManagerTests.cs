using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.Sharing;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class ShareCatalogManagerTests
{
    [TestMethod]
    public async Task Initialize_PropagatesCallerCancellationWithoutAManifest()
    {
        await using var directory = new TemporaryDirectory();
        await using var manager = new ShareCatalogManager(directory.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => manager.InitializeAsync("settings", cancellation.Token).AsTask());
    }

    [TestMethod]
    public async Task Publish_ObsoleteGenerationWaitsForOutstandingLease()
    {
        await using var directory = new TemporaryDirectory();
        await using var manager = new ShareCatalogManager(directory.Path);
        Assert.IsFalse(await manager.InitializeAsync("settings"));

        var first = await CreatePublicationAsync(manager, "settings", 1);
        await manager.PublishAsync(first);
        Assert.IsTrue(manager.TryAcquire(out var firstLease));

        var second = await CreatePublicationAsync(manager, "settings", 2);
        await manager.PublishAsync(second);
        var third = await CreatePublicationAsync(manager, "settings", 3);
        await manager.PublishAsync(third);

        Assert.IsTrue(File.Exists(first.DatabasePath));
        Assert.IsTrue(File.Exists(first.BrowseArtifactPath));

        firstLease!.Dispose();

        Assert.IsFalse(File.Exists(first.DatabasePath));
        Assert.IsFalse(File.Exists(first.BrowseArtifactPath));
        Assert.AreEqual(third.GenerationId, manager.CurrentMetadata!.GenerationId);
    }

    [TestMethod]
    public async Task Initialize_FallsBackToValidPreviousGeneration()
    {
        await using var directory = new TemporaryDirectory();
        ShareCatalogPublication first;
        ShareCatalogPublication second;
        await using (var manager = new ShareCatalogManager(directory.Path))
        {
            await manager.InitializeAsync("settings");
            first = await CreatePublicationAsync(manager, "settings", 1);
            await manager.PublishAsync(first);
            second = await CreatePublicationAsync(manager, "settings", 2);
            await manager.PublishAsync(second);
        }

        await File.AppendAllTextAsync(second.BrowseArtifactPath, "corrupt");

        await using var restarted = new ShareCatalogManager(directory.Path);
        Assert.IsTrue(await restarted.InitializeAsync("settings"));
        Assert.AreEqual(first.GenerationId, restarted.CurrentMetadata!.GenerationId);
        Assert.IsFalse(File.Exists(second.DatabasePath));
        Assert.IsFalse(File.Exists(second.BrowseArtifactPath));
    }

    [TestMethod]
    public async Task Initialize_RejectsCatalogForDifferentSettings()
    {
        await using var directory = new TemporaryDirectory();
        await using (var manager = new ShareCatalogManager(directory.Path))
        {
            await manager.InitializeAsync("settings-a");
            await manager.PublishAsync(
                await CreatePublicationAsync(manager, "settings-a", 1));
        }

        await using var restarted = new ShareCatalogManager(directory.Path);
        Assert.IsFalse(await restarted.InitializeAsync("settings-b"));
        Assert.IsFalse(restarted.IsReady);
    }

    [TestMethod]
    public async Task Scan_BuildsAndPublishesFilteredGenerationEndToEnd()
    {
        await using var directory = new TemporaryDirectory();
        string rootPath = System.IO.Path.Combine(directory.Path, "shared");
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(System.IO.Path.Combine(rootPath, "Empty"));
        string excluded = System.IO.Path.Combine(rootPath, "Private");
        Directory.CreateDirectory(excluded);
        await File.WriteAllTextAsync(System.IO.Path.Combine(rootPath, "Keep.txt"), "hello");
        await File.WriteAllTextAsync(System.IO.Path.Combine(rootPath, "Skip.part"), "partial");
        await File.WriteAllTextAsync(System.IO.Path.Combine(excluded, "Secret.txt"), "secret");

        var settings = new SharingSettings
        {
            Roots =
            [
                new ShareRootSettings
                {
                    LocalPath = rootPath,
                    Alias = "Public",
                    EffectiveAlias = "Public",
                },
            ],
            ExcludedDirectories = [excluded],
            Filters = [@"\.part$"],
        };

        await using var manager = new ShareCatalogManager(
            System.IO.Path.Combine(directory.Path, "catalog"));
        await manager.InitializeAsync("settings");
        var coordinator = new ShareScanCoordinator(manager);

        ShareScanResult result = await coordinator.ScanAsync(settings, "settings");

        Assert.AreEqual(ShareScanPhase.Completed, coordinator.State.Phase);
        Assert.AreEqual(2, result.DirectoriesVisited);
        Assert.AreEqual(1, result.FilesIndexed);
        Assert.AreEqual(1, result.FilesFiltered);
        Assert.AreEqual(1, result.DirectoriesExcluded);
        Assert.AreEqual(ShareBrowseStatus.Ready, result.ProvisionalMetadata.BrowseStatus);
        Assert.IsTrue(result.TotalElapsed >= result.Elapsed);
        Assert.IsTrue(result.DatabaseFinalizationElapsed >= TimeSpan.Zero);
        Assert.IsTrue(result.BrowseArtifactBuildElapsed >= TimeSpan.Zero);
        Assert.IsTrue(result.ValidationElapsed >= TimeSpan.Zero);
        Assert.IsTrue(result.PublicationElapsed >= TimeSpan.Zero);
        Assert.IsTrue(manager.TryAcquire(out var lease));
        using (lease)
        {
            var resolved = await lease!.Reader.ResolveFileAsync(
                RemotePathKey.Create(@"public\keep.TXT"));
            Assert.IsNotNull(resolved);
            Assert.IsNull(await lease.Reader.ResolveFileAsync(
                RemotePathKey.Create(@"Public\Private\Secret.txt")));
        }
    }

    [TestMethod]
    public async Task OversizeBrowsePublishesSearchableMarkerAndSurvivesRestart()
    {
        await using var directory = new TemporaryDirectory();
        string rootPath = System.IO.Path.Combine(directory.Path, "shared");
        Directory.CreateDirectory(rootPath);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(rootPath, "Keep.txt"),
            "hello");
        var settings = new SharingSettings
        {
            Roots =
            [
                new ShareRootSettings
                {
                    LocalPath = rootPath,
                    Alias = "Public",
                    EffectiveAlias = "Public",
                },
            ],
        };
        string catalogPath = System.IO.Path.Combine(directory.Path, "catalog");
        Guid generationId;

        await using (var manager = new ShareCatalogManager(catalogPath))
        {
            await manager.InitializeAsync("settings");
            var coordinator = new ShareScanCoordinator(
                manager,
                artifactBuilder: new SoulseekBrowseArtifactBuilder(
                    maximumArtifactLength: 9));

            ShareScanResult result =
                await coordinator.ScanAsync(settings, "settings");

            generationId = result.ProvisionalMetadata.GenerationId;
            Assert.AreEqual(
                ShareBrowseStatus.UnavailableOversize,
                result.ProvisionalMetadata.BrowseStatus);
            Assert.IsTrue(manager.TryAcquire(out IShareCatalogLease? lease));
            using (lease)
            {
                Assert.IsNotNull(await lease!.Reader.ResolveFileAsync(
                    RemotePathKey.Create(@"Public\Keep.txt")));
                Assert.ThrowsExactly<InvalidOperationException>(
                    () => lease.OpenBrowseStream(TimeSpan.FromSeconds(1)));
            }
        }

        await using var restarted = new ShareCatalogManager(catalogPath);
        Assert.IsTrue(await restarted.InitializeAsync("settings"));
        Assert.AreEqual(generationId, restarted.CurrentMetadata!.GenerationId);
        Assert.AreEqual(
            ShareBrowseStatus.UnavailableOversize,
            restarted.CurrentMetadata.BrowseStatus);
    }

    [TestMethod]
    public async Task MissingRootPublishesEmptyGenerationWithRedactedEntryError()
    {
        await using var directory = new TemporaryDirectory();
        var settings = new SharingSettings
        {
            Roots =
            [
                new ShareRootSettings
                {
                    LocalPath = System.IO.Path.Combine(directory.Path, "missing-root"),
                    Alias = "Public",
                    EffectiveAlias = "Public",
                },
            ],
        };
        await using var manager = new ShareCatalogManager(
            System.IO.Path.Combine(directory.Path, "catalog"));
        await manager.InitializeAsync("settings");
        var coordinator = new ShareScanCoordinator(manager);

        ShareScanResult result = await coordinator.ScanAsync(settings, "settings");

        Assert.AreEqual(ShareScanPhase.Completed, coordinator.State.Phase);
        Assert.IsNull(coordinator.State.ErrorCode);
        Assert.IsTrue(manager.IsReady);
        Assert.AreEqual(0, result.FilesIndexed);
        ShareScanError error = result.Errors.Single();
        Assert.AreEqual("root-unavailable", error.Code);
        Assert.AreEqual("Public", error.RelativePath);
        Assert.IsFalse(error.Message.Contains(directory.Path, StringComparison.Ordinal));
    }

    private static async ValueTask<ShareCatalogPublication> CreatePublicationAsync(
        ShareCatalogManager manager,
        string settingsHash,
        byte marker)
    {
        Guid generationId = Guid.NewGuid();
        var paths = manager.GetGenerationPaths(generationId);
        byte[] artifact = [marker, 0, 0, 0];
        await File.WriteAllBytesAsync(paths.ArtifactPath, artifact);
        string hash = Convert.ToHexString(SHA256.HashData(artifact));
        var metadata = new ShareCatalogMetadata(
            generationId,
            DateTimeOffset.UtcNow,
            settingsHash,
            1,
            0,
            0,
            ShareBrowseStatus.Ready,
            ShareCatalogVersions.BrowseWire,
            artifact.Length,
            hash);

        await using (var builder =
                     await SqliteShareCatalogBuilder.CreateAsync(paths.DatabasePath))
        {
            var root = new ShareCatalogRoot(
                1,
                "Music",
                manager.DirectoryPath,
                RemotePathKey.CreateAlias("Music"));
            await builder.AddRootAsync(root);
            await builder.AddDirectoryAsync(new ShareCatalogDirectory(
                1,
                1,
                "",
                "Music",
                RemotePathKey.Create("Music")));
            await builder.CompleteAsync(metadata);
        }

        return new ShareCatalogPublication(
            generationId,
            paths.DatabasePath,
            paths.ArtifactPath,
            metadata);
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sockseek-manager-{Guid.NewGuid():N}");
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
