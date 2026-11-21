using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Sqlite;

public sealed record DatabaseIntegrityResult(bool IsHealthy, string Result);
public sealed record DatabaseBackupResult(string BackupPath, long SizeBytes, DatabaseIntegrityResult Integrity);
public sealed record WalCheckpointResult(int Busy, int LogFrames, int CheckpointedFrames);
public sealed record DatabaseRestoreResult(string DatabasePath, long SizeBytes, DatabaseIntegrityResult Integrity);

public sealed class SqliteMaintenanceService(
    IDbContextFactory<SockseekDbContext> contextFactory,
    SockseekSqliteOptions options)
{
    public async Task<DatabaseIntegrityResult> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CheckIntegrityAsync((SqliteConnection)context.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseBackupResult> BackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(backupPath);
        if (string.Equals(path, options.GetFullDatabasePath(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup path must differ from the live database path.", nameof(backupPath));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var integrity = await PublishSnapshotAsync((SqliteConnection)context.Database.GetDbConnection(), path, cancellationToken)
            .ConfigureAwait(false);
        return new DatabaseBackupResult(path, new FileInfo(path).Length, integrity);
    }

    internal static async Task<WalCheckpointResult> CheckpointAsync(
        SockseekDbContext context, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SQLite did not return a WAL checkpoint result.");
        return new WalCheckpointResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public static async Task<DatabaseRestoreResult> RestoreOfflineAsync(
        string backupPath,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        string sourcePath = Path.GetFullPath(backupPath);
        string targetPath = Path.GetFullPath(databasePath);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup and database paths must differ.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The backup file does not exist.", sourcePath);
        var targetOptions = new SockseekSqliteOptions(targetPath);
        using var owner = SqliteDatabaseOwner.Acquire(targetOptions);
        SqliteConnectionPool.Clear(targetOptions);
        await using var source = OpenIndependent(sourcePath, SqliteOpenMode.ReadOnly);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var integrity = await PublishSnapshotAsync(source, targetPath, cancellationToken).ConfigureAwait(false);
        return new DatabaseRestoreResult(targetPath, new FileInfo(targetPath).Length, integrity);
    }

    // A published snapshot must be portable as a single file. Keep the previous
    // generation untouched until SQLite has closed and verified the replacement.
    private static async Task<DatabaseIntegrityResult> PublishSnapshotAsync(
        SqliteConnection source, string path, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        PersistenceFilePrivacy.RestrictDirectory(directory);
        string temporary = Path.Combine(directory, $".sockseek-{Guid.NewGuid():N}.db");
        try
        {
            DatabaseIntegrityResult integrity;
            await using (var snapshot = OpenIndependent(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                await snapshot.OpenAsync(cancellationToken).ConfigureAwait(false);
                PersistenceFilePrivacy.RestrictFile(temporary);
                source.BackupDatabase(snapshot);
                await UseSingleFileJournalAsync(snapshot, cancellationToken).ConfigureAwait(false);
                integrity = await CheckIntegrityAsync(snapshot, cancellationToken).ConfigureAwait(false);
                if (!integrity.IsHealthy)
                    throw new InvalidDataException($"Snapshot integrity check failed: {integrity.Result}");

                var dbOptions = new DbContextOptionsBuilder<SockseekDbContext>().UseSqlite(snapshot).Options;
                await using var context = new SockseekDbContext(dbOptions);
                await SqliteInitializer.ValidateMigrationHistoryAsync(context, allowEmpty: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            // A previous WAL generation may still contain committed rows. Let SQLite
            // checkpoint it or report a busy reader. Never unlink another connection's journal.
            if (File.Exists(path + "-wal") || File.Exists(path + "-shm") || File.Exists(path + "-journal"))
            {
                await using var previous = OpenIndependent(path, SqliteOpenMode.ReadWrite);
                await previous.OpenAsync(cancellationToken).ConfigureAwait(false);
                await UseSingleFileJournalAsync(previous, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return integrity;
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(temporary + "-wal");
            File.Delete(temporary + "-shm");
            File.Delete(temporary + "-journal");
        }
    }

    private static SqliteConnection OpenIndependent(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());

    private static async Task UseSingleFileJournalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string,
                "delete", StringComparison.OrdinalIgnoreCase))
            throw new IOException("SQLite could not close the snapshot journal.");
    }

    private static async Task<DatabaseIntegrityResult> CheckIntegrityAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        string result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? "";
        return new DatabaseIntegrityResult(string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), result);
    }
}
