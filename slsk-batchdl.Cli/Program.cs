using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Server;
using Soulseek;

namespace Sldl.Cli;

internal static partial class Program
{
    public static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (Help.PrintAndExitIfNeeded(args))
            return;

        bool daemonMode = args.Length > 0 && string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase);
        var bindArgs = daemonMode ? args.Skip(1).ToArray() : args;

        Logger.SetupExceptionHandling();
        Logger.AddConsole(writer: (msg, color) => Printing.WriteLine(msg, color));

        string configPath = ConfigManager.ExtractConfigPath(bindArgs);
        var configFile = ConfigManager.Load(configPath);
        var (engineSettings, rootSettings, cliSettings, daemonSettings, remoteSettings) = ConfigManager.BindAll(configFile, bindArgs);
        ConfigManager.ApplyAutoProfileCliSettings(configFile, rootSettings, cliSettings);

        if (!string.IsNullOrWhiteSpace(engineSettings.LogFilePath))
            Logger.AddOrReplaceFile(engineSettings.LogFilePath);

        Logger.SetConsoleLogLevel(rootSettings.NonVerbosePrint ? Logger.LogLevel.Error : engineSettings.LogLevel);

        if (daemonMode)
        {
            await RunDaemonAsync(bindArgs, configFile, engineSettings, rootSettings, daemonSettings);
            return;
        }

        var cts = new CancellationTokenSource();

        if (remoteSettings.IsEnabled)
        {
            try
            {
                await RunRemoteAsync(bindArgs, rootSettings, cliSettings, remoteSettings, cts);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Fatal(ex.Message);
            }
            return;
        }

        var clientManager = new SoulseekClientManager(engineSettings);

        if (string.IsNullOrEmpty(rootSettings.Extraction.Input))
        {
            var diagnostic = new DiagnosticService(clientManager);
            try
            {
                await diagnostic.PerformNoInputActions(rootSettings.PrintOption, rootSettings.Output.IndexFilePath, cts.Token);
            }
            catch (Exception ex)
            {
                Logger.Fatal($"Diagnostic action failed: {ex.Message}");
            }
            return;
        }

        var jobSettingsResolver = ConfigManager.CreateJobSettingsResolver(configFile, bindArgs, cliSettings);
        var engine = new DownloadEngine(engineSettings, clientManager, jobSettingsResolver);
        var backend = new LocalCliBackend(engine, rootSettings);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else if (ShouldAttachHumanProgressReporter(rootSettings.PrintOption))
        {
            cliReporter = new CliProgressReporter(cliSettings);
            cliReporter.Attach(backend);
        }
        backend.EventReceived += envelope =>
        {
            if (envelope.Type == "track-batch.resolved"
                && envelope.Payload is TrackBatchResolvedEventDto batch
                && !batch.PrintOption.HasFlag(PrintOption.Tracks)
                && ShouldPrintHumanBatchPreview(batch.PrintOption))
            {
                PrintTrackBatchResolved(batch);
            }
        };

        if (cliSettings.InteractiveMode)
        {
            var interactiveCoordinator = new InteractiveCliCoordinator(engine, cliSettings, cts.Token, backend);
            interactiveCoordinator.Start(
                new ExtractJob(rootSettings.Extraction.Input, rootSettings.Extraction.InputType),
                rootSettings);
        }
        else
        {
            await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    rootSettings.Extraction.Input,
                    rootSettings.Extraction.InputType.ToString()),
                cts.Token);
            engine.CompleteEnqueue();
        }

        ConsoleInputManager.Reporter = cliReporter;
        ConsoleInputManager.OnCancelRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Console.WriteLine();
                Printing.Write("Cancel job ID or all jobs? id/[A]ll/n=Esc: ", ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();

            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
            {
                Logger.LogNonConsole(Logger.LogLevel.Info, "Cancelling all jobs...");
                Printing.WriteLine("Cancelling all jobs...", ConsoleColor.Gray, force: true);
                engine.Cancel();
                return;
            }

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
            {
                if (await backend.CancelJobByDisplayIdAsync(id, ct: cts.Token))
                {
                    Logger.Info($"Cancelling job [{id}]...");
                }
                else
                {
                    Logger.Error($"Job ID [{id}] not found.");
                }
            }
            else
            {
                Logger.Error($"Invalid input '{result.Input}'.");
            }
        };

        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        try
        {
            await engine.RunAsync(cts.Token);
            Printing.PrintComplete(engine.Queue);

            if (rootSettings.DoNotDownload)
                Printing.PrintPlannedOutput(engine.Queue);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            engine.Cancel();
            cts.Cancel();
            Printing.SetBuffering(false);
            Printing.Flush();
        }
    }

    private static async Task RunRemoteAsync(
        string[] args,
        DownloadSettings rootSettings,
        CliSettings cliSettings,
        RemoteSettings remoteSettings,
        CancellationTokenSource cts)
    {
        if (string.IsNullOrWhiteSpace(rootSettings.Extraction.Input))
        {
            Logger.Fatal("Remote mode requires an input.");
            return;
        }

        await using var backend = new RemoteCliBackend(remoteSettings.ServerUrl!);
        await backend.StartAsync(cts.Token);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else if (ShouldAttachHumanProgressReporter(rootSettings.PrintOption))
        {
            cliReporter = new CliProgressReporter(cliSettings);
            cliReporter.Attach(backend);
        }

        backend.EventReceived += envelope =>
        {
            if (envelope.Type == "track-batch.resolved"
                && envelope.Payload is TrackBatchResolvedEventDto batch
                && !batch.PrintOption.HasFlag(PrintOption.Tracks)
                && ShouldPrintHumanBatchPreview(batch.PrintOption))
            {
                PrintTrackBatchResolved(batch);
            }
        };

        var request = new SubmitExtractJobRequestDto(
            rootSettings.Extraction.Input,
            rootSettings.Extraction.InputType.ToString(),
            Options:
            BuildRemoteSubmissionOptions(args, cliSettings));

        RemoteInteractiveCliCoordinator? interactiveCoordinator = null;
        JobSummaryDto submission;
        if (cliSettings.InteractiveMode)
        {
            interactiveCoordinator = new RemoteInteractiveCliCoordinator(backend, cliSettings, cts.Token);
            submission = await interactiveCoordinator.StartAsync(request, cts.Token);
        }
        else
        {
            submission = await backend.SubmitExtractJobAsync(request, cts.Token);
        }

        await backend.SubscribeWorkflowAsync(submission.WorkflowId, cts.Token);

        ConsoleInputManager.Reporter = cliReporter;
        ConsoleInputManager.OnCancelRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Console.WriteLine();
                Printing.Write("Cancel job ID or current workflow? id/[A]ll/n=Esc: ", ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();

            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
            {
                Logger.LogNonConsole(Logger.LogLevel.Info, "Cancelling workflow...");
                Printing.WriteLine("Cancelling workflow...", ConsoleColor.Gray, force: true);
                await backend.CancelWorkflowAsync(submission.WorkflowId, cts.Token);
                return;
            }

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
            {
                if (await backend.CancelJobByDisplayIdAsync(id, submission.WorkflowId, cts.Token))
                    Logger.Info($"Cancelling job [{id}]...");
                else
                    Logger.Error($"Job ID [{id}] not found.");
            }
            else
            {
                Logger.Error($"Invalid input '{result.Input}'.");
            }
        };

        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        try
        {
            if (interactiveCoordinator != null)
                await interactiveCoordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token);
            else
                await WaitForRemoteWorkflowAsync(backend, submission.WorkflowId, cts.Token);

            if (!rootSettings.DoNotDownload)
                await PrintRemoteCompleteAsync(backend, submission.WorkflowId, cts.Token);

            if (rootSettings.PrintResults)
                await PrintRemoteResultsAsync(backend, submission.WorkflowId, rootSettings, cts.Token);
            else if (rootSettings.PrintTracks)
                await PrintRemotePlannedOutputAsync(backend, submission.WorkflowId, rootSettings, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            cts.Cancel();
            cliReporter?.Stop();
            Printing.SetBuffering(false);
            Printing.Flush();
        }
    }

    private static async Task WaitForRemoteWorkflowAsync(ICliBackend backend, Guid workflowId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var workflow = await backend.GetWorkflowAsync(workflowId, ct);
            if (workflow?.Summary.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed)
                return;

            await Task.Delay(200, ct);
        }
    }

    private static void PrintTrackBatchResolved(TrackBatchResolvedEventDto batch)
    {
        bool needsRows = (batch.PrintOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
        if (needsRows)
        {
            Printing.PrintTracksTbd(
                batch.Pending.Select(ToSongJob).ToList(),
                batch.Existing.Select(ToSongJob).ToList(),
                batch.NotFound.Select(ToSongJob).ToList(),
                batch.IsNormal,
                batch.PrintOption);
            return;
        }

        if (batch.IsNormal && batch.PendingCount == 1 && batch.ExistingCount + batch.NotFoundCount == 0)
            return;

        string notFoundLastTime = batch.NotFoundCount > 0 ? $"{batch.NotFoundCount} not found" : "";
        string alreadyExist = batch.ExistingCount > 0 ? $"{batch.ExistingCount} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool allSkipped = batch.ExistingCount + batch.NotFoundCount > batch.PendingCount;
        Logger.Info($"Downloading {batch.PendingCount} tracks{skippedTracks}{(allSkipped ? '.' : ':')}");

        var preview = batch.Pending.Select(ToSongJob).ToList();
        if (preview.Count > 0)
            Printing.PrintTracks(preview, 10, fullInfo: false);
    }

    private static bool ShouldAttachHumanProgressReporter(PrintOption printOption)
        => !IsMachineReadablePrint(printOption);

    private static bool ShouldPrintHumanBatchPreview(PrintOption printOption)
        => !IsMachineReadablePrint(printOption);

    private static bool IsMachineReadablePrint(PrintOption printOption)
        => (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;

    internal static async Task PrintRemoteCompleteAsync(
        ICliBackend backend,
        Guid workflowId,
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return;

        int successes = 0;
        int fails = 0;

        foreach (var summary in workflow.Jobs.OrderBy(job => job.DisplayId))
        {
            var counts = await CountRemoteCompletedSongsAsync(backend, summary, ct);
            successes += counts.Successes;
            fails += counts.Fails;
        }

        Printing.PrintComplete(successes, fails);
    }

    private static async Task<(int Successes, int Fails)> CountRemoteCompletedSongsAsync(
        ICliBackend backend,
        JobSummaryDto summary,
        CancellationToken ct)
        => await CountRemoteCompletedSongsAsync(backend, summary, new HashSet<Guid>(), ct);

    private static async Task<(int Successes, int Fails)> CountRemoteCompletedSongsAsync(
        ICliBackend backend,
        JobSummaryDto summary,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        if (!visited.Add(summary.JobId))
            return (0, 0);

        if (summary.Kind == ServerJobKind.Song)
        {
            int successes = 0;
            int fails = 0;
            CountSummary(summary, ref successes, ref fails);
            return (successes, fails);
        }

        var detail = await backend.GetJobDetailAsync(summary.JobId, ct);
        if (detail == null)
            return (0, 0);

        int childSuccesses = 0;
        int childFails = 0;
        foreach (var child in detail.Children.OrderBy(job => job.DisplayId))
        {
            var counts = await CountRemoteCompletedSongsAsync(backend, child, visited, ct);
            childSuccesses += counts.Successes;
            childFails += counts.Fails;
        }

        return (childSuccesses, childFails);
    }

    private static void CountSong(SongJobPayloadDto song, JobSummaryDto? summary, ref int successes, ref int fails)
    {
        var serverState = song.State ?? summary?.State;
        if (!TryToCoreJobState(serverState, out var state))
            return;

        if (Printing.IsSuccessfulCompletion(state))
            successes++;
        else if (state == JobState.Failed)
            fails++;
    }

    private static void CountSummary(JobSummaryDto summary, ref int successes, ref int fails)
    {
        if (!TryToCoreJobState(summary.State, out var state))
            return;

        if (Printing.IsSuccessfulCompletion(state))
            successes++;
        else if (state == JobState.Failed)
            fails++;
    }

    private static IEnumerable<SongJobPayloadDto> ResolvedAlbumSongs(AlbumJobPayloadDto album)
        => album.Tracks?.Where(song => Utils.IsMusicFile(song.ResolvedFilename ?? "")) ?? [];

    internal static async Task PrintRemoteResultsAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return;

        bool nonVerbose = (settings.PrintOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
        bool printedAny = false;

        foreach (var summary in workflow.Jobs.OrderBy(job => job.DisplayId))
        {
            var detail = await backend.GetJobDetailAsync(summary.JobId, ct);
            if (detail?.Payload == null)
                continue;

            Job? job = detail.Payload switch
            {
                SongJobPayloadDto song when song.CandidateCount.GetValueOrDefault() > 0
                    => await ToSongResultsJobAsync(backend, summary.JobId, song, ct),
                SearchJobPayloadDto search when search.ResultCount > 0
                    => await ToSearchResultsJobAsync(backend, summary.JobId, search, ct),
                AlbumJobPayloadDto album when album.ResultCount > 0
                    => await ToAlbumResultsJobAsync(backend, summary.JobId, album, ct),
                AggregateJobPayloadDto aggregate when aggregate.SongCount > 0
                    => await ToAggregateResultsJobAsync(backend, aggregate, detail.Children, ct),
                _ => null,
            };

            if (job == null)
                continue;

            if (printedAny && !nonVerbose)
                Console.WriteLine();

            Printing.PrintResults(job, settings.PrintOption, settings.Search);
            printedAny = true;
        }
    }

    private static async Task<Job?> ToSearchResultsJobAsync(
        ICliBackend backend,
        Guid searchJobId,
        SearchJobPayloadDto search,
        CancellationToken ct)
    {
        if (search.DefaultFolderProjection != null)
        {
            var folders = await backend.GetFolderResultsAsync(
                searchJobId,
                search.DefaultFolderProjection with { IncludeFiles = true },
                ct);
            return folders == null
                ? null
                : new AlbumJob(ToAlbumQuery(search.DefaultFolderProjection.AlbumQuery))
                {
                    Results = folders.Items.Select(ToAlbumFolder).ToList(),
                };
        }

        var fileProjection = search.DefaultFileProjection
            ?? new FileSearchProjectionRequestDto(new SongQueryDto(null, search.QueryText, null, null, null, false));
        var files = await backend.GetFileResultsAsync(searchJobId, fileProjection, ct);
        return files == null
            ? null
            : new SongJob(ToSongQuery(fileProjection.SongQuery ?? new SongQueryDto(null, search.QueryText, null, null, null, false)))
            {
                Candidates = files.Items.Select(ToFileCandidate).ToList(),
            };
    }

    private static async Task<Job?> ToSongResultsJobAsync(
        ICliBackend backend,
        Guid songJobId,
        SongJobPayloadDto song,
        CancellationToken ct)
    {
        var files = await backend.GetFileResultsAsync(songJobId, ct);
        var job = ToSongJob(song);
        job.Candidates = files?.Items.Select(ToFileCandidate).ToList();
        return job;
    }

    private static async Task<Job?> ToAlbumResultsJobAsync(
        ICliBackend backend,
        Guid albumJobId,
        AlbumJobPayloadDto album,
        CancellationToken ct)
    {
        var folders = await backend.GetFolderResultsAsync(albumJobId, includeFiles: true, ct);
        var job = ToAlbumJob(album);
        job.Results = folders?.Items.Select(ToAlbumFolder).ToList() ?? [];
        return job;
    }

    private static async Task<Job?> ToAggregateResultsJobAsync(
        ICliBackend backend,
        AggregateJobPayloadDto aggregate,
        IReadOnlyList<JobSummaryDto> children,
        CancellationToken ct)
    {
        var job = new AggregateJob(ToSongQuery(aggregate.Query));
        foreach (var summary in children.Where(child => child.Kind == ServerJobKind.Song).OrderBy(child => child.DisplayId))
        {
            var detail = await backend.GetJobDetailAsync(summary.JobId, ct);
            if (detail?.Payload is not SongJobPayloadDto payload)
                continue;

            var song = ToSongJob(payload);
            if (payload.CandidateCount.GetValueOrDefault() > 0)
            {
                var files = await backend.GetFileResultsAsync(summary.JobId, ct);
                song.Candidates = files?.Items.Select(ToFileCandidate).ToList();
            }

            job.Songs.Add(song);
        }

        return job;
    }

    internal static async Task PrintRemotePlannedOutputAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return;

        var details = new Dictionary<Guid, JobDetailDto>();
        foreach (var summary in workflow.Jobs)
            await LoadRemoteJobTreeAsync(backend, summary.JobId, details, ct);

        var roots = details.Values
            .Where(detail => workflow.Jobs.Any(root => root.JobId == detail.Summary.JobId))
            .OrderBy(detail => detail.Summary.DisplayId)
            .ToList();

        var visited = new HashSet<Guid>();
        var plannedJobs = new List<Job>();
        foreach (var root in roots)
            CollectRemotePlannedDownloads(root, details, plannedJobs, visited);

        if (plannedJobs.Count > 0 && settings.PrintTracks)
            Printing.PrintPlannedDownloads(plannedJobs, settings);
    }

    private static async Task LoadRemoteJobTreeAsync(
        ICliBackend backend,
        Guid jobId,
        Dictionary<Guid, JobDetailDto> details,
        CancellationToken ct)
    {
        if (details.ContainsKey(jobId))
            return;

        var detail = await backend.GetJobDetailAsync(jobId, ct);
        if (detail == null)
            return;

        details[jobId] = detail;

        foreach (var child in detail.Children)
        {
            if (detail.Summary.Kind == ServerJobKind.Album
                && child.Kind == ServerJobKind.Song)
                continue;

            await LoadRemoteJobTreeAsync(backend, child.JobId, details, ct);
        }
    }

    private static void CollectRemotePlannedDownloads(
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        List<Job> plannedJobs,
        HashSet<Guid> visited)
    {
        if (!visited.Add(detail.Summary.JobId))
            return;

        if (detail.Payload is ExtractJobPayloadDto extract
            && extract.ResultJobId is Guid resultJobId
            && details.TryGetValue(resultJobId, out var resultDetail))
        {
            CollectRemotePlannedDownloads(resultDetail, details, plannedJobs, visited);
            return;
        }

        switch (detail.Payload)
        {
            case SongJobPayloadDto song:
                plannedJobs.Add(ToSongJob(song));
                break;

            case AlbumJobPayloadDto album:
                plannedJobs.Add(ToAlbumJob(album, detail.Summary));
                break;

            case AggregateJobPayloadDto:
                foreach (var child in ChildrenOf(detail, details))
                    CollectRemotePlannedDownloads(child, details, plannedJobs, visited);
                break;

            case AlbumAggregateJobPayloadDto albumAggregate:
                plannedJobs.Add(ToAlbumAggregateJob(albumAggregate, detail.Summary));
                break;

            case JobListPayloadDto:
                foreach (var child in ChildrenOf(detail, details))
                    CollectRemotePlannedDownloads(child, details, plannedJobs, visited);
                break;
        }
    }

    private static IReadOnlyList<JobDetailDto> ChildrenOf(
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details)
        => details.Values
            .Where(candidate => candidate.Summary.ParentJobId == detail.Summary.JobId)
            .OrderBy(candidate => candidate.Summary.DisplayId)
            .ToList();

    internal static SubmissionOptionsDto BuildRemoteSubmissionOptions(
        string[] args,
        CliSettings cliSettings)
        => new(
            ProfileNames: SplitProfileNames(ConfigManager.ExtractProfileName(args)),
            ProfileContext: new Dictionary<string, bool>
            {
                ["interactive"] = cliSettings.InteractiveMode,
                ["progress-json"] = cliSettings.ProgressJson,
                ["no-progress"] = cliSettings.NoProgress,
            },
            DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch(args));

    private static IReadOnlyList<string>? SplitProfileNames(string? names)
        => string.IsNullOrWhiteSpace(names)
            ? null
            : names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static async Task RunDaemonAsync(
        string[] args,
        ConfigFile configFile,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        DaemonSettings daemonSettings)
    {
        var url = $"http://{daemonSettings.ListenIp}:{daemonSettings.ListenPort}";
        var options = new ServerOptions
        {
            Engine = SettingsCloner.Clone(engineSettings),
            DefaultDownload = SettingsCloner.Clone(rootSettings),
            LaunchDownloadSettings = ConfigManager.CreateCliDownloadSettingsPatch(args),
            Profiles = ConfigManager.CreateProfileCatalog(configFile),
        };

        var app = ServerHost.Build(args, options, url);
        Logger.Info($"Starting sldl daemon on {url}");
        Logger.Info("Press Ctrl+C to stop.");
        await app.RunAsync();
    }

    private static SongJob ToSongJob(SongJobPayloadDto song)
        => ToSongJob(song, null);

    private static SongJob ToSongJob(SongJobPayloadDto song, JobSummaryDto? summary)
    {
        var job = new SongJob(new SongQuery
        {
            Artist = song.Query.Artist ?? "",
            Title = song.Query.Title ?? "",
            Album = song.Query.Album ?? "",
            URI = song.Query.Uri ?? "",
            Length = song.Query.Length ?? -1,
            ArtistMaybeWrong = song.Query.ArtistMaybeWrong,
        })
        {
            DownloadPath = song.DownloadPath,
            Candidates = song.Candidates?.Select(ToFileCandidate).ToList(),
        };

        ApplyJobOutcome(job, song.State, song.FailureReason, song.FailureMessage);

        if (summary != null)
        {
            ApplyJobOutcome(job, summary.State, summary.FailureReason, summary.FailureMessage);
        }

        if (!string.IsNullOrWhiteSpace(song.ResolvedUsername)
            && !string.IsNullOrWhiteSpace(song.ResolvedFilename))
        {
            job.ResolvedTarget = ToFileCandidate(new FileCandidateDto(
                new FileCandidateRefDto(song.ResolvedUsername, song.ResolvedFilename),
                song.ResolvedUsername,
                song.ResolvedFilename,
                new PeerInfoDto(song.ResolvedUsername, song.ResolvedHasFreeUploadSlot, song.ResolvedUploadSpeed),
                song.ResolvedSize ?? 0,
                null,
                null,
                null,
                song.ResolvedExtension,
                song.ResolvedAttributes));
        }

        return job;
    }

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album)
        => ToAlbumJob(album, null);

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album, JobSummaryDto? summary)
    {
        var job = new AlbumJob(ToAlbumQuery(album.Query))
        {
            Results = album.Results?.Select(ToAlbumFolder).ToList() ?? [],
            DownloadPath = album.DownloadPath,
        };

        if (summary != null)
            ApplyJobOutcome(job, summary.State, summary.FailureReason, summary.FailureMessage);

        return job;
    }

    private static AggregateJob ToAggregateJob(AggregateJobPayloadDto aggregate)
        => new(ToSongQuery(aggregate.Query))
        {
            Songs = aggregate.Songs?.Select(ToSongJob).ToList() ?? [],
        };

    private static AlbumAggregateJob ToAlbumAggregateJob(AlbumAggregateJobPayloadDto albumAggregate, JobSummaryDto? summary = null)
    {
        var job = new AlbumAggregateJob(ToAlbumQuery(albumAggregate.Query));
        if (summary != null)
            ApplyJobOutcome(job, summary.State, summary.FailureReason, summary.FailureMessage);
        return job;
    }

    private static void ApplyJobOutcome(Job job, ServerJobState? state, ServerFailureReason? failureReason, string? failureMessage)
    {
        if (TryToCoreJobState(state, out var parsedState))
            job.State = parsedState;
        if (TryToCoreFailureReason(failureReason, out var parsedFailureReason))
            job.FailureReason = parsedFailureReason;
        job.FailureMessage = failureMessage;
    }

    private static bool TryToCoreJobState(ServerJobState? state, out JobState coreState)
    {
        if (state == null)
        {
            coreState = default;
            return false;
        }

        return Enum.TryParse(state.Value.ToString(), out coreState);
    }

    private static bool TryToCoreFailureReason(ServerFailureReason? reason, out FailureReason coreReason)
    {
        if (reason == null)
        {
            coreReason = default;
            return false;
        }

        return Enum.TryParse(reason.Value.ToString(), out coreReason);
    }

    private static AlbumFolder ToAlbumFolder(AlbumFolderDto folder)
        => new(
            folder.Username,
            folder.FolderPath,
            folder.Files?.Select(ToSongJob).ToList() ?? []);

    private static SongJob ToSongJob(FileCandidateDto file)
    {
        var candidate = ToFileCandidate(file);
        var query = Searcher.InferSongQuery(candidate.Filename, new SongQuery());
        return new SongJob(query) { ResolvedTarget = candidate };
    }

    private static SongQuery ToSongQuery(SongQueryDto query)
        => new()
        {
            Artist = query.Artist ?? "",
            Title = query.Title ?? "",
            Album = query.Album ?? "",
            URI = query.Uri ?? "",
            Length = query.Length ?? -1,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    private static AlbumQuery ToAlbumQuery(AlbumQueryDto query)
        => new()
        {
            Artist = query.Artist ?? "",
            Album = query.Album ?? "",
            SearchHint = query.SearchHint ?? "",
            URI = query.Uri ?? "",
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    private static FileCandidate ToFileCandidate(FileCandidateDto candidate)
        => new(
            new SearchResponse(
                candidate.Username,
                -1,
                candidate.Peer.HasFreeUploadSlot ?? false,
                candidate.Peer.UploadSpeed ?? -1,
                -1,
                null),
            new Soulseek.File(
                0,
                candidate.Filename,
                candidate.Size,
                candidate.Extension ?? Path.GetExtension(candidate.Filename),
                candidate.Attributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));
}
