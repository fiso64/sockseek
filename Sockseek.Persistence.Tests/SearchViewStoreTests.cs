using Sockseek.Persistence.Write;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Diagnostics;
using System.Text.Json;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class SearchViewStoreTests
{
    [TestMethod]
    public async Task ConditionFactsUpgradeExpiresOldViewsButPreservesSourcesAndCurrentFacts()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        await using var context = await new SockseekDbContextFactory(SockseekDbContextOptions.Create(
            new SockseekSqliteOptions(directory.DatabasePath))).CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync("20261031010000_CompactSearchDirectories");
        Guid source = Guid.NewGuid();
        var update = Kernel().Apply([Input(1, "Peer", @"Artist\Album\Track.flac", 320)], 1, true);
        var obsolete = new List<Guid>();
        foreach (SearchViewProjectionKind kind in Enum.GetValues<SearchViewProjectionKind>())
        {
            var old = await store.CreateAsync(source, kind, "{}");
            await store.PublishSeededAsync(directory.Writes, old.Id, update, "Complete");
            // Simulate the pre-fix cache. Today's writer always stamps current facts.
            Assert.AreEqual(1, await context.Database.ExecuteSqlRawAsync(
                "UPDATE search_views SET definition_json='{{}}' WHERE id={0}", old.Id.ToString("D")));
            obsolete.Add(old.Id);
        }
        var current = await store.CreateAsync(source, SearchViewProjectionKind.Files, "{}");
        current = await store.PublishSeededAsync(directory.Writes, current.Id, update, "Complete");
        string before = JsonSerializer.Serialize(await store.GetFilesAsync(current.Id, current.Revision, null, 500));
        await context.Database.MigrateAsync();
        foreach (Guid id in obsolete)
            Assert.IsNull(await store.GetAsync(id));
        Assert.AreEqual(before, JsonSerializer.Serialize(await store.GetFilesAsync(current.Id, current.Revision, null, 500)));
        Assert.AreEqual(1L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_observations").SingleAsync());
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [TestMethod]
    public async Task PublicationRequiresCommittedOriginalsAndCannotCreateThem()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var view = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.Files, "{}");
        var update = Kernel().Apply([Input(1, "Peer", @"Share\Track.flac", 320)], 1, false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.PublishAsync(view.Id, update, "Live"));
        var unchanged = (await store.GetAsync(view.Id))!;
        Assert.AreEqual(view.Revision, unchanged.Revision);
        Assert.AreEqual(view.Counters, unchanged.Counters);
        Assert.AreEqual(view.ConsumedSequence, unchanged.ConsumedSequence);
        Assert.IsFalse(unchanged.IsComplete);
        await using var context = await new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath))).CreateDbContextAsync();
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM search_observations").SingleAsync());
        var published = await store.PublishSeededAsync(directory.Writes, view.Id, update, "Live");
        Assert.AreEqual(1L, published.Counters.ProjectedFileCount);
        Assert.IsFalse(published.IsComplete);
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.GenericDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AggregateAlbums)]
    public async Task DirectoryStorageRoundTripPreservesRevisionsRetrievalsAndOptions(SearchViewProjectionKind kind)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        await store.CreateAsync(Guid.NewGuid(), kind, "{}");
        var view = await store.CreateAsync(Guid.NewGuid(), kind, "{}");
        var settings = new SearchSettings { NecessaryCond = new(), PreferredCond = new(), NecessaryFolderCond = new(), MinSharesAggregate = 1 };
        var query = new SongQuery { Artist = "ELO", Title = "Track" };
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(kind, query,
            new AlbumQuery { Artist = "ELO", Album = "Time" }, IncludeFullResults: true), settings);
        var first = Input(1, "Péér", @"共有\ELO\Time\01. Track.flac", 320);
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply([first], 1, false), "Live");
        long firstRevision = view.Revision;
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply([
            Input(2, "Péér", @"共有\ELO\Time\02. Track.flac", 320),
            Input(3, "péér", @"共有\ELO\Time\01. Track.flac", 320),
        ], 2, false), "Live");
        long secondRevision = view.Revision;
        var folder = (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 100))!.Items.Single(x => x.Username == "Péér");
        var sorter = new IncrementalResultSorter(query, settings,
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(), retainProjectedRows: false);
        var cover = sorter.AddRangeAndGetProjected(
            [Input(100, "Péér", @"共有\ELO\Time\Cover.jpg", 0) with { Extension = ".jpg", Size = 2000 }]).ToArray();
        var snapshot = new PeerDirectorySnapshot(new("Péér", folder.FolderPath),
            [new PeerFileTarget(new("Péér", cover.Single().Input.Filename), 2000, ".jpg")], isComplete: false);
        view = (await store.PublishRetrievedDirectoryAsync(view.Id, folder.Ref, snapshot, cover, first)).View;
        long[] revisions = [firstRevision, secondRevision, view.Revision];
        async Task<string[]> Capture()
        {
            var result = new List<string>();
            foreach (long revision in revisions)
            {
                var folders = (await store.GetDirectoriesAsync(view.Id, revision, null, 100, initialFileLimit: 24))!;
                var children = new List<StoredSearchViewDirectoryFilePage>();
                foreach (var item in folders.Items)
                    children.Add((await store.GetDirectoryFilesAsync(view.Id, item.Ref, revision, null, 500))!);
                var albums = kind == SearchViewProjectionKind.AggregateAlbums
                    ? await store.GetAggregateAlbumsAsync(view.Id, revision, null, 100, initialFileLimit: 24) : null;
                var options = new List<StoredSearchViewAggregateAlbumOptionPage>();
                if (albums != null)
                    foreach (var album in albums.Items)
                        options.Add((await store.GetAggregateAlbumOptionsAsync(view.Id, album.Ref, revision, null, 100, initialFileLimit: 24))!);
                result.Add(JsonSerializer.Serialize(new { Folders = folders, Children = children, Albums = albums, Options = options }));
            }
            result.Add(JsonSerializer.Serialize(await store.GetDirectoryObservationAsync(view.SourceJobId, snapshot.Identity)));
            return result.ToArray();
        }
        string[] before = await Capture();
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.GetService<IMigrator>().MigrateAsync("20261030230000_CompactSearchRevisions");
            await context.Database.MigrateAsync();
            Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        }
        CollectionAssert.AreEqual(before, await Capture());
        await directory.RestartWriterAsync();
        await using var reopened = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await reopened.InitializeAsync();
        view = await reopened.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(4, "Péér", @"共有\ELO\Time\03. Track.flac", 320)], 3, true), "Complete");
        Assert.IsTrue(view.IsComplete);
        Assert.IsNotNull(await reopened.GetDirectoryObservationAsync(view.SourceJobId, snapshot.Identity));
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(0L, 1000L)]
    public async Task RetrievedDirectoryTotalsPreserveUnknownAndZeroSnapshotSizes(long? snapshotSize, long? expectedBytes)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var view = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.GenericDirectories, "{}");
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.GenericDirectories, new SongQuery { Title = "Track" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new(), PreferredCond = new() });
        var first = Input(1, "Peer", @"Share\Album\First Track.flac", 320);
        var second = Input(2, "Peer", @"Share\Album\Second Track.flac", 320) with { Size = 2000 };
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply([first, second], 1, false), "Live");
        var folder = (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 100))!.Items.Single();
        var snapshot = new PeerDirectorySnapshot(new("Peer", @"Share\Album"),
            [new PeerFileTarget(new("Peer", first.Filename), 1000, ".flac"),
             new PeerFileTarget(new("Peer", second.Filename), snapshotSize, ".flac")], isComplete: true);
        view = (await store.PublishRetrievedDirectoryAsync(view.Id, folder.Ref, snapshot, [], first)).View;
        var retrieved = (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 100))!.Items.Single();
        Assert.AreEqual(2L, retrieved.RetrievedFileCount);
        Assert.AreEqual(expectedBytes, retrieved.RetrievedBytes);
        Assert.IsTrue(retrieved.IsFullyRetrieved);
        Assert.AreEqual(2, (await store.GetDirectoryFilesAsync(view.Id, folder.Ref, view.Revision, null, 500))!.Items.Count);
    }

    [TestMethod]
    [TestCategory("Load")]
    [DoNotParallelize] // Process write counters must exclude other test classes.
    public async Task ExpiringHundredThousandFileViewBoundsWritesBesideAnotherRetainedSource()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-30T00:00:00Z"));
        var inputs = Enumerable.Range(0, 100_000).Select(index =>
            Input(index + 1, "Peer", $@"Share\Track-{index:D6}.flac", 320)).ToArray();
        SearchViewKernel Project(string? filter = null) => new(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.Files, new SongQuery { Title = "Track" }, IncludeFullResults: true, FilterText: filter),
            new SearchSettings { NecessaryCond = new(), PreferredCond = new() }, retainProjectedRows: false);
        Guid source = Guid.NewGuid();
        StoredSearchView narrow, other;
        await using (var setup = new SearchViewStore(directory.DatabasePath, directory.Writes, clock))
        {
            await setup.InitializeAsync();
            var full = await setup.CreateAsync(source, SearchViewProjectionKind.Files, "{}", TimeSpan.FromDays(1));
            await setup.PublishSeededAsync(directory.Writes, full.Id, Project().Apply(inputs, 1, true), "Complete");
            narrow = await setup.CreateAsync(source, SearchViewProjectionKind.Files, "{}", TimeSpan.FromDays(3));
            narrow = await setup.PublishSeededAsync(directory.Writes, narrow.Id, Project("Track-000000").Apply(inputs, 1, true), "Complete");
            other = await setup.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.Files, "{}", TimeSpan.FromDays(10));
            other = await setup.PublishSeededAsync(directory.Writes, other.Id, Project().Apply(inputs, 1, true), "Complete");
        }
        // Drain setup, including its asynchronous checkpoint, before attribution.
        await directory.RestartWriterAsync();
        clock.Advance(TimeSpan.FromDays(2));
        long? before = WriteBytes();
        await using (var pruning = new SearchViewStore(directory.DatabasePath, directory.Writes, clock))
        {
            await pruning.InitializeAsync();
            Assert.AreEqual(1, await pruning.PruneExpiredAsync());
        }
        await directory.RestartWriterAsync();
        long? writes = WriteBytes() - before;
        if (writes.HasValue)
            Assert.IsLessThan(35_000_000L, writes.Value,
                "Expiring the View must not repeatedly rewrite its file/index pages.");
        await using var retained = new SearchViewStore(directory.DatabasePath, directory.Writes, clock);
        await retained.InitializeAsync();
        Assert.AreEqual(1, (await retained.GetFilesAsync(narrow.Id, narrow.Revision, null, 500))!.Items.Count);
        Assert.AreEqual(100_000L, (await retained.GetAsync(other.Id))!.Counters.ProjectedFileCount);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.AreEqual(1, await retained.PruneExpiredAsync());
        Assert.AreEqual(500, (await retained.GetFilesAsync(other.Id, other.Revision, null, 500))!.Items.Count);

        static long? WriteBytes() => OperatingSystem.IsLinux()
            ? long.Parse(File.ReadLines("/proc/self/io").Single(line => line.StartsWith("write_bytes:", StringComparison.Ordinal)).AsSpan("write_bytes:".Length).Trim())
            : null;
    }

    [TestMethod]
    public async Task SourcePinsRetainFactsUntilTheLastNonemptyViewAndRejectCrossSourceLinks()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-30T00:00:00Z"));
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes, clock);
        await store.InitializeAsync();
        Guid source = Guid.NewGuid();
        SearchProjectionInput[] inputs = [Input(1, "Peer", @"Share\Track.flac", 320),
            Input(2, "Peer", @"Share\Other.flac", 320), Input(3, "Peer", @"Share\Third.flac", 320)];
        var full = await store.CreateAsync(source, SearchViewProjectionKind.Files, "{}", TimeSpan.FromDays(1));
        full = await store.PublishSeededAsync(directory.Writes, full.Id, Kernel().Apply(inputs, 1, true), "Complete");
        SearchViewKernel Filtered(string filter) => new(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.Files, new SongQuery { Title = "Track" }, IncludeFullResults: true, FilterText: filter),
            new SearchSettings { NecessaryCond = new(), PreferredCond = new() });
        var narrow = await store.CreateAsync(source, SearchViewProjectionKind.Files, "{}", TimeSpan.FromDays(3));
        narrow = await store.PublishSeededAsync(directory.Writes, narrow.Id, Filtered("Track").Apply(inputs, 1, true), "Complete");
        var empty = await store.CreateAsync(source, SearchViewProjectionKind.Files, "{}", TimeSpan.FromDays(10));
        empty = await store.PublishSeededAsync(directory.Writes, empty.Id, Filtered("absent").Apply(inputs, 1, true), "Complete");
        var other = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.Files, "{}", TimeSpan.FromDays(10));
        other = await store.PublishSeededAsync(directory.Writes, other.Id, Kernel().Apply([inputs[0]], 1, true), "Complete");
        long sourceId = (await store.GetAsync(narrow.Id))!.SourceId!.Value;
        long otherSource = (await store.GetAsync(other.Id))!.SourceId!.Value;
        Assert.IsNull((await store.GetAsync(empty.Id))!.SourceId, "An empty View must not retain source facts.");
        string retained = JsonSerializer.Serialize(await store.GetFilesAsync(narrow.Id, narrow.Revision, null, 500));
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE search_view_files SET source_id={0} WHERE view_id={1}", otherSource, narrow.StorageId));
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE search_view_files SET observation_id=(SELECT min(id) FROM search_observations WHERE source_id={0}) WHERE view_id={1}", otherSource, narrow.StorageId));
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => context.Database.ExecuteSqlRawAsync(
            "DELETE FROM search_observations WHERE id=(SELECT observation_id FROM search_view_files WHERE view_id={0} LIMIT 1)", narrow.StorageId));

        clock.Advance(TimeSpan.FromDays(2));
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        Assert.AreEqual(3L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_observations WHERE source_id={0}", sourceId).SingleAsync(),
            "A source pin retains the whole source, without per-file reverse lookup.");
        Assert.AreEqual(retained, JsonSerializer.Serialize(await store.GetFilesAsync(narrow.Id, narrow.Revision, null, 500)));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_observations WHERE source_id={0}", sourceId).SingleAsync());
        Assert.AreEqual(0, (await store.GetFilesAsync(empty.Id, empty.Revision, null, 500))!.Items.Count);
        Assert.AreEqual(1, (await store.GetFilesAsync(other.Id, other.Revision, null, 500))!.Items.Count);
        await directory.RestartWriterAsync();
        await using var reopened = new SearchViewStore(directory.DatabasePath, directory.Writes, clock);
        await reopened.InitializeAsync();
        Assert.AreEqual(1, (await reopened.GetFilesAsync(other.Id, other.Revision, null, 500))!.Items.Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RawHistoryAndRefinedViewsShareFactsWithIndependentRetention(bool pruneRawFirst)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-26T00:00:00Z"));
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        Guid source = Guid.NewGuid(), runtime = Guid.NewGuid();
        long completed = clock.GetUtcNow().AddDays(-40).ToUnixTimeMilliseconds();
        context.RuntimeSessions.Add(new() { Id = runtime });
        context.Jobs.Add(new() { Id = source, WorkflowId = source, LastRuntimeId = runtime,
            DisplayId = 1, Kind = "Search", LifecycleState = "Terminal", TerminalOutcome = "Succeeded", CompletedAtUtc = completed });
        context.SearchJobs.Add(new() { JobId = source, Revision = 2, ResultCount = 1, LockedFileCount = 1,
            IsComplete = true, CompletedAtUtc = completed, ResultPersistenceState = "Complete" });
        await context.SaveChangesAsync();
        SearchProjectionInput[] inputs =
        [
            Input(1, "Péér", @"共有\Track.flac", 320) with
                { Attributes = [new("曲\0", int.MinValue, 999), new("BitRate", 320, 0), new("duplicate", int.MaxValue, 999)],
                    HasFreeUploadSlot = null, UploadSpeed = null, QueueLength = null },
            Input(2, "Péér", @"共有\Track.flac", 128) with
                { Visibility = SearchResultVisibility.Locked, Size = -1, Attributes = pruneRawFirst ? [] : null },
        ];
        async Task PersistRawAsync()
        {
            foreach (var input in inputs)
                await TestPersistenceWriter.SeedSearchResultAsync(context, new()
                {
                    SearchJobId = source, Sequence = input.Sequence, Revision = input.Revision,
                    Username = input.Username, RemoteFilename = input.Filename, ResponseFileCount = input.ResponseFileCount,
                    SizeBytes = input.Size, BitRate = input.BitRate, BitDepth = input.BitDepth, SampleRate = input.SampleRate,
                    DurationSeconds = input.Length, Extension = input.Extension, UploadSpeed = input.UploadSpeed,
                    HasFreeUploadSlot = input.HasFreeUploadSlot, QueueLength = input.QueueLength,
                    AttributesJson = input.Attributes == null ? null : SearchObservationWriter.SerializeAttributes(input.Attributes),
                    ObservedAtUtc = input.ObservedAtUtc.ToUnixTimeMilliseconds(), Visibility = input.Visibility.ToString(),
                });
        }
        await PersistRawAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes, clock);
        await store.InitializeAsync();
        var first = await store.CreateAsync(source, SearchViewProjectionKind.Files, "{}", retention: TimeSpan.FromDays(1));
        first = await store.PublishSeededAsync(directory.Writes, first.Id, Kernel().Apply(inputs, 2, true), "Complete");
        var stricter = new SearchViewKernel(new FileSearchProjection(new SongQuery { Title = "Track" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions { MinBitrate = 512 } });
        var second = await store.CreateAsync(source, SearchViewProjectionKind.Files, "{}", retention: TimeSpan.FromDays(3));
        second = await store.PublishSeededAsync(directory.Writes, second.Id, stricter.Apply(inputs, 2, true), "Complete");
        var history = new Sockseek.Persistence.Read.SearchHistoryReader(factory);
        var raw = (await history.GetRawResultsAsync(source, 0, 500))!;
        Assert.AreEqual(2, raw.Items.Count);
        var firstKeys = (await history.GetObservationKeysAsync(source, 0, 1))!;
        Assert.AreEqual(raw.Metadata, firstKeys.Metadata);
        CollectionAssert.AreEqual(new[] { (raw.Items[0].Sequence, checked((int)raw.Items[0].Revision)) }, firstKeys.Items.ToArray());
        Assert.AreEqual(raw.Items[0].Sequence, firstKeys.NextSequence);
        var lastKeys = (await history.GetObservationKeysAsync(source, firstKeys.NextSequence!.Value, 1))!;
        CollectionAssert.AreEqual(new[] { (raw.Items[1].Sequence, checked((int)raw.Items[1].Revision)) }, lastKeys.Items.ToArray());
        Assert.IsNull(lastKeys.NextSequence);
        for (int i = 0; i < inputs.Length; i++)
            Assert.AreEqual(JsonSerializer.Serialize(inputs[i]), JsonSerializer.Serialize(raw.Items[i]));
        var publicPeer = await history.GetFolderResultsAsync(source, new("Péér", "共有"), new AlbumQuery(), SearchResultVisibility.Public);
        Assert.AreEqual(1, publicPeer.Count);
        Assert.AreEqual(JsonSerializer.Serialize(inputs[0]), JsonSerializer.Serialize(publicPeer[0]));
        var lockedPeer = await history.GetFolderResultsAsync(source, new("Péér", "共有"), new AlbumQuery(), SearchResultVisibility.Locked);
        Assert.AreEqual(1, lockedPeer.Count);
        Assert.AreEqual(inputs[1].Sequence, lockedPeer[0].Sequence);
        Assert.AreEqual(0, (await history.GetFolderResultsAsync(source, new("péér", "共有"), new AlbumQuery(), SearchResultVisibility.Public)).Count,
            "Peer identities are case-sensitive wire values.");
        Assert.AreEqual(raw.Metadata, await history.GetMetadataAsync(source));
        Assert.AreEqual(JsonSerializer.Serialize(raw.Items[0]),
            JsonSerializer.Serialize((await history.GetResultAsync(source, "Péér", @"共有\Track.flac"))!.Result));
        Assert.IsNull((await history.GetResultAsync(source, "péér", @"共有\Track.flac"))!.Result);
        var streamed = new List<SearchProjectionInput>();
        await foreach (var input in history.ReadProjectionInputsAsync(source)) streamed.Add(input);
        Assert.AreEqual(JsonSerializer.Serialize(inputs), JsonSerializer.Serialize(streamed));
        async Task<long> FactCountAsync() => await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_observations").SingleAsync();
        Assert.AreEqual(2L, await FactCountAsync(), "Two differently ranked Views and raw history share the same observations.");
        string retained = JsonSerializer.Serialize(await store.GetFilesAsync(second.Id, second.Revision, null, 500));
        Assert.AreNotEqual(first.Counters.PreferredFileCount, second.Counters.PreferredFileCount);

        var retention = new Sockseek.Persistence.Read.RetentionService(directory.Writes, new()
        {
            SearchResultAge = TimeSpan.FromDays(1), CompletedJobHistoryAge = null, UnsuccessfulJobHistoryAge = null,
            MaximumRetainedJobs = null, TransferHistoryAge = null,
        }, clock);
        if (!pruneRawFirst)
        {
            clock.Advance(TimeSpan.FromDays(4));
            Assert.AreEqual(2, await store.PruneExpiredAsync());
            Assert.AreEqual(2L, await FactCountAsync(), "Expiring every View must preserve public raw history.");
            Assert.AreEqual(2, (await history.GetRawResultsAsync(source, 0, 500))!.Items.Count);
            Assert.AreEqual(2, (await retention.RunBatchAsync()).PrunedSearchResults);
            Assert.AreEqual(0L, await FactCountAsync());
            return;
        }
        Assert.AreEqual(2, (await retention.RunBatchAsync()).PrunedSearchResults);
        raw = (await history.GetRawResultsAsync(source, 0, 500))!;
        Assert.AreEqual("Pruned", raw.Metadata.ResultPersistenceState);
        Assert.AreEqual(0, raw.Items.Count);
        Assert.AreEqual(2L, await FactCountAsync());
        Assert.AreEqual(retained, JsonSerializer.Serialize(await store.GetFilesAsync(second.Id, second.Revision, null, 500)));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        Assert.AreEqual(2L, await FactCountAsync());
        Assert.AreEqual(retained, JsonSerializer.Serialize(await store.GetFilesAsync(second.Id, second.Revision, null, 500)));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        Assert.AreEqual(0L, await FactCountAsync());
    }

    [TestMethod]
    public async Task ConditionExplanationsSurvivePagingAndMigration()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var kernel = new SearchViewKernel(new FileSearchProjection(
            new SongQuery { Artist = "Artist", Album = "Album", Title = "Track", Length = 180 }, IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions
                {
                    Formats = ["flac"], LengthTolerance = 3, MinBitrate = 256,
                    MinSampleRate = 44_100, MinBitDepth = 16, StrictTitle = true,
                    StrictArtist = true, StrictAlbum = true, AllowedUsers = ["Preferred"],
                },
            });
        var view = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.Files, "{}");
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
        [
            Input(1, "Preferred", @"Artist\Album\Track.flac", 320) with { BitDepth = 16 },
            Input(2, "Other", @"Artist\Album\Track.mp3", 128) with { Length = 200, SampleRate = 22_050 },
        ], 2, true), "Complete");
        var page = (await store.GetFilesAsync(view.Id, view.Revision, null, 1))!;
        var preferred = page.Items.Single();
        Assert.AreEqual(SearchPreferenceTier.Preferred, preferred.PreferenceTier);
        SearchPreferenceCondition[] expected = [SearchPreferenceCondition.Format, SearchPreferenceCondition.Length,
            SearchPreferenceCondition.Bitrate, SearchPreferenceCondition.SampleRate, SearchPreferenceCondition.BitDepth,
            SearchPreferenceCondition.Title, SearchPreferenceCondition.Artist, SearchPreferenceCondition.Album,
            SearchPreferenceCondition.Username];
        CollectionAssert.AreEqual(expected, preferred.ConfiguredPreferredConditions.ToArray());
        CollectionAssert.AreEqual(expected, preferred.SatisfiedPreferredConditions.ToArray());
        Assert.IsNotNull(page.NextPosition);
        var continued = (await store.GetFilesAsync(view.Id, view.Revision, page.NextPosition, 1))!;
        var other = continued.Items.Single();
        Assert.AreEqual(SearchPreferenceTier.Other, other.PreferenceTier);
        CollectionAssert.AreEqual(expected, other.ConfiguredPreferredConditions.ToArray());
        Assert.IsTrue(other.SatisfiedPreferredConditions.Count < expected.Length);
        Assert.IsTrue(other.SatisfiedPreferredConditions.Contains(SearchPreferenceCondition.Title));
        var emptyKernel = new SearchViewKernel(new FileSearchProjection(new SongQuery { Title = "Track" }, true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions() });
        var empty = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.Files, "{}");
        empty = await store.PublishSeededAsync(directory.Writes, empty.Id, emptyKernel.Apply([Input(1, "Peer", "Track.flac", 320)], 1, true), "Complete");
        async Task<string> ReadPagesAsync() => JsonSerializer.Serialize(new
        {
            First = await store.GetFilesAsync(view.Id, view.Revision, null, 1),
            Continued = await store.GetFilesAsync(view.Id, view.Revision, page.NextPosition, 1),
            Unconfigured = await store.GetFilesAsync(empty.Id, empty.Revision, null, 1),
        });
        var unconfigured = (await store.GetFilesAsync(empty.Id, empty.Revision, null, 1))!.Items.Single();
        Assert.AreEqual(0, unconfigured.ConfiguredPreferredConditions.Count);
        Assert.AreEqual(0, unconfigured.SatisfiedPreferredConditions.Count);
        string before = await ReadPagesAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync("20261027150000_ExpireObsoleteGenericSearchViews");
        await context.Database.MigrateAsync();
        Assert.AreEqual(before, await ReadPagesAsync());
    }

    [TestMethod]
    [DataRow("20261025090000_NormalizeAggregateAlbumParents")]
    [DataRow("20261027120000_CompactSearchDirectoryMembership")]
    public async Task MembershipIndexMigrationPreservesHistorySelectionAndCascades(string previous)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(
            new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        var migrator = context.Database.GetService<IMigrator>();
        await context.Database.MigrateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.GenericDirectories, "{\"genericDirectoryGroupingVersion\":2}");
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.GenericDirectories, new SongQuery { Title = "Track" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions() });
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(1, "Péér", @"Share\曲\Track 1.flac", 320)], 1, false), "Live");
        long firstRevision = view.Revision;
        string folder = (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 100))!.Items.Single().Ref;
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(2, "Péér", @"Share\曲\Track 2.flac", 320)], 2, true), "Complete");
        var files = (await store.GetDirectoryFilesAsync(view.Id, folder, view.Revision, null, 500))!.Items;
        string selectedRef = files[1].Ref;

        async Task VerifyAsync()
        {
            var historical = (await store.GetDirectoryFilesAsync(view.Id, folder, firstRevision, null, 500))!.Items;
            var current = (await store.GetDirectoryFilesAsync(view.Id, folder, view.Revision, null, 500))!.Items;
            Assert.AreEqual(1, historical.Count);
            Assert.AreEqual(files[0].Ref, historical[0].Ref);
            Assert.AreEqual(2, current.Count);
            CollectionAssert.AreEquivalent(files.Select(file => file.Ref).ToArray(),
                current.Select(file => file.Ref).ToArray());
            var selected = new List<StoredSearchViewCommitItem>();
            await foreach (var item in store.ReadCommitItemsAsync(view.Id, view.Revision,
                SearchViewProjectionKind.GenericDirectories.ToString(), "Only", new HashSet<string> { selectedRef }))
                selected.Add(item);
            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("DirectoryFile", selected[0].Kind);
        }

        await VerifyAsync();
        // Seed with today's writer, then downgrade to exercise upgrades from a
        // populated old schema without keeping an obsolete writer in production.
        await migrator.MigrateAsync(previous);
        Assert.AreEqual(2L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_view_files").SingleAsync());
        await context.Database.MigrateAsync();
        await VerifyAsync();
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        await context.Database.ExecuteSqlRawAsync("DELETE FROM search_views;");
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_view_directory_files").SingleAsync());
    }

    [TestMethod]
    public async Task FailedPublicationRollsBackAndTheWriterCanPublishAgain()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView initial = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.Files, "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(SearchViewProjectionKind.Files, new SongQuery { Title = "Fixture" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions() });
        SearchProjectionInput[] inputs = Enumerable.Range(1, 600)
            .Select(index => Input(index, index switch { 1 => "peer", 2 => "Peer", 3 => "Péér", _ => $"Peer-{index}" },
                $@"Share\File-{index:D4}.flac", 320)).ToArray();
        SearchViewKernelUpdate update = kernel.Apply(inputs, 2, true);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = directory.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Fail late in a populated publication: View rows roll back while its
        // independently ingested source facts remain available for retry.
        command.CommandText = """
            CREATE TRIGGER fail_later_file BEFORE INSERT ON search_view_files
            WHEN NEW.sequence = 550
            BEGIN SELECT RAISE(ABORT, 'Injected publication failure'); END;
            """;
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => store.PublishSeededAsync(directory.Writes, initial.Id, update, "Complete"));
        StoredSearchView afterFailure = (await store.GetAsync(initial.Id))!;
        Assert.AreEqual(initial.Revision, afterFailure.Revision);
        Assert.AreEqual(initial.Counters, afterFailure.Counters);
        Assert.AreEqual(initial.ConsumedSequence, afterFailure.ConsumedSequence);
        Assert.AreEqual(initial.IsComplete, afterFailure.IsComplete);
        Assert.IsNull(await store.GetRevisionAsync(initial.Id, initial.Revision + 1));
        Assert.AreEqual(0, (await store.GetFilesAsync(initial.Id, initial.Revision, null, 10))!.Items.Count);
        command.CommandText = "SELECT count(*) FROM search_observations;";
        Assert.AreEqual(600L, (long)(await command.ExecuteScalarAsync())!,
            "A failed publication must preserve independently committed source facts.");

        command.CommandText = "SELECT count(*) FROM search_view_peers;";
        Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync())!,
            "A failed publication must also roll back newly observed peers.");
        command.CommandText = "DROP TRIGGER fail_later_file;";
        await command.ExecuteNonQueryAsync();
        var conflicting = update with
        {
            ChangedFiles = [update.ChangedFiles[0], update.ChangedFiles[1] with
                { Input = update.ChangedFiles[1].Input with { Sequence = update.ChangedFiles[0].Input.Sequence,
                    Revision = update.ChangedFiles[0].Input.Revision } }],
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => store.PublishSeededAsync(directory.Writes, initial.Id, conflicting, "Complete"));
        Assert.AreEqual(initial.Revision, (await store.GetAsync(initial.Id))!.Revision);
        StoredSearchView published = await store.PublishSeededAsync(directory.Writes, initial.Id, update, "Complete");
        Assert.AreEqual(initial.Revision + 1, published.Revision);
        Assert.IsTrue(published.IsComplete);
        Assert.AreEqual(600, published.Counters.ObservedPeerCount,
            "Peer counts must preserve exact spelling, including case and Unicode, across the batch.");
        CollectionAssert.AreEquivalent(inputs.Select(input => input.Filename).ToArray(),
            (await store.GetFilesAsync(initial.Id, published.Revision, null, 1000))!.Items
                .Select(item => item.Input.Filename).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.PublishSeededAsync(directory.Writes,
            initial.Id, update with { SourceRevision = 0 }, "Complete"));
        Assert.AreEqual(PersistenceHealthState.Healthy, directory.Health.State,
            "A rejected stale request must not poison persistence; a rolled-back publication can recover.");
    }

    [TestMethod]
    public async Task FileAndDirectoryRefsAreStableAcrossViewsOfOneSourceOnly()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        SearchProjectionInput input = Input(1, "Péér", @"Share\" + new string('曲', 400) + @" é\Track 🚀.flac", 900) with
        {
            Size = long.MaxValue - 1,
            BitRate = int.MaxValue,
            BitDepth = int.MaxValue,
            SampleRate = int.MaxValue,
            Length = int.MaxValue,
            UploadSpeed = int.MaxValue,
            QueueLength = int.MaxValue,
            HasFreeUploadSlot = false,
            Attributes = [new("BitRate", int.MaxValue), new("Length", int.MaxValue)],
        };
        SearchViewKernelUpdate update = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            }).Apply(
            [input], 1, false);
        Guid source = Guid.NewGuid();
        StoredSearchView first = await store.CreateAsync(
            source, SearchViewProjectionKind.GenericDirectories, "{}");
        StoredSearchView second = await store.CreateAsync(
            source, SearchViewProjectionKind.GenericDirectories, "{}");
        StoredSearchView otherSource = await store.CreateAsync(
            Guid.NewGuid(), SearchViewProjectionKind.GenericDirectories, "{}");
        first = await store.PublishSeededAsync(directory.Writes, first.Id, update, "Live");
        second = await store.PublishSeededAsync(directory.Writes, second.Id, update, "Live");
        otherSource = await store.PublishSeededAsync(directory.Writes, otherSource.Id, update, "Live");

        async Task<(string File, string Directory)> Refs(StoredSearchView view)
        {
            var file = (await store.GetFilesAsync(view.Id, view.Revision, null, 10))!.Items.Single();
            Assert.AreEqual(JsonSerializer.Serialize(input), JsonSerializer.Serialize(file.Input),
                "Stored facts must preserve exact Unicode, nullable metadata and full-width numeric values.");
            return (file.Ref, (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 10))!.Items.Single().Ref);
        }
        var firstRefs = await Refs(first);
        Assert.AreEqual(firstRefs, await Refs(second));
        Assert.AreNotEqual(firstRefs, await Refs(otherSource));
    }

    [TestMethod]
    public async Task AggregateRefsAndOptionsRemainStableAcrossViewsAndMigration()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        Guid source = Guid.NewGuid();
        var retainedViews = new List<StoredSearchView>();
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
            MinSharesAggregate = 1,
            Relax = true,
        };
        SearchViewKernelUpdate trackUpdate = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateTracks,
                new SongQuery { Artist = "ELO" }),
            settings).Apply(
            [Input(1, "Peer", @"ELO - Track.flac", 900)], 1, false);
        SearchViewKernelUpdate albumUpdate = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateAlbums,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }),
            settings).Apply(
            [Input(1, "Peer", @"ELO\Time\01. Track.flac", 900)], 1, false);
        foreach (var (kind, update) in new[]
        {
            (SearchViewProjectionKind.AggregateTracks, trackUpdate),
            (SearchViewProjectionKind.AggregateAlbums, albumUpdate),
        })
        {
            // Each mode's fixture describes a different raw search response.
            source = Guid.NewGuid();
            StoredSearchView first = await store.CreateAsync(source, kind, "{}");
            StoredSearchView second = await store.CreateAsync(source, kind, "{}");
            first = await store.PublishSeededAsync(directory.Writes, first.Id, update, "Live");
            second = await store.PublishSeededAsync(directory.Writes, second.Id, update, "Live");
            string firstRef = kind == SearchViewProjectionKind.AggregateTracks
                ? (await store.GetAggregateTracksAsync(first.Id, first.Revision, null, 10))!.Items.Single().Ref
                : (await store.GetAggregateAlbumsAsync(first.Id, first.Revision, null, 10))!.Items.Single().Ref;
            string secondRef = kind == SearchViewProjectionKind.AggregateTracks
                ? (await store.GetAggregateTracksAsync(second.Id, second.Revision, null, 10))!.Items.Single().Ref
                : (await store.GetAggregateAlbumsAsync(second.Id, second.Revision, null, 10))!.Items.Single().Ref;
            Assert.AreEqual(firstRef, secondRef, kind.ToString());
            retainedViews.AddRange([first, second]);
        }
        async Task<string> ReadSnapshotAsync(StoredSearchView view)
        {
            if (view.ProjectionKind == nameof(SearchViewProjectionKind.AggregateTracks))
            {
                var page = (await store.GetAggregateTracksAsync(view.Id, view.Revision, null, 100))!;
                return JsonSerializer.Serialize(new { Page = page,
                    Options = await store.GetAggregateTrackOptionsAsync(view.Id, page.Items.Single().Ref,
                        view.Revision, null, 500) });
            }
            var albums = (await store.GetAggregateAlbumsAsync(view.Id, view.Revision, null, 100,
                initialFileLimit: 24))!;
            return JsonSerializer.Serialize(new { Page = albums,
                Options = await store.GetAggregateAlbumOptionsAsync(view.Id, albums.Items.Single().Ref,
                    view.Revision, null, 100, initialFileLimit: 24) });
        }
        var before = new List<string>();
        foreach (var view in retainedViews) before.Add(await ReadSnapshotAsync(view));
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(
            new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync("20261027120000_CompactSearchDirectoryMembership");
        await context.Database.MigrateAsync();
        for (int index = 0; index < retainedViews.Count; index++)
        {
            if (retainedViews[index].ProjectionKind == nameof(SearchViewProjectionKind.AggregateAlbums))
                Assert.IsNull(await store.GetAsync(retainedViews[index].Id),
                    "Older album caches lack directory visibility history and must be recreated.");
            else
                Assert.AreEqual(before[index], await ReadSnapshotAsync(retainedViews[index]),
                    "Parent table rebuilding must preserve supported groups, options and previews.");
        }
        Assert.AreEqual(2L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_observations").SingleAsync(),
            "Expiring derived Views must retain source facts.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileSortOrdersPageTheWholeRevisionWithoutDuplicates(bool readOrder)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        SearchProjectionInput[] inputs =
        [
            Input(1, "A", @"Music\Track A.flac", 900) with
                { Size = 100, UploadSpeed = 3000, QueueLength = 3 },
            Input(2, "B", @"Music\Track B.flac", 900) with
                { Size = 300, UploadSpeed = 1000, QueueLength = 1 },
            Input(3, "C", @"Music\Track C.flac", 900) with
                { Size = 200, UploadSpeed = 2000, QueueLength = 2 },
            Input(4, "D", @"Music\Track D.flac", 900) with
                { Size = -1, UploadSpeed = null, QueueLength = null },
            Input(5, "E", @"Music\Track E.flac", 900) with
                { Size = 0, UploadSpeed = 1000, QueueLength = 1, Visibility = SearchResultVisibility.Locked },
            Input(6, "F", @"Music\Track F.flac", 900) with
                { Size = 100, UploadSpeed = 3000, QueueLength = 3 },
        ];
        SearchProjectionInput late = Input(7, "G", @"Music\Track G.flac", 900) with
            { Size = 50, UploadSpeed = 4000, QueueLength = 0 };
        var expectations = new Dictionary<SearchViewSortOrder, (string[] Old, string[] Current)>
        {
            [SearchViewSortOrder.UploadSpeed] = (["A", "F", "C", "B", "E", "D"], ["G", "A", "F", "C", "B", "E", "D"]),
            [SearchViewSortOrder.QueueDepth] = (["B", "E", "C", "A", "F", "D"], ["G", "B", "E", "C", "A", "F", "D"]),
            [SearchViewSortOrder.ItemSizeAscending] = (["D", "E", "A", "F", "C", "B"], ["D", "E", "G", "A", "F", "C", "B"]),
            [SearchViewSortOrder.ItemSizeDescending] = (["B", "C", "A", "F", "D", "E"], ["B", "C", "A", "F", "G", "D", "E"]),
        };
        foreach (var (order, expected) in expectations)
        {
            var kernel = Kernel();
            SearchViewKernelUpdate initial = kernel.Apply(inputs, 6, false);
            StoredSearchView view = await store.CreateAsync(
                Guid.NewGuid(), SearchViewProjectionKind.Files,
                "{\"projection\":{\"sortOrder\":\"" + (readOrder ? SearchViewSortOrder.Relevance : order) + "\"}}");
            view = await store.PublishSeededAsync(directory.Writes, view.Id, initial, "Live");
            long oldRevision = view.Revision;
            var pinned = (await store.GetFilesAsync(view.Id, oldRevision, null, 2, sortOrder: readOrder ? order : null))!;
            CollectionAssert.AreEqual(expected.Old.Take(2).ToArray(), pinned.Items.Select(item => item.Input.Username).ToArray());
            view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply([late], 7, true), "Complete");
            var remaining = (await store.GetFilesAsync(view.Id, oldRevision, pinned.NextPosition, 500, sortOrder: readOrder ? order : null))!;
            CollectionAssert.AreEqual(expected.Old, pinned.Items.Concat(remaining.Items)
                .Select(item => item.Input.Username).ToArray(), $"{order}: pinned cursor after arrivals");
            Assert.IsNull(remaining.NextPosition);
            Assert.AreEqual(readOrder ? SearchViewSortOrder.Relevance : order, (await store.GetAsync(view.Id))!.SortOrder);
            foreach (int pageSize in new[] { 1, 2 })
            {
                var actual = new List<StoredSearchViewFile>();
                SearchViewFilePosition? cursor = null;
                do
                {
                    StoredSearchViewFilePage page = (await store.GetFilesAsync(
                        view.Id, view.Revision, cursor, pageSize, sortOrder: readOrder ? order : null))!;
                    actual.AddRange(page.Items);
                    cursor = page.NextPosition;
                }
                while (cursor != null);
                CollectionAssert.AreEqual(expected.Current, actual.Select(item => item.Input.Username).ToArray(),
                    $"{order}, page size {pageSize}");
                Assert.AreEqual(-1L, actual.Single(item => item.Input.Username == "D").Input.Size,
                    "Sort normalization must preserve the peer's reported metadata.");
                Assert.AreEqual(SearchResultVisibility.Locked,
                    actual.Single(item => item.Input.Username == "E").Input.Visibility);
            }
        }
    }

    [TestMethod]
    public async Task FileOrderingIndexMigrationPreservesFactsAndPinnedCursors()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(
            new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        var migrator = context.Database.GetService<IMigrator>();
        const string previous = "20261025160000_AlignHistoryIndexes";
        await context.Database.MigrateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var kernel = Kernel();
        var view = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.Files,
            "{\"projection\":{\"sortOrder\":\"QueueDepth\"}}");
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
        [
            Input(1, "Péér", @"共有\Track.flac", 900) with
                { QueueLength = 2, UploadSpeed = 2000, Size = 100, Attributes = [new("曲", 42, 999)] },
            Input(2, "péér", @"共有\Track.flac", 128) with
                { QueueLength = 2, UploadSpeed = null, Size = -1 },
            Input(3, "Péér", @"共有\Track.flac", 320) with
                { QueueLength = null, Size = 0, Visibility = SearchResultVisibility.Locked },
        ], 3, false), "Live");
        long oldRevision = view.Revision;
        var pinned = (await store.GetFilesAsync(view.Id, oldRevision, null, 1))!;
        Assert.IsNotNull(pinned.NextPosition);
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(4, "late peer", @"共有\Late.flac", 900) with { QueueLength = 0 }], 4, true), "Complete");
        var current = (await store.GetFilesAsync(view.Id, view.Revision, null, 1))!;
        Assert.AreEqual("late peer", current.Items.Single().Input.Username);

        async Task<string> ReadFactsAndPagesAsync() => JsonSerializer.Serialize(new
        {
            Old = await store.GetFilesAsync(view.Id, oldRevision, pinned.NextPosition, 500),
            Current = await store.GetFilesAsync(view.Id, view.Revision, null, 500),
            Continued = await store.GetFilesAsync(view.Id, view.Revision, current.NextPosition, 500),
        });
        string before = await ReadFactsAndPagesAsync();
        await migrator.MigrateAsync(previous);
        Assert.AreEqual(4L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_view_files").SingleAsync());
        await context.Database.MigrateAsync();
        Assert.AreEqual(before, await ReadFactsAndPagesAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectorySizeAndCountSortsUseTheBoundRevisionTotals(bool readOrder)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);
        SearchViewKernelUpdate first = kernel.Apply(
        [
            Input(1, "Peer-A", @"Share\A\Track.flac", 900) with
                { Size = 100, UploadSpeed = 3000, QueueLength = 3 },
            Input(2, "Peer-B", @"Share\B\Track.flac", 900) with
                { Size = 300, UploadSpeed = 1000, QueueLength = 1 },
            Input(3, "Peer-C", @"Share\C\Track.flac", 900) with
                { Size = 200, UploadSpeed = 2000, QueueLength = 2 },
        ], 3, false);
        SearchViewKernelUpdate second = kernel.Apply(
            [Input(4, "Peer-A", @"Share\A\Track 2.flac", 900) with
                { Size = 250, UploadSpeed = 3000, QueueLength = 3 }],
            4, false);
        var expectations = new Dictionary<SearchViewSortOrder, string[]>
        {
            [SearchViewSortOrder.UploadSpeed] = ["A", "C", "B"],
            [SearchViewSortOrder.DirectorySizeAscending] = ["C", "B", "A"],
            [SearchViewSortOrder.DirectorySizeDescending] = ["A", "B", "C"],
            [SearchViewSortOrder.FileCountAscending] = ["B", "C", "A"],
            [SearchViewSortOrder.FileCountDescending] = ["A", "B", "C"],
            [SearchViewSortOrder.DirectoryNameAscending] = ["A", "B", "C"],
            [SearchViewSortOrder.DirectoryNameDescending] = ["C", "B", "A"],
        };
        foreach (var (order, expected) in expectations)
        {
            StoredSearchView view = await store.CreateAsync(
                Guid.NewGuid(), SearchViewProjectionKind.GenericDirectories,
                "{\"projection\":{\"sortOrder\":\"" + (readOrder ? SearchViewSortOrder.Relevance : order) + "\"}}");
            view = await store.PublishSeededAsync(directory.Writes, view.Id, first, "Live");
            long oldRevision = view.Revision;
            view = await store.PublishSeededAsync(directory.Writes, view.Id, second, "Live");
            foreach (int pageSize in new[] { 1, 2 })
            {
                var actual = new List<string>();
                SearchViewDirectoryPosition? cursor = null;
                do
                {
                    StoredSearchViewDirectoryPage page = (await store.GetDirectoriesAsync(
                        view.Id, view.Revision, cursor, pageSize, sortOrder: readOrder ? order : null))!;
                    actual.AddRange(page.Items.Select(item => item.FolderPath.Split('\\')[^1]));
                    cursor = page.NextPosition;
                }
                while (cursor != null);
                CollectionAssert.AreEqual(expected, actual.ToArray(), $"{order}, page size {pageSize}");
            }
            if (order == SearchViewSortOrder.DirectorySizeDescending)
            {
                StoredSearchViewDirectoryPage old = (await store.GetDirectoriesAsync(
                    view.Id, oldRevision, null, 3, sortOrder: readOrder ? order : null))!;
                CollectionAssert.AreEqual(
                    new[] { "B", "C", "A" },
                    old.Items.Select(item => item.FolderPath.Split('\\')[^1]).ToArray());
            }
        }
    }

    [TestMethod]
    public async Task DirectoryNameOrderUsesNumericCaseInsensitiveCursorComparison()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            });
        SearchViewKernelUpdate update = kernel.Apply(
        [
            Input(1, "Peer", @"Share\Folder 10\Track.flac", 900),
            Input(2, "Peer", @"Share\folder 2\Track.flac", 900),
            Input(3, "Peer", @"Share\Folder 1\Track.flac", 900),
        ], 3, false);
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(), SearchViewProjectionKind.GenericDirectories,
            "{\"projection\":{\"sortOrder\":\"DirectoryNameAscending\"}}");
        view = await store.PublishSeededAsync(directory.Writes, view.Id, update, "Live");
        var names = new List<string>();
        SearchViewDirectoryPosition? cursor = null;
        do
        {
            StoredSearchViewDirectoryPage page = (await store.GetDirectoriesAsync(
                view.Id, view.Revision, cursor, 1))!;
            names.AddRange(page.Items.Select(item => item.FolderPath.Split('\\')[^1]));
            cursor = page.NextPosition;
        }
        while (cursor != null);
        CollectionAssert.AreEqual(
            new[] { "Folder 1", "folder 2", "Folder 10" }, names.ToArray());
    }

    [TestMethod]
    public async Task FilteredAlbumPublishesEarlierChildrenWhenTheFolderBecomesVisible()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.AlbumDirectories,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AlbumDirectories,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" },
                FilterText: "Target"),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);

        var first = Input(1, "Peer", @"ELO\Time\01. Twilight.flac", 900);
        var locked = first with { Sequence = 2, Visibility = SearchResultVisibility.Locked };
        var hidden = kernel.Apply([first, locked], 1, false);
        Assert.AreEqual(0, hidden.ChangedFiles.Count);
        view = await store.PublishSeededAsync(directory.Writes, view.Id, hidden, "Live");
        long hiddenRevision = view.Revision;
        Assert.AreEqual(0, (await store.GetDirectoriesAsync(view.Id, hiddenRevision, null, 10))!.Items.Count);
        Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, hiddenRevision, null, 500))!.Items.Count);

        // Upgrade an older publication shape once, before a later child reveals
        // the folder. Cleanup must leave the canonical facts available to backfill.
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        view = await store.PublishSeededAsync(directory.Writes, view.Id, hidden with
        {
            SourceRevision = 2,
            ChangedFiles = Kernel().Apply([first, locked], 1, false).ChangedFiles,
        }, "Live");
        Assert.AreEqual(2, (await store.GetFilesAsync(view.Id, view.Revision, null, 500))!.Items.Count);
        await context.Database.GetService<IMigrator>().MigrateAsync("20261030140000_RemoveUnpublishedTrackCandidates");
        await context.Database.MigrateAsync();
        Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, view.Revision, null, 500))!.Items.Count);

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(3, "Peer", @"ELO\Time\02. Target.flac", 900)], 3, true), "Complete");
        StoredSearchViewDirectory folder = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, null, 10))!.Items.Single();
        Assert.AreEqual(2L, folder.PublicMatchingFileCount);
        Assert.AreEqual(1L, folder.LockedMatchingFileCount);
        StoredSearchViewDirectoryFilePage children = (await store.GetDirectoryFilesAsync(
            view.Id, folder.Ref, view.Revision, null, 10))!;
        CollectionAssert.AreEquivalent(
            new[] { "01. Twilight.flac", "01. Twilight.flac", "02. Target.flac" },
            children.Items.Select(item => item.RelativePath).ToArray());
        Assert.AreEqual(1, children.Items.Count(item => item.File.Input.Visibility == SearchResultVisibility.Locked));
        Assert.AreEqual(3, (await store.GetFilesAsync(view.Id, view.Revision, null, 500))!.Items.Count);
        Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, hiddenRevision, null, 500))!.Items.Count);
        var selected = new List<StoredSearchViewCommitItem>();
        string firstRef = children.Items.Single(item => item.File.Input.Sequence == 1).Ref;
        await foreach (var item in store.ReadCommitItemsAsync(view.Id, view.Revision,
            "AlbumDirectories", "Only", new HashSet<string>([firstRef]))) selected.Add(item);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(first, selected[0].File!.Input);
        await directory.RestartWriterAsync();
        await using var reopened = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await reopened.InitializeAsync();
        Assert.AreEqual(3, (await reopened.GetDirectoryFilesAsync(view.Id, folder.Ref, view.Revision, null, 10))!.Items.Count);
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.Files, "root")]
    [DataRow(SearchViewProjectionKind.GenericDirectories, "root")]
    [DataRow(SearchViewProjectionKind.AlbumDirectories, "root")]
    [DataRow(SearchViewProjectionKind.AggregateTracks, "root")]
    [DataRow(SearchViewProjectionKind.AggregateAlbums, "root")]
    [DataRow(SearchViewProjectionKind.GenericDirectories, "children")]
    [DataRow(SearchViewProjectionKind.AggregateTracks, "options")]
    [DataRow(SearchViewProjectionKind.AggregateAlbums, "options")]
    [DataRow(SearchViewProjectionKind.AggregateAlbums, "chosen")]
    public async Task PruningDuringAReadCannotReturnEmptyRowsWithNonemptyCounters(
        SearchViewProjectionKind kind, string read)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes, clock);
        await store.InitializeAsync();
        var projection = kind is SearchViewProjectionKind.AlbumDirectories or SearchViewProjectionKind.AggregateAlbums
            ? new SearchViewProjectionDefinition(kind, AlbumQuery: new AlbumQuery { Artist = "Artist", Album = "Album" })
            : new SearchViewProjectionDefinition(kind, new SongQuery { Artist = "Artist", Title = "Track" });
        var kernel = new SearchViewKernel(projection, new SearchSettings
        {
            NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(), MinSharesAggregate = 1,
        });
        StoredSearchView view = await store.CreateAsync(Guid.NewGuid(), kind, "{}", retention: TimeSpan.FromMinutes(1));
        view = await store.PublishSeededAsync(directory.Writes, view.Id,
            kernel.Apply([Input(1, "Peer", @"Artist\Album\01 - Track.flac", 900)], 1, true), "Complete");
        string? groupRef = null;
        string? directoryRef = null;
        if (kind == SearchViewProjectionKind.AggregateAlbums)
        {
            var group = (await store.GetAggregateAlbumsAsync(view.Id, view.Revision, null, 100))!.Items.Single();
            groupRef = group.Ref;
            directoryRef = group.Representative.Ref;
        }
        else if (kind == SearchViewProjectionKind.AggregateTracks)
            groupRef = (await store.GetAggregateTracksAsync(view.Id, view.Revision, null, 100))!.Items.Single().Ref;
        else if (read == "children")
            directoryRef = (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 100))!.Items.Single().Ref;
        int armed = 1;
        int pruned = 0;
        using var observer = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Sockseek.Persistence.SearchViews",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "metadata" || activity.GetTagItem("view.id") is not Guid id
                    || id != view.Id || Interlocked.Exchange(ref armed, 0) == 0) return;
                // Expire and prune after the request has read its counters but
                // before it reads its page. No timer or polling race is needed.
                clock.Advance(TimeSpan.FromMinutes(2));
                pruned = store.PruneExpiredAsync().GetAwaiter().GetResult();
            },
        };
        ActivitySource.AddActivityListener(observer);
        (StoredSearchViewRevision? Revision, int Rows) page = read switch
        {
            "children" => await ChildPage(),
            "options" => await OptionPage(),
            "chosen" => await ChosenPage(),
            _ => await RootPage(),
        };
        async Task<(StoredSearchViewRevision?, int)> RootPage() => kind switch
        {
            SearchViewProjectionKind.Files => await FilePage(),
            SearchViewProjectionKind.AggregateTracks => await TrackPage(),
            SearchViewProjectionKind.AggregateAlbums => await AlbumPage(),
            _ => await DirectoryPage(),
        };
        Assert.AreEqual(1, pruned);
        if (page.Revision != null)
        {
            Assert.AreEqual(1L, page.Revision.Counters.TopLevelItemCount);
            Assert.AreEqual(1, page.Rows, "A read must return an unavailable view or rows consistent with its counters.");
        }
        Assert.IsNull(await store.GetAsync(view.Id));

        async Task<(StoredSearchViewRevision?, int)> FilePage()
        { var result = await store.GetFilesAsync(view.Id, view.Revision, null, 100); return (result?.Revision, result?.Items.Count ?? 0); }
        async Task<(StoredSearchViewRevision?, int)> DirectoryPage()
        {
            var result = await store.GetDirectoriesAsync(view.Id, view.Revision, null, 100, initialFileLimit: 24);
            if (result != null) AssertPreview(result.DirectoryFilePreviews, result.Items.Single().Ref);
            return (result?.Revision, result?.Items.Count ?? 0);
        }
        async Task<(StoredSearchViewRevision?, int)> TrackPage()
        { var result = await store.GetAggregateTracksAsync(view.Id, view.Revision, null, 100); return (result?.Revision, result?.Items.Count ?? 0); }
        async Task<(StoredSearchViewRevision?, int)> AlbumPage()
        {
            var result = await store.GetAggregateAlbumsAsync(view.Id, view.Revision, null, 100, initialFileLimit: 24);
            if (result != null) AssertPreview(result.DirectoryFilePreviews, result.Items.Single().Representative.Ref);
            return (result?.Revision, result?.Items.Count ?? 0);
        }
        async Task<(StoredSearchViewRevision?, int)> ChildPage()
        {
            var result = await store.GetDirectoryFilesAsync(view.Id, directoryRef!, view.Revision, null, 100);
            return (result?.Revision, result?.Items.Count ?? 0);
        }
        async Task<(StoredSearchViewRevision?, int)> OptionPage()
        {
            if (kind == SearchViewProjectionKind.AggregateTracks)
            {
                var result = await store.GetAggregateTrackOptionsAsync(view.Id, groupRef!, view.Revision, null, 100);
                return (result?.Revision, result?.Items.Count ?? 0);
            }
            var albums = await store.GetAggregateAlbumOptionsAsync(
                view.Id, groupRef!, view.Revision, null, 100, initialFileLimit: 24);
            if (albums != null) AssertPreview(albums.DirectoryFilePreviews, albums.Items.Single().Ref);
            return (albums?.Revision, albums?.Items.Count ?? 0);
        }
        async Task<(StoredSearchViewRevision?, int)> ChosenPage()
        {
            var result = await store.ResolveChosenOptionsAsync(view.Id, view.Revision,
                new Dictionary<string, string> { [groupRef!] = directoryRef! }, initialFileLimit: 24);
            if (result != null) AssertPreview(result.DirectoryFilePreviews, result.Albums[groupRef!].Ref);
            return (result?.Revision, result?.Albums.Count ?? 0);
        }
        static void AssertPreview(IReadOnlyDictionary<string, StoredSearchViewDirectoryFilePreview> previews, string folder)
        {
            Assert.IsTrue(previews.TryGetValue(folder, out var preview), "The returned folder must include its file prefix.");
            Assert.AreEqual(1, preview.Items.Count, "Pruning cannot empty an otherwise populated folder preview.");
            Assert.AreEqual(@"Artist\Album\01 - Track.flac", preview.Items[0].File.Input.Filename);
            Assert.IsNull(preview.NextPosition);
        }
    }

    [TestMethod]
    public async Task ExpiredViewsDisappearAndPruneWithoutAWait()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        await using var store = new SearchViewStore(
            directory.DatabasePath, directory.Writes,
            clock);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.Files,
            "{}",
            retention: TimeSpan.FromMinutes(1));
        Assert.IsNotNull(await store.GetFilesAsync(view.Id, 0, null, 10));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsNull(await store.GetFilesAsync(view.Id, 0, null, 10));
        var incomplete = new List<StoredSearchView>();
        await foreach (StoredSearchView item in store.ReadIncompleteAsync())
            incomplete.Add(item);
        Assert.AreEqual(0, incomplete.Count);
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        Assert.IsNull(await store.GetAsync(view.Id));
    }

    [TestMethod]
    public async Task ImmutableRevisionsPageBySortKeyAndRowsSurviveRestart()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        string path = directory.DatabasePath;
        Guid viewId;
        SearchViewKernelUpdate firstUpdate;
        SearchViewKernelUpdate secondUpdate;
        var kernel = Kernel();
        firstUpdate = kernel.Apply(
            [Input(1, "slow", @"Music\Track.mp3", 192)],
            sourceRevision: 1,
            isComplete: false);
        secondUpdate = kernel.Apply(
            [Input(2, "fast", @"Music\Track.flac", 900)],
            sourceRevision: 2,
            isComplete: false);

        await using (var store = new SearchViewStore(path, directory.Writes))
        {
            await store.InitializeAsync();
            StoredSearchView view = await store.CreateAsync(
                Guid.NewGuid(),
                SearchViewProjectionKind.Files,
                "{\"projection\":\"file\"}");
            viewId = view.Id;
            Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, 0, null, 10))!.Items.Count);

            view = await store.PublishSeededAsync(directory.Writes, view.Id, firstUpdate, "Live");
            Assert.AreEqual(1L, view.Revision);
            view = await store.PublishSeededAsync(directory.Writes, view.Id, secondUpdate, "Live");
            Assert.AreEqual(2L, view.Revision);

            StoredSearchViewFilePage revisionOne = (await store.GetFilesAsync(
                view.Id, 1, null, 10))!;
            CollectionAssert.AreEqual(
                new[] { "slow" },
                revisionOne.Items.Select(item => item.Input.Username).ToArray());
            Assert.IsFalse(revisionOne.Items.Single().NecessaryConditionsSatisfied);

            StoredSearchViewFilePage firstPage = (await store.GetFilesAsync(
                view.Id, 2, null, 1))!;
            Assert.AreEqual(1, firstPage.Items.Count);
            Assert.IsNotNull(firstPage.NextPosition);
            StoredSearchViewFilePage secondPage = (await store.GetFilesAsync(
                view.Id, 2, firstPage.NextPosition, 1))!;
            CollectionAssert.AreEquivalent(
                new[] { "slow", "fast" },
                firstPage.Items.Concat(secondPage.Items)
                    .Select(item => item.Input.Username)
                    .ToArray());

            SearchViewKernelUpdate thirdUpdate = kernel.Apply(
                [Input(3, "later", @"Music\Track.ogg", 320) with
                {
                    Visibility = SearchResultVisibility.Locked,
                }],
                sourceRevision: 3,
                isComplete: false);
            view = await store.PublishSeededAsync(directory.Writes, view.Id, thirdUpdate, "Live");
            Assert.AreEqual(3L, view.Revision);
        }

        await directory.RestartWriterAsync();
        await using (var reopened = new SearchViewStore(path, directory.Writes))
        {
            await reopened.InitializeAsync();
            StoredSearchView view = (await reopened.GetAsync(viewId))!;
            Assert.AreEqual(3L, view.Revision);
            Assert.AreEqual(3L, view.Counters.ProjectedFileCount);
            StoredSearchViewFilePage revisionTwo = (await reopened.GetFilesAsync(
                viewId, 2, null, 10))!;
            Assert.AreEqual(2, revisionTwo.Items.Count);
            Assert.IsFalse(revisionTwo.Items.Single(item => item.Input.Username == "slow")
                .NecessaryConditionsSatisfied);
            Assert.IsTrue(revisionTwo.Items.Single(item => item.Input.Username == "fast")
                .NecessaryConditionsSatisfied);
            StoredSearchViewFilePage current = (await reopened.GetFilesAsync(viewId, 3, null, 10))!;
            Assert.AreEqual(3, current.Items.Count);
            var later = new HashSet<string>(StringComparer.Ordinal)
            {
                current.Items.Single(item => item.Input.Username == "later").Ref,
            };
            Assert.AreEqual(new StoredSearchViewSelectionCounts(0, 0, 0),
                await reopened.CountTopLevelSelectionAsync(viewId, 2, "Files", "Only", later));
            Assert.AreEqual(new StoredSearchViewSelectionCounts(2, 0, 2),
                await reopened.CountTopLevelSelectionAsync(viewId, 2, "Files", "AllExcept", later),
                "An exclusion admitted later must not change a pinned revision's counts.");
            Assert.AreEqual(new StoredSearchViewSelectionCounts(0, 1, 1),
                await reopened.CountTopLevelSelectionAsync(viewId, 3, "Files", "Only", later));
            Assert.AreEqual(new StoredSearchViewSelectionCounts(2, 0, 2),
                await reopened.CountTopLevelSelectionAsync(viewId, 3, "Files", "AllExcept", later));
        }
    }

    [TestMethod]
    public async Task DirectoryVersionsAndChildrenPageAtTheExactBoundRevision()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.GenericDirectories,
            "{\"projection\":\"generic-directories\"}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions { MinBitrate = 256 },
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(1, "Peer", @"Share\One\Track.flac", 192)], 1, false), "Live");
        long firstRevision = view.Revision;
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(2, "Peer", @"Share\One\Track.flac", 900) with
            {
                Visibility = SearchResultVisibility.Locked,
            }], 2, false), "Live");
        long secondRevision = view.Revision;
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(3, "Other", @"Share\Two\Track.flac", 900)], 3, false), "Live");
        StoredSearchViewDirectoryPage current = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, null, 10, initialFileLimit: 1))!;
        Assert.AreEqual(2, current.Items.Count);
        var currentPreviews = current.DirectoryFilePreviews;
        Assert.AreEqual(2, currentPreviews.Count);
        foreach (StoredSearchViewDirectory folder in current.Items)
            Assert.AreEqual(1, currentPreviews[folder.Ref].Items.Count);

        StoredSearchViewDirectoryPage first = (await store.GetDirectoriesAsync(
            view.Id, firstRevision, null, 10))!;
        Assert.AreEqual(1, first.Items.Count);
        Assert.AreEqual(1L, first.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(0L, first.Items[0].LockedMatchingFileCount);

        StoredSearchViewDirectoryPage second = (await store.GetDirectoriesAsync(
            view.Id, secondRevision, null, 10, initialFileLimit: 1))!;
        Assert.AreEqual(1, second.Items.Count);
        Assert.AreEqual(1L, second.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(1L, second.Items[0].LockedMatchingFileCount);
        Assert.AreEqual(first.Items[0].Ref, second.Items[0].Ref,
            "A directory keeps one stable opaque ref across versions.");
        Assert.AreEqual(new StoredSearchViewSelectionCounts(1, 0, 1),
            await store.CountTopLevelSelectionAsync(view.Id, firstRevision,
                "GenericDirectories", "Only", new HashSet<string>([first.Items[0].Ref])));
        Assert.AreEqual(new StoredSearchViewSelectionCounts(1, 1, 1),
            await store.CountTopLevelSelectionAsync(view.Id, secondRevision,
                "GenericDirectories", "Only", new HashSet<string>([second.Items[0].Ref])));

        StoredSearchViewDirectoryFilePage childOne = (await store.GetDirectoryFilesAsync(
            view.Id, second.Items[0].Ref, secondRevision, null, 1))!;
        var firstPreview = second.DirectoryFilePreviews[second.Items[0].Ref];
        Assert.AreEqual(childOne.Items.Single().Ref, firstPreview.Items.Single().Ref);
        Assert.AreEqual(childOne.NextPosition, firstPreview.NextPosition);
        Assert.AreEqual(second.Items[0].Ref, childOne.DirectorySummary.Ref);
        Assert.AreEqual(second.Items[0].PublicMatchingFileCount, childOne.DirectorySummary.PublicMatchingFileCount);
        Assert.AreEqual(second.Items[0].LockedMatchingFileCount, childOne.DirectorySummary.LockedMatchingFileCount);
        Assert.AreEqual(second.Items[0].BestChild.Ref, childOne.DirectorySummary.BestChild.Ref);
        Assert.AreEqual(1, childOne.Items.Count);
        Assert.IsNotNull(childOne.NextPosition);
        StoredSearchViewDirectoryFilePage childTwo = (await store.GetDirectoryFilesAsync(
            view.Id, second.Items[0].Ref, secondRevision, childOne.NextPosition, 1))!;
        Assert.AreEqual(childOne.DirectorySummary.Ref, childTwo.DirectorySummary.Ref);
        Assert.AreEqual(childOne.DirectorySummary.BestChild.Ref, childTwo.DirectorySummary.BestChild.Ref);
        CollectionAssert.AreEquivalent(
            new[] { "Track.flac", "Track.flac" },
            childOne.Items.Concat(childTwo.Items)
                .Select(item => item.RelativePath)
                .ToArray());
        var childRefs = childOne.Items.Concat(childTwo.Items).Select(item => item.Ref).ToArray();
        CollectionAssert.AreEqual(childRefs.OrderBy(value => value, StringComparer.Ordinal).ToArray(), childRefs,
            "Equal relative paths retain public-ref ordering, independently of admission IDs.");
        var childInfo = await store.ReadDirectoryFileRefsAsync(view.Id, secondRevision,
            [.. childRefs, childRefs[0], "missing-ref"]);
        Assert.AreEqual(2, childInfo.Count);
        Assert.AreEqual(1, childInfo.Count(item => item.Visibility == SearchResultVisibility.Public));
        Assert.AreEqual(1, childInfo.Count(item => item.Visibility == SearchResultVisibility.Locked));

        var selectedChild = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            secondRevision,
            SearchViewProjectionKind.GenericDirectories.ToString(),
            "Only",
            new HashSet<string>([childOne.Items[0].Ref], StringComparer.Ordinal)))
        {
            selectedChild.Add(item);
        }
        Assert.AreEqual(1, selectedChild.Count);
        Assert.AreEqual("DirectoryFile", selectedChild[0].Kind);
        Assert.AreEqual(second.Items[0].Ref, selectedChild[0].ParentRef);
        Assert.AreEqual(childOne.Items[0].Ref, selectedChild[0].File!.Ref);

        StoredSearchViewDirectoryPage pageOne = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, null, 1))!;
        StoredSearchViewDirectoryPage pageTwo = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, pageOne.NextPosition, 1))!;
        Assert.AreEqual(2, pageOne.Items.Concat(pageTwo.Items)
            .Select(item => item.Ref).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(new StoredSearchViewSelectionCounts(1, 0, 1),
            await store.CountTopLevelSelectionAsync(view.Id, view.Revision,
                "GenericDirectories", "AllExcept", new HashSet<string>([second.Items[0].Ref])));

        // Rebuild the recorded ranks from an existing database, including the
        // pinned cursor and a folder whose best child changed between revisions.
        async Task<string> ReadPagesAsync() => JsonSerializer.Serialize(new
        {
            First = await store.GetDirectoriesAsync(view.Id, firstRevision, null, 10, 1),
            Second = await store.GetDirectoriesAsync(view.Id, secondRevision, null, 10, 1),
            Current = await store.GetDirectoriesAsync(view.Id, view.Revision, null, 1, 1),
            Continued = await store.GetDirectoriesAsync(view.Id, view.Revision, pageOne.NextPosition, 1, 1),
        });
        string beforeMigration = await ReadPagesAsync();
        await directory.RestartWriterAsync();
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(
            new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync("20261028190000_CompactSearchConditionFacts");
        await context.Database.MigrateAsync();
        Assert.AreEqual(beforeMigration, await ReadPagesAsync());

    }

    [TestMethod]
    public async Task GenericDirectoryMergeKeepsOldRevisionSelectionExact()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(), SearchViewProjectionKind.GenericDirectories, "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(1, "Peer", @"A\B\Track one.flac", 900),
             Input(2, "Peer", @"A\C\Track two.flac", 900)], 1, false), "Live");
        long oldRevision = view.Revision;
        StoredSearchViewDirectoryPage oldPage = (await store.GetDirectoriesAsync(
            view.Id, oldRevision, null, 10))!;
        Assert.AreEqual(2, oldPage.Items.Count);
        string oldBRef = oldPage.Items.Single(item => item.FolderPath == @"A\B").Ref;

        var retrieved = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"A\B"),
            [
                new PeerFileTarget(new PeerFileIdentity("Peer", @"A\B\Track one.flac"), 1000, ".flac"),
                new PeerFileTarget(new PeerFileIdentity("Peer", @"A\B\Cover.jpg"), 2000, ".jpg"),
            ],
            isComplete: true);
        var sorter = new IncrementalResultSorter(
            new SongQuery { Title = "Track" },
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(),
            retainProjectedRows: false);
        ProjectedFileCandidate cover = sorter.AddRangeAndGetProjected(
            [Input(5, "Peer", @"A\B\Cover.jpg", 0) with { Size = 2000 }]).Single();
        await store.PublishRetrievedDirectoryAsync(
            view.Id, oldBRef, retrieved, [cover],
            Input(1, "Peer", @"A\B\Track one.flac", 900));

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(3, "Peer", @"A\B\D\Track three.flac", 900),
             Input(4, "Peer", @"A\Track root.flac", 900)], 2, false), "Live");
        StoredSearchViewDirectoryPage current = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, null, 10))!;
        Assert.AreEqual(1, current.Items.Count);
        Assert.AreEqual(@"A", current.Items[0].FolderPath);
        Assert.AreEqual(4L, current.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(5L, current.Items[0].RetrievedFileCount);
        Assert.AreEqual(6000L, current.Items[0].RetrievedBytes);
        Assert.IsFalse(current.Items[0].IsFullyRetrieved);
        var children = new List<StoredSearchViewDirectoryFile>();
        SearchViewDirectoryFilePosition? cursor = null;
        do
        {
            StoredSearchViewDirectoryFilePage page = (await store.GetDirectoryFilesAsync(
                view.Id, current.Items[0].Ref, view.Revision, cursor, 2))!;
            children.AddRange(page.Items);
            cursor = page.NextPosition;
        } while (cursor != null);
        CollectionAssert.AreEquivalent(
            new[]
            {
                @"Track root.flac",
                @"B\Track one.flac",
                @"B\D\Track three.flac",
                @"B\Cover.jpg",
                @"C\Track two.flac",
            },
            children.Select(item => item.RelativePath).ToArray());
        Assert.AreEqual(new StoredSearchViewSelectionCounts(5, 0, 1),
            await store.CountTopLevelSelectionAsync(view.Id, view.Revision,
                "GenericDirectories", "Only", new HashSet<string>([current.Items[0].Ref])));

        StoredSearchViewDirectoryPage oldAgain = (await store.GetDirectoriesAsync(
            view.Id, oldRevision, null, 10))!;
        Assert.AreEqual(2, oldAgain.Items.Count);
        Assert.AreEqual(new StoredSearchViewSelectionCounts(1, 0, 1),
            await store.CountTopLevelSelectionAsync(view.Id, oldRevision,
                "GenericDirectories", "Only", new HashSet<string>([oldBRef])));
        StoredSearchViewDirectoryFilePage oldChildren = (await store.GetDirectoryFilesAsync(
            view.Id, oldBRef, oldRevision, null, 10))!;
        CollectionAssert.AreEqual(
            new[] { "Track one.flac" },
            oldChildren.Items.Select(item => item.RelativePath).ToArray());
        var fileRefs = children.Select(item => item.Ref).ToArray();
        var oldMemberships = await store.ReadDirectoryFileRefsAsync(view.Id, oldRevision, fileRefs);
        Assert.AreEqual(2, oldMemberships.Count);
        CollectionAssert.AreEquivalent(oldPage.Items.Select(item => item.Ref).ToArray(),
            oldMemberships.Select(item => item.DirectoryRef).ToArray());
        var currentMemberships = await store.ReadDirectoryFileRefsAsync(view.Id, view.Revision, fileRefs);
        Assert.AreEqual(5, currentMemberships.Count);
        Assert.IsTrue(currentMemberships.All(item => item.DirectoryRef == current.Items[0].Ref));
        var oldCommit = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id, oldRevision, "GenericDirectories", "Only",
            new HashSet<string>([oldBRef])))
            oldCommit.Add(item);
        Assert.AreEqual(1, oldCommit.Count);
        Assert.AreEqual(@"A\B", oldCommit[0].Directory!.FolderPath);
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AggregateAlbums)]
    public async Task AlbumParentAbsorbsEarlierChildrenWithoutRepublishingFileFacts(SearchViewProjectionKind kind)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var view = await store.CreateAsync(Guid.NewGuid(), kind, "{}");
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(kind,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }), new SearchSettings
        {
            NecessaryCond = new(), PreferredCond = new(), NecessaryFolderCond = new(), MinSharesAggregate = 1,
        });
        var first = Input(1, "Peer", @"ELO\Time\Bonus\01. Twilight.flac", 900);
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [first, first with { Sequence = 2, Visibility = SearchResultVisibility.Locked }], 1, false), "Live");
        long childRevision = view.Revision;
        var child = (await store.GetDirectoriesAsync(view.Id, childRevision, null, 10))!.Items.Single();
        var earlier = (await store.GetDirectoryFilesAsync(view.Id, child.Ref, childRevision, null, 10))!;
        Assert.AreEqual(2, earlier.Items.Count);

        var change = kernel.Apply([Input(3, "Peer", @"ELO\Time\02. Target.flac", 900)], 2, true);
        Assert.AreEqual(1, change.ChangedFiles.Count, "An absorbed child supplies links, not duplicate file facts.");
        view = await store.PublishSeededAsync(directory.Writes, view.Id, change, "Complete");
        var parent = (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 10))!.Items.Single();
        Assert.AreEqual(@"ELO\Time", parent.FolderPath);
        var current = (await store.GetDirectoryFilesAsync(view.Id, parent.Ref, view.Revision, null, 10))!;
        Assert.AreEqual(3, current.Items.Count);
        Assert.AreEqual(3, (await store.GetFilesAsync(view.Id, view.Revision, null, 500))!.Items.Count);
        foreach (var file in earlier.Items)
            Assert.AreEqual(file.File, current.Items.Single(item => item.Ref == file.Ref).File);
        Assert.IsNull(await store.GetDirectoryFilesAsync(view.Id, child.Ref, view.Revision, null, 10));

        if (kind == SearchViewProjectionKind.AlbumDirectories)
        {
            var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
            await using var context = await factory.CreateDbContextAsync();
            await context.Database.GetService<IMigrator>().MigrateAsync("20261030140000_RemoveUnpublishedTrackCandidates");
            await context.Database.MigrateAsync();
        }
        await directory.RestartWriterAsync();
        await using var reopened = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await reopened.InitializeAsync();
        CollectionAssert.AreEquivalent(earlier.Items.ToArray(),
            (await reopened.GetDirectoryFilesAsync(view.Id, child.Ref, childRevision, null, 10))!.Items.ToArray());
        CollectionAssert.AreEquivalent(current.Items.ToArray(),
            (await reopened.GetDirectoryFilesAsync(view.Id, parent.Ref, view.Revision, null, 10))!.Items.ToArray());
    }

    [TestMethod]
    public async Task RetrievedDirectoryPublishesTotalsAndChildrenWithoutChangingIssuingRevision()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-06T00:00:00Z"));
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes, clock);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.GenericDirectories,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);
        view = await store.PublishSeededAsync(directory.Writes,
            view.Id,
            kernel.Apply(
                [Input(1, "Peer", @"Share\Album\Track.mp3", 192)],
                1,
                false),
            "Live");
        long issuingRevision = view.Revision;
        StoredSearchViewDirectory issued = (await store.GetDirectoriesAsync(
            view.Id,
            issuingRevision,
            null,
            10))!.Items.Single();

        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Share\Album"),
            [
                new PeerFileTarget(
                    new PeerFileIdentity("Peer", @"Share\Album\Track.mp3"),
                    1000,
                    ".mp3",
                    bitRate: 192),
                new PeerFileTarget(
                    new PeerFileIdentity("Peer", @"Share\Album\Cover.jpg"),
                    2000,
                    ".jpg"),
            ],
            isComplete: true);
        var sorter = new IncrementalResultSorter(
            new SongQuery { Title = "Track" },
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(),
            retainProjectedRows: false);
        ProjectedFileCandidate[] projected = sorter.AddRangeAndGetProjected(
            [
                Input(2, "Peer", @"Share\Album\Track.mp3", 192),
                Input(3, "Peer", @"Share\Album\Cover.jpg", 0) with { Size = 2000 },
            ]).ToArray();
        SearchProjectionInput peerObservation = Input(1, "Peer", @"Share\Album\Track.mp3", 192);

        var mismatched = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Share\album"),
            snapshot.Files,
            isComplete: true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.PublishRetrievedDirectoryAsync(
                view.Id,
                issued.Ref,
                mismatched,
                projected,
                peerObservation));
        Assert.AreEqual(issuingRevision, (await store.GetAsync(view.Id))!.Revision);

        StoredSearchViewDirectoryPublishResult filtered = await store
            .PublishRetrievedDirectoryAsync(
                view.Id,
                issued.Ref,
                snapshot,
                [projected[0]],
                peerObservation);
        Assert.AreEqual(0, filtered.NewFileCount);
        StoredSearchViewDirectory filteredDirectory = (await store.GetDirectoriesAsync(
            view.Id,
            filtered.View.Revision,
            null,
            10))!.Items.Single();
        Assert.IsTrue(filteredDirectory.IsFullyRetrieved);
        Assert.AreEqual(1L, filteredDirectory.RetrievedFileCount);
        Assert.AreEqual(1000L, filteredDirectory.RetrievedBytes);
        Assert.AreEqual(1, (await store.GetDirectoryFilesAsync(
            view.Id,
            issued.Ref,
            filtered.View.Revision,
            null,
            10))!.Items.Count);
        StoredSearchDirectoryObservation firstObservation = (await store.GetDirectoryObservationAsync(
            view.SourceJobId, snapshot.Identity))!;
        Assert.AreEqual(2, firstObservation.Snapshot.Files.Count);
        Assert.AreEqual(peerObservation.Filename, firstObservation.PeerObservation.Filename);

        StoredSearchViewDirectoryPublishResult published = await store
            .PublishRetrievedDirectoryAsync(
                view.Id,
                issued.Ref,
                snapshot,
                projected,
                peerObservation);
        Assert.AreEqual(1, published.NewFileCount);
        Assert.AreEqual(filtered.View.Revision + 1, published.View.Revision);

        StoredSearchViewDirectory current = (await store.GetDirectoriesAsync(
            view.Id,
            published.View.Revision,
            null,
            10))!.Items.Single();
        Assert.IsTrue(current.IsFullyRetrieved);
        Assert.AreEqual(2L, current.RetrievedFileCount);
        Assert.AreEqual(3000L, current.RetrievedBytes);
        await using (var reopened = new SearchViewStore(directory.DatabasePath, directory.Writes, clock))
        {
            await reopened.InitializeAsync();
            StoredSearchDirectoryObservation retained = (await reopened.GetDirectoryObservationAsync(
                view.SourceJobId, snapshot.Identity))!;
            Assert.AreNotEqual(firstObservation.Id, retained.Id);
            Assert.AreEqual(1, retained.SourceRevision);
            Assert.IsTrue(retained.Snapshot.IsComplete);
            CollectionAssert.AreEqual(
                new[] { @"Share\Album\Track.mp3", @"Share\Album\Cover.jpg" },
                retained.Snapshot.Files.Select(file => file.Filename).ToArray());
            Assert.AreEqual(2000L, retained.Snapshot.Files[1].Size);
        }
        Assert.AreEqual(2, (await store.GetDirectoryFilesAsync(
            view.Id,
            issued.Ref,
            published.View.Revision,
            null,
            10))!.Items.Count);

        StoredSearchViewDirectory old = (await store.GetDirectoriesAsync(
            view.Id,
            issuingRevision,
            null,
            10))!.Items.Single();
        Assert.IsFalse(old.IsFullyRetrieved);
        Assert.IsNull(old.RetrievedFileCount);
        Assert.AreEqual(1, (await store.GetDirectoryFilesAsync(
            view.Id,
            issued.Ref,
            issuingRevision,
            null,
            10))!.Items.Count);

        var siblingKernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);
        StoredSearchView sibling = await store.CreateAsync(
            view.SourceJobId, SearchViewProjectionKind.GenericDirectories, "{}", retention: TimeSpan.FromDays(45));
        sibling = await store.PublishSeededAsync(directory.Writes,
            sibling.Id,
            siblingKernel.Apply([peerObservation], 1, false),
            "Live");
        var pending = new List<StoredSearchDirectoryObservationTarget>();
        await foreach (StoredSearchDirectoryObservationTarget target in
            store.ReadUnappliedDirectoryObservationsAsync(sibling.Id))
            pending.Add(target);
        Assert.AreEqual(1, pending.Count);
        StoredSearchDirectoryObservation latest = (await store.GetDirectoryObservationAsync(
            view.SourceJobId, snapshot.Identity))!;
        Assert.AreEqual(latest.Id, pending[0].ObservationId);
        Assert.AreEqual(snapshot.Identity, pending[0].Identity);
        await store.PublishRetrievedDirectoryAsync(
            sibling.Id,
            pending[0].DirectoryRef,
            latest.Snapshot,
            projected.Select(file => file with { Input = file.Input with { Sequence = file.Input.Sequence + 100 } }).ToArray(),
            latest.PeerObservation,
            existingObservationId: latest.Id);
        Assert.IsFalse(await HasUnappliedObservationAsync(store, sibling.Id));
        Assert.IsFalse(await HasUnappliedObservationAsync(store, view.Id));
        sibling = await store.PublishSeededAsync(directory.Writes,
            sibling.Id,
            siblingKernel.Apply(
                [Input(2, "Peer", @"Share\Album\Later.mp3", 192)],
                2,
                false),
            "Live");
        StoredSearchViewDirectory afterNewHit = (await store.GetDirectoriesAsync(
            sibling.Id,
            sibling.Revision,
            null,
            10))!.Items.Single();
        Assert.IsFalse(afterNewHit.IsFullyRetrieved,
            "A newer source hit invalidates the complete-snapshot claim.");
        Assert.IsTrue(await store.HasNewerDirectorySourceFileAsync(
            sibling.Id, afterNewHit.Ref, sibling.Revision, latest.SourceRevision));
        Assert.AreEqual(3L, afterNewHit.RetrievedFileCount);
        Assert.AreEqual(4000L, afterNewHit.RetrievedBytes);
        Assert.AreEqual(3, (await store.GetDirectoryFilesAsync(
            sibling.Id,
            afterNewHit.Ref,
            sibling.Revision,
            null,
            10))!.Items.Count);

        clock.Advance(TimeSpan.FromDays(31));
        Assert.IsNull(await store.GetDirectoryObservationAsync(view.SourceJobId, snapshot.Identity));
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        var retainedFiles = (await store.GetDirectoryFilesAsync(sibling.Id, afterNewHit.Ref, sibling.Revision, null, 10))!.Items;
        Assert.AreEqual(3, retainedFiles.Count, "A retained View must outlive its issuing directory snapshot.");
        var retainedCover = retainedFiles.Single(file => file.File.Input.Filename.EndsWith("Cover.jpg", StringComparison.Ordinal));
        Assert.AreEqual(103L, retainedCover.File.Input.Sequence, "View paging sequence must remain separate from snapshot ordinal.");
        Assert.AreEqual(2000L, retainedCover.File.Input.Size);
        clock.Advance(TimeSpan.FromDays(20));
        Assert.AreEqual(1, await store.PruneExpiredAsync());
    }

    [TestMethod]
    public async Task DirectSelectionReadsExactDiskBackedRevisionAndStreamsLockedRowsIndependently()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        string path = directory.DatabasePath;
        Guid viewId;
        long viewRevision;
        string[] selectedRefs;
        await using (var store = new SearchViewStore(path, directory.Writes))
        {
            await store.InitializeAsync();
            StoredSearchView view = await store.CreateAsync(
                Guid.NewGuid(),
                SearchViewProjectionKind.Files,
                "{}");
            var kernel = Kernel();
            view = await store.PublishSeededAsync(directory.Writes,
                view.Id,
                kernel.Apply(
                    [
                        Input(1, "Public", @"Share\Track.mp3", 192),
                        Input(2, "Locked", @"Share\Track.flac", 900) with
                        {
                            Visibility = SearchResultVisibility.Locked,
                        },
                    ],
                    1,
                    false),
                "Live");
            StoredSearchViewFilePage page = (await store.GetFilesAsync(
                view.Id,
                view.Revision,
                null,
                10))!;
            viewId = view.Id;
            viewRevision = view.Revision;
            selectedRefs = page.Items.Select(file => file.Ref).ToArray();
        }

        await directory.RestartWriterAsync();
        await using (var reopened = new SearchViewStore(path, directory.Writes))
        {
            await reopened.InitializeAsync();
            StoredSearchView view = (await reopened.GetAsync(viewId))!;
            Assert.AreEqual(SearchViewProjectionKind.Files.ToString(), view.ProjectionKind);
            var selected = new List<StoredSearchViewCommitItem>();
            await foreach (StoredSearchViewCommitItem item in reopened.ReadCommitItemsAsync(
                viewId,
                viewRevision,
                SearchViewProjectionKind.Files.ToString(),
                "Only",
                new HashSet<string>(selectedRefs, StringComparer.Ordinal)))
            {
                selected.Add(item);
            }
            Assert.AreEqual(2, selected.Count);
            Assert.AreEqual(1, selected.Count(item =>
                item.File?.Input.Visibility == SearchResultVisibility.Locked));

            var missingRefs = new HashSet<string>(StringComparer.Ordinal) { "not-a-file-reference" };
            foreach (string itemRef in selectedRefs)
                if (itemRef != itemRef.ToUpperInvariant()) missingRefs.Add(itemRef.ToUpperInvariant());
            Assert.AreEqual(new StoredSearchViewSelectionCounts(0, 0, 0),
                await reopened.CountTopLevelSelectionAsync(viewId, viewRevision, "Files", "Only", missingRefs));
            Assert.AreEqual(new StoredSearchViewSelectionCounts(1, 1, 2),
                await reopened.CountTopLevelSelectionAsync(viewId, viewRevision, "Files", "AllExcept", missingRefs),
                "Missing exclusions and different opaque-ref spelling must not exclude existing results.");
            var missingItems = new List<StoredSearchViewCommitItem>();
            await foreach (var item in reopened.ReadCommitItemsAsync(viewId, viewRevision, "Files", "Only", missingRefs))
                missingItems.Add(item);
            Assert.AreEqual(0, missingItems.Count);
            var excluded = new HashSet<string>(missingRefs, StringComparer.Ordinal) { selectedRefs[0] };
            var allExcept = new List<StoredSearchViewCommitItem>();
            await foreach (StoredSearchViewCommitItem item in reopened.ReadCommitItemsAsync(
                viewId,
                viewRevision,
                SearchViewProjectionKind.Files.ToString(),
                "AllExcept",
                excluded))
            {
                allExcept.Add(item);
            }
            Assert.AreEqual(1, allExcept.Count);
            Assert.AreNotEqual(selectedRefs[0], allExcept[0].Ref);
            Assert.AreEqual(new StoredSearchViewSelectionCounts(
                allExcept.Count(item => item.File!.Input.Visibility == SearchResultVisibility.Public),
                allExcept.Count(item => item.File!.Input.Visibility == SearchResultVisibility.Locked), 1),
                await reopened.CountTopLevelSelectionAsync(viewId, viewRevision, "Files", "AllExcept", excluded));
        }
    }

    [TestMethod]
    public async Task AlbumDirectoryRemovalKeepsOlderRevisionAndEndsCurrentMembership()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.AlbumDirectories,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AlbumDirectories,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions { MaxTrackCount = 1 },
            },
            trackPeerIdentities: false);
        view = await store.PublishSeededAsync(directory.Writes,
            view.Id,
            kernel.Apply(
                [Input(1, "Peer", @"ELO\Time\01. Track.flac", 900)],
                1,
                false),
            "Live");
        long presentRevision = view.Revision;
        StoredSearchViewDirectory present = (await store.GetDirectoriesAsync(
            view.Id,
            presentRevision,
            null,
            10))!.Items.Single();

        view = await store.PublishSeededAsync(directory.Writes,
            view.Id,
            kernel.Apply(
                [Input(2, "Peer", @"ELO\Time\02. Track.flac", 900)],
                2,
                false),
            "Live");
        Assert.AreEqual(0, (await store.GetDirectoriesAsync(
            view.Id,
            view.Revision,
            null,
            10))!.Items.Count);
        Assert.AreEqual(1, (await store.GetDirectoriesAsync(
            view.Id,
            presentRevision,
            null,
            10))!.Items.Count);
        Assert.AreEqual(1, (await store.GetDirectoryFilesAsync(
            view.Id,
            present.Ref,
            presentRevision,
            null,
            10))!.Items.Count);
        Assert.IsNull(await store.GetDirectoryFilesAsync(
            view.Id,
            present.Ref,
            view.Revision,
            null,
            10));

        // A requested browse can finish after later arrivals remove this album.
        // Retain the useful peer snapshot without resurrecting the hidden result.
        var identity = new PeerDirectoryIdentity("Peer", @"ELO\Time");
        var snapshot = new PeerDirectorySnapshot(identity, [], isComplete: true);
        var completed = await store.PublishRetrievedDirectoryAsync(view.Id, present.Ref,
            snapshot, [], Input(1, "Peer", @"ELO\Time\01. Track.flac", 900));
        Assert.AreEqual(0, (await store.GetDirectoriesAsync(
            view.Id, completed.View.Revision, null, 10))!.Items.Count);
        Assert.AreEqual(1, (await store.GetDirectoriesAsync(
            view.Id, presentRevision, null, 10))!.Items.Count);
        Assert.IsNotNull(await store.GetDirectoryObservationAsync(view.SourceJobId, identity));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AggregateTrackVisibilityBackfillsEarlierOptionsWithoutExposingHiddenCandidates(bool matchesLaterFilename)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var view = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.AggregateTracks, "{}");
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateTracks, new SongQuery { Artist = "ELO" },
            FilterText: matchesLaterFilename ? "Special" : null), new SearchSettings
            {
                NecessaryCond = new(), PreferredCond = new(), MinSharesAggregate = 2,
            });
        var firstInput = Input(1, "Peer-A", @"Other\ELO - Track.flac", 900);
        var firstUpdate = kernel.Apply([firstInput], 1, false);
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        // Seed the older publication shape, then normalize it once at upgrade.
        var olderCandidates = Kernel().Apply([firstInput], 1, false).ChangedFiles;
        view = await store.PublishSeededAsync(directory.Writes, view.Id, firstUpdate with { ChangedFiles = olderCandidates }, "Live");
        Assert.AreEqual(1, (await store.GetFilesAsync(view.Id, view.Revision, null, 500))!.Items.Count);
        await context.Database.GetService<IMigrator>().MigrateAsync("20261030100000_CompactAggregateTrackMembership");
        await context.Database.MigrateAsync();
        long hiddenRevision = view.Revision;
        Assert.AreEqual(1L, view.Counters.PublicFileCount);
        Assert.AreEqual(0L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, view.Revision, null, 500))!.Items.Count);

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(2, "Peer-B", @"Other\ELO - Track.flac", 900) with { Visibility = SearchResultVisibility.Locked }],
            2, false), "Live");
        Assert.AreEqual(matchesLaterFilename ? 0L : 1L, view.Counters.TopLevelItemCount);
        Assert.AreEqual(matchesLaterFilename ? 0 : 2,
            (await store.GetFilesAsync(view.Id, view.Revision, null, 500))!.Items.Count);
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(3, "Peer-C", @"Special\ELO - Track.flac", 900)], 3, true), "Complete");
        var group = (await store.GetAggregateTracksAsync(view.Id, view.Revision, null, 100))!.Items.Single();
        Assert.AreEqual(3, group.ShareCount);
        Assert.AreEqual(2L, group.SelectableOptionCount);
        Assert.AreEqual(3L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, hiddenRevision, null, 500))!.Items.Count);
        Assert.IsNull(await store.GetAggregateTrackOptionsAsync(view.Id, group.Ref, hiddenRevision, null, 500));
        var options = new List<StoredSearchViewFile>();
        SearchViewFilePosition? after = null;
        do
        {
            var page = (await store.GetAggregateTrackOptionsAsync(view.Id, group.Ref, view.Revision, after, 1))!;
            options.AddRange(page.Items);
            after = page.NextPosition;
        } while (after != null);
        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, options.Select(file => file.Input.Sequence).ToArray());
        Assert.AreEqual(1, options.Count(file => file.Input.Visibility == SearchResultVisibility.Locked));
        var selected = new List<StoredSearchViewCommitItem>();
        await foreach (var item in store.ReadCommitItemsAsync(view.Id, view.Revision, "AggregateTracks", "Only",
            new HashSet<string>([options.Single(file => file.Input.Sequence == 1).Ref]))) selected.Add(item);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual("Peer-A", selected[0].File!.Input.Username);
        await directory.RestartWriterAsync();
        await using var reopened = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await reopened.InitializeAsync();
        var retained = (await reopened.GetAggregateTrackOptionsAsync(view.Id, group.Ref, view.Revision, null, 500))!.Items;
        CollectionAssert.AreEquivalent(options.ToArray(), retained.ToArray());
    }

    [TestMethod]
    public async Task AggregateTrackSummaryAndAlternativesAreRevisionBoundAndPaged()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.AggregateTracks,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateTracks,
                new SongQuery { Artist = "ELO" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                MinSharesAggregate = 1,
                Relax = true,
            },
            trackPeerIdentities: false);
        view = await store.PublishSeededAsync(directory.Writes,
            view.Id,
            kernel.Apply(
                [Input(1, "Peer-A", @"ELO - Track.flac", 900)],
                1,
                false),
            "Live");
        long firstRevision = view.Revision;
        StoredSearchViewAggregateTrackGroup first = (await store.GetAggregateTracksAsync(
            view.Id,
            firstRevision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(1, first.ShareCount);

        view = await store.PublishSeededAsync(directory.Writes,
            view.Id,
            kernel.Apply(
                [Input(2, "Peer-B", @"ELO - Track.mp3", 192)],
                2,
                false),
            "Live");
        StoredSearchViewAggregateTrackGroup current = (await store.GetAggregateTracksAsync(
            view.Id,
            view.Revision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(first.Ref, current.Ref);
        Assert.AreEqual(2, current.ShareCount);
        Assert.AreEqual(new StoredSearchViewSelectionCounts(1, 0, 1),
            await store.CountTopLevelSelectionAsync(view.Id, view.Revision,
                "AggregateTracks", "AllExcept", new HashSet<string>()));
        Assert.AreEqual(1, (await store.GetAggregateTrackOptionsAsync(
            view.Id,
            current.Ref,
            firstRevision,
            null,
            10))!.Items.Count);
        var heldTrack = new Dictionary<string, string> { [current.Ref] = first.Representative.Ref };
        var resolvedTrack = await store.ResolveChosenOptionsAsync(view.Id, view.Revision, heldTrack);
        Assert.AreEqual(first.Representative.Ref, resolvedTrack!.Tracks[current.Ref].Ref);
        Assert.AreEqual(0, resolvedTrack.Albums.Count);
        Assert.AreEqual(0, (await store.ResolveChosenOptionsAsync(view.Id, view.Revision,
            new Dictionary<string, string> { [current.Ref] = "missing-option" }))!.Tracks.Count);
        StoredSearchViewAggregateTrackOptionPage firstPage = (await store
            .GetAggregateTrackOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                null,
                1))!;
        Assert.IsNotNull(firstPage.NextPosition);
        StoredSearchViewAggregateTrackOptionPage secondPage = (await store
            .GetAggregateTrackOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                firstPage.NextPosition,
                1))!;
        Assert.AreEqual(2, firstPage.Items.Concat(secondPage.Items).Count());

        string optionRef = firstPage.Items[0].Ref;
        var selectedOption = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            view.Revision,
            SearchViewProjectionKind.AggregateTracks.ToString(),
            "Only",
            new HashSet<string>([optionRef], StringComparer.Ordinal)))
        {
            selectedOption.Add(item);
        }
        Assert.AreEqual(1, selectedOption.Count);
        Assert.AreEqual("AggregateTrackFile", selectedOption[0].Kind);
        CollectionAssert.Contains(selectedOption[0].ContainerRefs!.ToArray(), current.Ref);

        // One publication mixes new groups and another option for an existing
        // group. Counts, representatives and membership must remain independent.
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply([
            Input(3, "Peer-C", @"ELO - Other Song.flac", 320),
            Input(4, "Peer-D", @"ELO - Last Song.flac", 256),
            Input(5, "Peer-E", @"ELO - Track.flac", 512),
        ], 5, true), "Complete");
        var roots = (await store.GetAggregateTracksAsync(view.Id, view.Revision, null, 2))!;
        Assert.IsNotNull(roots.NextPosition);
        var tail = (await store.GetAggregateTracksAsync(view.Id, view.Revision, roots.NextPosition, 2))!;
        var all = roots.Items.Concat(tail.Items).ToArray();
        Assert.AreEqual(3, all.Length);
        Assert.AreEqual(3, all.Select(group => group.Ref).Distinct().Count());
        Assert.AreEqual(3, all.Single(group => group.Ref == first.Ref).ShareCount);
        foreach (var group in all)
        {
            var options = (await store.GetAggregateTrackOptionsAsync(
                view.Id, group.Ref, view.Revision, null, 10))!.Items;
            Assert.AreEqual(group.SelectableOptionCount, (long)options.Count);
            Assert.IsTrue(options.Any(file => file.Ref == group.Representative.Ref));
            string title = JsonSerializer.Deserialize<SongQuery>(group.QueryJson)!.Title;
            foreach (var file in options)
                Assert.AreEqual($"ELO - {title}.flac", file.Input.Filename.Replace(".mp3", ".flac"));
        }
        var historical = (await store.GetAggregateTracksAsync(view.Id, firstRevision, null, 10))!.Items;
        Assert.AreEqual(1, historical.Count);
        Assert.AreEqual(first.Ref, historical[0].Ref);
        Assert.AreEqual(first.ShareCount, historical[0].ShareCount);
        Assert.AreEqual(first.Representative.Ref, historical[0].Representative.Ref);

        async Task<string> RetainedPagesAsync() => JsonSerializer.Serialize(new
        {
            Current = await store.GetAggregateTracksAsync(view.Id, view.Revision, null, 2),
            Historical = await store.GetAggregateTracksAsync(view.Id, firstRevision, null, 10),
            Continuation = await store.GetAggregateTrackOptionsAsync(view.Id, first.Ref,
                firstPage.Revision.Revision, firstPage.NextPosition, 1),
        });
        string retained = await RetainedPagesAsync();
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(
            new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync("20261029110000_IndexAggregateAlbumPaging");
        await context.Database.MigrateAsync();
        Assert.AreEqual(retained, await RetainedPagesAsync(),
            "Rebuilding internal group keys must preserve both revisions and an already-issued option cursor.");
    }

    [TestMethod]
    public async Task AggregateAlbumVisibilityBackfillsAndRestoresHistoricalDirectorySelections()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        var view = await store.CreateAsync(Guid.NewGuid(), SearchViewProjectionKind.AggregateAlbums, "{}");
        var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
        var settings = new SearchSettings
        {
            NecessaryCond = new(), PreferredCond = new(), NecessaryFolderCond = new(), MinSharesAggregate = 2,
        };
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateAlbums, AlbumQuery: query, FilterText: "Special"), settings);
        var first = Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900);
        var hidden = kernel.Apply([first], 1, false);
        Assert.AreEqual(0, hidden.ChangedFiles.Count);
        Assert.AreEqual(0, hidden.ChangedDirectories!.Count);
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
        await using var context = await factory.CreateDbContextAsync();
        // Seed an older eligible-folder cache; upgrade expires it without losing source facts.
        var oldShape = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AlbumDirectories, AlbumQuery: query), settings).Apply([first], 1, false);
        view = await store.PublishSeededAsync(directory.Writes, view.Id, hidden with
            { ChangedFiles = oldShape.ChangedFiles, ChangedDirectories = oldShape.ChangedDirectories }, "Live");
        Assert.AreEqual(1, (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 10))!.Items.Count);
        await context.Database.GetService<IMigrator>().MigrateAsync("20261030170000_RemoveUnpublishedAlbumCandidates");
        await context.Database.MigrateAsync();
        Assert.IsNull(await store.GetAsync(view.Id), "The older cached history cannot represent group visibility.");
        Assert.AreEqual(1L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM search_observations").SingleAsync());
        view = await store.CreateAsync(view.SourceJobId, SearchViewProjectionKind.AggregateAlbums, "{}");
        view = await store.PublishSeededAsync(directory.Writes, view.Id, hidden, "Live");
        long hiddenRevision = view.Revision;
        Assert.AreEqual(0, (await store.GetDirectoriesAsync(view.Id, hiddenRevision, null, 10))!.Items.Count);
        Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, hiddenRevision, null, 500))!.Items.Count);

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(2, "Peer-B", @"Special\ELO\Time\01. Track.flac", 900)
                with { Visibility = SearchResultVisibility.Locked }], 2, false), "Live");
        long firstVisibleRevision = view.Revision;
        var firstGroup = (await store.GetAggregateAlbumsAsync(view.Id, view.Revision, null, 10))!.Items.Single();
        var initialOptions = (await store.GetAggregateAlbumOptionsAsync(view.Id, firstGroup.Ref, view.Revision, null, 10))!.Items;
        var a = initialOptions.Single(folder => folder.Username == "Peer-A");
        var b = initialOptions.Single(folder => folder.Username == "Peer-B");
        var firstFile = (await store.GetDirectoryFilesAsync(view.Id, a.Ref, view.Revision, null, 10))!.Items.Single();
        Assert.AreEqual(2L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(1L, view.Counters.ProjectedLockedFileCount);

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(3, "Peer-A", @"ELO\Time\02. Other.flac", 900)], 3, false), "Live");
        long gapRevision = view.Revision;
        Assert.AreEqual(0, (await store.GetAggregateAlbumsAsync(view.Id, gapRevision, null, 10))!.Items.Count);
        Assert.AreEqual(0, (await store.GetDirectoriesAsync(view.Id, gapRevision, null, 10))!.Items.Count);
        Assert.IsNull(await store.GetDirectoryFilesAsync(view.Id, a.Ref, gapRevision, null, 10));

        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply(
            [Input(4, "Peer-C", @"Special\ELO\Time\01. Track.flac", 900)], 4, false), "Live");
        Assert.AreEqual(1, (await store.GetDirectoryFilesAsync(view.Id, b.Ref, view.Revision, null, 10))!.Items.Count,
            "A folder restored by another peer's arrival must retain its earlier locked child.");
        var restored = kernel.Apply([Input(5, "Peer-B", @"Special\ELO\Time\02. Other.flac", 900)], 5, true);
        CollectionAssert.AreEquivalent(new long[] { 3, 5 }, restored.ChangedFiles.Select(file => file.Input.Sequence).ToArray());
        view = await store.PublishSeededAsync(directory.Writes, view.Id, restored, "Complete");
        Assert.AreEqual(4L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(1L, view.Counters.ProjectedLockedFileCount);
        Assert.AreEqual(2, (await store.GetDirectoryFilesAsync(view.Id, a.Ref, view.Revision, null, 10))!.Items.Count);
        Assert.AreEqual(2, (await store.GetDirectoryFilesAsync(view.Id, b.Ref, view.Revision, null, 10))!.Items.Count);
        Assert.AreEqual(1, (await store.GetDirectoryFilesAsync(view.Id, a.Ref, firstVisibleRevision, null, 10))!.Items.Count);
        Assert.IsNull(await store.GetDirectoryFilesAsync(view.Id, a.Ref, gapRevision, null, 10));
        Assert.AreEqual(0, (await store.GetAggregateAlbumOptionsAsync(view.Id, firstGroup.Ref, gapRevision, null, 10))!.Items.Count);
        Assert.AreEqual(0, (await store.ReadDirectoryFileRefsAsync(view.Id, gapRevision, [firstFile.Ref])).Count);
        Assert.AreEqual(new StoredSearchViewSelectionCounts(0, 0, 0),
            await store.CountObservedDirectoryFilesAsync(view.Id, gapRevision, a.Ref));
        var hiddenChildren = new List<StoredSearchViewDirectoryFile>();
        await foreach (var child in store.ReadPublicDirectoryFilesAsync(view.Id, a.Ref, gapRevision)) hiddenChildren.Add(child);
        Assert.AreEqual(0, hiddenChildren.Count);
        var hiddenMemberships = new List<StoredSearchViewDirectoryFileMembership>();
        await foreach (var child in store.ReadDirectoryFileMembershipsAsync(view.Id, a.Ref, gapRevision)) hiddenMemberships.Add(child);
        Assert.AreEqual(0, hiddenMemberships.Count);
        var hiddenSelection = new List<StoredSearchViewCommitItem>();
        await foreach (var item in store.ReadCommitItemsAsync(view.Id, gapRevision,
            "AggregateAlbums", "Only", new HashSet<string>([firstFile.Ref]))) hiddenSelection.Add(item);
        Assert.AreEqual(0, hiddenSelection.Count);
        var selected = new List<StoredSearchViewCommitItem>();
        await foreach (var item in store.ReadCommitItemsAsync(view.Id, view.Revision,
            "AggregateAlbums", "Only", new HashSet<string>([firstFile.Ref]))) selected.Add(item);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(first, selected[0].File!.Input);
        await directory.RestartWriterAsync();
        await using var reopened = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await reopened.InitializeAsync();
        Assert.AreEqual(1, (await reopened.GetDirectoryFilesAsync(view.Id, a.Ref, firstVisibleRevision, null, 10))!.Items.Count);
        Assert.AreEqual(2, (await reopened.GetDirectoryFilesAsync(view.Id, a.Ref, view.Revision, null, 10))!.Items.Count);
        Assert.IsNull(await reopened.GetDirectoryFilesAsync(view.Id, a.Ref, gapRevision, null, 10));
    }

    [TestMethod]
    public async Task AggregateAlbumSummaryAndDirectoryAlternativesAreRevisionBound()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.AggregateAlbums,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateAlbums,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions(),
                MinSharesAggregate = 1,
            },
            trackPeerIdentities: false);
        view = await store.PublishSeededAsync(directory.Writes,
            view.Id,
            kernel.Apply(
                [Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900)],
                1,
                false),
            "Live");
        long firstRevision = view.Revision;
        StoredSearchViewAggregateAlbumGroup first = (await store.GetAggregateAlbumsAsync(
            view.Id,
            firstRevision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(1, first.ShareCount);
        Assert.AreEqual(1, (await store.GetAggregateAlbumOptionsAsync(
            view.Id,
            first.Ref,
            firstRevision,
            null,
            10))!.Items.Count);

        SearchViewKernelUpdate secondUpdate = kernel.Apply(
            [Input(2, "Peer-B", @"ELO\Time\01. Track.flac", 900)],
            2,
            false);
        view = await store.PublishSeededAsync(directory.Writes, view.Id, secondUpdate, "Live");
        StoredSearchViewAggregateAlbumGroup current = (await store.GetAggregateAlbumsAsync(
            view.Id,
            view.Revision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(first.Ref, current.Ref);
        Assert.AreEqual(2, current.ShareCount);
        StoredSearchViewAggregateAlbumOptionPage pageOne = (await store
            .GetAggregateAlbumOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                null,
                1, initialFileLimit: 1))!;
        StoredSearchViewAggregateAlbumOptionPage pageTwo = (await store
            .GetAggregateAlbumOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                pageOne.NextPosition,
                1, initialFileLimit: 1))!;
        Assert.IsNotNull(pageOne.NextPosition);
        Assert.IsNull(pageTwo.NextPosition);
        CollectionAssert.AreEquivalent(new[] { "Peer-A", "Peer-B" },
            pageOne.Items.Concat(pageTwo.Items).Select(item => item.Username).ToArray());
        Assert.AreEqual(1, pageOne.DirectoryFilePreviews.Single().Value.Items.Count);
        Assert.AreEqual(1, pageTwo.DirectoryFilePreviews.Single().Value.Items.Count);
        Assert.AreEqual(1, (await store.GetAggregateAlbumOptionsAsync(
            view.Id,
            current.Ref,
            firstRevision,
            null,
            10))!.Items.Count);
        var heldAlbum = new Dictionary<string, string> { [current.Ref] = first.Representative.Ref };
        var resolvedAlbum = await store.ResolveChosenOptionsAsync(view.Id, view.Revision, heldAlbum);
        Assert.AreEqual(first.Representative.Ref, resolvedAlbum!.Albums[current.Ref].Ref);
        Assert.AreEqual(0, resolvedAlbum.Tracks.Count);
        Assert.AreEqual(0, (await store.ResolveChosenOptionsAsync(view.Id, view.Revision,
            new Dictionary<string, string> { [current.Ref] = "missing-option" }))!.Albums.Count);

        StoredSearchViewDirectory selectedDirectory = pageOne.Items[0];
        StoredSearchViewDirectoryFile selectedFile = (await store.GetDirectoryFilesAsync(
            view.Id,
            selectedDirectory.Ref,
            view.Revision,
            null,
            10))!.Items.Single();
        var selectedNested = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            view.Revision,
            SearchViewProjectionKind.AggregateAlbums.ToString(),
            "Only",
            new HashSet<string>(
                [selectedDirectory.Ref, selectedFile.Ref],
                StringComparer.Ordinal)))
        {
            selectedNested.Add(item);
        }
        Assert.AreEqual(2, selectedNested.Count);
        StoredSearchViewCommitItem directoryItem = selectedNested.Single(item =>
            item.Kind == "AggregateAlbumDirectory");
        CollectionAssert.Contains(directoryItem.ContainerRefs!.ToArray(), current.Ref);
        StoredSearchViewCommitItem fileItem = selectedNested.Single(item =>
            item.Kind == "AggregateAlbumFile");
        CollectionAssert.Contains(fileItem.ContainerRefs!.ToArray(), current.Ref);
        CollectionAssert.Contains(fileItem.ContainerRefs!.ToArray(), selectedDirectory.Ref);

        // A later regrouping can remove one alternative without changing the
        // other. Earlier revisions must keep their original membership.
        SearchViewProjectedAggregateAlbumGroup group =
            secondUpdate.ChangedAggregateAlbumGroups!.Single();
        AlbumFolder retained = group.Options.Single(option => option.Username == "Peer-A");
        long secondRevision = view.Revision;
        view = await store.PublishSeededAsync(directory.Writes, view.Id, secondUpdate with
        {
            SourceRevision = 3,
            ConsumedSequence = 3,
            ChangedFiles = [],
            ChangedDirectories = [],
            ChangedAggregateAlbumGroups =
            [
                group with
                {
                    ShareCount = 1,
                    SelectableOptionCount = 1,
                    Representative = retained,
                    Options = [retained],
                },
            ],
        }, "Live");
        Assert.AreEqual(1, (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, view.Revision, null, 10))!.Items.Count);
        Assert.AreEqual(2, (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, secondRevision, null, 10))!.Items.Count);
        var historicalContinuation = (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, secondRevision, pageOne.NextPosition, 1, initialFileLimit: 1))!;
        Assert.AreEqual(pageTwo.Items.Single().Ref, historicalContinuation.Items.Single().Ref);
        Assert.AreEqual(pageTwo.DirectoryFilePreviews.Single().Value.Items.Single().File.Ref,
            historicalContinuation.DirectoryFilePreviews.Single().Value.Items.Single().File.Ref);
        Assert.AreEqual(1, (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, firstRevision, null, 10))!.Items.Count);

        StoredSearchViewDirectory removed = pageOne.Items.Concat(pageTwo.Items)
            .Single(option => option.Username == "Peer-B");
        StoredSearchViewDirectoryFile removedChild = (await store.GetDirectoryFilesAsync(
            view.Id, removed.Ref, secondRevision, null, 1))!.Items.Single();
        var removedRefs = new HashSet<string>([removed.Ref, removedChild.Ref], StringComparer.Ordinal);
        async Task<StoredSearchViewCommitItem[]> ReadRemovedSelection(long revision)
        {
            var items = new List<StoredSearchViewCommitItem>();
            await foreach (var item in store.ReadCommitItemsAsync(view.Id, revision,
                SearchViewProjectionKind.AggregateAlbums.ToString(), "Only", removedRefs))
                items.Add(item);
            return items.ToArray();
        }
        Assert.AreEqual(0, (await ReadRemovedSelection(firstRevision)).Length);
        CollectionAssert.AreEquivalent(new[] { removed.Ref, removedChild.Ref },
            (await ReadRemovedSelection(secondRevision)).Select(item => item.Ref).ToArray());
        Assert.AreEqual(0, (await ReadRemovedSelection(view.Revision)).Length);

        // A later update can restore an existing alternative without republishing
        // its directory or files. The removed revision must stay removed.
        long removedRevision = view.Revision;
        view = await store.PublishSeededAsync(directory.Writes, view.Id, secondUpdate with
        {
            SourceRevision = 4,
            ConsumedSequence = 4,
            ChangedFiles = [],
            ChangedDirectories = [],
        }, "Live");
        Assert.AreEqual(2, (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, view.Revision, null, 10))!.Items.Count);
        CollectionAssert.AreEquivalent(new[] { removed.Ref, removedChild.Ref },
            (await ReadRemovedSelection(view.Revision)).Select(item => item.Ref).ToArray());
        Assert.AreEqual(0, (await ReadRemovedSelection(removedRevision)).Length);

        // A publication can add one group, then remove another while changing
        // the survivor's options. Batched comparison must stay group-scoped.
        var other = group with
        {
            StableIdentity = new(removed.Username, removed.FolderPath),
            Index = group.Index + 1,
            ShareCount = 1,
            SelectableOptionCount = 1,
            Representative = group.Options.Single(option => option.Username == "Peer-B"),
            Options = group.Options.Where(option => option.Username == "Peer-B").ToArray(),
        };
        async Task PublishGroups(int sourceRevision,
            SearchViewProjectedAggregateAlbumGroup[] changed, PeerDirectoryIdentity[] removedGroups, int total)
        {
            view = await store.PublishSeededAsync(directory.Writes, view.Id, secondUpdate with
            {
                Counters = secondUpdate.Counters with { TopLevelItemCount = total },
                SourceRevision = sourceRevision,
                ConsumedSequence = sourceRevision,
                ChangedFiles = [],
                ChangedDirectories = [],
                ChangedAggregateAlbumGroups = changed,
                RemovedAggregateAlbumGroups = removedGroups,
            }, "Live");
        }
        await PublishGroups(5, [group with
        {
            Representative = retained, Options = [retained], ShareCount = 1, SelectableOptionCount = 1,
        }, other], [], 2);
        long bothRevision = view.Revision;
        var otherRef = (await store.GetAggregateAlbumsAsync(view.Id, bothRevision, null, 10))!
            .Items.Single(item => item.Ref != current.Ref).Ref;
        await PublishGroups(6, [other with { Representative = retained, Options = [retained] }],
            [group.StableIdentity], 1);
        long groupRemovedRevision = view.Revision;
        Assert.AreEqual(otherRef, (await store.GetAggregateAlbumsAsync(
            view.Id, view.Revision, null, 10))!.Items.Single().Ref);
        Assert.AreEqual("Peer-A", (await store.GetAggregateAlbumOptionsAsync(
            view.Id, otherRef, view.Revision, null, 10))!.Items.Single().Username);
        Assert.AreEqual("Peer-B", (await store.GetAggregateAlbumOptionsAsync(
            view.Id, otherRef, bothRevision, null, 10))!.Items.Single().Username);
        Assert.AreEqual(0, (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, groupRemovedRevision, null, 10))!.Items.Count);
        await PublishGroups(7, [other with { StableIdentity = group.StableIdentity, Index = group.Index }], [], 2);
        Assert.AreEqual("Peer-B", (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, view.Revision, null, 10))!.Items.Single().Username);
        Assert.AreEqual(0, (await store.GetAggregateAlbumOptionsAsync(
            view.Id, current.Ref, groupRemovedRevision, null, 10))!.Items.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LargeDirectoryPagesEveryChildAndResolvesExplicitChildSelectionWithoutACollectionLimit(bool bare)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.GenericDirectories,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);
        const int pageSize = 500;
        const int childCount = pageSize + 5;
        SearchProjectionInput[] inputs = Enumerable.Range(1, childCount)
            .Select(index => Input(
                index,
                "Peer",
                bare ? $"Track {index:D3}.flac" : $@"Share\Large\Track {index:D3}.flac",
                900))
            .ToArray();
        view = await store.PublishSeededAsync(directory.Writes,
            view.Id,
            kernel.Apply(inputs, childCount, false),
            "Live");
        StoredSearchViewDirectoryPage parentPage = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, null, 1, initialFileLimit: 24))!;
        StoredSearchViewDirectory parent = parentPage.Items.Single();
        var preview = parentPage.DirectoryFilePreviews[parent.Ref];
        Assert.AreEqual(24, preview.Items.Count);
        Assert.IsNotNull(preview.NextPosition);

        var children = new List<StoredSearchViewDirectoryFile>();
        SearchViewDirectoryFilePosition? position = null;
        do
        {
            StoredSearchViewDirectoryFilePage page = (await store.GetDirectoryFilesAsync(
                view.Id,
                parent.Ref,
                view.Revision,
                position,
                pageSize))!;
            Assert.IsTrue(page.Items.Count <= pageSize);
            children.AddRange(page.Items);
            position = page.NextPosition;
        }
        while (position != null);
        Assert.AreEqual(childCount, children.Count);
        Assert.AreEqual(childCount, (await store.GetDirectoryFilesAsync(
            view.Id, parent.Ref, view.Revision, null, int.MaxValue))!.Items.Count);

        var selected = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            view.Revision,
            SearchViewProjectionKind.GenericDirectories.ToString(),
            "Only",
            new HashSet<string>(children.Select(child => child.Ref), StringComparer.Ordinal)))
        {
            selected.Add(item);
        }
        Assert.AreEqual(childCount, selected.Count);
        Assert.IsTrue(selected.All(item => item.Kind == "DirectoryFile"));
    }

    [TestMethod]
    [TestCategory("Load")]
    [DataRow(SearchViewProjectionKind.Files)]
    [DataRow(SearchViewProjectionKind.GenericDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AggregateTracks)]
    [DataRow(SearchViewProjectionKind.AggregateAlbums)]
    public async Task TenThousandResultSelections_ResolveExactRootsAndChildren(
        SearchViewProjectionKind kind)
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath, directory.Writes);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(Guid.NewGuid(), kind, "{}");
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            kind, new SongQuery { Artist = "Fixture Artist" },
            new AlbumQuery { Artist = "Fixture Artist", Album = "Fixture" },
            IncludeFullResults: true), new SearchSettings
            {
                NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions(), MinSharesAggregate = 1,
            }, retainProjectedRows: false, trackPeerIdentities: false);
        var inputs = Enumerable.Range(0, 100).SelectMany(peer =>
            Enumerable.Range(0, 10).SelectMany(album => Enumerable.Range(0, 10)
                .Select(track => Input(peer * 100 + album * 10 + track + 1,
                    $"Peer-{peer:D3}",
                    $@"Music\Fixture Artist\Fixture Album-{album:D2}\{track:D2} - Fixture Track-{track:D2}.flac", 320)
                    with { Length = 200 + album * 60 + track * 5 }))).ToArray();
        var projectionTime = TimeSpan.Zero;
        var publicationTime = TimeSpan.Zero;
        // A long-lived file view can exceed SQLite's compound-query limit.
        // Keep exercising that valid workload without adding another large fixture.
        foreach (SearchProjectionInput[] batch in inputs.Chunk(kind == SearchViewProjectionKind.Files ? 17 : 200))
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            SearchViewKernelUpdate update = kernel.Apply(batch, checked((int)batch[^1].Sequence), false);
            projectionTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            view = await store.PublishSeededAsync(directory.Writes, view.Id, update, "Live");
            publicationTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
        view = await store.PublishSeededAsync(directory.Writes, view.Id, kernel.Apply([], 10_000, true), "Complete");
        Assert.AreEqual(10_000L, view.Counters.ProjectedFileCount);
        Assert.IsTrue(view.IsComplete);
        Console.WriteLine($"10k {kind}: projection={projectionTime.TotalMilliseconds:F1} ms, publication={publicationTime.TotalMilliseconds:F1} ms.");

        if (kind == SearchViewProjectionKind.Files)
        {
            var refinement = new SearchViewKernel(new FileSearchProjection(new SongQuery { Artist = "Fixture Artist" }, IncludeFullResults: true),
                new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions { MinBitrate = 512 } });
            var refined = await store.CreateAsync(view.SourceJobId, kind, "{}");
            var update = refinement.Apply(inputs, 10_000, true);
            var elapsed = Stopwatch.StartNew();
            refined = await store.PublishSeededAsync(directory.Writes, refined.Id, update, "Complete");
            Console.WriteLine($"10k existing source, refined file View publication: {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
            Assert.AreEqual(10_000L, refined.Counters.ProjectedFileCount);
            Assert.AreEqual(500, (await store.GetFilesAsync(refined.Id, refined.Revision, null, 500))!.Items.Count);
            var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(new SockseekSqliteOptions(directory.DatabasePath)));
            await using var context = await factory.CreateDbContextAsync();
            Assert.AreEqual(10_000L, await context.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS Value FROM search_observations").SingleAsync());
        }

        var targets = new List<(string Kind, string Ref, string? Parent)>();
        string rootRef;
        if (kind == SearchViewProjectionKind.Files)
        {
            rootRef = (await store.GetFilesAsync(view.Id, view.Revision, null, 1))!.Items.Single().Ref;
            targets.Add(("File", rootRef, null));
        }
        else if (kind is SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories)
        {
            var folder = (await store.GetDirectoriesAsync(view.Id, view.Revision, null, 1))!.Items.Single();
            rootRef = folder.Ref;
            var file = (await store.GetDirectoryFilesAsync(view.Id, folder.Ref, view.Revision, null, 1))!.Items.Single();
            targets.Add(("Directory", rootRef, null));
            targets.Add(("DirectoryFile", file.Ref, rootRef));
        }
        else if (kind == SearchViewProjectionKind.AggregateTracks)
        {
            var group = (await store.GetAggregateTracksAsync(view.Id, view.Revision, null, 1))!.Items.Single();
            rootRef = group.Ref;
            var file = (await store.GetAggregateTrackOptionsAsync(view.Id, group.Ref, view.Revision, null, 1))!.Items.Single();
            targets.Add(("AggregateTrack", rootRef, null));
            targets.Add(("AggregateTrackFile", file.Ref, rootRef));
        }
        else
        {
            var group = (await store.GetAggregateAlbumsAsync(view.Id, view.Revision, null, 1))!.Items.Single();
            rootRef = group.Ref;
            var folder = (await store.GetAggregateAlbumOptionsAsync(view.Id, group.Ref, view.Revision, null, 1))!.Items.Single();
            var file = (await store.GetDirectoryFilesAsync(view.Id, folder.Ref, view.Revision, null, 1))!.Items.Single();
            targets.Add(("AggregateAlbum", rootRef, null));
            targets.Add(("AggregateAlbumDirectory", folder.Ref, rootRef));
            targets.Add(("AggregateAlbumFile", file.Ref, folder.Ref));
        }
        foreach (var target in targets)
        {
            // A one-file choice must seek its identity, regardless of View size.
            // A fresh connection prevents SQLite's page cache hiding a scan;
            // kernel-cache hits still count without introducing a timing race.
            if (kind == SearchViewProjectionKind.Files)
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            long? readBefore = kind == SearchViewProjectionKind.Files && OperatingSystem.IsLinux()
                ? LogicalReadBytes() : null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var selected = new List<StoredSearchViewCommitItem>();
            await foreach (var item in store.ReadCommitItemsAsync(view.Id, view.Revision,
                kind.ToString(), "Only", new HashSet<string>([target.Ref], StringComparer.Ordinal)))
                selected.Add(item);
            elapsed.Stop();
            if (readBefore is { } before)
            {
                long readBytes = LogicalReadBytes() - before;
                Assert.IsTrue(readBytes < 256 * 1024,
                    $"Selecting one file read {readBytes:N0} bytes; it must not scan the 10k-file View.");
            }
            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual(target.Kind, selected[0].Kind);
            Assert.AreEqual(target.Ref, selected[0].Ref);
            if (target.Parent != null)
                Assert.IsTrue(selected[0].ParentRef == target.Parent
                    || selected[0].ContainerRefs?.Contains(target.Parent) == true);
            Console.WriteLine($"10k selection {kind}/{target.Kind}: {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
        }
        long remaining = 0;
        await foreach (var item in store.ReadCommitItemsAsync(view.Id, view.Revision,
            kind.ToString(), "AllExcept", new HashSet<string>([rootRef], StringComparer.Ordinal)))
        {
            Assert.AreNotEqual(rootRef, item.Ref);
            remaining++;
        }
        Assert.AreEqual(view.Counters.TopLevelItemCount - 1, remaining);

        static long LogicalReadBytes() => long.Parse(File.ReadLines("/proc/self/io")
            .Single(line => line.StartsWith("rchar:", StringComparison.Ordinal))
            .AsSpan("rchar:".Length).Trim());
    }

    private static SearchViewKernel Kernel()
        => new(
            new FileSearchProjection(
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["flac"] },
                PreferredCond = new FileConditions { MinBitrate = 256 },
            });

    private static SearchProjectionInput Input(
        long sequence,
        string username,
        string filename,
        int bitrate)
        => new(
            sequence,
            checked((int)sequence),
            username,
            1,
            filename,
            1000,
            bitrate,
            null,
            44_100,
            180,
            Path.GetExtension(filename),
            bitrate * 100,
            true,
            null,
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            0,
            SearchResultVisibility.Public);

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        private readonly SqliteDatabaseOwner owner;

        private TemporaryDirectory(string path, SqliteDatabaseOwner owner, SockseekDbContextFactory factory)
        {
            Path = path;
            this.owner = owner;
            this.factory = factory;
            DatabasePath = System.IO.Path.Combine(path, "sockseek.db");
        }

        private readonly SockseekDbContextFactory factory;
        private TestPersistenceWriter? writer;
        public PersistenceInbox Writes => (writer ??= new TestPersistenceWriter(factory)).Inbox;
        public PersistenceHealthSnapshot Health => writer!.Health.Snapshot(Writes);
        public async Task RestartWriterAsync()
        {
            if (writer != null) await writer.DisposeAsync();
            writer = null;
        }

        public string Path { get; }
        public string DatabasePath { get; }

        public static async Task<TemporaryDirectory> CreateAsync()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sockseek-search-view-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var options = new SockseekSqliteOptions(
                System.IO.Path.Combine(path, "sockseek.db"));
            global::Tests.TestDatabase.Create(options.DatabasePath);
            SqliteDatabaseOwner owner = SqliteDatabaseOwner.Acquire(options);
            var factory = new SockseekDbContextFactory(
                SockseekDbContextOptions.Create(options));
            try
            {
                await new SqliteInitializer(factory, options, owner).InitializeAsync();
                return new TemporaryDirectory(path, owner, factory);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (writer != null) await writer.DisposeAsync();
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static async Task<bool> HasUnappliedObservationAsync(
        SearchViewStore store, Guid viewId)
    {
        await foreach (StoredSearchDirectoryObservationTarget _ in
            store.ReadUnappliedDirectoryObservationsAsync(viewId))
            return true;
        return false;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
