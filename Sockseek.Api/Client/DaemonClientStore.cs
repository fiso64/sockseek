namespace Sockseek.Api;

public enum DaemonClientApplyStatus
{
    Applied,
    IgnoredStale,
    RecoveryRequired,
}

public enum DaemonClientRecoveryReason
{
    MissingSnapshot,
    EpochChanged,
    SequenceGap,
}

public sealed record DaemonClientUpdate(
    StateStreamScopeDto Scope,
    StateStreamPositionDto Position,
    DaemonClientApplyStatus Status,
    StateDeltaDto State,
    IReadOnlyList<ActivityEventDto> Activity,
    DaemonClientRecoveryReason? RecoveryReason = null,
    bool IsSnapshot = false,
    IReadOnlyList<WorkflowSummaryDto>? ChangedWorkflows = null,
    IReadOnlyList<JobSummaryDto>? ChangedJobs = null,
    IReadOnlyList<TransferStateDto>? ChangedTransfers = null,
    UserBrowseDto? ChangedUserBrowse = null)
{
    public IReadOnlyList<WorkflowSummaryDto> ChangedWorkflows { get; init; } =
        ChangedWorkflows ?? [];
    public IReadOnlyList<JobSummaryDto> ChangedJobs { get; init; } =
        ChangedJobs ?? [];
    public IReadOnlyList<TransferStateDto> ChangedTransfers { get; init; } =
        ChangedTransfers ?? [];
}

public sealed record WorkflowJobGroupDto(
    Guid WorkflowId,
    WorkflowSummaryDto? Workflow,
    IReadOnlyList<JobSummaryDto> Jobs);

/// <summary>
/// One coherent read of the replicated live partition. Explicitly hydrated
/// history is intentionally excluded.
/// </summary>
public sealed record DaemonClientStateView(
    DaemonStateDto? Daemon,
    IReadOnlyList<WorkflowSummaryDto> Workflows,
    IReadOnlyList<JobSummaryDto> Jobs,
    IReadOnlyList<SearchStateDto> Searches,
    IReadOnlyList<TransferStateDto> Transfers,
    LiveTransferSummaryDto? TransferSummary);

