using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soulseek;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Transfers.Downloads.Commands;
using Sockseek.Core.Transfers.Downloads.Queueing;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Transfers.Downloads.ManualSelection;
using Sockseek.Core.Transfers.Downloads.Reporting;
using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Transfers.Downloads.Skipping;
using Sockseek.Core.Transfers.Downloads.SourceMutations;
using Sockseek.Core.Transfers.Downloads.State;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Events;

using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Sockseek.Core;

public class DownloadEngine : IDisposable, IAsyncDisposable
{
    private readonly EngineSettings engineSettings;
    private readonly SoulseekClientManager _clientManager;
    private readonly IJobSettingsResolver _jobSettingsResolver;
    private readonly ISongDownloadFallback _songDownloadFallback;
    private readonly StaleDownloadCoordinator _staleDownloadCoordinator;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<DownloadEngine> logger;
    private readonly string engineId = Guid.NewGuid().ToString("N")[..12];
    private readonly bool retireTerminalWorkflows;

    internal bool AutomaticStaleChecksEnabled { get; set; } = true;

    public DownloadEvents Events { get; }
    public SearchEvents SearchEvents { get; }

    public JobList Queue { get; } = new();

    private readonly DownloadJobContextStore _contexts = new();
    private readonly SourceMutationCoordinator _sourceMutations;
    private readonly DownloadJobTracker _jobs;
    private readonly DownloadCommandTargetResolver _commandTargets;
    private readonly AutoProfileWorkflowReporter _autoProfiles;
    private readonly ManualSelectionCoordinator _manualSelections;
    private readonly SkipEvaluationCoordinator _skipEvaluation;
    private readonly DownloadExecutionContext _executionContext;
    private readonly JobOrchestrator _orchestrator;
    private readonly WorkflowLifetimeCoordinator? _workflowLifetime;
    private readonly Lock manualCancellationGate = new();
    private readonly HashSet<Task> manualCancellations = [];
    private readonly Lock lifetimeGate = new();
    private Task? runTask;
    private Task? disposeTask;

    public Task WaitForSubmissionAsync(Guid submissionId, CancellationToken cancellationToken)
        => _workflowLifetime?.WaitForSubmissionAsync(submissionId, cancellationToken) ?? Task.CompletedTask;

    // Called only after execution and its pending writes have settled. Other
    // submissions may still use the workflow; their execution state stays intact.
    public void ForgetSubmission(Guid submissionId)
    {
        var ids = _jobs.Jobs.Where(job => job.SubmissionId == submissionId).Select(job => job.Id).ToHashSet();
        Queue.RemoveJobs(ids);
        _manualSelections.Retire(ids);
        _jobs.Retire(ids);
    }

    public Job? GetJob(Guid id) => _jobs.GetJob(id);
    public Job? GetJob(int displayId) => _jobs.GetJob(displayId);
    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => _jobs.GetJobsByWorkflow(workflowId);

    public bool TryCancelTransfer(Guid transferId)
    {
        if (!_activeDownloads.TryGet(transferId, out var download))
            return false;
        download.Cts.Cancel();
        return true;
    }

    public bool TryNextCandidate(Guid jobId)
    {
        var job = _commandTargets.Resolve(jobId);
        if (job == null) return false;

        var activeDownloads = _commandTargets.ActiveDownloadsFor(job);

        if (activeDownloads.Count > 0)
        {
            Events.RaiseJobMessage(
                job,
                LogLevel.Information,
                null,
                $"trying next candidate; cancelling {activeDownloads.Count} active download{(activeDownloads.Count == 1 ? "" : "s")}");
            foreach (var ad in activeDownloads)
            {
                ad.IsManuallySkipped = true;
                ad.Cts.Cancel();
            }
            return true;
        }
        return false;
    }

    public bool TryNextCandidateByDisplayId(int displayId, Guid? workflowId = null)
    {
        var job = _commandTargets.ResolveDisplayId(displayId, workflowId);
        return job != null && TryNextCandidate(job.Id);
    }

