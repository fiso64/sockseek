using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Sockseek.Api;

namespace Sockseek.Server;

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
        var pathContext = new PathVariableContext(ConfigDir: this.options.ConfigDir);
        ServerJobSettingsResolver.NormalizeForServer(defaultDownloadSettings, pathContext);
        profileCatalog = this.options.Profiles ?? ProfileCatalog.Empty;
        jobSettingsResolver = new ServerJobSettingsResolver(defaultDownloadSettings, profileCatalog, this.options.LaunchDownloadSettings, pathContext);

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
                    {
                        if (submission.IsResume)
                            engine.Resume(submission.Job);
                        else
                            engine.Enqueue(submission.Job, submission.Settings!);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref restartCount);
                SockseekLog.Daemon.Error(ex, "Engine instance failed, restarting supervisor loop");
                StateStore.MarkActiveJobsInfrastructureFailed(
                    SockseekLog.ExceptionSummary(ex),
                    SockseekLog.ExceptionDetail(ex));
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
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
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

        job.Cancel(JobCancellationSource.UserRequestedJob);
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

        job.Cancel(JobCancellationSource.UserRequestedJob);
        return true;
    }

    public int CancelWorkflow(Guid workflowId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelWorkflow(workflowId) ?? 0;
    }

    public bool TryNextCandidate(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.TryNextCandidate(jobId) ?? false;
    }

    public bool TryNextCandidateByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        var job = engine?.GetJob(displayId);
        if (job == null || job.WorkflowId != workflowId)
            return false;

        return engine?.TryNextCandidate(job.Id) ?? false;
    }

    public JobDetailDto? GetJobDetailByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        var job = engine?.GetJob(displayId);
        if (job == null || job.WorkflowId != workflowId)
            return null;

        return StateStore.GetJobDetail(job.Id);
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
            IsComplete: songJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
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

        var folders = JobRequestMapper.ProjectAlbumJobFolders(albumJob, GetCurrentEngineUserSuccessCounts());
        return new SearchResultSnapshotDto<AlbumFolderDto>(
            Revision: 0,
            IsComplete: albumJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: folders.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToList());
    }

    public SearchResultSnapshotDto<AggregateTrackCandidateDto>? GetAggregateTrackResults(Guid jobId)
        => GetAggregateTrackResults(jobId, null);

    public SearchResultSnapshotDto<AggregateTrackCandidateDto>? GetAggregateTrackResults(Guid jobId, AggregateTrackProjectionRequestDto? projection)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var aggregateProjection = projection?.SongQuery != null
                ? new AggregateTrackProjection(JobRequestMapper.ToSongQuery(projection.SongQuery))
                : searchJob.DefaultAggregateTrackProjection
                    ?? (searchJob.DefaultFileProjection is { } fileProjection
                        ? new AggregateTrackProjection(fileProjection.Query)
                        : new AggregateTrackProjection(new SongQuery { Title = searchJob.QueryText }));
            bool includeCandidates = projection?.IncludeCandidates ?? false;
            var snapshot = searchJob.GetAggregateTracks(aggregateProjection, searchJob.Config.Search, GetCurrentEngineUserSuccessCounts());
            return new SearchResultSnapshotDto<AggregateTrackCandidateDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(song => new AggregateTrackCandidateDto(
                    ToSongQuery(song.Query),
                    song.ItemName,
                    includeCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList());
        }

        var aggregateJob = StateStore.GetJob<AggregateJob>(jobId);
        if (aggregateJob == null)
            return null;

        bool includeAggregateCandidates = projection?.IncludeCandidates ?? false;
        return new SearchResultSnapshotDto<AggregateTrackCandidateDto>(
            Revision: 0,
            IsComplete: aggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: aggregateJob.Songs.Select(song => new AggregateTrackCandidateDto(
                ToSongQuery(song.Query),
                song.ItemName,
                includeAggregateCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList());
    }

    public SearchResultSnapshotDto<AggregateAlbumCandidateDto>? GetAggregateAlbumResults(Guid jobId)
        => GetAggregateAlbumResults(jobId, null);

    public SearchResultSnapshotDto<AggregateAlbumCandidateDto>? GetAggregateAlbumResults(Guid jobId, AggregateAlbumProjectionRequestDto? projection)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var aggregateProjection = projection?.AlbumQuery != null
                ? new AggregateAlbumProjection(JobRequestMapper.ToAlbumQuery(projection.AlbumQuery))
                : searchJob.DefaultAggregateAlbumProjection
                    ?? (searchJob.DefaultFolderProjection is { } folderProjection
                        ? new AggregateAlbumProjection(folderProjection.Query)
                        : null);
            if (aggregateProjection == null)
                throw new ArgumentException("Aggregate album projection requires an album query.");

            bool includeFolders = projection?.IncludeFolders ?? false;
            var snapshot = searchJob.GetAggregateAlbums(aggregateProjection, searchJob.Config.Search);
            return new SearchResultSnapshotDto<AggregateAlbumCandidateDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(album => new AggregateAlbumCandidateDto(
                    ToAlbumQuery(album.Query),
                    album.ItemName,
                    includeFolders ? album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true)).ToList() : null)).ToList());
        }

        var albumAggregateJob = StateStore.GetJob<AlbumAggregateJob>(jobId);
        if (albumAggregateJob == null)
            return null;

        bool includeAggregateFolders = projection?.IncludeFolders ?? false;
        return new SearchResultSnapshotDto<AggregateAlbumCandidateDto>(
            Revision: 0,
            IsComplete: albumAggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: albumAggregateJob.Albums.Select(album => new AggregateAlbumCandidateDto(
                ToAlbumQuery(album.Query),
                album.ItemName,
                includeAggregateFolders ? album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true)).ToList() : null)).ToList());
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct)
    {
        var sourceJob = StateStore.GetJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return null;

        var folder = FindAlbumFolderForRetrieval(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath };
        retrieveJob.WorkflowId = sourceJob.WorkflowId;
        StateStore.SetSourceJob(retrieveJob.Id, sourceJobId);
        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(retrieveJob, sourceJob.Config), ct);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct)
    {
        var sourceJob = StateStore.GetJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return null;

        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");

        var summaries = new List<JobSummaryDto>();

        if (sourceJob is SongJob manualSong && manualSong.IsAwaitingSelection)
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
            manualSong.ResetToPending();

            await submissionChannel.Writer.WriteAsync(QueuedSubmission.Resume(manualSong), ct);
            return new List<JobSummaryDto> { StateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong, sourceJobId) };
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
            };

            var followUpSettings = jobSettingsResolver.ResolveFollowUp(followUpSongJob, request.Options);
            summaries.Add(await SubmitFollowUpJobAsync(sourceJobId, sourceJob, followUpSongJob, followUpSettings, request.Options, isolateOptions: true, ct));
        }

        return summaries;
    }

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid sourceJobId, StartFolderDownloadRequestDto request, CancellationToken ct)
    {
        var sourceJob = StateStore.GetJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return null;

        var folder = FindAlbumFolder(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        folder = JobRequestMapper.ApplySelectedFolderSnapshot(folder, request);
        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        var albumQuery = request.AlbumQuery != null
            ? JobRequestMapper.ToAlbumQuery(request.AlbumQuery)
            : sourceJob switch
            {
                SearchJob searchJob => searchJob.DefaultFolderProjection?.Query,
                AlbumJob album => album.Query,
                AlbumAggregateJob aggregate => aggregate.Query,
                _ => null,
            };

        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        if (engine?.TryStartManualAlbumSelection(
            sourceJobId,
            folder,
            albumQuery,
            album => JobRequestMapper.ApplyFolderDownloadSelection(album, request.Selection),
            out var selectedAlbum) == true)
        {
            if (sourceJob is AlbumAggregateJob)
                StateStore.SetSourceJob(selectedAlbum!.Id, sourceJobId);

            return StateStore.GetJobSummary(selectedAlbum!.Id) ?? BuildSubmittedJobSummary(selectedAlbum!, sourceJobId);
        }
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from this job require an album query.");

        string? itemName = sourceJob.ItemName;
        if (sourceJob is SearchJob { DefaultAggregateAlbumProjection: not null } && !string.IsNullOrWhiteSpace(folder.FolderPath))
            itemName = Utils.GetBaseNameSlsk(folder.FolderPath);

        var albumJob = new AlbumJob(new AlbumQuery(albumQuery))
        {
            ResolvedTarget = folder,
            ItemName = itemName,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
        };
        JobRequestMapper.ApplyFolderDownloadSelection(albumJob, request.Selection);

        var followUpSettings = jobSettingsResolver.ResolveFollowUp(albumJob, request.Options);

        return await SubmitFollowUpJobAsync(sourceJobId, sourceJob, albumJob, followUpSettings, request.Options, isolateOptions: true, ct);
    }

    public async Task<bool> CompleteManualSelectionAsync(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine != null && await engine.CompleteManualSelectionAsync(jobId);
    }

    public async Task<bool> SkipManualSelectionAsync(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine != null && await engine.SkipManualSelectionAsync(jobId);
    }

    private DownloadEngine CreateEngine()
    {
        var clientManager = new SoulseekClientManager(engineSettings, options.ClientFactory?.Invoke(engineSettings));
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
            EngineStateStore.ToServerJobLifecycleState(job.LifecycleState),
            EngineStateStore.ToServerJobActivityPhase(job.ActivityPhase),
            job.ActivityUntilUtc,
            EngineStateStore.ToServerJobTerminalOutcome(job.TerminalOutcome),
            EngineStateStore.ToServerJobSkipReason(job.SkipReason),
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
            [],
            CancellationSource: EngineStateStore.ToServerJobCancellationSource(job.CancellationSource));

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

    private AlbumFolder? FindAlbumFolderForRetrieval(Job sourceJob, AlbumFolderRefDto folderRef, AlbumQueryDto? albumQuery = null)
    {
        static bool Matches(AlbumFolder folder, AlbumFolderRefDto folderRef)
            => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal);

        if (sourceJob is AlbumJob albumJob)
            return albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? FindAlbumFolder(sourceJob, folderRef, albumQuery);

        return FindAlbumFolder(sourceJob, folderRef, albumQuery);
    }

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

            var folders = searchJob.GetAlbumFolders(projection, searchJob.Config.Search).Items;
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? JobRequestMapper.BuildRelatedFolder(folderRef, folders);
        }

        if (sourceJob is AlbumJob albumJob)
            return JobRequestMapper.FindProjectedAlbumFolder(albumJob, folderRef, GetCurrentEngineUserSuccessCounts())
                ?? albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? JobRequestMapper.BuildRelatedFolder(folderRef, albumJob.Results);

        if (sourceJob is AlbumAggregateJob aggregateJob)
        {
            var folders = aggregateJob.Albums
                .Where(album => albumQuery == null || AlbumQueriesEqual(album.Query, JobRequestMapper.ToAlbumQuery(albumQuery)))
                .SelectMany(album => album.Results)
                .ToList();
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? JobRequestMapper.BuildRelatedFolder(folderRef, folders);
        }

        return null;
    }

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase);

    private FileCandidate? FindFileCandidate(Job sourceJob, FileCandidateRefDto candidateRef)
    {
        static bool Matches(FileCandidate candidate, FileCandidateRefDto candidateRef)
            => string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal);

        if (sourceJob is SearchJob searchJob)
            return FindSearchFileCandidate(searchJob, candidateRef);

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

    private FileCandidate? FindSearchFileCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
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
        Job sourceJob,
        Job followUpJob,
        DownloadSettings settings,
        SubmissionOptionsDto? options,
        bool isolateOptions,
        CancellationToken ct)
    {
        followUpJob.WorkflowId = sourceJob.WorkflowId;
        if (ShouldPropagateSourceMutationToFollowUp(sourceJob))
            followUpJob.CopySourceMutationFrom(sourceJob);
        StateStore.SetSourceJob(followUpJob.Id, sourceJobId);
        if (isolateOptions)
            jobSettingsResolver.SetJobOptions(followUpJob.Id, options);
        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(followUpJob, settings), ct);
        return StateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId);
    }

    private static bool ShouldPropagateSourceMutationToFollowUp(Job sourceJob)
        => sourceJob is not AlbumAggregateJob;

    private sealed record QueuedSubmission(Job Job, DownloadSettings? Settings, bool IsResume = false)
    {
        public static QueuedSubmission Resume(Job job) => new(job, null, true);
    }
}
