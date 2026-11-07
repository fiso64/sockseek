using Sockseek.Api;
using Sockseek.Core.Chat;

namespace Sockseek.Server;

internal static class ChatEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/chat/conversations/{conversationId:guid}/snapshot", async (
            Guid conversationId,
            DaemonRuntime runtime,
            EngineStateStore stateStore,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                StateStreamScopeDto scope = StateStreamScopeDto.ChatConversation(conversationId);
                StateStreamPositionDto position = stateStore.GetChatPosition(scope);
                ChatTargetSnapshotDto? target = await chat.GetConversationSnapshotAsync(
                    conversationId, cancellationToken);
                return target is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(new StateSnapshotDto(
                        scope, position, DateTimeOffset.UtcNow, null, [], [], [], [], target));
            });
        })
            .WithTags("Live State")
            .WithSummary("Gets a bounded recoverable conversation snapshot.")
            .Produces<StateSnapshotDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}/snapshot", async (
            Guid roomId,
            DaemonRuntime runtime,
            EngineStateStore stateStore,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                StateStreamScopeDto scope = StateStreamScopeDto.ChatRoom(roomId);
                StateStreamPositionDto position = stateStore.GetChatPosition(scope);
                ChatTargetSnapshotDto? target = await chat.GetRoomSnapshotAsync(roomId, cancellationToken);
                return target is null
                    ? ChatNotFound("The room was not found.")
                    : Results.Ok(new StateSnapshotDto(
                        scope, position, DateTimeOffset.UtcNow, null, [], [], [], [], target));
            });
        })
            .WithTags("Live State")
            .WithSummary("Gets a bounded recoverable room snapshot.")
            .Produces<StateSnapshotDto>()
            .WithChatErrors();

        app.MapGet("/api/chat", (DaemonRuntime runtime) =>
        {
            ChatRuntime? chat = runtime.Chat;
            return chat is null
                ? ChatUnavailable()
                : Results.Ok(chat.GetState());
        })
            .WithTags("Chat")
            .WithSummary("Gets compact chat runtime status.")
            .Produces<ChatRuntimeStateDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations", async (
            bool? unread,
            bool? archived,
            string? cursor,
            int? limit,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                var page = await chat.GetConversationsAsync(
                    unread, archived, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new ConversationPageDto(
                    page.Items.Select(chat.MapConversation).ToArray(), page.NextCursor));
            });
        })
            .WithTags("Chat")
            .WithSummary("Pages private-message conversations.")
            .Produces<ConversationPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/private-messages", async (
            SendPrivateMessageRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendPrivateMessageAsync(
                    request.Username, request.MessageId, request.Text, cancellationToken)));
            });
        })
            .WithTags("Chat")
            .WithSummary("Sends an idempotent private message.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/by-peer", async (
            string username,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                var conversation = await chat.GetConversationByPeerAsync(username, cancellationToken);
                return conversation is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(chat.MapConversation(conversation));
            });
        })
            .WithTags("Chat")
            .WithSummary("Gets an existing conversation by exact peer username without creating one.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/{conversationId:guid}", async (
            Guid conversationId,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                var conversation = await chat.GetConversationAsync(conversationId, cancellationToken);
                return conversation is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(chat.MapConversation(conversation));
            });
        })
            .WithTags("Chat")
            .WithSummary("Gets a private-message conversation.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId,
            string? cursor,
            int? limit,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                if (await chat.GetConversationAsync(conversationId, cancellationToken) is null)
                    return ChatNotFound("The conversation was not found.");
                var page = await chat.GetMessagesAsync(
                    conversationId, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new ChatMessagePageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            });
        })
            .WithTags("Chat")
            .WithSummary("Pages messages in a private conversation.")
            .Produces<ChatMessagePageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId,
            SendChatMessageRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendConversationMessageAsync(
                    conversationId, request.MessageId, request.Text, cancellationToken)));
            });
        })
            .WithTags("Chat")
            .WithSummary("Sends an idempotent message to a conversation.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/read", async (
            Guid conversationId,
            MarkChatReadRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                await chat.MarkConversationReadAsync(
                    conversationId, request.ThroughMessageId, cancellationToken);
                return Results.Ok(chat.MapConversation(
                    await chat.GetConversationAsync(conversationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The conversation was not found.")));
            });
        })
            .WithTags("Chat")
            .WithSummary("Advances a conversation's local read watermark.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/archive", async (
            Guid conversationId,
            ArchiveConversationRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                await chat.ArchiveConversationAsync(conversationId, request.Archived, cancellationToken);
                return Results.Ok(chat.MapConversation(
                    await chat.GetConversationAsync(conversationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The conversation was not found.")));
            });
        })
            .WithTags("Chat")
            .WithSummary("Archives or reactivates a conversation.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/conversations/{conversationId:guid}/history", async (
            Guid conversationId,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                await chat.DeleteConversationHistoryAsync(conversationId, cancellationToken);
                return Results.NoContent();
            });
        })
            .WithTags("Chat")
            .WithSummary("Permanently deletes conversation history.")
            .Produces(StatusCodes.Status204NoContent)
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/available", async (
            ServerChatRoomKind? kind,
            string? cursor,
            int? limit,
            bool? refresh,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(await chat.Rooms.GetAvailableRoomsAsync(
                    kind, cursor, limit ?? ServerProtocol.DefaultPageSize, refresh == true, cancellationToken));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Pages rooms visible to the current Soulseek account.")
            .Produces<AvailableRoomPageDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms", async (
            string? state,
            string? cursor,
            int? limit,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(await chat.Rooms.GetRoomSummariesAsync(
                    state, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Pages known room subscriptions and runtime state.")
            .Produces<ChatRoomPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms", async (
            JoinRoomRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(await chat.Rooms.JoinRoomAsync(
                    request.RoomName, request.Remember, cancellationToken));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Joins a room and optionally remembers it across restarts.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}", async (
            Guid roomId,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                var room = await chat.Rooms.GetRoomDetailAsync(roomId, cancellationToken);
                return room is null ? ChatNotFound("The room was not found.") : Results.Ok(room);
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Gets room subscription, join, and private-room metadata.")
            .Produces<ChatRoomDetailDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/rooms/{roomId:guid}", async (
            Guid roomId,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(await chat.Rooms.LeaveRoomAsync(roomId, cancellationToken));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Leaves a room and removes its runtime subscription.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}/messages", async (
            Guid roomId,
            string? cursor,
            int? limit,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                if (await chat.Rooms.GetRoomSummaryAsync(roomId, cancellationToken) is null)
                    return ChatNotFound("The room was not found.");
                var page = await chat.GetMessagesAsync(
                    roomId, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new ChatMessagePageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Pages messages in a joined or retained room.")
            .Produces<ChatMessagePageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/messages", async (
            Guid roomId,
            SendChatMessageRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendRoomMessageAsync(
                    roomId, request.MessageId, request.Text, cancellationToken)));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Sends an idempotent room message.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/read", async (
            Guid roomId,
            MarkChatReadRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                await chat.MarkRoomReadAsync(roomId, request.ThroughMessageId, cancellationToken);
                return Results.Ok(await chat.Rooms.GetRoomSummaryAsync(roomId, cancellationToken)
                                  ?? throw new KeyNotFoundException("The room was not found."));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Advances a room's local read watermark.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}/members", async (
            Guid roomId,
            string? cursor,
            int? limit,
            long? revision,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                return Results.Ok(await chat.Rooms.GetRoomMembersAsync(
                    roomId, cursor, limit ?? ServerProtocol.DefaultPageSize, revision, cancellationToken));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Pages the current ephemeral room roster.")
            .Produces<RoomMemberPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/members", async (
            Guid roomId,
            AddRoomMemberRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                await chat.Rooms.AddPrivateRoomMemberAsync(roomId, request.Username, cancellationToken);
                return Results.Ok(await chat.Rooms.GetRoomDetailAsync(roomId, cancellationToken));
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Adds a member to a joined private room.")
            .Produces<ChatRoomDetailDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/rooms/{roomId:guid}/history", async (
            Guid roomId,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                await chat.DeleteRoomHistoryAsync(roomId, cancellationToken);
                return Results.NoContent();
            });
        })
            .WithTags("Chat Rooms")
            .WithSummary("Permanently deletes retained room history.")
            .Produces(StatusCodes.Status204NoContent)
            .WithChatErrors();

        app.MapGet("/api/notifications", async (
            bool? unread,
            ServerUserNotificationKind? kind,
            string? cursor,
            int? limit,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                var page = await chat.GetNotificationsAsync(
                    unread, kind, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new NotificationPageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            });
        })
            .WithTags("Notifications")
            .WithSummary("Pages durable chat notifications.")
            .Produces<NotificationPageDto>()
            .WithChatErrors();

        app.MapGet("/api/notifications/{notificationId:guid}", async (
            Guid notificationId,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                var notification = await chat.GetNotificationAsync(notificationId, cancellationToken);
                return notification is null
                    ? ChatNotFound("The notification was not found.")
                    : Results.Ok(ChatDtoMapper.ToDto(notification));
            });
        })
            .WithTags("Notifications")
            .WithSummary("Gets a durable chat notification.")
            .Produces<UserNotificationDto>()
            .WithChatErrors();

        app.MapPost("/api/notifications/{notificationId:guid}/read", async (
            Guid notificationId,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                if (await chat.GetNotificationAsync(notificationId, cancellationToken) is null)
                    return ChatNotFound("The notification was not found.");
                await chat.MarkNotificationsReadAsync(null, [notificationId], cancellationToken);
                return Results.Ok(ChatDtoMapper.ToDto(
                    await chat.GetNotificationAsync(notificationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The notification was not found.")));
            });
        })
            .WithTags("Notifications")
            .WithSummary("Marks one notification read.")
            .Produces<UserNotificationDto>()
            .WithChatErrors();

        app.MapPost("/api/notifications/read", async (
            MarkNotificationsReadRequestDto request,
            DaemonRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(runtime, async chat =>
            {
                await chat.MarkNotificationsReadAsync(
                    request.ThroughSequence, request.Ids, cancellationToken);
                return Results.Ok(chat.GetNotificationSummary());
            });
        })
            .WithTags("Notifications")
            .WithSummary("Marks a bounded notification set or sequence range read.")
            .Produces<NotificationSummaryDto>()
            .WithChatErrors();
    }

    private static IResult ChatUnavailable()
        => Results.Json(
            new ApiErrorDto(
                "Chat is unavailable because daemon persistence is disabled or not started.",
                "Unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> WithChatAsync(
        DaemonRuntime runtime,
        Func<ChatRuntime, Task<IResult>> action)
    {
        if (runtime.Chat is not { } chat)
            return ChatUnavailable();
        try
        {
            return await action(chat).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return ChatFailure(exception);
        }
    }

    private static RouteHandlerBuilder WithChatErrors(this RouteHandlerBuilder builder)
        => builder
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status429TooManyRequests)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

    private static IResult ChatNotFound(string message)
        => Results.NotFound(new ApiErrorDto(message, "NotFound"));

    private static IResult ChatFailure(Exception exception)
    {
        return exception switch
        {
            ArgumentException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "InvalidRequest")),
            KeyNotFoundException => ChatNotFound(exception.Message),
            UnauthorizedAccessException => Results.Json(
                new ApiErrorDto(exception.Message, "Denied"),
                statusCode: StatusCodes.Status403Forbidden),
            ChatCapacityException => Results.Json(
                new ApiErrorDto(exception.Message, "Capacity"),
                statusCode: StatusCodes.Status429TooManyRequests),
            ChatStateConflictException => Results.Conflict(
                new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException when exception.Message.Contains(
                "MessageId", StringComparison.Ordinal) => Results.Conflict(
                    new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException when exception.Message.Contains(
                "revision changed", StringComparison.OrdinalIgnoreCase) => Results.Conflict(
                    new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException => Results.Json(
                new ApiErrorDto(exception.Message, "Unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(
                new ApiErrorDto("The chat operation failed.", "Unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

}
