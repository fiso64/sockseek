using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Chat;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Sharing;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Chat;
using Soulseek;

namespace Sockseek.Server;

/// <summary>
/// Owns chat message acceptance, persistence/ACK ordering and the feature lifetime.
/// One ingress queue preserves ordering with room events; ChatRooms owns sessions
/// and rosters. Both use the existing chat store and this runtime's shutdown.
/// </summary>
public sealed class ChatRuntime : IAsyncDisposable
{
    internal const int IngressCapacity = 4_096;
    // Amortize SQLite commits while bounding transaction and private-message
    // acknowledgement latency on modest homeserver storage.
    private const int MaximumIngressPersistenceBatchSize = 16;

    private readonly EngineSettings settings;
    private readonly DaemonSoulseekRuntime soulseek;
    private readonly ChatPersistenceStore store;
    private readonly ILogger<ChatRuntime> logger;
    private readonly FeatureHealthLogger healthLog;
    private readonly RepeatedWarningGate ingressCapacityWarningGate = new();
    private readonly Channel<IngressItem> ingress;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim summaryMutationGate = new(1, 1);
    private readonly Lock stateGate = new();
    private readonly Dictionary<ISoulseekClient, byte> attachedClients = new(ReferenceEqualityComparer.Instance);
    private Task? worker;
    private ChatRuntimeStateDto state = new(
        DaemonFeatureState.Starting, "Starting", 0, 0, 0, 0, 0);
    private NotificationSummaryDto notificationSummary = new(0, 0);
    private long revision;
    private int peakIngressDepth;
    private int disposeState;

    public ChatRooms Rooms { get; }

    internal int PeakIngressDepth => Volatile.Read(ref peakIngressDepth);
    internal PeerRestrictionPolicy Restrictions => soulseek.Restrictions;

    public ChatRuntime(
        EngineSettings settings,
        DaemonSoulseekRuntime soulseek,
        ChatPersistenceStore store,
        ILogger<ChatRuntime>? logger = null)
        : this(settings, soulseek, store, IngressCapacity, logger)
    {
    }

