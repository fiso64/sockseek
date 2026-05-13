using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Tests.ClientTests;

namespace Tests.EndToEnd
{
    [TestClass]
    public class AggregateSkipTests
    {
        [TestMethod]
        public async Task AggregateDownload_SkipExisting_CorrectlySkipsAfterSearch()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            SldlLog.SetupExceptionHandling();
            SldlLog.AddConsole();
            SldlLog.SetConsoleLogLevel(LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-skip-agg-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-skip-agg-out-" + Guid.NewGuid());
            Directory.CreateDirectory(musicRoot);
            Directory.CreateDirectory(outputDir);

            try
            {
                // File that exists in "Soulseek" (mock) and in our local output dir
                string fileName = "Artist1 - Song1.mp3";
                string existingFilePath = Path.Combine(outputDir, fileName);
                File.WriteAllBytes(Path.Combine(musicRoot, fileName), TestHelpers.EmptyMp3Bytes);
                File.WriteAllBytes(existingFilePath, TestHelpers.EmptyMp3Bytes);

                var testClient = MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);

                var eng = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist1 - Song1";
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = false;
                dl.Output.WritePlaylist = false;
                dl.Skip.SkipExisting = true;
                dl.Skip.SkipMode = SkipMode.Name;
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, eng);
                var app = new DownloadEngine(eng, clientManager);
                app.Enqueue(new ExtractJob(dl.Extraction.Input!, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                // Check the job queue to see if the track was marked as already existing
                Assert.IsNotNull(app.Queue, "Queue should not be null");
                var aggregateJob = app.Queue.AllJobs().OfType<AggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggregateJob, "Should have found an AggregateJob");
                

                var song = aggregateJob.Songs.FirstOrDefault(s => s.Query.ToString(true).Contains("Artist1"));
                Assert.IsNotNull(song, "Should have found the song in the aggregate job results");
                
                // This is where it will fail if the regression is present:
                // It will be 'Downloaded' instead of 'AlreadyExists' because it wasn't skipped.
                // (Or it might fail during download if it tries to overwrite or something, 
                // but the goal is to see it skipped before it even tries to download).
                Assert.AreEqual(JobState.AlreadyExists, song.State, 
                    $"Song should have been skipped. Current state: {song.State}. Failure reason: {song.FailureReason}");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AggregateJob_SkipsExistingFilesOnRerun_IfAndOnlyIfWithinTolerance()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            // Response A: Length 180
            var responseA = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180)]);
            // Response B: Length 181 (within tol 3 of 180)
            var responseB = new Soulseek.SearchResponse("User2", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 181)]);
            // Response C: Length 185 (outside tol 3 of 180)
            var responseC = new Soulseek.SearchResponse("User3", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 185)]);

            var testClient1 = new ClientTests.MockSoulseekClient([responseA]);
            var testClient2 = new ClientTests.MockSoulseekClient([responseB]);
            var testClient3 = new ClientTests.MockSoulseekClient([responseC]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.HasConfiguredIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;
                dl.Search.AggregateLengthTol = 3;
                dl.Skip.SkipExisting = true;

                // Run 1: 180s
                var clientManager1 = TestHelpers.CreateMockClientManager(testClient1, eng);
                var app1 = new DownloadEngine(eng, clientManager1);
                app1.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var aggJob1 = app1.Queue.AllJobs().OfType<AggregateJob>().FirstOrDefault();
                Assert.AreEqual(JobState.Done, aggJob1!.Songs[0].State);
                int linesRun1 = File.ReadAllLines(dl.Output.IndexFilePath).Length;

                // Run 2: 181s (Within tolerance, should be skipped)
                var clientManager2 = TestHelpers.CreateMockClientManager(testClient2, eng);
                var app2 = new DownloadEngine(eng, clientManager2);
                app2.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var aggJob2 = app2.Queue.AllJobs().OfType<AggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob2);
                Assert.AreEqual(1, aggJob2.Songs.Count);
                Assert.AreEqual(JobState.AlreadyExists, aggJob2.Songs[0].State, "Aggregate job should skip existing files on rerun if within length tolerance.");
                int linesRun2 = File.ReadAllLines(dl.Output.IndexFilePath).Length;
                Assert.AreEqual(linesRun1, linesRun2, "Index file should not duplicate entries when skipping within tolerance.");

                // Run 3: 185s (Outside tolerance, should NOT be skipped)
                var clientManager3 = TestHelpers.CreateMockClientManager(testClient3, eng);
                var app3 = new DownloadEngine(eng, clientManager3);
                app3.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app3.CompleteEnqueue();
                await app3.RunAsync(CancellationToken.None);

                var aggJob3 = app3.Queue.AllJobs().OfType<AggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob3);
                Assert.AreEqual(1, aggJob3.Songs.Count);
                Assert.AreEqual(JobState.Done, aggJob3.Songs[0].State, "Aggregate job should NOT skip if the length is outside the aggregate tolerance.");
                int linesRun3 = File.ReadAllLines(dl.Output.IndexFilePath).Length;
                Assert.IsTrue(linesRun3 > linesRun2, "Index file should append new entry for track outside tolerance.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_MultipleAlbums_SkipsCorrectlyPerAlbumOnRerun()
        {
            // Regression: when an artist has multiple aggregate albums, all AlbumJobs were
            // created with the original empty-album query, giving every album the same index key.
            // The last album to write would overwrite the others; on rerun every album matched
            // that single path, causing wrong skips and re-downloads.
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            // Two albums with different track lengths so they form distinct aggregate buckets.
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Artist\Album A\01. Artist - Song.mp3", length: 100),
                TestHelpers.CreateSlFile(@"Artist\Album B\01. Artist - Song.mp3", length: 200),
            ]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Extraction.IsAlbum = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.HasConfiguredIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;
                dl.Skip.SkipExisting = true;

                // Run 1: download both albums.
                var app1 = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app1.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var aggJob1 = app1.Queue.AllJobs().OfType<AlbumAggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob1);
                Assert.AreEqual(2, aggJob1.Albums.Count, "Both albums should be found.");
                Assert.IsTrue(aggJob1.Albums.All(a => a.State == JobState.Done), "Both albums should download on first run.");

                // Run 2: both albums should be skipped.
                var app2 = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app2.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var aggJob2 = app2.Queue.AllJobs().OfType<AlbumAggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob2);
                Assert.AreEqual(2, aggJob2.Albums.Count, "Both albums should be found on second run.");
                Assert.IsTrue(aggJob2.Albums.All(a => a.State == JobState.AlreadyExists),
                    $"Both albums should be skipped on rerun. States: {string.Join(", ", aggJob2.Albums.Select(a => $"{a.ItemName}={a.State}"))}");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_SkipDoesNotCorruptIndexState()
        {
            // Regression: JobToIndexState had no case for AlreadyExists, so skipped albums
            // were written as Pending (state=0) to the index. The third run would then see
            // Pending and re-download the album instead of skipping it again.
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Album\01. Artist - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Extraction.IsAlbum = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.HasConfiguredIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;
                dl.Skip.SkipExisting = true;

                async Task RunOnce()
                {
                    var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                    app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                    app.CompleteEnqueue();
                    await app.RunAsync(CancellationToken.None);
                }

                await RunOnce(); // Run 1: download
                await RunOnce(); // Run 2: skip — must not corrupt index state

                // Run 3: must also skip (not re-download)
                var app3 = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app3.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app3.CompleteEnqueue();
                await app3.RunAsync(CancellationToken.None);

                var aggJob3 = app3.Queue.AllJobs().OfType<AlbumAggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob3);
                Assert.AreEqual(1, aggJob3.Albums.Count);
                Assert.AreEqual(JobState.AlreadyExists, aggJob3.Albums[0].State,
                    "Album skipped on run 2 must still be skipped on run 3 (index state must not be corrupted).");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumAggregateJob_SkipsExistingAlbumsOnRerun()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            // Direct mock response.
            var file = TestHelpers.CreateSlFile(@"Album\01. Artist - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Extraction.IsAlbum = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.HasConfiguredIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;
                dl.Skip.SkipExisting = true;

                var clientManager1 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app1 = new DownloadEngine(eng, clientManager1);
                app1.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var clientManager2 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app2 = new DownloadEngine(eng, clientManager2);
                app2.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var aggJob = app2.Queue.AllJobs().OfType<AlbumAggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob);
                Assert.AreEqual(1, aggJob.Albums.Count);
                Assert.AreEqual(JobState.AlreadyExists, aggJob.Albums[0].State, "AlbumAggregate job should skip existing albums on rerun.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task NormalJob_SkipsExistingFiles_RespectsLengthToleranceNotAggregateTol()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var responseA = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180)]);
            var responseB = new Soulseek.SearchResponse("User2", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 182)]);
            
            var testClient1 = new ClientTests.MockSoulseekClient([responseA]);
            var testClient2 = new ClientTests.MockSoulseekClient([responseB]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                
                // RUN 1: Download track with query length 180
                var dl1 = new DownloadSettings();
                dl1.Extraction.Input = "artist=Artist, title=Song, length=180";
                dl1.Output.ParentDir = outputDir;
                dl1.Output.WriteIndex = true;
                dl1.Output.HasConfiguredIndex = true;
                dl1.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl1.Search.IsAggregate = false;
                dl1.Skip.SkipExisting = true;

                var clientManager1 = TestHelpers.CreateMockClientManager(testClient1, eng);
                var app1 = new DownloadEngine(eng, clientManager1);
                app1.Enqueue(new ExtractJob(dl1.Extraction.Input, dl1.Extraction.InputType), dl1);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var songJob1 = app1.Queue.AllJobs().OfType<SongJob>().FirstOrDefault();
                Assert.AreEqual(JobState.Done, songJob1!.State);

                // RUN 2: Try to download with query length 182
                var dl2 = new DownloadSettings();
                dl2.Extraction.Input = "artist=Artist, title=Song, length=182";
                dl2.Output.ParentDir = outputDir;
                dl2.Output.WriteIndex = true;
                dl2.Output.HasConfiguredIndex = true;
                dl2.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                
                dl2.Search.IsAggregate = false;
                dl2.Search.NecessaryCond.LengthTolerance = 1; // Normal tolerance is tight (182 is outside)
                dl2.Search.AggregateLengthTol = 5;            // Aggregate tolerance is loose (182 is inside)
                dl2.Skip.SkipExisting = true;

                var clientManager2 = TestHelpers.CreateMockClientManager(testClient2, eng);
                var app2 = new DownloadEngine(eng, clientManager2);
                app2.Enqueue(new ExtractJob(dl2.Extraction.Input, dl2.Extraction.InputType), dl2);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var songJob2 = app2.Queue.AllJobs().OfType<SongJob>().FirstOrDefault();
                Assert.IsNotNull(songJob2);
                
                // It should download again (Done), NOT AlreadyExists, because normal tolerance (1) applies.
                Assert.AreEqual(JobState.Done, songJob2.State, "Normal job should NOT use AggregateLengthTol to skip.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }
    }
}
