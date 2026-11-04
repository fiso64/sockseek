using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Chat;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class ChatContractsTests
{
    [TestMethod]
    [DataRow("hello alice", "alice", true)]
    [DataRow("hello @Alice!", "alice", true)]
    [DataRow("malice", "alice", false)]
    [DataRow("alice_2", "alice", false)]
    [DataRow("alice-b", "alice", false)]
    public void MentionDetectionUsesWholeTokens(string message, string username, bool expected)
        => Assert.AreEqual(expected, MentionDetector.ContainsWholeUsername(message, username));

    [TestMethod]
    public void ChatSettingsNormalizeRoomsAndRemoveExactDuplicates()
    {
        var settings = new EngineSettings();
        settings.Chat.AutoJoinRooms.AddRange(["  indie ", "indie", "Indie"]);

        ChatSettingsValidator.NormalizeAndValidate(settings);

        CollectionAssert.AreEqual(new[] { "indie", "Indie" }, settings.Chat.AutoJoinRooms);
    }

    [TestMethod]
    public void MessageValidationPreservesWhitespaceButRejectsBlankAndNul()
    {
        Assert.AreEqual(" hello\n", ChatIdentity.ValidateMessage(" hello\n"));
        Assert.AreEqual(" hello ", ChatIdentity.ValidateOutgoingMessage(" hello "));
        foreach (string newline in new[] { "\r", "\n", "\r\n" })
            Assert.ThrowsExactly<ArgumentException>(() => ChatIdentity.ValidateOutgoingMessage($"hello{newline}world"));
        Assert.ThrowsExactly<ArgumentException>(() => ChatIdentity.ValidateMessage(" \t"));
        Assert.ThrowsExactly<ArgumentException>(() => ChatIdentity.ValidateMessage("hello\0world"));
    }

    [TestMethod]
    public void ChatTextValidationAcceptsLargeMessagesAndRejectsMalformedUtf16()
    {
        string large = new('x', 64 * 1_024);
        Assert.AreSame(large, ChatIdentity.ValidateMessage(large));
        Assert.ThrowsExactly<ArgumentException>(() => ChatIdentity.ValidateMessage("hello\ud800"));
        Assert.ThrowsExactly<ArgumentException>(() => ChatIdentity.NormalizeRoom("room\udc00"));
    }

    [TestMethod]
    public void UsernameValidationPreservesExactSoulseekSpelling()
    {
        string decomposed = " Cafe\u0301 ";

        Assert.AreEqual(decomposed, ChatIdentity.ValidateUsername(decomposed));
        Assert.AreNotEqual(
            ChatIdentity.ValidateUsername("Alice"),
            ChatIdentity.ValidateUsername("alice"));
    }

    [TestMethod]
    public void RoomValidationRejectsUnsupportedServerNamesInsteadOfChangingTheirIdentity()
    {
        Assert.AreEqual("Indie Music", ChatIdentity.NormalizeRoom(" Indie Music "));
        Assert.AreEqual(new string('a', 24), ChatIdentity.NormalizeRoom(new string('a', 24)));
        foreach (string invalid in new[] { "caf\u00e9", "cafe\u0301", "two  spaces", "control\u007f", new string('a', 25) })
            Assert.ThrowsExactly<ArgumentException>(() => ChatIdentity.NormalizeRoom(invalid));
    }

    [TestMethod]
    public void DuplicateConfiguredRoomsCountOnceBeforeCapacityValidation()
    {
        var settings = new EngineSettings();
        settings.Chat.AutoJoinRooms.AddRange(
            Enumerable.Repeat("indie", ChatLimits.MaximumDesiredRooms + 1));

        ChatSettingsValidator.NormalizeAndValidate(settings);

        CollectionAssert.AreEqual(new[] { "indie" }, settings.Chat.AutoJoinRooms);
    }
}
