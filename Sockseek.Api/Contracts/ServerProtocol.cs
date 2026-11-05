using System.Text.Json.Serialization;

namespace Sockseek.Api;

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
    /// <summary>Ordinary exact peer-file download job.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.RemoteFile)]
    RemoteFile,
    /// <summary>Ordinary peer-directory download job.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.RemoteDirectory)]
    RemoteDirectory,
    /// <summary>Fallback kind for unknown or unmapped core jobs.</summary>
    [JsonStringEnumMemberName(ServerProtocol.JobKinds.Generic)]
    Generic,
}

/// <summary>Stable semantic role within one accepted submission.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobRole>))]
public enum ServerJobRole
{
    [JsonStringEnumMemberName(ServerProtocol.JobRoles.UserRoot)]
    UserRoot,
    [JsonStringEnumMemberName(ServerProtocol.JobRoles.SemanticResult)]
    SemanticResult,
    [JsonStringEnumMemberName(ServerProtocol.JobRoles.Orchestration)]
    Orchestration,
    [JsonStringEnumMemberName(ServerProtocol.JobRoles.ExecutionChild)]
    ExecutionChild,
}

/// <summary>High-level runtime lifecycle returned by job, workflow, and live-state DTOs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobLifecycleState>))]
public enum ServerJobLifecycleState
{
    Pending,
    Running,
    AwaitingSelection,
    Terminal,
}

/// <summary>Current runtime activity for non-terminal jobs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobActivityPhase>))]
public enum ServerJobActivityPhase
{
    None,
    WaitingForSearchConcurrency,
    SearchRateLimited,
    Searching,
    ProcessingSearchResults,
    Extracting,
    Downloading,
    RetrievingFolder,
    RunningChildren,
    Organizing,
    RunningOnComplete,
    RunningFallback,
}

/// <summary>Terminal result for completed jobs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobTerminalOutcome>))]
public enum ServerJobTerminalOutcome
{
    None,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    PartialSuccess,
}

/// <summary>Source that produced a terminal song download.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerSongDownloadSource>))]
public enum ServerSongDownloadSource
{
    None,
    Soulseek,
    Fallback,
}

/// <summary>Reason a terminal job was skipped.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobSkipReason>))]
public enum ServerJobSkipReason
{
    None,
    AlreadyExists,
    NotFoundLastTime,
    Manual,
    Filtered,
}

/// <summary>
/// Source of a terminal cancellation outcome. This is job-level provenance, not every
/// lower-level cancellation token used by a download attempt.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobCancellationSource>))]
public enum ServerJobCancellationSource
{
    /// <summary>The job is not cancelled, or the source was not assigned.</summary>
    None,
    /// <summary>The user explicitly cancelled this single job.</summary>
    UserRequestedJob,
    /// <summary>The job was cancelled because its parent job was cancelled.</summary>
    ParentJob,
    /// <summary>The user cancelled the workflow that owns this job.</summary>
    UserRequestedWorkflow,
    /// <summary>The user requested global cancellation of all engine jobs.</summary>
    UserRequestedAllJobs,
    /// <summary>
    /// The engine cancelled the job internally, for example because an engine-owned
    /// timeout/watchdog caused a terminal cancellation.
    /// </summary>
    InternalEngine,
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
/// Stable failure reason returned by job and live-state DTOs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerJobFailureReason>))]
public enum ServerJobFailureReason
{
    /// <summary>No failure reason.</summary>
    None,
    /// <summary>The job could not form a valid search string.</summary>
    InvalidSearchString,
    /// <summary>The job exhausted download retry attempts.</summary>
    OutOfDownloadRetries,
    /// <summary>All attempted downloads failed.</summary>
    AllDownloadsFailed,
    /// <summary>Failure did not match a more specific reason.</summary>
    Other,
    /// <summary>Input extraction failed.</summary>
    ExtractionFailed,
    /// <summary>Job was cancelled.</summary>
    Cancelled,
    /// <summary>One or more child jobs failed and no child completed successfully.</summary>
    ChildJobsFailed,
    /// <summary>The Soulseek search returned no results.</summary>
    NoSearchResults,
    /// <summary>Soulseek returned results, but none matched the requested filters or projection.</summary>
    NoMatchingResults,
}

/// <summary>
/// Outcome of a retrieve-folder job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerFolderRetrievalOutcome>))]
public enum ServerFolderRetrievalOutcome
{
    /// <summary>Folder retrieval has not finished yet.</summary>
    [JsonStringEnumMemberName(ServerProtocol.FolderRetrievalOutcomes.None)]
    None,
    /// <summary>Folder retrieval completed successfully.</summary>
    [JsonStringEnumMemberName(ServerProtocol.FolderRetrievalOutcomes.Completed)]
    Completed,
    /// <summary>Folder retrieval was cancelled.</summary>
    [JsonStringEnumMemberName(ServerProtocol.FolderRetrievalOutcomes.Cancelled)]
    Cancelled,
    /// <summary>Folder retrieval failed for a non-cancellation reason.</summary>
    [JsonStringEnumMemberName(ServerProtocol.FolderRetrievalOutcomes.Failed)]
    Failed,
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
    /// <summary>Daemon stream position captured before a paged history read.</summary>
    public const string ReadEpochHeader = "X-Sockseek-Read-Epoch";
    public const string ReadSequenceHeader = "X-Sockseek-Read-Sequence";

