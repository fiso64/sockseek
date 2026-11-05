using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;

namespace Tests.Core;

[TestClass]
public sealed class ShareScannerTests
{
    [TestMethod]
    public async Task ScanAsync_AppliesFixedHiddenPolicyToWholeSubtrees()
    {
        await using var fixture = new ScannerFixture();
        string visible = Path.Combine(fixture.RootPath, "visible.txt");
        string hiddenFile = Path.Combine(
            fixture.RootPath,
            OperatingSystem.IsWindows() ? "hidden.txt" : ".hidden.txt");
        string hiddenDirectory = Path.Combine(
            fixture.RootPath,
            OperatingSystem.IsWindows() ? "hidden-dir" : ".hidden-dir");
        string empty = Path.Combine(fixture.RootPath, "empty.txt");
        await File.WriteAllTextAsync(visible, "visible");
        await File.WriteAllTextAsync(hiddenFile, "hidden");
        await File.WriteAllBytesAsync(empty, []);
        Directory.CreateDirectory(hiddenDirectory);
        await File.WriteAllTextAsync(Path.Combine(hiddenDirectory, "secret.txt"), "secret");

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
            File.SetAttributes(
                hiddenDirectory,
                File.GetAttributes(hiddenDirectory) | FileAttributes.Hidden);
        }

        var writer = new RecordingWriter(fixture.DatabasePath);
        ShareScanResult result = await new ShareScanner().ScanAsync(
            fixture.Settings,
            writer,
            Guid.NewGuid(),
            "settings");

