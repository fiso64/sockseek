using Sockseek.Api;
using Sockseek.Core.Planning;

namespace Sockseek.Server;

/// <summary>
/// An ordered projection change. Keep job values here until a reader or the
/// transport flush needs wire components; progress can replace unflushed values.
/// </summary>
public sealed record StateUpdate(
    StateStreamScopeDto Scope,
    Guid Epoch,
    long PreviousSequence,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    StateDelta State,
    IReadOnlyList<ActivityEventDto> Activity)
{
    public StateUpdateBatchDto ToDto()
        => new(Scope, Epoch, PreviousSequence, Sequence, OccurredAtUtc, State.ToDto(), Activity);
}

public sealed record StateDelta(
    DaemonStateDto? Daemon,
    IReadOnlyList<WorkflowStateDto> Workflows,
    IReadOnlyList<JobChange> Jobs,
    IReadOnlyList<SearchStateDto> Searches,
    IReadOnlyList<TransferChange> Transfers,
    IReadOnlyList<Guid> RemovedWorkflowIds,
    IReadOnlyList<Guid> RemovedJobIds,
    IReadOnlyList<Guid> RemovedSearchJobIds,
    IReadOnlyList<Guid> RemovedTransferIds,
    IReadOnlyList<UserNotificationDto>? Notifications = null,
    IReadOnlyList<ChatTargetDeltaDto>? ChatTargets = null,
    UserBrowseDto? UserBrowse = null,
    LiveActivitySummaryDto? ActivitySummary = null)
{
    public static StateDelta Empty { get; } = new(null, [], [], [], [], [], [], [], []);

    public bool IsEmpty => Daemon is null && Workflows.Count == 0 && Jobs.Count == 0
        && Searches.Count == 0 && Transfers.Count == 0 && RemovedWorkflowIds.Count == 0
        && RemovedJobIds.Count == 0 && RemovedSearchJobIds.Count == 0 && RemovedTransferIds.Count == 0
        && Notifications is null or { Count: 0 } && ChatTargets is null or { Count: 0 }
        && UserBrowse is null && ActivitySummary is null;

    public StateDeltaDto ToDto()
    {
        JobDeltaDto[] jobs = Jobs.Count == 0 ? [] : new JobDeltaDto[Jobs.Count];
        for (int index = 0; index < jobs.Length; index++) jobs[index] = Jobs[index].ToDto();
        return new(Daemon, Workflows, jobs, Searches, Transfers.Select(transfer => transfer.ToDto()).ToArray(),
            RemovedWorkflowIds, RemovedJobIds, RemovedSearchJobIds, RemovedTransferIds,
            Notifications, ChatTargets, UserBrowse, ActivitySummary);
    }
}

[Flags]
public enum JobFields
{
    None = 0, Display = 1, Lifecycle = 2, Discovery = 4, Relationships = 8, Stats = 16,
    All = Display | Lifecycle | Discovery | Relationships | Stats,
}

// Local observation facts, independent of network batches. A membership value
// is captured before runtime eviction; null means this change carries no count.
internal readonly record struct JobReadChange(
    long Sequence, Guid JobId, JobReadState? Current, int? ChildCount = null, int? RetrievalChildCount = null);

/// <summary>One immutable job revision, shared by snapshots and pending updates.</summary>
public sealed class JobReadState
{
    // Project actions and updated counters only when a reader needs the whole row.
    private readonly JobSummaryDto facts;
    private readonly JobSummaryStatsDto? stats;
    private readonly bool canCancel;
    private JobSummaryDto? summary;
    public long Revision { get; }

    public JobReadState(JobSummaryDto facts, long revision, bool canCancel)
        : this(facts, facts.Stats, revision, canCancel) { }

    private JobReadState(JobSummaryDto facts, JobSummaryStatsDto? stats, long revision, bool canCancel)
    {
        this.facts = facts;
        this.stats = stats;
        this.canCancel = canCancel;
        Revision = revision;
    }
    internal JobRetention Retention { get; init; } = JobRetention.History;
    public Guid JobId => facts.JobId;
    public Guid Id => JobId;
    public Guid WorkflowId => facts.Display.WorkflowId;
    public int DisplayId => facts.Display.DisplayId;
    public Guid? ParentJobId => facts.Relationships.ParentJobId;
    public Guid? ResultJobId => facts.Relationships.ResultJobId;
    public Guid? SourceJobId => facts.Relationships.SourceJobId;
    public Guid? SubmissionId => facts.Display.SubmissionId;
    public string? ItemName => facts.Display.ItemName;
    public string? QueryText => facts.Display.QueryText;
    public ServerJobKind Kind => facts.Display.Kind;
    public JobLifecycleState LifecycleState => facts.Lifecycle.LifecycleState;
    public JobTerminalOutcome TerminalOutcome => facts.Lifecycle.TerminalOutcome;
    public JobSkipReason SkipReason => facts.Lifecycle.SkipReason;
    public ServerJobRole Role => facts.Display.Role;

    public JobSummaryDto Summary
    {
        get
        {
            if (Volatile.Read(ref summary) is { } current) return current;
            var projected = canCancel || stats != facts.Stats || facts.Revision != Revision ? facts with
            {
                Stats = stats,
                Revision = Revision,
                Lifecycle = canCancel ? facts.Lifecycle with { AvailableActions = ServerSnapshotMapper.BuildJobActions(JobId, true) } : facts.Lifecycle,
            } : facts;
            return Interlocked.CompareExchange(ref summary, projected, null) ?? projected;
        }
    }

