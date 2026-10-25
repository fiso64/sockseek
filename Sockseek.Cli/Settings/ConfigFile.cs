using Sockseek.Core.Settings;

namespace Sockseek.Cli;

public record ConfigFile(string Path, Dictionary<string, ProfileEntry> Profiles, bool HasAutoProfiles = false)
{
    public string? ConfigDir => string.IsNullOrWhiteSpace(Path) || Path == "none" || !File.Exists(Path)
        ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
}

public record ProfileEntry(SettingsProfile Profile, SettingsPatch Cli, SettingsPatch Daemon, SettingsPatch Remote)
{
    public string? Condition => Profile.Condition;

    public ProfileEntry(List<string> tokens, string? condition) : this(ConfigManager.ParseProfileTokens(tokens))
    {
        Profile = Profile with { Condition = condition };
    }
}

public sealed record ParsedInvocation(ProfileEntry Profile, string? ConfigPath, string? ProfileName)
{
    public SettingsPatch? DownloadSettings => Profile.Profile.Download.HasOperations ? Profile.Profile.Download : null;
}

internal sealed class ProfileBuilder(string name)
{
    public string? Condition { get; set; }
    private readonly SettingsPatchBuilder engine = new();
    private readonly SettingsPatchBuilder download = new();
    private readonly SettingsPatchBuilder cli = new();
    private readonly SettingsPatchBuilder daemon = new();
    private readonly SettingsPatchBuilder remote = new();

    public SettingsPatchBuilder For(Type type)
        => type == typeof(EngineSettings) ? engine
        : type == typeof(DownloadSettings) ? download
        : type == typeof(CliSettings) ? cli
        : type == typeof(DaemonSettings) ? daemon
        : type == typeof(RemoteSettings) ? remote
        : throw new ArgumentException($"Unknown settings root {type.Name}");

    public ProfileEntry Build() => new(new SettingsProfile
    {
        Name = name,
        Condition = Condition,
        Engine = engine.Build(),
        Download = download.Build(),
    }, cli.Build(), daemon.Build(), remote.Build());
}
