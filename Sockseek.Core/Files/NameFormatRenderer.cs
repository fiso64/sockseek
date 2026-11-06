using System.Text.RegularExpressions;

namespace Sockseek.Core.Services;

public enum NameFormatValueKind
{
    Component,
    Path,
    Raw,
}

public readonly record struct NameFormatVariableValue(string Value, NameFormatValueKind Kind);

public enum NameFormatVariableApplicability
{
    Shared,
    Music,
}

public enum NameFormatEvaluationPhase
{
    Placement,
    Completion,
    MusicFinalization,
    OnComplete,
}

public sealed record NameFormatVariableDescriptor(
    string Name,
    NameFormatVariableApplicability Applicability,
    NameFormatEvaluationPhase Phase);

public interface INameFormatVariableProvider
{
    IReadOnlyCollection<string> SupportedVariables { get; }
    IReadOnlyCollection<NameFormatVariableDescriptor> VariableDescriptors { get; }
    bool TryResolve(string name, out NameFormatVariableValue value);
}

public sealed class UnsupportedNameFormatVariableException(string variable)
    : ArgumentException($"Name-format variable '{{{variable}}}' is not supported for this download type.")
{
    public string Variable { get; } = variable;
}

/// <summary>Shared conditional/fallback name-format grammar.</summary>
public static partial class NameFormatRenderer
{
    public static void ValidateVariables(
        string format,
        IReadOnlyCollection<string> supportedVariables)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(supportedVariables);
        var supported = supportedVariables.ToHashSet(StringComparer.Ordinal);

        foreach (Match match in VariableRegex().Matches(format))
        {
            string inner = match.Groups[1].Value[1..^1];
            foreach (string option in inner.Split('|'))
            {
                foreach (string name in ParenRegex().Split(option)
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    if (!supported.Contains(name))
                        throw new UnsupportedNameFormatVariableException(name);
                }
            }
        }
    }

    public static string Render(
        string format,
        string invalidReplacement,
        INameFormatVariableProvider provider,
        bool rejectUnsupportedVariables)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(invalidReplacement);
        ArgumentNullException.ThrowIfNull(provider);

        string rendered = format;
        var matches = VariableRegex().Matches(rendered);
        while (matches.Count > 0)
        {
            foreach (Match match in matches)
            {
                string inner = match.Groups[1].Value[1..^1];
                string[] options = inner.Split('|');
                string? chosen = null;

                foreach (string option in options)
                {
                    string[] names = ParenRegex().Split(option)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToArray();
                    if (names.All(name => TryClean(
                        name,
                        invalidReplacement,
                        provider,
                        rejectUnsupportedVariables: false,
                        out string value) && value.Length > 0))
                    {
                        chosen = option;
                        break;
                    }
                }

                chosen ??= options[^1];
                chosen = ConditionalChoiceRegex().Replace(chosen, token =>
                {
                    if (token.Value.StartsWith('(') && token.Value.EndsWith(')'))
                        return token.Value[1..^1].ReplaceInvalidChars(invalidReplacement, removeSlash: false);

                    TryClean(
                        token.Value,
                        invalidReplacement,
                        provider,
                        rejectUnsupportedVariables,
                        out string value);
                    return value;
                });

                string original = match.Groups[1].Value;
                original = original.StartsWith("{{", StringComparison.Ordinal) ? original[1..] : original;
                rendered = rendered.Replace(original, EscapeLiteralBraces(chosen));
            }

            matches = VariableRegex().Matches(rendered);
        }

        if (rendered == format)
            return format;

        rendered = UnescapeLiteralBraces(rendered);
        char separator = Path.DirectorySeparatorChar;
        rendered = rendered.Replace('/', separator).Replace('\\', separator);
        var components = rendered.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(separator, components.Select(component =>
            component.ReplaceInvalidChars(invalidReplacement).Trim(' ', '.')));
    }

    private static bool TryClean(
        string name,
        string replacement,
        INameFormatVariableProvider provider,
        bool rejectUnsupportedVariables,
        out string result)
    {
        if (!provider.TryResolve(name, out var value))
        {
            if (rejectUnsupportedVariables)
                throw new UnsupportedNameFormatVariableException(name);
            result = name.ReplaceInvalidChars(replacement);
            return false;
        }

        result = value.Kind switch
        {
            NameFormatValueKind.Raw => value.Value,
            NameFormatValueKind.Path => value.Value.CleanPath(replacement),
            _ => value.Value.ReplaceInvalidChars(replacement),
        };
        return true;
    }

    private static string EscapeLiteralBraces(string value)
        => value.Replace("{", "\uE000").Replace("}", "\uE001");

    private static string UnescapeLiteralBraces(string value)
        => value.Replace("\uE000", "{").Replace("\uE001", "}");

    [GeneratedRegex(@"(\{(?:\{??[^\{]*?\}))")]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"\([^\)]*\)")]
    private static partial Regex ParenRegex();

    [GeneratedRegex(@"\([^()]*\)|[^()]+")]
    private static partial Regex ConditionalChoiceRegex();
}
