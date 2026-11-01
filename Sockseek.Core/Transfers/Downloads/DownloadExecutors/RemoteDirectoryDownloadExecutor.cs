using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Transfers.Downloads.Runtime;

namespace Sockseek.Core;

internal sealed class RemoteDirectoryDownloadExecutor
{
    private readonly DownloadExecutionContext context;
    private readonly PlacementPlanner placement = new();
    private readonly DirectoryTransferRunner transfers;

    public RemoteDirectoryDownloadExecutor(DownloadExecutionContext context)
    {
        this.context = context;
        transfers = new DirectoryTransferRunner(context);
    }

    public async Task<JobOutcome> Process(RemoteDirectoryJob job)
    {
        try
        {
            var plan = await ResolvePlan(job);
            if (plan is null)
                return JobOutcome.Done();

            var placements = placement.PlanDirectory(plan, job.Config);
            var children = placements
                .Select(item => new RemoteFileJob(item.Target, item.RelativePath))
                .ToArray();
            var work = children.Zip(placements, (child, item) =>
                new DirectoryTransferWorkItem(child, item.Target, item.OutputPath)).ToArray();

            var outcome = await transfers.Run(job, work, job.Config);
            string? root = placements.Count == 0
                ? null
                : Utils.GreatestCommonDirectory(placements.Select(item => item.OutputPath));
            return WithDownloadPath(outcome, root);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return JobOutcome.Failed(
                JobFailureReason.AllDownloadsFailed,
                ex.Message,
                Diagnostics.ExceptionText.Detail(ex));
        }
    }

    private async Task<DirectoryTransferPlan?> ResolvePlan(RemoteDirectoryJob job)
    {
        if (job.ActiveAttempt is { } existing)
            return existing.Plan;

        if (job.Source is not RemoteDirectorySource.PeerDirectory peer)
            throw new InvalidOperationException("A resolved directory source must own its plan at construction time.");

        job.BeginDirectoryResolution();
        job.UpdateActivity(JobActivityPhase.RetrievingFolder);
        var snapshot = await context.Runtime.Searcher.RetrieveDirectory(peer.Directory, job.Cts!.Token);
        job.ResolvedDirectory = snapshot;
        // A complete directory with every file explicitly deselected is valid
        // empty work, not a failed download. Incomplete/empty peer responses still
        // follow the planner's validation rather than being mistaken for success.
        if (snapshot.IsComplete && snapshot.Files.Count > 0
            && snapshot.Files.All(file => peer.ExcludedFilenames.Contains(file.Filename)))
            return null;
        var plan = DirectoryTransferPlanner.FromSnapshot(snapshot, peer.ExcludedFilenames);
        job.BeginDirectoryAttempt(plan);
        return plan;
    }

    private static JobOutcome WithDownloadPath(JobOutcome outcome, string? root)
        => outcome.TerminalOutcome switch
        {
            JobTerminalOutcome.Succeeded => JobOutcome.Done(root),
            JobTerminalOutcome.PartialSuccess => JobOutcome.PartialSuccess(
                outcome.FailureMessage,
                outcome.CancellationSource,
                root),
            JobTerminalOutcome.Cancelled => JobOutcome.Cancelled(
                outcome.CancellationSource,
                outcome.FailureMessage,
                outcome.FailureDetail,
                root),
            JobTerminalOutcome.Failed => JobOutcome.Failed(
                outcome.FailureReason,
                outcome.FailureMessage,
                outcome.FailureDetail,
                root),
            _ => outcome,
        };
}
