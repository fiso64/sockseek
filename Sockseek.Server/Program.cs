using Sockseek.Server;

// Build-time OpenAPI tooling needs an executable entry point. Operators use the
// CLI's config-file/argument binding; this assembly has no second configuration path.
if (!ServerHost.IsOpenApiGeneration)
{
    Console.Error.WriteLine("Start the daemon with 'sockseek daemon'.");
    return 1;
}

await using var app = ServerHost.Build([], new ServerOptions(), logToConsole: false);
await app.RunAsync();
return 0;
