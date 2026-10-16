using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public class CliOutputControllerTests
{
    [TestMethod]
    public void Dispose_DetachesOnlyItsOwnConsoleSink()
    {
        var ownMessages = new List<string>();
        var otherMessages = new List<string>();
        using var output = CliOutputController.CreateDetached(eventSink: entry =>
            ownMessages.Add(entry.ToString()!));
        using var other = CliOutputController.CreateDetached(eventSink: entry =>
            otherMessages.Add(entry.ToString()!));
        using var ownFactory = output.CreateLoggerFactory(LogLevel.Information, null, LogLevel.Debug);
        using var otherFactory = other.CreateLoggerFactory(LogLevel.Information, null, LogLevel.Debug);
        var ownLogger = ownFactory.CreateLogger("test");
        var otherLogger = otherFactory.CreateLogger("test");
        ownLogger.LogInformation("before disposal");
        otherLogger.LogInformation("before disposal");
        output.Dispose();
        output.Dispose();
        ownLogger.LogInformation("after disposal");
        otherLogger.LogInformation("after disposal");
        Assert.AreEqual(1, ownMessages.Count, "A disposed controller must not emit later logs.");
        Assert.AreEqual(2, otherMessages.Count, "Disposal must not detach another controller's logger.");
    }
}