/// <summary>
/// Thread-safe reducer and query store shared by local, remote, and GUI clients.
/// Live replication and explicitly paged history are separate partitions.
/// </summary>
public sealed class DaemonClientStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, WorkflowStateDto> liveWorkflows = [];
    private readonly Dictionary<Guid, JobStateDto> liveJobs = [];
    private readonly Dictionary<Guid, SearchStateDto> liveSearches = [];
    private readonly Dictionary<Guid, TransferStateDto> liveTransfers = [];
    private readonly Dictionary<Guid, WorkflowSummaryDto> historyWorkflows = [];
    private readonly Dictionary<Guid, JobSummaryDto> historyJobs = [];
    private readonly Dictionary<Guid, UserNotificationDto> liveNotifications = [];
    private readonly Dictionary<StateStreamScopeDto, ChatTargetSnapshotDto> liveChatTargets = [];
    private readonly Dictionary<Guid, UserBrowseDto> liveUserBrowses = [];
    private readonly Dictionary<StateStreamScopeDto, StateStreamPositionDto> positions = [];
    private readonly HashSet<StateStreamScopeDto> staleScopes = [];
    private DaemonStateDto? daemon;
    private LiveTransferSummaryDto? transferSummary;

    public DaemonClientUpdate ApplySnapshot(StateSnapshotDto snapshot)
    {
        snapshot.Scope.Validate();
        ValidateSnapshotScope(snapshot);

        lock (gate)
        {
            ReplaceScope(snapshot);
            positions[snapshot.Scope] = snapshot.Position;
            staleScopes.Remove(snapshot.Scope);
            return new DaemonClientUpdate(
                snapshot.Scope,
                snapshot.Position,
                DaemonClientApplyStatus.Applied,
                StateDeltaDto.Empty,
                [],
                IsSnapshot: true,
                ChangedWorkflows: snapshot.Workflows.Select(row => row.Summary).ToList(),
                ChangedJobs: snapshot.Jobs.Select(row => row.ToSummary()).ToList(),
                ChangedTransfers: snapshot.Transfers,
                ChangedUserBrowse: snapshot.UserBrowse);
        }
    }

    public DaemonClientUpdate Apply(StateUpdateBatchDto batch)
    {
        batch.Scope.Validate();
        ValidateBatch(batch);

        lock (gate)
        {
            if (!positions.TryGetValue(batch.Scope, out var current))
                return RequireRecovery(batch, DaemonClientRecoveryReason.MissingSnapshot);

            if (current.Epoch != batch.Epoch)
                return RequireRecovery(batch, DaemonClientRecoveryReason.EpochChanged);

            if (staleScopes.Contains(batch.Scope))
                return RequireRecovery(batch, DaemonClientRecoveryReason.SequenceGap);

            if (batch.Sequence <= current.Sequence)
            {
                return new DaemonClientUpdate(
                    batch.Scope,
                    current,
                    DaemonClientApplyStatus.IgnoredStale,
                    StateDeltaDto.Empty,
                    []);
            }

            if (batch.PreviousSequence > current.Sequence)
                return RequireRecovery(batch, DaemonClientRecoveryReason.SequenceGap);

            var applied = ApplyState(batch.State);
            var activity = batch.Activity
                .Where(item => item.Sequence > current.Sequence)
                .OrderBy(item => item.Sequence)
                .ToList();
            var position = new StateStreamPositionDto(batch.Epoch, batch.Sequence);
            positions[batch.Scope] = position;
            return new DaemonClientUpdate(
                batch.Scope,
                position,
                DaemonClientApplyStatus.Applied,
                batch.State,
                activity,
                ChangedWorkflows: applied.Workflows,
                ChangedJobs: applied.Jobs,
                ChangedTransfers: applied.Transfers,
                ChangedUserBrowse: applied.UserBrowse);
        }
    }

    public bool IsStale(StateStreamScopeDto scope)
    {
        lock (gate)
            return staleScopes.Contains(scope);
    }

    public StateStreamPositionDto? GetPosition(StateStreamScopeDto scope)
    {
        lock (gate)
            return positions.GetValueOrDefault(scope);
    }

    public DaemonStateDto? GetDaemon()
    {
        lock (gate)
            return daemon;
    }

    /// <summary>Daemon-wide totals, or null before a daemon-scoped snapshot.</summary>
    public LiveTransferSummaryDto? GetTransferSummary()
    {
        lock (gate) return transferSummary;
    }

    public SharingStateDto? GetSharing()
    {
        lock (gate)
            return daemon?.Sharing;
    }

    public UploadRuntimeStateDto? GetUploadRuntime()
    {
        lock (gate)
            return daemon?.Uploads;
    }

    /// <summary>Returns the bounded notification tail received on the daemon live stream.</summary>
    public IReadOnlyList<UserNotificationDto> GetLiveNotifications()
    {
        lock (gate)
            return liveNotifications.Values.OrderByDescending(item => item.Sequence).ToList();
    }

    public ChatTargetSnapshotDto? GetChatTarget(StateStreamScopeDto scope)
    {
        scope.Validate();
        lock (gate)
            return liveChatTargets.GetValueOrDefault(scope);
    }

    public UserBrowseDto? GetUserBrowse(Guid browseId)
    {
        lock (gate)
            return liveUserBrowses.GetValueOrDefault(browseId);
    }

    public void RemoveUserBrowse(Guid browseId)
    {
        var scope = StateStreamScopeDto.UserBrowse(browseId);
        lock (gate)
        {
            liveUserBrowses.Remove(browseId);
            positions.Remove(scope);
            staleScopes.Remove(scope);
        }
    }

    public void RemoveChatTarget(StateStreamScopeDto scope)
    {
        scope.Validate();
        if (scope.Kind is not (StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
            throw new ArgumentException("A chat scope is required.", nameof(scope));
        lock (gate)
        {
            liveChatTargets.Remove(scope);
            positions.Remove(scope);
            staleScopes.Remove(scope);
        }
    }

    public IReadOnlyList<TransferStateDto> GetActiveTransfers()
    {
        lock (gate)
            return liveTransfers.Values
                .Where(transfer => !transfer.Status.IsTerminal)
                .OrderBy(transfer => transfer.TransferId)
                .ToList();
    }

    public DaemonClientStateView GetLiveStateView()
    {
        lock (gate)
        {
            return new DaemonClientStateView(
                daemon,
                liveWorkflows.Values
                    .Select(workflow => workflow.Summary)
                    .OrderBy(workflow => workflow.Title)
                    .ThenBy(workflow => workflow.WorkflowId)
                    .ToList(),
                liveJobs.Values
                    .Select(job => job.ToSummary())
                    .OrderBy(job => job.DisplayId)
                    .ThenBy(job => job.JobId)
                    .ToList(),
                liveSearches.Values
                    .OrderBy(search => search.JobId)
                    .ToList(),
                liveTransfers.Values
                    .OrderBy(transfer => transfer.TransferId)
                    .ToList(), transferSummary);
        }
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        lock (gate)
            return CombinedWorkflows().OrderBy(workflow => workflow.Title).ThenBy(workflow => workflow.WorkflowId).ToList();
    }

    public WorkflowSummaryDto? GetWorkflow(Guid workflowId)
    {
        lock (gate)
            return liveWorkflows.TryGetValue(workflowId, out var live)
                ? live.Summary
                : historyWorkflows.GetValueOrDefault(workflowId);
    }

    public IReadOnlyList<JobSummaryDto> GetJobs()
    {
        lock (gate)
            return CombinedJobs().OrderBy(job => job.DisplayId).ThenBy(job => job.JobId).ToList();
    }

    public JobSummaryDto? GetJob(Guid jobId)
    {
        lock (gate)
            return liveJobs.TryGetValue(jobId, out var live)
                ? live.ToSummary()
                : historyJobs.GetValueOrDefault(jobId);
    }

    public IReadOnlyList<JobSummaryDto> GetWorkflowJobs(Guid workflowId)
    {
        lock (gate)
            return CombinedJobs()
                .Where(job => job.WorkflowId == workflowId)
                .OrderBy(job => job.DisplayId)
                .ThenBy(job => job.JobId)
                .ToList();
    }

    public IReadOnlyList<WorkflowJobGroupDto> GetJobsGroupedByWorkflow()
    {
        lock (gate)
        {
            var workflows = CombinedWorkflows().ToDictionary(workflow => workflow.WorkflowId);
            return CombinedJobs()
                .GroupBy(job => job.WorkflowId)
                .OrderBy(group => workflows.GetValueOrDefault(group.Key)?.Title)
                .ThenBy(group => group.Key)
                .Select(group => new WorkflowJobGroupDto(
                    group.Key,
                    workflows.GetValueOrDefault(group.Key),
                    group.OrderBy(job => job.DisplayId).ThenBy(job => job.JobId).ToList()))
                .ToList();
        }
    }

    public IReadOnlyList<JobSummaryDto> GetActiveJobs()
    {
        lock (gate)
            return CombinedJobs()
                .Where(job => job.LifecycleState != ServerJobLifecycleState.Terminal)
                .OrderBy(job => job.DisplayId)
                .ToList();
    }

    public IReadOnlyList<JobSummaryDto> GetTerminalJobs()
    {
        lock (gate)
            return CombinedJobs()
                .Where(job => job.LifecycleState == ServerJobLifecycleState.Terminal)
                .OrderBy(job => job.DisplayId)
                .ToList();
    }

    public IReadOnlyList<TransferStateDto> GetTransfers()
    {
        lock (gate)
            return liveTransfers.Values.OrderBy(transfer => transfer.TransferId).ToList();
    }

    public TransferStateDto? GetTransfer(Guid transferId)
    {
        lock (gate)
            return liveTransfers.GetValueOrDefault(transferId);
    }

    public IReadOnlyList<TransferStateDto> GetJobTransfers(Guid jobId)
    {
        lock (gate)
            return liveTransfers.Values
                .Where(transfer => transfer.Identity.JobId == jobId)
                .OrderBy(transfer => transfer.TransferId)
                .ToList();
    }

    public SearchStateDto? GetSearchState(Guid jobId)
    {
        lock (gate)
            return liveSearches.GetValueOrDefault(jobId);
    }

    public void MergeWorkflowHistory(IEnumerable<WorkflowSummaryDto> workflows)
    {
        lock (gate)
        {
            foreach (var workflow in workflows)
                historyWorkflows[workflow.WorkflowId] = workflow;
        }
    }

    public void MergeJobHistory(IEnumerable<JobSummaryDto> jobs)
    {
        lock (gate)
        {
            foreach (var job in jobs)
                historyJobs[job.JobId] = job;
        }
    }

    private DaemonClientUpdate RequireRecovery(
        StateUpdateBatchDto batch,
        DaemonClientRecoveryReason reason)
    {
        staleScopes.Add(batch.Scope);
        var position = positions.GetValueOrDefault(batch.Scope)
            ?? new StateStreamPositionDto(batch.Epoch, 0);
        return new DaemonClientUpdate(
            batch.Scope,
            position,
            DaemonClientApplyStatus.RecoveryRequired,
            StateDeltaDto.Empty,
            [],
            reason);
    }

    private void ReplaceScope(StateSnapshotDto snapshot)
    {
        if (snapshot.Scope.Kind == StateStreamScopeKind.Daemon)
        {
            liveWorkflows.Clear();
            liveJobs.Clear();
            liveSearches.Clear();
            liveTransfers.Clear();
            // Notification rows are an opportunistic live tail, not part of
            // the daemon snapshot. Clear them across recovery so retained or
            // read rows are rehydrated authoritatively through the paged API.
            liveNotifications.Clear();
            daemon = snapshot.Daemon;
            transferSummary = snapshot.TransferSummary;
        }
        else if (snapshot.Scope.Kind == StateStreamScopeKind.Workflow)
        {
            var workflowId = snapshot.Scope.WorkflowId!.Value;
            liveWorkflows.Remove(workflowId);
            RemoveWhere(liveJobs, pair => pair.Value.Display.WorkflowId == workflowId);
            RemoveWhere(liveSearches, pair => pair.Value.WorkflowId == workflowId);
            RemoveWhere(liveTransfers, pair => pair.Value.Identity.WorkflowId == workflowId);
        }
        else if (snapshot.Scope.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom)
        {
            if (snapshot.ChatTarget is null)
                throw new ArgumentException("A chat snapshot requires chat target state.");
            liveChatTargets[snapshot.Scope] = snapshot.ChatTarget;
        }
        else
        {
            if (snapshot.UserBrowse is null)
                throw new ArgumentException("A user-browse snapshot requires its resource state.");
            liveUserBrowses[snapshot.Scope.UserBrowseId!.Value] = snapshot.UserBrowse;
        }

        foreach (var workflow in snapshot.Workflows)
            liveWorkflows[workflow.Summary.WorkflowId] = workflow;
        foreach (var job in snapshot.Jobs)
            liveJobs[job.JobId] = job;
        foreach (var search in snapshot.Searches)
            liveSearches[search.JobId] = search;
        foreach (var transfer in snapshot.Transfers)
            liveTransfers[transfer.TransferId] = transfer;
    }

    private AppliedState ApplyState(StateDeltaDto state)
    {
        if (state.TransferSummary is { } summary) transferSummary = summary;
        if (state.Daemon != null && (daemon == null || state.Daemon.Revision > daemon.Revision))
            daemon = state.Daemon;

        foreach (var workflow in state.Workflows)
        {
            var workflowId = workflow.Summary.WorkflowId;
            if (!liveWorkflows.TryGetValue(workflowId, out var current)
                || workflow.Revision > current.Revision)
            {
                liveWorkflows[workflowId] = workflow;
            }
        }

        foreach (var delta in state.Jobs)
            ApplyJob(delta);
        foreach (var search in state.Searches)
        {
            if (!liveSearches.TryGetValue(search.JobId, out var current)
                || search.Revision > current.Revision)
            {
                liveSearches[search.JobId] = search;
            }
        }
        foreach (var delta in state.Transfers)
            ApplyTransfer(delta);
        foreach (var notification in state.Notifications ?? [])
            liveNotifications[notification.NotificationId] = notification;
        if (liveNotifications.Count > 200)
        {
            foreach (Guid id in liveNotifications.Values
                         .OrderByDescending(item => item.Sequence)
                         .Skip(200)
                         .Select(item => item.NotificationId)
                         .ToArray())
            {
                liveNotifications.Remove(id);
            }
        }
        foreach (ChatTargetDeltaDto delta in state.ChatTargets ?? [])
            ApplyChatTarget(delta);
        UserBrowseDto? changedUserBrowse = null;
        if (state.UserBrowse is { } browse
            && (!liveUserBrowses.TryGetValue(browse.BrowseId, out UserBrowseDto? currentBrowse)
                || browse.Revision > currentBrowse.Revision))
        {
            liveUserBrowses[browse.BrowseId] = browse;
            changedUserBrowse = browse;
        }

        var changedWorkflows = state.Workflows
            .Select(workflow => liveWorkflows.GetValueOrDefault(workflow.Summary.WorkflowId)?.Summary)
            .OfType<WorkflowSummaryDto>()
            .ToList();
        var changedJobs = state.Jobs
            .Select(job => liveJobs.GetValueOrDefault(job.JobId)?.ToSummary())
            .OfType<JobSummaryDto>()
            .ToList();
        var changedTransfers = state.Transfers
            .Select(transfer => liveTransfers.GetValueOrDefault(transfer.TransferId) ?? transfer.Added)
            .OfType<TransferStateDto>()
            .ToList();

        foreach (var id in state.RemovedTransferIds)
            liveTransfers.Remove(id);
        foreach (var id in state.RemovedSearchJobIds)
            liveSearches.Remove(id);
        foreach (var id in state.RemovedJobIds)
        {
            liveJobs.Remove(id);
            liveSearches.Remove(id);
            RemoveWhere(liveTransfers, pair => pair.Value.Identity.JobId == id);
        }
        foreach (var id in state.RemovedWorkflowIds)
        {
            liveWorkflows.Remove(id);
            RemoveWhere(liveJobs, pair => pair.Value.Display.WorkflowId == id);
            RemoveWhere(liveSearches, pair => pair.Value.WorkflowId == id);
            RemoveWhere(liveTransfers, pair => pair.Value.Identity.WorkflowId == id);
        }

        return new AppliedState(changedWorkflows, changedJobs, changedTransfers, changedUserBrowse);
    }

    private void ApplyJob(JobDeltaDto delta)
    {
        if (delta.Added != null)
        {
            if (delta.Added.JobId != delta.JobId || delta.Added.Revision != delta.Revision)
                throw new ArgumentException("A job add must match its delta id and revision.");
            if (!liveJobs.TryGetValue(delta.JobId, out var addedCurrent)
                || delta.Revision > addedCurrent.Revision)
            {
                liveJobs[delta.JobId] = delta.Added;
            }
            return;
        }

        if (!liveJobs.TryGetValue(delta.JobId, out var current) || delta.Revision <= current.Revision)
            return;

        liveJobs[delta.JobId] = current with
        {
            Revision = delta.Revision,
            Display = delta.Display ?? current.Display,
            Lifecycle = delta.Lifecycle ?? current.Lifecycle,
            Discovery = delta.Discovery ?? current.Discovery,
            Relationships = delta.Relationships ?? current.Relationships,
            Stats = delta.Stats ?? current.Stats,
        };
    }

    private void ApplyTransfer(TransferDeltaDto delta)
    {
        if (delta.Added != null)
        {
            if (delta.Added.TransferId != delta.TransferId || delta.Added.Revision != delta.Revision)
                throw new ArgumentException("A transfer add must match its delta id and revision.");
            if (!liveTransfers.TryGetValue(delta.TransferId, out var addedCurrent)
                || delta.Revision > addedCurrent.Revision)
            {
                liveTransfers[delta.TransferId] = delta.Added;
            }
            return;
        }

        if (!liveTransfers.TryGetValue(delta.TransferId, out var current) || delta.Revision <= current.Revision)
            return;

        liveTransfers[delta.TransferId] = current with
        {
            Revision = delta.Revision,
            Status = delta.Status ?? current.Status,
            Progress = delta.Progress ?? current.Progress,
            Scheduling = delta.Scheduling ?? current.Scheduling,
        };
    }

    private void ApplyChatTarget(ChatTargetDeltaDto delta)
    {
        StateStreamScopeDto scope = delta.Kind == ServerChatTargetKind.Direct
            ? StateStreamScopeDto.ChatConversation(delta.TargetId)
            : StateStreamScopeDto.ChatRoom(delta.TargetId);
        if (!liveChatTargets.TryGetValue(scope, out ChatTargetSnapshotDto? current))
            return;
        var messages = delta.ReplaceMessages
            ? new Dictionary<Guid, ChatMessageDto>()
            : current.Messages.ToDictionary(message => message.MessageId);
        foreach (ChatMessageDto message in delta.Messages ?? [])
            messages[message.MessageId] = message;
        bool truncated = messages.Count > ChatProtocol.LiveMessageTailSize;
        var tail = messages.Values
            .OrderByDescending(message => message.Sequence)
            .Take(ChatProtocol.LiveMessageTailSize)
            .OrderBy(message => message.Sequence)
            .ToArray();
        liveChatTargets[scope] = current with
        {
            Conversation = delta.Conversation ?? current.Conversation,
            Room = delta.Room ?? current.Room,
            Messages = tail,
            HasEarlierMessages = delta.HasEarlierMessages
                ?? (delta.ReplaceMessages ? truncated : current.HasEarlierMessages || truncated),
        };
    }

    private IEnumerable<WorkflowSummaryDto> CombinedWorkflows()
        => historyWorkflows.Values
            .Where(workflow => !liveWorkflows.ContainsKey(workflow.WorkflowId))
            .Concat(liveWorkflows.Values.Select(workflow => workflow.Summary));

    private IEnumerable<JobSummaryDto> CombinedJobs()
        => historyJobs.Values
            .Where(job => !liveJobs.ContainsKey(job.JobId))
            .Concat(liveJobs.Values.Select(job => job.ToSummary()));

    private static void RemoveWhere<TKey, TValue>(
        Dictionary<TKey, TValue> dictionary,
        Func<KeyValuePair<TKey, TValue>, bool> predicate)
        where TKey : notnull
    {
        foreach (var key in dictionary.Where(predicate).Select(pair => pair.Key).ToList())
            dictionary.Remove(key);
    }

    private static void ValidateSnapshotScope(StateSnapshotDto snapshot)
    {
        if (snapshot.Scope.Kind != StateStreamScopeKind.Daemon && snapshot.TransferSummary != null)
            throw new ArgumentException("Transfer totals belong to the daemon scope.");
        if (snapshot.Position.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Snapshot sequence cannot be negative.");

        if (snapshot.Scope.Kind == StateStreamScopeKind.Workflow)
        {
            var workflowId = snapshot.Scope.WorkflowId!.Value;
            if (snapshot.Daemon != null
                || snapshot.ChatTarget != null
                || snapshot.UserBrowse != null
                || snapshot.Workflows.Any(workflow => workflow.Summary.WorkflowId != workflowId)
                || snapshot.Jobs.Any(job => job.Display.WorkflowId != workflowId)
                || snapshot.Searches.Any(search => search.WorkflowId != workflowId)
                || snapshot.Transfers.Any(transfer => transfer.Identity.WorkflowId != workflowId))
            {
                throw new ArgumentException("A workflow snapshot may contain only its requested workflow and no daemon row.");
            }
            return;
        }

        if (snapshot.Scope.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom)
        {
            var expectedKind = snapshot.Scope.Kind == StateStreamScopeKind.ChatConversation
                ? ServerChatTargetKind.Direct
                : ServerChatTargetKind.Room;
            if (snapshot.Daemon != null
                || snapshot.UserBrowse != null
                || snapshot.Workflows.Count != 0
                || snapshot.Jobs.Count != 0
                || snapshot.Searches.Count != 0
                || snapshot.Transfers.Count != 0
                || snapshot.ChatTarget is not { } target
                || target.TargetId != snapshot.Scope.ChatTargetId
                || target.Kind != expectedKind
                || expectedKind == ServerChatTargetKind.Direct
                    && (target.Conversation is null || target.Room is not null)
                || expectedKind == ServerChatTargetKind.Room
                    && (target.Room is null || target.Conversation is not null)
                || target.Conversation is { } conversation
                    && (expectedKind != ServerChatTargetKind.Direct
                        || conversation.ConversationId != target.TargetId)
                || target.Room is { } room
                    && (expectedKind != ServerChatTargetKind.Room
                        || room.RoomId != target.TargetId)
                || target.Messages.Any(message =>
                    message.TargetId != target.TargetId || message.TargetKind != expectedKind))
            {
                throw new ArgumentException("A chat snapshot may contain only its requested chat target.");
            }
        }
        else if (snapshot.Scope.Kind == StateStreamScopeKind.UserBrowse)
        {
            if (snapshot.Daemon != null
                || snapshot.Workflows.Count != 0
                || snapshot.Jobs.Count != 0
                || snapshot.Searches.Count != 0
                || snapshot.Transfers.Count != 0
                || snapshot.ChatTarget != null
                || snapshot.UserBrowse is not { } browse
                || browse.BrowseId != snapshot.Scope.UserBrowseId)
            {
                throw new ArgumentException("A user-browse snapshot may contain only its requested resource.");
            }
        }
        else if (snapshot.ChatTarget != null || snapshot.UserBrowse != null)
        {
            throw new ArgumentException("A daemon snapshot cannot contain a scoped resource.");
        }
    }

    private static void ValidateBatch(StateUpdateBatchDto batch)
    {
        if (batch.Scope.Kind != StateStreamScopeKind.Daemon && batch.State.TransferSummary != null)
            throw new ArgumentException("Transfer totals belong to the daemon scope.");
        if (batch.PreviousSequence < 0 || batch.Sequence <= batch.PreviousSequence)
            throw new ArgumentException("A stream batch must advance beyond a non-negative previous sequence.");
        if (batch.Activity.Any(item => item.Sequence <= batch.PreviousSequence || item.Sequence > batch.Sequence))
            throw new ArgumentException("Activity item sequences must fall within the batch position range.");
        bool chatScope = batch.Scope.Kind is
            StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom;
        if (chatScope)
        {
            var expectedKind = batch.Scope.Kind == StateStreamScopeKind.ChatConversation
                ? ServerChatTargetKind.Direct
                : ServerChatTargetKind.Room;
            if ((batch.State.ChatTargets ?? []).Any(target =>
                    target.TargetId != batch.Scope.ChatTargetId
                    || target.Kind != expectedKind
                    || target.Conversation is { } conversation
                        && (expectedKind != ServerChatTargetKind.Direct
                            || conversation.ConversationId != target.TargetId)
                    || target.Room is { } room
                        && (expectedKind != ServerChatTargetKind.Room
                            || room.RoomId != target.TargetId)
                    || (target.Messages ?? []).Any(message =>
                        message.TargetId != target.TargetId || message.TargetKind != expectedKind)))
                throw new ArgumentException("A chat batch may contain only its requested target.");
            if (batch.State.Daemon != null
                || batch.State.Workflows.Count != 0
                || batch.State.Jobs.Count != 0
                || batch.State.Searches.Count != 0
                || batch.State.Transfers.Count != 0
                || batch.State.RemovedWorkflowIds.Count != 0
                || batch.State.RemovedJobIds.Count != 0
                || batch.State.RemovedSearchJobIds.Count != 0
                || batch.State.RemovedTransferIds.Count != 0
                || batch.State.Notifications is { Count: > 0 })
            {
                throw new ArgumentException("A chat batch cannot contain daemon or workflow state.");
            }
        }
        else if (batch.State.ChatTargets is { Count: > 0 })
        {
            throw new ArgumentException("A daemon or workflow batch cannot contain chat-target state.");
        }
        bool userBrowseScope = batch.Scope.Kind == StateStreamScopeKind.UserBrowse;
        if (userBrowseScope)
        {
            if (batch.State.UserBrowse is not { } browse
                || browse.BrowseId != batch.Scope.UserBrowseId
                || batch.State.Daemon != null
                || batch.State.Workflows.Count != 0
                || batch.State.Jobs.Count != 0
                || batch.State.Searches.Count != 0
                || batch.State.Transfers.Count != 0
                || batch.State.RemovedWorkflowIds.Count != 0
                || batch.State.RemovedJobIds.Count != 0
                || batch.State.RemovedSearchJobIds.Count != 0
                || batch.State.RemovedTransferIds.Count != 0
                || batch.State.Notifications is { Count: > 0 }
                || batch.State.ChatTargets is { Count: > 0 })
            {
                throw new ArgumentException("A user-browse batch may contain only its requested resource.");
            }
        }
        else if (batch.State.UserBrowse != null)
        {
            throw new ArgumentException("A non-user-browse batch cannot contain user-browse state.");
        }
        if (batch.Scope.Kind != StateStreamScopeKind.Daemon
            && batch.State.Notifications is { Count: > 0 })
        {
            throw new ArgumentException("Notifications may be delivered only on the daemon stream.");
        }
    }

    private sealed record AppliedState(
        IReadOnlyList<WorkflowSummaryDto> Workflows,
        IReadOnlyList<JobSummaryDto> Jobs,
        IReadOnlyList<TransferStateDto> Transfers,
        UserBrowseDto? UserBrowse);
}
