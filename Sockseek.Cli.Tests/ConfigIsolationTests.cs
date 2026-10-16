using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public class ConfigIsolationTests
{
    [TestMethod]
    public void ImplicitConfigLoading_StartsWithoutLocalProfiles()
    {
        var file = ConfigManager.Load();

        Assert.AreEqual("", ConfigManager.ExtractConfigPath(["input"]));
        Assert.AreEqual("", file.Path);
        Assert.AreEqual(0, file.Profiles.Count);
    }

    [TestMethod]
    public void ExplicitConfigLoading_StillUsesTheRequestedFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sockseek-config-isolation-{Guid.NewGuid():N}.conf");
        try
        {
            File.WriteAllText(path, "concurrent-searches = 7");

            var extracted = ConfigManager.ExtractConfigPath(["input", "--config", path]);
            var file = ConfigManager.Load(extracted);
            var (engine, _, _) = ConfigManager.Bind(file, ["input"]);

            Assert.AreEqual(path, Path.GetFullPath(extracted));
            Assert.AreEqual(path, Path.GetFullPath(file.Path));
            Assert.AreEqual(7, engine.ConcurrentSearches);
            Assert.AreEqual("none", ConfigManager.ExtractConfigPath(["--config", path, "--no-config"]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
