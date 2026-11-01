using Sockseek.Core.Models;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Snapshots;
using Soulseek;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Core.Chat;

namespace Sockseek.Server;

public sealed record LiveWorkflowPageItem(long FirstDisplayId, WorkflowSummaryDto Summary);
public sealed record LiveTransferDetail(TransferStateDto Transfer, TransferAttemptHistoryDto? LatestAttempt);

public sealed class EngineStateStore
{
    private readonly bool persistenceOwnsJobRetirement;

    public EngineStateStore() => stateUpdates = new(QueueStateBatches);

    internal EngineStateStore(bool persistenceOwnsJobRetirement) : this()
        => this.persistenceOwnsJobRetirement = persistenceOwnsJobRetirement;

    private readonly Lock gate = new();
    // Capture and cursor allocation share gate. Only flushed immutable batches
    // enter the publication queue; callbacks run outside both locks.
    private readonly Lock stateBatchPublicationGate = new();
    private readonly StateUpdateCoalescer stateUpdates;
    private readonly Queue<StateUpdate> readyStateBatches = [];
    private bool publishingStateBatches;
    // Keep records and workflow aggregate indexes in sync only through UpdateJobRecord.
    private readonly Dictionary<Guid, JobSnapshot> jobs = [];
    private readonly Dictionary<Guid, DirectoryProjection> directoryProjections = [];
    private readonly HashSet<Guid> changedContainerSummaries = [];
    private DateTimeOffset containerSummaryChangedAt;
    private readonly Dictionary<Guid, MemberOutcomeProjection> listProjections = [];
    private readonly Dictionary<Guid, MemberOutcomeProjection> aggregateProjections = [];
    private readonly Dictionary<Guid, AlbumAggregateProjection> albumAggregateProjections = [];
    private readonly Dictionary<Guid, HashSet<Guid>> nestedJobIdsByContainer = [];
    private readonly Dictionary<Guid, HashSet<Guid>> containerIdsByNestedJob = [];
    // One entry owns current read facts and the last published revision used for
    // stream deltas. Both use the same immutable, payload-free model.
    private readonly Dictionary<Guid, JobReadEntry> records = [];
    private IEnumerable<JobReadState> CurrentJobs => records.Values.Select(entry => entry.Current);
    private IEnumerable<JobReadState> PublishedJobs => records.Values.Select(entry => entry.Published).OfType<JobReadState>();
    private readonly Dictionary<Guid, WorkflowStateRecord> workflows = [];
    private readonly Dictionary<Guid, SubmissionMembership> submissions = [];
    private readonly Dictionary<Guid, Guid?> parentJobIds = [];
    private readonly Dictionary<Guid, HashSet<Guid>> childJobIdsByParent = [];
    private readonly Dictionary<Guid, int> retrievalChildCountsByParent = [];
    private readonly Dictionary<Guid, Guid> resultJobIds = [];
    private readonly Dictionary<Guid, Guid> extractJobIdsByResult = [];
    private readonly Dictionary<Guid, Guid> sourceJobIds = [];
    private readonly HashSet<Guid> archivedSubmissionIds = [];
    private readonly HashSet<Guid> executionCompletedJobs = [];
    private readonly Dictionary<Guid, string> songTransferStates = [];
    private readonly TransferReadState transferReadState = new();
    private LiveTransferSummaryDto publishedTransferSummary = LiveTransferSummaryDto.Empty;
    private IReadOnlyDictionary<Guid, TransferReadSnapshot> activeTransfers => transferReadState.Active;
    private IReadOnlyDictionary<Guid, TransferReadSnapshot> liveUploadTransfers => transferReadState.Uploads;
    // Attempts stay readable with their live transfer until the existing durable
    // handoff releases it, including retries that have not reached SQLite yet.
    private readonly Dictionary<Guid, SortedList<int, TransferAttemptHistoryDto>> transferAttempts = [];
    private readonly Dictionary<Guid, Guid> transferWorkflowIds = [];
    private readonly HashSet<Guid> terminalTransfersAwaitingRemoval = [];
    private readonly Dictionary<Guid, SearchStateDto> searchStates = [];
    private readonly Dictionary<Guid, WorkflowStateDto> projectedWorkflows = [];
    private readonly Dictionary<Guid, long> workflowStreamSequences = [];
    private readonly Dictionary<Guid, Guid> workflowStreamEpochs = [];
    private readonly Dictionary<Guid, int> workflowStreamReservations = [];
    private readonly Dictionary<StateStreamScopeDto, long> chatStreamSequences = [];
    private readonly Dictionary<Guid, long> userBrowseStreamSequences = [];
    private readonly Dictionary<Guid, UserBrowseDto> userBrowses = [];
    private readonly HashSet<Guid> daemonLiveWorkflowIds = [];
    private readonly Guid streamEpoch = Guid.NewGuid();
    private long daemonStreamSequence;
    private DaemonStateDto daemonState = new(
        0,
        new SoulseekClientStatusDto("None", [], false),
        0,
        null,
        new SharingStateDto(
            DaemonFeatureState.Disabled,
            "NotConfigured",
            [],
            0,
            0,
            new ShareCatalogStateDto(
                null,
                0,
                0,
                0,
                false,
                null,
                null),
            null,
            null),
        new UploadRuntimeStateDto(
            DaemonFeatureState.Disabled,
            "NotConfigured",
            false,
            0,
            0,
            0,
            0,
            0,
            null),
        new ChatRuntimeStateDto(
            DaemonFeatureState.Disabled,
            "PersistenceUnavailable",
            0,
            0,
            0,
            0,
            0),
        new NotificationSummaryDto(0, 0));

    public event Action<JobSummaryDto>? JobUpserted;
    public event Action<WorkflowSummaryDto>? WorkflowUpserted;
    public event Action<SearchStateDto>? SearchUpdated;
    public event Action<StateUpdate>? StateBatchPublished;
    internal event Action? StatePublicationRequested;
    internal event Action? StateUpdatesFlushed;
    internal event Action<JobReadChange>? JobReadChanged;
    private long jobReadSequence;
    // Complete terminal facts are offered before runtime eviction. Readers can
    // coalesce them without requiring a durable history store to exist.
    internal event Action<TransferReadChange> TransferReadChanged
    {
        add => transferReadState.Changed += value;
        remove => transferReadState.Changed -= value;
    }
    internal StateStreamPositionDto GetTransferReadPosition()
    {
        lock (gate) return transferReadState.Position;
    }
    internal void MarkTransferHistoryChanged()
    {
        lock (gate) transferReadState.MarkHistoryChanged();
    }

    public void SetSubmissionArchived(Guid submissionId, bool archived)
    {
        lock (gate)
        {
            if (archived)
                archivedSubmissionIds.Add(submissionId);
            else
                archivedSubmissionIds.Remove(submissionId);
        }
    }

    internal EngineStateStoreRetainedWorkflowCounts RetainedWorkflowStateCounts
    {
        get
        {
            lock (gate)
            {
                return new EngineStateStoreRetainedWorkflowCounts(
                    jobs.Count,
                    records.Count,
                    workflows.Count,
                    parentJobIds.Count,
                    childJobIdsByParent.Count,
                    nestedJobIdsByContainer.Count,
                    containerIdsByNestedJob.Count,
                    resultJobIds.Count,
                    sourceJobIds.Count,
                    executionCompletedJobs.Count,
                    songTransferStates.Count,
                    activeTransfers.Count,
                    transferAttempts.Count,
                    transferWorkflowIds.Count,
                    searchStates.Count,
                    records.Values.Count(entry => entry.Published is not null),
                    projectedWorkflows.Count,
                    workflowStreamSequences.Count,
                    workflowStreamEpochs.Count,
                    workflowStreamReservations.Count,
                    daemonLiveWorkflowIds.Count,
                    listProjections.Count,
                    extractJobIdsByResult.Count,
                    aggregateProjections.Count,
                    albumAggregateProjections.Count);
            }
        }
    }

    public TransferStateDto? GetLiveTransfer(Guid transferId)
    {
        lock (gate)
            return (activeTransfers.GetValueOrDefault(transferId)
                   ?? liveUploadTransfers.GetValueOrDefault(transferId))?.ToDto();
    }

    public IReadOnlyList<TransferStateDto> GetActiveTransferSnapshot()
    {
        lock (gate)
            return activeTransfers.Values.Select(transfer => transfer.ToDto()).ToArray();
    }

    public IReadOnlyList<TransferStateDto> GetTransferSnapshot(IReadOnlySet<Guid> transferIds)
        => GetTransferFactsSnapshot(transferIds).Select(transfer => transfer.ToDto()).ToArray();

    internal IReadOnlyList<TransferReadSnapshot> GetTransferFactsSnapshot(IReadOnlySet<Guid> transferIds)
    {
        lock (gate)
            return transferIds.Select(id => activeTransfers.GetValueOrDefault(id)
                ?? liveUploadTransfers.GetValueOrDefault(id)).OfType<TransferReadSnapshot>().ToArray();
    }

    internal IReadOnlyList<TransferReadSnapshot> GetJobTransferSnapshot(IReadOnlySet<Guid> jobIds)
    {
        lock (gate) return transferReadState.ForJobs(jobIds);
    }

