using Sockseek.Core.Extractors;

namespace Sockseek.Core.Settings;

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
