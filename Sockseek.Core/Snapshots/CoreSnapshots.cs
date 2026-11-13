using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Core.Events;

namespace Sockseek.Core.Snapshots;

public enum JobSnapshotKind
{
    Generic,
    Extract,
    Search,
    Song,
    Album,
    SongGroups,
    AlbumGroups,
    JobList,
    RetrieveFolder,
    RemoteFile,
    RemoteDirectory,
}

public sealed record DiscoverySnapshot(
    int RawResultCount,
    int LockedFileCount,
    int ObservedPeerCount = 0);

public sealed record SongQuerySnapshot(
    string? Artist,
    string? Title,
    string? Album,
    string? URI,
    int? Length,
    bool ArtistMaybeWrong);

public sealed record AlbumQuerySnapshot(
    string? Artist,
    string? Album,
    string? SearchHint,
    string? URI,
    bool ArtistMaybeWrong);

public sealed record FileAttributeSnapshot(string Type, int Value, int StableCode = 0);

public sealed record PeerDirectoryIdentitySnapshot(string Username, string FolderPath);

// Exact file targets and output paths already own immutable data. Snapshots retain
// those values directly; routine progress must not rebuild a second representation.
public sealed record PeerDirectoryResultSnapshot(
    PeerDirectoryIdentitySnapshot Identity,
    IReadOnlyList<PeerFileTarget> Files,
    bool IsComplete);

public sealed record DirectoryTransferEntrySnapshot(
    PeerFileTarget Target,
    IReadOnlyList<string> RelativeDirectoryComponents);

public sealed record DirectoryTransferPlanSnapshot(
    string DisplayRoot,
    IReadOnlyList<DirectoryTransferEntrySnapshot> Entries,
    long TotalKnownBytes,
    bool RepresentsDirectorySelection = true,
    PeerDirectoryIdentitySnapshot? SourceDirectory = null);

public sealed record PeerSnapshot(
    string Username,
    bool? HasFreeUploadSlot,
    int? UploadSpeed,
    int? QueueLength = null,
    DateTimeOffset? ObservedAtUtc = null);

public sealed record FileCandidateSnapshot(
    string Username,
    string Filename,
    PeerSnapshot Peer,
    long Size,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    string Extension,
    IReadOnlyList<FileAttributeSnapshot>? Attributes,
    SearchResultVisibility Visibility = SearchResultVisibility.Public,
    int? ResponseFileCount = null);

public sealed record AlbumFileSnapshot(SongQuerySnapshot Query, FileCandidateSnapshot Candidate)
{
    public string Filename => Candidate.Filename;
}

public sealed record AlbumFolderSnapshot(
    string Username,
    string FolderPath,
    PeerSnapshot Peer,
    int SearchFileCount,
    int SearchAudioFileCount,
    IReadOnlyList<AlbumFileSnapshot> Files,
    bool IsFullyRetrieved);

public sealed record AlbumFolderSummarySnapshot(
    string Username, string FolderPath, PeerSnapshot Peer,
    int SearchFileCount, int SearchAudioFileCount, bool IsFullyRetrieved)
{
    public AlbumFolderSnapshot ToSnapshot()
        => new(Username, FolderPath, Peer, SearchFileCount, SearchAudioFileCount, [], IsFullyRetrieved);
}

public enum TransferSnapshotDirection
{
    Download,
    Upload,
}

public enum TransferSnapshotSource
{
    SoulseekPeer,
    Fallback,
}

public enum TransferSnapshotTerminalOutcome
{
    None,
    Succeeded,
    Cancelled,
    Failed,
    Interrupted,
}

/// <summary>
/// Reusable presentation-safe metadata for a transfer's exact file. Transfer
/// identity remains in the surrounding snapshot; fallback or genuinely
/// unknown files leave this value null.
/// </summary>
public sealed record TransferFileMetadataSnapshot(
    string Name,
    long Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<FileAttributeSnapshot>? Attributes = null);

// Directory selections remain folder cards even with one member. Files groups
// collect independent transfers and may be presented as a single file.
public enum TransferGroupKind { Directory, Files }

