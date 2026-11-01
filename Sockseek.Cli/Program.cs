using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;
using Sockseek.Core.Snapshots;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Sockseek.Core.Diagnostics;
using System.Diagnostics;

namespace Sockseek.Cli;

internal static partial class Program
{
    private static readonly TimeSpan RetainedWorkflowAvailabilityTimeout = TimeSpan.FromSeconds(5);

    internal enum CliExitCode
    {
        Success = 0,
        WorkFailed = 1,
        UsageError = 2,
        Cancelled = 130,
    }

    public static async Task<int> Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var output = CliOutputController.Install(args);

        try
        {
            CliExitCode? configuredCommand = await ConfiguredCommandDispatcher.TryRunAsync(args, output)
                .ConfigureAwait(false);
            if (configuredCommand is not null)
                return (int)configuredCommand.Value;
            if (Help.PrintAndExitIfNeeded(args))
                return (int)CliExitCode.Success;
            return (int)await MainCore(args, output);
        }
        catch (Exception ex)
        {
            Write(output, LogLevel.Critical, $"Unhandled CLI startup error: {ExceptionText.Summary(ex)}");
            return (int)CliExitCode.WorkFailed;
        }
    }

    internal static async Task<CliExitCode> MainCore(string[] args)
    {
        using var output = CliOutputController.CreateDetached();
        return await MainCore(args, output, CancellationToken.None);
    }

    internal static async Task<CliExitCode> MainCore(
        string[] args,
        CancellationToken cancellationToken)
    {
        using var output = CliOutputController.CreateDetached();
        return await MainCore(args, output, cancellationToken);
    }

    internal static Task<CliExitCode> MainCore(string[] args, CliOutputController output)
        => MainCore(args, output, CancellationToken.None);

    internal static async Task<CliExitCode> MainCore(
        string[] args,
        CliOutputController output,
        CancellationToken cancellationToken)
    {
        bool daemonMode = args.Length > 0 && string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase);
        var bindArgs = daemonMode ? args.Skip(1).ToArray() : args;

        ConfigFile configFile;
        EngineSettings engineSettings;
        DownloadSettings rootSettings;
        CliSettings cliSettings;
        DaemonSettings daemonSettings;
        RemoteSettings remoteSettings;

        // TODO [ARCHITECTURE]: Replace scattered CLI/server validation exception handling
        // with typed diagnostics carrying severity, exit-code class, output stream, and
        // optional debug detail. Parser, daemon startup, remote startup, and extractor
        // validation currently encode that policy in several catch/log branches.
        try
        {
            (configFile, engineSettings, rootSettings, cliSettings, daemonSettings, remoteSettings) =
                ConfigManager.LoadAndBindAll(bindArgs);
            ConfigManager.ApplyAutoProfileCliSettings(configFile, rootSettings, cliSettings);
            ApplyMockFilesDefaults(engineSettings, rootSettings);
        }
        catch (Exception ex) when (ex is ArgumentException || ex.Message.StartsWith("Input error:"))
        {
            Write(output, LogLevel.Error, ex.Message);
            return CliExitCode.UsageError;
        }

        string? profileArg = ConfigManager.ExtractProfileName(bindArgs);
        if (profileArg != null)
        {
            var requestedProfiles = profileArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (requestedProfiles.Contains("help", StringComparer.OrdinalIgnoreCase))
            {
                if (remoteSettings.IsEnabled)
                {
                    try
                    {
                        using var http = SockseekApiClient.CreateHttpClient(remoteSettings.ServerUrl!);
                        var api = new SockseekApiClient(http, RemoteCliBackend.CreateJsonOptions());
                        var profiles = await api.GetProfilesAsync();

                        if (profiles.Count == 0)
                            Console.WriteLine("No profiles found on remote daemon.");
                        else
                        {
                            Console.WriteLine($"Available profiles on remote daemon ({remoteSettings.ServerUrl}):");
                            foreach (var p in profiles)
                                Console.WriteLine($"  {p.Name}{(p.IsAutoProfile ? " (auto)" : "")}");
                        }
            }
            catch (Exception ex)
            {
                Write(output, LogLevel.Error, $"Failed to retrieve profiles from remote daemon: {ExceptionText.Summary(ex)}");
                return CliExitCode.WorkFailed;
            }
                }
                else
                {
                    var profiles = ConfigManager.GetProfileNames(configFile);
                    if (profiles.Count == 0)
                        Console.WriteLine("No profiles found in local config.");
                    else
                    {
                        Console.WriteLine("Available profiles:");
                        foreach (var p in profiles)
                            Console.WriteLine($"  {p}");
                    }
                }
                return CliExitCode.Success;
            }
        }

        using ILoggerFactory loggerFactory = output.CreateLoggerFactory(
            rootSettings.NonVerbosePrint ? LogLevel.Error : engineSettings.LogLevel,
            engineSettings.LogFilePath,
            LogLevel.Debug);
        ILogger logger = loggerFactory.CreateLogger("Sockseek.Cli.Program");
        using var exceptionObserver = ProcessExceptionObserver.Install(logger);

        if (daemonMode)
        {
            try
            {
                await RunDaemonAsync(bindArgs, configFile, engineSettings, rootSettings, daemonSettings);
            }
            catch (ArgumentException ex)
            {
                Write(output, LogLevel.Error, ex.Message);
                return CliExitCode.UsageError;
            }
            catch (DaemonEndpointUnavailableException ex)
            {
                Write(output, LogLevel.Error, ex.Message);
                return CliExitCode.WorkFailed;
            }
            catch (Exception ex)
            {
                CliLogMessages.OperationFailed(logger, ex, "daemon");
                Write(output, LogLevel.Error, $"Unhandled daemon error: {ExceptionText.Summary(ex)}");
                return CliExitCode.WorkFailed;
            }
            return CliExitCode.Success;
        }

        LogCliSessionStart(logger, remoteSettings);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (cliSettings.Monitor)
        {
            if (!remoteSettings.IsEnabled)
            {
                Write(output, LogLevel.Error,
                    "Monitor mode requires a configured remote URL "
                    + "(remote = <url> or --remote <url>).");
                return CliExitCode.UsageError;
            }

            try
            {
                return await RunRemoteMonitorAsync(
                    bindArgs,
                    engineSettings,
                    rootSettings,
                    cliSettings,
                    remoteSettings,
                    output,
                    logger,
                    cts);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return CliExitCode.Cancelled;
            }
            catch (Exception ex)
            {
                CliLogMessages.OperationFailed(logger, ex, "remote-monitor");
                Write(output, LogLevel.Error, $"Remote monitor failed: {ExceptionText.Summary(ex)}");
                return CliExitCode.WorkFailed;
            }
        }

        if (remoteSettings.IsEnabled)
        {
            try
            {
                return await RunRemoteAsync(
                    bindArgs,
                    engineSettings,
                    rootSettings,
                    cliSettings,
                    remoteSettings,
                    output,
                    logger,
                    cts);
            }
            catch (SockseekApiRequestException ex)
            {
                Write(output, LogLevel.Error, ex.Message);
                return CliExitCode.WorkFailed;
            }
            catch (Exception ex)
            {
                CliLogMessages.OperationFailed(logger, ex, "remote-session");
                Write(output, LogLevel.Error, $"Unhandled remote CLI error: {ExceptionText.Summary(ex)}");
                return CliExitCode.WorkFailed;
            }
        }

        var clientManager = new SoulseekClientManager(
            engineSettings,
            logger: loggerFactory.CreateLogger<SoulseekClientManager>());

        if (string.IsNullOrEmpty(rootSettings.Extraction.Input))
        {
            var diagnostic = new DiagnosticService(clientManager, output);
            try
            {
                await diagnostic.PerformNoInputActions(rootSettings.PrintOption, rootSettings.Output.IndexFilePath, cts.Token);
            }
            catch (Exception ex)
            {
                CliLogMessages.OperationFailed(logger, ex, "diagnostic-action");
                Write(output, LogLevel.Error, $"Diagnostic action failed: {ExceptionText.Summary(ex)}");
            }

            if (!rootSettings.PrintOption.HasFlag(PrintOption.Index))
            {
                Write(output, LogLevel.Error, "Input error: No input provided.");
                Help.PrintAndExitIfNeeded([]);
                return CliExitCode.UsageError;
            }
            return CliExitCode.Success;
        }

        IJobSettingsResolver jobSettingsResolver;
        try
        {
            jobSettingsResolver = ConfigManager.CreateJobSettingsResolver(
                configFile,
                bindArgs,
                cliSettings);
            if (!string.IsNullOrEmpty(engineSettings.MockFilesDir))
                jobSettingsResolver = new MockFilesJobSettingsResolver(jobSettingsResolver);
        }
        catch (Exception ex) when (ex is ArgumentException || ex.Message.StartsWith("Input error:"))
        {
            Write(output, LogLevel.Error, ex.Message);
            return CliExitCode.UsageError;
        }

        var localSubmissionOptionsResolver = new SubmissionOptionsJobSettingsResolver(
            jobSettingsResolver,
            normalize: settings => SettingsNormalizer.NormalizeDownloadPaths(settings, settings.RuntimePathContext));

        bool attachHumanProgressReporter = ShouldAttachHumanProgressReporter(rootSettings.PrintOption);
        if (attachHumanProgressReporter)
        {
            output.ConfigureLiveRendering(cliSettings, engineSettings.LogLevel);
            // Only the foreground local renderer replaces the engine's plain interval
            // progress logs. Daemon and remote execution must retain their own policy.
            if (output.WillUseLiveRendering)
                engineSettings.ReportIntervalProgress = false;
        }

        var engine = new DownloadEngine(
            engineSettings,
            clientManager,
            localSubmissionOptionsResolver,
            loggerFactory: loggerFactory,
            sensitiveOutput: new CliSensitiveOutput(output));
        await using var backend = new LocalCliBackend(
            engine,
            rootSettings,
            localSubmissionOptionsResolver,
            ConfigManager.CreateCliDownloadSettingsPatch(bindArgs));

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else if (attachHumanProgressReporter)
        {
            cliReporter = new CliProgressReporter(cliSettings, output);
            cliReporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, output, includeDiagnosticDetails: engineSettings.LogLevel <= LogLevel.Debug);
        eventLogger.Attach();

        backend.ActivityReceived += activity =>
        {
            if (activity.Payload is TrackBatchResolvedActivityDto batch
                && batch.PrintOption == ServerPrintOption.None
                && ShouldPrintHumanBatchPreview(batch.PrintOption.ToCore())
                && cliReporter?.UsesLiveRendering != true)
            {
                PrintCompactTrackBatchResolved(activity, batch, output);
            }
        };

        Task? interactiveCoordinatorTask = null;
        if (cliSettings.InteractiveMode)
        {
            var workflowId = Guid.NewGuid();
            var coordinator = new InteractiveCliCoordinator(backend, cliSettings, cts.Token);
            var submission = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    rootSettings.Extraction.Input,
                    rootSettings.Extraction.InputType.ToString(),
                    Options: new SubmissionOptionsDto(workflowId)),
                cts.Token);
            interactiveCoordinatorTask = coordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token);
            _ = interactiveCoordinatorTask
                .ContinueWith(_ => engine.CompleteEnqueue(), TaskScheduler.Default);
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

        using var consoleControls = StartConsoleControls(
            backend,
            cliReporter,
            workflowId: null,
            cancelPrompt: "Cancel job ID or all jobs? id/[A]ll/n: ",
            cancelAllMessage: "Cancelling all jobs...",
            cancelAll: _ =>
            {
                engine.Cancel();
                return Task.CompletedTask;
            },
            cts);

        try
        {
            await engine.RunAsync(cts.Token);
            if (interactiveCoordinatorTask != null)
                await interactiveCoordinatorTask;

            bool hasDownloadableJobs = PrintOutputRenderer.HasDownloadableJobs(engine.Queue);
            bool hasRequestedOutput = PrintOutputRenderer.HasRequestedOutput(engine.Queue);

            if (hasDownloadableJobs)
                Printing.PrintComplete(engine.Queue);

            if (hasRequestedOutput)
            {
                cliReporter?.Stop(printSummary: hasDownloadableJobs);
                cliReporter = null;
                PrintOutputRenderer.PrintRequestedOutput(
                    engine.Queue,
                    engine.GetUserSuccessCounts);
            }

            var exitCode = LogCliSessionExit(logger, DetermineLocalExitCode(engine.Queue));
            cliReporter?.Stop();
            cliReporter = null;

            return exitCode;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return LogCliSessionExit(logger, CliExitCode.Cancelled);
        }
        catch (SoulseekConnectionUnavailableException ex)
        {
            Write(output, LogLevel.Error, ex.Message);
            return LogCliSessionExit(logger, CliExitCode.WorkFailed);
        }
        catch (Exception ex)
        {
            CliLogMessages.OperationFailed(logger, ex, "local-session");
            Write(output, LogLevel.Error, $"Unhandled CLI error: {ExceptionText.Summary(ex)}");
            return LogCliSessionExit(logger, CliExitCode.WorkFailed);
        }
        finally
        {
            engine.Cancel();
            await engine.DisposeAsync();
            await cts.CancelAsync();
            cliReporter?.Stop();
            clientManager.Dispose();
            Printing.SetBuffering(false);
        }
    }

    internal static bool ArgsRequestProgressJson(IReadOnlyList<string> args)
        => CliOutputController.ArgsRequestProgressJson(args);

    private static void ApplyMockFilesDefaults(EngineSettings engineSettings, DownloadSettings downloadSettings)
    {
        if (!string.IsNullOrEmpty(engineSettings.MockFilesDir))
            downloadSettings.Search.MinSharesAggregate = 1;
    }

    private sealed class MockFilesJobSettingsResolver(IJobSettingsResolver inner)
        : IJobSettingsResolver, IJobSettingsRequestResolver
    {
        public DownloadSettings Resolve(
            DownloadSettings inherited,
            JobSettingsContext job,
            JobSettingsInheritance inheritance = JobSettingsInheritance.None)
        {
            var settings = inner.Resolve(inherited, job, inheritance);
            settings.Search.MinSharesAggregate = 1;
            return settings;
        }

        public DownloadSettings Resolve(
            DownloadSettings inherited,
            JobSettingsContext job,
            JobSettingsInheritance inheritance,
            JobSettingsRequestLayers? request)
        {
            DownloadSettings settings;
            if (inner is IJobSettingsRequestResolver requestResolver)
            {
                settings = requestResolver.Resolve(inherited, job, inheritance, request);
            }
            else
            {
                settings = inner.Resolve(inherited, job, inheritance);
                request?.Download?.ApplyTo(settings);
            }
            settings.Search.MinSharesAggregate = 1;
            return settings;
        }
    }

    private static async Task<CliExitCode> RunRemoteAsync(
        string[] args,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        CliSettings cliSettings,
        RemoteSettings remoteSettings,
        CliOutputController output,
        ILogger logger,
        CancellationTokenSource cts)
    {
        if (string.IsNullOrWhiteSpace(rootSettings.Extraction.Input))
        {
            Write(output, LogLevel.Error, "Remote mode requires an input.");
            return CliExitCode.UsageError;
        }

        await using var backend = new RemoteCliBackend(remoteSettings.ServerUrl!);
        await backend.StartAsync(cts.Token);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else if (ShouldAttachHumanProgressReporter(rootSettings.PrintOption))
        {
            output.ConfigureLiveRendering(cliSettings, engineSettings.LogLevel);
            cliReporter = new CliProgressReporter(cliSettings, output);
            cliReporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, output, includeDiagnosticDetails: false);
        eventLogger.Attach();

        backend.ActivityReceived += activity =>
        {
            if (activity.Payload is TrackBatchResolvedActivityDto batch
                && batch.PrintOption == ServerPrintOption.None
                && ShouldPrintHumanBatchPreview(batch.PrintOption.ToCore())
                && cliReporter?.UsesLiveRendering != true)
            {
                PrintCompactTrackBatchResolved(activity, batch, output);
            }
        };

        try
        {
            if (rootSettings.PrintJobs)
            {
                CliExitCode previewExit = await PrintRemoteJobPreviewAsync(
                    backend,
                    new SubmitExtractJobRequestDto(
                        rootSettings.Extraction.Input,
                        rootSettings.Extraction.InputType.ToString(),
                        Options: BuildRemoteSubmissionOptions(args, cliSettings)),
                    rootSettings.PrintOption,
                    cts.Token).ConfigureAwait(false);
                return LogCliSessionExit(logger, previewExit, remoteSettings);
            }

            Guid workflowId = Guid.NewGuid();
            await backend.SubscribeWorkflowAsync(workflowId, cts.Token);
            using var terminalUpdateObserver = new WorkflowTerminalUpdateObserver(backend, workflowId);

            var options = BuildRemoteSubmissionOptions(args, cliSettings) with { WorkflowId = workflowId };
            var request = new SubmitExtractJobRequestDto(
                rootSettings.Extraction.Input,
                rootSettings.Extraction.InputType.ToString(),
                Options: options);

            InteractiveCliCoordinator? interactiveCoordinator = null;
            JobSummaryDto submission;
            if (cliSettings.InteractiveMode)
            {
                interactiveCoordinator = new InteractiveCliCoordinator(backend, cliSettings, cts.Token);
                submission = await interactiveCoordinator.StartAsync(request, cts.Token);
            }
            else
            {
                submission = await backend.SubmitExtractJobAsync(request, cts.Token);
            }

            using var consoleControls = StartConsoleControls(
                backend,
                cliReporter,
                submission.WorkflowId,
                cancelPrompt: "Cancel job ID or current workflow? id/[A]ll/n: ",
                cancelAllMessage: "Cancelling workflow...",
                cancelAll: async ct =>
                {
                    await backend.CancelWorkflowAsync(submission.WorkflowId, ct);
                },
                cts);

            if (interactiveCoordinator != null)
                await interactiveCoordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token);

            await terminalUpdateObserver.WaitForTerminalUpdateAsync(cts.Token);
            var observedCompletion = terminalUpdateObserver.Completion
                ?? throw new InvalidOperationException("The terminal workflow update did not contain a completion snapshot.");

            if (!rootSettings.DoNotDownload)
                await PrintRemoteCompleteCoreAsync(
                    backend,
                    submission.WorkflowId,
                    cts.Token,
                    output: null,
                    observedCompletion: observedCompletion);

            var exitCode = LogCliSessionExit(
                logger,
                await DetermineRemoteExitCodeAsync(
                    backend,
                    submission.WorkflowId,
                    cts.Token,
                    observedCompletion),
                remoteSettings);
            cliReporter?.Stop();
            cliReporter = null;

            if (rootSettings.PrintResults || rootSettings.PrintJobs)
            {
                await PrintRemoteRequestedOutputAsync(
                    backend,
                    submission.WorkflowId,
                    rootSettings,
                    cts.Token,
                    expectedJobIds: observedCompletion.Jobs.Select(job => job.JobId).ToArray());
            }

            return exitCode;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return LogCliSessionExit(logger, CliExitCode.Cancelled, remoteSettings);
        }
        catch (SockseekApiRequestException ex)
        {
            if (cliReporter != null)
                cliReporter.ReportClientError(ex.Message);
            else
                Write(output, LogLevel.Error, ex.Message);

            return LogCliSessionExit(logger, CliExitCode.WorkFailed, remoteSettings);
        }
        catch (Exception ex)
        {
            if (cliReporter != null)
                cliReporter.ReportClientError($"Unhandled remote CLI error: {ExceptionText.Summary(ex)}");
            else
                Write(output, LogLevel.Error, $"Unhandled remote CLI error: {ExceptionText.Summary(ex)}");

            CliLogMessages.OperationFailed(logger, ex, "remote-session");
            return LogCliSessionExit(logger, CliExitCode.WorkFailed, remoteSettings);
        }
        finally
        {
            await cts.CancelAsync();
            cliReporter?.Stop();
        }
    }

    internal static async Task<CliExitCode> PrintRemoteJobPreviewAsync(
        RemoteCliBackend backend,
        SubmitExtractJobRequestDto request,
        PrintOption printOption,
        CancellationToken ct,
        TextWriter? output = null)
    {
        CreateJobPreviewResponseDto created = await backend.CreateJobPreviewAsync(request, ct)
            .ConfigureAwait(false);
        JobPreviewSummaryDto preview = await backend.WaitForJobPreviewAsync(
            created.Preview.PreviewId,
            ct).ConfigureAwait(false);
        if (preview.State is JobPreviewState.Expired or JobPreviewState.Committing
            or JobPreviewState.Committed)
        {
            throw new InvalidOperationException(
                $"Job Preview entered unexpected state '{preview.State}' before it could be printed.");
        }

        using var outputScope = output is null ? null : Printing.RedirectOutput(output);
        JobPrintFormatter.PrintHeader(preview.SelectableNodeCount);
        int printed = 0;
        await foreach (JobPreviewNodeDto node in backend.GetJobPreviewNodesAsync(
            preview.PreviewId,
            ct).ConfigureAwait(false))
        {
            if (!node.IsSelectable || ToPreviewPrintJob(node) is not { } job)
                continue;
            JobPrintFormatter.PrintJob(job, printOption, separate: printed > 0);
            printed++;
        }

        if (printed != preview.SelectableNodeCount)
        {
            throw new InvalidOperationException(
                "The daemon returned a Job Preview node kind that this CLI cannot render.");
        }
        if (preview.FailedNodeCount > 0)
        {
            Printing.WriteLine();
            Printing.WriteLine(
                $"Preview contains {preview.FailedNodeCount} failed planning "
                + (preview.FailedNodeCount == 1 ? "entry." : "entries."));
        }
        return preview.State == JobPreviewState.Ready
            ? CliExitCode.Success
            : CliExitCode.WorkFailed;
    }

    private static Job? ToPreviewPrintJob(JobPreviewNodeDto node)
    {
        Job? job = node.Kind switch
        {
            ServerJobKind.Search when node.AlbumQuery != null =>
                new SearchJob(ToAlbumQuery(node.AlbumQuery)),
            ServerJobKind.Search when node.SongQuery != null =>
                new SearchJob(ToSongQuery(node.SongQuery)),
            ServerJobKind.Search when !string.IsNullOrWhiteSpace(node.QueryText) =>
                new SearchJob(node.QueryText),
            ServerJobKind.Song when node.SongQuery != null =>
                new SongJob(ToSongQuery(node.SongQuery)),
            ServerJobKind.Album when node.AlbumQuery != null =>
                new AlbumJob(ToAlbumQuery(node.AlbumQuery)),
            ServerJobKind.Aggregate when node.SongQuery != null =>
                new AggregateJob(ToSongQuery(node.SongQuery)),
            ServerJobKind.AlbumAggregate when node.AlbumQuery != null =>
                new AlbumAggregateJob(ToAlbumQuery(node.AlbumQuery)),
            _ => null,
        };
        if (job != null)
        {
            job.ExtractorInfo = node.ExtractorInfo;
            if (!string.IsNullOrWhiteSpace(node.ItemName))
                job.ItemName = node.ItemName;
        }
        return job;
    }

    private static async Task<CliExitCode> RunRemoteMonitorAsync(
        string[] args,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        CliSettings cliSettings,
        RemoteSettings remoteSettings,
        CliOutputController output,
        ILogger logger,
        CancellationTokenSource cts)
    {
        await using var backend = new RemoteCliBackend(remoteSettings.ServerUrl!);
        CliProgressReporter? reporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else
        {
            output.ConfigureLiveRendering(cliSettings, engineSettings.LogLevel);
            reporter = new CliProgressReporter(cliSettings, output);
            reporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, output, includeDiagnosticDetails: false);
        eventLogger.Attach();

        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            await backend.SubscribeAllAsync(cts.Token);
            using var consoleControls = StartConsoleControls(
                backend,
                reporter,
                workflowId: null,
                cancelPrompt: "Cancel job ID or all daemon jobs? id/[A]ll/n: ",
                cancelAllMessage: "Cancelling all daemon jobs...",
                cancelAll: async ct =>
                {
                    await backend.CancelAllJobsAsync(ct);
                },
                cts);

            if (!string.IsNullOrWhiteSpace(rootSettings.Extraction.Input))
            {
                var options = BuildRemoteSubmissionOptions(args, cliSettings) with
                {
                    WorkflowId = Guid.NewGuid(),
                };
                await backend.SubmitExtractJobAsync(
                    new SubmitExtractJobRequestDto(
                        rootSettings.Extraction.Input,
                        rootSettings.Extraction.InputType.ToString(),
                        Options: options),
                    cts.Token);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            return CliExitCode.Success;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            reporter?.Stop();
        }
    }

    private static IDisposable StartConsoleControls(
        ICliBackend backend,
        CliProgressReporter? reporter,
        Guid? workflowId,
        string cancelPrompt,
        string cancelAllMessage,
        Func<CancellationToken, Task> cancelAll,
        CancellationTokenSource cts)
    {
        ConsoleInputManager.Reporter = reporter;

        Func<Task> cancelHandler = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write(cancelPrompt, ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();
            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
            {
                Printing.WriteLine(cancelAllMessage, ConsoleColor.Gray, force: true);
                await cancelAll(cts.Token);
                return;
            }

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob
                && result.JobId is int id)
            {
                if (await backend.CancelJobByDisplayIdAsync(id, workflowId, cts.Token))
                    Printing.WriteLine($"Cancelling job [{id}]...", force: true);
                else
                    Printing.WriteLine($"Job ID [{id}] not found.", ConsoleColor.Red, force: true);
                return;
            }

            Printing.WriteLine($"Invalid input '{result.Input}'.", ConsoleColor.Red, force: true);
        };

        Func<Task> nextCandidateHandler = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write(
                    "Try next candidate for job ID or n: ",
                    ConsoleColor.Yellow,
                    force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();
            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob
                && result.JobId is int id)
            {
                if (await backend.TryNextCandidateByDisplayIdAsync(
                    id,
                    workflowId,
                    cts.Token))
                {
                    Printing.WriteLine($"Trying next candidate for job [{id}]...", force: true);
                }
                else
                {
                    Printing.WriteLine(
                        $"Job ID [{id}] not found or has no active download.",
                        ConsoleColor.Red,
                        force: true);
                }
                return;
            }

            Printing.WriteLine($"Invalid input '{result.Input}'.", ConsoleColor.Red, force: true);
        };

        Func<Task> infoHandler = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write(
                    "Info for job ID (blank to cancel): ",
                    ConsoleColor.Yellow,
                    force: true);
            }

            var id = ConsoleInputManager.ReadJobIdInput();
            if (id == null)
                return;

            while (true)
            {
                int printStart = Console.IsOutputRedirected ? -1 : Console.CursorTop;
                var detail = await backend.GetJobDetailByDisplayIdAsync(
                    id.Value,
                    workflowId,
                    cts.Token);
                if (detail == null)
                    Printing.WriteLine($"Job ID [{id}] not found.", ConsoleColor.Red, force: true);
                else
                {
                    var children = await backend.GetJobsAsync(
                        new JobQuery(null, null, null, detail.Summary.WorkflowId, IncludeAll: true,
                            ParentJobId: detail.Summary.JobId),
                        cts.Token);
                    JobInfoPrinter.Print(detail, children);
                }

                lock (Printing.ConsoleLock)
                {
                    Printing.Write(
                        "Info for job ID (r to refresh, blank to exit): ",
                        ConsoleColor.Yellow,
                        force: true);
                }

                var result = ConsoleInputManager.ReadJobIdOrRefreshResult();
                if (result.Action == ConsoleInputManager.CancelPromptAction.Refresh)
                {
                    ClearPrintedJobInfo(printStart);
                }
                else if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob
                         && result.JobId.HasValue)
                {
                    id = result.JobId.Value;
                }
                else
                {
                    return;
                }
            }
        };

        ConsoleInputManager.OnCancelRequested = cancelHandler;
        ConsoleInputManager.OnNextCandidateRequested = nextCandidateHandler;
        ConsoleInputManager.OnInfoRequested = infoHandler;
        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        return new ConsoleControlRegistration(
            reporter,
            cancelHandler,
            nextCandidateHandler,
            infoHandler);
    }

    private static void ClearPrintedJobInfo(int printStart)
    {
        if (printStart < 0)
            return;

        int pos = Console.CursorTop;
        while (pos > printStart && pos > 0)
        {
            Console.SetCursorPosition(0, pos - 1);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, pos - 1);
            pos--;
        }
        Console.SetCursorPosition(0, printStart);
    }

    private sealed class ConsoleControlRegistration(
        CliProgressReporter? reporter,
        Func<Task> cancelHandler,
        Func<Task> nextCandidateHandler,
        Func<Task> infoHandler) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            if (ReferenceEquals(ConsoleInputManager.OnCancelRequested, cancelHandler))
                ConsoleInputManager.OnCancelRequested = null;
            if (ReferenceEquals(ConsoleInputManager.OnNextCandidateRequested, nextCandidateHandler))
                ConsoleInputManager.OnNextCandidateRequested = null;
            if (ReferenceEquals(ConsoleInputManager.OnInfoRequested, infoHandler))
                ConsoleInputManager.OnInfoRequested = null;
            if (ReferenceEquals(ConsoleInputManager.Reporter, reporter))
                ConsoleInputManager.Reporter = null;
        }
    }

    private sealed class WorkflowTerminalUpdateObserver : IDisposable
    {
        private readonly ICliBackend backend;
        private readonly Guid workflowId;
        private readonly TaskCompletionSource terminalUpdateSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObservedWorkflowCompletion? Completion { get; private set; }

        public WorkflowTerminalUpdateObserver(ICliBackend backend, Guid workflowId)
        {
            this.backend = backend;
            this.workflowId = workflowId;
            backend.StateUpdated += OnStateUpdated;
        }

        public async Task WaitForTerminalUpdateAsync(CancellationToken ct)
        {
            if (terminalUpdateSeen.Task.IsCompleted)
                return;

            await terminalUpdateSeen.Task.WaitAsync(ct);
        }

        public void Dispose()
            => backend.StateUpdated -= OnStateUpdated;

        private void OnStateUpdated(DaemonClientUpdate update)
        {
            if (update.Status != DaemonClientApplyStatus.Applied)
                return;

            var workflow = update.ChangedWorkflows.FirstOrDefault(workflow =>
                workflow.WorkflowId == workflowId
                && workflow.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed);
            if (workflow == null)
                return;

            Completion = new ObservedWorkflowCompletion(
                workflow,
                backend.ClientStore.GetWorkflowJobs(workflowId).ToArray());
            terminalUpdateSeen.TrySetResult();
        }
    }

    private sealed record ObservedWorkflowCompletion(
        WorkflowSummaryDto Workflow,
        IReadOnlyList<JobSummaryDto> Jobs);

    internal static CliExitCode DetermineLocalExitCode(JobList queue)
    {
        var allJobs = queue.AllJobs().ToArray();
        if (allJobs.Any(job => job.TerminalOutcome == JobTerminalOutcome.Cancelled))
            return CliExitCode.Cancelled;

        var (_, fails, _) = Printing.CountUserFacingCompletionsDetailed(queue);
        if (fails > 0)
            return CliExitCode.WorkFailed;

        if (allJobs.Any(IsFailedPrintResultsJob))
            return CliExitCode.WorkFailed;

        return allJobs.Any(IsInfrastructureFailure)
            ? CliExitCode.WorkFailed
            : CliExitCode.Success;
    }

    private static bool IsFailedPrintResultsJob(Job job)
        => job.Config?.PrintResults == true && job.IsUnsuccessfulTerminal;

    private static bool IsInfrastructureFailure(Job job)
        => job.IsUnsuccessfulTerminal
            && job is ExtractJob or JobList;

    private static async Task<CliExitCode> DetermineRemoteExitCodeAsync(
        ICliBackend backend,
        Guid workflowId,
        CancellationToken ct,
        ObservedWorkflowCompletion? observedCompletion = null)
    {
        WorkflowSummaryDto workflow;
        JobSummaryDto[] summaries;
        if (observedCompletion != null)
        {
            workflow = observedCompletion.Workflow;
            summaries = observedCompletion.Jobs.OrderBy(job => job.DisplayId).ToArray();
        }
        else
        {
            var detail = await backend.GetWorkflowAsync(workflowId, ct);
            if (detail == null)
                return CliExitCode.WorkFailed;
            workflow = detail.Summary;
            summaries = (await backend.GetJobsAsync(
                    new JobQuery(null, null, null, workflowId, IncludeAll: true),
                    ct))
                .OrderBy(job => job.DisplayId)
                .ToArray();
        }

        if (summaries.Any(job => job.TerminalOutcome == ServerJobTerminalOutcome.Cancelled))
            return CliExitCode.Cancelled;
        var jobsById = summaries.ToDictionary(job => job.JobId);
        var supersededSourceJobIds = summaries
            .Select(job => job.SourceJobId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        int successes = 0;
        int fails = 0;
        int skipped = 0;
        foreach (var summary in summaries)
            CountRemoteUserFacingCompletion(summary, jobsById, supersededSourceJobIds, ref successes, ref fails, ref skipped);

        if (fails > 0 || workflow.State == ServerWorkflowState.Failed)
            return CliExitCode.WorkFailed;

        return CliExitCode.Success;
    }

    private static void PrintCompactTrackBatchResolved(
        ActivityEventDto activity,
        TrackBatchResolvedActivityDto batch,
        CliOutputController output)
    {
        if (batch.IsNormal && batch.PendingCount == 1 && batch.ExistingCount + batch.NotFoundCount == 0)
            return;

        string skipped = string.Join(
            ", ",
            new[]
            {
                batch.ExistingCount > 0 ? $"{batch.ExistingCount} already exist" : null,
                batch.NotFoundCount > 0 ? $"{batch.NotFoundCount} not found" : null,
            }.Where(value => value != null));
        string message = batch.PendingCount > 0
            ? $"Downloading {batch.PendingCount} tracks{(skipped.Length > 0 ? $" ({skipped})" : "")}."
            : $"No tracks pending{(skipped.Length > 0 ? $" ({skipped})" : "")}.";

        if (batch.IsNormal || activity.JobId == null)
        {
            Write(output, LogLevel.Information, message);
            return;
        }

        var line = new TerminalLogLine(
            TerminalLogKind.Status,
            activity.JobId.Value.ToString(),
            batch.DisplayId,
            "Job List",
            message);
        output.WriteOutput(new CliOutputEvent.JobLog(line));
    }

    private static bool ShouldAttachHumanProgressReporter(PrintOption printOption)
        => !IsMachineReadablePrint(printOption);

    private static bool ShouldPrintHumanBatchPreview(PrintOption printOption)
        => !IsMachineReadablePrint(printOption);

    private static bool IsMachineReadablePrint(PrintOption printOption)
        => (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;

    internal static Task PrintRemoteCompleteAsync(
        ICliBackend backend,
        Guid workflowId,
        CancellationToken ct,
        TextWriter? output = null)
        => PrintRemoteCompleteCoreAsync(backend, workflowId, ct, output, observedCompletion: null);

    private static async Task PrintRemoteCompleteCoreAsync(
        ICliBackend backend,
        Guid workflowId,
        CancellationToken ct,
        TextWriter? output,
        ObservedWorkflowCompletion? observedCompletion)
    {
        JobSummaryDto[] summaries;
        if (observedCompletion != null)
        {
            summaries = observedCompletion.Jobs.OrderBy(job => job.DisplayId).ToArray();
        }
        else
        {
            var workflow = await backend.GetWorkflowAsync(workflowId, ct);
            if (workflow == null)
                return;
            summaries = (await backend.GetJobsAsync(
                    new JobQuery(null, null, null, workflowId, IncludeAll: true),
                    ct))
                .OrderBy(job => job.DisplayId)
                .ToArray();
        }
        var jobsById = summaries.ToDictionary(job => job.JobId);
        var supersededSourceJobIds = summaries
            .Select(job => job.SourceJobId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        int successes = 0;
        int fails = 0;
        int skipped = 0;
        foreach (var summary in summaries)
            CountRemoteUserFacingCompletion(summary, jobsById, supersededSourceJobIds, ref successes, ref fails, ref skipped);

        if (output is null)
        {
            Printing.PrintComplete(successes, fails, skipped);
            return;
        }

        string? message = Printing.FormatComplete(successes, fails, skipped);
        if (message is not null)
        {
            output.WriteLine();
            output.WriteLine(message);
        }
    }

    private static void CountRemoteUserFacingCompletion(
        JobSummaryDto summary,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById,
        IReadOnlySet<Guid> supersededSourceJobIds,
        ref int successes,
        ref int fails,
        ref int skipped)
    {
        if (supersededSourceJobIds.Contains(summary.JobId))
            return;

        if (IsRemoteInfrastructureJobKind(summary.Kind))
            return;

        if (summary.Kind == ServerJobKind.Song
            && summary.ParentJobId is Guid parentId
            && jobsById.TryGetValue(parentId, out var parent)
            && parent.Kind == ServerJobKind.Album)
        {
            return;
        }

        CountSummary(summary, ref successes, ref fails, ref skipped);
    }

    // TODO [ARCHITECTURE]: Move local and remote completion accounting onto one
    // shared domain-level summary model. Manual skips, already-exists skips,
    // partial success, cancellation, and infrastructure jobs should not be
    // recounted independently by each CLI/API consumer.
    private static bool IsRemoteInfrastructureJobKind(ServerJobKind kind)
        => kind is ServerJobKind.Extract or ServerJobKind.JobList or ServerJobKind.RetrieveFolder
            or ServerJobKind.Aggregate or ServerJobKind.AlbumAggregate;

    private static void CountSummary(JobSummaryDto summary, ref int successes, ref int fails, ref int skipped)
    {
        if (IsSuccessfulRemoteOutcome(summary.TerminalOutcome, summary.SkipReason))
            successes++;
        else if (IsManualSkipRemoteOutcome(summary.TerminalOutcome, summary.SkipReason))
            skipped++;
        else if (IsFailedRemoteOutcome(summary.TerminalOutcome, summary.SkipReason))
            fails++;
    }

    private static bool IsSuccessfulRemoteOutcome(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason)
        => outcome == ServerJobTerminalOutcome.Succeeded
            || (outcome == ServerJobTerminalOutcome.Skipped && skipReason == ServerJobSkipReason.AlreadyExists);

    private static bool IsManualSkipRemoteOutcome(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason)
        => outcome == ServerJobTerminalOutcome.Skipped && skipReason == ServerJobSkipReason.Manual;

    private static bool IsFailedRemoteOutcome(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason)
        => (outcome is ServerJobTerminalOutcome.Failed
                or ServerJobTerminalOutcome.Cancelled
                or ServerJobTerminalOutcome.PartialSuccess)
            || (outcome == ServerJobTerminalOutcome.Skipped
                && skipReason is not ServerJobSkipReason.AlreadyExists and not ServerJobSkipReason.Manual);

    internal static async Task PrintRemoteRequestedOutputAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct,
        TextWriter? output = null,
        IReadOnlyCollection<Guid>? expectedJobIds = null)
    {
        var queue = await BuildRemotePrintQueueAsync(
            backend,
            workflowId,
            settings,
            expectedJobIds,
            ct);
        using var outputScope = output is null ? null : Printing.RedirectOutput(output);
        PrintOutputRenderer.PrintRequestedOutput(queue);
    }

    internal static async Task PrintRemoteResultsAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
        => await PrintRemoteRequestedOutputAsync(backend, workflowId, settings, ct);

    internal static async Task PrintRemoteJobOutputAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
        => await PrintRemoteRequestedOutputAsync(backend, workflowId, settings, ct);

    private static async Task<JobList> BuildRemotePrintQueueAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        IReadOnlyCollection<Guid>? expectedJobIds,
        CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp()
            + (long)(Stopwatch.Frequency * RetainedWorkflowAvailabilityTimeout.TotalSeconds);
        while (true)
        {
            var (queue, complete) = await TryBuildRemotePrintQueueAsync(
                backend,
                workflowId,
                settings,
                expectedJobIds,
                ct);
            if (complete)
                return queue;

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new InvalidOperationException(
                    "The completed workflow did not become available from daemon history. " +
                    "Check that daemon persistence is enabled and healthy.");
            }

            await Task.Delay(25, ct);
        }
    }

    private static async Task<(JobList Queue, bool Complete)> TryBuildRemotePrintQueueAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        IReadOnlyCollection<Guid>? expectedJobIds,
        CancellationToken ct)
    {
        var queue = new JobList("remote workflow")
        {
            Config = SettingsCloner.Clone(settings),
        };

        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return (queue, false);

        var roots = await backend.GetJobsAsync(
            new JobQuery(null, null, null, workflowId, IncludeAll: false),
            ct);
        if (roots.Count != workflow.Summary.RootJobCount)
            return (queue, false);

        var details = new Dictionary<Guid, JobDetailDto>();
        foreach (var summary in roots)
        {
            if (!await LoadRemoteJobTreeAsync(backend, summary.JobId, details, ct))
                return (queue, false);
        }

        if (expectedJobIds != null
            && (details.Count != expectedJobIds.Count
                || expectedJobIds.Any(jobId => !details.ContainsKey(jobId))))
        {
            return (queue, false);
        }

        var rootDetails = details.Values
            .Where(detail => detail.Summary.ParentJobId == null)
            .OrderBy(detail => detail.Summary.DisplayId)
            .ToList();

        var visited = new HashSet<Guid>();
        foreach (var root in rootDetails)
        {
            var job = await ToRemotePrintJobAsync(backend, root, details, settings, visited, ct);
            if (job != null)
                queue.Add(job);
        }

        return (queue, true);
    }

    private static async Task<Job?> ToRemotePrintJobAsync(
        ICliBackend backend,
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        DownloadSettings settings,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        if (!visited.Add(detail.Summary.JobId) || detail.Payload == null)
            return null;

        var effectiveSettings = RemotePrintSettings(settings, detail.Summary);

        Job? job = detail.Payload switch
        {
            ExtractJobPayloadDto extract
                => await ToRemoteExtractPrintJobAsync(backend, detail, extract, details, effectiveSettings, visited, ct),

            JobListPayloadDto
                => await ToRemoteJobListPrintJobAsync(backend, detail, details, effectiveSettings, visited, ct),

            SearchJobPayloadDto search
                => effectiveSettings.PrintResults
                    ? await ToSearchResultsJobAsync(backend, detail.Summary.JobId, search, ct)
                    : ToSearchJob(search, detail.Summary),

            SongJobPayloadDto song
                => effectiveSettings.PrintResults
                    ? await ToSongResultsJobAsync(backend, detail.Summary.JobId, song, ct)
                    : ToSongJob(song, detail.Summary),

            AlbumJobPayloadDto album
                => effectiveSettings.PrintResults
                    ? await ToAlbumResultsJobAsync(backend, detail.Summary.JobId, album, detail.Summary, ct)
                    : ToAlbumJob(album, detail.Summary),

            AggregateJobPayloadDto aggregate
                => effectiveSettings.PrintResults
                    ? await ToAggregateResultsJobAsync(
                        backend,
                        detail.Summary.JobId,
                        aggregate,
                        ct)
                    : ToAggregateJob(aggregate, ChildrenOf(detail, details)),

            AlbumAggregateJobPayloadDto albumAggregate
                => effectiveSettings.PrintResults
                    ? await ToAlbumAggregateResultsJobAsync(
                        backend,
                        detail.Summary.JobId,
                        albumAggregate,
                        ct)
                    : ToAlbumAggregateJob(albumAggregate, detail.Summary),

            RetrieveFolderJobPayloadDto folder
                => ToRetrieveFolderJob(folder, detail.Summary),

            _ => null,
        };

        if (job != null)
            ApplyRemotePrintConfig(job, effectiveSettings);

        return job;
    }

    private static async Task<ExtractJob> ToRemoteExtractPrintJobAsync(
        ICliBackend backend,
        JobDetailDto detail,
        ExtractJobPayloadDto extract,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        DownloadSettings settings,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        var job = new ExtractJob(extract.Input, ParseRemoteInputType(extract.InputType));
        ApplyJobOutcome(job, detail.Summary.LifecycleState, detail.Summary.ActivityPhase, detail.Summary.TerminalOutcome, detail.Summary.SkipReason, detail.Summary.FailureReason, detail.Summary.FailureMessage, detail.Summary.CancellationSource);

        if (extract.ResultJobId is Guid resultJobId
            && details.TryGetValue(resultJobId, out var resultDetail))
        {
            job.Result = await ToRemotePrintJobAsync(backend, resultDetail, details, settings, visited, ct);
        }

        return job;
    }

    private static async Task<JobList> ToRemoteJobListPrintJobAsync(
        ICliBackend backend,
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        DownloadSettings settings,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        var jobList = new JobList(detail.Summary.ItemName ?? detail.Summary.QueryText);
        ApplyJobOutcome(jobList, detail.Summary.LifecycleState, detail.Summary.ActivityPhase, detail.Summary.TerminalOutcome, detail.Summary.SkipReason, detail.Summary.FailureReason, detail.Summary.FailureMessage, detail.Summary.CancellationSource);

        foreach (var child in ChildrenOf(detail, details))
        {
            var childJob = await ToRemotePrintJobAsync(backend, child, details, settings, visited, ct);
            if (childJob != null)
                jobList.Add(childJob);
        }

        return jobList;
    }

    private static async Task<Job?> ToSearchResultsJobAsync(
        ICliBackend backend,
        Guid searchJobId,
        SearchJobPayloadDto search,
        CancellationToken ct)
    {
        if (search.DefaultProjection == ServerSearchDefaultProjectionKind.Album
            && search.AlbumQuery != null)
        {
            var folders = await backend.GetFolderResultsAsync(
                searchJobId,
                new FolderSearchProjectionRequestDto(
                    search.AlbumQuery,
                    IncludeFiles: true),
                ct);
            return folders == null
                ? null
                : new AlbumJob(ToAlbumQuery(search.AlbumQuery))
                {
                    Results = folders.Items.Select(CliSearchProjectionMapper.ToCore).ToList(),
                };
        }

        var fileProjection = new FileSearchProjectionRequestDto(
            search.SongQuery
                ?? new SongQueryDto(null, search.QueryText, null, null, null, false),
            search.IncludeFullResults);
        var files = await backend.GetFileResultsAsync(searchJobId, fileProjection, ct);
        return files == null
            ? null
            : new SongJob(ToSongQuery(fileProjection.SongQuery ?? new SongQueryDto(null, search.QueryText, null, null, null, false)))
            {
                Candidates = files.Items.Select(CliSearchProjectionMapper.ToCore).ToList(),
            };
    }

    private static async Task<Job?> ToSongResultsJobAsync(
        ICliBackend backend,
        Guid songJobId,
        SongJobPayloadDto song,
        CancellationToken ct)
    {
        var files = await backend.GetFileResultsAsync(songJobId,
            new FileSearchProjectionRequestDto(IncludeFullResults: true), ct);
        var job = ToSongJob(song);
        job.Candidates = files?.Items.Select(CliSearchProjectionMapper.ToCore).ToList();
        return job;
    }

    private static async Task<Job?> ToAlbumResultsJobAsync(
        ICliBackend backend,
        Guid albumJobId,
        AlbumJobPayloadDto album,
        JobSummaryDto? summary,
        CancellationToken ct)
    {
        var folders = await backend.GetFolderResultsAsync(albumJobId, includeFiles: true, ct);
        var job = ToAlbumJob(album, summary);
        job.Results = folders?.Items.Select(CliSearchProjectionMapper.ToCore).ToList() ?? [];
        return job;
    }

    private static async Task<Job?> ToAggregateResultsJobAsync(
        ICliBackend backend,
        Guid aggregateJobId,
        AggregateJobPayloadDto aggregate,
        CancellationToken ct)
    {
        // Print-only discovery does not execute/register child jobs. Read the
        // same source projection as the UI instead of depending on those jobs.
        var results = await backend.GetAggregateTrackResultsAsync(
            aggregateJobId,
            new AggregateTrackProjectionRequestDto(aggregate.Query, IncludeCandidates: true),
            ct);
        return results == null ? null : new AggregateJob(ToSongQuery(aggregate.Query))
        {
            Songs = results.Items.Select(result => new SongJob(ToSongQuery(result.Query))
            {
                ItemName = result.ItemName,
                Candidates = result.Candidates?.Select(CliSearchProjectionMapper.ToCore).ToList(),
            }).ToList(),
        };
    }

    private static async Task<Job?> ToAlbumAggregateResultsJobAsync(
        ICliBackend backend,
        Guid aggregateJobId,
        AlbumAggregateJobPayloadDto aggregate,
        CancellationToken ct)
    {
        var results = await backend.GetAggregateAlbumResultsAsync(
            aggregateJobId,
            new AggregateAlbumProjectionRequestDto(aggregate.Query, IncludeFolders: true),
            ct);
        return results == null ? null : new AlbumAggregateJob(ToAlbumQuery(aggregate.Query))
        {
            Albums = results.Items.Select(result => new AlbumJob(ToAlbumQuery(result.Query))
            {
                ItemName = result.ItemName,
                Results = result.Folders?.Select(CliSearchProjectionMapper.ToCore).ToList() ?? [],
            }).ToList(),
        };
    }

    private static SearchJob ToSearchJob(SearchJobPayloadDto search, JobSummaryDto summary)
    {
        SearchJob job;
        if (search.DefaultProjection == ServerSearchDefaultProjectionKind.Album
            && search.AlbumQuery != null)
        {
            job = new SearchJob(ToAlbumQuery(search.AlbumQuery));
        }
        else if (search.SongQuery != null)
        {
            job = new SearchJob(
                ToSongQuery(search.SongQuery),
                search.IncludeFullResults);
        }
        else
        {
            job = new SearchJob(search.QueryText);
        }

        ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        return job;
    }

    private static RetrieveFolderJob ToRetrieveFolderJob(RetrieveFolderJobPayloadDto folder, JobSummaryDto summary)
    {
        var directory = new PeerDirectoryIdentity(folder.Username, folder.FolderPath);
        var job = new RetrieveFolderJob(directory)
        {
            NewFilesFoundCount = folder.NewFilesFoundCount,
            RetrievalOutcome = ToCoreFolderRetrievalOutcome(folder.RetrievalOutcome),
        };

        ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        return job;
    }

    private static void ApplyRemotePrintConfig(Job job, DownloadSettings settings)
        => job.Config = SettingsCloner.Clone(settings);

    private static DownloadSettings RemotePrintSettings(DownloadSettings inherited, JobSummaryDto summary)
    {
        var settings = SettingsCloner.Clone(inherited);
        if (summary.PrintOption != ServerPrintOption.None)
            settings.PrintOption = summary.PrintOption.ToCore();
        return settings;
    }

    private static InputType? ParseRemoteInputType(string? inputType)
        => Enum.TryParse<InputType>(inputType, ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private static FolderRetrievalOutcome ToCoreFolderRetrievalOutcome(ServerFolderRetrievalOutcome outcome)
        => Enum.TryParse<FolderRetrievalOutcome>(outcome.ToString(), out var parsed)
            ? parsed
            : FolderRetrievalOutcome.None;

    private static async Task<bool> LoadRemoteJobTreeAsync(
        ICliBackend backend,
        Guid jobId,
        Dictionary<Guid, JobDetailDto> details,
        CancellationToken ct)
    {
        if (details.ContainsKey(jobId))
            return true;

        var detail = await backend.GetJobDetailAsync(jobId, ct);
        if (detail == null)
            return false;

        details[jobId] = detail;

        if (detail.Payload is ExtractJobPayloadDto { ResultJobId: Guid resultJobId })
        {
            if (!await LoadRemoteJobTreeAsync(backend, resultJobId, details, ct))
                return false;
        }

        var children = await backend.GetJobsAsync(
            new JobQuery(null, null, null, detail.Summary.WorkflowId, IncludeAll: true,
                ParentJobId: detail.Summary.JobId),
            ct);
        if (children.Count != detail.ChildCount)
            return false;

        foreach (var child in children)
        {
            if (!await LoadRemoteJobTreeAsync(backend, child.JobId, details, ct))
                return false;
        }

        return true;
    }

    private static List<JobDetailDto> ChildrenOf(
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

    private static string[]? SplitProfileNames(string? names)
        => string.IsNullOrWhiteSpace(names)
            ? null
            : names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void LogCliSessionStart(ILogger logger, RemoteSettings remoteSettings)
        => CliLogMessages.SessionStarted(
            logger,
            remoteSettings.IsEnabled ? "remote" : "local");

    private static CliExitCode LogCliSessionExit(
        ILogger logger,
        CliExitCode exitCode,
        RemoteSettings? remoteSettings = null)
    {
        CliLogMessages.SessionEnded(
            logger,
            remoteSettings?.IsEnabled == true ? "remote" : "local",
            (int)exitCode);
        return exitCode;
    }

    private static async Task RunDaemonAsync(
        string[] args,
        ConfigFile configFile,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        DaemonSettings daemonSettings)
    {
        var url = BuildDaemonListenUrl(daemonSettings);
        EnsureDaemonEndpointAvailable(daemonSettings);
        var options = new ServerOptions
        {
            Engine = CreateDaemonEngineSettings(engineSettings),
            LaunchDownloadSettings = ConfigManager.CreateCliDownloadSettingsPatch(args),
            LaunchProfileNames = ConfigManager.ExtractProfileName(args)?
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? [],
            Profiles = ConfigManager.CreateProfileCatalog(configFile),
            ConfigDir = configFile.ConfigDir,
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = daemonSettings.DataDirectory,
                RetentionEnabled = daemonSettings.RetentionEnabled,
                CompletedJobHistoryAge = daemonSettings.CompletedJobRetention,
                UnsuccessfulJobHistoryAge = daemonSettings.UnsuccessfulJobRetention,
                SearchResultAge = daemonSettings.SearchResultRetention,
                TransferHistoryAge = daemonSettings.TransferRetention,
                PrivateMessageHistoryAge = daemonSettings.PrivateMessageRetention,
                RoomMessageHistoryAge = daemonSettings.RoomMessageRetention,
                MaximumRetainedJobs = daemonSettings.MaximumRetainedJobs,
            },
        };

        // A published CLI carries the WebUI beside the executable. Resolve
        // static assets there even when launched from a different directory.
        var app = ServerHost.Build([..args, "--contentRoot", AppContext.BaseDirectory], options, url);
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Sockseek.Cli.Daemon");
        CliLogMessages.DaemonStarting(logger, url);
        if (IsDaemonListenAddressNetworkExposed(daemonSettings))
            CliLogMessages.DaemonNetworkExposed(logger);
        Console.WriteLine("Press Ctrl+C to stop.");
        try
        {
            await app.RunAsync();
        }
        finally
        {
            CliLogMessages.DaemonStopped(logger);
        }
    }

    internal static EngineSettings CreateDaemonEngineSettings(EngineSettings settings)
    {
        EngineSettings daemonSettings = SettingsCloner.Clone(settings);
        if (daemonSettings.LogLevel == LogLevel.Information)
            daemonSettings.LogLevel = LogLevel.Debug;
        return daemonSettings;
    }

    internal static void EnsureDaemonEndpointAvailable(DaemonSettings daemonSettings)
    {
        if (!System.Net.IPAddress.TryParse(daemonSettings.ListenIp, out var ipAddress))
            throw new ArgumentException($"Invalid daemon listen IP '{daemonSettings.ListenIp}'. Use a valid IP address such as 127.0.0.1, 0.0.0.0, ::1, or ::.");

        try
        {
            using var listener = new System.Net.Sockets.TcpListener(ipAddress, daemonSettings.ListenPort);
            listener.Start();
            listener.Stop();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            throw new DaemonEndpointUnavailableException(
                $"Cannot start Sockseek daemon on {BuildDaemonListenUrl(daemonSettings)}: {ExceptionText.Summary(ex)}",
                ex);
        }
    }

    internal static string BuildDaemonListenUrl(DaemonSettings daemonSettings)
    {
        if (!System.Net.IPAddress.TryParse(daemonSettings.ListenIp, out var ipAddress))
            throw new ArgumentException($"Invalid daemon listen IP '{daemonSettings.ListenIp}'. Use a valid IP address such as 127.0.0.1, 0.0.0.0, ::1, or ::.");

        var host = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ipAddress}]"
            : ipAddress.ToString();

        return $"http://{host}:{daemonSettings.ListenPort}";
    }

    internal static bool IsDaemonListenAddressNetworkExposed(DaemonSettings daemonSettings)
    {
        if (!System.Net.IPAddress.TryParse(daemonSettings.ListenIp, out var ipAddress))
            throw new ArgumentException($"Invalid daemon listen IP '{daemonSettings.ListenIp}'. Use a valid IP address such as 127.0.0.1, 0.0.0.0, ::1, or ::.");

        return ipAddress.Equals(System.Net.IPAddress.Any)
            || ipAddress.Equals(System.Net.IPAddress.IPv6Any);
    }

    internal sealed class DaemonEndpointUnavailableException : Exception
    {
        public DaemonEndpointUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
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
            DownloadPath = song.File.DownloadPath,
            BytesTransferred = song.File.BytesTransferred,
            FileSize = song.File.FileSize,
            DownloadSource = ToSongDownloadSource(song.DownloadSource),
        };

        ApplyJobOutcome(job, song.LifecycleState, song.ActivityPhase, song.TerminalOutcome, song.SkipReason, song.FailureReason, song.FailureMessage, song.CancellationSource);

        if (summary != null)
        {
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        }

        if (song.ExactTarget != null)
        {
            job.ExactTarget = ToPeerFileTarget(song.ExactTarget);
        }
        else if (!string.IsNullOrWhiteSpace(song.ResolvedUsername)
            && !string.IsNullOrWhiteSpace(song.ResolvedFilename))
        {
            job.ResolvedTarget = CliSearchProjectionMapper.ToCore(new FileCandidateDto(
                new FileCandidateRefDto(song.ResolvedUsername, song.ResolvedFilename),
                song.ResolvedUsername,
                song.ResolvedFilename,
                new PeerInfoDto(song.ResolvedUsername, song.ResolvedHasFreeUploadSlot, song.ResolvedUploadSpeed),
                new FileMetadataDto(
                    Utils.GetFileNameSlsk(song.ResolvedFilename),
                    song.ResolvedSize ?? 0,
                    song.ResolvedExtension,
                    null,
                    null,
                    null,
                    null,
                    song.ResolvedAttributes)));
        }

        return job;
    }

    private static PeerFileTarget ToPeerFileTarget(PeerFileTargetDto target)
        => new(
            new PeerFileIdentity(target.Username, target.Filename),
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.Select(attribute =>
                new FileAttributeSnapshot(attribute.Type, attribute.Value, 0)).ToList());

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album)
        => ToAlbumJob(album, null);

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album, JobSummaryDto? summary)
    {
        var job = new AlbumJob(ToAlbumQuery(album.Query))
        {
            DownloadPath = album.Directory.DownloadPath,
        };

        if (summary != null)
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);

        return job;
    }

    private static AggregateJob ToAggregateJob(
        AggregateJobPayloadDto aggregate,
        IEnumerable<JobDetailDto> children)
        => new(ToSongQuery(aggregate.Query))
        {
            Songs = children
                .Select(child => child.Payload)
                .OfType<SongJobPayloadDto>()
                .Select(ToSongJob)
                .ToList(),
        };

    private static AlbumAggregateJob ToAlbumAggregateJob(AlbumAggregateJobPayloadDto albumAggregate, JobSummaryDto? summary = null)
    {
        var job = new AlbumAggregateJob(ToAlbumQuery(albumAggregate.Query));
        if (summary != null)
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        return job;
    }

    private static void ApplyJobOutcome(
        Job job,
        ServerJobLifecycleState? lifecycleState,
        ServerJobActivityPhase? activityPhase,
        ServerJobTerminalOutcome? terminalOutcome,
        ServerJobSkipReason? skipReason,
        ServerJobFailureReason? failureReason,
        string? failureMessage,
        ServerJobCancellationSource cancellationSource)
    {
        if (lifecycleState == ServerJobLifecycleState.AwaitingSelection)
        {
            job.SetAwaitingSelection();
            return;
        }

        if (lifecycleState is ServerJobLifecycleState.Running or ServerJobLifecycleState.Pending)
        {
            ApplyJobActivity(job, lifecycleState.Value, activityPhase ?? ServerJobActivityPhase.None);
            return;
        }

        if (lifecycleState != ServerJobLifecycleState.Terminal || terminalOutcome is null or ServerJobTerminalOutcome.None)
            return;

        TryToCoreFailureReason(failureReason, out var parsedFailureReason);

        switch (terminalOutcome.Value)
        {
            case ServerJobTerminalOutcome.Succeeded:
                job.SetDone();
                break;
            case ServerJobTerminalOutcome.Skipped:
                job.SetSkipped(ToCoreSkipReason(skipReason), parsedFailureReason);
                break;
            case ServerJobTerminalOutcome.PartialSuccess:
                job.SetPartialSuccess(failureMessage);
                break;
            case ServerJobTerminalOutcome.Cancelled:
                job.SetCancelled(ToCoreCancellationSource(cancellationSource), failureMessage);
                break;
            case ServerJobTerminalOutcome.Failed:
                job.Fail(parsedFailureReason, failureMessage);
                break;
        }
    }

    private static void ApplyJobActivity(Job job, ServerJobLifecycleState lifecycleState, ServerJobActivityPhase activityPhase)
    {
        if (lifecycleState == ServerJobLifecycleState.Pending)
        {
            job.ResetToPending();
            return;
        }

        var corePhase = activityPhase switch
        {
            ServerJobActivityPhase.Extracting => JobActivityPhase.Extracting,
            ServerJobActivityPhase.Downloading => JobActivityPhase.Downloading,
            ServerJobActivityPhase.RetrievingFolder => JobActivityPhase.RetrievingFolder,
            ServerJobActivityPhase.RunningChildren => JobActivityPhase.RunningChildren,
            ServerJobActivityPhase.Searching => JobActivityPhase.Searching,
            ServerJobActivityPhase.WaitingForSearchConcurrency => JobActivityPhase.WaitingForSearchConcurrency,
            ServerJobActivityPhase.SearchRateLimited => JobActivityPhase.SearchRateLimited,
            ServerJobActivityPhase.ProcessingSearchResults => JobActivityPhase.ProcessingSearchResults,
            ServerJobActivityPhase.Organizing => JobActivityPhase.Organizing,
            ServerJobActivityPhase.RunningOnComplete => JobActivityPhase.RunningOnComplete,
            ServerJobActivityPhase.RunningFallback => JobActivityPhase.RunningFallback,
            _ => JobActivityPhase.None,
        };

        if (corePhase == JobActivityPhase.None)
            job.UpdateActivity(JobActivityPhase.RunningChildren);
        else
            job.UpdateActivity(corePhase);
    }

    private static bool TryToCoreFailureReason(ServerJobFailureReason? reason, out JobFailureReason coreReason)
    {
        if (reason == null)
        {
            coreReason = default;
            return false;
        }

        return Enum.TryParse(reason.Value.ToString(), out coreReason);
    }

    private static SongDownloadSource ToSongDownloadSource(ServerSongDownloadSource source)
        => Enum.TryParse<SongDownloadSource>(source.ToString(), out var coreSource)
            ? coreSource
            : SongDownloadSource.None;

    private static JobSkipReason ToCoreSkipReason(ServerJobSkipReason? reason)
        => reason == null
            ? JobSkipReason.None
            : Enum.TryParse(reason.Value.ToString(), out JobSkipReason coreReason)
                ? coreReason
                : JobSkipReason.None;

    private static JobCancellationSource ToCoreCancellationSource(ServerJobCancellationSource source)
        => source == ServerJobCancellationSource.None
            ? JobCancellationSource.InternalEngine
            : Enum.TryParse(source.ToString(), out JobCancellationSource coreSource)
                ? coreSource
                : JobCancellationSource.InternalEngine;

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

    private static void Write(
        CliOutputController output,
        LogLevel level,
        string message,
        string category = "cli")
        => CliProcessOutput.Write(output, level, message, category);
}
