namespace Sockseek.Core.Settings;

/// <summary>Daemon-lifetime chat settings.</summary>
public sealed class ChatSettings
{
    /// <summary>Rooms requested after each successful Soulseek login.</summary>
    [SettingOption("--chat-room", ListSyntax = OptionListSyntax.CommaSeparated, DefaultProfileOnly = true)]
    public List<string> AutoJoinRooms { get; set; } = [];
}
