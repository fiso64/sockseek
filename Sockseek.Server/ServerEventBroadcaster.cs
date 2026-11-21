using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Snapshots;

namespace Sockseek.Server;

/// <summary>Publishes coalesced state and activity batches to scoped SignalR groups.</summary>
public sealed class ServerEventBroadcaster : IDisposable, IAsyncDisposable, IHostedService
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private readonly EngineStateStore stateStore;
    private readonly EngineSupervisor supervisor;
    private readonly IAsyncDisposable publication;
    private readonly BoundedStateBatchDispatcher dispatcher;
    private readonly ILogger<ServerEventBroadcaster> logger;
    private readonly ConcurrentDictionary<string, DaemonStateDetail> daemonReaders = [];
    private int disposeState;

    internal void SetDaemonReader(string connectionId, DaemonStateDetail? detail)
    {
        if (detail is { } value) daemonReaders[connectionId] = value;
        else daemonReaders.TryRemove(connectionId, out _);
    }

    public event Action<StateUpdateBatchDto>? BatchPublished;
    internal event Action? Flushed;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal ServerEventBroadcaster(
        EngineStateStore stateStore,
        EngineSupervisor supervisor,
        IHubContext<ServerEventHub> hubContext)
        : this(
            stateStore,
            supervisor,
            hubContext,
            NullLogger<ServerEventBroadcaster>.Instance,
            NullLoggerFactory.Instance)
    {
    }

    public ServerEventBroadcaster(
        EngineStateStore stateStore,
        EngineSupervisor supervisor,
        IHubContext<ServerEventHub> hubContext,
        ILogger<ServerEventBroadcaster> logger,
        ILoggerFactory loggerFactory)
    {
        this.stateStore = stateStore;
        this.supervisor = supervisor;
        this.hubContext = hubContext;
        this.logger = logger;
        dispatcher = new BoundedStateBatchDispatcher(
            SendBatchAsync,
            logger: loggerFactory.CreateLogger<BoundedStateBatchDispatcher>());
        stateStore.StateBatchPublished += PublishBatch;
        stateStore.StateUpdatesFlushed += NotifyFlushed;
        publication = stateStore.StartStatePublication();
        supervisor.EngineCreated += AttachEngine;
    }

    private void AttachEngine(DownloadEngine engine)
        => new EngineActivityDtoAdapter(stateStore)
            .Attach(engine.Events, engine.SearchEvents);

    private void PublishBatch(StateUpdate batch)
    {
        if (BatchPublished is { } handlers)
        {
            foreach (Action<StateUpdateBatchDto> handler in Delegate.EnumerateInvocationList(handlers))
            {
                try { handler(batch.ToDto()); }
                catch (Exception ex)
                {
                    ServerLogMessages.LiveBatchObserverFailed(logger, ex);
                }
            }
        }
        dispatcher.TryPublish(batch);
    }

    private void NotifyFlushed()
    {
        if (Flushed is not { } handlers) return;
        foreach (Action handler in Delegate.EnumerateInvocationList(handlers))
        {
            try { handler(); }
            catch (Exception ex) { ServerLogMessages.LiveBatchObserverFailed(logger, ex); }
        }
    }

    private async Task SendBatchAsync(
        StateUpdate batch,
        CancellationToken cancellationToken)
    {
        if (batch.Scope.Kind == StateStreamScopeKind.Daemon
            && daemonReaders.Values.Contains(DaemonStateDetail.Overview))
        {
            // Keep the same cursor, including empty filtered frames: a summary
            // subscriber must not interpret omitted transfer detail as a gap.
            var summary = batch with { State = batch.State with
            {
                Jobs = batch.State.Jobs.Where(job => job.Current.ParentJobId is null).ToArray(),
                Transfers = [], RemovedTransferIds = [],
            } };
            await hubContext.Clients.Group(ServerEventHub.SummaryEventsGroup).SendAsync(
                LiveProtocol.StateUpdateBatchMethod, summary.ToDto(), cancellationToken).ConfigureAwait(false);
        }
        // Unwatched child rows must not allocate full wire graphs merely to send
        // to an empty group. Overview and narrow observations map their own facts.
        if (batch.Scope.Kind == StateStreamScopeKind.Daemon
            && !daemonReaders.Values.Contains(DaemonStateDetail.Full)) return;
        if (batch.Scope is { Kind: StateStreamScopeKind.Workflow, WorkflowId: { } workflowId }
            && !stateStore.HasWorkflowStreamReaders(workflowId)) return;
        var clients = batch.Scope.Kind switch
        {
            StateStreamScopeKind.Daemon => hubContext.Clients.Group(ServerEventHub.AllEventsGroup),
            StateStreamScopeKind.Workflow => hubContext.Clients.Group(
                ServerEventHub.WorkflowGroupName(batch.Scope.WorkflowId!.Value)),
            StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom
                => hubContext.Clients.Group(ServerEventHub.ChatGroupName(batch.Scope)),
            StateStreamScopeKind.UserBrowse => hubContext.Clients.Group(
                ServerEventHub.UserBrowseGroupName(batch.Scope.UserBrowseId!.Value)),
            _ => throw new ArgumentOutOfRangeException(),
        };
        await clients.SendAsync(
            LiveProtocol.StateUpdateBatchMethod, batch.ToDto(), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;
        await publication.DisposeAsync().ConfigureAwait(false);
        stateStore.StateBatchPublished -= PublishBatch;
        stateStore.StateUpdatesFlushed -= NotifyFlushed;
        supervisor.EngineCreated -= AttachEngine;
        await dispatcher.DisposeAsync().ConfigureAwait(false);
    }
}
