using Sockseek.Api;

namespace Sockseek.Server;

/// <summary>
/// Accumulates changed source facts under the state owner's gate. Only a flush
/// builds immutable network batches; wire conversion happens after publication.
/// </summary>
internal sealed class StateUpdateCoalescer
{
    private readonly Action<IReadOnlyList<StateUpdate>> publish;
    private readonly Dictionary<StateStreamScopeDto, PendingBatch> pending = [];
    // Mutations/flushes remain under the state owner's gate. Publication may
    // cheaply skip that gate when this mutation has no prompt work; a writer
    // setting this flag is also responsible for requesting its publication.
    private volatile bool hasPromptChanges;
    public bool HasPromptChanges => hasPromptChanges;
    // A watermark in the existing daemon stream, not a second stream clock.
    // Snapshot/history reconciliation must ignore unrelated transfer/chat state.
    public long LastJobSequence { get; private set; }
    public bool HasPending(StateStreamScopeDto scope) => pending.ContainsKey(scope);

    // The EngineStateStore gate owns capture, cursors and this buffer together.
    // Its shared publisher supplies the clock; the buffer owns no timer.
    public StateUpdateCoalescer(Action<IReadOnlyList<StateUpdate>> publish)
        => this.publish = publish;

    internal PendingBatch Begin(StateStreamScopeDto scope, Guid epoch,
        long previousSequence, long sequence, DateTimeOffset occurredAtUtc)
    {
        if (!pending.TryGetValue(scope, out var current))
            pending[scope] = current = new PendingBatch(this, scope, epoch, previousSequence);
        current.Advance(epoch, previousSequence, sequence, occurredAtUtc);
        return current;
    }

    public bool FlushIfRequired()
    {
        if (!HasPromptChanges) return false;
        Flush();
        return true;
    }

    public void Flush() => FlushCore();

    private void FlushCore()
    {
        if (pending.Count == 0)
            return;

        var batches = pending.Values
            .OrderBy(batch => batch.Scope.Kind)
            .ThenBy(batch => batch.Scope.WorkflowId)
            .ThenBy(batch => batch.Scope.ChatTargetId)
            .ThenBy(batch => batch.Scope.UserBrowseId)
            .Select(batch => batch.Build())
            .ToList();
        pending.Clear();
        hasPromptChanges = false;
        publish(batches);
    }

    private static bool RequiresPromptFlush(StateDelta state)
        => state.Notifications is { Count: > 0 }
            || state.UserBrowse?.State is UserBrowseState.Complete
                or UserBrowseState.Failed
                or UserBrowseState.Cancelled
            || state.ChatTargets?.Any(target => target.Messages is { Count: > 0 }) == true
            || state.RemovedWorkflowIds.Count > 0
            || state.RemovedJobIds.Count > 0
            // Individual transfer retirement, like a terminal child, is an
            // ordinary state update. Flushing each file defeats coalescing and
            // overwhelms slow clients. The existing cadence bounds its delay;
            // whole-workflow completion/removal still flushes promptly.
            || state.Workflows.Any(workflow => workflow.Summary.State != ServerWorkflowState.Active);

    // Keep each unflushed scope incremental. Rebuilding all accumulated maps/lists
    // on every file change makes a burst quadratic and allocates gigabytes.
    // Only Build materializes immutable collections for the transport handoff.
    internal sealed class PendingBatch
    {
        public StateStreamScopeDto Scope { get; }
        private readonly Guid epoch;
        private readonly long previousSequence;
        private long sequence;
        private DateTimeOffset occurredAtUtc;
        private DaemonStateDto? daemon;
        private UserBrowseDto? userBrowse;
        private LiveTransferSummaryDto? transferSummary;
        private readonly Dictionary<Guid, WorkflowStateDto> workflows = [];
        private readonly Dictionary<Guid, JobChange> jobs = [];
        private readonly Dictionary<Guid, SearchStateDto> searches = [];
        private readonly Dictionary<Guid, TransferChange> transfers = [];
        private readonly HashSet<Guid> removedWorkflows = [];
        private readonly HashSet<Guid> removedJobs = [];
        private readonly HashSet<Guid> removedSearches = [];
        private readonly HashSet<Guid> removedTransfers = [];
        private readonly Dictionary<Guid, UserNotificationDto> notifications = [];
        private readonly Dictionary<(ServerChatTargetKind, Guid), ChatTargetDeltaDto> chatTargets = [];
        private readonly List<ActivityEventDto> activity = [];

