using System.Text.Json.Serialization;

namespace Sockseek.Core.Settings;

public class SpotifySettings
{
    internal SpotifySettings ShallowClone() => (SpotifySettings)MemberwiseClone();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ClientId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ClientSecret { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Token { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Refresh { get; set; }
}
