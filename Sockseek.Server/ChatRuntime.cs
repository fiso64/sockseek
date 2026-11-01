using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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
/// Daemon-lifetime owner for chat ingestion, room session state, and durable chat actions.
/// Protocol callbacks validate/copy data and enqueue it for the single durable worker.
/// </summary>
public sealed class ChatRuntime : IAsyncDisposable
{
    private static readonly TimeSpan RoomListLifetime = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaximumAvailableRooms = 20_000;
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
    private readonly SemaphoreSlim roomMutationGate = new(1, 1);
    private readonly SemaphoreSlim roomListGate = new(1, 1);
    private readonly SemaphoreSlim summaryMutationGate = new(1, 1);
    private readonly Lock stateGate = new();
    private readonly Dictionary<string, RoomSession> roomSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirtyRoomKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> configuredRooms;
    private readonly HashSet<string> runtimeDesiredRooms = new(StringComparer.Ordinal);
    private readonly Dictionary<ISoulseekClient, byte> attachedClients = new(ReferenceEqualityComparer.Instance);
    private Task? worker;
    private AvailableRoomCache? availableRoomCache;
    private ChatRuntimeStateDto state = new(
        DaemonFeatureState.Starting, "Starting", 0, 0, 0, 0, 0);
    private NotificationSummaryDto notificationSummary = new(0, 0);
    private long revision;
    private int peakIngressDepth;
    private int disposeState;

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
        configuredRooms = settings.Chat.AutoJoinRooms
            .Select(ChatIdentity.NormalizeRoom)
            .ToHashSet(StringComparer.Ordinal);
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
            await LoadDesiredRoomsAsync(cancellationToken).ConfigureAwait(false);
            await ReconcileDesiredRoomsAsync(cancellationToken).ConfigureAwait(false);
            await RefreshSummaryAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetHealth(DaemonFeatureState.Degraded, CompactFailure(ex));
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

    public async Task<AvailableRoomPageDto> GetAvailableRoomsAsync(
        ServerChatRoomKind? kind, string? cursor, int limit, bool refresh, CancellationToken cancellationToken)
    {
        limit = ChatIdentity.ValidatePageSize(limit);
        AvailableRoomCache cache = await GetAvailableRoomCacheAsync(refresh, cancellationToken).ConfigureAwait(false);
        string? after = DecodeTextCursor(cursor);
        IEnumerable<AvailableRoomDto> query = cache.Rooms;
        if (kind is not null)
            query = query.Where(room => room.Kind == kind.Value);
        if (after is not null)
            query = query.Where(room => string.CompareOrdinal(room.Name, after) > 0);
        var rows = query.Take(limit + 1).ToArray();
        var page = rows.Take(limit).ToArray();
        return new AvailableRoomPageDto(
            page,
            rows.Length > limit && page.Length > 0 ? EncodeTextCursor(page[^1].Name) : null,
            cache.ObservedAtUtc,
            cache.Truncated);
    }

    public async Task<ChatPage<RoomSubscriptionRecord>> GetRoomsAsync(
        string? cursor, int limit, CancellationToken cancellationToken)
        => await store.GetRoomsAsync(Account, cursor, limit, cancellationToken).ConfigureAwait(false);

    public async Task<ChatRoomPageDto> GetRoomSummariesAsync(
        string? stateFilter,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        Func<ChatRoomSummaryDto, bool> matches = CreateRoomStateFilter(stateFilter);
        var mapped = new Dictionary<Guid, ChatRoomSummaryDto>();
        var page = await store.GetFilteredRoomsAsync(
            Account,
            room =>
            {
                ChatRoomSummaryDto summary = MapRoom(room);
                mapped[room.RoomId] = summary;
                return matches(summary);
            },
            cursor,
            limit,
            cancellationToken).ConfigureAwait(false);
        return new ChatRoomPageDto(
            page.Items.Select(room => mapped.GetValueOrDefault(room.RoomId) ?? MapRoom(room)).ToArray(),
            page.NextCursor);
    }

