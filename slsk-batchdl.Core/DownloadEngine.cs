using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Soulseek;
using Sldl.Core.Models;
using Sldl.Core;
using Sldl.Core.Extractors;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core.Settings;

using Directory = System.IO.Directory;
using File = System.IO.File;
using SlFile = Soulseek.File;

namespace Sldl.Core;


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

    private readonly ConcurrentDictionary<Guid, Job> _jobById = new();
    private readonly ConcurrentDictionary<int, Job> _jobByDisplayId = new();

    public Job? GetJob(Guid id) => _jobById.TryGetValue(id, out var job) ? job : null;
    public Job? GetJob(int displayId) => _jobByDisplayId.TryGetValue(displayId, out var job) ? job : null;
    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => _jobById.Values
        .Where(job => job.WorkflowId == workflowId)
        .OrderBy(job => job.DisplayId)
        .ToList();

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

    // ── job channel ──────────────────────────────────────────────────────────

    private readonly Channel<(Job Job, DownloadSettings Settings)> _jobChannel =
        Channel.CreateUnbounded<(Job, DownloadSettings)>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Enqueues a job for processing. Call <see cref="CompleteEnqueue"/> when done adding jobs.</summary>
    public void Enqueue(Job job, DownloadSettings settings) =>
        _jobChannel.Writer.TryWrite((job, settings));

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
        _extractorSemaphore = new SemaphoreSlim(settings.ConcurrentExtractors);
    }


    // ── top-level entry point ─────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        bool servicesInitialized = false;
        var rootTasks = new List<Task>();

        await foreach (var (rootJob, settings) in _jobChannel.Reader.ReadAllAsync(ct))
        {
            Queue.Jobs.Add(rootJob);

            foreach (var (id, ctx) in JobPreparer.PrepareSubtree(rootJob, settings, _jobSettingsResolver))
                _contexts[id] = ctx;

            if (settings.NeedLogin && !servicesInitialized)
            {
                await _clientManager.EnsureConnectedAndLoggedInAsync(engineSettings, ct);
                await _clientManager.WaitUntilReadyAsync(ct);
                searcher = new Searcher(Client!, _registry, _registry, Events, engineSettings.SearchesPerTime, engineSettings.SearchRenewTime, engineSettings.ConcurrentSearches);
                downloader = new Downloader(Client!, _clientManager, _registry, Events);
                _ = Task.Run(() => UpdateLoop(appCts.Token), appCts.Token);
                Logger.Debug("Update task started");
                servicesInitialized = true;
            }

            rootTasks.Add(ProcessJob(rootJob));
        }

        await Task.WhenAll(rootTasks);

        if (Queue.Jobs.Count > 0 && !Queue.Jobs[^1].Config!.DoNotDownload)
            Events.RaiseEngineCompleted(Queue);

        Logger.Debug("Exiting");
        appCts.Cancel();
    }


    // ── recursive job processor ───────────────────────────────────────────────

    async Task ProcessJob(Job job, IExtractor? extractor = null, CancellationToken parentToken = default, Job? parentJob = null)
    {
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

        try
        {
            // ── ExtractJob: run extractor, set Result, recurse ───────────────────
            if (job is ExtractJob ej)
            {
                InputType inputType;
                IExtractor ex;
                try
                {
                    (inputType, ex) = ExtractorRegistry.GetMatchingExtractor(ej.Input, ej.InputType ?? InputType.None, ej.Config);
                }
                catch (Exception e)
                {
                    ej.State = JobState.Failed;
                    ej.FailureReason = FailureReason.ExtractionFailed;
                    ej.FailureMessage = e.Message;
                    return;
                }

                ej.InputType = inputType;
                Job extracted;
                await _extractorSemaphore.WaitAsync(ej.Cts!.Token);
                try
                {
                    ej.State = JobState.Extracting;
                    extracted = await ex.GetTracks(ej.Input, ej.Config.Extraction);
                }
                catch (Exception e)
                {
                    ej.State = JobState.Failed;
                    ej.FailureReason = FailureReason.ExtractionFailed;
                    ej.FailureMessage = e.Message;
                    return;
                }
                finally
                {
                    _extractorSemaphore.Release();
                }

                ej.Result = extracted;
                ej.State = JobState.Done;

                Logger.Debug("Got tracks");

                // Post-extraction transforms — album/aggregate upgrades and name assignment
                if (extracted is IUpgradeable upgradeable)
                {
                    var upgraded = upgradeable.Upgrade(ej.Config.Extraction.IsAlbum, ej.Config.Search.IsAggregate).ToList();

                    if (upgraded.Count == 1)
                    {
                        ej.Result = upgraded[0];
                        extracted = ej.Result;
                    }
                    else
                    {
                        ej.Result = new JobList(extracted.ItemName, upgraded);
                        extracted = ej.Result;
                        ((Job)extracted).CopySharedFieldsFrom(upgradeable as Job ?? extracted);
                    }
                }

                AssignWorkflowId(extracted, ej.WorkflowId);

                Events.RaiseJobResultCreated(ej, extracted);
                // ExtractJob completion moment:
                // - extraction work is finished
                // - the result job now exists
                // - the ExtractJob itself is complete
                // Any later automatic processing of the result job is separate execution.
                RaiseJobExecutionCompleted();

                // Propagate provenance from ExtractJob to the extracted result.
                extracted.LineNumber = ej.LineNumber;
                extracted.ItemNumber = ej.ItemNumber;
                // For a single-song JobList, also stamp the inner song (used by RemoveTrackFromSource).
                if (extracted is JobList ejl && ejl.Jobs.Count == 1 && ejl.Jobs[0] is SongJob innerSong)
                {
                    innerSong.LineNumber = ej.LineNumber;
                    innerSong.ItemNumber = ej.ItemNumber;
                }

                // Report the initial track list.
                var allSongs = (extracted is JobList jlr ? jlr.AllSongs() : extracted is SongJob sjs ? new[] { sjs }.AsEnumerable() : Enumerable.Empty<SongJob>()).ToList();
                if (allSongs.Count > 0)
                    Events.RaiseTrackListReady(allSongs);

                // Prepare contexts for the extracted subtree, inheriting from the ExtractJob's context.
                var newContexts = JobPreparer.PrepareSubtree(extracted, ej.Config, _jobSettingsResolver);
                foreach (var (id, ctx) in newContexts)
                    _contexts[id] = ctx;

                if (!ej.AutoProcessResult)
                    return;

                // Pass parentToken (not ej.Cts.Token): the Result is a sibling of the ExtractJob in
                // the CTS hierarchy. Cancelling the ExtractJob after extraction completes has no effect
                // on the already-running Result; the Result can be cancelled independently.
                await ProcessJob(extracted, ex, parentToken, parentJob);
                return;
            }

            // ── JobList: list-level setup, fan-out, list-level cleanup ──────────
            if (job is JobList jl)
            {
                var ctx = _contexts.TryGetValue(jl.Id, out var c) ? c : null;
                var config = jl.Config!;

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
                }

                if (config.PrintTracks)
                {
                    if (directSongs.Count == 0)
                        await Task.WhenAll(jl.Jobs.ToList().Select(child => ProcessJob(child, extractor, jl.Cts!.Token, jl)));

                    jl.PrintLines();
                    return;
                }

                ctx?.IndexEditor?.Update();
                ctx?.PlaylistEditor?.Update();

                // ── fan-out ───────────────────────────────────────────────────────
                if (directSongs.Count > 0)
                {
                    var intervalReporter = new IntervalProgressReporter(TimeSpan.FromSeconds(30), 5, directSongs);

                    await Task.WhenAll(jl.Jobs.ToList().Select(async child =>
                    {
                        bool wasInitial = child is SongJob s && s.State == JobState.Pending;
                        await ProcessJob(child, extractor, jl.Cts!.Token, jl);

                        if (wasInitial && child is SongJob song)
                        {
                            ctx?.IndexEditor?.Update();
                            ctx?.PlaylistEditor?.Update();
                            intervalReporter.MaybeReport(song.State);
                            int dl = directSongs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
                            int fl = directSongs.Count(s => s.State == JobState.Failed);
                            Events.RaiseOverallProgress(dl, fl, directSongs.Count);

                            if (config.Extraction.RemoveTracksFromSource && extractor != null && song.State == JobState.Done)
                            {
                                try { await extractor.RemoveTrackFromSource(song); }
                                catch (Exception ex) { Logger.Error($"Error removing track from source: {ex.Message}"); }
                            }
                        }
                    }));

                    int dlFinal = directSongs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
                    int flFinal = directSongs.Count(s => s.State == JobState.Failed);
                    Events.RaiseListProgress(jl, dlFinal, flFinal, directSongs.Count);

                    // If all succeeded, also call whole-list removal (e.g. list.txt removes the source file).
                    if (config.Extraction.RemoveTracksFromSource && extractor != null
                        && directSongs.All(s => s.State == JobState.Done || s.State == JobState.AlreadyExists))
                    {
                        try { await extractor.RemoveTrackFromSource(new SongJob(new SongQuery { Title = jl.ItemName ?? "" })); }
                        catch { /* list-level removal is best-effort */ }
                    }
                }
                else
                {
                    await Task.WhenAll(jl.Jobs.ToList().Select(child => ProcessJob(child, extractor, jl.Cts!.Token, jl)));
                }

                return;
            }

            // ── Leaf jobs: skip checks, search, download ─────────────────────────
            await ProcessLeafJob(job);
        }
        finally
        {
            RaiseJobExecutionCompleted();
        }
    }

    async Task ProcessLeafJob(Job job)
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
            if (TrySetNotFoundLastTimeForJob(job))
            {
                Logger.Info($"Download '{job.ToString(true)}' was not found during a prior run, skipping");
                return;
            }
        }

        if (config.Skip.SkipExisting && !config.PrintResults && job.CanBeSkipped && TrySetJobAlreadyExists(job, ctx))
        {
            var existingPath = job switch
            {
                SongJob songJob => songJob.DownloadPath,
                AlbumJob albumJob => albumJob.DownloadPath,
                _ => null,
            };
            Logger.Info($"Download '{job.ToString(true)}' already exists{(string.IsNullOrWhiteSpace(existingPath) ? "" : $" at {existingPath}")}, skipping");
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return;
        }

        if (config.PrintTracks)
        {
            job.PrintLines();
            return;
        }

        // ── source search ─────────────────────────────────────────────────────

        if (job is SearchJob searchJob)
        {
            await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

            var responseData = new ResponseData();
            await searcher!.Search(searchJob, config.Search, responseData, job.Cts!.Token);
            job.Discovery = new DiscoverySummary { ResultCount = searchJob.ResultCount, LockedFileCount = responseData.lockedFilesCount };
            return;
        }

        if (job is RetrieveFolderJob retrieveFolderJob)
        {
            await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);
            retrieveFolderJob.State = JobState.Searching;

            int newFilesFound = 0;
            try
            {
                newFilesFound = await searcher!.CompleteFolder(retrieveFolderJob.TargetFolder, job.Cts!.Token);
                retrieveFolderJob.NewFilesFoundCount = newFilesFound;
                retrieveFolderJob.State = JobState.Done;
                job.Discovery = new DiscoverySummary { ResultCount = newFilesFound, LockedFileCount = 0 };
            }
            catch (OperationCanceledException)
            {
                retrieveFolderJob.State = JobState.Failed;
                retrieveFolderJob.FailureReason = FailureReason.Cancelled;
                Events.RaiseJobStatus(retrieveFolderJob, "cancelled");
                job.Discovery = new DiscoverySummary { ResultCount = 0, LockedFileCount = 0 };
            }

            return;
        }

        if (job is SongJob printSong && config.PrintResults)
        {
            await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);
            await searcher!.SearchSong(printSong, config.Search, new ResponseData(), job.Cts!.Token);
            if (printSong.Candidates?.Count > 0)
                printSong.State = JobState.Done;
            else
            {
                printSong.State = JobState.Failed;
                printSong.FailureReason = FailureReason.NoSuitableFileFound;
            }
            return;
        }

        if (job is AlbumJob or AggregateJob or AlbumAggregateJob)
        {
            await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);
            await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

            bool foundSomething = false;
            ResponseData responseData = new ResponseData();

            if (job is AlbumJob albumJob)
            {
                if (albumJob.ResolvedTarget != null)
                {
                    if (albumJob.Results.Count == 0)
                        albumJob.Results = [albumJob.ResolvedTarget];

                    if (albumJob.ResolvedTargetNeedsInitialFolderRetrieval)
                    {
                        await ProcessFolderRetrieval(albumJob.ResolvedTarget, albumJob);
                        albumJob.ResolvedTargetNeedsInitialFolderRetrieval = false;
                        if (albumJob.ResolvedTarget.Files.Count == 0)
                            albumJob.Results.Clear();
                    }

                    foundSomething = true;
                }
                else if (albumJob.Results.Count > 0)
                    foundSomething = true;
                else
                    await searcher!.SearchAlbum(albumJob, config.Search, responseData, job.Cts!.Token);
                foundSomething = albumJob.Results.Count > 0;
            }
            else if (job is AggregateJob aggJob)
            {
                await searcher!.SearchAggregate(aggJob, config.Search, responseData, job.Cts!.Token);
                foundSomething = aggJob.Songs.Count > 0;
            }
            else if (job is AlbumAggregateJob aabJob)
            {
                var newAlbumJobs = await searcher!.SearchAggregateAlbum(aabJob, config.Search, responseData, job.Cts!.Token);
                aabJob.Albums = newAlbumJobs;

                job.State = JobState.Done;
                foundSomething = newAlbumJobs.Count > 0;
                job.Discovery = new DiscoverySummary { ResultCount = aabJob.Albums.Count, LockedFileCount = responseData.lockedFilesCount };

                if (config.PrintResults)
                    return;

                if (foundSomething)
                {
                    var albumList = new JobList(job.ItemName, newAlbumJobs);
                    albumList.Config = job.Config;
                    albumList.WorkflowId = job.WorkflowId;
                    foreach (var aj in newAlbumJobs)
                    {
                        aj.ItemName = job.ItemName;
                        aj.Config = job.Config;
                        _contexts[aj.Id] = new JobContext
                        {
                            IndexEditor = ctx.IndexEditor,
                            PlaylistEditor = ctx.PlaylistEditor,
                            PreprocessTracks = false,
                        };
                    }
                    _contexts[albumList.Id] = new JobContext
                    {
                        IndexEditor = ctx.IndexEditor,
                        PlaylistEditor = ctx.PlaylistEditor,
                        PreprocessTracks = false,
                    };
                    await ProcessJob(albumList, null, job.Cts!.Token, job);
                }
                return;
            }

            job.Discovery = new DiscoverySummary 
            { 
                ResultCount = job switch { SongJob s => s.Candidates?.Count ?? 0, AlbumJob a => a.Results.Count, _ => foundSomething ? 1 : 0 }, 
                LockedFileCount = responseData.lockedFilesCount 
            };

            if (!foundSomething)
            {
                job.State = JobState.Failed;
                job.FailureReason = FailureReason.NoSuitableFileFound;

                if (job is AlbumJob aj)
                    await OnCompleteExecutor.ExecuteAsync(aj, null, Ctx(aj));

                if (!config.PrintResults)
                    ctx.IndexEditor?.Update();

                return;
            }

            if (config.Skip.SkipExisting && job is AggregateJob foundAggJob)
            {
                var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.Search);
                foreach (var song in foundAggJob.Songs)
                    TrySetAlreadyExists(foundAggJob, song, skipCtx);
            }
        }

        if (config.PrintResults)
            return;

        // ── download ─────────────────────────────────────────────────────────

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        try
        {
            switch (job)
            {
                case SongJob sj:
                    await ProcessSongJob(sj, ctx);
                    break;

                case AlbumJob aj:
                    await ProcessAlbumJob(aj, ctx);
                    break;

                case AggregateJob ag:
                    ag.State = JobState.Downloading;
                    Events.RaiseTrackBatchResolved(ag,
                        ag.Songs.Where(s => s.State == JobState.Pending).ToList(),
                        [],
                        []);
                    await ProcessAggregateJob(ag, ctx);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (job.Cts != null && job.Cts.IsCancellationRequested)
            {
                job.State = JobState.Failed;
                job.FailureReason = FailureReason.Cancelled;
            }
        }
    }

    static bool HasPreResolvedAlbumResults(Job job)
        => job is AlbumJob albumJob
            && (albumJob.ResolvedTarget != null || albumJob.Results.Count > 0);


    // ── per-job-type handlers ─────────────────────────────────────────────────

    async Task ProcessSongJob(SongJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(job, config.Output, config.Extraction);

        // If ResolvedTarget is set, pre-populate Candidates so search is skipped.
        if (job.ResolvedTarget != null && job.Candidates == null)
            job.Candidates = new List<FileCandidate> { job.ResolvedTarget };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        await DownloadSong(job, job, config, organizer, cts,
            cancelOnFail: false, organize: true);

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
    }


    async Task ProcessAggregateJob(AggregateJob job, JobContext ctx)
    {
        var config = job.Config;
        var songs = job.Songs;
        var organizer = new FileManager(job, config.Output, config.Extraction);

        var downloadTasks = songs.Select(async song =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
            await DownloadEmbeddedSong(song, job, config, organizer, cts, cancelGroupOnFail: false, organize: true);
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
        });

        await Task.WhenAll(downloadTasks);
    }


    async Task ProcessAlbumJob(AlbumJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(job, config.Output, config.Extraction);
        List<SongJob>? chosenFiles = null;
        var retrievedFolders = new HashSet<string>();
        bool succeeded = false;
        string? filterStr = null;
        int index = 0;
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

                // Verify track counts after full folder retrieval if needed.
                var folderCond = config.Search.NecessaryFolderCond;
                if (config.Transfer.AlbumTrackCountMaxRetries > 0
                    && ((folderCond.MaxTrackCount ?? 0) > 0 || ((folderCond.MinTrackCount ?? 0) > 0 && job.Query.Album.Length > 0)))
                {
                    if (!chosenFolder.IsFullyRetrieved && !retrievedFolders.Contains(chosenFolder.FolderPath))
                    {
                        await ProcessFolderRetrieval(chosenFolder, job,
                            "Verifying album track count.\n    Retrieving full folder contents...");
                        retrievedFolders.Add(chosenFolder.FolderPath);
                    }
                    int newCount = chosenFolder.Files.Count(af => !af.IsNotAudio);
                    bool trackCountFailed = false;
                    if (folderCond.MaxTrackCount is { } maxTrackCount and > 0 && newCount > maxTrackCount)
                    { Logger.Info($"New file count ({newCount}) above maximum ({folderCond.MaxTrackCount}), skipping folder"); trackCountFailed = true; }
                    if (folderCond.MinTrackCount is { } minTrackCount and > 0 && newCount < minTrackCount)
                    { Logger.Info($"New file count ({newCount}) below minimum ({folderCond.MinTrackCount}), skipping folder"); trackCountFailed = true; }

                    if (trackCountFailed)
                    {
                        job.Results.RemoveAt(index);
                        if (--albumTrackCountRetries <= 0)
                        {
                            Logger.Info($"Failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times, skipping album.");
                            job.State = JobState.Failed;
                            job.FailureReason = FailureReason.NoSuitableFileFound;
                            break;
                        }
                        continue;
                    }
                }
            }

            lastChosenFolder = chosenFolder;
            organizer.SetremoteBaseDir(chosenFolder.FolderPath);
            job.ResolvedTarget = chosenFolder;
            job.State = JobState.Downloading;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            try
            {
                await RunAlbumDownloads(chosenFolder, cts);

                if (!config.Search.NoBrowseFolder && retrieveCurrent && !chosenFolder.IsFullyRetrieved && !retrievedFolders.Contains(chosenFolder.FolderPath))
                {
                    var newFilesFound = await ProcessFolderRetrieval(chosenFolder, job);
                    retrievedFolders.Add(chosenFolder.FolderPath);
                    if (newFilesFound > 0)
                    {
                        await RunAlbumDownloads(chosenFolder, cts);
                    }
                }

                job.ResolvedTarget = chosenFolder;
                succeeded = true;
                chosenFiles = chosenFolder.Files;
                break;
            }
            catch (OperationCanceledException)
            {
                MarkUnfinishedAlbumFilesCancelled(chosenFolder);

                if (!config.IgnoreAlbumFail)
                    HandleAlbumFail(job, chosenFolder, config.DeleteAlbumOnFail, config);

                if (job.Cts != null && job.Cts.IsCancellationRequested)
                {
                    job.State = JobState.Failed;
                    job.FailureReason = FailureReason.Cancelled;
                    break;
                }

                if (wasPreselected)
                    break;
            }

            if (!succeeded)
            {
                organizer.SetremoteBaseDir(null);
                if (wasPreselected)
                {
                    job.State = JobState.Failed;
                    job.FailureReason = FailureReason.AllDownloadsFailed;
                    break;
                }

                job.Results.RemoveAt(index);
            }
        }

        if (succeeded && chosenFiles != null)
        {
            job.State = JobState.Done;

            var downloadedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.State == JobState.Done && !string.IsNullOrEmpty(af.DownloadPath));

            if (downloadedAudio.Any())
            {
                job.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(af => af.DownloadPath!));
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, job.DownloadPath);
                // Note: album jobs have no parent extractor reference here; RemoveTrackFromSource
                // for albums is handled at the JobList fan-out level if needed.
            }
        }
        else if (index != -1 && job.State != JobState.Failed)
        {
            job.State = JobState.Failed;
            job.FailureReason = FailureReason.NoSuitableFileFound;
        }

        if (job.FailureReason == FailureReason.Cancelled)
        {
            var cancelledFolder = job.ResolvedTarget
                ?? lastChosenFolder;

            if (cancelledFolder != null)
                MarkUnfinishedAlbumFilesCancelled(cancelledFolder);
        }

        List<SongJob>? additionalImages = null;

        if (config.Output.AlbumArtOnly || (succeeded && config.Output.AlbumArtOption != AlbumArtOption.Default))
        {
            Logger.Info("Downloading additional images:");
            additionalImages = await DownloadImages(job, ctx, organizer, job.ResolvedTarget);

            if (chosenFiles != null && additionalImages?.Count > 0)
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
        }

        if (chosenFiles != null && !string.IsNullOrEmpty(job.DownloadPath))
            organizer.OrganizeAlbum(job, chosenFiles, additionalImages);


        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await OnCompleteExecutor.ExecuteAsync(job, null, ctx);
    }

    void MarkUnfinishedAlbumFilesCancelled(AlbumFolder folder)
    {
        foreach (var song in folder.Files.Where(song => song.State is not (JobState.Done or JobState.AlreadyExists or JobState.Failed)))
        {
            song.State = JobState.Failed;
            song.FailureReason = FailureReason.Cancelled;
        }
    }


    // ── single-song download ──────────────────────────────────────────────────

    async Task DownloadSong(SongJob song, Job job, DownloadSettings config, FileManager organizer,
        CancellationTokenSource cts, bool cancelOnFail, bool organize)
    {
        if (song.State != JobState.Pending) return;

        int tries = config.Transfer.UnknownErrorRetries;
        string savedFilePath = "";

        while (tries > 0)
        {
            await _clientManager.WaitUntilReadyAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                (savedFilePath, _) = await SearchAndDownloadSong(song, job, config, organizer, cts);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    Logger.DebugError($"{ex}");
                else
                    Logger.Debug($"Cancelled: {song}");

                if (!_clientManager.IsConnectedAndLoggedIn)
                {
                    continue;
                }
                else if (ex is SearchAndDownloadException sdEx)
                {
                    song.State = JobState.Failed;
                    song.FailureReason = sdEx.Reason;

                    if (cancelOnFail)
                    {
                        cts.Cancel();
                        throw new OperationCanceledException();
                    }
                }
                else if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    song.State = JobState.Failed;
                    song.FailureReason = FailureReason.Cancelled;
                    throw;
                }
                else
                {
                    tries--;
                    continue;
                }
            }

            break;
        }

        if (tries == 0)
        {
            song.State = JobState.Failed;
            song.FailureReason = FailureReason.AllDownloadsFailed;
            if (cancelOnFail)
            {
                cts.Cancel();
                throw new OperationCanceledException();
            }
        }

        if (savedFilePath.Length > 0)
        {
            song.State = JobState.Done;
            song.DownloadPath = savedFilePath;

        }

        if (song.State == JobState.Done && organize)
            organizer.OrganizeSong(song);

        var jobCtx2 = Ctx(job);
        if (job.Config.HasOnComplete)
        {
            Events.RaiseOnCompleteStart(song);
            await OnCompleteExecutor.ExecuteAsync(job, song, jobCtx2);
            Events.RaiseOnCompleteEnd(song);
        }

    }


    /// <summary>
    /// Searches for candidates for <paramref name="song"/> then downloads the best one.
    /// Returns (savedFilePath, chosenFile).
    /// Throws <see cref="SearchAndDownloadException"/> on unrecoverable search/download failures.
    /// </summary>
    async Task<(string, SlFile?)> SearchAndDownloadSong(SongJob song, Job job, DownloadSettings config,
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

                Task<(string path, FileCandidate? candidate)>? fastDownloadTask = null;

                var searchTask = searcher!.SearchSong(song, config.Search, responseData, searchCts.Token,
                    onFastSearchCandidate: fc =>
                    {
                        if (fastDownloadTask == null)
                        {
                            Logger.Debug($"Fast-search: starting provisional download from {fc.Username}");
                            string outputPath = organizer.GetSavePath(fc.Filename);
                            fastDownloadTask = downloader!
                                .DownloadFile(fc, outputPath, song, config.Transfer, config.Output.ParentDir, searchCts.Token)
                                .ContinueWith(t =>
                                {
                                    if (t.IsCompletedSuccessfully)
                                        return (outputPath, (FileCandidate?)fc);
                                    return ("", (FileCandidate?)null);
                                }, TaskScheduler.Default);
                        }
                    });

                // Wait for whichever finishes first.
                var neverComplete = new TaskCompletionSource<(string, FileCandidate?)>();
                await Task.WhenAny(fastDownloadTask ?? neverComplete.Task, searchTask);

                if (fastDownloadTask?.IsCompletedSuccessfully == true)
                {
                    var (fastPath, fastCandidate) = fastDownloadTask.Result;
                    if (fastPath.Length > 0 && fastCandidate != null)
                    {
                        // Fast download won — cancel the search. SearchSong will throw
                        // OperationCanceledException and release the concurrency slot internally.
                        searchCts.Cancel();
                        try { await searchTask; } catch (OperationCanceledException) { }
                        _registry.UserSuccessCounts.AddOrUpdate(fastCandidate.Username, 1, (_, c) => c + 1);
                        Logger.Debug("Fast-search: provisional download succeeded");
                        return (fastPath, fastCandidate.File);
                    }
                    // Fast download failed — fall through, wait for full search results.
                    Logger.Debug("Fast-search: provisional download failed, waiting for full search");
                }

                await searchTask;
            }
        }

        var candidates = song.Candidates;

        if (candidates == null || candidates.Count == 0)
        {
            throw new NoSuitableFileFoundException();
        }

        // Try candidates in order until one succeeds.
        int tried = 0;
        foreach (var candidate in candidates)
        {
            tried++;
            string outputPath = organizer.GetSavePath(candidate.Filename);

            try
            {
                song.State = JobState.Downloading;
                // ReportDownloadStart is called inside DownloadFile (via Downloader).
                await downloader!.DownloadFile(candidate, outputPath, song, config.Transfer, config.Output.ParentDir, cts.Token);
                _registry.UserSuccessCounts.AddOrUpdate(candidate.Username, 1, (_, c) => c + 1);
                return (outputPath, candidate.File);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.DebugError($"Download attempt {tried} failed: {ex.Message}");
                if (tried >= candidates.Count)
                {
                    throw new AllDownloadsFailedException();
                }
            }
        }

        throw new NoSuitableFileFoundException();
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
                Logger.Info("Building music directory index..");
                jobCtx.MusicDirSkipper.BuildIndex();
            }
            jobCtx.MusicDirSkipper.SongExists(song, skipCtx, out path);
        }

        if (path != null)
        {
            song.State = JobState.AlreadyExists;
            song.DownloadPath = path;
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
                    Logger.Info("Building music directory index..");
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
            job.State = JobState.Skipped;
            if (job is AlbumJob albumJob)
                albumJob.DownloadPath = path;
            ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, path);
        }

        return path != null;
    }

    bool TrySetNotFoundLastTime(SongJob song, M3uEditor? indexEditor)
    {
        if (indexEditor == null) return false;
        var prev = indexEditor.PreviousRunResult(song);
        if (prev == null) return false;
        if (prev.FailureReason == FailureReason.NoSuitableFileFound || prev.State == JobState.NotFoundLastTime)
        {
            song.State = JobState.NotFoundLastTime;
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
            job.State = job is SongJob ? JobState.NotFoundLastTime : JobState.Skipped;
            job.FailureReason = FailureReason.NoSuitableFileFound;
            return true;
        }
        return false;
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
            Logger.LogNonConsole(Logger.LogLevel.Info, $"[{job.DisplayId}] AlbumJob: Deleting album files");
        }
        else if (!string.IsNullOrEmpty(failedAlbumPath))
        {
            if (string.IsNullOrEmpty(outputParentDir))
                throw new InvalidOperationException("Cannot move failed album files because Output.ParentDir is not set.");

            Events.RaiseJobStatus(job, $"moving to {failedAlbumPath}");
            Logger.LogNonConsole(Logger.LogLevel.Info, $"[{job.DisplayId}] AlbumJob: Moving album files to {failedAlbumPath}");
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
                Logger.Error($"Error: Unable to move or delete file '{af.DownloadPath}' after album fail: {e}");
            }
        }

        if (deleteDownloaded)
            Events.RaiseJobStatus(job, "deleted files");
        else if (!string.IsNullOrEmpty(failedAlbumPath))
            Events.RaiseJobStatus(job, $"moved to {failedAlbumPath}");
    }


    // ── folder retrieval ──────────────────────────────────────────────────────

    public async Task<int> ProcessFolderRetrieval(AlbumFolder folder, Job parentJob, string? customMessage = null)
    {
        if (folder.IsFullyRetrieved)
            return 0;

        var rfJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath, WorkflowId = parentJob.WorkflowId, Config = parentJob.Config };
        RegisterJob(rfJob, parentJob);
        rfJob.State = JobState.Searching;
        rfJob.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, parentJob.Cts!.Token);

        int count = 0;
        try
        {
            count = await searcher!.CompleteFolder(rfJob.TargetFolder, rfJob.Cts.Token);
            rfJob.NewFilesFoundCount = count;
            rfJob.State = JobState.Done;
            return count;
        }
        catch (OperationCanceledException)
        {
            // Suppress upward exception and return 0 so the parent job doesn't fail
            rfJob.State = JobState.Failed;
            rfJob.FailureReason = FailureReason.Cancelled;
            Events.RaiseJobStatus(rfJob, "cancelled");
            Logger.LogNonConsole(Logger.LogLevel.Info, $"[{rfJob.DisplayId}] RetrieveFolderJob: Cancelled folder retrieval for {folder.FolderPath}");
            return 0;
        }
        finally
        {
            rfJob.Discovery = new DiscoverySummary { ResultCount = count, LockedFileCount = 0 };
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
        { Logger.Info("No images found"); return result; }

        if (imageFolders.Count == 1 && imageFolders[0].All(af => af.State != JobState.Pending))
        { Logger.Info("No additional images found"); return result; }

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
                Logger.Info("Image requirements already satisfied.");
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

    async Task DownloadEmbeddedSong(
        SongJob song,
        Job parentJob,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource groupCts,
        bool cancelGroupOnFail,
        bool organize)
    {
        if (song.State != JobState.Pending) return;

        song.WorkflowId = parentJob.WorkflowId;
        song.Config = config;
        song.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, groupCts.Token);
        RegisterJob(song, parentJob);

        try
        {
            await DownloadSong(song, parentJob, config, organizer, song.Cts, cancelOnFail: cancelGroupOnFail, organize);
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested
            && song.Cts.IsCancellationRequested
            && song.FailureReason == FailureReason.Cancelled)
        {
            // User cancelled only this embedded song; keep the album/aggregate parent running.
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


    // ── update / stale-detection loop ─────────────────────────────────────────

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
                            Logger.Debug($"Cancelling stale download: {song}");
                            song.State = JobState.Failed;
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
                Logger.Error($"Error in update loop: {ex.Message}");
                try { await Task.Delay(1000, cancellationToken); } catch { break; }
            }
        }
    }
}
