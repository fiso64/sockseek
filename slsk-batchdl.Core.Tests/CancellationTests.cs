using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Tests.Cancellation
{
    [TestClass]
    public class CancellationTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext _)
        {
            Logger.AddConsole(Logger.LogLevel.Fatal);
        }

        // Long enough for songs to be concurrently Searching when we cancel;
        // short enough for the tests to finish in under ~1 second.
        private const int SearchDelay = 300;

        private static (DownloadEngine engine, string outputDir) CreateEngine(
            ISoulseekClient client, string input, string[] extra = null!)
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            var eng = new EngineSettings { Username = "u", Password = "p" };
            var dl = new DownloadSettings();
            dl.Extraction.Input = input;
            dl.Output.ParentDir = outputDir;

            extra ??= Array.Empty<string>();
            var inputTypeIndex = Array.IndexOf(extra, "--input-type");
            if (inputTypeIndex >= 0 && inputTypeIndex + 1 < extra.Length)
                dl.Extraction.InputType = Enum.Parse<InputType>(extra[inputTypeIndex + 1], ignoreCase: true);

            var clientManager = TestHelpers.CreateMockClientManager(client, eng);
            var engine = new DownloadEngine(eng, clientManager);
            engine.Enqueue(new ExtractJob(dl.Extraction.Input!, dl.Extraction.InputType), dl);
            engine.CompleteEnqueue();
            return (engine, outputDir);
        }

        private static async Task WaitForAsync(Func<bool> cond, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!cond())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for condition");
                await Task.Delay(10);
            }
        }

        // Await a RunAsync task that may have been cancelled — swallow expected cancellation.
        private static async Task IgnoreCancellation(Task t)
        {
            try { await t; }
            catch (OperationCanceledException) { }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException)) { }
        }


        // ── Test 1: engine sets Cts on all jobs it processes ─────────────────
        // Verifies that Job.Cts is non-null after the engine processes each job.
        // Uses an empty client so the engine completes immediately (no real I/O).

        [TestMethod]
        public async Task Engine_SetsCts_OnAllProcessedJobs()
        {
            var client = new ClientTests.MockSoulseekClient(new List<SearchResponse>());
            var (engine, outputDir) = CreateEngine(client, "testartist - testsong");

            try
            {
                await engine.RunAsync(CancellationToken.None);

                var extractJob = (ExtractJob)engine.Queue.Jobs[0];
                Assert.IsNotNull(extractJob.Cts, "ExtractJob.Cts should be set by the engine");

                // StringExtractor returns a bare SongJob (not wrapped in a JobList).
                var songJob = (SongJob)extractJob.Result!;
                Assert.IsNotNull(songJob.Cts, "SongJob.Cts should be set by the engine");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }


        // ── Test 2: cancelling a JobList mid-search stops all children quickly ─
        // Cancels the parent JobList while both child songs are in the Searching state.
        // The songs' CTSes are linked to the list's CTS, so they should abort well
        // within the search delay window.

        [TestMethod]
        public async Task CancelJobList_MidSearch_StopsChildrenQuickly()
        {
            var listFile  = Path.GetTempFileName();
            var (engine, outputDir) = CreateEngine(
                new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex(), searchDelayMs: SearchDelay),
                listFile,
                new[] { "--input-type", "list" });

            try
            {
                System.IO.File.WriteAllLines(listFile, new[]
                {
                    "\"testartist - testsong\"",
                    "\"testartist - testsong2\"",
                });

                var runTask = engine.RunAsync(CancellationToken.None);

                var rootExtract = (ExtractJob)engine.Queue.Jobs[0];

                await WaitForAsync(() => rootExtract.State == JobState.Done);

                var rootList = (JobList)rootExtract.Result!;
                var songs    = rootList.AllSongs().ToList();
                Assert.AreEqual(2, songs.Count);

                // Wait until both songs are concurrently Searching.
                await WaitForAsync(() => songs.All(s => s.State == JobState.Searching));

                Assert.IsNotNull(rootList.Cts, "JobList.Cts must be set");
                Assert.IsTrue(songs.All(s => s.Cts != null), "All child SongJob.Cts must be set");

                // Cancel the list — both songs' CTSes are linked to it.
                rootList.Cancel();

                // RunAsync should abort well before the search delay expires.
                var completed = await Task.WhenAny(runTask, Task.Delay(SearchDelay / 2)) == runTask;
                await IgnoreCancellation(runTask);

                Assert.IsTrue(completed,
                    "RunAsync should have aborted quickly after cancelling the list, not waited the full search delay");
                Assert.IsTrue(songs.All(s => s.Cts!.IsCancellationRequested),
                    "All child song CTSes should be cancelled");
            }
            finally
            {
                System.IO.File.Delete(listFile);
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }


        // ── Test 3: cancelling one song does not cancel its sibling ───────────
        // Each song in a JobList has an independent CTS (linked to the parent list,
        // not to one another). Cancelling song1 must leave song2 to complete normally.

        [TestMethod]
        public async Task CancelOneSong_DoesNotAffect_Sibling()
        {
            var listFile  = Path.GetTempFileName();
            var (engine, outputDir) = CreateEngine(
                new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex(), searchDelayMs: SearchDelay),
                listFile,
                new[] { "--input-type", "list" });

            try
            {
                System.IO.File.WriteAllLines(listFile, new[]
                {
                    "\"testartist - testsong\"",
                    "\"testartist - testsong2\"",
                });

                var runTask = engine.RunAsync(CancellationToken.None);

                var rootExtract = (ExtractJob)engine.Queue.Jobs[0];
                await WaitForAsync(() => rootExtract.State == JobState.Done);

                var songs = ((JobList)rootExtract.Result!).AllSongs().ToList();
                Assert.AreEqual(2, songs.Count);

                // Wait until both songs are concurrently Searching.
                await WaitForAsync(() => songs.All(s => s.State == JobState.Searching));

                var song1 = songs[0];
                var song2 = songs[1];

                // Cancel only song1 — song2 must be unaffected.
                song1.Cancel();
                Assert.IsFalse(song2.Cts!.IsCancellationRequested,
                    "song2.Cts must NOT be cancelled when only song1 is cancelled");

                // Let the engine run to completion.
                await IgnoreCancellation(runTask);

                Assert.AreEqual(JobState.Failed, song1.State,
                    "song1 should be Failed after being cancelled mid-search");
                Assert.AreEqual(JobState.Done, song2.State,
                    "song2 should be Done — it was not cancelled and found a match");
            }
            finally
            {
                System.IO.File.Delete(listFile);
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }


        // ── Test 4: cancelling an ExtractJob does not cancel its Result ────────
        // The engine passes parentToken (not ej.Cts.Token) when recursing into Result,
        // so the Result's CTS is a sibling of the ExtractJob, not a child.
        // Cancelling the already-Done ExtractJob must leave the song's search running.

        [TestMethod]
        public async Task CancelExtractJob_DoesNotCancel_Result()
        {
            var (engine, outputDir) = CreateEngine(
                new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex(), searchDelayMs: SearchDelay),
                "testartist testsong");

            try
            {
                var runTask = engine.RunAsync(CancellationToken.None);

                var extractJob = (ExtractJob)engine.Queue.Jobs[0];

                // Extraction (StringExtractor) is synchronous — Done almost immediately.
                await WaitForAsync(() => extractJob.State == JobState.Done);
                var songJob = (SongJob)extractJob.Result!;

                // Wait for the song to enter Searching so its Cts is live.
                await WaitForAsync(() => songJob.State == JobState.Searching);

                // Cancel the already-Done ExtractJob.
                extractJob.Cancel();

                // The song's CTS is NOT linked to the ExtractJob's CTS.
                Assert.IsTrue(extractJob.Cts!.IsCancellationRequested,
                    "ExtractJob.Cts should be cancelled");
                Assert.IsFalse(songJob.Cts!.IsCancellationRequested,
                    "SongJob.Cts must NOT be cancelled — the ExtractJob is not its parent in the CTS tree");

                // Clean up.
                engine.Cancel();
                await IgnoreCancellation(runTask);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task GetJobsByWorkflow_ReturnsRegisteredWorkflowJobs()
        {
            var workflowId = Guid.NewGuid();
            var otherWorkflowId = Guid.NewGuid();
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-workflow-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;

                var clientManager = TestHelpers.CreateMockClientManager(
                    new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex()),
                    eng);
                var engine = new DownloadEngine(eng, clientManager);

                var first = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" }) { WorkflowId = workflowId };
                var second = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong2" }) { WorkflowId = workflowId };
                var third = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" }) { WorkflowId = otherWorkflowId };

                engine.Enqueue(first, dl);
                engine.Enqueue(second, dl);
                engine.Enqueue(third, dl);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                var workflowJobs = engine.GetJobsByWorkflow(workflowId);
                CollectionAssert.AreEqual(
                    new[] { first, second },
                    workflowJobs.ToArray(),
                    "GetJobsByWorkflow should return the registered jobs in display order for that workflow.");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task CancelWorkflow_CancelsOnlyMatchingWorkflowJobs()
        {
            var workflowId = Guid.NewGuid();
            var otherWorkflowId = Guid.NewGuid();
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-workflow-cancel-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;

                var clientManager = TestHelpers.CreateMockClientManager(
                    new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex(), searchDelayMs: SearchDelay),
                    eng);
                var engine = new DownloadEngine(eng, clientManager);

                var first = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" }) { WorkflowId = workflowId };
                var second = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong2" }) { WorkflowId = workflowId };
                var third = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" }) { WorkflowId = otherWorkflowId };

                engine.Enqueue(first, dl);
                engine.Enqueue(second, dl);
                engine.Enqueue(third, dl);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);

                await WaitForAsync(() => first.State == JobState.Searching
                    && second.State == JobState.Searching
                    && third.State == JobState.Searching);

                var cancelled = engine.CancelWorkflow(workflowId);

                Assert.AreEqual(2, cancelled, "CancelWorkflow should cancel the active jobs in the matching workflow.");
                Assert.IsTrue(first.Cts!.IsCancellationRequested, "First workflow job should be cancelled.");
                Assert.IsTrue(second.Cts!.IsCancellationRequested, "Second workflow job should be cancelled.");
                Assert.IsFalse(third.Cts!.IsCancellationRequested, "Jobs in other workflows must remain active.");

                await IgnoreCancellation(runTask);

                Assert.AreEqual(JobState.Failed, first.State, "Cancelled workflow jobs should fail as cancelled.");
                Assert.AreEqual(JobState.Failed, second.State, "Cancelled workflow jobs should fail as cancelled.");
                Assert.AreEqual(JobState.Done, third.State, "The unrelated workflow job should still complete.");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task SongJob_Cancellation_DoesNotFallbackToNextCandidate()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-song-fb-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            // Two candidates
            var file1 = TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Artist - Song.mp3", length: 180);

            var resp1 = new SearchResponse("user1", 1, true, 1000, 0, [file1]);
            var resp2 = new SearchResponse("user2", 1, true, 100, 0, [file2]);

            // slowMode = true so we can catch it downloading
            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], slowMode: true);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist - Song";
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                SongJob? songJob = null;
                app.Events.JobStateChanged += (job, state) =>
                {
                    if (state == JobState.Downloading && job is SongJob sj)
                    {
                        songJob = sj;
                        // Cancel as soon as downloading starts
                        sj.Cancel();
                    }
                };

                var runTask = app.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(songJob);
                Assert.AreEqual(JobState.Failed, songJob.State);
                Assert.AreEqual(FailureReason.Cancelled, songJob.FailureReason);
                Assert.AreEqual(1, testClient.DownloadCallCount, "Should only attempt the first candidate before cancellation takes effect.");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_Cancellation_DoesNotFallbackToNextFolder()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-album-fb-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            var file1 = TestHelpers.CreateSlFile(@"Music\Album\01. Artist - Song.mp3", length: 180);
            var file2 = TestHelpers.CreateSlFile(@"Shares\Album\01. Artist - Song.mp3", length: 180);

            var resp1 = new SearchResponse("user1", 1, true, 1000, 0, [file1]);
            var resp2 = new SearchResponse("user2", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2], slowMode: true);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true; // Avoid extra folder retrieval overhead
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                AlbumJob? albumJob = null;
                app.Events.JobStateChanged += (job, state) =>
                {
                    if (state == JobState.Downloading && job is AlbumJob aj)
                    {
                        albumJob = aj;
                        // Cancel as soon as downloading starts
                        aj.Cancel();
                    }
                };

                var runTask = app.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(albumJob);
                Assert.AreEqual(JobState.Failed, albumJob.State);
                Assert.AreEqual(FailureReason.Cancelled, albumJob.FailureReason);
                
                // Assuming only 1 file in the album, max 1 download call
                Assert.IsTrue(testClient.DownloadCallCount <= 1, $"Should only attempt the first folder. Calls: {testClient.DownloadCallCount}");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task CancelAlbum_MarksAllUnfinishedFolderFilesCancelled()
        {
            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-album-cancel-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-album-cancel-out-" + Guid.NewGuid());
            var albumDir = Path.Combine(musicRoot, "Artist", "Album");
            System.IO.Directory.CreateDirectory(albumDir);
            System.IO.Directory.CreateDirectory(outputDir);

            for (int i = 1; i <= 12; i++)
                System.IO.File.WriteAllBytes(Path.Combine(albumDir, $"{i:00}. Artist - Track {i:00}.mp3"), new byte[1024]);

            try
            {
                var eng = new EngineSettings
                {
                    Username = "u",
                    Password = "p",
                    MockFilesDir = musicRoot,
                    MockFilesReadTags = false,
                    MockFilesSlow = true,
                };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "{foldername}/{filename}";

                var clientManager = new SoulseekClientManager(eng);
                var engine = new DownloadEngine(eng, clientManager);

                AlbumJob? albumJob = null;
                AlbumFolder? folder = null;
                engine.Events.JobStateChanged += (job, state) =>
                {
                    if (state == JobState.Downloading && job is AlbumJob aj)
                    {
                        albumJob = aj;
                        folder = aj.ResolvedTarget;
                    }
                };

                engine.Enqueue(new ExtractJob(dl.Extraction.Input!, dl.Extraction.InputType), dl);
                engine.CompleteEnqueue();
                var runTask = engine.RunAsync(CancellationToken.None);

                await WaitForAsync(() => folder != null && folder.Files.Any(song => song.State == JobState.Downloading), 5000);

                albumJob!.Cancel();
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(folder);
                Assert.IsFalse(
                    folder!.Files.Any(song => song.State is JobState.Pending or JobState.Searching or JobState.Downloading),
                    "Cancelling an album should not leave unresolved folder files in active states.");
                Assert.IsTrue(
                    folder.Files.Any(song => song.State == JobState.Failed && song.FailureReason == FailureReason.Cancelled),
                    "At least one unfinished album file should be marked as cancelled.");

                Assert.IsNotNull(albumJob);
                var resolved = albumJob!.ResolvedTarget ?? folder;
                Assert.IsFalse(
                    resolved.Files.Any(song => song.State is JobState.Pending or JobState.Searching or JobState.Downloading),
                    "The album's resolved folder should not expose stale active child states after cancellation.");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot))
                    System.IO.Directory.Delete(musicRoot, recursive: true);
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
