using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Events;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Diagnostics;

namespace Sockseek.Core;

internal sealed class SongDownloadExecutor
{
    private readonly DownloadExecutionContext context;
    private readonly JobOrchestrator jobs;
    private readonly SongDownloadFallbackRunner fallbackRunner;
    private readonly ILogger<SongDownloadExecutor> logger;

    public SongDownloadExecutor(DownloadExecutionContext context, JobOrchestrator jobs)
    {
        this.context = context;
        this.jobs = jobs;
        logger = context.LoggerFactory.CreateLogger<SongDownloadExecutor>();
        fallbackRunner = new SongDownloadFallbackRunner(context);
    }

    public async Task<JobOutcome> ProcessSongDownload(
        SongJob job,
        Job downloadOwner,
        FileManager organizer,
        CancellationToken parentToken)
    {
        var config = job.Config;

        // If ResolvedTarget is set, pre-populate Candidates so search is skipped.
        if (job.ResolvedTarget != null && job.Candidates == null)
            job.Candidates = new List<FileCandidate> { job.ResolvedTarget };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        return await DownloadSong(job, downloadOwner, config, organizer, cts, () => jobs.CancellationSourceFor(job, parentToken));
    }

    public async Task<JobOutcome> CommitAndFinalizeSong(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        JobContext jobCtx,
        FileManager organizer,
        bool finalizePlacement,
        bool updateIndexes)
    {
        DownloadExecutorCoordinator.ApplyPreCommitOutcomeMetadata(song, outcome);
        if (outcome.FailureReason != JobFailureReason.Cancelled)
            outcome = await CompleteSongBeforeCommit(song, parentJob, outcome, jobCtx, organizer, finalizePlacement);

        outcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(song, outcome);
        JobOutcomeCommitter.Commit(song, outcome);

        if (updateIndexes)
        {
            DownloadLogMessages.JobDecision(
                logger,
                song.Id,
                "updating-output-indexes",
                (jobCtx.IndexEditor is null ? 0 : 1) + (jobCtx.PlaylistEditor is null ? 0 : 1));
            context.UpdateOutputs(song, jobCtx);
        }

        return outcome;
    }

