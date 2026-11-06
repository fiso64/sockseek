using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Sockseek.Cli;

internal static class CliLogStyle
{
    public static ConsoleColor LevelColor(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Critical => ConsoleColor.Red,
        LogLevel.Warning => ConsoleColor.DarkYellow,
        _ => ConsoleColor.Gray,
    };

    public static ConsoleColor MessageColor(LogLevel level)
        => ConsoleColor.Gray;

    public static ConsoleColor TerminalIdColor
        => ConsoleColor.DarkGray;

    public static string? MarkupLevelColor(LogLevel level) => level switch
    {
        LogLevel.Warning => "yellow",
        LogLevel.Error or LogLevel.Critical => "red",
        _ => null,
    };

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

    public static string FormatTerminalDisplayId(int displayId)
        => $"[{displayId:000}] ";

    public static string FormatTerminalLogText(TerminalLogLine line)
        => $"{FormatTerminalDisplayId(line.DisplayId)}{line.JobType}: {SourcePrefixText(line.Source)}{line.Message}";

    public static string FormatOutputEventText(CliOutputEvent outputEvent)
        => outputEvent switch
        {
            CliOutputEvent.JobLog jobLog => FormatTerminalLogText(jobLog.Line),
            CliOutputEvent.ProcessLog processLog => $"{ProcessLogPrefixText(processLog.Line)}{processLog.Line.Message}",
            CliOutputEvent.RawLine raw => raw.Text,
            _ => "",
        };

    public static void WriteConsoleEvent(CliOutputEvent outputEvent, bool forceError = false)
    {
        lock (Printing.ConsoleLock)
        {
            switch (outputEvent)
            {
                case CliOutputEvent.JobLog jobLog:
                    WriteTerminalLogLine(
                        WriterFor(jobLog.Level, forceError),
                        jobLog.Line,
                        jobLog.Color ?? MessageColor(jobLog.Level));
                    break;

                case CliOutputEvent.ProcessLog processLog:
                    WriteProcessLogLine(WriterFor(processLog.Line.Level, forceError), processLog.Line);
                    break;

                case CliOutputEvent.RawLine raw:
                    WriterFor(LogLevel.Information, forceError).WriteLine(raw.Text);
                    break;
            }
        }
    }

    public static string SourcePrefixText(string? source)
        => string.IsNullOrWhiteSpace(source) ? "" : $"{source}: ";

    public static string SourcePrefixMarkup(string? source)
        => Markup.Escape(SourcePrefixText(source));

    public static string ProcessLogPrefixText(TerminalProcessLogLine line)
    {
        if (line.Presentation == CliProcessLogPresentation.Plain)
            return "";

        var levelPrefix = line.Level == LogLevel.Information ? "" : $"[{ShortLevel(line.Level)}] ";
        return $"{levelPrefix}[{line.CategoryName}] ";
    }

    public static string ProcessLogPrefixMarkup(TerminalProcessLogLine line)
    {
        if (line.Presentation == CliProcessLogPresentation.Plain)
            return "";

        var categoryPrefix = $"[{line.CategoryName}] ";
        if (line.Level == LogLevel.Information)
            return $"[grey]{Markup.Escape(categoryPrefix)}[/]";

        var levelPrefix = $"[{ShortLevel(line.Level)}] ";
        var levelColor = MarkupLevelColor(line.Level);
        if (levelColor == null)
            return $"[grey]{Markup.Escape(ProcessLogPrefixText(line))}[/]";

        var levelMarkup = $"[{levelColor}]{Markup.Escape(levelPrefix)}[/]";

        return levelMarkup + $"[grey]{Markup.Escape(categoryPrefix)}[/]";
    }

    public static string FormatProcessLogMarkup(TerminalProcessLogLine line)
        => $"{ProcessLogPrefixMarkup(line)}{Markup.Escape(line.Message)}";

    public static string FormatTerminalLogMarkup(TerminalLogLine line)
    {
        int pathLineIdx = line.Message.IndexOf("\n    ", StringComparison.Ordinal);
        var mainPart = pathLineIdx >= 0 ? line.Message[..pathLineIdx] : line.Message;
        var pathPart = pathLineIdx >= 0 ? line.Message[pathLineIdx..] : null;

        var mainMarkup = SourcePrefixMarkup(line.Source) + FormatMainLogContentMarkup(mainPart, line.Kind, line.Highlight);

        var pathMarkup = pathPart != null ? $"[grey]{Markup.Escape(pathPart)}[/]" : "";
        return $"[grey]{Markup.Escape(FormatTerminalDisplayId(line.DisplayId))}[/]{Markup.Escape(line.JobType)}: {mainMarkup}{pathMarkup}";
    }

    public static string FormatMainLogContentMarkup(string content, TerminalLogKind kind, string? highlight)
    {
        var color = TerminalLogKindColor(kind);
        if (color == null)
            return Markup.Escape(content);

        if (!string.IsNullOrEmpty(highlight) && content.StartsWith(highlight, StringComparison.Ordinal))
            return $"[{color}]{Markup.Escape(highlight)}[/]{Markup.Escape(content[highlight.Length..])}";

        return $"[{color}]{Markup.Escape(content)}[/]";
    }

    public static string? TerminalLogKindColor(TerminalLogKind kind) => kind switch
    {
        TerminalLogKind.SongDownloaded or TerminalLogKind.AlbumTrackDownloaded
            or TerminalLogKind.JobSucceeded or TerminalLogKind.PlaylistCompleted
            or TerminalLogKind.AggregateCompleted
            => "green",
        TerminalLogKind.JobPartial
            => "yellow",
        TerminalLogKind.SongFailed or TerminalLogKind.AlbumTrackFailed
            or TerminalLogKind.JobFailed
            => "red",
        TerminalLogKind.SongSkipped or TerminalLogKind.AlbumTrackSkipped
            or TerminalLogKind.JobCancelled or TerminalLogKind.SongAlreadyExists
            or TerminalLogKind.JobAlreadyExists
            => "grey",
        _ => null,
    };

    private static TextWriter WriterFor(LogLevel level, bool forceError)
        => forceError || level >= LogLevel.Error
            ? Console.Error
            : Console.Out;

    private static void WriteProcessLogLine(TextWriter writer, TerminalProcessLogLine line)
    {
        if (line.Presentation == CliProcessLogPresentation.Plain)
        {
            WriteColoredLine(writer, line.Message, line.Color ?? MessageColor(line.Level));
            return;
        }

        if (line.Level != LogLevel.Information)
            WriteColored(writer, $"[{ShortLevel(line.Level)}] ", LevelColor(line.Level));

        WriteColored(writer, $"[{line.CategoryName}] ", ConsoleColor.DarkGray);
        WriteColoredLine(writer, line.Message, line.Color ?? MessageColor(line.Level));
    }

    private static void WriteColored(TextWriter writer, string value, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            writer.Write(value);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    private static void WriteColoredLine(TextWriter writer, string value, ConsoleColor color)
    {
        WriteColored(writer, value, color);
        writer.WriteLine();
    }

    private static void WriteTerminalLogLine(TextWriter writer, TerminalLogLine line, ConsoleColor messageColor)
    {
        WriteColored(writer, FormatTerminalDisplayId(line.DisplayId), TerminalIdColor);
        WriteColoredLine(writer, $"{line.JobType}: {SourcePrefixText(line.Source)}{line.Message}", messageColor);
    }
}
