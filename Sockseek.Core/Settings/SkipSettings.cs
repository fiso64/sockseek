using Sockseek.Core;

namespace Sockseek.Core.Settings;

/// Controls when and how already-downloaded tracks are skipped.
public class SkipSettings
{
    [SettingOption("--se", "--skip-existing")]
    [SettingOption("--nse", "--no-skip-existing", FixedValue = "false")]
    public bool SkipExisting { get; set; } = true;

    [SettingOption("--snf", "--skip-not-found")]
    public bool SkipNotFound { get; set; }

    [SettingOption("--smod", "--skip-mode-output-dir", Parser = nameof(ParseOutputSkipMode))]
    public SkipMode SkipMode { get; set; } = SkipMode.Index;

    private static SkipMode ParseOutputSkipMode(string value, string flag) => ParseSkipMode(value, flag, allowIndex: true);

    [SettingOption("--smd", "--skip-music-dir")]
    public string? SkipMusicDir { get; set; }

    [SettingOption("--smmd", "--skip-mode-music-dir", Parser = nameof(ParseMusicSkipMode))]
    public SkipMode SkipModeMusicDir { get; set; } = SkipMode.Name;

    private static SkipMode ParseMusicSkipMode(string value, string flag) => ParseSkipMode(value, flag, allowIndex: false);

    private static SkipMode ParseSkipMode(string s, string flag, bool allowIndex)
    {
        return s.ToLower().Trim() switch
        {
            "name"  => SkipMode.Name,
            "tag"   => SkipMode.Tag,
            "index" when allowIndex => SkipMode.Index,
            _ => throw new Exception($"Input error: Invalid skip mode '{s}' for '{flag}'"),
        };
    }

    [SettingOption("--scc", "--skip-check-cond")]
    public bool SkipCheckCond { get; set; }

    [SettingOption("--scpc", "--skip-check-pref-cond")]
    public bool SkipCheckPrefCond { get; set; }
}
