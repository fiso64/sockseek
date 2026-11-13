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
using Sockseek.Core.Snapshots;
using Soulseek;
using Sockseek.Api;
using Sockseek.Server.Persistence;
using Sockseek.Core.Sharing;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Events;
using Sockseek.Server.Planning;
using Sockseek.Server.PeerRestrictions;

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

    public EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        InputArtifactCoordinator inputArtifacts,
        PeerRestrictionCoordinator peerRestrictions)
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true, inputArtifacts, peerRestrictions.Policy)
    {
    }

    internal EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        bool retireTerminalWorkflows,
        InputArtifactCoordinator? inputArtifacts = null,
        PeerRestrictionPolicy? peerRestrictions = null)
    {
        this.options = options.Value;
        this.persistence = persistence;
        searchResults = new(persistence, GetRuntimeJob<Job>, GetCurrentEngineUserSuccessCounts);
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.retireTerminalWorkflows = retireTerminalWorkflows;
        logger = this.loggerFactory.CreateLogger<EngineSupervisor>();

        engineSettings = SettingsCloner.Clone(this.options.Engine);
        engineSettings.AutoReconnectAfterKickedFromServer = true;
        var pathContext = new PathVariableContext(ConfigDir: this.options.ConfigDir);
        SharingSettingsValidator.NormalizeAndValidate(engineSettings, pathContext);
        ChatSettingsValidator.NormalizeAndValidate(engineSettings);

        StateStore = new EngineStateStore(persistenceOwnsJobRetirement: persistence?.IsEnabled == true);
        Runtime = new(this.options, engineSettings, StateStore, persistence, this.loggerFactory, peerRestrictions, () => restartCount);
        Submissions = new(this.options, engineSettings, persistence, StateStore, Runtime,
            EnqueueAccepted, inputArtifacts, this.loggerFactory);
        if (persistence != null)
            persistence.JobProjectionHandoffCompleted += StateStore.RetireJobProjections;
    }

    public void Dispose()
    {
        if (persistence != null)
            persistence.JobProjectionHandoffCompleted -= StateStore.RetireJobProjections;
    }

    public Task RunAsync(CancellationToken ct) => RunAsync(ct, onInitialized: null);

    internal Task RunAsync(CancellationToken ct, Action? onInitialized)
        => Runtime.RunAsync(ct, onInitialized, SuperviseEnginesAsync);

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
        string version = typeof(EngineSupervisor).Assembly.GetName().Version?.ToString() ?? "dev";
        return new ServerInfoDto(options.Name, version, StartedAtUtc, LiveProtocol.Version);
    }

    public bool TryCancelDownloadTransfer(Guid transferId)
    {
        lock (engineGate)
            return currentEngine?.TryCancelTransfer(transferId) == true;
    }

    public TransferCommandReceiptDto CancelTransfers(
        BulkCancelTransfersRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
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
                    TransferCancellationScope.Queued => string.Equals(
                        transfer.Status.State, "Queued", StringComparison.OrdinalIgnoreCase),
                    TransferCancellationScope.InProgress => !string.Equals(
                        transfer.Status.State, "Queued", StringComparison.OrdinalIgnoreCase),
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
                client?.IPEndPoint?.ToString(), client?.Options.EnableListener == true ? client.Options.ListenPort : null));
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
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelJob(jobId) ?? false;
    }

    public bool CancelJobByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelJobByDisplayId(displayId, workflowId) ?? false;
    }

    public int CancelWorkflow(Guid workflowId)
    {
        long started = Stopwatch.GetTimestamp();
        ServerLogMessages.WorkflowCancellationStarted(logger, workflowId);
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        int cancelled = engine?.CancelWorkflow(workflowId) ?? 0;
        ServerLogMessages.WorkflowCancellationCompleted(logger, workflowId,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, cancelled);
        return cancelled;
    }

    public int CancelAllJobs()
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelAllJobs() ?? 0;
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
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
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
        JobSummaryDto receipt = BuildSubmittedJobSummary(retrieveJob, sourceJobId);
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
            return new List<JobSummaryDto> { StateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong, sourceJobId) };
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
                summaries.Add(StateStore.GetJobSummary(selectedSong.Id) ?? BuildSubmittedJobSummary(selectedSong, sourceJobId));
                continue;
            }

            Job followUpJob = sourceJob is not null
                ? JobRequestMapper.CreateFileSelectionFollowUp(sourceJob, candidate, request.RequestedMode?.ToCore())
                : request.RequestedMode == ServerExtractionMode.General
                    ? new RemoteFileJob(candidate.Target)
                    : new SongJob(Searcher.InferSongQuery(candidate.Filename, new SongQuery { Title = searchQuery ?? "" }))
                        { ResolvedTarget = candidate, ExactTarget = candidate.Target };
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
            return StateStore.GetJobSummary(selectedAlbum!.Id) ?? BuildSubmittedJobSummary(selectedAlbum!, sourceJobId);
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

    private ConcurrentDictionary<string, int> GetCurrentEngineUserSuccessCounts(Guid workflowId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.GetUserSuccessCounts(workflowId) ?? new ConcurrentDictionary<string, int>();
    }

    internal SearchViewSource? GetSearchViewSource(Guid jobId)
    {
        SearchViewSource? current = GetRuntimeJob<Job>(jobId) is { } job
            ? SearchViewSource.Capture(job, new Dictionary<string, int>(
                GetCurrentEngineUserSuccessCounts(job.WorkflowId), StringComparer.Ordinal)) : null;
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

        return engine?.GetJob(jobId) as TJob;
    }

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => ServerSnapshotMapper.ToSubmittedJobSummary(job, sourceJobId);

    private void EnqueueAccepted(Job job, DownloadSettings settings, Guid? sourceJobId, bool settingsAreFinal)
    {
        // Acceptance and its retry receipt are now committed together, before
        // runtime work can enqueue unrelated history writes or observe the root.
        // Materialized Review/rerun roots do not revisit the planner. Publish
        // their final settings with the pending owner so an immediate View
        // request can use the live session before engine registration.
        if (settingsAreFinal)
            job.Config ??= settings;
        var queued = new QueuedSubmission(job, settings, sourceJobId, settingsAreFinal);
        acceptedRootsAwaitingRegistration[job.Id] = queued;
        job.EnsureDisplayId();
        var accepted = CoreSnapshotFactory.CreateJob(job, revision: 0);
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
