using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
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

    public async Task ApplyIfNeededAsync(Job job, DownloadSettings config)
    {
        if (!config.Extraction.RemoveTracksFromSource) return;
        if (job is SearchJob or RetrieveFolderJob) return;
        if (job.SourceMutation == null) return;
        var applied = appliedByWorkflow.GetOrAdd(job.WorkflowId,
            static _ => new(StringComparer.Ordinal));
        if (!applied.TryAdd(job.SourceMutation.Key, 0)) return;

        DownloadLogMessages.JobDecision(logger, job.Id, "source-mutation", null);
        try { await executor.ApplyAsync(job.SourceMutation, config); }
        catch (Exception ex)
        {
            DownloadLogMessages.ComponentFailed(
                logger,
                ex,
                "source-mutation",
                job.Id);
            events.RaiseJobMessage(
                job,
                LogLevel.Warning,
                null,
                "download succeeded, but removing it from the source failed");
        }
    }

    // A later workflow may process the same repopulated row/playlist entry.
    public void RetireWorkflow(Guid workflowId) => appliedByWorkflow.TryRemove(workflowId, out _);
    internal int RetainedWorkflowCount => appliedByWorkflow.Count;
}
