using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Sldl.Core.Jobs;
using Sldl.Core.Services;

namespace Tests.EndToEnd
{
    [TestClass]
    public class ProgramTests
    {
        // Regression: --mock-files-dir should expose Soulseek-style remote paths while
        // still copying from local source files. {foldername} must stay the remote
        // album leaf, not a mirrored local filesystem subtree.
        [TestMethod]
        public async Task AlbumDownload_MockFilesDir_FoldernameIsLeafOnly()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "Main", "TestArtist", "TestAlbum");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(albumDir);
            System.IO.Directory.CreateDirectory(outputDir);

            // Write placeholder mp3 bytes so the downloader can copy actual content
            System.IO.File.WriteAllBytes(Path.Combine(albumDir, "01. TestArtist - Track1.mp3"), TestHelpers.EmptyMp3Bytes);
            System.IO.File.WriteAllBytes(Path.Combine(albumDir, "02. TestArtist - Track2.mp3"), TestHelpers.EmptyMp3Bytes);

            var testClient = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, albumDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = "TestArtist TestAlbum";
                rootSettings.Extraction.IsAlbum = true;
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.NameFormat = "{foldername}/{filename}";

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                // Files must land directly in outputDir/TestAlbum/, NOT buried inside a
                // full mirrored subtree like outputDir/TestAlbum/Main/TestArtist/TestAlbum/
                var expectedDir = Path.Combine(outputDir, "TestAlbum");
                Assert.IsTrue(System.IO.Directory.Exists(expectedDir),
                    $"Expected output folder '{expectedDir}' does not exist. " +
                    $"Actual tree: {string.Join(", ", System.IO.Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories))}");

