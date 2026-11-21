using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Sockseek.Core.Extractors;

namespace Sockseek.Core.Settings;

/// Controls what is extracted and how.
public class ExtractionSettings
{
    [SettingOption("-i", "--input", AllowMultiple = false)]
    public string? Input { get; set; }

    [SettingOption("--it", "--input-type", Parser = nameof(ParseInputType))]
    public InputType InputType { get; set; } = InputType.None;

    private static InputType ParseInputType(string value, string flag)
        => InputTypes.TryParse(value, out var parsed)
            ? parsed : throw new ArgumentException($"Input error: Invalid input type '{value}'");

    [SettingOption("-n", "--number")]
    [Range(1, int.MaxValue)]
    public int MaxTracks { get; set; } = int.MaxValue;

    [SettingOption("--offset")]
    [Range(0, int.MaxValue)]
    public int Offset { get; set; }

    [SettingOption("-r", "--reverse")]
    public bool Reverse { get; set; }

    [SettingOption("--rfp", "--rfs", "--remove-from-playlist", "--remove-from-source")]
    public bool RemoveTracksFromSource { get; set; }

    // Null means the input source decides. Ambiguous string/list inputs default to album mode,
    // while concrete sources such as CSV, Spotify, YouTube, and Soulseek links keep their own shape.
    [SettingOption("-a", "--album", Parser = nameof(ParseAlbumMode), ValueKind = OptionValueKind.Boolean)]
    [SettingOption("-s", "--song", Parser = nameof(ParseSongMode), ValueKind = OptionValueKind.Boolean)]
    public ExtractionMode? RequestedMode { get; set; }

    private static ExtractionMode ParseAlbumMode(string value, string flag)
        => (bool)SettingParser.Parse(value, typeof(bool), flag)! ? ExtractionMode.Album : ExtractionMode.Song;

    private static ExtractionMode ParseSongMode(string value, string flag)
        => (bool)SettingParser.Parse(value, typeof(bool), flag)! ? ExtractionMode.Song : ExtractionMode.Album;

    // When true, song-shaped extractor results are converted into album jobs where possible.
    // This is deliberately separate from RequestedMode: --album controls ambiguous string
    // interpretation, while --upgrade-to-album opts into source-result conversion.
    [SettingOption("--uta", "--upgrade-to-album")]
    public bool UpgradeToAlbum { get; set; }

    [SettingOption("--emtc", "--extract-min-track-count")]
    public bool SetAlbumMinTrackCount { get; set; } = true;

    [SettingOption("--eMtc", "--extract-max-track-count")]
    public bool SetAlbumMaxTrackCount { get; set; }
}

/// Controls Bandcamp extraction behaviour.
public class BandcampSettings
{
    /// When set, read the page HTML from this local file instead of fetching it from the URL.
    [SettingOption("--from-html")]
    public string? HtmlFromFile { get; set; }
}

public class CsvSettings
{
    [SettingOption("--ac", "--artist-col")]
    public string ArtistCol { get; set; } = "";

    [SettingOption("--alc", "--album-col")]
    public string AlbumCol { get; set; } = "";

    [SettingOption("--tc", "--track-col", "--title-col")]
    public string TitleCol { get; set; } = "";

    [SettingOption("--yic", "--yt-id-col")]
    public string YtIdCol { get; set; } = "";

    [SettingOption("--ydc", "--yt-desc-col")]
    public string DescCol { get; set; } = "";

    [SettingOption("--atcc", "--album-track-count-col")]
    public string TrackCountCol { get; set; } = "";

    [SettingOption("--lc", "--length-col")]
    public string LengthCol { get; set; } = "";

    [SettingOption("--tf", "--time-format")]
    [SettingValidation(typeof(CsvExtractor), nameof(CsvExtractor.ValidateTimeFormat))]
    public string TimeUnit { get; set; } = "s";

    [SettingOption("--yp", "--yt-parse")]
    public bool YtParse { get; set; }
}

/// Controls SoundCloud extraction behaviour.
public class SoundCloudSettings
{
    /// OAuth token sent to the SoundCloud api-v2 endpoints.
    [SettingOption("--soundcloud-token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? AuthToken { get; set; }
}

public class SpotifySettings
{
    [SettingOption("--si", "--spotify-id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ClientId { get; set; }

    [SettingOption("--ss", "--spotify-secret")]
    [SettingOption("--spotify-secret-file", CredentialFile = true)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ClientSecret { get; set; }

    [SettingOption("--stk", "--spotify-token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Token { get; set; }

    [SettingOption("--str", "--spotify-refresh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Refresh { get; set; }
}

/// Controls YouTube playlist/video extraction.
public class YouTubeSettings
{
    [SettingOption("--yk", "--youtube-key")]
    [SettingOption("--youtube-key-file", CredentialFile = true)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ApiKey { get; set; }

    /// When true, also fetch deleted tracks from an archive service.
    [SettingOption("--gd", "--get-deleted")]
    public bool GetDeleted { get; set; }

    /// When true, only return deleted tracks (skip live ones).
    [SettingOption("--do", "--deleted-only")]
    public bool DeletedOnly { get; set; }
}
