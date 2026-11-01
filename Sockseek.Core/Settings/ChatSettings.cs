namespace Sockseek.Core.Settings;

/// <summary>Daemon-lifetime chat settings.</summary>
public sealed class ChatSettings
{
    /// <summary>Rooms requested after each successful Soulseek login.</summary>
    public List<string> AutoJoinRooms { get; set; } = [];
    [SettingOption("--chat-room", DefaultProfileOnly = true)]
    private static void ConfigureRoom(SettingWriter changes, string value)
        => changes.ListItem(nameof(AutoJoinRooms), value, item => item);

}
