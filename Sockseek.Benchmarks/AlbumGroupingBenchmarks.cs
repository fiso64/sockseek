using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
public partial class AlbumGroupingBenchmarks
{
    private List<(SearchResponse Response, SlFile File)> rawResults = null!;
    private SearchSettings search = null!;
    private AlbumQuery albumQuery = null!;
    private ConcurrentDictionary<string, int> userSuccessCounts = null!;

    [Params(1_000, 10_000)]
    public int FolderCount { get; set; }

    [Params(10)]
    public int TracksPerFolder { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        rawResults = BenchmarkDataFactory.CreateAlbumResults(FolderCount, TracksPerFolder);
        search = BenchmarkDataFactory.CreateSearchSettings();
        albumQuery = BenchmarkDataFactory.AlbumQuery;
        userSuccessCounts = BenchmarkDataFactory.CreateUserSuccessCounts(FolderCount);
    }

    [Benchmark(Baseline = true)]
    public int Legacy_AlbumFolders_GlobalFileSort()
        => LegacyAlbumFolders(rawResults, albumQuery, search, userSuccessCounts).Count;

    [Benchmark]
    public int New_AlbumFolders_GroupThenSort()
        => SearchResultProjector.AlbumFolders(rawResults, albumQuery, search, userSuccessCounts).Count;

    [Benchmark]
    public int Legacy_AlbumAggregate_GlobalFileSortThenGroup()
    {
        var folders = LegacyAlbumFolders(rawResults, albumQuery, search, userSuccessCounts);
        return SearchResultProjector.AggregateAlbums(folders, albumQuery, search).Count;
    }

    [Benchmark]
    public int New_AlbumAggregate_GroupThenRankBuckets()
    {
        var folders = SearchResultProjector.AlbumFolders(
            rawResults,
            albumQuery,
            search,
            userSuccessCounts,
            ignoreStringSortConditions: true,
            sortMode: FolderSortMode.DeterministicUnranked);
        return SearchResultProjector.AggregateAlbums(folders, albumQuery, search).Count;
    }

    [Benchmark]
    public int Legacy_IncrementalAlbumFolders_GlobalFileSort()
    {
        var sortQuery = SearchResultProjector.AlbumFileMatchQuery(albumQuery);
        var sorter = new IncrementalResultSorter(
            sortQuery,
            search,
            userSuccessCounts,
            useInfer: false,
            useLevenshtein: false,
            albumMode: true);

        sorter.AddRange(rawResults.Where(result =>
            search.NecessaryCond.UserSatisfies(result.Response)
            && (!Utils.IsMusicFile(result.File.Filename)
                || search.NecessaryCond.FileSatisfies(result.File, sortQuery, result.Response))));

        return LegacyAlbumFoldersFromOrderedResults(
                sorter.Snapshot(),
                albumQuery,
                search,
                sorter.Count)
            .Count;
    }

    [Benchmark]
    public int New_IncrementalAlbumFolders_SortedGrouping()
    {
        var projector = new IncrementalAlbumFolderProjector(
            albumQuery,
            search,
            userSuccessCounts);

        projector.AddRange(rawResults);
        return projector.Snapshot().Count;
    }

    [Benchmark]
    public int Legacy_IncrementalAlbumAggregate_GlobalFileSort()
    {
        var sortQuery = SearchResultProjector.AlbumFileMatchQuery(albumQuery);
        var sorter = new IncrementalResultSorter(
            sortQuery,
            search,
            userSuccessCounts,
            useInfer: false,
            useLevenshtein: false,
            albumMode: true,
            ignoreStringSortConditions: true);

        sorter.AddRange(rawResults.Where(result =>
            search.NecessaryCond.UserSatisfies(result.Response)
            && (!Utils.IsMusicFile(result.File.Filename)
                || search.NecessaryCond.FileSatisfies(result.File, sortQuery, result.Response))));

        var folders = LegacyAlbumFoldersFromOrderedResults(
            sorter.Snapshot(),
            albumQuery,
            search,
            sorter.Count);

        var aggregateProjector = new IncrementalAlbumAggregateProjector(albumQuery, search);
        aggregateProjector.AddRange(folders);
        return aggregateProjector.Snapshot().Count;
    }

