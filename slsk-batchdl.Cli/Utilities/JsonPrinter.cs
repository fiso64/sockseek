using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Soulseek;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sldl.Cli;


public class UserInfoJson
{
    public string Username          { get; set; } = null!;
    public float  UploadSpeed       { get; set; }
    public bool   HasFreeUploadSlot { get; set; }

    public UserInfoJson() { }

    public UserInfoJson(SearchResponse response)
    {
        Username          = response.Username;
        UploadSpeed       = response.UploadSpeed / (1024f * 1024f);
        HasFreeUploadSlot = response.HasFreeUploadSlot;
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

    public FileInfoJson() { }

    public FileInfoJson(Soulseek.File file)
    {
        Length     = file.Length;
        Filename   = file.Filename;
        Size       = file.Size;
        Bitrate    = file.BitRate;
        SampleRate = file.SampleRate;
        BitDepth   = file.BitDepth;
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

    // Preserved for JSON backward-compat: 0 = Normal (song), 1 = Album.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TrackType Type { get; set; } = TrackType.Normal;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FailureReason? FailureReason { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobState State { get; set; } = JobState.Pending;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    public TrackJson() { }

    public TrackJson(SongJob song)
    {
        Title         = string.IsNullOrEmpty(song.Query.Title)  ? null : song.Query.Title;
        Artist        = string.IsNullOrEmpty(song.Query.Artist) ? null : song.Query.Artist;
        Album         = string.IsNullOrEmpty(song.Query.Album)  ? null : song.Query.Album;
        Path          = string.IsNullOrEmpty(song.DownloadPath) ? null : song.DownloadPath?.Replace('\\', '/');
        Length        = song.Query.Length == -1 ? null : (int?)song.Query.Length;
        Type          = TrackType.Normal;
        FailureReason = song.FailureReason == Sldl.Core.FailureReason.None ? null : song.FailureReason;
        State         = song.State;
    }

    public TrackJson(IndexEntry entry)
    {
        Title         = string.IsNullOrEmpty(entry.Title)        ? null : entry.Title;
        Artist        = string.IsNullOrEmpty(entry.Artist)       ? null : entry.Artist;
        Album         = string.IsNullOrEmpty(entry.Album)        ? null : entry.Album;
        Path          = string.IsNullOrEmpty(entry.DownloadPath) ? null : entry.DownloadPath.Replace('\\', '/');
        Length        = entry.Length == -1 ? null : (int?)entry.Length;
        Type          = entry.IsAlbum ? TrackType.Album : TrackType.Normal;
        FailureReason = entry.FailureReason == Sldl.Core.FailureReason.None ? null : entry.FailureReason;
        State         = entry.State;
    }
}

public class AggregateTrackJson
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

    public static void PrintTrackResultJson(SongQuery query, IEnumerable<(SearchResponse, Soulseek.File)> results, bool printAll = false)
    {
        if (results == null || !results.Any())
        {
            Console.WriteLine("[]");
            return;
        }

        var trackResults = results.Select(x => new TrackResultJson
        {
            User = new UserInfoJson(x.Item1),
            File = new FileInfoJson(x.Item2)
        });

        if (!printAll)
            trackResults = trackResults.Take(1);

        var json = JsonSerializer.Serialize(trackResults, _options);
        Console.WriteLine(json);
    }

    public static void PrintAggregateJson(IEnumerable<SongJob> songs)
    {
        var songList = songs.ToList();
        if (songList.Count == 0)
        {
            Console.WriteLine("[]");
            return;
        }

        var aggregateTracks = songList.Select(s => new AggregateTrackJson
        {
            Artist  = s.Query.Artist,
            Title   = s.Query.Title,
            Album   = s.Query.Album,
            Length  = s.Query.Length == -1 ? null : (int?)s.Query.Length,
            Results = s.Candidates?
                .Select(c => new TrackResultJson
                {
                    User = new UserInfoJson(c.Response),
                    File = new FileInfoJson(c.File)
                })
                .ToList() ?? new List<TrackResultJson>()
        }).ToList();

        var json = JsonSerializer.Serialize(aggregateTracks, _options);
        Console.WriteLine(json);
    }

    public static void PrintAlbumJson(List<AlbumFolder> folders, AlbumJob job)
    {
        if (folders.Count == 0)
        {
            Console.WriteLine("[]");
            return;
        }

        var albumResults = folders
            .Where(f => f.Files.Count > 0)
            .Select(f => new AlbumResultJson
            {
                User  = new UserInfoJson(f.Files[0].ResolvedTarget!.Response),
                Files = f.Files.Select(af => new FileInfoJson(af.ResolvedTarget!.File)).ToList()
            });

        var json = JsonSerializer.Serialize(albumResults, _options);
        Console.WriteLine(json);
    }

    public static void PrintIndexJson(IEnumerable<IndexEntry> entries)
    {
        var trackJsons = entries.Select(e => new TrackJson(e));
        var json = JsonSerializer.Serialize(trackJsons, _indentedOptions);
        Console.WriteLine(json);
    }
}
