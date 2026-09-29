using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Soulseek;
using System.Collections.Concurrent;
using File = Soulseek.File;

namespace Tests.ResultSorterTests
{
    [TestClass]
    public class SortKeyTests
    {
        private static ResultSorter.SortKey Key(
            bool userSuccessAboveDownrank = false,
            bool necessaryConditionsMet = false,
            bool preferredUserConditionsMet = false,
            bool hasValidLength = false,
            bool bracketCheckPassed = false,
            bool strictTitleMatch = false,
            bool fuzzyTitleMatch = false,
            bool strictAlbumMatch = false,
            bool fuzzyAlbumMatch = false,
            bool strictArtistMatch = false,
            bool fuzzyArtistMatch = false,
            bool lengthToleranceMatch = false,
            bool formatMatch = false,
            bool bitrateMatch = false,
            bool sampleRateMatch = false,
            bool bitDepthMatch = false,
            bool fileSatisfies = false,
            bool hasFreeUploadSlot = false,
            int uploadSpeedFast = 0,
            bool nonAlbumModeStrictString = false,
            bool albumModeStrictString = false,
            bool strictArtistString = false,
            int inferredTrackCount = 0,
            int uploadSpeedMedium = 0,
            int bitRate = 0,
            int randomTiebreaker = 0)
            => new(
                userSuccessAboveDownrank,
                necessaryConditionsMet,
                preferredUserConditionsMet,
                hasValidLength,
                bracketCheckPassed,
                strictTitleMatch,
                fuzzyTitleMatch,
                strictAlbumMatch,
                fuzzyAlbumMatch,
                strictArtistMatch,
                fuzzyArtistMatch,
                lengthToleranceMatch,
                formatMatch,
                bitrateMatch,
                sampleRateMatch,
                bitDepthMatch,
                fileSatisfies,
                hasFreeUploadSlot,
                uploadSpeedFast,
                nonAlbumModeStrictString,
                albumModeStrictString,
                strictArtistString,
                inferredTrackCount,
                uploadSpeedMedium,
                bitRate,
                randomTiebreaker);

        [TestMethod]
        public void CompareTo_NecessaryConditions_TrumpLowerCriteria()
        {
            var better = Key(necessaryConditionsMet: true);
            var worse = Key(
                hasFreeUploadSlot: true,
                uploadSpeedFast: 100,
                bitrateMatch: true,
                formatMatch: true);

            Assert.IsTrue(better.CompareTo(worse) > 0);
        }

        [TestMethod]
        public void CompareTo_UserSuccessAboveDownrank_IsHighestPriority()
        {
            var better = Key(userSuccessAboveDownrank: true);
            var worse = Key(necessaryConditionsMet: true, hasFreeUploadSlot: true);

            Assert.IsTrue(better.CompareTo(worse) > 0);
        }

        [TestMethod]
        public void CompareTo_FreeSlot_BeatsNoFreeSlot()
        {
            var withSlot = Key(hasFreeUploadSlot: true);
            var noSlot = Key(hasFreeUploadSlot: false);

            Assert.IsTrue(withSlot.CompareTo(noSlot) > 0);
        }

        [TestMethod]
        public void CompareTo_HighUploadSpeed_BeatsLow()
        {
            var fast = Key(uploadSpeedFast: 10);
            var slow = Key(uploadSpeedFast: 1);

            Assert.IsTrue(fast.CompareTo(slow) > 0);
        }

        [TestMethod]
        public void CompareTo_AllEqual_RandomTiebreakerDecides()
        {
            var a = Key(randomTiebreaker: 100);
            var b = Key(randomTiebreaker: 50);

            Assert.IsTrue(a.CompareTo(b) > 0);
            Assert.IsTrue(b.CompareTo(a) < 0);
        }

        [TestMethod]
        public void CompareTo_InferredTrackCount_IsLateTiebreaker()
        {
            var better = Key(inferredTrackCount: 2);
            var worse = Key(inferredTrackCount: 1);

            Assert.IsTrue(better.CompareTo(worse) > 0);
        }

