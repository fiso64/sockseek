namespace Sldl.Core.Models;
    public class SongQuery
    {
        public string Artist { get; init; } = "";
        public string Title  { get; init; } = "";
        public string Album  { get; init; } = "";  // hint for search/filtering
        public string URI    { get; init; } = "";  // optional source URI/ID metadata
        public int    Length { get; init; } = -1;  // seconds; -1 = unknown
        public bool   ArtistMaybeWrong { get; init; }

        public SongQuery() { }

        public SongQuery(SongQuery other)
        {
            Artist         = other.Artist;
            Title          = other.Title;
            Album          = other.Album;
            URI            = other.URI;
            Length         = other.Length;
            ArtistMaybeWrong = other.ArtistMaybeWrong;
        }

        public string ToKey()
        {
            // Keep numeric suffix compatible with old index files (TrackType.Normal = 0)
            return $"{Artist};{Album};{Title};{Length};0";
        }

        public override string ToString() => ToString(noInfo: false);

        public string ToString(bool noInfo)
        {
            string str = Artist;

            if (Title.Length > 0 || Album.Length > 0)
            {
                if (str.Length > 0)
                    str += " - ";

                str += Title.Length > 0 ? Title : Album;

                if (!noInfo && Length > 0)
                    str += $" ({Length}s)";
            }
            else if (!noInfo)
            {
                str += " (artist)";
            }

            return str;
        }
    }

    public class SongQueryComparer : IEqualityComparer<SongQuery>
    {
        private readonly bool _ignoreCase;
        private readonly int _lenTol;

        public SongQueryComparer(bool ignoreCase = false, int lenTol = -1)
        {
            _ignoreCase = ignoreCase;
            _lenTol     = lenTol;
        }

        public bool Equals(SongQuery? a, SongQuery? b)
        {
            if (a == null || b == null) return a == b;
            if (ReferenceEquals(a, b)) return true;

            var cmp = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            bool stringsMatch =
                string.Equals(a.Title,  b.Title,  cmp) &&
                string.Equals(a.Artist, b.Artist, cmp) &&
                string.Equals(a.Album,  b.Album,  cmp);

            if (!stringsMatch) return false;

            if (_lenTol == -1) return true;
            if (a.Length == -1 && b.Length == -1) return true;
            if (a.Length == -1 || b.Length == -1) return false;
            return Math.Abs(a.Length - b.Length) <= _lenTol;
        }

        public int GetHashCode(SongQuery a)
        {
            unchecked
            {
                var comparer = _ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                int hash = 17;
                hash = hash * 23 + comparer.GetHashCode(a.Title);
                hash = hash * 23 + comparer.GetHashCode(a.Artist);
                hash = hash * 23 + comparer.GetHashCode(a.Album);
                return hash;
            }
        }
    }
