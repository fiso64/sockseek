using Sockseek.Core;

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
/// Activity event emitted when extraction starts.
/// </summary>
public sealed record ExtractionStartedEventDto(
    JobSummaryDto Summary,
    string Input,
    string? InputType);

/// <summary>
/// Activity event emitted when extraction fails before producing a result job.
/// </summary>
public sealed record ExtractionFailedEventDto(
    JobSummaryDto Summary,
    string Reason);

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
    ServerJobState State,
    ServerFailureReason? FailureReason,
    string? DownloadPath,
    FileCandidateDto? ChosenCandidate,
    int? DiscoveryResultCount = null,
    int? DiscoveryLockedFileCount = null,
    string? FailureMessage = null);

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
/// Activity event emitted when on-complete work starts for a song.
/// </summary>
public sealed record OnCompleteStartedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

/// <summary>
/// Activity event emitted when on-complete work finishes for a song.
/// </summary>
public sealed record OnCompleteEndedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

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
