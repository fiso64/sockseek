using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;
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
        public M3uEditor?      indexEditor;
        public bool            checkFileExists;
        public SearchSettings? searchSettings;

        public static TrackSkipperContext From(JobContext ctx, SkipSettings skip, SearchSettings search)
        {
            FileConditions? cond = null;
            if (skip.SkipCheckPrefCond)
                cond = search.NecessaryCond.With(search.PreferredCond);
            else if (skip.SkipCheckCond)
                cond = search.NecessaryCond;

            return new TrackSkipperContext
            {
                checkFileExists = cond != null,
                indexEditor     = ctx.IndexEditor,
                conditions      = cond,
                folderConditions = search.NecessaryFolderCond,
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

            // Use title search from QueryTrack for "track within album" style queries
            string title = Preprocess(job.QueryTrack.Title, true, true);
            bool hasTitle = title.Length > 0;

            foreach ((var path, T item) in Index)
            {
                if (hasTitle && !FileMatchesSong(path, artist, title, item, context, null))
                    continue;

                var parent = Path.GetDirectoryName(path);
                if (!parents.Contains(parent) && DirectoryMatchesAlbum(parent, albumArtist, album, item, context, job))
                {
                    var folderCond = context.folderConditions ?? new FolderConditions();
                    if (FileBasedSkipper<T>.DirectoryHasGoodCount(parent, folderCond.MinTrackCount, folderCond.MaxTrackCount))
                    {
                        foundPath = parent;
                        return true;
                    }
                    parents.Add(parent);
                }
            }
            return false;
        }

        public static bool DirectoryHasGoodCount(string dir, int? min = null, int? max = null)
        {
            if ((min ?? 0) <= 0 && max == null)
                return true;
            var count = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Count(x => Utils.IsMusicFile(x));
            return count >= (min ?? 0) && (max == null || count <= max);
        }
    }

    public class NameSkipper : FileBasedSkipper<(string ppath, string pname)>
    {
        private readonly List<(string path, (string ppath, string pname) item)> index = new();
        protected override IEnumerable<(string path, (string ppath, string pname) item)> Index => index;
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
            int removeLen = Preprocess(dir, false, false).Length + 1;
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Utils.IsMusicFile(path))
                {
                    string ppath = Preprocess(path[removeLen..path.LastIndexOf('.')], false, false);
                    string pname = Path.GetFileName(ppath);
                    index.Add((path, (ppath, pname)));
                }
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, (string ppath, string pname) item, TrackSkipperContext c, SongJob? song)
            => item.pname.ContainsWithBoundary(title) && item.ppath.ContainsWithBoundary(artist);

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string ppath, string pname) item, TrackSkipperContext c, AlbumJob job)
            => item.ppath.ContainsWithBoundary(album) && item.ppath.ContainsWithBoundary(albumArtist);
    }

    public class NameConditionalSkipper : FileBasedSkipper<(string ppath, string pname, SimpleFile file)>
    {
        private readonly Dictionary<string, List<(string path, (string ppath, string pname, SimpleFile file) item)>> index = new();
        protected override IEnumerable<(string path, (string ppath, string pname, SimpleFile file) item)> Index => index.Values.SelectMany(x => x);
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
            int removeLen = Preprocess(dir, false, false).Length + 1;
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch (Exception ex) { Logger.Trace($"Failed to read tags for '{path}': {ex.Message}"); continue; }

                    string ppath  = Preprocess(path[..path.LastIndexOf('.')], false, false)[removeLen..];
                    string pname  = Path.GetFileName(ppath);
                    var    parent = Utils.NormalizedPath(Path.GetDirectoryName(path));

                    if (!index.TryGetValue(parent, out var value))
                        index[parent] = new() { (path, (ppath, pname, new SimpleFile(musicFile))) };
                    else
                        value.Add((path, (ppath, pname, new SimpleFile(musicFile))));
                }
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, (string ppath, string pname, SimpleFile file) item, TrackSkipperContext context, SongJob song)
            => item.pname.ContainsWithBoundary(title)
            && item.ppath.ContainsWithBoundary(artist)
            && (context.conditions == null || context.conditions.FileSatisfies(item.file, song?.Query));

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string ppath, string pname, SimpleFile file) item, TrackSkipperContext context, AlbumJob job)
        {
            if (!item.ppath.ContainsWithBoundary(album) || !item.ppath.ContainsWithBoundary(albumArtist))
                return false;
            if (context.conditions == null)
                return true;

            var parent = Utils.NormalizedPath(Path.GetDirectoryName(item.file.Path));
            foreach (var x in index[parent])
            {
                if (!context.conditions.FileSatisfies(x.item.file, null))
                    return false;
            }
            return true;
        }
    }

    public class TagSkipper : FileBasedSkipper<(string partist, string ptitle, string palbum, string palbumArtist)>
    {
        readonly string dir;
        readonly List<(string path, (string partist, string ptitle, string palbum, string palbumArtist) item)> index = new();
        protected override IEnumerable<(string path, (string partist, string ptitle, string palbum, string palbumArtist) item)> Index => index;

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
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch (Exception ex) { Logger.Trace($"Failed to read tags for '{path}': {ex.Message}"); continue; }

                    string partist      = Preprocess(musicFile.Tag.JoinedPerformers ?? "",   false, false);
                    string ptitle       = Preprocess(musicFile.Tag.Title ?? "",              false, false);
                    string palbum       = Preprocess(musicFile.Tag.Album ?? "",              false, false);
                    string palbumArtist = Preprocess(musicFile.Tag.JoinedAlbumArtists ?? "", false, false);
                    index.Add((path, (partist, ptitle, palbum, palbumArtist)));
                }
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, (string partist, string ptitle, string palbum, string palbumArtist) item, TrackSkipperContext c, SongJob? song)
            => title == item.ptitle && item.partist.Contains(artist);

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string partist, string ptitle, string palbum, string palbumArtist) item, TrackSkipperContext c, AlbumJob job)
            => album == item.palbum && (albumArtist == null || item.palbumArtist.Contains(albumArtist));
    }

    // TODO: Replace generic tuple soup with readonly record structs.
    public class TagConditionalSkipper : FileBasedSkipper<(string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file)>
    {
        readonly string dir;
        private readonly Dictionary<string, List<(string path, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item)>> index = new();
        protected override IEnumerable<(string path, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item)> Index => index.Values.SelectMany(x => x);

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
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch (Exception ex) { Logger.Trace($"Failed to read tags for '{path}': {ex.Message}"); continue; }

                    string partist      = Preprocess(musicFile.Tag.JoinedPerformers ?? "",   false, false);
                    string ptitle       = Preprocess(musicFile.Tag.Title ?? "",              false, false);
                    string palbum       = Preprocess(musicFile.Tag.Album ?? "",              false, false);
                    string palbumArtist = Preprocess(musicFile.Tag.JoinedAlbumArtists ?? "", false, false);
                    var    parent       = Utils.NormalizedPath(Path.GetDirectoryName(path));

                    if (!index.TryGetValue(parent, out var value))
                        index[parent] = new() { (path, (partist, ptitle, palbum, palbumArtist, new SimpleFile(musicFile))) };
                    else
                        value.Add((path, (partist, ptitle, palbum, palbumArtist, new SimpleFile(musicFile))));
                }
            }
            IndexIsBuilt = true;
        }

        protected override bool FileMatchesSong(string path, string artist, string title, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item, TrackSkipperContext c, SongJob? song)
            => title == item.ptitle && item.partist.Contains(artist);

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item, TrackSkipperContext c, AlbumJob job)
        {
            if (album != item.palbum) return false;
            if (albumArtist != null && !item.palbumArtist.Contains(albumArtist)) return false;
            if (c.conditions == null) return true;

            var parent = Utils.NormalizedPath(Path.GetDirectoryName(item.file.Path));
            foreach (var x in index[parent])
            {
                if (!c.conditions.FileSatisfies(x.item.file, null))
                    return false;
            }
            return true;
        }
    }

    public class IndexSkipper : TrackSkipper
    {
        public IndexSkipper() { IndexIsBuilt = true; }

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor?.PreviousRunResult(song, context.searchSettings);
            if (t == null || (t.State != JobState.Done && t.State != JobState.AlreadyExists))
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
            if (t == null || (t.State != JobState.Done && t.State != JobState.AlreadyExists))
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
            if (t == null || string.IsNullOrEmpty(t.DownloadPath) || !File.Exists(t.DownloadPath))
                return false;

            try
            {
                var musicFile = TagLib.File.Create(t.DownloadPath);
                if (context.conditions.FileSatisfies(musicFile, song.Query, false))
                {
                    foundPath = t.DownloadPath;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Trace($"Failed to read tags for '{t.DownloadPath}': {ex.Message}");
                return false;
            }
        }

        public override bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor?.PreviousRunResult(job);
            if (t == null || string.IsNullOrEmpty(t.DownloadPath) || !Directory.Exists(t.DownloadPath))
                return false;

            var files = Directory.GetFiles(t.DownloadPath, "*", SearchOption.AllDirectories);

            var folderCond = context.folderConditions ?? new FolderConditions();
            if (folderCond.MaxTrackCount != null || folderCond.MinTrackCount != null)
            {
                int count = files.Count(x => Utils.IsMusicFile(x));
                if (folderCond.MaxTrackCount is { } maxTrackCount && count > maxTrackCount) return false;
                if (folderCond.MinTrackCount is { } minTrackCount && count < minTrackCount) return false;
            }

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch (Exception ex) { Logger.Trace($"Failed to read tags for '{path}': {ex.Message}"); return false; }

                    if (!context.conditions.FileSatisfies(musicFile, null))
                        return false;
                }
            }

            foundPath = t.DownloadPath;
            return true;
        }
    }
