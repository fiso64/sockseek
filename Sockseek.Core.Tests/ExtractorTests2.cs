using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core;
using Sockseek.Core.Extractors;
using Sockseek.Core.Settings;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

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
        public async Task GetTracks_ModePrefixes_SetExplicitRequestedModeOverrides()
        {
            File.WriteAllText(_tempList,
                "s:\"Artist - Song\"\n" +
                "a:\"Artist - Album\"\n" +
                "\"Artist - Default\"");

            var extractor = new ListExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;

            var result = await extractor.GetTracks(_tempList, config.Extraction);
            var jobList = (JobList)result;

            Assert.AreEqual(3, jobList.Jobs.Count);
            Assert.AreEqual(ExtractionMode.Song, ((ExtractJob)jobList.Jobs[0]).RequestedModeOverride);
            Assert.AreEqual(ExtractionMode.Album, ((ExtractJob)jobList.Jobs[1]).RequestedModeOverride);
            Assert.IsNull(((ExtractJob)jobList.Jobs[2]).RequestedModeOverride);
        }

        [TestMethod]
        public async Task GetTracks_AlbumPrefix_DoesNotRewriteInputWithHiddenProtocol()
        {
            File.WriteAllText(_tempList, "a:\"Artist - Album\"");

            var extractor = new ListExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;

            var result = await extractor.GetTracks(_tempList, config.Extraction);
            var jobList = (JobList)result;
            var ej = (ExtractJob)jobList.Jobs[0];

            Assert.AreEqual("Artist - Album", ej.Input);
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

        [TestMethod]
        public async Task GetTracks_ConfigDirVariable_ResolvesListPath()
        {
            var configDir = Path.GetDirectoryName(_tempList)!;
            var listName = Path.GetFileName(_tempList);
            File.WriteAllText(_tempList, "Title");

            var extractor = new ListExtractor(new PathVariableContext(ConfigDir: configDir));
            var config = TestHelpers.CreateDefaultSettings().Download;

            var result = await extractor.GetTracks("{configdir}/" + listName, config.Extraction);
            var jobList = (JobList)result;

            Assert.AreEqual(1, jobList.Jobs.Count);
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

        [DataTestMethod]
        [DataRow("slsk:///bad")]
        [DataRow("slsk://local/")]
        [DataRow("slsk://local")]
        public async Task GetTracks_MalformedUri_ThrowsInputError(string uri)
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultSettings().Download;

            await Assert.ThrowsExceptionAsync<ArgumentException>(() => extractor.GetTracks(uri, config.Extraction));
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
        public async Task GetTracks_EmptyCsv_ReturnsEmptyJobList()
        {
            File.WriteAllText(_tempCsv, "");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);

            var extractTask = Task.Run(() => extractor.GetTracks(_tempCsv, config.Extraction));
            var completedTask = await Task.WhenAny(extractTask, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.AreSame(extractTask, completedTask, "Empty CSV extraction should complete instead of waiting forever.");
            var list = (JobList)await extractTask;

            Assert.AreEqual(0, list.Jobs.Count);
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
        public async Task GetTracks_ReturnsSingleFlatJobList()
        {
            File.WriteAllText(_tempCsv, "artist,title,album\nArtist1,Song1,\nAlbumArtist,,Album1\nArtist2,Song2,\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var list = (JobList)result;

            Assert.AreEqual(3, list.Jobs.Count);
            Assert.IsInstanceOfType(list.Jobs[0], typeof(SongJob));
            Assert.IsInstanceOfType(list.Jobs[1], typeof(AlbumJob));
            Assert.IsInstanceOfType(list.Jobs[2], typeof(SongJob));
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
            var list = (JobList)result;

            Assert.AreEqual(1, list.Jobs.Count);
            Assert.IsTrue(list.Jobs[0] is AlbumJob || list.Jobs[0] is AlbumAggregateJob);
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

        [TestMethod]
        public async Task GetTracks_InvalidTimeFormat_ThrowsInputError()
        {
            File.WriteAllText(_tempCsv, "artist,title,length\nArtist,Track,200\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Csv.TimeUnit = "bogus";
            var extractor = new CsvExtractor(config.Csv);

            await Assert.ThrowsExceptionAsync<ArgumentException>(() => extractor.GetTracks(_tempCsv, config.Extraction));
        }

        [TestMethod]
        public async Task GetTracks_ConfigDirVariable_ResolvesCsvPath()
        {
            var configDir = Path.GetDirectoryName(_tempCsv)!;
            var csvName = Path.GetFileName(_tempCsv);
            File.WriteAllText(_tempCsv, "artist,title\nArtist,Title\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv, new PathVariableContext(ConfigDir: configDir));

            var result = await extractor.GetTracks("{configdir}/" + csvName, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual("Artist", songs[0].Query.Artist);
            Assert.AreEqual("Title", songs[0].Query.Title);
        }
    }

    [TestClass]
    public class CsvSourceMutationTests
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
        public async Task ExtractedSongRows_CarryDurableCsvMutationMetadata()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var song = ((JobList)result).AllSongs().Single();

            Assert.AreEqual(2, song.LineNumber);
            Assert.AreEqual(SourceMutationKind.ClearCsvRow, song.SourceMutation?.Kind);
            Assert.AreEqual(Utils.NormalizedPath(_tempCsv), song.SourceMutation?.Source);
            Assert.AreEqual(2, song.SourceMutation?.LineNumber);
            Assert.AreEqual(2, song.SourceMutation?.CsvColumnCount);
        }

        [TestMethod]
        public async Task SourceMutationExecutor_ClearsCsvRowsWithoutTouchingHeader()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);
            var executor = new SourceMutationExecutor();
            var result = await extractor.GetTracks(_tempCsv, config.Extraction);
            var songs = ((JobList)result).AllSongs().ToList();

            foreach (var song in songs)
                await executor.ApplyAsync(song.SourceMutation!, config);

            var lines = await File.ReadAllLinesAsync(_tempCsv);
            Assert.AreEqual("artist,title", lines[0]);
            Assert.AreEqual(",", lines[1]);
            Assert.AreEqual(",", lines[2]);
        }

        [TestMethod]
        public async Task GetTracks_AlbumRow_AlbumJobHasDurableCsvMutationMetadata()
        {
            File.WriteAllText(_tempCsv, "artist,title,album\nBand,,TheAlbum\n");
            var config = TestHelpers.CreateDefaultSettings().Download;
            var extractor = new CsvExtractor(config.Csv);

            var result = await extractor.GetTracks(_tempCsv, config.Extraction);

            var list = (JobList)result;
            var album = list.Jobs.OfType<AlbumJob>().Single();

            Assert.AreEqual(2, album.LineNumber, "AlbumJob.LineNumber must match the 1-based CSV line");
            Assert.AreEqual(SourceMutationKind.ClearCsvRow, album.SourceMutation?.Kind);
            Assert.AreEqual(Utils.NormalizedPath(_tempCsv), album.SourceMutation?.Source);
            Assert.AreEqual(2, album.SourceMutation?.LineNumber);
            Assert.AreEqual(3, album.SourceMutation?.CsvColumnCount);
        }
    }

    [TestClass]
    public class ListRemoveFromSourceTests
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
        public async Task ListInput_SongFails_RemoveFromSource_DoesNotClearRow()
        {
            File.WriteAllText(_tempList, "\"Valid - Song\"\n\"Missing - Song\"\n");

            var validFile = TestHelpers.CreateSlFile(@"Music\Valid - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [validFile]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            var eng = new EngineSettings { Username = "u", Password = "p" };
            var dl = new DownloadSettings();
            dl.Extraction.Input = _tempList;
            dl.Extraction.InputType = InputType.List;
            dl.Extraction.RemoveTracksFromSource = true;

            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-test-list-rfs-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
            dl.Output.ParentDir = outputDir;

            try
            {
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(_tempList, InputType.List), dl);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                var listJob = app.Queue.AllJobs().OfType<JobList>().FirstOrDefault();
                Assert.IsNotNull(listJob);
                Assert.AreEqual(JobTerminalOutcome.PartialSuccess, listJob.TerminalOutcome);

                var lines = File.ReadAllLines(_tempList);
                Assert.AreEqual(2, lines.Length, "File should retain its physical lines (cleared lines become empty string).");
                Assert.AreEqual("", lines[0], "Successful song row should be cleared.");
                Assert.AreEqual("\"Missing - Song\"", lines[1], "Failed song row should NOT be cleared.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task ListInput_RecursiveCsvPartiallyFails_RemoveFromSource_DoesNotClearListRow()
        {
            var csvPath = Path.GetTempFileName() + ".csv";
            File.WriteAllText(csvPath, "artist,title\nValid,Song\nMissing,Song\n");
            File.WriteAllText(_tempList, $"\"{csvPath}\"\n");

            var validFile = TestHelpers.CreateSlFile(@"Music\Valid - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [validFile]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            var eng = new EngineSettings { Username = "u", Password = "p" };
            var dl = new DownloadSettings();
            dl.Extraction.Input = _tempList;
            dl.Extraction.InputType = InputType.List;
            dl.Extraction.RemoveTracksFromSource = true;

            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-test-list-rfs-csv-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
            dl.Output.ParentDir = outputDir;

            try
            {
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(_tempList, InputType.List), dl);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                Assert.IsTrue(
                    app.Queue.AllJobs().OfType<JobList>().Any(job => job.TerminalOutcome == JobTerminalOutcome.PartialSuccess),
                    "The nested CSV job list should be marked partial when some extracted children succeed and others fail.");

                var csvLines = File.ReadAllLines(csvPath);
                Assert.AreEqual("artist,title", csvLines[0]);
                Assert.AreEqual(",", csvLines[1], "Successful CSV song row should be cleared.");
                Assert.AreEqual("Missing,Song", csvLines[2], "Failed CSV song row should NOT be cleared.");

                var listLines = File.ReadAllLines(_tempList);
                Assert.AreEqual($"\"{csvPath}\"", listLines[0], "List row pointing to partially failed CSV should NOT be cleared.");
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task CsvInput_AggregateChildrenFail_RemoveFromSource_DoesNotClearRow()
        {
            var csvPath = Path.GetTempFileName() + ".csv";
            File.WriteAllText(csvPath, "artist,title\nFail,Song\n");

            var failingFile = TestHelpers.CreateSlFile(@"Music\Fail - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("failuser", 1, true, 100, 0, [failingFile]);
            var testClient = new ClientTests.MockSoulseekClient([response], failingUsers: ["failuser"]);

            var eng = new EngineSettings { Username = "u", Password = "p" };
            var dl = new DownloadSettings();
            dl.Extraction.Input = csvPath;
            dl.Extraction.InputType = InputType.CSV;
            dl.Extraction.RemoveTracksFromSource = true;
            dl.Search.IsAggregate = true;
            dl.Search.MinSharesAggregate = 1;

            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-test-csv-agg-rfs-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
            dl.Output.ParentDir = outputDir;

            try
            {
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(csvPath, InputType.CSV), dl);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                var aggregateJob = app.Queue.AllJobs().OfType<AggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggregateJob);
                Assert.IsTrue(aggregateJob.IsUnsuccessfulTerminal);

                var lines = File.ReadAllLines(csvPath);
                Assert.AreEqual("artist,title", lines[0]);
                Assert.AreEqual("Fail,Song", lines[1], "Aggregate CSV row should NOT be cleared when its child download fails.");
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task CsvInput_AlbumAggregateChildrenFail_RemoveFromSource_DoesNotClearRow()
        {
            var csvPath = Path.GetTempFileName() + ".csv";
            File.WriteAllText(csvPath, "artist,title,album\nFail,,Album\n");

            var failingFile = TestHelpers.CreateSlFile(@"Music\Fail\Album\01. Fail - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("failuser", 1, true, 100, 0, [failingFile]);
            var testClient = new ClientTests.MockSoulseekClient([response], failingUsers: ["failuser"]);

            var eng = new EngineSettings { Username = "u", Password = "p" };
            var dl = new DownloadSettings();
            dl.Extraction.Input = csvPath;
            dl.Extraction.InputType = InputType.CSV;
            dl.Extraction.RemoveTracksFromSource = true;
            dl.Search.IsAggregate = true;
            dl.Search.MinSharesAggregate = 1;
            dl.Search.NoBrowseFolder = true;

            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-test-csv-album-agg-rfs-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
            dl.Output.ParentDir = outputDir;

            try
            {
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(csvPath, InputType.CSV), dl);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                var aggregateJob = app.Queue.AllJobs().OfType<AlbumAggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggregateJob);
                Assert.IsTrue(
                    aggregateJob.Albums.Any(album => album.IsUnsuccessfulTerminal),
                    "At least one generated album child should have failed.");

                var lines = File.ReadAllLines(csvPath);
                Assert.AreEqual("artist,title,album", lines[0]);
                Assert.AreEqual("Fail,,Album", lines[1], "Album aggregate CSV row should NOT be cleared when its child album fails.");
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
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