    // ── public state (read by search and transfer services) ──────────────────

    public ISoulseekClient? Client => _clientManager.Client;
    public SoulseekClientStates ClientState => _clientManager.State;
    public bool IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;

    // Session state
    private readonly ActiveDownloadTracker _activeDownloads = new();
    private readonly UserSuccessTracker _userSuccesses = new();
    private readonly OutputFinalizer _outputFinalizer;
    public ConcurrentDictionary<string, int> GetUserSuccessCounts(Guid workflowId)
        => _userSuccesses.GetCounts(workflowId);

    // ── concurrency semaphores ────────────────────────────────────────────────

    // Limits simultaneous extractor runs to avoid API rate limits.
    // Search concurrency is handled inside Searcher (concurrencySemaphore).
    private readonly DownloadRunScope _runtime;

    // ── job channel ──────────────────────────────────────────────────────────

    private readonly DownloadJobQueue _jobQueue = new();

    /// <summary>Enqueues a new root job for processing. Call <see cref="CompleteEnqueue"/> when done adding jobs.</summary>
    public void Enqueue(
        Job job,
        DownloadSettings settings,
        Guid? sourceJobId = null,
        bool settingsAreFinal = false)
    {
        if (sourceJobId is Guid sourceId)
            _jobs.AssociateSource(job.Id, sourceId);

        _jobQueue.Enqueue(job, settings, _workflowLifetime?.QueueRoot(job), settingsAreFinal);
    }

    /// <summary>Resumes an existing job without re-parenting it or replacing its prepared context.</summary>
    public void Resume(Job job)
        => _jobQueue.Resume(job, _workflowLifetime?.QueueRoot(job));

    /// <summary>Applies one manual file choice and its optional settings before resuming.</summary>
    public bool TryStartManualSongSelection(
        Guid sourceJobId,
        FileCandidate candidate,
        Action<SongJob>? configureSelection,
        out SongJob? selectedJob)
        => _manualSelections.TryStartSong(sourceJobId, candidate, configureSelection, out selectedJob);

    /// <summary>
    /// Applies a manual album-folder selection to an existing selection job and resumes it
    /// without creating a follow-up job. New selection settings rebuild the
    /// settings-dependent context before resuming; ordinary choices reuse it.
    /// </summary>
    public bool TryStartManualAlbumSelection(
        Guid sourceJobId,
        AlbumFolder selectedFolder,
        AlbumQuery? albumQuery,
        Action<AlbumJob>? configureSelection,
        out AlbumJob? selectedJob)
        => _manualSelections.TryStartAlbum(sourceJobId, selectedFolder, albumQuery, configureSelection, out selectedJob);

    /// <summary>Completes an AwaitingSelection job through the engine so terminal side effects stay centralized.</summary>
    public Task<bool> CompleteManualSelectionAsync(Guid jobId)
        => FinishManualSelectionAsync(jobId, skip: false);

    /// <summary>Marks an AwaitingSelection job as explicitly skipped by the user.</summary>
    public Task<bool> SkipManualSelectionAsync(Guid jobId)
        => FinishManualSelectionAsync(jobId, skip: true);

    private async Task<bool> FinishManualSelectionAsync(Guid jobId, bool skip)
    {
        var job = GetJob(jobId);
        if (job is null) return false;
        var lifetime = _workflowLifetime?.QueueRoot(job);
        try
        {
            return skip ? await _manualSelections.SkipAsync(jobId) : await _manualSelections.CompleteAsync(jobId);
        }
        finally
        {
            if (lifetime != null) _workflowLifetime!.RootCompleted(lifetime);
        }
    }

    public async Task<RetrieveFolderJob> ProcessFolderRetrieval(
        AlbumFolder folder,
        Job parentJob,
        string? statusMessage = null,
        bool consumeJobSlot = true)
        => await _orchestrator.ProcessFolderRetrieval(folder, parentJob, statusMessage, consumeJobSlot);

