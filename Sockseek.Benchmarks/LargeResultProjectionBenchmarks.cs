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
public class LargeResultProjectionBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> trackResults = null!;
    private List<(SearchResponse Response, SlFile File)> albumResults = null!;
    private List<AlbumFolder> albumFolders = null!;
    private SearchSettings search = null!;
    private SearchSettings artworkSearch = null!;
    private SongQuery trackQuery = null!;
    private AlbumQuery albumQuery = null!;
    private ConcurrentDictionary<string, int> userSuccessCounts = null!;

    [Params(10_000, 100_000)]
    public int ResultCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        search = BenchmarkDataFactory.CreateSearchSettings();
        artworkSearch = new DownloadSettings { Search = search, Output = new() { AlbumArtOnly = true } }.ResolveSearchSettings();
        trackQuery = BenchmarkDataFactory.TrackQuery;
        albumQuery = BenchmarkDataFactory.AlbumQuery;
        userSuccessCounts = BenchmarkDataFactory.CreateUserSuccessCounts(ResultCount);

        trackResults = BenchmarkDataFactory.CreateTrackResults(ResultCount);
        albumResults = BenchmarkDataFactory
            .CreateAlbumResults(folderCount: ResultCount / 10 + 1, tracksPerFolder: 9)
            .Take(ResultCount)
            .ToList();
        albumFolders = SearchResultProjector.AlbumFolders(albumResults, albumQuery, search);
    }

    [Benchmark(Baseline = true)]
    public int SongSort()
        => SearchResultProjector.SortedTrackCandidates(
                trackResults,
                trackQuery,
                search,
                userSuccessCounts)
            .Count;

    [Benchmark]
    public int SongSort_NoInfer()
        => SearchResultProjector.SortedTrackCandidates(
                trackResults,
                trackQuery,
                search,
                userSuccessCounts,
                useInfer: false)
            .Count;

    [Benchmark]
    public int AlbumGroup()
        => SearchResultProjector.AlbumFolders(albumResults, albumQuery, search).Count;

    [Benchmark]
    public int ArtworkAlbumGroup()
        => SearchResultProjector.AlbumFolders(albumResults, albumQuery, artworkSearch).Count;

    [Benchmark]
    public int ArtworkSongSort()
        => SearchResultProjector.SortedTrackCandidates(
                trackResults,
                trackQuery,
                artworkSearch,
                userSuccessCounts,
                useInfer: false)
            .Count;

    [Benchmark]
    public int AggregateGroup()
        => SearchResultProjector.AggregateTracks(
                trackResults,
                trackQuery,
                search,
                userSuccessCounts)
            .Count;

    [Benchmark]
    public int AlbumAggregateGroup()
        => SearchResultProjector.AggregateAlbums(albumFolders, albumQuery, search).Count;
}
