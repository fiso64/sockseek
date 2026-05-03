using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core.Settings;

namespace Sldl.Core.Extractors;
    public class StringExtractor : IExtractor, IInputMatcher
    {
        public static bool InputMatches(string input)
        {
            return !input.IsInternetUrl();
        }

        public Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
            bool isAlbum = extraction.IsAlbum;

            if (input.StartsWith("album://"))
            {
                isAlbum = true;
                input = input[8..];
            }

            // Catch the common mistake of passing a local file path without --input-type.
            var expanded = Utils.ExpandVariables(input);
            if (File.Exists(expanded))
                throw new ArgumentException($"Input is a local file. To read it as a track list, specify --input-type list or --input-type csv.");
            ParseArgs(input, isAlbum,
                out string artist, out string title, out string album, out string uri, out int length,
                out bool artistMaybeWrong,
                out int minAlbumTrackCount, out int maxAlbumTrackCount);

            bool treatAsAlbum = isAlbum || (title.Length == 0 && album.Length > 0);

            if (treatAsAlbum)
            {
                var query = new AlbumQuery
                {
                    Artist          = artist,
                    Album           = album,
                    SearchHint      = title,
                    URI             = uri,
                    ArtistMaybeWrong = artistMaybeWrong,
                };
                return Task.FromResult<Job>(new AlbumJob(query)
                {
                    ExtractorFolderCond = new FolderConditionPatch
                    {
                        MinTrackCount = minAlbumTrackCount >= 0 ? minAlbumTrackCount : null,
                        MaxTrackCount = maxAlbumTrackCount >= 0 ? maxAlbumTrackCount : null,
                    },
                });
            }
            else
            {
                var query = new SongQuery
                {
                    Artist          = artist,
                    Title           = title,
                    Album           = album,
                    URI             = uri,
                    Length          = length,
                    ArtistMaybeWrong = artistMaybeWrong,
                };
                return Task.FromResult<Job>(new SongJob(query));
            }
        }

        // Parses a "Artist - Title/Album, key=value, ..." string.
        // Returns all parsed fields as out parameters so callers can build any query type.
        public static void ParseArgs(string input, bool isAlbum,
            out string artist, out string title, out string album, out string uri, out int length,
            out bool artistMaybeWrong,
            out int minAlbumTrackCount, out int maxAlbumTrackCount)
        {
            input = input.Trim();
            artist = ""; title = ""; album = ""; uri = "";
            length = -1; artistMaybeWrong = false;
            minAlbumTrackCount = -1; maxAlbumTrackCount = -1;

            // Capture refs for the closure
            string _artist = "", _title = "", _album = "", _uri = "";
            int _length = -1;
            bool _artistMaybeWrong = false;
            int _minCount = -1, _maxCount = -1;

            var keys = new string[] { "title", "artist", "length", "album", "artist-maybe-wrong", "album-track-count" };

            void setProperty(string key, string value)
            {
                switch (key)
                {
                    case "title":   _title  = value; break;
                    case "artist":  _artist = value; break;
                    case "length":  _length = int.Parse(value); break;
                    case "album":   _album  = value; break;
                    case "artist-maybe-wrong":
                        if (value == "true") _artistMaybeWrong = true;
                        break;
                    case "album-track-count":
                        if (value == "-1")
                        {
                            _minCount = -1;
                            _maxCount = -1;
                        }
                        else if (value.Last() == '-')
                            _maxCount = int.Parse(value[..^1]);
                        else if (value.Last() == '+')
                            _minCount = int.Parse(value[..^1]);
                        else
                        {
                            _minCount = int.Parse(value);
                            _maxCount = _minCount;
                        }
                        break;
                }
            }

            var parts = input.Split(',');
            var other = "";
            string? currentKey = null;
            string? currentVal = null;
            bool otherFieldDone = false;

            for (int i = 0; i < parts.Length; i++)
            {
                var x = parts[i];
                bool keyval = false;

                if (x.Contains('='))
                {
                    var lr = x.Split('=', 2);
                    lr[0] = lr[0].Trim();
                    if (lr.Length == 2 && keys.Contains(lr[0]))
                    {
                        if (currentKey != null && currentVal != null)
                            setProperty(currentKey, currentVal.Trim());
                        currentKey = lr[0];
                        currentVal = lr[1];
                        keyval = true;
                        otherFieldDone = true;
                    }
                }

                if (!keyval && currentVal != null)
                    currentVal += ',' + x;

                if (!otherFieldDone)
                {
                    if (i > 0) other += ',';
                    other += x;
                }
            }

            if (currentKey != null && currentVal != null)
                setProperty(currentKey, currentVal.Trim());

            other = other.Trim();

            if (other.Length > 0 && (isAlbum && _album.Length > 0 || !isAlbum && _title.Length > 0))
            {
                Logger.Warn($"Warning: Input part '{other}' provided without a property name " +
                    $"and album or title is already set. Ignoring.");
            }
            else if (other.Length > 0)
            {
                var splitParts = other.Split(" - ", 2, StringSplitOptions.TrimEntries);

                if (splitParts.Length == 1 || _artist.Length > 0)
                {
                    if (isAlbum)
                        _album = other.Trim();
                    else
                        _title = other.Trim();
                }
                else
                {
                    _artist = splitParts[0];
                    if (isAlbum)
                        _album = splitParts[1];
                    else
                        _title = splitParts[1];
                }
            }

            if (_title.Length == 0 && _album.Length == 0 && _artist.Length == 0)
                throw new ArgumentException("Track string must contain title, album or artist.");

            artist = _artist; title = _title; album = _album; uri = _uri;
            length = _length; artistMaybeWrong = _artistMaybeWrong;
            minAlbumTrackCount = _minCount; maxAlbumTrackCount = _maxCount;
        }

        // Legacy shim kept for ListExtractor which calls this with track-shaped results.
        public static SongQuery ParseTrackArg(string input, bool isAlbum)
        {
            ParseArgs(input, isAlbum,
                out string artist, out string title, out string album, out string uri, out int length,
                out bool artistMaybeWrong,
                out int _min, out int _max);

            string effectiveTitle = isAlbum ? (album.Length > 0 ? album : title) : title;
            return new SongQuery
            {
                Artist          = artist,
                Title           = effectiveTitle,
                Album           = album,
                URI             = uri,
                Length          = length,
                ArtistMaybeWrong = artistMaybeWrong,
            };
        }
    }
