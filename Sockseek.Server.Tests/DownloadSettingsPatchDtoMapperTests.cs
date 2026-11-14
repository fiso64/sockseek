using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public sealed class DownloadSettingsPatchDtoMapperTests
{
    [TestMethod]
    public void NumericConditionClearRemovesInheritedBoundsWithoutChangingOtherFields()
    {
        var settings = new DownloadSettings();
        settings.Search.PreferredCond.MaxSampleRate = 48000;
        settings.Search.PreferredCond.MaxBitrate = 1000;
        settings.Search.NecessaryCond.MinBitrate = 192;
        settings.Search.NecessaryFolderCond.MinTrackCount = 3;
        var patch = new DownloadSettingsPatchDto(Search: new SearchSettingsPatchDto(
            NecessaryCond: new FileConditionsPatchDto(ClearMinBitrate: true),
            PreferredCond: new FileConditionsPatchDto(ClearMaxSampleRate: true),
            NecessaryFolderCond: new FolderConditionsPatchDto(ClearMinTrackCount: true)));

        DownloadSettingsPatchDtoMapper.ApplyTo(settings, patch);

        Assert.IsNull(settings.Search.PreferredCond.MaxSampleRate);
        Assert.IsNull(settings.Search.NecessaryCond.MinBitrate);
        Assert.IsNull(settings.Search.NecessaryFolderCond.MinTrackCount);
        Assert.AreEqual(1000, settings.Search.PreferredCond.MaxBitrate);
        CollectionAssert.IsSubsetOf(
            new[] {
                "Search.NecessaryCond.MinBitrate",
                "Search.PreferredCond.MaxSampleRate",
                "Search.NecessaryFolderCond.MinTrackCount",
            },
            DownloadSettingsPatchDtoMapper.ExplicitFields(patch).ToArray());
    }

    [TestMethod]
    public void NumericClearAndSetComposeInEitherOrderAndDifferenceRetainsClear()
    {
        var set = new DownloadSettingsPatchDto(Search: new SearchSettingsPatchDto(
            PreferredCond: new FileConditionsPatchDto(MinBitrate: 320),
            NecessaryFolderCond: new FolderConditionsPatchDto(MaxTrackCount: 12)));
        var clear = new DownloadSettingsPatchDto(Search: new SearchSettingsPatchDto(
            PreferredCond: new FileConditionsPatchDto(ClearMinBitrate: true),
            NecessaryFolderCond: new FolderConditionsPatchDto(ClearMaxTrackCount: true)));
        DownloadSettingsPatchDto cleared = DownloadSettingsPatchDtoMapper.Combine(set, clear)!;
        Assert.IsNull(cleared.Search!.PreferredCond!.MinBitrate);
        Assert.IsTrue(cleared.Search.PreferredCond.ClearMinBitrate);
        Assert.IsNull(cleared.Search.NecessaryFolderCond!.MaxTrackCount);
        Assert.IsTrue(cleared.Search.NecessaryFolderCond.ClearMaxTrackCount);
        DownloadSettingsPatchDto restored = DownloadSettingsPatchDtoMapper.Combine(clear, set)!;
        Assert.AreEqual(320, restored.Search!.PreferredCond!.MinBitrate);
        Assert.IsNull(restored.Search.PreferredCond.ClearMinBitrate);
        Assert.AreEqual(12, restored.Search.NecessaryFolderCond!.MaxTrackCount);
        Assert.IsNull(restored.Search.NecessaryFolderCond.ClearMaxTrackCount);

        var baseline = new DownloadSettings();
        baseline.Search.PreferredCond.MinBitrate = 200;
        baseline.Search.NecessaryFolderCond.MaxTrackCount = 20;
        var effective = new DownloadSettings();
        effective.Search.PreferredCond.MinBitrate = null;
        effective.Search.NecessaryFolderCond.MaxTrackCount = null;
        DownloadSettingsPatchDto difference = DownloadSettingsPatchDtoMapper.FromDifference(baseline, effective)!;
        Assert.IsTrue(difference.Search!.PreferredCond!.ClearMinBitrate);
        Assert.IsTrue(difference.Search.NecessaryFolderCond!.ClearMaxTrackCount);
        DownloadSettingsPatchDtoMapper.ApplyTo(baseline, difference);
        Assert.IsNull(baseline.Search.PreferredCond.MinBitrate);
        Assert.IsNull(baseline.Search.NecessaryFolderCond.MaxTrackCount);
    }

    [TestMethod]
    public void ContradictoryNumericConditionPatchIsRejected()
    {
        var patch = new DownloadSettingsPatchDto(Search: new SearchSettingsPatchDto(
            PreferredCond: new FileConditionsPatchDto(MinBitrate: 192, ClearMinBitrate: true)));
        Assert.ThrowsExactly<ArgumentException>(() => DownloadSettingsPatchDtoMapper.ApplyTo(new DownloadSettings(), patch));
        Assert.ThrowsExactly<ArgumentException>(() => DownloadSettingsPatchDtoMapper.Combine(null, patch));
    }
}
