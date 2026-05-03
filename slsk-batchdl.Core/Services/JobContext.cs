using Sldl.Core.Services;

namespace Sldl.Core.Services;

public class JobContext
{
    public M3uEditor?     PlaylistEditor        { get; set; }
    public M3uEditor?     IndexEditor           { get; set; }
    public TrackSkipper?  OutputDirSkipper      { get; set; }
    public TrackSkipper?  MusicDirSkipper       { get; set; }
    public bool           PreprocessTracks      { get; set; } = true;
    public bool           EnablesIndexByDefault { get; set; }
}
