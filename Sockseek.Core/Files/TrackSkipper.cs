using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;
    public readonly record struct NameIndexEntry(string PathPart, string Name, bool IsKnownAudio);
    public readonly record struct NameConditionIndexEntry(string PathPart, string Name, SimpleFile File, bool IsKnownAudio);
    public readonly record struct TagIndexEntry(string Artist, string Title, string Album, string AlbumArtist, bool IsKnownAudio);
    public readonly record struct TagConditionIndexEntry(string Artist, string Title, string Album, string AlbumArtist, SimpleFile File, bool IsKnownAudio);

    public static class TrackSkipperRegistry
    {
        public static TrackSkipper GetSkipper(SkipMode mode, string dir, bool useConditions)
        {
            return mode switch
            {
                SkipMode.Name  => useConditions ? new NameConditionalSkipper(dir) : new NameSkipper(dir),
                SkipMode.Tag   => useConditions ? new TagConditionalSkipper(dir)  : new TagSkipper(dir),
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

    public abstract class FileBasedSkipper<T> : TrackSkipper
    {
        protected abstract IEnumerable<(string path, T item)> Index { get; }
        protected abstract string Preprocess(string s, bool removeSlash, bool isQuery);
        protected abstract bool FileMatchesSong(string path, string artist, string title, T item, TrackSkipperContext context, SongJob? song);
        protected abstract bool DirectoryMatchesAlbum(string directory, string? albumArtist, string album, T item, TrackSkipperContext context, AlbumJob job);

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            string title  = Preprocess(song.Query.Title, true, true);
            string artist = Preprocess(song.Query.Artist, true, true);

            foreach ((var path, T item) in Index)
            {
                if (FileMatchesSong(path, artist, title, item, context, song))
                {
                    foundPath = path;
                    return true;
                }
            }
            return false;
        }

        public override bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            string? albumArtist = job.Query.Album.Length > 0 ? null : job.Query.Artist;
            string album  = Preprocess(job.Query.Album,  true, true);
            string artist = Preprocess(job.Query.Artist, true, true);
            var parents   = new HashSet<string>();

            // Use SearchHint only for "track within album" style queries; QueryTrack.Title is the album title.
            string title = job.Query.Album.Length == 0 ? Preprocess(job.Query.SearchHint, true, true) : "";
            bool hasTitle = title.Length > 0;

            foreach ((var path, T item) in Index)
            {
                if (hasTitle && !FileMatchesSong(path, artist, title, item, context, null))
                    continue;

                var parent = Path.GetDirectoryName(path);
                if (parent == null || parents.Contains(parent))
                    continue;

                if (DirectoryMatchesAlbum(parent, albumArtist, album, item, context, job))
                {
                    if (ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(context.folderConditions, parent))
                    {
                        foundPath = parent;
                        return true;
                    }
                    parents.Add(parent);
                }
            }
            return false;
        }
    }

    public class NameSkipper : FileBasedSkipper<NameIndexEntry>
    {
        private readonly List<(string path, NameIndexEntry item)> index = new();
        protected override IEnumerable<(string path, NameIndexEntry item)> Index => index;
        readonly string[] ignore = { "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;

        public NameSkipper(string dir) { this.dir = dir; }

        protected override string Preprocess(string s, bool removeSlash, bool isQuery)
        {
            s = s.ToLower();
            if (isQuery) s = s.RemoveFt();
            s = s.Replace(ignore, " ");
            s = s.ReplaceInvalidChars(' ', false, removeSlash);
            return s.RemoveConsecutiveWs().Trim();
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir)) { IndexIsBuilt = true; return; }
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (FileIndexPathPolicy.ShouldIgnoreNameIndexPath(path))
                    continue;

                string ppath = Preprocess(FileIndexPathPolicy.RelativePathWithoutExtension(dir, path), false, false);
                string pname = Path.GetFileName(ppath);
                index.Add((path, new NameIndexEntry(ppath, pname, Utils.IsMusicFile(path))));
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, NameIndexEntry item, TrackSkipperContext c, SongJob? song)
            => item.Name.ContainsWithBoundary(title) && item.PathPart.ContainsWithBoundary(artist);

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, NameIndexEntry item, TrackSkipperContext c, AlbumJob job)
            => item.IsKnownAudio
                && item.PathPart.ContainsWithBoundary(album)
                && item.PathPart.ContainsWithBoundary(albumArtist);
    }

    public class NameConditionalSkipper : FileBasedSkipper<NameConditionIndexEntry>
    {
        private readonly Dictionary<string, List<(string path, NameConditionIndexEntry item)>> index = new();
        protected override IEnumerable<(string path, NameConditionIndexEntry item)> Index => index.Values.SelectMany(x => x);
        readonly string[] ignore = { "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;

        public NameConditionalSkipper(string dir) { this.dir = dir; }

        protected override string Preprocess(string s, bool removeSlash, bool isQuery)
        {
            s = s.ToLower();
            if (isQuery) s = s.RemoveFt();
            s = s.Replace(ignore, " ");
            s = s.ReplaceInvalidChars(' ', false, removeSlash);
            return s.RemoveConsecutiveWs().Trim();
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir)) { IndexIsBuilt = true; return; }
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (FileIndexPathPolicy.ShouldIgnoreNameIndexPath(path))
                    continue;

                string ppath  = Preprocess(FileIndexPathPolicy.RelativePathWithoutExtension(dir, path), false, false);
                string pname  = Path.GetFileName(ppath);
                var    parent = Utils.NormalizedPath(Path.GetDirectoryName(path)!);
                var    simpleFile = new SimpleFile(path);
                var    item = new NameConditionIndexEntry(ppath, pname, simpleFile, Utils.IsMusicFile(path));

                if (!index.TryGetValue(parent, out var value))
                    index[parent] = new() { (path, item) };
                else
                    value.Add((path, item));
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, NameConditionIndexEntry item, TrackSkipperContext context, SongJob? song)
            => item.Name.ContainsWithBoundary(title)
            && item.PathPart.ContainsWithBoundary(artist)
            && (context.conditions == null || ConditionSatisfactionPolicy.LocalFileSatisfies(context.conditions, item.File, song?.Query));

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, NameConditionIndexEntry item, TrackSkipperContext context, AlbumJob job)
        {
            if (!item.PathPart.ContainsWithBoundary(album) || !item.PathPart.ContainsWithBoundary(albumArtist))
                return false;
            if (context.conditions == null)
                return true;

            if (!item.IsKnownAudio)
                return false;

            var parent = Utils.NormalizedPath(Path.GetDirectoryName(item.File.Path)!);
            return AlbumConditionsSatisfy(
                index[parent].Where(x => x.item.IsKnownAudio).Select(x => x.item.File),
                context,
                job);
        }
    }

    public class TagSkipper : FileBasedSkipper<TagIndexEntry>
    {
        readonly string dir;
        readonly List<(string path, TagIndexEntry item)> index = new();
        protected override IEnumerable<(string path, TagIndexEntry item)> Index => index;

        public TagSkipper(string dir) { this.dir = dir; }

        protected override string Preprocess(string s, bool _, bool isQuery)
        {
            if (isQuery) s = s.RemoveFt();
            return s.Replace(" ", "").ToLower();
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir)) { IndexIsBuilt = true; return; }
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (FileIndexPathPolicy.ShouldIgnoreTagIndexPath(path))
                    continue;

                try
                {
                    using var musicFile = TagLib.File.Create(path);
                    string partist      = Preprocess(musicFile.Tag.JoinedPerformers ?? "",   false, false);
                    string ptitle       = Preprocess(musicFile.Tag.Title ?? "",              false, false);
                    string palbum       = Preprocess(musicFile.Tag.Album ?? "",              false, false);
                    string palbumArtist = Preprocess(musicFile.Tag.JoinedAlbumArtists ?? "", false, false);
                    index.Add((path, new TagIndexEntry(partist, ptitle, palbum, palbumArtist, Utils.IsMusicFile(path))));
                }
                catch (Exception) { continue; }
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, TagIndexEntry item, TrackSkipperContext c, SongJob? song)
            => title == item.Title && item.Artist.Contains(artist);

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, TagIndexEntry item, TrackSkipperContext c, AlbumJob job)
            => item.IsKnownAudio
                && album == item.Album
                && (albumArtist == null || item.AlbumArtist.Contains(albumArtist));
    }

    public class TagConditionalSkipper : FileBasedSkipper<TagConditionIndexEntry>
    {
        readonly string dir;
        private readonly Dictionary<string, List<(string path, TagConditionIndexEntry item)>> index = new();
        protected override IEnumerable<(string path, TagConditionIndexEntry item)> Index => index.Values.SelectMany(x => x);

        public TagConditionalSkipper(string dir) { this.dir = dir; }

        protected override string Preprocess(string s, bool _, bool isQuery)
        {
            if (isQuery) s = s.RemoveFt();
            return s.Replace(" ", "").ToLower();
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir)) { IndexIsBuilt = true; return; }
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (FileIndexPathPolicy.ShouldIgnoreTagIndexPath(path))
                    continue;

                try
                {
                    using var musicFile = TagLib.File.Create(path);
                    string partist      = Preprocess(musicFile.Tag.JoinedPerformers ?? "",   false, false);
                    string ptitle       = Preprocess(musicFile.Tag.Title ?? "",              false, false);
                    string palbum       = Preprocess(musicFile.Tag.Album ?? "",              false, false);
                    string palbumArtist = Preprocess(musicFile.Tag.JoinedAlbumArtists ?? "", false, false);
                    var    parent       = Utils.NormalizedPath(Path.GetDirectoryName(path)!);
                    var    simpleFile   = new SimpleFile(musicFile);
                    var    item = new TagConditionIndexEntry(
                        partist,
                        ptitle,
                        palbum,
                        palbumArtist,
                        simpleFile,
                        Utils.IsMusicFile(path));

                    if (!index.TryGetValue(parent, out var value))
                        index[parent] = new() { (path, item) };
                    else
                        value.Add((path, item));
                }
                catch (Exception) { continue; }
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, TagConditionIndexEntry item, TrackSkipperContext c, SongJob? song)
            => title == item.Title
            && item.Artist.Contains(artist)
            && (c.conditions == null || ConditionSatisfactionPolicy.LocalFileSatisfies(c.conditions, item.File, song?.Query));

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, TagConditionIndexEntry item, TrackSkipperContext c, AlbumJob job)
        {
            if (!item.IsKnownAudio || album != item.Album) return false;
            if (albumArtist != null && !item.AlbumArtist.Contains(albumArtist)) return false;
            if (c.conditions == null) return true;

            var parent = Utils.NormalizedPath(Path.GetDirectoryName(item.File.Path)!);
            return AlbumConditionsSatisfy(
                index[parent].Where(x => x.item.IsKnownAudio).Select(x => x.item.File),
                c,
                job);
        }
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

        public static bool ShouldIgnoreNameIndexPath(string path)
            => IsTransientSockseekPath(path);

        public static bool ShouldIgnoreTagIndexPath(string path)
            => IsTransientSockseekPath(path);

        private static bool IsTransientSockseekPath(string path)
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
                    audioFilePaths))
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
