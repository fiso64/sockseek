using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Diagnostics;

public static class LogIdentity
{
    public static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..12]
            .ToLowerInvariant();
    }

    public static string PeerHash(string username) => Hash(username);
}

public static class ExceptionText
{
    public static string Summary(Exception exception)
        => exception.InnerException?.Message
            ?? (string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message);

    public static string Detail(Exception exception) => exception.ToString();
}

public static class SockseekEventIds
{
    public const int OperationStarted = 1000;
    public const int OperationSucceeded = 1001;
    public const int OperationDegraded = 1002;
    public const int OperationCancelled = 1003;
    public const int OperationFailed = 1004;
    public const int OperationAbandoned = 1005;
    public const int FeatureStarting = 1010;
    public const int FeatureReady = 1011;
    public const int FeatureDegraded = 1012;
    public const int FeatureDisabled = 1013;
}

/// <summary>
/// Logs one bounded daemon-owned operation and enforces exactly one explicit
/// terminal outcome. Correlation values must already be safe IDs or hashes.
/// </summary>
public sealed class OperationLogScope : IDisposable
{
    private readonly ILogger logger;
    private readonly string operation;
    private readonly string correlationId;
    private readonly long startedAt;
    private int terminal;

    private OperationLogScope(ILogger logger, string operation, string correlationId)
    {
        this.logger = logger;
        this.operation = operation;
        this.correlationId = correlationId;
        startedAt = Stopwatch.GetTimestamp();
        OperationalLogMessages.OperationStarted(logger, operation, correlationId);
    }

    public static OperationLogScope Start(
        ILogger logger,
        string operation,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new OperationLogScope(logger, operation, correlationId);
    }

    public void Succeeded(
        string outcome = "succeeded",
        long? itemCount = null,
        long? byteCount = null)
    {
        ClaimTerminal();
        OperationalLogMessages.OperationSucceeded(
            logger, operation, correlationId, outcome, ElapsedMilliseconds(), itemCount, byteCount);
    }

    public void Degraded(
        string outcome,
        long? itemCount = null,
        long? byteCount = null)
    {
        ClaimTerminal();
        OperationalLogMessages.OperationDegraded(
            logger, operation, correlationId, outcome, ElapsedMilliseconds(), itemCount, byteCount);
    }

    public void Cancelled(string outcome = "cancelled")
    {
        ClaimTerminal();
        OperationalLogMessages.OperationCancelled(
            logger, operation, correlationId, outcome, ElapsedMilliseconds());
    }

    public void Failed(Exception exception, string outcome = "failed")
    {
        ArgumentNullException.ThrowIfNull(exception);
        ClaimTerminal();
        OperationalLogMessages.OperationFailed(
            logger, exception, operation, correlationId, outcome, ElapsedMilliseconds());
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref terminal, 1, 0) == 0)
        {
            OperationalLogMessages.OperationAbandoned(
                logger, operation, correlationId, ElapsedMilliseconds());
        }
    }

    private void ClaimTerminal()
    {
        if (Interlocked.CompareExchange(ref terminal, 1, 0) != 0)
            throw new InvalidOperationException(
                $"Logging operation '{operation}' already has a terminal outcome.");
    }

    private long ElapsedMilliseconds()
        => (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}

/// <summary>Emits feature health only when its state or stable reason changes.</summary>
public sealed class FeatureHealthLogger(ILogger logger, string feature)
{
    private readonly object gate = new();
    private string? lastState;
    private string? lastReason;

    public void Observe(string state, string reason, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            if (string.Equals(lastState, state, StringComparison.Ordinal)
                && string.Equals(lastReason, reason, StringComparison.Ordinal))
                return;
            lastState = state;
            lastReason = reason;
        }

        switch (state)
        {
            case "Ready":
                OperationalLogMessages.FeatureReady(logger, feature, reason);
                break;
            case "Degraded":
                OperationalLogMessages.FeatureDegraded(logger, exception, feature, reason);
                break;
            case "Disabled":
                OperationalLogMessages.FeatureDisabled(logger, feature, reason);
                break;
            default:
                OperationalLogMessages.FeatureStarting(logger, feature, state, reason);
                break;
        }
    }
}

