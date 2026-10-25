using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Sockseek.Core.Settings;

public enum OptionValueKind { Auto, None, Boolean, Required }

/// CLI names live beside the setting/default, or on a method for a compound option.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Method, AllowMultiple = true)]
public sealed class SettingOptionAttribute(params string[] names) : Attribute
{
    public string[] Names { get; } = names;
    public string? Member { get; init; }
    public string? Parser { get; init; }
    public string? FixedValue { get; init; }
    public bool Clear { get; init; }
    public bool Negate { get; init; }
    public bool AllowMultiple { get; init; } = true;
    public OptionValueKind ValueKind { get; init; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SettingValidationAttribute(Type validatorType, string method) : Attribute
{
    public Type ValidatorType { get; } = validatorType;
    public string Method { get; } = method;
}

public sealed class SettingOption
{
    private readonly SettingOptionAttribute declaration;
    private readonly SettingMember? member;
    private readonly MethodInfo? method;
    private readonly string prefix;
    private readonly Type valueType;
    private readonly MethodInfo? parser;

    internal SettingOption(Type rootType, string prefix, SettingOptionAttribute declaration,
        SettingMember? member, MethodInfo? method, Type declaringType)
    {
        RootType = rootType;
        this.prefix = prefix;
        this.declaration = declaration;
        this.member = member;
        this.method = method;
        valueType = member?.ValueType ?? method!.GetParameters()[1].ParameterType;
        parser = declaration.Parser == null ? null : declaringType.GetMethod(declaration.Parser,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing option parser {declaringType.Name}.{declaration.Parser}");
        ValueKind = declaration.FixedValue != null || declaration.Clear ? OptionValueKind.None
            : declaration.ValueKind != OptionValueKind.Auto ? declaration.ValueKind
            : (Nullable.GetUnderlyingType(valueType) ?? valueType) == typeof(bool) ? OptionValueKind.Boolean : OptionValueKind.Required;
    }

    public Type RootType { get; }
    public IReadOnlyList<string> Names => declaration.Names;
    public OptionValueKind ValueKind { get; }

    public void Bind(SettingsPatchBuilder changes, string text, string flag)
    {
        text = declaration.FixedValue ?? text;
        var value = declaration.Clear ? null : parser == null ? SettingParser.Parse(text, valueType, flag)
            : Invoke(parser, [text, flag]);
        if (declaration.Negate)
            value = value is bool boolean ? !boolean : -(int)value!;

        if (member != null)
        {
            member.Validate(value, flag);
            if (!declaration.AllowMultiple && changes.Contains(member.Path))
                throw new Exception($"Input error: Option '{flag}' can only be supplied once");
            changes.Set(member.Path, value);
        }
        else
        {
            Invoke(method!, [new SettingWriter(changes, prefix, flag), value]);
        }
    }

    internal static object? Invoke(MethodInfo method, object?[] arguments)
    {
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// Compound option parsers record ordinary changes using names relative to their settings group.
public sealed class SettingWriter(SettingsPatchBuilder changes, string prefix, string flag)
{
    public string Flag { get; } = flag;
    public void Set(string member, object? value) => changes.Set(prefix + member, value);
    public void Append(string member, object value, bool distinct = false)
        => changes.Add(new(prefix + member, value, distinct ? SettingOperation.AppendDistinct : SettingOperation.Append));

    public void Merge(string member, object supplied)
    {
        foreach (var field in SettingsSchema.DataMembers(supplied.GetType()))
        {
            var value = SettingsSchema.Get(field, supplied);
            if (value != null)
                Set(member + "." + field.Name, value);
        }
    }
}

public static class SettingParser
{
    public static object? Parse(string text, Type type, string flag)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return text;
        if (type == typeof(string[])) return text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (type == typeof(bool) && bool.TryParse(text, out var boolean)) return boolean;
        if (type == typeof(int) && int.TryParse(text.Replace("_", ""), out var integer)) return integer;
        if (type == typeof(double) && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)) return number;
        if (type.IsEnum && Enum.TryParse(type, text, ignoreCase: true, out var value)) return value;
        string expected = type == typeof(bool) ? "boolean" : type == typeof(int) ? "integer" : type == typeof(double) ? "numeric" : type.Name;
        throw new Exception($"Input error: Option '{flag}' requires a {expected} parameter, got '{text}'");
    }

    public static int Integer(string text, string flag) => (int)Parse(text, typeof(int), flag)!;
}
