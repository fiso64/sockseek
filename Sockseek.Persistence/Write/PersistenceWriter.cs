using Sockseek.Persistence.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sockseek.Persistence.Entities;
using Sockseek.Core.Planning;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using System.Diagnostics;
using System.Text.Json;
using Sockseek.Persistence.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Persistence.Write;

public sealed class PersistenceWriter(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceInbox inbox,
    PersistenceHealth health,
    PersistenceWriterOptions options,
    TimeProvider? timeProvider = null,
    IPersistenceMutationObserver? mutationObserver = null,
    ILogger? logger = null)
{
    private const int MaximumConsecutiveCommands = 16;
    private const int BulkBatchThreshold = 64;
    private static readonly TimeSpan BatchGatheringInterval = TimeSpan.FromMilliseconds(20);
    internal const long AccountingBucketMilliseconds = 5 * 60 * 1000;
    private static readonly ActivitySource WriteActivities = new("Sockseek.Persistence.Write");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private SockseekDbContext? connectionOwner;
    private SqliteCheckpoint? checkpoint;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset nextProgressFlush = clock.GetUtcNow() + options.TransferProgressFlushInterval;
            int consecutiveRecoveryAttempts = 0;
            int consecutiveCommands = 0;
            bool preferBackgroundCommand = true;
            long? bulkCommittedAt = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                await inbox.WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
                if (!HasPendingWork())
                {
                    if (IsDrained()) return;
                    continue;
                }

                bool commandsYielding = consecutiveCommands >= MaximumConsecutiveCommands;
                if (!commandsYielding
                    && inbox.TryDequeueCommand(out var command))
                {
                    await WriteCommandAsync(command!, cancellationToken).ConfigureAwait(false);
                    consecutiveCommands++;
                    continue;
                }

                consecutiveCommands = 0;
                // Alternate background transactions and mutation batches. Neither
                // a search publication backlog nor history ingestion owns the
                // writer until its queue empties. Interactive commands run between
                // these transactions, with a finite burst to preserve fairness.
                if (preferBackgroundCommand && inbox.TryDequeueBackgroundCommand(out var background))
                {
                    await WriteCommandAsync(background!, cancellationToken).ConfigureAwait(false);
                    preferBackgroundCommand = false;
                    continue;
                }

                TimeSpan gathering = bulkCommittedAt is { } committedAt
                    ? BatchGatheringInterval - clock.GetElapsedTime(committedAt) : TimeSpan.Zero;
                // Publication may outlast the previous gathering window while
                // producers are blocked on packet capacity. Release those slots
                // before gathering; elapsed work cannot fill a full input queue.
                if (inbox.SearchDepth >= options.SearchMutationQueueCapacity
                    && inbox.BufferedSearchResultCount < options.SearchResultFlushCount)
                    gathering = BatchGatheringInterval;
                DateTimeOffset now = clock.GetUtcNow();
                bool flushProgress = inbox.IsCompleted || now >= nextProgressFlush;
                var batch = await inbox.ReadBatchAsync(flushProgress, gathering, clock, cancellationToken)
                    .ConfigureAwait(false);
                if (flushProgress)
                    nextProgressFlush = now + options.TransferProgressFlushInterval;
                preferBackgroundCommand = true;
                if (batch.Count == 0)
                {
                    if (inbox.TryDequeueBackgroundCommand(out background))
                        await WriteCommandAsync(background!, cancellationToken).ConfigureAwait(false);
                    if (IsDrained()) return;
                    continue;
                }

                var admittedBatch = batch;
                List<(IReadOnlyList<PersistenceMutation> Mutations, Exception Exception)>? failures = null;
                try
                {
                    while (batch.Count > 0)
                    {
                        var (result, failure, rejected) = await TryWriteBatchAsync(
                            batch, cancellationToken, allowInteractiveYield: !commandsYielding).ConfigureAwait(false);
                        if (result == WriteBatchResult.RecoverableFailure)
                        {
                            consecutiveRecoveryAttempts++;
                            if (consecutiveRecoveryAttempts >= options.MaximumRecoveryAttempts)
                            {
                                var exhausted = new InvalidOperationException(
                                    $"Persistence recovery exhausted {options.MaximumRecoveryAttempts} attempts; dropping {batch.Count} retained mutations.",
                                    failure);
                                health.RecordOperationalFailure(clock.GetUtcNow(), exhausted);
                                (failures ??= []).Add((batch, exhausted));
                            }
                            else
                            {
                                // No outcome has been reported yet. Preserve admission
                                // order until a retry can settle the valid survivors.
                                inbox.RetainForRetry(admittedBatch);
                                failures = null;
                                await Task.Delay(options.FailureRetryDelay, clock, cancellationToken).ConfigureAwait(false);
                            }
                            break;
                        }
                        if (result == WriteBatchResult.Success)
                        {
                            consecutiveRecoveryAttempts = 0;
                            // First/sparse work remains immediate. After a bulk
                            // transaction, briefly gather its continuing burst so
                            // CPU improvements do not amplify durable commits.
                            bulkCommittedAt = batch.Count >= BulkBatchThreshold ? clock.GetTimestamp() : null;
                            break;
                        }

                        var lost = rejected ?? batch;
                        (failures ??= []).Add((lost,
                            failure ?? new InvalidOperationException("Persistence mutation batch failed permanently.")));
                        // Keep survivors together: individual writes would reject valid
                        // forward references, and re-coalescing could lose attempts.
                        var lostSet = new HashSet<PersistenceMutation>(lost, ReferenceEqualityComparer.Instance);
                        batch = batch.Where(mutation => !lostSet.Contains(mutation)).ToArray();
                    }
                    if (batch.Count == 0) consecutiveRecoveryAttempts = 0;
                }
                finally
                {
                    // A rejected entry rolls back its valid siblings too. Report
                    // terminal loss only after those survivors commit or are lost;
                    // readers may finish a source immediately on this notification.
                    // A recoverable replay retains the original batch instead.
                    if (failures != null)
                        foreach (var lost in failures)
                        {
                            health.RecordPermanentlyFailedMutations(lost.Mutations.Count);
                            mutationObserver?.PermanentlyFailed(lost.Mutations, lost.Exception);
                            foreach (var fence in lost.Mutations.OfType<PersistenceFence>())
                                fence.Completion.TrySetException(lost.Exception);
                        }
                }
                if (IsDrained()) return;
            }
        }
        finally
        {
            // No producer may remain blocked on capacity after its only reader
            // exits, including cancellation before the normal drain completes.
            inbox.Complete();
            inbox.FailPendingCommands(new OperationCanceledException("Persistence writer stopped."));
            try
            {
                if (checkpoint != null)
                    await checkpoint.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                checkpoint = null;
                var owner = connectionOwner;
                connectionOwner = null;
                if (owner != null) await owner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<SockseekDbContext> CreateWriteContextAsync(CancellationToken cancellationToken)
    {
        using var operation = WriteActivities.StartActivity("open");
        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connectionOwner == null)
            {
                // Only the writer owns this connection and its dirty-page cache.
                // Readers retain independent connections and their ordinary cache.
                var owner = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var connection = (SqliteConnection)owner.Database.GetDbConnection();
                connection.ConnectionString = new SqliteConnectionStringBuilder(connection.ConnectionString)
                    { Pooling = false }.ToString();
                try
                {
                    await owner.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await using var command = connection.CreateCommand();
                    // FULL WAL commits remain here; only page backfill leaves the queue.
                    command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA cache_size=-32768; PRAGMA wal_autocheckpoint=0;";
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    connection.CreateCollation("SOCKSEEK_NATURAL", static (left, right) =>
                        System.Globalization.CultureInfo.InvariantCulture.CompareInfo.Compare(left, right,
                            System.Globalization.CompareOptions.IgnoreCase
                            | System.Globalization.CompareOptions.IgnoreNonSpace
                            | System.Globalization.CompareOptions.NumericOrdering));
                    var copying = new SqliteCheckpoint(connection, logger ?? NullLogger.Instance,
                        exception => health.RecordFailure(clock.GetUtcNow(), exception, transient: true));
                    connectionOwner = owner;
                    checkpoint = copying;
                }
                catch
                {
                    await owner.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            context.Database.SetDbConnection(connectionOwner.Database.GetDbConnection(), contextOwnsConnection: false);
            return context;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool HasPendingWork()
        => inbox.CriticalDepth > 0
            || inbox.OrdinaryDepth > 0
            || inbox.ProgressCount > 0
            || inbox.RetryMutationCount > 0
            || inbox.SearchDepth > 0;

    private bool IsDrained()
        => inbox.IsCompleted && inbox.ActiveAdmissionCount == 0 && !HasPendingWork();

    private async Task WriteCommandAsync(
        AwaitablePersistenceCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.TryStart()) return;
        using var operation = WriteActivities.StartActivity("command", ActivityKind.Internal, command.TraceContext);
        operation?.SetTag("queue.duration_ms", Stopwatch.GetElapsedTime(command.QueuedAt).TotalMilliseconds);
        try
        {
            Exception? lastFailure = null;
            for (int recoveryAttempt = 0; recoveryAttempt < options.MaximumRecoveryAttempts; recoveryAttempt++)
            {
                var (result, exception) = await TryWriteCommandAsync(
                    command, cancellationToken).ConfigureAwait(false);
                if (result == WriteBatchResult.Success)
                    return;
                lastFailure = exception;
                if (result == WriteBatchResult.PermanentFailure)
                    break;
                await Task.Delay(options.FailureRetryDelay, clock, cancellationToken).ConfigureAwait(false);
            }

            var failure = lastFailure ?? new InvalidOperationException("Persistence command failed.");
            command.Fail(failure);
        }
        catch (Exception ex)
        {
            // The command has already left the inbox, so the writer's final
            // pending-command sweep cannot see it.
            command.Fail(ex);
            throw;
        }
    }

    private async Task<(WriteBatchResult Result, Exception? Exception)> TryWriteCommandAsync(
        AwaitablePersistenceCommand command,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                int rows;
                Stopwatch stopwatch;
                {
                    stopwatch = Stopwatch.StartNew();
                    await using var context = await CreateWriteContextAsync(cancellationToken).ConfigureAwait(false);
                    RecordCacheCounters(Activity.Current, context, "before");
                    // A failed attempt rolls back and discards this entire transaction.
                    // EF's nested savepoint adds SQLite subjournals with no recovery use.
                    context.Database.AutoSavepointsEnabled = false;
                    await using var transaction = command.RequiresTransaction
                        ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
                    using (WriteActivities.StartActivity("apply"))
                        await command.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
                    using (WriteActivities.StartActivity("save"))
                        rows = command.RequiresTransaction
                            ? await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false) : 0;
                    if (transaction != null)
                    {
                        using var commit = WriteActivities.StartActivity("commit");
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    RecordCacheCounters(Activity.Current, context, "after");
                }
                if (command.RequiresTransaction)
                {
                    checkpoint!.RequestIfNeeded();
                    health.RecordCommit(clock.GetUtcNow(), rows, stopwatch.Elapsed, 1,
                        reconciliationComplete: inbox.RetryMutationCount == 0);
                }
                command.Complete();
                return (WriteBatchResult.Success, null);
            }
            catch (Exception ex) when (IsBusy(ex) && attempt < options.BusyRetryCount)
            {
                health.RecordBusyRetry();
                health.RecordFailure(clock.GetUtcNow(), ex, transient: true);
                await Task.Delay(options.BusyRetryDelay, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                bool recoverable = IsRecoverable(ex);
                // A rejected command has rolled back and reports its failure to
                // its caller. It is not a lost history mutation. Domain errors
                // (missing/pruned views, stale revisions, invalid selections)
                // must not make the entire database permanently unhealthy.
                if (ex is System.Data.Common.DbException or DbUpdateException or IOException)
                    health.RecordFailure(clock.GetUtcNow(), ex, transient: recoverable,
                        isolated: ex is SqliteException { SqliteErrorCode: 19 }
                            || ex is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 19 } });
                return (recoverable ? WriteBatchResult.RecoverableFailure : WriteBatchResult.PermanentFailure, ex);
            }
        }
    }

    // Keep retry/failure ownership separate from phase execution. A single large
    // async state machine made JIT SSA construction expensive enough to stall the
    // writer during ordinary bulk downloads. These phases also make the only
    // interactive yield and the BEGIN/commit boundary explicit.
    private async Task<(WriteBatchResult Result, Exception? Exception, IReadOnlyList<PersistenceMutation>? Rejected)> TryWriteBatchAsync(
        IReadOnlyList<PersistenceMutation> batch,
        CancellationToken cancellationToken,
        bool allowInteractiveYield)
    {
        using var operation = WriteActivities.StartActivity("batch");
        operation?.SetTag("mutation.count", batch.Count);
        bool useBulkInserts = true;
        int busyRetries = 0;
        bool yielded = !allowInteractiveYield;
        while (true)
        {
            var attempt = new BatchAttempt(batch, useBulkInserts);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                PrepareBatchInputs(attempt);
                if (!yielded)
                {
                    yielded = true;
                    await YieldToInteractiveCommandsAsync(cancellationToken).ConfigureAwait(false);
                }
                int rows = await ExecuteBatchAsync(attempt, operation, cancellationToken).ConfigureAwait(false);
                checkpoint!.RequestIfNeeded();
                health.RecordCommit(clock.GetUtcNow(), rows, stopwatch.Elapsed, batch.Count,
                    reconciliationComplete: inbox.RetryMutationCount == 0);
                mutationObserver?.Committed(attempt.Normalized, attempt.SupersededTransfers);
                foreach (var fence in attempt.Normalized.OfType<PersistenceFence>()) fence.Completion.TrySetResult();
                return (WriteBatchResult.Success, null, null);
            }
            catch (Exception ex) when (attempt.PreparingCombinedSearch && ex is not OperationCanceledException)
            {
                // Combined encoding cannot identify the malformed original.
                useBulkInserts = false;
            }
            catch (SqliteException ex) when (attempt.InsertingRows && ex.SqliteErrorCode == 19)
            {
                // Rollback precedes individual/tracked retry and exact attribution.
                useBulkInserts = false;
            }
            catch (Exception ex) when (IsBusy(ex) && busyRetries < options.BusyRetryCount)
            {
                busyRetries++;
                health.RecordBusyRetry();
                health.RecordFailure(clock.GetUtcNow(), WithBatchContext(ex, batch), transient: true);
                await Task.Delay(options.BusyRetryDelay, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                bool recoverable = IsRecoverable(ex);
                var rejected = recoverable ? null : FindRejectedMutations(batch, attempt.Normalized, ex, attempt.Applying);
                health.RecordFailure(clock.GetUtcNow(), WithBatchContext(ex, batch), transient: recoverable, isolated: rejected != null);
                return (recoverable ? WriteBatchResult.RecoverableFailure : WriteBatchResult.PermanentFailure, ex, rejected);
            }
        }
    }

    // Only this attempt owns attribution state. Helpers run sequentially on the
    // same writer; no rows, commands or prepared statements survive its context.
    private sealed class BatchAttempt(IReadOnlyList<PersistenceMutation> original, bool bulk)
    {
        public IReadOnlyList<PersistenceMutation> Original { get; } = original;
        public bool Bulk { get; } = bulk;
        public IReadOnlyList<PersistenceMutation> Normalized = [];
        public PersistenceMutation[] Ordered = [];
        public readonly Dictionary<SearchResultsPersistenceMutation, PreparedSearchWrite> Search = new(ReferenceEqualityComparer.Instance);
        public PersistenceMutation? Applying;
        public bool PreparingCombinedSearch;
        public bool InsertingRows;
        public List<(Guid TransferId, long Revision)> SupersededTransfers = [];
    }

    private static void PrepareBatchInputs(BatchAttempt attempt)
    {
        using (WriteActivities.StartActivity("prepare"))
        {
            attempt.Normalized = NormalizeBatch(attempt.Original);
            attempt.Ordered = attempt.Normalized.OrderBy(DependencyOrder).ThenBy(item => item.Sequence).ToArray();
        }
        using var preparation = WriteActivities.StartActivity("prepare-search");
        attempt.PreparingCombinedSearch = attempt.Bulk;
        if (attempt.Bulk)
        {
            // Preserve original identities for acknowledgement/rejection while
            // encoding one append per source. Inputs remain canonical references.
            foreach (var group in attempt.Ordered.OfType<SearchResultsPersistenceMutation>().GroupBy(item => item.SearchJobId))
            {
                var packets = group.ToArray();
                attempt.Applying = packets[0];
                IReadOnlyList<SearchProjectionInput> inputs = packets.Length == 1
                    ? packets[0].Results : packets.SelectMany(item => item.Results).ToArray();
                var write = new PreparedSearchWrite(packets[0], packets.Max(item => item.Revision),
                    SearchResultWriter.Prepare(inputs));
                foreach (var packet in packets) attempt.Search[packet] = write;
            }
        }
        else
        {
            foreach (var mutation in attempt.Ordered.OfType<SearchResultsPersistenceMutation>())
            {
                attempt.Applying = mutation;
                attempt.Search[mutation] = new(mutation, mutation.Revision, SearchResultWriter.Prepare(mutation.Results));
            }
        }
        attempt.PreparingCombinedSearch = false;
        attempt.Applying = null;
    }

    private async Task YieldToInteractiveCommandsAsync(CancellationToken cancellationToken)
    {
        // Encoding may be expensive. Yield before loading DB-dependent facts;
        // no command may interleave between those reads and their commit.
        for (int i = 0; i < MaximumConsecutiveCommands && inbox.TryDequeueCommand(out var command); i++)
            await WriteCommandAsync(command!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteBatchAsync(BatchAttempt attempt, Activity? operation, CancellationToken cancellationToken)
    {
        await using var context = await CreateWriteContextAsync(cancellationToken).ConfigureAwait(false);
        RecordCacheCounters(Activity.Current, context, "before");
        // Failed attempts discard the whole transaction; nested EF savepoints
        // would only add SQLite subjournals without an independent recovery use.
        context.Database.AutoSavepointsEnabled = false;
        BatchRows rows;
        using (WriteActivities.StartActivity("load"))
            rows = await LoadBatchRowsAsync(context, attempt.Normalized, attempt.Search,
                tracked: !attempt.Bulk, cancellationToken).ConfigureAwait(false);
        // Use originals: coalescing current job state must retain every earlier
        // admitted manifest attempt. Read all known/absent revisions before BEGIN.
        var manifestJobs = attempt.Original.OfType<JobPersistenceMutation>()
            .Where(job => job.DirectoryManifest != null)
            .GroupBy(job => (job.JobId, job.DirectoryManifest!.AttemptNumber))
            .Select(group => group.MaxBy(job => job.Revision)!).ToArray();
        using var manifestRows = manifestJobs.Length == 0 ? null
            : await DirectoryManifests.LoadAsync(context, manifestJobs, cancellationToken).ConfigureAwait(false);
        var prepared = new PreparedBatchRows(rows, manifestRows);
        using (WriteActivities.StartActivity("prepare-manifests"))
            foreach (var job in manifestJobs)
            {
                attempt.Applying = job;
                if (manifestRows!.Prepare(job) is { } manifest) prepared.Manifests.Enqueue(manifest);
            }
        attempt.Applying = null;
        await PrepareHistoryRowsAsync(context, attempt, prepared, cancellationToken).ConfigureAwait(false);
        await CommitBatchAsync(context, attempt, prepared, operation, cancellationToken).ConfigureAwait(false);
        return prepared.AffectedRows;
    }

    private sealed class PreparedBatchRows(BatchRows rows, DirectoryManifests? directoryRows)
    {
        public BatchRows Rows { get; } = rows;
        public DirectoryManifests? DirectoryRows { get; } = directoryRows;
        public Queue<DirectoryManifests.Prepared> Manifests { get; } = [];
        public HistoryRows<JobEntity>.PreparedInsert? NewJobs;
        public int AffectedRows;
    }

    private async Task PrepareHistoryRowsAsync(SockseekDbContext context, BatchAttempt attempt,
        PreparedBatchRows prepared, CancellationToken cancellationToken)
    {
        // Includes deferred payload/accounting encoding on tracked retries too.
        // Dependency writes and constraint validation stay in the transaction.
        using var preparation = WriteActivities.StartActivity("prepare-history");
        foreach (var mutation in attempt.Ordered)
        {
            if (!IsHistoryMutation(mutation)) continue;
            attempt.Applying = mutation;
            prepared.AffectedRows += await ApplyAsync(context, mutation, prepared.Rows, cancellationToken).ConfigureAwait(false);
        }
        attempt.Applying = null;
        if (attempt.Bulk) prepared.NewJobs = prepared.Rows.Jobs.PrepareInsert();
    }

    private async Task CommitBatchAsync(SockseekDbContext context, BatchAttempt attempt,
        PreparedBatchRows prepared, Activity? operation, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyBatchAsync(context, transaction, attempt, prepared, cancellationToken).ConfigureAwait(false);
        using (WriteActivities.StartActivity("save"))
        {
            context.ChangeTracker.DetectChanges();
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            if (attempt.Bulk)
            {
                await prepared.Rows.Jobs.WriteAsync(cancellationToken).ConfigureAwait(false);
                await prepared.Rows.Transfers.WriteAsync(cancellationToken).ConfigureAwait(false);
                await prepared.Rows.Attempts.WriteAsync(cancellationToken).ConfigureAwait(false);
                await prepared.Rows.Accounting.WriteAsync(cancellationToken).ConfigureAwait(false);
            }
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await SupersedeUploadsAsync(context, attempt, prepared.Rows.NewUploadIds, cancellationToken).ConfigureAwait(false);
        }
        operation?.SetTag("ready.background_commands", inbox.BackgroundCommandDepth);
        operation?.SetTag("ready.interactive_commands", inbox.CommandDepth);
        using (WriteActivities.StartActivity("commit"))
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        RecordCacheCounters(operation, context, "after");
    }

    private static async Task SupersedeUploadsAsync(
        SockseekDbContext context, BatchAttempt attempt, IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        // Keep attempts/accounting for diagnostics, but display only the latest
        // request for an uploaded file. Run once per batch in its transaction;
        // repeating this on replay is harmless. Downloads already reuse their
        // transfer identity for retries, and separate jobs remain independent.
        // Terminal uploads have no later progress; source sequence orders requests
        // admitted in the same millisecond within one runtime.
        if (ids.Count == 0) return;
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = """
            UPDATE transfers AS old SET removed_at_utc = $now
            WHERE old.id IN (
                SELECT old.id FROM json_each($ids) incoming
                JOIN transfers inserted ON inserted.id = incoming.value
                JOIN transfers old ON old.username = inserted.username COLLATE BINARY
                    AND old.remote_path = inserted.remote_path COLLATE BINARY
                    AND old.direction = 'Upload' AND old.removed_at_utc IS NULL
                JOIN transfers current ON current.username = inserted.username COLLATE BINARY
                    AND current.remote_path = inserted.remote_path COLLATE BINARY
                    AND current.direction = 'Upload' AND current.removed_at_utc IS NULL
                WHERE old.id <> current.id AND old.terminal_outcome <> 'None'
                    AND (old.created_at_utc < current.created_at_utc
                        OR (old.created_at_utc = current.created_at_utc
                            AND old.last_runtime_id = current.last_runtime_id
                            AND old.last_sequence < current.last_sequence)))
            RETURNING id, revision;
            """;
        command.Parameters.Add(new SqliteParameter("$ids", SerializeSqliteGuids(ids)));
        command.Parameters.Add(new SqliteParameter("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            attempt.SupersededTransfers.Add((reader.GetGuid(0), reader.GetInt64(1)));
    }

    private async Task ApplyBatchAsync(SockseekDbContext context, IDbContextTransaction transaction,
        BatchAttempt attempt, PreparedBatchRows prepared, CancellationToken cancellationToken)
    {
        using var application = WriteActivities.StartActivity("apply");
        await using var rawHistory = new SearchResultWriter.RawHistoryBatch(
            (SqliteConnection)context.Database.GetDbConnection(), (SqliteTransaction)transaction.GetDbTransaction());
        if (prepared.NewJobs is { } newJobs)
        {
            // One insert resolves forward/cyclic job references, including
            // composite owners prepared with the ordinary job headers above.
            attempt.InsertingRows = true;
            await newJobs.WriteAsync(cancellationToken).ConfigureAwait(false);
            attempt.InsertingRows = false;
            prepared.NewJobs = null;
        }
        foreach (var mutation in attempt.Ordered)
        {
            if (IsHistoryMutation(mutation)) continue;
            attempt.Applying = mutation;
            if (mutation is SearchResultsPersistenceMutation search)
            {
                attempt.InsertingRows = attempt.Bulk;
                prepared.AffectedRows += await ApplySearchResultsAsync(context, search, prepared.Rows, rawHistory, cancellationToken).ConfigureAwait(false);
                attempt.InsertingRows = false;
            }
            else prepared.AffectedRows += await ApplyAsync(context, mutation, prepared.Rows, cancellationToken).ConfigureAwait(false);
        }
        attempt.Applying = null;
        // Encoded buffers leave this queue as they are written, before a slow commit.
        while (prepared.Manifests.TryDequeue(out var manifest))
        {
            attempt.Applying = manifest.Mutation;
            prepared.DirectoryRows!.Write(manifest, (SqliteTransaction)transaction.GetDbTransaction(), cancellationToken);
        }
        attempt.Applying = null;
    }

    private static void RecordCacheCounters(Activity? operation, SockseekDbContext context, string boundary)
    {
        if (operation is not { IsAllDataRequested: true }) return;
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) return;
        // SQLitePCL does not name SQLITE_DBSTATUS_CACHE_SPILL (12). These
        // cumulative counters distinguish WAL writes from checkpoint copies.
        if (SQLitePCL.raw.sqlite3_db_status(connection.Handle, 12, out int spills, out _, 0) == 0)
            operation.SetTag($"cache.spills.{boundary}", spills);
        if (SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_WRITE,
            out int writes, out _, 0) == 0)
            operation.SetTag($"cache.writes.{boundary}", writes);
        if (SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_USED,
            out int bytes, out _, 0) == 0)
            operation.SetTag($"cache.bytes.{boundary}", bytes);
    }

    private static IReadOnlyList<PersistenceMutation>? FindRejectedMutations(
        IReadOnlyList<PersistenceMutation> batch,
        IReadOnlyList<PersistenceMutation> normalized,
        Exception exception,
        PersistenceMutation? applying)
    {
        // An Apply operation can flush earlier tracked rows (search raw inserts
        // do this). Prefer EF's failing row to the current operation in that case.
        // Revision-guarded rows belong to the first applied matching revision;
        // a later equal-revision composite's job update was ignored.
        var owners = exception is DbUpdateException update
            ? update.Entries.Select(entry => normalized.OrderBy(DependencyOrder).ThenBy(mutation => mutation.Sequence)
                .FirstOrDefault(mutation => OwnsFailedRow(mutation, entry.Entity))).OfType<PersistenceMutation>().ToArray()
            : [];
        if (owners.Length == 0 && applying != null)
            owners = [applying];
        if (owners.Length == 0)
            return null; // A transaction/context failure has no independently attributable entry.

        var rejected = batch.Where(original => owners.Any(owner =>
            ReferenceEquals(original, owner)
            // Only ordinary transfer headers coalesce. A composite completion
            // keeps its independent attempt/job boundary and original identity.
            || owner is TransferPersistenceMutation && original is TransferPersistenceMutation
                && original.EntityId == owner.EntityId)).ToArray();
        return rejected.Length == 0 ? null : rejected;
    }

    private static bool OwnsFailedRow(PersistenceMutation mutation, object entity)
        => mutation switch
        {
            JobPersistenceMutation job => entity switch
            {
                JobEntity row => row.Id == job.JobId && row.Revision == job.Revision,
                SearchJobEntity row => row.JobId == job.JobId && row.Revision == 0 && job.Kind == "Search",
                _ => false,
            },
            TransferPersistenceMutation transfer => entity is TransferEntity row
                && row.Id == transfer.TransferId && row.Revision == transfer.Revision
                || TransferAccounting.OwnsRow(transfer.Direction, transfer.Username, transfer.AccountingObservations, entity),
            TransferAttemptPersistenceMutation attempt => entity is TransferAttemptEntity row
                && row.Id == attempt.AttemptId && row.Revision == attempt.Revision
                || TransferAccounting.OwnsRow(attempt.Direction, attempt.SourceUsername, attempt.AccountingObservations, entity),
            SearchResultsPersistenceMutation results => entity is SearchJobEntity row
                && row.JobId == results.SearchJobId && row.Revision == results.Revision,
            SearchCompletionPersistenceMutation completion => entity is SearchJobEntity row
                && row.JobId == completion.SearchJobId && row.Revision == completion.Revision,
            SearchIncompletePersistenceMutation incomplete => entity is SearchJobEntity row
                && row.JobId == incomplete.SearchJobId && row.Revision == incomplete.Revision,
            TransferTerminalPersistenceMutation terminal => OwnsFailedRow(terminal.Transfer, entity)
                || terminal.FinalAttempt != null && OwnsFailedRow(terminal.FinalAttempt, entity)
                || terminal.OwningJob != null && OwnsFailedRow(terminal.OwningJob, entity),
            _ => false,
        };

    // One transaction's positive and negative lookups. History rows stay outside
    // EF tracking; newly arriving transfers/checkpoints never require per-ID reads.
    private sealed record PreparedSearchWrite(SearchResultsPersistenceMutation Owner,
        long Revision, SearchResultWriter.PreparedBatch Data);

    private sealed record BatchRows(
        HistoryRows<JobEntity> Jobs,
        HistoryRows<TransferEntity> Transfers,
        HistoryRows<TransferAttemptEntity> Attempts,
        TransferAccounting Accounting,
        Dictionary<SearchResultsPersistenceMutation, PreparedSearchWrite> SearchObservations,
        IReadOnlySet<Guid> SourceJobIds)
    {
        public HashSet<Guid> NewUploadIds { get; } = [];
    }

    private static async Task<BatchRows> LoadBatchRowsAsync(
        SockseekDbContext context, IReadOnlyList<PersistenceMutation> mutations,
        Dictionary<SearchResultsPersistenceMutation, PreparedSearchWrite> searchObservations,
        bool tracked, CancellationToken cancellationToken)
    {
        var terminals = mutations.OfType<TransferTerminalPersistenceMutation>().ToArray();
        var transfers = mutations.OfType<TransferPersistenceMutation>().Concat(terminals.Select(item => item.Transfer)).ToArray();
        var attempts = mutations.OfType<TransferAttemptPersistenceMutation>()
            .Concat(terminals.Select(item => item.FinalAttempt).OfType<TransferAttemptPersistenceMutation>()).ToArray();
        var jobs = mutations.OfType<JobPersistenceMutation>()
            .Concat(terminals.Select(item => item.OwningJob).OfType<JobPersistenceMutation>()).ToArray();
        Guid[] jobIds = jobs.Select(job => job.JobId).Distinct().ToArray();
        // A follow-up can outlive its source. Resolve optional provenance once
        // per batch, including forward references, instead of reinstating a
        // deleted foreign key on the follow-up's next progress update.
        var sourceIds = jobs.Select(job => job.SourceJobId).OfType<Guid>().Except(jobIds).ToArray();
        var availableSources = jobIds.ToHashSet();
        if (sourceIds.Length > 0)
            availableSources.UnionWith(await SqliteReadQuery.ReadAsync(context,
                "SELECT id FROM jobs WHERE id IN (SELECT value FROM json_each($ids))",
                [new SqliteParameter("$ids", SerializeSqliteGuids(sourceIds))], row => row.GetGuid(0), cancellationToken)
                .ConfigureAwait(false));
        Guid[] transferIds = transfers.Select(item => item.TransferId).Distinct().ToArray();
        Guid[] attemptIds = attempts.Select(item => item.AttemptId).Distinct().ToArray();
        return new(
            await HistoryRows<JobEntity>.LoadAsync(context, jobIds, tracked, cancellationToken,
                nameof(JobEntity.PayloadJson)).ConfigureAwait(false),
            await HistoryRows<TransferEntity>.LoadAsync(context, transferIds, tracked, cancellationToken).ConfigureAwait(false),
            await HistoryRows<TransferAttemptEntity>.LoadAsync(context, attemptIds, tracked, cancellationToken).ConfigureAwait(false),
            await TransferAccounting.LoadAsync(context, transfers, attempts, tracked, cancellationToken).ConfigureAwait(false),
            searchObservations, availableSources);
    }

    // Keep bulk identity lookups at one parameter even when an observation
    // backlog contains many attempts. EF's scalar IN parameters grow with IDs.
    private static string SerializeSqliteGuids(IEnumerable<Guid> ids)
        => JsonSerializer.Serialize(ids.Select(id => id.ToString("D").ToUpperInvariant()));

    private async Task<int> ApplyAsync(
        SockseekDbContext context,
        PersistenceMutation mutation,
        BatchRows batchRows,
        CancellationToken cancellationToken)
        => mutation switch
        {
            PersistenceFence => 0,
            JobPersistenceMutation job => await ApplyJobAsync(context, job, batchRows, cancellationToken).ConfigureAwait(false),
            TransferPersistenceMutation transfer => ApplyTransfer(transfer, batchRows),
            TransferAttemptPersistenceMutation attempt => ApplyAttempt(attempt, batchRows),
            SearchCompletionPersistenceMutation completion => await ApplySearchCompletionAsync(context, completion, batchRows.Jobs, cancellationToken).ConfigureAwait(false),
            SearchIncompletePersistenceMutation incomplete => await ApplySearchIncompleteAsync(context, incomplete, cancellationToken).ConfigureAwait(false),
            TransferTerminalPersistenceMutation terminal => await ApplyTransferTerminalAsync(context, terminal, batchRows, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported persistence mutation type {mutation.GetType().FullName}."),
        };

    private async Task<int> ApplyJobAsync(SockseekDbContext context, JobPersistenceMutation mutation,
        BatchRows batchRows, CancellationToken cancellationToken)
    {
        var jobs = batchRows.Jobs;
        jobs.TryGetValue(mutation.JobId, out var entity);
        if (entity != null && (entity.Revision >= mutation.Revision || entity.LifecycleState == "Terminal" && mutation.LifecycleState != "Terminal"))
            return 0;

        long occurredAt = mutation.OccurredAtUnixMilliseconds;
        if (entity == null)
        {
            entity = new JobEntity
            {
                Id = mutation.JobId,
                CreatedAtUtc = (mutation.RegisteredAtUtc ?? mutation.OccurredAtUtc)
                    .ToUniversalTime().ToUnixTimeMilliseconds(),
                StartedAtUtc = mutation.StartedAtUtc?.ToUniversalTime().ToUnixTimeMilliseconds()
                    ?? (mutation.LifecycleState == "Pending" ? null : occurredAt),
            };
            jobs.Add(entity.Id, entity);
        }

        entity.WorkflowId = mutation.WorkflowId;
        entity.SubmissionId = mutation.SubmissionId;
        entity.SemanticRole = mutation.SemanticRole;
        entity.ParentJobId = mutation.ParentJobId;
        entity.SourceJobId = mutation.SourceJobId is Guid source && batchRows.SourceJobIds.Contains(source) ? source : null;
        entity.ResultJobId = mutation.ResultJobId;
        entity.LastRuntimeId = mutation.RuntimeId;
        entity.LastSequence = mutation.Sequence;
        entity.DisplayId = mutation.DisplayId;
        entity.Kind = mutation.Kind;
        entity.LifecycleState = mutation.LifecycleState;
        entity.ActivityPhase = mutation.ActivityPhase;
        entity.ActivityUntilUtc = mutation.ActivityUntilUtc?.ToUniversalTime().ToUnixTimeMilliseconds();
        entity.TerminalOutcome = mutation.TerminalOutcome;
        entity.SkipReason = mutation.SkipReason;
        entity.CancellationSource = mutation.CancellationSource;
        entity.FailureReason = mutation.FailureReason;
        entity.FailureMessage = Limit(mutation.FailureMessage);
        entity.FailureDetail = Limit(mutation.FailureDetail);
        entity.ItemName = mutation.ItemName;
        entity.QueryText = mutation.QueryText;
        entity.SourceDetail = Limit(mutation.SourceDetail, 128);
        entity.PrintOption = mutation.PrintOption;
        entity.UpdatedAtUtc = Math.Max(entity.UpdatedAtUtc, occurredAt);
        if (entity.StartedAtUtc == null && mutation.LifecycleState != "Pending")
            entity.StartedAtUtc = mutation.StartedAtUtc?.ToUniversalTime().ToUnixTimeMilliseconds()
                ?? occurredAt;
        if (mutation.LifecycleState == "Terminal" && entity.CompletedAtUtc == null)
            entity.CompletedAtUtc = occurredAt;
        entity.Revision = mutation.Revision;
        entity.PayloadSchemaVersion = mutation.PayloadSchemaVersion;
        entity.PayloadJson = mutation.PayloadJson?.Value;

        if (mutation.Kind == "Search" && await context.SearchJobs.FindAsync([mutation.JobId], cancellationToken).ConfigureAwait(false) == null)
        {
            context.SearchJobs.Add(new SearchJobEntity
            {
                JobId = mutation.JobId,
                Query = mutation.QueryText ?? "",
                Revision = 0,
                ResultPersistenceState = "NotPersisted",
            });
        }
        return 1;
    }

    private int ApplyTransfer(TransferPersistenceMutation mutation, BatchRows batchRows)
    {
        int accountingRows = batchRows.Accounting.Apply(mutation.TransferId, mutation.Direction,
            mutation.Username, mutation.AccountingObservations);
        batchRows.Transfers.TryGetValue(mutation.TransferId, out var entity);
        if (entity != null && (entity.Revision >= mutation.Revision || entity.TerminalOutcome != "None" && mutation.TerminalOutcome == "None"))
            return accountingRows;

        long occurredAt = mutation.OccurredAtUnixMilliseconds;
        if (entity == null)
        {
            entity = new TransferEntity
            {
                Id = mutation.TransferId,
                CreatedAtUtc = mutation.RequestedAtUtc?.ToUniversalTime().ToUnixTimeMilliseconds()
                    ?? occurredAt,
            };
            batchRows.Transfers.Add(entity.Id, entity);
            if (mutation.Direction == "Upload") batchRows.NewUploadIds.Add(entity.Id);
        }

        entity.JobId = mutation.JobId;
        entity.WorkflowId = mutation.WorkflowId;
        entity.LastRuntimeId = mutation.RuntimeId;
        entity.LastSequence = mutation.Sequence;
        entity.Direction = mutation.Direction;
        entity.Source = mutation.Source;
        entity.Username = mutation.Username;
        entity.RemotePath = mutation.RemotePath;
        entity.LocalPath = mutation.LocalPath;
        entity.State = mutation.State;
        entity.TerminalOutcome = mutation.TerminalOutcome;
        // The v1 schema constrains total_bytes to nonnegative values. Preserve
        // unknown (-1 in the live protocol) without conflating it with zero.
        entity.TotalBytes = mutation.TotalBytes < 0 ? long.MaxValue : mutation.TotalBytes;
        entity.TransferredBytes = Math.Max(0, mutation.TransferredBytes);
        entity.AttemptCount = mutation.AttemptCount;
        // Admission alone is not an attempt. A queued transfer may terminalize
        // without ever starting (for example, user or daemon cancellation), in
        // which case started_at must remain null.
        if (entity.StartedAtUtc == null && mutation.StartedAtUtc is { } startedAt)
        {
            entity.StartedAtUtc = startedAt.ToUniversalTime().ToUnixTimeMilliseconds();
        }
        else if (entity.StartedAtUtc == null && mutation.AttemptCount > 0)
        {
            entity.StartedAtUtc = occurredAt;
        }
        if (mutation.LastProgressAtUtc is { } progressAt)
        {
            entity.LastProgressAtUtc = Math.Max(
                entity.LastProgressAtUtc ?? 0,
                progressAt.ToUniversalTime().ToUnixTimeMilliseconds());
        }
        else if (mutation.Priority == PersistenceMutationPriority.Progress)
        {
            entity.LastProgressAtUtc = Math.Max(entity.LastProgressAtUtc ?? 0, occurredAt);
        }
        if (mutation.BytesPerSecond.HasValue)
            entity.BytesPerSecond = Math.Max(0, mutation.BytesPerSecond.Value);
        if (mutation.TerminalOutcome != "None")
            entity.CompletedAtUtc ??= mutation.CompletedAtUtc?.ToUnixTimeMilliseconds() ?? occurredAt;
        entity.FailureReason = mutation.FailureReason;
        entity.FailureMessage = Limit(mutation.FailureMessage);
        entity.CancellationSource = mutation.CancellationSource;
        if (mutation.File is { } file)
        {
            entity.FileName = Limit(file.Name, 1024);
            entity.FileSizeBytes = Math.Max(0, file.Size);
            entity.FileExtension = Limit(file.Extension, 32);
            entity.FileBitRate = file.BitRate;
            entity.FileBitDepth = file.BitDepth;
            entity.FileSampleRate = file.SampleRate;
            entity.FileLength = file.Length;
            entity.FileAttributesJson = file.Attributes is null
                ? null
                : JsonSerializer.Serialize(file.Attributes);
        }
        if (mutation.GroupRef is not null)
            entity.GroupRef = mutation.GroupRef;
        entity.GroupKind = mutation.GroupKind.ToString();
        if (mutation.GroupDisplayPath is not null)
            entity.GroupDisplayPath = Limit(mutation.GroupDisplayPath, 4096);
        if (mutation.GroupRelativePath is not null)
            entity.GroupRelativePath = mutation.GroupRelativePath;
        entity.Revision = mutation.Revision;
        return accountingRows + 1;
    }

    private int ApplyAttempt(TransferAttemptPersistenceMutation mutation, BatchRows batchRows)
    {
        int accountingRows = batchRows.Accounting.Apply(mutation.TransferId, mutation.Direction,
            mutation.SourceUsername, mutation.AccountingObservations);
        batchRows.Attempts.TryGetValue(mutation.AttemptId, out var entity);
        if (entity != null && entity.Revision >= mutation.Revision)
            return accountingRows;

        long occurredAt = mutation.OccurredAtUnixMilliseconds;
        if (entity == null)
        {
            entity = new TransferAttemptEntity
            {
                Id = mutation.AttemptId,
                TransferId = mutation.TransferId,
                StartedAtUtc = mutation.StartedAtUtc?.ToUniversalTime().ToUnixTimeMilliseconds()
                    ?? occurredAt,
            };
            batchRows.Attempts.Add(entity.Id, entity);
        }

        entity.LastRuntimeId = mutation.RuntimeId;
        entity.LastSequence = mutation.Sequence;
        entity.AttemptNumber = mutation.AttemptNumber;
        entity.Source = mutation.Source;
        entity.State = mutation.State;
        entity.SourceUsername = mutation.SourceUsername;
        entity.SourcePath = mutation.SourcePath;
        entity.OutputPath = mutation.OutputPath;
        if (mutation.State is "Completed" or "Failed" or "Cancelled" or "Interrupted")
            entity.CompletedAtUtc ??= occurredAt;
        entity.FailureReason = mutation.FailureReason;
        entity.FailureMessage = Limit(mutation.FailureMessage);
        entity.Revision = mutation.Revision;
        return accountingRows + 1;
    }

    private async Task<int> ApplySearchResultsAsync(SockseekDbContext context,
        SearchResultsPersistenceMutation mutation, BatchRows batchRows,
        SearchResultWriter.RawHistoryBatch rawHistory, CancellationToken cancellationToken)
    {
        if (!batchRows.SearchObservations.TryGetValue(mutation, out var prepared))
            throw new InvalidOperationException("Search observations must be prepared before write access.");
        if (!ReferenceEquals(prepared.Owner, mutation)) return 0;
        var search = await GetOrCreateSearchAsync(context, mutation.SearchJobId, batchRows.Jobs, cancellationToken).ConfigureAwait(false);
        if (search.ResultPersistenceState is "Pruned" or "Interrupted")
            return 0;

        // The job may have been added earlier in this batch. Flush its parent rows
        // inside the same transaction, then let the raw-membership unique indexes keep
        // the first sequence and peer/path/visibility candidate in input order.
        // Existing counters stay tracked until the batch save; flushing those
        // after every peer response turns one batch into hundreds of updates.
        if (context.Entry(search).State == EntityState.Added)
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var (publicAdded, lockedAdded) = await rawHistory.StoreAsync(
            mutation.SearchJobId, prepared.Data, cancellationToken).ConfigureAwait(false);
        search.ResultCount = checked(search.ResultCount + publicAdded);
        search.LockedFileCount = checked(search.LockedFileCount + lockedAdded);
        search.Revision = Math.Max(search.Revision, prepared.Revision);
        if (search.ResultPersistenceState == "NotPersisted")
            search.ResultPersistenceState = "Incomplete";
        return checked(publicAdded + lockedAdded);
    }

    private async Task<int> ApplySearchCompletionAsync(SockseekDbContext context, SearchCompletionPersistenceMutation mutation, HistoryRows<JobEntity> jobs, CancellationToken cancellationToken)
    {
        var search = await GetOrCreateSearchAsync(context, mutation.SearchJobId, jobs, cancellationToken).ConfigureAwait(false);
        if (search.Revision > mutation.Revision || search.ResultPersistenceState is "Pruned" or "Interrupted")
            return 0;
        if (search.IsComplete
            && search.ResultPersistenceState == "Incomplete"
            && mutation.ResultPersistenceState == "Complete")
            return 0;

        search.Query = mutation.Query;
        search.Revision = mutation.Revision;
        search.ResultCount = mutation.ResultCount;
        search.LockedFileCount = mutation.LockedFileCount;
        search.ObservedPeerCount = mutation.ObservedPeerCount;
        search.IsComplete = true;
        search.CompletedAtUtc ??= mutation.OccurredAtUnixMilliseconds;
        search.ResultPersistenceState = mutation.ResultPersistenceState;
        return 1;
    }

    private async Task<int> ApplySearchIncompleteAsync(SockseekDbContext context, SearchIncompletePersistenceMutation mutation, CancellationToken cancellationToken)
    {
        var search = await context.SearchJobs.FindAsync([mutation.SearchJobId], cancellationToken).ConfigureAwait(false);
        if (search == null || search.ResultPersistenceState is "Pruned" or "Interrupted")
            return 0;
        search.ResultPersistenceState = "Incomplete";
        search.Revision = Math.Max(search.Revision, mutation.Revision);
        return 1;
    }

    private async Task<int> ApplyTransferTerminalAsync(SockseekDbContext context, TransferTerminalPersistenceMutation mutation,
        BatchRows batchRows, CancellationToken cancellationToken)
    {
        int rows = 0;
        if (mutation.OwningJob != null)
            rows += await ApplyJobAsync(context, mutation.OwningJob, batchRows, cancellationToken).ConfigureAwait(false);
        rows += ApplyTransfer(mutation.Transfer, batchRows);
        if (mutation.FinalAttempt != null)
            rows += ApplyAttempt(mutation.FinalAttempt, batchRows);
        return rows;
    }

    private static async Task<SearchJobEntity> GetOrCreateSearchAsync(
        SockseekDbContext context,
        Guid jobId,
        HistoryRows<JobEntity> jobs,
        CancellationToken cancellationToken)
    {
        var search = await context.SearchJobs.FindAsync([jobId], cancellationToken).ConfigureAwait(false);
        if (search != null)
            return search;
        bool jobExists = jobs.TryGetValue(jobId, out _) || context.Jobs.Local.Any(job => job.Id == jobId)
            || await context.Jobs.AnyAsync(job => job.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (!jobExists)
            throw new InvalidOperationException($"Job {jobId} was not persisted before its search data.");
        search = new SearchJobEntity
        {
            JobId = jobId,
            Query = "",
            ResultPersistenceState = "NotPersisted",
        };
        context.SearchJobs.Add(search);
        return search;
    }

    private string? Limit(string? value)
        => value == null || value.Length <= options.MaximumFailureTextLength
            ? value
            : value[..options.MaximumFailureTextLength];

    private static string? Limit(string? value, int maximumLength)
        => value == null || value.Length <= maximumLength
            ? value
            : value[..maximumLength];

    private static bool IsBusy(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 5 or 6 }
            || exception.InnerException != null && IsBusy(exception.InnerException);

    private static Exception WithBatchContext(Exception exception, IReadOnlyList<PersistenceMutation> batch)
    {
        string items = string.Join(", ", batch.Take(3).Select(mutation =>
            $"{mutation.GetType().Name}:{mutation.EntityId:N}@r{mutation.Revision}"));
        if (batch.Count > 3)
            items += $", +{batch.Count - 3} more";
        return new InvalidOperationException($"Persistence batch [{items}] failed: {exception.Message}", exception);
    }

    private static bool IsRecoverable(Exception exception)
    {
        if (exception is DbUpdateException { InnerException: not null } update)
            return IsRecoverable(update.InnerException!);
        if (exception is SqliteException sqlite)
            return sqlite.SqliteErrorCode != 19;
        if (exception is IOException or UnauthorizedAccessException)
            return true;
        return exception.InnerException != null && IsRecoverable(exception.InnerException);
    }

    private static IReadOnlyList<PersistenceMutation> NormalizeBatch(IReadOnlyList<PersistenceMutation> batch)
    {
        var coalesced = new Dictionary<string, PersistenceMutation>();
        var appendOnly = new List<PersistenceMutation>();
        foreach (var mutation in batch)
        {
            string? key = mutation switch
            {
                JobPersistenceMutation job => $"job:{job.JobId}",
                TransferPersistenceMutation transfer => $"transfer:{transfer.TransferId}",
                TransferAttemptPersistenceMutation attempt => $"attempt:{attempt.AttemptId}",
                _ => null,
            };
            if (key == null)
            {
                // Composite completion carries an attempt and owning-job state,
                // not just a replaceable transfer header. Keep each accepted
                // completion so a later relocation/retry cannot erase its facts.
                appendOnly.Add(mutation);
                continue;
            }

            if (!coalesced.TryGetValue(key, out var current))
            {
                coalesced[key] = mutation;
                continue;
            }

            if (current is TransferPersistenceMutation currentTransfer
                && mutation is TransferPersistenceMutation incomingTransfer)
            {
                coalesced[key] = PersistenceInbox.CoalesceTransfer(currentTransfer, incomingTransfer);
                continue;
            }

            if (mutation.Revision > current.Revision
                || mutation.Revision == current.Revision && mutation.Priority > current.Priority)
                coalesced[key] = mutation;
        }

        appendOnly.AddRange(coalesced.Values);
        return appendOnly;
    }

    private static bool IsHistoryMutation(PersistenceMutation mutation)
        => mutation is JobPersistenceMutation or TransferPersistenceMutation
            or TransferAttemptPersistenceMutation or TransferTerminalPersistenceMutation;

    private static int DependencyOrder(PersistenceMutation mutation)
        => mutation switch
        {
            JobPersistenceMutation => 0,
            TransferTerminalPersistenceMutation => 1,
            TransferPersistenceMutation => 1,
            TransferAttemptPersistenceMutation => 2,
            SearchResultsPersistenceMutation => 3,
            SearchIncompletePersistenceMutation => 4,
            SearchCompletionPersistenceMutation => 5,
            _ => 6,
        };

    private enum WriteBatchResult
    {
        Success,
        RecoverableFailure,
        PermanentFailure,
    }
}
