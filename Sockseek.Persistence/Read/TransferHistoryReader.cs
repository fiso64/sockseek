using Microsoft.Data.Sqlite;
using Sockseek.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Sqlite;
using System.Text;
using System.Text.Json;
using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.Read;

public sealed record PersistedTransfer(
    Guid Id,
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string State,
    string TerminalOutcome,
    long? TotalBytes,
    long TransferredBytes,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string FailureReason,
    string? FailureMessage,
    string CancellationSource,
    long Revision,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? LastProgressAtUtc = null,
    long? BytesPerSecond = null,
    TransferFileMetadataSnapshot? File = null,
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    DateTimeOffset? ArchivedAtUtc = null,
    string? GroupRelativePath = null);

public sealed record PersistedTransferAttempt(
    Guid Id,
    Guid TransferId,
    int AttemptNumber,
    string Source,
    string State,
    string? SourceUsername,
    string? SourcePath,
    string? OutputPath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string FailureReason,
    string? FailureMessage,
    long Revision);

public sealed record PersistedTransferDetail(PersistedTransfer Transfer, PersistedTransferAttempt? LatestAttempt);
// Source facts needed to select a page before hydrating presentation metadata.
public sealed record LiveTransferCandidate(Guid TransferId, long Revision, long CreatedAtUtc, bool MatchesFilter);
/// <summary>Filters and moving cursor shared by current/retained transfer reads.</summary>
public sealed record TransferQuery(
    string? Cursor = null,
    int Limit = 100,
    Guid? JobId = null,
    Guid? WorkflowId = null,
    string? Direction = null,
    string? Source = null,
    string? State = null,
    string? TerminalOutcome = null,
    string? Username = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    bool Archived = false,
    string? GroupRef = null,
    IReadOnlyCollection<Guid>? TransferIds = null,
    IReadOnlyCollection<LiveTransferCandidate>? LiveCandidates = null,
    bool LiveHasMore = false);
// TransferIds is the complete ordered page, including source-only transfers.
// Items supplies retained facts for those identities, from the same SQL snapshot.
public sealed record PersistedTransferPage(
    IReadOnlyList<PersistedTransfer> Items, IReadOnlyList<Guid> TransferIds, string? NextCursor);
public sealed record TransferGroupKey(string Direction, string? Username, string GroupRef);
public sealed record PersistedTransferGroupSummary(
    TransferGroupKey Key,
    long FileCount,
    long KnownTotalBytes,
    long TransferredBytes,
    long SpeedBytesPerSecond,
    long UnknownTotalCount,
    long KnownRemainingActiveBytes,
    long UnknownActiveTotalCount,
    long QueuedCount,
    long InProgressCount,
    long SucceededCount,
    long FailedCount,
    long CancelledCount,
    long InterruptedCount);
// Group reconciliation needs scalar contributions, never paths, actions or
// file attributes. Keep that boundary explicit so totals cannot hydrate rows.
public sealed record TransferGroupMember(
    Guid Id,
    long Revision,
    string Direction,
    string? Username,
    string? GroupRef,
    string State,
    string TerminalOutcome,
    long? TotalBytes,
    long? FileSize,
    long TransferredBytes,
    long? BytesPerSecond,
    DateTimeOffset? ArchivedAtUtc);
public sealed record PersistedTransferGroupProjection(
    IReadOnlyList<PersistedTransferGroupSummary> Summaries,
    IReadOnlyList<TransferGroupMember> OverlappingTransfers);
public sealed record PersistedTransferAttemptPage(IReadOnlyList<PersistedTransferAttempt> Items, int? NextAttemptNumber);
public sealed record TransferArchiveFilter(
    Guid? TransferId = null,
    string? Direction = null,
    string? TerminalOutcome = null,
    string? Username = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? GroupRef = null);
public sealed record TransferArchiveResult(
    int ResolvedCount,
    int ChangedCount,
    int NoOpCount,
    int RejectedCount,
    IReadOnlyDictionary<string, int> Reasons);

