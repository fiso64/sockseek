using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class ResultSorterBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> results = null!;
    private List<List<(SearchResponse Response, SlFile File)>> resultBatches = null!;
    private SearchSettings search = null!;
    private SongQuery query = null!;
    private ConcurrentDictionary<string, int> peerFailureCounts = null!;

    [Params(1_000, 5_000, 20_000)]
    public int ResultCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        results = BenchmarkDataFactory.CreateTrackResults(ResultCount);
        resultBatches = BuildBatches(results, batchCount: 10);
        search = BenchmarkDataFactory.CreateSearchSettings();
        query = BenchmarkDataFactory.TrackQuery;
        peerFailureCounts = BenchmarkDataFactory.CreatePeerFailureCounts(ResultCount);
    }

    [Benchmark(Baseline = true)]
    public int TrackSort()
        => ConsumeOrderedResults(ResultSorter.OrderedResults(
                results.Select(x => (x.Response, x.File)),
                query,
                search,
                peerFailureCounts,
                useInfer: true));

    [Benchmark]
    public int TrackSort_NoInfer()
        => ConsumeOrderedResults(ResultSorter.OrderedResults(
                results.Select(x => (x.Response, x.File)),
                query,
                search,
                peerFailureCounts,
                useInfer: false));

    [Benchmark]
    public int AlbumModeSort()
        => ConsumeOrderedResults(ResultSorter.OrderedResults(
                results.Select(x => (x.Response, x.File)),
                query,
                search,
                peerFailureCounts,
                useInfer: false,
                albumMode: true));

    [Benchmark]
    public int IncrementalTrackSort_Filtered10Batches()
    {
        var sorter = new IncrementalResultSorter(
            query,
            search,
            peerFailureCounts,
            useInfer: true,
            requireFileSatisfies: true);

        foreach (var batch in resultBatches)
            sorter.AddRange(batch);

        return ConsumeOrderedResults(sorter.Snapshot());
    }

    private static int ConsumeOrderedResults(IEnumerable<(SearchResponse response, SlFile file)> orderedResults)
    {
        int checksum = 0;
        foreach (var (response, file) in orderedResults)
            checksum = HashCode.Combine(checksum, response.Username, file.Filename);

        return checksum;
    }

    private static List<List<T>> BuildBatches<T>(List<T> items, int batchCount)
    {
        var batches = new List<List<T>>(batchCount);
        int previousCount = 0;
        for (int i = 1; i <= batchCount; i++)
        {
            int count = (int)Math.Ceiling(items.Count * i / (double)batchCount);
            batches.Add(items.GetRange(previousCount, count - previousCount));
            previousCount = count;
        }

        return batches;
    }
}
