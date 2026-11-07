using Sockseek.Api;
using Sockseek.Server;
using Sockseek.Core.Diagnostics;

WebApplication? app = null;

try
{
    app = ServerHost.Build(args);
    var logger = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Sockseek.Server.Program");
    using var exceptionObserver = ProcessExceptionObserver.Install(logger);
    // Keep services alive while reporting startup failures; Run disposes them before throwing.
    await app.StartAsync();
    await app.WaitForShutdownAsync();
}
catch (Exception ex)
{
    Environment.ExitCode = 1;
    if (app is not null)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Sockseek.Server.Program");
        ServerLogMessages.UnhandledServerError(logger, ex);
    }
    else
    {
        Console.Error.WriteLine($"Sockseek server startup failed: {ex}");
    }
}
finally
{
    if (app is not null) await app.DisposeAsync();
}
