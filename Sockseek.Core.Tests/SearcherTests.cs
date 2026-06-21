using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Tests.ClientTests;
using Sockseek.Core.Settings;
using System.Collections.Concurrent;

namespace Tests.Unit
{
    [TestClass]
    public class SearcherTests
    {
        private MockSoulseekClient CreateMockClient(List<Soulseek.SearchResponse> index)
        {
            return new MockSoulseekClient(index);
        }

        private List<SearchResponse> CreateSophisticatedIndex()
        {
            var index = new List<SearchResponse>();

            // User1: Full ELO - Time (Standard, 10 tracks)
            var u1Files = new List<Soulseek.File>();
            for (int i = 1; i <= 10; i++)
                u1Files.Add(TestHelpers.CreateSlFile($@"ELO\Time\{i:D2}. Track {i}.mp3", length: 180 + i));
            u1Files.Add(TestHelpers.CreateSlFile(@"Abba\Gold\Dancing Queen.mp3", length: 230)); // Noise
            index.Add(new SearchResponse("User1", 1, true, 100, 0, u1Files));

            // User2: Full ELO - Time (Deluxe, 13 tracks)
            var u2Files = new List<Soulseek.File>();
            for (int i = 1; i <= 13; i++)
                u2Files.Add(TestHelpers.CreateSlFile($@"ELO\Time (Deluxe)\{i:D2}. Track {i}.mp3", length: 180 + i));
            index.Add(new SearchResponse("User2", 1, true, 100, 0, u2Files));

            // User3: Partial ELO - Time (Standard, 5 tracks - matching User1 lengths)
            var u3Files = new List<Soulseek.File>();
            for (int i = 1; i <= 5; i++)
                u3Files.Add(TestHelpers.CreateSlFile($@"Music\ELO - Time\{i:D2}. Track {i}.mp3", length: 180 + i));
            index.Add(new SearchResponse("User3", 1, true, 100, 0, u3Files));

            // User4: ELO - Discovery (Full, 9 tracks)
            var u4Files = new List<Soulseek.File>();
            for (int i = 1; i <= 9; i++)
                u4Files.Add(TestHelpers.CreateSlFile($@"ELO\Discovery\{i:D2}. Track {i}.mp3", length: 240 + i));
            index.Add(new SearchResponse("User4", 1, true, 100, 0, u4Files));

            // User5: "ELO" but bad uploader/fake version (drastically different lengths)
            var u5Files = new List<Soulseek.File>();
            for (int i = 1; i <= 10; i++)
                u5Files.Add(TestHelpers.CreateSlFile($@"ELO - Time\{i:D2}. Track {i}.mp3", length: 60)); 
            index.Add(new SearchResponse("User5", 1, true, 100, 0, u5Files));

            // User6: Same as User1 (Standard Time 10 tracks) to test multiple shares
            var u6Files = new List<Soulseek.File>();
            for (int i = 1; i <= 10; i++)
                u6Files.Add(TestHelpers.CreateSlFile($@"Shared\ELO\Time\{i:D2}. Track {i}.mp3", length: 180 + i));
            index.Add(new SearchResponse("User6", 1, true, 100, 0, u6Files));

            return index;
        }

        private Searcher CreateSearcher(ISoulseekClient client, DownloadSettings config)
        {
            var registry = TestHelpers.CreateSessionRegistry();
            return new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
        }

        [TestMethod]
        public async Task SearchSong_FiltersKnownWrongLength_ByDefault()
        {
            var response = new SearchResponse("User", 1, true, 1000, 0,
            [
                TestHelpers.CreateSlFile(@"Music\Artist\Twilight.flac", length: 209),
            ]);
            var client = CreateMockClient([response]);
            var settings = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, settings);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Twilight", Length = 522 });

            await searcher.SearchSong(song, settings.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(0, song.Candidates?.Count ?? 0);
        }

        [TestMethod]
        public async Task SearchSong_UpdatesActivityPhaseThroughSearchAndProjection()
        {
            var client = CreateMockClient(TestHelpers.CreateTestIndex());
            var settings = TestHelpers.CreateDefaultSettings().Download;
            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var song = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" });
            var phases = new List<JobActivityPhase>();

            song.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(Job.ActivityPhase))
                    phases.Add(song.ActivityPhase);
            };

            await searcher.SearchSong(song, settings.Search, new ResponseData(), CancellationToken.None);

