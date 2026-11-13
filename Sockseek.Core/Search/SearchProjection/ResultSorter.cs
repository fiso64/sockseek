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
        ConcurrentDictionary<string, int> peerFailureCounts,
        bool useInfer = false,
        bool albumMode = false,
        bool ignoreStringSortConditions = false)
    {
        return OrderedResults(results.Select(x => x.Value), query, search, peerFailureCounts, useInfer, albumMode, ignoreStringSortConditions);
    }

    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> peerFailureCounts,
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
            peerFailureCounts,
            useBracketCheck,
            useInfer,
            albumMode,
            ignoreStringSortConditions);
    }

    private static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResultsCore(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> peerFailureCounts,
        bool useBracketCheck,
        bool useInfer,
        bool albumMode,
        bool ignoreStringSortConditions)
    {
        var keyContext = new SortKeyContext(
            results,
            query,
            search,
            peerFailureCounts,
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

        return entries.Select(x => (x.Response!, x.File!));
    }

    public static IEnumerable<SearchProjectionInput> OrderedInputs(
        IEnumerable<SearchProjectionInput> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> peerFailureCounts,
        bool useInfer = false,
        bool albumMode = false,
        bool ignoreStringSortConditions = false)
    {
        var resultList = results as IReadOnlyCollection<SearchProjectionInput> ?? results.ToArray();
        var keyContext = new SortKeyContext(
            resultList, query, search, peerFailureCounts,
            useBracketCheck: !albumMode, useInfer: false, albumMode, ignoreStringSortConditions);
        var entries = new List<SortEntry>(resultList.Count);
        int index = 0;
        foreach (var input in resultList)
        {
            var entry = CreateSortEntry(input, keyContext, index++);
            if (entry.HasValue)
                entries.Add(entry.Value);
        }
        entries.Sort(SortEntryComparer.Instance);
        return entries.Select(entry => entry.Input);
    }

    internal static SortKeyContext CreateSortKeyContext(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> peerFailureCounts,
        bool useBracketCheck,
        bool useInfer,
        bool albumMode,
        bool ignoreStringSortConditions = false,
        Func<SearchProjectionInput, bool>? necessaryConditionEvaluator = null)
        => new(
            results,
            query,
            search,
            peerFailureCounts,
            useBracketCheck,
            useInfer,
            albumMode,
            ignoreStringSortConditions,
            necessaryConditionEvaluator);

    internal static SortEntry? CreateSortEntry(
        SearchResponse response,
        Soulseek.File file,
        SortKeyContext keyContext,
        int originalIndex)
    {
        var entry = CreateSortEntry(ToInput(response, file), keyContext, originalIndex);
        return entry is { } value ? value with { Response = response, File = file } : null;
    }

    internal static SortEntry? CreateSortEntry(
        SearchProjectionInput input,
        SortKeyContext keyContext,
        int originalIndex,
        int? peerFailureCount = null)
    {
        if (!keyContext.Search.NecessaryCond.VisibilitySatisfies(input.Visibility)) return null;
        int failureCount = peerFailureCount ?? keyContext.PeerFailureCounts.GetValueOrDefault(input.Username, 0);
        if (failureCount >= keyContext.Search.EffectiveIgnoreOn)
            return null;
        return new SortEntry(input, null, null, keyContext.CreateKey(input, failureCount), originalIndex);
    }

    internal static SortEntry? CreateSortEntry(
        FileCandidate candidate,
        SortKeyContext keyContext,
        int originalIndex)
        => CreateSortEntry(new SearchProjectionInput(
            0, 0, candidate.Username, 0, candidate.Filename, candidate.Size,
            candidate.BitRate, candidate.BitDepth, candidate.SampleRate, candidate.Length,
            candidate.Extension, candidate.UploadSpeed, candidate.HasFreeUploadSlot,
            candidate.Attributes, DateTimeOffset.UnixEpoch, Visibility: candidate.Visibility), keyContext, originalIndex);

    private static SearchProjectionInput ToInput(SearchResponse response, Soulseek.File file)
        => new(
            0, 0, response.Username, response.Files.Count, file.Filename, file.Size,
            file.BitRate, file.BitDepth, file.SampleRate, file.Length, file.Extension,
            response.UploadSpeed, response.HasFreeUploadSlot, null, DateTimeOffset.UnixEpoch);

    private static Dictionary<(string Username, string Filename), InferredResultGroup> GetInferredQueries(
        IEnumerable<SearchProjectionInput> results,
        SongQuery query,
        SearchSettings search)
    {
        var groups = new SongQueryGroups<InferredResultGroup>(search.GroupLengthTol,
            static (key, _) => new InferredResultGroup(key));
        var inferredByFilename = new Dictionary<string, SongQuery>();
        var inferredQueries = new Dictionary<(string Username, string Filename), InferredResultGroup>();

        foreach (var input in results)
        {
            if (!inferredByFilename.TryGetValue(input.Filename, out var inferred))
            {
                inferred = Searcher.InferSongQuery(input.Filename, query);
                inferredByFilename.Add(input.Filename, inferred);
            }

            var key = new SongQuery(inferred) { Length = input.Length ?? -1 };

            var group = groups.GetOrAdd(key);

            group.Count++;
            inferredQueries[(input.Username, input.Filename)] = group;
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
        private readonly string[] strictTitle;
        private readonly string[] strictArtist;
        private readonly string strictAlbum;
        private readonly string[] fuzzyTitle;
        private readonly string[] fuzzyArtist;
        private readonly string fuzzyAlbum;
        private readonly FileConditions necessaryCond;
        private readonly FileConditions preferredCond;
        private readonly SongQuery emptyQuery = new();
        private readonly bool queryTitleAllowsBrackets;
        private readonly bool ignoreStringSortConditions;
        private readonly Func<SearchProjectionInput, bool>? necessaryConditionEvaluator;
        private Dictionary<string, string>? strictDirectoryNames;
        private Dictionary<string, string>? fuzzyDirectoryNames;

        private SortKeyContext(
            SongQuery query, SearchSettings search,
            ConcurrentDictionary<string, int> peerFailureCounts,
            bool useBracketCheck, bool useInfer, bool albumMode, bool ignoreStringSortConditions,
            Func<SearchProjectionInput, bool>? necessaryConditionEvaluator)
        {
            Query = query;
            Search = search;
            PeerFailureCounts = peerFailureCounts;
            UseBracketCheck = useBracketCheck;
            UseInfer = useInfer;
            AlbumMode = albumMode;
            this.ignoreStringSortConditions = ignoreStringSortConditions;
            this.necessaryConditionEvaluator = necessaryConditionEvaluator;
            strictTitle = QueryMatching.Titles(query.Title).Select(value => FileConditions.StrictStringPreprocess(value, removeSlash: true)).ToArray();
            strictArtist = QueryMatching.Artists(query.Artist).Select(value => FileConditions.StrictStringPreprocess(value, removeSlash: true)).ToArray();
            strictAlbum = FileConditions.StrictStringPreprocess(query.Album, removeSlash: true);
            fuzzyTitle = QueryMatching.Titles(query.Title).Select(value => FileConditions.FuzzyPhrasePreprocess(value)).ToArray();
            fuzzyArtist = QueryMatching.Artists(query.Artist).Select(value => FileConditions.FuzzyPhrasePreprocess(value)).ToArray();
            fuzzyAlbum = FileConditions.FuzzyPhrasePreprocess(query.Album);
            necessaryCond = ignoreStringSortConditions ? WithoutStringConditions(search.NecessaryCond) : search.NecessaryCond;
            // Ranking may ignore strings; public facts must retain every configured
            // preference. Preserve the existing frozen settings for that ranking mode.
            preferredCond = ignoreStringSortConditions ? new FileConditions(search.PreferredCond) : search.PreferredCond;
            queryTitleAllowsBrackets = query.Title.RemoveFt().Replace('[', '(').Contains('(');
        }

        public SortKeyContext(
            IEnumerable<(SearchResponse, Soulseek.File)> results,
            SongQuery query,
            SearchSettings search,
            ConcurrentDictionary<string, int> peerFailureCounts,
            bool useBracketCheck,
            bool useInfer,
            bool albumMode,
            bool ignoreStringSortConditions,
            Func<SearchProjectionInput, bool>? necessaryConditionEvaluator = null)
            : this(query, search, peerFailureCounts, useBracketCheck, useInfer, albumMode,
                ignoreStringSortConditions, necessaryConditionEvaluator)
        {
            var resultList = useInfer ? results.ToList() : null;
            SortableResults = resultList ?? results;
            infQueriesAndCounts = useInfer
                ? new Lazy<Dictionary<(string Username, string Filename), InferredResultGroup>>(
                    () => GetInferredQueries(resultList!.Select(pair => ToInput(pair.Item1, pair.Item2)), query, search))
                : null;
        }

        public SortKeyContext(
            IEnumerable<SearchProjectionInput> results,
            SongQuery query,
            SearchSettings search,
            ConcurrentDictionary<string, int> peerFailureCounts,
            bool useBracketCheck,
            bool useInfer,
            bool albumMode,
            bool ignoreStringSortConditions,
            Func<SearchProjectionInput, bool>? necessaryConditionEvaluator = null)
            : this(query, search, peerFailureCounts, useBracketCheck, useInfer, albumMode,
                ignoreStringSortConditions, necessaryConditionEvaluator)
        {
            var resultList = useInfer ? results.ToList() : null;
            InputResults = resultList ?? results;
            SortableResults = [];
            infQueriesAndCounts = useInfer
                ? new Lazy<Dictionary<(string Username, string Filename), InferredResultGroup>>(
                    () => GetInferredQueries(resultList!, query, search))
                : null;
        }

        public IEnumerable<(SearchResponse, Soulseek.File)> SortableResults { get; } = [];
        public IEnumerable<SearchProjectionInput> InputResults { get; } = [];
        public SongQuery Query { get; }
        public SearchSettings Search { get; }
        public ConcurrentDictionary<string, int> PeerFailureCounts { get; }
        public bool UseBracketCheck { get; }
        public bool UseInfer { get; }
        public bool AlbumMode { get; }

        public SortKey CreateKey(SearchResponse response, Soulseek.File file)
            => CreateKey(ToInput(response, file));

        public SortKey CreateKey(SearchProjectionInput input, int? peerFailureCount = null)
        {
            (SongQuery Query, int Count)? inferred = null;
            (SongQuery Query, int Count) getInferred() => inferred ??= InferredQuery(input);

            string filename = input.Filename;
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

            // Reuse exact string checks for facts and ranking tie-breakers.
            bool? albumStringMatch = null;
            bool? artistStringMatch = null;
            bool matchesAlbum() => albumStringMatch ??= StrictStringPrepared(getStrictDirectoryName(), strictAlbum);
            bool matchesArtist() => artistStringMatch ??= FileConditions.StringMatchesAnyPrepared(getStrictFullFilename(), strictArtist, boundarySkipWs: false);

            bool strictTitleMatch = !preferredCond.StrictTitle || strictTitle.Length == 0
                || FileConditions.StringMatchesAnyPrepared(getStrictFilenameNoExt(), strictTitle);
            bool fuzzyTitleMatch = ignoreStringSortConditions || !preferredCond.StrictTitle || fuzzyTitle.Length == 0 || strictTitleMatch
                || FileConditions.StringMatchesAnyPrepared(getFuzzyFilenameNoExt(), fuzzyTitle, boundarySkipWs: false);
            bool strictAlbumMatch = !preferredCond.StrictAlbum || strictAlbum.Length == 0 || matchesAlbum();
            bool fuzzyAlbumMatch = ignoreStringSortConditions || !preferredCond.StrictAlbum || fuzzyAlbum.Length == 0 || strictAlbumMatch
                || FuzzyPhrasePrepared(getFuzzyDirectoryName(), fuzzyAlbum);
            bool strictArtistMatch = !preferredCond.StrictArtist || strictArtist.Length == 0 || matchesArtist();
            bool fuzzyArtistMatch = ignoreStringSortConditions || !preferredCond.StrictArtist || fuzzyArtist.Length == 0 || strictArtistMatch
                || FileConditions.StringMatchesAnyPrepared(getFuzzyFullFilename(), fuzzyArtist, boundarySkipWs: false);

            bool lengthToleranceMatch = preferredCond.LengthToleranceSatisfies(input.Length, Query.Length);
            bool formatMatch = preferredCond.FormatSatisfies(filename);
            bool bitrateMatch = preferredCond.BitrateSatisfies(input.BitRate);
            bool sampleRateMatch = preferredCond.SampleRateSatisfies(input.SampleRate);
            bool bitDepthMatch = preferredCond.BitDepthSatisfies(input.BitDepth);
            bool preferredUserConditionsMet = preferredCond.UsernameSatisfies(input.Username);

            bool necessaryConditionsMet = necessaryConditionEvaluator?.Invoke(input)
                ?? ConditionSatisfactionPolicy.SearchFileSatisfies(
                    necessaryCond,
                    input,
                    Query);
            bool qualityPreferencesMet = formatMatch && lengthToleranceMatch && bitrateMatch
                && sampleRateMatch && preferredUserConditionsMet && bitDepthMatch;
            bool stringPreferencesMet = strictTitleMatch && strictArtistMatch && strictAlbumMatch;
            bool unlockedMatch = preferredCond.VisibilitySatisfies(input.Visibility);
            bool preferredConditionsMet = qualityPreferencesMet && stringPreferencesMet && unlockedMatch;
            bool rankingPreferencesMet = qualityPreferencesMet && (ignoreStringSortConditions || stringPreferencesMet) && unlockedMatch;
            var configuredPreferredConditions = new List<SearchPreferenceCondition>(10);
            var satisfiedPreferredConditions = new List<SearchPreferenceCondition>(10);
            AddSatisfied(preferredCond.ExcludeLocked, unlockedMatch, SearchPreferenceCondition.ExcludeLocked);
            AddSatisfied(preferredCond.Formats.Length > 0, formatMatch, SearchPreferenceCondition.Format);
            AddSatisfied(preferredCond.LengthTolerance is >= 0, lengthToleranceMatch, SearchPreferenceCondition.Length);
            AddSatisfied(preferredCond.MinBitrate != null || preferredCond.MaxBitrate != null, bitrateMatch, SearchPreferenceCondition.Bitrate);
            AddSatisfied(preferredCond.MinSampleRate != null || preferredCond.MaxSampleRate != null, sampleRateMatch, SearchPreferenceCondition.SampleRate);
            AddSatisfied(preferredCond.MinBitDepth != null || preferredCond.MaxBitDepth != null, bitDepthMatch, SearchPreferenceCondition.BitDepth);
            AddSatisfied(preferredCond.StrictTitle && strictTitle.Length > 0, strictTitleMatch, SearchPreferenceCondition.Title);
            AddSatisfied(preferredCond.StrictArtist && strictArtist.Length > 0, strictArtistMatch, SearchPreferenceCondition.Artist);
            AddSatisfied(preferredCond.StrictAlbum && strictAlbum.Length > 0, strictAlbumMatch, SearchPreferenceCondition.Album);
            AddSatisfied(preferredCond.BannedUsers.Length > 0 || preferredCond.AllowedUsers.Length > 0, preferredUserConditionsMet, SearchPreferenceCondition.Username);

            void AddSatisfied(bool configured, bool satisfied, SearchPreferenceCondition condition)
            {
                if (!configured)
                    return;
                configuredPreferredConditions.Add(condition);
                if (satisfied)
                    satisfiedPreferredConditions.Add(condition);
            }

            return new SortKey(
                (peerFailureCount ?? PeerFailureCounts.GetValueOrDefault(input.Username, 0)) < Search.DownrankOn,
                necessaryConditionsMet,
                preferredUserConditionsMet,
                Search.AudioConditionsIgnored || (input.Length != null && input.Length > 0) || Search.PreferredCond.AcceptNoLength,
                !UseBracketCheck || CheapBracketCheck(queryTitleAllowsBrackets, filename),
                ignoreStringSortConditions || strictTitleMatch,
                fuzzyTitleMatch,
                ignoreStringSortConditions || strictAlbumMatch,
                fuzzyAlbumMatch,
                ignoreStringSortConditions || strictArtistMatch,
                fuzzyArtistMatch,
                lengthToleranceMatch,
                formatMatch,
                bitrateMatch,
                sampleRateMatch,
                bitDepthMatch,
                unlockedMatch,
                rankingPreferencesMet,
                input.HasFreeUploadSlot ?? false,
                (input.UploadSpeed ?? -1) / 1024 / 650,
                ignoreStringSortConditions || AlbumMode || strictTitle.Length == 0 || FileConditions.StringMatchesAnyPrepared(getStrictFullFilename(), strictTitle),
                ignoreStringSortConditions || !AlbumMode || strictAlbum.Length == 0 || matchesAlbum(),
                ignoreStringSortConditions || strictArtist.Length == 0 || matchesArtist(),
                UseInfer ? getInferred().Count : 0,
                (input.UploadSpeed ?? -1) / 1024 / 350,
                Search.AudioConditionsIgnored ? 0 : (input.BitRate ?? 0) / 80,
                StableTieBreaker(input.Username, filename),
                new SearchConditionFacts(necessaryConditionsMet, preferredConditionsMet,
                    satisfiedPreferredConditions, configuredPreferredConditions));
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

        private (SongQuery, int) InferredQuery(SearchProjectionInput input)
        {
            var key = (input.Username, input.Filename);
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
        SearchProjectionInput Input,
        SearchResponse? Response,
        Soulseek.File? File,
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

    internal readonly record struct SortKey : IComparable<SortKey>
    {
        private readonly uint albumBeforeQualityFlags;

        public SearchConditionFacts ConditionFacts { get; init; }
        public SearchProjectionSortKey Rank { get; init; }

        public SortKey(
            bool peerBelowFailureThreshold,
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
            bool unlockedMatch,
            bool rankingPreferencesMet,
            bool hasFreeUploadSlot,
            int uploadSpeedFast,
            bool nonAlbumModeStrictString,
            bool albumModeStrictString,
            bool strictArtistString,
            int inferredTrackCount,
            int uploadSpeedMedium,
            int bitRate,
            int randomTiebreaker,
            SearchConditionFacts conditionFacts)
        {
            // Identity beats quality: an MP3 from the requested album outranks
            // a FLAC from an unrelated album. Album coverage uses this prefix too.
            var identity = BoolSortKey.CreateDescending()
                .Then(peerBelowFailureThreshold)
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
                .Then(lengthToleranceMatch);
            albumBeforeQualityFlags = identity.Value;
            uint highFlags = identity
                .Then(formatMatch)
                .Then(bitrateMatch)
                .Then(sampleRateMatch)
                .Then(bitDepthMatch)
                .Value | SearchProjectionSortKey.AvailabilityFlags(unlockedMatch, rankingPreferencesMet, hasFreeUploadSlot);
            uint midFlags = BoolSortKey.CreateDescending()
                .Then(nonAlbumModeStrictString)
                .Then(albumModeStrictString)
                .Then(strictArtistString).Value;
            Rank = new(highFlags, uploadSpeedFast, midFlags,
                inferredTrackCount, uploadSpeedMedium, bitRate, randomTiebreaker);
            // Facts describe public condition semantics, independently of the
            // ranking policy encoded above (which may deliberately ignore strings).
            ConditionFacts = conditionFacts;
        }

        internal int CompareAlbumBeforeQualityTo(SortKey other)
            => albumBeforeQualityFlags.CompareTo(other.albumBeforeQualityFlags);

        public int CompareTo(SortKey other) => Rank.CompareTo(other.Rank);
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
