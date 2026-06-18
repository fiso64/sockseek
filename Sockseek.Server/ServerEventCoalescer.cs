using System.Collections.Concurrent;
using Sockseek.Api;

namespace Sockseek.Server;

public readonly record struct ServerEventItem(string Type, object Payload);

public sealed class ServerEventCoalescer : IDisposable
{
    private readonly Lock gate = new();
    private readonly Action<IReadOnlyList<ServerEventItem>> publishBatch;
    private readonly int maxJobUpsertsPerWorkflow;
    private readonly ConcurrentDictionary<Guid, DownloadProgressEventDto> pendingDownloadProgress = [];
    private readonly ConcurrentDictionary<Guid, SearchUpdatedDto> pendingSearchUpdated = [];
    private readonly ConcurrentDictionary<Guid, JobActivityChangedEventDto> pendingJobActivityChanged = [];
    private readonly ConcurrentDictionary<Guid, JobSummaryDto> pendingJobUpserted = [];
    private readonly ConcurrentDictionary<Guid, WorkflowSummaryDto> pendingWorkflowUpserted = [];
    private readonly List<ServerEventItem> pendingActivityEvents = [];
    private readonly Dictionary<Guid, int> pendingJobUpsertCountsByWorkflow = [];
    private readonly HashSet<Guid> snapshotOnlyWorkflows = [];
    private readonly HashSet<Guid> requiredJobUpserts = [];
    private readonly Timer timer;

    public ServerEventCoalescer(
        Action<IReadOnlyList<ServerEventItem>> publishBatch,
        TimeSpan? flushInterval = null,
        int maxJobUpsertsPerWorkflow = 250)
    {
        this.publishBatch = publishBatch;
        this.maxJobUpsertsPerWorkflow = maxJobUpsertsPerWorkflow;
        timer = new Timer(
            _ => Flush(),
            null,
            flushInterval ?? TimeSpan.FromMilliseconds(200),
            flushInterval ?? TimeSpan.FromMilliseconds(200));
    }

    public void Publish(string type, object payload)
    {
        lock (gate)
        {
            if (type == "download.progress" && payload is DownloadProgressEventDto progress)
            {
                pendingDownloadProgress[progress.JobId] = progress;
                return;
            }

            if (type == "search.updated" && payload is SearchUpdatedDto search)
            {
                pendingSearchUpdated[search.JobId] = search;
                return;
            }

            if (type == "job.upserted" && payload is JobSummaryDto job)
            {
                if (IsBulkCancellationSummary(job))
                {
                    snapshotOnlyWorkflows.Add(job.WorkflowId);
                    return;
                }

                if (!pendingJobUpserted.ContainsKey(job.JobId))
                {
                    pendingJobUpsertCountsByWorkflow[job.WorkflowId] =
                        pendingJobUpsertCountsByWorkflow.GetValueOrDefault(job.WorkflowId) + 1;
                    if (pendingJobUpsertCountsByWorkflow[job.WorkflowId] > maxJobUpsertsPerWorkflow)
                        snapshotOnlyWorkflows.Add(job.WorkflowId);
                }

                pendingJobUpserted[job.JobId] = job;
                return;
            }

            if (type == "job.activity-changed" && payload is JobActivityChangedEventDto activity)
            {
                if (activity.Summary.LifecycleState == ServerJobLifecycleState.Terminal
                    || activity.Summary.ActivityPhase == ServerJobActivityPhase.None)
                {
                    pendingJobActivityChanged.TryRemove(activity.Summary.JobId, out _);
                    return;
                }

                pendingJobActivityChanged[activity.Summary.JobId] = activity;
                return;
            }

            if (type == "workflow.upserted" && payload is WorkflowSummaryDto workflow)
            {
                pendingWorkflowUpserted[workflow.WorkflowId] = workflow;
                return;
            }

            if (GetWorkflowId(payload).HasValue)
            {
                MarkRelevantStateRequired(payload);
                pendingActivityEvents.Add(new ServerEventItem(type, payload));
                return;
            }

            PublishBatch([new ServerEventItem(type, payload)]);
        }
    }

    public void Flush()
    {
        lock (gate)
            FlushCore();
    }

    private void FlushCore()
    {
        var items = new List<ServerEventItem>();
        FlushProgressCore(items);
        FlushActivityCore(items);
        FlushStateCore(items);
        FlushBufferedActivityCore(items);
        PublishBatch(items);
    }

    private void FlushProgressCore(List<ServerEventItem> items)
    {
        foreach (var jobId in pendingDownloadProgress.Keys)
        {
            if (pendingDownloadProgress.TryRemove(jobId, out var progress))
                items.Add(new ServerEventItem("download.progress", progress));
        }

        foreach (var jobId in pendingSearchUpdated.Keys)
        {
            if (pendingSearchUpdated.TryRemove(jobId, out var search))
                items.Add(new ServerEventItem("search.updated", search));
        }
    }

    private void FlushActivityCore(List<ServerEventItem> items)
    {
        foreach (var jobId in pendingJobActivityChanged.Keys)
        {
            if (pendingJobActivityChanged.TryRemove(jobId, out var activity))
                items.Add(new ServerEventItem("job.activity-changed", activity));
        }
    }

