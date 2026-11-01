using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using SlFile = Soulseek.File;
using SlResponse = Soulseek.SearchResponse;

namespace Sockseek.Core.Services;

public static partial class SearchResultProjector
{
    public static List<FileCandidate> SortedTrackCandidates(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = true)
        => SortedTrackCandidates(
            rawResults.Select((result, index) => SearchProjectionInput.FromLive(
                index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)),
            query,
            search,
            userSuccessCounts,
            useInfer);

    public static List<FileCandidate> SortedTrackCandidates(
        IEnumerable<SearchProjectionInput> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = true,
        bool includeFullResults = true)
    {
        var sorter = new IncrementalResultSorter(
            query,
            search,
            userSuccessCounts,
            useInfer: useInfer,
            requireFileSatisfies: !includeFullResults);
        sorter.AddRange(rawResults);
        return sorter.SnapshotProjectedFiles()
            .Select(file => file.Input.ToFileCandidate(file.ConditionFacts))
            .ToList();
    }

    public static List<SongJob> AggregateTracks(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
        => AggregateTracks(rawResults.Select((result, index) => SearchProjectionInput.FromLive(
            index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)),
            query, search, userSuccessCounts);

    public static List<SongJob> AggregateTracks(
        IEnumerable<SearchProjectionInput> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
        => AggregateTracks(rawResults, query, search, userSuccessCounts, requiredConditionsAlreadyApplied: false);

    internal static List<SongJob> AggregateTracks(
        IEnumerable<SearchProjectionInput> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool requiredConditionsAlreadyApplied)
    {
        var projector = new IncrementalAggregateTrackProjector(query, search, userSuccessCounts);
        if (requiredConditionsAlreadyApplied) projector.AddAdmittedRange(rawResults);
        else projector.AddRange(rawResults);
        return projector.Snapshot();
    }

    internal static bool AggregateTrackProjectionIncludes(
        SearchResponse response,
        Soulseek.File file,
        SongQuery query,
        SearchSettings search)
        => ConditionSatisfactionPolicy.SearchFileSatisfies(search.NecessaryCond, response, file, query);

    internal static bool AggregateTrackProjectionIncludes(
        SearchProjectionInput input,
        SongQuery query,
        SearchSettings search)
        => ConditionSatisfactionPolicy.SearchFileSatisfies(search.NecessaryCond, input, query);

    public static List<AlbumFolder> AlbumFolders(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        var plan = new AlbumFolderProjectionPlan(
            query,
            search,
            userSuccessCounts,
            ignoreStringSortConditions,
            sortMode);
        var inputs = rawResults.Select((result, index) => SearchProjectionInput.FromLive(
            index + 1L,
            index + 1,
            result.Response,
            result.File,
            DateTimeOffset.UnixEpoch));
        List<EvaluatedAlbumProjectionInput> evaluated = plan.EvaluateToList(inputs);
        return plan.ProjectEvaluatedResults(evaluated);
    }

    public static List<AlbumFolder> AlbumFolders(
        IEnumerable<SearchProjectionInput> rawResults,
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        var plan = new AlbumFolderProjectionPlan(
            query,
            search,
            userSuccessCounts,
            ignoreStringSortConditions,
            sortMode);
        List<EvaluatedAlbumProjectionInput> evaluated = plan.EvaluateToList(rawResults);
        return plan.ProjectEvaluatedResults(evaluated);
    }

    // All callers supply admitted rows with their original condition/ranking
    // evidence. Grouping cannot silently evaluate those conditions again.
    internal static List<AlbumFolder> AlbumFoldersFromResults(
        IEnumerable<EvaluatedAlbumProjectionInput> results,
        AlbumQuery query,
        SearchSettings search,
        bool useAlbumFolderQualityRanking)
    {
        var dirStructure = new Dictionary<PeerPathKey, AlbumFolderBuilder>();
        foreach (var evaluated in results)
        {
            SearchProjectionInput input = evaluated.Input;
            if (evaluated.DirectoryPath is not { } path) continue;
            var key = new PeerPathKey(input.Username, path);
            if (!dirStructure.TryGetValue(key, out var value))
                dirStructure[key] = new AlbumFolderBuilder(key.Username, key.RemotePath, evaluated);
            else
                value.Add(evaluated);
        }

        MergeChildDirectories(dirStructure);
        var activeQuality = AlbumQualityPolicy.ActiveConditions(search.NecessaryCond);
        foreach (var folder in dirStructure.Values)
        {
            if (activeQuality.IsActive)
                folder.RefreshQualityCoverage(search.NecessaryCond, activeQuality);
            else
                folder.RefreshInactiveQualityCoverage();
        }

        var folders = new List<AlbumFolder>();
        var inferDefault = new SongQuery { Artist = query.Artist, Album = query.Album };

        IEnumerable<AlbumFolderBuilder> orderedFolders;
        IEnumerable<AlbumFolderBuilder> candidateFolders = dirStructure.Values;
        if (activeQuality.IsActive)
            candidateFolders = candidateFolders.Where(folder => ConditionSatisfactionPolicy.AlbumQualityIsAcceptable(folder.QualityCoverage, search));

        if (useAlbumFolderQualityRanking)
        {
            orderedFolders = candidateFolders.Order(
                activeQuality.IsActive
                    ? AlbumFolderBuilderComparer.WithQualityCoverage
                    : AlbumFolderBuilderComparer.WithoutQualityCoverage);
        }
        else
        {
            orderedFolders = candidateFolders
                .OrderBy(x => x.Username, StringComparer.Ordinal)
                .ThenBy(x => x.FolderPath, StringComparer.Ordinal);
        }

        foreach (var folder in orderedFolders)
        {
            if (folder.MusicCount == 0) continue;
            if (!ConditionSatisfactionPolicy.SearchAlbumFolderSatisfies(
                    search.NecessaryFolderCond,
                    folder.MusicCount,
                    folder.Files.Select(file => file.Input.Filename),
                    query,
                    search)) continue;

            if (folder.Files.Count > 1)
                folder.Files.Sort(AlbumFolderFileComparer.Instance);

            var qualityCoverage = folder.QualityCoverage;
            if (!ConditionSatisfactionPolicy.AlbumQualityIsAcceptable(qualityCoverage, search))
                continue;

            folders.Add(new AlbumFolder(
                folder.Username,
                folder.FolderPath,
                folder.Files.Select(file => file.File).ToArray(),
                inferDefault,
                folder.MusicCount,
                SortedAudioLengths(folder.Files),
                RepresentativeAudioFilename(folder.Files),
                qualityCoverage,
                folder.Best));
        }

        return folders;
    }

    private static int[] SortedAudioLengths(List<AlbumFolderFile> folderFiles)
        => folderFiles
            .Where(f => f.IsMusic)
            .Select(f => f.Input.Length ?? -1)
            .OrderBy(x => x)
            .ToArray();

    private static string? RepresentativeAudioFilename(List<AlbumFolderFile> folderFiles)
        => folderFiles.FirstOrDefault(f => f.IsMusic).Input?.Filename;

    public static List<AlbumJob> AggregateAlbums(
        IEnumerable<AlbumFolder> albums,
        AlbumQuery query,
        SearchSettings search)
    {
        var projector = new IncrementalAlbumAggregateProjector(query, search);
        projector.ResetBatch(albums);
        return projector.Snapshot();
    }

    internal static AlbumJob CreateAggregateAlbumJob(AlbumQuery query, List<AlbumFolder> versions)
    {
        var repFolder = versions.FirstOrDefault()?.FolderPath;
        var itemName = !string.IsNullOrWhiteSpace(repFolder)
            ? Utils.GetBaseNameSlsk(repFolder)
            : null;
        // Populate Album so each job gets a unique index key. Without this, all
        // aggregate album jobs share the same key (artist + empty album), causing
        // index collisions: the last write wins and all albums on rerun match the
        // same path.
        var jobQuery = !string.IsNullOrWhiteSpace(itemName)
            ? new AlbumQuery(query) { Album = itemName }
            : query;
        var newJob = new AlbumJob(jobQuery);
        newJob.Results = versions;
        if (itemName != null)
            newJob.ItemName = itemName;
        return newJob;
    }

    // Album search is still file-based under the hood, so we project the album query into:
    // 1. a network search query (Artist + Album, or SearchHint when Album is empty)
    // 2. a file-match/sort query used by StrictTitle and album-mode sorting
    public static SongQuery AlbumNetworkQuery(AlbumQuery query)
        => new()
        {
            Artist = query.Artist,
            Title = query.Album.Length > 0 ? query.Album : query.SearchHint,
            Album = query.Album,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    // Album search still uses Artist + Album (or SearchHint when Album is empty) for
    // the network query, but filename-level StrictTitle logic should only ever apply to
    // the optional song-title hint, never to the album name itself.
    public static SongQuery AlbumFileMatchQuery(AlbumQuery query)
        => new()
        {
            Artist = query.Artist,
            Title = query.SearchHint,
            Album = query.Album,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    internal static bool CanCombineDiscDirectories(AlbumQuery query)
        => !DiscPatternRegex().IsMatch(query.Album) && !DiscPatternRegex().IsMatch(query.Artist);

    internal static string? AlbumDirectoryPath(string filename, bool combineDiscs)
    {
        ReadOnlySpan<char> folder = AlbumDirectoryPath(filename.AsSpan(), combineDiscs);
        return folder.IsEmpty ? null : folder.ToString();
    }

    internal static ReadOnlySpan<char> AlbumDirectoryPath(ReadOnlySpan<char> filename, bool combineDiscs)
    {
        int separator = LastDirectorySeparator(filename);
        // Bare files have no advertised album folder. They remain available in
        // raw, File, Track and aggregate-track projections.
        if (separator <= 0) return [];
        ReadOnlySpan<char> folder = filename[..separator];
        if (combineDiscs && DiscPatternRegex().IsMatch(folder[(LastDirectorySeparator(folder) + 1)..]))
        {
            int parent = LastDirectorySeparator(folder);
            if (parent > 0) folder = folder[..parent];
        }
        return folder;
    }

    private static int LastDirectorySeparator(ReadOnlySpan<char> path)
        => Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));

    private static void MergeChildDirectories(Dictionary<PeerPathKey, AlbumFolderBuilder> dirStructure)
    {
        var sortedKeys = dirStructure.Keys
            .OrderByDescending(k => k.RemotePath.Count(c => c is '\\' or '/'))
            .ThenBy(k => k.Username, StringComparer.Ordinal)
            .ThenBy(k => k.RemotePath, StringComparer.Ordinal)
            .ToList();
        var toRemove = new HashSet<PeerPathKey>();

        foreach (var key in sortedKeys)
        {
            if (toRemove.Contains(key)) continue;
            var parentKey = FindNearestExistingAncestor(key, dirStructure, toRemove);
            if (parentKey is not { } parent)
                continue;

            dirStructure[parent].AddRange(dirStructure[key]);
            toRemove.Add(key);
        }
        foreach (var key in toRemove)
            dirStructure.Remove(key);

    }

    private static PeerPathKey? FindNearestExistingAncestor(
        PeerPathKey key,
        Dictionary<PeerPathKey, AlbumFolderBuilder> dirStructure,
        HashSet<PeerPathKey> toRemove)
    {
        int slash = LastDirectorySeparator(key.RemotePath);
        while (slash > 0)
        {
            var parentKey = key with { RemotePath = key.RemotePath[..slash] };
            if (!toRemove.Contains(parentKey) && dirStructure.ContainsKey(parentKey))
                return parentKey;

            slash = LastDirectorySeparator(parentKey.RemotePath);
        }

        return null;
    }

    private sealed class AlbumFolderBuilder
    {
        public string Username { get; }
        public string FolderPath { get; }
        public List<AlbumFolderFile> Files { get; }
        public int MusicCount { get; private set; }
        public AlbumAudioQualityCoverage QualityCoverage { get; private set; }
        // Keep the winning rank and its original evaluated file together. A
        // consumer never needs to reconstruct evidence or index every candidate.
        public EvaluatedAlbumProjectionInput Best { get; private set; }
        public ResultSorter.SortEntry AggregateSortEntry => Best.SortEntry;

        public AlbumFolderBuilder(
            string username,
            string folderPath,
            EvaluatedAlbumProjectionInput evaluated)
        {
            var file = new AlbumFolderFile(evaluated.File, Utils.IsMusicFile(evaluated.Input.Filename));
            Username = username;
            FolderPath = folderPath;
            // A peer-wide advertised count says nothing about this folder.
            // Grow from actual members instead of reserving it for every folder.
            Files = new List<AlbumFolderFile> { file };
            MusicCount = file.IsMusic ? 1 : 0;
            QualityCoverage = AlbumAudioQualityCoverage.Inactive(MusicCount);
            Best = evaluated;
        }

        public void RefreshQualityCoverage(FileConditions conditions, ActiveAlbumTrackConditions activeQuality)
            => QualityCoverage = AlbumQualityPolicy.Evaluate(
                Files.Where(file => file.IsMusic).Select(file => new ConditionFile(
                    file.Input.Filename,
                    file.Input.Length,
                    file.Input.BitRate,
                    file.Input.SampleRate,
                    file.Input.BitDepth)),
                conditions,
                activeQuality);

        public void RefreshInactiveQualityCoverage()
            => QualityCoverage = AlbumAudioQualityCoverage.Inactive(MusicCount);

        private void AddAggregateBest(EvaluatedAlbumProjectionInput evaluated)
        {
            if (ResultSorter.SortEntryComparer.Instance.Compare(evaluated.SortEntry, AggregateSortEntry) < 0)
                Best = evaluated;
        }

        public void Add(EvaluatedAlbumProjectionInput evaluated)
        {
            var file = new AlbumFolderFile(evaluated.File, Utils.IsMusicFile(evaluated.Input.Filename));
            Files.Add(file);
            if (file.IsMusic)
                MusicCount++;
            AddAggregateBest(evaluated);
        }

        public void AddRange(AlbumFolderBuilder other)
        {
            Files.AddRange(other.Files);
            AddAggregateBest(other.Best);
            MusicCount += other.MusicCount;
        }
    }

    private sealed class AlbumFolderBuilderComparer : IComparer<AlbumFolderBuilder>
    {
        public static AlbumFolderBuilderComparer WithQualityCoverage { get; } = new(compareQualityCoverage: true);
        public static AlbumFolderBuilderComparer WithoutQualityCoverage { get; } = new(compareQualityCoverage: false);

        private readonly bool compareQualityCoverage;

        private AlbumFolderBuilderComparer(bool compareQualityCoverage)
        {
            this.compareQualityCoverage = compareQualityCoverage;
        }

        public int Compare(AlbumFolderBuilder? x, AlbumFolderBuilder? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x == null)
                return 1;
            if (y == null)
                return -1;

            int beforeQualityComparison = ResultSorter.AlbumBeforeQualitySortEntryComparer.Instance.Compare(
                x.AggregateSortEntry, y.AggregateSortEntry);
            if (beforeQualityComparison != 0)
                return beforeQualityComparison;

            if (compareQualityCoverage)
            {
                // Match the file-sort key order, but lift each audio-quality key to
                // folder-level coverage: identity/length first, then format, bitrate,
                // sample rate, bit depth. This keeps high-quality unrelated folders
                // from outranking the album we asked for, while still preferring e.g.
                // 9/10 FLAC folders over 1/10 FLAC folders.
                int comparison = CompareCoverageBuckets(x.QualityCoverage.Format, y.QualityCoverage.Format);
                if (comparison != 0)
                    return comparison;
                comparison = CompareCoverageBuckets(x.QualityCoverage.Bitrate, y.QualityCoverage.Bitrate);
                if (comparison != 0)
                    return comparison;
                comparison = CompareCoverageBuckets(x.QualityCoverage.SampleRate, y.QualityCoverage.SampleRate);
                if (comparison != 0)
                    return comparison;
                comparison = CompareCoverageBuckets(x.QualityCoverage.BitDepth, y.QualityCoverage.BitDepth);
                if (comparison != 0)
                    return comparison;
            }

            int aggregateComparison = ResultSorter.SortEntryComparer.Instance.Compare(
                x.AggregateSortEntry, y.AggregateSortEntry);
            if (aggregateComparison != 0)
                return aggregateComparison;

            int usernameComparison = string.Compare(x.Username, y.Username, StringComparison.Ordinal);
            return usernameComparison != 0
                ? usernameComparison
                : string.Compare(x.FolderPath, y.FolderPath, StringComparison.Ordinal);
        }

        private static int CompareCoverageBuckets(AlbumQualityCoverageBucket x, AlbumQualityCoverageBucket y)
            => y.Bucket.CompareTo(x.Bucket);
    }

    private readonly record struct AlbumFolderFile(
        ProjectedFileCandidate File,
        bool IsMusic)
    {
        public SearchProjectionInput Input => File.Input;
    }

    private sealed class AlbumFolderFileComparer : IComparer<AlbumFolderFile>
    {
        public static readonly AlbumFolderFileComparer Instance = new();

        private AlbumFolderFileComparer()
        {
        }

        public int Compare(AlbumFolderFile x, AlbumFolderFile y)
        {
            int comparison = y.IsMusic.CompareTo(x.IsMusic);
            return comparison != 0
                ? comparison
                : string.Compare(x.Input.Filename, y.Input.Filename, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$")]
    private static partial Regex DiscPatternRegex();
}
