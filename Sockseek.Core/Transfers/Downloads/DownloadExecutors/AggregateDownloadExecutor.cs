using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core;

internal sealed class AggregateDownloadExecutor
{
    private readonly DownloadExecutionContext context;
    private readonly JobOrchestrator jobs;

    public AggregateDownloadExecutor(DownloadExecutionContext context, JobOrchestrator jobs)
    {
        this.context = context;
        this.jobs = jobs;
    }

    public async Task<JobOutcome> ProcessAggregateDownload(AggregateJob job, JobContext ctx)
    {
        var pendingSongs = job.Songs
            .Where(song => song.LifecycleState == JobLifecycleState.Pending)
            .ToList();

        if (pendingSongs.Count > 0)
        {
            var songList = context.PrepareAggregateChildren(job, pendingSongs, ctx);

            context.RegisterJob(songList, job);
            await jobs.ProcessJob(songList, job.Cts!.Token, job);
            context.UpdateOutputs(job, ctx);
        }

        return DeriveAggregateOutcome(job);
    }

    private static JobOutcome DeriveAggregateOutcome(AggregateJob job)
    {
        var songs = job.Songs;
        bool anySuccessful = songs.Any(JobOrchestrator.IsSubtreeSuccessful);
        bool anyCancelled = songs.Any(JobOrchestrator.HasCancelledDescendant);
        bool anyUnsuccessful = songs.Any(JobOrchestrator.IsSubtreeUnsuccessful);

        if (anySuccessful && (anyCancelled || anyUnsuccessful))
            return JobOutcome.PartialSuccess(
                "Some aggregate songs completed and some failed or were cancelled.",
                anyCancelled ? JobOrchestrator.CancellationSourceForDerivedCancellation(job, songs.Cast<Job>().ToArray()) : JobCancellationSource.None);

        if (job.Cts?.IsCancellationRequested == true)
            return JobOutcome.Cancelled(JobOrchestrator.CancellationSourceForDerivedCancellation(job, songs.Cast<Job>().ToArray()));

        if (anyCancelled)
            return JobOutcome.Cancelled(JobOrchestrator.CancellationSourceForDerivedCancellation(job, songs.Cast<Job>().ToArray()));

        var failedSong = songs.FirstOrDefault(song =>
            song.TerminalOutcome == JobTerminalOutcome.Failed && song.FailureReason != JobFailureReason.Cancelled);
        if (failedSong != null)
            return JobOutcome.Failed(
                failedSong.FailureReason == JobFailureReason.None ? JobFailureReason.AllDownloadsFailed : failedSong.FailureReason,
                failedSong.FailureMessage,
                failedSong.FailureDetail);

        if (anyUnsuccessful)
            return JobOutcome.Failed(JobFailureReason.Other, "One or more aggregate songs failed.");

        return JobOutcome.Done();
    }
}
