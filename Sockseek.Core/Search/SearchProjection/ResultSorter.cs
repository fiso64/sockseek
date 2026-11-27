using Sockseek.Api;
using Sockseek.Core.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public static partial class ResultSorter
{
    internal static SortEntry? CreateSortEntry(
        SearchProjectionInput input,
        SortKeyContext keyContext,
        int originalIndex,
        int? peerFailureCount = null)
    {
        int failureCount = peerFailureCount ?? keyContext.PeerFailureCounts.GetValueOrDefault(input.Username, 0);
        if (!keyContext.AcceptsPeerAndVisibility(input, failureCount))
            return null;
        return new SortEntry(input, keyContext.CreateKey(input, failureCount), originalIndex);
    }

    internal static SortEntry? CreateSortEntry(
        FileCandidate candidate,
        SortKeyContext keyContext,
        int originalIndex)
        => CreateSortEntry(candidate.ToProjectionInput(), keyContext, originalIndex);

    internal sealed class SortKeyContext
    {
        private readonly string[] strictTitle;
        private readonly string[] strictArtist;
        private readonly string strictAlbum;
        private readonly string[] fuzzyTitle;
        private readonly string[] fuzzyArtist;
        private readonly string fuzzyAlbum;
        private readonly FileConditions necessaryCond;
        private readonly FileConditions preferredCond;
        private readonly UnrequestedVersionTags versionTags;
        private readonly bool titleNamesArtist;
        private readonly bool albumNamesArtist;
        private readonly bool ignoreStringSortConditions;
        private readonly Func<SearchProjectionInput, ResultRejection>? necessaryConditionEvaluator;
        private Dictionary<string, string>? strictDirectoryNames;
        private Dictionary<string, bool>? selfTitledAlbumFolders;
        private Dictionary<string, string>? fuzzyDirectoryNames;

        public SortKeyContext(
            SongQuery query, SearchSettings search,
            ConcurrentDictionary<string, int> peerFailureCounts,
            bool albumMode, bool ignoreStringSortConditions = false,
            Func<SearchProjectionInput, ResultRejection>? necessaryConditionEvaluator = null)
        {
            Query = query;
            Search = search;
            PeerFailureCounts = peerFailureCounts;
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
            versionTags = new UnrequestedVersionTags(query);
            titleNamesArtist = QueryMatching.NamesArtist(query.Title, query.Artist);
            albumNamesArtist = QueryMatching.NamesArtist(query.Album, query.Artist);
        }

        public SongQuery Query { get; }
        public SearchSettings Search { get; }
        public ConcurrentDictionary<string, int> PeerFailureCounts { get; }
        public bool AlbumMode { get; }

        public bool AcceptsPeerAndVisibility(SearchProjectionInput input, int failureCount)
            => failureCount < Search.EffectiveIgnoreOn && Search.NecessaryCond.VisibilitySatisfies(input.Visibility);

        public bool NecessaryConditionsSatisfied(SearchProjectionInput input)
            => NecessaryRejection(input) == ResultRejection.None;

        public ResultRejection NecessaryRejection(SearchProjectionInput input)
            => necessaryConditionEvaluator?.Invoke(input) ?? ConditionSatisfactionPolicy.SearchFileRejection(necessaryCond, input, Query);

        public SortKey CreateKey(SearchProjectionInput input, int? peerFailureCount = null)
        {
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
            bool matchesAlbum() => albumStringMatch ??= albumNamesArtist
                ? SelfTitledAlbumMatches(filename)
                : FileConditions.StrictStringPrepared(getStrictDirectoryName(), strictAlbum);
            bool matchesArtist() => artistStringMatch ??= FileConditions.StringMatchesAnyPrepared(getStrictFullFilename(), strictArtist, boundarySkipWs: false);

            // TODO: Rank track and album offers by edition clues in the request and peer path.
            // For example, prefer a requested clean edit and downrank an unrequested remaster.
            bool strictTitleMatch = !preferredCond.StrictTitle || strictTitle.Length == 0
                || (titleNamesArtist
                    ? FileConditions.SelfTitledTitleMatches(getStrictFilenameNoExt(), strictTitle)
                    : FileConditions.StringMatchesAnyPrepared(getStrictFilenameNoExt(), strictTitle));
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

            ResultRejection necessaryRejection = NecessaryRejection(input);
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
                necessaryRejection == ResultRejection.None,
                preferredUserConditionsMet,
                Search.AudioConditionsIgnored || (input.Length != null && input.Length > 0) || Search.PreferredCond.AcceptNoLength,
                ignoreStringSortConditions || strictTitleMatch,
                fuzzyTitleMatch,
                ignoreStringSortConditions || strictAlbumMatch,
                fuzzyAlbumMatch,
                ignoreStringSortConditions || strictArtistMatch,
                fuzzyArtistMatch,
                AlbumMode || !versionTags.Present(filename),
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
                (input.UploadSpeed ?? -1) / 1024 / 350,
                Search.AudioConditionsIgnored ? 0 : (input.BitRate ?? 0) / 80,
                StableTieBreaker(input.Username, filename),
                new SearchConditionFacts(necessaryRejection, preferredConditionsMet,
                    satisfiedPreferredConditions, configuredPreferredConditions));
        }

        private static FileConditions WithoutStringConditions(FileConditions conditions)
            => new(conditions)
            {
                StrictTitle = false,
                StrictArtist = false,
                StrictAlbum = false,
            };

        // Files of one folder share the answer; look it up without copying the path.
        private bool SelfTitledAlbumMatches(string filename)
        {
            ReadOnlySpan<char> folder = FileConditions.SelfTitledAlbumFolder(filename);
            var folders = (selfTitledAlbumFolders ??= new(StringComparer.Ordinal)).GetAlternateLookup<ReadOnlySpan<char>>();
            if (!folders.TryGetValue(folder, out bool matches))
                folders[folder] = matches = FileConditions.SelfTitledAlbumFolderMatches(folder, strictAlbum);
            return matches;
        }

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

        private static bool FuzzyPhrasePrepared(string fname, string tname, bool boundarySkipWs = true)
        {
            if (tname.Length == 0)
                return true;

            return fname.ContainsWithBoundary(tname, ignoreCase: true);
        }
    }

    internal readonly record struct SortEntry(
        SearchProjectionInput Input,
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
        private uint AlbumBeforeQualityFlags { get; init; }

        public SearchConditionFacts ConditionFacts { get; init; }
        public SearchProjectionSortKey Rank { get; init; }

        public SortKey(
            bool peerBelowFailureThreshold,
            bool necessaryConditionsMet,
            bool preferredUserConditionsMet,
            bool hasValidLength,
            bool strictTitleMatch,
            bool fuzzyTitleMatch,
            bool strictAlbumMatch,
            bool fuzzyAlbumMatch,
            bool strictArtistMatch,
            bool fuzzyArtistMatch,
            bool noUnrequestedVersionTag,
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
            int uploadSpeedMedium,
            int bitRate,
            int randomTiebreaker,
            SearchConditionFacts conditionFacts)
        {
            // Identity beats quality: an MP3 from the requested album outranks
            // a FLAC from an unrelated album. Album coverage uses this prefix too.
            // An unrequested version of the requested song still beats another song.
            var identity = BoolSortKey.CreateDescending()
                .Then(peerBelowFailureThreshold)
                .Then(necessaryConditionsMet)
                .Then(preferredUserConditionsMet)
                .Then(hasValidLength)
                .Then(strictTitleMatch)
                .Then(fuzzyTitleMatch)
                .Then(strictAlbumMatch)
                .Then(fuzzyAlbumMatch)
                .Then(strictArtistMatch)
                .Then(fuzzyArtistMatch)
                .Then(noUnrequestedVersionTag)
                .Then(lengthToleranceMatch)
                // A file is never incomplete; album projection clears this for
                // folders holding fewer tracks than most peers share.
                .Then(true);
            AlbumBeforeQualityFlags = identity.Value;
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
                uploadSpeedMedium, bitRate, randomTiebreaker);
            // Facts describe public condition semantics, independently of the
            // ranking policy encoded above (which may deliberately ignore strings).
            ConditionFacts = conditionFacts;
        }

        internal int CompareAlbumBeforeQualityTo(SortKey other)
            => AlbumBeforeQualityFlags.CompareTo(other.AlbumBeforeQualityFlags);

        internal SortKey ForIncompleteFolder()
            => this with
            {
                Rank = Rank.WithoutCompleteFolder(),
                AlbumBeforeQualityFlags = AlbumBeforeQualityFlags & ~SearchProjectionSortKey.CompleteFolder,
            };

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

    // Unrequested version tags: bracketed filename text (artists put arbitrary edition
    // names there, "(tommy hardcore REdrum)") or a common version word anywhere in the
    // path, unless the query itself contains it. Bracketed numbers (track, disc, year)
    // and label catalogue numbers ("THGO-009", "C104", "TIRCD001") name no version.
    internal sealed class UnrequestedVersionTags
    {
        private static readonly string[] VersionWords =
        [
            "live", "bootleg", "rehearsal", "demo", "demos", "remix", "remixes", "rmx", "mix", "mixes",
            "edit", "acoustic", "instrumental", "karaoke", "cover", "covers", "acapella", "nightcore", "slowed",
        ];

        private static readonly System.Buffers.SearchValues<string> AnyVersionWord =
            System.Buffers.SearchValues.Create(VersionWords, StringComparison.OrdinalIgnoreCase);
        private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> VersionWordSet =
            new HashSet<string>(VersionWords, StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

        private readonly bool queryAllowsBrackets;
        // Version words the query itself contains; usually none.
        private readonly string[] requested = [];

        public UnrequestedVersionTags(SongQuery query)
        {
            queryAllowsBrackets = query.Title.RemoveFt().Replace('[', '(').Contains('(');
            foreach (string field in (ReadOnlySpan<string>)[query.Title, query.Artist, query.Album])
                for (ReadOnlySpan<char> rest = field; NextVersionWord(ref rest) is { Length: > 0 } word;)
                    requested = [.. requested, word.ToString()];
        }

        public bool Present(string filename)
        {
            if (!queryAllowsBrackets && HasBracketedVersionText(filename))
                return true;
            for (ReadOnlySpan<char> rest = filename; NextVersionWord(ref rest) is { Length: > 0 } word;)
                if (!Requested(word))
                    return true;
            return false;
        }

        // The next whole word that is a version word, searching all of them at once.
        private static ReadOnlySpan<char> NextVersionWord(ref ReadOnlySpan<char> rest)
        {
            for (int hit; (hit = rest.IndexOfAny(AnyVersionWord)) >= 0;)
            {
                int end = hit;
                while (end < rest.Length && char.IsLetter(rest[end]))
                    end++;
                ReadOnlySpan<char> word = rest[hit..end];
                bool wordStart = hit == 0 || !char.IsLetter(rest[hit - 1]);
                rest = rest[end..];
                if (wordStart && VersionWordSet.Contains(word))
                    return word;
            }
            return default;
        }

        private bool Requested(ReadOnlySpan<char> word)
        {
            foreach (string requestedWord in requested)
                if (word.Equals(requestedWord, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool HasBracketedVersionText(string filename)
        {
            string name = Utils.GetFileNameWithoutExtSlsk(filename);
            if (!name.Contains('(') && !name.Contains('['))
                return false;

            ReadOnlySpan<char> rest = name.RemoveFt();
            for (int open; (open = rest.IndexOfAny('(', '[')) >= 0;)
            {
                rest = rest[(open + 1)..];
                int close = rest.IndexOfAny(')', ']');
                var text = (close < 0 ? rest : rest[..close]).Trim();
                if (text.ContainsAnyExcept(NumberChars) && !CatalogueNumberRegex().IsMatch(text))
                    return true;
                if (close < 0)
                    break;
                rest = rest[(close + 1)..];
            }
            return false;
        }

        private static readonly System.Buffers.SearchValues<char> NumberChars = System.Buffers.SearchValues.Create("0123456789 -./");
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9]*[-_ ]?\d+$")]
    private static partial Regex CatalogueNumberRegex();
}
