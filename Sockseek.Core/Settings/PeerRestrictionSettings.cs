namespace Sockseek.Core.Settings;

/// <summary>
/// Independent inbound restrictions. Upload access controls what a remote peer
/// may search, browse, and download from this daemon. Private-message access
/// controls only incoming direct messages; room messages and outbound actions
/// are deliberately separate.
/// </summary>
public sealed class PeerRestrictionSettings
{
    public UploadAccessSettings UploadAccess { get; set; } = new();

    public PrivateMessageAccessSettings PrivateMessages { get; set; } = new();
}

public sealed class UploadAccessSettings
{
    public List<string> BlockedUsernames { get; set; } = [];

    public List<string> BlockedIpAddresses { get; set; } = [];
    [SettingOption("--upload-blocked-user", DefaultProfileOnly = true)]
    private static void ConfigureUser(SettingWriter changes, string value)
        => changes.ListItem(nameof(BlockedUsernames), value, item => item);

    [SettingOption("--upload-blocked-ip", DefaultProfileOnly = true)]
    private static void ConfigureIp(SettingWriter changes, string value)
        => changes.ListItem(nameof(BlockedIpAddresses), value, item => item);

}

public sealed class PrivateMessageAccessSettings
{
    public List<string> BlockedUsernames { get; set; } = [];
    [SettingOption("--private-message-blocked-user", DefaultProfileOnly = true)]
    private static void ConfigureUser(SettingWriter changes, string value)
        => changes.ListItem(nameof(BlockedUsernames), value, item => item);

}
