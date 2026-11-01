using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.Sqlite;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Persistence.PeerBrowsing;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class PeerBrowseArtifactStoreTests
{
    [TestMethod]
    public async Task StartupExpiresObsoleteBrowseCacheOnceAndRetainsNewArtifacts()
    {
        await using var directory = new TemporaryDirectory();
        Guid expiredId;
        string artifactPath;
        string registryPath;
        await using (var original = new PeerBrowseArtifactStore(directory.Path))
        {
            expiredId = (await CompleteEmptyAsync(original)).BrowseId;
            artifactPath = Directory.EnumerateFiles(Path.Combine(original.RootDirectory, "artifacts"), "*.sqlite").Single();
            registryPath = Path.Combine(original.RootDirectory, "resources.sqlite");
        }
        await using (var connection = new SqliteConnection($"Data Source={registryPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=9;";
            await command.ExecuteNonQueryAsync();
        }
        Guid currentId;
        await using (var upgraded = new PeerBrowseArtifactStore(directory.Path))
        {
            await upgraded.InitializeAsync();
            Assert.IsNull(await upgraded.GetAsync(expiredId));
            Assert.IsFalse(File.Exists(artifactPath));
            currentId = (await CompleteEmptyAsync(upgraded)).BrowseId;
        }
        await using var restarted = new PeerBrowseArtifactStore(directory.Path);
        Assert.AreEqual(PeerBrowseState.Complete, (await restarted.GetAsync(currentId))!.State);
        Assert.AreEqual(0, (await restarted.SearchAsync(currentId, "anything", null, null, null, 100)).Items.Count);
    }

    [TestMethod]
    public async Task CompletedArtifact_ProvidesOwnedPublicDirectorySnapshot()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var store = new PeerBrowseArtifactStore(directory.Path, clock);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await store.UpdateProgressAsync(resource.BrowseId, PeerBrowsePhase.Receiving, 5, 10, new(0, 0, 0));
        Assert.AreEqual(5, (await store.GetAsync(resource.BrowseId))!.CompressedBytesReceived);

        PeerBrowseWireAttribute[] attributes =
        [
            new(0, 900), new(1, 240), new(0, 1_000),
            new(73, int.MinValue), new(73, int.MaxValue), new(4, 0),
        ];
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync(@"Collection\Public", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "track.flac", 123, "flac", attributes.Length));
            foreach (var attribute in attributes) await writer.AddAttributeAsync(attribute);
            await writer.EndFileAsync();
            await writer.BeginDirectoryAsync(@"Collection\Locked", PeerShareVisibility.Locked, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "secret.flac", 456, "flac", 0));
            await writer.EndFileAsync();
            await writer.CompleteAsync();
        }

        string artifactPath = Directory.EnumerateFiles(
            Path.Combine(store.RootDirectory, "artifacts"),
            "*.sqlite").Single();
        await using (var connection = new SqliteConnection($"Data Source={artifactPath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT completion_marker FROM artifact_metadata;";
            Assert.AreEqual(1L, await command.ExecuteScalarAsync());
        }

        PeerBrowseResource? completed = await store.GetAsync(resource.BrowseId);
        PeerDirectorySnapshot snapshot = await store.ReadDirectoryAsync(
            resource.BrowseId,
            new PeerDirectoryIdentity("Peer", "Collection"));

        Assert.AreEqual(PeerBrowseState.Complete, completed!.State);
        Assert.AreEqual(2, completed.DirectoryCount);
        Assert.AreEqual(2, completed.FileCount);
        Assert.AreEqual(579, completed.TotalFileBytes);
        await store.UpdateProgressAsync(resource.BrowseId, PeerBrowsePhase.Indexing, 20, 20, new(99, 999, 9999));
        PeerBrowseResource afterLateProgress = (await store.GetAsync(resource.BrowseId))!;
        Assert.AreEqual(completed.Revision, afterLateProgress.Revision);
        Assert.AreEqual(completed.FileCount, afterLateProgress.FileCount);
        Assert.AreEqual(PeerBrowseState.Complete, afterLateProgress.State);
        Assert.AreEqual(1, snapshot.Files.Count);
        Assert.AreEqual(@"Collection\Public\track.flac", snapshot.Files[0].Filename);
        Assert.AreEqual(900, snapshot.Files[0].BitRate);
        Assert.AreEqual(240, snapshot.Files[0].Length);
        CollectionAssert.AreEqual(attributes.Select(a => (a.Type, a.Value)).ToArray(),
            snapshot.Files[0].Attributes!.Select(a => (a.StableCode, a.Value)).ToArray());

        PeerBrowsePage<PeerBrowseDirectoryEntry> allDirectories = await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, true, null, null, 20);
        PeerBrowseDirectoryEntry publicDirectory = allDirectories.Items.Single(
            item => item.DisplayPath == @"Collection\Public");
        PeerBrowseDirectoryEntry lockedDirectory = allDirectories.Items.Single(
            item => item.DisplayPath == @"Collection\Locked");
        PeerBrowseFileEntry publicFile = (await store.ReadFilesAsync(
            resource.BrowseId, publicDirectory.DirectoryId, null, null, null, 20)).Items.Single();
        PeerBrowseFileEntry lockedFile = (await store.ReadFilesAsync(
            resource.BrowseId, lockedDirectory.DirectoryId, null, null, null, 20)).Items.Single();
        Assert.AreEqual(PeerBrowseEntryVisibility.Public, publicFile.Visibility);
        Assert.AreEqual(PeerBrowseEntryVisibility.Locked, lockedFile.Visibility);
        Assert.IsNull(lockedFile.Attributes);
        CollectionAssert.AreEqual(attributes.Select(a => new PeerBrowseFileAttribute(a.Type, a.Value)).ToArray(),
            publicFile.Attributes!.ToArray());
        PeerBrowseFileEntry direct = (await store.ReadFileEntryAsync(resource.BrowseId, publicFile.FileId))!;
        CollectionAssert.AreEqual(publicFile.Attributes!.ToArray(), direct.Attributes!.ToArray());
        Assert.AreEqual(900, direct.BitRate, "The first valid scalar value still wins over duplicate codes.");
        var selected = await store.ResolveDownloadSelectionAsync(resource.BrowseId, [], [publicFile.FileId]);
        CollectionAssert.AreEqual(snapshot.Files[0].Attributes!.ToArray(), selected.Plans.Single().Entries.Single().Target.Attributes!.ToArray());

        await Assert.ThrowsExactlyAsync<PeerBrowseSelectionException>(() =>
            store.ResolveDownloadSelectionAsync(resource.BrowseId, [], [lockedFile.FileId]));
    }

    [TestMethod]
    public async Task PeerSuppliedStructuralParentBecomesMixedAndSelectsOnlyPublicSubtree()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);

        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Root", PeerShareVisibility.Public, 0);
            await writer.BeginDirectoryAsync(@"Root\Public", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "visible.bin", 12, "bin", 0));
            await writer.EndFileAsync();
            await writer.BeginDirectoryAsync(@"Root\Locked", PeerShareVisibility.Locked, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "secret.bin", 34, "bin", 0));
            await writer.EndFileAsync();
            await writer.CompleteAsync();
        }

        PeerBrowseDirectoryEntry root = (await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, false, null, null, 20)).Items.Single();
        PeerBrowseDownloadResolution selection = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], []);

        Assert.IsFalse(root.IsSynthetic);
        Assert.AreEqual(PeerBrowseEntryVisibility.Mixed, root.Visibility);
        Assert.AreEqual(1, root.RecursiveFileCount);
        Assert.AreEqual(12, root.RecursiveFileBytes);
        Assert.AreEqual(2, root.TotalRecursiveFileCount);
        Assert.AreEqual(46, root.TotalRecursiveFileBytes);
        Assert.AreEqual(1, root.LockedDescendantCount);
        Assert.AreEqual(1, selection.TotalPublicFiles);
        Assert.AreEqual(12, selection.TotalPublicBytes);
        Assert.AreEqual(1, selection.LockedBranchesSkipped);
        Assert.AreEqual(@"Root\Public\visible.bin", selection.Plans.Single().Entries.Single().Target.Filename);

        PeerBrowseDirectoryEntry lockedChild = (await store.ReadDirectoriesAsync(
            resource.BrowseId, root.DirectoryId, null, false, null, null, 20)).Items.Single(
                child => child.Visibility == PeerBrowseEntryVisibility.Locked);
        PeerBrowseDownloadResolution withoutLockedChild = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], [], excludedDirectoryIds: [lockedChild.DirectoryId]);
        Assert.AreEqual(1, withoutLockedChild.TotalPublicFiles);
        Assert.AreEqual(0, withoutLockedChild.LockedBranchesSkipped);
        Assert.AreEqual(@"Root\Public\visible.bin", withoutLockedChild.Plans.Single().Entries.Single().Target.Filename);
    }

    [TestMethod]
    public async Task FreshLookup_IsStrictAtFiveMinuteBoundary()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var store = new PeerBrowseArtifactStore(directory.Path, clock);
        PeerBrowseResource resource = await CompleteEmptyAsync(store);

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.AreEqual(
            resource.BrowseId,
            (await store.FindFreshAsync("local", "Peer", TimeSpan.FromMinutes(5)))!.BrowseId);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.IsNull(await store.FindFreshAsync("local", "Peer", TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public async Task FreshLookup_IgnoresACompletedResourceWhoseArtifactIsMissing()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await CompleteEmptyAsync(store);
        string artifactPath = Directory.EnumerateFiles(
            Path.Combine(store.RootDirectory, "artifacts"),
            "*.sqlite").Single();
        File.Delete(artifactPath);

        PeerBrowseResource? fresh = await store.FindFreshAsync(
            "local",
            "Peer",
            TimeSpan.FromMinutes(5));

        Assert.IsNull(fresh);
        Assert.AreEqual(resource.BrowseId, (await store.GetAsync(resource.BrowseId))!.BrowseId);
    }

    [TestMethod]
    public async Task ActiveResourceDoesNotExpireAndTerminalStateStartsFreshRetentionWindow()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var store = new PeerBrowseArtifactStore(
            directory.Path,
            clock,
            resourceRetention: TimeSpan.FromMinutes(1));
        var removed = new List<Guid>();
        store.ResourceRemoved += removed.Add;
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);

        clock.Advance(TimeSpan.FromMinutes(2));
        await store.EvictAsync();

        Assert.AreEqual(PeerBrowseState.Running, (await store.GetAsync(resource.BrowseId))!.State);
        Assert.AreEqual(1, (await store.ListAsync("local", null, null, null, null, 10)).Items.Count);

        await store.MarkCancelledAsync(resource.BrowseId);
        clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.AreEqual(PeerBrowseState.Cancelled, (await store.GetAsync(resource.BrowseId))!.State);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.IsNull(await store.GetAsync(resource.BrowseId));
        CollectionAssert.AreEqual(new[] { resource.BrowseId }, removed);
    }

    [TestMethod]
    public async Task Eviction_WaitsForArtifactLeaseBeforeRemovingExpiredResource()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var store = new PeerBrowseArtifactStore(
            directory.Path,
            clock,
            resourceRetention: TimeSpan.FromMinutes(1));
        PeerBrowseResource resource = await CompleteEmptyAsync(store);
        await using PeerBrowseArtifactStore.ArtifactLease lease = await store.AcquireLeaseAsync(resource.BrowseId);

        clock.Advance(TimeSpan.FromMinutes(2));
        await store.EvictAsync();

        Assert.IsNull(await store.GetAsync(resource.BrowseId));
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(store.RootDirectory, "artifacts")).Count());

        await lease.DisposeAsync();
        await store.EvictAsync();

        Assert.IsNull(await store.GetAsync(resource.BrowseId));
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(store.RootDirectory, "artifacts")).Count());
    }

    [TestMethod]
    public async Task ByteBudgetEvictsOldestCompletedArtifactButRetainsNewest()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var store = new PeerBrowseArtifactStore(directory.Path, clock, artifactByteBudget: 1);
        PeerBrowseResource first = await CompleteEmptyAsync(store);
        clock.Advance(TimeSpan.FromTicks(1));
        PeerBrowseResource second = await CompleteEmptyAsync(store);

        Assert.IsNull(await store.GetAsync(first.BrowseId));
        Assert.IsNotNull(await store.GetAsync(second.BrowseId));
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(store.RootDirectory, "artifacts")).Count());
    }

    [TestMethod]
    public async Task TerminalResourceCountTargetEvictsOldestWithoutRejectingNewWork()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var store = new PeerBrowseArtifactStore(
            directory.Path,
            clock,
            resourceCountTarget: 2);
        var resources = new List<PeerBrowseResource>();
        for (int index = 0; index < 3; index++)
        {
            PeerBrowseResource resource = await store.CreateQueuedAsync("local", $"Peer-{index}");
            await store.MarkFailedAsync(resource.BrowseId, "test-failure", "failed");
            resources.Add(resource);
            clock.Advance(TimeSpan.FromTicks(1));
        }

        Assert.IsNull(await store.GetAsync(resources[0].BrowseId));
        Assert.IsNotNull(await store.GetAsync(resources[1].BrowseId));
        Assert.IsNotNull(await store.GetAsync(resources[2].BrowseId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AbandonedStagingFile_IsRemovedOnRestartWithoutReplacingResource(bool indexing)
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var first = new PeerBrowseArtifactStore(directory.Path, clock);
        PeerBrowseResource resource = await first.CreateQueuedAsync("local", "Peer");
        await first.MarkRunningAsync(resource.BrowseId);
        if (indexing) await first.MarkIndexingAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await first.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Music", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "partial.mp3", 1, "mp3", 0));
        }

        await using var restarted = new PeerBrowseArtifactStore(directory.Path, clock);
        await restarted.InitializeAsync();
        PeerBrowseResource? interrupted = await restarted.GetAsync(resource.BrowseId);

        Assert.AreEqual(PeerBrowseState.Failed, interrupted!.State);
        Assert.AreEqual("daemon-restarted", interrupted.Failure!.Code);
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(restarted.RootDirectory, "staging")).Count());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => restarted.ReadDirectoryAsync(resource.BrowseId, new PeerDirectoryIdentity("Peer", "Music")));
    }

    [TestMethod]
    public async Task LockedAbandonedStagingFileDoesNotPreventInitializationAndIsRetried()
    {
        await using var directory = new TemporaryDirectory();
        await using var original = new PeerBrowseArtifactStore(directory.Path);
        await original.InitializeAsync();
        string stagingPath = Path.Combine(original.RootDirectory, "staging", "held.staging");
        await File.WriteAllTextAsync(stagingPath, "partial");

        await using (var held = new FileStream(
                         stagingPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            await using var firstRestart = new PeerBrowseArtifactStore(directory.Path);
            await firstRestart.InitializeAsync();
        }

        await using var secondRestart = new PeerBrowseArtifactStore(directory.Path);
        await secondRestart.InitializeAsync();
        Assert.IsFalse(File.Exists(stagingPath));
    }

    [TestMethod]
    public async Task FailedWriter_DeletesPrivateStagingArtifact()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Music", PeerShareVisibility.Public, 1);
            await Assert.ThrowsExactlyAsync<PeerBrowseProtocolException>(
                async () => await writer.BeginFileAsync(new PeerBrowseWireFile(1, @"Other\track.mp3", 1, "mp3", 0)));
        }

        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(store.RootDirectory, "staging")).Count());
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(store.RootDirectory, "artifacts")).Count());
    }

    [TestMethod]
    public async Task DeferredDuplicateFileIdentityRejectsArtifactBeforePromotion()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);

        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Music", PeerShareVisibility.Public, 2);
            for (int index = 0; index < 2; index++)
            {
                await writer.BeginFileAsync(new PeerBrowseWireFile(1, "same.mp3", 1, "mp3", 0));
                await writer.EndFileAsync();
            }

            await Assert.ThrowsExactlyAsync<PeerBrowseProtocolException>(
                async () => await writer.CompleteAsync());
        }

        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(store.RootDirectory, "staging")).Count());
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(store.RootDirectory, "artifacts")).Count());
    }

    [TestMethod]
    public async Task LocalTransport_WritesEveryFileInRequestedDirectory()
    {
        await using var directory = new TemporaryDirectory();
        string album = Path.Combine(directory.Path, "Artist", "Album");
        Directory.CreateDirectory(album);
        await File.WriteAllTextAsync(Path.Combine(album, "one.mp3"), "1");
        await File.WriteAllTextAsync(Path.Combine(album, "two.mp3"), "22");
        var localClient = LocalFilesSoulseekClient.FromLocalPaths(false, false, directory.Path);
        await using var manager = new SoulseekClientManager(
            new EngineSettings { Username = "local", Password = "unused" },
            localClient);
        var transport = new SoulseekPeerBrowseTransport(manager);
        await using var store = new PeerBrowseArtifactStore(Path.Combine(directory.Path, "data"));
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "local");
        await store.MarkRunningAsync(resource.BrowseId);
        await using PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource);

        await transport.ReceiveAsync("local", writer);
        PeerDirectorySnapshot snapshot = await store.ReadDirectoryAsync(
            resource.BrowseId,
            new PeerDirectoryIdentity("local", @"Artist\Album"));

        CollectionAssert.AreEquivalent(
            new[] { @"Artist\Album\one.mp3", @"Artist\Album\two.mp3" },
            snapshot.Files.Select(static file => file.Filename).ToArray());
    }

    [TestMethod]
    public async Task DirectoryAndFilePages_UseStableKeysetContinuation()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Zulu", PeerShareVisibility.Public, 0);
            await writer.BeginDirectoryAsync("Alpha", PeerShareVisibility.Public, 3);
            foreach (string filename in new[] { "three.mp3", "one.mp3", "two.mp3" })
            {
                await writer.BeginFileAsync(new PeerBrowseWireFile(1, filename, 1, "mp3", 0));
                await writer.EndFileAsync();
            }
            await writer.CompleteAsync();
        }

        PeerBrowsePage<PeerBrowseDirectoryEntry> directoryPage1 = await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, false, null, null, 1);
        PeerBrowsePage<PeerBrowseDirectoryEntry> directoryPage2 = await store.ReadDirectoriesAsync(
            resource.BrowseId,
            null,
            null,
            false,
            directoryPage1.NextSortKey,
            directoryPage1.NextId,
            1);

        Assert.AreEqual("Alpha", directoryPage1.Items.Single().Name);
        Assert.AreEqual("Zulu", directoryPage2.Items.Single().Name);
        Assert.IsNull(directoryPage2.NextSortKey);

        long alphaId = directoryPage1.Items.Single().DirectoryId;
        PeerBrowsePage<PeerBrowseFileEntry> filePage1 = await store.ReadFilesAsync(
            resource.BrowseId, alphaId, null, null, null, 2);
        PeerBrowsePage<PeerBrowseFileEntry> filePage2 = await store.ReadFilesAsync(
            resource.BrowseId,
            alphaId,
            null,
            filePage1.NextSortKey,
            filePage1.NextId,
            2);

        CollectionAssert.AreEqual(new[] { "one.mp3", "three.mp3" }, filePage1.Items.Select(x => x.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "two.mp3" }, filePage2.Items.Select(x => x.Name).ToArray());
        Assert.IsNull(filePage2.NextSortKey);
    }

    [TestMethod]
    public async Task MixedSearch_IndexesGlobalPathsAndReturnsAncestorsExactTotalsAndStablePages()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync(@"Root\Música\Álbum", PeerShareVisibility.Public, 2);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "Track One.FLAC", 100, "flac", 4));
            await writer.AddAttributeAsync(new PeerBrowseWireAttribute(0, 320));
            await writer.AddAttributeAsync(new PeerBrowseWireAttribute(1, 180));
            await writer.AddAttributeAsync(new PeerBrowseWireAttribute(4, 44_100));
            await writer.AddAttributeAsync(new PeerBrowseWireAttribute(5, 16));
            await writer.EndFileAsync();
            await AddFileAsync(writer, "A+B \"Live\".flac", 20, "flac");
            await writer.BeginDirectoryAsync(@"Root\Locked", PeerShareVisibility.Locked, 1);
            await AddFileAsync(writer, "track secret.mp3", 50, "mp3");
            await writer.BeginDirectoryAsync("Elsewhere", PeerShareVisibility.Public, 1);
            await AddFileAsync(writer, "soundtrack.mp3", 30, "mp3");
            await writer.BeginDirectoryAsync(@"Root\Empty", PeerShareVisibility.Public, 0);
            // A peer can advertise a directory and file at the same display
            // path. Paging must distinguish their kinds rather than lose one.
            await writer.BeginDirectoryAsync(@"Root\Locked\track secret.mp3", PeerShareVisibility.Public, 0);
            await writer.CompleteAsync();
        }

        PeerBrowseSearchPage first = await store.SearchAsync(
            resource.BrowseId, "TrAcK", null, null, null, 3);
        PeerBrowseSearchPage second = await store.SearchAsync(
            resource.BrowseId,
            "TrAcK",
            first.NextSortKey,
            first.NextKind,
            first.NextId,
            20);
        PeerBrowseSearchEntry[] rows = first.Items.Concat(second.Items).ToArray();

        Assert.AreEqual(2, first.PublicMatchingFileCount);
        Assert.AreEqual(130, first.PublicMatchingBytes);
        Assert.AreEqual(1, first.LockedMatchingFileCount);
        Assert.AreEqual(50, first.LockedMatchingBytes);
        Assert.AreEqual(first.PublicMatchingFileCount, second.PublicMatchingFileCount);
        Assert.AreEqual(3, rows.Count(row => row.Kind == PeerBrowseSearchEntryKind.File));
        Assert.AreEqual(rows.Length, rows.Select(row => (row.Kind, row.EntryId)).Distinct().Count());
        PeerBrowseSearchEntry track = rows.Single(row => row.Name == "Track One.FLAC");
        Assert.AreEqual(@"Root\Música\Álbum\Track One.FLAC", track.DisplayPath);
        Assert.AreEqual(100L, track.FileSize);
        Assert.AreEqual("flac", track.Extension);
        Assert.AreEqual(320, track.BitRate);
        Assert.AreEqual(180, track.Length);
        Assert.AreEqual(44_100, track.SampleRate);
        Assert.AreEqual(16, track.BitDepth);
        Assert.AreEqual(track.DisplayPath, await store.ReadSearchSortKeyAsync(
            resource.BrowseId, track.Kind, track.EntryId));
        CollectionAssert.Contains(rows.Select(row => row.DisplayPath).ToArray(), "Root");
        CollectionAssert.Contains(rows.Select(row => row.DisplayPath).ToArray(), @"Root\Música\Álbum");

        PeerBrowseSearchEntry root = rows.Single(row =>
            row.Kind == PeerBrowseSearchEntryKind.Directory && row.DisplayPath == "Root");
        Assert.AreEqual(1, root.PublicMatchingFileCount);
        Assert.AreEqual(100, root.PublicMatchingBytes);
        Assert.AreEqual(2L, root.PublicSubtreeFileCount);
        Assert.AreEqual(120L, root.PublicSubtreeBytes);
        Assert.AreEqual(3L, root.TotalSubtreeFileCount);
        Assert.AreEqual(170L, root.TotalSubtreeBytes);
        Assert.AreEqual(1, root.LockedMatchingFileCount);
        Assert.AreEqual(50, root.LockedMatchingBytes);

        PeerBrowseSearchPage directoryMatch = await store.SearchAsync(
            resource.BrowseId, "ÁLBUM", null, null, null, 20);
        Assert.AreEqual(2, directoryMatch.PublicMatchingFileCount);
        Assert.AreEqual(120, directoryMatch.PublicMatchingBytes);
        Assert.AreEqual(2, directoryMatch.Items.Count(row => row.Kind == PeerBrowseSearchEntryKind.File));

        PeerBrowseSearchPage crossingPath = await store.SearchAsync(
            resource.BrowseId, @"áLBUM\tRaCk oNe", null, null, null, 20);
        Assert.AreEqual(1, crossingPath.PublicMatchingFileCount);
        Assert.AreEqual("Track One.FLAC", crossingPath.Items.Single(
            row => row.Kind == PeerBrowseSearchEntryKind.File).Name);

        PeerBrowseSearchPage punctuation = await store.SearchAsync(
            resource.BrowseId, "+B \"", null, null, null, 20);
        Assert.AreEqual(1, punctuation.PublicMatchingFileCount);
        Assert.AreEqual("A+B \"Live\".flac", punctuation.Items.Single(
            row => row.Kind == PeerBrowseSearchEntryKind.File).Name);

        PeerBrowseSearchPage shortQuery = await store.SearchAsync(
            resource.BrowseId, "ú", null, null, null, 20);
        Assert.AreEqual(2, shortQuery.PublicMatchingFileCount);

        PeerBrowseSearchPage emptyDirectory = await store.SearchAsync(
            resource.BrowseId, "Empty", null, null, null, 20);
        CollectionAssert.AreEqual(new[] { "Root", @"Root\Empty" },
            emptyDirectory.Items.Select(row => row.DisplayPath).ToArray());
        Assert.AreEqual(0, emptyDirectory.PublicMatchingFileCount);
        Assert.AreEqual(0, emptyDirectory.LockedMatchingFileCount);

        var tied = new List<PeerBrowseSearchEntry>();
        PeerBrowseSearchPage? previous = null;
        foreach (var expectedKind in new[] { PeerBrowseSearchEntryKind.Directory, PeerBrowseSearchEntryKind.Directory,
                     PeerBrowseSearchEntryKind.Directory, PeerBrowseSearchEntryKind.File })
        {
            previous = await store.SearchAsync(resource.BrowseId, "track secret.mp3",
                previous?.NextSortKey, previous?.NextKind, previous?.NextId, 1);
            Assert.AreEqual(0, previous.PublicMatchingFileCount);
            Assert.AreEqual(1, previous.LockedMatchingFileCount);
            Assert.AreEqual(expectedKind, previous.Items.Single().Kind);
            tied.Add(previous.Items.Single());
        }
        Assert.IsNull(previous!.NextId);
        Assert.AreEqual(tied[2].DisplayPath, tied[3].DisplayPath);
        Assert.AreEqual(4, tied.Select(row => (row.Kind, row.EntryId)).Distinct().Count());
    }

    [TestMethod]
    public async Task MixedSearch_SubstringsUseOrdinalIgnoreCaseForUnicodeAndLiteralPunctuation()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        string[] names = ["ΟΣΟΣ.flac", "οσος.flac", "οσοσ.flac", "FıLE.flac", "file.flac",
            "fİle.flac", "ſound.flac", "sound.flac", "Kelvin.flac", "Kelvin.flac",
            "a+b \"live\".flac", "percent%_star*.flac", "𐐀abc.flac", "𐐨abc.flac", "café.flac", "café.flac",
            "Აbc.flac", "აbc.flac", "𞤀bc.flac", "𞤢bc.flac", "abc--def--ghi.flac",
            "abcdefghi.flac", "aaaaa.flac", "abc\"def.flac", "[literal].flac"];
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Root", PeerShareVisibility.Public, names.Length);
            foreach (string name in names)
                await AddFileAsync(writer, name, 1, "flac");
            await writer.CompleteAsync();
        }

        foreach (string query in new[] { "οσος", "ΟΣΟΣ", "file", "ſou", "sou", "Kel", "kel",
                     "A+B \"", "%_", "star*", "𐐨ab", "𐐀", "CAFÉ", "café", "Აbc", "აbc", "𞤀bc", "𞤢bc", "f", "fl", "not-present", "abcdefghi", "aaaa", "abc\"def", "[literal]", "\0ab",
                     @"ROOT\f", @"\ſou", "root\\", "oot\\", @"Root\not-present", "Root\0\\f" })
        {
            PeerBrowseSearchPage page = await store.SearchAsync(resource.BrowseId, query, null, null, null, 500);
            string[] expected = names.Where(name => (@"Root\" + name).Contains(query,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            CollectionAssert.AreEquivalent(expected, page.Items
                .Where(row => row.Kind == PeerBrowseSearchEntryKind.File).Select(row => row.Name).ToArray(), query);
            Assert.AreEqual(expected.Length, page.PublicMatchingFileCount, query);
            Assert.AreEqual(expected.Length, page.PublicMatchingBytes, query);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MixedSearch_SumsBytesExactlyAcrossDirectoriesWithoutDoubleCountingAncestors(bool saturated)
    {
        long largeFile = saturated ? long.MaxValue : long.MaxValue / 4;
        long expectedPublicBytes = saturated ? long.MaxValue : largeFile + 5;
        long expectedLockedBytes = saturated ? long.MaxValue : largeFile + 4;
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync(@"Root\Public\One", PeerShareVisibility.Public, 2);
            await AddFileAsync(writer, "track-one.flac", largeFile, "flac");
            await AddFileAsync(writer, "track-two.flac", 2, "flac");
            await writer.BeginDirectoryAsync(@"Root\Public\Two", PeerShareVisibility.Public, 1);
            await AddFileAsync(writer, "track-three.flac", 3, "flac");
            await writer.BeginDirectoryAsync(@"Root\Locked\One", PeerShareVisibility.Locked, 1);
            await AddFileAsync(writer, "track-four.flac", largeFile, "flac");
            await writer.BeginDirectoryAsync(@"Root\Locked\Two", PeerShareVisibility.Locked, 1);
            await AddFileAsync(writer, "track-five.flac", 4, "flac");
            await writer.CompleteAsync();
        }

        var totals = (await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, false, null, null, 20)).Items.Single();
        Assert.AreEqual(3, totals.RecursiveFileCount);
        Assert.AreEqual(5, totals.TotalRecursiveFileCount);
        Assert.AreEqual(expectedPublicBytes, totals.RecursiveFileBytes);
        long expectedTotalBytes = saturated ? long.MaxValue : expectedPublicBytes + expectedLockedBytes;
        Assert.AreEqual(expectedTotalBytes, totals.TotalRecursiveFileBytes);
        Assert.AreEqual(expectedTotalBytes, (await store.GetAsync(resource.BrowseId))!.TotalFileBytes);

        PeerBrowseSearchPage first = await store.SearchAsync(resource.BrowseId, "track", null, null, null, 3);
        Assert.AreEqual(3, first.PublicMatchingFileCount);
        Assert.AreEqual(2, first.LockedMatchingFileCount);
        Assert.AreEqual(expectedPublicBytes, first.PublicMatchingBytes);
        Assert.AreEqual(expectedLockedBytes, first.LockedMatchingBytes);
        PeerBrowseSearchEntry root = first.Items.Single(item => item.DisplayPath == "Root");
        Assert.AreEqual(first.PublicMatchingFileCount, root.PublicMatchingFileCount);
        Assert.AreEqual(first.LockedMatchingFileCount, root.LockedMatchingFileCount);
        Assert.AreEqual(expectedPublicBytes, root.PublicMatchingBytes);
        Assert.AreEqual(expectedLockedBytes, root.LockedMatchingBytes);
        PeerBrowseSearchPage next = await store.SearchAsync(resource.BrowseId, "track",
            first.NextSortKey, first.NextKind, first.NextId, 20);
        Assert.AreEqual(first.PublicMatchingBytes, next.PublicMatchingBytes);
        Assert.AreEqual(first.LockedMatchingBytes, next.LockedMatchingBytes);
        Assert.AreEqual(5, first.Items.Concat(next.Items).Count(item => item.Kind == PeerBrowseSearchEntryKind.File));
        PeerBrowseSearchPage empty = await store.SearchAsync(resource.BrowseId, "absent", null, null, null, 20);
        Assert.AreEqual(0, empty.Items.Count);
        Assert.AreEqual(0, empty.PublicMatchingBytes);
        Assert.AreEqual(0, empty.LockedMatchingBytes);
    }

    [TestMethod]
    public async Task RetainedSearchCursorKeepsExactSpellingAcrossRestart()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Music", PeerShareVisibility.Public, 2);
            await AddFileAsync(writer, "one.flac", 1, "flac");
            await AddFileAsync(writer, "two.flac", 2, "flac");
            await writer.CompleteAsync();
        }
        await using var restarted = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseSearchPage first = await restarted.SearchAsync(resource.BrowseId, "flac", null, null, null, 2);
        PeerBrowseSearchEntry file = first.Items.Single(item => item.Kind == PeerBrowseSearchEntryKind.File);
        Assert.AreEqual(@"Music\one.flac", file.DisplayPath);
        string? sortKey = await restarted.ReadSearchSortKeyAsync(resource.BrowseId, file.Kind, file.EntryId);
        Assert.AreEqual(file.DisplayPath, sortKey);
        PeerBrowseSearchPage next = await restarted.SearchAsync(resource.BrowseId, "flac", sortKey, file.Kind, file.EntryId, 2);
        Assert.AreEqual("two.flac", next.Items.Single().Name);
        Assert.IsNull(next.NextId);
    }

    [TestMethod]
    public async Task MixedSeparatorsUseNormalizedLookupButRetainExactDownloadIdentity()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Root/Música\\Disc", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(
                1,
                "Root/Música\\Disc/track.flac",
                10,
                "flac",
                0));
            await writer.EndFileAsync();
            await writer.CompleteAsync();
        }

        PeerDirectorySnapshot snapshot = await store.ReadDirectoryAsync(
            resource.BrowseId,
            new PeerDirectoryIdentity("Peer", @"Root\Música\Disc"));

        Assert.AreEqual("Root/Música\\Disc/track.flac", snapshot.Files.Single().Filename);

        PeerBrowsePage<PeerBrowseDirectoryEntry> directoryMatch = await store.ReadDirectoriesAsync(
            resource.BrowseId, null, "mÚSICA", true, null, null, 10);
        PeerBrowseDirectoryEntry disc = directoryMatch.Items.Single(item => item.Name == "Disc");
        PeerBrowsePage<PeerBrowseFileEntry> fileMatch = await store.ReadFilesAsync(
            resource.BrowseId, disc.DirectoryId, "TRACK", null, null, 10);
        Assert.AreEqual("track.flac", fileMatch.Items.Single().Name);
    }

    [TestMethod]
    public async Task ControlBearingPathsRemainDownloadableAndHaveSafeDisplayText()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Root\\Mu\0sic\\Sub\n", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(
                1, "song\u001B.mp3", 10, "mp3", 0));
            await writer.EndFileAsync();
            await writer.CompleteAsync();
        }

        PeerBrowseDirectoryEntry selected = (await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, true, null, null, 10)).Items.Single(
                item => item.Name == "Sub␊");
        PeerBrowseFileEntry file = (await store.ReadFilesAsync(
            resource.BrowseId, selected.DirectoryId, null, null, null, 10)).Items.Single();
        PeerDirectorySnapshot snapshot = await store.ReadDirectoryAsync(
            resource.BrowseId,
            new PeerDirectoryIdentity("Peer", "Root\\Mu\0sic"));
        PeerBrowseDownloadResolution resolution = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [selected.DirectoryId], []);

        Assert.AreEqual("Root\\Mu␀sic\\Sub␊", selected.DisplayPath);
        Assert.AreEqual("song␛.mp3", file.Name);
        Assert.AreEqual("Root\\Mu\0sic\\Sub\n\\song\u001B.mp3", snapshot.Files.Single().Filename);
        DirectoryTransferPlan plan = resolution.Plans.Single();
        Assert.AreEqual("Sub\n", plan.DisplayRoot);
        Assert.AreEqual("Root\\Mu\0sic\\Sub\n\\song\u001B.mp3", plan.Entries.Single().Target.Filename);
    }

    [TestMethod]
    public async Task ResourcePages_UseCreatedTimeAndBrowseIdContinuation()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
        await using var store = new PeerBrowseArtifactStore(directory.Path, clock);
        var expected = new List<Guid>();
        for (int index = 0; index < 3; index++)
            expected.Add((await store.CreateQueuedAsync("local", "Peer")).BrowseId);
        expected.Sort();

        PeerBrowseResourcePage page1 = await store.ListAsync("local", "Peer", null, null, null, 2);
        PeerBrowseResourcePage page2 = await store.ListAsync(
            "local", "Peer", null, page1.NextCreatedAt, page1.NextBrowseId, 2);

        CollectionAssert.AreEqual(expected.Take(2).ToArray(), page1.Items.Select(x => x.BrowseId).ToArray());
        CollectionAssert.AreEqual(expected.Skip(2).ToArray(), page2.Items.Select(x => x.BrowseId).ToArray());
        Assert.IsNull(page2.NextBrowseId);
    }

    [TestMethod]
    public async Task DownloadSelectionCanonicalizesAntichainAndBuildsExactOrdinaryPlans()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync(@"Root\A", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "a.flac", 10, "flac", 0));
            await writer.EndFileAsync();
            await writer.BeginDirectoryAsync(@"Root\A\Child", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "child.flac", 20, "flac", 0));
            await writer.EndFileAsync();
            await writer.BeginDirectoryAsync(@"Root\Locked", PeerShareVisibility.Locked, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "secret.flac", 30, "flac", 0));
            await writer.EndFileAsync();
            await writer.BeginDirectoryAsync("Other", PeerShareVisibility.Public, 1);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "single.mp3", 40, "mp3", 0));
            await writer.EndFileAsync();
            await writer.CompleteAsync();
        }

        PeerBrowsePage<PeerBrowseDirectoryEntry> directories = await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, true, null, null, 20);
        PeerBrowseDirectoryEntry root = directories.Items.Single(item => item.DisplayPath == "Root");
        PeerBrowseDirectoryEntry a = directories.Items.Single(item => item.DisplayPath == @"Root\A");
        PeerBrowseDirectoryEntry child = directories.Items.Single(item => item.DisplayPath == @"Root\A\Child");
        PeerBrowseDirectoryEntry locked = directories.Items.Single(item => item.DisplayPath == @"Root\Locked");
        PeerBrowseDirectoryEntry other = directories.Items.Single(item => item.DisplayPath == "Other");
        long childFile = (await store.ReadFilesAsync(
            resource.BrowseId, child.DirectoryId, null, null, null, 10)).Items.Single().FileId;
        long standaloneFile = (await store.ReadFilesAsync(
            resource.BrowseId, other.DirectoryId, null, null, null, 10)).Items.Single().FileId;

        PeerBrowseDownloadResolution resolution = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId,
            [root.DirectoryId, root.DirectoryId, a.DirectoryId],
            [childFile, standaloneFile]);

        Assert.AreEqual(1, resolution.CanonicalDirectoryRoots);
        Assert.AreEqual(1, resolution.StandaloneFiles);
        Assert.AreEqual(3, resolution.TotalPublicFiles);
        Assert.AreEqual(70, resolution.TotalPublicBytes);
        Assert.AreEqual(3, resolution.RedundantSelectionsRemoved);
        Assert.AreEqual(1, resolution.LockedBranchesSkipped);
        Assert.AreEqual(2, resolution.Plans.Count);
        Assert.IsTrue(resolution.Plans.Single(plan => plan.DisplayRoot == "Root").RepresentsDirectorySelection);
        Assert.IsFalse(resolution.Plans.Single(plan => plan.DisplayRoot == "Other").RepresentsDirectorySelection);
        PeerBrowseDownloadResolution excluded = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], [childFile, standaloneFile],
            excludedFileIds: [childFile, childFile]);
        Assert.AreEqual(2, excluded.TotalPublicFiles);
        Assert.AreEqual(50, excluded.TotalPublicBytes);
        PeerBrowseDownloadResolution excludedBranch = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], [], excludedDirectoryIds: [a.DirectoryId]);
        Assert.AreEqual(0, excludedBranch.TotalPublicFiles);
        PeerBrowseDownloadResolution survivingFile = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], [standaloneFile], excludedDirectoryIds: [a.DirectoryId]);
        Assert.AreEqual(1, survivingFile.TotalPublicFiles);
        Assert.AreEqual(40, survivingFile.TotalPublicBytes);
        PeerBrowseDownloadResolution restoredFile = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], [childFile], excludedDirectoryIds: [a.DirectoryId]);
        Assert.AreEqual(1, restoredFile.TotalPublicFiles);
        Assert.AreEqual(20, restoredFile.TotalPublicBytes);
        Assert.AreEqual(0, restoredFile.RedundantSelectionsRemoved);
        PeerBrowseDownloadResolution restoredBranch = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId, child.DirectoryId], [], excludedDirectoryIds: [a.DirectoryId]);
        Assert.AreEqual(1, restoredBranch.TotalPublicFiles);
        Assert.AreEqual(20, restoredBranch.TotalPublicBytes);
        Assert.AreEqual(0, restoredBranch.RedundantSelectionsRemoved);
        PeerBrowseDownloadResolution redundantRestoredFile = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId, child.DirectoryId], [childFile], excludedDirectoryIds: [a.DirectoryId]);
        Assert.AreEqual(1, redundantRestoredFile.RedundantSelectionsRemoved);
        Assert.IsFalse(excluded.Plans.SelectMany(plan => plan.Entries)
            .Any(entry => entry.Target.Filename.EndsWith("child.flac", StringComparison.Ordinal)));
        await Assert.ThrowsExactlyAsync<PeerBrowseSelectionException>(() =>
            store.ResolveDownloadSelectionAsync(resource.BrowseId, [root.DirectoryId], [],
                excludedFileIds: [long.MaxValue]));
        DirectoryTransferPlan rootPlan = resolution.Plans.Single(plan => plan.DisplayRoot == "Root");
        CollectionAssert.AreEqual(
            new[] { @"Root\A\a.flac", @"Root\A\Child\child.flac" },
            rootPlan.Entries.Select(entry => entry.Target.Filename).ToArray());
        CollectionAssert.AreEqual(new[] { "A" }, rootPlan.Entries[0].RelativeDirectoryComponents.ToArray());
        CollectionAssert.AreEqual(new[] { "A", "Child" }, rootPlan.Entries[1].RelativeDirectoryComponents.ToArray());

        PeerBrowseDownloadResolution multipleRoots = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId,
            [a.DirectoryId, other.DirectoryId],
            []);
        Assert.AreEqual(2, multipleRoots.CanonicalDirectoryRoots);
        CollectionAssert.AreEqual(
            new[] { "Other", "A" },
            multipleRoots.Plans.Select(plan => plan.DisplayRoot).ToArray());

        await Assert.ThrowsExactlyAsync<PeerBrowseSelectionException>(() =>
            store.ResolveDownloadSelectionAsync(resource.BrowseId, [locked.DirectoryId], []));
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task LargeArtifactPagesAndExpandsFromOneCompactDirectoryId()
    {
        const int fileCount = 2_000;
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Large", PeerShareVisibility.Public, fileCount);
            for (int index = 0; index < fileCount; index++)
            {
                await writer.BeginFileAsync(new PeerBrowseWireFile(
                    1, $"file-{index:D4}.bin", 1, "bin", 0));
                await writer.EndFileAsync();
            }
            await writer.CompleteAsync();
        }

        PeerBrowseDirectoryEntry root = (await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, false, null, null, 10)).Items.Single();
        PeerBrowsePage<PeerBrowseFileEntry> page = await store.ReadFilesAsync(
            resource.BrowseId, root.DirectoryId, null, null, null, 25);
        PeerBrowseDownloadResolution resolution = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], []);
        PeerBrowseSearchPage search = await store.SearchAsync(
            resource.BrowseId, "file", null, null, null, 25);

        Assert.AreEqual(25, page.Items.Count);
        Assert.IsNotNull(page.NextId);
        Assert.AreEqual(25, search.Items.Count);
        Assert.IsNotNull(search.NextId);
        Assert.AreEqual(fileCount, search.PublicMatchingFileCount);
        Assert.AreEqual(fileCount, root.RecursiveFileCount);
        Assert.AreEqual(fileCount, resolution.Plans.Single().Entries.Count);
        Assert.AreEqual(1, resolution.CanonicalDirectoryRoots);
    }

    [TestMethod]
    [TestCategory("Load")]
    [DoNotParallelize]
    public async Task HundredThousandFileBrowse_FiltersAndReadsBoundedPages()
    {
        const int directoryCount = 100;
        const int filesPerDirectory = 1_000;
        string libraryPath = @"Library\" + new string('p', 96);
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            for (int directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
            {
                await writer.BeginDirectoryAsync($@"{libraryPath}\Album-{directoryIndex:D3}",
                    PeerShareVisibility.Public, filesPerDirectory);
                for (int fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
                {
                    await writer.BeginFileAsync(new PeerBrowseWireFile(
                        1, $"track-{directoryIndex:D3}-{fileIndex:D3}.flac", 36_000_000, "flac", 4));
                    await writer.AddAttributeAsync(new PeerBrowseWireAttribute(0, 900));
                    await writer.AddAttributeAsync(new PeerBrowseWireAttribute(1, 240));
                    await writer.AddAttributeAsync(new PeerBrowseWireAttribute(4, 48_000));
                    await writer.AddAttributeAsync(new PeerBrowseWireAttribute(5, 24));
                    await writer.EndFileAsync();
                }
            }
            await writer.CompleteAsync();
        }
        TimeSpan ingestion = stopwatch.Elapsed;
        long ingestionAllocation = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        long? filterWritesBefore = ProcessWriteBytes();
        stopwatch.Restart();
        PeerBrowseSearchPage selective = await store.SearchAsync(
            resource.BrowseId, "track-042-017", null, null, null, 100);
        TimeSpan selectiveElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        PeerBrowseSearchPage broad = await store.SearchAsync(
            resource.BrowseId, "flac", null, null, null, 100);
        TimeSpan broadElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        PeerBrowseSearchPage next = await store.SearchAsync(resource.BrowseId, "flac",
            broad.NextSortKey, broad.NextKind, broad.NextId, 100);
        TimeSpan continuationElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        PeerBrowseSearchPage shortQuery = await store.SearchAsync(resource.BrowseId, "fl", null, null, null, 100);
        TimeSpan shortElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        PeerBrowseDirectoryEntry album = (await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, true, null, null, 100)).Items.Single(
                item => item.DisplayPath == $@"{libraryPath}\Album-042");
        PeerBrowsePage<PeerBrowseFileEntry> firstPage = await store.ReadFilesAsync(
            resource.BrowseId, album.DirectoryId, null, null, null, 500);
        TimeSpan pageElapsed = stopwatch.Elapsed;
        long? filterWrites = ProcessWriteBytes() - filterWritesBefore;

        Assert.AreEqual(1, selective.PublicMatchingFileCount);
        Assert.AreEqual(100_000, broad.PublicMatchingFileCount);
        Assert.AreEqual(100, broad.Items.Count);
        Assert.AreEqual(broad.PublicMatchingFileCount, next.PublicMatchingFileCount);
        Assert.AreEqual(3_600_000_000_000L, next.PublicMatchingBytes);
        Assert.AreEqual(broad.PublicMatchingFileCount, shortQuery.PublicMatchingFileCount);
        Assert.AreEqual(broad.PublicMatchingBytes, shortQuery.PublicMatchingBytes);
        Assert.AreEqual(200, broad.Items.Concat(next.Items).Select(row => (row.Kind, row.EntryId)).Distinct().Count());
        Assert.AreEqual(500, firstPage.Items.Count);
        Assert.AreEqual(4, firstPage.Items[0].Attributes!.Count);
        Assert.AreEqual(24, firstPage.Items[0].BitDepth);
        Assert.IsNotNull(firstPage.NextId);
        if (filterWrites is { } bytesWritten)
            Assert.IsTrue(bytesWritten < 4 * 1024 * 1024,
                $"Read-only filtering wrote {bytesWritten:N0} bytes; avoid spooling every matching path.");
        Assert.IsTrue(ingestionAllocation < 250_000_000,
            $"100k files with four attributes allocated {ingestionAllocation:N0} bytes; avoid per-field provider objects.");
        Assert.IsTrue(selectiveElapsed < TimeSpan.FromMilliseconds(100),
            $"Selective filtering took {selectiveElapsed}.");
        Assert.IsTrue(pageElapsed < TimeSpan.FromMilliseconds(100),
            $"A directory plus 500-file page took {pageElapsed}.");
        Assert.IsTrue(new[] { broadElapsed, continuationElapsed, shortElapsed }
                .All(elapsed => elapsed < TimeSpan.FromMilliseconds(300)),
            "Broad filtering of 100k shares must remain within the 300-ms browse budget.");
        Console.WriteLine($"Peer browse 100 albums x1k files, long paths/four attributes: ingest {ingestion.TotalMilliseconds:F0} ms, "
            + $"{ingestionAllocation:N0} allocated bytes; "
            + $"selective filter {selectiveElapsed.TotalMilliseconds:F0} ms; "
            + $"broad filter {broadElapsed.TotalMilliseconds:F0} ms; "
            + $"continuation {continuationElapsed.TotalMilliseconds:F0} ms; "
            + $"short filter {shortElapsed.TotalMilliseconds:F0} ms; "
            + $"root/page {pageElapsed.TotalMilliseconds:F0} ms, filtering writes {filterWrites:N0} bytes.");

        static long? ProcessWriteBytes()
            => File.Exists("/proc/self/io")
                ? long.Parse(File.ReadLines("/proc/self/io").Single(line => line.StartsWith("write_bytes:", StringComparison.Ordinal))
                    .Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                : null;
    }

    [TestMethod]
    public async Task InformationalByteTotalsSaturateWithoutRejectingValidFiles()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new PeerBrowseArtifactStore(directory.Path);
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using (PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource))
        {
            await writer.BeginDirectoryAsync("Huge", PeerShareVisibility.Public, 2);
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "one.bin", long.MaxValue, "bin", 0));
            await writer.EndFileAsync();
            await writer.BeginFileAsync(new PeerBrowseWireFile(1, "two.bin", 1, "bin", 0));
            await writer.EndFileAsync();
            await writer.CompleteAsync();
        }

        PeerBrowseResource complete = (await store.GetAsync(resource.BrowseId))!;
        PeerBrowseDirectoryEntry root = (await store.ReadDirectoriesAsync(
            resource.BrowseId, null, null, false, null, null, 10)).Items.Single();
        PeerBrowseDownloadResolution resolution = await store.ResolveDownloadSelectionAsync(
            resource.BrowseId, [root.DirectoryId], []);

        Assert.AreEqual(long.MaxValue, complete.TotalFileBytes);
        Assert.AreEqual(long.MaxValue, root.RecursiveFileBytes);
        Assert.AreEqual(long.MaxValue, resolution.TotalPublicBytes);
        Assert.AreEqual(long.MaxValue, resolution.Plans.Single().TotalKnownBytes);
    }

    private static async Task<PeerBrowseResource> CompleteEmptyAsync(PeerBrowseArtifactStore store)
    {
        PeerBrowseResource resource = await store.CreateQueuedAsync("local", "Peer");
        await store.MarkRunningAsync(resource.BrowseId);
        await using PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource);
        await writer.CompleteAsync();
        return resource;
    }

    private static async Task AddFileAsync(
        PeerBrowseArtifactWriter writer,
        string name,
        long size,
        string extension)
    {
        await writer.BeginFileAsync(new PeerBrowseWireFile(1, name, size, extension, 0));
        await writer.EndFileAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan amount) => utcNow += amount;
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sockseek-peer-browse-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
