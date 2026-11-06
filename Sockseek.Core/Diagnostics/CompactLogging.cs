using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Diagnostics;

public sealed record CompactLogRecord(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception);

public static class CompactLogFormatter
{
    public static string Format(
        CompactLogRecord record,
        bool includeTimestamp,
        bool includeInformationLevel,
        bool includeSource)
    {
        var parts = new List<string>(4);
        if (includeTimestamp)
            parts.Add(record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (includeInformationLevel || record.Level != LogLevel.Information)
            parts.Add($"[{ShortLevel(record.Level)}]");
        string category = LogicalCategory(record);
        parts.Add(includeSource
            ? $"[{category}:{Source(record.Category)}]"
            : $"[{category}]");
        parts.Add(record.Message.TrimStart());
        string line = string.Join(" ", parts);
        return record.Exception is null ? line : $"{line}: {record.Exception}";
    }

    public static string LogicalCategory(string category) => category switch
    {
        _ when category.StartsWith("Sockseek.Server", StringComparison.Ordinal) => "daemon",
        _ when category.StartsWith("Sockseek.Cli", StringComparison.Ordinal) => "cli",
        _ when category.StartsWith("Sockseek.Persistence", StringComparison.Ordinal) => "persistence",
        _ when category.StartsWith("Sockseek.Core.Soulseek", StringComparison.Ordinal) => "soulseek",
        _ when category.StartsWith("Sockseek.Core.Transfers.Downloads", StringComparison.Ordinal)
            || category.StartsWith("Sockseek.Core.Search", StringComparison.Ordinal)
            || category.StartsWith("Sockseek.Core.Extractors", StringComparison.Ordinal)
            || category.StartsWith("Sockseek.Core.Jobs", StringComparison.Ordinal) => "jobs",
        _ => "core",
    };

    public static string LogicalCategory(CompactLogRecord record)
        => record.EventId.Id switch
        {
            >= 2000 and < 3000 => "soulseek",
            >= 3000 and < 4000 => "jobs",
            >= 4050 and < 4100 => "persistence",
            >= 4000 and < 4500 => "daemon",
            >= 5000 and < 6000 => "cli",
            _ => LogicalCategory(record.Category),
        };

    public static string Source(string category)
    {
        int separator = category.LastIndexOf('.');
        string source = separator >= 0 ? category[(separator + 1)..] : category;
        int nested = source.IndexOf('`');
        return nested >= 0 ? source[..nested] : source;
    }

    public static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => level.ToString().ToLowerInvariant(),
    };
}

/// <summary>A compact standard ILogger provider for process-owned text sinks.</summary>
public sealed class CompactTextLoggerProvider(
    Action<CompactLogRecord> write,
    LogLevel minimumLevel) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
        => new CompactLogger(categoryName, write, minimumLevel);

    public void Dispose() { }

    private sealed class CompactLogger(
        string category,
        Action<CompactLogRecord> write,
        LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            write(new CompactLogRecord(
                DateTimeOffset.Now,
                logLevel,
                category,
                eventId,
                formatter(state, exception),
                exception));
        }
    }
}

public sealed class CompactFileLoggerProvider : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string path;
    private readonly CompactTextLoggerProvider inner;

    public CompactFileLoggerProvider(string path, LogLevel minimumLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(this.path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        inner = new CompactTextLoggerProvider(Write, minimumLevel);
    }

    public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);

    public void Dispose() => inner.Dispose();

    private void Write(CompactLogRecord record)
    {
        try
        {
            string line = CompactLogFormatter.Format(
                record,
                includeTimestamp: true,
                includeInformationLevel: true,
                includeSource: true);
            lock (gate)
            {
                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
