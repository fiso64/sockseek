using Sockseek.Api;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core.Models;
using Sockseek.Core.Transfers.Downloads;
using Sockseek.Core.Extractors;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Planning;


namespace Sockseek.Core;

internal sealed class DiscoveryCoordinator
{
    private readonly DownloadExecutionContext context;
    private readonly JobOrchestrator jobs;
    private readonly ILogger<DiscoveryCoordinator> logger;

    public DiscoveryCoordinator(DownloadExecutionContext context, JobOrchestrator jobs)
    {
        this.context = context;
        this.jobs = jobs;
        logger = context.LoggerFactory.CreateLogger<DiscoveryCoordinator>();
    }
    public async Task<ExtractJobResult> ProcessExtractJob(ExtractJob job, Job? parentJob, CancellationToken parentToken)
    {
        if (job.HasPlannedExtraction)
        {
            if (job.PlannedExtractionFailure is { } failure)
            {
                return new(
                    DownloadOutcomes.ExtractionFailed(
                        new InvalidOperationException(failure)),
                    null,
                    null);
            }

            Job planned = job.Result
                ?? throw new InvalidOperationException(
                    "A successfully planned extraction has no retained result.");
            PublishExtractedResult(job, planned, parentJob);
            return new(JobOutcome.Done(), planned, null);
        }

        JobExtractionResult plan;
        try
        {
            plan = await context.Runtime.WithExtractorSlot(job.Cts!.Token, async () =>
            {
                job.UpdateActivity(JobActivityPhase.Extracting);
                job.Cts.Token.ThrowIfCancellationRequested();
                return await JobExtraction.ExtractAsync(
                    job,
                    ExtractorContext.ForJob(
                        job,
                        context.Events,
                        (job.InputType ?? InputType.None).ToInputName(),
                        context.SensitiveOutput),
                    job.Cts.Token).ConfigureAwait(false);
            });
        }
        catch (OperationCanceledException) when (jobs.IsJobCancellationRequested(job, parentToken))
        {
            return new(JobOutcome.Cancelled(jobs.CancellationSourceFor(job, parentToken)), null, null);
        }
        catch (Exception e)
        {
            return new(DownloadOutcomes.ExtractionFailed(e), null, null);
        }

        Job extracted = plan.Result;
        PublishExtractedResult(job, extracted, parentJob);
        return new(JobOutcome.Done(), extracted, plan.Extractor);
    }

