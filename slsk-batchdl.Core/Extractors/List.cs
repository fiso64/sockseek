using System.Text;

using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core.Settings;

namespace Sldl.Core.Extractors;
    public class ListExtractor : IExtractor, IInputMatcher
    {
        string? listFilePath = null;
        readonly object fileLock = new object();

        public static bool InputMatches(string input)
        {
            return !input.IsInternetUrl();
        }

        public Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            listFilePath = Utils.ExpandVariables(input);

            if (!File.Exists(listFilePath))
                throw new FileNotFoundException($"List file '{listFilePath}' not found");

            var lines = File.ReadAllLines(listFilePath);

            var result = new JobList { ItemName = Path.GetFileNameWithoutExtension(listFilePath), EnablesIndexByDefault = true };

            int step  = reverse ? -1 : 1;
            int start = reverse ? lines.Length - 1 : 0;
            int count = 0;
            int added = 0;

            for (int i = start; i < lines.Length && i >= 0; i += step)
            {
                var line = lines[i].Trim();

                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (count++ < offset) continue;
                if (added >= maxTracks) break;

                bool isAlbum = false;

                if (line.StartsWith("a:"))
                {
                    line    = line[2..];
                    isAlbum = true;
                }

                var fields = ParseLine(line);

                if (isAlbum)
                    fields[0] = "album://" + fields[0];

                FileConditionPatch?      extractorCond         = null;
                FileConditionPatch?      extractorPrefCond     = null;
                FolderConditionPatch?    extractorFolderCond     = null;
                FolderConditionPatch?    extractorPrefFolderCond = null;

                if (fields.Count >= 2)
                {
                    var fc = new FolderConditionPatch();
                    extractorCond       = Services.ConditionParser.ParseFileConditions(fields[1], fc);
                    extractorFolderCond = fc;
                }
                if (fields.Count >= 3)
                {
                    var fc = new FolderConditionPatch();
                    extractorPrefCond       = Services.ConditionParser.ParseFileConditions(fields[2], fc);
                    extractorPrefFolderCond = fc;
                }

                var ej = new ExtractJob(fields[0])
                {
                    ExtractorCond           = extractorCond,
                    ExtractorPrefCond       = extractorPrefCond,
                    ExtractorFolderCond     = extractorFolderCond,
                    ExtractorPrefFolderCond = extractorPrefFolderCond,
                    EnablesIndexByDefault   = true,
                    LineNumber              = i + 1,
                    ItemNumber              = offset + added + 1,
                };

                result.Jobs.Add(ej);
                added++;
            }

            return Task.FromResult<Job>(result);
        }

        static List<string> ParseLine(string input)
        {
            var fields = new List<string>();
            bool inQuotes    = false;
            var currentField = new StringBuilder();
            input = input.Replace('\t', ' ');

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ' ' && !inQuotes)
                {
                    if (currentField.Length > 0)
                    {
                        fields.Add(currentField.ToString());
                        currentField.Clear();
                    }
                    while (i < input.Length - 1 && input[i + 1] == ' ')
                        i++;
                }
                else
                {
                    currentField.Append(c);
                }
            }

            if (currentField.Length > 0)
                fields.Add(currentField.ToString());

            return fields;
        }

        public Task RemoveFromSource(Job job)
        {
            lock (fileLock)
            {
                if (File.Exists(listFilePath))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(listFilePath, Encoding.UTF8);
                        int idx = job.LineNumber - 1;

                        if (idx > -1 && idx < lines.Length)
                        {
                            lines[idx] = "";
                            Utils.WriteAllLines(listFilePath, lines, '\n');
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Error removing from source: {e}");
                    }
                }
            }
            return Task.CompletedTask;
        }
    }
