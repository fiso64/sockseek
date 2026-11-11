using System.Text.Json.Serialization;

namespace Sockseek.Api;

/// <summary>
/// Typed job-specific payload carried by JobDetailDto. Switch on the JSON "kind" discriminator
/// or the concrete DTO type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExtractJobPayloadDto), ServerProtocol.JobKinds.Extract)]
[JsonDerivedType(typeof(SearchJobPayloadDto), ServerProtocol.JobKinds.Search)]
[JsonDerivedType(typeof(SongJobPayloadDto), ServerProtocol.JobKinds.Song)]
[JsonDerivedType(typeof(AlbumJobPayloadDto), ServerProtocol.JobKinds.Album)]
[JsonDerivedType(typeof(SongGroupsJobPayloadDto), ServerProtocol.JobKinds.SongGroups)]
[JsonDerivedType(typeof(AlbumGroupsJobPayloadDto), ServerProtocol.JobKinds.AlbumGroups)]
[JsonDerivedType(typeof(JobListPayloadDto), ServerProtocol.JobKinds.JobList)]
[JsonDerivedType(typeof(RetrieveFolderJobPayloadDto), ServerProtocol.JobKinds.RetrieveFolder)]
[JsonDerivedType(typeof(RemoteFileJobPayloadDto), ServerProtocol.JobKinds.RemoteFile)]
[JsonDerivedType(typeof(RemoteDirectoryJobPayloadDto), ServerProtocol.JobKinds.RemoteDirectory)]
[JsonDerivedType(typeof(GenericJobPayloadDto), ServerProtocol.JobKinds.Generic)]
public abstract record JobPayloadDto;

public sealed record PeerFileTargetDto(
    string Username,
    string Filename,
    long? Size,
    string? Extension,
    int? BitRate = null,
    int? BitDepth = null,
    int? SampleRate = null,
    int? Length = null,
    IReadOnlyList<FileAttributeDto>? Attributes = null);

public sealed record DirectoryTransferEntryDto(
    PeerFileTargetDto Target,
    IReadOnlyList<string> RelativeDirectoryComponents);

public sealed record DirectoryTransferPlanDto(
    string DisplayRoot,
    IReadOnlyList<DirectoryTransferEntryDto> Entries,
    long TotalKnownBytes);

/// <summary>
/// One accepted or supplemental file. Child/File may be absent before child registration or
/// after independent child-history pruning; parent totals do not infer its outcome.
/// </summary>
public sealed record DirectoryManifestEntryDto(
    int Ordinal,
    PeerFileTargetDto? Target,
    IReadOnlyList<string> RelativeDirectoryComponents,
    Guid? ChildJobId,
    JobSummaryDto? Child = null,
    FileDownloadStateDto? File = null);

/// <summary>
/// Files in one directory attempt: immutable accepted entries followed by supplemental work.
/// Pin AttemptNumber while paging. Supplemental rows can lack targets after child-history pruning.
/// </summary>
/// <param name="EntryCount">Number of immutable accepted entries, excluding supplemental work.</param>
/// <param name="TotalKnownBytes">Known bytes in the immutable accepted plan, excluding supplemental work.</param>
public sealed record DirectoryManifestPageDto(
    int AttemptNumber,
    string DisplayRoot,
    int EntryCount,
    long TotalKnownBytes,
    bool ChildrenMaterialized,
    int SupplementalEntryCount,
    long JobRevision,
    IReadOnlyList<DirectoryManifestEntryDto> Items,
    int? NextOrdinal);

public sealed record FileDownloadStateDto(
    string? DownloadPath,
    long BytesTransferred,
    long? FileSize,
    double? ProgressPercent);

public sealed record DirectoryDownloadStateDto(
    string Phase,
    int? AttemptNumber,
    string? DownloadPath,
    int FileCount,
    int TerminalFileCount,
    int SuccessfulFileCount,
    int FailedFileCount,
    long BytesTransferred,
    long TotalKnownBytes,
    double? ProgressPercent,
    int UnknownFileSizeCount);

/// <summary>
/// Payload for extract jobs.
/// </summary>
/// <param name="ResultJobId">Semantic job produced and automatically processed by extraction.</param>
public sealed record ExtractJobPayloadDto(
    string Input,
    string? InputType,
    Guid? ResultJobId) : JobPayloadDto;

/// <summary>
/// Payload for search jobs. Use a revision-bound Search View for result items.
/// </summary>
/// <param name="QueryText">Raw text submitted to Soulseek.</param>
/// <param name="DefaultProjection">Typed default Search View projection.</param>
/// <param name="SongQuery">Immutable file/track query, when applicable.</param>
/// <param name="AlbumQuery">Immutable album query, when applicable.</param>
/// <param name="IncludeFullResults">Whether the accepted file projection includes rows that fail necessary conditions.</param>
/// <param name="GroupResults">Whether the submitted search defaults to group result groups.</param>
/// <param name="Revision">Current raw observation revision.</param>
public sealed record SearchJobPayloadDto(
    string QueryText,
    ServerSearchDefaultProjectionKind DefaultProjection,
    SongQueryDto? SongQuery,
    AlbumQueryDto? AlbumQuery,
    bool IncludeFullResults,
    int ResultCount,
    int Revision,
    bool IsComplete,
    bool GroupResults = false) : JobPayloadDto;

