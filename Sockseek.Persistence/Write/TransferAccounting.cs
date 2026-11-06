using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Write;

/// <summary>
/// One writer batch owns checkpoint replay, bucket lookups and coverage state.
/// Prepare positive and negative lookups before the transaction; applying an
/// observation does no SQL or EF lookup. Buckets can span many peers/times: keep
/// ordinary reads untracked and reuse prepared writes for the changed buckets.
/// Only the coverage singleton and exceptional retries use EF tracking.
/// </summary>
internal sealed class TransferAccounting(
    SockseekDbContext context,
    HistoryRows<TransferAccountingCheckpointEntity> checkpoints,
    Dictionary<TransferAccounting.BucketKey, TransferByteBucketEntity> buckets,
    TransferAccountingStateEntity? state,
    bool tracked)
{
    private TransferAccountingStateEntity? state = state;
    private readonly Dictionary<BucketKey, long> originalBuckets = tracked ? []
        : buckets.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);

    internal readonly record struct BucketKey(long Start, string Direction, string Username)
    {
        public static BucketKey From(DateTimeOffset observedAt, string direction, string? username)
        {
            long time = observedAt.ToUniversalTime().ToUnixTimeMilliseconds();
            Math.DivRem(time, PersistenceWriter.AccountingBucketMilliseconds, out long remainder);
            return new(time - remainder, direction, username ?? "");
        }
    }

    public static async Task<TransferAccounting> LoadAsync(
        SockseekDbContext context, IReadOnlyList<TransferPersistenceMutation> transfers,
        IReadOnlyList<TransferAttemptPersistenceMutation> attempts, bool tracked,
        CancellationToken cancellationToken)
    {
        Guid[] ids = transfers.SelectMany(row => row.AccountingObservations ?? [])
            .Concat(attempts.SelectMany(row => row.AccountingObservations ?? []))
            .Select(row => row.AttemptId).Distinct().ToArray();
        var checkpoints = await HistoryRows<TransferAccountingCheckpointEntity>.LoadAsync(
            context, ids, tracked, cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return new(context, checkpoints, [], null, tracked);

        var keys = new HashSet<BucketKey>();
        void Collect(string direction, string? username, IReadOnlyList<TransferAccountingObservation>? observations)
        {
            if (observations is null) return;
            foreach (var observation in observations)
                if (observation.CumulativeBytes > 0
                    && (!checkpoints.TryGetValue(observation.AttemptId, out var previous)
                        || observation.Revision > previous!.Revision))
                    keys.Add(BucketKey.From(observation.OccurredAtUtc, direction, username));
        }
        foreach (var transfer in transfers) Collect(transfer.Direction, transfer.Username, transfer.AccountingObservations);
        foreach (var attempt in attempts) Collect(attempt.Direction, attempt.SourceUsername, attempt.AccountingObservations);

        Dictionary<BucketKey, TransferByteBucketEntity> buckets = [];
        if (keys.Count > 0)
        {
            // Seek each distinct composite key through the bucket primary key.
            // One JSON parameter also works for large backlogs of observations.
            var parameter = new SqliteParameter("$keys", JsonSerializer.Serialize(keys));
            const string sql = """
                SELECT bucket.bucket_start_utc, bucket.direction, bucket.username, bucket.bytes
                FROM json_each($keys) requested
                JOIN transfer_byte_buckets bucket
                  ON bucket.bucket_start_utc = json_extract(requested.value, '$.Start')
                 AND bucket.direction = json_extract(requested.value, '$.Direction')
                 AND bucket.username = json_extract(requested.value, '$.Username')
                """;
            if (tracked)
                buckets = await context.TransferByteBuckets.FromSqlRaw(sql, parameter).AsTracking()
                    .ToDictionaryAsync(row => new BucketKey(row.BucketStartUtc, row.Direction, row.Username), cancellationToken)
                    .ConfigureAwait(false);
            else
            {
                await using var result = await SqliteReadQuery.OpenAsync(
                    context, sql, [parameter], cancellationToken).ConfigureAwait(false);
                var reader = result.DbDataReader;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new TransferByteBucketEntity { BucketStartUtc = reader.GetInt64(0),
                        Direction = reader.GetString(1), Username = reader.GetString(2), Bytes = reader.GetInt64(3) };
                    buckets.Add(new(row.BucketStartUtc, row.Direction, row.Username), row);
                }
            }
        }
        var state = await context.TransferAccountingStates.FindAsync([1], cancellationToken).ConfigureAwait(false);
        return new(context, checkpoints, buckets, state, tracked);
    }

    public int Apply(Guid transferId, string direction, string? username,
        IReadOnlyList<TransferAccountingObservation>? observations)
    {
        if (observations is not { Count: > 0 }) return 0;
        int changed = 0;
        if (observations.Count == 1)
            changed = ApplyObservation(transferId, direction, username, observations[0]);
        else
            foreach (var observation in observations.OrderBy(row => row.OccurredAtUtc).ThenBy(row => row.Revision))
                changed += ApplyObservation(transferId, direction, username, observation);

        long first = long.MaxValue, last = long.MinValue;
        foreach (var observation in observations)
        {
            long time = observation.OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            first = Math.Min(first, time);
            last = Math.Max(last, time);
        }
        if (state is null)
        {
            state = new() { Id = 1, CompleteFromUtc = first, UpdatedAtUtc = last };
            context.TransferAccountingStates.Add(state);
            changed++;
        }
        else state.UpdatedAtUtc = Math.Max(state.UpdatedAtUtc, last);
        return changed;
    }

    private int ApplyObservation(Guid transferId, string direction, string? username,
        TransferAccountingObservation observation)
    {
        checkpoints.TryGetValue(observation.AttemptId, out var checkpoint);
        if (checkpoint is not null && checkpoint.Revision >= observation.Revision) return 0;
        int changed = 0;
        long cumulative = Math.Max(0, observation.CumulativeBytes);
        long delta = checkpoint is null || cumulative < checkpoint.CumulativeBytes
            ? cumulative : cumulative - checkpoint.CumulativeBytes;
        if (checkpoint is null)
        {
            checkpoint = new() { AttemptId = observation.AttemptId, TransferId = transferId };
            checkpoints.Add(checkpoint.AttemptId, checkpoint);
            changed++;
        }
        checkpoint.Revision = observation.Revision;
        checkpoint.CumulativeBytes = cumulative;
        checkpoint.LastObservedAtUtc = observation.OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        if (delta <= 0) return changed;

        var key = BucketKey.From(observation.OccurredAtUtc, direction, username);
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new() { BucketStartUtc = key.Start, Direction = key.Direction, Username = key.Username, Bytes = delta };
            buckets.Add(key, bucket);
            if (tracked) context.TransferByteBuckets.Add(bucket);
        }
        else bucket.Bytes = checked(bucket.Bytes + delta);
        return changed + 1;
    }

    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        await checkpoints.WriteAsync(cancellationToken).ConfigureAwait(false);
        if (tracked) return;
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var transaction = (SqliteTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        SqliteWriteStatement? insert = null, update = null;
        try
        {
            foreach (var (key, bucket) in buckets)
            {
                bool exists = originalBuckets.TryGetValue(key, out long previous);
                if (exists && previous == bucket.Bytes) continue;
                var statement = exists ? update ??= new(connection, transaction, """
                    UPDATE transfer_byte_buckets SET bytes=$bytes
                    WHERE bucket_start_utc=$start AND direction=$direction AND username=$username;
                    """) : insert ??= new(connection, transaction, """
                    INSERT INTO transfer_byte_buckets(bytes,bucket_start_utc,direction,username)
                    VALUES ($bytes,$start,$direction,$username);
                    """);
                try
                {
                    statement.Bind(0, bucket.Bytes);
                    statement.Bind(1, key.Start);
                    statement.Bind(2, key.Direction);
                    statement.Bind(3, key.Username);
                    if (statement.Execute(cancellationToken) != 1)
                        throw new DbUpdateConcurrencyException("Transfer byte totals were not written.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The same owner attributes a failed bucket to its original
                    // mutation, rolls back, and preserves independent siblings.
                    throw new DbUpdateException("Could not persist transfer byte totals.", exception, [context.Entry(bucket)]);
                }
            }
        }
        finally
        {
            insert?.Dispose();
            update?.Dispose();
        }
    }

    internal static bool OwnsRow(string direction, string? username,
        IReadOnlyList<TransferAccountingObservation>? observations, object entity)
        => observations is { Count: > 0 } && (entity switch
        {
            TransferAccountingCheckpointEntity row => observations.Any(observation =>
                observation.AttemptId == row.AttemptId && observation.Revision == row.Revision),
            TransferByteBucketEntity row => row.Direction == direction && row.Username == (username ?? "")
                && observations.Any(observation => BucketKey.From(observation.OccurredAtUtc, direction, username).Start == row.BucketStartUtc),
            TransferAccountingStateEntity => true,
            _ => false,
        });
}
