using Sockseek.Core.Services;
using System.Runtime.CompilerServices;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

public sealed record CombinedJobPage(
    IReadOnlyList<JobSummaryDto> Items, string? NextCursor, StateStreamPositionDto ReadPosition, long ReadSequence);
public sealed record CombinedSearchRawPage(IReadOnlyList<SearchRawResultDto> Items, long? NextSequence);
public sealed record CombinedWorkflowPage(IReadOnlyList<WorkflowSummaryDto> Items, string? NextCursor);
public sealed record CombinedSubmissionPage(IReadOnlyList<SubmissionSummaryDto> Items, string? NextCursor);

/// <summary>Canonical current/retained job, workflow and submission reads.</summary>
public sealed partial class JobReadService(EngineStateStore live, EngineSupervisor supervisor,
    PersistenceCoordinator persistence, ILogger<JobReadService>? logger = null)
{
    private readonly Sockseek.Core.Diagnostics.RepeatedWarningGate sourceReadWarnings = new();
    // A live job can be inspected repeatedly while its original search is in history.
    // Keep positive lookups only for that job's lifetime and until the next deletion.
    private sealed record RetainedSource(Guid Id, long RemovalVersion);
    private readonly ConditionalWeakTable<Job, RetainedSource> retainedSources = new();

    private async Task<bool> IsSourceJobAvailableAsync(Guid? sourceId, CancellationToken cancellationToken, Job? owner = null)
    {
        if (sourceId is not Guid id) return false;
        if (live.IsJobAvailable(id)) return true;
        long version = persistence.JobRemovalVersion;
        if (owner != null && retainedSources.TryGetValue(owner, out var known)
            && known.Id == id && known.RemovalVersion == version) return true;
        try
        {
            if (persistence.JobHistory is { } history)
            {
                await persistence.WaitForJobHandoffAsync(id, cancellationToken, includeSearchResults: false).ConfigureAwait(false);
                if (await history.ContainsJobAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    if (owner != null) retainedSources.AddOrUpdate(owner, new(id, version));
                    return true;
                }
            }
            return live.IsJobAvailable(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (logger != null && sourceReadWarnings.TryAcquire(out long suppressed))
                ServerLogMessages.SourceJobAvailabilityFailed(logger, ex, suppressed);
            return false;
        }
    }

    private async Task<JobDetailDto> WithSourceAvailabilityAsync(JobDetailDto detail, CancellationToken cancellationToken, Job? owner)
        => await WithRepeatOptionsAsync(detail with {
            SourceJobAvailable = await IsSourceJobAvailableAsync(detail.Summary.Relationships.SourceJobId, cancellationToken, owner).ConfigureAwait(false)
        }, cancellationToken);

    private async Task<JobDetailDto> WithRepeatOptionsAsync(JobDetailDto detail, CancellationToken ct)
    {
        var job = detail.Summary;
        if (job.Lifecycle.LifecycleState != JobLifecycleState.Terminal || job.Display.SubmissionId is not Guid submission ||
            persistence.JobHistory == null || persistence.Submissions == null) return detail;
        bool canRun = false, canExtract = false, canEdit = false;
        if (job.Display.Kind is not (ServerJobKind.RemoteFile or ServerJobKind.RemoteDirectory))
        {
            var json = await persistence.Submissions.GetSpecificationJsonAsync(submission, ct);
            var specification = json == null ? null : SubmissionSpecificationCodec.Deserialize(json);
            canRun = specification?.CanRunAgain(job.JobId) == true;
            if (canRun && specification!.ForRequest(job.JobId) is { } request)
            {
                canExtract = request.CanExtractAgain();
                canEdit = JobRequestMapper.CreateEditableDraft(request.Command) != null;
            }
        }
        int missing = 0;
        Guid? file = null;
        if (!canRun && job.Lifecycle.TerminalOutcome is not JobTerminalOutcome.Succeeded
            && job.Display.Kind is ServerJobKind.RemoteDirectory or ServerJobKind.Album or ServerJobKind.RemoteFile or ServerJobKind.Song)
        {
            try { await persistence.WaitForRetryHistoryAsync(live, job.JobId, ct); }
            catch (PersistenceHandoffException) { return detail; } // Live details survive loss of retry history.
            if (job.Display.Kind is ServerJobKind.RemoteDirectory or ServerJobKind.Album)
            {
                var key = new DirectoryRetryKey(job.JobId);
                var status = await persistence.JobHistory.GetDirectoryRetryStatusAsync([key], ct);
                missing = status.GetValueOrDefault(key)?.MissingFiles ?? 0;
            }
            else if (job.Display.Kind is ServerJobKind.RemoteFile or ServerJobKind.Song && persistence.TransferHistory != null)
            {
                var page = await persistence.TransferHistory.GetTransfersAsync(new(Limit: 1, JobId: job.JobId, Direction: "Download"), ct);
                if (page.Items.FirstOrDefault() is { CanRetry: true } candidate) file = candidate.Id;
            }
        }
        return detail with { RepeatOptions = new(canRun, missing, file, canExtract, canEdit) };
    }

    public async Task<DeleteCompletedJobsResponseDto> DeleteCompletedAsync(
        CompletedJobCategory category, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentException("Unknown job category.");
        if (persistence.Submissions is not { } submissions)
            throw new NotSupportedException("Submission deletion requires persistence.");
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var candidates = await submissions.GetCompletedIdsAsync(category == CompletedJobCategory.Searches, cancellationToken).ConfigureAwait(false);
        var active = live.GetJobs(new JobQuery(null, null, null, null, IncludeAll: true))
            .Where(job => job.Lifecycle.LifecycleState != JobLifecycleState.Terminal)
            .Select(job => job.Display.SubmissionId).OfType<Guid>().ToHashSet();
        active.UnionWith(supervisor.GetPendingSubmissionIds());
        var eligible = candidates.Where(id => !active.Contains(id)).ToArray();
        foreach (Guid id in eligible)
            await supervisor.WaitForSubmissionExecutionAsync(id, cancellationToken).ConfigureAwait(false);
        var results = await submissions.DeleteManyAsync(eligible, cancellationToken).ConfigureAwait(false);
        var deleted = results.Where(result => result.RejectedSubmissionCount == 0)
            .Select(result => result.SubmissionId).ToHashSet();
        if (deleted.Count > 0)
        {
            supervisor.ForgetSubmissions(deleted);
            live.RemoveSubmissions(deleted);
            persistence.NotifyJobsRemoved();
        }
        var receipt = new DeleteCompletedJobsResponseDto(results.Sum(result => result.AffectedSubmissionCount),
            results.Sum(result => result.AffectedJobCount), candidates.Count - eligible.Length + results.Sum(result => result.RejectedSubmissionCount));
        if (logger != null)
            ServerLogMessages.CompletedJobsDeleted(logger, Guid.NewGuid(), category,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                receipt.DeletedSubmissions, receipt.DeletedJobs, receipt.SkippedSubmissions);
        return receipt;
    }

    public async Task<DeleteSubmissionResponseDto> DeleteSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        var liveJobs = live.GetJobs(new JobQuery(
            null, null, null, null, IncludeAll: true, SubmissionId: submissionId));
        if (supervisor.GetPendingSubmissionIds().Contains(submissionId)
            || liveJobs.Any(job => job.Lifecycle.LifecycleState != JobLifecycleState.Terminal))
            return new(submissionId, 0, 0, 1, [new("nonterminal-jobs", 1)]);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        await supervisor.WaitForSubmissionExecutionAsync(submissionId, cancellationToken).ConfigureAwait(false);
        if (supervisor.ReleaseTransientResults(submissionId, out int released))
            return new(submissionId, 1, released, 0, []);
        if (persistence.Submissions == null)
            throw new NotSupportedException("Submission deletion requires persistence.");
        var result = await persistence.Submissions.DeleteAsync(submissionId, cancellationToken).ConfigureAwait(false);
        if (result.RejectedSubmissionCount == 0)
        {
            supervisor.ForgetSubmissions(new HashSet<Guid> { submissionId });
            live.RemoveSubmissions([submissionId]);
            if (result.AffectedJobCount > 0) persistence.NotifyJobsRemoved();
        }
        if (logger != null && result.AffectedSubmissionCount > 0)
            ServerLogMessages.SubmissionDeleted(logger, submissionId,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, result.AffectedJobCount);
        return SubmissionDtoMapper.ToDeleteResponse(result);
    }

    public async Task<CombinedSubmissionPage> GetSubmissionsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (persistence.Submissions == null)
            throw new NotSupportedException("Submission history is unavailable because persistence is disabled or not started.");
        var liveJobs = live.GetJobs(new JobQuery(null, null, null, null, IncludeAll: true))
            .ToLookup(job => job.Display.SubmissionId);
        persistence.EnsureJobProjectionsAvailable();
        var page = await persistence.Submissions.GetSubmissionsAsync(
            new SubmissionHistoryQuery(cursor, limit),
            cancellationToken).ConfigureAwait(false);
        return new CombinedSubmissionPage(
            page.Items.Select(item => WithLiveSubmissionCounts(
                SubmissionDtoMapper.ToSummary(item),
                liveJobs[item.Id].ToArray())).ToArray(),
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
            UserRootJobCount = jobs.Count(job => job.Display.Role == ServerJobRole.UserRoot),
            ActiveJobCount = jobs.Count(job => job.Lifecycle.LifecycleState != JobLifecycleState.Terminal),
            FailedJobCount = jobs.Count(job => job.Lifecycle.TerminalOutcome == JobTerminalOutcome.Failed),
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
            return await WithSourceAvailabilityAsync(liveDetail, cancellationToken, supervisor.GetRuntimeJob<Job>(liveDetail.Summary.JobId)).ConfigureAwait(false);
        await persistence.WaitForWorkflowHandoffAsync(workflowId, cancellationToken, includeSearchResults: false)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;
        var job = await persistence.JobHistory.GetJobByDisplayIdAsync(workflowId, displayId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        int childCount = await persistence.JobHistory.GetChildCountAsync(job.Id, cancellationToken).ConfigureAwait(false);
        return await WithRepeatOptionsAsync(HistoricalJobDtoMapper.ToDetail(job, childCount), cancellationToken);
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
                ? JobHistoryReader.EncodeCursor(items[^1].Display.DisplayId, items[^1].JobId, query.NewestFirst) : null,
                capture.Position, capture.ReadSequence);
        }
        persistence.EnsureJobProjectionsAvailable(query.WorkflowId);
        var persisted = await persistence.JobHistory.GetJobsAsync(new JobHistoryQuery(
            cursor, limit, query.LifecycleState?.ToString(), query.TerminalOutcome?.ToString(),
            query.SkipReason?.ToString(), query.Kind?.ToString(), query.WorkflowId, query.IncludeAll,
            query.ParentJobId, query.SubmissionId, query.Role?.ToString(),
            query.NewestFirst, query.ExcludeExtractResults, capture.OwnedJobIds, query.ExcludeKinds?.Select(kind => kind.ToString()).ToArray()), cancellationToken).ConfigureAwait(false);
        // Runtime rows and retained rows are disjoint by ownership. Capture and
        // exact-ID reads use this same runtime-first rule, rather than choosing
        // different authorities for detail, status filtering and list paging.
        var merged = persisted.Items.Select(HistoricalJobDtoMapper.ToSummary).Concat(capture.Items);
        var ordered = (query.NewestFirst
                ? merged.OrderByDescending(row => row.Display.DisplayId).ThenByDescending(row => row.JobId)
                : merged.OrderBy(row => row.Display.DisplayId).ThenBy(row => row.JobId))
            .Take(limit == int.MaxValue ? limit : limit + 1).ToArray();
        bool hasMore = ordered.Length > limit || persisted.NextCursor is not null || capture.Items.Count > limit;
        var pageItems = ordered.Take(limit).ToArray();
        return new(pageItems, hasMore && pageItems.Length > 0
            ? JobHistoryReader.EncodeCursor(pageItems[^1].Display.DisplayId, pageItems[^1].JobId, query.NewestFirst) : null,
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

    public async Task<string?> GetFailureDetailAsync(Guid jobId, CancellationToken ct)
    {
        if (live.TryGetJobFailureDetail(jobId, out var detail)) return detail;
        await persistence.WaitForJobHandoffAsync(jobId, ct, includeSearchResults: false);
        return persistence.JobHistory is { } history ? await history.GetFailureDetailAsync(jobId, ct) : null;
    }

    /// <summary>The query and settings a download job searched with, to run as an ordinary search.</summary>
    public async Task<JobSearchDraftDto?> GetSearchDraftAsync(Guid jobId, CancellationToken ct)
    {
        JobSnapshotPayload? payload = live.GetJobPayload(jobId);
        if (payload == null)
        {
            await persistence.WaitForJobHandoffAsync(jobId, ct, includeSearchResults: false).ConfigureAwait(false);
            if (persistence.JobHistory is { } history && await history.GetJobAsync(jobId, ct).ConfigureAwait(false) is { } job)
                payload = RetainedJobPayloadCodec.Read(job);
        }
        SearchDefinition? definition = RetainedJobPayloadCodec.Definition(payload);
        if (definition == null || definition.FileQuery == null && definition.AlbumQuery == null)
            return null;
        return new(
            definition.AlbumQuery == null ? ServerSnapshotMapper.ToSongQueryDto(definition.FileQuery!) : null,
            definition.AlbumQuery == null ? null : ServerSnapshotMapper.ToAlbumQueryDto(definition.AlbumQuery),
            EffectiveSettingsMapper.ToSearchSettings(definition.ProjectionSettings.ToSettings()));
    }

    public async Task<JobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var runtimeJob = supervisor.GetRuntimeJob<Job>(jobId);
        var liveDetail = live.GetJobDetail(jobId);
        if (liveDetail != null)
            return await WithSourceAvailabilityAsync(liveDetail, cancellationToken, runtimeJob).ConfigureAwait(false);
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken, includeSearchResults: false)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;

        var job = await persistence.JobHistory.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        int childCount = await persistence.JobHistory.GetChildCountAsync(jobId, cancellationToken).ConfigureAwait(false);
        return await WithRepeatOptionsAsync(HistoricalJobDtoMapper.ToDetail(job, childCount), cancellationToken);
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
                var file = child?.Payload switch
                {
                    SongJobSnapshotPayload song => ServerSnapshotMapper.ToFileDownloadStateDto(song),
                    RemoteFileJobSnapshotPayload remote => ServerSnapshotMapper.ToFileDownloadStateDto(remote.File),
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
        var items = session?.RawSnapshot(afterSequence, limit == int.MaxValue ? limit : limit + 1)
            .Select(ServerSnapshotMapper.ToSearchRawResultDto).ToList();
        if (items != null)
        {
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