    internal ChatRuntime(
        EngineSettings settings,
        DaemonSoulseekRuntime soulseek,
        ChatPersistenceStore store,
        int ingressCapacity,
        ILogger<ChatRuntime>? logger = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.soulseek = soulseek ?? throw new ArgumentNullException(nameof(soulseek));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.logger = logger ?? NullLogger<ChatRuntime>.Instance;
        healthLog = new FeatureHealthLogger(this.logger, "chat");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ingressCapacity);
        Rooms = new ChatRooms(settings, soulseek, store, this.logger);
        Rooms.StateChanged += PublishCurrentSummary;
        Rooms.TargetChanged += PublishTarget;
        ingress = Channel.CreateBounded<IngressItem>(new BoundedChannelOptions(ingressCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        // Local construction attaches ingress before any daemon API can start
        // login. StartAsync may wait for the network; queued server messages
        // must already have a receiver while that startup is pending.
        soulseek.ClientManager.ClientCreated += AttachClient;
        soulseek.ClientManager.StateChanged += OnClientStateChanged;
        if (soulseek.ClientManager.Client is { } existing)
            AttachClient(existing);
    }

    public event Action<ChatRuntimeStateDto, NotificationSummaryDto>? StateChanged;
    public event Action<ChatMessageRecord>? MessageCommitted;
    public event Action<UserNotificationRecord>? NotificationCommitted;
    public event Action<ChatTargetDeltaDto>? TargetChanged;

    public ChatRuntimeStateDto GetState()
    {
        lock (stateGate)
            return state;
    }

    public NotificationSummaryDto GetNotificationSummary()
    {
        lock (stateGate)
            return notificationSummary;
    }

    public ConversationSummaryDto MapConversation(ConversationRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ChatDtoMapper.ToDto(
            value,
            soulseek.Restrictions.IsPrivateMessageBlocked(value.DisplayUsername));
    }

    public async Task PublishPeerRestrictionsChangedAsync(
        string username,
        CancellationToken cancellationToken)
    {
        username = PeerUsername.Validate(username);
        ConversationRecord? conversation = await store.GetConversationByPeerAsync(
            Account,
            username,
            cancellationToken).ConfigureAwait(false);
        if (conversation is not null)
            await PublishConversationAsync(
                conversation.ConversationId,
                message: null,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (worker is not null)
            return;

        try
        {
            await soulseek.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await Rooms.ReconnectAsync(cancellationToken).ConfigureAwait(false);
            await RefreshSummaryAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetHealth(DaemonFeatureState.Degraded, ChatLogMessages.CompactFailure(ex));
            ChatLogMessages.StartupFailed(logger, ex);
        }
        finally
        {
            worker ??= Task.Run(() => RunIngressAsync(lifetime.Token), CancellationToken.None);
        }
    }

    public Task<ChatPage<ConversationRecord>> GetConversationsAsync(
        bool? unread, bool? archived, string? cursor, int limit, CancellationToken cancellationToken)
        => store.GetConversationsAsync(Account, unread, archived, cursor, limit, cancellationToken);

    public Task<ConversationRecord?> GetConversationAsync(Guid id, CancellationToken cancellationToken)
        => store.GetConversationAsync(Account, id, cancellationToken);

    public Task<ConversationRecord?> GetConversationByPeerAsync(string username, CancellationToken cancellationToken)
        => store.GetConversationByPeerAsync(Account, username, cancellationToken);

    public Task<ChatPage<ChatMessageRecord>> GetMessagesAsync(
        Guid targetId, string? cursor, int limit, CancellationToken cancellationToken)
        => store.GetMessagesAsync(Account, targetId, cursor, limit, cancellationToken);

    public Task<ChatTargetSnapshotDto?> GetConversationSnapshotAsync(
        Guid conversationId, CancellationToken cancellationToken)
        => GetTargetSnapshotAsync(ChatTargetKind.Direct, conversationId, cancellationToken);

    public Task<ChatTargetSnapshotDto?> GetRoomSnapshotAsync(
        Guid roomId, CancellationToken cancellationToken)
        => GetTargetSnapshotAsync(ChatTargetKind.Room, roomId, cancellationToken);

    public async Task<ChatMessageRecord> SendPrivateMessageAsync(
        string username, Guid messageId, string text, CancellationToken cancellationToken)
    {
        username = username.Trim();
        ChatIdentity.ValidateUsername(username);
        text = ChatIdentity.ValidateMessage(text);
        EnsureReady();
        string account = Account;
        ISoulseekClient client = Client;
        var prepared = await store.PrepareOutgoingPrivateMessageAsync(
            account, username, messageId, text, cancellationToken).ConfigureAwait(false);
        return await CompleteSendAsync(
            prepared,
            account,
            () => client.SendPrivateMessageAsync(username, text, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatMessageRecord> SendConversationMessageAsync(
        Guid conversationId, Guid messageId, string text, CancellationToken cancellationToken)
    {
        var conversation = await store.GetConversationAsync(Account, conversationId, cancellationToken).ConfigureAwait(false)
                           ?? throw new KeyNotFoundException("The conversation was not found.");
        return await SendPrivateMessageAsync(
            conversation.DisplayUsername, messageId, text, cancellationToken).ConfigureAwait(false);
    }

    public Task MarkConversationReadAsync(Guid id, Guid throughMessageId, CancellationToken cancellationToken)
        => MarkTargetReadAsync(ChatTargetKind.Direct, id, throughMessageId, cancellationToken);

    public async Task ArchiveConversationAsync(Guid id, bool archived, CancellationToken cancellationToken)
    {
        await store.ArchiveConversationAsync(Account, id, archived, cancellationToken).ConfigureAwait(false);
        await PublishConversationAsync(id, null, CancellationToken.None).ConfigureAwait(false);
        PublishCurrentSummary();
    }

    public Task DeleteConversationHistoryAsync(Guid id, CancellationToken cancellationToken)
        => DeleteTargetHistoryAsync(ChatTargetKind.Direct, id, cancellationToken);

    public async Task<ChatMessageRecord> SendRoomMessageAsync(
        Guid roomId, Guid messageId, string text, CancellationToken cancellationToken)
    {
        text = ChatIdentity.ValidateMessage(text);
        EnsureReady();
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The room was not found.");
        Rooms.EnsureJoined(room.RoomKey);
        string account = Account;
        ISoulseekClient client = Client;
        var prepared = await store.PrepareOutgoingRoomMessageAsync(
            account, roomId, messageId, text, cancellationToken).ConfigureAwait(false);
        return await CompleteSendAsync(
            prepared,
            account,
            () => client.SendRoomMessageAsync(room.DisplayName, text, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public Task MarkRoomReadAsync(Guid id, Guid throughMessageId, CancellationToken cancellationToken)
        => MarkTargetReadAsync(ChatTargetKind.Room, id, throughMessageId, cancellationToken);

    public Task DeleteRoomHistoryAsync(Guid id, CancellationToken cancellationToken)
        => DeleteTargetHistoryAsync(ChatTargetKind.Room, id, cancellationToken);

    private async Task<ChatTargetSnapshotDto?> GetTargetSnapshotAsync(
        ChatTargetKind kind,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        ConversationSummaryDto? conversation = null;
        ChatRoomSummaryDto? room = null;
        if (kind == ChatTargetKind.Direct)
        {
            ConversationRecord? record = await store.GetConversationAsync(
                Account, targetId, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return null;
            conversation = MapConversation(record);
        }
        else
        {
            RoomSubscriptionRecord? record = await store.GetRoomAsync(
                Account, targetId, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return null;
            room = Rooms.MapRoom(record);
        }

        ChatPage<ChatMessageRecord> messages = await store.GetMessagesAsync(
            Account,
            targetId,
            cursor: null,
            ChatProtocol.LiveMessageTailSize,
            cancellationToken).ConfigureAwait(false);
        return new ChatTargetSnapshotDto(
            kind.ToServer(),
            targetId,
            conversation,
            room,
            messages.Items.Select(ChatDtoMapper.ToDto).ToArray(),
            messages.NextCursor is not null);
    }

    private async Task MarkTargetReadAsync(
        ChatTargetKind kind,
        Guid targetId,
        Guid throughMessageId,
        CancellationToken cancellationToken)
    {
        await summaryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (kind == ChatTargetKind.Direct)
                await store.MarkConversationReadAsync(
                    Account, targetId, throughMessageId, cancellationToken).ConfigureAwait(false);
            else
                await store.MarkRoomReadAsync(
                    Account, targetId, throughMessageId, cancellationToken).ConfigureAwait(false);
            await PublishTargetAsync(kind, targetId, null, CancellationToken.None).ConfigureAwait(false);
            await RefreshSummaryAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            summaryMutationGate.Release();
        }
    }

    private async Task DeleteTargetHistoryAsync(
        ChatTargetKind kind,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        await summaryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.DeleteHistoryAsync(
                Account, targetId, kind, cancellationToken).ConfigureAwait(false);
            await PublishTargetReplacementAsync(
                kind, targetId, CancellationToken.None).ConfigureAwait(false);
            await RefreshSummaryAsync(null, CancellationToken.None).ConfigureAwait(false);
            ChatLogMessages.HistoryDeleted(
                logger, kind == ChatTargetKind.Direct ? "direct" : "room", targetId);
        }
        finally
        {
            summaryMutationGate.Release();
        }
    }

    public async Task PublishRetentionAsync(
        ChatRetentionResult result,
        CancellationToken cancellationToken)
    {
        await summaryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (ChatRetentionTarget target in result.AffectedTargets)
                await PublishTargetReplacementAsync(
                    target.Kind, target.TargetId, CancellationToken.None).ConfigureAwait(false);
            await RefreshSummaryAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            summaryMutationGate.Release();
        }
    }

    public Task<ChatPage<UserNotificationRecord>> GetNotificationsAsync(
        bool? unread, ServerUserNotificationKind? kind, string? cursor, int limit, CancellationToken cancellationToken)
        => store.GetNotificationsAsync(Account, unread, kind?.ToCore(), cursor, limit, cancellationToken);

    public Task<UserNotificationRecord?> GetNotificationAsync(Guid id, CancellationToken cancellationToken)
        => store.GetNotificationAsync(Account, id, cancellationToken);

    public async Task MarkNotificationsReadAsync(
        long? throughSequence, IReadOnlyCollection<Guid>? ids, CancellationToken cancellationToken)
    {
        await summaryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.MarkNotificationsReadAsync(Account, throughSequence, ids, cancellationToken).ConfigureAwait(false);
            await RefreshSummaryAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            summaryMutationGate.Release();
        }
    }

    private string Account => soulseek.ClientManager.LoggedInUsername
        ?? settings.Username
        ?? throw new InvalidOperationException("Chat is unavailable before Soulseek login.");

    private ISoulseekClient Client => soulseek.ClientManager.Client
        ?? throw new InvalidOperationException("The Soulseek client is unavailable.");

    private bool IsCurrentAccount(string account)
    {
        string? current = soulseek.ClientManager.LoggedInUsername;
        return current is not null
               && string.Equals(
                   ChatIdentity.ValidateAccount(current),
                   ChatIdentity.ValidateAccount(account),
                   StringComparison.Ordinal);
    }

    private void EnsureReady()
    {
        if (!soulseek.ClientManager.IsConnectedAndLoggedIn)
            throw new InvalidOperationException("Chat is unavailable while Soulseek is disconnected.");
    }

    private void AttachClient(ISoulseekClient client)
    {
        lock (stateGate)
        {
            if (!attachedClients.TryAdd(client, 0))
                return;
        }
        client.PrivateMessageReceived += OnPrivateMessageReceived;
        client.RoomMessageReceived += OnRoomMessageReceived;
        client.RoomJoined += OnRoomJoined;
        client.RoomLeft += OnRoomLeft;
        client.PrivateRoomMembershipAdded += OnPrivateRoomChanged;
        client.PrivateRoomMembershipRemoved += OnPrivateRoomChanged;
        client.PrivateRoomModerationAdded += OnPrivateRoomModerationAdded;
        client.PrivateRoomModerationRemoved += OnPrivateRoomModerationRemoved;
        client.PrivateRoomUserListReceived += OnPrivateRoomInfoChanged;
        client.PrivateRoomModeratedUserListReceived += OnPrivateRoomInfoChanged;
    }

    private void DetachClient(ISoulseekClient client)
    {
        client.PrivateMessageReceived -= OnPrivateMessageReceived;
        client.RoomMessageReceived -= OnRoomMessageReceived;
        client.RoomJoined -= OnRoomJoined;
        client.RoomLeft -= OnRoomLeft;
        client.PrivateRoomMembershipAdded -= OnPrivateRoomChanged;
        client.PrivateRoomMembershipRemoved -= OnPrivateRoomChanged;
        client.PrivateRoomModerationAdded -= OnPrivateRoomModerationAdded;
        client.PrivateRoomModerationRemoved -= OnPrivateRoomModerationRemoved;
        client.PrivateRoomUserListReceived -= OnPrivateRoomInfoChanged;
        client.PrivateRoomModeratedUserListReceived -= OnPrivateRoomInfoChanged;
    }

    private void OnPrivateMessageReceived(object? sender, PrivateMessageReceivedEventArgs e)
    {
        try
        {
            ChatTelemetry.RecordIngress("private");
            if (sender is not ISoulseekClient client)
                return;
            bool discard = soulseek.Restrictions.IsPrivateMessageBlocked(e.Username);
            if (!discard)
            {
                ChatIdentity.ValidateUsername(e.Username);
                ChatIdentity.ValidateMessage(e.Message);
            }
            IngressItem item = discard
                ? new AcknowledgeOnly(client, e.Id)
                : soulseek.ClientManager.LoggedInUsername is { } account
                    ? new PrivateMessage(client, account, e.Id, e.Timestamp, e.Username, e.Message)
                    : throw new InvalidOperationException("A private message arrived without a logged-in account.");
            if (discard)
                ChatTelemetry.RecordInboundResult("private", "blocked");
            if (!TryWriteIngress(item))
            {
                ChatTelemetry.RecordInboundResult("private", "dropped");
                ChatTelemetry.RecordDropped("private", "capacity");
                LogIngressCapacityWarning();
            }
        }
        catch (ArgumentException)
        {
            ChatTelemetry.RecordInboundResult("private", "invalid");
            if (sender is ISoulseekClient client
                && !TryWriteIngress(new AcknowledgeOnly(client, e.Id)))
            {
                ChatTelemetry.RecordInboundResult("private", "dropped");
                ChatTelemetry.RecordDropped("private", "capacity");
                LogIngressCapacityWarning();
            }
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "private-message");
        }
    }

    private void OnRoomMessageReceived(object? sender, RoomMessageReceivedEventArgs e)
    {
        try
        {
            ChatTelemetry.RecordIngress("room");
            ChatIdentity.NormalizeRoom(e.RoomName);
            ChatIdentity.ValidateUsername(e.Username);
            ChatIdentity.ValidateMessage(e.Message);
            string? account = soulseek.ClientManager.LoggedInUsername;
            if (account is null)
                return;
            if (!TryWriteIngress(new RoomMessage(account, e.RoomName, e.Username, e.Message)))
                throw new InvalidOperationException("Chat ingress closed while accepting a room message.");
        }
        catch (ArgumentException)
        {
            ChatTelemetry.RecordInboundResult("room", "invalid");
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "room-message");
        }
    }

    private void OnRoomJoined(object? sender, RoomJoinedEventArgs e)
    {
        try
        {
            ChatIdentity.NormalizeRoom(e.RoomName);
            ChatIdentity.ValidateUsername(e.UserData.Username);
            TryEnqueueRoster(new RosterJoined(e.RoomName, ChatRooms.MapMember(e.UserData, null, [])));
        }
        catch (Exception ex)
        {
            Rooms.MarkRosterIncomplete(e.RoomName, "InvalidRosterEvent");
            ChatLogMessages.CallbackFailed(logger, ex, "room-join");
        }
    }

    private void OnRoomLeft(object? sender, RoomLeftEventArgs e)
    {
        try
        {
            ChatIdentity.NormalizeRoom(e.RoomName);
            ChatIdentity.ValidateUsername(e.Username);
            TryEnqueueRoster(new RosterLeft(e.RoomName, e.Username));
        }
        catch (Exception ex)
        {
            Rooms.MarkRosterIncomplete(e.RoomName, "InvalidRosterEvent");
            ChatLogMessages.CallbackFailed(logger, ex, "room-leave");
        }
    }

    private void OnPrivateRoomChanged(object? sender, string roomName)
        => TryEnqueueRoomInvalidation(roomName);

    private void OnPrivateRoomInfoChanged(object? sender, RoomInfo info)
        => TryEnqueueRoomInvalidation(info.Name);

    private void OnPrivateRoomModerationAdded(object? sender, string roomName)
        => TryEnqueueCurrentAccountModeration(roomName, moderated: true);

    private void OnPrivateRoomModerationRemoved(object? sender, string roomName)
        => TryEnqueueCurrentAccountModeration(roomName, moderated: false);

    private void TryEnqueueRoomInvalidation(string roomName)
    {
        try
        {
            ChatIdentity.NormalizeRoom(roomName);
            if (!TryWriteIngress(new InvalidateRooms(roomName)))
                ChatTelemetry.RecordDropped("room", "metadata_capacity");
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "private-room");
        }
    }

    private void TryEnqueueCurrentAccountModeration(string roomName, bool moderated)
    {
        try
        {
            ChatIdentity.NormalizeRoom(roomName);
            if (!TryWriteIngress(new CurrentAccountModerationChanged(roomName, moderated)))
            {
                ChatTelemetry.RecordDropped("room", "metadata_capacity");
                Rooms.InvalidateAvailableRooms();
                Rooms.MarkRosterIncomplete(roomName, "IngressClosed");
            }
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "private-room-moderation");
        }
    }

    private void TryEnqueueRoster(IngressItem item)
    {
        try
        {
            if (!TryWriteIngress(item) && item is RoomNamed named)
            {
                ChatTelemetry.RecordDropped("room", "roster_capacity");
                Rooms.MarkRosterIncomplete(named.RoomName, "IngressClosed");
            }
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "room-roster");
        }
    }

    private void OnClientStateChanged(SoulseekClientStates clientState)
    {
        try
        {
            if (!clientState.HasFlag(SoulseekClientStates.LoggedIn))
                TryWriteIngress(new Disconnected());
            else
                TryWriteIngress(new ReconcileRooms());
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "connection");
        }
    }

