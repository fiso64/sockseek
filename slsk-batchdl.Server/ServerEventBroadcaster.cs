using Microsoft.AspNetCore.SignalR;
using Sldl.Core;
using Sldl.Core.Jobs;

namespace Sldl.Server;

public sealed class ServerEventBroadcaster : IDisposable
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private readonly EngineStateStore stateStore;
    private readonly ServerEventCoalescer coalescer;
    private long nextSequence;

    public ServerEventBroadcaster(EngineStateStore stateStore, EngineSupervisor supervisor, IHubContext<ServerEventHub> hubContext)
    {
        this.stateStore = stateStore;
        this.hubContext = hubContext;
        coalescer = new ServerEventCoalescer(PublishImmediate);
        stateStore.JobUpserted += summary => coalescer.Publish("job.upserted", summary);
        stateStore.WorkflowUpserted += summary => coalescer.Publish("workflow.upserted", summary);
        stateStore.SearchUpdated += update => coalescer.Publish("search.updated", update);
        supervisor.EngineCreated += AttachEngine;
    }

    private void AttachEngine(DownloadEngine engine)
    {
        new EngineEventDtoAdapter(GetSummary, coalescer.Publish).Attach(engine.Events);
    }

    private void PublishImmediate(string type, object payload)
    {
        var descriptor = ServerEventCatalog.Describe(type);
        var envelope = new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            descriptor.Category,
            descriptor.SnapshotInvalidation,
            GetWorkflowId(payload),
            payload);

        _ = descriptor.Category != ServerEventCatalog.StateCategory && envelope.WorkflowId is Guid workflowId
            ? hubContext.Clients
                .Groups(ServerEventHub.AllEventsGroup, ServerEventHub.WorkflowGroupName(workflowId))
                .SendAsync("serverEvent", envelope)
            : hubContext.Clients.All.SendAsync("serverEvent", envelope);
    }

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
            EngineStateStore.ToServerJobState(job.State),
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
            []);

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
            SongSearchingEventDto e => e.WorkflowId,
            DownloadStartedEventDto e => e.WorkflowId,
            DownloadProgressEventDto e => e.WorkflowId,
            DownloadStateChangedEventDto e => e.WorkflowId,
            SongStateChangedEventDto e => e.WorkflowId,
            AlbumDownloadStartedEventDto e => e.Summary.WorkflowId,
            AlbumTrackDownloadStartedEventDto e => e.Summary.WorkflowId,
            AlbumDownloadCompletedEventDto e => e.Summary.WorkflowId,
            JobFolderRetrievingEventDto e => e.Summary.WorkflowId,
            OnCompleteStartedEventDto e => e.WorkflowId,
            OnCompleteEndedEventDto e => e.WorkflowId,
            TrackBatchResolvedEventDto e => e.Summary.WorkflowId,
            _ => null,
        };
}
