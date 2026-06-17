using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Soulseek;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Sockseek.Core;


// TODO [ARCHITECTURE]: Refactor DownloadEngine to alleviate "God Class" anti-pattern.
// Currently, this class violates the Single Responsibility Principle by managing the queue,
// instantiating concrete dependencies (new Searcher(), new Downloader()), managing cancellation
// hierarchies, and polling for stale downloads.
// We should adopt Microsoft.Extensions.DependencyInjection:
// 1. Break this class into isolated services (e.g., IJobPipeline, IStaleMonitor, IQueueOrchestrator).
// 2. Inject dependencies (ISearcher, IDownloader) via constructor injection.
// This will drastically improve maintainability and make the orchestration logic actually unit-testable.
public class DownloadEngine
{
    private const int updateInterval = 100;

    private Searcher? searcher = null;
    private Downloader? downloader = null;

    private readonly EngineSettings engineSettings;
    private readonly SoulseekClientManager _clientManager;
    private readonly IJobSettingsResolver _jobSettingsResolver;

    public EngineEvents Events { get; } = new();

    public JobList Queue { get; } = new();

    private readonly ConcurrentDictionary<Guid, JobContext> _contexts = new();
    private readonly ConcurrentDictionary<string, byte> _appliedSourceMutations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Guid> _manualAggregateParentByAlbumId = new();
    private readonly ConcurrentDictionary<Guid, byte> _closedManualAggregateSelections = new();
    private readonly SourceMutationExecutor _sourceMutationExecutor = new();

    private readonly ConcurrentDictionary<Guid, Job> _jobById = new();
    private readonly ConcurrentDictionary<int, Job> _jobByDisplayId = new();

    public Job? GetJob(Guid id) => _jobById.TryGetValue(id, out var job) ? job : null;
    public Job? GetJob(int displayId) => _jobByDisplayId.TryGetValue(displayId, out var job) ? job : null;
    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => _jobById.Values
        .Where(job => job.WorkflowId == workflowId)
        .OrderBy(job => job.DisplayId)
        .ToList();

    public bool TryNextCandidate(Guid jobId)
    {
        var job = GetJob(jobId);
        if (job == null) return false;

        var activeDownloads = _registry.Downloads.Values.Where(d => d.Song == job).ToList();

        if (job is AlbumJob albumJob && albumJob.ResolvedTarget != null)
        {
            var songIds = albumJob.ResolvedTarget.Files.Select(f => f.Id).ToHashSet();
            activeDownloads.AddRange(_registry.Downloads.Values.Where(d => songIds.Contains(d.Song.Id)));
        }
        else if (job is AggregateJob aggregateJob)
        {
            var songIds = aggregateJob.Songs.Select(f => f.Id).ToHashSet();
            activeDownloads.AddRange(_registry.Downloads.Values.Where(d => songIds.Contains(d.Song.Id)));
        }

        if (activeDownloads.Count > 0)
        {
            SockseekLog.Jobs.Info($"[{job.DisplayId}] {JobLogKind(job)}: trying next candidate; cancelling {activeDownloads.Count} active download{(activeDownloads.Count == 1 ? "" : "s")}: {job}");
            foreach (var ad in activeDownloads)
            {
                ad.IsManuallySkipped = true;
                ad.Cts.Cancel();
            }
            return true;
        }
        return false;
    }

    private JobContext Ctx(Job job) => _contexts[job.Id];

