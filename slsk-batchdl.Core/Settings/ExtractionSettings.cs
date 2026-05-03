using Sldl.Core;

namespace Sldl.Core.Settings;

/// Controls what is extracted and how.
public class ExtractionSettings
{
    public string? Input { get; set; }

    public InputType InputType { get; set; } = InputType.None;

    public int MaxTracks { get; set; } = int.MaxValue;

    public int Offset { get; set; }

    public bool Reverse { get; set; }

    public bool RemoveTracksFromSource { get; set; }

    // Result shape hints — tell extractors what job shape to produce
    public bool IsAlbum { get; set; }

    public bool SetAlbumMinTrackCount { get; set; } = true;

    public bool SetAlbumMaxTrackCount { get; set; }
}