/// <summary>An observed attempt, not a selected target or replay/download instruction.</summary>
public sealed record AttemptedSongFileDto(PeerFileTargetDto Target, PeerInfoDto Peer);

/// <summary>
/// Payload for song jobs, including child song rows owned by album or group jobs.
/// </summary>
/// <param name="JobId">
/// Present when the row corresponds to a registered job and can be addressed directly.
/// </param>
/// <param name="AvailableActions">
/// Actions currently valid for this song.
/// </param>
/// <param name="BytesTransferred">Current downloaded byte count for in-flight downloads.</param>
/// <param name="TotalBytes">Expected total byte count for the selected file, when known.</param>
/// <param name="ProgressPercent">Download progress from 0 to 100, when TotalBytes is known.</param>
public sealed record SongJobPayloadDto(
    SongQueryDto Query,
    int? CandidateCount,
    FileDownloadStateDto File,
    string? ResolvedUsername = null,
    string? ResolvedFilename = null,
    bool? ResolvedHasFreeUploadSlot = null,
    int? ResolvedUploadSpeed = null,
    long? ResolvedSize = null,
    int? ResolvedSampleRate = null,
    string? ResolvedExtension = null,
    IReadOnlyList<FileAttributeDto>? ResolvedAttributes = null,
    Guid? JobId = null,
    int? DisplayId = null,
    ServerJobLifecycleState? LifecycleState = null,
    ServerJobActivityPhase? ActivityPhase = null,
    DateTimeOffset? ActivityUntilUtc = null,
    ServerJobTerminalOutcome? TerminalOutcome = null,
    ServerJobSkipReason? SkipReason = null,
    ServerJobFailureReason? FailureReason = null,
    string? FailureMessage = null,
    IReadOnlyList<ResourceActionDto>? AvailableActions = null,
    string? TransferState = null,
    ServerJobCancellationSource CancellationSource = ServerJobCancellationSource.None,
    ServerSongDownloadSource DownloadSource = ServerSongDownloadSource.None,
    PeerFileTargetDto? ExactTarget = null,
    AttemptedSongFileDto? LastAttemptedFile = null) : JobPayloadDto;

/// <summary>
/// Payload for album search/download jobs.
/// </summary>
/// <param name="ResolvedFolderUsername">
/// Username of the folder selected/downloaded by the album job, when known.
/// </param>
/// <param name="ResolvedFolderPath">
/// Folder path selected/downloaded by the album job, when known.
/// </param>
public sealed record AlbumJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    DirectoryDownloadStateDto Directory,
    string? ResolvedFolderUsername,
    string? ResolvedFolderPath) : JobPayloadDto;

public sealed record RemoteFileJobPayloadDto(
    PeerFileTargetDto Target,
    IReadOnlyList<string> OutputPathComponents,
    FileDownloadStateDto File) : JobPayloadDto;

[JsonConverter(typeof(JsonStringEnumConverter<RemoteDirectorySourceKindDto>))]
public enum RemoteDirectorySourceKindDto
{
    PeerDirectory,
    Resolved,
}

/// <summary>Typed state for a peer-directory or exact resolved directory plan.</summary>
/// <param name="SourceUsername">Exact peer for either source kind when known; null for plans without a single peer.</param>
/// <param name="SourceFolderPath">Exact original remote folder path when an explicit peer directory or a resolved plan with known directory origin owns the selection; null for independent-file logical plans without a directory origin.</param>
public sealed record RemoteDirectoryJobPayloadDto(
    RemoteDirectorySourceKindDto SourceKind,
    string? SourceUsername,
    string? SourceFolderPath,
    DirectoryDownloadStateDto Directory,
    string? DisplayRoot = null) : JobPayloadDto;

/// <summary>
/// Payload for group track download jobs.
/// </summary>
public sealed record SongGroupsJobPayloadDto(
    SongQueryDto Query,
    int SongCount,
    int CompletedSongCount,
    int SucceededSongCount,
    int FailedSongCount) : JobPayloadDto;

/// <summary>
/// Payload for album-groups jobs, which search for distinct album candidates.
/// </summary>
public sealed record AlbumGroupsJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    int CompletedAlbumCount,
    int SucceededAlbumCount,
    int FailedAlbumCount) : JobPayloadDto;

/// <summary>
/// Payload for job-list jobs. Direct children are listed through the jobs collection.
/// </summary>
public sealed record JobListPayloadDto(
    int Count,
    int ActiveJobCount,
    int CompletedJobCount,
    int SucceededJobCount,
    int FailedJobCount) : JobPayloadDto;

/// <summary>
/// Payload for full-folder retrieval jobs started from album result views.
/// </summary>
public sealed record RetrieveFolderJobPayloadDto(
    string FolderPath,
    string Username,
    int NewFilesFoundCount,
    ServerFolderRetrievalOutcome RetrievalOutcome,
    bool RetrievalCancelled) : JobPayloadDto;

/// <summary>
/// Fallback payload for job kinds without a specialized DTO.
/// </summary>
public sealed record GenericJobPayloadDto(
    string Text) : JobPayloadDto;
