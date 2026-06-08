using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Settings;
using Sockseek.Server;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Tests.Cli;

[TestClass]
public class RemoteCliBackendTests
{
    [TestInitialize]
    public void Initialize()
    {
        SockseekLog.RemoveNonFileOutputs();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SockseekLog.RemoveNonFileOutputs();
    }

    [TestMethod]
    public void NormalizeServerUrl_AcceptsHostOnlyAndDefaultsDaemonPort()
    {
        Assert.AreEqual(
            "http://127.0.0.1:5030/",
            SockseekApiClient.NormalizeServerUrl("127.0.0.1").ToString());
    }

    [TestMethod]
    public void NormalizeServerUrl_PreservesExplicitSchemeAndPort()
    {
        Assert.AreEqual(
            "http://127.0.0.1:6123/",
            SockseekApiClient.NormalizeServerUrl("http://127.0.0.1:6123").ToString());
    }

    [TestMethod]
    public async Task RemoteCliBackend_SearchProjectionAndDownloadFollowUp_Work()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.mp3"), "a");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            var seenTypes = new ConcurrentBag<string>();
            backend.EventReceived += envelope => seenTypes.Add(envelope.Type);
            await backend.StartAsync();

            var searchSummary = await backend.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)));

            await WaitForJobStateAsync(backend, searchSummary.JobId, ServerProtocol.JobStates.Done);

            var projection = await backend.GetFileResultsAsync(searchSummary.JobId);
            Assert.IsNotNull(projection);
            Assert.AreEqual(1, projection.Items.Count);

            var downloadSummary = await backend.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto(
                    [projection.Items[0].Ref],
                    new SubmissionOptionsDto(OutputParentDir: outputDir)));

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(1, downloadSummary.Count);
            var downloadedSummary = downloadSummary[0];
            Assert.AreEqual(searchSummary.WorkflowId, downloadedSummary.WorkflowId);
            Assert.IsNull(downloadedSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadedSummary.SourceJobId);

            await WaitForJobStateAsync(backend, downloadedSummary.JobId, ServerProtocol.JobStates.Done);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray();
            CollectionAssert.Contains(downloaded, "01. Artist - Track One.mp3");

            Assert.IsTrue(seenTypes.Contains("job.upserted"));
            Assert.IsTrue(seenTypes.Contains("search.updated"));
            Assert.IsTrue(seenTypes.Contains("download.started"));
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_GenericSearchProjectsFilesAndFolders()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-generic-search-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-generic-search-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var searchSummary = await backend.SubmitSearchJobAsync(new SubmitSearchJobRequestDto("Artist Album"));
            await WaitForJobStateAsync(backend, searchSummary.JobId, ServerProtocol.JobStates.Done);

            var files = await backend.GetFileResultsAsync(
                searchSummary.JobId,
                new FileSearchProjectionRequestDto(new SongQueryDto("Artist", "Track One", "", "", -1, false)));
            Assert.IsNotNull(files);
            Assert.IsTrue(files.Items.Any(file => file.Filename.EndsWith("01. Artist - Track One.mp3", StringComparison.Ordinal)));

            var albumQuery = new AlbumQueryDto("Artist", "Album", "", "", false);
            var folders = await backend.GetFolderResultsAsync(
                searchSummary.JobId,
                new FolderSearchProjectionRequestDto(albumQuery, IncludeFiles: true));
            Assert.IsNotNull(folders);
            Assert.AreEqual(1, folders!.Items.Count);
            Assert.AreEqual(2, folders.Items[0].Files!.Count);

            var downloadSummary = await backend.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(folders.Items[0].Ref, AlbumQuery: albumQuery));
            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.SourceJobId);

            await WaitForJobStateAsync(backend, downloadSummary.JobId, ServerProtocol.JobStates.Done);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Artist - Track One.mp3", "02. Artist - Track Two.mp3" }, downloaded);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_SubmitExtract_UsesClientDownloadSettingsDelta()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-album-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-album-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        TextWriter originalOut = Console.Out;
        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var summary = await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    "Artist Album",
                    "String",
                    Options: new SubmissionOptionsDto(
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch(["-a", "--no-browse-folder"]))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);

            await WaitForConditionAsync(
                () => Task.FromResult(Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length >= 2),
                "Timed out waiting for extracted album downloads to appear on disk.");

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Track One.mp3", "02. Track Two.mp3" }, downloaded);

            using var output = new StringWriter();
            Console.SetOut(output);
            SockseekLog.AddConsole(writer: (message, _) => Console.WriteLine(message));
            SockseekLog.SetConsoleLogLevel(LogLevel.Information);
            await Sockseek.Cli.Program.PrintRemoteCompleteAsync(backend, summary.WorkflowId, CancellationToken.None);
            Assert.AreEqual(
                string.Empty,
                output.ToString(),
                "A single successful user-facing album completion is intentionally not summarized.");
        }
        finally
        {
            SockseekLog.RemoveNonFileOutputs();
            Console.SetOut(originalOut);
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveCliCoordinator_FromListSerializesPromptsAndDownloadsSelections()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-" + Guid.NewGuid() + ".txt");
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-out-" + Guid.NewGuid());
        string albumOneDir = Path.Combine(musicRoot, "Artist One", "Album One");
        string albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Album Two");
        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumOneDir, "01. Artist One - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumTwoDir, "01. Artist Two - Track Two.mp3"), "b");
        File.WriteAllLines(inputPath, ["a:\"Artist One - Album One\"", "a:\"Artist Two - Album Two\""]);

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = new ProfileCatalog
            {
                AutoProfiles =
                [
                    new SettingsProfile
                    {
                        Name = "interactive-context",
                        Condition = "interactive",
                    },
                ],
            },
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            int activePickers = 0;
            int maxActivePickers = 0;
            int pickerCalls = 0;
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                CancellationToken.None,
                async request =>
                {
                    var active = Interlocked.Increment(ref activePickers);
                    int observed;
                    do
                    {
                        observed = maxActivePickers;
                        if (active <= observed) break;
                    }
                    while (Interlocked.CompareExchange(ref maxActivePickers, active, observed) != observed);

                    try
                    {
                        await Task.Delay(25);
                        Interlocked.Increment(ref pickerCalls);
                        var folder = request.Folders.First();
                        return new InteractiveModeManager.RunResult(
                            0,
                            folder,
                            RetrieveCurrentFolder: true,
                            ExitInteractiveMode: false,
                            request.FilterStr);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activePickers);
                    }
                },
                pollInterval: TimeSpan.FromMilliseconds(10));

            var summary = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    inputPath,
                    "List",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        ProfileContext: new Dictionary<string, bool> { ["interactive"] = true },
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch([inputPath, "--input-type", "list", "--no-browse-folder"]))),
                CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await coordinator.RunUntilCompleteAsync(summary.WorkflowId, timeout.Token);

            Assert.AreEqual(2, pickerCalls, "Both extracted album searches should reach the interactive picker.");
            Assert.AreEqual(1, maxActivePickers, "Remote interactive album prompts must not overlap.");

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Artist One - Track One.mp3", "01. Artist Two - Track Two.mp3" }, downloaded);
        }
        finally
        {
            await app.StopAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveCliCoordinator_FromListPreservesLineConditionsBeforePrompt()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-cond-" + Guid.NewGuid() + ".txt");
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-cond-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-cond-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Album Artist", "Album Name");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. file1.mp3"), "a");
        File.WriteAllLines(inputPath, ["a:\"Album Name\"                 strict-album=true;format=flac"]);

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            int pickerCalls = 0;
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                CancellationToken.None,
                request =>
                {
                    Interlocked.Increment(ref pickerCalls);
                    var folder = request.Folders.FirstOrDefault();
                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        folder == null ? -1 : 0,
                        folder,
                        RetrieveCurrentFolder: true,
                        ExitInteractiveMode: false,
                        request.FilterStr));
                },
                pollInterval: TimeSpan.FromMilliseconds(10));

            var summary = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    inputPath,
                    "List",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch([inputPath, "--input-type", "list", "--no-browse-folder"]))),
                CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await coordinator.RunUntilCompleteAsync(summary.WorkflowId, timeout.Token);

            Assert.AreEqual(0, pickerCalls, "The MP3 folder must be filtered out by the list-line FLAC condition before prompting.");
            Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Count(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await app.StopAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveCliCoordinator_AlbumAggregatePromptsEachBucket()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-agg-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-agg-out-" + Guid.NewGuid());
        string timeDir = Path.Combine(musicRoot, "ELO", "Time");
        string discoveryDir = Path.Combine(musicRoot, "ELO", "Discovery");
        Directory.CreateDirectory(timeDir);
        Directory.CreateDirectory(discoveryDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(timeDir, "01. ELO - Prologue.mp3"), "a");
        File.WriteAllText(Path.Combine(timeDir, "02. ELO - Twilight.mp3"), "b");
        File.WriteAllText(Path.Combine(discoveryDir, "01. ELO - Shine a Little Love.mp3"), "c");
        File.WriteAllText(Path.Combine(discoveryDir, "02. ELO - Confusion.mp3"), "d");
        File.WriteAllText(Path.Combine(discoveryDir, "03. ELO - Last Train to London.mp3"), "e");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                    MinSharesAggregate = 1,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var promptedBuckets = new List<string>();
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                CancellationToken.None,
                request =>
                {
                    promptedBuckets.Add(request.PromptJob.ToString(noInfo: true));
                    var folder = request.Folders.First();
                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        0,
                        folder,
                        RetrieveCurrentFolder: true,
                        ExitInteractiveMode: false,
                        request.FilterStr));
                },
                pollInterval: TimeSpan.FromMilliseconds(10));

            var summary = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    "ELO",
                    "String",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        ProfileContext: new Dictionary<string, bool> { ["interactive"] = true },
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch(["ELO", "--album", "--aggregate", "--min-shares-aggregate", "1", "--no-browse-folder"]))),
                CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await coordinator.RunUntilCompleteAsync(summary.WorkflowId, timeout.Token);

            Assert.AreEqual(2, promptedBuckets.Count, "Each album-aggregate bucket should reach the interactive picker.");
            Assert.IsTrue(promptedBuckets.Any(x => x.Contains("Time", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(promptedBuckets.Any(x => x.Contains("Discovery", StringComparison.OrdinalIgnoreCase)));

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "01. ELO - Prologue.mp3",
                "01. ELO - Shine a Little Love.mp3",
                "02. ELO - Confusion.mp3",
                "02. ELO - Twilight.mp3",
                "03. ELO - Last Train to London.mp3",
            }, downloaded);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_PrintCompleteCountsCancelledAlbumAsUserFacingFailure()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-cancel-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-cancel-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
            File.WriteAllBytes(Path.Combine(albumDir, $"{i:00}. Artist - Track {i:00}.mp3"), new byte[1024]);

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
                MockFilesSlow = true,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        TextWriter originalOut = Console.Out;
        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var searchSummary = await backend.SubmitAlbumSearchJobAsync(
            new SubmitAlbumSearchJobRequestDto(
                new AlbumQueryDto("Artist", "Album", "", "", false)));

            await WaitForJobStateAsync(backend, searchSummary.JobId, ServerProtocol.JobStates.Done);
            var projection = await backend.GetFolderResultsAsync(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(projection);
            Assert.AreEqual(1, projection.Items.Count);

            var downloadSummary = await backend.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(projection.Items[0].Ref));

            Assert.IsNotNull(downloadSummary);

            await WaitForConditionAsync(
                async () =>
                {
                    return (await GetChildSongPayloadsAsync(backend, downloadSummary.JobId))
                        .Any(file => file.State == ServerProtocol.JobStates.Downloading) == true;
                },
                "Timed out waiting for remote album file downloads to start.");

            Assert.IsTrue(await backend.CancelWorkflowAsync(downloadSummary.WorkflowId) > 0);
            await WaitForJobStateAsync(backend, downloadSummary.JobId, ServerProtocol.JobStates.Failed);

            using var output = new StringWriter();
            Console.SetOut(output);
            SockseekLog.AddConsole(writer: (message, _) => Console.WriteLine(message));
            SockseekLog.SetConsoleLogLevel(LogLevel.Information);
            await Sockseek.Cli.Program.PrintRemoteCompleteAsync(backend, downloadSummary.WorkflowId, CancellationToken.None);

            string rendered = output.ToString();
            StringAssert.Contains(
                rendered,
                "Completed: 0 succeeded, 1 failed.",
                "Remote completion output should match the live renderer's user-facing job counts.");
        }
        finally
        {
            SockseekLog.RemoveNonFileOutputs();
            Console.SetOut(originalOut);
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_CancelJobByDisplayId_WhenScopedToWorkflow_DoesNotCancelOtherWorkflow()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-scoped-cancel-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-scoped-cancel-out-" + Guid.NewGuid());
        string albumOneDir = Path.Combine(musicRoot, "Artist One", "Album One");
        string albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Album Two");
        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
        {
            File.WriteAllBytes(Path.Combine(albumOneDir, $"{i:00}. Artist One - Track {i:00}.mp3"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(albumTwoDir, $"{i:00}. Artist Two - Track {i:00}.mp3"), new byte[1024]);
        }

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
                MockFilesSlow = true,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var firstSearch = await StartAlbumSearchAsync(backend, "Artist One", "Album One");
            var secondSearch = await StartAlbumSearchAsync(backend, "Artist Two", "Album Two");
            await WaitForJobStateAsync(backend, firstSearch.JobId, ServerProtocol.JobStates.Done);
            await WaitForJobStateAsync(backend, secondSearch.JobId, ServerProtocol.JobStates.Done);

            var firstDownload = await StartFirstAlbumDownloadAsync(backend, firstSearch.JobId);
            var secondDownload = await StartFirstAlbumDownloadAsync(backend, secondSearch.JobId);

            await WaitForAlbumFileDownloadToStartAsync(backend, firstDownload.JobId);
            await WaitForAlbumFileDownloadToStartAsync(backend, secondDownload.JobId);

            Assert.IsFalse(
                await backend.CancelJobByDisplayIdAsync(secondDownload.DisplayId, firstDownload.WorkflowId),
                "A remote CLI scoped to one workflow must not cancel another workflow's display id.");

            Assert.IsTrue(await backend.CancelJobByDisplayIdAsync(firstDownload.DisplayId, firstDownload.WorkflowId));
            await WaitForJobStateAsync(backend, firstDownload.JobId, ServerProtocol.JobStates.Failed);

            var secondDetail = await backend.GetJobDetailAsync(secondDownload.JobId);
            Assert.AreNotEqual(
                ServerProtocol.JobStates.Failed,
                secondDetail?.Summary.State,
                "Cancelling the first workflow by display id must not fail the second workflow's job.");

            await backend.CancelWorkflowAsync(secondDownload.WorkflowId);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_PrintResults_RendersCompletedSearchPayloadWithoutDownloading()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.mp3"), "a");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        TextWriter originalOut = Console.Out;
        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var printSettings = new DownloadSettings
            {
                PrintOption = PrintOption.Results,
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            };

            var summary = await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    "Artist - Track One",
                    "String",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch(["Artist - Track One", "--print-results"]))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);

            using var output = new StringWriter();
            Console.SetOut(output);
            await Sockseek.Cli.Program.PrintRemoteResultsAsync(backend, summary.WorkflowId, printSettings, CancellationToken.None);

            string rendered = output.ToString();
            StringAssert.Contains(rendered, "Results for Artist - Track One");
            StringAssert.Contains(rendered, "Artist - Track One.mp3");
            Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                "Remote print-results mode should not download files.");
        }
        finally
        {
            Console.SetOut(originalOut);
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_PrintTracks_RendersPlannedTracksFromWorkflowSnapshot()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-tracks-" + Guid.NewGuid() + ".txt");
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-tracks-out-" + Guid.NewGuid());
        Directory.CreateDirectory(outputDir);
        string existingAlbumDir = Path.Combine(outputDir, "Artist Two", "Album Two");
        Directory.CreateDirectory(existingAlbumDir);
        File.WriteAllText(Path.Combine(outputDir, "Artist One - Track One.mp3"), "already here");
        File.WriteAllText(Path.Combine(existingAlbumDir, "01. Artist Two - Album Track.mp3"), "already here");
        File.WriteAllLines(inputPath, ["\"Artist One - Track One\"", "a:\"Artist Two - Album Two\""]);

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings(),
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
                Skip =
                {
                    SkipMode = SkipMode.Name,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        TextWriter originalOut = Console.Out;
        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var printSettings = new DownloadSettings
            {
                PrintOption = PrintOption.Tracks,
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            };

            var summary = await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    inputPath,
                    "List",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch([inputPath, "--input-type", "list", "--print-tracks"]))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);

            using var output = new StringWriter();
            Console.SetOut(output);
            await Sockseek.Cli.Program.PrintRemotePlannedOutputAsync(backend, summary.WorkflowId, printSettings, CancellationToken.None);

            string rendered = output.ToString();
            StringAssert.Contains(rendered, "Artist One - Track One");
            StringAssert.Contains(rendered, "Artist Two - Album Two");
            StringAssert.Contains(rendered, "already exist");
            Assert.AreEqual(2, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                "Remote print-tracks mode should not download files.");
        }
        finally
        {
            Console.SetOut(originalOut);
            await app.StopAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }


    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_SubmitJobList_SerializesTypedChildItems()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-list-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-list-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.mp3"), "a");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var summary = await backend.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "batch",
                    [
                        new TrackSearchJobDraftDto(
                            new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                    ]));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);

            var jobs = await backend.GetJobsAsync(new JobQuery(null, null, summary.WorkflowId, IncludeAll: true));
            Assert.IsTrue(jobs.Any(job => job.Kind == ServerJobKind.JobList));
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static async Task WaitForJobStateAsync(ICliBackend backend, Guid jobId, ServerJobState expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var detail = await backend.GetJobDetailAsync(jobId, CancellationToken.None);
            if (detail?.Summary.State == expectedState)
                return;

            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Fail($"Timed out waiting for job {jobId} to reach state '{expectedState}'.");
    }

    private static async Task WaitForWorkflowStateAsync(ICliBackend backend, Guid workflowId, ServerWorkflowState expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var detail = await backend.GetWorkflowAsync(workflowId, CancellationToken.None);
            if (detail?.Summary.State == expectedState)
                return;

            await Task.Delay(50, CancellationToken.None);
        }

        var finalDetail = await backend.GetWorkflowAsync(workflowId, CancellationToken.None);
        string jobs = finalDetail == null
            ? "<missing>"
            : string.Join(", ", finalDetail.Jobs.Select(job => $"[{job.DisplayId}] {job.Kind}:{job.State} parent={job.ParentJobId?.ToString() ?? "-"} result={job.ResultJobId?.ToString() ?? "-"}"));
        Assert.Fail($"Timed out waiting for workflow {workflowId} to reach state '{expectedState}'. Jobs: {jobs}");
    }

    private static async Task WaitForAlbumFileDownloadToStartAsync(ICliBackend backend, Guid albumJobId)
    {
        await WaitForConditionAsync(
                async () =>
                {
                    return (await GetChildSongPayloadsAsync(backend, albumJobId))
                        .Any(file => file.State == ServerProtocol.JobStates.Downloading) == true;
                },
                "Timed out waiting for remote album file downloads to start.");
    }

    private static async Task<List<SongJobPayloadDto>> GetChildSongPayloadsAsync(ICliBackend backend, Guid parentJobId)
    {
        var parent = await backend.GetJobDetailAsync(parentJobId);
        var payloads = new List<SongJobPayloadDto>();
        foreach (var child in parent?.Children ?? [])
        {
            var detail = await backend.GetJobDetailAsync(child.JobId);
            if (detail?.Payload is SongJobPayloadDto song)
                payloads.Add(song);
        }

        return payloads;
    }

    private static async Task<JobSummaryDto> StartAlbumSearchAsync(ICliBackend backend, string artist, string album)
        => await backend.SubmitAlbumSearchJobAsync(
            new SubmitAlbumSearchJobRequestDto(
                new AlbumQueryDto(artist, album, "", "", false)));

    private static async Task<JobSummaryDto> StartFirstAlbumDownloadAsync(ICliBackend backend, Guid searchJobId)
    {
        var projection = await backend.GetFolderResultsAsync(searchJobId, includeFiles: false);
        Assert.IsNotNull(projection);
        Assert.IsTrue(projection.Items.Count > 0);

        var summary = await backend.StartFolderDownloadAsync(
            searchJobId,
            new StartFolderDownloadRequestDto(projection.Items[0].Ref));
        Assert.IsNotNull(summary);
        return summary;
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> condition, string failureMessage, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            if (await condition())
                return;

            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Fail(failureMessage);
    }
}