public sealed record TransferSnapshot(
    Guid Id,
    TransferSnapshotDirection Direction,
    TransferSnapshotSource Source,
    Guid? JobId,
    Guid? WorkflowId,
    long Revision,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string? CandidateKey,
    string? State,
    long BytesTransferred,
    long TotalBytes,
    int AttemptCount,
    FileCandidateSnapshot? Candidate,
    PeerFileTarget? Target = null,
    DateTimeOffset? RequestedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? LastProgressAtUtc = null,
    long? BytesPerSecond = null,
    TransferSnapshotTerminalOutcome TerminalOutcome = TransferSnapshotTerminalOutcome.None,
    TransferFailureReason? FailureReason = null,
    TransferCancellationReason? CancellationReason = null,
    TransferFileMetadataSnapshot? File = null,
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    string? GroupRelativePath = null,
    DateTimeOffset? CompletedAtUtc = null,
    TransferGroupKind GroupKind = TransferGroupKind.Directory);

public sealed record FileSearchProjectionSnapshot(SongQuerySnapshot Query, bool IncludeFullResults);

public sealed record FolderSearchProjectionSnapshot(AlbumQuerySnapshot Query, bool IncludeFiles);

public closed record JobSnapshotPayload;

/// <summary>Execution data cannot contain job trees, plans or directory listings.</summary>
public closed record JobExecutionPayload : JobSnapshotPayload;

public sealed record FileDownloadStateSnapshot(
    string? DownloadPath,
    long BytesTransferred,
    long? FileSize,
    ExistingFileSource? ExistingFileSource = null,
    bool Reused = false);

public sealed record DirectoryDownloadStateSnapshot(
    string Phase,
    int? AttemptNumber,
    string? DownloadPath,
    int FileCount,
    int TerminalFileCount,
    int SuccessfulFileCount,
    int FailedFileCount,
    long BytesTransferred,
    long TotalKnownBytes,
    int UnknownFileSizeCount,
    double? Progress = null,
    ExistingFileSource? ExistingFileSource = null,
    int SkippedFileCount = 0,
    int DownloadedFileCount = 0,
    int ReusedFileCount = 0);

public sealed record ExtractJobSnapshotPayload(
    string Input,
    string? InputType,
    Guid? ResultJobId,
    bool AutoProcessResult) : JobExecutionPayload;

public sealed record SearchJobSnapshotPayload(
    string QueryText,
    FileSearchProjectionSnapshot? DefaultFileProjection,
    FolderSearchProjectionSnapshot? DefaultFolderProjection,
    int ResultCount,
    int Revision,
    bool IsComplete,
    SearchDefinition? Definition) : JobExecutionPayload;

public sealed record SongJobSnapshotPayload(
    SongQuerySnapshot Query,
    int? CandidateCount,
    FileCandidateSnapshot? ResolvedTarget,
    PeerFileTarget? ExactTarget,
    SongDownloadSource DownloadSource,
    FileDownloadStateSnapshot File,
    SearchDefinition? Definition,
    FileCandidateSnapshot? LastAttemptedCandidate = null) : JobExecutionPayload;

public sealed record AlbumJobSnapshotPayload(
    AlbumQuerySnapshot Query,
    int ResultCount,
    AlbumFolderSnapshot? ResolvedTarget,
    IReadOnlyList<JobSnapshot> TrackJobs,
    DirectoryDownloadStateSnapshot Directory,
    SearchDefinition? Definition,
    DirectoryManifestSnapshot? Manifest = null) : JobSnapshotPayload;

/// <summary>
/// The exact accepted directory attempt, including entries without registered
/// child jobs. Supplemental work is separate from the immutable plan prefix.
/// </summary>
public sealed record DirectoryManifestSnapshot(
    int AttemptNumber,
    DirectoryTransferPlanSnapshot Plan,
    bool ChildrenMaterialized,
    IReadOnlyList<Guid> EntryJobIds,
    IReadOnlyList<Guid> SupplementalJobIds,
    IReadOnlyList<PeerFileTarget?>? SupplementalTargets = null);

public sealed record RemoteFileJobSnapshotPayload(
    PeerFileTarget Target,
    RelativeOutputPath OutputPath,
    FileDownloadStateSnapshot File) : JobExecutionPayload;

public enum RemoteDirectorySourceSnapshotKind
{
    PeerDirectory,
    Resolved,
}

