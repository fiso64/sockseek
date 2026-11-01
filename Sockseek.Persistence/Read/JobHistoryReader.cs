using Sockseek.Core.Models;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore.Storage;
using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.Read;

public sealed record JobHistoryQuery(
    string? Cursor = null,
    int Limit = 100,
    string? LifecycleState = null,
    string? TerminalOutcome = null,
    string? SkipReason = null,
    string? Kind = null,
    Guid? WorkflowId = null,
    bool IncludeAll = false,
    Guid? ParentJobId = null,
    Guid? SubmissionId = null,
    string? SemanticRole = null,
    bool Archived = false,
    bool NewestFirst = false,
    bool ExcludeExtractResults = false,
    IReadOnlyCollection<Guid>? ExcludedJobIds = null);

public sealed record PersistedJob(
    Guid Id,
    long DisplayId,
    Guid WorkflowId,
    Guid? ParentJobId,
    Guid? SourceJobId,
    Guid? ResultJobId,
    string Kind,
    string LifecycleState,
    string ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    string TerminalOutcome,
    string SkipReason,
    string CancellationSource,
    string FailureReason,
    string? FailureMessage,
    string? FailureDetail,
    string? ItemName,
    string? QueryText,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long Revision,
    int PayloadSchemaVersion,
    string? PayloadJson,
    Guid? SubmissionId = null,
    string SemanticRole = "Legacy",
    long? DiscoveryPublicFileCount = null,
    long? DiscoveryLockedFileCount = null,
    long? DiscoveryObservedPeerCount = null,
    string? SourceDetail = null);

public sealed record PersistedJobPage(IReadOnlyList<PersistedJob> Items, string? NextCursor);
public sealed record PersistedDirectoryManifestEntry(
    int Ordinal,
    PeerFileTarget? Target,
    IReadOnlyList<string> RelativeDirectoryComponents,
    Guid? ChildJobId,
    PersistedJob? Child = null);
public sealed record PersistedDirectoryManifestPage(
    int AttemptNumber,
    string DisplayRoot,
    int EntryCount,
    long TotalKnownBytes,
    bool ChildrenMaterialized,
    int SupplementalEntryCount,
    long JobRevision,
    IReadOnlyList<PersistedDirectoryManifestEntry> Items,
    int? NextOrdinal);
public sealed record PersistedWorkflowSummary(
    Guid WorkflowId,
    long FirstDisplayId,
    string Title,
    string State,
    int RootJobCount,
    int ActiveJobCount,
    int FailedJobCount,
    int CompletedJobCount);
public sealed record PersistedWorkflowPage(IReadOnlyList<PersistedWorkflowSummary> Items, string? NextCursor);

public interface IJobHistoryReader
{
    Task<PersistedJobPage> GetJobsAsync(JobHistoryQuery query, CancellationToken cancellationToken = default);
    Task<PersistedJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersistedJob>> GetJobsByIdsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default);
    Task<PersistedDirectoryManifestPage?> GetDirectoryManifestAsync(
        Guid jobId, int? attemptNumber, int afterOrdinal, int limit,
        CancellationToken cancellationToken = default);
    Task<int> GetChildCountAsync(Guid parentJobId, CancellationToken cancellationToken = default);
    Task<PersistedWorkflowPage> GetWorkflowsAsync(string? cursor = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<PersistedWorkflowSummary?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<PersistedJob?> GetJobByDisplayIdAsync(Guid workflowId, long displayId, CancellationToken cancellationToken = default);
}

public sealed class JobHistoryReader(IDbContextFactory<SockseekDbContext> contextFactory) : IJobHistoryReader
{

