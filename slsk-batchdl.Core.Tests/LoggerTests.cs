using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;

namespace Tests.Core;

[TestClass]
public class LoggerTests
{
    [TestCleanup]
    public void Cleanup()
    {
        Logger.RemoveNonFileOutputs();
        Logger.RemoveFileOutputs();
    }

    [TestMethod]
    public void LogConsoleOnly_DoesNotWriteToNonConsoleSinks()
    {
        Logger.RemoveNonFileOutputs();
        var consoleMessages = new List<string>();
        var sinkMessages = new List<string>();

        Logger.AddConsole(writer: (message, _) => consoleMessages.Add(message));
        Logger.AddSink((_, message) => sinkMessages.Add(message));

        Logger.LogConsoleOnly(Logger.LogLevel.Info, "spotify-token=secret");

        CollectionAssert.Contains(consoleMessages, "spotify-token=secret");
        Assert.AreEqual(0, sinkMessages.Count);
    }

    [TestMethod]
    public async Task FileLogging_AllowsConcurrentWritesToSameLogFile()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "sldl-logger-concurrent-" + Guid.NewGuid() + ".log");

        try
        {
            Logger.AddOrReplaceFile(logPath, Logger.LogLevel.Debug, prependDate: false, prependLogLevel: false);

            await Task.WhenAll(Enumerable.Range(0, 100)
                .Select(i => Task.Run(() => Logger.Debug($"message-{i}"))));

            var lines = File.ReadAllLines(logPath);
            Assert.AreEqual(100, lines.Length);
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 100).Select(i => $"message-{i}").ToArray(),
                lines);
        }
        finally
        {
            Logger.RemoveFileOutputs();
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    [TestMethod]
    public void FileLogging_DoesNotThrowWhenLogFileIsLockedByAnotherProcess()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "sldl-logger-locked-" + Guid.NewGuid() + ".log");
        File.WriteAllText(logPath, "");

        try
        {
            Logger.AddOrReplaceFile(logPath, Logger.LogLevel.Debug, prependDate: false, prependLogLevel: false);

            using var locked = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Logger.Debug("this should not crash");
        }
        finally
        {
            Logger.RemoveFileOutputs();
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }
}
