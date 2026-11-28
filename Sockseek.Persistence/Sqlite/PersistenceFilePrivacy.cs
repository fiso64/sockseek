namespace Sockseek.Persistence.Sqlite;

internal static class PersistenceFilePrivacy
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // These paths can be operator-selected (for example, a shared backup directory).
    public static void CreateDatabaseDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, PrivateDirectoryMode);
    }

    public static void PrepareDatabaseFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        // SQLite uses the database mode when creating its journals.
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete,
            UnixCreateMode = PrivateFileMode,
        });
        File.SetUnixFileMode(file.SafeFileHandle, PrivateFileMode);
    }

    public static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            return; // User-profile/application-data ACL inheritance is authoritative on Windows.
        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, PrivateFileMode);
    }
}
