using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core.Settings;

namespace Tests.ConfigParsingTests;

[TestClass]
public sealed class SharingConfigParsingTests
{
    [TestMethod]
    public void SharingDefaults_AreSettledDaemonDefaults()
    {
        var settings = Bind();

        Assert.AreEqual(0, settings.Sharing.Roots.Count);
        Assert.IsTrue(settings.Sharing.ScanOnStart);
        Assert.IsNull(settings.Sharing.RescanInterval);
        Assert.AreEqual(10, settings.Uploads.Slots);
        Assert.IsNull(settings.Uploads.SpeedLimitKiBPerSecond);
        Assert.IsNull(settings.UserPicturePath);
    }

    [TestMethod]
    public void UserPicturePath_IsAPathNormalizedDaemonSetting()
    {
        string relative = Path.Combine("profile", "picture.png");

        EngineSettings settings = Bind("--user-picture", relative);

        Assert.AreEqual(Path.GetFullPath(relative), settings.UserPicturePath);
    }

    [TestMethod]
    public void ShareAndIndependentRestrictionLists_UseReplaceAndExplicitAppend()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-config-sharing");
        string first = Path.Combine(parent, "first, collection");
        string second = Path.Combine(parent, "second");

        var settings = Bind(
            "--share", first,
            "--share", $"+ [Second Alias]{second}",
            "--share-filter", "[0-9]{1,3}",
            "--share-filter", "+ two",
            "--upload-blocked-user", "old",
            "--upload-blocked-user", "Alice, Lee,,Jr",
            "--upload-blocked-user", "+ Bob,,Jr",
            "--upload-blocked-ip", "192.0.2.1, 192.0.2.2",
            "--upload-blocked-ip", "+ 2001:db8::1",
            "--private-message-blocked-user", "Carol,,Jr",
            "--private-message-blocked-user", "+ Dave");

        Assert.AreEqual(2, settings.Sharing.Roots.Count);
        Assert.AreEqual("first, collection", settings.Sharing.Roots[0].EffectiveAlias);
        Assert.AreEqual("Second Alias", settings.Sharing.Roots[1].EffectiveAlias);
        CollectionAssert.AreEqual(new[] { "[0-9]{1,3}", "two" }, settings.Sharing.Filters);
        CollectionAssert.AreEqual(
            new[] { "Alice", "Lee,Jr", "Bob,,Jr" },
            settings.PeerRestrictions.UploadAccess.BlockedUsernames);
        CollectionAssert.AreEqual(
            new[] { "192.0.2.1", "192.0.2.2", "2001:db8::1" },
            settings.PeerRestrictions.UploadAccess.BlockedIpAddresses);
        CollectionAssert.AreEqual(
            new[] { "Carol,Jr", "Dave" },
            settings.PeerRestrictions.PrivateMessages.BlockedUsernames);
    }

    [TestMethod]
    public void UnprefixedListValue_ReplacesEarlierValue()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-config-sharing");
        string first = Path.Combine(parent, "first");
        string second = Path.Combine(parent, "second");

        var settings = Bind("--share", first, "--share", second);

        Assert.AreEqual(1, settings.Sharing.Roots.Count);
        Assert.AreEqual(Path.GetFullPath(second), settings.Sharing.Roots[0].LocalPath);
    }

    [TestMethod]
    public void UploadAndScanPolicy_BindsAndValidates()
    {
        var settings = Bind(
            "--share-scan-on-start", "false",
            "--share-rescan-interval", "30m",
            "--share-search-result-limit", "750",
            "--upload-slots", "4",
            "--upload-speed-limit-kib", "512");

        Assert.AreEqual(750, settings.Sharing.SearchResultLimit);
        Assert.IsFalse(settings.Sharing.ScanOnStart);
        Assert.AreEqual(TimeSpan.FromMinutes(30), settings.Sharing.RescanInterval);
        Assert.AreEqual(4, settings.Uploads.Slots);
        Assert.AreEqual(512, settings.Uploads.SpeedLimitKiBPerSecond);
    }

    [TestMethod]
    [DataRow("--share-search-result-limit", "-1")]
    [DataRow("--upload-slots", "0")]
    [DataRow("--upload-speed-limit-kib", "0")]
    [DataRow("--share-rescan-interval", "30s")]
    [DataRow("--upload-blocked-ip", "192.0.2.0/24")]
    [DataRow("--upload-blocked-ip", "+ 192.0.2.1,192.0.2.2")]
    public void InvalidSharingValues_FailBinding(string option, string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Bind($"{option}={value}"));
    }

    [TestMethod]
    [DataRow("--share-scan-workers")]
    [DataRow("--share-search-concurrency")]
    [DataRow("--share-search-queue-capacity")]
    [DataRow("--upload-max-queued-files-per-user")]
    [DataRow("--upload-max-queued-mib-per-user")]
    public void InternalTuningOptions_AreNotPublicConfiguration(string option)
    {
        Assert.ThrowsExactly<Exception>(() => Bind(option, "1"));
    }

    [TestMethod]
    [DataRow("--shared-files")]
    [DataRow("--shared-folders")]
    [DataRow("--no-modify-share-count")]
    [DataRow("--nmsc")]
    [DataRow("--peer-blocked-user")]
    [DataRow("--peer-blocked-ip")]
    public void RemovedManualCountOptions_AreUnknown(string option)
    {
        Assert.ThrowsExactly<Exception>(() => Bind(option, "1"));
    }

    [TestMethod]
    public void DaemonSharingSettings_AreRejectedInNamedProfiles()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-sharing-profile-{Guid.NewGuid():N}.conf");
        File.WriteAllText(
            path,
            """
            [named]
            upload-slots = 2
            """);

        try
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(() => ConfigManager.Load(path));
            StringAssert.Contains(ex.Message, "not allowed in named or automatic profile");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EngineSettings Bind(params string[] arguments)
    {
        var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
        return ConfigManager.Bind(file, arguments).Engine;
    }
}
