namespace Sldl.Core.Models;
    public class AlbumQuery
    {
        public string Artist { get; init; } = "";
        public string Album  { get; init; } = "";
        // Optional song-title hint: used as the network search keyword when Album is empty,
        // but NOT used for folder-name matching. Allows "search albums by song title".
        // TODO: Revisit AlbumQuery.SearchHint semantics. It may be cleaner for the hint
        // to qualify folders that contain a matching track, while still showing all files
        // from matching folders that were present in the search response.
        public string SearchHint { get; init; } = "";
        public string URI    { get; init; } = "";
        public bool   ArtistMaybeWrong { get; init; }

        public AlbumQuery() { }

        public AlbumQuery(AlbumQuery other)
        {
            Artist         = other.Artist;
            Album          = other.Album;
            SearchHint     = other.SearchHint;
            URI            = other.URI;
            ArtistMaybeWrong = other.ArtistMaybeWrong;
        }

        public string ToKey()
        {
            // Keep numeric suffix compatible with old index files (TrackType.Album = 1)
            return $"{Artist};{Album};1";
        }

        public override string ToString() => ToString(noInfo: false);

        public string ToString(bool noInfo)
        {
            string str = Artist;

            if (Album.Length > 0)
            {
                if (str.Length > 0)
                    str += " - ";
                str += Album;
                if (!noInfo)
                    str += " (album)";
            }
            else if (!noInfo)
            {
                str += " (artist)";
            }

            return str;
        }

        public static AlbumQuery FromSongQuery(SongQuery q)
            => new AlbumQuery { Artist = q.Artist, Album = q.Album, URI = q.URI, ArtistMaybeWrong = q.ArtistMaybeWrong };
    }
