using Sockseek.Api;
using Sockseek.Server.Planning;
using Sockseek.Persistence;
using Sockseek.Persistence.Tests;
using Sockseek.Persistence.Write;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SearchViewCacheTests
{
    [TestMethod]
    [DataRow(SearchViewProjectionKind.GenericDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumGroups)]
    public void LateLockedMemberChangesFolderRankWithoutChangingFileFactsOrOldPages(SearchViewProjectionKind kind)
    {
        var store = new SearchViewCache();
        var view = store.Create(Guid.NewGuid(), Definition(kind));
        var settings = new SearchSettings { NecessaryCond = new(), PreferredCond = new() { ExcludeLocked = true }, NecessaryFolderCond = new(), MinGroupShares = 1 };
        var definition = new SearchViewProjectionDefinition(kind, new SongQuery { Artist = "Artist", Title = "Track" },
            new AlbumQuery { Artist = "Artist", Album = "Album" });
        var kernel = new SearchViewKernel(definition, settings);
        var inputs = new[]
        {
            Input(1, "Fast", @"Artist\Album\Artist - Track.flac", 900) with { UploadSpeed = 10_000_000 },
            Input(2, "Slow", @"Artist\Album\Artist - Track.flac", 900) with { UploadSpeed = 1, HasFreeUploadSlot = false },
            Input(3, "Fast", @"Artist\Album\cover.jpg", 0) with { Visibility = SearchResultVisibility.Locked },
        };
        view = store.Publish(view.Id, kernel.Apply(inputs[..2], 2, false), "Live");
        var original = store.GetSnapshot(view.Id, view.Revision, default)!;
        Assert.AreEqual("Fast", original.GetDirectories(null, 100).Items[0].Username);
        string? group = kind == SearchViewProjectionKind.AlbumGroups ? original.GetAlbumGroups(null, 100).Items.Single().Ref : null;
        if (group != null) Assert.AreEqual("Fast", original.GetAlbumGroupOptions(group, null, 100)!.Items[0].Username);
        view = store.Publish(view.Id, kernel.Apply(inputs[2..], 3, true), "Complete");
        var current = store.GetSnapshot(view.Id, view.Revision, default)!;
        var folders = current.GetDirectories(null, 100).Items;
        CollectionAssert.AreEqual(new[] { "Slow", "Fast" }, folders.Select(folder => folder.Username).ToArray());
        Assert.AreEqual(SearchPreferenceTier.Other, folders[1].BestChild.PreferenceTier);
        var publicFile = current.GetDirectoryFiles(folders[1].Ref, null, 500)!.Items.Single(file => file.File.Input.Visibility == SearchResultVisibility.Public);
        Assert.AreEqual(SearchPreferenceTier.Preferred, publicFile.File.PreferenceTier);
        Assert.AreEqual("Fast", original.GetDirectories(null, 100).Items[0].Username);
        if (group != null)
        {
            Assert.AreEqual("Slow", current.GetAlbumGroupOptions(group, null, 100)!.Items[0].Username);
            Assert.AreEqual("Fast", original.GetAlbumGroupOptions(group, null, 100)!.Items[0].Username);
        }
        var rebuilt = new SearchViewKernel(definition, settings).Apply(inputs, 3, true);
        Assert.AreEqual(view.Counters, rebuilt.Counters);
        Assert.AreEqual(SearchPreferenceTier.Other, rebuilt.ChangedDirectories!.Single(folder => folder.Directory.Username == "Fast").BestChild.ConditionFacts.PreferenceTier);
    }

    [TestMethod]
    public void PublicationDoesNotRequireDurableSourceOrDatabase()
    {
        var store = new SearchViewCache();
        var view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files));
        var update = Kernel().Apply([Input(1, "Peer", @"Share\Track.flac", 320)], 1, false);
        var published = store.Publish(view.Id, update, "Live");
        Assert.AreEqual(1L, published.Counters.ProjectedFileCount);
        Assert.IsFalse(published.IsComplete);
        Assert.AreEqual("Peer", (store.GetSnapshot(view.Id, published.Revision, default)?.GetFiles(null, 500))!.Items.Single().Input.Username);
        var restarted = new SearchViewCache();
        Assert.IsNull(restarted.Get(view.Id), "A restart requires a fresh View from retained raw inputs.");
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.GenericDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumGroups)]
    public void DirectorySnapshotsPreserveRevisionsRetrievalsAndOptions(SearchViewProjectionKind kind)
    {
        var store = new SearchViewCache();

        store.Create(Guid.NewGuid(), Definition(kind));
        var view = store.Create(Guid.NewGuid(), Definition(kind));
        var settings = new SearchSettings { NecessaryCond = new(), PreferredCond = new(), NecessaryFolderCond = new(), MinGroupShares = 1 };
        var query = new SongQuery { Artist = "ELO", Title = "Track" };
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(kind, query,
            new AlbumQuery { Artist = "ELO", Album = "Time" }, IncludeFullResults: true), settings);
        var first = Input(1, "Péér", @"共有\ELO\Time\01. Track.flac", 320);
        view = store.Publish(view.Id, kernel.Apply([first], 1, false), "Live");
        long firstRevision = view.Revision;
        view = store.Publish(view.Id, kernel.Apply([
            Input(2, "Péér", @"共有\ELO\Time\02. Track.flac", 320),
            Input(3, "péér", @"共有\ELO\Time\01. Track.flac", 320),
        ], 2, false), "Live");
        long secondRevision = view.Revision;
        var folder = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 100))!.Items.Single(x => x.Username == "Péér");
        var sorter = new IncrementalResultSorter(query, settings,
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(), retainProjectedRows: false);
        var cover = sorter.AddRangeAndGetProjected(
            [Input(100, "Péér", @"共有\ELO\Time\Cover.jpg", 0) with { Extension = ".jpg", Size = 2000 }]).ToArray();
        var snapshot = new PeerDirectorySnapshot(new("Péér", folder.FolderPath),
            [new PeerFileTarget(new("Péér", cover.Single().Input.Filename), 2000, ".jpg")], isComplete: false);
        view = (store.PublishRetrievedDirectory(view.Id, folder.Ref, snapshot, cover, first)).View;
        long[] revisions = [firstRevision, secondRevision, view.Revision];
        string[] Capture()
        {
            var result = new List<string>();
            foreach (long revision in revisions)
            {
                var folders = (store.GetSnapshot(view.Id, revision, default)?.GetDirectories(null, 100, initialFileLimit: 24))!;
                var children = new List<SearchViewDirectoryFilePage>();
                foreach (var item in folders.Items)
                    children.Add((store.GetSnapshot(view.Id, revision, default)?.GetDirectoryFiles(item.Ref, null, 500))!);
                var albums = kind == SearchViewProjectionKind.AlbumGroups
                    ? store.GetSnapshot(view.Id, revision, default)?.GetAlbumGroups(null, 100, initialFileLimit: 24) : null;
                var options = new List<SearchViewAlbumGroupOptionPage>();
                if (albums != null)
                    foreach (var album in albums.Items)
                        options.Add((store.GetSnapshot(view.Id, revision, default)?.GetAlbumGroupOptions(album.Ref, null, 100, initialFileLimit: 24))!);
                result.Add(JsonSerializer.Serialize(new { Folders = folders, Children = children, Albums = albums, Options = options }));
            }
            result.Add(JsonSerializer.Serialize(store.GetDirectoryObservation(view.SourceJobId, snapshot.Identity)));
            return result.ToArray();
        }
        string[] before = Capture();

        CollectionAssert.AreEqual(before, Capture());

        view = store.Publish(view.Id, kernel.Apply(
            [Input(4, "Péér", @"共有\ELO\Time\03. Track.flac", 320)], 3, true), "Complete");
        Assert.IsTrue(view.IsComplete);
        Assert.IsNotNull(store.GetDirectoryObservation(view.SourceJobId, snapshot.Identity));
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(0L, 1000L)]
    public void RetrievedDirectoryTotalsPreserveUnknownAndZeroSnapshotSizes(long? snapshotSize, long? expectedBytes)
    {
        var store = new SearchViewCache();

        var view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories));
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.GenericDirectories, new SongQuery { Title = "Track" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new(), PreferredCond = new() });
        var first = Input(1, "Peer", @"Share\Album\First Track.flac", 320);
        var second = Input(2, "Peer", @"Share\Album\Second Track.flac", 320) with { Size = 2000 };
        view = store.Publish(view.Id, kernel.Apply([first, second], 1, false), "Live");
        var folder = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 100))!.Items.Single();
        var snapshot = new PeerDirectorySnapshot(new("Peer", @"Share\Album"),
            [new PeerFileTarget(new("Peer", first.Filename), 1000, ".flac"),
             new PeerFileTarget(new("Peer", second.Filename), snapshotSize, ".flac")], isComplete: true);
        view = (store.PublishRetrievedDirectory(view.Id, folder.Ref, snapshot, [], first)).View;
        var retrieved = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 100))!.Items.Single();
        Assert.AreEqual(2L, retrieved.RetrievedFileCount);
        Assert.AreEqual(expectedBytes, retrieved.RetrievedBytes);
        Assert.IsTrue(retrieved.IsFullyRetrieved);
        Assert.AreEqual(2, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(folder.Ref, null, 500))!.Items.Count);
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
                    AttributesJson = input.Attributes == null ? null : SearchResultWriter.SerializeAttributes(input.Attributes),
                    ObservedAtUtc = input.ObservedAtUtc.ToUnixTimeMilliseconds(), Visibility = input.Visibility.ToString(),
                });
        }
        await PersistRawAsync();
        var store = new SearchViewCache(clock);

        var first = store.Create(source, Definition(SearchViewProjectionKind.Files), retention: TimeSpan.FromDays(1));
        first = store.Publish(first.Id, Kernel().Apply(inputs, 2, true), "Complete");
        var stricter = new SearchViewKernel(new FileSearchProjection(new SongQuery { Title = "Track" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions { MinBitrate = 512 } });
        var second = store.Create(source, Definition(SearchViewProjectionKind.Files), retention: TimeSpan.FromDays(3));
        second = store.Publish(second.Id, stricter.Apply(inputs, 2, true), "Complete");
        var history = new Sockseek.Persistence.Read.SearchHistoryReader(factory);
        var raw = (await history.GetRawResultsAsync(source, 0, 500))!;
        Assert.AreEqual(2, raw.Items.Count);
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
        string retained = JsonSerializer.Serialize(store.GetSnapshot(second.Id, second.Revision, default)?.GetFiles(null, 500));
        Assert.AreNotEqual(first.Counters.PreferredFileCount, second.Counters.PreferredFileCount);

        var retention = new Sockseek.Persistence.Read.RetentionService(directory.Writes, new()
        {
            SearchResultAge = TimeSpan.FromDays(1), CompletedJobHistoryAge = null, UnsuccessfulJobHistoryAge = null,
            TransferHistoryAge = null,
        }, clock);
        if (!pruneRawFirst)
        {
            clock.Advance(TimeSpan.FromDays(4));
            Assert.AreEqual(2, store.PruneExpired());
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
        Assert.AreEqual(0L, await FactCountAsync());
        Assert.AreEqual(retained, JsonSerializer.Serialize(store.GetSnapshot(second.Id, second.Revision, default)?.GetFiles(null, 500)));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.AreEqual(1, store.PruneExpired());
        Assert.AreEqual(0L, await FactCountAsync());
        Assert.AreEqual(retained, JsonSerializer.Serialize(store.GetSnapshot(second.Id, second.Revision, default)?.GetFiles(null, 500)));
        clock.Advance(TimeSpan.FromDays(4));
        Assert.AreEqual(1, store.PruneExpired());
        Assert.AreEqual(0L, await FactCountAsync());
    }

    [TestMethod]
    public void ConditionExplanationsSurvivePaging()
    {

        var store = new SearchViewCache();

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
        var view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files));
        view = store.Publish(view.Id, kernel.Apply(
        [
            Input(1, "Preferred", @"Artist\Album\Track.flac", 320) with { BitDepth = 16 },
            Input(2, "Other", @"Artist\Album\Track.mp3", 128) with { Length = 200, SampleRate = 22_050 },
        ], 2, true), "Complete");
        var page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 1))!;
        var preferred = page.Items.Single();
        Assert.AreEqual(SearchPreferenceTier.Preferred, preferred.PreferenceTier);
        SearchPreferenceCondition[] expected = [SearchPreferenceCondition.Format, SearchPreferenceCondition.Length,
            SearchPreferenceCondition.Bitrate, SearchPreferenceCondition.SampleRate, SearchPreferenceCondition.BitDepth,
            SearchPreferenceCondition.Title, SearchPreferenceCondition.Artist, SearchPreferenceCondition.Album,
            SearchPreferenceCondition.Username];
        CollectionAssert.AreEqual(expected, preferred.ConfiguredPreferredConditions.ToArray());
        CollectionAssert.AreEqual(expected, preferred.SatisfiedPreferredConditions.ToArray());
        Assert.IsNotNull(page.NextPosition);
        var continued = (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(page.NextPosition, 1))!;
        var other = continued.Items.Single();
        Assert.AreEqual(SearchPreferenceTier.Other, other.PreferenceTier);
        CollectionAssert.AreEqual(expected, other.ConfiguredPreferredConditions.ToArray());
        Assert.IsTrue(other.SatisfiedPreferredConditions.Count < expected.Length);
        Assert.IsTrue(other.SatisfiedPreferredConditions.Contains(SearchPreferenceCondition.Title));
        var emptyKernel = new SearchViewKernel(new FileSearchProjection(new SongQuery { Title = "Track" }, true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions() });
        var empty = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files));
        empty = store.Publish(empty.Id, emptyKernel.Apply([Input(1, "Peer", "Track.flac", 320)], 1, true), "Complete");
        string ReadPages() => JsonSerializer.Serialize(new
        {
            First = store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 1),
            Continued = store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(page.NextPosition, 1),
            Unconfigured = store.GetSnapshot(empty.Id, empty.Revision, default)?.GetFiles(null, 1),
        });
        var unconfigured = (store.GetSnapshot(empty.Id, empty.Revision, default)?.GetFiles(null, 1))!.Items.Single();
        Assert.AreEqual(0, unconfigured.ConfiguredPreferredConditions.Count);
        Assert.AreEqual(0, unconfigured.SatisfiedPreferredConditions.Count);
        string before = ReadPages();

        Assert.AreEqual(before, ReadPages());
    }

    [TestMethod]
    public void FailedPublicationLeavesThePreviousSnapshotIntact()
    {
        var store = new SearchViewCache();

        SearchViewInfo initial = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files));
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(SearchViewProjectionKind.Files, new SongQuery { Title = "Fixture" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions() });
        SearchProjectionInput[] inputs = Enumerable.Range(1, 600)
            .Select(index => Input(index, index switch { 1 => "peer", 2 => "Peer", 3 => "Péér", _ => $"Peer-{index}" },
                $@"Share\File-{index:D4}.flac", 320)).ToArray();
        SearchViewKernelUpdate update = kernel.Apply(inputs, 2, true);

        var folders = new SearchViewKernel(new SearchViewProjectionDefinition(SearchViewProjectionKind.GenericDirectories,
            new SongQuery { Title = "Fixture" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new(), PreferredCond = new() });
        var invalid = update with { ChangedDirectories = folders.Apply(
            [Input(601, "missing", @"Share\Missing.flac", 320)], 2, true).ChangedDirectories };
        // Files have already been staged when the invalid membership is encountered.
        Assert.ThrowsExactly<InvalidDataException>(() => store.Publish(initial.Id, invalid, "Complete"));
        SearchViewInfo afterFailure = (store.Get(initial.Id))!;
        Assert.AreEqual(initial.Revision, afterFailure.Revision);
        Assert.AreEqual(initial.Counters, afterFailure.Counters);
        Assert.AreEqual(initial.ConsumedSequence, afterFailure.ConsumedSequence);
        Assert.AreEqual(initial.IsComplete, afterFailure.IsComplete);
        Assert.IsNull(store.GetSnapshot(initial.Id, initial.Revision + 1)?.Revision);
        Assert.AreEqual(0, (store.GetSnapshot(initial.Id, initial.Revision, default)?.GetFiles(null, 10))!.Items.Count);
        SearchViewInfo published = store.Publish(initial.Id, update, "Complete");
        Assert.AreEqual(initial.Revision + 1, published.Revision);
        Assert.IsTrue(published.IsComplete);
        Assert.AreEqual(600, published.Counters.ObservedPeerCount,
            "Peer counts must preserve exact spelling, including case and Unicode, across the batch.");
        CollectionAssert.AreEquivalent(inputs.Select(input => input.Filename).ToArray(),
            (store.GetSnapshot(initial.Id, published.Revision, default)?.GetFiles(null, 1000))!.Items
                .Select(item => item.Input.Filename).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Publish(initial.Id, update with { SourceRevision = 0 }, "Complete"));
    }

    [TestMethod]
    public void FileAndDirectoryRefsAreStableAcrossViewsOfOneSourceOnly()
    {
        var store = new SearchViewCache();

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
        SearchViewInfo first = store.Create(source, Definition(SearchViewProjectionKind.GenericDirectories));
        SearchViewInfo second = store.Create(source, Definition(SearchViewProjectionKind.GenericDirectories));
        SearchViewInfo otherSource = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories));
        first = store.Publish(first.Id, update, "Live");
        second = store.Publish(second.Id, update, "Live");
        otherSource = store.Publish(otherSource.Id, update, "Live");

        (string File, string Directory) Refs(SearchViewInfo view)
        {
            var file = (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 10))!.Items.Single();
            Assert.AreEqual(JsonSerializer.Serialize(input), JsonSerializer.Serialize(file.Input),
                "Stored facts must preserve exact Unicode, nullable metadata and full-width numeric values.");
            return (file.Ref, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 10))!.Items.Single().Ref);
        }
        var firstRefs = Refs(first);
        Assert.AreEqual(firstRefs, Refs(second));
        Assert.AreNotEqual(firstRefs, Refs(otherSource));
    }

    [TestMethod]
    public void GroupRefsAndOptionsRemainStableAcrossViews()
    {
        var store = new SearchViewCache();

        Guid source = Guid.NewGuid();
        var retainedViews = new List<SearchViewInfo>();
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
            MinGroupShares = 1,
            Relax = true,
        };
        SearchViewKernelUpdate trackUpdate = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.TrackGroups,
                new SongQuery { Artist = "ELO" }),
            settings).Apply(
            [Input(1, "Peer", @"ELO - Track.flac", 900)], 1, false);
        SearchViewKernelUpdate albumUpdate = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AlbumGroups,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }),
            settings).Apply(
            [Input(1, "Peer", @"ELO\Time\01. Track.flac", 900)], 1, false);
        foreach (var (kind, update) in new[]
        {
            (SearchViewProjectionKind.TrackGroups, trackUpdate),
            (SearchViewProjectionKind.AlbumGroups, albumUpdate),
        })
        {
            // Each mode's fixture describes a different raw search response.
            source = Guid.NewGuid();
            SearchViewInfo first = store.Create(source, Definition(kind));
            SearchViewInfo second = store.Create(source, Definition(kind));
            first = store.Publish(first.Id, update, "Live");
            second = store.Publish(second.Id, update, "Live");
            string firstRef = kind == SearchViewProjectionKind.TrackGroups
                ? (store.GetSnapshot(first.Id, first.Revision, default)?.GetTrackGroups(null, 10))!.Items.Single().Ref
                : (store.GetSnapshot(first.Id, first.Revision, default)?.GetAlbumGroups(null, 10))!.Items.Single().Ref;
            string secondRef = kind == SearchViewProjectionKind.TrackGroups
                ? (store.GetSnapshot(second.Id, second.Revision, default)?.GetTrackGroups(null, 10))!.Items.Single().Ref
                : (store.GetSnapshot(second.Id, second.Revision, default)?.GetAlbumGroups(null, 10))!.Items.Single().Ref;
            Assert.AreEqual(firstRef, secondRef, kind.ToString());
            retainedViews.AddRange([first, second]);
        }
        string ReadSnapshot(SearchViewInfo view)
        {
            if (view.ProjectionKind == SearchViewProjectionKind.TrackGroups)
            {
                var page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 100))!;
                return JsonSerializer.Serialize(new { Page = page,
                    Options = store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(page.Items.Single().Ref, null, 500) });
            }
            var albums = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroups(null, 100, initialFileLimit: 24))!;
            return JsonSerializer.Serialize(new { Page = albums,
                Options = store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(albums.Items.Single().Ref, null, 100, initialFileLimit: 24) });
        }
        foreach (var retainedView in retainedViews)
            Assert.IsNotNull(ReadSnapshot(retainedView));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileSortOrdersPageTheWholeRevisionWithoutDuplicates(bool readOrder)
    {
        var store = new SearchViewCache();

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
            SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files, readOrder ? SearchViewSortOrder.Relevance : order));
            view = store.Publish(view.Id, initial, "Live");
            long oldRevision = view.Revision;
            var pinned = (store.GetSnapshot(view.Id, oldRevision, default)?.GetFiles(null, 2, sortOrder: readOrder ? order : null))!;
            CollectionAssert.AreEqual(expected.Old.Take(2).ToArray(), pinned.Items.Select(item => item.Input.Username).ToArray());
            view = store.Publish(view.Id, kernel.Apply([late], 7, true), "Complete");
            var remaining = (store.GetSnapshot(view.Id, oldRevision, default)?.GetFiles(pinned.NextPosition, 500, sortOrder: readOrder ? order : null))!;
            CollectionAssert.AreEqual(expected.Old, pinned.Items.Concat(remaining.Items)
                .Select(item => item.Input.Username).ToArray(), $"{order}: pinned cursor after arrivals");
            Assert.IsNull(remaining.NextPosition);
            Assert.AreEqual(readOrder ? SearchViewSortOrder.Relevance : order, (store.Get(view.Id))!.SortOrder);
            foreach (int pageSize in new[] { 1, 2 })
            {
                var actual = new List<SearchViewFile>();
                SearchViewFilePosition? cursor = null;
                do
                {
                    SearchViewFilePage page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(cursor, pageSize, sortOrder: readOrder ? order : null))!;
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
    [DataRow(false)]
    [DataRow(true)]
    public void DirectorySizeAndCountSortsUseTheBoundRevisionTotals(bool readOrder)
    {
        var store = new SearchViewCache();

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
            SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories, readOrder ? SearchViewSortOrder.Relevance : order));
            view = store.Publish(view.Id, first, "Live");
            long oldRevision = view.Revision;
            view = store.Publish(view.Id, second, "Live");
            foreach (int pageSize in new[] { 1, 2 })
            {
                var actual = new List<string>();
                SearchViewDirectoryPosition? cursor = null;
                do
                {
                    SearchViewDirectoryPage page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(cursor, pageSize, sortOrder: readOrder ? order : null))!;
                    actual.AddRange(page.Items.Select(item => item.FolderPath.Split('\\')[^1]));
                    cursor = page.NextPosition;
                }
                while (cursor != null);
                CollectionAssert.AreEqual(expected, actual.ToArray(), $"{order}, page size {pageSize}");
            }
            if (order == SearchViewSortOrder.DirectorySizeDescending)
            {
                SearchViewDirectoryPage old = (store.GetSnapshot(view.Id, oldRevision, default)?.GetDirectories(null, 3, sortOrder: readOrder ? order : null))!;
                CollectionAssert.AreEqual(
                    new[] { "B", "C", "A" },
                    old.Items.Select(item => item.FolderPath.Split('\\')[^1]).ToArray());
            }
        }
    }

    [TestMethod]
    public void DirectoryNameOrderUsesNumericCaseInsensitiveCursorComparison()
    {
        var store = new SearchViewCache();

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
        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories, SearchViewSortOrder.DirectoryNameAscending));
        view = store.Publish(view.Id, update, "Live");
        var names = new List<string>();
        SearchViewDirectoryPosition? cursor = null;
        do
        {
            SearchViewDirectoryPage page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(cursor, 1))!;
            names.AddRange(page.Items.Select(item => item.FolderPath.Split('\\')[^1]));
            cursor = page.NextPosition;
        }
        while (cursor != null);
        CollectionAssert.AreEqual(
            new[] { "Folder 1", "folder 2", "Folder 10" }, names.ToArray());
    }

    [TestMethod]
    public void FilteredAlbumPublishesEarlierChildrenWhenTheFolderBecomesVisible()
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.AlbumDirectories));
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
        view = store.Publish(view.Id, hidden, "Live");
        long hiddenRevision = view.Revision;
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, hiddenRevision, default)?.GetDirectories(null, 10))!.Items.Count);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, hiddenRevision, default)?.GetFiles(null, 500))!.Items.Count);

        view = store.Publish(view.Id, kernel.Apply(
            [Input(3, "Peer", @"ELO\Time\02. Target.flac", 900)], 3, true), "Complete");
        SearchViewDirectory folder = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 10))!.Items.Single();
        Assert.AreEqual(2L, folder.PublicMatchingFileCount);
        Assert.AreEqual(1L, folder.LockedMatchingFileCount);
        SearchViewDirectoryFilePage children = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(folder.Ref, null, 10))!;
        CollectionAssert.AreEquivalent(
            new[] { "01. Twilight.flac", "01. Twilight.flac", "02. Target.flac" },
            children.Items.Select(item => item.RelativePath).ToArray());
        Assert.AreEqual(1, children.Items.Count(item => item.File.Input.Visibility == SearchResultVisibility.Locked));
        Assert.AreEqual(3, (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 500))!.Items.Count);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, hiddenRevision, default)?.GetFiles(null, 500))!.Items.Count);
        var selected = new List<SearchViewCommitItem>();
        string firstRef = children.Items.Single(item => item.File.Input.Sequence == 1).Ref;
        foreach (var item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([firstRef])) ?? [])) selected.Add(item);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(first, selected[0].File!.Input);

        Assert.AreEqual(3, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(folder.Ref, null, 10))!.Items.Count);
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.Files, "root")]
    [DataRow(SearchViewProjectionKind.GenericDirectories, "root")]
    [DataRow(SearchViewProjectionKind.AlbumDirectories, "root")]
    [DataRow(SearchViewProjectionKind.TrackGroups, "root")]
    [DataRow(SearchViewProjectionKind.AlbumGroups, "root")]
    [DataRow(SearchViewProjectionKind.GenericDirectories, "children")]
    [DataRow(SearchViewProjectionKind.TrackGroups, "options")]
    [DataRow(SearchViewProjectionKind.AlbumGroups, "options")]
    [DataRow(SearchViewProjectionKind.AlbumGroups, "chosen")]
    public void PruningDuringAReadCannotReturnEmptyRowsWithNonemptyCounters(
        SearchViewProjectionKind kind, string read)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        var store = new SearchViewCache(clock);

        var projection = kind is SearchViewProjectionKind.AlbumDirectories or SearchViewProjectionKind.AlbumGroups
            ? new SearchViewProjectionDefinition(kind, AlbumQuery: new AlbumQuery { Artist = "Artist", Album = "Album" })
            : new SearchViewProjectionDefinition(kind, new SongQuery { Artist = "Artist", Title = "Track" });
        var kernel = new SearchViewKernel(projection, new SearchSettings
        {
            NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(), MinGroupShares = 1,
        });
        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(kind), retention: TimeSpan.FromMinutes(1));
        view = store.Publish(view.Id, kernel.Apply([Input(1, "Peer", @"Artist\Album\01 - Track.flac", 900)], 1, true), "Complete");
        string? groupRef = null;
        string? directoryRef = null;
        if (kind == SearchViewProjectionKind.AlbumGroups)
        {
            var group = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroups(null, 100))!.Items.Single();
            groupRef = group.Ref;
            directoryRef = group.Representative.Ref;
        }
        else if (kind == SearchViewProjectionKind.TrackGroups)
            groupRef = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 100))!.Items.Single().Ref;
        else if (read == "children")
            directoryRef = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 100))!.Items.Single().Ref;
        int armed = 1;
        int pruned = 0;
        using var observer = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Sockseek.Server.SearchViewCache",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "snapshot" || activity.GetTagItem("view.id") is not Guid id
                    || id != view.Id || Interlocked.Exchange(ref armed, 0) == 0) return;
                // Expire and prune after the request has read its counters but
                // before it reads its page. No timer or polling race is needed.
                clock.Advance(TimeSpan.FromMinutes(2));
                pruned = store.PruneExpired();
            },
        };
        ActivitySource.AddActivityListener(observer);
        (SearchViewRevision? Revision, int Rows) page = read switch
        {
            "children" => ChildPage(),
            "options" => OptionPage(),
            "chosen" => ChosenPage(),
            _ => RootPage(),
        };
        (SearchViewRevision?, int) RootPage() => kind switch
        {
            SearchViewProjectionKind.Files => FilePage(),
            SearchViewProjectionKind.TrackGroups => TrackPage(),
            SearchViewProjectionKind.AlbumGroups => AlbumPage(),
            _ => DirectoryPage(),
        };
        Assert.AreEqual(1, pruned);
        Assert.IsNotNull(page.Revision);
        Assert.AreEqual(1L, page.Revision.Counters.TopLevelItemCount);
        Assert.AreEqual(1, page.Rows, "An acquired snapshot remains readable after expiry/pruning.");
        Assert.IsNull(store.GetSnapshot(view.Id, view.Revision));
        Assert.IsNull(store.Get(view.Id));

        (SearchViewRevision?, int) FilePage()
        { var result = store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 100); return (result?.Revision, result?.Items.Count ?? 0); }
        (SearchViewRevision?, int) DirectoryPage()
        {
            var result = store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 100, initialFileLimit: 24);
            if (result != null) AssertPreview(result.DirectoryFilePreviews, result.Items.Single().Ref);
            return (result?.Revision, result?.Items.Count ?? 0);
        }
        (SearchViewRevision?, int) TrackPage()
        { var result = store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 100); return (result?.Revision, result?.Items.Count ?? 0); }
        (SearchViewRevision?, int) AlbumPage()
        {
            var result = store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroups(null, 100, initialFileLimit: 24);
            if (result != null) AssertPreview(result.DirectoryFilePreviews, result.Items.Single().Representative.Ref);
            return (result?.Revision, result?.Items.Count ?? 0);
        }
        (SearchViewRevision?, int) ChildPage()
        {
            var result = store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(directoryRef!, null, 100);
            return (result?.Revision, result?.Items.Count ?? 0);
        }
        (SearchViewRevision?, int) OptionPage()
        {
            if (kind == SearchViewProjectionKind.TrackGroups)
            {
                var result = store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(groupRef!, null, 100);
                return (result?.Revision, result?.Items.Count ?? 0);
            }
            var albums = store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(groupRef!, null, 100, initialFileLimit: 24);
            if (albums != null) AssertPreview(albums.DirectoryFilePreviews, albums.Items.Single().Ref);
            return (albums?.Revision, albums?.Items.Count ?? 0);
        }
        (SearchViewRevision?, int) ChosenPage()
        {
            var result = store.GetSnapshot(view.Id, view.Revision, default)?.ResolveChosenOptions(new Dictionary<string, string> { [groupRef!] = directoryRef! }, initialFileLimit: 24);
            if (result != null) AssertPreview(result.DirectoryFilePreviews, result.Albums[groupRef!].Ref);
            return (result?.Revision, result?.Albums.Count ?? 0);
        }
        static void AssertPreview(IReadOnlyDictionary<string, SearchViewDirectoryFilePreview> previews, string folder)
        {
            Assert.IsTrue(previews.TryGetValue(folder, out var preview), "The returned folder must include its file prefix.");
            Assert.AreEqual(1, preview.Items.Count, "Pruning cannot empty an otherwise populated folder preview.");
            Assert.AreEqual(@"Artist\Album\01 - Track.flac", preview.Items[0].File.Input.Filename);
            Assert.IsNull(preview.NextPosition);
        }
    }

    [TestMethod]
    public void ReadsRenewOnlyTheirSnapshotAndExpiryNeverSubstitutesNewResults()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-02T00:00:00Z"));
        var store = new SearchViewCache(clock);
        var kernel = Kernel();
        var view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files));
        view = store.Publish(view.Id, kernel.Apply([Input(1, "A", "Track.flac", 320)], 1, false), "Live");
        long reviewed = view.Revision;
        clock.Advance(TimeSpan.FromMinutes(5));
        view = store.Publish(view.Id, kernel.Apply([Input(2, "B", "Track.flac", 320)], 2, false), "Live");
        long unused = view.Revision;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, reviewed, default)?.GetFiles(null, 500))!.Items.Count);
        clock.Advance(TimeSpan.FromMinutes(2));
        view = store.Publish(view.Id, kernel.Apply([Input(3, "C", "Track.flac", 320)], 3, true), "Complete");
        clock.Advance(TimeSpan.FromMinutes(9));
        store.PruneExpired();
        Assert.IsNull(store.GetSnapshot(view.Id, unused, default)?.GetFiles(null, 500));
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, reviewed, default)?.GetFiles(null, 500))!.Items.Count);
        Assert.AreEqual(3, (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 500))!.Items.Count);
        Assert.AreEqual(new SearchViewSelectionCounts(1, 0, 1),
            store.GetSnapshot(view.Id, reviewed, default)?.CountTopLevelSelection(RefSelectionMode.AllExcept, new HashSet<string>()));
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.IsNotNull(store.Peek(view.Id));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(1, store.PruneExpired(), "Background metadata inspection must not renew the View.");
        Assert.IsNull(store.Get(view.Id));
        Assert.IsNull(store.GetSnapshot(view.Id, reviewed, default)?.GetFiles(null, 500));
    }

    [TestMethod]
    public void ExpiredViewsDisappearAndPruneWithoutAWait()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        var store = new SearchViewCache(clock);

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files), retention: TimeSpan.FromMinutes(1));
        Assert.IsNotNull(store.GetSnapshot(view.Id, 0, default)?.GetFiles(null, 10));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsNull(store.GetSnapshot(view.Id, 0, default)?.GetFiles(null, 10));
        Assert.AreEqual(1, store.PruneExpired());
        Assert.IsNull(store.Get(view.Id));
    }

    [TestMethod]
    public void ImmutableRevisionsPageBySortKeyAndKeepSelectionCounts()
    {

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

        var store = new SearchViewCache();
        {

            SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files));
            viewId = view.Id;
            Assert.AreEqual(0, (store.GetSnapshot(view.Id, 0, default)?.GetFiles(null, 10))!.Items.Count);

            view = store.Publish(view.Id, firstUpdate, "Live");
            Assert.AreEqual(1L, view.Revision);
            view = store.Publish(view.Id, secondUpdate, "Live");
            Assert.AreEqual(2L, view.Revision);

            SearchViewFilePage revisionOne = (store.GetSnapshot(view.Id, 1, default)?.GetFiles(null, 10))!;
            CollectionAssert.AreEqual(
                new[] { "slow" },
                revisionOne.Items.Select(item => item.Input.Username).ToArray());
            Assert.IsFalse(revisionOne.Items.Single().NecessaryConditionsSatisfied);

            SearchViewFilePage firstPage = (store.GetSnapshot(view.Id, 2, default)?.GetFiles(null, 1))!;
            Assert.AreEqual(1, firstPage.Items.Count);
            Assert.IsNotNull(firstPage.NextPosition);
            SearchViewFilePage secondPage = (store.GetSnapshot(view.Id, 2, default)?.GetFiles(firstPage.NextPosition, 1))!;
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
            view = store.Publish(view.Id, thirdUpdate, "Live");
            Assert.AreEqual(3L, view.Revision);
        }

        {

            SearchViewInfo view = (store.Get(viewId))!;
            Assert.AreEqual(3L, view.Revision);
            Assert.AreEqual(3L, view.Counters.ProjectedFileCount);
            SearchViewFilePage revisionTwo = (store.GetSnapshot(viewId, 2, default)?.GetFiles(null, 10))!;
            Assert.AreEqual(2, revisionTwo.Items.Count);
            Assert.IsFalse(revisionTwo.Items.Single(item => item.Input.Username == "slow")
                .NecessaryConditionsSatisfied);
            Assert.IsTrue(revisionTwo.Items.Single(item => item.Input.Username == "fast")
                .NecessaryConditionsSatisfied);
            SearchViewFilePage current = (store.GetSnapshot(viewId, 3, default)?.GetFiles(null, 10))!;
            Assert.AreEqual(3, current.Items.Count);
            var later = new HashSet<string>(StringComparer.Ordinal)
            {
                current.Items.Single(item => item.Input.Username == "later").Ref,
            };
            Assert.AreEqual(new SearchViewSelectionCounts(0, 0, 0),
                store.GetSnapshot(viewId, 2, default)?.CountTopLevelSelection(RefSelectionMode.Only, later));
            Assert.AreEqual(new SearchViewSelectionCounts(2, 0, 2),
                store.GetSnapshot(viewId, 2, default)?.CountTopLevelSelection(RefSelectionMode.AllExcept, later),
                "An exclusion admitted later must not change a pinned revision's counts.");
            Assert.AreEqual(new SearchViewSelectionCounts(0, 1, 1),
                store.GetSnapshot(viewId, 3, default)?.CountTopLevelSelection(RefSelectionMode.Only, later));
            Assert.AreEqual(new SearchViewSelectionCounts(2, 0, 2),
                store.GetSnapshot(viewId, 3, default)?.CountTopLevelSelection(RefSelectionMode.AllExcept, later));
        }
    }

    [TestMethod]
    public void DirectoryVersionsAndChildrenPageAtTheExactBoundRevision()
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories));
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

        view = store.Publish(view.Id, kernel.Apply(
            [Input(1, "Peer", @"Share\One\Track.flac", 192)], 1, false), "Live");
        long firstRevision = view.Revision;
        view = store.Publish(view.Id, kernel.Apply(
            [Input(2, "Peer", @"Share\One\Track.flac", 900) with
            {
                Visibility = SearchResultVisibility.Locked,
            }], 2, false), "Live");
        long secondRevision = view.Revision;
        view = store.Publish(view.Id, kernel.Apply(
            [Input(3, "Other", @"Share\Two\Track.flac", 900)], 3, false), "Live");
        SearchViewDirectoryPage current = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 10, initialFileLimit: 1))!;
        Assert.AreEqual(2, current.Items.Count);
        var currentPreviews = current.DirectoryFilePreviews;
        Assert.AreEqual(2, currentPreviews.Count);
        foreach (SearchViewDirectory folder in current.Items)
            Assert.AreEqual(1, currentPreviews[folder.Ref].Items.Count);

        SearchViewDirectoryPage first = (store.GetSnapshot(view.Id, firstRevision, default)?.GetDirectories(null, 10))!;
        Assert.AreEqual(1, first.Items.Count);
        Assert.AreEqual(1L, first.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(0L, first.Items[0].LockedMatchingFileCount);

        SearchViewDirectoryPage second = (store.GetSnapshot(view.Id, secondRevision, default)?.GetDirectories(null, 10, initialFileLimit: 1))!;
        Assert.AreEqual(1, second.Items.Count);
        Assert.AreEqual(1L, second.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(1L, second.Items[0].LockedMatchingFileCount);
        Assert.AreEqual(first.Items[0].Ref, second.Items[0].Ref,
            "A directory keeps one stable opaque ref across versions.");
        Assert.AreEqual(new SearchViewSelectionCounts(1, 0, 1),
            store.GetSnapshot(view.Id, firstRevision, default)?.CountTopLevelSelection(RefSelectionMode.Only, new HashSet<string>([first.Items[0].Ref])));
        Assert.AreEqual(new SearchViewSelectionCounts(1, 1, 1),
            store.GetSnapshot(view.Id, secondRevision, default)?.CountTopLevelSelection(RefSelectionMode.Only, new HashSet<string>([second.Items[0].Ref])));

        SearchViewDirectoryFilePage childOne = (store.GetSnapshot(view.Id, secondRevision, default)?.GetDirectoryFiles(second.Items[0].Ref, null, 1))!;
        var firstPreview = second.DirectoryFilePreviews[second.Items[0].Ref];
        Assert.AreEqual(childOne.Items.Single().Ref, firstPreview.Items.Single().Ref);
        Assert.AreEqual(childOne.NextPosition, firstPreview.NextPosition);
        Assert.AreEqual(second.Items[0].Ref, childOne.DirectorySummary.Ref);
        Assert.AreEqual(second.Items[0].PublicMatchingFileCount, childOne.DirectorySummary.PublicMatchingFileCount);
        Assert.AreEqual(second.Items[0].LockedMatchingFileCount, childOne.DirectorySummary.LockedMatchingFileCount);
        Assert.AreEqual(second.Items[0].BestChild.Ref, childOne.DirectorySummary.BestChild.Ref);
        Assert.AreEqual(1, childOne.Items.Count);
        Assert.IsNotNull(childOne.NextPosition);
        SearchViewDirectoryFilePage childTwo = (store.GetSnapshot(view.Id, secondRevision, default)?.GetDirectoryFiles(second.Items[0].Ref, childOne.NextPosition, 1))!;
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
        var childInfo = (store.GetSnapshot(view.Id, secondRevision, default)?.ReadDirectoryFileRefs([.. childRefs, childRefs[0], "missing-ref"]) ?? []);
        Assert.AreEqual(2, childInfo.Count);
        Assert.AreEqual(1, childInfo.Count(item => item.Visibility == SearchResultVisibility.Public));
        Assert.AreEqual(1, childInfo.Count(item => item.Visibility == SearchResultVisibility.Locked));

        var selectedChild = new List<SearchViewCommitItem>();
        foreach (SearchViewCommitItem item in (store.GetSnapshot(view.Id, secondRevision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([childOne.Items[0].Ref], StringComparer.Ordinal)) ?? []))
        {
            selectedChild.Add(item);
        }
        Assert.AreEqual(1, selectedChild.Count);
        Assert.IsNotNull(selectedChild[0].File);
        Assert.AreEqual(second.Items[0].Ref, selectedChild[0].ParentRef);
        Assert.AreEqual(childOne.Items[0].Ref, selectedChild[0].File!.Ref);

        SearchViewDirectoryPage pageOne = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 1))!;
        SearchViewDirectoryPage pageTwo = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(pageOne.NextPosition, 1))!;
        Assert.AreEqual(2, pageOne.Items.Concat(pageTwo.Items)
            .Select(item => item.Ref).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(new SearchViewSelectionCounts(1, 0, 1),
            store.GetSnapshot(view.Id, view.Revision, default)?.CountTopLevelSelection(RefSelectionMode.AllExcept, new HashSet<string>([second.Items[0].Ref])));

        // Repeated reads preserve the pinned cursor and the earlier best child.
        string ReadPages() => JsonSerializer.Serialize(new
        {
            First = store.GetSnapshot(view.Id, firstRevision, default)?.GetDirectories(null, 10, 1),
            Second = store.GetSnapshot(view.Id, secondRevision, default)?.GetDirectories(null, 10, 1),
            Current = store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 1, 1),
            Continued = store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(pageOne.NextPosition, 1, 1),
        });
        string beforeRepeat = ReadPages();

        Assert.AreEqual(beforeRepeat, ReadPages());

    }

    [TestMethod]
    public void GenericDirectoryMergeKeepsOldRevisionSelectionExact()
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories));
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

        view = store.Publish(view.Id, kernel.Apply(
            [Input(1, "Peer", @"A\B\Track one.flac", 900),
             Input(2, "Peer", @"A\C\Track two.flac", 900)], 1, false), "Live");
        long oldRevision = view.Revision;
        SearchViewDirectoryPage oldPage = (store.GetSnapshot(view.Id, oldRevision, default)?.GetDirectories(null, 10))!;
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
        store.PublishRetrievedDirectory(view.Id, oldBRef, retrieved, [cover], Input(1, "Peer", @"A\B\Track one.flac", 900));

        view = store.Publish(view.Id, kernel.Apply(
            [Input(3, "Peer", @"A\B\D\Track three.flac", 900),
             Input(4, "Peer", @"A\Track root.flac", 900)], 2, false), "Live");
        SearchViewDirectoryPage current = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 10))!;
        Assert.AreEqual(1, current.Items.Count);
        Assert.AreEqual(@"A", current.Items[0].FolderPath);
        Assert.AreEqual(4L, current.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(5L, current.Items[0].RetrievedFileCount);
        Assert.AreEqual(6000L, current.Items[0].RetrievedBytes);
        Assert.IsFalse(current.Items[0].IsFullyRetrieved);
        var children = new List<SearchViewDirectoryFile>();
        SearchViewDirectoryFilePosition? cursor = null;
        do
        {
            SearchViewDirectoryFilePage page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(current.Items[0].Ref, cursor, 2))!;
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
        Assert.AreEqual(new SearchViewSelectionCounts(5, 0, 1),
            store.GetSnapshot(view.Id, view.Revision, default)?.CountTopLevelSelection(RefSelectionMode.Only, new HashSet<string>([current.Items[0].Ref])));

        SearchViewDirectoryPage oldAgain = (store.GetSnapshot(view.Id, oldRevision, default)?.GetDirectories(null, 10))!;
        Assert.AreEqual(2, oldAgain.Items.Count);
        Assert.AreEqual(new SearchViewSelectionCounts(1, 0, 1),
            store.GetSnapshot(view.Id, oldRevision, default)?.CountTopLevelSelection(RefSelectionMode.Only, new HashSet<string>([oldBRef])));
        SearchViewDirectoryFilePage oldChildren = (store.GetSnapshot(view.Id, oldRevision, default)?.GetDirectoryFiles(oldBRef, null, 10))!;
        CollectionAssert.AreEqual(
            new[] { "Track one.flac" },
            oldChildren.Items.Select(item => item.RelativePath).ToArray());
        var fileRefs = children.Select(item => item.Ref).ToArray();
        var oldMemberships = (store.GetSnapshot(view.Id, oldRevision, default)?.ReadDirectoryFileRefs(fileRefs) ?? []);
        Assert.AreEqual(2, oldMemberships.Count);
        CollectionAssert.AreEquivalent(oldPage.Items.Select(item => item.Ref).ToArray(),
            oldMemberships.Select(item => item.DirectoryRef).ToArray());
        var currentMemberships = (store.GetSnapshot(view.Id, view.Revision, default)?.ReadDirectoryFileRefs(fileRefs) ?? []);
        Assert.AreEqual(5, currentMemberships.Count);
        Assert.IsTrue(currentMemberships.All(item => item.DirectoryRef == current.Items[0].Ref));
        var oldCommit = new List<SearchViewCommitItem>();
        foreach (SearchViewCommitItem item in (store.GetSnapshot(view.Id, oldRevision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([oldBRef])) ?? []))
            oldCommit.Add(item);
        Assert.AreEqual(1, oldCommit.Count);
        Assert.AreEqual(@"A\B", oldCommit[0].Directory!.FolderPath);
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumGroups)]
    public void AlbumParentAbsorbsEarlierChildrenWithoutRepublishingFileFacts(SearchViewProjectionKind kind)
    {
        var store = new SearchViewCache();

        var view = store.Create(Guid.NewGuid(), Definition(kind));
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(kind,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }), new SearchSettings
        {
            NecessaryCond = new(), PreferredCond = new(), NecessaryFolderCond = new(), MinGroupShares = 1,
        });
        var first = Input(1, "Peer", @"ELO\Time\Bonus\01. Twilight.flac", 900);
        view = store.Publish(view.Id, kernel.Apply(
            [first, first with { Sequence = 2, Visibility = SearchResultVisibility.Locked }], 1, false), "Live");
        long childRevision = view.Revision;
        var child = (store.GetSnapshot(view.Id, childRevision, default)?.GetDirectories(null, 10))!.Items.Single();
        var earlier = (store.GetSnapshot(view.Id, childRevision, default)?.GetDirectoryFiles(child.Ref, null, 10))!;
        Assert.AreEqual(2, earlier.Items.Count);

        var change = kernel.Apply([Input(3, "Peer", @"ELO\Time\02. Target.flac", 900)], 2, true);
        Assert.AreEqual(1, change.ChangedFiles.Count, "An absorbed child supplies links, not duplicate file facts.");
        view = store.Publish(view.Id, change, "Complete");
        var parent = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 10))!.Items.Single();
        Assert.AreEqual(@"ELO\Time", parent.FolderPath);
        var current = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(parent.Ref, null, 10))!;
        Assert.AreEqual(3, current.Items.Count);
        Assert.AreEqual(3, (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 500))!.Items.Count);
        foreach (var file in earlier.Items)
            Assert.AreEqual(file.File, current.Items.Single(item => item.Ref == file.Ref).File);
        Assert.IsNull(store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(child.Ref, null, 10));

        CollectionAssert.AreEquivalent(earlier.Items.ToArray(),
            (store.GetSnapshot(view.Id, childRevision, default)?.GetDirectoryFiles(child.Ref, null, 10))!.Items.ToArray());
        CollectionAssert.AreEquivalent(current.Items.ToArray(),
            (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(parent.Ref, null, 10))!.Items.ToArray());
    }

    [TestMethod]
    public void RetrievedDirectoryPublishesTotalsAndChildrenWithoutChangingIssuingRevision()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-06T00:00:00Z"));
        var store = new SearchViewCache(clock);

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories));
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
        view = store.Publish(view.Id, kernel.Apply(
                [Input(1, "Peer", @"Share\Album\Track.mp3", 192)],
                1,
                false), "Live");
        long issuingRevision = view.Revision;
        SearchViewDirectory issued = (store.GetSnapshot(view.Id, issuingRevision, default)?.GetDirectories(null, 10))!.Items.Single();

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
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.PublishRetrievedDirectory(view.Id, issued.Ref, mismatched, projected, peerObservation));
        Assert.AreEqual(issuingRevision, (store.Get(view.Id))!.Revision);

        SearchViewDirectoryPublishResult filtered = store.PublishRetrievedDirectory(view.Id, issued.Ref, snapshot, [projected[0]], peerObservation);
        Assert.AreEqual(0, filtered.NewFileCount);
        SearchViewDirectory filteredDirectory = (store.GetSnapshot(view.Id, filtered.View.Revision, default)?.GetDirectories(null, 10))!.Items.Single();
        Assert.IsTrue(filteredDirectory.IsFullyRetrieved);
        Assert.AreEqual(1L, filteredDirectory.RetrievedFileCount);
        Assert.AreEqual(1000L, filteredDirectory.RetrievedBytes);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, filtered.View.Revision, default)?.GetDirectoryFiles(issued.Ref, null, 10))!.Items.Count);
        SearchDirectoryObservation firstObservation = (store.GetDirectoryObservation(view.SourceJobId, snapshot.Identity))!;
        Assert.AreEqual(2, firstObservation.Snapshot.Files.Count);
        Assert.AreEqual(peerObservation.Filename, firstObservation.PeerObservation.Filename);

        SearchViewDirectoryPublishResult published = store.PublishRetrievedDirectory(view.Id, issued.Ref, snapshot, projected, peerObservation);
        Assert.AreEqual(1, published.NewFileCount);
        Assert.AreEqual(filtered.View.Revision + 1, published.View.Revision);

        SearchViewDirectory current = (store.GetSnapshot(view.Id, published.View.Revision, default)?.GetDirectories(null, 10))!.Items.Single();
        Assert.IsTrue(current.IsFullyRetrieved);
        Assert.AreEqual(2L, current.RetrievedFileCount);
        Assert.AreEqual(3000L, current.RetrievedBytes);
        {

            SearchDirectoryObservation retained = (store.GetDirectoryObservation(view.SourceJobId, snapshot.Identity))!;
            Assert.AreNotEqual(firstObservation.Id, retained.Id);
            Assert.AreEqual(1, retained.SourceRevision);
            Assert.IsTrue(retained.Snapshot.IsComplete);
            CollectionAssert.AreEqual(
                new[] { @"Share\Album\Track.mp3", @"Share\Album\Cover.jpg" },
                retained.Snapshot.Files.Select(file => file.Filename).ToArray());
            Assert.AreEqual(2000L, retained.Snapshot.Files[1].Size);
        }
        Assert.AreEqual(2, (store.GetSnapshot(view.Id, published.View.Revision, default)?.GetDirectoryFiles(issued.Ref, null, 10))!.Items.Count);

        SearchViewDirectory old = (store.GetSnapshot(view.Id, issuingRevision, default)?.GetDirectories(null, 10))!.Items.Single();
        Assert.IsFalse(old.IsFullyRetrieved);
        Assert.IsNull(old.RetrievedFileCount);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, issuingRevision, default)?.GetDirectoryFiles(issued.Ref, null, 10))!.Items.Count);

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
        SearchViewInfo sibling = store.Create(view.SourceJobId, Definition(SearchViewProjectionKind.GenericDirectories), retention: TimeSpan.FromDays(45));
        sibling = store.Publish(sibling.Id, siblingKernel.Apply([peerObservation], 1, false), "Live");
        var pending = new List<SearchDirectoryObservationTarget>();
        foreach (SearchDirectoryObservationTarget target in
            store.ReadUnappliedDirectoryObservations(sibling.Id))
            pending.Add(target);
        Assert.AreEqual(1, pending.Count);
        SearchDirectoryObservation latest = (store.GetDirectoryObservation(view.SourceJobId, snapshot.Identity))!;
        Assert.AreEqual(latest.Id, pending[0].ObservationId);
        Assert.AreEqual(snapshot.Identity, pending[0].Identity);
        store.PublishRetrievedDirectory(sibling.Id, pending[0].DirectoryRef, latest.Snapshot, projected.Select(file => file with { Input = file.Input with { Sequence = file.Input.Sequence + 100 } }).ToArray(), latest.PeerObservation, existingObservationId: latest.Id);
        Assert.IsFalse(HasUnappliedObservation(store, sibling.Id));
        Assert.IsFalse(HasUnappliedObservation(store, view.Id));
        sibling = store.Publish(sibling.Id, siblingKernel.Apply(
                [Input(2, "Peer", @"Share\Album\Later.mp3", 192)],
                2,
                false), "Live");
        SearchViewDirectory afterNewHit = (store.GetSnapshot(sibling.Id, sibling.Revision, default)?.GetDirectories(null, 10))!.Items.Single();
        Assert.IsFalse(afterNewHit.IsFullyRetrieved,
            "A newer source hit invalidates the complete-snapshot claim.");
        Assert.IsTrue(store.GetSnapshot(sibling.Id, sibling.Revision, default)?.HasNewerDirectorySourceFile(afterNewHit.Ref, latest.SourceRevision));
        Assert.AreEqual(3L, afterNewHit.RetrievedFileCount);
        Assert.AreEqual(4000L, afterNewHit.RetrievedBytes);
        Assert.AreEqual(3, (store.GetSnapshot(sibling.Id, sibling.Revision, default)?.GetDirectoryFiles(afterNewHit.Ref, null, 10))!.Items.Count);

        clock.Advance(TimeSpan.FromDays(31));
        Assert.IsNull(store.GetDirectoryObservation(view.SourceJobId, snapshot.Identity));
        Assert.AreEqual(1, store.PruneExpired());
        var retainedFiles = (store.GetSnapshot(sibling.Id, sibling.Revision, default)?.GetDirectoryFiles(afterNewHit.Ref, null, 10))!.Items;
        Assert.AreEqual(3, retainedFiles.Count, "A retained View must outlive its issuing directory snapshot.");
        var retainedCover = retainedFiles.Single(file => file.File.Input.Filename.EndsWith("Cover.jpg", StringComparison.Ordinal));
        Assert.AreEqual(103L, retainedCover.File.Input.Sequence, "View paging sequence must remain separate from snapshot ordinal.");
        Assert.AreEqual(2000L, retainedCover.File.Input.Size);
        clock.Advance(TimeSpan.FromDays(46));
        Assert.AreEqual(1, store.PruneExpired());
    }

    [TestMethod]
    public void DirectSelectionReadsExactSnapshotAndStreamsLockedRowsIndependently()
    {

        Guid viewId;
        long viewRevision;
        string[] selectedRefs;
        var store = new SearchViewCache();
        {

            SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.Files));
            var kernel = Kernel();
            view = store.Publish(view.Id, kernel.Apply(
                    [
                        Input(1, "Public", @"Share\Track.mp3", 192),
                        Input(2, "Locked", @"Share\Track.flac", 900) with
                        {
                            Visibility = SearchResultVisibility.Locked,
                        },
                    ],
                    1,
                    false), "Live");
            SearchViewFilePage page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 10))!;
            viewId = view.Id;
            viewRevision = view.Revision;
            selectedRefs = page.Items.Select(file => file.Ref).ToArray();
        }

        {

            SearchViewInfo view = (store.Get(viewId))!;
            Assert.AreEqual(SearchViewProjectionKind.Files, view.ProjectionKind);
            var selected = new List<SearchViewCommitItem>();
            foreach (SearchViewCommitItem item in (store.GetSnapshot(viewId, viewRevision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>(selectedRefs, StringComparer.Ordinal)) ?? []))
            {
                selected.Add(item);
            }
            Assert.AreEqual(2, selected.Count);
            Assert.AreEqual(1, selected.Count(item =>
                item.File?.Input.Visibility == SearchResultVisibility.Locked));

            var missingRefs = new HashSet<string>(StringComparer.Ordinal) { "not-a-file-reference" };
            foreach (string itemRef in selectedRefs)
                if (itemRef != itemRef.ToUpperInvariant()) missingRefs.Add(itemRef.ToUpperInvariant());
            Assert.AreEqual(new SearchViewSelectionCounts(0, 0, 0),
                store.GetSnapshot(viewId, viewRevision, default)?.CountTopLevelSelection(RefSelectionMode.Only, missingRefs));
            Assert.AreEqual(new SearchViewSelectionCounts(1, 1, 2),
                store.GetSnapshot(viewId, viewRevision, default)?.CountTopLevelSelection(RefSelectionMode.AllExcept, missingRefs),
                "Missing exclusions and different opaque-ref spelling must not exclude existing results.");
            var missingItems = new List<SearchViewCommitItem>();
            foreach (var item in (store.GetSnapshot(viewId, viewRevision, default)?.ReadCommitItems(RefSelectionMode.Only, missingRefs) ?? []))
                missingItems.Add(item);
            Assert.AreEqual(0, missingItems.Count);
            var excluded = new HashSet<string>(missingRefs, StringComparer.Ordinal) { selectedRefs[0] };
            var allExcept = new List<SearchViewCommitItem>();
            foreach (SearchViewCommitItem item in (store.GetSnapshot(viewId, viewRevision, default)?.ReadCommitItems(RefSelectionMode.AllExcept, excluded) ?? []))
            {
                allExcept.Add(item);
            }
            Assert.AreEqual(1, allExcept.Count);
            Assert.AreNotEqual(selectedRefs[0], allExcept[0].Ref);
            Assert.AreEqual(new SearchViewSelectionCounts(
                allExcept.Count(item => item.File!.Input.Visibility == SearchResultVisibility.Public),
                allExcept.Count(item => item.File!.Input.Visibility == SearchResultVisibility.Locked), 1),
                store.GetSnapshot(viewId, viewRevision, default)?.CountTopLevelSelection(RefSelectionMode.AllExcept, excluded));
        }
    }

    [TestMethod]
    public void AlbumDirectoryRemovalKeepsOlderRevisionAndEndsCurrentMembership()
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.AlbumDirectories));
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
        view = store.Publish(view.Id, kernel.Apply(
                [Input(1, "Peer", @"ELO\Time\01. Track.flac", 900)],
                1,
                false), "Live");
        long presentRevision = view.Revision;
        SearchViewDirectory present = (store.GetSnapshot(view.Id, presentRevision, default)?.GetDirectories(null, 10))!.Items.Single();

        view = store.Publish(view.Id, kernel.Apply(
                [Input(2, "Peer", @"ELO\Time\02. Track.flac", 900)],
                2,
                false), "Live");
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 10))!.Items.Count);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, presentRevision, default)?.GetDirectories(null, 10))!.Items.Count);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, presentRevision, default)?.GetDirectoryFiles(present.Ref, null, 10))!.Items.Count);
        Assert.IsNull(store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(present.Ref, null, 10));

        // A requested browse can finish after later arrivals remove this album.
        // Retain the useful peer snapshot without resurrecting the hidden result.
        var identity = new PeerDirectoryIdentity("Peer", @"ELO\Time");
        var snapshot = new PeerDirectorySnapshot(identity, [], isComplete: true);
        var completed = store.PublishRetrievedDirectory(view.Id, present.Ref, snapshot, [], Input(1, "Peer", @"ELO\Time\01. Track.flac", 900));
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, completed.View.Revision, default)?.GetDirectories(null, 10))!.Items.Count);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, presentRevision, default)?.GetDirectories(null, 10))!.Items.Count);
        Assert.IsNotNull(store.GetDirectoryObservation(view.SourceJobId, identity));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TrackGroupVisibilityBackfillsEarlierOptionsWithoutExposingHiddenCandidates(bool matchesLaterFilename)
    {
        var store = new SearchViewCache();

        var view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.TrackGroups));
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.TrackGroups, new SongQuery { Artist = "ELO" },
            FilterText: matchesLaterFilename ? "Special" : null), new SearchSettings
            {
                NecessaryCond = new(), PreferredCond = new(), MinGroupShares = 2,
            });
        var firstInput = Input(1, "Peer-A", @"Other\ELO - Track.flac", 900);
        var firstUpdate = kernel.Apply([firstInput], 1, false);

        view = store.Publish(view.Id, firstUpdate, "Live");

        long hiddenRevision = view.Revision;
        Assert.AreEqual(1L, view.Counters.PublicFileCount);
        Assert.AreEqual(0L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 500))!.Items.Count);

        view = store.Publish(view.Id, kernel.Apply(
            [Input(2, "Peer-B", @"Other\ELO - Track.flac", 900) with { Visibility = SearchResultVisibility.Locked }],
            2, false), "Live");
        Assert.AreEqual(matchesLaterFilename ? 0L : 1L, view.Counters.TopLevelItemCount);
        Assert.AreEqual(matchesLaterFilename ? 0 : 2,
            (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 500))!.Items.Count);
        view = store.Publish(view.Id, kernel.Apply(
            [Input(3, "Peer-C", @"Special\ELO - Track.flac", 900)], 3, true), "Complete");
        var group = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 100))!.Items.Single();
        Assert.AreEqual(3, group.ShareCount);
        Assert.AreEqual(2L, group.SelectableOptionCount);
        Assert.AreEqual(3L, group.OptionCount);
        Assert.AreEqual(3L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, hiddenRevision, default)?.GetFiles(null, 500))!.Items.Count);
        Assert.IsNull(store.GetSnapshot(view.Id, hiddenRevision, default)?.GetTrackGroupOptions(group.Ref, null, 500));
        var options = new List<SearchViewFile>();
        SearchViewFilePosition? after = null;
        do
        {
            var page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(group.Ref, after, 1))!;
            options.AddRange(page.Items);
            after = page.NextPosition;
        } while (after != null);
        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, options.Select(file => file.Input.Sequence).ToArray());
        Assert.AreEqual(1, options.Count(file => file.Input.Visibility == SearchResultVisibility.Locked));
        var selected = new List<SearchViewCommitItem>();
        foreach (var item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([options.Single(file => file.Input.Sequence == 1).Ref])) ?? [])) selected.Add(item);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual("Peer-A", selected[0].File!.Input.Username);

        var retained = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(group.Ref, null, 500))!.Items;
        CollectionAssert.AreEquivalent(options.ToArray(), retained.ToArray());
    }

    [TestMethod]
    public void TrackGroupSummaryAndAlternativesAreRevisionBoundAndPaged()
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.TrackGroups));
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.TrackGroups,
                new SongQuery { Artist = "ELO" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                MinGroupShares = 1,
                Relax = true,
            },
            trackPeerIdentities: false);
        view = store.Publish(view.Id, kernel.Apply(
                [Input(1, "Peer-A", @"ELO - Track.flac", 900)],
                1,
                false), "Live");
        long firstRevision = view.Revision;
        SearchViewTrackGroup first = (store.GetSnapshot(view.Id, firstRevision, default)?.GetTrackGroups(null, 10))!.Items.Single();
        Assert.AreEqual(1, first.ShareCount);

        view = store.Publish(view.Id, kernel.Apply(
                [Input(2, "Peer-B", @"ELO - Track.mp3", 192)],
                2,
                false), "Live");
        SearchViewTrackGroup current = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 10))!.Items.Single();
        Assert.AreEqual(first.Ref, current.Ref);
        Assert.AreEqual(2, current.ShareCount);
        Assert.AreEqual(1L, first.OptionCount);
        Assert.AreEqual(2L, current.OptionCount);
        var currentSnapshot = store.GetSnapshot(view.Id, view.Revision, default)!;
        Assert.AreSame(current, currentSnapshot.GetTrackGroups(null, 10).Items.Single());
        Assert.AreSame(current, currentSnapshot.ReadCommitItems(RefSelectionMode.Only,
            new HashSet<string>([current.Ref])).Single().TrackGroup);
        Assert.AreEqual(new SearchViewSelectionCounts(1, 0, 1),
            store.GetSnapshot(view.Id, view.Revision, default)?.CountTopLevelSelection(RefSelectionMode.AllExcept, new HashSet<string>()));
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, firstRevision, default)?.GetTrackGroupOptions(current.Ref, null, 10))!.Items.Count);
        var heldTrack = new Dictionary<string, string> { [current.Ref] = first.Representative.Ref };
        var resolvedTrack = store.GetSnapshot(view.Id, view.Revision, default)?.ResolveChosenOptions(heldTrack);
        Assert.AreEqual(first.Representative.Ref, resolvedTrack!.Tracks[current.Ref].Ref);
        Assert.AreEqual(0, resolvedTrack.Albums.Count);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, view.Revision, default)?.ResolveChosenOptions(new Dictionary<string, string> { [current.Ref] = "missing-option" }))!.Tracks.Count);
        SearchViewTrackGroupOptionPage firstPage = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(current.Ref, null, 1))!;
        Assert.IsNotNull(firstPage.NextPosition);
        SearchViewTrackGroupOptionPage secondPage = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(current.Ref, firstPage.NextPosition, 1))!;
        Assert.AreEqual(2, firstPage.Items.Concat(secondPage.Items).Count());

        string optionRef = firstPage.Items[0].Ref;
        var selectedOption = new List<SearchViewCommitItem>();
        foreach (SearchViewCommitItem item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([optionRef], StringComparer.Ordinal)) ?? []))
        {
            selectedOption.Add(item);
        }
        Assert.AreEqual(1, selectedOption.Count);
        Assert.AreEqual(optionRef, selectedOption[0].File!.Ref);
        CollectionAssert.Contains(selectedOption[0].ContainerRefs!.ToArray(), current.Ref);

        // One publication mixes new groups and another option for an existing
        // group. Counts, representatives and membership must remain independent.
        view = store.Publish(view.Id, kernel.Apply([
            Input(3, "Peer-C", @"ELO - Other Song.flac", 320),
            Input(4, "Peer-D", @"ELO - Last Song.flac", 256),
            Input(5, "Peer-E", @"ELO - Track.flac", 512),
        ], 5, true), "Complete");
        var roots = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 2))!;
        Assert.IsNotNull(roots.NextPosition);
        var tail = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(roots.NextPosition, 2))!;
        var all = roots.Items.Concat(tail.Items).ToArray();
        Assert.AreEqual(3, all.Length);
        Assert.AreEqual(3, all.Select(group => group.Ref).Distinct().Count());
        Assert.AreEqual(3, all.Single(group => group.Ref == first.Ref).ShareCount);
        foreach (var group in all)
        {
            var options = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(group.Ref, null, 10))!.Items;
            Assert.AreEqual(group.SelectableOptionCount, (long)options.Count);
            Assert.IsTrue(options.Any(file => file.Ref == group.Representative.Ref));
            string title = group.Query.Title;
            foreach (var file in options)
                Assert.AreEqual($"ELO - {title}.flac", file.Input.Filename.Replace(".mp3", ".flac"));
        }
        var historical = (store.GetSnapshot(view.Id, firstRevision, default)?.GetTrackGroups(null, 10))!.Items;
        Assert.AreEqual(1, historical.Count);
        Assert.AreEqual(first.Ref, historical[0].Ref);
        Assert.AreEqual(first.ShareCount, historical[0].ShareCount);
        Assert.AreEqual(first.Representative.Ref, historical[0].Representative.Ref);

        string RetainedPages() => JsonSerializer.Serialize(new
        {
            Current = store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 2),
            Historical = store.GetSnapshot(view.Id, firstRevision, default)?.GetTrackGroups(null, 10),
            Continuation = store.GetSnapshot(view.Id, firstPage.Revision.Revision, default)?.GetTrackGroupOptions(first.Ref, firstPage.NextPosition, 1),
        });
        string retained = RetainedPages();

        Assert.AreEqual(retained, RetainedPages(),
            "Rebuilding internal group keys must preserve both revisions and an already-issued option cursor.");
    }

    [TestMethod]
    public void AlbumGroupVisibilityBackfillsAndRestoresHistoricalDirectorySelections()
    {
        var store = new SearchViewCache();

        var view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.AlbumGroups));
        var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
        var settings = new SearchSettings
        {
            NecessaryCond = new(), PreferredCond = new(), NecessaryFolderCond = new(), MinGroupShares = 2,
        };
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AlbumGroups, AlbumQuery: query, FilterText: "Special"), settings);
        var first = Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900);
        var hidden = kernel.Apply([first], 1, false);
        Assert.AreEqual(0, hidden.ChangedFiles.Count);
        Assert.AreEqual(0, hidden.ChangedDirectories!.Count);

        view = store.Publish(view.Id, hidden, "Live");
        long hiddenRevision = view.Revision;
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, hiddenRevision, default)?.GetDirectories(null, 10))!.Items.Count);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, hiddenRevision, default)?.GetFiles(null, 500))!.Items.Count);

        view = store.Publish(view.Id, kernel.Apply(
            [Input(2, "Peer-B", @"Special\ELO\Time\01. Track.flac", 900)
                with { Visibility = SearchResultVisibility.Locked }], 2, false), "Live");
        long firstVisibleRevision = view.Revision;
        var firstGroup = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroups(null, 10))!.Items.Single();
        var initialOptions = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(firstGroup.Ref, null, 10))!.Items;
        var a = initialOptions.Single(folder => folder.Username == "Peer-A");
        var b = initialOptions.Single(folder => folder.Username == "Peer-B");
        var firstFile = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(a.Ref, null, 10))!.Items.Single();
        Assert.AreEqual(2L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(1L, view.Counters.ProjectedLockedFileCount);

        view = store.Publish(view.Id, kernel.Apply(
            [Input(3, "Peer-A", @"ELO\Time\02. Other.flac", 900)], 3, false), "Live");
        long gapRevision = view.Revision;
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, gapRevision, default)?.GetAlbumGroups(null, 10))!.Items.Count);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, gapRevision, default)?.GetDirectories(null, 10))!.Items.Count);
        Assert.IsNull(store.GetSnapshot(view.Id, gapRevision, default)?.GetDirectoryFiles(a.Ref, null, 10));

        view = store.Publish(view.Id, kernel.Apply(
            [Input(4, "Peer-C", @"Special\ELO\Time\01. Track.flac", 900)], 4, false), "Live");
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(b.Ref, null, 10))!.Items.Count,
            "A folder restored by another peer's arrival must retain its earlier locked child.");
        var restored = kernel.Apply([Input(5, "Peer-B", @"Special\ELO\Time\02. Other.flac", 900)], 5, true);
        CollectionAssert.AreEquivalent(new long[] { 3, 5 }, restored.ChangedFiles.Select(file => file.Input.Sequence).ToArray());
        view = store.Publish(view.Id, restored, "Complete");
        Assert.AreEqual(4L, view.Counters.ProjectedFileCount);
        Assert.AreEqual(1L, view.Counters.ProjectedLockedFileCount);
        Assert.AreEqual(2, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(a.Ref, null, 10))!.Items.Count);
        Assert.AreEqual(2, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(b.Ref, null, 10))!.Items.Count);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, firstVisibleRevision, default)?.GetDirectoryFiles(a.Ref, null, 10))!.Items.Count);
        Assert.IsNull(store.GetSnapshot(view.Id, gapRevision, default)?.GetDirectoryFiles(a.Ref, null, 10));
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, gapRevision, default)?.GetAlbumGroupOptions(firstGroup.Ref, null, 10))!.Items.Count);
        Assert.AreEqual(0, ((store.GetSnapshot(view.Id, gapRevision, default)?.ReadDirectoryFileRefs([firstFile.Ref]) ?? [])).Count);
        Assert.AreEqual(new SearchViewSelectionCounts(0, 0, 0),
            store.GetSnapshot(view.Id, gapRevision, default)?.CountObservedDirectoryFiles(a.Ref));
        var hiddenChildren = new List<SearchViewDirectoryFile>();
        foreach (var child in (store.GetSnapshot(view.Id, gapRevision, default)?.ReadPublicDirectoryFiles(a.Ref) ?? [])) hiddenChildren.Add(child);
        Assert.AreEqual(0, hiddenChildren.Count);
        var hiddenMemberships = new List<SearchViewDirectoryFileMembership>();
        foreach (var child in (store.GetSnapshot(view.Id, gapRevision, default)?.ReadDirectoryFileMemberships(a.Ref) ?? [])) hiddenMemberships.Add(child);
        Assert.AreEqual(0, hiddenMemberships.Count);
        var hiddenSelection = new List<SearchViewCommitItem>();
        foreach (var item in (store.GetSnapshot(view.Id, gapRevision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([firstFile.Ref])) ?? [])) hiddenSelection.Add(item);
        Assert.AreEqual(0, hiddenSelection.Count);
        var selected = new List<SearchViewCommitItem>();
        foreach (var item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([firstFile.Ref])) ?? [])) selected.Add(item);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(first, selected[0].File!.Input);

        Assert.AreEqual(1, (store.GetSnapshot(view.Id, firstVisibleRevision, default)?.GetDirectoryFiles(a.Ref, null, 10))!.Items.Count);
        Assert.AreEqual(2, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(a.Ref, null, 10))!.Items.Count);
        Assert.IsNull(store.GetSnapshot(view.Id, gapRevision, default)?.GetDirectoryFiles(a.Ref, null, 10));
    }

    [TestMethod]
    public void AlbumGroupSummaryAndDirectoryAlternativesAreRevisionBound()
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.AlbumGroups));
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AlbumGroups,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions(),
                MinGroupShares = 1,
            },
            trackPeerIdentities: false);
        view = store.Publish(view.Id, kernel.Apply(
                [Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900)],
                1,
                false), "Live");
        long firstRevision = view.Revision;
        SearchViewAlbumGroup first = (store.GetSnapshot(view.Id, firstRevision, default)?.GetAlbumGroups(null, 10))!.Items.Single();
        Assert.AreEqual(1, first.ShareCount);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, firstRevision, default)?.GetAlbumGroupOptions(first.Ref, null, 10))!.Items.Count);

        SearchViewKernelUpdate secondUpdate = kernel.Apply(
            [Input(2, "Peer-B", @"ELO\Time\01. Track.flac", 900)],
            2,
            false);
        view = store.Publish(view.Id, secondUpdate, "Live");
        SearchViewAlbumGroup current = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroups(null, 10))!.Items.Single();
        Assert.AreEqual(first.Ref, current.Ref);
        Assert.AreEqual(2, current.ShareCount);
        SearchViewAlbumGroupOptionPage pageOne = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(current.Ref, null, 1, initialFileLimit: 1))!;
        SearchViewAlbumGroupOptionPage pageTwo = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(current.Ref, pageOne.NextPosition, 1, initialFileLimit: 1))!;
        Assert.IsNotNull(pageOne.NextPosition);
        Assert.IsNull(pageTwo.NextPosition);
        CollectionAssert.AreEquivalent(new[] { "Peer-A", "Peer-B" },
            pageOne.Items.Concat(pageTwo.Items).Select(item => item.Username).ToArray());
        Assert.AreEqual(1, pageOne.DirectoryFilePreviews.Single().Value.Items.Count);
        Assert.AreEqual(1, pageTwo.DirectoryFilePreviews.Single().Value.Items.Count);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, firstRevision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Count);
        var heldAlbum = new Dictionary<string, string> { [current.Ref] = first.Representative.Ref };
        var resolvedAlbum = store.GetSnapshot(view.Id, view.Revision, default)?.ResolveChosenOptions(heldAlbum);
        Assert.AreEqual(first.Representative.Ref, resolvedAlbum!.Albums[current.Ref].Ref);
        Assert.AreEqual(0, resolvedAlbum.Tracks.Count);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, view.Revision, default)?.ResolveChosenOptions(new Dictionary<string, string> { [current.Ref] = "missing-option" }))!.Albums.Count);

        SearchViewDirectory selectedDirectory = pageOne.Items[0];
        SearchViewDirectoryFile selectedFile = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(selectedDirectory.Ref, null, 10))!.Items.Single();
        var selectedNested = new List<SearchViewCommitItem>();
        foreach (SearchViewCommitItem item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>(
                [selectedDirectory.Ref, selectedFile.Ref],
                StringComparer.Ordinal)) ?? []))
        {
            selectedNested.Add(item);
        }
        Assert.AreEqual(2, selectedNested.Count);
        SearchViewCommitItem directoryItem = selectedNested.Single(item =>
            item.Directory != null);
        CollectionAssert.Contains(directoryItem.ContainerRefs!.ToArray(), current.Ref);
        SearchViewCommitItem fileItem = selectedNested.Single(item =>
            item.File != null);
        CollectionAssert.Contains(fileItem.ContainerRefs!.ToArray(), current.Ref);
        CollectionAssert.Contains(fileItem.ContainerRefs!.ToArray(), selectedDirectory.Ref);

        // A later regrouping can remove one alternative without changing the
        // other. Earlier revisions must keep their original membership.
        SearchViewProjectedAlbumGroup group =
            secondUpdate.ChangedAlbumGroups!.Single();
        AlbumFolder retained = group.Options.Single(option => option.Username == "Peer-A");
        long secondRevision = view.Revision;
        view = store.Publish(view.Id, secondUpdate with
        {
            SourceRevision = 3,
            ConsumedSequence = 3,
            ChangedFiles = [],
            ChangedDirectories = [],
            ChangedAlbumGroups =
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
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Count);
        Assert.AreEqual(2, (store.GetSnapshot(view.Id, secondRevision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Count);
        var historicalContinuation = (store.GetSnapshot(view.Id, secondRevision, default)?.GetAlbumGroupOptions(current.Ref, pageOne.NextPosition, 1, initialFileLimit: 1))!;
        Assert.AreEqual(pageTwo.Items.Single().Ref, historicalContinuation.Items.Single().Ref);
        Assert.AreEqual(pageTwo.DirectoryFilePreviews.Single().Value.Items.Single().File.Ref,
            historicalContinuation.DirectoryFilePreviews.Single().Value.Items.Single().File.Ref);
        Assert.AreEqual(1, (store.GetSnapshot(view.Id, firstRevision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Count);

        SearchViewDirectory removed = pageOne.Items.Concat(pageTwo.Items)
            .Single(option => option.Username == "Peer-B");
        SearchViewDirectoryFile removedChild = (store.GetSnapshot(view.Id, secondRevision, default)?.GetDirectoryFiles(removed.Ref, null, 1))!.Items.Single();
        var removedRefs = new HashSet<string>([removed.Ref, removedChild.Ref], StringComparer.Ordinal);
        SearchViewCommitItem[] ReadRemovedSelection(long revision)
        {
            var items = new List<SearchViewCommitItem>();
            foreach (var item in (store.GetSnapshot(view.Id, revision, default)?.ReadCommitItems(RefSelectionMode.Only, removedRefs) ?? []))
                items.Add(item);
            return items.ToArray();
        }
        Assert.AreEqual(0, (ReadRemovedSelection(firstRevision)).Length);
        CollectionAssert.AreEquivalent(new[] { removed.Ref, removedChild.Ref },
            (ReadRemovedSelection(secondRevision)).Select(item => item.Ref).ToArray());
        Assert.AreEqual(0, (ReadRemovedSelection(view.Revision)).Length);

        // A later update can restore an existing alternative without republishing
        // its directory or files. The removed revision must stay removed.
        long removedRevision = view.Revision;
        view = store.Publish(view.Id, secondUpdate with
        {
            SourceRevision = 4,
            ConsumedSequence = 4,
            ChangedFiles = [],
            ChangedDirectories = [],
        }, "Live");
        Assert.AreEqual(2, (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Count);
        CollectionAssert.AreEquivalent(new[] { removed.Ref, removedChild.Ref },
            (ReadRemovedSelection(view.Revision)).Select(item => item.Ref).ToArray());
        Assert.AreEqual(0, (ReadRemovedSelection(removedRevision)).Length);

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
        void PublishGroups(int sourceRevision,
            SearchViewProjectedAlbumGroup[] changed, PeerDirectoryIdentity[] removedGroups, int total)
        {
            view = store.Publish(view.Id, secondUpdate with
            {
                Counters = secondUpdate.Counters with { TopLevelItemCount = total },
                SourceRevision = sourceRevision,
                ConsumedSequence = sourceRevision,
                ChangedFiles = [],
                ChangedDirectories = [],
                ChangedAlbumGroups = changed,
                RemovedAlbumGroups = removedGroups,
            }, "Live");
        }
        PublishGroups(5, [group with
        {
            Representative = retained, Options = [retained], ShareCount = 1, SelectableOptionCount = 1,
        }, other], [], 2);
        long bothRevision = view.Revision;
        var otherRef = (store.GetSnapshot(view.Id, bothRevision, default)?.GetAlbumGroups(null, 10))!
            .Items.Single(item => item.Ref != current.Ref).Ref;
        PublishGroups(6, [other with { Representative = retained, Options = [retained] }],
            [group.StableIdentity], 1);
        long groupRemovedRevision = view.Revision;
        Assert.AreEqual(otherRef, (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroups(null, 10))!.Items.Single().Ref);
        Assert.AreEqual("Peer-A", (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(otherRef, null, 10))!.Items.Single().Username);
        Assert.AreEqual("Peer-B", (store.GetSnapshot(view.Id, bothRevision, default)?.GetAlbumGroupOptions(otherRef, null, 10))!.Items.Single().Username);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, groupRemovedRevision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Count);
        PublishGroups(7, [other with { StableIdentity = group.StableIdentity, Index = group.Index }], [], 2);
        Assert.AreEqual("Peer-B", (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Single().Username);
        Assert.AreEqual(0, (store.GetSnapshot(view.Id, groupRemovedRevision, default)?.GetAlbumGroupOptions(current.Ref, null, 10))!.Items.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LargeDirectoryPagesEveryChildAndResolvesExplicitChildSelectionWithoutACollectionLimit(bool bare)
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories));
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
        view = store.Publish(view.Id, kernel.Apply(inputs, childCount, false), "Live");
        SearchViewDirectoryPage parentPage = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 1, initialFileLimit: 24))!;
        SearchViewDirectory parent = parentPage.Items.Single();
        var preview = parentPage.DirectoryFilePreviews[parent.Ref];
        Assert.AreEqual(24, preview.Items.Count);
        Assert.IsNotNull(preview.NextPosition);

        var children = new List<SearchViewDirectoryFile>();
        SearchViewDirectoryFilePosition? position = null;
        do
        {
            SearchViewDirectoryFilePage page = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(parent.Ref, position, pageSize))!;
            Assert.IsTrue(page.Items.Count <= pageSize);
            children.AddRange(page.Items);
            position = page.NextPosition;
        }
        while (position != null);
        Assert.AreEqual(childCount, children.Count);
        Assert.AreEqual(childCount, (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(parent.Ref, null, int.MaxValue))!.Items.Count);

        var selected = new List<SearchViewCommitItem>();
        foreach (SearchViewCommitItem item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>(children.Select(child => child.Ref), StringComparer.Ordinal)) ?? []))
        {
            selected.Add(item);
        }
        Assert.AreEqual(childCount, selected.Count);
        Assert.IsTrue(selected.All(item => item.File != null && item.ParentRef == parent.Ref));
    }

    [TestMethod]
    [TestCategory("Load")]
    [DataRow(SearchViewProjectionKind.Files)]
    [DataRow(SearchViewProjectionKind.GenericDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.TrackGroups)]
    [DataRow(SearchViewProjectionKind.AlbumGroups)]
    public void TenThousandResultSelections_ResolveExactRootsAndChildren(
        SearchViewProjectionKind kind)
    {
        var store = new SearchViewCache();

        SearchViewInfo view = store.Create(Guid.NewGuid(), Definition(kind));
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            kind, new SongQuery { Artist = "Fixture Artist" },
            new AlbumQuery { Artist = "Fixture Artist", Album = "Fixture" },
            IncludeFullResults: true), new SearchSettings
            {
                NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions(), MinGroupShares = 1,
            }, retainProjectedRows: false, trackPeerIdentities: false);
        var inputs = Enumerable.Range(0, 100).SelectMany(peer =>
            Enumerable.Range(0, 10).SelectMany(album => Enumerable.Range(0, 10)
                .Select(track => Input(peer * 100 + album * 10 + track + 1,
                    $"Peer-{peer:D3}",
                    $@"Music\Fixture Artist\Fixture Album-{album:D2}\{track:D2} - Fixture Track-{track:D2}.flac", 320)
                    with { Length = 200 + album * 60 + track * 5 }))).ToArray();
        var projectionTime = TimeSpan.Zero;
        var publicationTime = TimeSpan.Zero;
        // Exercise hundreds of small publications as well as normal batches;
        // retained snapshots must share their unchanged data.
        foreach (SearchProjectionInput[] batch in inputs.Chunk(kind == SearchViewProjectionKind.Files ? 17 : 200))
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            SearchViewKernelUpdate update = kernel.Apply(batch, checked((int)batch[^1].Sequence), false);
            projectionTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            view = store.Publish(view.Id, update, "Live");
            publicationTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
        view = store.Publish(view.Id, kernel.Apply([], 10_000, true), "Complete");
        Assert.AreEqual(10_000L, view.Counters.ProjectedFileCount);
        Assert.IsTrue(view.IsComplete);
        Console.WriteLine($"10k {kind}: projection={projectionTime.TotalMilliseconds:F1} ms, publication={publicationTime.TotalMilliseconds:F1} ms.");

        if (kind == SearchViewProjectionKind.Files)
        {
            var refinement = new SearchViewKernel(new FileSearchProjection(new SongQuery { Artist = "Fixture Artist" }, IncludeFullResults: true),
                new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions { MinBitrate = 512 } });
            var refined = store.Create(view.SourceJobId, Definition(kind));
            var update = refinement.Apply(inputs, 10_000, true);
            var elapsed = Stopwatch.StartNew();
            refined = store.Publish(refined.Id, update, "Complete");
            Console.WriteLine($"10k existing source, refined file View publication: {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
            Assert.AreEqual(10_000L, refined.Counters.ProjectedFileCount);
            Assert.AreEqual(500, (store.GetSnapshot(refined.Id, refined.Revision, default)?.GetFiles(null, 500))!.Items.Count);

        }

        var targets = new List<(string Kind, string Ref, string? Parent)>();
        string rootRef;
        if (kind == SearchViewProjectionKind.Files)
        {
            rootRef = (store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 1))!.Items.Single().Ref;
            targets.Add(("File", rootRef, null));
        }
        else if (kind is SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories)
        {
            var folder = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 1))!.Items.Single();
            rootRef = folder.Ref;
            var file = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(folder.Ref, null, 1))!.Items.Single();
            targets.Add(("Directory", rootRef, null));
            targets.Add(("DirectoryFile", file.Ref, rootRef));
        }
        else if (kind == SearchViewProjectionKind.TrackGroups)
        {
            var group = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroups(null, 1))!.Items.Single();
            rootRef = group.Ref;
            var file = (store.GetSnapshot(view.Id, view.Revision, default)?.GetTrackGroupOptions(group.Ref, null, 1))!.Items.Single();
            targets.Add(("TrackGroup", rootRef, null));
            targets.Add(("TrackGroupFile", file.Ref, rootRef));
        }
        else
        {
            var group = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroups(null, 1))!.Items.Single();
            rootRef = group.Ref;
            var folder = (store.GetSnapshot(view.Id, view.Revision, default)?.GetAlbumGroupOptions(group.Ref, null, 1))!.Items.Single();
            var file = (store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(folder.Ref, null, 1))!.Items.Single();
            targets.Add(("AlbumGroup", rootRef, null));
            targets.Add(("AlbumGroupDirectory", folder.Ref, rootRef));
            targets.Add(("AlbumGroupFile", file.Ref, folder.Ref));
        }
        foreach (var target in targets)
        {
            // Explicit choices must look up their identities, regardless of View size.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var selected = new List<SearchViewCommitItem>();
            foreach (var item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>([target.Ref], StringComparer.Ordinal)) ?? []))
                selected.Add(item);
            elapsed.Stop();
            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual(target.Ref, selected[0].Ref);
            if (target.Parent != null)
                Assert.IsTrue(selected[0].ParentRef == target.Parent
                    || selected[0].ContainerRefs?.Contains(target.Parent) == true);
            Console.WriteLine($"10k selection {kind}/{target.Kind}: {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
        }
        long remaining = 0;
        foreach (var item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.AllExcept, new HashSet<string>([rootRef], StringComparer.Ordinal)) ?? []))
        {
            Assert.AreNotEqual(rootRef, item.Ref);
            remaining++;
        }
        Assert.AreEqual(view.Counters.TopLevelItemCount - 1, remaining);


    }

    private static SearchViewDefinition Definition(SearchViewProjectionKind kind,
        SearchViewSortOrder order = SearchViewSortOrder.Relevance)
        => new(SearchDefinition.Create(new SearchJob("test"), new SearchSettings()), new(kind, SortOrder: order));

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
            var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
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
            global::Tests.TestDatabase.ClearPool(factory);
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static bool HasUnappliedObservation(
        SearchViewCache store, Guid viewId)
    {
        foreach (SearchDirectoryObservationTarget _ in
            store.ReadUnappliedDirectoryObservations(viewId))
            return true;
        return false;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
    [TestMethod]
    [TestCategory("Load")]
    [DataRow(false)]
    [DataRow(true)]
    public void HundredThousandDirectoryFiles_PageCurrentAndOlderMembershipsPromptly(bool bare)
    {
        var store = new SearchViewCache();
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(SearchViewProjectionKind.GenericDirectories, new SongQuery { Title = "Track" }),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions() },
            retainProjectedRows: false, trackPeerIdentities: false);
        SearchProjectionInput Input(int index, bool late) => new(
            index, late ? 2 : 1, "peer", late ? 99000 : 1000,
            (bare ? "" : @"Share\Large\") + $"Track-{(late ? "A" : "Z")}-{index:D6}.txt", 128,
            null, null, null, null, ".txt", 1_000_000, true, [], DateTimeOffset.UnixEpoch, 0);
        var view = store.Create(Guid.NewGuid(), Definition(SearchViewProjectionKind.GenericDirectories));
        view = store.Publish(view.Id, kernel.Apply(Enumerable.Range(1, 1000).Select(index => Input(index, false)).ToArray(), 1, false), "Live");
        long oldRevision = view.Revision;
        var pinnedFiles = (store.GetSnapshot(view.Id, oldRevision, default)?.GetFiles(null, 500))!;
        // Projection/publication are synchronous; exclude allocations from parallel tests.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long? writtenBefore = ProcessWriteBytes();
        var publication = Stopwatch.StartNew();
        // Exercise sharing across repeated publications as the folder grows.
        for (int start = 1001; start <= 100000; start += 2000)
        {
            int count = Math.Min(2000, 100001 - start);
            bool complete = start + count == 100001;
            view = store.Publish(view.Id, kernel.Apply(Enumerable.Range(start, count).Select(index => Input(index, true)), 2, complete), complete ? "Complete" : "Live");
            Assert.AreEqual(start + count - 1L, view.Counters.ProjectedFileCount);
        }
        publication.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long? written = ProcessWriteBytes() - writtenBefore;
        Console.WriteLine($"Final 99k observations: input construction + generic folder projection + publication allocated "
            + $"{allocated / 1048576.0:F1} MiB; process write_bytes delta {written?.ToString() ?? "unavailable"}.");
        Assert.IsTrue(allocated < 500_000_000L,
            "Projection and publication of this 99k-file arrival workload must stay below 500 MB allocated.");
        Assert.AreEqual(100000L, view.Counters.ProjectedFileCount);
        var readTimes = new Dictionary<string, double>();
        T Measure<T>(string name, Func<T> read)
        {
            var stopwatch = Stopwatch.StartNew();
            T result = read();
            readTimes.Add(name, stopwatch.Elapsed.TotalMilliseconds);
            return result;
        }
        var folders = (Measure("folder", () => store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectories(null, 100, initialFileLimit: 24)))!;
        string directoryRef = folders.Items.Single().Ref;
        Assert.AreEqual(100000L, folders.Items.Single().PublicMatchingFileCount);
        Assert.AreEqual(24, folders.DirectoryFilePreviews[directoryRef].Items.Count);
        Assert.IsNotNull(folders.DirectoryFilePreviews[directoryRef].NextPosition);
        var first = (Measure("first", () => store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(directoryRef, null, 500)))!;
        var second = (Measure("second", () => store.GetSnapshot(view.Id, view.Revision, default)?.GetDirectoryFiles(directoryRef, first.NextPosition, 500)))!;
        var old = (Measure("older-revision", () => store.GetSnapshot(view.Id, oldRevision, default)?.GetDirectoryFiles(directoryRef, null, 500)))!;
        var oldSecond = (Measure("older-continuation", () => store.GetSnapshot(view.Id, oldRevision, default)?.GetDirectoryFiles(directoryRef, old.NextPosition, 500)))!;
        CollectionAssert.AreEqual(Enumerable.Range(1001, 1000).Select(index => Input(index, true).Filename).ToArray(),
            first.Items.Concat(second.Items).Select(item => item.File.Input.Filename).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(1, 1000).Select(index => Input(index, false).Filename).ToArray(),
            old.Items.Concat(oldSecond.Items).Select(item => item.File.Input.Filename).ToArray());
        Assert.AreEqual(1000L, old.DirectorySummary.PublicMatchingFileCount);
        Assert.AreEqual(100000L, first.DirectorySummary.PublicMatchingFileCount);
        Assert.IsNotNull(first.NextPosition);
        Assert.IsNotNull(second.NextPosition);
        Assert.IsNotNull(old.NextPosition);
        Assert.IsNull(oldSecond.NextPosition);
        // Reuse the already ingested file rows to exercise the flat-page SQL;
        // this qualifies the storage query, not a Files-kind API response.
        var flatFirst = (Measure("projected-files", () => store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(null, 500)))!;
        var flatNext = (Measure("projected-files-next", () => store.GetSnapshot(view.Id, view.Revision, default)?.GetFiles(flatFirst.NextPosition, 500)))!;
        var oldFlatNext = (Measure("projected-files-old-next", () => store.GetSnapshot(view.Id, oldRevision, default)?.GetFiles(pinnedFiles.NextPosition, 500)))!;
        Assert.AreEqual(500, flatFirst.Items.Count);
        Assert.AreEqual(500, flatNext.Items.Count);
        Assert.AreEqual(1000, flatFirst.Items.Concat(flatNext.Items).Select(item => item.Ref).Distinct().Count());
        Assert.IsNotNull(flatNext.NextPosition);
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 1000).Select(index => (long)index).ToArray(),
            pinnedFiles.Items.Concat(oldFlatNext.Items).Select(item => item.Input.Sequence).ToArray());
        Assert.IsNull(oldFlatNext.NextPosition);
        var selectedRefs = new HashSet<string>([second.Items.Last().Ref], StringComparer.Ordinal);
        var membership = Measure("selected-membership", () => (store.GetSnapshot(view.Id, view.Revision, default)?.ReadDirectoryFileRefs(selectedRefs) ?? []));
        Assert.AreEqual(1, membership.Count);
        Assert.AreEqual(directoryRef, membership.Single().DirectoryRef);
        Assert.AreEqual(selectedRefs.Single(), membership.Single().Ref);
        Assert.AreEqual(SearchResultVisibility.Public, membership.Single().Visibility);
        Assert.AreEqual(0, (Measure("older-selected-membership", () => (store.GetSnapshot(view.Id, oldRevision, default)?.ReadDirectoryFileRefs(selectedRefs) ?? []))).Count);
        List<SearchViewCommitItem> ReadSelected()
        {
            var items = new List<SearchViewCommitItem>();
            foreach (var item in (store.GetSnapshot(view.Id, view.Revision, default)?.ReadCommitItems(RefSelectionMode.Only, selectedRefs) ?? []))
                items.Add(item);
            return items;
        }
        var selected = Measure("selected-plan", ReadSelected);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(directoryRef, selected.Single().ParentRef);
        Assert.AreEqual(JsonSerializer.Serialize(second.Items.Last().File.Input),
            JsonSerializer.Serialize(selected.Single().File!.Input));
        Console.WriteLine($"bare={bare} 100k directory publication: {publication.Elapsed.TotalMilliseconds:F1} ms; reads: " + string.Join(", ",
            readTimes.Select(pair => $"{pair.Key}={pair.Value:F1} ms")));
        Assert.IsTrue(readTimes.Values.Count(ms => ms < 100) >= readTimes.Count - 1,
            "Most bounded directory reads should finish below 100 ms.");
        Assert.IsTrue(readTimes.Where(pair => pair.Key.Contains("selected", StringComparison.Ordinal))
                .All(pair => pair.Value < 100),
            "Selecting one exact file must not scan all directory members.");
        Assert.IsTrue(readTimes.Values.All(ms => ms < 1000),
            "A bounded directory page must not take seconds.");

        static long? ProcessWriteBytes()
            => File.Exists("/proc/self/io")
                ? File.ReadLines("/proc/self/io").Where(line => line.StartsWith("write_bytes:", StringComparison.Ordinal))
                    .Select(line => (long?)long.Parse(line.AsSpan("write_bytes:".Length).Trim())).Single()
                : null;
    }


}
