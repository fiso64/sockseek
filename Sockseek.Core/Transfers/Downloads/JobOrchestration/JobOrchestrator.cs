using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Transfers.Downloads.Queueing;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Transfers.Downloads.ManualSelection;
using Sockseek.Core.Transfers.Downloads.Reporting;
using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Transfers.Downloads.Skipping;
using Sockseek.Core.Transfers.Downloads.SourceMutations;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Sockseek.Core;

internal sealed class JobOrchestrator
{
    private readonly DownloadExecutionContext context;
    private readonly Func<AlbumJob, Task> finalizeManualSelectionForAlbum;
    private readonly DiscoveryCoordinator discovery;
    private readonly DownloadExecutorCoordinator download;

    public JobOrchestrator(DownloadExecutionContext context, Func<AlbumJob, Task> finalizeManualSelectionForAlbum)
    {
        this.context = context;
        this.finalizeManualSelectionForAlbum = finalizeManualSelectionForAlbum;
        discovery = new DiscoveryCoordinator(context, this);
        download = new DownloadExecutorCoordinator(context, this);
    }
    // ── recursive job processor ───────────────────────────────────────────────

    public async Task ProcessRootJob(Job rootJob)
    {
        try
        {
            await ProcessJob(rootJob);
        }
        finally
        {
            context.EmitAutoProfileFinalSummary(rootJob);
        }
    }

    public async Task<RetrieveFolderJob> ProcessFolderRetrieval(
        AlbumFolder folder,
        Job parentJob,
        string? statusMessage = null,
        bool consumeJobSlot = true)
        => await discovery.ProcessFolderRetrieval(folder, parentJob, statusMessage, consumeJobSlot);

