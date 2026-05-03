using Sldl.Core.Models;
using Sldl.Core.Jobs;
using System.Text.RegularExpressions;
using Sldl.Core.Settings;

namespace Sldl.Core.Extractors;
    public partial class CsvExtractor : IExtractor, IInputMatcher
    {
        [GeneratedRegex(@"\(.*?\)")]
        private static partial Regex ParenthesesRegex();

        [GeneratedRegex("[a-zA-Z]")]
        private static partial Regex LettersRegex();

        [GeneratedRegex(@"\W+")]
        private static partial Regex NonWordRegex();

        private readonly CsvSettings _csv;

        public CsvExtractor(CsvSettings csv) { _csv = csv; }

        string? csvFilePath = null;
        int csvColumnCount = -1;
        private readonly SemaphoreSlim csvLock = new(1, 1);

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return !input.IsInternetUrl() && input.EndsWith(".csv");
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            csvFilePath = Utils.ExpandVariables(input);

            if (!File.Exists(csvFilePath))
                throw new FileNotFoundException($"CSV file '{csvFilePath}' not found");

            var rows = await ParseCsvRows(csvFilePath, _csv.ArtistCol, _csv.TitleCol, _csv.LengthCol,
                _csv.AlbumCol, _csv.DescCol, _csv.YtIdCol, _csv.TrackCountCol, _csv.TimeUnit, _csv.YtParse);

            if (reverse)
                rows.Reverse();

            var csvName = Path.GetFileNameWithoutExtension(csvFilePath);
            var jobs = BuildJobList(rows.Skip(offset).Take(maxTracks), csvName);

            if (jobs.Count == 1)
                return jobs[0];

            var list = new JobList { ItemName = csvName, EnablesIndexByDefault = true };
            list.Jobs.AddRange(jobs);
            return list;
        }

        // Builds a List<Job> from a sequence of per-row items (SongJob or AlbumJob).
        // Consecutive SongJobs are grouped into a single JobList.
        private static List<Job> BuildJobList(IEnumerable<object> rows, string csvName)
        {
            var jobs = new List<Job>();
            JobList? currentSlj = null;

            foreach (var row in rows)
            {
                if (row is AlbumJob albumJob)
                {
                    if (currentSlj != null)
                    {
                        jobs.Add(currentSlj);
                        currentSlj = null;
                    }
                    albumJob.EnablesIndexByDefault = true;
                    jobs.Add(albumJob);
                }
                else if (row is SongJob song)
                {
                    currentSlj ??= new JobList
                    {
                        ItemName              = csvName,
                        EnablesIndexByDefault = true,
                    };
                    currentSlj.Jobs.Add(song);
                }
            }

            if (currentSlj != null && currentSlj.Jobs.Count > 0)
                jobs.Add(currentSlj);

            return jobs;
        }

        public async Task RemoveFromSource(Job job)
        {
            await csvLock.WaitAsync();
            try
            {
                if (File.Exists(csvFilePath))
                {
                    try
                    {
                        string[] lines = await File.ReadAllLinesAsync(csvFilePath, System.Text.Encoding.UTF8);
                        int idx = job.LineNumber - 1;
                        if (idx > -1 && idx < lines.Length)
                        {
                            lines[idx] = new string(',', Math.Max(0, csvColumnCount - 1));
                            await Utils.WriteAllLinesAsync(csvFilePath, lines, '\n');
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Error removing from source: {e}");
                    }
                }
            }
            finally
            {
                csvLock.Release();
            }
        }

        // Returns a list of rows — either SongJob (song row) or AlbumJob (album row).
        async Task<List<object>> ParseCsvRows(string path, string artistCol = "", string trackCol = "",
            string lengthCol = "", string albumCol = "", string descCol = "", string ytIdCol = "", string trackCountCol = "",
            string timeUnit = "s", bool ytParse = false)
        {
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
                ["artist", "artist name", "artists", "artist names"],
                ["album", "album name", "album title"],
                ["title", "song", "track title", "track name", "song name", "track"],
                ["length", "duration", "track length", "track duration", "song length", "song duration"],
                ["description", "youtube description"],
                ["url", "track url", "uri", "id", "youtube id"],
                ["track count", "album track count"]
            ];

            string usingColumns = "";
            for (int i = 0; i < cols.Length; i++)
            {
                if (string.IsNullOrEmpty(cols[i]))
                {
                    string? res = header.FirstOrDefault(h => ParenthesesRegex().Replace(h, "").Trim().EqualsAny(aliases[i], StringComparison.OrdinalIgnoreCase));
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
                Logger.Info($"Using columns: {usingColumns.TrimEnd(' ', ',')}.");
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
                    catch { Logger.Warn($"Couldn't parse track length \"{values[lengthIndex]}\" with format \"{timeUnit}\""); }
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
                    rows.Add(new AlbumJob(query)
                    {
                        ItemNumber = rows.Count + 1,
                        LineNumber = index,
                        ExtractorFolderCond = new FolderConditionPatch
                        {
                            MinTrackCount = minAlbumTrackCount >= 0 ? minAlbumTrackCount : null,
                            MaxTrackCount = maxAlbumTrackCount >= 0 ? maxAlbumTrackCount : null,
                        },
                    });
                }
                else if (ytParse)
                {
                    var song = await YouTube.ParseSongInfo(title, artist, uri, length, desc);
                    song.ItemNumber = rows.Count + 1;
                    song.LineNumber = index;
                    rows.Add(song);
                }
                else
                {
                    var query = new SongQuery { Artist = artist, Title = title, Album = album, URI = uri, Length = length };
                    rows.Add(new SongJob(query) { ItemNumber = rows.Count + 1, LineNumber = index });
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
