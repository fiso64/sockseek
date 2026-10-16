using Microsoft.AspNetCore.Builder;
using Sockseek.Server;

namespace Tests;

internal static class TestServerHost
{
    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null)
        // Engine and host share this app's logger factory; do not install its console output.
        => ServerHost.Build(args, options, url, logToConsole: false);
}
