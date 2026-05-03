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
public class SearchResultProjectorBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> rawResults = null!;
    private List<AlbumFolder> albumFolders = null!;
    private SearchSettings search = null!;
    private SongQuery trackQuery = null!;
    private AlbumQuery albumQuery = null!;
    private ConcurrentDictionary<string, int> userSuccessCounts = null!;

    [Params(100, 1_000)]
    public int FolderCount { get; set; }

    [Params(10)]
    public int TracksPerFolder { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        rawResults = BenchmarkDataFactory.CreateAlbumResults(FolderCount, TracksPerFolder);
        search = BenchmarkDataFactory.CreateSearchSettings();
        trackQuery = BenchmarkDataFactory.TrackQuery;
        albumQuery = BenchmarkDataFactory.AlbumQuery;
        userSuccessCounts = BenchmarkDataFactory.CreateUserSuccessCounts(FolderCount);
        albumFolders = SearchResultProjector.AlbumFolders(rawResults, albumQuery, search);
    }

    [Benchmark(Baseline = true)]
    public int AlbumFolders()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, search).Count;

    [Benchmark]
    public int AggregateTracks()
        => SearchResultProjector.AggregateTracks(rawResults, trackQuery, search, userSuccessCounts).Count;

    [Benchmark]
    public int AggregateAlbums()
        => SearchResultProjector.AggregateAlbums(albumFolders, albumQuery, search).Count;
}
