using Sldl.Core;

namespace Sldl.Core.Settings;

/// Controls where and how results are written.
public class OutputSettings
{
    /// null resolves to Directory.GetCurrentDirectory() at bind time.
    public string? ParentDir { get; set; }

    public string NameFormat { get; set; } = "";

    public string InvalidReplaceStr { get; set; } = " ";

    public bool WritePlaylist { get; set; }

    public bool WriteIndex { get; set; } = true;

    /// Set to true when any of --write-index, --no-write-index, or --index-path is explicitly
    /// specified. When false, ConfigManager.WillWriteIndex() decides based on job queue state.
    public bool HasConfiguredIndex { get; set; }

    public string? M3uFilePath { get; set; }

    public string? IndexFilePath { get; set; }

    /// null = default (parentDir/failed). "delete" = delete on fail. "disable" = keep partial.
    public string? FailedAlbumPath { get; set; }

    /// null = no on-complete command. Populated by --on-complete (with optional "+ " append mode).
    /// The binder sets the whole list; ConfigManager handles the "+ " append prefix as a special case.
    public List<string>? OnComplete { get; set; }

    public bool AlbumArtOnly { get; set; }

    public AlbumArtOption AlbumArtOption { get; set; } = AlbumArtOption.Default;
}
