using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
    private readonly ConcurrentDictionary<Guid, AutoProfileWorkflowLogState> _autoProfileLogStateByWorkflow = new();
    private readonly ConcurrentDictionary<Guid, byte> _musicDirectoryIndexBuildLoggedByWorkflow = new();
    private readonly SourceMutationExecutor _sourceMutationExecutor = new();

    private readonly ConcurrentDictionary<Guid, Job> _jobById = new();
    private readonly ConcurrentDictionary<int, Job> _jobByDisplayId = new();

    public Job? GetJob(Guid id) => _jobById.TryGetValue(id, out var job) ? job : null;
    public Job? GetJob(int displayId) => _jobByDisplayId.TryGetValue(displayId, out var job) ? job : null;
    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => _jobById.Values
        .Where(job => job.WorkflowId == workflowId)
        .OrderBy(job => job.DisplayId)
        .ToList();

    private sealed class AutoProfileWorkflowLogState
    {
        public object Gate { get; } = new();
        public HashSet<Guid> CountedJobIds { get; } = [];
        public HashSet<string> ObservedProfileNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, int>> CountsByProfileAndKind { get; } = new(StringComparer.Ordinal);
        public bool FinalSummaryEmitted { get; set; }
    }

    public bool TryNextCandidate(Guid jobId)
    {
        var job = GetJob(jobId);
        if (job == null) return false;

        var targetIds = ActiveDownloadTargetIds(job);
        var activeDownloads = _registry.Downloads.Values
            .Where(d => targetIds.Contains(d.Song.Id))
            .ToList();

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

    // TODO [ARCHITECTURE]: Keep parent-job command targeting centralized here (or move it
    // behind a shared command-target resolver) so local and remote skip/try-next/cancel
    // commands resolve active descendants with identical rules.
    private static HashSet<Guid> ActiveDownloadTargetIds(Job job)
    {
        var ids = new HashSet<Guid>();
        Add(job);
        return ids;

        void Add(Job current)
        {
            ids.Add(current.Id);

            switch (current)
            {
                case JobList list:
                    foreach (var child in list.Jobs)
                        Add(child);
                    break;

                case ExtractJob { Result: { } result }:
                    Add(result);
                    break;

                case AlbumJob album:
                    foreach (var song in album.TrackJobs)
                        Add(song);
                    break;

                case AggregateJob aggregate:
                    foreach (var song in aggregate.Songs)
                        Add(song);
                    break;

                case AlbumAggregateJob aggregate:
                    foreach (var album in aggregate.Albums)
                        Add(album);
                    break;
            }
        }
    }

    private JobContext Ctx(Job job) => _contexts[job.Id];

    private void RegisterJob(Job job, Job? parent)
    {
        job.EnsureDisplayId();
        bool firstRegistration = _jobById.TryAdd(job.Id, job);
        _jobByDisplayId[job.DisplayId] = job;

        if (!firstRegistration)
            return;

        job.StateChanged += (_, transition) =>
        {
            Events.RaiseJobStateChanged(job);
            if (transition.ActivityChanged
                && transition.After.LifecycleState == JobLifecycleState.Running
                && transition.After.ActivityPhase != JobActivityPhase.None)
            {
                Events.RaiseJobActivityChanged(job, job.ActivityPhase, job.ActivityUntilUtc);
            }
        };
        Events.RaiseJobRegistered(job, parent);
    }

    private void ObservePreparedAutoProfiles(Job preparedRoot)
    {
        // Auto-profile logs are edge-triggered per workflow: announce profile names the
        // first time they appear on a real job, then leave detailed counts to debug logs.
        var state = _autoProfileLogStateByWorkflow.GetOrAdd(
            preparedRoot.WorkflowId,
            _ => new AutoProfileWorkflowLogState());
        var newlyObservedProfiles = new List<string>();
        Job? firstTriggeringJob = null;

        lock (state.Gate)
        {
            foreach (var job in EnumerateAutoProfileLogJobs(preparedRoot))
            {
                var kindLabel = AutoProfileLogKind(job);
                if (kindLabel == null)
                    continue;

                var profiles = job.Config?.AppliedAutoProfiles
                    .Where(profile => !string.IsNullOrWhiteSpace(profile))
                    .OrderBy(profile => profile, StringComparer.Ordinal)
                    .ToList();
                if (profiles == null || profiles.Count == 0)
                    continue;

                if (!state.CountedJobIds.Add(job.Id))
                    continue;

                foreach (var profile in profiles)
                {
                    if (state.ObservedProfileNames.Add(profile))
                    {
                        newlyObservedProfiles.Add(profile);
                        firstTriggeringJob ??= job;
                    }

                    if (!state.CountsByProfileAndKind.TryGetValue(profile, out var countsByKind))
                    {
                        countsByKind = new Dictionary<string, int>(StringComparer.Ordinal);
                        state.CountsByProfileAndKind[profile] = countsByKind;
                    }

                    countsByKind[kindLabel] = countsByKind.GetValueOrDefault(kindLabel) + 1;
                }
            }
        }

        if (newlyObservedProfiles.Count > 0)
        {
            var message = $"Auto profiles active: {string.Join(", ", newlyObservedProfiles)}";
            Events.RaiseWorkflowMessage(
                preparedRoot.WorkflowId,
                LogLevel.Information,
                null,
                message);

            if (firstTriggeringJob != null)
                Events.RaiseJobMessage(firstTriggeringJob, LogLevel.Debug, null, message);
        }
    }

    private void EmitAutoProfileFinalSummary(Job rootJob)
    {
        if (!_autoProfileLogStateByWorkflow.TryGetValue(rootJob.WorkflowId, out var state))
            return;

        string? summary;
        lock (state.Gate)
        {
            if (state.FinalSummaryEmitted || state.CountsByProfileAndKind.Count == 0)
                return;

            state.FinalSummaryEmitted = true;
            summary = FormatAutoProfileSummary(state.CountsByProfileAndKind);
        }

        if (!string.IsNullOrWhiteSpace(summary))
            Events.RaiseWorkflowMessage(rootJob.WorkflowId, LogLevel.Debug, null, $"Auto profiles applied: {summary}");
    }

    private void RaiseBuildingMusicDirectoryIndex(Job job)
    {
        if (_musicDirectoryIndexBuildLoggedByWorkflow.TryAdd(job.WorkflowId, 0))
            Events.RaiseWorkflowMessage(job.WorkflowId, LogLevel.Information, null, "Building music directory index..");
    }

    private static IEnumerable<Job> EnumerateAutoProfileLogJobs(Job root)
    {
        yield return root;

        switch (root)
        {
            case JobList list:
                foreach (var child in list.Jobs)
                {
                    foreach (var descendant in EnumerateAutoProfileLogJobs(child))
                        yield return descendant;
                }
                break;

            case ExtractJob { Result: { } result }:
                foreach (var descendant in EnumerateAutoProfileLogJobs(result))
                    yield return descendant;
                break;
        }
    }

    private static string? AutoProfileLogKind(Job job) => job switch
    {
        SongJob => "song",
        AlbumJob => "album",
        AggregateJob => "aggregate",
        AlbumAggregateJob => "album aggregate",
        SearchJob => "search",
        _ => null,
    };

    private static string FormatAutoProfileSummary(Dictionary<string, Dictionary<string, int>> countsByProfileAndKind)
        => string.Join(", ",
            countsByProfileAndKind
                .OrderBy(profile => profile.Key, StringComparer.Ordinal)
                .Select(profile =>
                    $"{profile.Key} ({string.Join(", ", profile.Value.OrderBy(kind => kind.Key, StringComparer.Ordinal).Select(FormatAutoProfileKindCount))})"));

    private static string FormatAutoProfileKindCount(KeyValuePair<string, int> count)
        => $"{count.Value} {PluralizeAutoProfileKind(count.Key, count.Value)}";

    private static string PluralizeAutoProfileKind(string kind, int count)
        => count == 1
            ? kind
            : kind switch
            {
                "album aggregate" => "album aggregates",
                "search" => "searches",
                _ => $"{kind}s",
            };

    // ── public state (read by Searcher / Downloader) ─────────────────────────

    public ISoulseekClient? Client => _clientManager.Client;
    public SoulseekClientStates ClientState => _clientManager.State;
    public bool IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;

    // Session state
    private readonly SessionRegistry _registry = new();
    private readonly OutputFinalizer _outputFinalizer;
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
    public void Enqueue(Job job, DownloadSettings settings)
    {
        job.EnsureDisplayId();
        _jobChannel.Writer.TryWrite(QueuedEngineJob.Root(job, settings));
    }

    /// <summary>Resumes an existing job without re-parenting it or replacing its prepared context.</summary>
    public void Resume(Job job)
    {
        job.EnsureDisplayId();
        _jobChannel.Writer.TryWrite(QueuedEngineJob.Resume(job));
    }

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

        if (sourceJob is AlbumAggregateJob aggregateJob && aggregateJob.IsAwaitingSelection)
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
        if (job == null || !job.IsAwaitingSelection || job.Config == null)
            return false;

        if (job is AlbumAggregateJob aggregateJob)
        {
            _closedManualAggregateSelections.TryAdd(aggregateJob.Id, 0);
            await TryFinalizeClosedManualAggregateSelectionAsync(aggregateJob);
            return true;
        }

        job.Fail(JobFailureReason.NoMatchingResults);
        await FlushManualSelectionTerminalEffectsAsync(job);
        return true;
    }

    /// <summary>Marks an AwaitingSelection job as explicitly skipped by the user.</summary>
    public async Task<bool> SkipManualSelectionAsync(Guid jobId)
    {
        var job = GetJob(jobId);
        if (job == null || !job.IsAwaitingSelection || job.Config == null)
            return false;

        job.SetSkipped(JobSkipReason.Manual);
        await FlushManualSelectionTerminalEffectsAsync(job);
        return true;
    }

    /// <summary>Signals that no more jobs will be enqueued. <see cref="RunAsync"/> will drain and exit.</summary>
    public void CompleteEnqueue() => _jobChannel.Writer.Complete();

    // ── cancellation ─────────────────────────────────────────────────────────

    private readonly CancellationTokenSource appCts = new();
    public void Cancel()
    {
        foreach (var job in _jobById.Values)
            if (!job.IsTerminal)
                job.MarkCancellationSource(JobCancellationSource.UserRequestedAllJobs);

        appCts.Cancel();
    }

    public int CancelWorkflow(Guid workflowId)
    {
        var jobs = GetJobsByWorkflow(workflowId);
        int cancelled = 0;

        foreach (var job in jobs)
        {
            var cts = job.Cts;
            if (cts == null || cts.IsCancellationRequested)
                continue;

            job.Cancel(JobCancellationSource.UserRequestedWorkflow);
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
        _outputFinalizer = new OutputFinalizer(_registry);
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
        albumJob.ResetToPending();
        Resume(albumJob);
    }

    private static bool CanStartManualAlbumSelection(AlbumJob albumJob)
        => albumJob.DownloadBehavior == DownloadBehavior.Manual
            && (albumJob.IsAwaitingSelection || albumJob.IsUnsuccessfulTerminal);

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

        ObservePreparedAutoProfiles(albumJob);
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

                ObservePreparedAutoProfiles(rootJob);
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
                catch (SoulseekConnectionUnavailableException) when (_clientManager.HasFatalError)
                {
                    throw;
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

            rootTasks.Add(ProcessRootJob(rootJob));
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

    async Task ProcessRootJob(Job rootJob)
    {
        try
        {
            await ProcessJob(rootJob);
        }
        finally
        {
            EmitAutoProfileFinalSummary(rootJob);
        }
    }

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
                var extractResult = await ProcessExtractJob(ej, parentJob, parentToken);
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
                await ProcessJobList(jl, parentToken);
                return;
            }

            // ── Leaf jobs: skip checks, search, download ─────────────────────────
            await ProcessLeafJob(job, parentToken);
        }
        catch (OperationCanceledException) when (IsJobCancellationRequested(job, parentToken))
        {
            MarkCancelledIfActive(job, CancellationSourceFor(job, parentToken));
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

    bool IsJobCancellationRequested(Job job, CancellationToken parentToken)
        => appCts.IsCancellationRequested
            || parentToken.IsCancellationRequested
            || job.Cts?.IsCancellationRequested == true;

    JobCancellationSource CancellationSourceFor(Job job, CancellationToken parentToken)
    {
        if (job.CancellationSource != JobCancellationSource.None)
            return job.CancellationSource;
        if (appCts.IsCancellationRequested)
            return JobCancellationSource.UserRequestedAllJobs;
        if (parentToken.IsCancellationRequested)
            return JobCancellationSource.ParentJob;

        return JobCancellationSource.InternalEngine;
    }

    static JobCancellationSource CancellationSourceForDerivedCancellation(Job job, params Job?[] relatedJobs)
    {
        if (job.CancellationSource != JobCancellationSource.None)
            return job.CancellationSource;

        foreach (var relatedJob in relatedJobs)
        {
            var source = CancellationSourceFromSubtree(relatedJob);
            if (source != JobCancellationSource.None)
                return source;
        }

        return JobCancellationSource.InternalEngine;
    }

    static JobCancellationSource CancellationSourceFromSubtree(Job? job)
    {
        if (job == null)
            return JobCancellationSource.None;
        if (job.CancellationSource != JobCancellationSource.None)
            return job.CancellationSource;

        return job switch
        {
            JobList list => list.Jobs.Select(CancellationSourceFromSubtree).FirstOrDefault(source => source != JobCancellationSource.None),
            AlbumJob album => album.TrackJobs.Select(CancellationSourceFromSubtree).FirstOrDefault(source => source != JobCancellationSource.None),
            AggregateJob aggregate => aggregate.Songs.Select(CancellationSourceFromSubtree).FirstOrDefault(source => source != JobCancellationSource.None),
            AlbumAggregateJob aggregate => aggregate.Albums.Select(CancellationSourceFromSubtree).FirstOrDefault(source => source != JobCancellationSource.None),
            ExtractJob extract => CancellationSourceFromSubtree(extract.Result),
            _ => JobCancellationSource.None,
        };
    }

    static void MarkCancelledIfActive(Job job, JobCancellationSource source)
    {
        if (!job.IsTerminal)
        {
            CommitOutcome(job, JobOutcome.Cancelled(source));
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

    async Task ProcessJobList(JobList jl, CancellationToken parentToken)
    {
        var ctx = _contexts.TryGetValue(jl.Id, out var c) ? c : null;
        var config = jl.Config!;
        jl.UpdateActivity(JobActivityPhase.RunningChildren);

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
                foreach (var song in directSongs.Where(s => s.LifecycleState == JobLifecycleState.Pending))
                    if (TrySetAlreadyExists(jl, song, TrackSkipperContext.From(ctx, config.Skip, config.Search)))
                        existing.Add(song);

            Events.RaiseTrackBatchResolved(jl,
                directSongs.Where(s => s.LifecycleState == JobLifecycleState.Pending).ToList(),
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
            // TODO [PERFORMANCE]: Split bulk child registration/materialization from child execution.
            // Today each ProcessJob(child) runs synchronously until its first incomplete await, so a
            // large JobList can start skip/search/failure work for early children before later children
            // are even registered. That makes "workflow registration" cost include real processing.
            // Register/materialize all children in one cheap pass, then schedule execution separately.
            if (directSongs.Count > 0)
            {
                var intervalReporter = engineSettings.ReportIntervalProgress
                    ? new IntervalProgressReporter(TimeSpan.FromSeconds(30), 5, directSongs)
                    : null;

                await Task.WhenAll(jl.Jobs.ToList().Select(async child =>
                {
                    bool wasInitial = child is SongJob s && s.LifecycleState == JobLifecycleState.Pending;
                    await ProcessJob(child, jl.Cts!.Token, jl);

                    if (wasInitial && child is SongJob song)
                    {
                        ctx?.IndexEditor?.Update();
                        ctx?.PlaylistEditor?.Update();
                        intervalReporter?.MaybeReport(song);
                        int dl = directSongs.Count(IsSubtreeSuccessful);
                        int fl = directSongs.Count(IsSubtreeUnsuccessful);
                        Events.RaiseOverallProgress(dl, fl, directSongs.Count);

                        await MaybeRemoveFromSource(song, config);
                    }
                }));

                int dlFinal = directSongs.Count(IsSubtreeSuccessful);
                int flFinal = directSongs.Count(IsSubtreeUnsuccessful);
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

        SetJobListTerminalState(jl, parentToken);
    }

    static bool IsSubtreeSuccessful(Job? job)
    {
        if (job == null) return false;

        return job switch
        {
            JobList jl => jl.Jobs.All(IsSubtreeSuccessful),
            ExtractJob ej => ej.TerminalOutcome == JobTerminalOutcome.Succeeded && ej.Result != null && IsSubtreeSuccessful(ej.Result),
            AlbumAggregateJob aag => aag.Albums.Count > 0 && aag.Albums.All(IsSubtreeSuccessful),
            AggregateJob ag => ag.Songs.Count > 0 && ag.Songs.All(IsSubtreeSuccessful),
            _ => IsSuccessfulTerminal(job),
        };
    }

    async Task TryFinalizeClosedManualAggregateSelectionAsync(AlbumAggregateJob aggregateJob)
    {
        if (!_closedManualAggregateSelections.ContainsKey(aggregateJob.Id))
            return;

        if (aggregateJob.LifecycleState == JobLifecycleState.Terminal)
            return;

        var selectedAlbums = _manualAggregateParentByAlbumId
            .Where(pair => pair.Value == aggregateJob.Id)
            .Select(pair => GetJob(pair.Key))
            .OfType<AlbumJob>()
            .ToList();

        if (selectedAlbums.Count == 0)
        {
            aggregateJob.Fail(JobFailureReason.NoMatchingResults);
            await FlushManualSelectionTerminalEffectsAsync(aggregateJob);
            return;
        }

        if (selectedAlbums.Any(IsActiveManualSelectionChild))
            return;

        if (selectedAlbums.All(IsSuccessfulTerminal))
            aggregateJob.SetDone();
        else
            aggregateJob.Fail(JobFailureReason.NoMatchingResults);

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

    static bool IsActiveManualSelectionChild(Job job)
        => job.LifecycleState != JobLifecycleState.Terminal;

    void SetJobListTerminalState(JobList jobList, CancellationToken parentToken)
    {
        bool anySuccessful = jobList.Jobs.Any(IsSubtreeSuccessful);
        bool anyCancelled = jobList.Jobs.Any(HasCancelledDescendant);
        bool anyUnsuccessful = jobList.Jobs.Any(IsSubtreeUnsuccessful);

        if (anySuccessful && (anyCancelled || anyUnsuccessful))
        {
            var source = anyCancelled
                ? CancellationSourceForDerivedCancellation(jobList, jobList)
                : JobCancellationSource.None;
            CommitOutcome(jobList, JobOutcome.PartialSuccess(
                "Some child jobs completed and some failed or were cancelled.",
                source));
            return;
        }

        if (jobList.Cts?.IsCancellationRequested == true || anyCancelled)
        {
            var source = jobList.Cts?.IsCancellationRequested == true
                ? CancellationSourceFor(jobList, parentToken)
                : CancellationSourceForDerivedCancellation(jobList, jobList);
            CommitOutcome(jobList, JobOutcome.Cancelled(source));
            return;
        }

        if (anyUnsuccessful)
        {
            CommitOutcome(jobList, JobOutcome.Failed(JobFailureReason.ChildJobsFailed, "One or more child jobs failed."));
            return;
        }

        CommitOutcome(jobList, JobOutcome.Done());
    }

    static bool IsSubtreeUnsuccessful(Job job)
    {
        if (job.TerminalOutcome is JobTerminalOutcome.Failed
            or JobTerminalOutcome.PartialSuccess
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped
                && job.SkipReason is not JobSkipReason.AlreadyExists and not JobSkipReason.Manual))
            return true;

        return job switch
        {
            JobList list => list.Jobs.Any(IsSubtreeUnsuccessful),
            AlbumJob album => album.TrackJobs.Any(IsSubtreeUnsuccessful),
            AggregateJob aggregate => aggregate.Songs.Any(IsSubtreeUnsuccessful),
            AlbumAggregateJob aggregate => aggregate.Albums.Any(IsSubtreeUnsuccessful),
            ExtractJob extract => extract.Result != null && IsSubtreeUnsuccessful(extract.Result),
            _ => false,
        };
    }

    static bool IsSuccessfulTerminal(Job job)
        => job.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason == JobSkipReason.AlreadyExists);

    static bool HasCancelledDescendant(Job job)
    {
        if (job.FailureReason == JobFailureReason.Cancelled)
            return true;

        return job switch
        {
            JobList list => list.Jobs.Any(HasCancelledDescendant),
            AlbumJob album => album.TrackJobs.Any(song => song.FailureReason == JobFailureReason.Cancelled),
            AggregateJob aggregate => aggregate.Songs.Any(song => song.FailureReason == JobFailureReason.Cancelled),
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

    async Task<JobOutcome> ProcessLeafJob(Job job, CancellationToken parentToken)
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
            if (job is SongJob existingSong)
            {
                var organizer = new FileManager(existingSong, config.Output, config.Extraction);
                await CommitAndFinalizeSong(
                    existingSong,
                    existingSong,
                    alreadyExistsOutcome,
                    ctx,
                    organizer,
                    organize: false,
                    updateIndexes: false);
            }
            else
            {
                ApplyPreCommitOutcomeMetadata(job, alreadyExistsOutcome);
                var postProcessOutcome = OutcomeWithCurrentMetadata(job, alreadyExistsOutcome);
                await RunOnCompleteIfApplicable(job, null, ctx, postProcessOutcome);
                CommitOutcome(job, OutcomeWithCurrentMetadata(job, postProcessOutcome));
            }

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
            return await WithJobSlot(job.Cts!.Token, () => ProcessLeafJobCore(job, ctx, parentToken));
        else
            return await ProcessLeafJobCore(job, ctx, parentToken);
    }

    async Task<JobOutcome> ProcessLeafJobCore(Job job, JobContext ctx, CancellationToken parentToken)
    {
        var config = job.Config;

        if (job is SearchJob searchJob)
            return await ProcessSearchJob(searchJob, parentToken);

        if (job is RetrieveFolderJob retrieveFolderJob)
            return await ProcessRetrieveFolderJob(retrieveFolderJob, parentToken);

        if (job is SongJob songJob)
        {
            if (await ProcessSongDiscovery(songJob, parentToken) is { } outcome)
                return outcome;
        }

        if (job is AlbumJob albumJob)
        {
            if (await ProcessAlbumDiscovery(albumJob, ctx, parentToken) is { } outcome)
                return outcome;
        }

        if (job is AggregateJob aggregateJob)
        {
            if (await ProcessAggregateDiscovery(aggregateJob, ctx, parentToken) is { } outcome)
                return outcome;
        }

        if (job is AlbumAggregateJob albumAggregateJob)
            return await ProcessAlbumAggregateDiscovery(albumAggregateJob, ctx, parentToken);

        if (config.PrintResults)
        {
            return JobOutcome.NoChange();
        }

        return await ProcessLeafDownload(job, ctx, parentToken);
    }

    async Task<ExtractJobResult> ProcessExtractJob(ExtractJob job, Job? parentJob, CancellationToken parentToken)
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
                job.UpdateActivity(JobActivityPhase.Extracting);
                job.Cts.Token.ThrowIfCancellationRequested();
                var extraction = EffectiveExtractionSettings(job);
                extracted = await extractor.GetTracks(job.Input, extraction, ExtractorContext.ForExtractJob(job, Events, ExtractorLogSource(inputType)));
                job.Cts.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                _extractorSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new(JobOutcome.Cancelled(CancellationSourceFor(job, parentToken)), null, extractor);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(ExtractionFailedOutcome(e), null, extractor);
        }

        var effectiveExtraction = EffectiveExtractionSettings(job);
        extracted = ApplyExtractedResultTransforms(job, extracted, effectiveExtraction.UpgradeToAlbum);
        PublishExtractedResult(job, extracted, parentJob);
        return new(JobOutcome.Done(), extracted, extractor);
    }

    static ExtractionSettings EffectiveExtractionSettings(ExtractJob job)
    {
        if (job.RequestedModeOverride == null)
            return job.Config.Extraction;

        var extraction = SettingsCloner.Clone(job.Config.Extraction);
        extraction.RequestedMode = job.RequestedModeOverride;
        return extraction;
    }

    Job ApplyExtractedResultTransforms(ExtractJob job, Job extracted, bool forceAlbumUpgrade)
    {
        job.Result = extracted;

        if (extracted is IUpgradeable upgradeable)
        {
            var upgraded = upgradeable.Upgrade(forceAlbumUpgrade, job.Config.Search.IsAggregate).ToList();

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

        EnsureDisplayIdsForExecutableSubtree(extracted);
        ObservePreparedAutoProfiles(extracted);
        Events.RaiseJobResultCreated(job, extracted);
    }

    static void EnsureDisplayIdsForExecutableSubtree(Job job)
    {
        job.EnsureDisplayId();

        switch (job)
        {
            case ExtractJob { Result: { } result }:
                EnsureDisplayIdsForExecutableSubtree(result);
                break;

            case JobList list:
                foreach (var child in list.Jobs)
                    EnsureDisplayIdsForExecutableSubtree(child);
                break;

            case AggregateJob aggregate:
                foreach (var song in aggregate.Songs)
                    EnsureDisplayIdsForExecutableSubtree(song);
                break;

            case AlbumAggregateJob aggregate:
                foreach (var album in aggregate.Albums)
                    EnsureDisplayIdsForExecutableSubtree(album);
                break;

            case AlbumJob album:
                foreach (var song in album.TrackJobs)
                    EnsureDisplayIdsForExecutableSubtree(song);
                break;
        }
    }

    sealed record ExtractJobResult(JobOutcome Outcome, Job? Result, IExtractor? Extractor);

    static JobOutcome ExtractionFailedOutcome(Exception exception)
        => ExceptionFailureOutcome(JobFailureReason.ExtractionFailed, exception);

    static string ExtractorLogSource(InputType inputType)
        => inputType.ToString();

    static JobOutcome ExceptionFailureOutcome(JobFailureReason reason, Exception exception)
        => JobOutcome.Failed(
            reason,
            SockseekLog.ExceptionSummary(exception),
            SockseekLog.ExceptionDetail(exception));

    static JobOutcome NoMatchingDiscoveryOutcome(
        ResponseData responseData,
        string rawResultSingular,
        string rawResultPlural,
        string candidatePlural)
    {
        if (responseData.resultCount <= 0)
            return JobOutcome.Failed(JobFailureReason.NoSearchResults, $"No Soulseek {rawResultPlural} found.");

        var rawResultNoun = responseData.resultCount == 1 ? rawResultSingular : rawResultPlural;
        return JobOutcome.Failed(
            JobFailureReason.NoMatchingResults,
            $"{responseData.resultCount} Soulseek {rawResultNoun} found, but no matching {candidatePlural} satisfied the required conditions.");
    }

    static JobOutcome NoMatchingCandidatesOutcome()
        => JobOutcome.Failed(JobFailureReason.NoMatchingResults);

    static bool IsNotFoundFailure(JobFailureReason reason)
        => reason is JobFailureReason.NoSearchResults or JobFailureReason.NoMatchingResults;

    async Task<JobOutcome> ProcessSearchJob(SearchJob job, CancellationToken parentToken)
    {
        var responseData = new ResponseData();
        var (outcome, searchFailure) = await TrySearchWithReconnect(job, parentToken,
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

    async Task<JobOutcome?> TrySearchWithReconnect(Job job, CancellationToken parentToken, Func<Task> searchAction)
    {
        var (_, outcome) = await TrySearchWithReconnect(job, parentToken, async () =>
        {
            await searchAction();
            return true;
        });
        return outcome;
    }

    async Task<(T Result, JobOutcome? Failure)> TrySearchWithReconnect<T>(Job job, CancellationToken parentToken, Func<Task<T>> searchAction)
    {
        try
        {
            var result = await RunSearchWithReconnect(job.Cts!.Token, searchAction);
            return (result, null);
        }
        catch (OperationCanceledException)
        {
            var outcome = JobOutcome.Cancelled(CancellationSourceFor(job, parentToken));
            CommitOutcome(job, outcome);
            return (default!, outcome);
        }
        catch (Exception e)
        {
            if (job is SearchJob searchJob)
                searchJob.Session.Complete();

            var outcome = ExceptionFailureOutcome(JobFailureReason.Other, e);
            CommitOutcome(job, outcome);
            return (default!, outcome);
        }
    }

    async Task<JobOutcome> ProcessRetrieveFolderJob(RetrieveFolderJob job, CancellationToken parentToken)
    {
        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        try
        {
            job.UpdateActivity(JobActivityPhase.RetrievingFolder);
            int newFilesFound = await searcher!.CompleteFolder(job.TargetFolder, job.Cts!.Token);
            job.NewFilesFoundCount = newFilesFound;
            job.RetrievalOutcome = FolderRetrievalOutcome.Completed;
            job.Discovery = new DiscoverySummary { RawResultCount = newFilesFound, LockedFileCount = 0 };
            var outcome = JobOutcome.Done();
            CommitOutcome(job, outcome);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            job.Discovery = new DiscoverySummary { RawResultCount = 0, LockedFileCount = 0 };
            job.RetrievalOutcome = FolderRetrievalOutcome.Cancelled;
            var outcome = JobOutcome.Cancelled(CancellationSourceFor(job, parentToken));
            CommitOutcome(job, outcome);
            Events.RaiseJobStatus(job, "cancelled");
            return outcome;
        }
    }

    async Task<JobOutcome?> ProcessSongDiscovery(SongJob job, CancellationToken parentToken)
    {
        var config = job.Config;

        if (config.PrintResults)
        {
            var printResponseData = new ResponseData();
            var searchFailure = await TrySearchWithReconnect(job, parentToken,
                () => searcher!.SearchSong(job, config.Search, printResponseData, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;

            var outcome = job.Candidates?.Count > 0
                ? JobOutcome.Done()
                : NoMatchingDiscoveryOutcome(printResponseData, "file result", "file results", "song candidates");
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior != DownloadBehavior.Manual || job.ResolvedTarget != null)
            return null;

        var responseData = new ResponseData();
        if (job.Candidates == null)
        {
            var searchFailure = await TrySearchWithReconnect(job, parentToken,
                () => searcher!.SearchSong(job, config.Search, responseData, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;
        }

        job.Discovery ??= new DiscoverySummary();
        job.Discovery.LockedFileCount = responseData.lockedFilesCount;

        var manualOutcome = job.Candidates?.Count > 0
            ? JobOutcome.AwaitingSelection()
            : NoMatchingDiscoveryOutcome(responseData, "file result", "file results", "song candidates");
        CommitOutcome(job, manualOutcome);
        return manualOutcome;
    }

    async Task<JobOutcome?> ProcessAlbumDiscovery(AlbumJob job, JobContext ctx, CancellationToken parentToken)
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
            var (searchOutcome, searchFailure) = await TrySearchWithReconnect(job, parentToken,
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

        job.Discovery ??= new DiscoverySummary();
        job.Discovery.LockedFileCount = responseData.lockedFilesCount;

        if (!foundSomething)
        {
            var outcome = NoMatchingDiscoveryOutcome(responseData, "file result", "file results", "album folders");
            await RunOnCompleteIfApplicable(job, null, Ctx(job), outcome);
            CommitOutcome(job, outcome);

            if (!config.PrintResults)
                ctx.IndexEditor?.Update();

            return outcome;
        }

        if (!config.PrintResults
            && job.DownloadBehavior == DownloadBehavior.Manual
            && job.ResolvedTarget == null)
        {
            var outcome = JobOutcome.AwaitingSelection();
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

    async Task<JobOutcome?> ProcessAggregateDiscovery(AggregateJob job, JobContext ctx, CancellationToken parentToken)
    {
        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        var (searchOutcome, searchFailure) = await TrySearchWithReconnect(job, parentToken,
            () => searcher!.SearchAggregate(job, config.Search, responseData, job.Cts!.Token));
        if (searchFailure != null)
            return searchFailure;

        if (searchOutcome != null)
        {
            CommitOutcome(job, searchOutcome);
            return searchOutcome;
        }

        bool foundSomething = job.Songs.Count > 0;

        job.Discovery ??= new DiscoverySummary();
        job.Discovery.LockedFileCount = responseData.lockedFilesCount;

        if (!foundSomething)
        {
            var outcome = NoMatchingDiscoveryOutcome(responseData, "file result", "file results", "aggregate track candidates");
            CommitOutcome(job, outcome);

            if (!config.PrintResults)
                ctx.IndexEditor?.Update();

            return outcome;
        }

        if (!config.PrintResults && job.DownloadBehavior == DownloadBehavior.Manual)
        {
            var outcome = JobOutcome.AwaitingSelection();
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

    async Task<JobOutcome> ProcessAlbumAggregateDiscovery(AlbumAggregateJob job, JobContext ctx, CancellationToken parentToken)
    {
        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        var (searchResult, searchFailure) = await TrySearchWithReconnect(job, parentToken,
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
        job.Discovery ??= new DiscoverySummary();
        job.Discovery.LockedFileCount = responseData.lockedFilesCount;

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (!foundSomething)
        {
            var outcome = NoMatchingDiscoveryOutcome(responseData, "file result", "file results", "album aggregate candidates");
            CommitOutcome(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior == DownloadBehavior.Manual)
        {
            var outcome = JobOutcome.AwaitingSelection();
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
        job.UpdateActivity(JobActivityPhase.RunningChildren);
        await ProcessJob(albumList, job.Cts!.Token, job);

        var finalOutcome = DeriveAlbumAggregateOutcome(job, albumList);
        CommitOutcome(job, finalOutcome);
        return finalOutcome;
    }

    static JobOutcome DeriveAlbumAggregateOutcome(AlbumAggregateJob job, JobList albumList)
    {
        bool anySuccessful = job.Albums.Any(IsSubtreeSuccessful);
        bool anyCancelled = job.Albums.Any(HasCancelledDescendant);
        bool anyUnsuccessful = job.Albums.Any(IsSubtreeUnsuccessful);

        if (anySuccessful && (anyCancelled || anyUnsuccessful))
            return JobOutcome.PartialSuccess(
                "Some generated albums completed and some failed or were cancelled.",
                anyCancelled ? CancellationSourceForDerivedCancellation(job, albumList) : JobCancellationSource.None);

        if (job.Cts?.IsCancellationRequested == true || albumList.FailureReason == JobFailureReason.Cancelled || anyCancelled)
            return JobOutcome.Cancelled(CancellationSourceForDerivedCancellation(job, albumList));

        var failedAlbum = job.Albums.FirstOrDefault(album => album.TerminalOutcome == JobTerminalOutcome.Failed);
        if (failedAlbum != null)
            return JobOutcome.Failed(
                failedAlbum.FailureReason == JobFailureReason.None ? JobFailureReason.AllDownloadsFailed : failedAlbum.FailureReason,
                failedAlbum.FailureMessage,
                failedAlbum.FailureDetail);

        var skippedAlbum = job.Albums.FirstOrDefault(album =>
            album.TerminalOutcome == JobTerminalOutcome.Skipped
            && album.SkipReason != JobSkipReason.AlreadyExists);
        if (skippedAlbum != null)
            return JobOutcome.Skipped(skippedAlbum.SkipReason, skippedAlbum.FailureReason);

        var unfinishedAlbum = job.Albums.FirstOrDefault(album => !IsSubtreeSuccessful(album));
        if (unfinishedAlbum != null)
            return JobOutcome.Failed(JobFailureReason.Other, $"Generated album did not finish successfully: {unfinishedAlbum}");

        return JobOutcome.Done();
    }

    async Task<JobOutcome> ProcessLeafDownload(Job job, JobContext ctx, CancellationToken parentToken)
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
                    outcome = await ProcessSongDownload(sj, songOrganizer, parentToken);
                    outcome = await CommitAndFinalizeSong(sj, sj, outcome, ctx, songOrganizer, organize: true, updateIndexes: true);
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
                    foreach (var song in ag.Songs.Where(s => s.LifecycleState == JobLifecycleState.Pending))
                    {
                        song.WorkflowId = ag.WorkflowId;
                        song.Config = config;
                        RegisterJob(song, ag);
                    }
                    ag.UpdateActivity(JobActivityPhase.RunningChildren);
                    outcome = await ProcessAggregateDownload(ag, ctx);
                    CommitOutcome(ag, outcome);
                    break;
            }

            SockseekLog.Jobs.Trace($"ProcessLeafJob: finished for job {job.DisplayId} ({job.GetType().Name})");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            var outcome = JobOutcome.Cancelled(CancellationSourceFor(job, parentToken));
            CommitOutcome(job, outcome);
            return outcome;
        }
    }

    static bool HasPreResolvedAlbumResults(Job job)
        => job is AlbumJob albumJob
            && (albumJob.ResolvedTarget != null || albumJob.Results.Count > 0);


    // ── per-job-type handlers ─────────────────────────────────────────────────

    async Task<JobOutcome> ProcessSongDownload(SongJob job, FileManager organizer, CancellationToken parentToken)
    {
        var config = job.Config;

        // If ResolvedTarget is set, pre-populate Candidates so search is skipped.
        if (job.ResolvedTarget != null && job.Candidates == null)
            job.Candidates = new List<FileCandidate> { job.ResolvedTarget };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        return await DownloadSong(job, job, config, organizer, cts, () => CancellationSourceFor(job, parentToken));
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
        bool anySuccessful = songs.Any(IsSubtreeSuccessful);
        bool anyCancelled = childOutcomes.Any(outcome => outcome.FailureReason == JobFailureReason.Cancelled)
            || songs.Any(HasCancelledDescendant);
        bool anyUnsuccessful = childOutcomes.Any(outcome =>
                outcome.FailureReason != JobFailureReason.Cancelled
                && (outcome.TerminalOutcome is JobTerminalOutcome.Failed
                    or JobTerminalOutcome.PartialSuccess
                    || (outcome.TerminalOutcome == JobTerminalOutcome.Skipped && outcome.SkipReason != JobSkipReason.AlreadyExists)))
            || songs.Any(IsSubtreeUnsuccessful);

        if (anySuccessful && (anyCancelled || anyUnsuccessful))
            return JobOutcome.PartialSuccess(
                "Some aggregate songs completed and some failed or were cancelled.",
                anyCancelled ? CancellationSourceForDerivedCancellation(job, songs.Cast<Job>().ToArray()) : JobCancellationSource.None);

        if (job.Cts?.IsCancellationRequested == true)
            return JobOutcome.Cancelled(CancellationSourceForDerivedCancellation(job, songs.Cast<Job>().ToArray()));

        if (anyCancelled)
            return JobOutcome.Cancelled(CancellationSourceForDerivedCancellation(job, songs.Cast<Job>().ToArray()));

        var failedOutcome = childOutcomes.FirstOrDefault(outcome =>
            outcome.TerminalOutcome == JobTerminalOutcome.Failed && outcome.FailureReason != JobFailureReason.Cancelled);
        if (failedOutcome != null)
            return JobOutcome.Failed(
                failedOutcome.FailureReason == JobFailureReason.None ? JobFailureReason.AllDownloadsFailed : failedOutcome.FailureReason,
                failedOutcome.FailureMessage,
                failedOutcome.FailureDetail);

        if (anyUnsuccessful)
            return JobOutcome.Failed(JobFailureReason.Other, "One or more aggregate songs failed.");

        return JobOutcome.Done();
    }


    async Task<JobOutcome> ProcessAlbumDownload(AlbumJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(job, config.Output, config.Extraction);
        var audioResult = await TryDownloadAlbumAudio(job, ctx, organizer);
        var completion = PrepareAlbumAudioOutcome(job, audioResult, ctx);
        var chosenFiles = completion.ChosenFiles;

        if (completion.Outcome.LifecycleState == JobLifecycleState.AwaitingSelection)
        {
            CommitOutcome(job, completion.Outcome);
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return completion.Outcome;
        }

        MarkCancelledAlbumFiles(job, audioResult, completion.Outcome);
        var images = await DownloadAlbumImagesIfNeeded(job, ctx, organizer, audioResult, chosenFiles);
        var outcome = config.Output.AlbumArtOnly
            ? DeriveAlbumArtOnlyOutcome(job, completion.Outcome, images.ChosenFiles, images.AdditionalImages)
            : completion.Outcome;
        if (!string.IsNullOrEmpty(job.DownloadPath))
            job.UpdateActivity(JobActivityPhase.Organizing);
        var finalization = _outputFinalizer.FinalizeAlbumPlacement(
            job,
            organizer,
            images.ChosenFiles,
            images.AdditionalImages,
            outcome);
        outcome = finalization.Outcome;
        if (finalization.OrganizationException != null && job.ResolvedTarget != null)
        {
            HandleIncompleteAlbumIfNeeded(job, job.ResolvedTarget, outcome, config);
        }

        var postProcessOutcome = OutcomeWithCurrentMetadata(job, outcome);
        await RunOnCompleteIfApplicable(job, null, ctx, postProcessOutcome);

        var finalOutcome = OutcomeWithCurrentMetadata(job, postProcessOutcome);
        CommitOutcome(job, finalOutcome);
        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
        return finalOutcome;
    }

    AlbumDownloadCompletion PrepareAlbumAudioOutcome(AlbumJob job, AlbumAudioDownloadResult audioResult, JobContext ctx)
    {
        var chosenFiles = audioResult.ChosenFiles;
        JobOutcome outcome;

        if (audioResult.Succeeded && chosenFiles != null)
        {
            var downloadedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.TerminalOutcome == JobTerminalOutcome.Succeeded && !string.IsNullOrEmpty(af.DownloadPath));

            if (downloadedAudio.Any())
            {
                var downloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(af => af.DownloadPath!));
                outcome = JobOutcome.Done(downloadPath);
                job.DownloadPath = downloadPath;
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, downloadPath);
                // Note: album jobs have no parent extractor reference here; RemoveTrackFromSource
                // for albums is handled at the JobList fan-out level if needed.
            }
            else
            {
                outcome = JobOutcome.Done();
            }
        }
        else if (audioResult.Outcome != null)
        {
            outcome = audioResult.Outcome;
            ApplyPreCommitOutcomeMetadata(job, outcome);
        }
        else
        {
            outcome = NoMatchingCandidatesOutcome();
        }

        return new(outcome, chosenFiles);
    }

    void MarkCancelledAlbumFiles(AlbumJob job, AlbumAudioDownloadResult audioResult, JobOutcome outcome)
    {
        if (outcome.FailureReason == JobFailureReason.Cancelled)
        {
            var cancelledFolder = job.ResolvedTarget
                ?? audioResult.LastChosenFolder;

            if (cancelledFolder != null)
                MarkUnfinishedAlbumFilesCancelled(job, cancelledFolder);
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

    JobOutcome DeriveAlbumArtOnlyOutcome(
        AlbumJob job,
        JobOutcome fallbackOutcome,
        List<SongJob>? chosenFiles,
        List<SongJob>? additionalImages)
    {
        var imageFiles = (chosenFiles ?? [])
            .Concat(additionalImages ?? [])
            .Where(file => file.IsNotAudio)
            .Distinct()
            .ToList();

        if (job.Cts?.IsCancellationRequested == true
            || imageFiles.Any(file => file.FailureReason == JobFailureReason.Cancelled))
            return JobOutcome.Cancelled(CancellationSourceForDerivedCancellation(job, imageFiles.Cast<Job>().ToArray()));

        var downloadedImages = imageFiles
            .Where(file => file.TerminalOutcome == JobTerminalOutcome.Succeeded && !string.IsNullOrEmpty(file.DownloadPath))
            .ToList();

        if (downloadedImages.Count > 0)
        {
            var downloadPath = Utils.GreatestCommonDirectory(downloadedImages.Select(file => file.DownloadPath!));
            job.DownloadPath = downloadPath;
            return JobOutcome.Done(downloadPath);
        }

        var existingImages = imageFiles
            .Where(file => file.IsSkippedAlreadyExists && !string.IsNullOrEmpty(file.DownloadPath))
            .ToList();

        if (existingImages.Count > 0)
        {
            var downloadPath = Utils.GreatestCommonDirectory(existingImages.Select(file => file.DownloadPath!));
            job.DownloadPath = downloadPath;
            return JobOutcome.AlreadyExists(downloadPath);
        }

        var failedImage = imageFiles.FirstOrDefault(file => file.IsUnsuccessfulTerminal);
        if (failedImage != null)
        {
            return JobOutcome.Failed(
                failedImage.FailureReason == JobFailureReason.None ? JobFailureReason.AllDownloadsFailed : failedImage.FailureReason,
                failedImage.FailureMessage);
        }

        return fallbackOutcome.TerminalOutcome == JobTerminalOutcome.Failed
            ? fallbackOutcome
            : NoMatchingCandidatesOutcome();
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
        var activeQuality = AlbumQualityPolicy.ActiveConditions(config.Search.NecessaryCond);
        bool verifyStrictAlbumQuality = config.Search.StrictAlbumQuality && activeQuality.IsActive;
        AlbumFolder? lastChosenFolder = null;

        List<SongJob> TrackJobsFor(AlbumFolder folder)
            => job.EnsureTrackJobs(folder);

        async Task RunAlbumDownloads(AlbumFolder folder, CancellationTokenSource cts)
        {
            var tasks = TrackJobsFor(folder).Select(async af =>
            {
                if (af.LifecycleState != JobLifecycleState.Pending) return;
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await DownloadEmbeddedSong(af, job, config, organizer, cts, cancelGroupOnFail: !af.IsNotAudio, organize: true);
            });
            await Task.WhenAll(tasks);
        }

        AlbumAudioDownloadResult ReturnSelectedFolderToManualPicker(AlbumFolder? failedFolder, JobFailureReason finalReason)
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
            return new(false, JobOutcome.AwaitingSelection(), null, failedFolder ?? lastChosenFolder);
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
                    index = job.Results.FindIndex(f => f.Files.Any(af => af.Filename.ContainsIgnoreCase(filterStr)));
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
                            return ReturnSelectedFolderToManualPicker(chosenFolder, JobFailureReason.NoMatchingResults);

                        job.Results.RemoveAt(index);
                        if (--albumTrackCountRetries <= 0)
                        {
                            SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times, skipping album: {job}");
                            return new(false, NoMatchingCandidatesOutcome(), null, lastChosenFolder);
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
                        return ReturnSelectedFolderToManualPicker(chosenFolder, JobFailureReason.NoMatchingResults);
                    }

                    job.Results.RemoveAt(index);
                    if (--albumTrackCountRetries <= 0)
                    {
                        SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times, skipping album: {job}");
                        return new(false, NoMatchingCandidatesOutcome(), null, lastChosenFolder);
                    }
                    continue;
                }
            }

            if (verifyStrictAlbumQuality)
            {
                if (!chosenFolder.IsFullyRetrieved && retrieveCurrent && !retrievedFolders.Contains(chosenFolder.FolderPath))
                {
                    var retrieval = await ProcessFolderRetrieval(chosenFolder, job,
                        "Verifying strict album quality.\n    Retrieving full folder contents...",
                        consumeJobSlot: false);
                    if (retrieval.RetrievalCompleted)
                        retrievedFolders.Add(chosenFolder.FolderPath);
                    else
                    {
                        SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: strict album quality verification was cancelled, skipping folder: {chosenFolder.FolderPath}");
                        if (wasPreselected)
                            return ReturnSelectedFolderToManualPicker(chosenFolder, JobFailureReason.NoMatchingResults);

                        job.Results.RemoveAt(index);
                        continue;
                    }
                }

                var qualityCoverage = AlbumQualityPolicy.Evaluate(chosenFolder, config.Search.NecessaryCond, activeQuality);
                if (!qualityCoverage.IsAcceptable(strict: true))
                {
                    SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: strict album quality failed ({qualityCoverage.MatchingFileCount}/{qualityCoverage.AudioFileCount} matching audio files), skipping folder: {chosenFolder.FolderPath}");
                    if (wasPreselected)
                        return ReturnSelectedFolderToManualPicker(chosenFolder, JobFailureReason.NoMatchingResults);

                    job.Results.RemoveAt(index);
                    continue;
                }
            }

            lastChosenFolder = chosenFolder;
            organizer.SetremoteBaseDir(chosenFolder.FolderPath);
            job.ResolvedTarget = chosenFolder;
            TrackJobsFor(chosenFolder);
            job.UpdateActivity(JobActivityPhase.Downloading);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            tried++;

            try
            {
                await RunAlbumDownloads(chosenFolder, cts);
                if (TryGetInterruptedAlbumOutcome(job, chosenFolder) is { } interruptedOutcome)
                {
                    HandleIncompleteAlbumIfNeeded(job, chosenFolder, interruptedOutcome, config);
                    return new(false, interruptedOutcome, null, lastChosenFolder);
                }

                if (!config.Search.NoBrowseFolder && retrieveCurrent && !chosenFolder.IsFullyRetrieved && !retrievedFolders.Contains(chosenFolder.FolderPath))
                {
                    var retrieval = await ProcessFolderRetrieval(chosenFolder, job, consumeJobSlot: false);
                    if (retrieval.RetrievalCompleted)
                        retrievedFolders.Add(chosenFolder.FolderPath);
                    if (retrieval.NewFilesFoundCount > 0)
                    {
                        await RunAlbumDownloads(chosenFolder, cts);
                        if (TryGetInterruptedAlbumOutcome(job, chosenFolder) is { } interruptedOutcomeAfterRetrieval)
                        {
                            HandleIncompleteAlbumIfNeeded(job, chosenFolder, interruptedOutcomeAfterRetrieval, config);
                            return new(false, interruptedOutcomeAfterRetrieval, null, lastChosenFolder);
                        }
                    }
                }

                job.ResolvedTarget = chosenFolder;
                return new(true, null, TrackJobsFor(chosenFolder), lastChosenFolder);
            }
            catch (OperationCanceledException)
            {
                MarkUnfinishedAlbumFilesCancelled(job, chosenFolder);

                HandleIncompleteAlbum(job, chosenFolder, config.ResolveIncompleteAlbumAction(), config);

                if (job.Cts != null && job.Cts.IsCancellationRequested)
                    return new(false, JobOutcome.Cancelled(CancellationSourceForDerivedCancellation(job, TrackJobsFor(chosenFolder).Cast<Job>().ToArray())), null, lastChosenFolder);

                if (wasPreselected)
                    return ReturnSelectedFolderToManualPicker(chosenFolder, JobFailureReason.AllDownloadsFailed);
            }

            organizer.SetremoteBaseDir(null);
            if (wasPreselected || tried >= config.Transfer.MaxDownloadRetries)
                return wasPreselected
                    ? ReturnSelectedFolderToManualPicker(lastChosenFolder, JobFailureReason.AllDownloadsFailed)
                    : new(false, JobOutcome.Failed(JobFailureReason.AllDownloadsFailed), null, lastChosenFolder);

            job.ResolvedTarget = null;
            job.ClearTrackJobs();
            job.Results.RemoveAt(index);

            // Reset state so the next iteration transitions to Downloading naturally
            job.ResetToPending();
        }

        return new(false, null, null, lastChosenFolder);
    }

    JobOutcome? TryGetInterruptedAlbumOutcome(AlbumJob job, AlbumFolder folder)
    {
        var tracks = job.EnsureTrackJobs(folder);
        if (job.Cts?.IsCancellationRequested == true || tracks.Any(song => song.FailureReason == JobFailureReason.Cancelled))
        {
            MarkUnfinishedAlbumFilesCancelled(job, folder);
            return JobOutcome.Cancelled(CancellationSourceForDerivedCancellation(job, tracks.Cast<Job>().ToArray()));
        }

        var failedSong = tracks.FirstOrDefault(song =>
            !song.IsNotAudio
            &&
            song.LifecycleState == JobLifecycleState.Terminal
            && !IsSuccessfulTerminal(song));

        return failedSong == null
            ? null
            : JobOutcome.Failed(
                failedSong.FailureReason == JobFailureReason.None ? JobFailureReason.AllDownloadsFailed : failedSong.FailureReason,
                failedSong.FailureMessage);
    }

    void HandleIncompleteAlbumIfNeeded(
        AlbumJob job,
        AlbumFolder folder,
        JobOutcome outcome,
        DownloadSettings config)
    {
        if (outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled)
            HandleIncompleteAlbum(job, folder, config.ResolveIncompleteAlbumAction(), config);
    }

    void MarkUnfinishedAlbumFilesCancelled(AlbumJob job, AlbumFolder folder)
    {
        foreach (var song in job.EnsureTrackJobs(folder).Where(song => song.LifecycleState != JobLifecycleState.Terminal))
        {
            song.MarkCancellationSource(JobCancellationSource.ParentJob);
            song.SetCancelled(JobCancellationSource.ParentJob);
        }
    }


    // ── single-song download ──────────────────────────────────────────────────

    async Task<JobOutcome> DownloadSong(
        SongJob song,
        Job job,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource cts,
        Func<JobCancellationSource> cancellationSource)
    {
        if (song.LifecycleState != JobLifecycleState.Pending) return JobOutcome.NoChange();

        int tries = config.Transfer.UnknownErrorRetries;
        JobOutcome? finalOutcome = null;
        string? lastFailureMessage = null;
        string? lastFailureDetail = null;

        while (tries > 0)
        {
            if (song.LifecycleState == JobLifecycleState.Terminal)
                break;

            await _clientManager.WaitUntilReadyAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                var outcome = await SearchAndDownloadSong(song, job, config, organizer, cts);
                if (outcome.TerminalOutcome == JobTerminalOutcome.Succeeded)
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
                    return JobOutcome.Cancelled(cancellationSource());
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
            return JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, lastFailureMessage, lastFailureDetail);
        }

        return finalOutcome ?? JobOutcome.NoChange();
    }

    async Task<JobOutcome> CommitAndFinalizeSong(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        JobContext jobCtx,
        FileManager organizer,
        bool organize,
        bool updateIndexes)
    {
        ApplyPreCommitOutcomeMetadata(song, outcome);
        if (outcome.FailureReason != JobFailureReason.Cancelled)
            outcome = await CompleteSongBeforeCommit(song, parentJob, outcome, jobCtx, organizer, organize);

        outcome = OutcomeWithCurrentMetadata(song, outcome);
        CommitOutcome(song, outcome);

        if (updateIndexes)
        {
            SockseekLog.Jobs.Trace($"ProcessSongJob finished for {song.DisplayId}. Calling IndexEditor Update ({(jobCtx.IndexEditor != null ? "Yes" : "No")}) and PlaylistEditor Update ({(jobCtx.PlaylistEditor != null ? "Yes" : "No")})");
            jobCtx.IndexEditor?.Update();
            jobCtx.PlaylistEditor?.Update();
        }

        return outcome;
    }

    async Task<JobOutcome> CompleteSongBeforeCommit(SongJob song, Job parentJob, JobOutcome outcome, JobContext jobCtx, FileManager organizer, bool organize)
    {
        var finalization = _outputFinalizer.FinalizeSongPlacement(song, parentJob, outcome, organizer, organize);
        if (finalization.OrganizationException != null)
            return finalization.Outcome;

        var postProcessOutcome = OutcomeWithCurrentMetadata(song, finalization.Outcome);
        await RunOnCompleteIfApplicable(parentJob, song, jobCtx, postProcessOutcome);

        _outputFinalizer.PublishDownloadedFileCache(song, postProcessOutcome);
        return postProcessOutcome;
    }

    async Task RunOnCompleteIfApplicable(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
    {
        if (!OnCompleteExecutor.HasApplicableCommand(job, song, outcome))
            return;

        var activityJob = song ?? job;
        activityJob.UpdateActivity(JobActivityPhase.RunningOnComplete);
        await OnCompleteExecutor.ExecuteAsync(job, song, ctx, outcome);
    }

    static void ApplyPreCommitOutcomeMetadata(Job job, JobOutcome outcome)
    {
        if (job is SongJob song)
        {
            if (outcome.ChosenCandidate != null)
                song.ChosenCandidate = outcome.ChosenCandidate;
            if (outcome.DownloadPath != null)
                song.DownloadPath = outcome.DownloadPath;
        }
        else if (job is AlbumJob album && outcome.DownloadPath != null)
        {
            album.DownloadPath = outcome.DownloadPath;
        }
    }

    static JobOutcome OutcomeWithCurrentMetadata(Job job, JobOutcome outcome)
    {
        if (job is SongJob song)
        {
            var downloadPath = song.DownloadPath ?? outcome.DownloadPath;
            var chosenCandidate = song.ChosenCandidate ?? outcome.ChosenCandidate;

            return outcome.TerminalOutcome switch
            {
                JobTerminalOutcome.Succeeded => JobOutcome.Done(downloadPath, chosenCandidate),
                JobTerminalOutcome.Skipped when outcome.SkipReason == JobSkipReason.AlreadyExists => JobOutcome.AlreadyExists(downloadPath),
                JobTerminalOutcome.Skipped => JobOutcome.Skipped(outcome.SkipReason, outcome.FailureReason, downloadPath),
                _ => outcome,
            };
        }

        if (job is AlbumJob album)
        {
            var downloadPath = album.DownloadPath ?? outcome.DownloadPath;

            return outcome.TerminalOutcome switch
            {
                JobTerminalOutcome.Succeeded => JobOutcome.Done(downloadPath),
                JobTerminalOutcome.Skipped when outcome.SkipReason == JobSkipReason.AlreadyExists => JobOutcome.AlreadyExists(downloadPath),
                JobTerminalOutcome.Skipped => JobOutcome.Skipped(outcome.SkipReason, outcome.FailureReason, downloadPath),
                _ => outcome,
            };
        }

        return outcome;
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
        bool searched = false;

        // Skip search if candidates are pre-set (ResolvedTarget / direct download).
        if (song.Candidates == null)
        {

            if (!config.Search.FastSearch)
            {
                searched = true;
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

                searched = true;
                var searchTask = searcher!.SearchSong(song, config.Search, responseData, searchCts.Token,
                    onFastSearchCandidate: fc =>
                    {
                        if (fastDownloadTask == null)
                        {
                            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search starting provisional download from {fc.Username}\\{fc.Filename}: {song}");
                            var target = _outputFinalizer.GetInitialDownloadTarget(config, song, organizer, fc);

                            // Use the main job CTS for the download so cancelling the search doesn't kill the download.
                            fastDownloadTask = downloader!
                                .DownloadFile(
                                    fc,
                                    target.Path,
                                    song,
                                    config.Transfer,
                                    config.Output.ParentDir,
                                    cts.Token,
                                    target.PublishToDuplicateCache)
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
            return searched
                ? NoMatchingDiscoveryOutcome(responseData, "file result", "file results", "song candidates")
                : NoMatchingCandidatesOutcome();
        }

        // Try candidates in order until one succeeds.
        int tried = 0;
        Exception? lastDownloadException = null;
        string? lastDownloadFailureMessage = null;
        foreach (var candidate in candidates)
        {
            tried++;
            var target = _outputFinalizer.GetInitialDownloadTarget(config, song, organizer, candidate);

            FileDownloadOutcome download;
            try
            {
                song.UpdateActivity(JobActivityPhase.Downloading);
                // ReportDownloadStart is called inside DownloadFile (via Downloader).
                download = await downloader!.DownloadFile(
                    candidate,
                    target.Path,
                    song,
                    config.Transfer,
                    config.Output.ParentDir,
                    cts.Token,
                    target.PublishToDuplicateCache);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_clientManager.IsConnectedAndLoggedIn)
                    throw;

                lastDownloadException = ex;
                lastDownloadFailureMessage = DownloadFailureMessage(ex);
                SockseekLog.Jobs.Debug(
                    $"Download attempt {tried} failed for '{candidate.Username}\\{candidate.Filename}' " +
                    $"to '{target.Path}': {SockseekLog.ExceptionSummary(ex)}");
                if (tried >= candidates.Count || tried >= config.Transfer.MaxDownloadRetries)
                {
                    return JobOutcome.Failed(
                        JobFailureReason.AllDownloadsFailed,
                        lastDownloadFailureMessage);
                }

                continue;
            }

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

        if (lastDownloadException != null)
            return JobOutcome.Failed(
                JobFailureReason.AllDownloadsFailed,
                lastDownloadFailureMessage ?? DownloadFailureMessage(lastDownloadException));

        return NoMatchingCandidatesOutcome();
    }

    static void CommitOutcome(Job job, JobOutcome outcome)
    {
        if (!outcome.ShouldCommit)
            return;

        if (outcome.LifecycleState == JobLifecycleState.AwaitingSelection)
        {
            job.SetAwaitingSelection();
            return;
        }

        if (outcome.ActivityPhase is { } phase)
        {
            job.UpdateActivity(phase);
            return;
        }

        switch (outcome.TerminalOutcome)
        {
            case JobTerminalOutcome.Succeeded:
                if (job is SongJob song)
                    song.SetDone(outcome.DownloadPath, outcome.ChosenCandidate);
                else if (job is AlbumJob album)
                    album.SetDone(outcome.DownloadPath);
                else
                    job.SetDone();
                break;

            case JobTerminalOutcome.Failed:
                job.Fail(outcome.FailureReason, outcome.FailureMessage, outcome.FailureDetail);
                break;

            case JobTerminalOutcome.Cancelled:
                job.SetCancelled(outcome.CancellationSource, outcome.FailureMessage, outcome.FailureDetail);
                break;

            case JobTerminalOutcome.Skipped:
                if (outcome.SkipReason == JobSkipReason.AlreadyExists)
                {
                    if (job is SongJob existingSong)
                        existingSong.SetAlreadyExists(outcome.DownloadPath);
                    else if (job is AlbumJob existingAlbum)
                        existingAlbum.SetAlreadyExists(outcome.DownloadPath);
                    else
                        job.SetAlreadyExists();
                }
                else
                {
                    job.SetSkipped(outcome.SkipReason, outcome.FailureReason);
                }
                break;

            case JobTerminalOutcome.PartialSuccess:
                job.SetPartialSuccess(outcome.FailureMessage, outcome.CancellationSource);
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
                RaiseBuildingMusicDirectoryIndex(job);
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
                    RaiseBuildingMusicDirectoryIndex(job);
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
                    RaiseBuildingMusicDirectoryIndex(job);
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
                RaiseBuildingMusicDirectoryIndex(job);
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
        if (IsNotFoundFailure(prev.FailureReason) || prev.State == JobStateOld.NotFoundLastTime)
        {
            song.SetSkipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason));
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
        if (IsNotFoundFailure(prev.FailureReason) || prev.State == JobStateOld.NotFoundLastTime)
        {
            job.SetSkipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason));
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
        if (!IsNotFoundFailure(prev.FailureReason) && prev.State != JobStateOld.NotFoundLastTime)
            return null;

        SockseekLog.Jobs.Debug($"[{job.DisplayId}] {JobLogKind(job)}: skipped because prior index entry was {prev.State}/{prev.FailureReason}: {job}");
        return JobOutcome.Skipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason));
    }

    static JobFailureReason NotFoundFailureReasonOrDefault(JobFailureReason reason)
        => IsNotFoundFailure(reason) ? reason : JobFailureReason.NoMatchingResults;


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

    void HandleIncompleteAlbum(AlbumJob job, AlbumFolder folder, ResolvedIncompleteAlbumAction action, DownloadSettings config)
    {
        if (action.Kind == IncompleteAlbumActionKind.Keep)
            return;

        var failedAlbumPath = action.Path;
        var outputParentDir = config.Output.ParentDir;
        var filesToHandle = job.EnsureTrackJobs(folder)
            .Where(IsIncompleteAlbumActionFile)
            .ToList();

        if (filesToHandle.Count == 0)
        {
            SockseekLog.Jobs.Debug($"[{job.DisplayId}] AlbumJob: skipping failed-album action; no completed files were downloaded for failed folder {folder.FolderPath}");
            return;
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
        {
            Events.RaiseJobStatus(job, "deleting files");
            SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: Deleting album files");
        }
        else if (action.Kind == IncompleteAlbumActionKind.Move)
        {
            if (string.IsNullOrEmpty(outputParentDir))
                throw new InvalidOperationException("Cannot move incomplete album files because Output.ParentDir is not set.");
            if (string.IsNullOrEmpty(failedAlbumPath))
                throw new InvalidOperationException("Cannot move incomplete album files because incomplete album action path is not set.");

            Events.RaiseJobStatus(job, $"moving to {failedAlbumPath}");
            SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: Moving album files to {failedAlbumPath}");
        }

        foreach (var af in filesToHandle)
        {
            var downloadPath = af.DownloadPath!;
            try
            {
                if (action.Kind == IncompleteAlbumActionKind.Delete)
                {
                    File.Delete(downloadPath);
                }
                else if (action.Kind == IncompleteAlbumActionKind.Move)
                {
                    var relativeBase = outputParentDir
                        ?? throw new InvalidOperationException("Cannot move incomplete album files because Output.ParentDir is not set.");
                    var targetBase = failedAlbumPath
                        ?? throw new InvalidOperationException("Cannot move incomplete album files because incomplete album action path is not set.");
                    var newPath = Path.Join(targetBase, Path.GetRelativePath(relativeBase, downloadPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Utils.Move(downloadPath, newPath);
                }

                var downloadParent = Path.GetDirectoryName(downloadPath);
                if (!string.IsNullOrEmpty(downloadParent) && !string.IsNullOrEmpty(outputParentDir))
                    Utils.DeleteAncestorsIfEmpty(downloadParent, outputParentDir);
            }
            catch (Exception e)
            {
                SockseekLog.Jobs.Error($"Error: Unable to move or delete file '{downloadPath}' after album fail: {e}");
            }
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
            Events.RaiseJobStatus(job, "deleted files");
        else if (action.Kind == IncompleteAlbumActionKind.Move)
            Events.RaiseJobStatus(job, $"moved to {failedAlbumPath}");
    }

    static bool IsIncompleteAlbumActionFile(SongJob song)
        => song.TerminalOutcome == JobTerminalOutcome.Succeeded
            && !string.IsNullOrEmpty(song.DownloadPath)
            && !song.DownloadPath.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)
            && File.Exists(song.DownloadPath);


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
        var parentActivityBeforeRetrieval = parentJob.ActivityPhase;
        var parentActivityUntilBeforeRetrieval = parentJob.ActivityUntilUtc;
        if (!parentJob.IsTerminal)
            parentJob.UpdateActivity(JobActivityPhase.RetrievingFolder);
        rfJob.UpdateActivity(JobActivityPhase.RetrievingFolder);
        SockseekLog.Jobs.Debug($"[{rfJob.DisplayId}] RetrieveFolderJob: retrieving folder for parent [{parentJob.DisplayId}] {JobLogKind(parentJob)}: {folder.FolderPath}");

        int count = 0;
        try
        {
            async Task<int> CompleteFolder()
            {
                rfJob.UpdateActivity(JobActivityPhase.RetrievingFolder);
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
            rfJob.SetCancelled(CancellationSourceFor(rfJob, parentJob.Cts!.Token));
            Events.RaiseJobStatus(rfJob, "cancelled");
            SockseekLog.Jobs.Info($"[{rfJob.DisplayId}] RetrieveFolderJob: Cancelled folder retrieval for {folder.FolderPath}");
            return rfJob;
        }
        finally
        {
            if (!parentJob.IsTerminal
                && parentJob.Cts?.IsCancellationRequested != true
                && parentJob.ActivityPhase == JobActivityPhase.RetrievingFolder)
            {
                parentJob.UpdateActivity(parentActivityBeforeRetrieval, parentActivityUntilBeforeRetrieval);
            }

            rfJob.Discovery = new DiscoverySummary { RawResultCount = count, LockedFileCount = 0 };
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
            .Select(f => f.Files.Where(af => Utils.IsImageFile(af.Filename)).ToList())
            .Where(ls => ls.Count > 0)
            .ToList();

        if (imageFolders.Count == 0)
        { SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: no images found: {job}"); return result; }

        if (option == AlbumArtOption.Largest)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Max(af => af.Candidate.File.Size) / 1024 / 100)
                .ThenByDescending(ls => ls[0].Candidate.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.Candidate.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mSize = job.TrackJobs
                    .Where(af => af.TerminalOutcome == JobTerminalOutcome.Succeeded && Utils.IsImageFile(af.DownloadPath ?? ""))
                    .Select(af => af.ResolvedTarget!.File.Size)
                    .DefaultIfEmpty(0).Max();
        }
        else if (option == AlbumArtOption.Most)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Count)
                .ThenByDescending(ls => ls[0].Candidate.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.Candidate.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mCount = job.TrackJobs.Count(af => af.TerminalOutcome == JobTerminalOutcome.Succeeded && Utils.IsImageFile(af.DownloadPath ?? ""));
        }

        bool needsDownload(List<AlbumFile> ls) => option == AlbumArtOption.Most
            ? mCount < ls.Count
            : option == AlbumArtOption.Largest
                ? mSize == 0 || mSize < ls.Max(af => af.Candidate.File.Size) - 1024 * 50
                : true;

        bool SameCandidate(FileCandidate? left, FileCandidate right)
            => left != null
                && string.Equals(left.Username, right.Username, StringComparison.Ordinal)
                && string.Equals(left.Filename, right.Filename, StringComparison.Ordinal);

        SongJob ImageJobFor(AlbumFile file)
        {
            var existing = job.TrackJobs.Concat(result)
                .FirstOrDefault(song => SameCandidate(song.ResolvedTarget, file.Candidate));
            if (existing != null)
                return existing;

            var imageJob = AlbumJob.CreateTrackJob(file);
            job.TrackJobs.Add(imageJob);
            return imageJob;
        }

        while (imageFolders.Count > 0)
        {
            var imgs = imageFolders[0];
            imageFolders.RemoveAt(0);
            var imageJobs = imgs.Select(ImageJobFor).ToList();

            if (imageJobs.All(af => af.TerminalOutcome == JobTerminalOutcome.Succeeded
                    || (af.TerminalOutcome == JobTerminalOutcome.Skipped && af.SkipReason == JobSkipReason.AlreadyExists))
                || !needsDownload(imgs))
            {
                var imageFolderPath = Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.Filename));
                SockseekLog.Jobs.Info($"[{job.DisplayId}] AlbumJob: image requirements already satisfied: {imageFolderPath}");
                return result;
            }

            fileManager.downloadingAdditionalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.Filename)));

            bool allSucceeded = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            foreach (var af in imageJobs)
            {
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await DownloadEmbeddedSong(af, job, config, fileManager, cts, cancelGroupOnFail: false, organize: true);
                if (af.TerminalOutcome == JobTerminalOutcome.Succeeded)
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
        if (song.LifecycleState != JobLifecycleState.Pending) return JobOutcome.NoChange();

        song.WorkflowId = parentJob.WorkflowId;
        song.Config = config;
        song.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, groupCts.Token);
        RegisterJob(song, parentJob);

        JobOutcome outcome = JobOutcome.NoChange();
        try
        {
            outcome = await DownloadSong(
                song,
                parentJob,
                config,
                organizer,
                song.Cts,
                () => CancellationSourceForEmbeddedSong(song, parentJob, groupCts));

            if (outcome.FailureReason == JobFailureReason.Cancelled && !groupCts.IsCancellationRequested)
            {
                CommitOutcome(song, outcome);
                return outcome;
            }

            if (cancelGroupOnFail && ShouldCancelGroupOnEmbeddedOutcome(outcome))
            {
                CommitOutcome(song, outcome);
                groupCts.Cancel();
                throw new OperationCanceledException();
            }

            var finalOutcome = await CommitAndFinalizeSong(
                song,
                parentJob,
                outcome,
                Ctx(parentJob),
                organizer,
                organize,
                updateIndexes: false);

            if (cancelGroupOnFail && ShouldCancelGroupOnEmbeddedOutcome(finalOutcome))
            {
                groupCts.Cancel();
                throw new OperationCanceledException();
            }

            return finalOutcome;
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested
            && song.Cts.IsCancellationRequested
            && song.FailureReason == JobFailureReason.Cancelled)
        {
            // User cancelled only this embedded song; keep the album/aggregate parent running.
            return JobOutcome.Cancelled(CancellationSourceForDerivedCancellation(song));
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

    static bool ShouldCancelGroupOnEmbeddedOutcome(JobOutcome outcome)
        => outcome.TerminalOutcome is JobTerminalOutcome.Failed
            or JobTerminalOutcome.Skipped
            or JobTerminalOutcome.PartialSuccess;

    JobCancellationSource CancellationSourceForEmbeddedSong(
        SongJob song,
        Job parentJob,
        CancellationTokenSource groupCts)
    {
        if (song.CancellationSource != JobCancellationSource.None)
            return song.CancellationSource;
        if (appCts.IsCancellationRequested)
            return JobCancellationSource.UserRequestedAllJobs;
        if (parentJob.Cts?.IsCancellationRequested == true || groupCts.IsCancellationRequested)
            return JobCancellationSource.ParentJob;

        return JobCancellationSource.InternalEngine;
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
                            // This cancels the active transfer attempt. If that cancellation bubbles up
                            // and terminal-cancels the job, CancellationSourceFor classifies it as
                            // InternalEngine rather than a direct user cancellation.
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
