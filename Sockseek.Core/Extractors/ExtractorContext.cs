using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Extractors;

public sealed class ExtractorContext
{
    public static ExtractorContext None { get; } = new(new FallbackExtractorJobLog());

    public IJobLog Log { get; }

    private ExtractorContext(IJobLog log)
    {
        Log = log;
    }

    public static ExtractorContext ForExtractJob(ExtractJob job, EngineEvents events, string source)
        => new(new EventExtractorJobLog(job, events, source));
}

public interface IJobLog
{
    void Trace(string message);
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

internal sealed class EventExtractorJobLog(Job job, EngineEvents events, string source) : IJobLog
{
    public void Trace(string message) => Log(LogLevel.Trace, message);
    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Information, message);
    public void Warn(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);

    private void Log(LogLevel level, string message)
        => events.RaiseJobMessage(job, level, source, message);
}

internal sealed class FallbackExtractorJobLog : IJobLog
{
    public void Trace(string message) => SockseekLog.Jobs.Trace(message);
    public void Debug(string message) => SockseekLog.Jobs.Debug(message);
    public void Info(string message) => SockseekLog.Jobs.Info(message);
    public void Warn(string message) => SockseekLog.Jobs.Warn(message);
    public void Error(string message) => SockseekLog.Jobs.Error(message);
}
