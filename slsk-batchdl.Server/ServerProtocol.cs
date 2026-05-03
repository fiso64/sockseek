using System.Text.Json.Serialization;

namespace Sldl.Server;

/// <summary>
/// Runtime job kind returned by job and workflow endpoints.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobKind>))]
public enum ServerJobKind
{
    /// <summary>Input extraction job.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.Extract)]
    Extract,
    /// <summary>Search job. Track and album searches share this runtime kind.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.Search)]
    Search,
    /// <summary>Single-file download job.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.Song)]
    Song,
    /// <summary>Folder or album download job.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.Album)]
    Album,
    /// <summary>Aggregate track search job.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.Aggregate)]
    Aggregate,
    /// <summary>Aggregate album search job.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.AlbumAggregate)]
    AlbumAggregate,
    /// <summary>Container job that owns child jobs.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.JobList)]
    JobList,
    /// <summary>Folder-browse job used to fully load a search result folder.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.RetrieveFolder)]
    RetrieveFolder,
    /// <summary>Fallback kind for unknown or unmapped core jobs.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.Generic)]
    Generic,
}

/// <summary>
/// Runtime job state returned by job, workflow, and event DTOs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobState>))]
public enum ServerJobState
{
    /// <summary>Job has not started running yet.</summary>
    Pending,
    /// <summary>Job completed successfully.</summary>
    Done,
    /// <summary>Job completed unsuccessfully.</summary>
    Failed,
    /// <summary>Job was skipped because the target already exists.</summary>
    AlreadyExists,
    /// <summary>Job was skipped because the item was previously marked not found.</summary>
    NotFoundLastTime,
    /// <summary>Job was skipped by user or configuration decision.</summary>
    Skipped,
    /// <summary>Job is searching Soulseek results.</summary>
    Searching,
    /// <summary>Job is downloading.</summary>
    Downloading,
    /// <summary>Job is extracting input into follow-up jobs.</summary>
    Extracting,
}

/// <summary>
/// Aggregate workflow state returned by workflow endpoints.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerWorkflowState>))]
public enum ServerWorkflowState
{
    /// <summary>At least one workflow job is still active.</summary>
    [JsonStringEnumMemberName("active")]
    Active,
    /// <summary>Workflow has finished and at least one job failed.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
    /// <summary>Workflow has finished without failures.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,
}

/// <summary>
/// Stable failure reason returned by job and event DTOs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerFailureReason>))]
public enum ServerFailureReason
{
    /// <summary>No failure reason.</summary>
    None,
    /// <summary>The job could not form a valid search string.</summary>
    InvalidSearchString,
    /// <summary>The job exhausted download retry attempts.</summary>
    OutOfDownloadRetries,
    /// <summary>No acceptable candidate file or folder was found.</summary>
    NoSuitableFileFound,
    /// <summary>All attempted downloads failed.</summary>
    AllDownloadsFailed,
    /// <summary>Failure did not match a more specific reason.</summary>
    Other,
    /// <summary>Input extraction failed.</summary>
    ExtractionFailed,
    /// <summary>Job was cancelled.</summary>
    Cancelled,
}

/// <summary>
/// Action identifiers used by ResourceActionDto.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerResourceActionKind>))]
public enum ServerResourceActionKind
{
    /// <summary>Resource can be cancelled through the supplied HTTP method and URL.</summary>
    [JsonStringEnumMemberName(ServerProtocol.ResourceActionKinds.Cancel)]
    Cancel,
}

/// <summary>
/// Stable string values used by server wire formats and JSON discriminators.
/// Prefer the typed protocol enums for normal DTO fields in .NET code.
/// </summary>
public static class ServerProtocol
{
    /// <summary>
    /// Runtime job kind wire values used by JSON serialization and polymorphic discriminators.
    /// Prefer <see cref="ServerJobKind"/> in .NET consumer code.
    /// </summary>
    public static class JobKinds
    {
        public const string Extract = "extract";
        public const string Search = "search";
        public const string Song = "song";
        public const string Album = "album";
        public const string Aggregate = "aggregate";
        public const string AlbumAggregate = "album-aggregate";
        public const string JobList = "job-list";
        public const string RetrieveFolder = "retrieve-folder";
        public const string Generic = "generic";
    }

