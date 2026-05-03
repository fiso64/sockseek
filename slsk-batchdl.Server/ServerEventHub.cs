using Microsoft.AspNetCore.SignalR;

namespace Sldl.Server;

/// <summary>
/// SignalR hub for live server events. Clients receive envelopes through the
/// <c>serverEvent</c> client method and should use HTTP endpoints as the canonical state source.
/// </summary>
public sealed class ServerEventHub : Hub
{
    private const string AllEventsGroupName = "events:all";

    /// <summary>
    /// Subscribes this connection to every server event.
    /// GUI clients normally use this and refresh HTTP snapshots when events indicate stale state.
    /// </summary>
    public Task SubscribeAll()
        => Groups.AddToGroupAsync(Context.ConnectionId, AllEventsGroupName);

    /// <summary>
    /// Removes this connection from the all-events subscription.
    /// </summary>
    public Task UnsubscribeAll()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AllEventsGroupName);

    /// <summary>
    /// Subscribes this connection to events for one workflow.
    /// This is mainly useful for narrow clients such as a remote CLI tracking one submitted workflow.
    /// </summary>
    public Task SubscribeWorkflow(Guid workflowId)
        => Groups.AddToGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));

    /// <summary>
    /// Removes this connection from the workflow-specific event subscription.
    /// </summary>
    public Task UnsubscribeWorkflow(Guid workflowId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));

    internal static string WorkflowGroupName(Guid workflowId)
        => $"workflow:{workflowId:N}";

    internal static string AllEventsGroup => AllEventsGroupName;
}