            AssertPhaseOrder(
                phases,
                JobActivityPhase.WaitingForSearchConcurrency,
                JobActivityPhase.Searching,
                JobActivityPhase.ProcessingSearchResults);
            Assert.AreEqual(JobLifecycleState.Running, song.LifecycleState);
            Assert.AreEqual(JobActivityPhase.ProcessingSearchResults, song.ActivityPhase);
            Assert.AreEqual(JobTerminalOutcome.None, song.TerminalOutcome);
        }

        [TestMethod]
        public void AlbumFolders_PreservesAlbumModeSorterOrder()
        {
            var badResponse = new SearchResponse("SlowUser", 1, false, 1, 10,
            [
                TestHelpers.CreateSlFile(@"ELO\Discovery\01. Track.mp3", length: 200),
            ]);
            var goodResponse = new SearchResponse("GoodUser", 2, true, 1000, 0,
            [
                TestHelpers.CreateSlFile(@"ELO\Time\01. Track.mp3", length: 200),
            ]);

            var rawResults = new List<(SearchResponse Response, Soulseek.File File)>
            {
                (badResponse, badResponse.Files.First()),
                (goodResponse, goodResponse.Files.First()),
            };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var folders = SearchResultProjector.AlbumFolders(rawResults, new AlbumQuery { Artist = "ELO", Album = "Time" }, search);

            Assert.AreEqual(2, folders.Count);
            Assert.AreEqual("GoodUser", folders[0].Username, "Album folders should inherit first-seen order from album-mode result sorting, not raw arrival order.");
            Assert.AreEqual(@"ELO\Time", folders[0].FolderPath);
        }

        [TestMethod]
        public void AlbumFolders_PrefersFuzzyStrictAlbumMatchOverTechnicalPrefs()
        {
            var unrelated = new SearchResponse("FastUnrelated", 1, true, 30_000 * 1024, 0,
            [
                TestHelpers.CreateSlFile(@"FLAC\CD1\18 - Forze DJ Team - '98 To Piano.flac", length: 210, sampleRate: 44100),
            ]);
            var matching = new SearchResponse("SlowAlbumMatch", 2, true, 500 * 1024, 0,
            [
                TestHelpers.CreateSlFile(@"Diverse System\AD：PIANO X\01 - 噓喰らいの怪物.flac", length: 238, sampleRate: 44100),
            ]);

            var rawResults = new List<(SearchResponse Response, Soulseek.File File)>
            {
                (unrelated, unrelated.Files.First()),
                (matching, matching.Files.First()),
            };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;

            var folders = SearchResultProjector.AlbumFolders(rawResults, new AlbumQuery { Album = "AD:PIANO X" }, search);

            Assert.AreEqual(2, folders.Count);
            Assert.AreEqual("SlowAlbumMatch", folders[0].Username);
            Assert.AreEqual(@"Diverse System\AD：PIANO X", folders[0].FolderPath);
        }

        [TestMethod]
        public void AlbumFolders_RequiredQualityConditions_RankFoldersByCoverageWithoutPartialFiltering()
        {
            static SearchResponse Response(string user, params string[] extensions)
            {
                var files = extensions
                    .Select((extension, index) => TestHelpers.CreateSlFile($@"ELO\Time\{index + 1:D2}. Track {index + 1}.{extension}", length: 180 + index))
                    .ToArray();
                return new SearchResponse(user, 1, true, 1000, 0, files);
            }

            var full = Response("full", "flac", "flac", "flac");
            var high = Response("high", "flac", "flac", "mp3");
            var low = Response("low", "flac", "mp3", "mp3");
            var none = Response("none", "mp3", "mp3", "mp3");
            var rawResults = new[] { none, low, high, full }
                .SelectMany(response => response.Files.Select(file => (response, file)))
                .ToList();
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryCond.Formats = ["flac"];

            var folders = SearchResultProjector.AlbumFolders(rawResults, new AlbumQuery { Artist = "ELO", Album = "Time" }, search);

            CollectionAssert.AreEqual(new[] { "full", "high", "low" }, folders.Select(f => f.Username).ToList());
            Assert.AreEqual(3, folders.Single(f => f.Username == "high").Files.Count, "Mixed-quality folders must remain whole; quality conditions rank/reject folders, not individual files.");
            Assert.AreEqual(2, folders.Single(f => f.Username == "high").SearchAudioQualityCoverage.MatchingFileCount);
            Assert.AreEqual(3, folders.Single(f => f.Username == "high").SearchAudioQualityCoverage.AudioFileCount);
            Assert.AreEqual(2, folders.Single(f => f.Username == "high").SearchAudioQualityCoverage.Format.MatchingFileCount);
        }

        [TestMethod]
        public void AlbumFolders_StrictAlbumQuality_RequiresEveryAudioFileToMatch()
        {
            var full = new SearchResponse("full", 1, true, 1000, 0,
            [
                TestHelpers.CreateSlFile(@"ELO\Time\01. Track 1.flac", length: 180),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Track 2.flac", length: 181),
            ]);
            var mixed = new SearchResponse("mixed", 1, true, 1000, 0,
            [
                TestHelpers.CreateSlFile(@"ELO\Time\01. Track 1.flac", length: 180),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Track 2.mp3", length: 181),
            ]);
            var rawResults = new[] { mixed, full }
                .SelectMany(response => response.Files.Select(file => (response, file)))
                .ToList();
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryCond.Formats = ["flac"];
            search.StrictAlbumQuality = true;

            var folders = SearchResultProjector.AlbumFolders(rawResults, new AlbumQuery { Artist = "ELO", Album = "Time" }, search);

            CollectionAssert.AreEqual(new[] { "full" }, folders.Select(f => f.Username).ToList());
        }

        [TestMethod]
        public void AlbumFolders_AlbumIdentityBeatsQualityCoverage()
        {
            var matchingMixed = new SearchResponse("matching-mixed", 1, true, 500 * 1024, 0,
            [
                TestHelpers.CreateSlFile(@"Diverse System\AD：PIANO X\01 - Song.flac", length: 180),
                TestHelpers.CreateSlFile(@"Diverse System\AD：PIANO X\02 - Song.mp3", length: 181),
            ]);
            var unrelatedFull = new SearchResponse("unrelated-full", 1, true, 30_000 * 1024, 0,
            [
                TestHelpers.CreateSlFile(@"FLAC\CD1\01 - Song.flac", length: 180),
                TestHelpers.CreateSlFile(@"FLAC\CD1\02 - Song.flac", length: 181),
            ]);
            var rawResults = new[] { unrelatedFull, matchingMixed }
                .SelectMany(response => response.Files.Select(file => (response, file)))
                .ToList();
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryCond.Formats = ["flac"];

            var folders = SearchResultProjector.AlbumFolders(rawResults, new AlbumQuery { Album = "AD:PIANO X" }, search);

            Assert.AreEqual("matching-mixed", folders[0].Username);
        }

        [TestMethod]
        public void AlbumFolders_RequiredQualityConditions_RankSeparateCoverageBucketsInSortOrder()
        {
            static SearchResponse Response(string user, params Soulseek.File[] files)
                => new(user, 1, true, 1000, 0, files);

            var perfectFormatWeakBitrate = Response("format-wins",
                TestHelpers.CreateSlFile(@"ELO\Time\01. Track 1.flac", length: 180, bitrate: 900),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Track 2.flac", length: 181, bitrate: 128),
                TestHelpers.CreateSlFile(@"ELO\Time\03. Track 3.flac", length: 182, bitrate: 128));
            var weakerFormatPerfectBitrate = Response("bitrate-loses",
                TestHelpers.CreateSlFile(@"ELO\Time\01. Track 1.flac", length: 180, bitrate: 900),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Track 2.flac", length: 181, bitrate: 900),
                TestHelpers.CreateSlFile(@"ELO\Time\03. Track 3.mp3", length: 182, bitrate: 900));
            var rawResults = new[] { weakerFormatPerfectBitrate, perfectFormatWeakBitrate }
                .SelectMany(response => response.Files.Select(file => (response, file)))
                .ToList();
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryCond.Formats = ["flac"];
            search.NecessaryCond.MinBitrate = 900;

            var folders = SearchResultProjector.AlbumFolders(rawResults, new AlbumQuery { Artist = "ELO", Album = "Time" }, search);

            CollectionAssert.AreEqual(new[] { "format-wins", "bitrate-loses" }, folders.Select(f => f.Username).ToList());
            Assert.AreEqual(3, folders[0].SearchAudioQualityCoverage.Format.MatchingFileCount);
            Assert.AreEqual(1, folders[0].SearchAudioQualityCoverage.Bitrate.MatchingFileCount);
            Assert.AreEqual(2, folders[1].SearchAudioQualityCoverage.Format.MatchingFileCount);
            Assert.AreEqual(3, folders[1].SearchAudioQualityCoverage.Bitrate.MatchingFileCount);
        }

        [TestMethod]
        public void AlbumFolders_IgnoresFuzzyStrictAlbumMatch_WhenStringSortConditionsIgnored()
        {
            var unrelated = new SearchResponse("FastUnrelated", 1, true, 30_000 * 1024, 0,
            [
                TestHelpers.CreateSlFile(@"FLAC\CD1\18 - Forze DJ Team - '98 To Piano.flac", length: 210, sampleRate: 44100),
            ]);
            var matching = new SearchResponse("SlowAlbumMatch", 2, true, 500 * 1024, 0,
            [
                TestHelpers.CreateSlFile(@"Diverse System\AD：PIANO X\01 - 噓喰らいの怪物.flac", length: 238, sampleRate: 44100),
            ]);

            var rawResults = new List<(SearchResponse Response, Soulseek.File File)>
            {
                (matching, matching.Files.First()),
                (unrelated, unrelated.Files.First()),
            };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;

            var folders = SearchResultProjector.AlbumFolders(
                rawResults,
                new AlbumQuery { Album = "AD:PIANO X" },
                search,
                ignoreStringSortConditions: true);

            Assert.AreEqual(2, folders.Count);
            Assert.AreEqual("FastUnrelated", folders[0].Username);
        }

        [TestMethod]
        public void IncrementalAlbumFolders_MatchesFullAlbumFolders_WhenFedInChunks()
        {
            var rawResults = new List<(SearchResponse Response, Soulseek.File File)>();
            for (int user = 0; user < 8; user++)
            {
                var files = new List<Soulseek.File>();
                for (int track = 1; track <= 4; track++)
                    files.Add(TestHelpers.CreateSlFile($@"Music\User{user}\Album {user % 3}\{track:D2}. Track {track}.flac", length: 180 + track));
                files.Add(TestHelpers.CreateSlFile($@"Music\User{user}\Album {user % 3}\cover.jpg"));

                var response = new SearchResponse($"User{user}", 1, user % 2 == 0, (100 + user) * 1024, 0, files);
                rawResults.AddRange(files.Select(file => (response, file)));
            }

            var query = new AlbumQuery { Artist = "Artist", Album = "Album" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var expected = SearchResultProjector.AlbumFolders(rawResults, query, search);
            var incremental = new IncrementalAlbumFolderProjector(query, search, new ConcurrentDictionary<string, int>());

            foreach (var chunk in rawResults.Chunk(5))
                incremental.AddRange(chunk);

            var actual = incremental.Snapshot();

            CollectionAssert.AreEqual(
                expected.Select(x => x.Username + "\\" + x.FolderPath).ToList(),
                actual.Select(x => x.Username + "\\" + x.FolderPath).ToList());
            CollectionAssert.AreEqual(
                expected.Select(x => x.SearchAudioFileCount).ToList(),
                actual.Select(x => x.SearchAudioFileCount).ToList());
        }

        [TestMethod]
        public void IncrementalAlbumFolders_MergesParentAndChildDirectories_WhenParentArrivesLast()
        {
            var childFile = TestHelpers.CreateSlFile(@"ELO\Time\Disc 1\01. Twilight.flac", length: 209);
            var parentFile = TestHelpers.CreateSlFile(@"ELO\Time\Cover.jpg");
            var childResponse = new SearchResponse("User1", 1, true, 1000, 0, [childFile]);
            var parentResponse = new SearchResponse("User1", 1, true, 1000, 0, [parentFile]);
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var incremental = new IncrementalAlbumFolderProjector(query, search);

            incremental.AddRange([(childResponse, childFile)]);
            incremental.AddRange([(parentResponse, parentFile)]);

            var folders = incremental.Snapshot();

            Assert.AreEqual(1, folders.Count);
            Assert.AreEqual(@"ELO\Time", folders[0].FolderPath);
            Assert.AreEqual(1, folders[0].SearchAudioFileCount);
            Assert.AreEqual(2, folders[0].Files.Count);
        }

        [TestMethod]
        public void IncrementalAlbumFolderChanges_ReportsAddedAndUpdatedFolders()
        {
            var track1 = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var track2 = TestHelpers.CreateSlFile(@"ELO\Time\02. Yours Truly.flac", length: 200);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track1, track2]);
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var incremental = new IncrementalAlbumFolderProjector(query, search);

            var first = incremental.AddRangeAndGetChanges([(response, track1)]);
            var second = incremental.AddRangeAndGetChanges([(response, track2)]);

            Assert.AreEqual(1, first.Added.Count);
            Assert.AreEqual(0, first.Updated.Count);
            Assert.AreEqual(0, first.Removed.Count);
            Assert.AreEqual(0, second.Added.Count);
            Assert.AreEqual(1, second.Updated.Count);
            Assert.AreEqual(0, second.Removed.Count);
            Assert.AreEqual(2, second.Updated[0].SearchAudioFileCount);
        }

        [TestMethod]
        public void IncrementalAlbumFolderChanges_ReportsRemovedFolderWhenNewFilesViolateTrackCount()
        {
            var track1 = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var track2 = TestHelpers.CreateSlFile(@"ELO\Time\02. Yours Truly.flac", length: 200);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track1, track2]);
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryFolderCond.MaxTrackCount = 1;
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var incremental = new IncrementalAlbumFolderProjector(query, search);

            var first = incremental.AddRangeAndGetChanges([(response, track1)]);
            var second = incremental.AddRangeAndGetChanges([(response, track2)]);

            Assert.AreEqual(@"ELO\Time", first.Added[0].FolderPath);
            Assert.AreEqual(0, second.Added.Count);
            Assert.AreEqual(0, second.Updated.Count);
            Assert.AreEqual(1, second.Removed.Count);
            Assert.AreEqual(@"ELO\Time", second.Removed[0].FolderPath);
            Assert.AreEqual(0, second.Folders.Count);
        }

        [TestMethod]
        public void AlbumFolders_MinTrackCount_FiltersNormalAlbumSearchResult()
        {
            var track = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track]);
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryFolderCond.MinTrackCount = 10;
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };

            var folders = SearchResultProjector.AlbumFolders([(response, track)], query, search);

            Assert.AreEqual(0, folders.Count,
                "A normal album search result with too few visible tracks should be filtered before slow folder browsing.");
        }

        [TestMethod]
        public void AlbumFolders_MinTrackCount_DoesNotFilterTrackHintNetworkSearchResult()
        {
            var track = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track]);
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryFolderCond.MinTrackCount = 10;
            var query = new AlbumQuery { Artist = "ELO", SearchHint = "Twilight" };

            var folders = SearchResultProjector.AlbumFolders([(response, track)], query, search);

            Assert.AreEqual(1, folders.Count,
                "When SearchHint supplies the network query, visible results may be only the hinted track and must not prove min-count failure.");
            Assert.AreEqual(@"ELO\Time", folders[0].FolderPath);
            Assert.AreEqual(1, folders[0].SearchAudioFileCount);
            Assert.IsFalse(folders[0].IsFullyRetrieved);
        }

        [TestMethod]
        public void AlbumFolders_MinTrackCount_DoesNotFilterStrictTitleSearchHintResult()
        {
            var track = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track]);
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryFolderCond.MinTrackCount = 10;
            search.NecessaryCond.StrictTitle = true;
            var query = new AlbumQuery { Artist = "ELO", Album = "Time", SearchHint = "Twilight" };

            var folders = SearchResultProjector.AlbumFolders([(response, track)], query, search);

            Assert.AreEqual(1, folders.Count,
                "When SearchHint can become a title filter, visible results may be partial and must not prove min-count failure.");
            Assert.AreEqual(@"ELO\Time", folders[0].FolderPath);
        }

        [TestMethod]
        public void AlbumFolders_MaxTrackCount_FiltersVisibleOverflow()
        {
            var track1 = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var track2 = TestHelpers.CreateSlFile(@"ELO\Time\02. Yours Truly.flac", length: 200);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track1, track2]);
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryFolderCond.MaxTrackCount = 1;
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };

            var folders = SearchResultProjector.AlbumFolders([(response, track1), (response, track2)], query, search);

            Assert.AreEqual(0, folders.Count,
                "A search result with more visible tracks than the max proves the complete folder cannot satisfy the max.");
        }

        [TestMethod]
        public void IncrementalAlbumFolderChanges_MinTrackCount_FiltersNormalAlbumSearchResult()
        {
            var track = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track]);
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.NecessaryFolderCond.MinTrackCount = 10;
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var incremental = new IncrementalAlbumFolderProjector(query, search);

            var changes = incremental.AddRangeAndGetChanges([(response, track)]);

            Assert.AreEqual(0, changes.Added.Count);
            Assert.AreEqual(0, changes.Folders.Count);
        }

        [TestMethod]
        public void IncrementalAlbumFolders_CollapsesDiscFoldersLikeFullAlbumFolders()
        {
            var disc1File = TestHelpers.CreateSlFile(@"ELO\Time\CD 1\01. Twilight.flac", length: 209);
            var disc2File = TestHelpers.CreateSlFile(@"ELO\Time\CD 2\02. Hold On Tight.flac", length: 200);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [disc1File, disc2File]);
            var rawResults = new List<(SearchResponse Response, Soulseek.File File)>
            {
                (response, disc1File),
                (response, disc2File),
            };
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var expected = SearchResultProjector.AlbumFolders(rawResults, query, search);
            var incremental = new IncrementalAlbumFolderProjector(query, search);

            incremental.AddRange(rawResults.Take(1));
            incremental.AddRange(rawResults.Skip(1));
            var actual = incremental.Snapshot();

            Assert.AreEqual(1, actual.Count);
            Assert.AreEqual(expected[0].FolderPath, actual[0].FolderPath);
            Assert.AreEqual(2, actual[0].SearchAudioFileCount);
        }

        [TestMethod]
        public void AlbumFolders_GroupsParentArtAndDiscSiblings_OrderIndependently()
        {
            var cover = TestHelpers.CreateSlFile(@"ELO\Time\cover.jpg", extension: ".jpg");
            var disc1 = TestHelpers.CreateSlFile(@"ELO\Time\Disc 1\01. Prologue.flac", length: 60);
            var disc2 = TestHelpers.CreateSlFile(@"ELO\Time\Disc 2\01. Twilight.flac", length: 209);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [cover, disc1, disc2]);
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;

            var forward = SearchResultProjector.AlbumFolders(
                [(response, cover), (response, disc1), (response, disc2)],
                query,
                search);
            var reversed = SearchResultProjector.AlbumFolders(
                [(response, disc2), (response, disc1), (response, cover)],
                query,
                search);

            Assert.AreEqual(1, forward.Count);
            Assert.AreEqual(1, reversed.Count);
            Assert.AreEqual(@"ELO\Time", forward[0].FolderPath);
            Assert.AreEqual(@"ELO\Time", reversed[0].FolderPath);
            CollectionAssert.AreEqual(
                forward[0].Files.Select(x => x.ResolvedTarget!.Filename).ToList(),
                reversed[0].Files.Select(x => x.ResolvedTarget!.Filename).ToList());
        }

        [TestMethod]
        public void SongUpgrade_ToAlbum_UsesSourceAlbumAndRequiresSourceTrackInFolder()
        {
            var song = new SongJob(new SongQuery
            {
                Artist = "Electric Light Orchestra",
                Album = "Time",
                Title = "Twilight",
                Length = 209,
            });

            var upgraded = song.Upgrade(album: true, aggregate: false).Single();
            var album = (AlbumJob)upgraded;

            Assert.AreEqual("Electric Light Orchestra", album.Query.Artist);
            Assert.AreEqual("Time", album.Query.Album);
            Assert.AreEqual("", album.Query.SearchHint);
            Assert.IsNotNull(album.ExtractorFolderCond);
            CollectionAssert.AreEqual(new[] { "Twilight" }, album.ExtractorFolderCond.RequiredTrackTitles);
        }

        [TestMethod]
        public async Task SearchAlbum_RequiredTrackTitle_FiltersFoldersWithoutSourceTrack()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Time\01. Prologue.mp3", length: 60),
                    TestHelpers.CreateSlFile(@"ELO\Time\02. Twilight.mp3", length: 209),
                    TestHelpers.CreateSlFile(@"ELO\Time\03. Yours Truly 2095.mp3", length: 201),
                ]),
                new("User2", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Time\01. Prologue.mp3", length: 60),
                    TestHelpers.CreateSlFile(@"ELO\Time\03. Yours Truly 2095.mp3", length: 201),
                ]),
            };
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.NecessaryFolderCond.RequiredTrackTitles = ["Twilight"];
            var searcher = CreateSearcher(client, config);
            var job = new AlbumJob(new AlbumQuery { Artist = "ELO", Album = "Time" });

            await searcher.SearchAlbum(job, config.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(1, job.Results.Count);
            Assert.AreEqual("User1", job.Results[0].Username);
        }

        [TestMethod]
        public async Task CompleteFolder_PrependsBrowseDirectory_WhenBrowseFilesAreBasenames()
        {
            var track1 = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.mp3", length: 209);
            var track2 = TestHelpers.CreateSlFile(@"ELO\Time\02. Yours Truly 2095.mp3", length: 201);
            var response = new SearchResponse("User1", 1, true, 100, 0, [track1, track2]);
            var client = new MockSoulseekClient([response]) { BrowseReturnsBasenames = true };
            var config = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, config);
            var folder = new AlbumFolder(
                "User1",
                @"ELO\Time",
                [
                    new SongJob(new SongQuery { Artist = "ELO", Album = "Time", Title = "Twilight" })
                    {
                        ResolvedTarget = new FileCandidate(response, track1),
                    },
                ]);

            int newFiles = await searcher.CompleteFolder(folder, CancellationToken.None);

            Assert.AreEqual(1, newFiles);
            Assert.AreEqual(2, folder.Files.Count);
            Assert.AreEqual(@"ELO\Time\02. Yours Truly 2095.mp3", folder.Files[1].ResolvedTarget!.Filename);
        }

        [TestMethod]
        public async Task SearchAlbum_RequiredTrackTitles_RequiresEverySourceTrack()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Time\01. Prologue.mp3", length: 60),
                    TestHelpers.CreateSlFile(@"ELO\Time\02. Twilight.mp3", length: 209),
                ]),
                new("User2", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Time\01. Prologue.mp3", length: 60),
                    TestHelpers.CreateSlFile(@"ELO\Time\02. Twilight.mp3", length: 209),
                    TestHelpers.CreateSlFile(@"ELO\Time\03. Yours Truly 2095.mp3", length: 201),
                ]),
            };
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.NecessaryFolderCond.RequiredTrackTitles = ["Twilight", "Yours Truly 2095"];
            var searcher = CreateSearcher(client, config);
            var job = new AlbumJob(new AlbumQuery { Artist = "ELO", Album = "Time" });

            await searcher.SearchAlbum(job, config.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(1, job.Results.Count);
            Assert.AreEqual("User2", job.Results[0].Username);
        }

        [TestMethod]
        public async Task SearchAlbum_LargeResult_GroupsByFolderAndFiltersIncomplete()
        {
            var index = CreateSophisticatedIndex();
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, config);
            
            // Search for "ELO Time" specifically
            var job = new AlbumJob(new AlbumQuery { Artist = "ELO", Album = "Time" });
            var responseData = new ResponseData();

            await searcher.SearchAlbum(job, config.Search, responseData, CancellationToken.None);

            // We expect folders matching "ELO" AND "Time" (or at least "Time")
            // 1. User1: ELO\Time (10 tracks)
            // 2. User2: ELO\Time (Deluxe) (13 tracks)
            // 3. User3: Music\ELO - Time (5 tracks)
            // 5. User5: ELO - Time (10 tracks)
            // 6. User6: Shared\ELO\Time (10 tracks)
            // (User4: Discovery - should NOT be returned by MockSoulseekClient because query terms include "Time")
            
            Assert.AreEqual(5, job.Results.Count, "Should find folders matching album name search terms.");
            Assert.IsTrue(job.Results.Any(f => f.Username == "User1"), "User1 folder missing.");
            Assert.IsTrue(job.Results.All(f => !f.Files.Any(fi => fi.ResolvedTarget!.Filename.Contains("Dancing Queen"))), "Noise file not filtered!");
        }

        [TestMethod]
        public async Task SearchAlbum_MergesChildDirectoriesIntoAlbumFolder()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Time\01. Prologue.mp3", length: 60),
                    TestHelpers.CreateSlFile(@"ELO\Time\02. Twilight.mp3", length: 209),
                    TestHelpers.CreateSlFile(@"ELO\Time\Scans\Time booklet.jpg"),
                ]),
            };
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, config);
            var job = new AlbumJob(new AlbumQuery { Artist = "ELO", Album = "Time" });

            await searcher.SearchAlbum(job, config.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(1, job.Results.Count, "Child directories should merge into the parent album folder.");
            Assert.AreEqual(@"ELO\Time", job.Results[0].FolderPath);
            Assert.IsTrue(job.Results[0].Files.Any(f => f.ResolvedTarget!.Filename.EndsWith(@"Scans\Time booklet.jpg")),
                "Merged album folder should retain non-audio files from child directories.");
        }

        [TestMethod]
        public async Task SearchAlbum_DiscSubfoldersCollapseToAlbumFolder()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Time\Disc 1\01. Prologue.mp3", length: 60),
                    TestHelpers.CreateSlFile(@"ELO\Time\Disc 2\01. Twilight.mp3", length: 209),
                ]),
            };
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, config);
            var job = new AlbumJob(new AlbumQuery { Artist = "ELO", Album = "Time" });

            await searcher.SearchAlbum(job, config.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(1, job.Results.Count, "Disc folders should be treated as one album folder.");
            Assert.AreEqual(@"ELO\Time", job.Results[0].FolderPath);
            Assert.AreEqual(2, job.Results[0].Files.Count(f => !f.IsNotAudio));
        }

        [TestMethod]
        public async Task SearchAlbum_MatchesSearchJobAlbumProjection()
        {
            var index = CreateSophisticatedIndex();
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, config);
            var albumQuery = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var albumJob = new AlbumJob(albumQuery);
            var searchJob = new SearchJob(albumQuery);

            await searcher.SearchAlbum(albumJob, config.Search, new ResponseData(), CancellationToken.None);
            await searcher.Search(searchJob, config.Search, new ResponseData(), CancellationToken.None);

            var projected = searchJob.GetAlbumFolders(config.Search).Items;

            CollectionAssert.AreEqual(
                albumJob.Results.Select(x => x.Username + "\\" + x.FolderPath).ToList(),
                projected.Select(x => x.Username + "\\" + x.FolderPath).ToList());
            CollectionAssert.AreEqual(
                albumJob.Results.Select(x => x.SearchAudioFileCount).ToList(),
                projected.Select(x => x.SearchAudioFileCount).ToList());
        }

        [TestMethod]
        public async Task SearchAlbum_DoesNotRequireAlbumNameInBasenames()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Artist\Album\01. Track One.mp3", length: 180),
                    TestHelpers.CreateSlFile(@"Artist\Album\02. Track Two.mp3", length: 181),
                ]),
            };

            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, config);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

            await searcher.SearchAlbum(job, config.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(1, job.Results.Count);
            Assert.AreEqual(@"Artist\Album", job.Results[0].FolderPath);
            Assert.AreEqual(2, job.Results[0].SearchAudioFileCount);
        }

        [TestMethod]
        public async Task SearchAlbum_StrictTitle_IsNoOpWithoutSearchHint()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Artist\Album\01. Track One.mp3", length: 180),
                    TestHelpers.CreateSlFile(@"Artist\Album\02. Track Two.mp3", length: 181),
                ]),
            };

            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.NecessaryCond.StrictTitle = true;
            config.Search.PreferredCond.StrictTitle = true;
            var searcher = CreateSearcher(client, config);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

            await searcher.SearchAlbum(job, config.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(1, job.Results.Count, "StrictTitle should not require album names to appear in basenames for normal album searches.");
        }

        [TestMethod]
        public async Task SearchAlbum_StrictTitle_UsesSearchHintWhenPresent()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Artist\Album\01. Blue Sky.mp3", length: 180),
                ]),
                new("User2", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Artist\Album\01. Other Song.mp3", length: 180),
                ]),
            };

            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.NecessaryCond.StrictTitle = true;
            config.Search.PreferredCond.StrictTitle = true;
            var searcher = CreateSearcher(client, config);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album", SearchHint = "Blue Sky" });

            await searcher.SearchAlbum(job, config.Search, new ResponseData(), CancellationToken.None);

            Assert.AreEqual(1, job.Results.Count);
            Assert.AreEqual("User1", job.Results[0].Username);
        }

        [TestMethod]
        public async Task AggregateAlbum_DiscographySearch_IdentifiesAllUniqueAlbums()
        {
            var index = CreateSophisticatedIndex();
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
            var responseData = new ResponseData();

            var (results, _) = await searcher.SearchAggregateAlbum(job, config.Search, responseData, CancellationToken.None);

            // Expected groups:
            // 1. Time Standard (10 tracks, lengths 181..190 - shared by User1, User6)
            // 2. Time Deluxe (13 tracks, User2)
            // 3. Time Partial (5 tracks, User3)
            // 4. Discovery (9 tracks, User4)
            // 5. Time BadVersion (10 tracks, length 60 - User5)
            Assert.AreEqual(5, results.Count, "Should identify 5 distinct album/version groups.");
            
            var multiShare = results.FirstOrDefault(r => r.Results.Count == 2);
            Assert.IsNotNull(multiShare, "Standard Multi-Share version missing.");
            Assert.AreEqual(10, multiShare.Results[0].Files.Count(f => !f.IsNotAudio));
        }

        [TestMethod]
        public async Task AggregateAlbum_HighEntropy_FiltersByShares()
        {
            var index = CreateSophisticatedIndex();
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 2; // Only return if shared by 2+ peers

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
            var responseData = new ResponseData();

            var (results, _) = await searcher.SearchAggregateAlbum(job, config.Search, responseData, CancellationToken.None);

            // Only the "Standard Time" version (shared by User1 and User6) meets the threshold.
            Assert.AreEqual(1, results.Count, "High entropy filter (minShares=2) failed.");
            Assert.AreEqual(2, results[0].Results.Count);
        }

        [TestMethod]
        public void AggregateAlbums_AssignsItemNameFromFolderName()
        {
            var folders = new List<AlbumFolder>
            {
                AlbumFolder("User1", @"Music\Rock\ELO\Time",[180, 181, 182]),
            };

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };

            // Check full projector
            var fullProjected = SearchResultProjector.AggregateAlbums(folders, query, search);
            Assert.AreEqual(1, fullProjected.Count);
            Assert.AreEqual("Time", fullProjected[0].ItemName, "Full projector should derive the item name from the representative folder path.");

            // Check incremental projector
            var incrementalProjector = new IncrementalAlbumAggregateProjector(query, search);
            incrementalProjector.AddRange(folders);
            var incrementalProjected = incrementalProjector.Snapshot();
            Assert.AreEqual(1, incrementalProjected.Count);
            Assert.AreEqual("Time", incrementalProjected[0].ItemName, "Incremental projector should derive the item name from the representative folder path.");
        }

        [TestMethod]
        public void AggregateAlbums_UsesSearchMetadataWithoutMaterializingFiles()
        {
            List<SongJob> ThrowIfMaterialized() => throw new AssertFailedException("AggregateAlbums should not force AlbumFolder.Files when search metadata is available.");

            var folders = new List<AlbumFolder>
            {
                new(
                    "User1",
                    @"ELO\Time",
                    ThrowIfMaterialized,
                    searchAudioFileCount: 1,
                    searchSortedAudioLengths: [209],
                    searchRepresentativeAudioFilename: @"ELO\Time\02. Twilight.mp3"),
                new(
                    "User2",
                    @"Shared\ELO\Time",
                    ThrowIfMaterialized,
                    searchAudioFileCount: 1,
                    searchSortedAudioLengths: [210],
                    searchRepresentativeAudioFilename: @"Shared\ELO\Time\02. Twilight.mp3"),
            };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 2;
            search.AggregateLengthTol = 2;

            var results = SearchResultProjector.AggregateAlbums(
                folders,
                new AlbumQuery { Artist = "ELO", Album = "Time" },
                search);

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(2, results[0].Results.Count);
        }

        [TestMethod]
        public void IncrementalAggregateTracks_MatchesFullAggregateTracks_WhenFedInChunks()
        {
            var rawResults = new List<(SearchResponse Response, Soulseek.File File)>();
            for (int user = 0; user < 6; user++)
            {
                var file = TestHelpers.CreateSlFile($@"Music\User{user}\ELO - Blue Sky.mp3", length: user < 4 ? 180 : 260);
                var response = new SearchResponse($"User{user}", 1, user % 2 == 0, 1000 + user, user, [file]);
                rawResults.Add((response, file));
            }

            var query = new SongQuery { Artist = "ELO", Title = "Blue Sky" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            var counts = new ConcurrentDictionary<string, int>();
            var expected = SearchResultProjector.AggregateTracks(rawResults, query, search, counts);
            var incremental = new IncrementalAggregateTrackProjector(query, search, counts);

            foreach (var chunk in rawResults.Chunk(2))
                incremental.AddRange(chunk);

            var actual = incremental.Snapshot();

            CollectionAssert.AreEqual(
                expected.Select(x => $"{x.Query.Artist}|{x.Query.Title}|{x.Query.Length}|{x.Candidates!.Count}").ToList(),
                actual.Select(x => $"{x.Query.Artist}|{x.Query.Title}|{x.Query.Length}|{x.Candidates!.Count}").ToList());
            CollectionAssert.AreEqual(
                expected.SelectMany(x => x.Candidates!.Select(c => c.Username + "\\" + c.Filename)).ToList(),
                actual.SelectMany(x => x.Candidates!.Select(c => c.Username + "\\" + c.Filename)).ToList());
        }

        [TestMethod]
        public void AggregateTracks_DoesNotSortBucketByStrictArtist()
        {
            var artistMatch = TestHelpers.CreateSlFile(@"Music\Right Artist\Track.mp3", length: 200);
            var titleOnly = TestHelpers.CreateSlFile(@"Music\Other Artist\Track.mp3", length: 200);
            var artistResponse = new SearchResponse("artist-match", 1, true, 100 * 1024, 0, [artistMatch]);
            var titleOnlyResponse = new SearchResponse("title-only", 1, true, 800 * 1024, 0, [titleOnly]);

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            search.PreferredCond.StrictArtist = true;
            var counts = new ConcurrentDictionary<string, int>();
            var query = new SongQuery { Artist = "Right Artist", Title = "Track" };

            var aggregate = SearchResultProjector.AggregateTracks(
                [(artistResponse, artistMatch), (titleOnlyResponse, titleOnly)],
                query,
                search,
                counts);

            Assert.AreEqual(1, aggregate.Count);
            Assert.AreEqual("title-only", aggregate[0].Candidates![0].Username);
        }

        [TestMethod]
        public void IncrementalAlbumAggregate_MatchesFullAggregate_WhenFedInChunks()
        {
            var folders = new List<AlbumFolder>
            {
                AlbumFolder("User1", @"ELO\Time", [180, 181, 182]),
                AlbumFolder("User2", @"Shared\ELO\Time", [180, 181, 182]),
                AlbumFolder("User3", @"ELO\Time Deluxe", [180, 181, 182, 183]),
                AlbumFolder("User4", @"ELO\Discovery", [240, 241, 242]),
            };
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            var expected = SearchResultProjector.AggregateAlbums(folders, query, search);
            var incremental = new IncrementalAlbumAggregateProjector(query, search);

            foreach (var chunk in folders.Chunk(2))
                incremental.AddRange(chunk);

            var actual = incremental.Snapshot();

            CollectionAssert.AreEqual(
                expected.Select(x => x.Results.Count).ToList(),
                actual.Select(x => x.Results.Count).ToList());
            CollectionAssert.AreEqual(
                expected.SelectMany(x => x.Results.Select(r => r.Username + "\\" + r.FolderPath)).ToList(),
                actual.SelectMany(x => x.Results.Select(r => r.Username + "\\" + r.FolderPath)).ToList());
        }

        [TestMethod]
        public void IncrementalAlbumAggregate_SortsEachBucketByAlbumSort()
        {
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            search.PreferredCond.Formats = ["flac"];
            var folderProjector = new IncrementalAlbumFolderProjector(query, search);
            var aggregateProjector = new IncrementalAlbumAggregateProjector(query, search);

            var responses = new[]
            {
                AlbumSearchResponse("OpusTimeUser", false, 1000, @"Music\ELO\Time [OPUS]", ".opus", [209, 200]),
                AlbumSearchResponse("OpusLiveUser", false, 1000, @"Music\ELO\Time Live [OPUS]", ".opus", [300, 301]),
                AlbumSearchResponse("FlacTimeUser", true, 5000, @"Music\ELO\Time [FLAC]", ".flac", [209, 200]),
                AlbumSearchResponse("FlacLiveUser", true, 5000, @"Music\ELO\Time Live [FLAC]", ".flac", [300, 301]),
            };

            foreach (var response in responses)
            {
                var changes = folderProjector.AddRangeAndGetChanges(
                    response.Files.Select(file => (response, file)));
                aggregateProjector.ApplyChanges(changes);
            }

            var albumFolders = folderProjector.Snapshot();
            var aggregateBuckets = aggregateProjector.Snapshot();

            AssertAlbumSortRanksLaterFlacAheadOfEarlierOpus(albumFolders);
            Assert.AreEqual(2, aggregateBuckets.Count);
            foreach (var bucket in aggregateBuckets)
                Assert.AreEqual(2, bucket.Results.Count);
            AssertBucketsSortedByAlbumSort(aggregateBuckets, albumFolders);

            static SearchResponse AlbumSearchResponse(
                string username,
                bool hasFreeUploadSlot,
                int uploadSpeedKibPerSecond,
                string folderPath,
                string extension,
                int[] lengths)
            {
                var files = lengths
                    .Select((length, index) => TestHelpers.CreateSlFile(
                        $@"{folderPath}\{index + 1:D2}. Track {index + 1}{extension}",
                        extension: extension,
                        bitrate: extension == ".flac" ? 900 : 192,
                        length: length,
                        sampleRate: 44100))
                    .ToList();
                return new SearchResponse(username, 1, hasFreeUploadSlot, uploadSpeedKibPerSecond * 1024, 0, files);
            }

            static void AssertAlbumSortRanksLaterFlacAheadOfEarlierOpus(IReadOnlyList<AlbumFolder> albumSortedFolders)
            {
                var albumSortOrder = albumSortedFolders
                    .Select((folder, index) => new { Key = FolderKey(folder), Index = index })
                    .ToDictionary(x => x.Key, x => x.Index, StringComparer.Ordinal);

                Assert.IsTrue(
                    albumSortOrder[@"FlacTimeUser\Music\ELO\Time [FLAC]"] < albumSortOrder[@"OpusTimeUser\Music\ELO\Time [OPUS]"],
                    "Test setup should rank later-arriving FLAC Time ahead of earlier-arriving OPUS Time.");
                Assert.IsTrue(
                    albumSortOrder[@"FlacLiveUser\Music\ELO\Time Live [FLAC]"] < albumSortOrder[@"OpusLiveUser\Music\ELO\Time Live [OPUS]"],
                    "Test setup should rank later-arriving FLAC Live ahead of earlier-arriving OPUS Live.");
            }

            static void AssertBucketsSortedByAlbumSort(
                IEnumerable<AlbumJob> buckets,
                IReadOnlyList<AlbumFolder> albumSortedFolders)
            {
                var albumSortOrder = albumSortedFolders
                    .Select((folder, index) => new { Key = FolderKey(folder), Index = index })
                    .ToDictionary(x => x.Key, x => x.Index, StringComparer.Ordinal);

                foreach (var bucket in buckets)
                {
                    var actual = bucket.Results.Select(FolderKey).ToList();
                    var expected = bucket.Results
                        .OrderBy(folder => albumSortOrder[FolderKey(folder)])
                        .Select(FolderKey)
                        .ToList();

                    CollectionAssert.AreEqual(
                        expected,
                        actual,
                        $"Aggregate bucket '{bucket.ItemName}' should be sorted by the normal album-folder ranking.");
                }
            }

            static string FolderKey(AlbumFolder folder)
                => folder.Username + '\\' + folder.FolderPath;
        }

        [TestMethod]
        public void AlbumAggregate_RealProjection_RanksBucketByFormatButNotStrictAlbum()
        {
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            search.PreferredCond.Formats = ["flac"];
            search.PreferredCond.StrictAlbum = true;
            var job = new SearchJob(query);

            var opusStrictAlbum = AlbumSearchResponse("AOpusTimeUser", false, 1000, @"Music\ELO\Time [OPUS]", ".opus", [209, 200]);
            var flacOtherAlbum = AlbumSearchResponse("ZFlacOtherUser", true, 5000, @"Music\ELO\Other [FLAC]", ".flac", [209, 200]);

            job.Session.AddResponse(opusStrictAlbum);
            job.Session.AddResponse(flacOtherAlbum);

            var buckets = job.GetAggregateAlbums(search).Items;

            Assert.AreEqual(1, buckets.Count);
            Assert.AreEqual("ZFlacOtherUser", buckets[0].Results[0].Username,
                "The real album-aggregate projection should honor non-string preferences within a bucket while ignoring strict string ranking.");

            static SearchResponse AlbumSearchResponse(
                string username,
                bool hasFreeUploadSlot,
                int uploadSpeedKibPerSecond,
                string folderPath,
                string extension,
                int[] lengths)
            {
                var files = lengths
                    .Select((length, index) => TestHelpers.CreateSlFile(
                        $@"{folderPath}\{index + 1:D2}. Track {index + 1}{extension}",
                        extension: extension,
                        bitrate: extension == ".flac" ? 900 : 192,
                        length: length,
                        sampleRate: 44100))
                    .ToList();
                return new SearchResponse(username, 1, hasFreeUploadSlot, uploadSpeedKibPerSecond * 1024, 0, files);
            }
        }

        [TestMethod]
        public void IncrementalAlbumAggregate_DoesNotMergeSingleTrackAlbumsByLengthOnly()
        {
            var folders = new List<AlbumFolder>
            {
                AlbumFolder("User1", @"ELO\Blue Sky", [180], representativeFilename: @"ELO\Blue Sky\01. ELO - Blue Sky.mp3"),
                AlbumFolder("User2", @"ELO\Telephone Line", [180], representativeFilename: @"ELO\Telephone Line\01. ELO - Telephone Line.mp3"),
            };
            var query = new AlbumQuery { Artist = "ELO" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            var incremental = new IncrementalAlbumAggregateProjector(query, search);

            incremental.AddRange(folders);
            var actual = incremental.Snapshot();

            Assert.AreEqual(2, actual.Count);
        }

        [TestMethod]
        public void IncrementalAlbumAggregate_AppliesFolderUpdatesConservatively()
        {
            var track1 = TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209);
            var track2 = TestHelpers.CreateSlFile(@"ELO\Time\02. Yours Truly.flac", length: 200);
            var response = new SearchResponse("User1", 1, true, 1000, 0, [track1, track2]);
            var query = new AlbumQuery { Artist = "ELO", Album = "Time" };
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            var folderProjector = new IncrementalAlbumFolderProjector(query, search);
            var aggregateProjector = new IncrementalAlbumAggregateProjector(query, search);

            aggregateProjector.ApplyChanges(folderProjector.AddRangeAndGetChanges([(response, track1)]));
            var changes = folderProjector.AddRangeAndGetChanges([(response, track2)]);
            aggregateProjector.ApplyChanges(changes);

            var expected = SearchResultProjector.AggregateAlbums(changes.Folders, query, search);
            var actual = aggregateProjector.Snapshot();

            Assert.AreEqual(expected.Count, actual.Count);
            Assert.AreEqual(2, actual[0].Results[0].SearchAudioFileCount);
        }

        private static AlbumFolder AlbumFolder(
            string username,
            string folderPath,
            int[] lengths,
            string? representativeFilename = null)
            => new(
                username,
                folderPath,
                () => throw new AssertFailedException("Test aggregate should not materialize album files."),
                searchAudioFileCount: lengths.Length,
                searchSortedAudioLengths: lengths,
                searchRepresentativeAudioFilename: representativeFilename ?? $@"{folderPath}\01. Track.mp3");

        [TestMethod]
        public async Task SongAggregate_Comprehensive_GroupsEquivalentTracks()
        {
            var index = new List<SearchResponse>();
            
            // Standard version (User1 and User2 share lengths 180-181)
            index.Add(new SearchResponse("User1", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO\Time\Blue Sky.mp3", length: 180)
            }));
            index.Add(new SearchResponse("User2", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 181)
            }));
            
            // Group 2: Live/Odd Version (User3 - diff length)
            index.Add(new SearchResponse("User3", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky (Live).mp3", length: 300)
            }));

            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            var responseData = new ResponseData();

            await searcher.SearchAggregate(job, config.Search, responseData, CancellationToken.None);

            // Verified groups: 
            // 1. "Blue Sky" group (User1, User2)
            // 2. "Live Blue Sky" group (User3)
            var songs = job.Songs!;
            Assert.AreEqual(2, songs.Count);
            var shared = songs.FirstOrDefault(s => s.Candidates!.Count == 2);
            Assert.IsNotNull(shared, "Failed to aggregate shared track version across users.");
        }
        [TestMethod]
        public async Task SongAggregate_NameVariations_GroupedByInference()
        {
            var index = new List<SearchResponse>();
            
            // Three variations of the same track name
            index.Add(new SearchResponse("User1", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 180)
            }));
            index.Add(new SearchResponse("User2", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"01. ELO - Blue Sky.mp3", length: 180) // With track number
            }));
            index.Add(new SearchResponse("User3", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky  .mp3", length: 180) // With extra whitespace
            }));

            var client = CreateMockClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            var responseData = new ResponseData();

            await searcher.SearchAggregate(job, config.Search, responseData, CancellationToken.None);

            // Should all three group into one job after inference handles the name variations.
            var songs = job.Songs!;
            Assert.AreEqual(1, songs.Count, "Should group all name variations into a single SongJob.");
            Assert.AreEqual(3, songs.Single().Candidates!.Count);
        }
        [TestMethod]
        public async Task SearchAggregate_ResultsSortedByPopularity()
        {
            var index = new List<SearchResponse>();
            
            // 3 peers have Version A (Low Bitrate)
            for (int i = 1; i <= 3; i++)
                index.Add(new SearchResponse($"User{i}", 1, true, 100, 0, new List<Soulseek.File> {
                    TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 180, bitrate: 128)
                }));
            // 2 peers have Version B (High Bitrate)
            for (int i = 4; i <= 5; i++)
                index.Add(new SearchResponse($"User{i}", 1, true, 100, 0, new List<Soulseek.File> {
                    TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 300, bitrate: 320)
                }));

            var client = CreateMockClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            var responseData = new ResponseData();

            await searcher.SearchAggregate(job, config.Search, responseData, CancellationToken.None);

            // Version A has more shares (3) so it should be first, even though bitrate is lower.
            var songs = job.Songs!;
            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual(3, songs.ElementAt(0).Candidates!.Count);
            Assert.AreEqual(2, songs.ElementAt(1).Candidates!.Count);
        }

        [TestMethod]
        public async Task AggregateAlbum_ResultsSortedByPopularity()
        {
            var index = new List<SearchResponse>();
            
            // 2 peers have album
            for (int i = 1; i <= 2; i++) {
                var files = new List<Soulseek.File>();
                for (int j = 1; j <= 5; j++) files.Add(TestHelpers.CreateSlFile($@"ELO\Time\Track {j}.mp3", length: 200));
                index.Add(new SearchResponse($"User{i}", 1, true, 100, 0, files));
            }
            // 1 peer has different version
            var files3 = new List<Soulseek.File>();
            for (int j = 1; j <= 5; j++) files3.Add(TestHelpers.CreateSlFile($@"ELO\Time (Alt)\Track {j}.mp3", length: 300));
            index.Add(new SearchResponse("User3", 1, true, 100, 0, files3));

            var client = CreateMockClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
            var responseData = new ResponseData();

            var (results, _) = await searcher.SearchAggregateAlbum(job, config.Search, responseData, CancellationToken.None);

            // First result should be the one with 2 shares.
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, results[0].Results.Count);
            Assert.AreEqual(1, results[1].Results.Count);
        }

        [TestMethod]
        public async Task AggregateAlbum_SingleTrackAlbumsWithDifferentTitles_DoNotMergeByLengthOnly()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Blue Sky\01. ELO - Blue Sky.mp3", length: 180),
                ]),
                new("User2", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"ELO\Telephone Line\01. ELO - Telephone Line.mp3", length: 180),
                ]),
            };

            var client = CreateMockClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
            var responseData = new ResponseData();

            var (results, _) = await searcher.SearchAggregateAlbum(job, config.Search, responseData, CancellationToken.None);

            Assert.AreEqual(2, results.Count, "Single-track album versions should not merge just because lengths match.");
            Assert.IsTrue(results.Any(r => r.Results[0].Files.Any(f => f.Query.Title.Contains("Blue Sky"))));
            Assert.IsTrue(results.Any(r => r.Results[0].Files.Any(f => f.Query.Title.Contains("Telephone Line"))));
        }

        [TestMethod]
        public async Task SearchSong_WhenRateLimitExhausted_RaisesSearchRateLimitedEvent()
        {
            var client = CreateMockClient([]);
            var settings = TestHelpers.CreateDefaultSettings().Download;
            var events = new EngineEvents();
            var registry = TestHelpers.CreateSessionRegistry();

            // 1 search per 10 seconds — second search will block immediately
            var searcher = new Searcher(client, registry, registry, events, searchesPerTime: 1, searchRenewTime: 10);

            var song1 = new SongJob(new SongQuery { Artist = "A", Title = "B" });
            await searcher.SearchSong(song1, settings.Search, new ResponseData(), CancellationToken.None);

            bool fired = false;
            events.SearchRateLimited += _ => fired = true;

            var song2 = new SongJob(new SongQuery { Artist = "C", Title = "D" });
            using var cts = new CancellationTokenSource();
            var task2 = searcher.SearchSong(song2, settings.Search, new ResponseData(), cts.Token);

            // onWaiting fires synchronously before WaitAsync yields, so fired is already true
            Assert.IsTrue(fired, "SearchRateLimited should fire when the rate semaphore is exhausted.");
            Assert.AreEqual(JobActivityPhase.SearchRateLimited, song2.ActivityPhase);
            Assert.IsTrue(song2.ActivityUntilUtc.HasValue);

            cts.Cancel();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await task2);
        }

        private static void AssertPhaseOrder(IReadOnlyList<JobActivityPhase> phases, params JobActivityPhase[] expected)
        {
            var nextSearchIndex = 0;
            foreach (var phase in phases)
            {
                if (nextSearchIndex < expected.Length && phase == expected[nextSearchIndex])
                    nextSearchIndex++;
            }

            Assert.AreEqual(
                expected.Length,
                nextSearchIndex,
                $"Expected phases in order: {string.Join(", ", expected)}. Actual: {string.Join(", ", phases)}");
        }
    }
}
