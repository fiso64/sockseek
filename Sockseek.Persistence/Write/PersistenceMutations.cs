using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.Write;

public enum PersistenceMutationPriority
{
    Progress,
    Ordinary,
    Structural,
    Terminal,
}

public abstract record PersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid EntityId,
    long Revision,
    PersistenceMutationPriority Priority)
{
    public long OccurredAtUnixMilliseconds => OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
}

/// <summary>
/// Captured job facts. PayloadJson encodes immutable input after coalescing and
/// revision checks, before write access; a null payload clears stored detail.
/// Lazy preserves the same bytes or encoding failure through exact replay.
/// </summary>
public sealed record JobPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid JobId,
    long Revision,
    PersistenceMutationPriority Priority,
    Guid WorkflowId,
    Guid? ParentJobId,
    Guid? SourceJobId,
    Guid? ResultJobId,
    long DisplayId,
    string Kind,
    string LifecycleState,
    string ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    string TerminalOutcome,
    string SkipReason,
    string CancellationSource,
    string FailureReason,
    string? FailureMessage,
    string? FailureDetail,
    string? ItemName,
    string? QueryText,
    int PayloadSchemaVersion,
    Lazy<string?>? PayloadJson,
    Guid? SubmissionId = null,
    string SemanticRole = "UserRoot",
    DateTimeOffset? RegisteredAtUtc = null,
    string? SubmissionSpecificationJson = null,
    Guid? RerunOfSubmissionId = null,
    Guid? PreviewId = null,
    string? ArtifactId = null,
    DateTimeOffset? StartedAtUtc = null,
    DirectoryManifestSnapshot? DirectoryManifest = null,
    string? SourceDetail = null)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, JobId, Revision, Priority);

public sealed record TransferPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid TransferId,
    long Revision,
    PersistenceMutationPriority Priority,
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string State,
    string TerminalOutcome,
    long TotalBytes,
    long TransferredBytes,
    int AttemptCount,
    string FailureReason,
    string? FailureMessage,
    string CancellationSource = "None",
    DateTimeOffset? RequestedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? LastProgressAtUtc = null,
    long? BytesPerSecond = null,
    TransferFileMetadataSnapshot? File = null,
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    IReadOnlyList<TransferAccountingObservation>? AccountingObservations = null,
    string? GroupRelativePath = null,
    DateTimeOffset? CompletedAtUtc = null)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, TransferId, Revision, Priority);

/// <summary>
/// One cumulative transport-byte observation for an attempt. Revision is the
/// owning transfer revision, which makes replay idempotent independently of the
/// transfer/attempt history projection revisions.
/// </summary>
public sealed record TransferAccountingObservation(
    Guid AttemptId,
    long Revision,
    DateTimeOffset OccurredAtUtc,
    long CumulativeBytes);

public sealed record TransferAttemptPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid AttemptId,
    long Revision,
    PersistenceMutationPriority Priority,
    Guid TransferId,
    int AttemptNumber,
    string Source,
    string State,
    string? SourceUsername,
    string? SourcePath,
    string? OutputPath,
    string FailureReason,
    string? FailureMessage,
    string Direction = "Download",
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    IReadOnlyList<TransferAccountingObservation>? AccountingObservations = null,
    DateTimeOffset? StartedAtUtc = null)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, AttemptId, Revision, Priority);

public sealed record SearchResultsPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid SearchJobId,
    long Revision,
    IReadOnlyList<SearchProjectionInput> Results)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, SearchJobId, Revision, PersistenceMutationPriority.Ordinary);

public sealed record SearchCompletionPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid SearchJobId,
    long Revision,
    string Query,
    long ResultCount,
    long LockedFileCount,
    string ResultPersistenceState,
    long ObservedPeerCount = 0)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, SearchJobId, Revision, PersistenceMutationPriority.Terminal);

public sealed record SearchIncompletePersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid SearchJobId,
    long Revision,
    string Reason)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, SearchJobId, Revision, PersistenceMutationPriority.Terminal);

public sealed record TransferTerminalPersistenceMutation(
    TransferPersistenceMutation Transfer,
    TransferAttemptPersistenceMutation? FinalAttempt,
    JobPersistenceMutation? OwningJob)
    : PersistenceMutation(
        Transfer.RuntimeId,
        Transfer.Sequence,
        Transfer.OccurredAtUtc,
        Transfer.TransferId,
        Transfer.Revision,
        PersistenceMutationPriority.Terminal);
