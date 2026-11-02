using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Core.Chat;
using Sockseek.Server.Persistence;

namespace Sockseek.Server;

public sealed record LiveWorkflowPageItem(long FirstDisplayId, WorkflowSummaryDto Summary);
public sealed record LiveTransferDetail(TransferStateDto Transfer, TransferAttemptHistoryDto? LatestAttempt);

public sealed class EngineStateStore
{
    private readonly bool persistenceOwnsJobRetirement;
    internal void SetJobPersistence(JobPersistence? writer)
    {
        lock (gate) jobState.Persistence = writer;
    }

    public EngineStateStore() => stateUpdates = new(QueueStateBatches);

    internal EngineStateStore(bool persistenceOwnsJobRetirement) : this()
        => this.persistenceOwnsJobRetirement = persistenceOwnsJobRetirement;

    internal void SetTransferPersistence(TransferPersistence? writer)
    {
        lock (gate) transferState.Persistence = writer;
    }

    private readonly Lock gate = new();
    // Capture and cursor allocation share gate. Only flushed immutable batches
    // enter the publication queue; callbacks run outside both locks.
    private readonly Lock stateBatchPublicationGate = new();
    private readonly StateUpdateCoalescer stateUpdates;
    private readonly Queue<StateUpdate> readyStateBatches = [];
    private bool publishingStateBatches;
    private readonly JobState jobState = new();
    private readonly TransferState transferState = new();
    private LiveTransferSummaryDto publishedTransferSummary = LiveTransferSummaryDto.Empty;
    private IReadOnlyDictionary<Guid, TransferReadSnapshot> activeTransfers => transferState.Active;
    private IReadOnlyDictionary<Guid, TransferReadSnapshot> liveUploadTransfers => transferState.Uploads;
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
    internal event Action<JobReadChange> JobReadChanged
    {
        add => jobState.ReadChanged += value;
        remove => jobState.ReadChanged -= value;
    }

    internal event Action JobListChanged
    {
        add => jobState.ListChanged += value;
        remove => jobState.ListChanged -= value;
    }
    internal bool MatchesJobQuery(JobReadState row, JobQuery query)
    {
        lock (gate) return jobState.MatchesQuery(row, query);
    }

    internal event Action<TransferReadChange> TransferReadChanged
    {
        add => transferState.Changed += value;
        remove => transferState.Changed -= value;
    }
    internal StateStreamPositionDto GetTransferReadPosition()
    {
        lock (gate) return transferState.Position;
    }
    internal void MarkTransferHistoryChanged()
    {
        lock (gate) transferState.MarkHistoryChanged();
    }

    public void SetSubmissionArchived(Guid submissionId, bool archived)
    {
        lock (gate) jobState.SetSubmissionArchived(submissionId, archived);
    }

