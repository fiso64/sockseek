using System.Text.Json.Serialization;

namespace Sockseek.Api;

/// <summary>The live replication protocol implemented by this API.</summary>
public static class LiveProtocol
{
    public const int Version = 23;
    public const string HubPath = "api/events";
    public const string StateUpdateBatchMethod = "stateUpdateBatch";
    public const string SubscribeAllMethod = nameof(LiveClientInvocation.SubscribeAll);
    public const string ObserveJobListMethod = nameof(LiveClientInvocation.ObserveJobList);
    public const string ObserveJobsMethod = nameof(LiveClientInvocation.ObserveJobs);
    public const string UnsubscribeAllMethod = nameof(LiveClientInvocation.UnsubscribeAll);
    public const string SubscribeWorkflowMethod = nameof(LiveClientInvocation.SubscribeWorkflow);
    public const string UnsubscribeWorkflowMethod = nameof(LiveClientInvocation.UnsubscribeWorkflow);
    public const string SubscribeChatMethod = nameof(LiveClientInvocation.SubscribeChat);
    public const string UnsubscribeChatMethod = nameof(LiveClientInvocation.UnsubscribeChat);
    public const string SubscribeUserBrowseMethod = nameof(LiveClientInvocation.SubscribeUserBrowse);
    public const string UnsubscribeUserBrowseMethod = nameof(LiveClientInvocation.UnsubscribeUserBrowse);
    public const string ObserveUserPresenceMethod = nameof(LiveClientInvocation.ObserveUserPresence);
    public const string ObserveSearchViewMethod = nameof(LiveClientInvocation.ObserveSearchView);
    public const string ObserveTransferTimelineMethod = nameof(LiveClientInvocation.ObserveTransferTimeline);
}

/// <summary>Overview carries roots and global totals; Full includes every live job and transfer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DaemonStateDetail>))]
public enum DaemonStateDetail { Full, Overview }

/// <summary>
/// Displayed job facts/file progress and direct counts for the open root. The first
/// frame replaces the scope's rows; later frames are revision-ordered deltas.
/// Retained revisions in a fresh scope need not exceed a prior live generation.
/// SourceJobAvailable is present on the first frame and when source availability
/// changes; SourceJobId identifies the source to which it applies.
/// </summary>
public sealed record JobObservationUpdateDto(
    StateDeltaDto State,
    int? ChildCount = null,
    int? RetrievalChildCount = null,
    bool? SourceJobAvailable = null,
    Guid? SourceJobId = null);

/// <summary>
/// Displayed prefix of one job query. Increasing Limit expands the fetched extent;
/// each observation begins with authoritative membership and rows, including on reconnect.
/// </summary>
public sealed record JobListScopeDto(JobQuery Query, int Limit = ServerProtocol.DefaultNestedPageSize);

/// <summary>
/// Initial WindowJobIds replaces the scope, regardless of revisions in an old scope.
/// Later frames patch rows or remove actual scope exits; runtime retirement is invisible.
/// NextCursor follows the current window boundary. Membership changes can move that boundary.
/// With ExcludeExtractResults, LinkedResults carries the summary of each displayed
/// extraction's result job the first time that result is linked in this scope.
/// Observe active results through their own job subscription for later changes.
/// </summary>
public sealed record JobListUpdateDto(
    IReadOnlyList<JobDeltaDto> Jobs,
    IReadOnlyList<Guid> RemovedJobIds,
    string? NextCursor,
    IReadOnlyList<Guid>? WindowJobIds = null,
    IReadOnlyList<JobSummaryDto>? LinkedResults = null);

/// <summary>The version tied to the generated live contract graph.</summary>
public enum LiveProtocolContractVersion
{
    Current = LiveProtocol.Version,
}

/// <summary>The maximum bounded message tail carried by a live chat snapshot.</summary>
public enum ChatLiveMessageTailSize
{
    Current = ChatProtocol.LiveMessageTailSize,
}

