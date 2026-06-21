using BenchmarkDotNet.Attributes;
using Sockseek.Core;
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
    private SearchSettings legacyProjectionSearch = null!;
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

        strictRequiredFlacSearch = CloneSearchSettings(requiredFlacSearch);
        strictRequiredFlacSearch.StrictAlbumQuality = true;

        legacyProjectionSearch = CloneSearchSettings(requiredFlacSearch);
        legacyProjectionSearch.NecessaryCond = legacyProjectionSearch.NecessaryCond.WithoutAudioQualityConditions();
    }

    [Benchmark(Baseline = true)]
    public int Old_FormatFilterBeforeGrouping()
    {
        var sortQuery = SearchResultProjector.AlbumFileMatchQuery(albumQuery);
        var filteredResults = rawResults.Where(result =>
            requiredFlacSearch.NecessaryCond.UserSatisfies(result.Response)
            && (!Utils.IsMusicFile(result.File.Filename)
                || requiredFlacSearch.NecessaryCond.FileSatisfies(result.File, sortQuery, result.Response)));

        return SearchResultProjector.AlbumFolders(filteredResults, albumQuery, legacyProjectionSearch).Count;
    }

    [Benchmark]
    public int New_MixedQualityCoverage()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, requiredFlacSearch).Count;

    [Benchmark]
    public int New_StrictQualityCoverage()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, strictRequiredFlacSearch).Count;

    private static SearchSettings CloneSearchSettings(SearchSettings source)
        => new()
        {
            NecessaryCond = new FileConditions(source.NecessaryCond),
            PreferredCond = new FileConditions(source.PreferredCond),
            NecessaryFolderCond = new FolderConditions(source.NecessaryFolderCond),
            PreferredFolderCond = new FolderConditions(source.PreferredFolderCond),
            StrictAlbumQuality = source.StrictAlbumQuality,
            SearchTimeout = source.SearchTimeout,
            MaxStaleTime = source.MaxStaleTime,
            DownrankOn = source.DownrankOn,
            IgnoreOn = source.IgnoreOn,
            FastSearch = source.FastSearch,
            FastSearchDelay = source.FastSearchDelay,
            FastSearchMinUpSpeed = source.FastSearchMinUpSpeed,
            DesperateSearch = source.DesperateSearch,
            NoRemoveSpecialChars = source.NoRemoveSpecialChars,
            RemoveSingleCharSearchTerms = source.RemoveSingleCharSearchTerms,
            NoBrowseFolder = source.NoBrowseFolder,
            Relax = source.Relax,
            ArtistMaybeWrong = source.ArtistMaybeWrong,
            IsAggregate = source.IsAggregate,
            MinSharesAggregate = source.MinSharesAggregate,
            AggregateLengthTol = source.AggregateLengthTol,
        };
}
