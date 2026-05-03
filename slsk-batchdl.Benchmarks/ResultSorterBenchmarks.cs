using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace slsk_batchdl.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class ResultSorterBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> results = null!;
    private SearchSettings search = null!;
    private SongQuery query = null!;
    private ConcurrentDictionary<string, int> userSuccessCounts = null!;

    [Params(1_000, 5_000, 20_000)]
    public int ResultCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        results = BenchmarkDataFactory.CreateTrackResults(ResultCount);
        search = BenchmarkDataFactory.CreateSearchSettings();
        query = BenchmarkDataFactory.TrackQuery;
        userSuccessCounts = BenchmarkDataFactory.CreateUserSuccessCounts(ResultCount);
    }

    [Benchmark(Baseline = true)]
    public int TrackSort()
        => ConsumeOrderedResults(ResultSorter.OrderedResults(
                results.Select(x => (x.Response, x.File)),
                query,
                search,
                userSuccessCounts,
                useInfer: true,
                useLevenshtein: true));

    [Benchmark]
    public int TrackSort_NoInferNoLevenshtein()
        => ConsumeOrderedResults(ResultSorter.OrderedResults(
                results.Select(x => (x.Response, x.File)),
                query,
                search,
                userSuccessCounts,
                useInfer: false,
                useLevenshtein: false));

    [Benchmark]
    public int AlbumModeSort()
        => ConsumeOrderedResults(ResultSorter.OrderedResults(
                results.Select(x => (x.Response, x.File)),
                query,
                search,
                userSuccessCounts,
                useInfer: false,
                useLevenshtein: false,
                albumMode: true));

    private static int ConsumeOrderedResults(IEnumerable<(SearchResponse response, SlFile file)> orderedResults)
    {
        int checksum = 0;
        foreach (var (response, file) in orderedResults)
            checksum = HashCode.Combine(checksum, response.Username, file.Filename);

        return checksum;
    }
}