[JsonConverter(typeof(JsonStringEnumConverter<LiveClientInvocation>))]
public enum LiveClientInvocation
{
    SubscribeAll,
    UnsubscribeAll,
    SubscribeWorkflow,
    UnsubscribeWorkflow,
    SubscribeChat,
    UnsubscribeChat,
    SubscribeUserBrowse,
    UnsubscribeUserBrowse,
    ObserveUserPresence,
    ObserveSearchView,
    ObserveTransferTimeline,
    ObserveJobs,
    ObserveJobList,
}

[JsonConverter(typeof(JsonStringEnumConverter<LiveServerMessage>))]
public enum LiveServerMessage
{
    [JsonStringEnumMemberName(LiveProtocol.StateUpdateBatchMethod)]
    StateUpdateBatch,
}

[JsonConverter(typeof(JsonStringEnumConverter<StateStreamScopeKind>))]
public enum StateStreamScopeKind
{
    [JsonStringEnumMemberName("daemon")]
    Daemon,
    [JsonStringEnumMemberName("workflow")]
    Workflow,
    [JsonStringEnumMemberName("chatConversation")]
    ChatConversation,
    [JsonStringEnumMemberName("chatRoom")]
    ChatRoom,
    [JsonStringEnumMemberName("userBrowse")]
    UserBrowse,
}

/// <summary>A recoverable live stream with exactly one kind-specific target id.</summary>
public sealed record StateStreamScopeDto(
    StateStreamScopeKind Kind,
    Guid? WorkflowId = null,
    Guid? ChatTargetId = null,
    Guid? UserBrowseId = null)
{
    public static StateStreamScopeDto Daemon { get; } = new(StateStreamScopeKind.Daemon);

    public static StateStreamScopeDto Workflow(Guid workflowId)
        => new(StateStreamScopeKind.Workflow, workflowId);

    public static StateStreamScopeDto ChatConversation(Guid conversationId)
        => new(StateStreamScopeKind.ChatConversation, ChatTargetId: conversationId);

    public static StateStreamScopeDto ChatRoom(Guid roomId)
        => new(StateStreamScopeKind.ChatRoom, ChatTargetId: roomId);

    public static StateStreamScopeDto UserBrowse(Guid browseId)
        => new(StateStreamScopeKind.UserBrowse, UserBrowseId: browseId);

    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
            throw new ArgumentException("The stream scope kind is invalid.");
        if (Kind == StateStreamScopeKind.Daemon
            && (WorkflowId != null || ChatTargetId != null || UserBrowseId != null))
            throw new ArgumentException("A daemon stream scope cannot contain a target id.");
        if (Kind == StateStreamScopeKind.Workflow
            && (WorkflowId is null || WorkflowId == Guid.Empty || ChatTargetId != null || UserBrowseId != null))
            throw new ArgumentException("A workflow stream scope requires only a workflow id.");
        if (Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom
            && (ChatTargetId is null || ChatTargetId == Guid.Empty || WorkflowId != null || UserBrowseId != null))
            throw new ArgumentException("A chat stream scope requires only a chat target id.");
        if (Kind == StateStreamScopeKind.UserBrowse
            && (UserBrowseId is null || UserBrowseId == Guid.Empty || WorkflowId != null || ChatTargetId != null))
            throw new ArgumentException("A user-browse stream scope requires only a browse id.");
    }
}

/// <summary>
/// A stream cursor. The daemon epoch changes on process start; a workflow epoch
/// also changes when a retired workflow ID starts a new live generation. Sequence
/// is scoped independently to the daemon stream or to one workflow generation.
/// </summary>
public sealed record StateStreamPositionDto(Guid Epoch, long Sequence);

