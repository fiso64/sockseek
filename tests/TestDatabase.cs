using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence;
using Sockseek.Persistence.Sqlite;

namespace Tests;

/// <summary>
/// Ordinary integration fixtures copy an empty, genuinely migrated schema.
/// Each copy owns its connections and data; migration/startup tests create their
/// databases normally instead of using this helper.
/// </summary>
internal static class TestDatabase
{
    private static readonly Lazy<byte[]> Schema = new(CreateSchema);

    public static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            file.Write(Schema.Value);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true,
        }.ToString());
        connection.Open();
        // The schema's only clock-dependent seed belongs to this fixture.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transfer_accounting_state
            SET complete_from_utc = CAST(strftime('%s', 'now') AS INTEGER) * 1000,
                updated_at_utc = CAST(strftime('%s', 'now') AS INTEGER) * 1000
            WHERE state_id = 1;
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("The fixture schema has no accounting seed.");
    }

    private static byte[] CreateSchema()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-test-schema-").FullName;
        var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
        try
        {
            using (var owner = SqliteDatabaseOwner.Acquire(options))
            {
                using var context = factory.CreateDbContext();
                context.Database.Migrate();
                context.Database.OpenConnection();
                using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var result = command.ExecuteReader();
                if (!result.Read() || result.GetInt32(0) != 0)
                    throw new InvalidOperationException("The fixture schema checkpoint is busy.");
            }
            return File.ReadAllBytes(options.DatabasePath);
        }
        finally
        {
            using var context = factory.CreateDbContext();
            SqliteConnection.ClearPool((SqliteConnection)context.Database.GetDbConnection());
            Directory.Delete(directory, recursive: true);
        }
    }
}
