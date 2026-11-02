using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Chat;
using Sockseek.Core.Settings;
using Sockseek.Server;
using Soulseek;

namespace Tests.Server;

[TestClass]
public sealed class ChatApiTests
{
    [TestMethod]
    public async Task TypedClientExercisesDurableChatRoomAndNotificationResources()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "sockseek-chat-api-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dataDirectory);
        var fake = SoulseekClientProxy.Create();
        fake.Rooms = new RoomList(
            [new RoomInfo("indie", 1)], [], [], []);
        fake.JoinedRoom = new RoomData(
            "indie",
            [new UserData("Alice", UserPresence.Online, 0, 0, 0, 0, "CH")]);
        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                Username = "local",
                Password = "password",
                ListenPort = null,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
                PeerRestrictions = new PeerRestrictionSettings
                {
                    UploadAccess = new UploadAccessSettings
                    {
                        BlockedUsernames = ["Configured"],
                    },
                    PrivateMessages = new PrivateMessageAccessSettings
                    {
                        BlockedUsernames = ["Configured"],
                    },
                },
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
            },
            ClientFactory = _ => fake.Client,
        }, url);

        try
        {
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            var api = new SockseekApiClient(http);
            await WaitUntilAsync(async () =>
            {
                try { return (await api.GetChatStatusAsync()).State == DaemonFeatureState.Ready; }
                catch (SockseekApiRequestException) { return false; }
            });

            Guid outgoingId = Guid.NewGuid();
            ChatMessageDto outgoing = await api.SendPrivateMessageAsync(
                new SendPrivateMessageRequestDto(outgoingId, "Alice", "outbound"));
            Assert.AreEqual(ServerChatMessageState.Sent, outgoing.State);
            ConversationSummaryDto? byPeer = await api.GetConversationByPeerAsync("Alice");
            Assert.IsNotNull(byPeer);
            Assert.AreEqual(outgoing.TargetId, byPeer.ConversationId);
            Assert.IsNull(await api.GetConversationByPeerAsync("alice"));
            Assert.IsNull(await api.GetConversationByPeerAsync(" Alice "));
            SockseekApiRequestException conflict =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.SendPrivateMessageAsync(
                        new SendPrivateMessageRequestDto(outgoingId, "Alice", "different")));
            Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.AreEqual("Conflict", conflict.Code);

            fake.RaisePrivateMessage(new PrivateMessageReceivedEventArgs(
                7, DateTime.UtcNow, "Alice", "incoming\nmessage", replayed: false));
            await WaitUntilAsync(() => Task.FromResult(fake.AcknowledgeCount == 1));

            ConversationSummaryDto conversation = (await api.GetConversationsAsync())
                .Items.Single(item => item.Username == "Alice");
            Assert.IsFalse(conversation.PrivateMessagesBlocked);

            UserRestrictionsDto configured =
                (await api.GetUserRestrictionsAsync("Configured"))!;
            Assert.IsTrue(configured.UploadAccess.IsBlocked);
            Assert.IsTrue(configured.PrivateMessages.IsBlocked);
            UserRestrictionsDto allowedUpload = await api.SetUserRestrictionAsync(
                "Configured",
                new SetUserRestrictionOverrideRequestDto(
                    UserRestrictionKind.UploadAccess,
                    UserRestrictionOverrideState.Allowed));
            Assert.IsFalse(allowedUpload.UploadAccess.IsBlocked);
            Assert.IsTrue(allowedUpload.PrivateMessages.IsBlocked);
            Assert.IsTrue((await api.SetUserRestrictionAsync(
                "Configured",
                new SetUserRestrictionOverrideRequestDto(
                    UserRestrictionKind.UploadAccess,
                    null))).UploadAccess.IsBlocked);

            UserRestrictionsDto blocked = await api.SetUserRestrictionAsync(
                "Alice",
                new SetUserRestrictionOverrideRequestDto(
                    UserRestrictionKind.PrivateMessages,
                    UserRestrictionOverrideState.Blocked));
            Assert.IsTrue(blocked.PrivateMessages.IsBlocked);
            Assert.IsFalse(blocked.UploadAccess.IsBlocked);
            Assert.IsFalse((await api.GetUserRestrictionsAsync("alice"))!
                .PrivateMessages.IsBlocked,
                "Soulseek username restrictions remain exact ordinal.");
            Assert.IsTrue((await api.GetConversationAsync(
                conversation.ConversationId))!.PrivateMessagesBlocked);
            UserProfileDto messageBlockedProfile = await api.GetUserProfileAsync("Alice");
            Assert.IsTrue(messageBlockedProfile.PrivateMessagesBlocked);
            Assert.IsFalse(messageBlockedProfile.UploadAccessBlocked);
            Assert.AreEqual(ResourceSectionState.Available, messageBlockedProfile.Status.State,
                "Restrictions annotate profiles; they do not hide outbound profile data.");

            UserRestrictionsDto uploadBlocked = await api.SetUserRestrictionAsync(
                "Alice",
                new SetUserRestrictionOverrideRequestDto(
                    UserRestrictionKind.UploadAccess,
                    UserRestrictionOverrideState.Blocked));
            Assert.IsTrue(uploadBlocked.UploadAccess.IsBlocked);
            Assert.IsTrue(uploadBlocked.PrivateMessages.IsBlocked);
            UserProfileDto fullyBlockedProfile = await api.GetUserProfileAsync("Alice");
            Assert.IsTrue(fullyBlockedProfile.UploadAccessBlocked);
            Assert.IsTrue(fullyBlockedProfile.PrivateMessagesBlocked);
            Assert.AreEqual(ResourceSectionState.Available, fullyBlockedProfile.Status.State);

            await api.SetUserRestrictionAsync(
                "Alice",
                new SetUserRestrictionOverrideRequestDto(
                    UserRestrictionKind.PrivateMessages,
                    UserRestrictionOverrideState.Allowed));
            Assert.IsFalse((await api.GetConversationAsync(
                conversation.ConversationId))!.PrivateMessagesBlocked);
            Assert.IsTrue((await api.GetUserRestrictionsAsync("Alice"))!
                .UploadAccess.IsBlocked,
                "Changing private-message policy must not change upload access.");

            var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            var peerRestrictions = app.Services.GetRequiredService<
                Sockseek.Server.PeerRestrictions.PeerRestrictionCoordinator>();
            Assert.AreSame(peerRestrictions.Policy, supervisor.Runtime.SoulseekRuntime!.Restrictions);
            Assert.AreSame(peerRestrictions.Policy, supervisor.Runtime.Sharing!.Restrictions);
            Assert.AreSame(peerRestrictions.Policy, supervisor.Runtime.Chat!.Restrictions);

            ChatMessagePageDto messages = await api.GetConversationMessagesAsync(
                conversation.ConversationId);
            Assert.AreEqual(2, messages.Items.Count);
            Assert.AreEqual(1, conversation.UnreadCount);

            NotificationPageDto notifications = await api.GetNotificationsAsync(unread: true);
            UserNotificationDto notification = notifications.Items.Single();
            Assert.AreEqual("incoming message", notification.Preview);
            Assert.AreEqual(conversation.ConversationId, notification.TargetId);

            await api.MarkConversationReadAsync(
                conversation.ConversationId, messages.Items[^1].MessageId);
            Assert.AreEqual(0, (await api.GetNotificationsAsync(unread: true)).Items.Count);
            await api.ArchiveConversationAsync(conversation.ConversationId);
            Assert.IsTrue((await api.GetConversationAsync(conversation.ConversationId))!.Archived);

            ChatRoomSummaryDto room = await api.JoinRoomAsync("indie");
            Assert.AreEqual(ServerChatRoomJoinPhase.Joined, room.Phase);
            Assert.IsTrue((await api.GetRoomMembersAsync(room.RoomId)).Complete);
            ChatMessageDto roomMessage = await api.SendRoomMessageAsync(
                room.RoomId,
                new SendChatMessageRequestDto(Guid.NewGuid(), "hello room"));
            Assert.AreEqual(ServerChatMessageState.Sent, roomMessage.State);

            StateSnapshotDto snapshot = await api.GetRoomSnapshotAsync(room.RoomId);
            Assert.AreEqual(StateStreamScopeDto.ChatRoom(room.RoomId), snapshot.Scope);
            Assert.AreEqual(room.RoomId, snapshot.ChatTarget?.TargetId);

            SockseekApiRequestException invalidCursor =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.GetConversationMessagesAsync(
                        conversation.ConversationId, new string('a', 257)));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
            Assert.AreEqual("InvalidRequest", invalidCursor.Code);
        }
        finally
        {
            await app.StopAsync();
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(dataDirectory))
                System.IO.Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.IsTrue(await condition(), "The daemon did not reach the expected chat state.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
