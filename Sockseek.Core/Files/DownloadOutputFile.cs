using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Services;


public enum IndexEntryKind { Song, Album, SearchResultsAlbum }

// Holds the persisted state of one entry read from a prior-run index file.
public sealed record IndexEntry
{
    public string       DownloadPath  { get; init; } = "";
    public string       Artist        { get; init; } = "";
    public string       Album         { get; init; } = "";
    public string       Title         { get; init; } = "";
    public int          Length        { get; init; } = -1;
    public JobStateOld  State         { get; init; } = JobStateOld.Pending;
    public JobFailureReason FailureReason { get; init; } = JobFailureReason.None;
    // Album owners retain one outcome instead of individual child-track rows.
    public IndexEntryKind Kind { get; init; }
    public bool IsAlbum => Kind is IndexEntryKind.Album or IndexEntryKind.SearchResultsAlbum;

    internal static IndexEntry ForAlbum(AlbumQuery query) => new()
    {
        Artist = query.Artist, Album = query.Album,
        Title = query.Album.Length == 0 ? query.SearchHint : "", Kind = IndexEntryKind.Album,
    };

    public string ToKey()
    {
        string key = $"{Artist.ToLowerInvariant()}\n{Album.ToLowerInvariant()}\n{Title.ToLowerInvariant()}\n{Length}";
        return Kind switch
        {
            IndexEntryKind.Album => key + "\nalbum",
            IndexEntryKind.SearchResultsAlbum => key + "\nsearch-results",
            _ => key,
        };
    }
}

public enum OutputFileKind { Index, Playlist }

// Workflows using the same CSV completion index share its history facts, not
// their job trees or output membership. These are output indexes, not CSV inputs.
// The engine releases the facts after the last workflow retires; the index file
// remains the durable history. Updates neither lose another workflow's entries
// nor reread the index per song.
public sealed class DownloadIndexStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (DownloadOutputFile.IndexData Data, HashSet<Guid> Workflows)> indexes =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>> workflowPaths = [];
    internal int Count { get { lock (gate) return indexes.Count; } }

    public DownloadOutputFile Open(string path, JobList owner, Guid workflowId, bool readOnly)
    {
        path = Utils.GetFullPath(path);
        DownloadOutputFile.IndexData data;
        lock (gate)
        {
            if (!indexes.TryGetValue(path, out var entry))
                indexes.Add(path, entry = (new(), []));
            data = entry.Data;
            entry.Workflows.Add(workflowId);
            if (!workflowPaths.TryGetValue(workflowId, out var paths))
                workflowPaths.Add(workflowId, paths = new(indexes.Comparer));
            paths.Add(path);
        }
        // The index has its own lock. Loading it must not hold up unrelated paths.
        return new DownloadOutputFile(path, owner, OutputFileKind.Index, loadPreviousResults: true, readOnly, data);
    }

    public void RetireWorkflow(Guid workflowId)
    {
        lock (gate)
        {
            if (!workflowPaths.Remove(workflowId, out var paths)) return;
            foreach (string path in paths)
            {
                var entry = indexes[path];
                entry.Workflows.Remove(workflowId);
                if (entry.Workflows.Count == 0) indexes.Remove(path);
            }
        }
    }

    internal void Clear()
    {
        lock (gate) { indexes.Clear(); workflowPaths.Clear(); }
    }
}

/// <summary>
/// One workflow output: incremental logical facts and ordered membership, with
/// publication scheduled by the engine's file-I/O owner. Observing a track never
/// scans its siblings. Only explicit structural changes reconcile a subtree.
/// </summary>
public sealed class DownloadOutputFile
{
    internal Guid OperationId { get; } = Guid.NewGuid();
    private const int LegacyIndexNotFoundFailureReasonValue = 3;

    public string Path { get; }
    public OutputFileKind Kind { get; }
    public bool IsWritable { get; }
    private readonly string parent;
    private List<string> lastPlaylistAdditions = [];
    private readonly IndexData indexData;
    private Dictionary<string, IndexValue> previousRunData => indexData.Entries;
    // Duration-tolerant lookup only visits variants of the exact text identity,
    // never every retained song for each group completion/skip decision.
    private Dictionary<SongQuery, List<IndexValue>>? songVariants
    {
        get => indexData.Variants;
        set => indexData.Variants = value;
    }
    private readonly List<JobList> owners = [];
    private readonly List<OutputNode> roots = [];
    private readonly Dictionary<Guid, List<OutputNode>> nodes = [];
    private Lock writeGate => indexData.WriteGate;
    private long revision { get => indexData.Revision; set => indexData.Revision = value; }
    private long writtenRevision { get => indexData.WrittenRevision; set => indexData.WrittenRevision = value; }

