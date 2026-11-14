using System.Text.Json.Serialization;

namespace Sockseek.Api;

/// <summary>
/// Starts an extract job from a URL, list path, CSV path, or free-text query.
/// InputText supplies CSV/List contents without creating an input artifact.
/// Planning consumes a temporary file; source mutation is disabled.
/// </summary>
public sealed record SubmitExtractJobRequestDto(
    string? Input = null,
    string? InputType = null,
    SubmissionOptionsDto? Options = null,
    DownloadBehaviorPolicyDto? ResultDownloadBehavior = null,
    string? ArtifactId = null,
    string? InputText = null);

/// <summary>
/// Starts a generic Soulseek discovery job from raw query text.
/// Search jobs are useful for exploratory/manual UIs: inspect raw results, project them
/// as files, folders, or group candidates, then start follow-up downloads from selected refs.
/// </summary>
public sealed record SubmitSearchJobRequestDto(
    string QueryText,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Starts a track discovery job.
/// The job completes after search/projection; result endpoints expose candidates, and
/// follow-up download endpoints start new download jobs from selected candidates.
/// GroupResults identifies the grouped search mode for profiles and the default Search View;
/// individual group source choices remain client-owned until selection commit.
/// </summary>
public sealed record SubmitTrackSearchJobRequestDto(
    SongQueryDto SongQuery,
    bool IncludeFullResults = false,
    SubmissionOptionsDto? Options = null,
    bool GroupResults = false);

/// <summary>
/// Starts an album discovery job.
/// The job completes after search/projection; result endpoints expose folders, and
/// follow-up download endpoints start new download jobs from selected folders.
/// GroupResults identifies the grouped search mode for profiles and the default Search View;
/// individual group source choices remain client-owned until selection commit.
/// </summary>
public sealed record SubmitAlbumSearchJobRequestDto(
    AlbumQueryDto AlbumQuery,
    SubmissionOptionsDto? Options = null,
    bool GroupResults = false);

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
/// Starts a grouped track job.
/// Automatic behavior downloads from grouped candidates; DownloadBehavior.Manual projects grouped
/// candidates and enters AwaitingSelection so the caller can resume selected child downloads.
/// </summary>
public sealed record SubmitSongGroupsJobRequestDto(
    SongQueryDto SongQuery,
    SubmissionOptionsDto? Options = null,
    DownloadBehaviorPolicyDto? DownloadBehavior = null);

/// <summary>
/// Starts a grouped album job.
/// Automatic behavior downloads selected album buckets; DownloadBehavior.Manual projects buckets
/// and enters AwaitingSelection so the caller can resume selected child downloads.
/// </summary>
public sealed record SubmitAlbumGroupsJobRequestDto(
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
/// Reusable job shape accepted inside job-list submissions.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExtractJobDraftDto), ServerProtocol.JobDraftKinds.Extract)]
[JsonDerivedType(typeof(GenericSearchJobDraftDto), ServerProtocol.JobDraftKinds.GenericSearch)]
[JsonDerivedType(typeof(TrackSearchJobDraftDto), ServerProtocol.JobDraftKinds.TrackSearch)]
[JsonDerivedType(typeof(AlbumSearchJobDraftDto), ServerProtocol.JobDraftKinds.AlbumSearch)]
[JsonDerivedType(typeof(SongJobDraftDto), ServerProtocol.JobDraftKinds.Song)]
[JsonDerivedType(typeof(AlbumJobDraftDto), ServerProtocol.JobDraftKinds.Album)]
[JsonDerivedType(typeof(SongGroupsJobDraftDto), ServerProtocol.JobDraftKinds.SongGroups)]
[JsonDerivedType(typeof(AlbumGroupsJobDraftDto), ServerProtocol.JobDraftKinds.AlbumGroups)]
[JsonDerivedType(typeof(JobListJobDraftDto), ServerProtocol.JobDraftKinds.JobList)]
[JsonDerivedType(typeof(RemoteFileJobDraftDto), ServerProtocol.JobDraftKinds.RemoteFile)]
[JsonDerivedType(typeof(RemoteDirectoryJobDraftDto), ServerProtocol.JobDraftKinds.RemoteDirectory)]
public abstract record JobDraftDto;

public sealed record ExtractJobDraftDto(
    string? Input = null,
    string? InputType = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    DownloadBehaviorPolicyDto? ResultDownloadBehavior = null,
    JobProvenanceDto? Provenance = null,
    string? ArtifactId = null) : JobDraftDto;

public sealed record GenericSearchJobDraftDto(
    string QueryText,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record TrackSearchJobDraftDto(
    SongQueryDto SongQuery,
    bool IncludeFullResults = false,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null,
    bool GroupResults = false) : JobDraftDto;

public sealed record AlbumSearchJobDraftDto(
    AlbumQueryDto AlbumQuery,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null,
    bool GroupResults = false) : JobDraftDto;

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

public sealed record SongGroupsJobDraftDto(
    SongQueryDto SongQuery,
    DownloadBehaviorPolicyDto? DownloadBehavior = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record AlbumGroupsJobDraftDto(
    AlbumQueryDto AlbumQuery,
    DownloadBehaviorPolicyDto? DownloadBehavior = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record JobListJobDraftDto(
    string? Name,
    IReadOnlyList<JobDraftDto> Jobs,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record RemoteFileJobDraftDto(
    PeerFileTargetDto Target,
    IReadOnlyList<string>? OutputPathComponents = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

public sealed record RemoteDirectoryJobDraftDto(
    string? Username = null,
    string? FolderPath = null,
    DirectoryTransferPlanDto? Plan = null,
    DownloadSettingsPatchDto? DownloadSettings = null,
    JobProvenanceDto? Provenance = null) : JobDraftDto;

/// <summary>
/// Controls automatic versus caller-selected downloads for download-capable jobs.
/// Automatic jobs continue into transfer. Manual jobs collect candidates and enter AwaitingSelection
/// until the caller resumes with a selection or completes the manual step.
/// Null per-kind values inherit Default.
/// </summary>
public sealed record DownloadBehaviorPolicyDto(
    ServerDownloadBehavior Default = ServerDownloadBehavior.Automatic,
    ServerDownloadBehavior? Song = null,
    ServerDownloadBehavior? Album = null,
    ServerDownloadBehavior? SongGroups = null,
    ServerDownloadBehavior? AlbumGroups = null);

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
    SubmissionOptionsDto? Options = null,
    ServerExtractionMode? RequestedMode = null);

/// <summary>
/// Starts an album/folder download from a selected search result folder.
/// </summary>
public sealed record StartFolderDownloadRequestDto(
    AlbumFolderRefDto Folder,
    SubmissionOptionsDto? Options = null,
    AlbumQueryDto? AlbumQuery = null,
    AlbumFolderDownloadSelectionDto? Selection = null,
    ServerExtractionMode? RequestedMode = null);

/// <summary>
/// Describes how a selected album/folder should be downloaded.
/// </summary>
public sealed record AlbumFolderDownloadSelectionDto(
    IReadOnlyList<FileCandidateRefDto>? Files = null,
    bool ExactFiles = false,
    bool SkipTrackCountVerification = false);
