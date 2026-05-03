using Sldl.Core.Settings;

namespace Sldl.Cli;

/// Parsed representation of a sldl.conf file.
public record ConfigFile(
    string Path,
    Dictionary<string, ProfileEntry> Profiles,
    bool HasAutoProfiles = false
);

/// One profile's typed Core patch plus optional CLI-only settings patch.
public record ProfileEntry(
    SettingsProfile Profile,
    CliSettingsPatch Cli,
    DaemonSettingsPatch Daemon,
    RemoteSettingsPatch Remote,
    List<string> Tokens)
{
    public string? Condition => Profile.Condition;

    public ProfileEntry(List<string> tokens, string? condition)
        : this(new SettingsProfile { Condition = condition }, new CliSettingsPatch(), new DaemonSettingsPatch(), new RemoteSettingsPatch(), tokens)
    {
    }
}

public sealed class CliSettingsPatch
{
    private readonly List<Action<CliSettings>> _operations = [];

    public bool HasOperations => _operations.Count > 0;

    public void Add(Action<CliSettings> operation) => _operations.Add(operation);

    public void ApplyTo(CliSettings settings)
    {
        foreach (var operation in _operations)
            operation(settings);
    }
}

public sealed class DaemonSettingsPatch
{
    private readonly List<Action<DaemonSettings>> _operations = [];

    public bool HasOperations => _operations.Count > 0;

    public void Add(Action<DaemonSettings> operation) => _operations.Add(operation);

    public void ApplyTo(DaemonSettings settings)
    {
        foreach (var operation in _operations)
            operation(settings);
    }
}

public sealed class RemoteSettingsPatch
{
    private readonly List<Action<RemoteSettings>> _operations = [];

    public bool HasOperations => _operations.Count > 0;

    public void Add(Action<RemoteSettings> operation) => _operations.Add(operation);

    public void ApplyTo(RemoteSettings settings)
    {
        foreach (var operation in _operations)
            operation(settings);
    }
}