    [Benchmark]
    public int New_IncrementalAlbumAggregate_GroupThenRankBuckets()
    {
        var folderProjector = new IncrementalAlbumFolderProjector(
            albumQuery,
            search,
            userSuccessCounts,
            ignoreStringSortConditions: true,
            sortMode: FolderSortMode.DeterministicUnranked);
        var aggregateProjector = new IncrementalAlbumAggregateProjector(albumQuery, search);

        var changes = folderProjector.AddRangeAndGetChanges(rawResults);
        aggregateProjector.ApplyChanges(changes);
        return aggregateProjector.Snapshot().Count;
    }

    private static List<AlbumFolder> LegacyAlbumFolders(
        IEnumerable<(SearchResponse Response, SlFile File)> rawResults,
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var sortQuery = SearchResultProjector.AlbumFileMatchQuery(query);
        var filteredResults = rawResults
            .Where(result =>
                search.NecessaryCond.UserSatisfies(result.Response)
                && (!Utils.IsMusicFile(result.File.Filename)
                    || search.NecessaryCond.FileSatisfies(result.File, sortQuery, result.Response)));
        var orderedResults = ResultSorter.OrderedResults(
            filteredResults.Select(x => (x.Response, x.File)),
            sortQuery,
            search,
            userSuccessCounts,
            useInfer: false,
            useLevenshtein: false,
            albumMode: true);

        int capacity = rawResults.TryGetNonEnumeratedCount(out int resultCount) ? resultCount : 0;
        return LegacyAlbumFoldersFromOrderedResults(orderedResults.Select(x => (x.response, x.file)), query, search, capacity);
    }

    private static List<AlbumFolder> LegacyAlbumFoldersFromOrderedResults(
        IEnumerable<(SearchResponse Response, SlFile File)> orderedResults,
        AlbumQuery query,
        SearchSettings search,
        int capacity = 0)
    {
        bool canMatchDisc = !DiscPatternRegex().IsMatch(query.Album) && !DiscPatternRegex().IsMatch(query.Artist);
        var dirStructure = capacity > 0
            ? new Dictionary<string, LegacyAlbumFolderBuilder>(capacity)
            : new Dictionary<string, LegacyAlbumFolderBuilder>();
        int idx = 0;

        foreach (var (response, file) in orderedResults)
        {
            string username = response.Username;
            string folderPath = file.Filename[..file.Filename.LastIndexOf('\\')];
            string dirName = folderPath[(folderPath.LastIndexOf('\\') + 1)..];

            if (canMatchDisc && DiscPatternRegex().IsMatch(dirName))
                folderPath = folderPath[..folderPath.LastIndexOf('\\')];

            string key = username + '\\' + folderPath;
            bool isMusic = Utils.IsMusicFile(file.Filename);
            var folderFile = new LegacyAlbumFolderFile(response, file, isMusic);
            if (!dirStructure.TryGetValue(key, out var value))
                dirStructure[key] = new LegacyAlbumFolderBuilder(username, folderPath, folderFile, idx);
            else
                value.Add(folderFile);

            idx++;
        }

        LegacyMergeChildDirectories(dirStructure);

        int? min = search.NecessaryFolderCond.MinTrackCount;
        int? max = search.NecessaryFolderCond.MaxTrackCount;
        bool searchResultsLikelyContainCompleteAlbumFolders =
            query.SearchHint.Length == 0
            || query.Album.Length > 0
            && !search.NecessaryCond.StrictTitle
            && !search.PreferredCond.StrictTitle;
        var folders = new List<AlbumFolder>();
        var inferDefault = new SongQuery { Artist = query.Artist, Album = query.Album };

        foreach (var (_, folder) in dirStructure)
        {
            if (folder.MusicCount == 0) continue;
            if (min is { } minCount && minCount > 0
                && searchResultsLikelyContainCompleteAlbumFolders
                && folder.MusicCount < minCount) continue;
            if (max is { } maxCount && folder.MusicCount > maxCount) continue;

            folder.Files.Sort(LegacyAlbumFolderFileComparer.Instance);

            folders.Add(new AlbumFolder(
                folder.Username,
                folder.FolderPath,
                () => LegacyBuildAlbumFiles(folder.Files, inferDefault),
                folder.Files.Count,
                folder.MusicCount,
                LegacySortedAudioLengths(folder.Files),
                LegacyRepresentativeAudioFilename(folder.Files)));
        }

        return folders;
    }