    private bool TryWriteIngress(IngressItem item)
    {
        bool written = ingress.Writer.TryWrite(item);
        if (!written && !lifetime.IsCancellationRequested)
        {
            try
            {
                ingress.Writer.WriteAsync(item, lifetime.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                written = true;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }
        if (written && ingress.Reader.CanCount)
        {
            int depth = ingress.Reader.Count;
            ChatTelemetry.SetIngressDepth(depth);
            int observed = Volatile.Read(ref peakIngressDepth);
            while (depth > observed)
            {
                int previous = Interlocked.CompareExchange(
                    ref peakIngressDepth, depth, observed);
                if (previous == observed)
                    break;
                observed = previous;
            }
        }
        return written;
    }

    private void LogIngressCapacityWarning()
    {
        if (ingressCapacityWarningGate.TryAcquire(out long suppressedCount))
            ChatLogMessages.IngressFull(logger, suppressedCount);
    }

    private async Task RunIngressAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await ingress.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!ingress.Reader.TryRead(out IngressItem? item))
                    continue;

                var messageBatch = new List<IngressItem>(MaximumIngressPersistenceBatchSize);
                if (item is PrivateMessage or RoomMessage)
                {
                    messageBatch.Add(item);
                    while (messageBatch.Count < MaximumIngressPersistenceBatchSize
                        && ingress.Reader.TryPeek(out IngressItem? next)
                        && next is PrivateMessage or RoomMessage
                        && ingress.Reader.TryRead(out next))
                    {
                        messageBatch.Add(next);
                    }
                }

                if (ingress.Reader.CanCount)
                    ChatTelemetry.SetIngressDepth(ingress.Reader.Count);
                try
                {
                    if (messageBatch.Count > 0)
                    {
                        await ProcessMessageBatchAsync(messageBatch, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ProcessIngressAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    SetHealth(DaemonFeatureState.Degraded, ChatLogMessages.CompactFailure(ex));
                    ChatLogMessages.OperationFailed(logger, ex, "ingress-item");
                }
                try
                {
                    await Rooms.PublishDirtyRoomsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    SetHealth(DaemonFeatureState.Degraded, ChatLogMessages.CompactFailure(ex));
                    ChatLogMessages.OperationFailed(logger, ex, "publish-incomplete-roster");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ProcessIngressAsync(IngressItem item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case AcknowledgeOnly ack:
                await ack.Client.AcknowledgePrivateMessageAsync(ack.ProtocolId, cancellationToken).ConfigureAwait(false);
                ChatTelemetry.RecordAcknowledged("discarded");
                break;
            case PrivateMessage or RoomMessage:
                await ProcessMessageBatchAsync([item], cancellationToken).ConfigureAwait(false);
                break;
            case RosterJoined joined:
                await Rooms.RosterJoinedAsync(joined.RoomName, joined.Member, cancellationToken).ConfigureAwait(false);
                break;
            case RosterLeft left:
                await Rooms.RosterLeftAsync(left.RoomName, left.Username, cancellationToken).ConfigureAwait(false);
                break;
            case InvalidateRooms:
                Rooms.InvalidateAvailableRooms();
                break;
            case CurrentAccountModerationChanged moderation:
                await Rooms.ModerationChangedAsync(moderation.RoomName, moderation.Moderated, cancellationToken).ConfigureAwait(false);
                break;
            case Disconnected:
                await Rooms.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                SetHealth(DaemonFeatureState.Starting, "SoulseekDisconnected");
                break;
            case ReconcileRooms:
                await Rooms.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                await RefreshSummaryAsync(null, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task ProcessMessageBatchAsync(
        IReadOnlyList<IngressItem> items,
        CancellationToken cancellationToken)
    {
        var pending = new List<(IngressItem Item, ChatInboundMessage Message)>(items.Count);
        foreach (IngressItem item in items)
        {
            if (item is PrivateMessage direct)
            {
                pending.Add((item, new PrivateChatInboundMessage(
                    direct.LocalAccount,
                    direct.Username,
                    direct.Text,
                    direct.ProtocolId,
                    new DateTimeOffset(DateTime.SpecifyKind(direct.Timestamp, DateTimeKind.Utc)))));
                continue;
            }

            var roomMessage = (RoomMessage)item;
            if (ChatIdentity.ValidateUsername(roomMessage.Username)
                == ChatIdentity.ValidateAccount(roomMessage.LocalAccount))
            {
                continue;
            }
            pending.Add((item, new RoomChatInboundMessage(
                roomMessage.LocalAccount,
                roomMessage.RoomName,
                roomMessage.Username,
                roomMessage.Text,
                MentionDetector.ContainsWholeUsername(roomMessage.Text, roomMessage.LocalAccount))));
        }

        if (pending.Count == 0)
            return;

        IReadOnlyList<IncomingChatCommitResult> results;

        try
        {
            results = await store.AcceptIncomingMessagesAsync(
                pending.Select(item => item.Message).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            foreach ((IngressItem item, _) in pending)
            {
                ChatTelemetry.RecordPersistenceFailure(
                    item is PrivateMessage ? "private_ingress" : "room_ingress");
            }
            SetHealth(DaemonFeatureState.Degraded, ChatLogMessages.CompactFailure(ex));
            ChatLogMessages.OperationFailed(logger, ex, "ingress-batch");
            return;
        }

        var acknowledgements = new List<(PrivateMessage Message, ISoulseekClient Client)>();
        await summaryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int index = 0; index < pending.Count; index++)
            {
                IngressItem item = pending[index].Item;
                IncomingChatCommitResult result = results[index];

                if (item is PrivateMessage direct)
                {
                    ChatTelemetry.RecordPersisted("private", duplicate: !result.Inserted);
                    ChatTelemetry.RecordInboundResult("private", result.Inserted ? "accepted" : "duplicate");
                    bool currentAccount = IsCurrentAccount(direct.LocalAccount);
                    if (result.Inserted && currentAccount)
                    {
                        NotifyMessageCommitted(result.Message);
                        if (result.Notification is { } notification)
                            NotifyNotificationCommitted(notification);
                        PublishTarget(new ChatTargetDeltaDto(
                            ServerChatTargetKind.Direct,
                            result.Message.TargetId,
                            result.Conversation is null ? null : MapConversation(result.Conversation),
                            null,
                            [ChatDtoMapper.ToDto(result.Message)]));
                        ApplySummaryDelta(privateMessages: 1, notifications: 1);
                    }
                    if (currentAccount)
                        acknowledgements.Add((direct, direct.Client));
                    continue;
                }

                var roomMessage = (RoomMessage)item;
                ChatTelemetry.RecordPersisted("room");
                ChatTelemetry.RecordInboundResult("room", "accepted");
                if (IsCurrentAccount(roomMessage.LocalAccount))
                {
                    NotifyMessageCommitted(result.Message);
                    if (result.Notification is { } notification)
                        NotifyNotificationCommitted(notification);
                    PublishTarget(new ChatTargetDeltaDto(
                        ServerChatTargetKind.Room,
                        result.Message.TargetId,
                        null,
                        result.Room is null ? null : Rooms.MapRoom(result.Room),
                        [ChatDtoMapper.ToDto(result.Message)]));
                    ApplySummaryDelta(
                        roomMessages: 1,
                        notifications: result.Notification is null ? 0 : 1);
                }
            }
        }
        finally
        {
            summaryMutationGate.Release();
        }

        foreach (var (message, client) in acknowledgements)
        {
            try
            {
                await client.AcknowledgePrivateMessageAsync(
                    message.ProtocolId, cancellationToken).ConfigureAwait(false);
                ChatTelemetry.RecordAcknowledged("stored");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetHealth(DaemonFeatureState.Degraded, ChatLogMessages.CompactFailure(ex));
                ChatLogMessages.OperationFailed(logger, ex, "private-message-acknowledgement");
            }
        }
    }

    private async Task<ChatMessageRecord> CompleteSendAsync(
        OutgoingChatPreparationResult prepared,
        string account,
        Func<Task> send,
        CancellationToken cancellationToken)
    {
        if (prepared.Status == OutgoingChatPreparationStatus.Conflict)
            throw new InvalidOperationException("MessageId was already used with different content or target.");
        if (prepared.Status == OutgoingChatPreparationStatus.Existing)
            return prepared.Message;

        NotifyMessageCommitted(prepared.Message);
        PublishPrepared(prepared, prepared.Message);
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryRecordSendOutcomeAsync(
                account,
                prepared.Message,
                ChatMessageState.Unknown,
                "The send was cancelled after durable intent was recorded; delivery is unknown.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            ChatMessageRecord? failed = await TryRecordSendOutcomeAsync(
                account,
                prepared.Message,
                ChatMessageState.Failed,
                ChatLogMessages.CompactFailure(ex),
                CancellationToken.None).ConfigureAwait(false);
            if (failed is null)
                throw;
            return failed;
        }

        // The protocol write completed. A failure to record Sent is not evidence
        // that delivery failed; leave the durable Pending row for startup
        // reconciliation to classify as Unknown.
        try
        {
            var sent = await store.SetMessageStateAsync(
                account, prepared.Message.MessageId, ChatMessageState.Sent, null, cancellationToken)
                .ConfigureAwait(false);
            NotifyMessageCommitted(sent);
            ChatTelemetry.RecordSend(
                sent.TargetKind == ChatTargetKind.Direct ? "private" : "room", "sent");
            await PublishMessageTargetAsync(sent, CancellationToken.None).ConfigureAwait(false);
            PublishCurrentSummary();
            return sent;
        }
        catch
        {
            ChatTelemetry.RecordPersistenceFailure("send_state");
            throw;
        }
    }

    private async Task<ChatMessageRecord?> TryRecordSendOutcomeAsync(
        string account,
        ChatMessageRecord pending,
        ChatMessageState state,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await store.SetMessageStateAsync(
                account, pending.MessageId, state, reason, cancellationToken).ConfigureAwait(false);
            NotifyMessageCommitted(updated);
            ChatTelemetry.RecordSend(
                updated.TargetKind == ChatTargetKind.Direct ? "private" : "room",
                state.ToString().ToLowerInvariant());
            await PublishMessageTargetAsync(updated, cancellationToken).ConfigureAwait(false);
            PublishCurrentSummary();
            return updated;
        }
        catch (Exception persistenceFailure)
        {
            ChatTelemetry.RecordPersistenceFailure("send_state");
            ChatLogMessages.OperationFailed(logger, persistenceFailure, "persist-send-outcome");
            return null;
        }
    }

    private void PublishPrepared(
        OutgoingChatPreparationResult prepared,
        ChatMessageRecord message)
    {
        PublishTarget(new ChatTargetDeltaDto(
            message.TargetKind.ToServer(),
            message.TargetId,
            prepared.Conversation is null ? null : MapConversation(prepared.Conversation),
            prepared.Room is null ? null : Rooms.MapRoom(prepared.Room),
            [ChatDtoMapper.ToDto(message)]));
    }

    private Task PublishMessageTargetAsync(
        ChatMessageRecord message,
        CancellationToken cancellationToken)
        => PublishTargetAsync(
            message.TargetKind, message.TargetId, message, cancellationToken);

    private Task PublishTargetAsync(
        ChatTargetKind kind,
        Guid targetId,
        ChatMessageRecord? message,
        CancellationToken cancellationToken)
        => kind == ChatTargetKind.Direct
            ? PublishConversationAsync(targetId, message, cancellationToken)
            : Rooms.PublishRoomAsync(targetId, message, cancellationToken);

    private async Task PublishConversationAsync(
        Guid conversationId,
        ChatMessageRecord? message,
        CancellationToken cancellationToken)
    {
        var conversation = await store.GetConversationAsync(
            Account, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return;
        PublishTarget(new ChatTargetDeltaDto(
            ServerChatTargetKind.Direct,
            conversationId,
            MapConversation(conversation),
            null,
            message is null ? null : [ChatDtoMapper.ToDto(message)]));
    }

    private async Task PublishTargetReplacementAsync(
        ChatTargetKind kind,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        ChatTargetSnapshotDto? snapshot = await GetTargetSnapshotAsync(
            kind, targetId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return;
        PublishTarget(new ChatTargetDeltaDto(
            kind.ToServer(),
            targetId,
            snapshot.Conversation,
            snapshot.Room,
            snapshot.Messages,
            ReplaceMessages: true,
            HasEarlierMessages: snapshot.HasEarlierMessages));
    }

    private void PublishTarget(ChatTargetDeltaDto delta)
    {
        Action<ChatTargetDeltaDto>? handler = TargetChanged;
        if (handler is null)
            return;
        try { handler(delta); }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "target-observer");
        }
    }

    private void InvokeContained<T>(Action<T>? handlers, T value, string observerName)
    {
        if (handlers is null)
            return;
        foreach (Action<T> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try { handler(value); }
            catch (Exception ex)
            {
                ChatLogMessages.CallbackFailed(logger, ex, observerName);
            }
        }
    }

    private void NotifyMessageCommitted(ChatMessageRecord message)
        => InvokeContained(MessageCommitted, message, "Chat message");

    private void NotifyNotificationCommitted(UserNotificationRecord notification)
        => InvokeContained(NotificationCommitted, notification, "Chat notification");

    private async Task RefreshSummaryAsync(
        string? reason,
        CancellationToken cancellationToken,
        string? localAccount = null)
    {
        ChatStoreSummary summary = await store.GetSummaryAsync(
            localAccount ?? Account, cancellationToken).ConfigureAwait(false);
        var (joined, desired) = Rooms.GetCounts();
        var health = soulseek.ClientManager.IsConnectedAndLoggedIn
            ? reason is null ? DaemonFeatureState.Ready : DaemonFeatureState.Degraded
            : DaemonFeatureState.Starting;
        lock (stateGate)
        {
            revision = Math.Max(revision + 1, summary.Revision);
            state = new ChatRuntimeStateDto(
                health,
                reason,
                desired,
                joined,
                summary.UnreadPrivateMessages,
                summary.UnreadRoomMessages,
                revision);
            notificationSummary = new NotificationSummaryDto(summary.UnreadNotifications, revision);
        }
        ChatTelemetry.SetRoomCounts(joined, desired);
        ChatTelemetry.SetUnreadNotifications(summary.UnreadNotifications);
        PublishState();
    }

    private void PublishCurrentSummary()
        => ApplySummaryDelta();

    private void ApplySummaryDelta(
        int privateMessages = 0,
        int roomMessages = 0,
        int notifications = 0)
    {
        var (joined, desired) = Rooms.GetCounts();
        int unreadNotifications;
        lock (stateGate)
        {
            revision++;
            int unreadPrivate = checked(state.UnreadPrivateMessageCount + privateMessages);
            int unreadRoom = checked(state.UnreadRoomMessageCount + roomMessages);
            unreadNotifications = checked(notificationSummary.UnreadCount + notifications);
            bool connected = soulseek.ClientManager.IsConnectedAndLoggedIn;
            var health = !connected
                ? DaemonFeatureState.Starting
                : state.State == DaemonFeatureState.Degraded
                    ? DaemonFeatureState.Degraded
                    : DaemonFeatureState.Ready;
            string? reason = health switch
            {
                DaemonFeatureState.Degraded => state.Reason,
                DaemonFeatureState.Starting => "SoulseekDisconnected",
                _ => null,
            };
            state = new ChatRuntimeStateDto(
                health,
                reason,
                desired,
                joined,
                unreadPrivate,
                unreadRoom,
                revision);
            notificationSummary = new NotificationSummaryDto(unreadNotifications, revision);
        }
        ChatTelemetry.SetRoomCounts(joined, desired);
        ChatTelemetry.SetUnreadNotifications(unreadNotifications);
        PublishState();
    }

    private void SetHealth(DaemonFeatureState health, string? reason)
    {
        var (joined, desired) = Rooms.GetCounts();
        lock (stateGate)
        {
            revision++;
            state = state with
            {
                State = health,
                Reason = reason,
                DesiredRoomCount = desired,
                JoinedRoomCount = joined,
                Revision = revision,
            };
            notificationSummary = notificationSummary with { Revision = revision };
        }
        ChatTelemetry.SetRoomCounts(joined, desired);
        healthLog.Observe(health.ToString(), reason ?? "Available");
        PublishState();
    }

    private void PublishState()
    {
        Action<ChatRuntimeStateDto, NotificationSummaryDto>? handler = StateChanged;
        if (handler is null)
            return;
        ChatRuntimeStateDto chat;
        NotificationSummaryDto notifications;
        lock (stateGate)
        {
            chat = state;
            notifications = notificationSummary;
        }
        healthLog.Observe(chat.State.ToString(), chat.Reason ?? "Available");
        try { handler(chat, notifications); }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "state-observer");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;
        soulseek.ClientManager.ClientCreated -= AttachClient;
        soulseek.ClientManager.StateChanged -= OnClientStateChanged;
        ISoulseekClient[] clients;
        lock (stateGate)
            clients = attachedClients.Keys.ToArray();
        foreach (ISoulseekClient client in clients)
            DetachClient(client);
        ingress.Writer.TryComplete();
        if (worker is not null)
        {
            Task completed = await Task.WhenAny(
                worker,
                Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (!ReferenceEquals(completed, worker))
                lifetime.Cancel();
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lifetime.Cancel();
        ChatTelemetry.SetIngressDepth(0);
        Rooms.Dispose();
        summaryMutationGate.Dispose();
        lifetime.Dispose();
    }

    private abstract record IngressItem;
    private abstract record RoomNamed(string RoomName) : IngressItem;
    private sealed record AcknowledgeOnly(ISoulseekClient Client, int ProtocolId) : IngressItem;
    private sealed record PrivateMessage(
        ISoulseekClient Client,
        string LocalAccount,
        int ProtocolId,
        DateTime Timestamp,
        string Username,
        string Text) : IngressItem;
    private sealed record RoomMessage(
        string LocalAccount,
        string RoomName,
        string Username,
        string Text) : RoomNamed(RoomName);
    private sealed record RosterJoined(string RoomName, RoomMemberDto Member) : RoomNamed(RoomName);
    private sealed record RosterLeft(string RoomName, string Username) : RoomNamed(RoomName);
    private sealed record InvalidateRooms(string RoomName) : RoomNamed(RoomName);
    private sealed record CurrentAccountModerationChanged(
        string RoomName,
        bool Moderated) : RoomNamed(RoomName);
    private sealed record Disconnected : IngressItem;
    private sealed record ReconcileRooms : IngressItem;

}

internal static partial class ChatLogMessages
{
    internal static string CompactFailure(Exception exception)
    {
        string summary = ExceptionText.Summary(exception);
        return summary[..Math.Min(summary.Length, ChatLimits.MaximumFailureReasonLength)];
    }


    [LoggerMessage(4400, LogLevel.Error, "Chat runtime could not start")]
    internal static partial void StartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(4401, LogLevel.Warning,
        "Chat ingress capacity was exhausted; an inbound item could not be accepted (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void IngressFull(ILogger logger, long suppressedCount);

    [LoggerMessage(4402, LogLevel.Warning, "Chat callback failed during {Operation}")]
    internal static partial void CallbackFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(4403, LogLevel.Error, "Chat operation {Operation} failed")]
    internal static partial void OperationFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(4404, LogLevel.Warning, "Configured chat room join failed for room hash {RoomHash}")]
    internal static partial void ConfiguredRoomJoinFailed(ILogger logger, Exception exception, string roomHash);

    [LoggerMessage(4405, LogLevel.Warning, "Chat room list refresh failed for room hash {RoomHash}")]
    internal static partial void RoomListRefreshFailed(ILogger logger, Exception exception, string roomHash);

    [LoggerMessage(4406, LogLevel.Information, "Joined chat room {RoomId} (room hash {RoomHash})")]
    internal static partial void RoomJoined(ILogger logger, Guid roomId, string roomHash);

    [LoggerMessage(4407, LogLevel.Information, "Left chat room {RoomId} (room hash {RoomHash})")]
    internal static partial void RoomLeft(ILogger logger, Guid roomId, string roomHash);

    [LoggerMessage(4408, LogLevel.Information, "Deleted {TargetKind} chat history for target {TargetId}")]
    internal static partial void HistoryDeleted(ILogger logger, string targetKind, Guid targetId);
}
