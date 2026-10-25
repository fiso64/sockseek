using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core.Settings;

namespace Tests.ConfigParsingTests;

[TestClass]
public class SearchFallbackConfigTests
{
    private static DownloadSettings Bind(params string[] args)
        => ConfigManager.Bind(new ConfigFile("none", new Dictionary<string, ProfileEntry>()), ["Some input", .. args]).Download;

    [TestMethod]
    public void Defaults_EnableBoundedLevelTwo()
    {
        var search = Bind().Search;
        Assert.AreEqual(2, search.FallbackLevel);
        Assert.AreEqual(8, search.MaxFallbackAttempts);
        Assert.IsFalse(search.DesperateSearch);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("3")]
    public void LevelsAndBudget_SurviveProfilesCloningAndRemotePatches(string level)
    {
        var settings = Bind("--fallback-level", level, "--max-fallback-attempts", "4");
        var clone = SettingsCloner.Clone(settings);
        Assert.AreEqual(int.Parse(level), clone.Search.FallbackLevel);
        Assert.AreEqual(4, clone.Search.MaxFallbackAttempts);

        var delta = SettingsPatch.Difference(new DownloadSettings(), settings)!;
        var remote = new DownloadSettings();
        RoundTrip(delta).ApplyTo(remote);
        Assert.AreEqual(settings.Search.FallbackLevel, remote.Search.FallbackLevel);
        Assert.AreEqual(4, remote.Search.MaxFallbackAttempts);

        var patch = ConfigManager.CreateCliDownloadSettingsPatch(["--fallback-level", level, "--max-fallback-attempts", "4"])!;
        remote = new DownloadSettings();
        RoundTrip(patch).ApplyTo(remote);
        Assert.AreEqual(settings.Search.FallbackLevel, remote.Search.FallbackLevel);
        Assert.AreEqual(4, remote.Search.MaxFallbackAttempts);
    }

    [TestMethod]
    public void ExplicitLevel_ClearsEarlierLegacyFlag()
    {
        var search = Bind("--desperate", "--fallback-level", "0").Search;
        Assert.AreEqual(0, search.FallbackLevel);
        Assert.IsFalse(search.DesperateSearch);
        Assert.IsTrue(Bind("--fallback-level", "0", "--desperate").Search.DesperateSearch);

        var remote = new DownloadSettings();
        RoundTrip(ConfigManager.CreateCliDownloadSettingsPatch(["--desperate", "--fallback-level", "0"])!).ApplyTo(remote);
        Assert.AreEqual(0, remote.Search.FallbackLevel);
        Assert.IsFalse(remote.Search.DesperateSearch);
        remote = new DownloadSettings();
        RoundTrip(ConfigManager.CreateCliDownloadSettingsPatch(["--fallback-level", "0", "--desperate"])!).ApplyTo(remote);
        Assert.IsTrue(remote.Search.DesperateSearch);
    }

    [TestMethod]
    [DataRow("--fallback-level", "-1")]
    [DataRow("--fallback-level", "4")]
    [DataRow("--max-fallback-attempts", "-1")]
    public void InvalidLimits_AreRejected(string flag, string value)
        => Assert.Throws<Exception>(() => Bind(flag, value));

    [TestMethod]
    [DataRow("Search.FallbackLevel", -1)]
    [DataRow("Search.FallbackLevel", 4)]
    [DataRow("Search.MaxFallbackAttempts", -1)]
    public void RemotePatches_RejectInvalidLimits(string path, int value)
    {
        var patch = RoundTrip(new SettingsPatch([new(path, value)]));
        Assert.ThrowsExactly<ArgumentException>(() => patch.Validate<DownloadSettings>());
    }

    private static SettingsPatch RoundTrip(SettingsPatch patch)
        => JsonSerializer.Deserialize<SettingsPatch>(
            JsonSerializer.Serialize(patch, SockseekApiJson.CreateSerializerOptions()),
            SockseekApiJson.CreateSerializerOptions())!;
}
