using System.Text.Json.Serialization;

namespace Sockseek.Core.Settings;

/// Controls YouTube playlist/video extraction.
public class YouTubeSettings
{
    internal YouTubeSettings ShallowClone() => (YouTubeSettings)MemberwiseClone();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ApiKey { get; set; }

    /// When true, also fetch deleted tracks from an archive service.
    public bool GetDeleted { get; set; }

    /// When true, only return deleted tracks (skip live ones).
    public bool DeletedOnly { get; set; }


}
