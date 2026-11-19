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

    [SettingOption("--re", "--regex", ListSyntax = OptionListSyntax.SingleItem, Parser = nameof(ParseRegex))]
    public List<RegexRule>? Regex { get; set; }

    private static RegexRule ParseRegex(string raw, string flag)
    {
        string s = raw.Replace("\\;", "<<semicol>>");

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

        return new RegexRule(toReplace, replaceBy);
    }
}

public sealed record RegexRule(RegexFields Match, RegexFields Replace)
{
    public static implicit operator RegexRule((RegexFields Match, RegexFields Replace) rule) => new(rule.Match, rule.Replace);
}
