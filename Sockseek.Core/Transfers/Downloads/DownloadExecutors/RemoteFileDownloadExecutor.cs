using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Transfers.Downloads.Runtime;

namespace Sockseek.Core;

internal sealed class RemoteFileDownloadExecutor
{
    private readonly DownloadExecutionContext context;
    private readonly PlacementPlanner placement = new();

    public RemoteFileDownloadExecutor(DownloadExecutionContext context)
    {
        this.context = context;
    }

    public async Task<JobOutcome> Process(RemoteFileJob job, Job? parentJob)
    {
        var config = job.Config;
        var destination = placement.PlanFile(job.Target, job.OutputPath, config);

        var outcome = await context.Runtime.ExactFileTransfers.DownloadFile(
            job.Target,
            destination.OutputPath,
            job,
            config.Transfer,
            config.Output.ParentDir,
            config.Search.MaxStaleTime,
            ct: job.Cts!.Token,
            parentJob: parentJob,
            allowOverwrite: !config.Skip.SkipExisting);

        return outcome.Status switch
        {
            ExactFileTransferStatus.Completed when outcome.Result != null
                => JobOutcome.Done(outcome.Result.OutputPath),
            ExactFileTransferStatus.ManuallySkipped
                => JobOutcome.Skipped(JobSkipReason.Manual),
            ExactFileTransferStatus.AlreadyExists
                => JobOutcome.AlreadyExists(outcome.Result?.OutputPath),
            _ => JobOutcome.Failed(JobFailureReason.Other, "The exact file transfer did not produce a result."),
        };
    }
}
