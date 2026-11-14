using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using System.Text.Json;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
public class CliExitCodeTests
{
    [TestMethod]
    public async Task MainCore_UnknownFlag_ReturnsUsageError()
    {
        var exitCode = await Sockseek.Cli.Program.MainCore(["--not-a-real-flag"]);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
    }

    [TestMethod]
    public async Task MainCore_InvalidDaemonIp_ReturnsUsageError()
    {
        var exitCode = await Sockseek.Cli.Program.MainCore(["daemon", "--no-config", "--server-ip", "999.1.1.1"]);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
    }

    [TestMethod]
    public async Task MainCore_InvalidDaemonPort_ReturnsUsageError()
    {
        var exitCode = await Sockseek.Cli.Program.MainCore(["daemon", "--no-config", "--server-port", "70000"]);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
    }

    [TestMethod]
    [DataRow("share", "status")]
    [DataRow("transfers", null)]
    [DataRow("transfer", "cancel")]
    [DataRow("user", "profile")]
    public async Task DaemonResourceCommands_RequireRemote(
        string command,
        string? action)
    {
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            string[] args = action is null
                ? [command, "--no-config"]
                : [command, action, "--no-config"];

            int exitCode = await Sockseek.Cli.Program.Main(args);

            Assert.AreEqual(
                (int)Sockseek.Cli.Program.CliExitCode.UsageError,
                exitCode);
            StringAssert.Contains(stderr.ToString(), "requires a configured remote URL");
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task GroupScopedTransferCommands_RequirePeerAndDirection()
    {
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            foreach (string[] args in new[]
            {
                new[] { "transfers", "cancel", "--direction", "download", "--group-ref", "folder" },
                new[] { "transfers", "remove", "--username", "peer", "--group-ref", "folder" },
            })
            {
                stderr.GetStringBuilder().Clear();
                var exitCode = await DaemonResourceCommandRunner.RunAsync(args, new RemoteSettings { ServerUrl = "http://127.0.0.1:1" });
                Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
            }
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    [DataRow("chat", "status", null)]
    [DataRow("share", "status", null)]
    [DataRow("user", "profile", "Peer")]
    public async Task ConfiguredResourceCommands_UseRemoteFromConfig(
        string command,
        string action,
        string? operand)
    {
        string root = Path.Combine(
            Path.GetTempPath(), "sockseek-configured-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configPath = Path.Combine(root, "sockseek.conf");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        int requests = 0;
        // Commands may issue concurrent requests (profile and presence, for
        // example). Answer all of them rather than waiting for HTTP timeouts.
        app.Run(context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.StatusCode = 503;
            return Task.CompletedTask;
        });

        var originalError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            await app.StartAsync();
            await File.WriteAllTextAsync(configPath, $"remote = {app.Urls.Single()}\n");
            Console.SetError(stderr);

            string[] args = operand is null
                ? [command, action, "--config", configPath]
                : [command, action, operand, "--config", configPath];
            int exitCode = await Sockseek.Cli.Program.Main(args).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.WorkFailed, exitCode);
            Assert.IsTrue(requests > 0, "The command must contact the configured remote server.");
        }
        finally
        {
            Console.SetError(originalError);
            await app.StopAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void ConfiguredCommand_CliRemoteOverridesConfigAndGlobalOptionsAreStripped()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "sockseek-command-precedence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configPath = Path.Combine(root, "sockseek.conf");
        File.WriteAllText(
            configPath,
            "remote = http://127.0.0.1:5001\n"
            + "[alternate]\n"
            + "remote = http://127.0.0.1:5003\n");

        try
        {
            ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                [
                    "chat", "status",
                    "--config", configPath,
                    "--remote", "http://127.0.0.1:5002",
                ],
                ConfiguredCommandOptions.Remote);

            Assert.AreEqual("http://127.0.0.1:5002", invocation.Remote.ServerUrl);
            CollectionAssert.AreEqual(
                new[] { "chat", "status" }, invocation.CommandArguments);

            ConfiguredCommandInvocation profileInvocation = ConfiguredCommandInvocation.Create(
                ["chat", "status", "--config", configPath, "--profile", "alternate"],
                ConfiguredCommandOptions.Remote);
            Assert.AreEqual("http://127.0.0.1:5003", profileInvocation.Remote.ServerUrl);
            CollectionAssert.AreEqual(
                new[] { "chat", "status" }, profileInvocation.CommandArguments);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void EnsureDaemonEndpointAvailable_PortCollision_ThrowsConciseException()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var ex = Assert.ThrowsExactly<Sockseek.Cli.Program.DaemonEndpointUnavailableException>(() =>
                Sockseek.Cli.Program.EnsureDaemonEndpointAvailable(new DaemonSettings
                {
                    ListenIp = "127.0.0.1",
                    ListenPort = port,
                }));

            StringAssert.Contains(ex.Message, "Cannot start Sockseek daemon on");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void DetermineLocalExitCode_ManualSkipsOnly_ReturnsSuccess()
    {
        var skipped = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        skipped.SetSkipped(JobSkipReason.Manual);
        var queue = new JobList("root", [skipped]);
        queue.SetDone();

        var exitCode = Sockseek.Cli.Program.DetermineLocalExitCode(queue);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Success, exitCode);
    }

    [TestMethod]
    public async Task MainCore_NoSuitableFile_ReturnsWorkFailed()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-exit-empty-music-" + Guid.NewGuid());
        var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-exit-empty-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);

