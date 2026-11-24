using BenchmarkDotNet.Attributes;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class AlbumQualityCoverageBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> rawResults = null!;
    private SearchSettings requiredFlacSearch = null!;
    private SearchSettings strictRequiredFlacSearch = null!;
    private AlbumQuery albumQuery = null!;

    [Params(10_000)]
    public int FolderCount { get; set; }

    [Params(10)]
    public int TracksPerFolder { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        rawResults = BenchmarkDataFactory.CreateMixedAlbumQualityResults(FolderCount, TracksPerFolder);
        albumQuery = BenchmarkDataFactory.AlbumQuery;

        requiredFlacSearch = BenchmarkDataFactory.CreateSearchSettings();
        requiredFlacSearch.NecessaryCond.Formats = ["flac"];

        strictRequiredFlacSearch = BenchmarkDataFactory.CreateSearchSettings();
        strictRequiredFlacSearch.NecessaryCond.Formats = ["flac"];
        strictRequiredFlacSearch.StrictAlbumQuality = true;
    }

    [Benchmark(Baseline = true)]
    public int MixedQualityCoverage()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, requiredFlacSearch).Count;

    [Benchmark]
    public int StrictQualityCoverage()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, strictRequiredFlacSearch).Count;
}
