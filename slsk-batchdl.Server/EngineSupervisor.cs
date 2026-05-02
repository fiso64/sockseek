using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Soulseek;

namespace Sldl.Server;

public sealed class EngineSupervisor
{
    private readonly ServerOptions options;
    private readonly EngineSettings engineSettings;
    private readonly DownloadSettings defaultDownloadSettings;
    private readonly ProfileCatalog profileCatalog;
    private readonly ServerJobSettingsResolver jobSettingsResolver;
    private readonly Channel<QueuedSubmission> submissionChannel = Channel.CreateUnbounded<QueuedSubmission>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock engineGate = new();

    private DownloadEngine? currentEngine;
    private int restartCount;

    public event Action<DownloadEngine>? EngineCreated;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public EngineStateStore StateStore { get; }

    public EngineSupervisor(IOptions<ServerOptions> options)
    {
        this.options = options.Value;

        engineSettings = SettingsCloner.Clone(this.options.Engine);
        defaultDownloadSettings = SettingsCloner.Clone(this.options.DefaultDownload);
        ServerJobSettingsResolver.NormalizeForServer(defaultDownloadSettings);
        profileCatalog = this.options.Profiles ?? ProfileCatalog.Empty;
        jobSettingsResolver = new ServerJobSettingsResolver(defaultDownloadSettings, profileCatalog, this.options.LaunchDownloadSettings);

        StateStore = new EngineStateStore();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var engine = CreateEngine();
            var runTask = engine.RunAsync(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var waitToReadTask = submissionChannel.Reader.WaitToReadAsync(ct).AsTask();
                    var completedTask = await Task.WhenAny(runTask, waitToReadTask);

                    if (completedTask == runTask)
                    {
                        await runTask;
                        return;
                    }

                    if (!await waitToReadTask)
                        continue;

                    while (submissionChannel.Reader.TryRead(out var submission))
                        engine.Enqueue(submission.Job, submission.Settings);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref restartCount);
                Logger.Error($"Engine instance failed, restarting supervisor loop: {ex.Message}");
                StateStore.MarkActiveJobsInfrastructureFailed(ex.Message);
                StateStore.DetachEngine(engine);
                lock (engineGate)
                {
                    if (ReferenceEquals(currentEngine, engine))
                        currentEngine = null;
                }
                continue;
            }
        }
    }

    public ServerInfoDto GetInfo()
    {
        string version = typeof(EngineSupervisor).Assembly.GetName().Version?.ToString() ?? "dev";
        return new ServerInfoDto(options.Name, version, StartedAtUtc);
    }

    public ServerStatusDto GetStatus()
    {
        SoulseekClientStates clientState;
        lock (engineGate)
            clientState = currentEngine?.ClientState ?? SoulseekClientStates.None;

        var stats = StateStore.GetStatistics();
        return new ServerStatusDto(
            ToSoulseekClientStatusDto(clientState),
            stats.TotalJobCount,
            stats.ActiveJobCount,
            stats.TotalWorkflowCount,
            stats.ActiveWorkflowCount,
            restartCount);
    }

    public IReadOnlyList<ProfileSummaryDto> GetProfiles()
        => profileCatalog.NamedProfiles
            .Select(profile => new ProfileSummaryDto(
                profile.Name,
                profile.Condition,
                profile.Condition != null,
                profile.HasEngineSettings,
                profile.HasDownloadSettings))
            .OrderBy(profile => profile.Name)
            .ToList();

    private static SoulseekClientStatusDto ToSoulseekClientStatusDto(SoulseekClientStates state)
    {
        var flags = Enum.GetValues<SoulseekClientStates>()
            .Where(flag => flag != SoulseekClientStates.None && state.HasFlag(flag))
            .Select(flag => flag.ToString())
            .ToList();

        bool isConnected = state.HasFlag(SoulseekClientStates.Connected);
        bool isLoggedIn = state.HasFlag(SoulseekClientStates.LoggedIn);

        return new SoulseekClientStatusDto(
            state.ToString(),
            flags,
            isConnected && isLoggedIn);
    }

    public Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateExtractJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateTrackSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSongJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateJobList(request), request.Options, ct);

    private async Task<JobSummaryDto> SubmitJobAsync(Job job, SubmissionOptionsDto? options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        jobSettingsResolver.SetWorkflowOptions(job.WorkflowId, options);

        var settings = jobSettingsResolver.Resolve(defaultDownloadSettings, job);

        if (settings.NeedLogin && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");

        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(job, settings), ct);

        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    public bool CancelJob(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        var job = engine?.GetJob(jobId);
        if (job == null)
            return false;

        job.Cancel();
        return true;
    }

    public bool CancelJobByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        var job = engine?.GetJob(displayId);
        if (job == null || job.WorkflowId != workflowId)
            return false;

        job.Cancel();
        return true;
    }

    public int CancelWorkflow(Guid workflowId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelWorkflow(workflowId) ?? 0;
    }

    public IReadOnlyList<SearchRawResultDto>? GetSearchRawResults(Guid jobId, long afterSequence)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob == null)
            return null;

        return searchJob.RawSnapshot(afterSequence)
            .Select(ToSearchRawResultDto)
            .ToList();
    }

    public SearchResultSnapshotDto<FileCandidateDto>? GetFileResults(Guid jobId)
        => GetFileResults(jobId, null);

    public SearchResultSnapshotDto<FileCandidateDto>? GetFileResults(Guid jobId, FileSearchProjectionRequestDto? projection)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var fileProjection = projection?.SongQuery != null
                ? new FileSearchProjection(
                    JobRequestMapper.ToSongQuery(projection.SongQuery),
                    projection.IncludeFullResults)
                : searchJob.DefaultFileProjection
                    ?? new FileSearchProjection(new SongQuery { Title = searchJob.QueryText });
            var snapshot = searchJob.GetSortedTrackCandidates(fileProjection, searchJob.Config.Search, GetCurrentEngineUserSuccessCounts());
            return new SearchResultSnapshotDto<FileCandidateDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(ToFileCandidateDto).ToList());
        }

        var songJob = StateStore.GetJob<SongJob>(jobId);
        if (songJob == null)
            return null;

        return new SearchResultSnapshotDto<FileCandidateDto>(
            Revision: 0,
            IsComplete: songJob.State is not (JobState.Pending or JobState.Searching),
            Items: songJob.Candidates?.Select(ToFileCandidateDto).ToList() ?? []);
    }

    public SearchResultSnapshotDto<AlbumFolderDto>? GetFolderResults(Guid jobId, bool includeFiles)
        => GetFolderResults(jobId, null, includeFiles);

    public SearchResultSnapshotDto<AlbumFolderDto>? GetFolderResults(Guid jobId, FolderSearchProjectionRequestDto request)
        => GetFolderResults(jobId, request.AlbumQuery, request.IncludeFiles);

    private SearchResultSnapshotDto<AlbumFolderDto>? GetFolderResults(Guid jobId, AlbumQueryDto? albumQuery, bool includeFiles)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
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
            return new SearchResultSnapshotDto<AlbumFolderDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToList());
        }

        var albumJob = StateStore.GetJob<AlbumJob>(jobId);
        if (albumJob == null)
            return null;

        return new SearchResultSnapshotDto<AlbumFolderDto>(
            Revision: 0,
            IsComplete: albumJob.State is not (JobState.Pending or JobState.Searching),
            Items: albumJob.Results.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToList());
    }

    public SearchResultSnapshotDto<AggregateTrackCandidateDto>? GetAggregateTrackResults(Guid jobId)
        => GetAggregateTrackResults(jobId, null);

    public SearchResultSnapshotDto<AggregateTrackCandidateDto>? GetAggregateTrackResults(Guid jobId, AggregateTrackProjectionRequestDto? projection)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return null;

        var aggregateProjection = projection?.SongQuery != null
            ? new AggregateTrackProjection(JobRequestMapper.ToSongQuery(projection.SongQuery))
            : searchJob.DefaultAggregateTrackProjection
                ?? (searchJob.DefaultFileProjection is { } fileProjection
                    ? new AggregateTrackProjection(fileProjection.Query)
                    : new AggregateTrackProjection(new SongQuery { Title = searchJob.QueryText }));
        var snapshot = searchJob.GetAggregateTracks(aggregateProjection, searchJob.Config.Search, GetCurrentEngineUserSuccessCounts());
        return new SearchResultSnapshotDto<AggregateTrackCandidateDto>(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(song => new AggregateTrackCandidateDto(ToSongQuery(song.Query), song.ItemName)).ToList());
    }

    public SearchResultSnapshotDto<AggregateAlbumCandidateDto>? GetAggregateAlbumResults(Guid jobId)
        => GetAggregateAlbumResults(jobId, null);

    public SearchResultSnapshotDto<AggregateAlbumCandidateDto>? GetAggregateAlbumResults(Guid jobId, AggregateAlbumProjectionRequestDto? projection)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return null;

        var aggregateProjection = projection?.AlbumQuery != null
            ? new AggregateAlbumProjection(JobRequestMapper.ToAlbumQuery(projection.AlbumQuery))
            : searchJob.DefaultAggregateAlbumProjection
                ?? (searchJob.DefaultFolderProjection is { } folderProjection
                    ? new AggregateAlbumProjection(folderProjection.Query)
                    : null);
        if (aggregateProjection == null)
            throw new ArgumentException("Aggregate album projection requires an album query.");

        var snapshot = searchJob.GetAggregateAlbums(aggregateProjection, searchJob.Config.Search);
        return new SearchResultSnapshotDto<AggregateAlbumCandidateDto>(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(album => new AggregateAlbumCandidateDto(ToAlbumQuery(album.Query), album.ItemName)).ToList());
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct)
    {
        var searchJob = StateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return null;

        var folder = FindAlbumFolder(searchJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this search job.");

        var retrieveJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath };
        return await SubmitFollowUpJobAsync(searchJobId, searchJob, retrieveJob, searchJob.Config, null, isolateOptions: false, ct);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct)
    {
        var searchJob = StateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return null;

        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");

        var summaries = new List<JobSummaryDto>();
        foreach (var file in request.Files)
        {
            var candidate = FindFileCandidate(searchJob, file);
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this search job.");

            var songQuery = searchJob.DefaultFileProjection?.Query
                ?? Searcher.InferSongQuery(candidate.Filename, new SongQuery { Title = searchJob.QueryText });
            var songJob = new SongJob(new SongQuery(songQuery))
            {
                ResolvedTarget = candidate,
                ItemName = searchJob.ItemName,
            };

            var settings = jobSettingsResolver.ResolveFollowUp(songJob, request.Options);
            summaries.Add(await SubmitFollowUpJobAsync(searchJobId, searchJob, songJob, settings, request.Options, isolateOptions: true, ct));
        }

        return summaries;
    }

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct)
    {
        var searchJob = StateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return null;

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
        };

        var settings = jobSettingsResolver.ResolveFollowUp(albumJob, request.Options);

        return await SubmitFollowUpJobAsync(searchJobId, searchJob, albumJob, settings, request.Options, isolateOptions: true, ct);
    }

    private DownloadEngine CreateEngine()
    {
        var clientManager = new SoulseekClientManager(engineSettings);
        var engine = new DownloadEngine(engineSettings, clientManager, jobSettingsResolver);
        StateStore.AttachEngine(engine);
        lock (engineGate)
            currentEngine = engine;
        EngineCreated?.Invoke(engine);
        return engine;
    }

    private ConcurrentDictionary<string, int> GetCurrentEngineUserSuccessCounts()
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.UserSuccessCounts ?? new ConcurrentDictionary<string, int>();
    }

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

    private static SearchRawResultDto ToSearchRawResultDto(SearchRawResult result)
        => new(
            result.Sequence,
            result.Revision,
            result.Username,
            result.Filename,
            result.File.Size,
            result.File.BitRate,
            result.File.SampleRate,
            result.File.Length);

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

    private static SongQueryDto ToSongQuery(SongQuery query)
        => new(Optional(query.Artist), Optional(query.Title), Optional(query.Album), Optional(query.URI), Optional(query.Length), query.ArtistMaybeWrong);

    private static AlbumQueryDto ToAlbumQuery(AlbumQuery query)
        => new(Optional(query.Artist), Optional(query.Album), Optional(query.SearchHint), Optional(query.URI), query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    private bool CanAcceptLoginRequiredJobs()
        => !string.IsNullOrWhiteSpace(engineSettings.MockFilesDir)
        || engineSettings.UseRandomLogin
        || (!string.IsNullOrWhiteSpace(engineSettings.Username)
            && !string.IsNullOrWhiteSpace(engineSettings.Password));

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

    private FileCandidate? FindFileCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
    {
        if (searchJob.Config == null)
            return null;

        var trackCandidate = searchJob.GetSortedTrackCandidates(searchJob.Config.Search, GetCurrentEngineUserSuccessCounts())
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

    private async Task<JobSummaryDto> SubmitFollowUpJobAsync(
        Guid sourceJobId,
        SearchJob sourceJob,
        Job followUpJob,
        DownloadSettings settings,
        SubmissionOptionsDto? options,
        bool isolateOptions,
        CancellationToken ct)
    {
        followUpJob.WorkflowId = sourceJob.WorkflowId;
        StateStore.SetSourceJob(followUpJob.Id, sourceJobId);
        if (isolateOptions)
            jobSettingsResolver.SetJobOptions(followUpJob.Id, options);
        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(followUpJob, settings), ct);
        return StateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId);
    }

    private sealed record QueuedSubmission(Job Job, DownloadSettings Settings);
}
