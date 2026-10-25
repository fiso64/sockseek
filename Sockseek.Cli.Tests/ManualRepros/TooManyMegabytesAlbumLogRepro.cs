using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Sockseek.Cli;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Tests.ClientTests;
using SlskFile = Soulseek.File;

namespace Tests.Cli.ManualRepros;

public static class TooManyMegabytesAlbumLogRepro
{
    private const string Artist = "Artist Gamma";
    private const string Album = "Album Three";
    private const string Username = "SampleUser";
    private const string RemoteFolder = @"ArtistGamma\Album Three";

    private static readonly MethodInfo ReportDownloadAttemptFailedMethod =
        typeof(CliProgressReporter).GetMethod("ReportDownloadAttemptFailed", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(nameof(CliProgressReporter), "ReportDownloadAttemptFailed");

    private static readonly MethodInfo ReportAlbumTrackDownloadStartedMethod =
        typeof(CliProgressReporter).GetMethod("ReportAlbumTrackDownloadStarted", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(nameof(CliProgressReporter), "ReportAlbumTrackDownloadStarted");

    private static readonly FieldInfo NextDisplayIdField =
        typeof(Job).GetField("_nextDisplayId", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(Job), "_nextDisplayId");

    public static async Task WriteArtifactsAsync(string artifactDir, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(artifactDir);

        var liveLines = await CaptureAsync(ReproRenderMode.Live, artifactDir, cancellationToken);
        await System.IO.File.WriteAllLinesAsync(Path.Combine(artifactDir, "live-render.txt"), liveLines, cancellationToken);

        var noProgressLines = await CaptureAsync(ReproRenderMode.NoProgress, artifactDir, cancellationToken);
        await System.IO.File.WriteAllLinesAsync(Path.Combine(artifactDir, "no-progress.txt"), noProgressLines, cancellationToken);

        var logFileLines = await CaptureAsync(ReproRenderMode.LogFile, artifactDir, cancellationToken);
        await System.IO.File.WriteAllLinesAsync(Path.Combine(artifactDir, "log-file.txt"), logFileLines, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> CaptureAsync(
        ReproRenderMode mode,
        string artifactDir,
        CancellationToken cancellationToken)
    {
        var outputDir = Path.Combine(artifactDir, WorkDirectoryName(mode));
        if (System.IO.Directory.Exists(outputDir))
            System.IO.Directory.Delete(outputDir, recursive: true);
        System.IO.Directory.CreateDirectory(outputDir);
        NextDisplayIdField.SetValue(null, 0);

        var wishlistPath = Path.Combine(outputDir, "wishlist.txt");
        var logFilePath = mode == ReproRenderMode.LogFile ? Path.Combine(outputDir, "sockseek.log") : null;
        await System.IO.File.WriteAllLinesAsync(wishlistPath, CreateWishlistLines(), cancellationToken);

        SockseekLog.RemoveNonFileOutputs();
        SockseekLog.RemoveFileOutputs();
        var lines = new ConcurrentQueue<string>();
        if (logFilePath != null)
        {
            if (System.IO.File.Exists(logFilePath))
                System.IO.File.Delete(logFilePath);

            SockseekLog.AddOrReplaceFile(logFilePath, LogLevel.Debug);
        }
        else
        {
            SockseekLog.AddStructuredConsoleSink(
                (entry, _) =>
                {
                    var outputEvent = CliOutputEvent.FromLogEntry(entry);
                    if (mode == ReproRenderMode.Live
                        && outputEvent is CliOutputEvent.JobLog { Line.ShowInLive: false })
                    {
                        return;
                    }

                    lines.Enqueue(CliLogStyle.FormatOutputEventText(outputEvent));
                },
                LogLevel.Information);
        }

        var files = CreateAlbumFiles();
        var response = new SearchResponse(Username, 1, true, 100_000, 0, files);
        var client = CreateClient(response, files);
        var engineSettings = new EngineSettings
        {
            Username = "test_user",
            Password = "test_pass",
            LogFilePath = logFilePath,
        };
        var settings = new DownloadSettings();
        settings.Extraction.Input = wishlistPath;
        settings.Extraction.InputType = InputType.List;
        settings.Search.MaxStaleTime = 80_000;
        settings.Search.NecessaryCond.Formats = ["flac", "mp3"];
        settings.Search.NecessaryCond.MinBitrate = 200;
        settings.Output.ParentDir = outputDir;
        settings.Output.NameFormat = "{foldername}/{filename}";
        settings.Output.WriteIndex = false;

        var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings, client));
        var backend = new LocalCliBackend(engine, settings);
        var reporter = new CliProgressReporter(new CliSettings
        {
            NoProgress = mode != ReproRenderMode.Live,
        });

        if (mode == ReproRenderMode.Live)
        {
            backend.EventReceived += envelope =>
            {
                if (envelope.Type == "album.track-download-started"
                    && envelope.Payload is AlbumTrackDownloadStartedEventDto albumTrack)
                {
                    ReportAlbumTrackDownloadStartedMethod.Invoke(reporter, [albumTrack]);
                }
                else if (envelope.Type == "download.attempt-failed"
                    && envelope.Payload is DownloadAttemptFailedEventDto failure)
                {
                    ReportDownloadAttemptFailedMethod.Invoke(reporter, [failure]);
                }
            };
        }
        else
        {
            reporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, includeDiagnosticDetails: false);
        eventLogger.Attach();

        engine.Enqueue(new ExtractJob(settings.Extraction.Input, settings.Extraction.InputType), settings);
        engine.CompleteEnqueue();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await engine.RunAsync(timeout.Token);
        }
        finally
        {
            reporter.Stop();
            SockseekLog.RemoveNonFileOutputs();
            SockseekLog.RemoveFileOutputs();
        }

        if (logFilePath != null && System.IO.File.Exists(logFilePath))
            return await System.IO.File.ReadAllLinesAsync(logFilePath, cancellationToken);

        return lines.ToArray();
    }

    private static string WorkDirectoryName(ReproRenderMode mode)
        => mode switch
        {
            ReproRenderMode.Live => "work-live",
            ReproRenderMode.NoProgress => "work-no-progress",
            ReproRenderMode.LogFile => "work-log-file",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    private static MockSoulseekClient CreateClient(SearchResponse response, IReadOnlyList<SlskFile> files)
    {
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return new MockSoulseekClient([response])
        {
            BeforeDownloadStartsAsync = async (_, remoteFilename, _) =>
            {
                if (Interlocked.Increment(ref started) >= files.Count)
                    allStarted.TrySetResult();

                await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                if (remoteFilename.Contains("01 Track 01", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SoulseekClientException(
                        "Failed to establish a direct or indirect transfer connection to SampleUser (10.0.0.1:12345)");
                }

                throw new TransferRejectedException("Transfer rejected: Too many megabytes");
            },
        };
    }

    private static List<SlskFile> CreateAlbumFiles()
    {
        var tracks = new (int Number, string Suffix)[]
        {
            (1, "House Bootleg"),
            (12, "Hardtek Mix"),
            (13, "Mix Version"),
            (14, "Remastered"),
            (15, "Club Edit"),
            (16, "Bootleg Version"),
            (17, "Bootleg Version"),
            (18, "Remix"),
            (19, "Remix"),
            (20, "Bootleg Version"),
            (21, "Edit"),
            (22, "Bootleg Version"),
            (23, "Bootleg Version"),
            (24, "Rework"),
            (25, "Bootleg Version"),
            (26, "Bootleg Version"),
        };

        return tracks.Select((track, index) =>
        {
            var filename = $@"{RemoteFolder}\{Album} - {track.Number:D2} Track {track.Number:D2} ({track.Suffix}).flac";
            return new SlskFile(
                index + 1,
                filename,
                size: 35_000_000 + index,
                extension: ".flac",
                attributeList:
                [
                    new FileAttribute(FileAttributeType.Length, 180 + index),
                    new FileAttribute(FileAttributeType.BitRate, 950),
                ]);
        }).ToList();
    }

    private static string[] CreateWishlistLines()
        =>
        [
            "a:\"Artist Alpha - Release One EP\"",
            "a:\"Artist Beta - Release Two\"",
            $"a:\"{Artist} - {Album}\"",
            "a:\"Artist Delta - Release Four\"",
            "a:\"Artist Delta - Release Five #001\"",
            "a:\"Artist Epsilon - Release Six\"",
            "a:\"Artist Zeta - Release Seven\"",
            "a:\"Artist Eta - Release Eight\"",
            "a:\"Artist Theta - Release Nine\"",
            "a:\"Artist Iota - Release Ten\"",
        ];

    private enum ReproRenderMode
    {
        Live,
        NoProgress,
        LogFile,
    }
}
