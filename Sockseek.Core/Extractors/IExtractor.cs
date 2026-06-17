using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public interface IInputMatcher
    {
        static abstract bool InputMatches(string input);
    }

    public interface IExtractor
    {
        Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null);
    }

    public static class ExtractorRegistry
    {
        private interface IEntry
        {
            InputType Type { get; }
            bool InputMatches(string input);
            IExtractor Create(DownloadSettings dl);
        }

        private class Entry<T>(InputType type, Func<DownloadSettings, T> factory) : IEntry
            where T : IExtractor, IInputMatcher
        {
            public InputType Type { get; } = type;
            public bool InputMatches(string input) => T.InputMatches(input);
            public IExtractor Create(DownloadSettings dl) => factory(dl);
        }

        // The order determines which extractor has priority when input matches multiple and no explicit inputType is provided
        static readonly List<IEntry> extractors =
        [
            new Entry<CsvExtractor>        (InputType.CSV,         dl => new CsvExtractor(dl.Csv, dl.RuntimePathContext)),
            new Entry<YouTubeExtractor>    (InputType.YouTube,     dl => new YouTubeExtractor(dl.YouTube)),
            new Entry<SpotifyExtractor>    (InputType.Spotify,     dl => new SpotifyExtractor(dl.Spotify)),
            new Entry<BandcampExtractor>   (InputType.Bandcamp,    dl => new BandcampExtractor(dl.Bandcamp)),
            new Entry<MusicBrainzExtractor>(InputType.MusicBrainz, _ => new MusicBrainzExtractor()),
            new Entry<SoulseekExtractor>   (InputType.Soulseek,    _ => new SoulseekExtractor()),
            new Entry<StringExtractor>     (InputType.String,      _ => new StringExtractor()),
            new Entry<ListExtractor>       (InputType.List,        dl => new ListExtractor(dl.RuntimePathContext)), // never reached without inputType=List hint
        ];

        public static (InputType, IExtractor) GetMatchingExtractor(string input, InputType inputType, DownloadSettings dl)
        {
            if (string.IsNullOrEmpty(input))
                throw new ArgumentException("Input string can not be null or empty.");

            if (inputType != InputType.None)
            {
                var entry = extractors.Find(e => e.Type == inputType);
                if (entry != null)
                    return (inputType, entry.Create(dl));
                throw new ArgumentException($"No extractor for input type {inputType}");
            }

            foreach (var entry in extractors)
            {
                if (entry.InputMatches(input))
                    return (entry.Type, entry.Create(dl));
            }

            throw new ArgumentException($"No matching extractor for input '{input}'");
        }
    }
