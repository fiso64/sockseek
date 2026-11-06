using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Server;
using System.Collections.Concurrent;
using Sockseek.Api;

namespace Tests.Cli;

[TestClass]
public class LocalCliBackendTests
{
    [TestMethod]
    public async Task LocalCliBackend_RemoteDraftRejectsMusicOnlyOverrideBeforeEnqueue()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-local-remote-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var engineSettings = new EngineSettings();
            var settings = new DownloadSettings { Output = { ParentDir = root } };
            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine, settings);
            var request = new SubmitJobListRequestDto(
                "invalid remote settings",
                [
                    new RemoteFileJobDraftDto(
                        new PeerFileTargetDto("Peer", @"Share\File.bin", 4, ".bin"),
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(WritePlaylist: true))),
                ]);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                backend.SubmitJobListAsync(request));
            Assert.AreEqual(0, engine.Queue.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LocalCliBackend_SoulseekDraftSettingsValidationUsesEffectiveInterpretation()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-local-slsk-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var engineSettings = new EngineSettings();
            var settings = new DownloadSettings { Output = { ParentDir = root } };
            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine, settings);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => backend.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "ordinary",
                    [new JobListJobDraftDto("nested", [new ExtractJobDraftDto(
                        "slsk://Peer/Share/File.bin",
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(WritePlaylist: true)))])])));

            JobSummaryDto accepted = await backend.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "music",
                    [new ExtractJobDraftDto(
                        "slsk://Peer/Share/File.mp3",
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(
                                NameFormat: "{artist}/{title}",
                                WritePlaylist: true),
                            Extraction: new ExtractionSettingsPatchDto(RequestedMode: ServerExtractionMode.Song)))]));

            Assert.AreEqual(ServerJobKind.JobList, accepted.Kind);
            Assert.AreNotEqual(Guid.Empty, accepted.WorkflowId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LocalCliBackend_ExplicitCliMusicNameFormatRejectsOrdinarySoulseekLink()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-local-slsk-name-format-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var engineSettings = new EngineSettings();
            var settings = new DownloadSettings { Output = { ParentDir = root } };
            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var explicitCliSettings = new DownloadSettingsPatchDto(
                Output: new OutputSettingsPatchDto(NameFormat: "{artist}/{filename}"));
            var backend = new LocalCliBackend(
                engine,
                settings,
                explicitCliDownloadSettings: explicitCliSettings);

            var exception = await Assert.ThrowsExactlyAsync<UnsupportedNameFormatVariableException>(() =>
                backend.SubmitExtractJobAsync(
                    new SubmitExtractJobRequestDto("slsk://Peer/Share/File.bin")));

            Assert.AreEqual("artist", exception.Variable);
            Assert.AreEqual(0, engine.Queue.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LocalCliBackend_ObservesSearchJobsThroughServerShapedModel()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        Directory.CreateDirectory(trackDir);
        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = musicRoot,
                    IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(musicRoot, "failed") },
                },
            };
            downloadSettings.Extraction.Input = "test";
            var clientManager = new SoulseekClientManager(engineSettings);
            await using var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine, downloadSettings);
            var seenUpdates = new ConcurrentBag<DaemonClientUpdate>();
            backend.StateUpdated += update => seenUpdates.Add(update);

            var submitted = await backend.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                cts.Token);
            engine.CompleteEnqueue();

            await engine.RunAsync(cts.Token);

            var jobs = await backend.GetJobsAsync(new JobQuery(null, null, ServerJobKind.Search, null, IncludeAll: true));
            Assert.AreEqual(1, jobs.Count);
            Assert.AreEqual(submitted.JobId, jobs[0].JobId);

            var projection = await backend.GetFileResultsAsync(submitted.JobId);
            Assert.IsNotNull(projection);
            Assert.AreEqual(1, projection.Items.Count);

            Assert.IsTrue(seenUpdates.Any(update => update.ChangedJobs.Any(job => job.JobId == submitted.JobId)));
            Assert.IsTrue(seenUpdates.Any(update => update.ChangedWorkflows.Any(workflow => workflow.WorkflowId == submitted.WorkflowId)));
            Assert.IsTrue(seenUpdates.Any(update => update.State.Searches.Any(search => search.JobId == submitted.JobId)));
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task InteractiveCliCoordinator_FromListPreservesLineConditionsBeforePrompt()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "Sockseek-local-interactive-cond-" + Guid.NewGuid() + ".txt");
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-local-interactive-cond-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-local-interactive-cond-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Album Artist", "Album Name");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. file1.mp3"), "a");
        File.WriteAllLines(inputPath, ["a:\"Album Name\"                 strict-album=true;format=flac"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
            };

            var clientManager = new SoulseekClientManager(engineSettings);
            string[] args =
            [
                inputPath,
                "--input-type", "list",
                "--mock-files-dir", musicRoot,
                "--mock-files-no-read-tags",
                "-p", outputDir,
                "--name-format", "{foldername}/{filename}",
                "--no-progress",
                "-t",
            ];
            var resolver = new SubmissionOptionsJobSettingsResolver(
                ConfigManager.CreateJobSettingsResolver(new ConfigFile("none", []), args, new CliSettings { InteractiveMode = true, NoProgress = true }),
                normalize: settings => SettingsNormalizer.NormalizeDownloadPaths(settings, settings.RuntimePathContext));
            await using var engine = new DownloadEngine(engineSettings, clientManager, resolver);
            var backend = new LocalCliBackend(engine, downloadSettings, resolver);
            var engineTask = engine.RunAsync(cts.Token);

            int pickerCalls = 0;
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                cts.Token,
                request =>
                {
                    Interlocked.Increment(ref pickerCalls);
                    var folder = request.Folders.FirstOrDefault();
                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        folder == null
                            ? InteractiveModeManager.RunAction.SkipCurrent
                            : InteractiveModeManager.RunAction.Accept,
                        folder == null ? -1 : 0,
                        folder,
                        RetrieveCurrentFolder: true,
                        request.FilterStr));
                },
                pollInterval: TimeSpan.FromMilliseconds(10));

            var summary = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    inputPath,
                    "List",
                    Options: new SubmissionOptionsDto(Guid.NewGuid())),
                cts.Token);

            await coordinator.RunUntilCompleteAsync(summary.WorkflowId, cts.Token);

            Assert.AreEqual(0, pickerCalls, "The MP3 folder must be filtered out by the list-line FLAC condition before prompting.");
            Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Count(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)));

            engine.CompleteEnqueue();
            await engineTask;
        }
        finally
        {
            cts.Cancel();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task LocalCliBackend_RetrieveFolderAndWaitAsync_ReturnsNewFilesFoundCount()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-retrieve-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = musicRoot,
                    IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(musicRoot, "failed") },
                },
            };
            downloadSettings.Search.NecessaryCond.StrictTitle = true;

            var clientManager = new SoulseekClientManager(engineSettings);
            await using var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new AlbumQuery
            {
                Artist = "Artist",
                Album = "Album",
                SearchHint = "Track One",
            });

            await backend.SubscribeWorkflowAsync(searchJob.WorkflowId, cts.Token);
            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                backend,
                () => searchJob.TerminalOutcome == JobTerminalOutcome.Succeeded,
                "Timed out waiting for the album search to complete.");

            var initialProjection = await backend.GetFolderResultsAsync(searchJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(initialProjection);
            Assert.AreEqual(1, initialProjection.Items.Count);
            Assert.AreEqual(1, initialProjection.Items[0].Files?.Count);

            var retrieved = await backend.RetrieveFolderAndWaitAsync(
                searchJob.Id,
                new RetrieveFolderRequestDto(initialProjection.Items[0].Ref),
                cts.Token);

            Assert.IsNotNull(retrieved);
            Assert.AreEqual(1, retrieved.NewFilesFoundCount);

            var expandedProjection = await backend.GetFolderResultsAsync(searchJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(expandedProjection);
            Assert.AreEqual(2, expandedProjection.Items[0].Files?.Count);
            Assert.AreEqual(2, expandedProjection.Items[0].FileCount);
            Assert.AreEqual(2, expandedProjection.Items[0].AudioFileCount);

            engine.CompleteEnqueue();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }


    [TestMethod]
    public async Task LocalCliBackend_RetrieveFolderAndWaitAsync_UpdatesManualAlbumJobResults()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-album-retrieve-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = musicRoot,
                    IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(musicRoot, "failed") },
                },
            };
            downloadSettings.Search.NecessaryCond.StrictTitle = true;

            await using var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine);

            var albumJob = new AlbumJob(new AlbumQuery
            {
                Artist = "Artist",
                Album = "Album",
                SearchHint = "Track One",
            })
            {
                DownloadBehaviorPolicy = new DownloadBehaviorPolicy { Album = DownloadBehavior.Manual },
            };

            await backend.SubscribeWorkflowAsync(albumJob.WorkflowId, cts.Token);
            engine.Enqueue(albumJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                backend,
                () => albumJob.IsAwaitingSelection,
                "Timed out waiting for the manual album job to reach the picker.");

            var initialProjection = await backend.GetFolderResultsAsync(albumJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(initialProjection);
            Assert.AreEqual(1, initialProjection.Items.Count);
            Assert.AreEqual(1, initialProjection.Items[0].Files?.Count);
            Assert.AreEqual(1, albumJob.Results[0].Files.Count);

            var retrieved = await backend.RetrieveFolderAndWaitAsync(
                albumJob.Id,
                new RetrieveFolderRequestDto(initialProjection.Items[0].Ref),
                cts.Token);

            Assert.IsNotNull(retrieved);
            Assert.AreEqual(1, retrieved.NewFilesFoundCount);
            Assert.AreEqual(2, albumJob.Results[0].Files.Count, "Folder retrieval must update the canonical AlbumJob results, not only a projected copy.");

            var expandedProjection = await backend.GetFolderResultsAsync(albumJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(expandedProjection);
            Assert.AreEqual(2, expandedProjection.Items[0].Files?.Count);
            Assert.AreEqual(2, expandedProjection.Items[0].FileCount);
            Assert.AreEqual(2, expandedProjection.Items[0].AudioFileCount);

            engine.CompleteEnqueue();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task LocalCliBackend_StartFolderDownloadAsync_UsesRetrievedFolderSnapshotWithoutRebrowse()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-parent-retrieve-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-parent-retrieve-out-" + Guid.NewGuid());
        string disc1 = Path.Combine(musicRoot, "Artist", "Album", "Disc 1");
        string disc2 = Path.Combine(musicRoot, "Artist", "Album", "Disc 2");
        Directory.CreateDirectory(disc1);
        Directory.CreateDirectory(disc2);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(disc1, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(disc2, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(outputDir, "failed") },
                },
            };
            downloadSettings.Search.NecessaryCond.StrictTitle = true;

            await using var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new AlbumQuery
            {
                Artist = "Artist",
                Album = "Album",
                SearchHint = "Track One",
            });

            await backend.SubscribeWorkflowAsync(searchJob.WorkflowId, cts.Token);
            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                backend,
                () => searchJob.TerminalOutcome == JobTerminalOutcome.Succeeded,
                "Timed out waiting for the album search to complete.");

            var retrieved = await backend.RetrieveFolderAndWaitAsync(
                searchJob.Id,
                new RetrieveFolderRequestDto(new AlbumFolderRefDto("local", @"Artist\Album")),
                cts.Token);

            Assert.IsNotNull(retrieved);
            var retrievedProjection = await backend.GetFolderResultsAsync(
                searchJob.Id,
                includeFiles: true,
                cts.Token);
            var retrievedFolder = retrievedProjection?.Items.Single();
            Assert.IsNotNull(retrievedFolder);
            Assert.IsTrue(retrievedFolder.IsFullyRetrieved);
            Assert.AreEqual(2, retrievedFolder.Files?.Count);
            Assert.AreEqual(2, retrievedFolder.FileCount);
            Assert.AreEqual(2, retrievedFolder.AudioFileCount);

            var downloadSummary = await backend.StartFolderDownloadAsync(
                searchJob.Id,
                new StartFolderDownloadRequestDto(
                    retrievedFolder.Ref,
                    AlbumQuery: new AlbumQueryDto("Artist", "Album", "Track One", null, false)),
                cts.Token);

            Assert.IsNotNull(downloadSummary);
            AlbumJob? albumJob = null;
            await WaitForConditionAsync(
                backend,
                () =>
                {
                    albumJob = (AlbumJob?)engine.GetJob(downloadSummary.JobId);
                    return albumJob != null;
                },
                "Timed out waiting for selected album job to be registered.");

            Assert.IsNotNull(albumJob);
            Assert.IsNotNull(albumJob.ResolvedTarget);
            Assert.IsTrue(albumJob.ResolvedTarget.IsFullyRetrieved, "The selected retrieved folder must remain fully retrieved after handoff to album download.");
            Assert.AreEqual(2, albumJob.ResolvedTarget.Files.Count, "The download should use the retrieved folder snapshot, not reconstruct a partial folder from old search results.");

            await WaitForConditionAsync(
                backend,
                () => albumJob.IsTerminal,
                "Timed out waiting for selected album download to complete.");

            var retrieveJobs = await backend.GetJobsAsync(
                new JobQuery(null, null, ServerJobKind.RetrieveFolder, null, IncludeAll: true),
                cts.Token);
            Assert.AreEqual(1, retrieveJobs.Count, "A fully retrieved interactive selection should not be browsed again before or after album download.");

            engine.CompleteEnqueue();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task LocalCliBackend_PublishesTypedStateAndCompactActivity_ForSongDownload()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-progress-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-progress-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                    IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(outputDir, "failed") },
                },
            };

            var clientManager = new SoulseekClientManager(engineSettings);
            await using var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine, downloadSettings);
            var updates = new ConcurrentBag<DaemonClientUpdate>();
            var activity = new ConcurrentBag<ActivityEventDto>();
            backend.StateUpdated += update => updates.Add(update);
            backend.ActivityReceived += item => activity.Add(item);

            await backend.SubmitSongJobAsync(
                new SubmitSongJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                cts.Token);

            engine.CompleteEnqueue();
            await engine.RunAsync(cts.Token);

            Assert.IsTrue(updates.Any(update => update.ChangedJobs.Any()));
            Assert.IsTrue(updates.Any(update => update.ChangedTransfers.Any()));
            Assert.IsTrue(updates
                .SelectMany(update => update.ChangedJobs)
                .Any(job => job.Kind == ServerJobKind.Song
                    && job.LifecycleState == ServerJobLifecycleState.Terminal));
            Assert.IsFalse(activity.Any(item => item.Type is "download.started" or "download.state-changed" or "song.state-changed"));
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task GetAggregateTrackResultsAsync_WithIncludeCandidates_PopulatesCandidatesPerGroup()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-agg-track-" + Guid.NewGuid());
        // Same filename in two different folders → same inferred (artist, title) and same hash-derived length.
        // They end up as two candidates for the same aggregate group.
        Directory.CreateDirectory(Path.Combine(musicRoot, "Folder1"));
        Directory.CreateDirectory(Path.Combine(musicRoot, "Folder2"));
        File.WriteAllText(Path.Combine(musicRoot, "Folder1", "Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(musicRoot, "Folder2", "Artist - Track One.mp3"), "b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings { MockFilesDir = musicRoot, MockFilesReadTags = false };
            var downloadSettings = new DownloadSettings
            {
                Output = { ParentDir = musicRoot, IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(musicRoot, "failed") } },
                Search = { MinSharesAggregate = 1 },
            };

            await using var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track One" });
            await backend.SubscribeWorkflowAsync(searchJob.WorkflowId, cts.Token);
            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                backend,
                () => searchJob.TerminalOutcome == JobTerminalOutcome.Succeeded,
                "Timed out waiting for aggregate track search to complete.");

            var result = await backend.GetAggregateTrackResultsAsync(
                searchJob.Id,
                new AggregateTrackProjectionRequestDto(IncludeCandidates: true),
                cts.Token);

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Items.Count, "Expected one aggregate group for 'Artist - Track One'.");
            var group = result.Items[0];
            Assert.IsNotNull(group.Candidates, "Candidates should be populated when IncludeCandidates = true.");
            Assert.AreEqual(2, group.Candidates!.Count, "Both file versions should be included as candidates.");

            engine.CompleteEnqueue();
            cts.Cancel();
            await runTask;
        }
        finally
        {
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task GetAggregateAlbumResultsAsync_WithIncludeFolders_PopulatesFoldersPerBucket()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-cli-backend-agg-album-" + Guid.NewGuid());
        // Two album folders with the same track filename → same hash-derived length →
        // grouped into one aggregate bucket. MinSharesAggregate is set to 1 since both
        // folders belong to the single mock "local" user.
        Directory.CreateDirectory(Path.Combine(musicRoot, "Artist", "Album A"));
        Directory.CreateDirectory(Path.Combine(musicRoot, "Artist", "Album B"));
        File.WriteAllText(Path.Combine(musicRoot, "Artist", "Album A", "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(musicRoot, "Artist", "Album B", "01. Artist - Track One.mp3"), "b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings { MockFilesDir = musicRoot, MockFilesReadTags = false };
            var downloadSettings = new DownloadSettings
            {
                Output = { ParentDir = musicRoot, IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(musicRoot, "failed") } },
                Search = { MinSharesAggregate = 1 },
            };

            await using var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new AlbumQuery { Artist = "Artist" });
            await backend.SubscribeWorkflowAsync(searchJob.WorkflowId, cts.Token);
            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                backend,
                () => searchJob.TerminalOutcome == JobTerminalOutcome.Succeeded,
                "Timed out waiting for aggregate album search to complete.");

            var result = await backend.GetAggregateAlbumResultsAsync(
                searchJob.Id,
                new AggregateAlbumProjectionRequestDto(IncludeFolders: true),
                cts.Token);

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Items.Count, "Expected one aggregate bucket for the two album versions.");
            var bucket = result.Items[0];
            Assert.IsNotNull(bucket.Folders, "Folders should be populated when IncludeFolders = true.");
            Assert.AreEqual(2, bucket.Folders!.Count, "Both album folder versions should appear in the bucket.");

            engine.CompleteEnqueue();
            cts.Cancel();
            await runTask;
        }
        finally
        {
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    private static Task WaitForConditionAsync(
        ICliBackend backend,
        Func<bool> condition,
        string failureMessage)
        => BackendTestWaiter.UntilAsync(
            backend,
            _ => Task.FromResult(condition()),
            failureMessage,
            timeoutMs: 10_000);


}
