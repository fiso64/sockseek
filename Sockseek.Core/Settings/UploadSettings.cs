namespace Sockseek.Core.Settings;

/// <summary>
/// Policy for uploads served to Soulseek peers. Limits can be updated at runtime.
/// </summary>
public sealed class UploadSettings
{
    [SettingOption("--upload-slots", DefaultProfileOnly = true)]
    [SettingReloadable]
    public int Slots { get; set; } = 10;

    /// <summary>
    /// Null means unlimited. A configured value is an aggregate KiB/s limit.
    /// </summary>
    [SettingOption("--upload-speed-limit-kib", DefaultProfileOnly = true)]
    [SettingReloadable]
    public int? SpeedLimitKiBPerSecond { get; set; }
}
