using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class HelpReferenceTests
{
    [TestMethod]
    [DataRow("daemon", "--server-port")]
    [DataRow("auth", "sockseek auth keys create")]
    public async Task CommandHelp_SelectsItsTopic(string command, string usage)
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            Assert.AreEqual(0, await Sockseek.Cli.Program.Main([command, "--help"]));
            string commandHelp = output.ToString();
            StringAssert.Contains(commandHelp, usage);
            output.GetStringBuilder().Clear();
            Assert.AreEqual(0, await Sockseek.Cli.Program.Main(["--help", command]));
            Assert.AreEqual(commandHelp, output.ToString());
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
