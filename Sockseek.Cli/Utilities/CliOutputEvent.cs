using Microsoft.Extensions.Logging;
using Sockseek.Core.Diagnostics;

namespace Sockseek.Cli;

internal abstract record CliOutputEvent
{
    public sealed record JobLog(
        TerminalLogLine Line,
        LogLevel Level = LogLevel.Information,
        ConsoleColor? Color = null) : CliOutputEvent;

    public sealed record ProcessLog(TerminalProcessLogLine Line) : CliOutputEvent;

    public sealed record RawLine(string Text) : CliOutputEvent;

    public sealed record ReplaceRenderState(TerminalRenderState State) : CliOutputEvent;

    public sealed record RateLimit(DateTimeOffset? ResetsAt) : CliOutputEvent;

    public static CliOutputEvent FromLogRecord(CompactLogRecord record, bool includeDiagnosticDetails)
        => new ProcessLog(new TerminalProcessLogLine(
            record.Level,
            includeDiagnosticDetails
                ? $"{CompactLogFormatter.LogicalCategory(record)}:{CompactLogFormatter.Source(record.Category)}"
                : CompactLogFormatter.LogicalCategory(record),
            record.Exception is null
                ? record.Message
                : $"{record.Message}: {(includeDiagnosticDetails ? ExceptionText.Detail(record.Exception) : ExceptionText.Summary(record.Exception))}",
            CliProcessLogPresentation.Decorated));
}

internal enum CliProcessLogPresentation
{
    Decorated,
    Plain,
}

internal sealed record TerminalProcessLogLine(
    LogLevel Level,
    string CategoryName,
    string Message,
    CliProcessLogPresentation Presentation,
    ConsoleColor? Color = null);

internal static class CliProcessOutput
{
    public static void Write(
        CliOutputController? output,
        LogLevel level,
        string message,
        string category = "cli",
        CliProcessLogPresentation presentation = CliProcessLogPresentation.Decorated)
    {
        if (output is null)
        {
            (level >= LogLevel.Error ? Console.Error : Console.Out).WriteLine(message);
            return;
        }

        output.WriteOutput(new CliOutputEvent.ProcessLog(
            new TerminalProcessLogLine(level, category, message, presentation)));
    }
}
