using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;

using Directory = System.IO.Directory;

namespace Tests.Core;

[TestClass]
public class StaleDownloadTests
{
    private static readonly TimeSpan MaxStaleTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(2);

    [TestMethod]
    public async Task SongDownload_ChosenResultFailsAfterMaxStaleTimeWithoutActivity()
    {
        var outputDir = CreateOutputDir();
        var (response, candidate) = CreateCandidate("stalled-user", @"Music\Test Artist - Test Song.mp3");
        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Test Song" })
        {
            ResolvedTarget = candidate,
        };
        var engine = CreateEngine(engineSettings, client, clock);
        string? attemptExceptionType = null;
        engine.Events.DownloadAttemptFailed += change =>
            attemptExceptionType = change.Exception.Type;

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, song, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());

            clock.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            Assert.IsFalse(song.Cts?.IsCancellationRequested == true);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertFailed(song, JobFailureReason.AllDownloadsFailed);
            Assert.AreEqual(JobCancellationSource.None, song.CancellationSource);
            StringAssert.Contains(song.FailureMessage, "Download attempt became stale");
            Assert.AreEqual(nameof(StaleDownloadException), attemptExceptionType);
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongDownload_QueuedResultFailsAfterMaxStaleTimeWithoutActivity()
    {
        var outputDir = CreateOutputDir();
        var (response, candidate) = CreateCandidate("queued-user", @"Music\Test Artist - Queued Song.mp3");
        var queued = NewSignal();
        var releaseQueued = NewSignal();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            AfterDownloadStateChangedAsync = (_, _, state, ct) =>
            {
                if (!state.HasFlag(TransferStates.Queued))
                    return Task.CompletedTask;

                queued.TrySetResult();
                return releaseQueued.Task.WaitAsync(ct);
            },
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Queued Song" })
        {
            ResolvedTarget = candidate,
        };
        var engine = CreateEngine(engineSettings, client, clock);
        string? attemptExceptionType = null;
        engine.Events.DownloadAttemptFailed += change =>
            attemptExceptionType = change.Exception.Type;

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, song, settings, runCts.Token);
        try
        {
            await queued.Task.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertFailed(song, JobFailureReason.AllDownloadsFailed);
            Assert.AreEqual(JobCancellationSource.None, song.CancellationSource);
            StringAssert.Contains(song.FailureMessage, "Download attempt became stale");
            Assert.AreEqual(nameof(StaleDownloadException), attemptExceptionType);
        }
        finally
        {
            runCts.Cancel();
            releaseQueued.TrySetResult();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongDownload_StaleCandidateTriesNextCandidate()
    {
        var outputDir = CreateOutputDir();
        var staleFile = TestHelpers.CreateSlFile(@"Music\Test Artist - Test Song.mp3", size: 5000, length: 180);
        var goodFile = TestHelpers.CreateSlFile(@"Shares\Test Artist - Test Song.mp3", size: 5000, length: 180);
        var staleResponse = CreateResponse("stale-user", staleFile);
        var goodResponse = CreateResponse("good-user", goodFile);
        var staleCandidate = SoulseekSearchAdapter.ToFileCandidate(staleResponse, staleFile);
        var goodCandidate = SoulseekSearchAdapter.ToFileCandidate(goodResponse, goodFile);
        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([staleResponse, goodResponse])
        {
            BeforeDownloadCompletesAsync = (username, filename, ct) =>
                username == "stale-user"
                    ? downloadGate.BlockAsync(username, filename, ct)
                    : Task.CompletedTask,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        settings.Transfer.MaxDownloadRetries = 2;
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Test Song" })
        {
            Candidates = [staleCandidate, goodCandidate],
        };
        var engine = CreateEngine(engineSettings, client, clock);
        string? attemptExceptionType = null;
        engine.Events.DownloadAttemptFailed += change =>
        {
            if (change.Target.Identity.Username == "stale-user")
                attemptExceptionType = change.Exception.Type;
        };

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, song, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
            Assert.AreEqual("good-user", song.ResolvedTarget?.Username);
            Assert.AreEqual(nameof(StaleDownloadException), attemptExceptionType);
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongDownload_DownloadStartedWorkIsOutsideStaleWindow()
    {
        var outputDir = CreateOutputDir();
        var (response, candidate) = CreateCandidate("started-user", @"Music\Test Artist - Started Song.mp3");
        var client = new ClientTests.MockSoulseekClient([response]);
        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Started Song" })
        {
            ResolvedTarget = candidate,
            Config = settings,
            Cts = new CancellationTokenSource(),
        };
        var activeDownloads = new ActiveDownloadTracker();
        var events = new DownloadEvents();
        var staleDownloads = new StaleDownloadCoordinator(activeDownloads, clock);
        using var fileOperations = new DownloadFileOperations(engineSettings.ConcurrentJobs);
        var downloader = new ExactPeerFileTransferRunner(
            client,
            TestHelpers.CreateMockClientManager(client, engineSettings),
            activeDownloads,
            events,
            staleDownloads, fileOperations);
        var started = NewSignal();
        var releaseStarted = NewSignal();
        events.DownloadStarted += _ =>
        {
            started.TrySetResult();
            releaseStarted.Task.GetAwaiter().GetResult();
        };
        var outputPath = Path.Combine(outputDir, "started.mp3");

        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(song.Cts.Token);
        var downloadTask = Task.Run(() => downloader.DownloadFile(
            candidate.Target,
            outputPath,
            song,
            settings.Transfer,
            settings.Output.ParentDir,
            settings.Search.MaxStaleTime,
            downloadCts.Token));
        try
        {
            await started.Task.WaitAsync(SignalTimeout);

            clock.Advance(MaxStaleTime);
            Assert.AreEqual(0, staleDownloads.CancelStaleDownloads(),
                "Work before the Soulseek transfer is established must not be part of stale cancellation.");
            Assert.IsFalse(song.Cts?.IsCancellationRequested == true);

            releaseStarted.TrySetResult();
            var outcome = await downloadTask.WaitAsync(SignalTimeout);
            Assert.AreEqual(ExactFileTransferStatus.Completed, outcome.Status);
        }
        finally
        {
            downloadCts.Cancel();
            releaseStarted.TrySetResult();
            try { await IgnoreRunCancellation(downloadTask.WaitAsync(SignalTimeout)); }
            catch (TimeoutException) { }
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongDownload_FallbackWorkIsOutsideStaleWindow()
    {
        var outputDir = CreateOutputDir();
        var client = new ClientTests.MockSoulseekClient([]);
        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var fallback = new BlockingFallback();
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Fallback Song" })
        {
            Candidates = [],
        };
        var engine = CreateEngine(engineSettings, client, clock, fallback);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, song, settings, runCts.Token);
        try
        {
            await fallback.Started.WaitAsync(SignalTimeout);

            clock.Advance(MaxStaleTime);
            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting(),
                "Fallback download work must not be part of Soulseek stale cancellation.");
            Assert.IsFalse(song.Cts?.IsCancellationRequested == true);

            var fallbackPath = Path.Combine(outputDir, "fallback.mp3");
            await System.IO.File.WriteAllBytesAsync(fallbackPath, [1, 2, 3], runCts.Token);
            fallback.Release(fallbackPath);
            await runTask.WaitAsync(SignalTimeout);

            Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
            Assert.AreEqual(SongDownloadSource.Fallback, song.DownloadSource);
        }
        finally
        {
            runCts.Cancel();
            fallback.Release(null);
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AlbumDownload_StaleFolderFailsAfterRetriesExhausted()
    {
        var outputDir = CreateOutputDir();
        var file = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - Test Song.mp3", size: 5000, length: 180);
        var response = CreateResponse("album-user", file);
        var folder = CreateAlbumFolder(response, file);
        var album = new AlbumJob(new AlbumQuery { Artist = "Test Artist", Album = "Test Album" })
        {
            ResolvedTarget = folder,
            Results = [folder],
            DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.UseSelectedSnapshot,
            ValidationRequirement = AlbumValidationRequirement.UserAccepted,
        };

        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };
        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var engine = CreateEngine(engineSettings, client, clock);
        var warnings = new List<string>();
        engine.Events.JobMessage += change =>
        {
            if (change.Job.Id == album.Id && change.Level == LogLevel.Warning)
                warnings.Add(change.Message);
        };

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, album, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertFailed(album, JobFailureReason.AllDownloadsFailed);
            Assert.AreEqual(JobCancellationSource.None, album.CancellationSource);
            Assert.IsTrue(album.TrackJobs.Any(song =>
                song.TerminalOutcome == JobTerminalOutcome.Failed
                && song.FailureReason == JobFailureReason.AllDownloadsFailed
                && StaleDownloadException.IsStaleFailureMessage(song.FailureMessage)));
            Assert.IsTrue(warnings.Any(message =>
                message.Contains("album candidate became stale", StringComparison.Ordinal)
                && message.Contains("no more album candidates will be tried", StringComparison.Ordinal)));
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AlbumDownload_StaleFolderTriesNextFolder()
    {
        var outputDir = CreateOutputDir();
        var staleFile = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - Test Song.mp3", size: 5000, length: 180);
        var goodFile = TestHelpers.CreateSlFile(@"Shares\Test Artist\Test Album\01. Test Artist - Test Song.mp3", size: 5000, length: 180);
        var staleResponse = CreateResponse("stale-album-user", staleFile);
        var goodResponse = CreateResponse("good-album-user", goodFile);
        var staleFolder = CreateAlbumFolder(staleResponse, staleFile);
        var goodFolder = CreateAlbumFolder(goodResponse, goodFile);
        var album = new AlbumJob(new AlbumQuery { Artist = "Test Artist", Album = "Test Album" })
        {
            Results = [staleFolder, goodFolder],
            ValidationRequirement = AlbumValidationRequirement.UserAccepted,
        };

        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([staleResponse, goodResponse])
        {
            BeforeDownloadCompletesAsync = (username, filename, ct) =>
                username == "stale-album-user"
                    ? downloadGate.BlockAsync(username, filename, ct)
                    : Task.CompletedTask,
        };
        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        settings.Transfer.MaxDownloadRetries = 2;
        var engine = CreateEngine(engineSettings, client, clock);
        var warnings = new List<string>();
        engine.Events.JobMessage += change =>
        {
            if (change.Job.Id == album.Id && change.Level == LogLevel.Warning)
                warnings.Add(change.Message);
        };

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, album, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
            Assert.AreSame(goodFolder, album.ResolvedTarget);
            Assert.IsTrue(album.TrackJobs.All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
            Assert.IsTrue(warnings.Any(message =>
                message.Contains("album candidate became stale", StringComparison.Ordinal)
                && message.Contains("trying next album candidate", StringComparison.Ordinal)));
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task GroupDownload_StaleChildFailsGroup()
    {
        var outputDir = CreateOutputDir();
        var file = TestHelpers.CreateSlFile(@"Music\Test Artist - Test Song.mp3", size: 5000, length: 180);
        var response = CreateResponse("group-user", file);
        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        settings.Search.MinGroupShares = 1;
        var group = new SongGroupsJob(new SongQuery { Artist = "Test Artist", Title = "Test Song" });
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, group, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertFailed(group, JobFailureReason.AllDownloadsFailed);
            Assert.IsTrue(group.Songs.Any(song =>
                song.FailureReason == JobFailureReason.AllDownloadsFailed
                && StaleDownloadException.IsStaleFailureMessage(song.FailureMessage)));
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AlbumGroupsDownload_StaleTrackFailsAlbumGroups()
    {
        var outputDir = CreateOutputDir();
        var file = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - Test Song.mp3", size: 5000, length: 180);
        var response = CreateResponse("album-groups-user", file);
        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        settings.Search.MinGroupShares = 1;
        settings.Search.NoBrowseFolder = true;
        var group = new AlbumGroupsJob(new AlbumQuery { Artist = "Test Artist" });
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, group, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertFailed(group, JobFailureReason.AllDownloadsFailed);
            Assert.IsTrue(group.Albums.SelectMany(album => album.TrackJobs).Any(song =>
                song.FailureReason == JobFailureReason.AllDownloadsFailed
                && StaleDownloadException.IsStaleFailureMessage(song.FailureMessage)));
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongJobList_QueuedSameUserSongUsesSiblingActivityForStaleTimer()
    {
        var outputDir = CreateOutputDir();
        var firstFile = TestHelpers.CreateSlFile(@"Music\Test Artist - First Single.mp3", size: 50000, length: 180);
        var secondFile = TestHelpers.CreateSlFile(@"Music\Other Artist - Second Single.mp3", size: 50000, length: 181);
        var response = CreateResponse("single-slot-song-user", firstFile, secondFile);
        var firstSong = new SongJob(new SongQuery { Artist = "Test Artist", Title = "First Single" })
        {
            ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(response, firstFile),
        };
        var secondSong = new SongJob(new SongQuery { Artist = "Other Artist", Title = "Second Single" })
        {
            ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(response, secondFile),
        };
        var songList = new JobList("same-user songs", [firstSong, secondSong]);

        var sameUserQueue = new SameUserQueuedSiblingProbe();

        var client = new ClientTests.MockSoulseekClient([response])
        {
            AfterDownloadStateChangedAsync = sameUserQueue.OnStateChangedAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, songList, settings, runCts.Token);
        try
        {
            await sameUserQueue.BothQueued.WaitAsync(SignalTimeout);
            await sameUserQueue.ActiveInitializing.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());

            clock.Advance(MaxStaleTime);
            sameUserQueue.ReleaseInitializing();
            await sameUserQueue.ActiveInProgress.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting(),
                "A queued song job should inherit fresh activity from another active download by the same user.");
            var queuedSong = new[] { firstSong, secondSong }.Single(song =>
                !song.ResolvedTarget!.Filename.Equals(sameUserQueue.ActiveFilename, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(queuedSong.Cts?.IsCancellationRequested == true);
            Assert.AreNotEqual(JobFailureReason.Cancelled, queuedSong.FailureReason);
        }
        finally
        {
            runCts.Cancel();
            sameUserQueue.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AlbumDownload_QueuedSameUserTrackUsesSiblingActivityForStaleTimer()
    {
        var outputDir = CreateOutputDir();
        var firstFile = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - First.mp3", size: 50000, length: 180);
        var secondFile = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\02. Test Artist - Second.mp3", size: 50000, length: 181);
        var response = CreateResponse("single-slot-user", firstFile, secondFile);
        var folder = CreateAlbumFolder(response, firstFile, secondFile);
        var album = new AlbumJob(new AlbumQuery { Artist = "Test Artist", Album = "Test Album" })
        {
            ResolvedTarget = folder,
            Results = [folder],
            DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.UseSelectedSnapshot,
            ValidationRequirement = AlbumValidationRequirement.UserAccepted,
        };

        var sameUserQueue = new SameUserQueuedSiblingProbe();

        var client = new ClientTests.MockSoulseekClient([response])
        {
            AfterDownloadStateChangedAsync = sameUserQueue.OnStateChangedAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, album, settings, runCts.Token);
        try
        {
            await sameUserQueue.BothQueued.WaitAsync(SignalTimeout);
            await sameUserQueue.ActiveInitializing.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());

            clock.Advance(MaxStaleTime);
            sameUserQueue.ReleaseInitializing();
            await sameUserQueue.ActiveInProgress.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting(),
                "A queued download should inherit fresh activity from another active download by the same user.");
            var queuedSong = album.TrackJobs.Single(song =>
                !song.ResolvedTarget!.Filename.Equals(sameUserQueue.ActiveFilename, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(queuedSong.Cts?.IsCancellationRequested == true);
            Assert.AreNotEqual(JobFailureReason.Cancelled, queuedSong.FailureReason);
        }
        finally
        {
            runCts.Cancel();
            sameUserQueue.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    private static Task Start(DownloadEngine engine, Job job, DownloadSettings settings, CancellationToken cancellationToken)
    {
        engine.Enqueue(job, settings);
        engine.CompleteEnqueue();
        return engine.RunAsync(cancellationToken);
    }

    private static DownloadEngine CreateEngine(
        EngineSettings engineSettings,
        ClientTests.MockSoulseekClient client,
        ManualTimeProvider clock,
        ISongDownloadFallback? songDownloadFallback = null)
    {
        var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings),
            songDownloadFallback: songDownloadFallback,
            timeProvider: clock);
        engine.AutomaticStaleChecksEnabled = false;
        return engine;
    }

    private static EngineSettings CreateEngineSettings()
        => new() { Username = "u", Password = "p", ConcurrentSearches = 10 };

    private static DownloadSettings CreateSettings(string outputDir)
    {
        var settings = new DownloadSettings();
        settings.Output.ParentDir = outputDir;
        settings.Search.MaxStaleTime = (int)MaxStaleTime.TotalMilliseconds;
        settings.Search.NoBrowseFolder = true;
        settings.Transfer.UnknownErrorRetries = 1;
        settings.Transfer.MaxDownloadRetries = 1;
        settings.Skip.SkipExisting = false;
        return settings;
    }

    private static (SearchResponse Response, FileCandidate Candidate) CreateCandidate(
        string username,
        string filename,
        long size = 5000)
    {
        var file = TestHelpers.CreateSlFile(filename, size: size, length: 180);
        var response = CreateResponse(username, file);
        return (response, SoulseekSearchAdapter.ToFileCandidate(response, file));
    }

    private static SearchResponse CreateResponse(string username, params Soulseek.File[] files)
        => new(
            username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100_000,
            queueLength: 0,
            fileList: files);

    private static AlbumFolder CreateAlbumFolder(SearchResponse response, params Soulseek.File[] files)
        => new(
            response.Username,
            Utils.GetDirectoryNameSlsk(files[0].Filename),
            files.Select(file => TestHelpers.CreateAlbumFile(response, file)).ToList())
        {
            IsFullyRetrieved = true,
        };

    private static string CreateOutputDir()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "sockseek-stale-download-" + Guid.NewGuid());
        Directory.CreateDirectory(outputDir);
        return outputDir;
    }

    private static void DeleteOutputDir(string outputDir)
    {
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertFailed(Job job, JobFailureReason reason)
    {
        Assert.AreEqual(JobLifecycleState.Terminal, job.LifecycleState);
        Assert.AreEqual(JobTerminalOutcome.Failed, job.TerminalOutcome);
        Assert.AreEqual(reason, job.FailureReason);
    }

    private static async Task IgnoreRunCancellation(Task runTask)
    {
        try { await runTask; }
        catch (OperationCanceledException) { }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException)) { }
    }

    private sealed class BlockingFallback : ISongDownloadFallback
    {
        private readonly TaskCompletionSource started = NewSignal();
        private readonly TaskCompletionSource release = NewSignal();
        private string? downloadPath;

        public Task Started => started.Task;

        public bool CanRun(SongJob song, DownloadSettings settings)
            => true;

        public async Task<JobOutcome?> TryDownloadAsync(
            SongJob song,
            DownloadSettings settings,
            FileManager organizer,
            IJobLog? log,
            CancellationToken ct,
            Action<FallbackTransferDescriptor>? transferStarting = null)
        {
            transferStarting?.Invoke(new FallbackTransferDescriptor(
                "blocking-fallback",
                organizer.GetSavePathNoExt(song.Query.ToString() + ".mp3")));
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return string.IsNullOrWhiteSpace(downloadPath)
                ? null
                : JobOutcome.Done(downloadPath, downloadSource: SongDownloadSource.Fallback);
        }

        public void Release(string? path)
        {
            downloadPath = path;
            release.TrySetResult();
        }
    }

    private sealed class SameUserQueuedSiblingProbe
    {
        private readonly object gate = new();
        private readonly HashSet<string> queuedFilenames = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource bothQueued = NewSignal();
        private readonly TaskCompletionSource activeInitializing = NewSignal();
        private readonly TaskCompletionSource activeInProgress = NewSignal();
        private readonly TaskCompletionSource releaseInitializing = NewSignal();
        private readonly TaskCompletionSource releaseInProgress = NewSignal();

        public string? ActiveFilename { get; private set; }
        public Task BothQueued => bothQueued.Task;
        public Task ActiveInitializing => activeInitializing.Task;
        public Task ActiveInProgress => activeInProgress.Task;

        public async Task OnStateChangedAsync(string username, string filename, TransferStates state, CancellationToken ct)
        {
            if (state.HasFlag(TransferStates.Queued))
            {
                lock (gate)
                {
                    queuedFilenames.Add(filename);
                    if (queuedFilenames.Count == 2)
                        bothQueued.TrySetResult();
                }
            }

            if (state.HasFlag(TransferStates.Initializing) && TryMarkActive(filename))
            {
                activeInitializing.TrySetResult();
                await releaseInitializing.Task.WaitAsync(ct);
                return;
            }

            if (state.HasFlag(TransferStates.InProgress) && IsActive(filename))
            {
                activeInProgress.TrySetResult();
                await releaseInProgress.Task.WaitAsync(ct);
            }
        }

        public void ReleaseInitializing()
            => releaseInitializing.TrySetResult();

        public void ReleaseAll()
        {
            releaseInitializing.TrySetResult();
            releaseInProgress.TrySetResult();
        }

        private bool TryMarkActive(string filename)
        {
            lock (gate)
            {
                if (ActiveFilename != null)
                    return false;

                ActiveFilename = filename;
                return true;
            }
        }

        private bool IsActive(string filename)
        {
            lock (gate)
                return filename.Equals(ActiveFilename, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan timeSpan)
        {
            utcNow += timeSpan;
            timestamp += timeSpan.Ticks;
        }
    }
}