    public async Task<PersistedDirectoryManifestPage?> GetDirectoryManifestAsync(
        Guid jobId, int? attemptNumber, int afterOrdinal, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterOrdinal < -1)
            throw new ArgumentOutOfRangeException(nameof(afterOrdinal));
        if (attemptNumber is < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();
        await using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction.GetDbTransaction();
        metadata.CommandText = """
            SELECT attempt_number, display_root, entry_count, total_known_bytes,
                   children_materialized, json_array_length(supplemental_job_ids_json), job_revision
            FROM directory_manifests
            WHERE job_id = $job AND attempt_number = COALESCE($attempt, (
                SELECT json_extract(payload_json, '$.Directory.AttemptNumber')
                FROM jobs WHERE id = $job))
            ORDER BY attempt_number DESC LIMIT 1;
            """;
        var jobKey = jobId;
        var job = metadata.CreateParameter();
        job.ParameterName = "$job";
        job.Value = jobKey;
        metadata.Parameters.Add(job);
        var attempt = metadata.CreateParameter();
        attempt.ParameterName = "$attempt";
        attempt.Value = attemptNumber is { } requested ? requested : DBNull.Value;
        metadata.Parameters.Add(attempt);
        int number;
        string root;
        int count;
        long bytes;
        bool materialized;
        int supplementalCount;
        long revision;
        await using (var row = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            number = row.GetInt32(0);
            root = row.GetString(1);
            count = row.GetInt32(2);
            bytes = row.GetInt64(3);
            materialized = row.GetInt32(4) != 0;
            supplementalCount = row.GetInt32(5);
            revision = row.GetInt64(6);
        }

        await using var entries = connection.CreateCommand();
        entries.Transaction = transaction.GetDbTransaction();
        entries.CommandText = """
            SELECT ordinal, target_json, relative_directory_components_json, child_job_id
            FROM directory_manifest_entries
            WHERE job_id = $job AND attempt_number = $attempt AND ordinal > $after
            UNION ALL
            SELECT $count + CAST(supplemental.key AS INTEGER) AS ordinal,
                   NULL AS target_json, '[]' AS relative_directory_components_json,
                   supplemental.value AS child_job_id
            FROM directory_manifests manifest, json_each(manifest.supplemental_job_ids_json) supplemental
            WHERE manifest.job_id = $job AND manifest.attempt_number = $attempt
              AND $count + CAST(supplemental.key AS INTEGER) > $after
              AND NOT EXISTS (
                  SELECT 1 FROM directory_manifest_entries entry
                  WHERE entry.job_id = manifest.job_id AND entry.attempt_number = manifest.attempt_number
                    AND entry.ordinal = $count + CAST(supplemental.key AS INTEGER))
            ORDER BY ordinal LIMIT $limit;
            """;
        foreach (var (name, value) in new (string, object)[] {
            ("$job", jobKey), ("$attempt", number), ("$after", afterOrdinal), ("$limit", (long)limit + 1), ("$count", count) })
        {
            var parameter = entries.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            entries.Parameters.Add(parameter);
        }
        var items = new List<PersistedDirectoryManifestEntry>();
        await using (var rows = await entries.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new PersistedDirectoryManifestEntry(
                    rows.GetInt32(0),
                    rows.IsDBNull(1) ? null : JsonSerializer.Deserialize<PeerFileTarget>(rows.GetString(1)),
                    JsonSerializer.Deserialize<string[]>(rows.GetString(2)) ?? [],
                    rows.IsDBNull(3) ? null : Guid.Parse(rows.GetString(3))));
            }
        }
        bool more = items.Count > limit;
        if (more) items.RemoveAt(items.Count - 1);
        Guid[] childIds = items.Where(item => item.ChildJobId.HasValue)
            .Select(item => item.ChildJobId!.Value).Distinct().ToArray();
        if (childIds.Length > 0)
        {
            var children = (await ReadRowsByIdsAsync(context, childIds, cancellationToken)
                .ConfigureAwait(false)).ToDictionary(child => child.Id);
            items = items.Select(item => item with
            {
                Child = item.ChildJobId is Guid id ? children.GetValueOrDefault(id) : null,
            }).ToList();
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersistedDirectoryManifestPage(
            number, root, count, bytes, materialized, supplementalCount, revision,
            items, more ? items[^1].Ordinal : null);
    }

    public async Task<PersistedJobPage> GetJobsAsync(JobHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(query), "Page size must be positive.");
        var cursor = DecodeCursor(query.Cursor);
        if (cursor is { } position && position.NewestFirst != query.NewestFirst)
            throw new ArgumentException("The job cursor has a different sort direction.", nameof(query));

        var conditions = new List<string>();
        var parameters = new List<object>();
        void Filter(string predicate, string parameter, object? value)
        {
            if (value is null) return;
            conditions.Add(predicate);
            parameters.Add(new SqliteParameter(parameter, value));
        }
        if (query.ExcludedJobIds is { Count: > 0 } excluded)
        {
            // Runtime owns captured identities until handoff, including rows
            // that no longer match. Exclude before filtering and paging.
            Filter("job.id NOT IN (SELECT value FROM json_each($excluded))", "$excluded", EncodeIds(excluded));
        }
        const string archived = """
            EXISTS (SELECT 1 FROM submissions submission
                WHERE submission.id=job.submission_id AND submission.archived_at_utc IS NOT NULL)
            """;
        conditions.Add(query.Archived ? archived : "NOT " + archived);
        if (query.ParentJobId is Guid parent)
            Filter("job.parent_job_id=$parent", "$parent", parent);
        else if (!query.IncludeAll)
            conditions.Add("job.parent_job_id IS NULL");
        if (query.ExcludeExtractResults)
        {
            // Keep workflow comparison outside each identity lookup. Including
            // it inside EXISTS lets SQLite choose a broad workflow scan
            // instead of the source/result indexes, even without an OR.
            conditions.Add("""
                job.workflow_id NOT IN (SELECT source.workflow_id FROM jobs source
                    WHERE source.id=job.source_job_id AND source.kind='Extract')
                AND job.workflow_id NOT IN (SELECT source.workflow_id FROM jobs source
                    WHERE source.result_job_id=job.id AND source.kind='Extract')
                """);
        }
        Filter("job.lifecycle_state=$state", "$state", query.LifecycleState);
        Filter("job.terminal_outcome=$outcome", "$outcome", query.TerminalOutcome);
        Filter("job.skip_reason=$skip", "$skip", query.SkipReason);
        Filter("job.kind=$kind", "$kind", query.Kind);
        Filter("job.workflow_id=$workflow", "$workflow", query.WorkflowId);
        Filter("job.submission_id=$submission", "$submission", query.SubmissionId);
        Filter("job.semantic_role=$role", "$role", query.SemanticRole);
        if (cursor is not null)
        {
            string comparison = query.NewestFirst ? "<" : ">";
            conditions.Add($"(job.display_id,job.id){comparison}($display,$id)");
            parameters.Add(new SqliteParameter("$display", cursor.DisplayId));
            parameters.Add(new SqliteParameter("$id", cursor.Id));
        }
        string order = query.NewestFirst ? "DESC" : "ASC";
        parameters.Add(new SqliteParameter("$limit", query.Limit == int.MaxValue ? query.Limit : query.Limit + 1));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadRowsAsync(context, "FROM jobs job", $"""
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY job.display_id {order},job.id {order} LIMIT $limit
            """, parameters, cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? EncodeCursor(rows[^1].DisplayId, rows[^1].Id, query.NewestFirst) : null);
    }

    public async Task<IReadOnlyList<PersistedJob>> GetJobsByIdsAsync(
        IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default)
    {
        if (jobIds.Count == 0) return [];
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRowsByIdsAsync(context, jobIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistedJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadRowsAsync(context, "FROM jobs job", "WHERE job.id=$id",
            [new SqliteParameter("$id", jobId)], cancellationToken).ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    public async Task<int> GetChildCountAsync(Guid parentJobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await SqliteReadQuery.ReadAsync(context,
            "SELECT COUNT(*) FROM jobs WHERE parent_job_id=$parent",
            [new SqliteParameter("$parent", parentJobId)], row => row.GetInt32(0), cancellationToken).ConfigureAwait(false);
        return rows.Single();
    }

    public async Task<PersistedWorkflowPage> GetWorkflowsAsync(
        string? cursor = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var decoded = DecodeCursor(cursor);
        if (decoded?.NewestFirst == true)
            throw new ArgumentException("The workflow cursor has a different sort direction.", nameof(cursor));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new List<object> { new SqliteParameter("$limit", limit == int.MaxValue ? limit : limit + 1) };
        if (decoded is not null) parameters.Add(new SqliteParameter("$after", decoded.Id));
        var rows = await ReadWorkflowsAsync(context, $"""
            SELECT workflow_id FROM jobs {(decoded is null ? "" : "WHERE workflow_id>$after")}
            GROUP BY workflow_id ORDER BY workflow_id LIMIT $limit
            """, parameters, cancellationToken).ConfigureAwait(false);
        bool more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows, more && rows.Count > 0 ? EncodeCursor(0, rows[^1].WorkflowId) : null);
    }

    public async Task<PersistedWorkflowSummary?> GetWorkflowAsync(
        Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadWorkflowsAsync(context,
            "SELECT workflow_id FROM jobs WHERE workflow_id=$workflow GROUP BY workflow_id",
            [new SqliteParameter("$workflow", workflowId)], cancellationToken).ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    public async Task<PersistedJob?> GetJobByDisplayIdAsync(
        Guid workflowId, long displayId, CancellationToken cancellationToken = default)
    {
        if (displayId <= 0) throw new ArgumentOutOfRangeException(nameof(displayId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadRowsAsync(context, "FROM jobs job", "WHERE job.workflow_id=$workflow AND job.display_id=$display",
            [new SqliteParameter("$workflow", workflowId), new SqliteParameter("$display", displayId)], cancellationToken)
            .ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    private static Task<List<PersistedJob>> ReadRowsByIdsAsync(
        SockseekDbContext context, IEnumerable<Guid> ids, CancellationToken cancellationToken)
        // Requested identities drive seeks, irrespective of ordered history size.
        => ReadRowsAsync(context, "FROM json_each($ids) requested CROSS JOIN jobs job",
            "WHERE job.id=requested.value", [new SqliteParameter("$ids", EncodeIds(ids))], cancellationToken);

    private static Task<List<PersistedJob>> ReadRowsAsync(
        SockseekDbContext context, string from, string suffix, IEnumerable<object> parameters,
        CancellationToken cancellationToken)
    {
        // One scalar mapper belongs to the existing EF model and serves writes,
        // lists, details, explicit selections and manifest children. Join counters
        // in the same statement: no per-query LINQ compilation or second snapshot.
        var shape = HistoryRowMapping<JobEntity>.For(context);
        return SqliteReadQuery.ReadAsync(context, $"""
            SELECT {shape.SelectColumns("job")},search.result_count,search.locked_file_count,search.observed_peer_count
            {from} LEFT JOIN search_jobs search ON job.kind='Search' AND search.job_id=job.id
            {suffix}
            """, parameters, row =>
            {
                int start = shape.Properties.Length;
                return Map(shape.Read(row), row.IsDBNull(start) ? null : row.GetInt64(start),
                    row.IsDBNull(start + 1) ? null : row.GetInt64(start + 1),
                    row.IsDBNull(start + 2) ? null : row.GetInt64(start + 2));
            }, cancellationToken);
    }

    private static string EncodeIds(IEnumerable<Guid> ids)
        => JsonSerializer.Serialize(ids.Distinct().Select(id => id.ToString("D").ToUpperInvariant()));

    private static Task<List<PersistedWorkflowSummary>> ReadWorkflowsAsync(
        SockseekDbContext context, string selected, IEnumerable<object> parameters,
        CancellationToken cancellationToken)
        // Page workflow identities first; aggregate only those workflows. Titles
        // retain the earliest nonempty item name, otherwise the first query/kind.
        // Aggregate in workflow-index order without a temporary grouping tree;
        // discover the first named job in that same pass, then seek its row.
        => SqliteReadQuery.ReadAsync(context, $"""
            WITH selected AS MATERIALIZED ({selected}),
            totals AS (
                SELECT job.workflow_id,MIN(job.display_id) AS first_display_id,
                    SUM(job.parent_job_id IS NULL) AS root_count,
                    SUM(job.lifecycle_state<>'Terminal') AS active_count,
                    SUM(job.terminal_outcome IN ('Failed','Cancelled','PartialSuccess')
                        OR job.terminal_outcome='Skipped' AND job.skip_reason<>'AlreadyExists') AS failed_count,
                    SUM(job.lifecycle_state='Terminal') AS completed_count,
                    MIN(CASE WHEN job.item_name<>'' THEN job.display_id END) AS first_item_display_id
                FROM jobs job WHERE job.workflow_id IN (SELECT workflow_id FROM selected)
                GROUP BY job.workflow_id)
            SELECT totals.workflow_id,totals.first_display_id,totals.root_count,
                totals.active_count,totals.failed_count,totals.completed_count,
                COALESCE(named.item_name,first.query_text,first.kind)
            FROM totals CROSS JOIN jobs first ON first.display_id=totals.first_display_id
            LEFT JOIN jobs named ON named.display_id=totals.first_item_display_id
            ORDER BY totals.workflow_id
            """, parameters, row => new PersistedWorkflowSummary(
                row.GetGuid(0), row.GetInt64(1), row.GetString(6),
                row.GetInt32(3) > 0 ? "Active" : row.GetInt32(4) > 0 ? "Failed" : "Completed",
                row.GetInt32(2), row.GetInt32(3), row.GetInt32(4), row.GetInt32(5)), cancellationToken);

    private static PersistedJob Map(JobEntity job, long? publicCount, long? lockedCount, long? peerCount)
        => new(
            job.Id, job.DisplayId, job.WorkflowId, job.ParentJobId, job.SourceJobId, job.ResultJobId,
            job.Kind, job.LifecycleState, job.ActivityPhase, FromUnixMilliseconds(job.ActivityUntilUtc),
            job.TerminalOutcome, job.SkipReason, job.CancellationSource, job.FailureReason,
            job.FailureMessage, job.FailureDetail, job.ItemName, job.QueryText,
            DateTimeOffset.FromUnixTimeMilliseconds(job.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(job.UpdatedAtUtc), FromUnixMilliseconds(job.CompletedAtUtc),
            job.Revision, job.PayloadSchemaVersion, job.PayloadJson, job.SubmissionId, job.SemanticRole,
            publicCount, lockedCount, peerCount, job.SourceDetail);

    private static DateTimeOffset? FromUnixMilliseconds(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    public static CursorValue? DecodeCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length > 128)
            throw new ArgumentException("The job cursor is malformed.", nameof(cursor));
        if (string.IsNullOrWhiteSpace(cursor))
            return null;
        try
        {
            string padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            bool newestFirst = value.StartsWith("d:", StringComparison.Ordinal);
            string[] parts = (newestFirst ? value[2..] : value).Split(':', 2);
            return new CursorValue(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Guid.ParseExact(parts[1], "N"), newestFirst);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException("The job cursor is malformed.", nameof(cursor), ex);
        }
    }

    public static string EncodeCursor(long displayId, Guid id, bool newestFirst = false)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{(newestFirst ? "d:" : "")}{displayId}:{id:N}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public sealed record CursorValue(long DisplayId, Guid Id, bool NewestFirst = false);

}
