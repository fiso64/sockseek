using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Chat;
using Sockseek.Core.Planning;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Snapshots;
using Soulseek;
using Sockseek.Api;
using Sockseek.Server.Persistence;
using Sockseek.Persistence.Write;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Events;
using Sockseek.Server.Planning;

namespace Sockseek.Server;

public sealed class EngineSupervisor : IDisposable
{
    private readonly ServerOptions options;
    private readonly EngineSettings engineSettings;
    private readonly Channel<QueuedSubmission> submissionChannel = Channel.CreateUnbounded<QueuedSubmission>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, QueuedSubmission> acceptedRootsAwaitingRegistration = [];
    private readonly Lock engineGate = new();
    private readonly PersistenceCoordinator? persistence;
    private readonly SearchResultReader searchResults;
    private readonly TransientJobResults transientResults;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<EngineSupervisor> logger;
    private readonly bool retireTerminalWorkflows;

    private DownloadEngine? currentEngine;
    private int restartCount;

    public event Action<DownloadEngine>? EngineCreated;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public EngineStateStore StateStore { get; }
    public DaemonRuntime Runtime { get; }
    public JobSubmissions Submissions { get; }

    public EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence = null,
        ILoggerFactory? loggerFactory = null)
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true)
    {
    }

    public EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        InputArtifactCoordinator inputArtifacts)
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true, inputArtifacts)
    {
    }

    internal EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        bool retireTerminalWorkflows,
        InputArtifactCoordinator? inputArtifacts = null)
    {
        this.options = options.Value;
        this.persistence = persistence;
        searchResults = new(persistence, GetRuntimeJob<Job>, GetCurrentEnginePeerFailureCounts);
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.retireTerminalWorkflows = retireTerminalWorkflows;
        logger = this.loggerFactory.CreateLogger<EngineSupervisor>();

        engineSettings = SettingsCloner.Clone(this.options.Engine);
        engineSettings.AutoReconnectAfterKickedFromServer = true;
        var pathContext = new PathVariableContext(ConfigDir: this.options.ConfigDir);
        SharingSettingsValidator.NormalizeAndValidate(engineSettings, pathContext);
        ChatSettingsValidator.NormalizeAndValidate(engineSettings);

        StateStore = new EngineStateStore(persistenceOwnsJobRetirement: persistence?.IsEnabled == true);
        transientResults = new(StateStore.RetireJobProjections);
        Runtime = new(this.options, engineSettings, StateStore, persistence, this.loggerFactory, () => restartCount);
        Submissions = new(this.options, engineSettings, persistence, StateStore, Runtime,
            EnqueueAccepted, inputArtifacts, this.loggerFactory);
        if (persistence != null)
            persistence.JobProjectionHandoffCompleted += StateStore.RetireJobProjections;
    }

    public void Dispose()
    {
        transientResults.Dispose();
        if (persistence != null)
            persistence.JobProjectionHandoffCompleted -= StateStore.RetireJobProjections;
    }

    public Task RunAsync(CancellationToken ct) => RunAsync(ct, onInitialized: null);

    internal Task RunAsync(CancellationToken ct, Action? onInitialized)
        => Runtime.RunAsync(ct, onInitialized, async (client, token) =>
        {
            using var recoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var recovery = RecoverInterruptedDownloadsAsync(client, recoveryLifetime.Token);
            try { await SuperviseEnginesAsync(client, token); }
            finally
            {
                await recoveryLifetime.CancelAsync();
                await recovery;
            }
        });

    private async Task RecoverInterruptedDownloadsAsync(SoulseekClientManager client, CancellationToken ct)
    {
        if (persistence?.TransferHistory is not { } history) return;
        try
        {
            var ids = await history.GetPendingRecoveryIdsAsync(ct);
            if (ids.Count == 0) return;
            try { await client.EnsureConnectedAndLoggedInAsync(engineSettings, ct); }
            catch (SoulseekConnectionUnavailableException) when (!client.HasFatalError)
            {
                // The shared session monitor is already reconnecting.
                await client.WaitUntilReadyAsync(ct);
            }
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await Submissions.RetryDownloadAsync(id, ct, recovering: true) is { } job)
                        logger.LogInformation("Requeued interrupted download {TransferId} as job {JobId}", id, job.JobId);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Could not requeue interrupted download {TransferId}", id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load interrupted downloads for restart recovery");
        }
    }

    private async Task SuperviseEnginesAsync(SoulseekClientManager clientManager, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var engine = CreateEngine(clientManager);
            // Without durable history, release terminal presentation after the
            // typed event publishes it. With persistence, only commit acknowledgements retire it.
            void OnDownloadChange(CoreChange change)
            {
                var transfer = change switch
                {
                    TransferCompletedChange completed => completed.Transfer,
                    TransferFailedChange failed => failed.Transfer,
                    TransferCancelledChange cancelled => cancelled.Transfer,
                    TransferOutputChangedChange moved => moved.Transfer,
                    _ => null,
                };
                if (transfer is not null && persistence?.IsStarted != true)
                    StateStore.RemoveTerminalTransfer(transfer.Id);
            }
            engine.Events.ChangePublished += OnDownloadChange;
            var runTask = engine.RunAsync(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var waitToReadTask = submissionChannel.Reader.WaitToReadAsync(ct).AsTask();
                    var completedTask = await Task.WhenAny(runTask, waitToReadTask);

                    if (completedTask == runTask)
                    {
                        await runTask;
                        return;
                    }

                    if (!await waitToReadTask)
                        continue;

                    while (submissionChannel.Reader.TryRead(out var submission))
                    {
                        // Retain the full accepted input until registration. A
                        // failed engine may have read its queue without starting
                        // this root; only this supervisor loop changes Dispatched.
                        submission.Dispatched = true;
                        engine.Enqueue(
                            submission.Job,
                            submission.Settings,
                            submission.SourceJobId,
                            submission.SettingsAreFinal);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref restartCount);
                ServerLogMessages.EngineRestarting(logger, ex, restartCount);
                StateStore.MarkActiveJobsInfrastructureFailed(
                    ExceptionText.Summary(ex),
                    ExceptionText.Detail(ex));
            }
            finally
            {
                // Roots still publish terminal state while cancellation
                // unwinds. Join them before detaching their observers.
                await engine.DisposeAsync();
                engine.Events.ChangePublished -= OnDownloadChange;
                StateStore.DetachEngine(engine);
                lock (engineGate)
                {
                    if (ReferenceEquals(currentEngine, engine))
                        currentEngine = null;
                }
            }

            // Disposal joins the failed engine's publishers, so registration can
            // no longer race this handoff. Entries still in the daemon channel
            // were never dispatched and must not be duplicated. Registered work
            // belongs to the old engine's outcome and must not be replayed.
            foreach (var pending in acceptedRootsAwaitingRegistration.Values)
            {
                if (!pending.Dispatched) continue;
                pending.Dispatched = false;
                submissionChannel.Writer.TryWrite(pending);
            }
        }
    }

    public ServerInfoDto GetInfo()
    {
        string version = ProductVersion.Display;
        return new ServerInfoDto(options.Name, version, StartedAtUtc, LiveProtocol.Version);
    }

    public bool TryCancelDownloadTransfer(Guid transferId)
    {
        DownloadEngine? engine;
        lock (engineGate) engine = currentEngine;
        if (engine is null) return false;
        if (engine.TryCancelTransfer(transferId)) return true;
        // An external downloader runs under the song's cancellation token.
        var transfer = StateStore.GetLiveTransfer(transferId);
        return transfer is { Status.IsTerminal: false, Identity.Source: nameof(TransferSnapshotSource.Fallback),
                Identity.JobId: Guid jobId }
            && engine.GetJob(jobId) is { IsTerminal: false, ActivityPhase: JobActivityPhase.RunningFallback
                or JobActivityPhase.DownloadingFallback or JobActivityPhase.ProcessingFallback }
            && engine.CancelJob(jobId);
    }

    public TransferCommandReceiptDto CancelTransfers(
        BulkCancelTransfersRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A directory starts its next file as slots open. Cancel its owner so
        // members still waiting for a slot receive the same cancellation.
        if (request.Direction == TransferCommandDirection.Download && request.Scope == TransferCancellationScope.All
            && Guid.TryParseExact(request.GroupRef, "D", out Guid directoryId))
        {
            DownloadEngine? engine;
            lock (engineGate) engine = currentEngine;
            if (engine?.GetJob(directoryId) is DirectoryDownloadJob { IsTerminal: false, ActiveAttempt: { } attempt }
                && attempt.Plan.RepresentsDirectorySelection
                && (request.Username == null || request.Username == attempt.Plan.Username))
            {
                int count = attempt.FileJobs.Count(file => !file.IsTerminal);
                if (count > 0 && engine.CancelJob(directoryId))
                    return new(count, count, 0, 0, 0, []);
            }
        }

        TransferStateDto[] targets = StateStore.GetCancellableTransferSnapshot()
            .Where(transfer =>
                !transfer.Status.IsTerminal
                && string.Equals(
                    transfer.Identity.Direction,
                    request.Direction.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                && (request.Username is null || string.Equals(
                    transfer.Identity.Username, request.Username, StringComparison.Ordinal))
                && (request.GroupRef is null || string.Equals(
                    transfer.Identity.GroupRef, request.GroupRef, StringComparison.Ordinal))
                && request.Scope switch
                {
                    TransferCancellationScope.Queued => TransferReadModel.IsQueued(transfer.Status.State),
                    TransferCancellationScope.InProgress => !TransferReadModel.IsQueued(transfer.Status.State),
                    _ => true,
                })
            .ToArray();

        return CancelTransferSnapshot(
            request.Direction,
            targets,
            StateStore.GetLiveTransfer,
            TryCancelDownloadTransfer,
            transferId => Runtime.Sharing?.Uploads.Cancel(transferId) == true,
            (transferId, exception) => ServerLogMessages.BulkTransferCancellationFailed(
                logger,
                exception,
                transferId),
            uploadRuntimeAvailable: Runtime.Sharing is not null);
    }

    internal static TransferCommandReceiptDto CancelTransferSnapshot(
        TransferCommandDirection direction,
        IReadOnlyList<TransferStateDto> targets,
        Func<Guid, TransferStateDto?> getCurrent,
        Func<Guid, bool> cancelDownload,
        Func<Guid, bool> cancelUpload,
        Action<Guid, Exception> logFailure,
        bool uploadRuntimeAvailable = true)
    {
        int succeeded = 0;
        int noOp = 0;
        int rejected = 0;
        int failed = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        void Reason(string reason)
            => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

        foreach (TransferStateDto target in targets)
        {
            try
            {
                TransferStateDto? current = getCurrent(target.TransferId);
                if (current is null || current.Status.IsTerminal)
                {
                    noOp++;
                    Reason("already-terminal");
                    continue;
                }

                bool cancelled = direction switch
                {
                    TransferCommandDirection.Download => cancelDownload(target.TransferId),
                    TransferCommandDirection.Upload => cancelUpload(target.TransferId),
                    _ => false,
                };
                if (cancelled)
                {
                    succeeded++;
                    continue;
                }

                current = getCurrent(target.TransferId);
                if (current is null || current.Status.IsTerminal)
                {
                    noOp++;
                    Reason("already-terminal");
                }
                else
                {
                    rejected++;
                    Reason(direction == TransferCommandDirection.Upload
                           && !uploadRuntimeAvailable
                        ? "runtime-unavailable"
                        : "not-cancellable");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Reason("internal-failure");
                logFailure(target.TransferId, ex);
            }
        }

        return new TransferCommandReceiptDto(
            targets.Count,
            succeeded,
            noOp,
            rejected,
            failed,
            reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TransferCommandReasonCountDto(pair.Key, pair.Value))
                .ToArray());
    }

    public ServerStatusDto GetStatus()
    {
        SoulseekClientStates clientState;
        lock (engineGate)
            clientState = Runtime.SoulseekSession?.State
                          ?? currentEngine?.ClientState
                          ?? SoulseekClientStates.None;

        var client = Runtime.SoulseekSession?.Client;
        var stats = StateStore.GetStatistics();
        var persistenceStatus = GetPersistenceStatus();
        var profile = Runtime.SoulseekRuntime?.LocalProfile;
        return new ServerStatusDto(
            DaemonRuntime.ToSoulseekClientStatusDto(clientState),
            stats.TotalJobCount,
            stats.ActiveJobCount,
            stats.TotalWorkflowCount,
            stats.ActiveWorkflowCount,
            restartCount,
            persistenceStatus,
            stats.Configuration,
            new SoulseekConnectionDto(Runtime.SoulseekSession?.LoggedInUsername, client?.Address, client?.Port,
                client?.IPEndPoint?.ToString(), client?.Options.EnableListener == true ? client.Options.ListenPort : null),
            profile is null ? null : new LocalUserProfileDto(profile.Description, profile.Picture is { } picture
                ? $"/api/server/profile/picture?v={Uri.EscapeDataString(picture.ETag.Trim('"'))}" : null));
    }

    private PersistenceStatusDto GetPersistenceStatus()
    {
        if (persistence?.IsEnabled != true)
            return new PersistenceStatusDto(
                false, false, "Disabled", null, null, null, null, null, null,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var runtime = persistence.Runtime;
        var snapshot = persistence.HealthSnapshot;
        var reconciliation = persistence.Reconciliation;
        var lastRetention = persistence.LastRetentionResult;
        if (snapshot == null)
            return new PersistenceStatusDto(
                true, false, "Starting", persistence.Initialization?.SchemaVersion,
                runtime?.RuntimeId, runtime?.StartedAtUtc, null, null, null,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        return new PersistenceStatusDto(
            true,
            persistence.Initialization != null,
            snapshot.State.ToString(),
            persistence.Initialization?.SchemaVersion,
            runtime?.RuntimeId,
            runtime?.StartedAtUtc,
            snapshot.LastSuccessfulCommitAtUtc,
            snapshot.LastFailureAtUtc,
            snapshot.LastFailure,
            snapshot.CriticalQueueDepth,
            snapshot.OrdinaryQueueDepth,
            snapshot.ProgressEntityCount,
            snapshot.ProgressEntityCapacity,
            snapshot.BufferedSearchResultCount,
            snapshot.RetryMutationCount,
            snapshot.BusyRetryCount,
            snapshot.DroppedProgressCount,
            snapshot.SuccessfulCommitCount,
            snapshot.RowsWritten,
            persistence.DatabaseSizeBytes,
            persistence.WalSizeBytes,
            snapshot.LastCommitDurationMilliseconds,
            snapshot.LastBatchMutationCount,
            snapshot.PermanentlyFailedMutationCount,
            snapshot.CommitLatencyHistogram,
            snapshot.BatchSizeHistogram,
            reconciliation?.UnfinishedRuntimeCount ?? 0,
            reconciliation?.InterruptedJobCount ?? 0,
            reconciliation?.InterruptedTransferCount ?? 0,
            reconciliation?.InterruptedAttemptCount ?? 0,
            reconciliation?.InterruptedSearchCount ?? 0,
            persistence.LastRetentionAtUtc,
            lastRetention?.PrunedJobs ?? 0,
            lastRetention?.PrunedSearchResults ?? 0,
            lastRetention?.PrunedTransfers ?? 0,
            lastRetention?.PrunedTransferAttempts ?? 0);
    }

    public bool CancelJob(Guid jobId)
    {
        acceptedRootsAwaitingRegistration.TryGetValue(jobId, out var queued);
        if (queued is not null && CancelQueued(queued, JobCancellationSource.UserRequestedJob)) return true;
        DownloadEngine? engine;
        lock (engineGate) engine = currentEngine;
        return engine?.CancelJob(jobId) == true;
    }

    private bool CancelQueued(QueuedSubmission queued, JobCancellationSource source)
    {
        var job = queued.Job;
        if (job.IsTerminal) return false;
        if (job.TryCancelQueuedRoot(source, () =>
        {
            var snapshot = CoreSnapshotFactory.CreateJob(job, job.StateSnapshot.Revision);
            if (job.Retention != JobRetention.Memory && persistence?.Runtime is { } runtime)
                persistence.MutationSink?.TryEnqueue(JobPersistence.Capture(runtime.RuntimeId, snapshot, 0,
                    DateTimeOffset.UtcNow, PersistenceMutationPriority.Terminal, null, queued.SourceJobId, null, null));
            StateStore.RegisterAcceptedRoot(snapshot, queued.SourceJobId);
        })) return true;
        // The scheduler took the execution gate just before registration.
        // RegisterForExecution carries this request into its new token.
        if (job.CancellationSource != JobCancellationSource.None || job.IsTerminal) return false;
        job.Cancel(source);
        return true;
    }

    public bool CancelJobByDisplayId(Guid workflowId, int displayId)
    {
        var queued = acceptedRootsAwaitingRegistration.Values.FirstOrDefault(item =>
            item.Job.DisplayId == displayId && item.Job.WorkflowId == workflowId);
        if (queued is not null && CancelQueued(queued, JobCancellationSource.UserRequestedJob)) return true;
        DownloadEngine? engine;
        lock (engineGate) engine = currentEngine;
        return engine?.CancelJobByDisplayId(displayId, workflowId) == true;
    }

    public int CancelWorkflow(Guid workflowId)
    {
        long started = Stopwatch.GetTimestamp();
        ServerLogMessages.WorkflowCancellationStarted(logger, workflowId);
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        int cancelled = acceptedRootsAwaitingRegistration.Values.Count(queued =>
            queued.Job.WorkflowId == workflowId && CancelQueued(queued, JobCancellationSource.UserRequestedWorkflow));
        cancelled += engine?.CancelWorkflow(workflowId) ?? 0;
        ServerLogMessages.WorkflowCancellationCompleted(logger, workflowId,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, cancelled);
        return cancelled;
    }

    public int CancelAllJobs()
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        int cancelled = acceptedRootsAwaitingRegistration.Values.Count(queued =>
            CancelQueued(queued, JobCancellationSource.UserRequestedAllJobs));
        return cancelled + (engine?.CancelAllJobs() ?? 0);
    }

    internal Task WaitForSubmissionExecutionAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        lock (engineGate)
            return currentEngine?.WaitForSubmissionAsync(submissionId, cancellationToken) ?? Task.CompletedTask;
    }

    internal void ForgetSubmissions(IReadOnlySet<Guid> submissionIds)
    {
        lock (engineGate) currentEngine?.ForgetSubmissions(submissionIds);
    }

    internal HashSet<Guid> GetPendingSubmissionIds()
        => acceptedRootsAwaitingRegistration.Values.Select(pending => pending.Job.SubmissionId)
            .OfType<Guid>().ToHashSet();

    public bool TryNextCandidate(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.TryNextCandidate(jobId) ?? false;
    }

    public bool TryNextCandidateByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.TryNextCandidateByDisplayId(displayId, workflowId) ?? false;
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct)
    {
        var source = await searchResults.GetSourceAsync(sourceJobId, ct).ConfigureAwait(false);
        if (source is null) return null;
        var (folder, _) = await searchResults.GetFolderAsync(source, request.Folder, request.AlbumQuery,
            forRetrieval: true, ct).ConfigureAwait(false);
        var retrieveJob = new RetrieveFolderJob(folder.DirectoryIdentity)
        {
            ItemName = folder.FolderPath,
            WorkflowId = source.WorkflowId,
            ResultObserver = source.RuntimeJob is null ? null : (snapshot, _) => Searcher.ApplyDirectorySnapshot(folder, snapshot),
        };
        var settings = source.RuntimeJob?.Config ?? Submissions.SettingsResolver.ResolveFollowUp(retrieveJob, options: null);
        await Submissions.AcceptAsync(retrieveJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? ServerSnapshotMapper.ToSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    internal async Task<JobSummaryDto?> StartSearchViewDirectoryRetrievalAsync(
        Guid sourceJobId,
        PeerDirectoryIdentity directory,
        Func<PeerDirectorySnapshot, CancellationToken, int> resultObserver,
        Guid idempotencyKey,
        string commitFingerprint,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(resultObserver);
        var source = await searchResults.GetSourceAsync(sourceJobId, ct).ConfigureAwait(false);
        if (source is null) return null;
        var retrieveJob = new RetrieveFolderJob(directory)
        {
            ItemName = directory.FolderPath,
            WorkflowId = source.WorkflowId,
            ResultObserver = resultObserver,
        };
        var settings = source.RuntimeJob?.Config ?? Submissions.SettingsResolver.ResolveFollowUp(retrieveJob, options: null);
        SubmissionIdentity.AssignAccepted(
            retrieveJob,
            settings,
            submissionId: idempotencyKey);
        retrieveJob.EnsureDisplayId();
        JobSummaryDto receipt = ServerSnapshotMapper.ToSubmittedJobSummary(retrieveJob, sourceJobId);
        await Submissions.AcceptAsync(
            retrieveJob,
            settings,
            sourceJobId,
            ct,
            commitFingerprint: commitFingerprint,
            commitReceipt: receipt).ConfigureAwait(false);
        return receipt;
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct)
    {
        var resolver = Submissions.SettingsResolver;
        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");
        if (request.RequestedMode == ServerExtractionMode.Album)
            throw new ArgumentException("A file selection cannot be interpreted as an album.");

        var source = await searchResults.GetSourceAsync(sourceJobId, ct).ConfigureAwait(false);
        if (source is null) return null;
        var sourceJob = source.RuntimeJob;

        var summaries = new List<JobSummaryDto>();

        if ((request.RequestedMode is null or ServerExtractionMode.Song)
            && sourceJob is SongJob manualSong && manualSong.IsAwaitingSelection)
        {
            if (request.Files.Count != 1)
                throw new ArgumentException("Manual song jobs require exactly one selected file.");

            var (candidate, _) = await searchResults.GetFileAsync(source, request.Files[0], ct).ConfigureAwait(false);

            DownloadEngine? engine;
            lock (engineGate)
                engine = currentEngine;
            if (engine?.TryStartManualSongSelection(sourceJobId, candidate,
                song => resolver.ApplyManualSelectionOptions(sourceJob, song, request.Options), out _) != true)
                throw new ArgumentException("The song is no longer awaiting selection.");
            return new List<JobSummaryDto> { StateStore.GetJobSummary(manualSong.Id) ?? ServerSnapshotMapper.ToSubmittedJobSummary(manualSong, sourceJobId) };
        }

        foreach (var file in request.Files)
        {
            var (candidate, searchQuery) = await searchResults.GetFileAsync(source, file, ct).ConfigureAwait(false);

            if ((request.RequestedMode is null or ServerExtractionMode.Song)
                && sourceJob is SongGroupsJob && sourceJob.IsAwaitingSelection)
            {
                DownloadEngine? engine;
                lock (engineGate)
                    engine = currentEngine;
                if (engine == null || !engine.TryStartManualSongSelection(sourceJobId, candidate,
                    song => resolver.ApplyManualSelectionOptions(sourceJob, song, request.Options), out var selectedSong)
                    || selectedSong == null)
                    throw new ArgumentException("The group is no longer accepting selections.");
                summaries.Add(StateStore.GetJobSummary(selectedSong.Id) ?? ServerSnapshotMapper.ToSubmittedJobSummary(selectedSong, sourceJobId));
                continue;
            }

            Job followUpJob = sourceJob is not null
                ? JobRequestMapper.CreateFileSelectionFollowUp(sourceJob, candidate, request.RequestedMode?.ToCore())
                : request.RequestedMode == ServerExtractionMode.General
                    ? new RemoteFileJob(candidate.Target)
                    : new SongJob(Searcher.InferSongQuery(candidate.Filename, new SongQuery { Title = searchQuery ?? "" }))
                        { ResolvedTarget = candidate, ExactTarget = candidate.Target };
            followUpJob.SelectionSource = JobSelectionSource.ManualSearch;
            followUpJob.ItemName = source.ItemName;
            followUpJob.WorkflowId = source.WorkflowId;

            var followUpSettings = resolver.ResolveFollowUp(followUpJob, request.Options);
            summaries.Add(await Submissions.SubmitFollowUpJobAsync(sourceJobId, sourceJob, followUpJob, followUpSettings, request.Options, ct, resolver));
        }

        return summaries;
    }

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid sourceJobId, StartFolderDownloadRequestDto request, CancellationToken ct)
    {
        var resolver = Submissions.SettingsResolver;
        if (request.RequestedMode == ServerExtractionMode.Song)
            throw new ArgumentException("A directory selection cannot be interpreted as one song.");
        var source = await searchResults.GetSourceAsync(sourceJobId, ct).ConfigureAwait(false);
        if (source is null) return null;
        var sourceJob = source.RuntimeJob;
        var (folder, albumQuery) = await searchResults.GetFolderAsync(source, request.Folder, request.AlbumQuery,
            forRetrieval: false, ct).ConfigureAwait(false);

        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        if (request.RequestedMode == ServerExtractionMode.General)
        {
            var directoryJob = JobRequestMapper.CreateRemoteDirectoryDownload(folder, request.Selection);
            directoryJob.ItemName = source.ItemName;
            directoryJob.WorkflowId = source.WorkflowId;
            var directorySettings = resolver.ResolveFollowUp(directoryJob, request.Options);
            return await Submissions.SubmitFollowUpJobAsync(
                sourceJobId, sourceJob, directoryJob, directorySettings,
                request.Options, ct, resolver);
        }

        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        bool manualSelection = sourceJob is AlbumJob or AlbumGroupsJob && sourceJob.IsAwaitingSelection;
        if (sourceJob is not null && engine?.TryStartManualAlbumSelection(
            sourceJobId,
            folder,
            albumQuery,
            album =>
            {
                JobRequestMapper.ApplyFolderDownloadSelection(album, request.Selection);
                resolver.ApplyManualSelectionOptions(sourceJob, album, request.Options);
            },
            out var selectedAlbum) == true)
        {
            return StateStore.GetJobSummary(selectedAlbum!.Id) ?? ServerSnapshotMapper.ToSubmittedJobSummary(selectedAlbum!, sourceJobId);
        }
        if (manualSelection)
            throw new ArgumentException("The job is no longer accepting selections.");
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from this job require an album query.");

        AlbumJob albumJob = sourceJob is not null
            ? JobRequestMapper.CreateAlbumSelectionFollowUp(sourceJob, folder, albumQuery, request.Selection)
            : new AlbumJob(new AlbumQuery(albumQuery))
                {
                    ResolvedTarget = folder,
                    ItemName = source.ItemName,
                    WorkflowId = source.WorkflowId,
                    DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
                };
        if (sourceJob is null) JobRequestMapper.ApplyFolderDownloadSelection(albumJob, request.Selection);

        albumJob.SelectionSource = JobSelectionSource.ManualSearch;
        var followUpSettings = resolver.ResolveFollowUp(albumJob, request.Options);

        return await Submissions.SubmitFollowUpJobAsync(sourceJobId, sourceJob, albumJob, followUpSettings, request.Options, ct, resolver);
    }

    public async Task<bool> CompleteManualSelectionAsync(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine != null && await engine.CompleteManualSelectionAsync(jobId);
    }

    public async Task<bool> SkipManualSelectionAsync(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine != null && await engine.SkipManualSelectionAsync(jobId);
    }

    private DownloadEngine CreateEngine(SoulseekClientManager clientManager)
    {
        var engine = new DownloadEngine(
            engineSettings,
            clientManager,
            Submissions.SettingsResolver,
            directorySource: Runtime.PeerBrowses,
            loggerFactory: loggerFactory,
            retireTerminalWorkflows: retireTerminalWorkflows);
        StateStore.SetJobPersistence(persistence?.CreateJobPersistence(engine));
        engine.Events.WorkflowRetired += change => transientResults.Capture(engine.GetJobsByWorkflow(change.WorkflowId));
        StateStore.AttachEngine(engine);
        // A queued root is already readable, but its runtime owner remains here
        // until the engine actually registers it (not merely a snapshot update).
        engine.Events.JobRegistered += change =>
            acceptedRootsAwaitingRegistration.TryRemove(change.Job.Id, out _);
        lock (engineGate)
            currentEngine = engine;
        StateStore.UpdateDaemonRuntime(DaemonRuntime.ToSoulseekClientStatusDto(clientManager.State), restartCount);
        EngineCreated?.Invoke(engine);
        return engine;
    }

    private ConcurrentDictionary<string, int> GetCurrentEnginePeerFailureCounts(Guid workflowId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.GetPeerFailureCounts(workflowId) ?? new ConcurrentDictionary<string, int>();
    }

    internal SearchViewSource? GetSearchViewSource(Guid jobId)
    {
        SearchViewSource? current = GetRuntimeJob<Job>(jobId) is { } job
            ? SearchViewSource.Capture(job, new Dictionary<string, int>(
                GetCurrentEnginePeerFailureCounts(job.WorkflowId), StringComparer.Ordinal)) : null;
        // Retirement can start between the job lookup and counter capture.
        // Its handoff owns the final inputs even while Core still has the job.
        return persistence?.GetRetiringSearchSource(jobId) ?? current;
    }

    internal TJob? GetRuntimeJob<TJob>(Guid jobId)
        where TJob : Job
    {
        // Admission precedes engine registration. Read the pending owner first:
        // its entry is removed only after the engine has published this root.
        // An immediate Search View request must not fall through to absent history.
        if (acceptedRootsAwaitingRegistration.TryGetValue(jobId, out var accepted))
            return accepted.Job as TJob;

        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return (engine?.GetJob(jobId) ?? transientResults.Find(jobId)) as TJob;
    }

    internal bool ReleaseTransientResults(Guid submissionId, out int count)
        => transientResults.Release(submissionId, out count);

    private void EnqueueAccepted(Job job, JobSnapshot accepted, DownloadSettings settings, Guid? sourceJobId, bool settingsAreFinal)
    {
        var queued = new QueuedSubmission(job, settings, sourceJobId, settingsAreFinal);
        acceptedRootsAwaitingRegistration[job.Id] = queued;
        StateStore.RegisterAcceptedRoot(accepted, sourceJobId);
        submissionChannel.Writer.TryWrite(queued);
    }

    private sealed record QueuedSubmission(
        Job Job,
        DownloadSettings Settings,
        Guid? SourceJobId = null,
        bool SettingsAreFinal = false)
    {
        public bool Dispatched { get; set; }
    }
}