    public async Task<ChatRoomSummaryDto?> GetRoomSummaryAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false);
        return room is null ? null : MapRoom(room);
    }

    public async Task<ChatRoomDetailDto?> GetRoomDetailAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false);
        if (room is null)
            return null;
        RoomSessionSnapshot runtime = GetRoomSession(room.RoomKey);
        return new ChatRoomDetailDto(MapRoom(room), runtime.Owner, runtime.Operators);
    }

    public async Task<ChatRoomSummaryDto> JoinRoomAsync(
        string roomName, bool remember, CancellationToken cancellationToken)
    {
        EnsureReady();
        roomName = roomName.Trim();
        string key = ChatIdentity.NormalizeRoom(roomName);
        RoomSubscriptionRecord? existing = await store.GetRoomByNameAsync(
            Account, roomName, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            bool active = roomSessions.TryGetValue(key, out RoomSession? session)
                          && session.Phase is ChatRoomJoinPhase.Joined
                              or ChatRoomJoinPhase.Joining
                              or ChatRoomJoinPhase.Leaving;
            int activeCount = roomSessions.Values.Count(item => item.Phase is
                ChatRoomJoinPhase.Joined or ChatRoomJoinPhase.Joining or ChatRoomJoinPhase.Leaving);
            if (!active && activeCount >= ChatLimits.MaximumDesiredRooms)
                throw new ChatCapacityException(
                    $"At most {ChatLimits.MaximumDesiredRooms} rooms may be joined at once.");

            bool addsDesired = remember
                               && existing?.RuntimeDesired != true
                               && !configuredRooms.Contains(key)
                               && !runtimeDesiredRooms.Contains(key);
            int desiredCount = configuredRooms.Union(runtimeDesiredRooms, StringComparer.Ordinal).Count();
            if (addsDesired && desiredCount >= ChatLimits.MaximumDesiredRooms)
                throw new ChatCapacityException(
                    $"At most {ChatLimits.MaximumDesiredRooms} rooms may be remembered or configured.");
        }
        var persisted = await store.UpsertRoomAsync(
            Account,
            roomName,
            remember || existing?.RuntimeDesired == true,
            ChatRoomKind.Unknown,
            cancellationToken).ConfigureAwait(false);
        if (persisted.RuntimeDesired)
        {
            lock (stateGate)
                runtimeDesiredRooms.Add(key);
        }
        try
        {
            await JoinRoomCoreAsync(persisted, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryPublishRoomFailureAsync(persisted.RoomId).ConfigureAwait(false);
            throw;
        }
        persisted = await store.GetRoomAsync(Account, persisted.RoomId, cancellationToken).ConfigureAwait(false)
                    ?? persisted;
        PublishCurrentSummary();
        await PublishRoomAsync(persisted.RoomId, null, CancellationToken.None).ConfigureAwait(false);
        ChatLogMessages.RoomJoined(
            logger,
            persisted.RoomId,
            LogIdentity.Hash(persisted.RoomKey));
        return MapRoom(persisted);
    }

    public async Task<ChatRoomSummaryDto> LeaveRoomAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var persisted = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false)
                        ?? throw new KeyNotFoundException("The room was not found.");
        if (persisted.RuntimeDesired)
        {
            persisted = await store.UpsertRoomAsync(
                Account, persisted.DisplayName, false, persisted.Kind, cancellationToken).ConfigureAwait(false);
            lock (stateGate)
                runtimeDesiredRooms.Remove(persisted.RoomKey);
        }

        await roomMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetOrCreateSession(persisted.RoomKey, persisted.DisplayName);
            bool shouldLeave;
            lock (stateGate)
            {
                shouldLeave = session.Phase is ChatRoomJoinPhase.Joined or ChatRoomJoinPhase.Joining;
                if (shouldLeave)
                    session.Phase = ChatRoomJoinPhase.Leaving;
            }
            try
            {
                if (shouldLeave)
                    await Client.LeaveRoomAsync(persisted.DisplayName, cancellationToken).ConfigureAwait(false);
                lock (stateGate)
                {
                    session.Reset(ChatRoomJoinPhase.Disconnected, null);
                    roomSessions.Remove(persisted.RoomKey);
                }
            }
            catch (Exception ex)
            {
                lock (stateGate)
                {
                    session.Phase = ChatRoomJoinPhase.Failed;
                    session.FailureReason = CompactFailure(ex);
                }
                await TryPublishRoomFailureAsync(persisted.RoomId).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            roomMutationGate.Release();
        }
        PublishCurrentSummary();
        await PublishRoomAsync(persisted.RoomId, null, CancellationToken.None).ConfigureAwait(false);
        ChatLogMessages.RoomLeft(
            logger,
            persisted.RoomId,
            LogIdentity.Hash(persisted.RoomKey));
        return MapRoom(persisted);
    }

    public async Task<ChatMessageRecord> SendRoomMessageAsync(
        Guid roomId, Guid messageId, string text, CancellationToken cancellationToken)
    {
        text = ChatIdentity.ValidateMessage(text);
        EnsureReady();
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The room was not found.");
        if (GetRoomSession(room.RoomKey).Phase != ChatRoomJoinPhase.Joined)
            throw new ChatStateConflictException("The room is not joined.");
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
            room = MapRoom(record);
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

    public async Task<RoomMemberPageDto> GetRoomMembersAsync(
        Guid roomId, string? cursor, int limit, long? expectedRevision, CancellationToken cancellationToken)
    {
        limit = ChatIdentity.ValidatePageSize(limit);
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The room was not found.");
        RoomSessionSnapshot session = GetRoomSession(room.RoomKey);
        if (expectedRevision is not null && expectedRevision != session.MemberRevision)
            throw new InvalidOperationException("The room roster revision changed.");
        string? after = DecodeTextCursor(cursor);
        var members = session.Members.Values
            .Where(member => after is null || string.CompareOrdinal(member.Username, after) > 0)
            .OrderBy(member => member.Username, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToArray();
        var page = members.Take(limit).ToArray();
        return new RoomMemberPageDto(
            page,
            members.Length > limit && page.Length > 0 ? EncodeTextCursor(page[^1].Username) : null,
            session.MemberRevision,
            session.RosterComplete);
    }

    public async Task AddPrivateRoomMemberAsync(Guid roomId, string username, CancellationToken cancellationToken)
    {
        username = username.Trim();
        ChatIdentity.ValidateUsername(username);
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The room was not found.");
        var session = GetRoomSession(room.RoomKey);
        if (session.Phase != ChatRoomJoinPhase.Joined || session.Kind != ChatRoomKind.Private)
            throw new ChatStateConflictException(
                "Members can only be added to a joined private room.");
        await Client.AddPrivateRoomMemberAsync(room.DisplayName, username, cancellationToken).ConfigureAwait(false);
        InvalidateAvailableRooms();
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
            TryEnqueueRoster(new RosterJoined(e.RoomName, MapMember(e.UserData, null, [])));
        }
        catch (Exception ex)
        {
            MarkRosterIncomplete(e.RoomName, "InvalidRosterEvent");
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
            MarkRosterIncomplete(e.RoomName, "InvalidRosterEvent");
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
                InvalidateAvailableRooms();
                MarkRosterIncomplete(roomName, "IngressClosed");
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
                MarkRosterIncomplete(named.RoomName, "IngressClosed");
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
                    SetHealth(DaemonFeatureState.Degraded, CompactFailure(ex));
                    ChatLogMessages.OperationFailed(logger, ex, "ingress-item");
                }
                try
                {
                    await PublishDirtyRoomsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    SetHealth(DaemonFeatureState.Degraded, CompactFailure(ex));
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
                ApplyRosterJoin(joined.RoomName, joined.Member);
                await PublishRoomByNameAsync(joined.RoomName, cancellationToken).ConfigureAwait(false);
                break;
            case RosterLeft left:
                ApplyRosterLeave(left.RoomName, left.Username);
                await PublishRoomByNameAsync(left.RoomName, cancellationToken).ConfigureAwait(false);
                break;
            case InvalidateRooms:
                InvalidateAvailableRooms();
                break;
            case CurrentAccountModerationChanged moderation:
                ApplyCurrentAccountModeration(moderation.RoomName, moderation.Moderated);
                InvalidateAvailableRooms();
                await PublishRoomByNameAsync(moderation.RoomName, cancellationToken).ConfigureAwait(false);
                break;
            case Disconnected:
                HandleDisconnected();
                await PublishKnownRoomsAsync(cancellationToken).ConfigureAwait(false);
                PruneNonDesiredRoomSessions();
                break;
            case ReconcileRooms:
                await LoadDesiredRoomsAsync(cancellationToken).ConfigureAwait(false);
                await ReconcileDesiredRoomsAsync(cancellationToken).ConfigureAwait(false);
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
            SetHealth(DaemonFeatureState.Degraded, CompactFailure(ex));
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
                        result.Room is null ? null : MapRoom(result.Room),
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
                SetHealth(DaemonFeatureState.Degraded, CompactFailure(ex));
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
                CompactFailure(ex),
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

    private async Task LoadDesiredRoomsAsync(CancellationToken cancellationToken)
    {
        var desired = await store.GetDesiredRoomsAsync(Account, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            runtimeDesiredRooms.Clear();
            foreach (string configured in configuredRooms)
                GetOrCreateSessionLocked(configured, configured);
            foreach (var room in desired)
            {
                runtimeDesiredRooms.Add(room.RoomKey);
                GetOrCreateSessionLocked(room.RoomKey, room.DisplayName);
            }
        }
    }

    private async Task ReconcileDesiredRoomsAsync(CancellationToken cancellationToken)
    {
        if (!soulseek.ClientManager.IsConnectedAndLoggedIn)
            return;
        var desiredRows = await store.GetDesiredRoomsAsync(Account, cancellationToken).ConfigureAwait(false);
        var names = configuredRooms
            .Concat(desiredRows.Select(room => room.RoomKey))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string key in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = desiredRows.FirstOrDefault(room => room.RoomKey == key)
                      ?? await store.UpsertRoomAsync(
                          Account, key, false, ChatRoomKind.Unknown, cancellationToken).ConfigureAwait(false);
            try { await JoinRoomCoreAsync(row, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ChatLogMessages.ConfiguredRoomJoinFailed(
                    logger,
                    ex,
                    LogIdentity.Hash(row.RoomKey));
            }
            await PublishRoomAsync(row.RoomId, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task JoinRoomCoreAsync(RoomSubscriptionRecord persisted, CancellationToken cancellationToken)
    {
        await roomMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RoomSession session = GetOrCreateSession(persisted.RoomKey, persisted.DisplayName);
            lock (stateGate)
            {
                if (session.Phase is ChatRoomJoinPhase.Joined or ChatRoomJoinPhase.Joining)
                    return;
                session.Phase = ChatRoomJoinPhase.Joining;
                session.FailureReason = null;
                session.Provisional.Clear();
                session.ProvisionalOverflowed = false;
                session.PendingCurrentAccountModeration = null;
            }
            try
            {
                AvailableRoomDto? classification = null;
                try
                {
                    var available = await GetAvailableRoomCacheAsync(false, cancellationToken).ConfigureAwait(false);
                    classification = available.Rooms.FirstOrDefault(room =>
                    {
                        try { return ChatIdentity.NormalizeRoom(room.Name) == persisted.RoomKey; }
                        catch (ArgumentException) { return false; }
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ChatLogMessages.RoomListRefreshFailed(
                        logger,
                        ex,
                        LogIdentity.Hash(persisted.RoomKey));
                }
                bool isPrivate = classification?.Kind == ServerChatRoomKind.Private
                                 || persisted.Kind == ChatRoomKind.Private;
                RoomData joined = await Client.JoinRoomAsync(
                    persisted.DisplayName, isPrivate, cancellationToken).ConfigureAwait(false);
                string joinedKey = ChatIdentity.NormalizeRoom(joined.Name);
                if (!string.Equals(joinedKey, persisted.RoomKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("The server returned a different room than requested.");
                await store.UpsertRoomAsync(
                    Account,
                    joined.Name,
                    persisted.RuntimeDesired,
                    joined.IsPrivate ? ChatRoomKind.Private : ChatRoomKind.Public,
                    cancellationToken).ConfigureAwait(false);
                var roster = BuildRosterSnapshot(joined);
                lock (stateGate)
                {
                    session.DisplayName = joined.Name;
                    session.Kind = joined.IsPrivate ? ChatRoomKind.Private : ChatRoomKind.Public;
                    session.Owner = roster.Owner;
                    session.Operators = roster.Operators;
                    session.Members = roster.Members;
                    session.RosterComplete = roster.Complete && !session.ProvisionalOverflowed;
                    foreach (RosterMutation mutation in session.Provisional)
                        ApplyRosterMutationLocked(session, mutation);
                    if (session.PendingCurrentAccountModeration is { } moderated
                        && soulseek.ClientManager.LoggedInUsername is { } currentAccount)
                    {
                        ApplyCurrentAccountModerationLocked(
                            session,
                            currentAccount,
                            ChatIdentity.ValidateAccount(currentAccount),
                            moderated);
                    }
                    session.Provisional.Clear();
                    session.PendingCurrentAccountModeration = null;
                    session.MemberRevision++;
                    session.Phase = ChatRoomJoinPhase.Joined;
                    session.FailureReason = null;
                }
            }
            catch (Exception ex)
            {
                lock (stateGate)
                    session.Reset(ChatRoomJoinPhase.Failed, CompactFailure(ex));
                throw;
            }
        }
        finally
        {
            roomMutationGate.Release();
        }
    }

    private async Task<AvailableRoomCache> GetAvailableRoomCacheAsync(
        bool force, CancellationToken cancellationToken)
    {
        lock (stateGate)
        {
            if (!force && availableRoomCache is { } cached
                && cached.ObservedAtUtc + RoomListLifetime > DateTimeOffset.UtcNow)
                return cached;
        }
        await roomListGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (!force && availableRoomCache is { } cached
                    && cached.ObservedAtUtc + RoomListLifetime > DateTimeOffset.UtcNow)
                    return cached;
            }
            EnsureReady();
            RoomList list = await Client.GetRoomListAsync(cancellationToken).ConfigureAwait(false);
            var owned = ValidRoomKeys(list.Owned.Select(room => room.Name));
            var moderated = ValidRoomKeys(list.ModeratedRoomNames);
            var byName = new Dictionary<string, AvailableRoomDto>(StringComparer.Ordinal);
            AddAvailableRooms(byName, list.Public, ChatRoomKind.Public, owned, moderated);
            AddAvailableRooms(byName, list.Private, ChatRoomKind.Private, owned, moderated);
            var allRooms = byName.Values
                .OrderBy(room => room.Name, StringComparer.Ordinal)
                .Take(MaximumAvailableRooms + 1)
                .ToArray();
            bool truncated = allRooms.Length > MaximumAvailableRooms;
            var refreshed = new AvailableRoomCache(
                allRooms.Take(MaximumAvailableRooms).ToArray(),
                DateTimeOffset.UtcNow,
                truncated);
            lock (stateGate)
                availableRoomCache = refreshed;
            return refreshed;
        }
        finally
        {
            roomListGate.Release();
        }
    }

    private void InvalidateAvailableRooms()
    {
        lock (stateGate)
            availableRoomCache = null;
    }

    private static HashSet<string> ValidRoomKeys(IEnumerable<string> roomNames)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in roomNames)
        {
            try { result.Add(ChatIdentity.NormalizeRoom(name)); }
            catch (ArgumentException) { }
        }
        return result;
    }

    private static void AddAvailableRooms(
        Dictionary<string, AvailableRoomDto> target,
        IEnumerable<RoomInfo> rooms,
        ChatRoomKind kind,
        IReadOnlySet<string> owned,
        IReadOnlySet<string> moderated)
    {
        foreach (RoomInfo room in rooms)
        {
            string key;
            try { key = ChatIdentity.NormalizeRoom(room.Name); }
            catch (ArgumentException) { continue; }
            var candidate = new AvailableRoomDto(
                key,
                Math.Max(0, room.UserCount),
                kind.ToServer(),
                owned.Contains(key),
                moderated.Contains(key));
            if (!target.TryGetValue(key, out AvailableRoomDto? current)
                || kind == ChatRoomKind.Private && current.Kind != ServerChatRoomKind.Private)
            {
                target[key] = candidate;
            }
        }
    }

    private void ApplyRosterJoin(string roomName, RoomMemberDto member)
    {
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        string memberKey = ChatIdentity.ValidateUsername(member.Username);
        lock (stateGate)
        {
            if (!roomSessions.TryGetValue(roomKey, out RoomSession? session))
                return;
            member = member with
            {
                IsOwner = session.Owner is not null
                          && ChatIdentity.ValidateUsername(session.Owner) == memberKey,
                IsOperator = session.Operators.Any(item =>
                    ChatIdentity.ValidateUsername(item) == memberKey),
            };
            var mutation = new RosterAdd(memberKey, member);
            if (session.Phase == ChatRoomJoinPhase.Joining)
            {
                if (session.Provisional.Count < ChatLimits.MaximumProvisionalRosterChanges)
                    session.Provisional.Add(mutation);
                else
                {
                    session.RosterComplete = false;
                    session.ProvisionalOverflowed = true;
                }
            }
            ApplyRosterMutationLocked(session, mutation);
            session.MemberRevision++;
        }
    }

    private void ApplyRosterLeave(string roomName, string username)
    {
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        string memberKey = ChatIdentity.ValidateUsername(username);
        lock (stateGate)
        {
            if (!roomSessions.TryGetValue(roomKey, out RoomSession? session))
                return;
            var mutation = new RosterRemove(memberKey);
            if (session.Phase == ChatRoomJoinPhase.Joining)
            {
                if (session.Provisional.Count < ChatLimits.MaximumProvisionalRosterChanges)
                    session.Provisional.Add(mutation);
                else
                {
                    session.RosterComplete = false;
                    session.ProvisionalOverflowed = true;
                }
            }
            ApplyRosterMutationLocked(session, mutation);
            session.MemberRevision++;
        }
    }

    private void ApplyCurrentAccountModeration(string roomName, bool moderated)
    {
        string? currentAccount = soulseek.ClientManager.LoggedInUsername;
        if (currentAccount is null)
            return;
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        string accountKey = ChatIdentity.ValidateAccount(currentAccount);
        lock (stateGate)
        {
            if (!roomSessions.TryGetValue(roomKey, out RoomSession? session))
                return;
            if (session.Phase == ChatRoomJoinPhase.Joining)
                session.PendingCurrentAccountModeration = moderated;
            ApplyCurrentAccountModerationLocked(session, currentAccount, accountKey, moderated);
        }
    }

    private static void ApplyCurrentAccountModerationLocked(
        RoomSession session,
        string currentAccount,
        string accountKey,
        bool moderated)
    {
        bool wasModerated = session.Operators.Any(item =>
            ChatIdentity.ValidateUsername(item) == accountKey);
        bool changed = wasModerated != moderated;
        if (changed)
        {
            var operators = session.Operators
                .Where(item => ChatIdentity.ValidateUsername(item) != accountKey)
                .ToImmutableArray();
            if (moderated)
            {
                if (operators.Length >= ChatLimits.MaximumRoomOperators)
                {
                    operators = operators[..(ChatLimits.MaximumRoomOperators - 1)];
                    session.RosterComplete = false;
                    ChatTelemetry.RecordDropped("room", "operator_limit");
                }
                operators = operators.Add(currentAccount);
            }
            session.Operators = operators;
        }

        if (session.Members.TryGetValue(accountKey, out RoomMemberDto? member)
            && member.IsOperator != moderated)
        {
            session.Members = session.Members.SetItem(
                accountKey,
                member with { IsOperator = moderated });
            changed = true;
        }
        if (changed)
            session.MemberRevision++;
    }

    private void MarkRosterIncomplete(string roomName, string reason)
    {
        try
        {
            string key = ChatIdentity.NormalizeRoom(roomName);
            lock (stateGate)
            {
                if (!roomSessions.TryGetValue(key, out RoomSession? session))
                    return;
                session.RosterComplete = false;
                session.FailureReason ??= reason;
                session.MemberRevision++;
                dirtyRoomKeys.Add(key);
            }
        }
        catch { }
    }

    private void HandleDisconnected()
    {
        lock (stateGate)
        {
            foreach (RoomSession session in roomSessions.Values)
                session.Reset(ChatRoomJoinPhase.Disconnected, null);
        }
        SetHealth(DaemonFeatureState.Starting, "SoulseekDisconnected");
    }

    private void PruneNonDesiredRoomSessions()
    {
        lock (stateGate)
        {
            foreach (string key in roomSessions.Keys
                         .Where(key => !configuredRooms.Contains(key)
                                       && !runtimeDesiredRooms.Contains(key))
                         .ToArray())
            {
                roomSessions.Remove(key);
                dirtyRoomKeys.Remove(key);
            }
        }
    }

    private async Task PublishKnownRoomsAsync(CancellationToken cancellationToken)
    {
        string[] roomNames;
        lock (stateGate)
            roomNames = roomSessions.Values.Select(room => room.DisplayName).ToArray();
        foreach (string roomName in roomNames)
            await PublishRoomByNameAsync(roomName, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishDirtyRoomsAsync(CancellationToken cancellationToken)
    {
        string[] roomNames;
        lock (stateGate)
        {
            roomNames = dirtyRoomKeys
                .Select(key => roomSessions.GetValueOrDefault(key)?.DisplayName)
                .OfType<string>()
                .ToArray();
            dirtyRoomKeys.Clear();
        }
        foreach (string roomName in roomNames)
        {
            try
            {
                await PublishRoomByNameAsync(roomName, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (stateGate)
                {
                    try { dirtyRoomKeys.Add(ChatIdentity.NormalizeRoom(roomName)); }
                    catch (ArgumentException) { }
                }
                throw;
            }
        }
    }

    private RoomSession GetOrCreateSession(string key, string displayName)
    {
        lock (stateGate)
            return GetOrCreateSessionLocked(key, displayName);
    }

    private RoomSession GetOrCreateSessionLocked(string key, string displayName)
    {
        if (!roomSessions.TryGetValue(key, out RoomSession? session))
        {
            session = new RoomSession(key, displayName);
            roomSessions.Add(key, session);
        }
        return session;
    }

    private RoomSessionSnapshot GetRoomSession(string key)
    {
        lock (stateGate)
        {
            if (!roomSessions.TryGetValue(key, out RoomSession? session))
                return RoomSessionSnapshot.Empty;
            return session.Snapshot();
        }
    }

    private ChatRoomSummaryDto MapRoom(RoomSubscriptionRecord room)
    {
        RoomSessionSnapshot runtime = GetRoomSession(room.RoomKey);
        bool configured = configuredRooms.Contains(room.RoomKey);
        string localAccountKey = ChatIdentity.ValidateAccount(Account);
        bool owned = runtime.Owner is not null
                     && ChatIdentity.ValidateUsername(runtime.Owner) == localAccountKey;
        bool moderated = runtime.Operators.Any(item =>
            ChatIdentity.ValidateUsername(item) == localAccountKey);
        return new ChatRoomSummaryDto(
            room.RoomId,
            room.DisplayName,
            configured,
            room.RuntimeDesired,
            configured || room.RuntimeDesired,
            (runtime.Kind != ChatRoomKind.Unknown ? runtime.Kind : room.Kind).ToServer(),
            owned,
            moderated,
            runtime.Phase.ToServer(),
            runtime.FailureReason,
            runtime.Members.Count,
            runtime.MemberRevision,
            runtime.RosterComplete,
            room.UnreadCount,
            room.LastReadSequence,
            room.Revision,
            room.LastMessage is null ? null : ChatDtoMapper.ToDto(room.LastMessage));
    }

    private void PublishPrepared(
        OutgoingChatPreparationResult prepared,
        ChatMessageRecord message)
    {
        PublishTarget(new ChatTargetDeltaDto(
            message.TargetKind.ToServer(),
            message.TargetId,
            prepared.Conversation is null ? null : MapConversation(prepared.Conversation),
            prepared.Room is null ? null : MapRoom(prepared.Room),
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
            : PublishRoomAsync(targetId, message, cancellationToken);

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

    private async Task PublishRoomAsync(
        Guid roomId,
        ChatMessageRecord? message,
        CancellationToken cancellationToken)
    {
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false);
        if (room is null)
            return;
        PublishTarget(new ChatTargetDeltaDto(
            ServerChatTargetKind.Room,
            roomId,
            null,
            MapRoom(room),
            message is null ? null : [ChatDtoMapper.ToDto(message)]));
    }

    private async Task PublishRoomByNameAsync(string roomName, CancellationToken cancellationToken)
    {
        var room = await store.GetRoomByNameAsync(Account, roomName, cancellationToken).ConfigureAwait(false);
        if (room is not null)
            await PublishRoomAsync(room.RoomId, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryPublishRoomFailureAsync(Guid roomId)
    {
        try
        {
            PublishCurrentSummary();
            await PublishRoomAsync(roomId, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "publish-failed-room");
        }
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
        int joined;
        int desired;
        lock (stateGate)
        {
            joined = roomSessions.Values.Count(room => room.Phase == ChatRoomJoinPhase.Joined);
            desired = configuredRooms.Union(runtimeDesiredRooms, StringComparer.Ordinal).Count();
        }
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
        int joined;
        int desired;
        int unreadNotifications;
        lock (stateGate)
        {
            joined = roomSessions.Values.Count(room => room.Phase == ChatRoomJoinPhase.Joined);
            desired = configuredRooms.Union(runtimeDesiredRooms, StringComparer.Ordinal).Count();
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
        int joined;
        int desired;
        lock (stateGate)
        {
            joined = roomSessions.Values.Count(room => room.Phase == ChatRoomJoinPhase.Joined);
            desired = configuredRooms.Union(runtimeDesiredRooms, StringComparer.Ordinal).Count();
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

    private static RoomMemberDto MapMember(
        UserData user,
        string? owner,
        IReadOnlyCollection<string> operators)
        => new(
            user.Username,
            user.Status.ToString(),
            user.CountryCode,
            owner is not null
                && ChatIdentity.ValidateUsername(owner) == ChatIdentity.ValidateUsername(user.Username),
            operators.Any(item =>
                ChatIdentity.ValidateUsername(item) == ChatIdentity.ValidateUsername(user.Username)));

    private static RosterSnapshot BuildRosterSnapshot(RoomData joined)
    {
        bool complete = true;
        string? owner = joined.Owner;
        if (owner is not null)
        {
            try { ChatIdentity.ValidateUsername(owner); }
            catch (ArgumentException) { owner = null; complete = false; }
        }

        var operatorKeys = new HashSet<string>(StringComparer.Ordinal);
        var operators = ImmutableArray.CreateBuilder<string>();
        foreach (string candidate in joined.Operators ?? [])
        {
            try
            {
                string key = ChatIdentity.ValidateUsername(candidate);
                if (!operatorKeys.Add(key))
                    continue;
                if (operators.Count == ChatLimits.MaximumRoomOperators)
                {
                    complete = false;
                    break;
                }
                operators.Add(candidate);
            }
            catch (ArgumentException) { complete = false; }
        }

        var members = ImmutableDictionary.CreateBuilder<string, RoomMemberDto>(StringComparer.Ordinal);
        foreach (UserData user in joined.Users)
        {
            try
            {
                string key = ChatIdentity.ValidateUsername(user.Username);
                if (!members.ContainsKey(key) && members.Count == ChatLimits.MaximumRoomMembers)
                {
                    complete = false;
                    break;
                }
                members[key] = MapMember(user, owner, operators);
            }
            catch (ArgumentException) { complete = false; }
        }
        return new RosterSnapshot(owner, operators.ToImmutable(), members.ToImmutable(), complete);
    }

    private static void ApplyRosterMutationLocked(RoomSession session, RosterMutation mutation)
    {
        if (mutation is RosterAdd add
            && !session.Members.ContainsKey(add.Key)
            && session.Members.Count >= ChatLimits.MaximumRoomMembers)
        {
            session.RosterComplete = false;
            if (session.Phase == ChatRoomJoinPhase.Joining)
                session.ProvisionalOverflowed = true;
            ChatTelemetry.RecordDropped("room", "roster_limit");
            return;
        }
        session.Members = mutation.Apply(session.Members);
    }

    private static string CompactFailure(Exception exception)
    {
        string summary = ExceptionText.Summary(exception);
        return summary[..Math.Min(summary.Length, ChatLimits.MaximumFailureReasonLength)];
    }

    private static string EncodeTextCursor(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? DecodeTextCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length is 0 or > 2_048)
            throw new ArgumentException("Invalid cursor.", nameof(cursor));
        try
        {
            string value = cursor.Replace('-', '+').Replace('_', '/');
            value += new string('=', (4 - value.Length % 4) % 4);
            return StrictUtf8.GetString(Convert.FromBase64String(value));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor));
        }
    }

    private static Func<ChatRoomSummaryDto, bool> CreateRoomStateFilter(string? state)
        => state?.Trim().ToLowerInvariant() switch
        {
            null or "" => static _ => true,
            "desired" => static room => room.Desired,
            "joined" => static room => room.Phase == ServerChatRoomJoinPhase.Joined,
            "failed" => static room => room.Phase == ServerChatRoomJoinPhase.Failed,
            "disconnected" => static room => room.Phase == ServerChatRoomJoinPhase.Disconnected,
            _ => throw new ArgumentException("Invalid room state filter."),
        };

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
        roomMutationGate.Dispose();
        roomListGate.Dispose();
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

    private abstract record RosterMutation
    {
        public abstract ImmutableDictionary<string, RoomMemberDto> Apply(
            ImmutableDictionary<string, RoomMemberDto> members);
    }

    private sealed record RosterAdd(string Key, RoomMemberDto Member) : RosterMutation
    {
        public override ImmutableDictionary<string, RoomMemberDto> Apply(
            ImmutableDictionary<string, RoomMemberDto> members) => members.SetItem(Key, Member);
    }

    private sealed record RosterRemove(string Key) : RosterMutation
    {
        public override ImmutableDictionary<string, RoomMemberDto> Apply(
            ImmutableDictionary<string, RoomMemberDto> members) => members.Remove(Key);
    }

    private sealed class RoomSession(string key, string displayName)
    {
        public string Key { get; } = key;
        public string DisplayName { get; set; } = displayName;
        public ChatRoomKind Kind { get; set; }
        public ChatRoomJoinPhase Phase { get; set; } = ChatRoomJoinPhase.Disconnected;
        public string? FailureReason { get; set; }
        public string? Owner { get; set; }
        public ImmutableArray<string> Operators { get; set; } = [];
        public ImmutableDictionary<string, RoomMemberDto> Members { get; set; }
            = ImmutableDictionary<string, RoomMemberDto>.Empty.WithComparers(StringComparer.Ordinal);
        public long MemberRevision { get; set; }
        public bool RosterComplete { get; set; }
        public List<RosterMutation> Provisional { get; } = [];
        public bool ProvisionalOverflowed { get; set; }
        public bool? PendingCurrentAccountModeration { get; set; }

        public void Reset(ChatRoomJoinPhase phase, string? reason)
        {
            Phase = phase;
            FailureReason = reason;
            Owner = null;
            Operators = [];
            Members = Members.Clear();
            MemberRevision++;
            RosterComplete = false;
            Provisional.Clear();
            ProvisionalOverflowed = false;
            PendingCurrentAccountModeration = null;
        }

        public RoomSessionSnapshot Snapshot()
            => new(Kind, Phase, FailureReason, Owner, Operators, Members, MemberRevision, RosterComplete);
    }

    private sealed record RoomSessionSnapshot(
        ChatRoomKind Kind,
        ChatRoomJoinPhase Phase,
        string? FailureReason,
        string? Owner,
        ImmutableArray<string> Operators,
        ImmutableDictionary<string, RoomMemberDto> Members,
        long MemberRevision,
        bool RosterComplete)
    {
        public static RoomSessionSnapshot Empty { get; } = new(
            ChatRoomKind.Unknown,
            ChatRoomJoinPhase.Disconnected,
            null,
            null,
            [],
            ImmutableDictionary<string, RoomMemberDto>.Empty,
            0,
            false);
    }

    private sealed record RosterSnapshot(
        string? Owner,
        ImmutableArray<string> Operators,
        ImmutableDictionary<string, RoomMemberDto> Members,
        bool Complete);

    private sealed record AvailableRoomCache(
        IReadOnlyList<AvailableRoomDto> Rooms,
        DateTimeOffset ObservedAtUtc,
        bool Truncated);
}

internal static partial class ChatLogMessages
{
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
