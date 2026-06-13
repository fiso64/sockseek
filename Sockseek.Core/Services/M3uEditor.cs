using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;
using System.Text;

namespace Sockseek.Core.Services;


// Holds the persisted state of one entry read from a prior-run index file.
public class IndexEntry
{
    public string       DownloadPath  { get; set; } = "";
    public string       Artist        { get; set; } = "";
    public string       Album         { get; set; } = "";
    public string       Title         { get; set; } = "";
    public int          Length        { get; set; } = -1;
    public JobStateOld  State         { get; set; } = JobStateOld.Pending;
    public JobFailureReason FailureReason { get; set; } = JobFailureReason.None;
    // True when a Normal-type entry was promoted to also be keyed as an Album entry.
    public bool         IsAlbum       { get; set; }

    public string ToKey() =>
        $"{Artist.ToLower()}\n{Album.ToLower()}\n{Title.ToLower()}\n{Length}";
}

// TODO: This class does two completely different jobs with different file formats.
// The index file is no longer in M3U format. Separate into PlaylistEditor and IndexEditor (or similar).
// Also remove the M3uOption enum.
public class M3uEditor
{
    public string path { get; private set; } = null!;
    public M3uOption option = M3uOption.Index;
    string parent = null!;
    List<string> lines = null!;
    int initialLineCount = 0;
    bool needFirstUpdate = false;
    int offset = 0;
    readonly JobList queue;
    readonly Dictionary<string, IndexEntry> previousRunData = new(); // key → IndexEntry

    private readonly Lock locker = new();
    private readonly Dictionary<Guid, string?> jobDownloadPaths = new();

    public void NotifyJobDownloadPath(Guid jobId, string? path)
    {
        lock (locker) jobDownloadPaths[jobId] = path;
    }

    private M3uEditor(JobList queue, M3uOption option, int offset = 0)
    {
        this.queue  = queue;
        this.option = option;
        this.offset = offset;
        this.needFirstUpdate = option == M3uOption.All || option == M3uOption.Playlist;
    }

    public M3uEditor(string path, JobList queue, M3uOption option, bool loadPreviousResults) : this(queue, option)
    {
        SetPathAndLoad(path, loadPreviousResults);
    }

    private void SetPathAndLoad(string path, bool loadPreviousResults)
    {
        if (string.IsNullOrEmpty(path))
            return;

        if (this.path != null && Utils.NormalizedPath(this.path) == Utils.NormalizedPath(path))
            return;

        this.path = Utils.GetFullPath(path);
        parent    = Utils.NormalizedPath(Path.GetDirectoryName(this.path) ?? "");

        lines = ReadAllLines().ToList();
        initialLineCount = lines.Count;

        if (loadPreviousResults)
            LoadPreviousResults();
    }

    private void LoadPreviousResults()
    {
        if (lines.Count == 0 || !lines.Any(x => x.Trim() != ""))
            return;

        bool useOldFormat = lines[0].StartsWith("#Sockseek:");

        var indexLines   = useOldFormat ? new string[] { lines[0] } : lines.Skip(1);
        var currentItem  = new StringBuilder();

        if (useOldFormat) lines = lines.Skip(1).ToList();
        int startOffset = useOldFormat ? "#Sockseek:".Length : 0;

        foreach (var SockseekLine in indexLines)
        {
            if (string.IsNullOrWhiteSpace(SockseekLine))
                continue;

            int  k       = startOffset;
            bool inQuotes = false;

            for (; k < SockseekLine.Length && SockseekLine[k] == ' '; k++) ;

            for (; k < SockseekLine.Length; k++)
            {
                var entry = new IndexEntry();
                int field = 0;
                for (int i = k; i < SockseekLine.Length; i++)
                {
                    char c = SockseekLine[i];

                    if (c == '"' && (i == k || SockseekLine[i - 1] != '\\'))
                    {
                        if (inQuotes && i + 1 < SockseekLine.Length && SockseekLine[i + 1] == '"')
                        {
                            currentItem.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                        }
                    }
                    else if (field <= 6 && c == ',' && !inQuotes)
                    {
                        var x = currentItem.ToString();

                        if (field == 0)
                        {
                            if (x.StartsWith("./"))
                                x = Path.Join(parent, x[2..]);
                            entry.DownloadPath = x;
                        }
                        else if (field == 1) entry.Artist = x;
                        else if (field == 2) entry.Album  = x;
                        else if (field == 3) entry.Title  = x;
                        else if (field == 4) entry.Length = int.Parse(x);
                        else if (field == 5) { /* tracktype — ignored, determined by Title/Album */ }
                        else if (field == 6) entry.State  = (JobStateOld)int.Parse(x);

                        currentItem.Clear();
                        field++;
                    }
                    else if (field == 7 && c == ';' && useOldFormat)
                    {
                        entry.FailureReason = (JobFailureReason)int.Parse(currentItem.ToString());
                        currentItem.Clear();
                        k = i;
                        break;
                    }
                    else
                    {
                        currentItem.Append(c);
                    }
                }

                if (!useOldFormat)
                {
                    entry.FailureReason = (JobFailureReason)int.Parse(currentItem.ToString());
                    currentItem.Clear();
                }

                previousRunData[entry.ToKey()] = entry;

                // When an entry has both album and title set, also register it under the album-only key
                // so AlbumJob lookups (which use empty title) can find it.
                if (entry.Title.Length > 0 && entry.Album.Length > 0)
                {
                    var albumKey = new IndexEntry
                    {
                        Artist = entry.Artist,
                        Album  = entry.Album,
                        Title  = "",
                        Length = -1,
                    }.ToKey();
                    previousRunData.TryAdd(albumKey, entry);
                }

                if (!useOldFormat)
                    break;
            }
        }
    }

