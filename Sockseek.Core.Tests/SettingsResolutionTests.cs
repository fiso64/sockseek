using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;

namespace Tests.Unit;

[TestClass]
public class SettingsResolutionTests
{
    [TestMethod]
    public void ArtworkMode_IgnoresAudioConditionsAndRetainsIdentityAndUserConditions()
    {
        var settings = ArtworkSettings();
        settings.Search.NecessaryCond.BannedUsers = ["banned"];
        settings.Search.NecessaryCond.StrictTitle = true;
        settings.Search.NecessaryCond.LengthTolerance = 0;
        settings.Search.NecessaryCond.AcceptNoLength = false;
        settings.Search.NecessaryCond.AcceptMissingProps = false;
        settings.Search.NecessaryFolderCond.MinTrackCount = 50;
        settings.Search.NecessaryFolderCond.RequiredTrackTitles = ["Missing track"];

        var effective = settings.ResolveSearchSettings();

        Assert.IsEmpty(effective.NecessaryCond.Formats);
        Assert.IsNull(effective.NecessaryCond.MinBitrate);
        Assert.IsNull(effective.NecessaryCond.LengthTolerance);
        Assert.IsTrue(effective.NecessaryCond.AcceptNoLength);
        Assert.IsTrue(effective.NecessaryCond.StrictArtist);
        Assert.IsTrue(effective.NecessaryCond.StrictAlbum);
        Assert.IsTrue(effective.NecessaryCond.StrictTitle);
        CollectionAssert.AreEqual(new[] { "banned" }, effective.NecessaryCond.BannedUsers);
        Assert.IsNull(effective.NecessaryFolderCond.MinTrackCount);
        Assert.IsEmpty(effective.NecessaryFolderCond.RequiredTrackTitles);
        Assert.IsFalse(effective.StrictAlbumQuality);

        settings.Output.AlbumArtOnly = false;
        Assert.AreSame(settings.Search, settings.ResolveSearchSettings());
        CollectionAssert.AreEqual(new[] { "flac" }, settings.Search.NecessaryCond.Formats);
        Assert.AreEqual(1000, settings.Search.NecessaryCond.MinBitrate);
        Assert.AreEqual(50, settings.Search.NecessaryFolderCond.MinTrackCount);
        Assert.IsTrue(settings.Search.StrictAlbumQuality);
    }

    [TestMethod]
    public void ArtworkMode_DefaultSelectionDoesNotOverwriteTheRequestedSelection()
    {
        var settings = new OutputSettings { AlbumArtOnly = true };
        Assert.AreEqual(AlbumArtOption.Largest, settings.EffectiveAlbumArtOption);
        Assert.AreEqual(AlbumArtOption.Default, settings.AlbumArtOption);
        settings.AlbumArtOption = AlbumArtOption.Most;
        Assert.AreEqual(AlbumArtOption.Most, settings.EffectiveAlbumArtOption);
        settings.AlbumArtOnly = false;
        settings.AlbumArtOption = AlbumArtOption.Default;
        Assert.AreEqual(AlbumArtOption.Default, settings.EffectiveAlbumArtOption);
    }

    [TestMethod]
    public void ArtworkRanking_DoesNotPreferAudioPropertiesOrKnownDuration()
    {
        const string filename = @"Music\Artist\Album\Track.mp3";
        var low = TestHelpers.CreateSlFile(filename, bitrate: 64, length: 0);
        var high = TestHelpers.CreateSlFile(filename, bitrate: 320, length: 180);
        var response = new SearchResponse("user", 1, true, 100, 0, [low]);
        var query = new SongQuery { Artist = "Artist", Title = "Track", Album = "Album", Length = 180 };
        var settings = ArtworkSettings();
        settings.Search.PreferredCond.AcceptNoLength = false;
        var context = ResultSorter.CreateSortKeyContext([], query, settings.ResolveSearchSettings(), new(),
            useBracketCheck: false, useInfer: false, albumMode: true);

        Assert.AreEqual(0, context.CreateKey(response, low).CompareTo(context.CreateKey(response, high)));
    }

    [TestMethod]
    public void ArtworkAlbumProjections_MatchOneShotAndIncrementalResultsAndKeepIdentityFiltering()
    {
        var settings = ArtworkSettings();
        var query = new AlbumQuery { Artist = "Artist", Album = "Album" };
        var job = new SearchJob(query) { Config = settings };
        var mp3 = Response("mp3", "Artist", "mp3");
        var flac = Response("flac", "Artist", "flac");
        var unrelated = Response("other", "Other", "mp3");

        job.Session.AddResponse(mp3);
        job.Session.AddResponse(unrelated);
        var first = job.GetAlbumFolders(settings.Search);
        Assert.HasCount(1, first.Items);
        Assert.AreSame(first, job.GetAlbumFolders(settings.Search), "Artwork views must preserve incremental cache reuse.");

        job.Session.AddResponse(flac);
        var incremental = job.GetAlbumFolders(settings.Search);
        var oneShot = SearchResultProjector.AlbumFolders(job.Snapshot(), query, settings.ResolveSearchSettings());
        Assert.HasCount(2, incremental.Items);
        CollectionAssert.AreEquivalent(oneShot.Select(f => f.Username).ToArray(), incremental.Items.Select(f => f.Username).ToArray());
        var aggregate = job.GetAggregateAlbums(settings.Search);
        Assert.HasCount(1, aggregate.Items);
        Assert.HasCount(2, aggregate.Items[0].Results);

        settings.Output.AlbumArtOnly = false;
        Assert.IsEmpty(job.GetAlbumFolders(settings.Search).Items, "Returning to audio mode must apply the preserved conditions.");
    }

