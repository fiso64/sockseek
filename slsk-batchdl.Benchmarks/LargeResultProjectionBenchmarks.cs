using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace slsk_batchdl.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class LargeResultProjectionBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> trackResults = null!;
    private List<(SearchResponse Response, SlFile File)> albumResults = null!;
    private List<AlbumFolder> albumFolders = null!;
    private SearchSettings search = null!;
    private SongQuery trackQuery = null!;
    private AlbumQuery albumQuery = null!;
    private ConcurrentDictionary<string, int> userSuccessCounts = null!;

    [Params(20_000)]
    public int ResultCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        search = BenchmarkDataFactory.CreateSearchSettings();
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
    public int SongSort_NoInferNoLevenshtein()
        => SearchResultProjector.SortedTrackCandidates(
                trackResults,
                trackQuery,
                search,
                userSuccessCounts,
                useInfer: false,
                useLevenshtein: false)
            .Count;

    [Benchmark]
    public int AlbumGroup()
        => SearchResultProjector.AlbumFolders(albumResults, albumQuery, search).Count;

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
