using System.Globalization;
using System.Text;
using Sockseek.Api;
using Sockseek.Core.Chat;

namespace Sockseek.Server;

internal static class ChatDtoMapper
{
    public static ChatMessageDto ToDto(ChatMessageRecord value)
        => new(
            value.MessageId,
            value.Sequence,
            value.TargetKind.ToServer(),
            value.TargetId,
            value.DisplaySender,
            value.Direction.ToServer(),
            value.Body,
            value.OccurredAtUtc,
            value.RecordedAtUtc,
            value.State.ToServer(),
            value.FailureReason);

    public static ConversationSummaryDto ToDto(
        ConversationRecord value,
        bool privateMessagesBlocked)
        => new(
            value.ConversationId,
            value.DisplayUsername,
            privateMessagesBlocked,
            value.ArchivedAtUtc is not null,
            value.UnreadCount,
            value.LastReadSequence,
            value.Revision,
            value.LastMessage is null ? null : ToDto(value.LastMessage));

    public static UserNotificationDto ToDto(UserNotificationRecord value)
        => new(
            value.NotificationId,
            value.Sequence,
            value.Kind.ToServer(),
            value.CreatedAtUtc,
            value.ReadAtUtc,
            value.SourceMessage.DisplaySender,
            value.SourceMessage.TargetKind.ToServer(),
            value.SourceMessage.TargetId,
            value.SourceMessage.DisplayTarget,
            value.SourceMessageId,
            Preview(value.SourceMessage.Body),
            value.SourceMessage.TargetKind == ChatTargetKind.Direct
                ? $"/api/chat/conversations/{value.SourceMessage.TargetId:D}"
                : $"/api/chat/rooms/{value.SourceMessage.TargetId:D}");

    private static string Preview(string body)
    {
        var result = new StringBuilder(ChatLimits.NotificationPreviewCharacters);
        bool pendingSpace = false;
        int count = 0;
        foreach (Rune rune in body.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune)
                || category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                if (count == ChatLimits.NotificationPreviewCharacters)
                    break;
                result.Append(' ');
                pendingSpace = false;
                count++;
            }
            if (count == ChatLimits.NotificationPreviewCharacters)
                break;
            result.Append(rune.ToString());
            count++;
        }
        return result.ToString();
    }
}
