using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Sockseek.Core;

namespace Sockseek.Core.Settings;

/// Controls what is extracted and how.
public class ExtractionSettings
{
    [SettingOption("-i", "--input", AllowMultiple = false)]
    public string? Input { get; set; }

    [SettingOption("--it", "--input-type", Parser = nameof(ParseInputType))]
    public InputType InputType { get; set; } = InputType.None;

    private static InputType ParseInputType(string value, string flag)
        => Enum.TryParse<InputType>(value.Replace("-", ""), ignoreCase: true, out var parsed)
            ? parsed : throw new ArgumentException($"Input error: Invalid input type '{value}'");

    [SettingOption("-n", "--number")]
    [Range(1, int.MaxValue)]
    public int MaxTracks { get; set; } = int.MaxValue;

    [SettingOption("--offset")]
    [Range(0, int.MaxValue)]
    public int Offset { get; set; }

    [SettingOption("-r", "--reverse")]
    public bool Reverse { get; set; }

    [SettingOption("--rfp", "--rfs", "--remove-from-source", "--remove-from-playlist")]
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

    // Convenience bridge for older call sites. New mode decisions should use RequestedMode so
    // the 3.0 default album behavior does not accidentally force concrete sources into albums.
    [JsonIgnore]
    public bool IsAlbum
    {
        get => RequestedMode == ExtractionMode.Album;
        set => RequestedMode = value ? ExtractionMode.Album : ExtractionMode.Song;
    }

    [SettingOption("--emtc", "--extract-min-track-count")]
    public bool SetAlbumMinTrackCount { get; set; } = true;

    [SettingOption("--eMtc", "--extract-max-track-count")]
    public bool SetAlbumMaxTrackCount { get; set; }
}
