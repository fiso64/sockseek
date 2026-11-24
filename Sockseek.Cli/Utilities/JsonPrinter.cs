using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sockseek.Cli;


public class UserInfoJson
{
    public string Username          { get; set; } = null!;
    public float  UploadSpeed       { get; set; }
    public bool   HasFreeUploadSlot { get; set; }

    public UserInfoJson(FileCandidate candidate)
    {
        Username = candidate.Username;
        UploadSpeed = (candidate.UploadSpeed ?? -1) / (1024f * 1024f);
        HasFreeUploadSlot = candidate.HasFreeUploadSlot ?? false;
    }
}

public class FileInfoJson
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int?   Length     { get; set; }
    public string Filename   { get; set; } = null!;
    public long   Size       { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int?   Bitrate    { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int?   SampleRate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int?   BitDepth   { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SearchResultVisibility Visibility { get; set; }

    public FileInfoJson(FileCandidate candidate)
    {
        Length = candidate.Length;
        Filename = candidate.Filename;
        Size = candidate.Size;
        Bitrate = candidate.BitRate;
        SampleRate = candidate.SampleRate;
        BitDepth = candidate.BitDepth;
        Visibility = candidate.Visibility;
    }
}

public class AlbumResultJson
{
    public UserInfoJson      User  { get; set; } = null!;
    public List<FileInfoJson> Files { get; set; } = new();
}

public class TrackResultJson
{
    public UserInfoJson User { get; set; } = null!;
    public FileInfoJson File { get; set; } = null!;
}

public class TrackJson
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Artist { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Album { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Length { get; set; }

    public string Type { get; } = "Normal";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobFailureReason? FailureReason { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobLifecycleState LifecycleState { get; set; } = JobLifecycleState.Pending;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobActivityPhase ActivityPhase { get; set; } = JobActivityPhase.None;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobTerminalOutcome TerminalOutcome { get; set; } = JobTerminalOutcome.None;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobSkipReason SkipReason { get; set; } = JobSkipReason.None;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    public TrackJson(IndexEntry entry)
    {
        Title         = string.IsNullOrEmpty(entry.Title)        ? null : entry.Title;
        Artist        = string.IsNullOrEmpty(entry.Artist)       ? null : entry.Artist;
        Album         = string.IsNullOrEmpty(entry.Album)        ? null : entry.Album;
        Path          = string.IsNullOrEmpty(entry.DownloadPath) ? null : entry.DownloadPath.Replace('\\', '/');
        Length        = entry.Length == -1 ? null : (int?)entry.Length;
        Type          = entry.IsAlbum ? "Album" : "Normal";
        FailureReason = entry.FailureReason == JobFailureReason.None ? null : entry.FailureReason;
        (LifecycleState, ActivityPhase, TerminalOutcome, SkipReason) = SplitIndexState(entry.State, entry.FailureReason);
    }

    private static (JobLifecycleState Lifecycle, JobActivityPhase Activity, JobTerminalOutcome Outcome, JobSkipReason SkipReason) SplitIndexState(
        JobStateOld state,
        JobFailureReason failureReason)
        => state switch
        {
            JobStateOld.Pending => (JobLifecycleState.Pending, JobActivityPhase.None, JobTerminalOutcome.None, JobSkipReason.None),
            JobStateOld.Done => (JobLifecycleState.Terminal, JobActivityPhase.None, JobTerminalOutcome.Succeeded, JobSkipReason.None),
            JobStateOld.AlreadyExists => (JobLifecycleState.Terminal, JobActivityPhase.None, JobTerminalOutcome.Skipped, JobSkipReason.AlreadyExists),
            JobStateOld.NotFoundLastTime => (JobLifecycleState.Terminal, JobActivityPhase.None, JobTerminalOutcome.Skipped, JobSkipReason.NotFoundLastTime),
            JobStateOld.Failed when failureReason == JobFailureReason.Cancelled
                => (JobLifecycleState.Terminal, JobActivityPhase.None, JobTerminalOutcome.Cancelled, JobSkipReason.None),
            JobStateOld.Failed => (JobLifecycleState.Terminal, JobActivityPhase.None, JobTerminalOutcome.Failed, JobSkipReason.None),
            _ => (JobLifecycleState.Pending, JobActivityPhase.None, JobTerminalOutcome.None, JobSkipReason.None),
        };
}

public class TrackGroupJson
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Artist { get; set; } = null!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Title { get; set; } = null!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Album { get; set; } = null!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Length { get; set; }

    public List<TrackResultJson> Results { get; set; } = new();
}

public static class JsonPrinter
{
    private static readonly JsonSerializerOptions _options = new()
    {
        //WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions _indentedOptions = new(_options) { WriteIndented = true };

    public static void PrintTrackResultJson(IEnumerable<FileCandidate> results, bool printAll = false)
    {
        var candidates = results.ToList();
        if (candidates.Count == 0)
        {
            Printing.WriteLine("[]");
            return;
        }

        var trackResults = candidates.Select(candidate => new TrackResultJson
        {
            User = new UserInfoJson(candidate),
            File = new FileInfoJson(candidate),
        });
        if (!printAll)
            trackResults = trackResults.Take(1);

        Printing.WriteLine(JsonSerializer.Serialize(trackResults, _options));
    }

    public static void PrintGroupJson(IEnumerable<SongJob> songs)
    {
        var songList = songs.ToList();
        if (songList.Count == 0)
        {
            Printing.WriteLine("[]");
            return;
        }

        var trackGroups = songList.Select(s => new TrackGroupJson
        {
            Artist  = s.Query.Artist,
            Title   = s.Query.Title,
            Album   = s.Query.Album,
            Length  = s.Query.Length == -1 ? null : (int?)s.Query.Length,
            Results = s.Candidates?
                .Select(c => new TrackResultJson
                {
                    User = new UserInfoJson(c),
                    File = new FileInfoJson(c)
                })
                .ToList() ?? new List<TrackResultJson>()
        }).ToList();

        var json = JsonSerializer.Serialize(trackGroups, _options);
        Printing.WriteLine(json);
    }

    public static void PrintAlbumJson(List<AlbumFolder> folders)
    {
        if (folders.Count == 0)
        {
            Printing.WriteLine("[]");
            return;
        }

        var albumResults = folders
            .Where(f => f.Files.Count > 0)
            .Select(f => new AlbumResultJson
            {
                User  = new UserInfoJson(f.Files[0].Candidate),
                Files = f.Files.Select(af => new FileInfoJson(af.Candidate)).ToList()
            });

        var json = JsonSerializer.Serialize(albumResults, _options);
        Printing.WriteLine(json);
    }

    public static void PrintIndexJson(IEnumerable<IndexEntry> entries)
    {
        var trackJsons = entries.Select(e => new TrackJson(e));
        var json = JsonSerializer.Serialize(trackJsons, _indentedOptions);
        Printing.WriteLine(json);
    }
}