/// <summary>
/// Daemon-wide activity totals independent of the rows a client is viewing.
/// Active, Downloads, Uploads, Queued, Failed and rates describe transfers.
/// Job counts include nested jobs in the Running or Pending lifecycle state.
/// </summary>
public sealed record LiveActivitySummaryDto(
    int Active, int Downloads, int Uploads, int Queued, int Failed,
    long DownloadRate, long UploadRate, int RunningJobCount = 0, int PendingJobCount = 0)
{
    public static LiveActivitySummaryDto Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Small daemon state replicated to daemon-wide monitors.</summary>
public sealed record DaemonStateDto(
    long Revision,
    SoulseekClientStatusDto SoulseekClient,
    int RestartCount,
    DateTimeOffset? SearchRateLimitResetsAtUtc,
    SharingStateDto Sharing,
    UploadRuntimeStateDto Uploads,
    ChatRuntimeStateDto Chat,
    NotificationSummaryDto Notifications,
    // Post-commit invalidation for retained transfer analytics. Active progress
    // can also change accounting, so clients keep their periodic fallback.
    long TerminalTransferCommitRevision = 0,
    ConfigurationStatusDto? Configuration = null,
    // Null when the daemon is current, has not checked yet or does not check.
    AvailableUpdateDto? Update = null);

[JsonConverter(typeof(JsonStringEnumConverter<DaemonFeatureState>))]
public enum DaemonFeatureState
{
    Disabled,
    Starting,
    Ready,
    Degraded,
}

public sealed record ShareCatalogStateDto(
    Guid? GenerationId,
    long DirectoryCount,
    long FileCount,
    long TotalBytes,
    bool BrowseAvailable,
    long? BrowseArtifactBytes,
    DateTimeOffset? PublishedAtUtc);

public sealed record ShareScanErrorSampleDto(
    string Code,
    string RelativePath,
    string Message);

public sealed record ShareScanStateDto(
    Guid ScanId,
    long Revision,
    ShareScanPhase Phase,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long DirectoriesDiscovered,
    long FilesDiscovered,
    long BytesDiscovered,
    int ErrorCount,
    IReadOnlyList<ShareScanErrorSampleDto> ErrorSamples,
    IReadOnlyList<ResourceActionDto> AvailableActions);

public sealed record SharingStateDto(
    DaemonFeatureState State,
    string? Reason,
    IReadOnlyList<string> Aliases,
    int UploadBlockedUsernameCount,
    int UploadBlockedIpAddressCount,
    ShareCatalogStateDto Catalog,
    ShareScanStateDto? ActiveScan,
    ShareScanStateDto? LastScan);

public sealed record UploadRuntimeStateDto(
    DaemonFeatureState State,
    string? Reason,
    bool AcceptingUploads,
    int Slots,
    int ActiveSlots,
    int QueuedFiles,
    long QueuedBytes,
    long QueueRevision,
    int? SpeedLimitKiBPerSecond);

public sealed record ChatRuntimeStateDto(
    DaemonFeatureState State,
    string? Reason,
    int DesiredRoomCount,
    int JoinedRoomCount,
    int UnreadPrivateMessageCount,
    int UnreadRoomMessageCount,
    long Revision);

public sealed record NotificationSummaryDto(
    int UnreadCount,
    long Revision);

/// <summary>Fields that identify and label a job and normally remain stable.</summary>
public sealed record JobDisplayFieldsDto(
    int DisplayId,
    Guid WorkflowId,
    ServerJobKind Kind,
    string? ItemName,
    string? QueryText,
    IReadOnlyList<AppliedProfileDto> AppliedProfiles,
    ServerPrintOption PrintOption,
    Guid? SubmissionId,
    ServerJobRole Role,
    DateTimeOffset? CreatedAtUtc,
    SearchDefaultProjectionKind? SearchDefaultProjection = null,
    bool? SearchGroupResults = null,
    string? SourceDetail = null);

/// <summary>
/// A cohesive replacement for job lifecycle state. Nullable failure and timing fields
/// are cleared by sending this component with those fields set to null.
/// </summary>
public sealed record JobLifecycleFieldsDto(
    JobLifecycleState LifecycleState,
    JobActivityPhase ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    JobTerminalOutcome TerminalOutcome,
    JobSkipReason SkipReason,
    JobFailureReason? FailureReason,
    string? FailureMessage,
    bool HasFailureDetail,
    JobCancellationSource CancellationSource,
    IReadOnlyList<ResourceActionDto> AvailableActions);

/// <summary>A cohesive replacement for current discovery counters.</summary>
public sealed record JobDiscoveryFieldsDto(
    int? RawResultCount,
    int? LockedFileCount,
    int? PublicFileCount = null,
    int? ObservedPeerCount = null);

/// <summary>A cohesive replacement for job graph and provenance relationships.</summary>
public sealed record JobRelationshipFieldsDto(
    Guid? ParentJobId,
    Guid? ResultJobId,
    Guid? SourceJobId);

/// <summary>One complete job summary used by HTTP reads and live snapshots.</summary>
public sealed record JobSummaryDto(
    Guid JobId,
    long Revision,
    JobDisplayFieldsDto Display,
    JobLifecycleFieldsDto Lifecycle,
    JobDiscoveryFieldsDto Discovery,
    JobRelationshipFieldsDto Relationships,
    JobSummaryStatsDto? Stats = null);

/// <summary>
/// A new job uses Added. Existing jobs replace only the supplied cohesive components.
/// Revision is the resulting entity revision.
/// </summary>
public sealed record JobDeltaDto(
    Guid JobId,
    long Revision,
    JobSummaryDto? Added = null,
    JobDisplayFieldsDto? Display = null,
    JobLifecycleFieldsDto? Lifecycle = null,
    JobDiscoveryFieldsDto? Discovery = null,
    JobRelationshipFieldsDto? Relationships = null,
    JobSummaryStatsDto? Stats = null);

/// <summary>A complete small workflow row with an API-owned aggregate revision.</summary>
public sealed record WorkflowStateDto(
    long Revision,
    WorkflowSummaryDto Summary);

/// <summary>Latest search projection metadata for one job.</summary>
public sealed record SearchStateDto(
    Guid JobId,
    Guid WorkflowId,
    long Revision,
    int ResultCount,
    bool IsComplete);

/// <summary>
/// Stable source and ownership metadata for a transfer. PeerUploadSpeed is
/// Soulseek-advertised bytes per second, distinct from measured transfer speed.
/// </summary>
public sealed record TransferIdentityFieldsDto(
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? CandidateKey,
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    // Search-response evidence for this exact transfer candidate, not the
    // transfer's measured throughput. Absent for target-only transfers.
    int? PeerUploadSpeed = null,
    bool? PeerHasFreeUploadSlot = null,
    string? GroupRelativePath = null,
    TransferGroupKind GroupKind = TransferGroupKind.Directory,
    FallbackSourceDto? Fallback = null);

/// <summary>
/// Where one transfer is in its lifecycle. An ended transfer describes its
/// result through <see cref="TransferTerminalOutcome"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransferPhase>))]
public enum TransferPhase
{
    /// <summary>Accepted by the daemon; the transport has not reported a state yet.</summary>
    Starting,
    /// <summary>Waiting for a local slot. For uploads, the daemon's own upload queue.</summary>
    QueuedLocally,
    Requested,
    /// <summary>Waiting in the remote peer's upload queue.</summary>
    QueuedRemotely,
    /// <summary>
    /// The peer cannot serve the file now (offline, unreachable or over its
    /// queue limit). The download is kept and asked for again later.
    /// </summary>
    QueuedForPeer,
    Initializing,
    InProgress,
    /// <summary>The bytes arrived; the output is being verified and placed.</summary>
    Finalizing,
    /// <summary>An attempt failed and the transfer has not ended.</summary>
    Retrying,
    Ended,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferTerminalOutcome>))]
