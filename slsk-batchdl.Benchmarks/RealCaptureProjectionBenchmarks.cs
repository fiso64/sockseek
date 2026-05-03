using System.Collections.Concurrent;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace slsk_batchdl.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class RealCaptureProjectionBenchmarks
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<IReadOnlyDictionary<string, CapturePayload>> Captures = new(LoadCaptures);

    private List<(SearchResponse Response, SlFile File)> rawResults = null!;
    private List<List<(SearchResponse Response, SlFile File)>> cumulativeResultSnapshots = null!;
    private List<List<(SearchResponse Response, SlFile File)>> resultBatches = null!;
    private List<AlbumFolder> albumFolders = null!;
    private List<List<AlbumFolder>> albumFolderBatches = null!;
    private SearchSettings search = null!;
    private SongQuery trackQuery = null!;
    private AlbumQuery albumQuery = null!;
    private ConcurrentDictionary<string, int> userSuccessCounts = null!;

    public IEnumerable<string> CaptureNames => Captures.Value.Keys.OrderBy(x => x);

    [ParamsSource(nameof(CaptureNames))]
    public string CaptureName { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var capture = Captures.Value[CaptureName];

        search = BenchmarkDataFactory.CreateSearchSettings();
        trackQuery = capture.Query;
        albumQuery = new AlbumQuery
        {
            Artist = trackQuery.Artist,
            Album = trackQuery.Album.Length > 0 ? trackQuery.Album : trackQuery.Title,
            SearchHint = trackQuery.Title,
            ArtistMaybeWrong = trackQuery.ArtistMaybeWrong,
        };
        userSuccessCounts = new ConcurrentDictionary<string, int>();

        rawResults = capture.Results.Select(Rehydrate).ToList();
        cumulativeResultSnapshots = BuildCumulativeSnapshots(rawResults, snapshotCount: 10);
        resultBatches = BuildBatches(rawResults, batchCount: 10);
        albumFolders = SearchResultProjector.AlbumFolders(rawResults, albumQuery, search);
        albumFolderBatches = BuildBatches(albumFolders, batchCount: 10);
    }

    [Benchmark]
    public int SongSort()
        => SearchResultProjector.SortedTrackCandidates(rawResults, trackQuery, search, userSuccessCounts).Count;

    [Benchmark]
    public int SongSort_Streaming10Snapshots()
    {
        int count = 0;
        foreach (var snapshot in cumulativeResultSnapshots)
            count += SearchResultProjector.SortedTrackCandidates(snapshot, trackQuery, search, userSuccessCounts).Count;
        return count;
    }

    [Benchmark]
    public int SongSort_Incremental10Batches()
    {
        int count = 0;
        var sorter = new IncrementalResultSorter(trackQuery, search, userSuccessCounts);
        foreach (var batch in resultBatches)
        {
            sorter.AddRange(batch);
            count += sorter.Snapshot().Count;
        }

        return count;
    }

    [Benchmark(Baseline = true)]
    public int AlbumGroup()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, search).Count;

    [Benchmark]
    public int AlbumGroup_Streaming10Snapshots()
    {
        int count = 0;
        foreach (var snapshot in cumulativeResultSnapshots)
            count += SearchResultProjector.AlbumFolders(snapshot, albumQuery, search).Count;
        return count;
    }

    [Benchmark]
    public int AlbumGroup_Incremental10Batches()
    {
        int count = 0;
        var projector = new IncrementalAlbumFolderProjector(albumQuery, search, userSuccessCounts);
        foreach (var batch in resultBatches)
        {
            projector.AddRange(batch);
            count += projector.Snapshot().Count;
        }

        return count;
    }

    [Benchmark]
    public int AggregateGroup()
        => SearchResultProjector.AggregateTracks(rawResults, trackQuery, search, userSuccessCounts).Count;

    [Benchmark]
    public int AggregateGroup_Streaming10Snapshots()
    {
        int count = 0;
        foreach (var snapshot in cumulativeResultSnapshots)
            count += SearchResultProjector.AggregateTracks(snapshot, trackQuery, search, userSuccessCounts).Count;
        return count;
    }

    [Benchmark]
    public int AggregateGroup_Incremental10Batches()
    {
        int count = 0;
        var projector = new IncrementalAggregateTrackProjector(trackQuery, search, userSuccessCounts);
        foreach (var batch in resultBatches)
        {
            projector.AddRange(batch);
            count += projector.Snapshot().Count;
        }

        return count;
    }

    [Benchmark]
    public int AlbumAggregateGroup()
        => SearchResultProjector.AggregateAlbums(albumFolders, albumQuery, search).Count;

    [Benchmark]
    public int AlbumAggregateGroup_Streaming10Snapshots()
    {
        int count = 0;
        foreach (var snapshot in cumulativeResultSnapshots)
        {
            var folders = SearchResultProjector.AlbumFolders(snapshot, albumQuery, search);
            count += SearchResultProjector.AggregateAlbums(folders, albumQuery, search).Count;
        }

        return count;
    }

    [Benchmark]
    public int AlbumAggregateGroup_Incremental10Batches()
    {
        int count = 0;
        var projector = new IncrementalAlbumAggregateProjector(albumQuery, search);
        foreach (var batch in albumFolderBatches)
        {
            projector.AddRange(batch);
            count += projector.Snapshot().Count;
        }

        return count;
    }

    [Benchmark]
    public int AlbumAggregateGroup_IncrementalAlbumFolders10Batches()
    {
        int count = 0;
        var projector = new IncrementalAlbumFolderProjector(albumQuery, search, userSuccessCounts);
        foreach (var batch in resultBatches)
        {
            projector.AddRange(batch);
            var folders = projector.Snapshot();
            count += SearchResultProjector.AggregateAlbums(folders, albumQuery, search).Count;
        }

        return count;
    }

    private static IReadOnlyDictionary<string, CapturePayload> LoadCaptures()
    {
        string captureDir = FindCaptureDirectory();
        return System.IO.Directory.GetFiles(captureDir, "*.raw.json")
            .Select(path => JsonSerializer.Deserialize<CapturePayload>(System.IO.File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"Could not deserialize {path}"))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static string FindCaptureDirectory()
    {
        var dir = new DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "search-captures");
            if (System.IO.Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find artifacts/search-captures from benchmark working directory.");
    }

    private static List<List<T>> BuildCumulativeSnapshots<T>(List<T> items, int snapshotCount)
    {
        var snapshots = new List<List<T>>(snapshotCount);
        for (int i = 1; i <= snapshotCount; i++)
        {
            int count = (int)Math.Ceiling(items.Count * i / (double)snapshotCount);
            snapshots.Add(items.Take(count).ToList());
        }

        return snapshots;
    }

    private static List<List<T>> BuildBatches<T>(List<T> items, int batchCount)
    {
        var batches = new List<List<T>>(batchCount);
        int previousCount = 0;
        for (int i = 1; i <= batchCount; i++)
        {
            int count = (int)Math.Ceiling(items.Count * i / (double)batchCount);
            batches.Add(items.Skip(previousCount).Take(count - previousCount).ToList());
            previousCount = count;
        }

        return batches;
    }

    private static (SearchResponse Response, SlFile File) Rehydrate(CapturedResult result)
    {
        var attrs = new List<FileAttribute>();
        if (result.File.Length is int length)
            attrs.Add(new FileAttribute(FileAttributeType.Length, length));
        if (result.File.Bitrate is int bitrate)
            attrs.Add(new FileAttribute(FileAttributeType.BitRate, bitrate));
        if (result.File.SampleRate is int sampleRate)
            attrs.Add(new FileAttribute(FileAttributeType.SampleRate, sampleRate));
        if (result.File.BitDepth is int bitDepth)
            attrs.Add(new FileAttribute(FileAttributeType.BitDepth, bitDepth));

        string ext = Path.GetExtension(result.File.Filename);
        var file = new SlFile(0, result.File.Filename, result.File.Size, ext, attributeList: attrs);
        var response = new SearchResponse(
            result.User.Username,
            0,
            result.User.HasFreeUploadSlot,
            result.User.UploadSpeed,
            result.User.QueueLength,
            [file]);
        return (response, file);
    }

    private record CapturePayload(
        string Name,
        DateTimeOffset CapturedAt,
        SongQuery Query,
        int SearchTimeout,
        int ResultCount,
        int LockedFilesCount,
        List<CapturedResult> Results);

    private record CapturedResult(CapturedUser User, CapturedFile File);

    private record CapturedUser(
        string Username,
        int UploadSpeed,
        bool HasFreeUploadSlot,
        int QueueLength,
        int LockedFileCount);

    private record CapturedFile(
        string Filename,
        long Size,
        int? Length,
        int? Bitrate,
        int? SampleRate,
        int? BitDepth);
}
