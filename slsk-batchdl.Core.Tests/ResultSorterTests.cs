using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Soulseek;
using System.Collections.Concurrent;
using File = Soulseek.File;

namespace Tests.ResultSorterTests
{
    [TestClass]
    public class SortingCriteriaTests
    {
        [TestMethod]
        public void CompareTo_Null_ReturnsPositive()
        {
            var a = new SortingCriteria();
            Assert.IsTrue(a.CompareTo(null) > 0);
        }

        [TestMethod]
        public void CompareTo_NecessaryConditions_TrumpLowerCriteria()
        {
            var better = new SortingCriteria
            {
                UserSuccessAboveDownrank = false,
                NecessaryConditionsMet = true,
            };
            var worse = new SortingCriteria
            {
                UserSuccessAboveDownrank = false,
                NecessaryConditionsMet = false,
                // Even with all lower criteria being better
                HasFreeUploadSlot = true,
                UploadSpeedFast = 100,
                BitrateMatch = true,
                FormatMatch = true,
            };

            Assert.IsTrue(better.CompareTo(worse) > 0);
        }

        [TestMethod]
        public void CompareTo_UserSuccessAboveDownrank_IsHighestPriority()
        {
            var better = new SortingCriteria { UserSuccessAboveDownrank = true };
            var worse = new SortingCriteria
            {
                UserSuccessAboveDownrank = false,
                NecessaryConditionsMet = true,
                HasFreeUploadSlot = true,
            };

            Assert.IsTrue(better.CompareTo(worse) > 0);
        }

        [TestMethod]
        public void CompareTo_FreeSlot_BeatsNoFreeSlot()
        {
            var withSlot = new SortingCriteria { HasFreeUploadSlot = true };
            var noSlot = new SortingCriteria { HasFreeUploadSlot = false };

            Assert.IsTrue(withSlot.CompareTo(noSlot) > 0);
        }

        [TestMethod]
        public void CompareTo_HighUploadSpeed_BeatsLow()
        {
            var fast = new SortingCriteria { UploadSpeedFast = 10 };
            var slow = new SortingCriteria { UploadSpeedFast = 1 };

            Assert.IsTrue(fast.CompareTo(slow) > 0);
        }

        [TestMethod]
        public void CompareTo_AllEqual_RandomTiebreakerDecides()
        {
            var a = new SortingCriteria { RandomTiebreaker = 100 };
            var b = new SortingCriteria { RandomTiebreaker = 50 };

            Assert.IsTrue(a.CompareTo(b) > 0);
            Assert.IsTrue(b.CompareTo(a) < 0);
        }

        [TestMethod]
        public void CompareTo_DoesNotComputeLevenshtein_WhenHigherPriorityFieldDiffers()
        {
            var calls = 0;
            var better = new SortingCriteria
            {
                NecessaryConditionsMet = true,
                LevenshteinScoreFactory = () => { calls++; return 0; },
            };
            var worse = new SortingCriteria
            {
                NecessaryConditionsMet = false,
                LevenshteinScoreFactory = () => { calls++; return 100; },
            };

            Assert.IsTrue(better.CompareTo(worse) > 0);
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void CompareTo_ComputesLevenshteinOnce_WhenNeeded()
        {
            var calls = 0;
            var better = new SortingCriteria
            {
                LevenshteinScoreFactory = () => { calls++; return 2; },
            };
            var worse = new SortingCriteria
            {
                LevenshteinScoreFactory = () => { calls++; return 1; },
            };

            Assert.IsTrue(better.CompareTo(worse) > 0);
            Assert.IsTrue(better.CompareTo(worse) > 0);
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void CompareTo_DoesNotComputeInferredTrackCount_WhenHigherPriorityFieldDiffers()
        {
            var calls = 0;
            var better = new SortingCriteria
            {
                NecessaryConditionsMet = true,
                InferredTrackCountFactory = () => { calls++; return 1; },
            };
            var worse = new SortingCriteria
            {
                NecessaryConditionsMet = false,
                InferredTrackCountFactory = () => { calls++; return 2; },
            };

            Assert.IsTrue(better.CompareTo(worse) > 0);
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void CompareTo_ComputesInferredTrackCountOnce_WhenNeeded()
        {
            var calls = 0;
            var better = new SortingCriteria
            {
                InferredTrackCountFactory = () => { calls++; return 2; },
            };
            var worse = new SortingCriteria
            {
                InferredTrackCountFactory = () => { calls++; return 1; },
            };

            Assert.IsTrue(better.CompareTo(worse) > 0);
            Assert.IsTrue(better.CompareTo(worse) > 0);
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void CompareTo_FullPriorityChain_EachLevelWins()
        {
            // Test that each successive criterion can win when all above are equal
            var fields = new (string name, Action<SortingCriteria> setBetter)[]
            {
                ("UserSuccessAboveDownrank", c => c.UserSuccessAboveDownrank = true),
                ("NecessaryConditionsMet", c => c.NecessaryConditionsMet = true),
                ("PreferredUserConditionsMet", c => c.PreferredUserConditionsMet = true),
                ("HasValidLength", c => c.HasValidLength = true),
                ("BracketCheckPassed", c => c.BracketCheckPassed = true),
                ("StrictTitleMatch", c => c.StrictTitleMatch = true),
                ("AlbumModeStrictAlbumMatch", c => c.AlbumModeStrictAlbumMatch = true),
                ("StrictArtistMatch", c => c.StrictArtistMatch = true),
                ("LengthToleranceMatch", c => c.LengthToleranceMatch = true),
                ("FormatMatch", c => c.FormatMatch = true),
                ("NonAlbumModeStrictAlbumMatch", c => c.NonAlbumModeStrictAlbumMatch = true),
                ("BitrateMatch", c => c.BitrateMatch = true),
                ("SampleRateMatch", c => c.SampleRateMatch = true),
                ("BitDepthMatch", c => c.BitDepthMatch = true),
                ("FileSatisfies", c => c.FileSatisfies = true),
                ("HasFreeUploadSlot", c => c.HasFreeUploadSlot = true),
                ("UploadSpeedFast", c => c.UploadSpeedFast = 1),
            };

            for (int i = 0; i < fields.Length; i++)
            {
                var better = new SortingCriteria();
                var worse = new SortingCriteria();

                // Set all higher-priority fields equal (true/positive)
                for (int j = 0; j < i; j++)
                {
                    fields[j].setBetter(better);
                    fields[j].setBetter(worse);
                }

                // Set the current field differently
                fields[i].setBetter(better);

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
                useInfer: false,
                useLevenshtein: false).ToList();

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
                useInfer: true,
                useLevenshtein: false).ToList();

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