/// <summary>Bounds repeated warnings without creating an unbounded key cache.</summary>
public sealed class RepeatedWarningGate(
    TimeProvider? timeProvider = null,
    TimeSpan? interval = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan minimumInterval = interval ?? TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private long lastTimestamp = long.MinValue;
    private long suppressed;

    public bool TryAcquire(out long suppressedSinceLastEmission)
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            if (lastTimestamp != long.MinValue
                && clock.GetElapsedTime(lastTimestamp, now) < minimumInterval)
            {
                suppressed++;
                suppressedSinceLastEmission = 0;
                return false;
            }

            lastTimestamp = now;
            suppressedSinceLastEmission = suppressed;
            suppressed = 0;
            return true;
        }
    }
}

public sealed class ProcessExceptionObserver : IDisposable
{
    private readonly ILogger logger;
    private int disposed;

    private ProcessExceptionObserver(ILogger logger)
    {
        this.logger = logger;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static ProcessExceptionObserver Install(ILogger logger)
        => new(logger ?? throw new ArgumentNullException(nameof(logger)));

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled non-exception object: {args.ExceptionObject}");
        OperationalLogMessages.UnhandledException(logger, exception, args.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        ObserveUnobservedTaskException(logger, args);
    }

    internal static void ObserveUnobservedTaskException(
        ILogger logger,
        UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        if (IsExpectedSoulseekPeerNetworkNoise(args.Exception))
            OperationalLogMessages.ExpectedPeerNetworkTaskException(logger, args.Exception);
        else
            OperationalLogMessages.UnobservedTaskException(logger, args.Exception);
    }

    internal static bool IsExpectedSoulseekPeerNetworkNoise(Exception exception)
    {
        IReadOnlyCollection<Exception> flattened = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];
        return flattened.Count > 0 && flattened.All(IsExpectedSoulseekPeerNetworkException);
    }

    private static bool IsExpectedSoulseekPeerNetworkException(Exception exception)
    {
        if (exception is TimeoutException or OperationCanceledException
            or Services.SoulseekConnectionUnavailableException)
            return true;
        // Soulseek.NET creates never-thrown faults for disposed connection
        // disconnect tasks. Thrown/disposed application objects remain errors.
        if (exception is ObjectDisposedException
            { ObjectName: "Connection" or "MessageConnection", StackTrace: null })
            return true;
        if (exception is SocketException socketException)
        {
            return socketException.SocketErrorCode is SocketError.OperationAborted
                or SocketError.TimedOut
                or SocketError.ConnectionAborted
                or SocketError.ConnectionRefused
                or SocketError.ConnectionReset
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable;
        }
        if (exception is IOException && exception.InnerException is { } ioInner)
            return IsExpectedSoulseekPeerNetworkException(ioInner);
        if (IsSoulseekNetworkException(exception))
        {
            return exception.InnerException is null
                || IsExpectedSoulseekPeerNetworkException(exception.InnerException);
        }
        return IsSoulseekNetworkStackException(exception);
    }

