using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Server;
using Sockseek.Api;

namespace Tests.ProgressReporterTests;

[TestClass]
public class CliProgressReporterTests
{
    private static string JobLog(string message) => $"[jobs] {message}";
    private static string ErrorJobLog(string message) => $"[error] [jobs] {message}";

    [TestCleanup]
    public void Cleanup()
    {
        SockseekLog.RemoveNonFileOutputs();
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
                "job.activity-changed",
                "extraction.started",
                "extraction.failed",
            },
            EventLogger.HandledEventTypes.ToArray(),
            "Regression coverage for event names previously stale in EventLogger.");
    }

    [TestMethod]
    public void EventLogger_HandleEvent_UsesCatalogedActivityNames()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var query = new SongQueryDto("Artist", "Song", null, null, null, false);
        var candidate = CreateFileCandidate("user", @"Music\Artist\Song.flac");
        var songSummary = WithState(CreateSongSummary(songId, workflowId, null) with { DisplayId = 9 }, ExpectedJobStatus.RunningOnComplete);
        var extractSummary = CreateExtractSummary(Guid.NewGuid(), workflowId, ExpectedJobStatus.Extracting, null);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("download.started", new DownloadStartedEventDto(songId, 9, workflowId, query, candidate)));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.activity-changed", new JobActivityChangedEventDto(songSummary)));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.started", new ExtractionStartedEventDto(extractSummary, "input.txt", "List")));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.failed", new ExtractionFailedEventDto(
            WithState(extractSummary, ExpectedJobStatus.Failed),
            "Could not parse input")));

        Assert.AreEqual(4, messages.Count);
        StringAssert.StartsWith(messages[0], JobLog(@"[9] SongJob: downloading: Artist - Song: user\Music\Artist\Song.flac"));
        Assert.AreEqual(JobLog("[9] SongJob: on-complete: Artist - Track"), messages[1]);
        Assert.AreEqual(JobLog("[11] ExtractJob: List: Input: input.txt"), messages[2]);
        Assert.AreEqual(ErrorJobLog("[11] ExtractJob: Failed: input.txt\n  Reason:    Could not parse input"), messages[3]);
    }

    [TestMethod]
    public void EventLogger_JobMessage_RespectsLogLevelAndPrintsSource()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var extractSummary = CreateExtractSummary(Guid.NewGuid(), workflowId, ExpectedJobStatus.Extracting, null);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.message", new JobMessageEventDto(
            extractSummary,
            LogLevel.Debug.ToString(),
            "Spotify",
            "Authorizing (login=False, modify=False)")));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.message", new JobMessageEventDto(
            extractSummary,
            LogLevel.Information.ToString(),
            "Spotify",
            "Loading playlist")));

        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual(JobLog("[11] ExtractJob: Spotify: Loading playlist"), messages[0]);
    }

    [TestMethod]
    public void EventLogger_ExtractionFailure_SuppressesGenericExtractUpsert()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var extractSummary = CreateExtractSummary(
            Guid.NewGuid(),
            workflowId,
            ExpectedJobStatus.Failed,
            ServerProtocol.FailureReasons.ExtractionFailed) with
        {
            FailureMessage = "Could not parse input",
        };

        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", extractSummary));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.failed", new ExtractionFailedEventDto(
            extractSummary,
            "Could not parse input")));

        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual(ErrorJobLog("[11] ExtractJob: Failed: input.txt\n  Reason:    Could not parse input"), messages[0]);
    }

    [TestMethod]
    public void EventLogger_ExtractionEvents_PrintConsistentSourcePrefix()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var extractSummary = CreateExtractSummary(
            Guid.NewGuid(),
            workflowId,
            ExpectedJobStatus.Failed,
            ServerProtocol.FailureReasons.ExtractionFailed);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.started", new ExtractionStartedEventDto(
            extractSummary,
            "https://open.spotify.com/playlist/123",
            "Spotify",
            "Spotify")));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.failed", new ExtractionFailedEventDto(
            extractSummary,
            "HTTP 403 Forbidden",
            "Spotify")));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("diagnostic.error", new DiagnosticErrorEventDto(
            "job",
            "HTTP 403 Forbidden",
            "Sockseek.Core.Extractors.SpotifyApiRequestException",
            "Sockseek.Core.Extractors.SpotifyApiRequestException: HTTP 403 Forbidden",
            extractSummary,
            workflowId,
            "Spotify")));

        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual(JobLog("[11] ExtractJob: Spotify: Input: https://open.spotify.com/playlist/123"), messages[0]);
        Assert.AreEqual(ErrorJobLog("[11] ExtractJob: Spotify: Failed: input.txt\n  Reason:    HTTP 403 Forbidden"), messages[1]);
        StringAssert.Contains(messages[2], "[11] ExtractJob: Spotify: diagnostic: SpotifyApiRequestException");
    }

    [TestMethod]
    public void EventLogger_DiagnosticError_PrintsExceptionDetail()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var extractSummary = CreateExtractSummary(
            Guid.NewGuid(),
            workflowId,
            ExpectedJobStatus.Failed,
            ServerProtocol.FailureReasons.ExtractionFailed);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.failed", new ExtractionFailedEventDto(
            extractSummary,
            "Object reference not set to an instance of an object.")));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("diagnostic.error", new DiagnosticErrorEventDto(
            "job",
            "Object reference not set to an instance of an object.",
            "System.NullReferenceException",
            "System.NullReferenceException: Object reference not set to an instance of an object.\n   at Sockseek.Core.Extractors.Spotify.GetTracks()",
            extractSummary,
            workflowId)));

        Assert.AreEqual(2, messages.Count);
        StringAssert.Contains(messages[0], "Reason:    Object reference not set to an instance of an object.");
        Assert.IsFalse(messages[0].Contains("Exception:", StringComparison.Ordinal));
        StringAssert.Contains(messages[1], "[11] ExtractJob: diagnostic: NullReferenceException");
        StringAssert.Contains(messages[1], "Exception:");
        StringAssert.Contains(messages[1], "System.NullReferenceException");
        StringAssert.Contains(messages[1], "at Sockseek.Core.Extractors.Spotify.GetTracks()");
    }

    [TestMethod]
    public void EventLogger_GenericFailure_PrintsDiagnosticDetailSeparately()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!);
        var summary = CreateAlbumSummary(
            Guid.NewGuid(),
            ExpectedJobStatus.Failed,
            ServerProtocol.FailureReasons.Other) with
        {
            FailureMessage = "Infrastructure failure: engine crashed",
            FailureDetail = "System.InvalidOperationException: engine crashed\n   at Sockseek.Core.DownloadEngine.RunAsync()",
        };

        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", summary));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("diagnostic.error", new DiagnosticErrorEventDto(
            "job",
            "Infrastructure failure: engine crashed",
            "System.InvalidOperationException",
            "System.InvalidOperationException: engine crashed\n   at Sockseek.Core.DownloadEngine.RunAsync()",
            summary,
            summary.WorkflowId)));

        Assert.AreEqual(2, messages.Count);
        StringAssert.Contains(messages[0], "failed [Unknown error]: Artist Album");
        StringAssert.Contains(messages[0], "Error: Infrastructure failure: engine crashed");
        Assert.IsFalse(messages[0].Contains("Exception:", StringComparison.Ordinal));
        StringAssert.Contains(messages[1], "[6] AlbumJob: diagnostic: InvalidOperationException");
        StringAssert.Contains(messages[1], "Exception:");
        StringAssert.Contains(messages[1], "System.InvalidOperationException");
        StringAssert.Contains(messages[1], "at Sockseek.Core.DownloadEngine.RunAsync()");
    }

    [TestMethod]
    public void EventLogger_SongFailure_PrintsDiagnosticDetailSeparately()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var query = new SongQueryDto("Artist", "Song", null, null, null, false);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            9,
            workflowId,
            query,
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Failed,
            ServerProtocol.FailureReasons.Other,
            DownloadPath: null,
            ChosenCandidate: null,
            FailureMessage: "Unhandled song failure")));
        var summary = WithState(CreateSongSummary(songId, workflowId, null) with { DisplayId = 9 }, ExpectedJobStatus.Failed, ServerProtocol.FailureReasons.Other, "Unhandled song failure");
        InvokePrivate(eventLogger, "HandleEvent", Envelope("diagnostic.error", new DiagnosticErrorEventDto(
            "job",
            "Unhandled song failure",
            "System.InvalidOperationException",
            "System.InvalidOperationException: Unhandled song failure\n   at Sockseek.Core.DownloadEngine.DownloadSong()",
            summary,
            workflowId)));

        Assert.AreEqual(2, messages.Count);
        StringAssert.Contains(messages[0], "Error: Unhandled song failure");
        Assert.IsFalse(messages[0].Contains("Exception:", StringComparison.Ordinal));
        StringAssert.Contains(messages[1], "[9] SongJob: diagnostic: InvalidOperationException");
        StringAssert.Contains(messages[1], "Exception:");
        StringAssert.Contains(messages[1], "System.InvalidOperationException");
        StringAssert.Contains(messages[1], "at Sockseek.Core.DownloadEngine.DownloadSong()");
    }

    [TestMethod]
    public void EventLogger_DiagnosticError_CanBeSuppressedForRemoteClients()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var eventLogger = new EventLogger(null!, includeDiagnosticDetails: false);
        var summary = CreateAlbumSummary(
            Guid.NewGuid(),
            ExpectedJobStatus.Failed,
            ServerProtocol.FailureReasons.Other);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("diagnostic.error", new DiagnosticErrorEventDto(
            "job",
            "engine crashed",
            "System.InvalidOperationException",
            "System.InvalidOperationException: engine crashed\n   at Sockseek.Core.DownloadEngine.RunAsync()",
            summary,
            summary.WorkflowId)));

        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public void EventLogger_RedundantActivityLogs_AreHiddenFromLiveRenderer()
    {
        SockseekLog.RemoveNonFileOutputs();
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry));

        var workflowId = Guid.NewGuid();
        var eventLogger = new EventLogger(null!);
        InvokePrivate(eventLogger, "HandleEvent", Envelope(
            "download.started",
            new DownloadStartedEventDto(
                Guid.NewGuid(),
                9,
                workflowId,
                new SongQueryDto("Artist", "Song", null, null, null, false),
                CreateFileCandidate("user", @"Music\Artist\Song.flac"))));

        Assert.AreEqual(1, entries.Count);
        var line = (TerminalLogLine)entries[0].Context!;
        Assert.IsFalse(line.ShowInLive);
        Assert.AreEqual("downloading: Artist - Song: user\\Music\\Artist\\Song.flac", line.Message);
    }

    [TestMethod]
    public void EventLogger_ErrorActivityLogs_AreShownInLiveRenderer()
    {
        SockseekLog.RemoveNonFileOutputs();
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var extractSummary = CreateExtractSummary(
            Guid.NewGuid(),
            workflowId,
            ExpectedJobStatus.Failed,
            ServerProtocol.FailureReasons.ExtractionFailed);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("extraction.failed", new ExtractionFailedEventDto(
            extractSummary,
            "Could not parse input",
            "List")));

        Assert.AreEqual(1, entries.Count);
        var line = (TerminalLogLine)entries[0].Context!;
        Assert.IsTrue(line.ShowInLive);
        Assert.AreEqual("List", line.Source);
        Assert.AreEqual("Failed", line.Highlight);
    }

    [TestMethod]
    public void EventLogger_SuccessfulTerminalActivityLogs_AreShownInLiveRenderer()
    {
        SockseekLog.RemoveNonFileOutputs();
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var query = new SongQueryDto("Artist", "Song", null, null, null, false);
        var candidate = CreateFileCandidate("user", @"Music\Artist\Song.flac");

        InvokePrivate(eventLogger, "HandleEvent", Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            9,
            workflowId,
            query,
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Succeeded,
            FailureReason: null,
            DownloadPath: null,
            ChosenCandidate: candidate)));

        Assert.AreEqual(1, entries.Count);
        var line = (TerminalLogLine)entries[0].Context!;
        Assert.IsTrue(line.ShowInLive);
        Assert.AreEqual("succeeded", line.Highlight);
        Assert.AreEqual("succeeded: Artist - Song: user\\Music\\Artist\\Song.flac", line.Message);
    }

    [TestMethod]
    public void EventLogger_AlreadyExistingJobListChildren_AreHiddenFromLiveRenderer()
    {
        SockseekLog.RemoveNonFileOutputs();
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var listId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var listSummary = new JobSummaryDto(
            listId,
            DisplayId: 5,
            WorkflowId: workflowId,
            Kind: ServerJobKind.JobList,
            LifecycleState: ServerJobLifecycleState.Running,
            ActivityPhase: ServerJobActivityPhase.RunningChildren,
            ActivityUntilUtc: null,
            TerminalOutcome: ServerJobTerminalOutcome.None,
            ItemName: "playlist",
            QueryText: "playlist",
            FailureReason: null,
            FailureMessage: null,
            ParentJobId: null,
            ResultJobId: null,
            SourceJobId: null,
            DiscoveryResultCount: null,
            DiscoveryLockedFileCount: null,
            AppliedAutoProfiles: [],
            AvailableActions: []);
        var songSummary = CreateSongSummary(songId, workflowId, listId);
        var query = new SongQueryDto("Artist", "Song", null, null, null, false);
        var candidate = CreateFileCandidate("user", @"Music\Artist\Song.flac");

        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", listSummary));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", songSummary));
        entries.Clear();

        InvokePrivate(eventLogger, "HandleEvent", Envelope("song.state-changed", new SongStateChangedEventDto(
            songId,
            9,
            workflowId,
            query,
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Skipped,
            ServerJobSkipReason.AlreadyExists,
            FailureReason: null,
            DownloadPath: null,
            ChosenCandidate: candidate)));

        Assert.AreEqual(1, entries.Count);
        var line = (TerminalLogLine)entries[0].Context!;
        Assert.IsFalse(line.ShowInLive);
        Assert.AreEqual("already exists", line.Highlight);
    }

    [TestMethod]
    public void EventLogger_AlbumTrackTerminalLogs_UseAlbumTrackDisplay()
    {
        SockseekLog.RemoveNonFileOutputs();
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry));

        var eventLogger = new EventLogger(null!);
        var workflowId = Guid.NewGuid();
        var albumJobId = Guid.NewGuid();
        var fileJobId = Guid.NewGuid();
        var albumSummary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null) with
        {
            WorkflowId = workflowId,
        };
        var childSummary = CreateSongSummary(fileJobId, workflowId, albumJobId);
        var query = new SongQueryDto("Artist", "Track", null, null, null, false);
        var candidate = CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac");

        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", albumSummary));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", childSummary));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("album.track-download-started", new AlbumTrackDownloadStartedEventDto(
            albumSummary,
            CreateSingleFileAlbumFolder(fileJobId, ExpectedJobStatus.Pending, null),
            [CreateSongPayload(fileJobId, ExpectedJobStatus.Pending, null)])));
        entries.Clear();

        InvokePrivate(eventLogger, "HandleEvent", Envelope("song.state-changed", new SongStateChangedEventDto(
            fileJobId,
            7,
            workflowId,
            query,
            ServerJobLifecycleState.Terminal,
            ServerJobActivityPhase.None,
            ActivityUntilUtc: null,
            ServerJobTerminalOutcome.Succeeded,
            FailureReason: null,
            DownloadPath: @"out\Track.flac",
            ChosenCandidate: candidate)));

        Assert.AreEqual(1, entries.Count);
        var line = (TerminalLogLine)entries[0].Context!;
        Assert.AreEqual("Album Track", line.JobType);
        Assert.AreEqual(TerminalLogKind.AlbumTrackDownloaded, line.Kind);
        Assert.AreEqual("succeeded: Artist Album: 01. Artist - Track.flac", line.Message);
        Assert.IsTrue(line.ShowInLive);
    }

    [TestMethod]
    public void TerminalLiveRenderer_WrapsWideUnicodeByCellWidth()
    {
        var text = "failed [No suitable file found]: サン";

        Assert.IsTrue(TerminalLiveRenderer.CellCount(text) > text.Length,
            "Japanese kana should count wider than one terminal cell per UTF-16 char.");

        var wrapped = TerminalLiveRenderer.WrapContentForWidth(text, text.Length + 1);

        Assert.IsTrue(wrapped.Count > 1,
            "Live log wrapping must use terminal cell width, not string.Length, so wide Unicode does not wrap underneath Spectre.Live.");
    }

    [TestMethod]
    public void TerminalLiveRenderer_DimsStructuredProcessLogPrefixes()
    {
        Assert.AreEqual(
            "[grey][[debug]] [[soulseek]] [/]" + "Logging in",
            TerminalLiveRenderer.FormatProcessLogMarkup(new TerminalProcessLogLine(
                LogLevel.Debug,
                SockseekLog.Categories.Soulseek,
                "Logging in",
                SockseekLog.LogRouting.All)));
    }

    [TestMethod]
    public void TerminalLiveRenderer_ColorsStatusAfterStructuredSourcePrefix()
    {
        Assert.AreEqual(
            "[grey][[002]] [/]" + "ExtractJob: Spotify: [red]Failed[/]: https://open.spotify.com/playlist/123",
            TerminalLiveRenderer.FormatLogMarkup(new TerminalLogLine(
                TerminalLogKind.JobFailed,
                "",
                2,
                "ExtractJob",
                "Failed: https://open.spotify.com/playlist/123",
                "Spotify",
                "Failed")));
    }

    [TestMethod]
    public void TerminalLiveRenderer_SourcePrefixText_IsMeasuredSeparately()
    {
        Assert.AreEqual("Spotify: ", TerminalLiveRenderer.SourcePrefixText("Spotify"));
        Assert.AreEqual("", TerminalLiveRenderer.SourcePrefixText(null));
    }

    [TestMethod]
    public void LiveSummaryVisibility_IncludesSearchWaitAndRateLimitButNotPending()
    {
        var summary = CreateSongSummary(Guid.NewGuid(), Guid.NewGuid(), parentJobId: null);

        var pending = summary with
        {
            LifecycleState = ServerJobLifecycleState.Pending,
            ActivityPhase = ServerJobActivityPhase.None,
        };
        Assert.IsFalse(CliProgressReporter.ShouldShowStandaloneSummaryInLiveTable(
            pending,
            CliJobStatusPresenter.ForSummary(pending)));

        var waiting = summary with
        {
            LifecycleState = ServerJobLifecycleState.Running,
            ActivityPhase = ServerJobActivityPhase.WaitingForSearchConcurrency,
        };
        Assert.IsTrue(CliProgressReporter.ShouldShowStandaloneSummaryInLiveTable(
            waiting,
            CliJobStatusPresenter.ForSummary(waiting)));

        var rateLimited = summary with
        {
            LifecycleState = ServerJobLifecycleState.Running,
            ActivityPhase = ServerJobActivityPhase.SearchRateLimited,
        };
        Assert.IsTrue(CliProgressReporter.ShouldShowStandaloneSummaryInLiveTable(
            rateLimited,
            CliJobStatusPresenter.ForSummary(rateLimited)));
    }

    [TestMethod]
    public void EventLogger_JobActivityChanged_DoesNotPrintPlainStatusLine()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var summary = CreateAlbumSummary(Guid.NewGuid(), ExpectedJobStatus.Searching, null) with
        {
            ActivityPhase = ServerJobActivityPhase.SearchRateLimited,
        };
        var eventLogger = new EventLogger(null!);

        InvokePrivate(eventLogger, "HandleEvent", Envelope(
            "job.activity-changed",
            new JobActivityChangedEventDto(summary)));

        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public void ProgressReporter_SearchRateLimited_NoProgressPrintsGlobalPlainLine()
    {
        using var output = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(output);
        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            InvokePrivate(reporter, "ReportSearchRateLimited", new SearchRateLimitedEventDto(DateTimeOffset.UtcNow.AddSeconds(30)));

            StringAssert.Contains(output.ToString(), "Search rate limit reached, resuming in");
        }
        finally
        {
            reporter.Stop();
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public void EventLogger_NoProgressMode_RoutesActivityLogsToConsoleAndNonConsole()
    {
        SockseekLog.RemoveNonFileOutputs();
        var consoleMessages = new List<string>();
        var sinkMessages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => consoleMessages.Add(message));
        SockseekLog.AddSink((_, message) => sinkMessages.Add(message));

        var workflowId = Guid.NewGuid();
        var eventLogger = new EventLogger(null!);
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
        StringAssert.StartsWith(consoleMessages[0], JobLog(@"[9] SongJob: downloading: Artist - Song: user\Music\Artist\Song.flac"));
        StringAssert.StartsWith(sinkMessages[0], @"[jobs] [9] SongJob: downloading: Artist - Song: user\Music\Artist\Song.flac");
    }


    [TestMethod]
    public void EventLogger_FailedAlbumUpsertThenStateChanged_PrintsOneTerminalLine()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var albumId = Guid.NewGuid();
        var summary = CreateAlbumSummary(albumId, ExpectedJobStatus.Failed, ServerProtocol.FailureReasons.Cancelled);
        var eventLogger = new EventLogger(null!);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", summary));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("album.state-changed", new AlbumStateChangedEventDto(summary)));

        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual(JobLog("[6] AlbumJob: cancelled: Artist Album"), messages[0]);
    }

    [TestMethod]
    public void EventLogger_FailedAlbumStateChangedThenUpsert_PrintsOneTerminalLine()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var albumId = Guid.NewGuid();
        var summary = CreateAlbumSummary(albumId, ExpectedJobStatus.Failed, ServerProtocol.FailureReasons.Cancelled);
        var eventLogger = new EventLogger(null!);

        InvokePrivate(eventLogger, "HandleEvent", Envelope("album.state-changed", new AlbumStateChangedEventDto(summary)));
        InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", summary));

        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual(JobLog("[6] AlbumJob: cancelled: Artist Album"), messages[0]);
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
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

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

            var eventLogger = new EventLogger(null!);
            var searching = Envelope("song.searching", new SongSearchingEventDto(
                song.Id,
                song.DisplayId,
                workflowId,
                new SongQueryDto("Artist", "Song", null, null, null, false)));
            InvokePrivate(eventLogger, "HandleEvent", searching);
            InvokePrivate(eventLogger, "HandleEvent", searching);

            Assert.AreEqual(1, messages.Count);
            Assert.AreEqual(JobLog($"[{song.DisplayId}] SongJob: searching: Artist - Song"), messages[0]);
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void SongLifecycle_NoProgress_UsesJobFormatForDownloadAndTerminalState()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

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
            var eventLogger = new EventLogger(null!);
            InvokePrivate(eventLogger, "HandleEvent", Envelope("download.started", new DownloadStartedEventDto(song.Id, song.DisplayId, workflowId, query, candidateDto)));
            InvokePrivate(eventLogger, "HandleEvent", Envelope("song.state-changed", new SongStateChangedEventDto(
                song.Id, song.DisplayId, workflowId, query,
                ServerJobLifecycleState.Terminal,
                ServerJobActivityPhase.None,
                null,
                ServerJobTerminalOutcome.Succeeded,
                null, null, candidateDto)));

            Assert.AreEqual(2, messages.Count);
            StringAssert.StartsWith(messages[0], JobLog($"[{song.DisplayId}] SongJob: downloading: "));
            StringAssert.StartsWith(messages[1], JobLog($"[{song.DisplayId}] SongJob: succeeded: "));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void SongFailure_NoProgress_PrintsFailureMessage()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var workflowId = Guid.NewGuid();
            var songId = Guid.NewGuid();
            var query = new SongQueryDto("Artist", "Song", null, null, null, false);
            var candidate = CreateFileCandidate("user", @"Music\Artist\Song.flac");

            var eventLogger = new EventLogger(null!);
            var summary = WithState(CreateSongSummary(songId, workflowId, null) with
            {
                DisplayId = 12,
                ItemName = "Artist - Song",
                QueryText = "Artist - Song",
            }, ExpectedJobStatus.Failed, ServerProtocol.FailureReasons.AllDownloadsFailed, "Connection reset by peer");
            InvokePrivate(eventLogger, "HandleEvent", Envelope("song.state-changed", new SongStateChangedEventDto(
                songId,
                DisplayId: 12,
                workflowId,
                query,
                ServerJobLifecycleState.Terminal,
                ServerJobActivityPhase.None,
                ActivityUntilUtc: null,
                ServerJobTerminalOutcome.Failed,
                ServerProtocol.FailureReasons.AllDownloadsFailed,
                DownloadPath: null,
                ChosenCandidate: candidate,
                FailureMessage: "Connection reset by peer")));
 
            CollectionAssert.AreEqual(new[]
            {
                JobLog("[12] SongJob: failed [All downloads failed]: Artist - Song: user\\Music\\Artist\\Song.flac\n    Error: Connection reset by peer"),
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
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

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
                ErrorJobLog("[12] SongJob: download error: Artist - Song: user\\Music\\Artist\\Song.flac\n    Output: out\\Song.flac.incomplete\n    Attempt: 1/3\n    SoulseekClientException: Connection reset by peer\n    Soulseek.SoulseekClientException: Connection reset by peer"),
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
            song.SetCancelled(JobCancellationSource.ParentJob);

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
                ServerJobLifecycleState.Terminal,
                ServerJobActivityPhase.None,
                ActivityUntilUtc: null,
                ServerJobTerminalOutcome.Cancelled,
                ServerProtocol.FailureReasons.Cancelled,
                DownloadPath: null,
                ChosenCandidate: candidateDto));

            Assert.AreEqual("cancelled", GetField<string>(barData, "StateLabel"));
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
                LifecycleState: ServerJobLifecycleState.Running,
                ActivityPhase: ServerJobActivityPhase.Downloading,
                ActivityUntilUtc: null,
                TerminalOutcome: ServerJobTerminalOutcome.None,
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
                [CreateSongPayload(fileJobId, ExpectedJobStatus.Pending, null)]));
            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));

            var failedSummary = summary with
            {
                LifecycleState = ServerJobLifecycleState.Terminal,
                ActivityPhase = ServerJobActivityPhase.None,
                ActivityUntilUtc = null,
                TerminalOutcome = ServerJobTerminalOutcome.Cancelled,
                FailureReason = ServerProtocol.FailureReasons.Cancelled,
            };
            InvokePrivate(reporter, "ReportAlbumStateChanged", new AlbumStateChangedEventDto(failedSummary));

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
            var summary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null);
            var folder = CreateSingleFileAlbumFolder(fileJobId, ExpectedJobStatus.Pending, null);

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                summary,
                folder,
                [CreateSongPayload(fileJobId, ExpectedJobStatus.Pending, null)]));
            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));

            InvokePrivate(
                reporter,
                "ReportJobUpserted",
                CreateAlbumSummary(albumJobId, ExpectedJobStatus.Failed, ServerProtocol.FailureReasons.Cancelled));

            Assert.IsFalse(
                HasBackendBarData(reporter, fileJobId),
                "Terminal album job upserts should reconcile leftover requested bars even if album.state-changed has not arrived.");
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
            var albumSummary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var childSummary = CreateSongSummary(fileJobId, workflowId, albumJobId);

            InvokePrivate(reporter, "ReportJobUpserted", albumSummary);
            InvokePrivate(reporter, "ReportJobUpserted", childSummary);
            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobId, ExpectedJobStatus.Pending, null),
                [CreateSongPayload(fileJobId, ExpectedJobStatus.Pending, null)]));
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
            var albumSummary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null) with
            {
                WorkflowId = workflowId,
            };

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobId, ExpectedJobStatus.Pending, null),
                [CreateSongPayload(fileJobId, ExpectedJobStatus.Pending, null)]));

            InvokePrivate(reporter, "ReportStateChanged", new SongStateChangedEventDto(
                fileJobId,
                DisplayId: 7,
                workflowId,
                new SongQueryDto("Artist", "Track", null, null, null, false),
                ServerJobLifecycleState.Terminal,
                ServerJobActivityPhase.None,
                ActivityUntilUtc: null,
                ServerJobTerminalOutcome.Succeeded,
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
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var workflowId = Guid.NewGuid();
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var albumSummary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var songSummary = CreateSongSummary(fileJobId, workflowId, albumJobId);
            var query = new SongQueryDto("Artist", "Track", null, null, null, false);
            var candidate = CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac");

            var eventLogger = new EventLogger(null!);
            InvokePrivate(eventLogger, "HandleEvent", Envelope("album.track-download-started", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobId, ExpectedJobStatus.Pending, null),
                [CreateSongPayload(fileJobId, ExpectedJobStatus.Pending, null)])));
            InvokePrivate(eventLogger, "HandleEvent", Envelope("job.upserted", songSummary));
            InvokePrivate(eventLogger, "HandleEvent", Envelope("download.started", new DownloadStartedEventDto(fileJobId, 7, workflowId, query, candidate)));
            InvokePrivate(eventLogger, "HandleEvent", Envelope("song.state-changed", new SongStateChangedEventDto(
                fileJobId, 7, workflowId, query,
                ServerJobLifecycleState.Terminal,
                ServerJobActivityPhase.None,
                null,
                ServerJobTerminalOutcome.Succeeded,
                null, @"out\Track.flac", candidate)));

            Assert.AreEqual(3, messages.Count);
            Assert.AreEqual(JobLog(@"[6] AlbumJob: downloading tracks: Artist Album - Artist\Album"), messages[0]);
            Assert.AreEqual(JobLog(@"[7] SongJob: downloading: Artist - Track: local\Artist\Album\01. Artist - Track.flac"), messages[1]);
            Assert.AreEqual(JobLog(@"[6] Album Track: succeeded: Artist Album: 01. Artist - Track.flac"), messages[2]);
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
            var albumSummary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var fileJobIds = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
            var tracks = fileJobIds
                .Select(id => CreateSongPayload(id, ExpectedJobStatus.Pending, null))
                .ToList();

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(fileJobIds[0], ExpectedJobStatus.Pending, null),
                tracks));

            Parallel.ForEach(fileJobIds, fileJobId =>
            {
                InvokePrivate(reporter, "ReportStateChanged", new SongStateChangedEventDto(
                    fileJobId,
                    DisplayId: 7,
                    workflowId,
                    new SongQueryDto("Artist", "Track", null, null, null, false),
                    ServerJobLifecycleState.Terminal,
                    ServerJobActivityPhase.None,
                    ActivityUntilUtc: null,
                    ServerJobTerminalOutcome.Cancelled,
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
            var albumSummary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var dynamicSummary = CreateSongSummary(dynamicFileJobId, workflowId, albumJobId);
            var query = new SongQueryDto("Artist", "Bonus", null, null, null, false);
            var candidate = CreateFileCandidate("local", @"Artist\Album\Disc 2\02. Artist - Bonus.flac");

            InvokePrivate(reporter, "ReportJobUpserted", albumSummary);
            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(initialFileJobId, ExpectedJobStatus.Pending, null),
                [CreateSongPayload(initialFileJobId, ExpectedJobStatus.Pending, null)]));
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
    public void InlineAlbumChildAddedDuringAlbumProgress_DoesNotRaceAlbumSongEnumeration()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var workflowId = Guid.NewGuid();
            var albumJobId = Guid.NewGuid();
            var initialFileJobId = Guid.NewGuid();
            var albumSummary = CreateAlbumSummary(albumJobId, ExpectedJobStatus.Downloading, null) with
            {
                WorkflowId = workflowId,
            };
            var dynamicFileJobIds = Enumerable.Range(0, 250).Select(_ => Guid.NewGuid()).ToArray();

            InvokePrivate(reporter, "ReportJobUpserted", albumSummary);
            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(
                albumSummary,
                CreateSingleFileAlbumFolder(initialFileJobId, ExpectedJobStatus.Pending, null),
                [CreateSongPayload(initialFileJobId, ExpectedJobStatus.Pending, null)]));

            var errors = new ConcurrentQueue<Exception>();
            var addChildren = Task.Run(() =>
            {
                foreach (var fileJobId in dynamicFileJobIds)
                {
                    try
                    {
                        var summary = CreateSongSummary(fileJobId, workflowId, albumJobId);
                        var query = new SongQueryDto("Artist", "Bonus", null, null, null, false);
                        var candidate = CreateFileCandidate("local", $@"Artist\Album\Disc 2\{fileJobId:N}.flac");

                        InvokePrivate(reporter, "ReportJobUpserted", summary);
                        InvokePrivate(reporter, "ReportDownloadStart", new DownloadStartedEventDto(fileJobId, 8, workflowId, query, candidate));
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }
            });
            var readAlbumCounts = Task.Run(() =>
            {
                for (int i = 0; i < dynamicFileJobIds.Length * 20; i++)
                {
                    try
                    {
                        _ = GetBackendAlbumDoneCount(reporter, albumJobId);
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }
            });

            Task.WaitAll(addChildren, readAlbumCounts);

            if (errors.TryPeek(out var first))
                Assert.Fail(first.ToString());
            Assert.AreEqual(dynamicFileJobIds.Length + 1, GetBackendAlbumTrackCount(reporter, albumJobId));
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
                LifecycleState: ServerJobLifecycleState.Running,
                ActivityPhase: ServerJobActivityPhase.RunningChildren,
                ActivityUntilUtc: null,
                TerminalOutcome: ServerJobTerminalOutcome.None,
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
                LifecycleState: ServerJobLifecycleState.Running,
                ActivityPhase: ServerJobActivityPhase.RunningChildren,
                ActivityUntilUtc: null,
                TerminalOutcome: ServerJobTerminalOutcome.None,
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
            InvokePrivate(reporter, "RememberStructure", CreateAlbumSummary(albumId, ExpectedJobStatus.Downloading, null) with
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

    private static JobSummaryDto CreateAlbumSummary(Guid jobId, ExpectedJobStatus state, ServerJobFailureReason? failureReason)
    {
        var split = Split(state, failureReason);
        return new(
            jobId,
            DisplayId: 6,
            WorkflowId: Guid.NewGuid(),
            Kind: ServerJobKind.Album,
            LifecycleState: split.LifecycleState,
            ActivityPhase: split.ActivityPhase,
            ActivityUntilUtc: null,
            TerminalOutcome: split.TerminalOutcome,
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
    }

    private static JobSummaryDto CreateSongSummary(Guid jobId, Guid workflowId, Guid? parentJobId)
        => new(
            jobId,
            DisplayId: 7,
            WorkflowId: workflowId,
            Kind: ServerJobKind.Song,
            LifecycleState: ServerJobLifecycleState.Running,
            ActivityPhase: ServerJobActivityPhase.Searching,
            ActivityUntilUtc: null,
            TerminalOutcome: ServerJobTerminalOutcome.None,
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

    private static JobSummaryDto CreateExtractSummary(Guid jobId, Guid workflowId, ExpectedJobStatus state, ServerJobFailureReason? failureReason)
    {
        var split = Split(state, failureReason);
        return new(
            jobId,
            DisplayId: 11,
            WorkflowId: workflowId,
            Kind: ServerJobKind.Extract,
            LifecycleState: split.LifecycleState,
            ActivityPhase: split.ActivityPhase,
            ActivityUntilUtc: null,
            TerminalOutcome: split.TerminalOutcome,
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
    }

    private static AlbumFolderDto CreateSingleFileAlbumFolder(Guid fileJobId, ExpectedJobStatus state, ServerJobFailureReason? failureReason)
        => new(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            "local",
            @"Artist\Album",
            new PeerInfoDto("local"),
            FileCount: 1,
            AudioFileCount: 1,
            Files: [CreateFileCandidate("local", @"Artist\Album\01. Artist - Track.flac")]);

    private static SongJobPayloadDto CreateSongPayload(Guid fileJobId, ExpectedJobStatus state, ServerJobFailureReason? failureReason)
    {
        var split = Split(state, failureReason);
        return new(
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
            LifecycleState: split.LifecycleState,
            ActivityPhase: split.ActivityPhase,
            ActivityUntilUtc: null,
            TerminalOutcome: split.TerminalOutcome,
            SkipReason: split.SkipReason,
            FailureReason: failureReason,
            FailureMessage: null);
    }

    private static JobSummaryDto WithState(
        JobSummaryDto summary,
        ExpectedJobStatus state,
        ServerJobFailureReason? failureReason = null,
        string? failureMessage = null)
    {
        var split = Split(state, failureReason);
        return summary with
        {
            LifecycleState = split.LifecycleState,
            ActivityPhase = split.ActivityPhase,
            ActivityUntilUtc = null,
            TerminalOutcome = split.TerminalOutcome,
            SkipReason = split.SkipReason,
            FailureReason = failureReason,
            FailureMessage = failureMessage,
        };
    }

    private static (ServerJobLifecycleState LifecycleState, ServerJobActivityPhase ActivityPhase, ServerJobTerminalOutcome TerminalOutcome, ServerJobSkipReason SkipReason) Split(
        ExpectedJobStatus state,
        ServerJobFailureReason? reason = null)
        => state switch
        {
            ExpectedJobStatus.Pending => (ServerJobLifecycleState.Pending, ServerJobActivityPhase.None, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.Searching => (ServerJobLifecycleState.Running, ServerJobActivityPhase.Searching, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.Downloading => (ServerJobLifecycleState.Running, ServerJobActivityPhase.Downloading, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.RunningOnComplete => (ServerJobLifecycleState.Running, ServerJobActivityPhase.RunningOnComplete, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.Extracting => (ServerJobLifecycleState.Running, ServerJobActivityPhase.Extracting, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.RunningChildren => (ServerJobLifecycleState.Running, ServerJobActivityPhase.RunningChildren, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.AwaitingSelection => (ServerJobLifecycleState.AwaitingSelection, ServerJobActivityPhase.None, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
            ExpectedJobStatus.Succeeded => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Succeeded, ServerJobSkipReason.None),
            ExpectedJobStatus.AlreadyExists => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Skipped, ServerJobSkipReason.AlreadyExists),
            ExpectedJobStatus.Skipped => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Skipped, ServerJobSkipReason.Manual),
            ExpectedJobStatus.NotFoundLastTime => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Skipped, ServerJobSkipReason.NotFoundLastTime),
            ExpectedJobStatus.Failed when reason == ServerJobFailureReason.Cancelled => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Cancelled, ServerJobSkipReason.None),
            ExpectedJobStatus.Failed => (ServerJobLifecycleState.Terminal, ServerJobActivityPhase.None, ServerJobTerminalOutcome.Failed, ServerJobSkipReason.None),
            _ => (ServerJobLifecycleState.Running, ServerJobActivityPhase.None, ServerJobTerminalOutcome.None, ServerJobSkipReason.None),
        };

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
    [TestMethod]
    public void Printing_PrintComplete_CountsUserFacingJobsNotAlbumFiles()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var first = CompletedAlbum("Artist One", "Album One", 8);
        var second = CompletedAlbum("Artist Two", "Album Two", 12);
        var failed = new AlbumJob(new AlbumQuery { Artist = "Artist Three", Album = "Album Three" });
        failed.Fail(JobFailureReason.NoSuitableFileFound);

        var queue = new JobList("root", [first, second, failed]);

        Printing.PrintComplete(queue);

        Assert.IsTrue(
            messages.Any(message => message.Contains("Completed: 2 succeeded, 1 failed.", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, messages));
    }

    [TestMethod]
    public void Printing_PrintPlannedOutput_DoesNotPrintFailedExtractAsDownload()
    {
        SockseekLog.RemoveNonFileOutputs();
        var messages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => messages.Add(message));

        var extract = new ExtractJob("input.txt", InputType.List);
        extract.Config = new DownloadSettings { PrintOption = PrintOption.Tracks };
        extract.Fail(JobFailureReason.ExtractionFailed, "Could not parse input");
        var queue = new JobList("root", [extract]);

        Printing.PrintPlannedOutput(queue);

        Assert.IsFalse(
            messages.Any(message => message.Contains("Downloading", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, messages));
        Assert.AreEqual(0, messages.Count, string.Join(Environment.NewLine, messages));
    }

    private static AlbumJob CompletedAlbum(string artist, string album, int trackCount)
    {
        var files = Enumerable.Range(1, trackCount)
            .Select(i =>
            {
                var song = new SongJob(new SongQuery { Artist = artist, Title = $"Track {i}" });
                song.SetDone();
                return song;
            })
            .ToList();

        var job = new AlbumJob(new AlbumQuery { Artist = artist, Album = album })
        {
            ResolvedTarget = new AlbumFolder("local", $@"{artist}\{album}", files),
        };
        job.SetDone(Path.Combine("output", album));
        return job;
    }

}
