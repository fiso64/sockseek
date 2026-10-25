using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Tests.ClientTests;

namespace Tests.Unit;

[TestClass]
public class SearchFallbackTests
{
    private static Searcher CreateSearcher(ISoulseekClient client)
        => new(client, TestHelpers.CreateUserSuccessTracker(), new DownloadEvents(), 100, 1);

    private static SearchResponse Response(string artist, string title = "Some Song", int length = 200, string user = "User", int speed = 1000)
        => new(user, 1, true, speed, 0,
            [TestHelpers.CreateSlFile($@"Music\{artist}\Some Album\{title}.mp3", length: length, bitrate: 320)]);

    [TestMethod]
    public async Task StringInput_SecondaryArtistFallbackPassesStrictArtist()
    {
        var extracted = await new StringExtractor().GetTracks("Artist1; Artist2 - Title",
            new ExtractionSettings { RequestedMode = ExtractionMode.Song });
        Assert.IsInstanceOfType<SongJob>(extracted);
        var song = (SongJob)extracted;
        var response = new SearchResponse("OnlyArtist2", 1, true, 1000, 0,
            [TestHelpers.CreateSlFile(@"Music\Artist2 - Title.mp3", length: 200)]);
        var client = new MockSoulseekClient([response]);
        var queries = new List<string>();
        client.BeforeSearchAsync = (query, _) =>
        {
            queries.Add(query.SearchText);
            return Task.CompletedTask;
        };
        using var searcher = CreateSearcher(client);
        var search = new SearchSettings();
        search.NecessaryCond.StrictArtist = true;
        search.NecessaryCond.StrictTitle = true;

        await searcher.SearchSong(song, search, new ResponseData(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "artist1 artist2 title", "artist1 title", "artist2 title" }, queries);
        Assert.AreEqual(1, song.Candidates?.Count);
        Assert.AreEqual(@"Music\Artist2 - Title.mp3", song.Candidates![0].File.Filename);
        Assert.IsTrue(search.NecessaryCond.StrictArtistSatisfies(song.Candidates[0].File.Filename, song.Query.Artist));
        Assert.AreEqual("Artist1; Artist2", song.Query.Artist);
        Assert.AreEqual("Title", song.Query.Title);
    }

