using System.Net;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Sockseek.Api;
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
        await using var app = global::Tests.TestServerHost.Build([], new ServerOptions
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
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var api = new SockseekApiClient(http);
            Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync("/api/users/Alice/restrictions")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await http.PutAsync("/api/users/Alice/restrictions", new StringContent("{}"))).StatusCode);
            await WaitUntilAsync(async () =>
            {
                try { return (await api.GetChatStatusAsync()).State == DaemonFeatureState.Ready; }
                catch (SockseekApiRequestException) { return false; }
            });

            Guid outgoingId = Guid.NewGuid();
            ChatMessageDto outgoing = await api.SendPrivateMessageAsync(
                new SendPrivateMessageRequestDto(outgoingId, "Alice", "outbound"));
            Assert.AreEqual(ChatMessageState.Sent, outgoing.State);
            foreach (string newline in new[] { "\n", "\r", "\r\n" })
            {
                var invalid = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.SendPrivateMessageAsync(new(Guid.NewGuid(), "Alice", $"first{newline}second")));
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                StringAssert.Contains(invalid.Message, "cannot contain line breaks");
            }
            var invalidConversation = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                api.SendConversationMessageAsync(outgoing.TargetId, new(Guid.NewGuid(), "first\nsecond")));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidConversation.StatusCode);
            Assert.AreEqual(1, fake.PrivateSendCount, "Invalid text must not reach the Soulseek client.");
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

            var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            var policy = supervisor.Runtime.SoulseekRuntime!.Restrictions;
            Assert.AreSame(policy, supervisor.Runtime.Sharing!.Restrictions);
            Assert.AreSame(policy, supervisor.Runtime.Chat!.Restrictions);
            Assert.IsTrue(policy.IsUploadUsernameBlocked("Configured"));
            Assert.IsTrue(policy.IsPrivateMessageBlocked("Configured"));
            var restrictionChanges = new ConcurrentQueue<ChatTargetDeltaDto>();
            supervisor.Runtime.Chat.TargetChanged += restrictionChanges.Enqueue;
            var changedSettings = new EngineSettings
            {
                PeerRestrictions = new()
                {
                    PrivateMessages = new() { BlockedUsernames = ["Alice"] },
                    UploadAccess = new() { BlockedUsernames = ["Alice"] },
                },
            };
            await supervisor.Runtime.ReloadConfigurationAsync(changedSettings, new());
            Assert.IsTrue(restrictionChanges.Any(change => change.Conversation?.ConversationId == conversation.ConversationId
                && change.Conversation.PrivateMessagesBlocked));
            Assert.IsTrue((await api.GetConversationAsync(conversation.ConversationId))!.PrivateMessagesBlocked);
            var profile = await api.GetUserProfileAsync("Alice");
            Assert.IsTrue(profile.PrivateMessagesBlocked);
            Assert.IsTrue(profile.UploadAccessBlocked);
            Assert.IsFalse(policy.IsPrivateMessageBlocked("alice"));
            restrictionChanges.Clear();
            await supervisor.Runtime.ReloadConfigurationAsync(new EngineSettings(), new());
            Assert.IsTrue(restrictionChanges.Any(change => change.Conversation?.ConversationId == conversation.ConversationId
                && !change.Conversation.PrivateMessagesBlocked));
            Assert.IsFalse((await api.GetConversationAsync(conversation.ConversationId))!.PrivateMessagesBlocked);
            Assert.IsFalse(policy.IsUploadUsernameBlocked("Alice"));

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

            foreach (string invalidRoom in new[] { "caf\u00e9", "two  spaces", new string('a', 25) })
            {
                var invalid = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.JoinRoomAsync(invalidRoom));
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                StringAssert.Contains(invalid.Message, "Soulseek room names");
            }
            ChatRoomSummaryDto room = await api.JoinRoomAsync("indie");
            Assert.AreEqual(ChatRoomJoinPhase.Joined, room.Phase);
            Assert.IsTrue((await api.GetRoomMembersAsync(room.RoomId)).Complete);
            foreach (string newline in new[] { "\n", "\r", "\r\n" })
            {
                var invalid = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.SendRoomMessageAsync(room.RoomId, new(Guid.NewGuid(), $"first{newline}second")));
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                StringAssert.Contains(invalid.Message, "cannot contain line breaks");
            }
            Assert.AreEqual(0, fake.RoomSendCount);
            ChatMessageDto roomMessage = await api.SendRoomMessageAsync(
                room.RoomId,
                new SendChatMessageRequestDto(Guid.NewGuid(), "hello room"));
            Assert.AreEqual(ChatMessageState.Sent, roomMessage.State);

            StateSnapshotDto snapshot = await api.GetRoomSnapshotAsync(room.RoomId);
            Assert.AreEqual(StateStreamScopeDto.ChatRoom(room.RoomId), snapshot.Scope);
            Assert.AreEqual(room.RoomId, snapshot.ChatTarget?.TargetId);

            SockseekApiRequestException invalidCursor =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.GetConversationMessagesAsync(
                        conversation.ConversationId, new string('a', 257)));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
            Assert.AreEqual("InvalidRequest", invalidCursor.Code);

            await api.DeleteConversationAsync(conversation.ConversationId);
            Assert.IsNull(await api.GetConversationAsync(conversation.ConversationId));
            Assert.IsNull(await api.GetConversationByPeerAsync("Alice"));
            Assert.IsEmpty((await api.GetNotificationsAsync()).Items);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
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
