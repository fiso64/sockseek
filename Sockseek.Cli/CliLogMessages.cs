using Microsoft.Extensions.Logging;

namespace Sockseek.Cli;

internal static partial class CliLogMessages
{
    [LoggerMessage(5000, LogLevel.Error, "CLI operation {Operation} failed")]
    internal static partial void OperationFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(5001, LogLevel.Information, "CLI session started in {Mode} mode")]
    internal static partial void SessionStarted(ILogger logger, string mode);

    [LoggerMessage(5002, LogLevel.Debug, "CLI session in {Mode} mode ended with exit code {ExitCode}")]
    internal static partial void SessionEnded(ILogger logger, string mode, int exitCode);

    [LoggerMessage(5003, LogLevel.Information, "Sockseek daemon starting on {ListenUrl}")]
    internal static partial void DaemonStarting(ILogger logger, string listenUrl);

    [LoggerMessage(5004, LogLevel.Warning, "Sockseek daemon is listening on all network interfaces; the API is unauthenticated and must only be exposed on trusted networks or behind access control")]
    internal static partial void DaemonNetworkExposed(ILogger logger);

    [LoggerMessage(5005, LogLevel.Information, "Sockseek daemon stopped")]
    internal static partial void DaemonStopped(ILogger logger);

}
