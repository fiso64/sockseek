namespace Sldl.Core.Models;

/// Holds the Title/Artist/Album regex strings used by the --regex option.
public class RegexFields
{
    public string Title  { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album  { get; init; } = "";
}
