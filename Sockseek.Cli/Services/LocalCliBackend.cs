using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;

namespace Sockseek.Cli;

internal sealed class LocalCliBackend
    : ICliBackend
{
    private readonly DownloadEngine engine;
    private readonly DownloadSettings? defaultSubmitSettings;
    private readonly SubmissionOptionsJobSettingsResolver? submissionOptionsResolver;
    private readonly EngineStateStore stateStore = new();
    private long nextSequence;

    public event Action<ServerEventEnvelopeDto>? EventReceived;

    public LocalCliBackend(
        DownloadEngine engine,
        DownloadSettings? defaultSubmitSettings = null,
        SubmissionOptionsJobSettingsResolver? submissionOptionsResolver = null)
    {
        this.engine = engine;
        this.submissionOptionsResolver = submissionOptionsResolver;
        this.defaultSubmitSettings = defaultSubmitSettings != null
            ? SettingsCloner.Clone(defaultSubmitSettings)
            : null;
        stateStore.AttachEngine(engine);
        stateStore.JobUpserted += summary => Publish("job.upserted", summary);
        stateStore.WorkflowUpserted += summary => Publish("workflow.upserted", summary);
        stateStore.SearchUpdated += update => Publish("search.updated", update);
        new EngineEventDtoAdapter(GetSummary, Publish).Attach(engine.Events);
    }

    public Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateExtractJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateTrackSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateSongJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateJobList(request), request.Options, ct);

    public Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SubscribeAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private Task<JobSummaryDto> SubmitJobAsync(Job job, SubmissionOptionsDto? options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (defaultSubmitSettings == null)
            throw new NotSupportedException("Local CLI submissions require a default settings baseline.");

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;

        submissionOptionsResolver?.SetJobOptions(job.Id, options);

        var settings = SettingsCloner.Clone(defaultSubmitSettings);
        ApplySubmissionOptionsToInheritedSettings(settings, options);
        NormalizeLocalSettings(settings);

        engine.Enqueue(job, settings);
        return Task.FromResult(stateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job));
    }

    public Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(stateStore.GetJobs(query));
    }

    public Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(stateStore.GetJobDetail(jobId));
    }

    public Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var job = engine.GetJob(displayId);
        if (job == null || (workflowId.HasValue && job.WorkflowId != workflowId.Value))
            return Task.FromResult<JobDetailDto?>(null);
        return Task.FromResult(stateStore.GetJobDetail(job.Id));
    }

    public Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(stateStore.GetWorkflow(workflowId));
    }

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetFileResultsAsync(jobId, new FileSearchProjectionRequestDto(), ct);

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request.SongQuery != null
                ? new FileSearchProjection(
                    JobRequestMapper.ToSongQuery(request.SongQuery),
                    request.IncludeFullResults)
                : searchJob.DefaultFileProjection
                    ?? new FileSearchProjection(new SongQuery { Title = searchJob.QueryText });
            var snapshot = searchJob.GetSortedTrackCandidates(projection, searchJob.Config.Search, engine.UserSuccessCounts);
            return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(ToFileCandidateDto).ToList()));
        }

        var songJob = stateStore.GetJob<SongJob>(jobId);
        if (songJob == null)
            return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(null);

        return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(new(
            Revision: 0,
            IsComplete: songJob.State is not (JobState.Pending or JobState.Searching),
            Items: songJob.Candidates?.Select(ToFileCandidateDto).ToList() ?? []));
    }

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
        => GetFolderResultsAsync(jobId, null, includeFiles, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => GetFolderResultsAsync(jobId, request.AlbumQuery, request.IncludeFiles, ct);

    private Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, AlbumQueryDto? albumQuery, bool includeFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = albumQuery != null
                ? new FolderSearchProjection(JobRequestMapper.ToAlbumQuery(albumQuery), includeFiles)
                : searchJob.DefaultFolderProjection is { } defaultProjection
                    ? defaultProjection with { IncludeFiles = includeFiles }
                    : null;
            if (projection == null)
                throw new ArgumentException("Album folder projection requires an album query.");

            var snapshot = searchJob.GetAlbumFolders(projection, searchJob.Config.Search);
            return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(folder => new AlbumFolderDto(
                    new AlbumFolderRefDto(folder.Username, folder.FolderPath),
                    folder.Username,
                    folder.FolderPath,
                    new PeerInfoDto(
                        folder.Username,
                        folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.HasFreeUploadSlot,
                        folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.UploadSpeed),
                    folder.SearchFileCount,
                    folder.SearchAudioFileCount,
                    includeFiles
                        ? folder.Files
                            .Where(song => song.ResolvedTarget != null)
                            .Select(song => ToFileCandidateDto(song.ResolvedTarget!))
                            .ToList()
                        : null,
                    folder.IsFullyRetrieved)).ToList()));
        }

        var albumJob = stateStore.GetJob<AlbumJob>(jobId);
        if (albumJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(null);

        return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(new(
            Revision: 0,
            IsComplete: albumJob.State is not (JobState.Pending or JobState.Searching),
            Items: albumJob.Results.Select(folder => new AlbumFolderDto(
                new AlbumFolderRefDto(folder.Username, folder.FolderPath),
                folder.Username,
                folder.FolderPath,
                new PeerInfoDto(
                    folder.Username,
                    folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.HasFreeUploadSlot,
                    folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.UploadSpeed),
                folder.SearchFileCount,
                folder.SearchAudioFileCount,
                includeFiles
                    ? folder.Files
                        .Where(song => song.ResolvedTarget != null)
                        .Select(song => ToFileCandidateDto(song.ResolvedTarget!))
                        .ToList()
                    : null,
                folder.IsFullyRetrieved)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetAggregateTrackResultsAsync(jobId, new AggregateTrackProjectionRequestDto(), ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request.SongQuery != null
                ? new AggregateTrackProjection(JobRequestMapper.ToSongQuery(request.SongQuery))
                : searchJob.DefaultAggregateTrackProjection
                    ?? (searchJob.DefaultFileProjection is { } fileProjection
                        ? new AggregateTrackProjection(fileProjection.Query)
                        : new AggregateTrackProjection(new SongQuery { Title = searchJob.QueryText }));
            bool includeCandidates = request.IncludeCandidates;
            var snapshot = searchJob.GetAggregateTracks(projection, searchJob.Config.Search, engine.UserSuccessCounts);
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(song => new AggregateTrackCandidateDto(
                    ToSongQueryDto(song.Query),
                    song.ItemName,
                    includeCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList()));
        }

        var aggregateJob = stateStore.GetJob<AggregateJob>(jobId);
        if (aggregateJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(null);

        bool includeAggregateCandidates = request.IncludeCandidates;
        return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
            Revision: 0,
            IsComplete: aggregateJob.State is not (JobState.Pending or JobState.Searching),
            Items: aggregateJob.Songs.Select(song => new AggregateTrackCandidateDto(
                ToSongQueryDto(song.Query),
                song.ItemName,
                includeAggregateCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, null, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, request, ct);

    private Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsCoreAsync(Guid jobId, AggregateAlbumProjectionRequestDto? request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request?.AlbumQuery != null
                ? new AggregateAlbumProjection(JobRequestMapper.ToAlbumQuery(request.AlbumQuery))
                : searchJob.DefaultAggregateAlbumProjection
                    ?? (searchJob.DefaultFolderProjection is { } folderProjection
                        ? new AggregateAlbumProjection(folderProjection.Query)
                        : null);
            if (projection == null)
                throw new ArgumentException("Aggregate album projection requires an album query.");

            bool includeFolders = request?.IncludeFolders ?? false;
            var snapshot = searchJob.GetAggregateAlbums(projection, searchJob.Config.Search);
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(album => new AggregateAlbumCandidateDto(
                    ToAlbumQueryDto(album.Query),
                    album.ItemName,
                    includeFolders ? [..album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true))] : null)).ToList()));
        }

        var albumAggregateJob = stateStore.GetJob<AlbumAggregateJob>(jobId);
        if (albumAggregateJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(null);

        bool includeAggregateFolders = request?.IncludeFolders ?? false;
        return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
            Revision: 0,
            IsComplete: albumAggregateJob.State is not (JobState.Pending or JobState.Searching),
            Items: albumAggregateJob.Albums.Select(album => new AggregateAlbumCandidateDto(
                ToAlbumQueryDto(album.Query),
                album.ItemName,
                includeAggregateFolders ? [..album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true))] : null)).ToList()));
    }

    public Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = stateStore.GetJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = FindAlbumFolder(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath, WorkflowId = sourceJob.WorkflowId };
        stateStore.SetSourceJob(retrieveJob.Id, sourceJobId);
        engine.Enqueue(retrieveJob, sourceJob.Config);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId));
    }

    public async Task<int> RetrieveFolderAndWaitAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = stateStore.GetJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return 0;

        var folder = FindAlbumFolder(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = await engine.ProcessFolderRetrieval(folder, sourceJob);
        return retrieveJob.NewFilesFoundCount;
    }

    public Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = stateStore.GetJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(null);

        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");

        var summaries = new List<JobSummaryDto>();
        var settings = BuildFollowUpSettings(sourceJob, request.Options);

        if (sourceJob is SongJob manualSong && manualSong.State == JobState.AwaitingSelection)
        {
            if (request.Files.Count != 1)
                throw new ArgumentException("Manual song jobs require exactly one selected file.");

            var candidate = FindFileCandidate(sourceJob, request.Files[0]);
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            manualSong.ResolvedTarget = candidate;
            manualSong.Candidates ??= [candidate];
            if (!manualSong.Candidates.Contains(candidate))
                manualSong.Candidates.Insert(0, candidate);
            manualSong.UpdateState(JobState.Pending);
            submissionOptionsResolver?.SetJobOptions(manualSong.Id, request.Options);
            engine.Enqueue(manualSong, settings);
            summaries.Add(stateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong));
            return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(summaries);
        }

        foreach (var file in request.Files)
        {
            var candidate = FindFileCandidate(sourceJob, file);
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            var songQuery = sourceJob switch
            {
                SearchJob searchJob => searchJob.DefaultFileProjection?.Query
                    ?? Searcher.InferSongQuery(candidate.Filename, new SongQuery { Title = searchJob.QueryText }),
                SongJob existingSongJob => existingSongJob.Query,
                AggregateJob aggregateJob => aggregateJob.Songs
                    .FirstOrDefault(song => song.Candidates?.Contains(candidate) == true)?.Query
                    ?? Searcher.InferSongQuery(candidate.Filename, aggregateJob.Query),
                _ => Searcher.InferSongQuery(candidate.Filename, sourceJob.QueryTrack ?? new SongQuery()),
            };

            var followUpSongJob = new SongJob(new SongQuery(songQuery))
            {
                ResolvedTarget = candidate,
                ItemName = sourceJob.ItemName,
                WorkflowId = sourceJob.WorkflowId,
            };

            stateStore.SetSourceJob(followUpSongJob.Id, sourceJobId);
            submissionOptionsResolver?.SetJobOptions(followUpSongJob.Id, request.Options);
            engine.Enqueue(followUpSongJob, settings);
            summaries.Add(stateStore.GetJobSummary(followUpSongJob.Id) ?? BuildSubmittedJobSummary(followUpSongJob, sourceJobId));
        }

        return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(summaries);
    }

    public Task<JobSummaryDto?> StartFolderDownloadAsync(Guid sourceJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = stateStore.GetJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = FindAlbumFolder(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        var settings = BuildFollowUpSettings(sourceJob, request.Options);

        if (sourceJob is AlbumJob manualAlbum && manualAlbum.State == JobState.AwaitingSelection)
        {
            manualAlbum.ResolvedTarget = folder;
            JobRequestMapper.ApplyFolderDownloadSelection(manualAlbum, request.Selection);
            if (!manualAlbum.Results.Contains(folder))
                manualAlbum.Results.Insert(0, folder);
            manualAlbum.UpdateState(JobState.Pending);
            submissionOptionsResolver?.SetJobOptions(manualAlbum.Id, request.Options);
            engine.Enqueue(manualAlbum, settings);
            return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(manualAlbum.Id) ?? BuildSubmittedJobSummary(manualAlbum));
        }

        var albumQuery = request.AlbumQuery != null
            ? JobRequestMapper.ToAlbumQuery(request.AlbumQuery)
            : sourceJob switch
            {
                SearchJob searchJob => searchJob.DefaultFolderProjection?.Query,
                AlbumJob existingAlbumJob => existingAlbumJob.Query,
                AlbumAggregateJob aggregate => aggregate.Query,
                _ => null,
            };
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from this job require an album query.");

        string? itemName = sourceJob.ItemName;
        if (sourceJob is SearchJob { DefaultAggregateAlbumProjection: not null } && !string.IsNullOrWhiteSpace(folder.FolderPath))
            itemName = Utils.GetBaseNameSlsk(folder.FolderPath);

        var followUpAlbumJob = new AlbumJob(new AlbumQuery(albumQuery))
        {
            ResolvedTarget = folder,
            ItemName = itemName,
            WorkflowId = sourceJob.WorkflowId,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
        };
        JobRequestMapper.ApplyFolderDownloadSelection(followUpAlbumJob, request.Selection);

        stateStore.SetSourceJob(followUpAlbumJob.Id, sourceJobId);
        submissionOptionsResolver?.SetJobOptions(followUpAlbumJob.Id, request.Options);
        engine.Enqueue(followUpAlbumJob, settings);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(followUpAlbumJob.Id) ?? BuildSubmittedJobSummary(followUpAlbumJob, sourceJobId));
    }

    public Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var job = engine.GetJob(jobId);
        if (job == null || job.State != JobState.AwaitingSelection)
            return Task.FromResult(false);

        job.SetDone();
        return Task.FromResult(true);
    }

    private DownloadSettings BuildFollowUpSettings(Job sourceJob, SubmissionOptionsDto? options)
    {
        var settings = SettingsCloner.Clone(defaultSubmitSettings ?? sourceJob.Config);
        ApplySubmissionOptionsToInheritedSettings(settings, options);
        NormalizeLocalSettings(settings);
        return settings;
    }

    private void ApplySubmissionOptionsToInheritedSettings(DownloadSettings settings, SubmissionOptionsDto? options)
    {
        if (submissionOptionsResolver != null)
            return;

        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);
        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;
    }

    private static void NormalizeLocalSettings(DownloadSettings settings)
    {
        SettingsNormalizer.NormalizeDownloadPaths(settings, settings.RuntimePathContext);
    }

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var job = engine.GetJob(jobId);
        if (job == null)
            return Task.FromResult(false);

        job.Cancel();
        return Task.FromResult(true);
    }

    public async Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var job = engine.GetJob(displayId);
        if (job == null || (workflowId.HasValue && job.WorkflowId != workflowId.Value))
            return false;

        job.Cancel();
        return await Task.FromResult(true);
    }

    public Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.CancelWorkflow(workflowId));
    }

    public Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.TryNextCandidate(jobId));
    }

    public Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var job = engine.GetJob(displayId);
        if (job == null || (workflowId.HasValue && job.WorkflowId != workflowId.Value))
            return Task.FromResult(false);

        return Task.FromResult(engine.TryNextCandidate(job.Id));
    }

    private void Publish(string type, object payload)
    {
        var descriptor = ServerEventCatalog.Describe(type);
        EventReceived?.Invoke(new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            descriptor.Category,
            descriptor.SnapshotInvalidation,
            GetWorkflowId(payload),
            payload));
    }

    private static Guid? GetWorkflowId(object payload)
        => payload switch
        {
            JobSummaryDto summary => summary.WorkflowId,
            WorkflowSummaryDto summary => summary.WorkflowId,
            SearchUpdatedDto update => update.WorkflowId,
            _ => null,
        };

    private AlbumFolder? FindAlbumFolder(Job sourceJob, AlbumFolderRefDto folderRef, AlbumQueryDto? albumQuery = null)
    {
        static bool Matches(AlbumFolder folder, AlbumFolderRefDto folderRef)
            => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal);

        if (sourceJob is SearchJob searchJob)
        {
            if (searchJob.Config == null)
                return null;

            var projection = albumQuery != null
                ? new FolderSearchProjection(JobRequestMapper.ToAlbumQuery(albumQuery))
                : searchJob.DefaultFolderProjection;
            if (projection == null)
                return null;

            return searchJob.GetAlbumFolders(projection, searchJob.Config.Search).Items.FirstOrDefault(folder => Matches(folder, folderRef));
        }

        if (sourceJob is AlbumJob albumJob)
            return albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef));

        if (sourceJob is AlbumAggregateJob aggregateJob)
            return aggregateJob.Albums
                .Where(album => albumQuery == null || AlbumQueriesEqual(album.Query, JobRequestMapper.ToAlbumQuery(albumQuery)))
                .SelectMany(album => album.Results)
                .FirstOrDefault(folder => Matches(folder, folderRef));

        return null;
    }

    private FileCandidate? FindFileCandidate(Job sourceJob, FileCandidateRefDto candidateRef)
    {
        static bool Matches(FileCandidate candidate, FileCandidateRefDto candidateRef)
            => string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal);

        if (sourceJob is SearchJob searchJob)
            return FindTrackCandidate(searchJob, candidateRef);

        if (sourceJob is SongJob songJob)
            return songJob.Candidates?.FirstOrDefault(candidate => Matches(candidate, candidateRef));

        if (sourceJob is AggregateJob aggregateJob)
            return aggregateJob.Songs
                .SelectMany(song => song.Candidates ?? Enumerable.Empty<FileCandidate>())
                .FirstOrDefault(candidate => Matches(candidate, candidateRef));

        if (sourceJob is AlbumJob albumJob)
            return albumJob.Results
                .SelectMany(folder => folder.Files)
                .Select(song => song.ResolvedTarget)
                .OfType<FileCandidate>()
                .FirstOrDefault(candidate => Matches(candidate, candidateRef));

        if (sourceJob is AlbumAggregateJob aggregateAlbumJob)
            return aggregateAlbumJob.Albums
                .SelectMany(album => album.Results)
                .SelectMany(folder => folder.Files)
                .Select(song => song.ResolvedTarget)
                .OfType<FileCandidate>()
                .FirstOrDefault(candidate => Matches(candidate, candidateRef));

        return null;
    }

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase);

    private FileCandidate? FindTrackCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
    {
        if (searchJob.Config == null)
            return null;

        var trackCandidate = searchJob.GetSortedTrackCandidates(searchJob.Config.Search, engine.UserSuccessCounts)
            .Items
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));

        if (trackCandidate != null || searchJob.DefaultFolderProjection == null)
            return trackCandidate ?? FindRawFileCandidate(searchJob, candidateRef);

        return searchJob.GetAlbumFolders(searchJob.Config.Search)
            .Items
            .SelectMany(folder => folder.Files)
            .Select(song => song.ResolvedTarget)
            .FirstOrDefault(candidate =>
                candidate != null
                && string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));
    }

    private static FileCandidate? FindRawFileCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
        => searchJob.Snapshot()
            .Select(pair => new FileCandidate(pair.Response, pair.File))
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => new(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            EngineStateStore.GetJobKind(job),
            EngineStateStore.ToServerJobState(job.State),
            job.ItemName,
            job.ToString(noInfo: true),
            EngineStateStore.ToServerFailureReason(job.FailureReason),
            job.FailureMessage,
            null,
            null,
            sourceJobId,
            job.Discovery?.ResultCount,
            job.Discovery?.LockedFileCount,
            job.Config?.AppliedAutoProfiles?.ToList() ?? [],
            []);

    private JobSummaryDto GetSummary(Job job)
        => stateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);

    private static SongQueryDto ToSongQueryDto(SongQuery query)
        => new(Optional(query.Artist), Optional(query.Title), Optional(query.Album), Optional(query.URI), Optional(query.Length), query.ArtistMaybeWrong);

    private static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query)
        => new(Optional(query.Artist), Optional(query.Album), Optional(query.SearchHint), Optional(query.URI), query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate)
        => new(
            new FileCandidateRefDto(candidate.Username, candidate.Filename),
            candidate.Username,
            candidate.Filename,
            new PeerInfoDto(candidate.Username, candidate.Response.HasFreeUploadSlot, candidate.Response.UploadSpeed),
            candidate.File.Size,
            candidate.File.BitRate,
            candidate.File.SampleRate,
            candidate.File.Length,
            candidate.File.Extension,
            candidate.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList());

    private static SongJobPayloadDto ToSongJobPayloadDto(SongJob song)
        => new(
            ToSongQueryDto(song.Query),
            song.Candidates?.Count,
            song.DownloadPath,
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.Response.HasFreeUploadSlot,
            song.ResolvedTarget?.Response.UploadSpeed,
            song.ResolvedTarget?.File.Size,
            song.ResolvedTarget?.File.SampleRate,
            song.ResolvedTarget?.File.Extension,
            song.ResolvedTarget?.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList(),
            song.Id,
            song.DisplayId,
            song.Candidates?.Select(ToFileCandidateDto).ToList(),
            EngineStateStore.ToServerJobState(song.State),
            EngineStateStore.ToServerFailureReason(song.FailureReason),
            song.FailureMessage);

    private static AlbumFolderDto ToAlbumFolderDto(AlbumFolder folder, bool includeFiles)
        => new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            new PeerInfoDto(
                folder.Username,
                folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.HasFreeUploadSlot,
                folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.UploadSpeed),
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            includeFiles
                ? folder.Files
                    .Where(song => song.ResolvedTarget != null)
                    .Select(song => ToFileCandidateDto(song.ResolvedTarget!))
                    .ToList()
                : null,
            folder.IsFullyRetrieved);
}
