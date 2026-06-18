using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Sockseek.Core;

public static class SockseekLog
{
    public enum LogRouting
    {
        All,
        ConsoleOnly,
    }

    public sealed record StructuredLogEntry(
        LogLevel Level,
        string CategoryName,
        string Message,
        ConsoleColor? Color = null,
        LogRouting Routing = LogRouting.All,
        object? Context = null);

    public static class Categories
    {
        public const string Cli = "cli";
        public const string Core = "core";
        public const string Daemon = "daemon";
        public const string Jobs = "jobs";
        public const string Soulseek = "soulseek";
        public const string CliTests = "tests.cli";
        public const string CoreTests = "tests.core";
        public const string DaemonTests = "tests.daemon";
    }

    public static LogChannel Cli { get; } = new(Categories.Cli);
    public static LogChannel Core { get; } = new(Categories.Core);
    public static LogChannel Daemon { get; } = new(Categories.Daemon);
    public static LogChannel Jobs { get; } = new(Categories.Jobs);
    public static LogChannel Soulseek { get; } = new(Categories.Soulseek);

    private static readonly object Sync = new();
    private static readonly List<RoutingLoggerProvider> Providers = new();
    private static ILoggerFactory Factory = BuildFactory();
    private static bool ExceptionHandlingConfigured;

