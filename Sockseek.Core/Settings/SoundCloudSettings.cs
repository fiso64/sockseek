namespace Sockseek.Core.Settings;

/// Controls SoundCloud extraction behaviour.
public class SoundCloudSettings
{
    /// OAuth token sent to the SoundCloud api-v2 endpoints.
    [SettingOption("--soundcloud-token")]
    public string? AuthToken { get; set; }
}
