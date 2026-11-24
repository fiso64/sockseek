using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Core.Services;

using SearchResponse = Soulseek.SearchResponse;

namespace Sockseek.Core.Models;

    public class FileConditions
    {
        public int? LengthTolerance;
        public int? MinBitrate;
        public int? MaxBitrate;
        public int? MinSampleRate;
        public int? MaxSampleRate;
        public int? MinBitDepth;
        public int? MaxBitDepth;
        public bool StrictTitle;
        public bool StrictArtist;
        public bool StrictAlbum;
        public bool ExcludeLocked;
        public string[] Formats = [];
        public string[] BannedUsers = [];
        public string[] AllowedUsers = [];
        public bool AcceptNoLength = true;
        public bool AcceptMissingProps = true;

        public bool VisibilitySatisfies(SearchResultVisibility visibility)
            => !ExcludeLocked || visibility == SearchResultVisibility.Public;

        public FileConditions() { }

        public FileConditions(FileConditions other)
        {
            (LengthTolerance, MinBitrate, MaxBitrate, MinSampleRate, MaxSampleRate, MinBitDepth, MaxBitDepth,
                StrictTitle, StrictArtist, StrictAlbum, ExcludeLocked, AcceptNoLength, AcceptMissingProps) = other.Scalars;
            Formats = other.Formats.ToArray();
            BannedUsers = other.BannedUsers.ToArray();
            AllowedUsers = other.AllowedUsers.ToArray();
        }

        // Every non-list condition, for copying and comparing in one place.
        private (int?, int?, int?, int?, int?, int?, int?, bool, bool, bool, bool, bool, bool) Scalars
            => (LengthTolerance, MinBitrate, MaxBitrate, MinSampleRate, MaxSampleRate, MinBitDepth, MaxBitDepth,
                StrictTitle, StrictArtist, StrictAlbum, ExcludeLocked, AcceptNoLength, AcceptMissingProps);

        public FileConditions With(FileConditionPatch other)
        {
            var res = new FileConditions(this);
            res.AddConditions(other);
            return res;
        }

        public FileConditions With(FileConditions other) => new()
        {
            LengthTolerance = other.LengthTolerance ?? LengthTolerance,
            MinBitrate = other.MinBitrate ?? MinBitrate,
            MaxBitrate = other.MaxBitrate ?? MaxBitrate,
            MinSampleRate = other.MinSampleRate ?? MinSampleRate,
            MaxSampleRate = other.MaxSampleRate ?? MaxSampleRate,
            MinBitDepth = other.MinBitDepth ?? MinBitDepth,
            MaxBitDepth = other.MaxBitDepth ?? MaxBitDepth,
            StrictTitle = StrictTitle || other.StrictTitle,
            StrictArtist = StrictArtist || other.StrictArtist,
            StrictAlbum = StrictAlbum || other.StrictAlbum,
            ExcludeLocked = ExcludeLocked || other.ExcludeLocked,
            Formats = other.Formats.Length > 0 ? other.Formats.ToArray() : Formats.ToArray(),
            BannedUsers = BannedUsers.Concat(other.BannedUsers).Distinct().ToArray(),
            AllowedUsers = AllowedUsers.Concat(other.AllowedUsers).Distinct().ToArray(),
            AcceptNoLength = AcceptNoLength && other.AcceptNoLength,
            AcceptMissingProps = AcceptMissingProps && other.AcceptMissingProps,
        };

        public void AddConditions(FileConditionPatch mod)
        {
            LengthTolerance = mod.LengthTolerance ?? LengthTolerance;
            MinBitrate = mod.MinBitrate ?? MinBitrate;
            MaxBitrate = mod.MaxBitrate ?? MaxBitrate;
            MinSampleRate = mod.MinSampleRate ?? MinSampleRate;
            MaxSampleRate = mod.MaxSampleRate ?? MaxSampleRate;
            MinBitDepth = mod.MinBitDepth ?? MinBitDepth;
            MaxBitDepth = mod.MaxBitDepth ?? MaxBitDepth;
            StrictTitle = mod.StrictTitle ?? StrictTitle;
            StrictArtist = mod.StrictArtist ?? StrictArtist;
            StrictAlbum = mod.StrictAlbum ?? StrictAlbum;
            ExcludeLocked = mod.ExcludeLocked ?? ExcludeLocked;
            Formats = mod.Formats?.ToArray() ?? Formats;
            BannedUsers = mod.BannedUsers?.ToArray() ?? BannedUsers;
            AllowedUsers = mod.AllowedUsers?.ToArray() ?? AllowedUsers;
            AcceptNoLength = mod.AcceptNoLength ?? AcceptNoLength;
            AcceptMissingProps = mod.AcceptMissingProps ?? AcceptMissingProps;
        }

        public override bool Equals(object? obj)
            => obj is FileConditions other && GetType() == other.GetType()
                && Scalars == other.Scalars
                && Formats.SequenceEqual(other.Formats)
                && BannedUsers.SequenceEqual(other.BannedUsers)
                && AllowedUsers.SequenceEqual(other.AllowedUsers);

        public bool HasActiveAudioPropertyConditions()
            => MinBitrate != null
                || MaxBitrate != null
                || MinSampleRate != null
                || MaxSampleRate != null
                || MinBitDepth != null
                || MaxBitDepth != null;

        public bool HasActiveAlbumTrackConditions()
            => HasActiveFormatCondition() || HasActiveAudioPropertyConditions();

        public FileConditions WithoutAudioPropertyConditions()
        {
            var result = new FileConditions(this);
            result.MinBitrate = null;
            result.MaxBitrate = null;
            result.MinSampleRate = null;
            result.MaxSampleRate = null;
            result.MinBitDepth = null;
            result.MaxBitDepth = null;
            return result;
        }

        public FileConditions WithoutAlbumTrackConditions()
        {
            var result = WithoutAudioPropertyConditions();
            result.Formats = [];
            return result;
        }

        public bool HasActiveFormatCondition()
        {
            if (Formats.Length == 0)
                return false;

            var configured = Formats
                .Select(format => format.TrimStart('.').ToLowerInvariant())
                .Where(format => format.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(format => format, StringComparer.Ordinal)
                .ToArray();
            var defaultFormats = SearchDefaults.Formats
                .Select(format => format.TrimStart('.').ToLowerInvariant())
                .OrderBy(format => format, StringComparer.Ordinal)
                .ToArray();
            return !configured.SequenceEqual(defaultFormats);
        }

        internal bool FileSatisfies(
            ConditionFile file,
            SongQuery? query,
            SearchResponse? response = null,
            bool filenameChecks = true,
            bool checkUser = true)
            => ProjectionFileSatisfies(file, query, response?.Username, filenameChecks, checkUser);

        internal bool ProjectionFileSatisfies(
            ConditionFile file,
            SongQuery? query,
            string? username,
            bool filenameChecks = true,
            bool checkUser = true)
            => Rejection(file, query, username, filenameChecks, checkUser) == ResultRejection.None;

        /// <summary>
        /// The first condition the file fails, cheapest checks first. This is
        /// the only implementation of the file conditions: passing means None.
        /// </summary>
        internal ResultRejection Rejection(
            ConditionFile file,
            SongQuery? query,
            string? username,
            bool filenameChecks = true,
            bool checkUser = true)
        {
            int length = query?.Length ?? -1;
            string title = query?.Title ?? "";
            string artist = query?.Artist ?? "";
            string album = query?.Album ?? "";
            return !FormatSatisfies(file.Path) ? ResultRejection.Format
                : !LengthToleranceSatisfies(file.Length, length) ? ResultRejection.Length
                : !BitrateSatisfies(file.Bitrate) ? ResultRejection.Bitrate
                : !SampleRateSatisfies(file.SampleRate) ? ResultRejection.SampleRate
                : !BitDepthSatisfies(file.BitDepth) ? ResultRejection.BitDepth
                : filenameChecks && !StrictTitleSatisfies(file.Path, title) ? ResultRejection.StrictTitle
                : filenameChecks && !StrictArtistSatisfies(file.Path, artist) ? ResultRejection.StrictArtist
                : filenameChecks && !StrictAlbumSatisfies(file.Path, album) ? ResultRejection.StrictAlbum
                : checkUser && !UsernameSatisfies(username) ? ResultRejection.User
                : ResultRejection.None;
        }

        public bool StrictTitleSatisfies(string fname, string tname, bool noPath = true)
        {
            if (!StrictTitle || tname.Length == 0)
                return true;

            fname = noPath ? Utils.GetFileNameWithoutExtSlsk(fname) : fname;
            return Utils.HasFeatureCredit(tname)
                ? StrictStringAny(fname, QueryMatching.Titles(tname))
                : StrictString(fname, tname, diacrRemove: true, ignoreCase: true);
        }

        public bool StrictArtistSatisfies(string fname, string aname)
        {
            if (!StrictArtist || aname.Length == 0)
                return true;

            // Keep the common single-artist path allocation-free apart from existing preprocessing.
            if (aname.Contains(';'))
                return StrictStringAny(fname, aname, ';', optionalCredits: true, boundarySkipWs: false);
            return Utils.HasFeatureCredit(aname)
                ? StrictStringAny(fname, QueryMatching.Titles(aname), boundarySkipWs: false)
                : StrictString(fname, aname, diacrRemove: true, ignoreCase: true, boundarySkipWs: false);
        }

        public bool StrictAlbumSatisfies(string fname, string alname)
        {
            if (!StrictAlbum || alname.Length == 0)
                return true;

            return StrictString(Utils.GetDirectoryNameSlsk(fname), alname, diacrRemove: true, ignoreCase: true, boundarySkipWs: true);
        }

        // Equivalent to: replace '_' and Windows-invalid chars (optionally also '/' and '\') with spaces,
        // optionally remove diacritics, trim, and collapse consecutive literal spaces.
        // Shared by condition filtering and result ranking.
        public static string StrictStringPreprocess(string str, bool diacrRemove = true, bool removeSlash = false)
        {
            // Most candidate paths and query terms already need no changes.
            // Reuse those strings rather than allocating a buffer and a copy.
            for (int i = 0; i < str.Length; i++)
                if (NormalizeStrictChar(str[i], diacrRemove, removeSlash) != str[i]
                    || (i == 0 || i == str.Length - 1) && char.IsWhiteSpace(str[i])
                    || i > 0 && str[i] == ' ' && str[i - 1] == ' ')
                    return StrictStringPreprocess(str.AsSpan(), diacrRemove, removeSlash);
            return str;
        }

        private static string StrictStringPreprocess(ReadOnlySpan<char> str, bool diacrRemove = true, bool removeSlash = false)
        {
            if (str.Length == 0)
                return string.Empty;

            Span<char> buffer = str.Length <= 512 ? stackalloc char[str.Length] : new char[str.Length];
            int length = 0;
            bool previousWasSpace = false;
            bool hasOutput = false;

            for (int i = 0; i < str.Length; i++)
            {
                char c = NormalizeStrictChar(str[i], diacrRemove, removeSlash);

                if (!hasOutput && char.IsWhiteSpace(c))
                    continue;

                if (c == ' ')
                {
                    if (previousWasSpace)
                        continue;

                    previousWasSpace = true;
                }
                else
                {
                    previousWasSpace = false;
                }

                buffer[length++] = c;
                hasOutput = true;
            }

            while (length > 0 && char.IsWhiteSpace(buffer[length - 1]))
                length--;

            return length == 0 ? string.Empty : new string(buffer[..length]);
        }

        public static string FuzzyPhrasePreprocess(string str, bool diacrRemove = true)
        {
            if (str.Length == 0)
                return str;

            Span<char> buffer = str.Length <= 512
                ? stackalloc char[str.Length]
                : new char[str.Length];
            int length = 0;
            bool previousWasSpace = false;
            bool hasOutput = false;

            for (int i = 0; i < str.Length; i++)
            {
                char c = str[i];
                if (diacrRemove && c > 127)
                    c = c.RemoveDiacritics();

                if (Utils.IsSpecialChar(c) || char.IsWhiteSpace(c))
                    c = ' ';

                if (!hasOutput && c == ' ')
                    continue;

                if (c == ' ')
                {
                    if (previousWasSpace)
                        continue;

                    previousWasSpace = true;
                }
                else
                {
                    previousWasSpace = false;
                }

                buffer[length++] = c;
                hasOutput = true;
            }

            while (length > 0 && buffer[length - 1] == ' ')
                length--;

            return length == 0 ? string.Empty : new string(buffer[..length]);
        }

        private static char NormalizeStrictChar(char c, bool diacrRemove, bool removeSlash)
        {
            if (c == '_' || IsStrictInvalidChar(c) || removeSlash && c is '/' or '\\')
                return ' ';

            return diacrRemove && c > 127 ? c.RemoveDiacritics() : c;
        }

        private static bool IsStrictInvalidChar(char c)
            => c is ':' or '|' or '?' or '>' or '<' or '*' or '"';

        public static bool StrictString(string fname, string tname, bool diacrRemove = true, bool ignoreCase = true, bool boundarySkipWs = true)
        {
            if (tname.Length == 0)
                return true;

            fname = StrictStringPreprocess(fname, diacrRemove);
            tname = StrictStringPreprocess(tname, diacrRemove, removeSlash: true);

            return StrictStringPrepared(fname, tname, ignoreCase, boundarySkipWs);
        }

        internal static bool StrictStringPrepared(string fname, string tname, bool ignoreCase = true, bool boundarySkipWs = true)
            => tname.Length == 0 || (boundarySkipWs
                ? fname.ContainsWithBoundaryIgnoreWs(tname, ignoreCase, acceptLeftDigit: true)
                : fname.ContainsWithBoundary(tname, ignoreCase));

        // The candidate is prepared once, regardless of the number of alternatives.
        internal static bool StrictStringAny(string fname, string[] alternatives, bool boundarySkipWs = true)
        {
            fname = StrictStringPreprocess(fname);
            foreach (string alternative in alternatives)
                if (StrictStringPrepared(fname, StrictStringPreprocess(alternative, removeSlash: true), boundarySkipWs: boundarySkipWs))
                    return true;
            return false;
        }

        internal static bool StrictStringAny(string fname, string alternatives, char separator,
            bool optionalCredits = false, bool boundarySkipWs = true)
        {
            fname = StrictStringPreprocess(fname);
            var source = alternatives.AsSpan();
            foreach (var range in source.Split(separator))
            {
                var alternative = source[range].Trim();
                if (alternative.IsEmpty)
                    continue;
                if (StrictStringPrepared(fname, StrictStringPreprocess(alternative, removeSlash: true), boundarySkipWs: boundarySkipWs))
                    return true;
                if (optionalCredits && Utils.HasFeatureCredit(alternative))
                {
                    string withoutCredits = alternative.ToString().RemoveFt();
                    if (withoutCredits.Length > 0 && StrictStringPrepared(fname,
                            StrictStringPreprocess(withoutCredits, removeSlash: true), boundarySkipWs: boundarySkipWs))
                        return true;
                }
            }
            return false;
        }

        internal static bool StringMatchesAnyPrepared(string fname, string[] alternatives, bool boundarySkipWs = true)
        {
            foreach (string alternative in alternatives)
                if (StrictStringPrepared(fname, alternative, boundarySkipWs: boundarySkipWs))
                    return true;
            return false;
        }

        public bool FormatSatisfies(string fname)
        {
            if (Formats.Length == 0)
                return true;

            string ext = Path.GetExtension(fname).TrimStart('.').ToLower();
            return ext.Length > 0 && Formats.Any(f => f == ext);
        }

        public bool LengthToleranceSatisfies(int? length, int wantedLength)
        {
            if (LengthTolerance == null || LengthTolerance < 0 || wantedLength < 0)
                return true;
            if (length == null || length < 0)
                return AcceptNoLength;
            return Math.Abs((int)length - wantedLength) <= LengthTolerance;
        }

        public bool BitrateSatisfies(int? bitrate)
        {
            return BoundCheck(bitrate, MinBitrate, MaxBitrate);
        }

        public bool SampleRateSatisfies(int? sampleRate)
        {
            return BoundCheck(sampleRate, MinSampleRate, MaxSampleRate);
        }

        public bool BitDepthSatisfies(int? bitdepth)
        {
            return BoundCheck(bitdepth, MinBitDepth, MaxBitDepth);
        }

        public bool BoundCheck(int? num, int? min, int? max)
        {
            if (max == null && min == null)
                return true;
            if (num == null)
                return AcceptMissingProps;
            if ((min != null && num < min) || (max != null && num > max))
                return false;
            return true;
        }

        public bool BannedUsersSatisfies(SearchResponse? response)
        {
            return BannedUsernameSatisfies(response?.Username);
        }

        public bool BannedUsernameSatisfies(string? username)
            => username == null || BannedUsers.Length == 0 || !BannedUsers.Any(x => x == username);

        public bool AllowedUsersSatisfies(SearchResponse? response)
        {
            return AllowedUsernameSatisfies(response?.Username);
        }

        public bool AllowedUsernameSatisfies(string? username)
            => username == null || AllowedUsers.Length == 0 || AllowedUsers.Any(x => x == username);

        public bool UserSatisfies(SearchResponse? response)
        {
            return BannedUsersSatisfies(response) && AllowedUsersSatisfies(response);
        }

        public bool UsernameSatisfies(string? username)
            => BannedUsernameSatisfies(username) && AllowedUsernameSatisfies(username);

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }

    public class FileConditionPatch
    {
        public int? LengthTolerance;
        public int? MinBitrate;
        public int? MaxBitrate;
        public int? MinSampleRate;
        public int? MaxSampleRate;
        public int? MinBitDepth;
        public int? MaxBitDepth;
        public bool? StrictTitle;
        public bool? StrictArtist;
        public bool? StrictAlbum;
        public bool? ExcludeLocked;
        public string[]? Formats;
        public string[]? BannedUsers;
        public string[]? AllowedUsers;
        public bool? AcceptNoLength;
        public bool? AcceptMissingProps;

        // Every non-list condition, for comparing in one place.
        private (int?, int?, int?, int?, int?, int?, int?, bool?, bool?, bool?, bool?, bool?, bool?) Scalars
            => (LengthTolerance, MinBitrate, MaxBitrate, MinSampleRate, MaxSampleRate, MinBitDepth, MaxBitDepth,
                StrictTitle, StrictArtist, StrictAlbum, ExcludeLocked, AcceptNoLength, AcceptMissingProps);

        public bool IsEmpty()
            => Scalars == default && Formats == null && BannedUsers == null && AllowedUsers == null;

        public void FillMissingFrom(FileConditionPatch? fallback)
        {
            if (fallback == null)
                return;

            LengthTolerance ??= fallback.LengthTolerance;
            MinBitrate ??= fallback.MinBitrate;
            MaxBitrate ??= fallback.MaxBitrate;
            MinSampleRate ??= fallback.MinSampleRate;
            MaxSampleRate ??= fallback.MaxSampleRate;
            MinBitDepth ??= fallback.MinBitDepth;
            MaxBitDepth ??= fallback.MaxBitDepth;
            StrictTitle ??= fallback.StrictTitle;
            StrictArtist ??= fallback.StrictArtist;
            StrictAlbum ??= fallback.StrictAlbum;
            ExcludeLocked ??= fallback.ExcludeLocked;
            Formats ??= fallback.Formats == null ? null : [.. fallback.Formats];
            BannedUsers ??= fallback.BannedUsers == null ? null : [.. fallback.BannedUsers];
            AllowedUsers ??= fallback.AllowedUsers == null ? null : [.. fallback.AllowedUsers];
            AcceptNoLength ??= fallback.AcceptNoLength;
            AcceptMissingProps ??= fallback.AcceptMissingProps;
        }

        public static FileConditionPatch? Merge(FileConditionPatch? primary, FileConditionPatch? fallback)
        {
            if (primary == null)
                return fallback;

            primary.FillMissingFrom(fallback);
            return primary.IsEmpty() ? null : primary;
        }

        public FileConditionPatch Clone()
        {
            var copy = (FileConditionPatch)MemberwiseClone();
            copy.Formats = Formats?.ToArray();
            copy.BannedUsers = BannedUsers?.ToArray();
            copy.AllowedUsers = AllowedUsers?.ToArray();
            return copy;
        }

        public override bool Equals(object? obj)
            => obj is FileConditionPatch other
                && Scalars == other.Scalars
                && SameList(Formats, other.Formats)
                && SameList(BannedUsers, other.BannedUsers)
                && SameList(AllowedUsers, other.AllowedUsers);

        private static bool SameList(string[]? left, string[]? right)
            => left == null ? right == null : right != null && left.SequenceEqual(right);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Scalars);
            foreach (string item in (Formats ?? []).Concat(BannedUsers ?? []).Concat(AllowedUsers ?? []))
                hash.Add(item);
            return hash.ToHashCode();
        }
    }
