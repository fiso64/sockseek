using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core.Chat;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Core.UserProfiles;
using Sockseek.Persistence.PeerBrowsing;
using Sockseek.Server.PeerBrowsing;
using Sockseek.Server.Persistence;
using Sockseek.Server.UserProfiles;
using Soulseek;

namespace Sockseek.Server;

/// <summary>
/// One daemon lifetime for the Soulseek session and its services. Download-engine
/// restarts run inside this lifetime and never recreate chat, shares or peer readers.
/// </summary>
public sealed class DaemonRuntime
{
    private readonly ServerOptions options;
    private readonly EngineSettings engineSettings;
    private EngineSettings? appliedConfiguration;
    private readonly EngineStateStore StateStore;
    private readonly PersistenceCoordinator? persistence;
    private readonly ILoggerFactory loggerFactory;
    private readonly PeerRestrictionPolicy? peerRestrictions;
    private readonly Func<int> restartCount;

    public SharingRuntime? Sharing { get; private set; }
    public ChatRuntime? Chat { get; private set; }
    public DaemonSoulseekRuntime? SoulseekRuntime { get; private set; }
    public SoulseekClientManager? SoulseekSession { get; private set; }
    public PeerBrowseService? PeerBrowses { get; private set; }
    public UserPresenceService? UserPresence { get; private set; }
    public UserProfileService? UserProfiles { get; private set; }

    internal DaemonRuntime(ServerOptions options, EngineSettings engineSettings,
        EngineStateStore stateStore, PersistenceCoordinator? persistence,
        ILoggerFactory loggerFactory, PeerRestrictionPolicy? peerRestrictions, Func<int> restartCount)
    {
        this.options = options;
        this.engineSettings = engineSettings;
        appliedConfiguration = engineSettings;
        StateStore = stateStore;
        this.persistence = persistence;
        this.loggerFactory = loggerFactory;
        this.peerRestrictions = peerRestrictions;
        this.restartCount = restartCount;
    }

    internal async Task RunAsync(CancellationToken ct, Action? onInitialized,
        Func<SoulseekClientManager, CancellationToken, Task> supervise)
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
        UserPresenceService? userPresence = null;
        void OnClientStateChanged(SoulseekClientStates state)
        {
            StateStore.UpdateDaemonRuntime(
                ToSoulseekClientStatusDto(state),
                restartCount());
            userProfiles?.OnSoulseekStateChanged(state);
            userPresence?.OnSoulseekStateChanged(state);
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
        void OnTerminalTransfersCommitted(IReadOnlyList<(Guid TransferId, long Revision)> committed)
        {
            foreach (Guid id in StateStore.RetireCommittedTransfers(committed))
                sharing.Uploads.Forget(id);
        }
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
            if (persistence?.IsStarted != true && transfer.State is (UploadTransferState.Completed
                or UploadTransferState.Cancelled
                or UploadTransferState.Failed
                or UploadTransferState.Interrupted))
            {
                StateStore.RemoveTerminalTransfer(transfer.TransferId);
                sharing.Uploads.Forget(transfer.TransferId);
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
        if (persistence is not null)
        {
            persistence.HistoryHealthChanged += OnHistoryHealthChanged;
            persistence.TerminalTransfersCommitted += OnTerminalTransfersCommitted;
            persistence.TransfersPruned += StateStore.MarkTransferHistoryChanged;
            persistence.ChatRetentionCompleted += OnChatRetentionCompleted;
        }
        StateStore.SetTransferPersistence(persistence?.CreateTransferPersistence());
        sharing.Uploads.TransferChanged += OnUploadChanged;
        foreach (UploadTransferSnapshot transfer in sharing.Uploads.Snapshot())
            OnUploadChanged(transfer);
        try
        {
            userPresence = new UserPresenceService(() => soulseek.ClientManager.Client,
                soulseek.EnsureStartedAsync, loggerFactory.CreateLogger<UserPresenceService>());
            userPresence.OnSoulseekStateChanged(sharing.ClientManager.State);
            UserPresence = userPresence;
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
            await supervise(sharing.ClientManager, ct).ConfigureAwait(false);
        }
        finally
        {
            UserPresence = null;
            if (userPresence is not null) await userPresence.DisposeAsync();
            userPresence = null;
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
            StateStore.SetTransferPersistence(null);
            if (persistence is not null)
            {
                persistence.HistoryHealthChanged -= OnHistoryHealthChanged;
                persistence.TerminalTransfersCommitted -= OnTerminalTransfersCommitted;
                persistence.TransfersPruned -= StateStore.MarkTransferHistoryChanged;
                persistence.ChatRetentionCompleted -= OnChatRetentionCompleted;
            }
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

    }

    /// <summary>Apply supported changes through the existing feature owners. Calls are serialized by the config watcher.</summary>
    public async Task ReloadConfigurationAsync(EngineSettings candidate, WebUiOptions webUi,
        CancellationToken cancellationToken = default)
    {
        // Keep restart-only values at startup even when their enclosing group
        // also contains reloadable settings. The attribute controls actual input
        // to the owners as well as the restart notice.
        var active = SettingsCloner.Clone(engineSettings);
        var schema = SettingsSchema.For(typeof(EngineSettings));
        new SettingsPatch((SettingsPatch.Difference(engineSettings, candidate)?.Operations ?? [])
            .Where(change => schema.GetMember(change.Path).Reloadable).ToArray()).ApplyTo(active);
        if (appliedConfiguration is null || !SettingsSchema.ValuesEqual(appliedConfiguration, active))
        {
            SharingRuntime sharing = Sharing
                ?? throw new InvalidOperationException("The daemon runtime is not running.");
            // A failed network operation may have partially applied; retry the
            // owners even if the next edit restores the previous configuration.
            appliedConfiguration = null;
            await sharing.ReloadConfigurationAsync(active, cancellationToken).ConfigureAwait(false);
            sharing.Restrictions.ReloadConfigured(active.PeerRestrictions);
            appliedConfiguration = active;
        }
        options.WebUi = webUi;
    }

    internal static SoulseekClientStatusDto ToSoulseekClientStatusDto(SoulseekClientStates state)
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

}
