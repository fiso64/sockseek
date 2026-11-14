using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Extractors;

public sealed class ExtractorContext
{
    public static ExtractorContext None { get; } = new(
        NullJobLog.Instance,
        NullSensitiveOutput.Instance);

    public IJobLog Log { get; }
    public ISensitiveOutput SensitiveOutput { get; }

    private ExtractorContext(IJobLog log, ISensitiveOutput sensitiveOutput)
    {
        Log = log;
        SensitiveOutput = sensitiveOutput;
    }

    public static ExtractorContext ForExtractJob(
        ExtractJob job,
        DownloadEvents events,
        string source,
        ISensitiveOutput? sensitiveOutput = null)
        => ForJob(job, events, source, sensitiveOutput);

    public static ExtractorContext ForJob(
        Job job,
        DownloadEvents events,
        string? source = null,
        ISensitiveOutput? sensitiveOutput = null)
        => new(
            new EventExtractorJobLog(job, events, source),
            sensitiveOutput ?? NullSensitiveOutput.Instance);
}

public interface IJobLog
{
    void Trace(string message);
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

internal sealed class EventExtractorJobLog(Job job, DownloadEvents events, string? source) : IJobLog
{
    public void Trace(string message) => Log(LogLevel.Trace, message);
    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Information, message);
    public void Warn(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);

    private void Log(LogLevel level, string message)
        => events.RaiseJobMessage(job, level, source, message);
}

public interface ISensitiveOutput
{
    void WriteLine(string value);
}

public sealed class NullSensitiveOutput : ISensitiveOutput
{
    public static NullSensitiveOutput Instance { get; } = new();
    private NullSensitiveOutput() { }
    public void WriteLine(string value) { }
}

internal sealed class NullJobLog : IJobLog
{
    public static NullJobLog Instance { get; } = new();
    private NullJobLog() { }
    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}