    public async Task<JobOutcome> RunOnCompleteIfApplicable(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
        => await download.RunOnCompleteIfApplicable(job, song, ctx, outcome);

    public async Task ProcessJob(Job job, CancellationToken parentToken = default, Job? parentJob = null)
    {
        context.RegisterJob(job, parentJob);
        bool executionCompletedRaised = false;

        void RaiseJobExecutionCompleted()
        {
            if (executionCompletedRaised)
                return;

            executionCompletedRaised = true;
            context.Events.RaiseJobExecutionCompleted(job);
        }

        // Create a per-job CTS linked to both the engine-wide appCts and the parent job's token
        // (if any). Cancelling this job propagates to all descendants; cancelling the parent
        // propagates here automatically. ExtractJob passes parentToken (not its own token) when
        // recursing into its Result so that the Result is a sibling, not a child, in the hierarchy.
        job.Cts = CancellationTokenSource.CreateLinkedTokenSource(context.Runtime.Token, parentToken);

        SockseekLog.Jobs.Trace($"ProcessJob: Starting job {job.DisplayId} ({job.GetType().Name})");
        try
        {
            // ── ExtractJob: run extractor, set Result, recurse ───────────────────
            if (job is ExtractJob ej)
            {
                var extractResult = await discovery.ProcessExtractJob(ej, parentJob, parentToken);
                JobOutcomeCommitter.Commit(ej, extractResult.Outcome);

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

                var extractCtx = context.Ctx(ej);
                extractCtx.IndexEditor?.Update();
                extractCtx.PlaylistEditor?.Update();

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
                await ProcessJobList(jl, parentToken, parentJob);
                return;
            }

            // ── Leaf jobs: skip checks, search, download ─────────────────────────
            await ProcessLeafJob(job, parentToken, parentJob);
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

            if (job is AlbumJob albumJob)
                await finalizeManualSelectionForAlbum(albumJob);
        }
    }

    internal bool IsJobCancellationRequested(Job job, CancellationToken parentToken)
        => context.Runtime.IsCancellationRequested
            || parentToken.IsCancellationRequested
            || job.Cts?.IsCancellationRequested == true;

    internal JobCancellationSource CancellationSourceFor(Job job, CancellationToken parentToken)
    {
        if (job.CancellationSource != JobCancellationSource.None)
            return job.CancellationSource;
        if (context.Runtime.IsCancellationRequested)
            return JobCancellationSource.UserRequestedAllJobs;
        if (parentToken.IsCancellationRequested)
            return JobCancellationSource.ParentJob;

        return JobCancellationSource.InternalEngine;
    }

    internal static JobCancellationSource CancellationSourceForDerivedCancellation(Job job, params Job?[] relatedJobs)
    {
        if (job.CancellationSource != JobCancellationSource.None)
            return job.CancellationSource;

        var source = ChooseDerivedCancellationSource(relatedJobs.SelectMany(CancellationSourcesFromSubtree));
        if (source != JobCancellationSource.None)
            return source;

        return JobCancellationSource.InternalEngine;
    }

    static JobCancellationSource ChooseDerivedCancellationSource(IEnumerable<JobCancellationSource> sources)
    {
        var best = JobCancellationSource.None;
        var bestRank = 0;
        foreach (var source in sources)
        {
            var rank = DerivedCancellationSourceRank(source);
            if (rank > bestRank)
            {
                best = source;
                bestRank = rank;
            }
        }

        return best;
    }

    static int DerivedCancellationSourceRank(JobCancellationSource source) => source switch
    {
        JobCancellationSource.UserRequestedAllJobs => 50,
        JobCancellationSource.UserRequestedWorkflow => 40,
        JobCancellationSource.UserRequestedJob => 30,
        JobCancellationSource.InternalEngine => 20,
        JobCancellationSource.ParentJob => 10,
        _ => 0,
    };

    static IEnumerable<JobCancellationSource> CancellationSourcesFromSubtree(Job? job)
    {
        if (job == null)
            yield break;

        if (job.CancellationSource != JobCancellationSource.None)
        {
            yield return job.CancellationSource;
            yield break;
        }

        switch (job)
        {
            case JobList list:
                foreach (var source in list.Jobs.SelectMany(CancellationSourcesFromSubtree))
                    yield return source;
                break;

            case AlbumJob album:
                foreach (var source in album.TrackJobs.SelectMany(CancellationSourcesFromSubtree))
                    yield return source;
                break;

            case AggregateJob aggregate:
                foreach (var source in aggregate.Songs.SelectMany(CancellationSourcesFromSubtree))
                    yield return source;
                break;

            case AlbumAggregateJob aggregate:
                foreach (var source in aggregate.Albums.SelectMany(CancellationSourcesFromSubtree))
                    yield return source;
                break;

            case ExtractJob extract:
                foreach (var source in CancellationSourcesFromSubtree(extract.Result))
                    yield return source;
                break;
        }
    }

    static void MarkCancelledIfActive(Job job, JobCancellationSource source)
    {
        if (!job.IsTerminal)
        {
            JobOutcomeCommitter.Commit(job, JobOutcome.Cancelled(source));
        }
    }

    internal static void ApplyDownloadBehaviorPolicy(Job job, DownloadBehaviorPolicy policy)
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

    public async Task ProcessJobList(JobList jl, CancellationToken parentToken, Job? parentJob)
    {
        var ctx = context.Contexts.TryGetValue(jl.Id, out var c) ? c : null;
        var config = jl.Config!;
        jl.UpdateActivity(JobActivityPhase.RunningChildren);
        var childParentJob = parentJob is AggregateJob ? parentJob : jl;
        var batchOwner = parentJob is AggregateJob ? parentJob : jl;

        if (ctx?.PreprocessTracks == true)
        {
            Preprocessor.PreprocessJob(jl, config.Preprocess);
            JobPreparer.ApplySearchSettings(jl, config.Search);
        }

        if (config.PrintJobs)
        {
            await Task.WhenAll(jl.Jobs.ToList().Select(child => ProcessJob(child, jl.Cts!.Token, childParentJob)));
            SetJobListTerminalState(jl, parentToken);
            return;
        }

        // ── skip checks for direct SongJob children ──────────────────────
        var directSongs = jl.Jobs.OfType<SongJob>().ToList();
        var skipEligibleDirectSongs = directSongs
            .Where(song => !song.Config.PrintJobs && !song.Config.PrintResults)
            .ToList();
        var existing = new List<SongJob>();
        var notFound = new List<SongJob>();

        if (skipEligibleDirectSongs.Count > 0)
        {
            foreach (var song in skipEligibleDirectSongs)
            {
                var songCtx = context.Ctx(song);
                var songConfig = song.Config;

                if (songConfig.Skip.SkipNotFound
                    && context.SkipEvaluation.TrySetNotFoundLastTime(song, songCtx.IndexEditor))
                {
                    notFound.Add(song);
                    continue;
                }

                if (songConfig.Skip.SkipExisting
                    && song.LifecycleState == JobLifecycleState.Pending
                    && context.SkipEvaluation.TrySetAlreadyExists(batchOwner, song, TrackSkipperContext.From(songCtx, songConfig.Skip, songConfig.ResolveSearchSettings())))
                {
                    existing.Add(song);
                }
            }

            context.Events.RaiseTrackBatchResolved(batchOwner,
                skipEligibleDirectSongs.Where(s => s.LifecycleState == JobLifecycleState.Pending).ToList(),
                existing,
                notFound);

            foreach (var song in existing)
                await MaybeRemoveFromSource(song, song.Config);
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
                var intervalReporter = context.EngineSettings.ReportIntervalProgress
                    ? new IntervalProgressReporter(TimeSpan.FromSeconds(30), 5, directSongs)
                    : null;

                await Task.WhenAll(jl.Jobs.ToList().Select(async child =>
                {
                    bool wasInitial = child is SongJob s && s.LifecycleState == JobLifecycleState.Pending;
                    await ProcessJob(child, jl.Cts!.Token, childParentJob);

                    if (wasInitial && child is SongJob song)
                    {
                        context.Ctx(song).IndexEditor?.Update();
                        context.Ctx(song).PlaylistEditor?.Update();
                        intervalReporter?.MaybeReport(song);
                        int dl = directSongs.Count(IsSubtreeSuccessful);
                        int fl = directSongs.Count(IsSubtreeUnsuccessful);
                        context.Events.RaiseOverallProgress(dl, fl, directSongs.Count);

                        await MaybeRemoveFromSource(song, song.Config);
                    }
                }));

                int dlFinal = directSongs.Count(IsSubtreeSuccessful);
                int flFinal = directSongs.Count(IsSubtreeUnsuccessful);
                context.Events.RaiseListProgress(jl, dlFinal, flFinal, directSongs.Count);
            }
            else
            {
                await Task.WhenAll(jl.Jobs.ToList().Select(child => ProcessJob(child, jl.Cts!.Token, childParentJob)));

                foreach (var child in jl.Jobs)
                    await MaybeRemoveFromSource(child, child.Config);
            }
        }
        catch (OperationCanceledException) when (jl.Cts?.IsCancellationRequested == true)
        {
        }

        SetJobListTerminalState(jl, parentToken);
    }

