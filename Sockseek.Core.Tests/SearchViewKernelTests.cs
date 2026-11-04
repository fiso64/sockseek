using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Unit;

[TestClass]
public sealed class SearchViewKernelTests
{
    [TestMethod]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AggregateAlbums)]
    public void FreeTextAlbumMinimumCountsObservedTracksAcrossPublications(SearchViewProjectionKind kind)
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions { MinTrackCount = 2 }, MinSharesAggregate = 1,
        };
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(kind,
            AlbumQuery: new AlbumQuery { SearchHint = "Track" }), settings);
        var first = Input(1, "Peer", @"Share\Album\01 Track.flac", 900, SearchResultVisibility.Public);
        var second = Input(2, "Peer", @"Share\Album\02 Track.flac", 900, SearchResultVisibility.Public);
        Assert.AreEqual(0L, kernel.Apply([first], 1, false).Counters.ProjectedFileCount);
        Assert.AreEqual(2L, kernel.Apply([second], 2, false).Counters.ProjectedFileCount);
        Assert.AreEqual(2L, kernel.Apply([], 3, true).Counters.ProjectedFileCount);
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AggregateAlbums)]
    public void AlbumGroupingAcceptsSmallResponsesWithLargePeerFileCounts(SearchViewProjectionKind kind)
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(), MinSharesAggregate = 1,
        };
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(kind,
            new SongQuery(), new AlbumQuery { Artist = "Artist" }), settings);
        var inputs = new[]
        {
            Input(1, "Peer", @"Share\Album A\Artist - Track.flac", 900, SearchResultVisibility.Public)
                with { ResponseFileCount = int.MaxValue, Length = 200 },
            Input(2, "Peer", @"Share\Album B\Artist - Track.flac", 900, SearchResultVisibility.Locked)
                with { ResponseFileCount = int.MaxValue, Length = 300 },
        };
        var update = kernel.Apply(inputs, 2, true);
        Assert.AreEqual(2L, update.Counters.ProjectedFileCount);
        Assert.AreEqual(1L, update.Counters.ProjectedPublicFileCount);
        Assert.AreEqual(1L, update.Counters.ProjectedLockedFileCount);
        Assert.IsTrue(update.ChangedFiles.All(file => file.Input.ResponseFileCount == int.MaxValue));
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.GenericDirectories, false)]
    [DataRow(SearchViewProjectionKind.GenericDirectories, true)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories, false)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories, true)]
    public void FolderProjectionsKeepBareFilesSeparateFromAdvertisedAlbumFolders(SearchViewProjectionKind kind, bool forwardSlash)
    {
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(kind,
            new SongQuery { Artist = "Artist" }, new AlbumQuery { Artist = "Artist", Album = "Album" },
            IncludeFullResults: true), new SearchSettings
        {
            NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
        });
        var inputs = new[]
        {
            Input(1, "Peer", @"Share\Album\Artist - Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer", "Artist - Bare.flac", 900, SearchResultVisibility.Public),
            Input(3, "Peer", "Artist - Locked.flac", 900, SearchResultVisibility.Locked),
        };
        if (forwardSlash) inputs[0] = inputs[0] with { Filename = inputs[0].Filename.Replace('\\', '/') };
        kernel.Apply(inputs[..1], 1, false);
        kernel.Apply(inputs[1..], 3, true);
        var snapshot = kernel.Snapshot();
        Assert.AreEqual(2L, snapshot.Counters.PublicFileCount);
        Assert.AreEqual(1L, snapshot.Counters.LockedFileCount);
        bool album = kind == SearchViewProjectionKind.AlbumDirectories;
        Assert.AreEqual(album ? 1L : 3L, snapshot.Counters.ProjectedFileCount);
        Assert.AreEqual(album ? 1L : 2L, snapshot.Counters.TopLevelItemCount);
        if (!album)
        {
            var bare = snapshot.Directories!.Single(item => item.Directory.FolderPath.Length == 0);
            Assert.AreEqual(1L, bare.PublicMatchingFileCount);
            Assert.AreEqual(1L, bare.LockedMatchingFileCount);
        }
        Assert.AreEqual(forwardSlash ? "Share/Album" : @"Share\Album", snapshot.Directories!
            .Single(item => item.Directory.FolderPath.Length != 0).Directory.FolderPath);
        var rebuilt = new SearchViewKernel(new SearchViewProjectionDefinition(kind,
            new SongQuery { Artist = "Artist" }, new AlbumQuery { Artist = "Artist", Album = "Album" },
            IncludeFullResults: true), new SearchSettings
        {
            NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
        });
        rebuilt.Apply(inputs, 3, true);
        Assert.AreEqual(snapshot.Counters, rebuilt.Snapshot().Counters);
    }

    [TestMethod]
    public void TextFilterCountsRawObservationsButProjectsOnlyMatchingFilesAndDirectories()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
        };
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer", @"Share\One\Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer", @"Share\Two\Target.flac", 900, SearchResultVisibility.Public),
        ];
        var files = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.Files,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true,
                FilterText: "target"),
            settings);
        files.Apply(inputs, 2, false);
        Assert.AreEqual(2L, files.Snapshot().Counters.PublicFileCount);
        Assert.AreEqual(1L, files.Snapshot().Counters.ProjectedFileCount);
        Assert.AreEqual(inputs[1].Filename, files.Snapshot().Files.Single().Input.Filename);

        var directories = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true,
                FilterText: "two"),
            settings);
        directories.Apply(inputs, 2, false);
        Assert.AreEqual(2L, directories.Snapshot().Counters.PublicFileCount);
        Assert.AreEqual(1L, directories.Snapshot().Counters.ProjectedFileCount);
        Assert.AreEqual(@"Share\Two", directories.Snapshot().Directories!.Single().Directory.FolderPath);
    }

    [TestMethod]
    public void AlbumTextMatchRevealsTheWholeFolderWhenALaterChildMatches()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AlbumDirectories,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" },
            FilterText: "Target");
        var kernel = new SearchViewKernel(projection, settings);
        SearchProjectionInput first = Input(
            1, "Peer", @"ELO\Time\01. Twilight.flac", 900, SearchResultVisibility.Public);
        SearchProjectionInput second = Input(
            2, "Peer", @"ELO\Time\02. Target.flac", 900, SearchResultVisibility.Public);
        SearchViewKernelUpdate hidden = kernel.Apply([first], 1, false);
        Assert.AreEqual(0, hidden.ChangedDirectories!.Count);
        Assert.AreEqual(0L, hidden.Counters.ProjectedFileCount);
        Assert.AreEqual(0, hidden.ChangedFiles.Count);
        SearchViewKernelUpdate visible = kernel.Apply([second], 2, false);
        SearchViewProjectedDirectory folder = visible.ChangedDirectories!.Single();
        Assert.AreEqual(2L, folder.PublicMatchingFileCount);
        Assert.AreEqual(2, folder.NewChildren.Count);
        CollectionAssert.AreEquivalent(new[] { first, second }, visible.ChangedFiles.Select(file => file.Input).ToArray());
        Assert.AreEqual(2L, visible.Counters.ProjectedFileCount);

        var rebuilt = new SearchViewKernel(projection, settings);
        rebuilt.Apply([first, second], 2, false);
        AssertDirectoriesEqual(rebuilt.Snapshot().Directories!, kernel.Snapshot().Directories!);
        Assert.AreEqual(rebuilt.Snapshot().Counters, kernel.Snapshot().Counters);
    }

    [TestMethod]
    public void AggregateTextMatchRevealsEveryOptionInTheMatchingGroup()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            MinSharesAggregate = 1,
            Relax = true,
        };
        var tracks = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateTracks,
                new SongQuery { Artist = "ELO" },
                FilterText: "Rare"),
            settings);
        tracks.Apply(
            [Input(1, "Peer-A", @"ELO - Track.flac", 900, SearchResultVisibility.Public)],
            1, false);
        Assert.AreEqual(0L, tracks.Snapshot().Counters.TopLevelItemCount);
        SearchViewKernelUpdate revealed = tracks.Apply(
            [Input(2, "Peer-B", @"Rare\ELO - Track.flac", 900, SearchResultVisibility.Public)],
            2, false);
        Assert.AreEqual(1, revealed.ChangedAggregateTrackGroups!.Count);
        Assert.AreEqual(2, revealed.ChangedAggregateTrackGroups[0].NewOptions.Count);
        Assert.AreEqual(2L, revealed.Counters.ProjectedFileCount);
        Assert.AreEqual(2L, revealed.Counters.SelectableOptionCount);

        SearchViewKernelUpdate appended = tracks.Apply(
            [Input(3, "Peer-C", @"ELO - Track.mp3", 192, SearchResultVisibility.Locked)],
            3, false);
        Assert.AreEqual(1, appended.ChangedAggregateTrackGroups!.Count);
        Assert.AreEqual(1, appended.ChangedAggregateTrackGroups[0].NewOptions.Count);
        Assert.AreEqual(3L, appended.Counters.ProjectedFileCount);
        Assert.AreEqual(2L, appended.Counters.SelectableOptionCount);
        Assert.AreEqual(1L, appended.Counters.TopLevelItemCount);

        var rebuilt = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateTracks,
                new SongQuery { Artist = "ELO" },
                FilterText: "Rare"),
            settings);
        rebuilt.Apply(
            [
                Input(1, "Peer-A", @"ELO - Track.flac", 900, SearchResultVisibility.Public),
                Input(2, "Peer-B", @"Rare\ELO - Track.flac", 900, SearchResultVisibility.Public),
                Input(3, "Peer-C", @"ELO - Track.mp3", 192, SearchResultVisibility.Locked),
            ],
            3, false);
        Assert.AreEqual(rebuilt.Snapshot().Counters, tracks.Snapshot().Counters);
    }

    [TestMethod]
    public void AggregateAlbumTextMatchKeepsEveryDirectoryOption()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
            MinSharesAggregate = 1,
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateAlbums,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" },
            FilterText: "Special");
        var kernel = new SearchViewKernel(projection, settings);
        kernel.Apply(
            [Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public)],
            1, false);
        Assert.AreEqual(0L, kernel.Snapshot().Counters.TopLevelItemCount);
        SearchViewKernelUpdate revealed = kernel.Apply(
            [Input(2, "Peer-B", @"Special\ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public)],
            2, false);
        SearchViewProjectedAggregateAlbumGroup group = revealed.ChangedAggregateAlbumGroups!.Single();
        Assert.AreEqual(2, group.Options.Count);
        Assert.AreEqual(2L, revealed.Counters.ProjectedFileCount);
        Assert.AreEqual(1L, revealed.Counters.TopLevelItemCount);
    }

    [TestMethod]
    [TestCategory("Load")]
    public void AggregateTrackRefinementRetainsOptionsWithoutRankingEveryHiddenGroup()
    {
        const int count = 100_000;
        const int titles = 10_000;
        var inputs = Enumerable.Range(0, count).Select(index => new SearchProjectionInput(
            index + 1L, 1, $"fixture-peer-{index / titles:D3}", titles,
            $@"Music\Fixture Artist\Fixture Album-{index % titles / 10:D2}\{index % 10:D2} - Fixture Track-{index % titles:D2}.flac",
            8_000_000 + index % 10 * 100_000, 320, null, null, 200 + index % 10 * 5,
            ".flac", 1_000_000, true, null, DateTimeOffset.UnixEpoch)).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateTracks, new SongQuery { Artist = "Fixture Artist" },
            FilterText: "Track-9999"), new SearchSettings(), retainProjectedRows: false,
            trackPeerIdentities: false);
        SearchViewKernelUpdate? final = null;
        foreach (var batch in inputs.Chunk(titles))
            final = kernel.Apply(batch, 1, batch[^1].Sequence == count);
        var snapshot = kernel.Snapshot();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual((long)count, final!.Counters.PublicFileCount);
        Assert.AreEqual(10L, final.Counters.ProjectedFileCount);
        Assert.AreEqual(1L, final.Counters.TopLevelItemCount);
        var group = snapshot.AggregateTrackGroups!.Single();
        Assert.AreEqual(10, group.ShareCount);
        Assert.AreEqual(10, group.NewOptions.Count);
        Assert.IsTrue(group.NewOptions.All(option => option.Input.Filename.Contains("Track-9999")));
        Assert.AreEqual(final.Counters, snapshot.Counters);
        Assert.IsLessThan(128_000_000L, allocated,
            "A one-group refinement must not build ranking/condition records for the other 9,999 groups.");
    }

    [TestMethod]
    [TestCategory("Load")]
    [DataRow(false)]
    [DataRow(true)]
    public void AggregateTrackDurationLookupScalesWhenOneTitleHasManyDistinctDurations(bool descending)
    {
        const int groupCount = 20_000;
        var inputs = Enumerable.Range(0, groupCount).Select(index => Input(index + 1, $"peer-{index}",
            @"Music\ELO - Track.flac", 900, SearchResultVisibility.Public) with
            { Length = 200 + (descending ? groupCount - index - 1 : index) * 10 }).ToArray();
        var secondPeers = inputs.Select(input => input with
            { Sequence = input.Sequence + groupCount, Username = "other-" + input.Username }).ToArray();
        var projector = new IncrementalAggregateTrackProjector(new SongQuery { Artist = "ELO", Title = "Track" },
            new SearchSettings { NecessaryCond = new(), PreferredCond = new(), MinSharesAggregate = 2 });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(0, projector.AddRangeForSearchView(inputs).Count);
        var groups = projector.AddRangeForSearchView(secondPeers);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        watch.Stop();
        Assert.AreEqual(groupCount, groups.Count);
        Assert.IsTrue(groups.All(group => group.ShareCount == 2 && group.SelectableOptionCount == 2));
        CollectionAssert.AreEquivalent(inputs.Select(input => input.Length!.Value).ToArray(),
            groups.Select(group => group.Query.Length).ToArray());
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed,
            "Same-title durations must not form a quadratic collision chain.");
        Assert.IsLessThan(256_000_000L, allocated, "Admission must not repeatedly copy earlier duration groups.");
    }

    [TestMethod]
    [DataRow(-2)]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(int.MaxValue)]
    public void AggregateTrackDurationGroupingPreservesGreedyRootsAcrossBatches(int tolerance)
    {
        var query = new SongQuery { Artist = "ELO", Title = "Track" };
        var settings = new SearchSettings
        {
            NecessaryCond = new(), PreferredCond = new(), MinSharesAggregate = 1,
            Relax = true, AggregateLengthTol = tolerance,
        };
        int?[] lengths = [417, 423, 420, 414, 426, null, -1, 0, 3, 6,
            int.MinValue, int.MinValue + 3, int.MaxValue, int.MaxValue - 3, 420, 417, -2, -4];
        var inputs = lengths.Select((length, index) => Input(index + 1, $"peer-{index:D2}",
            index % 2 == 0 ? @"Music\ELO - Track.flac" : @"Music\elo - track.flac",
            900, SearchResultVisibility.Public) with { Length = length }).ToArray();
        SongQuery Key(SearchProjectionInput input) => new(Searcher.InferSongQuery(input.Filename, query))
            { Length = input.Length ?? -1 };
        string Members(IEnumerable<string> peers) => string.Join(",", peers.Order(StringComparer.Ordinal));

        // Keep the previous greedy grouping semantics, including ambiguous
        // overlaps: 420 joins the newer 423 root rather than the older 417 root.
        var expected = inputs.GroupBy(Key, new SongQueryComparer(ignoreCase: true, tolerance))
            .Select(group => $"{group.Key.Length}:{Members(group.Select(input => input.Username))}").ToArray();
        if (tolerance == 3) Assert.AreEqual("423:peer-01,peer-02,peer-04,peer-14", expected[1]);
        foreach (int batchSize in new[] { 1, 7, inputs.Length })
        foreach (bool readView in new[] { false, true })
        {
            var projector = new IncrementalAggregateTrackProjector(query, settings);
            for (int replay = 0; replay < 2; replay++)
            {
                foreach (var batch in inputs.Chunk(batchSize))
                {
                    if (readView) projector.AddRangeForSearchView(batch);
                    else projector.AddRange(batch);
                }
                var groups = projector.SnapshotForSearchView();
                CollectionAssert.AreEqual(expected, groups.Select(group =>
                    $"{group.StableQuery!.Length}:{Members(group.NewOptions.Select(option => option.Input.Username))}").ToArray());
                Assert.AreEqual((long)inputs.Length, groups.Sum(group => group.SelectableOptionCount));
                CollectionAssert.AreEquivalent(inputs.Select(input => input.Username).ToArray(),
                    projector.Snapshot().SelectMany(song => song.Candidates!).Select(file => file.Username).ToArray());
                projector.Clear();
            }
        }
    }

    [TestMethod]
    [DataRow(int.MinValue, 0)]
    [DataRow(int.MinValue, int.MaxValue)]
    public void AggregateTrackReportedDurationExtremesKeepEverySourceInItsCorrectGroup(
        int firstLength, int secondLength)
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            MinSharesAggregate = 1,
            Relax = true,
            AggregateLengthTol = 3,
        };
        var definition = new SearchViewProjectionDefinition(SearchViewProjectionKind.AggregateTracks,
            new SongQuery { Artist = "ELO" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "first", @"ELO - Track.flac", 900, SearchResultVisibility.Public) with { Length = firstLength },
            Input(2, "second", @"ELO - Track.flac", 900, SearchResultVisibility.Public) with { Length = secondLength },
        ];
        var incremental = new SearchViewKernel(definition, settings);
        incremental.Apply([inputs[0]], 1, false);
        incremental.Apply([inputs[1]], 2, false);
        var rebuilt = new SearchViewKernel(definition, settings);
        rebuilt.Apply(inputs, 2, false);
        AssertEquivalent(rebuilt.Snapshot(), incremental.Snapshot());
        Assert.AreEqual(2L, incremental.Snapshot().Counters.TopLevelItemCount);
        Assert.AreEqual(2L, incremental.Snapshot().Counters.ProjectedFileCount);
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, incremental.Snapshot().AggregateTrackGroups!
            .SelectMany(group => group.NewOptions).Select(file => file.Input.Username).ToArray());
        CollectionAssert.AreEquivalent(new[] { firstLength, secondLength }, incremental.Snapshot().AggregateTrackGroups!
            .SelectMany(group => group.NewOptions).Select(file => file.Input.Length!.Value).ToArray());
        incremental.Apply([], 3, true);
        Assert.AreEqual(2L, incremental.Snapshot().Counters.TopLevelItemCount);
        Assert.AreEqual(2L, incremental.Snapshot().Counters.ProjectedFileCount);
    }

    [TestMethod]
    [DataRow(3, null, null, null, int.MaxValue, 2)]
    [DataRow(int.MaxValue, 180, 180, 240, 240, 1)]
    public void AggregateAlbumExtremeDurationsAndTolerancesKeepAllSources(
        int tolerance, int? leftFirst, int? leftSecond, int? rightFirst, int? rightSecond, int expectedGroups)
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
            MinSharesAggregate = 1,
            AggregateLengthTol = tolerance,
        };
        var projection = new SearchViewProjectionDefinition(SearchViewProjectionKind.AggregateAlbums,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "left", @"ELO\Time\01 - Track.flac", 900, SearchResultVisibility.Public) with { Length = leftFirst },
            Input(2, "left", @"ELO\Time\02 - Track.flac", 900, SearchResultVisibility.Public) with { Length = leftSecond },
            Input(3, "right", @"ELO\Time\01 - Track.flac", 900, SearchResultVisibility.Public) with { Length = rightFirst },
            Input(4, "right", @"ELO\Time\02 - Track.flac", 900, SearchResultVisibility.Public) with { Length = rightSecond },
        ];
        var incremental = new SearchViewKernel(projection, settings);
        foreach (SearchProjectionInput input in inputs)
            incremental.Apply([input], checked((int)input.Sequence), false);
        var rebuilt = new SearchViewKernel(projection, settings);
        rebuilt.Apply(inputs, 4, false);
        AssertEquivalent(rebuilt.Snapshot(), incremental.Snapshot());
        Assert.AreEqual((long)expectedGroups, incremental.Snapshot().Counters.TopLevelItemCount);
        Assert.AreEqual(4L, incremental.Snapshot().Counters.ProjectedFileCount);
        CollectionAssert.AreEquivalent(new[] { "left", "right" }, incremental.Snapshot().AggregateAlbumGroups!
            .SelectMany(group => group.Options).Select(folder => folder.Username).ToArray());
        AlbumFolder[] folders = incremental.Snapshot().AggregateAlbumGroups!
            .SelectMany(group => group.Options).ToArray();
        AlbumFolder left = folders.Single(folder => folder.Username == "left");
        AlbumFolder right = folders.Single(folder => folder.Username == "right");
        // Artwork fallback compares the same folders outside the projection.
        Assert.AreEqual(expectedGroups == 1, Searcher.AlbumsAreSimilar(left, right, tolerance: tolerance));
        int[] cachedLengths = new[] { leftFirst ?? -1, leftSecond ?? -1 }.Order().ToArray();
        Assert.AreEqual(expectedGroups == 1, Searcher.AlbumsAreSimilar(left, right, cachedLengths, tolerance));
        SearchViewCounters before = incremental.Snapshot().Counters;
        incremental.Apply([], 5, true);
        Assert.AreEqual(before, incremental.Snapshot().Counters);
    }

    [TestMethod]
    public void EveryIncrementalPrefixEqualsRebuildAndCompletionOnlyChangesCompleteness()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions
            {
                StrictTitle = true,
                MinBitrate = 256,
            },
        };
        var projection = new FileSearchProjection(
            new SongQuery { Title = "Track" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "peer-A", @"Music\Track low.mp3", 192, SearchResultVisibility.Public),
            Input(2, "peer-B", @"Music\Track.flac", 900, SearchResultVisibility.Locked),
            Input(3, "peer-C", @"Music\Other.flac", 900, SearchResultVisibility.Public),
            Input(4, "peer-D", @"Music\Track.flac", 900, SearchResultVisibility.Public),
        ];

        var incremental = new SearchViewKernel(projection, settings);
        int consumed = 0;
        foreach (int batchSize in new[] { 1, 2, 1 })
        {
            consumed += batchSize;
            incremental.Apply(
                inputs.Skip(consumed - batchSize).Take(batchSize),
                sourceRevision: consumed,
                isComplete: false);

            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(
                inputs.Take(consumed),
                sourceRevision: consumed,
                isComplete: false);
            AssertEquivalent(rebuilt.Snapshot(), incremental.Snapshot());
        }

        SearchViewKernelSnapshot before = incremental.Snapshot();
        SearchViewKernelUpdate completed = incremental.Apply(
            [],
            sourceRevision: 5,
            isComplete: true);
        SearchViewKernelSnapshot after = incremental.Snapshot();

        Assert.IsTrue(completed.IsComplete);
        Assert.AreEqual(0, completed.ChangedFiles.Count);
        Assert.AreEqual(before.Counters, after.Counters);
        CollectionAssert.AreEqual(
            before.Files.Select(file => file.Input.Sequence).ToArray(),
            after.Files.Select(file => file.Input.Sequence).ToArray());
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.Files)]
    [DataRow(SearchViewProjectionKind.GenericDirectories)]
    [DataRow(SearchViewProjectionKind.AlbumDirectories)]
    [DataRow(SearchViewProjectionKind.AggregateTracks)]
    [DataRow(SearchViewProjectionKind.AggregateAlbums)]
    public void PreferenceTierIsExactlyAllConfiguredPreferredConditions(SearchViewProjectionKind kind)
    {
        var withConditions = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
            MinSharesAggregate = 1,
            PreferredCond = new FileConditions { StrictArtist = true, MinBitrate = 256 },
        };
        var projection = new SearchViewProjectionDefinition(kind,
            new SongQuery { Artist = "Artist", Title = "Track" },
            new AlbumQuery { Artist = "Artist", Album = "Album" }, IncludeFullResults: true);
        var kernel = new SearchViewKernel(projection, withConditions);
        SearchViewKernelUpdate update = kernel.Apply(
        [
            Input(1, "preferred", @"Music\Artist\Album\Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "low-bitrate", @"Music\Artist\Album\Track.mp3", 192, SearchResultVisibility.Public),
            Input(3, "wrong-artist", @"Music\Elsewhere\Album\Track.flac", 900, SearchResultVisibility.Public),
        ], 3, false);
        SearchViewKernelSnapshot snapshot = kernel.Snapshot();

        ProjectedFileCandidate preferred = update.ChangedFiles.Single(file => file.Input.Username == "preferred");
        ProjectedFileCandidate lowBitrate = update.ChangedFiles.Single(file => file.Input.Username == "low-bitrate");
        ProjectedFileCandidate wrongArtist = update.ChangedFiles.Single(file => file.Input.Username == "wrong-artist");
        Assert.AreEqual(SearchPreferenceTier.Preferred, preferred.ConditionFacts.PreferenceTier);
        CollectionAssert.AreEquivalent(
            new[] { SearchPreferenceCondition.Artist, SearchPreferenceCondition.Bitrate },
            preferred.ConditionFacts.SatisfiedPreferredConditions!.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { SearchPreferenceCondition.Artist, SearchPreferenceCondition.Bitrate },
            preferred.ConditionFacts.ConfiguredPreferredConditions!.ToArray());
        Assert.AreEqual(0, preferred.ConditionFacts.UnsatisfiedPreferredConditions.Count);
        Assert.AreEqual(SearchPreferenceTier.Other, lowBitrate.ConditionFacts.PreferenceTier);
        CollectionAssert.AreEquivalent(new[] { SearchPreferenceCondition.Artist },
            lowBitrate.ConditionFacts.SatisfiedPreferredConditions!.ToArray());
        CollectionAssert.AreEquivalent(new[] { SearchPreferenceCondition.Bitrate },
            lowBitrate.ConditionFacts.UnsatisfiedPreferredConditions.ToArray());
        Assert.AreEqual(SearchPreferenceTier.Other, wrongArtist.ConditionFacts.PreferenceTier);
        CollectionAssert.AreEquivalent(new[] { SearchPreferenceCondition.Artist },
            wrongArtist.ConditionFacts.UnsatisfiedPreferredConditions.ToArray());
        Assert.AreEqual(1L, snapshot.Counters.PreferredFileCount);
        Assert.AreEqual(2L, snapshot.Counters.OtherFileCount);

        var withoutConditions = new SearchViewKernel(projection, new SearchSettings
        {
            NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(), MinSharesAggregate = 1,
        });
        var unconfigured = withoutConditions.Apply(
            [Input(1, "any", @"Elsewhere\Album\Track.flac", null, SearchResultVisibility.Public)], 1, false);
        Assert.AreEqual(SearchPreferenceTier.Preferred,
            unconfigured.ChangedFiles.Single().ConditionFacts.PreferenceTier);
    }

    [TestMethod]
    [DataRow(SearchViewProjectionKind.Files)]
    [DataRow(SearchViewProjectionKind.AggregateTracks)]
    public void WorkflowLocalUserSuccessInputProducesTheSameAdmissionAndOrderForEveryConsumer(SearchViewProjectionKind kind)
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            DownrankOn = -1,
            IgnoreOn = -2,
        };
        var projection = new SearchViewProjectionDefinition(kind,
            new SongQuery { Title = "Track" },
            IncludeFullResults: true);
        SearchProjectionInput[] inputs =
        [
            Input(1, "failed-peer", @"Music\Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "successful-peer", @"Music\Track.flac", 900, SearchResultVisibility.Public),
            Input(3, "ignored-peer", @"Music\Track.flac", 900, SearchResultVisibility.Public),
        ];
        var workflowCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["failed-peer"] = -1,
            ["successful-peer"] = 0,
            ["ignored-peer"] = -2,
        };

        var local = new SearchViewKernel(projection, settings, workflowCounts);
        var daemon = new SearchViewKernel(projection, settings, workflowCounts);
        local.Apply(inputs, 3, true);
        daemon.Apply(inputs, 3, true);

        AssertEquivalent(local.Snapshot(), daemon.Snapshot());
        if (kind == SearchViewProjectionKind.AggregateTracks)
            AssertAggregateTracksEqual(local.Snapshot().AggregateTrackGroups!, daemon.Snapshot().AggregateTrackGroups!);
        var snapshot = daemon.Snapshot();
        var admitted = kind == SearchViewProjectionKind.Files ? snapshot.Files
            : snapshot.AggregateTrackGroups!.Single().NewOptions;
        CollectionAssert.AreEqual(
            new[] { "successful-peer", "failed-peer" },
            admitted.Select(file => file.Input.Username).ToArray());
        Assert.AreEqual(3L, snapshot.Counters.PublicFileCount);
        Assert.AreEqual(2L, snapshot.Counters.ProjectedPublicFileCount);
    }

    [TestMethod]
    public void RejectsOutOfOrderFreshInputsWithoutPublishingAPartialBatch()
    {
        var kernel = new SearchViewKernel(
            new FileSearchProjection(new SongQuery { Title = "Track" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            });

        Assert.ThrowsExactly<InvalidDataException>(() => kernel.Apply(
        [
            Input(2, "later", @"Music\Track 2.flac", 900, SearchResultVisibility.Public),
            Input(1, "earlier", @"Music\Track 1.flac", 900, SearchResultVisibility.Public),
        ], 2, false));

        SearchViewKernelSnapshot snapshot = kernel.Snapshot();
        Assert.AreEqual(0L, snapshot.ConsumedSequence);
        Assert.AreEqual(0L, snapshot.Counters.PublicFileCount);
        Assert.AreEqual(0, snapshot.Files.Count);
    }

    [TestMethod]
    public void GenericDirectoriesAreExactAndEveryPrefixMatchesARebuild()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions { MinBitrate = 256 },
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.GenericDirectories,
            new SongQuery { Title = "Track" },
            IncludeFullResults: true);
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer", @"Share\One\Track low.mp3", 192, SearchResultVisibility.Public),
            Input(2, "Peer", @"Share\One\Track.flac", 900, SearchResultVisibility.Locked),
            Input(3, "Peer", @"Share\Two\Track.flac", 900, SearchResultVisibility.Public),
            Input(4, "peer", @"Share\One\Track.flac", 900, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);

        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply(
                [inputs[count - 1]],
                count,
                false);
            Assert.IsNotNull(update.ChangedDirectories);

            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(inputs.Take(count), count, false);
            AssertDirectoriesEqual(
                rebuilt.Snapshot().Directories!,
                incremental.Snapshot().Directories!);
            Assert.AreEqual(
                rebuilt.Snapshot().Counters,
                incremental.Snapshot().Counters);
        }

        SearchViewProjectedDirectory exact = incremental.Snapshot().Directories!
            .Single(directory => directory.Directory == new PeerDirectoryIdentity("Peer", @"Share\One"));
        Assert.AreEqual(1L, exact.PublicMatchingFileCount);
        Assert.AreEqual(1L, exact.LockedMatchingFileCount);
        Assert.AreEqual(SearchResultVisibility.Locked, exact.BestChild.Input.Visibility);
        Assert.AreEqual(3L, incremental.Snapshot().Counters.TopLevelItemCount);
        Assert.AreEqual(3L, incremental.Snapshot().Counters.SelectableOptionCount);
    }

    [TestMethod]
    public void GenericDirectoriesMergeUnderTheTopmostObservedAncestor()
    {
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.GenericDirectories,
            new SongQuery { Title = "Track" },
            IncludeFullResults: true);
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
        };
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer", @"A\B\Track one.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer", @"A\C\Track two.flac", 900, SearchResultVisibility.Locked),
            Input(3, "Peer", @"A\B\D\Track three.flac", 900, SearchResultVisibility.Public),
            Input(4, "Peer", @"A\Track root.flac", 900, SearchResultVisibility.Public),
            Input(5, "Peer", @"A\C\E\Track four.flac", 900, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);
        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply([inputs[count - 1]], count, false);
            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(inputs.Take(count), count, false);
            AssertDirectoriesEqual(rebuilt.Snapshot().Directories!, incremental.Snapshot().Directories!);
            Assert.AreEqual(rebuilt.Snapshot().Counters, incremental.Snapshot().Counters);
            if (count == 3)
            {
                Assert.AreEqual(2L, update.Counters.TopLevelItemCount);
                CollectionAssert.AreEqual(
                    new[] { new PeerDirectoryIdentity("Peer", @"A\B") },
                    update.ChangedDirectories!.Select(directory => directory.Directory).ToArray());
            }
            if (count == 4)
            {
                Assert.AreEqual(1L, update.Counters.TopLevelItemCount);
                CollectionAssert.AreEquivalent(
                    new[]
                    {
                        new PeerDirectoryIdentity("Peer", @"A\B"),
                        new PeerDirectoryIdentity("Peer", @"A\C"),
                    },
                    update.RemovedDirectories!.ToArray());
                CollectionAssert.AreEquivalent(
                    update.RemovedDirectories!.ToArray(),
                    update.ChangedDirectories!.Single().AbsorbedDirectories!.ToArray());
            }
        }
        SearchViewProjectedDirectory root = incremental.Snapshot().Directories!.Single();
        Assert.AreEqual(@"A", root.Directory.FolderPath);
        Assert.AreEqual(4L, root.PublicMatchingFileCount);
        Assert.AreEqual(1L, root.LockedMatchingFileCount);
    }

    [TestMethod]
    public void GenericDirectoriesDoNotPublishTransientChildRootsFromOneBatch()
    {
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
                Input(1, "Peer", @"A\B\Track one.flac", 900, SearchResultVisibility.Public),
                Input(2, "Peer", @"A\Track root.flac", 900, SearchResultVisibility.Public),
                Input(3, "Peer", @"A\C\Track two.flac", 900, SearchResultVisibility.Public),
            ], 1, false);

        Assert.AreEqual(1L, update.Counters.TopLevelItemCount);
        Assert.AreEqual(@"A", update.ChangedDirectories!.Single().Directory.FolderPath);
        Assert.AreEqual(3, update.ChangedDirectories!.Single().NewChildren.Count);
        Assert.AreEqual(0, update.ChangedDirectories!.Single().AbsorbedDirectories!.Count);
        Assert.AreEqual(0, update.RemovedDirectories!.Count);
    }

    [TestMethod]
    public void AlbumDirectoriesUseOnePassFactsAndRemovalMatchesEveryPrefix()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions { MinBitrate = 256 },
            NecessaryFolderCond = new FolderConditions { MaxTrackCount = 1 },
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AlbumDirectories,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer", @"ELO\Time\01. Twilight.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer", @"ELO\Time\02. Yours Truly.flac", 192, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);

        SearchViewKernelUpdate first = incremental.Apply([inputs[0]], 1, false);
        Assert.AreEqual(1, first.ChangedDirectories!.Count);
        Assert.AreEqual(0, first.RemovedDirectories!.Count);
        SearchViewProjectedDirectory folder = incremental.Snapshot().Directories!.Single();
        Assert.AreEqual(SearchPreferenceTier.Preferred, folder.BestChild.ConditionFacts.PreferenceTier);

        SearchViewKernelUpdate second = incremental.Apply([inputs[1]], 2, false);
        Assert.AreEqual(0, second.ChangedDirectories!.Count);
        Assert.AreEqual(1, second.RemovedDirectories!.Count);
        Assert.AreEqual(0L, second.Counters.TopLevelItemCount);
        Assert.AreEqual(0L, second.Counters.ProjectedFileCount);
        Assert.AreEqual(inputs[0], folder.BestChild.Input,
            "A later folder removal must not alter the previously published best child.");
        Assert.AreEqual(SearchPreferenceTier.Preferred, folder.BestChild.ConditionFacts.PreferenceTier);

        var rebuilt = new SearchViewKernel(projection, settings);
        rebuilt.Apply(inputs, 2, false);
        AssertDirectoriesEqual(
            rebuilt.Snapshot().Directories!,
            incremental.Snapshot().Directories!);
        Assert.AreEqual(rebuilt.Snapshot().Counters, incremental.Snapshot().Counters);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void AggregateTrackGroupsAndAlternativesMatchEveryPrefix(int minimumShares)
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions { MinBitrate = 256 },
            MinSharesAggregate = minimumShares,
            Relax = true,
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateTracks,
            new SongQuery { Artist = "ELO" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer-A", @"ELO - Track.mp3", 192, SearchResultVisibility.Public),
            Input(2, "Peer-B", @"ELO - Track.flac", 900, SearchResultVisibility.Locked),
            Input(3, "Peer-C", @"ELO - Other.flac", 900, SearchResultVisibility.Public),
            Input(4, "Peer-D", @"ELO - Track.flac", 1200, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);
        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply(
                [inputs[count - 1]],
                count,
                false);
            Assert.IsNotNull(update.ChangedAggregateTrackGroups);
            CollectionAssert.AreEquivalent(
                update.ChangedAggregateTrackGroups.SelectMany(group => group.NewOptions)
                    .Select(file => file.Input).ToArray(),
                update.ChangedFiles.Select(file => file.Input).ToArray(),
                "Publication must contain visible alternatives, including earlier arrivals when a group first qualifies.");
            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(inputs.Take(count), count, false);
            AssertAggregateTracksEqual(
                rebuilt.Snapshot().AggregateTrackGroups!,
                incremental.Snapshot().AggregateTrackGroups!);
            Assert.AreEqual(rebuilt.Snapshot().Counters, incremental.Snapshot().Counters);
            foreach (var changed in update.ChangedAggregateTrackGroups)
            {
                var complete = rebuilt.Snapshot().AggregateTrackGroups!.Single(group => group.Index == changed.Index);
                Assert.AreEqual(complete.Representative.Input, changed.Representative.Input);
                Assert.AreEqual(complete.ShareCount, changed.ShareCount);
                Assert.AreEqual(complete.SelectableOptionCount, changed.SelectableOptionCount);
            }
        }

        SearchViewProjectedAggregateTrackGroup track = incremental.Snapshot()
            .AggregateTrackGroups!
            .Single(group => group.Query.Title.Contains("Track", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(3, track.ShareCount);
        Assert.AreEqual(2L, track.SelectableOptionCount);
        Assert.AreEqual(3, track.NewOptions.Count);
    }

    [TestMethod]
    public void AggregateAlbumGroupsAndDirectoryOptionsMatchEveryPrefix()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
            MinSharesAggregate = 1,
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateAlbums,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer-B", @"ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public),
            Input(3, "Peer-A", @"ELO\Time\cover.jpg", null, SearchResultVisibility.Locked)
                with { Extension = ".jpg", Length = null },
        ];
        var incremental = new SearchViewKernel(projection, settings);
        var observedDirectories = new Dictionary<PeerDirectoryIdentity, SearchViewProjectedDirectory>();
        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply(
                [inputs[count - 1]],
                count,
                false);
            Assert.IsNotNull(update.ChangedAggregateAlbumGroups);
            AssertVisibleAlbumFileCounts(incremental.Snapshot());
            if (count == 3)
            {
                Assert.AreEqual(0, update.ChangedAggregateAlbumGroups!.Count);
                Assert.AreEqual(1, update.ChangedDirectories!.Count);
                Assert.AreEqual(1, update.ChangedFiles.Count);
            }
            ApplyDirectoryChanges(observedDirectories, update);
            CollectionAssert.AreEquivalent(
                incremental.Snapshot().AggregateAlbumGroups!.SelectMany(group => group.Options)
                    .Select(folder => folder.DirectoryIdentity).Distinct().ToArray(),
                observedDirectories.Keys.ToArray());
            var rebuilt = new SearchViewKernel(projection, settings);
            SearchViewKernelUpdate rebuiltUpdate = rebuilt.Apply(inputs.Take(count), count, false);
            AssertDirectoriesEqual(rebuiltUpdate.ChangedDirectories!, observedDirectories.Values.ToArray());
            AssertAggregateAlbumsEqual(
                rebuilt.Snapshot().AggregateAlbumGroups!,
                incremental.Snapshot().AggregateAlbumGroups!);
            Assert.AreEqual(rebuilt.Snapshot().Counters, incremental.Snapshot().Counters);
        }

        SearchViewProjectedAggregateAlbumGroup time = incremental.Snapshot()
            .AggregateAlbumGroups!
            .Single(group => group.Options.Any(folder => folder.FolderPath == @"ELO\Time"));
        Assert.AreEqual(2, time.ShareCount);
        Assert.AreEqual(2L, time.SelectableOptionCount);
        Assert.AreEqual(2, time.Options.Count);
        Assert.AreEqual(3L, incremental.Snapshot().Counters.ProjectedFileCount);
        Assert.AreEqual(1L, incremental.Snapshot().Counters.ProjectedLockedFileCount);
        var locked = observedDirectories.Values.Single(folder => folder.Directory.Username == "Peer-A");
        Assert.AreEqual(1L, locked.LockedMatchingFileCount);
    }

    [TestMethod]
    public void FilteredAlbumAggregateCountersMatchRebuildAcrossFolderRegrouping()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions { MinBitrate = 256 },
            NecessaryFolderCond = new FolderConditions(),
            MinSharesAggregate = 2,
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateAlbums,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" },
            FilterText: "Special");
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer-Z", @"ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer-A", @"Special\ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public),
            Input(3, "Peer-Z", @"ELO\Time\02. Other.flac", 900, SearchResultVisibility.Locked),
            Input(4, "Peer-B", @"Special\ELO\Time\01. Track.flac", 192, SearchResultVisibility.Public),
            Input(5, "Peer-A", @"Special\ELO\Time\02. Other.flac", 900, SearchResultVisibility.Public),
            Input(6, "Peer-0", @"Special\ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public)
                with { Length = 240 },
            Input(7, "Peer-1", @"Special\ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public)
                with { Length = 240 },
        ];
        var incremental = new SearchViewKernel(projection, settings);
        var observedDirectories = new Dictionary<PeerDirectoryIdentity, SearchViewProjectedDirectory>();
        var observedGroups = new Dictionary<PeerDirectoryIdentity,
            SearchViewProjectedAggregateAlbumGroup>();
        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply([inputs[count - 1]], count, false);
            foreach (PeerDirectoryIdentity removed in update.RemovedAggregateAlbumGroups!)
                observedGroups.Remove(removed);
            foreach (SearchViewProjectedAggregateAlbumGroup changed in update.ChangedAggregateAlbumGroups!)
                observedGroups[changed.StableIdentity] = changed;
            AssertVisibleAlbumFileCounts(incremental.Snapshot());
            ApplyDirectoryChanges(observedDirectories, update);
            CollectionAssert.AreEquivalent(
                incremental.Snapshot().AggregateAlbumGroups!.SelectMany(group => group.Options)
                    .Select(folder => folder.DirectoryIdentity).Distinct().ToArray(),
                observedDirectories.Keys.ToArray());
            var rebuilt = new SearchViewKernel(projection, settings);
            SearchViewKernelUpdate rebuiltUpdate = rebuilt.Apply(inputs.Take(count), count, false);
            AssertDirectoriesEqual(rebuiltUpdate.ChangedDirectories!, observedDirectories.Values.ToArray());
            AssertAggregateAlbumsEqual(
                rebuilt.Snapshot().AggregateAlbumGroups!,
                incremental.Snapshot().AggregateAlbumGroups!);
            Assert.AreEqual(rebuilt.Snapshot().Counters, update.Counters);
            AssertAggregateAlbumsEqual(
                incremental.Snapshot().AggregateAlbumGroups!,
                observedGroups.Values.ToArray());
            PeerDirectoryIdentity[] visibleIdentities = incremental.Snapshot()
                .AggregateAlbumGroups!
                .Select(group => group.StableIdentity)
                .ToArray();
            Assert.IsFalse(update.RemovedAggregateAlbumGroups!
                .Any(visibleIdentities.Contains));
        }

        SearchViewKernelUpdate unchanged = incremental.Apply([], inputs.Length, true);
        Assert.AreEqual(0, unchanged.ChangedAggregateAlbumGroups!.Count);
        Assert.AreEqual(0, unchanged.RemovedAggregateAlbumGroups!.Count);
    }

    private static void ApplyDirectoryChanges(
        Dictionary<PeerDirectoryIdentity, SearchViewProjectedDirectory> observed,
        SearchViewKernelUpdate update)
    {
        foreach (var removed in update.RemovedDirectories!) observed.Remove(removed);
        foreach (var changed in update.ChangedDirectories!)
        {
            var children = observed.TryGetValue(changed.Directory, out var previous)
                ? previous.NewChildren.Concat(changed.NewChildren).ToArray() : changed.NewChildren;
            observed[changed.Directory] = changed with { NewChildren = children };
        }
    }

    private static void AssertVisibleAlbumFileCounts(SearchViewKernelSnapshot snapshot)
    {
        var files = snapshot.AggregateAlbumGroups!.SelectMany(group => group.Options)
            .SelectMany(folder => folder.Files)
            .DistinctBy(file => (new PeerPathKey(file.Candidate.Username, file.Filename), file.Candidate.Visibility))
            .ToArray();
        long publicFiles = files.LongCount(file => file.Candidate.Visibility == SearchResultVisibility.Public);
        long preferred = files.LongCount(file => file.Candidate.ProjectionFacts?.PreferenceTier == SearchPreferenceTier.Preferred);
        Assert.AreEqual((long)files.Length, snapshot.Counters.ProjectedFileCount);
        Assert.AreEqual(publicFiles, snapshot.Counters.ProjectedPublicFileCount);
        Assert.AreEqual(files.Length - publicFiles, snapshot.Counters.ProjectedLockedFileCount);
        Assert.AreEqual(preferred, snapshot.Counters.PreferredFileCount);
        Assert.AreEqual(files.Length - preferred, snapshot.Counters.OtherFileCount);
    }

    private static void AssertEquivalent(
        SearchViewKernelSnapshot expected,
        SearchViewKernelSnapshot actual)
    {
        Assert.AreEqual(expected.SourceRevision, actual.SourceRevision);
        Assert.AreEqual(expected.ConsumedSequence, actual.ConsumedSequence);
        Assert.AreEqual(expected.IsComplete, actual.IsComplete);
        Assert.AreEqual(expected.Counters, actual.Counters);
        CollectionAssert.AreEqual(
            expected.Files.Select(file => file.Input.Sequence).ToArray(),
            actual.Files.Select(file => file.Input.Sequence).ToArray());
        CollectionAssert.AreEqual(
            expected.Files.Select(file => file.ConditionFacts.PreferenceTier).ToArray(),
            actual.Files.Select(file => file.ConditionFacts.PreferenceTier).ToArray());
        CollectionAssert.AreEqual(
            expected.Files.Select(file => file.SortKey).ToArray(),
            actual.Files.Select(file => file.SortKey).ToArray());
    }

    private static void AssertDirectoriesEqual(
        IReadOnlyList<SearchViewProjectedDirectory> expected,
        IReadOnlyList<SearchViewProjectedDirectory> actual)
    {
        string[] expectedRows = expected
            .OrderBy(item => item.Directory.Username, StringComparer.Ordinal)
            .ThenBy(item => item.Directory.FolderPath, StringComparer.Ordinal)
            .Select(item => $"{item.Directory.Username}\0{item.Directory.FolderPath}\0" +
                $"{item.PublicMatchingFileCount}\0{item.LockedMatchingFileCount}\0" +
                $"{item.PublicMatchingBytes}\0{item.LockedMatchingBytes}\0" +
                JsonSerializer.Serialize(item.BestChild))
            .ToArray();
        string[] actualRows = actual
            .OrderBy(item => item.Directory.Username, StringComparer.Ordinal)
            .ThenBy(item => item.Directory.FolderPath, StringComparer.Ordinal)
            .Select(item => $"{item.Directory.Username}\0{item.Directory.FolderPath}\0" +
                $"{item.PublicMatchingFileCount}\0{item.LockedMatchingFileCount}\0" +
                $"{item.PublicMatchingBytes}\0{item.LockedMatchingBytes}\0" +
                JsonSerializer.Serialize(item.BestChild))
            .ToArray();
        CollectionAssert.AreEqual(expectedRows, actualRows);
    }

    private static void AssertAggregateTracksEqual(
        IReadOnlyList<SearchViewProjectedAggregateTrackGroup> expected,
        IReadOnlyList<SearchViewProjectedAggregateTrackGroup> actual)
    {
        string[] ExpectedRows() => expected
            .OrderBy(group => group.Index)
            .Select(group => $"{group.Index}\0{group.Query}\0{group.ShareCount}\0" +
                $"{group.SelectableOptionCount}\0{group.Representative.Input.Sequence}\0" +
                string.Join(',', group.NewOptions.Select(option => option.Input.Sequence)))
            .ToArray();
        string[] ActualRows() => actual
            .OrderBy(group => group.Index)
            .Select(group => $"{group.Index}\0{group.Query}\0{group.ShareCount}\0" +
                $"{group.SelectableOptionCount}\0{group.Representative.Input.Sequence}\0" +
                string.Join(',', group.NewOptions.Select(option => option.Input.Sequence)))
            .ToArray();
        CollectionAssert.AreEqual(ExpectedRows(), ActualRows());
    }

    private static void AssertAggregateAlbumsEqual(
        IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> expected,
        IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> actual)
    {
        string[] expectedRows = expected
            .OrderBy(group => group.Index)
            .Select(group => $"{group.StableIdentity.Username}\0{group.StableIdentity.FolderPath}\0" +
                $"{group.ShareCount}\0{group.SelectableOptionCount}\0" +
                string.Join(',', group.Options.Select(folder =>
                    folder.Username + "|" + folder.FolderPath)))
            .ToArray();
        string[] actualRows = actual
            .OrderBy(group => group.Index)
            .Select(group => $"{group.StableIdentity.Username}\0{group.StableIdentity.FolderPath}\0" +
                $"{group.ShareCount}\0{group.SelectableOptionCount}\0" +
                string.Join(',', group.Options.Select(folder =>
                    folder.Username + "|" + folder.FolderPath)))
            .ToArray();
        CollectionAssert.AreEqual(expectedRows, actualRows);
    }

    private static SearchProjectionInput Input(
        long sequence,
        string username,
        string filename,
        int? bitrate,
        SearchResultVisibility visibility)
        => new(
            sequence,
            checked((int)sequence),
            username,
            1,
            filename,
            1_000,
            bitrate,
            null,
            44_100,
            180,
            Path.GetExtension(filename),
            1_000,
            true,
            null,
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            3,
            visibility);
}
