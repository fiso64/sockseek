using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Sockseek.Core.Settings;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SettingIgnoreAttribute : Attribute;

/// The daemon's runtime owner supports applying this setting without restart.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SettingReloadableAttribute : Attribute;

public enum OptionListSyntax { None, SingleItem, CommaSeparated, PlainCommaSeparated }

public enum OptionValueKind { Auto, None, Boolean, Required }

/// CLI aliases precede the canonical configuration name. Declare them on the setting or compound option method.
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
    public bool DefaultProfileOnly { get; init; }
    public bool CredentialFile { get; init; }
    public OptionValueKind ValueKind { get; init; }
    public OptionListSyntax ListSyntax { get; init; }
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

    public bool IsPresenceOnly => declaration.Clear || declaration.FixedValue != null;
    public Type ConfigValueType => parser != null || declaration.CredentialFile ? typeof(string)
        : Nullable.GetUnderlyingType(valueType) ?? valueType;
    public bool IsList => declaration.ListSyntax != OptionListSyntax.None;
    public bool DefaultProfileOnly => declaration.DefaultProfileOnly;
    public Type RootType { get; }
    public IReadOnlyList<string> Names => declaration.Names;
    public OptionValueKind ValueKind { get; }

    public void Bind(SettingsPatchBuilder changes, string text, string flag, Func<string?>? configDirectory = null)
    {
        if (declaration.CredentialFile)
        {
            // An unused profile does not need its credentials. Read once when applied
            // or sent by the remote CLI, into the same field as an inline credential.
            changes.Set(member!.Path, FileCredential(text, configDirectory));
            return;
        }

        if (IsList)
        {
            string trimmed = text.TrimStart();
            bool append = trimmed.StartsWith("+ ", StringComparison.Ordinal);
            IEnumerable<string> items = append ? [trimmed[2..]] : declaration.ListSyntax switch
            {
                OptionListSyntax.CommaSeparated => SettingParser.CommaSeparatedItems(text),
                OptionListSyntax.PlainCommaSeparated => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                _ => [text],
            };
            BindList(changes, items, flag, append);
            return;
        }

        text = declaration.FixedValue ?? text;
        var value = declaration.Clear ? null : parser == null ? SettingParser.Parse(text, valueType, flag)
            : Invoke(parser, [text, flag]);
        if (declaration.Negate)
            value = value is bool boolean ? !boolean : -(int)value!;

        if (member != null) BindMember(changes, value, flag, SettingOperation.Set);
        else Invoke(method!, [new SettingWriter(changes, prefix, flag), value]);
    }

    private static Lazy<string> FileCredential(string path, Func<string?>? configDirectory)
        => new(() => Utils.ReadSecretFile(Utils.ExpandVariables(path, new(ConfigDir: configDirectory?.Invoke()))),
            LazyThreadSafetyMode.PublicationOnly);

    /// Bind literal items as one edit, including when the profile is applied repeatedly.
    public void BindList(SettingsPatchBuilder changes, IEnumerable<string> items, string flag, bool append)
    {
        if (!IsList) throw new ArgumentException($"Input error: Option '{flag}' does not accept a list.");

        // Array-backed settings are string arrays; typed lists have an item parser.
        object? ParseItem(string text)
        {
            if (parser != null) return Invoke(parser, [text, flag]);
            string item = text.Trim();
            if (item.Length == 0)
                throw new ArgumentException($"Input error: Option '{flag}' requires a non-empty value.");
            return item;
        }

        IList values;
        if (valueType == typeof(string[]))
            values = items.Select(item => (string)ParseItem(item)!).ToArray();
        else
        {
            values = (IList)Activator.CreateInstance(valueType)!;
            foreach (string item in items) values.Add(ParseItem(item));
        }
        BindMember(changes, values, flag, append ? SettingOperation.Append : SettingOperation.Set);
    }

    private void BindMember(SettingsPatchBuilder changes, object? value, string flag, SettingOperation operation)
    {
        member!.Validate(value, flag);
        if (!declaration.AllowMultiple && changes.Contains(member.Path))
            throw new Exception($"Input error: Option '{flag}' can only be supplied once");
        changes.Add(new(member.Path, value, operation));
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
        if (type == typeof(bool) && bool.TryParse(text, out var boolean)) return boolean;
        if (type == typeof(int) && int.TryParse(text.Replace("_", ""), out var integer)) return integer;
        if (type == typeof(double) && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)) return number;
        if (type.IsEnum && Enum.TryParse(type, text, ignoreCase: true, out var value)) return value;
        string expected = type == typeof(bool) ? "boolean" : type == typeof(int) ? "integer" : type == typeof(double) ? "numeric" : type.Name;
        throw new Exception($"Input error: Option '{flag}' requires a {expected} parameter, got '{text}'");
    }

    // A single comma separates entries. A pair within an entry spells one literal comma.
    internal static List<string> CommaSeparatedItems(string text)
    {
        var items = new List<string>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != ',') continue;
            if (i + 1 < text.Length && text[i + 1] == ',')
            {
                i++;
                continue;
            }

            string item = text[start..i].Trim();
            if (item.Length > 0) items.Add(item.Replace(",,", ","));
            start = i + 1;
        }
        return items;
    }

    public static int Integer(string text, string flag) => (int)Parse(text, typeof(int), flag)!;
}
