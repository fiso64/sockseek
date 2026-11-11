using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Server;

namespace Tests;

[TestClass]
public sealed class CliJobSettingsParityTests
{
    [TestMethod]
    public void ConfigResolver_SelectsGenericAndMusicBaselinesFromTheTypedJob()
    {
        var file = new ConfigFile("none", []);
        IJobSettingsResolver resolver = ConfigManager.CreateJobSettingsResolver(
            file,
            [],
            new CliSettings());

        DownloadSettings generic = resolver.Resolve(new DownloadSettings(), new SearchJob("manual"));
        DownloadSettings track = resolver.Resolve(
            new DownloadSettings(),
            new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" }));
        DownloadSettings album = resolver.Resolve(
            new DownloadSettings(),
            new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" }));

        Assert.AreEqual(0, generic.Search.NecessaryCond.Formats.Length);
        Assert.AreEqual(0, generic.Search.PreferredCond.Formats.Length);
        CollectionAssert.AreEqual(
            SearchDefaults.Formats.ToArray(),
            track.Search.NecessaryCond.Formats);
        Assert.AreEqual(track.Search.NecessaryCond, album.Search.NecessaryCond);
        Assert.AreEqual(track.Search.PreferredCond, album.Search.PreferredCond);
    }

    [TestMethod]
    public void LocalSubmissionOptions_UseRequestPatchForAutoProfileMatching()
    {
        var autoPatch = new SettingsPatch([new("Search.MaxStaleTime", 7654)]);
        var autoProfile = new SettingsProfile
        {
            Name = "group-request",
            Condition = "group",
            Download = autoPatch,
        };
        var catalog = new ProfileCatalog
        {
            AutoProfiles = [autoProfile],
            NamedProfiles = [autoProfile],
        };
        var inner = new ProfileJobSettingsResolver(
            null,
            catalog,
            namedProfiles: [],
            cliProfile: null);
        var resolver = new SubmissionOptionsJobSettingsResolver(inner);
        var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
        resolver.SetJobOptions(job.Id, new SubmissionOptionsDto(
            DownloadSettings: new DownloadSettingsPatchDto(
                Search: new SearchSettingsPatchDto(GroupResults: true))));

        DownloadSettings settings = resolver.Resolve(new DownloadSettings(), job);

        CollectionAssert.Contains(settings.AppliedAutoProfiles.ToList(), "group-request");
        Assert.AreEqual(7654, settings.Search.MaxStaleTime);
        Assert.IsTrue(settings.Search.GroupResults);
    }
}
