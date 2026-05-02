using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Server;

namespace Sldl.Cli;

internal sealed class LocalCliBackend
    : ICliBackend
{
    private readonly DownloadEngine engine;
    private readonly DownloadSettings? defaultSubmitSettings;
    private readonly EngineStateStore stateStore = new();
    private long nextSequence;

    public event Action<ServerEventEnvelopeDto>? EventReceived;

    public LocalCliBackend(DownloadEngine engine, DownloadSettings? defaultSubmitSettings = null)
    {
        this.engine = engine;
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

        var settings = SettingsCloner.Clone(defaultSubmitSettings);
        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;
        SettingsNormalizer.Normalize(settings);

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
        if (searchJob?.Config == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(null);

        var projection = request.SongQuery != null
            ? new AggregateTrackProjection(JobRequestMapper.ToSongQuery(request.SongQuery))
            : searchJob.DefaultAggregateTrackProjection
                ?? (searchJob.DefaultFileProjection is { } fileProjection
                    ? new AggregateTrackProjection(fileProjection.Query)
                    : new AggregateTrackProjection(new SongQuery { Title = searchJob.QueryText }));
        var snapshot = searchJob.GetAggregateTracks(projection, searchJob.Config.Search, engine.UserSuccessCounts);
        return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(song => new AggregateTrackCandidateDto(ToSongQueryDto(song.Query), song.ItemName)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, null, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, request, ct);

    private Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsCoreAsync(Guid jobId, AggregateAlbumProjectionRequestDto? request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(null);

        var projection = request?.AlbumQuery != null
            ? new AggregateAlbumProjection(JobRequestMapper.ToAlbumQuery(request.AlbumQuery))
            : searchJob.DefaultAggregateAlbumProjection
                ?? (searchJob.DefaultFolderProjection is { } folderProjection
                    ? new AggregateAlbumProjection(folderProjection.Query)
                    : null);
        if (projection == null)
            throw new ArgumentException("Aggregate album projection requires an album query.");

        var snapshot = searchJob.GetAggregateAlbums(projection, searchJob.Config.Search);
        return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(album => new AggregateAlbumCandidateDto(ToAlbumQueryDto(album.Query), album.ItemName)).ToList()));
    }

    public Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = FindAlbumFolder(searchJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this search job.");

        var retrieveJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath };
        retrieveJob.WorkflowId = searchJob.WorkflowId;
        engine.Enqueue(retrieveJob, searchJob.Config);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob));
    }

    public async Task<int> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return 0;

        var folder = FindAlbumFolder(searchJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this search job.");

        return await engine.ProcessFolderRetrieval(folder, searchJob);
    }

    public Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(null);

        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");

        var summaries = new List<JobSummaryDto>();
        foreach (var file in request.Files)
        {
            var candidate = FindTrackCandidate(searchJob, file);
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this search job.");

            var songQuery = searchJob.DefaultFileProjection?.Query
                ?? Searcher.InferSongQuery(candidate.Filename, new SongQuery { Title = searchJob.QueryText });
            var songJob = new SongJob(new SongQuery(songQuery))
            {
                ResolvedTarget = candidate,
                ItemName = searchJob.ItemName,
                WorkflowId = searchJob.WorkflowId,
            };

            var settings = SettingsCloner.Clone(defaultSubmitSettings ?? searchJob.Config);
            DownloadSettingsPatchDtoMapper.ApplyTo(settings, request.Options?.DownloadSettings);
            if (!string.IsNullOrWhiteSpace(request.Options?.OutputParentDir))
                settings.Output.ParentDir = request.Options.OutputParentDir;
            SettingsNormalizer.Normalize(settings);

            engine.Enqueue(songJob, settings);
            summaries.Add(stateStore.GetJobSummary(songJob.Id) ?? BuildSubmittedJobSummary(songJob));
        }

        return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(summaries);
    }

    public Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var albumQuery = request.AlbumQuery != null
            ? JobRequestMapper.ToAlbumQuery(request.AlbumQuery)
            : searchJob.DefaultFolderProjection?.Query;
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from generic search jobs require an album query.");

        var folder = FindAlbumFolder(searchJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this search job.");

        var albumJob = new AlbumJob(new AlbumQuery(albumQuery))
        {
            ResolvedTarget = folder,
            ItemName = searchJob.ItemName,
            WorkflowId = searchJob.WorkflowId,
        };

        var settings = SettingsCloner.Clone(defaultSubmitSettings ?? searchJob.Config);
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, request.Options?.DownloadSettings);
        if (!string.IsNullOrWhiteSpace(request.Options?.OutputParentDir))
            settings.Output.ParentDir = request.Options.OutputParentDir;
        SettingsNormalizer.Normalize(settings);

        engine.Enqueue(albumJob, settings);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(albumJob.Id) ?? BuildSubmittedJobSummary(albumJob));
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

    private AlbumFolder? FindAlbumFolder(SearchJob searchJob, AlbumFolderRefDto folderRef, AlbumQueryDto? albumQuery = null)
    {
        if (searchJob.Config == null)
            return null;

        var projection = albumQuery != null
            ? new FolderSearchProjection(JobRequestMapper.ToAlbumQuery(albumQuery))
            : searchJob.DefaultFolderProjection;
        if (projection == null)
            return null;

        return searchJob.GetAlbumFolders(projection, searchJob.Config.Search).Items.FirstOrDefault(folder =>
                string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));
    }

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

    private static JobSummaryDto BuildSubmittedJobSummary(Job job)
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
            null,
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
