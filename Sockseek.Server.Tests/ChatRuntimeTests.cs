using System.Reflection;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Chat;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Chat;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;
using Sockseek.Server;
using Soulseek;

namespace Tests.Server;

[TestClass]
public sealed class ChatRuntimeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PrivateMessageCommitsBeforeAckAndReplayDoesNotDuplicate()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        chat.NotificationCommitted += _ => throw new InvalidOperationException("observer failure");
        DateTime timestamp = DateTime.UtcNow;
        var firstAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Acknowledged = async id =>
        {
            ChatStoreSummary summary = await database.Host.Chat!.GetSummaryAsync("local");
            Assert.AreEqual(1, summary.UnreadPrivateMessages, "ACK ran before the durable row was visible.");
            firstAck.TrySetResult();
        };

        // Another local service can initiate login before chat startup finishes.
        // The first queued message still belongs to this runtime and is acked
        // only after persistence, exactly like later arrivals/replays.
        await session.EnsureStartedAsync();
        fake.RaisePrivateMessage(new PrivateMessageReceivedEventArgs(
            42, timestamp, "Alice", "hello", replayed: false));
        await chat.StartAsync(CancellationToken.None);
        await firstAck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fake.RaisePrivateMessage(new PrivateMessageReceivedEventArgs(
            42, timestamp, "Alice", "hello", replayed: true));
        await WaitUntilAsync(() => fake.AcknowledgeCount == 2);

        ChatStoreSummary final = await database.Host.Chat!.GetSummaryAsync("local");
        Assert.AreEqual(1, final.UnreadPrivateMessages);
        Assert.AreEqual(1, final.UnreadNotifications);
    }

    [TestMethod]
    public async Task PrivateMessageRestrictionDiscardsOnlyIncomingDirectMessages()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        var settings = Settings();
        settings.PeerRestrictions.PrivateMessages.BlockedUsernames.Add("Alice");
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);

        fake.RaisePrivateMessage(new PrivateMessageReceivedEventArgs(
            1, DateTime.UtcNow, "Alice", "blocked", replayed: false));
        fake.RaisePrivateMessage(new PrivateMessageReceivedEventArgs(
            2, DateTime.UtcNow, "Bob", "   ", replayed: false));
        await WaitUntilAsync(() => fake.AcknowledgeCount == 2);

        ChatMessageRecord outgoing = await chat.SendPrivateMessageAsync(
            "Alice", Guid.NewGuid(), "outgoing remains allowed", CancellationToken.None);

        ChatStoreSummary summary = await database.Host.Chat!.GetSummaryAsync("local");
        Assert.AreEqual(ChatMessageState.Sent, outgoing.State);
        Assert.AreEqual(0, summary.UnreadPrivateMessages,
            "A private-message restriction applies only to incoming direct messages.");
        Assert.AreEqual(0, summary.UnreadNotifications);
    }

    [TestMethod]
    public async Task RepeatedOutgoingMessageIdSendsOnlyOnce()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        Guid messageId = Guid.NewGuid();

        ChatMessageRecord first = await chat.SendPrivateMessageAsync(
            "Alice", messageId, "hello", CancellationToken.None);
        ChatMessageRecord retry = await chat.SendPrivateMessageAsync(
            "Alice", messageId, "hello", CancellationToken.None);

        Assert.AreEqual(ChatMessageState.Sent, first.State);
        Assert.AreEqual(first.MessageId, retry.MessageId);
        Assert.AreEqual(1, fake.PrivateSendCount);
    }

    [TestMethod]
    public async Task FailedOutgoingSendIsDurableAndNeverRetriedImplicitly()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        fake.PrivateSend = _ => Task.FromException(new IOException("network failed"));
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        Guid messageId = Guid.NewGuid();

        ChatMessageRecord failed = await chat.SendPrivateMessageAsync(
            "Alice", messageId, "hello", CancellationToken.None);
        ChatMessageRecord retry = await chat.SendPrivateMessageAsync(
            "Alice", messageId, "hello", CancellationToken.None);

        Assert.AreEqual(ChatMessageState.Failed, failed.State);
        Assert.AreEqual(ChatMessageState.Failed, retry.State);
        Assert.AreEqual(1, fake.PrivateSendCount);
    }

    [TestMethod]
    public async Task CancellationAfterDurableIntentRecordsUnknown()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        using var cancellation = new CancellationTokenSource();
        fake.PrivateSend = _ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        };
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        Guid messageId = Guid.NewGuid();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => chat.SendPrivateMessageAsync(
            "Alice", messageId, "hello", cancellation.Token));
        ConversationRecord conversation = await database.Host.Chat!.GetConversationByPeerAsync(
            "local", "Alice") ?? throw new AssertFailedException();
        ChatPage<ChatMessageRecord> messages = await database.Host.Chat.GetMessagesAsync(
            "local", conversation.ConversationId, null, 10);
        Assert.AreEqual(ChatMessageState.Unknown, messages.Items.Single().State);
    }

    [TestMethod]
    public async Task DeletingHistoryPublishesAReplacementWindow()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        var settings = Settings();
        var logger = new RecordingLogger<ChatRuntime>();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!, logger);
        await chat.StartAsync(CancellationToken.None);
        ChatMessageRecord message = await chat.SendPrivateMessageAsync(
            "Alice", Guid.NewGuid(), "hello", CancellationToken.None);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<ChatTargetDeltaDto>();
        chat.TargetChanged += changes.Enqueue;

        await chat.MarkConversationReadAsync(
            message.TargetId, message.MessageId, CancellationToken.None);
        Assert.IsFalse(logger.Messages.Any(text => text.StartsWith("Deleted ", StringComparison.Ordinal)));

        await chat.DeleteConversationHistoryAsync(
            message.TargetId, CancellationToken.None);

        ChatTargetDeltaDto replacement = changes.Last(change => change.TargetId == message.TargetId);
        Assert.IsTrue(replacement.ReplaceMessages);
        Assert.AreEqual(0, replacement.Messages?.Count);
        Assert.AreEqual(false, replacement.HasEarlierMessages);
        Assert.IsTrue(logger.Messages.Any(text => text.StartsWith(
            "Deleted direct chat history", StringComparison.Ordinal)));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [TestMethod]
    public async Task PrivateRoomClassificationRosterMentionAndIdempotentSendWorkTogether()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        fake.Rooms = new RoomList(
            [],
            [new RoomInfo("secret", 1)],
            [new RoomInfo("secret", 1)],
            ["secret"]);
        fake.JoinedRoom = new RoomData(
            "secret",
            [new UserData("Alice", UserPresence.Online, 0, 0, 0, 0, "CH")],
            isPrivate: true,
            owner: "local",
            operatorList: ["local"]);
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);

        var joined = await chat.Rooms.JoinRoomAsync("secret", remember: true, CancellationToken.None);
        Assert.AreEqual(ServerChatRoomKind.Private, joined.Kind);
        Assert.IsTrue(joined.Owned);
        Assert.IsTrue(joined.Moderated);
        Assert.AreEqual(ServerChatRoomJoinPhase.Joined, joined.Phase);
        Assert.IsTrue(fake.LastJoinWasPrivate);
        RoomMemberPageDto members = await chat.Rooms.GetRoomMembersAsync(
            joined.RoomId, null, 100, null, CancellationToken.None);
        Assert.AreEqual("Alice", members.Items.Single().Username);
        Assert.IsTrue(members.Complete);
        fake.RaiseRoomJoined(
            "secret",
            new UserData("local", UserPresence.Online, 0, 0, 0, 0, "CH"));
        await WaitUntilAsync(async () =>
            (await chat.Rooms.GetRoomMembersAsync(
                joined.RoomId, null, 100, null, CancellationToken.None)).Items.Count == 2);
        RoomMemberDto localMember = (await chat.Rooms.GetRoomMembersAsync(
            joined.RoomId, null, 100, null, CancellationToken.None)).Items
            .Single(member => member.Username == "local");
        Assert.IsTrue(localMember.IsOwner);
        Assert.IsTrue(localMember.IsOperator);
        await chat.Rooms.AddPrivateRoomMemberAsync(joined.RoomId, "Bob", CancellationToken.None);
        Assert.AreEqual(1, fake.AddMemberCount);

        Guid messageId = Guid.NewGuid();
        await chat.SendRoomMessageAsync(joined.RoomId, messageId, "hello", CancellationToken.None);
        await chat.SendRoomMessageAsync(joined.RoomId, messageId, "hello", CancellationToken.None);
        Assert.AreEqual(1, fake.RoomSendCount);

        fake.RaiseRoomMessage(new RoomMessageReceivedEventArgs("secret", "Alice", "hello local!"));
        await WaitUntilAsync(async () =>
            (await database.Host.Chat!.GetSummaryAsync("local")).UnreadNotifications == 1);

        var roomChanges = new System.Collections.Concurrent.ConcurrentQueue<ChatTargetDeltaDto>();
        chat.TargetChanged += roomChanges.Enqueue;
        fake.RaiseState(SoulseekClientStates.None);
        await WaitUntilAsync(() => roomChanges.Any(change =>
            change.TargetId == joined.RoomId
            && change.Room?.Phase == ServerChatRoomJoinPhase.Disconnected
            && change.Room.MemberCount == 0));
    }

    [TestMethod]
    public async Task PrivateRoomModerationEventsUpdateJoinedSummaryAndLocalRosterMember()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        fake.Rooms = new RoomList(
            [],
            [new RoomInfo("secret", 2)],
            [],
            ["secret"]);
        fake.JoinedRoom = new RoomData(
            "secret",
            [
                new UserData("local", UserPresence.Online, 0, 0, 0, 0, "CH"),
                new UserData("Alice", UserPresence.Online, 0, 0, 0, 0, "CH"),
            ],
            isPrivate: true,
            owner: "Alice",
            operatorList: ["local"]);
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        ChatRoomSummaryDto joined = await chat.Rooms.JoinRoomAsync(
            "secret", remember: false, CancellationToken.None);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<ChatTargetDeltaDto>();
        chat.TargetChanged += changes.Enqueue;

        fake.RaisePrivateRoomModeration("secret", moderated: false);

        await WaitUntilAsync(() => changes.Any(change =>
            change.TargetId == joined.RoomId && change.Room?.Moderated == false));
        ChatRoomDetailDto removed = await chat.Rooms.GetRoomDetailAsync(
            joined.RoomId, CancellationToken.None) ?? throw new AssertFailedException();
        Assert.IsFalse(removed.Summary.Moderated);
        Assert.IsFalse(removed.Operators.Contains("local"));
        RoomMemberDto removedMember = (await chat.Rooms.GetRoomMembersAsync(
            joined.RoomId, null, 100, null, CancellationToken.None)).Items
            .Single(member => member.Username == "local");
        Assert.IsFalse(removedMember.IsOperator);

        fake.RaisePrivateRoomModeration("secret", moderated: true);

        await WaitUntilAsync(async () =>
            (await chat.Rooms.GetRoomDetailAsync(joined.RoomId, CancellationToken.None))?.Summary.Moderated == true);
        ChatRoomDetailDto restored = await chat.Rooms.GetRoomDetailAsync(
            joined.RoomId, CancellationToken.None) ?? throw new AssertFailedException();
        Assert.IsTrue(restored.Operators.Contains("local"));
        RoomMemberDto restoredMember = (await chat.Rooms.GetRoomMembersAsync(
            joined.RoomId, null, 100, null, CancellationToken.None)).Items
            .Single(member => member.Username == "local");
        Assert.IsTrue(restoredMember.IsOperator);
    }

    [TestMethod]
    public async Task ModerationEventDuringJoinOverridesTheJoinResponseSnapshot()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        fake.Rooms = new RoomList(
            [],
            [new RoomInfo("secret", 1)],
            [],
            []);
        var joinResponse = new TaskCompletionSource<RoomData>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RoomJoin = _ => joinResponse.Task;
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);

        Task<ChatRoomSummaryDto> joining = chat.Rooms.JoinRoomAsync(
            "secret", remember: false, CancellationToken.None);
        await WaitUntilAsync(() => fake.RoomJoinCount == 1);
        fake.RaisePrivateRoomModeration("secret", moderated: true);
        await WaitUntilAsync(async () =>
            (await database.Host.Chat!.GetRoomByNameAsync(
                "local", "secret", CancellationToken.None)) is { } persisted
            && (await chat.Rooms.GetRoomSummaryAsync(persisted.RoomId, CancellationToken.None))?.Moderated == true);
        joinResponse.SetResult(new RoomData(
            "secret",
            [new UserData("local", UserPresence.Online, 0, 0, 0, 0, "CH")],
            isPrivate: true,
            owner: "Alice",
            operatorList: []));

        ChatRoomSummaryDto joined = await joining;
        Assert.IsTrue(joined.Moderated);
        ChatRoomDetailDto detail = await chat.Rooms.GetRoomDetailAsync(
            joined.RoomId, CancellationToken.None) ?? throw new AssertFailedException();
        Assert.IsTrue(detail.Operators.Contains("local"));
        RoomMemberDto localMember = (await chat.Rooms.GetRoomMembersAsync(
            joined.RoomId, null, 100, null, CancellationToken.None)).Items.Single();
        Assert.IsTrue(localMember.IsOperator);
    }

    [TestMethod]
    public async Task RememberedRoomRejoinsAfterReconnectAndLeaveRemovesRuntimeDesire()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        fake.JoinedRoom = new RoomData("remembered", []);
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        ChatRoomSummaryDto room = await chat.Rooms.JoinRoomAsync(
            "remembered", remember: true, CancellationToken.None);
        Assert.AreEqual(1, fake.RoomJoinCount);

        fake.RaiseState(SoulseekClientStates.None);
        await WaitUntilAsync(() => chat.GetState().State == DaemonFeatureState.Starting);
        fake.RaiseState(SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);
        await WaitUntilAsync(() => fake.RoomJoinCount == 2);
        await WaitUntilAsync(async () =>
            (await chat.Rooms.GetRoomSummaryAsync(room.RoomId, CancellationToken.None))?.Phase
            == ServerChatRoomJoinPhase.Joined);

        await chat.Rooms.LeaveRoomAsync(room.RoomId, CancellationToken.None);
        fake.RaiseState(SoulseekClientStates.None);
        await WaitUntilAsync(() => chat.GetState().State == DaemonFeatureState.Starting);
        long disconnectedRevision = chat.GetState().Revision;
        fake.RaiseState(SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);
        await WaitUntilAsync(() =>
            chat.GetState().State == DaemonFeatureState.Ready
            && chat.GetState().Revision > disconnectedRevision);
        Assert.AreEqual(2, fake.RoomJoinCount);
    }

    [TestMethod]
    public async Task JoinedRoomFilterScansPastEarlierNonMatchingHistory()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        fake.JoinedRoom = new RoomData("joined", []);
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        for (int index = 0; index < 3; index++)
        {
            await database.Host.Chat!.AcceptRoomMessageAsync(
                "local", $"history-{index}", "Alice", "old", false);
        }
        ChatRoomSummaryDto joined = await chat.Rooms.JoinRoomAsync(
            "joined", remember: false, CancellationToken.None);

        ChatRoomPageDto page = await chat.Rooms.GetRoomSummariesAsync(
            "joined", null, 1, CancellationToken.None);

        Assert.AreEqual(joined.RoomId, page.Items.Single().RoomId);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => chat.Rooms.GetAvailableRoomsAsync(
            null, "_w", 10, false, CancellationToken.None));
    }

    [TestMethod]
    public async Task FailedManualJoinPublishesThePersistedRoomFailure()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        fake.RoomJoin = _ => Task.FromException<RoomData>(new IOException("join failed"));
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<ChatTargetDeltaDto>();
        chat.TargetChanged += changes.Enqueue;

        await Assert.ThrowsExactlyAsync<IOException>(() => chat.Rooms.JoinRoomAsync(
            "broken", remember: true, CancellationToken.None));

        RoomSubscriptionRecord room = await database.Host.Chat!.GetRoomByNameAsync(
            "local", "broken") ?? throw new AssertFailedException();
        Assert.IsTrue(changes.Any(change =>
            change.TargetId == room.RoomId
            && change.Room?.Phase == ServerChatRoomJoinPhase.Failed
            && change.Room.FailureReason?.Contains("join failed", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    [TestCategory("Load")]
    [Timeout(180_000)]
    public async Task TenThousandMixedInboundEventsDrainWithinHomeserverBounds()
    {
        const int eventCount = 10_000;
        const int batchSize = 250;
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        await using var chat = new ChatRuntime(settings, session, database.Host.Chat!);
        await chat.StartAsync(CancellationToken.None);
        int committed = 0;
        int notifications = 0;
        int targetChanges = 0;
        chat.MessageCommitted += _ => Interlocked.Increment(ref committed);
        chat.NotificationCommitted += _ => Interlocked.Increment(ref notifications);
        chat.TargetChanged += _ => Interlocked.Increment(ref targetChanges);
        long baselineMemory = GC.GetTotalMemory(forceFullCollection: true);
        long peakMemory = baselineMemory;
        var stopwatch = Stopwatch.StartNew();

        for (int offset = 0; offset < eventCount; offset += batchSize)
        {
            int upper = Math.Min(eventCount, offset + batchSize);
            for (int index = offset; index < upper; index++)
            {
                if ((index & 1) == 0)
                {
                    fake.RaisePrivateMessage(new PrivateMessageReceivedEventArgs(
                        index + 1,
                        DateTime.UnixEpoch.AddSeconds(index),
                        "Alice",
                        $"direct-{index}",
                        replayed: false));
                }
                else
                {
                    fake.RaiseRoomMessage(new RoomMessageReceivedEventArgs(
                        "load-room", "Alice", $"room-{index} mentions local"));
                }
            }
            int expected = upper;
            await WaitUntilAsync(() => Volatile.Read(ref committed) >= expected, TimeSpan.FromSeconds(30));
            peakMemory = Math.Max(peakMemory, GC.GetTotalMemory(forceFullCollection: false));
        }
        stopwatch.Stop();

        ChatStoreSummary summary = await database.Host.Chat!.GetSummaryAsync("local");
        long writerCommits = database.Host.HealthSnapshot?.SuccessfulCommitCount ?? -1;
        long databaseBytes = FileSize(database.DatabasePath)
                             + FileSize(database.DatabasePath + "-wal");
        long managedGrowth = Math.Max(0, peakMemory - baselineMemory);
        TestContext.WriteLine(
            $"chat-load events={eventCount} elapsed_ms={stopwatch.ElapsedMilliseconds} "
            + $"managed_growth_bytes={managedGrowth} database_bytes={databaseBytes} "
            + $"peak_ingress_depth={chat.PeakIngressDepth} drops=0 "
            + $"commits={writerCommits} "
            + $"acks={fake.AcknowledgeCount} notifications={notifications} target_changes={targetChanges}");

        Assert.AreEqual(eventCount / 2, fake.AcknowledgeCount);
        Assert.AreEqual(eventCount / 2, summary.UnreadPrivateMessages);
        Assert.AreEqual(eventCount / 2, summary.UnreadRoomMessages);
        Assert.AreEqual(eventCount, summary.UnreadNotifications);
        Assert.AreEqual(eventCount, notifications);
        Assert.IsTrue(targetChanges >= eventCount);
        Assert.IsTrue(chat.PeakIngressDepth > 0);
        Assert.IsTrue(writerCommits is > 0 and <= eventCount / 8 + 50,
            $"The 10,000-event fixture used {writerCommits:N0} writer commits.");
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"The 10,000-event fixture took {stopwatch.Elapsed}.");
        Assert.IsTrue(managedGrowth < 256L * 1024 * 1024,
            $"Managed memory grew by {managedGrowth:N0} bytes.");
        Assert.IsTrue(databaseBytes < 256L * 1024 * 1024,
            $"SQLite data grew to {databaseBytes:N0} bytes.");
    }

    [TestMethod]
    public async Task BusyDatabaseDelaysPrivateAckWithoutLosingRoomMessages()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var fake = SoulseekClientProxy.Create();
        var settings = Settings();
        await using var session = new DaemonSoulseekRuntime(settings, _ => fake.Client);
        const int roomMessageCount = 17;
        const int ingressCapacity = 16;
        await using var chat = new ChatRuntime(
            settings,
            session,
            database.Host.Chat!,
            ingressCapacity);
        await chat.StartAsync(CancellationToken.None);
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
        }.ToString();
        await using var blocker = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await blocker.OpenAsync();
        await using var command = blocker.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE;";
        await command.ExecuteNonQueryAsync();

        fake.RaisePrivateMessage(new PrivateMessageReceivedEventArgs(
            17, DateTime.UtcNow, "Alice", "held until durable", replayed: false));
        await WaitUntilAsync(
            () => database.Host.HealthSnapshot?.BusyRetryCount > 0,
            TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, fake.AcknowledgeCount, "The protocol message was acknowledged while SQLite was busy.");
        Task roomProducer = Task.Run(() =>
        {
            for (int index = 0; index < roomMessageCount; index++)
            {
                fake.RaiseRoomMessage(new RoomMessageReceivedEventArgs(
                    "pressure-room", "Alice", $"pressure-{index}"));
            }
        });
        await WaitUntilAsync(() => chat.PeakIngressDepth == ingressCapacity);
        Assert.IsFalse(roomProducer.IsCompleted, "A full durable-ingress queue did not backpressure the producer.");
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync();
        await roomProducer.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fake.AcknowledgeCount == 1, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () =>
            (await database.Host.Chat!.GetSummaryAsync("local")).UnreadRoomMessages == roomMessageCount);

        ChatStoreSummary summary = await database.Host.Chat!.GetSummaryAsync("local");
        Assert.AreEqual(1, summary.UnreadPrivateMessages);
        Assert.AreEqual(1, summary.UnreadNotifications);
        Assert.AreEqual(roomMessageCount, summary.UnreadRoomMessages);
    }

    private static EngineSettings Settings() => new()
    {
        Username = "local",
        Password = "password",
        ListenPort = null,
    };

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.IsTrue(condition(), "The expected asynchronous callback did not complete.");
    }

    private static long FileSize(string path)
        => System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0;

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!await condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.IsTrue(await condition(), "The expected asynchronous callback did not complete.");
    }

    private sealed class ChatDatabase : IAsyncDisposable
    {
        private readonly string directory;

        private ChatDatabase(string directory, PersistenceRuntimeHost host)
        {
            this.directory = directory;
            Host = host;
            DatabasePath = Path.Combine(directory, "sockseek.db");
        }

        public PersistenceRuntimeHost Host { get; }
        public string DatabasePath { get; }

        public static async Task<ChatDatabase> CreateAsync()
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "sockseek-chat-runtime-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var host = new PersistenceRuntimeHost(
                new SockseekSqliteOptions(
                    Path.Combine(directory, "sockseek.db"),
                    DefaultTimeoutSeconds: 1,
                    BusyTimeoutMilliseconds: 20),
                new PersistenceWriterOptions
                {
                    BusyRetryDelay = TimeSpan.FromMilliseconds(1),
                    FailureRetryDelay = TimeSpan.FromMilliseconds(10),
                },
                new PersistenceRetentionOptions(),
                "test");
            await host.StartAsync();
            return new ChatDatabase(directory, host);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(TimeSpan.FromSeconds(5));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
