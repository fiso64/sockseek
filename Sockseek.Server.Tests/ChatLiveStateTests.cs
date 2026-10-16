using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Chat;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public sealed class ChatLiveStateTests
{
    [TestMethod]
    public void ConversationSnapshotAndDeltaUpdateBoundedClientPartition()
    {
        var store = new DaemonClientStore();
        Guid epoch = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        StateStreamScopeDto scope = StateStreamScopeDto.ChatConversation(conversationId);
        ChatMessageDto first = Message(conversationId, 1, "first", ServerChatMessageState.Received);
        var conversation = Conversation(conversationId, 1, first);
        store.ApplySnapshot(new StateSnapshotDto(
            scope,
            new StateStreamPositionDto(epoch, 3),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], [],
            new ChatTargetSnapshotDto(
                ServerChatTargetKind.Direct, conversationId, conversation, null, [first], false)));

        ChatMessageDto second = Message(conversationId, 2, "second", ServerChatMessageState.Received);
        var update = store.Apply(new StateUpdateBatchDto(
            scope,
            epoch,
            3,
            4,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                ChatTargets =
                [
                    new ChatTargetDeltaDto(
                        ServerChatTargetKind.Direct,
                        conversationId,
                        Conversation(conversationId, 2, second),
                        Messages: [second])
                ],
            },
            []));

        Assert.AreEqual(DaemonClientApplyStatus.Applied, update.Status);
        ChatTargetSnapshotDto target = store.GetChatTarget(scope)
            ?? throw new AssertFailedException("Chat target was not retained.");
        Assert.AreEqual(2, target.Messages.Count);
        Assert.AreEqual("second", target.Messages[^1].Text);
        Assert.AreEqual(2, target.Conversation?.Revision);
    }

    [TestMethod]
    public void ChatScopeRejectsCrossTargetDelta()
    {
        var store = new DaemonClientStore();
        Guid epoch = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        StateStreamScopeDto scope = StateStreamScopeDto.ChatConversation(conversationId);
        ChatMessageDto first = Message(conversationId, 1, "first", ServerChatMessageState.Received);
        store.ApplySnapshot(new StateSnapshotDto(
            scope,
            new StateStreamPositionDto(epoch, 0),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], [],
            new ChatTargetSnapshotDto(
                ServerChatTargetKind.Direct,
                conversationId,
                Conversation(conversationId, 1, first),
                null,
                [first],
                false)));

        Assert.ThrowsExactly<ArgumentException>(() => store.Apply(new StateUpdateBatchDto(
            scope,
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                ChatTargets =
                [
                    new ChatTargetDeltaDto(ServerChatTargetKind.Direct, Guid.NewGuid())
                ],
            },
            [])));
    }

    [TestMethod]
    public void DaemonNotificationDeltaIsImmediatelyAvailableForGuiBubble()
    {
        var store = new DaemonClientStore();
        Guid epoch = Guid.NewGuid();
        store.ApplySnapshot(new StateSnapshotDto(
            StateStreamScopeDto.Daemon,
            new StateStreamPositionDto(epoch, 0),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], []));
        Guid conversationId = Guid.NewGuid();
        ChatMessageDto message = Message(conversationId, 1, "hello", ServerChatMessageState.Received);
        var notification = new UserNotificationDto(
            Guid.NewGuid(), 1, ServerUserNotificationKind.PrivateMessage,
            DateTimeOffset.UtcNow, null, "Alice", ServerChatTargetKind.Direct,
            conversationId, "Alice", message.MessageId, "hello",
            $"/api/chat/conversations/{conversationId:D}");

        store.Apply(new StateUpdateBatchDto(
            StateStreamScopeDto.Daemon,
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with { Notifications = [notification] },
            []));

        Assert.AreEqual(notification.NotificationId, store.GetLiveNotifications().Single().NotificationId);

        store.ApplySnapshot(new StateSnapshotDto(
            StateStreamScopeDto.Daemon,
            new StateStreamPositionDto(Guid.NewGuid(), 0),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], []));
        Assert.AreEqual(0, store.GetLiveNotifications().Count);
    }

    [TestMethod]
    public void NonChatScopesRejectChatTargetsAndWorkflowNotifications()
    {
        var store = new DaemonClientStore();
        Guid epoch = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        store.ApplySnapshot(new StateSnapshotDto(
            StateStreamScopeDto.Daemon,
            new StateStreamPositionDto(epoch, 0),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], []));

        Assert.ThrowsExactly<ArgumentException>(() => store.Apply(new StateUpdateBatchDto(
            StateStreamScopeDto.Daemon,
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                ChatTargets = [new ChatTargetDeltaDto(ServerChatTargetKind.Direct, conversationId)],
            },
            [])));

        Guid workflowId = Guid.NewGuid();
        StateStreamScopeDto workflowScope = StateStreamScopeDto.Workflow(workflowId);
        store.ApplySnapshot(new StateSnapshotDto(
            workflowScope,
            new StateStreamPositionDto(epoch, 0),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], []));
        var notification = Notification(conversationId, 1);
        Assert.ThrowsExactly<ArgumentException>(() => store.Apply(new StateUpdateBatchDto(
            workflowScope,
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with { Notifications = [notification] },
            [])));
    }

    [TestMethod]
    public void ScopeValidationRejectsUnknownKindsAndEmptyIds()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new StateStreamScopeDto((StateStreamScopeKind)999).Validate());
        Assert.ThrowsExactly<ArgumentException>(() =>
            StateStreamScopeDto.Workflow(Guid.Empty).Validate());
        Assert.ThrowsExactly<ArgumentException>(() =>
            StateStreamScopeDto.ChatRoom(Guid.Empty).Validate());
    }

    [TestMethod]
    public void RemovingChatSubscriptionDropsOnlyItsLivePartition()
    {
        var store = new DaemonClientStore();
        Guid epoch = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        StateStreamScopeDto scope = StateStreamScopeDto.ChatConversation(conversationId);
        ChatMessageDto message = Message(conversationId, 1, "hello", ServerChatMessageState.Received);
        store.ApplySnapshot(new StateSnapshotDto(
            scope,
            new StateStreamPositionDto(epoch, 1),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], [],
            new ChatTargetSnapshotDto(
                ServerChatTargetKind.Direct,
                conversationId,
                Conversation(conversationId, 1, message),
                null,
                [message],
                false)));

        store.RemoveChatTarget(scope);

        Assert.IsNull(store.GetChatTarget(scope));
        Assert.IsNull(store.GetPosition(scope));
    }

    [TestMethod]
    public void HistoryReplacementDeltaRemovesMessagesFromOpenScope()
    {
        var store = new DaemonClientStore();
        Guid epoch = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        StateStreamScopeDto scope = StateStreamScopeDto.ChatConversation(conversationId);
        ChatMessageDto first = Message(conversationId, 1, "first", ServerChatMessageState.Received);
        store.ApplySnapshot(new StateSnapshotDto(
            scope,
            new StateStreamPositionDto(epoch, 1),
            DateTimeOffset.UtcNow,
            null,
            [], [], [], [],
            new ChatTargetSnapshotDto(
                ServerChatTargetKind.Direct,
                conversationId,
                Conversation(conversationId, 1, first),
                null,
                [first],
                false)));

        store.Apply(new StateUpdateBatchDto(
            scope,
            epoch,
            1,
            2,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                ChatTargets =
                [
                    new ChatTargetDeltaDto(
                        ServerChatTargetKind.Direct,
                        conversationId,
                        Conversation(conversationId, 2, first) with { LastMessage = null },
                        Messages: [],
                        ReplaceMessages: true,
                        HasEarlierMessages: false),
                ],
            },
            []));

        ChatTargetSnapshotDto target = store.GetChatTarget(scope)
            ?? throw new AssertFailedException();
        Assert.AreEqual(0, target.Messages.Count);
        Assert.IsFalse(target.HasEarlierMessages);
    }

    [TestMethod]
    public void NotificationProjectionUsesBoundedOneLinePreviewAndResourceIdentity()
    {
        Guid conversationId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        var message = new ChatMessageRecord(
            messageId, 1, "local", Sockseek.Core.Chat.ChatTargetKind.Direct, conversationId,
            "alice", "Alice", "alice", "Alice", Sockseek.Core.Chat.ChatMessageDirection.Incoming,
            "first\n" + new string('x', ChatLimits.NotificationPreviewCharacters + 20),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Sockseek.Core.Chat.ChatMessageState.Received,
            null, 1, DateTimeOffset.UtcNow);
        var record = new UserNotificationRecord(
            Guid.NewGuid(), 1, "local", Sockseek.Core.Chat.UserNotificationKind.PrivateMessage,
            messageId, DateTimeOffset.UtcNow, null, message);

        UserNotificationDto projected = ChatDtoMapper.ToDto(record);

        Assert.IsFalse(projected.Preview.Contains('\n'));
        Assert.IsTrue(projected.Preview.EnumerateRunes().Count() <= ChatLimits.NotificationPreviewCharacters);
        Assert.AreEqual(conversationId, projected.TargetId);
        Assert.AreEqual($"/api/chat/conversations/{conversationId:D}", projected.ResourcePath);
    }

    private static ChatMessageDto Message(
        Guid targetId, long sequence, string text, ServerChatMessageState state)
        => new(
            Guid.NewGuid(), sequence, ServerChatTargetKind.Direct, targetId, "Alice",
            ServerChatMessageDirection.Incoming, text, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, state, null);

    private static ConversationSummaryDto Conversation(
        Guid id, long revision, ChatMessageDto last)
        => new(id, "Alice", false, false, 1, 0, revision, last);

    private static UserNotificationDto Notification(Guid conversationId, long sequence)
    {
        Guid messageId = Guid.NewGuid();
        return new UserNotificationDto(
            Guid.NewGuid(), sequence, ServerUserNotificationKind.PrivateMessage,
            DateTimeOffset.UtcNow, null, "Alice", ServerChatTargetKind.Direct,
            conversationId, "Alice", messageId, "hello",
            $"/api/chat/conversations/{conversationId:D}");
    }
}
