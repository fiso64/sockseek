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
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        string result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? "";
        return new DatabaseIntegrityResult(string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), result);
    }

    public async Task<DatabaseBackupResult> BackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        string fullBackupPath = Path.GetFullPath(backupPath);
        if (string.Equals(fullBackupPath, options.GetFullDatabasePath(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup path must differ from the live database path.", nameof(backupPath));
        string backupDirectory = Path.GetDirectoryName(fullBackupPath)
            ?? throw new InvalidOperationException("The backup path has no parent directory.");
        Directory.CreateDirectory(backupDirectory);
        PersistenceFilePrivacy.RestrictDirectory(backupDirectory);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var source = (SqliteConnection)context.Database.GetDbConnection();
        var destinationString = new SqliteConnectionStringBuilder
        {
            DataSource = fullBackupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
        await using (var destination = new SqliteConnection(destinationString))
        {
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }

        var integrity = await CheckIndependentIntegrityAsync(fullBackupPath, cancellationToken).ConfigureAwait(false);
        if (!integrity.IsHealthy)
            throw new InvalidOperationException($"Backup integrity check failed: {integrity.Result}");
        DeleteSidecars(fullBackupPath);
        PersistenceFilePrivacy.RestrictFile(fullBackupPath);
        return new DatabaseBackupResult(fullBackupPath, new FileInfo(fullBackupPath).Length, integrity);
    }

    public async Task<WalCheckpointResult> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CheckpointAsync(context, cancellationToken).ConfigureAwait(false);
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
        var sourceIntegrity = await CheckIndependentIntegrityAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceIntegrity.IsHealthy)
            throw new InvalidDataException($"Backup integrity check failed: {sourceIntegrity.Result}");

        var targetOptions = new SockseekSqliteOptions(targetPath);
        using var owner = SqliteDatabaseOwner.Acquire(targetOptions);
        string targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(targetDirectory);
        PersistenceFilePrivacy.RestrictDirectory(targetDirectory);
        string temporaryPath = targetPath + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            var sourceString = new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            var destinationString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                ForeignKeys = true,
            }.ToString();
            await using (var source = new SqliteConnection(sourceString))
            await using (var destination = new SqliteConnection(destinationString))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }
            DeleteEmptyReadOnlySidecars(sourcePath);

            var temporaryIntegrity = await CheckIndependentIntegrityAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!temporaryIntegrity.IsHealthy)
                throw new InvalidDataException($"Restored temporary database failed integrity check: {temporaryIntegrity.Result}");

            SqliteConnectionPool.Clear(targetOptions);
            DeleteIfExists(targetPath + "-wal");
            DeleteIfExists(targetPath + "-shm");
            File.Move(temporaryPath, targetPath, overwrite: true);
            PersistenceFilePrivacy.RestrictFile(targetPath);
            var finalIntegrity = await CheckIndependentIntegrityAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!finalIntegrity.IsHealthy)
                throw new InvalidDataException($"Restored database failed final integrity check: {finalIntegrity.Result}");
            return new DatabaseRestoreResult(targetPath, new FileInfo(targetPath).Length, finalIntegrity);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
            DeleteSidecars(temporaryPath);
        }
    }

    private static async Task<DatabaseIntegrityResult> CheckIndependentIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await CheckIndependentIntegrityCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteEmptyReadOnlySidecars(path);
        }
    }

    private static async Task<DatabaseIntegrityResult> CheckIndependentIntegrityCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        string result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? "";
        return new DatabaseIntegrityResult(string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), result);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteSidecars(string databasePath)
    {
        DeleteIfExists(databasePath + "-wal");
        DeleteIfExists(databasePath + "-shm");
    }

    private static void DeleteEmptyReadOnlySidecars(string databasePath)
    {
        string walPath = databasePath + "-wal";
        if (File.Exists(walPath) && new FileInfo(walPath).Length > 0)
            return;
        TryDeleteIfExists(walPath);
        TryDeleteIfExists(databasePath + "-shm");
    }

    private static void TryDeleteIfExists(string path)
    {
        try { DeleteIfExists(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
