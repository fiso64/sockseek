using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var rootDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent?.Parent?.Parent?.FullName ?? ".";
var docsPath = Path.Combine(rootDir, "docs", "pages");
var helpCsPath = Path.Combine(rootDir, "Sockseek.Cli", "Help.Content.cs");

if (!Directory.Exists(docsPath))
{
    Console.WriteLine($"Error: Manual not found at {docsPath}");
    return 1;
}

var markdown = string.Join("\n", Directory.EnumerateFiles(docsPath, "*.md", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal).Select(File.ReadAllText));
// Disabled command references are omitted from both the main help and the topic index.
markdown = Regex.Replace(markdown,
    @"<!-- sockseek-help-topic:start\([\w-]+\) disabled -->.*?<!-- sockseek-help-topic:end -->",
    "", RegexOptions.Singleline);
var topics = ExtractHelpTopics(markdown, "sockseek-help");
var commands = ExtractHelpTopics(markdown, "sockseek-help-topic");
var generatedContent = NormalizeNewlines(GenerateHelpCs(topics, commands));

if (args.Contains("--check", StringComparer.Ordinal))
{
    if (!File.Exists(helpCsPath))
    {
        Console.WriteLine($"Error: generated help file not found at {helpCsPath}");
        return 1;
    }

    var existingContent = File.ReadAllText(helpCsPath);
    if (!string.Equals(NormalizeNewlines(existingContent), generatedContent, StringComparison.Ordinal))
    {
        Console.WriteLine("Error: Sockseek.Cli/Help.Content.cs is generated and does not match the Markdown manual.");
        Console.WriteLine("Update the Markdown in docs/pages/ instead of editing Help.Content.cs directly, then rebuild or run:");
        Console.WriteLine("  dotnet run --project Sockseek.HelpGenerator");
        return 1;
    }

    Console.WriteLine("Help.Content.cs is up to date.");
    return 0;
}

if (!File.Exists(helpCsPath) || NormalizeNewlines(File.ReadAllText(helpCsPath)) != generatedContent)
    File.WriteAllText(helpCsPath, generatedContent);

Console.WriteLine("Help.cs generated successfully.");
return 0;

static Dictionary<string, string> ExtractHelpTopics(string markdown, string marker)
{
    var topics = new Dictionary<string, string>();
    var matches = Regex.Matches(markdown,
        $@"<!-- {marker}:start\(([\w-]+)\) -->(.*?)<!-- {marker}:end -->", RegexOptions.Singleline);
    foreach (Match match in matches)
        topics.Add(match.Groups[1].Value, ToPlainText(match.Groups[2].Value));
    return topics;
}

static string ToPlainText(string markdown)
{
    var pipeline = new MarkdownPipelineBuilder().UseAlertBlocks().Build();
    var document = Markdown.Parse(markdown, pipeline);
    var sb = new StringBuilder();

    var headings = document.Descendants<HeadingBlock>();
    var minLevel = headings.Any() ? headings.Min(h => h.Level) : 1;

    // Local function to get text from inline elements
    string GetInlineText(Inline? inline)
    {
        if (inline == null) return "";
        var inlineSb = new StringBuilder();

        switch (inline)
        {
            case LiteralInline literal:
                inlineSb.Append(literal.Content.ToString().Replace("\"", "\"\""));
                break;
            case CodeInline code:
                inlineSb.Append(code.Content.Replace("\"", "\"\""));
                break;
            case LineBreakInline:
                inlineSb.Append(' '); // Treat hard breaks as spaces
                break;
            case LinkInline { IsImage: true }:
                break; // Screenshots have nothing to show in a terminal.
            case ContainerInline container:
                foreach (var child in container)
                {
                    inlineSb.Append(GetInlineText(child));
                }
                break;
        }
        return inlineSb.ToString();
    }

    // Local function for word wrapping and indentation
    string WrapAndIndent(string text, int indent, int subsequentIndent = -1)
    {
        if (subsequentIndent == -1) subsequentIndent = indent;
        const int maxWidth = 100;
        var result = new StringBuilder();
        var indentStr = new string(' ', indent);
        var subsequentIndentStr = new string(' ', subsequentIndent);

        var lines = text.Split(HelpGeneratorSeparators.Newlines, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var words = line.Split(HelpGeneratorSeparators.Words, StringSplitOptions.RemoveEmptyEntries);
            var currentLine = new StringBuilder();
            currentLine.Append(indentStr);
            var firstWord = true;

            foreach (var word in words)
            {
                if (!firstWord && currentLine.Length + word.Length + 1 > maxWidth)
                {
                    result.AppendLine(currentLine.ToString());
                    currentLine.Clear();
                    currentLine.Append(subsequentIndentStr);
                    firstWord = true;
                }

                if (!firstWord)
                {
                    currentLine.Append(' ');
                }
                currentLine.Append(word);
                firstWord = false;
            }
            result.AppendLine(currentLine.ToString());
        }
        return result.ToString().TrimEnd();
    }

    // Local function to process each block
    void ProcessBlock(MarkdownObject block, int indent)
    {
        switch (block)
        {
            case HeadingBlock heading:
                sb.AppendLine();
                sb.AppendLine(WrapAndIndent(GetInlineText(heading.Inline), indent - 2));
                break;

            case ParagraphBlock para:
                var paraText = GetInlineText(para.Inline);
                if (!string.IsNullOrWhiteSpace(paraText))
                {
                    sb.AppendLine(WrapAndIndent(paraText, indent));
                }
                break;

            case FencedCodeBlock codeBlock:
                sb.AppendLine();
                var codeIndent = new string(' ', indent);
                foreach (var line in codeBlock.Lines.Lines)
                {
                    var escapedLine = line.ToString().TrimEnd().Replace("\"", "\"\"");
                    sb.AppendLine(escapedLine.Length == 0 ? "" : $"{codeIndent}{escapedLine}");
                }
                sb.AppendLine();
                break;

            case ListBlock list:
                var listCounter = list.IsOrdered && int.TryParse(list.OrderedStart, out int start) ? start - 1 : 0;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    listCounter++;
                    var bullet = list.IsOrdered ? $"{listCounter}." : "-";
                    var contentIndent = indent + bullet.Length + 1;
                    var firstParagraph = item.FirstOrDefault() as ParagraphBlock;
                    var firstLine = firstParagraph is null ? bullet : $"{bullet} {GetInlineText(firstParagraph.Inline).Trim()}";
                    sb.AppendLine(WrapAndIndent(firstLine, indent, contentIndent));
                    foreach (var child in item.Skip(firstParagraph is null ? 0 : 1))
                        ProcessBlock(child, contentIndent);
                }
                break;

            case AlertBlock alert:
                sb.AppendLine(WrapAndIndent(alert.Kind.ToString().ToUpperInvariant(), indent));
                foreach (var child in alert)
                    ProcessBlock(child, indent);
                break;

            case HtmlBlock:
                // Ignore blocks like <details>
                break;
        }
    }

    var bodyIndent = 2;
    foreach (var block in document)
    {
        if (block is HeadingBlock heading)
            bodyIndent = (heading.Level - minLevel) * 2 + 2;
        ProcessBlock(block, bodyIndent);
    }

    // Final cleanup of extra newlines
    var finalString = Regex.Replace(sb.ToString(), @"([ \t]*(\r\n|\n)){3,}", Environment.NewLine + Environment.NewLine);
    return finalString.TrimEnd();
}

