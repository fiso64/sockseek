using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;
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
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, songJob.TerminalOutcome);
                Assert.AreEqual("gooduser", songJob.ChosenCandidate?.Username, "SongJob should have fallen back to gooduser after failuser failed.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
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
                Assert.IsTrue(firstAlbum.ResolvedTarget?.Files.Any(file => file.IsNotAudio && System.IO.File.Exists(file.DownloadPath)) == true);
                Assert.IsTrue(secondAlbum.ResolvedTarget?.Files.Any(file => file.IsNotAudio && System.IO.File.Exists(file.DownloadPath)) == true);
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
                dl.Output.ParentDir = outputDir;
                dl.Transfer.MaxDownloadRetries = 1; // Limit to 1 attempt

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.IsTrue(songJob.IsUnsuccessfulTerminal, "SongJob should fail since MaxDownloadRetries was 1 and the first candidate failed.");
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
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var albumJob = app.Queue.AllJobs().OfType<AlbumJob>().FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.IsTrue(albumJob.IsUnsuccessfulTerminal, "AlbumJob should fail since MaxDownloadRetries was 1 and the first folder failed.");
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
    }
}
