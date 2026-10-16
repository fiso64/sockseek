using Microsoft.Extensions.Logging;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

internal sealed class CliOutputController : IDisposable
{
    private readonly bool _forceHumanLogsToError;
    private readonly Action<CliOutputEvent>? _eventSink;
    private readonly object _liveGate = new();
    private TerminalLiveRenderer? _live;
    private bool _canUseLiveRendering;
    private volatile bool _disposed;

    private CliOutputController(
        bool forceHumanLogsToError,
        Action<CliOutputEvent>? eventSink = null)
    {
        _forceHumanLogsToError = forceHumanLogsToError;
        _eventSink = eventSink;
    }

    public bool UsesLiveRendering => _live != null;

    public bool WillUseLiveRendering => _canUseLiveRendering;

    public bool IsPaused
    {
        get => _live?.IsPaused ?? false;
        set
        {
            if (_live != null)
                _live.IsPaused = value;
            else
                Printing.SetBuffering(value);
        }
    }

    public static CliOutputController Install(IReadOnlyList<string> args)
    {
        return new CliOutputController(
            forceHumanLogsToError: ArgsRequestProgressJson(args));
    }

    public static CliOutputController CreateDetached(
        CliSettings? cliSettings = null,
        LogLevel liveLogMinimumLevel = LogLevel.Information,
        Action<CliOutputEvent>? eventSink = null)
    {
        var controller = new CliOutputController(
            forceHumanLogsToError: false,
            eventSink);

        if (cliSettings != null)
            controller.ConfigureLiveRendering(cliSettings, liveLogMinimumLevel);

        return controller;
    }

    public static bool ArgsRequestProgressJson(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--progress-json", StringComparison.OrdinalIgnoreCase))
                return i + 1 >= args.Count || !args[i + 1].Equals("false", StringComparison.OrdinalIgnoreCase);

            if (arg.StartsWith("--progress-json=", StringComparison.OrdinalIgnoreCase))
                return !arg["--progress-json=".Length..].Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool WouldUseLiveRendering(CliSettings cliSettings)
        => !cliSettings.NoProgress
            && !cliSettings.ProgressJson
            && !Console.IsOutputRedirected;

    public void ConfigureLiveRendering(CliSettings cliSettings, LogLevel minimumLevel)
    {
        lock (_liveGate)
        {
            _canUseLiveRendering = WouldUseLiveRendering(cliSettings);
            _ = minimumLevel;
        }
    }

    public void StartLiveRenderingIfNeeded()
    {
        if (!_canUseLiveRendering || _live != null || _disposed)
            return;

        lock (_liveGate)
        {
            if (_live != null || !_canUseLiveRendering || _disposed)
                return;

            _live = new TerminalLiveRenderer();
            Printing.LiveWriteLine = (line, _) => Publish(new CliOutputEvent.RawLine(line));
        }
    }

    public void Publish(CliOutputEvent outputEvent)
    {
        if (_disposed)
            return;

        _live?.Publish(outputEvent);
    }

    private void Write(CompactLogRecord record, bool includeDiagnosticDetails)
        => WriteOutput(CliOutputEvent.FromLogRecord(record, includeDiagnosticDetails));

    public ILoggerFactory CreateLoggerFactory(
        LogLevel consoleMinimumLevel,
        string? logFilePath,
        LogLevel fileMinimumLevel)
        => LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            // Provider levels are intentionally independent: a quiet console
            // must not prevent the file provider from retaining Debug records.
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new CompactTextLoggerProvider(
                record => Write(
                    record,
                    includeDiagnosticDetails: consoleMinimumLevel <= LogLevel.Debug),
                consoleMinimumLevel));
            if (!string.IsNullOrWhiteSpace(logFilePath))
                builder.AddProvider(new CompactFileLoggerProvider(logFilePath, fileMinimumLevel));
        });

    public void WriteOutput(CliOutputEvent outputEvent)
    {
        if (_disposed)
            return;

        if (_eventSink != null)
        {
            _eventSink(outputEvent);
            return;
        }

        if (_live != null)
        {
            if (outputEvent is CliOutputEvent.JobLog { Line.ShowInLive: false })
                return;
            _live.Publish(outputEvent);
            return;
        }

        CliLogStyle.WriteConsoleEvent(outputEvent, _forceHumanLogsToError);
    }

    public void ReplaceRenderState(TerminalRenderState state)
        => Publish(new CliOutputEvent.ReplaceRenderState(state));

    public void SetRateLimited(DateTimeOffset? resetsAt)
        => Publish(new CliOutputEvent.RateLimit(resetsAt));

    public void StopLiveRendering(bool printSummary = true)
    {
        lock (_liveGate)
        {
            if (_live == null)
                return;

            _live.Dispose(printSummary);
            _live = null;
            Printing.LiveWriteLine = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopLiveRendering();
    }

}
