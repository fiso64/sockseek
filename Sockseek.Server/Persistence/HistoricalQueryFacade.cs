using Sockseek.Api;
using Sockseek.Core.Services;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Write;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Diagnostics;

namespace Sockseek.Server.Persistence;

public sealed record CombinedJobPage(
    IReadOnlyList<JobSummaryDto> Items, string? NextCursor, StateStreamPositionDto ReadPosition);
public sealed record CombinedSearchRawPage(IReadOnlyList<SearchRawResultDto> Items, long? NextSequence);
public sealed record CombinedTransferAttemptPage(IReadOnlyList<TransferAttemptHistoryDto> Items, int? NextAttemptNumber);
public sealed record CombinedWorkflowPage(IReadOnlyList<WorkflowSummaryDto> Items, string? NextCursor);
public sealed record CombinedSubmissionPage(IReadOnlyList<SubmissionSummaryDto> Items, string? NextCursor);

public sealed partial class HistoricalQueryFacade(EngineStateStore live, EngineSupervisor supervisor,
    PersistenceCoordinator persistence, ILogger<HistoricalQueryFacade>? logger = null)
{
    private readonly RepeatedWarningGate groupReadWarnings = new();
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

    public Task<TransferTimelinePageDto> LookupTransfersAsync(
        TransferLookupRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.TransferIds);
        string? direction = null;
        if (request.Direction is { } value)
        {
            if (!Enum.TryParse<TransferCommandDirection>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                throw new ArgumentException("Transfer direction must be Download or Upload.");
            direction = parsed.ToString();
        }
        var ids = request.TransferIds.ToHashSet();
        return GetTransfersAsync(null, Math.Max(1, ids.Count), null, null,
            direction, null, null, null, null, null, null,
            cancellationToken: cancellationToken, transferIds: ids);
    }

    public async Task<TransferTimelinePageDto> GetTransfersAsync(
        string? cursor,
        int limit,
        Guid? jobId,
        Guid? workflowId,
        string? direction,
        string? source,
        string? state,
        string? terminalOutcome,
        string? username,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        bool archived = false,
        CancellationToken cancellationToken = default,
        string? groupRef = null,
        IReadOnlySet<Guid>? transferIds = null)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
        TransferHistoryReader.TransferCursorValue? boundary =
            TransferHistoryReader.DecodeCursor(cursor);

        // Capture before either source: claiming a later commit could conceal
        // a completion in the gap between the HTTP read and live subscription.
        StateStreamPositionDto readPosition = live.GetReadPosition();
        var retainedCoverage = RetainedTransferCoverage();
        // Keep captured facts until membership is known. Wire metadata and
        // retained file attributes belong to the selected page, not every live row.
        var current = (archived ? [] : transferIds is not null
            ? live.GetTransferFactsSnapshot(transferIds)
            : live.GetTransferPageCandidates(groupRef, direction, username))
            .ToDictionary(row => row.TransferId);
        bool queueHasMore = false;
        if (!archived && groupRef is null && transferIds is null
            && CanIncludeQueuedUploads(direction, source, state, terminalOutcome)
            && supervisor?.Sharing is { } sharing)
        {
            var queued = sharing.Uploads.GetNewestQueuePage(
                boundary is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value.CreatedAtUtc) : null,
                boundary?.Id, limit == int.MaxValue ? limit : limit + 1, username, fromUtc, toUtc);
            queueHasMore = queued.NextTransferId != null;
            var queuedIds = queued.Items.Select(item => item.TransferId).ToHashSet();
            foreach (var row in live.GetTransferFactsSnapshot(queuedIds))
                if (!current.TryGetValue(row.TransferId, out var previous) || row.Revision > previous.Revision)
                    current[row.TransferId] = row;
        }
        var candidates = current.Values.Select(row => new LiveTransferCandidate(
            row.TransferId, row.Revision, row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0,
            MatchesTransferFilter(row, null, jobId, workflowId, direction, source, state,
                terminalOutcome, username, null, null, groupRef))).ToArray();
        if (persistence.TransferHistory is not null && retainedCoverage.State != TransferRetainedCoverageState.Unavailable)
        {
            var retained = await persistence.TransferHistory.GetTransfersAsync(
                new TransferHistoryQuery(cursor, limit, jobId, workflowId, direction, source, state,
                    terminalOutcome, username, fromUtc, toUtc, archived, groupRef, transferIds?.ToArray(),
                    candidates, queueHasMore), cancellationToken).ConfigureAwait(false);
            var rows = retained.Items.ToDictionary(row => row.Id, TransferReadModel.FromRetained);
            foreach (Guid id in retained.TransferIds)
                if (current.TryGetValue(id, out var captured))
                {
                    if (rows.TryGetValue(id, out var newer) && newer.Revision > captured.Revision)
                        continue;
                    var row = TransferReadModel.FromLive(captured.ToDto());
                    rows[id] = rows.TryGetValue(id, out var predecessor)
                        ? TransferReadModel.Merge(predecessor, row) : row;
                }
            return new(retained.TransferIds.Select(id => rows[id]).ToArray(), retained.NextCursor,
                retainedCoverage, readPosition);
        }

        var selected = current.Values.Where(row => MatchesTransferFilter(row, boundary,
                jobId, workflowId, direction, source, state, terminalOutcome, username, fromUtc, toUtc, groupRef))
            .OrderByDescending(row => row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0)
            .ThenByDescending(row => row.TransferId)
            .Take(limit == int.MaxValue ? limit : limit + 1).ToArray();
        return ComposeTransferTimeline([], selected.Select(row => TransferReadModel.FromLive(row.ToDto())).ToArray(), [],
            limit, false, queueHasMore, retainedCoverage) with { ReadPosition = readPosition };
    }

    private static HashSet<TransferGroupKeyDto> TransferGroups(IEnumerable<TransferHistoryDto> rows)
        => rows.Where(row => row.GroupRef is not null)
            .Select(row => new TransferGroupKeyDto(
                Enum.Parse<TransferCommandDirection>(row.Direction, ignoreCase: true), row.Username, row.GroupRef!))
            .ToHashSet();

    private async Task<TransferGroupSummariesResponseDto> ReadRequestedTransferGroupSummariesAsync(
        IReadOnlyCollection<TransferGroupKeyDto> groups, CancellationToken cancellationToken)
    {
        using var activity = TransferReads.StartActivity("timeline.groups");
        activity?.SetTag("group.count", groups.Count);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await GetTransferGroupSummariesAsync(groups.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            // Totals are optional metadata, not ownership of valid file rows.
            // Repeated observations must not flood logs during a storage outage.
            if (groupReadWarnings.TryAcquire(out long suppressed))
                ServerLogMessages.TransferGroupReadFailed(logger ?? NullLogger<HistoricalQueryFacade>.Instance,
                    ex, activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"), groups.Count,
                    (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, suppressed);
            return new([], new(TransferRetainedCoverageState.Unavailable,
                "Transfer folder totals could not be read."));
        }
    }

    public async Task<TransferGroupSummariesResponseDto> GetTransferGroupSummariesAsync(
        IReadOnlyList<TransferGroupKeyDto> groups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var readPosition = live.GetTransferReadPosition();
        var coverage = RetainedTransferCoverage();
        if (coverage.State == TransferRetainedCoverageState.Unavailable
            || persistence.TransferHistory is null)
            return new([], coverage, readPosition);

        var requested = groups.Select(group => new TransferGroupKey(
                group.Direction.ToString(), group.Username, group.GroupRef))
            .ToHashSet();
        if (requested.Count == 0)
            return new([], coverage, readPosition);

        // Capture volatile rows before their durable successors. The reader
        // obtains aggregate totals and overlap rows from one SQLite snapshot.
        var current = live.GetTransferGroupSnapshot(groups.ToHashSet());
        PersistedTransferGroupProjection retained = await persistence.TransferHistory
            .GetGroupProjectionAsync(requested, current.Select(row => row.TransferId).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        var totals = retained.Summaries.ToDictionary(summary => summary.Key);
        var persistedById = retained.OverlappingTransfers.ToDictionary(transfer => transfer.Id);

        foreach (TransferReadSnapshot liveRow in current)
        {
            persistedById.TryGetValue(liveRow.TransferId, out TransferGroupMember? persisted);
            if (persisted is { ArchivedAtUtc: null })
                AdjustGroupTotals(totals, persisted, -1);
            var chosen = TransferReadModel.ForGroup(liveRow, persisted);
            if (chosen.ArchivedAtUtc is null)
                AdjustGroupTotals(totals, chosen, 1);
        }

        return new(totals.Values
            .Where(summary => summary.FileCount > 0 && requested.Contains(summary.Key))
            .Select(summary => new TransferGroupSummaryDto(
                    new TransferGroupKeyDto(
                        Enum.Parse<TransferCommandDirection>(summary.Key.Direction, ignoreCase: true),
                        summary.Key.Username,
                        summary.Key.GroupRef),
                    summary.FileCount,
                    summary.KnownTotalBytes,
                    summary.TransferredBytes,
                    summary.SpeedBytesPerSecond,
                    summary.UnknownTotalCount,
                    summary.KnownRemainingActiveBytes,
                    summary.UnknownActiveTotalCount,
                    summary.QueuedCount,
                    summary.InProgressCount,
                    summary.SucceededCount,
                    summary.FailedCount,
                    summary.CancelledCount,
                    summary.InterruptedCount))
            .OrderBy(summary => summary.Key.Direction)
            .ThenBy(summary => summary.Key.Username, StringComparer.Ordinal)
            .ThenBy(summary => summary.Key.GroupRef, StringComparer.Ordinal)
            .ToArray(), coverage, readPosition);
    }

    private static void AdjustGroupTotals(
        Dictionary<TransferGroupKey, PersistedTransferGroupSummary> totals,
        TransferGroupMember row,
        int sign)
    {
        if (row.GroupRef is null) return;
        var key = new TransferGroupKey(row.Direction, row.Username, row.GroupRef);
        totals.TryGetValue(key, out PersistedTransferGroupSummary? current);
        current ??= new(key, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        long? size = row.TotalBytes ?? row.FileSize;
        bool active = string.Equals(row.TerminalOutcome, "None", StringComparison.OrdinalIgnoreCase);
        bool queued = active && string.Equals(row.State, "Queued", StringComparison.OrdinalIgnoreCase);
        totals[key] = current with
        {
            FileCount = current.FileCount + sign,
            KnownTotalBytes = current.KnownTotalBytes + sign * (size ?? 0),
            TransferredBytes = current.TransferredBytes + sign * row.TransferredBytes,
            SpeedBytesPerSecond = current.SpeedBytesPerSecond
                + (active ? sign * (row.BytesPerSecond ?? 0) : 0),
            UnknownTotalCount = current.UnknownTotalCount + (size is null ? sign : 0),
            KnownRemainingActiveBytes = current.KnownRemainingActiveBytes
                + (active && size is not null ? sign * Math.Max(0, size.Value - row.TransferredBytes) : 0),
            UnknownActiveTotalCount = current.UnknownActiveTotalCount + (active && size is null ? sign : 0),
            QueuedCount = current.QueuedCount + (queued ? sign : 0),
            InProgressCount = current.InProgressCount + (active && !queued ? sign : 0),
            SucceededCount = current.SucceededCount + (row.TerminalOutcome == "Succeeded" ? sign : 0),
            FailedCount = current.FailedCount + (row.TerminalOutcome == "Failed" ? sign : 0),
            CancelledCount = current.CancelledCount + (row.TerminalOutcome == "Cancelled" ? sign : 0),
            InterruptedCount = current.InterruptedCount + (row.TerminalOutcome == "Interrupted" ? sign : 0),
        };
    }

    internal static TransferTimelinePageDto ComposeTransferTimeline(
        IReadOnlyList<TransferHistoryDto> retained,
        IReadOnlyList<TransferHistoryDto> liveRows,
        IReadOnlyList<TransferHistoryDto> queuedRows,
        int limit,
        bool retainedHasMore,
        bool queueHasMore,
        TransferRetainedCoverageDto retainedCoverage)
    {
        var candidates = retained.ToDictionary(item => item.TransferId);
        foreach (TransferHistoryDto row in liveRows)
            candidates[row.TransferId] = candidates.TryGetValue(row.TransferId, out TransferHistoryDto? retainedRow)
                ? TransferReadModel.Merge(retainedRow, row)
                : row;
        foreach (TransferHistoryDto row in queuedRows)
            candidates[row.TransferId] = candidates.TryGetValue(row.TransferId, out TransferHistoryDto? queuedRetainedRow)
                ? TransferReadModel.Merge(queuedRetainedRow, row)
                : row;

        var ordered = candidates.Values
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.TransferId)
            .ToList();
        bool hasMore = ordered.Count > limit
            || retainedHasMore
            || queueHasMore;
        if (ordered.Count > limit)
            ordered.RemoveRange(limit, ordered.Count - limit);
        string? next = hasMore && ordered.Count > 0
            ? TransferHistoryReader.EncodeCursor(
                ordered[^1].CreatedAtUtc.ToUnixTimeMilliseconds(),
                ordered[^1].TransferId)
            : null;
        return new TransferTimelinePageDto(ordered, next, retainedCoverage);
    }

    public async Task<TransferCommandReceiptDto> SetTransfersArchivedAsync(
        TransferArchiveFilter filter,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        if (persistence.TransferHistory is null)
            throw new NotSupportedException(
                "Transfer archive is unavailable because persistence is disabled or not started.");
        // A terminal delta is readable before its durable successor exists.
        // Archive only after those known terminal writes can participate, then
        // release the same captured population rather than newer completions.
        var terminal = archived ? live.GetActiveTransferSnapshot()
            .Where(transfer => transfer.Status.IsTerminal && MatchesArchiveFilter(transfer, filter)).ToArray() : [];
        foreach (var transfer in terminal)
            await persistence.WaitForTransferHandoffAsync(
                transfer.TransferId, transfer.Revision, cancellationToken).ConfigureAwait(false);
        TransferArchiveResult result = await persistence.TransferHistory
            .SetArchivedAsync(filter, archived, cancellationToken)
            .ConfigureAwait(false);

        foreach (var transfer in terminal)
            live.RemoveTerminalTransfer(transfer.TransferId);

        if (result.ChangedCount > 0)
            live.MarkTransferHistoryChanged();

        return new TransferCommandReceiptDto(
            result.ResolvedCount,
            result.ChangedCount,
            result.NoOpCount,
            result.RejectedCount,
            FailedCount: 0,
            result.Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TransferCommandReasonCountDto(pair.Key, pair.Value))
                .ToArray());
    }

    private static bool MatchesArchiveFilter(
        TransferStateDto transfer,
        TransferArchiveFilter filter)
    {
        DateTimeOffset created = transfer.Scheduling?.RequestedAtUtc
            ?? DateTimeOffset.UnixEpoch;
        return (!filter.TransferId.HasValue || transfer.TransferId == filter.TransferId.Value)
            && (filter.Direction is null || EqualsFilter(transfer.Identity.Direction, filter.Direction))
            && (filter.TerminalOutcome is null
                || EqualsFilter(transfer.Status.TerminalOutcome.ToString(), filter.TerminalOutcome))
            && (filter.Username is null
                || string.Equals(transfer.Identity.Username, filter.Username, StringComparison.Ordinal))
            && (filter.GroupRef is null
                || string.Equals(transfer.Identity.GroupRef, filter.GroupRef, StringComparison.Ordinal))
            && (!filter.FromUtc.HasValue || created >= filter.FromUtc.Value)
            && (!filter.ToUtc.HasValue || created <= filter.ToUtc.Value);
    }

    private TransferRetainedCoverageDto RetainedTransferCoverage()
    {
        if (!persistence.IsEnabled)
            return new(TransferRetainedCoverageState.Unavailable, "PersistenceDisabled");
        if (!persistence.IsStarted || persistence.TransferHistory == null)
            return new(TransferRetainedCoverageState.Unavailable, "PersistenceUnavailable");
        return persistence.HealthSnapshot?.State switch
        {
            PersistenceHealthState.Degraded => new(
                TransferRetainedCoverageState.Degraded,
                "PersistenceDegraded"),
            PersistenceHealthState.Unhealthy => new(
                TransferRetainedCoverageState.Unavailable,
                "PersistenceUnhealthy"),
            _ => new(TransferRetainedCoverageState.Available),
        };
    }

    private static bool CanIncludeQueuedUploads(
        string? direction,
        string? source,
        string? state,
        string? terminalOutcome)
        => (direction == null || EqualsFilter(direction, "Upload"))
            && (source == null || EqualsFilter(source, "SoulseekPeer"))
            && (state == null || EqualsFilter(state, "Queued"))
            && (terminalOutcome == null || EqualsFilter(terminalOutcome, "None"));

    private static bool MatchesTransferFilter(
        TransferReadSnapshot row,
        TransferHistoryReader.TransferCursorValue? boundary,
        Guid? jobId,
        Guid? workflowId,
        string? direction,
        string? source,
        string? state,
        string? terminalOutcome,
        string? username,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? groupRef = null)
    {
        long created = row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0;
        if (boundary is { } cursor
            && (created > cursor.CreatedAtUtc
                || created == cursor.CreatedAtUtc
                && row.TransferId.CompareTo(cursor.Id) >= 0))
            return false;
        return (!jobId.HasValue || row.JobId == jobId)
            && (!workflowId.HasValue || row.WorkflowId == workflowId)
            && (direction == null || EqualsFilter(row.Direction, direction))
            && (source == null || EqualsFilter(row.Source, source))
            && (state == null || EqualsFilter(row.State, state))
            && (terminalOutcome == null || EqualsFilter(row.TerminalOutcome.ToString(), terminalOutcome))
            && (username == null || string.Equals(row.Username, username, StringComparison.Ordinal))
            && (groupRef == null || string.Equals(row.GroupRef, groupRef, StringComparison.Ordinal))
            && (!fromUtc.HasValue || created >= fromUtc.Value.ToUnixTimeMilliseconds())
            && (!toUtc.HasValue || created <= toUtc.Value.ToUnixTimeMilliseconds());
    }

    private static bool EqualsFilter(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
        var liveDetail = supervisor.GetJobDetailByDisplayId(workflowId, displayId);
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
        return new JobDetailDto(HistoricalJobDtoMapper.ToSummary(job), HistoricalJobDtoMapper.ToPayload(job), childCount);
    }

    private async Task<(TransferHistoryDto Transfer, TransferAttemptHistoryDto? LatestAttempt)?> GetHistoricalTransferAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (persistence.TransferHistory == null)
            return null;
        var detail = await persistence.TransferHistory.GetTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
        return detail == null
            ? null
            : (TransferReadModel.FromRetained(detail.Transfer), detail.LatestAttempt == null ? null : ToAttempt(detail.LatestAttempt));
    }

    public async Task<TransferDetailDto?> GetTransferDetailAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        LiveTransferDetail? liveDetail = live.GetLiveTransferDetail(transferId);
        var historical = await GetHistoricalTransferAsync(
            transferId,
            cancellationToken).ConfigureAwait(false);
        if (liveDetail is null && historical is null)
            return null;

        TransferQueueEstimateDto? estimate = null;
        if (liveDetail is not null
            && liveDetail.Transfer.Status.State.Equals("Queued", StringComparison.OrdinalIgnoreCase)
            && supervisor.Sharing is { } sharing)
        {
            var value = sharing.Uploads.GetQueueEstimate(transferId);
            estimate = new TransferQueueEstimateDto(
                value.AheadCount,
                value.QueueRevision);
        }

        return new TransferDetailDto(
            liveDetail is not null && historical is not null
                ? TransferDetailSource.Merged
                : liveDetail is not null
                    ? TransferDetailSource.Live
                    : TransferDetailSource.Historical,
            liveDetail?.Transfer,
            estimate,
            historical?.Transfer,
            Math.Max(
                liveDetail?.Transfer.Status.AttemptCount ?? 0,
                historical?.Transfer.AttemptCount ?? 0),
            liveDetail?.LatestAttempt ?? historical?.LatestAttempt);
    }

    public async Task<CombinedTransferAttemptPage?> GetTransferAttemptsAsync(
        Guid transferId,
        int afterAttemptNumber,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Capture live attempts before reading their durable successors, so a
        // transfer cannot disappear between retirement and the SQLite snapshot.
        // Never wait for the writer just to read an already-visible transfer.
        var current = live.GetLiveTransferAttempts(
            transferId, afterAttemptNumber, limit == int.MaxValue ? limit : limit + 1);
        var retained = persistence.TransferHistory == null ? null
            : await persistence.TransferHistory.GetAttemptsAsync(
                transferId, afterAttemptNumber, limit, cancellationToken).ConfigureAwait(false);
        if (current == null && retained == null)
            return null;
        var attempts = (current ?? []).ToDictionary(attempt => attempt.AttemptNumber);
        foreach (var item in retained?.Items ?? [])
        {
            var attempt = ToAttempt(item);
            if (!attempts.TryGetValue(attempt.AttemptNumber, out var previous)
                || previous.Revision < attempt.Revision)
                attempts[attempt.AttemptNumber] = attempt;
        }
        var ordered = attempts.Values.OrderBy(attempt => attempt.AttemptNumber).ToList();
        bool hasMore = ordered.Count > limit || retained?.NextAttemptNumber != null;
        if (ordered.Count > limit)
            ordered.RemoveRange(limit, ordered.Count - limit);
        return new CombinedTransferAttemptPage(ordered,
            hasMore && ordered.Count > 0 ? ordered[^1].AttemptNumber : null);
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
                capture.Position);
        }
        persistence.EnsureJobProjectionsAvailable(query.WorkflowId);
        var persisted = await persistence.JobHistory.GetJobsAsync(new JobHistoryQuery(
            cursor, limit, query.LifecycleState?.ToString(), query.TerminalOutcome?.ToString(),
            query.SkipReason?.ToString(), query.Kind?.ToString(), query.WorkflowId, query.IncludeAll,
            query.ParentJobId, query.SubmissionId, query.Role?.ToString(), query.Archived,
            query.NewestFirst, query.ExcludeExtractResults, capture.OwnedJobIds), cancellationToken).ConfigureAwait(false);
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
            capture.Position);
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
        return new JobDetailDto(
            HistoricalJobDtoMapper.ToSummary(job),
            HistoricalJobDtoMapper.ToPayload(job),
            childCount);
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
                var file = item.Child == null ? null : HistoricalJobDtoMapper.ToPayload(item.Child) switch
                {
                    SongJobPayloadDto song => song.File,
                    RemoteFileJobPayloadDto remote => remote.File,
                    _ => null,
                };
                return new DirectoryManifestEntryDto(
                    item.Ordinal,
                    item.Target != null ? ServerSnapshotMapper.ToPeerFileTargetDto(item.Target)
                        : item.Child == null ? null : HistoricalJobDtoMapper.ToSelectedFileTarget(item.Child),
                    item.RelativeDirectoryComponents,
                    item.ChildJobId,
                    item.Child == null ? null : HistoricalJobDtoMapper.ToSummary(item.Child),
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
        var liveResults = supervisor.GetSearchRawResults(jobId, afterSequence, limit == int.MaxValue ? limit : limit + 1);
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

    private static TransferAttemptHistoryDto ToAttempt(PersistedTransferAttempt attempt)
        => new(
            attempt.Id, attempt.TransferId, attempt.AttemptNumber, attempt.Source, attempt.State,
            attempt.SourceUsername, attempt.SourcePath, attempt.OutputPath,
            attempt.StartedAtUtc, attempt.CompletedAtUtc, attempt.FailureReason, attempt.FailureMessage, attempt.Revision);

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
