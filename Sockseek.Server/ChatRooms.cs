using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Chat;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Chat;
using Soulseek;

namespace Sockseek.Server;

/// <summary>
/// Owns room membership, reconnect, available rooms and roster revisions.
/// ChatRuntime delivers ordered protocol events and owns this object's lifetime;
/// message persistence and acknowledgements never access mutable room sessions.
/// </summary>
public sealed class ChatRooms : IDisposable
{
    private readonly EngineSettings settings;
    private readonly DaemonSoulseekRuntime soulseek;
    private readonly ChatPersistenceStore store;
    private readonly ILogger logger;
    private readonly Lock stateGate = new();
    private static readonly TimeSpan RoomListLifetime = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaximumAvailableRooms = 20_000;
    private readonly SemaphoreSlim roomMutationGate = new(1, 1);
    private readonly SemaphoreSlim roomListGate = new(1, 1);
    private readonly Dictionary<string, RoomSession> roomSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirtyRoomKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> configuredRooms;
    private readonly HashSet<string> runtimeDesiredRooms = new(StringComparer.Ordinal);
    private AvailableRoomCache? availableRoomCache;

    internal ChatRooms(EngineSettings settings, DaemonSoulseekRuntime soulseek,
        ChatPersistenceStore store, ILogger logger)
    {
        this.settings = settings;
        this.soulseek = soulseek;
        this.store = store;
        this.logger = logger;
        configuredRooms = settings.Chat.AutoJoinRooms
            .Select(ChatIdentity.NormalizeRoom)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal event Action? StateChanged;
    internal event Action<ChatTargetDeltaDto>? TargetChanged;

    private string Account => soulseek.ClientManager.LoggedInUsername ?? settings.Username
        ?? throw new InvalidOperationException("Chat is unavailable before Soulseek login.");
    private ISoulseekClient Client => soulseek.ClientManager.Client
        ?? throw new InvalidOperationException("The Soulseek client is unavailable.");

    private void EnsureReady()
    {
        if (!soulseek.ClientManager.IsConnectedAndLoggedIn)
            throw new InvalidOperationException("Chat is unavailable while Soulseek is disconnected.");
    }

    internal void EnsureJoined(string roomKey)
    {
        if (GetRoomSession(roomKey).Phase != ChatRoomJoinPhase.Joined)
            throw new ChatStateConflictException("The room is not joined.");
    }

    internal (int Joined, int Desired) GetCounts()
    {
        lock (stateGate)
            return (roomSessions.Values.Count(room => room.Phase == ChatRoomJoinPhase.Joined),
                configuredRooms.Union(runtimeDesiredRooms, StringComparer.Ordinal).Count());
    }

    public void Dispose()
    {
        roomMutationGate.Dispose();
        roomListGate.Dispose();
    }

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
        StateChanged?.Invoke();
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
                    session.FailureReason = ChatLogMessages.CompactFailure(ex);
                }
                await TryPublishRoomFailureAsync(persisted.RoomId).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            roomMutationGate.Release();
        }
        StateChanged?.Invoke();
        await PublishRoomAsync(persisted.RoomId, null, CancellationToken.None).ConfigureAwait(false);
        ChatLogMessages.RoomLeft(
            logger,
            persisted.RoomId,
            LogIdentity.Hash(persisted.RoomKey));
        return MapRoom(persisted);
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

    internal async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        var desiredRows = await store.GetDesiredRoomsAsync(Account, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            runtimeDesiredRooms.Clear();
            foreach (string configured in configuredRooms)
                GetOrCreateSessionLocked(configured, configured);
            foreach (var room in desiredRows)
            {
                runtimeDesiredRooms.Add(room.RoomKey);
                GetOrCreateSessionLocked(room.RoomKey, room.DisplayName);
            }
        }
        if (!soulseek.ClientManager.IsConnectedAndLoggedIn)
            return;
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
                    session.Reset(ChatRoomJoinPhase.Failed, ChatLogMessages.CompactFailure(ex));
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

    internal void InvalidateAvailableRooms()
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

    internal async Task RosterJoinedAsync(string roomName, RoomMemberDto member, CancellationToken cancellationToken)
    {
        UpdateRoster(roomName, member.Username, member);
        await PublishRoomByNameAsync(roomName, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RosterLeftAsync(string roomName, string username, CancellationToken cancellationToken)
    {
        UpdateRoster(roomName, username, member: null);
        await PublishRoomByNameAsync(roomName, cancellationToken).ConfigureAwait(false);
    }

    private void UpdateRoster(string roomName, string username, RoomMemberDto? member)
    {
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        string memberKey = ChatIdentity.ValidateUsername(username);
        lock (stateGate)
        {
            if (!roomSessions.TryGetValue(roomKey, out RoomSession? session))
                return;
            RosterMutation mutation = member is null ? new RosterRemove(memberKey)
                : new RosterAdd(memberKey, member with
                {
                    IsOwner = session.Owner is not null
                        && ChatIdentity.ValidateUsername(session.Owner) == memberKey,
                    IsOperator = session.Operators.Any(item =>
                        ChatIdentity.ValidateUsername(item) == memberKey),
                });
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

    internal async Task ModerationChangedAsync(string roomName, bool moderated, CancellationToken cancellationToken)
    {
        ApplyCurrentAccountModeration(roomName, moderated);
        InvalidateAvailableRooms();
        await PublishRoomByNameAsync(roomName, cancellationToken).ConfigureAwait(false);
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

    internal void MarkRosterIncomplete(string roomName, string reason)
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

    internal async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        lock (stateGate)
        {
            foreach (RoomSession session in roomSessions.Values)
                session.Reset(ChatRoomJoinPhase.Disconnected, null);
        }
        await PublishKnownRoomsAsync(cancellationToken).ConfigureAwait(false);
        PruneNonDesiredRoomSessions();
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

    internal async Task PublishDirtyRoomsAsync(CancellationToken cancellationToken)
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

    internal ChatRoomSummaryDto MapRoom(RoomSubscriptionRecord room)
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

    internal async Task PublishRoomAsync(
        Guid roomId,
        ChatMessageRecord? message,
        CancellationToken cancellationToken)
    {
        var room = await store.GetRoomAsync(Account, roomId, cancellationToken).ConfigureAwait(false);
        if (room is null)
            return;
        TargetChanged?.Invoke(new ChatTargetDeltaDto(
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
            StateChanged?.Invoke();
            await PublishRoomAsync(roomId, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ChatLogMessages.CallbackFailed(logger, ex, "publish-failed-room");
        }
    }

    internal static RoomMemberDto MapMember(
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
