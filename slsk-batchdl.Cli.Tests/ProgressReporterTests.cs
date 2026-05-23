using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Cli;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Server;
using Sldl.Api;

namespace Tests.ProgressReporterTests;

[TestClass]
public class CliProgressReporterTests
{
    [TestCleanup]
    public void Cleanup()
    {
        SldlLog.RemoveNonFileOutputs();
    }

    [TestMethod]
    public void EventLogger_HandledEventTypes_AreCataloged()
    {
        var catalogedTypes = ServerEventCatalog.All
            .Select(descriptor => descriptor.Type)
            .ToArray();

        CollectionAssert.IsSubsetOf(
            EventLogger.HandledEventTypes.ToArray(),
            catalogedTypes,
            "EventLogger must not use stale or uncataloged server event names.");

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "download.started",
                "on-complete.started",
                "on-complete.ended",
                "extraction.started",
                "extraction.failed",
            },
            EventLogger.HandledEventTypes.ToArray(),
            "Regression coverage for event names previously stale in EventLogger.");
    }

    [TestMethod]
    public void EventLogger_HandleEvent_UsesCatalogedActivityNames()
    {
        SldlLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!, liveMode: false);
        var workflowId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var query = new SongQueryDto("Artist", "Song", null, null, null, false);
        var candidate = CreateFileCandidate("user", @"Music\Artist\Song.flac");
        var extractSummary = CreateExtractSummary(Guid.NewGuid(), workflowId, ServerProtocol.JobStates.Extracting, null);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("download.started", new DownloadStartedEventDto(songId, 9, workflowId, query, candidate)));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("on-complete.started", new OnCompleteStartedEventDto(songId, 9, workflowId, query)));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("on-complete.ended", new OnCompleteEndedEventDto(songId, 9, workflowId, query)));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.started", new ExtractionStartedEventDto(extractSummary, "input.txt", "List")));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.failed", new ExtractionFailedEventDto(
            extractSummary with { State = ServerProtocol.JobStates.Failed },
            "Could not parse input")));

        Assert.AreEqual(5, messages.Count);
        StringAssert.StartsWith(messages[0], @"[9] SongJob: downloading: Artist - Song: user\Music\Artist\Song.flac");
        Assert.AreEqual("OnComplete start: [9] Artist - Song", messages[1]);
        Assert.AreEqual("OnComplete end: [9] Artist - Song", messages[2]);
        Assert.AreEqual("[11] ExtractJob: Input (List): input.txt", messages[3]);
        Assert.AreEqual($"[11] ExtractJob: Failed: input.txt{Environment.NewLine}  Reason:    Could not parse input", messages[4]);
    }

    [TestMethod]
    public void EventLogger_LiveMode_RoutesActivityLogsToNonConsoleOnly()
    {
        SldlLog.RemoveNonFileOutputs();
        var consoleMessages = new List<string>();
        var sinkMessages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => consoleMessages.Add(message));
        SldlLog.AddSink((_, message) => sinkMessages.Add(message));

        var workflowId = Guid.NewGuid();
        var eventLogger = new EventLogger(null!, liveMode: true);
        InvokePrivate(eventLogger, "HandleEvent", Envelope(
            "download.started",
            new DownloadStartedEventDto(
                Guid.NewGuid(),
                9,
                workflowId,
                new SongQueryDto("Artist", "Song", null, null, null, false),
                CreateFileCandidate("user", @"Music\Artist\Song.flac"))));

        Assert.AreEqual(0, consoleMessages.Count);
        Assert.AreEqual(1, sinkMessages.Count);
        StringAssert.StartsWith(sinkMessages[0], @"[sldl.cli] [9] SongJob: downloading: Artist - Song: user\Music\Artist\Song.flac");
    }

    [TestMethod]
    public void EventLogger_NoProgressMode_RoutesActivityLogsToConsoleAndNonConsole()
    {
        SldlLog.RemoveNonFileOutputs();
        var consoleMessages = new List<string>();
        var sinkMessages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => consoleMessages.Add(message));
        SldlLog.AddSink((_, message) => sinkMessages.Add(message));

        var workflowId = Guid.NewGuid();
        var eventLogger = new EventLogger(null!, liveMode: false);
        InvokePrivate(eventLogger, "HandleEvent", Envelope(
            "download.started",
            new DownloadStartedEventDto(
                Guid.NewGuid(),
                9,
                workflowId,
                new SongQueryDto("Artist", "Song", null, null, null, false),
                CreateFileCandidate("user", @"Music\Artist\Song.flac"))));

        Assert.AreEqual(1, consoleMessages.Count);
        Assert.AreEqual(1, sinkMessages.Count);
        StringAssert.StartsWith(consoleMessages[0], @"[9] SongJob: downloading: Artist - Song: user\Music\Artist\Song.flac");
        StringAssert.StartsWith(sinkMessages[0], @"[sldl.cli] [9] SongJob: downloading: Artist - Song: user\Music\Artist\Song.flac");
    }

    [TestMethod]
    public void DownloadStart_NoProgress_CreatesStateTracking()
    {
        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var file = new Soulseek.File(1, @"Music\Artist\Song.flac", 100, ".flac");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, [file]);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" });

            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(
                song.Id,
                song.DisplayId,
                Guid.NewGuid(),
                new SongQueryDto("Artist", "Song", null, null, null, false),
                CreateFileCandidate(candidate.Response.Username, candidate.File.Filename)));

            Assert.IsTrue(HasBarData(reporter, song));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void SongSearching_NoProgress_UsesJobFormatAndDeduplicatesEquivalentSearchEvents()
    {
        SldlLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" });
            var workflowId = Guid.NewGuid();
            var summary = CreateSongSummary(song.Id, workflowId, null) with
            {
                DisplayId = song.DisplayId,
                ItemName = "Artist - Song",
                QueryText = "Artist - Song",
            };

            var searching = new SongSearchingEventDto(
                song.Id,
                song.DisplayId,
                workflowId,
                new SongQueryDto("Artist", "Song", null, null, null, false));

            var eventLogger = new EventLogger(null!, liveMode: false);
            InvokePrivate(eventLogger, "HandleSongSearching", searching);
            InvokePrivate(eventLogger, "HandleSongSearching", searching);

            Assert.AreEqual(1, messages.Count);
            Assert.AreEqual($"[{song.DisplayId}] SongJob: searching: Artist - Song", messages[0]);
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void SongLifecycle_NoProgress_UsesJobFormatForDownloadAndTerminalState()
    {
        SldlLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var file = new Soulseek.File(1, @"Music\Artist\Song.flac", 100, ".flac");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, [file]);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" })
            {
                ResolvedTarget = candidate,
            };
            song.SetDone();

            var workflowId = Guid.NewGuid();
            var query = new SongQueryDto("Artist", "Song", null, null, null, false);
            var candidateDto = CreateFileCandidate(candidate.Response.Username, candidate.File.Filename);

            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(
                song.Id,
                song.DisplayId,
                workflowId,
                query,
                candidateDto));
            var summary = CreateSongSummary(song.Id, workflowId, null) with
            {
                DisplayId = song.DisplayId,
                ItemName = "Artist - Song",
                QueryText = "Artist - Song",
            };
            var eventLogger = new EventLogger(null!, liveMode: false);
            InvokePrivate(eventLogger, "HandleDownloadStart", new DownloadStartedEventDto(song.Id, song.DisplayId, workflowId, query, candidateDto));
            InvokePrivate(eventLogger, "HandleSongStateChanged", new SongStateChangedEventDto(
                song.Id, song.DisplayId, workflowId, query, ServerProtocol.JobStates.Done,
                null, null, candidateDto));

            Assert.AreEqual(2, messages.Count);
            StringAssert.StartsWith(messages[0], $"[{song.DisplayId}] SongJob: downloading: ");
            StringAssert.StartsWith(messages[1], $"[{song.DisplayId}] SongJob: succeeded: ");
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void SongFailure_NoProgress_PrintsFailureMessage()
    {
        SldlLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var workflowId = Guid.NewGuid();
            var songId = Guid.NewGuid();
            var query = new SongQueryDto("Artist", "Song", null, null, null, false);
            var candidate = CreateFileCandidate("user", @"Music\Artist\Song.flac");

            var eventLogger = new EventLogger(null!, liveMode: false);
            var summary = CreateSongSummary(songId, workflowId, null) with
            {
                DisplayId = 12,
                ItemName = "Artist - Song",
                QueryText = "Artist - Song",
                State = ServerProtocol.JobStates.Failed,
                FailureReason = ServerProtocol.FailureReasons.AllDownloadsFailed,
                FailureMessage = "Connection reset by peer"
            };
            InvokePrivate(eventLogger, "HandleSongStateChanged", new SongStateChangedEventDto(
                songId,
                DisplayId: 12,
                workflowId,
                query,
                ServerProtocol.JobStates.Failed,
                ServerProtocol.FailureReasons.AllDownloadsFailed,
                DownloadPath: null,
                ChosenCandidate: candidate,
                FailureMessage: "Connection reset by peer"));
 
            CollectionAssert.AreEqual(new[]
            {
                $"[12] SongJob: failed [All downloads failed]: Artist - Song: user\\Music\\Artist\\Song.flac{Environment.NewLine}    Error: Connection reset by peer",
            }, messages);
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void DownloadAttemptFailed_NoProgress_PrintsDiagnosticImmediately()
    {
        SldlLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var workflowId = Guid.NewGuid();
            var songId = Guid.NewGuid();
            var query = new SongQueryDto("Artist", "Song", null, null, null, false);
            var candidate = CreateFileCandidate("user", @"Music\Artist\Song.flac");

            InvokePrivate(reporter, "ReportDownloadAttemptFailed", new DownloadAttemptFailedEventDto(
                songId,
                DisplayId: 12,
                workflowId,
                query,
                candidate,
                OutputPath: @"out\Song.flac.incomplete",
                Attempt: 1,
                MaxAttempts: 3,
                ExceptionType: "SoulseekClientException",
                ExceptionMessage: "Connection reset by peer",
                Exception: "Soulseek.SoulseekClientException: Connection reset by peer"));

            CollectionAssert.AreEqual(new[]
            {
                "[12] SongJob: download error: Artist - Song: user\\Music\\Artist\\Song.flac\n    Output: out\\Song.flac.incomplete\n    Attempt: 1/3\n    SoulseekClientException: Connection reset by peer\n    Soulseek.SoulseekClientException: Connection reset by peer",
            }, messages);
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void StateChanged_FailedPreResolvedSong_DoesNotRenderAsFailedInAlbumButKeepsState()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var file = new Soulseek.File(1, @"Music\Artist\Song.flac", 100, ".flac");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, [file]);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" })
            {
                ResolvedTarget = candidate,
            };
            song.Fail(FailureReason.Cancelled);

            var workflowId = Guid.NewGuid();
            var query = new SongQueryDto("Artist", "Song", null, null, null, false);
            var candidateDto = CreateFileCandidate(candidate.Response.Username, candidate.File.Filename);

            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(
                song.Id,
                song.DisplayId,
                workflowId,
                query,
                candidateDto));
            var barData = GetBarData(reporter, song);

            InvokePrivate(reporter, "ReportStateChanged", new SongStateChangedEventDto(
                song.Id,
                song.DisplayId,
                workflowId,
                query,
                ServerProtocol.JobStates.Failed,
                ServerProtocol.FailureReasons.Cancelled,
                DownloadPath: null,
                ChosenCandidate: candidateDto));

            Assert.AreEqual("failed [Cancelled]", GetField<string>(barData, "StateLabel"));
            Assert.AreNotEqual(100, GetField<int>(barData, "Pct"));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumFolderConversion_PreservesCandidateFileIdentity()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var file = CreateFileCandidate("user", @"Artist\Album\01. Artist - Track.flac");
            var folder = new AlbumFolderDto(
                new AlbumFolderRefDto("user", @"Artist\Album"),
                "user",
                @"Artist\Album",
                new PeerInfoDto("user"),
                FileCount: 1,
                AudioFileCount: 1,
                Files: [file]);

            var converted = (AlbumFolder)InvokePrivate(reporter, "ToAlbumFolder", folder)!;

            Assert.AreEqual(1, converted.Files.Count);
            Assert.AreEqual(@"Artist\Album\01. Artist - Track.flac", converted.Files[0].ResolvedTarget?.Filename);
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumTrackDownloadCompleted_ReconcilesLeftoverRequestedBars()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var workflowId = Guid.NewGuid();
            var summary = new JobSummaryDto(
                albumJobId,
                DisplayId: 6,
                WorkflowId: workflowId,
                Kind: ServerJobKind.Album,
                State: ServerProtocol.JobStates.Downloading,
                ItemName: "Artist Album",
                QueryText: "Artist Album",
                FailureReason: null,
                FailureMessage: null,
                ParentJobId: null,
                ResultJobId: null,
                SourceJobId: null,
                DiscoveryResultCount: null,
                DiscoveryLockedFileCount: null,
                AppliedAutoProfiles: [],
                AvailableActions: []);
            var folder = new AlbumFolderDto(
                new AlbumFolderRefDto("local", @"Artist\Album"),
                "local",
                @"Artist\Album",
                new PeerInfoDto("local"),
                FileCount: 1,
                AudioFileCount: 1,
                Files: [CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac")]);

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                summary,
                folder,
                [CreateSongPayload(fileJobId, ServerProtocol.JobStates.Pending, null)]));
            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));

            var failedSummary = summary with
            {
                State = ServerProtocol.JobStates.Failed,
                FailureReason = ServerProtocol.FailureReasons.Cancelled,
            };
            InvokePrivate(reporter, "ReportAlbumDownloadCompleted", new AlbumDownloadCompletedEventDto(failedSummary));

            Assert.IsFalse(
                HasBackendBarData(reporter, fileJobId),
                "Album completion should reconcile and remove leftover requested bars.");
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumJobUpsertedTerminalState_ReconcilesLeftoverRequestedBars()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var summary = CreateAlbumSummary(albumJobId, ServerProtocol.JobStates.Downloading, null);
            var folder = CreateSingleFileAlbumFolder(fileJobId, ServerProtocol.JobStates.Pending, null);

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                summary,
                folder,
                [CreateSongPayload(fileJobId, ServerProtocol.JobStates.Pending, null)]));
            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));

            InvokePrivate(
                reporter,
                "ReportJobUpserted",
                CreateAlbumSummary(albumJobId, ServerProtocol.JobStates.Failed, ServerProtocol.FailureReasons.Cancelled));

            Assert.IsFalse(
                HasBackendBarData(reporter, fileJobId),
                "Terminal album job upserts should reconcile leftover requested bars even if album.download-completed has not arrived.");
        }
        finally
        {
            reporter.Stop();
        }
    }


    [TestMethod]
    public void RemoteAlbumChildDownloadStart_UpdatesAlbumTrackBarWithoutStandaloneJobLine()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var workflowId = Guid.NewGuid();
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var albumSummary = CreateAlbumSummary(albumJobId, ServerProtocol.JobStates.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var childSummary = CreateSongSummary(fileJobId, workflowId, albumJobId);

            InvokePrivate(reporter, "ReportJobUpserted", albumSummary);
            InvokePrivate(reporter, "ReportJobUpserted", childSummary);
            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobId, ServerProtocol.JobStates.Pending, null),
                [CreateSongPayload(fileJobId, ServerProtocol.JobStates.Pending, null)]));
            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(
                fileJobId,
                DisplayId: 7,
                workflowId,
                new SongQueryDto("Artist", "Track", null, null, null, false),
                CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac")));

            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumChildTerminalState_StillCountsTowardAlbumProgressAfterBarRemoval()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var workflowId = Guid.NewGuid();
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var albumSummary = CreateAlbumSummary(albumJobId, ServerProtocol.JobStates.Downloading, null) with
            {
                WorkflowId = workflowId,
            };

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobId, ServerProtocol.JobStates.Pending, null),
                [CreateSongPayload(fileJobId, ServerProtocol.JobStates.Pending, null)]));

            InvokePrivate(reporter, "ReportStateChanged", new SongStateChangedEventDto(
                fileJobId,
                DisplayId: 7,
                workflowId,
                new SongQueryDto("Artist", "Track", null, null, null, false),
                ServerProtocol.JobStates.Done,
                FailureReason: null,
                DownloadPath: @"out\Track.flac",
                ChosenCandidate: null));

            Assert.IsFalse(HasBackendBarData(reporter, fileJobId));
            Assert.AreEqual(1, GetBackendAlbumDoneCount(reporter, albumJobId));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumChildDownload_NoProgress_PrintsTrackLifecycle()
    {
        SldlLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SldlLog.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var workflowId = Guid.NewGuid();
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var albumSummary = CreateAlbumSummary(albumJobId, ServerProtocol.JobStates.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var songSummary = CreateSongSummary(fileJobId, workflowId, albumJobId);
            var query = new SongQueryDto("Artist", "Track", null, null, null, false);
            var candidate = CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac");

            var eventLogger = new EventLogger(null!, liveMode: false);
            InvokePrivate(eventLogger, "HandleAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobId, ServerProtocol.JobStates.Pending, null),
                [CreateSongPayload(fileJobId, ServerProtocol.JobStates.Pending, null)]));
            InvokePrivate(eventLogger, "HandleJobUpserted", songSummary);
            InvokePrivate(eventLogger, "HandleDownloadStart", new DownloadStartedEventDto(fileJobId, 7, workflowId, query, candidate));
            InvokePrivate(eventLogger, "HandleSongStateChanged", new SongStateChangedEventDto(
                fileJobId, 7, workflowId, query, ServerProtocol.JobStates.Done,
                null, @"out\Track.flac", candidate));

            Assert.AreEqual(3, messages.Count);
            Assert.AreEqual(@"[6] AlbumJob: downloading tracks: Artist Album - Artist\Album", messages[0]);
            Assert.AreEqual(@"[7] SongJob: downloading: Artist - Track: local\Artist\Album\01. Artist - Track.flac", messages[1]);
            Assert.AreEqual(@"[6] AlbumJob: succeeded: 01. Artist - Track.flac", messages[2]);
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumChildTerminalStates_CanArriveConcurrently()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var workflowId = Guid.NewGuid();
            var albumJobId = Guid.NewGuid();
            var albumSummary = CreateAlbumSummary(albumJobId, ServerProtocol.JobStates.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var fileJobIds = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
            var tracks = fileJobIds
                .Select(id => CreateSongPayload(id, ServerProtocol.JobStates.Pending, null))
                .ToList();

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobIds[0], ServerProtocol.JobStates.Pending, null),
                tracks));

            Parallel.ForEach(fileJobIds, fileJobId =>
            {
                InvokePrivate(reporter, "ReportStateChanged", new SongStateChangedEventDto(
                    fileJobId,
                    DisplayId: 7,
                    workflowId,
                    new SongQueryDto("Artist", "Track", null, null, null, false),
                    ServerProtocol.JobStates.Failed,
                    ServerProtocol.FailureReasons.Cancelled,
                    DownloadPath: null,
                    ChosenCandidate: null));
            });

            Assert.AreEqual(fileJobIds.Length, GetBackendAlbumDoneCount(reporter, albumJobId));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumChildAddedAfterAlbumStart_IsAddedToAlbumBlock()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var workflowId = Guid.NewGuid();
            var albumJobId = Guid.NewGuid();
            var initialFileJobId = Guid.NewGuid();
            var dynamicFileJobId = Guid.NewGuid();
            var albumSummary = CreateAlbumSummary(albumJobId, ServerProtocol.JobStates.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var dynamicSummary = CreateSongSummary(dynamicFileJobId, workflowId, albumJobId);
            var query = new SongQueryDto("Artist", "Bonus", null, null, null, false);
            var candidate = CreateFileCandidate("local", @"Artist\Album\Disc 2\02. Artist - Bonus.flac");

            InvokePrivate(reporter, "ReportJobUpserted", albumSummary);
            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(initialFileJobId, ServerProtocol.JobStates.Pending, null),
                [CreateSongPayload(initialFileJobId, ServerProtocol.JobStates.Pending, null)]));
            InvokePrivate(reporter, "ReportJobUpserted", dynamicSummary);
            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(dynamicFileJobId, 8, workflowId, query, candidate));

            Assert.AreEqual(2, GetBackendAlbumTrackCount(reporter, albumJobId));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void AggregateWrapperJobList_IsTransparentForLiveParenting()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var workflowId = Guid.NewGuid();
            var aggregateId = Guid.NewGuid();
            var listId = Guid.NewGuid();
            var albumId = Guid.NewGuid();

            InvokePrivate(reporter, "RememberStructure", new JobSummaryDto(
                aggregateId,
                DisplayId: 5,
                WorkflowId: workflowId,
                Kind: ServerJobKind.AlbumAggregate,
                State: ServerProtocol.JobStates.Running,
                ItemName: "squarepusher",
                QueryText: "squarepusher",
                FailureReason: null,
                FailureMessage: null,
                ParentJobId: null,
                ResultJobId: null,
                SourceJobId: null,
                DiscoveryResultCount: null,
                DiscoveryLockedFileCount: null,
                AppliedAutoProfiles: [],
                AvailableActions: []));
            InvokePrivate(reporter, "RememberStructure", new JobSummaryDto(
                listId,
                DisplayId: 19,
                WorkflowId: workflowId,
                Kind: ServerJobKind.JobList,
                State: ServerProtocol.JobStates.Running,
                ItemName: "squarepusher",
                QueryText: "squarepusher",
                FailureReason: null,
                FailureMessage: null,
                ParentJobId: aggregateId,
                ResultJobId: null,
                SourceJobId: null,
                DiscoveryResultCount: null,
                DiscoveryLockedFileCount: null,
                AppliedAutoProfiles: [],
                AvailableActions: []));
            InvokePrivate(reporter, "RememberStructure", CreateAlbumSummary(albumId, ServerProtocol.JobStates.Downloading, null) with
            {
                WorkflowId = workflowId,
                ParentJobId = listId,
            });

            var parentId = (string?)InvokePrivate(reporter, "GetContainerParentId", albumId);

            Assert.AreEqual(aggregateId.ToString(), parentId);
        }
        finally
        {
            reporter.Stop();
        }
    }

    private static object? InvokePrivate(object target, string name, params object[] args)
    {
        var argTypes = args.Select(a => a.GetType()).ToArray();
        return target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic, binder: null, types: argTypes, modifiers: null)!
            .Invoke(target, args);
    }

    private static ServerEventEnvelopeDto Envelope(string type, object payload)
    {
        var descriptor = ServerEventCatalog.Describe(type);
        return new ServerEventEnvelopeDto(
            Sequence: 1,
            Type: type,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Category: descriptor.Category,
            SnapshotInvalidation: descriptor.SnapshotInvalidation,
            WorkflowId: null,
            Payload: payload);
    }

    private static object GetBarData(CliProgressReporter reporter, SongJob song)
    {
        Assert.IsTrue(TryGetBarData(reporter, song, out var barData));
        return barData!;
    }

    private static bool HasBarData(CliProgressReporter reporter, SongJob song)
    {
        return TryGetBarData(reporter, song, out _);
    }

    private static bool TryGetBarData(CliProgressReporter reporter, SongJob song, out object? barData)
    {
        var bars = typeof(CliProgressReporter)
            .GetField("_bars", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [song.Id, null];
        var found = (bool)bars.GetType().GetMethod("TryGetValue")!.Invoke(bars, args)!;
        barData = args[1];
        return found;
    }

    private static T GetField<T>(object target, string name)
    {
        return (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(target)!;
    }


    private static bool HasBackendBarData(CliProgressReporter reporter, Guid jobId)
    {
        var bars = typeof(CliProgressReporter)
            .GetField("_bars", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [jobId, null];
        return (bool)bars.GetType().GetMethod("TryGetValue")!.Invoke(bars, args)!;
    }


    private static int GetBackendAlbumDoneCount(CliProgressReporter reporter, Guid albumJobId)
    {
        var blocks = typeof(CliProgressReporter)
            .GetField("_albumBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [albumJobId, null];
        Assert.IsTrue((bool)blocks.GetType().GetMethod("TryGetValue")!.Invoke(blocks, args)!);
        return (int)typeof(CliProgressReporter)
            .GetMethod("AlbumDoneCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(reporter, [args[1]])!;
    }

    private static int GetBackendAlbumTrackCount(CliProgressReporter reporter, Guid albumJobId)
    {
        var blocks = typeof(CliProgressReporter)
            .GetField("_albumBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [albumJobId, null];
        Assert.IsTrue((bool)blocks.GetType().GetMethod("TryGetValue")!.Invoke(blocks, args)!);
        var songs = args[1]!.GetType()
            .GetField("Songs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(args[1])!;
        return (int)songs.GetType().GetProperty("Count")!.GetValue(songs)!;
    }

    private static JobSummaryDto CreateAlbumSummary(Guid jobId, ServerJobState state, ServerFailureReason? failureReason)
        => new(
            jobId,
            DisplayId: 6,
            WorkflowId: Guid.NewGuid(),
            Kind: ServerJobKind.Album,
            State: state,
            ItemName: "Artist Album",
            QueryText: "Artist Album",
            FailureReason: failureReason,
            FailureMessage: null,
            ParentJobId: null,
            ResultJobId: null,
            SourceJobId: null,
            DiscoveryResultCount: null,
            DiscoveryLockedFileCount: null,
            AppliedAutoProfiles: [],
            AvailableActions: []);

    private static JobSummaryDto CreateSongSummary(Guid jobId, Guid workflowId, Guid? parentJobId)
        => new(
            jobId,
            DisplayId: 7,
            WorkflowId: workflowId,
            Kind: ServerJobKind.Song,
            State: ServerProtocol.JobStates.Searching,
            ItemName: "Artist - Track",
            QueryText: "Artist - Track",
            FailureReason: null,
            FailureMessage: null,
            ParentJobId: parentJobId,
            ResultJobId: null,
            SourceJobId: null,
            DiscoveryResultCount: null,
            DiscoveryLockedFileCount: null,
            AppliedAutoProfiles: [],
            AvailableActions: []);

    private static JobSummaryDto CreateExtractSummary(Guid jobId, Guid workflowId, ServerJobState state, ServerFailureReason? failureReason)
        => new(
            jobId,
            DisplayId: 11,
            WorkflowId: workflowId,
            Kind: ServerJobKind.Extract,
            State: state,
            ItemName: "input.txt",
            QueryText: "input.txt",
            FailureReason: failureReason,
            FailureMessage: null,
            ParentJobId: null,
            ResultJobId: null,
            SourceJobId: null,
            DiscoveryResultCount: null,
            DiscoveryLockedFileCount: null,
            AppliedAutoProfiles: [],
            AvailableActions: []);

    private static AlbumFolderDto CreateSingleFileAlbumFolder(Guid fileJobId, ServerJobState state, ServerFailureReason? failureReason)
        => new(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            "local",
            @"Artist\Album",
            new PeerInfoDto("local"),
            FileCount: 1,
            AudioFileCount: 1,
            Files: [CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac")]);

    private static SongJobPayloadDto CreateSongPayload(Guid fileJobId, ServerJobState state, ServerFailureReason? failureReason)
        => new(
            new SongQueryDto("Artist", "Track", null, null, null, false),
            CandidateCount: 1,
            DownloadPath: null,
            ResolvedUsername: "local",
            ResolvedFilename: @"Artist\Album\01. Artist - Track.flac",
            ResolvedHasFreeUploadSlot: true,
            ResolvedUploadSpeed: 100,
            ResolvedSize: 100,
            ResolvedSampleRate: null,
            ResolvedExtension: ".flac",
            ResolvedAttributes: null,
            JobId: fileJobId,
            DisplayId: 7,
            Candidates: null,
            State: state,
            FailureReason: failureReason,
            FailureMessage: null);

    private static FileCandidateDto CreateFileCandidate(string username, string filename)
        => new(
            new FileCandidateRefDto(username, filename),
            username,
            filename,
            new PeerInfoDto(username, true, 100),
            Size: 100,
            BitRate: null,
            SampleRate: null,
            Length: null,
            Extension: ".flac",
            Attributes: null);
}
