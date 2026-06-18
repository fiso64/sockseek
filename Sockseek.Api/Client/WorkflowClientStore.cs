namespace Sockseek.Api;

public sealed class WorkflowClientStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, WorkflowSummaryDto> workflows = [];
    private readonly Dictionary<Guid, JobSummaryDto> jobs = [];
    private readonly Dictionary<Guid, SearchUpdatedDto> searchUpdates = [];
    private readonly Dictionary<Guid, DownloadProgressEventDto> downloadProgress = [];
    private readonly Dictionary<Guid, long> workflowSequences = [];

    public WorkflowClientUpdate Apply(WorkflowUpdateBatchDto batch)
    {
        lock (gate)
        {
            var sequenceObservation = ObserveSequence(batch.WorkflowId, batch.Sequence);
            if (sequenceObservation.IsStale)
                return new WorkflowClientUpdate(
                    batch.WorkflowId,
                    batch.Sequence,
                    [],
                    SequenceGapDetected: false,
                    ExpectedSequence: null,
                    IsStale: true);

            var events = new List<ServerEventEnvelopeDto>(
                batch.JobUpserts.Count
                + (batch.Workflow == null ? 0 : 1)
                + batch.SearchUpdates.Count
                + batch.Activity.Count
                + batch.Progress.Count);

            foreach (var summary in batch.JobUpserts)
            {
                jobs[summary.JobId] = summary;
                events.Add(BatchEnvelope(batch, "job.upserted", "state", snapshotInvalidation: true, summary));
            }

            if (batch.Workflow != null)
            {
                workflows[batch.Workflow.WorkflowId] = batch.Workflow;
                events.Add(BatchEnvelope(batch, "workflow.upserted", "state", snapshotInvalidation: true, batch.Workflow));
            }

            foreach (var update in batch.SearchUpdates)
            {
                searchUpdates[update.JobId] = update;
                events.Add(BatchEnvelope(batch, "search.updated", "state", snapshotInvalidation: true, update));
            }

            // Activity edges run after state so renderers/loggers can rely on current structure.
            events.AddRange(batch.Activity);

            foreach (var progress in batch.Progress)
            {
                downloadProgress[progress.JobId] = progress;
                events.Add(BatchEnvelope(batch, "download.progress", "progress", snapshotInvalidation: false, progress));
            }

            return new WorkflowClientUpdate(
                batch.WorkflowId,
                batch.Sequence,
                events,
                sequenceObservation.SequenceGapDetected,
                sequenceObservation.ExpectedSequence,
                IsStale: false,
                Workflow: batch.Workflow,
                JobUpserts: batch.JobUpserts,
                SearchUpdates: batch.SearchUpdates,
                Progress: batch.Progress,
                Activity: batch.Activity);
        }
    }

    public WorkflowClientUpdate ApplySnapshot(
        WorkflowDetailDto snapshot,
        bool replaceKnownWorkflowJobs = false,
        long sequence = 0,
        DateTimeOffset? occurredAtUtc = null)
    {
        lock (gate)
        {
            var workflowId = snapshot.Summary.WorkflowId;
            var now = occurredAtUtc ?? DateTimeOffset.UtcNow;
            var events = new List<ServerEventEnvelopeDto>(snapshot.Jobs.Count + 1);

            if (replaceKnownWorkflowJobs)
            {
                var snapshotJobIds = snapshot.Jobs.Select(job => job.JobId).ToHashSet();
                foreach (var jobId in jobs.Values
                    .Where(job => job.WorkflowId == workflowId && !snapshotJobIds.Contains(job.JobId))
                    .Select(job => job.JobId)
                    .ToList())
                {
                    jobs.Remove(jobId);
                    searchUpdates.Remove(jobId);
                    downloadProgress.Remove(jobId);
                }
            }

            foreach (var summary in snapshot.Jobs)
            {
                jobs[summary.JobId] = summary;
                events.Add(SnapshotEnvelope(sequence, now, workflowId, "job.upserted", "state", snapshotInvalidation: true, summary));
            }

            workflows[workflowId] = snapshot.Summary;
            events.Add(SnapshotEnvelope(sequence, now, workflowId, "workflow.upserted", "state", snapshotInvalidation: true, snapshot.Summary));

            if (sequence > 0)
            {
                workflowSequences[workflowId] = workflowSequences.TryGetValue(workflowId, out var current)
                    ? Math.Max(current, sequence)
                    : sequence;
            }

            return new WorkflowClientUpdate(
                workflowId,
                sequence,
                events,
                Workflow: snapshot.Summary,
                JobUpserts: snapshot.Jobs);
        }
    }

    public WorkflowSummaryDto? GetWorkflow(Guid workflowId)
    {
        lock (gate)
            return workflows.GetValueOrDefault(workflowId);
    }

    public JobSummaryDto? GetJob(Guid jobId)
    {
        lock (gate)
            return jobs.GetValueOrDefault(jobId);
    }

    public IReadOnlyList<JobSummaryDto> GetWorkflowJobs(Guid workflowId)
    {
        lock (gate)
            return jobs.Values
                .Where(job => job.WorkflowId == workflowId)
                .OrderBy(job => job.DisplayId)
                .ToList();
    }

    public SearchUpdatedDto? GetSearchUpdate(Guid jobId)
    {
        lock (gate)
            return searchUpdates.GetValueOrDefault(jobId);
    }

    public DownloadProgressEventDto? GetDownloadProgress(Guid jobId)
    {
        lock (gate)
            return downloadProgress.GetValueOrDefault(jobId);
    }

    private static ServerEventEnvelopeDto BatchEnvelope(
        WorkflowUpdateBatchDto batch,
        string type,
        string category,
        bool snapshotInvalidation,
        object payload)
        => new(
            batch.Sequence,
            type,
            batch.OccurredAtUtc,
            category,
            snapshotInvalidation,
            batch.WorkflowId,
            payload);

    private static ServerEventEnvelopeDto SnapshotEnvelope(
        long sequence,
        DateTimeOffset occurredAtUtc,
        Guid workflowId,
        string type,
        string category,
        bool snapshotInvalidation,
        object payload)
        => new(
            sequence,
            type,
            occurredAtUtc,
            category,
            snapshotInvalidation,
            workflowId,
            payload);

    private SequenceObservation ObserveSequence(Guid workflowId, long sequence)
    {
        if (sequence <= 0)
            return new SequenceObservation(false, null, false);

        if (!workflowSequences.TryGetValue(workflowId, out var lastSequence))
        {
            workflowSequences[workflowId] = sequence;
            return sequence == 1
                ? new SequenceObservation(false, null, false)
                : new SequenceObservation(true, 1, false);
        }

        if (sequence <= lastSequence)
            return new SequenceObservation(false, null, true);

        var expectedSequence = lastSequence + 1;
        workflowSequences[workflowId] = sequence;
        return sequence == expectedSequence
            ? new SequenceObservation(false, null, false)
            : new SequenceObservation(true, expectedSequence, false);
    }

    private readonly record struct SequenceObservation(
        bool SequenceGapDetected,
        long? ExpectedSequence,
        bool IsStale);
}

public sealed record WorkflowClientUpdate(
    Guid WorkflowId,
    long Sequence,
    IReadOnlyList<ServerEventEnvelopeDto> Events,
    bool SequenceGapDetected = false,
    long? ExpectedSequence = null,
    bool IsStale = false,
    WorkflowSummaryDto? Workflow = null,
    IReadOnlyList<JobSummaryDto>? JobUpserts = null,
    IReadOnlyList<SearchUpdatedDto>? SearchUpdates = null,
    IReadOnlyList<DownloadProgressEventDto>? Progress = null,
    IReadOnlyList<ServerEventEnvelopeDto>? Activity = null)
{
    public IReadOnlyList<JobSummaryDto> JobUpserts { get; init; } = JobUpserts ?? [];
    public IReadOnlyList<SearchUpdatedDto> SearchUpdates { get; init; } = SearchUpdates ?? [];
    public IReadOnlyList<DownloadProgressEventDto> Progress { get; init; } = Progress ?? [];
    public IReadOnlyList<ServerEventEnvelopeDto> Activity { get; init; } = Activity ?? [];
}