    public void Update()
    {
        SockseekLog.Trace($"M3uEditor.Update() called for {path} (Option: {option}, Queue length: {queue.Jobs.Count})");
        if (option == M3uOption.None)
            return;

        lock (queue) lock (locker)
        {
            bool needUpdate = false;
            var newLines = option == M3uOption.Playlist ? lines.Take(initialLineCount).ToList() : lines;

            bool entryChanged(IndexEntry? prev, string downloadPath, JobStateOld state, JobFailureReason reason)
            {
                return prev == null
                    || prev.State != state
                    || prev.FailureReason != reason
                    || Utils.NormalizedPath(prev.DownloadPath) != Utils.NormalizedPath(downloadPath ?? "");
            }

            void updateEntryIfNeeded(string key, string downloadPath, string artist, string album,
                string title, int length, JobStateOld state, JobFailureReason reason, bool isAlbum = false)
            {
                if (option == M3uOption.Playlist)
                    return;

                previousRunData.TryGetValue(key, out var prev);

                if (!needUpdate)
                    needUpdate = entryChanged(prev, downloadPath, state, reason);

                if (needUpdate)
                {
                    SockseekLog.Trace($"M3uEditor: Updating entry for {key}");
                    if (prev == null)
                    {
                        previousRunData[key] = new IndexEntry
                        {
                            DownloadPath  = downloadPath ?? "",
                            Artist        = artist,
                            Album         = album,
                            Title         = title,
                            Length        = length,
                            State         = state,
                            FailureReason = reason,
                            IsAlbum       = isAlbum,
                        };
                    }
                    else
                    {
                        prev.State         = state;
                        prev.FailureReason = reason;
                        prev.DownloadPath  = downloadPath ?? "";
                    }
                }
            }

            foreach (var job in queue.AllJobs())
            {
                SockseekLog.Trace($"M3uEditor: Checking job {job.GetType().Name} (ID: {job.DisplayId}, Lifecycle: {job.LifecycleState}, Activity: {job.ActivityPhase}, Outcome: {job.TerminalOutcome}, Skip: {job.SkipReason})");
                var albumJobs = job switch
                {
                    AlbumJob aj => new[] { aj },
                    AlbumAggregateJob aaj => aaj.Albums,
                    _ => Enumerable.Empty<AlbumJob>()
                };

                foreach (var albumJob in albumJobs)
                {
                    if (!albumJob.IsPending)
                    {
                        var (state, reason) = JobToIndexState(albumJob);
                        string key = MakeAlbumKey(albumJob.Query.Artist, albumJob.Query.Album);
                        jobDownloadPaths.TryGetValue(albumJob.Id, out var downloadPath);
                        updateEntryIfNeeded(key, downloadPath ?? "",
                            albumJob.Query.Artist, albumJob.Query.Album, "", -1, state, reason, isAlbum: true);
                    }
                }

                IEnumerable<SongJob> songs = job switch
                {
                    SongJob sj      => new[] { sj },
                    AggregateJob ag => ag.Songs.Where(s => s.IsSuccessfulTerminal).DefaultIfEmpty(ag.Songs.FirstOrDefault()!).Where(s => s != null)!,
                    AlbumJob aj     => aj.ResolvedTarget?.Files ?? Enumerable.Empty<SongJob>(),
                    AlbumAggregateJob aaj => aaj.Albums.SelectMany(a => a.ResolvedTarget?.Files ?? Enumerable.Empty<SongJob>()),
                    _               => Enumerable.Empty<SongJob>(),
                };

                foreach (var song in songs)
                {
                    if (song.IsNotAudio)
                        continue;

                    SockseekLog.Trace($"M3uEditor: Checking song {song.Query.Title} (Lifecycle: {song.LifecycleState}, Activity: {song.ActivityPhase}, Outcome: {song.TerminalOutcome}, Skip: {song.SkipReason}, Path: {song.DownloadPath})");
                    if (song.IsPending)
                        continue;

                    bool isAlbumChild = job is AlbumJob or AlbumAggregateJob;
                    if (!isAlbumChild)
                    {
                        string key = MakeSongKey(song);
                        var prev = PreviousRunResult(song, song.Config?.Search ?? job.Config?.Search);
                        string actualKey = prev != null ? prev.ToKey() : key;

                        updateEntryIfNeeded(actualKey, song.DownloadPath ?? "",
                            song.Query.Artist, song.Query.Album, song.Query.Title, song.Query.Length,
                            JobToIndexState(song).state, song.FailureReason);
                    }

                    if (option == M3uOption.All || option == M3uOption.Playlist)
                    {
                        var line = SongToLine(song);
                        newLines.Add(line);
                        SockseekLog.Trace($"M3uEditor: Added line to newLines: {line}");
                    }
                }
            }

            if (option == M3uOption.Playlist && !newLines.SequenceEqual(lines))
            {
                SockseekLog.Trace($"M3uEditor: newLines changed (count: {newLines.Count} vs {lines.Count})");
                lines = newLines;
                needUpdate = true;
            }

            if (needUpdate || needFirstUpdate)
            {
                SockseekLog.Trace($"M3uEditor: Writing all lines (needUpdate: {needUpdate}, needFirstUpdate: {needFirstUpdate})");
                needFirstUpdate = false;
                WriteAllLines();
            }
            else
            {
                SockseekLog.Trace($"M3uEditor: No update needed.");
            }
        }
    }

