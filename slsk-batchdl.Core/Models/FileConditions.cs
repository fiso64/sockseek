

using SearchResponse = Soulseek.SearchResponse;

namespace Sldl.Core.Models;

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
        public string[] Formats = [];
        public string[] BannedUsers = [];
        public bool AcceptNoLength = true;
        public bool AcceptMissingProps = true;

        public FileConditions() { }

        public FileConditions(FileConditions other)
        {
            LengthTolerance = other.LengthTolerance;
            MinBitrate = other.MinBitrate;
            MaxBitrate = other.MaxBitrate;
            MinSampleRate = other.MinSampleRate;
            MaxSampleRate = other.MaxSampleRate;
            AcceptNoLength = other.AcceptNoLength;
            StrictArtist = other.StrictArtist;
            StrictTitle = other.StrictTitle;
            StrictAlbum = other.StrictAlbum;
            MinBitDepth = other.MinBitDepth;
            MaxBitDepth = other.MaxBitDepth;
            AcceptMissingProps = other.AcceptMissingProps;
            Formats = other.Formats.ToArray();
            BannedUsers = other.BannedUsers.ToArray();
        }

        public FileConditions With(FileConditionPatch other)
        {
            var res = new FileConditions(this);
            res.AddConditions(other);
            return res;
        }

        public FileConditions With(FileConditions other) => new(this)
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
            Formats = other.Formats.Length > 0 ? other.Formats.ToArray() : Formats.ToArray(),
            BannedUsers = BannedUsers.Concat(other.BannedUsers).Distinct().ToArray(),
            AcceptNoLength = AcceptNoLength && other.AcceptNoLength,
            AcceptMissingProps = AcceptMissingProps && other.AcceptMissingProps,
        };

        public FileConditionPatch AddConditions(FileConditionPatch mod)
        {
            var undoMod = new FileConditionPatch();

            if (mod.LengthTolerance != null)
            {
                undoMod.LengthTolerance = LengthTolerance;
                LengthTolerance = mod.LengthTolerance.Value;
            }
            if (mod.MinBitrate != null)
            {
                undoMod.MinBitrate = MinBitrate;
                MinBitrate = mod.MinBitrate.Value;
            }
            if (mod.MaxBitrate != null)
            {
                undoMod.MaxBitrate = MaxBitrate;
                MaxBitrate = mod.MaxBitrate.Value;
            }
            if (mod.MinSampleRate != null)
            {
                undoMod.MinSampleRate = MinSampleRate;
                MinSampleRate = mod.MinSampleRate.Value;
            }
            if (mod.MaxSampleRate != null)
            {
                undoMod.MaxSampleRate = MaxSampleRate;
                MaxSampleRate = mod.MaxSampleRate.Value;
            }
            if (mod.MinBitDepth != null)
            {
                undoMod.MinBitDepth = MinBitDepth;
                MinBitDepth = mod.MinBitDepth.Value;
            }
            if (mod.MaxBitDepth != null)
            {
                undoMod.MaxBitDepth = MaxBitDepth;
                MaxBitDepth = mod.MaxBitDepth.Value;
            }
            if (mod.StrictTitle != null)
            {
                undoMod.StrictTitle = StrictTitle;
                StrictTitle = mod.StrictTitle.Value;
            }
            if (mod.StrictArtist != null)
            {
                undoMod.StrictArtist = StrictArtist;
                StrictArtist = mod.StrictArtist.Value;
            }
            if (mod.StrictAlbum != null)
            {
                undoMod.StrictAlbum = StrictAlbum;
                StrictAlbum = mod.StrictAlbum.Value;
            }
            if (mod.Formats != null)
            {
                undoMod.Formats = Formats;
                Formats = mod.Formats.ToArray();
            }
            if (mod.BannedUsers != null)
            {
                undoMod.BannedUsers = BannedUsers;
                BannedUsers = mod.BannedUsers.ToArray();
            }
            if (mod.AcceptNoLength != null)
            {
                undoMod.AcceptNoLength = AcceptNoLength;
                AcceptNoLength = mod.AcceptNoLength.Value;
            }
            if (mod.AcceptMissingProps != null)
            {
                undoMod.AcceptMissingProps = AcceptMissingProps;
                AcceptMissingProps = mod.AcceptMissingProps.Value;
            }

            return undoMod;
        }

        public override bool Equals(object? obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var other = (FileConditions)obj;

            return LengthTolerance == other.LengthTolerance
                && MinBitrate == other.MinBitrate
                && MaxBitrate == other.MaxBitrate
                && MinSampleRate == other.MinSampleRate
                && MaxSampleRate == other.MaxSampleRate
                && MinBitDepth == other.MinBitDepth
                && MaxBitDepth == other.MaxBitDepth
                && StrictTitle == other.StrictTitle
                && StrictArtist == other.StrictArtist
                && StrictAlbum == other.StrictAlbum
                && AcceptNoLength == other.AcceptNoLength
                && AcceptMissingProps == other.AcceptMissingProps
                && Formats.SequenceEqual(other.Formats)
                && BannedUsers.SequenceEqual(other.BannedUsers);
        }

        public void UnsetClientSpecificFields()
        {
            MinBitrate = null;
            MaxBitrate = null;
            MinSampleRate = null;
            MaxSampleRate = null;
            MinBitDepth = null;
            MaxBitDepth = null;
        }

        public bool FileSatisfies(Soulseek.File file, SongQuery? query, SearchResponse? response)
        {
            int length    = query?.Length ?? -1;
            string title  = query?.Title  ?? "";
            string artist = query?.Artist ?? "";
            string album  = query?.Album  ?? "";
            return FormatSatisfies(file.Filename)
                && LengthToleranceSatisfies(file, length) && BitrateSatisfies(file) && SampleRateSatisfies(file)
                && StrictTitleSatisfies(file.Filename, title) && StrictArtistSatisfies(file.Filename, artist)
                && StrictAlbumSatisfies(file.Filename, album) && BannedUsersSatisfies(response) && BitDepthSatisfies(file);
        }

        public bool FileSatisfies(TagLib.File file, SongQuery? query, bool filenameChecks = false)
        {
            int length    = query?.Length ?? -1;
            string title  = query?.Title  ?? "";
            string artist = query?.Artist ?? "";
            string album  = query?.Album  ?? "";
            return FormatSatisfies(file.Name)
                && LengthToleranceSatisfies(file, length) && BitrateSatisfies(file) && SampleRateSatisfies(file)
                && BitDepthSatisfies(file) && (!filenameChecks || StrictTitleSatisfies(file.Name, title)
                && StrictArtistSatisfies(file.Name, artist) && StrictAlbumSatisfies(file.Name, album));
        }

        public bool FileSatisfies(SimpleFile file, SongQuery? query, bool filenameChecks = false)
        {
            int length    = query?.Length ?? -1;
            string title  = query?.Title  ?? "";
            string artist = query?.Artist ?? "";
            string album  = query?.Album  ?? "";
            return FormatSatisfies(file.Path)
                && LengthToleranceSatisfies(file, length) && BitrateSatisfies(file) && SampleRateSatisfies(file)
                && BitDepthSatisfies(file) && (!filenameChecks || StrictTitleSatisfies(file.Path, title)
                && StrictArtistSatisfies(file.Path, artist) && StrictAlbumSatisfies(file.Path, album));
        }

        public bool StrictTitleSatisfies(string fname, string tname, bool noPath = true)
        {
            if (!StrictTitle || tname.Length == 0)
                return true;

            fname = noPath ? Utils.GetFileNameWithoutExtSlsk(fname) : fname;
            return StrictString(fname, tname, diacrRemove: true, ignoreCase: true);
        }

        public bool StrictArtistSatisfies(string fname, string aname)
        {
            if (!StrictArtist || aname.Length == 0)
                return true;

            return StrictString(fname, aname, diacrRemove: true, ignoreCase: true, boundarySkipWs: false);
        }

        public bool StrictAlbumSatisfies(string fname, string alname)
        {
            if (!StrictAlbum || alname.Length == 0)
                return true;

            return StrictString(Utils.GetDirectoryNameSlsk(fname), alname, diacrRemove: true, ignoreCase: true, boundarySkipWs: true);
        }

        // Equivalent to: replace '_' and Windows-invalid chars with spaces,
        // optionally remove diacritics, trim, and collapse consecutive literal spaces.
        // Kept as one pass because result sorting calls this for every candidate path.
        public static string StrictStringPreprocess(string str, bool diacrRemove = true)
        {
            if (str.Length == 0)
                return str;

            // Equivalent to replacing '_' and Windows-invalid chars with spaces,
            // optionally removing diacritics, trimming, and collapsing spaces.
            // This runs for every sorted candidate path, so keep it single-pass.
            char[] buffer = new char[str.Length];
            int length = 0;
            bool previousWasSpace = false;
            bool hasOutput = false;

            for (int i = 0; i < str.Length; i++)
            {
                char c = NormalizeStrictChar(str[i], diacrRemove);

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

            return length == 0 ? string.Empty : new string(buffer, 0, length);
        }

        private static char NormalizeStrictChar(char c, bool diacrRemove)
        {
            if (c == '_' || IsStrictInvalidChar(c))
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
            tname = StrictStringPreprocess(tname, diacrRemove);

            if (boundarySkipWs)
                return fname.ContainsWithBoundaryIgnoreWs(tname, ignoreCase, acceptLeftDigit: true);
            else
                return fname.ContainsWithBoundary(tname, ignoreCase);
        }

        public static bool BracketCheck(SongQuery query, SongQuery inferred)
        {
            string t1 = query.Title.RemoveFt().Replace('[', '(');
            if (t1.Contains('('))
                return true;

            string t2 = inferred.Title.RemoveFt().Replace('[', '(');
            if (!t2.Contains('('))
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

        public bool LengthToleranceSatisfies(Soulseek.File file, int wantedLength) => LengthToleranceSatisfies(file.Length, wantedLength);
        public bool LengthToleranceSatisfies(TagLib.File file, int wantedLength) => LengthToleranceSatisfies((int)file.Properties.Duration.TotalSeconds, wantedLength);
        public bool LengthToleranceSatisfies(SimpleFile file, int wantedLength) => LengthToleranceSatisfies(file.Length, wantedLength);
        public bool LengthToleranceSatisfies(int? length, int wantedLength)
        {
            if (LengthTolerance == null || LengthTolerance < 0 || wantedLength < 0)
                return true;
            if (length == null || length < 0)
                return AcceptNoLength;
            return Math.Abs((int)length - wantedLength) <= LengthTolerance;
        }

        public bool BitrateSatisfies(Soulseek.File file) => BitrateSatisfies(file.BitRate);
        public bool BitrateSatisfies(TagLib.File file) => BitrateSatisfies(file.Properties.AudioBitrate);
        public bool BitrateSatisfies(SimpleFile file) => BitrateSatisfies(file.Bitrate);
        public bool BitrateSatisfies(int? bitrate)
        {
            return BoundCheck(bitrate, MinBitrate, MaxBitrate);
        }

        public bool SampleRateSatisfies(Soulseek.File file) => SampleRateSatisfies(file.SampleRate);
        public bool SampleRateSatisfies(TagLib.File file) => SampleRateSatisfies(file.Properties.AudioSampleRate);
        public bool SampleRateSatisfies(SimpleFile file) => SampleRateSatisfies(file.Samplerate);
        public bool SampleRateSatisfies(int? sampleRate)
        {
            return BoundCheck(sampleRate, MinSampleRate, MaxSampleRate);
        }

        public bool BitDepthSatisfies(Soulseek.File file) => BitDepthSatisfies(file.BitDepth);
        public bool BitDepthSatisfies(TagLib.File file) => BitDepthSatisfies(file.Properties.BitsPerSample);
        public bool BitDepthSatisfies(SimpleFile file) => BitDepthSatisfies(file.Bitdepth);
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
            return response == null || BannedUsers.Length == 0 || !BannedUsers.Any(x => x == response.Username);
        }

        public string GetNotSatisfiedName(Soulseek.File file, SongQuery? query, SearchResponse? response)
        {
            string title  = query?.Title  ?? "";
            string artist = query?.Artist ?? "";
            string album  = query?.Album  ?? "";
            int length    = query?.Length ?? -1;
            if (!BannedUsersSatisfies(response))
                return "BannedUsers fails";
            if (!StrictTitleSatisfies(file.Filename, title))
                return "StrictTitle fails";
            if (!StrictArtistSatisfies(file.Filename, artist))
                return "StrictArtist fails";
            if (!LengthToleranceSatisfies(file, length))
                return "LengthTolerance fails";
            if (!FormatSatisfies(file.Filename))
                return "Format fails";
            if (!StrictAlbumSatisfies(file.Filename, album))
                return "StrictAlbum fails";
            if (!BitrateSatisfies(file))
                return "Bitrate fails";
            if (!SampleRateSatisfies(file))
                return "SampleRate fails";
            if (!BitDepthSatisfies(file))
                return "BitDepth fails";
            return "Satisfied";
        }

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
        public string[]? Formats;
        public string[]? BannedUsers;
        public bool? AcceptNoLength;
        public bool? AcceptMissingProps;

        public bool IsEmpty()
            => LengthTolerance == null
            && MinBitrate == null
            && MaxBitrate == null
            && MinSampleRate == null
            && MaxSampleRate == null
            && MinBitDepth == null
            && MaxBitDepth == null
            && StrictTitle == null
            && StrictArtist == null
            && StrictAlbum == null
            && Formats == null
            && BannedUsers == null
            && AcceptNoLength == null
            && AcceptMissingProps == null;

        public override bool Equals(object? obj)
        {
            if (obj is not FileConditionPatch other)
                return false;

            return LengthTolerance == other.LengthTolerance
                && MinBitrate == other.MinBitrate
                && MaxBitrate == other.MaxBitrate
                && MinSampleRate == other.MinSampleRate
                && MaxSampleRate == other.MaxSampleRate
                && MinBitDepth == other.MinBitDepth
                && MaxBitDepth == other.MaxBitDepth
                && StrictTitle == other.StrictTitle
                && StrictArtist == other.StrictArtist
                && StrictAlbum == other.StrictAlbum
                && AcceptNoLength == other.AcceptNoLength
                && AcceptMissingProps == other.AcceptMissingProps
                && ((Formats == null && other.Formats == null) || (Formats != null && other.Formats != null && Formats.SequenceEqual(other.Formats)))
                && ((BannedUsers == null && other.BannedUsers == null) || (BannedUsers != null && other.BannedUsers != null && BannedUsers.SequenceEqual(other.BannedUsers)));
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(LengthTolerance);
            hash.Add(MinBitrate);
            hash.Add(MaxBitrate);
            hash.Add(MinSampleRate);
            hash.Add(MaxSampleRate);
            hash.Add(MinBitDepth);
            hash.Add(MaxBitDepth);
            hash.Add(StrictTitle);
            hash.Add(StrictArtist);
            hash.Add(StrictAlbum);
            hash.Add(AcceptNoLength);
            hash.Add(AcceptMissingProps);

            if (Formats != null)
                foreach (var format in Formats)
                    hash.Add(format);

            if (BannedUsers != null)
                foreach (var bannedUser in BannedUsers)
                    hash.Add(bannedUser);

            return hash.ToHashCode();
        }
    }