public enum TransferTerminalOutcome
{
    None,
    Succeeded,
    Cancelled,
    Failed,
    Interrupted,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferFailureReason>))]
public enum TransferFailureReason
{
    None,
    FileUnavailable,
    FileNoLongerShared,
    FileChanged,
    InvalidOffset,
    Denied,
    PeerDisconnected,
    ConnectionFailed,
    TransferTimedOut,
    InternalError,
    Unknown,
    LocalStorageError,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferCancellationSource>))]
public enum TransferCancellationSource
{
    None,
    User,
    /// <summary>The owning job stopped the transfer; the user did not cancel it.</summary>
    OwningJob,
    Peer,
    DaemonShutdown,
    CatalogInvalidation,
}

/// <summary>A cohesive replacement for the current transfer state.</summary>
public sealed record TransferStatusFieldsDto(
    TransferPhase Phase,
    string? LocalPath,
    int AttemptCount,
    bool IsTerminal,
    TransferTerminalOutcome TerminalOutcome = TransferTerminalOutcome.None,
    TransferFailureReason FailureReason = TransferFailureReason.None,
    TransferCancellationSource CancellationSource = TransferCancellationSource.None,
    IReadOnlyList<ResourceActionDto>? AvailableActions = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? FailureMessage = null,
    DateTimeOffset? RemovedAtUtc = null,
    // A download's place in the peer's upload queue, as the peer last reported
    // it. Present only while the phase is QueuedRemotely and the peer answered.
    int? QueuePosition = null)
{
    public IReadOnlyList<ResourceActionDto> AvailableActions { get; init; } =
        AvailableActions ?? [];
}