    internal EngineStateStoreRetainedWorkflowCounts RetainedWorkflowStateCounts
    {
        get
        {
            lock (gate) return jobState.RetainedCounts with
            {
                Transfers = activeTransfers.Count, Attempts = transferState.AttemptCount,
                WorkflowStreamSequences = workflowStreamSequences.Count,
                WorkflowStreamEpochs = workflowStreamEpochs.Count,
                WorkflowStreamReservations = workflowStreamReservations.Count,
                DaemonWorkflowLinks = daemonLiveWorkflowIds.Count,
            };
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
        lock (gate) return transferState.ForJobs(jobIds);
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
                    transferState.LatestAttempt(transferId));
        }
    }

    public IReadOnlyList<TransferAttemptHistoryDto>? GetLiveTransferAttempts(
        Guid transferId, int afterAttemptNumber, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterAttemptNumber);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (gate) return transferState.ReadAttempts(transferId, afterAttemptNumber, limit);
    }

    public void UpdateUploadTransfer(UploadTransferSnapshot upload)
    {
        lock (gate)
        {
            var delta = transferState.ApplyUpload(upload);
            if (delta is null) return;

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
        lock (gate) RemoveTerminalTransferLocked(transferId);
        PublishStateChanges();
    }

    // Persistence acknowledges exact revisions, not the transfer's current identity
    // alone. A late commit must not evict a newer terminal update or active attempt.
    internal IReadOnlyList<Guid> RetireCommittedTransfers(IReadOnlyList<(Guid TransferId, long Revision)> committed)
    {
        var retiredUploads = new List<Guid>();
        lock (gate)
        {
            RecordTerminalTransferCommit();
            foreach (var (id, revision) in committed)
            {
                var row = liveUploadTransfers.GetValueOrDefault(id) ?? activeTransfers.GetValueOrDefault(id);
                if (row is not { IsTerminal: true } || row.Revision > revision) continue;
                if (liveUploadTransfers.ContainsKey(id)) retiredUploads.Add(id);
                RemoveTerminalTransferLocked(id);
            }
        }
        PublishStateChanges();
        return retiredUploads;
    }

    private void RemoveTerminalTransferLocked(Guid transferId)
    {
        if (transferState.Remove(transferId) is not { } transfer) return;
        if (transfer.WorkflowId is { } workflowId && !daemonLiveWorkflowIds.Contains(workflowId)) return;
        AppendDaemonChanges(StateDelta.Empty with { RemovedTransferIds = [transferId] }, DateTimeOffset.UtcNow);
    }

    public void AttachEngine(DownloadEngine engine) => AttachEvents(engine.Events);

    internal void AttachEvents(DownloadEvents events)
    {
        events.JobRegistered += OnJobRegistered;
        events.JobResultCreated += OnJobResultCreated;
        events.JobStateChanged += OnJobStateChanged;
        events.JobStructureChanged += OnJobStructureChanged;
        events.JobDiscoveryChanged += OnJobDiscoveryChanged;
        events.JobExecutionCompleted += OnJobExecutionCompleted;
        events.WorkflowRetired += OnWorkflowRetired;
        events.SearchResultsAdded += OnSearchResultsAdded;
        events.SearchCompleted += OnSearchCompleted;
        events.DownloadStarted += OnNestedSongDownloadStarted;
        events.FallbackTransferStarted += OnFallbackTransferStarted;
        events.DownloadProgress += OnDownloadProgress;
        events.DownloadStateChanged += OnDownloadStateChanged;
        events.TransferAttemptStarted += OnTransferAttemptStarted;
        events.TransferAttemptCompleted += OnTransferAttemptCompleted;
        events.TransferAttemptFailed += OnTransferAttemptFailed;
        events.TransferAttemptCancelled += OnTransferAttemptCancelled;
        events.TransferCompleted += OnTransferCompleted;
        events.TransferOutputChanged += OnTransferOutputChanged;
        events.TransferFailed += OnTransferFailed;
        events.TransferCancelled += OnTransferCancelled;
    }

    public void DetachEngine(DownloadEngine engine)
    {
        DetachEvents(engine.Events);
        SetJobPersistence(null);
    }

    private void DetachEvents(DownloadEvents events)
    {
        events.JobRegistered -= OnJobRegistered;
        events.JobResultCreated -= OnJobResultCreated;
        events.JobStateChanged -= OnJobStateChanged;
        events.JobStructureChanged -= OnJobStructureChanged;
        events.JobDiscoveryChanged -= OnJobDiscoveryChanged;
        events.JobExecutionCompleted -= OnJobExecutionCompleted;
        events.WorkflowRetired -= OnWorkflowRetired;
        events.SearchResultsAdded -= OnSearchResultsAdded;
        events.SearchCompleted -= OnSearchCompleted;
        events.DownloadStarted -= OnNestedSongDownloadStarted;
        events.FallbackTransferStarted -= OnFallbackTransferStarted;
        events.DownloadProgress -= OnDownloadProgress;
        events.DownloadStateChanged -= OnDownloadStateChanged;
        events.TransferAttemptStarted -= OnTransferAttemptStarted;
        events.TransferAttemptCompleted -= OnTransferAttemptCompleted;
        events.TransferAttemptFailed -= OnTransferAttemptFailed;
        events.TransferAttemptCancelled -= OnTransferAttemptCancelled;
        events.TransferCompleted -= OnTransferCompleted;
        events.TransferOutputChanged -= OnTransferOutputChanged;
        events.TransferFailed -= OnTransferFailed;
        events.TransferCancelled -= OnTransferCancelled;
    }

    public JobSummaryDto? GetJobSummary(Guid jobId)
    {
        using (LockJobReads()) return jobState.GetJobSummary(jobId);
    }

    public IReadOnlyList<JobSummaryDto> GetJobSummaries(IReadOnlyCollection<Guid> jobIds)
    {
        using (LockJobReads()) return jobState.GetJobSummaries(jobIds);
    }

    public JobDetailDto? GetJobDetail(Guid jobId)
    {
        using (LockJobReads()) return jobState.GetJobDetail(jobId);
    }

    public JobDetailDto? GetJobDetailByDisplayId(Guid workflowId, int displayId)
    {
        using (LockJobReads()) return jobState.GetJobDetailByDisplayId(workflowId, displayId);
    }

    public DirectoryManifestPageDto? GetDirectoryManifest(Guid jobId, int? attemptNumber, int afterOrdinal, int limit)
    {
        using (LockJobReads()) return jobState.GetDirectoryManifest(jobId, attemptNumber, afterOrdinal, limit);
    }

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        using (LockJobReads()) return jobState.GetJobs(query);
    }

    internal JobPageCapture CaptureJobPage(JobQuery query, long? afterDisplayId, Guid? afterJobId, int take)
    {
        using (LockJobReads()) return jobState.CaptureJobPage(query, afterDisplayId, afterJobId, take,
            new(streamEpoch, daemonStreamSequence));
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        lock (gate) return jobState.GetWorkflows();
    }

    public IReadOnlyList<LiveWorkflowPageItem> GetWorkflowPageCandidates(long? afterFirstDisplayId, Guid? afterWorkflowId, int take)
    {
        lock (gate) return jobState.GetWorkflowPageCandidates(afterFirstDisplayId, afterWorkflowId, take);
    }

    public WorkflowSummaryDto? GetWorkflowSummary(Guid workflowId)
    {
        lock (gate) return jobState.GetWorkflowSummary(workflowId);
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId)
    {
        lock (gate) return jobState.GetWorkflow(workflowId);
    }

    /// <summary>Captures live state and its cursor under one ordering lock.</summary>
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
                jobState.PublishedWorkflows
                    .Where(workflow => workflowIds.Contains(workflow.Summary.WorkflowId))
                    .OrderBy(workflow => workflow.Summary.Title)
                    .ToList(),
                jobState.Published
                    .Where(job => workflowIds.Contains(job.WorkflowId)
                        && (detail == DaemonStateDetail.Full || job.ParentJobId is null))
                    .OrderBy(job => job.DisplayId)
                    .Select(job => job.ToDto()).ToList(),
                jobState.Searches
                    .Where(search => workflowIds.Contains(search.WorkflowId))
                    .OrderBy(search => search.JobId)
                    .ToList(),
                detail == DaemonStateDetail.Full ? activeTransfers.Values
                    .Where(transfer =>
                        transfer.WorkflowId is null
                        || workflowIds.Contains(transfer.WorkflowId.Value))
                    .OrderBy(transfer => transfer.TransferId)
                    .Select(transfer => transfer.ToDto()).ToList() : [], TransferSummary: transferState.Summary,
                JobHistoryPosition: new(streamEpoch, stateUpdates.LastJobSequence));
        }
    }

    internal (IReadOnlyList<JobReadState> Jobs, int? ChildCount, int? RetrievalChildCount, long Sequence)
        GetJobObservationSnapshot(Guid rootId, IReadOnlySet<Guid> requested)
    {
        using (LockJobReads()) return jobState.GetJobObservationSnapshot(rootId, requested);
    }

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
            var workflowRows = jobState.PublishedWorkflow(workflowId) is { } workflow ? new[] { workflow } : [];

            return new StateSnapshotDto(
                StateStreamScopeDto.Workflow(workflowId),
                new StateStreamPositionDto(
                    GetWorkflowSnapshotEpoch(workflowId),
                    workflowStreamSequences.GetValueOrDefault(workflowId)),
                DateTimeOffset.UtcNow,
                null,
                workflowRows,
                jobState.Published
                    .Where(job => job.WorkflowId == workflowId)
                    .OrderBy(job => job.DisplayId)
                    .Select(job => job.ToDto()).ToList(),
                jobState.Searches
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
            if (!jobState.HasWorkflow(workflowId))
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
        lock (gate) RecordTerminalTransferCommit();
        PublishStateChanges();
    }

    private void RecordTerminalTransferCommit()
    {
        daemonState = daemonState with
        {
            Revision = checked(daemonState.Revision + 1),
            TerminalTransferCommitRevision = checked(daemonState.TerminalTransferCommitRevision + 1),
        };
        AppendDaemonChanges(StateDelta.Empty with { Daemon = daemonState }, DateTimeOffset.UtcNow);
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
        lock (gate) return jobState.GetStatistics();
    }

    public void MarkActiveJobsInfrastructureFailed(string reason, string? detail = null)
    {
        JobStateUpdate update;
        lock (gate) ProjectJobUpdate(update = jobState.MarkActiveJobsInfrastructureFailed(reason, detail));
        PublishJobUpdate(update);
    }

    public void SetSourceJob(Guid jobId, Guid sourceJobId)
    {
        JobStateUpdate update;
        lock (gate) ProjectJobUpdate(update = jobState.SetSourceJob(jobId, sourceJobId));
        PublishJobUpdate(update);
    }

    internal void RegisterAcceptedRoot(JobSnapshot root, Guid? sourceJobId)
    {
        JobStateUpdate update;
        lock (gate) ProjectJobUpdate(update = jobState.RegisterAcceptedRoot(root, sourceJobId));
        PublishJobUpdate(update);
    }

    private void ApplyJobChange(CoreChange change)
    {
        JobStateUpdate update;
        lock (gate) ProjectJobUpdate(update = jobState.Apply(change));
        PublishJobUpdate(update);
    }

    private void ProjectJobUpdate(JobStateUpdate update, IReadOnlyList<TransferChange>? transfers = null)
    {
        if (update.IsEmpty && transfers is not { Count: > 0 }) return;
        ProjectStateChanges(update.Jobs, update.Workflows, update.Searches, transfers ?? [], [], update.OccurredAt);
    }

    private void PublishJobUpdate(JobStateUpdate update)
    {
        if (update.IsEmpty) return;
        PublishJobAndWorkflowUpserts(update.Jobs, update.Workflows);
        foreach (var search in update.Searches) SearchUpdated?.Invoke(search);
        PublishStateChanges();
    }

    private void OnJobRegistered(JobRegisteredChange change) => ApplyJobChange(change);

    private void OnJobResultCreated(JobResultCreatedChange change) => ApplyJobChange(change);

    private void OnJobStateChanged(JobStateChangedChange change) => ApplyJobChange(change);

    private void OnJobStructureChanged(JobStructureChangedChange change) => ApplyJobChange(change);

    private void OnJobDiscoveryChanged(JobDiscoveryChangedChange change) => ApplyJobChange(change);

    private void OnJobExecutionCompleted(JobExecutionCompletedChange change) => ApplyJobChange(change);

    private void OnSearchResultsAdded(SearchResultsAddedChange change)
    {
        // Raw chunks are independent of job-header revisions; none may be lost
        // because a later lifecycle or discovery header was already accepted.
        JobPersistence? writer;
        lock (gate) writer = jobState.Persistence;
        // SearchSession serializes result/completion publication. Its bounded
        // persistence admission may wait, but writer acknowledgements and unrelated
        // state reads must still be able to acquire our gate. Engine shutdown joins
        // publishers before detaching; retain this writer for an admitted callback.
        writer?.WriteResults(change);
    }

    private void OnSearchCompleted(SearchCompletedChange change)
    {
        JobPersistence? writer;
        lock (gate)
        {
            writer = jobState.Persistence;
            writer?.RecordSearchCompletion(change);
        }
        writer?.WriteSearchCompletion(change);
    }

    private void OnWorkflowRetired(WorkflowRetiredChange change)
    {
        JobPersistence? writer;
        lock (gate) writer = jobState.Persistence;
        writer?.RetireWorkflow(change.WorkflowId);
        if (persistenceOwnsJobRetirement) return;
        Guid[] jobIds;
        lock (gate)
            jobIds = jobState.WorkflowJobIds(change.WorkflowId);
        RetireJobProjections(change.WorkflowId, jobIds);
    }

    internal void RetireJobProjections(Guid workflowId, IReadOnlyCollection<Guid> retiredJobIds)
    {
        lock (gate)
        {
            // An acknowledgement owns one exact generation, not every record
            // currently using its workflow. A successor may already be running.
            bool wasDaemonLive = daemonLiveWorkflowIds.Contains(workflowId);
            var (jobIds, searchJobIds) = jobState.Retire(workflowId, retiredJobIds);
            if (jobIds.Count == 0) return;
            var transferIds = transferState.ForJobs(jobIds).Where(row => !row.IsTerminal)
                .Select(row => row.TransferId).ToHashSet();
            foreach (Guid transferId in transferIds) transferState.Remove(transferId);
            bool hasSuccessor = jobState.HasWorkflow(workflowId);
            WorkflowStateDto? successor = hasSuccessor
                ? jobState.ProjectWorkflow(jobState.GetWorkflowSummary(workflowId)!) : null;
            if (!hasSuccessor) SetDaemonWorkflowVisible(workflowId, false);
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

    private void ReleaseWorkflowStream(Guid workflowId, Guid epoch, long sequence)
    {
        lock (gate)
        {
            // Keep a captured generation until its last publication and reader
            // settle, including activity captured during retirement callbacks.
            if (jobState.HasWorkflow(workflowId)
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
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false, context: change);
            if (transferDelta is null) return;
            var fileSummary = jobState.UpdateFileProgress(change.Job, change.File, change.OccurredAtUtc, change.State);
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
        JobStateUpdate update;
        lock (gate)
        {
            var transfer = UpsertTransfer(change.Transfer, isTerminal: false, context: change);
            if (transfer is null) return;
            update = jobState.DownloadStarted(change);
            ProjectJobUpdate(update, [transfer]);
        }
        PublishJobUpdate(update);
    }

    private void OnFallbackTransferStarted(FallbackTransferStartedChange change)
    {
        lock (gate)
        {
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false, context: change);
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
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false, context: change);
            if (transferDelta is null) return;
            // Keep file state fresh for parent/detail reads without rebuilding
            // every containing album/directory on each transport callback.
            var fileSummary = jobState.UpdateFileProgress(change.Job, change.File, change.OccurredAtUtc);
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
        lock (gate) transferState.StartAttempt(change);
    }

    private void OnTransferAttemptCompleted(TransferAttemptCompletedChange change)
        => CompleteTransferAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Completed",
            "None",
            null);

    private void OnTransferAttemptFailed(TransferAttemptFailedChange change)
        => CompleteTransferAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Failed",
            Sockseek.Core.Events.TransferFailureReason.PeerFailure.ToString(),
            change.Exception.Message);

    private void OnTransferAttemptCancelled(TransferAttemptCancelledChange change)
        => CompleteTransferAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Cancelled",
            change.Reason.ToString(),
            null);

    private void CompleteTransferAttempt(TransferSnapshot transfer, Guid attemptId, int attemptNumber,
        long revision, DateTimeOffset occurredAt, string state, string failureReason, string? failureMessage)
    {
        lock (gate) transferState.CompleteAttempt(transfer, attemptId, attemptNumber,
            revision, occurredAt, state, failureReason, failureMessage);
    }

    private void OnTransferOutputChanged(TransferOutputChangedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc, context: change);

    private void OnTransferCompleted(TransferCompletedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc, context: change);

    private void OnTransferFailed(TransferFailedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc, change.Exception.Message, change);

    private void OnTransferCancelled(TransferCancelledChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc, context: change);

    private void OnTerminalTransfer(TransferSnapshot transfer, DateTimeOffset occurredAtUtc,
        string? failureMessage = null, CoreChange? context = null)
    {
        lock (gate)
        {
            var transferDelta = UpsertTransfer(transfer, isTerminal: true, occurredAtUtc, failureMessage, context);
            if (transferDelta is null) return;
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
            bool isLive = jobState.PublishedWorkflow(workflowId)?.Summary.State == ServerWorkflowState.Active;
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
            var projected = jobState.ProjectWorkflow(summary);
            if (projected != null) ForWorkflow(summary.WorkflowId)?.Add(projected);
        }
        HashSet<Guid>? seenJobs = jobSummaries.Count > 1 ? [] : null;
        for (int i = 0; i < jobSummaries.Count; i++)
        {
            var summary = jobSummaries[i];
            if (seenJobs != null && !seenJobs.Add(summary.JobId)) continue;
            var delta = jobState.ProjectJob(summary);
            if (delta != null) ForWorkflow(JobWorkflowId(delta))?.Add(delta);
        }
        for (int i = 0; i < searches.Count; i++)
        {
            var search = searches[i];
            if (jobState.ProjectSearch(search)) ForWorkflow(search.WorkflowId)?.Add(search);
        }
        for (int i = 0; i < transfers.Count; i++)
            ForWorkflow(TransferWorkflowId(transfers[i]))?.Add(transfers[i]);
        for (int i = 0; i < removedTransferIds.Count; i++)
        {
            Guid id = removedTransferIds[i];
            ForWorkflow(activeTransfers.GetValueOrDefault(id)?.WorkflowId ?? Guid.Empty)?.RemoveTransfer(id);
        }
        for (int i = 0; i < searches.Count; i++)
        {
            var search = searches[i];
            if (search.IsComplete && byWorkflow.TryGetValue(
                jobState.PublishedJob(search.JobId)?.WorkflowId ?? Guid.Empty, out var changes))
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
            foreach (var job in jobState.Published)
                if (terminalWorkflows.Contains(job.WorkflowId)) (daemon ??= BeginDaemonChange(occurredAtUtc)).RemoveJob(job.JobId);
            foreach (var search in jobState.Searches)
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
        transferState.SetWorkflowVisible(workflowId, visible);
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

    private TransferChange? UpsertTransfer(TransferSnapshot transfer, bool isTerminal,
        DateTimeOffset? completedAtUtc = null, string? failureMessage = null, CoreChange? context = null)
        => transferState.ApplyDownload(transfer, isTerminal, completedAtUtc, failureMessage, context);

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
        LiveTransferSummaryDto summary = transferState.Summary;
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

        return jobState.HasWorkflow(workflowId)
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
            ProjectJobUpdate(jobState.RefreshContainerSummaries());
            return held;
        }
        catch { held.Dispose(); throw; }
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

    private static Guid TransferWorkflowId(TransferChange delta) => delta.Current.WorkflowId ?? Guid.Empty;
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
