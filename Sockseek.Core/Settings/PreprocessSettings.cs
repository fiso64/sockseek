using Sockseek.Core.Models;

namespace Sockseek.Core.Settings;

/// Controls how query strings are transformed before searching.
public class PreprocessSettings
{
    [SettingOption("--rft", "--remove-ft")]
    public bool RemoveFt { get; set; }

    [SettingOption("--rb", "--remove-brackets")]
    public bool RemoveBrackets { get; set; }

    [SettingOption("--ea", "--extract-artist")]
    public bool ExtractArtist { get; set; }

    [SettingOption("--parse-title")]
    public string? ParseTitleTemplate { get; set; }

    public List<RegexRule>? Regex { get; set; }

    [SettingOption("--re", "--regex")]
    private static void ConfigureRegex(SettingWriter changes, string raw)
    {
        string s = raw.Replace("\\;", "<<semicol>>");
        bool append = s.TrimStart().StartsWith("+ ");
        if (append) s = s.TrimStart()[2..];

        string applyTo = "TAL";
        if (s.Length > 2 && s[1] == ':' && s[0] is 'T' or 'A' or 'L')
        {
            applyTo = s[0].ToString();
            s = s[2..];
        }

        var parts  = s.Split(';');
        string pat = parts[0].Replace("<<semicol>>", ";");
        string rep = (parts.Length > 1 ? parts[1] : "").Replace("<<semicol>>", ";");

        var toReplace = new RegexFields
        {
            Title  = applyTo.Contains('T') ? pat : "",
            Artist = applyTo.Contains('A') ? pat : "",
            Album  = applyTo.Contains('L') ? pat : "",
        };
        var replaceBy = new RegexFields
        {
            Title  = applyTo.Contains('T') ? rep : "",
            Artist = applyTo.Contains('A') ? rep : "",
            Album  = applyTo.Contains('L') ? rep : "",
        };

        var rule = new RegexRule(toReplace, replaceBy);
        if (append) changes.Append(nameof(Regex), new List<RegexRule> { rule });
        else changes.Set(nameof(Regex), new List<RegexRule> { rule });
    }
}

public sealed record RegexRule(RegexFields Match, RegexFields Replace)
{
    public static implicit operator RegexRule((RegexFields Match, RegexFields Replace) rule) => new(rule.Match, rule.Replace);
}
