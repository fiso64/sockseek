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
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Planning;

using Directory = System.IO.Directory;
using File = System.IO.File;

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
                    ExtractorContext.ForExtractJob(
                        job,
                        context.Events,
                        ExtractorLogSource(job.InputType ?? InputType.None),
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

        var newContexts = JobPreparer.PrepareSubtree(extracted, job.Config, context.JobSettingsResolver, parentJob as JobList, context.Ctx(job));
        foreach (var (id, ctx) in newContexts)
            context.Contexts.Set(id, extracted.WorkflowId, ctx);

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

    public sealed record ExtractJobResult(JobOutcome Outcome, Job? Result, IExtractor? Extractor);

    static string ExtractorLogSource(InputType inputType)
        => inputType.ToString();

    public async Task<JobOutcome> ProcessSearchJob(SearchJob job, CancellationToken parentToken)
    {
        var responseData = new ResponseData();
        var (outcome, searchFailure) = await TrySearchWithReconnect(job, parentToken,
            () => context.Runtime.Searcher.Search(job, job.Config.Search, responseData, job.Cts!.Token, completeSessionOnError: false));
        if (searchFailure != null)
            return searchFailure;

        JobOutcomeCommitter.Commit(job, outcome);
        return outcome;
    }

    async Task<T> RunSearchWithReconnect<T>(CancellationToken ct, Func<Task<T>> searchAction)
    {
        while (true)
        {
            await context.ClientManager.WaitUntilReadyAsync(ct);

            try
            {
                return await searchAction();
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
        catch (OperationCanceledException) when (jobs.IsJobCancellationRequested(job, parentToken))
        {
            var outcome = JobOutcome.Cancelled(jobs.CancellationSourceFor(job, parentToken));
            JobOutcomeCommitter.Commit(job, outcome);
            return (default!, outcome);
        }
        catch (Exception e)
        {
            if (job is SearchJob searchJob)
                searchJob.Session.Complete();

            var outcome = DownloadOutcomes.ExceptionFailure(JobFailureReason.Other, e);
            JobOutcomeCommitter.Commit(job, outcome);
            return (default!, outcome);
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
            var printResponseData = new ResponseData();
            var searchFailure = await TrySearchWithReconnect(job, parentToken,
                () => context.Runtime.Searcher.SearchSong(job, config.Search, printResponseData, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;

            var outcome = job.Candidates?.Count > 0
                ? JobOutcome.Done()
                : DownloadOutcomes.NoMatchingDiscovery(printResponseData, "file result", "file results", "song candidates");
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior != DownloadBehavior.Manual || job.ResolvedPeerTarget != null)
            return null;

        var responseData = new ResponseData();
        if (job.Candidates == null)
        {
            var searchFailure = await TrySearchWithReconnect(job, parentToken,
                () => context.Runtime.Searcher.SearchSong(job, config.Search, responseData, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;
        }

        job.Discovery ??= new DiscoverySummary();
        job.Discovery.LockedFileCount = responseData.lockedFilesCount;

        var manualOutcome = job.Candidates?.Count > 0
            ? JobOutcome.AwaitingSelection()
            : DownloadOutcomes.NoMatchingDiscovery(responseData, "file result", "file results", "song candidates");
        JobOutcomeCommitter.Commit(job, manualOutcome);
        return manualOutcome;
    }

    public async Task<JobOutcome?> ProcessAlbumDiscovery(AlbumJob job, JobContext ctx, CancellationToken parentToken)
    {
        await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        bool foundSomething;

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

            foundSomething = true;
        }
        else if (job.Results.Count > 0)
            foundSomething = true;
        else
        {
            var (searchOutcome, searchFailure) = await TrySearchWithReconnect(job, parentToken,
                () => context.Runtime.Searcher.SearchAlbum(job, config.Search, responseData, job.Cts!.Token));
            if (searchFailure != null)
                return searchFailure;

            if (searchOutcome != null)
            {
                JobOutcomeCommitter.Commit(job, searchOutcome);
                return searchOutcome;
            }

            foundSomething = job.Results.Count > 0;
        }
        foundSomething = job.Results.Count > 0;

        job.Discovery ??= new DiscoverySummary();
        job.Discovery.LockedFileCount = responseData.lockedFilesCount;

        if (!foundSomething)
        {
            var outcome = DownloadOutcomes.NoMatchingDiscovery(responseData, "file result", "file results", "album folders");
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

    public async Task<JobOutcome?> ProcessAggregateDiscovery(AggregateJob job, JobContext ctx, CancellationToken parentToken)
    {
        await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        var (searchOutcome, searchFailure) = await TrySearchWithReconnect(job, parentToken,
            () => context.Runtime.Searcher.SearchAggregate(job, config.Search, responseData, job.Cts!.Token));
        if (searchFailure != null)
            return searchFailure;

        if (searchOutcome != null)
        {
            JobOutcomeCommitter.Commit(job, searchOutcome);
            return searchOutcome;
        }

        bool foundSomething = job.Songs.Count > 0;

        job.Discovery ??= new DiscoverySummary();
        job.Discovery.LockedFileCount = responseData.lockedFilesCount;

        if (!foundSomething)
        {
            var outcome = DownloadOutcomes.NoMatchingDiscovery(responseData, "file result", "file results", "aggregate track candidates");
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
                context.SkipEvaluation.TrySetAlreadyExists(job, song, skipCtx);
        }

        if (config.PrintResults)
        {
            var outcome = JobOutcome.Done();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        return null;
    }

    public async Task<JobOutcome> ProcessAlbumAggregateDiscovery(AlbumAggregateJob job, JobContext ctx, CancellationToken parentToken)
    {
        await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

        var config = job.Config;
        var responseData = new ResponseData();
        var (searchResult, searchFailure) = await TrySearchWithReconnect(job, parentToken,
            () => context.Runtime.Searcher.SearchAggregateAlbum(job, config.Search, responseData, job.Cts!.Token));
        if (searchFailure != null)
            return searchFailure;

        var (newAlbumJobs, searchOutcome) = searchResult;

        if (searchOutcome != null)
        {
            JobOutcomeCommitter.Commit(job, searchOutcome);
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
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (!foundSomething)
        {
            var outcome = DownloadOutcomes.NoMatchingDiscovery(responseData, "file result", "file results", "album aggregate candidates");
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        if (job.DownloadBehavior == DownloadBehavior.Manual)
        {
            var outcome = JobOutcome.AwaitingSelection();
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }

        foreach (var aj in newAlbumJobs)
            aj.ItemName ??= job.ItemName;
        var albumList = context.PrepareAggregateChildren(job, newAlbumJobs, ctx);

        job.UpdateActivity(JobActivityPhase.RunningChildren);
        await jobs.ProcessJob(albumList, job.Cts!.Token, job);

        var finalOutcome = DeriveAlbumAggregateOutcome(job, albumList);
        JobOutcomeCommitter.Commit(job, finalOutcome);
        return finalOutcome;
    }

    static JobOutcome DeriveAlbumAggregateOutcome(AlbumAggregateJob job, JobList albumList)
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
                "folder-already-retrieved",
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
            "folder-retrieval-started",
            null);

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
                "folder-retrieval-completed",
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

}
