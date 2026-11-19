using System.Text;

using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public class ListExtractor : IExtractor, IInputMatcher
    {
        string? listFilePath = null;
        private readonly PathVariableContext pathVariables;

        public ListExtractor()
            : this(PathVariableContext.Empty)
        {
        }

        public ListExtractor(PathVariableContext pathVariables)
        {
            this.pathVariables = pathVariables;
        }

        public static bool InputMatches(string input)
        {
            return !input.IsInternetUrl();
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            listFilePath = Utils.ExpandVariables(input, pathVariables);

            if (!File.Exists(listFilePath))
                throw new FileNotFoundException($"List file '{listFilePath}' not found");

            var lines = await File.ReadAllLinesAsync(listFilePath);

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

                ExtractionMode? requestedModeOverride = null;

                if (line.StartsWith("a:"))
                {
                    line = line[2..].TrimStart();
                    requestedModeOverride = ExtractionMode.Album;
                }
                else if (line.StartsWith("s:"))
                {
                    line = line[2..].TrimStart();
                    requestedModeOverride = ExtractionMode.Song;
                }

                List<string> fields;
                FileConditionPatch?      extractorCond           = null;
                FileConditionPatch?      extractorPrefCond       = null;
                FolderConditionPatch?    extractorFolderCond     = null;
                FolderConditionPatch?    extractorPrefFolderCond = null;

                try
                {
                    fields = ParseLine(line);

                    if (fields.Count >= 2)
                    {
                        extractorFolderCond = new FolderConditionPatch();
                        extractorCond = Services.ConditionParser.ParseFileConditions(fields[1], extractorFolderCond);
                    }
                    if (fields.Count >= 3)
                    {
                        extractorPrefFolderCond = new FolderConditionPatch();
                        extractorPrefCond = Services.ConditionParser.ParseFileConditions(fields[2], extractorPrefFolderCond);
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidDataException($"Error parsing list file '{listFilePath}' at line {i + 1}: {e.Message}");
                }

                var ej = new ExtractJob(fields[0])
                {
                    RequestedModeOverride = requestedModeOverride,
                    ExtractorCond           = extractorCond,
                    ExtractorPrefCond       = extractorPrefCond,
                    ExtractorFolderCond     = extractorFolderCond,
                    ExtractorPrefFolderCond = extractorPrefFolderCond,
                    EnablesIndexByDefault   = true,
                    LineNumber              = i + 1,
                    ItemNumber              = offset + added + 1,
                    SourceMutation          = SourceMutation.ClearTextLine(listFilePath!, i + 1, offset + added + 1),
                };

                result.Jobs.Add(ej);
                added++;
            }

            return result;
        }

        static List<string> ParseLine(string input)
        {
            var fields = new List<string>();
            bool inQuotes    = false;
            var currentField = new StringBuilder();

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && c is ' ' or '\t')
                {
                    int whitespaceStart = i;
                    while (i + 1 < input.Length && input[i + 1] is ' ' or '\t')
                        i++;

                    if (c == ' ' && i == whitespaceStart)
                    {
                        currentField.Append(c);
                    }
                    else
                    {
                        fields.Add(currentField.ToString());
                        currentField.Clear();
                    }
                }
                else
                {
                    currentField.Append(c);
                }
            }

            if (inQuotes)
                throw new FormatException("Unclosed double quote.");

            fields.Add(currentField.ToString());
            if (fields.Count > 3)
                throw new FormatException($"Expected at most 3 fields, found {fields.Count}.");
            if (string.IsNullOrWhiteSpace(fields[0]))
                throw new FormatException("Missing input field.");

            return fields;
        }

    }