    [TestMethod]
    [DataRow(0, 1, 0)]
    [DataRow(1, 2, 0)]
    [DataRow(2, 3, 1)]
    public async Task Song_FallbackLevelsAreCumulative(int level, int calls, int candidates)
    {
        var client = new MockSoulseekClient([Response("Knock2")]);
        using var searcher = CreateSearcher(client);
        var query = new SongQuery { Artist = "Henry Fong;Knock2;General Degree", Title = "Some Song", Length = 200 };
        var song = new SongJob(query);
        var search = new SearchSettings { FallbackLevel = level };
        search.NecessaryCond.StrictArtist = true;
        search.NecessaryCond.StrictTitle = true;

        await searcher.SearchSong(song, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(calls, client.SearchCallCount);
        Assert.AreEqual(candidates, song.Candidates?.Count ?? 0);
        Assert.AreEqual("Henry Fong;Knock2;General Degree", song.Query.Artist);
    }

    [TestMethod]
    public async Task Song_StripsOptionalCreditsButPreservesRemixAndMetadata()
    {
        const string title = "Human (feat. Echoes) - Aaron Hibell Remix";
        var client = new MockSoulseekClient([
            Response("John Summit", "Human"),
            Response("John Summit", "Human - Aaron Hibell Remix", user: "Remix"),
        ]);
        using var searcher = CreateSearcher(client);
        var song = new SongJob(new SongQuery { Artist = "John Summit;Echoes;Aaron Hibell", Title = title });
        var search = new SearchSettings();
        search.NecessaryCond.StrictArtist = true;
        search.NecessaryCond.StrictTitle = true;

        await searcher.SearchSong(song, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(2, client.SearchCallCount);
        Assert.AreEqual(1, song.Candidates?.Count);
        Assert.AreEqual("Remix", song.Candidates![0].Response.Username);
        Assert.AreEqual(title, song.Query.Title);
    }

    [TestMethod]
    public async Task Song_SingleArtistAlsoRetriesWithoutCredits()
    {
        var client = new MockSoulseekClient([Response("Artist", "Some Song")]);
        using var searcher = CreateSearcher(client);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Some Song (ft. Guest)" });
        var search = new SearchSettings();
        search.NecessaryCond.StrictTitle = true;
        await searcher.SearchSong(song, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(2, client.SearchCallCount);
        Assert.AreEqual(1, song.Candidates?.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Song_OnlyDesperateLevelRelaxesDurationEvenWhenAlbumEqualsTitle(bool desperate)
    {
        var client = new MockSoulseekClient([Response("Artist", length: 240)]);
        using var searcher = CreateSearcher(client);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Some Song", Album = "Some Song", Length = 200 });
        var search = new SearchSettings { DesperateSearch = desperate };

        await searcher.SearchSong(song, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(desperate ? 1 : 0, song.Candidates?.Count ?? 0);
        if (!desperate)
            Assert.AreEqual(1, client.SearchCallCount, "Equivalent primary-artist retry must be skipped.");
    }

    [TestMethod]
    public async Task Song_StopsWhenNormalResultsExistRegardlessOfPreferences()
    {
        var client = new MockSoulseekClient([Response("First Second")]);
        using var searcher = CreateSearcher(client);
        var song = new SongJob(new SongQuery { Artist = "First;Second", Title = "Some Song" });
        var search = new SearchSettings();
        search.PreferredCond.Formats = ["flac"];

        await searcher.SearchSong(song, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(1, client.SearchCallCount);
        Assert.AreEqual(1, song.Candidates?.Count);
    }

    [TestMethod]
    public async Task Album_RetainsStructuredArtistsForFallbackAndRequiredMatching()
    {
        var client = new MockSoulseekClient([Response("Second")]);
        using var searcher = CreateSearcher(client);
        var album = new AlbumJob(new AlbumQuery { Artist = "First;Second", Album = "Some Album" });
        var search = new SearchSettings();
        search.NecessaryCond.StrictArtist = true;

        await searcher.SearchAlbum(album, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(3, client.SearchCallCount);
        Assert.AreEqual(1, album.Results.Count);
        Assert.AreEqual("First;Second", album.Query.Artist);
    }

    [TestMethod]
    public async Task Album_RawResultsStillStopFallbackEvenIfProjectionRejectsThem()
    {
        var client = new MockSoulseekClient([Response("First Second")]);
        using var searcher = CreateSearcher(client);
        var album = new AlbumJob(new AlbumQuery { Artist = "First;Second", Album = "Some Album" });
        var search = new SearchSettings();
        search.NecessaryFolderCond.MinTrackCount = 2;

        await searcher.SearchAlbum(album, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(1, client.SearchCallCount);
        Assert.AreEqual(0, album.Results.Count);
    }

    [TestMethod]
    public async Task FreeTextSearch_DoesNotAcquireArtistFallbacks()
    {
        var client = new MockSoulseekClient([]);
        using var searcher = CreateSearcher(client);
        await searcher.Search(new SearchJob("First;Second Some Song (feat. Guest)"), new SearchSettings(), new ResponseData(), CancellationToken.None);
        Assert.AreEqual(1, client.SearchCallCount);
    }

    [TestMethod]
    public void FreeTextSearch_ProjectionDoesNotChangeDiscoveryQuery()
    {
        var job = new SearchJob("Original discovery terms")
        {
            DefaultFileProjection = new(new SongQuery { Artist = "First;Second", Title = "Song" }),
        };
        Assert.AreEqual("Original discovery terms", job.NetworkQuery.Title);
        Assert.AreEqual("", job.NetworkQuery.Artist);
    }

    [TestMethod]
    [DataRow("Simon & Garfunkel", "Simon", false)]
    [DataRow("Tyler, the Creator", "Tyler", false)]
    [DataRow("AC/DC;Beyoncé", "AC_DC", true)]
    [DataRow("AC/DC;Beyoncé", "Beyonce", true)]
    [DataRow("First;Second", "Secondhand", false)]
    [DataRow("; First ;; Second ;", "Second", true)]
    public void RequiredArtist_UsesAnyCompleteSemicolonDelimitedArtist(string artists, string candidate, bool expected)
        => Assert.AreEqual(expected, new FileConditions { StrictArtist = true }.StrictArtistSatisfies($@"Music\{candidate}\Song.mp3", artists));

    [TestMethod]
    [DataRow("Second", "Second", false)]
    [DataRow("Second", "Second", true)]
    [DataRow("Second-Artist", "Second Artist", false)]
    [DataRow("Second-Artist", "Second Artist", true)]
    public void PreferredArtist_SecondaryArtistOutranksUnrelatedFasterCandidate(string requested, string shared, bool albumMode)
    {
        var secondary = Response(shared, user: "Matching", speed: 1000);
        var unrelated = Response("Unrelated", user: "Unrelated", speed: 10_000_000);
        var search = new SearchSettings();
        search.PreferredCond.StrictArtist = true;
        var query = new SongQuery { Artist = $"First;{requested}", Title = "Some Song" };
        var results = new[] { (unrelated, unrelated.Files.Single()), (secondary, secondary.Files.Single()) };

        var ordered = ResultSorter.OrderedResults(results, query, search, new(), albumMode: albumMode).ToList();
        Assert.AreEqual("Matching", ordered[0].response.Username);
    }

    [TestMethod]
    public void PreferredArtist_DoesNotPenalizeSecondaryArtistRelativeToPrimary()
    {
        var primary = Response("First", user: "Primary", speed: 1000);
        var secondary = Response("Second", user: "Secondary", speed: 10_000_000);
        var search = new SearchSettings();
        search.PreferredCond.StrictArtist = true;
        var query = new SongQuery { Artist = "First;Second", Title = "Some Song" };
        var ordered = ResultSorter.OrderedResults(
            [(primary, primary.Files.Single()), (secondary, secondary.Files.Single())], query, search, new()).ToList();
        Assert.AreEqual("Secondary", ordered[0].response.Username);
    }

    [TestMethod]
    public void BatchAndIncrementalProjections_AgreeOnArtistAndTitleAlternatives()
    {
        var query = new SongQuery { Artist = "First;Second", Title = "Some Song (feat. Guest)", Length = 200 };
        var search = new SearchSettings { MinSharesAggregate = 2 };
        search.NecessaryCond.StrictArtist = true;
        search.NecessaryCond.StrictTitle = true;
        search.PreferredCond.StrictArtist = true;
        var job = new SearchJob(query);
        job.Session.AddResponse(Response("Second", user: "One"));
        Assert.AreEqual(0, job.GetAggregateTracks(search, new()).Items.Count);
        job.Session.AddResponse(Response("Second", user: "Two"));
        job.Session.AddResponse(Response("Unrelated", user: "Wrong"));

        var incrementalFiles = job.GetSortedTrackCandidates(search, new()).Items;
        Assert.AreEqual(2, incrementalFiles.Count);
        var filtered = job.Snapshot().Where(x => ConditionSatisfactionPolicy.SearchFileSatisfies(search.NecessaryCond, x.Response, x.File, query)).ToList();
        var batchFiles = SearchResultProjector.SortedTrackCandidates(filtered, query, search, new());
        CollectionAssert.AreEqual(batchFiles.Select(x => x.Response.Username).ToArray(), incrementalFiles.Select(x => x.Response.Username).ToArray());

        var incremental = job.GetAggregateTracks(search, new()).Items;
        var batch = SearchResultProjector.AggregateTracks(job.Snapshot(), query, search, new());
        Assert.AreEqual(1, batch.Count);
        Assert.AreEqual(1, incremental.Count);
        Assert.AreEqual(2, incremental[0].Candidates?.Count);
        Assert.AreEqual(batch[0].Query.Artist, incremental[0].Query.Artist);
    }

    [TestMethod]
    public void AlbumProjections_ApplyAnyArtistToRequiredAndPreferredMatching()
    {
        var query = new AlbumQuery { Artist = "First;Second", Album = "Some Album" };
        var search = new SearchSettings { MinSharesAggregate = 2 };
        search.NecessaryCond.StrictArtist = true;
        search.PreferredCond.StrictArtist = true;
        var job = new SearchJob(query);
        job.Session.AddResponse(Response("Second", user: "One"));
        job.Session.AddResponse(Response("Second", user: "Two"));
        job.Session.AddResponse(Response("Unrelated", user: "Wrong"));

        Assert.AreEqual(2, job.GetAlbumFolders(search).Items.Count);
        Assert.AreEqual(2, SearchResultProjector.AlbumFolders(job.Snapshot(), query, search).Count);
        Assert.AreEqual(1, job.GetAggregateAlbums(search).Items.Count);
    }

    [TestMethod]
    public async Task AggregateSearch_UsesFallbackAndRetainsSecondaryArtistGroups()
    {
        var client = new MockSoulseekClient([Response("Second", user: "One"), Response("Second", user: "Two")]);
        using var searcher = CreateSearcher(client);
        var job = new AggregateJob(new SongQuery { Artist = "First;Second", Title = "Some Song" });
        var search = new SearchSettings();
        search.NecessaryCond.StrictArtist = true;
        await searcher.SearchAggregate(job, search, new ResponseData(), CancellationToken.None);

        Assert.AreEqual(3, client.SearchCallCount);
        Assert.AreEqual(1, job.Songs.Count);
    }

    [TestMethod]
    public void Planner_DeduplicatesEffectiveQueriesAndKeepsConditionOverrides()
    {
        var normal = SearchAttemptPlanner.Create(new SongQuery { Artist = "Artist;artist;Artist?", Title = "Song" }, new SearchSettings());
        Assert.AreEqual(1, normal.SelectMany(x => x).Count());

        var desperate = SearchAttemptPlanner.Create(new SongQuery { Artist = "Artist", Title = "Song", Length = 200 }, new SearchSettings { DesperateSearch = true });
        var sameText = desperate.SelectMany(x => x).Where(x => x.Query.SearchText == "artist song").ToArray();
        Assert.AreEqual(2, sameText.Length);
        Assert.AreEqual(3, sameText[0].Conditions.LengthTolerance);
        Assert.AreEqual(-1, sameText[1].Conditions.LengthTolerance);
    }

    [TestMethod]
    public void Planner_LimitsAdditionalRequestsAfterDeduplication()
    {
        var query = new SongQuery { Artist = "First;First;Second;Third;Fourth", Title = "Song" };
        var search = new SearchSettings { MaxFallbackAttempts = 2 };
        var attempts = SearchAttemptPlanner.Create(query, search).SelectMany(x => x).ToArray();
        Assert.AreEqual(3, attempts.Length);
        Assert.AreEqual("second song", attempts[^1].Query.SearchText);
    }

    [TestMethod]
    public void Planner_PreservesNormalDiacriticBatchAndUncertainArtistRecovery()
    {
        var batches = SearchAttemptPlanner.Create(new SongQuery { Artist = "Björk;Guest", Title = "Song" }, new SearchSettings { FallbackLevel = 0 });
        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual(2, batches[0].Length);

        var uncertain = SearchAttemptPlanner.Create(new SongQuery { Artist = "Uploader", Title = "Actual Artist - Song", ArtistMaybeWrong = true }, new SearchSettings { FallbackLevel = 0 });
        Assert.AreEqual(2, uncertain.Count);
        Assert.IsFalse(uncertain[1][0].Conditions.StrictArtist);
    }

    [TestMethod]
    public async Task Fallbacks_RespectBudgetAndSearchNotifications()
    {
        var client = new MockSoulseekClient([]);
        using var searcher = CreateSearcher(client);
        var song = new SongJob(new SongQuery { Artist = "First;First;Second;Third;Fourth", Title = "Song" });
        int notifications = 0;
        await searcher.SearchSong(song, new SearchSettings { MaxFallbackAttempts = 2 }, new ResponseData(), CancellationToken.None,
            onSearch: () => notifications++);
        Assert.AreEqual(3, client.SearchCallCount);
        Assert.AreEqual(3, notifications);
    }

    [TestMethod]
    public async Task Fallbacks_RespectRateLimitingAndCancellation()
    {
        var client = new MockSoulseekClient([]);
        var events = new SearchEvents();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool limited = false;
        events.SearchRateLimited += _ => { limited = true; cts.Cancel(); };
        using var searcher = new Searcher(client, TestHelpers.CreateUserSuccessTracker(), new DownloadEvents(), 1, 60, searchEvents: events);
        var song = new SongJob(new SongQuery { Artist = "First;Second", Title = "Song" });

        await Assert.ThrowsAsync<OperationCanceledException>(() => searcher.SearchSong(song, new SearchSettings(), new ResponseData(), cts.Token));
        Assert.IsTrue(limited);
        Assert.AreEqual(1, client.SearchCallCount);
    }

    [TestMethod]
    public async Task Fallbacks_IncludeDiacriticAlternatives()
    {
        var client = new MockSoulseekClient([Response("Bjork")]);
        using var searcher = CreateSearcher(client);
        var song = new SongJob(new SongQuery { Artist = "Björk;Guest", Title = "Some Song" });
        await searcher.SearchSong(song, new SearchSettings(), new ResponseData(), CancellationToken.None);
        Assert.AreEqual(4, client.SearchCallCount);
        Assert.AreEqual(1, song.Candidates?.Count);
    }

    [TestMethod]
    [DataRow("Simon & Garfunkel")]
    [DataRow("Tyler, the Creator")]
    public void Planner_DoesNotSplitConservativeArtistNames(string artist)
    {
        var attempts = SearchAttemptPlanner.Create(new SongQuery { Artist = artist, Title = "Song" }, new SearchSettings());
        Assert.AreEqual(1, attempts.SelectMany(x => x).Count());
    }

    [TestMethod]
    [DataRow("  Beyoncé__Artist?  ", "Beyonce Artist")]
    [DataRow("\tMusic\\Artist\\Song.mp3\t", "Music\\Artist\\Song.mp3")]
    [DataRow("Artist\tName", "Artist\tName")]
    [DataRow("Music\\Artist\\01 - Song (Remix).mp3", "Music\\Artist\\01 - Song (Remix).mp3")]
    public void StrictPreprocessing_PreservesExistingNormalization(string input, string expected)
        => Assert.AreEqual(expected, FileConditions.StrictStringPreprocess(input));
}
