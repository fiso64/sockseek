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
    [SettingOption("--upload-blocked-user", ListSyntax = OptionListSyntax.CommaSeparated, DefaultProfileOnly = true)]
    [SettingReloadable]
    public List<string> BlockedUsernames { get; set; } = [];

    [SettingOption("--upload-blocked-ip", ListSyntax = OptionListSyntax.CommaSeparated, DefaultProfileOnly = true)]
    [SettingReloadable]
    public List<string> BlockedIpAddresses { get; set; } = [];
}

public sealed class PrivateMessageAccessSettings
{
    [SettingOption("--private-message-blocked-user", ListSyntax = OptionListSyntax.CommaSeparated, DefaultProfileOnly = true)]
    [SettingReloadable]
    public List<string> BlockedUsernames { get; set; } = [];
}
