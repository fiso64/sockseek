using Sockseek.Api;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

public sealed record CombinedJobPage(
    IReadOnlyList<JobSummaryDto> Items, string? NextCursor, StateStreamPositionDto ReadPosition, long ReadSequence);
public sealed record CombinedSearchRawPage(IReadOnlyList<SearchRawResultDto> Items, long? NextSequence);
public sealed record CombinedWorkflowPage(IReadOnlyList<WorkflowSummaryDto> Items, string? NextCursor);
public sealed record CombinedSubmissionPage(IReadOnlyList<SubmissionSummaryDto> Items, string? NextCursor);

/// <summary>Canonical current/retained job, workflow and submission reads.</summary>
public sealed partial class JobReadService(EngineStateStore live, EngineSupervisor supervisor,
    PersistenceCoordinator persistence)
{
    public async Task<SubmissionArchiveResponseDto> SetSubmissionArchivedAsync(
        Guid submissionId,
        bool archived,
        CancellationToken cancellationToken)
    {
        if (persistence.Submissions == null)
            throw new NotSupportedException("Submission archive is unavailable because persistence is disabled or not started.");
        var liveJobs = live.GetJobs(new JobQuery(
            null, null, null, null, IncludeAll: true, SubmissionId: submissionId));
        if (archived)
        {
            bool pending = supervisor.HasPendingSubmission(submissionId);
            bool liveActive = liveJobs.Any(job => job.LifecycleState != ServerJobLifecycleState.Terminal);
            if (pending || liveActive)
            {
                return new SubmissionArchiveResponseDto(
                    submissionId,
                    true,
                    0,
                    0,
                    1,
                    [new SubmissionReasonCountDto("nonterminal-jobs", 1)]);
            }
        }
        // Archive needs this submission's terminal metadata, not another
        // workflow's search history. Pending projections remain in StateStore
        // until their durable acknowledgement.
        foreach (var job in liveJobs)
            await persistence.WaitForJobHandoffAsync(job.JobId, cancellationToken, includeSearchResults: false)
                .ConfigureAwait(false);
        var result = await persistence.Submissions
            .SetArchivedAsync(submissionId, archived, cancellationToken)
            .ConfigureAwait(false);
        if (result.RejectedSubmissionCount == 0)
            live.SetSubmissionArchived(submissionId, archived);
        return SubmissionDtoMapper.ToArchiveResponse(result);
    }

    public async Task<CombinedSubmissionPage> GetSubmissionsAsync(
        string? cursor,
        int limit,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        if (persistence.Submissions == null)
            throw new NotSupportedException("Submission history is unavailable because persistence is disabled or not started.");
        var liveJobs = live.GetJobs(new JobQuery(
            null,
            null,
            null,
            null,
            IncludeAll: true,
            Archived: archived));
        persistence.EnsureJobProjectionsAvailable();
        var page = await persistence.Submissions.GetSubmissionsAsync(
            new SubmissionHistoryQuery(cursor, limit, archived),
            cancellationToken).ConfigureAwait(false);
        return new CombinedSubmissionPage(
            page.Items.Select(item => WithLiveSubmissionCounts(
                SubmissionDtoMapper.ToSummary(item),
                liveJobs.Where(job => job.SubmissionId == item.Id).ToArray())).ToArray(),
            page.NextCursor);
    }

    public async Task<SubmissionDetailDto?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        if (persistence.Submissions == null)
            throw new NotSupportedException("Submission history is unavailable because persistence is disabled or not started.");
        var liveJobs = live.GetJobs(new JobQuery(
            null,
            null,
            null,
            null,
            IncludeAll: true,
            SubmissionId: submissionId));
        persistence.EnsureJobProjectionsAvailable();
        var submission = await persistence.Submissions
            .GetSubmissionAsync(submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (submission == null)
            return null;
        var detail = SubmissionDtoMapper.ToDetail(submission);
        return detail with { Summary = WithLiveSubmissionCounts(detail.Summary, liveJobs) };
    }

    private static SubmissionSummaryDto WithLiveSubmissionCounts(
        SubmissionSummaryDto summary,
        IReadOnlyList<JobSummaryDto> jobs)
    {
        if (jobs.Count == 0)
            return summary;
        return summary with
        {
            TotalJobCount = jobs.Count,
            UserRootJobCount = jobs.Count(job => job.Role == ServerJobRole.UserRoot),
            ActiveJobCount = jobs.Count(job => job.LifecycleState != ServerJobLifecycleState.Terminal),
            FailedJobCount = jobs.Count(job => job.TerminalOutcome == ServerJobTerminalOutcome.Failed),
        };
    }

    public async Task<CombinedWorkflowPage> GetWorkflowsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateJobPageLimit(limit);
        var decoded = JobHistoryReader.DecodeCursor(cursor);
        var liveRows = live.GetWorkflowPageCandidates(
            decoded?.DisplayId,
            decoded?.Id,
            limit == int.MaxValue ? limit : limit + 1);
        persistence.EnsureJobProjectionsAvailable();
        PersistedWorkflowPage? persisted = persistence.JobHistory == null
            ? null
            : await persistence.JobHistory.GetWorkflowsAsync(cursor, limit, cancellationToken).ConfigureAwait(false);

        var merged = new Dictionary<Guid, (long FirstDisplayId, WorkflowSummaryDto Summary)>();
        foreach (var workflow in persisted?.Items ?? [])
            merged[workflow.WorkflowId] = (workflow.FirstDisplayId, ToWorkflowSummary(workflow));
        foreach (var workflow in liveRows)
            merged[workflow.Summary.WorkflowId] = (workflow.FirstDisplayId, workflow.Summary);

        var ordered = merged.Values
            .OrderBy(workflow => workflow.Summary.WorkflowId)
            .ToList();
        bool hasMore = ordered.Count > limit
            || liveRows.Count > limit
            || persisted?.NextCursor != null;
        if (ordered.Count > limit)
            ordered.RemoveRange(limit, ordered.Count - limit);
        string? next = hasMore && ordered.Count > 0
            ? JobHistoryReader.EncodeCursor(
                0,
                ordered[^1].Summary.WorkflowId)
            : null;
        return new CombinedWorkflowPage(ordered.Select(item => item.Summary).ToArray(), next);
    }

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var liveWorkflow = live.GetWorkflow(workflowId);
        if (liveWorkflow != null)
            return liveWorkflow;
        await persistence.WaitForWorkflowHandoffAsync(workflowId, cancellationToken, includeSearchResults: false)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;
        var historical = await persistence.JobHistory.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return historical == null ? null : new WorkflowDetailDto(ToWorkflowSummary(historical));
    }

    public async Task<JobDetailDto?> GetJobByDisplayIdAsync(
        Guid workflowId,
        int displayId,
        CancellationToken cancellationToken = default)
    {
        var liveDetail = live.GetJobDetailByDisplayId(workflowId, displayId);
        if (liveDetail != null)
            return liveDetail;
        await persistence.WaitForWorkflowHandoffAsync(workflowId, cancellationToken, includeSearchResults: false)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;
        var job = await persistence.JobHistory.GetJobByDisplayIdAsync(workflowId, displayId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        int childCount = await persistence.JobHistory.GetChildCountAsync(job.Id, cancellationToken).ConfigureAwait(false);
        return HistoricalJobDtoMapper.ToDetail(job, childCount);
    }

    public async Task<CombinedJobPage> GetJobsAsync(
        JobQuery query,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateJobPageLimit(limit);
        var decoded = JobHistoryReader.DecodeCursor(cursor);
        if (decoded is { } position && position.NewestFirst != query.NewestFirst)
            throw new ArgumentException("The job cursor has a different sort direction.", nameof(cursor));
        var capture = live.CaptureJobPage(query, decoded?.DisplayId, decoded?.Id,
            limit == int.MaxValue ? limit : limit + 1);
        if (capture.IsCompleteScope || persistence.JobHistory is null)
        {
            bool more = capture.Items.Count > limit;
            var items = capture.Items.Take(limit).ToArray();
            return new(items, more && items.Length > 0
                ? JobHistoryReader.EncodeCursor(items[^1].DisplayId, items[^1].JobId, query.NewestFirst) : null,
                capture.Position, capture.ReadSequence);
        }
        persistence.EnsureJobProjectionsAvailable(query.WorkflowId);
        var persisted = await persistence.JobHistory.GetJobsAsync(new JobHistoryQuery(
            cursor, limit, query.LifecycleState?.ToString(), query.TerminalOutcome?.ToString(),
            query.SkipReason?.ToString(), query.Kind?.ToString(), query.WorkflowId, query.IncludeAll,
            query.ParentJobId, query.SubmissionId, query.Role?.ToString(), query.Archived,
            query.NewestFirst, query.ExcludeExtractResults, capture.OwnedJobIds, query.ExcludeKinds?.Select(kind => kind.ToString()).ToArray()), cancellationToken).ConfigureAwait(false);
        // Runtime rows and retained rows are disjoint by ownership. Capture and
        // exact-ID reads use this same runtime-first rule, rather than choosing
        // different authorities for detail, status filtering and list paging.
        var merged = persisted.Items.Select(HistoricalJobDtoMapper.ToSummary).Concat(capture.Items);
        var ordered = (query.NewestFirst
                ? merged.OrderByDescending(row => row.DisplayId).ThenByDescending(row => row.JobId)
                : merged.OrderBy(row => row.DisplayId).ThenBy(row => row.JobId))
            .Take(limit == int.MaxValue ? limit : limit + 1).ToArray();
        bool hasMore = ordered.Length > limit || persisted.NextCursor is not null || capture.Items.Count > limit;
        var pageItems = ordered.Take(limit).ToArray();
        return new(pageItems, hasMore && pageItems.Length > 0
            ? JobHistoryReader.EncodeCursor(pageItems[^1].DisplayId, pageItems[^1].JobId, query.NewestFirst) : null,
            capture.Position, capture.ReadSequence);
    }

    public async Task<IReadOnlyList<JobSummaryDto>> LookupJobsAsync(
        JobLookupRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.JobIds);
        Guid[] ids = request.JobIds.Distinct().ToArray();
        var found = live.GetJobSummaries(ids).ToDictionary(job => job.JobId);
        Guid[] missing = ids.Where(id => !found.ContainsKey(id)).ToArray();
        foreach (Guid id in missing)
            await persistence.WaitForJobHandoffAsync(id, cancellationToken, includeSearchResults: false).ConfigureAwait(false);
        if (missing.Length > 0 && persistence.JobHistory is { } history)
            foreach (var job in await history.GetJobsByIdsAsync(missing, cancellationToken).ConfigureAwait(false))
                found[job.Id] = HistoricalJobDtoMapper.ToSummary(job);
        return ids.Where(found.ContainsKey).Select(id => found[id]).ToArray();
    }

    public async Task<JobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var liveDetail = live.GetJobDetail(jobId);
        if (liveDetail != null)
            return liveDetail;
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken, includeSearchResults: false)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;

        var job = await persistence.JobHistory.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        int childCount = await persistence.JobHistory.GetChildCountAsync(jobId, cancellationToken).ConfigureAwait(false);
        return HistoricalJobDtoMapper.ToDetail(job, childCount);
    }

    public async Task<DirectoryManifestPageDto?> GetDirectoryManifestAsync(
        Guid jobId,
        int? attemptNumber,
        int afterOrdinal,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterOrdinal < -1 || afterOrdinal == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(afterOrdinal));
        if (attemptNumber is < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));

        var livePage = live.GetDirectoryManifest(jobId, attemptNumber, afterOrdinal, limit);
        if (livePage != null)
            return livePage;
        if (live.GetJobSummary(jobId) != null && attemptNumber == null)
            return null;
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken, includeSearchResults: false).ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;
        var retained = await persistence.JobHistory.GetDirectoryManifestAsync(
            jobId, attemptNumber, afterOrdinal, limit, cancellationToken).ConfigureAwait(false);
        if (retained == null)
            return null;
        return new DirectoryManifestPageDto(
            retained.AttemptNumber,
            retained.DisplayRoot,
            retained.EntryCount,
            retained.TotalKnownBytes,
            retained.ChildrenMaterialized,
            retained.SupplementalEntryCount,
            retained.JobRevision,
            retained.Items.Select(item =>
            {
                var child = item.Child == null ? null : HistoricalJobDtoMapper.ReadSnapshot(item.Child, tolerateInvalidPayload: true);
                var file = child == null ? null : HistoricalJobDtoMapper.ToPayload(child) switch
                {
                    SongJobPayloadDto song => song.File,
                    RemoteFileJobPayloadDto remote => remote.File,
                    _ => null,
                };
                return new DirectoryManifestEntryDto(
                    item.Ordinal,
                    item.Target != null ? ServerSnapshotMapper.ToPeerFileTargetDto(item.Target)
                        : child == null ? null : HistoricalJobDtoMapper.ToSelectedFileTarget(child),
                    item.RelativeDirectoryComponents,
                    item.ChildJobId,
                    item.Child == null ? null : HistoricalJobDtoMapper.ToSummary(item.Child, child!),
                    file);
            }).ToArray(),
            retained.NextOrdinal);
    }

    public async Task<CombinedSearchRawPage?> GetRawSearchResultsAsync(
        Guid jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        // Raw observations exist before settings preparation. Retirement owns
        // the final session until durable history acknowledges the complete source.
        var current = supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(jobId)?.SearchObservationSession;
        var session = persistence.GetRetiringSearchSource(jobId)?.Session ?? current;
        var liveResults = session?.RawSnapshot(afterSequence, limit == int.MaxValue ? limit : limit + 1)
            .Select(result => ServerSnapshotMapper.ToSearchRawResultDto(result.ProjectionInput)).ToList();
        if (liveResults != null)
        {
            var items = liveResults.ToList();
            bool hasMore = items.Count > limit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            return new CombinedSearchRawPage(items, hasMore && items.Count > 0 ? items[^1].Sequence : null);
        }
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (persistence.SearchHistory == null)
            return null;

        var page = await persistence.SearchHistory.GetRawResultsAsync(jobId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new CombinedSearchRawPage(
            page.Items.Select(ServerSnapshotMapper.ToSearchRawResultDto).ToArray(),
            page.NextSequence);
    }

    private static WorkflowSummaryDto ToWorkflowSummary(PersistedWorkflowSummary workflow)
        => new(
            workflow.WorkflowId,
            workflow.Title,
            Enum.TryParse(workflow.State, ignoreCase: true, out ServerWorkflowState state)
                ? state
                : ServerWorkflowState.Completed,
            workflow.RootJobCount,
            workflow.ActiveJobCount,
            workflow.FailedJobCount,
            workflow.CompletedJobCount);

    private static void ValidateJobPageLimit(int limit)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Page size must be positive.");
    }
}
