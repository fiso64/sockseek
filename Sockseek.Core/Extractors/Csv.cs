using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using System.Text.RegularExpressions;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public partial class CsvExtractor : IExtractor, IInputMatcher
    {
        [GeneratedRegex(@"\(.*?\)")]
        private static partial Regex ParenthesesRegex();

        [GeneratedRegex("[a-zA-Z]")]
        private static partial Regex LettersRegex();

        [GeneratedRegex(@"\W+")]
        private static partial Regex NonWordRegex();

        private readonly CsvSettings _csv;
        private readonly PathVariableContext pathVariables;

        public CsvExtractor(CsvSettings csv)
            : this(csv, PathVariableContext.Empty)
        {
        }

        public CsvExtractor(CsvSettings csv, PathVariableContext pathVariables)
        {
            _csv = csv;
            this.pathVariables = pathVariables;
        }

        string? csvFilePath = null;
        int csvColumnCount = -1;
        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return !input.IsInternetUrl() && input.EndsWith(".csv");
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            context ??= ExtractorContext.None;
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            csvFilePath = Utils.ExpandVariables(input, pathVariables);

            if (!File.Exists(csvFilePath))
                throw new FileNotFoundException($"CSV file '{csvFilePath}' not found");

            var rows = await ParseCsvRows(csvFilePath, _csv.ArtistCol, _csv.TitleCol, _csv.LengthCol,
                _csv.AlbumCol, _csv.DescCol, _csv.YtIdCol, _csv.TrackCountCol, _csv.TimeUnit, _csv.YtParse, context.Log);

            if (reverse)
                rows.Reverse();

            var csvName = Path.GetFileNameWithoutExtension(csvFilePath);
            var list = new JobList { ItemName = csvName, EnablesIndexByDefault = true };
            list.Jobs.AddRange(BuildJobs(rows.Skip(offset).Take(maxTracks)));
            return list;
        }

        private static IEnumerable<Job> BuildJobs(IEnumerable<object> rows)
            => rows.OfType<Job>();

        // Returns a list of rows — either SongJob (song row) or AlbumJob (album row).
        async Task<List<object>> ParseCsvRows(string path, string artistCol = "", string trackCol = "",
            string lengthCol = "", string albumCol = "", string descCol = "", string ytIdCol = "", string trackCountCol = "",
            string timeUnit = "s", bool ytParse = false, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            var rows = new List<object>();
            using var sr = new StreamReader(path, System.Text.Encoding.UTF8);
            var parser = new SmallestCSV.SmallestCSVParser(sr);

            int index = 1;
            var header = parser.ReadNextRow();
            while (header == null || header.Count == 0 || !header.Any(t => t.Trim().Length > 0))
            {
                index++;
                header = parser.ReadNextRow();
            }

            string[] cols = [artistCol, albumCol, trackCol, lengthCol, descCol, ytIdCol, trackCountCol];
            string[][] aliases = [
                ["artist", "artistname", "artists", "artistnames"],
                ["album", "albumname", "albumtitle"],
                ["title", "song", "tracktitle", "trackname", "songname", "track"],
                ["length", "duration", "tracklength", "trackduration", "songlength", "songduration"],
                ["description", "youtubedescription"],
                ["url", "trackurl", "uri", "id", "youtubeid"],
                ["trackcount", "albumtrackcount"]
            ];

            string usingColumns = "";
            for (int i = 0; i < cols.Length; i++)
            {
                if (string.IsNullOrEmpty(cols[i]))
                {
                    string? res = header.FirstOrDefault(h => ParenthesesRegex().Replace(h, "").Replace(" ", "").EqualsAny(aliases[i], StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(res))
                    {
                        cols[i] = res;
                        usingColumns += $"{aliases[i][0]}:\"{res}\", ";
                    }
                }
                else
                {
                    if (header.IndexOf(cols[i]) == -1)
                        throw new Exception($"Column \"{cols[i]}\" not found in CSV file");
                    usingColumns += $"{aliases[i][0]}:\"{cols[i]}\", ";
                }
            }

            int foundCount = cols.Count(col => col.Length > 0);
            if (!string.IsNullOrEmpty(usingColumns))
                log.Info($"Using columns: {usingColumns.TrimEnd(' ', ',')}.");
            else if (foundCount == 0)
                throw new Exception("No columns specified and couldn't determine automatically");

            int[] indices = cols.Select(col => col.Length == 0 ? -1 : header.IndexOf(col)).ToArray();
            int artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex, trackCountIndex;
            (artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex, trackCountIndex)
                = (indices[0], indices[1], indices[2], indices[3], indices[4], indices[5], indices[6]);

            while (true)
            {
                index++;
                List<string>? values = null;
                try
                {
                    values = parser.ReadNextRow();
                }
                catch (Exception e)
                {
                    throw new InvalidDataException($"Error parsing CSV at line {index}: {e.Message}", e);
                }

                if (values == null) break;
                if (!values.Any(t => t.Trim().Length > 0)) continue;
                while (values.Count < foundCount) values.Add("");

                if (csvColumnCount == -1)
                    csvColumnCount = values.Count;

                string artist = "", title = "", album = "", uri = "", desc = "";
                int length = -1;
                int minAlbumTrackCount = -1, maxAlbumTrackCount = -1;

                if (artistIndex     >= 0) artist = values[artistIndex];
                if (trackIndex      >= 0) title  = values[trackIndex];
                if (albumIndex      >= 0) album  = values[albumIndex];
                if (descIndex       >= 0) desc   = values[descIndex];
                if (ytIdIndex       >= 0) uri    = values[ytIdIndex];
                if (trackCountIndex >= 0)
                {
                    string a = values[trackCountIndex].Trim();
                    if (a == "-1")
                    {
                        minAlbumTrackCount = -1;
                        maxAlbumTrackCount = -1;
                    }
                    else if (a.Last() == '-' && int.TryParse(a.AsSpan(0, a.Length - 1), out int n))
                        maxAlbumTrackCount = n;
                    else if (a.Last() == '+' && int.TryParse(a.AsSpan(0, a.Length - 1), out n))
                        minAlbumTrackCount = n;
                    else if (int.TryParse(a, out n))
                    {
                        minAlbumTrackCount = n;
                        maxAlbumTrackCount = n;
                    }
                }
                if (lengthIndex >= 0)
                {
                    try { length = (int)ParseTrackLength(values[lengthIndex], timeUnit); }
                    catch { log.Warn($"Couldn't parse track length \"{values[lengthIndex]}\" with format \"{timeUnit}\""); }
                }

                if (title.Length == 0 && album.Length == 0 && artist.Length == 0)
                    continue;

                // Album row: no title, has album name
                if (title.Length == 0 && album.Length > 0)
                {
                    var query = new AlbumQuery
                    {
                        Artist        = artist,
                        Album         = album,
                        URI           = uri,
                    };
                    var itemNumber = rows.Count + 1;
                    rows.Add(new AlbumJob(query)
                    {
                        ItemNumber = itemNumber,
                        LineNumber = index,
                        SourceMutation = SourceMutation.ClearCsvRow(csvFilePath!, index, itemNumber, csvColumnCount),
                        ExtractorFolderCond = new FolderConditionPatch
                        {
                            MinTrackCount = minAlbumTrackCount >= 0 ? minAlbumTrackCount : null,
                            MaxTrackCount = maxAlbumTrackCount >= 0 ? maxAlbumTrackCount : null,
                        },
                    });
                }
                else if (ytParse)
                {
                    var song = await YouTube.ParseSongInfo(title, artist, uri, length, desc, log: log);
                    song.ItemNumber = rows.Count + 1;
                    song.LineNumber = index;
                    song.SourceMutation = SourceMutation.ClearCsvRow(csvFilePath!, index, song.ItemNumber, csvColumnCount);
                    rows.Add(song);
                }
                else
                {
                    var query = new SongQuery { Artist = artist, Title = title, Album = album, URI = uri, Length = length };
                    var itemNumber = rows.Count + 1;
                    rows.Add(new SongJob(query)
                    {
                        ItemNumber = itemNumber,
                        LineNumber = index,
                        SourceMutation = SourceMutation.ClearCsvRow(csvFilePath!, index, itemNumber, csvColumnCount),
                    });
                }
            }

            if (ytParse)
                YouTube.StopService();

            return rows;
        }

        static double ParseTrackLength(string duration, string format)
        {
            if (string.IsNullOrEmpty(format))
                throw new ArgumentException("Duration format string empty");
            duration = LettersRegex().Replace(duration, "");
            var formatParts   = NonWordRegex().Split(format);
            var durationParts = NonWordRegex().Split(duration).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            double totalSeconds = 0;
            for (int i = 0; i < formatParts.Length; i++)
            {
                switch (formatParts[i])
                {
                    case "h":  totalSeconds += double.Parse(durationParts[i]) * 3600; break;
                    case "m":  totalSeconds += double.Parse(durationParts[i]) * 60;   break;
                    case "s":  totalSeconds += double.Parse(durationParts[i]);         break;
                    case "ms": totalSeconds += double.Parse(durationParts[i]) / Math.Pow(10, durationParts[i].Length); break;
                }
            }
            return totalSeconds;
        }
    }
