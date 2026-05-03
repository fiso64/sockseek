using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var rootDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent?.Parent?.Parent?.FullName ?? ".";
var readmePath = Path.Combine(rootDir, "README.md");
var helpCsPath = Path.Combine(rootDir, "slsk-batchdl.Cli", "Help.Content.cs");

if (!File.Exists(readmePath))
{
    Console.WriteLine($"Error: README.md not found at {readmePath}");
    return 1;
}

var markdown = File.ReadAllText(readmePath);
var topics = ExtractHelpTopics(markdown);
GenerateHelpCs(helpCsPath, topics);

Console.WriteLine("Help.cs generated successfully.");
return 0;

static Dictionary<string, string> ExtractHelpTopics(string markdown)
{
    var topics = new Dictionary<string, string>();
    var matches = Regex.Matches(markdown, @"<!-- sldl-help:start\((.*?)\) -->(.*?)<!-- sldl-help:end -->", RegexOptions.Singleline);

    foreach (Match match in matches)
    {
        var topicName = match.Groups[1].Value.Trim();
        var content = match.Groups[2].Value;
        topics[topicName] = ToPlainText(content);
    }
    return topics;
}

static string ToPlainText(string markdown)
{
    var pipeline = new MarkdownPipelineBuilder().Build();
    var document = Markdown.Parse(markdown, pipeline);
    var sb = new StringBuilder();
    var currentIndent = 0;

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

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var words = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
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
    void ProcessBlock(MarkdownObject block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                currentIndent = (heading.Level - minLevel) * 2;
                sb.AppendLine();
                sb.AppendLine(WrapAndIndent(GetInlineText(heading.Inline), currentIndent));
                break;

            case ParagraphBlock para:
                var paraText = GetInlineText(para.Inline);
                if (!string.IsNullOrWhiteSpace(paraText))
                {
                    sb.AppendLine(WrapAndIndent(paraText, currentIndent + 2));
                }
                break;

            case FencedCodeBlock codeBlock:
                sb.AppendLine();
                var codeIndent = new string(' ', currentIndent + 2);
                foreach (var line in codeBlock.Lines.Lines)
                {
                    var escapedLine = line.ToString().Replace("\"", "\"\"");
                    sb.AppendLine($"{codeIndent}{escapedLine}");
                }
                sb.AppendLine();
                break;

            case ListBlock list:
                var listCounter = 0;
                foreach (var item in list)
                {
                    listCounter++;
                    if (item is ListItemBlock listItem)
                    {
                        var bullet = list.IsOrdered ? $"{listCounter}." : "-";
                        var itemText = new StringBuilder();
                        foreach (var subBlock in listItem)
                        {
                            if (subBlock is ParagraphBlock p)
                            {
                                itemText.Append(GetInlineText(p.Inline).Trim() + " ");
                            }
                        }
                        var fullText = bullet + " " + itemText.ToString().Trim();
                        sb.AppendLine(WrapAndIndent(fullText, currentIndent + 2, currentIndent + 4));
                    }
                }
                break;

            case HtmlBlock:
                // Ignore blocks like <details>
                break;
        }
    }

    foreach (var b in document)
    {
        ProcessBlock(b);
    }

    // Final cleanup of extra newlines
    var finalString = Regex.Replace(sb.ToString(), @"([ \t]*(\r\n|\n)){3,}", Environment.NewLine + Environment.NewLine);
    return finalString.TrimEnd();
}

static void GenerateHelpCs(string filePath, Dictionary<string, string> topics)
{
    var template = @"// ------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
// ------------------------------------------------------------------------------

// This file is generated from README.md. To update, run your build, or:
// dotnet run --project slsk-batchdl.HelpGenerator

namespace Sldl.Cli;

public static partial class Help
{
    const string usageText = @""Usage: sldl <input> [OPTIONS]"";

    const string helpText = usageText + @""
__HELP_TEXT_MAIN__"";

    const string inputHelp = @""__HELP_TEXT_INPUT__"";

    const string downloadModesHelp = @""__HELP_TEXT_DOWNLOAD_MODES__"";

    const string fileConditionsHelp = @""__HELP_TEXT_FILE_CONDITIONS__"";

    const string nameFormatHelp = @""__HELP_TEXT_NAME_FORMAT__"";
    
    const string configHelp = @""__HELP_TEXT_CONFIG__"";

    const string onCompleteHelp = @""__HELP_TEXT_ON_COMPLETE__"";

    const string shortcutsHelp = @""__HELP_TEXT_SHORTCUTS__"";

    const string notesAndTipsHelp = @""__HELP_TEXT_NOTES_AND_TIPS__"";
}
";

    var content = template
        .Replace("__HELP_TEXT_MAIN__", topics["main"])
        .Replace("__HELP_TEXT_INPUT__", topics["input"])
        .Replace("__HELP_TEXT_DOWNLOAD_MODES__", topics["download-modes"])
        .Replace("__HELP_TEXT_FILE_CONDITIONS__", topics["file-conditions"])
        .Replace("__HELP_TEXT_NAME_FORMAT__", topics["name-format"])
        .Replace("__HELP_TEXT_CONFIG__", topics["config"])
        .Replace("__HELP_TEXT_ON_COMPLETE__", topics["on-complete"])
        .Replace("__HELP_TEXT_SHORTCUTS__", topics["shortcuts"])
        .Replace("__HELP_TEXT_NOTES_AND_TIPS__", topics["notes-and-tips"]);

    var directory = Path.GetDirectoryName(filePath);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    File.WriteAllText(filePath, content);
}