    /// <summary>
    /// Captures one command-resolution population, including queued uploads
    /// which are intentionally absent from the compact active-state snapshot.
    /// </summary>
    public IReadOnlyList<TransferStateDto> GetCancellableTransferSnapshot()
    {
        lock (gate)
        {
            var snapshot = activeTransfers.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var pair in liveUploadTransfers)
                snapshot[pair.Key] = pair.Value;
            return snapshot.Values.Select(transfer => transfer.ToDto()).ToArray();
        }
    }

    /// <summary>
    /// Captures only the requested directory groups, including queued uploads,
    /// without allocating a copy of every cancellable transfer.
    /// </summary>
    internal IReadOnlyList<TransferReadSnapshot> GetTransferGroupSnapshot(
        IReadOnlySet<TransferGroupKeyDto> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0) return [];
        lock (gate)
        {
            var selected = new Dictionary<Guid, TransferReadSnapshot>();
            void Include(TransferReadSnapshot transfer)
            {
                if (transfer.GroupRef is not { } groupRef
                    || !Enum.TryParse<TransferCommandDirection>(
                        transfer.Direction, ignoreCase: true, out var direction)
                    || !groups.Contains(new TransferGroupKeyDto(
                        direction, transfer.Username, groupRef)))
                    return;
                selected[transfer.TransferId] = transfer;
            }
            foreach (TransferReadSnapshot transfer in activeTransfers.Values)
                Include(transfer);
            foreach (TransferReadSnapshot transfer in liveUploadTransfers.Values)
                Include(transfer);
            return selected.Values.ToArray();
        }
    }

    // Capture immutable source facts; the read owner reconciles retained keys
    // and chooses the page before any wire metadata is built.
    internal IReadOnlyList<TransferReadSnapshot> GetTransferPageCandidates(
        string? groupRef, string? direction, string? username)
    {
        lock (gate)
        {
            if (groupRef is null) return activeTransfers.Values.ToArray();
            var selected = new Dictionary<Guid, TransferReadSnapshot>();
            void Include(TransferReadSnapshot transfer)
            {
                if (transfer.GroupRef == groupRef
                    && (direction is null || string.Equals(transfer.Direction, direction, StringComparison.OrdinalIgnoreCase))
                    && (username is null || transfer.Username == username))
                    selected[transfer.TransferId] = transfer;
            }
            foreach (var transfer in activeTransfers.Values) Include(transfer);
            foreach (var transfer in liveUploadTransfers.Values) Include(transfer);
            return selected.Values.ToArray();
        }
    }

    public LiveTransferDetail? GetLiveTransferDetail(Guid transferId)
    {
        lock (gate)
        {
            var transfer = activeTransfers.GetValueOrDefault(transferId)
                ?? liveUploadTransfers.GetValueOrDefault(transferId);
            return transfer == null
                ? null
                : new LiveTransferDetail(
                    transfer.ToDto(),
                    LatestTransferAttempt(transferId));
        }
    }

    public IReadOnlyList<TransferAttemptHistoryDto>? GetLiveTransferAttempts(
        Guid transferId, int afterAttemptNumber, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterAttemptNumber);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (gate)
        {
            if (transferAttempts.TryGetValue(transferId, out var attempts))
                return attempts.Values.Where(attempt => attempt.AttemptNumber > afterAttemptNumber)
                    .Take(limit).ToArray();
            return activeTransfers.ContainsKey(transferId) || liveUploadTransfers.ContainsKey(transferId)
                ? [] : null;
        }
    }

    // Called only while gate is held. Attempt revisions are independent of the
    // transfer revision; a late older notification must not restore Started.
    private void RecordTransferAttempt(TransferAttemptHistoryDto attempt)
    {
        if (!transferAttempts.TryGetValue(attempt.TransferId, out var attempts))
            transferAttempts.Add(attempt.TransferId, attempts = new());
        if (!attempts.TryGetValue(attempt.AttemptNumber, out var previous)
            || previous.Revision <= attempt.Revision)
            attempts[attempt.AttemptNumber] = attempt;
    }

    private TransferAttemptHistoryDto? LatestTransferAttempt(Guid transferId)
        => transferAttempts.TryGetValue(transferId, out var attempts) && attempts.Count > 0
            ? attempts.Values[^1] : null;

    public void UpdateUploadTransfer(UploadTransferSnapshot upload)
    {
        lock (gate)
        {
            // Source revisions are shared with durable history. Replays must not
            // mint a newer live version that its committed successor cannot match.
            if (liveUploadTransfers.TryGetValue(upload.TransferId, out var observed)
                && observed.Revision >= upload.Revision)
                return;
            activeTransfers.TryGetValue(upload.TransferId, out var previous);
            var current = new TransferReadSnapshot(upload, previous);

            if (ToUploadAttempt(upload) is { } attempt)
                RecordTransferAttempt(attempt);

            bool replicated = upload.State is not UploadTransferState.Queued
                              && (current.IsTerminal
                                  || upload.Attempt is not null
                                  || activeTransfers.ContainsKey(upload.TransferId));
            var delta = new TransferChange(current);
            transferReadState.UpsertUpload(current, replicated, delta.Added);
            if (!replicated) return;

            var daemonDelta = StateDelta.Empty with
            {
                Transfers = [delta],
            };
            AppendDaemonChanges(
                daemonDelta,
                DateTimeOffset.UtcNow);
        }
        PublishStateChanges();
    }

    public void RemoveTerminalTransfer(Guid transferId)
    {
        lock (gate)
        {
            transferReadState.RemoveUpload(transferId);
            terminalTransfersAwaitingRemoval.Remove(transferId);
            transferWorkflowIds.Remove(transferId);
            transferAttempts.Remove(transferId);
            if (!transferReadState.RemoveActive(transferId, out var transfer))
                return;
            // The compact daemon stream already removed a finished workflow's
            // transfers. Its later durable handoff only releases retained state.
            if (transfer.WorkflowId is { } workflowId && !daemonLiveWorkflowIds.Contains(workflowId))
                return;
            AppendDaemonChanges(
                StateDelta.Empty with { RemovedTransferIds = [transferId] },
                DateTimeOffset.UtcNow);
        }
        PublishStateChanges();
    }

    public void AttachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered += OnJobRegistered;
        engine.Events.JobResultCreated += OnJobResultCreated;
        engine.Events.JobStateChanged += OnJobStateChanged;
        engine.Events.JobStructureChanged += OnJobStructureChanged;
        engine.Events.JobDiscoveryChanged += OnJobDiscoveryChanged;
        engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
        engine.Events.WorkflowRetired += OnWorkflowRetired;
        engine.Events.DownloadStarted += OnNestedSongDownloadStarted;
        engine.Events.FallbackTransferStarted += OnFallbackTransferStarted;
        engine.Events.DownloadProgress += OnDownloadProgress;
        engine.Events.DownloadStateChanged += OnDownloadStateChanged;
        engine.Events.TransferAttemptStarted += OnTransferAttemptStarted;
        engine.Events.TransferAttemptCompleted += OnTransferAttemptCompleted;
        engine.Events.TransferAttemptFailed += OnTransferAttemptFailed;
        engine.Events.TransferAttemptCancelled += OnTransferAttemptCancelled;
        engine.Events.TransferCompleted += OnTransferCompleted;
        engine.Events.TransferOutputChanged += OnTransferOutputChanged;
        engine.Events.TransferFailed += OnTransferFailed;
        engine.Events.TransferCancelled += OnTransferCancelled;
    }

    public void DetachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered -= OnJobRegistered;
        engine.Events.JobResultCreated -= OnJobResultCreated;
        engine.Events.JobStateChanged -= OnJobStateChanged;
        engine.Events.JobStructureChanged -= OnJobStructureChanged;
        engine.Events.JobDiscoveryChanged -= OnJobDiscoveryChanged;
        engine.Events.JobExecutionCompleted -= OnJobExecutionCompleted;
        engine.Events.WorkflowRetired -= OnWorkflowRetired;
        engine.Events.DownloadStarted -= OnNestedSongDownloadStarted;
        engine.Events.FallbackTransferStarted -= OnFallbackTransferStarted;
        engine.Events.DownloadProgress -= OnDownloadProgress;
        engine.Events.DownloadStateChanged -= OnDownloadStateChanged;
        engine.Events.TransferAttemptStarted -= OnTransferAttemptStarted;
        engine.Events.TransferAttemptCompleted -= OnTransferAttemptCompleted;
        engine.Events.TransferAttemptFailed -= OnTransferAttemptFailed;
        engine.Events.TransferAttemptCancelled -= OnTransferAttemptCancelled;
        engine.Events.TransferCompleted -= OnTransferCompleted;
        engine.Events.TransferOutputChanged -= OnTransferOutputChanged;
        engine.Events.TransferFailed -= OnTransferFailed;
        engine.Events.TransferCancelled -= OnTransferCancelled;
    }

    public JobSummaryDto? GetJobSummary(Guid jobId)
    {
        using (LockJobReads())
        {
            return records.TryGetValue(jobId, out var record)
                ? record.Current.Summary
                : null;
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobSummaries(IReadOnlyCollection<Guid> jobIds)
    {
        using (LockJobReads())
            return jobIds.Distinct().Where(records.ContainsKey).Select(id => records[id].Current.Summary).ToArray();
    }

    public JobDetailDto? GetJobDetail(Guid jobId)
    {
        using (LockJobReads())
        {
            if (!jobs.TryGetValue(jobId, out var job))
                return null;
            JobReadState record = UpdateJobRecord(job);
            int childCount = childJobIdsByParent.TryGetValue(jobId, out var childIds)
                ? childIds.Count
                : 0;
            // Detail rendering belongs to the read. Routine updates retain only
            // compact summaries, never query/target/attribute DTOs nobody asked for.
            JobPayloadDto payload = ServerSnapshotMapper.ToJobPayload(
                RefreshNestedSnapshots(job), GetTransferState,
                listProjections.GetValueOrDefault(jobId)?.Summary,
                aggregateProjections.GetValueOrDefault(jobId)?.Summary,
                albumAggregateProjections.GetValueOrDefault(jobId)?.Summary);
            return new JobDetailDto(record.Summary, payload, childCount);
        }
    }

    public JobDetailDto? GetJobDetailByDisplayId(Guid workflowId, int displayId)
    {
        lock (gate)
        {
            var record = CurrentJobs.FirstOrDefault(row => row.WorkflowId == workflowId
                && row.DisplayId == displayId);
            return record == null ? null : GetJobDetail(record.Id);
        }
    }

    public DirectoryManifestPageDto? GetDirectoryManifest(
        Guid jobId, int? attemptNumber, int afterOrdinal, int limit)
    {
        using (LockJobReads())
        {
            if (!jobs.TryGetValue(jobId, out var job))
                return null;
            DirectoryManifestSnapshot? manifest = job.Payload switch
            {
                AlbumJobSnapshotPayload album => album.Manifest,
                RemoteDirectoryJobSnapshotPayload directory => directory.Manifest,
                _ => null,
            };
            if (manifest == null || attemptNumber is { } requested && requested != manifest.AttemptNumber)
                return null;
            int plannedCount = manifest.Plan.Entries.Count;
            int totalCount = checked(plannedCount + manifest.SupplementalJobIds.Count);
            int first = (int)Math.Min((long)afterOrdinal + 1, totalCount);
            int end = (int)Math.Min((long)first + limit, totalCount);
            var items = new DirectoryManifestEntryDto[end - first];
            for (int ordinal = first; ordinal < end; ordinal++)
            {
                var entry = ordinal < plannedCount ? manifest.Plan.Entries[ordinal] : null;
                Guid? childId = ordinal >= plannedCount
                    ? manifest.SupplementalJobIds[ordinal - plannedCount]
                    : manifest.ChildrenMaterialized && ordinal < manifest.EntryJobIds.Count
                        ? manifest.EntryJobIds[ordinal] : null;
                JobSnapshot? child = childId is Guid id ? jobs.GetValueOrDefault(id) : null;
                FileDownloadStateSnapshot? file = child?.Payload switch
                {
                    SongJobSnapshotPayload song => song.File,
                    RemoteFileJobSnapshotPayload remote => remote.File,
                    _ => null,
                };
                PeerFileTarget? retainedTarget = entry?.Target ??
                    (manifest.SupplementalTargets is { } supplementalTargets && ordinal >= plannedCount
                        && ordinal - plannedCount < supplementalTargets.Count ? supplementalTargets[ordinal - plannedCount] : null);
                var target = retainedTarget != null ? ServerSnapshotMapper.ToPeerFileTargetDto(retainedTarget) : child?.Payload switch
                {
                    SongJobSnapshotPayload { ResolvedTarget: { } candidate } => ServerSnapshotMapper.ToPeerFileTargetDto(candidate),
                    SongJobSnapshotPayload { ExactTarget: { } exact } => ServerSnapshotMapper.ToPeerFileTargetDto(exact),
                    SongJobSnapshotPayload { LastAttemptedCandidate: { } attempted } => ServerSnapshotMapper.ToPeerFileTargetDto(attempted),
                    RemoteFileJobSnapshotPayload remote => ServerSnapshotMapper.ToPeerFileTargetDto(remote.Target),
                    _ => null,
                };
                items[ordinal - first] = new DirectoryManifestEntryDto(
                    ordinal,
                    target,
                    entry?.RelativeDirectoryComponents ?? [],
                    childId,
                    child == null ? null : BuildJobSummary(child),
                    file == null ? null : ServerSnapshotMapper.ToFileDownloadStateDto(file));
            }
            bool more = end < totalCount;
            return new DirectoryManifestPageDto(
                manifest.AttemptNumber,
                manifest.Plan.DisplayRoot,
                manifest.Plan.Entries.Count,
                manifest.Plan.TotalKnownBytes,
                manifest.ChildrenMaterialized,
                manifest.SupplementalJobIds.Count,
                job.Revision,
                items,
                more ? items[^1].Ordinal : null);
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        using (LockJobReads())
        {
            var rows = FilterJobs(CurrentJobs, query);
            return (query.NewestFirst
                    ? rows.OrderByDescending(record => record.DisplayId).ThenByDescending(record => record.Id)
                    : rows.OrderBy(record => record.DisplayId).ThenBy(record => record.Id))
                .Select(record => record.Summary)
                .ToList();
        }
    }

    internal sealed record JobPageCapture(
        IReadOnlyList<JobSummaryDto> Items, IReadOnlyList<Guid> OwnedJobIds,
        bool IsCompleteScope, StateStreamPositionDto Position);

    internal JobPageCapture CaptureJobPage(JobQuery query, long? afterDisplayId, Guid? afterJobId, int take)
    {
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
        using (LockJobReads())
        {
            bool complete = query.ParentJobId is Guid parentId
                && records.GetValueOrDefault(parentId)?.ChildrenCoverage == MembershipCoverage.Live
                || query.SubmissionId is Guid submissionId
                    && submissions.GetValueOrDefault(submissionId)?.Coverage == MembershipCoverage.Live;
            // A newly accepted parent's children are introduced by this same
            // owner. Until any child retires, this index is authoritative; no
            // retained query is needed, even while persistence is busy/unavailable.
            // Submission membership has the same positive proof, independently
            // of workflow IDs that can span multiple accepted submissions.
            var afterCursor = FilterJobs(CurrentJobs, query);
            if (afterDisplayId is long displayId && afterJobId is Guid jobId)
                afterCursor = query.NewestFirst
                    ? afterCursor.Where(row => row.DisplayId < displayId || row.DisplayId == displayId && row.Id.CompareTo(jobId) < 0)
                    : afterCursor.Where(row => row.DisplayId > displayId || row.DisplayId == displayId && row.Id.CompareTo(jobId) > 0);
            var items = (query.NewestFirst
                    ? afterCursor.OrderByDescending(row => row.DisplayId).ThenByDescending(row => row.Id)
                    : afterCursor.OrderBy(row => row.DisplayId).ThenBy(row => row.Id))
                .Take(take).Select(row => row.Summary).ToArray();
            return new(items, complete ? [] : records.Keys.ToArray(), complete,
                new(streamEpoch, daemonStreamSequence));
        }
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        lock (gate)
        {
            return workflows.Values
                .OrderBy(workflow => workflow.WorkflowId)
                .Select(workflow => workflow.ToSummary(records))
                .ToList();
        }
    }

    public IReadOnlyList<LiveWorkflowPageItem> GetWorkflowPageCandidates(
        long? afterFirstDisplayId,
        Guid? afterWorkflowId,
        int take)
    {
        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take));

        lock (gate)
        {
            IEnumerable<WorkflowStateRecord> filtered = workflows.Values;
            if (afterWorkflowId is Guid workflowId)
                filtered = filtered.Where(workflow => workflow.WorkflowId.CompareTo(workflowId) > 0);

            return filtered
                .OrderBy(workflow => workflow.WorkflowId)
                .Take(take)
                .Select(workflow => new LiveWorkflowPageItem(
                    workflow.FirstDisplayId,
                    workflow.ToSummary(records)))
                .ToArray();
        }
    }

    public WorkflowSummaryDto? GetWorkflowSummary(Guid workflowId)
    {
        lock (gate)
        {
            return workflows.TryGetValue(workflowId, out var workflow)
                ? workflow.ToSummary(records)
                : null;
        }
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId)
    {
        lock (gate)
        {
            if (!workflows.TryGetValue(workflowId, out var workflow))
                return null;

            return new WorkflowDetailDto(workflow.ToSummary(records));
        }
    }

    /// <summary>
    /// Captures bounded daemon live state and its cursor under the same ordering lock
    /// used to allocate stream sequences.
    /// </summary>
    public StateSnapshotDto GetDaemonSnapshot(DaemonStateDetail detail = DaemonStateDetail.Full)
    {
        using (LockJobReads())
        {
            var workflowIds = daemonLiveWorkflowIds.ToHashSet();
            return new StateSnapshotDto(
                StateStreamScopeDto.Daemon,
                new StateStreamPositionDto(streamEpoch, daemonStreamSequence),
                DateTimeOffset.UtcNow,
                daemonState,
                projectedWorkflows.Values
                    .Where(workflow => workflowIds.Contains(workflow.Summary.WorkflowId))
                    .OrderBy(workflow => workflow.Summary.Title)
                    .ToList(),
                PublishedJobs
                    .Where(job => workflowIds.Contains(job.WorkflowId)
                        && (detail == DaemonStateDetail.Full || job.ParentJobId is null))
                    .OrderBy(job => job.DisplayId)
                    .Select(job => job.ToDto()).ToList(),
                searchStates.Values
                    .Where(search => workflowIds.Contains(search.WorkflowId))
                    .OrderBy(search => search.JobId)
                    .ToList(),
                detail == DaemonStateDetail.Full ? activeTransfers.Values
                    .Where(transfer =>
                        transfer.WorkflowId is null
                        || workflowIds.Contains(transfer.WorkflowId.Value))
                    .OrderBy(transfer => transfer.TransferId)
                    .Select(transfer => transfer.ToDto()).ToList() : [], TransferSummary: transferReadState.Summary,
                JobHistoryPosition: new(streamEpoch, stateUpdates.LastJobSequence));
        }
    }

    internal (IReadOnlyList<JobReadState> Jobs, int? ChildCount, int? RetrievalChildCount, long Sequence)
        GetJobObservationSnapshot(Guid rootId, IReadOnlySet<Guid> requested)
    {
        using (LockJobReads())
        {
            var rows = requested.Select(id => records.GetValueOrDefault(id)?.Published)
                .OfType<JobReadState>().ToArray();
            bool rootPresent = records.ContainsKey(rootId);
            return (rows,
                rootPresent ? childJobIdsByParent.GetValueOrDefault(rootId)?.Count ?? 0 : null,
                rootPresent ? retrievalChildCountsByParent.GetValueOrDefault(rootId) : null,
                jobReadSequence);
        }
    }

    /// <summary>Captures the daemon position without materializing its live snapshot.</summary>
    public StateStreamPositionDto GetReadPosition()
    {
        lock (gate)
            return new(streamEpoch, daemonStreamSequence);
    }

    /// <summary>
    /// Captures the complete requested workflow, including terminal jobs, and its
    /// workflow-local cursor under one ordering lock.
    /// </summary>
    public StateSnapshotDto GetWorkflowSnapshot(Guid workflowId)
    {
        using (LockJobReads())
        {
            var workflowRows = projectedWorkflows.TryGetValue(workflowId, out var workflow)
                ? new[] { workflow }
                : [];

            return new StateSnapshotDto(
                StateStreamScopeDto.Workflow(workflowId),
                new StateStreamPositionDto(
                    GetWorkflowSnapshotEpoch(workflowId),
                    workflowStreamSequences.GetValueOrDefault(workflowId)),
                DateTimeOffset.UtcNow,
                null,
                workflowRows,
                PublishedJobs
                    .Where(job => job.WorkflowId == workflowId)
                    .OrderBy(job => job.DisplayId)
                    .Select(job => job.ToDto()).ToList(),
                searchStates.Values
                    .Where(search => search.WorkflowId == workflowId)
                    .OrderBy(search => search.JobId)
                    .ToList(),
                activeTransfers.Values
                    .Where(transfer => transfer.WorkflowId == workflowId)
                    .OrderBy(transfer => transfer.TransferId)
                    .Select(transfer => transfer.ToDto()).ToList());
        }
    }

    /// <summary>
    /// Reserves the stream generation before a live client fetches its initial snapshot.
    /// This keeps the snapshot and the first delta on one epoch without retaining state
    /// for arbitrary snapshot requests that never establish a subscription.
    /// </summary>
    internal bool HasWorkflowStreamReaders(Guid workflowId)
    {
        lock (gate) return workflowStreamReservations.GetValueOrDefault(workflowId) > 0;
    }

    public void ReserveWorkflowStream(Guid workflowId)
    {
        if (workflowId == Guid.Empty)
            throw new ArgumentException("A workflow stream requires a workflow ID.", nameof(workflowId));

        lock (gate)
        {
            workflowStreamReservations[workflowId] =
                workflowStreamReservations.GetValueOrDefault(workflowId) + 1;
            GetOrCreateWorkflowStreamEpoch(workflowId);
        }
    }

    public void ReleaseWorkflowStreamReservation(Guid workflowId)
    {
        lock (gate)
        {
            if (!workflowStreamReservations.TryGetValue(workflowId, out int count))
                return;

            if (count > 1)
            {
                workflowStreamReservations[workflowId] = count - 1;
                return;
            }

            workflowStreamReservations.Remove(workflowId);
            if (!workflows.ContainsKey(workflowId))
                ReleaseWorkflowStream(workflowId, workflowStreamEpochs.GetValueOrDefault(workflowId),
                    workflowStreamSequences.GetValueOrDefault(workflowId));
        }
    }

    public void UpdateDaemonState(
        SoulseekClientStatusDto soulseekClient,
        int restartCount,
        DateTimeOffset? searchRateLimitResetsAtUtc)
    {
        lock (gate)
        {
            var next = new DaemonStateDto(
                daemonState.Revision + 1,
                soulseekClient,
                restartCount,
                searchRateLimitResetsAtUtc,
                daemonState.Sharing,
                daemonState.Uploads,
                daemonState.Chat,
                daemonState.Notifications,
                daemonState.TerminalTransferCommitRevision);
            if (daemonState with { Revision = 0 } == next with { Revision = 0 })
                return;

            daemonState = next;
            AppendDaemonChanges(
                StateDelta.Empty with { Daemon = daemonState },
                DateTimeOffset.UtcNow);
        }

        PublishStateChanges();
    }

    public void NotifyTerminalTransferCommitted()
    {
        lock (gate)
        {
            daemonState = daemonState with
            {
                Revision = checked(daemonState.Revision + 1),
                TerminalTransferCommitRevision = checked(
                    daemonState.TerminalTransferCommitRevision + 1),
            };
            AppendDaemonChanges(
                StateDelta.Empty with { Daemon = daemonState },
                DateTimeOffset.UtcNow);
        }
        PublishStateChanges();
    }

    public void UpdateSearchRateLimit(DateTimeOffset? resetsAtUtc)
    {
        SoulseekClientStatusDto soulseekClient;
        int restartCount;
        lock (gate)
        {
            soulseekClient = daemonState.SoulseekClient;
            restartCount = daemonState.RestartCount;
        }

        UpdateDaemonState(soulseekClient, restartCount, resetsAtUtc);
    }

    public void UpdateDaemonRuntime(SoulseekClientStatusDto soulseekClient, int restartCount)
    {
        DateTimeOffset? searchRateLimitResetsAtUtc;
        lock (gate)
            searchRateLimitResetsAtUtc = daemonState.SearchRateLimitResetsAtUtc;

        UpdateDaemonState(soulseekClient, restartCount, searchRateLimitResetsAtUtc);
    }

    public void UpdateSharingRuntime(
        SharingStateDto sharing,
        UploadRuntimeStateDto uploads)
    {
        lock (gate)
        {
            var next = daemonState with
            {
                Revision = daemonState.Revision + 1,
                Sharing = sharing,
                Uploads = uploads,
            };
            if (daemonState with { Revision = 0 } == next with { Revision = 0 })
                return;
            daemonState = next;
            AppendDaemonChanges(
                StateDelta.Empty with { Daemon = daemonState },
                DateTimeOffset.UtcNow);
        }
        PublishStateChanges();
    }

    public void UpdateChatRuntime(
        ChatRuntimeStateDto chat,
        NotificationSummaryDto notifications)
    {
        lock (gate)
        {
            var next = daemonState with
            {
                Revision = daemonState.Revision + 1,
                Chat = chat,
                Notifications = notifications,
            };
            if (daemonState with { Revision = 0 } == next with { Revision = 0 })
                return;
            daemonState = next;
            AppendDaemonChanges(
                StateDelta.Empty with { Daemon = daemonState },
                DateTimeOffset.UtcNow);
        }
        PublishStateChanges();
    }

    public void PublishNotification(UserNotificationRecord notification)
    {
        lock (gate)
        {
            AppendDaemonChanges(
                StateDelta.Empty with
                {
                    Notifications = [ChatDtoMapper.ToDto(notification)],
                },
                notification.CreatedAtUtc);
        }
        PublishStateChanges();
    }

    public StateStreamPositionDto GetChatPosition(StateStreamScopeDto scope)
    {
        scope.Validate();
        if (scope.Kind is not (StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
            throw new ArgumentException("A chat position requires a chat scope.", nameof(scope));
        lock (gate)
            return new StateStreamPositionDto(streamEpoch, chatStreamSequences.GetValueOrDefault(scope));
    }

    public void PublishChatTarget(ChatTargetDeltaDto delta)
    {
        StateStreamScopeDto scope = delta.Kind == ServerChatTargetKind.Direct
            ? StateStreamScopeDto.ChatConversation(delta.TargetId)
            : StateStreamScopeDto.ChatRoom(delta.TargetId);
        lock (gate)
        {
            long previous = chatStreamSequences.GetValueOrDefault(scope);
            long sequence = previous + 1;
            chatStreamSequences[scope] = sequence;
            stateUpdates.Begin(scope, streamEpoch, previous, sequence, DateTimeOffset.UtcNow)
                .Merge(StateDelta.Empty with { ChatTargets = [delta] }, []);
        }
        PublishStateChanges();
    }

    public StateSnapshotDto GetUserBrowseSnapshot(UserBrowseDto resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (gate)
        {
            if (!userBrowses.TryGetValue(resource.BrowseId, out UserBrowseDto? current)
                || resource.Revision >= current.Revision)
            {
                userBrowses[resource.BrowseId] = resource;
            }
            else
            {
                resource = current;
            }
            return new StateSnapshotDto(
                StateStreamScopeDto.UserBrowse(resource.BrowseId),
                new StateStreamPositionDto(
                    streamEpoch,
                    userBrowseStreamSequences.GetValueOrDefault(resource.BrowseId)),
                DateTimeOffset.UtcNow,
                null,
                [],
                [],
                [],
                [],
                UserBrowse: resource);
        }
    }

    public void UpdateUserBrowse(UserBrowseDto resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (gate)
        {
            if (userBrowses.TryGetValue(resource.BrowseId, out UserBrowseDto? current)
                && resource.Revision <= current.Revision)
            {
                return;
            }
            userBrowses[resource.BrowseId] = resource;
            long previous = userBrowseStreamSequences.GetValueOrDefault(resource.BrowseId);
            long sequence = previous + 1;
            userBrowseStreamSequences[resource.BrowseId] = sequence;
            stateUpdates.Begin(StateStreamScopeDto.UserBrowse(resource.BrowseId),
                streamEpoch, previous, sequence, resource.UpdatedAt).SetUserBrowse(resource);
        }
        PublishStateChanges();
    }

    /// <summary>
    /// Drops the live projection after its backing ephemeral resource has been
    /// removed. There is no removal delta because future snapshots already return
    /// 410 before entering the live-state store.
    /// </summary>
    public void RemoveUserBrowse(Guid browseId)
    {
        lock (gate)
        {
            userBrowses.Remove(browseId);
            userBrowseStreamSequences.Remove(browseId);
        }
    }

    /// <summary>
    /// Allocates activity positions under the same ordering boundary as snapshots and
    /// state changes. Activity is emitted to the daemon stream and, when scoped, to the
    /// matching workflow stream.
    /// </summary>
    public void PublishActivity(
        string type,
        ActivityPayloadDto payload,
        Guid? workflowId = null,
        Guid? jobId = null,
        Guid? transferId = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        lock (gate)
        {
            var occurredAt = occurredAtUtc ?? DateTimeOffset.UtcNow;
            AppendActivity(
                type,
                payload,
                workflowId,
                jobId,
                transferId,
                occurredAt);
        }

        PublishStateChanges();
    }

    public ServerStatusDto GetStatistics()
    {
        lock (gate)
        {
            int totalJobCount = records.Count;
            int activeJobCount = workflows.Values.Sum(workflow => workflow.ActiveJobCount);
            int totalWorkflowCount = workflows.Count;
            int activeWorkflowCount = workflows.Values.Count(workflow => workflow.ActiveJobCount > 0);

            return new ServerStatusDto(
                new SoulseekClientStatusDto("None", [], false),
                totalJobCount,
                activeJobCount,
                totalWorkflowCount,
                activeWorkflowCount,
                0);
        }
    }

    public void MarkActiveJobsInfrastructureFailed(string reason, string? detail = null)
    {
        List<JobReadState> changedJobs;
        List<WorkflowSummaryDto> changedWorkflows;
        lock (gate)
        {
            var failedJobs = jobs.Values
                // Revision zero is accepted by the submission queue, before
                // engine registration. An engine failure does not own that work.
                .Where(job => job.Revision > 0 && EffectiveLifecycleState(job) != JobLifecycleState.Terminal)
                .Select(job => job with
                {
                    LifecycleState = JobLifecycleState.Terminal,
                    ActivityPhase = JobActivityPhase.None,
                    ActivityUntilUtc = null,
                    TerminalOutcome = JobTerminalOutcome.Failed,
                    FailureReason = JobFailureReason.Other,
                    FailureMessage = "Infrastructure failure: " + reason,
                    FailureDetail = detail,
                    CanCancel = false,
                    Revision = job.Revision + 1,
                })
                .ToList();

            foreach (var failedJob in failedJobs)
            {
                StoreJob(failedJob);
                UpdateJobRecord(failedJob);
            }

            changedJobs = failedJobs
                .Select(job => records[job.Id].Current)
                .OrderBy(summary => summary.DisplayId)
                .ToList();
            changedWorkflows = changedJobs
                .Select(job => job.WorkflowId)
                .Distinct()
                .Select(BuildWorkflowSummary)
                .ToList();
            ProjectStateChanges(
                changedJobs,
                changedWorkflows,
                [],
                [],
                [],
                DateTimeOffset.UtcNow);
        }

        PublishJobAndWorkflowUpserts(changedJobs, changedWorkflows);
        PublishStateChanges();
    }

    public static ServerJobKind GetJobKind(Job job)
        => ServerSnapshotMapper.ToServerJobKind(job);

    public void SetSourceJob(Guid jobId, Guid sourceJobId)
    {
        JobReadState? summary = null;
        lock (gate)
        {
            sourceJobIds[jobId] = sourceJobId;
            if (jobs.TryGetValue(jobId, out var job))
            {
                summary = UpdateJobRecord(job);
                ProjectStateChanges(
                    [summary],
                    [],
                    [],
                    [],
                    [],
                    DateTimeOffset.UtcNow);
            }
        }

        if (summary != null)
            JobUpserted?.Invoke(summary.Summary);
        PublishStateChanges();
    }

    internal void RegisterAcceptedRoot(JobSnapshot root, Guid? sourceJobId)
        => RegisterJob(root, parentJobId: null, sourceJobId, DateTimeOffset.UtcNow, acceptedRoot: true);

    private void OnJobRegistered(JobRegisteredChange change)
        => RegisterJob(change.Job, change.ParentJobId, change.SourceJobId, change.OccurredAtUtc);

    private void RegisterJob(JobSnapshot job, Guid? parentJobId, Guid? sourceJobId, DateTimeOffset occurredAtUtc, bool acceptedRoot = false)
    {
        List<JobReadState> summaries;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            StoreJob(job);
            parentJobIds[job.Id] = parentJobId;
            if (sourceJobId is Guid sourceId)
                sourceJobIds[job.Id] = sourceId;
            summaries = [UpdateJobRecord(job)];
            var entry = records[job.Id];
            if (entry.ChildrenCoverage == MembershipCoverage.Unknown && (acceptedRoot
                || parentJobId is Guid parent && records.GetValueOrDefault(parent)?.ChildrenCoverage == MembershipCoverage.Live
                || sourceJobId is Guid source && records.GetValueOrDefault(source)?.ChildrenCoverage == MembershipCoverage.Live))
                entry.ChildrenCoverage = MembershipCoverage.Live;
            if (acceptedRoot && job.SubmissionId is Guid acceptedId
                && submissions[acceptedId].Coverage == MembershipCoverage.Unknown)
                submissions[acceptedId].Coverage = MembershipCoverage.Live;
            if (job.Kind == JobSnapshotKind.Album)
            {
                foreach (Guid aggregateId in UpdateAlbumAggregateProjectionsForAlbum(job.Id))
                    summaries.Add(UpdateJobRecord(jobs[aggregateId]));
            }
            workflowSummary = BuildWorkflowSummary(job.WorkflowId);
            ProjectStateChanges(
                summaries,
                [workflowSummary],
                [],
                [],
                [],
                occurredAtUtc);
        }

        PublishJobAndWorkflowUpserts(summaries, [workflowSummary]);
        PublishStateChanges();
    }

    private void OnJobResultCreated(JobResultCreatedChange change)
    {
        List<JobReadState> changedJobs = [];
        WorkflowSummaryDto? workflowSummary = null;
        lock (gate)
        {
            StoreJob(change.ExtractJob);
            if (resultJobIds.TryGetValue(change.ExtractJob.Id, out Guid previousResultId))
                extractJobIdsByResult.Remove(previousResultId);
            resultJobIds[change.ExtractJob.Id] = change.ResultJob.Id;
            extractJobIdsByResult[change.ResultJob.Id] = change.ExtractJob.Id;

            changedJobs.Add(UpdateJobRecord(change.ExtractJob));

            if (jobs.ContainsKey(change.ResultJob.Id))
            {
                StoreJob(change.ResultJob);
                changedJobs.Add(UpdateJobRecord(change.ResultJob));
            }
            else if (change.ExtractJob.Payload is ExtractJobSnapshotPayload { AutoProcessResult: true })
            {
                StoreJob(change.ResultJob);
                parentJobIds[change.ResultJob.Id] = parentJobIds.GetValueOrDefault(change.ExtractJob.Id);
                changedJobs.Add(UpdateJobRecord(change.ResultJob));
            }
            else
            {
                // Persistence retains unprocessed extractor results too. Their
                // absence here means a submission query still needs history.
                MarkSubmissionRetained(change.ExtractJob.SubmissionId);
            }

            MarkContainingSummariesChanged(change.ExtractJob.Id, change.OccurredAtUtc);

            if (workflows.ContainsKey(change.ExtractJob.WorkflowId))
                workflowSummary = BuildWorkflowSummary(change.ExtractJob.WorkflowId);

            changedJobs = changedJobs.DistinctBy(summary => summary.JobId).ToList();
            ProjectStateChanges(
                changedJobs,
                workflowSummary != null ? [workflowSummary] : [],
                [],
                [],
                [],
                change.OccurredAtUtc);
        }

        PublishJobAndWorkflowUpserts(
            changedJobs,
            workflowSummary != null ? [workflowSummary] : []);
        PublishStateChanges();
    }

    private void OnJobStateChanged(JobStateChangedChange change)
        => OnJobChanged(change.Job, change.OccurredAtUtc);

    private void OnJobStructureChanged(JobStructureChangedChange change)
        => OnJobChanged(change.Job, change.OccurredAtUtc);

    private void OnJobChanged(JobSnapshotHeader snapshot, DateTimeOffset occurredAtUtc)
    {
        List<JobReadState> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        SearchStateDto? searchUpdate;
        lock (gate)
        {
            if (ServerSnapshotMapper.IsRunningOrPending(snapshot))
                executionCompletedJobs.Remove(snapshot.Id);

            var job = StoreJob(snapshot);
            MarkContainingSummariesChanged(job.Id, occurredAtUtc);
            summaries = [UpdateJobRecord(job)];
            workflowSummaries = [BuildWorkflowSummary(job.WorkflowId)];
            searchUpdate = ToSearchState(job);
            ProjectStateChanges(
                summaries,
                workflowSummaries,
                searchUpdate != null ? [searchUpdate] : [],
                [],
                [],
                occurredAtUtc);
        }

        if (summaries.Count > 0 || workflowSummaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, workflowSummaries);

        if (searchUpdate != null)
            SearchUpdated?.Invoke(searchUpdate);
        PublishStateChanges();
    }

    private void OnJobDiscoveryChanged(JobDiscoveryChangedChange change)
    {
        List<JobReadState> summaries = [];
        SearchStateDto? searchUpdate;
        lock (gate)
        {
            var job = StoreJob(change.Job);
            MarkContainingSummariesChanged(change.Job.Id, change.OccurredAtUtc);
            summaries.Add(UpdateJobRecord(job));
            searchUpdate = ToSearchState(job);
            ProjectStateChanges(
                summaries,
                [],
                searchUpdate != null ? [searchUpdate] : [],
                [],
                [],
                change.OccurredAtUtc);
        }

        if (summaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, []);

        if (searchUpdate != null)
            SearchUpdated?.Invoke(searchUpdate);
        PublishStateChanges();
    }

    private void OnJobExecutionCompleted(JobExecutionCompletedChange change)
    {
        JobReadState summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            var job = StoreJob(change.Job);
            executionCompletedJobs.Add(change.Job.Id);
            summary = UpdateJobRecord(job);
            workflowSummary = BuildWorkflowSummary(change.Job.WorkflowId);
            ProjectStateChanges(
                [summary],
                [workflowSummary],
                [],
                [],
                [],
                change.OccurredAtUtc);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
        PublishStateChanges();
    }

    private void OnWorkflowRetired(WorkflowRetiredChange change)
    {
        if (persistenceOwnsJobRetirement) return;
        Guid[] jobIds;
        lock (gate)
            jobIds = CurrentJobs.Where(record => record.WorkflowId == change.WorkflowId)
                .Select(record => record.Id).ToArray();
        RetireJobProjections(change.WorkflowId, jobIds);
    }

    internal void RetireJobProjections(Guid workflowId, IReadOnlyCollection<Guid> retiredJobIds)
    {
        lock (gate)
        {
            // An acknowledgement owns one exact generation, not every record
            // currently using its workflow. A successor may already be running.
            bool wasDaemonLive = daemonLiveWorkflowIds.Contains(workflowId);
            var jobIds = retiredJobIds
                .Where(id => records.TryGetValue(id, out var record) && record.Current.WorkflowId == workflowId)
                .ToHashSet();
            if (jobIds.Count == 0) return;
            foreach (Guid id in jobIds.Select(id => records[id].Current.SubmissionId).OfType<Guid>().Distinct())
                MarkSubmissionRetained(id);
            // Capture the whole retiring generation's membership before removing
            // any child index. A slow observer must retain the final counts even
            // when its next read happens after every runtime row is gone.
            foreach (Guid jobId in jobIds) PublishJobMembership(jobId);
            var searchJobIds = searchStates.Values
                .Where(search => jobIds.Contains(search.JobId))
                .Select(search => search.JobId)
                .ToHashSet();
            var transferIds = transferWorkflowIds
                .Where(pair => pair.Value == workflowId
                    && activeTransfers.GetValueOrDefault(pair.Key)?.JobId is Guid jobId
                    && jobIds.Contains(jobId)
                    && !terminalTransfersAwaitingRemoval.Contains(pair.Key))
                .Select(pair => pair.Key)
                .ToHashSet();
            foreach (Guid jobId in jobIds)
            {
                // Partial retirement leaves a live parent with retained children.
                // Never infer completeness merely from the parent's presence.
                if (parentJobIds.GetValueOrDefault(jobId) is Guid parent && !jobIds.Contains(parent)
                    && records.TryGetValue(parent, out var parentEntry))
                    parentEntry.ChildrenCoverage = MembershipCoverage.Retained;
                RemoveWorkflowRecord(records[jobId].Current);
                RemoveJobState(jobId);
            }
            foreach (Guid searchJobId in searchJobIds)
                searchStates.Remove(searchJobId);
            foreach (Guid transferId in transferIds)
            {
                transferReadState.RemoveActive(transferId, out _);
                transferWorkflowIds.Remove(transferId);
                transferAttempts.Remove(transferId);
            }

            bool hasSuccessor = workflows.ContainsKey(workflowId);
            WorkflowStateDto? successor = hasSuccessor ? ProjectWorkflow(BuildWorkflowSummary(workflowId)) : null;
            if (!hasSuccessor)
            {
                projectedWorkflows.Remove(workflowId);
                SetDaemonWorkflowVisible(workflowId, false);
            }
            var removal = StateDelta.Empty with
            {
                RemovedWorkflowIds = hasSuccessor ? [] : [workflowId],
                Workflows = successor == null ? [] : [successor],
                RemovedJobIds = jobIds.Order().ToArray(),
                RemovedSearchJobIds = searchJobIds.Order().ToArray(),
                RemovedTransferIds = transferIds.Order().ToArray(),
            };
            var occurredAtUtc = DateTimeOffset.UtcNow;
            AppendDaemonChanges(
                // The compact daemon stream already removed terminal workflows.
                // Do not re-add them or invalidate history again on acknowledgement.
                wasDaemonLive ? removal : StateDelta.Empty, occurredAtUtc);
            AppendWorkflowChanges(workflowId, removal, occurredAtUtc);
        }

        PublishStateChanges();
    }

    private void RemoveJobState(Guid jobId)
    {
        RemoveChildIndex(parentJobIds.GetValueOrDefault(jobId), jobId);
        retrievalChildCountsByParent.Remove(jobId);
        jobs.Remove(jobId);
        directoryProjections.Remove(jobId);
        changedContainerSummaries.Remove(jobId);
        listProjections.Remove(jobId);
        aggregateProjections.Remove(jobId);
        albumAggregateProjections.Remove(jobId);
        if (records.Remove(jobId, out var removed)) RemoveSubmissionMember(removed.Current.SubmissionId, jobId);
        executionCompletedJobs.Remove(jobId);
        songTransferStates.Remove(jobId);
        searchStates.Remove(jobId);
        parentJobIds.Remove(jobId);
        if (resultJobIds.Remove(jobId, out Guid resultJobId))
            extractJobIdsByResult.Remove(resultJobId);
        extractJobIdsByResult.Remove(jobId);
        sourceJobIds.Remove(jobId);

        if (childJobIdsByParent.Remove(jobId, out var directChildren))
        {
            foreach (Guid childId in directChildren)
                parentJobIds.Remove(childId);
        }

        if (nestedJobIdsByContainer.Remove(jobId, out var nestedIds))
        {
            foreach (Guid nestedId in nestedIds)
            {
                if (containerIdsByNestedJob.TryGetValue(nestedId, out var containers))
                {
                    containers.Remove(jobId);
                    if (containers.Count == 0)
                        containerIdsByNestedJob.Remove(nestedId);
                }
            }
        }
        if (containerIdsByNestedJob.Remove(jobId, out var containerIds))
        {
            foreach (Guid containerId in containerIds)
            {
                if (nestedJobIdsByContainer.TryGetValue(containerId, out var nested))
                {
                    nested.Remove(jobId);
                    if (nested.Count == 0)
                        nestedJobIdsByContainer.Remove(containerId);
                }
            }
        }
    }

    private static void RemoveIndexValue(
        Dictionary<Guid, HashSet<Guid>> index,
        Guid value)
    {
        foreach (Guid key in index.Keys.ToArray())
        {
            index[key].Remove(value);
            if (index[key].Count == 0)
                index.Remove(key);
        }
    }

    private void ReleaseWorkflowStream(Guid workflowId, Guid epoch, long sequence)
    {
        lock (gate)
        {
            // Keep a captured generation until its last publication and reader
            // settle, including activity captured during retirement callbacks.
            if (workflows.ContainsKey(workflowId)
                || workflowStreamReservations.ContainsKey(workflowId)
                || workflowStreamEpochs.GetValueOrDefault(workflowId) != epoch
                || workflowStreamSequences.GetValueOrDefault(workflowId) != sequence)
                return;
            var scope = StateStreamScopeDto.Workflow(workflowId);
            if (stateUpdates.HasPending(scope)) return;
            lock (stateBatchPublicationGate)
                if (readyStateBatches.Any(batch => batch.Scope == scope && batch.Epoch == epoch)) return;
            workflowStreamSequences.Remove(workflowId);
            workflowStreamEpochs.Remove(workflowId);
        }
    }

    private void OnDownloadStateChanged(DownloadStateChangedChange change)
    {
        lock (gate)
        {
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            if (transferDelta is null) return;
            songTransferStates[change.Job.Id] = change.State;
            var fileSummary = StoreFileProgress(change.Job, change.File, change.OccurredAtUtc);
            ProjectStateChanges(
                fileSummary is null ? [] : [fileSummary],
                [],
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        PublishStateChanges();
    }

    private void OnNestedSongDownloadStarted(DownloadStartedChange change)
    {
        List<JobReadState> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        lock (gate)
        {
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            if (transferDelta is null) return;
            var job = StoreJob(change.Job);
            MarkContainingSummariesChanged(change.Job.Id, change.OccurredAtUtc);
            summaries = [UpdateJobRecord(job)];
            workflowSummaries = summaries
                .Select(record => record.WorkflowId)
                .Distinct()
                .Select(BuildWorkflowSummary)
                .ToList();
            ProjectStateChanges(
                summaries,
                workflowSummaries,
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        if (summaries.Count > 0 || workflowSummaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, workflowSummaries);
        PublishStateChanges();
    }

    private void OnFallbackTransferStarted(FallbackTransferStartedChange change)
    {
        lock (gate)
        {
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            if (transferDelta is null) return;
            ProjectStateChanges(
                [],
                [],
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        PublishStateChanges();
    }

    private void OnDownloadProgress(DownloadProgressedChange change)
    {
        lock (gate)
        {
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            if (transferDelta is null) return;
            // Keep file state fresh for parent/detail reads without rebuilding
            // every containing album/directory on each transport callback.
            var fileSummary = StoreFileProgress(change.Job, change.File, change.OccurredAtUtc);
            ProjectStateChanges(
                fileSummary is null ? [] : [fileSummary],
                [],
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        PublishStateChanges();
    }

    private void OnTransferAttemptStarted(TransferAttemptStartedChange change)
    {
        lock (gate)
        {
            RecordTransferAttempt(new TransferAttemptHistoryDto(
                change.AttemptId,
                change.Transfer.Id,
                change.AttemptNumber,
                change.Source.ToString(),
                "Started",
                change.Transfer.Username,
                change.Transfer.RemotePath,
                change.OutputPath,
                change.OccurredAtUtc,
                null,
                "None",
                null,
                change.AttemptRevision));
        }
    }

    private void OnTransferAttemptCompleted(TransferAttemptCompletedChange change)
        => UpdateTerminalAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Completed",
            "None",
            null);

    private void OnTransferAttemptFailed(TransferAttemptFailedChange change)
        => UpdateTerminalAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Failed",
            "AttemptFailed",
            change.Exception.Message);

    private void OnTransferAttemptCancelled(TransferAttemptCancelledChange change)
        => UpdateTerminalAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Cancelled",
            change.Reason.ToString(),
            null);

    private void UpdateTerminalAttempt(
        TransferSnapshot transfer,
        Guid attemptId,
        int attemptNumber,
        long attemptRevision,
        DateTimeOffset occurredAtUtc,
        string state,
        string failureReason,
        string? failureMessage)
    {
        lock (gate)
        {
            var previous = transferAttempts.GetValueOrDefault(transfer.Id)?.GetValueOrDefault(attemptNumber);
            RecordTransferAttempt(new TransferAttemptHistoryDto(
                attemptId,
                transfer.Id,
                attemptNumber,
                transfer.Source.ToString(),
                state,
                transfer.Username,
                transfer.RemotePath,
                transfer.LocalPath ?? previous?.OutputPath,
                previous is { AttemptId: var previousId } && previousId == attemptId
                    ? previous.StartedAtUtc
                    : occurredAtUtc,
                occurredAtUtc,
                failureReason,
                failureMessage,
                attemptRevision));
        }
    }

    private void OnTransferOutputChanged(TransferOutputChangedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc);

    private void OnTransferCompleted(TransferCompletedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc);

    private void OnTransferFailed(TransferFailedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc, change.Exception.Message);

    private void OnTransferCancelled(TransferCancelledChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc);

    private void OnTerminalTransfer(TransferSnapshot transfer, DateTimeOffset occurredAtUtc, string? failureMessage = null)
    {
        lock (gate)
        {
            var transferDelta = UpsertTransfer(transfer, isTerminal: true, occurredAtUtc, failureMessage);
            if (transferDelta is null) return;
            terminalTransfersAwaitingRemoval.Add(transfer.Id);
            ProjectStateChanges(
                [],
                [],
                [],
                [transferDelta],
                [],
                occurredAtUtc);
        }

        PublishStateChanges();
    }

    private void PublishJobAndWorkflowUpserts(
        IReadOnlyList<JobReadState> jobSummaries,
        IReadOnlyList<WorkflowSummaryDto> workflowSummaries)
    {
        foreach (var summary in jobSummaries)
            JobUpserted?.Invoke(summary.Summary);

        foreach (var workflow in workflowSummaries)
            WorkflowUpserted?.Invoke(workflow);
    }

    private void ProjectStateChanges(
        IReadOnlyList<JobReadState> jobSummaries,
        IReadOnlyList<WorkflowSummaryDto> workflowSummaries,
        IReadOnlyList<SearchStateDto> searches,
        IReadOnlyList<TransferChange> transfers,
        IReadOnlyList<Guid> removedTransferIds,
        DateTimeOffset occurredAtUtc)
    {
        var byWorkflow = new Dictionary<Guid, ScopedStateChanges>();
        StateUpdateCoalescer.PendingBatch? daemon = null;
        ScopedStateChanges? ForWorkflow(Guid workflowId)
        {
            if (workflowId == Guid.Empty) return null;
            if (byWorkflow.TryGetValue(workflowId, out var existing)) return existing;
            bool wasLive = daemonLiveWorkflowIds.Contains(workflowId);
            bool isLive = projectedWorkflows.TryGetValue(workflowId, out var workflow)
                && workflow.Summary.State == ServerWorkflowState.Active;
            var created = new ScopedStateChanges(BeginWorkflowChange(workflowId, occurredAtUtc),
                wasLive || isLive ? daemon ??= BeginDaemonChange(occurredAtUtc) : null, wasLive, isLive);
            byWorkflow.Add(workflowId, created);
            return created;
        }

        // Establish membership before routing the fields. A workflow that just
        // finished still publishes its final changes and daemon removals together.
        HashSet<Guid>? seenWorkflows = workflowSummaries.Count > 1 ? [] : null;
        for (int i = 0; i < workflowSummaries.Count; i++)
        {
            var summary = workflowSummaries[i];
            if (seenWorkflows != null && !seenWorkflows.Add(summary.WorkflowId)) continue;
            var projected = ProjectWorkflow(summary);
            if (projected != null) ForWorkflow(summary.WorkflowId)?.Add(projected);
        }
        HashSet<Guid>? seenJobs = jobSummaries.Count > 1 ? [] : null;
        for (int i = 0; i < jobSummaries.Count; i++)
        {
            var summary = jobSummaries[i];
            if (seenJobs != null && !seenJobs.Add(summary.JobId)) continue;
            long coreRevision = jobs.GetValueOrDefault(summary.JobId)?.Revision ?? 0;
            var delta = ProjectJob(summary, coreRevision);
            if (delta != null) ForWorkflow(JobWorkflowId(delta))?.Add(delta);
        }
        for (int i = 0; i < searches.Count; i++)
        {
            var search = searches[i];
            var current = searchStates.GetValueOrDefault(search.JobId);
            if (current == null || search.Revision > current.Revision
                || search.ResultCount != current.ResultCount || search.IsComplete != current.IsComplete)
                ForWorkflow(search.WorkflowId)?.Add(search);
            if (search.IsComplete) searchStates.Remove(search.JobId);
            else searchStates[search.JobId] = search;
        }
        for (int i = 0; i < transfers.Count; i++)
            ForWorkflow(TransferWorkflowId(transfers[i]))?.Add(transfers[i]);
        for (int i = 0; i < removedTransferIds.Count; i++)
        {
            Guid id = removedTransferIds[i];
            ForWorkflow(transferWorkflowIds.GetValueOrDefault(id))?.RemoveTransfer(id);
        }
        for (int i = 0; i < searches.Count; i++)
        {
            var search = searches[i];
            if (search.IsComplete && byWorkflow.TryGetValue(
                records.GetValueOrDefault(search.JobId)?.Published?.WorkflowId ?? Guid.Empty, out var changes))
                changes.RemoveSearch(search.JobId);
        }

        HashSet<Guid>? terminalWorkflows = null;
        foreach (var pair in byWorkflow)
        {
            if (pair.Value.WasDaemonLive && !pair.Value.IsDaemonLive)
                (terminalWorkflows ??= []).Add(pair.Key);
        }
        if (terminalWorkflows != null)
        {
            // This full removal happens only when a workflow leaves the daemon
            // snapshot, never for individual child progress or retirement.
            foreach (var job in PublishedJobs)
                if (terminalWorkflows.Contains(job.WorkflowId)) (daemon ??= BeginDaemonChange(occurredAtUtc)).RemoveJob(job.JobId);
            foreach (var search in searchStates.Values)
                if (terminalWorkflows.Contains(search.WorkflowId)) (daemon ??= BeginDaemonChange(occurredAtUtc)).RemoveSearch(search.JobId);
            foreach (var transfer in activeTransfers.Values)
                if (transfer.WorkflowId is Guid id && terminalWorkflows.Contains(id))
                    (daemon ??= BeginDaemonChange(occurredAtUtc)).RemoveTransfer(transfer.TransferId);
        }
        foreach (var pair in byWorkflow)
        {
            SetDaemonWorkflowVisible(pair.Key, pair.Value.IsDaemonLive);
        }
        if (terminalWorkflows != null)
            foreach (Guid id in terminalWorkflows) (daemon ??= BeginDaemonChange(occurredAtUtc)).RemoveWorkflow(id);
        AppendTransferSummary(occurredAtUtc, daemon);
    }

    private void SetDaemonWorkflowVisible(Guid workflowId, bool visible)
    {
        if (visible) daemonLiveWorkflowIds.Add(workflowId);
        else daemonLiveWorkflowIds.Remove(workflowId);
        transferReadState.SetWorkflowVisible(workflowId, visible);
    }

    // Routing is a value scoped to one source mutation. The mutable maps live
    // at the publication owner, so repeated progress never builds source packets.
    private readonly record struct ScopedStateChanges(
        StateUpdateCoalescer.PendingBatch Workflow, StateUpdateCoalescer.PendingBatch? Daemon,
        bool WasDaemonLive, bool IsDaemonLive)
    {
        public void Add(WorkflowStateDto value) { Workflow.Add(value); Daemon?.Add(value); }
        public void Add(JobChange value) { Workflow.Add(value); Daemon?.Add(value); }
        public void Add(SearchStateDto value) { Workflow.Add(value); Daemon?.Add(value); }
        public void Add(TransferChange value) { Workflow.Add(value); Daemon?.Add(value); }
        public void RemoveSearch(Guid id) { Workflow.RemoveSearch(id); Daemon?.RemoveSearch(id); }
        public void RemoveTransfer(Guid id) { Workflow.RemoveTransfer(id); Daemon?.RemoveTransfer(id); }
    }

    private JobChange? ProjectJob(JobReadState fact, long coreRevision)
    {
        var entry = records[fact.JobId];
        var previous = entry.Published;
        JobFields fields = previous?.Difference(fact) ?? JobFields.All;
        if (fields == JobFields.None) return null;
        var current = fact.WithRevision(Math.Max(coreRevision, (previous?.Revision ?? 0) + 1));
        entry.Published = current;
        if (JobReadChanged is { } changed)
            changed(new(++jobReadSequence, current.JobId, current));
        return new(current, Added: previous is null, fields);
    }

    private WorkflowStateDto? ProjectWorkflow(WorkflowSummaryDto summary)
    {
        if (!projectedWorkflows.TryGetValue(summary.WorkflowId, out var previous))
        {
            var added = new WorkflowStateDto(1, summary);
            projectedWorkflows[summary.WorkflowId] = added;
            return added;
        }

        if (WorkflowSummaryEquals(previous.Summary, summary))
            return null;

        var current = new WorkflowStateDto(previous.Revision + 1, summary);
        projectedWorkflows[summary.WorkflowId] = current;
        return current;
    }

    private TransferChange? UpsertTransfer(TransferSnapshot transfer, bool isTerminal,
        DateTimeOffset? completedAtUtc = null, string? failureMessage = null)
    {
        // The engine serializes transfer events and gives each change a source
        // revision. Use that same revision for streams, reads and commit waits.
        if (activeTransfers.TryGetValue(transfer.Id, out var observed) && observed.Revision >= transfer.Revision)
            return null;
        transferWorkflowIds[transfer.Id] = transfer.WorkflowId ?? Guid.Empty;
        var current = new TransferReadSnapshot(transfer, isTerminal, completedAtUtc, failureMessage, observed);
        var delta = new TransferChange(current);
        transferReadState.UpsertActive(current, delta.Added);
        return delta;
    }

    private StateUpdateCoalescer.PendingBatch BeginDaemonChange(DateTimeOffset occurredAtUtc)
    {
        long previous = daemonStreamSequence++;
        return stateUpdates.Begin(StateStreamScopeDto.Daemon, streamEpoch,
            previous, daemonStreamSequence, occurredAtUtc);
    }

    private StateUpdateCoalescer.PendingBatch BeginWorkflowChange(Guid workflowId, DateTimeOffset occurredAtUtc)
    {
        long previous = workflowStreamSequences.GetValueOrDefault(workflowId);
        workflowStreamSequences[workflowId] = previous + 1;
        return stateUpdates.Begin(StateStreamScopeDto.Workflow(workflowId),
            GetOrCreateWorkflowStreamEpoch(workflowId), previous, previous + 1, occurredAtUtc);
    }

    private void AppendTransferSummary(DateTimeOffset occurredAtUtc, StateUpdateCoalescer.PendingBatch? pending = null)
    {
        LiveTransferSummaryDto summary = transferReadState.Summary;
        if (summary == publishedTransferSummary) return;
        publishedTransferSummary = summary;
        (pending ?? BeginDaemonChange(occurredAtUtc)).SetSummary(summary);
    }

    private void AppendDaemonChanges(StateDelta delta, DateTimeOffset occurredAtUtc)
    {
        var pending = delta.IsEmpty ? null : BeginDaemonChange(occurredAtUtc);
        pending?.Merge(delta, []);
        AppendTransferSummary(occurredAtUtc, pending);
    }

    private void AppendWorkflowChanges(Guid workflowId, StateDelta delta, DateTimeOffset occurredAtUtc)
    {
        if (!delta.IsEmpty) BeginWorkflowChange(workflowId, occurredAtUtc).Merge(delta, []);
    }

    private Guid GetOrCreateWorkflowStreamEpoch(Guid workflowId)
    {
        if (!workflowStreamEpochs.TryGetValue(workflowId, out Guid epoch))
        {
            epoch = Guid.NewGuid();
            workflowStreamEpochs[workflowId] = epoch;
        }
        return epoch;
    }

    private Guid GetWorkflowSnapshotEpoch(Guid workflowId)
    {
        if (workflowStreamEpochs.TryGetValue(workflowId, out Guid epoch))
            return epoch;

        return workflows.ContainsKey(workflowId)
               || workflowStreamReservations.ContainsKey(workflowId)
            ? GetOrCreateWorkflowStreamEpoch(workflowId)
            : streamEpoch;
    }

    private void AppendActivity(string type, ActivityPayloadDto payload, Guid? workflowId,
        Guid? jobId, Guid? transferId, DateTimeOffset occurredAtUtc)
    {
        var daemon = BeginDaemonChange(occurredAtUtc);
        daemon.AddActivity(new(daemonStreamSequence, occurredAtUtc, type, workflowId, jobId, transferId, payload));
        if (workflowId is Guid id)
        {
            var workflow = BeginWorkflowChange(id, occurredAtUtc);
            workflow.AddActivity(new(workflowStreamSequences[id], occurredAtUtc, type, id, jobId, transferId, payload));
        }
    }

    /// <summary>Start the shared coalescing clock for an in-process or remote publisher.</summary>
    public IAsyncDisposable StartStatePublication(TimeProvider? timeProvider = null)
        => new StateUpdatePublication(this, timeProvider ?? TimeProvider.System);

    // Summary reads and publication share this boundary. Transfer callbacks
    // maintain counters immediately; a burst constructs each containing job's
    // derived summary once, without waiting for another lifecycle transition.
    private Lock.Scope LockJobReads()
    {
        var held = gate.EnterScope();
        try
        {
            RefreshContainerSummaries();
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private void MarkDirectorySummaryChanged(Guid id, DirectoryProjection directory, DateTimeOffset occurredAtUtc)
    {
        if (!directory.HasSummaryChanges) return;
        changedContainerSummaries.Add(id);
        containerSummaryChangedAt = occurredAtUtc;
    }

    private void RefreshContainerSummaries()
    {
        if (changedContainerSummaries.Count == 0) return;
        var summaries = new List<JobReadState>(changedContainerSummaries.Count);
        foreach (Guid id in changedContainerSummaries)
            if (jobs.TryGetValue(id, out var job))
                summaries.Add(directoryProjections.TryGetValue(id, out var directory)
                    ? UpdateDirectorySummary(id, directory) : UpdateJobRecord(job));
        changedContainerSummaries.Clear();
        ProjectStateChanges(summaries, [], [], [], [], containerSummaryChangedAt);
    }

    /// <summary>Flush captured changes on the publisher's cadence.</summary>
    internal void FlushStateUpdates()
    {
        using (LockJobReads()) stateUpdates.Flush();
        DrainStateBatches();
        StateUpdatesFlushed?.Invoke();
    }

    private void PublishStateChanges()
    {
        // Ordinary facts stay in the owner until its next clock tick. Completion,
        // removal, notifications and messages preserve their prompt handoff.
        // Each mutation requests publication after releasing gate. A concurrent
        // mutation/clock may flush first; it owns draining those captured batches.
        // Routine progress must not contend for both state/publication gates
        // merely to discover that no prompt batch exists.
        if (!stateUpdates.HasPromptChanges)
        {
            StatePublicationRequested?.Invoke();
            return;
        }
        bool prompt;
        using (LockJobReads())
        {
            prompt = stateUpdates.FlushIfRequired();
        }
        DrainStateBatches();
        if (prompt) StateUpdatesFlushed?.Invoke();
        else StatePublicationRequested?.Invoke();
    }

    // Called only while gate is held: capture order already is publication order.
    // No out-of-order registry or second set of stream cursors is necessary.
    private void QueueStateBatches(IReadOnlyList<StateUpdate> batches)
    {
        lock (stateBatchPublicationGate)
            foreach (var batch in batches) readyStateBatches.Enqueue(batch);
    }

    private void DrainStateBatches()
    {
        lock (stateBatchPublicationGate)
        {
            if (publishingStateBatches || readyStateBatches.Count == 0) return;
            publishingStateBatches = true;
        }

        try
        {
            while (true)
            {
                StateUpdate? batch;
                lock (stateBatchPublicationGate)
                {
                    if (!readyStateBatches.TryDequeue(out batch))
                    {
                        publishingStateBatches = false;
                        return;
                    }
                }

                try { StateBatchPublished?.Invoke(batch); }
                finally
                {
                    // Closing the last reader may leave final activity queued.
                    // Release that generation after its actual last publication.
                    if (batch.Scope.Kind == StateStreamScopeKind.Workflow
                        && batch.Scope.WorkflowId is Guid workflowId)
                        ReleaseWorkflowStream(workflowId, batch.Epoch, batch.Sequence);
                }
            }
        }
        catch
        {
            lock (stateBatchPublicationGate)
                publishingStateBatches = false;
            throw;
        }
    }

    private static Guid JobWorkflowId(JobChange delta) => delta.Current.WorkflowId;

    private Guid TransferWorkflowId(TransferChange delta)
        => delta.Current.WorkflowId
            ?? transferWorkflowIds.GetValueOrDefault(delta.TransferId);

    private static TransferAttemptHistoryDto? ToUploadAttempt(UploadTransferSnapshot transfer)
    {
        if (transfer.Attempt is not { } attempt)
            return null;

        bool terminal = transfer.State is UploadTransferState.Completed
            or UploadTransferState.Cancelled
            or UploadTransferState.Failed
            or UploadTransferState.Interrupted;
        string state = terminal
            ? transfer.State switch
            {
                UploadTransferState.Completed => "Completed",
                UploadTransferState.Cancelled => "Cancelled",
                UploadTransferState.Interrupted => "Interrupted",
                _ => "Failed",
            }
            : "Started";
        return new TransferAttemptHistoryDto(
            attempt.AttemptId,
            transfer.TransferId,
            attempt.Number,
            "SoulseekPeer",
            state,
            transfer.Username,
            transfer.RemotePath,
            null,
            attempt.StartedAtUtc,
            terminal
                ? attempt.FinishedAtUtc ?? transfer.FinishedAtUtc
                : null,
            transfer.FailureReason.ToString(),
            null,
            transfer.Revision);
    }

    private static bool WorkflowSummaryEquals(WorkflowSummaryDto left, WorkflowSummaryDto right)
        => left.WorkflowId == right.WorkflowId
            && left.Title == right.Title
            && left.State == right.State
            && left.ActiveJobCount == right.ActiveJobCount
            && left.FailedJobCount == right.FailedJobCount
            && left.CompletedJobCount == right.CompletedJobCount
            && left.RootJobCount == right.RootJobCount;

    private SearchStateDto? ToSearchState(JobSnapshot job)
        => job.Payload is SearchJobSnapshotPayload search
            ? new SearchStateDto(job.Id, job.WorkflowId, search.Revision, search.ResultCount, search.IsComplete)
            : null;

    private WorkflowSummaryDto BuildWorkflowSummary(Guid workflowId)
        => workflows.TryGetValue(workflowId, out var workflow)
            ? workflow.ToSummary(records)
            : throw new InvalidOperationException($"Workflow {workflowId} is not registered.");

    private static bool IsDefaultRoot(JobReadState record)
        => record.ParentJobId == null;

    private IEnumerable<JobReadState> FilterJobs(
        IEnumerable<JobReadState> source,
        JobQuery query)
    {
        IEnumerable<JobReadState> filtered;
        if (query.ParentJobId is Guid parentJobId)
        {
            filtered = childJobIdsByParent.TryGetValue(parentJobId, out var childIds)
                ? childIds.Select(childId => records[childId].Current)
                : [];
        }
        else if (query.SubmissionId is Guid submissionId)
        {
            filtered = submissions.TryGetValue(submissionId, out var membership)
                ? membership.JobIds.Select(id => records[id].Current) : [];
        }
        else
        {
            filtered = source;
        }
        if (query.ExcludeExtractResults)
        {
            filtered = filtered.Where(record =>
                !(record.SourceJobId is Guid sourceId && IsCurrentExtract(sourceId, record.WorkflowId))
                && !(extractJobIdsByResult.TryGetValue(record.Id, out Guid extractId)
                    && IsCurrentExtract(extractId, record.WorkflowId)));
        }
        if (query.WorkflowId.HasValue)
            filtered = filtered.Where(record => record.WorkflowId == query.WorkflowId.Value);
        if (query.Kind.HasValue)
            filtered = filtered.Where(record => record.Kind == query.Kind.Value);
        if (query.LifecycleState.HasValue)
            filtered = filtered.Where(record => record.LifecycleState == query.LifecycleState.Value);
        if (query.TerminalOutcome.HasValue)
            filtered = filtered.Where(record => record.TerminalOutcome == query.TerminalOutcome.Value);
        if (query.SkipReason.HasValue)
            filtered = filtered.Where(record => record.SkipReason == query.SkipReason.Value);
        if (query.SubmissionId.HasValue)
            filtered = filtered.Where(record => record.SubmissionId == query.SubmissionId.Value);
        if (query.Role.HasValue)
            filtered = filtered.Where(record => record.Role == query.Role.Value);
        filtered = filtered.Where(record => MatchesJobArchive(record, query.Archived));
        return filtered.Where(record => query.ParentJobId.HasValue || query.IncludeAll || IsDefaultRoot(record));
    }

    private bool MatchesJobArchive(JobReadState record, bool archived)
        => archived == (record.SubmissionId is Guid id && archivedSubmissionIds.Contains(id));

    private bool IsCurrentExtract(Guid id, Guid workflowId)
        => records.TryGetValue(id, out var entry) && entry.Current.Kind == ServerJobKind.Extract
            && entry.Current.WorkflowId == workflowId;

    private JobReadState UpdateJobRecord(JobSnapshot job)
    {
        var current = jobs.GetValueOrDefault(job.Id) ?? job;
        // Headers read the maintained counters directly. Refreshing a detail
        // snapshot here would copy its payload for every child progress event.
        var directory = directoryProjections.GetValueOrDefault(current.Id);
        JobSummaryStatsDto? stats = directory is not null
            ? ServerSnapshotMapper.ToDirectorySummaryStats(directory.State,
                (current.Payload as AlbumJobSnapshotPayload)?.ResultCount)
            : ServerSnapshotMapper.ToJobSummaryStats(
                current,
                listProjections.GetValueOrDefault(current.Id)?.Summary,
                aggregateProjections.GetValueOrDefault(current.Id)?.Summary,
                albumAggregateProjections.GetValueOrDefault(current.Id)?.Summary);
        var record = CreateJobRecord(current, stats);
        directory?.MarkSummarized();
        return StoreJobRecord(record);
    }

    private JobReadState UpdateDirectorySummary(Guid id, DirectoryProjection directory)
    {
        if (!records.TryGetValue(id, out var entry)) return UpdateJobRecord(jobs[id]);
        if (!directory.HasSummaryChanges) return entry.Current;
        int? folders = (jobs[id].Payload as AlbumJobSnapshotPayload)?.ResultCount;
        var stats = ServerSnapshotMapper.ToDirectorySummaryStats(directory.State, folders);
        var record = entry.Current.WithStats(stats);
        directory.MarkSummarized();
        return ReferenceEquals(record, entry.Current) ? record : StoreJobRecord(record);
    }

    private JobReadState StoreJobRecord(JobReadState record)
    {
        records.TryGetValue(record.Id, out var entry);
        var oldRecord = entry?.Current;
        var parentJobId = record.ParentJobId;
        if (oldRecord is not null && oldRecord.ParentJobId != parentJobId)
            RemoveChildIndex(oldRecord.ParentJobId, record.Id);
        if (oldRecord == null || oldRecord.ParentJobId != parentJobId)
            AddChildIndex(parentJobId, record.Id);

        if (oldRecord is null || oldRecord.SubmissionId != record.SubmissionId)
        {
            RemoveSubmissionMember(oldRecord?.SubmissionId, record.Id);
            if (record.SubmissionId is Guid submissionId)
            {
                if (!submissions.TryGetValue(submissionId, out var membership))
                    submissions.Add(submissionId, membership = new SubmissionMembership());
                membership.JobIds.Add(record.Id);
            }
        }
        if (entry is null) records.Add(record.Id, new JobReadEntry(record));
        else entry.Current = record;
        if (oldRecord is not null && oldRecord.WorkflowId == record.WorkflowId)
            workflows[record.WorkflowId].Update(oldRecord, record);
        else
        {
            if (oldRecord is not null) RemoveWorkflowRecord(oldRecord);
            AddWorkflowRecord(record);
        }
        if (oldRecord == null || oldRecord.ParentJobId != parentJobId)
        {
            PublishJobMembership(oldRecord?.ParentJobId);
            PublishJobMembership(parentJobId);
        }
        return record;
    }

    private void MarkSubmissionRetained(Guid? id)
    {
        if (id is Guid submissionId && submissions.TryGetValue(submissionId, out var membership))
            membership.Coverage = MembershipCoverage.Retained;
    }

    private void RemoveSubmissionMember(Guid? id, Guid jobId)
    {
        if (id is not Guid submissionId || !submissions.TryGetValue(submissionId, out var membership)) return;
        membership.JobIds.Remove(jobId);
        if (membership.JobIds.Count == 0) submissions.Remove(submissionId);
    }

    private void PublishJobMembership(Guid? jobId)
    {
        if (jobId is not Guid id || JobReadChanged is not { } changed || !records.ContainsKey(id)) return;
        changed(new(++jobReadSequence, id, null,
            childJobIdsByParent.GetValueOrDefault(id)?.Count ?? 0,
            retrievalChildCountsByParent.GetValueOrDefault(id)));
    }

    private void AddChildIndex(Guid? parentJobId, Guid jobId)
    {
        if (parentJobId is not Guid parentId)
            return;
        if (!childJobIdsByParent.TryGetValue(parentId, out var childIds))
            childJobIdsByParent[parentId] = childIds = [];
        if (childIds.Add(jobId) && jobs.GetValueOrDefault(jobId)?.Kind == JobSnapshotKind.RetrieveFolder)
            retrievalChildCountsByParent[parentId] = retrievalChildCountsByParent.GetValueOrDefault(parentId) + 1;
    }

    private void RemoveChildIndex(Guid? parentJobId, Guid jobId)
    {
        if (parentJobId is not Guid parentId
            || !childJobIdsByParent.TryGetValue(parentId, out var childIds))
            return;
        if (childIds.Remove(jobId) && jobs.GetValueOrDefault(jobId)?.Kind == JobSnapshotKind.RetrieveFolder)
        {
            int remaining = retrievalChildCountsByParent.GetValueOrDefault(parentId) - 1;
            if (remaining > 0) retrievalChildCountsByParent[parentId] = remaining;
            else retrievalChildCountsByParent.Remove(parentId);
        }
        if (childIds.Count == 0)
            childJobIdsByParent.Remove(parentId);
    }

    private void AddWorkflowRecord(JobReadState record)
    {
        if (!workflows.TryGetValue(record.WorkflowId, out var workflow))
        {
            workflow = new WorkflowStateRecord(record.WorkflowId);
            workflows[record.WorkflowId] = workflow;
        }

        workflow.Add(record);
    }

    private void RemoveWorkflowRecord(JobReadState record)
    {
        if (!workflows.TryGetValue(record.WorkflowId, out var workflow))
            return;

        workflow.Remove(record);
        if (workflow.Count == 0)
            workflows.Remove(record.WorkflowId);
    }

    private void MarkContainingSummariesChanged(Guid jobId, DateTimeOffset occurredAtUtc)
    {
        var ids = new HashSet<Guid>();
        foreach (Guid memberId in LogicalResultAncestors(jobId))
        {
            if (containerIdsByNestedJob.TryGetValue(memberId, out HashSet<Guid>? containers))
                ids.UnionWith(containers);
        }
        if (jobs.GetValueOrDefault(jobId)?.Kind == JobSnapshotKind.Album)
            ids.UnionWith(AlbumAggregateAncestors(jobId));
        // StoreJob already maintains each member's counter contribution. The
        // containing summaries are derived: capture them at the shared read /
        // publication boundary rather than rebuilding a header per child.
        foreach (Guid id in ids)
        {
            if (directoryProjections.TryGetValue(id, out var directory))
                MarkDirectorySummaryChanged(id, directory, occurredAtUtc);
            else
            {
                changedContainerSummaries.Add(id);
                containerSummaryChangedAt = occurredAtUtc;
            }
        }
    }

    private IEnumerable<Guid> LogicalResultAncestors(Guid jobId)
    {
        yield return jobId;
        if (!extractJobIdsByResult.TryGetValue(jobId, out Guid current))
            yield break;
        var visited = new HashSet<Guid> { jobId };
        while (visited.Add(current))
        {
            yield return current;
            if (!extractJobIdsByResult.TryGetValue(current, out Guid extractId))
                yield break;
            current = extractId;
        }
    }

    private JobSnapshot StoreJob(JobSnapshotHeader snapshot)
    {
        if (snapshot is JobExecutionSnapshot execution)
            return StoreExecution(execution);
        var job = (JobSnapshot)snapshot;
        if (jobs.TryGetValue(job.Id, out var previous) && previous.Revision > job.Revision)
            return previous;
        if (containerIdsByNestedJob.TryGetValue(job.Id, out var owners))
        {
            foreach (Guid owner in owners)
                if (directoryProjections.TryGetValue(owner, out var projection))
                    projection.Update(job);
        }

        if (nestedJobIdsByContainer.Remove(job.Id, out HashSet<Guid>? previousIds))
        {
            foreach (Guid nestedId in previousIds)
            {
                HashSet<Guid> containers = containerIdsByNestedJob[nestedId];
                containers.Remove(job.Id);
                if (containers.Count == 0)
                    containerIdsByNestedJob.Remove(nestedId);
            }
        }

        HashSet<Guid> nestedIds = ServerSnapshotMapper.NestedJobIds(job)
            .Where(id => id != job.Id)
            .ToHashSet();
        if (nestedIds.Count > 0)
        {
            nestedJobIdsByContainer[job.Id] = nestedIds;
            foreach (Guid nestedId in nestedIds)
            {
                if (!containerIdsByNestedJob.TryGetValue(nestedId, out HashSet<Guid>? containers))
                    containerIdsByNestedJob[nestedId] = containers = [];
                containers.Add(job.Id);
            }
        }

        jobs[job.Id] = job;
        if (job.Payload is JobListSnapshotPayload list)
            listProjections[job.Id] = new MemberOutcomeProjection(
                list.Jobs, jobs.GetValueOrDefault, followExtractResults: true);
        if (job.Payload is AggregateJobSnapshotPayload aggregate)
            aggregateProjections[job.Id] = new MemberOutcomeProjection(
                aggregate.Songs, jobs.GetValueOrDefault, followExtractResults: false);
        if (job.Payload is AlbumAggregateJobSnapshotPayload && !albumAggregateProjections.ContainsKey(job.Id))
            albumAggregateProjections[job.Id] = new AlbumAggregateProjection(
                RegisteredAlbumDescendants(job.Id));
        UpdateContainerProjectionsForMember(job.Id);
        if (job.Kind == JobSnapshotKind.Album)
            UpdateAlbumAggregateProjectionsForAlbum(job.Id);
        switch (job.Payload)
        {
            case AlbumJobSnapshotPayload album:
                if (album.Manifest is { } albumManifest)
                    directoryProjections[job.Id] = new DirectoryProjection(
                        album.Directory, albumManifest,
                        CurrentDirectoryFiles(album.TrackJobs, albumManifest)
                            .Select(child => jobs.GetValueOrDefault(child.Id) ?? child));
                else
                    directoryProjections.Remove(job.Id);
                break;
            case RemoteDirectoryJobSnapshotPayload directory:
                if (directory.Manifest is { } directoryManifest)
                    directoryProjections[job.Id] = new DirectoryProjection(
                        directory.Directory, directoryManifest,
                        directory.FileJobs.Select(child => jobs.GetValueOrDefault(child.Id) ?? child));
                else
                    directoryProjections.Remove(job.Id);
                break;
        }
        return job;
    }

    private JobReadState? StoreFileProgress(JobReferenceSnapshot reference, FileDownloadStateSnapshot file,
        DateTimeOffset occurredAtUtc)
    {
        if (!jobs.TryGetValue(reference.Id, out var previous) || previous.Revision > reference.Revision)
            return null;
        FileDownloadStateSnapshot? oldFile = previous.Payload switch
        {
            SongJobSnapshotPayload song => song.File,
            RemoteFileJobSnapshotPayload remote => remote.File,
            _ => null,
        };
        if (oldFile is null || oldFile == file) return null;
        JobSnapshotPayload payload = previous.Payload switch
        {
            SongJobSnapshotPayload song => song with { File = file },
            RemoteFileJobSnapshotPayload remote => remote with { File = file },
            _ => throw new InvalidOperationException("A file state must belong to a file job."),
        };
        var current = previous with { Payload = payload };
        jobs[current.Id] = current;
        // Transport callbacks change file facts, never semantic membership or
        // job outcomes. Semantic summaries refresh on job events/transfer start;
        // only newly learned file sizes can change this file's summary here.
        JobReadState? changedSummary = null;
        if (previous.Payload is RemoteFileJobSnapshotPayload target
            && (oldFile.FileSize ?? target.Target.Size) != (file.FileSize ?? target.Target.Size))
            changedSummary = UpdateJobRecord(current);
        if (containerIdsByNestedJob.TryGetValue(current.Id, out var owners))
            foreach (Guid owner in owners)
                if (directoryProjections.TryGetValue(owner, out var directory))
                {
                    directory.Update(current);
                    MarkDirectorySummaryChanged(owner, directory, occurredAtUtc);
                }
        return changedSummary;
    }

    private JobSnapshot StoreExecution(JobExecutionSnapshot execution)
    {
        if (jobs.TryGetValue(execution.Id, out var previous) && previous.Revision > execution.Revision)
            return previous;
        var job = execution.ApplyTo(previous);
        jobs[job.Id] = job;
        if (containerIdsByNestedJob.TryGetValue(job.Id, out var owners))
            foreach (Guid owner in owners)
                if (directoryProjections.TryGetValue(owner, out var directory)) directory.Update(job);
        if (directoryProjections.TryGetValue(job.Id, out var ownDirectory))
        {
            var state = execution.Payload switch
            {
                AlbumExecutionPayload album => album.Directory,
                RemoteDirectoryExecutionPayload directory => directory.Directory,
                _ => null,
            };
            if (state != null) ownDirectory.UpdateState(state);
        }
        UpdateContainerProjectionsForMember(job.Id);
        if (job.Kind == JobSnapshotKind.Album) UpdateAlbumAggregateProjectionsForAlbum(job.Id);
        return job;
    }

    private void UpdateContainerProjectionsForMember(Guid jobId)
    {
        foreach (Guid memberId in LogicalResultAncestors(jobId))
        {
            if (!containerIdsByNestedJob.TryGetValue(memberId, out HashSet<Guid>? containers))
                continue;
            foreach (Guid containerId in containers)
            {
                if (listProjections.TryGetValue(containerId, out MemberOutcomeProjection? projection))
                    projection.Update(memberId, jobs.GetValueOrDefault);
                if (aggregateProjections.TryGetValue(containerId, out MemberOutcomeProjection? aggregate))
                    aggregate.Update(memberId, jobs.GetValueOrDefault);
            }
        }
    }

    private IReadOnlyList<Guid> UpdateAlbumAggregateProjectionsForAlbum(Guid albumId)
    {
        if (!jobs.TryGetValue(albumId, out JobSnapshot? album))
            return [];
        var changed = new List<Guid>();
        foreach (Guid aggregateId in AlbumAggregateAncestors(albumId))
        {
            if (!albumAggregateProjections.TryGetValue(aggregateId, out AlbumAggregateProjection? projection))
                continue;
            projection.Update(album);
            changed.Add(aggregateId);
        }
        return changed;
    }

    private IEnumerable<Guid> AlbumAggregateAncestors(Guid jobId)
    {
        var visited = new HashSet<Guid> { jobId };
        while (parentJobIds.TryGetValue(jobId, out Guid? parentId)
            && parentId is Guid parent
            && visited.Add(parent))
        {
            if (jobs.GetValueOrDefault(parent)?.Payload is AlbumAggregateJobSnapshotPayload)
                yield return parent;
            jobId = parent;
        }
    }

    private IEnumerable<JobSnapshot> RegisteredAlbumDescendants(Guid aggregateId)
    {
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid> { aggregateId };
        pending.Push(aggregateId);
        while (pending.TryPop(out Guid currentId))
        {
            if (!childJobIdsByParent.TryGetValue(currentId, out HashSet<Guid>? childIds))
                continue;
            foreach (Guid childId in childIds)
            {
                if (!visited.Add(childId)) continue;
                pending.Push(childId);
                if (jobs.TryGetValue(childId, out JobSnapshot? child)
                    && child.Kind == JobSnapshotKind.Album)
                    yield return child;
            }
        }
    }

    private static IEnumerable<JobSnapshot> CurrentDirectoryFiles(
        IReadOnlyList<JobSnapshot> children, DirectoryManifestSnapshot manifest)
    {
        if (!manifest.ChildrenMaterialized)
            return [];
        var ownedIds = manifest.EntryJobIds.Concat(manifest.SupplementalJobIds).ToHashSet();
        return children.Where(child => ownedIds.Contains(child.Id));
    }

    private JobSnapshot RefreshNestedSnapshots(JobSnapshot job)
    {
        if (jobs.TryGetValue(job.Id, out var current) && !ReferenceEquals(current, job))
            job = current;

        return job.Payload switch
        {
            AlbumJobSnapshotPayload album => RefreshAlbumPayload(job, album),
            RemoteDirectoryJobSnapshotPayload directory => RefreshRemoteDirectoryPayload(job, directory),
            AggregateJobSnapshotPayload => job,
            JobListSnapshotPayload => job,
            _ => job,
        };
    }

    private JobSnapshot RefreshAlbumPayload(JobSnapshot job, AlbumJobSnapshotPayload album)
    {
        return job with
        {
            Payload = album with
            {
                Directory = directoryProjections.GetValueOrDefault(job.Id)?.State ?? album.Directory,
            },
        };
    }

    private JobSnapshot RefreshRemoteDirectoryPayload(
        JobSnapshot job,
        RemoteDirectoryJobSnapshotPayload directory)
    {
        return job with
        {
            Payload = directory with
            {
                Directory = directoryProjections.GetValueOrDefault(job.Id)?.State ?? directory.Directory,
            },
        };
    }

    private sealed class MemberOutcomeProjection
    {
        private readonly Dictionary<Guid, (JobSnapshot Fallback, int Count)> members = [];
        private readonly Dictionary<Guid, LogicalJobProgress.Outcome> outcomes = [];
        private readonly bool followExtractResults;
        public JobOutcomeSummary Summary { get; private set; }

        public MemberOutcomeProjection(
            IEnumerable<JobSnapshot> jobs,
            Func<Guid, JobSnapshot?> getJob,
            bool followExtractResults)
        {
            this.followExtractResults = followExtractResults;
            foreach (JobSnapshot job in jobs)
            {
                if (members.TryGetValue(job.Id, out var existing))
                    members[job.Id] = (job, checked(existing.Count + 1));
                else
                    members[job.Id] = (job, 1);
            }
            foreach (Guid id in members.Keys)
                Update(id, getJob);
        }

        public void Update(Guid memberId, Func<Guid, JobSnapshot?> getJob)
        {
            if (!members.TryGetValue(memberId, out var member))
                return;
            JobSnapshot snapshot = getJob(memberId) ?? member.Fallback;
            LogicalJobProgress.Outcome current = followExtractResults
                ? LogicalJobProgress.FromSnapshot(snapshot, getJob)
                : new(snapshot.LifecycleState, snapshot.TerminalOutcome, snapshot.SkipReason);
            if (outcomes.TryGetValue(memberId, out LogicalJobProgress.Outcome previous))
            {
                if (previous == current)
                    return;
                Summary = Summary.Exclude(
                    previous.Lifecycle, previous.Terminal, previous.Skip, member.Count);
            }
            outcomes[memberId] = current;
            Summary = Summary.Include(
                current.Lifecycle, current.Terminal, current.Skip, member.Count);
        }
    }

    private sealed class AlbumAggregateProjection
    {
        private readonly Dictionary<Guid, (JobLifecycleState Lifecycle, JobTerminalOutcome Terminal, JobSkipReason Skip)> albums = [];
        public JobOutcomeSummary Summary { get; private set; }

        public AlbumAggregateProjection(IEnumerable<JobSnapshot> registeredAlbums)
        {
            foreach (JobSnapshot album in registeredAlbums)
                Update(album);
        }

        public void Update(JobSnapshot album)
        {
            var current = (album.LifecycleState, album.TerminalOutcome, album.SkipReason);
            if (albums.TryGetValue(album.Id, out var previous))
            {
                if (previous == current)
                    return;
                Summary = Summary.Exclude(previous.Lifecycle, previous.Terminal, previous.Skip);
            }
            albums[album.Id] = current;
            Summary = Summary.Include(
                current.LifecycleState, current.TerminalOutcome, current.SkipReason);
        }
    }

    private sealed class DirectoryProjection
    {
        private readonly DirectoryProgress progress = new();
        private readonly long plannedBytes;
        private readonly int plannedUnknownCount;
        private DirectoryDownloadStateSnapshot? summarizedState;
        public DirectoryDownloadStateSnapshot State { get; private set; }

        // Transport states often leave directory counters unchanged. Track the
        // derived state itself so those callbacks can reuse its indexed summary;
        // changed bytes/outcomes/sizes and a new attempt invalidate it here.
        public bool HasSummaryChanges => !ReferenceEquals(State, summarizedState);
        public void MarkSummarized() => summarizedState = State;

        public DirectoryProjection(
            DirectoryDownloadStateSnapshot state, DirectoryManifestSnapshot manifest,
            IEnumerable<JobSnapshot> files)
        {
            State = state;
            plannedBytes = manifest.Plan.TotalKnownBytes;
            plannedUnknownCount = manifest.Plan.Entries.Count(entry => entry.Target.Size is null);
            var supplementalIds = manifest.SupplementalJobIds.ToHashSet();
            if (files.TryGetNonEnumeratedCount(out int count))
                progress.EnsureCapacity(count);
            foreach (var file in files)
            {
                var data = FileState(file);
                progress.Add(file.Id, data.BytesTransferred, data.FileSize, file.LifecycleState,
                    file.TerminalOutcome, file.SkipReason, supplementalIds.Contains(file.Id));
            }
            Refresh();
        }

        public void Update(JobSnapshot file)
        {
            var data = FileState(file);
            if (progress.Update(file.Id, data.BytesTransferred, data.FileSize,
                file.LifecycleState, file.TerminalOutcome, file.SkipReason))
                Refresh();
        }

        public void UpdateState(DirectoryDownloadStateSnapshot state)
        {
            State = state;
            Refresh();
        }

        private void Refresh()
        {
            var summary = progress.Summary;
            State = State with
            {
                FileCount = summary.FileCount,
                TerminalFileCount = summary.TerminalFileCount,
                SuccessfulFileCount = summary.SuccessfulFileCount,
                FailedFileCount = summary.FailedFileCount,
                BytesTransferred = summary.BytesTransferred,
                TotalKnownBytes = plannedBytes + summary.SupplementalKnownBytes,
                UnknownFileSizeCount = plannedUnknownCount + summary.SupplementalUnknownCount,
                Progress = summary.GetProgress(plannedBytes),
            };
        }

        private static FileDownloadStateSnapshot FileState(JobSnapshot file)
            => file.Payload switch
            {
                SongJobSnapshotPayload song => song.File,
                RemoteFileJobSnapshotPayload remote => remote.File,
                _ => throw new InvalidOperationException("A directory member must be a file job."),
            };
    }

    private JobSummaryDto BuildJobSummary(JobSnapshot job, JobSummaryStatsDto? stats = null)
        => CreateJobRecord(job, stats).Summary;

    private JobReadState CreateJobRecord(JobSnapshot job, JobSummaryStatsDto? stats)
    {
        var parentJobId = parentJobIds.GetValueOrDefault(job.Id);
        Guid? resultJobId = resultJobIds.TryGetValue(job.Id, out var resultId) ? resultId : null;
        Guid? sourceJobId = sourceJobIds.TryGetValue(job.Id, out var sourceId) ? sourceId : null;

        return new JobReadState(ServerSnapshotMapper.ToJobSummary(
            job, parentJobId, resultJobId, sourceJobId, EffectiveLifecycleState(job),
            EffectiveActivityPhase(job), EffectiveActivityUntilUtc(job), EffectiveTerminalOutcome(job),
            stats, includeActions: false), job.Revision, job.CanCancel);
    }

    private JobLifecycleState EffectiveLifecycleState(JobSnapshot job)
        => executionCompletedJobs.Contains(job.Id)
            && ServerSnapshotMapper.IsRunningOrPending(job)
                ? JobLifecycleState.Terminal
                : job.LifecycleState;

    private JobActivityPhase EffectiveActivityPhase(JobSnapshot job)
        => EffectiveLifecycleState(job) == JobLifecycleState.Terminal
            ? JobActivityPhase.None
            : job.ActivityPhase;

    private DateTimeOffset? EffectiveActivityUntilUtc(JobSnapshot job)
        => EffectiveActivityPhase(job) == JobActivityPhase.None
            ? null
            : job.ActivityUntilUtc;

    private JobTerminalOutcome EffectiveTerminalOutcome(JobSnapshot job)
        => executionCompletedJobs.Contains(job.Id)
            && ServerSnapshotMapper.IsRunningOrPending(job)
                ? JobTerminalOutcome.Succeeded
                : job.TerminalOutcome;

    private string? GetTransferState(Guid jobId)
        => songTransferStates.TryGetValue(jobId, out var state) ? state : null;

    private static bool IsActiveRecord(JobReadState record)
        => record.LifecycleState != ServerJobLifecycleState.Terminal;

    private static bool IsFailedRecord(JobReadState record)
        => record.TerminalOutcome is ServerJobTerminalOutcome.Failed
            or ServerJobTerminalOutcome.Cancelled
            or ServerJobTerminalOutcome.PartialSuccess
            || (record.TerminalOutcome == ServerJobTerminalOutcome.Skipped
                && record.SkipReason != ServerJobSkipReason.AlreadyExists);

    public static ServerJobLifecycleState ToServerJobLifecycleState(JobLifecycleState state)
        => ServerSnapshotMapper.ToServerJobLifecycleState(state);

    public static ServerJobActivityPhase ToServerJobActivityPhase(JobActivityPhase phase)
        => ServerSnapshotMapper.ToServerJobActivityPhase(phase);

    public static ServerJobTerminalOutcome ToServerJobTerminalOutcome(JobTerminalOutcome outcome)
        => ServerSnapshotMapper.ToServerJobTerminalOutcome(outcome);

    public static ServerSongDownloadSource ToServerSongDownloadSource(SongDownloadSource source)
        => ServerSnapshotMapper.ToServerSongDownloadSource(source);

    public static ServerJobSkipReason ToServerJobSkipReason(JobSkipReason reason)
        => ServerSnapshotMapper.ToServerJobSkipReason(reason);

    public static ServerJobFailureReason? ToServerFailureReason(JobFailureReason reason)
        => ServerSnapshotMapper.ToServerFailureReason(reason);

    public static ServerJobCancellationSource ToServerJobCancellationSource(JobCancellationSource source)
        => ServerSnapshotMapper.ToServerJobCancellationSource(source);

    public static ServerFolderRetrievalOutcome ToServerFolderRetrievalOutcome(FolderRetrievalOutcome outcome)
        => ServerSnapshotMapper.ToServerFolderRetrievalOutcome(outcome);

    private enum MembershipCoverage { Unknown, Live, Retained }

    // Membership indexes share job lifetime; there is no retained cache or
    // independent cleanup. Fresh acceptance proves completeness once. Losing a
    // member makes it incomplete until the remaining runtime rows retire.
    private sealed class SubmissionMembership
    {
        public HashSet<Guid> JobIds { get; } = [];
        public MembershipCoverage Coverage { get; set; }
    }

    private sealed class JobReadEntry(JobReadState current)
    {
        // Indexed reads and the last emitted stream baseline share one fact type.
        // The stream revision also accounts for daemon-derived member changes.
        public JobReadState Current { get; set; } = current;
        public JobReadState? Published { get; set; }
        public MembershipCoverage ChildrenCoverage { get; set; }
    }

    private readonly record struct WorkflowRecordRef(int DisplayId, Guid JobId);

    private sealed class WorkflowStateRecord(Guid workflowId)
    {
        private static readonly IComparer<WorkflowRecordRef> RecordRefComparer =
            Comparer<WorkflowRecordRef>.Create((x, y) =>
            {
                int displayIdComparison = x.DisplayId.CompareTo(y.DisplayId);
                return displayIdComparison != 0
                    ? displayIdComparison
                    : x.JobId.CompareTo(y.JobId);
            });

        private readonly SortedSet<WorkflowRecordRef> allJobs = new(RecordRefComparer);
        private readonly SortedSet<WorkflowRecordRef> rootJobs = new(RecordRefComparer);
        private readonly SortedSet<WorkflowRecordRef> itemNameJobs = new(RecordRefComparer);

        public Guid WorkflowId => workflowId;
        public int Count => allJobs.Count;
        public int ActiveJobCount { get; private set; }
        public int FailedJobCount { get; private set; }
        public int CompletedJobCount { get; private set; }
        public int FirstDisplayId => allJobs.Count == 0 ? int.MaxValue : allJobs.Min.DisplayId;

        public void Add(JobReadState record)
        {
            var key = ToRef(record);
            if (!allJobs.Add(key))
                return;

            if (IsDefaultRoot(record))
                rootJobs.Add(key);

            if (HasItemName(record))
                itemNameJobs.Add(key);

            AdjustCounts(record, 1);
        }

        public void Remove(JobReadState record)
        {
            var key = ToRef(record);
            if (!allJobs.Remove(key))
                return;

            if (IsDefaultRoot(record))
                rootJobs.Remove(key);

            if (HasItemName(record))
                itemNameJobs.Remove(key);

            AdjustCounts(record, -1);
        }

        public void Update(JobReadState previous, JobReadState current)
        {
            // Progress usually changes neither order nor membership. Keep the
            // existing index nodes instead of removing/reallocating them (or
            // recreating the whole workflow when it contains only one job).
            var previousKey = ToRef(previous);
            var key = ToRef(current);
            UpdateMembership(allJobs, previousKey, true, key, true);
            UpdateMembership(rootJobs, previousKey, IsDefaultRoot(previous), key, IsDefaultRoot(current));
            UpdateMembership(itemNameJobs, previousKey, HasItemName(previous), key, HasItemName(current));
            AdjustCounts(previous, -1);
            AdjustCounts(current, 1);
        }

        private static bool HasItemName(JobReadState record) => !string.IsNullOrWhiteSpace(record.ItemName);

        private static void UpdateMembership(
            SortedSet<WorkflowRecordRef> index,
            WorkflowRecordRef previousKey, bool wasMember,
            WorkflowRecordRef key, bool isMember)
        {
            if (wasMember && (!isMember || previousKey != key)) index.Remove(previousKey);
            if (isMember && (!wasMember || previousKey != key)) index.Add(key);
        }

        private void AdjustCounts(JobReadState record, int delta)
        {
            if (IsActiveRecord(record)) ActiveJobCount += delta;
            else CompletedJobCount += delta;
            if (IsFailedRecord(record)) FailedJobCount += delta;
        }

        public WorkflowSummaryDto ToSummary(IReadOnlyDictionary<Guid, JobReadEntry> records)
        {
            var firstRecord = records[allJobs.Min.JobId].Current;
            string? itemNameTitle = itemNameJobs.Count > 0
                ? records[itemNameJobs.Min.JobId].Current.ItemName
                : null;

            string title = itemNameTitle
                ?? firstRecord.QueryText
                ?? firstRecord.Kind.ToWireString();

            var state = ActiveJobCount > 0 ? ServerWorkflowState.Active
                : FailedJobCount > 0 ? ServerWorkflowState.Failed
                : ServerWorkflowState.Completed;

            return new WorkflowSummaryDto(
                workflowId,
                title,
                state,
                rootJobs.Count,
                ActiveJobCount,
                FailedJobCount,
                CompletedJobCount);
        }

        private static WorkflowRecordRef ToRef(JobReadState record)
            => new(record.DisplayId, record.Id);
    }
}

internal sealed record EngineStateStoreRetainedWorkflowCounts(
    int Jobs,
    int Records,
    int Workflows,
    int ParentLinks,
    int ChildIndexes,
    int NestedIndexes,
    int ContainerIndexes,
    int ResultLinks,
    int SourceLinks,
    int ExecutionMarkers,
    int SongTransferStates,
    int Transfers,
    int Attempts,
    int TransferWorkflowLinks,
    int Searches,
    int ProjectedJobs,
    int ProjectedWorkflows,
    int WorkflowStreamSequences,
    int WorkflowStreamEpochs,
    int WorkflowStreamReservations,
    int DaemonWorkflowLinks,
    int ListProjections = 0,
    int ExtractReverseLinks = 0,
    int AggregateProjections = 0,
    int AlbumAggregateProjections = 0);
