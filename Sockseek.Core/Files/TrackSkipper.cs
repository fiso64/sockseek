using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
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
        internal CancellationToken CancellationToken;

        public static TrackSkipperContext From(JobContext ctx, SkipSettings skip, SearchSettings search)
        {
            var cond = CheckedConditions(skip, search);

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

        private static FileConditions? CheckedConditions(SkipSettings skip, SearchSettings search)
            => skip.SkipCheckPrefCond ? search.NecessaryCond.With(search.PreferredCond)
                : skip.SkipCheckCond ? search.NecessaryCond : null;

        internal static bool KeepExistingOutput(SongJob? song, string path)
        {
            if (song?.Config is not { } config || (!config.Skip.SkipCheckCond && !config.Skip.SkipCheckPrefCond)
                || !Utils.IsMusicFile(path))
                return true;
            var conditions = CheckedConditions(config.Skip, config.ResolveSearchSettings())!;
            return ConditionSatisfactionPolicy.LocalAudioFileSatisfies(conditions, path, song.Query);
        }
    }

    public abstract class TrackSkipper
    {
        // Returns true if the given song already exists. foundPath is where it was found.
        public abstract bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath);

        // Returns true if the given album job already exists. foundPath is the directory.
        public virtual bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath, List<string>? playlistTracks = null)
        {
            foundPath = null;
            return false;
        }

        public virtual void BuildIndex() { IndexIsBuilt = true; }
        private volatile bool indexIsBuilt;
        public bool IndexIsBuilt { get => indexIsBuilt; protected set => indexIsBuilt = value; }
        internal virtual Task EnsureIndexAsync(CancellationToken token, Action? starting = null)
        {
            token.ThrowIfCancellationRequested();
            if (!IndexIsBuilt)
            {
                starting?.Invoke();
                BuildIndex();
            }
            return Task.CompletedTask;
        }

        protected static bool AlbumConditionsSatisfy(IEnumerable<SimpleFile> files, TrackSkipperContext context, AlbumJob job)
            => context.conditions == null
                || ConditionSatisfactionPolicy.LocalAlbumSatisfies(files, context.conditions, context.searchSettings, job.Query);
    }

    internal abstract class FileBasedSkipper<T>(string root, bool useConditions, Func<T, string>? albumTag = null) : TrackSkipper where T : struct
    {
        protected string Root => root;
        protected bool UseConditions => useConditions;
        protected readonly record struct Entry(string Path, T Match, bool IsAudio, SimpleFile? Metadata);
        private sealed class FileIndex(Entry[] files, string libraryRoot, Func<T, string>? albumTag)
        {
            public Entry[] Files { get; } = files;
            private readonly Lazy<ILookup<string, int>>? byTag = albumTag == null ? null : new(() =>
                Enumerable.Range(0, files.Length).ToLookup(i => albumTag(files[i].Match)));

            public IEnumerable<Entry> Candidates(string album) =>
                byTag == null ? Files : byTag.Value[album].Select(i => Files[i]);

            public readonly Lazy<ILookup<string, Entry>> ByAlbumDirectory = new(() =>
                files.ToLookup(entry => AlbumDirectory(entry.Path, libraryRoot)));
        }

        private FileIndex index = new([], root, albumTag);

        protected static string AlbumDirectory(string file, string libraryRoot)
        {
            string directory = Path.GetDirectoryName(file)!;
            return SearchResultProjector.IsDiscDirectory(Path.GetFileName(directory))
                && Path.GetRelativePath(libraryRoot, directory) != "."
                ? Path.GetDirectoryName(directory) ?? directory : directory;
        }

        protected abstract string Preprocess(string text, bool isQuery);
        protected abstract Entry? Read(string path);
        protected abstract bool MatchesSong(T item, string artist, string title, string album);
        protected abstract bool MatchesAlbum(T item, string? albumArtist, string album);
        protected virtual bool RequiresAudioForAlbum(TrackSkipperContext context) => true;

        private readonly SemaphoreSlim buildGate = new(1);

        public override void BuildIndex() => BuildIndex(CancellationToken.None);

        internal override Task EnsureIndexAsync(CancellationToken token, Action? starting = null)
        {
            token.ThrowIfCancellationRequested();
            // A mounted filesystem can suspend a synchronous read indefinitely.
            // Its worker owns only the snapshot. The cancelled job can settle immediately.
            return IndexIsBuilt ? Task.CompletedTask : BuildWhenNeededAsync(token, starting).WaitAsync(token);
        }

        private async Task BuildWhenNeededAsync(CancellationToken token, Action? starting)
        {
            await buildGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IndexIsBuilt) return;
                starting?.Invoke();
                await Task.Run(() => BuildIndex(token), token).ConfigureAwait(false);
            }
            finally { buildGate.Release(); }
        }

        internal void BuildIndex(CancellationToken token)
        {
            Entry[] files = Directory.Exists(root) ? ReadFiles(token).ToArray() : [];
            token.ThrowIfCancellationRequested();
            index = new(files, root, albumTag);
            IndexIsBuilt = true;
        }

        private IEnumerable<Entry> ReadFiles(CancellationToken token)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (!FileIndexPathPolicy.IsTransientSockseekPath(path) && Read(path) is { } entry)
                    yield return entry;
            }
        }

        private bool SongMatches(in Entry file, string artist, string title, string album, FileConditions? conditions, SongQuery? query)
            => MatchesSong(file.Match, artist, title, album)
                && (file.Metadata is null || conditions is null
                    || ConditionSatisfactionPolicy.LocalFileSatisfies(conditions, file.Metadata, query));

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            string title = Preprocess(song.Query.Title, true);
            string artist = Preprocess(song.Query.Artist, true);
            string album = Preprocess(song.Query.Album, true);
            var requestedFormats = context.conditions ?? context.searchSettings?.NecessaryCond;
            foreach (var file in index.Files)
            {
                if ((file.IsAudio || requestedFormats is { Formats.Length: > 0 } && requestedFormats.FormatSatisfies(file.Path))
                    && SongMatches(file, artist, title, album, context.conditions, song.Query)
                    && CanRead(file.Path))
                {
                    foundPath = file.Path;
                    return true;
                }
            }
            foundPath = null;
            return false;
        }

        private static bool CanRead(string path)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        public override bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath, List<string>? playlistTracks = null)
        {
            var query = job.Query;
            string album = Preprocess(query.Album, true);
            string artist = Preprocess(query.Artist, true);
            string? albumArtist = query.Artist.Length == 0 ? null : artist;
            string title = query.Album.Length == 0 ? Preprocess(query.SearchHint, true) : "";
            var checkedAlbums = new HashSet<(string Directory, string Album)>();
            var snapshot = index;
            bool requireAudio = RequiresAudioForAlbum(context);
            bool combineDiscs = SearchResultProjector.CanCombineDiscDirectories(query);
            foundPath = null;
            Entry[] selected = [];
            bool bySong = title.Length > 0;
            var candidates = bySong ? snapshot.Files : snapshot.Candidates(album);
            foreach (var file in candidates)
            {
                if (!file.IsAudio && requireAudio) continue;
                bool matches = bySong
                    ? SongMatches(file, artist, title, album, context.conditions, null)
                    : MatchesAlbum(file.Match, albumArtist, album);
                if (!matches) continue;

                string albumDirectory = AlbumDirectory(file.Path, root);
                string directory = combineDiscs ? albumDirectory : Path.GetDirectoryName(file.Path)!;
                // A song hint identifies one track. A compilation can have other performers.
                string selectedAlbum = bySong ? albumTag?.Invoke(file.Match) ?? album : album;
                if (!checkedAlbums.Add((directory, selectedAlbum))) continue;
                // Different editions can have identical tags. Prefer the folder the user named.
                bool exact = album.Length > 0 && Preprocess(Path.GetFileName(directory), false) == album;
                if (!exact && foundPath != null) continue;
                Entry[] members = [];
                if (playlistTracks != null || UseConditions && context.conditions != null
                    || context.folderConditions is { } limits && ConditionSatisfactionPolicy.HasAlbumFolderConditions(limits))
                {
                    members = MatchingMembers(snapshot.ByAlbumDirectory.Value[albumDirectory],
                        directory, combineDiscs, bySong ? null : albumArtist, selectedAlbum);
                    if (!ConditionSatisfactionPolicy.LocalAlbumFolderSatisfies(
                            context.folderConditions, members.Length, members.Select(entry => entry.Path))
                        || UseConditions && !AlbumConditionsSatisfy(members.Select(entry => entry.Metadata!), context, job))
                        continue;
                    if (playlistTracks != null && (members.Length == 0 || members.Any(member => !CanRead(member.Path))))
                        continue;
                }
                foundPath = directory;
                selected = members;
                if (exact || album.Length == 0) break;
            }
            playlistTracks?.AddRange(selected.Select(entry => entry.Path));
            return foundPath != null;
        }

        private Entry[] MatchingMembers(IEnumerable<Entry> entries, string directory, bool combineDiscs,
            string? artist, string album) =>
            entries.Where(entry => entry.IsAudio && (combineDiscs || Path.GetDirectoryName(entry.Path) == directory)
                && MatchesAlbum(entry.Match, artist, album)).ToArray();
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

        protected override bool MatchesSong((string PathPart, string Name) item, string artist, string title, string album)
            => item.Name.ContainsWithBoundary(title) && item.PathPart.ContainsWithBoundary(artist)
                && item.PathPart.ContainsWithBoundary(album);

        protected override bool MatchesAlbum((string PathPart, string Name) item, string? albumArtist, string album)
            => item.PathPart.ContainsWithBoundary(album) && item.PathPart.ContainsWithBoundary(albumArtist);

        protected override bool RequiresAudioForAlbum(TrackSkipperContext context)
            => !UseConditions || context.conditions != null;
    }

    internal readonly record struct TrackTags(string[] Artists, string Title, string Album, string[] AlbumArtists)
    {
        internal static string Normalize(string value) => value.Replace(" ", "").ToLowerInvariant();

        internal static TrackTags From(TagLib.Tag tag) => new(tag.Performers.Select(Normalize).ToArray(),
            Normalize(tag.Title ?? ""), Normalize(tag.Album ?? ""), tag.AlbumArtists.Select(Normalize).ToArray());

        internal bool MatchesAlbum(string? artist, string album)
            => album == Album && (artist == null || AlbumArtists.Contains(artist)
                || AlbumArtists.Length == 0 && Artists.Contains(artist));
    }

    internal sealed class TagSkipper(string root, bool useConditions = false, AlbumQuery? indexedAlbum = null)
        : FileBasedSkipper<TrackTags>(root, useConditions, tags => tags.Album)
    {
        protected override string Preprocess(string text, bool isQuery)
            => TrackTags.Normalize(isQuery ? text.RemoveFt() : text);

        protected override Entry? Read(string path)
        {
            try
            {
                using var file = TagLib.File.Create(path, Utils.AudioMetadataReadStyle);
                var tags = TrackTags.From(file.Tag);
                // A recorded album directory can identify an untagged track, including one in a numbered disc.
                if (tags.Album.Length == 0 && indexedAlbum is { Album.Length: > 0 }
                    && AlbumDirectory(path, Root) == Path.TrimEndingDirectorySeparator(Root)
                    && TrackTags.Normalize(Path.GetFileName(Path.TrimEndingDirectorySeparator(Root))) == Preprocess(indexedAlbum.Album, true))
                {
                    tags = tags with { Album = Preprocess(indexedAlbum.Album, true) };
                    if (tags.AlbumArtists.Length == 0 && tags.Artists.Length == 0 && indexedAlbum.Artist.Length > 0)
                        tags = tags with { AlbumArtists = [Preprocess(indexedAlbum.Artist, true)] };
                }
                return new(path, tags, Utils.IsMusicFile(path), UseConditions ? new SimpleFile(file) : null);
            }
            catch (Exception) { return null; }
        }

        protected override bool MatchesSong(TrackTags item, string artist, string title, string album)
            => title == item.Title && (artist.Length == 0 || item.Artists.Contains(artist))
                && (album.Length == 0 || album == item.Album);

        protected override bool MatchesAlbum(TrackTags item, string? albumArtist, string album)
            => item.MatchesAlbum(albumArtist, album);
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
                || path.EndsWith(OutputStaging.IncompleteExtension, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class IndexSkipper : TrackSkipper
    {
        public IndexSkipper() { IndexIsBuilt = true; }

        internal static string? CompletedPath(Job job, TrackSkipperContext context)
        {
            var entry = job switch
            {
                SongJob song => context.indexEditor?.PreviousRunResult(song, context.searchSettings),
                AlbumJob album => context.indexEditor?.PreviousRunResult(album),
                _ => null,
            };
            return entry?.State is JobStateOld.Done or JobStateOld.AlreadyExists ? entry.DownloadPath : null;
        }

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            var path = CompletedPath(song, context);
            foundPath = null;
            if (path == null || (context.checkFileExists && !File.Exists(path)))
                return false;
            foundPath = path;
            return true;
        }

        public override bool AlbumExists(AlbumJob job, TrackSkipperContext context, out string? foundPath, List<string>? playlistTracks = null)
        {
            var path = CompletedPath(job, context);
            foundPath = null;
            if (path == null || ((CheckAlbumConditions || context.checkFileExists) && !Directory.Exists(path)))
                return false;

            if (CheckAlbumConditions)
            {
                if (context.conditions == null) return false;
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                var paths = files.Where(Utils.IsMusicFile).ToList();
                if (!ConditionSatisfactionPolicy.LocalAlbumFolderSatisfies(context.folderConditions, paths.Count, files))
                    return false;
                var audio = new List<SimpleFile>(paths.Count);
                foreach (var filePath in paths)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var file = TagLib.File.Create(filePath, Utils.AudioMetadataReadStyle);
                        audio.Add(new SimpleFile(file));
                    }
                    catch (Exception) { return false; }
                }
                if (!AlbumConditionsSatisfy(audio, context, job)) return false;
            }
            if (playlistTracks != null)
            {
                // Discover playable members only after deciding to skip the recorded album.
                var library = new TagSkipper(path, indexedAlbum: job.Query);
                library.BuildIndex(context.CancellationToken);
                library.AlbumExists(job, new(), out _, playlistTracks);
            }
            foundPath = path;
            return true;
        }

        protected virtual bool CheckAlbumConditions => false;
    }

    public class IndexConditionalSkipper : IndexSkipper
    {
        protected override bool CheckAlbumConditions => true;

        public override bool SongExists(SongJob song, TrackSkipperContext context, out string? foundPath)
        {
            var path = CompletedPath(song, context);
            foundPath = null;
            if (context.conditions == null || string.IsNullOrEmpty(path))
                return false;

            try
            {
                if (!ConditionSatisfactionPolicy.LocalAudioFileSatisfies(context.conditions, path, song.Query))
                    return false;
                foundPath = path;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

    }