    private static void LegacyMergeChildDirectories(Dictionary<string, LegacyAlbumFolderBuilder> dirStructure)
    {
        var sortedKeys = dirStructure.Keys.OrderBy(k => k).ToList();
        var toRemove = new HashSet<string>();

        for (int i = 0; i < sortedKeys.Count; i++)
        {
            var key = sortedKeys[i];
            if (toRemove.Contains(key)) continue;
            for (int j = i + 1; j < sortedKeys.Count; j++)
            {
                var key2 = sortedKeys[j];
                if (toRemove.Contains(key2)) continue;
                if (LegacyIsDescendantOrSamePrefix(key2, key))
                {
                    if (dirStructure[key].FirstIndex <= dirStructure[key2].FirstIndex)
                    {
                        dirStructure[key].AddRange(dirStructure[key2]);
                        toRemove.Add(key2);
                    }
                    else
                    {
                        dirStructure[key2].AddRange(dirStructure[key]);
                        toRemove.Add(key);
                        key = key2;
                    }
                }
                else if (!key2.StartsWith(key, StringComparison.Ordinal)) break;
            }
        }
        foreach (var key in toRemove) dirStructure.Remove(key);
    }

    private static bool LegacyIsDescendantOrSamePrefix(string possibleChild, string parent)
        => possibleChild.Length > parent.Length
            && possibleChild[parent.Length] == '\\'
            && possibleChild.StartsWith(parent, StringComparison.Ordinal);

    private static int[] LegacySortedAudioLengths(List<LegacyAlbumFolderFile> folderFiles)
        => folderFiles
            .Where(f => f.IsMusic)
            .Select(f => f.File.Length ?? -1)
            .OrderBy(x => x)
            .ToArray();

    private static string? LegacyRepresentativeAudioFilename(List<LegacyAlbumFolderFile> folderFiles)
        => folderFiles.FirstOrDefault(f => f.IsMusic).File?.Filename;

    private static List<SongJob> LegacyBuildAlbumFiles(List<LegacyAlbumFolderFile> folderFiles, SongQuery inferDefault)
    {
        var files = new List<SongJob>(folderFiles.Count);
        var inferredByFilename = new Dictionary<string, SongQuery>();

        foreach (var item in folderFiles)
        {
            if (!inferredByFilename.TryGetValue(item.File.Filename, out var info))
            {
                info = Searcher.InferSongQuery(item.File.Filename, inferDefault);
                inferredByFilename.Add(item.File.Filename, info);
            }

            files.Add(new SongJob(info) { ResolvedTarget = new FileCandidate(item.Response, item.File) });
        }

        return files;
    }

    private sealed class LegacyAlbumFolderBuilder
    {
        public string Username { get; }
        public string FolderPath { get; }
        public List<LegacyAlbumFolderFile> Files { get; }
        public int FirstIndex { get; }
        public int MusicCount { get; private set; }

        public LegacyAlbumFolderBuilder(string username, string folderPath, LegacyAlbumFolderFile file, int firstIndex)
        {
            Username = username;
            FolderPath = folderPath;
            Files = [file];
            FirstIndex = firstIndex;
            MusicCount = file.IsMusic ? 1 : 0;
        }

        public void Add(LegacyAlbumFolderFile file)
        {
            Files.Add(file);
            if (file.IsMusic)
                MusicCount++;
        }

        public void AddRange(LegacyAlbumFolderBuilder other)
        {
            Files.AddRange(other.Files);
            MusicCount += other.MusicCount;
        }
    }

    private readonly record struct LegacyAlbumFolderFile(SearchResponse Response, SlFile File, bool IsMusic);

    private sealed class LegacyAlbumFolderFileComparer : IComparer<LegacyAlbumFolderFile>
    {
        public static readonly LegacyAlbumFolderFileComparer Instance = new();

        public int Compare(LegacyAlbumFolderFile x, LegacyAlbumFolderFile y)
        {
            int comparison = y.IsMusic.CompareTo(x.IsMusic);
            return comparison != 0
                ? comparison
                : Comparer<string>.Default.Compare(x.File.Filename, y.File.Filename);
        }
    }

    [GeneratedRegex(@"^(?:cd|disc|disk|vinyl)\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscPatternRegex();
}
