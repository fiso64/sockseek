using Microsoft.Data.Sqlite;

namespace Sockseek.Persistence.Sqlite;

public static class PersistenceOfflineOperations
{
    public static async Task<SqliteInitializationResult> MigrateAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var options = new SockseekSqliteOptions(databasePath);
        using var owner = SqliteDatabaseOwner.Acquire(options);
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
        try { return await new SqliteInitializer(factory, options, owner).InitializeAsync(cancellationToken).ConfigureAwait(false); }
        finally { SqliteConnection.ClearAllPools(); }
    }

    public static async Task<DatabaseIntegrityResult> CheckIntegrityAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        RequireExisting(databasePath);
        var options = new SockseekSqliteOptions(databasePath);
        using var owner = SqliteDatabaseOwner.Acquire(options);
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
        try { return await new SqliteMaintenanceService(factory, options).CheckIntegrityAsync(cancellationToken).ConfigureAwait(false); }
        finally { SqliteConnection.ClearAllPools(); }
    }

    public static async Task<DatabaseBackupResult> BackupAsync(
        string databasePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        RequireExisting(databasePath);
        var options = new SockseekSqliteOptions(databasePath);
        using var owner = SqliteDatabaseOwner.Acquire(options);
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
        try { return await new SqliteMaintenanceService(factory, options).BackupAsync(outputPath, cancellationToken).ConfigureAwait(false); }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static void RequireExisting(string databasePath)
    {
        string path = Path.GetFullPath(databasePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("The Sockseek database does not exist.", path);
    }
}
