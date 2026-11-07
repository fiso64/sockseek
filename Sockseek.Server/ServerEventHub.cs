using Microsoft.AspNetCore.SignalR;
using Sockseek.Api;

namespace Sockseek.Server;

/// <summary>
/// SignalR hub for typed live state batches. Daemon and chat scopes may share a
/// connection; workflow mode remains exclusive so one change is never delivered
/// twice to that connection.
/// </summary>
public sealed class ServerEventHub(
    EngineStateStore stateStore,
    Planning.SearchViewCoordinator searchViews,
    Persistence.JobReadService jobs,
    Persistence.TransferReadService transfers,
    ServerEventBroadcaster events) : Hub
{
    public async IAsyncEnumerable<JobListUpdateDto> ObserveJobList(
        JobListScopeDto scope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in jobs.ObserveJobListAsync(scope, events, cancellationToken))
            yield return update;
    }

    public async IAsyncEnumerable<JobObservationUpdateDto> ObserveJobs(
        Guid jobId, IReadOnlyList<Guid> jobIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in jobs.ObserveJobsAsync(jobId, jobIds, events, cancellationToken))
            yield return update;
    }

    public async IAsyncEnumerable<TransferTimelineUpdateDto> ObserveTransferTimeline(
        TransferTimelineScopeDto scope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in transfers.ObserveTransferTimelineAsync(scope, events, cancellationToken))
            yield return update;
    }

    /// <summary>Observe a projection until complete or the client cancels the stream.</summary>
    public IAsyncEnumerable<SearchViewSummaryDto> ObserveSearchView(Guid viewId, CancellationToken cancellationToken)
        => searchViews.ObserveAsync(viewId, cancellationToken);

    private const string AllEventsGroupName = "events:all";
    private const string SummaryEventsGroupName = "events:overview";
    private const string ModeKey = "sockseek:stream-mode";
    private const string DaemonMode = "daemon";
    private const string WorkflowMode = "workflow";
    private const string ChatMode = "chat";
    private const string DaemonChatMode = "daemonChat";
    private const string ChatScopesKey = "sockseek:chat-scopes";
    private const string WorkflowScopesKey = "sockseek:workflow-scopes";

    public override async Task OnConnectedAsync()
    {
        var requested = Context.GetHttpContext()?.Request.Query["liveProtocol"].ToString();
        if (!int.TryParse(requested, out var version) || version != LiveProtocol.Version)
        {
            Context.Abort();
            throw new HubException(
                $"Incompatible live protocol. Server requires version {LiveProtocol.Version}.");
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribes this connection to the daemon-scoped stream containing every live
    /// workflow change and daemon-global activity edge. Overview includes root jobs
    /// and global totals; displayed children/files use a narrow observation.
    /// </summary>
    public async Task SubscribeAll(DaemonStateDetail detail)
    {
        if (!Enum.IsDefined(detail)) throw new HubException("Invalid daemon detail mode.");
        if (Context.Items.TryGetValue(ModeKey, out var mode) && Equals(mode, WorkflowMode))
            throw new HubException("Cannot mix daemon and workflow subscriptions on one connection.");

        Context.Items[ModeKey] = Equals(mode, ChatMode) ? DaemonChatMode : DaemonMode;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId,
            detail == DaemonStateDetail.Full ? SummaryEventsGroupName : AllEventsGroupName);
        await Groups.AddToGroupAsync(Context.ConnectionId,
            detail == DaemonStateDetail.Full ? AllEventsGroupName : SummaryEventsGroupName);
        events.SetDaemonReader(Context.ConnectionId, detail);
    }

    /// <summary>
    /// Removes this connection from the all-events subscription.
    /// </summary>
    public async Task UnsubscribeAll()
    {
        events.SetDaemonReader(Context.ConnectionId, null);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AllEventsGroupName);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SummaryEventsGroupName);
        var scopes = ChatScopes();
        bool hasChat;
        lock (scopes)
            hasChat = scopes.Count > 0;
        if (hasChat)
            Context.Items[ModeKey] = ChatMode;
        else
            Context.Items.Remove(ModeKey);
    }

    /// <summary>
    /// Subscribes this connection to events for one workflow.
    /// This is mainly useful for narrow clients such as a remote CLI tracking one submitted workflow.
    /// </summary>
    public async Task SubscribeWorkflow(Guid workflowId)
    {
        if (workflowId == Guid.Empty)
            throw new HubException("A workflow subscription requires a workflow ID.");
        if (Context.Items.TryGetValue(ModeKey, out var mode)
            && !Equals(mode, WorkflowMode))
            throw new HubException("Cannot mix workflow and daemon/chat subscriptions on one connection.");

        Context.Items[ModeKey] = WorkflowMode;
        var scopes = WorkflowScopes();
        bool added;
        lock (scopes)
            added = scopes.Add(workflowId);
        if (added)
            stateStore.ReserveWorkflowStream(workflowId);

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));
        }
        catch
        {
            if (added)
            {
                lock (scopes)
                    scopes.Remove(workflowId);
                stateStore.ReleaseWorkflowStreamReservation(workflowId);
            }
            throw;
        }
    }

    /// <summary>
    /// Removes this connection from the workflow-specific event subscription.
    /// </summary>
    public async Task UnsubscribeWorkflow(Guid workflowId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));
        var scopes = WorkflowScopes();
        bool removed;
        bool hasWorkflows;
        lock (scopes)
        {
            removed = scopes.Remove(workflowId);
            hasWorkflows = scopes.Count > 0;
        }
        if (removed)
            stateStore.ReleaseWorkflowStreamReservation(workflowId);
        if (!hasWorkflows)
            Context.Items.Remove(ModeKey);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        events.SetDaemonReader(Context.ConnectionId, null);
        if (Context.Items.TryGetValue(WorkflowScopesKey, out object? value)
            && value is HashSet<Guid> scopes)
        {
            Guid[] workflowIds;
            lock (scopes)
            {
                workflowIds = scopes.ToArray();
                scopes.Clear();
            }
            foreach (Guid workflowId in workflowIds)
                stateStore.ReleaseWorkflowStreamReservation(workflowId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Subscribes to one stable-id conversation or room stream.</summary>
    public async Task SubscribeChat(StateStreamScopeDto scope)
    {
        scope.Validate();
        if (scope.Kind is not (StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
            throw new HubException("A chat subscription requires a conversation or room scope.");
        if (Context.Items.TryGetValue(ModeKey, out var mode) && Equals(mode, WorkflowMode))
            throw new HubException("Cannot mix chat and workflow subscriptions on one connection.");
        Context.Items[ModeKey] = Equals(mode, DaemonMode) || Equals(mode, DaemonChatMode)
            ? DaemonChatMode
            : ChatMode;
        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroupName(scope));
        var scopes = ChatScopes();
        lock (scopes)
            scopes.Add(scope);
    }

    public async Task UnsubscribeChat(StateStreamScopeDto scope)
    {
        scope.Validate();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroupName(scope));
        var scopes = ChatScopes();
        bool hasChat;
        lock (scopes)
        {
            scopes.Remove(scope);
            hasChat = scopes.Count > 0;
        }
        if (!hasChat)
        {
            if (Context.Items.TryGetValue(ModeKey, out object? mode)
                && Equals(mode, DaemonChatMode))
            {
                Context.Items[ModeKey] = DaemonMode;
            }
            else if (Equals(mode, ChatMode))
            {
                Context.Items.Remove(ModeKey);
            }
        }
    }

    public async Task SubscribeUserBrowse(Guid browseId)
    {
        var scope = StateStreamScopeDto.UserBrowse(browseId);
        scope.Validate();
        if (Context.Items.TryGetValue(ModeKey, out var mode) && Equals(mode, WorkflowMode))
            throw new HubException("Cannot mix user-browse and workflow subscriptions on one connection.");
        Context.Items[ModeKey] = Equals(mode, DaemonMode) || Equals(mode, DaemonChatMode)
            ? DaemonChatMode
            : ChatMode;
        await Groups.AddToGroupAsync(Context.ConnectionId, UserBrowseGroupName(browseId));
        var scopes = ChatScopes();
        lock (scopes)
            scopes.Add(scope);
    }

    public async Task UnsubscribeUserBrowse(Guid browseId)
    {
        var scope = StateStreamScopeDto.UserBrowse(browseId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UserBrowseGroupName(browseId));
        var scopes = ChatScopes();
        bool hasAuxiliary;
        lock (scopes)
        {
            scopes.Remove(scope);
            hasAuxiliary = scopes.Count > 0;
        }
        if (!hasAuxiliary)
        {
            if (Context.Items.TryGetValue(ModeKey, out object? mode)
                && Equals(mode, DaemonChatMode))
                Context.Items[ModeKey] = DaemonMode;
            else if (Equals(mode, ChatMode))
                Context.Items.Remove(ModeKey);
        }
    }

    private HashSet<StateStreamScopeDto> ChatScopes()
    {
        if (Context.Items.TryGetValue(ChatScopesKey, out object? value)
            && value is HashSet<StateStreamScopeDto> scopes)
        {
            return scopes;
        }
        var created = new HashSet<StateStreamScopeDto>();
        Context.Items[ChatScopesKey] = created;
        return created;
    }

    private HashSet<Guid> WorkflowScopes()
    {
        if (Context.Items.TryGetValue(WorkflowScopesKey, out object? value)
            && value is HashSet<Guid> scopes)
        {
            return scopes;
        }
        var created = new HashSet<Guid>();
        Context.Items[WorkflowScopesKey] = created;
        return created;
    }

    internal static string WorkflowGroupName(Guid workflowId)
        => $"workflow:{workflowId:N}";

    internal static string ChatGroupName(StateStreamScopeDto scope)
        => scope.Kind switch
        {
            StateStreamScopeKind.ChatConversation => $"chat:conversation:{scope.ChatTargetId!.Value:N}",
            StateStreamScopeKind.ChatRoom => $"chat:room:{scope.ChatTargetId!.Value:N}",
            _ => throw new ArgumentException("The scope is not a chat scope.", nameof(scope)),
        };

    internal static string UserBrowseGroupName(Guid browseId)
        => $"user-browse:{browseId:N}";

    internal static string AllEventsGroup => AllEventsGroupName;
    internal static string SummaryEventsGroup => SummaryEventsGroupName;
}
