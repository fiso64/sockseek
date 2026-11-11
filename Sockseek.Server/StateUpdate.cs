using Sockseek.Api;

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
    LiveTransferSummaryDto? TransferSummary = null)
{
    public static StateDelta Empty { get; } = new(null, [], [], [], [], [], [], [], []);

    public bool IsEmpty => Daemon is null && Workflows.Count == 0 && Jobs.Count == 0
        && Searches.Count == 0 && Transfers.Count == 0 && RemovedWorkflowIds.Count == 0
        && RemovedJobIds.Count == 0 && RemovedSearchJobIds.Count == 0 && RemovedTransferIds.Count == 0
        && Notifications is null or { Count: 0 } && ChatTargets is null or { Count: 0 }
        && UserBrowse is null && TransferSummary is null;

    public StateDeltaDto ToDto()
    {
        JobDeltaDto[] jobs = Jobs.Count == 0 ? [] : new JobDeltaDto[Jobs.Count];
        for (int index = 0; index < jobs.Length; index++) jobs[index] = Jobs[index].ToDto();
        return new(Daemon, Workflows, jobs, Searches, Transfers.Select(transfer => transfer.ToDto()).ToArray(),
            RemovedWorkflowIds, RemovedJobIds, RemovedSearchJobIds, RemovedTransferIds,
            Notifications, ChatTargets, UserBrowse, TransferSummary);
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
    // Flat scalar presentation facts cannot retain a plan, manifest or job graph.
    // Actions and cohesive wire components belong to actual readers/publication.
    private readonly JobSummaryDto facts;
    private readonly JobSummaryStatsDto? stats;
    private readonly bool canCancel;
    private JobSummaryDto? summary;
    private JobStateDto? wire;
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
    public Guid JobId => facts.JobId;
    public Guid Id => JobId;
    public Guid WorkflowId => facts.WorkflowId;
    public int DisplayId => facts.DisplayId;
    public Guid? ParentJobId => facts.ParentJobId;
    public Guid? ResultJobId => facts.ResultJobId;
    public Guid? SourceJobId => facts.SourceJobId;
    public Guid? SubmissionId => facts.SubmissionId;
    public string? ItemName => facts.ItemName;
    public string? QueryText => facts.QueryText;
    public ServerJobKind Kind => facts.Kind;
    public ServerJobLifecycleState LifecycleState => facts.LifecycleState;
    public ServerJobTerminalOutcome TerminalOutcome => facts.TerminalOutcome;
    public ServerJobSkipReason SkipReason => facts.SkipReason;
    public ServerJobRole Role => facts.Role;

    public JobSummaryDto Summary
    {
        get
        {
            if (Volatile.Read(ref summary) is { } current) return current;
            var projected = canCancel || stats != facts.Stats ? facts with
            {
                Stats = stats,
                AvailableActions = canCancel ? ServerSnapshotMapper.BuildJobActions(JobId, true) : facts.AvailableActions,
            } : facts;
            return Interlocked.CompareExchange(ref summary, projected, null) ?? projected;
        }
    }

    // Directory counters can change for every child while display/lifecycle and
    // relationships remain unchanged. Reuse those scalar facts; render a summary
    // only for a reader or the final coalesced transport value. Neither revision
    // changes nor stats updates can retain a mutable directory or job graph.
    internal JobReadState WithStats(JobSummaryStatsDto next)
        => stats == next ? this : new(facts, next, Revision, canCancel);

    internal JobReadState WithRevision(long nextRevision) => new(facts, stats, nextRevision, canCancel)
    {
        summary = Volatile.Read(ref summary),
    };

    public JobStateDto ToDto()
    {
        if (Volatile.Read(ref wire) is { } current) return current;
        var projected = JobStateDto.FromSummary(Summary, Revision);
        return Interlocked.CompareExchange(ref wire, projected, null) ?? projected;
    }

    internal JobFields Difference(JobReadState next)
    {
        JobSummaryDto previous = facts;
        JobSummaryDto current = next.facts;
        var fields = JobFields.None;
        if (previous.DisplayId != current.DisplayId || previous.WorkflowId != current.WorkflowId
            || previous.Kind != current.Kind || previous.ItemName != current.ItemName || previous.QueryText != current.QueryText
            || previous.SourceDetail != current.SourceDetail
            || previous.PrintOption != current.PrintOption || previous.SubmissionId != current.SubmissionId
            || previous.Role != current.Role || previous.CreatedAtUtc != current.CreatedAtUtc
            || previous.SearchDefaultProjection != current.SearchDefaultProjection
            || previous.SearchGroupResults != current.SearchGroupResults
            || !previous.AppliedAutoProfiles.SequenceEqual(current.AppliedAutoProfiles)) fields |= JobFields.Display;
        if (previous.LifecycleState != current.LifecycleState || previous.ActivityPhase != current.ActivityPhase
            || previous.ActivityUntilUtc != current.ActivityUntilUtc || previous.TerminalOutcome != current.TerminalOutcome
            || previous.SkipReason != current.SkipReason || previous.FailureReason != current.FailureReason
            || previous.FailureMessage != current.FailureMessage || previous.FailureDetail != current.FailureDetail
            || previous.CancellationSource != current.CancellationSource || canCancel != next.canCancel)
            fields |= JobFields.Lifecycle;
        if (previous.DiscoveryRawResultCount != current.DiscoveryRawResultCount
            || previous.DiscoveryLockedFileCount != current.DiscoveryLockedFileCount
            || previous.DiscoveryPublicFileCount != current.DiscoveryPublicFileCount
            || previous.DiscoveryObservedPeerCount != current.DiscoveryObservedPeerCount) fields |= JobFields.Discovery;
        if (previous.ParentJobId != current.ParentJobId || previous.ResultJobId != current.ResultJobId
            || previous.SourceJobId != current.SourceJobId) fields |= JobFields.Relationships;
        if (stats != next.stats) fields |= JobFields.Stats;
        return fields;
    }
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
        JobStateDto current = Current.ToDto();
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
