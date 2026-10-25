using Sockseek.Core.Settings;

namespace Sockseek.Cli;

/// Settings consumed by the CLI when connecting to an existing daemon.
public class RemoteSettings
{
    [SettingOption("--remote", "--server-url")]
    public string? ServerUrl { get; set; }
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ServerUrl);
    [SettingOption("--detach")]
    public bool Detach { get; set; }
}
