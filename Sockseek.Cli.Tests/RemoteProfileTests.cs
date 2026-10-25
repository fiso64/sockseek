using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;
using Sockseek.Server;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
public class RemoteProfileTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task DaemonOnlyProfile_UsesDaemonInput(bool detach)
        => RunCase(clientProfile: false, daemonProfile: true, detach);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ClientOnlyProfile_IsRejectedByDaemon(bool detach)
        => RunCase(clientProfile: true, daemonProfile: false, detach);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ProfileOnBothSides_UsesDaemonSettings(bool detach)
        => RunCase(clientProfile: true, daemonProfile: true, detach);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ExplicitInput_OverridesBothProfileInputs(bool detach)
        => RunCase(clientProfile: true, daemonProfile: true, detach, explicitInput: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task CommandLineSettings_OverrideDaemonProfile(bool detach)
        => RunCase(clientProfile: true, daemonProfile: true, detach, overrideDownloadSettings: true);

    [TestMethod]
    public void ClientProfile_SelectsConnectionAndDetachMode()
    {
        var (_, _, _, _, remote) = ConfigManager.BindAll(ClientSettingsConfig(), ["--profile", "wishlist"]);

        Assert.AreEqual("http://profile.invalid:5030", remote.ServerUrl);
        Assert.IsTrue(remote.IsEnabled);
        Assert.IsTrue(remote.Detach);
    }

    [TestMethod]
    public void CommandLineConnectionSettings_OverrideLocalProfile()
    {
        string[] args = ["--profile", "wishlist", "--server-url", "http://cli.invalid:5030", "--detach=false"];
        var (_, _, _, _, remote) = ConfigManager.BindAll(ClientSettingsConfig(), args);

        Assert.AreEqual("http://cli.invalid:5030", remote.ServerUrl);
        Assert.IsTrue(remote.IsEnabled);
        Assert.IsFalse(remote.Detach);
    }

    [TestMethod]
    public void ClientProfile_ControlsClientSettingsAndSubmissionContext()
    {
        string[] args = ["--profile", "wishlist"];
        var (engine, download, cli, _, _) = ConfigManager.BindAll(ClientSettingsConfig(), args);
        var options = Sockseek.Cli.Program.BuildRemoteSubmissionOptions(args, cli);

        Assert.AreEqual(LogLevel.Debug, engine.LogLevel);
        Assert.IsTrue(cli.InteractiveMode);
        Assert.IsTrue(cli.NoProgress);
        Assert.IsTrue(cli.ProgressJson);
        Assert.IsTrue(download.PrintJobs);
        Assert.IsFalse(CliOutputController.WouldUseLiveRendering(cli));
        CollectionAssert.AreEqual(new[] { "wishlist" }, options.ProfileNames!.ToArray());
        Assert.IsTrue(options.ProfileContext!["interactive"]);
        Assert.IsTrue(options.ProfileContext["no-progress"]);
        Assert.IsTrue(options.ProfileContext["progress-json"]);
        Assert.IsNull(options.DownloadSettings, "Local profile download settings must not be sent as overrides.");
    }

    [TestMethod]
    public void CommandLineClientSettings_OverrideLocalProfile()
    {
        string[] args = ["--profile", "wishlist", "--interactive=false", "--progress",
            "--progress-json=false", "--trace", "--print", "none"];
        var (engine, download, cli, _, _) = ConfigManager.BindAll(ClientSettingsConfig(), args);
        var options = Sockseek.Cli.Program.BuildRemoteSubmissionOptions(args, cli);

        Assert.AreEqual(LogLevel.Trace, engine.LogLevel);
        Assert.IsFalse(cli.InteractiveMode);
        Assert.IsFalse(cli.NoProgress);
        Assert.IsFalse(cli.ProgressJson);
        Assert.AreEqual(PrintOption.None, download.PrintOption);
        Assert.IsFalse(options.ProfileContext!["interactive"]);
        Assert.IsFalse(options.ProfileContext["no-progress"]);
        Assert.IsFalse(options.ProfileContext["progress-json"]);
    }

    private static ConfigFile ClientSettingsConfig()
        => new("none", new Dictionary<string, ProfileEntry>
        {
            ["wishlist"] = new(["--remote", "http://profile.invalid:5030", "--detach", "--interactive", "--no-progress",
                "--progress-json", "--verbose", "--print-jobs", "--format", "mp3"], null),
        });

    private static async Task RunCase(
        bool clientProfile, bool daemonProfile, bool detach, bool explicitInput = false, bool overrideDownloadSettings = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "sockseek-remote-profile-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        var clientInput = Path.Combine(root, "client-wishlist.txt");
        var daemonInput = Path.Combine(root, "daemon-wishlist.txt");
        var overrideInput = Path.Combine(root, "explicit-wishlist.txt");
        File.WriteAllText(clientInput, "\"Artist - Client Album\"\n");
        File.WriteAllText(daemonInput, "\"Artist - Daemon Album\"\n");
        File.WriteAllText(overrideInput, "\"Artist - Explicit Album\"\n");
        var configPath = Path.Combine(root, "client.conf");
        var daemonWishlist = new SettingsProfile
        {
            Name = "wishlist",
            Download = new SettingsPatch([
                new("Extraction.Input", daemonInput),
                new("Extraction.InputType", InputType.List),
                new("Search.MaxStaleTime", 2000),
                new("Search.NecessaryCond.Formats", new[] { "flac" }),
                new("Search.PreferredCond.Formats", new[] { "flac" }),
                new("Search.NecessaryCond.MinBitrate", 320),
                new("Output.NameFormat", "daemon/{filename}"),
                new("Output.WriteIndex", true),
                new("Skip.SkipExisting", false),
                new("Extraction.Reverse", true),
            ]),
        };
        await using var app = TestServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings { MockFilesDir = library, MockFilesReadTags = false },
            DefaultDownload = new DownloadSettings
            {
                Output = { ParentDir = Path.Combine(root, "output") },
                Transfer = { MaxDownloadRetries = 7 },
            },
            Profiles = new ProfileCatalog
            {
                NamedProfiles = daemonProfile ? [daemonWishlist] : [],
                AutoProfiles = [new SettingsProfile
                {
                    Name = "client-no-progress", Condition = "no-progress",
                    Download = new SettingsPatch([new("Search.SearchTimeout", 12345)]),
                }],
            },
        }, "http://127.0.0.1:0");
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var stateStore = app.Services.GetRequiredService<EngineSupervisor>().StateStore;
        var completed = new TaskCompletionSource<JobSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnJobUpserted(JobSummaryDto summary)
        {
            if (summary.Kind == ServerJobKind.Extract && summary.ParentJobId == null
                && summary.TerminalOutcome != ServerJobTerminalOutcome.None)
                completed.TrySetResult(summary);
        }
        stateStore.JobUpserted += OnJobUpserted;
        try
        {
            await app.StartAsync();
            var url = app.Urls.Single();
            File.WriteAllText(configPath, clientProfile
                ? $"[wishlist]\nremote = {url}\ninput = {clientInput}\ninput-type = list\nmax-stale-time = 1000\n"
                    + "format = mp3\npref-format = mp3\nmin-bitrate = 128\nname-format = client/{filename}\n"
                    + "write-index = false\nskip-existing = true\nreverse = false\nmax-retries = 99\nno-progress = true\n"
                : "");
            using var http = SockseekApiClient.CreateHttpClient(url);
            var api = new SockseekApiClient(http);
            List<string> args = ["--config", configPath, "--profile", "wishlist", "--print-jobs"];
            if (!clientProfile) args.AddRange(["--remote", url]);
            if (detach) args.Add("--detach");
            if (explicitInput) args.AddRange([overrideInput, "--input-type", "list"]);
            if (overrideDownloadSettings)
                args.AddRange(["--format", "ogg", "--pref-format", "ogg", "--min-bitrate", "192",
                    "--max-stale-time", "3000", "--name-format", "cli/{filename}", "--no-write-index",
                    "--skip-existing=true", "--reverse=false", "--max-retries", "11"]);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await Sockseek.Cli.Program.Main(args.ToArray()).WaitAsync(TimeSpan.FromSeconds(15));
            Console.SetOut(originalOut);
            Console.SetError(originalError);

            var query = new JobQuery(null, null, null, null, IncludeAll: false);
            if (clientProfile && !daemonProfile)
            {
                Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.WorkFailed, exit);
                StringAssert.Contains(stderr.ToString(), "No profile 'wishlist' found in config");
                Assert.IsEmpty(await api.GetJobsAsync(query));
                return;
            }

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.Success, exit, stderr.ToString());
            var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(ServerJobTerminalOutcome.Succeeded, summary.TerminalOutcome, summary.FailureMessage);
            var job = stateStore.GetJob<ExtractJob>(summary.JobId);
            Assert.IsNotNull(job);
            var expectedInput = explicitInput ? overrideInput : daemonInput;
            Assert.AreEqual(expectedInput, job.Input);
            Assert.AreEqual(expectedInput, job.Config.Extraction.Input);
            Assert.AreEqual(InputType.List, job.InputType);
            var settings = job.Config;
            CollectionAssert.AreEqual(new[] { overrideDownloadSettings ? "ogg" : "flac" }, settings.Search.NecessaryCond.Formats);
            CollectionAssert.AreEqual(new[] { overrideDownloadSettings ? "ogg" : "flac" }, settings.Search.PreferredCond.Formats);
            Assert.AreEqual(overrideDownloadSettings ? 192 : 320, settings.Search.NecessaryCond.MinBitrate);
            Assert.AreEqual(overrideDownloadSettings ? 3000 : 2000, settings.Search.MaxStaleTime);
            Assert.AreEqual(overrideDownloadSettings ? "cli/{filename}" : "daemon/{filename}", settings.Output.NameFormat);
            Assert.AreEqual(!overrideDownloadSettings, settings.Output.WriteIndex);
            Assert.AreEqual(overrideDownloadSettings, settings.Skip.SkipExisting);
            Assert.AreEqual(!overrideDownloadSettings, settings.Extraction.Reverse);
            Assert.AreEqual(overrideDownloadSettings ? 11 : 7, settings.Transfer.MaxDownloadRetries,
                "A client-only setting must not fill gaps in the daemon profile.");
            Assert.AreEqual(clientProfile ? 12345 : 5000, settings.Search.SearchTimeout);
            CollectionAssert.AreEqual(clientProfile ? new[] { "client-no-progress" } : Array.Empty<string>(),
                settings.AppliedAutoProfiles.ToArray());
        }
        finally
        {
            stateStore.JobUpserted -= OnJobUpserted;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SockseekLog.RemoveNonFileOutputs();
            await app.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
