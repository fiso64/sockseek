namespace Sldl.Cli;

/// Settings consumed by the CLI when connecting to an existing daemon.
public class RemoteSettings
{
    public string? ServerUrl { get; set; }
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ServerUrl);
}
