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
public class SearchResultProjectorBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> rawResults = null!;
    private List<(SearchResponse Response, SlFile File)> trackResults = null!;
    private List<AlbumFolder> albumFolders = null!;
    private SearchSettings search = null!;
    private SearchSettings noStringSearch = null!;
    private SongQuery trackQuery = null!;
    private AlbumQuery albumQuery = null!;
    private ConcurrentDictionary<string, int> peerFailureCounts = null!;

    [Params(100, 1_000)]
    public int FolderCount { get; set; }

    [Params(10)]
    public int TracksPerFolder { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        rawResults = BenchmarkDataFactory.CreateAlbumResults(FolderCount, TracksPerFolder);
        trackResults = BenchmarkDataFactory.CreateTrackResults(FolderCount * TracksPerFolder);
        search = BenchmarkDataFactory.CreateSearchSettings();
        noStringSearch = BenchmarkDataFactory.CreateSearchSettings();
        noStringSearch.PreferredCond.StrictTitle = false;
        noStringSearch.PreferredCond.StrictArtist = false;
        noStringSearch.PreferredCond.StrictAlbum = false;
        trackQuery = BenchmarkDataFactory.TrackQuery;
        albumQuery = BenchmarkDataFactory.AlbumQuery;
        peerFailureCounts = BenchmarkDataFactory.CreatePeerFailureCounts(FolderCount);
        albumFolders = SearchResultProjector.AlbumFolders(rawResults, albumQuery, search);
    }

    [Benchmark(Baseline = true)]
    public int AlbumFolders()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, search).Count;

    [Benchmark]
    public int AlbumFolders_StringRankingIgnored()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, search, ignoreStringSortConditions: true).Count;

    [Benchmark]
    public int SortedTrackCandidates()
        => SearchResultProjector.SortedTrackCandidates(trackResults, trackQuery, search, peerFailureCounts).Count;

    [Benchmark]
    public int SortedTrackCandidates_StringPrefsOff()
        => SearchResultProjector.SortedTrackCandidates(trackResults, trackQuery, noStringSearch, peerFailureCounts).Count;

    [Benchmark]
    public int TrackGroups()
        => SearchResultProjector.GroupTracks(rawResults, trackQuery, search, peerFailureCounts).Count;

    [Benchmark]
    public int AlbumGroups()
        => SearchResultProjector.GroupAlbums(albumFolders, albumQuery, search).Count;
}
