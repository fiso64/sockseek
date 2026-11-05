using System.Text.Json.Serialization;
namespace Sockseek.Core.Settings;

/// Controls SoundCloud extraction behaviour.
public class SoundCloudSettings
{
    /// OAuth token sent to the SoundCloud api-v2 endpoints.
    [SettingOption("--soundcloud-token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? AuthToken { get; set; }
}
