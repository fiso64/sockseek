using Sldl.Core.Settings;

namespace Sldl.Server;

public sealed class ServerOptions
{
    public string Name { get; set; } = "slsk-batchdl";
    public EngineSettings Engine { get; set; } = new();
    public DownloadSettings DefaultDownload { get; set; } = new();
    public DownloadSettingsPatchDto? LaunchDownloadSettings { get; set; }
    public ProfileCatalog Profiles { get; set; } = ProfileCatalog.Empty;
}
