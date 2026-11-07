using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;

namespace Sockseek.Server;

internal readonly record struct JobStateUpdate(
    IReadOnlyList<JobReadState> Jobs, IReadOnlyList<WorkflowSummaryDto> Workflows,
    IReadOnlyList<SearchStateDto> Searches, DateTimeOffset OccurredAt)
{
    public static JobStateUpdate Empty => new([], [], [], default);
    public bool IsEmpty => Jobs.Count == 0 && Workflows.Count == 0 && Searches.Count == 0;
}

internal sealed record JobPageCapture(
    IReadOnlyList<JobSummaryDto> Items, IReadOnlyList<Guid> OwnedJobIds,
    bool IsCompleteScope, StateStreamPositionDto Position, long ReadSequence);

// Owns job/workflow facts, relationships, membership and derived counters together.
// EngineStateStore serializes calls with its existing gate, including transfer-driven
// updates and stream capture. This owner has no stream scopes, queues or lifecycle.
internal sealed class JobState
{
    internal JobPersistence? Persistence { get; set; }
    internal event Action<JobReadChange>? ReadChanged;
    internal event Action? ListChanged;
    internal IEnumerable<JobReadState> Published => PublishedJobs;
    internal IEnumerable<WorkflowStateDto> PublishedWorkflows => projectedWorkflows.Values;
    internal IEnumerable<SearchStateDto> Searches => searchStates.Values;
    internal bool HasWorkflow(Guid id) => workflows.ContainsKey(id);
    internal WorkflowStateDto? PublishedWorkflow(Guid id) => projectedWorkflows.GetValueOrDefault(id);
    internal JobReadState? PublishedJob(Guid id) => records.GetValueOrDefault(id)?.Published;
    internal Guid[] WorkflowJobIds(Guid id) => CurrentJobs.Where(row => row.WorkflowId == id).Select(row => row.Id).ToArray();

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
    private readonly HashSet<Guid> executionCompletedJobs = [];
    private readonly Dictionary<Guid, string> songTransferStates = [];
    private readonly Dictionary<Guid, SearchStateDto> searchStates = [];

    private readonly Dictionary<Guid, WorkflowStateDto> projectedWorkflows = [];

    private long jobReadSequence;

    internal JobChange? ProjectJob(JobReadState fact)
    {
        long coreRevision = jobs.GetValueOrDefault(fact.JobId)?.Revision ?? 0;
        var entry = records[fact.JobId];
        var previous = entry.Published;
        JobFields fields = previous?.Difference(fact) ?? JobFields.All;
        if (fields == JobFields.None) return null;
        var current = fact.WithRevision(Math.Max(coreRevision, (previous?.Revision ?? 0) + 1));
        entry.Published = current;
        if (ReadChanged is { } changed)
            changed(new(++jobReadSequence, current.JobId, current));
        return new(current, Added: previous is null, fields);
    }

    internal WorkflowStateDto? ProjectWorkflow(WorkflowSummaryDto summary)
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

    internal bool ProjectSearch(SearchStateDto search)
    {
        var current = searchStates.GetValueOrDefault(search.JobId);
        bool changed = current is null || search.Revision > current.Revision
            || search.ResultCount != current.ResultCount || search.IsComplete != current.IsComplete;
        if (search.IsComplete) searchStates.Remove(search.JobId);
        else searchStates[search.JobId] = search;
        return changed;
    }