                var topLevelFiles = System.IO.Directory.GetFiles(expectedDir, "*", SearchOption.TopDirectoryOnly);
                Assert.IsTrue(topLevelFiles.Any(f => Path.GetFileName(f) == "01. TestArtist - Track1.mp3"),
                    "Track1 must be directly inside TestAlbum/, not in a subdirectory");
                Assert.IsTrue(topLevelFiles.Any(f => Path.GetFileName(f) == "02. TestArtist - Track2.mp3"),
                    "Track2 must be directly inside TestAlbum/, not in a subdirectory");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot)) System.IO.Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumDownload_E2E()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Debug);

            var testClient = new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-batchdl-e2e", Guid.NewGuid().ToString());
            System.IO.Directory.CreateDirectory(outputDir);

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var rootSettings = new DownloadSettings();
            rootSettings.Extraction.Input = "testartist - testalbum";
            rootSettings.Extraction.IsAlbum = true;
            rootSettings.Output.ParentDir = outputDir;
            rootSettings.Output.NameFormat = "{foldername}/{filename}";

            var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
            var app = new DownloadEngine(engineSettings, clientManager);
            app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
            app.CompleteEnqueue();

            try
            {
                await app.RunAsync(CancellationToken.None);

                // Assertions
                Console.WriteLine($"[Trace] outputDir contents: {string.Join(", ", System.IO.Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Select(f => f.Replace(outputDir, "")))}");
                Console.WriteLine($"[Trace] Queue jobs: {app.Queue.Jobs.Count}, states: {string.Join(", ", app.Queue.Jobs.Select(j => $"{j.GetType().Name}:{j.State}"))}");
                var albumJob2 = app.Queue.Jobs.OfType<AlbumJob>().FirstOrDefault();
                if (albumJob2 != null)
                {
                    Console.WriteLine($"[Trace] AlbumJob state={albumJob2.State} failureReason={albumJob2.FailureReason} resolvedTarget={albumJob2.ResolvedTarget?.FolderPath} results={albumJob2.Results.Count}");
                    foreach (var f in albumJob2.Results.SelectMany(r => r.Files))
                        Console.WriteLine($"[Trace]   file: {f.Query.Title} state={f.State} dp={f.DownloadPath} candidates={f.Candidates?.Count} rt={f.ResolvedTarget?.Filename}");
                }
                var downloadedFiles = System.IO.Directory.GetFiles(Path.Combine(outputDir, "(2011) testalbum [MP3]"), "*", SearchOption.AllDirectories);
                Assert.AreEqual(4, downloadedFiles.Length, "Should download 4 files for the album.");
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("0101. testartist - testsong.mp3")));
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("0102. testartist - testsong2.mp3")));
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("0103. testartist - testsong3.mp3")));
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("cover.jpg")));
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                {
                    System.IO.Directory.Delete(outputDir, true);
                }
            }
        }

        [TestMethod]
        public async Task PreselectedAlbumJob_SkipsSearchAndDownloadsResolvedFolder()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Error);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-preselected-music-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "Artist", "Chosen Album");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-preselected-out-" + Guid.NewGuid());
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Track One.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), TestHelpers.EmptyMp3Bytes);

            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.NameFormat = "{foldername}/{filename}";
                rootSettings.Search.NoBrowseFolder = true;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var registry = TestHelpers.CreateSessionRegistry();
                var searcher = new Searcher(testClient, registry, registry, new EngineEvents(), 10, 10);
                var seedJob = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Chosen Album" });
                await searcher.SearchAlbum(seedJob, rootSettings.Search, new ResponseData(), CancellationToken.None);
                var selected = seedJob.Results.Single();

                var concreteJob = new AlbumJob(new AlbumQuery { Artist = "Wrong Artist", Album = "Wrong Album" })
                {
                    ResolvedTarget = selected,
                    AllowBrowseResolvedTarget = false,
                    Results = [selected],
                };

                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(concreteJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Done, concreteJob.State);
                var downloadedFiles = Directory.GetFiles(Path.Combine(outputDir, "Chosen Album"), "*", SearchOption.AllDirectories);
                Assert.AreEqual(2, downloadedFiles.Length);
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("01. Artist - Track One.mp3")));
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("02. Artist - Track Two.mp3")));
            }
            finally
            {
                if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task PreselectedSongJob_SkipsSearchAndDownloadsResolvedFile()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Error);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-preselected-song-music-" + Guid.NewGuid());
            var songDir   = Path.Combine(musicRoot, "Artist");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-preselected-song-out-" + Guid.NewGuid());
            Directory.CreateDirectory(songDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(songDir, "Artist - Real Track.mp3"), TestHelpers.EmptyMp3Bytes);

            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.NameFormat = "{filename}";

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var registry = TestHelpers.CreateSessionRegistry();
                var searcher = new Searcher(testClient, registry, registry, new EngineEvents(), 10, 10);
                var seedSong = new SongJob(new SongQuery { Artist = "Artist", Title = "Real Track" });
                await searcher.SearchSong(seedSong, rootSettings.Search, new ResponseData(), CancellationToken.None);
                var selected = seedSong.Candidates!.Single();

                var concreteJob = new SongJob(new SongQuery { Artist = "Wrong Artist", Title = "Wrong Track" })
                {
                    ResolvedTarget = selected,
                };

                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(concreteJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Done, concreteJob.State);
                var downloadedFiles = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
                Assert.AreEqual(1, downloadedFiles.Length);
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("Artist - Real Track.mp3")));
            }
            finally
            {
                if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task PrintResults_SongJob_SearchesWithoutDownloading()
        {
            var testClient = new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-print-song-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.PrintOption = PrintOption.Results;

                var songJob = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" });
                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(songJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Done, songJob.State);
                Assert.IsTrue(songJob.Candidates?.Count > 0, "Print-results mode should populate song candidates.");
                Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                    "Print-results mode should not download files.");
            }
            finally
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task PrintResults_AlbumJob_SearchesWithoutDownloading()
        {
            var testClient = new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-print-album-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.PrintOption = PrintOption.Results;
                rootSettings.Extraction.IsAlbum = true;

                var albumJob = new AlbumJob(new AlbumQuery { Artist = "testartist", Album = "testalbum" });
                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(albumJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.IsTrue(albumJob.Results.Count > 0, "Print-results mode should populate album search results.");
                Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                    "Print-results mode should not download album files.");
            }
            finally
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task PrintResults_AlbumAggregateJob_SearchesWithoutDownloading()
        {
            var lengthOne = new Soulseek.FileAttribute(Soulseek.FileAttributeType.Length, 120);
            var lengthTwo = new Soulseek.FileAttribute(Soulseek.FileAttributeType.Length, 180);
            var index = new List<Soulseek.SearchResponse>
            {
                new(
                    username: "user1",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList:
                    [
                        new Soulseek.File(1, "Music\\ELO\\Time\\01. ELO - First.mp3", 10000, ".mp3", [lengthOne]),
                        new Soulseek.File(2, "Music\\ELO\\Time\\02. ELO - Second.mp3", 10000, ".mp3", [lengthTwo]),
                    ]),
                new(
                    username: "user2",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList:
                    [
                        new Soulseek.File(3, "Shares\\Electric Light Orchestra\\Time\\01. ELO - First.mp3", 10000, ".mp3", [lengthOne]),
                        new Soulseek.File(4, "Shares\\Electric Light Orchestra\\Time\\02. ELO - Second.mp3", 10000, ".mp3", [lengthTwo]),
                    ]),
            };
            var testClient = new ClientTests.MockSoulseekClient(index);
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-print-album-aggregate-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.PrintOption = PrintOption.Results;

                var aggregateJob = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                var startedAlbumJobs = 0;
                app.Events.JobStarted += job =>
                {
                    if (job is AlbumJob)
                        startedAlbumJobs++;
                };
                app.Enqueue(aggregateJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Done, aggregateJob.State);
                Assert.IsTrue(aggregateJob.Albums.Count > 0, "Print-results mode should retain album-aggregate candidates for printing.");
                Assert.AreEqual(0, startedAlbumJobs, "Print-results mode should not re-search album-aggregate candidate albums.");
                Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                    "Print-results mode should not download album-aggregate files.");
            }
            finally
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_DownloadsResolvedAlbumCandidatesWithoutResearchingThem()
        {
            var lengthOne = new Soulseek.FileAttribute(Soulseek.FileAttributeType.Length, 120);
            var lengthTwo = new Soulseek.FileAttribute(Soulseek.FileAttributeType.Length, 180);
            var index = new List<Soulseek.SearchResponse>
            {
                new(
                    username: "user1",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList:
                    [
                        new Soulseek.File(1, "Music\\ELO\\Time\\01. ELO - First.mp3", 10000, ".mp3", [lengthOne]),
                        new Soulseek.File(2, "Music\\ELO\\Time\\02. ELO - Second.mp3", 10000, ".mp3", [lengthTwo]),
                    ]),
                new(
                    username: "user2",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList:
                    [
                        new Soulseek.File(3, "Shares\\Electric Light Orchestra\\Time\\01. ELO - First.mp3", 10000, ".mp3", [lengthOne]),
                        new Soulseek.File(4, "Shares\\Electric Light Orchestra\\Time\\02. ELO - Second.mp3", 10000, ".mp3", [lengthTwo]),
                    ]),
            };
            var testClient = new ClientTests.MockSoulseekClient(index);
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-download-album-aggregate-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Search.NoBrowseFolder = true;

                var aggregateJob = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                var startedAlbumJobs = 0;
                var albumDownloadsStarted = 0;
                app.Events.JobStarted += job =>
                {
                    if (job is AlbumJob)
                        startedAlbumJobs++;
                };
                app.Events.AlbumDownloadStarted += (job, _) =>
                {
                    if (job is AlbumJob)
                        albumDownloadsStarted++;
                };
                app.Enqueue(aggregateJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Done, aggregateJob.State);
                Assert.IsTrue(aggregateJob.Albums.Count > 0, "Album aggregate should produce resolved album candidates.");
                Assert.AreEqual(0, startedAlbumJobs, "Resolved album-aggregate candidates should not run a second album search.");
                Assert.IsTrue(albumDownloadsStarted > 0, "Resolved album-aggregate candidates should still enter album download.");
                Assert.IsTrue(Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length > 0,
                    "Album aggregate should download files from the resolved candidate album.");
            }
            finally
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AggregateJob_DownloadsResolvedSongCandidatesWithoutResearchingThem()
        {
            var length = new Soulseek.FileAttribute(Soulseek.FileAttributeType.Length, 180);
            var index = new List<Soulseek.SearchResponse>
            {
                new(
                    username: "user1",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList:
                    [
                        new Soulseek.File(1, "Music\\ELO\\01. ELO - Blue Sky.mp3", 10000, ".mp3", [length]),
                    ]),
                new(
                    username: "user2",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList:
                    [
                        new Soulseek.File(2, "Shares\\Electric Light Orchestra\\01. ELO - Blue Sky.mp3", 10000, ".mp3", [length]),
                    ]),
            };
            var testClient = new ClientTests.MockSoulseekClient(index);
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-download-aggregate-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Search.MinSharesAggregate = 1;
                rootSettings.Skip.SkipExisting = false;

                var aggregateJob = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                var startedSongJobs = 0;
                var songSearchesStarted = 0;
                var downloadsStarted = 0;
                app.Events.JobStarted += job =>
                {
                    if (job is SongJob)
                        startedSongJobs++;
                };
                app.Events.SongSearching += _ => songSearchesStarted++;
                app.Events.DownloadStarted += (_, _) => downloadsStarted++;
                app.Enqueue(aggregateJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.IsTrue(aggregateJob.Songs.Count > 0, "Aggregate should produce resolved song candidates.");
                Assert.AreEqual(0, startedSongJobs, "Resolved aggregate child songs should not emit generic search-style job starts.");
                Assert.AreEqual(0, songSearchesStarted, "Resolved aggregate child songs should not run a second song search.");
                Assert.IsTrue(downloadsStarted > 0, "Resolved aggregate child songs should still download.");
            }
            finally
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
            }
        }

    }
}