    void PublishExtractedResult(ExtractJob job, Job extracted, Job? parentJob)
    {
        var allSongs = (extracted is JobList list
            ? list.AllSongs()
            : extracted is SongJob song
                ? new[] { song }.AsEnumerable()
                : Enumerable.Empty<SongJob>()).ToList();
        DownloadLogMessages.JobDecision(
            logger,
            job.Id,
            "extraction-completed",
            allSongs.Count);
        if (allSongs.Count > 0)
            context.Events.RaiseTrackListReady(allSongs);

        var newContexts = JobPreparer.PrepareSubtree(extracted, job.Config, context.JobSettingsResolver, parentJob as JobList, context.Ctx(job), indexes: context.Runtime.Files.Indexes);
        context.Contexts.SetPrepared(extracted, newContexts, inheritFrom: job);

        EnsureDisplayIdsForExecutableSubtree(extracted);
        context.ObservePreparedAutoProfiles(extracted);
        context.Events.RaiseJobResultCreated(job, extracted);
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

            case SongGroupsJob group:
                foreach (var song in group.Songs)
                    EnsureDisplayIdsForExecutableSubtree(song);
                break;

            case AlbumGroupsJob group:
                foreach (var album in group.Albums)
                    EnsureDisplayIdsForExecutableSubtree(album);
                break;

            case AlbumJob album:
                foreach (var song in album.TrackJobs)
                    EnsureDisplayIdsForExecutableSubtree(song);
                break;
        }
    }

    public sealed record ExtractJobResult(JobOutcome Outcome, Job? Result, IExtractor? Extractor);

    public async Task<JobOutcome> ProcessSearchJob(SearchJob job, CancellationToken parentToken)
    {
        var searchFailure = await TrySearchWithReconnect(job, parentToken,
            () => context.Runtime.Searcher.Search(job, job.Config.Search, job.Cts!.Token, completeSessionOnError: false));
        if (searchFailure != null)
            return searchFailure;

        var outcome = JobOutcome.Done();
        JobOutcomeCommitter.Commit(job, outcome);
        return outcome;
    }

    async Task RunSearchWithReconnect(CancellationToken ct, Func<Task> searchAction)
    {
        while (true)
        {
            await context.ClientManager.WaitUntilReadyAsync(ct);

            try
            {
                await searchAction();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (!context.ClientManager.IsConnectedAndLoggedIn)
            {
            }
        }
    }

    async Task<JobOutcome?> TrySearchWithReconnect(Job job, CancellationToken parentToken, Func<Task> searchAction)
    {
        try
        {
            await RunSearchWithReconnect(job.Cts!.Token, searchAction);
            return null;
        }
        catch (OperationCanceledException) when (jobs.IsJobCancellationRequested(job, parentToken))
        {
            var outcome = JobOutcome.Cancelled(jobs.CancellationSourceFor(job, parentToken));
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }
        catch (Exception e)
        {
            if (job is SearchJob searchJob)
                searchJob.Session.Complete();

            var outcome = DownloadOutcomes.ExceptionFailure(JobFailureReason.Other, e);
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }
    }

    public async Task<JobOutcome> ProcessRetrieveFolderJob(RetrieveFolderJob job, CancellationToken parentToken)
    {
        try
        {
            await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);
            job.UpdateActivity(JobActivityPhase.RetrievingFolder);
            job.Result = await context.Runtime.Searcher.RetrieveDirectory(job.Directory, job.Cts!.Token);
            job.NewFilesFoundCount = job.ResultObserver?.Invoke(job.Result, job.Cts.Token)
                ?? job.Result.Files.Count;
            job.RetrievalOutcome = FolderRetrievalOutcome.Completed;
            job.Discovery = new DiscoverySummary { RawResultCount = job.NewFilesFoundCount, LockedFileCount = 0 };
            var outcome = JobOutcome.Done();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (jobs.IsJobCancellationRequested(job, parentToken))
        {
            job.Discovery = new DiscoverySummary { RawResultCount = 0, LockedFileCount = 0 };
            job.RetrievalOutcome = FolderRetrievalOutcome.Cancelled;
            var outcome = JobOutcome.Cancelled(jobs.CancellationSourceFor(job, parentToken));
            JobOutcomeCommitter.Commit(job, outcome);
            context.Events.RaiseJobStatus(job, "cancelled");
            return outcome;
        }
        catch (Exception e)
        {
            return CommitFolderRetrievalFailure(job, e);
        }
    }

    public async Task<JobOutcome?> ProcessSongDiscovery(SongJob job, CancellationToken parentToken)
    {
        var config = job.Config;

        if (config.PrintResults)
        {
            var searchFailure = await TrySearchWithReconnect(job, parentToken,
                () => context.Runtime.Searcher.SearchSong(job, config.Search, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;

            var outcome = job.Candidates?.Count > 0
                ? JobOutcome.Done()
                : DownloadOutcomes.NoMatchingDiscovery(job, "song candidates");
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior != DownloadBehavior.Manual || job.ResolvedPeerTarget != null)
            return null;

        if (job.Candidates == null)
        {
            var searchFailure = await TrySearchWithReconnect(job, parentToken,
                () => context.Runtime.Searcher.SearchSong(job, config.Search, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;
        }

        job.Discovery ??= new DiscoverySummary();

        var manualOutcome = job.Candidates?.Count > 0
            ? JobOutcome.AwaitingSelection()
            : DownloadOutcomes.NoMatchingDiscovery(job, "song candidates");
        JobOutcomeCommitter.Commit(job, manualOutcome);
        return manualOutcome;
    }

    public async Task<JobOutcome?> ProcessAlbumDiscovery(AlbumJob job, JobContext ctx, CancellationToken parentToken)
    {
        await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;

        if (job.ResolvedTarget != null)
        {
            if (job.Results.Count == 0)
                job.Results = [job.ResolvedTarget];

            if (job.DirectoryResolutionPolicy == AlbumDirectoryResolutionPolicy.RetrieveBeforeSelection)
            {
                var retrieval = await ProcessFolderRetrieval(job.ResolvedTarget, job);
                job.DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.UseSelectedSnapshot;
                if (retrieval.RetrievalCancelled || job.ResolvedTarget.Files.Count == 0)
                    job.Results.Clear();
            }
        }
        else if (job.Results.Count == 0)
        {
            var searchFailure = await TrySearchWithReconnect(job, parentToken,
                () => context.Runtime.Searcher.SearchAlbum(job, config.Search, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;
        }

        job.Discovery ??= new DiscoverySummary();

        if (job.Results.Count == 0)
        {
            var outcome = DownloadOutcomes.NoMatchingDiscovery(job, "album folders");
            outcome = await jobs.RunOnCompleteIfApplicable(job, null, context.Ctx(job), outcome);
            JobOutcomeCommitter.Commit(job, outcome);

            if (!config.PrintResults)
                context.UpdateOutputs(job, ctx, indexOnly: true);

            return outcome;
        }

        if (!config.PrintResults
            && job.DownloadBehavior == DownloadBehavior.Manual
            && job.ResolvedTarget == null)
        {
            var outcome = JobOutcome.AwaitingSelection();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        return null;
    }

    public async Task<JobOutcome?> ProcessGroupDiscovery(SongGroupsJob job, JobContext ctx, CancellationToken parentToken)
    {
        await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var searchFailure = await TrySearchWithReconnect(job, parentToken,
            () => context.Runtime.Searcher.SearchSongGroups(job, config.Search, job.Cts!.Token));
        if (searchFailure != null)
            return searchFailure;

        if (job.Songs.Count == 0)
        {
            var outcome = DownloadOutcomes.NoMatchingDiscovery(job, "group track candidates");
            JobOutcomeCommitter.Commit(job, outcome);

            if (!config.PrintResults)
                context.UpdateOutputs(job, ctx, indexOnly: true);

            return outcome;
        }

        if (!config.PrintResults && job.DownloadBehavior == DownloadBehavior.Manual)
        {
            var outcome = JobOutcome.AwaitingSelection();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (config.Skip.SkipExisting)
        {
            var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.ResolveSearchSettings());
            foreach (var song in job.Songs)
                await context.SkipEvaluation.TrySetAlreadyExistsAsync(job, song, skipCtx);
        }

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        return null;
    }

    public async Task<JobOutcome> ProcessAlbumGroupsDiscovery(AlbumGroupsJob job, JobContext ctx, CancellationToken parentToken)
    {
        await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var searchFailure = await TrySearchWithReconnect(job, parentToken,
            () => context.Runtime.Searcher.SearchAlbumGroups(job, config.Search, job.Cts!.Token));
        if (searchFailure != null)
            return searchFailure;

        var albums = job.Albums;
        foreach (var album in albums)
            album.DownloadBehaviorPolicy = job.DownloadBehaviorPolicy;

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (albums.Count == 0)
        {
            var outcome = DownloadOutcomes.NoMatchingDiscovery(job, "album groups candidates");
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior == DownloadBehavior.Manual)
        {
            var outcome = JobOutcome.AwaitingSelection();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        foreach (var album in albums)
            album.ItemName ??= job.ItemName;
        var albumList = context.PrepareGroupChildren(job, albums, ctx);

        job.UpdateActivity(JobActivityPhase.RunningChildren);
        await jobs.ProcessJob(albumList, job.Cts!.Token, job);

        var finalOutcome = DeriveAlbumGroupsOutcome(job, albumList);
        JobOutcomeCommitter.Commit(job, finalOutcome);
        return finalOutcome;
    }

    static JobOutcome DeriveAlbumGroupsOutcome(AlbumGroupsJob job, JobList albumList)
    {
        bool anySuccessful = job.Albums.Any(JobOrchestrator.IsSubtreeSuccessful);
        bool anyCancelled = job.Albums.Any(JobOrchestrator.HasCancelledDescendant);
        bool anyUnsuccessful = job.Albums.Any(JobOrchestrator.IsSubtreeUnsuccessful);

        if (anySuccessful && (anyCancelled || anyUnsuccessful))
            return JobOutcome.PartialSuccess(
                "Some generated albums completed and some failed or were cancelled.",
                anyCancelled ? JobOrchestrator.CancellationSourceForDerivedCancellation(job, albumList) : JobCancellationSource.None);

        if (job.Cts?.IsCancellationRequested == true || albumList.FailureReason == JobFailureReason.Cancelled || anyCancelled)
            return JobOutcome.Cancelled(JobOrchestrator.CancellationSourceForDerivedCancellation(job, albumList));

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

        var unfinishedAlbum = job.Albums.FirstOrDefault(album => !JobOrchestrator.IsSubtreeSuccessful(album));
        if (unfinishedAlbum != null)
            return JobOutcome.Failed(JobFailureReason.Other, $"Generated album did not finish successfully: {unfinishedAlbum}");

        return JobOutcome.Done();
    }

    // ── folder retrieval ──────────────────────────────────────────────────────

    public async Task<RetrieveFolderJob> ProcessFolderRetrieval(
        AlbumFolder folder,
        Job parentJob,
        string? customMessage = null,
        bool consumeJobSlot = true)
    {
        var rfJob = new RetrieveFolderJob(folder.DirectoryIdentity)
        {
            WorkflowId = parentJob.WorkflowId,
            Config = parentJob.Config,
        };
        if (parentJob is AlbumJob album && !string.IsNullOrWhiteSpace(album.Query.Album))
            rfJob.ItemName = $"Retrieve {album.Query.Album}";

        if (folder.IsFullyRetrieved)
        {
            DownloadLogMessages.JobDecision(
                logger,
                parentJob.Id,
                "folder already retrieved",
                folder.Files.Count);
            rfJob.Result = folder.Directory;
            rfJob.RetrievalOutcome = FolderRetrievalOutcome.Completed;
            return rfJob;
        }

        context.RegisterForExecution(rfJob, parentJob.Cts!.Token, parentJob);
        var retrievalToken = rfJob.Cts!.Token;
        var parentActivityBeforeRetrieval = parentJob.ActivityPhase;
        var parentActivityUntilBeforeRetrieval = parentJob.ActivityUntilUtc;
        if (!parentJob.IsTerminal)
            parentJob.UpdateActivity(JobActivityPhase.RetrievingFolder);
        rfJob.UpdateActivity(JobActivityPhase.RetrievingFolder);
        DownloadLogMessages.JobDecision(
            logger,
            rfJob.Id,
            "folder retrieval started");

        int count = 0;
        try
        {
            async Task<int> CompleteFolder()
            {
                rfJob.UpdateActivity(JobActivityPhase.RetrievingFolder);
                var snapshot = await context.Runtime.Searcher.RetrieveDirectory(rfJob.Directory, retrievalToken);
                rfJob.Result = snapshot;
                if (parentJob is SearchJob searchJob) searchJob.RecordRetrievedDirectory(snapshot);
                return Searcher.ApplyDirectorySnapshot(folder, snapshot);
            }

            count = consumeJobSlot
                ? await context.Runtime.WithJobSlot(retrievalToken, CompleteFolder)
                : await CompleteFolder();
            rfJob.NewFilesFoundCount = count;
            rfJob.RetrievalOutcome = FolderRetrievalOutcome.Completed;
            JobOutcomeCommitter.Commit(rfJob, JobOutcome.Done());
            DownloadLogMessages.JobDecision(
                logger,
                rfJob.Id,
                "folder retrieval completed",
                count);
            return rfJob;
        }
        catch (OperationCanceledException) when (jobs.IsJobCancellationRequested(rfJob, parentJob.Cts!.Token))
        {
            // Suppress upward exception so cancelling this retrieval job doesn't cancel its parent.
            rfJob.RetrievalOutcome = FolderRetrievalOutcome.Cancelled;
            JobOutcomeCommitter.Commit(rfJob, JobOutcome.Cancelled(jobs.CancellationSourceFor(rfJob, parentJob.Cts!.Token)));
            context.Events.RaiseJobStatus(rfJob, "cancelled");
            context.Events.RaiseJobMessage(
                rfJob,
                LogLevel.Information,
                null,
                "folder retrieval cancelled");
            return rfJob;
        }
        catch (Exception e)
        {
            CommitFolderRetrievalFailure(rfJob, e);
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
            context.Events.RaiseJobExecutionCompleted(rfJob);
        }
    }

    private JobOutcome CommitFolderRetrievalFailure(RetrieveFolderJob job, Exception exception)
    {
        job.Discovery = new DiscoverySummary { RawResultCount = 0, LockedFileCount = 0 };
        job.RetrievalOutcome = FolderRetrievalOutcome.Failed;
        var outcome = DownloadOutcomes.ExceptionFailure(JobFailureReason.Other, exception);
        JobOutcomeCommitter.Commit(job, outcome);
        DownloadLogMessages.FolderCompletionFailed(
            logger,
            exception,
            LogIdentity.Hash(job.Directory.Username + "\0" + job.Directory.FolderPath));
        return outcome;
    }

}
