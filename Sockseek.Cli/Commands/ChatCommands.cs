using System.Globalization;
using System.Text.Json;
using Sockseek.Api;

namespace Sockseek.Cli;

/// <summary>Scriptable remote chat, room, and notification commands.</summary>
internal static class ChatCommands
{
    public static async Task<Program.CliExitCode> RunAsync(
        SockseekApiClient api, ConfiguredCommandInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.CommandArguments;
        return PositionalArgument(args, 0)?.ToLowerInvariant() switch
        {
            "chat" => await RunChatAsync(api, args, cancellationToken).ConfigureAwait(false),
            "room" => await RunRoomAsync(api, args, cancellationToken).ConfigureAwait(false),
            "notifications" => await ListNotificationsAsync(api, args, cancellationToken).ConfigureAwait(false),
            "notification" => await ReadNotificationAsync(api, args, cancellationToken).ConfigureAwait(false),
            _ => ConfiguredCommandDispatcher.Usage("Expected chat, room, notifications, or notification."),
        };
    }

    private static async Task<Program.CliExitCode> RunChatAsync(
        SockseekApiClient api, IReadOnlyList<string> args, CancellationToken ct)
    {
        string action = PositionalArgument(args, 1) ?? "status";
        switch (action.ToLowerInvariant())
        {
            case "status":
                Write(await api.GetChatStatusAsync(ct), Json(args));
                return Program.CliExitCode.Success;
            case "conversations":
            {
                var page = await api.GetConversationsAsync(
                    unread: Flag(args, "--unread") ? true : null,
                    limit: Limit(args),
                    ct: ct);
                if (Json(args)) Write(page, true);
                else foreach (var item in page.Items)
                    Console.WriteLine($"{item.Username}\t{item.UnreadCount}\t{item.LastMessage?.Text ?? ""}");
                return Program.CliExitCode.Success;
            }
            case "messages":
            {
                string username = RequiredArgument(args, 2, "username");
                var conversation = await api.GetConversationByPeerAsync(username, ct)
                    ?? throw new ArgumentException("Conversation not found.");
                var page = await api.GetConversationMessagesAsync(conversation.ConversationId, limit: Limit(args), ct: ct);
                WriteMessages(page.Items, args);
                return Program.CliExitCode.Success;
            }
            case "send":
            {
                string username = RequiredArgument(args, 2, "username");
                string message = RequiredArgument(args, 3, "message");
                Write(await api.SendPrivateMessageAsync(
                    new SendPrivateMessageRequestDto(Guid.NewGuid(), username, message), ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "read":
            {
                string username = RequiredArgument(args, 2, "username");
                var conversation = await api.GetConversationByPeerAsync(username, ct)
                    ?? throw new ArgumentException("Conversation not found.");
                Guid through = ParseGuid(Option(args, "--through"))
                    ?? conversation.LastMessage?.MessageId
                    ?? throw new ArgumentException("Conversation has no message to mark read.");
                Write(await api.MarkConversationReadAsync(conversation.ConversationId, through, ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "delete":
            {
                string username = RequiredArgument(args, 2, "username");
                var conversation = await api.GetConversationByPeerAsync(username, ct)
                    ?? throw new ArgumentException("Conversation not found.");
                await api.DeleteConversationAsync(conversation.ConversationId, ct);
                return Program.CliExitCode.Success;
            }
            default:
                return ConfiguredCommandDispatcher.Usage("Expected chat status, conversations, messages, send, read, or delete.");
        }
    }

    private static async Task<Program.CliExitCode> RunRoomAsync(
        SockseekApiClient api, IReadOnlyList<string> args, CancellationToken ct)
    {
        string action = PositionalArgument(args, 1) ?? "joined";
        switch (action.ToLowerInvariant())
        {
            case "available":
            {
                var page = await api.GetAvailableRoomsAsync(limit: Limit(args), ct: ct);
                if (Json(args)) Write(page, true);
                else foreach (var room in page.Items)
                    Console.WriteLine($"{room.Name}\t{room.UserCount}\t{room.Kind}");
                return Program.CliExitCode.Success;
            }
            case "joined":
            {
                var page = await api.GetRoomsAsync(state: "joined", limit: Limit(args), ct: ct);
                if (Json(args)) Write(page, true);
                else foreach (var room in page.Items)
                    Console.WriteLine($"{room.Name}\t{room.MemberCount}\t{room.Kind}");
                return Program.CliExitCode.Success;
            }
            case "join":
                Write(await api.JoinRoomAsync(
                    RequiredArgument(args, 2, "room name"), !Flag(args, "--no-remember"), ct), Json(args));
                return Program.CliExitCode.Success;
            case "leave":
            {
                var room = await FindRoomAsync(api, RequiredArgument(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                Write(await api.LeaveRoomAsync(room.RoomId, ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "messages":
            {
                var room = await FindRoomAsync(api, RequiredArgument(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                var page = await api.GetRoomMessagesAsync(room.RoomId, limit: Limit(args), ct: ct);
                WriteMessages(page.Items, args);
                return Program.CliExitCode.Success;
            }
            case "send":
            {
                var room = await FindRoomAsync(api, RequiredArgument(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                Write(await api.SendRoomMessageAsync(
                    room.RoomId,
                    new SendChatMessageRequestDto(Guid.NewGuid(), RequiredArgument(args, 3, "message")),
                    ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "members":
            {
                var room = await FindRoomAsync(api, RequiredArgument(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                var page = await api.GetRoomMembersAsync(room.RoomId, limit: Limit(args), ct: ct);
                if (Json(args)) Write(page, true);
                else foreach (var member in page.Items)
                    Console.WriteLine($"{member.Username}\t{member.Presence}");
                return Program.CliExitCode.Success;
            }
            case "member" when string.Equals(PositionalArgument(args, 2), "add", StringComparison.OrdinalIgnoreCase):
            {
                var room = await FindRoomAsync(api, RequiredArgument(args, 3, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                Write(await api.AddPrivateRoomMemberAsync(
                    room.RoomId, RequiredArgument(args, 4, "username"), ct), Json(args));
                return Program.CliExitCode.Success;
            }
            default:
                return ConfiguredCommandDispatcher.Usage("Expected room available, joined, join, leave, messages, send, members, or member add.");
        }
    }

    private static async Task<Program.CliExitCode> ListNotificationsAsync(
        SockseekApiClient api, IReadOnlyList<string> args, CancellationToken ct)
    {
        var page = await api.GetNotificationsAsync(
            unread: Flag(args, "--unread") ? true : null,
            limit: Limit(args),
            ct: ct);
        if (Json(args)) Write(page, true);
        else foreach (var item in page.Items)
            Console.WriteLine($"{item.NotificationId:D}\t{item.Kind}\t{item.Actor}\t{item.Preview}");
        return Program.CliExitCode.Success;
    }

    private static async Task<Program.CliExitCode> ReadNotificationAsync(
        SockseekApiClient api, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!string.Equals(PositionalArgument(args, 1), "read", StringComparison.OrdinalIgnoreCase))
            return ConfiguredCommandDispatcher.Usage("Expected notification read <id|all>.");
        string target = RequiredArgument(args, 2, "notification id or all");
        if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
        {
            var page = await api.GetNotificationsAsync(unread: true, limit: 200, ct: ct);
            long? through = page.Items.Count == 0 ? null : page.Items.Max(item => item.Sequence);
            if (through is null)
                return Program.CliExitCode.Success;
            Write(await api.MarkNotificationsReadAsync(
                new MarkNotificationsReadRequestDto(through, null), ct), Json(args));
            return Program.CliExitCode.Success;
        }
        if (!Guid.TryParse(target, out Guid id))
            return ConfiguredCommandDispatcher.Usage("Notification id must be a UUID or 'all'.");
        Write(await api.MarkNotificationReadAsync(id, ct), Json(args));
        return Program.CliExitCode.Success;
    }

    private static async Task<ChatRoomSummaryDto?> FindRoomAsync(
        SockseekApiClient api, string name, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var page = await api.GetRoomsAsync(cursor: cursor, limit: 200, ct: ct);
            var match = page.Items.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.Ordinal));
            if (match is not null)
                return match;
            cursor = page.NextCursor;
        } while (cursor is not null);
        return null;
    }

    private static void WriteMessages(IReadOnlyList<ChatMessageDto> messages, IReadOnlyList<string> args)
    {
        if (Json(args)) { Write(messages, true); return; }
        foreach (var message in messages)
            Console.WriteLine($"{message.OccurredAtUtc:u}\t{message.Sender}\t{message.Text}");
    }

    private static int Limit(IReadOnlyList<string> args)
    {
        string? value = Option(args, "--limit");
        return value is null ? ServerProtocol.DefaultPageSize
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int limit)
              && limit >= 1
                ? limit
                : throw new ArgumentException("--limit must be a positive integer.");
    }

    private static string? PositionalArgument(IReadOnlyList<string> args, int index)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (args[i] is "--limit" or "--through") i++;
                continue;
            }
            if (index-- == 0) return args[i];
        }
        return null;
    }

    private static string RequiredArgument(IReadOnlyList<string> args, int index, string name)
        => PositionalArgument(args, index) ?? throw new ArgumentException($"Missing {name}.");

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static Guid? ParseGuid(string? value)
        => value is null ? null
            : Guid.TryParse(value, out Guid id) ? id
            : throw new ArgumentException("--through must be a message UUID.");

    private static bool Flag(IReadOnlyList<string> args, string name)
        => args.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static bool Json(IReadOnlyList<string> args) => Flag(args, "--json");

    private static void Write<T>(T value, bool json)
    {
        if (!json) { Console.WriteLine(value); return; }
        JsonSerializerOptions options = SockseekApiJson.CreateSerializerOptions();
        options.WriteIndented = true;
        Console.WriteLine(JsonSerializer.Serialize(value, options));
    }
}