    class Writer
    {
        private readonly StringBuilder sb = new();
        public void Write(string s) => sb.Append(s);
        public void Write(char c)   => sb.Append(c);
        public override string ToString() => sb.ToString();
    }

    private void WriteAllLines()
    {
        SockseekLog.Trace($"M3uEditor: Writing file to {path}");
        if (!Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        var writer = new Writer();

        if (option != M3uOption.Playlist)
            WriteSockseekLine(writer);

        if (option != M3uOption.Index)
        {
            foreach (var line in lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }
        }

        File.WriteAllText(path, writer.ToString());
    }

    private void WriteSockseekLine(Writer writer)
    {
        void writeCsvLine(string[] items)
        {
            bool comma = false;
            foreach (var item in items)
            {
                if (comma) writer.Write(',');

                if (item.Contains(',') || item.Contains('\"'))
                {
                    writer.Write('"');
                    writer.Write(item.Replace("\"", "\"\""));
                    writer.Write('"');
                }
                else
                {
                    writer.Write(item);
                }
                comma = true;
            }
        }

        writer.Write("filepath,artist,album,title,length,tracktype,state,failurereason\n");

        foreach (var val in previousRunData.Values)
        {
            string p = val.DownloadPath;
            if (Utils.NormalizedPath(p).StartsWith(parent))
                p = "./" + System.IO.Path.GetRelativePath(parent, p);
            p = p.Replace('\\', '/');

            // tracktype: 1 for album entries, 0 for song entries (backward-compat with old readers)
            int tracktype = val.IsAlbum || val.Title.Length == 0 ? 1 : 0;

            var items = new string[]
            {
                p,
                val.Artist,
                val.Album,
                val.Title,
                val.Length.ToString(),
                tracktype.ToString(),
                ((int)val.State).ToString(),
                ((int)val.FailureReason).ToString(),
            };

            writeCsvLine(items);
            writer.Write('\n');
        }

        writer.Write('\n');
    }

    private string SongToLine(SongJob song)
    {
        string? failureReason = song.FailureReason != JobFailureReason.None ? song.FailureReason.ToString() : null;
        if (failureReason == null && song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.NotFoundLastTime)
            failureReason = nameof(JobFailureReason.NoSuitableFileFound);

        if (failureReason != null)
            return $"#FAIL: {song} [{failureReason}]";

        if (!string.IsNullOrEmpty(song.DownloadPath))
        {
            if (Utils.NormalizedPath(song.DownloadPath).StartsWith(parent))
                return Path.GetRelativePath(parent, song.DownloadPath);
            else
                return song.DownloadPath;
        }

        return $"# {song}";
    }

    // Looks up the persisted state for a SongJob from a prior run.
    public IndexEntry? PreviousRunResult(SongJob song, SearchSettings? search = null)
    {
        if (previousRunData.TryGetValue(MakeSongKey(song), out var t))
            return t;

        if (search != null && search.IsAggregate)
        {
            var comparer = new SongQueryComparer(ignoreCase: true, search.AggregateLengthTol);
            foreach (var entry in previousRunData.Values)
            {
                if (entry.IsAlbum || entry.Title.Length == 0) continue;
                var entryQuery = new SongQuery { Artist = entry.Artist, Album = entry.Album, Title = entry.Title, Length = entry.Length };
                if (comparer.Equals(song.Query, entryQuery))
                    return entry;
            }
        }

        return null;
    }

    // Looks up the persisted state for an AlbumJob from a prior run.
    public IndexEntry? PreviousRunResult(AlbumJob job)
    {
        previousRunData.TryGetValue(MakeAlbumKey(job.Query.Artist, job.Query.Album), out var t);
        return t;
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

    public IReadOnlyCollection<IndexEntry> GetPreviousRunData() => previousRunData.Values;

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
                => (JobStateOld.NotFoundLastTime, job.FailureReason == JobFailureReason.None ? JobFailureReason.NoSuitableFileFound : job.FailureReason),
            JobTerminalOutcome.Skipped
                => (JobStateOld.AlreadyExists, job.FailureReason),
            _ => (JobStateOld.Pending, JobFailureReason.None),
        };
    }

    private string ReadAllText()
    {
        if (!File.Exists(path))
            return "";
        using var fileStream    = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
