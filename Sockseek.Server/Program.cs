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
    app.Run();
}
catch (Exception ex)
{
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
