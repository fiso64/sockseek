using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Soulseek;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Settings;
using SlFile = Soulseek.File;
using SlResponse = Soulseek.SearchResponse;

namespace Sldl.Core.Services;

public static partial class SearchResultProjector
{
    public static List<FileCandidate> SortedTrackCandidates(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = true,
        bool useLevenshtein = true)
    {
        var ordered = ResultSorter.OrderedResults(
                rawResults.Select(x => (x.Response, x.File)),
                query,
                search,
                userSuccessCounts,
                useInfer,
                useLevenshtein);

        var candidates = new List<FileCandidate>();
        foreach (var (response, file) in ordered)
            candidates.Add(new FileCandidate(response, file));

        return candidates;
    }

    public static List<SongJob> AggregateTracks(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var equivalentFiles = Searcher.EquivalentFiles(query, rawResults.Select(x => (x.Response, x.File)), search)
            .Select(x => (x.query, Ordered: ResultSorter.OrderedResults(
                x.candidates.Select(c => (c.Response, c.File)),
                x.query,
                search,
                userSuccessCounts,
                useInfer: false,
                useLevenshtein: false,
                albumMode: false)))
            .ToList();

        if (!search.Relax)
        {
            equivalentFiles = equivalentFiles
                .Where(x => FileConditions.StrictString(x.query.Title, query.Title, ignoreCase: true)
                    && (FileConditions.StrictString(x.query.Artist, query.Artist, ignoreCase: true, boundarySkipWs: false)
                        || FileConditions.StrictString(x.query.Title, query.Artist, ignoreCase: true, boundarySkipWs: false)
                            && x.query.Title.ContainsInBrackets(query.Artist, ignoreCase: true)))
                .ToList();
        }

        return equivalentFiles.Select(x =>
        {
            var song = new SongJob(x.query);
            song.Candidates = x.Ordered.Select(r => new FileCandidate(r.response, r.file)).ToList();
            return song;
        }).ToList();
    }

    public static List<AlbumFolder> AlbumFolders(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null)
    {
        var sortQuery = AlbumFileMatchQuery(query);
        var filteredResults = rawResults
            .Where(result =>
                search.NecessaryCond.UserSatisfies(result.Response)
                && (!Utils.IsMusicFile(result.File.Filename)
                    || search.NecessaryCond.FileSatisfies(result.File, sortQuery, result.Response)));
        var orderedResults = ResultSorter.OrderedResults(
            filteredResults.Select(x => (x.Response, x.File)),
            sortQuery,
            search,
            userSuccessCounts ?? new ConcurrentDictionary<string, int>(),
            useInfer: false,
            useLevenshtein: false,
            albumMode: true);

        int capacity = rawResults.TryGetNonEnumeratedCount(out int resultCount) ? resultCount : 0;
        return AlbumFoldersFromOrderedResults(orderedResults.Select(x => (x.response, x.file)), query, search, capacity);
    }

    internal static List<AlbumFolder> AlbumFoldersFromOrderedResults(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> orderedResults,
        AlbumQuery query,
        SearchSettings search,
        int capacity = 0)
    {
        bool canMatchDisc = !DiscPatternRegex().IsMatch(query.Album) && !DiscPatternRegex().IsMatch(query.Artist);
        var dirStructure = capacity > 0
            ? new Dictionary<string, AlbumFolderBuilder>(capacity)
            : new Dictionary<string, AlbumFolderBuilder>();
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
            var folderFile = new AlbumFolderFile(response, file, isMusic);
            if (!dirStructure.TryGetValue(key, out var value))
                dirStructure[key] = new AlbumFolderBuilder(username, folderPath, folderFile, idx);
            else
                value.Add(folderFile);

            idx++;
        }

        MergeChildDirectories(dirStructure);

        int? min = search.NecessaryFolderCond.MinTrackCount;
        int? max = search.NecessaryFolderCond.MaxTrackCount;
        bool searchResultsLikelyContainCompleteAlbumFolders =
            SearchResultsLikelyContainCompleteAlbumFolders(query, search);
        var folders = new List<AlbumFolder>();
        var inferDefault = new SongQuery { Artist = query.Artist, Album = query.Album };

