using System.Text;
using System.Text.Json.Serialization;
using Sockseek.Core.Sharing;

namespace Sockseek.Core.Chat;

public static class ChatLimits
{
    public const int MaximumFailureReasonLength = 2_048;
    public const int MaximumDesiredRooms = 100;
    public const int MaximumRoomMembers = 20_000;
    public const int MaximumRoomOperators = 1_000;
    public const int MaximumProvisionalRosterChanges = 4_096;
    public const int NotificationPreviewCharacters = 240;
}

/// <summary>A bounded chat resource cannot accept more work right now.</summary>
public sealed class ChatCapacityException(string message) : InvalidOperationException(message);

/// <summary>The requested mutation conflicts with the target's current chat state.</summary>
public sealed class ChatStateConflictException(string message) : InvalidOperationException(message);

[JsonConverter(typeof(JsonStringEnumConverter<ChatTargetKind>))]
public enum ChatTargetKind
{
    Direct,
    Room,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageDirection>))]
public enum ChatMessageDirection
{
    Incoming,
    Outgoing,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageState>))]
public enum ChatMessageState
{
    Received,
    Pending,
    Sent,
    Failed,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatRoomKind>))]
public enum ChatRoomKind
{
    Unknown,
    Public,
    Private,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatRoomJoinPhase>))]
public enum ChatRoomJoinPhase
{
    Disconnected,
    Joining,
    Joined,
    Leaving,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<UserNotificationKind>))]
public enum UserNotificationKind
{
    PrivateMessage,
    RoomMention,
}

public sealed record ChatMessageRecord(
    Guid MessageId,
    long Sequence,
    string LocalAccountKey,
    ChatTargetKind TargetKind,
    Guid TargetId,
    string TargetKey,
    string DisplayTarget,
    string SenderKey,
    string DisplaySender,
    ChatMessageDirection Direction,
    string Body,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    ChatMessageState State,
    string? FailureReason,
    int? ProtocolMessageId,
    DateTimeOffset? ProtocolTimestamp);

public sealed record ConversationRecord(
    Guid ConversationId,
    string LocalAccountKey,
    string PeerKey,
    string DisplayUsername,
    DateTimeOffset? ArchivedAtUtc,
    long LastReadSequence,
    long LastMessageSequence,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int UnreadCount,
    ChatMessageRecord? LastMessage);

public sealed record RoomSubscriptionRecord(
    Guid RoomId,
    string LocalAccountKey,
    string RoomKey,
    string DisplayName,
    bool RuntimeDesired,
    ChatRoomKind Kind,
    long LastReadSequence,
    long LastMessageSequence,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int UnreadCount,
    ChatMessageRecord? LastMessage);

public sealed record UserNotificationRecord(
    Guid NotificationId,
    long Sequence,
    string LocalAccountKey,
    UserNotificationKind Kind,
    Guid SourceMessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc,
    ChatMessageRecord SourceMessage);

public sealed record ChatPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record IncomingChatCommitResult(
    bool Inserted,
    ChatMessageRecord Message,
    ConversationRecord? Conversation,
    RoomSubscriptionRecord? Room,
    UserNotificationRecord? Notification);

public enum OutgoingChatPreparationStatus
{
    Created,
    Existing,
    Conflict,
}

public sealed record OutgoingChatPreparationResult(
    OutgoingChatPreparationStatus Status,
    ChatMessageRecord Message,
    ConversationRecord? Conversation,
    RoomSubscriptionRecord? Room);

public static class ChatIdentity
{
    public static string ValidateAccount(string username)
        => ValidateUsername(username);

    public static string ValidateUsername(string username)
        => PeerUsername.Validate(username);

    public static string NormalizeRoom(string roomName)
    {
        ArgumentNullException.ThrowIfNull(roomName);
        string normalized = roomName.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Input error: Room name cannot be empty.", nameof(roomName));
        // Server code 14 rejects these names via a private message rather than
        // a failed join response. Validate before saving intent or waiting for a
        // join that cannot succeed. Do not replace characters and join another room.
        // https://nicotine-plus.org/doc/SLSKPROTOCOL.html#server-code-14
        if (normalized.Length > 24)
            throw new ArgumentException("Input error: Soulseek room names cannot exceed 24 characters.", nameof(roomName));
        if (normalized.Any(c => c < ' ' || c > '~'))
            throw new ArgumentException("Input error: Soulseek room names must contain only printable ASCII characters.", nameof(roomName));
        if (normalized.Contains("  ", StringComparison.Ordinal))
            throw new ArgumentException("Input error: Soulseek room names cannot contain consecutive spaces.", nameof(roomName));
        return normalized;
    }

    public static string ValidateMessage(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Input error: Message text cannot be empty.", nameof(body));
        if (body.IndexOf('\0') >= 0)
            throw new ArgumentException("Input error: Message text cannot contain NUL.", nameof(body));
        EnsureWellFormedUtf16(body, nameof(body));
        return body;
    }

    public static string ValidateOutgoingMessage(string body)
    {
        ValidateMessage(body);
        // The Soulseek server rejects outgoing private and room messages with
        // CR/LF, without a sender-visible failure. Incoming server messages may
        // contain line breaks, so keep this separate from received-text checks.
        // https://github.com/nicotine-plus/nicotine-plus/blob/master/pynicotine/privatechat.py
        if (body.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Input error: Soulseek messages cannot contain line breaks.", nameof(body));
        return body;
    }

    public static int ValidatePageSize(int value)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Page size must be positive.");
        return value;
    }

    private static void EnsureWellFormedUtf16(string value, string parameterName)
    {
        for (int index = 0; index < value.Length;)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(index), out _, out int consumed);
            if (status != System.Buffers.OperationStatus.Done)
                throw new ArgumentException("Input error: Text contains invalid UTF-16.", parameterName);
            index += consumed;
        }
    }
}

public static class MentionDetector
{
    public static bool ContainsWholeUsername(string message, string username)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(username);
        string candidate = username.Trim();
        if (candidate.Length == 0)
            return false;

        int searchFrom = 0;
        while (searchFrom < message.Length)
        {
            int index = message.IndexOf(candidate, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            int tokenStart = index > 0 && message[index - 1] == '@' ? index - 1 : index;
            bool leftBoundary = tokenStart == 0 || !IsTokenCharacter(message[tokenStart - 1]);
            int end = index + candidate.Length;
            bool rightBoundary = end == message.Length || !IsTokenCharacter(message[end]);
            if (leftBoundary && rightBoundary)
                return true;
            searchFrom = index + 1;
        }
        return false;
    }

    private static bool IsTokenCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '-';
}
