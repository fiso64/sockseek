using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Soulseek;
using Sockseek.Api;
using Sockseek.Server.Persistence;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Core.Chat;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Events;
using Sockseek.Persistence.PeerBrowsing;
using Sockseek.Server.PeerBrowsing;
using Sockseek.Core.UserProfiles;
using Sockseek.Server.UserProfiles;
using Sockseek.Core.Planning;
using Sockseek.Server.Planning;
using Sockseek.Server.PeerRestrictions;

namespace Sockseek.Server;

public sealed class EngineSupervisor : IDisposable
{
    private static readonly ActivitySource SubmissionActivities = new("Sockseek.Server.Submissions");
    private readonly ServerOptions options;
    private readonly EngineSettings engineSettings;
    private readonly DownloadSettings defaultDownloadSettings;
    private readonly ProfileCatalog profileCatalog;
    private readonly SubmissionOptionsJobSettingsResolver jobSettingsResolver;
    private readonly Channel<QueuedSubmission> submissionChannel = Channel.CreateUnbounded<QueuedSubmission>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, Job> acceptedRootsAwaitingRegistration = [];
    private readonly Lock engineGate = new();
    private readonly PersistenceCoordinator? persistence;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<EngineSupervisor> logger;
    private readonly bool retireTerminalWorkflows;
    private readonly InputArtifactCoordinator? inputArtifacts;
    private PeerRestrictionPolicy? peerRestrictions;

    private DownloadEngine? currentEngine;
    private int restartCount;

