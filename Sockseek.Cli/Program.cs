using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;
using Soulseek;

namespace Sockseek.Cli;

internal static partial class Program
{
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
        if (Help.PrintAndExitIfNeeded(args))
            return (int)CliExitCode.Success;

        SockseekLog.SetupExceptionHandling();
        using var output = CliOutputController.Install(args);

        try
        {
            return (int)await MainCore(args, output);
        }
        catch (Exception ex)
        {
            SockseekLog.Fatal($"Unhandled CLI startup error: {SockseekLog.ExceptionSummary(ex)}");
            return (int)CliExitCode.WorkFailed;
        }
    }

    internal static async Task<CliExitCode> MainCore(string[] args)
    {
        using var output = CliOutputController.CreateDetached();
        return await MainCore(args, output);
    }

    internal static async Task<CliExitCode> MainCore(string[] args, CliOutputController output)
    {
        bool daemonMode = args.Length > 0 && string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase);
        var bindArgs = daemonMode ? args.Skip(1).ToArray() : args;

        string configPath;
        ParsedInvocation invocation;
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
            invocation = ConfigManager.ParseInvocation(bindArgs);
            configPath = ConfigManager.GetConfigPath(invocation);
            configFile = ConfigManager.Load(configPath);
            (engineSettings, rootSettings, cliSettings, daemonSettings, remoteSettings) = ConfigManager.BindAll(configFile, invocation);
            ConfigManager.ApplyAutoProfileCliSettings(configFile, rootSettings, cliSettings);
        }
        catch (Exception ex) when (ex is ArgumentException || ex.Message.StartsWith("Input error:"))
        {
            SockseekLog.Error(ex.Message);
            return CliExitCode.UsageError;
        }

        string? profileArg = invocation.ProfileName;
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
                SockseekLog.Error($"Failed to retrieve profiles from remote daemon: {SockseekLog.ExceptionSummary(ex)}");
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

        if (!string.IsNullOrWhiteSpace(engineSettings.LogFilePath))
            SockseekLog.AddOrReplaceFile(engineSettings.LogFilePath, engineSettings.LogLevel < LogLevel.Debug ? engineSettings.LogLevel : LogLevel.Debug);

        SockseekLog.SetConsoleLogLevel(rootSettings.NonVerbosePrint ? LogLevel.Error : engineSettings.LogLevel);
        if (CliOutputController.WouldUseLiveRendering(cliSettings))
            engineSettings.ReportIntervalProgress = false;

        if (daemonMode)
        {
            try
            {
                await RunDaemonAsync(bindArgs, invocation, configFile, engineSettings, rootSettings, daemonSettings);
            }
            catch (ArgumentException ex)
            {
                SockseekLog.Error(ex.Message);
                return CliExitCode.UsageError;
            }
            catch (DaemonEndpointUnavailableException ex)
            {
                SockseekLog.Error(ex.Message);
                return CliExitCode.WorkFailed;
            }
            catch (Exception ex)
            {
                SockseekLog.Fatal($"Unhandled daemon error: {SockseekLog.ExceptionSummary(ex)}");
                return CliExitCode.WorkFailed;
            }
            return CliExitCode.Success;
        }

        if (remoteSettings.Detach && (!remoteSettings.IsEnabled || cliSettings.InteractiveMode))
        {
            SockseekLog.Error("Input error: --detach requires --remote and cannot be used with interactive mode.");
            return CliExitCode.UsageError;
        }

        LogCliSessionStart(remoteSettings);

        using var cts = new CancellationTokenSource();

        if (remoteSettings.IsEnabled)
        {
            try
            {
                return await RunRemoteAsync(invocation, engineSettings, rootSettings, cliSettings, remoteSettings, output, cts);
            }
            catch (SockseekApiRequestException ex)
            {
                SockseekLog.Error(ex.Message);
                return CliExitCode.WorkFailed;
            }
            catch (Exception ex)
            {
                SockseekLog.Fatal($"Unhandled remote CLI error: {SockseekLog.ExceptionSummary(ex)}");
                return CliExitCode.WorkFailed;
            }
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
                SockseekLog.Error($"Diagnostic action failed: {SockseekLog.ExceptionSummary(ex)}");
            }

            if (!rootSettings.PrintOption.HasFlag(PrintOption.Index))
            {
                SockseekLog.Error("Input error: No input provided.");
                Help.PrintAndExitIfNeeded([]);
                return CliExitCode.UsageError;
            }
            return CliExitCode.Success;
        }

        IJobSettingsResolver jobSettingsResolver;
        try
        {
            jobSettingsResolver = ConfigManager.CreateJobSettingsResolver(configFile, invocation, cliSettings, engine: engineSettings);
        }
        catch (Exception ex) when (ex is ArgumentException || ex.Message.StartsWith("Input error:"))
        {
            SockseekLog.Error(ex.Message);
            return CliExitCode.UsageError;
        }

        var localSubmissionOptionsResolver = new SubmissionOptionsJobSettingsResolver(
            jobSettingsResolver,
            normalize: settings => SettingsNormalizer.NormalizeDownloadPaths(settings, settings.RuntimePathContext));
        var engine = new DownloadEngine(engineSettings, clientManager, localSubmissionOptionsResolver);
        var backend = new LocalCliBackend(engine, rootSettings, localSubmissionOptionsResolver);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else if (ShouldAttachHumanProgressReporter(rootSettings.PrintOption))
        {
            output.ConfigureLiveRendering(cliSettings, engineSettings.LogLevel);
            cliReporter = new CliProgressReporter(cliSettings, output);
            cliReporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, includeDiagnosticDetails: engineSettings.LogLevel <= LogLevel.Debug);
        eventLogger.Attach();

        backend.EventReceived += envelope =>
        {
            if (envelope.Type == "track-batch.resolved"
                && envelope.Payload is TrackBatchResolvedEventDto batch
                && batch.PrintOption == PrintOption.None
                && ShouldPrintHumanBatchPreview(batch.PrintOption)
                && cliReporter?.UsesLiveRendering != true)
            {
                PrintTrackBatchResolved(batch);
            }
        };

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
            _ = coordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token)
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

        ConsoleInputManager.Reporter = cliReporter;
        ConsoleInputManager.OnCancelRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write("Cancel job ID or all jobs? id/[A]ll/n: ", ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();

            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
            {
                SockseekLog.Info("Cancelling all jobs...");
                Printing.WriteLine("Cancelling all jobs...", ConsoleColor.Gray, force: true);
                engine.Cancel();
                return;
            }

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
            {
                if (await backend.CancelJobByDisplayIdAsync(id, ct: cts.Token))
                {
                    SockseekLog.Info($"Cancelling job [{id}]...");
                }
                else
                {
                    SockseekLog.Error($"Job ID [{id}] not found.");
                }
            }
            else
            {
                SockseekLog.Error($"Invalid input '{result.Input}'.");
            }
        };

        ConsoleInputManager.OnNextCandidateRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write("Try next candidate for job ID or n: ", ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();

            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
            {
                if (await backend.TryNextCandidateByDisplayIdAsync(id, ct: cts.Token))
                {
                    SockseekLog.Info($"Trying next candidate for job [{id}]...");
                }
                else
                {
                    SockseekLog.Error($"Job ID [{id}] not found or has no active download.");
                }
            }
            else
            {
                SockseekLog.Error($"Invalid input '{result.Input}'.");
            }
        };

        ConsoleInputManager.OnInfoRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write("Info for job ID (blank to cancel): ", ConsoleColor.Yellow, force: true);
            }
            var id = ConsoleInputManager.ReadJobIdInput();
            if (id == null) return;

            while (true)
            {
                int printStart = Console.IsOutputRedirected ? -1 : Console.CursorTop;

                var detail = await backend.GetJobDetailByDisplayIdAsync(id.Value, ct: cts.Token);
                if (detail == null)
                    SockseekLog.Error($"Job ID [{id}] not found.");
                else
                    JobInfoPrinter.Print(detail);

                lock (Printing.ConsoleLock)
                    Printing.Write("Info for job ID (r to refresh, blank to exit): ", ConsoleColor.Yellow, force: true);

                var result = ConsoleInputManager.ReadJobIdOrRefreshResult();

                if (result.Action == ConsoleInputManager.CancelPromptAction.Refresh)
                {
                    if (printStart >= 0)
                    {
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
                }
                else if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId.HasValue)
                {
                    id = result.JobId.Value;
                }
                else
                {
                    return;
                }
            }
        };

        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        try
        {
            await engine.RunAsync(cts.Token);
            SockseekLog.Trace("Main: RunAsync returned.");
            bool hasDownloadableJobs = PrintOutputRenderer.HasDownloadableJobs(engine.Queue);
            bool hasRequestedOutput = PrintOutputRenderer.HasRequestedOutput(engine.Queue);

            if (hasDownloadableJobs)
                Printing.PrintComplete(engine.Queue);

            if (hasRequestedOutput)
            {
                cliReporter?.Stop(printSummary: hasDownloadableJobs);
                cliReporter = null;
                PrintOutputRenderer.PrintRequestedOutput(engine.Queue);
            }

            var exitCode = LogCliSessionExit(DetermineLocalExitCode(engine.Queue));
            cliReporter?.Stop();
            cliReporter = null;

            return exitCode;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return LogCliSessionExit(CliExitCode.Cancelled);
        }
        catch (SoulseekConnectionUnavailableException ex)
        {
            SockseekLog.Error(ex.Message);
            return LogCliSessionExit(CliExitCode.WorkFailed);
        }
        catch (Exception ex)
        {
            SockseekLog.Fatal($"Unhandled CLI error: {SockseekLog.ExceptionSummary(ex)}");
            return LogCliSessionExit(CliExitCode.WorkFailed);
        }
        finally
        {
            SockseekLog.Trace("Main: Entered finally block. Disposing clientManager...");
            engine.Cancel();
            await engine.DisposeAsync();
            await cts.CancelAsync();
            cliReporter?.Stop();
            clientManager.Dispose();
            Printing.SetBuffering(false);
            SockseekLog.Trace("Main: ClientManager disposed.");
            SockseekLog.Trace("Main: Exiting.");
        }
    }

    internal static bool ArgsRequestProgressJson(IReadOnlyList<string> args)
        => CliOutputController.ArgsRequestProgressJson(args);

    private static async Task<CliExitCode> RunRemoteAsync(
        ParsedInvocation invocation,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        CliSettings cliSettings,
        RemoteSettings remoteSettings,
        CliOutputController output,
        CancellationTokenSource cts)
    {
        if (string.IsNullOrWhiteSpace(rootSettings.Extraction.Input))
        {
            SockseekLog.Error("Remote mode requires an input.");
            return CliExitCode.UsageError;
        }

        if (remoteSettings.Detach)
        {
            using var http = SockseekApiClient.CreateHttpClient(remoteSettings.ServerUrl!);
            var detached = await new SockseekApiClient(http).SubmitExtractJobAsync(
                BuildRemoteExtractRequest(rootSettings, BuildRemoteSubmissionOptions(invocation, cliSettings)),
                cts.Token);
            if (cliSettings.ProgressJson)
                new JsonStreamProgressReporter(Console.Out).ReportWorkflowSubmitted(detached.WorkflowId);
            else
                Console.WriteLine(detached.WorkflowId);
            return LogCliSessionExit(CliExitCode.Success, remoteSettings);
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

        var eventLogger = new EventLogger(backend, includeDiagnosticDetails: false);
        eventLogger.Attach();

        backend.EventReceived += envelope =>
        {
            if (envelope.Type == "track-batch.resolved"
                && envelope.Payload is TrackBatchResolvedEventDto batch
                && batch.PrintOption == PrintOption.None
                && ShouldPrintHumanBatchPreview(batch.PrintOption)
                && cliReporter?.UsesLiveRendering != true)
            {
                PrintTrackBatchResolved(batch);
            }
        };

        try
        {
            Guid workflowId = Guid.NewGuid();
            await backend.SubscribeWorkflowAsync(workflowId, cts.Token);
            using var terminalUpdateObserver = new WorkflowTerminalUpdateObserver(backend, workflowId);

            var options = BuildRemoteSubmissionOptions(invocation, cliSettings) with { WorkflowId = workflowId };
            var request = BuildRemoteExtractRequest(rootSettings, options);

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

            ConsoleInputManager.Reporter = cliReporter;
            ConsoleInputManager.OnCancelRequested = async () =>
            {
                lock (Printing.ConsoleLock)
                {
                    Printing.WriteLine(force: true);
                    Printing.Write("Cancel job ID or current workflow? id/[A]ll/n: ", ConsoleColor.Yellow, force: true);
                }

                var result = ConsoleInputManager.ReadCancelPromptResult();

                if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                    return;

                if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
                {
                    SockseekLog.Info("Cancelling workflow...");
                    Printing.WriteLine("Cancelling workflow...", ConsoleColor.Gray, force: true);
                    await backend.CancelWorkflowAsync(submission.WorkflowId, cts.Token);
                    return;
                }

                if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
                {
                    if (await backend.CancelJobByDisplayIdAsync(id, submission.WorkflowId, cts.Token))
                        SockseekLog.Info($"Cancelling job [{id}]...");
                    else
                        SockseekLog.Error($"Job ID [{id}] not found.");
                }
                else
                {
                    SockseekLog.Error($"Invalid input '{result.Input}'.");
                }
            };

            ConsoleInputManager.OnNextCandidateRequested = async () =>
            {
                lock (Printing.ConsoleLock)
                {
                    Printing.WriteLine(force: true);
                    Printing.Write("Try next candidate for job ID or n: ", ConsoleColor.Yellow, force: true);
                }

                var result = ConsoleInputManager.ReadCancelPromptResult();

                if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                    return;

                if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
                {
                    if (await backend.TryNextCandidateByDisplayIdAsync(id, submission.WorkflowId, cts.Token))
                        SockseekLog.Info($"Trying next candidate for job [{id}]...");
                    else
                        SockseekLog.Error($"Job ID [{id}] not found or has no active download.");
                }
                else
                {
                    SockseekLog.Error($"Invalid input '{result.Input}'.");
                }
            };

            ConsoleInputManager.OnInfoRequested = async () =>
            {
                lock (Printing.ConsoleLock)
                {
                    Printing.WriteLine(force: true);
                    Printing.Write("Info for job ID (blank to cancel): ", ConsoleColor.Yellow, force: true);
                }
                var id = ConsoleInputManager.ReadJobIdInput();
                if (id == null) return;

                while (true)
                {
                    int printStart = Console.IsOutputRedirected ? -1 : Console.CursorTop;

                    var detail = await backend.GetJobDetailByDisplayIdAsync(id.Value, submission.WorkflowId, cts.Token);
                    if (detail == null)
                        SockseekLog.Error($"Job ID [{id}] not found.");
                    else
                        JobInfoPrinter.Print(detail);

                    lock (Printing.ConsoleLock)
                        Printing.Write("Info for job ID (r to refresh, blank to exit): ", ConsoleColor.Yellow, force: true);

                    var result = ConsoleInputManager.ReadJobIdOrRefreshResult();

                    if (result.Action == ConsoleInputManager.CancelPromptAction.Refresh)
                    {
                        if (printStart >= 0)
                        {
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
                    }
                    else if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId.HasValue)
                    {
                        id = result.JobId.Value;
                    }
                    else
                    {
                        return;
                    }
                }
            };

            _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

            if (interactiveCoordinator != null)
                await interactiveCoordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token);
            else
                await WaitForRemoteWorkflowAsync(backend, submission.WorkflowId, cts.Token);

            await terminalUpdateObserver.WaitForTerminalUpdateAsync(cts.Token);

            if (!rootSettings.DoNotDownload)
                await PrintRemoteCompleteAsync(backend, submission.WorkflowId, cts.Token);

            var exitCode = LogCliSessionExit(await DetermineRemoteExitCodeAsync(backend, submission.WorkflowId, cts.Token), remoteSettings);
            cliReporter?.Stop();
            cliReporter = null;

            if (rootSettings.PrintResults || rootSettings.PrintJobs)
                await PrintRemoteRequestedOutputAsync(backend, submission.WorkflowId, rootSettings, cts.Token);

            return exitCode;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return LogCliSessionExit(CliExitCode.Cancelled, remoteSettings);
        }
        catch (SockseekApiRequestException ex)
        {
            if (cliReporter != null)
                cliReporter.ReportClientError(ex.Message);
            else
                SockseekLog.Error(ex.Message);

            return LogCliSessionExit(CliExitCode.WorkFailed, remoteSettings);
        }
        catch (Exception ex)
        {
            if (cliReporter != null)
                cliReporter.ReportClientError($"Unhandled remote CLI error: {SockseekLog.ExceptionSummary(ex)}");
            else
                SockseekLog.Fatal($"Unhandled remote CLI error: {SockseekLog.ExceptionSummary(ex)}");

            return LogCliSessionExit(CliExitCode.WorkFailed, remoteSettings);
        }
        finally
        {
            await cts.CancelAsync();
            cliReporter?.Stop();
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

    private sealed class WorkflowTerminalUpdateObserver : IDisposable
    {
        private static readonly TimeSpan TerminalUpdateDrainTimeout = TimeSpan.FromSeconds(2);

        private readonly ICliBackend backend;
        private readonly Guid workflowId;
        private readonly TaskCompletionSource terminalUpdateSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkflowTerminalUpdateObserver(ICliBackend backend, Guid workflowId)
        {
            this.backend = backend;
            this.workflowId = workflowId;
            backend.WorkflowUpdated += OnWorkflowUpdated;
        }

        public async Task WaitForTerminalUpdateAsync(CancellationToken ct)
        {
            if (terminalUpdateSeen.Task.IsCompleted)
                return;

            try
            {
                await terminalUpdateSeen.Task.WaitAsync(TerminalUpdateDrainTimeout, ct);
            }
            catch (TimeoutException)
            {
                // The HTTP snapshot is authoritative for completion. This wait is only to give
                // the remote event stream a chance to deliver terminal activity before the CLI exits.
            }
        }

        public void Dispose()
            => backend.WorkflowUpdated -= OnWorkflowUpdated;

        private void OnWorkflowUpdated(WorkflowClientUpdate update)
        {
            if (update.WorkflowId != workflowId || update.IsStale)
                return;

            if (update.Workflow?.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed)
                terminalUpdateSeen.TrySetResult();
        }
    }

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
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return CliExitCode.WorkFailed;

        var summaries = (await backend.GetJobsAsync(
                new JobQuery(null, null, null, workflowId, IncludeAll: true),
                ct))
            .OrderBy(job => job.DisplayId)
            .ToArray();

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

        if (fails > 0 || workflow.Summary.State == ServerWorkflowState.Failed)
            return CliExitCode.WorkFailed;

        return CliExitCode.Success;
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

        if (batch.PendingCount > 0)
        {
            string notFoundLastTime = batch.NotFoundCount > 0 ? $"{batch.NotFoundCount} not found" : "";
            string alreadyExist = batch.ExistingCount > 0 ? $"{batch.ExistingCount} already exist" : "";
            notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
            string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
            bool allSkipped = batch.ExistingCount + batch.NotFoundCount > batch.PendingCount;
            var message = $"Downloading {batch.PendingCount} tracks{skippedTracks}{(allSkipped ? '.' : ':')}";
            if (batch.IsNormal)
                SockseekLog.Info(message);
            else
                WriteBatchJobLog(batch.Summary, message);

            var preview = batch.Pending.Select(ToSongJob).ToList();
            if (preview.Count > 0)
            {
                Printing.PrintTracks(preview, int.MaxValue, fullInfo: false);
                if (batch.PendingCount > preview.Count)
                    Printing.WriteLine($"  ... and {batch.PendingCount - preview.Count} more");
            }
        }

        // For aggregate batches print the skipped/not-found songs so the user can see what was skipped.
        if (!batch.IsNormal)
        {
            if (batch.ExistingCount > 0)
            {
                WriteBatchJobLog(batch.Summary, $"{batch.ExistingCount} tracks already exist:");
                Printing.PrintTracks([.. batch.Existing.Select(ToSongJob)], int.MaxValue, fullInfo: false);
            }
            if (batch.NotFoundCount > 0)
            {
                WriteBatchJobLog(batch.Summary, $"{batch.NotFoundCount} tracks were not found in a prior run:");
                Printing.PrintTracks([.. batch.NotFound.Select(ToSongJob)], int.MaxValue, fullInfo: false);
            }
        }
    }

    private static void WriteBatchJobLog(JobSummaryDto summary, string message)
    {
        var line = new TerminalLogLine(
            TerminalLogKind.Status,
            summary.JobId.ToString(),
            summary.DisplayId,
            BatchJobTypeLabel(summary.Kind),
            message);
        SockseekLog.Write(new SockseekLog.StructuredLogEntry(
            LogLevel.Information,
            SockseekLog.Categories.Jobs,
            CliLogStyle.FormatTerminalLogText(line),
            Context: new CliOutputEvent.JobLog(line)));
    }

    private static string BatchJobTypeLabel(ServerJobKind kind)
    {
        if (kind == ServerJobKind.RetrieveFolder)
            return "Retrieve Folder";
        if (kind == ServerJobKind.JobList)
            return "Job List";
        if (kind == ServerJobKind.AlbumAggregate)
            return "Album Aggregate";

        string kindText = kind.ToWireString();
        return $"{char.ToUpperInvariant(kindText[0])}{kindText[1..]}";
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

        var summaries = workflow.Jobs
            .OrderBy(job => job.DisplayId)
            .ToArray();
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

        Printing.PrintComplete(successes, fails, skipped);
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

    private static IEnumerable<SongJobPayloadDto> ResolvedAlbumSongs(AlbumJobPayloadDto album)
        => album.Tracks?.Where(song => Utils.IsMusicFile(song.ResolvedFilename ?? "")) ?? [];

    internal static async Task PrintRemoteRequestedOutputAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
    {
        var queue = await BuildRemotePrintQueueAsync(backend, workflowId, settings, ct);
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
        CancellationToken ct)
    {
        var queue = new JobList("remote workflow")
        {
            Config = SettingsCloner.Clone(settings),
        };

        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return queue;

        var details = new Dictionary<Guid, JobDetailDto>();
        foreach (var summary in workflow.Jobs)
            await LoadRemoteJobTreeAsync(backend, summary.JobId, details, ct);

        var roots = details.Values
            .Where(detail => workflow.Jobs.Any(root => root.JobId == detail.Summary.JobId))
            .OrderBy(detail => detail.Summary.DisplayId)
            .ToList();

        var visited = new HashSet<Guid>();
        foreach (var root in roots)
        {
            var job = await ToRemotePrintJobAsync(backend, root, details, settings, visited, ct);
            if (job != null)
                queue.Add(job);
        }

        return queue;
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
                    ? await ToAggregateResultsJobAsync(backend, aggregate, detail.Children, details, ct)
                    : ToAggregateJob(aggregate),

            AlbumAggregateJobPayloadDto albumAggregate
                => effectiveSettings.PrintResults
                    ? await ToAlbumAggregateResultsJobAsync(backend, albumAggregate, detail.Children, details, ct)
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
        var job = new ExtractJob(extract.Input, ParseRemoteInputType(extract.InputType))
        {
            AutoProcessResult = extract.AutoProcessResult,
        };
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
        JobSummaryDto? summary,
        CancellationToken ct)
    {
        var folders = await backend.GetFolderResultsAsync(albumJobId, includeFiles: true, ct);
        var job = ToAlbumJob(album, summary);
        job.Results = folders?.Items.Select(ToAlbumFolder).ToList() ?? [];
        return job;
    }

    private static async Task<Job?> ToAggregateResultsJobAsync(
        ICliBackend backend,
        AggregateJobPayloadDto aggregate,
        IReadOnlyList<JobSummaryDto> children,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        CancellationToken ct)
    {
        var job = new AggregateJob(ToSongQuery(aggregate.Query));
        foreach (var summary in children.Where(child => child.Kind == ServerJobKind.Song).OrderBy(child => child.DisplayId))
        {
            if (!details.TryGetValue(summary.JobId, out var detail))
                detail = await backend.GetJobDetailAsync(summary.JobId, ct);

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

    private static async Task<Job?> ToAlbumAggregateResultsJobAsync(
        ICliBackend backend,
        AlbumAggregateJobPayloadDto aggregate,
        IReadOnlyList<JobSummaryDto> children,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        CancellationToken ct)
    {
        var job = new AlbumAggregateJob(ToAlbumQuery(aggregate.Query));
        foreach (var summary in children.Where(child => child.Kind == ServerJobKind.Album).OrderBy(child => child.DisplayId))
        {
            if (!details.TryGetValue(summary.JobId, out var detail))
                detail = await backend.GetJobDetailAsync(summary.JobId, ct);

            if (detail?.Payload is not AlbumJobPayloadDto payload)
                continue;

            if (await ToAlbumResultsJobAsync(backend, summary.JobId, payload, summary, ct) is AlbumJob album)
                job.Albums.Add(album);
        }

        return job;
    }

    private static SearchJob ToSearchJob(SearchJobPayloadDto search, JobSummaryDto summary)
    {
        SearchJob job;
        if (search.DefaultFolderProjection != null)
        {
            job = new SearchJob(ToAlbumQuery(search.DefaultFolderProjection.AlbumQuery));
        }
        else if (search.DefaultFileProjection?.SongQuery != null)
        {
            job = new SearchJob(
                ToSongQuery(search.DefaultFileProjection.SongQuery),
                search.DefaultFileProjection.IncludeFullResults);
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
        var target = folder.Folder != null
            ? ToAlbumFolder(folder.Folder)
            : new AlbumFolder(folder.Username, folder.FolderPath, []);
        var job = new RetrieveFolderJob(target)
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
        if (summary.PrintOption != PrintOption.None)
            settings.PrintOption = summary.PrintOption;
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

        if (detail.Payload is ExtractJobPayloadDto { ResultJobId: Guid resultJobId })
            await LoadRemoteJobTreeAsync(backend, resultJobId, details, ct);

        foreach (var child in detail.Children)
        {
            if (detail.Summary.Kind == ServerJobKind.Album
                && child.Kind == ServerJobKind.Song)
                continue;

            await LoadRemoteJobTreeAsync(backend, child.JobId, details, ct);
        }
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
        => BuildRemoteSubmissionOptions(ConfigManager.ParseInvocation(args), cliSettings);

    private static SubmissionOptionsDto BuildRemoteSubmissionOptions(ParsedInvocation invocation, CliSettings cliSettings)
        => new(
            ProfileNames: SplitProfileNames(invocation.ProfileName),
            ProfileContext: new Dictionary<string, bool>
            {
                ["interactive"] = cliSettings.InteractiveMode,
                ["progress-json"] = cliSettings.ProgressJson,
                ["no-progress"] = cliSettings.NoProgress,
            },
            DownloadSettings: invocation.DownloadSettings);

    private static SubmitExtractJobRequestDto BuildRemoteExtractRequest(DownloadSettings rootSettings, SubmissionOptionsDto options)
        => new(
            rootSettings.Extraction.Input!,
            rootSettings.Extraction.InputType.ToString(),
            Options: options);

    private static string[]? SplitProfileNames(string? names)
        => string.IsNullOrWhiteSpace(names)
            ? null
            : names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void LogCliSessionStart(RemoteSettings remoteSettings)
    {
        if (remoteSettings.IsEnabled)
        {
            SockseekLog.Cli.Info($"Starting CLI session in remote mode: {remoteSettings.ServerUrl}");
            return;
        }

        SockseekLog.Cli.Info("Starting CLI session in local mode");
    }

    private static CliExitCode LogCliSessionExit(CliExitCode exitCode, RemoteSettings? remoteSettings = null)
    {
        if (remoteSettings?.IsEnabled == true)
        {
            SockseekLog.Cli.Debug($"Exiting CLI session in remote mode with code {(int)exitCode} ({exitCode})");
            return exitCode;
        }

        SockseekLog.Cli.Debug($"Exiting CLI session in local mode with code {(int)exitCode} ({exitCode})");
        return exitCode;
    }

    private static async Task RunDaemonAsync(
        string[] args,
        ParsedInvocation invocation,
        ConfigFile configFile,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        DaemonSettings daemonSettings)
    {
        var url = BuildDaemonListenUrl(daemonSettings);
        EnsureDaemonEndpointAvailable(daemonSettings);
        var options = new ServerOptions
        {
            Engine = SettingsCloner.Clone(engineSettings),
            DefaultDownload = SettingsCloner.Clone(rootSettings),
            LaunchDownloadSettings = invocation.DownloadSettings,
            Profiles = ConfigManager.CreateProfileCatalog(configFile),
            ConfigDir = configFile.ConfigDir,
        };

        var app = ServerHost.Build(args, options, url);
        SockseekLog.Info($"Starting Sockseek daemon on {url}", categoryName: SockseekLog.Categories.Daemon);
        if (IsDaemonListenAddressNetworkExposed(daemonSettings))
        {
            SockseekLog.Warn(
                "Sockseek daemon is listening on all network interfaces. The API is unauthenticated; expose it only on trusted networks or behind your own access control.",
                categoryName: SockseekLog.Categories.Daemon);
        }
        SockseekLog.Info("Press Ctrl+C to stop.", categoryName: SockseekLog.Categories.Daemon);
        try
        {
            await app.RunAsync();
        }
        finally
        {
            SockseekLog.Info($"Exiting Sockseek daemon on {url}", categoryName: SockseekLog.Categories.Daemon);
        }
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
                $"Cannot start Sockseek daemon on {BuildDaemonListenUrl(daemonSettings)}: {SockseekLog.ExceptionSummary(ex)}",
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
            DownloadPath = song.DownloadPath,
            Candidates = song.Candidates?.Select(ToFileCandidate).ToList(),
            DownloadSource = ToSongDownloadSource(song.DownloadSource),
        };

        ApplyJobOutcome(job, song.LifecycleState, song.ActivityPhase, song.TerminalOutcome, song.SkipReason, song.FailureReason, song.FailureMessage, song.CancellationSource);

        if (summary != null)
        {
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
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
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);

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

    private static AlbumFolder ToAlbumFolder(AlbumFolderDto folder)
        => new(
            folder.Username,
            folder.FolderPath,
            folder.Files?.Select(ToAlbumFile).ToList() ?? [])
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };

    private static AlbumFile ToAlbumFile(FileCandidateDto file)
    {
        var candidate = ToFileCandidate(file);
        return AlbumFile.WithLazyQuery(
            () => Searcher.InferSongQuery(candidate.Filename, new SongQuery()),
            candidate);
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
