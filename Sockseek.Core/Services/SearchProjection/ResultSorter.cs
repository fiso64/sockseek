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
        bool useLevenshtein = true,
        bool albumMode = false)
    {
        return OrderedResults(results.Select(x => x.Value), query, search, userSuccessCounts, useInfer, useLevenshtein, albumMode);
    }

    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = false,
        bool useLevenshtein = true,
        bool albumMode = false)
    {
        bool useBracketCheck = !albumMode;
        useInfer = false;
        useLevenshtein = false;

        return OrderedResultsCore(
            results,
            query,
            search,
            userSuccessCounts,
            useBracketCheck,
            useInfer,
            useLevenshtein,
            albumMode);
    }

    private static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResultsCore(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useBracketCheck,
        bool useInfer,
        bool useLevenshtein,
        bool albumMode)
    {
        var keyContext = new SortKeyContext(
            results,
            query,
            search,
            userSuccessCounts,
            useBracketCheck,
            useInfer,
            useLevenshtein,
            albumMode);
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
        bool useLevenshtein,
        bool albumMode)
        => new(results, query, search, userSuccessCounts, useBracketCheck, useInfer, useLevenshtein, albumMode);

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
        private readonly SongQuery emptyQuery = new();
        private readonly bool queryTitleAllowsBrackets;
        private string? normalizedQueryTitle;
        private Dictionary<string, int>? levenshteinScores;

        public SortKeyContext(
            IEnumerable<(SearchResponse, Soulseek.File)> results,
            SongQuery query,
            SearchSettings search,
            ConcurrentDictionary<string, int> userSuccessCounts,
            bool useBracketCheck,
            bool useInfer,
            bool useLevenshtein,
            bool albumMode)
        {
            Query = query;
            Search = search;
            UserSuccessCounts = userSuccessCounts;
            UseBracketCheck = useBracketCheck;
            UseInfer = useInfer;
            UseLevenshtein = useLevenshtein;
            AlbumMode = albumMode;

            var resultList = useInfer ? results.ToList() : null;
            SortableResults = resultList ?? results;
            infQueriesAndCounts = useInfer
                ? new Lazy<Dictionary<(string Username, string Filename), InferredResultGroup>>(
                    () => GetInferredQueries(resultList!, query, search))
                : null;
            strictTitle = FileConditions.StrictStringPreprocess(query.Title);
            strictArtist = FileConditions.StrictStringPreprocess(query.Artist);
            strictAlbum = FileConditions.StrictStringPreprocess(query.Album);
            queryTitleAllowsBrackets = query.Title.RemoveFt().Replace('[', '(').Contains('(');
        }

        public IEnumerable<(SearchResponse, Soulseek.File)> SortableResults { get; }
        public SongQuery Query { get; }
        public SearchSettings Search { get; }
        public ConcurrentDictionary<string, int> UserSuccessCounts { get; }
        public bool UseBracketCheck { get; }
        public bool UseInfer { get; }
        public bool UseLevenshtein { get; }
        public bool AlbumMode { get; }

        public SortKey CreateKey(SearchResponse response, Soulseek.File file)
        {
            (SongQuery Query, int Count)? inferred = null;
            (SongQuery Query, int Count) getInferred() => inferred ??= InferredQuery(response, file);

            string filename = file.Filename;
            string? strictFullFilename = null;
            string? strictFilenameNoExt = null;
            string? strictDirectoryName = null;
            string getStrictFullFilename() => strictFullFilename ??= FileConditions.StrictStringPreprocess(filename);
            string getStrictFilenameNoExt() => strictFilenameNoExt ??= FileConditions.StrictStringPreprocess(Utils.GetFileNameWithoutExtSlsk(filename));
            string getStrictDirectoryName() => strictDirectoryName ??= FileConditions.StrictStringPreprocess(Utils.GetDirectoryNameSlsk(filename));

            bool strictTitleMatch = !Search.PreferredCond.StrictTitle
                || StrictStringPrepared(getStrictFilenameNoExt(), strictTitle);
            bool strictAlbumMatch = !Search.PreferredCond.StrictAlbum
                || StrictStringPrepared(getStrictDirectoryName(), strictAlbum);
            bool preferredStrictArtistMatch = !Search.PreferredCond.StrictArtist
                || StrictStringPrepared(getStrictFullFilename(), strictArtist, boundarySkipWs: false);
            bool strictArtistMatch = !Search.PreferredCond.StrictArtist
                || StrictStringPrepared(getStrictFullFilename(), strictArtist, boundarySkipWs: false);

            bool lengthToleranceMatch = Search.PreferredCond.LengthToleranceSatisfies(file, Query.Length);
            bool formatMatch = Search.PreferredCond.FormatSatisfies(filename);
            bool bitrateMatch = Search.PreferredCond.BitrateSatisfies(file);
            bool sampleRateMatch = Search.PreferredCond.SampleRateSatisfies(file);
            bool bitDepthMatch = Search.PreferredCond.BitDepthSatisfies(file);
            bool preferredUserConditionsMet = Search.PreferredCond.UserSatisfies(response);

            return new SortKey(
                UserSuccessCounts.GetValueOrDefault(response.Username, 0) > Search.DownrankOn,
                Search.NecessaryCond.FileSatisfies(file, Query, response),
                preferredUserConditionsMet,
                (file.Length != null && file.Length > 0) || Search.PreferredCond.AcceptNoLength,
                !UseBracketCheck || CheapBracketCheck(queryTitleAllowsBrackets, filename),
                strictTitleMatch,
                !AlbumMode || strictAlbumMatch,
                strictArtistMatch,
                lengthToleranceMatch,
                formatMatch,
                AlbumMode || strictAlbumMatch,
                bitrateMatch,
                sampleRateMatch,
                bitDepthMatch,
                formatMatch
                    && lengthToleranceMatch
                    && bitrateMatch
                    && sampleRateMatch
                    && strictTitleMatch
                    && preferredStrictArtistMatch
                    && strictAlbumMatch
                    && preferredUserConditionsMet
                    && bitDepthMatch,
                response.HasFreeUploadSlot,
                response.UploadSpeed / 1024 / 650,
                AlbumMode || StrictStringPrepared(getStrictFullFilename(), strictTitle),
                !AlbumMode || StrictStringPrepared(getStrictDirectoryName(), strictAlbum),
                StrictStringPrepared(getStrictFullFilename(), strictArtist, boundarySkipWs: false),
                UseInfer ? getInferred().Count : 0,
                response.UploadSpeed / 1024 / 350,
                (file.BitRate ?? 0) / 80,
                UseLevenshtein ? LevenshteinScore(getInferred().Query) : 0,
                StableTieBreaker(response.Username, filename));
        }

        private (SongQuery, int) InferredQuery(SearchResponse response, Soulseek.File file)
        {
            var key = (response.Username, file.Filename);
            if (infQueriesAndCounts != null && infQueriesAndCounts.Value.TryGetValue(key, out var inferred))
                return (inferred.Query, inferred.Count);
            return (emptyQuery, 0);
        }

        // TODO: Check if this is ever used (i.e. if UseLevenshtein can even be true)
        private int LevenshteinScore(SongQuery inferred)
        {
            if (string.Equals(Query.Title, inferred.Title, StringComparison.OrdinalIgnoreCase))
                return 0;

            string normalizedInferredTitle = NormalizeLevenshteinTitle(inferred.Title);
            normalizedQueryTitle ??= NormalizeLevenshteinTitle(Query.Title);
            levenshteinScores ??= new Dictionary<string, int>(StringComparer.Ordinal);
            if (!levenshteinScores.TryGetValue(normalizedInferredTitle, out int score))
            {
                score = CalculateLevenshtein(normalizedQueryTitle, normalizedInferredTitle) / 5;
                levenshteinScores.Add(normalizedInferredTitle, score);
            }

            return score;
        }

        private static bool StrictStringPrepared(string fname, string tname, bool boundarySkipWs = true)
        {
            if (tname.Length == 0)
                return true;

            if (boundarySkipWs)
                return fname.ContainsWithBoundaryIgnoreWs(tname, ignoreCase: true, acceptLeftDigit: true);

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

    internal readonly struct SortKey : IComparable<SortKey>
    {
        private readonly uint highFlags;
        private readonly uint midFlags;
        private readonly int uploadSpeedFast;
        private readonly int inferredTrackCount;
        private readonly int uploadSpeedMedium;
        private readonly int bitRate;
        private readonly int levenshteinScore;
        private readonly int randomTiebreaker;

        public SortKey(
            bool userSuccessAboveDownrank,
            bool necessaryConditionsMet,
            bool preferredUserConditionsMet,
            bool hasValidLength,
            bool bracketCheckPassed,
            bool strictTitleMatch,
            bool albumModeStrictAlbumMatch,
            bool strictArtistMatch,
            bool lengthToleranceMatch,
            bool formatMatch,
            bool nonAlbumModeStrictAlbumMatch,
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
            int levenshteinScore,
            int randomTiebreaker)
        {
            highFlags = PackHighFlags(
                userSuccessAboveDownrank,
                necessaryConditionsMet,
                preferredUserConditionsMet,
                hasValidLength,
                bracketCheckPassed,
                strictTitleMatch,
                albumModeStrictAlbumMatch,
                strictArtistMatch,
                lengthToleranceMatch,
                formatMatch,
                nonAlbumModeStrictAlbumMatch,
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
            this.levenshteinScore = levenshteinScore;
            this.randomTiebreaker = randomTiebreaker;
        }

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

            comparison = levenshteinScore.CompareTo(other.levenshteinScore);
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
            bool albumModeStrictAlbumMatch,
            bool strictArtistMatch,
            bool lengthToleranceMatch,
            bool formatMatch,
            bool nonAlbumModeStrictAlbumMatch,
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
                .Then(albumModeStrictAlbumMatch)
                .Then(strictArtistMatch)
                .Then(lengthToleranceMatch)
                .Then(formatMatch)
                .Then(nonAlbumModeStrictAlbumMatch)
                .Then(bitrateMatch)
                .Then(sampleRateMatch)
                .Then(bitDepthMatch)
                .Then(fileSatisfies)
                .Then(hasFreeUploadSlot)
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

    private static int CalculateLevenshtein(string normalizedQueryTitle, string normalizedInferredTitle)
        => normalizedQueryTitle == normalizedInferredTitle
            ? 0
            : Utils.Levenshtein(normalizedQueryTitle, normalizedInferredTitle);

    private static string NormalizeLevenshteinTitle(string title)
        => title.RemoveFt().ReplaceSpecialChars("").Replace(" ", "").Replace("_", "").ToLower();

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

// TODO: Delete this legacy sorter key once ResultSorterTests cover SortKey or OrderedResults directly.
public class SortingCriteria : IComparable<SortingCriteria>
{
    public bool UserSuccessAboveDownrank;
    public bool NecessaryConditionsMet;
    public bool PreferredUserConditionsMet;
    public bool HasValidLength;
    public bool BracketCheckPassed;
    public bool StrictTitleMatch;
    public bool AlbumModeStrictAlbumMatch;
    public bool StrictArtistMatch;
    public bool LengthToleranceMatch;
    public bool FormatMatch;
    public bool NonAlbumModeStrictAlbumMatch;
    public bool BitrateMatch;
    public bool SampleRateMatch;
    public bool BitDepthMatch;
    public bool FileSatisfies;
    public bool HasFreeUploadSlot;
    public bool NoQueue;
    public int  UploadSpeedFast;
    public bool NonAlbumModeStrictString;
    public bool AlbumModeStrictString;
    public bool StrictArtistString;
    private int? _inferredTrackCount;
    public Func<int>? InferredTrackCountFactory;
    public int InferredTrackCount
    {
        get
        {
            _inferredTrackCount ??= InferredTrackCountFactory?.Invoke() ?? 0;
            return _inferredTrackCount.Value;
        }
        set
        {
            _inferredTrackCount = value;
            InferredTrackCountFactory = null;
        }
    }
    public int  UploadSpeedMedium;
    public int  BitRate;
    private int? _levenshteinScore;
    public Func<int>? LevenshteinScoreFactory;
    public int LevenshteinScore
    {
        get
        {
            _levenshteinScore ??= LevenshteinScoreFactory?.Invoke() ?? 0;
            return _levenshteinScore.Value;
        }
        set
        {
            _levenshteinScore = value;
            LevenshteinScoreFactory = null;
        }
    }
    public int  RandomTiebreaker;

    public int CompareTo(SortingCriteria? other)
    {
        if (other == null) return 1;

        int comparison;

        comparison = UserSuccessAboveDownrank.CompareTo(other.UserSuccessAboveDownrank);
        if (comparison != 0) return comparison;

        comparison = NecessaryConditionsMet.CompareTo(other.NecessaryConditionsMet);
        if (comparison != 0) return comparison;

        comparison = PreferredUserConditionsMet.CompareTo(other.PreferredUserConditionsMet);
        if (comparison != 0) return comparison;

        comparison = HasValidLength.CompareTo(other.HasValidLength);
        if (comparison != 0) return comparison;

        comparison = BracketCheckPassed.CompareTo(other.BracketCheckPassed);
        if (comparison != 0) return comparison;

        comparison = StrictTitleMatch.CompareTo(other.StrictTitleMatch);
        if (comparison != 0) return comparison;

        comparison = AlbumModeStrictAlbumMatch.CompareTo(other.AlbumModeStrictAlbumMatch);
        if (comparison != 0) return comparison;

        comparison = StrictArtistMatch.CompareTo(other.StrictArtistMatch);
        if (comparison != 0) return comparison;

        comparison = LengthToleranceMatch.CompareTo(other.LengthToleranceMatch);
        if (comparison != 0) return comparison;

        comparison = FormatMatch.CompareTo(other.FormatMatch);
        if (comparison != 0) return comparison;

        comparison = NonAlbumModeStrictAlbumMatch.CompareTo(other.NonAlbumModeStrictAlbumMatch);
        if (comparison != 0) return comparison;

        comparison = BitrateMatch.CompareTo(other.BitrateMatch);
        if (comparison != 0) return comparison;

        comparison = SampleRateMatch.CompareTo(other.SampleRateMatch);
        if (comparison != 0) return comparison;

        comparison = BitDepthMatch.CompareTo(other.BitDepthMatch);
        if (comparison != 0) return comparison;

        comparison = FileSatisfies.CompareTo(other.FileSatisfies);
        if (comparison != 0) return comparison;

        comparison = HasFreeUploadSlot.CompareTo(other.HasFreeUploadSlot);
        if (comparison != 0) return comparison;

        //comparison = NoQueue.CompareTo(other.NoQueue);
        //if (comparison != 0) return comparison;

        comparison = UploadSpeedFast.CompareTo(other.UploadSpeedFast);
        if (comparison != 0) return comparison;

        comparison = NonAlbumModeStrictString.CompareTo(other.NonAlbumModeStrictString);
        if (comparison != 0) return comparison;

        comparison = AlbumModeStrictString.CompareTo(other.AlbumModeStrictString);
        if (comparison != 0) return comparison;

        comparison = StrictArtistString.CompareTo(other.StrictArtistString);
        if (comparison != 0) return comparison;

        comparison = InferredTrackCount.CompareTo(other.InferredTrackCount);
        if (comparison != 0) return comparison;

        comparison = UploadSpeedMedium.CompareTo(other.UploadSpeedMedium);
        if (comparison != 0) return comparison;

        comparison = BitRate.CompareTo(other.BitRate);
        if (comparison != 0) return comparison;

        comparison = LevenshteinScore.CompareTo(other.LevenshteinScore);
        if (comparison != 0) return comparison;

        return RandomTiebreaker.CompareTo(other.RandomTiebreaker);
    }
}