    /// <summary>Signals that no more jobs will be enqueued. <see cref="RunAsync"/> will drain and exit.</summary>
    public void CompleteEnqueue() => _jobQueue.Complete();

    // ── cancellation ─────────────────────────────────────────────────────────

    public void Cancel()
    {
        foreach (var job in _jobs.Jobs)
            if (!job.IsTerminal)
                if (!_manualSelections.TryCancel(job.Id, JobCancellationSource.UserRequestedAllJobs))
                    job.MarkCancellationSource(JobCancellationSource.UserRequestedAllJobs);

        _runtime.Cancel();
    }

    public int CancelWorkflow(Guid workflowId)
    {
        var jobs = _commandTargets.ResolveWorkflow(workflowId);
        int cancelled = 0;

        foreach (var job in jobs)
        {
            var cts = job.Cts;
            if (cts == null || cts.IsCancellationRequested)
                continue;

            CancelCommandTarget(job, JobCancellationSource.UserRequestedWorkflow);
            cancelled++;
        }

        return cancelled;
    }

    /// <summary>
    /// Cancels every currently cancellable job without stopping the engine runtime.
    /// Daemon callers use this instead of <see cref="Cancel"/> so the engine remains
    /// available for later submissions.
    /// </summary>
    public int CancelAllJobs()
    {
        int cancelled = 0;
        foreach (var job in _jobs.Jobs)
        {
            var cts = job.Cts;
            if (job.IsTerminal || cts == null || cts.IsCancellationRequested)
                continue;

            CancelCommandTarget(job, JobCancellationSource.UserRequestedAllJobs);
            cancelled++;
        }

        return cancelled;
    }

    public bool CancelJob(Guid jobId)
        => CancelCommandTarget(_commandTargets.Resolve(jobId), JobCancellationSource.UserRequestedJob);

    public bool CancelJobByDisplayId(int displayId, Guid? workflowId = null)
        => CancelCommandTarget(_commandTargets.ResolveDisplayId(displayId, workflowId), JobCancellationSource.UserRequestedJob);

    private bool CancelCommandTarget(Job? job, JobCancellationSource source)
    {
        if (job == null)
            return false;

        if (!_manualSelections.TryCancel(job.Id, source))
            job.Cancel(source);
        return true;
    }

    private void CancelManualSelection(Job job, JobCancellationSource source)
    {
        // Reserve both owners before signalling cancellation. Terminal work
        // must drain with the engine without waiting for a download slot.
        var lifetime = _workflowLifetime?.QueueRoot(job);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (manualCancellationGate) manualCancellations.Add(completion.Task);
        _ = FinishManualCancellationAsync(job, source, lifetime, completion);
    }

    private async Task FinishManualCancellationAsync(Job job, JobCancellationSource source,
        WorkflowLifetimeCoordinator.WorkflowRootLease? lifetime, TaskCompletionSource completion)
    {
        try
        {
            try { job.Cancel(source); }
            finally
            {
                // Signal the token immediately; terminal side effects run
                // asynchronously under the same per-job execution gate, even
                // if a registered cancellation callback throws.
                await Task.Yield();
                await ProcessPreparedRootJob(new PreparedRootJob(job, lifetime, null,
                    IsResume: false, CompletionOutcome: JobOutcome.Cancelled(source)));
            }
        }
        catch (Exception exception)
        {
            DownloadLogMessages.ComponentFailed(logger, exception, "manual-selection-cancellation", job.Id);
        }
        finally
        {
            lock (manualCancellationGate)
            {
                manualCancellations.Remove(completion.Task);
                completion.TrySetResult();
            }
        }
    }

    // ── construction ─────────────────────────────────────────────────────────

