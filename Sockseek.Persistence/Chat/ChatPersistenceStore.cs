using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sockseek.Core.Chat;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Chat;

public sealed record ChatStoreSummary(
    int UnreadPrivateMessages,
    int UnreadRoomMessages,
    int UnreadNotifications,
    long Revision);

public sealed record ChatRetentionTarget(ChatTargetKind Kind, Guid TargetId);
public sealed record ChatRetentionResult(
    int PrunedMessages,
    IReadOnlyList<ChatRetentionTarget> AffectedTargets);

public abstract record ChatInboundMessage(string LocalAccount, string Body);

public sealed record PrivateChatInboundMessage(
    string LocalAccount,
    string Username,
    string Body,
    int ProtocolMessageId,
    DateTimeOffset ProtocolTimestamp)
    : ChatInboundMessage(LocalAccount, Body);

public sealed record RoomChatInboundMessage(
    string LocalAccount,
    string RoomName,
    string Username,
    string Body,
    bool CreateNotification)
    : ChatInboundMessage(LocalAccount, Body);

public sealed class ChatPersistenceStore(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceInbox inbox,
    TimeProvider? timeProvider = null)
{
    private const int FilteredRoomReadBatchSize = 500;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<int> ReconcilePendingMessagesAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(async (context, ct) =>
            await context.ChatMessages
                .Where(message => message.SendState == nameof(ChatMessageState.Pending))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        message => message.SendState,
                        nameof(ChatMessageState.Unknown)),
                    ct)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public async Task<IncomingChatCommitResult> AcceptPrivateMessageAsync(
        string localAccount,
        string username,
        string body,
        int protocolMessageId,
        DateTimeOffset protocolTimestamp,
        CancellationToken cancellationToken = default)
        => (await AcceptIncomingMessagesAsync(
            [new PrivateChatInboundMessage(
                localAccount, username, body, protocolMessageId, protocolTimestamp)],
            cancellationToken).ConfigureAwait(false))[0];

    public async Task<IncomingChatCommitResult> AcceptRoomMessageAsync(
        string localAccount,
        string roomName,
        string username,
        string body,
        bool createNotification,
        CancellationToken cancellationToken = default)
        => (await AcceptIncomingMessagesAsync(
            [new RoomChatInboundMessage(
                localAccount, roomName, username, body, createNotification)],
            cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<IncomingChatCommitResult>> AcceptIncomingMessagesAsync(
        IReadOnlyList<ChatInboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            return [];

        NormalizedInboundMessage[] normalized = messages.Select(NormalizeInboundMessage).ToArray();
        return await ExecuteAsync(async (context, ct) =>
        {
            ChatSequenceEntity sequence = await GetSequenceAsync(context, ct).ConfigureAwait(false);
            var results = new List<IncomingChatCommitResult>(normalized.Length);
            var unreadCounts = new Dictionary<Guid, int>();
            var knownPrivateMessages = new Dictionary<PrivateReplayKey, ChatMessageEntity>();
            // Avoid repeated queries and DbSet.Local change detection as the batch grows.
            var conversations = new Dictionary<(string Account, string Peer), ChatConversationEntity>();
            var rooms = new Dictionary<(string Account, string Room), ChatRoomSubscriptionEntity>();
            NormalizedPrivateMessage[] privateMessages = normalized.OfType<NormalizedPrivateMessage>().ToArray();
            if (privateMessages.Length > 0)
            {
                string[] accountKeys = privateMessages.Select(message => message.AccountKey).Distinct().ToArray();
                string[] peerKeys = privateMessages.Select(message => message.PeerKey).Distinct().ToArray();
                int[] protocolIds = privateMessages.Select(message => message.ProtocolMessageId).Distinct().ToArray();
                long[] protocolTimestamps = privateMessages.Select(message => message.ProtocolTimestamp).Distinct().ToArray();
                ChatMessageEntity[] existingPrivateMessages = await context.ChatMessages
                    .Where(message =>
                        message.TargetKind == nameof(ChatTargetKind.Direct)
                        && accountKeys.Contains(message.LocalAccountKey)
                        && peerKeys.Contains(message.TargetKey)
                        && message.ProtocolMessageId.HasValue
                        && protocolIds.Contains(message.ProtocolMessageId.Value)
                        && message.ProtocolTimestamp.HasValue
                        && protocolTimestamps.Contains(message.ProtocolTimestamp.Value))
                    .ToArrayAsync(ct)
                    .ConfigureAwait(false);
                foreach (ChatMessageEntity message in existingPrivateMessages)
                    knownPrivateMessages.Add(PrivateReplayKey.For(message), message);
            }

            async Task<int> GetUnreadCountAsync(
                string accountKey,
                Guid targetId,
                long afterSequence,
                bool excludeLocalSender)
            {
                if (unreadCounts.TryGetValue(targetId, out int count))
                    return count;

                var query = context.ChatMessages.Where(item =>
                    item.LocalAccountKey == accountKey
                    && item.TargetId == targetId
                    && item.Direction == nameof(ChatMessageDirection.Incoming)
                    && item.Sequence > afterSequence);
                if (excludeLocalSender)
                    query = query.Where(item => item.SenderKey != accountKey);
                count = await query.CountAsync(ct).ConfigureAwait(false);
                unreadCounts.Add(targetId, count);
                return count;
            }

            foreach (NormalizedInboundMessage inbound in normalized)
            {
                long now = ToUnixMilliseconds(clock.GetUtcNow());
                if (inbound is NormalizedPrivateMessage direct)
                {
                    var target = (direct.AccountKey, direct.PeerKey);
                    if (!conversations.TryGetValue(target, out var conversation))
                    {
                        conversation = await context.ChatConversations.FirstOrDefaultAsync(
                            item => item.LocalAccountKey == direct.AccountKey && item.PeerKey == direct.PeerKey, ct)
                            .ConfigureAwait(false);
                        if (conversation is null)
                        {
                            conversation = new ChatConversationEntity
                            {
                                Id = Guid.NewGuid(),
                                LocalAccountKey = direct.AccountKey,
                                PeerKey = direct.PeerKey,
                                DisplayUsername = direct.DisplayUsername,
                                CreatedAtUtc = now,
                                UpdatedAtUtc = now,
                            };
                            context.ChatConversations.Add(conversation);
                        }
                        conversations.Add(target, conversation);
                    }

                    await GetUnreadCountAsync(
                            direct.AccountKey,
                            conversation.Id,
                            conversation.LastReadSequence,
                            excludeLocalSender: false)
                        .ConfigureAwait(false);
                    var replayKey = PrivateReplayKey.For(direct);
                    knownPrivateMessages.TryGetValue(replayKey, out ChatMessageEntity? existing);
                    if (existing is not null)
                    {
                        ChatMessageEntity? lastMessage = context.ChatMessages.Local.FirstOrDefault(
                            item => item.Sequence == conversation.LastMessageSequence);
                        lastMessage ??= await context.ChatMessages.AsNoTracking().SingleOrDefaultAsync(
                            item => item.Sequence == conversation.LastMessageSequence, ct).ConfigureAwait(false);
                        results.Add(new IncomingChatCommitResult(
                            false,
                            MapMessage(existing),
                            MapConversation(conversation, unreadCounts[conversation.Id], lastMessage),
                            null,
                            null));
                        continue;
                    }

                    long messageSequence = checked(++sequence.LastMessageSequence);
                    var message = new ChatMessageEntity
                    {
                        Id = Guid.NewGuid(),
                        Sequence = messageSequence,
                        LocalAccountKey = direct.AccountKey,
                        TargetKind = nameof(ChatTargetKind.Direct),
                        TargetId = conversation.Id,
                        TargetKey = direct.PeerKey,
                        DisplayTarget = direct.DisplayUsername,
                        SenderKey = direct.PeerKey,
                        DisplaySender = direct.DisplayUsername,
                        Direction = nameof(ChatMessageDirection.Incoming),
                        Body = direct.Body,
                        OccurredAtUtc = direct.ProtocolTimestamp,
                        RecordedAtUtc = now,
                        SendState = nameof(ChatMessageState.Received),
                        ProtocolMessageId = direct.ProtocolMessageId,
                        ProtocolTimestamp = direct.ProtocolTimestamp,
                    };
                    context.ChatMessages.Add(message);
                    knownPrivateMessages.Add(replayKey, message);
                    conversation.DisplayUsername = direct.DisplayUsername;
                    conversation.ArchivedAtUtc = null;
                    conversation.LastMessageSequence = messageSequence;
                    conversation.UpdatedAtUtc = now;
                    conversation.Revision++;
                    unreadCounts[conversation.Id]++;

                    var notification = new NotificationEntity
                    {
                        Id = Guid.NewGuid(),
                        Sequence = checked(++sequence.LastNotificationSequence),
                        LocalAccountKey = direct.AccountKey,
                        Kind = nameof(UserNotificationKind.PrivateMessage),
                        SourceMessageId = message.Id,
                        CreatedAtUtc = now,
                    };
                    context.Notifications.Add(notification);
                    results.Add(new IncomingChatCommitResult(
                        true,
                        MapMessage(message),
                        MapConversation(conversation, unreadCounts[conversation.Id], message),
                        null,
                        MapNotification(notification, message)));
                    continue;
                }

                var roomMessage = (NormalizedRoomMessage)inbound;
                var roomTarget = (roomMessage.AccountKey, roomMessage.RoomKey);
                if (!rooms.TryGetValue(roomTarget, out var room))
                {
                    room = await GetOrCreateRoomEntityAsync(
                        context,
                        roomMessage.AccountKey,
                        roomMessage.RoomKey,
                        roomMessage.DisplayRoomName,
                        now,
                        ct).ConfigureAwait(false);
                    rooms.Add(roomTarget, room);
                }
                await GetUnreadCountAsync(
                        roomMessage.AccountKey,
                        room.Id,
                        room.LastReadSequence,
                        excludeLocalSender: true)
                    .ConfigureAwait(false);
                long roomMessageSequence = checked(++sequence.LastMessageSequence);
                var roomEntity = new ChatMessageEntity
                {
                    Id = Guid.NewGuid(),
                    Sequence = roomMessageSequence,
                    LocalAccountKey = roomMessage.AccountKey,
                    TargetKind = nameof(ChatTargetKind.Room),
                    TargetId = room.Id,
                    TargetKey = roomMessage.RoomKey,
                    DisplayTarget = roomMessage.DisplayRoomName,
                    SenderKey = roomMessage.SenderKey,
                    DisplaySender = roomMessage.DisplayUsername,
                    Direction = nameof(ChatMessageDirection.Incoming),
                    Body = roomMessage.Body,
                    OccurredAtUtc = now,
                    RecordedAtUtc = now,
                    SendState = nameof(ChatMessageState.Received),
                };
                context.ChatMessages.Add(roomEntity);
                room.LastMessageSequence = roomMessageSequence;
                room.UpdatedAtUtc = now;
                room.Revision++;
                if (roomMessage.SenderKey != roomMessage.AccountKey)
                    unreadCounts[room.Id]++;

                NotificationEntity? roomNotification = null;
                if (roomMessage.CreateNotification)
                {
                    roomNotification = new NotificationEntity
                    {
                        Id = Guid.NewGuid(),
                        Sequence = checked(++sequence.LastNotificationSequence),
                        LocalAccountKey = roomMessage.AccountKey,
                        Kind = nameof(UserNotificationKind.RoomMention),
                        SourceMessageId = roomEntity.Id,
                        CreatedAtUtc = now,
                    };
                    context.Notifications.Add(roomNotification);
                }
                results.Add(new IncomingChatCommitResult(
                    true,
                    MapMessage(roomEntity),
                    null,
                    MapRoom(room, unreadCounts[room.Id], roomEntity),
                    roomNotification is null ? null : MapNotification(roomNotification, roomEntity)));
            }

            return results.ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutgoingChatPreparationResult> PrepareOutgoingPrivateMessageAsync(
        string localAccount,
        string username,
        Guid messageId,
        string body,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        string peerKey = ChatIdentity.ValidateUsername(username);
        body = ChatIdentity.ValidateMessage(body);
        return await PrepareOutgoingAsync(
            accountKey, ChatTargetKind.Direct, peerKey, username.Trim(), messageId, body,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutgoingChatPreparationResult> PrepareOutgoingRoomMessageAsync(
        string localAccount,
        Guid roomId,
        Guid messageId,
        string body,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        body = ChatIdentity.ValidateMessage(body);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var room = await context.ChatRoomSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == roomId && item.LocalAccountKey == accountKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The room was not found.");
        return await PrepareOutgoingAsync(
            accountKey, ChatTargetKind.Room, room.RoomKey, room.DisplayName, messageId, body,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutgoingChatPreparationResult> PrepareOutgoingAsync(
        string accountKey,
        ChatTargetKind targetKind,
        string targetKey,
        string displayTarget,
        Guid messageId,
        string body,
        CancellationToken cancellationToken)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("MessageId cannot be empty.", nameof(messageId));

        var prepared = await ExecuteAsync(async (context, ct) =>
        {
            var existing = await context.ChatMessages.FirstOrDefaultAsync(
                item => item.Id == messageId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                bool same = existing.LocalAccountKey == accountKey
                            && existing.TargetKind == targetKind.ToString()
                            && existing.TargetKey == targetKey
                            && existing.Direction == nameof(ChatMessageDirection.Outgoing)
                            && existing.Body == body;
                return new OutgoingCommandResult(
                    same ? OutgoingChatPreparationStatus.Existing : OutgoingChatPreparationStatus.Conflict,
                    existing.Id);
            }

            long now = ToUnixMilliseconds(clock.GetUtcNow());
            Guid targetId;
            if (targetKind == ChatTargetKind.Direct)
            {
                var conversation = await context.ChatConversations.FirstOrDefaultAsync(
                    item => item.LocalAccountKey == accountKey && item.PeerKey == targetKey, ct).ConfigureAwait(false);
                if (conversation is null)
                {
                    conversation = new ChatConversationEntity
                    {
                        Id = Guid.NewGuid(),
                        LocalAccountKey = accountKey,
                        PeerKey = targetKey,
                        DisplayUsername = displayTarget,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    context.ChatConversations.Add(conversation);
                }
                targetId = conversation.Id;
                conversation.ArchivedAtUtc = null;
                conversation.DisplayUsername = displayTarget;
                conversation.UpdatedAtUtc = now;
                conversation.Revision++;
            }
            else
            {
                var room = await GetOrCreateRoomEntityAsync(
                    context, accountKey, targetKey, displayTarget, now, ct).ConfigureAwait(false);
                targetId = room.Id;
                room.UpdatedAtUtc = now;
                room.Revision++;
            }

            long sequence = await NextMessageSequenceAsync(context, ct).ConfigureAwait(false);
            var message = new ChatMessageEntity
            {
                Id = messageId,
                Sequence = sequence,
                LocalAccountKey = accountKey,
                TargetKind = targetKind.ToString(),
                TargetId = targetId,
                TargetKey = targetKey,
                DisplayTarget = displayTarget,
                SenderKey = accountKey,
                DisplaySender = accountKey,
                Direction = nameof(ChatMessageDirection.Outgoing),
                Body = body,
                OccurredAtUtc = now,
                RecordedAtUtc = now,
                SendState = nameof(ChatMessageState.Pending),
            };
            context.ChatMessages.Add(message);
            if (targetKind == ChatTargetKind.Direct)
            {
                var conversation = context.ChatConversations.Local.Single(item => item.Id == targetId);
                conversation.LastMessageSequence = sequence;
            }
            else
            {
                var room = await context.ChatRoomSubscriptions.SingleAsync(item => item.Id == targetId, ct).ConfigureAwait(false);
                room.LastMessageSequence = sequence;
            }
            return new OutgoingCommandResult(OutgoingChatPreparationStatus.Created, message.Id);
        }, cancellationToken).ConfigureAwait(false);

        var messageRecord = await GetMessageAsync(accountKey, prepared.MessageId, cancellationToken).ConfigureAwait(false);
        if (messageRecord is null)
            throw new ChatStateConflictException(
                "MessageId was already used by another local account.");
        ConversationRecord? conversationRecord = messageRecord.TargetKind == ChatTargetKind.Direct
            ? await GetConversationAsync(accountKey, messageRecord.TargetId, cancellationToken).ConfigureAwait(false)
            : null;
        RoomSubscriptionRecord? roomRecord = messageRecord.TargetKind == ChatTargetKind.Room
            ? await GetRoomAsync(accountKey, messageRecord.TargetId, cancellationToken).ConfigureAwait(false)
            : null;
        return new OutgoingChatPreparationResult(
            prepared.Status, messageRecord, conversationRecord, roomRecord);
    }

    public async Task<ChatMessageRecord> SetMessageStateAsync(
        string localAccount,
        Guid messageId,
        ChatMessageState state,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        failureReason = failureReason is null
            ? null
            : failureReason[..Math.Min(failureReason.Length, ChatLimits.MaximumFailureReasonLength)];
        await ExecuteAsync(async (context, ct) =>
        {
            var message = await context.ChatMessages.SingleOrDefaultAsync(
                item => item.Id == messageId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The message was not found.");
            if (message.Direction != nameof(ChatMessageDirection.Outgoing))
                throw new InvalidOperationException("Only outgoing message state can be changed.");
            message.SendState = state.ToString();
            message.FailureReason = failureReason;
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return await GetMessageAsync(accountKey, messageId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The updated message was not found.");
    }

    public async Task<ConversationRecord?> GetConversationAsync(
        string localAccount,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ConversationProjection? row = await ProjectConversations(
                context,
                context.ChatConversations.AsNoTracking().Where(
                    item => item.Id == conversationId && item.LocalAccountKey == accountKey))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : MapConversation(row.Entity, row.Unread, row.LastMessage);
    }

    public async Task<ConversationRecord?> GetConversationByPeerAsync(
        string localAccount,
        string username,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        string peerKey = ChatIdentity.ValidateUsername(username);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ConversationProjection? row = await ProjectConversations(
                context,
                context.ChatConversations.AsNoTracking().Where(
                    item => item.LocalAccountKey == accountKey && item.PeerKey == peerKey))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : MapConversation(row.Entity, row.Unread, row.LastMessage);
    }

    public async Task<ChatPage<ConversationRecord>> GetConversationsAsync(
        string localAccount,
        bool? unread,
        bool? archived,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        var position = DecodeSummaryCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<ChatConversationEntity> query = context.ChatConversations.AsNoTracking()
            .Where(item => item.LocalAccountKey == accountKey);
        if (archived is not null)
            query = archived.Value ? query.Where(item => item.ArchivedAtUtc != null) : query.Where(item => item.ArchivedAtUtc == null);
        if (unread is not null)
        {
            query = unread.Value
                ? query.Where(item => context.ChatMessages.Any(message =>
                    message.LocalAccountKey == accountKey
                    && message.TargetId == item.Id
                    && message.Direction == nameof(ChatMessageDirection.Incoming)
                    && message.Sequence > item.LastReadSequence))
                : query.Where(item => !context.ChatMessages.Any(message =>
                    message.LocalAccountKey == accountKey
                    && message.TargetId == item.Id
                    && message.Direction == nameof(ChatMessageDirection.Incoming)
                    && message.Sequence > item.LastReadSequence));
        }
        if (position is not null)
            query = query.Where(item => item.LastMessageSequence < position.Value.Sequence
                || item.LastMessageSequence == position.Value.Sequence && item.Id.CompareTo(position.Value.Id) < 0);
        var rows = await ProjectConversations(
                context,
                query.OrderByDescending(item => item.LastMessageSequence)
                    .ThenByDescending(item => item.Id)
                    .Take(limit == int.MaxValue ? limit : limit + 1))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = rows.Take(limit)
            .Select(row => MapConversation(row.Entity, row.Unread, row.LastMessage))
            .ToArray();
        string? next = rows.Count > limit && records.Length > 0
            ? EncodeSummaryCursor(records[^1].LastMessageSequence, records[^1].ConversationId)
            : null;
        return new ChatPage<ConversationRecord>(records, next);
    }

    public async Task<ChatPage<ChatMessageRecord>> GetMessagesAsync(
        string localAccount,
        Guid targetId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        long? before = DecodeSequenceCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.ChatMessages.AsNoTracking().Where(
            item => item.LocalAccountKey == accountKey && item.TargetId == targetId);
        if (before is not null)
            query = query.Where(item => item.Sequence < before.Value);
        var entities = await query.OrderByDescending(item => item.Sequence)
            .Take(limit == int.MaxValue ? limit : limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = entities.Take(limit).Select(MapMessage).Reverse().ToArray();
        string? next = entities.Count > limit && items.Length > 0
            ? EncodeSequenceCursor(items[0].Sequence)
            : null;
        return new ChatPage<ChatMessageRecord>(items, next);
    }

    public async Task<RoomSubscriptionRecord> UpsertRoomAsync(
        string localAccount,
        string roomName,
        bool runtimeDesired,
        ChatRoomKind kind,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        Guid roomId = await ExecuteAsync(async (context, ct) =>
        {
            long now = ToUnixMilliseconds(clock.GetUtcNow());
            var room = await GetOrCreateRoomEntityAsync(
                context, accountKey, roomKey, roomName.Trim(), now, ct).ConfigureAwait(false);
            room.DisplayName = roomName.Trim();
            room.RuntimeDesired = runtimeDesired;
            if (kind != ChatRoomKind.Unknown)
                room.Kind = kind.ToString();
            room.UpdatedAtUtc = now;
            room.Revision++;
            return room.Id;
        }, cancellationToken).ConfigureAwait(false);
        return await GetRoomAsync(accountKey, roomId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The room subscription was not persisted.");
    }

    public async Task<RoomSubscriptionRecord?> GetRoomAsync(
        string localAccount,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        RoomProjection? row = await ProjectRooms(
                context,
                context.ChatRoomSubscriptions.AsNoTracking().Where(
                    item => item.Id == roomId && item.LocalAccountKey == accountKey))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : MapRoom(row.Entity, row.Unread, row.LastMessage);
    }

    public async Task<RoomSubscriptionRecord?> GetRoomByNameAsync(
        string localAccount,
        string roomName,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        RoomProjection? row = await ProjectRooms(
                context,
                context.ChatRoomSubscriptions.AsNoTracking().Where(
                    item => item.LocalAccountKey == accountKey && item.RoomKey == roomKey))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : MapRoom(row.Entity, row.Unread, row.LastMessage);
    }

    public async Task<ChatPage<RoomSubscriptionRecord>> GetRoomsAsync(
        string localAccount,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        var position = DecodeSummaryCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.ChatRoomSubscriptions.AsNoTracking().Where(item => item.LocalAccountKey == accountKey);
        if (position is not null)
            query = query.Where(item => item.LastMessageSequence < position.Value.Sequence
                || item.LastMessageSequence == position.Value.Sequence && item.Id.CompareTo(position.Value.Id) < 0);
        var rows = await ProjectRooms(
                context,
                query.OrderByDescending(item => item.LastMessageSequence)
                    .ThenByDescending(item => item.Id)
                    .Take(limit == int.MaxValue ? limit : limit + 1))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = rows.Take(limit)
            .Select(row => MapRoom(row.Entity, row.Unread, row.LastMessage))
            .ToArray();
        string? next = rows.Count > limit && records.Length > 0
            ? EncodeSummaryCursor(records[^1].LastMessageSequence, records[^1].RoomId)
            : null;
        return new ChatPage<RoomSubscriptionRecord>(records, next);
    }

    public async Task<ChatPage<RoomSubscriptionRecord>> GetFilteredRoomsAsync(
        string localAccount,
        Func<RoomSubscriptionRecord, bool> predicate,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        limit = ChatIdentity.ValidatePageSize(limit);
        var matches = new List<RoomSubscriptionRecord>();
        string? next = cursor;
        do
        {
            ChatPage<RoomSubscriptionRecord> page = await GetRoomsAsync(
                localAccount,
                next,
                FilteredRoomReadBatchSize,
                cancellationToken).ConfigureAwait(false);
            foreach (RoomSubscriptionRecord room in page.Items)
            {
                if (predicate(room))
                    matches.Add(room);
                if (matches.Count > limit)
                {
                    RoomSubscriptionRecord last = matches[limit - 1];
                    return new ChatPage<RoomSubscriptionRecord>(
                        matches.Take(limit).ToArray(),
                        EncodeSummaryCursor(last.LastMessageSequence, last.RoomId));
                }
            }
            next = page.NextCursor;
        } while (next is not null);

        return new ChatPage<RoomSubscriptionRecord>(matches, null);
    }

    public async Task<IReadOnlyList<RoomSubscriptionRecord>> GetDesiredRoomsAsync(
        string localAccount,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ProjectRooms(
                context,
                context.ChatRoomSubscriptions.AsNoTracking()
                    .Where(item => item.LocalAccountKey == accountKey && item.RuntimeDesired)
                    .OrderBy(item => item.DisplayName))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => MapRoom(row.Entity, row.Unread, row.LastMessage)).ToArray();
    }

    public Task MarkConversationReadAsync(
        string localAccount, Guid conversationId, Guid throughMessageId, CancellationToken cancellationToken = default)
        => MarkTargetReadAsync(localAccount, conversationId, throughMessageId, ChatTargetKind.Direct, cancellationToken);

    public Task MarkRoomReadAsync(
        string localAccount, Guid roomId, Guid throughMessageId, CancellationToken cancellationToken = default)
        => MarkTargetReadAsync(localAccount, roomId, throughMessageId, ChatTargetKind.Room, cancellationToken);

    private async Task MarkTargetReadAsync(
        string localAccount,
        Guid targetId,
        Guid throughMessageId,
        ChatTargetKind kind,
        CancellationToken cancellationToken)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await ExecuteAsync(async (context, ct) =>
        {
            var through = await context.ChatMessages.SingleOrDefaultAsync(
                item => item.Id == throughMessageId && item.LocalAccountKey == accountKey && item.TargetId == targetId, ct)
                .ConfigureAwait(false) ?? throw new KeyNotFoundException("The message was not found in this chat target.");
            if (kind == ChatTargetKind.Direct)
            {
                var target = await context.ChatConversations.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The conversation was not found.");
                if (through.Sequence > target.LastReadSequence)
                {
                    target.LastReadSequence = through.Sequence;
                    target.Revision++;
                    target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
                }
            }
            else
            {
                var target = await context.ChatRoomSubscriptions.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The room was not found.");
                if (through.Sequence > target.LastReadSequence)
                {
                    target.LastReadSequence = through.Sequence;
                    target.Revision++;
                    target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
                }
            }
            long now = ToUnixMilliseconds(clock.GetUtcNow());
            await context.Notifications
                .Where(notification =>
                    notification.LocalAccountKey == accountKey
                    && notification.ReadAtUtc == null
                    && context.ChatMessages.Any(message =>
                        message.Id == notification.SourceMessageId
                        && message.TargetId == targetId
                        && message.Sequence <= through.Sequence))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(notification => notification.ReadAtUtc, now),
                    ct)
                .ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ArchiveConversationAsync(
        string localAccount, Guid conversationId, bool archived, CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await ExecuteAsync(async (context, ct) =>
        {
            var conversation = await context.ChatConversations.SingleOrDefaultAsync(
                item => item.Id == conversationId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The conversation was not found.");
            conversation.ArchivedAtUtc = archived ? ToUnixMilliseconds(clock.GetUtcNow()) : null;
            conversation.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
            conversation.Revision++;
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteHistoryAsync(
        string localAccount,
        Guid targetId,
        ChatTargetKind kind,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await ExecuteAsync(async (context, ct) =>
        {
            int removed = await context.ChatMessages
                .Where(item => item.LocalAccountKey == accountKey && item.TargetId == targetId)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (kind == ChatTargetKind.Direct)
            {
                var target = await context.ChatConversations.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The conversation was not found.");
                target.LastMessageSequence = 0;
                target.LastReadSequence = 0;
                target.Revision++;
                target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
            }
            else
            {
                var target = await context.ChatRoomSubscriptions.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The room was not found.");
                target.LastMessageSequence = 0;
                target.LastReadSequence = 0;
                target.Revision++;
                target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
            }
            return removed;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatPage<UserNotificationRecord>> GetNotificationsAsync(
        string localAccount,
        bool? unread,
        UserNotificationKind? kind,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        long? before = DecodeSequenceCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Notifications.AsNoTracking().Where(item => item.LocalAccountKey == accountKey);
        if (unread is not null)
            query = unread.Value ? query.Where(item => item.ReadAtUtc == null) : query.Where(item => item.ReadAtUtc != null);
        if (kind is not null)
        {
            string kindValue = kind.Value.ToString();
            query = query.Where(item => item.Kind == kindValue);
        }
        if (before is not null)
            query = query.Where(item => item.Sequence < before.Value);
        var rows = await ProjectNotifications(
                context,
                query.OrderByDescending(item => item.Sequence).Take(limit == int.MaxValue ? limit : limit + 1))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = rows.Take(limit)
            .Select(row => MapNotification(row.Entity, row.Message))
            .ToArray();
        string? next = rows.Count > limit && records.Length > 0
            ? EncodeSequenceCursor(records[^1].Sequence)
            : null;
        return new ChatPage<UserNotificationRecord>(records, next);
    }

    public async Task<UserNotificationRecord?> GetNotificationAsync(
        string localAccount, Guid notificationId, CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        NotificationProjection? row = await ProjectNotifications(
                context,
                context.Notifications.AsNoTracking().Where(
                    item => item.Id == notificationId && item.LocalAccountKey == accountKey))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : MapNotification(row.Entity, row.Message);
    }

    public async Task MarkNotificationsReadAsync(
        string localAccount,
        long? throughSequence,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        if ((throughSequence is null) == (ids is null))
            throw new ArgumentException("Specify either throughSequence or ids.");
        if (throughSequence < 0)
            throw new ArgumentOutOfRangeException(
                nameof(throughSequence), "Notification sequence cannot be negative.");
        await ExecuteAsync(async (context, ct) =>
        {
            IQueryable<NotificationEntity> query = context.Notifications.Where(
                item => item.LocalAccountKey == accountKey && item.ReadAtUtc == null);
            query = throughSequence is not null
                ? query.Where(item => item.Sequence <= throughSequence.Value)
                : query.Where(item => ids!.Contains(item.Id));
            long now = ToUnixMilliseconds(clock.GetUtcNow());
            return await query.ExecuteUpdateAsync(
                    setters => setters.SetProperty(notification => notification.ReadAtUtc, now),
                    ct)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatStoreSummary> GetSummaryAsync(
        string localAccount,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.ValidateAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        int direct = await CountUnreadAsync(context, accountKey, ChatTargetKind.Direct, cancellationToken).ConfigureAwait(false);
        int room = await CountUnreadAsync(context, accountKey, ChatTargetKind.Room, cancellationToken).ConfigureAwait(false);
        int notifications = await context.Notifications.CountAsync(
            item => item.LocalAccountKey == accountKey && item.ReadAtUtc == null, cancellationToken).ConfigureAwait(false);
        long revision = Math.Max(
            await context.ChatConversations.Where(item => item.LocalAccountKey == accountKey)
                .Select(item => (long?)item.Revision).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
            await context.ChatRoomSubscriptions.Where(item => item.LocalAccountKey == accountKey)
                .Select(item => (long?)item.Revision).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0);
        return new ChatStoreSummary(direct, room, notifications, revision);
    }

    public async Task<ChatRetentionResult> ApplyRetentionAsync(
        TimeSpan? privateMessageAge,
        TimeSpan? roomMessageAge,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (privateMessageAge is null && roomMessageAge is null)
            return new ChatRetentionResult(0, []);
        if (privateMessageAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(privateMessageAge));
        if (roomMessageAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(roomMessageAge));
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        long now = ToUnixMilliseconds(clock.GetUtcNow());
        long? privateMessageCutoff = privateMessageAge is null
            ? null
            : now - (long)privateMessageAge.Value.TotalMilliseconds;
        long? roomMessageCutoff = roomMessageAge is null
            ? null
            : now - (long)roomMessageAge.Value.TotalMilliseconds;
        return await ExecuteAsync(async (context, ct) =>
        {
            var messages = await context.ChatMessages
                .AsNoTracking()
                .Where(item =>
                    privateMessageCutoff.HasValue
                    && item.TargetKind == nameof(ChatTargetKind.Direct)
                    && item.RecordedAtUtc < privateMessageCutoff.Value
                    || roomMessageCutoff.HasValue
                    && item.TargetKind == nameof(ChatTargetKind.Room)
                    && item.RecordedAtUtc < roomMessageCutoff.Value)
                .OrderBy(item => item.Sequence)
                .Take(batchSize)
                .ToListAsync(ct).ConfigureAwait(false);
            if (messages.Count == 0)
                return new ChatRetentionResult(0, []);
            Guid[] targetIds = messages.Select(item => item.TargetId).Distinct().ToArray();
            Guid[] messageIds = messages.Select(item => item.Id).ToArray();
            await context.ChatMessages
                .Where(item => messageIds.Contains(item.Id))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            Guid[] conversationIds = await context.ChatConversations
                .Where(item => targetIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToArrayAsync(ct).ConfigureAwait(false);
            Guid[] roomIds = await context.ChatRoomSubscriptions
                .Where(item => targetIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToArrayAsync(ct).ConfigureAwait(false);

            await context.ChatConversations
                .Where(item => conversationIds.Contains(item.Id))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            item => item.LastMessageSequence,
                            item => context.ChatMessages
                                .Where(message => message.TargetId == item.Id)
                                .Max(message => (long?)message.Sequence) ?? 0)
                        .SetProperty(
                            item => item.LastReadSequence,
                            item => Math.Min(
                                item.LastReadSequence,
                                context.ChatMessages
                                    .Where(message => message.TargetId == item.Id)
                                    .Max(message => (long?)message.Sequence) ?? 0))
                        .SetProperty(item => item.UpdatedAtUtc, now)
                        .SetProperty(item => item.Revision, item => item.Revision + 1),
                    ct).ConfigureAwait(false);
            await context.ChatRoomSubscriptions
                .Where(item => roomIds.Contains(item.Id))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            item => item.LastMessageSequence,
                            item => context.ChatMessages
                                .Where(message => message.TargetId == item.Id)
                                .Max(message => (long?)message.Sequence) ?? 0)
                        .SetProperty(
                            item => item.LastReadSequence,
                            item => Math.Min(
                                item.LastReadSequence,
                                context.ChatMessages
                                    .Where(message => message.TargetId == item.Id)
                                    .Max(message => (long?)message.Sequence) ?? 0))
                        .SetProperty(item => item.UpdatedAtUtc, now)
                        .SetProperty(item => item.Revision, item => item.Revision + 1),
                    ct).ConfigureAwait(false);

            var conversations = conversationIds.ToHashSet();
            var rooms = roomIds.ToHashSet();
            var affected = targetIds
                .Where(id => conversations.Contains(id) || rooms.Contains(id))
                .Select(id => new ChatRetentionTarget(
                    conversations.Contains(id) ? ChatTargetKind.Direct : ChatTargetKind.Room,
                    id))
                .ToArray();
            return new ChatRetentionResult(messages.Count, affected);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatMessageRecord?> GetMessageAsync(
        string accountKey,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.ChatMessages.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == messageId && item.LocalAccountKey == accountKey, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : MapMessage(entity);
    }

    private static async Task<ChatRoomSubscriptionEntity> GetOrCreateRoomEntityAsync(
        SockseekDbContext context,
        string accountKey,
        string roomKey,
        string displayName,
        long now,
        CancellationToken cancellationToken)
    {
        var room = context.ChatRoomSubscriptions.Local.FirstOrDefault(
                item => item.LocalAccountKey == accountKey && item.RoomKey == roomKey)
            ?? await context.ChatRoomSubscriptions.FirstOrDefaultAsync(
                item => item.LocalAccountKey == accountKey && item.RoomKey == roomKey, cancellationToken)
                .ConfigureAwait(false);
        if (room is not null)
            return room;
        room = new ChatRoomSubscriptionEntity
        {
            Id = Guid.NewGuid(),
            LocalAccountKey = accountKey,
            RoomKey = roomKey,
            DisplayName = displayName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.ChatRoomSubscriptions.Add(room);
        return room;
    }

    private static async Task<long> NextMessageSequenceAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
    {
        ChatSequenceEntity sequence = await GetSequenceAsync(context, cancellationToken).ConfigureAwait(false);
        sequence.LastMessageSequence = checked(sequence.LastMessageSequence + 1);
        return sequence.LastMessageSequence;
    }

    private static async Task<long> NextNotificationSequenceAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
    {
        ChatSequenceEntity sequence = await GetSequenceAsync(context, cancellationToken).ConfigureAwait(false);
        sequence.LastNotificationSequence = checked(sequence.LastNotificationSequence + 1);
        return sequence.LastNotificationSequence;
    }

    private static async Task<ChatSequenceEntity> GetSequenceAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
    {
        ChatSequenceEntity? sequence = context.ChatSequences.Local.FirstOrDefault(item => item.Id == 1)
            ?? await context.ChatSequences.SingleOrDefaultAsync(
            item => item.Id == 1, cancellationToken).ConfigureAwait(false);
        if (sequence is not null)
            return sequence;

        sequence = new ChatSequenceEntity
        {
            Id = 1,
            LastMessageSequence = await context.ChatMessages
                .Select(item => (long?)item.Sequence)
                .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
            LastNotificationSequence = await context.Notifications
                .Select(item => (long?)item.Sequence)
                .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
        };
        context.ChatSequences.Add(sequence);
        return sequence;
    }

    private static ChatMessageRecord MapMessage(ChatMessageEntity entity)
        => new(
            entity.Id,
            entity.Sequence,
            entity.LocalAccountKey,
            Enum.Parse<ChatTargetKind>(entity.TargetKind),
            entity.TargetId,
            entity.TargetKey,
            entity.DisplayTarget,
            entity.SenderKey,
            entity.DisplaySender,
            Enum.Parse<ChatMessageDirection>(entity.Direction),
            entity.Body,
            FromUnixMilliseconds(entity.OccurredAtUtc),
            FromUnixMilliseconds(entity.RecordedAtUtc),
            Enum.Parse<ChatMessageState>(entity.SendState),
            entity.FailureReason,
            entity.ProtocolMessageId,
            entity.ProtocolTimestamp is { } timestamp ? FromUnixMilliseconds(timestamp) : null);

    private static IQueryable<ConversationProjection> ProjectConversations(
        SockseekDbContext context,
        IQueryable<ChatConversationEntity> query)
        => query.Select(entity => new ConversationProjection(
            entity,
            context.ChatMessages.Count(message =>
                message.LocalAccountKey == entity.LocalAccountKey
                && message.TargetId == entity.Id
                && message.Direction == nameof(ChatMessageDirection.Incoming)
                && message.Sequence > entity.LastReadSequence),
            entity.LastMessageSequence == 0
                ? null
                : context.ChatMessages.FirstOrDefault(
                    message => message.Sequence == entity.LastMessageSequence)));

    private static ConversationRecord MapConversation(
        ChatConversationEntity entity,
        int unread,
        ChatMessageEntity? last)
        => new(
            entity.Id, entity.LocalAccountKey, entity.PeerKey, entity.DisplayUsername,
            entity.ArchivedAtUtc is { } archived ? FromUnixMilliseconds(archived) : null,
            entity.LastReadSequence, entity.LastMessageSequence, entity.Revision,
            FromUnixMilliseconds(entity.CreatedAtUtc), FromUnixMilliseconds(entity.UpdatedAtUtc),
            unread, last is null ? null : MapMessage(last));

    private static IQueryable<RoomProjection> ProjectRooms(
        SockseekDbContext context,
        IQueryable<ChatRoomSubscriptionEntity> query)
        => query.Select(entity => new RoomProjection(
            entity,
            context.ChatMessages.Count(message =>
                message.LocalAccountKey == entity.LocalAccountKey
                && message.TargetId == entity.Id
                && message.Direction == nameof(ChatMessageDirection.Incoming)
                && message.SenderKey != entity.LocalAccountKey
                && message.Sequence > entity.LastReadSequence),
            entity.LastMessageSequence == 0
                ? null
                : context.ChatMessages.FirstOrDefault(
                    message => message.Sequence == entity.LastMessageSequence)));

    private static RoomSubscriptionRecord MapRoom(
        ChatRoomSubscriptionEntity entity,
        int unread,
        ChatMessageEntity? last)
        => new(
            entity.Id, entity.LocalAccountKey, entity.RoomKey, entity.DisplayName,
            entity.RuntimeDesired, Enum.Parse<ChatRoomKind>(entity.Kind),
            entity.LastReadSequence, entity.LastMessageSequence, entity.Revision,
            FromUnixMilliseconds(entity.CreatedAtUtc), FromUnixMilliseconds(entity.UpdatedAtUtc),
            unread, last is null ? null : MapMessage(last));

    private static IQueryable<NotificationProjection> ProjectNotifications(
        SockseekDbContext context,
        IQueryable<NotificationEntity> query)
        => from entity in query
           join message in context.ChatMessages.AsNoTracking()
               on entity.SourceMessageId equals message.Id
           select new NotificationProjection(entity, message);

    private static UserNotificationRecord MapNotification(
        NotificationEntity entity,
        ChatMessageEntity message)
        => new(
            entity.Id, entity.Sequence, entity.LocalAccountKey,
            Enum.Parse<UserNotificationKind>(entity.Kind), entity.SourceMessageId,
            FromUnixMilliseconds(entity.CreatedAtUtc),
            entity.ReadAtUtc is { } read ? FromUnixMilliseconds(read) : null,
            MapMessage(message));

    private static async Task<int> CountUnreadAsync(
        SockseekDbContext context,
        string accountKey,
        ChatTargetKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == ChatTargetKind.Direct)
        {
            return await (
                from message in context.ChatMessages
                join target in context.ChatConversations on message.TargetId equals target.Id
                where message.LocalAccountKey == accountKey
                      && message.Direction == nameof(ChatMessageDirection.Incoming)
                      && message.Sequence > target.LastReadSequence
                select message).CountAsync(cancellationToken).ConfigureAwait(false);
        }
        return await (
            from message in context.ChatMessages
            join target in context.ChatRoomSubscriptions on message.TargetId equals target.Id
            where message.LocalAccountKey == accountKey
                  && message.Direction == nameof(ChatMessageDirection.Incoming)
                  && message.SenderKey != accountKey
                  && message.Sequence > target.LastReadSequence
            select message).CountAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<SockseekDbContext, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        var command = new AwaitablePersistenceCommand<TResult>(action);
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        // Once admitted, the command is durable intent. Do not let request
        // cancellation abandon it while the single writer still commits it.
        return await command.Task.ConfigureAwait(false);
    }

    private static string EncodeSequenceCursor(long sequence)
        => Base64UrlEncode(sequence.ToString(CultureInfo.InvariantCulture));

    private static long? DecodeSequenceCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length > 128 || !long.TryParse(Base64UrlDecode(cursor), NumberStyles.None,
                CultureInfo.InvariantCulture, out long sequence) || sequence < 1)
            throw new ArgumentException("The chat cursor is invalid.", nameof(cursor));
        return sequence;
    }

    private static string EncodeSummaryCursor(long sequence, Guid id)
        => Base64UrlEncode($"{sequence.ToString(CultureInfo.InvariantCulture)}:{id:D}");

    private static (long Sequence, Guid Id)? DecodeSummaryCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length > 256)
            throw new ArgumentException("The chat cursor is invalid.", nameof(cursor));
        string[] parts = Base64UrlDecode(cursor).Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
            || sequence < 0
            || !Guid.TryParseExact(parts[1], "D", out Guid id))
            throw new ArgumentException("The chat cursor is invalid.", nameof(cursor));
        return (sequence, id);
    }

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        if (value.Length is 0 or > 256)
            throw new ArgumentException("The chat cursor is invalid.", nameof(value));
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(padded)); }
        catch (FormatException ex) { throw new ArgumentException("The chat cursor is invalid.", nameof(value), ex); }
    }

    private static long ToUnixMilliseconds(DateTimeOffset value)
        => value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMilliseconds(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static NormalizedInboundMessage NormalizeInboundMessage(ChatInboundMessage message)
        => message switch
        {
            PrivateChatInboundMessage direct => new NormalizedPrivateMessage(
                ChatIdentity.ValidateAccount(direct.LocalAccount),
                ChatIdentity.ValidateUsername(direct.Username),
                direct.Username.Trim(),
                ChatIdentity.ValidateMessage(direct.Body),
                direct.ProtocolMessageId,
                ToUnixMilliseconds(direct.ProtocolTimestamp)),
            RoomChatInboundMessage room => new NormalizedRoomMessage(
                ChatIdentity.ValidateAccount(room.LocalAccount),
                ChatIdentity.NormalizeRoom(room.RoomName),
                room.RoomName.Trim(),
                ChatIdentity.ValidateUsername(room.Username),
                room.Username.Trim(),
                ChatIdentity.ValidateMessage(room.Body),
                room.CreateNotification),
            null => throw new ArgumentException("Inbound message lists cannot contain null items.", nameof(message)),
            _ => throw new ArgumentException(
                $"Unsupported inbound chat message type '{message.GetType().Name}'.", nameof(message)),
        };

    private abstract record NormalizedInboundMessage(string AccountKey, string Body);

    private sealed record NormalizedPrivateMessage(
        string AccountKey,
        string PeerKey,
        string DisplayUsername,
        string Body,
        int ProtocolMessageId,
        long ProtocolTimestamp)
        : NormalizedInboundMessage(AccountKey, Body);

    private sealed record NormalizedRoomMessage(
        string AccountKey,
        string RoomKey,
        string DisplayRoomName,
        string SenderKey,
        string DisplayUsername,
        string Body,
        bool CreateNotification)
        : NormalizedInboundMessage(AccountKey, Body);

    private readonly record struct PrivateReplayKey(
        string AccountKey,
        string PeerKey,
        int ProtocolMessageId,
        long ProtocolTimestamp)
    {
        public static PrivateReplayKey For(NormalizedPrivateMessage message)
            => new(message.AccountKey, message.PeerKey, message.ProtocolMessageId, message.ProtocolTimestamp);

        public static PrivateReplayKey For(ChatMessageEntity message)
            => new(
                message.LocalAccountKey,
                message.TargetKey,
                message.ProtocolMessageId!.Value,
                message.ProtocolTimestamp!.Value);
    }

    private sealed record OutgoingCommandResult(OutgoingChatPreparationStatus Status, Guid MessageId);

    private sealed record ConversationProjection(
        ChatConversationEntity Entity,
        int Unread,
        ChatMessageEntity? LastMessage);

    private sealed record RoomProjection(
        ChatRoomSubscriptionEntity Entity,
        int Unread,
        ChatMessageEntity? LastMessage);

    private sealed record NotificationProjection(
        NotificationEntity Entity,
        ChatMessageEntity Message);
}
