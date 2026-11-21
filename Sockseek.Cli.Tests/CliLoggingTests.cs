using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public sealed class CliLoggingTests
{
    [TestMethod]
    public void LoggerFactory_InformationConsoleSummarizesExceptionWhileFileKeepsDetail()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-cli-logging-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");
        var outputEvents = new List<CliOutputEvent>();

        try
        {
            using var output = CliOutputController.CreateDetached(eventSink: outputEvents.Add);
            using ILoggerFactory factory = output.CreateLoggerFactory(
                LogLevel.Information,
                path,
                LogLevel.Debug);
            ILogger logger = factory.CreateLogger("Sockseek.Cli.TestComponent");
            Exception exception = CreateTestException();

            logger.LogError(exception, "operation failed");

            Assert.AreEqual(1, outputEvents.Count, "One log record must produce one console event.");
            string console = CliLogStyle.FormatOutputEventText(outputEvents[0]);
            StringAssert.Contains(console, "operation failed: concise failure");
            Assert.IsFalse(console.Contains(nameof(CreateTestException), StringComparison.Ordinal));
            Assert.IsFalse(console.Contains("InvalidOperationException", StringComparison.Ordinal));

            string file = File.ReadAllText(path);
            StringAssert.Contains(file, "operation failed");
            StringAssert.Contains(file, "InvalidOperationException: concise failure");
            StringAssert.Contains(file, nameof(CreateTestException));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void LoggerFactory_DebugConsoleIncludesFullExceptionDetail()
    {
        var outputEvents = new List<CliOutputEvent>();
        using var output = CliOutputController.CreateDetached(eventSink: outputEvents.Add);
        using ILoggerFactory factory = output.CreateLoggerFactory(
            LogLevel.Debug,
            logFilePath: null,
            fileMinimumLevel: LogLevel.Debug);
        ILogger logger = factory.CreateLogger("Sockseek.Cli.TestComponent");

        logger.LogError(CreateTestException(), "operation failed");

        Assert.AreEqual(1, outputEvents.Count);
        string console = CliLogStyle.FormatOutputEventText(outputEvents[0]);
        StringAssert.Contains(console, "InvalidOperationException: concise failure");
        StringAssert.Contains(console, nameof(CreateTestException));
    }

    [TestMethod]
    public void PresentationOutput_DoesNotEnterDiagnosticLogFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-cli-logging-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");
        var outputEvents = new List<CliOutputEvent>();

        try
        {
            using var output = CliOutputController.CreateDetached(eventSink: outputEvents.Add);
            using ILoggerFactory factory = output.CreateLoggerFactory(
                LogLevel.Information,
                path,
                LogLevel.Debug);

            CliProcessOutput.Write(output, LogLevel.Error, "private job-shaped activity");

            Assert.AreEqual(1, outputEvents.Count);
            Assert.IsFalse(File.Exists(path),
                "Presentation activity must not silently cross into diagnostic logging.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void LoggerFactory_DebugConsoleIncludesSource()
    {
        var outputEvents = new List<CliOutputEvent>();
        using var output = CliOutputController.CreateDetached(eventSink: outputEvents.Add);
        using ILoggerFactory factory = output.CreateLoggerFactory(
            LogLevel.Debug,
            logFilePath: null,
            fileMinimumLevel: LogLevel.Debug);
        ILogger logger = factory.CreateLogger("Sockseek.Cli.TestComponent");

        logger.LogInformation("visible lifecycle");

        Assert.AreEqual(1, outputEvents.Count);
        StringAssert.Contains(
            CliLogStyle.FormatOutputEventText(outputEvents[0]),
            "[cli:TestComponent] visible lifecycle");
    }

    [TestMethod]
    public void LoggerFactory_QuietConsoleStillWritesDebugFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-cli-logging-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");
        var outputEvents = new List<CliOutputEvent>();

        try
        {
            using var output = CliOutputController.CreateDetached(eventSink: outputEvents.Add);
            using ILoggerFactory factory = output.CreateLoggerFactory(
                LogLevel.Information,
                path,
                LogLevel.Debug);
            ILogger logger = factory.CreateLogger("Sockseek.Cli.TestComponent");

            logger.LogDebug("debug decision");
            logger.LogInformation("visible lifecycle");

            Assert.AreEqual(1, outputEvents.Count);
            StringAssert.Contains(
                CliLogStyle.FormatOutputEventText(outputEvents[0]),
                "[cli] visible lifecycle");
            Assert.IsFalse(
                CliLogStyle.FormatOutputEventText(outputEvents[0]).Contains("TestComponent", StringComparison.Ordinal));
            string file = File.ReadAllText(path);
            StringAssert.Contains(file, "[debug] [cli:TestComponent] debug decision");
            StringAssert.Contains(file, "[info] [cli:TestComponent] visible lifecycle");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void SensitiveOutput_ReachesOnlyExplicitConsoleBoundary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-cli-sensitive-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");
        var outputEvents = new List<CliOutputEvent>();

        try
        {
            using var output = CliOutputController.CreateDetached(eventSink: outputEvents.Add);
            using ILoggerFactory factory = output.CreateLoggerFactory(
                LogLevel.Error,
                path,
                LogLevel.Debug);
            var sensitiveOutput = new CliSensitiveOutput(output);

            sensitiveOutput.WriteLine("access-token=private-value");

            Assert.AreEqual(1, outputEvents.Count);
            Assert.IsInstanceOfType<CliOutputEvent.RawLine>(outputEvents[0]);
            Assert.AreEqual("access-token=private-value", ((CliOutputEvent.RawLine)outputEvents[0]).Text);
            Assert.IsFalse(File.Exists(path), "Sensitive output must never create or enter the diagnostic log file.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    private static Exception CreateTestException()
    {
        try
        {
            throw new InvalidOperationException("concise failure");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
