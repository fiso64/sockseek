using Sldl.Core.Jobs;
using Sldl.Core.Models;
using System.Text.RegularExpressions;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;
    public static class Preprocessor
    {
        /// <summary>
        /// Applies all configured preprocessing transformations to song.Query.
        /// Sets song.Query = new SongQuery(song.Query) { ... } (copy-and-replace — no mutation of original).
        /// </summary>
        public static void PreprocessSong(SongJob song, PreprocessSettings preprocess)
        {
            var q = song.Query;
            string artist = q.Artist;
            string title  = q.Title;
            string album  = q.Album;

            if (preprocess.RemoveFt)
            {
                title  = title.RemoveFt();
                artist = artist.RemoveFt();
            }

            if (preprocess.RemoveBrackets)
            {
                title = title.RemoveSquareBrackets();
            }

            if (preprocess.Regex != null)
            {
                foreach (var (toReplace, replaceBy) in preprocess.Regex)
                {
                    title  = Regex.Replace(title,  toReplace.Title,  replaceBy.Title,  RegexOptions.IgnoreCase);
                    artist = Regex.Replace(artist, toReplace.Artist, replaceBy.Artist, RegexOptions.IgnoreCase);
                    album  = Regex.Replace(album,  toReplace.Album,  replaceBy.Album,  RegexOptions.IgnoreCase);
                }
            }

            if (!string.IsNullOrEmpty(preprocess.ParseTitleTemplate) && title.Length > 0)
            {
                var updated = new SongQuery(q) { Artist = artist, Title = title, Album = album };
                TrackTemplateParser.TryUpdateSongQuery(title, preprocess.ParseTitleTemplate, ref updated);
                artist = updated.Artist;
                title  = updated.Title;
                album  = updated.Album;
            }

            if (preprocess.ExtractArtist && title.Length > 0)
            {
                (var parsedArtist, var parsedTitle) = Utils.SplitArtistAndTitle(title);
                if (parsedArtist != null)
                {
                    artist = parsedArtist;
                    title  = parsedTitle;
                }
            }

            song.Query = new SongQuery(q)
            {
                Artist          = artist.Trim(),
                Title           = title.Trim(),
                Album           = album.Trim(),
            };
        }

        /// <summary>
        /// Applies all configured preprocessing transformations to job.Query.
        /// Sets job.Query = new AlbumQuery(job.Query) { ... }.
        /// </summary>
        public static void PreprocessAlbum(AlbumJob job, PreprocessSettings preprocess)
        {
            var q = job.Query;
            string artist = q.Artist;
            string album  = q.Album;

            if (preprocess.RemoveFt)
                artist = artist.RemoveFt();

            if (preprocess.Regex != null)
            {
                foreach (var (toReplace, replaceBy) in preprocess.Regex)
                {
                    artist = Regex.Replace(artist, toReplace.Artist, replaceBy.Artist, RegexOptions.IgnoreCase);
                    album  = Regex.Replace(album,  toReplace.Album,  replaceBy.Album,  RegexOptions.IgnoreCase);
                }
            }

            job.Query = new AlbumQuery(q)
            {
                Artist = artist.Trim(),
                Album  = album.Trim(),
            };
        }

        /// <summary>
        /// Preprocesses all songs/albums in a job according to its Config.
        /// Called per-job during the main loop, just before download begins.
        /// </summary>
        public static void PreprocessJob(Job job, PreprocessSettings preprocess)
        {
            switch (job)
            {
                case JobList jl:
                    foreach (var song in jl.Jobs.OfType<SongJob>())
                        PreprocessSong(song, preprocess);
                    foreach (var aj in jl.Jobs.OfType<AlbumJob>())
                        PreprocessAlbum(aj, preprocess);
                    break;

                case AlbumJob aj:
                    PreprocessAlbum(aj, preprocess);
                    break;

                case AggregateJob ag:
                    foreach (var song in ag.Songs)
                        PreprocessSong(song, preprocess);
                    break;

                case AlbumAggregateJob aaj:
                    // AlbumAggregateJob only has an AlbumQuery, preprocess artist/album.
                    // Synthesise a temporary AlbumJob to reuse PreprocessAlbum.
                    var tempAlbum = new AlbumJob(aaj.Query);
                    PreprocessAlbum(tempAlbum, preprocess);
                    aaj.Query = tempAlbum.Query;
                    break;
            }
        }
    }
