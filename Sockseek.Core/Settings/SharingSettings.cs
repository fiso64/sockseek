using Sockseek.Core.Sharing;

namespace Sockseek.Core.Settings;

/// <summary>
/// Settings for the local public share catalog; each scan captures one snapshot.
/// </summary>
public sealed class SharingSettings
{
    [SettingReloadable]
    public List<ShareRootSettings> Roots { get; set; } = [];

    [SettingReloadable]
    public List<string> ExcludedDirectories { get; set; } = [];

    [SettingReloadable]
    public List<string> Filters { get; set; } = [];

    [SettingOption("--share-scan-on-start", DefaultProfileOnly = true)]
    public bool ScanOnStart { get; set; } = true;

    [SettingOption("--share-rescan-interval", Parser = nameof(ParseRescanInterval), DefaultProfileOnly = true)]
    public TimeSpan? RescanInterval { get; set; }
    [SettingOption("--share", DefaultProfileOnly = true)]
    private static void ConfigureRoot(SettingWriter changes, string value)
        => changes.ListItem(nameof(Roots), value, ShareRootParser.Parse);

    [SettingOption("--share-exclude", DefaultProfileOnly = true)]
    private static void ConfigureExclude(SettingWriter changes, string value)
        => changes.ListItem(nameof(ExcludedDirectories), value, item => item);

    [SettingOption("--share-filter", DefaultProfileOnly = true)]
    private static void ConfigureFilter(SettingWriter changes, string value)
        => changes.ListItem(nameof(Filters), value, item => item);

    private static TimeSpan? ParseRescanInterval(string value, string flag)
    {
        value = value.Trim();
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TimeSpan.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        if (value.Length > 1
            && double.TryParse(
                value[..^1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double amount))
        {
            return char.ToLowerInvariant(value[^1]) switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw InvalidDuration(),
            };
        }

        throw InvalidDuration();

        ArgumentException InvalidDuration()
            => new(
                $"Input error: Option '{flag}' requires a duration such as " +
                "'00:30:00', '30m', '12h', '7d', or 'off'.");
    }

}

/// <summary>
/// One configured local root and the public alias under which peers see it.
/// </summary>
public sealed class ShareRootSettings
{
    public required string LocalPath { get; set; }

    public string? Alias { get; set; }

    /// <summary>
    /// Populated by settings normalization from <see cref="Alias"/> or the final
    /// local path segment.
    /// </summary>
    public string EffectiveAlias { get; set; } = "";
}
