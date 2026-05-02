using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Cli;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Server;

namespace Tests.ProgressReporterTests;

[TestClass]
public class CliProgressReporterTests
{
    [TestCleanup]
    public void Cleanup()
    {
        Logger.RemoveNonFileOutputs();
    }

    [TestMethod]
    public void DownloadStart_NoProgress_DoesNotCreateProgressBar()
    {
        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var file = new Soulseek.File(1, @"Music\Artist\Song.flac", 100, ".flac");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, [file]);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" });

            InvokePrivate(reporter, "ReportDownloadStart", song, candidate);

            Assert.IsFalse(HasBarData(reporter, song));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void SongSearching_NoProgress_UsesJobFormatAndDeduplicatesEquivalentSearchEvents()
    {
        Logger.RemoveNonFileOutputs();
        var messages = new List<string>();
        Logger.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" });

            InvokePrivate(reporter, "ReportJobStarted", song);
            InvokePrivate(reporter, "ReportSongSearching", song);
            InvokePrivate(reporter, "ReportJobStatus", song, "searching");

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
        Logger.RemoveNonFileOutputs();
        var messages = new List<string>();
        Logger.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var file = new Soulseek.File(1, @"Music\Artist\Song.flac", 100, ".flac");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, [file]);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" })
            {
                State = JobState.Done,
                ResolvedTarget = candidate,
            };

            InvokePrivate(reporter, "ReportDownloadStart", song, candidate);
            InvokePrivate(reporter, "ReportStateChanged", song);

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
    public void StateChanged_FailedPreResolvedSong_DoesNotRenderAsSucceeded()
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
                State = JobState.Failed,
                FailureReason = FailureReason.Cancelled,
            };

            InvokePrivate(reporter, "ReportDownloadStart", song, candidate);
            var barData = GetBarData(reporter, song);

            InvokePrivate(reporter, "ReportStateChanged", song);

            Assert.AreEqual("Failed", GetField<string>(barData, "StateLabel"));
            Assert.AreNotEqual(100, GetField<int>(barData, "Pct"));
            StringAssert.Contains(GetField<string>(barData, "BaseText"), "Cancelled");
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void StateChanged_NormalProgress_UsesSongJobPrefixInTerminalLine()
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
                State = JobState.Done,
            };

            InvokePrivate(reporter, "ReportDownloadStart", song, candidate);
            var barData = GetBarData(reporter, song);

            InvokePrivate(reporter, "ReportStateChanged", song);

            StringAssert.StartsWith(BuildBarText(barData), $"  [{song.DisplayId}] SongJob: succeeded: ");
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
    public void RemoteAlbumChildSongEvents_DoNotCreateStandaloneProgressLines()
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
            InvokePrivate(reporter, "ReportJobStarted", new JobStartedEventDto(childSummary));
            InvokePrivate(reporter, "ReportSongSearching", new SongSearchingEventDto(
                fileJobId,
                DisplayId: 7,
                workflowId,
                new SongQueryDto("Artist", "Track", null, null, null, false)));
            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(
                fileJobId,
                DisplayId: 7,
                workflowId,
                new SongQueryDto("Artist", "Track", null, null, null, false),
                CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac")));

            Assert.IsFalse(HasBackendJobBar(reporter, fileJobId));
            Assert.IsFalse(HasBackendBarData(reporter, fileJobId));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAggregateChildSongEvents_CreateStandaloneProgressLines()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var workflowId = Guid.NewGuid();
            var aggregateJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var aggregateSummary = new JobSummaryDto(
                aggregateJobId,
                DisplayId: 6,
                WorkflowId: workflowId,
                Kind: ServerJobKind.Aggregate,
                State: ServerProtocol.JobStates.Downloading,
                ItemName: "Artist",
                QueryText: "Artist",
                FailureReason: null,
                FailureMessage: null,
                ParentJobId: null,
                ResultJobId: null,
                SourceJobId: null,
                DiscoveryResultCount: null,
                DiscoveryLockedFileCount: null,
                AppliedAutoProfiles: [],
                AvailableActions: []);
            var childSummary = CreateSongSummary(fileJobId, workflowId, aggregateJobId);

            InvokePrivate(reporter, "ReportJobUpserted", aggregateSummary);
            InvokePrivate(reporter, "ReportJobUpserted", childSummary);
            InvokePrivate(reporter, "ReportJobStarted", new JobStartedEventDto(childSummary));
            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(
                fileJobId,
                DisplayId: 7,
                workflowId,
                new SongQueryDto("Artist", "Track", null, null, null, false),
                CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac")));

            Assert.IsFalse(HasBackendJobBar(reporter, fileJobId));
            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));
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

            Assert.IsFalse(HasBackendJobBar(reporter, fileJobId));
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
        Logger.RemoveNonFileOutputs();
        var messages = new List<string>();
        Logger.AddConsole(writer: (message, _) => messages.Add(message));

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

            InvokePrivate(reporter, "ReportJobUpserted", albumSummary);
            InvokePrivate(reporter, "ReportJobUpserted", songSummary);
            InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(fileJobId, 7, workflowId, query, candidate));
            InvokePrivate(reporter, "ReportStateChanged", new SongStateChangedEventDto(
                fileJobId,
                DisplayId: 7,
                workflowId,
                query,
                ServerProtocol.JobStates.Done,
                FailureReason: null,
                DownloadPath: @"out\Track.flac",
                ChosenCandidate: candidate));

            CollectionAssert.AreEqual(new[]
            {
                @"[7] SongJob: downloading: local\..\Artist\Album\01. Artist - Track.flac",
                @"[7] SongJob: succeeded: local\..\Artist\Album\01. Artist - Track.flac",
            }, messages);
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

    private static object? InvokePrivate(object target, string name, params object[] args)
    {
        var argTypes = args.Select(a => a.GetType()).ToArray();
        return target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic, binder: null, types: argTypes, modifiers: null)!
            .Invoke(target, args);
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

        object?[] args = [song, null];
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

    private static string BuildBarText(object barData)
        => (string)typeof(CliProgressReporter)
            .GetMethod("BuildText", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [barData, false])!;

    private static bool HasBackendBarData(CliProgressReporter reporter, Guid jobId)
    {
        var bars = typeof(CliProgressReporter)
            .GetField("_backendBars", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [jobId, null];
        return (bool)bars.GetType().GetMethod("TryGetValue")!.Invoke(bars, args)!;
    }

    private static bool HasBackendJobBar(CliProgressReporter reporter, Guid jobId)
    {
        var bars = typeof(CliProgressReporter)
            .GetField("_backendJobBars", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [jobId, null];
        return (bool)bars.GetType().GetMethod("TryGetValue")!.Invoke(bars, args)!;
    }

    private static int GetBackendAlbumDoneCount(CliProgressReporter reporter, Guid albumJobId)
    {
        var blocks = typeof(CliProgressReporter)
            .GetField("_backendAlbumBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [albumJobId, null];
        Assert.IsTrue((bool)blocks.GetType().GetMethod("TryGetValue")!.Invoke(blocks, args)!);
        return (int)typeof(CliProgressReporter)
            .GetMethod("BackendAlbumDoneCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(reporter, [args[1]])!;
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