        [TestMethod]
        public void CompareTo_DoesNotUseInferredTrackCount_WhenHigherPriorityFieldDiffers()
        {
            var better = Key(necessaryConditionsMet: true, inferredTrackCount: 1);
            var worse = Key(necessaryConditionsMet: false, inferredTrackCount: 100);

            Assert.IsTrue(better.CompareTo(worse) > 0);
        }

        [TestMethod]
        public void CompareTo_FullPriorityChain_EachLevelWins()
        {
            // Test that each successive criterion can win when all above are equal
            var fields = new (string name, Func<ResultSorter.SortKey> better)[]
            {
                ("UserSuccessAboveDownrank", () => Key(userSuccessAboveDownrank: true)),
                ("NecessaryConditionsMet", () => Key(necessaryConditionsMet: true)),
                ("PreferredUserConditionsMet", () => Key(preferredUserConditionsMet: true)),
                ("HasValidLength", () => Key(hasValidLength: true)),
                ("BracketCheckPassed", () => Key(bracketCheckPassed: true)),
                ("StrictTitleMatch", () => Key(strictTitleMatch: true)),
                ("FuzzyTitleMatch", () => Key(fuzzyTitleMatch: true)),
                ("StrictAlbumMatch", () => Key(strictAlbumMatch: true)),
                ("FuzzyAlbumMatch", () => Key(fuzzyAlbumMatch: true)),
                ("StrictArtistMatch", () => Key(strictArtistMatch: true)),
                ("FuzzyArtistMatch", () => Key(fuzzyArtistMatch: true)),
                ("LengthToleranceMatch", () => Key(lengthToleranceMatch: true)),
                ("FormatMatch", () => Key(formatMatch: true)),
                ("BitrateMatch", () => Key(bitrateMatch: true)),
                ("SampleRateMatch", () => Key(sampleRateMatch: true)),
                ("BitDepthMatch", () => Key(bitDepthMatch: true)),
                ("FileSatisfies", () => Key(fileSatisfies: true)),
                ("HasFreeUploadSlot", () => Key(hasFreeUploadSlot: true)),
                ("UploadSpeedFast", () => Key(uploadSpeedFast: 1)),
                ("NonAlbumModeStrictString", () => Key(nonAlbumModeStrictString: true)),
                ("AlbumModeStrictString", () => Key(albumModeStrictString: true)),
                ("StrictArtistString", () => Key(strictArtistString: true)),
                ("InferredTrackCount", () => Key(inferredTrackCount: 1)),
                ("UploadSpeedMedium", () => Key(uploadSpeedMedium: 1)),
                ("BitRate", () => Key(bitRate: 1)),
                ("RandomTiebreaker", () => Key(randomTiebreaker: 1)),
            };

            for (int i = 0; i < fields.Length; i++)
            {
                var better = fields[i].better();
                var worse = Key();

                Assert.IsTrue(better.CompareTo(worse) > 0,
                    $"Field '{fields[i].name}' at priority {i} should make 'better' sort higher");
            }
        }
    }

    [TestClass]
    public class OrderedResultsTests
    {
        private static SearchResponse CreateResponse(string username, bool freeSlot = true, int uploadSpeed = 1000, int queueLen = 0, params File[] files)
        {
            return new SearchResponse(username, 1, freeSlot, uploadSpeed, queueLen, files.ToList());
        }

        [TestMethod]
        public void OrderedResults_EmptyResults_ReturnsEmpty()
        {
            var results = new List<(SearchResponse, File)>();
            var config = TestHelpers.CreateDefaultSettings().Download;
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "A", title: "T");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(0, ordered.Count);
        }

        [TestMethod]
        public void OrderedResults_SingleResult_ReturnsThatResult()
        {
            var file = TestHelpers.CreateSlFile("Music\\Artist\\Track.mp3", bitrate: 320, length: 200);
            var response = CreateResponse("user1", files: file);
            var results = new List<(SearchResponse, File)> { (response, file) };
            var config = TestHelpers.CreateDefaultSettings().Download;
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(1, ordered.Count);
            Assert.AreEqual("user1", ordered[0].response.Username);
        }

