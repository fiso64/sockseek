using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using System.Text.RegularExpressions;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;
    public static class Preprocessor
    {
        /// <summary>
        /// Applies all configured preprocessing transformations to song.Query.
        /// Sets song.Query = new SongQuery(song.Query) { ... } (copy-and-replace — no mutation of original).
        /// </summary>
        public static void PreprocessSong(SongJob song, PreprocessSettings preprocess)
            => song.Query = Apply(song.Query, preprocess);

        internal static SongQuery Apply(SongQuery q, PreprocessSettings preprocess)
        {
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

            artist = artist.Trim();
            title = title.Trim();
            album = album.Trim();
            return q.Artist == artist && q.Title == title && q.Album == album
                ? q : new SongQuery(q) { Artist = artist, Title = title, Album = album };
        }

        /// <summary>
        /// Applies all configured preprocessing transformations to job.Query.
        /// Sets job.Query = new AlbumQuery(job.Query) { ... }.
        /// </summary>
        public static void PreprocessAlbum(AlbumJob job, PreprocessSettings preprocess)
            => job.Query = Apply(job.Query, preprocess);

        internal static AlbumQuery Apply(AlbumQuery q, PreprocessSettings preprocess)
        {
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

            artist = artist.Trim();
            album = album.Trim();
            return q.Artist == artist && q.Album == album
                ? q : new AlbumQuery(q) { Artist = artist, Album = album };
        }

        /// <summary>
        /// Preprocesses all songs/albums in a job according to its Config.
        /// Called per-job during the main loop, just before download begins.
        /// </summary>
        public static void PreprocessJob(Job job, PreprocessSettings preprocess)
        {
            switch (job)
            {
                case SongJob song:
                    PreprocessSong(song, preprocess);
                    break;

                case JobList jl:
                    foreach (var song in jl.Jobs.OfType<SongJob>())
                        PreprocessSong(song, preprocess);
                    foreach (var aj in jl.Jobs.OfType<AlbumJob>())
                        PreprocessAlbum(aj, preprocess);
                    break;

                case AlbumJob aj:
                    PreprocessAlbum(aj, preprocess);
                    break;

                case SongGroupsJob ag:
                    foreach (var song in ag.Songs)
                        PreprocessSong(song, preprocess);
                    break;

                case AlbumGroupsJob aaj:
                    aaj.Query = Apply(aaj.Query, preprocess);
                    break;
            }
        }
    }
