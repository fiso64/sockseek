using System.Text;

using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public class ListExtractor : IExtractor, IInputMatcher
    {
        public JobSourceInfo? DescribeSource(ExtractJob source, Job item)
        {
            if (ReferenceEquals(source, item)) return new(Detail: "List import", PreviewDetail: "List import");
            if (item is not JobList list) return null;
            string detail = $"{list.Count} source {(list.Count == 1 ? "item" : "items")}";
            return new(Detail: detail, PreviewDetail: detail);
        }

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

            var result = new JobList
            {
                ItemName = Path.GetFileNameWithoutExtension(listFilePath), EnablesIndexByDefault = true,
                SourceCollectionInputType = InputType.List,
            };

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
                    line = line[2..];
                    requestedModeOverride = ExtractionMode.Album;
                }
                else if (line.StartsWith("s:"))
                {
                    line = line[2..];
                    requestedModeOverride = ExtractionMode.Song;
                }

                var fields = ParseLine(line);

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

                result.Add(ej);
                added++;
            }

            return result;
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

    }
