namespace Sockseek.Api;

public static class ChatProtocol
{
    public const int LiveMessageTailSize = 100;
}

public sealed record ChatMessageDto(
    Guid MessageId,
    long Sequence,
    ServerChatTargetKind TargetKind,
    Guid TargetId,
    string Sender,
    ServerChatMessageDirection Direction,
    string Text,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    ServerChatMessageState State,
    string? FailureReason);

public sealed record ConversationSummaryDto(
    Guid ConversationId,
    string Username,
    bool PrivateMessagesBlocked,
    bool Archived,
    int UnreadCount,
    long LastReadSequence,
    long Revision,
    ChatMessageDto? LastMessage);

public sealed record ConversationPageDto(
    IReadOnlyList<ConversationSummaryDto> Items,
    string? NextCursor);

public sealed record ChatMessagePageDto(
    IReadOnlyList<ChatMessageDto> Items,
    string? NextCursor);

public sealed record AvailableRoomDto(
    string Name,
    int UserCount,
    ServerChatRoomKind Kind,
    bool Owned,
    bool Moderated);

public sealed record AvailableRoomPageDto(
    IReadOnlyList<AvailableRoomDto> Items,
    string? NextCursor,
    DateTimeOffset ObservedAtUtc,
    bool Truncated);

public sealed record ChatRoomSummaryDto(
    Guid RoomId,
    string Name,
    bool Configured,
    bool Remembered,
    bool Desired,
    ServerChatRoomKind Kind,
    bool Owned,
    bool Moderated,
    ServerChatRoomJoinPhase Phase,
    string? FailureReason,
    int MemberCount,
    long MemberRevision,
    bool RosterComplete,
    int UnreadCount,
    long LastReadSequence,
    long Revision,
    ChatMessageDto? LastMessage);

public sealed record RoomMemberDto(
    string Username,
    string Presence,
    string? CountryCode,
    bool IsOwner,
    bool IsOperator);

public sealed record RoomMemberPageDto(
    IReadOnlyList<RoomMemberDto> Items,
    string? NextCursor,
    long Revision,
    bool Complete);

public sealed record ChatRoomDetailDto(
    ChatRoomSummaryDto Summary,
    string? Owner,
    IReadOnlyList<string> Operators);

public sealed record ChatRoomPageDto(
    IReadOnlyList<ChatRoomSummaryDto> Items,
    string? NextCursor);

public sealed record UserNotificationDto(
    Guid NotificationId,
    long Sequence,
    ServerUserNotificationKind Kind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc,
    string Actor,
    ServerChatTargetKind TargetKind,
    Guid TargetId,
    string TargetName,
    Guid SourceMessageId,
    string Preview,
    string ResourcePath);

public sealed record NotificationPageDto(
    IReadOnlyList<UserNotificationDto> Items,
    string? NextCursor);

public sealed record SendPrivateMessageRequestDto(
    Guid MessageId,
    string Username,
    string Text);

public sealed record SendChatMessageRequestDto(
    Guid MessageId,
    string Text);

public sealed record MarkChatReadRequestDto(Guid ThroughMessageId);

public sealed record ArchiveConversationRequestDto(bool Archived = true);

public sealed record JoinRoomRequestDto(string RoomName, bool Remember = true);

public sealed record AddRoomMemberRequestDto(string Username);

public sealed record MarkNotificationsReadRequestDto(
    long? ThroughSequence,
    IReadOnlyList<Guid>? Ids);
