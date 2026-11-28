namespace Sockseek.Server;

public static class SockseekDataPaths
{
    public const string DatabaseFileName = "sockseek.db";

    public static string GetDefaultDataDirectory()
    {
        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdgDataHome
                ? xdgDataHome
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
        }

        return Path.GetFullPath(Path.Combine(root, "sockseek"));
    }

    public static string ResolveDataDirectory(string? configuredDataDirectory)
        => string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? GetDefaultDataDirectory()
            : Path.GetFullPath(configuredDataDirectory);

    public static string ResolveDatabasePath(string? configuredDataDirectory)
        => Path.Combine(ResolveDataDirectory(configuredDataDirectory), DatabaseFileName);
}
