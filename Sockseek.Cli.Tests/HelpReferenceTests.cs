using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class HelpReferenceTests
{
    [TestMethod]
    public void DaemonCommandHelp_SelectsDaemonTopic()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            Assert.IsTrue(Help.PrintAndExitIfNeeded(["daemon", "--help"]));

            StringAssert.Contains(output.ToString(), "--remote-api-key-file");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [TestMethod]
    public void DatabaseCommandHelp_SelectsDatabaseTopic()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            Assert.IsTrue(Help.PrintAndExitIfNeeded(["database", "--help"]));

            StringAssert.Contains(output.ToString(), "sockseek database backup");
            Assert.IsFalse(output.ToString().Contains("--config", StringComparison.Ordinal));
            Assert.IsFalse(output.ToString().Contains("--no-config", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }
}
