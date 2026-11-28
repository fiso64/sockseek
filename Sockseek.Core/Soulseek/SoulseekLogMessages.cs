using System.Net;

namespace Sockseek.Core.Services;

internal static partial class SoulseekLogMessages
{
    [LoggerMessage(2026, LogLevel.Debug, "Discarded invalid search facts (token {Token}, invalid peer: {InvalidPeer}, files: {FileCount})")]
    internal static partial void InvalidSearchResponse(ILogger logger, int token, bool invalidPeer, int fileCount);

    [LoggerMessage(2025, LogLevel.Warning, "Soulseek {Operation} response failed for peer hash {PeerHash} (suppressed failures: {SuppressedCount})")]
    internal static partial void SharingCallbackFailed(ILogger logger, Exception exception, string operation, string peerHash, long suppressedCount);

    [LoggerMessage(2023, LogLevel.Warning, "Soulseek {Source}: {Diagnostic} (suppressed warnings: {SuppressedCount})")]
    internal static partial void ProtocolWarning(ILogger logger, Exception? exception, string source, string diagnostic, long suppressedCount);

    [LoggerMessage(2024, LogLevel.Debug, "Soulseek {Source}: {Diagnostic}")]
    internal static partial void ProtocolDiagnostic(ILogger logger, string source, string diagnostic);

    [LoggerMessage(2000, LogLevel.Warning, "Soulseek excluded-search-phrase update failed")]
    internal static partial void ExcludedPhrasesFailed(ILogger logger, Exception exception);

    [LoggerMessage(2001, LogLevel.Warning, "Soulseek state observer failed")]
    internal static partial void StateObserverFailed(ILogger logger, Exception exception);

    [LoggerMessage(2002, LogLevel.Warning, "Soulseek server kicked this client; reconnecting because daemon mode is active")]
    internal static partial void KickedReconnecting(ILogger logger);

    [LoggerMessage(2003, LogLevel.Error, "Soulseek server kicked this client; stopping this run")]
    internal static partial void KickedStopping(ILogger logger);

    [LoggerMessage(2004, LogLevel.Debug, "Soulseek attempt {Attempt} failed while {Phase} after {ElapsedMs} ms: {Reason}")]
    internal static partial void SessionStartFailed(ILogger logger, int attempt, string phase, long elapsedMs, string reason);

    [LoggerMessage(2005, LogLevel.Warning, "Soulseek attempt {Attempt} failed while {Phase} after {ElapsedMs} ms: {Reason}. Retrying automatically ({SuppressedCount} other failed attempts since the last warning)")]
    internal static partial void ConnectionLost(ILogger logger, int attempt, string phase, long elapsedMs, string reason, long suppressedCount);

    [LoggerMessage(2029, LogLevel.Debug, "Next Soulseek reconnect attempt in {Delay}")]
    internal static partial void ReconnectScheduled(ILogger logger, TimeSpan delay);

    [LoggerMessage(2028, LogLevel.Debug, "Connected to Soulseek server {Endpoint} over TCP; starting login")]
    internal static partial void ServerConnected(ILogger logger, IPEndPoint? endpoint);

    [LoggerMessage(2027, LogLevel.Warning, "Lost Soulseek server connection to {Endpoint}: {Reason}")]
    internal static partial void Disconnected(ILogger logger, Exception? exception, IPEndPoint? endpoint, string reason);

    [LoggerMessage(2007, LogLevel.Error, "Soulseek login failed. Automatic retries have stopped")]
    internal static partial void PermanentFailure(ILogger logger, Exception exception);

    [LoggerMessage(2010, LogLevel.Information, "Using the local-files Soulseek client")]
    internal static partial void UsingLocalClient(ILogger logger);

    [LoggerMessage(2011, LogLevel.Debug, "Configuring the network Soulseek client")]
    internal static partial void ConfiguringNetworkClient(ILogger logger);

    [LoggerMessage(2012, LogLevel.Debug, "Starting Soulseek login")]
    internal static partial void LoginStarting(ILogger logger);

    [LoggerMessage(2013, LogLevel.Information, "Soulseek login completed at {Endpoint} after {Attempts} attempt(s), {ElapsedMs} ms since connection loss or startup attempt")]
    internal static partial void LoginCompleted(ILogger logger, IPEndPoint? endpoint, int attempts, long elapsedMs);

    [LoggerMessage(2014, LogLevel.Warning, "Soulseek client-created observer failed")]
    internal static partial void ClientCreatedObserverFailed(ILogger logger, Exception exception);

    [LoggerMessage(2015, LogLevel.Warning, "Soulseek monitor stopped with an error during disposal")]
    internal static partial void MonitorDisposeFailed(ILogger logger, Exception exception);

    [LoggerMessage(2016, LogLevel.Warning, "Configured profile picture was not loaded because image processing timed out; continuing without a picture")]
    internal static partial void ProfilePictureTimedOut(ILogger logger);

    [LoggerMessage(2017, LogLevel.Warning, "Configured profile picture was not loaded ({FailureKind}); continuing without a picture")]
    internal static partial void ProfilePictureRejected(ILogger logger, string failureKind);

    [LoggerMessage(2018, LogLevel.Information, "Building local-files Soulseek index (read tags: {ReadTags})")]
    internal static partial void LocalIndexStarting(ILogger logger, bool readTags);

    [LoggerMessage(2019, LogLevel.Warning, "Local-files metadata could not be read ({FailureKind}; suppressed since previous warning: {SuppressedCount})")]
    internal static partial void LocalMetadataFailed(ILogger logger, string failureKind, long suppressedCount);

    [LoggerMessage(2020, LogLevel.Information, "Built local-files Soulseek index with {FileCount} files")]
    internal static partial void LocalIndexCompleted(ILogger logger, int fileCount);

    [LoggerMessage(2021, LogLevel.Debug, "Starting Soulseek login with a random account")]
    internal static partial void RandomLoginStarting(ILogger logger);

    [LoggerMessage(2022, LogLevel.Debug, "Ignored Soulseek search lock disposal failure after successful completion")]
    internal static partial void SearchDisposalFailureIgnored(ILogger logger);
}
