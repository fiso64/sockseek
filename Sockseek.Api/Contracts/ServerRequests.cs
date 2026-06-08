using System.Text.Json.Serialization;
using Sockseek.Core;

namespace Sockseek.Api;

/// <summary>
/// Starts an extract job from a URL, list path, CSV path, or free-text query.
/// </summary>
public sealed record SubmitExtractJobRequestDto(
    string Input,
    string? InputType = null,
    bool? AutoStartExtractedResult = null,
    SubmissionOptionsDto? Options = null,
    DownloadBehaviorPolicyDto? ResultDownloadBehavior = null);

/// <summary>
/// Starts a generic Soulseek discovery job from raw query text.
/// Search jobs are useful for exploratory/manual UIs: inspect raw results, project them
/// as files, folders, or aggregate candidates, then start follow-up downloads from selected refs.
/// </summary>
public sealed record SubmitSearchJobRequestDto(
    string QueryText,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a track discovery job.
/// The job completes after search/projection; result endpoints expose candidates, and
/// follow-up download endpoints start new download jobs from selected candidates.
/// </summary>
public sealed record SubmitTrackSearchJobRequestDto(
    SongQueryDto SongQuery,
    bool IncludeFullResults = false,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts an album discovery job.
/// The job completes after search/projection; result endpoints expose folders, and
/// follow-up download endpoints start new download jobs from selected folders.
/// </summary>
public sealed record SubmitAlbumSearchJobRequestDto(
    AlbumQueryDto AlbumQuery,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a song download job.
/// Automatic behavior downloads the selected match; DownloadBehavior.Manual searches/projects
/// candidates and enters AwaitingSelection so the caller can resume the same job with a selection.
/// </summary>
public sealed record SubmitSongJobRequestDto(
    SongQueryDto SongQuery,
    SubmissionOptionsDto? Options = null,
    DownloadBehaviorPolicyDto? DownloadBehavior = null);

/// <summary>
/// Starts an album/folder download job.
/// Automatic behavior downloads the selected folder; DownloadBehavior.Manual searches/projects
/// folder candidates and enters AwaitingSelection so the caller can resume the same job with a selection.
/// </summary>
public sealed record SubmitAlbumJobRequestDto(
    AlbumQueryDto AlbumQuery,
    SubmissionOptionsDto? Options = null,
    DownloadBehaviorPolicyDto? DownloadBehavior = null);

/// <summary>
/// Starts an aggregate track job.
/// Automatic behavior downloads from grouped candidates; DownloadBehavior.Manual projects grouped
/// candidates and enters AwaitingSelection so the caller can resume selected child downloads.
/// </summary>
public sealed record SubmitAggregateJobRequestDto(
    SongQueryDto SongQuery,
    SubmissionOptionsDto? Options = null,
    DownloadBehaviorPolicyDto? DownloadBehavior = null);

/// <summary>
/// Starts an aggregate album job.
/// Automatic behavior downloads selected album buckets; DownloadBehavior.Manual projects buckets
/// and enters AwaitingSelection so the caller can resume selected child downloads.
/// </summary>
public sealed record SubmitAlbumAggregateJobRequestDto(
    AlbumQueryDto AlbumQuery,
    SubmissionOptionsDto? Options = null,
    DownloadBehaviorPolicyDto? DownloadBehavior = null);

/// <summary>
/// Starts a job-list root. Child items are typed with the "kind" discriminator because lists can
/// contain mixed job shapes.
/// </summary>
public sealed record SubmitJobListRequestDto(
    string? Name,
    IReadOnlyList<JobDraftDto> Jobs,
    SubmissionOptionsDto? Options = null);

public sealed record SourceMutationDto(
    string Kind,
    string Source,
    int LineNumber = 0,
    int ItemNumber = 1,
    int CsvColumnCount = 0,
    string? TrackUri = null);

public sealed record JobProvenanceDto(
    int ItemNumber = 1,
    int LineNumber = 0,
    SourceMutationDto? SourceMutation = null);

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
    bool? AutoStartExtractedResult = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    DownloadBehaviorPolicyDto? ResultDownloadBehavior = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record TrackSearchJobDraftDto(
    SongQueryDto SongQuery,
    bool IncludeFullResults = false,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record AlbumSearchJobDraftDto(
    AlbumQueryDto AlbumQuery,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record SongJobDraftDto(
    SongQueryDto SongQuery,
    DownloadBehaviorPolicyDto? DownloadBehavior = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record AlbumJobDraftDto(
    AlbumQueryDto AlbumQuery,
    DownloadBehaviorPolicyDto? DownloadBehavior = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record AggregateJobDraftDto(
    SongQueryDto SongQuery,
    DownloadBehaviorPolicyDto? DownloadBehavior = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record AlbumAggregateJobDraftDto(
    AlbumQueryDto AlbumQuery,
    DownloadBehaviorPolicyDto? DownloadBehavior = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record JobListJobDraftDto(
    string? Name,
    IReadOnlyList<JobDraftDto> Jobs,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

/// <summary>
/// Controls automatic versus caller-selected downloads for download-capable jobs.
/// Automatic jobs continue into transfer. Manual jobs collect candidates and enter AwaitingSelection
/// until the caller resumes with a selection or completes the manual step.
/// Null per-kind values inherit Default.
/// </summary>
public sealed record DownloadBehaviorPolicyDto(
    DownloadBehavior Default = DownloadBehavior.Automatic,
    DownloadBehavior? Song = null,
    DownloadBehavior? Album = null,
    DownloadBehavior? Aggregate = null,
    DownloadBehavior? AlbumAggregate = null);

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
    AlbumQueryDto? AlbumQuery = null,
    AlbumFolderDownloadSelectionDto? Selection = null);

/// <summary>
/// Describes how a selected album/folder should be downloaded.
/// </summary>
public sealed record AlbumFolderDownloadSelectionDto(
    IReadOnlyList<FileCandidateRefDto>? Files = null,
    bool ExactFiles = false,
    bool SkipTrackCountVerification = false);

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
