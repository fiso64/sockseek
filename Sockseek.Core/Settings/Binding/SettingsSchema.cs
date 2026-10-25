using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sockseek.Core.Settings;

/// Shared metadata for configuration boundaries. Normal engine code keeps using ordinary typed properties.
public sealed class SettingsSchema
{
    private static readonly ConcurrentDictionary<Type, SettingsSchema> schemas = new();
    private sealed record TypeMembers(MemberInfo[] All, MemberInfo[] References);
    private static readonly ConcurrentDictionary<Type, TypeMembers> dataMembers = new();
    private static readonly Func<object, object> shallowCopy = typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<object, object>>();
    // Local CLI binding does not need to initialize the JSON serializer.
    private static class JsonValues
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter() },
        };
    }

    private readonly Dictionary<string, SettingMember> members = new(StringComparer.Ordinal);
    private readonly List<(Type Type, string Prefix)> groups = [];
    private readonly List<SettingMember> leaves = [];
    private readonly Type rootType;
    private IReadOnlyList<SettingOption>? options;

    private SettingsSchema(Type type)
    {
        rootType = type;
        Visit(type, "", []);
    }

    public static SettingsSchema For(Type type) => schemas.GetOrAdd(type, static type => new(type));
    public IReadOnlyList<SettingMember> Leaves => leaves;

    public IReadOnlyList<SettingOption> Options
    {
        get
        {
            if (options != null) return options;
            var result = new List<SettingOption>();
            foreach (var (type, prefix) in groups)
            {
                foreach (var member in DataMembers(type))
                foreach (SettingOptionAttribute option in member.GetCustomAttributes(typeof(SettingOptionAttribute), inherit: false))
                {
                    string path = prefix + member.Name + (option.Member == null ? "" : "." + option.Member);
                    result.Add(new(rootType, prefix, option, members[path], null, type));
                }
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                foreach (SettingOptionAttribute option in method.GetCustomAttributes(typeof(SettingOptionAttribute), inherit: false))
                {
                    if (method.GetParameters() is not { Length: 2 } parameters
                        || parameters[0].ParameterType != typeof(SettingWriter))
                        throw new InvalidOperationException($"Invalid option method {type.Name}.{method.Name}");
                    result.Add(new(rootType, prefix, option, null, method, type));
                }
            }
            return options = result;
        }
    }

    private void Visit(Type type, string prefix, MemberInfo[] parents)
    {
        groups.Add((type, prefix));
        foreach (var member in DataMembers(type))
        {
            if (member.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            var entry = new SettingMember(prefix + member.Name, parents, member);
            members.Add(entry.Path, entry);
            if (IsGroup(entry.ValueType))
                Visit(entry.ValueType, entry.Path + ".", [.. parents, member]);
            else
                leaves.Add(entry);
        }
    }

    private static bool IsGroup(Type type) => type.IsClass && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type) && DataMembers(type).Length != 0;

    internal Action<object> Compile(SettingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!members.TryGetValue(change.Path, out var member))
            throw new ArgumentException($"Unknown setting '{change.Path}'.");
        var value = ConvertValue(change.Value, member.ValueType);
        if (value == null && change.Operation is SettingOperation.Append or SettingOperation.AppendDistinct)
            throw new ArgumentException($"An append operation for '{change.Path}' requires a collection.");
        member.Validate(value);
        if (value != null && IsGroup(member.ValueType))
            foreach (var child in For(member.ValueType).Leaves)
                child.Validate(child.Get(value));
        // Prepared patches own their values; applying one cannot change a later application.
        value = CopyValue(value);
        return change.Operation switch
        {
            SettingOperation.Set => root => member.Set(root, CopyValue(value)),
            SettingOperation.Append or SettingOperation.AppendDistinct when typeof(IList).IsAssignableFrom(member.ValueType)
                => root => member.Set(root, Append(member.Get(root) as IList, value as IList, member.ValueType,
                    change.Operation == SettingOperation.AppendDistinct)),
            _ => throw new ArgumentException($"Operation '{change.Operation}' is not supported for '{change.Path}'."),
        };
    }

    private static object? ConvertValue(object? value, Type type)
    {
        if (value is JsonElement element)
        {
            try { value = element.Deserialize(type, JsonValues.Options); }
            catch (JsonException ex) { throw new ArgumentException($"Invalid value for setting of type {type.Name}: {ex.Message}", ex); }
        }
        if (value == null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) == null || IsGroup(type))
                throw new ArgumentException($"A setting of type {type.Name} cannot be null.");
        }
        else if (!type.IsInstanceOfType(value))
            throw new ArgumentException($"Expected setting value of type {type.Name}, got {value.GetType().Name}.");
        return value;
    }

    private static IList Append(IList? current, IList? added, Type type, bool distinct)
    {
        if (added == null) throw new ArgumentException("An append operation requires a collection.");
        var values = new List<object?>();
        if (current != null) foreach (var value in current) values.Add(CopyValue(value));
        foreach (var value in added)
            if (!distinct || !values.Contains(value)) values.Add(CopyValue(value));
        if (type.IsArray)
        {
            var result = Array.CreateInstance(type.GetElementType()!, values.Count);
            for (int i = 0; i < values.Count; i++) result.SetValue(values[i], i);
            return result;
        }
        var list = (IList)Activator.CreateInstance(type)!;
        foreach (var value in values) list.Add(value);
        return list;
    }

    public static T Copy<T>(T source) where T : class => (T)CopyValue(source)!;

    internal static object? CopyValue(object? value)
    {
        if (value == null || value is string || value.GetType().IsValueType) return value;
        if (value is HashSet<string> set) return new HashSet<string>(set, set.Comparer);
        if (value is Array array)
        {
            if (array.Length == 0) return array;
            var result = (Array)array.Clone();
            var elementType = array.GetType().GetElementType()!;
            if (!elementType.IsValueType && elementType != typeof(string))
                for (int i = 0; i < result.Length; i++) result.SetValue(CopyValue(result.GetValue(i)), i);
            return result;
        }
        if (value is IList list) return Append(null, list, value.GetType(), distinct: false);
        if (value is IEnumerable)
            throw new NotSupportedException($"Unsupported settings collection: {value.GetType().Name}. Use arrays or lists.");
        var copy = shallowCopy(value);
        foreach (var member in Members(value.GetType()).References)
            Set(member, copy, CopyValue(Get(member, value)));
        return copy;
    }

    internal static bool ValuesEqual(object? left, object? right)
    {
        if (Equals(left, right)) return true;
        if (left == null || right == null || left.GetType() != right.GetType()) return false;
        if (left is string || left.GetType().IsValueType) return false;
        if (left is IEnumerable a && right is IEnumerable b)
            return a.Cast<object?>().SequenceEqual(b.Cast<object?>(), ValueComparer.Instance);
        return DataMembers(left.GetType()).All(member => ValuesEqual(Get(member, left), Get(member, right)));
    }

    private sealed class ValueComparer : IEqualityComparer<object?>
    {
        public static ValueComparer Instance { get; } = new();
        public new bool Equals(object? left, object? right) => ValuesEqual(left, right);
        public int GetHashCode(object? value) => throw new NotSupportedException();
    }

    internal static MemberInfo[] DataMembers(Type type) => Members(type).All;

    private static TypeMembers Members(Type type) => dataMembers.GetOrAdd(type, static type =>
    {
        var all = new List<MemberInfo>();
        var references = new List<MemberInfo>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                all.Add(property);
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!field.IsInitOnly)
                all.Add(field);
        foreach (var member in all)
            if (!MemberType(member).IsValueType && MemberType(member) != typeof(string))
                references.Add(member);
        return new(all.ToArray(), references.ToArray());
    });

    internal static Type MemberType(MemberInfo member) => member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
    internal static object? Get(MemberInfo member, object target) => member is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)member).GetValue(target);
    internal static void Set(MemberInfo member, object target, object? value)
    {
        if (member is PropertyInfo p) p.SetValue(target, value);
        else ((FieldInfo)member).SetValue(target, value);
    }
    internal static object Parent(object root, MemberInfo[] parents)
    {
        foreach (var parent in parents) root = Get(parent, root)!;
        return root;
    }
}

