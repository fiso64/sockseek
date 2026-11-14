namespace Sockseek.Persistence.Sqlite;

internal static class PersistenceFilePrivacy
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

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
