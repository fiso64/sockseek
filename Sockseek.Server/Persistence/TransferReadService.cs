using Sockseek.Api;
using Sockseek.Core.Services;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Write;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Diagnostics;

namespace Sockseek.Server.Persistence;

public sealed record CombinedTransferAttemptPage(IReadOnlyList<TransferAttemptHistoryDto> Items, int? NextAttemptNumber);

/// <summary>Canonical current/retained transfer reads and displayed timeline scopes.</summary>
public sealed partial class TransferReadService(EngineStateStore live, DaemonRuntime? runtime,
    PersistenceCoordinator persistence, ILogger<TransferReadService>? logger = null)
{
    private readonly RepeatedWarningGate groupReadWarnings = new();
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
        return GetTransfersAsync(new(Limit: Math.Max(1, ids.Count), Direction: direction, TransferIds: ids), cancellationToken);
    }

    public async Task<TransferTimelinePageDto> GetTransfersAsync(
        TransferQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var transferIds = query.TransferIds as IReadOnlySet<Guid> ?? query.TransferIds?.ToHashSet();
        if (query.Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(query.Limit));
        TransferHistoryReader.TransferCursorValue? boundary =
            TransferHistoryReader.DecodeCursor(query.Cursor);

        // Capture before either source: claiming a later commit could conceal
        // a completion in the gap between the HTTP read and live subscription.
        StateStreamPositionDto readPosition = live.GetReadPosition();
        var retainedCoverage = RetainedTransferCoverage();
        // Keep captured facts until membership is known. Wire metadata and
        // retained file attributes belong to the selected page, not every live row.
        var current = (query.Archived ? [] : transferIds is not null
            ? live.GetTransferFactsSnapshot(transferIds)
            : live.GetTransferPageCandidates(query.GroupRef, query.Direction, query.Username))
            .ToDictionary(row => row.TransferId);
        bool queueHasMore = false;
        if (!query.Archived && query.GroupRef is null && transferIds is null
            && CanIncludeQueuedUploads(query)
            && runtime?.Sharing is { } sharing)
        {
            var queued = sharing.Uploads.GetNewestQueuePage(
                boundary is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value.CreatedAtUtc) : null,
                boundary?.Id, query.Limit == int.MaxValue ? query.Limit : query.Limit + 1, query.Username, query.FromUtc, query.ToUtc);
            queueHasMore = queued.NextTransferId != null;
            var queuedIds = queued.Items.Select(item => item.TransferId).ToHashSet();
            foreach (var row in live.GetTransferFactsSnapshot(queuedIds))
                if (!current.TryGetValue(row.TransferId, out var previous) || row.Revision > previous.Revision)
                    current[row.TransferId] = row;
        }
        var membershipQuery = query with { FromUtc = null, ToUtc = null };
        var candidates = current.Values.Select(row => new LiveTransferCandidate(
            row.TransferId, row.Revision, row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0,
            MatchesTransferFilter(row, membershipQuery))).ToArray();
        if (persistence.TransferHistory is not null && retainedCoverage.State != TransferRetainedCoverageState.Unavailable)
        {
            var retained = await persistence.TransferHistory.GetTransfersAsync(
                query with { LiveCandidates = candidates, LiveHasMore = queueHasMore }, cancellationToken).ConfigureAwait(false);
            var rows = retained.Items.ToDictionary(row => row.Id, TransferReadModel.FromRetained);
            foreach (Guid id in retained.TransferIds)
                if (current.TryGetValue(id, out var captured))
                {
                    if (rows.TryGetValue(id, out var newer) && newer.Revision > captured.Revision)
                        continue;
                    var row = captured.ToDto();
                    rows[id] = rows.TryGetValue(id, out var predecessor)
                        ? TransferReadModel.Merge(predecessor, row) : row;
                }
            return new(retained.TransferIds.Select(id => rows[id]).ToArray(), retained.NextCursor,
                retainedCoverage, readPosition);
        }

        var selected = current.Values.Where(row => MatchesTransferFilter(row, query, boundary))
            .OrderByDescending(row => row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0)
            .ThenByDescending(row => row.TransferId)
            .Take(query.Limit == int.MaxValue ? query.Limit : query.Limit + 1).ToArray();
        return ComposeTransferTimeline([], selected.Select(row => row.ToDto()).ToArray(), [],
            query.Limit, false, queueHasMore, retainedCoverage) with { ReadPosition = readPosition };
    }

    private static HashSet<TransferGroupKeyDto> TransferGroups(IEnumerable<TransferStateDto> rows)
        => rows.Where(row => row.Identity.GroupRef is not null)
            .Select(row => new TransferGroupKeyDto(
                Enum.Parse<TransferCommandDirection>(row.Identity.Direction, ignoreCase: true), row.Identity.Username, row.Identity.GroupRef!))
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
                ServerLogMessages.TransferGroupReadFailed(logger ?? NullLogger<TransferReadService>.Instance,
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
        bool queued = active && row.State.StartsWith("Queued", StringComparison.OrdinalIgnoreCase);
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
        IReadOnlyList<TransferStateDto> retained,
        IReadOnlyList<TransferStateDto> liveRows,
        IReadOnlyList<TransferStateDto> queuedRows,
        int limit,
        bool retainedHasMore,
        bool queueHasMore,
        TransferRetainedCoverageDto retainedCoverage)
    {
        var candidates = retained.ToDictionary(item => item.TransferId);
        foreach (TransferStateDto row in liveRows)
            candidates[row.TransferId] = candidates.TryGetValue(row.TransferId, out TransferStateDto? retainedRow)
                ? TransferReadModel.Merge(retainedRow, row)
                : row;
        foreach (TransferStateDto row in queuedRows)
            candidates[row.TransferId] = candidates.TryGetValue(row.TransferId, out TransferStateDto? queuedRetainedRow)
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

    private static bool CanIncludeQueuedUploads(TransferQuery query)
        => (query.Direction == null || EqualsFilter(query.Direction, "Upload"))
            && (query.Source == null || EqualsFilter(query.Source, "SoulseekPeer"))
            && (query.State == null || EqualsFilter(query.State, "Queued"))
            && (query.TerminalOutcome == null || EqualsFilter(query.TerminalOutcome, "None"));

    private static bool MatchesTransferFilter(
        TransferReadSnapshot row, TransferQuery query,
        TransferHistoryReader.TransferCursorValue? boundary = null)
    {
        long created = row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0;
        if (boundary is { } cursor
            && (created > cursor.CreatedAtUtc
                || created == cursor.CreatedAtUtc
                && row.TransferId.CompareTo(cursor.Id) >= 0))
            return false;
        return (!query.JobId.HasValue || row.JobId == query.JobId)
            && (!query.WorkflowId.HasValue || row.WorkflowId == query.WorkflowId)
            && (query.Direction == null || EqualsFilter(row.Direction, query.Direction))
            && (query.Source == null || EqualsFilter(row.Source, query.Source))
            && (query.State == null || EqualsFilter(row.State, query.State)
                || EqualsFilter(query.State, "Queued") && row.State.StartsWith("Queued", StringComparison.OrdinalIgnoreCase))
            && (query.TerminalOutcome == null || EqualsFilter(row.TerminalOutcome.ToString(), query.TerminalOutcome))
            && (query.Username == null || string.Equals(row.Username, query.Username, StringComparison.Ordinal))
            && (query.GroupRef == null || string.Equals(row.GroupRef, query.GroupRef, StringComparison.Ordinal))
            && (!query.FromUtc.HasValue || created >= query.FromUtc.Value.ToUnixTimeMilliseconds())
            && (!query.ToUtc.HasValue || created <= query.ToUtc.Value.ToUnixTimeMilliseconds());
    }

    private static bool EqualsFilter(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private async Task<(TransferStateDto Transfer, TransferAttemptHistoryDto? LatestAttempt)?> GetHistoricalTransferAsync(
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
            && runtime?.Sharing is { } sharing)
        {
            var value = sharing.Uploads.GetQueueEstimate(transferId);
            estimate = new TransferQueueEstimateDto(
                value.AheadCount,
                value.QueueRevision);
        }

        var transfer = historical is { } retained
            ? liveDetail is { } current
                ? TransferReadModel.Merge(retained.Transfer, current.Transfer)
                : retained.Transfer
            : liveDetail!.Transfer;
        var latestAttempt = liveDetail?.LatestAttempt;
        if (historical?.LatestAttempt is { } storedAttempt
            && (latestAttempt is null || storedAttempt.AttemptNumber > latestAttempt.AttemptNumber
                || storedAttempt.AttemptNumber == latestAttempt.AttemptNumber && storedAttempt.Revision > latestAttempt.Revision))
            latestAttempt = storedAttempt;
        return new TransferDetailDto(transfer, estimate, latestAttempt);
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

    private static TransferAttemptHistoryDto ToAttempt(PersistedTransferAttempt attempt)
        => new(
            attempt.Id, attempt.TransferId, attempt.AttemptNumber, attempt.Source, attempt.State,
            attempt.SourceUsername, attempt.SourcePath, attempt.OutputPath,
            attempt.StartedAtUtc, attempt.CompletedAtUtc, attempt.FailureReason, attempt.FailureMessage, attempt.Revision);

}
