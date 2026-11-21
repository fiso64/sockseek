using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SockseekDataPathsTests
{
    [TestMethod]
    public void ResolveDatabasePath_PlacesOwnedFilenameInConfiguredDataDirectory()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), "sockseek-data-" + Guid.NewGuid());

        string databasePath = SockseekDataPaths.ResolveDatabasePath(dataDirectory);

        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(dataDirectory), SockseekDataPaths.DatabaseFileName),
            databasePath);
    }

    [TestMethod]
    public void DefaultDataDirectory_UsesPlatformDataLocation()
    {
        string expectedRoot;
        if (OperatingSystem.IsWindows())
        {
            expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            expectedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }
        else
        {
            expectedRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdgDataHome
                ? xdgDataHome
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
        }

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(expectedRoot, "sockseek")),
            SockseekDataPaths.GetDefaultDataDirectory());
    }
}