static string GenerateHelpCs(Dictionary<string, string> topics, Dictionary<string, string> commands)
{
    var template = @"// ------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
// ------------------------------------------------------------------------------

// This file is generated from the Markdown manual in docs/pages/. To update, run your build, or:
// dotnet run --project Sockseek.HelpGenerator

namespace Sockseek.Cli;

public static partial class Help
{
    const string usageText = @""Usage: sockseek <input> [OPTIONS]
       sockseek <command> [OPTIONS]"";

    const string helpText = usageText + @""
__HELP_TEXT_MAIN__"";

    const string inputHelp = @""__HELP_TEXT_INPUT__"";

    const string downloadModesHelp = @""__HELP_TEXT_DOWNLOAD_MODES__"";

    const string rankingAndFilteringHelp = @""__HELP_TEXT_RANKING_AND_FILTERING__"";

    const string nameFormatHelp = @""__HELP_TEXT_NAME_FORMAT__"";

    const string variablesHelp = @""__HELP_TEXT_VARIABLES__"";
    
    const string configHelp = @""__HELP_TEXT_CONFIG__"";

    const string onCompleteHelp = @""__HELP_TEXT_ON_COMPLETE__"";

    const string shortcutsHelp = @""__HELP_TEXT_SHORTCUTS__"";

    const string notesAndTipsHelp = @""__HELP_TEXT_NOTES_AND_TIPS__"";

    const string daemonHelp = @""__HELP_TEXT_DAEMON__"";

    private static Dictionary<string, string> CommandTopics() => new(StringComparer.OrdinalIgnoreCase)
    {
__COMMAND_TOPICS__
    };
}
";

    var content = template
        .Replace("__HELP_TEXT_MAIN__", string.Join("\n", topics["main"], topics["daemon-cli"], topics["cli-notes"]))
        .Replace("__HELP_TEXT_INPUT__", topics["input"])
        .Replace("__HELP_TEXT_DOWNLOAD_MODES__", topics["download-modes"])
        .Replace("__HELP_TEXT_RANKING_AND_FILTERING__", topics["ranking-and-filtering"])
        .Replace("__HELP_TEXT_NAME_FORMAT__", topics["name-format"])
        .Replace("__HELP_TEXT_VARIABLES__", topics["variables"])
        .Replace("__HELP_TEXT_CONFIG__", topics["config"])
        .Replace("__HELP_TEXT_ON_COMPLETE__", topics["on-complete"])
        .Replace("__HELP_TEXT_SHORTCUTS__", topics["shortcuts"])
        .Replace("__HELP_TEXT_NOTES_AND_TIPS__", topics["notes-and-tips"])
        .Replace("__HELP_TEXT_DAEMON__", topics["daemon"])
        .Replace("__COMMAND_TOPICS__", string.Join("\n", commands.Select(command =>
            command.Key == "daemon-options"
                ? $"        [\"daemon\"] = daemonHelp + \"\\n\" + @\"{command.Value}\","
                : $"        [\"{command.Key}\"] = @\"{command.Value}\",")));
    return content;
}

static string NormalizeNewlines(string text)
    => text.Replace("\r\n", "\n").Replace('\r', '\n');

file static class HelpGeneratorSeparators
{
    public static readonly string[] Newlines = ["\r\n", "\r", "\n"];
    public static readonly char[] Words = [' '];
}
