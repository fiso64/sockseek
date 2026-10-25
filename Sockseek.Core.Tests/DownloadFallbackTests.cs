using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;
using Directory = System.IO.Directory;

namespace Tests.Core
{
    [TestClass]
    public class DownloadFallbackTests
    {
        [TestMethod]
        public async Task SongJob_FallsBackToNextCandidate_OnDownloadFailure()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-fallback-song-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file1 = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Artist - Song.mp3", length: 180);

            // failuser will throw a simulated download failure
            var resp1 = new SearchResponse("failuser", 1, true, 10000000, 0, [file1]);
            var resp2 = new SearchResponse("gooduser", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist - Song";
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, songJob.TerminalOutcome);
                Assert.AreEqual("gooduser", songJob.ChosenCandidate?.Username, "SongJob should have fallen back to gooduser after failuser failed.");
                Assert.AreEqual(SongDownloadSource.Soulseek, songJob.DownloadSource);
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SongJob_WithYtDlpEnabled_FallsBackToYtDlp_WhenMockFilesSearchHasNoResults()
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "Sockseek-ytdlp-fallback-" + Guid.NewGuid());
            var mockFilesDir = Path.Combine(rootDir, "mock-files");
            var outputDir = Path.Combine(rootDir, "downloads");
            Directory.CreateDirectory(mockFilesDir);
            Directory.CreateDirectory(outputDir);

