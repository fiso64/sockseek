using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Core.Settings;

/// Controls where and how results are written.
public class OutputSettings
{
    /// null resolves to Directory.GetCurrentDirectory() at bind time.
    [SettingOption("-o", Parser = nameof(ParseLegacyOutputDirectory))]
    [SettingOption("--output-dir", "-p", "--path", "--parent")]
    public string? ParentDir { get; set; }

    private static string ParseLegacyOutputDirectory(string value, string flag)
    {
        if (int.TryParse(value.Replace("_", ""), out _))
            throw new Exception(
                $"Input error: '-o {value}' looks like the old short form for '--offset {value}'. " +
                $"'-o' now means '--output-dir'. Use '--offset {value}' to skip tracks, or " +
                $"'-o ./{value}' if you really want to download into a '{value}' subdirectory of the current directory.");
        return value;
    }

    [SettingOption("--nf", "--name-format")]
    public string NameFormat { get; set; } = "";

    [SettingOption("--irs", "--invalid-replace-str")]
    public string InvalidReplaceStr { get; set; } = " ";

    [SettingOption("--wp", "--write-playlist")]
    [SettingOption("--nwp", "--no-write-playlist", FixedValue = "false")]
    public bool WritePlaylist { get; set; }

    [SettingOption("--pp", "--playlist-path")]
    public string? M3uFilePath { get; set; }

    /// Null selects the default for the input; an explicit path also enables indexing.
    [SettingOption("--wi", "--write-index")]
    [SettingOption("--nwi", "--no-write-index", FixedValue = "false")]
    public bool? WriteIndex { get; set; }

    [SettingOption("--ip", "--index-path")]
    public string? IndexFilePath { get; set; }

    /// Controls what happens to completed files when another file in the album fails.
    /// Unset means move to {ParentDir}/failed.
    [SettingOption("--iaa", "--incomplete-album-action", Parser = nameof(ParseIncompleteAlbumAction))]
    public IncompleteAlbumActionSettings IncompleteAlbumAction { get; set; } = new();

    private static IncompleteAlbumActionSettings ParseIncompleteAlbumAction(string value, string flag)
    {
        string text = value.Trim();
        var action = new IncompleteAlbumActionSettings();
        if (text.StartsWith("move:", StringComparison.OrdinalIgnoreCase))
        {
            action.Kind = IncompleteAlbumActionKind.Move;
            action.Path = text["move:".Length..].Trim();
            if (action.Path.Length == 0)
                throw new Exception($"Input error: Option '{flag}' requires a path for move:<path>");
        }
        else
        {
            action.Kind = text.ToLowerInvariant() switch
            {
                "move" => IncompleteAlbumActionKind.Move,
                "delete" => IncompleteAlbumActionKind.Delete,
                "keep" => IncompleteAlbumActionKind.Keep,
                _ => throw new Exception($"Input error: Invalid incomplete album action '{value}' for '{flag}'"),
            };
        }
        return action;
    }

    /// --on-complete replaces the commands, or appends with the "+ " prefix.
    [SettingValidation(typeof(OnCompleteExecutor), nameof(OnCompleteExecutor.ValidateCommands))]
    public List<string>? OnComplete { get; set; }

    [SettingOption("--oc", "--on-complete")]
    private static void ConfigureOnComplete(SettingWriter changes, string value)
    {
        var trimmed = value.TrimStart();
        bool append = trimmed.StartsWith("+ ", StringComparison.Ordinal);
        var command = append ? trimmed[2..] : value.Trim();
        OnCompleteExecutor.ValidateCommand(command);
        if (append) changes.Append(nameof(OnComplete), new List<string> { command });
        else changes.Set(nameof(OnComplete), new List<string> { command });
    }

    [SettingOption("--aa", "--album-art", Parser = nameof(ParseAlbumArt))]
    public AlbumArtOption AlbumArtOption { get; set; } = AlbumArtOption.Default;

    private static AlbumArtOption ParseAlbumArt(string value, string flag) => value.ToLower().Trim() switch
    {
        "default" => AlbumArtOption.Default,
        "largest" => AlbumArtOption.Largest,
        "most" => AlbumArtOption.Most,
        var invalid => throw new ArgumentException($"Input error: Invalid album art option '{invalid}'"),
    };

    [SettingOption("--aao", "--aa-only", "--album-art-only")]
    public bool AlbumArtOnly { get; set; }

    public AlbumArtOption EffectiveAlbumArtOption => AlbumArtOnly && AlbumArtOption == AlbumArtOption.Default
        ? AlbumArtOption.Largest : AlbumArtOption;
}

public class IncompleteAlbumActionSettings
{
    public IncompleteAlbumActionKind? Kind { get; set; }

    /// Used when Kind is Move. Null means {ParentDir}/failed.
    public string? Path { get; set; }
}

public sealed record ResolvedIncompleteAlbumAction(IncompleteAlbumActionKind Kind, string? Path);
