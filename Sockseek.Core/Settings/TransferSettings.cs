namespace Sockseek.Core.Settings;

/// Controls file transfer behaviour: retries and incomplete-file handling.
public class TransferSettings
{
    /// Maximum number of times to retry downloading before giving up on the item.
    [SettingOption("--Mr", "--retries", "--max-retries")]
    public int MaxDownloadRetries { get; set; } = 10;

    /// Number of extra attempts when an unknown/transient error occurs during download.
    [SettingOption("--uer", "--unknown-error-retries")]
    public int UnknownErrorRetries { get; set; } = 2;

    /// Write directly to the destination during download.
    [SettingOption("--nie", "--no-incomplete-ext")]
    public bool NoIncompleteExt { get; set; }

    [SettingOption("--album-track-count-max-retries")]
    public int AlbumTrackCountMaxRetries { get; set; } = 5;
}

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
