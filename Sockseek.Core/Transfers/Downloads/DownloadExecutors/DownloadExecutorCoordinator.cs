using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core;

internal sealed class DownloadExecutorCoordinator
{
    private readonly DownloadExecutionContext context;
    private readonly JobOrchestrator jobs;
    private readonly GroupDownloadExecutor groupDownloads;
    private readonly SongDownloadExecutor songDownloads;
    private readonly AlbumDownloadExecutor albumDownloads;
    private readonly RemoteFileDownloadExecutor remoteFileDownloads;
    private readonly RemoteDirectoryDownloadExecutor remoteDirectoryDownloads;
    private readonly ILogger<DownloadExecutorCoordinator> logger;

    public DownloadExecutorCoordinator(DownloadExecutionContext context, JobOrchestrator jobs)
    {
        this.context = context;
        this.jobs = jobs;
        logger = context.LoggerFactory.CreateLogger<DownloadExecutorCoordinator>();
        groupDownloads = new GroupDownloadExecutor(context, jobs);
        songDownloads = new SongDownloadExecutor(context, jobs);
        albumDownloads = new AlbumDownloadExecutor(context, jobs, songDownloads);
        remoteFileDownloads = new RemoteFileDownloadExecutor(context);
        remoteDirectoryDownloads = new RemoteDirectoryDownloadExecutor(context);
    }

    public async Task<JobOutcome> ProcessLeafDownload(Job job, JobContext ctx, CancellationToken parentToken, Job? parentJob)
    {
        var config = job.Config;

        context.UpdateOutputs(job, ctx);

        try
        {
            await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

            JobOutcome outcome = JobOutcome.NoChange();
            switch (job)
            {
                case SongJob sj:
                    var songParent = parentJob ?? sj;
                    var songOrganizer = new FileManager(
                        sj,
                        config.Output,
                        config.Extraction,
                        context.LoggerFactory.CreateLogger<FileManager>(),
                        ctx.OutputScope);
                    outcome = await songDownloads.ProcessSongDownload(sj, songParent, songOrganizer, parentToken);
                    outcome = await songDownloads.CommitAndFinalizeSong(sj, songParent, outcome, ctx, songOrganizer, finalizePlacement: true, updateIndexes: true);
                    break;

                case AlbumJob aj:
                    outcome = await albumDownloads.ProcessAlbumDownload(aj, ctx);
                    break;

                case RemoteFileJob remoteFile:
                    outcome = await remoteFileDownloads.Process(remoteFile, parentJob);
                    outcome = await OnCompleteExecutor.ExecuteAsync(
                        remoteFile,
                        null,
                        ctx,
                        outcome,
                        context.Events,
                        logger);
                    JobOutcomeCommitter.Commit(remoteFile, outcome);
                    break;

                case RemoteDirectoryJob remoteDirectory:
                    outcome = await remoteDirectoryDownloads.Process(remoteDirectory);
                    outcome = await OnCompleteExecutor.ExecuteAsync(
                        remoteDirectory,
                        null,
                        ctx,
                        outcome,
                        context.Events,
                        logger);
                    JobOutcomeCommitter.Commit(remoteDirectory, outcome);
                    break;

                case SongGroupsJob ag:
                    ag.UpdateActivity(JobActivityPhase.RunningChildren);
                    outcome = await groupDownloads.ProcessGroupDownload(ag, ctx);
                    JobOutcomeCommitter.Commit(ag, outcome);
                    break;
            }

            DownloadLogMessages.JobDecision(logger, job.Id, "leaf-download-completed", null);
            return outcome;
        }
        catch (Exception ex)
        {
            var outcome = ClassifyLeafFailure(job, parentToken, ex);
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }
    }

    private JobOutcome ClassifyLeafFailure(
        Job job,
        CancellationToken parentToken,
        Exception exception)
    {
        if (exception is OperationCanceledException
            && jobs.IsJobCancellationRequested(job, parentToken))
        {
            return JobOutcome.Cancelled(jobs.CancellationSourceFor(job, parentToken));
        }

        DownloadLogMessages.ComponentFailed(
            logger,
            exception,
            "leaf-download",
            job.Id);
        return JobOutcome.Failed(
            job is FileDownloadJob or DirectoryDownloadJob
                ? JobFailureReason.AllDownloadsFailed
                : JobFailureReason.Other,
            ExceptionText.Summary(exception),
            ExceptionText.Detail(exception));
    }

    // ── per-job-type handlers ─────────────────────────────────────────────────

    public async Task<JobOutcome> CommitAndFinalizeSong(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        JobContext jobCtx,
        FileManager organizer,
        bool finalizePlacement,
        bool updateIndexes)
        => await songDownloads.CommitAndFinalizeSong(song, parentJob, outcome, jobCtx, organizer, finalizePlacement, updateIndexes);

    public async Task<JobOutcome> RunOnCompleteIfApplicable(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
        => await songDownloads.RunOnCompleteIfApplicable(job, song, ctx, outcome);

    public static void ApplyPreCommitOutcomeMetadata(Job job, JobOutcome outcome)
    {
        if (job is SongJob song)
        {
            if (outcome.ChosenCandidate != null)
                song.ResolvedTarget = outcome.ChosenCandidate;
            if (outcome.ShouldUpdateDownloadPath)
                song.DownloadPath = outcome.DownloadPath;
            if (outcome.DownloadSource != SongDownloadSource.None)
                song.DownloadSource = outcome.DownloadSource;
        }
        else if (job is DirectoryDownloadJob album && outcome.ShouldUpdateDownloadPath)
        {
            album.DownloadPath = outcome.DownloadPath;
        }
        else if (job is FileDownloadJob file && outcome.ShouldUpdateDownloadPath)
        {
            file.DownloadPath = outcome.DownloadPath;
        }
    }

    public static JobOutcome OutcomeWithCurrentMetadata(Job job, JobOutcome outcome)
    {
        if (outcome.TerminalOutcome is not (JobTerminalOutcome.Succeeded or JobTerminalOutcome.Skipped))
            return outcome;
        return job switch
        {
            SongJob song => outcome.WithDownloadMetadata(song.DownloadPath ?? outcome.DownloadPath,
                song.ResolvedTarget ?? outcome.ChosenCandidate,
                song.DownloadSource != SongDownloadSource.None ? song.DownloadSource : outcome.DownloadSource),
            DirectoryDownloadJob directory => outcome.WithDownloadMetadata(directory.DownloadPath ?? outcome.DownloadPath),
            FileDownloadJob file => outcome.WithDownloadMetadata(file.DownloadPath ?? outcome.DownloadPath),
            _ => outcome,
        };
    }
}