        private readonly StateUpdateCoalescer owner;
        public PendingBatch(StateUpdateCoalescer owner, StateStreamScopeDto scope, Guid epoch, long previousSequence)
        {
            this.owner = owner;
            Scope = scope;
            this.epoch = epoch;
            this.previousSequence = previousSequence;
            sequence = previousSequence;
        }

        public void Advance(Guid epoch, long previous, long next, DateTimeOffset occurredAt)
        {
            if (epoch != this.epoch || previous != sequence)
                throw new InvalidOperationException($"Cannot coalesce discontinuous {Scope.Kind} stream batches {previousSequence}->{sequence} and {previous}->{next}.");
            sequence = next;
            occurredAtUtc = occurredAt;
        }

        public void Add(WorkflowStateDto value)
        {
            MarkJobChange();
            if (!workflows.TryGetValue(value.Summary.WorkflowId, out var current) || value.Revision > current.Revision)
                workflows[value.Summary.WorkflowId] = value;
            if (value.Summary.State != ServerWorkflowState.Active) owner.hasPromptChanges = true;
        }
        public void Add(JobChange value)
        {
            MarkJobChange();
            if (!jobs.TryGetValue(value.JobId, out var current)) jobs[value.JobId] = value;
            else if (value.Revision > current.Revision) jobs[value.JobId] = current.Merge(value);
        }
        public void Add(SearchStateDto value)
        {
            if (!searches.TryGetValue(value.JobId, out var current) || value.Revision > current.Revision)
                searches[value.JobId] = value;
        }
        public void Add(TransferChange value)
        {
            if (!transfers.TryGetValue(value.TransferId, out var current)) transfers[value.TransferId] = value;
            else if (value.Revision > current.Revision) transfers[value.TransferId] = current.Merge(value);
        }
        public void RemoveWorkflow(Guid id) { MarkJobChange(); removedWorkflows.Add(id); owner.hasPromptChanges = true; }
        public void RemoveJob(Guid id) { MarkJobChange(); removedJobs.Add(id); owner.hasPromptChanges = true; }
        private void MarkJobChange()
        {
            if (Scope.Kind == StateStreamScopeKind.Daemon) owner.LastJobSequence = sequence;
        }
        public void RemoveSearch(Guid id) => removedSearches.Add(id);
        public void RemoveTransfer(Guid id) => removedTransfers.Add(id);
        public void SetSummary(LiveTransferSummaryDto value) => transferSummary = value;
        public void AddActivity(ActivityEventDto value) => activity.Add(value);
        public void SetUserBrowse(UserBrowseDto value)
        {
            userBrowse = Latest(userBrowse, value);
            if (value.State is UserBrowseState.Complete or UserBrowseState.Failed or UserBrowseState.Cancelled)
                owner.hasPromptChanges = true;
        }

        public void Merge(StateDelta state, IReadOnlyList<ActivityEventDto> events)
        {
            if (RequiresPromptFlush(state)) owner.hasPromptChanges = true;
            daemon = Latest(daemon, state.Daemon);
            userBrowse = Latest(userBrowse, state.UserBrowse);
            transferSummary = state.TransferSummary ?? transferSummary;
            foreach (var workflow in state.Workflows) Add(workflow);
            foreach (var job in state.Jobs) Add(job);
            foreach (var search in state.Searches) Add(search);
            foreach (var transfer in state.Transfers) Add(transfer);
            foreach (Guid id in state.RemovedWorkflowIds) RemoveWorkflow(id);
            foreach (Guid id in state.RemovedJobIds) RemoveJob(id);
            removedSearches.UnionWith(state.RemovedSearchJobIds);
            removedTransfers.UnionWith(state.RemovedTransferIds);
            foreach (var notification in state.Notifications ?? [])
            {
                if (!notifications.TryGetValue(notification.NotificationId, out var current)
                    || notification.Sequence > current.Sequence)
                    notifications[notification.NotificationId] = notification;
            }
            foreach (var target in state.ChatTargets ?? [])
            {
                var key = (target.Kind, target.TargetId);
                chatTargets[key] = chatTargets.TryGetValue(key, out var current)
                    ? MergeChatTarget(current, target) : target;
            }
            activity.AddRange(events);
        }

