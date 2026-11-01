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
        string normalized = roomName.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0)
            throw new ArgumentException("Input error: Room name cannot be empty.", nameof(roomName));
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Input error: Room name cannot contain control characters.", nameof(roomName));
        EnsureWellFormedUtf16(normalized, nameof(roomName));
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
