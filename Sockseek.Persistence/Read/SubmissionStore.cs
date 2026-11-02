using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Read;

public sealed record SubmissionHistoryQuery(
    string? Cursor = null,
    int Limit = 100,
    bool Archived = false);

public record PersistedSubmissionSummary(
    Guid Id,
    DateTimeOffset SubmittedAtUtc,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
    long Revision,
    DateTimeOffset? ArchivedAtUtc,
    int TotalJobCount,
    int UserRootJobCount,
    int ActiveJobCount,
    int FailedJobCount);

public sealed record PersistedSubmission(
    Guid Id,
    DateTimeOffset SubmittedAtUtc,
    int SpecificationSchemaVersion,
    string SpecificationJson,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
    string? CommitFingerprint,
    string? CommitReceiptJson,
    long Revision,
    DateTimeOffset? ArchivedAtUtc,
    int TotalJobCount,
    int UserRootJobCount,
    int ActiveJobCount,
    int FailedJobCount) : PersistedSubmissionSummary(
        Id, SubmittedAtUtc, RerunOfSubmissionId, PreviewId, ArtifactId,
        Revision, ArchivedAtUtc, TotalJobCount, UserRootJobCount,
        ActiveJobCount, FailedJobCount);

public sealed record PersistedSubmissionPage(
    IReadOnlyList<PersistedSubmissionSummary> Items,
    string? NextCursor);

public sealed record SubmissionArchiveResult(
    Guid SubmissionId,
    bool Archived,
    int AffectedSubmissionCount,
    int AffectedJobCount,
    int RejectedSubmissionCount,
    string? RejectionReason);

public sealed record SubmissionRegistration(
    Guid SubmissionId,
    DateTimeOffset SubmittedAtUtc,
    int SpecificationSchemaVersion,
    string SpecificationJson,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
    string? CommitFingerprint = null,
    string? CommitReceiptJson = null);

public sealed record PersistedSubmissionCommitReceipt(
    string? CommitFingerprint,
    string? CommitReceiptJson);

public interface ISubmissionStore
{
    Task CreateAsync(
        SubmissionRegistration registration,
        CancellationToken cancellationToken = default);
    Task<PersistedSubmissionPage> GetSubmissionsAsync(
        SubmissionHistoryQuery query,
        CancellationToken cancellationToken = default);
    Task<PersistedSubmission?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);
    Task<string?> GetSpecificationJsonAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);
    Task<PersistedSubmissionCommitReceipt?> GetCommitReceiptAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);
    Task<SubmissionArchiveResult> SetArchivedAsync(
        Guid submissionId,
        bool archived,
        CancellationToken cancellationToken = default);
}

