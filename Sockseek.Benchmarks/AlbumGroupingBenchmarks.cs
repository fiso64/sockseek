using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class AlbumGroupingBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> rawResults = null!;
    private SearchSettings search = null!;
    private AlbumQuery albumQuery = null!;
    private ConcurrentDictionary<string, int> peerFailureCounts = null!;

    [Params(1_000, 10_000)]
    public int FolderCount { get; set; }

    [Params(10)]
    public int TracksPerFolder { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        rawResults = BenchmarkDataFactory.CreateAlbumResults(FolderCount, TracksPerFolder);
        search = BenchmarkDataFactory.CreateSearchSettings();
        albumQuery = BenchmarkDataFactory.AlbumQuery;
        peerFailureCounts = BenchmarkDataFactory.CreatePeerFailureCounts(FolderCount);
    }

    [Benchmark]
    public int AlbumFolders_GroupThenSort()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, search, peerFailureCounts).Count;

    [Benchmark]
    public int AlbumGroups_GroupThenRankBuckets()
    {
        var folders = SearchResultProjector.AlbumFolders(
            rawResults,
            albumQuery,
            search,
            peerFailureCounts,
            ignoreStringSortConditions: true,
            sortMode: FolderSortMode.DeterministicUnranked);
        return SearchResultProjector.GroupAlbums(folders, albumQuery, search).Count;
    }

    [Benchmark]
    public int IncrementalAlbumFolders_SortedGrouping()
    {
        var projector = new IncrementalAlbumFolderProjector(
            albumQuery,
            search,
            peerFailureCounts);

        projector.AddRange(rawResults);
        return projector.Snapshot().Count;
    }

    [Benchmark]
    public int IncrementalAlbumGroups_GroupThenRankBuckets()
    {
        var folderProjector = new IncrementalAlbumFolderProjector(
            albumQuery,
            search,
            peerFailureCounts,
            ignoreStringSortConditions: true,
            sortMode: FolderSortMode.DeterministicUnranked);
        var groupProjector = new IncrementalAlbumGroupProjector(albumQuery, search);

        var changes = folderProjector.AddRangeAndGetChanges(rawResults);
        groupProjector.ApplyChanges(changes);
        return groupProjector.Snapshot().Count;
    }
}
