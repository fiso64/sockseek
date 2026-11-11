using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;
using System.Text;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Services;


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
    public bool         IsAlbum       { get; init; }

    public string ToKey() =>
        $"{Artist.ToLower()}\n{Album.ToLower()}\n{Title.ToLower()}\n{Length}";
}

public enum OutputFileKind { Index, Playlist }

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
    private readonly List<string> originalLines;
    readonly Dictionary<string, IndexValue> previousRunData = new();
    // Duration-tolerant lookup only visits variants of the exact text identity,
    // never every retained song for each group completion/skip decision.
    private Dictionary<SongQuery, List<IndexValue>>? songVariants;
    private readonly List<JobList> owners = [];
    private readonly List<OutputNode> roots = [];
    private readonly Dictionary<Guid, List<OutputNode>> nodes = [];
    private readonly Lock writeGate = new();
    private long revision;
    private long writtenRevision;

    private sealed class IndexValue(IndexEntry entry, string key)
    {
        public readonly string Key = key;
        public IndexEntry Entry = entry;
        public string? Encoded;
        private SongQuery? query;
        public SongQuery Query => query ??= new() { Artist = Entry.Artist, Album = Entry.Album,
            Title = Entry.Title, Length = Entry.Length };
    }

    // Runtime membership owns positions; queued snapshots contain only text.
    private sealed class OutputNode(Job job, OutputNode? parent)
    {
        public readonly Guid Id = job.Id;
        public readonly JobSnapshotKind Kind = CoreSnapshotFactory.GetJobKind(job);
        public readonly OutputNode? Parent = parent;
        public readonly List<OutputNode> Children = [];
        public string? PlaylistLine;
        public bool Successful = job.IsSuccessfulTerminal;
        public int SuccessfulChildren;
        public IndexEntry? Candidate;
        public SearchSettings? Search = job.Config?.Search ?? parent?.Search;
    }

    private readonly Lock locker = new();
    private readonly Dictionary<Guid, string?> jobDownloadPaths = new();

    public void NotifyJobDownloadPath(Guid jobId, string? path)
    {
        lock (locker) jobDownloadPaths[jobId] = path;
    }

    public DownloadOutputFile(string path, JobList queue, OutputFileKind kind, bool loadPreviousResults, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = Utils.GetFullPath(path);
        Kind = kind;
        IsWritable = !readOnly;
        parent = Utils.NormalizedPath(System.IO.Path.GetDirectoryName(Path) ?? "");
        originalLines = kind == OutputFileKind.Playlist ? ReadAllLines().ToList() : [];
        if (kind == OutputFileKind.Index && loadPreviousResults)
            LoadPreviousResults();
        if (IsWritable) AttachOwner(queue);
        if (kind == OutputFileKind.Playlist) revision++;
    }

    private void LoadPreviousResults()
    {
        if (!File.Exists(Path)) return;
        using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var input = new StreamReader(file, Encoding.UTF8);
        string? header = input.ReadLine();
        if (header is null) return;
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
            ReadIndex(migrated);
        }
        else ReadIndex(input);
    }

    private void ReadIndex(StreamReader input)
    {
        var parser = new SmallestCSV.SmallestCSVParser(input);
        while (parser.ReadNextRow() is { } fields)
        {
            if (fields.Count == 0 || fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Count < 8) throw new InvalidDataException("The download index contains an incomplete row.");
            string downloadPath = fields[0];
            if (downloadPath.StartsWith("./", StringComparison.Ordinal)) downloadPath = System.IO.Path.Join(parent, downloadPath[2..]);
            var entry = new IndexEntry { DownloadPath = downloadPath, Artist = fields[1], Album = fields[2],
                Title = fields[3], Length = int.Parse(fields[4]), State = (JobStateOld)int.Parse(fields[6]),
                FailureReason = ReadFailureReason(fields[7]) };
            string key = entry.ToKey();
            if (previousRunData.TryGetValue(key, out var value) && value.Key == key)
                value.Entry = entry;
            else
                previousRunData[key] = value = new IndexValue(entry, key);
            if (entry.Title.Length > 0 && entry.Album.Length > 0)
                previousRunData.TryAdd(MakeAlbumKey(entry.Artist, entry.Album), value);
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
                if (changed is SongJob) Capture(node, changed);
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

    private static IEnumerable<Job> Children(Job job) => job switch
    {
        JobList list => list.Jobs,
        ExtractJob { Result: { } result } => [result],
        SongGroupsJob group => group.Songs,
        AlbumGroupsJob group => group.Albums,
        AlbumJob album => album.TrackJobs,
        _ => [],
    };

    private OutputNode RegisterNode(Job job, OutputNode? parent)
    {
        var node = new OutputNode(job, parent);
        if (!nodes.TryGetValue(job.Id, out var memberships)) nodes.Add(job.Id, memberships = []);
        memberships.Add(node);
        foreach (var child in Children(job)) node.Children.Add(RegisterNode(child, node));
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

    private void RebuildChildren(OutputNode node, Job job)
    {
        // Keep cached facts/positions for unchanged identities, including
        // duplicate source memberships. Appending an extraction must not erase
        // previously completed siblings while only membership is reconciled.
        var retained = node.Children.GroupBy(child => child.Id)
            .ToDictionary(group => group.Key, group => new Queue<OutputNode>(group));
        node.Children.Clear();
        foreach (var child in Children(job))
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
        var children = Children(job).ToArray();
        if (!node.Children.Select(child => child.Id).SequenceEqual(children.Select(child => child.Id)))
            RebuildChildren(node, job);
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
        if (Kind == OutputFileKind.Index && job is AlbumJob album && !album.IsPending && ShouldWriteIndexEntry(album))
        {
            jobDownloadPaths.TryGetValue(album.Id, out string? pathOverride);
            var (state, reason) = JobToIndexState(album);
            PutIndex(new IndexEntry { Artist = album.Query.Artist, Album = album.Query.Album,
                DownloadPath = pathOverride ?? album.DownloadPath ?? "", Length = -1,
                State = state, FailureReason = reason, IsAlbum = true });
        }
        if (job is not SongJob song) return;
        string? line = song.IsPending || song.IsNotAudio || Kind == OutputFileKind.Index ? null : SongToLine(song);
        if (node.PlaylistLine != line) { node.PlaylistLine = line; revision++; }
        if (Kind == OutputFileKind.Playlist) return;
        if (song.IsPending || !ShouldWriteIndexEntry(song)) return;
        node.Candidate = new IndexEntry { Artist = song.Query.Artist, Album = song.Query.Album,
            Title = song.Query.Title, Length = song.Query.Length, DownloadPath = song.DownloadPath ?? "",
            State = JobToIndexState(song).state, FailureReason = song.FailureReason };
        node.Search = song.Config?.Search ?? node.Parent?.Search;
        bool albumChild = node.Parent?.Kind is JobSnapshotKind.Album;
        if (albumChild) return;
        if (node.Parent is { Kind: JobSnapshotKind.SongGroups } group)
        {
            if (successful || group.SuccessfulChildren == 0 && ReferenceEquals(group.Children.FirstOrDefault(), node))
                PutIndex(node.Candidate, node.Search);
            if (group.SuccessfulChildren == 0 && group.Children.FirstOrDefault()?.Candidate is { } fallback)
                PutIndex(fallback, group.Children[0].Search);
        }
        else PutIndex(node.Candidate, node.Search);
    }

    private void PutIndex(IndexEntry candidate, SearchSettings? search = null)
    {
        if (Kind == OutputFileKind.Playlist) return;
        string key = candidate.ToKey();
        if (!previousRunData.TryGetValue(key, out var value) && search?.GroupResults == true)
            value = FindSong(candidate, search);
        if (value is null)
        {
            value = new IndexValue(candidate, key);
            previousRunData.Add(key, value);
            RegisterVariant(value);
        }
        else
        {
            var previous = value.Entry;
            if (previous.State == candidate.State && previous.FailureReason == candidate.FailureReason
                && Utils.NormalizedPath(previous.DownloadPath) == Utils.NormalizedPath(candidate.DownloadPath)) return;
            value.Entry = previous with { State = candidate.State, FailureReason = candidate.FailureReason,
                DownloadPath = candidate.DownloadPath };
            value.Encoded = null;
        }
        revision++;
    }

    private void AddPlaylistLines(OutputNode node, List<string> result)
    {
        if (node.Kind == JobSnapshotKind.Song)
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
        foreach (var child in node.Children) AddPlaylistLines(child, result);
    }

    internal bool PublishPending()
    {
        if (!IsWritable) return false;
        lock (writeGate)
        {
            string contents;
            long capturedRevision;
            lock (locker)
            {
                if (writtenRevision == revision) return false;
                capturedRevision = revision;
                if (Kind != OutputFileKind.Index)
                {
                    var current = originalLines.ToList();
                    foreach (var root in roots) AddPlaylistLines(root, current);
                    contents = BuildPlaylist(current);
                }
                else contents = BuildIndex();
            }
            PublishContents(contents);
            lock (locker) writtenRevision = capturedRevision;
            return true;
        }
    }

    private void PublishContents(string contents)
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        // Reuse the inode, as the original index/playlist did. Both truncating
        // before writing and replacing by rename can force ext4 data writeback
        // for every intermediate snapshot. Grow normally; trim only a genuinely
        // shorter document after its complete contents have been written.
        using var stream = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
        writer.Flush();
        if (stream.Length > stream.Position) stream.SetLength(stream.Position);
    }

    private string BuildPlaylist(List<string> current)
        => string.Join('\n', current) + (current.Count > 0 ? "\n" : "");

    private string BuildIndex()
    {
        var contents = new StringBuilder("filepath,artist,album,title,length,tracktype,state,failurereason\n");
        // Album lookup aliases share a row; write it once, not once per alias.
        foreach (var value in previousRunData.Values.Distinct())
            contents.Append(value.Encoded ??= EncodeIndexEntry(value.Entry));
        return contents.Append('\n').ToString();
    }

    private string EncodeIndexEntry(IndexEntry entry)
    {
        string localPath = entry.DownloadPath;
        if (Utils.NormalizedPath(localPath).StartsWith(parent))
            localPath = "./" + System.IO.Path.GetRelativePath(parent, localPath);
        var encoded = new StringBuilder();
        string[] fields = [localPath.Replace('\\', '/'), entry.Artist, entry.Album, entry.Title,
            entry.Length.ToString(), (entry.IsAlbum || entry.Title.Length == 0 ? 1 : 0).ToString(),
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

    private string SongToLine(SongJob song)
    {
        string? failureReason = song.FailureReason != JobFailureReason.None ? song.FailureReason.ToString() : null;
        if (failureReason == null && song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.NotFoundLastTime)
            failureReason = nameof(JobFailureReason.NoMatchingResults);

        if (failureReason != null)
            return $"#FAIL: {song} [{failureReason}]";

        if (!string.IsNullOrEmpty(song.DownloadPath))
        {
            if (Utils.NormalizedPath(song.DownloadPath).StartsWith(parent))
                return System.IO.Path.GetRelativePath(parent, song.DownloadPath);
            else
                return song.DownloadPath;
        }

        return $"# {song}";
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
            foreach (var value in previousRunData.Values.Distinct()) RegisterVariant(value);
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
            return previousRunData.GetValueOrDefault(MakeAlbumKey(job.Query.Artist, job.Query.Album))?.Entry;
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
        lock (locker) return previousRunData.Values.Distinct().Select(value => value.Entry).ToArray();
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

    private static string MakeAlbumKey(string artist, string album)
    {
        return new IndexEntry { Artist = artist, Album = album, Title = "", Length = -1 }.ToKey();
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

    private string ReadAllText()
    {
        if (!File.Exists(Path))
            return "";
        using var fileStream    = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var streamReader  = new StreamReader(fileStream, encoding: Encoding.UTF8);
        return streamReader.ReadToEnd();
    }

    private string[] ReadAllLines()
    {
        var text = ReadAllText().TrimEnd();
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        return text.Split('\n');
    }
}