    public async Task<JobOutcome> RunOnCompleteIfApplicable(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
    {
        if (!OnCompleteExecutor.HasApplicableCommand(job, song, outcome))
            return outcome;

        var activityJob = song ?? job;
        activityJob.UpdateActivity(JobActivityPhase.RunningOnComplete);
        return await OnCompleteExecutor.ExecuteAsync(
            job,
            song,
            ctx,
            outcome,
            context.Events,
            logger);
    }

    public async Task<JobOutcome> DownloadEmbeddedSong(
        SongJob song,
        Job parentJob,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource groupCts,
        bool cancelGroupOnFail,
        bool finalizePlacement)
    {
        if (song.LifecycleState != JobLifecycleState.Pending) return JobOutcome.NoChange();

        song.WorkflowId = parentJob.WorkflowId;
        song.Config = config;
        song.Cts = CancellationTokenSource.CreateLinkedTokenSource(context.Runtime.Token, groupCts.Token);
        context.RegisterJob(song, parentJob);

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
                JobOutcomeCommitter.Commit(song, outcome);
                return outcome;
            }

            if (cancelGroupOnFail && ShouldCancelGroupOnEmbeddedOutcome(outcome))
            {
                JobOutcomeCommitter.Commit(song, outcome);
                await groupCts.CancelAsync();
                throw new OperationCanceledException();
            }

            var finalOutcome = await CommitAndFinalizeSong(
                song,
                parentJob,
                outcome,
                context.Ctx(parentJob),
                organizer,
                finalizePlacement,
                updateIndexes: false);

            if (cancelGroupOnFail && ShouldCancelGroupOnEmbeddedOutcome(finalOutcome))
            {
                await groupCts.CancelAsync();
                throw new OperationCanceledException();
            }

            return finalOutcome;
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested
            && song.Cts.IsCancellationRequested
            && song.FailureReason == JobFailureReason.Cancelled)
        {
            // User cancelled only this embedded song; keep the album/group parent running.
            return JobOutcome.Cancelled(JobOrchestrator.CancellationSourceForDerivedCancellation(song));
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested && cancelGroupOnFail)
        {
            await groupCts.CancelAsync();
            throw;
        }
        finally
        {
            context.Events.RaiseJobExecutionCompleted(song);
        }
    }

    private async Task<JobOutcome> CompleteSongBeforeCommit(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        JobContext jobCtx,
        FileManager organizer,
        bool finalizePlacement)
    {
        var finalization = await context.OutputFinalizer.FinalizeSongPlacementAsync(
            song,
            parentJob,
            outcome,
            organizer,
            finalizePlacement,
            allowOverwrite: !song.Config.Skip.SkipExisting);
        if (finalization.OrganizationException != null)
        {
            FailPendingTerminalTransfer(song, finalization.OrganizationException, TransferFailureReason.Finalization);
            return finalization.Outcome;
        }

        var postProcessOutcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(song, finalization.Outcome);
        // Organization can replace the transfer's staging path. Publish the
        // same updated path that the job commits. Album-owned placement finishes
        // later, after all of the album's files have been organized together.
        if (finalizePlacement)
            await CompletePendingTerminalTransferAsync(song, postProcessOutcome.DownloadPath);

        postProcessOutcome = await RunOnCompleteIfApplicable(parentJob, song, jobCtx, postProcessOutcome);
        DownloadExecutorCoordinator.ApplyPreCommitOutcomeMetadata(song, postProcessOutcome);

        await context.OutputFinalizer.PublishDownloadedFileCacheAsync(song, postProcessOutcome);
        return postProcessOutcome;
    }

    private async Task<JobOutcome> DownloadSong(
        SongJob song,
        Job job,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource cts,
        Func<JobCancellationSource> cancellationSource)
    {
        if (song.LifecycleState != JobLifecycleState.Pending) return JobOutcome.NoChange();

        await context.ClientManager.WaitUntilReadyAsync(cts.Token);
        cts.Token.ThrowIfCancellationRequested();

        try
        {
            return await SearchAndDownloadSong(song, job, config, organizer, cts);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            DownloadLogMessages.JobDecision(logger, song.Id, "cancelled", null);
            return JobOutcome.Cancelled(cancellationSource());
        }
        catch (Exception ex)
        {
            DownloadLogMessages.ComponentFailed(
                logger,
                ex,
                "song-download",
                song.Id);
            return JobOutcome.Failed(
                JobFailureReason.AllDownloadsFailed,
                DownloadFailureMessage(ex),
                ExceptionText.Detail(ex));
        }
    }

    /// <summary>
    /// Searches for candidates for <paramref name="song"/> then downloads the best one.
    /// Returns an explicit outcome for expected domain failures; unexpected infrastructure
    /// exceptions still bubble after the exact-file runner exhausts its transfer
    /// retry budget.
    /// </summary>
    private async Task<JobOutcome> SearchAndDownloadSong(
        SongJob song,
        Job job,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource cts)
    {
        var responseData = new ResponseData();
        bool searched = false;

        if (song.ExactTarget is { } exactTarget && song.Candidates == null)
            return await DownloadExactSongTarget(song, job, config, organizer, cts, exactTarget);

        // Skip search if candidates are pre-set (ResolvedTarget / direct download).
        if (song.Candidates == null)
        {

            if (!config.Search.FastSearch)
            {
                searched = true;
                await context.Runtime.Searcher.SearchSong(song, config.Search, responseData, cts.Token);
            }
            else
            {
                // Fast-search: start the search as a background task and race it against a
                // provisional download of the first qualifying candidate.
                // The search concurrency slot is held by SearchSong internally; cancelling
                // searchCts causes SearchSong to return and release it naturally.
                using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                int fastCandidateClaimed = 0;
                var fastDownloadStarted = new TaskCompletionSource<(
                    FileCandidate Candidate,
                    Task<ExactFileTransferOutcome?> Download)>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                searched = true;
                var searchTask = context.Runtime.Searcher.SearchSong(song, config.Search, responseData, searchCts.Token,
                    onFastSearchCandidate: fc =>
                    {
                        if (Interlocked.CompareExchange(ref fastCandidateClaimed, 1, 0) != 0)
                            return;

                        DownloadLogMessages.JobDecision(
                            logger,
                            song.Id,
                            "fast-search-provisional-download-started",
                            null);
                        async Task<ExactFileTransferOutcome?> DownloadCandidateAsync()
                        {
                            try
                            {
                                var target = await context.OutputFinalizer.GetInitialDownloadTargetAsync(config, song, organizer, fc);

                                // Use the main job CTS for the download so cancelling the search doesn't kill the download.
                                return await context.Runtime.ExactFileTransfers
                                    .DownloadFile(
                                        fc.Target,
                                        target.Path,
                                        song,
                                        config.Transfer,
                                        config.Output.ParentDir,
                                        config.Search.MaxStaleTime,
                                        ct: cts.Token,
                                        publishToDuplicateCache: target.PublishToDuplicateCache,
                                        parentJob: DownloadParentFor(song, job),
                                        deferTerminalCompletion: true,
                                        allowOverwrite: !config.Skip.SkipExisting,
                                        allowReuse: config.Skip.SkipExisting);
                            }
                            catch { return null; }
                        }
                        Task<ExactFileTransferOutcome?> download = DownloadCandidateAsync();
                        fastDownloadStarted.TrySetResult((fc, download));
                    });

                Task first = await Task.WhenAny(searchTask, fastDownloadStarted.Task);
                (FileCandidate Candidate, Task<ExactFileTransferOutcome?> Download)? provisional = null;
                if (first == fastDownloadStarted.Task)
                {
                    provisional = await fastDownloadStarted.Task;
                }
                else
                {
                    await searchTask;
                    if (fastDownloadStarted.Task.IsCompletedSuccessfully)
                        provisional = fastDownloadStarted.Task.Result;
                }

                if (provisional is { } started)
                {
                    var fastDownload = await started.Download;
                    if (fastDownload?.Status == ExactFileTransferStatus.Completed
                        && fastDownload.Result != null)
                    {
                        // Fast download won - cancel the search.
                        await searchCts.CancelAsync();
                        try { await searchTask; } catch (OperationCanceledException) { }

                        var result = fastDownload.Result;
                        context.UserSuccesses.RecordSuccess(song.WorkflowId, result.Target.Username);
                        if (result.TransferId is Guid transferId)
                        {
                            context.PendingTerminalTransfers[song.Id] = new PendingTerminalTransfer(
                                transferId,
                                result.AttemptCount,
                                result.Target,
                                result.Target.Filename,
                                result.OutputPath);
                        }
                        DownloadLogMessages.JobDecision(
                            logger,
                            song.Id,
                            "fast-search-provisional-download-succeeded",
                            null);
                        return JobOutcome.Done(result.OutputPath, started.Candidate, reused: result.Reused);
                    }

                    if (fastDownload?.Status == ExactFileTransferStatus.ManuallySkipped)
                    {
                        DownloadLogMessages.JobDecision(
                            logger,
                            song.Id,
                            "fast-search-provisional-download-skipped",
                            null);
                    }
                    else if (fastDownload?.Status == ExactFileTransferStatus.AlreadyExists)
                    {
                        await searchCts.CancelAsync();
                        try { await searchTask; } catch (OperationCanceledException) { }
                        return JobOutcome.AlreadyExists(fastDownload.Result?.OutputPath);
                    }
                    else
                    {
                        DownloadLogMessages.JobDecision(
                            logger,
                            song.Id,
                            "fast-search-provisional-download-failed",
                            null);
                    }

                    await searchTask;
                }
                else
                {
                    await searchTask;
                }
            }
        }

        // An explicit manual choice cannot fall back to another source. Keep
        // discovery options intact for a still-open group selection.
        IReadOnlyList<FileCandidate>? candidates = song.DownloadBehavior == DownloadBehavior.Manual
            && song.ResolvedTarget is { } selected ? [selected] : song.Candidates;

        if (candidates == null || candidates.Count == 0)
        {
            var fallbackOutcome = await fallbackRunner.TryRunAsync(song, config, organizer, cts.Token);
            if (fallbackOutcome != null)
                return fallbackOutcome;

            DownloadLogMessages.JobDecision(logger, song.Id, "no-suitable-candidates", 0);
            return searched
                ? DownloadOutcomes.NoMatchingDiscovery(responseData, "file result", "file results", "song candidates")
                : DownloadOutcomes.NoMatchingCandidates();
        }

        // Try candidates in order until one succeeds.
        int tried = 0;
        Exception? lastDownloadException = null;
        string? lastDownloadFailureMessage = null;
        foreach (var candidate in candidates)
        {
            tried++;
            var target = await context.OutputFinalizer.GetInitialDownloadTargetAsync(config, song, organizer, candidate);

            ExactFileTransferOutcome download;
            try
            {
                song.ActiveCandidate = candidate;
                song.UpdateActivity(JobActivityPhase.Downloading);
                // Transfer start is reported by the exact peer-file runner.
                download = await context.Runtime.ExactFileTransfers.DownloadFile(
                    candidate.Target,
                    target.Path,
                    song,
                    config.Transfer,
                    config.Output.ParentDir,
                    config.Search.MaxStaleTime,
                    ct: cts.Token,
                    publishToDuplicateCache: target.PublishToDuplicateCache,
                    parentJob: DownloadParentFor(song, job),
                    deferTerminalCompletion: true,
                    allowOverwrite: !config.Skip.SkipExisting,
                    allowReuse: config.Skip.SkipExisting);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!context.ClientManager.IsConnectedAndLoggedIn)
                    throw;

                lastDownloadException = ex;
                lastDownloadFailureMessage = DownloadFailureMessage(ex);
                DownloadLogMessages.JobDecision(
                    logger,
                    song.Id,
                    "candidate-download-failed",
                    tried);
                if (tried >= candidates.Count || tried >= config.Transfer.MaxDownloadRetries)
                {
                    return JobOutcome.Failed(
                        JobFailureReason.AllDownloadsFailed,
                        lastDownloadFailureMessage);
                }

                continue;
            }
            finally
            {
                song.ActiveCandidate = null;
            }

            if (download.Status == ExactFileTransferStatus.ManuallySkipped)
            {
                DownloadLogMessages.JobDecision(
                    logger,
                    song.Id,
                    "candidate-manually-skipped",
                    tried);
                tried--;
                continue;
            }

            if (download.Status == ExactFileTransferStatus.AlreadyExists)
                return JobOutcome.AlreadyExists(download.Result?.OutputPath);

            var result = download.Result
                ?? throw new InvalidOperationException($"Completed download outcome missing result for '{candidate.Username}\\{candidate.Filename}'.");
            context.UserSuccesses.RecordSuccess(song.WorkflowId, result.Target.Username);
            if (result.TransferId is Guid transferId)
            {
                context.PendingTerminalTransfers[song.Id] = new PendingTerminalTransfer(
                    transferId,
                    result.AttemptCount,
                    candidate.Target,
                    result.Target.Filename,
                    result.OutputPath);
            }
            DownloadLogMessages.JobDecision(
                logger,
                song.Id,
                "candidate-download-succeeded",
                tried);
            return JobOutcome.Done(result.OutputPath, candidate, reused: result.Reused);
        }

        if (lastDownloadException != null)
            return JobOutcome.Failed(
                JobFailureReason.AllDownloadsFailed,
                lastDownloadFailureMessage ?? DownloadFailureMessage(lastDownloadException));

        return DownloadOutcomes.NoMatchingCandidates();
    }

    private async Task<JobOutcome> DownloadExactSongTarget(
        SongJob song,
        Job parentJob,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource cts,
        PeerFileTarget target)
    {
        var destination = await context.OutputFinalizer.GetInitialDownloadTargetAsync(config, song, organizer, target);
        song.UpdateActivity(JobActivityPhase.Downloading);

        var download = await context.Runtime.ExactFileTransfers.DownloadFile(
            target,
            destination.Path,
            song,
            config.Transfer,
            config.Output.ParentDir,
            config.Search.MaxStaleTime,
            ct: cts.Token,
            publishToDuplicateCache: destination.PublishToDuplicateCache,
            parentJob: DownloadParentFor(song, parentJob),
            deferTerminalCompletion: true,
            allowOverwrite: !config.Skip.SkipExisting,
            allowReuse: config.Skip.SkipExisting);

        if (download.Status == ExactFileTransferStatus.ManuallySkipped)
            return JobOutcome.Skipped(JobSkipReason.Manual);
        if (download.Status == ExactFileTransferStatus.AlreadyExists)
            return JobOutcome.AlreadyExists(download.Result?.OutputPath);

        var result = download.Result
            ?? throw new InvalidOperationException(
                $"Completed exact download outcome missing result for "
                + $"'{target.Username}\\{PeerIdentityValidator.ToDisplayText(target.Filename)}'.");
        context.UserSuccesses.RecordSuccess(song.WorkflowId, target.Username);
        if (result.TransferId is Guid transferId)
        {
            context.PendingTerminalTransfers[song.Id] = new PendingTerminalTransfer(
                transferId,
                result.AttemptCount,
                target,
                target.Filename,
                result.OutputPath);
        }

        return JobOutcome.Done(result.OutputPath, downloadSource: SongDownloadSource.Soulseek, reused: result.Reused);
    }

    internal ValueTask<bool> CompleteAlbumTransfersAsync(IEnumerable<SongJob> files)
        => context.Runtime.Files.WithCacheAsync(_ =>
        {
            foreach (var file in files) CompletePendingTerminalTransfer(file, file.DownloadPath);
            return true;
        });

    private ValueTask<bool> CompletePendingTerminalTransferAsync(SongJob song, string? finalPath)
        => context.Runtime.Files.WithCacheAsync(_ => { CompletePendingTerminalTransfer(song, finalPath); return true; });

    private void CompletePendingTerminalTransfer(SongJob song, string? finalPath)
    {
        if (!context.PendingTerminalTransfers.TryRemove(song.Id, out var pending))
            return;

        var resolvedPath = string.IsNullOrWhiteSpace(finalPath) ? pending.InitialOutputPath : finalPath;
        long size = System.IO.File.Exists(resolvedPath) ? new FileInfo(resolvedPath).Length : 0;
        if (pending.Target is { } target)
        {
            context.Events.RaiseTransferCompleted(
                pending.TransferId,
                song,
                target,
                resolvedPath,
                size > 0 ? size : target.Size ?? 0,
                pending.AttemptCount);
        }
        else
        {
            context.Events.RaiseFallbackTransferCompleted(
                pending.TransferId,
                song,
                pending.SourceReference,
                resolvedPath,
                size,
                pending.AttemptCount);
        }
    }

    private void FailPendingTerminalTransfer(SongJob song, Exception exception, TransferFailureReason reason)
    {
        if (!context.PendingTerminalTransfers.TryRemove(song.Id, out var pending))
            return;

        if (pending.Target is { } target)
        {
            context.Events.RaiseTransferFailed(
                pending.TransferId,
                song,
                target,
                pending.InitialOutputPath,
                song.BytesTransferred,
                target.Size ?? 0,
                pending.AttemptCount,
                reason,
                exception);
        }
        else
        {
            context.Events.RaiseFallbackTransferFailed(
                pending.TransferId,
                song,
                pending.SourceReference,
                pending.InitialOutputPath,
                pending.AttemptCount,
                reason,
                exception);
        }
    }

    private static bool ShouldCancelGroupOnEmbeddedOutcome(JobOutcome outcome)
        // Existing output is a successful result, not a reason to cancel the
        // album's remaining independent files.
        => outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.PartialSuccess
            || outcome.TerminalOutcome == JobTerminalOutcome.Skipped
                && outcome.SkipReason != JobSkipReason.AlreadyExists;

    private JobCancellationSource CancellationSourceForEmbeddedSong(
        SongJob song,
        Job parentJob,
        CancellationTokenSource groupCts)
    {
        if (song.CancellationSource != JobCancellationSource.None)
            return song.CancellationSource;
        if (context.Runtime.IsCancellationRequested)
            return JobCancellationSource.UserRequestedAllJobs;
        if (parentJob.Cts?.IsCancellationRequested == true || groupCts.IsCancellationRequested)
            return JobCancellationSource.ParentJob;

        return JobCancellationSource.InternalEngine;
    }

    private static string? DownloadFailureMessage(Exception ex)
        => ExceptionText.Summary(ex);

    private static Job? DownloadParentFor(SongJob song, Job job)
        => ReferenceEquals(song, job) ? null : job;
}
