using System.ComponentModel.DataAnnotations;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

/// Settings consumed by the CLI launcher when hosting `Sockseek daemon`.
public class DaemonSettings
{
    /// IP/interface used by `Sockseek daemon` for the HTTP/SignalR API.
    [SettingOption("--server-ip", "--daemon-ip", "--api-ip")]
    public string ListenIp { get; set; } = "127.0.0.1";

    /// Port used by `Sockseek daemon` for the HTTP/SignalR API.
    [SettingOption("--server-port", "--daemon-port", "--api-port")]
    [Range(1, 65535, ErrorMessage = "{0} must be a TCP port between 1 and 65535.")]
    public int ListenPort { get; set; } = 5030;
}