    /// <summary>
    /// Submission-only discriminators used by job-list draft payloads.
    /// Some draft kinds map to the same runtime kind.
    /// </summary>
    public static class JobDraftKinds
    {
        /// <summary>Draft for <see cref="SubmitExtractJobRequestDto"/>.</summary>
        public const string Extract = "extract";
        /// <summary>Draft for a track-oriented search job.</summary>
        public const string TrackSearch = "search-track";
        /// <summary>Draft for an album-oriented search job.</summary>
        public const string AlbumSearch = "search-album";
        /// <summary>Draft for a single-file download job.</summary>
        public const string Song = "song";
        /// <summary>Draft for a folder or album download job.</summary>
        public const string Album = "album";
        /// <summary>Draft for an aggregate track search job.</summary>
        public const string Aggregate = "aggregate";
        /// <summary>Draft for an aggregate album search job.</summary>
        public const string AlbumAggregate = "album-aggregate";
        /// <summary>Draft for a nested job list.</summary>
        public const string JobList = "job-list";
    }

    /// <summary>
    /// Compatibility aliases for <see cref="ServerJobState"/> values.
    /// Prefer <see cref="ServerJobState"/> directly in new .NET code.
    /// </summary>
    public static class JobStates
    {
        public const ServerJobState Pending = ServerJobState.Pending;
        public const ServerJobState Done = ServerJobState.Done;
        public const ServerJobState Failed = ServerJobState.Failed;
        public const ServerJobState AlreadyExists = ServerJobState.AlreadyExists;
        public const ServerJobState NotFoundLastTime = ServerJobState.NotFoundLastTime;
        public const ServerJobState Skipped = ServerJobState.Skipped;
        public const ServerJobState Searching = ServerJobState.Searching;
        public const ServerJobState Downloading = ServerJobState.Downloading;
        public const ServerJobState Extracting = ServerJobState.Extracting;
    }

    /// <summary>
    /// Compatibility aliases for <see cref="ServerFailureReason"/> values.
    /// Prefer <see cref="ServerFailureReason"/> directly in new .NET code.
    /// </summary>
    public static class FailureReasons
    {
        public const ServerFailureReason None = ServerFailureReason.None;
        public const ServerFailureReason InvalidSearchString = ServerFailureReason.InvalidSearchString;
        public const ServerFailureReason OutOfDownloadRetries = ServerFailureReason.OutOfDownloadRetries;
        public const ServerFailureReason NoSuitableFileFound = ServerFailureReason.NoSuitableFileFound;
        public const ServerFailureReason AllDownloadsFailed = ServerFailureReason.AllDownloadsFailed;
        public const ServerFailureReason Other = ServerFailureReason.Other;
        public const ServerFailureReason ExtractionFailed = ServerFailureReason.ExtractionFailed;
        public const ServerFailureReason Cancelled = ServerFailureReason.Cancelled;
    }

    /// <summary>
    /// Action identifiers used by <see cref="ResourceActionDto"/>.
    /// </summary>
    public static class ResourceActionKinds
    {
        /// <summary>Resource can be cancelled through the supplied HTTP method and URL.</summary>
        public const string Cancel = "cancel";
    }
}

public static class ServerProtocolEnumExtensions
{
    public static string ToWireString(this ServerJobKind kind)
        => kind switch
        {
            ServerJobKind.Extract => ServerProtocol.JobKinds.Extract,
            ServerJobKind.Search => ServerProtocol.JobKinds.Search,
            ServerJobKind.Song => ServerProtocol.JobKinds.Song,
            ServerJobKind.Album => ServerProtocol.JobKinds.Album,
            ServerJobKind.Aggregate => ServerProtocol.JobKinds.Aggregate,
            ServerJobKind.AlbumAggregate => ServerProtocol.JobKinds.AlbumAggregate,
            ServerJobKind.JobList => ServerProtocol.JobKinds.JobList,
            ServerJobKind.RetrieveFolder => ServerProtocol.JobKinds.RetrieveFolder,
            ServerJobKind.Generic => ServerProtocol.JobKinds.Generic,
            _ => kind.ToString(),
        };
}
