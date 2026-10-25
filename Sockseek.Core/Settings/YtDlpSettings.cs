namespace Sockseek.Core.Settings;

/// Controls yt-dlp as a download fallback when a track is not found on Soulseek.
public class YtDlpSettings
{
    /// When true, fall back to yt-dlp for tracks not found on Soulseek.
    [SettingOption("--yd", "--yt-dlp")]
    public bool UseYtdlp { get; set; }

    /// Command-line arguments passed to yt-dlp. Supports {id} and {savepath-noext} placeholders.
    [SettingOption("--yda", "--yt-dlp-argument")]
    public string? YtdlpArgument { get; set; }
}
