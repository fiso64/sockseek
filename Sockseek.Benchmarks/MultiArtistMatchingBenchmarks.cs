using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;

namespace Sockseek.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class MultiArtistMatchingBenchmarks
{
    private List<(SearchResponse Response, Soulseek.File File)> results = null!;
    private SongQuery query = null!;
    private SearchSettings search = null!;
    private readonly ConcurrentDictionary<string, int> userStats = new();

    [Params(10_000, 100_000)]
    public int CandidateCount { get; set; }

    [Params("Single", "First", "Last", "Miss")]
    public string ArtistMatch { get; set; } = "Single";

    [GlobalSetup]
    public void Setup()
    {
        query = new SongQuery
        {
            Artist = ArtistMatch == "Single" ? "Artist One" : "Artist One;Artist Two;Artist Three;Artist Four",
            Title = "Some Song (Guest Remix)",
            Album = "Some Album",
            Length = 200,
        };
        search = new SearchSettings
        {
            NecessaryCond = new FileConditions { StrictArtist = true, StrictTitle = true, LengthTolerance = 3 },
            PreferredCond = new FileConditions
            {
                StrictArtist = true, StrictTitle = true, StrictAlbum = true,
                Formats = ["mp3"], LengthTolerance = 3, MinBitrate = 200,
            },
        };
        string artist = ArtistMatch switch { "Last" => "Artist Four", "Miss" => "Other Artist", _ => "Artist One" };
        results = new(CandidateCount);
        for (int i = 0; i < CandidateCount; i++)
        {
            var file = new Soulseek.File(1, $@"Music\{artist}\Some Album\{i:D6} - Some Song (Guest Remix).mp3",
                5_000_000, ".mp3", [new FileAttribute(FileAttributeType.Length, 200), new FileAttribute(FileAttributeType.BitRate, 320)]);
            var response = new SearchResponse($"User{i % 100}", 1, true, 1_000_000, 0, [file]);
            results.Add((response, file));
        }
    }

    [Benchmark]
    public int RequiredFiltering()
    {
        int count = 0;
        foreach (var (response, file) in results)
            if (ConditionSatisfactionPolicy.SearchFileSatisfies(search.NecessaryCond, response, file, query))
                count++;
        return count;
    }

    [Benchmark]
    public int RequiredAndPreferredRanking()
        => ResultSorter.OrderedResults(results, query, search, userStats).Count();
}
