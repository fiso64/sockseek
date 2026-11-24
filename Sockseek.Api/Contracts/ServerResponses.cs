using System.Text.Json.Serialization;

namespace Sockseek.Api;

/// <summary>
/// Basic daemon identity.
/// </summary>
public sealed record ServerInfoDto(
    string Name,
    string Version,
    DateTimeOffset StartedAtUtc,
    int LiveProtocolVersion);

/// <summary>
/// Current daemon and engine activity counters.
/// </summary>
public sealed record ServerStatusDto(
    SoulseekClientStatusDto SoulseekClient,
    int TotalJobCount,
    int ActiveJobCount,
    int TotalWorkflowCount,
    int ActiveWorkflowCount,
    int RestartCount,
    PersistenceStatusDto? Persistence = null,
    ConfigurationStatusDto? Configuration = null,
    SoulseekConnectionDto? Connection = null,
    LocalUserProfileDto? Profile = null,
    ProcessMemoryDto? Memory = null);

/// <summary>
/// Memory held by the daemon process. The three parts add up to
/// <see cref="ResidentBytes"/> and are reported on Linux only. A service
/// manager's figure differs: it also counts file cache.
/// </summary>
/// <param name="ResidentBytes">Memory currently in RAM.</param>
/// <param name="PeakResidentBytes">The highest resident size since the process started.</param>
/// <param name="HeapBytes">The daemon's own objects and native allocations.</param>
/// <param name="RuntimeBytes">The runtime's type data and compiled code.</param>
/// <param name="MappedFileBytes">Libraries and databases mapped from disk, which the system can reclaim.</param>
/// <param name="SwappedBytes">Memory the system moved to swap.</param>
public sealed record ProcessMemoryDto(
    long ResidentBytes,
    long PeakResidentBytes,
    long? HeapBytes = null,
    long? RuntimeBytes = null,
    long? MappedFileBytes = null,
    long? SwappedBytes = null);

/// <summary>
/// The profile this daemon shows to Soulseek peers. PictureUrl changes with the
/// picture, so clients can cache it; it is null when no picture is configured.
/// </summary>
public sealed record LocalUserProfileDto(string Description, string? PictureUrl);

/// <summary>File reload state. Restart-required settings remain at their startup values.</summary>
public sealed record ConfigurationStatusDto(bool Watching, bool RestartRequired, bool ReloadFailed,
    string? Error = null, int? ErrorLine = null, int Revision = 0);

public sealed record PersistenceStatusDto(
    bool Enabled,
    bool Initialized,
    string State,
    string? SchemaVersion,
    Guid? RuntimeId,
    DateTimeOffset? RuntimeStartedAtUtc,
    DateTimeOffset? LastSuccessfulCommitAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? LastFailure,
    int CriticalQueueDepth,
    int OrdinaryQueueDepth,
    int ProgressEntityCount,
    int ProgressEntityCapacity,
    int BufferedSearchResultCount,
    int RetryMutationCount,
    long BusyRetryCount,
    long DroppedProgressCount,
    long SuccessfulCommitCount,
    long RowsWritten,
    long? DatabaseSizeBytes = null,
    long? WalSizeBytes = null,
    double? LastCommitDurationMilliseconds = null,
    int? LastBatchMutationCount = null,
    long PermanentlyFailedMutationCount = 0,
    IReadOnlyList<long>? CommitLatencyHistogram = null,
    IReadOnlyList<long>? BatchSizeHistogram = null,
    int ReconciledUnfinishedRuntimeCount = 0,
    int ReconciledInterruptedJobCount = 0,
    int ReconciledInterruptedTransferCount = 0,
    int ReconciledInterruptedAttemptCount = 0,
    int ReconciledInterruptedSearchCount = 0,
    DateTimeOffset? LastRetentionAtUtc = null,
    int LastRetentionPrunedJobs = 0,
    int LastRetentionPrunedSearchResults = 0,
    int LastRetentionPrunedTransfers = 0,
    int LastRetentionPrunedTransferAttempts = 0);

/// <summary>
/// Current Soulseek client connection state.
/// </summary>
/// <param name="State">Combined Soulseek.NET client state string.</param>
/// <param name="Flags">Individual Soulseek.NET state flag names.</param>
/// <param name="IsReady">True when the client is both connected and logged in.</param>
public sealed record SoulseekClientStatusDto(
    string State,
    IReadOnlyList<string> Flags,
    bool IsReady);

/// <summary>
/// User-visible summary of a configured profile.
/// </summary>
public sealed record ProfileSummaryDto(
    string Name,
    string? Condition,
    bool IsAutoProfile,
    bool HasEngineSettings,
    bool HasDownloadSettings,
    bool HasInputSettings = false,
    string? Input = null,
    string? InputType = null,
    string? DetectedInputType = null);

/// <summary>
/// Error response body for rejected API requests.
/// </summary>
public sealed record ApiErrorDto(
    string Error,
    string? Code = null);

