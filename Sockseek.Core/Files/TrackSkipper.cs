using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;
    public static class TrackSkipperRegistry
    {
        public static TrackSkipper GetSkipper(SkipMode mode, string dir, bool useConditions)
        {
            return mode switch
            {
                SkipMode.Name  => new NameSkipper(dir, useConditions),
                SkipMode.Tag   => new TagSkipper(dir, useConditions),
                SkipMode.Index => useConditions ? new IndexConditionalSkipper()   : new IndexSkipper(),
                _ => throw new ArgumentException("Invalid SkipMode")
            };
        }
    }

    public struct TrackSkipperContext
    {
        public FileConditions? conditions;
        public FolderConditions? folderConditions;
        public DownloadOutputFile?      indexEditor;
        public bool            checkFileExists;
        public SearchSettings? searchSettings;

        public static TrackSkipperContext From(JobContext ctx, SkipSettings skip, SearchSettings search)
        {
            FileConditions? cond = null;
            if (skip.SkipCheckPrefCond)
                cond = search.NecessaryCond.With(search.PreferredCond);
            else if (skip.SkipCheckCond)
                cond = search.NecessaryCond;

            var folderCond = skip.SkipCheckPrefCond
                ? search.NecessaryFolderCond.With(search.PreferredFolderCond)
                : search.NecessaryFolderCond;

            return new TrackSkipperContext
            {
                checkFileExists = cond != null,
                indexEditor     = ctx.IndexEditor,
                conditions      = cond,
                folderConditions = folderCond,
                searchSettings  = search,
            };
        }
    }

    public abstract class TrackSkipper
    {
        // Returns true if the given song already exists. foundPath is where it was found.
        public abstract bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath);

        // Returns true if the given album job already exists. foundPath is the directory.
        public virtual bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            return false;
        }

        public virtual void BuildIndex() { IndexIsBuilt = true; }
        public bool IndexIsBuilt { get; protected set; } = false;

        protected static bool AlbumConditionsSatisfy(IEnumerable<SimpleFile> files, TrackSkipperContext context, AlbumJob job)
            => context.conditions == null
                || ConditionSatisfactionPolicy.LocalAlbumSatisfies(files, context.conditions, context.searchSettings, job.Query);
    }

    internal abstract class FileBasedSkipper<T>(string root, bool useConditions) : TrackSkipper where T : struct
    {
        protected string Root => root;
        protected bool UseConditions => useConditions;
        protected readonly record struct Entry(string Path, T Match, bool IsAudio, SimpleFile? Metadata);
        private sealed record FileIndex(Entry[] Files, ILookup<string, SimpleFile>? AudioByDirectory);
        private FileIndex index = new([], null);

        protected abstract string Preprocess(string text, bool isQuery);
        protected abstract Entry? Read(string path);
        protected abstract bool MatchesSong(T item, string artist, string title);
        protected abstract bool MatchesAlbum(T item, string? albumArtist, string album);
        protected virtual bool RequiresAudioForAlbum(TrackSkipperContext context) => true;

        public override void BuildIndex()
        {
            Entry[] files = Directory.Exists(root) ? ReadFiles().ToArray() : [];
            var audioByDirectory = useConditions
                ? files.Where(file => file.IsAudio).ToLookup(
                    file => Utils.NormalizedPath(Path.GetDirectoryName(file.Path)!), file => file.Metadata!)
                : null;
            index = new(files, audioByDirectory);
            IndexIsBuilt = true;
        }

        private IEnumerable<Entry> ReadFiles()
        {
            // Collect paths first so the matching data is allocated together for repeated scans.
            foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                if (!FileIndexPathPolicy.IsTransientSockseekPath(path) && Read(path) is { } entry)
                    yield return entry;
        }

        private bool SongMatches(in Entry file, string artist, string title, FileConditions? conditions, SongQuery? query)
            => MatchesSong(file.Match, artist, title)
                && (file.Metadata is null || conditions is null
                    || ConditionSatisfactionPolicy.LocalFileSatisfies(conditions, file.Metadata, query));

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            string title = Preprocess(song.Query.Title, true);
            string artist = Preprocess(song.Query.Artist, true);
            foreach (var file in index.Files)
            {
                if (SongMatches(file, artist, title, context.conditions, song.Query))
                {
                    foundPath = file.Path;
                    return true;
                }
            }
            foundPath = null;
            return false;
        }

        public override bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath)
        {
            var query = job.Query;
            string? albumArtist = query.Album.Length > 0 ? null : query.Artist;
            string album = Preprocess(query.Album, true);
            string artist = Preprocess(query.Artist, true);
            string title = query.Album.Length == 0 ? Preprocess(query.SearchHint, true) : "";
            var checkedDirectories = new HashSet<string>();
            var snapshot = index;
            bool requireAudio = RequiresAudioForAlbum(context);
            foreach (var file in snapshot.Files)
            {
                if ((!file.IsAudio && requireAudio)
                    || !MatchesAlbum(file.Match, albumArtist, album)
                    || (title.Length > 0 && !SongMatches(file, artist, title, context.conditions, null)))
                    continue;

                string? parent = Path.GetDirectoryName(file.Path);
                if (parent is null || !checkedDirectories.Add(parent)) continue;
                if (snapshot.AudioByDirectory != null && context.conditions != null
                    && !AlbumConditionsSatisfy(snapshot.AudioByDirectory[Utils.NormalizedPath(parent)], context, job))
                    continue;
                if (ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(context.folderConditions, parent))
                {
                    foundPath = parent;
                    return true;
                }
            }
            foundPath = null;
            return false;
        }
    }

    internal sealed class NameSkipper(string root, bool useConditions = false)
        : FileBasedSkipper<(string PathPart, string Name)>(root, useConditions)
    {
        private static readonly string[] ignore = ["_", "-", ".", "(", ")", "[", "]"];

        protected override string Preprocess(string text, bool isQuery)
        {
            text = text.ToLower();
            if (isQuery) text = text.RemoveFt();
            return text.Replace(ignore, " ").ReplaceInvalidChars(' ', false, isQuery).RemoveConsecutiveWs().Trim();
        }

        protected override Entry? Read(string path)
        {
            string relative = Preprocess(FileIndexPathPolicy.RelativePathWithoutExtension(Root, path), false);
            return new(path, (relative, Path.GetFileName(relative)), Utils.IsMusicFile(path),
                UseConditions ? new SimpleFile(path) : null);
        }

        protected override bool MatchesSong((string PathPart, string Name) item, string artist, string title)
            => item.Name.ContainsWithBoundary(title) && item.PathPart.ContainsWithBoundary(artist);

        protected override bool MatchesAlbum((string PathPart, string Name) item, string? albumArtist, string album)
            => item.PathPart.ContainsWithBoundary(album) && item.PathPart.ContainsWithBoundary(albumArtist);

        protected override bool RequiresAudioForAlbum(TrackSkipperContext context)
            => !UseConditions || context.conditions != null;
    }

    internal readonly record struct TrackTags(string Artist, string Title, string Album, string AlbumArtist);

    internal sealed class TagSkipper(string root, bool useConditions = false) : FileBasedSkipper<TrackTags>(root, useConditions)
    {
        protected override string Preprocess(string text, bool isQuery)
            => (isQuery ? text.RemoveFt() : text).Replace(" ", "").ToLower();

        protected override Entry? Read(string path)
        {
            try
            {
                using var file = TagLib.File.Create(path);
                var tags = new TrackTags(Preprocess(file.Tag.JoinedPerformers ?? "", false),
                    Preprocess(file.Tag.Title ?? "", false), Preprocess(file.Tag.Album ?? "", false),
                    Preprocess(file.Tag.JoinedAlbumArtists ?? "", false));
                return new(path, tags, Utils.IsMusicFile(path), UseConditions ? new SimpleFile(file) : null);
            }
            catch (Exception) { return null; }
        }

        protected override bool MatchesSong(TrackTags item, string artist, string title)
            => title == item.Title && item.Artist.Contains(artist);

        protected override bool MatchesAlbum(TrackTags item, string? albumArtist, string album)
            => album == item.Album && (albumArtist == null || item.AlbumArtist.Contains(albumArtist));
    }

    internal static class FileIndexPathPolicy
    {
        public static string RelativePathWithoutExtension(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            return Path.Join(
                Path.GetDirectoryName(relative),
                Path.GetFileNameWithoutExtension(relative));
        }

        public static bool IsTransientSockseekPath(string path)
        {
            var segments = Utils.NormalizedPath(path)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Contains(OutputStaging.DirectoryName, StringComparer.OrdinalIgnoreCase)
                || path.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class IndexSkipper : TrackSkipper
    {
        public IndexSkipper() { IndexIsBuilt = true; }

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor?.PreviousRunResult(song, context.searchSettings);
            if (t == null || (t.State != JobStateOld.Done && t.State != JobStateOld.AlreadyExists))
                return false;

            if (context.checkFileExists)
            {
                if (string.IsNullOrEmpty(t.DownloadPath)) return false;
                if (!File.Exists(t.DownloadPath)) return false;
            }

            foundPath = t.DownloadPath;
            return true;
        }

        public override bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor?.PreviousRunResult(job);
            if (t == null || (t.State != JobStateOld.Done && t.State != JobStateOld.AlreadyExists))
                return false;

            if (context.checkFileExists)
            {
                if (string.IsNullOrEmpty(t.DownloadPath)) return false;
                if (!Directory.Exists(t.DownloadPath)) return false;
            }

            foundPath = t.DownloadPath;
            return true;
        }
    }

    public class IndexConditionalSkipper : TrackSkipper
    {
        public IndexConditionalSkipper() { IndexIsBuilt = true; }

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor?.PreviousRunResult(song, context.searchSettings);
            if (context.conditions == null || t == null || string.IsNullOrEmpty(t.DownloadPath) || !File.Exists(t.DownloadPath))
                return false;

            try
            {
                using var musicFile = TagLib.File.Create(t.DownloadPath);
                if (ConditionSatisfactionPolicy.LocalFileSatisfies(context.conditions, musicFile, song.Query))
                {
                    foundPath = t.DownloadPath;
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor?.PreviousRunResult(job);
            if (context.conditions == null || t == null || string.IsNullOrEmpty(t.DownloadPath) || !Directory.Exists(t.DownloadPath))
                return false;

            var files = Directory.GetFiles(t.DownloadPath, "*", SearchOption.AllDirectories);

            var audioFilePaths = files.Where(Utils.IsMusicFile).ToList();
            if (!ConditionSatisfactionPolicy.LocalAlbumFolderSatisfies(
                    context.folderConditions,
                    audioFilePaths.Count,
                    files))
                return false;

            var audioFiles = new List<SimpleFile>();
            foreach (var path in audioFilePaths)
            {
                try
                {
                    using var musicFile = TagLib.File.Create(path);
                    audioFiles.Add(new SimpleFile(musicFile));
                }
                catch (Exception) { return false; }
            }

            if (!AlbumConditionsSatisfy(audioFiles, context, job))
                return false;

            foundPath = t.DownloadPath;
            return true;
        }
    }
