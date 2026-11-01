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
        Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null);

        // TODO [v4]: Unlike GetTracks, this function is not self-explanatory.
        JobSourceInfo? DescribeSource(ExtractJob source, Job item) => null;
    }

    public static class ExtractorRegistry
    {
        private interface IEntry
        {
            InputType Type { get; }
            string SourceName { get; }
            bool InputMatches(string input);
            IExtractor Create(DownloadSettings dl);
        }

        private class Entry<T>(InputType type, Func<DownloadSettings, T> factory) : IEntry
            where T : IExtractor, IInputMatcher
        {
            public InputType Type { get; } = type;
            public string SourceName => T.SourceName ?? Type.ToString();
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

        public static string SourceName(InputType inputType)
            => extractors.Find(entry => entry.Type == inputType)?.SourceName ?? inputType.ToString();

        public static bool TryResolveInputType(string? input, InputType inputType, out InputType resolved)
        {
            if (inputType != InputType.None)
            {
                if (extractors.Any(entry => entry.Type == inputType))
                {
                    resolved = inputType;
                    return true;
                }

                resolved = InputType.None;
                return false;
            }

            if (!string.IsNullOrEmpty(input))
            {
                var match = extractors.Find(entry => entry.InputMatches(input));
                if (match != null)
                {
                    resolved = match.Type;
                    return true;
                }
            }

            resolved = InputType.None;
            return false;
        }

        public static (InputType, IExtractor) GetMatchingExtractor(string input, InputType inputType, DownloadSettings dl)
        {
            if (string.IsNullOrEmpty(input))
                throw new ArgumentException("Input string can not be null or empty.");

            if (!TryResolveInputType(input, inputType, out InputType resolved))
            {
                if (inputType != InputType.None)
                {
                    throw new ArgumentException($"No extractor for input type {inputType}");
                }

                throw new ArgumentException($"No matching extractor for input '{input}'");
            }

            var entry = extractors.Find(candidate => candidate.Type == resolved);
            if (entry == null)
                throw new ArgumentException($"No extractor for input type {inputType}");

            return (resolved, entry.Create(dl));
        }
    }
