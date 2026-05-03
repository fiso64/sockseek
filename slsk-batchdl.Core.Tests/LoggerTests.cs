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
}
