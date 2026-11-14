using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Settings;
using Sockseek.Core.Planning;
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

        var job = new ExtractJob(request.Input, ParseInputType(request.InputType))
        {
            ArtifactId = request.ArtifactId,
        };
        if (request.ResultDownloadBehavior != null)
            job.ResultDownloadBehaviorPolicy = ToDownloadBehaviorPolicy(request.ResultDownloadBehavior);

        return job;
    }

    private static InputType? ParseInputType(string? value)
    {
        InputType? inputType = null;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!InputTypes.TryParse(value, out var parsed))
                throw new ArgumentException($"Unsupported inputType '{value}'");
            inputType = parsed;
        }
        return inputType;
    }

    public static SearchJob CreateSearchJob(SubmitSearchJobRequestDto request)
    {
        ValidateSearchText(request.QueryText, nameof(request.QueryText));
        return new(request.QueryText);
    }

    public static SearchJob CreateTrackSearchJob(SubmitTrackSearchJobRequestDto request)
        => new(ToSongQuery(request.SongQuery), request.IncludeFullResults)
        {
            GroupResults = request.GroupResults,
        };

    public static SearchJob CreateAlbumSearchJob(SubmitAlbumSearchJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery))
        {
            GroupResults = request.GroupResults,
        };

    public static SongJob CreateSongJob(SubmitSongJobRequestDto request)
        => ApplyDownloadBehavior(new SongJob(ToSongQuery(request.SongQuery)), request.DownloadBehavior);

    public static AlbumJob CreateAlbumJob(SubmitAlbumJobRequestDto request)
        => ApplyDownloadBehavior(new AlbumJob(ToAlbumQuery(request.AlbumQuery)), request.DownloadBehavior);

    public static SongGroupsJob CreateSongGroupsJob(SubmitSongGroupsJobRequestDto request)
        => ApplyDownloadBehavior(new SongGroupsJob(ToSongQuery(request.SongQuery)), request.DownloadBehavior);

    public static AlbumGroupsJob CreateAlbumGroupsJob(SubmitAlbumGroupsJobRequestDto request)
        => ApplyDownloadBehavior(new AlbumGroupsJob(ToAlbumQuery(request.AlbumQuery)), request.DownloadBehavior);

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
            case SongGroupsJob group:
                foreach (var song in group.Songs)
                    AssignWorkflowId(song, workflowId);
                break;
            case AlbumGroupsJob group:
                foreach (var album in group.Albums)
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

    public static Job CreateJob(JobDraftDto item)
        => SubmissionSpecification.MaterializeCommand(CreateCommand(item));

    // One draft-to-definition mapping for settings reads and executable plans.
    // Settings inspect one node at a time; the owner walks list inheritance.
    public static JobSettingsContext CreateSettingsContext(JobDraftDto item, bool allowMissingInput = true)
        => JobSettingsContext.FromCommand(CreateCommand(item, includeChildren: false, allowMissingInput), Guid.Empty);

    internal static NormalizedJobCommand CreateCommand(
        JobDraftDto item, bool includeChildren = true, bool allowMissingInput = false)
    {
        NormalizedJobCommand command = item switch
        {
            ExtractJobDraftDto extract => ExtractCommand(extract, allowMissingInput),
            GenericSearchJobDraftDto search => GenericSearchCommand(search, allowMissingInput),
            TrackSearchJobDraftDto search => SongCommand(JobSnapshotKind.Search, search.SongQuery)
                with { IncludeFullResults = search.IncludeFullResults, GroupResults = search.GroupResults },
            AlbumSearchJobDraftDto search => AlbumCommand(JobSnapshotKind.Search, search.AlbumQuery)
                with { GroupResults = search.GroupResults },
            SongJobDraftDto song => SongCommand(JobSnapshotKind.Song, song.SongQuery, song.DownloadBehavior),
            AlbumJobDraftDto album => AlbumCommand(JobSnapshotKind.Album, album.AlbumQuery, album.DownloadBehavior),
            SongGroupsJobDraftDto group => SongCommand(JobSnapshotKind.SongGroups, group.SongQuery, group.DownloadBehavior),
            AlbumGroupsJobDraftDto group => AlbumCommand(JobSnapshotKind.AlbumGroups, group.AlbumQuery, group.DownloadBehavior),
            JobListJobDraftDto list => new()
            {
                Kind = JobSnapshotKind.JobList, ItemName = list.Name,
                Children = includeChildren ? list.Jobs.Select(child => CreateCommand(child)).ToArray() : [],
            },
            RemoteFileJobDraftDto remote => RemoteFileCommand(remote),
            RemoteDirectoryJobDraftDto remote => RemoteDirectoryCommand(remote),
            _ => throw new ArgumentException($"Unsupported job draft type '{item.GetType().Name}'"),
        };
        JobProvenanceDto? provenance = item switch
        {
            ExtractJobDraftDto value => value.Provenance,
            GenericSearchJobDraftDto value => value.Provenance,
            TrackSearchJobDraftDto value => value.Provenance,
            AlbumSearchJobDraftDto value => value.Provenance,
            SongJobDraftDto value => value.Provenance,
            AlbumJobDraftDto value => value.Provenance,
            SongGroupsJobDraftDto value => value.Provenance,
            AlbumGroupsJobDraftDto value => value.Provenance,
            JobListJobDraftDto value => value.Provenance,
            RemoteFileJobDraftDto value => value.Provenance,
            RemoteDirectoryJobDraftDto value => value.Provenance,
            _ => throw new ArgumentException($"Unsupported job draft type '{item.GetType().Name}'"),
        };
        return provenance is null ? command : command with
        {
            ItemNumber = provenance.ItemNumber, LineNumber = provenance.LineNumber,
            SourceMutation = ToSourceMutation(provenance.SourceMutation),
        };
    }

    private static NormalizedJobCommand ExtractCommand(ExtractJobDraftDto draft, bool allowMissingInput)
    {
        if ((!allowMissingInput || draft.ArtifactId is not null) && string.IsNullOrWhiteSpace(draft.Input))
            throw new ArgumentException("input is required for extract jobs");
        return new()
        {
            Kind = JobSnapshotKind.Extract, Input = draft.Input,
            InputType = ParseInputType(draft.InputType), ArtifactId = draft.ArtifactId,
            ResultDownloadBehavior = draft.ResultDownloadBehavior is null ? null
                : ToDownloadBehaviorPolicy(draft.ResultDownloadBehavior),
        };
    }

    private static NormalizedJobCommand GenericSearchCommand(GenericSearchJobDraftDto draft, bool allowMissingInput)
    {
        ValidateSearchText(draft.QueryText, nameof(draft.QueryText), allowEmpty: allowMissingInput);
        return new() { Kind = JobSnapshotKind.Search, QueryText = draft.QueryText };
    }

    private static NormalizedJobCommand SongCommand(
        JobSnapshotKind kind, SongQueryDto dto, DownloadBehaviorPolicyDto? behavior = null)
    {
        SongQuery query = ToSongQuery(dto);
        return new()
        {
            Kind = kind, SongQuery = SongQueryDefinition.From(query),
            QueryText = kind == JobSnapshotKind.Search ? query.ToString(noInfo: true) : null,
            DownloadBehavior = behavior is null ? new() : ToDownloadBehaviorPolicy(behavior),
        };
    }

    private static NormalizedJobCommand AlbumCommand(
        JobSnapshotKind kind, AlbumQueryDto dto, DownloadBehaviorPolicyDto? behavior = null)
    {
        AlbumQuery query = ToAlbumQuery(dto);
        return new()
        {
            Kind = kind, AlbumQuery = AlbumQueryDefinition.From(query),
            QueryText = kind == JobSnapshotKind.Search ? SearchResultProjector.AlbumNetworkQuery(query).ToString(noInfo: true) : null,
            DownloadBehavior = behavior is null ? new() : ToDownloadBehaviorPolicy(behavior),
        };
    }

    private static NormalizedJobCommand RemoteFileCommand(RemoteFileJobDraftDto draft)
    {
        PeerFileTarget target = ToPeerFileTarget(draft.Target);
        var output = draft.OutputPathComponents is null ? RelativeOutputPath.FromRemoteFile(target)
            : new RelativeOutputPath(draft.OutputPathComponents);
        return new()
        {
            Kind = JobSnapshotKind.RemoteFile, PeerFile = target,
            OutputPathComponents = output.Components,
            ItemName = Path.GetFileName(target.Filename.Replace('\\', Path.DirectorySeparatorChar)),
        };
    }

    private static NormalizedJobCommand RemoteDirectoryCommand(RemoteDirectoryJobDraftDto draft)
        => CreateRemoteDirectorySource(draft) switch
        {
            RemoteDirectorySource.PeerDirectory peer => new()
            {
                Kind = JobSnapshotKind.RemoteDirectory,
                PeerDirectory = new(peer.Directory.Username, peer.Directory.FolderPath),
                ItemName = Path.GetFileName(peer.Directory.FolderPath.Replace('\\', Path.DirectorySeparatorChar)),
            },
            RemoteDirectorySource.Resolved resolved => new NormalizedJobCommand
            {
                Kind = JobSnapshotKind.RemoteDirectory,
                ItemName = resolved.Plan.DisplayRoot.Length == 0 ? "Selected files" : resolved.Plan.DisplayRoot,
            }.WithDirectoryPlan(resolved.Plan),
            _ => throw new InvalidOperationException("Unknown directory source."),
        };

    public static DownloadSettingsPatchDto? DraftDownloadSettings(JobDraftDto draft)
        => draft switch
        {
            ExtractJobDraftDto typed => typed.DownloadSettings,
            GenericSearchJobDraftDto typed => typed.DownloadSettings,
            TrackSearchJobDraftDto typed => typed.DownloadSettings,
            AlbumSearchJobDraftDto typed => typed.DownloadSettings,
            SongJobDraftDto typed => typed.DownloadSettings,
            AlbumJobDraftDto typed => typed.DownloadSettings,
            SongGroupsJobDraftDto typed => typed.DownloadSettings,
            AlbumGroupsJobDraftDto typed => typed.DownloadSettings,
            JobListJobDraftDto typed => typed.DownloadSettings,
            RemoteFileJobDraftDto typed => typed.DownloadSettings,
            RemoteDirectoryJobDraftDto typed => typed.DownloadSettings,
            _ => null,
        };

    public static void ApplyDraftDownloadSettings(
        JobList jobs,
        IReadOnlyList<JobDraftDto> drafts,
        Action<Job, DownloadSettingsPatchDto> apply)
    {
        for (int index = 0; index < jobs.Jobs.Count && index < drafts.Count; index++)
            ApplyDraftDownloadSettings(jobs.Jobs[index], drafts[index], apply);
    }

    public static void ApplyDraftDownloadSettings(
        Job job,
        JobDraftDto draft,
        Action<Job, DownloadSettingsPatchDto> apply)
    {
        if (DraftDownloadSettings(draft) is { } patch)
            apply(job, patch);
        if (job is JobList children && draft is JobListJobDraftDto childDraft)
            ApplyDraftDownloadSettings(children, childDraft.Jobs, apply);
    }

    private static RemoteDirectorySource CreateRemoteDirectorySource(RemoteDirectoryJobDraftDto draft)
    {
        bool hasDirectory = draft.Username != null || draft.FolderPath != null;
        if (hasDirectory == (draft.Plan != null))
            throw new ArgumentException("A remote directory draft requires exactly one peer-directory or resolved-plan source.");
        if (hasDirectory)
        {
            if (draft.Username == null || draft.FolderPath == null)
                throw new ArgumentException("Both username and folderPath are required for a peer-directory source.");
            return new RemoteDirectorySource.PeerDirectory(
                new PeerDirectoryIdentity(draft.Username, draft.FolderPath));
        }

        return new RemoteDirectorySource.Resolved(ToDirectoryTransferPlan(draft.Plan!));
    }

    private static PeerFileTarget ToPeerFileTarget(PeerFileTargetDto target)
        => new(
            new PeerFileIdentity(target.Username, target.Filename),
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.Select(attribute =>
                new FileAttributeSnapshot(attribute.Type, attribute.Value)).ToList());

    private static DirectoryTransferPlan ToDirectoryTransferPlan(DirectoryTransferPlanDto plan)
    {
        var entries = new List<DirectoryTransferEntry>(plan.Entries.Count);
        foreach (DirectoryTransferEntryDto entry in plan.Entries)
        {
            if (entry?.Target is null || entry.RelativeDirectoryComponents is null)
                continue;
            try
            {
                entries.Add(new DirectoryTransferEntry(
                    ToPeerFileTarget(entry.Target),
                    entry.RelativeDirectoryComponents));
            }
            catch (ArgumentException)
            {
                // A malformed resolved entry cannot escape placement validation,
                // but it does not invalidate independently downloadable siblings.
            }
        }
        return new DirectoryTransferPlan(plan.DisplayRoot, entries);
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
        Default = dto.Default.ToCore(),
        Song = dto.Song?.ToCore(),
        Album = dto.Album?.ToCore(),
        SongGroups = dto.SongGroups?.ToCore(),
        AlbumGroups = dto.AlbumGroups?.ToCore(),
    };

    public static TJob ApplyDownloadBehavior<TJob>(TJob job, DownloadBehaviorPolicyDto? policy)
        where TJob : Job
    {
        if (policy != null)
            job.DownloadBehaviorPolicy = ToDownloadBehaviorPolicy(policy);
        return job;
    }


    public static List<AlbumFolder> ProjectAlbumJobFolders(AlbumJob albumJob, ConcurrentDictionary<string, int>? peerFailureCounts = null)
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
            .Select(candidate => candidate.ToProjectionInput())
            .ToList();

        if (rawResults.Count == 0)
            return [];

        var projected = SearchResultProjector.AlbumFolders(
            rawResults,
            albumJob.Query,
            albumJob.Config.Search,
            peerFailureCounts);

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

    public static AlbumFolder? FindProjectedAlbumFolder(AlbumJob albumJob, AlbumFolderRefDto folderRef, ConcurrentDictionary<string, int>? peerFailureCounts = null)
        => ProjectAlbumJobFolders(albumJob, peerFailureCounts)
            .FirstOrDefault(folder => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));

    public static AlbumFolder? BuildRelatedFolder(AlbumFolderRefDto folderRef, IEnumerable<AlbumFolder> knownFolders)
    {
        var seedFiles = knownFolders
            .Where(folder => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && PathsAreRelated(folder.FolderPath, folderRef.FolderPath))
            .SelectMany(folder => folder.Files)
            .Where(file => file.Filename.StartsWith(folderRef.FolderPath + "\\", StringComparison.Ordinal))
            .ToList();

        return seedFiles.Count == 0
            ? null
            : new AlbumFolder(folderRef.Username, folderRef.FolderPath, seedFiles);
    }

    public static AlbumFolder? FindAlbumFolderForRetrieval(
        Job sourceJob,
        AlbumFolderRefDto folderRef,
        ConcurrentDictionary<string, int> peerFailureCounts,
        AlbumQueryDto? albumQuery = null)
    {
        if (sourceJob is AlbumJob albumJob)
        {
            return albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? FindAlbumFolder(sourceJob, folderRef, peerFailureCounts, albumQuery);
        }

        return FindAlbumFolder(sourceJob, folderRef, peerFailureCounts, albumQuery);
    }

    public static AlbumFolder? FindAlbumFolder(
        Job sourceJob,
        AlbumFolderRefDto folderRef,
        ConcurrentDictionary<string, int> peerFailureCounts,
        AlbumQueryDto? albumQuery = null)
    {
        if (sourceJob is SearchJob searchJob)
        {
            if (searchJob.Config == null)
                return null;

            FolderSearchProjection? projection = albumQuery != null
                ? new FolderSearchProjection(ToAlbumQuery(albumQuery))
                : searchJob.DefaultFolderProjection;
            if (projection == null)
                return null;

            IReadOnlyList<AlbumFolder> folders = searchJob
                .GetAlbumFolders(projection, searchJob.Config.Search)
                .Items;
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? BuildRelatedFolder(folderRef, folders);
        }

        if (sourceJob is AlbumJob albumJob)
        {
            return FindProjectedAlbumFolder(albumJob, folderRef, peerFailureCounts)
                ?? albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? BuildRelatedFolder(folderRef, albumJob.Results);
        }

        if (sourceJob is AlbumGroupsJob songGroupsJob)
        {
            AlbumQuery? query = albumQuery == null ? null : ToAlbumQuery(albumQuery);
            List<AlbumFolder> folders = songGroupsJob.Albums
                .Where(album => query == null || AlbumQueriesEqual(album.Query, query))
                .SelectMany(album => album.Results)
                .ToList();
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? BuildRelatedFolder(folderRef, folders);
        }

        return null;
    }

    public static FileCandidate? FindFileCandidate(
        Job sourceJob,
        FileCandidateRefDto candidateRef,
        ConcurrentDictionary<string, int> peerFailureCounts)
    {
        if (sourceJob is SearchJob searchJob)
            return FindSearchFileCandidate(searchJob, candidateRef, peerFailureCounts);

        IEnumerable<FileCandidate> candidates = sourceJob switch
        {
            SongJob song => song.Candidates ?? [],
            SongGroupsJob group => group.Songs
                .SelectMany(song => song.Candidates ?? []),
            AlbumJob album => album.Results
                .SelectMany(folder => folder.Files)
                .Select(file => file.Candidate),
            AlbumGroupsJob group => group.Albums
                .SelectMany(album => album.Results)
                .SelectMany(folder => folder.Files)
                .Select(file => file.Candidate),
            _ => [],
        };
        return candidates.FirstOrDefault(candidate => Matches(candidate, candidateRef));
    }

    public static Job CreateFileSelectionFollowUp(
        Job sourceJob,
        FileCandidate candidate,
        ExtractionMode? requestedMode)
    {
        Job followUp = requestedMode == ExtractionMode.General
            ? new RemoteFileJob(candidate.Target)
            : new SongJob(new SongQuery(sourceJob switch
            {
                SearchJob search => search.DefaultFileProjection?.Query
                    ?? Searcher.InferSongQuery(
                        candidate.Filename,
                        new SongQuery { Title = search.QueryText }),
                SongJob song => song.Query,
                SongGroupsJob group => group.Songs
                    .FirstOrDefault(song => song.Candidates?.Contains(candidate) == true)?.Query
                    ?? Searcher.InferSongQuery(candidate.Filename, group.Query),
                _ => Searcher.InferSongQuery(
                    candidate.Filename,
                    sourceJob.QueryTrack ?? new SongQuery()),
            }))
            {
                ResolvedTarget = candidate,
                ExactTarget = candidate.Target,
            };
        followUp.SelectionSource = JobSelectionSource.ManualSearch;
        followUp.ItemName = sourceJob.ItemName;
        followUp.WorkflowId = sourceJob.WorkflowId;
        return followUp;
    }

    public static void PropagateSourceMutationToFollowUp(Job sourceJob, Job followUpJob)
    {
        // Album-group children already retain the source mutation that owns
        // the shared group input; copying it to a separate follow-up would
        // let that follow-up retire the same source twice.
        if (sourceJob is not AlbumGroupsJob)
            followUpJob.CopySourceMutationFrom(sourceJob);
    }

    public static AlbumQuery? ResolveFolderSelectionQuery(
        Job sourceJob,
        AlbumQueryDto? query)
        => query != null
            ? ToAlbumQuery(query)
            : sourceJob switch
            {
                SearchJob search => search.DefaultFolderProjection?.Query,
                AlbumJob album => album.Query,
                AlbumGroupsJob group => group.Query,
                _ => null,
            };

    public static AlbumJob CreateAlbumSelectionFollowUp(
        Job sourceJob,
        AlbumFolder folder,
        AlbumQuery query,
        AlbumFolderDownloadSelectionDto? selection)
    {
        string? itemName = sourceJob is SearchJob
            {
                DefaultAlbumGroupProjection: not null,
            } && !string.IsNullOrWhiteSpace(folder.FolderPath)
                ? Utils.GetBaseNameSlsk(folder.FolderPath)
                : sourceJob.ItemName;
        var followUp = new AlbumJob(new AlbumQuery(query))
        {
            SelectionSource = JobSelectionSource.ManualSearch,
            ResolvedTarget = folder,
            ItemName = itemName,
            WorkflowId = sourceJob.WorkflowId,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
        };
        ApplyFolderDownloadSelection(followUp, selection);
        return followUp;
    }

    private static FileCandidate? FindSearchFileCandidate(
        SearchJob searchJob,
        FileCandidateRefDto candidateRef,
        ConcurrentDictionary<string, int> peerFailureCounts)
    {
        if (searchJob.Config == null)
            return null;

        FileCandidate? candidate = searchJob
            .GetSortedTrackCandidates(searchJob.Config.Search, peerFailureCounts)
            .Items
            .FirstOrDefault(item => Matches(item, candidateRef));
        if (candidate != null || searchJob.DefaultFolderProjection == null)
        {
            return candidate
                ?? searchJob.RawSnapshot()
                    .Select(result => result.ProjectionInput.ToFileCandidate())
                    .FirstOrDefault(item => Matches(item, candidateRef));
        }

        return searchJob.GetAlbumFolders(searchJob.Config.Search)
            .Items
            .SelectMany(folder => folder.Files)
            .Select(file => file.Candidate)
            .FirstOrDefault(item => Matches(item, candidateRef));
    }

    private static bool Matches(AlbumFolder folder, AlbumFolderRefDto folderRef)
        => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
            && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal);

    private static bool Matches(FileCandidate candidate, FileCandidateRefDto candidateRef)
        => string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
            && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal);

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase);

    private static bool PathsAreRelated(string left, string right)
        => left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsInFolderPath(string filename, string folderPath)
        => filename.StartsWith(folderPath.TrimEnd('\\') + "\\", StringComparison.Ordinal)
            || filename.Equals(folderPath.TrimEnd('\\'), StringComparison.Ordinal);

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
        // Source acceptance bypasses history matching independently of the
        // caller's folder-retrieval and track-count verification choices.
        job.CanBeSkippedOverride = false;
        job.DirectoryResolutionPolicy = selection?.ExactFiles == true
            ? AlbumDirectoryResolutionPolicy.UseSelectedSnapshot
            : AlbumDirectoryResolutionPolicy.CompleteIfNeeded;
        job.ValidationRequirement = selection?.SkipTrackCountVerification == true
            ? AlbumValidationRequirement.UserAccepted
            : AlbumValidationRequirement.Standard;
    }

    public static RemoteDirectoryJob CreateRemoteDirectoryDownload(
        AlbumFolder folder,
        AlbumFolderDownloadSelectionDto? selection)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.IsFullyRetrieved && selection?.ExactFiles != true)
        {
            return new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                folder.DirectoryIdentity)) { SelectionSource = JobSelectionSource.ManualSearch };
        }

        var selectedSnapshot = new PeerDirectorySnapshot(
            folder.DirectoryIdentity,
            folder.Files.Select(file => file.Candidate.Target).ToArray(),
            isComplete: true);
        return new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            DirectoryTransferPlanner.FromSnapshot(selectedSnapshot))) { SelectionSource = JobSelectionSource.ManualSearch };
    }

    private static JobList CreateJobList(string? name, IReadOnlyList<JobDraftDto> jobs)
    {
        if (jobs.Count == 0)
            throw new ArgumentException("job-list must contain at least one child job");

        return new JobList(name, jobs.Select(CreateJob));
    }
}
