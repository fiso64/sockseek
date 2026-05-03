namespace Sldl.Core.Settings;

/// Controls Bandcamp extraction behaviour.
public class BandcampSettings
{
    /// When set, read the page HTML from this local file instead of fetching it from the URL.
    public string? HtmlFromFile { get; set; }
}
