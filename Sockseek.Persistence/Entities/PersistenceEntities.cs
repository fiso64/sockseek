namespace Sockseek.Persistence.Entities;

// History mutations accept changes only at a newer revision. Unchanged/replayed
// rows retain theirs, so the writer need not snapshot every column again.
internal interface IRevisionedHistoryRow
{
    long Revision { get; }
}

internal sealed class RuntimeSessionEntity
{
    public Guid Id { get; set; }
    public long StartedAtUtc { get; set; }
    public long? StoppedAtUtc { get; set; }
    public string? ShutdownKind { get; set; }
    public string Version { get; set; } = "";
}

internal sealed class JobEntity : IRevisionedHistoryRow
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid? SubmissionId { get; set; }
    public string SemanticRole { get; set; } = "Legacy";
    public Guid? ParentJobId { get; set; }
    public Guid? SourceJobId { get; set; }
    public Guid? ResultJobId { get; set; }
    public Guid LastRuntimeId { get; set; }
    public long LastSequence { get; set; }
    public long DisplayId { get; set; }
    public string Kind { get; set; } = "";
    public string LifecycleState { get; set; } = "";
    public string ActivityPhase { get; set; } = "";
    public long? ActivityUntilUtc { get; set; }
    public string TerminalOutcome { get; set; } = "";
    public string SkipReason { get; set; } = "";
    public string CancellationSource { get; set; } = "";
    public string FailureReason { get; set; } = "";
    public string? FailureMessage { get; set; }
    public string? FailureDetail { get; set; }
    public string? ItemName { get; set; }
    public string? QueryText { get; set; }
    public string? SourceDetail { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public long Revision { get; set; }
    public int PayloadSchemaVersion { get; set; }
    public string? PayloadJson { get; set; }
}

internal sealed class SubmissionEntity
{
    public Guid Id { get; set; }
    public long SubmittedAtUtc { get; set; }
    public int SpecificationSchemaVersion { get; set; }
    public string SpecificationJson { get; set; } = "";
    public Guid? RerunOfSubmissionId { get; set; }
    public Guid? PreviewId { get; set; }
    public string? ArtifactId { get; set; }
    public string? CommitFingerprint { get; set; }
    public string? CommitReceiptJson { get; set; }
    public long Revision { get; set; }
    public long? ArchivedAtUtc { get; set; }
}

internal sealed class SearchJobEntity
{
    public Guid JobId { get; set; }
    public string Query { get; set; } = "";
    public long Revision { get; set; }
    public long ResultCount { get; set; }
    public long LockedFileCount { get; set; }
    public long ObservedPeerCount { get; set; }
    public bool IsComplete { get; set; }
    public long? CompletedAtUtc { get; set; }
    public string ResultPersistenceState { get; set; } = "NotPersisted";
    public long? ResultsPrunedAtUtc { get; set; }
}

internal sealed class SearchResultEntity
{
    public Guid SearchJobId { get; set; }
    public long Sequence { get; set; }
    public long Revision { get; set; }
    public string Username { get; set; } = "";
    public string RemoteFilename { get; set; } = "";
    public long SizeBytes { get; set; }
    public int? BitRate { get; set; }
    public int? BitDepth { get; set; }
    public int ResponseFileCount { get; set; }
    public int? SampleRate { get; set; }
    public int? DurationSeconds { get; set; }
    public string Extension { get; set; } = "";
    public int? UploadSpeed { get; set; }
    public bool? HasFreeUploadSlot { get; set; }
    public int? QueueLength { get; set; }
    public string Visibility { get; set; } = "Public";
    public string? AttributesJson { get; set; }
    public long ObservedAtUtc { get; set; }
}