public sealed class SubmissionStore(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceInbox inbox,
    TimeProvider? timeProvider = null) : ISubmissionStore
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task CreateAsync(
        SubmissionRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.SubmissionId == Guid.Empty)
            throw new ArgumentException("Submission ID cannot be empty.", nameof(registration));
        if (registration.SpecificationSchemaVersion <= 0
            || string.IsNullOrWhiteSpace(registration.SpecificationJson))
            throw new ArgumentException("Submission specification is required.", nameof(registration));
        if ((registration.CommitFingerprint == null) != (registration.CommitReceiptJson == null)
            || registration.CommitFingerprint != null && string.IsNullOrWhiteSpace(registration.CommitFingerprint)
            || registration.CommitReceiptJson != null && string.IsNullOrWhiteSpace(registration.CommitReceiptJson))
            throw new ArgumentException("An idempotent acceptance requires both its fingerprint and receipt.", nameof(registration));

        var command = new AwaitablePersistenceCommand<bool>(async (context, ct) =>
        {
            var existing = await context.Submissions.Include(item => item.Specification)
                .SingleOrDefaultAsync(item => item.Id == registration.SubmissionId, ct)
                .ConfigureAwait(false);
            if (existing != null)
            {
                if (existing.SpecificationSchemaVersion != registration.SpecificationSchemaVersion
                    || !string.Equals(
                        existing.Specification.Json,
                        registration.SpecificationJson,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        existing.CommitFingerprint,
                        registration.CommitFingerprint,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        existing.CommitReceiptJson,
                        registration.CommitReceiptJson,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Submission {registration.SubmissionId:N} is already registered with a different specification.");
                }
                return false;
            }

            context.Submissions.Add(new SubmissionEntity
            {
                Id = registration.SubmissionId,
                SubmittedAtUtc = registration.SubmittedAtUtc
                    .ToUniversalTime().ToUnixTimeMilliseconds(),
                SpecificationSchemaVersion = registration.SpecificationSchemaVersion,
                Specification = new() { Json = registration.SpecificationJson },
                RerunOfSubmissionId = registration.RerunOfSubmissionId,
                PreviewId = registration.PreviewId,
                ArtifactId = registration.ArtifactId,
                CommitFingerprint = registration.CommitFingerprint,
                CommitReceiptJson = registration.CommitReceiptJson,
                Revision = 1,
            });
            return true;
        });
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        await command.Task.ConfigureAwait(false);
    }

    public async Task<PersistedSubmissionPage> GetSubmissionsAsync(
        SubmissionHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(query), "Page size must be positive.");
        CursorValue? cursor = DecodeCursor(query.Cursor);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        using var command = ((SqliteConnection)context.Database.GetDbConnection()).CreateCommand();
        command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
        // Select only the page's scalar headers. Accepted commands/receipts live
        // outside this read, and fixed SQL needs no first-use query compilation.
        command.CommandText = $"""
            SELECT id, submitted_at_utc, rerun_of_submission_id, preview_id,
                artifact_id, revision, archived_at_utc
            FROM submissions
            WHERE archived_at_utc IS {(query.Archived ? "NOT NULL" : "NULL")}
                {(cursor is null ? "" : "AND (submitted_at_utc < $time OR (submitted_at_utc = $time AND id < $id))")}
            ORDER BY submitted_at_utc DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit == int.MaxValue ? query.Limit : query.Limit + 1);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$time", cursor.SubmittedAtUtc);
            command.Parameters.AddWithValue("$id", cursor.Id.ToString().ToUpperInvariant());
        }
        var rows = new List<PersistedSubmissionSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new(reader.GetGuid(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)), 0, 0, 0, 0));
        bool hasMore = rows.Count > query.Limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        var items = await WithJobCountsAsync(context, transaction, rows, cancellationToken).ConfigureAwait(false);
        string? next = hasMore && rows.Count > 0
            ? EncodeCursor(rows[^1].SubmittedAtUtc.ToUnixTimeMilliseconds(), rows[^1].Id)
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersistedSubmissionPage(items, next);
    }

    public async Task<PersistedSubmission?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var row = await context.Submissions.AsNoTracking().Include(item => item.Specification)
            .SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (row == null)
            return null;
        var submission = new PersistedSubmission(
            row.Id,
            DateTimeOffset.FromUnixTimeMilliseconds(row.SubmittedAtUtc),
            row.SpecificationSchemaVersion,
            row.Specification.Json,
            row.RerunOfSubmissionId,
            row.PreviewId,
            row.ArtifactId,
            row.CommitFingerprint,
            row.CommitReceiptJson,
            row.Revision,
            row.ArchivedAtUtc.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(row.ArchivedAtUtc.Value)
                : null,
            0, 0, 0, 0);
        var counted = (await WithJobCountsAsync<PersistedSubmission>(context, transaction, [submission], cancellationToken)
            .ConfigureAwait(false))[0];
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counted;
    }

    public async Task<string?> GetSpecificationJsonAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Submissions.AsNoTracking()
            .Where(item => item.Id == submissionId)
            .Select(item => item.Specification.Json)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistedSubmissionCommitReceipt?> GetCommitReceiptAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Submissions.AsNoTracking()
            .Where(item => item.Id == submissionId)
            .Select(item => new PersistedSubmissionCommitReceipt(
                item.CommitFingerprint, item.CommitReceiptJson))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }


    public async Task<SubmissionArchiveResult> SetArchivedAsync(
        Guid submissionId,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        var command = new AwaitablePersistenceCommand<SubmissionArchiveResult>(async (context, ct) =>
        {
            var submission = await context.Submissions
                .Where(item => item.Id == submissionId)
                .Select(item => new { item.ArchivedAtUtc, item.Revision })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (submission == null)
                return new(submissionId, archived, 0, 0, 1, "not-found");

            int totalJobs = await context.Jobs
                .CountAsync(job => job.SubmissionId == submissionId, ct)
                .ConfigureAwait(false);
            if (archived)
            {
                int activeJobs = await context.Jobs
                    .CountAsync(
                        job => job.SubmissionId == submissionId
                            && job.LifecycleState != "Terminal",
                        ct)
                    .ConfigureAwait(false);
                if (activeJobs > 0)
                    return new(submissionId, true, 0, 0, 1, "nonterminal-jobs");
            }

            bool changed = archived
                ? submission.ArchivedAtUtc == null
                : submission.ArchivedAtUtc != null;
            if (changed)
            {
                var entry = context.Submissions.Attach(new SubmissionEntity
                {
                    Id = submissionId,
                    ArchivedAtUtc = archived ? clock.GetUtcNow().ToUnixTimeMilliseconds() : null,
                    Revision = checked(submission.Revision + 1),
                });
                entry.Property(item => item.ArchivedAtUtc).IsModified = true;
                entry.Property(item => item.Revision).IsModified = true;
            }
            return new(submissionId, archived, changed ? 1 : 0, changed ? totalJobs : 0, 0, null);
        });
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        // Once admitted, archive/unarchive is durable intent owned by the writer.
        return await command.Task.ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<T>> WithJobCountsAsync<T>(
        SockseekDbContext context,
        SqliteReadSnapshot transaction,
        IReadOnlyList<T> rows,
        CancellationToken cancellationToken) where T : PersistedSubmissionSummary
    {
        if (rows.Count == 0)
            return [];
        using var command = ((SqliteConnection)context.Database.GetDbConnection()).CreateCommand();
        command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
        // One indexed traversal for all selected submissions, including pages
        // containing roots with many descendants. Never read accepted payloads.
        command.CommandText = """
            SELECT job.submission_id, COUNT(*),
                COUNT(*) FILTER (WHERE job.semantic_role = 'UserRoot'),
                COUNT(*) FILTER (WHERE job.lifecycle_state != 'Terminal'),
                COUNT(*) FILTER (WHERE job.terminal_outcome = 'Failed')
            FROM json_each($ids) requested CROSS JOIN jobs job
            WHERE job.submission_id = requested.value
            GROUP BY job.submission_id;
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(rows.Select(row => row.Id.ToString().ToUpperInvariant())));
        var counts = new Dictionary<Guid, (int Total, int UserRoots, int Active, int Failed)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                counts.Add(reader.GetGuid(0), (reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));

        return rows.Select(row =>
        {
            counts.TryGetValue(row.Id, out var count);
            // Record cloning preserves the full retained payload for single-item
            // reads without making history pages hydrate that payload as well.
            return (T)((PersistedSubmissionSummary)row with
            {
                TotalJobCount = count.Total,
                UserRootJobCount = count.UserRoots,
                ActiveJobCount = count.Active,
                FailedJobCount = count.Failed,
            });
        }).ToArray();
    }

    private static string EncodeCursor(long submittedAtUtc, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{submittedAtUtc}:{id:D}"));

    private static CursorValue? DecodeCursor(string? cursor)
    {
        if (cursor == null)
            return null;
        if (cursor.Length > 128)
            throw new ArgumentException("The submission cursor is malformed.", nameof(cursor));
        try
        {
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int separator = value.IndexOf(':');
            if (separator <= 0
                || !long.TryParse(value[..separator], out long submittedAtUtc)
                || !Guid.TryParse(value[(separator + 1)..], out Guid id))
                throw new FormatException();
            return new CursorValue(submittedAtUtc, id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("The submission cursor is malformed.", nameof(cursor), ex);
        }
    }

    private sealed record CursorValue(long SubmittedAtUtc, Guid Id);
}
