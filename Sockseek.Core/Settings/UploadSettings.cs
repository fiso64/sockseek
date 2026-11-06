namespace Sockseek.Core.Settings;

/// <summary>
/// Daemon-lifetime policy for uploads served to Soulseek peers.
/// </summary>
public sealed class UploadSettings
{
    [SettingOption("--upload-slots", DefaultProfileOnly = true)]
    public int Slots { get; set; } = 10;

    /// <summary>
    /// Null means unlimited. A configured value is an aggregate KiB/s limit.
    /// </summary>
    [SettingOption("--upload-speed-limit-kib", DefaultProfileOnly = true)]
    public int? SpeedLimitKiBPerSecond { get; set; }
}