        Assert.AreEqual(2, result.FilesIndexed);
        Assert.AreEqual(2, writer.Files.Count);
        CollectionAssert.AreEquivalent(
            new[] { @"Music\visible.txt", @"Music\empty.txt" },
            writer.Files.Select(file => file.RemotePath).ToArray());
        Assert.IsFalse(writer.Directories.Any(
            directory => directory.RemotePath.Contains("hidden", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.EntriesSkipped >= 2);
    }

    [TestMethod]
    public async Task ScanAsync_NestedCatalogFileCanBeOpenedForUpload()
    {
        await using var fixture = new ScannerFixture();
        string relative = Path.Combine("[testing] Artist", "[testing] Album", "01 - [testing] Track.txt");
        string local = Path.Combine(fixture.RootPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllTextAsync(local, "shared file contents");
        var writer = new RecordingWriter(fixture.DatabasePath);
        await new ShareScanner().ScanAsync(fixture.Settings, writer, Guid.NewGuid(), "settings");

        var file = writer.Files.Single();
        Assert.AreEqual(relative, file.RelativePath);
        Assert.AreEqual(@"Music\[testing] Artist\[testing] Album\01 - [testing] Track.txt", file.RemotePath);
        await using var opened = SafeSharedFileOpener.Open(fixture.RootPath, file.RelativePath,
            new SharedFileFingerprint(file.SizeBytes, file.ModifiedAtUtc));
        using var reader = new StreamReader(opened.Stream);
        Assert.AreEqual("shared file contents", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task ScanAsync_UnavailableRootIsSkippedWithoutLosingValidRoot()
    {
        await using var fixture = new ScannerFixture(createRoot: false);
        string validRoot = Path.Combine(Path.GetDirectoryName(fixture.RootPath)!, "valid-root");
        Directory.CreateDirectory(validRoot);
        await File.WriteAllTextAsync(Path.Combine(validRoot, "kept.txt"), "kept");
        fixture.Settings.Roots.Add(new ShareRootSettings
        {
            LocalPath = validRoot,
            Alias = "Valid",
            EffectiveAlias = "Valid",
        });
        var writer = new RecordingWriter(fixture.DatabasePath);

        ShareScanResult result = await new ShareScanner().ScanAsync(
            fixture.Settings,
            writer,
            Guid.NewGuid(),
            "settings");

        Assert.AreEqual(1, result.FilesIndexed);
        Assert.AreEqual(@"Valid\kept.txt", writer.Files.Single().RemotePath);
        Assert.IsTrue(result.Errors.Any(error =>
            error.Code == "root-unavailable" && error.RelativePath == "Music"));
        Assert.IsFalse(result.Errors.Any(error =>
            error.Message.Contains(fixture.RootPath, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ScanAsync_RemoteCollisionSkipsOnlyCollidingEntry()
    {
        await using var fixture = new ScannerFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "collision.txt"), "bad");
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "kept.txt"), "good");
        var writer = new RecordingWriter(fixture.DatabasePath)
        {
            CollidingRemotePath = @"Music\collision.txt",
        };

        ShareScanResult result = await new ShareScanner().ScanAsync(
            fixture.Settings,
            writer,
            Guid.NewGuid(),
            "settings");

        Assert.AreEqual(1, result.FilesIndexed);
        Assert.AreEqual(@"Music\kept.txt", writer.Files.Single().RemotePath);
        Assert.IsTrue(result.Errors.Any(error =>
            error.Code == "remote-path-collision"
            && error.RelativePath == "collision.txt"));
    }

    [TestMethod]
    public async Task ScanAsync_WriterFailureCancelsTheBoundedPipeline()
    {
        await using var fixture = new ScannerFixture();
        for (int index = 0; index < 300; index++)
            await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, $"file-{index:D3}.txt"), "data");

        Task<ShareScanResult> scan = new ShareScanner().ScanAsync(
            fixture.Settings,
            new FailingWriter(fixture.DatabasePath),
            Guid.NewGuid(),
            "settings").AsTask();

        InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => scan.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("Synthetic writer failure.", failure.Message);
    }

    private sealed class RecordingWriter(string databasePath)
        : IShareCatalogGenerationWriter
    {
        public string DatabasePath { get; } = databasePath;
        public List<ShareCatalogRoot> Roots { get; } = [];
        public List<ShareCatalogDirectory> Directories { get; } = [];
        public List<ShareCatalogFile> Files { get; } = [];
        public string? CollidingRemotePath { get; init; }

        public ValueTask AddRootAsync(
            ShareCatalogRoot root,
            CancellationToken cancellationToken = default)
        {
            Roots.Add(root);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddDirectoryAsync(
            ShareCatalogDirectory directory,
            CancellationToken cancellationToken = default)
        {
            Directories.Add(directory);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddFileAsync(
            ShareCatalogFile file,
            CancellationToken cancellationToken = default)
        {
            if (file.RemotePath == CollidingRemotePath)
            {
                throw new ShareCatalogEntryCollisionException(
                    "Synthetic collision.",
                    new InvalidOperationException());
            }
            Files.Add(file);
            return ValueTask.CompletedTask;
        }

        public ValueTask PrepareForReadAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask CompleteAsync(
            ShareCatalogMetadata metadata,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingWriter(string databasePath)
        : IShareCatalogGenerationWriter
    {
        public string DatabasePath { get; } = databasePath;

        public ValueTask AddRootAsync(ShareCatalogRoot root, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new InvalidOperationException("Synthetic writer failure."));

        public ValueTask AddDirectoryAsync(ShareCatalogDirectory directory, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask AddFileAsync(ShareCatalogFile file, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask PrepareForReadAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask CompleteAsync(ShareCatalogMetadata metadata, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScannerFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-scanner-{Guid.NewGuid():N}");

        public ScannerFixture(bool createRoot = true)
        {
            Directory.CreateDirectory(directory);
            RootPath = Path.Combine(directory, "root");
            if (createRoot)
                Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(directory, "catalog.sqlite3");
            Settings = new SharingSettings
            {
                Roots =
                [
                    new ShareRootSettings
                    {
                        LocalPath = RootPath,
                        Alias = "Music",
                        EffectiveAlias = "Music",
                    },
                ],
            };
        }

        public string RootPath { get; }
        public string DatabasePath { get; }
        public SharingSettings Settings { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(
                             directory,
                             "*",
                             SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }
                Directory.Delete(directory, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
