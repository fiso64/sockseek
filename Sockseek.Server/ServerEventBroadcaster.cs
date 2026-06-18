using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Api;

namespace Sockseek.Server;

public sealed class ServerEventBroadcaster : IDisposable
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private readonly EngineStateStore stateStore;
    private readonly ServerEventCoalescer coalescer;
    private readonly ConcurrentDictionary<Guid, long> workflowBatchSequences = [];
    private long nextSequence;

    public event Action<ServerEventEnvelopeDto>? EventPublished;
    public event Action<WorkflowUpdateBatchDto>? BatchPublished;

    public ServerEventBroadcaster(EngineStateStore stateStore, EngineSupervisor supervisor, IHubContext<ServerEventHub> hubContext)
    {
        this.stateStore = stateStore;
        this.hubContext = hubContext;
        coalescer = new ServerEventCoalescer(PublishItems);
        stateStore.JobUpserted += summary => coalescer.Publish("job.upserted", summary);
        stateStore.WorkflowUpserted += summary => coalescer.Publish("workflow.upserted", summary);
        stateStore.SearchUpdated += update => coalescer.Publish("search.updated", update);
        supervisor.EngineCreated += AttachEngine;
    }

    private void AttachEngine(DownloadEngine engine)
    {
        new EngineEventDtoAdapter(GetSummary, coalescer.Publish).Attach(engine.Events);
    }

    private void PublishItems(IReadOnlyList<ServerEventItem> items)
    {
        var workflowEnvelopes = new Dictionary<Guid, List<ServerEventEnvelopeDto>>();

        foreach (var item in items)
        {
            var envelope = CreateEnvelope(item.Type, item.Payload);
            if (envelope.WorkflowId is Guid workflowId)
            {
                if (!workflowEnvelopes.TryGetValue(workflowId, out var list))
                {
                    list = [];
                    workflowEnvelopes[workflowId] = list;
                }

                list.Add(envelope);
            }
            else
            {
                PublishGlobalEnvelope(envelope);
            }
        }

        foreach (var pair in workflowEnvelopes)
            PublishWorkflowBatch(pair.Key, pair.Value);
    }

    private ServerEventEnvelopeDto CreateEnvelope(string type, object payload)
    {
        var descriptor = ServerEventCatalog.Describe(type);
        return new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            descriptor.Category,
            descriptor.SnapshotInvalidation,
            GetWorkflowId(payload),
            payload);
    }

    private void PublishGlobalEnvelope(ServerEventEnvelopeDto envelope)
    {
        EventPublished?.Invoke(envelope);
        _ = hubContext.Clients.All.SendAsync("serverEvent", envelope);
    }

    private void PublishWorkflowBatch(Guid workflowId, IReadOnlyList<ServerEventEnvelopeDto> envelopes)
    {
        var jobUpserts = Envelopes<JobSummaryDto>(envelopes, "job.upserted");
        var workflow = Envelopes<WorkflowSummaryDto>(envelopes, "workflow.upserted").LastOrDefault();
        var searchUpdates = Envelopes<SearchUpdatedDto>(envelopes, "search.updated");
        var progress = Envelopes<DownloadProgressEventDto>(envelopes, "download.progress");
        var activity = envelopes
            .Where(envelope => envelope.Type is not "job.upserted"
                and not "workflow.upserted"
                and not "search.updated"
                and not "download.progress")
            .ToList();

        var orderedEnvelopes = envelopes
            .Where(envelope => envelope.Type == "job.upserted")
            .Concat(envelopes.Where(envelope => envelope.Type == "workflow.upserted"))
            .Concat(envelopes.Where(envelope => envelope.Type == "search.updated"))
            .Concat(activity)
            .Concat(envelopes.Where(envelope => envelope.Type == "download.progress"))
            .ToList();

        foreach (var envelope in orderedEnvelopes)
            EventPublished?.Invoke(envelope);

        var sequence = workflowBatchSequences.AddOrUpdate(workflowId, 1, static (_, current) => current + 1);
        var batch = new WorkflowUpdateBatchDto(
            sequence,
            DateTimeOffset.UtcNow,
            workflowId,
            workflow,
            jobUpserts,
            searchUpdates,
            progress,
            activity);

        BatchPublished?.Invoke(batch);

        _ = hubContext.Clients
            .Groups(ServerEventHub.AllEventsGroup, ServerEventHub.WorkflowGroupName(workflowId))
            .SendAsync("workflowUpdateBatch", batch);
    }

    private static List<T> Envelopes<T>(IReadOnlyList<ServerEventEnvelopeDto> envelopes, string type)
        => envelopes
            .Where(envelope => envelope.Type == type)
            .Select(envelope => (T)envelope.Payload)
            .ToList();

    public void Dispose()
    {
        coalescer.Dispose();
    }

    private JobSummaryDto GetSummary(Job job)
        => stateStore.GetJobSummary(job.Id) ?? new JobSummaryDto(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            EngineStateStore.GetJobKind(job),
            EngineStateStore.ToServerJobLifecycleState(job.LifecycleState),
            EngineStateStore.ToServerJobActivityPhase(job.ActivityPhase),
            job.ActivityUntilUtc,
            EngineStateStore.ToServerJobTerminalOutcome(job.TerminalOutcome),
            EngineStateStore.ToServerJobSkipReason(job.SkipReason),
            job.ItemName,
            job.ToString(noInfo: true),
            EngineStateStore.ToServerFailureReason(job.FailureReason),
            job.FailureMessage,
            null,
            null,
            null,
            job.Discovery?.ResultCount,
            job.Discovery?.LockedFileCount,
            job.Config?.AppliedAutoProfiles?.ToList() ?? [],
            [],
            job.FailureDetail,
            EngineStateStore.ToServerJobCancellationSource(job.CancellationSource));

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
}