        [TestMethod]
        public void OrderedResults_PrefersUserWithSuccessHistory()
        {
            var file1 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var file2 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var response1 = CreateResponse("loser", files: file1);
            var response2 = CreateResponse("winner", files: file2);
            var results = new List<(SearchResponse, File)> { (response1, file1), (response2, file2) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.DownrankOn = 0;
            var counts = new ConcurrentDictionary<string, int>();
            counts["winner"] = 5;  // Above downrankOn
            // "loser" has 0, which is not > 0

            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");
            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("winner", ordered[0].response.Username);
        }

        [TestMethod]
        public void OrderedResults_FiltersOutByIgnoreOn()
        {
            var file1 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var response1 = CreateResponse("baduser", files: file1);
            var results = new List<(SearchResponse, File)> { (response1, file1) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.IgnoreOn = 0;  // Need > 0 to be included
            var counts = new ConcurrentDictionary<string, int>();
            // "baduser" has 0 which is not > 0

            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");
            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(0, ordered.Count);
        }

        [TestMethod]
        public void OrderedResults_PrefersFreeUploadSlot()
        {
            var file1 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var file2 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var noSlot = CreateResponse("noslot", freeSlot: false, files: file1);
            var freeSlot = CreateResponse("freeslot", freeSlot: true, files: file2);
            var results = new List<(SearchResponse, File)> { (noSlot, file1), (freeSlot, file2) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("freeslot", ordered[0].response.Username);
        }

        [TestMethod]
        public void CheapBracketCheck_DownranksBracketedFilename_WhenQueryHasNoBrackets()
        {
            var clean = TestHelpers.CreateSlFile("Music\\Artist\\Track.mp3", bitrate: 320, length: 200);
            var remix = TestHelpers.CreateSlFile("Music\\Artist\\Track (Remix).mp3", bitrate: 320, length: 200);
            var cleanResponse = CreateResponse("clean", files: clean);
            var remixResponse = CreateResponse("remix", files: remix);
            var results = new List<(SearchResponse, File)> { (remixResponse, remix), (cleanResponse, clean) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(
                results,
                track,
                config.Search,
                counts,
                useInfer: false).ToList();

            Assert.AreEqual("clean", ordered[0].response.Username);
        }

        [TestMethod]
        public void CheapBracketCheck_AllowsBracketedFilename_WhenQueryHasBrackets()
        {
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track (Remix)");

            Assert.IsTrue(ResultSorter.CheapBracketCheck(track, "Music\\Artist\\Track (Remix).mp3"));
        }

        [TestMethod]
        public void CheapBracketCheck_IgnoresLeadingBracketedTrackNumber()
        {
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            Assert.IsTrue(ResultSorter.CheapBracketCheck(track, "Music\\Artist\\(01) Track.mp3"));
            Assert.IsTrue(ResultSorter.CheapBracketCheck(track, "Music\\Artist\\[1-07] Track.mp3"));
        }

        [TestMethod]
        public void CheapBracketCheck_IgnoresFeaturedArtistBrackets()
        {
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            Assert.IsTrue(ResultSorter.CheapBracketCheck(track, "Music\\Artist\\Track (feat. Guest).mp3"));
            Assert.IsTrue(ResultSorter.CheapBracketCheck(track, "Music\\Artist\\Track [ft. Guest].mp3"));
        }

        [TestMethod]
        public void OrderedResults_DoesNotUseInferenceForTrackRanking()
        {
            var results = new List<(SearchResponse, File)>();
            const int cheapWinnerCount = 250;

            for (int i = 0; i < cheapWinnerCount; i++)
            {
                var file = TestHelpers.CreateSlFile($"Music\\Artist\\Album {i}\\Artist - Track.mp3", bitrate: 320, length: i + 1);
                var response = CreateResponse($"cheap-top-{i}", uploadSpeed: 500 * 1024, files: file);
                results.Add((response, file));
            }

            for (int i = 0; i < 5; i++)
            {
                var file = TestHelpers.CreateSlFile($"Music\\Artist\\Outside Album\\Artist - Track.mp3", bitrate: 320, length: 999);
                var response = CreateResponse($"outside-{i}", uploadSpeed: 100 * 1024, files: file);
                results.Add((response, file));
            }

            var config = TestHelpers.CreateDefaultSettings().Download;
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(
                results,
                track,
                config.Search,
                counts,
                useInfer: true).ToList();

            CollectionAssert.DoesNotContain(
                ordered.Take(cheapWinnerCount).Select(x => x.response.Username).ToList(),
                "outside-0");
        }

        [TestMethod]
        public void OrderedResults_PrefersMatchingFormat()
        {
            var flacFile = TestHelpers.CreateSlFile("Music\\Track.flac", bitrate: 900, length: 200);
            var mp3File = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var response1 = CreateResponse("flacuser", files: flacFile);
            var response2 = CreateResponse("mp3user", files: mp3File);
            var results = new List<(SearchResponse, File)> { (response1, flacFile), (response2, mp3File) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.PreferredCond = new FileConditions { Formats = new[] { "flac" } };
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("flacuser", ordered[0].response.Username);
        }

        [TestMethod]
        public void OrderedResults_PrefersMatchingFormatOverMuchFasterUpload()
        {
            var flacFile = TestHelpers.CreateSlFile("Music\\Track.flac", bitrate: 900, length: 200);
            var mp3File = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var flacResponse = CreateResponse("flacuser", uploadSpeed: 100 * 1024, files: flacFile);
            var mp3Response = CreateResponse("mp3user", uploadSpeed: 5_000 * 1024, files: mp3File);
            var results = new List<(SearchResponse, File)> { (mp3Response, mp3File), (flacResponse, flacFile) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.PreferredCond.Formats = ["flac"];
            config.Search.PreferredCond.MinBitrate = 0;
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("flacuser", ordered[0].response.Username);
        }

        [TestMethod]
        public void OrderedResults_PrefersStrictArtistMatch_WhenPreferred()
        {
            var matchingArtist = TestHelpers.CreateSlFile("Music\\Right Artist\\Track.mp3", bitrate: 320, length: 200);
            var titleOnly = TestHelpers.CreateSlFile("Music\\Wrong Artist\\Track.mp3", bitrate: 320, length: 200);
            var matchingResponse = CreateResponse("artist-match", uploadSpeed: 100 * 1024, files: matchingArtist);
            var titleOnlyResponse = CreateResponse("title-only", uploadSpeed: 800 * 1024, files: titleOnly);
            var results = new List<(SearchResponse, File)> { (titleOnlyResponse, titleOnly), (matchingResponse, matchingArtist) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.PreferredCond = new FileConditions { StrictArtist = true, StrictAlbum = true };
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Right Artist", title: "Track", album: "Missing Album");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("artist-match", ordered[0].response.Username);
        }

        [TestMethod]
        public void OrderedResults_PrefersStrictArtistMatch_WithSlashInArtist()
        {
            var matchingArtist = TestHelpers.CreateSlFile(@"music\AC_DC\1979 - Highway to Hell\01 - Highway to Hell.flac", bitrate: 900, length: 208);
            var fuzzyArtist = TestHelpers.CreateSlFile(@"music\AC-DC\1979 - Highway to Hell\01 - Highway to Hell.flac", bitrate: 900, length: 208);
            var matchingResponse = CreateResponse("artist-match", uploadSpeed: 100 * 1024, files: matchingArtist);
            var fuzzyResponse = CreateResponse("fuzzy-artist", uploadSpeed: 600 * 1024, files: fuzzyArtist);
            var results = new List<(SearchResponse, File)> { (fuzzyResponse, fuzzyArtist), (matchingResponse, matchingArtist) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "AC/DC", title: "Highway to Hell");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("artist-match", ordered[0].response.Username);
        }

        // Matching the requested album is more important than matching a preferred quality.
        // Otherwise --pref-format flac can promote unrelated high-quality files over the right album.
        [TestMethod]
        public void OrderedResults_PrefersFuzzyStrictAlbumMatchOverFormat_WhenPreferred()
        {
            var matchingAlbum = TestHelpers.CreateSlFile(@"Music\Artist\AD：PIANO X\Track.mp3", bitrate: 320, length: 200);
            var preferredFormat = TestHelpers.CreateSlFile(@"Music\Artist\Other Album\Track.flac", bitrate: 900, length: 200);
            var matchingResponse = CreateResponse("album-match", uploadSpeed: 100 * 1024, files: matchingAlbum);
            var formatResponse = CreateResponse("format-match", uploadSpeed: 5_000 * 1024, files: preferredFormat);
            var results = new List<(SearchResponse, File)> { (formatResponse, preferredFormat), (matchingResponse, matchingAlbum) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.PreferredCond = new FileConditions { Formats = ["flac"], StrictAlbum = true };
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track", album: "AD:PIANO X");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("album-match", ordered[0].response.Username);
        }

        [TestMethod]
        public void OrderedResults_DownranksUsersNotInNecessaryAllowedUsers()
        {
            var file1 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var file2 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var blocked = CreateResponse("blocked", files: file1);
            var allowed = CreateResponse("allowed", files: file2);
            var results = new List<(SearchResponse, File)> { (blocked, file1), (allowed, file2) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.NecessaryCond.AllowedUsers = ["allowed"];
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("allowed", ordered[0].response.Username);
        }

        [TestMethod]
        public void OrderedResults_PrefersUsersInPreferredAllowedUsers()
        {
            var file1 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var file2 = TestHelpers.CreateSlFile("Music\\Track.mp3", bitrate: 320, length: 200);
            var fallback = CreateResponse("fallback", files: file1);
            var preferred = CreateResponse("preferred", files: file2);
            var results = new List<(SearchResponse, File)> { (fallback, file1), (preferred, file2) };

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.PreferredCond.AllowedUsers = ["preferred"];
            var counts = new ConcurrentDictionary<string, int>();
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var ordered = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual("preferred", ordered[0].response.Username);
        }

        [TestMethod]
        public void IncrementalResultSorter_MatchesOrderedResults_WhenFedInChunks()
        {
            var results = new List<(SearchResponse, File)>();
            for (int i = 0; i < 50; i++)
            {
                var file = TestHelpers.CreateSlFile(
                    $"Music\\Artist\\Album {i % 5}\\Artist - Track {(i % 7 == 0 ? "(Remix)" : "")}.{(i % 3 == 0 ? "flac" : "mp3")}",
                    bitrate: i % 3 == 0 ? 900 : 320,
                    length: 180 + i);
                var response = CreateResponse(
                    $"user-{i}",
                    freeSlot: i % 4 != 0,
                    uploadSpeed: (100 + i) * 1024,
                    files: file);
                results.Add((response, file));
            }

            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.PreferredCond = new FileConditions { Formats = ["flac"], StrictTitle = true };
            var counts = new ConcurrentDictionary<string, int>();
            counts["user-3"] = 4;
            config.Search.IgnoreOn = -1;
            var track = TestHelpers.CreateQuery(artist: "Artist", title: "Track");

            var expected = ResultSorter.OrderedResults(results, track, config.Search, counts).ToList();
            var incremental = new IncrementalResultSorter(track, config.Search, counts);

            foreach (var chunk in results.Chunk(7))
                incremental.AddRange(chunk);

            var actual = incremental.Snapshot();

            CollectionAssert.AreEqual(
                expected.Select(x => x.response.Username + "\\" + x.file.Filename).ToList(),
                actual.Select(x => x.Response.Username + "\\" + x.File.Filename).ToList());
        }
    }
}