internal sealed class TransferEntity : IRevisionedHistoryRow
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public Guid? WorkflowId { get; set; }
    public Guid LastRuntimeId { get; set; }
    public long LastSequence { get; set; }
    public string Direction { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Username { get; set; }
    public string? RemotePath { get; set; }
    public string? LocalPath { get; set; }
    public string State { get; set; } = "";
    public string TerminalOutcome { get; set; } = "";
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public int AttemptCount { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? LastProgressAtUtc { get; set; }
    public long? BytesPerSecond { get; set; }
    public long? CompletedAtUtc { get; set; }
    public string FailureReason { get; set; } = "";
    public string? FailureMessage { get; set; }
    public string CancellationSource { get; set; } = "None";
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileExtension { get; set; }
    public int? FileBitRate { get; set; }
    public int? FileBitDepth { get; set; }
    public int? FileSampleRate { get; set; }
    public int? FileLength { get; set; }
    public string? FileAttributesJson { get; set; }
    public string? GroupRef { get; set; }
    public string? GroupDisplayPath { get; set; }
    public string? GroupRelativePath { get; set; }
    public long? ArchivedAtUtc { get; set; }
    public long Revision { get; set; }
}

internal sealed class TransferAttemptEntity : IRevisionedHistoryRow
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public Guid LastRuntimeId { get; set; }
    public long LastSequence { get; set; }
    public int AttemptNumber { get; set; }
    public string Source { get; set; } = "";
    public string State { get; set; } = "";
    public string? SourceUsername { get; set; }
    public string? SourcePath { get; set; }
    public string? OutputPath { get; set; }
    public long StartedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public string FailureReason { get; set; } = "";
    public string? FailureMessage { get; set; }
    public long Revision { get; set; }
}

internal sealed class TransferAccountingCheckpointEntity : IRevisionedHistoryRow
{
    public Guid AttemptId { get; set; }
    public Guid TransferId { get; set; }
    public long Revision { get; set; }
    public long CumulativeBytes { get; set; }
    public long LastObservedAtUtc { get; set; }
}

internal sealed class TransferByteBucketEntity
{
    public long BucketStartUtc { get; set; }
    public string Direction { get; set; } = "";
    public string Username { get; set; } = "";
    public long Bytes { get; set; }
}

internal sealed class TransferAccountingStateEntity
{
    public int Id { get; set; }
    public long CompleteFromUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}

internal sealed class PeerRestrictionOverrideEntity
{
    public string RestrictionKind { get; set; } = "";
    public string Username { get; set; } = "";
    public string OverrideState { get; set; } = "";
    public long UpdatedAtUtc { get; set; }
}

internal sealed class InputArtifactEntity
{
    public string Id { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Length { get; set; }
    public long CreatedAtUtc { get; set; }
    public long ExpiresAtUtc { get; set; }
    public string? OriginalName { get; set; }
}

internal sealed class ChatConversationEntity
{
    public Guid Id { get; set; }
    public string LocalAccountKey { get; set; } = "";
    public string PeerKey { get; set; } = "";
    public string DisplayUsername { get; set; } = "";
    public long? ArchivedAtUtc { get; set; }
    public long LastReadSequence { get; set; }
    public long LastMessageSequence { get; set; }
    public long Revision { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}

internal sealed class ChatSequenceEntity
{
    public int Id { get; set; }
    public long LastMessageSequence { get; set; }
    public long LastNotificationSequence { get; set; }
}

internal sealed class ChatRoomSubscriptionEntity
{
    public Guid Id { get; set; }
    public string LocalAccountKey { get; set; } = "";
    public string RoomKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool RuntimeDesired { get; set; }
    public string Kind { get; set; } = "Unknown";
    public long LastReadSequence { get; set; }
    public long LastMessageSequence { get; set; }
    public long Revision { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}

internal sealed class ChatMessageEntity
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string LocalAccountKey { get; set; } = "";
    public string TargetKind { get; set; } = "";
    public Guid TargetId { get; set; }
    public string TargetKey { get; set; } = "";
    public string DisplayTarget { get; set; } = "";
    public string SenderKey { get; set; } = "";
    public string DisplaySender { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Body { get; set; } = "";
    public long OccurredAtUtc { get; set; }
    public long RecordedAtUtc { get; set; }
    public string SendState { get; set; } = "";
    public string? FailureReason { get; set; }
    public int? ProtocolMessageId { get; set; }
    public long? ProtocolTimestamp { get; set; }
}

internal sealed class NotificationEntity
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string LocalAccountKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid SourceMessageId { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? ReadAtUtc { get; set; }
}
