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

        var delta = DownloadSettingsDeltaMapper.FromDifference(new DownloadSettings(), settings);
        var remote = new DownloadSettings();
        DownloadSettingsDeltaMapper.ApplyTo(remote, delta);
        Assert.AreEqual(settings.Search.FallbackLevel, remote.Search.FallbackLevel);
        Assert.AreEqual(4, remote.Search.MaxFallbackAttempts);

        var patch = DownloadSettingsPatchDtoMapper.FromDifference(new DownloadSettings(), settings);
        remote = new DownloadSettings();
        DownloadSettingsPatchDtoMapper.ApplyTo(remote, patch);
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
    }

    [TestMethod]
    [DataRow("--fallback-level", "-1")]
    [DataRow("--fallback-level", "4")]
    [DataRow("--max-fallback-attempts", "-1")]
    public void InvalidLimits_AreRejected(string flag, string value)
        => Assert.Throws<Exception>(() => Bind(flag, value));
}
