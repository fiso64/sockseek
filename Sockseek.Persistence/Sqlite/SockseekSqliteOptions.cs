using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Sqlite;

public sealed record SockseekSqliteOptions(
    string DatabasePath,
    int DefaultTimeoutSeconds = 5,
    int BusyTimeoutMilliseconds = 5_000)
{
    public string GetFullDatabasePath() => Path.GetFullPath(DatabasePath);
}

public static class SockseekDbContextOptions
{
    public static DbContextOptions<SockseekDbContext> Create(SockseekSqliteOptions options)
        => Create(options, readOnly: false);

    // Runtime feature stores receive these connections. SQLite itself rejects
    // accidental direct writes; the writable context arrives only in a command
    // executed by PersistenceWriter.
    internal static DbContextOptions<SockseekDbContext> CreateReadOnly(SockseekSqliteOptions options)
        => Create(options, readOnly: true);

    private static DbContextOptions<SockseekDbContext> Create(SockseekSqliteOptions options, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DefaultTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Default timeout must be positive.");
        if (options.BusyTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Busy timeout must be positive.");

        string connectionString = CreateConnectionString(options, readOnly);

        return new DbContextOptionsBuilder<SockseekDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(SockseekDbContext).Assembly.FullName))
            .AddInterceptors(new SqliteConnectionInterceptor(options.BusyTimeoutMilliseconds))
            .Options;
    }

    internal static string CreateConnectionString(SockseekSqliteOptions options, bool readOnly = false)
        => new SqliteConnectionStringBuilder
        {
            DataSource = options.GetFullDatabasePath(),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            // Shared-cache table locks defeat concurrent readers even with WAL enabled.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            DefaultTimeout = options.DefaultTimeoutSeconds,
            Pooling = true,
        }.ToString();
}

internal static class SqliteConnectionPool
{
    public static void Clear(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
