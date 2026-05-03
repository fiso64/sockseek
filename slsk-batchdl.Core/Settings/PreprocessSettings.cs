using Sldl.Core.Models;

namespace Sldl.Core.Settings;

/// Controls how query strings are transformed before searching.
///
/// Regex is NOT decorated with [Option] — it is populated via special handling
/// in the binder for --regex (which supports a target prefix T:/A:/L: and append mode).
public class PreprocessSettings
{
    public bool RemoveFt { get; set; }

    public bool RemoveBrackets { get; set; }

    public bool ExtractArtist { get; set; }

    public string? ParseTitleTemplate { get; set; }

    public List<(RegexFields, RegexFields)>? Regex { get; set; }
}
