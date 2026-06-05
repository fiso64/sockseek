using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Sockseek.Core;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class CoreLoggerBridgeTests
{
    [TestCleanup]
    public void Cleanup()
    {
        SockseekLog.RemoveNonFileOutputs();
        SockseekLog.RemoveFileOutputs();
    }

    [TestMethod]
    public void Configure_RoutesDaemonLogsToTimestampedStdout()
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();

        try
        {
            Console.SetOut(output);
            CoreLoggerBridge.Configure(null!, LogLevel.Information);

            SockseekLog.Info("daemon is ready", categoryName: SockseekLog.Categories.Daemon);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = output.ToString().Trim();
        StringAssert.Contains(line, "[info] [daemon] daemon is ready");
        StringAssert.Matches(line, new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} "));
    }
}
