using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Chat;
using Sockseek.Persistence;
using Sockseek.Persistence.Chat;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class ChatPersistenceStoreTests
{
    [TestMethod]
    public async Task PrivateReplayIsIdempotentAndCreatesOneNotification()
    {
        await using var database = await ChatDatabase.CreateAsync();
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");

        var first = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "hello", 42, sentAt);
        var replay = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "hello", 42, sentAt);

        Assert.IsTrue(first.Inserted);
        Assert.IsFalse(replay.Inserted);
        Assert.AreEqual(first.Message.MessageId, replay.Message.MessageId);
        Assert.IsNotNull(first.Notification);
        Assert.IsNull(replay.Notification);
        ChatStoreSummary summary = await database.Store.GetSummaryAsync("local");
        Assert.AreEqual(1, summary.UnreadPrivateMessages);
        Assert.AreEqual(1, summary.UnreadNotifications);
    }

    [TestMethod]
    public async Task MixedInboundBatchCommitsOnceAndPreservesOrderAndSnapshots()
    {
        await using var database = await ChatDatabase.CreateAsync();
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");

        IReadOnlyList<IncomingChatCommitResult> results =
            await database.Store.AcceptIncomingMessagesAsync(
            [
                new PrivateChatInboundMessage("local", "Alice", "direct-1", 1, sentAt),
                new RoomChatInboundMessage("local", "lobby", "Alice", "room-1", true),
                new PrivateChatInboundMessage("local", "Alice", "direct-1", 1, sentAt),
                new PrivateChatInboundMessage("local", "Alice", "direct-2", 2, sentAt.AddSeconds(1)),
                new RoomChatInboundMessage("local", "lobby", "Alice", "room-2", false),
            ]);

        Assert.AreEqual(5, results.Count);
        CollectionAssert.AreEqual(
            new[] { "direct-1", "room-1", "direct-1", "direct-2", "room-2" },
            results.Select(result => result.Message.Body).ToArray());
        Assert.IsFalse(results[2].Inserted);
        Assert.AreEqual(results[0].Message.MessageId, results[2].Message.MessageId);
        Assert.IsNull(results[2].Notification);
        Assert.IsTrue(results[0].Message.Sequence < results[1].Message.Sequence);
        Assert.IsTrue(results[1].Message.Sequence < results[3].Message.Sequence);
        Assert.IsTrue(results[3].Message.Sequence < results[4].Message.Sequence);
        CollectionAssert.AreEqual(
            new[] { 1, 1, 2 },
            results.Where(result => result.Conversation is not null)
                .Select(result => result.Conversation!.UnreadCount).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            results.Where(result => result.Room is not null)
                .Select(result => result.Room!.UnreadCount).ToArray());
        Assert.AreEqual(1L, database.HealthSnapshot.SuccessfulCommitCount);

        ChatStoreSummary summary = await database.Store.GetSummaryAsync("local");
        Assert.AreEqual(2, summary.UnreadPrivateMessages);
        Assert.AreEqual(2, summary.UnreadRoomMessages);
        Assert.AreEqual(3, summary.UnreadNotifications);
    }

    [TestMethod]
    public async Task OutgoingMessageIdIsIdempotentAndConflictingReuseIsReported()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid id = Guid.NewGuid();

        var created = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "hello");
        var existing = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "hello");
        var conflict = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "different");

        Assert.AreEqual(OutgoingChatPreparationStatus.Created, created.Status);
        Assert.AreEqual(OutgoingChatPreparationStatus.Existing, existing.Status);
        Assert.AreEqual(OutgoingChatPreparationStatus.Conflict, conflict.Status);
        Assert.AreEqual(ChatMessageState.Pending, created.Message.State);

        ChatMessageRecord sent = await database.Store.SetMessageStateAsync(
            "local", id, ChatMessageState.Sent, null);
        Assert.AreEqual(ChatMessageState.Sent, sent.State);
    }

    [TestMethod]
    public async Task MarkReadAdvancesWatermarkAndReadsRelatedNotification()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var accepted = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "hello", 7, DateTimeOffset.UtcNow);

        await database.Store.MarkConversationReadAsync(
            "local", accepted.Message.TargetId, accepted.Message.MessageId);

        ConversationRecord conversation = await database.Store.GetConversationAsync(
            "local", accepted.Message.TargetId) ?? throw new AssertFailedException();
        Assert.AreEqual(0, conversation.UnreadCount);
        ChatStoreSummary summary = await database.Store.GetSummaryAsync("local");
        Assert.AreEqual(0, summary.UnreadNotifications);
    }

    [TestMethod]
    public async Task ReadOperationsUpdateManyNotificationsWithConstantDatabaseCommands()
    {
        await using var database = await ChatDatabase.CreateAsync(countCommands: true);
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");
        ChatInboundMessage[] messages = Enumerable.Range(1, 200)
            .Select(index => (ChatInboundMessage)new PrivateChatInboundMessage(
                "local",
                index <= 100 ? "Alice" : "Bob",
                $"message-{index}",
                index,
                sentAt.AddSeconds(index)))
            .ToArray();
        database.CommandCounter!.Reset();
        IReadOnlyList<IncomingChatCommitResult> accepted =
            await database.Store.AcceptIncomingMessagesAsync(messages);
        Assert.IsTrue(database.CommandCounter.SelectCommands <= 10,
            $"Persisting one inbound batch executed {database.CommandCounter.SelectCommands} select commands.");

        database.CommandCounter.Reset();
        await database.Store.MarkConversationReadAsync(
            "local",
            accepted[99].Message.TargetId,
            accepted[99].Message.MessageId);

        Assert.IsTrue(database.CommandCounter.Executed <= 5,
            $"Marking 100 related notifications read executed {database.CommandCounter.Executed} database commands.");
        Assert.AreEqual(100, (await database.Store.GetSummaryAsync("local")).UnreadNotifications);

        database.CommandCounter.Reset();
        await database.Store.MarkNotificationsReadAsync(
            "local", accepted[^1].Notification!.Sequence, ids: null);

        Assert.IsTrue(database.CommandCounter.Executed <= 3,
            $"Marking 100 remaining notifications read executed {database.CommandCounter.Executed} database commands.");
        Assert.AreEqual(0, (await database.Store.GetSummaryAsync("local")).UnreadNotifications);
    }

    [TestMethod]
    public async Task InboundSnapshotCountsOnlyMessagesAfterReadWatermark()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var first = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "read", 1, DateTimeOffset.UtcNow);
        await database.Store.MarkConversationReadAsync(
            "local", first.Message.TargetId, first.Message.MessageId);

        var second = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "unread", 2, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.AreEqual(1, second.Conversation!.UnreadCount);
    }

    [TestMethod]
    public async Task SequencesRemainMonotonicAfterNewestHistoryIsDeleted()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var first = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "first", 1, DateTimeOffset.Parse("2026-08-06T20:00:00Z"));

        await database.Store.DeleteHistoryAsync(
            "local", first.Message.TargetId, ChatTargetKind.Direct);
        var second = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "second", 2, DateTimeOffset.Parse("2026-08-06T20:00:01Z"));

        Assert.IsTrue(second.Message.Sequence > first.Message.Sequence);
        Assert.IsNotNull(first.Notification);
        Assert.IsNotNull(second.Notification);
        Assert.IsTrue(second.Notification!.Sequence > first.Notification!.Sequence);
    }

    [TestMethod]
    public async Task LocalAccountsHaveIndependentReplayAndUnreadPartitions()
    {
        await using var database = await ChatDatabase.CreateAsync();
        DateTimeOffset timestamp = DateTimeOffset.Parse("2026-08-06T20:00:00Z");

        var first = await database.Store.AcceptPrivateMessageAsync(
            "local-a", "Alice", "one", 42, timestamp);
        var second = await database.Store.AcceptPrivateMessageAsync(
            "local-b", "Alice", "two", 42, timestamp);

        Assert.AreNotEqual(first.Message.MessageId, second.Message.MessageId);
        Assert.AreEqual(1, (await database.Store.GetSummaryAsync("local-a")).UnreadPrivateMessages);
        Assert.AreEqual(1, (await database.Store.GetSummaryAsync("local-b")).UnreadPrivateMessages);
    }

    [TestMethod]
    public async Task MessageIdReuseAcrossAccountsIsAnExplicitConflict()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid id = Guid.NewGuid();
        await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local-a", "Alice", id, "hello");

        ChatStateConflictException error = await Assert.ThrowsExactlyAsync<ChatStateConflictException>(
            () => database.Store.PrepareOutgoingPrivateMessageAsync(
                "local-b", "Alice", id, "hello"));

        StringAssert.Contains(error.Message, "MessageId");
    }

    [TestMethod]
    public async Task MessagePagesUseValidatedReverseKeysetCursors()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid? targetId = null;
        for (int id = 1; id <= 3; id++)
        {
            var accepted = await database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"message-{id}", id,
                DateTimeOffset.Parse("2026-08-06T20:00:00Z").AddSeconds(id));
            targetId = accepted.Message.TargetId;
        }

        ChatPage<ChatMessageRecord> newest = await database.Store.GetMessagesAsync(
            "local", targetId!.Value, null, 2);
        ChatPage<ChatMessageRecord> older = await database.Store.GetMessagesAsync(
            "local", targetId.Value, newest.NextCursor, 2);

        CollectionAssert.AreEqual(
            new[] { "message-2", "message-3" }, newest.Items.Select(item => item.Body).ToArray());
        CollectionAssert.AreEqual(
            new[] { "message-1" }, older.Items.Select(item => item.Body).ToArray());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => database.Store.GetMessagesAsync(
            "local", targetId.Value, new string('a', 257), 2));
    }

    [TestMethod]
    public async Task StartupReconciliationMarksPendingMessagesUnknownWithoutResend()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid id = Guid.NewGuid();
        var pending = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "hello");

        Assert.AreEqual(1, await database.Store.ReconcilePendingMessagesAsync());
        ChatPage<ChatMessageRecord> messages = await database.Store.GetMessagesAsync(
            "local", pending.Message.TargetId, null, 10);
        Assert.AreEqual(ChatMessageState.Unknown, messages.Items.Single().State);
    }

    [TestMethod]
    public async Task StartupReconciliationUsesConstantDatabaseCommands()
    {
        await using var database = await ChatDatabase.CreateAsync(countCommands: true);
        for (int index = 0; index < 8; index++)
        {
            await database.Store.PrepareOutgoingPrivateMessageAsync(
                "local", "Alice", Guid.NewGuid(), $"message-{index}");
        }

        database.CommandCounter!.Reset();
        Assert.AreEqual(8, await database.Store.ReconcilePendingMessagesAsync());

        Assert.IsTrue(database.CommandCounter.Executed <= 3,
            $"Pending-message reconciliation executed {database.CommandCounter.Executed} database commands.");
    }

    [TestMethod]
    public async Task RetentionRepairsTargetStateAndPreservesMonotonicSequences()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-06T20:00:00Z"));
        await using var database = await ChatDatabase.CreateAsync(clock);
        var first = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "old", 1, clock.GetUtcNow());

        clock.Advance(TimeSpan.FromDays(2));
        ChatRetentionResult retained = await database.Store.ApplyRetentionAsync(
            TimeSpan.FromDays(1), TimeSpan.FromDays(1), 100);

        Assert.AreEqual(1, retained.PrunedMessages);
        Assert.AreEqual(first.Message.TargetId, retained.AffectedTargets.Single().TargetId);
        Assert.AreEqual(0, (await database.Store.GetSummaryAsync("local")).UnreadNotifications);
        Assert.AreEqual(0, (await database.Store.GetMessagesAsync(
            "local", first.Message.TargetId, null, 10)).Items.Count);

        var second = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "new", 2, clock.GetUtcNow());
        Assert.IsTrue(second.Message.Sequence > first.Message.Sequence);
        Assert.IsNotNull(first.Notification);
        Assert.IsNotNull(second.Notification);
        Assert.IsTrue(second.Notification!.Sequence > first.Notification!.Sequence);
    }

    [TestMethod]
    public async Task PrivateAndRoomRetentionPoliciesAreIndependent()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-06T20:00:00Z"));
        await using var database = await ChatDatabase.CreateAsync(clock);
        IncomingChatCommitResult direct = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "durable direct message", 1, clock.GetUtcNow());
        IncomingChatCommitResult room = await database.Store.AcceptRoomMessageAsync(
            "local", "busy-room", "Alice", "transient room message", true);

        clock.Advance(TimeSpan.FromDays(31));
        ChatRetentionResult roomRetention = await database.Store.ApplyRetentionAsync(
            privateMessageAge: null,
            roomMessageAge: TimeSpan.FromDays(30),
            batchSize: 100);

        Assert.AreEqual(1, roomRetention.PrunedMessages);
        Assert.AreEqual(ChatTargetKind.Room, roomRetention.AffectedTargets.Single().Kind);
        Assert.AreEqual(1, (await database.Store.GetMessagesAsync(
            "local", direct.Message.TargetId, null, 10)).Items.Count);
        Assert.AreEqual(0, (await database.Store.GetMessagesAsync(
            "local", room.Message.TargetId, null, 10)).Items.Count);
        ChatStoreSummary afterRoomRetention = await database.Store.GetSummaryAsync("local");
        Assert.AreEqual(1, afterRoomRetention.UnreadPrivateMessages);
        Assert.AreEqual(0, afterRoomRetention.UnreadRoomMessages);
        Assert.AreEqual(1, afterRoomRetention.UnreadNotifications);

        ChatRetentionResult privateRetention = await database.Store.ApplyRetentionAsync(
            privateMessageAge: TimeSpan.FromDays(30),
            roomMessageAge: null,
            batchSize: 100);

        Assert.AreEqual(1, privateRetention.PrunedMessages);
        Assert.AreEqual(ChatTargetKind.Direct, privateRetention.AffectedTargets.Single().Kind);
        Assert.AreEqual(0, (await database.Store.GetSummaryAsync("local")).UnreadNotifications);
    }

    [TestMethod]
    public async Task RetentionRepairsManyTargetsWithConstantDatabaseCommands()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-06T20:00:00Z"));
        await using var database = await ChatDatabase.CreateAsync(clock, countCommands: true);
        const int targetCount = 12;
        for (int index = 0; index < targetCount; index++)
        {
            await database.Store.AcceptPrivateMessageAsync(
                "local", $"peer-{index}", "old", index, clock.GetUtcNow());
        }

        clock.Advance(TimeSpan.FromDays(2));
        database.CommandCounter!.Reset();
        ChatRetentionResult result = await database.Store.ApplyRetentionAsync(
            privateMessageAge: TimeSpan.FromDays(1),
            roomMessageAge: null,
            batchSize: targetCount);

        Assert.AreEqual(targetCount, result.PrunedMessages);
        Assert.AreEqual(targetCount, result.AffectedTargets.Count);
        Assert.IsTrue(database.CommandCounter.Executed <= 8,
            $"Multi-target retention executed {database.CommandCounter.Executed} database commands.");
    }

    [TestMethod]
    [TestCategory("Load")]
    [DoNotParallelize] // Measure this workload without unrelated database writers.
    [Timeout(60_000)]
    public async Task RetentionWithConcurrentReadsAndNewMessagesRemainsConsistent()
    {
        const int oldMessageCount = 1_000;
        const int newMessageCount = 64;
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-01T20:00:00Z"));
        await using var database = await ChatDatabase.CreateAsync(clock);

        Task<IncomingChatCommitResult>[] oldWrites = Enumerable.Range(1, oldMessageCount)
            .Select(id => database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"old-{id}", id, clock.GetUtcNow().AddSeconds(id)))
            .ToArray();
        await Task.WhenAll(oldWrites).WaitAsync(TimeSpan.FromSeconds(30));
        Guid targetId = oldWrites[0].Result.Message.TargetId;
        long largestOldSequence = oldWrites.Max(task => task.Result.Message.Sequence);

        clock.Advance(TimeSpan.FromDays(2));
        Task<ChatRetentionResult> retention = database.Store.ApplyRetentionAsync(
            TimeSpan.FromDays(1), TimeSpan.FromDays(1), oldMessageCount);
        Task<IncomingChatCommitResult>[] newWrites = Enumerable.Range(
                oldMessageCount + 1, newMessageCount)
            .Select(id => database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"new-{id}", id, clock.GetUtcNow().AddSeconds(id)))
            .ToArray();
        Task[] readers = Enumerable.Range(0, 4).Select(async _ =>
        {
            for (int pass = 0; pass < 20; pass++)
            {
                ChatPage<ChatMessageRecord> page = await database.Store.GetMessagesAsync(
                    "local", targetId, null, 500);
                Assert.IsTrue(page.Items.Zip(page.Items.Skip(1))
                    .All(pair => pair.First.Sequence < pair.Second.Sequence));
                await Task.Yield();
            }
        }).ToArray();

        await Task.WhenAll([retention, Task.WhenAll(newWrites), Task.WhenAll(readers)])
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(oldMessageCount, retention.Result.PrunedMessages);
        ChatPage<ChatMessageRecord> remaining = await database.Store.GetMessagesAsync(
            "local", targetId, null, int.MaxValue);
        Assert.AreEqual(newMessageCount, remaining.Items.Count);
        Assert.IsTrue(remaining.Items.All(message => message.Sequence > largestOldSequence));
        Assert.AreEqual(
            newMessageCount,
            (await database.Store.GetSummaryAsync("local")).UnreadPrivateMessages);
    }

    [TestMethod]
    public async Task BurstOfCriticalCommandsDrainsWithoutPeriodicWriterDelay()
    {
        await using var database = await ChatDatabase.CreateAsync();
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");

        // Cross the writer's 16-command fairness window; a larger burst only
        // repeats the same scheduling boundary and belongs in Load coverage.
        const int count = 33;
        Task<IncomingChatCommitResult>[] writes = Enumerable.Range(1, count)
            .Select(id => database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"message-{id}", id, sentAt.AddSeconds(id)))
            .ToArray();
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(count, writes.Select(task => task.Result.Message.Sequence).Distinct().Count());
        Assert.AreEqual(count, (await database.Store.GetSummaryAsync("local")).UnreadPrivateMessages);
    }

    [TestMethod]
    public async Task ConversationPageUsesConstantDatabaseQueries()
    {
        await using var database = await ChatDatabase.CreateAsync(countCommands: true);
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");
        for (int index = 0; index < 20; index++)
        {
            await database.Store.AcceptPrivateMessageAsync(
                "local", $"peer-{index}", "hello", index, sentAt.AddSeconds(index));
        }

        database.CommandCounter!.Reset();
        ChatPage<ConversationRecord> page = await database.Store.GetConversationsAsync(
            "local", unread: null, archived: null, cursor: null, limit: 20);

        Assert.AreEqual(20, page.Items.Count);
        Assert.IsTrue(database.CommandCounter.Executed <= 3,
            $"Conversation page executed {database.CommandCounter.Executed} database commands.");
    }

    [TestMethod]
    public async Task DeleteHistoryUsesConstantDatabaseCommands()
    {
        await using var database = await ChatDatabase.CreateAsync(countCommands: true);
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");
        var accepted = await database.Store.AcceptIncomingMessagesAsync(Enumerable.Range(1, 100)
            .Select(index => (ChatInboundMessage)new PrivateChatInboundMessage(
                "local", "Alice", $"message-{index}", index, sentAt.AddSeconds(index))).ToArray());
        Guid targetId = accepted[0].Message.TargetId;

        database.CommandCounter!.Reset();
        await database.Store.DeleteHistoryAsync("local", targetId, ChatTargetKind.Direct);

        Assert.IsTrue(database.CommandCounter.Executed <= 4,
            $"History deletion executed {database.CommandCounter.Executed} database commands.");
        Assert.AreEqual(0, (await database.Store.GetMessagesAsync(
            "local", targetId, cursor: null, limit: 10)).Items.Count);
    }

    private sealed class ChatDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly SqliteDatabaseOwner owner;
        private readonly SockseekDbContextFactory factory;
        private readonly PersistenceInbox inbox;
        private readonly PersistenceHealth health;
        private readonly CancellationTokenSource stop = new();
        private readonly Task writerTask;

        private ChatDatabase(
            string directory,
            SqliteDatabaseOwner owner,
            SockseekDbContextFactory factory,
            CountingCommandInterceptor? commandCounter,
            TimeProvider? timeProvider)
        {
            this.directory = directory;
            this.owner = owner;
            this.factory = factory;
            var options = new PersistenceWriterOptions();
            health = new PersistenceHealth();
            inbox = new PersistenceInbox(options, health);
            writerTask = new PersistenceWriter(factory, inbox, health, options).RunAsync(stop.Token);
            Store = new ChatPersistenceStore(factory, inbox, timeProvider);
            CommandCounter = commandCounter;
        }

        public ChatPersistenceStore Store { get; }
        public CountingCommandInterceptor? CommandCounter { get; }
        public PersistenceHealthSnapshot HealthSnapshot => health.Snapshot(inbox);

        public static Task<ChatDatabase> CreateAsync(
            TimeProvider? timeProvider = null,
            bool countCommands = false)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "sockseek-chat-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sqlite = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
            global::Tests.TestDatabase.Create(sqlite.DatabasePath);
            SqliteDatabaseOwner owner = SqliteDatabaseOwner.Acquire(sqlite);
            var commandCounter = countCommands ? new CountingCommandInterceptor() : null;
            DbContextOptions<SockseekDbContext> contextOptions = SockseekDbContextOptions.Create(sqlite);
            if (commandCounter is not null)
            {
                contextOptions = new DbContextOptionsBuilder<SockseekDbContext>(contextOptions)
                    .AddInterceptors(commandCounter)
                    .Options;
            }
            var factory = new SockseekDbContextFactory(contextOptions);
            return Task.FromResult(new ChatDatabase(directory, owner, factory, commandCounter, timeProvider));
        }

        public async ValueTask DisposeAsync()
        {
            inbox.Complete();
            await writerTask;
            stop.Dispose();
            owner.Dispose();
            using var context = factory.CreateDbContext();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                (Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection());
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int executed;
        private int selectCommands;

        public int Executed => Volatile.Read(ref executed);
        public int SelectCommands => Volatile.Read(ref selectCommands);

        public void Reset()
        {
            Volatile.Write(ref executed, 0);
            Volatile.Write(ref selectCommands, 0);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref executed);
            CountSelect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            CountSelect(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Interlocked.Increment(ref executed);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Interlocked.Increment(ref executed);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            return ValueTask.FromResult(result);
        }

        private void CountSelect(DbCommand command)
        {
            if (command.CommandText.AsSpan().TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref selectCommands);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