    internal JobReadState? UpdateFileProgress(JobReferenceSnapshot reference, FileDownloadStateSnapshot file,
        DateTimeOffset occurredAtUtc, string? transferState = null)
    {
        if (transferState is not null) songTransferStates[reference.Id] = transferState;
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

    internal JobStateUpdate RefreshContainerSummaries()
    {
        if (changedContainerSummaries.Count == 0) return JobStateUpdate.Empty;
        var summaries = new List<JobReadState>(changedContainerSummaries.Count);
        foreach (Guid id in changedContainerSummaries)
            if (jobs.TryGetValue(id, out var job))
                summaries.Add(directoryProjections.TryGetValue(id, out var directory)
                    ? UpdateDirectorySummary(id, directory) : UpdateJobRecord(job));
        changedContainerSummaries.Clear();
        return new(summaries, [], [], containerSummaryChangedAt);
    }

    internal JobStateUpdate MarkActiveJobsInfrastructureFailed(string reason, string? detail = null)
    {
        List<JobReadState> changedJobs;
        List<WorkflowSummaryDto> changedWorkflows;
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
        return new(changedJobs, changedWorkflows, [], DateTimeOffset.UtcNow);
    }

    internal JobStateUpdate SetSourceJob(Guid jobId, Guid sourceJobId)
    {
        sourceJobIds[jobId] = sourceJobId;
        return jobs.TryGetValue(jobId, out var job)
            ? new([UpdateJobRecord(job)], [], [], DateTimeOffset.UtcNow) : JobStateUpdate.Empty;
    }

    internal JobStateUpdate Apply(CoreChange change) => change switch
    {
        JobRegisteredChange value => RegisterJob(value.Job, value.ParentJobId, value.SourceJobId, value.OccurredAtUtc, change: value),
        JobStateChangedChange value => OnJobChanged(value.Job, value),
        JobStructureChangedChange value => OnJobChanged(value.Job, value),
        JobDiscoveryChangedChange value => OnJobChanged(value.Job, value),
        JobExecutionCompletedChange value => OnJobExecutionCompleted(value),
        JobResultCreatedChange value => OnJobResultCreated(value),
        _ => throw new ArgumentException($"Not a job-state change: {change.GetType().Name}.", nameof(change)),
    };

    internal JobStateUpdate RegisterAcceptedRoot(JobSnapshot root, Guid? sourceJobId)
        => RegisterJob(root, null, sourceJobId, DateTimeOffset.UtcNow, acceptedRoot: true);

    internal JobStateUpdate DownloadStarted(DownloadStartedChange change)
    {
        var job = StoreJob(change.Job);
        MarkContainingSummariesChanged(job.Id, change.OccurredAtUtc);
        return new([UpdateJobRecord(job)], [BuildWorkflowSummary(job.WorkflowId)], [], change.OccurredAtUtc);
    }

    internal (HashSet<Guid> Jobs, Guid[] Searches) Retire(Guid workflowId, IReadOnlyCollection<Guid> retiredJobIds)
    {
        var jobIds = retiredJobIds.Where(id => records.TryGetValue(id, out var record)
            && record.Current.WorkflowId == workflowId).ToHashSet();
        if (jobIds.Count == 0) return (jobIds, []);
        var searchJobIds = searchStates.Keys.Where(jobIds.Contains).ToArray();
        foreach (Guid id in jobIds.Select(id => records[id].Current.SubmissionId).OfType<Guid>().Distinct())
            MarkSubmissionRetained(id);
        // Preserve final membership before removing any child index.
        foreach (Guid jobId in jobIds) PublishJobMembership(jobId);
        foreach (Guid jobId in jobIds)
        {
            if (parentJobIds.GetValueOrDefault(jobId) is Guid parent && !jobIds.Contains(parent)
                && records.TryGetValue(parent, out var parentEntry))
                parentEntry.ChildrenCoverage = MembershipCoverage.Retained;
            RemoveWorkflowRecord(records[jobId].Current);
            RemoveJobState(jobId);
        }
        if (!workflows.ContainsKey(workflowId)) projectedWorkflows.Remove(workflowId);
        return (jobIds, searchJobIds);
    }

    public bool IsJobAvailable(Guid jobId)
        => records.ContainsKey(jobId);

    public JobSummaryDto? GetJobSummary(Guid jobId)
    {
        return records.TryGetValue(jobId, out var record)
            ? record.Current.Summary
            : null;
    }

    public IReadOnlyList<JobSummaryDto> GetJobSummaries(IReadOnlyCollection<Guid> jobIds)
    {
        return jobIds.Distinct().Where(records.ContainsKey).Select(id => records[id].Current.Summary).ToArray();
    }

    public JobDetailDto? GetJobDetail(Guid jobId)
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

    public JobDetailDto? GetJobDetailByDisplayId(Guid workflowId, int displayId)
    {
        var record = CurrentJobs.FirstOrDefault(row => row.WorkflowId == workflowId
            && row.DisplayId == displayId);
        return record == null ? null : GetJobDetail(record.Id);
    }

    public DirectoryManifestPageDto? GetDirectoryManifest(
        Guid jobId, int? attemptNumber, int afterOrdinal, int limit)
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

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        var rows = FilterJobs(CurrentJobs, query);
        return (query.NewestFirst
                ? rows.OrderByDescending(record => record.DisplayId).ThenByDescending(record => record.Id)
                : rows.OrderBy(record => record.DisplayId).ThenBy(record => record.Id))
            .Select(record => record.Summary)
            .ToList();
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        return workflows.Values
            .OrderBy(workflow => workflow.WorkflowId)
            .Select(workflow => workflow.ToSummary(records))
            .ToList();
    }

