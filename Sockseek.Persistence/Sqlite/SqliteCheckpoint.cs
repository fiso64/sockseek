using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace Sockseek.Persistence.Sqlite;

/// <summary>
/// SQLite's page-copying work belongs to the writer's lifetime, but must not
/// occupy its command queue. PASSIVE uses SQLite's separate checkpoint lock;
/// application mutations still have exactly one writer and FULL WAL commits.
/// </summary>
internal sealed class SqliteCheckpoint(SqliteConnection writer, ILogger logger,
    Action<Exception> failed) : IAsyncDisposable
{
    // Normally 128 MiB. Copying during each catch-up burst rewrites shared index
    // pages and competes with foreground WAL fsync. Coalesce those copies;
    // FULL commits already make the WAL durable. This is a scheduling target,
    // not a limit on accepted work or WAL growth while readers pin old pages.
    private const int CheckpointFrames = 32_768;
    // Supported by the pinned SQLite runtime: inspect the WAL without copying
    // pages or waiting for readers/writers. SQLitePCLRaw's older API lacks the name.
    private const int CheckpointNoop = -1;
    private static readonly ActivitySource Activities = new("Sockseek.Persistence.Write");
    private readonly object gate = new();
    private readonly Guid operationId = Guid.NewGuid();
    private readonly string connectionString = new SqliteConnectionStringBuilder(writer.ConnectionString)
        { Pooling = false }.ToString();
    private Task? worker;
    private bool requested;
    private volatile bool active;
    private volatile bool stopping;
    private int failureReported;

    public void RequestIfNeeded()
    {
        try
        {
            if (active || stopping) return;
            int rc = raw.sqlite3_wal_checkpoint_v2(writer.Handle, "main", CheckpointNoop,
                out int frames, out int copied);
            if (rc == raw.SQLITE_OK && frames - copied >= CheckpointFrames) Request();
            else if (rc != raw.SQLITE_OK && rc != raw.SQLITE_BUSY)
                ReportFailure(new SqliteException("SQLite could not inspect the persistence WAL.", rc));
            // Another checkpointer can hold the checkpoint lock. A later commit
            // retries; a non-WAL database has no frames to copy.
        }
        catch (Exception exception)
        {
            // This is called after COMMIT: optional checkpoint scheduling must
            // never turn already durable work into a failed/replayed command.
            lock (gate) if (worker is null) active = false;
            ReportFailure(exception);
        }
    }

    internal void Request()
    {
        lock (gate)
        {
            if (stopping) return;
            requested = true;
            active = true;
            if (worker is null)
            {
                // Do not retain an HTTP request's ambient state for this owner.
                // Native checkpoint I/O can block, so it also needs no pool worker.
                using (ExecutionContext.SuppressFlow())
                    worker = Task.Factory.StartNew(Run, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            Monitor.Pulse(gate);
        }
    }

    private void Run()
    {
        using var connection = new SqliteConnection(connectionString);
        while (true)
        {
            lock (gate)
            {
                while (!requested && !stopping) Monitor.Wait(gate);
                if (stopping) return;
                requested = false;
            }
            try
            {
                using var operation = Activities.StartActivity("checkpoint");
                operation?.SetTag("checkpoint.id", operationId);
                var timer = Stopwatch.StartNew();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    connection.Open();
                    // The native checkpoint API does not open a fresh pager's
                    // WAL itself. A metadata read establishes it; without this,
                    // an unpooled connection silently reports -1/-1 frames.
                    using var initialize = connection.CreateCommand();
                    initialize.CommandText = "PRAGMA schema_version;";
                    initialize.ExecuteScalar();
                }
                int rc = raw.sqlite3_wal_checkpoint_v2(connection.Handle, "main", raw.SQLITE_CHECKPOINT_PASSIVE,
                    out int frames, out int copied);
                string outcome = rc == raw.SQLITE_BUSY ? "Busy" : frames < 0 ? "NotWal"
                    : copied < frames ? "ReadersPinned" : "Complete";
                if (rc != raw.SQLITE_OK && rc != raw.SQLITE_BUSY)
                    throw new SqliteException("SQLite could not checkpoint the persistence WAL.", rc);
                operation?.SetTag("checkpoint.outcome", outcome);
                operation?.SetTag("checkpoint.frames", frames);
                operation?.SetTag("checkpoint.copied", copied);
                PersistenceLogMessages.CheckpointCompleted(logger, operationId, outcome, frames, copied,
                    timer.Elapsed.TotalMilliseconds);
                if (rc == raw.SQLITE_OK) Interlocked.Exchange(ref failureReported, 0);
                // Writes may have continued during the copy. Pick up a large
                // remaining backlog even if that burst's last commit has passed.
                int status = raw.sqlite3_wal_checkpoint_v2(connection.Handle, "main", CheckpointNoop,
                    out int remaining, out int done);
                lock (gate)
                    requested |= rc == raw.SQLITE_OK && copied == frames && status == raw.SQLITE_OK
                        && remaining - done >= CheckpointFrames;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
            lock (gate) active = requested;
        }
    }

    private void ReportFailure(Exception exception)
    {
        // A failed copy cannot undo a durable WAL commit. Report once until
        // recovery, then retry when later commits request work.
        if (Interlocked.Exchange(ref failureReported, 1) == 0)
        {
            failed(exception);
            PersistenceLogMessages.CheckpointFailed(logger, operationId, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? finishing;
        lock (gate)
        {
            stopping = true;
            Monitor.Pulse(gate);
            finishing = worker;
        }
        if (finishing is not null) await finishing.ConfigureAwait(false);
    }
}
