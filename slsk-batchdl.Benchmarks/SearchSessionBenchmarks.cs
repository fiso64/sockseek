using BenchmarkDotNet.Attributes;
using Sldl.Core.Services;
using Soulseek;

namespace slsk_batchdl.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class SearchSessionBenchmarks
{
    private List<SearchResponse> responses = null!;

    [Params(1_000, 5_000)]
    public int ResultCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        responses = BenchmarkDataFactory
            .CreateTrackResults(ResultCount)
            .Select(x => x.Response)
            .ToList();
    }

    [Benchmark]
    public int AddResponses()
    {
        var session = new SearchSession();
        foreach (var response in responses)
            session.AddResponse(response);
        return session.Results.Count;
    }

    [Benchmark]
    public int Snapshot()
    {
        var session = new SearchSession();
        foreach (var response in responses)
            session.AddResponse(response);
        return session.Snapshot().Count;
    }
}
