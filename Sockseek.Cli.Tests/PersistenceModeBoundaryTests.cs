using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;

namespace Tests.Cli;

[TestClass]
public sealed class PersistenceModeBoundaryTests
{
    [TestMethod]
    public async Task DatabaseMigrate_UsesDataDirectoryFromConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-database-config-" + Guid.NewGuid());
        string configPath = Path.Combine(root, "sockseek.conf");
        string dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(configPath, "data-dir = {configdir}/data");

        try
        {
            var exit = await Sockseek.Cli.Program.Main(
                ["database", "migrate", "--config", configPath]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.Success, exit);
            Assert.IsTrue(File.Exists(Path.Combine(dataDirectory, "sockseek.db")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "persistence")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task DatabaseBackup_UsesBackupOption()
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-database-backup-" + Guid.NewGuid());
        string dataDirectory = Path.Combine(root, "data");
        string backupPath = Path.Combine(root, "backup", "sockseek.db");

        try
        {
            Assert.AreEqual(
                (int)Sockseek.Cli.Program.CliExitCode.Success,
                await Sockseek.Cli.Program.Main(
                    ["database", "migrate", "--no-config", "--data-dir", dataDirectory]));

            Assert.AreEqual(
                (int)Sockseek.Cli.Program.CliExitCode.Success,
                await Sockseek.Cli.Program.Main(
                    ["database", "backup", "--no-config", "--data-dir", dataDirectory, "--backup", backupPath]));

            Assert.IsTrue(File.Exists(backupPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task DatabaseCommand_RejectsDatabaseFileOption()
    {
        try
        {
            var exit = await Sockseek.Cli.Program.Main(
                ["database", "migrate", "--database", "sockseek.db"]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.UsageError, exit);
        }
        finally
        {
        }
    }

    [TestMethod]
    public async Task OneShotProfileInspection_DoesNotCreatePersistenceArtifacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-one-shot-persistence-boundary-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string configPath = Path.Combine(root, "config.ini");
        try
        {
            var exit = await Sockseek.Cli.Program.MainCore(["--config", configPath, "--profile", "help"]);

            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Success, exit);
            string[] artifacts = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsPersistenceArtifact)
                .ToArray();
            CollectionAssert.AreEqual(Array.Empty<string>(), artifacts,
                "Ordinary one-shot CLI execution must remain persistence-free.");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "persistence")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task LocalPrintJobsUsesPlannerWithoutCreatingPreviewDatabase()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "sockseek-local-planner-boundary-" + Guid.NewGuid());
        string input = Path.Combine(root, "input.csv");
        string dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            input,
            "artist,title\nLocal Artist,Local Title\n");
        TextWriter originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exit = await Sockseek.Cli.Program.MainCore([
                input,
                "--input-type", "csv",
                "--print", "jobs",
                "--no-config",
                "--data-dir", dataDirectory,
                "--no-progress",
            ]);

            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Success, exit);
            StringAssert.Contains(output.ToString(), "Local Artist");
            StringAssert.Contains(output.ToString(), "Local Title");
            Assert.IsFalse(Directory.Exists(dataDirectory));
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.db*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Console.SetOut(originalOut);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsPersistenceArtifact(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || name.Contains("backup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("migration", StringComparison.OrdinalIgnoreCase);
    }
}