        try
        {
            var exitCode = await Sockseek.Cli.Program.MainCore([
                "Definitely Missing Track",
                "--song",
                "--mock-files-dir", musicRoot,
                "--mock-files-no-read-tags",
                "--no-config",
                "--no-progress",
                "--path", outputDir,
            ]);

            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.WorkFailed, exitCode);
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task MainCore_SuccessfulDownload_ReturnsSuccess()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-exit-music-" + Guid.NewGuid());
        var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-exit-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(musicRoot, "Artist - Song.mp3"), [1, 2, 3, 4]);

        try
        {
            var exitCode = await Sockseek.Cli.Program.MainCore([
                "Artist - Song",
                "--song",
                "--mock-files-dir", musicRoot,
                "--mock-files-no-read-tags",
                "--no-config",
                "--no-progress",
                "--path", outputDir,
            ]);

            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Success, exitCode);
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task Main_UnknownFlag_WritesDiagnosticToStderr()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main(["--no-config", "--not-a-real-flag"]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
            Assert.AreEqual("", stdout.ToString());
            StringAssert.Contains(stderr.ToString(), "Input error:");
            StringAssert.Contains(stderr.ToString(), "--not-a-real-flag");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task Main_MissingCredentials_WritesCleanErrorWithoutEmptyProgressSummary()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main(["--no-config", "blah"]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.WorkFailed, exitCode);
            StringAssert.Contains(stdout.ToString(), "[cli] CLI session started in local mode");
            StringAssert.Contains(stderr.ToString(), "[error] [cli] Soulseek login failed: Missing Soulseek username and password.");
            var combined = stdout.ToString() + stderr.ToString();
            Assert.IsFalse(combined.Contains("0 active", StringComparison.Ordinal), "Missing credentials must not print an empty progress summary.");
            Assert.IsFalse(combined.Contains("--random-login", StringComparison.Ordinal), "Credential guidance must not advertise random login.");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task Main_ProgressJsonMissingCredentials_WritesHumanLogsToStderrOnly()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main(["--no-config", "blah", "--progress-json"]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.WorkFailed, exitCode);
            Assert.AreEqual("", stdout.ToString());
            StringAssert.Contains(stderr.ToString(), "[cli] CLI session started in local mode");
            StringAssert.Contains(stderr.ToString(), "[error] [cli] Soulseek login failed: Missing Soulseek username and password.");
            Assert.IsFalse(stderr.ToString().Contains("0 active", StringComparison.Ordinal), "Missing credentials must not print an empty progress summary.");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task Main_ProgressJson_WritesOnlyJsonLinesToStdout()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-json-music-" + Guid.NewGuid());
        var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-json-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(musicRoot, "Artist - Song.mp3"), [1, 2, 3, 4]);

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main([
                "Artist - Song",
                "--song",
                "--mock-files-dir", musicRoot,
                "--mock-files-no-read-tags",
                "--no-config",
                "--progress-json",
                "--path", outputDir,
            ]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.Success, exitCode);
            var lines = stdout.ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.IsTrue(lines.Length > 0, "Expected at least one JSON progress line on stdout.");

            foreach (var line in lines)
                using (JsonDocument.Parse(line)) { }

            StringAssert.Contains(stderr.ToString(), "[soulseek]");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }
}