public interface ITransferHistoryReader
{
    Task<PersistedTransferPage> GetTransfersAsync(TransferQuery query, CancellationToken cancellationToken = default);
    Task<PersistedTransferGroupProjection> GetGroupProjectionAsync(
        IReadOnlyCollection<TransferGroupKey> keys,
        IReadOnlyCollection<Guid> overlappingTransferIds,
        CancellationToken cancellationToken = default);
    Task<PersistedTransferDetail?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<PersistedTransferAttemptPage?> GetAttemptsAsync(Guid transferId, int afterAttemptNumber, int limit, CancellationToken cancellationToken = default);
    Task<TransferArchiveResult> SetArchivedAsync(
        TransferArchiveFilter filter,
        bool archived,
        CancellationToken cancellationToken = default);
}

public sealed class TransferHistoryReader(
    IDbContextFactory<SockseekDbContext> contextFactory,
    Persistence.Write.PersistenceInbox? inbox = null,
    TimeProvider? timeProvider = null) : ITransferHistoryReader
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<PersistedTransferPage> GetTransfersAsync(TransferQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit < 1) throw new ArgumentOutOfRangeException(nameof(query));
        var cursor = DecodeCursor(query.Cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shape = HistoryRowMapping<TransferEntity>.For(context);
        var conditions = new List<string> { query.Archived ? "t.archived_at_utc IS NOT NULL" : "t.archived_at_utc IS NULL" };
        var values = new List<(string Name, object Value)>();
        IEnumerable<object> Parameters() => values.Select(value => new SqliteParameter(value.Name, value.Value));
        void Filter(string predicate, string parameter, object? value)
        {
            if (value is null) return;
            conditions.Add(predicate);
            values.Add((parameter, value));
        }
        Filter("t.job_id = $job", "$job", query.JobId);
        Filter("t.workflow_id = $workflow", "$workflow", query.WorkflowId);
        Filter("t.direction COLLATE NOCASE = $direction", "$direction", query.Direction);
        Filter("t.source COLLATE NOCASE = $source", "$source", query.Source);
        // Keep the exact source label (including local/remote queue flags),
        // while the Queued filter covers the same family as live counters.
        if (string.Equals(query.State, "Queued", StringComparison.OrdinalIgnoreCase))
            Filter("t.state LIKE $state", "$state", "Queued%");
        else Filter("t.state COLLATE NOCASE = $state", "$state", query.State);
        Filter("t.terminal_outcome COLLATE NOCASE = $outcome", "$outcome", query.TerminalOutcome);
        Filter("t.username = $username", "$username", query.Username);
        Filter("t.group_ref = $group", "$group", query.GroupRef);
        Filter("t.created_at_utc >= $from", "$from", query.FromUtc?.ToUnixTimeMilliseconds());
        Filter("t.created_at_utc <= $to", "$to", query.ToUtc?.ToUnixTimeMilliseconds());
        if (cursor is { } boundary)
        {
            conditions.Add("(t.created_at_utc < $before OR t.created_at_utc = $before AND t.id < $beforeId)");
            values.Add(("$before", boundary.CreatedAtUtc));
            values.Add(("$beforeId", boundary.Id));
        }
        var superseded = query.LiveCandidates?.Where(status => !status.MatchesFilter).ToArray() ?? [];
        if (superseded.Length > 0)
        {
            values.Add(("$superseded", JsonSerializer.Serialize(superseded.ToDictionary(
                status => status.TransferId.ToString().ToUpperInvariant(), status => status.Revision))));
            conditions.Add("""
                t.id NOT IN (
                    SELECT predecessor.id FROM json_each($superseded) current_state
                    CROSS JOIN transfers predecessor
                    WHERE predecessor.id = current_state.key
                        AND predecessor.revision <= CAST(current_state.value AS INTEGER))
                """);
        }
        var matchingLive = query.LiveCandidates?.Where(row => row.MatchesFilter)
            .DistinctBy(row => row.TransferId).ToArray() ?? [];
        await using var snapshot = matchingLive.Length > 0
            ? await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false)
            : null;
        string predicate = string.Join(" AND ", conditions);
        string from = $"FROM {shape.TableName} t WHERE {predicate}";
        if (query.TransferIds is { } ids)
        {
            values.Add(("$ids", EncodeIds(ids)));
            // Explicit identities drive unique seeks. A filter on an ordered
            // timeline alone can make SQLite scan all history to find them.
            from = $"FROM json_each($ids) requested CROSS JOIN {shape.TableName} t "
                + $"WHERE t.id = requested.value AND {predicate}";
            predicate += " AND t.id IN (SELECT value FROM json_each($ids))";
        }
        values.Add(("$limit", query.Limit == int.MaxValue ? query.Limit : query.Limit + 1));
        string ordered = from + " ORDER BY t.created_at_utc DESC, t.id DESC LIMIT $limit";
        if (matchingLive.Length == 0)
        {
            var rows = await shape.ReadAsync(context,
                $"SELECT {shape.SelectColumns("t")} {ordered}", Parameters(), cancellationToken).ConfigureAwait(false);
            bool hasMore = rows.Count > query.Limit || query.LiveHasMore;
            if (rows.Count > query.Limit) rows.RemoveAt(rows.Count - 1);
            return new(rows.Select(MapTransfer).ToArray(), rows.Select(row => row.Id).ToArray(),
                hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].CreatedAtUtc, rows[^1].Id) : null);
        }

        // Reconcile scalar keys before bounded metadata hydration. All three
        // reads share a deferred WAL snapshot and the same filter predicate;
        // live volume cannot cause every file attribute blob to be loaded.
        var keys = await SqliteReadQuery.ReadAsync(context,
            "SELECT t.id, t.created_at_utc " + ordered, Parameters(),
            static row => new TransferPageKey(row.GetGuid(0), row.GetInt64(1)), cancellationToken).ConfigureAwait(false);
        var candidates = keys.ToDictionary(row => row.Id);
        var overlaps = await SqliteReadQuery.ReadAsync(context, $"""
            SELECT t.id, t.created_at_utc, t.revision, t.archived_at_utc, COALESCE(({predicate}), 0)
            FROM json_each($liveIds) requested CROSS JOIN {shape.TableName} t
            WHERE t.id = requested.value
            """, Parameters().Append(new SqliteParameter("$liveIds", EncodeIds(matchingLive.Select(row => row.TransferId)))),
            static row => new TransferPageOverlap(row.GetGuid(0), row.GetInt64(1), row.GetInt64(2),
                row.IsDBNull(3) ? null : row.GetInt64(3), row.GetBoolean(4)), cancellationToken).ConfigureAwait(false);
        var predecessors = overlaps.ToDictionary(row => row.Id);
        foreach (var current in matchingLive)
        {
            predecessors.TryGetValue(current.TransferId, out var retained);
            var key = new TransferPageKey(current.TransferId, retained?.CreatedAtUtc ?? current.CreatedAtUtc);
            bool matches = retained is not null && retained.Revision > current.Revision
                ? retained.MatchesFilter
                : query.Archived == (retained?.ArchivedAtUtc is not null)
                    && (!query.FromUtc.HasValue || key.CreatedAtUtc >= query.FromUtc.Value.ToUnixTimeMilliseconds())
                    && (!query.ToUtc.HasValue || key.CreatedAtUtc <= query.ToUtc.Value.ToUnixTimeMilliseconds())
                    && (cursor is null || key.CreatedAtUtc < cursor.Value.CreatedAtUtc
                        || key.CreatedAtUtc == cursor.Value.CreatedAtUtc && key.Id.CompareTo(cursor.Value.Id) < 0);
            if (matches) candidates[key.Id] = key;
            else candidates.Remove(key.Id);
        }
        var selected = candidates.Values.OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id)
            .Take(query.Limit).ToArray();
        var selectedRows = await ReadTransfersByIdsAsync(context, selected.Select(row => row.Id), cancellationToken)
            .ConfigureAwait(false);
        var rowsById = selectedRows.ToDictionary(row => row.Id);
        bool more = candidates.Count > query.Limit || query.LiveHasMore;
        await snapshot!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(selected.Where(row => rowsById.ContainsKey(row.Id)).Select(row => MapTransfer(rowsById[row.Id])).ToArray(),
            selected.Select(row => row.Id).ToArray(),
            more && selected.Length > 0 ? EncodeCursor(selected[^1].CreatedAtUtc, selected[^1].Id) : null);
    }

    private sealed record TransferPageKey(Guid Id, long CreatedAtUtc);
    private sealed record TransferPageOverlap(Guid Id, long CreatedAtUtc, long Revision,
        long? ArchivedAtUtc, bool MatchesFilter);

    private static string EncodeIds(IEnumerable<Guid> ids)
        => JsonSerializer.Serialize(ids.Distinct().Select(id => id.ToString().ToUpperInvariant()));

    private static Task<List<TransferEntity>> ReadTransfersByIdsAsync(
        SockseekDbContext context, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var shape = HistoryRowMapping<TransferEntity>.For(context);
        return shape.ReadAsync(context, $"""
            SELECT {shape.SelectColumns("t")}
            FROM json_each($ids) requested CROSS JOIN {shape.TableName} t
            WHERE t.id = requested.value
            """, [new SqliteParameter("$ids", EncodeIds(ids))], cancellationToken);
    }

    public async Task<PersistedTransferGroupProjection> GetGroupProjectionAsync(
        IReadOnlyCollection<TransferGroupKey> keys,
        IReadOnlyCollection<Guid> overlappingTransferIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(overlappingTransferIds);
        if (keys.Count == 0) return new([], []);
        string requestedKeys = JsonSerializer.Serialize(keys.Distinct()
            .Select(key => new string?[] { key.Direction, key.Username, key.GroupRef }));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TransferGroupMember> overlapping = [];
        if (overlappingTransferIds.Count > 0)
        {
            overlapping = await SqliteReadQuery.ReadAsync(context, """
                SELECT t.id, t.revision, t.direction, t.username, t.group_ref, t.state, t.terminal_outcome,
                    NULLIF(t.total_bytes, $unknown),
                    CASE WHEN t.file_name IS NOT NULL THEN t.file_size_bytes END,
                    t.transferred_bytes, t.bytes_per_second, t.archived_at_utc
                FROM json_each($ids) requested CROSS JOIN transfers t
                WHERE t.id = requested.value
                """, [new SqliteParameter("$ids", EncodeIds(overlappingTransferIds)), new SqliteParameter("$unknown", long.MaxValue)],
                static row => new TransferGroupMember(row.GetGuid(0), row.GetInt64(1), row.GetString(2),
                    row.IsDBNull(3) ? null : row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4),
                    row.GetString(5), row.GetString(6), row.IsDBNull(7) ? null : row.GetInt64(7),
                    row.IsDBNull(8) ? null : row.GetInt64(8), row.GetInt64(9),
                    row.IsDBNull(10) ? null : row.GetInt64(10), FromUnix(row.IsDBNull(11) ? null : row.GetInt64(11))),
                cancellationToken).ConfigureAwait(false);
        }
        // This fixed scalar query needs no EF expression compilation or
        // intermediate row model. Read directly within the same WAL snapshot
        // as the overlap facts above, including on the first request.
        using var command = ((SqliteConnection)context.Database.GetDbConnection()).CreateCommand();
        command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
        // Materialize keys once and keep the group-index traversal ordered. An
        // outer JSON join instead sorts every matched file into a temporary tree.
        // Test exact tuple membership after grouping, once per group, while the
        // independent candidate sets let SQLite seek the existing group index.
        // Require that index: choosing the timeline index scans unrelated history.
        command.CommandText = """
            WITH requested AS MATERIALIZED (
                SELECT json_extract(value, '$[0]') AS direction,
                    json_extract(value, '$[1]') AS username,
                    json_extract(value, '$[2]') AS group_ref
                FROM json_each($keys)
            )
            SELECT t.direction AS Direction, t.username AS Username, t.group_ref AS GroupRef,
                COUNT(*) AS FileCount,
                SUM(CASE WHEN t.total_bytes = $unknown THEN COALESCE(t.file_size_bytes, 0) ELSE t.total_bytes END) AS KnownTotalBytes,
                SUM(t.transferred_bytes) AS TransferredBytes,
                COALESCE(SUM(t.bytes_per_second) FILTER (WHERE t.terminal_outcome = 'None'), 0) AS SpeedBytesPerSecond,
                COUNT(*) FILTER (WHERE t.total_bytes = $unknown AND t.file_size_bytes IS NULL) AS UnknownTotalCount,
                COALESCE(SUM(MAX(
                    (CASE WHEN t.total_bytes = $unknown THEN t.file_size_bytes ELSE t.total_bytes END)
                        - t.transferred_bytes, 0)) FILTER (WHERE t.terminal_outcome = 'None'
                            AND (t.total_bytes != $unknown OR t.file_size_bytes IS NOT NULL)), 0) AS KnownRemainingActiveBytes,
                COUNT(*) FILTER (WHERE t.terminal_outcome = 'None' AND t.total_bytes = $unknown AND t.file_size_bytes IS NULL) AS UnknownActiveTotalCount,
                COUNT(*) FILTER (WHERE t.terminal_outcome = 'None' AND t.state LIKE 'Queued%') AS QueuedCount,
                COUNT(*) FILTER (WHERE t.terminal_outcome = 'None' AND t.state NOT LIKE 'Queued%') AS InProgressCount,
                COUNT(*) FILTER (WHERE t.terminal_outcome = 'Succeeded') AS SucceededCount,
                COUNT(*) FILTER (WHERE t.terminal_outcome = 'Failed') AS FailedCount,
                COUNT(*) FILTER (WHERE t.terminal_outcome = 'Cancelled') AS CancelledCount,
                COUNT(*) FILTER (WHERE t.terminal_outcome = 'Interrupted') AS InterruptedCount
            FROM transfers t INDEXED BY IX_transfers_group_ref_username_direction_archived_at_utc
            WHERE t.archived_at_utc IS NULL
                AND t.group_ref IN (SELECT group_ref FROM requested)
                AND t.direction IN (SELECT direction FROM requested)
                AND (t.username IN (SELECT username FROM requested)
                    OR t.username IS NULL AND EXISTS (SELECT 1 FROM requested WHERE username IS NULL))
            GROUP BY t.group_ref, t.username, t.direction
            HAVING EXISTS (SELECT 1 FROM requested r
                WHERE r.group_ref = t.group_ref AND r.username IS t.username AND r.direction = t.direction)
            """;
        command.Parameters.AddWithValue("$keys", requestedKeys);
        command.Parameters.AddWithValue("$unknown", long.MaxValue);
        var summaries = new List<PersistedTransferGroupSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                summaries.Add(new(
                    new TransferGroupKey(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                    reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                    reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15)));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(summaries, overlapping);
    }

    public async Task<PersistedTransferDetail?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var transfer = (await ReadTransfersByIdsAsync(context, [transferId], cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();
        if (transfer == null) return null;
        var attempts = HistoryRowMapping<TransferAttemptEntity>.For(context);
        var latestAttempt = (await attempts.ReadAsync(context, $"""
            SELECT {attempts.SelectColumns("a")} FROM {attempts.TableName} a
            WHERE a.transfer_id = $id ORDER BY a.attempt_number DESC, a.id DESC LIMIT 1
            """, [new SqliteParameter("$id", transferId)], cancellationToken).ConfigureAwait(false)).SingleOrDefault();

        var detail = new PersistedTransferDetail(
            MapTransfer(transfer),
            latestAttempt == null ? null : MapAttempt(latestAttempt));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return detail;
    }

    public async Task<PersistedTransferAttemptPage?> GetAttemptsAsync(
        Guid transferId,
        int afterAttemptNumber,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (afterAttemptNumber < 0) throw new ArgumentOutOfRangeException(nameof(afterAttemptNumber));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var found = await SqliteReadQuery.ReadAsync(context,
            "SELECT 1 FROM transfers WHERE id = $id", [new SqliteParameter("$id", transferId)],
            static row => row.GetInt32(0), cancellationToken).ConfigureAwait(false);
        if (found.Count == 0) return null;
        var attempts = HistoryRowMapping<TransferAttemptEntity>.For(context);
        var rows = await attempts.ReadAsync(context, $"""
            SELECT {attempts.SelectColumns("a")} FROM {attempts.TableName} a
            WHERE a.transfer_id = $id AND a.attempt_number > $after
            ORDER BY a.attempt_number LIMIT $limit
            """, [new SqliteParameter("$id", transferId), new SqliteParameter("$after", afterAttemptNumber),
                new SqliteParameter("$limit", limit == int.MaxValue ? limit : limit + 1)],
            cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var page = new PersistedTransferAttemptPage(
            rows.Select(MapAttempt).ToArray(),
            hasMore && rows.Count > 0 ? rows[^1].AttemptNumber : null);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<TransferArchiveResult> SetArchivedAsync(
        TransferArchiveFilter filter,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (inbox is null)
            throw new NotSupportedException("Transfer archive requires the daemon persistence writer.");

        var command = new Persistence.Write.AwaitablePersistenceCommand<TransferArchiveResult>(
            async (context, ct) =>
            {
                IQueryable<Entities.TransferEntity> matching = ApplyArchiveFilter(
                    context.Transfers,
                    filter);
                int resolved = await matching.CountAsync(ct).ConfigureAwait(false);
                if (resolved == 0)
                {
                    return filter.TransferId.HasValue
                        ? new(0, 0, 0, 1, new Dictionary<string, int>
                        {
                            ["not-found"] = 1,
                        })
                        : new(0, 0, 0, 0, new Dictionary<string, int>());
                }

                int rejected = archived
                    ? await matching.CountAsync(
                        transfer => transfer.TerminalOutcome == "None",
                        ct).ConfigureAwait(false)
                    : 0;
                IQueryable<Entities.TransferEntity> terminal = matching
                    .Where(transfer => transfer.TerminalOutcome != "None");
                IQueryable<Entities.TransferEntity> changes = archived
                    ? terminal.Where(transfer => transfer.ArchivedAtUtc == null)
                    : terminal.Where(transfer => transfer.ArchivedAtUtc != null);
                long? archiveTime = archived
                    ? clock.GetUtcNow().ToUnixTimeMilliseconds()
                    : null;
                int changed = await changes.ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        transfer => transfer.ArchivedAtUtc,
                        archiveTime),
                    ct).ConfigureAwait(false);
                int noOp = resolved - rejected - changed;
                var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
                if (rejected > 0)
                    reasons["nonterminal"] = rejected;
                if (noOp > 0)
                    reasons[archived ? "already-archived" : "already-restored"] = noOp;
                return new(resolved, changed, noOp, rejected, reasons);
            });
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return await command.Task.ConfigureAwait(false);
    }

    private static IQueryable<Entities.TransferEntity> ApplyArchiveFilter(
        IQueryable<Entities.TransferEntity> transfers,
        TransferArchiveFilter filter)
    {
        if (filter.TransferId.HasValue)
            transfers = transfers.Where(row => row.Id == filter.TransferId.Value);
        if (filter.Direction is not null)
            transfers = transfers.Where(row => EF.Functions.Collate(row.Direction, "NOCASE") == filter.Direction);
        if (filter.TerminalOutcome is not null)
            transfers = transfers.Where(row => EF.Functions.Collate(row.TerminalOutcome, "NOCASE") == filter.TerminalOutcome);
        if (filter.Username is not null)
            transfers = transfers.Where(row => row.Username == filter.Username);
        if (filter.GroupRef is not null)
            transfers = transfers.Where(row => row.GroupRef == filter.GroupRef);
        if (filter.FromUtc.HasValue)
        {
            long from = filter.FromUtc.Value.ToUniversalTime().ToUnixTimeMilliseconds();
            transfers = transfers.Where(row => row.CreatedAtUtc >= from);
        }
        if (filter.ToUtc.HasValue)
        {
            long to = filter.ToUtc.Value.ToUniversalTime().ToUnixTimeMilliseconds();
            transfers = transfers.Where(row => row.CreatedAtUtc <= to);
        }
        return transfers;
    }

    private static PersistedTransfer MapTransfer(Entities.TransferEntity transfer)
        => new(
            transfer.Id, transfer.JobId, transfer.WorkflowId, transfer.Direction, transfer.Source,
            transfer.Username, transfer.RemotePath, transfer.LocalPath, transfer.State, transfer.TerminalOutcome,
            transfer.TotalBytes == long.MaxValue ? null : transfer.TotalBytes,
            transfer.TransferredBytes, transfer.AttemptCount,
            DateTimeOffset.FromUnixTimeMilliseconds(transfer.CreatedAtUtc), FromUnix(transfer.CompletedAtUtc),
            transfer.FailureReason, transfer.FailureMessage, transfer.CancellationSource, transfer.Revision,
            FromUnix(transfer.StartedAtUtc),
            FromUnix(transfer.LastProgressAtUtc),
            transfer.BytesPerSecond,
            MapFile(transfer),
            transfer.GroupRef,
            transfer.GroupDisplayPath,
            FromUnix(transfer.ArchivedAtUtc),
            transfer.GroupRelativePath);

    private static TransferFileMetadataSnapshot? MapFile(
        Entities.TransferEntity transfer)
    {
        if (transfer.FileName is null || transfer.FileSizeBytes is null)
            return null;
        IReadOnlyList<FileAttributeSnapshot>? attributes = transfer.FileAttributesJson is null
            ? null
            : JsonSerializer.Deserialize<FileAttributeSnapshot[]>(transfer.FileAttributesJson);
        return new TransferFileMetadataSnapshot(
            transfer.FileName,
            transfer.FileSizeBytes.Value,
            transfer.FileExtension,
            transfer.FileBitRate,
            transfer.FileBitDepth,
            transfer.FileSampleRate,
            transfer.FileLength,
            attributes);
    }

    private static PersistedTransferAttempt MapAttempt(Entities.TransferAttemptEntity attempt)
        => new(
            attempt.Id, attempt.TransferId, attempt.AttemptNumber, attempt.Source, attempt.State,
            attempt.SourceUsername, attempt.SourcePath, attempt.OutputPath,
            DateTimeOffset.FromUnixTimeMilliseconds(attempt.StartedAtUtc), FromUnix(attempt.CompletedAtUtc),
            attempt.FailureReason, attempt.FailureMessage, attempt.Revision);

    public static string EncodeCursor(long createdAtUtc, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAtUtc}:{id:N}"));

    public static TransferCursorValue? DecodeCursor(string? cursor)
    {
        if (cursor is null) return null;
        if (cursor.Length > 128)
            throw new ArgumentException("The transfer cursor is malformed.", nameof(cursor));
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int separator = decoded.IndexOf(':');
            if (separator <= 0
                || !long.TryParse(decoded[..separator], System.Globalization.CultureInfo.InvariantCulture, out long created)
                || !Guid.TryParseExact(decoded[(separator + 1)..], "N", out Guid id))
                throw new FormatException();
            return new TransferCursorValue(created, id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("The transfer cursor is malformed.", nameof(cursor), ex);
        }
    }

    private static DateTimeOffset? FromUnix(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    public readonly record struct TransferCursorValue(long CreatedAtUtc, Guid Id);
}
