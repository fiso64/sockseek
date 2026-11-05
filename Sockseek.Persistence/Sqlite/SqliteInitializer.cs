using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Sqlite;

public sealed record SqliteInitializationResult(string JournalMode, string SynchronousMode, string SchemaVersion);

public sealed class SqliteInitializer(
    IDbContextFactory<SockseekDbContext> contextFactory,
    SockseekSqliteOptions options,
    SqliteDatabaseOwner owner)
{
    public async Task<SqliteInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = options.GetFullDatabasePath();
        if (!string.Equals(databasePath, owner.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The database ownership lease does not match the configured database path.");
        string databaseDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(databaseDirectory);
        PersistenceFilePrivacy.RestrictDirectory(databaseDirectory);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var knownMigrations = context.Database.GetMigrations()
            .ToHashSet(StringComparer.Ordinal);
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var unknownApplied = appliedMigrations
            .Where(migration => !knownMigrations.Contains(migration))
            .ToArray();
        if (unknownApplied.Length > 0)
        {
            throw new PersistenceSchemaCompatibilityException(
                "This database uses an unsupported Sockseek schema. " +
                "The current binary does not recognize applied migrations: " +
                string.Join(", ", unknownApplied) +
                ". Use a compatible binary, or explicitly choose a fresh database for this unreleased v4 baseline. The existing database has not been replaced.");
        }
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        PersistenceFilePrivacy.RestrictFile(databasePath);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var connection = context.Database.GetDbConnection();
        string journalMode = ExecuteScalar(connection, "PRAGMA journal_mode=WAL;");
        string synchronousMode = ExecuteScalar(connection, "PRAGMA synchronous;");
        string schemaVersion = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).LastOrDefault()
            ?? "none";

        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite refused WAL mode and returned '{journalMode}'. Use a supported local filesystem.");
        if (synchronousMode != "2")
            throw new InvalidOperationException($"SQLite synchronous mode must be FULL (2), but was '{synchronousMode}'.");

        return new SqliteInitializationResult(journalMode, synchronousMode, schemaVersion);
    }

    private static string ExecuteScalar(System.Data.Common.DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}