public sealed class SettingMember
{
    private readonly MemberInfo[] parents;
    private readonly MemberInfo member;
    private ValidationAttribute[]? validators;
    private MethodInfo? validate;

    internal SettingMember(string path, MemberInfo[] parents, MemberInfo member)
    {
        Path = path;
        this.parents = parents;
        this.member = member;
        ValueType = SettingsSchema.MemberType(member);
    }

    public string Path { get; }
    public Type ValueType { get; }
    public object? Get(object root) => SettingsSchema.Get(member, SettingsSchema.Parent(root, parents));
    internal void Set(object root, object? value) => SettingsSchema.Set(member, SettingsSchema.Parent(root, parents), value);

    internal void Validate(object? value, string? argument = null)
    {
        // Most declared settings are never supplied in an invocation.
        // Discover their validation only when a value is actually bound.
        var rules = Volatile.Read(ref validators);
        if (rules == null)
        {
            if (member.GetCustomAttribute<SettingValidationAttribute>() is { } custom)
                validate = custom.ValidatorType.GetMethod(custom.Method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Missing validation method {custom.Method}");
            rules = member.GetCustomAttributes<ValidationAttribute>().ToArray();
            Volatile.Write(ref validators, rules);
        }
        if (value == null)
        {
            var nullability = new NullabilityInfoContext();
            var state = member is PropertyInfo property ? nullability.Create(property).WriteState
                : nullability.Create((FieldInfo)member).WriteState;
            if (state == NullabilityState.NotNull)
                throw new ArgumentException($"Setting '{Path}' cannot be null.");
        }
        foreach (var validator in rules)
        {
            if (validator.IsValid(value)) continue;
            var message = validator.FormatErrorMessage(argument ?? Path);
            if (argument != null) throw new Exception("Input error: " + message);
            throw new ArgumentException(message);
        }
        if (validate != null) SettingOption.Invoke(validate, [value]);
    }
}
