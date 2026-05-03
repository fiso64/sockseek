
namespace Sldl.Core;
    // Values 0-4 are written to index files — do not reorder, existing files must remain readable.
    // Values 5+ are runtime-only and never persisted.
    public enum JobState
    {
        Pending          = 0,
        Done             = 1,
        Failed           = 2,
        AlreadyExists    = 3,
        NotFoundLastTime = 4,
        Skipped          = 5,
        Searching        = 6,
        Downloading      = 7,
        Extracting       = 8,
    }

    public enum FailureReason
    {
        None = 0,
        InvalidSearchString = 1,
        OutOfDownloadRetries = 2,
        NoSuitableFileFound = 3,
        AllDownloadsFailed = 4,
        Other = 5,
        ExtractionFailed = 6,
        Cancelled = 7,
    }

    public enum SkipMode
    {
        Name = 0,
        Tag = 2,
        // non file-based skip modes are >= 4
        Index = 4,
    }

    public enum InputType
    {
        CSV,
        YouTube,
        Spotify,
        Bandcamp,
        String,
        List,
        Soulseek,
        MusicBrainz,
        None = -1,
    }

    public enum TrackType
    {
        Normal = 0,
        Album = 1,
        Aggregate = 2,
        AlbumAggregate = 3,
    }

    public enum M3uOption
    {
        None,
        Index,
        Playlist,
        All,
    }

    public enum PrintOption
    {
        None = 0,
        Tracks = 1,
        Results = 2,
        Full = 4,
        Link = 8,
        Json = 16,
        Index = 32,
        IndexFailed = 64,
    }

    public enum AlbumArtOption
    {
        Default,
        Most,
        Largest,
    }

    public enum Verbosity
    {
        Silent,
        Error,
        Warning,
        Normal,
        Verbose
    }

    public enum AlbumFailOption
    {
        Ignore,
        Keep,
        Delete,
    }
