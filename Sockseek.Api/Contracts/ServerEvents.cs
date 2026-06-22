using Sockseek.Core;
using System.Text.Json.Serialization;

namespace Sockseek.Api;

/// <summary>
/// SignalR event envelope. Correct UI state must be reconstructable from HTTP snapshots alone;
/// events are for invalidation, progress, and live activity.
/// </summary>
/// <param name="Category">state, activity, or progress.</param>
/// <param name="SnapshotInvalidation">
/// True when clients should refresh relevant HTTP snapshots.
/// </param>
public sealed record ServerEventEnvelopeDto(
    long Sequence,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string Category,
    bool SnapshotInvalidation,
    Guid? WorkflowId,
    object Payload);

/// <summary>
/// Batched workflow-scoped SignalR update. Clients should apply state fields first,
/// activity entries second, and progress last.
/// </summary>
// TODO [ARCHITECTURE][GUI-EVENT-DELTAS]: Before GUI work, replace this summary-heavy
// SignalR contract with a snapshot + compact delta replication protocol.
//
// Desired model:
// - HTTP endpoints provide full snapshots for startup and recovery.
// - SignalR batches provide ordered, compact deltas only.
// - Add sparse patch DTOs such as WorkflowPatchDto, JobPatchDto, TransferPatchDto,
//   and SearchPatchDto. `JobPatchDto` should carry only changed fields like
//   lifecycle/activity/outcome, discovery counts, names, failure info, or actions.
// - Use full JobSummaryDto payloads only for startup snapshots, newly appeared jobs,
//   or rare explicit row replacement, not for every activity/terminal/progress edge.
// - Convert durable UI state changes into state patches, not activity/log events.
//   This includes `job.activity-changed`, `song.state-changed`, `job.started`,
//   album selection/download-start/folder-retrieval state, transfer state, and
//   discovery counts. Clients must be able to reconstruct the UI from an HTTP
//   snapshot plus ordered patches without replaying log-style activity events.
// - Keep compact semantic activity events only for non-state, user-visible edges:
//   job messages, diagnostics, download-attempt failures, extraction/listing text,
//   and other plain-mode log lines. These events should carry jobId/workflowId plus
//   the few fields needed to format the message, never a full JobSummaryDto.
// - Represent transfer state as coalescible TransferPatchDto values instead of many
//   `download.state-changed` activity events. `download.started` and terminal
//   transfer patches should carry only jobId plus small display/reference fields
//   like username, remote filename, output path, or a candidate id/hash. Full
//   FileCandidateDto, AlbumFolderDto, and SongJobPayloadDto metadata belongs in
//   snapshots, detail endpoints, or explicit result endpoints.
// - Stop nesting full ServerEventEnvelopeDto objects inside workflow batches; the
//   batch already has sequence/workflow/timestamp metadata.
// - Let EngineStateStore compute patches from previous/current snapshots and let
//   ServerEventCoalescer merge patches per job/transfer/search during the flush window.
// - Make WorkflowClientStore apply initial snapshots plus ordered patches. Local CLI,
//   remote CLI, and future GUI should render from that same client-side state store;
//   sequence gaps should trigger HTTP snapshot recovery.
public sealed record WorkflowUpdateBatchDto(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid WorkflowId,
    WorkflowSummaryDto? Workflow,
    IReadOnlyList<JobSummaryDto> JobUpserts,
    IReadOnlyList<SearchUpdatedDto> SearchUpdates,
    IReadOnlyList<DownloadProgressEventDto> Progress,
    IReadOnlyList<ServerEventEnvelopeDto> Activity);

/// <summary>
/// Machine-readable event catalog entry. SnapshotInvalidation=true means refresh the relevant
/// HTTP snapshot instead of maintaining state from activity events.
/// </summary>
public sealed record ServerEventDescriptorDto(
    string Type,
    string Category,
    bool SnapshotInvalidation,
    string PayloadDto);

/// <summary>
/// State event emitted when a search result view revision changes.
/// </summary>
public sealed record SearchUpdatedDto(
    Guid JobId,
    Guid WorkflowId,
    int Revision,
    int ResultCount,
    bool IsComplete);

/// <summary>
/// Diagnostic event emitted when an exception-backed failure has detailed context.
/// </summary>
public sealed record DiagnosticErrorEventDto(
    string Scope,
    string Message,
    string ExceptionType,
    string Exception,
    JobSummaryDto? Summary = null,
    Guid? WorkflowId = null,
    string? Source = null);

/// <summary>
/// Activity event emitted when extraction starts.
/// </summary>
public sealed record ExtractionStartedEventDto(
    JobSummaryDto Summary,
    string Input,
    string? InputType,
    string? Source = null);

/// <summary>
/// Activity event emitted when extraction fails before producing a result job.
/// </summary>
public sealed record ExtractionFailedEventDto(
    JobSummaryDto Summary,
    string Reason,
    string? Source = null);

/// <summary>
/// Activity event emitted when a job begins engine execution.
/// </summary>
public sealed record JobStartedEventDto(
    JobSummaryDto Summary);


/// <summary>
/// Activity event carrying transient human-readable job status text.
/// </summary>
public sealed record JobStatusEventDto(
    JobSummaryDto Summary,
    string Status);

/// <summary>
/// Activity event carrying a job-scoped log message.
/// </summary>
public sealed record JobMessageEventDto(
    JobSummaryDto Summary,
    string Level,
    string? Source,
    string Message);

