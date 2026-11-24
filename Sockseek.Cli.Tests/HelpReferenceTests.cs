
namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class HelpReferenceTests
{
    [TestMethod]
    [DataRow("daemon", "--server-port")]
    [DataRow("database", "sockseek database backup")]
    [DataRow("auth", "sockseek auth keys create")]
    [DataRow("chat", "sockseek chat delete")]
    [DataRow("room", "sockseek room join")]
    [DataRow("notifications", "sockseek notifications [--unread]")]
    [DataRow("notification", "sockseek notification read")]
    [DataRow("share", "sockseek share scan")]
    [DataRow("transfers", "sockseek transfers cancel")]
    [DataRow("transfer", "sockseek transfer cancel")]
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
    public async Task MainHelp_ListsCommandsAndKeepsDetailsInFullReference()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.AreEqual(0, await Sockseek.Cli.Program.Main(["--help"]));
            string main = output.ToString();
            StringAssert.Contains(main, "sockseek chat --help");
            StringAssert.Contains(main, "--remote <url>");
            Assert.IsFalse(main.Contains("sockseek chat delete", StringComparison.Ordinal));
            Assert.IsFalse(main.Contains("--server-port", StringComparison.Ordinal));

            output.GetStringBuilder().Clear();
            Assert.AreEqual(0, await Sockseek.Cli.Program.Main(["--help", "all"]));
            StringAssert.Contains(output.ToString(), "sockseek chat delete");
            StringAssert.Contains(output.ToString(), "--server-port");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }
}
