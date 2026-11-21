using Sockseek.Core;

namespace Sockseek.Cli;

public static partial class Help
{
    private static readonly Dictionary<string, string> Guides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["input"] = inputHelp,
        ["download-modes"] = downloadModesHelp,
        ["file-conditions"] = fileConditionsHelp,
        ["name-format"] = nameFormatHelp,
        ["variables"] = variablesHelp,
        ["on-complete"] = onCompleteHelp,
        ["config"] = configHelp,
        ["shortcuts"] = shortcutsHelp,
        ["notes"] = notesAndTipsHelp,
    };

    private static readonly Dictionary<string, string> Topics = CommandTopics();

    public static void PrintHelp(string? option = null)
    {
        if (option == "user" && !Topics.ContainsKey("user"))
            option = "remote";

        string text = helpText;
        if (option == "all")
        {
            // Remote connection flags are already in the main options.
            var references = Topics.Where(topic => topic.Key != "remote").Select(topic => topic.Value);
            text = string.Join("\n", Guides.Values.Prepend(helpText).Concat(references));
        }
        else if (option != null)
        {
            if (Topics.TryGetValue(option, out var reference) || Guides.TryGetValue(option, out reference))
                text = reference;
            else
            {
                string prompt = option == "help" ? "Choose from:" : $"Unrecognized help option '{option}'. Choose from:";
                text = $"{prompt}\n\n  {string.Join("\n  ", Guides.Keys.Concat(Topics.Keys))}";
            }
        }

        Console.WriteLine(text.TrimStart('\r', '\n'));
        if (option?.ToLowerInvariant() is "auth" or "chat" or "room" or "notifications" or "notification"
            or "share" or "transfers" or "transfer")
            Console.WriteLine("\nSee sockseek --help remote for shared remote CLI options.");
    }

    public static void PrintVersion() => Console.WriteLine(ProductVersion.Display);

    public static bool PrintAndExitIfNeeded(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(usageText.Trim());
            Console.WriteLine();
            Console.WriteLine("Type sockseek --help to see commands and options.");
            return true;
        }

        int helpIdx = Array.FindLastIndex(args, x => x == "--help" || x == "-h");
        if (helpIdx < 0)
            return false;

        string command = args[0].ToLowerInvariant();
        string? topic = helpIdx > 0 && (command == "user" || command != "remote" && Topics.ContainsKey(command))
            ? command
            : args.ElementAtOrDefault(helpIdx + 1);
        PrintHelp(topic);
        return true;
    }
}
