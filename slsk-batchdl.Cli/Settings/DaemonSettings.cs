namespace Sldl.Cli;

/// Settings consumed by the CLI launcher when hosting `sldl daemon`.
public class DaemonSettings
{
    /// IP/interface used by `sldl daemon` for the HTTP/SignalR API.
    public string ListenIp { get; set; } = "127.0.0.1";

    /// Port used by `sldl daemon` for the HTTP/SignalR API.
    public int ListenPort { get; set; } = 5030;
}