        foreach (var (_, folder) in dirStructure)
        {
            if (folder.MusicCount == 0) continue;
            // Search results can prove a folder has at least the visible audio files.
            // That always proves max-count overflow, and it can prove min-count
            // underflow when the search itself was not narrowed to a track hint.
            if (min is { } minCount && minCount > 0
                && searchResultsLikelyContainCompleteAlbumFolders
                && folder.MusicCount < minCount) continue;
            if (max is { } maxCount && folder.MusicCount > maxCount) continue;

            folder.Files.Sort(AlbumFolderFileComparer.Instance);

            if (!RequiredTrackTitlesSatisfy(search.NecessaryFolderCond.RequiredTrackTitles, folder.Files))
                continue;

            folders.Add(new AlbumFolder(
                folder.Username,
                folder.FolderPath,
                () => BuildAlbumFiles(folder.Files, inferDefault),
                folder.Files.Count,
                folder.MusicCount,
                SortedAudioLengths(folder.Files),
                RepresentativeAudioFilename(folder.Files)));
        }

        return folders;
    }

    private static bool SearchResultsLikelyContainCompleteAlbumFolders(AlbumQuery query, SearchSettings search)
    {
        if (query.SearchHint.Length == 0)
            return true;

        // If Album is empty, SearchHint becomes the network query, so Soulseek may only
        // return tracks matching that hint rather than the whole album folder.
        if (query.Album.Length == 0)
            return false;

        // SearchHint can also become a file-level title filter when title conditions apply,
        // which means non-hint tracks may be filtered before folder grouping.
        if (search.NecessaryCond.StrictTitle || search.PreferredCond.StrictTitle)
            return false;

        return true;
    }

    private static int[] SortedAudioLengths(List<AlbumFolderFile> folderFiles)
        => folderFiles
            .Where(f => f.IsMusic)
            .Select(f => f.File.Length ?? -1)
            .OrderBy(x => x)
            .ToArray();

    private static string? RepresentativeAudioFilename(List<AlbumFolderFile> folderFiles)
        => folderFiles.FirstOrDefault(f => f.IsMusic).File?.Filename;

    private static List<SongJob> BuildAlbumFiles(List<AlbumFolderFile> folderFiles, SongQuery inferDefault)
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

    private static bool RequiredTrackTitlesSatisfy(List<string> requiredTrackTitles, List<AlbumFolderFile> files)
    {
        if (requiredTrackTitles.Count == 0)
            return true;

        var cond = new FileConditions { StrictTitle = true };
        foreach (string title in requiredTrackTitles)
        {
            bool found = false;
            foreach (var file in files)
            {
                if (cond.StrictTitleSatisfies(file.File.Filename, title))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }

    public static List<AlbumJob> AggregateAlbums(
        IEnumerable<AlbumFolder> albums,
        AlbumQuery query,
        SearchSettings search)
    {
        int maxDiff = search.AggregateLengthTol;

        bool LengthsAreSimilar(int[] s1, int[] s2)
        {
            for (int i = 0; i < s1.Length; i++)
                if (Math.Abs(s1[i] - s2[i]) > maxDiff) return false;
            return true;
        }

        var byTrackCountAndFirstLength = new Dictionary<int, Dictionary<int, List<AlbumAggregateBucket>>>();
        var buckets = new List<AlbumAggregateBucket>();
        var representativeQueries = new Dictionary<AlbumFolder, SongQuery?>();

        foreach (var folder in albums)
        {
            var sortedLengths = GetSearchSortedAudioLengths(folder);
            if (sortedLengths.Length == 0) continue;

            if (!byTrackCountAndFirstLength.TryGetValue(sortedLengths.Length, out var byFirstLength))
            {
                byFirstLength = [];
                byTrackCountAndFirstLength.Add(sortedLengths.Length, byFirstLength);
            }

            AlbumAggregateBucket? matchingBucket = null;
            int firstLengthBand = LengthBand(sortedLengths[0], maxDiff);
            for (int bandOffset = -1; bandOffset <= 1; bandOffset++)
            {
                if (!byFirstLength.TryGetValue(firstLengthBand + bandOffset, out var candidates))
                    continue;

                for (int i = 0; i < candidates.Count; i++)
                {
                    var bucket = candidates[i];
                    if (!LengthsAreSimilar(sortedLengths, bucket.Lengths)) continue;

                    if (sortedLengths.Length == 1 && !SingleTrackAlbumsMatch(bucket.Versions[0], folder, representativeQueries))
                        continue;

                    if (matchingBucket == null || bucket.Index < matchingBucket.Index)
                        matchingBucket = bucket;
                }
            }

            if (matchingBucket != null)
            {
                matchingBucket.Versions.Add(folder);
                matchingBucket.Users.Add(folder.Username);
                continue;
            }

            var newBucket = new AlbumAggregateBucket(buckets.Count, sortedLengths, folder);
            buckets.Add(newBucket);
            if (!byFirstLength.TryGetValue(firstLengthBand, out var byLength))
            {
                byLength = [];
                byFirstLength.Add(firstLengthBand, byLength);
            }
            byLength.Add(newBucket);
        }

        return buckets
            .Where(x => x.Users.Count >= search.MinSharesAggregate)
            .OrderByDescending(x => x.Users.Count)
            .Select(x =>
            {
                var repFolder = x.Versions.FirstOrDefault()?.FolderPath;
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
                newJob.Results = x.Versions;
                if (itemName != null)
                    newJob.ItemName = itemName;
                return newJob;
            })
            .ToList();
    }

    private static int LengthBand(int length, int maxDiff)
    {
        int bandSize = Math.Max(1, maxDiff + 1);
        return (int)Math.Floor(length / (double)bandSize);
    }

    private static int[] GetSearchSortedAudioLengths(AlbumFolder folder)
        => folder.HasSearchMetadata
            ? folder.SearchSortedAudioLengths
            : folder.Files
                .Where(f => !f.IsNotAudio)
                .Select(f => f.ResolvedTarget!.File.Length ?? -1)
                .OrderBy(x => x)
                .ToArray();

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

    private static void MergeChildDirectories(Dictionary<string, AlbumFolderBuilder> dirStructure)
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
                if (IsDescendantOrSamePrefix(key2, key))
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
                else if (!HasSortedPrefix(key2, key)) break;
            }
        }
        foreach (var key in toRemove) dirStructure.Remove(key);
    }

    private static bool IsDescendantOrSamePrefix(string possibleChild, string parent)
        => possibleChild.Length > parent.Length
            && possibleChild[parent.Length] == '\\'
            && possibleChild.StartsWith(parent, StringComparison.Ordinal);

    private static bool HasSortedPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.Ordinal);

    private sealed class AlbumFolderBuilder
    {
        public string Username { get; }
        public string FolderPath { get; }
        public List<AlbumFolderFile> Files { get; }
        public int FirstIndex { get; }
        public int MusicCount { get; private set; }

        public AlbumFolderBuilder(string username, string folderPath, AlbumFolderFile file, int firstIndex)
        {
            Username = username;
            FolderPath = folderPath;
            Files = [file];
            FirstIndex = firstIndex;
            MusicCount = file.IsMusic ? 1 : 0;
        }

        public void Add(AlbumFolderFile file)
        {
            Files.Add(file);
            if (file.IsMusic)
                MusicCount++;
        }

        public void AddRange(AlbumFolderBuilder other)
        {
            Files.AddRange(other.Files);
            MusicCount += other.MusicCount;
        }
    }

    private readonly record struct AlbumFolderFile(SlResponse Response, SlFile File, bool IsMusic);

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
                : Comparer<string>.Default.Compare(x.File.Filename, y.File.Filename);
        }
    }

    private sealed class AlbumAggregateBucket
    {
        public int Index { get; }
        public int[] Lengths { get; }
        public List<AlbumFolder> Versions { get; }
        public HashSet<string> Users { get; }

        public AlbumAggregateBucket(int index, int[] lengths, AlbumFolder folder)
        {
            Index = index;
            Lengths = lengths;
            Versions = [folder];
            Users = [folder.Username];
        }
    }

    private static bool SingleTrackAlbumsMatch(
        AlbumFolder a,
        AlbumFolder b,
        Dictionary<AlbumFolder, SongQuery?> representativeQueries)
    {
        SongQuery? q1 = RepresentativeAudioQuery(a, representativeQueries);
        SongQuery? q2 = RepresentativeAudioQuery(b, representativeQueries);
        if (q1 == null || q2 == null)
            return true;

        return (q2.Artist.ContainsIgnoreCase(q1.Artist) || q1.Artist.ContainsIgnoreCase(q2.Artist))
            && (q2.Title.ContainsIgnoreCase(q1.Title) || q1.Title.ContainsIgnoreCase(q2.Title));
    }

    private static SongQuery? RepresentativeAudioQuery(
        AlbumFolder folder,
        Dictionary<AlbumFolder, SongQuery?> representativeQueries)
    {
        if (representativeQueries.TryGetValue(folder, out var query))
            return query;

        string? filename = RepresentativeAudioFilename(folder);
        query = filename == null
            ? null
            : Searcher.InferSongQuery(filename, new SongQuery());
        representativeQueries.Add(folder, query);
        return query;
    }

    private static string? RepresentativeAudioFilename(AlbumFolder folder)
        => folder.HasSearchMetadata
            ? folder.SearchRepresentativeAudioFilename
            : folder.Files.FirstOrDefault(f => !f.IsNotAudio)?.ResolvedTarget?.Filename;

    [GeneratedRegex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$")]
    private static partial Regex DiscPatternRegex();
}