            try
            {
                var eng = new EngineSettings
                {
                    MockFilesDir = mockFilesDir,
                    MockFilesReadTags = false,
                };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.YtDlp.UseYtdlp = true;

                SockseekLog.RemoveNonFileOutputs();
                var logEntries = new List<SockseekLog.StructuredLogEntry>();
                SockseekLog.AddStructuredSink((entry, _) => logEntries.Add(entry), LogLevel.Information);

                var fakeFallback = new FakeSongDownloadFallback();
                var song = new SongJob(new SongQuery
                {
                    Artist = "Lavish Life",
                    Title = "TEXAS HOLD 'EM",
                });
                var app = new DownloadEngine(eng, new SoulseekClientManager(eng), songDownloadFallback: fakeFallback);
                app.Enqueue(song, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(1, fakeFallback.Calls, "Song fallback should run after the mock Soulseek search returns no results.");
                Assert.AreEqual(JobActivityPhase.RunningFallback, fakeFallback.ObservedPhase, "Song fallback should run under the fallback activity phase.");
                var fallbackLog = logEntries.SingleOrDefault(e => e.Message == $"[{song.DisplayId}] SongJob: running fallback: {song}");
                Assert.IsNotNull(fallbackLog, "Fallback should leave an info-level log entry.");
                Assert.AreEqual(LogLevel.Information, fallbackLog.Level);
                Assert.AreEqual(SockseekLog.Categories.Jobs, fallbackLog.CategoryName);
                Assert.AreEqual(JobLifecycleState.Terminal, song.LifecycleState);
                Assert.AreEqual(JobActivityPhase.None, song.ActivityPhase);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
                Assert.AreEqual(SongDownloadSource.Fallback, song.DownloadSource);
                Assert.IsNull(song.ChosenCandidate);
                Assert.IsTrue(System.IO.File.Exists(song.DownloadPath), $"Expected fallback output at {song.DownloadPath}");
            }
            finally
            {
                SockseekLog.RemoveNonFileOutputs();
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        [TestMethod]
        public async Task SongJob_WithYtDlpFallbackAndIndex_WritesReturnedDownloadPathToIndex()
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "Sockseek-ytdlp-fallback-index-" + Guid.NewGuid());
            var mockFilesDir = Path.Combine(rootDir, "mock-files");
            var outputDir = Path.Combine(rootDir, "downloads");
            var ytDlpDir = Path.Combine(rootDir, "yt-dlp-output");
            Directory.CreateDirectory(mockFilesDir);
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(ytDlpDir);

            try
            {
                var returnedPath = Path.Combine(ytDlpDir, "actual path returned by yt-dlp.opus");
                var eng = new EngineSettings
                {
                    MockFilesDir = mockFilesDir,
                    MockFilesReadTags = false,
                };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.YtDlp.UseYtdlp = true;

                var fakeFallback = new FakeSongDownloadFallback(returnedPath);
                var song = new SongJob(new SongQuery
                {
                    Artist = "Lavish Life",
                    Title = "TEXAS HOLD 'EM",
                });
                var app = new DownloadEngine(eng, new SoulseekClientManager(eng), songDownloadFallback: fakeFallback);
                app.Enqueue(song, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
                Assert.AreEqual(returnedPath, song.DownloadPath);

                var lookupSong = new SongJob(new SongQuery
                {
                    Artist = song.Query.Artist,
                    Title = song.Query.Title,
                });
                var queue = new JobList();
                queue.Add(lookupSong);
                var index = new M3uEditor(dl.Output.IndexFilePath, queue, M3uOption.Index, true);

                Assert.IsTrue(index.TryGetPreviousRunResult(lookupSong, out var previous));
                Assert.IsNotNull(previous);
                Assert.AreEqual(Utils.NormalizedPath(returnedPath), Utils.NormalizedPath(previous.DownloadPath));
                Assert.AreEqual(JobStateOld.Done, previous.State);
            }
            finally
            {
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        [TestMethod]
        public async Task SongJob_DisconnectDuringDownload_RetriesAfterReconnect()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-disconnect-retry-song-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180);
            var response = new SearchResponse("flakyuser", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);
            testClient.FailNextDownloadWithDisconnect("flakyuser");

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist - Song";
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, songJob.TerminalOutcome);
                Assert.AreEqual("flakyuser", songJob.ChosenCandidate?.Username);
                Assert.IsTrue(testClient.DownloadCallCount >= 2, "Disconnect retry should attempt the same candidate again after reconnect.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_DisconnectDuringSearch_RetriesAfterReconnect()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-disconnect-retry-album-search-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", length: 180);
            var response = new SearchResponse("flakyuser", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);
            testClient.FailNextSearchWithDisconnect();

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.PrintOption = PrintOption.Results;

                var albumJob = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(albumJob, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, albumJob.TerminalOutcome);
                Assert.IsTrue(testClient.SearchCallCount >= 2, "Search should be retried after reconnect instead of becoming a terminal domain failure.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task DuplicateDownloadCache_UsesOrganizedPathAfterNameFormatMove()
        {
            var listPath = Path.GetTempFileName();
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-duplicate-cache-organized-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
            System.IO.File.WriteAllLines(listPath, ["\"Artist - Song\"", "\"Artist - Song\""]);

            var file = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180);
            var response = new SearchResponse("user1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p", ConcurrentJobs = 1 };
                var dl = new DownloadSettings();
                dl.Extraction.Input = listPath;
                dl.Extraction.InputType = InputType.List;
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "{artist}/{title}";
                dl.Skip.SkipExisting = false;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(listPath, InputType.List), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songs = app.Queue.AllSongs().ToList();
                Assert.AreEqual(2, songs.Count);
                Assert.IsTrue(songs.All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
                Assert.AreEqual(1, testClient.DownloadCallCount, "Second duplicate should copy/reuse the first final organized path, not redownload.");
                Assert.IsTrue(System.IO.File.Exists(songs[0].DownloadPath), "First song should point at the organized file.");
                Assert.IsTrue(System.IO.File.Exists(songs[1].DownloadPath), "Second song should copy from the organized cache path.");
            }
            finally
            {
                if (System.IO.File.Exists(listPath)) System.IO.File.Delete(listPath);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task DuplicateDownloads_WithConcurrentUniqueNameFormat_ProduceEveryOutput()
        {
            var listPath = Path.GetTempFileName();
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-duplicate-concurrent-unique-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
            System.IO.File.WriteAllLines(listPath, Enumerable.Repeat("\"artist=Artist, title=Song\"", 8));

            var file = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", size: 10_000, length: 180);
            var response = new SearchResponse("user1", 1, true, 100, 0, [file]);
            var downloadGate = new TestHelpers.DownloadGate();
            var testClient = new ClientTests.MockSoulseekClient([response])
            {
                BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
            };

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p", ConcurrentJobs = 4 };
                var dl = new DownloadSettings();
                dl.Extraction.Input = listPath;
                dl.Extraction.InputType = InputType.List;
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "{snum} - {stitle}";
                dl.Skip.SkipExisting = false;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(listPath, InputType.List), dl);
                app.CompleteEnqueue();

                var runTask = app.RunAsync(CancellationToken.None);
                await downloadGate.WaitForStartedCountAsync(1);
                using var changes = new TestSignal().Subscribe<Job>(
                    handler => app.Events.JobStateChanged += handler, handler => app.Events.JobStateChanged -= handler);
                await changes.WaitUntilAsync(() => app.Queue.AllSongs()
                    .Count(song => song.ActivityPhase == JobActivityPhase.Downloading) == eng.ConcurrentJobs,
                    "Duplicate requests must overlap while the first transfer is blocked.");
                downloadGate.Open();
                await runTask.WaitAsync(TimeSpan.FromSeconds(5));

                var songs = app.Queue.AllSongs().OrderBy(song => song.ItemNumber).ToList();
                Assert.AreEqual(8, songs.Count);
                var outcomeSummary = string.Join(", ", songs.Select(song => $"{song.ItemNumber}:{song.TerminalOutcome}/{song.FailureReason}:{song.FailureMessage}"));
                Assert.IsTrue(
                    songs.All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded),
                    $"Every duplicate row should either download or reuse successfully. Outcomes: {outcomeSummary}");

                for (var i = 1; i <= 8; i++)
                {
                    var path = Path.Combine(outputDir, $"{i} - Song.mp3");
                    Assert.IsTrue(System.IO.File.Exists(path), $"Expected output file missing: {path}");
                    Assert.AreEqual(file.Size, new FileInfo(path).Length, $"Output file size mismatch: {path}");
                }
            }
            finally
            {
                if (System.IO.File.Exists(listPath)) System.IO.File.Delete(listPath);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task DuplicateDownloadCache_UsesAlbumOrganizedPathForNonAudioFiles()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-duplicate-cache-album-art-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var audio = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", size: 18000, length: 180);
            var cover = TestHelpers.CreateSlFile(@"Music\Album\cover.jpg", size: 4096);
            var response = new SearchResponse("user1", 1, true, 100, 0, [audio, cover]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p", ConcurrentJobs = 1 };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "{artist}/{album}/{title}";
                dl.Output.AlbumArtOption = AlbumArtOption.Most;
                dl.Skip.SkipExisting = false;

                var firstAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var secondAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(firstAlbum, dl);
                app.Enqueue(secondAlbum, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, firstAlbum.TerminalOutcome);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, secondAlbum.TerminalOutcome);
                Assert.AreEqual(2, testClient.DownloadCallCount, "Second album should reuse both the audio and cover from their final organized paths.");
                Assert.IsTrue(firstAlbum.TrackJobs.Any(file => file.IsNotAudio && System.IO.File.Exists(file.DownloadPath)));
                Assert.IsTrue(secondAlbum.TrackJobs.Any(file => file.IsNotAudio && System.IO.File.Exists(file.DownloadPath)));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumNameFormat_FinalizesAudioAndAllAncillaryFiles()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-album-generic-files-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var audio = TestHelpers.CreateSlFile(@"Music\Artist\Album\01. Artist - Song.flac", size: 18_000, length: 180);
            var cover = TestHelpers.CreateSlFile(@"Music\Artist\Album\cover.jpg", size: 4_096);
            var booklet = TestHelpers.CreateSlFile(@"Music\Artist\Album\booklet.pdf", size: 8_192);
            var log = TestHelpers.CreateSlFile(@"Music\Artist\Album\rip.log", size: 1_024);
            var response = new SearchResponse("user1", 1, true, 100, 0, [audio, cover, booklet, log]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "Organized/{foldername}/{filename}";
                dl.Search.NoBrowseFolder = true;
                dl.Skip.SkipExisting = false;

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                Assert.AreEqual(4, album.TrackJobs.Count);
                Assert.IsTrue(album.TrackJobs.All(file => file.TerminalOutcome == JobTerminalOutcome.Succeeded));
                foreach (var filename in new[] { "01. Artist - Song.flac", "cover.jpg", "booklet.pdf", "rip.log" })
                    Assert.IsTrue(System.IO.File.Exists(Path.Combine(outputDir, "Organized", "Album", filename)), $"Missing finalized album file '{filename}'.");

                Assert.IsTrue(album.TrackJobs.All(file =>
                    !string.IsNullOrEmpty(file.DownloadPath)
                    && !Utils.IsInDirectory(file.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true)));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAncillaryOrganizationFailure_FailsAlbumAndRetainsStagedPayload()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-album-ancillary-organization-failure-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var audio = TestHelpers.CreateSlFile(@"Music\Artist\Album\01. Artist - Song.flac", size: 18_000, length: 180);
            var booklet = TestHelpers.CreateSlFile(@"Music\Artist\Album\booklet.pdf", size: 8_192);
            var response = new SearchResponse("user1", 1, true, 100, 0, [audio, booklet]);
            var testClient = new ClientTests.MockSoulseekClient([response]);
            var blockedBookletPath = Path.Combine(outputDir, "Organized", "Album", "booklet.pdf");
            Directory.CreateDirectory(blockedBookletPath);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "Organized/{foldername}/{filename}";
                dl.Search.NoBrowseFolder = true;
                dl.Skip.SkipExisting = false;

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Failed, album.TerminalOutcome);
                StringAssert.Contains(album.FailureMessage ?? "", "Failed to move album ancillary file");
                StringAssert.Contains(album.FailureMessage ?? "", "Downloaded payload retained at:");
                var pdf = album.TrackJobs.Single(file => file.IsNotAudio);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, pdf.TerminalOutcome);
                Assert.IsNotNull(pdf.DownloadPath);
                Assert.IsTrue(Utils.IsInDirectory(pdf.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true));
                Assert.IsTrue(System.IO.File.Exists(pdf.DownloadPath));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumArtOnly_IgnoresAudioConditionsWithoutChangingSettings()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-album-art-only-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var audio = TestHelpers.CreateSlFile(@"Music\Artist\Album\01. Artist - Song.mp3", size: 18000, length: 180);
            var cover = TestHelpers.CreateSlFile(@"Music\Artist\Album\cover.jpg", size: 4096);
            var response = new SearchResponse("user1", 1, true, 100, 0, [audio, cover]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p", ConcurrentJobs = 1 };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.AlbumArtOnly = true;
                dl.Search.NecessaryCond = new FileConditions
                {
                    Formats = ["flac"], MinBitrate = 1000, MinSampleRate = 96000, MinBitDepth = 24,
                    AcceptMissingProps = false, AcceptNoLength = false, LengthTolerance = 0,
                    StrictArtist = true, StrictAlbum = true,
                };
                dl.Search.NecessaryFolderCond = new FolderConditions
                {
                    MinTrackCount = 50, RequiredTrackTitles = ["Missing track"],
                };
                dl.Search.StrictAlbumQuality = true;
                dl.Skip.SkipExisting = false;

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                CollectionAssert.AreEqual(new[] { "flac" }, album.Config.Search.NecessaryCond.Formats);
                Assert.AreEqual(1000, album.Config.Search.NecessaryCond.MinBitrate);
                Assert.AreEqual(50, album.Config.Search.NecessaryFolderCond.MinTrackCount);
                Assert.AreEqual(AlbumArtOption.Default, album.Config.Output.AlbumArtOption);
                Assert.AreEqual(1, testClient.DownloadCallCount, "Album-art-only should download the image, not the audio track.");
                var image = album.TrackJobs.Single(file => file.IsNotAudio);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, image.TerminalOutcome);
                Assert.IsTrue(System.IO.File.Exists(image.DownloadPath), $"Expected downloaded image at {image.DownloadPath}");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumArtOnly_WithNameFormat_FinalizesImageOutsideStaging()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-album-art-only-name-format-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var audio = TestHelpers.CreateSlFile(@"Music\Artist\Album\01. Artist - Song.mp3", size: 18_000, length: 180);
            var cover = TestHelpers.CreateSlFile(@"Music\Artist\Album\cover.jpg", size: 4_096);
            var response = new SearchResponse("user1", 1, true, 100, 0, [audio, cover]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "IgnoredForAncillary/{filename}";
                dl.Output.AlbumArtOnly = true;
                dl.Output.AlbumArtOption = AlbumArtOption.Largest;
                dl.Skip.SkipExisting = false;

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                var image = album.TrackJobs.Single(file => file.IsNotAudio);
                Assert.AreEqual(Path.GetFullPath(outputDir), Path.GetFullPath(album.DownloadPath!));
                Assert.AreEqual(Path.Combine(outputDir, "cover.jpg"), image.DownloadPath);
                Assert.IsTrue(System.IO.File.Exists(image.DownloadPath));
                Assert.IsFalse(Utils.IsInDirectory(image.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_SucceedsWhenOptionalAlbumArtDownloadFails()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-album-art-optional-fail-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var audio = TestHelpers.CreateSlFile(@"Music\Artist\Album\01. Artist - Song.mp3", size: 18000, length: 180);
            var cover = TestHelpers.CreateSlFile(@"Music\Artist\Album\cover.jpg", size: 4096);
            var response = new SearchResponse("user1", 1, true, 100, 0, [audio, cover]);
            var testClient = new ClientTests.MockSoulseekClient([response])
            {
                BeforeDownloadCompletesAsync = (_, remoteFilename, _) =>
                    Utils.IsImageFile(remoteFilename)
                        ? throw new SoulseekClientException("simulated cover failure")
                        : Task.CompletedTask,
            };

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p", ConcurrentJobs = 1 };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.AlbumArtOption = AlbumArtOption.Largest;
                dl.Skip.SkipExisting = false;

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                Assert.IsTrue(System.IO.File.Exists(Path.Combine(outputDir, "Album", "01. Artist - Song.mp3")));

                var image = album.TrackJobs.SingleOrDefault(file => file.IsNotAudio);
                Assert.IsNotNull(image);
                Assert.AreEqual(JobTerminalOutcome.Failed, image.TerminalOutcome);
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_FallsBackToNextFolder_OnDownloadFailure()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-fallback-album-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file1 = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Album\01. Artist - Song.mp3", length: 180);

            var resp1 = new SearchResponse("failuser", 1, true, 10000000, 0, [file1]);
            var resp2 = new SearchResponse("gooduser", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var albumJob = app.Queue.AllJobs().OfType<AlbumJob>().FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, albumJob.TerminalOutcome);
                Assert.AreEqual("gooduser", albumJob.ResolvedTarget?.Username, "AlbumJob should have fallen back to gooduser's folder after failuser failed.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AggregateJob_FallsBackToNextCandidate_OnDownloadFailure()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-fallback-agg-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file1 = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Artist - Song.mp3", length: 180);

            var resp1 = new SearchResponse("failuser", 1, true, 10000000, 0, [file1]);
            var resp2 = new SearchResponse("gooduser", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist - Song";
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var aggJob = app.Queue.AllJobs().OfType<AggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob);
                var song = aggJob.Songs.FirstOrDefault();
                Assert.IsNotNull(song);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
                Assert.AreEqual("gooduser", song.ChosenCandidate?.Username, "Aggregate song bucket should have fallen back to gooduser.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SongJob_RespectsMaxDownloadRetries()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-fallback-song-max-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file1 = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Artist - Song.mp3", length: 180);

            var resp1 = new SearchResponse("failuser", 1, true, 10000000, 0, [file1]);
            var resp2 = new SearchResponse("gooduser", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist - Song";
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;
                dl.Transfer.MaxDownloadRetries = 1; // Limit to 1 attempt

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                string? attemptException = null;
                app.Events.DownloadAttemptFailed += (_, _, _, _, _, ex) =>
                {
                    attemptException = SockseekLog.ExceptionDetail(ex);
                };
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.IsTrue(songJob.IsUnsuccessfulTerminal, "SongJob should fail since MaxDownloadRetries was 1 and the first candidate failed.");
                StringAssert.Contains(attemptException, nameof(SoulseekClientException));
                Assert.IsNull(songJob.FailureDetail, "The attempt event carries known download exception detail, so terminal state should not duplicate it as diagnostic detail.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SongJob_FailsWhenFinalRenameCannotReplaceBlockedPath()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-final-rename-blocked-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", size: 10_000, length: 180);
            var response = new SearchResponse("user1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);
            var finalPath = Path.Combine(outputDir, "Artist - Song.mp3");
            var incompletePath = finalPath + ".incomplete";
            Directory.CreateDirectory(finalPath);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist - Song";
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;
                dl.Transfer.MaxDownloadRetries = 1;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.IsTrue(songJob.IsUnsuccessfulTerminal, "A failed final rename must not be reported as a successful download.");
                Assert.AreEqual(JobFailureReason.AllDownloadsFailed, songJob.FailureReason);
                Assert.IsTrue(Directory.Exists(finalPath), "The blocked destination directory should be left untouched.");
                Assert.IsFalse(System.IO.File.Exists(incompletePath), "The incomplete file should be cleaned up after a failed final rename.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_RespectsMaxDownloadRetries()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-fallback-album-max-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file1 = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Album\01. Artist - Song.mp3", length: 180);

            var resp1 = new SearchResponse("failuser", 1, true, 10000000, 0, [file1]);
            var resp2 = new SearchResponse("gooduser", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;
                dl.Transfer.MaxDownloadRetries = 1; // Limit to 1 attempt

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                var albumStatuses = new List<string>();
                app.Events.JobStatus += (job, status) =>
                {
                    if (job is AlbumJob)
                        albumStatuses.Add(status);
                };
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var albumJob = app.Queue.AllJobs().OfType<AlbumJob>().FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.IsTrue(albumJob.IsUnsuccessfulTerminal, "AlbumJob should fail since MaxDownloadRetries was 1 and the first folder failed.");
                Assert.IsFalse(
                    albumStatuses.Any(status => status.StartsWith("moving to ", StringComparison.Ordinal)
                        || status.StartsWith("moved to ", StringComparison.Ordinal)
                        || status is "deleting files" or "deleted files"),
                    "Failed-album move/delete actions should not run when every file in the failed folder is incomplete or absent.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_SingleFailedFolder_WithRemainingRetryBudgetReportsAllDownloadsFailed()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-fallback-album-single-failed-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", length: 180);
            var response = new SearchResponse("failuser", 1, true, 10000000, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var albumJob = app.Queue.AllJobs().OfType<AlbumJob>().FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.IsTrue(albumJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.AllDownloadsFailed, albumJob.FailureReason);
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SongJob_FailsWhenNameFormatOrganizationCannotReplaceBlockedPath()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-name-format-blocked-song-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Music\Mock Artist - Standalone Song.mp3", size: 10_000, length: 180);
            var response = new SearchResponse("user1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);
            var finalPath = Path.Combine(outputDir, "Standalone Song.mp3");
            Directory.CreateDirectory(finalPath);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "{stitle}";
                dl.Skip.SkipExisting = false;
                dl.Transfer.MaxDownloadRetries = 1;

                var song = new SongJob(new SongQuery { Artist = "Mock Artist", Title = "Standalone Song" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(song, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.IsTrue(song.IsUnsuccessfulTerminal, "A song whose name-formatted final placement fails must not be reported as successful.");
                Assert.AreEqual(JobFailureReason.Other, song.FailureReason);
                StringAssert.Contains(song.FailureMessage ?? "", "Failed to move organized file");
                StringAssert.Contains(song.FailureMessage ?? "", "Downloaded payload retained at:");
                Assert.IsTrue(Directory.Exists(finalPath), "The blocked destination directory should be left untouched.");
                Assert.IsNotNull(song.DownloadPath);
                Assert.IsTrue(System.IO.File.Exists(song.DownloadPath), "A fully downloaded payload should survive organization failure.");
                Assert.IsTrue(
                    Utils.IsInDirectory(song.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true),
                    $"The recovery payload should remain in staging, but was '{song.DownloadPath}'.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public void OutputFinalizer_FailsIfOrganizerLeavesOwnedFileInsideStaging()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-name-format-staging-invariant-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "";
                var candidate = new FileCandidate(
                    new SearchResponse("user1", 1, true, 100, 0, []),
                    TestHelpers.CreateSlFile(@"Music\Artist - Payload.xyz", size: 10_000));
                var stagingPath = Path.Combine(outputDir, ".sockseek-staging", Guid.NewGuid().ToString("N"), "Artist - Payload.xyz");
                Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
                System.IO.File.WriteAllText(stagingPath, "downloaded payload");
                var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Payload" })
                {
                    Config = dl,
                    ResolvedTarget = candidate,
                    DownloadPath = stagingPath,
                };
                var organizer = new FileManager(song, dl.Output, dl.Extraction);
                var finalizer = new OutputFinalizer(new DownloadedFileCache());

                var result = finalizer.FinalizeSongPlacement(
                    song,
                    song,
                    JobOutcome.Done(stagingPath, candidate),
                    organizer,
                    finalizePlacement: true);

                Assert.AreEqual(JobTerminalOutcome.Failed, result.Outcome.TerminalOutcome);
                StringAssert.Contains(result.Outcome.FailureMessage ?? "", "left the downloaded file in Sockseek staging");
                StringAssert.Contains(result.Outcome.FailureMessage ?? "", "Downloaded payload retained at:");
                Assert.AreEqual(stagingPath, song.DownloadPath);
                Assert.IsTrue(System.IO.File.Exists(stagingPath));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAudioTrack_IsNameFormattedBeforeWholeAlbumCompletes()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-album-progressive-organization-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var first = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - First.mp3", size: 10_000, length: 180);
            var second = TestHelpers.CreateSlFile(@"Music\Album\02. Artist - Second.mp3", size: 10_000, length: 181);
            var response = new SearchResponse("user1", 1, true, 100, 0, [first, second]);
            var secondTransferReachedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecondTransfer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int completionOrder = 0;
            var testClient = new ClientTests.MockSoulseekClient([response])
            {
                BeforeDownloadCompletesAsync = async (_, _, ct) =>
                {
                    if (Interlocked.Increment(ref completionOrder) != 2)
                        return;

                    secondTransferReachedCompletion.TrySetResult();
                    await releaseSecondTransfer.Task.WaitAsync(ct);
                },
            };

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "Organized/{filename}";
                dl.Search.NoBrowseFolder = true;
                dl.Skip.SkipExisting = false;

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                using var changes = new TestSignal().Subscribe<Job>(
                    handler => app.Events.JobStateChanged += handler, handler => app.Events.JobStateChanged -= handler);
                var runTask = app.RunAsync(CancellationToken.None);
                await secondTransferReachedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

                SongJob? organizedTrack = null;
                await changes.WaitUntilAsync(() =>
                {
                    organizedTrack = album.TrackJobs.FirstOrDefault(track =>
                        track.TerminalOutcome == JobTerminalOutcome.Succeeded
                        && !string.IsNullOrEmpty(track.DownloadPath)
                        && !Utils.IsInDirectory(track.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true));
                    return organizedTrack != null;
                }, "The first audio track was not organized while the second transfer was blocked.");

                Assert.IsNotNull(organizedTrack, "A completed audio child should be organized while a later album transfer is still blocked.");
                Assert.IsTrue(System.IO.File.Exists(organizedTrack.DownloadPath));
                Assert.IsFalse(runTask.IsCompleted, "The observation must happen before the album finishes.");
                Assert.AreNotEqual(JobLifecycleState.Terminal, album.LifecycleState);

                releaseSecondTransfer.TrySetResult();
                await runTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                Assert.IsTrue(album.TrackJobs.Where(track => !track.IsNotAudio).All(track =>
                    !string.IsNullOrEmpty(track.DownloadPath)
                    && System.IO.File.Exists(track.DownloadPath)
                    && !Utils.IsInDirectory(track.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true)));
            }
            finally
            {
                releaseSecondTransfer.TrySetResult();
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_FailsWhenTrackFinalRenameCannotReplaceBlockedPath()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-final-rename-blocked-album-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", size: 10_000, length: 180);
            var response = new SearchResponse("user1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);
            var finalPath = Path.Combine(outputDir, "Album", "01. Artist - Song.mp3");
            var incompletePath = finalPath + ".incomplete";
            Directory.CreateDirectory(finalPath);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;
                dl.Transfer.MaxDownloadRetries = 1;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                var albumStatuses = new List<string>();
                app.Events.JobStatus += (job, status) =>
                {
                    if (job is AlbumJob)
                        albumStatuses.Add(status);
                };
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var albumJob = app.Queue.AllJobs().OfType<AlbumJob>().FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.IsTrue(albumJob.IsUnsuccessfulTerminal, "An album with a track that cannot be finalized must not be reported as successful.");
                Assert.AreEqual(JobFailureReason.AllDownloadsFailed, albumJob.FailureReason);
                var failedTrack = albumJob.TrackJobs.FirstOrDefault();
                Assert.IsNotNull(failedTrack);
                Assert.IsTrue(failedTrack.IsUnsuccessfulTerminal, "The track whose final placement failed should be terminal unsuccessful.");
                Assert.IsTrue(Directory.Exists(finalPath), "The blocked destination directory should be left untouched.");
                Assert.IsFalse(System.IO.File.Exists(incompletePath), "The incomplete file should be cleaned up after a failed final rename.");
                Assert.IsFalse(
                    albumStatuses.Any(status => status.StartsWith("moving to ", StringComparison.Ordinal)
                        || status.StartsWith("moved to ", StringComparison.Ordinal)
                        || status is "deleting files" or "deleted files"),
                    "Failed-album actions should not run when no file reached a completed path.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_FallsBackToNextFolder_OnDownloadFailure()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-fallback-aggalbum-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file1 = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Album\01. Artist - Song.mp3", length: 180);

            var resp1 = new SearchResponse("failuser", 1, true, 10000000, 0, [file1]);
            var resp2 = new SearchResponse("gooduser", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var aggAlbumJob = app.Queue.AllJobs().OfType<AlbumAggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggAlbumJob);
                var albumJob = aggAlbumJob.Albums.FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, albumJob.TerminalOutcome);
                Assert.AreEqual("gooduser", albumJob.ResolvedTarget?.Username, "Aggregate album bucket should have fallen back to gooduser.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_FailsWhenGeneratedAlbumDownloadFails()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-failed-aggalbum-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", length: 180);
            var response = new SearchResponse("failuser", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;
                dl.Search.NoBrowseFolder = true;
                dl.Transfer.MaxDownloadRetries = 1;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var aggAlbumJob = app.Queue.AllJobs().OfType<AlbumAggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggAlbumJob);
                Assert.IsTrue(aggAlbumJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.AllDownloadsFailed, aggAlbumJob.FailureReason);
                Assert.IsTrue(aggAlbumJob.Albums.Any(album => album.IsUnsuccessfulTerminal));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AggregateJob_MixedChildOutcomes_CompletesWithPartialSuccess()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-partial-aggregate-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var goodFile = TestHelpers.CreateSlFile(@"Music\Artist - Good.mp3", length: 180);
            var failingFile = TestHelpers.CreateSlFile(@"Music\Artist - Bad.mp3", length: 181);
            var goodResponse = new SearchResponse("gooduser", 1, true, 100, 0, [goodFile]);
            var failingResponse = new SearchResponse("failuser", 2, true, 100, 0, [failingFile]);
            var testClient = new ClientTests.MockSoulseekClient([goodResponse, failingResponse], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Search.MinSharesAggregate = 1;
                dl.Transfer.MaxDownloadRetries = 1;
                dl.Output.ParentDir = outputDir;

                var aggregate = new AggregateJob(new SongQuery { Artist = "Artist" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(aggregate, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.PartialSuccess, aggregate.TerminalOutcome);
                Assert.IsTrue(aggregate.Songs.Any(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
                Assert.IsTrue(aggregate.Songs.Any(song => song.TerminalOutcome == JobTerminalOutcome.Failed));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_MixedChildOutcomes_CompletesWithPartialSuccess()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-partial-albumaggregate-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var goodFile = TestHelpers.CreateSlFile(@"Music\Artist\Album One\01. Artist - Good.mp3", length: 180);
            var failingFile = TestHelpers.CreateSlFile(@"Music\Artist\Album Two\01. Artist - Bad.mp3", length: 181);
            var goodResponse = new SearchResponse("gooduser", 1, true, 100, 0, [goodFile]);
            var failingResponse = new SearchResponse("failuser", 2, true, 100, 0, [failingFile]);
            var testClient = new ClientTests.MockSoulseekClient([goodResponse, failingResponse], failingUsers: ["failuser"]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Search.MinSharesAggregate = 1;
                dl.Search.NoBrowseFolder = true;
                dl.Transfer.MaxDownloadRetries = 1;
                dl.Output.ParentDir = outputDir;

                var aggregate = new AlbumAggregateJob(new AlbumQuery { Artist = "Artist" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(aggregate, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.PartialSuccess, aggregate.TerminalOutcome);
                Assert.IsTrue(aggregate.Albums.Any(album => album.TerminalOutcome == JobTerminalOutcome.Succeeded));
                Assert.IsTrue(aggregate.Albums.Any(album => album.TerminalOutcome == JobTerminalOutcome.Failed));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task JobList_AllChildrenFail_UsesChildJobsFailedReason()
        {
            var eng = new EngineSettings { Username = "u", Password = "p" };
            var dl = new DownloadSettings();
            var list = new JobList("wishlist", new Job[]
            {
                new SongJob(new SongQuery { Artist = "Missing Artist", Title = "Missing One" }),
                new SongJob(new SongQuery { Artist = "Missing Artist", Title = "Missing Two" }),
            });
            var client = new ClientTests.MockSoulseekClient([]);
            var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(client, eng));

            app.Enqueue(list, dl);
            app.CompleteEnqueue();

            await app.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Failed, list.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.ChildJobsFailed, list.FailureReason);
            Assert.AreEqual("One or more child jobs failed.", list.FailureMessage);
        }

        private sealed class FakeSongDownloadFallback : ISongDownloadFallback
        {
            private readonly string? outputPath;

            public FakeSongDownloadFallback(string? outputPath = null)
            {
                this.outputPath = outputPath;
            }

            public int Calls { get; private set; }
            public JobActivityPhase ObservedPhase { get; private set; } = JobActivityPhase.None;

            public bool CanRun(SongJob song, DownloadSettings settings)
                => settings.YtDlp.UseYtdlp;

            public Task<JobOutcome?> TryDownloadAsync(
                SongJob song,
                DownloadSettings settings,
                FileManager organizer,
                IJobLog? log,
                CancellationToken ct)
            {
                Calls++;
                ObservedPhase = song.ActivityPhase;
                var path = outputPath ?? organizer.GetSavePathNoExt($"{song.Query.Artist} - {song.Query.Title}.mp3") + ".mp3";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllBytes(path, TestHelpers.EmptyMp3Bytes);
                return Task.FromResult<JobOutcome?>(JobOutcome.Done(path, downloadSource: SongDownloadSource.Fallback));
            }
        }
    }
}
