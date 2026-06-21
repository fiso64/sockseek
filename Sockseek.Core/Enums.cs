using System.Text.Json.Serialization;


namespace Sockseek.Core;

    public enum JobLifecycleState
    {
        Pending = 0,
        Running = 1,
        AwaitingSelection = 2,
        Terminal = 3,
    }

    public enum JobActivityPhase
    {
        None = 0,
        WaitingForSearchConcurrency = 1,
        SearchRateLimited = 2,
        Searching = 3,
        ProcessingSearchResults = 4,
        Extracting = 5,
        Downloading = 6,
        RetrievingFolder = 7,
        RunningChildren = 8,
        Organizing = 9,
        RunningOnComplete = 10,
    }

    public enum JobTerminalOutcome
    {
        None = 0,
        Succeeded = 1,
        Failed = 2,
        Skipped = 3,
        Cancelled = 4,
        PartialSuccess = 5,
    }

    public enum JobSkipReason
    {
        None = 0,
        AlreadyExists = 1,
        NotFoundLastTime = 2,
        Manual = 3,
        Filtered = 4,
    }

    /// <summary>
    /// Provenance for a terminal job cancellation. This describes why the job ended as
    /// cancelled; it should not be used for non-terminal attempt cancellations such as
    /// "try next candidate" unless that cancellation becomes the job's terminal result.
    /// </summary>
    public enum JobCancellationSource
    {
        /// <summary>The job is not cancelled, or the source has not been assigned yet.</summary>
        None = 0,
        /// <summary>The user explicitly cancelled this single job.</summary>
        UserRequestedJob = 1,
        /// <summary>The job was cancelled because its parent job was cancelled.</summary>
        ParentJob = 2,
        /// <summary>The user cancelled the workflow that owns this job.</summary>
        UserRequestedWorkflow = 3,
        /// <summary>The user requested global cancellation of all engine jobs.</summary>
        UserRequestedAllJobs = 4,
        /// <summary>
        /// The engine cancelled the job internally, for example because an engine-owned
        /// timeout/watchdog such as stale-download detection caused a terminal cancellation.
        /// </summary>
        InternalEngine = 5,
    }

    // Legacy index-file state. Values are persisted in user index files; do not reorder.
    // New runtime code should use JobLifecycleState, JobActivityPhase, JobTerminalOutcome,
    // and JobSkipReason. Keep this enum boxed into index read/write compatibility.
    public enum JobStateOld
    {
        Pending          = 0,
        Done             = 1,
        Failed           = 2,
        AlreadyExists    = 3,
        NotFoundLastTime = 4,
    }

    public enum JobFailureReason
    {
        None = 0,
        InvalidSearchString = 1,
        OutOfDownloadRetries = 2,
        AllDownloadsFailed = 4,
        Other = 5,
        ExtractionFailed = 6,
        Cancelled = 7,
        ChildJobsFailed = 8,
        NoSearchResults = 9,
        NoMatchingResults = 10,
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

    /// <summary>
    /// User-requested song/album interpretation for inputs whose shape is ambiguous or
    /// intentionally overrideable. A null requested mode means "let the source decide":
    /// string/list string input defaults to Album in 3.0, while concrete sources such as
    /// CSV rows, Spotify/YouTube playlists, MusicBrainz releases, and Soulseek links keep
    /// their source-defined song/album shape. Source-result upgrades are controlled separately
    /// by ExtractionSettings.UpgradeToAlbum.
    /// </summary>
    public enum ExtractionMode
    {
        Song = 0,
        Album = 1,
    }

    // backward-compat, remove this
    public enum TrackTypeOld
    {
        Normal = 0,
        Album = 1,
        Aggregate = 2,
        AlbumAggregate = 3,
    }

    // TODO: should be removed once the TODO in M3uEditor.cs is done.
    // Indexes are no longer stored in the M3Us, so it doesn't make sense to model this
    // as one enum. Use two independent bools instead.
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

    [JsonConverter(typeof(JsonStringEnumConverter<IncompleteAlbumActionKind>))]
    public enum IncompleteAlbumActionKind
    {
        Move,
        Delete,
        Keep,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<DownloadBehavior>))]
    public enum DownloadBehavior
    {
        Automatic,
        Manual,
    }
