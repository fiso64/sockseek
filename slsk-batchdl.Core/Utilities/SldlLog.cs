using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Sldl.Core;

public static class SldlLog
{
    public static class Categories
    {
        public const string Cli = "sldl.cli";
        public const string Core = "sldl.core";
        public const string Daemon = "sldl.daemon";
        public const string CliTests = "sldl.tests.cli";
        public const string CoreTests = "sldl.tests.core";
        public const string DaemonTests = "sldl.tests.daemon";
    }

    private static readonly object Sync = new();
    private static readonly List<RoutingLoggerProvider> Providers = new();
    private static ILoggerFactory Factory = BuildFactory();

    public static void SetupExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                NonConsoleCritical(exception, "Unhandled exception");
            else
                NonConsoleCritical("Unhandled exception: {ExceptionObject}", args.ExceptionObject);
        };
    }

    public static ILogger CreateLogger(string categoryName) => Factory.CreateLogger(categoryName);

    public static ILogger<T> CreateLogger<T>() => Factory.CreateLogger<T>();

    public static void AddConsole(
        LogLevel minimumLevel = LogLevel.Information,
        bool useColors = true,
        bool prependDate = false,
        bool prependLogLevel = false,
        Action<string, ConsoleColor>? writer = null)
    {
        var write = writer ?? ((message, color) =>
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        });

        AddProvider(new RoutingLoggerProvider(
            minimumLevel,
            includeConsole: true,
            includeFile: false,
            prependDate,
            prependLogLevel,
            (level, message, _) => write(message, useColors ? ColorFor(level) : ConsoleColor.Gray)));
    }

    public static void AddSink(
        Action<LogLevel, string> output,
        LogLevel minimumLevel = LogLevel.Information,
        bool prependDate = false,
        bool prependLogLevel = false)
    {
        AddProvider(new RoutingLoggerProvider(
            minimumLevel,
            includeConsole: false,
            includeFile: false,
            prependDate,
            prependLogLevel,
            (level, message, _) => output(level, message)));
    }

    public static void AddOrReplaceFile(
        string filePath,
        LogLevel minimumLevel = LogLevel.Debug,
        bool prependDate = true,
        bool prependLogLevel = true)
    {
        var directoryName = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directoryName))
            Directory.CreateDirectory(directoryName);

        lock (Sync)
        {
            Providers.RemoveAll(provider => provider.IsFileOutput);
            Providers.Add(new RoutingLoggerProvider(
                minimumLevel,
                includeConsole: false,
                includeFile: true,
                prependDate,
                prependLogLevel,
                (_, message, _) => AppendLogFile(filePath, message)));
            RebuildFactory();
        }
    }

    public static void SetConsoleLogLevel(LogLevel logLevel)
    {
        lock (Sync)
        {
            foreach (var provider in Providers.Where(provider => provider.IsConsoleOutput))
                provider.MinimumLevel = logLevel;
        }
    }

    public static void RemoveConsoleOutputs()
    {
        lock (Sync)
        {
            Providers.RemoveAll(provider => provider.IsConsoleOutput);
            RebuildFactory();
        }
    }

    public static void RemoveNonFileOutputs()
    {
        lock (Sync)
        {
            Providers.RemoveAll(provider => !provider.IsFileOutput);
            RebuildFactory();
        }
    }

    public static void RemoveFileOutputs()
    {
        lock (Sync)
        {
            Providers.RemoveAll(provider => provider.IsFileOutput);
            RebuildFactory();
        }
    }

    public static void LogNonConsole(LogLevel level, string message, string? categoryName = null, [CallerFilePath] string callerFilePath = "")
        => Log(level, message, consoleOnly: false, nonConsoleOnly: true, categoryName: categoryName, callerFilePath: callerFilePath);

    public static void LogConsoleOnly(LogLevel level, string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "")
        => Log(level, message, color, consoleOnly: true, nonConsoleOnly: false, categoryName: categoryName, callerFilePath: callerFilePath);

    public static void Trace(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Trace, message, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Debug(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Debug, message, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Info(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Information, message, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Warn(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Warning, message, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Error(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Error, message, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Fatal(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Critical, message, color, categoryName: categoryName, callerFilePath: callerFilePath);

    private static void NonConsoleCritical(Exception exception, string message)
    {
        foreach (var provider in SnapshotProviders().Where(provider => !provider.IsConsoleOutput))
            provider.Write(LogLevel.Critical, "sldl.core", $"{message}. {exception}", null);
    }

    private static void NonConsoleCritical(string message, object? arg)
    {
        foreach (var provider in SnapshotProviders().Where(provider => !provider.IsConsoleOutput))
            provider.Write(LogLevel.Critical, "sldl.core", string.Format(message.Replace("{ExceptionObject}", "{0}"), arg), null);
    }

    private static void Log(
        LogLevel level,
        string message,
        ConsoleColor? color = null,
        bool consoleOnly = false,
        bool nonConsoleOnly = false,
        string? categoryName = null,
        string callerFilePath = "")
    {
        categoryName ??= CategoryFor(callerFilePath);
        foreach (var provider in SnapshotProviders())
        {
            if (consoleOnly && !provider.IsConsoleOutput) continue;
            if (nonConsoleOnly && provider.IsConsoleOutput) continue;
            provider.Write(level, categoryName, message, color);
        }
    }

    private static string CategoryFor(string callerFilePath)
    {
        var normalized = callerFilePath.Replace('\\', '/');
        if (normalized.Contains("slsk-batchdl.Cli.Tests/", StringComparison.Ordinal)) return Categories.CliTests;
        if (normalized.Contains("slsk-batchdl.Server.Tests/", StringComparison.Ordinal)) return Categories.DaemonTests;
        if (normalized.Contains("slsk-batchdl.Core.Tests/", StringComparison.Ordinal)) return Categories.CoreTests;
        if (normalized.Contains("slsk-batchdl.Cli/", StringComparison.Ordinal)) return Categories.Cli;
        if (normalized.Contains("slsk-batchdl.Server/", StringComparison.Ordinal)) return Categories.Daemon;
        return Categories.Core;
    }

    private static void AddProvider(RoutingLoggerProvider provider)
    {
        lock (Sync)
        {
            Providers.Add(provider);
            RebuildFactory();
        }
    }

    private static IReadOnlyList<RoutingLoggerProvider> SnapshotProviders()
    {
        lock (Sync)
            return Providers.ToList();
    }

    private static ILoggerFactory BuildFactory()
        => LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            foreach (var provider in Providers)
                builder.AddProvider(provider);
        });

    private static void RebuildFactory()
    {
        var oldFactory = Factory;
        Factory = BuildFactory();
        oldFactory.Dispose();
    }

    private static readonly object FileSync = new();

    private static void AppendLogFile(string filePath, string message)
    {
        try
        {
            lock (FileSync)
            {
                using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(message);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ConsoleColor ColorFor(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Critical => ConsoleColor.Red,
        LogLevel.Warning => ConsoleColor.DarkYellow,
        _ => ConsoleColor.Gray,
    };

    private sealed class RoutingLoggerProvider : ILoggerProvider
    {
        private readonly bool _prependDate;
        private readonly bool _prependLogLevel;
        private readonly Action<LogLevel, string, ConsoleColor?> _write;

        public RoutingLoggerProvider(
            LogLevel minimumLevel,
            bool includeConsole,
            bool includeFile,
            bool prependDate,
            bool prependLogLevel,
            Action<LogLevel, string, ConsoleColor?> write)
        {
            MinimumLevel = minimumLevel;
            IsConsoleOutput = includeConsole;
            IsFileOutput = includeFile;
            _prependDate = prependDate;
            _prependLogLevel = prependLogLevel;
            _write = write;
        }

        public LogLevel MinimumLevel { get; set; }
        public bool IsConsoleOutput { get; }
        public bool IsFileOutput { get; }

        public ILogger CreateLogger(string categoryName) => new RoutingLogger(this, categoryName);

        public void Dispose()
        {
        }

        public void Write(LogLevel level, string categoryName, string message, ConsoleColor? color)
        {
            if (level < MinimumLevel) return;

            try
            {
                _write(level, Format(level, categoryName, message), color);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string Format(LogLevel level, string categoryName, string message)
        {
            var parts = new List<string>();

            if (_prependDate)
                parts.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            if (_prependLogLevel)
                parts.Add($"[{ShortLevel(level)}]");

            if (!IsConsoleOutput)
                parts.Add($"[{categoryName}]");

            parts.Add(IsConsoleOutput ? message : message.TrimStart());
            return string.Join(" ", parts);
        }

        private static string ShortLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            _ => level.ToString().ToLowerInvariant(),
        };

        private sealed class RoutingLogger : ILogger
        {
            private readonly RoutingLoggerProvider _provider;
            private readonly string _categoryName;

            public RoutingLogger(RoutingLoggerProvider provider, string categoryName)
            {
                _provider = provider;
                _categoryName = categoryName;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.MinimumLevel;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var message = formatter(state, exception);
                if (exception != null)
                    message = string.IsNullOrWhiteSpace(message) ? exception.ToString() : $"{message}: {exception}";
                _provider.Write(logLevel, _categoryName, message, null);
            }
        }
    }
}
