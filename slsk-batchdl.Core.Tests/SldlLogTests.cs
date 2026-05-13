using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;

namespace Tests.Core;

[TestClass]
public class SldlLogTests
{
    [TestCleanup]
    public void Cleanup()
    {
        SldlLog.RemoveNonFileOutputs();
        SldlLog.RemoveFileOutputs();
    }

    [TestMethod]
    public void LogConsoleOnly_DoesNotWriteToNonConsoleSinks()
    {
        SldlLog.RemoveNonFileOutputs();
        var consoleMessages = new List<string>();
        var sinkMessages = new List<string>();

        SldlLog.AddConsole(writer: (message, _) => consoleMessages.Add(message));
        SldlLog.AddSink((_, message) => sinkMessages.Add(message));

        SldlLog.LogConsoleOnly(LogLevel.Information, "spotify-token=secret");

        CollectionAssert.Contains(consoleMessages, "spotify-token=secret");
        Assert.AreEqual(0, sinkMessages.Count);
    }

    [TestMethod]
    public async Task FileLogging_AllowsConcurrentWritesToSameLogFile()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "sldl-logger-concurrent-" + Guid.NewGuid() + ".log");

        try
        {
            SldlLog.AddOrReplaceFile(logPath, LogLevel.Debug, prependDate: false, prependLogLevel: false);

            await Task.WhenAll(Enumerable.Range(0, 100)
                .Select(i => Task.Run(() => SldlLog.Debug($"message-{i}"))));

            var lines = File.ReadAllLines(logPath);
            Assert.AreEqual(100, lines.Length);
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 100).Select(i => $"[sldl.tests.core] message-{i}").ToArray(),
                lines);
        }
        finally
        {
            SldlLog.RemoveFileOutputs();
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
            SldlLog.AddOrReplaceFile(logPath, LogLevel.Debug, prependDate: false, prependLogLevel: false);

            using var locked = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            SldlLog.Debug("this should not crash");
        }
        finally
        {
            SldlLog.RemoveFileOutputs();
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }
}
