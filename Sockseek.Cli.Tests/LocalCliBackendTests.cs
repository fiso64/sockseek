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
                    FailedAlbumPath = Path.Combine(musicRoot, "failed"),
                },
            };
            downloadSettings.Extraction.Input = "test";
            var clientManager = new SoulseekClientManager(engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine, downloadSettings);
            var seenEvents = new ConcurrentBag<ServerEventEnvelopeDto>();
            backend.EventReceived += envelope => seenEvents.Add(envelope);

            var submitted = await backend.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                cts.Token);
            engine.CompleteEnqueue();

            await engine.RunAsync(cts.Token);

            var jobs = await backend.GetJobsAsync(new JobQuery(null, ServerJobKind.Search, null, IncludeAll: true));
            Assert.AreEqual(1, jobs.Count);
            Assert.AreEqual(submitted.JobId, jobs[0].JobId);

            var projection = await backend.GetFileResultsAsync(submitted.JobId);
            Assert.IsNotNull(projection);
            Assert.AreEqual(1, projection.Items.Count);

            Assert.IsTrue(seenEvents.Any(e => e.Type == "job.upserted"));
            Assert.IsTrue(seenEvents.Any(e => e.Type == "workflow.upserted"));
            Assert.IsTrue(seenEvents.Any(e => e.Type == "search.updated"));
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
            var engine = new DownloadEngine(engineSettings, clientManager, resolver);
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
                    FailedAlbumPath = Path.Combine(musicRoot, "failed"),
                },
            };
            downloadSettings.Search.NecessaryCond.StrictTitle = true;

            var clientManager = new SoulseekClientManager(engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new AlbumQuery
            {
                Artist = "Artist",
                Album = "Album",
                SearchHint = "Track One",
            });

            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                () => searchJob.State == JobState.Done,
                "Timed out waiting for the album search to complete.");

            var initialProjection = await backend.GetFolderResultsAsync(searchJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(initialProjection);
            Assert.AreEqual(1, initialProjection.Items.Count);
            Assert.AreEqual(1, initialProjection.Items[0].Files?.Count);

            var foundCount = await backend.RetrieveFolderAndWaitAsync(
                searchJob.Id,
                new RetrieveFolderRequestDto(initialProjection.Items[0].Ref),
                cts.Token);

            Assert.AreEqual(1, foundCount);

            var expandedProjection = await backend.GetFolderResultsAsync(searchJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(expandedProjection);
            Assert.AreEqual(2, expandedProjection.Items[0].Files?.Count);

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
    public async Task LocalCliBackend_PublishesSharedProgressEvents_ForSongDownload()
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
                    FailedAlbumPath = Path.Combine(outputDir, "failed"),
                },
            };

            var clientManager = new SoulseekClientManager(engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine, downloadSettings);
            var seenTypes = new ConcurrentBag<string>();
            backend.EventReceived += envelope => seenTypes.Add(envelope.Type);

            await backend.SubmitSongJobAsync(
                new SubmitSongJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                cts.Token);

            engine.CompleteEnqueue();
            await engine.RunAsync(cts.Token);

            Assert.IsTrue(seenTypes.Contains("job.upserted"));
            Assert.IsTrue(seenTypes.Contains("song.searching"));
            Assert.IsTrue(seenTypes.Contains("download.started"));
            Assert.IsTrue(seenTypes.Contains("download.state-changed"));
            Assert.IsTrue(seenTypes.Contains("song.state-changed"));
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
                Output = { ParentDir = musicRoot, FailedAlbumPath = Path.Combine(musicRoot, "failed") },
                Search = { MinSharesAggregate = 1 },
            };

            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track One" });
            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                () => searchJob.State == JobState.Done,
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
                Output = { ParentDir = musicRoot, FailedAlbumPath = Path.Combine(musicRoot, "failed") },
                Search = { MinSharesAggregate = 1 },
            };

            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new AlbumQuery { Artist = "Artist" });
            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                () => searchJob.State == JobState.Done,
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

    private static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        Assert.Fail(failureMessage);
    }
}
