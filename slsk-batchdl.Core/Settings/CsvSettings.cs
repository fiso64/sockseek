namespace Sldl.Core.Settings;

public class CsvSettings
{
    public string ArtistCol { get; set; } = "";

    public string AlbumCol { get; set; } = "";

    public string TitleCol { get; set; } = "";

    public string YtIdCol { get; set; } = "";

    public string DescCol { get; set; } = "";

    public string TrackCountCol { get; set; } = "";

    public string LengthCol { get; set; } = "";

    public string TimeUnit { get; set; } = "s";

    public bool YtParse { get; set; }
}
