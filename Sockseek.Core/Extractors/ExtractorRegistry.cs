using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;

public interface IInputMatcher
{
    static virtual string? SourceName => null;
    static abstract bool InputMatches(string input);
}

public interface IExtractor
{
    Task<Job> ExtractAsync(string input, ExtractionSettings extraction, ExtractorContext? context = null);

    // Describes the input itself or an individual job produced from that input.
    JobSourceInfo? DescribeSource(ExtractJob source, Job item) => null;
}

public static class ExtractorRegistry
{
    private interface IEntry
    {
        InputType Type { get; }
        string SourceName { get; }
        bool InputMatches(string input);
        IExtractor Create(DownloadSettings settings);
    }

    private class Entry<T>(InputType type, Func<DownloadSettings, T> factory) : IEntry
        where T : IExtractor, IInputMatcher
    {
        public InputType Type { get; } = type;
        public string SourceName => T.SourceName ?? Type.ToString();
        public bool InputMatches(string input) => T.InputMatches(input);
        public IExtractor Create(DownloadSettings settings) => factory(settings);
    }

    // The order determines which extractor has priority when input matches multiple and no explicit inputType is provided
    private static readonly List<IEntry> extractors =
    [
        new Entry<CsvExtractor>(InputType.CSV, settings => new CsvExtractor(settings.Csv, settings.RuntimePathContext)),
        new Entry<YouTubeExtractor>(InputType.YouTube, settings => new YouTubeExtractor(settings.YouTube)),
        new Entry<SpotifyExtractor>(InputType.Spotify, settings => new SpotifyExtractor(settings.Spotify)),
        new Entry<BandcampExtractor>(InputType.Bandcamp, settings => new BandcampExtractor(settings.Bandcamp)),
        new Entry<MusicBrainzExtractor>(InputType.MusicBrainz, _ => new MusicBrainzExtractor()),
        new Entry<SoundCloudExtractor>(InputType.SoundCloud, settings => new SoundCloudExtractor(settings.SoundCloud)),
        new Entry<SoulseekExtractor>(InputType.SoulseekLink, _ => new SoulseekExtractor()),
        new Entry<StringExtractor>(InputType.String, _ => new StringExtractor()),
        new Entry<ListExtractor>(InputType.List, settings => new ListExtractor(settings.RuntimePathContext)), // never reached without inputType=List hint
    ];

    public static string SourceName(InputType inputType)
        => extractors.Find(entry => entry.Type == inputType)?.SourceName ?? inputType.ToString();

    private static IEntry? FindExtractor(string? input, InputType requestedType)
    {
        if (requestedType != InputType.None)
            return extractors.Find(entry => entry.Type == requestedType);
        return string.IsNullOrEmpty(input) ? null : extractors.Find(entry => entry.InputMatches(input));
    }

    public static bool TryResolveInputType(string? input, InputType inputType, out InputType resolved)
    {
        resolved = FindExtractor(input, inputType)?.Type ?? InputType.None;
        return resolved != InputType.None;
    }

    public static (InputType, IExtractor) CreateExtractor(string input, InputType inputType, DownloadSettings settings)
    {
        if (string.IsNullOrEmpty(input))
            throw new ArgumentException("Input string can not be null or empty.");
        var entry = FindExtractor(input, inputType) ?? throw new ArgumentException(inputType == InputType.None
            ? $"No matching extractor for input '{input}'" : $"No extractor for input type {inputType}");
        return (entry.Type, entry.Create(settings));
    }
}
