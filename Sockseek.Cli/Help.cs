namespace Sockseek.Cli;

public static partial class Help
{
    public static void PrintHelp(string? option = null)
    {
        string text = helpText;

        var dict = new Dictionary<string, string>()
        {
            { "input", inputHelp },
            { "download-modes", downloadModesHelp },
            { "file-conditions", fileConditionsHelp },
            { "name-format", nameFormatHelp },
            { "variables", variablesHelp },
            { "on-complete", onCompleteHelp },
            { "config", configHelp },
            { "shortcuts", shortcutsHelp },
            { "notes", notesAndTipsHelp },
            { "daemon", daemonHelp },
            { "user", userHelp },
            { "database", databaseHelp },
        };

        if (option != null && dict.TryGetValue(option, out string? value))
            text = value;
        else if (option == "all")
            text = $"{helpText}\n{string.Join('\n', dict.Values)}";
        else if (option == "help")
            text = $"Choose from:\n\n  {string.Join("\n  ", dict.Keys)}";
        else if (option != null)
            text = $"Unrecognized help option '{option}'. Choose from:\n\n  {string.Join("\n  ", dict.Keys)}";

        Console.WriteLine(text.TrimStart('\r', '\n'));
    }

    public static void PrintVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
            System.Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        var v = attr?.InformationalVersion ?? "";
        Console.WriteLine(v.Split('+')[0]);
    }

    public static bool PrintAndExitIfNeeded(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(usageText.Trim());
            Console.WriteLine();
            Console.WriteLine("Type sockseek --help to see a list of all options.");
            return true;
        }

        int helpIdx = Array.FindLastIndex(args, x => x == "--help" || x == "-h");
        if (helpIdx >= 0)
        {
            string? topic = helpIdx + 1 < args.Length
                ? args[helpIdx + 1]
                : args.Length > 0 && string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase)
                    ? "daemon"
                    : args.Length > 0 && string.Equals(args[0], "database", StringComparison.OrdinalIgnoreCase)
                        ? "database"
                    : null;
            PrintHelp(topic);
            return true;
        }
        else if (args.Contains("--version"))
        {
            PrintVersion();
            return true;
        }

        return false;
    }
}
