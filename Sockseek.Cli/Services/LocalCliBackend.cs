using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Api;
using Sockseek.Server;
using Sockseek.Core.Planning;

namespace Sockseek.Cli;

internal sealed class LocalCliBackend
    : ICliBackend, IAsyncDisposable
{
    private readonly DownloadEngine engine;
    private readonly DownloadSettings? defaultSubmitSettings;
    private readonly SubmissionOptionsJobSettingsResolver? submissionOptionsResolver;
    private readonly DownloadSettingsPatchDto? explicitCliDownloadSettings;
    private readonly EngineStateStore stateStore = new();
    private readonly DaemonClientStore daemonStore = new();
    private readonly IAsyncDisposable publication;
    private readonly ConcurrentDictionary<Guid, byte> liveWorkflowSubscriptions = [];
    private readonly ConcurrentDictionary<LocalFileProjectionKey, LocalFileProjectionState>
        fileProjectionStates = [];
    private volatile bool liveDaemonSubscription;

    public event Action<DaemonClientUpdate>? StateUpdated;
    public event Action<ActivityEventDto>? ActivityReceived;
    public event Action<StateSnapshotDto>? LiveSnapshotApplied;

    public DaemonClientStore ClientStore => daemonStore;

    public LocalCliBackend(
        DownloadEngine engine,
        DownloadSettings? defaultSubmitSettings = null,
        SubmissionOptionsJobSettingsResolver? submissionOptionsResolver = null,
        DownloadSettingsPatchDto? explicitCliDownloadSettings = null)
    {
        this.engine = engine;
        this.submissionOptionsResolver = submissionOptionsResolver;
        this.explicitCliDownloadSettings = explicitCliDownloadSettings;
        this.defaultSubmitSettings = defaultSubmitSettings != null
            ? SettingsCloner.Clone(defaultSubmitSettings)
            : null;
        stateStore.AttachEngine(engine);
        stateStore.StateBatchPublished += HandleStateBatch;
        new EngineActivityDtoAdapter(stateStore).Attach(engine.Events, engine.SearchEvents);
        publication = stateStore.StartStatePublication();
    }

    public async ValueTask DisposeAsync()
    {
        await publication.DisposeAsync().ConfigureAwait(false);
        stateStore.StateBatchPublished -= HandleStateBatch;
        stateStore.DetachEngine(engine);
    }

    public Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => request.ArtifactId != null
            ? throw new NotSupportedException(
                "Daemon input artifacts are unavailable in local CLI mode; use a local path instead.")
            : SubmitJobAsync(JobRequestMapper.CreateExtractJob(request), request.Options, ct);

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
    {
        var job = JobRequestMapper.CreateJobList(request);
        JobRequestMapper.ApplyDraftDownloadSettings(
            job,
            request.Jobs,
            (item, patch) => submissionOptionsResolver?.SetJobOptions(
                item.Id,
                new SubmissionOptionsDto(DownloadSettings: patch)));
        return SubmitJobAsync(job, request.Options, ct, request.Jobs);
    }

    public Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (liveDaemonSubscription)
            throw new InvalidOperationException("Cannot mix daemon and workflow subscriptions in one local backend.");
        if (liveWorkflowSubscriptions.TryAdd(workflowId, 0))
        {
            stateStore.ReserveWorkflowStream(workflowId);
            var snapshot = stateStore.GetWorkflowSnapshot(workflowId);
            LiveSnapshotApplied?.Invoke(snapshot);
            var update = daemonStore.ApplySnapshot(snapshot);
            StateUpdated?.Invoke(update);
        }
        return Task.CompletedTask;
    }

    public Task SubscribeAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!liveWorkflowSubscriptions.IsEmpty)
            throw new InvalidOperationException("Cannot mix daemon and workflow subscriptions in one local backend.");
        if (!liveDaemonSubscription)
        {
            liveDaemonSubscription = true;
            var snapshot = stateStore.GetDaemonSnapshot();
            LiveSnapshotApplied?.Invoke(snapshot);
            var update = daemonStore.ApplySnapshot(snapshot);
            StateUpdated?.Invoke(update);
        }
        return Task.CompletedTask;
    }

    private async Task<JobSummaryDto> SubmitJobAsync(
        Job job,
        SubmissionOptionsDto? options,
        CancellationToken ct,
        IReadOnlyList<JobDraftDto>? childDrafts = null)
    {
        ct.ThrowIfCancellationRequested();
        if (defaultSubmitSettings == null)
            throw new NotSupportedException("Local CLI submissions require a default settings baseline.");

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);

        if (!liveDaemonSubscription)
            SubscribeWorkflowAsync(job.WorkflowId, ct).GetAwaiter().GetResult();

        submissionOptionsResolver?.SetJobOptions(job.Id, options);

        var settings = submissionOptionsResolver?.Resolve(defaultSubmitSettings, job)
            ?? SettingsCloner.Clone(defaultSubmitSettings);
        ApplySubmissionOptionsToInheritedSettings(settings, options);
        bool containsRemoteTransfer = RemoteTransferSubmissionPolicy
            .ValidateOverrides(job, settings, options?.DownloadSettings, ResolveChildSettings, childDrafts);
        if (containsRemoteTransfer)
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(explicitCliDownloadSettings);
        NormalizeLocalSettings(settings);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);

        var planner = new JobPlanner(
            (IJobSettingsResolver?)submissionOptionsResolver
                ?? DefaultJobSettingsResolver.Instance);
        await planner.PrepareResolvedAsync(job, settings, ct).ConfigureAwait(false);
        SubmissionIdentity.AssignAccepted(job, SubmissionSpecification.Create(job));
        EnqueueAccepted(job, settings, settingsAreFinal: true);
        return stateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
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

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request.SongQuery != null
                ? new FileSearchProjection(
                    JobRequestMapper.ToSongQuery(request.SongQuery),
                    request.IncludeFullResults)
                : (searchJob.DefaultFileProjection
                    ?? new FileSearchProjection(new SongQuery { Title = searchJob.QueryText }))
                    with { IncludeFullResults = request.IncludeFullResults };
            LocalFileProjectionState state = fileProjectionStates.GetOrAdd(
                LocalFileProjectionKey.Create(searchJob.Id, projection),
                _ => new LocalFileProjectionState(
                    projection,
                    searchJob.Config.Search,
                    engine.GetUserSuccessCounts(searchJob.WorkflowId).ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.Ordinal)));
            SearchViewKernelSnapshot snapshot = state.Snapshot(searchJob);
            return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(new(
                snapshot.SourceRevision,
                snapshot.IsComplete,
                snapshot.Files.Select(file => CliSearchProjectionMapper.ToDto(
                    file.Input.ToFileCandidate(file.ConditionFacts))).ToList()));
        }

        var songJob = GetRuntimeJob<SongJob>(jobId);
        if (songJob == null)
            return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(null);

        return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(new(
            Revision: 0,
            IsComplete: songJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: songJob.Candidates?.Select(CliSearchProjectionMapper.ToDto).ToList() ?? []));
    }

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
        => GetFolderResultsAsync(jobId, null, includeFiles, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => GetFolderResultsAsync(jobId, request.AlbumQuery, request.IncludeFiles, ct);

    private Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, AlbumQueryDto? albumQuery, bool includeFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = albumQuery != null
                ? new FolderSearchProjection(JobRequestMapper.ToAlbumQuery(albumQuery), includeFiles)
                : searchJob.DefaultFolderProjection is { } defaultProjection
                    ? defaultProjection with { IncludeFiles = includeFiles }
                    : null;
            if (projection == null)
                throw new ArgumentException("Album folder projection requires an album query.");

            var snapshot = searchJob.GetAlbumFolders(projection, searchJob.Config.Search, includeLocked: true);
            return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(folder => CliSearchProjectionMapper.ToDto(folder, includeFiles)).ToList()));
        }

        var albumJob = GetRuntimeJob<AlbumJob>(jobId);
        if (albumJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(null);

        var folders = JobRequestMapper.ProjectAlbumJobFolders(albumJob, engine.GetUserSuccessCounts(albumJob.WorkflowId));
        return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(new(
            Revision: 0,
            IsComplete: albumJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: folders.Select(folder => CliSearchProjectionMapper.ToDto(folder, includeFiles)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetAggregateTrackResultsAsync(jobId, new AggregateTrackProjectionRequestDto(), ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request.SongQuery != null
                ? new AggregateTrackProjection(JobRequestMapper.ToSongQuery(request.SongQuery))
                : searchJob.DefaultAggregateTrackProjection
                    ?? (searchJob.DefaultFileProjection is { } fileProjection
                        ? new AggregateTrackProjection(fileProjection.Query)
                        : new AggregateTrackProjection(new SongQuery { Title = searchJob.QueryText }));
            bool includeCandidates = request.IncludeCandidates;
            var snapshot = searchJob.GetAggregateTracks(projection, searchJob.Config.Search, engine.GetUserSuccessCounts(searchJob.WorkflowId), includeLocked: true);
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(song => new AggregateTrackCandidateDto(
                    ServerSnapshotMapper.ToSongQueryDto(song.Query),
                    song.ItemName,
                    includeCandidates ? song.Candidates?.Select(CliSearchProjectionMapper.ToDto).ToList() : null)).ToList()));
        }

        var aggregateJob = GetRuntimeJob<AggregateJob>(jobId);
        if (aggregateJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(null);

        bool includeAggregateCandidates = request.IncludeCandidates;
        return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
            Revision: 0,
            IsComplete: aggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: aggregateJob.Songs.Select(song => new AggregateTrackCandidateDto(
                ServerSnapshotMapper.ToSongQueryDto(song.Query),
                song.ItemName,
                includeAggregateCandidates ? song.Candidates?.Select(CliSearchProjectionMapper.ToDto).ToList() : null)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, null, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, request, ct);

    private Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsCoreAsync(Guid jobId, AggregateAlbumProjectionRequestDto? request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
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
            var snapshot = searchJob.GetAggregateAlbums(projection, searchJob.Config.Search, includeLocked: true);
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(album => new AggregateAlbumCandidateDto(
                    ServerSnapshotMapper.ToAlbumQueryDto(album.Query),
                    album.ItemName,
                    includeFolders ? [..album.Results.Select(f => CliSearchProjectionMapper.ToDto(f, includeFiles: true))] : null)).ToList()));
        }

        var albumAggregateJob = GetRuntimeJob<AlbumAggregateJob>(jobId);
        if (albumAggregateJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(null);

        bool includeAggregateFolders = request?.IncludeFolders ?? false;
        return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
            Revision: 0,
            IsComplete: albumAggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: albumAggregateJob.Albums.Select(album => new AggregateAlbumCandidateDto(
                ServerSnapshotMapper.ToAlbumQueryDto(album.Query),
                album.ItemName,
                includeAggregateFolders ? [..album.Results.Select(f => CliSearchProjectionMapper.ToDto(f, includeFiles: true))] : null)).ToList()));
    }

    public Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = JobRequestMapper.FindAlbumFolderForRetrieval(
            sourceJob,
            request.Folder,
            engine.GetUserSuccessCounts(sourceJob.WorkflowId),
            request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = new RetrieveFolderJob(folder.DirectoryIdentity) { ItemName = folder.FolderPath, WorkflowId = sourceJob.WorkflowId };
        EnqueueAccepted(retrieveJob, sourceJob.Config, sourceJobId);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId));
    }

    public async Task<RetrieveFolderJobPayloadDto?> RetrieveFolderAndWaitAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return null;

        var folder = JobRequestMapper.FindAlbumFolderForRetrieval(
            sourceJob,
            request.Folder,
            engine.GetUserSuccessCounts(sourceJob.WorkflowId),
            request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = await engine.ProcessFolderRetrieval(folder, sourceJob);
        return new RetrieveFolderJobPayloadDto(
            retrieveJob.Directory.FolderPath,
            retrieveJob.Directory.Username,
            retrieveJob.NewFilesFoundCount,
            ServerSnapshotMapper.ToServerFolderRetrievalOutcome(retrieveJob.RetrievalOutcome),
            retrieveJob.RetrievalCancelled);
    }

    public Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(null);

        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");
        if (request.RequestedMode == ServerExtractionMode.Album)
            throw new ArgumentException("A file selection cannot be interpreted as an album.");

        var summaries = new List<JobSummaryDto>();
        if (request.RequestedMode == ServerExtractionMode.General
            && request.Options?.DownloadSettings is { } explicitPatch)
        {
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        }

        if ((request.RequestedMode is null or ServerExtractionMode.Song)
            && sourceJob is SongJob manualSong && manualSong.IsAwaitingSelection)
        {
            if (request.Files.Count != 1)
                throw new ArgumentException("Manual song jobs require exactly one selected file.");

            var candidate = JobRequestMapper.FindFileCandidate(
                sourceJob,
                request.Files[0],
                engine.GetUserSuccessCounts(sourceJob.WorkflowId));
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            if (!engine.TryStartManualSongSelection(sourceJobId, candidate,
                song => ApplyManualSelectionOptions(sourceJob, song, request.Options), out _))
                throw new ArgumentException("The song is no longer awaiting selection.");
            summaries.Add(stateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong));
            return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(summaries);
        }

        foreach (var file in request.Files)
        {
            var candidate = JobRequestMapper.FindFileCandidate(
                sourceJob,
                file,
                engine.GetUserSuccessCounts(sourceJob.WorkflowId));
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            if ((request.RequestedMode is null or ServerExtractionMode.Song)
                && sourceJob is AggregateJob && sourceJob.IsAwaitingSelection)
            {
                if (!engine.TryStartManualSongSelection(sourceJobId, candidate,
                    song => ApplyManualSelectionOptions(sourceJob, song, request.Options), out var selectedSong)
                    || selectedSong == null)
                    throw new ArgumentException("The aggregate is no longer accepting selections.");
                summaries.Add(stateStore.GetJobSummary(selectedSong.Id) ?? BuildSubmittedJobSummary(selectedSong, sourceJobId));
                continue;
            }

            Job followUpJob = JobRequestMapper.CreateFileSelectionFollowUp(
                sourceJob,
                candidate,
                request.RequestedMode?.ToCore());
            var settings = BuildFollowUpSettings(sourceJob, followUpJob, request.Options);
            if (followUpJob is RemoteFileJob)
            {
                RemoteTransferSettingsValidator.ValidateExplicitNameFormat(explicitCliDownloadSettings);
                RemoteTransferSubmissionPolicy.NormalizeSettings(followUpJob, settings);
            }

            JobRequestMapper.PropagateSourceMutationToFollowUp(sourceJob, followUpJob);
            EnqueueAccepted(followUpJob, settings, sourceJobId);
            summaries.Add(stateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId));
        }

        return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(summaries);
    }

    public Task<JobSummaryDto?> StartFolderDownloadAsync(Guid sourceJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.RequestedMode == ServerExtractionMode.Song)
            throw new ArgumentException("A directory selection cannot be interpreted as one song.");

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = JobRequestMapper.FindAlbumFolder(
            sourceJob,
            request.Folder,
            engine.GetUserSuccessCounts(sourceJob.WorkflowId),
            request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        if (request.RequestedMode == ServerExtractionMode.General
            && request.Options?.DownloadSettings is { } explicitPatch)
        {
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        }

        if (request.RequestedMode == ServerExtractionMode.General)
        {
            var directoryJob = JobRequestMapper.CreateRemoteDirectoryDownload(folder, request.Selection);
            directoryJob.ItemName = sourceJob.ItemName;
            directoryJob.WorkflowId = sourceJob.WorkflowId;
            var settings = BuildFollowUpSettings(sourceJob, directoryJob, request.Options);
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(explicitCliDownloadSettings);
            RemoteTransferSubmissionPolicy.NormalizeSettings(directoryJob, settings);
            JobRequestMapper.PropagateSourceMutationToFollowUp(sourceJob, directoryJob);
            EnqueueAccepted(directoryJob, settings, sourceJobId);
            return Task.FromResult<JobSummaryDto?>(
                stateStore.GetJobSummary(directoryJob.Id)
                ?? BuildSubmittedJobSummary(directoryJob, sourceJobId));
        }

        AlbumQuery? albumQuery = JobRequestMapper.ResolveFolderSelectionQuery(
            sourceJob,
            request.AlbumQuery);

        bool manualSelection = sourceJob.IsAwaitingSelection && sourceJob is AlbumJob or AlbumAggregateJob;
        if (engine.TryStartManualAlbumSelection(
            sourceJobId,
            folder,
            albumQuery,
            album =>
            {
                JobRequestMapper.ApplyFolderDownloadSelection(album, request.Selection);
                ApplyManualSelectionOptions(sourceJob, album, request.Options);
            },
            out var selectedAlbum))
        {
            if (sourceJob is AlbumAggregateJob)
                stateStore.SetSourceJob(selectedAlbum!.Id, sourceJobId);

            return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(selectedAlbum!.Id) ?? BuildSubmittedJobSummary(selectedAlbum!, sourceJobId));
        }
        if (manualSelection)
            throw new ArgumentException("The job is no longer accepting selections.");
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from this job require an album query.");

        AlbumJob followUpAlbumJob = JobRequestMapper.CreateAlbumSelectionFollowUp(
            sourceJob,
            folder,
            albumQuery,
            request.Selection);
        var albumSettings = BuildFollowUpSettings(sourceJob, followUpAlbumJob, request.Options);
        JobRequestMapper.PropagateSourceMutationToFollowUp(sourceJob, followUpAlbumJob);

        EnqueueAccepted(followUpAlbumJob, albumSettings, sourceJobId);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(followUpAlbumJob.Id) ?? BuildSubmittedJobSummary(followUpAlbumJob, sourceJobId));
    }

    public Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return engine.CompleteManualSelectionAsync(jobId);
    }

    public Task<bool> SkipManualSelectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return engine.SkipManualSelectionAsync(jobId);
    }

    private void EnqueueAccepted(
        Job job,
        DownloadSettings settings,
        Guid? sourceJobId = null,
        bool settingsAreFinal = false)
    {
        if (job.SubmissionId == null)
            SubmissionIdentity.AssignAccepted(job, settings);
        if (sourceJobId.HasValue)
            stateStore.SetSourceJob(job.Id, sourceJobId.Value);
        job.EnsureDisplayId();
        engine.Enqueue(job, settings, settingsAreFinal: settingsAreFinal);
    }

    private DownloadSettings ResolveChildSettings(
        DownloadSettings parentSettings,
        Job child,
        DownloadSettingsPatchDto? fallbackPatch = null)
    {
        if (submissionOptionsResolver != null)
            return submissionOptionsResolver.Resolve(
                parentSettings,
                child,
                JobSettingsInheritance.SearchConstraints);

        var settings = SettingsCloner.Clone(parentSettings);
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, fallbackPatch);
        return settings;
    }

    private DownloadSettings BuildFollowUpSettings(
        Job sourceJob,
        Job followUpJob,
        SubmissionOptionsDto? options)
    {
        if (submissionOptionsResolver != null)
        {
            submissionOptionsResolver.SetIsolatedJobOptions(followUpJob.Id, options);
            return submissionOptionsResolver.Resolve(
                defaultSubmitSettings ?? sourceJob.Config!,
                followUpJob);
        }

        var settings = SettingsCloner.Clone(defaultSubmitSettings ?? sourceJob.Config);
        ApplySubmissionOptionsToInheritedSettings(settings, options);
        NormalizeLocalSettings(settings);
        return settings;
    }

    private void ApplyManualSelectionOptions(Job source, Job selected, SubmissionOptionsDto? options)
    {
        if (submissionOptionsResolver != null)
        {
            submissionOptionsResolver.ApplyManualSelectionOptions(source, selected, options);
            return;
        }
        if (options == null || !SubmissionOptionsStore.HasSettingsOverrides(options))
            return;
        var settings = SettingsCloner.Clone(selected.Config);
        ApplySubmissionOptionsToInheritedSettings(settings, options);
        NormalizeLocalSettings(settings);
        selected.PlannedSettings = JobSettingsSnapshot.Capture(settings);
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

        return Task.FromResult(engine.CancelJob(jobId));
    }

    public Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.CancelJobByDisplayId(displayId, workflowId));
    }

    public Task<int> CancelAllJobsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.CancelAllJobs());
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
        return Task.FromResult(engine.TryNextCandidateByDisplayId(displayId, workflowId));
    }

    private void HandleStateBatch(StateUpdate batch)
    {
        bool subscribed = batch.Scope.Kind == StateStreamScopeKind.Daemon
            ? liveDaemonSubscription
            : liveWorkflowSubscriptions.ContainsKey(batch.Scope.WorkflowId!.Value);
        if (!subscribed)
            return;

        var update = daemonStore.Apply(batch.ToDto());
        StateUpdated?.Invoke(update);
        foreach (var activity in update.Activity)
            ActivityReceived?.Invoke(activity);
    }

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => ServerSnapshotMapper.ToSubmittedJobSummary(job, sourceJobId);

    private TJob? GetRuntimeJob<TJob>(Guid jobId)
        where TJob : Job
        => engine.GetJob(jobId) as TJob;

    private sealed record LocalFileProjectionKey(
        Guid JobId,
        string Artist,
        string Title,
        string Album,
        string Uri,
        int Length,
        bool ArtistMaybeWrong,
        bool IncludeFullResults)
    {
        public static LocalFileProjectionKey Create(
            Guid jobId,
            FileSearchProjection projection)
            => new(
                jobId,
                projection.Query.Artist,
                projection.Query.Title,
                projection.Query.Album,
                projection.Query.URI,
                projection.Query.Length,
                projection.Query.ArtistMaybeWrong,
                projection.IncludeFullResults);
    }

    private sealed class LocalFileProjectionState
    {
        private readonly object gate = new();
        private readonly SearchViewKernel kernel;

        public LocalFileProjectionState(
            FileSearchProjection projection,
            SearchSettings settings,
            IReadOnlyDictionary<string, int> reputationSnapshot)
            => kernel = new SearchViewKernel(
                projection,
                settings,
                reputationSnapshot);

        public SearchViewKernelSnapshot Snapshot(SearchJob job)
        {
            lock (gate)
            {
                kernel.Apply(
                    job.RawSnapshot(kernel.ConsumedSequence)
                        .Select(result => result.ProjectionInput),
                    job.Revision,
                    job.IsComplete);
                return kernel.Snapshot();
            }
        }
    }
}