public sealed record TransferSchedulingFieldsDto(
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc);

/// <summary>A cohesive replacement for transfer byte progress.</summary>
public sealed record TransferProgressFieldsDto(
    long BytesTransferred,
    long TotalBytes,
    long? BytesPerSecond = null,
    DateTimeOffset? LastProgressAtUtc = null);

/// <summary>A complete transfer row across current state and retained history, keyed by TransferId.</summary>
public sealed record TransferStateDto(
    Guid TransferId,
    long Revision,
    TransferIdentityFieldsDto Identity,
    TransferStatusFieldsDto Status,
    TransferProgressFieldsDto Progress,
    TransferSchedulingFieldsDto? Scheduling = null,
    FileMetadataDto? File = null,
    DateTimeOffset CreatedAtUtc = default);

/// <summary>
/// A new transfer uses Added. Existing transfers replace only the supplied state and
/// progress components. Revision is the resulting entity revision.
/// </summary>
public sealed record TransferDeltaDto(
    Guid TransferId,
    long Revision,
    TransferStateDto? Added = null,
    TransferStatusFieldsDto? Status = null,
    TransferProgressFieldsDto? Progress = null,
    TransferSchedulingFieldsDto? Scheduling = null);

/// <summary>Typed compact state changes carried by one stream batch.</summary>
public sealed record StateDeltaDto(
    DaemonStateDto? Daemon,
    IReadOnlyList<WorkflowStateDto> Workflows,
    IReadOnlyList<JobDeltaDto> Jobs,
    IReadOnlyList<SearchStateDto> Searches,
    IReadOnlyList<TransferDeltaDto> Transfers,
    IReadOnlyList<Guid> RemovedWorkflowIds,
    IReadOnlyList<Guid> RemovedJobIds,
    IReadOnlyList<Guid> RemovedSearchJobIds,
    IReadOnlyList<Guid> RemovedTransferIds,
    IReadOnlyList<UserNotificationDto>? Notifications = null,
    IReadOnlyList<ChatTargetDeltaDto>? ChatTargets = null,
    UserBrowseDto? UserBrowse = null,
    LiveActivitySummaryDto? ActivitySummary = null)
{
    public static StateDeltaDto Empty { get; } = new(null, [], [], [], [], [], [], [], []);

    [JsonIgnore]
    public bool IsEmpty =>
        Daemon == null
        && Workflows.Count == 0
        && Jobs.Count == 0
        && Searches.Count == 0
        && Transfers.Count == 0
        && RemovedWorkflowIds.Count == 0
        && RemovedJobIds.Count == 0
        && RemovedSearchJobIds.Count == 0
        && RemovedTransferIds.Count == 0
        && Notifications is null or { Count: 0 }
        && ChatTargets is null or { Count: 0 }
        && UserBrowse is null
        && ActivitySummary is null;
}

public sealed record ChatTargetDeltaDto(
    ChatTargetKind Kind,
    Guid TargetId,
    ConversationSummaryDto? Conversation = null,
    ChatRoomSummaryDto? Room = null,
    IReadOnlyList<ChatMessageDto>? Messages = null,
    bool ReplaceMessages = false,
    bool? HasEarlierMessages = null,
    bool Removed = false);

public sealed record ChatTargetSnapshotDto(
    ChatTargetKind Kind,
    Guid TargetId,
    ConversationSummaryDto? Conversation,
    ChatRoomSummaryDto? Room,
    IReadOnlyList<ChatMessageDto> Messages,
    bool HasEarlierMessages);

