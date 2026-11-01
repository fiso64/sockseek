using Microsoft.AspNetCore.Builder;
using Sockseek.Server;

namespace Tests;

internal static class TestServerHost
{
    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null)
    {
        // Ordinary host fixtures have already created their isolated data root.
        // Reuse schema construction only; startup still validates/opens the copy.
        // Missing roots and existing/corrupt databases retain real startup behavior.
        if (options?.Persistence is { Enabled: true, DataDirectory: { } directory }
            && Directory.Exists(directory))
        {
            string path = SockseekDataPaths.ResolveDatabasePath(directory);
            if (!File.Exists(path)) TestDatabase.Create(path);
        }
        // Engine and host share this app's logger factory; do not install its console output.
        return ServerHost.Build(args, options, url, logToConsole: false);
    }
}