    [TestMethod]
    public void ArtworkTrackProjections_IgnoreAudioConditionsInTrackAndAggregateViews()
    {
        var settings = ArtworkSettings();
        var query = new SongQuery { Artist = "Artist", Title = "Track", Album = "Album", Length = 200 };
        var job = new SearchJob(query) { Config = settings };
        job.Session.AddResponse(Response("mp3", "Artist", "mp3"));
        job.Session.AddResponse(Response("flac", "Artist", "flac"));

        Assert.HasCount(2, job.GetSortedTrackCandidates(settings.Search, new()).Items);
        var aggregate = job.GetAggregateTracks(settings.Search, new ConcurrentDictionary<string, int>());
        Assert.AreEqual(2, aggregate.Items.Sum(track => track.Candidates?.Count ?? 0));
    }

    [TestMethod]
    public void AggregateProjection_UsesBackendDefaultsWithoutOverwritingTheRequestedThreshold()
    {
        var settings = new DownloadSettings();
        var job = new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" }) { Config = settings };
        job.Session.AddResponse(Response("one-source", "Artist", "mp3"));
        Assert.IsEmpty(job.GetAggregateAlbums(settings.Search).Items);
        settings.DefaultMinSharesAggregate = 1;
        Assert.HasCount(1, job.GetAggregateAlbums(settings.Search).Items);
        Assert.IsNull(settings.Search.MinSharesAggregate);
    }

    [TestMethod]
    public void AutomaticIndexWriting_UsesInputDefaultsAndExplicitChoices()
    {
        var settings = new DownloadSettings();
        var queue = new JobList();
        Assert.IsFalse(JobPreparer.WillWriteIndex(settings, queue));
        queue.Add(new SongJob(new SongQuery()) { EnablesIndexByDefault = true });
        Assert.IsTrue(JobPreparer.WillWriteIndex(settings, queue));
        queue.Jobs.Clear();
        settings.Output.IndexFilePath = "index.csv";
        Assert.IsTrue(JobPreparer.WillWriteIndex(settings, queue));
        settings.Output.WriteIndex = false;
        Assert.IsFalse(JobPreparer.WillWriteIndex(settings, queue));
        settings.Output.IndexFilePath = null;
        settings.Output.WriteIndex = true;
        Assert.IsTrue(JobPreparer.WillWriteIndex(settings, queue));
        settings.Output.WriteIndex = null;
        Assert.IsFalse(JobPreparer.WillWriteIndex(settings, queue));
    }

    [TestMethod]
    public void ResolvingDependentValues_DoesNotOverwriteRequestedValues()
    {
        var settings = new DownloadSettings();
        settings.Search.IgnoreOn = -1;
        settings.Search.DownrankOn = -5;
        settings.YouTube.DeletedOnly = true;
        SettingsNormalizer.NormalizeDownloadPaths(settings);
        Assert.AreEqual(-1, settings.Search.IgnoreOn);
        Assert.AreEqual(-5, settings.Search.EffectiveIgnoreOn);
        settings.Search.DownrankOn = 0;
        Assert.AreEqual(-1, settings.Search.EffectiveIgnoreOn);
        Assert.IsFalse(settings.YouTube.GetDeleted);
        Assert.IsTrue(settings.YouTube.DeletedOnly);
    }

    private static DownloadSettings ArtworkSettings()
    {
        var settings = new DownloadSettings();
        settings.Output.AlbumArtOnly = true;
        settings.Search.MinSharesAggregate = 1;
        settings.Search.NecessaryCond = new FileConditions
        {
            Formats = ["flac"], MinBitrate = 1000, MinSampleRate = 96000, MinBitDepth = 24,
            StrictArtist = true, StrictAlbum = true, AcceptMissingProps = false,
        };
        settings.Search.PreferredCond = new FileConditions(settings.Search.NecessaryCond);
        settings.Search.StrictAlbumQuality = true;
        return settings;
    }

    private static SearchResponse Response(string user, string artist, string format)
        => new(user, 1, true, 100, 0,
        [
            TestHelpers.CreateSlFile($@"Music\{artist}\Album\Track.{format}", bitrate: 128, length: 180),
        ]);
}
