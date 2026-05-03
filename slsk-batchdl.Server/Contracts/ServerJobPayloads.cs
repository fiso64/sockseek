using System.Text.Json.Serialization;

namespace Sldl.Server;

/// <summary>
/// Typed job-specific payload carried by JobDetailDto. Switch on the JSON "kind" discriminator
/// or the concrete DTO type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExtractJobPayloadDto), ServerProtocol.JobKinds.Extract)]
[JsonDerivedType(typeof(SearchJobPayloadDto), ServerProtocol.JobKinds.Search)]
[JsonDerivedType(typeof(SongJobPayloadDto), ServerProtocol.JobKinds.Song)]
[JsonDerivedType(typeof(AlbumJobPayloadDto), ServerProtocol.JobKinds.Album)]
[JsonDerivedType(typeof(AggregateJobPayloadDto), ServerProtocol.JobKinds.Aggregate)]
[JsonDerivedType(typeof(AlbumAggregateJobPayloadDto), ServerProtocol.JobKinds.AlbumAggregate)]
[JsonDerivedType(typeof(JobListPayloadDto), ServerProtocol.JobKinds.JobList)]
[JsonDerivedType(typeof(RetrieveFolderJobPayloadDto), ServerProtocol.JobKinds.RetrieveFolder)]
[JsonDerivedType(typeof(GenericJobPayloadDto), ServerProtocol.JobKinds.Generic)]
public abstract record JobPayloadDto;

/// <summary>
/// Payload for extract jobs.
/// </summary>
/// <param name="ResultJobId">
/// Semantic job produced by extraction. It may be started separately when auto-processing is off.
/// </param>
/// <param name="ResultDraft">
/// Typed draft for the produced job. Submit it through the normal job submission endpoints to
/// continue manually.
/// </param>
public sealed record ExtractJobPayloadDto(
    string Input,
    string? InputType,
    Guid? ResultJobId,
    JobDraftDto? ResultDraft) : JobPayloadDto;

/// <summary>
/// Payload for search jobs. Use the matching /results endpoint for the actual result items.
/// </summary>
/// <param name="QueryText">Raw text submitted to Soulseek.</param>
/// <param name="DefaultFileProjection">Default file projection used by compatibility file-results endpoints.</param>
/// <param name="DefaultFolderProjection">Default folder projection used by compatibility folder-results endpoints.</param>
/// <param name="Revision">Current result revision for matching SearchResultSnapshotDto views.</param>
public sealed record SearchJobPayloadDto(
    string QueryText,
    FileSearchProjectionRequestDto? DefaultFileProjection,
    FolderSearchProjectionRequestDto? DefaultFolderProjection,
    int ResultCount,
    int Revision,
    bool IsComplete) : JobPayloadDto;

/// <summary>
/// Payload for song jobs, including child song rows owned by album or aggregate jobs.
/// </summary>
/// <param name="JobId">
/// Present when the row corresponds to a registered job and can be addressed directly.
/// </param>
/// <param name="Candidates">
/// Reserved for compatibility. General job detail payloads do not inline candidates;
/// use /api/jobs/{jobId}/results/files for full result rows.
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
    string? DownloadPath,
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
    IReadOnlyList<FileCandidateDto>? Candidates = null,
    ServerJobState? State = null,
    ServerFailureReason? FailureReason = null,
    string? FailureMessage = null,
    long? BytesTransferred = null,
    long? TotalBytes = null,
    double? ProgressPercent = null,
    IReadOnlyList<ResourceActionDto>? AvailableActions = null) : JobPayloadDto;

/// <summary>
/// Payload for album search/download jobs.
/// </summary>
/// <param name="ResolvedFolderUsername">
/// Username of the folder selected/downloaded by the album job, when known.
/// </param>
/// <param name="ResolvedFolderPath">
/// Folder path selected/downloaded by the album job, when known.
/// </param>
/// <param name="SelectedFolderFileCount">
/// Number of files in the selected folder, when known. This is the count the album job is processing.
/// </param>
/// <param name="SelectedFolderCompletedFileCount">
/// Number of selected folder files that reached a terminal state, successful or failed.
/// </param>
/// <param name="SelectedFolderSucceededFileCount">
/// Number of selected folder files that completed successfully or already existed.
/// </param>
/// <param name="SelectedFolderFailedFileCount">
/// Number of selected folder files that failed or were skipped.
/// </param>
/// <param name="Results">
/// Reserved for compatibility. General job detail payloads do not inline album results;
/// use /api/jobs/{jobId}/results/folders for full folder rows.
/// </param>
/// <param name="Tracks">
/// Reserved for compatibility. General job detail payloads do not inline child tracks;
/// use JobDetailDto.Children or /api/jobs with the parent id to inspect child jobs.
/// </param>
public sealed record AlbumJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    string? DownloadPath,
    string? ResolvedFolderUsername,
    string? ResolvedFolderPath,
    int? SelectedFolderFileCount = null,
    int? SelectedFolderCompletedFileCount = null,
    int? SelectedFolderSucceededFileCount = null,
    int? SelectedFolderFailedFileCount = null,
    IReadOnlyList<AlbumFolderDto>? Results = null,
    IReadOnlyList<SongJobPayloadDto>? Tracks = null) : JobPayloadDto;

/// <summary>
/// Payload for aggregate track download jobs.
/// </summary>
public sealed record AggregateJobPayloadDto(
    SongQueryDto Query,
    int SongCount,
    int CompletedSongCount,
    int SucceededSongCount,
    int FailedSongCount,
    IReadOnlyList<SongJobPayloadDto>? Songs = null) : JobPayloadDto;

/// <summary>
/// Payload for album-aggregate jobs, which search for distinct album candidates.
/// </summary>
public sealed record AlbumAggregateJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount) : JobPayloadDto;

/// <summary>
/// Payload for job-list jobs. DirectSongs is reserved for compatibility; use
/// JobDetailDto.Children or /api/jobs with the parent id to inspect direct children.
/// </summary>
public sealed record JobListPayloadDto(
    int Count,
    int ActiveJobCount,
    int CompletedJobCount,
    int SucceededJobCount,
    int FailedJobCount,
    IReadOnlyList<SongJobPayloadDto>? DirectSongs = null) : JobPayloadDto;

/// <summary>
/// Payload for full-folder retrieval jobs started from album result views.
/// </summary>
public sealed record RetrieveFolderJobPayloadDto(
    string FolderPath,
    string Username,
    int NewFilesFoundCount) : JobPayloadDto;

/// <summary>
/// Fallback payload for job kinds without a specialized DTO.
/// </summary>
public sealed record GenericJobPayloadDto(
    string Text) : JobPayloadDto;