public sealed record RemoteDirectoryJobSnapshotPayload(
    RemoteDirectorySourceSnapshotKind SourceKind,
    PeerDirectoryIdentitySnapshot? DirectorySource,
    DirectoryTransferPlanSnapshot? ResolvedPlanSource,
    PeerDirectoryResultSnapshot? ResolvedDirectory,
    DirectoryManifestSnapshot? Manifest,
    IReadOnlyList<JobSnapshot> FileJobs,
    DirectoryDownloadStateSnapshot Directory) : JobSnapshotPayload;

public sealed record SongGroupsJobSnapshotPayload(
    SongQuerySnapshot Query,
    IReadOnlyList<JobSnapshot> Songs,
    SearchDefinition? Definition,
    JobOutcomeSummary Outcomes) : JobSnapshotPayload;

public readonly record struct JobOutcomeSummary(
    int Total,
    int Completed,
    int Succeeded,
    int Failed,
    int Skipped = 0)
{
    public JobOutcomeSummary Include(
        JobLifecycleState lifecycle,
        JobTerminalOutcome outcome,
        JobSkipReason skipReason,
        int count = 1)
        => new(
            checked(Total + count),
            checked(Completed + (lifecycle == JobLifecycleState.Terminal ? count : 0)),
            checked(Succeeded + (IsSuccessful(outcome, skipReason) ? count : 0)),
            checked(Failed + (outcome != JobTerminalOutcome.Skipped && IsFailedOrSkipped(outcome, skipReason) ? count : 0)),
            checked(Skipped + (outcome == JobTerminalOutcome.Skipped ? count : 0)));

    public JobOutcomeSummary Exclude(
        JobLifecycleState lifecycle,
        JobTerminalOutcome outcome,
        JobSkipReason skipReason,
        int count = 1)
        => new(
            checked(Total - count),
            checked(Completed - (lifecycle == JobLifecycleState.Terminal ? count : 0)),
            checked(Succeeded - (IsSuccessful(outcome, skipReason) ? count : 0)),
            checked(Failed - (outcome != JobTerminalOutcome.Skipped && IsFailedOrSkipped(outcome, skipReason) ? count : 0)),
            checked(Skipped - (outcome == JobTerminalOutcome.Skipped ? count : 0)));

    public static bool IsSuccessful(JobTerminalOutcome outcome, JobSkipReason skipReason)
        => outcome == JobTerminalOutcome.Succeeded
            || (outcome == JobTerminalOutcome.Skipped && skipReason == JobSkipReason.AlreadyExists);

    public static bool IsFailedOrSkipped(JobTerminalOutcome outcome, JobSkipReason skipReason)
        => outcome is JobTerminalOutcome.Failed
                or JobTerminalOutcome.Cancelled
                or JobTerminalOutcome.PartialSuccess
            || (outcome == JobTerminalOutcome.Skipped && skipReason != JobSkipReason.AlreadyExists);
}

public sealed record AlbumGroupsJobSnapshotPayload(
    AlbumQuerySnapshot Query,
    JobOutcomeSummary Albums,
    SearchDefinition? Definition) : JobExecutionPayload;

public sealed record JobListSnapshotPayload(
    int Count,
    IReadOnlyList<JobSnapshot> Jobs,
    JobOutcomeSummary? LogicalOutcomes = null) : JobSnapshotPayload;

public sealed record RetrieveFolderJobSnapshotPayload(
    PeerDirectoryIdentitySnapshot Directory,
    PeerDirectoryResultSnapshot? Result,
    int NewFilesFoundCount,
    FolderRetrievalOutcome RetrievalOutcome,
    bool RetrievalCancelled) : JobSnapshotPayload;

public sealed record GenericJobSnapshotPayload(string Text) : JobExecutionPayload;

/// <summary>
/// Identity and display context for notifications. Deliberately has no payload:
/// emitting a message or progress count must not capture a descendant graph.
/// </summary>
public sealed record JobReferenceSnapshot(
    Guid Id,
    int DisplayId,
    Guid WorkflowId,
    JobSnapshotKind Kind,
    long Revision,
    string? ItemName,
    PrintOption PrintOption);

