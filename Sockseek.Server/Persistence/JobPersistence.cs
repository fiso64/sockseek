using System.Text.Json;
using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Write;
using Sockseek.Server.Planning;

namespace Sockseek.Server.Persistence;

// Consumes accepted job facts from the state owner. It does not subscribe to Core
// events or maintain a second interpretation of job relationships.
internal sealed class JobPersistence(
    Guid runtimeId,
    IPersistenceMutationSink sink,
    PersistenceHandoffTracker? handoffs = null,
    Func<Guid, IReadOnlyCollection<Guid>, IReadOnlyDictionary<Guid, SearchViewSource>>? captureSearchSources = null)
{
    private readonly ConcurrentDictionary<Guid, PendingJob> pendingJobs = new();

    internal void WriteJob(JobSnapshotHeader job, CoreChange change,
        PersistenceMutationPriority priority, Guid? parentJobId, Guid? sourceJobId, Guid? resultJobId)
    {
        handoffs?.RegisterJob(job.WorkflowId, job.Id);
        var pending = pendingJobs.GetValueOrDefault(job.Id)
            ?? new PendingJob(job.WorkflowId, null, null, null);
        pending = pending with
        {
            StartedAtUtc = pending.StartedAtUtc ?? (job.LifecycleState != JobLifecycleState.Pending ? change.OccurredAtUtc : null),
            TerminalRevision = job.LifecycleState == JobLifecycleState.Terminal ? job.Revision : null,
        };
        pendingJobs[job.Id] = pending;
        var payload = job switch
        {
            JobSnapshot full => full.Payload,
            JobExecutionSnapshot execution => execution.Payload,
            _ => throw new InvalidOperationException($"Unsupported job snapshot {job.GetType().FullName}."),
        };
        var manifest = payload switch
        {
            AlbumJobSnapshotPayload album => album.Manifest,
            RemoteDirectoryJobSnapshotPayload directory => directory.Manifest,
            _ => null,
        };
        // The writer encodes only the coalesced revision. Capture immutable compact
        // values here, without retaining a runtime Job or its descendant graph.
        var payloadJson = RetainedJobPayloadCodec.Capture(payload);
        sink.TryEnqueue(new JobPersistenceMutation(
            runtimeId,
            change.Sequence,
            change.OccurredAtUtc,
            job.Id,
            job.Revision,
            priority,
            job.WorkflowId,
            parentJobId,
            sourceJobId,
            resultJobId,
            job.DisplayId,
            job.Kind.ToString(),
            job.LifecycleState.ToString(),
            job.ActivityPhase.ToString(),
            job.ActivityUntilUtc,
            job.TerminalOutcome.ToString(),
            job.SkipReason.ToString(),
            job.CancellationSource.ToString(),
            job.FailureReason.ToString(),
            job.FailureMessage,
            job.FailureDetail,
            job.ItemName,
            job.QueryText,
            PayloadSchemaVersion: RetainedJobPayloadCodec.SchemaVersion,
            payloadJson,
            job.SubmissionId,
            job.SemanticRole.ToString(),
            job.CreatedAtUtc,
            pending.StartedAtUtc ?? default,
            manifest,
            job.SourceDetail,
            (int)job.PrintOption,
            job.AppliedProfiles.Count == 0 ? "[]" : JsonSerializer.Serialize(job.AppliedProfiles)));
    }

    internal void WriteResults(SearchResultsAddedChange change)
        => sink.TryEnqueue(new SearchResultsPersistenceMutation(
            runtimeId, change.Sequence, change.OccurredAtUtc, change.JobId, change.Revision, change.Results));

    // Update handoff metadata with the other accepted job facts under the state
    // owner's gate. Bounded raw admission below must happen outside that gate.
    internal void RecordSearchCompletion(SearchCompletedChange change)
    {
        if (pendingJobs.TryGetValue(change.JobId, out var pending))
            pendingJobs[change.JobId] = pending with { SearchCompletionRevision = change.Revision };
    }

    internal void WriteSearchCompletion(SearchCompletedChange change)
        => sink.TryEnqueue(new SearchCompletionPersistenceMutation(runtimeId, change.Sequence,
            change.OccurredAtUtc, change.JobId, change.Revision, change.QueryText,
            change.ResultCount, change.LockedFileCount, "Complete", change.ObservedPeerCount));
    internal void RetireWorkflow(Guid workflowId)
    {
        var entries = pendingJobs.Where(pair => pair.Value.WorkflowId == workflowId).ToArray();
        var jobs = entries.Where(pair => pair.Value.TerminalRevision.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.TerminalRevision!.Value);
        var searches = entries.Where(pair => pair.Value.SearchCompletionRevision.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.SearchCompletionRevision!.Value);
        // Core keeps sources alive until the retirement observer returns. Capture
        // without the state owner's gate; later acknowledgements retire its records.
        var sources = searches.Count == 0 ? null : captureSearchSources?.Invoke(workflowId, searches.Keys);
        handoffs?.BeginRetirement(workflowId, jobs, searches, sources);
        foreach (var entry in entries) pendingJobs.TryRemove(entry.Key, out _);
    }

    internal int RetainedJobCount => pendingJobs.Count;

    private sealed record PendingJob(Guid WorkflowId, DateTimeOffset? StartedAtUtc,
        long? TerminalRevision, long? SearchCompletionRevision);

}
