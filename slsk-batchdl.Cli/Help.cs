namespace Sldl.Cli;

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
            { "on-complete", onCompleteHelp },
            { "config", configHelp },
            { "shortcuts", shortcutsHelp },
            { "notes", notesAndTipsHelp },
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
            Console.WriteLine("Type sldl --help to see a list of all options.");
            return true;
        }

        int helpIdx = Array.FindLastIndex(args, x => x == "--help" || x == "-h");
        if (helpIdx >= 0)
        {
            PrintHelp(helpIdx + 1 < args.Length ? args[helpIdx + 1] : null);
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