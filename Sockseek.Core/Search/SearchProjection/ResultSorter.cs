using Sockseek.Core.Models;
using Soulseek;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public static partial class ResultSorter
{
    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<KeyValuePair<string, (SearchResponse, Soulseek.File)>> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = false,
        bool albumMode = false,
        bool ignoreStringSortConditions = false)
    {
        return OrderedResults(results.Select(x => x.Value), query, search, userSuccessCounts, useInfer, albumMode, ignoreStringSortConditions);
    }

    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = false,
        bool albumMode = false,
        bool ignoreStringSortConditions = false)
    {
        bool useBracketCheck = !albumMode;
        useInfer = false;

        return OrderedResultsCore(
            results,
            query,
            search,
            userSuccessCounts,
            useBracketCheck,
            useInfer,
            albumMode,
            ignoreStringSortConditions);
    }

    private static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResultsCore(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useBracketCheck,
        bool useInfer,
        bool albumMode,
        bool ignoreStringSortConditions)
    {
        var keyContext = new SortKeyContext(
            results,
            query,
            search,
            userSuccessCounts,
            useBracketCheck,
            useInfer,
            albumMode,
            ignoreStringSortConditions);
        var sortableResults = keyContext.SortableResults;
        int capacity = sortableResults.TryGetNonEnumeratedCount(out int resultCount) ? resultCount : 0;
        List<SortEntry> entries = capacity > 0 ? new List<SortEntry>(capacity) : new List<SortEntry>();
        int index = 0;
        foreach (var (response, file) in sortableResults)
        {
            var entry = CreateSortEntry(response, file, keyContext, index++);
            if (entry.HasValue)
                entries.Add(entry.Value);
        }

        entries.Sort(SortEntryComparer.Instance);

        return entries.Select(x => (x.Response, x.File));
    }

    internal static SortKeyContext CreateSortKeyContext(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useBracketCheck,
        bool useInfer,
        bool albumMode,
        bool ignoreStringSortConditions = false)
        => new(results, query, search, userSuccessCounts, useBracketCheck, useInfer, albumMode, ignoreStringSortConditions);

    internal static SortEntry? CreateSortEntry(
        SearchResponse response,
        Soulseek.File file,
        SortKeyContext keyContext,
        int originalIndex)
    {
        if (keyContext.UserSuccessCounts.GetValueOrDefault(response.Username, 0) <= keyContext.Search.IgnoreOn)
            return null;

        return new SortEntry(
            response,
            file,
            keyContext.CreateKey(response, file),
            originalIndex);
    }

    private static Dictionary<(string Username, string Filename), InferredResultGroup> GetInferredQueries(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search)
    {
        var comparer = new SongQueryComparer(ignoreCase: true, search.AggregateLengthTol);
        var groups = new Dictionary<SongQuery, InferredResultGroup>(comparer);
        var inferredByFilename = new Dictionary<string, SongQuery>();
        var inferredQueries = new Dictionary<(string Username, string Filename), InferredResultGroup>();

        foreach (var (response, file) in results)
        {
            if (!inferredByFilename.TryGetValue(file.Filename, out var inferred))
            {
                inferred = Searcher.InferSongQuery(file.Filename, query);
                inferredByFilename.Add(file.Filename, inferred);
            }

            var key = new SongQuery(inferred) { Length = file.Length ?? -1 };

            if (!groups.TryGetValue(key, out var group))
            {
                group = new InferredResultGroup(key);
                groups.Add(key, group);
            }

            group.Count++;
            inferredQueries[(response.Username, file.Filename)] = group;
        }

        return inferredQueries;
    }

    // TODO [PERFORMANCE]: Fix O(N) latency spike in Lazy inferred queries evaluation.
    // infQueriesAndCounts is evaluated the first time ANY item needs an inferred track count 
    // to break a tie. Because GetInferredQueries processes the *entire* resultList at once, 
    // this causes a massive latency spike during the sort. 
    // Fix: Instead of grouping all results upfront, infer the query on-demand per file and 
    // cache the result in a thread-safe dictionary, or restructure the sort so inference 
    // is only performed on the subset of items that actually tie on higher-level flags.
    //
    // But first: Check if inferred track count ranking is even used. Remove if not.
    internal sealed class SortKeyContext
    {
        private readonly Lazy<Dictionary<(string Username, string Filename), InferredResultGroup>>? infQueriesAndCounts;
        private readonly string strictTitle;
        private readonly string strictArtist;
        private readonly string strictAlbum;
        private readonly string fuzzyTitle;
        private readonly string fuzzyArtist;
        private readonly string fuzzyAlbum;
        private readonly FileConditions necessaryCond;
        private readonly FileConditions preferredCond;
        private readonly SongQuery emptyQuery = new();
        private readonly bool queryTitleAllowsBrackets;
        private readonly bool ignoreStringSortConditions;
        private Dictionary<string, string>? strictDirectoryNames;
        private Dictionary<string, string>? fuzzyDirectoryNames;

        public SortKeyContext(
            IEnumerable<(SearchResponse, Soulseek.File)> results,
            SongQuery query,
            SearchSettings search,
            ConcurrentDictionary<string, int> userSuccessCounts,
            bool useBracketCheck,
            bool useInfer,
            bool albumMode,
            bool ignoreStringSortConditions)
        {
            Query = query;
            Search = search;
            UserSuccessCounts = userSuccessCounts;
            UseBracketCheck = useBracketCheck;
            UseInfer = useInfer;
            AlbumMode = albumMode;
            this.ignoreStringSortConditions = ignoreStringSortConditions;

            var resultList = useInfer ? results.ToList() : null;
            SortableResults = resultList ?? results;
            infQueriesAndCounts = useInfer
                ? new Lazy<Dictionary<(string Username, string Filename), InferredResultGroup>>(
                    () => GetInferredQueries(resultList!, query, search))
                : null;
            strictTitle = FileConditions.StrictStringPreprocess(query.Title, removeSlash: true);
            strictArtist = FileConditions.StrictStringPreprocess(query.Artist, removeSlash: true);
            strictAlbum = FileConditions.StrictStringPreprocess(query.Album, removeSlash: true);
            fuzzyTitle = FileConditions.FuzzyPhrasePreprocess(query.Title);
            fuzzyArtist = FileConditions.FuzzyPhrasePreprocess(query.Artist);
            fuzzyAlbum = FileConditions.FuzzyPhrasePreprocess(query.Album);
            necessaryCond = ignoreStringSortConditions
                ? WithoutStringConditions(search.NecessaryCond)
                : search.NecessaryCond;
            preferredCond = ignoreStringSortConditions
                ? WithoutStringConditions(search.PreferredCond)
                : search.PreferredCond;
            queryTitleAllowsBrackets = query.Title.RemoveFt().Replace('[', '(').Contains('(');
        }

        public IEnumerable<(SearchResponse, Soulseek.File)> SortableResults { get; }
        public SongQuery Query { get; }
        public SearchSettings Search { get; }
        public ConcurrentDictionary<string, int> UserSuccessCounts { get; }
        public bool UseBracketCheck { get; }
        public bool UseInfer { get; }
        public bool AlbumMode { get; }

        public SortKey CreateKey(SearchResponse response, Soulseek.File file)
        {
            (SongQuery Query, int Count)? inferred = null;
            (SongQuery Query, int Count) getInferred() => inferred ??= InferredQuery(response, file);

            string filename = file.Filename;
            string? strictFullFilename = null;
            string? strictFilenameNoExt = null;
            string? strictDirectoryName = null;
            string? fuzzyFullFilename = null;
            string? fuzzyFilenameNoExt = null;
            string? fuzzyDirectoryName = null;
            string getStrictFullFilename() => strictFullFilename ??= FileConditions.StrictStringPreprocess(filename);
            string getStrictFilenameNoExt() => strictFilenameNoExt ??= FileConditions.StrictStringPreprocess(Utils.GetFileNameWithoutExtSlsk(filename));
            string getStrictDirectoryName() => strictDirectoryName ??= StrictDirectoryName(filename);
            string getFuzzyFullFilename() => fuzzyFullFilename ??= FileConditions.FuzzyPhrasePreprocess(filename);
            string getFuzzyFilenameNoExt() => fuzzyFilenameNoExt ??= FileConditions.FuzzyPhrasePreprocess(Utils.GetFileNameWithoutExtSlsk(filename));
            string getFuzzyDirectoryName() => fuzzyDirectoryName ??= FuzzyDirectoryName(filename);

            bool strictTitleMatch = ignoreStringSortConditions || !preferredCond.StrictTitle || strictTitle.Length == 0
                || StrictStringPrepared(getStrictFilenameNoExt(), strictTitle);
            bool fuzzyTitleMatch = ignoreStringSortConditions || !preferredCond.StrictTitle || fuzzyTitle.Length == 0 || strictTitleMatch
                || FuzzyPhrasePrepared(getFuzzyFilenameNoExt(), fuzzyTitle);
            bool strictAlbumMatch = ignoreStringSortConditions || !preferredCond.StrictAlbum || strictAlbum.Length == 0
                || StrictStringPrepared(getStrictDirectoryName(), strictAlbum);
            bool fuzzyAlbumMatch = ignoreStringSortConditions || !preferredCond.StrictAlbum || fuzzyAlbum.Length == 0 || strictAlbumMatch
                || FuzzyPhrasePrepared(getFuzzyDirectoryName(), fuzzyAlbum);
            bool strictArtistMatch = ignoreStringSortConditions || !preferredCond.StrictArtist || strictArtist.Length == 0
                || StrictStringPrepared(getStrictFullFilename(), strictArtist, boundarySkipWs: false);
            bool fuzzyArtistMatch = ignoreStringSortConditions || !preferredCond.StrictArtist || fuzzyArtist.Length == 0 || strictArtistMatch
                || FuzzyPhrasePrepared(getFuzzyFullFilename(), fuzzyArtist, boundarySkipWs: false);

            bool lengthToleranceMatch = preferredCond.LengthToleranceSatisfies(file, Query.Length);
            bool formatMatch = preferredCond.FormatSatisfies(filename);
            bool bitrateMatch = preferredCond.BitrateSatisfies(file);
            bool sampleRateMatch = preferredCond.SampleRateSatisfies(file);
            bool bitDepthMatch = preferredCond.BitDepthSatisfies(file);
            bool preferredUserConditionsMet = preferredCond.UserSatisfies(response);

            return new SortKey(
                UserSuccessCounts.GetValueOrDefault(response.Username, 0) > Search.DownrankOn,
                ConditionSatisfactionPolicy.SearchFileSatisfies(necessaryCond, response, file, Query),
                preferredUserConditionsMet,
                (file.Length != null && file.Length > 0) || Search.PreferredCond.AcceptNoLength,
                !UseBracketCheck || CheapBracketCheck(queryTitleAllowsBrackets, filename),
                strictTitleMatch,
                fuzzyTitleMatch,
                strictAlbumMatch,
                fuzzyAlbumMatch,
                strictArtistMatch,
                fuzzyArtistMatch,
                lengthToleranceMatch,
                formatMatch,
                bitrateMatch,
                sampleRateMatch,
                bitDepthMatch,
                formatMatch
                    && lengthToleranceMatch
                    && bitrateMatch
                    && sampleRateMatch
                    && strictTitleMatch
                    && strictArtistMatch
                    && strictAlbumMatch
                    && preferredUserConditionsMet
                    && bitDepthMatch,
                response.HasFreeUploadSlot,
                response.UploadSpeed / 1024 / 650,
                ignoreStringSortConditions || AlbumMode || strictTitle.Length == 0 || StrictStringPrepared(getStrictFullFilename(), strictTitle),
                ignoreStringSortConditions || !AlbumMode || strictAlbum.Length == 0 || StrictStringPrepared(getStrictDirectoryName(), strictAlbum),
                ignoreStringSortConditions || strictArtist.Length == 0 || StrictStringPrepared(getStrictFullFilename(), strictArtist, boundarySkipWs: false),
                UseInfer ? getInferred().Count : 0,
                response.UploadSpeed / 1024 / 350,
                (file.BitRate ?? 0) / 80,
                StableTieBreaker(response.Username, filename));
        }

        private static FileConditions WithoutStringConditions(FileConditions conditions)
            => new(conditions)
            {
                StrictTitle = false,
                StrictArtist = false,
                StrictAlbum = false,
            };

        private string StrictDirectoryName(string filename)
        {
            string directory = GetDirectoryNameSlskFast(filename);
            strictDirectoryNames ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (!strictDirectoryNames.TryGetValue(directory, out string? prepared))
            {
                prepared = FileConditions.StrictStringPreprocess(directory);
                strictDirectoryNames.Add(directory, prepared);
            }

            return prepared;
        }

        private string FuzzyDirectoryName(string filename)
        {
            string directory = GetDirectoryNameSlskFast(filename);
            fuzzyDirectoryNames ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (!fuzzyDirectoryNames.TryGetValue(directory, out string? prepared))
            {
                prepared = FileConditions.FuzzyPhrasePreprocess(directory);
                fuzzyDirectoryNames.Add(directory, prepared);
            }

            return prepared;
        }

        private static string GetDirectoryNameSlskFast(string filename)
        {
            int slash = filename.LastIndexOf('\\');
            int forwardSlash = filename.LastIndexOf('/');
            int index = Math.Max(slash, forwardSlash);
            return index <= 0 ? string.Empty : filename[..index];
        }

        private (SongQuery, int) InferredQuery(SearchResponse response, Soulseek.File file)
        {
            var key = (response.Username, file.Filename);
            if (infQueriesAndCounts != null && infQueriesAndCounts.Value.TryGetValue(key, out var inferred))
                return (inferred.Query, inferred.Count);
            return (emptyQuery, 0);
        }

        private static bool StrictStringPrepared(string fname, string tname, bool boundarySkipWs = true)
        {
            if (tname.Length == 0)
                return true;

            if (boundarySkipWs)
                return fname.ContainsWithBoundaryIgnoreWs(tname, ignoreCase: true, acceptLeftDigit: true);

            return fname.ContainsWithBoundary(tname, ignoreCase: true);
        }

        private static bool FuzzyPhrasePrepared(string fname, string tname, bool boundarySkipWs = true)
        {
            if (tname.Length == 0)
                return true;

            return fname.ContainsWithBoundary(tname, ignoreCase: true);
        }
    }

    internal readonly record struct SortEntry(
        SearchResponse Response,
        Soulseek.File File,
        SortKey Key,
        int OriginalIndex);

    internal sealed class SortEntryComparer : IComparer<SortEntry>
    {
        public static readonly SortEntryComparer Instance = new();

        private SortEntryComparer()
        {
        }

        public int Compare(SortEntry x, SortEntry y)
        {
            int comparison = y.Key.CompareTo(x.Key);
            return comparison != 0
                ? comparison
                : x.OriginalIndex.CompareTo(y.OriginalIndex);
        }
    }

    internal sealed class AlbumBeforeQualitySortEntryComparer : IComparer<SortEntry>
    {
        public static readonly AlbumBeforeQualitySortEntryComparer Instance = new();

        private AlbumBeforeQualitySortEntryComparer()
        {
        }

        public int Compare(SortEntry x, SortEntry y)
            => y.Key.CompareAlbumBeforeQualityTo(x.Key);
    }

    internal readonly struct SortKey : IComparable<SortKey>
    {
        private readonly uint highFlags;
        private readonly uint midFlags;
        private readonly int uploadSpeedFast;
        private readonly int inferredTrackCount;
        private readonly int uploadSpeedMedium;
        private readonly int bitRate;
        private readonly int randomTiebreaker;
        private readonly uint albumBeforeQualityFlags;

        public SortKey(
            bool userSuccessAboveDownrank,
            bool necessaryConditionsMet,
            bool preferredUserConditionsMet,
            bool hasValidLength,
            bool bracketCheckPassed,
            bool strictTitleMatch,
            bool fuzzyTitleMatch,
            bool strictAlbumMatch,
            bool fuzzyAlbumMatch,
            bool strictArtistMatch,
            bool fuzzyArtistMatch,
            bool lengthToleranceMatch,
            bool formatMatch,
            bool bitrateMatch,
            bool sampleRateMatch,
            bool bitDepthMatch,
            bool fileSatisfies,
            bool hasFreeUploadSlot,
            int uploadSpeedFast,
            bool nonAlbumModeStrictString,
            bool albumModeStrictString,
            bool strictArtistString,
            int inferredTrackCount,
            int uploadSpeedMedium,
            int bitRate,
            int randomTiebreaker)
        {
            highFlags = PackHighFlags(
                userSuccessAboveDownrank,
                necessaryConditionsMet,
                preferredUserConditionsMet,
                hasValidLength,
                bracketCheckPassed,
                strictTitleMatch,
                fuzzyTitleMatch,
                strictAlbumMatch,
                fuzzyAlbumMatch,
                strictArtistMatch,
                fuzzyArtistMatch,
                lengthToleranceMatch,
                formatMatch,
                bitrateMatch,
                sampleRateMatch,
                bitDepthMatch,
                fileSatisfies,
                hasFreeUploadSlot);
            this.uploadSpeedFast = uploadSpeedFast;
            midFlags = PackMidFlags(nonAlbumModeStrictString, albumModeStrictString, strictArtistString);
            this.inferredTrackCount = inferredTrackCount;
            this.uploadSpeedMedium = uploadSpeedMedium;
            this.bitRate = bitRate;
            this.randomTiebreaker = randomTiebreaker;
            albumBeforeQualityFlags = PackAlbumBeforeQualityFlags(
                userSuccessAboveDownrank,
                necessaryConditionsMet,
                preferredUserConditionsMet,
                hasValidLength,
                bracketCheckPassed,
                strictTitleMatch,
                fuzzyTitleMatch,
                strictAlbumMatch,
                fuzzyAlbumMatch,
                strictArtistMatch,
                fuzzyArtistMatch,
                lengthToleranceMatch);
        }

        internal int CompareAlbumBeforeQualityTo(SortKey other)
            => albumBeforeQualityFlags.CompareTo(other.albumBeforeQualityFlags);

        public int CompareTo(SortKey other)
        {
            int comparison = highFlags.CompareTo(other.highFlags);
            if (comparison != 0) return comparison;

            comparison = uploadSpeedFast.CompareTo(other.uploadSpeedFast);
            if (comparison != 0) return comparison;

            comparison = midFlags.CompareTo(other.midFlags);
            if (comparison != 0) return comparison;

            comparison = inferredTrackCount.CompareTo(other.inferredTrackCount);
            if (comparison != 0) return comparison;

            comparison = uploadSpeedMedium.CompareTo(other.uploadSpeedMedium);
            if (comparison != 0) return comparison;

            comparison = bitRate.CompareTo(other.bitRate);
            if (comparison != 0) return comparison;

            return randomTiebreaker.CompareTo(other.randomTiebreaker);
        }

        private static uint PackHighFlags(
            bool userSuccessAboveDownrank,
            bool necessaryConditionsMet,
            bool preferredUserConditionsMet,
            bool hasValidLength,
            bool bracketCheckPassed,
            bool strictTitleMatch,
            bool fuzzyTitleMatch,
            bool strictAlbumMatch,
            bool fuzzyAlbumMatch,
            bool strictArtistMatch,
            bool fuzzyArtistMatch,
            bool lengthToleranceMatch,
            bool formatMatch,
            bool bitrateMatch,
            bool sampleRateMatch,
            bool bitDepthMatch,
            bool fileSatisfies,
            bool hasFreeUploadSlot)
        {
            return BoolSortKey.CreateDescending()
                .Then(userSuccessAboveDownrank)
                .Then(necessaryConditionsMet)
                .Then(preferredUserConditionsMet)
                .Then(hasValidLength)
                .Then(bracketCheckPassed)
                .Then(strictTitleMatch)
                .Then(fuzzyTitleMatch)
                // Identity beats quality preferences: an MP3 from the requested album is a better
                // candidate than a FLAC from an unrelated album.
                .Then(strictAlbumMatch)
                .Then(fuzzyAlbumMatch)
                .Then(strictArtistMatch)
                .Then(fuzzyArtistMatch)
                .Then(lengthToleranceMatch)
                .Then(formatMatch)
                .Then(bitrateMatch)
                .Then(sampleRateMatch)
                .Then(bitDepthMatch)
                .Then(fileSatisfies)
                .Then(hasFreeUploadSlot)
                .Value;
        }

        private static uint PackAlbumBeforeQualityFlags(
            bool userSuccessAboveDownrank,
            bool necessaryConditionsMet,
            bool preferredUserConditionsMet,
            bool hasValidLength,
            bool bracketCheckPassed,
            bool strictTitleMatch,
            bool fuzzyTitleMatch,
            bool strictAlbumMatch,
            bool fuzzyAlbumMatch,
            bool strictArtistMatch,
            bool fuzzyArtistMatch,
            bool lengthToleranceMatch)
        {
            return BoolSortKey.CreateDescending()
                .Then(userSuccessAboveDownrank)
                .Then(necessaryConditionsMet)
                .Then(preferredUserConditionsMet)
                .Then(hasValidLength)
                .Then(bracketCheckPassed)
                .Then(strictTitleMatch)
                .Then(fuzzyTitleMatch)
                .Then(strictAlbumMatch)
                .Then(fuzzyAlbumMatch)
                .Then(strictArtistMatch)
                .Then(fuzzyArtistMatch)
                .Then(lengthToleranceMatch)
                .Value;
        }

        private static uint PackMidFlags(
            bool nonAlbumModeStrictString,
            bool albumModeStrictString,
            bool strictArtistString)
        {
            return BoolSortKey.CreateDescending()
                .Then(nonAlbumModeStrictString)
                .Then(albumModeStrictString)
                .Then(strictArtistString)
                .Value;
        }
    }

    private readonly struct BoolSortKey
    {
        private readonly uint value;
        private readonly int nextBit;

        private BoolSortKey(uint value, int nextBit)
        {
            this.value = value;
            this.nextBit = nextBit;
        }

        public uint Value => value;

        // Earlier booleans get higher bits, so uint comparison is equivalent
        // to comparing each boolean in the order the caller lists them.
        public static BoolSortKey CreateDescending()
            => new(0, 31);

        public BoolSortKey Then(bool preferred)
            => new(preferred ? value | (1u << nextBit) : value, nextBit - 1);
    }

    private static int StableTieBreaker(string username, string filename)
    {
        unchecked
        {
            uint hash = 2166136261;
            for (int i = 0; i < username.Length; i++)
                hash = (hash ^ username[i]) * 16777619;

            hash = (hash ^ 0) * 16777619;

            for (int i = 0; i < filename.Length; i++)
                hash = (hash ^ filename[i]) * 16777619;

            return (int)(hash & 0x7fffffff);
        }
    }

    public static bool CheapBracketCheck(SongQuery query, string filename)
    {
        bool queryTitleAllowsBrackets = query.Title.RemoveFt().Replace('[', '(').Contains('(');
        return CheapBracketCheck(queryTitleAllowsBrackets, filename);
    }

    private static bool CheapBracketCheck(bool queryTitleAllowsBrackets, string filename)
    {
        if (queryTitleAllowsBrackets)
            return true;

        string name = Utils.GetFileNameWithoutExtSlsk(filename);
        if (!name.Contains('(') && !name.Contains('['))
            return true;

        name = LeadingBracketTrackNumberRegex().Replace(name, "", 1);
        name = name.RemoveFt();
        return !name.Contains('(') && !name.Contains('[');
    }

    [GeneratedRegex(@"^\s*[\(\[]\s*\d{1,3}(?:\s*[-./]\s*\d{1,3})?\s*[\)\]]\s*")]
    private static partial Regex LeadingBracketTrackNumberRegex();

    private sealed class InferredResultGroup(SongQuery query)
    {
        public SongQuery Query { get; } = query;
        public int Count { get; set; }
    }
}
