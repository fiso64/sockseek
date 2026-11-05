using System.Text.Json.Serialization;
namespace Sockseek.Core.Settings;

public class SpotifySettings
{
    [SettingOption("--si", "--spotify-id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ClientId { get; set; }

    [SettingOption("--ss", "--spotify-secret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ClientSecret { get; set; }

    [SettingOption("--stk", "--spotify-token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Token { get; set; }

    [SettingOption("--str", "--spotify-refresh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Refresh { get; set; }
}