    private static bool IsSoulseekNetworkException(Exception exception)
    {
        string typeName = exception.GetType().FullName ?? exception.GetType().Name;
        if (!typeName.StartsWith("Soulseek.", StringComparison.Ordinal))
            return false;
        return exception.GetType().Name is "ConnectionReadException" or "ConnectionException"
            || exception.Message.Contains("Failed to read", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains(
                "Transfer failed: Transfer complete",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSoulseekNetworkStackException(Exception exception)
    {
        if (exception.TargetSite?.DeclaringType?.FullName?.StartsWith(
                "Soulseek.Network.",
                StringComparison.Ordinal) == true)
        {
            return true;
        }
        return exception.StackTrace?.Contains(
            "at Soulseek.Network.",
            StringComparison.Ordinal) == true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }
}

internal static partial class OperationalLogMessages
{
    [LoggerMessage(1020, LogLevel.Critical, "Unhandled process exception (terminating: {IsTerminating})")]
    public static partial void UnhandledException(
        ILogger logger,
        Exception exception,
        bool isTerminating);

    [LoggerMessage(1021, LogLevel.Error, "Unobserved task exception")]
    public static partial void UnobservedTaskException(
        ILogger logger,
        Exception exception);

    [LoggerMessage(1022, LogLevel.Trace, "Ignored expected Soulseek peer-network task exception")]
    public static partial void ExpectedPeerNetworkTaskException(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = SockseekEventIds.OperationStarted,
        EventName = "daemon.operation.started",
        Level = LogLevel.Information,
        Message = "{Operation} {CorrelationId} started")]
    public static partial void OperationStarted(
        ILogger logger, string operation, string correlationId);

    [LoggerMessage(
        EventId = SockseekEventIds.OperationSucceeded,
        EventName = "daemon.operation.succeeded",
        Level = LogLevel.Information,
        Message = "{Operation} {CorrelationId} ended as {Outcome} in {DurationMs} ms (items={ItemCount}, bytes={ByteCount})")]
    public static partial void OperationSucceeded(
        ILogger logger, string operation, string correlationId, string outcome,
        long durationMs, long? itemCount, long? byteCount);

    [LoggerMessage(
        EventId = SockseekEventIds.OperationDegraded,
        EventName = "daemon.operation.degraded",
        Level = LogLevel.Warning,
        Message = "{Operation} {CorrelationId} ended as {Outcome} in {DurationMs} ms (items={ItemCount}, bytes={ByteCount})")]
    public static partial void OperationDegraded(
        ILogger logger, string operation, string correlationId, string outcome,
        long durationMs, long? itemCount, long? byteCount);

    [LoggerMessage(
        EventId = SockseekEventIds.OperationCancelled,
        EventName = "daemon.operation.cancelled",
        Level = LogLevel.Information,
        Message = "{Operation} {CorrelationId} ended as {Outcome} in {DurationMs} ms")]
    public static partial void OperationCancelled(
        ILogger logger, string operation, string correlationId, string outcome,
        long durationMs);

    [LoggerMessage(
        EventId = SockseekEventIds.OperationFailed,
        EventName = "daemon.operation.failed",
        Level = LogLevel.Error,
        Message = "{Operation} {CorrelationId} ended as {Outcome} in {DurationMs} ms")]
    public static partial void OperationFailed(
        ILogger logger, Exception exception, string operation, string correlationId,
        string outcome, long durationMs);

    [LoggerMessage(
        EventId = SockseekEventIds.OperationAbandoned,
        EventName = "daemon.operation.abandoned",
        Level = LogLevel.Error,
        Message = "{Operation} {CorrelationId} was disposed without a terminal outcome after {DurationMs} ms")]
    public static partial void OperationAbandoned(
        ILogger logger, string operation, string correlationId, long durationMs);

    [LoggerMessage(
        EventId = SockseekEventIds.FeatureStarting,
        EventName = "daemon.feature.starting",
        Level = LogLevel.Information,
        Message = "{Feature} health changed to {State} ({Reason})")]
    public static partial void FeatureStarting(
        ILogger logger, string feature, string state, string reason);

    [LoggerMessage(
        EventId = SockseekEventIds.FeatureReady,
        EventName = "daemon.feature.ready",
        Level = LogLevel.Information,
        Message = "{Feature} health changed to Ready ({Reason})")]
    public static partial void FeatureReady(ILogger logger, string feature, string reason);

    [LoggerMessage(
        EventId = SockseekEventIds.FeatureDegraded,
        EventName = "daemon.feature.degraded",
        Level = LogLevel.Warning,
        Message = "{Feature} health changed to Degraded ({Reason})")]
    public static partial void FeatureDegraded(
        ILogger logger, Exception? exception, string feature, string reason);

    [LoggerMessage(
        EventId = SockseekEventIds.FeatureDisabled,
        EventName = "daemon.feature.disabled",
        Level = LogLevel.Information,
        Message = "{Feature} health changed to Disabled ({Reason})")]
    public static partial void FeatureDisabled(ILogger logger, string feature, string reason);
}