    /// <summary>Default collection size for flat items; callers may request more.</summary>
    public const int DefaultPageSize = 500;

    /// <summary>Default collection size for items with nested presentation data.</summary>
    public const int DefaultNestedPageSize = 100;

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
        public const string RemoteFile = "remote-file";
        public const string RemoteDirectory = "remote-directory";
        public const string Generic = "generic";
    }

    public static class JobRoles
    {
        public const string UserRoot = "user-root";
        public const string SemanticResult = "semantic-result";
        public const string Orchestration = "orchestration";
        public const string ExecutionChild = "execution-child";
    }

    /// <summary>
    /// Submission-only discriminators used by job-list draft payloads.
    /// Some draft kinds map to the same runtime kind.
    /// </summary>
    public static class JobDraftKinds
    {
        /// <summary>Draft for a generic file search job.</summary>
        public const string GenericSearch = "search-generic";
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
        public const string RemoteFile = "remote-file";
        public const string RemoteDirectory = "remote-directory";
    }

    /// <summary>
    /// Compatibility aliases for <see cref="ServerJobFailureReason"/> values.
    /// Prefer <see cref="ServerJobFailureReason"/> directly in new .NET code.
    /// </summary>
    public static class FailureReasons
    {
        public const ServerJobFailureReason None = ServerJobFailureReason.None;
        public const ServerJobFailureReason InvalidSearchString = ServerJobFailureReason.InvalidSearchString;
        public const ServerJobFailureReason OutOfDownloadRetries = ServerJobFailureReason.OutOfDownloadRetries;
        public const ServerJobFailureReason AllDownloadsFailed = ServerJobFailureReason.AllDownloadsFailed;
        public const ServerJobFailureReason Other = ServerJobFailureReason.Other;
        public const ServerJobFailureReason ExtractionFailed = ServerJobFailureReason.ExtractionFailed;
        public const ServerJobFailureReason Cancelled = ServerJobFailureReason.Cancelled;
        public const ServerJobFailureReason ChildJobsFailed = ServerJobFailureReason.ChildJobsFailed;
        public const ServerJobFailureReason NoSearchResults = ServerJobFailureReason.NoSearchResults;
        public const ServerJobFailureReason NoMatchingResults = ServerJobFailureReason.NoMatchingResults;
    }

    /// <summary>
    /// Compatibility aliases for <see cref="ServerFolderRetrievalOutcome"/> values.
    /// Prefer <see cref="ServerFolderRetrievalOutcome"/> directly in new .NET code.
    /// </summary>
    public static class FolderRetrievalOutcomes
    {
        public const string None = "none";
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";
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
    /// <summary>Accept the public job-kind spelling in query parameters as well as older CLR enum names.</summary>
    public static bool TryParseJobKind(string value, out ServerJobKind kind)
    {
        foreach (var candidate in Enum.GetValues<ServerJobKind>())
        {
            if (string.Equals(value, candidate.ToWireString(), StringComparison.Ordinal)
                || string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>Use one spelling for role filters and JSON job summaries.</summary>
    public static string ToWireString(this ServerJobRole role)
        => role switch
        {
            ServerJobRole.UserRoot => ServerProtocol.JobRoles.UserRoot,
            ServerJobRole.SemanticResult => ServerProtocol.JobRoles.SemanticResult,
            ServerJobRole.Orchestration => ServerProtocol.JobRoles.Orchestration,
            ServerJobRole.ExecutionChild => ServerProtocol.JobRoles.ExecutionChild,
            _ => role.ToString(),
        };

    public static bool TryParseJobRole(string value, out ServerJobRole role)
    {
        foreach (var candidate in Enum.GetValues<ServerJobRole>())
        {
            if (string.Equals(value, candidate.ToWireString(), StringComparison.Ordinal)
                || string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }

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
            ServerJobKind.RemoteFile => ServerProtocol.JobKinds.RemoteFile,
            ServerJobKind.RemoteDirectory => ServerProtocol.JobKinds.RemoteDirectory,
            ServerJobKind.Generic => ServerProtocol.JobKinds.Generic,
            _ => kind.ToString(),
        };
}
