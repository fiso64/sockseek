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
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

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
        public async Task SingleSong_WritePlaylist_GeneratesCorrectM3uFile()
        {
            Console.ResetColor();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-playlist-test-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-playlist-test-out-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(musicRoot);
            System.IO.Directory.CreateDirectory(outputDir);

            System.IO.File.WriteAllBytes(Path.Combine(musicRoot, "02. Electric Light Orchestra - Twilight.flac"), TestHelpers.EmptyMp3Bytes);

            var testClient = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = "electric light orchestra twilight";
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.WritePlaylist = true;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                
                // This mimics the CLI: enqueue an ExtractJob which will yield a bare SongJob
                app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
                app.CompleteEnqueue();
                
                await app.RunAsync(CancellationToken.None);

                // Find the playlist file
                string expectedPlaylistName = "_electric light orchestra twilight.m3u8";
                string playlistPath = Path.Combine(outputDir, expectedPlaylistName);
                
                Assert.IsTrue(System.IO.File.Exists(playlistPath), $"Playlist file was not created at {playlistPath}");
                
                var lines = System.IO.File.ReadAllLines(playlistPath);
                Assert.AreEqual(1, lines.Length, "Playlist should contain exactly one track line.");
                Assert.AreEqual("02. Electric Light Orchestra - Twilight.flac", lines[0].Replace('\\', '/'), "Playlist should contain the correct relative file path.");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot)) System.IO.Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SingleSong_WriteIndex_GeneratesCorrectIndexFile()
        {
            Console.ResetColor();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-index-test-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-index-test-out-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(musicRoot);
            System.IO.Directory.CreateDirectory(outputDir);

            System.IO.File.WriteAllBytes(Path.Combine(musicRoot, "01. Test Artist - Test Title.mp3"), TestHelpers.EmptyMp3Bytes);

            var testClient = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = "Test Artist - Test Title";
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.WriteIndex = true;
                rootSettings.Output.HasConfiguredIndex = true;
                rootSettings.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv"); // Force fixed path to simplify asserting

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                
                // This mimics the CLI: enqueue an ExtractJob which will yield a bare SongJob
                app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
                app.CompleteEnqueue();
                
                await app.RunAsync(CancellationToken.None);

                string indexPath = Path.Combine(outputDir, "_index.csv");
                
                Assert.IsTrue(System.IO.File.Exists(indexPath), $"Index file was not created at {indexPath}");
                
                var lines = System.IO.File.ReadAllLines(indexPath);
                Assert.IsTrue(lines.Length >= 2, "Index should contain at least a header and one track line.");
                Assert.AreEqual("filepath,artist,album,title,length,tracktype,state,failurereason", lines[0]);
                
                // We expect a line like: ./01. Test Artist - Test Title.mp3,Test Artist,,Test Title,-1,0,1,0
                // Tracktype=0 (Song), State=1 (Done), FailureReason=0 (None)
                Assert.IsTrue(lines[1].StartsWith("./01. Test Artist - Test Title.mp3,Test Artist,,Test Title,"), $"Unexpected index line format: {lines[1]}");
                Assert.IsTrue(lines[1].EndsWith(",0,1,0"), $"Unexpected state/failure reason in index line: {lines[1]}");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot)) System.IO.Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SingleSong_WriteIndex_UsesNameFormattedPath()
        {
            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-index-song-format-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-index-song-format-out-" + Guid.NewGuid());
            Directory.CreateDirectory(musicRoot);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(musicRoot, "01. Test Artist - Test Title.mp3"), TestHelpers.EmptyMp3Bytes);
            var testClient = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var settings = new DownloadSettings();
                settings.Extraction.Input = "Test Artist - Test Title";
                settings.Output.ParentDir = outputDir;
                settings.Output.NameFormat = "Renamed/{sartist} - {stitle}";
                settings.Output.WriteIndex = true;
                settings.Output.HasConfiguredIndex = true;
                settings.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");

                var app = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(testClient, engineSettings));
                app.Enqueue(new ExtractJob(settings.Extraction.Input!, settings.Extraction.InputType), settings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var lines = File.ReadAllLines(settings.Output.IndexFilePath);
                Assert.AreEqual("filepath,artist,album,title,length,tracktype,state,failurereason", lines[0]);
                var normalizedLines = lines.Select(line => line.Replace('\\', '/')).ToList();
                Assert.IsTrue(normalizedLines.Any(line => line.StartsWith("./Renamed/Test Artist - Test Title.mp3,Test Artist,,Test Title,")
                    && line.EndsWith(",0,1,0")), string.Join(Environment.NewLine, lines));
            }
            finally
            {
                if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumDownload_WriteIndex_UsesNameFormattedAlbumPath()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-index-album-format-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var response = new Soulseek.SearchResponse(
                username: "user",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100_000,
                queueLength: 0,
                fileList:
                [
                    TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - First.mp3", length: 180),
                    TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\02. Test Artist - Second.mp3", length: 181),
                ]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var settings = new DownloadSettings();
                settings.Extraction.Input = "artist=Test Artist, album=Test Album";
                settings.Extraction.IsAlbum = true;
                settings.Search.NoBrowseFolder = true;
                settings.Output.ParentDir = outputDir;
                settings.Output.NameFormat = "Renamed/{foldername}/{filename}";
                settings.Output.WriteIndex = true;
                settings.Output.HasConfiguredIndex = true;
                settings.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");

                var app = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(testClient, engineSettings));
                app.Enqueue(new ExtractJob(settings.Extraction.Input!, settings.Extraction.InputType), settings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var lines = File.ReadAllLines(settings.Output.IndexFilePath);
                Assert.AreEqual("filepath,artist,album,title,length,tracktype,state,failurereason", lines[0]);
                var normalizedLines = lines.Select(line => line.Replace('\\', '/')).ToList();
                Assert.IsTrue(normalizedLines.Any(line => line == "./Renamed/Test Album,Test Artist,Test Album,,-1,1,1,0"),
                    string.Join(Environment.NewLine, lines));
                Assert.IsFalse(lines.Any(line => line.Contains("First.mp3") || line.Contains("Second.mp3")),
                    "Album child songs should not be written as index entries.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SingleSong_WriteIndex_FailedDownloadStoresFailureWithoutPath()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-index-song-fail-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var response = new Soulseek.SearchResponse(
                "failuser", 1, true, 100_000, 0,
                [TestHelpers.CreateSlFile(@"Music\Test Artist - Test Title.mp3", length: 180)]);
            var testClient = new ClientTests.MockSoulseekClient([response], failingUsers: ["failuser"]);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var settings = new DownloadSettings();
                settings.Extraction.Input = "Test Artist - Test Title";
                settings.Output.ParentDir = outputDir;
                settings.Output.WriteIndex = true;
                settings.Output.HasConfiguredIndex = true;
                settings.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                settings.Transfer.MaxDownloadRetries = 1;

                var app = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(testClient, engineSettings));
                app.Enqueue(new ExtractJob(settings.Extraction.Input!, settings.Extraction.InputType), settings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var lines = File.ReadAllLines(settings.Output.IndexFilePath);
                Assert.IsTrue(lines.Any(line => line == ",Test Artist,,Test Title,-1,0,2,4"),
                    string.Join(Environment.NewLine, lines));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumDownload_WriteIndex_FailedDownloadStoresFailureWithoutPath()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-index-album-fail-out-" + Guid.NewGuid());
            var failedDir = Path.Combine(Path.GetTempPath(), "slsk-index-album-fail-failed-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(failedDir);

            var completedAlbumDir = Path.Combine(outputDir, "Test Album");
            Directory.CreateDirectory(completedAlbumDir);
            var completedPath = Path.Combine(completedAlbumDir, "01. Test Artist - First.mp3");
            File.WriteAllBytes(completedPath, TestHelpers.EmptyMp3Bytes);

            var completedRemoteFile = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - First.mp3", length: 180);
            var failingRemoteFile = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\02. Test Artist - Second.mp3", length: 181);
            var response = new Soulseek.SearchResponse(
                "failuser", 1, true, 100_000, 0,
                [completedRemoteFile, failingRemoteFile]);
            var testClient = new ClientTests.MockSoulseekClient([response], failingUsers: ["failuser"]);

            var completedSong = new SongJob(new SongQuery { Artist = "Test Artist", Album = "Test Album", Title = "First" })
            {
                ResolvedTarget = new FileCandidate(response, completedRemoteFile),
            };
            completedSong.SetDone(completedPath);
            var failingSong = new SongJob(new SongQuery { Artist = "Test Artist", Album = "Test Album", Title = "Second" })
            {
                ResolvedTarget = new FileCandidate(response, failingRemoteFile),
            };
            var folder = new AlbumFolder(
                response.Username,
                Utils.GetDirectoryNameSlsk(failingRemoteFile.Filename),
                [completedSong, failingSong])
                {
                    IsFullyRetrieved = true,
                };
            var album = new AlbumJob(new AlbumQuery { Artist = "Test Artist", Album = "Test Album" })
            {
                Results = [folder],
            };

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var settings = new DownloadSettings();
                settings.Extraction.Input = "artist=Test Artist, album=Test Album";
                settings.Extraction.IsAlbum = true;
                settings.Search.NoBrowseFolder = true;
                settings.Output.ParentDir = outputDir;
                settings.Output.FailedAlbumPath = failedDir;
                settings.Output.WriteIndex = true;
                settings.Output.HasConfiguredIndex = true;
                settings.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                settings.Transfer.MaxDownloadRetries = 1;

                var app = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(testClient, engineSettings));
                app.Enqueue(album, settings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.IsTrue(Directory.GetFiles(failedDir, "*", SearchOption.AllDirectories)
                        .Any(path => Path.GetFileName(path) == "01. Test Artist - First.mp3"),
                    "The already-downloaded album file should have been moved to failed-album-path.");

                var lines = File.ReadAllLines(settings.Output.IndexFilePath);
                Assert.IsTrue(lines.Any(line => line == ",Test Artist,Test Album,,-1,1,2,4"),
                    string.Join(Environment.NewLine, lines));
                Assert.IsFalse(lines.Any(line => line.Contains(failedDir) || line.Contains("First.mp3")),
                    "Failed album index entry should record only album failure, not failed-album-path or child files.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (Directory.Exists(failedDir)) Directory.Delete(failedDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumDownload_E2E()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

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
        public async Task ListAlbumDownload_LineFormatConditionRejectsMp3Album()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-list-format-music-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "Artist", "Album2");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-list-format-out-" + Guid.NewGuid());
            var listPath  = Path.GetTempFileName();
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Album2 Track.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllText(listPath, "a:\"Album2\"\t\t\t\t\tstrict-album=true;format=flac\n");

            var testClient = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = listPath;
                rootSettings.Extraction.InputType = InputType.List;
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.WriteIndex = false;
                rootSettings.Output.HasConfiguredIndex = true;
                rootSettings.Search.NecessaryCond.Formats = ["flac", "mp3"];
                rootSettings.Search.NecessaryCond.MinBitrate = 200;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var resolver = new ProfileJobSettingsResolver(
                    rootSettings,
                    defaultProfile: null,
                    autoProfiles: [],
                    namedProfiles: [new SettingsProfile { Name = "wishlist" }],
                    cliProfile: null,
                    normalize: SettingsNormalizer.Normalize);
                var app = new DownloadEngine(engineSettings, clientManager, resolver);
                app.Enqueue(new ExtractJob(listPath, InputType.List), rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var downloadedFiles = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                    .Where(Utils.IsMusicFile)
                    .ToList();
                Assert.AreEqual(0, downloadedFiles.Count,
                    $"The list-row format=flac condition should reject the only available mp3 album, but downloaded: {string.Join(", ", downloadedFiles)}");
            }
            finally
            {
                if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(listPath)) File.Delete(listPath);
            }
        }

        [TestMethod]
        public async Task ListAlbumDownload_StrictAlbumRequiresFullAlbumNameInFolderPath()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-list-strict-album-music-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "Artist", "Album", "Disc 1");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-list-strict-album-out-" + Guid.NewGuid());
            var listPath  = Path.GetTempFileName();
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Split Terms.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllText(listPath, "a:\"Album 1\"                  strict-album=true\n");

            var testClient = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = listPath;
                rootSettings.Extraction.InputType = InputType.List;
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.WriteIndex = false;
                rootSettings.Output.HasConfiguredIndex = true;
                rootSettings.Search.NecessaryCond.Formats = ["flac", "mp3"];
                rootSettings.Search.NecessaryCond.MinBitrate = 200;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var resolver = new ProfileJobSettingsResolver(
                    rootSettings,
                    defaultProfile: null,
                    autoProfiles: [],
                    namedProfiles: [new SettingsProfile { Name = "wishlist" }],
                    cliProfile: null,
                    normalize: SettingsNormalizer.Normalize);
                var app = new DownloadEngine(engineSettings, clientManager, resolver);
                app.Enqueue(new ExtractJob(listPath, InputType.List), rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var downloadedFiles = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                    .Where(Utils.IsMusicFile)
                    .ToList();
                Assert.AreEqual(0, downloadedFiles.Count,
                    $"Soulseek search terms can match Album and 1 separately, but strict-album must require the full album name 'Album 1'. Downloaded: {string.Join(", ", downloadedFiles)}");
            }
            finally
            {
                if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(listPath)) File.Delete(listPath);
            }
        }

        [TestMethod]
        public async Task ListAlbumDownload_StrictAlbumAndTrackCountMustBothMatchSameFolder()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-list-strict-count-out-" + Guid.NewGuid());
            var listPath  = Path.GetTempFileName();

            Directory.CreateDirectory(outputDir);

            List<Soulseek.File> Tracks(string folder, string marker, int count)
            {
                var files = new List<Soulseek.File>();
                for (int i = 1; i <= count; i++)
                    files.Add(TestHelpers.CreateSlFile($@"{folder}\{i:D2}. Artist - {marker} {i:D2}.mp3", bitrate: 320));
                return files;
            }

            var index = new List<Soulseek.SearchResponse>
            {
                new(
                    username: "strict-fails-count-passes",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 500_000,
                    queueLength: 0,
                    fileList: Tracks(@"Artist\Album\Disc 1", "Strict Fails", 10)),
                new(
                    username: "strict-passes-count-fails",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 400_000,
                    queueLength: 0,
                    fileList: Tracks(@"Artist\Album 1", "Count Fails", 9)),
                new(
                    username: "both-pass",
                    token: 3,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: Tracks(@"Artist\Album 1", "Both Pass", 10)),
            };
            File.WriteAllText(listPath, "a:\"Album 1\"                         strict-album=true;album-track-count=10\n");

            var testClient = new LocalFilesSoulseekClient(index);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = listPath;
                rootSettings.Extraction.InputType = InputType.List;
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.NameFormat = "{foldername}/{filename}";
                rootSettings.Output.WriteIndex = false;
                rootSettings.Output.HasConfiguredIndex = true;
                rootSettings.Search.NecessaryCond.Formats = ["flac", "mp3"];
                rootSettings.Search.NecessaryCond.MinBitrate = 200;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var resolver = new ProfileJobSettingsResolver(
                    rootSettings,
                    defaultProfile: null,
                    autoProfiles: [],
                    namedProfiles: [new SettingsProfile { Name = "wishlist" }],
                    cliProfile: null,
                    normalize: SettingsNormalizer.Normalize);
                var app = new DownloadEngine(engineSettings, clientManager, resolver);
                AlbumJob? albumJob = null;
                app.Events.JobRegistered += (job, _) =>
                {
                    if (job is AlbumJob aj)
                        albumJob = aj;
                };
                app.Enqueue(new ExtractJob(listPath, InputType.List), rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var downloadedFiles = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                    .Where(Utils.IsMusicFile)
                    .ToList();

                Assert.IsNotNull(albumJob, "The list input should produce an album job.");
                Assert.AreEqual(JobState.Done, albumJob.State);
                Assert.IsNotNull(albumJob.ResolvedTarget, "The album job should resolve to the one folder satisfying both conditions.");
                Assert.AreEqual("both-pass", albumJob.ResolvedTarget!.Username,
                    $"Wrong album source selected. Candidates: {string.Join(", ", albumJob.Results.Select(r => $"{r.Username}:{r.FolderPath}"))}");
                Assert.AreEqual("Artist\\Album 1", albumJob.ResolvedTarget.FolderPath.Replace('/', '\\'),
                    $"Wrong album folder selected. Candidates: {string.Join(", ", albumJob.Results.Select(r => r.FolderPath))}");
                Assert.AreEqual(1, albumJob.Results.Count,
                    $"Folders satisfying only one condition should be filtered or rejected before download. Candidates: {string.Join(", ", albumJob.Results.Select(r => $"{r.Username}:{r.FolderPath}"))}");
                Assert.AreEqual(10, downloadedFiles.Count,
                    $"Expected exactly the ten tracks from the folder satisfying both conditions. Downloaded: {string.Join(", ", downloadedFiles)}");
                Assert.IsTrue(downloadedFiles.All(f => Path.GetFileName(f).Contains("Both Pass")),
                    $"Folders satisfying only one condition must be rejected. Downloaded: {string.Join(", ", downloadedFiles)}");
                Assert.IsTrue(downloadedFiles.All(f => f.Contains($"{Path.DirectorySeparatorChar}Album 1{Path.DirectorySeparatorChar}")),
                    $"The selected output folder should be the strict Album 1 folder. Downloaded: {string.Join(", ", downloadedFiles)}");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(listPath)) File.Delete(listPath);
            }
        }

        [TestMethod]
        public async Task ListAlbumDownload_AlbumTrackCountMinFiltersNormalAlbumSearchBeforeBrowse()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-list-track-count-browse-out-" + Guid.NewGuid());
            var listPath  = Path.GetTempFileName();
            Directory.CreateDirectory(outputDir);

            var files = new List<Soulseek.File>
            {
                TestHelpers.CreateSlFile(@"Artist\Candidate\01. Artist - Album 1.mp3", bitrate: 320),
            };

            for (int i = 2; i <= 10; i++)
                files.Add(TestHelpers.CreateSlFile($@"Artist\Candidate\{i:D2}. Artist - Track {i:D2}.mp3", bitrate: 320));

            var index = new List<Soulseek.SearchResponse>
            {
                new(
                    username: "partial-search-full-browse",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: files),
            };
            File.WriteAllText(listPath, "a:\"Album 1\"                         album-track-count=10\n");

            var testClient = new ClientTests.MockSoulseekClient(index);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = listPath;
                rootSettings.Extraction.InputType = InputType.List;
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.NameFormat = "{foldername}/{filename}";
                rootSettings.Output.WriteIndex = false;
                rootSettings.Output.HasConfiguredIndex = true;
                rootSettings.Search.NecessaryCond.Formats = ["flac", "mp3"];
                rootSettings.Search.NecessaryCond.MinBitrate = 200;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var resolver = new ProfileJobSettingsResolver(
                    rootSettings,
                    defaultProfile: null,
                    autoProfiles: [],
                    namedProfiles: [new SettingsProfile { Name = "wishlist" }],
                    cliProfile: null,
                    normalize: SettingsNormalizer.Normalize);
                var app = new DownloadEngine(engineSettings, clientManager, resolver);
                AlbumJob? albumJob = null;
                app.Events.JobRegistered += (job, _) =>
                {
                    if (job is AlbumJob aj)
                        albumJob = aj;
                };
                app.Enqueue(new ExtractJob(listPath, InputType.List), rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var downloadedFiles = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                    .Where(Utils.IsMusicFile)
                    .ToList();

                Assert.IsNotNull(albumJob, "The list input should produce an album job.");
                Assert.AreEqual(JobState.Failed, albumJob.State);
                Assert.AreEqual(FailureReason.NoSuitableFileFound, albumJob.FailureReason);
                Assert.AreEqual(0, testClient.BrowseCallCount,
                    "Normal album searches should filter visible min-count underflow before the slow full-user browse.");
                Assert.AreEqual(0, downloadedFiles.Count,
                    $"Min-count filtering should prevent downloads. Downloaded: {string.Join(", ", downloadedFiles)}");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(listPath)) File.Delete(listPath);
            }
        }

        [TestMethod]
        public async Task PreselectedAlbumJob_SkipsSearchAndDownloadsResolvedFolder()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Error);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-preselected-music-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "Artist", "Chosen Album");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-preselected-out-" + Guid.NewGuid());
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Track One.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), TestHelpers.EmptyMp3Bytes);

            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);

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
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Error);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-preselected-song-music-" + Guid.NewGuid());
            var songDir   = Path.Combine(musicRoot, "Artist");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-preselected-song-out-" + Guid.NewGuid());
            Directory.CreateDirectory(songDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(songDir, "Artist - Real Track.mp3"), TestHelpers.EmptyMp3Bytes);

            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);

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
                var searchedAlbumJobs = 0;
                app.Events.JobStateChanged += (job, state) =>
                {
                    if (state == JobState.Searching && job is AlbumJob)
                        searchedAlbumJobs++;
                };
                app.Enqueue(aggregateJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Done, aggregateJob.State);
                Assert.IsTrue(aggregateJob.Albums.Count > 0, "Print-results mode should retain album-aggregate candidates for printing.");
                Assert.AreEqual(0, searchedAlbumJobs, "Print-results mode should not re-search album-aggregate candidate albums.");
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
                var searchedAlbumJobs = 0;
                var albumDownloadsStarted = 0;
                app.Events.JobStateChanged += (job, state) =>
                {
                    if (state == JobState.Searching && job is AlbumJob)
                        searchedAlbumJobs++;
                    else if (state == JobState.Downloading && job is AlbumJob)
                        albumDownloadsStarted++;
                };
                app.Enqueue(aggregateJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Done, aggregateJob.State);
                Assert.IsTrue(aggregateJob.Albums.Count > 0, "Album aggregate should produce resolved album candidates.");
                Assert.AreEqual(0, searchedAlbumJobs, "Resolved album-aggregate candidates should not run a second album search.");
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

        // Bug: DownloadEngine calls extractor.RemoveTrackFromSource(new SongJob{...}) when a
        // JobList's directSongs all succeed. SongJob.LineNumber defaults to 1, so idx = 0,
        // which erases lines[0] = the CSV header instead of being a no-op.
        [TestMethod]
        public async Task CsvInput_SongDownload_RemoveFromSource_PreservesHeader()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var testClient = new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-csv-song-rfs-" + Guid.NewGuid());
            var csvPath   = Path.GetTempFileName() + ".csv";
            Directory.CreateDirectory(outputDir);
            // testartist - testsong is in CreateTestIndex (user3)
            File.WriteAllText(csvPath, "artist,title\ntestartist,testsong\n");

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = csvPath;
                rootSettings.Extraction.RemoveTracksFromSource = true;
                rootSettings.Output.ParentDir = outputDir;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(new ExtractJob(csvPath, InputType.None), rootSettings);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                var lines = File.ReadAllLines(csvPath);
                Assert.IsTrue(lines.Length > 0, "CSV file should not be empty after removal");
                Assert.IsTrue(lines[0].Contains("artist") || lines[0].Contains("title"),
                    $"Header was erased by list-level cleanup. First line: '{lines[0]}'");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        // Bug: DownloadEngine only calls RemoveTrackFromSource for SongJobs inside a JobList's
        // directSongs, never for AlbumJobs. An album row in the CSV is never cleared after
        // a successful album download.
        [TestMethod]
        public async Task CsvInput_AlbumDownload_RemoveFromSource_ClearsAlbumRow()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var testClient = new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-csv-album-rfs-" + Guid.NewGuid());
            var csvPath   = Path.GetTempFileName() + ".csv";
            Directory.CreateDirectory(outputDir);
            // testartist / testalbum is in CreateTestIndex (user3, folder "(2011) testalbum [MP3]")
            File.WriteAllText(csvPath, "artist,title,album\ntestartist,,testalbum\n");

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = csvPath;
                rootSettings.Extraction.RemoveTracksFromSource = true;
                rootSettings.Output.ParentDir = outputDir;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(new ExtractJob(csvPath, InputType.None), rootSettings);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                var lines = File.ReadAllLines(csvPath);
                Assert.IsTrue(lines.Length >= 2, "CSV should still have at least 2 lines (header + data)");
                Assert.AreEqual("artist,title,album", lines[0], "Header must be preserved");
                Assert.AreEqual(",,", lines[1],
                    $"Album row should be cleared after successful download, but got: '{lines[1]}'");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        // Bug: the fan-out branch only called MaybeRemoveFromSource on JobState.Done, not AlreadyExists.
        // Songs that are skipped because they already exist on disk were never removed from the CSV.
        [TestMethod]
        public async Task CsvInput_SongAlreadyExists_RemoveFromSource_ClearsSongRow()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-csv-song-ae-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-csv-song-ae-out-" + Guid.NewGuid());
            var csvPath   = Path.GetTempFileName() + ".csv";
            Directory.CreateDirectory(musicRoot);
            Directory.CreateDirectory(outputDir);
            File.WriteAllBytes(Path.Combine(musicRoot, "Artist - Track.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllText(csvPath, "artist,title\nArtist,Track\n");

            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);

            try
            {
                var eng = new EngineSettings { Username = "test_user", Password = "test_pass" };

                // Run 1: download so the file lands in outputDir.
                var dl1 = new DownloadSettings();
                dl1.Extraction.Input = csvPath;
                dl1.Output.ParentDir = outputDir;
                var cm1 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app1 = new DownloadEngine(eng, cm1);
                app1.Enqueue(new ExtractJob(csvPath, InputType.None), dl1);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                // Run 2: file exists → AlreadyExists; with RemoveFromSource the CSV row must be cleared.
                var dl2 = new DownloadSettings();
                dl2.Extraction.Input = csvPath;
                dl2.Extraction.RemoveTracksFromSource = true;
                dl2.Output.ParentDir = outputDir;
                dl2.Skip.SkipExisting = true;
                dl2.Skip.SkipMode = SkipMode.Name;
                var cm2 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app2 = new DownloadEngine(eng, cm2);
                app2.Enqueue(new ExtractJob(csvPath, InputType.None), dl2);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var lines = File.ReadAllLines(csvPath);
                Assert.AreEqual("artist,title", lines[0], "Header must be preserved");
                Assert.AreEqual(",", lines[1],
                    $"Song row should be cleared when AlreadyExists, but got: '{lines[1]}'");
            }
            finally
            {
                if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        // Regression guard: albums with AlreadyExists should also be cleared (this already works via
        // the else-branch / post-ProcessJob path, but keep it tested).
        [TestMethod]
        public async Task CsvInput_AlbumAlreadyExists_RemoveFromSource_ClearsAlbumRow()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-csv-album-ae-music-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "TestArtist", "TestAlbum");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-csv-album-ae-out-" + Guid.NewGuid());
            var csvPath   = Path.GetTempFileName() + ".csv";
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(outputDir);
            File.WriteAllBytes(Path.Combine(albumDir, "01. TestArtist - Track1.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(albumDir, "02. TestArtist - Track2.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllText(csvPath, "artist,title,album\nTestArtist,,TestAlbum\n");

            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);

            try
            {
                var eng = new EngineSettings { Username = "test_user", Password = "test_pass" };

                // Run 1: download so the album folder lands in outputDir.
                var dl1 = new DownloadSettings();
                dl1.Extraction.Input = csvPath;
                dl1.Output.ParentDir = outputDir;
                var cm1 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app1 = new DownloadEngine(eng, cm1);
                app1.Enqueue(new ExtractJob(csvPath, InputType.None), dl1);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                // Run 2: album folder exists → AlreadyExists; CSV row must be cleared.
                var dl2 = new DownloadSettings();
                dl2.Extraction.Input = csvPath;
                dl2.Extraction.RemoveTracksFromSource = true;
                dl2.Output.ParentDir = outputDir;
                dl2.Skip.SkipExisting = true;
                var cm2 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app2 = new DownloadEngine(eng, cm2);
                app2.Enqueue(new ExtractJob(csvPath, InputType.None), dl2);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var lines = File.ReadAllLines(csvPath);
                Assert.AreEqual("artist,title,album", lines[0], "Header must be preserved");
                Assert.AreEqual(",,", lines[1],
                    $"Album row should be cleared when AlreadyExists, but got: '{lines[1]}'");
            }
            finally
            {
                if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_FailsWhenNoAlbumsAreFound()
        {
            var testClient = new ClientTests.MockSoulseekClient([]);
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-empty-album-aggregate-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Search.MinSharesAggregate = 1;

                var aggregateJob = new AlbumAggregateJob(new AlbumQuery { Artist = "Missing Artist" });
                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);

                app.Enqueue(aggregateJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobState.Failed, aggregateJob.State);
                Assert.AreEqual(FailureReason.NoSuitableFileFound, aggregateJob.FailureReason);
                Assert.AreEqual(0, aggregateJob.Albums.Count);
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

                var songSearchesStarted = 0;
                var downloadsStarted = 0;
                app.Events.JobStateChanged += (job, state) =>
                {
                    if (job is SongJob && state == JobState.Searching)
                        songSearchesStarted++;
                };
                app.Events.DownloadStarted += (_, _) => downloadsStarted++;
                app.Enqueue(aggregateJob, rootSettings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.IsTrue(aggregateJob.Songs.Count > 0, "Aggregate should produce resolved song candidates.");
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