/// <summary>
/// Response body returned when cancelling a workflow.
/// </summary>
public sealed record CancelWorkflowResponseDto(
    int Cancelled);

/// <summary>
/// Response body returned when cancelling all currently cancellable daemon jobs.
/// </summary>
public sealed record CancelJobsResponseDto(
    int Cancelled);

/// <summary>
/// Discoverable mutation affordance. Clients should prefer this over hard-coding job states.
/// </summary>
/// <param name="Kind">Action kind, for example ServerProtocol.ResourceActionKinds.Cancel.</param>
/// <param name="Method">HTTP method to invoke.</param>
/// <param name="Href">Server-relative URL for the action.</param>
public sealed record ResourceActionDto(
    ServerResourceActionKind Kind,
    string Method,
    string Href);

/// <summary>Kind-specific counts for list-row presentation; unavailable or inapplicable values are null.</summary>
public sealed record JobSummaryStatsDto(
    int? MemberCount = null,
    int? CompletedMemberCount = null,
    int? FailedMemberCount = null,
    int? CandidateCount = null,
    int? FolderCount = null,
    int? FileCount = null,
    long? KnownBytes = null,
    int? NewFilesFoundCount = null,
    double? ProgressPercent = null,
    int? SkippedMemberCount = null,
    int? DownloadedMemberCount = null,
    int? ReusedMemberCount = null);

/// <summary>
/// Fixed-size selected-job snapshot. Direct children are listed through the jobs collection.
/// SourceJobAvailable indicates whether its source is still present.
/// </summary>
public sealed record JobDetailDto(
    JobSummaryDto Summary,
    JobPayloadDto? Payload,
    int ChildCount,
    bool SourceJobAvailable = false,
    JobRepeatOptionsDto? RepeatOptions = null);

/// <summary>
/// Workflow list item summarizing related jobs submitted under one workflow id.
/// </summary>
public sealed record WorkflowSummaryDto(
    Guid WorkflowId,
    string Title,
    ServerWorkflowState State,
    int RootJobCount,
    int ActiveJobCount,
    int FailedJobCount,
    int CompletedJobCount);

/// <summary>
/// Fixed-size workflow detail. Jobs are listed through the jobs collection.
/// </summary>
public sealed record WorkflowDetailDto(
    WorkflowSummaryDto Summary);

/// <summary>
/// Query parameters for listing jobs.
/// </summary>
/// <param name="IncludeAll">
/// When true, includes every matching job as a flat list. Default lists return only execution roots where ParentJobId is null.
/// </param>
/// <param name="NewestFirst">Return newest display IDs first; continuation cursors are bound to the selected direction.</param>
/// <param name="ExcludeExtractResults">Exclude generated jobs represented by their extraction source, before paging.</param>
/// <param name="ExcludeKinds">Omit these job kinds before paging, including live updates.</param>
public sealed record JobQuery(
    ServerJobLifecycleState? LifecycleState,
    ServerJobTerminalOutcome? TerminalOutcome,
    ServerJobKind? Kind,
    Guid? WorkflowId,
    bool IncludeAll,
    ServerJobSkipReason? SkipReason = null,
    Guid? ParentJobId = null,
    Guid? SubmissionId = null,
    ServerJobRole? Role = null,
    bool NewestFirst = false,
    bool ExcludeExtractResults = false,
    IReadOnlyList<ServerJobKind>? ExcludeKinds = null);

/// <summary>Read compact summaries for linked jobs without hydrating their full details.</summary>
public sealed record JobLookupRequestDto(IReadOnlyList<Guid> JobIds);

/// <summary>Current network session; contains no credentials.</summary>
public sealed record SoulseekConnectionDto(string? Username, string? ServerAddress, int? ServerPort, string? ServerEndpoint, int? ListenPort);

/// <param name="CanRunAgain">The job can be run again from its saved command.</param>
/// <param name="CanExtractAgain">
/// Running it again can also read its source afresh, which may give different
/// items. False when there is no source to read, or it was an uploaded file.
/// </param>
/// <param name="CanEditAgain">The job's request can be read back as a draft to change and submit again.</param>
public sealed record JobRepeatOptionsDto(bool CanRunAgain, int MissingFiles, Guid? RetryTransferId = null,
    bool CanExtractAgain = false, bool CanEditAgain = false);

/// <summary>
/// What was asked for when a job was submitted, in the form a new submission
/// takes. Edits made to individual items during review are not part of it.
/// </summary>
/// <param name="ProfileNames">Profiles that were selected for the job and still exist.</param>
/// <param name="DownloadSettings">Where the job's settings differ from what those profiles give now.</param>
public sealed record JobRerunDraftDto(
    JobDraftDto Job,
    IReadOnlyList<string> ProfileNames,
    DownloadSettingsPatchDto? DownloadSettings = null);