/// <summary>
/// Activity event carrying a workflow-scoped jobs log message.
/// </summary>
public sealed record WorkflowMessageEventDto(
    Guid WorkflowId,
    string Level,
    string? Source,
    string Message);

/// <summary>
/// Activity event emitted when a job changes its current phase.
/// </summary>
public sealed record JobActivityChangedEventDto(
    JobSummaryDto Summary);

/// <summary>
/// Activity event emitted when a song search begins.
/// </summary>
public sealed record SongSearchingEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);



/// <summary>
/// Activity event emitted when a file transfer starts.
/// </summary>
public sealed record DownloadStartedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    FileCandidateDto Candidate);

/// <summary>
/// Coalesced progress event for an active file transfer.
/// </summary>
public sealed record DownloadProgressEventDto(
    Guid JobId,
    Guid WorkflowId,
    long BytesTransferred,
    long TotalBytes);

/// <summary>
/// Activity event carrying the lower-level transfer state.
/// </summary>
public sealed record DownloadStateChangedEventDto(
    Guid JobId,
    Guid WorkflowId,
    string State);

/// <summary>
/// Activity event emitted immediately when a low-level transfer attempt throws.
/// </summary>
public sealed record DownloadAttemptFailedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    FileCandidateDto Candidate,
    string OutputPath,
    int Attempt,
    int MaxAttempts,
    string ExceptionType,
    string ExceptionMessage,
    string Exception);

/// <summary>
/// Activity event emitted when a song job changes state.
/// </summary>
public sealed record SongStateChangedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    ServerJobLifecycleState LifecycleState,
    ServerJobActivityPhase ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    ServerJobTerminalOutcome TerminalOutcome,
    ServerJobSkipReason SkipReason,
    ServerJobFailureReason? FailureReason,
    string? DownloadPath,
    FileCandidateDto? ChosenCandidate,
    int? DiscoveryRawResultCount = null,
    int? DiscoveryLockedFileCount = null,
    string? FailureMessage = null,
    ServerJobCancellationSource CancellationSource = ServerJobCancellationSource.None)
{
    public SongStateChangedEventDto()
        : this(
            Guid.Empty,
            0,
            Guid.Empty,
            new SongQueryDto(null, null, null, null, null, false),
            ServerJobLifecycleState.Pending,
            ServerJobActivityPhase.None,
            null,
            ServerJobTerminalOutcome.None,
            ServerJobSkipReason.None,
            null,
            null,
            null)
    {
    }

    public SongStateChangedEventDto(
        Guid JobId,
        int DisplayId,
        Guid WorkflowId,
        SongQueryDto Query,
        ServerJobLifecycleState LifecycleState,
        ServerJobActivityPhase ActivityPhase,
        DateTimeOffset? ActivityUntilUtc,
        ServerJobTerminalOutcome TerminalOutcome,
        ServerJobFailureReason? FailureReason,
        string? DownloadPath,
        FileCandidateDto? ChosenCandidate,
        int? DiscoveryRawResultCount = null,
        int? DiscoveryLockedFileCount = null,
        string? FailureMessage = null,
        ServerJobCancellationSource CancellationSource = ServerJobCancellationSource.None)
        : this(
            JobId,
            DisplayId,
            WorkflowId,
            Query,
            LifecycleState,
            ActivityPhase,
            ActivityUntilUtc,
            TerminalOutcome,
            ServerJobSkipReason.None,
            FailureReason,
            DownloadPath,
            ChosenCandidate,
            DiscoveryRawResultCount,
            DiscoveryLockedFileCount,
            FailureMessage,
            CancellationSource)
    {
    }

}

/// <summary>
/// Activity event emitted when an album download begins for a selected folder.
/// </summary>
public sealed record AlbumDownloadStartedEventDto(
    JobSummaryDto Summary,
    AlbumFolderDto Folder,
    IReadOnlyList<SongJobPayloadDto>? Tracks = null);

/// <summary>
/// Activity event emitted when an album starts downloading folder tracks.
/// </summary>
public sealed record AlbumTrackDownloadStartedEventDto(
    JobSummaryDto Summary,
    AlbumFolderDto Folder,
    IReadOnlyList<SongJobPayloadDto>? Tracks = null);

/// <summary>
/// Activity event emitted when an album job reaches a terminal state
/// (Done, AlreadyExists, Failed, Skipped). Mirrors SongStateChangedEventDto.
/// </summary>
public sealed record AlbumStateChangedEventDto(
    JobSummaryDto Summary,
    string? DownloadPath = null);

/// <summary>
/// Activity event emitted once per rate-limit window when the search semaphore is exhausted.
/// </summary>
public sealed record SearchRateLimitedEventDto(DateTimeOffset ResetsAt);

/// <summary>
/// Activity event emitted when the rate-limit window resets and searching resumes.
/// </summary>
public sealed record SearchResumedEventDto();

/// <summary>
/// Activity event emitted when a job starts retrieving full folder contents.
/// </summary>
public sealed record JobFolderRetrievingEventDto(
    JobSummaryDto Summary);

/// <summary>
/// Activity event used by CLI-style track listing output after skip checks.
/// </summary>
public sealed record TrackBatchResolvedEventDto(
    JobSummaryDto Summary,
    bool IsNormal,
    PrintOption PrintOption,
    int PendingCount,
    int ExistingCount,
    int NotFoundCount,
    IReadOnlyList<SongJobPayloadDto> Pending,
    IReadOnlyList<SongJobPayloadDto> Existing,
    IReadOnlyList<SongJobPayloadDto> NotFound);