public record JobSnapshotHeader(
    Guid Id,
    int DisplayId,
    Guid WorkflowId,
    Guid? SubmissionId,
    JobSemanticRole SemanticRole,
    DateTimeOffset? CreatedAtUtc,
    string? SubmissionSpecificationJson,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
    JobSnapshotKind Kind,
    long Revision,
    JobLifecycleState LifecycleState,
    JobActivityPhase ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    JobTerminalOutcome TerminalOutcome,
    JobSkipReason SkipReason,
    JobCancellationSource CancellationSource,
    JobFailureReason FailureReason,
    string? FailureMessage,
    string? FailureDetail,
    string? ItemName,
    string? QueryText,
    DiscoverySnapshot? Discovery,
    IReadOnlyList<Sockseek.Core.Settings.AppliedProfile> AppliedProfiles,
    PrintOption PrintOption,
    bool CanCancel,
    string? SourceDetail = null);

public sealed record JobSnapshot : JobSnapshotHeader
{
    public JobSnapshot(JobSnapshotHeader header, JobSnapshotPayload payload) : base(header)
        => Payload = payload;

    public JobSnapshotPayload Payload { get; init; }
}

/// <summary>
/// A point-in-time execution update. Its payload types have no descendant graphs
/// or manifests. Structural snapshots own those separately; merging reuses them.
/// </summary>
public sealed record JobExecutionSnapshot : JobSnapshotHeader
{
    public JobExecutionSnapshot(JobSnapshotHeader header, JobExecutionPayload payload) : base(header)
        => Payload = payload;

    public JobExecutionPayload Payload { get; init; }

    public JobSnapshot ApplyTo(JobSnapshot? previous)
        => new(this, (Payload, previous?.Payload) switch
        {
            (AlbumExecutionPayload current, AlbumJobSnapshotPayload old) => old with
            {
                Query = current.Query, ResultCount = current.ResultCount,
                ResolvedTarget = current.ResolvedTarget?.ToSnapshot(), Directory = current.Directory, Definition = current.Definition,
            },
            (RemoteDirectoryExecutionPayload current, RemoteDirectoryJobSnapshotPayload old)
                => old with { Directory = current.Directory },
            (SongGroupsExecutionPayload current, SongGroupsJobSnapshotPayload old)
                => old with { Query = current.Query, Definition = current.Definition, Outcomes = current.Outcomes },
            (JobListExecutionPayload current, JobListSnapshotPayload old)
                => old with { Count = current.Outcomes.Total, LogicalOutcomes = current.Outcomes },
            (RetrieveFolderExecutionPayload current, RetrieveFolderJobSnapshotPayload old) => old with
            {
                NewFilesFoundCount = current.NewFilesFoundCount, RetrievalOutcome = current.RetrievalOutcome,
                RetrievalCancelled = current.RetrievalCancelled,
            },
            _ => Payload,
        });
}

public sealed record AlbumExecutionPayload(
    AlbumQuerySnapshot Query, int ResultCount, AlbumFolderSummarySnapshot? ResolvedTarget,
    DirectoryDownloadStateSnapshot Directory, SearchDefinition? Definition) : JobExecutionPayload;

public sealed record RemoteDirectoryExecutionPayload(
    RemoteDirectorySourceSnapshotKind SourceKind, PeerDirectoryIdentitySnapshot? DirectorySource,
    string? SourceUsername, string? SourceFolderPath, string? DisplayRoot,
    DirectoryDownloadStateSnapshot Directory) : JobExecutionPayload;

public sealed record SongGroupsExecutionPayload(
    SongQuerySnapshot Query, SearchDefinition? Definition, JobOutcomeSummary Outcomes) : JobExecutionPayload;

public sealed record JobListExecutionPayload(JobOutcomeSummary Outcomes) : JobExecutionPayload;

public sealed record RetrieveFolderExecutionPayload(
    PeerDirectoryIdentitySnapshot Directory, int NewFilesFoundCount,
    FolderRetrievalOutcome RetrievalOutcome, bool RetrievalCancelled) : JobExecutionPayload;

internal static class SnapshotCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> source)
        => Array.AsReadOnly(source.ToArray());

    public static IReadOnlyList<T> Empty<T>() => Array.AsReadOnly(Array.Empty<T>());
}
