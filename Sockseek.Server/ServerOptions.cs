using Sockseek.Core.Settings;
using Sockseek.Api;

namespace Sockseek.Server;

public sealed class ServerOptions
{
    public string Name { get; set; } = "Sockseek";
    public EngineSettings Engine { get; set; } = new();
    public DownloadSettings DefaultDownload { get; set; } = new();
    public DownloadSettingsPatchDto? LaunchDownloadSettings { get; set; }
    public ProfileCatalog Profiles { get; set; } = ProfileCatalog.Empty;
    public string? ConfigDir { get; set; }
}
