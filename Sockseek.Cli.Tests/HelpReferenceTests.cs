using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class HelpReferenceTests
{
    [TestMethod]
    public void MainHelp_ContainsPublicDaemonDatabaseAndRetentionOptions()
    {
        string mainHelp = GetGeneratedHelpConstant("helpText");
        string[] expectedOptions =
        [
            "--data-dir",
            "--no-retention",
            "--successful-job-retention-days",
            "--unsuccessful-job-retention-days",
            "--transfer-retention-days",
            "--search-result-retention-days",
            "--chat-room",
            "--private-message-retention-days",
            "--room-message-retention-days",
            "sockseek database migrate",
            "sockseek database integrity",
            "sockseek database backup",
            "sockseek database restore",
        ];

        foreach (string option in expectedOptions)
            StringAssert.Contains(mainHelp, option);

        string[] internalOptions =
        [
            "--persistence-db",
            "--persistence-progress-flush-seconds",
            "--persistence-search-flush-count",
            "--persistence-search-flush-ms",
            "--retention-interval-hours",
            "--completed-job-retention-days",
            "--history-retention-days",
            "--max-retained-jobs",
            "--retention-batch-size",
            "sockseek persistence",
        ];

        foreach (string option in internalOptions)
            Assert.IsFalse(mainHelp.Contains(option, StringComparison.Ordinal));

        Assert.AreEqual(1, CountOccurrences(mainHelp, "-c, --config <path>"));
        Assert.AreEqual(1, CountOccurrences(mainHelp, "--no-config"));

        int aggregateIndex = mainHelp.IndexOf("Aggregate Download Options", StringComparison.Ordinal);
        int daemonIndex = mainHelp.IndexOf("Daemon / Remote Options", StringComparison.Ordinal);
        int databaseIndex = mainHelp.IndexOf("Sockseek Database", StringComparison.Ordinal);
        int debugIndex = mainHelp.IndexOf("Printing & Debug Options", StringComparison.Ordinal);
        Assert.IsTrue(aggregateIndex < daemonIndex);
        Assert.IsTrue(daemonIndex < databaseIndex);
        Assert.IsTrue(databaseIndex < debugIndex);
    }

    [TestMethod]
    public void DaemonCommandHelp_SelectsDaemonTopic()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            Assert.IsTrue(Help.PrintAndExitIfNeeded(["daemon", "--help"]));

            StringAssert.Contains(output.ToString(), "Daemon / remote mode");
            StringAssert.Contains(output.ToString(), "daemon setup and remote commands");
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

            StringAssert.Contains(output.ToString(), "Sockseek Database");
            StringAssert.Contains(output.ToString(), "sockseek database backup");
            Assert.IsFalse(output.ToString().Contains("Required Arguments", StringComparison.Ordinal));
            Assert.IsFalse(output.ToString().Contains("--config", StringComparison.Ordinal));
            Assert.IsFalse(output.ToString().Contains("--no-config", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    private static string GetGeneratedHelpConstant(string name)
    {
        var field = typeof(Help).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field, $"Generated help constant '{name}' was not found.");
        return field.GetRawConstantValue() as string
            ?? throw new AssertFailedException($"Generated help constant '{name}' is not a string.");
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
