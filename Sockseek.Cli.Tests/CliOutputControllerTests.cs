using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
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

    [TestMethod]
    public void Install_Detach_RoutesLogsToStderr()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            using var output = CliOutputController.Install(["--remote", "http://127.0.0.1:5030", "--detach"]);

            using var logs = output.CreateLoggerFactory(LogLevel.Information, null, LogLevel.Debug);
            logs.CreateLogger("test").LogInformation("detached log");

            Assert.AreEqual("", stdout.ToString());
            StringAssert.Contains(stderr.ToString(), "detached log");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            }
    }
}
