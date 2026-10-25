using System.Text.Json.Serialization;

namespace Sockseek.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<SettingOperation>))]
public enum SettingOperation { Set, Append, AppendDistinct }

public sealed record SettingChange(string Path, object? Value, SettingOperation Operation = SettingOperation.Set);

/// A sparse, ordered description of supplied settings. An explicit Set with null clears a value.
public sealed class SettingsPatch
{
    private sealed record Compiled(Type Type, Action<object>[] Steps);
    private Compiled? compiled;

    public static SettingsPatch Empty { get; } = new([]);

    [JsonConstructor]
    public SettingsPatch(IReadOnlyList<SettingChange> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Operations = Array.AsReadOnly(operations.ToArray());
    }

    public IReadOnlyList<SettingChange> Operations { get; }

    [JsonIgnore]
    public bool HasOperations => Operations.Count != 0;

    public void ApplyTo<T>(T settings) where T : class
    {
        if (!HasOperations) return;
        var steps = Prepare(typeof(T));
        foreach (var step in steps)
            step(settings);
    }

    public void Validate<T>() where T : class => Prepare(typeof(T));

    private Action<object>[] Prepare(Type type)
    {
        var prepared = Volatile.Read(ref compiled);
        if (prepared?.Type == type) return prepared.Steps;
        var schema = SettingsSchema.For(type);
        var steps = new Action<object>[Operations.Count];
        for (int i = 0; i < steps.Length; i++)
            steps[i] = schema.Compile(Operations[i]);
        Interlocked.CompareExchange(ref compiled, new(type, steps), null);
        return steps;
    }

    public static SettingsPatch? Difference<T>(T before, T after) where T : class
    {
        var changes = new List<SettingChange>();
        foreach (var member in SettingsSchema.For(typeof(T)).Leaves)
        {
            var oldValue = member.Get(before);
            var newValue = member.Get(after);
            if (!SettingsSchema.ValuesEqual(oldValue, newValue))
                changes.Add(new(member.Path, SettingsSchema.CopyValue(newValue)));
        }
        return changes.Count == 0 ? null : new(changes);
    }
}

public sealed class SettingsPatchBuilder
{
    private readonly List<SettingChange> operations = [];

    internal bool Contains(string path) => operations.Exists(change => change.Path == path);

    public void Add(SettingChange change) => operations.Add(change);
    public void Set(string path, object? value) => Add(new(path, value));
    public SettingsPatch Build() => operations.Count == 0 ? SettingsPatch.Empty : new(operations);
}