    public DownloadEngine(
        EngineSettings settings,
        SoulseekClientManager clientManager,
        IJobSettingsResolver? jobSettingsResolver = null,
        ISongDownloadFallback? songDownloadFallback = null,
        TimeProvider? timeProvider = null,
        IPeerDirectorySource? directorySource = null,
        ILoggerFactory? loggerFactory = null,
        ISensitiveOutput? sensitiveOutput = null,
        bool retireTerminalWorkflows = false)
    {
        engineSettings = settings;
        this.retireTerminalWorkflows = retireTerminalWorkflows;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<DownloadEngine>();
        Events = new DownloadEvents(
            timeProvider,
            this.loggerFactory.CreateLogger<DownloadEvents>());
        Events.JobRegistered += change => _userSuccesses.StartWorkflow(change.Job.WorkflowId);
        Events.TransferFailed += change =>
        {
            if (change.Transfer.Source == TransferSnapshotSource.SoulseekPeer
                && (change.Reason is TransferFailureReason.PeerFailure or TransferFailureReason.Stale)
                && change.Transfer.Username is { } username)
                _userSuccesses.RecordFailure(change.Job.WorkflowId, username);
        };
        SearchEvents = new SearchEvents();
        _sourceMutations = new SourceMutationCoordinator(
            Events,
            this.loggerFactory.CreateLogger<SourceMutationCoordinator>());
        _clientManager = clientManager;
        _jobSettingsResolver = jobSettingsResolver ?? DefaultJobSettingsResolver.Instance;
        _songDownloadFallback = songDownloadFallback ?? SongDownloadFallback.Default;
        _staleDownloadCoordinator = new StaleDownloadCoordinator(
            _activeDownloads,
            timeProvider,
            this.loggerFactory.CreateLogger<StaleDownloadCoordinator>());
        _jobs = new DownloadJobTracker(Events);
        _commandTargets = new DownloadCommandTargetResolver(_jobs, _activeDownloads);
        _autoProfiles = new AutoProfileWorkflowReporter(Events);
        // These collaborators call each other only after construction; the delegates break the cycle explicitly.
        _skipEvaluation = new SkipEvaluationCoordinator(
            job => _executionContext!.Ctx(job),
            job => _executionContext!.RaiseBuildingMusicDirectoryIndex(job),
            this.loggerFactory.CreateLogger<SkipEvaluationCoordinator>());
        if (settings.ConcurrentJobs <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings.ConcurrentJobs), "ConcurrentJobs must be greater than zero.");
        _runtime = new DownloadRunScope(
            settings,
            _clientManager,
            _activeDownloads,
            _userSuccesses,
            Events,
            SearchEvents,
            _staleDownloadCoordinator,
            timeProvider,
            directorySource,
            this.loggerFactory);
        _outputFinalizer = new OutputFinalizer(
            _runtime.Files,
            Events,
            this.loggerFactory.CreateLogger<OutputFinalizer>());
        _executionContext = new DownloadExecutionContext(
            engineSettings,
            _clientManager,
            _jobSettingsResolver,
            _songDownloadFallback,
            _staleDownloadCoordinator,
            Events,
            Queue,
            _userSuccesses,
            _outputFinalizer,
            _contexts,
            _sourceMutations,
            _jobs,
            _autoProfiles,
            _skipEvaluation,
            _runtime,
            this.loggerFactory,
            sensitiveOutput ?? NullSensitiveOutput.Instance);
        _orchestrator = new JobOrchestrator(
            _executionContext,
            child => _manualSelections!.TryFinalizeClosedGroupSelectionForChildAsync(child));
        _manualSelections = new ManualSelectionCoordinator(
            GetJob,
            _contexts,
            _executionContext.RegisterJob,
            _executionContext.ObservePreparedAutoProfiles,
            Resume,
            CancelManualSelection,
            _orchestrator.FlushManualSelectionTerminalEffectsAsync,
            JobOrchestrator.IsSuccessfulTerminal,
            PrepareManualSelectionSettings);
        if (retireTerminalWorkflows)
        {
            _workflowLifetime = new WorkflowLifetimeCoordinator(
                _jobs.GetJobsByWorkflow,
                _manualSelections.HasResumableState,
                workflowId => (_jobSettingsResolver as IWorkflowSettingsLifetime)
                    ?.CaptureWorkflowVersion(workflowId) ?? 0,
                RetireWorkflow);
        }
    }

    private void PrepareManualSelectionSettings(Job job)
    {
        JobContext previous = _contexts.Get(job);
        // Preserve workflow scoping and reuse editors only when their actual
        // paths still match. Skippers belong to the old settings and must not
        // be cached under the newly selected output directory.
        var inherited = new JobContext
        {
            OutputScope = previous.OutputScope,
            IndexEditor = previous.IndexEditor,
            PlaylistEditor = previous.PlaylistEditor,
        };
        foreach (var (id, context) in JobPreparer.PrepareSubtree(
            job, job.PlannedSettings!.ReadSettings(), _jobSettingsResolver,
            explicitOwnerList: previous.PreparationOwner,
            parentCtx: inherited, rootSettingsAreFinal: true))
        {
            context.PreprocessTracks = previous.PreprocessTracks;
            _contexts.Set(id, job.WorkflowId, context);
        }
        _executionContext.ObservePreparedAutoProfiles(job);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetimeGate)
            return new ValueTask(disposeTask ??= DisposeCoreAsync(runTask));
    }

    private async Task DisposeCoreAsync(Task? running)
    {
        try
        {
            await _runtime.CancelAsync().ConfigureAwait(false);
            // The caller of RunAsync owns its outcome. Disposal must join it
            // even when it failed, without throwing the same failure again.
            if (running is not null)
                await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _userSuccesses.Clear();
            GC.SuppressFinalize(this);
        }
    }

    internal int RunStaleDownloadCheckForTesting() => _staleDownloadCoordinator.CancelStaleDownloads();

    internal DownloadEngineRetainedStateCounts RetainedStateCounts
    {
        get
        {
            var execution = _executionContext.RetainedStateCounts;
            var events = Events.RetainedStateCounts;
            return new DownloadEngineRetainedStateCounts(
                Queue.Count,
                _jobs.Count,
                _contexts.Count,
                _activeDownloads.Count,
                _manualSelections.RetainedStateCount,
                _workflowLifetime?.RetainedGenerationCount ?? 0,
                execution.WorkflowDiagnostics,
                execution.PendingTerminalTransfers,
                execution.AutoProfileWorkflows,
                events.Jobs,
                events.Transfers,
                events.Attempts,
                events.Gates,
                events.TerminalTransfers,
                _runtime.Files.ActiveCachedDownloadCount,
                _sourceMutations.RetainedWorkflowCount);
        }
    }

    // ── top-level entry point ─────────────────────────────────────────────────

    public Task RunAsync(CancellationToken ct)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null, this);
            if (runTask is not null)
                throw new InvalidOperationException("The download engine can only run once.");
            return runTask = RunCoreAsync(ct);
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        // One lifetime token stops preparation/channel reads as well as active
        // roots. Cancelling only the input leaves roots running indefinitely.
        using var cancellation = ct.UnsafeRegister(
            static state => ((DownloadRunScope)state!).Cancel(), _runtime);
        DownloadLogMessages.EngineStage(logger, engineId, "reading-job-channel");
        try
        {
            await BoundedAsync.ForEachAsync(
                ReadPreparedRootJobs(_runtime.Token),
                _runtime.ConcurrentSchedulingLimit,
                ProcessPreparedRootJob,
                _runtime.Token).ConfigureAwait(false);
        }
        finally
        {
            await DrainManualCancellationsAsync().ConfigureAwait(false);
            await _runtime.CancelAsync().ConfigureAwait(false);
        }
        DownloadLogMessages.EngineStage(logger, engineId, "root-jobs-completed");

        CleanupEmptyStagingDirectories();

        if (Queue.Jobs.Any(ContainsDownloadableJob))
            Events.RaiseEngineCompleted(Queue);

        DownloadLogMessages.EngineStage(logger, engineId, "stopped");
    }

    private async Task DrainManualCancellationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (manualCancellationGate)
            {
                if (manualCancellations.Count == 0) return;
                pending = manualCancellations.ToArray();
                manualCancellations.Clear();
            }
            await Task.WhenAll(pending);
        }
    }

    private async Task ProcessPreparedRootJob(PreparedRootJob prepared)
    {
        var executionGate = prepared.Job.ExecutionGate;
        await executionGate.WaitAsync();
        try
        {
            // A resume can wait behind the discovery execution that published
            // its picker. Check cancellation here, before replacing that run's
            // token, rather than capturing it when the queue is read.
            var completion = prepared.CompletionOutcome;
            if (prepared.IsResume
                && prepared.Job.CancellationSource != JobCancellationSource.None)
                completion = JobOutcome.Cancelled(prepared.Job.CancellationSource);

            if (prepared.PreparationFailure != null)
            {
                _executionContext.RegisterJob(prepared.Job, parent: null);
                FailActiveWorkflowJobs(prepared.Job, prepared.PreparationFailure);
                Events.RaiseJobExecutionCompleted(prepared.Job);
            }
            else if (completion is { } outcome)
            {
                if (!prepared.Job.IsTerminal)
                {
                    long startedAt = Stopwatch.GetTimestamp();
                    JobOutcomeCommitter.Commit(prepared.Job, outcome);
                    await _orchestrator.FlushManualSelectionTerminalEffectsAsync(prepared.Job);
                    var state = prepared.Job.StateSnapshot;
                    DownloadLogMessages.RootJobCompleted(logger, prepared.Job.Id,
                        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                        state.LifecycleState, state.TerminalOutcome, state.FailureReason);
                }
            }
            else
            {
                try
                {
                    await _orchestrator.ProcessRootJob(
                        prepared.Job,
                        emitAutoProfileFinalSummary: !retireTerminalWorkflows);
                }
                catch (Exception ex) when (retireTerminalWorkflows)
                {
                    FailActiveWorkflowJobs(prepared.Job, ex);
                }
            }
        }
        finally
        {
            try { await _executionContext.FlushOutputsAsync(prepared.Job); }
            finally
            {
                executionGate.Release();
                if (prepared.Lifetime != null)
                    _workflowLifetime!.RootCompleted(prepared.Lifetime);
            }
        }
    }

    private async IAsyncEnumerable<PreparedRootJob> ReadPreparedRootJobs(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var queuedJob in _jobQueue.ReadAllAsync(cancellationToken))
        {
            var rootJob = queuedJob.Job;
            var settings = queuedJob.Settings;
            if (queuedJob.Lifetime != null)
                await _workflowLifetime!.WaitUntilReadyAsync(queuedJob.Lifetime, cancellationToken);
            DownloadLogMessages.JobDecision(
                logger,
                rootJob.Id,
                queuedJob.IsResume ? "resume-dequeued" : "root-dequeued",
                null);

            Exception? preparationFailure = null;
            try
            {
                if (!queuedJob.IsResume)
                {
                    Queue.Add(rootJob);

                    foreach (var (id, ctx) in JobPreparer.PrepareSubtree(
                        rootJob,
                        settings!,
                        _jobSettingsResolver,
                        rootSettingsAreFinal: queuedJob.SettingsAreFinal))
                        _contexts.Set(id, rootJob.WorkflowId, ctx);

                    _executionContext.ObservePreparedAutoProfiles(rootJob);
                }
                else if (!_contexts.ContainsKey(rootJob.Id))
                {
                    throw new InvalidOperationException($"Cannot resume job {rootJob.DisplayId}: no prepared job context exists.");
                }

                if (ContainsLoginRequiredJob(rootJob))
                {
                    await _runtime.EnsureServicesInitializedAsync(cancellationToken, AutomaticStaleChecksEnabled);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (retireTerminalWorkflows)
            {
                preparationFailure = ex;
            }

            yield return new PreparedRootJob(rootJob, queuedJob.Lifetime, preparationFailure,
                queuedJob.IsResume, CompletionOutcome: null);
        }

        DownloadLogMessages.EngineStage(logger, engineId, "waiting-for-root-jobs");
    }

    private void RetireWorkflow(
        Guid workflowId,
        IReadOnlyList<Job> registeredJobs,
        long settingsVersion)
    {
        _executionContext.EmitAutoProfileFinalSummary(workflowId);
        Events.RaiseWorkflowRetired(workflowId, registeredJobs.Count);

        var registeredIds = registeredJobs.Select(job => job.Id).ToHashSet();
        Queue.RemoveJobs(registeredIds);
        var preparedIds = _contexts.RemoveWorkflow(workflowId);
        var allJobIds = registeredIds.Concat(preparedIds).ToHashSet();
        _executionContext.RetireWorkflow(workflowId, allJobIds);
        _manualSelections.Retire(registeredIds);
        _jobs.Retire(registeredIds);
        _userSuccesses.RetireWorkflow(workflowId);
        _runtime.Files.RetireWorkflow(workflowId);
        _sourceMutations.RetireWorkflow(workflowId);
        if (_jobSettingsResolver is IWorkflowSettingsLifetime settingsLifetime)
            settingsLifetime.RetireWorkflow(workflowId, allJobIds, settingsVersion);
    }

    private void FailActiveWorkflowJobs(Job rootJob, Exception exception)
    {
        DownloadLogMessages.ComponentFailed(logger, exception, "root-execution", rootJob.Id);
        string message = Diagnostics.ExceptionText.Summary(exception);
        string detail = Diagnostics.ExceptionText.Detail(exception);
        foreach (var job in _jobs.GetJobsByWorkflow(rootJob.WorkflowId))
        {
            if (!job.IsTerminal)
                JobOutcomeCommitter.Commit(
                    job,
                    JobOutcome.Failed(JobFailureReason.Other, message, detail));
        }
    }

    private sealed record PreparedRootJob(
        Job Job,
        WorkflowLifetimeCoordinator.WorkflowRootLease? Lifetime,
        Exception? PreparationFailure,
        bool IsResume,
        JobOutcome? CompletionOutcome);

    private void CleanupEmptyStagingDirectories()
    {
        var outputParents = Queue.AllJobs()
            .Select(job => job.Config?.Output.ParentDir)
            .Where(parent => !string.IsNullOrWhiteSpace(parent))
            .Select(parent => Path.GetFullPath(parent!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var parentDir in outputParents)
        {
            var stagingRoot = Path.Join(parentDir, OutputStaging.DirectoryName);
            if (!Directory.Exists(stagingRoot) || Utils.FileCountRecursive(stagingRoot) > 0)
                continue;

            try
            {
                Directory.Delete(stagingRoot, recursive: false);
            }
            catch (Exception ex)
            {
                DownloadLogMessages.CleanupFailed(
                    logger,
                    "staging-directory",
                    ex.GetType().Name);
            }
        }
    }

    private static bool ContainsDownloadableJob(Job job)
        => job switch
        {
            ExtractJob { Result: { } result } => ContainsDownloadableJob(result),
            ExtractJob => false,
            JobList list => list.Jobs.Any(ContainsDownloadableJob),
            RetrieveFolderJob => false,
            _ => job.Config?.DoNotDownload == false,
        };

    private static bool ContainsLoginRequiredJob(Job job)
        => job switch
        {
            ExtractJob => job.Config?.NeedLogin == true,
            JobList list => list.Jobs.Any(ContainsLoginRequiredJob),
            _ => job.Config?.NeedLogin == true,
        };

}

internal sealed record DownloadEngineRetainedStateCounts(
    int QueueRoots,
    int RegisteredJobs,
    int Contexts,
    int ActiveDownloads,
    int ManualSelections,
    int WorkflowGenerations,
    int WorkflowDiagnostics,
    int PendingTerminalTransfers,
    int AutoProfileWorkflows,
    int EventJobs,
    int EventTransfers,
    int EventAttempts,
    int EventGates,
    int EventTerminalTransfers,
    int ActiveCachedDownloads = 0,
    int SourceMutationWorkflows = 0);