    internal static bool IsSubtreeSuccessful(Job? job)
    {
        if (job == null) return false;

        return job switch
        {
            JobList jl => jl.Jobs.All(IsSubtreeSuccessful),
            ExtractJob ej => ej.TerminalOutcome == JobTerminalOutcome.Succeeded && ej.Result != null && IsSubtreeSuccessful(ej.Result),
            AlbumAggregateJob aag => aag.Albums.Count > 0 ? aag.Albums.All(IsSubtreeSuccessful) : IsSuccessfulTerminal(aag),
            AggregateJob ag => ag.Songs.Count > 0 ? ag.Songs.All(IsSubtreeSuccessful) : IsSuccessfulTerminal(ag),
            _ => IsSuccessfulTerminal(job),
        };
    }

    public async Task FlushManualSelectionTerminalEffectsAsync(Job job)
    {
        if (context.Contexts.TryGetValue(job.Id, out var ctx))
        {
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
        }

        await MaybeRemoveFromSource(job, job.Config);
        context.Events.RaiseJobExecutionCompleted(job);
    }

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
            JobOutcomeCommitter.Commit(jobList, JobOutcome.PartialSuccess(
                "Some child jobs completed and some failed or were cancelled.",
                source));
            return;
        }

        if (jobList.Cts?.IsCancellationRequested == true || anyCancelled)
        {
            var source = jobList.Cts?.IsCancellationRequested == true
                ? CancellationSourceFor(jobList, parentToken)
                : CancellationSourceForDerivedCancellation(jobList, jobList);
            JobOutcomeCommitter.Commit(jobList, JobOutcome.Cancelled(source));
            return;
        }

        if (anyUnsuccessful)
        {
            JobOutcomeCommitter.Commit(jobList, JobOutcome.Failed(JobFailureReason.ChildJobsFailed, "One or more child jobs failed."));
            return;
        }

        JobOutcomeCommitter.Commit(jobList, JobOutcome.Done());
    }

    internal static bool IsSubtreeUnsuccessful(Job job)
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

    internal static bool IsSuccessfulTerminal(Job job)
        => job.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason == JobSkipReason.AlreadyExists);

    internal static bool HasCancelledDescendant(Job job)
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

    internal async Task MaybeRemoveFromSource(Job job, DownloadSettings config)
    {
        if (config.DoNotDownload) return;
        if (!config.Extraction.RemoveTracksFromSource) return;
        if (job is SearchJob or RetrieveFolderJob) return;
        if (job.SourceMutation == null) return;
        if (!IsSubtreeSuccessful(job)) return;
        await context.SourceMutations.ApplyIfNeededAsync(job, config);
    }

    async Task<JobOutcome> ProcessLeafJob(Job job, CancellationToken parentToken, Job? parentJob)
    {
        var ctx = context.Ctx(job);
        var config = job.Config;

        if (ctx.PreprocessTracks)
        {
            Preprocessor.PreprocessJob(job, config.Preprocess);
            JobPreparer.ApplySearchSettings(job, config.Search);
        }

        // ── skip checks ──────────────────────────────────────────────────────

        if (config.PrintJobs)
        {
            var outcome = JobOutcome.Done();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (config.Skip.SkipNotFound && !config.PrintResults && job.CanBeSkipped)
        {
            if (context.SkipEvaluation.TryGetNotFoundLastTimeOutcome(job) is { } outcome)
            {
                JobOutcomeCommitter.Commit(job, outcome);
                SockseekLog.Jobs.Info($"Download '{job.ToString(true)}' was not found during a prior run, skipping");
                return outcome;
            }
        }

        if (config.Skip.SkipExisting && !config.PrintResults && job.CanBeSkipped
            && context.SkipEvaluation.TryGetJobAlreadyExistsOutcome(job, ctx) is { } alreadyExistsOutcome)
        {
            if (job is SongJob existingSong)
            {
                var organizer = new FileManager(existingSong, config.Output, config.Extraction, ctx.OutputScope);
                await download.CommitAndFinalizeSong(
                    existingSong,
                    existingSong,
                    alreadyExistsOutcome,
                    ctx,
                    organizer,
                    finalizePlacement: false,
                    updateIndexes: false);
            }
            else
            {
                DownloadExecutorCoordinator.ApplyPreCommitOutcomeMetadata(job, alreadyExistsOutcome);
                var postProcessOutcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(job, alreadyExistsOutcome);
                postProcessOutcome = await download.RunOnCompleteIfApplicable(job, null, ctx, postProcessOutcome);
                JobOutcomeCommitter.Commit(job, DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(job, postProcessOutcome));
            }

            if (!string.IsNullOrEmpty(alreadyExistsOutcome.DownloadPath))
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, alreadyExistsOutcome.DownloadPath);
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return alreadyExistsOutcome;
        }

        // ── source search / download ──────────────────────────────────────────
        // Leaf jobs hold a single job slot for their entire lifetime (search + download combined).
        // Containers (AggregateJob, AlbumAggregateJob) don't hold a slot here; their children do.
        if (job is SongJob or AlbumJob or SearchJob or RetrieveFolderJob)
            return await context.Runtime.WithJobSlot(job.Cts!.Token, () => ProcessLeafJobCore(job, ctx, parentToken, parentJob));
        else
            return await ProcessLeafJobCore(job, ctx, parentToken, parentJob);
    }

    async Task<JobOutcome> ProcessLeafJobCore(Job job, JobContext ctx, CancellationToken parentToken, Job? parentJob)
    {
        var config = job.Config;

        if (job is SearchJob searchJob)
            return await discovery.ProcessSearchJob(searchJob, parentToken);

        if (job is RetrieveFolderJob retrieveFolderJob)
            return await discovery.ProcessRetrieveFolderJob(retrieveFolderJob, parentToken);

        if (job is SongJob songJob)
        {
            if (await discovery.ProcessSongDiscovery(songJob, parentToken) is { } outcome)
                return outcome;
        }

        if (job is AlbumJob albumJob)
        {
            if (await discovery.ProcessAlbumDiscovery(albumJob, ctx, parentToken) is { } outcome)
                return outcome;
        }

        if (job is AggregateJob aggregateJob)
        {
            if (await discovery.ProcessAggregateDiscovery(aggregateJob, ctx, parentToken) is { } outcome)
                return outcome;
        }

        if (job is AlbumAggregateJob albumAggregateJob)
            return await discovery.ProcessAlbumAggregateDiscovery(albumAggregateJob, ctx, parentToken);

        if (config.PrintResults)
        {
            return JobOutcome.NoChange();
        }

        return await download.ProcessLeafDownload(job, ctx, parentToken, parentJob);
    }

}