    private void RegisterJob(Job job, Job? parent)
    {
        bool firstRegistration = _jobById.TryAdd(job.Id, job);
        _jobByDisplayId[job.DisplayId] = job;

        if (!firstRegistration)
            return;

        job.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Job.State))
                Events.RaiseJobStateChanged(job, job.State);
        };
        Events.RaiseJobRegistered(job, parent);
    }

    // ── public state (read by Searcher / Downloader) ─────────────────────────

    public ISoulseekClient? Client => _clientManager.Client;
    public SoulseekClientStates ClientState => _clientManager.State;
    public bool IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;

    // Session state (Decoupled)
    private readonly SessionRegistry _registry = new();
    public ConcurrentDictionary<string, int> UserSuccessCounts => _registry.UserSuccessCounts;

    // ── concurrency semaphores ────────────────────────────────────────────────

    // Limits simultaneous extractor runs to avoid API rate limits.
    // Search concurrency is handled inside Searcher (concurrencySemaphore).
    private readonly SemaphoreSlim _extractorSemaphore;
    private readonly SemaphoreSlim _jobSemaphore;

    // ── job channel ──────────────────────────────────────────────────────────

    private readonly Channel<QueuedEngineJob> _jobChannel =
        Channel.CreateUnbounded<QueuedEngineJob>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Enqueues a new root job for processing. Call <see cref="CompleteEnqueue"/> when done adding jobs.</summary>
    public void Enqueue(Job job, DownloadSettings settings) =>
        _jobChannel.Writer.TryWrite(QueuedEngineJob.Root(job, settings));

    /// <summary>Resumes an existing job without re-parenting it or replacing its prepared context.</summary>
    public void Resume(Job job) =>
        _jobChannel.Writer.TryWrite(QueuedEngineJob.Resume(job));

    /// <summary>
    /// Applies a manual album-folder selection to an existing selection job and resumes it
    /// without creating a follow-up job or rebuilding its prepared context.
    /// </summary>
    public bool TryStartManualAlbumSelection(
        Guid sourceJobId,
        AlbumFolder selectedFolder,
        AlbumQuery? albumQuery,
        Action<AlbumJob>? configureSelection,
        out AlbumJob? selectedJob)
    {
        selectedJob = null;
        var sourceJob = GetJob(sourceJobId);

        if (sourceJob is AlbumJob albumJob && CanStartManualAlbumSelection(albumJob))
        {
            StartExistingAlbumSelection(albumJob, selectedFolder, configureSelection);
            selectedJob = albumJob;
            return true;
        }

        if (sourceJob is AlbumAggregateJob aggregateJob && aggregateJob.State == JobState.AwaitingSelection)
        {
            var childAlbum = FindAggregateAlbumForSelection(aggregateJob, selectedFolder, albumQuery);
            if (childAlbum == null)
                return false;

            EnsureManualAggregateAlbumChildPrepared(aggregateJob, childAlbum);
            StartExistingAlbumSelection(childAlbum, selectedFolder, configureSelection);
            selectedJob = childAlbum;
            return true;
        }

        return false;
    }

    /// <summary>Completes an AwaitingSelection job through the engine so terminal side effects stay centralized.</summary>
    public async Task<bool> CompleteManualSelectionAsync(Guid jobId)
    {
        var job = GetJob(jobId);
        if (job == null || job.State != JobState.AwaitingSelection || job.Config == null)
            return false;

        if (job is AlbumAggregateJob aggregateJob)
        {
            _closedManualAggregateSelections.TryAdd(aggregateJob.Id, 0);
            await TryFinalizeClosedManualAggregateSelectionAsync(aggregateJob);
            return true;
        }

        job.Fail(FailureReason.NoSuitableFileFound);
        await FlushManualSelectionTerminalEffectsAsync(job);
        return true;
    }

    /// <summary>Signals that no more jobs will be enqueued. <see cref="RunAsync"/> will drain and exit.</summary>
    public void CompleteEnqueue() => _jobChannel.Writer.Complete();

    // ── cancellation ─────────────────────────────────────────────────────────

    private readonly CancellationTokenSource appCts = new();
    public void Cancel() => appCts.Cancel();
    public int CancelWorkflow(Guid workflowId)
    {
        var jobs = GetJobsByWorkflow(workflowId);
        int cancelled = 0;

        foreach (var job in jobs)
        {
            var cts = job.Cts;
            if (cts == null || cts.IsCancellationRequested)
                continue;

            job.Cancel();
            cancelled++;
        }

        return cancelled;
    }

    // ── construction ─────────────────────────────────────────────────────────

    public DownloadEngine(EngineSettings settings, SoulseekClientManager clientManager, IJobSettingsResolver? jobSettingsResolver = null)
    {
        engineSettings = settings;
        _clientManager = clientManager;
        _jobSettingsResolver = jobSettingsResolver ?? DefaultJobSettingsResolver.Instance;
        if (settings.ConcurrentJobs <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings.ConcurrentJobs), "ConcurrentJobs must be greater than zero.");
        _jobSemaphore = new SemaphoreSlim(settings.ConcurrentJobs);
        _extractorSemaphore = new SemaphoreSlim(settings.ConcurrentExtractors);
    }

    private async Task WithJobSlot(CancellationToken ct, Func<Task> action)
    {
        await _jobSemaphore.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            _jobSemaphore.Release();
        }
    }

    private async Task<T> WithJobSlot<T>(CancellationToken ct, Func<Task<T>> action)
    {
        await _jobSemaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _jobSemaphore.Release();
        }
    }

    private sealed record QueuedEngineJob(Job Job, DownloadSettings? Settings, bool IsResume)
    {
        public static QueuedEngineJob Root(Job job, DownloadSettings settings) => new(job, settings, false);
        public static QueuedEngineJob Resume(Job job) => new(job, null, true);
    }

    private void StartExistingAlbumSelection(AlbumJob albumJob, AlbumFolder selectedFolder, Action<AlbumJob>? configureSelection)
    {
        albumJob.ClearFailure();
        albumJob.ResolvedTarget = selectedFolder;
        configureSelection?.Invoke(albumJob);
        if (!albumJob.Results.Any(folder => SameAlbumFolder(folder, selectedFolder)))
            albumJob.Results.Insert(0, selectedFolder);
        albumJob.UpdateState(JobState.Pending);
        Resume(albumJob);
    }

    private static bool CanStartManualAlbumSelection(AlbumJob albumJob)
        => albumJob.DownloadBehavior == DownloadBehavior.Manual
            && albumJob.State is JobState.AwaitingSelection or JobState.Failed;

    private AlbumJob? FindAggregateAlbumForSelection(AlbumAggregateJob aggregateJob, AlbumFolder selectedFolder, AlbumQuery? albumQuery)
        => aggregateJob.Albums.FirstOrDefault(album =>
            (albumQuery == null || AlbumQueriesEqual(album.Query, albumQuery))
            && album.Results.Any(folder => SameAlbumFolder(folder, selectedFolder)));

    private void EnsureManualAggregateAlbumChildPrepared(AlbumAggregateJob aggregateJob, AlbumJob albumJob)
    {
        albumJob.WorkflowId = aggregateJob.WorkflowId;
        albumJob.Config = aggregateJob.Config;
        albumJob.ItemName ??= albumJob.ToString(noInfo: true);
        albumJob.DownloadBehaviorPolicy = albumJob.DownloadBehaviorPolicy with { Album = DownloadBehavior.Manual };

        _manualAggregateParentByAlbumId[albumJob.Id] = aggregateJob.Id;
        RegisterJob(albumJob, aggregateJob);

        if (_contexts.ContainsKey(albumJob.Id))
            return;

        var parentCtx = Ctx(aggregateJob);
        _contexts[albumJob.Id] = new JobContext
        {
            IndexEditor = parentCtx.IndexEditor,
            PlaylistEditor = parentCtx.PlaylistEditor,
            OutputDirSkipper = parentCtx.OutputDirSkipper,
            MusicDirSkipper = parentCtx.MusicDirSkipper,
            PreprocessTracks = false,
        };
    }

    private static bool SameAlbumFolder(AlbumFolder left, AlbumFolder right)
        => string.Equals(left.Username, right.Username, StringComparison.Ordinal)
            && string.Equals(left.FolderPath, right.FolderPath, StringComparison.Ordinal);

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.URI, right.URI, StringComparison.OrdinalIgnoreCase);


    // ── top-level entry point ─────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        bool servicesInitialized = false;
        var rootTasks = new List<Task>();

        SockseekLog.Jobs.Trace("RunAsync: Starting to read from job channel.");
        await foreach (var queuedJob in _jobChannel.Reader.ReadAllAsync(ct))
        {
            var rootJob = queuedJob.Job;
            var settings = queuedJob.Settings;
            SockseekLog.Jobs.Trace($"RunAsync: Read {(queuedJob.IsResume ? "resume" : "root")} job {rootJob.DisplayId} from channel.");

            if (!queuedJob.IsResume)
            {
                Queue.Jobs.Add(rootJob);

                foreach (var (id, ctx) in JobPreparer.PrepareSubtree(rootJob, settings!, _jobSettingsResolver))
                    _contexts[id] = ctx;
            }
            else if (!_contexts.ContainsKey(rootJob.Id))
            {
                throw new InvalidOperationException($"Cannot resume job {rootJob.DisplayId}: no prepared job context exists.");
            }

            var effectiveSettings = settings ?? rootJob.Config!;
            if (effectiveSettings.NeedLogin && !servicesInitialized)
            {
                try
                {
                    await _clientManager.EnsureConnectedAndLoggedInAsync(engineSettings, ct);
                }
                catch (Exception ex)
                {
                    SockseekLog.Soulseek.Error(ex, "Initial Soulseek login failed. Reconnection will be attempted automatically in the background");
                }

                await _clientManager.WaitUntilReadyAsync(ct);
                searcher = new Searcher(Client!, _registry, _registry, Events, engineSettings.SearchesPerTime, engineSettings.SearchRenewTime, engineSettings.ConcurrentSearches);
                downloader = new Downloader(Client!, _clientManager, _registry, Events);
                _ = Task.Run(() => UpdateLoop(appCts.Token), appCts.Token);
                SockseekLog.Jobs.Debug("Update task started");
                servicesInitialized = true;
            }

            rootTasks.Add(ProcessJob(rootJob));
        }

        SockseekLog.Jobs.Trace("RunAsync: Channel fully drained. Waiting for rootTasks to complete.");
        await Task.WhenAll(rootTasks);
        SockseekLog.Jobs.Trace("RunAsync: All rootTasks completed.");

        if (Queue.Jobs.Count > 0 && !Queue.Jobs[^1].Config!.DoNotDownload)
            Events.RaiseEngineCompleted(Queue);

        SockseekLog.Jobs.Debug("Exiting RunAsync");
        appCts.Cancel();
    }


    // ── recursive job processor ───────────────────────────────────────────────

    async Task ProcessJob(Job job, CancellationToken parentToken = default, Job? parentJob = null)
    {
        // TODO: This function is way too long.

        RegisterJob(job, parentJob);
        bool executionCompletedRaised = false;

        void RaiseJobExecutionCompleted()
        {
            if (executionCompletedRaised)
                return;

            executionCompletedRaised = true;
            Events.RaiseJobExecutionCompleted(job);
        }

        // Create a per-job CTS linked to both the engine-wide appCts and the parent job's token
        // (if any). Cancelling this job propagates to all descendants; cancelling the parent
        // propagates here automatically. ExtractJob passes parentToken (not its own token) when
        // recursing into its Result so that the Result is a sibling, not a child, in the hierarchy.
        job.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, parentToken);

        SockseekLog.Jobs.Trace($"ProcessJob: Starting job {job.DisplayId} ({job.GetType().Name})");
        try
        {
            // ── ExtractJob: run extractor, set Result, recurse ───────────────────
            if (job is ExtractJob ej)
            {
                var extractResult = await ProcessExtractJob(ej, parentJob);
                CommitOutcome(ej, extractResult.Outcome);

                // ExtractJob completion moment: extraction is terminal here.
                // Any later automatic processing of a successful result job is separate execution.
                RaiseJobExecutionCompleted();

                if (extractResult.Result == null || !ej.AutoProcessResult)
                    return;

                // Pass parentToken (not ej.Cts.Token): the Result is a sibling of the ExtractJob in
                // the CTS hierarchy. Cancelling the ExtractJob after extraction completes has no effect
                // on the already-running Result; the Result can be cancelled independently.
                SockseekLog.Jobs.Trace($"ProcessJob (ExtractJob {job.DisplayId}): Processing extracted job {extractResult.Result.DisplayId}");
                await ProcessJob(extractResult.Result, parentToken, parentJob);

                // For single extracted jobs with a source line (e.g. a lone AlbumJob from a CSV row),
                // trigger removal now that processing is complete. Multi-item results use LineNumber=0
                // (no source line of their own) and handle per-child removal inside ProcessJob.
                SockseekLog.Jobs.Trace($"ProcessJob (ExtractJob {job.DisplayId}): Calling MaybeRemoveFromSource");
                await MaybeRemoveFromSource(extractResult.Result, ej.Config);

                SockseekLog.Jobs.Trace($"ProcessJob (ExtractJob {job.DisplayId}): Extracted job processing complete.");
                return;
            }

            if (job is JobList jl)
            {
                await ProcessJobList(jl);
                return;
            }

            // ── Leaf jobs: skip checks, search, download ─────────────────────────
            await ProcessLeafJob(job);
        }
        finally
        {
            if (job.Config != null)
                await MaybeRemoveFromSource(job, job.Config);

            SockseekLog.Jobs.Trace($"ProcessJob: Finished job {job.DisplayId} ({job.GetType().Name}). Raising execution completed.");
            RaiseJobExecutionCompleted();

            if (job is AlbumJob albumJob
                && _manualAggregateParentByAlbumId.TryGetValue(albumJob.Id, out var aggregateId)
                && GetJob(aggregateId) is AlbumAggregateJob aggregateJob)
            {
                await TryFinalizeClosedManualAggregateSelectionAsync(aggregateJob);
            }
        }
    }

    static void ApplyDownloadBehaviorPolicy(Job job, DownloadBehaviorPolicy policy)
    {
        job.DownloadBehaviorPolicy = policy;

        switch (job)
        {
            case JobList list:
                foreach (var child in list.Jobs)
                    ApplyDownloadBehaviorPolicy(child, policy);
                break;
            case ExtractJob extract:
                extract.ResultDownloadBehaviorPolicy = policy;
                if (extract.Result != null)
                    ApplyDownloadBehaviorPolicy(extract.Result, policy);
                break;
            case AggregateJob aggregate:
                foreach (var song in aggregate.Songs)
                    ApplyDownloadBehaviorPolicy(song, policy);
                break;
            case AlbumAggregateJob aggregate:
                foreach (var album in aggregate.Albums)
                    ApplyDownloadBehaviorPolicy(album, policy);
                break;
        }
    }

    async Task ProcessJobList(JobList jl)
    {
        var ctx = _contexts.TryGetValue(jl.Id, out var c) ? c : null;
        var config = jl.Config!;
        jl.UpdateState(JobState.Running);

        if (ctx?.PreprocessTracks == true)
        {
            Preprocessor.PreprocessJob(jl, config.Preprocess);
            JobPreparer.ApplySearchSettings(jl, config.Search);
        }

        jl.PrintLines();

        // ── skip checks for direct SongJob children ──────────────────────
        var directSongs = jl.Jobs.OfType<SongJob>().ToList();
        var existing = new List<SongJob>();
        var notFound = new List<SongJob>();

        if (directSongs.Count > 0 && !config.PrintResults)
        {
            if (ctx != null && config.Skip.SkipNotFound)
                foreach (var song in directSongs)
                    if (TrySetNotFoundLastTime(song, ctx.IndexEditor))
                        notFound.Add(song);

            if (ctx != null && config.Skip.SkipExisting)
                foreach (var song in directSongs.Where(s => s.State == JobState.Pending))
                    if (TrySetAlreadyExists(jl, song, TrackSkipperContext.From(ctx, config.Skip, config.Search)))
                        existing.Add(song);

            Events.RaiseTrackBatchResolved(jl,
                directSongs.Where(s => s.State == JobState.Pending).ToList(),
                existing,
                notFound);

            foreach (var song in existing)
                await MaybeRemoveFromSource(song, config);
        }

        if (config.PrintTracks)
        {
            if (directSongs.Count == 0)
                await Task.WhenAll(jl.Jobs.ToList().Select(child => ProcessJob(child, jl.Cts!.Token, jl)));

            jl.PrintLines();
            return;
        }

        ctx?.IndexEditor?.Update();
        ctx?.PlaylistEditor?.Update();

        try
        {
            // ── fan-out ───────────────────────────────────────────────────────
            if (directSongs.Count > 0)
            {
                var intervalReporter = engineSettings.ReportIntervalProgress
                    ? new IntervalProgressReporter(TimeSpan.FromSeconds(30), 5, directSongs)
                    : null;

                await Task.WhenAll(jl.Jobs.ToList().Select(async child =>
                {
                    bool wasInitial = child is SongJob s && s.State == JobState.Pending;
                    await ProcessJob(child, jl.Cts!.Token, jl);

                    if (wasInitial && child is SongJob song)
                    {
                        ctx?.IndexEditor?.Update();
                        ctx?.PlaylistEditor?.Update();
                        intervalReporter?.MaybeReport(song.State);
                        int dl = directSongs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
                        int fl = directSongs.Count(s => s.State == JobState.Failed);
                        Events.RaiseOverallProgress(dl, fl, directSongs.Count);

                        await MaybeRemoveFromSource(song, config);
                    }
                }));

                int dlFinal = directSongs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
                int flFinal = directSongs.Count(s => s.State == JobState.Failed);
                Events.RaiseListProgress(jl, dlFinal, flFinal, directSongs.Count);
            }
            else
            {
                await Task.WhenAll(jl.Jobs.ToList().Select(child => ProcessJob(child, jl.Cts!.Token, jl)));

                foreach (var child in jl.Jobs)
                    await MaybeRemoveFromSource(child, config);
            }
        }
        catch (OperationCanceledException) when (jl.Cts?.IsCancellationRequested == true)
        {
        }

        SetJobListTerminalState(jl);
    }

    static bool IsSubtreeSuccessful(Job? job)
    {
        if (job == null) return false;

        return job switch
        {
            JobList jl => jl.Jobs.All(IsSubtreeSuccessful),
            ExtractJob ej => ej.State == JobState.Done && ej.Result != null && IsSubtreeSuccessful(ej.Result),
            AlbumAggregateJob aag => aag.Albums.Count > 0 && aag.Albums.All(IsSubtreeSuccessful),
            AggregateJob ag => ag.Songs.Count > 0 && ag.Songs.All(IsSubtreeSuccessful),
            _ => job.State == JobState.Done || job.State == JobState.AlreadyExists,
        };
    }

    async Task TryFinalizeClosedManualAggregateSelectionAsync(AlbumAggregateJob aggregateJob)
    {
        if (!_closedManualAggregateSelections.ContainsKey(aggregateJob.Id))
            return;

        if (aggregateJob.State is JobState.Done or JobState.AlreadyExists or JobState.Failed)
            return;

        var selectedAlbums = _manualAggregateParentByAlbumId
            .Where(pair => pair.Value == aggregateJob.Id)
            .Select(pair => GetJob(pair.Key))
            .OfType<AlbumJob>()
            .ToList();

        if (selectedAlbums.Count == 0)
        {
            aggregateJob.Fail(FailureReason.NoSuitableFileFound);
            await FlushManualSelectionTerminalEffectsAsync(aggregateJob);
            return;
        }

        if (selectedAlbums.Any(album => IsActiveManualSelectionChild(album.State)))
            return;

        if (selectedAlbums.All(album => album.State is JobState.Done or JobState.AlreadyExists))
            aggregateJob.SetDone();
        else
            aggregateJob.Fail(FailureReason.NoSuitableFileFound);

        await FlushManualSelectionTerminalEffectsAsync(aggregateJob);
    }

    async Task FlushManualSelectionTerminalEffectsAsync(Job job)
    {
        if (_contexts.TryGetValue(job.Id, out var ctx))
        {
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
        }

        await MaybeRemoveFromSource(job, job.Config);
        Events.RaiseJobExecutionCompleted(job);
    }

    static bool IsActiveManualSelectionChild(JobState state)
        => state is JobState.Pending
            or JobState.Extracting
            or JobState.Searching
            or JobState.Downloading
            or JobState.Running
            or JobState.AwaitingSelection;

    static void SetJobListTerminalState(JobList jobList)
    {
        if (jobList.Cts?.IsCancellationRequested == true
            || jobList.Jobs.Any(HasCancelledDescendant))
        {
            jobList.Fail(FailureReason.Cancelled);
            return;
        }

        jobList.SetDone();
    }

    static bool HasCancelledDescendant(Job job)
    {
        if (job.FailureReason == FailureReason.Cancelled)
            return true;

        return job switch
        {
            JobList list => list.Jobs.Any(HasCancelledDescendant),
            AlbumJob album => album.ResolvedTarget?.Files.Any(song => song.FailureReason == FailureReason.Cancelled) == true,
            AggregateJob aggregate => aggregate.Songs.Any(song => song.FailureReason == FailureReason.Cancelled),
            AlbumAggregateJob aggregate => aggregate.Albums.Any(HasCancelledDescendant),
            ExtractJob extract => extract.Result != null && HasCancelledDescendant(extract.Result),
            _ => false,
        };
    }

    async Task MaybeRemoveFromSource(Job job, DownloadSettings config)
    {
        if (!config.Extraction.RemoveTracksFromSource) return;
        if (job is SearchJob or RetrieveFolderJob) return;
        if (job.SourceMutation == null) return;
        if (!IsSubtreeSuccessful(job)) return;
        if (!_appliedSourceMutations.TryAdd(job.SourceMutation.Key, 0)) return;

        SockseekLog.Jobs.Debug($"RemoveFromSource: '{job}' ({job.SourceMutation.Kind}, source='{job.SourceMutation.Source}', line={job.SourceMutation.LineNumber})");
        try { await _sourceMutationExecutor.ApplyAsync(job.SourceMutation, config); }
        catch (Exception ex) { SockseekLog.Jobs.Error(ex, "Error removing from source"); }
    }

    async Task<JobOutcome> ProcessLeafJob(Job job)
    {
        var ctx = Ctx(job);
        var config = job.Config;

        if (ctx.PreprocessTracks)
        {
            Preprocessor.PreprocessJob(job, config.Preprocess);
            JobPreparer.ApplySearchSettings(job, config.Search);
        }

        job.PrintLines();

        // ── skip checks ──────────────────────────────────────────────────────

        if (config.Skip.SkipNotFound && !config.PrintResults && job.CanBeSkipped)
        {
            if (TryGetNotFoundLastTimeOutcome(job) is { } outcome)
            {
                CommitOutcome(job, outcome);
                SockseekLog.Jobs.Info($"Download '{job.ToString(true)}' was not found during a prior run, skipping");
                return outcome;
            }
        }

        if (config.Skip.SkipExisting && !config.PrintResults && job.CanBeSkipped
            && TryGetJobAlreadyExistsOutcome(job, ctx) is { } alreadyExistsOutcome)
        {
            CommitOutcome(job, alreadyExistsOutcome);
            if (!string.IsNullOrEmpty(alreadyExistsOutcome.DownloadPath))
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, alreadyExistsOutcome.DownloadPath);
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return alreadyExistsOutcome;
        }

        if (config.PrintTracks)
        {
            job.PrintLines();
            return JobOutcome.NoChange();
        }

        // ── source search / download ──────────────────────────────────────────
        // Leaf jobs hold a single job slot for their entire lifetime (search + download combined).
        // Containers (AggregateJob, AlbumAggregateJob) don't hold a slot here; their children do.
        if (job is SongJob or AlbumJob or SearchJob or RetrieveFolderJob)
            return await WithJobSlot(job.Cts!.Token, () => ProcessLeafJobCore(job, ctx));
        else
            return await ProcessLeafJobCore(job, ctx);
    }

    async Task<JobOutcome> ProcessLeafJobCore(Job job, JobContext ctx)
    {
        var config = job.Config;

        if (job is SearchJob searchJob)
            return await ProcessSearchJob(searchJob);

        if (job is RetrieveFolderJob retrieveFolderJob)
            return await ProcessRetrieveFolderJob(retrieveFolderJob);

        if (job is SongJob songJob)
        {
            if (await ProcessSongDiscovery(songJob) is { } outcome)
                return outcome;
        }

        if (job is AlbumJob albumJob)
        {
            if (await ProcessAlbumDiscovery(albumJob, ctx) is { } outcome)
                return outcome;
        }

        if (job is AggregateJob aggregateJob)
        {
            if (await ProcessAggregateDiscovery(aggregateJob, ctx) is { } outcome)
                return outcome;
        }

        if (job is AlbumAggregateJob albumAggregateJob)
            return await ProcessAlbumAggregateDiscovery(albumAggregateJob, ctx);

        if (config.PrintResults)
        {
            return JobOutcome.NoChange();
        }

        return await ProcessLeafDownload(job, ctx);
    }

    async Task<ExtractJobResult> ProcessExtractJob(ExtractJob job, Job? parentJob)
    {
        InputType inputType;
        IExtractor extractor;
        try
        {
            (inputType, extractor) = ExtractorRegistry.GetMatchingExtractor(
                job.Input,
                job.InputType ?? InputType.None,
                job.Config);
        }
        catch (Exception e)
        {
            return new(ExtractionFailedOutcome(e), null, null);
        }

        job.InputType = inputType;

        Job extracted;
        try
        {
            await _extractorSemaphore.WaitAsync(job.Cts!.Token);
            try
            {
                job.UpdateState(JobState.Extracting);
                job.Cts.Token.ThrowIfCancellationRequested();
                extracted = await extractor.GetTracks(job.Input, job.Config.Extraction, ExtractorContext.ForExtractJob(job, Events, ExtractorLogSource(inputType)));
                job.Cts.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                _extractorSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new(JobOutcome.Failed(FailureReason.Cancelled), null, extractor);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(ExtractionFailedOutcome(e), null, extractor);
        }

        extracted = ApplyExtractedResultTransforms(job, extracted);
        PublishExtractedResult(job, extracted, parentJob);
        return new(JobOutcome.Done(), extracted, extractor);
    }

    Job ApplyExtractedResultTransforms(ExtractJob job, Job extracted)
    {
        job.Result = extracted;

        if (extracted is IUpgradeable upgradeable)
        {
            var upgraded = upgradeable.Upgrade(job.Config.Extraction.IsAlbum, job.Config.Search.IsAggregate).ToList();

            if (upgraded.Count == 1)
            {
                job.Result = upgraded[0];
                extracted = job.Result;
            }
            else
            {
                job.Result = new JobList(extracted.ItemName, upgraded);
                extracted = job.Result;
                extracted.CopySharedFieldsFrom(upgradeable as Job ?? extracted);
            }
        }

        AssignWorkflowId(extracted, job.WorkflowId);
        if (job.ResultDownloadBehaviorPolicy != null)
            ApplyDownloadBehaviorPolicy(extracted, job.ResultDownloadBehaviorPolicy);

        // Propagate provenance from ExtractJob to the extracted result,
        // but don't overwrite a LineNumber already set by the extractor (e.g. CSV parsing).
        if (extracted.LineNumber == 0)
            extracted.LineNumber = job.LineNumber;
        extracted.ItemNumber = job.ItemNumber;
        extracted.SourceMutation ??= job.SourceMutation;

        if (job.EnablesIndexByDefault)
            extracted.EnablesIndexByDefault = true;

        // List/CSV row conditions are attached to the transient ExtractJob first.
        // Carry them across so profile resolution on the extracted job cannot drop them.
        // Merge rather than null-coalesce: the inner extractor may have created an
        // empty or partial patch, while the outer row still carries real conditions.
        extracted.ExtractorCond = FileConditionPatch.Merge(extracted.ExtractorCond, job.ExtractorCond);
        extracted.ExtractorPrefCond = FileConditionPatch.Merge(extracted.ExtractorPrefCond, job.ExtractorPrefCond);
        extracted.ExtractorFolderCond = FolderConditionPatch.Merge(extracted.ExtractorFolderCond, job.ExtractorFolderCond);
        extracted.ExtractorPrefFolderCond = FolderConditionPatch.Merge(extracted.ExtractorPrefFolderCond, job.ExtractorPrefFolderCond);

        // For a single-song JobList, also stamp the inner song (used by RemoveTrackFromSource),
        // but only if it doesn't already have a LineNumber from extraction (e.g. CSV parsing).
        if (extracted is JobList list && list.Jobs.Count == 1 && list.Jobs[0] is SongJob innerSong
            && innerSong.LineNumber == 0)
        {
            innerSong.LineNumber = job.LineNumber;
            innerSong.ItemNumber = job.ItemNumber;
            innerSong.SourceMutation ??= job.SourceMutation;

            if (job.EnablesIndexByDefault)
                innerSong.EnablesIndexByDefault = true;
        }

        return extracted;
    }

    void PublishExtractedResult(ExtractJob job, Job extracted, Job? parentJob)
    {
        var allSongs = (extracted is JobList list
            ? list.AllSongs()
            : extracted is SongJob song
                ? new[] { song }.AsEnumerable()
                : Enumerable.Empty<SongJob>()).ToList();
        SockseekLog.Jobs.Debug($"[{job.DisplayId}] ExtractJob: extracted {DescribeExtractedResult(extracted, allSongs.Count)}: {job.Input}");
        if (allSongs.Count > 0)
            Events.RaiseTrackListReady(allSongs);

        var newContexts = JobPreparer.PrepareSubtree(extracted, job.Config, _jobSettingsResolver, parentJob as JobList, Ctx(job));
        foreach (var (id, ctx) in newContexts)
            _contexts[id] = ctx;

        Events.RaiseJobResultCreated(job, extracted);
    }

    sealed record ExtractJobResult(JobOutcome Outcome, Job? Result, IExtractor? Extractor);

    static JobOutcome ExtractionFailedOutcome(Exception exception)
        => ExceptionFailureOutcome(FailureReason.ExtractionFailed, exception);

    static string ExtractorLogSource(InputType inputType)
        => inputType.ToString();

    static JobOutcome ExceptionFailureOutcome(FailureReason reason, Exception exception)
        => JobOutcome.Failed(
            reason,
            SockseekLog.ExceptionSummary(exception),
            SockseekLog.ExceptionDetail(exception));

    async Task<JobOutcome> ProcessSearchJob(SearchJob job)
    {
        var responseData = new ResponseData();
        var (outcome, searchFailure) = await TrySearchWithReconnect(job,
            () => searcher!.Search(job, job.Config.Search, responseData, job.Cts!.Token, completeSessionOnError: false));
        if (searchFailure != null)
            return searchFailure;

        CommitOutcome(job, outcome);
        return outcome;
    }

    async Task<T> RunSearchWithReconnect<T>(CancellationToken ct, Func<Task<T>> searchAction)
    {
        while (true)
        {
            await _clientManager.WaitUntilReadyAsync(ct);

            try
            {
                return await searchAction();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (!_clientManager.IsConnectedAndLoggedIn)
            {
            }
        }
    }

    async Task<JobOutcome?> TrySearchWithReconnect(Job job, Func<Task> searchAction)
    {
        var (_, outcome) = await TrySearchWithReconnect(job, async () =>
        {
            await searchAction();
            return true;
        });
        return outcome;
    }

    async Task<(T Result, JobOutcome? Failure)> TrySearchWithReconnect<T>(Job job, Func<Task<T>> searchAction)
    {
        try
        {
            var result = await RunSearchWithReconnect(job.Cts!.Token, searchAction);
            return (result, null);
        }
        catch (OperationCanceledException)
        {
            var outcome = JobOutcome.Failed(FailureReason.Cancelled);
            CommitOutcome(job, outcome);
            return (default!, outcome);
        }
        catch (Exception e)
        {
            if (job is SearchJob searchJob)
                searchJob.Session.Complete();

            var outcome = ExceptionFailureOutcome(FailureReason.Other, e);
            CommitOutcome(job, outcome);
            return (default!, outcome);
        }
    }

    async Task<JobOutcome> ProcessRetrieveFolderJob(RetrieveFolderJob job)
    {
        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        try
        {
            job.UpdateState(JobState.Searching);
            int newFilesFound = await searcher!.CompleteFolder(job.TargetFolder, job.Cts!.Token);
            job.NewFilesFoundCount = newFilesFound;
            job.RetrievalOutcome = FolderRetrievalOutcome.Completed;
            job.Discovery = new DiscoverySummary { ResultCount = newFilesFound, LockedFileCount = 0 };
            var outcome = JobOutcome.Done();
            CommitOutcome(job, outcome);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            job.Discovery = new DiscoverySummary { ResultCount = 0, LockedFileCount = 0 };
            job.RetrievalOutcome = FolderRetrievalOutcome.Cancelled;
            var outcome = JobOutcome.Failed(FailureReason.Cancelled);
            CommitOutcome(job, outcome);
            Events.RaiseJobStatus(job, "cancelled");
            return outcome;
        }
    }

    async Task<JobOutcome?> ProcessSongDiscovery(SongJob job)
    {
        var config = job.Config;

        if (config.PrintResults)
        {
            var searchFailure = await TrySearchWithReconnect(job,
                () => searcher!.SearchSong(job, config.Search, new ResponseData(), job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;

            var outcome = job.Candidates?.Count > 0
                ? JobOutcome.Done()
                : JobOutcome.Failed(FailureReason.NoSuitableFileFound);
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior != DownloadBehavior.Manual || job.ResolvedTarget != null)
            return null;

        var responseData = new ResponseData();
        if (job.Candidates == null)
        {
            var searchFailure = await TrySearchWithReconnect(job,
                () => searcher!.SearchSong(job, config.Search, responseData, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;
        }

        job.Discovery = new DiscoverySummary
        {
            ResultCount = job.Candidates?.Count ?? 0,
            LockedFileCount = responseData.lockedFilesCount,
        };

        var manualOutcome = job.Candidates?.Count > 0
            ? JobOutcome.StateChange(JobState.AwaitingSelection)
            : JobOutcome.Failed(FailureReason.NoSuitableFileFound);
        CommitOutcome(job, manualOutcome);
        return manualOutcome;
    }

    async Task<JobOutcome?> ProcessAlbumDiscovery(AlbumJob job, JobContext ctx)
    {
        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        bool foundSomething;

        if (job.ResolvedTarget != null)
        {
            if (job.Results.Count == 0)
                job.Results = [job.ResolvedTarget];

            if (job.ResolvedTargetNeedsInitialFolderRetrieval)
            {
                var retrieval = await ProcessFolderRetrieval(job.ResolvedTarget, job);
                job.ResolvedTargetNeedsInitialFolderRetrieval = false;
                if (retrieval.RetrievalCancelled || job.ResolvedTarget.Files.Count == 0)
                    job.Results.Clear();
            }

            foundSomething = true;
        }
        else if (job.Results.Count > 0)
            foundSomething = true;
        else
        {
            var (searchOutcome, searchFailure) = await TrySearchWithReconnect(job,
                () => searcher!.SearchAlbum(job, config.Search, responseData, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;

            if (searchOutcome != null)
            {
                CommitOutcome(job, searchOutcome);
                return searchOutcome;
            }

            foundSomething = job.Results.Count > 0;
        }
        foundSomething = job.Results.Count > 0;

        job.Discovery = new DiscoverySummary
        {
            ResultCount = job.Results.Count,
            LockedFileCount = responseData.lockedFilesCount
        };

        if (!foundSomething)
        {
            var outcome = JobOutcome.Failed(FailureReason.NoSuitableFileFound);
            CommitOutcome(job, outcome);
            await OnCompleteExecutor.ExecuteAsync(job, null, Ctx(job));

            if (!config.PrintResults)
                ctx.IndexEditor?.Update();

            return outcome;
        }

        if (!config.PrintResults
            && job.DownloadBehavior == DownloadBehavior.Manual
            && job.ResolvedTarget == null)
        {
            var outcome = JobOutcome.StateChange(JobState.AwaitingSelection);
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            CommitOutcome(job, outcome);
            return outcome;
        }

        return null;
    }

    async Task<JobOutcome?> ProcessAggregateDiscovery(AggregateJob job, JobContext ctx)
    {
        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        var (searchOutcome, searchFailure) = await TrySearchWithReconnect(job,
            () => searcher!.SearchAggregate(job, config.Search, responseData, job.Cts!.Token));
        if (searchFailure != null)
            return searchFailure;

        if (searchOutcome != null)
        {
            CommitOutcome(job, searchOutcome);
            return searchOutcome;
        }

        bool foundSomething = job.Songs.Count > 0;

        job.Discovery = new DiscoverySummary
        {
            ResultCount = foundSomething ? 1 : 0,
            LockedFileCount = responseData.lockedFilesCount,
        };

        if (!foundSomething)
        {
            var outcome = JobOutcome.Failed(FailureReason.NoSuitableFileFound);
            CommitOutcome(job, outcome);

            if (!config.PrintResults)
                ctx.IndexEditor?.Update();

            return outcome;
        }

        if (!config.PrintResults && job.DownloadBehavior == DownloadBehavior.Manual)
        {
            var outcome = JobOutcome.StateChange(JobState.AwaitingSelection);
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (config.Skip.SkipExisting)
        {
            var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.Search);
            foreach (var song in job.Songs)
                TrySetAlreadyExists(job, song, skipCtx);
        }

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            CommitOutcome(job, outcome);
            return outcome;
        }

        return null;
    }

    async Task<JobOutcome> ProcessAlbumAggregateDiscovery(AlbumAggregateJob job, JobContext ctx)
    {
        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        var (searchResult, searchFailure) = await TrySearchWithReconnect(job,
            () => searcher!.SearchAggregateAlbum(job, config.Search, responseData, job.Cts!.Token));
        if (searchFailure != null)
            return searchFailure;

        var (newAlbumJobs, searchOutcome) = searchResult;

        if (searchOutcome != null)
        {
            CommitOutcome(job, searchOutcome);
            return searchOutcome;
        }

        job.Albums = newAlbumJobs;

        foreach (var album in newAlbumJobs)
            album.DownloadBehaviorPolicy = job.DownloadBehaviorPolicy;

        bool foundSomething = newAlbumJobs.Count > 0;
        job.Discovery = new DiscoverySummary { ResultCount = job.Albums.Count, LockedFileCount = responseData.lockedFilesCount };

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (!foundSomething)
        {
            var outcome = JobOutcome.Failed(FailureReason.NoSuitableFileFound);
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior == DownloadBehavior.Manual)
        {
            var outcome = JobOutcome.StateChange(JobState.AwaitingSelection);
            CommitOutcome(job, outcome);
            return outcome;
        }

        var albumList = new JobList(job.ItemName, newAlbumJobs);
        albumList.Config = job.Config;
        albumList.WorkflowId = job.WorkflowId;
        foreach (var aj in newAlbumJobs)
        {
            aj.ItemName ??= job.ItemName;
            aj.Config = job.Config;
            _contexts[aj.Id] = new JobContext
            {
                IndexEditor = ctx.IndexEditor,
                PlaylistEditor = ctx.PlaylistEditor,
                OutputDirSkipper = ctx.OutputDirSkipper,
                MusicDirSkipper = ctx.MusicDirSkipper,
                PreprocessTracks = false,
            };
        }
        _contexts[albumList.Id] = new JobContext
        {
            IndexEditor = ctx.IndexEditor,
            PlaylistEditor = ctx.PlaylistEditor,
            OutputDirSkipper = ctx.OutputDirSkipper,
            MusicDirSkipper = ctx.MusicDirSkipper,
            PreprocessTracks = false,
        };

        RegisterJob(albumList, job);
        job.UpdateState(JobState.Running);
        await ProcessJob(albumList, job.Cts!.Token, job);

        var finalOutcome = DeriveAlbumAggregateOutcome(job, albumList);
        CommitOutcome(job, finalOutcome);
        return finalOutcome;
    }

    static JobOutcome DeriveAlbumAggregateOutcome(AlbumAggregateJob job, JobList albumList)
    {
        if (job.Cts?.IsCancellationRequested == true || albumList.FailureReason == FailureReason.Cancelled)
            return JobOutcome.Failed(FailureReason.Cancelled);

        var failedAlbum = job.Albums.FirstOrDefault(album => album.State == JobState.Failed);
        if (failedAlbum != null)
            return JobOutcome.Failed(
                failedAlbum.FailureReason == FailureReason.None ? FailureReason.AllDownloadsFailed : failedAlbum.FailureReason,
                failedAlbum.FailureMessage,
                failedAlbum.FailureDetail);

        var skippedAlbum = job.Albums.FirstOrDefault(album => album.State is JobState.Skipped or JobState.NotFoundLastTime);
        if (skippedAlbum != null)
            return JobOutcome.Skipped(JobState.Skipped, skippedAlbum.FailureReason);

        var unfinishedAlbum = job.Albums.FirstOrDefault(album => !IsSubtreeSuccessful(album));
        if (unfinishedAlbum != null)
            return JobOutcome.Failed(FailureReason.Other, $"Generated album did not finish successfully: {unfinishedAlbum}");

        return JobOutcome.Done();
    }

    async Task<JobOutcome> ProcessLeafDownload(Job job, JobContext ctx)
    {
        var config = job.Config;

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        try
        {
            JobOutcome outcome = JobOutcome.NoChange();
            switch (job)
            {
                case SongJob sj:
                    var songOrganizer = new FileManager(sj, config.Output, config.Extraction);
                    outcome = await ProcessSongDownload(sj, songOrganizer);
                    await CommitAndFinalizeSong(sj, sj, outcome, ctx, songOrganizer, organize: true, updateIndexes: true);
                    break;

                case AlbumJob aj:
                    outcome = await ProcessAlbumDownload(aj, ctx);
                    break;

                case AggregateJob ag:
                    // TODO [ARCHITECTURE]: AggregateJob processes songs via ProcessAggregateJob /
                    // DownloadEmbeddedSong rather than the standard ProcessJob pipeline that
                    // AlbumAggregateJob uses (wrapping results in a JobList). As a result songs
                    // are not individually registered upfront and are invisible to the state store
                    // until DownloadEmbeddedSong registers them one-by-one as they are dispatched.
                    // This pre-registration is a workaround. The proper fix is to align AggregateJob
                    // with AlbumAggregateJob: wrap pending songs in a JobList after skip-checking
                    // and process it through ProcessJob, giving songs first-class lifecycle tracking
                    // without this manual step.
                    foreach (var song in ag.Songs.Where(s => s.State == JobState.Pending))
                    {
                        song.WorkflowId = ag.WorkflowId;
                        song.Config = config;
                        RegisterJob(song, ag);
                    }
                    ag.UpdateState(JobState.Running);
                    outcome = await ProcessAggregateDownload(ag, ctx);
                    CommitOutcome(ag, outcome);
                    break;
            }

            SockseekLog.Jobs.Trace($"ProcessLeafJob: finished for job {job.DisplayId} ({job.GetType().Name})");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            var outcome = JobOutcome.Failed(FailureReason.Cancelled);
            CommitOutcome(job, outcome);
            return outcome;
        }
    }

    static bool HasPreResolvedAlbumResults(Job job)
        => job is AlbumJob albumJob
            && (albumJob.ResolvedTarget != null || albumJob.Results.Count > 0);


    // ── per-job-type handlers ─────────────────────────────────────────────────

    async Task<JobOutcome> ProcessSongDownload(SongJob job, FileManager organizer)
    {
        var config = job.Config;

        // If ResolvedTarget is set, pre-populate Candidates so search is skipped.
        if (job.ResolvedTarget != null && job.Candidates == null)
            job.Candidates = new List<FileCandidate> { job.ResolvedTarget };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        return await DownloadSong(job, job, config, organizer, cts);
    }


    async Task<JobOutcome> ProcessAggregateDownload(AggregateJob job, JobContext ctx)
    {
        var config = job.Config;
        var songs = job.Songs;
        var organizer = new FileManager(job, config.Output, config.Extraction);

        var downloadTasks = songs.Select(async song =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
            var outcome = await WithJobSlot(job.Cts!.Token, () =>
                DownloadEmbeddedSong(song, job, config, organizer, cts, cancelGroupOnFail: false, organize: true));
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return outcome;
        });

        var childOutcomes = await Task.WhenAll(downloadTasks);
        if (job.Cts?.IsCancellationRequested == true)
            return JobOutcome.Failed(FailureReason.Cancelled);

        var failedOutcome = childOutcomes.FirstOrDefault(outcome =>
            outcome.State == JobState.Failed && outcome.FailureReason != FailureReason.Cancelled);
        if (failedOutcome != null)
            return JobOutcome.Failed(
                failedOutcome.FailureReason == FailureReason.None ? FailureReason.AllDownloadsFailed : failedOutcome.FailureReason,
                failedOutcome.FailureMessage,
                failedOutcome.FailureDetail);

        // Debatable: without a partial-success state, a locally cancelled child is
        // treated like a child-level result rather than parent cancellation.
        return JobOutcome.Done();
    }


    async Task<JobOutcome> ProcessAlbumDownload(AlbumJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(job, config.Output, config.Extraction);
        var audioResult = await TryDownloadAlbumAudio(job, ctx, organizer);
        var completion = CommitAlbumAudioOutcome(job, audioResult, ctx);
        var chosenFiles = completion.ChosenFiles;

        if (completion.Outcome.State == JobState.AwaitingSelection)
        {
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return completion.Outcome;
        }

        MarkCancelledAlbumFiles(job, audioResult);
        var images = await DownloadAlbumImagesIfNeeded(job, ctx, organizer, audioResult, chosenFiles);
        OrganizeAlbumIfNeeded(job, organizer, images.ChosenFiles, images.AdditionalImages);
        RefreshDownloadedFileCache(images.ChosenFiles);
        RefreshDownloadedFileCache(images.AdditionalImages);

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await OnCompleteExecutor.ExecuteAsync(job, null, ctx);
        return completion.Outcome;
    }

    AlbumDownloadCompletion CommitAlbumAudioOutcome(AlbumJob job, AlbumAudioDownloadResult audioResult, JobContext ctx)
    {
        var chosenFiles = audioResult.ChosenFiles;
        JobOutcome outcome;

        if (audioResult.Succeeded && chosenFiles != null)
        {
            var downloadedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.State == JobState.Done && !string.IsNullOrEmpty(af.DownloadPath));

            if (downloadedAudio.Any())
            {
                var downloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(af => af.DownloadPath!));
                outcome = JobOutcome.Done(downloadPath);
                CommitOutcome(job, outcome);
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, downloadPath);
                // Note: album jobs have no parent extractor reference here; RemoveTrackFromSource
                // for albums is handled at the JobList fan-out level if needed.
            }
            else
            {
                outcome = JobOutcome.Done();
                CommitOutcome(job, outcome);
            }
        }
        else if (audioResult.Outcome != null)
        {
            outcome = audioResult.Outcome;
            CommitOutcome(job, outcome);
        }
        else
        {
            outcome = JobOutcome.Failed(FailureReason.NoSuitableFileFound);
            CommitOutcome(job, outcome);
        }

        return new(outcome, chosenFiles);
    }

    void MarkCancelledAlbumFiles(AlbumJob job, AlbumAudioDownloadResult audioResult)
    {
        if (job.FailureReason == FailureReason.Cancelled)
        {
            var cancelledFolder = job.ResolvedTarget
                ?? audioResult.LastChosenFolder;

            if (cancelledFolder != null)
                MarkUnfinishedAlbumFilesCancelled(cancelledFolder);
        }
    }

    async Task<AlbumImageDownloadResult> DownloadAlbumImagesIfNeeded(
        AlbumJob job,
        JobContext ctx,
        FileManager organizer,
        AlbumAudioDownloadResult audioResult,
        List<SongJob>? chosenFiles)
    {
        var config = job.Config;
        if (!config.Output.AlbumArtOnly && (!audioResult.Succeeded || config.Output.AlbumArtOption == AlbumArtOption.Default))
            return new(chosenFiles, null);

        SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: downloading additional images: {job}");
        var additionalImages = await DownloadImages(job, ctx, organizer, job.ResolvedTarget);

        if (chosenFiles != null && additionalImages.Count > 0)
        {
            var addedPaths = additionalImages
                .Select(af => Utils.NormalizedPath(af.DownloadPath ?? ""))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToHashSet();

            chosenFiles.RemoveAll(af => af.IsNotAudio
                && !string.IsNullOrEmpty(af.DownloadPath)
                && addedPaths.Contains(Utils.NormalizedPath(af.DownloadPath)));

            chosenFiles.AddRange(additionalImages);
        }

        return new(chosenFiles, additionalImages);
    }

    static void OrganizeAlbumIfNeeded(
        AlbumJob job,
        FileManager organizer,
        List<SongJob>? chosenFiles,
        List<SongJob>? additionalImages)
    {
        if (chosenFiles != null && !string.IsNullOrEmpty(job.DownloadPath))
            organizer.OrganizeAlbum(job, chosenFiles, additionalImages);
    }

    sealed record AlbumDownloadCompletion(JobOutcome Outcome, List<SongJob>? ChosenFiles);
    sealed record AlbumImageDownloadResult(List<SongJob>? ChosenFiles, List<SongJob>? AdditionalImages);

    sealed record AlbumAudioDownloadResult(
        bool Succeeded,
        JobOutcome? Outcome,
        List<SongJob>? ChosenFiles,
        AlbumFolder? LastChosenFolder);

    async Task<AlbumAudioDownloadResult> TryDownloadAlbumAudio(AlbumJob job, JobContext ctx, FileManager organizer)
    {
        var config = job.Config;
        var retrievedFolders = new HashSet<string>();
        string? filterStr = null;
        int index = 0;
        int tried = 0;
        int albumTrackCountRetries = config.Transfer.AlbumTrackCountMaxRetries;
        AlbumFolder? lastChosenFolder = null;

        async Task RunAlbumDownloads(AlbumFolder folder, CancellationTokenSource cts)
        {
            var tasks = folder.Files.Select(async af =>
            {
                if (af.State != JobState.Pending) return;
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await DownloadEmbeddedSong(af, job, config, organizer, cts, cancelGroupOnFail: true, organize: true);
            });
            await Task.WhenAll(tasks);
        }

        AlbumAudioDownloadResult ReturnSelectedFolderToManualPicker(AlbumFolder? failedFolder, FailureReason finalReason)
        {
            if (job.DownloadBehavior != DownloadBehavior.Manual || job.Cts?.IsCancellationRequested == true)
                return new(false, JobOutcome.Failed(finalReason), null, failedFolder ?? lastChosenFolder);

            if (failedFolder != null)
                job.Results.RemoveAll(folder => SameAlbumFolder(folder, failedFolder));

            job.ResolvedTarget = null;
            job.AllowBrowseResolvedTarget = true;
            job.SkipResolvedTargetTrackCountVerification = false;
            organizer.SetremoteBaseDir(null);

            if (job.Results.Count == 0)
                return new(false, JobOutcome.Failed(finalReason), null, failedFolder ?? lastChosenFolder);

            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return new(false, JobOutcome.StateChange(JobState.AwaitingSelection), null, failedFolder ?? lastChosenFolder);
        }
        while (job.Results.Count > 0 && !config.Output.AlbumArtOnly)
        {
            bool wasPreselected = job.ResolvedTarget != null;
            bool retrieveCurrent = wasPreselected ? job.AllowBrowseResolvedTarget : true;
            index = 0;

            AlbumFolder chosenFolder;

            if (wasPreselected)
            {
                chosenFolder = job.ResolvedTarget!;
                index = job.Results.Contains(chosenFolder) ? job.Results.IndexOf(chosenFolder) : 0;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(filterStr))
                {
                    index = job.Results.FindIndex(f => f.Files.Any(af => af.ResolvedTarget!.Filename.ContainsIgnoreCase(filterStr)));
                    if (index == -1) break;
                }
                chosenFolder = job.Results[index];
            }

            // Track-count correctness is independent of the optional post-download browse.
            // Search results can prove max overflow, but min underflow and hidden max
            // overflow require browsing unless the folder is already fully retrieved.
            var folderCond = config.Search.NecessaryFolderCond;
            bool verifyTrackCount = !wasPreselected || !job.SkipResolvedTargetTrackCountVerification;
            if (verifyTrackCount
                && config.Transfer.AlbumTrackCountMaxRetries > 0
                && ((folderCond.MaxTrackCount ?? 0) > 0 || (folderCond.MinTrackCount ?? 0) > 0))
            {
                int KnownAudioCount() => chosenFolder.Files.Count(af => !af.IsNotAudio);
                int knownCount = KnownAudioCount();
                bool mustBrowseBeforeDownload = !chosenFolder.IsFullyRetrieved
                    && ((folderCond.MaxTrackCount is int browseMaxTrackCount && browseMaxTrackCount > 0 && knownCount <= browseMaxTrackCount)
                        || (folderCond.MinTrackCount is int browseMinTrackCount && browseMinTrackCount > 0 && knownCount < browseMinTrackCount));

                if (mustBrowseBeforeDownload && !retrievedFolders.Contains(chosenFolder.FolderPath))
                {
                    var retrieval = await ProcessFolderRetrieval(chosenFolder, job,
                        "Verifying album track count.\n    Retrieving full folder contents...",
                        consumeJobSlot: false);
                    if (retrieval.RetrievalCompleted)
                        retrievedFolders.Add(chosenFolder.FolderPath);
                    else
                    {
                        SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: album track count verification was cancelled, skipping folder: {chosenFolder.FolderPath}");
                        if (wasPreselected)
                            return ReturnSelectedFolderToManualPicker(chosenFolder, FailureReason.NoSuitableFileFound);

                        job.Results.RemoveAt(index);
                        if (--albumTrackCountRetries <= 0)
                        {
                            SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times, skipping album: {job}");
                            return new(false, JobOutcome.Failed(FailureReason.NoSuitableFileFound), null, lastChosenFolder);
                        }
                        continue;
                    }
                    knownCount = KnownAudioCount();
                }

                bool trackCountFailed = false;
                if (folderCond.MaxTrackCount is { } maxTrackCount and > 0 && knownCount > maxTrackCount)
                { SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: file count ({knownCount}) above maximum ({maxTrackCount}), skipping folder: {chosenFolder.FolderPath}"); trackCountFailed = true; }
                if (folderCond.MinTrackCount is { } minTrackCount and > 0 && knownCount < minTrackCount)
                { SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: file count ({knownCount}) below minimum ({minTrackCount}), skipping folder: {chosenFolder.FolderPath}"); trackCountFailed = true; }

                if (trackCountFailed)
                {
                    if (wasPreselected)
                    {
                        SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: preselected folder failed album track count condition, skipping album: {chosenFolder.FolderPath}");
                        return ReturnSelectedFolderToManualPicker(chosenFolder, FailureReason.NoSuitableFileFound);
                    }

                    job.Results.RemoveAt(index);
                    if (--albumTrackCountRetries <= 0)
                    {
                        SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times, skipping album: {job}");
                        return new(false, JobOutcome.Failed(FailureReason.NoSuitableFileFound), null, lastChosenFolder);
                    }
                    continue;
                }
            }

            lastChosenFolder = chosenFolder;
            organizer.SetremoteBaseDir(chosenFolder.FolderPath);
            job.ResolvedTarget = chosenFolder;
            job.UpdateState(JobState.Downloading);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            tried++;

            try
            {
                await RunAlbumDownloads(chosenFolder, cts);

                if (!config.Search.NoBrowseFolder && retrieveCurrent && !chosenFolder.IsFullyRetrieved && !retrievedFolders.Contains(chosenFolder.FolderPath))
                {
                    var retrieval = await ProcessFolderRetrieval(chosenFolder, job, consumeJobSlot: false);
                    if (retrieval.RetrievalCompleted)
                        retrievedFolders.Add(chosenFolder.FolderPath);
                    if (retrieval.NewFilesFoundCount > 0)
                    {
                        await RunAlbumDownloads(chosenFolder, cts);
                    }
                }

                job.ResolvedTarget = chosenFolder;
                return new(true, null, chosenFolder.Files, lastChosenFolder);
            }
            catch (OperationCanceledException)
            {
                MarkUnfinishedAlbumFilesCancelled(chosenFolder);

                if (!config.IgnoreAlbumFail)
                    HandleAlbumFail(job, chosenFolder, config.DeleteAlbumOnFail, config);

                if (job.Cts != null && job.Cts.IsCancellationRequested)
                    return new(false, JobOutcome.Failed(FailureReason.Cancelled), null, lastChosenFolder);

                if (wasPreselected)
                    break;
            }

            organizer.SetremoteBaseDir(null);
            if (wasPreselected || tried >= config.Transfer.MaxDownloadRetries)
                return wasPreselected
                    ? ReturnSelectedFolderToManualPicker(lastChosenFolder, FailureReason.AllDownloadsFailed)
                    : new(false, JobOutcome.Failed(FailureReason.AllDownloadsFailed), null, lastChosenFolder);

            job.ResolvedTarget = null;
            job.Results.RemoveAt(index);

            // Reset state so the next iteration transitions to Downloading naturally
            job.UpdateState(JobState.Pending);
        }

        return new(false, null, null, lastChosenFolder);
    }

    void MarkUnfinishedAlbumFilesCancelled(AlbumFolder folder)
    {
        foreach (var song in folder.Files.Where(song => song.State is not (JobState.Done or JobState.AlreadyExists or JobState.Failed)))
        {
            song.Fail(FailureReason.Cancelled);
        }
    }


    // ── single-song download ──────────────────────────────────────────────────

    async Task<JobOutcome> DownloadSong(SongJob song, Job job, DownloadSettings config, FileManager organizer,
        CancellationTokenSource cts)
    {
        if (song.State != JobState.Pending) return JobOutcome.NoChange();

        int tries = config.Transfer.UnknownErrorRetries;
        JobOutcome? finalOutcome = null;
        string? lastFailureMessage = null;
        string? lastFailureDetail = null;

        while (tries > 0)
        {
            if (song.State == JobState.Done || song.State == JobState.Failed)
                break;

            await _clientManager.WaitUntilReadyAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                var outcome = await SearchAndDownloadSong(song, job, config, organizer, cts);
                if (outcome.State == JobState.Done)
                {
                    finalOutcome = outcome;
                }
                else
                {
                    lastFailureMessage = outcome.FailureMessage;
                    lastFailureDetail = outcome.FailureDetail;
                    finalOutcome = outcome;
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    SockseekLog.Jobs.Debug($"{ex}");
                else
                    SockseekLog.Jobs.Debug($"Cancelled: {song}");

                if (!_clientManager.IsConnectedAndLoggedIn)
                {
                    continue;
                }
                else if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    return JobOutcome.Failed(FailureReason.Cancelled);
                }
                else
                {
                    lastFailureMessage = DownloadFailureMessage(ex);
                    lastFailureDetail = SockseekLog.ExceptionDetail(ex);
                    tries--;
                    continue;
                }
            }

            break;
        }

        if (tries == 0)
        {
            return JobOutcome.Failed(FailureReason.AllDownloadsFailed, lastFailureMessage, lastFailureDetail);
        }

        return finalOutcome ?? JobOutcome.NoChange();
    }

    async Task CommitAndFinalizeSong(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        JobContext jobCtx,
        FileManager organizer,
        bool organize,
        bool updateIndexes)
    {
        CommitOutcome(song, outcome);
        if (outcome.FailureReason != FailureReason.Cancelled)
            await CompleteSongAfterOutcome(song, parentJob, jobCtx, organizer, organize);

        if (updateIndexes)
        {
            SockseekLog.Jobs.Trace($"ProcessSongJob finished for {song.DisplayId}. Calling IndexEditor Update ({(jobCtx.IndexEditor != null ? "Yes" : "No")}) and PlaylistEditor Update ({(jobCtx.PlaylistEditor != null ? "Yes" : "No")})");
            jobCtx.IndexEditor?.Update();
            jobCtx.PlaylistEditor?.Update();
        }
    }

    async Task CompleteSongAfterOutcome(SongJob song, Job parentJob, JobContext jobCtx, FileManager organizer, bool organize)
    {
        if (song.State == JobState.Done && organize)
        {
            lock (_registry.DownloadedFiles)
            {
                organizer.OrganizeSong(song);
                RefreshDownloadedFileCache(song);
            }
        }

        if (parentJob.Config.HasOnComplete)
        {
            Events.RaiseOnCompleteStart(song);
            await OnCompleteExecutor.ExecuteAsync(parentJob, song, jobCtx);
            Events.RaiseOnCompleteEnd(song);
        }

        RefreshDownloadedFileCache(song);
    }

    void RefreshDownloadedFileCache(SongJob song)
    {
        if (song.State != JobState.Done)
            return;

        var candidate = song.ChosenCandidate;
        if (candidate == null || string.IsNullOrEmpty(song.DownloadPath))
            return;

        var fileKey = candidate.Username + '\\' + candidate.Filename;
        lock (_registry.DownloadedFiles)
            _registry.DownloadedFiles[fileKey] = new FileDownloadResult(song.DownloadPath, candidate);
    }

    void RefreshDownloadedFileCache(IEnumerable<SongJob>? songs)
    {
        if (songs == null)
            return;

        foreach (var song in songs)
            RefreshDownloadedFileCache(song);
    }

    static string? DownloadFailureMessage(Exception ex)
        => SockseekLog.ExceptionSummary(ex);

    static string JobLogKind(Job job) => job switch
    {
        SongJob => "SongJob",
        AlbumJob => "AlbumJob",
        AlbumAggregateJob => "AlbumAggregateJob",
        AggregateJob => "AggregateJob",
        SearchJob => "SearchJob",
        RetrieveFolderJob => "RetrieveFolderJob",
        ExtractJob => "ExtractJob",
        JobList => "Job List",
        _ => job.GetType().Name,
    };


    /// <summary>
    /// Searches for candidates for <paramref name="song"/> then downloads the best one.
    /// Returns an explicit outcome for expected domain failures; unexpected infrastructure
    /// exceptions still bubble to the retry policy in <see cref="DownloadSong"/>.
    /// </summary>
    async Task<JobOutcome> SearchAndDownloadSong(SongJob song, Job job, DownloadSettings config,
        FileManager organizer, CancellationTokenSource cts)
    {
        var responseData = new ResponseData();

        // Skip search if candidates are pre-set (ResolvedTarget / direct download).
        if (song.Candidates == null)
        {

            if (!config.Search.FastSearch)
            {
                await searcher!.SearchSong(song, config.Search, responseData, cts.Token);
            }
            else
            {
                // Fast-search: start the search as a background task and race it against a
                // provisional download of the first qualifying candidate.
                // The search concurrency slot is held by SearchSong internally; cancelling
                // searchCts causes SearchSong to return and release it naturally.
                using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                Task<FileDownloadOutcome?>? fastDownloadTask = null;

                var searchTask = searcher!.SearchSong(song, config.Search, responseData, searchCts.Token,
                    onFastSearchCandidate: fc =>
                    {
                        if (fastDownloadTask == null)
                        {
                            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search starting provisional download from {fc.Username}\\{fc.Filename}: {song}");
                            string outputPath = organizer.GetSavePath(fc.Filename);

                            // Use the main job CTS for the download so cancelling the search doesn't kill the download.
                            fastDownloadTask = downloader!
                                .DownloadFile(fc, outputPath, song, config.Transfer, config.Output.ParentDir, cts.Token)
                                .ContinueWith(t =>
                                {
                                    if (t.IsCompletedSuccessfully)
                                        return (FileDownloadOutcome?)t.Result;
                                    return null;
                                }, TaskScheduler.Default);
                        }
                    });

                while (!searchTask.IsCompleted)
                {
                    if (fastDownloadTask != null && fastDownloadTask.IsCompleted)
                        break;
                    await Task.WhenAny(fastDownloadTask ?? searchTask, searchTask);
                }

                if (fastDownloadTask != null)
                {
                    var fastDownload = await fastDownloadTask;
                    if (fastDownload?.Status == FileDownloadStatus.Completed && fastDownload.Result != null)
                    {
                        // Fast download won — cancel the search.
                        searchCts.Cancel();
                        try { await searchTask; } catch (OperationCanceledException) { }

                        var result = fastDownload.Result;
                        _registry.UserSuccessCounts.AddOrUpdate(result.Candidate.Username, 1, (_, c) => c + 1);
                        SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search provisional download succeeded from {result.Candidate.Username}\\{result.Candidate.Filename}: {song}");
                        return JobOutcome.Done(result.OutputPath, result.Candidate);
                    }

                    if (fastDownload?.Status == FileDownloadStatus.ManuallySkipped)
                    {
                        SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search provisional download was manually skipped, waiting for full search to complete: {song}");
                    }
                    else
                    {
                        SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search provisional download failed, waiting for full search to complete: {song}");
                    }

                    await searchTask;
                }
                else
                {
                    await searchTask;
                }
            }
        }

        var candidates = song.Candidates;

        if (candidates == null || candidates.Count == 0)
        {
            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: no suitable candidates after search: {song}");
            return JobOutcome.Failed(FailureReason.NoSuitableFileFound);
        }

        // Try candidates in order until one succeeds.
        int tried = 0;
        Exception? lastDownloadException = null;
        foreach (var candidate in candidates)
        {
            tried++;
            string outputPath = organizer.GetSavePath(candidate.Filename);

            try
            {
                song.UpdateState(JobState.Downloading);
                // ReportDownloadStart is called inside DownloadFile (via Downloader).
                var download = await downloader!.DownloadFile(candidate, outputPath, song, config.Transfer, config.Output.ParentDir, cts.Token);
                if (download.Status == FileDownloadStatus.ManuallySkipped)
                {
                    SockseekLog.Jobs.Debug($"Manually skipped candidate: {candidate.Username}\\{candidate.Filename}");
                    tried--;
                    continue;
                }

                var result = download.Result
                    ?? throw new InvalidOperationException($"Completed download outcome missing result for '{candidate.Username}\\{candidate.Filename}'.");
                _registry.UserSuccessCounts.AddOrUpdate(result.Candidate.Username, 1, (_, c) => c + 1);
                SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: download succeeded from {result.Candidate.Username}\\{result.Candidate.Filename} to '{result.OutputPath}': {song}");
                return JobOutcome.Done(result.OutputPath, result.Candidate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_clientManager.IsConnectedAndLoggedIn)
                    throw;

                lastDownloadException = ex;
                SockseekLog.Jobs.Debug(SockseekLog.FormatException($"Download attempt {tried} failed for '{candidate.Username}\\{candidate.Filename}' to '{outputPath}'", ex));
                if (tried >= candidates.Count || tried >= config.Transfer.MaxDownloadRetries)
                {
                    return JobOutcome.Failed(
                        FailureReason.AllDownloadsFailed,
                        DownloadFailureMessage(ex),
                        SockseekLog.ExceptionDetail(ex));
                }
            }
        }

        if (lastDownloadException != null)
            return JobOutcome.Failed(
                FailureReason.AllDownloadsFailed,
                DownloadFailureMessage(lastDownloadException),
                SockseekLog.ExceptionDetail(lastDownloadException));

        return JobOutcome.Failed(FailureReason.NoSuitableFileFound);
    }

    static void CommitOutcome(Job job, JobOutcome outcome)
    {
        if (!outcome.ShouldCommit)
            return;

        switch (outcome.State)
        {
            case JobState.Done:
                if (job is SongJob song)
                    song.SetDone(outcome.DownloadPath, outcome.ChosenCandidate);
                else if (job is AlbumJob album)
                    album.SetDone(outcome.DownloadPath);
                else
                    job.SetDone();
                break;

            case JobState.Failed:
                job.Fail(outcome.FailureReason, outcome.FailureMessage, outcome.FailureDetail);
                break;

            case JobState.AlreadyExists:
                if (job is SongJob existingSong)
                    existingSong.SetAlreadyExists(outcome.DownloadPath);
                else if (job is AlbumJob existingAlbum)
                    existingAlbum.SetAlreadyExists(outcome.DownloadPath);
                else
                    job.SetAlreadyExists();
                break;

            case JobState.Skipped:
            case JobState.NotFoundLastTime:
                job.SetSkipped(outcome.State, outcome.FailureReason);
                break;

            default:
                job.UpdateState(outcome.State);
                break;
        }
    }




    // ── skip helpers ──────────────────────────────────────────────────────────

    bool TrySetAlreadyExists(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        string? path = null;
        var jobCtx = Ctx(job);

        if (jobCtx.OutputDirSkipper != null)
        {
            if (!jobCtx.OutputDirSkipper.IndexIsBuilt) jobCtx.OutputDirSkipper.BuildIndex();
            jobCtx.OutputDirSkipper.SongExists(song, skipCtx, out path);
        }

        if (path == null && jobCtx.MusicDirSkipper != null)
        {
            if (!jobCtx.MusicDirSkipper.IndexIsBuilt)
            {
                SockseekLog.Jobs.Info("Building music directory index..");
                jobCtx.MusicDirSkipper.BuildIndex();
            }
            jobCtx.MusicDirSkipper.SongExists(song, skipCtx, out path);
        }

        if (path != null)
        {
            song.SetAlreadyExists(path);
            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: skipped because matching file already exists at '{path}': {song}");
        }

        return path != null;
    }

    bool TrySetJobAlreadyExists(Job job, JobContext ctx)
    {
        var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.Search);
        string? path = null;

        if (job is SongJob song)
        {
            return TrySetAlreadyExists(job, song, skipCtx);
        }
        else if (job is AlbumJob aj)
        {
            if (ctx.OutputDirSkipper != null)
            {
                if (!ctx.OutputDirSkipper.IndexIsBuilt) ctx.OutputDirSkipper.BuildIndex();
                ctx.OutputDirSkipper.AlbumExists(aj, skipCtx, out path);
            }

            if (path == null && ctx.MusicDirSkipper != null)
            {
                if (!ctx.MusicDirSkipper.IndexIsBuilt)
                {
                    SockseekLog.Jobs.Info("Building music directory index..");
                    ctx.MusicDirSkipper.BuildIndex();
                }
                ctx.MusicDirSkipper.AlbumExists(aj, skipCtx, out path);
            }
        }
        else
        {
            return false;
        }

        if (path != null)
        {
            if (job is AlbumJob albumJob)
                albumJob.SetAlreadyExists(path);
            else
                job.SetAlreadyExists();
            ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, path);
            SockseekLog.Jobs.Debug($"[{job.DisplayId}] {JobLogKind(job)}: skipped because matching output already exists at '{path}': {job}");
        }

        return path != null;
    }

    JobOutcome? TryGetJobAlreadyExistsOutcome(Job job, JobContext ctx)
    {
        var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.Search);
        string? path = null;

        if (job is SongJob song)
        {
            path = FindExistingSongPath(job, song, skipCtx);
        }
        else if (job is AlbumJob aj)
        {
            if (ctx.OutputDirSkipper != null)
            {
                if (!ctx.OutputDirSkipper.IndexIsBuilt) ctx.OutputDirSkipper.BuildIndex();
                ctx.OutputDirSkipper.AlbumExists(aj, skipCtx, out path);
            }

            if (path == null && ctx.MusicDirSkipper != null)
            {
                if (!ctx.MusicDirSkipper.IndexIsBuilt)
                {
                    SockseekLog.Jobs.Info("Building music directory index..");
                    ctx.MusicDirSkipper.BuildIndex();
                }
                ctx.MusicDirSkipper.AlbumExists(aj, skipCtx, out path);
            }
        }
        else
        {
            return null;
        }

        if (path == null)
            return null;

        SockseekLog.Jobs.Debug($"[{job.DisplayId}] {JobLogKind(job)}: skipped because matching output already exists at '{path}': {job}");
        return JobOutcome.AlreadyExists(path);
    }

    string? FindExistingSongPath(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        string? path = null;
        var jobCtx = Ctx(job);

        if (jobCtx.OutputDirSkipper != null)
        {
            if (!jobCtx.OutputDirSkipper.IndexIsBuilt) jobCtx.OutputDirSkipper.BuildIndex();
            jobCtx.OutputDirSkipper.SongExists(song, skipCtx, out path);
        }

        if (path == null && jobCtx.MusicDirSkipper != null)
        {
            if (!jobCtx.MusicDirSkipper.IndexIsBuilt)
            {
                SockseekLog.Jobs.Info("Building music directory index..");
                jobCtx.MusicDirSkipper.BuildIndex();
            }
            jobCtx.MusicDirSkipper.SongExists(song, skipCtx, out path);
        }

        return path;
    }

    bool TrySetNotFoundLastTime(SongJob song, M3uEditor? indexEditor)
    {
        if (indexEditor == null) return false;
        var prev = indexEditor.PreviousRunResult(song);
        if (prev == null) return false;
        if (prev.FailureReason == FailureReason.NoSuitableFileFound || prev.State == JobState.NotFoundLastTime)
        {
            song.SetSkipped(JobState.NotFoundLastTime);
            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: skipped because prior index entry was {prev.State}/{prev.FailureReason}: {song}");
            return true;
        }
        return false;
    }

    bool TrySetNotFoundLastTimeForJob(Job job)
    {
        var jobCtx = Ctx(job);
        if (jobCtx.IndexEditor == null) return false;
        IndexEntry? prev = null;

        if (job is SongJob song)
            prev = jobCtx.IndexEditor.PreviousRunResult(song);
        else if (job is AlbumJob aj)
            prev = jobCtx.IndexEditor.PreviousRunResult(aj);

        if (prev == null) return false;
        if (prev.FailureReason == FailureReason.NoSuitableFileFound || prev.State == JobState.NotFoundLastTime)
        {
            job.SetSkipped(job is SongJob ? JobState.NotFoundLastTime : JobState.Skipped, FailureReason.NoSuitableFileFound);
            SockseekLog.Jobs.Debug($"[{job.DisplayId}] {JobLogKind(job)}: skipped because prior index entry was {prev.State}/{prev.FailureReason}: {job}");
            return true;
        }
        return false;
    }

    JobOutcome? TryGetNotFoundLastTimeOutcome(Job job)
    {
        var jobCtx = Ctx(job);
        if (jobCtx.IndexEditor == null) return null;
        IndexEntry? prev = null;

        if (job is SongJob song)
            prev = jobCtx.IndexEditor.PreviousRunResult(song);
        else if (job is AlbumJob aj)
            prev = jobCtx.IndexEditor.PreviousRunResult(aj);

        if (prev == null) return null;
        if (prev.FailureReason != FailureReason.NoSuitableFileFound && prev.State != JobState.NotFoundLastTime)
            return null;

        SockseekLog.Jobs.Debug($"[{job.DisplayId}] {JobLogKind(job)}: skipped because prior index entry was {prev.State}/{prev.FailureReason}: {job}");
        return JobOutcome.Skipped(
            job is SongJob ? JobState.NotFoundLastTime : JobState.Skipped,
            FailureReason.NoSuitableFileFound);
    }


    // ── album failure handling ────────────────────────────────────────────────

    // Applies search-specific settings (ArtistMaybeWrong, folder track-count constraints)
    // to every query in the job tree.  Separated from Preprocessor because these are
    // search concerns, not text-transformation concerns.
    static void ApplySearchSettings(Job job, SearchSettings search)
    {
        switch (job)
        {
            case JobList jl:
                foreach (var s in jl.Jobs.OfType<SongJob>())  ApplySearchSettings(s, search);
                foreach (var a in jl.Jobs.OfType<AlbumJob>()) ApplySearchSettings(a, search);
                break;

            case SongJob song:
                if (search.ArtistMaybeWrong && !song.Query.ArtistMaybeWrong)
                    song.Query = new SongQuery(song.Query) { ArtistMaybeWrong = true };
                break;

            case AlbumJob aj:
                ApplySearchSettingsToAlbumQuery(aj, search);
                break;

            case AggregateJob ag:
                foreach (var s in ag.Songs) ApplySearchSettings(s, search);
                break;

            case AlbumAggregateJob aaj:
                ApplySearchSettingsToAlbumAggregateQuery(aaj, search);
                break;
        }
    }

    static void ApplySearchSettingsToAlbumQuery(AlbumJob aj, SearchSettings search)
    {
        var q   = aj.Query;
        bool amw = q.ArtistMaybeWrong;

        if (search.ArtistMaybeWrong)                               amw = true;

        if (amw != q.ArtistMaybeWrong)
            aj.Query = new AlbumQuery(q) { ArtistMaybeWrong = amw };
    }

    static void ApplySearchSettingsToAlbumAggregateQuery(AlbumAggregateJob aaj, SearchSettings search)
    {
        var q   = aaj.Query;
        bool amw = q.ArtistMaybeWrong;

        if (search.ArtistMaybeWrong)                               amw = true;

        if (amw != q.ArtistMaybeWrong)
            aaj.Query = new AlbumQuery(q) { ArtistMaybeWrong = amw };
    }

    static void AssignWorkflowId(Job job, Guid workflowId)
    {
        job.WorkflowId = workflowId;

        switch (job)
        {
            case JobList jl:
                foreach (var child in jl.Jobs)
                    AssignWorkflowId(child, workflowId);
                break;

            case AggregateJob ag:
                foreach (var song in ag.Songs)
                    AssignWorkflowId(song, workflowId);
                break;
        }
    }

    void HandleAlbumFail(AlbumJob job, AlbumFolder folder, bool deleteDownloaded, DownloadSettings config)
    {
        var failedAlbumPath = config.Output.FailedAlbumPath;
        var outputParentDir = config.Output.ParentDir;

        if (deleteDownloaded)
        {
            Events.RaiseJobStatus(job, "deleting files");
            SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: Deleting album files");
        }
        else if (!string.IsNullOrEmpty(failedAlbumPath))
        {
            if (string.IsNullOrEmpty(outputParentDir))
                throw new InvalidOperationException("Cannot move failed album files because Output.ParentDir is not set.");

            Events.RaiseJobStatus(job, $"moving to {failedAlbumPath}");
            SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: Moving album files to {failedAlbumPath}");
        }

        foreach (var af in folder.Files)
        {
            if (string.IsNullOrEmpty(af.DownloadPath) || !File.Exists(af.DownloadPath)) continue;
            try
            {
                if (deleteDownloaded || af.DownloadPath.EndsWith(".incomplete"))
                {
                    File.Delete(af.DownloadPath);
                }
                else if (!string.IsNullOrEmpty(failedAlbumPath))
                {
                    var relativeBase = outputParentDir
                        ?? throw new InvalidOperationException("Cannot move failed album files because Output.ParentDir is not set.");
                    var newPath = Path.Join(failedAlbumPath, Path.GetRelativePath(relativeBase, af.DownloadPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Utils.Move(af.DownloadPath, newPath);
                }

                var downloadParent = Path.GetDirectoryName(af.DownloadPath);
                if (!string.IsNullOrEmpty(downloadParent) && !string.IsNullOrEmpty(outputParentDir))
                    Utils.DeleteAncestorsIfEmpty(downloadParent, outputParentDir);
            }
            catch (Exception e)
            {
                SockseekLog.Jobs.Error($"Error: Unable to move or delete file '{af.DownloadPath}' after album fail: {e}");
            }
        }

        if (deleteDownloaded)
            Events.RaiseJobStatus(job, "deleted files");
        else if (!string.IsNullOrEmpty(failedAlbumPath))
            Events.RaiseJobStatus(job, $"moved to {failedAlbumPath}");
    }


    // ── folder retrieval ──────────────────────────────────────────────────────

    public async Task<RetrieveFolderJob> ProcessFolderRetrieval(
        AlbumFolder folder,
        Job parentJob,
        string? customMessage = null,
        bool consumeJobSlot = true)
    {
        if (folder.IsFullyRetrieved)
        {
            SockseekLog.Jobs.Debug($"[{parentJob.DisplayId}] {JobLogKind(parentJob)}: folder already fully retrieved: {folder.FolderPath}");
            var completedJob = new RetrieveFolderJob(folder)
            {
                WorkflowId = parentJob.WorkflowId,
                Config = parentJob.Config,
                RetrievalOutcome = FolderRetrievalOutcome.Completed,
            };
            return completedJob;
        }

        var rfJob = new RetrieveFolderJob(folder) { WorkflowId = parentJob.WorkflowId, Config = parentJob.Config };
        rfJob.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, parentJob.Cts!.Token);
        RegisterJob(rfJob, parentJob);
        rfJob.UpdateState(JobState.Searching);
        SockseekLog.Jobs.Debug($"[{rfJob.DisplayId}] RetrieveFolderJob: retrieving folder for parent [{parentJob.DisplayId}] {JobLogKind(parentJob)}: {folder.FolderPath}");

        int count = 0;
        try
        {
            async Task<int> CompleteFolder()
            {
                rfJob.UpdateState(JobState.Searching);
                return await searcher!.CompleteFolder(rfJob.TargetFolder, rfJob.Cts.Token);
            }

            count = consumeJobSlot
                ? await WithJobSlot(rfJob.Cts.Token, CompleteFolder)
                : await CompleteFolder();
            rfJob.NewFilesFoundCount = count;
            rfJob.RetrievalOutcome = FolderRetrievalOutcome.Completed;
            rfJob.SetDone();
            SockseekLog.Jobs.Debug($"[{rfJob.DisplayId}] RetrieveFolderJob: retrieved folder with {count} new file{(count == 1 ? "" : "s")}: {folder.FolderPath}");
            return rfJob;
        }
        catch (OperationCanceledException)
        {
            // Suppress upward exception so cancelling this retrieval job doesn't cancel its parent.
            rfJob.RetrievalOutcome = FolderRetrievalOutcome.Cancelled;
            rfJob.Fail(FailureReason.Cancelled);
            Events.RaiseJobStatus(rfJob, "cancelled");
            SockseekLog.Jobs.Info($"[{rfJob.DisplayId}] RetrieveFolderJob: Cancelled folder retrieval for {folder.FolderPath}");
            return rfJob;
        }
        finally
        {
            rfJob.Discovery = new DiscoverySummary { ResultCount = count, LockedFileCount = 0 };
            Events.RaiseJobExecutionCompleted(rfJob);
        }
    }


    // ── album art download ────────────────────────────────────────────────────

    async Task<List<SongJob>> DownloadImages(AlbumJob job, JobContext ctx, FileManager fileManager, AlbumFolder? chosenFolder)
    {
        var result = new List<SongJob>();
        var config = job.Config;
        long mSize = 0;
        int mCount = 0;
        var option = config.Output.AlbumArtOption;

        if (chosenFolder != null)
        {
            string dir = chosenFolder.FolderPath;
            fileManager.SetDefaultFolderName(Path.GetFileName(Utils.NormalizedPath(dir)));
        }

        if (option == AlbumArtOption.Default) return result;

        int[]? sortedLengths = null;
        if (chosenFolder?.Files.Any(af => !af.IsNotAudio) == true)
            sortedLengths = chosenFolder.Files.Where(af => !af.IsNotAudio)
                .Select(af => af.Query.Length).OrderBy(x => x).ToArray();

        var imageFolders = job.Results
            .Where(f => chosenFolder == null || Searcher.AlbumsAreSimilar(chosenFolder, f, sortedLengths))
            .Select(f => f.Files.Where(af => Utils.IsImageFile(af.ResolvedTarget!.Filename)).ToList())
            .Where(ls => ls.Count > 0)
            .ToList();

        if (imageFolders.Count == 0)
        { SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: no images found: {job}"); return result; }

        if (imageFolders.Count == 1 && imageFolders[0].All(af => af.State != JobState.Pending))
        { SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: no additional images found: {job}"); return result; }

        if (option == AlbumArtOption.Largest)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Max(af => af.ResolvedTarget!.File.Size) / 1024 / 100)
                .ThenByDescending(ls => ls[0].ResolvedTarget!.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.ResolvedTarget!.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mSize = chosenFolder.Files
                    .Where(af => af.State == JobState.Done && Utils.IsImageFile(af.DownloadPath ?? ""))
                    .Select(af => af.ResolvedTarget!.File.Size)
                    .DefaultIfEmpty(0).Max();
        }
        else if (option == AlbumArtOption.Most)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Count)
                .ThenByDescending(ls => ls[0].ResolvedTarget!.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.ResolvedTarget!.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mCount = chosenFolder.Files.Count(af => af.State == JobState.Done && Utils.IsImageFile(af.DownloadPath ?? ""));
        }

        bool needsDownload(List<SongJob> ls) => option == AlbumArtOption.Most
            ? mCount < ls.Count
            : option == AlbumArtOption.Largest
                ? mSize < ls.Max(af => af.ResolvedTarget!.File.Size) - 1024 * 50
                : true;

        while (imageFolders.Count > 0)
        {
            var imgs = imageFolders[0];
            imageFolders.RemoveAt(0);

            if (imgs.All(af => af.State == JobState.Done || af.State == JobState.AlreadyExists)
                || !needsDownload(imgs))
            {
                var imageFolderPath = Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename));
                SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: image requirements already satisfied: {imageFolderPath}");
                return result;
            }

            var syntheticFolder = new AlbumFolder(
                imgs[0].ResolvedTarget!.Response.Username,
                Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename)),
                imgs);

            fileManager.downloadingAdditionalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename)));

            bool allSucceeded = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            foreach (var af in imgs)
            {
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await DownloadEmbeddedSong(af, job, config, fileManager, cts, cancelGroupOnFail: false, organize: true);
                if (af.State == JobState.Done)
                    result.Add(af);
                else
                    allSucceeded = false;
            }

            if (allSucceeded) break;
        }

        return result;
    }

    async Task<JobOutcome> DownloadEmbeddedSong(
        SongJob song,
        Job parentJob,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource groupCts,
        bool cancelGroupOnFail,
        bool organize)
    {
        if (song.State != JobState.Pending) return JobOutcome.NoChange();

        song.WorkflowId = parentJob.WorkflowId;
        song.Config = config;
        song.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, groupCts.Token);
        RegisterJob(song, parentJob);

        JobOutcome outcome = JobOutcome.NoChange();
        try
        {
            outcome = await DownloadSong(song, parentJob, config, organizer, song.Cts);
            CommitOutcome(song, outcome);

            if (outcome.FailureReason == FailureReason.Cancelled && !groupCts.IsCancellationRequested)
                return outcome;

            if (outcome.State == JobState.Failed && cancelGroupOnFail)
            {
                groupCts.Cancel();
                throw new OperationCanceledException();
            }

            if (outcome.FailureReason != FailureReason.Cancelled)
                await CompleteSongAfterOutcome(song, parentJob, Ctx(parentJob), organizer, organize);

            return outcome;
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested
            && song.Cts.IsCancellationRequested
            && song.FailureReason == FailureReason.Cancelled)
        {
            // User cancelled only this embedded song; keep the album/aggregate parent running.
            return JobOutcome.Failed(FailureReason.Cancelled);
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested && cancelGroupOnFail)
        {
            groupCts.Cancel();
            throw;
        }
        finally
        {
            Events.RaiseJobExecutionCompleted(song);
        }
    }

    static string DescribeExtractedResult(Job result, int songCount)
    {
        var resultKind = result switch
        {
            JobList list => $"{list.Jobs.Count} jobs",
            SongJob => "1 song",
            AlbumJob => "album",
            AlbumAggregateJob => "album aggregate",
            SearchJob => "search",
            RetrieveFolderJob => "folder retrieval",
            ExtractJob => "extract job",
            _ => result.GetType().Name,
        };

        return songCount > 0
            ? $"{resultKind}, {songCount} songs"
            : resultKind;
    }


    // ── update / stale-detection loop ─────────────────────────────────────────

    // TODO: Replace this while-true polling loop with a PeriodicTimer or scheduled callbacks.
    // Iterating over every active download every 100ms to check for stale states burns CPU cycles.
    // Stale detection should ideally be handled by CancellationTokens with timeouts (e.g., CancelAfter)
    // attached directly to the network streams, or by using a System.Threading.PeriodicTimer for
    // better async hygiene and less GC pressure.
    async Task UpdateLoop(CancellationToken cancellationToken)
    {
        while (!appCts.IsCancellationRequested)
        {
            try
            {
                if (_clientManager.IsConnectedAndLoggedIn)
                {
                    // Prune completed searches (or those without a handler task)
                    foreach (var (song, info) in _registry.Searches)
                        if (info.Task == null || info.Task.IsCompleted)
                            _registry.Searches.TryRemove(song, out _);

                    // Check for stale downloads
                    foreach (var (filename, ad) in _registry.Downloads)
                    {
                        if (ad == null) { _registry.Downloads.TryRemove(filename, out _); continue; }

                        var song = ad.Song;
                        var songJob = ad.Song as SongJob;
                        int maxStale = ad.Song.FileSize > 0 ? (songJob?.Config?.Search.MaxStaleTime ?? 30_000) : int.MaxValue;
                        if (song.LastActivityTime.HasValue &&
                            (DateTime.Now - song.LastActivityTime.Value).TotalMilliseconds > maxStale)
                        {
                            SockseekLog.Jobs.Debug($"Cancelling stale download: {song}");
                            try { ad.Cts.Cancel(); } catch { }
                            _registry.Downloads.TryRemove(filename, out _);
                        }
                    }
                }

                await Task.Delay(updateInterval, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SockseekLog.Jobs.Error(ex, "Error in update loop");
                try { await Task.Delay(1000, cancellationToken); } catch { break; }
            }
        }
    }
}
