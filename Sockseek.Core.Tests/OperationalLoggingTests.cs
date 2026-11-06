using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Services;

namespace Tests.Core;

[TestClass]
public sealed class OperationalLoggingTests
{
    [TestMethod]
    public void OperationScope_EmitsOneStructuredTerminalOutcome()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(provider));
        ILogger logger = factory.CreateLogger("test");

        using (OperationLogScope operation = OperationLogScope.Start(
            logger, "peer-browse", "browse-1"))
        {
            operation.Succeeded("complete", itemCount: 12, byteCount: 34);
        }

        Assert.AreEqual(2, provider.Entries.Count);
        Assert.AreEqual(SockseekEventIds.OperationStarted, provider.Entries[0].EventId.Id);
        CapturedLog terminal = provider.Entries[1];
        Assert.AreEqual(SockseekEventIds.OperationSucceeded, terminal.EventId.Id);
        Assert.AreEqual("complete", terminal.Properties["Outcome"]);
        Assert.AreEqual(12L, terminal.Properties["ItemCount"]);
        Assert.AreEqual(34L, terminal.Properties["ByteCount"]);
        Assert.AreEqual(1, provider.Entries.Count(entry =>
            entry.EventId.Id is >= SockseekEventIds.OperationSucceeded
                and <= SockseekEventIds.OperationAbandoned));
    }

    [TestMethod]
    public void OperationScope_FailureRetainsExceptionAndRejectsSecondTerminalOutcome()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        ILogger logger = factory.CreateLogger("test");
        var exception = new InvalidOperationException("broken");

        using (OperationLogScope operation = OperationLogScope.Start(logger, "scan", "scan-1"))
        {
            operation.Failed(exception, "storage-failed");
            Assert.ThrowsExactly<InvalidOperationException>(() => operation.Succeeded());
        }

        CapturedLog failure = provider.Entries.Single(entry =>
            entry.EventId.Id == SockseekEventIds.OperationFailed);
        Assert.AreSame(exception, failure.Exception);
        Assert.AreEqual("storage-failed", failure.Properties["Outcome"]);
    }

    [TestMethod]
    public void OperationScope_DisposeWithoutOutcomeIsVisible()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));

        using (OperationLogScope.Start(factory.CreateLogger("test"), "upload", "transfer-1"))
        {
        }

        Assert.AreEqual(1, provider.Entries.Count(entry =>
            entry.EventId.Id == SockseekEventIds.OperationAbandoned));
    }

    [TestMethod]
    public void FeatureHealthLogger_EmitsOnlyChangedStateOrReason()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var health = new FeatureHealthLogger(factory.CreateLogger("test"), "chat");

        health.Observe("Starting", "Connecting");
        health.Observe("Starting", "Connecting");
        health.Observe("Ready", "Connected");
        health.Observe("Ready", "Connected");
        health.Observe("Degraded", "PersistenceUnavailable");

        CollectionAssert.AreEqual(
            new[]
            {
                SockseekEventIds.FeatureStarting,
                SockseekEventIds.FeatureReady,
                SockseekEventIds.FeatureDegraded,
            },
            provider.Entries.Select(entry => entry.EventId.Id).ToArray());
    }

    [TestMethod]
    public void RepeatedWarningGate_ReportsSuppressedCountAfterInterval()
    {
        var clock = new ManualTimeProvider();
        var gate = new RepeatedWarningGate(clock, TimeSpan.FromMinutes(1));

        Assert.IsTrue(gate.TryAcquire(out long first));
        Assert.AreEqual(0, first);
        Assert.IsFalse(gate.TryAcquire(out _));
        Assert.IsFalse(gate.TryAcquire(out _));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue(gate.TryAcquire(out long suppressed));
        Assert.AreEqual(2, suppressed);
    }

    [TestMethod]
    public void LoginStart_OnlyShowsRandomAccountFlagForRandomLogin()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        ILogger logger = factory.CreateLogger("Sockseek.Core.Soulseek.SoulseekClientManager");

        SoulseekLogMessages.LoginStarting(logger);
        SoulseekLogMessages.RandomLoginStarting(logger);
        SoulseekLogMessages.LoginCompleted(logger);

        Assert.AreEqual("Starting Soulseek login", provider.Entries[0].Message);
        Assert.AreEqual(
            "Starting Soulseek login (random account: True)",
            provider.Entries[1].Message);
        Assert.IsFalse(provider.Entries.Any(entry =>
            entry.Message.Contains("random account: False", StringComparison.Ordinal)));
        Assert.AreEqual(LogLevel.Information, provider.Entries[2].Level);
        Assert.AreEqual("Soulseek login completed", provider.Entries[2].Message);
    }

    [TestMethod]
    public void UnobservedTaskException_IsObservedAndClassifiedByFailureKind()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(provider));
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(
            new TimeoutException("peer timeout"),
            new IOException(
                "transport stopped",
                new SocketException((int)SocketError.OperationAborted))));

        ProcessExceptionObserver.ObserveUnobservedTaskException(
            factory.CreateLogger("test"),
            args);

        Assert.IsTrue(args.Observed);
        CapturedLog entry = provider.Entries.Single();
        Assert.AreEqual(1022, entry.EventId.Id);
        Assert.AreEqual(LogLevel.Trace, entry.Level);
        Assert.AreSame(args.Exception, entry.Exception);

        var unknown = new UnobservedTaskExceptionEventArgs(new AggregateException(
            new InvalidOperationException("engine invariant broke")));

        ProcessExceptionObserver.ObserveUnobservedTaskException(
            factory.CreateLogger("test"),
            unknown);

        Assert.IsTrue(unknown.Observed);
        CapturedLog unknownEntry = provider.Entries[1];
        Assert.AreEqual(1021, unknownEntry.EventId.Id);
        Assert.AreEqual(LogLevel.Error, unknownEntry.Level);
        Assert.AreSame(unknown.Exception, unknownEntry.Exception);
    }

    [TestMethod]
    public void PeerNoiseIncludesOnlyNeverThrownDisposedSoulseekConnections()
    {
        Assert.IsTrue(ProcessExceptionObserver.IsExpectedSoulseekPeerNetworkNoise(new AggregateException(
            new ObjectDisposedException("Connection"), new ObjectDisposedException("MessageConnection"))));
        Assert.IsFalse(ProcessExceptionObserver.IsExpectedSoulseekPeerNetworkNoise(new ObjectDisposedException("FileStream")));
        try
        {
            throw new ObjectDisposedException("Connection");
        }
        catch (ObjectDisposedException thrown)
        {
            Assert.IsFalse(ProcessExceptionObserver.IsExpectedSoulseekPeerNetworkNoise(thrown));
        }
    }

    [TestMethod]
    public void PreviouslyReportedConnectionFailureDoesNotHideUnknownSiblingFailures()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var connection = new SoulseekConnectionUnavailableException(
            "Soulseek login failed", new InvalidOperationException("Missing credentials"));
        var expected = new UnobservedTaskExceptionEventArgs(new AggregateException(
            connection, new SocketException((int)SocketError.ConnectionRefused)));
        ProcessExceptionObserver.ObserveUnobservedTaskException(factory.CreateLogger("test"), expected);
        Assert.IsTrue(expected.Observed);
        Assert.AreEqual(LogLevel.Trace, provider.Entries.Single().Level);
        var mixed = new UnobservedTaskExceptionEventArgs(new AggregateException(
            connection, new InvalidOperationException("engine invariant broke")));
        ProcessExceptionObserver.ObserveUnobservedTaskException(factory.CreateLogger("test"), mixed);
        Assert.IsTrue(mixed.Observed);
        Assert.AreEqual(LogLevel.Error, provider.Entries[1].Level);
        Assert.AreSame(mixed.Exception, provider.Entries[1].Exception);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<CapturedLog> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(
        string category,
        List<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Where(pair => pair.Key != "{OriginalFormat}")
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            entries.Add(new CapturedLog(
                category, logLevel, eventId, formatter(state, exception), exception, properties));
        }
    }

    private sealed record CapturedLog(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);
}
