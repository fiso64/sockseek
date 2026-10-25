using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Cancellation
{
    [TestClass]
    public class CancellationTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext _)
        {
            SockseekLog.AddConsole(LogLevel.Critical);
        }

        private static ClientTests.MockSoulseekClient CreateBlockedSearchClient(out TestHelpers.DownloadGate searchGate)
        {
            var gate = new TestHelpers.DownloadGate();
            searchGate = gate;
            return new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex())
            {
                BeforeSearchAsync = (query, ct) => gate.BlockAsync("", query.SearchText, ct),
            };
        }

        private static (DownloadEngine engine, string outputDir) CreateEngine(
            ISoulseekClient client, string input, string[] extra = null!)
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            var eng = new EngineSettings { Username = "u", Password = "p" };
            var dl = new DownloadSettings();
            dl.Extraction.Input = input;
            dl.Extraction.RequestedMode = ExtractionMode.Song;
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

        private static async Task WaitForAsync(DownloadEngine engine, Func<bool> condition, int timeoutMs = 5000)
        {
            using var signal = new TestSignal()
                .Subscribe<Job>(handler => engine.Events.JobStateChanged += handler, handler => engine.Events.JobStateChanged -= handler)
                .Subscribe<Job>(handler => engine.Events.JobExecutionCompleted += handler, handler => engine.Events.JobExecutionCompleted -= handler);
            await signal.WaitUntilAsync(condition, "Engine did not reach the expected state.", timeoutMs);
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


        // ── Test 2: cancelling a JobList unblocks all child searches ──────────
        // Cancels the parent JobList while both child songs are in the Searching state.
        // The songs' CTSes are linked to the list's CTS, so cancellation must
        // unblock their searches without releasing the test gate.

        [TestMethod]
        public async Task CancelJobList_MidSearch_UnblocksChildrenWithoutReleasingSearches()
        {
            var listFile  = Path.GetTempFileName();
            var (engine, outputDir) = CreateEngine(
                CreateBlockedSearchClient(out var searchGate),
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

                await WaitForAsync(engine, () => rootExtract.TerminalOutcome == JobTerminalOutcome.Succeeded);

                var rootList = (JobList)rootExtract.Result!;
                await WaitForAsync(engine, () => rootList.AllSongs().Count() == 2);
                var songs    = rootList.AllSongs().ToList();
                Assert.AreEqual(2, songs.Count);

                // Wait until both songs are concurrently Searching.
                await WaitForAsync(engine, () => songs.All(s => s.ActivityPhase == JobActivityPhase.Searching));

                Assert.IsNotNull(rootList.Cts, "JobList.Cts must be set");
                Assert.IsTrue(songs.All(s => s.Cts != null), "All child SongJob.Cts must be set");

                // Cancel the list — both songs' CTSes are linked to it.
                rootList.Cancel(JobCancellationSource.UserRequestedJob);

                // The search gate is never released: cancellation must unblock it.
                await IgnoreCancellation(runTask.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(songs.All(s => s.Cts!.IsCancellationRequested),
                    "All child song CTSes should be cancelled");
                Assert.IsTrue(rootList.IsUnsuccessfulTerminal,
                    "A cancelled JobList should not be left as a completed-running container.");
                Assert.AreEqual(JobFailureReason.Cancelled, rootList.FailureReason);
            }
            finally
            {
                System.IO.File.Delete(listFile);
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task CancelJobList_WithQueuedChildren_MarksUnstartedChildrenCancelled()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-queued-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var eng = new EngineSettings
                {
                    Username = "u",
                    Password = "p",
                    ConcurrentJobs = 1,
                };
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;

                var clientManager = TestHelpers.CreateMockClientManager(
                    CreateBlockedSearchClient(out var searchGate),
                    eng);
                var engine = new DownloadEngine(eng, clientManager);

                var first = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" });
                var second = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong2" });
                var list = new JobList("cancel queued children", [first, second]);

                engine.Enqueue(list, dl);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);

                await WaitForAsync(engine, () => first.ActivityPhase == JobActivityPhase.Searching && second.IsPending);

                list.Cancel(JobCancellationSource.UserRequestedJob);
                await IgnoreCancellation(runTask);

                Assert.AreEqual(JobTerminalOutcome.Cancelled, first.TerminalOutcome);
                Assert.AreEqual(JobFailureReason.Cancelled, first.FailureReason);
                Assert.AreEqual(JobCancellationSource.ParentJob, first.CancellationSource);
                Assert.AreEqual(JobTerminalOutcome.Cancelled, second.TerminalOutcome,
                    "A child cancelled while waiting for a job slot must not be left Pending and projected as Done.");
                Assert.AreEqual(JobFailureReason.Cancelled, second.FailureReason);
                Assert.AreEqual(JobCancellationSource.ParentJob, second.CancellationSource);
                Assert.AreEqual(JobTerminalOutcome.Cancelled, list.TerminalOutcome);
                Assert.AreEqual(JobFailureReason.Cancelled, list.FailureReason);
                Assert.AreEqual(JobCancellationSource.UserRequestedJob, list.CancellationSource);
            }
            finally
            {
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
                CreateBlockedSearchClient(out var searchGate),
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
                await WaitForAsync(engine, () => rootExtract.TerminalOutcome == JobTerminalOutcome.Succeeded);

                var rootList = (JobList)rootExtract.Result!;
                await WaitForAsync(engine, () => rootList.AllSongs().Count() == 2);
                var songs = rootList.AllSongs().ToList();
                Assert.AreEqual(2, songs.Count);

                // Wait until both songs are concurrently Searching.
                await WaitForAsync(engine, () => songs.All(s => s.ActivityPhase == JobActivityPhase.Searching));

                var song1 = songs[0];
                var song2 = songs[1];

                // Cancel only song1 — song2 must be unaffected.
                song1.Cancel(JobCancellationSource.UserRequestedJob);
                Assert.IsFalse(song2.Cts!.IsCancellationRequested,
                    "song2.Cts must NOT be cancelled when only song1 is cancelled");

                // Release the sibling's search only after verifying cancellation isolation.
                searchGate.Open();
                await IgnoreCancellation(runTask.WaitAsync(TimeSpan.FromSeconds(5)));

                Assert.IsTrue(song1.IsUnsuccessfulTerminal,
                    "song1 should be Failed after being cancelled mid-search");
                Assert.AreEqual(JobTerminalOutcome.Succeeded, song2.TerminalOutcome,
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
                CreateBlockedSearchClient(out var searchGate),
                "testartist testsong");

            try
            {
                var runTask = engine.RunAsync(CancellationToken.None);

                var extractJob = (ExtractJob)engine.Queue.Jobs[0];

                // Extraction (StringExtractor) is synchronous — Done almost immediately.
                await WaitForAsync(engine, () => extractJob.TerminalOutcome == JobTerminalOutcome.Succeeded);
                var songJob = (SongJob)extractJob.Result!;

                // Wait for the song to enter Searching so its Cts is live.
                await WaitForAsync(engine, () => songJob.ActivityPhase == JobActivityPhase.Searching);

                // Cancel the already-Done ExtractJob.
                extractJob.Cancel(JobCancellationSource.UserRequestedJob);

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
                    CreateBlockedSearchClient(out var searchGate),
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

                await WaitForAsync(engine, () => first.LifecycleState == JobLifecycleState.Running
                    && second.LifecycleState == JobLifecycleState.Running
                    && third.LifecycleState == JobLifecycleState.Running);

                var cancelled = engine.CancelWorkflow(workflowId);

                Assert.AreEqual(2, cancelled, "CancelWorkflow should cancel the active jobs in the matching workflow.");
                Assert.IsTrue(first.Cts!.IsCancellationRequested, "First workflow job should be cancelled.");
                Assert.IsTrue(second.Cts!.IsCancellationRequested, "Second workflow job should be cancelled.");
                Assert.IsFalse(third.Cts!.IsCancellationRequested, "Jobs in other workflows must remain active.");

                searchGate.Open();
                await IgnoreCancellation(runTask.WaitAsync(TimeSpan.FromSeconds(5)));

                Assert.IsTrue(first.IsUnsuccessfulTerminal, "Cancelled workflow jobs should fail as cancelled.");
                Assert.IsTrue(second.IsUnsuccessfulTerminal, "Cancelled workflow jobs should fail as cancelled.");
                Assert.AreEqual(JobTerminalOutcome.Succeeded, third.TerminalOutcome, "The unrelated workflow job should still complete.");
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

            var downloadGate = new TestHelpers.DownloadGate();
            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2])
            {
                BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
            };

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
                app.Events.JobStateChanged += job =>
                {
                    if (job.ActivityPhase == JobActivityPhase.Downloading && job is SongJob sj)
                    {
                        songJob = sj;
                        // Cancel as soon as downloading starts
                        sj.Cancel(JobCancellationSource.UserRequestedJob);
                    }
                };

                var runTask = app.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(songJob);
                Assert.IsTrue(songJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, songJob.FailureReason);
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

            var downloadGate = new TestHelpers.DownloadGate();
            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2])
            {
                BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
            };

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
                app.Events.JobStateChanged += job =>
                {
                    if (job.ActivityPhase == JobActivityPhase.Downloading && job is AlbumJob aj)
                    {
                        albumJob = aj;
                        // Cancel as soon as downloading starts
                        aj.Cancel(JobCancellationSource.UserRequestedJob);
                    }
                };

                var runTask = app.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(albumJob);
                Assert.IsTrue(albumJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, albumJob.FailureReason);
                
                // Assuming only 1 file in the album, max 1 download call
                Assert.IsTrue(testClient.DownloadCallCount <= 1, $"Should only attempt the first folder. Calls: {testClient.DownloadCallCount}");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumJob_CancelledDuringSearch_RemainsCancelled()
        {
            var response = new SearchResponse(
                username: "user1",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList:
                [
                    TestHelpers.CreateSlFile(@"Shares\ELO\Out Of The Blue\01. ELO - Turn Me On.mp3", length: 240),
                ]);

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-album-search-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "u", Password = "p" };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;

                var albumJob = new AlbumJob(new AlbumQuery { Artist = "ELO", Album = "Out Of The Blue" });
                var client = new ClientTests.MockSoulseekClient([response]);
                var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));

                engine.Events.JobStateChanged += job =>
                {
                    if (ReferenceEquals(job, albumJob) && job.LifecycleState == JobLifecycleState.Running)
                        job.Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(albumJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsTrue(albumJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, albumJob.FailureReason);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task AggregateJob_CancelledDuringSearch_RemainsCancelled()
        {
            var response = new SearchResponse(
                username: "user1",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList:
                [
                    TestHelpers.CreateSlFile(@"Shares\ELO\Turn Me On.mp3", length: 240),
                ]);

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-aggregate-search-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "u", Password = "p" };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;
                downloadSettings.Search.MinSharesAggregate = 1;

                var aggregateJob = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Turn Me On" });
                var client = new ClientTests.MockSoulseekClient([response]);
                var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));

                engine.Events.JobStateChanged += job =>
                {
                    if (ReferenceEquals(job, aggregateJob) && job.LifecycleState == JobLifecycleState.Running)
                        job.Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(aggregateJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsTrue(aggregateJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, aggregateJob.FailureReason);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ManualSongJob_CancelledDuringSearch_RemainsCancelled()
        {
            var response = new SearchResponse(
                username: "user1",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList:
                [
                    TestHelpers.CreateSlFile(@"Shares\ELO\Turn Me On.mp3", length: 240),
                ]);

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-manual-song-search-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "u", Password = "p" };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;

                var songJob = new SongJob(new SongQuery { Artist = "ELO", Title = "Turn Me On" })
                {
                    DownloadBehaviorPolicy = new DownloadBehaviorPolicy { Song = DownloadBehavior.Manual },
                };

                var client = new ClientTests.MockSoulseekClient([response]);
                var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));

                engine.Events.JobStateChanged += job =>
                {
                    if (ReferenceEquals(job, songJob) && job.LifecycleState == JobLifecycleState.Running)
                        job.Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(songJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsTrue(songJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, songJob.FailureReason);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ExtractJob_CancelledDuringExtraction_RemainsCancelled()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-extract-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "u", Password = "p" };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Extraction.Input = "Artist - Song";
                downloadSettings.Output.ParentDir = outputDir;

                var extractJob = new ExtractJob(downloadSettings.Extraction.Input, downloadSettings.Extraction.InputType);
                var client = new ClientTests.MockSoulseekClient([]);
                var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));

                engine.Events.JobStateChanged += job =>
                {
                    if (ReferenceEquals(job, extractJob) && job.ActivityPhase == JobActivityPhase.Extracting)
                        job.Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(extractJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsTrue(extractJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, extractJob.FailureReason);
                Assert.IsNull(extractJob.Result);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
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
                    MockFilesReadTags = false,
                };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "{foldername}/{filename}";

                var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);
                var downloadGate = new TestHelpers.DownloadGate();
                testClient.BeforeDownloadCompletesAsync = downloadGate.BlockAsync;
                var clientManager = TestHelpers.CreateMockClientManager(testClient, eng);
                var engine = new DownloadEngine(eng, clientManager);

                AlbumJob? albumJob = null;
                AlbumFolder? folder = null;
                engine.Events.JobStateChanged += job =>
                {
                    if (job.ActivityPhase == JobActivityPhase.Downloading && job is AlbumJob aj)
                    {
                        albumJob = aj;
                        folder = aj.ResolvedTarget;
                    }
                };

                engine.Enqueue(new ExtractJob(dl.Extraction.Input!, dl.Extraction.InputType), dl);
                engine.CompleteEnqueue();
                var runTask = engine.RunAsync(CancellationToken.None);

                await WaitForAsync(engine, () => albumJob?.TrackJobs.Any(song => song.ActivityPhase == JobActivityPhase.Downloading) == true, 5000);

                albumJob!.Cancel(JobCancellationSource.UserRequestedJob);
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(folder);
                Assert.IsFalse(
                    albumJob!.TrackJobs.Any(song => song.LifecycleState == JobLifecycleState.Pending || song.ActivityPhase is JobActivityPhase.Searching or JobActivityPhase.Downloading),
                    "Cancelling an album should not leave unresolved folder files in active states.");
                Assert.IsTrue(
                    albumJob.TrackJobs.Any(song => song.IsUnsuccessfulTerminal && song.FailureReason == JobFailureReason.Cancelled),
                    "At least one unfinished album file should be marked as cancelled.");

                Assert.IsNotNull(albumJob);
                Assert.IsFalse(
                    albumJob.TrackJobs.Any(song => song.LifecycleState == JobLifecycleState.Pending || song.ActivityPhase is JobActivityPhase.Searching or JobActivityPhase.Downloading),
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

        [TestMethod]
        public async Task AlbumAggregate_CancelledChildAlbums_MarksParentContainersCancelled()
        {
            var index = new List<SearchResponse>
            {
                new(
                    username: "user1",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList:
                    [
                        TestHelpers.CreateSlFile(@"Shares\ELO\Album One\01. ELO - One.mp3", length: 240),
                        TestHelpers.CreateSlFile(@"Shares\ELO\Album One\02. ELO - Two.mp3", length: 241),
                    ]),
                new(
                    username: "user2",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList:
                    [
                        TestHelpers.CreateSlFile(@"Shares\ELO\Album Two\01. ELO - Three.mp3", length: 240),
                        TestHelpers.CreateSlFile(@"Shares\ELO\Album Two\02. ELO - Four.mp3", length: 241),
                    ]),
            };

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-album-aggregate-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings
                {
                    Username = "u",
                    Password = "p",
                    ConcurrentJobs = 2,
                    ConcurrentSearches = 10,
                };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;
                downloadSettings.Output.WriteIndex = false;
                downloadSettings.Search.MinSharesAggregate = 1;
                downloadSettings.Search.NoBrowseFolder = true;
                downloadSettings.Skip.SkipExisting = false;

                var aggregateJob = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
                var client = new ClientTests.MockSoulseekClient(index);
                var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));

                JobList? aggregateList = null;
                engine.Events.JobRegistered += (job, parent) =>
                {
                    if (parent == aggregateJob && job is JobList list)
                        aggregateList = list;
                };
                engine.Events.JobStateChanged += job =>
                {
                    if (ReferenceEquals(job, aggregateList) && job.ActivityPhase == JobActivityPhase.RunningChildren)
                        job.Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(aggregateJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(aggregateList, "AlbumAggregate should process matching albums through a registered JobList.");
                Assert.IsTrue(aggregateList!.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, aggregateList.FailureReason);
                Assert.IsTrue(aggregateJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, aggregateJob.FailureReason);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
