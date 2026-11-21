using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sockseek.Cli;
using Sockseek.Core.Settings;

// Keep option descriptions beside the CLI documentation. Match its two-column tables.
var help = new Dictionary<string, string>();
foreach (var page in new[] { "download-options", "daemon-options" })
{
    var lines = File.ReadAllLines($"docs/pages/reference/{page}.md");
    for (int i = 0; i < lines.Length; i++)
    {
        var columns = Regex.Match(lines[i], @"^(?<flags>-[^\s,]+(?:,\s*-[^\s,]+)*(?:\s+<[^>]+>)?)(?:\s+(?<doc>\S.*))?$");
        if (!columns.Success) continue;
        var description = columns.Groups["doc"].Value;
        while (i + 1 < lines.Length && lines[i + 1].StartsWith("        "))
            description += " " + lines[++i].Trim();
        if (string.IsNullOrWhiteSpace(description)) continue;
        foreach (Match flag in Regex.Matches(columns.Groups["flags"].Value, @"(?<!\w)--?[\w-]+"))
            help[flag.Value] = description.Trim();
    }
}

var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var option in ConfigManager.ConfigurationOptions)
{
    string name = option.Names[^1];
    if (!name.StartsWith("--")) continue; // Short-only CLI compatibility forms.
    string description = option.Names.Reverse().Select(alias => help.GetValueOrDefault(alias)).FirstOrDefault(doc => doc != null)
        ?? $"Sockseek's {name} setting.";
    var entry = new Dictionary<string, object> { ["type"] = NixType(option), ["description"] = description };
    // Download profiles are replaced on reload. Only engine and daemon startup
    // settings need a new process, including compound setters such as --login.
    if ((option.RootType == typeof(EngineSettings) || option.RootType == typeof(DaemonSettings)) && !option.Reloadable)
        entry["restartRequired"] = true;
    if (option.DefaultProfileOnly) entry["defaultOnly"] = true;
    entries.Add(name.TrimStart('-'), JsonSerializer.Serialize(entry, jsonOptions));
}

static string NixType(SettingOption option)
{
    if (option.IsList) return "list";
    if (option.IsPresenceOnly) return "flag";
    var type = option.ConfigValueType;
    if (option.ValueKind == OptionValueKind.Boolean || type == typeof(bool)) return "bool";
    if (type == typeof(int)) return "int";
    if (type == typeof(double)) return "number";
    return "string";
}

string json = "{\n" + string.Join(",\n", entries.Select(pair => $"  {JsonSerializer.Serialize(pair.Key)}: {pair.Value}")) + "\n}\n";
const string output = "nix/settings.json";
if (args.Contains("--check"))
{
    if (File.Exists(output) && File.ReadAllText(output).ReplaceLineEndings("\n") == json) return 0;
    Console.Error.WriteLine($"Regenerate {output} with dotnet build");
    return 1;
}
if (!File.Exists(output) || File.ReadAllText(output).ReplaceLineEndings("\n") != json)
    File.WriteAllText(output, json);
return 0;
