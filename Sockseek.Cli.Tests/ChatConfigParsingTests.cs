using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.ConfigParsingTests;

[TestClass]
public sealed class ChatConfigParsingTests
{
    [TestMethod]
    public void ChatRoomsUseReplaceAndExplicitAppendSyntax()
    {
        var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());

        var bound = ConfigManager.BindAll(file,
            ["--chat-room", "indie", "--chat-room", "+ electronic"]);

        CollectionAssert.AreEqual(
            new[] { "indie", "electronic" },
            bound.Engine.Chat.AutoJoinRooms);
    }

    [TestMethod]
    public void PrivateAndRoomRetentionHaveIndependentDefaultsAndOverrides()
    {
        var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
        var defaults = ConfigManager.BindAll(file, []);
        var overridden = ConfigManager.BindAll(file,
        [
            "--private-message-retention-days", "90",
            "--room-message-retention-days", "forever",
        ]);

        Assert.IsNull(defaults.Daemon.PrivateMessageRetention);
        Assert.AreEqual(TimeSpan.FromDays(30), defaults.Daemon.RoomMessageRetention);
        Assert.AreEqual(TimeSpan.FromDays(90), overridden.Daemon.PrivateMessageRetention);
        Assert.IsNull(overridden.Daemon.RoomMessageRetention);
    }

    [TestMethod]
    public void ChatSettingsAreRejectedInNamedProfiles()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sockseek-chat-profile-{Guid.NewGuid():N}.conf");
        File.WriteAllText(path, "[named]\nchat-room = indie\n");
        try
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(() => ConfigManager.Load(path));
            StringAssert.Contains(ex.Message, "not allowed in named or automatic profile");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
