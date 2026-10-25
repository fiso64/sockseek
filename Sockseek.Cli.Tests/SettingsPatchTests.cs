using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Tests;

[TestClass]
public class SettingsPatchTests
{
    private static SettingsPatch RoundTrip(SettingsPatch patch) => JsonSerializer.Deserialize<SettingsPatch>(
        JsonSerializer.Serialize(patch, SockseekApiJson.CreateSerializerOptions()),
        SockseekApiJson.CreateSerializerOptions())!;

    [TestMethod]
    public void CliPatch_RoundTripPreservesCompoundOptionsAndTheirOrder()
    {
        var patch = ConfigManager.CreateCliDownloadSettingsPatch([
            "Artist - Title", "--album-art-only", "--album-art", "most", "--strict-artist",
            "--format", "flac", "--on-complete", "-- first", "--on-complete", "+ -- second",
            "--regex", "T:foo;bar", "--regex", "+ A:baz;qux",
            "--incomplete-album-action", "move:failed", "--incomplete-album-action", "keep",
        ])!;
        var local = new DownloadSettings();
        var remote = new DownloadSettings();
        patch.ApplyTo(local);
        RoundTrip(patch).ApplyTo(remote);

        Assert.IsNull(SettingsPatch.Difference(local, remote));
        Assert.AreEqual(AlbumArtOption.Most, remote.Output.AlbumArtOption);
        Assert.IsTrue(remote.Search.NecessaryCond.StrictArtist);
        CollectionAssert.AreEqual(new[] { "flac" }, remote.Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(new[] { "-- first", "-- second" }, remote.Output.OnComplete);
        Assert.HasCount(2, remote.Preprocess.Regex!);
        Assert.AreEqual("foo", remote.Preprocess.Regex![0].Match.Title);
        Assert.AreEqual("qux", remote.Preprocess.Regex[1].Replace.Artist);
        Assert.IsNull(remote.Output.IncompleteAlbumAction.Path);
    }

    [TestMethod]
    public void ExplicitFalseZeroNullAndDefault_OverrideInheritedValues()
    {
        var settings = new DownloadSettings();
        settings.Output.WriteIndex = false;
        settings.Output.WritePlaylist = true;
        settings.Output.ParentDir = "old";
        settings.Output.OnComplete = ["-- old"];
        settings.Extraction.Offset = 8;
        settings.Search.SearchTimeout = 1234;
        var patch = new SettingsPatch([
            new("Output.WriteIndex", true),
            new("Output.WritePlaylist", false),
            new("Output.ParentDir", null),
            new("Output.OnComplete", null),
            new("Extraction.Offset", 0),
        ]);

        RoundTrip(patch).ApplyTo(settings);

        Assert.IsTrue(settings.Output.WriteIndex);
        Assert.IsFalse(settings.Output.WritePlaylist);
        Assert.IsNull(settings.Output.ParentDir);
        Assert.IsNull(settings.Output.OnComplete);
        Assert.AreEqual(0, settings.Extraction.Offset);
        Assert.AreEqual(1234, settings.Search.SearchTimeout);
    }

    [TestMethod]
    public void AppendAndReplace_AreOrderedAndRetainRepeatedValues()
    {
        var settings = new DownloadSettings();
        settings.Output.OnComplete = ["-- inherited"];
        var patch = ConfigManager.CreateCliDownloadSettingsPatch([
            "--on-complete", "+ -- discarded", "--on-complete", "-- replacement",
            "--on-complete", "+ -- repeated", "--on-complete", "+ -- repeated",
        ])!;

        RoundTrip(patch).ApplyTo(settings);

        CollectionAssert.AreEqual(new[] { "-- replacement", "-- repeated", "-- repeated" }, settings.Output.OnComplete);
    }

    [TestMethod]
    public void AlbumArtOnly_UsesInheritedChoiceAndPreservesOptionOrder()
    {
        var settings = new DownloadSettings();
        settings.Output.AlbumArtOption = AlbumArtOption.Most;
        var patch = ConfigManager.CreateCliDownloadSettingsPatch(["--strict-artist", "--album-art-only"])!;

        RoundTrip(patch).ApplyTo(settings);

        Assert.AreEqual(AlbumArtOption.Most, settings.Output.AlbumArtOption);
        Assert.AreNotEqual(true, settings.Search.NecessaryCond.StrictArtist);
        Assert.IsNull(settings.Search.PreferredCond.MinBitrate);
    }

    [TestMethod]
    public void Difference_RoundTripsClearsCollectionsAndNestedChanges()
    {
        var before = new DownloadSettings();
        before.Output.OnComplete = ["-- old"];
        before.Output.IncompleteAlbumAction.Path = "old";
        var after = SettingsCloner.Clone(before);
        after.Output.OnComplete = null;
        after.Output.IncompleteAlbumAction.Path = null;
        after.Search.NecessaryCond.Formats = ["flac"];
        after.Output.WriteIndex = false;
        after.AppliedAutoProfiles.Add("runtime-only");

        RoundTrip(SettingsPatch.Difference(before, after)!).ApplyTo(before);

        Assert.IsNull(SettingsPatch.Difference(before, after));
        Assert.IsEmpty(before.AppliedAutoProfiles);
    }

    [TestMethod]
    public void CloneAndRepeatedPatchApplication_DoNotShareMutableValues()
    {
        var patch = ConfigManager.CreateCliDownloadSettingsPatch([
            "--regex", "foo;bar", "--format", "flac", "--on-complete", "-- original",
        ])!;
        var first = new DownloadSettings();
        patch.ApplyTo(first);
        first.AppliedAutoProfiles.Add("one");
        var clone = SettingsCloner.Clone(first);
        clone.Output.OnComplete!.Add("-- clone");
        clone.Search.NecessaryCond.Formats![0] = "mp3";
        clone.Preprocess.Regex!.Clear();
        clone.AppliedAutoProfiles.Clear();
        var second = new DownloadSettings();
        patch.ApplyTo(second);

        Assert.HasCount(1, first.Output.OnComplete!);
        Assert.HasCount(1, first.Preprocess.Regex!);
        Assert.HasCount(1, first.AppliedAutoProfiles);
        Assert.AreEqual("flac", first.Search.NecessaryCond.Formats![0]);
        Assert.IsNull(SettingsPatch.Difference(first, second));
    }

    [TestMethod]
    [DataRow("Unknown.Value")]
    [DataRow("RuntimePathContext.ConfigDir")]
    [DataRow("AppliedAutoProfiles")]
    [DataRow("Extraction.IsAlbum")]
    [DataRow("PrintJobs")]
    public void Patch_RejectsUnknownRuntimeAndComputedPaths(string path)
    {
        var patch = new SettingsPatch([new(path, null)]);
        Assert.ThrowsExactly<ArgumentException>(() => patch.Validate<DownloadSettings>());
    }

    [TestMethod]
    public void Validation_FailsBeforeApplyingEarlierChanges()
    {
        var settings = new DownloadSettings();
        var patch = new SettingsPatch([new("Output.WriteIndex", false), new("Extraction.Offset", -1)]);

        Assert.ThrowsExactly<ArgumentException>(() => RoundTrip(patch).ApplyTo(settings));
        Assert.IsTrue(settings.Output.WriteIndex);
    }

    [TestMethod]
    public void GroupReplacement_CannotBypassNestedValidation()
    {
        var patch = new SettingsPatch([new("Extraction", new ExtractionSettings { Offset = -1 })]);
        Assert.ThrowsExactly<ArgumentException>(() => RoundTrip(patch).Validate<DownloadSettings>());
    }

    [TestMethod]
    public void GroupReplacement_RoundTripDoesNotApplyComputedAliases()
    {
        var patch = new SettingsPatch([new("Extraction", new ExtractionSettings { Offset = 5 })]);
        var settings = new DownloadSettings();
        RoundTrip(patch).ApplyTo(settings);
        Assert.AreEqual(5, settings.Extraction.Offset);
        Assert.IsNull(settings.Extraction.RequestedMode);
    }

    [TestMethod]
    public void NewSetting_UsesSharedBindingCloningAndPatchingWithoutAMapper()
    {
        var option = SettingsSchema.For(typeof(ExampleSettings)).Options.Single();
        var builder = new SettingsPatchBuilder();
        option.Bind(builder, "3", "--retries");
        var settings = new ExampleSettings();
        RoundTrip(builder.Build()).ApplyTo(settings);
        var clone = SettingsCloner.Clone(settings);
        Assert.AreEqual(3, clone.Retries);
        Assert.IsNull(SettingsPatch.Difference(settings, clone));
        clone.Retries = 5;
        RoundTrip(SettingsPatch.Difference(settings, clone)!).ApplyTo(settings);
        Assert.AreEqual(5, settings.Retries);
    }

    private sealed class ExampleSettings
    {
        [SettingOption("--retries")]
        [Range(0, 10)]
        public int Retries { get; set; } = 2;
    }
}