    internal sealed class IndexData
    {
        internal readonly Lock Gate = new();
        internal readonly Lock WriteGate = new();
        internal Dictionary<string, IndexValue> Entries = new();
        internal HashSet<IndexValue> Changed = [];
        internal readonly Dictionary<string, IndexValue> SongsByPath = new(LocalPathComparer);
        internal readonly Dictionary<string, IndexEntry> Withdrawn = [];
        internal byte[]? DiskHash;
        internal Dictionary<SongQuery, List<IndexValue>>? Variants;
        internal long Revision;
        internal long WrittenRevision;
        internal bool Loaded;
    }

    internal sealed class IndexValue(IndexEntry entry, string key)
    {
        public readonly string Key = key;
        public IndexEntry Entry = entry;
        public string? Encoded;
        public bool FromHistory;
        private SongQuery? query;
        public SongQuery Query => query ??= new() { Artist = Entry.Artist, Album = Entry.Album,
            Title = Entry.Title, Length = Entry.Length };
    }

    // Runtime membership owns positions; queued snapshots contain only text.
    private sealed class OutputNode(Job job, OutputNode? parent)
    {
        public readonly Guid Id = job.Id;
        public readonly JobSnapshotKind Kind = job.Kind;
        public readonly OutputNode? Parent = parent;
        public readonly List<OutputNode> Children = [];
        public string? PlaylistLine;
        public IReadOnlyList<string>? ExistingAlbumTracks;
        public bool Successful = job.IsSuccessfulTerminal;
        public int SuccessfulChildren;
        public IndexEntry? Candidate;
        public SearchSettings? Search = job.Config?.Search ?? parent?.Search;
    }

    private static readonly StringComparer LocalPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private Lock locker => indexData.Gate;
    private readonly Dictionary<Guid, string?> jobDownloadPaths = new();

    public void NotifyJobDownloadPath(Guid jobId, string? path)
    {
        lock (locker) jobDownloadPaths[jobId] = path;
    }

    public DownloadOutputFile(string path, JobList queue, OutputFileKind kind, bool loadPreviousResults, bool readOnly = false)
        : this(path, queue, kind, loadPreviousResults, readOnly, new IndexData()) { }

    internal DownloadOutputFile(string path, JobList queue, OutputFileKind kind, bool loadPreviousResults, bool readOnly, IndexData data)
    {
        indexData = data;
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = Utils.GetFullPath(path);
        Kind = kind;
        IsWritable = !readOnly;
        parent = System.IO.Path.GetDirectoryName(Path)!;
        lock (locker)
            if (kind == OutputFileKind.Index && loadPreviousResults && !indexData.Loaded)
            {
                var (hash, entries) = ReadIndexChanges();
                if (entries != null) ReplaceIndex(entries);
                indexData.DiskHash = hash;
                indexData.Loaded = true;
            }
        if (IsWritable) AttachOwner(queue);
        if (kind == OutputFileKind.Playlist) revision++;
    }

