using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Extractors;
using Sldl.Core.Settings;

namespace Tests.ExtractorTests2
{
    [TestClass]
    public class ListExtractorTests
    {
        private string _tempList = "";

        [TestInitialize]
        public void Setup()
        {
            _tempList = Path.GetTempFileName() + ".txt";
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(_tempList)) File.Delete(_tempList);
        }

        [TestMethod]
        public async Task GetTracks_AlbumLineWithAlbumTrackCountCondition_SetsExtractorFolderCond()
        {
            File.WriteAllText(_tempList, "a:\"some album\"    album-track-count=10");

            var extractor = new ListExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;

            var result = await extractor.GetTracks(_tempList, config.Extraction);
            var jobList = (JobList)result;
            var ej = (ExtractJob)jobList.Jobs[0];

            Assert.IsNotNull(ej.ExtractorFolderCond, "album-track-count in list.txt conditions is silently dropped (ExtractorFolderCond is null)");
            Assert.AreEqual(10, ej.ExtractorFolderCond.MinTrackCount);
            Assert.AreEqual(10, ej.ExtractorFolderCond.MaxTrackCount);
        }

        [TestMethod]
        public async Task GetTracks_AlbumLineWithAlbumTrackCountGe_SetsMinOnly()
        {
            File.WriteAllText(_tempList, "a:\"some album\"    album-track-count>=8");

            var extractor = new ListExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;

            var result = await extractor.GetTracks(_tempList, config.Extraction);
            var jobList = (JobList)result;
            var ej = (ExtractJob)jobList.Jobs[0];

            Assert.IsNotNull(ej.ExtractorFolderCond);
            Assert.AreEqual(8,  ej.ExtractorFolderCond.MinTrackCount);
            Assert.IsNull(ej.ExtractorFolderCond.MaxTrackCount);
        }