    public IReadOnlyList<LiveWorkflowPageItem> GetWorkflowPageCandidates(
        long? afterFirstDisplayId,
        Guid? afterWorkflowId,
        int take)
    {
        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take));

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

    public WorkflowSummaryDto? GetWorkflowSummary(Guid workflowId)
    {
        return workflows.TryGetValue(workflowId, out var workflow)
            ? workflow.ToSummary(records)
            : null;
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId)
    {
        if (!workflows.TryGetValue(workflowId, out var workflow))
            return null;

        return new WorkflowDetailDto(workflow.ToSummary(records));
    }

    public ServerStatusDto GetStatistics()
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

    internal void NotifyHistoryChanged() => ListChanged?.Invoke();

    internal JobPageCapture CaptureJobPage(JobQuery query, long? afterDisplayId, Guid? afterJobId, int take, StateStreamPositionDto position)
    {
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
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
            position, jobReadSequence);
    }

    internal (IReadOnlyList<JobReadState> Jobs, int? ChildCount, int? RetrievalChildCount, long Sequence)
        GetJobObservationSnapshot(Guid rootId, IReadOnlySet<Guid> requested)
    {
        var rows = requested.Select(id => records.GetValueOrDefault(id)?.Published)
            .OfType<JobReadState>().ToArray();
        bool rootPresent = records.ContainsKey(rootId);
        return (rows,
            rootPresent ? childJobIdsByParent.GetValueOrDefault(rootId)?.Count ?? 0 : null,
            rootPresent ? retrievalChildCountsByParent.GetValueOrDefault(rootId) : null,
            jobReadSequence);
    }

    internal EngineStateStoreRetainedWorkflowCounts RetainedCounts
    {
        get
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
                0,
                0,
                searchStates.Count,
                records.Values.Count(entry => entry.Published is not null),
                projectedWorkflows.Count,
                0,
                0,
                0,
                0,
                listProjections.Count,
                extractJobIdsByResult.Count,
                aggregateProjections.Count,
                albumAggregateProjections.Count);
        }
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
        return filtered.Where(record => MatchesQuery(record, query));
    }

    // HTTP pages and observed membership use the same owner/index semantics.
    internal bool MatchesQuery(JobReadState record, JobQuery query)
        => (!query.ExcludeExtractResults
            || !(record.SourceJobId is Guid sourceId && IsCurrentExtract(sourceId, record.WorkflowId))
                && !(extractJobIdsByResult.TryGetValue(record.Id, out Guid extractId)
                    && IsCurrentExtract(extractId, record.WorkflowId)))
        && (query.WorkflowId is null || record.WorkflowId == query.WorkflowId)
        && (query.Kind is null || record.Kind == query.Kind)
        && (query.ExcludeKinds is null || !query.ExcludeKinds.Contains(record.Kind))
        && (query.LifecycleState is null || record.LifecycleState == query.LifecycleState)
        && (query.TerminalOutcome is null || record.TerminalOutcome == query.TerminalOutcome)
        && (query.SkipReason is null || record.SkipReason == query.SkipReason)
        && (query.SubmissionId is null || record.SubmissionId == query.SubmissionId)
        && (query.Role is null || record.Role == query.Role)
        && (query.ParentJobId is null || record.ParentJobId == query.ParentJobId)
        && (query.ParentJobId.HasValue || query.IncludeAll || IsDefaultRoot(record));

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
        if (membership.JobIds.Count == 0)
            submissions.Remove(submissionId);
    }

    private void PublishJobMembership(Guid? jobId)
    {
        if (jobId is not Guid id || ReadChanged is not { } changed || !records.ContainsKey(id)) return;
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
        public bool Registered { get; set; }
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

    private void MarkDirectorySummaryChanged(Guid id, DirectoryProjection directory, DateTimeOffset occurredAtUtc)
    {
        if (!directory.HasSummaryChanges) return;
        changedContainerSummaries.Add(id);
        containerSummaryChangedAt = occurredAtUtc;
    }

    private bool IsOlderJob(JobSnapshotHeader snapshot)
        => jobs.TryGetValue(snapshot.Id, out var current) && current.Revision > snapshot.Revision;

    private void PersistJob(JobSnapshotHeader snapshot, CoreChange change, PersistenceMutationPriority priority)
        => Persistence?.WriteJob(snapshot, change,
            snapshot.LifecycleState == JobLifecycleState.Terminal ? PersistenceMutationPriority.Terminal : priority,
            parentJobIds.GetValueOrDefault(snapshot.Id), sourceJobIds.TryGetValue(snapshot.Id, out var source) ? source : null,
            resultJobIds.TryGetValue(snapshot.Id, out var result) ? result : null);

    private JobStateUpdate RegisterJob(JobSnapshot job, Guid? parentJobId, Guid? sourceJobId, DateTimeOffset occurredAtUtc, bool acceptedRoot = false, JobRegisteredChange? change = null)
    {
        // Registration supplies relationships independently of lifecycle
        // headers. A late first registration must not lose those links or
        // replace a newer execution snapshot with its initial state.
        if (IsOlderJob(job) && records.GetValueOrDefault(job.Id)?.Registered == true) return JobStateUpdate.Empty;
        job = StoreJob(job);
        parentJobIds[job.Id] = parentJobId;
        if (sourceJobId is Guid sourceId)
            sourceJobIds[job.Id] = sourceId;
        if (change is not null) PersistJob(job, change, PersistenceMutationPriority.Structural);
        List<JobReadState> summaries = [UpdateJobRecord(job)];
        var entry = records[job.Id];
        if (change is not null) entry.Registered = true;
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
        var workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        return new(summaries, [workflowSummary], [], occurredAtUtc);
    }

    private JobStateUpdate OnJobResultCreated(JobResultCreatedChange change)
    {
        List<JobReadState> changedJobs = [];
        WorkflowSummaryDto? workflowSummary = null;
        bool acceptExtract = !IsOlderJob(change.ExtractJob);
        bool acceptResult = !IsOlderJob(change.ResultJob);
        var extract = StoreJob(change.ExtractJob);
        if (acceptExtract)
        {
            if (resultJobIds.TryGetValue(extract.Id, out Guid previousResultId))
                extractJobIdsByResult.Remove(previousResultId);
            resultJobIds[extract.Id] = change.ResultJob.Id;
            extractJobIdsByResult[change.ResultJob.Id] = extract.Id;
        }
        changedJobs.Add(UpdateJobRecord(extract));

        if (jobs.ContainsKey(change.ResultJob.Id))
        {
            var result = StoreJob(change.ResultJob);
            changedJobs.Add(UpdateJobRecord(result));
        }
        else if (extract.Payload is ExtractJobSnapshotPayload { AutoProcessResult: true })
        {
            var result = StoreJob(change.ResultJob);
            parentJobIds[result.Id] = parentJobIds.GetValueOrDefault(extract.Id);
            changedJobs.Add(UpdateJobRecord(result));
        }
        else
        {
            // Unprocessed results remain available through retained history.
            MarkSubmissionRetained(extract.SubmissionId);
        }

        // Admit the result before its referring extract so a writer batch
        // boundary cannot persist a forward reference alone.
        if (acceptResult) PersistJob(change.ResultJob, change, PersistenceMutationPriority.Structural);
        if (acceptExtract) PersistJob(extract, change, PersistenceMutationPriority.Structural);

        MarkContainingSummariesChanged(change.ExtractJob.Id, change.OccurredAtUtc);

        if (workflows.ContainsKey(change.ExtractJob.WorkflowId))
            workflowSummary = BuildWorkflowSummary(change.ExtractJob.WorkflowId);

        changedJobs = changedJobs.DistinctBy(summary => summary.JobId).ToList();
        return new(changedJobs, workflowSummary != null ? [workflowSummary] : [], [], change.OccurredAtUtc);
    }

    private JobStateUpdate OnJobChanged(JobSnapshotHeader snapshot, CoreChange change)
    {
        if (IsOlderJob(snapshot)) return JobStateUpdate.Empty;
        bool lifecycleChanged = change is not JobDiscoveryChangedChange;
        if (lifecycleChanged && ServerSnapshotMapper.IsRunningOrPending(snapshot))
            executionCompletedJobs.Remove(snapshot.Id);

        var job = StoreJob(snapshot);
        PersistJob(snapshot, change, change is JobStructureChangedChange
            ? PersistenceMutationPriority.Structural : PersistenceMutationPriority.Ordinary);
        MarkContainingSummariesChanged(job.Id, change.OccurredAtUtc);
        var summary = UpdateJobRecord(job);
        var search = ToSearchState(job);
        return new([summary], lifecycleChanged ? [BuildWorkflowSummary(job.WorkflowId)] : [],
            search is null ? [] : [search], change.OccurredAtUtc);
    }

    private JobStateUpdate OnJobExecutionCompleted(JobExecutionCompletedChange change)
    {
        if (IsOlderJob(change.Job)) return JobStateUpdate.Empty;
        var job = StoreJob(change.Job);
        executionCompletedJobs.Add(change.Job.Id);
        var summary = UpdateJobRecord(job);
        var workflowSummary = BuildWorkflowSummary(change.Job.WorkflowId);
        return new([summary], [workflowSummary], [], change.OccurredAtUtc);
    }

}