    private (byte[]? Hash, Dictionary<string, IndexValue>? Entries) ReadIndexChanges()
    {
        if (Directory.Exists(Path)) return (null, null); // The writer will report the invalid destination.
        FileStream file;
        try { file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, indexData.DiskHash == null ? null : []);
        }
        using (file)
        {
            byte[] hash = SHA256.HashData(file);
            if (hash.AsSpan().SequenceEqual(indexData.DiskHash)) return (hash, null);
            file.Position = 0;
            using var input = new StreamReader(file, Encoding.UTF8);
            return (hash, ParseIndex(input));
        }
    }

    private Dictionary<string, IndexValue> ParseIndex(StreamReader input)
    {
        var entries = new Dictionary<string, IndexValue>();
        string? header = input.ReadLine();
        if (header is null) return entries;
        if (header.StartsWith("#Sockseek:", StringComparison.Ordinal))
        {
            // Normalize the old inline index once at the file boundary. Runtime
            // entries and publication always use the current CSV representation.
            string inline = header["#Sockseek:".Length..];
            var normalized = new StringBuilder(inline.Length);
            bool quoted = false;
            for (int i = 0; i < inline.Length; i++)
            {
                char c = inline[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < inline.Length && inline[i + 1] == '"')
                    {
                        normalized.Append("\"\""); i++; continue;
                    }
                    quoted = !quoted;
                }
                normalized.Append(c == ';' && !quoted ? '\n' : c);
            }
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(normalized.ToString()), writable: false);
            using var migrated = new StreamReader(bytes, Encoding.UTF8);
            ReadIndex(migrated, entries);
        }
        else ReadIndex(input, entries);
        return entries;
    }

    private void ReadIndex(StreamReader input, Dictionary<string, IndexValue> entries)
    {
        var parser = new SmallestCSV.SmallestCSVParser(input);
        while (parser.ReadNextRow() is { } fields)
        {
            if (fields.Count == 0 || fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Count < 8) throw new InvalidDataException("The download index contains an incomplete row.");
            string downloadPath = fields[0];
            if (downloadPath.StartsWith("./", StringComparison.Ordinal)) downloadPath = System.IO.Path.Join(parent, downloadPath[2..]);
            var entry = new IndexEntry { DownloadPath = downloadPath, Artist = fields[1], Album = fields[2],
                Title = fields[3], Length = int.Parse(fields[4]), Kind = fields[5] switch
                    {
                        "2" => IndexEntryKind.SearchResultsAlbum,
                        "1" => IndexEntryKind.Album,
                        _ => fields[3].Length == 0 ? IndexEntryKind.Album : IndexEntryKind.Song,
                    }, State = (JobStateOld)int.Parse(fields[6]),
                FailureReason = ReadFailureReason(fields[7]) };
            MergeIndexEntry(entries, entry);
        }
    }

    private static void MergeIndexEntry(Dictionary<string, IndexValue> entries, IndexEntry entry)
    {
        string key = entry.ToKey();
        if (entries.TryGetValue(key, out var value))
        {
            value.Entry = entry;
            value.Encoded = null;
        }
        else entries[key] = new IndexValue(entry, key);
    }

    private static string? SongPath(IndexEntry entry)
        => !entry.IsAlbum && entry.State is JobStateOld.Done or JobStateOld.AlreadyExists
            && entry.DownloadPath.Length != 0 ? Utils.NormalizedPath(entry.DownloadPath) : null;

    private void ReplaceIndex(Dictionary<string, IndexValue> entries, bool publishing = false)
    {
        // A legacy index or independent writers may claim the same recording
        // path for different songs. The order of CSV rows cannot identify its contents.
        indexData.Entries = entries;
        songVariants = null;
        var paths = indexData.SongsByPath;
        paths.Clear();
        HashSet<string>? conflicts = null;
        foreach (var value in entries.Values)
            if (SongPath(value.Entry) is { } path && !paths.TryAdd(path, value))
                (conflicts ??= new(LocalPathComparer)).Add(path);
        if (conflicts == null) return;
        foreach (var (key, value) in entries.ToArray())
            if (SongPath(value.Entry) is { } path && conflicts.Contains(path))
            {
                if (publishing) indexData.Withdrawn[key] = value.Entry;
                entries.Remove(key);
                paths.Remove(path);
            }
    }

    /// <summary>Explicit whole-scope refresh for structural reconciliation or standalone callers.</summary>
    public void Refresh()
    {
        Reconcile();
        PublishPending();
    }

    internal void Reconcile()
    {
        if (!IsWritable) return;
        lock (locker)
            for (int i = 0; i < owners.Count; i++) RefreshNode(roots[i], owners[i]);
    }

    internal void AttachOwner(JobList owner, bool synthetic = false)
    {
        if (!IsWritable) return;
        lock (locker)
        {
            if (nodes.ContainsKey(owner.Id)) return;
            // Extraction may have attached this owner beneath an existing
            // source since preparation began. Discover that membership before
            // treating it as another root (and duplicating playlist entries).
            for (int i = 0; i < owners.Count; i++) RefreshNode(roots[i], owners[i], capture: false);
            if (nodes.ContainsKey(owner.Id)) return;
            // A synthetic resume wrapper does not add another playlist copy.
            if (synthetic && owner.Count > 0 && owner.Jobs.All(job => nodes.ContainsKey(job.Id))) return;
            owners.Add(owner);
            roots.Add(RegisterNode(owner, null));
        }
    }

    internal void Observe(Job changed)
    {
        if (!IsWritable) return;
        lock (locker)
        {
            if (!nodes.TryGetValue(changed.Id, out var matches))
            {
                // New extractor/group membership is structural work, not a
                // reason to rescan all siblings on ordinary track transitions.
                for (int i = 0; i < owners.Count; i++) RefreshNode(roots[i], owners[i]);
                if (!nodes.TryGetValue(changed.Id, out matches)) return;
            }
            foreach (var node in matches.ToArray())
            {
                if (changed is FileDownloadJob) Capture(node, changed);
                else RefreshNode(node, changed);
            }
        }
    }

    internal void RegisterChildren(Job owner)
    {
        if (!IsWritable) return;
        lock (locker)
            if (nodes.TryGetValue(owner.Id, out var matches))
                foreach (var node in matches.ToArray()) RefreshNode(node, owner);
    }

    private IReadOnlyList<Job> OutputChildren(Job job)
        => Kind == OutputFileKind.Index && job is RemoteDirectoryJob ? [] : job.Children;

    private OutputNode RegisterNode(Job job, OutputNode? parent)
    {
        var node = new OutputNode(job, parent);
        if (!nodes.TryGetValue(job.Id, out var memberships)) nodes.Add(job.Id, memberships = []);
        memberships.Add(node);
        foreach (var child in OutputChildren(job)) node.Children.Add(RegisterNode(child, node));
        node.SuccessfulChildren = node.Children.Count(child => child.Successful);
        return node;
    }

    private void UnregisterNode(OutputNode node)
    {
        foreach (var child in node.Children) UnregisterNode(child);
        var memberships = nodes[node.Id];
        memberships.Remove(node);
        if (memberships.Count == 0) nodes.Remove(node.Id);
    }

    private void RebuildChildren(OutputNode node, IReadOnlyList<Job> children)
    {
        // Keep cached facts/positions for unchanged identities, including
        // duplicate source memberships. Appending an extraction must not erase
        // previously completed siblings while only membership is reconciled.
        var retained = node.Children.GroupBy(child => child.Id)
            .ToDictionary(group => group.Key, group => new Queue<OutputNode>(group));
        node.Children.Clear();
        foreach (var child in children)
            node.Children.Add(retained.TryGetValue(child.Id, out var matches) && matches.Count > 0
                ? matches.Dequeue() : RegisterNode(child, node));
        foreach (var remaining in retained.Values)
            foreach (var child in remaining) UnregisterNode(child);
        node.SuccessfulChildren = node.Children.Count(child => child.Successful);
        if (Kind != OutputFileKind.Index) revision++;
    }

    private void RefreshNode(OutputNode node, Job job, bool capture = true)
    {
        // Containers reconcile explicit membership. A leaf update never reaches
        // this traversal; captured lines and index records survive between it.
        var children = OutputChildren(job).ToArray();
        if (!node.Children.Select(child => child.Id).SequenceEqual(children.Select(child => child.Id)))
            RebuildChildren(node, children);
        node.Search = job.Config?.Search ?? node.Parent?.Search;
        for (int i = 0; i < children.Length; i++) RefreshNode(node.Children[i], children[i], capture);
        if (capture) Capture(node, job);
    }

    private void Capture(OutputNode node, Job job)
    {
        bool successful = job.IsSuccessfulTerminal;
        if (successful != node.Successful)
        {
            if (node.Parent is { } parent) parent.SuccessfulChildren += successful ? 1 : -1;
            node.Successful = successful;
        }
        if (Kind == OutputFileKind.Index && job is AlbumJob album && ShouldIndexAlbum(album))
        {
            jobDownloadPaths.TryGetValue(album.Id, out string? pathOverride);
            var (state, reason) = JobToIndexState(album);
            PutIndex(IndexEntry.ForAlbum(album.Query) with {
                DownloadPath = pathOverride ?? album.DownloadPath ?? "", Length = -1,
                State = state, FailureReason = reason, Kind = album.Config?.Search.NoBrowseFolder == true && album.ResolvedTarget?.IsFullyRetrieved != true
                    ? IndexEntryKind.SearchResultsAlbum : IndexEntryKind.Album });
        }
        if (Kind == OutputFileKind.Playlist)
        {
            if (job is AlbumJob albumJob)
            {
                var tracks = albumJob.SkipReason == JobSkipReason.AlreadyExists ? albumJob.ExistingPlaylistTracks : null;
                if (node.ExistingAlbumTracks != tracks) { node.ExistingAlbumTracks = tracks; revision++; }
                return;
            }
            bool audio = job switch
            {
                SongJob track => !track.IsNotAudio,
                RemoteFileJob remote => Utils.IsMusicFile(remote.Target.Filename),
                _ => false,
            };
            string? line = audio && job is FileDownloadJob file && file.IsTerminal ? FileToLine(file) : null;
            if (node.PlaylistLine != line) { node.PlaylistLine = line; revision++; }
            return;
        }
        if (job is not SongJob song) return;
        if (!song.IsTerminal || !ShouldWriteIndexEntry(song)) return;
        // A filename by itself cannot establish which recording occupies it.
        if (song.ExistingFileSource == ExistingFileSource.Destination) return;
        if (song.ExistingFileSource == ExistingFileSource.CompletionHistory
            && PreviousRunResult(song, song.Config?.Search)?.DownloadPath != song.DownloadPath) return;
        var candidate = new IndexEntry { Artist = song.Query.Artist, Album = song.Query.Album,
            Title = song.Query.Title, Length = song.Query.Length, DownloadPath = song.DownloadPath ?? "",
            State = JobToIndexState(song).state, FailureReason = song.FailureReason };
        if (candidate == node.Candidate && node.Parent?.Kind != JobSnapshotKind.SongGroups) return;
        node.Candidate = candidate;
        node.Search = song.Config?.Search ?? node.Parent?.Search;
        bool albumChild = node.Parent?.Kind is JobSnapshotKind.Album;
        if (albumChild) return;
        if (node.Parent is { Kind: JobSnapshotKind.SongGroups } group)
        {
            if (successful || group.SuccessfulChildren == 0 && ReferenceEquals(group.Children.FirstOrDefault(), node))
                PutIndex(node.Candidate, node.Search, song.ExistingFileSource == ExistingFileSource.CompletionHistory);
            if (group.SuccessfulChildren == 0 && group.Children.FirstOrDefault()?.Candidate is { } fallback)
                PutIndex(fallback, group.Children[0].Search);
        }
        else PutIndex(node.Candidate, node.Search, song.ExistingFileSource == ExistingFileSource.CompletionHistory);
    }

    private void PutIndex(IndexEntry candidate, SearchSettings? search = null, bool fromHistory = false)
    {
        if (Kind == OutputFileKind.Playlist) return;
        string key = candidate.ToKey();
        if (!previousRunData.TryGetValue(key, out var value) && search?.GroupResults == true)
            value = FindSong(candidate, search);
        string? path = SongPath(candidate);
        var displaced = path == null ? null : indexData.SongsByPath.GetValueOrDefault(path);
        if (displaced == value) displaced = null;
        // Finding a recording through library matching does not replace the file.
        if (displaced != null && candidate.State == JobStateOld.AlreadyExists) return;
        if (value is not null)
        {
            var previous = value.Entry;
            candidate = previous with { State = candidate.State, FailureReason = candidate.FailureReason,
                DownloadPath = candidate.DownloadPath };
            if (candidate == previous) return;
            if (SongPath(previous) is { } oldPath) indexData.SongsByPath.Remove(oldPath);
        }
        if (displaced != null)
        {
            // A successful placement gives us the new owner. Withdraw the old
            // claim, including from an index another process published meanwhile.
            previousRunData.Remove(displaced.Key);
            indexData.Changed.Remove(displaced);
            indexData.Withdrawn[displaced.Key] = displaced.Entry;
            songVariants = null;
        }
        if (value is null)
        {
            value = new IndexValue(candidate, key);
            previousRunData.Add(key, value);
            RegisterVariant(value);
        }
        else
        {
            value.Entry = candidate;
            value.Encoded = null;
        }
        value.FromHistory = fromHistory;
        if (path != null) indexData.SongsByPath[path] = value;
        indexData.Changed.Add(value);
        revision++;
    }

    private static readonly StringComparer AlbumPlaylistOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);

    private void AddPlaylistLines(OutputNode node, List<string> result)
    {
        if (node.Kind is JobSnapshotKind.Song or JobSnapshotKind.RemoteFile)
        {
            if (node.PlaylistLine is { } line) result.Add(line);
            return;
        }
        if (node.Kind == JobSnapshotKind.SongGroups)
        {
            if (node.SuccessfulChildren > 0)
                foreach (var child in node.Children) { if (child.Successful) AddPlaylistLines(child, result); }
            else if (node.Children.FirstOrDefault() is { } first) AddPlaylistLines(first, result);
            return;
        }
        int albumStart = result.Count;
        if (node.ExistingAlbumTracks is { } tracks)
            result.AddRange(tracks.Select(PlaylistPath));
        else
            foreach (var child in node.Children) AddPlaylistLines(child, result);
        // A folder has filenames for ordering. Lists and imports have an
        // explicit sequence supplied by the user.
        if (node.Kind is JobSnapshotKind.Album or JobSnapshotKind.RemoteDirectory)
            result.Sort(albumStart, result.Count - albumStart, AlbumPlaylistOrder);
    }

    internal bool PublishPending()
    {
        if (!IsWritable) return false;
        lock (writeGate)
        {
            long capturedRevision;
            lock (locker)
            {
                if (writtenRevision == revision) return false;
                capturedRevision = revision;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            Utils.UpdateFile(Path, () =>
            {
                if (Kind == OutputFileKind.Index) capturedRevision = PublishIndex();
                else
                {
                    var lines = new List<string>();
                    lock (locker)
                    {
                        capturedRevision = revision;
                        foreach (var root in roots) AddPlaylistLines(root, lines);
                    }
                    PublishPlaylist(lines);
                }
            });
            lock (locker) writtenRevision = capturedRevision;
            return true;
        }
    }

    private long PublishIndex()
    {
        // An independently running CLI or daemon may have published since we
        // loaded the index. Merge only our changed rows into that generation.
        var latest = indexData.Loaded ? ReadIndexChanges().Entries : null;
        KeyValuePair<IndexValue, IndexEntry>[] pending;
        KeyValuePair<string, IndexEntry>[] withdrawals;
        long captured;
        string[] rows;
        lock (locker)
        {
            if (latest != null)
            {
                foreach (var (key, old) in indexData.Withdrawn)
                    if (latest.TryGetValue(key, out var current) && current.Entry == old)
                        latest.Remove(key);
                var merged = new HashSet<IndexValue>();
                foreach (var value in indexData.Changed)
                {
                    if (value.FromHistory && latest.GetValueOrDefault(value.Key)?.Entry.DownloadPath != value.Entry.DownloadPath)
                        continue;
                    MergeIndexEntry(latest, value.Entry);
                    latest[value.Key].FromHistory = value.FromHistory;
                    merged.Add(latest[value.Key]);
                }
                ReplaceIndex(latest, publishing: true);
                merged.RemoveWhere(value => !latest.ContainsKey(value.Key));
                indexData.Changed = merged;
            }
            withdrawals = indexData.Withdrawn.ToArray();
            captured = revision;
            pending = indexData.Changed.Select(value => KeyValuePair.Create(value, value.Entry)).ToArray();
            rows = previousRunData.Values.Select(value => value.Encoded ??= EncodeIndexEntry(value.Entry)).ToArray();
        }
        using var hash = SHA256.Create();
        Utils.WriteAtomically(Path, stream =>
        {
            using var hashing = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true);
            using var writer = new StreamWriter(hashing, new UTF8Encoding(false), 8192);
            writer.Write("filepath,artist,album,title,length,tracktype,state,failurereason\n");
            foreach (string row in rows) writer.Write(row);
            writer.Write('\n');
        });
        lock (locker)
        {
            indexData.Loaded = true;
            indexData.DiskHash = hash.Hash!;
            foreach (var (key, old) in withdrawals)
                if (indexData.Withdrawn.TryGetValue(key, out var current) && ReferenceEquals(current, old))
                    indexData.Withdrawn.Remove(key);
            foreach (var (value, entry) in pending)
                if (ReferenceEquals(value.Entry, entry)) indexData.Changed.Remove(value);
        }
        return captured;
    }

    private void PublishContents(string contents, Encoding encoding)
        => Utils.WriteAtomically(Path, output =>
        {
            using var writer = new StreamWriter(output, encoding, leaveOpen: true);
            writer.Write(contents);
        });

    private void PublishPlaylist(List<string> generated)
    {
        var (current, encoding) = ReadPlaylist();
        string newline = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var retained = new List<string>();
        using var reader = new StringReader(current);
        while (reader.ReadLine() is { } line) retained.Add(line);

        // Withdraw only occurrences added by this writer's preceding snapshot.
        // A second import may have appended tracks while this one was running.
        var previous = lastPlaylistAdditions.CountBy(line => line).ToDictionary();
        if (previous.Count > 0)
        {
            retained.Reverse();
            retained.RemoveAll(line => TakeOccurrence(previous, line));
            retained.Reverse();
        }

        var available = retained.CountBy(line => line).ToDictionary();
        var added = new List<string>();
        foreach (string line in generated)
            if (!TakeOccurrence(available, line)) added.Add(line);
        retained.AddRange(added);
        string contents = retained.Count == 0 ? "" : string.Join(newline, retained) + newline;
        if (current != contents || !File.Exists(Path)) PublishContents(contents, encoding);
        lastPlaylistAdditions = added;
    }

    private static bool TakeOccurrence(Dictionary<string, int> counts, string line)
    {
        if (!counts.TryGetValue(line, out int count)) return false;
        if (count == 1) counts.Remove(line);
        else counts[line] = count - 1;
        return true;
    }

    private (string Text, Encoding Encoding) ReadPlaylist()
    {
        var utf8 = new UTF8Encoding(false, true);
        if (!File.Exists(Path)) return ("", utf8);
        byte[] bytes = File.ReadAllBytes(Path);
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), utf8);
        try { return (reader.ReadToEnd(), reader.CurrentEncoding); }
        catch (DecoderFallbackException) when (System.IO.Path.GetExtension(Path).Equals(".m3u", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = CodePagesEncodingProvider.Instance.GetEncoding(1252,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;
            return (legacy.GetString(bytes), legacy);
        }
    }

    private string EncodeIndexEntry(IndexEntry entry)
    {
        string localPath = RelativeOutputPath(entry.DownloadPath) is { } relative ? "./" + relative : entry.DownloadPath;
        var encoded = new StringBuilder();
        string[] fields = [localPath.Replace('\\', '/'), entry.Artist, entry.Album, entry.Title,
            entry.Length.ToString(), ((int)entry.Kind).ToString(),
            ((int)entry.State).ToString(), ((int)entry.FailureReason).ToString()];
        bool first = true;
        foreach (string field in fields)
        {
            if (!first) encoded.Append(',');
            first = false;
            if (field.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                encoded.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            else encoded.Append(field);
        }
        return encoded.Append('\n').ToString();
    }

    private string FileToLine(FileDownloadJob file)
    {
        string? failureReason = file.FailureReason != JobFailureReason.None ? file.FailureReason.ToString() : null;
        if (failureReason == null && file.TerminalOutcome == JobTerminalOutcome.Skipped && file.SkipReason == JobSkipReason.NotFoundLastTime)
            failureReason = nameof(JobFailureReason.NoMatchingResults);

        if (failureReason != null)
            return $"#FAIL: {file} [{failureReason}]";

        return !string.IsNullOrEmpty(file.DownloadPath) ? PlaylistPath(file.DownloadPath) : $"# {file}";
    }

    private string PlaylistPath(string localPath)
    {
        string path = RelativeOutputPath(localPath) ?? localPath;
        return path.StartsWith('#') ? "./" + path : path;
    }

    private string? RelativeOutputPath(string localPath)
    {
        if (!System.IO.Path.IsPathFullyQualified(localPath)) return null;
        string relative = System.IO.Path.GetRelativePath(parent, localPath);
        return relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || System.IO.Path.IsPathRooted(relative) ? null : relative;
    }

    // Looks up the persisted state for a SongJob from a prior run.
    public IndexEntry? PreviousRunResult(SongJob song, SearchSettings? search = null)
    {
        lock (locker)
        {
            if (previousRunData.TryGetValue(MakeSongKey(song), out var t)) return t.Entry;
            return search?.GroupResults == true ? FindSong(new IndexEntry { Artist = song.Query.Artist,
                Album = song.Query.Album, Title = song.Query.Title, Length = song.Query.Length }, search)?.Entry : null;
        }
    }

    private IndexValue? FindSong(IndexEntry song, SearchSettings search)
    {
        if (!search.GroupResults) return null;
        if (songVariants is null)
        {
            songVariants = new(new SongQueryComparer(ignoreCase: true));
            foreach (var value in previousRunData.Values) RegisterVariant(value);
        }
        var query = new SongQuery { Artist = song.Artist, Album = song.Album, Title = song.Title, Length = song.Length };
        if (!songVariants.TryGetValue(query, out var variants)) return null;
        var comparer = new SongQueryComparer(ignoreCase: true, search.GroupLengthTol);
        return variants.FirstOrDefault(value => comparer.Equals(query, value.Query));
    }

    private void RegisterVariant(IndexValue value)
    {
        if (songVariants is null || value.Entry.IsAlbum || value.Entry.Title.Length == 0) return;
        if (!songVariants.TryGetValue(value.Query, out var variants)) songVariants.Add(value.Query, variants = []);
        variants.Add(value);
    }

    // Looks up the persisted state for an AlbumJob from a prior run.
    public IndexEntry? PreviousRunResult(AlbumJob job)
    {
        lock (locker)
        {
            var album = IndexEntry.ForAlbum(job.Query);
            var whole = previousRunData.GetValueOrDefault(album.ToKey())?.Entry;
            if (job.Config?.Search.NoBrowseFolder != true || whole?.State is JobStateOld.Done or JobStateOld.AlreadyExists)
                return whole;
            var searchOnly = album with { Kind = IndexEntryKind.SearchResultsAlbum };
            return previousRunData.GetValueOrDefault(searchOnly.ToKey())?.Entry ?? whole;
        }
    }

    public bool TryGetPreviousRunResult(SongJob song, out IndexEntry? result)
    {
        result = PreviousRunResult(song);
        return result != null;
    }

    public bool TryGetFailureReason(SongJob song, out JobFailureReason reason)
    {
        reason = JobFailureReason.None;
        var t = PreviousRunResult(song);
        if (t != null && t.State == JobStateOld.Failed)
        {
            reason = t.FailureReason;
            return true;
        }
        return false;
    }

    public IReadOnlyCollection<IndexEntry> GetPreviousRunData()
    {
        lock (locker) return previousRunData.Values.Select(value => value.Entry).ToArray();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string MakeSongKey(SongJob song)
    {
        return new IndexEntry
        {
            Artist = song.Query.Artist,
            Album  = song.Query.Album,
            Title  = song.Query.Title,
            Length = song.Query.Length,
        }.ToKey();
    }

    private static bool ShouldIndexAlbum(AlbumJob album)
    {
        if (!album.IsTerminal || album.Config?.Output.AlbumArtOnly == true) return false;
        // A successful transfer of a partial listing is useful, but cannot satisfy a later whole-album request.
        return ShouldWriteIndexEntry(album)
            && (!album.IsSuccessfulTerminal || album.ResolvedTarget?.IsFullyRetrieved != false
                || album.Config?.Search.NoBrowseFolder == true
                    && album.DirectoryResolutionPolicy != AlbumDirectoryResolutionPolicy.UseSelectedSnapshot
                    && album.ExcludedFilenames is not { Count: > 0 });
    }

    private static bool ShouldWriteIndexEntry(Job job)
        => job.TerminalOutcome != JobTerminalOutcome.Skipped
            || job.SkipReason is JobSkipReason.AlreadyExists or JobSkipReason.NotFoundLastTime;

    // Maps split runtime state to the old index-file state + failure reason.
    private static (JobStateOld state, JobFailureReason reason) JobToIndexState(Job job)
    {
        return job.TerminalOutcome switch
        {
            JobTerminalOutcome.Succeeded => (JobStateOld.Done, job.FailureReason),
            JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled or JobTerminalOutcome.PartialSuccess
                => (JobStateOld.Failed, job.FailureReason),
            JobTerminalOutcome.Skipped when job.SkipReason == JobSkipReason.AlreadyExists
                => (JobStateOld.AlreadyExists, JobFailureReason.None),
            JobTerminalOutcome.Skipped when job.SkipReason == JobSkipReason.NotFoundLastTime
                => (JobStateOld.NotFoundLastTime, job.FailureReason == JobFailureReason.None ? JobFailureReason.NoMatchingResults : job.FailureReason),
            _ => (JobStateOld.Pending, JobFailureReason.None),
        };
    }

    private static JobFailureReason ReadFailureReason(string value)
    {
        var parsed = int.Parse(value);
        return parsed == LegacyIndexNotFoundFailureReasonValue
            ? JobFailureReason.NoMatchingResults
            : (JobFailureReason)parsed;
    }

}