/// <summary>
/// A complete bounded replication snapshot. Daemon snapshots contain active workflows
/// only; retained terminal history is loaded through paginated history endpoints.
/// </summary>
public sealed record StateSnapshotDto(
    StateStreamScopeDto Scope,
    StateStreamPositionDto Position,
    DateTimeOffset CapturedAtUtc,
    DaemonStateDto? Daemon,
    IReadOnlyList<WorkflowStateDto> Workflows,
    IReadOnlyList<JobSummaryDto> Jobs,
    IReadOnlyList<SearchStateDto> Searches,
    IReadOnlyList<TransferStateDto> Transfers,
    ChatTargetSnapshotDto? ChatTarget = null,
    UserBrowseDto? UserBrowse = null,
    LiveActivitySummaryDto? ActivitySummary = null,
    // Last daemon-stream position that can invalidate Jobs history. Compare a
    // Jobs HTTP read position against this, rather than unrelated live updates.
    StateStreamPositionDto? JobHistoryPosition = null);

/// <summary>
/// An ordered stream batch. State is applied before Activity. PreviousSequence permits
/// safe overlap with a concurrently captured HTTP snapshot.
/// </summary>
public sealed record StateUpdateBatchDto(
    StateStreamScopeDto Scope,
    Guid Epoch,
    long PreviousSequence,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    StateDeltaDto State,
    IReadOnlyList<ActivityEventDto> Activity);

/// <summary>An ephemeral, best-effort activity edge. It is never required for state.</summary>
public sealed record ActivityEventDto(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Type,
    Guid? WorkflowId,
    Guid? JobId,
    Guid? TransferId,
    ActivityPayloadDto Payload);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JobStatusActivityDto), "jobStatus")]
[JsonDerivedType(typeof(JobMessageActivityDto), "jobMessage")]
[JsonDerivedType(typeof(WorkflowMessageActivityDto), "workflowMessage")]
[JsonDerivedType(typeof(DiagnosticActivityDto), "diagnostic")]
[JsonDerivedType(typeof(ExtractionStartedActivityDto), "extractionStarted")]
[JsonDerivedType(typeof(DownloadAttemptFailedActivityDto), "downloadAttemptFailed")]
[JsonDerivedType(typeof(TrackBatchResolvedActivityDto), "trackBatchResolved")]
[JsonDerivedType(typeof(SessionChangedActivityDto), "sessionChanged")]
public abstract record ActivityPayloadDto;

public sealed record JobStatusActivityDto(
    int DisplayId,
    string Status) : ActivityPayloadDto;

public sealed record JobMessageActivityDto(
    int DisplayId,
    string Level,
    string? Source,
    string Message) : ActivityPayloadDto;

public sealed record WorkflowMessageActivityDto(
    string Level,
    string? Source,
    string Message) : ActivityPayloadDto;

public sealed record DiagnosticActivityDto(
    int? DisplayId,
    string Scope,
    string Message,
    string ExceptionType,
    string Exception,
    string? Source) : ActivityPayloadDto;

public sealed record ExtractionStartedActivityDto(
    int DisplayId,
    string Input,
    string? InputType,
    string? Source) : ActivityPayloadDto;

/// <summary>
/// The Soulseek session changed (connecting, logged in, lost). Sent on the daemon
/// stream and on every watched workflow stream, so a client following one job
/// can say so. A session that does not change sends nothing.
/// </summary>
public sealed record SessionChangedActivityDto(SoulseekClientStatusDto Session) : ActivityPayloadDto;

public sealed record DownloadAttemptFailedActivityDto(
    int DisplayId,
    string? Username,
    string? RemotePath,
    string OutputPath,
    int Attempt,
    int MaxAttempts,
    string ExceptionType,
    string ExceptionMessage) : ActivityPayloadDto;

public sealed record TrackBatchResolvedActivityDto(
    int DisplayId,
    bool IsNormal,
    ServerPrintOption PrintOption,
    int PendingCount,
    int ExistingCount,
    int NotFoundCount) : ActivityPayloadDto;
