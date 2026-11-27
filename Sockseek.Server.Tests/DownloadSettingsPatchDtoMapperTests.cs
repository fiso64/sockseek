using Sockseek.Api;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public sealed class DownloadSettingsPatchDtoMapperTests
{
    [TestMethod]
    public void EffectiveSearchSettingsExposeEverySearchScalarByName()
    {
        var settings = new SearchSettings();
        var dto = EffectiveSettingsMapper.ToSearchSettings(settings);
        foreach (var scalar in SettingsSchema.For(typeof(SearchSettings)).Leaves.Values.Where(member => !member.Path.Contains('.')))
        {
            var property = typeof(SearchSettingsPatchDto).GetProperty(scalar.Path);
            Assert.IsNotNull(property, $"Search patch is missing {scalar.Path}.");
            var expected = scalar.Path == nameof(SearchSettings.MinGroupShares)
                ? settings.EffectiveMinGroupShares : scalar.Get(settings);
            Assert.IsNotNull(expected, $"Supply a value for {scalar.Path} to check its effective settings mapping.");
            Assert.AreEqual(expected, property.GetValue(dto), scalar.Path);
        }
    }

    // A search's views rank its results with the settings captured at submission. A
    // setting missing there silently reverts to its default in every view, so each
    // search scalar is either captured or declared here as not about collected results.
    [TestMethod]
    public void EverySearchScalarIsCapturedWithTheSearchOrDeclaredIrrelevantToItsResults()
    {
        string[] notAboutCollectedResults =
        [
            // How the network search runs.
            nameof(SearchSettings.SearchTimeout), nameof(SearchSettings.ResponseLimit), nameof(SearchSettings.MaxStaleTime),
            nameof(SearchSettings.FastSearch), nameof(SearchSettings.FastSearchDelay), nameof(SearchSettings.FastSearchMinUpSpeed),
            nameof(SearchSettings.DesperateSearch), nameof(SearchSettings.FallbackLevel), nameof(SearchSettings.MaxFallbackAttempts),
            nameof(SearchSettings.NoRemoveSpecialChars), nameof(SearchSettings.RemoveSingleCharSearchTerms),
            // How a chosen result is downloaded.
            nameof(SearchSettings.NoBrowseFolder),
            // Captured with the search's query and projection instead.
            nameof(SearchSettings.ArtistMaybeWrong), nameof(SearchSettings.GroupResults),
        ];
        // Captured, but fixed for the search: a view cannot refine them.
        string[] notRefinable = [nameof(SearchSettings.DownrankOn), nameof(SearchSettings.IgnoreOn), nameof(SearchSettings.Relax)];

        foreach (var scalar in SettingsSchema.For(typeof(SearchSettings)).Leaves.Values.Where(member => !member.Path.Contains('.')))
        {
            var settings = new SearchSettings();
            object changed = scalar.Get(settings) switch
            {
                bool value => !value,
                int value => value + 1,
                double value => value + 1,
                null when scalar.ValueType == typeof(int?) => 7,
                _ => throw new InvalidOperationException($"Supply a changed value for {scalar.Path}."),
            };
            new SettingsPatch([new(scalar.Path, changed)]).ApplyTo(settings);

            bool captured = Equals(changed, scalar.Get(SearchProjectionSettingsDefinition.From(settings).ToSettings()));
            bool refinable = typeof(SearchSettingsPatchDto).GetProperty(scalar.Path)!
                .GetValue(EffectiveSettingsMapper.ToSearchSettings(settings).ForResultRefinement()) != null;

            Assert.AreEqual(!notAboutCollectedResults.Contains(scalar.Path), captured,
                $"{scalar.Path}: capture it in {nameof(SearchProjectionSettingsDefinition)} or declare it irrelevant to collected results.");
            Assert.AreEqual(captured && !notRefinable.Contains(scalar.Path), refinable,
                $"{scalar.Path}: result refinement must carry exactly the captured settings a view may change.");
        }
    }

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
