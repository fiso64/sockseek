using System.Text.Json.Serialization;

namespace Sldl.Server;

/// <summary>
/// Starts an extract job from a URL, list path, CSV path, or free-text query.
/// </summary>
public sealed record SubmitExtractJobRequestDto(
    string Input,
    string? InputType = null,
    bool? AutoStartExtractedResult = null,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a generic Soulseek search from raw query text. Result endpoints decide how raw results are projected.
/// </summary>
public sealed record SubmitSearchJobRequestDto(
    string QueryText,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a track search job. Use result endpoints to inspect candidates and follow-up endpoints
/// to start downloads from selected candidates.
/// </summary>
public sealed record SubmitTrackSearchJobRequestDto(
    SongQueryDto SongQuery,
    bool IncludeFullResults = false,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts an album search job. Use result endpoints to inspect folders and follow-up endpoints
/// to start downloads from selected folders.
/// </summary>
public sealed record SubmitAlbumSearchJobRequestDto(
    AlbumQueryDto AlbumQuery,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a direct song download job.
/// </summary>
public sealed record SubmitSongJobRequestDto(
    SongQueryDto SongQuery,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a direct album download job.
/// </summary>
public sealed record SubmitAlbumJobRequestDto(
    AlbumQueryDto AlbumQuery,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts an aggregate track job.
/// </summary>
public sealed record SubmitAggregateJobRequestDto(
    SongQueryDto SongQuery,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts an aggregate album job.
/// </summary>
public sealed record SubmitAlbumAggregateJobRequestDto(
    AlbumQueryDto AlbumQuery,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a job-list root. Child items are typed with the "kind" discriminator because lists can
/// contain mixed job shapes.
/// </summary>
public sealed record SubmitJobListRequestDto(
    string? Name,
    IReadOnlyList<JobDraftDto> Jobs,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Reusable job shape returned by extraction and accepted inside job-list submissions.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExtractJobDraftDto), ServerProtocol.JobDraftKinds.Extract)]
[JsonDerivedType(typeof(TrackSearchJobDraftDto), ServerProtocol.JobDraftKinds.TrackSearch)]
[JsonDerivedType(typeof(AlbumSearchJobDraftDto), ServerProtocol.JobDraftKinds.AlbumSearch)]
[JsonDerivedType(typeof(SongJobDraftDto), ServerProtocol.JobDraftKinds.Song)]
[JsonDerivedType(typeof(AlbumJobDraftDto), ServerProtocol.JobDraftKinds.Album)]
[JsonDerivedType(typeof(AggregateJobDraftDto), ServerProtocol.JobDraftKinds.Aggregate)]
[JsonDerivedType(typeof(AlbumAggregateJobDraftDto), ServerProtocol.JobDraftKinds.AlbumAggregate)]
[JsonDerivedType(typeof(JobListJobDraftDto), ServerProtocol.JobDraftKinds.JobList)]
public abstract record JobDraftDto;

public sealed record ExtractJobDraftDto(
    string Input,
    string? InputType = null,
    bool? AutoStartExtractedResult = null) : JobDraftDto;

public sealed record TrackSearchJobDraftDto(
    SongQueryDto SongQuery,
    bool IncludeFullResults = false) : JobDraftDto;

public sealed record AlbumSearchJobDraftDto(
    AlbumQueryDto AlbumQuery) : JobDraftDto;

public sealed record SongJobDraftDto(
    SongQueryDto SongQuery) : JobDraftDto;

public sealed record AlbumJobDraftDto(
    AlbumQueryDto AlbumQuery) : JobDraftDto;

public sealed record AggregateJobDraftDto(
    SongQueryDto SongQuery) : JobDraftDto;

public sealed record AlbumAggregateJobDraftDto(
    AlbumQueryDto AlbumQuery) : JobDraftDto;

public sealed record JobListJobDraftDto(
    string? Name,
    IReadOnlyList<JobDraftDto> Jobs) : JobDraftDto;

/// <summary>
/// Submission-time settings layered over the daemon defaults.
/// </summary>
public sealed record SubmissionOptionsDto(
    Guid? WorkflowId = null,
    string? OutputParentDir = null,
    IReadOnlyList<string>? ProfileNames = null,
    IReadOnlyDictionary<string, bool>? ProfileContext = null,
    DownloadSettingsPatchDto? DownloadSettings = null);

/// <summary>
/// Starts a folder retrieval job for an album result folder.
/// </summary>
public sealed record RetrieveFolderRequestDto(
    AlbumFolderRefDto Folder,
    AlbumQueryDto? AlbumQuery = null);

/// <summary>
/// Starts one or more downloads from selected search result files.
/// </summary>
public sealed record StartFileDownloadsRequestDto(
    IReadOnlyList<FileCandidateRefDto> Files,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts an album/folder download from a selected search result folder.
/// </summary>
public sealed record StartFolderDownloadRequestDto(
    AlbumFolderRefDto Folder,
    SubmissionOptionsDto? Options = null,
    AlbumQueryDto? AlbumQuery = null);

/// <summary>
/// Projection options for viewing search results as file candidates.
/// </summary>
public sealed record FileSearchProjectionRequestDto(
    SongQueryDto? SongQuery = null,
    bool IncludeFullResults = false);

/// <summary>
/// Projection options for viewing search results as album folders.
/// </summary>
public sealed record FolderSearchProjectionRequestDto(
    AlbumQueryDto AlbumQuery,
    bool IncludeFiles = false);

/// <summary>
/// Projection options for grouping search results as aggregate track candidates.
/// </summary>
public sealed record AggregateTrackProjectionRequestDto(
    SongQueryDto? SongQuery = null,
    bool IncludeCandidates = false);

/// <summary>
/// Projection options for grouping search results as aggregate album candidates.
/// </summary>
public sealed record AggregateAlbumProjectionRequestDto(
    AlbumQueryDto? AlbumQuery = null,
    bool IncludeFolders = false);
