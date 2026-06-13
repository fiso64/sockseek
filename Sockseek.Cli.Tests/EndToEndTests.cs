using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Cli;
using Sockseek.Server;
using Sockseek.Api;

namespace Tests.EndToEnd;

[TestClass]
public class CliEndToEndTests
{
    private static string? GetSoulseekFileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var lastSlash = path.LastIndexOfAny(['\\', '/']);
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }

    [TestMethod]
    public async Task AlbumDownload_CliPath_Completes()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-cli-" + Guid.NewGuid());
        var albumDir  = Path.Combine(musicRoot, "TestArtist", "TestAlbum");
        var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-cli-" + Guid.NewGuid());
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllBytes(Path.Combine(albumDir, "01. Track1.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(albumDir, "02. Track2.mp3"), [4, 5, 6]);

        var testArgs = new string[]
        {
            "--config",              "none",
            "--input",               "TestArtist TestAlbum",
            "--album",
            "--path",                outputDir,
            "--mock-files-dir",      musicRoot,
            "--mock-files-no-read-tags",
            "--user",                "test_user",
            "--pass",                "test_pass",
        };

        try
        {
            var configFile = ConfigManager.Load("none");
            var (engineSettings, rootSettings, _) = ConfigManager.Bind(configFile, testArgs);

            var clientManager = new SoulseekClientManager(engineSettings);
            var app           = new DownloadEngine(engineSettings, clientManager);
            app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
            app.CompleteEnqueue();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.RunAsync(cts.Token);

            Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out; connection was never initiated");

            var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            Assert.IsTrue(files.Length >= 2, $"Expected >=2 downloaded files, got {files.Length}");
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveAlbumSelection_FromList_IsSerialized()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-interactive-" + Guid.NewGuid());
        var albumOneDir = Path.Combine(musicRoot, "Artist One", "Album One");
        var albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Album Two");
        var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-interactive-" + Guid.NewGuid());
        var listPath = Path.Combine(Path.GetTempPath(), "slsk-list-" + Guid.NewGuid() + ".txt");

        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllBytes(Path.Combine(albumOneDir, "01. Artist One - Track One.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(albumTwoDir, "01. Artist Two - Track Two.mp3"), [4, 5, 6]);
        File.WriteAllText(listPath,
            "a:\"Artist One - Album One\"" + "\n" +
            "a:\"Artist Two - Album Two\"");

        try
        {
            var engineSettings = new EngineSettings
            {
                Username = "test_user",
                Password = "test_pass",
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var rootSettings = new DownloadSettings();
            rootSettings.Extraction.Input = listPath;
            rootSettings.Extraction.InputType = InputType.List;
            rootSettings.Search.NoBrowseFolder = true;
            rootSettings.Output.ParentDir = outputDir;
            rootSettings.Output.NameFormat = "{foldername}/{filename}";

            var cliSettings = new CliSettings { InteractiveMode = true, NoProgress = true };
            var clientManager = new SoulseekClientManager(engineSettings);
            var app = new DownloadEngine(engineSettings, clientManager);

            var activePickers = 0;
            var maxActivePickers = 0;
            var pickerCalls = 0;

            var backend = new LocalCliBackend(app, rootSettings);
            var coordinator = new InteractiveCliCoordinator(
                backend,
                cliSettings,
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
                        var folder = request.Folders.FirstOrDefault();
                        return new InteractiveModeManager.RunResult(
                            folder == null ? -1 : 0,
                            folder,
                            RetrieveCurrentFolder: true,
                            ExitInteractiveMode: false,
                            request.FilterStr);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activePickers);
                    }
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var workflowId = Guid.NewGuid();
            var submission = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType.ToString(), Options: new SubmissionOptionsDto(workflowId)),
                cts.Token);
            _ = coordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token)
                .ContinueWith(_ => app.CompleteEnqueue(), TaskScheduler.Default);
            await app.RunAsync(cts.Token);

            Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out");
            Assert.AreEqual(2, pickerCalls, "Both list album jobs should reach the interactive picker");
            Assert.AreEqual(1, maxActivePickers, "Interactive album prompts must not overlap");

            var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.AreEqual(2, files.Length, "Both selected albums should download.");
        }
        finally
        {
            if (File.Exists(listPath)) File.Delete(listPath);
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveAlbumSelection_SelectedFiles_DownloadsOnlySelectedFiles()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-interactive-selected-" + Guid.NewGuid());
        var albumDir = Path.Combine(musicRoot, "Artist", "Album");
        var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-interactive-selected-" + Guid.NewGuid());

        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Track One.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), [4, 5, 6]);

        try
        {
            var engineSettings = new EngineSettings
            {
                Username = "test_user",
                Password = "test_pass",
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var rootSettings = new DownloadSettings();
            rootSettings.Extraction.Input = "Artist Album";
            rootSettings.Extraction.InputType = InputType.String;
            rootSettings.Search.NecessaryFolderCond.MinTrackCount = 2;
            rootSettings.Output.ParentDir = outputDir;
            rootSettings.Output.NameFormat = "{foldername}/{filename}";

            var clientManager = new SoulseekClientManager(engineSettings);
            var app = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(app, rootSettings);
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                CancellationToken.None,
                request =>
                {
                    var folder = request.Folders.First();
                    var selected = folder.Files
                        .Where(song => song.ResolvedTarget?.Filename.EndsWith("Track Two.mp3", StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();

                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        0,
                        new AlbumFolder(folder.Username, folder.FolderPath, selected),
                        RetrieveCurrentFolder: false,
                        ExitInteractiveMode: false,
                        request.FilterStr));
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var workflowId = Guid.NewGuid();
            var submission = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType.ToString(), Options: new SubmissionOptionsDto(workflowId)),
                cts.Token);
            _ = coordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token)
                .ContinueWith(_ => app.CompleteEnqueue(), TaskScheduler.Default);
            await app.RunAsync(cts.Token);

            Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out");

            var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(GetSoulseekFileName)
                .OrderBy(x => x)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "02. Artist - Track Two.mp3" }, files);
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveAlbumSelection_CancelledChosenAlbum_DoesNotReprompt()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-reprompt-" + Guid.NewGuid());
        var albumOneDir = Path.Combine(musicRoot, "Artist One", "Shared Album");
        var albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Shared Album");
        var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-reprompt-" + Guid.NewGuid());

        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllBytes(Path.Combine(albumOneDir, "01. Artist One - Track One.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(albumTwoDir, "01. Artist Two - Track Two.mp3"), [4, 5, 6]);

        try
        {
            var engineSettings = new EngineSettings
            {
                Username = "test_user",
                Password = "test_pass",
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var rootSettings = new DownloadSettings();
            rootSettings.Extraction.Input = "Shared Album";
            rootSettings.Extraction.IsAlbum = true;
            rootSettings.Search.NoBrowseFolder = true;
            rootSettings.Output.ParentDir = outputDir;
            rootSettings.Output.NameFormat = "{foldername}/{filename}";

            var cliSettings = new CliSettings { InteractiveMode = true, NoProgress = true };
            var clientManager = new SoulseekClientManager(engineSettings);
            var app = new DownloadEngine(engineSettings, clientManager);

            var pickerCalls = 0;
            string? cancelledFolderKey = null;
            var cancellationIssued = 0;

            app.Events.JobStateChanged += job =>
            {
                if (job.ActivityPhase != JobActivityPhase.Downloading || job is not AlbumJob albumJob || albumJob.ResolvedTarget == null || cancelledFolderKey == null)
                    return;

                var key = albumJob.ResolvedTarget.Username + "\\" + albumJob.ResolvedTarget.FolderPath;
                if (!string.Equals(key, cancelledFolderKey, StringComparison.OrdinalIgnoreCase))
                    return;

                if (Interlocked.Exchange(ref cancellationIssued, 1) != 0)
                    return;

                albumJob.Cancel(JobCancellationSource.UserRequestedJob);
            };

            var backend = new LocalCliBackend(app, rootSettings);
            var coordinator = new InteractiveCliCoordinator(
                backend,
                cliSettings,
                CancellationToken.None,
                request =>
                {
                    pickerCalls++;
                    var first = request.Folders.First();
                    cancelledFolderKey = first.Username + "\\" + first.FolderPath;
                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        0,
                        first,
                        RetrieveCurrentFolder: true,
                        ExitInteractiveMode: false,
                        request.FilterStr));
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var workflowId = Guid.NewGuid();
            var submission = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType.ToString(), Options: new SubmissionOptionsDto(workflowId)),
                cts.Token);
            _ = coordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token)
                .ContinueWith(_ => app.CompleteEnqueue(), TaskScheduler.Default);
            await app.RunAsync(cts.Token);

            Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out");
            Assert.AreEqual(1, pickerCalls, "A cancelled chosen album should NOT reopen the picker.");
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveAlbumSelection_FailedChosenAlbum_RepromptsWithoutFailedFolder()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-fail-reprompt-" + Guid.NewGuid());
        var albumOneDir = Path.Combine(musicRoot, "Source One", "Shared Album");
        var albumTwoDir = Path.Combine(musicRoot, "Source Two", "Shared Album");
        var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-fail-reprompt-" + Guid.NewGuid());

        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);

        // Identical filenames so they look like perfect alternate sources
        string doomedFilePath = Path.Combine(albumOneDir, "01. Artist - Track.mp3");
        File.WriteAllBytes(doomedFilePath, [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(albumTwoDir, "01. Artist - Track.mp3"), [4, 5, 6]);

        try
        {
            var engineSettings = new EngineSettings
            {
                Username = "test_user",
                Password = "test_pass",
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var rootSettings = new DownloadSettings();
            rootSettings.Extraction.Input = "Shared Album";
            rootSettings.Extraction.IsAlbum = true;
            rootSettings.Search.NoBrowseFolder = true;
            rootSettings.Output.ParentDir = outputDir;
            rootSettings.Output.NameFormat = "{foldername}/{filename}";
            rootSettings.Transfer.MaxDownloadRetries = 0; // Fail quickly

            var cliSettings = new CliSettings { InteractiveMode = true, NoProgress = true };
            var clientManager = new SoulseekClientManager(engineSettings);
            var app = new DownloadEngine(engineSettings, clientManager);

            var pickerCalls = 0;
            var backend = new LocalCliBackend(app, rootSettings);
            var coordinator = new InteractiveCliCoordinator(
                backend,
                cliSettings,
                CancellationToken.None,
                request =>
                {
                    pickerCalls++;

                    Assert.IsTrue(request.Folders.Count >= 1, "Expected at least 1 folder candidate available to pick.");
                    var folder = request.Folders.First();

                    // We delete the file here so the download fails
                    if (pickerCalls == 1 && File.Exists(doomedFilePath))
                    {
                        File.Delete(doomedFilePath);
                    }

                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        0,
                        folder,
                        RetrieveCurrentFolder: true,
                        ExitInteractiveMode: false,
                        request.FilterStr));
                });

            var workflowId = Guid.NewGuid();
            var submission = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType.ToString(), Options: new SubmissionOptionsDto(workflowId)),
                CancellationToken.None);
            _ = coordinator.RunUntilCompleteAsync(submission.WorkflowId, CancellationToken.None)
                .ContinueWith(_ => app.CompleteEnqueue(), TaskScheduler.Default);
            await app.RunAsync(CancellationToken.None);

            Assert.AreEqual(2, pickerCalls, "A failed chosen album should reopen the picker with remaining candidates.");

            var albumJobs = app.Queue.AllJobs().OfType<AlbumJob>().ToList();
            Assert.AreEqual(1, albumJobs.Count, "Interactive retry should reuse the extracted AlbumJob instead of creating a follow-up root job.");
            var albumJob = albumJobs[0];
            Assert.AreEqual(JobTerminalOutcome.Succeeded, albumJob.TerminalOutcome, "Album should eventually succeed with the remaining folder.");

            var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            Assert.AreEqual(1, files.Length, "Only the retry selection should complete.");
            Assert.IsTrue(files[0].EndsWith("Track.mp3"));
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }
    [TestMethod]
    public async Task InteractiveCsvAlbumSelection_RemoveFromSource_ClearsSuccessfulAlbumRows()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-interactive-csv-rfs-" + Guid.NewGuid());
        var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-interactive-csv-rfs-" + Guid.NewGuid());
        var csvPath = Path.Combine(Path.GetTempPath(), "slsk-albums-" + Guid.NewGuid() + ".csv");

        Directory.CreateDirectory(Path.Combine(musicRoot, "Sonic Youth", "Daydream Nation"));
        Directory.CreateDirectory(Path.Combine(musicRoot, "Mitski", "Be the Cowboy"));
        Directory.CreateDirectory(outputDir);

        for (var i = 1; i <= 8; i++)
            File.WriteAllBytes(Path.Combine(musicRoot, "Sonic Youth", "Daydream Nation", $"{i:00}. Sonic Youth - Track {i}.mp3"), [1, 2, 3]);
        for (var i = 1; i <= 12; i++)
            File.WriteAllBytes(Path.Combine(musicRoot, "Mitski", "Be the Cowboy", $"{i:00}. Mitski - Track {i}.mp3"), [4, 5, 6]);

        File.WriteAllText(csvPath,
            "Artist,Album,TrackCount\n" +
            "Sonic Youth,Daydream Nation,8\n" +
            "Mitski,Be the Cowboy,12\n" +
            "Talking Heads,Remain in Light,7\n");

        try
        {
            var engineSettings = new EngineSettings
            {
                Username = "test_user",
                Password = "test_pass",
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var rootSettings = new DownloadSettings();
            rootSettings.Extraction.Input = csvPath;
            rootSettings.Extraction.InputType = InputType.None;
            rootSettings.Extraction.RemoveTracksFromSource = true;
            rootSettings.Csv.ArtistCol = "Artist";
            rootSettings.Csv.AlbumCol = "Album";
            rootSettings.Csv.TrackCountCol = "TrackCount";
            rootSettings.Search.NoBrowseFolder = true;
            rootSettings.Output.ParentDir = outputDir;
            rootSettings.Output.NameFormat = "{foldername}/{filename}";

            var cliSettings = new CliSettings { InteractiveMode = true, NoProgress = true };
            var clientManager = new SoulseekClientManager(engineSettings);
            var app = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(app, rootSettings);

            var pickerCalls = 0;
            var coordinator = new InteractiveCliCoordinator(
                backend,
                cliSettings,
                CancellationToken.None,
                request =>
                {
                    pickerCalls++;
                    var folder = request.Folders.FirstOrDefault();
                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        folder == null ? -1 : 0,
                        folder,
                        RetrieveCurrentFolder: true,
                        ExitInteractiveMode: false,
                        request.FilterStr));
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var workflowId = Guid.NewGuid();
            var submission = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(csvPath, InputType.None.ToString(), Options: new SubmissionOptionsDto(workflowId)),
                cts.Token);
            _ = coordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token)
                .ContinueWith(_ => app.CompleteEnqueue(), TaskScheduler.Default);
            await app.RunAsync(cts.Token);

            Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out");
            Assert.AreEqual(2, pickerCalls, "Only the two found albums should reach the interactive picker.");

            var lines = File.ReadAllLines(csvPath);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Artist,Album,TrackCount",
                    ",,",
                    ",,",
                    "Talking Heads,Remain in Light,7",
                },
                lines,
                "Interactive mode must clear successful CSV album rows using the original extracted jobs.");

            var indexPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(csvPath), "_index.csv");
            Assert.IsTrue(File.Exists(indexPath), $"Expected interactive CSV run to update list index at {indexPath}");
            var indexLines = File.ReadAllLines(indexPath).Select(line => line.Replace('\\', '/')).ToList();

            Assert.IsTrue(indexLines.Any(line => line.Contains("Sonic Youth,Daydream Nation,,-1,1,1,0")),
                string.Join("\n", indexLines));
            Assert.IsTrue(indexLines.Any(line => line.Contains("Mitski,Be the Cowboy,,-1,1,1,0")),
                string.Join("\n", indexLines));
            Assert.IsTrue(indexLines.Any(line => line == ",Talking Heads,Remain in Light,,-1,1,2,3"),
                string.Join("\n", indexLines));
        }
        finally
        {
            if (File.Exists(csvPath)) File.Delete(csvPath);
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

}
