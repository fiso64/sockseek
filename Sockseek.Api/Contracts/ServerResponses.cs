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
    SoulseekConnectionDto? Connection = null);

/// <summary>File reload state. Restart-required settings remain at their startup values.</summary>
public sealed record ConfigurationStatusDto(bool Watching, bool RestartRequired, bool ReloadFailed,
    string? Error = null, int? ErrorLine = null);

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
    bool HasDownloadSettings);

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
/// Lightweight job list item. Fetch JobDetailDto for a selected job's typed payload.
/// </summary>
/// <param name="Kind">Stable job kind.</param>
/// <param name="LifecycleState">High-level lifecycle state.</param>
/// <param name="ActivityPhase">Current activity phase for non-terminal jobs.</param>
/// <param name="TerminalOutcome">Terminal result when LifecycleState is Terminal.</param>
/// <param name="SkipReason">Reason when TerminalOutcome is Skipped.</param>
/// <param name="FailureReason">Stable failure reason when TerminalOutcome is failed or cancelled.</param>
/// <param name="CancellationSource">Source of a cancellation outcome, when known.</param>
/// <param name="ParentJobId">Execution parent. Parent cancellation propagates to this job.</param>
/// <param name="ResultJobId">For extract jobs, the semantic result job produced by extraction.</param>
/// <param name="SourceJobId">Provenance link for independently submitted follow-up jobs, such as downloads started from search results.</param>
/// <param name="AvailableActions">Actions currently valid for this job.</param>
/// <param name="PrintOption">Effective print mode for this job, when print-only behavior is active.</param>
/// <param name="DiscoveryPublicFileCount">Raw public files observed by a search job; null for non-search jobs or unavailable history.</param>
/// <param name="DiscoveryObservedPeerCount">Distinct exact peer identities that supplied public or locked files. Locked-only peers count.</param>
/// <param name="SearchDefaultProjection">Accepted default search presentation, when retained; null for other jobs or unavailable definitions.</param>
/// <param name="SearchGroupResults">Accepted group presentation preference, when retained; null for other jobs or unavailable definitions.</param>
/// <param name="Stats">Compact per-kind counts from the job owner; absent values must not be inferred from loaded children.</param>
public sealed record JobSummaryDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    ServerJobKind Kind,
    ServerJobLifecycleState LifecycleState,
    ServerJobActivityPhase ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    ServerJobTerminalOutcome TerminalOutcome,
    ServerJobSkipReason SkipReason,
    string? ItemName,
    string? QueryText,
    ServerJobFailureReason? FailureReason,
    string? FailureMessage,
    Guid? ParentJobId,
    Guid? ResultJobId,
    Guid? SourceJobId,
    int? DiscoveryRawResultCount,
    int? DiscoveryLockedFileCount,
    IReadOnlyList<string> AppliedAutoProfiles,
    IReadOnlyList<ResourceActionDto> AvailableActions,
    string? FailureDetail = null,
    ServerJobCancellationSource CancellationSource = ServerJobCancellationSource.None,
    ServerPrintOption PrintOption = ServerPrintOption.None,
    Guid? SubmissionId = null,
    ServerJobRole Role = ServerJobRole.UserRoot,
    DateTimeOffset? CreatedAtUtc = null,
    int? DiscoveryPublicFileCount = null,
    int? DiscoveryObservedPeerCount = null,
    JobSummaryStatsDto? Stats = null,
    ServerSearchDefaultProjectionKind? SearchDefaultProjection = null,
    bool? SearchGroupResults = null,
    string? SourceDetail = null)
{
    public JobSummaryDto()
        : this(
            Guid.Empty,
            0,
            Guid.Empty,
            ServerJobKind.Generic,
            ServerJobLifecycleState.Pending,
            ServerJobActivityPhase.None,
            null,
            ServerJobTerminalOutcome.None,
            ServerJobSkipReason.None,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            null,
            ServerJobCancellationSource.None)
    {
    }

    public JobSummaryDto(
        Guid JobId,
        int DisplayId,
        Guid WorkflowId,
        ServerJobKind Kind,
        ServerJobLifecycleState LifecycleState,
        ServerJobActivityPhase ActivityPhase,
        DateTimeOffset? ActivityUntilUtc,
        ServerJobTerminalOutcome TerminalOutcome,
        string? ItemName,
        string? QueryText,
        ServerJobFailureReason? FailureReason,
        string? FailureMessage,
        Guid? ParentJobId,
        Guid? ResultJobId,
        Guid? SourceJobId,
        int? DiscoveryRawResultCount,
        int? DiscoveryLockedFileCount,
        IReadOnlyList<string> AppliedAutoProfiles,
        IReadOnlyList<ResourceActionDto> AvailableActions,
        string? FailureDetail = null)
        : this(
            JobId,
            DisplayId,
            WorkflowId,
            Kind,
            LifecycleState,
            ActivityPhase,
            ActivityUntilUtc,
            TerminalOutcome,
            ServerJobSkipReason.None,
            ItemName,
            QueryText,
            FailureReason,
            FailureMessage,
            ParentJobId,
            ResultJobId,
            SourceJobId,
            DiscoveryRawResultCount,
            DiscoveryLockedFileCount,
            AppliedAutoProfiles,
            AvailableActions,
            FailureDetail)
    {
    }

}

/// <summary>
/// Fixed-size selected-job snapshot. Direct children are listed through the jobs collection.
/// SourceJobAvailable indicates whether its source is still present.
/// </summary>
public sealed record JobDetailDto(
    JobSummaryDto Summary,
    JobPayloadDto? Payload,
    int ChildCount,
    bool SourceJobAvailable = false);

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
