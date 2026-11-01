using Sockseek.Core.Jobs;

namespace Sockseek.Core.Services;

public class JobContext
{
    internal JobList?     PreparationOwner    { get; set; }
    public DownloadOutputFile?     PlaylistEditor        { get; set; }
    public DownloadOutputFile?     IndexEditor           { get; set; }
    public TrackSkipper?  OutputDirSkipper      { get; set; }
    public TrackSkipper?  MusicDirSkipper       { get; set; }
    public OutputScope    OutputScope           { get; set; } = OutputScope.Empty;
    public bool           PreprocessTracks      { get; set; } = true;
    public bool           EnablesIndexByDefault { get; set; }
}