        public StateUpdate Build()
            => new(Scope, epoch, previousSequence, sequence, occurredAtUtc,
                new StateDelta(
                    daemon,
                    workflows.Values.ToArray(),
                    jobs.Values.ToArray(),
                    searches.Values.ToArray(),
                    transfers.Values.ToArray(),
                    removedWorkflows.ToArray(),
                    removedJobs.ToArray(),
                    removedSearches.ToArray(),
                    removedTransfers.ToArray(),
                    notifications.Values.OrderBy(item => item.Sequence).ToArray(),
                    chatTargets.Values.OrderBy(item => item.Kind).ThenBy(item => item.TargetId).ToArray(),
                    userBrowse, transferSummary),
                activity.OrderBy(item => item.Sequence).ToArray());
    }

    private static ChatTargetDeltaDto MergeChatTarget(ChatTargetDeltaDto current, ChatTargetDeltaDto next)
    {
        IEnumerable<ChatMessageDto> messageSource = next.ReplaceMessages
            ? next.Messages ?? []
            : (current.Messages ?? []).Concat(next.Messages ?? []);
        var messages = messageSource
            .GroupBy(message => message.MessageId)
            .Select(group => group.Last())
            .OrderBy(message => message.Sequence)
            .ToArray();
        return new ChatTargetDeltaDto(
            next.Kind,
            next.TargetId,
            next.Conversation ?? current.Conversation,
            next.Room ?? current.Room,
            messages,
            current.ReplaceMessages || next.ReplaceMessages,
            next.HasEarlierMessages ?? current.HasEarlierMessages);
    }

    private static DaemonStateDto? Latest(DaemonStateDto? first, DaemonStateDto? second)
        => second == null || first?.Revision > second.Revision ? first : second;

    private static UserBrowseDto? Latest(UserBrowseDto? first, UserBrowseDto? second)
        => second == null || first?.Revision > second.Revision ? first : second;

}

/// <summary>
/// One coalescing policy for local CLI and SignalR delivery. A pending change
/// arms a single tick; idle stores need no repeating timer or network host.
/// </summary>
internal sealed class StateUpdatePublication : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private readonly EngineStateStore store;
    private readonly ITimer timer;
    private readonly Lock gate = new();
    private volatile bool scheduled;
    private bool disposed;

    public StateUpdatePublication(EngineStateStore store, TimeProvider clock)
    {
        this.store = store;
        timer = clock.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        store.StatePublicationRequested += Request;
        store.JobReadChanged += JobChanged;
        store.TransferReadChanged += TransferChanged;
        Request(); // Include facts captured before this publisher attached.
    }

    private void JobChanged(JobReadChange _) => Request();
    private void TransferChanged(TransferReadChange _) => Request();

    private void Request()
    {
        // A burst arms one tick. Most source callbacks arrive while that tick
        // is already scheduled and need no second lock inside the state gate.
        if (scheduled) return;
        lock (gate)
        {
            if (disposed || scheduled) return;
            scheduled = true;
            timer.Change(Interval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        lock (gate)
        {
            if (disposed) return;
            scheduled = false;
        }
        store.FlushStateUpdates();
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        store.StatePublicationRequested -= Request;
        store.JobReadChanged -= JobChanged;
        store.TransferReadChanged -= TransferChanged;
        await timer.DisposeAsync().ConfigureAwait(false);
        store.FlushStateUpdates();
    }
}