    public static void SetupExceptionHandling()
    {
        lock (Sync)
        {
            if (ExceptionHandlingConfigured)
                return;

            ExceptionHandlingConfigured = true;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                Critical(exception, "Unhandled exception");
            else
                Critical($"Unhandled exception: {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) => HandleUnobservedTaskException(args);
    }

    public static ILogger CreateLogger(string categoryName) => Factory.CreateLogger(categoryName);

    public static ILogger<T> CreateLogger<T>() => Factory.CreateLogger<T>();

    public sealed class LogChannel(string categoryName)
    {
        public string CategoryName { get; } = categoryName;

        public void Trace(string message, ConsoleColor? color = null) => SockseekLog.Trace(message, color, CategoryName);
        public void Debug(string message, ConsoleColor? color = null) => SockseekLog.Debug(message, color, CategoryName);
        public void Info(string message, ConsoleColor? color = null) => SockseekLog.Info(message, color, CategoryName);
        public void Warn(string message, ConsoleColor? color = null) => SockseekLog.Warn(message, color, CategoryName);
        public void Error(string message, ConsoleColor? color = null) => SockseekLog.Error(message, color, CategoryName);
        public void Fatal(string message, ConsoleColor? color = null) => SockseekLog.Fatal(message, color, CategoryName);
        public void Error(Exception exception, string message, ConsoleColor? color = null) => SockseekLog.Error(exception, message, color, CategoryName);
        public void Fatal(Exception exception, string message, ConsoleColor? color = null) => SockseekLog.Fatal(exception, message, color, CategoryName);
        public void Write(LogLevel level, string message, ConsoleColor? color = null) => SockseekLog.Write(level, message, color, CategoryName);
        public void LogConsoleOnly(LogLevel level, string message, ConsoleColor? color = null) => SockseekLog.LogConsoleOnly(level, message, color, CategoryName);
    }

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
            (entry, message) => write(message, useColors ? entry.Color ?? ColorFor(entry.Level) : ConsoleColor.Gray)));
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
            (entry, message) => output(entry.Level, message)));
    }

    public static void AddStructuredSink(
        Action<StructuredLogEntry, string> output,
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
            output));
    }

    public static void AddStructuredConsoleSink(
        Action<StructuredLogEntry, string> output,
        LogLevel minimumLevel = LogLevel.Information,
        bool prependDate = false,
        bool prependLogLevel = false)
    {
        AddProvider(new RoutingLoggerProvider(
            minimumLevel,
            includeConsole: true,
            includeFile: false,
            prependDate,
            prependLogLevel,
            output));
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
                (_, message) => AppendLogFile(filePath, message)));
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

    public static void LogConsoleOnly(LogLevel level, string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "")
        => Log(level, message, LogRouting.ConsoleOnly, color, categoryName: categoryName, callerFilePath: callerFilePath);

    public static void Write(LogLevel level, string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "")
        => Log(level, message, LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);

    public static void Write(StructuredLogEntry entry)
    {
        foreach (var provider in SnapshotProviders())
            provider.Write(entry);
    }

    public static void Trace(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Trace, message, LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Debug(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Debug, message, LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Info(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Information, message, LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Warn(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Warning, message, LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Error(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Error, message, LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Fatal(string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "") => Log(LogLevel.Critical, message, LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Error(Exception exception, string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "")
        => Log(LogLevel.Error, FormatException(message, exception), LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);
    public static void Fatal(Exception exception, string message, ConsoleColor? color = null, string? categoryName = null, [CallerFilePath] string callerFilePath = "")
        => Log(LogLevel.Critical, FormatException(message, exception), LogRouting.All, color, categoryName: categoryName, callerFilePath: callerFilePath);

    public static string ExceptionSummary(Exception exception)
        => exception.InnerException?.Message
            ?? (string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message);

    public static string ExceptionDetail(Exception exception) => exception.ToString();

    public static string FormatException(string message, Exception exception)
        => $"{message}: {ExceptionDetail(exception)}";

    private static void Critical(Exception exception, string message)
        => Critical(FormatException(message, exception));

    private static void Critical(string message)
        => Log(LogLevel.Critical, message, categoryName: Categories.Core);

    private static void HandleUnobservedTaskException(UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();

        if (IsExpectedSoulseekPeerNetworkNoise(args.Exception))
        {
            Trace(
                $"Ignored unobserved Soulseek peer-network task exception: {ExceptionSummary(args.Exception)}",
                categoryName: Categories.Core);
            return;
        }

        Error(args.Exception, "Unobserved task exception", categoryName: Categories.Core);
    }

    internal static bool IsExpectedSoulseekPeerNetworkNoise(Exception exception)
    {
        var flattened = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];

        return flattened.Count > 0 && flattened.All(IsExpectedSoulseekPeerNetworkException);
    }

    private static bool IsExpectedSoulseekPeerNetworkException(Exception exception)
    {
        if (exception is TimeoutException or OperationCanceledException)
            return true;

        if (exception is SocketException socketException)
            return socketException.SocketErrorCode is SocketError.OperationAborted
                or SocketError.TimedOut
                or SocketError.ConnectionAborted
                or SocketError.ConnectionRefused
                or SocketError.ConnectionReset
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable;

        if (exception is IOException && exception.InnerException is { } ioInner)
            return IsExpectedSoulseekPeerNetworkException(ioInner);

        if (IsSoulseekNetworkException(exception))
            return exception.InnerException == null || IsExpectedSoulseekPeerNetworkException(exception.InnerException);

        return false;
    }

    private static bool IsSoulseekNetworkException(Exception exception)
    {
        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        if (!typeName.StartsWith("Soulseek.", StringComparison.Ordinal))
            return false;

        return exception.GetType().Name is "ConnectionReadException" or "ConnectionException"
            || exception.Message.Contains("Failed to read", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Transfer failed: Transfer complete", StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(
        LogLevel level,
        string message,
        LogRouting routing = LogRouting.All,
        ConsoleColor? color = null,
        string? categoryName = null,
        string callerFilePath = "")
    {
        categoryName ??= CategoryFor(callerFilePath);
        var entry = new StructuredLogEntry(level, categoryName, message, color, routing);
        Write(entry);
    }

    private static string CategoryFor(string callerFilePath)
    {
        var normalized = callerFilePath.Replace('\\', '/');
        if (normalized.Contains("Sockseek.Cli.Tests/", StringComparison.Ordinal)) return Categories.CliTests;
        if (normalized.Contains("Sockseek.Server.Tests/", StringComparison.Ordinal)) return Categories.DaemonTests;
        if (normalized.Contains("Sockseek.Core.Tests/", StringComparison.Ordinal)) return Categories.CoreTests;
        if (normalized.Contains("Sockseek.Cli/", StringComparison.Ordinal)) return Categories.Cli;
        if (normalized.Contains("Sockseek.Server/", StringComparison.Ordinal)) return Categories.Daemon;
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
        private readonly Action<StructuredLogEntry, string> _write;

        public RoutingLoggerProvider(
            LogLevel minimumLevel,
            bool includeConsole,
            bool includeFile,
            bool prependDate,
            bool prependLogLevel,
            Action<StructuredLogEntry, string> write)
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

        public void Write(StructuredLogEntry entry)
        {
            if (entry.Level < MinimumLevel) return;
            if (entry.Routing == LogRouting.ConsoleOnly && !IsConsoleOutput) return;

            try
            {
                _write(entry, Format(entry));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string Format(StructuredLogEntry entry)
        {
            if (entry.Routing == LogRouting.ConsoleOnly && IsConsoleOutput)
                return entry.Message;

            var parts = new List<string>();

            if (_prependDate)
                parts.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            if (_prependLogLevel || (IsConsoleOutput && entry.Level != LogLevel.Information))
                parts.Add($"[{ShortLevel(entry.Level)}]");

            parts.Add($"[{entry.CategoryName}]");

            parts.Add(IsConsoleOutput ? entry.Message : entry.Message.TrimStart());
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
                _provider.Write(new StructuredLogEntry(logLevel, _categoryName, message));
            }
        }
    }
}