    public event Action<DownloadEngine>? EngineCreated;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public EngineStateStore StateStore { get; }
    public SharingRuntime? Sharing { get; private set; }
    public ChatRuntime? Chat { get; private set; }
    public DaemonSoulseekRuntime? SoulseekRuntime { get; private set; }
    public PeerBrowseService? PeerBrowses { get; private set; }
    public UserProfileService? UserProfiles { get; private set; }
    /// <summary>
    /// Neutral daemon-session seam for future chat and remote-user services.
    /// This is the same manager used by sharing and every download engine.
    /// </summary>
    public SoulseekClientManager? SoulseekSession { get; private set; }
    internal DownloadSettings DefaultDownloadSettings => defaultDownloadSettings;

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
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true)
    {
        this.inputArtifacts = inputArtifacts;
    }

    public EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        InputArtifactCoordinator inputArtifacts,
        PeerRestrictionCoordinator peerRestrictions)
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true)
    {
        this.inputArtifacts = inputArtifacts;
        this.peerRestrictions = peerRestrictions.Policy;
    }

    internal EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        bool retireTerminalWorkflows)
    {
        this.options = options.Value;
        this.persistence = persistence;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.retireTerminalWorkflows = retireTerminalWorkflows;
        logger = this.loggerFactory.CreateLogger<EngineSupervisor>();

        engineSettings = SettingsCloner.Clone(this.options.Engine);
        engineSettings.AutoReconnectAfterKickedFromServer = true;
        defaultDownloadSettings = this.options.DefaultDownload == null
            ? SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic)
            : SettingsCloner.Clone(this.options.DefaultDownload);
        DownloadSettingsPatchDtoMapper.ApplyTo(
            defaultDownloadSettings,
            this.options.OperatorDownloadSettings);
        defaultDownloadSettings.DefaultMinSharesAggregate = engineSettings.DefaultMinSharesAggregate;
        var pathContext = new PathVariableContext(ConfigDir: this.options.ConfigDir);
        SharingSettingsValidator.NormalizeAndValidate(engineSettings, pathContext);
        ChatSettingsValidator.NormalizeAndValidate(engineSettings);
        SettingsNormalizer.NormalizeDownloadPaths(defaultDownloadSettings, pathContext);
        profileCatalog = this.options.Profiles ?? ProfileCatalog.Empty;
        jobSettingsResolver = CreateJobSettingsResolver(this.options, profileCatalog, pathContext);

        StateStore = new EngineStateStore(persistenceOwnsJobRetirement: persistence?.IsEnabled == true);
        if (persistence != null)
            persistence.JobProjectionHandoffCompleted += StateStore.RetireJobProjections;
    }

    public void Dispose()
    {
        if (persistence != null)
            persistence.JobProjectionHandoffCompleted -= StateStore.RetireJobProjections;
    }

    private static SubmissionOptionsJobSettingsResolver CreateJobSettingsResolver(
        ServerOptions options,
        ProfileCatalog profiles,
        PathVariableContext pathContext)
    {
        SettingsPatch launchPatch = DownloadSettingsPatchDtoMapper.ToCore(options.LaunchDownloadSettings);
        SettingsPatch operatorPatch = DownloadSettingsPatchDtoMapper.ToCore(options.OperatorDownloadSettings);
        var profilesResolver = new ProfileJobSettingsResolver(
            options.DefaultDownload,
            profiles,
            profiles.ResolveNamedProfiles(options.LaunchProfileNames),
            new SettingsProfile { Name = "<daemon-launch>", Download = launchPatch },
            normalize: settings =>
            {
                settings.DefaultMinSharesAggregate = options.Engine.DefaultMinSharesAggregate;
                SettingsNormalizer.NormalizeDownloadPaths(settings, pathContext);
            },
            operatorDefault: operatorPatch);
        return new SubmissionOptionsJobSettingsResolver(profilesResolver);
    }

    public Task RunAsync(CancellationToken ct) => RunAsync(ct, onInitialized: null);

    internal async Task RunAsync(CancellationToken ct, Action? onInitialized)
    {
        string dataDirectory = SockseekDataPaths.ResolveDataDirectory(
            options.Persistence.DataDirectory);
        LocalUserProfile localProfile = await LocalUserProfile.LoadAsync(
            engineSettings,
            ct,
            loggerFactory.CreateLogger<LocalUserProfile>()).ConfigureAwait(false);
        await using var soulseek = new DaemonSoulseekRuntime(
            engineSettings,
            options.ClientFactory,
            localProfile,
            loggerFactory.CreateLogger<SoulseekClientManager>(),
            peerRestrictions);
        await using var sharing = new SharingRuntime(
            engineSettings,
            dataDirectory,
            soulseek,
            loggerFactory.CreateLogger<SharingRuntime>(),
            loggerFactory.CreateLogger<UploadCoordinator>());
        await using ChatRuntime? chat = persistence?.Chat is { } chatStore
            ? new ChatRuntime(
                engineSettings,
                soulseek,
                chatStore,
                loggerFactory.CreateLogger<ChatRuntime>())
            : null;
        await using DisabledChatIngress? disabledChat = chat is null
            ? new DisabledChatIngress(
                soulseek,
                loggerFactory.CreateLogger<DisabledChatIngress>())
            : null;
        Sharing = sharing;
        Chat = chat;
        SoulseekRuntime = soulseek;
        SoulseekSession = soulseek.ClientManager;
        PeerBrowseService? peerBrowses = null;
        PeerBrowseArtifactStore? peerBrowseStore = null;
        UserProfileService? userProfiles = null;
        void OnClientStateChanged(SoulseekClientStates state)
        {
            StateStore.UpdateDaemonRuntime(
                ToSoulseekClientStatusDto(state),
                restartCount);
            userProfiles?.OnSoulseekStateChanged(state);
            peerBrowses?.OnSoulseekStateChanged(state);
        }
        void OnSharingStateChanged(
            SharingStateDto sharingState,
            UploadRuntimeStateDto uploadState)
            => StateStore.UpdateSharingRuntime(
                sharingState,
                WithHistoryHealth(uploadState));
        void OnHistoryHealthChanged()
            => StateStore.UpdateSharingRuntime(
                sharing.GetSharingState(),
                WithHistoryHealth(sharing.GetUploadRuntimeState()));
        void OnTerminalTransferCommitted()
            => StateStore.NotifyTerminalTransferCommitted();
        void OnChatStateChanged(
            ChatRuntimeStateDto chatState,
            NotificationSummaryDto notifications)
            => StateStore.UpdateChatRuntime(chatState, notifications);
        void OnNotificationCommitted(Sockseek.Core.Chat.UserNotificationRecord notification)
            => StateStore.PublishNotification(notification);
        void OnChatTargetChanged(ChatTargetDeltaDto delta)
            => StateStore.PublishChatTarget(delta);
        Task OnChatRetentionCompleted(
            Sockseek.Persistence.Chat.ChatRetentionResult result,
            CancellationToken cancellationToken)
            => chat?.PublishRetentionAsync(result, cancellationToken) ?? Task.CompletedTask;
        void OnUploadChanged(UploadTransferSnapshot transfer)
        {
            StateStore.UpdateUploadTransfer(transfer);
            if (transfer.State is UploadTransferState.Completed
                or UploadTransferState.Cancelled
                or UploadTransferState.Failed
                or UploadTransferState.Interrupted)
            {
                _ = RetireTerminalUploadAsync(transfer);
            }
        }
        void OnPeerBrowseChanged(PeerBrowseResource resource)
            => StateStore.UpdateUserBrowse(UserBrowseDtoMapper.ToDto(resource));
        void OnPeerBrowseRemoved(Guid browseId)
            => StateStore.RemoveUserBrowse(browseId);
        sharing.ClientManager.StateChanged += OnClientStateChanged;
        sharing.StateChanged += OnSharingStateChanged;
        if (chat is not null)
        {
            chat.StateChanged += OnChatStateChanged;
            chat.NotificationCommitted += OnNotificationCommitted;
            chat.TargetChanged += OnChatTargetChanged;
        }
        persistence?.AttachUploads(sharing.Uploads);
        sharing.Uploads.TransferChanged += OnUploadChanged;
        foreach (UploadTransferSnapshot transfer in sharing.Uploads.Snapshot())
            OnUploadChanged(transfer);
        if (persistence is not null)
        {
            persistence.HistoryHealthChanged += OnHistoryHealthChanged;
            persistence.TerminalTransferCommitted += OnTerminalTransferCommitted;
            persistence.TransfersPruned += StateStore.MarkTransferHistoryChanged;
            persistence.ChatRetentionCompleted += OnChatRetentionCompleted;
        }
        try
        {
            userProfiles = new UserProfileService(
                new SoulseekUserProfileTransport(() => soulseek.ClientManager.Client),
                soulseek.EnsureStartedAsync,
                () => soulseek.ClientManager.LoggedInUsername
                      ?? (!string.IsNullOrWhiteSpace(engineSettings.MockFilesDir) ? "local" : null)
                      ?? (!engineSettings.UseRandomLogin ? engineSettings.Username : null),
                logger: loggerFactory.CreateLogger<UserProfileService>());
            userProfiles.OnSoulseekStateChanged(sharing.ClientManager.State);
            UserProfiles = userProfiles;
            peerBrowseStore = new PeerBrowseArtifactStore(
                dataDirectory,
                logger: loggerFactory.CreateLogger<PeerBrowseArtifactStore>());
            await peerBrowseStore.InitializeAsync(ct);
            peerBrowses = new PeerBrowseService(
                peerBrowseStore,
                new SoulseekPeerBrowseTransport(
                    soulseek.ClientManager,
                    ensureSessionStarted: soulseek.EnsureStartedAsync),
                () => soulseek.ClientManager.LoggedInUsername
                      ?? (!string.IsNullOrWhiteSpace(engineSettings.MockFilesDir) ? "local" : null)
                      ?? (!engineSettings.UseRandomLogin ? engineSettings.Username : null),
                logger: loggerFactory.CreateLogger<PeerBrowseService>());
            peerBrowses.OnSoulseekStateChanged(sharing.ClientManager.State);
            peerBrowses.Changed += OnPeerBrowseChanged;
            peerBrowses.Removed += OnPeerBrowseRemoved;
            PeerBrowses = peerBrowses;

            // Chat construction already attached ingress: an immediate API
            // request can start login and release queued private messages.
            // Local API readiness must not wait for the remote server.
            onInitialized?.Invoke();
            if (chat is not null)
                await chat.StartAsync(ct);
            // Chat attaches protocol callbacks before any sharing-triggered
            // login can release queued private messages from the server.
            await sharing.StartAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var engine = CreateEngine(sharing.ClientManager);
                // CreateEngine attaches persistence first. Specific terminal
                // events precede ChangePublished, so retirement must listen here
                // after persistence has registered/admitted the terminal handoff.
                void OnDownloadChange(CoreChange change)
                {
                    var transfer = change switch
                    {
                        TransferCompletedChange completed => completed.Transfer,
                        TransferFailedChange failed => failed.Transfer,
                        TransferCancelledChange cancelled => cancelled.Transfer,
                        _ => null,
                    };
                    if (transfer is not null) _ = RetireTerminalDownloadAsync(transfer);
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
                    continue;
                }
                finally
                {
                    // Roots still publish terminal state while cancellation
                    // unwinds. Join them before detaching their observers.
                    await engine.DisposeAsync();
                    engine.Events.ChangePublished -= OnDownloadChange;
                    StateStore.DetachEngine(engine);
                    persistence?.DetachEngine(engine);
                    lock (engineGate)
                    {
                        if (ReferenceEquals(currentEngine, engine))
                            currentEngine = null;
                    }
                }
            }
        }
        finally
        {
            UserProfiles = null;
            if (userProfiles is not null)
                await userProfiles.DisposeAsync();
            userProfiles = null;
            PeerBrowses = null;
            if (peerBrowses is not null)
            {
                peerBrowses.Changed -= OnPeerBrowseChanged;
                peerBrowses.Removed -= OnPeerBrowseRemoved;
                await peerBrowses.DisposeAsync();
            }
            if (peerBrowseStore is not null)
                await peerBrowseStore.DisposeAsync();
            sharing.ClientManager.StateChanged -= OnClientStateChanged;
            sharing.StateChanged -= OnSharingStateChanged;
            if (chat is not null)
            {
                chat.StateChanged -= OnChatStateChanged;
                chat.NotificationCommitted -= OnNotificationCommitted;
                chat.TargetChanged -= OnChatTargetChanged;
            }
            sharing.Uploads.TransferChanged -= OnUploadChanged;
            if (persistence is not null)
            {
                persistence.HistoryHealthChanged -= OnHistoryHealthChanged;
                persistence.TerminalTransferCommitted -= OnTerminalTransferCommitted;
                persistence.TransfersPruned -= StateStore.MarkTransferHistoryChanged;
                persistence.ChatRetentionCompleted -= OnChatRetentionCompleted;
            }
            persistence?.DetachUploads(sharing.Uploads);
            Sharing = null;
            Chat = null;
            SoulseekRuntime = null;
            SoulseekSession = null;
        }

        UploadRuntimeStateDto WithHistoryHealth(UploadRuntimeStateDto upload)
        {
            if (!upload.AcceptingUploads
                || persistence?.IsEnabled != true
                || persistence.HealthSnapshot?.State
                    is null or Sockseek.Persistence.Write.PersistenceHealthState.Healthy)
            {
                return upload;
            }
            return upload with
            {
                State = DaemonFeatureState.Degraded,
                Reason = "HistoryPersistenceDegraded",
            };
        }

        async Task RetireTerminalUploadAsync(UploadTransferSnapshot transfer)
        {
            try
            {
                // The terminal delta is published synchronously before this
                // continuation. When history is enabled, retain presentation
                // state until the same terminal revision is durably committed.
                if (persistence?.IsStarted == true)
                {
                    await persistence.WaitForTransferHandoffAsync(
                        transfer.TransferId,
                        transfer.Revision,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ServerLogMessages.TerminalUploadHandoffFailed(
                    logger,
                    ex,
                    transfer.TransferId);
            }
            finally
            {
                StateStore.RemoveTerminalTransfer(transfer.TransferId);
                sharing.Uploads.Forget(transfer.TransferId);
            }
        }

        async Task RetireTerminalDownloadAsync(TransferSnapshot transfer)
        {
            try
            {
                if (persistence?.IsStarted == true)
                {
                    await persistence.WaitForTransferHandoffAsync(
                        transfer.Id,
                        transfer.Revision,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ServerLogMessages.TerminalDownloadHandoffFailed(
                    logger,
                    ex,
                    transfer.Id);
            }
            finally
            {
                StateStore.RemoveTerminalTransfer(transfer.Id);
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
            transferId => Sharing?.Uploads.Cancel(transferId) == true,
            (transferId, exception) => ServerLogMessages.BulkTransferCancellationFailed(
                logger,
                exception,
                transferId),
            uploadRuntimeAvailable: Sharing is not null);
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
            clientState = SoulseekSession?.State
                          ?? currentEngine?.ClientState
                          ?? SoulseekClientStates.None;

        var stats = StateStore.GetStatistics();
        var persistenceStatus = GetPersistenceStatus();
        return new ServerStatusDto(
            ToSoulseekClientStatusDto(clientState),
            stats.TotalJobCount,
            stats.ActiveJobCount,
            stats.TotalWorkflowCount,
            stats.ActiveWorkflowCount,
            restartCount,
            persistenceStatus);
    }

    private PersistenceStatusDto GetPersistenceStatus()
    {
        if (persistence?.IsEnabled != true)
            return new PersistenceStatusDto(
                false, false, "Disabled", null, null, null, null, null, null,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0);

        var runtime = persistence.Runtime;
        var snapshot = persistence.HealthSnapshot;
        var reconciliation = persistence.Reconciliation;
        var lastRetention = persistence.LastRetentionResult;
        if (snapshot == null)
            return new PersistenceStatusDto(
                true, false, "Starting", persistence.Initialization?.SchemaVersion,
                runtime?.RuntimeId, runtime?.StartedAtUtc, null, null, null,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0);

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
            snapshot.BufferedSearchResultCapacity,
            snapshot.RetryMutationCount,
            snapshot.BusyRetryCount,
            snapshot.DroppedOrdinaryCount,
            snapshot.DroppedProgressCount,
            snapshot.DroppedSearchResultCount,
            snapshot.IncompleteSearchCount,
            snapshot.SuccessfulCommitCount,
            snapshot.RowsWritten,
            persistence.DatabaseSizeBytes,
            persistence.WalSizeBytes,
            snapshot.LastCommitDurationMilliseconds,
            snapshot.LastBatchMutationCount,
            snapshot.PermanentlyFailedMutationCount,
            snapshot.IncompleteSearchTrackingCount,
            snapshot.IncompleteSearchTrackingCapacity,
            snapshot.IncompleteSearchTrackingOverflowed,
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

    public IReadOnlyList<ProfileSummaryDto> GetProfiles()
        => profileCatalog.NamedProfiles
            .Select(profile => new ProfileSummaryDto(
                profile.Name,
                profile.Condition,
                profile.Condition != null,
                profile.HasEngineSettings,
                profile.HasDownloadSettings))
            .OrderBy(profile => profile.Name)
            .ToList();

    private static SoulseekClientStatusDto ToSoulseekClientStatusDto(SoulseekClientStates state)
    {
        var flags = Enum.GetValues<SoulseekClientStates>()
            .Where(flag => flag != SoulseekClientStates.None && state.HasFlag(flag))
            .Select(flag => flag.ToString())
            .ToList();

        bool isConnected = state.HasFlag(SoulseekClientStates.Connected);
        bool isLoggedIn = state.HasFlag(SoulseekClientStates.LoggedIn);

        return new SoulseekClientStatusDto(
            state.ToString(),
            flags,
            isConnected && isLoggedIn);
    }

    public async Task<JobSummaryDto> SubmitExtractJobAsync(
        SubmitExtractJobRequestDto request,
        CancellationToken ct)
    {
        if (request.ArtifactId == null)
        {
            var resolved = ResolveExtractInput(new ExtractJobDraftDto(request.Input, request.InputType), request.Options);
            request = request with { Input = resolved.Input, InputType = resolved.InputType };
            return await SubmitJobAsync(
                JobRequestMapper.CreateExtractJob(request),
                request.Options,
                ct).ConfigureAwait(false);
        }
        if (inputArtifacts == null)
            throw new InputArtifactUnavailableException(
                "Input artifacts are unavailable; ordinary inputs remain available.");

        Guid submissionId = Guid.NewGuid();
        IReadOnlyDictionary<string, Sockseek.Persistence.Planning.ResolvedInputArtifact> inputs =
            await inputArtifacts.RetainAsync([request.ArtifactId], submissionId, ct)
                .ConfigureAwait(false);
        try
        {
            var lease = inputs[request.ArtifactId];
            string inputType = request.InputType
                ?? (string.Equals(
                        Path.GetExtension(lease.Artifact.OriginalName),
                        ".csv",
                        StringComparison.OrdinalIgnoreCase)
                    ? InputType.CSV.ToString()
                    : InputType.List.ToString());
            var job = JobRequestMapper.CreateExtractJob(request with
            {
                Input = lease.Path,
                InputType = inputType,
            });
            job.PlannedSourceRevision = InputArtifactCoordinator.Revision(lease);
            var immutablePatch = new DownloadSettingsPatchDto(
                Extraction: new ExtractionSettingsPatchDto(RemoveTracksFromSource: false));
            SubmissionOptionsDto options = (request.Options ?? new SubmissionOptionsDto()) with
            {
                DownloadSettings = DownloadSettingsPatchDtoMapper.Combine(
                    request.Options?.DownloadSettings,
                    immutablePatch),
            };
            return await SubmitJobAsync(
                job,
                options,
                ct,
                submissionId: submissionId,
                sourceRevision: job.PlannedSourceRevision,
                artifactId: request.ArtifactId).ConfigureAwait(false);
        }
        finally
        {
            // Acceptance stores the extracted input and source revision. Execution
            // and rerun no longer need the upload; failed admission releases it too.
            try
            {
                await inputArtifacts.ReleaseAsync(submissionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ServerLogMessages.SubmissionArtifactReleaseFailed(logger, exception, submissionId);
            }
        }
    }

    public Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateTrackSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSongJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct)
    {
        var job = JobRequestMapper.CreateJobList(request);
        JobRequestMapper.ApplyDraftDownloadSettings(
            job,
            request.Jobs,
            (item, patch) => jobSettingsResolver.SetJobOptions(
                item.Id,
                new SubmissionOptionsDto(DownloadSettings: patch)));
        return SubmitJobAsync(job, request.Options, ct, request.Jobs);
    }

    internal string ResolveUserShareOutputParent(
        string username,
        PeerBrowseDownloadResolution resolution,
        SubmissionOptionsDto? options)
    {
        EnsureShareSubmissionConnection();
        JobList workflow = CreateUserShareWorkflow(username, resolution);
        JobRequestMapper.AssignWorkflowId(workflow, workflow.WorkflowId);
        jobSettingsResolver.SetWorkflowOptions(workflow.WorkflowId, options);
        try
        {
            DownloadSettings settings = jobSettingsResolver.Resolve(defaultDownloadSettings, workflow);
            ValidateExplicitRemoteTransferOverrides(
                workflow,
                options?.DownloadSettings,
                settings);
            RemoteTransferSubmissionPolicy.NormalizeSettings(workflow, settings);
            return settings.Output.ParentDir
                ?? throw new InvalidOperationException("The output parent was not resolved.");
        }
        finally
        {
            jobSettingsResolver.RemoveWorkflowOptions(workflow.WorkflowId);
        }
    }

    public Task<JobSummaryDto> SubmitUserShareDownloadsAsync(
        string username,
        PeerBrowseDownloadResolution resolution,
        SubmissionOptionsDto? options,
        CancellationToken cancellationToken)
    {
        EnsureShareSubmissionConnection();
        JobList workflow = CreateUserShareWorkflow(username, resolution);
        return SubmitJobAsync(workflow, options, cancellationToken);
    }

    private static JobList CreateUserShareWorkflow(
        string username,
        PeerBrowseDownloadResolution resolution)
    {
        username = PeerUsername.Validate(username);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Plans.Count == 0)
            throw new PeerBrowseSelectionException("The selection contains no downloadable public files.");
        return new JobList(
            $"Shares from {username}",
            resolution.Plans.Select(plan =>
                (Job)new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan))));
    }

    private void EnsureShareSubmissionConnection()
    {
        if (SoulseekSession?.IsConnectedAndLoggedIn != true)
            throw new InvalidOperationException("Soulseek is not connected.");
    }

    public ResolveEffectiveSettingsResponseDto ResolveEffectiveSettings(
        ResolveEffectiveSettingsRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Job);
        JobSettingsContext job = JobRequestMapper.CreateSettingsContext(request.Job);
        bool isProvisional = request.Job is ExtractJobDraftDto
        {
            Input: var input,
            ArtifactId: null,
        } && string.IsNullOrWhiteSpace(input)
            || job.Kind == JobSnapshotKind.Search && string.IsNullOrWhiteSpace(job.QueryText);
        DownloadSettingsPatchDto? combinedPatch = DownloadSettingsPatchDtoMapper.Combine(
            request.Options?.DownloadSettings,
            JobRequestMapper.DraftDownloadSettings(request.Job));
        SubmissionOptionsDto requestOptions = (request.Options ?? new SubmissionOptionsDto()) with
        {
            DownloadSettings = combinedPatch,
        };
        JobSettingsCompositionResult result = jobSettingsResolver.ResolveDetailed(job, requestOptions);
        bool containsRemoteTransfer = ValidateDraftSubmissionSettings(
            job,
            request.Job,
            result.Settings,
            requestOptions,
            combinedPatch);
        if (containsRemoteTransfer)
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
        return EffectiveSettingsMapper.ToDto(result, requestOptions.ProfileNames) with
        {
            IsProvisional = isProvisional,
        };
    }

    private bool ValidateDraftSubmissionSettings(
        JobSettingsContext job,
        JobDraftDto draft,
        DownloadSettings effectiveSettings,
        SubmissionOptionsDto submissionOptions,
        DownloadSettingsPatchDto? explicitPatch)
    {
        bool containsRemoteTransfer = RemoteTransferSubmissionPolicy
            .IsOrdinaryRemoteTransfer(job, effectiveSettings);
        if (containsRemoteTransfer)
            RemoteTransferNameFormatPolicy.ApplyInherited(effectiveSettings.Output);

        if (draft is JobListJobDraftDto listDraft)
        {
            foreach (JobDraftDto childDraft in listDraft.Jobs)
            {
                JobSettingsContext child = JobRequestMapper.CreateSettingsContext(childDraft, allowMissingInput: false);
                DownloadSettingsPatchDto? childPatch = JobRequestMapper.DraftDownloadSettings(childDraft);
                SubmissionOptionsDto? childOptions = SubmissionOptionsStore.Merge(
                    submissionOptions,
                    childPatch == null
                        ? null
                        : new SubmissionOptionsDto(DownloadSettings: childPatch));
                DownloadSettings childSettings = jobSettingsResolver.ResolveWithOptions(
                    effectiveSettings,
                    child,
                    childOptions,
                    JobSettingsInheritance.SearchConstraints);
                containsRemoteTransfer |= ValidateDraftSubmissionSettings(
                    child,
                    childDraft,
                    childSettings,
                    submissionOptions,
                    childPatch);
            }
        }

        if (containsRemoteTransfer && explicitPatch != null)
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        return containsRemoteTransfer;
    }

    private async Task<JobSummaryDto> SubmitJobAsync(
        Job job,
        SubmissionOptionsDto? options,
        CancellationToken ct,
        IReadOnlyList<JobDraftDto>? childDrafts = null,
        Guid? submissionId = null,
        SubmissionSourceRevision? sourceRevision = null,
        string? artifactId = null)
    {
        using var activity = SubmissionActivities.StartActivity("submission.accept");
        activity?.SetTag("job.id", job.Id);
        ct.ThrowIfCancellationRequested();

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        jobSettingsResolver.SetWorkflowOptions(job.WorkflowId, options);

        var settings = jobSettingsResolver.Resolve(defaultDownloadSettings, job);

        ValidateExplicitRemoteTransferOverrides(
            job, options?.DownloadSettings, settings, childDrafts: childDrafts);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);

        if (!CanAcceptLoginRequiredJobs() && ContainsLoginRequiredJob(job, settings, jobSettingsResolver))
            throw new ArgumentException("This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");
        activity?.AddEvent(new("prepared"));

        await new JobPlanner(jobSettingsResolver).PrepareResolvedAsync(job, settings, ct).ConfigureAwait(false);
        activity?.AddEvent(new("planned"));
        SubmissionSpecification specification = SubmissionSpecification.Create(job);
        SubmissionIdentity.AssignAccepted(
            job,
            sourceRevision is null ? specification : specification with { SourceRevision = sourceRevision },
            submissionId,
            artifactId: artifactId);
        activity?.AddEvent(new("captured"));
        await QueueAcceptedSubmissionAsync(
            job,
            settings,
            sourceJobId: null,
            ct,
            settingsAreFinal: true).ConfigureAwait(false);
        activity?.AddEvent(new("admitted"));
        JobSummaryDto summary = StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
        activity?.AddEvent(new("receipt-read"));
        return summary;
    }

    private ExtractJobDraftDto ResolveExtractInput(ExtractJobDraftDto draft, SubmissionOptionsDto? options)
    {
        if (!string.IsNullOrWhiteSpace(draft.Input)) return draft;
        DownloadSettings settings = jobSettingsResolver.ResolveDetailed(
            JobRequestMapper.CreateSettingsContext(draft, allowMissingInput: true),
            SubmissionOptionsStore.Merge(options, new SubmissionOptionsDto(DownloadSettings: draft.DownloadSettings))).Settings;
        string? input = settings.Extraction.Input;
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("input is required for extract jobs");
        return draft with
        {
            Input = input,
            InputType = draft.InputType ?? settings.Extraction.InputType.ToString(),
        };
    }

    internal async IAsyncEnumerable<PlannedJobNode> PlanDraftAsync(
        JobDraftDto draft,
        SubmissionOptionsDto? submissionOptions,
        IReadOnlyDictionary<string, SubmissionSourceRevision>? artifactRevisions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft is ExtractJobDraftDto { ArtifactId: null } extract && string.IsNullOrWhiteSpace(extract.Input))
            draft = ResolveExtractInput(extract, submissionOptions);
        Job root = JobRequestMapper.CreateJob(draft);
        if (submissionOptions?.WorkflowId is Guid workflowId)
            root.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);
        AttachArtifactRevisions(root, artifactRevisions);
        var settingsResolver = jobSettingsResolver.CreateScope(root.WorkflowId);
        settingsResolver.SetWorkflowOptions(root.WorkflowId, submissionOptions);
        JobRequestMapper.ApplyDraftDownloadSettings(
            root,
            draft,
            (item, patch) => settingsResolver.SetJobOptions(
                item.Id, new SubmissionOptionsDto(DownloadSettings: patch)));
        DownloadSettings rootSettings = settingsResolver.Resolve(defaultDownloadSettings, root);
        if (RemoteTransferSubmissionPolicy.ValidateOverrides(
            root,
            rootSettings,
            SubmissionOptionsStore.Merge(
                submissionOptions,
                JobRequestMapper.DraftDownloadSettings(draft) is { } rootPatch
                    ? new SubmissionOptionsDto(DownloadSettings: rootPatch) : null)?.DownloadSettings,
            (parent, child, _) => settingsResolver.Resolve(parent, child, JobSettingsInheritance.SearchConstraints),
            (draft as JobListJobDraftDto)?.Jobs))
        {
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
        }
        RemoteTransferSubmissionPolicy.NormalizeSettings(root, rootSettings);
        if (!CanAcceptLoginRequiredJobs() && ContainsLoginRequiredJob(root, rootSettings, settingsResolver))
        {
            throw new ArgumentException(
                "This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");
        }

        var planner = new JobPlanner(settingsResolver);
        await foreach (PlannedJobNode node in planner.PlanResolvedAsync(
            root, rootSettings, cancellationToken).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    private static void AttachArtifactRevisions(
        Job job,
        IReadOnlyDictionary<string, SubmissionSourceRevision>? revisions)
    {
        if (job.ArtifactId is { } artifactId
            && revisions?.TryGetValue(artifactId, out SubmissionSourceRevision? revision) == true)
        {
            job.PlannedSourceRevision = revision;
        }
        if (job is JobList list)
        {
            foreach (Job child in list.Jobs)
                AttachArtifactRevisions(child, revisions);
        }
    }

    internal async Task QueuePlannedSubmissionAsync(
        Job root,
        DownloadSettings rootSettings,
        SubmissionSpecification specification,
        Guid submissionId,
        Guid previewId,
        string commitFingerprint,
        CommitJobPreviewResponseDto receipt,
        CancellationToken cancellationToken)
    {
        SubmissionIdentity.AssignAccepted(
            root,
            specification,
            submissionId: submissionId,
            previewId: previewId);
        await QueueAcceptedSubmissionAsync(
            root,
            rootSettings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true,
            commitFingerprint: commitFingerprint,
            commitReceipt: receipt).ConfigureAwait(false);
    }

    internal DownloadSettings PrepareSearchViewSelectionJob(
        Job job,
        SubmissionOptionsDto? options,
        bool allowSearchViewAlbumFolderOption = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        DownloadSettings settings = jobSettingsResolver.ResolveFollowUp(job, options);
        ValidateExplicitRemoteTransferOverrides(
            job,
            options?.DownloadSettings,
            settings,
            allowSearchViewAlbumFolderOption);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);
        job.PlannedSettings = JobSettingsSnapshot.Capture(settings);
        return settings;
    }

    internal async Task QueueSearchViewSelectionAsync(
        JobList root,
        Guid sourceJobId,
        Guid submissionId,
        string commitFingerprint,
        CommitSearchViewSelectionResponseDto receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Count == 0)
            throw new ArgumentException("A search-view submission requires at least one resolved job.");
        DownloadSettings rootSettings = SearchSettingsBaselines.Create(
            SearchSettingsBaselineKind.Generic);
        root.PlannedSettings = JobSettingsSnapshot.Capture(rootSettings);
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);
        SubmissionSpecification specification = SubmissionSpecification.Create(root);
        SubmissionIdentity.AssignAccepted(
            root,
            specification,
            submissionId: submissionId);
        await QueueAcceptedSubmissionAsync(
            root,
            rootSettings,
            sourceJobId,
            cancellationToken,
            settingsAreFinal: true,
            commitFingerprint: commitFingerprint,
            commitReceipt: receipt).ConfigureAwait(false);
    }

    private void ValidateExplicitRemoteTransferOverrides(
        Job job,
        DownloadSettingsPatchDto? patch,
        DownloadSettings effectiveSettings,
        bool allowSearchViewAlbumFolderOption = false,
        IReadOnlyList<JobDraftDto>? childDrafts = null)
    {
        if (!RemoteTransferSubmissionPolicy.ValidateOverrides(
                job,
                effectiveSettings,
                patch,
                ResolveChildSettings,
                childDrafts,
                allowSearchViewAlbumFolderOption))
            return;

        RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
    }

    private DownloadSettings ResolveChildSettings(
        DownloadSettings parentSettings,
        Job child,
        DownloadSettingsPatchDto? _)
        => jobSettingsResolver.Resolve(
            parentSettings,
            child,
            JobSettingsInheritance.SearchConstraints);

    private static bool ContainsLoginRequiredJob(
        Job job, DownloadSettings settings, IJobSettingsResolver resolver)
    {
        if (job is not JobList list) return settings.NeedLogin;
        foreach (Job child in list.Jobs)
        {
            if (ContainsLoginRequiredJob(child,
                resolver.Resolve(settings, child, JobSettingsInheritance.SearchConstraints), resolver)) return true;
        }
        return false;
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

    public async Task<SubmissionArchiveResponseDto> SetSubmissionArchivedAsync(
        Guid submissionId,
        bool archived,
        CancellationToken cancellationToken)
    {
        if (persistence?.Submissions == null)
            throw new NotSupportedException("Submission archive is unavailable because persistence is disabled or not started.");
        var liveJobs = StateStore.GetJobs(new JobQuery(
            null, null, null, null, IncludeAll: true, SubmissionId: submissionId));
        if (archived)
        {
            bool pending = acceptedRootsAwaitingRegistration.Values.Any(root =>
                root.SubmissionId == submissionId
                && root.LifecycleState != JobLifecycleState.Terminal);
            bool liveActive = liveJobs.Any(job => job.LifecycleState != ServerJobLifecycleState.Terminal);
            if (pending || liveActive)
            {
                return new SubmissionArchiveResponseDto(
                    submissionId,
                    true,
                    0,
                    0,
                    1,
                    [new SubmissionReasonCountDto("nonterminal-jobs", 1)]);
            }
        }
        // Archive needs this submission's terminal metadata, not another
        // workflow's search history. Pending projections remain in StateStore
        // until their durable acknowledgement.
        foreach (var job in liveJobs)
            await persistence.WaitForJobHandoffAsync(job.JobId, cancellationToken, includeSearchResults: false)
                .ConfigureAwait(false);
        var result = await persistence.Submissions
            .SetArchivedAsync(submissionId, archived, cancellationToken)
            .ConfigureAwait(false);
        if (result.RejectedSubmissionCount == 0)
            StateStore.SetSubmissionArchived(submissionId, archived);
        return SubmissionDtoMapper.ToArchiveResponse(result);
    }

    public async Task<JobSummaryDto?> RerunSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        if (persistence?.Submissions == null)
            throw new NotSupportedException("Submission rerun is unavailable because persistence is disabled or not started.");
        var started = System.Diagnostics.Stopwatch.StartNew();
        // Accepted intent is committed before execution starts. Replaying it
        // requires neither terminal history handoffs nor mutable job counts.
        string? retained = await persistence.Submissions
            .GetSpecificationJsonAsync(submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (retained == null)
            return null;
        long readMs = started.ElapsedMilliseconds;

        SubmissionSpecification specification =
            SubmissionSpecificationCodec.Deserialize(retained);
        var (job, settings) = specification.Materialize(defaultDownloadSettings);
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);
        if (settings.NeedLogin && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This retained submission requires Soulseek login, but the server is not configured for it.");

        SubmissionIdentity.AssignAccepted(
            job,
            specification,
            rerunOfSubmissionId: submissionId);
        long materializedMs = started.ElapsedMilliseconds;
        await QueueAcceptedSubmissionAsync(
            job,
            settings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true).ConfigureAwait(false);
        long durationMs = started.ElapsedMilliseconds;
        ServerLogMessages.SubmissionRerunAccepted(logger, submissionId, job.SubmissionId!.Value, durationMs);
        ServerLogMessages.SubmissionRerunTiming(logger, submissionId, job.SubmissionId.Value,
            readMs, materializedMs - readMs, durationMs - materializedMs);
        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    public async Task<JobSummaryDto?> RerunSearchJobAsync(
        Guid sourceJobId,
        CancellationToken cancellationToken)
    {
        if (persistence?.Submissions == null || persistence.JobHistory == null)
            throw new NotSupportedException("Search replay is unavailable because persistence is disabled or not started.");
        var started = System.Diagnostics.Stopwatch.StartNew();
        Guid submissionId;
        // A live or admitted job may not have reached job history yet. Its
        // identity is already authoritative; execution settings still come from
        // the durably accepted command below, never mutable live configuration.
        if (GetRuntimeJob<Job>(sourceJobId) is { } liveSource)
        {
            if (liveSource is not SearchJob || liveSource.SubmissionId is not Guid liveSubmissionId)
                throw new ArgumentException("Only an accepted search job can be replayed.");
            submissionId = liveSubmissionId;
        }
        else
        {
            await persistence.WaitForJobHandoffAsync(sourceJobId, cancellationToken, includeSearchResults: false)
                .ConfigureAwait(false);
            PersistedJob? source = await persistence.JobHistory
                .GetJobAsync(sourceJobId, cancellationToken).ConfigureAwait(false);
            if (source == null)
                return null;
            if (source.Kind != ServerJobKind.Search.ToString() || source.SubmissionId is not Guid retainedSubmissionId)
                throw new ArgumentException("Only an accepted search job can be replayed.");
            submissionId = retainedSubmissionId;
        }

        string? retained = await persistence.Submissions
            .GetSpecificationJsonAsync(submissionId, cancellationToken).ConfigureAwait(false);
        if (retained == null)
            return null;
        long readMs = started.ElapsedMilliseconds;
        SubmissionSpecification accepted = SubmissionSpecificationCodec.Deserialize(retained);
        SubmissionSpecification replay = accepted.ForSearchJob(sourceJobId)
            ?? throw new ArgumentException(
                $"Search {sourceJobId} has no matching planned command in its retained submission.");
        var (restoredJob, settings) = replay.Materialize(defaultDownloadSettings);
        var job = (SearchJob)restoredJob;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);
        if (settings.NeedLogin && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This retained search requires Soulseek login, but the server is not configured for it.");

        SubmissionIdentity.AssignAccepted(
            job,
            replay,
            rerunOfSubmissionId: submissionId);
        long materializedMs = started.ElapsedMilliseconds;
        await QueueAcceptedSubmissionAsync(
            job,
            settings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true).ConfigureAwait(false);
        long durationMs = started.ElapsedMilliseconds;
        ServerLogMessages.SubmissionRerunAccepted(logger, submissionId, job.SubmissionId!.Value, durationMs);
        ServerLogMessages.SubmissionRerunTiming(logger, submissionId, job.SubmissionId.Value,
            readMs, materializedMs - readMs, durationMs - materializedMs);
        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

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

    public JobDetailDto? GetJobDetailByDisplayId(Guid workflowId, int displayId)
        => StateStore.GetJobDetailByDisplayId(workflowId, displayId);

    public IReadOnlyList<SearchRawResultDto>? GetSearchRawResults(Guid jobId, long afterSequence, int limit)
    {
        // Search Views and raw paging share the existing retirement read owner.
        // It remains available until history acknowledges the complete source.
        // Raw observations also exist before prepared settings are available.
        SearchSession? current = GetRuntimeJob<Job>(jobId)?.SearchObservationSession;
        SearchSession? session = persistence?.GetRetiringSearchSource(jobId)?.Session ?? current;
        return session?.RawSnapshot(afterSequence, limit)
            .Select(result => ServerSnapshotMapper.ToSearchRawResultDto(result.ProjectionInput))
            .ToList();
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct)
    {
        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalRetrieveFolderAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var folder = JobRequestMapper.FindAlbumFolderForRetrieval(
            sourceJob,
            request.Folder,
            GetCurrentEngineUserSuccessCounts(sourceJob.WorkflowId),
            request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = new RetrieveFolderJob(folder.DirectoryIdentity)
        {
            ItemName = folder.FolderPath,
            ResultObserver = snapshot => Searcher.ApplyDirectorySnapshot(folder, snapshot),
        };
        retrieveJob.WorkflowId = sourceJob.WorkflowId;
        await QueueAcceptedSubmissionAsync(retrieveJob, sourceJob.Config, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    internal async Task<JobSummaryDto?> StartSearchViewDirectoryRetrievalAsync(
        Guid sourceJobId,
        PeerDirectoryIdentity directory,
        Func<PeerDirectorySnapshot, CancellationToken, Task<int>> resultObserver,
        Guid idempotencyKey,
        string commitFingerprint,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(resultObserver);
        Job? sourceJob = GetRuntimeJob<Job>(sourceJobId);
        var retrieveJob = new RetrieveFolderJob(directory)
        {
            ItemName = directory.FolderPath,
            AsyncResultObserver = resultObserver,
        };
        DownloadSettings settings;
        if (sourceJob?.Config != null)
        {
            retrieveJob.WorkflowId = sourceJob.WorkflowId;
            settings = sourceJob.Config;
        }
        else
        {
            if (persistence?.JobHistory == null)
                return null;
            await persistence.WaitForJobHandoffAsync(sourceJobId, ct).ConfigureAwait(false);
            PersistedJob? retained = await persistence.JobHistory.GetJobAsync(
                sourceJobId,
                ct).ConfigureAwait(false);
            if (retained == null)
                return null;
            retrieveJob.WorkflowId = retained.WorkflowId;
            settings = jobSettingsResolver.ResolveFollowUp(retrieveJob, options: null);
        }
        SubmissionIdentity.AssignAccepted(
            retrieveJob,
            settings,
            submissionId: idempotencyKey);
        retrieveJob.EnsureDisplayId();
        JobSummaryDto receipt = BuildSubmittedJobSummary(retrieveJob, sourceJobId);
        await QueueAcceptedSubmissionAsync(
            retrieveJob,
            settings,
            sourceJobId,
            ct,
            commitFingerprint: commitFingerprint,
            commitReceipt: receipt).ConfigureAwait(false);
        return receipt;
    }

    private async Task<JobSummaryDto?> StartHistoricalRetrieveFolderAsync(
        Guid sourceJobId,
        RetrieveFolderRequestDto request,
        CancellationToken ct)
    {
        var historical = await ResolveHistoricalFolderAsync(sourceJobId, request.Folder, request.AlbumQuery, ct).ConfigureAwait(false);
        if (historical == null)
            return null;
        var retrieveJob = new RetrieveFolderJob(historical.Value.Folder.DirectoryIdentity)
        {
            ItemName = historical.Value.Folder.FolderPath,
            WorkflowId = historical.Value.Job.WorkflowId,
        };
        var settings = jobSettingsResolver.ResolveFollowUp(retrieveJob, options: null);
        await QueueAcceptedSubmissionAsync(retrieveJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct)
    {
        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");
        if (request.RequestedMode == ServerExtractionMode.Album)
            throw new ArgumentException("A file selection cannot be interpreted as an album.");

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalFileDownloadsAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var summaries = new List<JobSummaryDto>();

        if ((request.RequestedMode is null or ServerExtractionMode.Song)
            && sourceJob is SongJob manualSong && manualSong.IsAwaitingSelection)
        {
            if (request.Files.Count != 1)
                throw new ArgumentException("Manual song jobs require exactly one selected file.");

            var candidate = JobRequestMapper.FindFileCandidate(
                sourceJob,
                request.Files[0],
                GetCurrentEngineUserSuccessCounts(sourceJob.WorkflowId));
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            DownloadEngine? engine;
            lock (engineGate)
                engine = currentEngine;
            if (engine?.TryStartManualSongSelection(sourceJobId, candidate,
                song => jobSettingsResolver.ApplyManualSelectionOptions(sourceJob, song, request.Options), out _) != true)
                throw new ArgumentException("The song is no longer awaiting selection.");
            return new List<JobSummaryDto> { StateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong, sourceJobId) };
        }

        foreach (var file in request.Files)
        {
            var candidate = JobRequestMapper.FindFileCandidate(
                sourceJob,
                file,
                GetCurrentEngineUserSuccessCounts(sourceJob.WorkflowId));
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            if ((request.RequestedMode is null or ServerExtractionMode.Song)
                && sourceJob is AggregateJob && sourceJob.IsAwaitingSelection)
            {
                DownloadEngine? engine;
                lock (engineGate)
                    engine = currentEngine;
                if (engine == null || !engine.TryStartManualSongSelection(sourceJobId, candidate,
                    song => jobSettingsResolver.ApplyManualSelectionOptions(sourceJob, song, request.Options), out var selectedSong)
                    || selectedSong == null)
                    throw new ArgumentException("The aggregate is no longer accepting selections.");
                summaries.Add(StateStore.GetJobSummary(selectedSong.Id) ?? BuildSubmittedJobSummary(selectedSong, sourceJobId));
                continue;
            }

            Job followUpJob = JobRequestMapper.CreateFileSelectionFollowUp(
                sourceJob,
                candidate,
                request.RequestedMode?.ToCore());

            var followUpSettings = jobSettingsResolver.ResolveFollowUp(followUpJob, request.Options);
            summaries.Add(await SubmitFollowUpJobAsync(sourceJobId, sourceJob, followUpJob, followUpSettings, request.Options, isolateOptions: true, ct));
        }

        return summaries;
    }

    private async Task<IReadOnlyList<JobSummaryDto>?> StartHistoricalFileDownloadsAsync(
        Guid sourceJobId,
        StartFileDownloadsRequestDto request,
        CancellationToken ct)
    {
        if (persistence?.JobHistory == null || persistence.SearchHistory == null)
            return null;

        await persistence.WaitForJobHandoffAsync(sourceJobId, ct).ConfigureAwait(false);

        var persistedJob = await persistence.JobHistory.GetJobAsync(sourceJobId, ct).ConfigureAwait(false);
        if (persistedJob == null)
            return null;

        var summaries = new List<JobSummaryDto>(request.Files.Count);
        foreach (var file in request.Files)
        {
            PersistedSearchResultLookup? lookup;
            try
            {
                lookup = await persistence.SearchHistory
                    .GetResultAsync(sourceJobId, file.Username, file.Filename, ct)
                    .ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("The retained result has invalid file-attribute data and cannot be downloaded safely.", exception);
            }
            if (lookup == null)
                throw new ArgumentException("The historical job has no retained search-result history.");
            if (lookup.Result == null)
            {
                string detail = lookup.Metadata.ResultPersistenceState switch
                {
                    "Pruned" => "Its raw results were pruned by retention.",
                    "Incomplete" => "Its persisted raw results are incomplete.",
                    "Interrupted" => "The search was interrupted and the requested result was not committed.",
                    "NotPersisted" => "Its raw results were not persisted.",
                    _ => "The requested result was not found.",
                };
                throw new ArgumentException($"Cannot start a download from this historical search. {detail}");
            }

            var candidate = lookup.Result.ToFileCandidate();
            Job followUp = request.RequestedMode == ServerExtractionMode.General
                ? new RemoteFileJob(candidate.Target)
                : new SongJob(Searcher.InferSongQuery(
                    candidate.Filename,
                    new SongQuery { Title = lookup.Metadata.Query }))
                {
                    ResolvedTarget = candidate,
                };
            followUp.ItemName = persistedJob.ItemName;
            followUp.WorkflowId = persistedJob.WorkflowId;
            var settings = jobSettingsResolver.ResolveFollowUp(followUp, request.Options);
            ValidateExplicitRemoteTransferOverrides(
                followUp,
                request.Options?.DownloadSettings ?? new DownloadSettingsPatchDto(),
                settings);
            RemoteTransferSubmissionPolicy.NormalizeSettings(followUp, settings);
            jobSettingsResolver.SetIsolatedJobOptions(followUp.Id, request.Options);
            await QueueAcceptedSubmissionAsync(followUp, settings, sourceJobId, ct).ConfigureAwait(false);
            summaries.Add(StateStore.GetJobSummary(followUp.Id) ?? BuildSubmittedJobSummary(followUp, sourceJobId));
        }

        return summaries;
    }

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid sourceJobId, StartFolderDownloadRequestDto request, CancellationToken ct)
    {
        if (request.RequestedMode == ServerExtractionMode.Song)
            throw new ArgumentException("A directory selection cannot be interpreted as one song.");
        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalFolderDownloadAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var folder = JobRequestMapper.FindAlbumFolder(
            sourceJob,
            request.Folder,
            GetCurrentEngineUserSuccessCounts(sourceJob.WorkflowId),
            request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        if (request.RequestedMode == ServerExtractionMode.General)
        {
            var directoryJob = JobRequestMapper.CreateRemoteDirectoryDownload(folder, request.Selection);
            directoryJob.ItemName = sourceJob.ItemName;
            var directorySettings = jobSettingsResolver.ResolveFollowUp(directoryJob, request.Options);
            return await SubmitFollowUpJobAsync(
                sourceJobId, sourceJob, directoryJob, directorySettings,
                request.Options, isolateOptions: true, ct);
        }

        AlbumQuery? albumQuery = JobRequestMapper.ResolveFolderSelectionQuery(
            sourceJob,
            request.AlbumQuery);

        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        bool manualSelection = sourceJob.IsAwaitingSelection && sourceJob is AlbumJob or AlbumAggregateJob;
        if (engine?.TryStartManualAlbumSelection(
            sourceJobId,
            folder,
            albumQuery,
            album =>
            {
                JobRequestMapper.ApplyFolderDownloadSelection(album, request.Selection);
                jobSettingsResolver.ApplyManualSelectionOptions(sourceJob, album, request.Options);
            },
            out var selectedAlbum) == true)
        {
            return StateStore.GetJobSummary(selectedAlbum!.Id) ?? BuildSubmittedJobSummary(selectedAlbum!, sourceJobId);
        }
        if (manualSelection)
            throw new ArgumentException("The job is no longer accepting selections.");
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from this job require an album query.");

        AlbumJob albumJob = JobRequestMapper.CreateAlbumSelectionFollowUp(
            sourceJob,
            folder,
            albumQuery,
            request.Selection);

        var followUpSettings = jobSettingsResolver.ResolveFollowUp(albumJob, request.Options);

        return await SubmitFollowUpJobAsync(sourceJobId, sourceJob, albumJob, followUpSettings, request.Options, isolateOptions: true, ct);
    }

    private async Task<JobSummaryDto?> StartHistoricalFolderDownloadAsync(
        Guid sourceJobId,
        StartFolderDownloadRequestDto request,
        CancellationToken ct)
    {
        var historical = await ResolveHistoricalFolderAsync(sourceJobId, request.Folder, request.AlbumQuery, ct).ConfigureAwait(false);
        if (historical == null)
            return null;
        var folder = historical.Value.Folder;
        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);
        if (request.RequestedMode == ServerExtractionMode.General)
        {
            var directoryJob = JobRequestMapper.CreateRemoteDirectoryDownload(folder, request.Selection);
            directoryJob.ItemName = historical.Value.Job.ItemName;
            directoryJob.WorkflowId = historical.Value.Job.WorkflowId;
            var directorySettings = jobSettingsResolver.ResolveFollowUp(directoryJob, request.Options);
            ValidateExplicitRemoteTransferOverrides(
                directoryJob,
                request.Options?.DownloadSettings ?? new DownloadSettingsPatchDto(),
                directorySettings);
            RemoteTransferSubmissionPolicy.NormalizeSettings(directoryJob, directorySettings);
            jobSettingsResolver.SetIsolatedJobOptions(directoryJob.Id, request.Options);
            await QueueAcceptedSubmissionAsync(directoryJob, directorySettings, sourceJobId, ct).ConfigureAwait(false);
            return StateStore.GetJobSummary(directoryJob.Id) ?? BuildSubmittedJobSummary(directoryJob, sourceJobId);
        }
        var albumJob = new AlbumJob(new AlbumQuery(historical.Value.Query))
        {
            ResolvedTarget = folder,
            ItemName = historical.Value.Job.ItemName,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
            WorkflowId = historical.Value.Job.WorkflowId,
        };
        JobRequestMapper.ApplyFolderDownloadSelection(albumJob, request.Selection);
        var settings = jobSettingsResolver.ResolveFollowUp(albumJob, request.Options);
        jobSettingsResolver.SetIsolatedJobOptions(albumJob.Id, request.Options);
        await QueueAcceptedSubmissionAsync(albumJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(albumJob.Id) ?? BuildSubmittedJobSummary(albumJob, sourceJobId);
    }

    private async Task<(PersistedJob Job, AlbumFolder Folder, AlbumQuery Query)?> ResolveHistoricalFolderAsync(
        Guid sourceJobId,
        AlbumFolderRefDto folderRef,
        AlbumQueryDto? requestedQuery,
        CancellationToken ct)
    {
        if (persistence?.JobHistory == null || persistence.SearchHistory == null)
            return null;
        await persistence.WaitForJobHandoffAsync(sourceJobId, ct).ConfigureAwait(false);
        var job = await persistence.JobHistory.GetJobAsync(sourceJobId, ct).ConfigureAwait(false);
        var metadata = await persistence.SearchHistory.GetMetadataAsync(sourceJobId, ct).ConfigureAwait(false);
        if (job == null || metadata == null)
            return null;
        if (metadata.ResultPersistenceState is "Pruned" or "NotPersisted")
            throw new ArgumentException($"Cannot use this historical folder because its result data is {metadata.ResultPersistenceState.ToLowerInvariant()}.");
        var defaultProjection = HistoricalJobDtoMapper.DefaultFolderProjection(job);
        var query = requestedQuery != null
            ? JobRequestMapper.ToAlbumQuery(requestedQuery)
            : defaultProjection?.Query
                ?? throw new ArgumentException("Historical folder operations require an album query.");
        // Album folders combine only directories belonging to this exact peer.
        // Match live/local execution eligibility: locked observations belong to
        // read-only Views, not a resolved download target.
        IReadOnlyList<SearchProjectionInput> inputs = await persistence.SearchHistory
            .GetFolderResultsAsync(sourceJobId, new(folderRef.Username, folderRef.FolderPath),
                query, SearchResultVisibility.Public, ct).ConfigureAwait(false);
        SearchDefinition definition = await HistoricalJobDtoMapper.SearchDefinitionAsync(
            persistence.Submissions,
            job,
            ct).ConfigureAwait(false);
        var folder = SearchResultProjector.AlbumFolders(
                inputs,
                query,
                definition.ProjectionSettings.ToSettings(),
                new ConcurrentDictionary<string, int>(StringComparer.Ordinal))
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));
        if (folder == null)
        {
            string detail = metadata.ResultPersistenceState is "Incomplete" or "Interrupted"
                ? $" Persisted results are {metadata.ResultPersistenceState.ToLowerInvariant()}."
                : "";
            throw new ArgumentException("Requested folder was not found in retained search results." + detail);
        }
        return (job, folder, query);
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
            jobSettingsResolver,
            directorySource: PeerBrowses,
            loggerFactory: loggerFactory,
            retireTerminalWorkflows: retireTerminalWorkflows);
        persistence?.AttachEngine(engine);
        StateStore.AttachEngine(engine);
        // A queued root is already readable, but its runtime owner remains here
        // until the engine actually registers it (not merely a snapshot update).
        engine.Events.JobRegistered += change =>
            acceptedRootsAwaitingRegistration.TryRemove(change.Job.Id, out _);
        lock (engineGate)
            currentEngine = engine;
        StateStore.UpdateDaemonRuntime(ToSoulseekClientStatusDto(clientManager.State), restartCount);
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
        if (acceptedRootsAwaitingRegistration.TryGetValue(jobId, out Job? accepted))
            return accepted as TJob;

        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.GetJob(jobId) as TJob;
    }

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => ServerSnapshotMapper.ToSubmittedJobSummary(job, sourceJobId);

    private bool CanAcceptLoginRequiredJobs()
        => !string.IsNullOrWhiteSpace(engineSettings.MockFilesDir)
        || engineSettings.UseRandomLogin
        || (!string.IsNullOrWhiteSpace(engineSettings.Username)
            && !string.IsNullOrWhiteSpace(engineSettings.Password));

    private async Task<JobSummaryDto> SubmitFollowUpJobAsync(
        Guid sourceJobId,
        Job sourceJob,
        Job followUpJob,
        DownloadSettings settings,
        SubmissionOptionsDto? options,
        bool isolateOptions,
        CancellationToken ct)
    {
        followUpJob.WorkflowId = sourceJob.WorkflowId;
        JobRequestMapper.PropagateSourceMutationToFollowUp(sourceJob, followUpJob);
        if (isolateOptions)
            jobSettingsResolver.SetIsolatedJobOptions(followUpJob.Id, options);
        ValidateExplicitRemoteTransferOverrides(
            followUpJob,
            options?.DownloadSettings,
            settings);
        RemoteTransferSubmissionPolicy.NormalizeSettings(followUpJob, settings);
        await QueueAcceptedSubmissionAsync(followUpJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId);
    }

    private async Task QueueAcceptedSubmissionAsync(
        Job job,
        DownloadSettings settings,
        Guid? sourceJobId,
        CancellationToken cancellationToken,
        bool settingsAreFinal = false,
        string? commitFingerprint = null,
        object? commitReceipt = null)
    {
        using var activity = SubmissionActivities.StartActivity("submission.queue");
        activity?.SetTag("job.id", job.Id);
        if (job.SubmissionId == null)
            SubmissionIdentity.AssignAccepted(job, settings);
        activity?.AddEvent(new("identity-retained"));
        if (persistence?.Submissions != null)
        {
            await persistence.Submissions.CreateAsync(
                new SubmissionRegistration(
                    job.SubmissionId!.Value,
                    job.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                    SubmissionSpecification.CurrentSchemaVersion,
                    job.SubmissionSpecificationJson
                        ?? throw new InvalidOperationException("An accepted submission has no retained specification."),
                    job.RerunOfSubmissionId,
                    job.PreviewId,
                    job.ArtifactId,
                    commitFingerprint,
                    commitReceipt == null ? null : SubmissionCommitCoordinator.SerializeReceipt(commitReceipt)),
                cancellationToken).ConfigureAwait(false);
        }
        activity?.AddEvent(new("persisted"));
        // Acceptance and its retry receipt are now committed together, before
        // runtime work can enqueue unrelated history writes or observe the root.
        // Materialized Review/rerun roots do not revisit the planner. Publish
        // their final settings with the pending owner so an immediate View
        // request can use the live session before engine registration.
        if (settingsAreFinal)
            job.Config ??= settings;
        acceptedRootsAwaitingRegistration[job.Id] = job;
        job.EnsureDisplayId();
        var accepted = CoreSnapshotFactory.CreateJob(job, revision: 0);
        activity?.AddEvent(new("snapshot-created"));
        StateStore.RegisterAcceptedRoot(accepted, sourceJobId);
        activity?.AddEvent(new("registered"));
        await submissionChannel.Writer.WriteAsync(
            new QueuedSubmission(
                job,
                settings,
                SourceJobId: sourceJobId,
                SettingsAreFinal: settingsAreFinal),
            CancellationToken.None).ConfigureAwait(false);
        activity?.AddEvent(new("enqueued"));
    }

    private sealed record QueuedSubmission(
        Job Job,
        DownloadSettings Settings,
        Guid? SourceJobId = null,
        bool SettingsAreFinal = false);
}
