using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class ResultSorterBenchmarks
{
    private List<SearchProjectionInput> results = null!;
    private List<List<SearchProjectionInput>> resultBatches = null!;
    private SearchSettings search = null!;
    private SongQuery query = null!;
    private ConcurrentDictionary<string, int> peerFailureCounts = null!;

    [Params(1_000, 5_000, 20_000)]
    public int ResultCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        results = BenchmarkDataFactory.CreateTrackResults(ResultCount)
            .Select((pair, index) => SearchProjectionInput.FromLive(index + 1L, 1, pair.Response, pair.File, DateTimeOffset.UnixEpoch)).ToList();
        resultBatches = BuildBatches(results, batchCount: 10);
        search = BenchmarkDataFactory.CreateSearchSettings();
        query = BenchmarkDataFactory.TrackQuery;
        peerFailureCounts = BenchmarkDataFactory.CreatePeerFailureCounts(ResultCount);
    }

    [Benchmark(Baseline = true)]
    public int TrackSort() => Sort(albumMode: false);

    [Benchmark]
    public int AlbumModeSort() => Sort(albumMode: true);

    private int Sort(bool albumMode)
    {
        var sorter = new IncrementalResultSorter(query, search, peerFailureCounts, albumMode);
        sorter.AddRange(results);
        return ConsumeOrderedResults(sorter.SnapshotInputs());
    }

    [Benchmark]
    public int IncrementalTrackSort_Filtered10Batches()
    {
        var sorter = new IncrementalResultSorter(
            query,
            search,
            peerFailureCounts,
            requireFileSatisfies: true);

        foreach (var batch in resultBatches)
            sorter.AddRange(batch);

        return ConsumeOrderedResults(sorter.SnapshotInputs());
    }

    private static int ConsumeOrderedResults(IEnumerable<SearchProjectionInput> orderedResults)
    {
        int checksum = 0;
        foreach (var file in orderedResults)
            checksum = HashCode.Combine(checksum, file.Username, file.Filename);

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
