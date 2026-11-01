using Sockseek.Core;

namespace Sockseek.Core.Settings;

/// Controls where and how results are written.
public class OutputSettings
{
    internal OutputSettings ShallowClone() => (OutputSettings)MemberwiseClone();

    /// null resolves to Directory.GetCurrentDirectory() at bind time.
    public string? ParentDir { get; set; }

    public string NameFormat { get; set; } = "";

    public string InvalidReplaceStr { get; set; } = " ";

    public bool WritePlaylist { get; set; }

    public bool WriteIndex { get; set; } = true;

    /// Set to true when any of --write-index, --no-write-index, or --index-path is explicitly
    /// specified. When false, job preparation decides from the owner list's index defaults.
    public bool HasConfiguredIndex { get; set; }

    public string? M3uFilePath { get; set; }

    public string? IndexFilePath { get; set; }

    /// Controls what happens to completed files from an album folder when another file
    /// in that folder fails. Unset means move to {ParentDir}/failed.
    public IncompleteAlbumActionSettings IncompleteAlbumAction { get; set; } = new();

    /// null = no on-complete command. Populated by --on-complete (with optional "+ " append mode).
    /// The binder sets the whole list; ConfigManager handles the "+ " append prefix as a special case.
    public List<string>? OnComplete { get; set; }

    public bool AlbumArtOnly { get; set; }

    public AlbumArtOption AlbumArtOption { get; set; } = AlbumArtOption.Default;
}

public class IncompleteAlbumActionSettings
{
    public IncompleteAlbumActionKind? Kind { get; set; }

    /// Used when Kind is Move. Null means {configured output dir}/failed.
    public string? Path { get; set; }
}

public sealed record ResolvedIncompleteAlbumAction(IncompleteAlbumActionKind Kind, string? Path);
