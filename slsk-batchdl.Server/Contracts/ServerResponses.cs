namespace Sldl.Server;

/// <summary>
/// Basic daemon identity.
/// </summary>
public sealed record ServerInfoDto(
    string Name,
    string Version,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// Current daemon and engine activity counters.
/// </summary>
public sealed record ServerStatusDto(
    SoulseekClientStatusDto SoulseekClient,
    int TotalJobCount,
    int ActiveJobCount,
    int TotalWorkflowCount,
    int ActiveWorkflowCount,
    int RestartCount);

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
    string Error);

/// <summary>
/// Response body returned when cancelling a workflow.
/// </summary>
public sealed record CancelWorkflowResponseDto(
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

/// <summary>
/// Lightweight job list item. Fetch JobDetailDto for a selected job's typed payload.
/// </summary>
/// <param name="Kind">Stable job kind.</param>
/// <param name="State">Stable job state.</param>
/// <param name="FailureReason">Stable failure reason when State is failed.</param>
/// <param name="ParentJobId">Execution parent. Parent cancellation propagates to this job.</param>
/// <param name="ResultJobId">For extract jobs, the semantic result job produced by extraction.</param>
/// <param name="SourceJobId">Provenance link for independently submitted follow-up jobs, such as downloads started from search results.</param>
/// <param name="AvailableActions">Actions currently valid for this job.</param>
public sealed record JobSummaryDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    ServerJobKind Kind,
    ServerJobState State,
    string? ItemName,
    string? QueryText,
    ServerFailureReason? FailureReason,
    string? FailureMessage,
    Guid? ParentJobId,
    Guid? ResultJobId,
    Guid? SourceJobId,
    int? DiscoveryResultCount,
    int? DiscoveryLockedFileCount,
    IReadOnlyList<string> AppliedAutoProfiles,
    IReadOnlyList<ResourceActionDto> AvailableActions);

/// <summary>
/// Selected-job snapshot: summary, typed payload, and direct child summaries for client navigation.
/// </summary>
public sealed record JobDetailDto(
    JobSummaryDto Summary,
    JobPayloadDto? Payload,
    IReadOnlyList<JobSummaryDto> Children);

/// <summary>
/// Workflow list item summarizing related jobs submitted under one workflow id.
/// </summary>
public sealed record WorkflowSummaryDto(
    Guid WorkflowId,
    string Title,
    ServerWorkflowState State,
    IReadOnlyList<Guid> RootJobIds,
    int ActiveJobCount,
    int FailedJobCount,
    int CompletedJobCount);

/// <summary>
/// Workflow snapshot containing execution-root job summaries unless IncludeAll is requested.
/// </summary>
public sealed record WorkflowDetailDto(
    WorkflowSummaryDto Summary,
    IReadOnlyList<JobSummaryDto> Jobs);

/// <summary>
/// Recursive execution tree node built from ParentJobId relationships.
/// </summary>
public sealed record WorkflowJobNodeDto(
    JobSummaryDto Summary,
    IReadOnlyList<WorkflowJobNodeDto> Children);

/// <summary>
/// Workflow snapshot shaped as an execution tree.
/// </summary>
public sealed record WorkflowTreeDto(
    WorkflowSummaryDto Summary,
    IReadOnlyList<WorkflowJobNodeDto> Jobs);

/// <summary>
/// Query parameters for listing jobs.
/// </summary>
/// <param name="IncludeAll">
/// When true, includes every matching job as a flat list. Default lists return only execution roots where ParentJobId is null.
/// </param>
public sealed record JobQuery(
    ServerJobState? State,
    ServerJobKind? Kind,
    Guid? WorkflowId,
    bool IncludeAll);
