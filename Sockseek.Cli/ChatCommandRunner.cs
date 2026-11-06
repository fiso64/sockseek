using System.Globalization;
using System.Text.Json;
using Sockseek.Api;

namespace Sockseek.Cli;

/// <summary>Scriptable remote chat, room, and notification commands.</summary>
internal static class ChatCommandRunner
{
    public static async Task<Program.CliExitCode> RunAsync(
        IReadOnlyList<string> args,
        string? remote,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            PrintHelp();
            return Program.CliExitCode.Success;
        }
        if (string.IsNullOrWhiteSpace(remote))
            return Usage("This command requires a configured remote URL (remote = <url> or --remote <url>).");
        try
        {
            using HttpClient http = SockseekApiClient.CreateHttpClient(remote);
            var api = new SockseekApiClient(http);
            return Word(args, 0)?.ToLowerInvariant() switch
            {
                "chat" => await RunChatAsync(api, args, cancellationToken).ConfigureAwait(false),
                "room" => await RunRoomAsync(api, args, cancellationToken).ConfigureAwait(false),
                "notifications" => await ListNotificationsAsync(api, args, cancellationToken).ConfigureAwait(false),
                "notification" => await ReadNotificationAsync(api, args, cancellationToken).ConfigureAwait(false),
                _ => Usage("Expected chat, room, notifications, or notification."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Program.CliExitCode.Cancelled;
        }
        catch (SockseekApiRequestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Program.CliExitCode.WorkFailed;
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Daemon chat command failed: {ex.Message}");
            return Program.CliExitCode.WorkFailed;
        }
    }

    private static async Task<Program.CliExitCode> RunChatAsync(
        SockseekApiClient api, IReadOnlyList<string> args, CancellationToken ct)
    {
        string action = Word(args, 1) ?? "status";
        switch (action.ToLowerInvariant())
        {
            case "status":
                Write(await api.GetChatStatusAsync(ct), Json(args));
                return Program.CliExitCode.Success;
            case "conversations":
            {
                var page = await api.GetConversationsAsync(
                    unread: Flag(args, "--unread") ? true : null,
                    archived: null,
                    limit: Limit(args),
                    ct: ct);
                if (Json(args)) Write(page, true);
                else foreach (var item in page.Items)
                    Console.WriteLine($"{item.Username}\t{item.UnreadCount}\t{item.LastMessage?.Text ?? ""}");
                return Program.CliExitCode.Success;
            }
            case "messages":
            {
                string username = RequiredWord(args, 2, "username");
                var conversation = await FindConversationAsync(api, username, ct)
                    ?? throw new ArgumentException("Conversation not found.");
                var page = await api.GetConversationMessagesAsync(conversation.ConversationId, limit: Limit(args), ct: ct);
                WriteMessages(page.Items, args);
                return Program.CliExitCode.Success;
            }
            case "send":
            {
                string username = RequiredWord(args, 2, "username");
                string message = RequiredWord(args, 3, "message");
                Write(await api.SendPrivateMessageAsync(
                    new SendPrivateMessageRequestDto(Guid.NewGuid(), username, message), ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "read":
            {
                string username = RequiredWord(args, 2, "username");
                var conversation = await FindConversationAsync(api, username, ct)
                    ?? throw new ArgumentException("Conversation not found.");
                Guid through = ParseGuid(Option(args, "--through"))
                    ?? conversation.LastMessage?.MessageId
                    ?? throw new ArgumentException("Conversation has no message to mark read.");
                Write(await api.MarkConversationReadAsync(conversation.ConversationId, through, ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "archive":
            {
                string username = RequiredWord(args, 2, "username");
                var conversation = await FindConversationAsync(api, username, ct)
                    ?? throw new ArgumentException("Conversation not found.");
                Write(await api.ArchiveConversationAsync(conversation.ConversationId, true, ct), Json(args));
                return Program.CliExitCode.Success;
            }
            default:
                return Usage("Expected chat status, conversations, messages, send, read, or archive.");
        }
    }

    private static async Task<Program.CliExitCode> RunRoomAsync(
        SockseekApiClient api, IReadOnlyList<string> args, CancellationToken ct)
    {
        string action = Word(args, 1) ?? "joined";
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
                    RequiredWord(args, 2, "room name"), !Flag(args, "--no-remember"), ct), Json(args));
                return Program.CliExitCode.Success;
            case "leave":
            {
                var room = await FindRoomAsync(api, RequiredWord(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                Write(await api.LeaveRoomAsync(room.RoomId, ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "messages":
            {
                var room = await FindRoomAsync(api, RequiredWord(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                var page = await api.GetRoomMessagesAsync(room.RoomId, limit: Limit(args), ct: ct);
                WriteMessages(page.Items, args);
                return Program.CliExitCode.Success;
            }
            case "send":
            {
                var room = await FindRoomAsync(api, RequiredWord(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                Write(await api.SendRoomMessageAsync(
                    room.RoomId,
                    new SendChatMessageRequestDto(Guid.NewGuid(), RequiredWord(args, 3, "message")),
                    ct), Json(args));
                return Program.CliExitCode.Success;
            }
            case "members":
            {
                var room = await FindRoomAsync(api, RequiredWord(args, 2, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                var page = await api.GetRoomMembersAsync(room.RoomId, limit: Limit(args), ct: ct);
                if (Json(args)) Write(page, true);
                else foreach (var member in page.Items)
                    Console.WriteLine($"{member.Username}\t{member.Presence}");
                return Program.CliExitCode.Success;
            }
            case "member" when string.Equals(Word(args, 2), "add", StringComparison.OrdinalIgnoreCase):
            {
                var room = await FindRoomAsync(api, RequiredWord(args, 3, "room name"), ct)
                    ?? throw new ArgumentException("Room not found.");
                Write(await api.AddPrivateRoomMemberAsync(
                    room.RoomId, RequiredWord(args, 4, "username"), ct), Json(args));
                return Program.CliExitCode.Success;
            }
            default:
                return Usage("Expected room available, joined, join, leave, messages, send, members, or member add.");
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
        if (!string.Equals(Word(args, 1), "read", StringComparison.OrdinalIgnoreCase))
            return Usage("Expected notification read <id|all>.");
        string target = RequiredWord(args, 2, "notification id or all");
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
            return Usage("Notification id must be a UUID or 'all'.");
        Write(await api.MarkNotificationReadAsync(id, ct), Json(args));
        return Program.CliExitCode.Success;
    }

    private static async Task<ConversationSummaryDto?> FindConversationAsync(
        SockseekApiClient api, string username, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var page = await api.GetConversationsAsync(cursor: cursor, limit: 200, ct: ct);
            var match = page.Items.FirstOrDefault(item =>
                string.Equals(item.Username, username, StringComparison.Ordinal));
            if (match is not null)
                return match;
            cursor = page.NextCursor;
        } while (cursor is not null);
        return null;
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

    private static string? Word(IReadOnlyList<string> args, int index)
    {
        var words = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (args[i] is "--limit" or "--through") i++;
                continue;
            }
            words.Add(args[i]);
        }
        return index < words.Count ? words[index] : null;
    }

    private static string RequiredWord(IReadOnlyList<string> args, int index, string name)
        => Word(args, index) ?? throw new ArgumentException($"Missing {name}.");

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

    private static void Write(object value, bool json)
    {
        if (!json) { Console.WriteLine(value); return; }
        JsonSerializerOptions options = SockseekApiJson.CreateSerializerOptions();
        options.WriteIndented = true;
        Console.WriteLine(JsonSerializer.Serialize(value, value.GetType(), options));
    }

    private static Program.CliExitCode Usage(string message)
    {
        Console.Error.WriteLine(message.StartsWith("Input error:", StringComparison.Ordinal)
            ? message : $"Input error: {message}");
        return Program.CliExitCode.UsageError;
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        Remote chat commands:
          sockseek chat status [--remote <url>] [--json]
          sockseek chat conversations [--unread] [--remote <url>] [--json]
          sockseek chat messages <username> [--limit N] [--remote <url>] [--json]
          sockseek chat send <username> <message> [--remote <url>] [--json]
          sockseek chat read <username> [--through <message-id>] [--remote <url>]
          sockseek chat archive <username> [--remote <url>]
          sockseek room available|joined [--remote <url>] [--json]
          sockseek room join <name> [--no-remember] [--remote <url>]
          sockseek room leave|messages|members <name> [--remote <url>] [--json]
          sockseek room send <name> <message> [--remote <url>]
          sockseek room member add <name> <username> [--remote <url>]
          sockseek notifications [--unread] [--remote <url>] [--json]
          sockseek notification read <id|all> [--remote <url>] [--json]

        The remote URL can be set as `remote = <url>` in config; --remote overrides it.
        """);
}