    private void FlushStateCore(List<ServerEventItem> items)
    {
        var snapshotOnly = snapshotOnlyWorkflows
            .Where(workflowId => pendingWorkflowUpserted.ContainsKey(workflowId))
            .ToHashSet();

        foreach (var jobId in pendingJobUpserted.Keys)
        {
            if (pendingJobUpserted.TryRemove(jobId, out var summary)
                && (!snapshotOnly.Contains(summary.WorkflowId) || requiredJobUpserts.Contains(jobId)))
            {
                items.Add(new ServerEventItem("job.upserted", summary));
            }
        }

        foreach (var workflowId in pendingWorkflowUpserted.Keys)
        {
            if (pendingWorkflowUpserted.TryRemove(workflowId, out var workflow))
                items.Add(new ServerEventItem("workflow.upserted", workflow));
        }

        pendingJobUpsertCountsByWorkflow.Clear();
        snapshotOnlyWorkflows.Clear();
        requiredJobUpserts.Clear();
    }

    private void FlushBufferedActivityCore(List<ServerEventItem> items)
    {
        items.AddRange(pendingActivityEvents);
        pendingActivityEvents.Clear();
    }

    private void MarkRelevantStateRequired(object payload)
    {
        if (GetJobId(payload) is not Guid jobId)
            return;

        if (!pendingJobUpserted.TryGetValue(jobId, out _))
            return;

        requiredJobUpserts.Add(jobId);
    }

    private static Guid? GetWorkflowId(object payload)
        => payload switch
        {
            JobSummaryDto summary => summary.WorkflowId,
            WorkflowSummaryDto summary => summary.WorkflowId,
            WorkflowDetailDto detail => detail.Summary.WorkflowId,
            WorkflowTreeDto workflow => workflow.Summary.WorkflowId,
            JobDetailDto detail => detail.Summary.WorkflowId,
            SearchUpdatedDto update => update.WorkflowId,
            ExtractionStartedEventDto e => e.Summary.WorkflowId,
            ExtractionFailedEventDto e => e.Summary.WorkflowId,
            JobStartedEventDto e => e.Summary.WorkflowId,
            JobStatusEventDto e => e.Summary.WorkflowId,
            JobMessageEventDto e => e.Summary.WorkflowId,
            JobActivityChangedEventDto e => e.Summary.WorkflowId,
            SongSearchingEventDto e => e.WorkflowId,
            DownloadStartedEventDto e => e.WorkflowId,
            DownloadProgressEventDto e => e.WorkflowId,
            DownloadStateChangedEventDto e => e.WorkflowId,
            DownloadAttemptFailedEventDto e => e.WorkflowId,
            SongStateChangedEventDto e => e.WorkflowId,
            AlbumDownloadStartedEventDto e => e.Summary.WorkflowId,
            AlbumTrackDownloadStartedEventDto e => e.Summary.WorkflowId,
            AlbumStateChangedEventDto e => e.Summary.WorkflowId,
            JobFolderRetrievingEventDto e => e.Summary.WorkflowId,
            OnCompleteStartedEventDto e => e.WorkflowId,
            OnCompleteEndedEventDto e => e.WorkflowId,
            TrackBatchResolvedEventDto e => e.Summary.WorkflowId,
            _ => null,
        };

    private void PublishBatch(List<ServerEventItem> items)
    {
        if (items.Count > 0)
            publishBatch(items);
    }

    private static Guid? GetJobId(object payload)
        => payload switch
        {
            ExtractionStartedEventDto e => e.Summary.JobId,
            ExtractionFailedEventDto e => e.Summary.JobId,
            JobStartedEventDto e => e.Summary.JobId,
            JobStatusEventDto e => e.Summary.JobId,
            JobMessageEventDto e => e.Summary.JobId,
            JobActivityChangedEventDto e => e.Summary.JobId,
            SongSearchingEventDto e => e.JobId,
            DownloadStartedEventDto e => e.JobId,
            DownloadProgressEventDto e => e.JobId,
            DownloadStateChangedEventDto e => e.JobId,
            DownloadAttemptFailedEventDto e => e.JobId,
            SongStateChangedEventDto e => e.JobId,
            AlbumDownloadStartedEventDto e => e.Summary.JobId,
            AlbumTrackDownloadStartedEventDto e => e.Summary.JobId,
            AlbumStateChangedEventDto e => e.Summary.JobId,
            JobFolderRetrievingEventDto e => e.Summary.JobId,
            OnCompleteStartedEventDto e => e.JobId,
            OnCompleteEndedEventDto e => e.JobId,
            TrackBatchResolvedEventDto e => e.Summary.JobId,
            _ => null,
        };

    private static bool IsBulkCancellationSummary(JobSummaryDto summary)
        => summary.LifecycleState == ServerJobLifecycleState.Terminal
            && summary.TerminalOutcome == ServerJobTerminalOutcome.Cancelled
            && summary.CancellationSource is ServerJobCancellationSource.ParentJob
                or ServerJobCancellationSource.UserRequestedWorkflow
                or ServerJobCancellationSource.UserRequestedAllJobs;

    public void Dispose()
    {
        timer.Dispose();
        Flush();
    }
}
