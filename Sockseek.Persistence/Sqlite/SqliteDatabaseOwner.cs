namespace Sockseek.Persistence.Sqlite;

public sealed class SqliteDatabaseOwner : IDisposable
{
    private readonly FileStream lease;

    private SqliteDatabaseOwner(string databasePath, string lockPath, FileStream lease)
    {
        DatabasePath = databasePath;
        LockPath = lockPath;
        this.lease = lease;
    }

    public string DatabasePath { get; }
    public string LockPath { get; }

    public static SqliteDatabaseOwner Acquire(SockseekSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string databasePath = options.GetFullDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory."));
        string lockPath = databasePath + ".lock";

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write($"pid={Environment.ProcessId};acquired_at_utc={DateTimeOffset.UtcNow:O}");
                writer.Flush();
            }
            stream.Position = 0;
            return new SqliteDatabaseOwner(databasePath, lockPath, stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"The Sockseek persistence database '{databasePath}' is already owned by another process.",
                ex);
        }
    }

    public void Dispose() => lease.Dispose();
}
