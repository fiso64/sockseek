namespace Sockseek.Core.Settings;

/// Controls YouTube playlist/video extraction.
public class YouTubeSettings
{
    [SettingOption("--yk", "--youtube-key")]
    public string? ApiKey { get; set; }

    /// When true, also fetch deleted tracks from an archive service.
    [SettingOption("--gd", "--get-deleted")]
    public bool GetDeleted { get; set; }

    /// When true, only return deleted tracks (skip live ones).
    [SettingOption("--do", "--deleted-only")]
    public bool DeletedOnly { get; set; }
}
