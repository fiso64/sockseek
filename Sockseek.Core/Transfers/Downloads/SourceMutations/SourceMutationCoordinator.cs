using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Transfers.Downloads.SourceMutations;

internal sealed class SourceMutationCoordinator
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> appliedByWorkflow = new();
    private readonly SourceMutationExecutor executor = new();
    private readonly DownloadEvents events;
    private readonly ILogger<SourceMutationCoordinator> logger;

    public SourceMutationCoordinator(
        DownloadEvents events,
        ILogger<SourceMutationCoordinator> logger)
    {
        this.events = events;
        this.logger = logger;
    }

    public bool TryClaim(Job job)
    {
        var applied = appliedByWorkflow.GetOrAdd(job.WorkflowId,
            static _ => new(StringComparer.Ordinal));
        return applied.TryAdd(job.SourceMutation!.Key, 0);
    }

    public async Task ApplyAsync(IEnumerable<Job> jobs)
    {
        foreach (var source in jobs.GroupBy(job => (job.SourceMutation!.Kind, job.SourceMutation.Source)))
        {
            if (source.Key.Kind is SourceMutationKind.ClearCsvRow or SourceMutationKind.ClearTextLine)
            {
                try
                {
                    var unmatched = await SourceMutationExecutor.ClearReviewedRowsAsync(source.Key.Source,
                        source.Select(job => job.SourceMutation!).ToArray());
                    foreach (var job in source.Where(job => unmatched.Contains(job.SourceMutation!)))
                        events.RaiseJobMessage(job, LogLevel.Warning, "source-mutation",
                            SourceMutationExecutor.ChangedRowMessage);
                }
                catch (Exception ex) { ReportFailure(source, ex); }
                continue;
            }
            foreach (var job in source)
            {
                try { await executor.ApplyAsync(job.SourceMutation!, job.Config); }
                catch (Exception ex) { ReportFailure([job], ex); }
            }
        }
    }

    private void ReportFailure(IEnumerable<Job> jobs, Exception failure)
    {
        DownloadLogMessages.ComponentFailed(logger, failure, "source-mutation", jobs.First().Id);
        foreach (var job in jobs)
            events.RaiseJobMessage(job, LogLevel.Warning, "source-mutation",
                "Download succeeded, but removing it from the source failed.");
    }

    // A later workflow may process the same repopulated row/playlist entry.
    public void RetireWorkflow(Guid workflowId) => appliedByWorkflow.TryRemove(workflowId, out _);
    internal int RetainedWorkflowCount => appliedByWorkflow.Count;
}