    // Directory counters can change for every child while display/lifecycle and
    // relationships remain unchanged. Reuse those scalar facts; render a summary
    // only for a reader or the final coalesced transport value. Neither revision
    // changes nor stats updates can retain a mutable directory or job graph.
    internal JobReadState WithStats(JobSummaryStatsDto next)
        => stats == next ? this : new(facts, next, Revision, canCancel) { Retention = Retention };

    internal JobReadState WithRevision(long nextRevision)
        => nextRevision == Revision ? this : new(facts, stats, nextRevision, canCancel) { Retention = Retention };

    internal JobFields Difference(JobReadState next)
    {
        JobSummaryDto previous = facts;
        JobSummaryDto current = next.facts;
        var fields = JobFields.None;
        if (!SameDisplay(previous.Display, current.Display)) fields |= JobFields.Display;
        if (canCancel != next.canCancel || !SameLifecycle(previous.Lifecycle, current.Lifecycle))
            fields |= JobFields.Lifecycle;
        if (previous.Discovery != current.Discovery) fields |= JobFields.Discovery;
        if (previous.Relationships != current.Relationships) fields |= JobFields.Relationships;
        if (stats != next.stats) fields |= JobFields.Stats;
        return fields;
    }

    // Records compare list references. A fresh history read can return the same
    // profiles or actions in a different list. Compare those by value as well.
    private static bool SameDisplay(JobDisplayFieldsDto left, JobDisplayFieldsDto right)
        => ReferenceEquals(left.AppliedProfiles, right.AppliedProfiles) ? left == right
            : left.AppliedProfiles.SequenceEqual(right.AppliedProfiles) && left with { AppliedProfiles = right.AppliedProfiles } == right;

    private static bool SameLifecycle(JobLifecycleFieldsDto left, JobLifecycleFieldsDto right)
        => ReferenceEquals(left.AvailableActions, right.AvailableActions) ? left == right
            : left.AvailableActions.SequenceEqual(right.AvailableActions) && left with { AvailableActions = right.AvailableActions } == right;
}

/// <summary>
/// Accumulate which cohesive fields changed as well as the latest value. A field
/// that changes back before flushing still replaces its previous wire component.
/// </summary>
public sealed class JobChange(JobReadState Current, bool Added = true, JobFields Fields = JobFields.All)
{
    public JobReadState Current { get; } = Current;
    public bool Added { get; } = Added;
    public JobFields Fields { get; } = Fields;
    public Guid JobId => Current.JobId;
    public long Revision => Current.Revision;
    private JobDeltaDto? wire;

    internal JobChange Merge(JobChange next)
        => next.Added ? next : new(next.Current, Added, Fields | next.Fields);

    public JobDeltaDto ToDto()
    {
        // Daemon and workflow scopes can share the same immutable change.
        if (Volatile.Read(ref wire) is { } previous) return previous;
        JobSummaryDto current = Current.Summary;
        var projected = Added ? new JobDeltaDto(JobId, Revision, Added: current) : new(JobId, Revision,
            Display: (Fields & JobFields.Display) != 0 ? current.Display : null,
            Lifecycle: (Fields & JobFields.Lifecycle) != 0 ? current.Lifecycle : null,
            Discovery: (Fields & JobFields.Discovery) != 0 ? current.Discovery : null,
            Relationships: (Fields & JobFields.Relationships) != 0 ? current.Relationships : null,
            Stats: (Fields & JobFields.Stats) != 0 ? current.Stats : null);
        return Interlocked.CompareExchange(ref wire, projected, null) ?? projected;
    }
}

[Flags]
public enum TransferFields
{
    None = 0, Status = 1, Progress = 2, Scheduling = 4,
    All = Status | Progress | Scheduling,
}

/// <summary>Pending source facts and changed components, projected after coalescing.</summary>
public sealed class TransferChange(TransferReadSnapshot current, bool added, TransferFields fields)
{
    public TransferReadSnapshot Current { get; } = current;
    public bool Added { get; } = added;
    public TransferFields Fields { get; } = fields;
    public Guid TransferId => Current.TransferId;
    public long Revision => Current.Revision;
    private TransferDeltaDto? wire;

    internal TransferChange(TransferReadSnapshot current)
        : this(current, current.IsReplacement, current.ChangedFields) { }

    internal TransferChange Merge(TransferChange next)
        => next.Added ? next : new(next.Current, Added, Fields | next.Fields);

    public TransferDeltaDto ToDto()
    {
        if (Volatile.Read(ref wire) is { } previous) return previous;
        var current = Current.ToDto();
        var projected = Added ? new TransferDeltaDto(TransferId, Revision, Added: current) : new(TransferId, Revision,
            Status: (Fields & TransferFields.Status) != 0 ? current.Status : null,
            Progress: (Fields & TransferFields.Progress) != 0 ? current.Progress : null,
            Scheduling: (Fields & TransferFields.Scheduling) != 0 ? current.Scheduling : null);
        return Interlocked.CompareExchange(ref wire, projected, null) ?? projected;
    }
}
