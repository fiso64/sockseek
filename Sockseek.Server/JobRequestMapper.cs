using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Api;

namespace Sockseek.Server;

public static class JobRequestMapper
{
    public const int MaxSearchTextLength = 1024;
    public const int MaxSearchUriLength = 4096;

    public static ExtractJob CreateExtractJob(SubmitExtractJobRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new ArgumentException("input is required for extract jobs");

        InputType? inputType = null;
        if (!string.IsNullOrWhiteSpace(request.InputType))
        {
            if (!Enum.TryParse<InputType>(request.InputType, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unsupported inputType '{request.InputType}'");
            inputType = parsed;
        }

        var job = new ExtractJob(request.Input, inputType);
        if (request.AutoStartExtractedResult.HasValue)
            job.AutoProcessResult = request.AutoStartExtractedResult.Value;
        if (request.ResultDownloadBehavior != null)
            job.ResultDownloadBehaviorPolicy = ToDownloadBehaviorPolicy(request.ResultDownloadBehavior);

        return job;
    }

    public static SearchJob CreateSearchJob(SubmitSearchJobRequestDto request)
    {
        ValidateSearchText(request.QueryText, nameof(request.QueryText));
        return new(request.QueryText);
    }

    public static SearchJob CreateTrackSearchJob(SubmitTrackSearchJobRequestDto request)
        => new(ToSongQuery(request.SongQuery), request.IncludeFullResults);

    public static SearchJob CreateAlbumSearchJob(SubmitAlbumSearchJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static SongJob CreateSongJob(SubmitSongJobRequestDto request)
        => ApplyDownloadBehavior(new SongJob(ToSongQuery(request.SongQuery)), request.DownloadBehavior);

    public static AlbumJob CreateAlbumJob(SubmitAlbumJobRequestDto request)
        => ApplyDownloadBehavior(new AlbumJob(ToAlbumQuery(request.AlbumQuery)), request.DownloadBehavior);

    public static AggregateJob CreateAggregateJob(SubmitAggregateJobRequestDto request)
        => ApplyDownloadBehavior(new AggregateJob(ToSongQuery(request.SongQuery)), request.DownloadBehavior);

    public static AlbumAggregateJob CreateAlbumAggregateJob(SubmitAlbumAggregateJobRequestDto request)
        => ApplyDownloadBehavior(new AlbumAggregateJob(ToAlbumQuery(request.AlbumQuery)), request.DownloadBehavior);

    public static JobList CreateJobList(SubmitJobListRequestDto request)
        => CreateJobList(request.Name, request.Jobs);

    public static void AssignWorkflowId(Job job, Guid workflowId)
    {
        job.WorkflowId = workflowId;

        switch (job)
        {
            case JobList list:
                foreach (var child in list.Jobs)
                    AssignWorkflowId(child, workflowId);
                break;
            case ExtractJob extract when extract.Result != null:
                AssignWorkflowId(extract.Result, workflowId);
                break;
            case AggregateJob aggregate:
                foreach (var song in aggregate.Songs)
                    AssignWorkflowId(song, workflowId);
                break;
            case AlbumAggregateJob aggregate:
                foreach (var album in aggregate.Albums)
                    AssignWorkflowId(album, workflowId);
                break;
        }
    }

    public static SongQuery ToSongQuery(SongQueryDto dto)
    {
        ValidateSearchText(dto.Artist, nameof(dto.Artist), allowEmpty: true);
        ValidateSearchText(dto.Title, nameof(dto.Title), allowEmpty: true);
        ValidateSearchText(dto.Album, nameof(dto.Album), allowEmpty: true);
        ValidateSearchText(dto.Uri, nameof(dto.Uri), maxLength: MaxSearchUriLength, allowEmpty: true);

        return new()
        {
            Artist = dto.Artist ?? "",
            Title = dto.Title ?? "",
            Album = dto.Album ?? "",
            URI = dto.Uri ?? "",
            Length = dto.Length ?? -1,
            ArtistMaybeWrong = dto.ArtistMaybeWrong,
        };
    }

    public static AlbumQuery ToAlbumQuery(AlbumQueryDto dto)
    {
        ValidateSearchText(dto.Artist, nameof(dto.Artist), allowEmpty: true);
        ValidateSearchText(dto.Album, nameof(dto.Album), allowEmpty: true);
        ValidateSearchText(dto.SearchHint, nameof(dto.SearchHint), allowEmpty: true);
        ValidateSearchText(dto.Uri, nameof(dto.Uri), maxLength: MaxSearchUriLength, allowEmpty: true);

        return new()
        {
            Artist = dto.Artist ?? "",
            Album = dto.Album ?? "",
            SearchHint = dto.SearchHint ?? "",
            URI = dto.Uri ?? "",
            ArtistMaybeWrong = dto.ArtistMaybeWrong,
        };
    }

    private static void ValidateSearchText(
        string? value,
        string fieldName,
        int maxLength = MaxSearchTextLength,
        bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (allowEmpty)
                return;
            throw new ArgumentException($"{fieldName} is required");
        }

        if (value.Length > maxLength)
            throw new ArgumentException($"{fieldName} is too long; maximum length is {maxLength} characters");
    }

    public static AlbumFolder ToAlbumFolder(AlbumFolderDto dto)
        => new(
            dto.Username,
            dto.FolderPath,
            dto.Files?.Select(ToAlbumFile).ToList() ?? [])
        {
            IsFullyRetrieved = dto.IsFullyRetrieved,
        };

    private static AlbumFile ToAlbumFile(FileCandidateDto dto)
    {
        var candidate = ToFileCandidate(dto);
        return AlbumFile.WithLazyQuery(
            () => Searcher.InferSongQuery(candidate.Filename, new SongQuery()),
            candidate);
    }

    private static FileCandidate ToFileCandidate(FileCandidateDto dto)
        => new(
            new Soulseek.SearchResponse(
                dto.Username,
                token: -1,
                dto.Peer.HasFreeUploadSlot ?? false,
                dto.Peer.UploadSpeed ?? -1,
                queueLength: -1,
                fileList: null),
            new Soulseek.File(
                code: 0,
                dto.Filename,
                dto.Size,
                dto.Extension ?? Path.GetExtension(dto.Filename),
                dto.Attributes?.Select(attr => new Soulseek.FileAttribute(
                    Enum.Parse<Soulseek.FileAttributeType>(attr.Type),
                    attr.Value))));

    public static Job CreateJob(JobDraftDto item)
        => item switch
        {
            ExtractJobDraftDto extract => ApplyProvenance(CreateExtractJob(new SubmitExtractJobRequestDto(
                extract.Input,
                extract.InputType,
                extract.AutoStartExtractedResult,
                ResultDownloadBehavior: extract.ResultDownloadBehavior)), extract.Provenance),
            TrackSearchJobDraftDto search => ApplyProvenance(new SearchJob(ToSongQuery(search.SongQuery), search.IncludeFullResults), search.Provenance),
            AlbumSearchJobDraftDto search => ApplyProvenance(new SearchJob(ToAlbumQuery(search.AlbumQuery)), search.Provenance),
            SongJobDraftDto song => ApplyProvenance(ApplyDownloadBehavior(new SongJob(ToSongQuery(song.SongQuery)), song.DownloadBehavior), song.Provenance),
            AlbumJobDraftDto album => ApplyProvenance(ApplyDownloadBehavior(new AlbumJob(ToAlbumQuery(album.AlbumQuery)), album.DownloadBehavior), album.Provenance),
            AggregateJobDraftDto aggregate => ApplyProvenance(ApplyDownloadBehavior(new AggregateJob(ToSongQuery(aggregate.SongQuery)), aggregate.DownloadBehavior), aggregate.Provenance),
            AlbumAggregateJobDraftDto aggregate => ApplyProvenance(ApplyDownloadBehavior(new AlbumAggregateJob(ToAlbumQuery(aggregate.AlbumQuery)), aggregate.DownloadBehavior), aggregate.Provenance),
            JobListJobDraftDto list => ApplyProvenance(CreateJobList(list.Name, list.Jobs), list.Provenance),
            _ => throw new ArgumentException($"Unsupported job draft type '{item.GetType().Name}'")
        };

    private static TJob ApplyProvenance<TJob>(TJob job, JobProvenanceDto? provenance)
        where TJob : Job
    {
        if (provenance == null)
            return job;

        job.ItemNumber = provenance.ItemNumber;
        job.LineNumber = provenance.LineNumber;
        job.SourceMutation = ToSourceMutation(provenance.SourceMutation);
        return job;
    }

    private static SourceMutation? ToSourceMutation(SourceMutationDto? dto)
    {
        if (dto == null)
            return null;
        if (!Enum.TryParse<SourceMutationKind>(dto.Kind, ignoreCase: true, out var kind))
            throw new ArgumentException($"Unsupported source mutation kind '{dto.Kind}'");

        return new SourceMutation(kind, dto.Source, dto.LineNumber, dto.ItemNumber, dto.CsvColumnCount, dto.TrackUri);
    }

    public static DownloadBehaviorPolicy ToDownloadBehaviorPolicy(DownloadBehaviorPolicyDto dto) => new()
    {
        Default = dto.Default,
        Song = dto.Song,
        Album = dto.Album,
        Aggregate = dto.Aggregate,
        AlbumAggregate = dto.AlbumAggregate,
    };

    public static TJob ApplyDownloadBehavior<TJob>(TJob job, DownloadBehaviorPolicyDto? policy)
        where TJob : Job
    {
        if (policy != null)
            job.DownloadBehaviorPolicy = ToDownloadBehaviorPolicy(policy);
        return job;
    }


    public static List<AlbumFolder> ProjectAlbumJobFolders(AlbumJob albumJob, ConcurrentDictionary<string, int>? userSuccessCounts = null)
    {
        if (albumJob.Config == null || albumJob.Results.Count == 0)
            return albumJob.Results.ToList();

        // Search results are already projected/ranked by the engine; re-project only
        // after folder retrieval has changed the visible folder contents.
        if (albumJob.Results.All(folder => !folder.IsFullyRetrieved))
            return albumJob.Results.ToList();

        var rawResults = albumJob.Results
            .SelectMany(folder => folder.Files)
            .Select(file => file.Candidate)
            .Select(candidate => (Response: candidate.Response, File: candidate.File))
            .ToList();

        if (rawResults.Count == 0)
            return [];

        var projected = SearchResultProjector.AlbumFolders(
            rawResults,
            albumJob.Query,
            albumJob.Config.Search,
            userSuccessCounts);

        // Once a folder has been explicitly browsed, preserve the full browsed contents
        // in result projections. Re-projecting the raw files through the original search
        // conditions can hide the newly discovered files, which makes interactive `r`
        // appear to do nothing even though retrieval succeeded.
        foreach (var retrieved in albumJob.Results.Where(folder => folder.IsFullyRetrieved))
        {
            var fullFolder = new AlbumFolder(retrieved.Username, retrieved.FolderPath, retrieved.Files.ToList())
            {
                IsFullyRetrieved = true,
            };
            int index = projected.FindIndex(folder =>
                string.Equals(folder.Username, retrieved.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, retrieved.FolderPath, StringComparison.Ordinal));
            if (index >= 0)
                projected[index] = fullFolder;
            else
                projected.Add(fullFolder);
        }

        return projected;
    }

    public static AlbumFolder? FindProjectedAlbumFolder(AlbumJob albumJob, AlbumFolderRefDto folderRef, ConcurrentDictionary<string, int>? userSuccessCounts = null)
        => ProjectAlbumJobFolders(albumJob, userSuccessCounts)
            .FirstOrDefault(folder => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));

    public static AlbumFolder ApplySelectedFolderSnapshot(AlbumFolder resolvedFolder, StartFolderDownloadRequestDto request)
    {
        if (request.SelectedFolder == null)
            return resolvedFolder;

        if (!string.Equals(request.SelectedFolder.Username, request.Folder.Username, StringComparison.Ordinal)
            || !string.Equals(request.SelectedFolder.FolderPath, request.Folder.FolderPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("Selected folder snapshot does not match the requested folder reference.");
        }

        ValidateSelectedFolderSnapshot(request.SelectedFolder);

        return ToAlbumFolder(request.SelectedFolder);
    }

    private static void ValidateSelectedFolderSnapshot(AlbumFolderDto folder)
    {
        if (folder.Files == null)
            return;

        foreach (var file in folder.Files)
        {
            if (!string.Equals(file.Username, folder.Username, StringComparison.Ordinal)
                || !string.Equals(file.Ref.Username, folder.Username, StringComparison.Ordinal))
            {
                throw new ArgumentException("Selected folder snapshot contains a file from a different user.");
            }

            if (!IsInFolderPath(file.Filename, folder.FolderPath)
                || !IsInFolderPath(file.Ref.Filename, folder.FolderPath))
            {
                throw new ArgumentException("Selected folder snapshot contains a file outside the requested folder.");
            }
        }
    }

    public static AlbumFolder? BuildRelatedFolder(AlbumFolderRefDto folderRef, IEnumerable<AlbumFolder> knownFolders)
    {
        var seedFiles = knownFolders
            .Where(folder => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && PathsAreRelated(folder.FolderPath, folderRef.FolderPath))
            .SelectMany(folder => folder.Files)
            .Where(file => file.Filename.StartsWith(folderRef.FolderPath + "\\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return seedFiles.Count == 0
            ? null
            : new AlbumFolder(folderRef.Username, folderRef.FolderPath, seedFiles);
    }

    private static bool PathsAreRelated(string left, string right)
        => left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsInFolderPath(string filename, string folderPath)
        => filename.StartsWith(folderPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || filename.Equals(folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static AlbumFolder ApplyFolderDownloadSelection(AlbumFolder folder, AlbumFolderDownloadSelectionDto? selection)
    {
        if (selection?.ExactFiles == true && selection.Files is not { Count: > 0 })
            throw new ArgumentException("Exact folder downloads require at least one selected file.");

        if (selection?.Files is not { Count: > 0 } selectedFiles)
            return folder;

        var selected = selectedFiles
            .Select(file => (file.Username, file.Filename))
            .ToHashSet();

        var files = folder.Files
            .Where(file => selected.Contains((file.Candidate.Username, file.Candidate.Filename)))
            .ToList();

        if (files.Count != selected.Count)
            throw new ArgumentException("One or more selected files were not found in the requested folder.");

        return new AlbumFolder(folder.Username, folder.FolderPath, files)
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };
    }

    public static void ApplyFolderDownloadSelection(AlbumJob job, AlbumFolderDownloadSelectionDto? selection)
    {
        job.AllowBrowseResolvedTarget = selection?.ExactFiles != true;
        job.SkipResolvedTargetTrackCountVerification = selection?.SkipTrackCountVerification == true;
    }

    private static JobList CreateJobList(string? name, IReadOnlyList<JobDraftDto> jobs)
    {
        if (jobs.Count == 0)
            throw new ArgumentException("job-list must contain at least one child job");

        return new JobList(name, jobs.Select(CreateJob));
    }
}
