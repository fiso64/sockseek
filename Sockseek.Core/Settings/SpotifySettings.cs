namespace Sockseek.Core.Settings;

public class SpotifySettings
{
    [SettingOption("--si", "--spotify-id")]
    public string? ClientId { get; set; }

    [SettingOption("--ss", "--spotify-secret")]
    public string? ClientSecret { get; set; }

    [SettingOption("--stk", "--spotify-token")]
    public string? Token { get; set; }

    [SettingOption("--str", "--spotify-refresh")]
    public string? Refresh { get; set; }
}