        [TestMethod]
        public async Task GetTracks_AlbumLineWithStrictAlbumAndTrackCount_TrackCountInFolderCondStrictAlbumInFileCond()
        {
            // strict-album stays in FileConditions; album-track-count goes to FolderConditions
            File.WriteAllText(_tempList, "a:\"some album\"    strict-album=true;album-track-count=10");

            var extractor = new ListExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;

            var result = await extractor.GetTracks(_tempList, config.Extraction);
            var jobList = (JobList)result;
            var ej = (ExtractJob)jobList.Jobs[0];

            Assert.IsNotNull(ej.ExtractorFolderCond);
            Assert.AreEqual(10,  ej.ExtractorFolderCond.MinTrackCount);
            Assert.AreEqual(10,  ej.ExtractorFolderCond.MaxTrackCount);
            Assert.IsTrue(ej.ExtractorCond?.StrictAlbum == true);
        }
    }


    [TestClass]
    public class SoulseekExtractorTests
    {
        [TestMethod]
        public void InputMatches_SlskUrl_ReturnsTrue()
        {
            Assert.IsTrue(SoulseekExtractor.InputMatches("slsk://user/path/to/file.mp3"));
        }

        [TestMethod]
        public void InputMatches_SlskUrlCaseInsensitive_ReturnsTrue()
        {
            Assert.IsTrue(SoulseekExtractor.InputMatches("SLSK://user/path/file.mp3"));
        }

        [TestMethod]
        public void InputMatches_HttpUrl_ReturnsFalse()
        {
            Assert.IsFalse(SoulseekExtractor.InputMatches("https://example.com/file.mp3"));
        }

        [TestMethod]
        public void InputMatches_PlainString_ReturnsFalse()
        {
            Assert.IsFalse(SoulseekExtractor.InputMatches("artist - title"));
        }

        [TestMethod]
        public async Task GetTracks_FileLink_CreatesDirectDownload()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;
            var result = await extractor.GetTracks("slsk://someuser/Music/Artist/Song.mp3", config.Extraction);

            var slj = (SongJob)result;
            Assert.IsNotNull(slj.ResolvedTarget);
        }

        [TestMethod]
        public async Task GetTracks_FolderLink_CreatesAlbumType()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;
            var result = await extractor.GetTracks("slsk://someuser/Music/Artist/Album/", config.Extraction);

            Assert.IsInstanceOfType(result, typeof(AlbumJob));
            var album = (AlbumJob)result;
            Assert.IsNotNull(album.ResolvedTarget);
            Assert.IsTrue(album.ResolvedTargetNeedsInitialFolderRetrieval);
            Assert.IsFalse(album.AllowBrowseResolvedTarget);
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumConfig_CreatesAlbumType()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Extraction.IsAlbum = true;
            var result = await extractor.GetTracks("slsk://someuser/Music/Song.mp3", config.Extraction);

            Assert.IsInstanceOfType(result, typeof(AlbumJob));
        }

        [TestMethod]
        public async Task GetTracks_FileLink_SetsUsernameAndPath()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;
            var result = await extractor.GetTracks("slsk://myuser/Music/folder/track.mp3", config.Extraction);

            var song = (SongJob)result;
            Assert.IsNotNull(song.Candidates);
            Assert.IsTrue(song.Candidates.Count > 0);
            Assert.AreEqual("myuser", song.Candidates[0].Response.Username);
        }
    }

    [TestClass]
    public class CsvExtractorTests
    {
        private string _tempCsv = "";

        [TestInitialize]
        public void Setup()
        {
            _tempCsv = Path.GetTempFileName() + ".csv";
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(_tempCsv)) File.Delete(_tempCsv);
        }

        [TestMethod]
        public void InputMatches_CsvFile_ReturnsTrue()
        {
            Assert.IsTrue(CsvExtractor.InputMatches("playlist.csv"));
        }

        [TestMethod]
        public void InputMatches_CsvFileWithPath_ReturnsTrue()
        {
            Assert.IsTrue(CsvExtractor.InputMatches("/home/user/music/list.csv"));
        }

        [TestMethod]
        public void InputMatches_NonCsv_ReturnsFalse()
        {
            Assert.IsFalse(CsvExtractor.InputMatches("playlist.m3u"));
        }

        [TestMethod]
        public void InputMatches_HttpCsvUrl_ReturnsFalse()
        {
            Assert.IsFalse(CsvExtractor.InputMatches("https://example.com/list.csv"));
        }

        [TestMethod]
        public async Task GetTracks_WithArtistTitleColumns_ParsesCorrectly()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("Artist1", songs[0].Query.Artist);
            Assert.AreEqual("Song1",   songs[0].Query.Title);
            Assert.AreEqual("Artist2", songs[1].Query.Artist);
            Assert.AreEqual("Song2",   songs[1].Query.Title);
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumColumn_ParsesAlbum()
        {
            File.WriteAllText(_tempCsv, "artist,title,album\nBand,Track,TheAlbum\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual("TheAlbum", songs[0].Query.Album);
        }

        [TestMethod]
        public async Task GetTracks_NoTitleColumn_CreatesAlbumType()
        {
            File.WriteAllText(_tempCsv, "artist,album\nBand,TheAlbum\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);

            Assert.IsTrue(result is AlbumJob || result is AlbumAggregateJob);
        }

        [TestMethod]
        public async Task GetTracks_WithOffset_SkipsTracks()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\nArtist3,Song3\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);
            config.Extraction.Offset = 1;

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("Artist2", songs[0].Query.Artist);
        }

        [TestMethod]
        public async Task GetTracks_WithReverse_ReversesOrder()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);
            config.Extraction.Reverse = true;

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual("Artist2", songs[0].Query.Artist);
            Assert.AreEqual("Artist1", songs[1].Query.Artist);
        }

        [TestMethod]
        public async Task GetTracks_WithMaxTracks_LimitsResults()
        {
            File.WriteAllText(_tempCsv, "artist,title\nA,T1\nB,T2\nC,T3\nD,T4\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);
            config.Extraction.MaxTracks = 2;

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(2, songs.Count);
        }

        [TestMethod]
        public async Task GetTracks_LengthInSeconds_ParsesCorrectly()
        {
            File.WriteAllText(_tempCsv, "artist,title,length\nArtist,Track,200\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);
            config.Csv.TimeUnit = "s";

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(200, songs[0].Query.Length);
        }
    }

    [TestClass]
    public class ExtractorRegistryTests
    {
        private static readonly DownloadSettings _dl = TestHelpers.CreateDefaultSettings().Download;

        [TestMethod]
        public void GetMatchingExtractor_CsvFile_ReturnsCsvExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("playlist.csv", InputType.None, _dl);
            Assert.AreEqual(InputType.CSV, type);
            Assert.IsInstanceOfType(extractor, typeof(CsvExtractor));
        }

        [TestMethod]
        public void GetMatchingExtractor_SlskUrl_ReturnsSoulseekExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("slsk://user/file.mp3", InputType.None, _dl);
            Assert.AreEqual(InputType.Soulseek, type);
            Assert.IsInstanceOfType(extractor, typeof(SoulseekExtractor));
        }

        [TestMethod]
        public void GetMatchingExtractor_PlainString_ReturnsStringExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("Artist - Title", InputType.None, _dl);
            Assert.AreEqual(InputType.String, type);
            Assert.IsInstanceOfType(extractor, typeof(StringExtractor));
        }

        [TestMethod]
        public void GetMatchingExtractor_EmptyInput_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                ExtractorRegistry.GetMatchingExtractor("", InputType.None, _dl));
        }

        [TestMethod]
        public void GetMatchingExtractor_ExplicitInputType_ReturnsCorrectExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("anything", InputType.Soulseek, _dl);
            Assert.AreEqual(InputType.Soulseek, type);
            Assert.IsInstanceOfType(extractor, typeof(SoulseekExtractor));
        }
    }
}
