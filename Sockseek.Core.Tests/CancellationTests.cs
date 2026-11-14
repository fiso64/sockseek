using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Transfers.Downloads.ManualSelection;

namespace Tests.Cancellation
{
    [TestClass]
    public class CancellationTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext _)
        {
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
                .Subscribe<Sockseek.Core.Events.JobStateChangedChange>(handler => engine.Events.JobStateChanged += handler, handler => engine.Events.JobStateChanged -= handler)
                .Subscribe<Sockseek.Core.Events.JobExecutionCompletedChange>(handler => engine.Events.JobExecutionCompleted += handler, handler => engine.Events.JobExecutionCompleted -= handler);
            await signal.WaitUntilAsync(condition, "Engine did not reach the expected state.", timeoutMs);
        }

        // Await a RunAsync task that may have been cancelled — swallow expected cancellation.
        private static async Task IgnoreCancellation(Task t)
        {
            try { await t; }
            catch (OperationCanceledException) { }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException)) { }
        }


        [TestMethod]
        public async Task Engine_RegistersQueuedListTreeAndPreservesChildCancellation()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-register-queued-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);
            var settings = new EngineSettings
            {
                Username = "u", Password = "p", ConcurrentJobs = 1, ConcurrentExtractors = 1,
            };
            var downloads = new DownloadSettings();
            downloads.Output.ParentDir = outputDir;
            downloads.Output.WriteIndex = false;
            downloads.Skip.SkipExisting = false;
            var client = CreateBlockedSearchClient(out var searchGate);
            await using var engine = new DownloadEngine(settings, TestHelpers.CreateMockClientManager(client, settings));
            var first = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" });
            var second = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong2" });
            var cancelled = new SongJob(new SongQuery { Artist = "testartist", Title = "never start" });
            var sibling = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" });
            var nested = new JobList("queued branch", [cancelled, sibling]);
            var root = new JobList("queued tree", [first, second, nested]);
            Job[] expected = [root, first, second, nested, cancelled, sibling];
            var registered = new System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid?>();
            int registrationCount = 0;
            engine.Events.JobRegistered += change =>
            {
                registered[change.Job.Id] = change.ParentJobId;
                Interlocked.Increment(ref registrationCount);
            };
            Task? running = null;
            try
            {
                engine.Enqueue(root, downloads);
                engine.CompleteEnqueue();
                running = engine.RunAsync(CancellationToken.None);
                await searchGate.WaitForStartedCountAsync(1);

                foreach (var job in expected)
                {
                    Assert.AreSame(job, engine.GetJob(job.Id), "Known queued members must be addressable before execution reaches them.");
                    Assert.IsNotNull(job.Cts, "A published queued job must already be cancellable.");
                }
                Assert.AreEqual(6, engine.GetJobsByWorkflow(root.WorkflowId).Count);
                Assert.AreEqual(root.Id, registered[first.Id]);
                Assert.AreEqual(root.Id, registered[second.Id]);
                Assert.AreEqual(root.Id, registered[nested.Id]);
                Assert.AreEqual(nested.Id, registered[cancelled.Id]);
                Assert.AreEqual(nested.Id, registered[sibling.Id]);
                Assert.IsTrue(second.IsPending && nested.IsPending && cancelled.IsPending && sibling.IsPending);
                Assert.AreEqual(1, client.SearchCallCount, "Registration must not start queued searches.");
                Assert.IsTrue(engine.CancelJobByDisplayId(cancelled.DisplayId, root.WorkflowId));
                Assert.IsTrue(cancelled.Cts!.IsCancellationRequested);
                Assert.IsFalse(root.Cts!.IsCancellationRequested || nested.Cts!.IsCancellationRequested || sibling.Cts!.IsCancellationRequested);

                searchGate.Open();
                await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(first.IsSuccessfulTerminal && second.IsSuccessfulTerminal && sibling.IsSuccessfulTerminal);
                Assert.AreEqual(JobTerminalOutcome.Cancelled, cancelled.TerminalOutcome);
                Assert.AreEqual(JobCancellationSource.UserRequestedJob, cancelled.CancellationSource);
                Assert.AreEqual(3, client.SearchCallCount, "A queued cancellation must survive admission and never search.");
                Assert.AreEqual(6, registrationCount, "Starting a registered member must not publish it again.");
            }
            finally
            {
                engine.Cancel();
                searchGate.Open();
                if (running != null) await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5)));
                System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }


        [TestMethod]
        [DataRow("async-dispose")]
        [DataRow("sync-dispose")]
        [DataRow("run-cancellation")]
        public async Task Engine_ShutdownWaitsForActiveRootCleanup(string shutdown)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleaningUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex())
            {
                BeforeSearchAsync = async (_, ct) =>
                {
                    started.TrySetResult();
                    try { await never.Task.WaitAsync(ct); }
                    finally
                    {
                        cleaningUp.TrySetResult();
                        await releaseCleanup.Task;
                    }
                },
            };
            var settings = new EngineSettings { Username = "u", Password = "p" };
            var engine = new DownloadEngine(settings, TestHelpers.CreateMockClientManager(client, settings));
            var job = new SearchJob("testartist");
            engine.Enqueue(job, new DownloadSettings());
            using var cancellation = new CancellationTokenSource();
            var running = engine.RunAsync(cancellation.Token);
            Task stopping = Task.CompletedTask;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                switch (shutdown)
                {
                    case "async-dispose": stopping = engine.DisposeAsync().AsTask(); break;
                    case "sync-dispose": stopping = Task.Run(engine.Dispose); break;
                    default: cancellation.Cancel(); stopping = running; break;
                }
                await cleaningUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(stopping.IsCompleted, "Shutdown must join the root before destroying its shared resources.");
                Assert.IsFalse(job.IsTerminal, "The root is still unwinding the peer operation.");
                releaseCleanup.TrySetResult();
                await IgnoreCancellation(stopping.WaitAsync(TimeSpan.FromSeconds(5)));
                await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(JobTerminalOutcome.Cancelled, job.TerminalOutcome);
                Assert.AreEqual(JobCancellationSource.InternalEngine, job.CancellationSource);
            }
            finally
            {
                releaseCleanup.TrySetResult();
                cancellation.Cancel();
                engine.Cancel();
                await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5)));
                await IgnoreCancellation(stopping.WaitAsync(TimeSpan.FromSeconds(5)));
                await engine.DisposeAsync();
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
        [DataRow(false)]
        [DataRow(true)]
        public async Task CancelJobList_WithQueuedChildren_MarksUnstartedChildrenCancelled(bool separateOutput)
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
                dl.Output.WriteIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "index.csv");
                dl.Output.WritePlaylist = true;
                dl.Output.M3uFilePath = Path.Combine(outputDir, "playlist.m3u8");

                var clientManager = TestHelpers.CreateMockClientManager(
                    CreateBlockedSearchClient(out var searchGate),
                    eng);
                var engine = new DownloadEngine(eng, clientManager);

                var first = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong" });
                var second = new SongJob(new SongQuery { Artist = "testartist", Title = "testsong2" });
                if (separateOutput)
                {
                    var secondSettings = SettingsCloner.Clone(dl);
                    secondSettings.Output.IndexFilePath = Path.Combine(outputDir, "second-index.csv");
                    secondSettings.Output.M3uFilePath = Path.Combine(outputDir, "second-playlist.m3u8");
                    second.PlannedSettings = JobSettingsSnapshot.Capture(secondSettings);
                }
                var list = new JobList("cancel queued children", [first, second]);
                int cancelling = 0;
                var cancellationProgress = new System.Collections.Concurrent.ConcurrentBag<Sockseek.Core.Events.OverallProgressChange>();
                engine.Events.OverallProgress += change =>
                {
                    if (Volatile.Read(ref cancelling) != 0) cancellationProgress.Add(change);
                };

                engine.Enqueue(list, dl);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);

                await WaitForAsync(engine, () => first.ActivityPhase == JobActivityPhase.Searching && second.IsPending);

                Volatile.Write(ref cancelling, 1);
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
                Assert.AreEqual(1, cancellationProgress.Count,
                    "Bulk cancellation publishes one exact final overall count, not a full recount/broadcast per queued child.");
                Assert.AreEqual(0, cancellationProgress.Single().Done);
                // Legacy overall progress keeps cancellation distinct from a
                // download failure; per-job outcomes above remain Cancelled.
                Assert.AreEqual(0, cancellationProgress.Single().Failed);
                Assert.AreEqual(2, cancellationProgress.Single().Total);
                foreach (var indexPath in separateOutput
                    ? new[] { dl.Output.IndexFilePath, second.PlannedSettings!.ReadSettings().Output.IndexFilePath }
                    : new[] { dl.Output.IndexFilePath })
                {
                    var retained = new DownloadOutputFile(indexPath!, list, OutputFileKind.Index, true).GetPreviousRunData();
                    Assert.AreEqual(2, retained.Count);
                    Assert.IsTrue(retained.All(entry => entry.State == JobStateOld.Failed
                        && entry.FailureReason == JobFailureReason.Cancelled));
                }
                foreach (var playlistPath in separateOutput
                    ? new[] { dl.Output.M3uFilePath, second.PlannedSettings!.ReadSettings().Output.M3uFilePath }
                    : new[] { dl.Output.M3uFilePath })
                {
                    string[] lines = System.IO.File.ReadAllLines(playlistPath!).Where(line => line.Length > 0).ToArray();
                    Assert.AreEqual(2, lines.Length);
                    Assert.IsTrue(lines.All(line => line.StartsWith("#FAIL:", StringComparison.Ordinal)));
                }
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
                app.Events.JobStateChanged += change =>
                {
                    if (change.ActivityPhase == JobActivityPhase.Downloading
                        && change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.Song
                        && app.GetJob(change.Job.Id) is SongJob sj)
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
                Assert.AreEqual(0, app.GetPeerFailureCounts(songJob.WorkflowId).Count,
                    "Operator cancellation must not penalize the peer.");
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
                app.Events.JobStateChanged += change =>
                {
                    if (change.ActivityPhase == JobActivityPhase.Downloading
                        && change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.Album
                        && app.GetJob(change.Job.Id) is AlbumJob aj)
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

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Id == albumJob.Id && change.LifecycleState == JobLifecycleState.Running)
                        albumJob.Cancel(JobCancellationSource.UserRequestedJob);
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
        public async Task SongGroupsJob_CancelledDuringSearch_RemainsCancelled()
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

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-group-search-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings { Username = "u", Password = "p" };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;
                downloadSettings.Search.MinGroupShares = 1;

                var songGroupsJob = new SongGroupsJob(new SongQuery { Artist = "ELO", Title = "Turn Me On" });
                var client = new ClientTests.MockSoulseekClient([response]);
                var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Id == songGroupsJob.Id && change.LifecycleState == JobLifecycleState.Running)
                        songGroupsJob.Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(songGroupsJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsTrue(songGroupsJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, songGroupsJob.FailureReason);
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

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Id == songJob.Id && change.LifecycleState == JobLifecycleState.Running)
                        songJob.Cancel(JobCancellationSource.UserRequestedJob);
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
        public async Task ManualSelection_SkipCommitsExplicitTerminalReasonAndFlushesEffects()
        {
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
            {
                Config = new DownloadSettings(),
                DownloadBehaviorPolicy = new DownloadBehaviorPolicy
                {
                    Album = DownloadBehavior.Manual,
                },
            };
            album.SetAwaitingSelection();
            int flushCount = 0;
            var coordinator = new ManualSelectionCoordinator(
                id => id == album.Id ? album : null,
                new DownloadJobContextStore(),
                (_, _, _) => { },
                _ => { },
                _ => { },
                (_, _) => { },
                _ =>
                {
                    flushCount++;
                    return Task.CompletedTask;
                },
                job => job.IsSuccessfulTerminal,
                _ => { });

            Assert.IsTrue(coordinator.HasResumableState([album]));
            Assert.IsTrue(await coordinator.SkipAsync(album.Id));
            Assert.AreEqual(JobLifecycleState.Terminal, album.LifecycleState);
            Assert.AreEqual(JobTerminalOutcome.Skipped, album.TerminalOutcome);
            Assert.AreEqual(JobSkipReason.Manual, album.SkipReason);
            Assert.AreEqual(1, flushCount);
            Assert.IsFalse(coordinator.HasResumableState([album]));
            Assert.IsFalse(await coordinator.SkipAsync(album.Id));
        }

        [TestMethod]
        [DataRow(false, true, false)]
        [DataRow(false, false, false)]
        [DataRow(true, true, false)]
        [DataRow(true, false, false)]
        [DataRow(false, true, true)]
        [DataRow(true, true, true)]
        public async Task ManualGroupSelection_ClosingWaitsForOnlyTheSelectedGroup(bool albumMode, bool succeeds, bool cancelOwner)
        {
            string outputDir = System.IO.Directory.CreateTempSubdirectory("sockseek-manual-group-").FullName;
            var gate = new TestHelpers.DownloadGate();
            Soulseek.File[] Files(string prefix) =>
            [
                TestHelpers.CreateSlFile($@"{prefix}\Artist\Album One\01 - First.mp3", size: 128),
                TestHelpers.CreateSlFile($@"{prefix}\Artist\Album Two\01 - Second.mp3", size: 128),
            ];
            var client = new ClientTests.MockSoulseekClient(
            [
                new SearchResponse("peer", 1, true, 100_000, 0, Files("Music")),
                new SearchResponse("other-peer", 2, true, 50_000, 0, Files("Other")),
            ])
            {
                BeforeDownloadCompletesAsync = async (username, filename, ct) =>
                {
                    await gate.BlockAsync(username, filename, ct);
                    if (!succeeds) throw new TransferRejectedException("Selected source rejects the download.");
                },
            };
            var settings = new DownloadSettings();
            settings.Output.ParentDir = outputDir;
            settings.Output.WriteIndex = false;
            settings.Skip.SkipExisting = false;
            settings.Search.MinGroupShares = 1;
            settings.Search.NoBrowseFolder = true;
            Job group = albumMode
                ? new AlbumGroupsJob(new AlbumQuery { Artist = "Artist" })
                : new SongGroupsJob(new SongQuery { Artist = "Artist" });
            group.WorkflowId = Guid.NewGuid();
            group.DownloadBehaviorPolicy = new DownloadBehaviorPolicy
            {
                SongGroups = DownloadBehavior.Manual, AlbumGroups = DownloadBehavior.Manual,
            };
            var engineSettings = new EngineSettings
            {
                Username = "u", Password = "p", LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            };
            var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings), retireTerminalWorkflows: true);
            Task? running = null;
            bool queueClosed = false;
            try
            {
                engine.Enqueue(group, settings);
                running = engine.RunAsync(CancellationToken.None);
                await WaitForAsync(engine, () => group.IsAwaitingSelection);
                Job selected, other;
                Func<bool> choose, chooseAlternative, chooseOther;
                Func<int> alternativeCount;
                if (group is AlbumGroupsJob albums)
                {
                    Assert.AreEqual(2, albums.Albums.Count);
                    var album = albums.Albums[^1];
                    var ignored = albums.Albums[0];
                    selected = album;
                    other = ignored;
                    choose = () => engine.TryStartManualAlbumSelection(group.Id, album.Results[^1], album.Query, null, out _);
                    chooseAlternative = () => engine.TryStartManualAlbumSelection(group.Id, album.Results[0], album.Query, null, out _);
                    chooseOther = () => engine.TryStartManualAlbumSelection(group.Id, ignored.Results[0], ignored.Query, null, out _);
                    alternativeCount = () => album.Results.Count;
                }
                else
                {
                    var songs = (SongGroupsJob)group;
                    Assert.AreEqual(2, songs.Songs.Count);
                    var song = songs.Songs[^1];
                    var ignored = songs.Songs[0];
                    selected = song;
                    other = ignored;
                    choose = () => engine.TryStartManualSongSelection(group.Id, song.Candidates![^1], null, out _);
                    chooseAlternative = () => engine.TryStartManualSongSelection(group.Id, song.Candidates![0], null, out _);
                    chooseOther = () => engine.TryStartManualSongSelection(group.Id, ignored.Candidates![0], null, out _);
                    alternativeCount = () => song.Candidates!.Count;
                }
                Assert.AreEqual(2, alternativeCount());
                Assert.IsTrue(choose());
                Assert.AreSame(selected, engine.GetJob(selected.Id));
                Assert.AreEqual(group.WorkflowId, selected.WorkflowId);
                Assert.AreEqual(2, alternativeCount(), "Executing a choice retains discovery alternatives.");
                await gate.WaitForStartedCountAsync(1);
                Assert.IsNull(engine.GetJob(other.Id), "Unselected groups are not execution children.");
                Assert.IsFalse(chooseAlternative(), "An active group cannot be retargeted or resumed twice.");
                if (cancelOwner)
                    Assert.IsTrue(engine.CancelJob(group.Id));
                else
                {
                    Assert.IsTrue(await engine.CompleteManualSelectionAsync(group.Id));
                    Assert.IsTrue(group.IsAwaitingSelection, "Closing waits for the selected group to finish.");
                    Assert.IsFalse(chooseOther(), "A closed selection cannot admit another group.");
                    gate.Open();
                }
                try
                {
                    await WaitForAsync(engine, () => group.IsTerminal && selected.IsTerminal);
                }
                catch (TimeoutException)
                {
                    Assert.Fail($"Owner never settled: root={group.LifecycleState}/{group.TerminalOutcome}, "
                        + $"selected={selected.LifecycleState}/{selected.TerminalOutcome}; cancel={cancelOwner}.");
                }
                var expected = cancelOwner ? JobTerminalOutcome.Cancelled
                    : succeeds ? JobTerminalOutcome.Succeeded : JobTerminalOutcome.Failed;
                Assert.AreEqual(expected, group.TerminalOutcome);
                Assert.AreEqual(expected, selected.TerminalOutcome);
                if (cancelOwner) Assert.AreEqual(JobCancellationSource.UserRequestedJob, group.CancellationSource);
                Assert.AreEqual(1, gate.StartedCount, "An explicit choice must not try another source or group.");
                if (!cancelOwner) Assert.AreEqual(JobLifecycleState.Pending, other.LifecycleState);
                Assert.IsFalse(await engine.CompleteManualSelectionAsync(group.Id));
                engine.CompleteEnqueue();
                queueClosed = true;
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(0, engine.GetJobsByWorkflow(group.WorkflowId).Count,
                    "A terminal manual workflow must not stay alive as a resumable picker.");
                Assert.AreEqual(0, engine.RetainedStateCounts.Contexts);
                Assert.AreEqual(0, engine.RetainedStateCounts.ManualSelections);
            }
            finally
            {
                gate.Open();
                if (!queueClosed) engine.CompleteEnqueue();
                try
                {
                    if (running != null) await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    await engine.DisposeAsync();
                    System.IO.Directory.Delete(outputDir, recursive: true);
                }
            }
        }

        [TestMethod]
        [DataRow(false, false, "job")]
        [DataRow(false, true, "job")]
        [DataRow(true, false, "job")]
        [DataRow(true, true, "job")]
        [DataRow(false, false, "workflow")]
        [DataRow(false, true, "workflow")]
        [DataRow(true, false, "workflow")]
        [DataRow(true, true, "workflow")]
        [DataRow(false, false, "all")]
        [DataRow(false, true, "all")]
        [DataRow(true, false, "all")]
        [DataRow(true, true, "all")]
        [DataRow(false, false, "engine")]
        [DataRow(false, true, "engine")]
        [DataRow(true, false, "engine")]
        [DataRow(true, true, "engine")]
        public async Task ManualSelection_CancellationFinalizesIdleJobsWithoutRediscovery(bool albumMode, bool groupMode, string scope)
        {
            string outputDir = System.IO.Directory.CreateTempSubdirectory("sockseek-idle-cancel-").FullName;
            int searches = 0, downloads = 0;
            var client = new ClientTests.MockSoulseekClient(
            [
                new SearchResponse("peer", 1, true, 100_000, 0,
                [TestHelpers.CreateSlFile(@"Music\Artist\Album\01 - First.mp3", size: 128)]),
            ])
            {
                BeforeSearchAsync = (_, _) => { Interlocked.Increment(ref searches); return Task.CompletedTask; },
                BeforeDownloadCompletesAsync = (_, _, _) => { Interlocked.Increment(ref downloads); return Task.CompletedTask; },
            };
            var settings = new DownloadSettings();
            settings.Output.ParentDir = outputDir;
            settings.Output.WriteIndex = false;
            settings.Search.MinGroupShares = 1;
            settings.Search.NoBrowseFolder = true;
            Job job = (albumMode, groupMode) switch
            {
                (false, false) => new SongJob(new SongQuery { Artist = "Artist", Title = "First" }),
                (false, true) => new SongGroupsJob(new SongQuery { Artist = "Artist" }),
                (true, false) => new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }),
                _ => new AlbumGroupsJob(new AlbumQuery { Artist = "Artist" }),
            };
            job.DownloadBehaviorPolicy = new DownloadBehaviorPolicy { Default = DownloadBehavior.Manual };
            var engineSettings = new EngineSettings
            {
                Username = "u", Password = "p", LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            };
            await using var engine = new DownloadEngine(engineSettings,
                TestHelpers.CreateMockClientManager(client, engineSettings), retireTerminalWorkflows: true);
            var discoveryFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int terminalNotifications = 0;
            bool inlineCancellationAccepted = false;
            engine.Events.JobStateChanged += change =>
            {
                if (change.Job.Id != job.Id || change.Job.LifecycleState != JobLifecycleState.AwaitingSelection) return;
                // Cancel in the publication callback: the discovery execution
                // has not returned yet, so its completion can race cancellation.
                if (scope == "job") inlineCancellationAccepted = engine.CancelJob(job.Id);
                discoveryFinished.TrySetResult();
            };
            engine.Events.JobExecutionCompleted += change =>
            {
                if (change.Job.Id == job.Id && change.Job.LifecycleState == JobLifecycleState.Terminal)
                    Interlocked.Increment(ref terminalNotifications);
            };
            Task? running = null;
            try
            {
                engine.Enqueue(job, settings);
                running = engine.RunAsync(CancellationToken.None);
                await discoveryFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                int searchesBeforeCancellation = Volatile.Read(ref searches);
                var source = scope switch
                {
                    "job" => JobCancellationSource.UserRequestedJob,
                    "workflow" => JobCancellationSource.UserRequestedWorkflow,
                    _ => JobCancellationSource.UserRequestedAllJobs,
                };
                switch (scope)
                {
                    case "job": Assert.IsTrue(inlineCancellationAccepted); break;
                    case "workflow": Assert.IsTrue(engine.CancelWorkflow(job.WorkflowId) > 0); break;
                    case "all": Assert.IsTrue(engine.CancelAllJobs() > 0); break;
                    default: engine.Cancel(); break;
                }
                await WaitForAsync(engine, () => job.IsTerminal);
                Assert.AreEqual(JobTerminalOutcome.Cancelled, job.TerminalOutcome);
                Assert.AreEqual(source, job.CancellationSource);
                Assert.AreEqual(searchesBeforeCancellation, searches, "Cancellation must not run discovery again.");
                Assert.AreEqual(0, downloads, "Cancelling a picker cannot start its candidates.");
                if (scope != "engine")
                {
                    var healthy = new RemoteFileJob(TestHelpers.CreateAlbumFile("peer",
                        @"Music\Artist\Album\01 - First.mp3", size: 128).Candidate.Target);
                    engine.Enqueue(healthy, settings);
                    await WaitForAsync(engine, () => healthy.IsTerminal);
                    Assert.AreEqual(JobTerminalOutcome.Succeeded, healthy.TerminalOutcome,
                        "Scoped cancellation must leave the daemon available for new work.");
                }
            }
            finally
            {
                engine.CompleteEnqueue();
                try
                {
                    if (running != null) await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally { System.IO.Directory.Delete(outputDir, recursive: true); }
            }
            Assert.AreEqual(0, engine.GetJobsByWorkflow(job.WorkflowId).Count);
            Assert.AreEqual(0, engine.RetainedStateCounts.Contexts);
            Assert.AreEqual(0, engine.RetainedStateCounts.ManualSelections);
            Assert.AreEqual(1, terminalNotifications, "Only one execution may publish the terminal cancellation.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ManualSong_CancellationDuringResumeAdmissionDoesNotStartDownload(bool cancelOnAdmission)
        {
            string outputDir = System.IO.Directory.CreateTempSubdirectory("sockseek-queued-choice-cancel-").FullName;
            const string selectedPath = @"Music\Artist\Album\01 - First.mp3";
            const string healthyPath = @"Music\Artist\Album\02 - Second.mp3";
            var attempted = new System.Collections.Concurrent.ConcurrentBag<string>();
            var client = new ClientTests.MockSoulseekClient(
            [
                new SearchResponse("peer", 1, true, 100_000, 0,
                [TestHelpers.CreateSlFile(selectedPath, size: 128), TestHelpers.CreateSlFile(healthyPath, size: 128)]),
            ])
            {
                BeforeDownloadCompletesAsync = (_, filename, _) => { attempted.Add(filename); return Task.CompletedTask; },
            };
            var settings = new DownloadSettings();
            settings.Output.ParentDir = outputDir;
            settings.Output.WriteIndex = false;
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "First" })
            {
                DownloadBehaviorPolicy = new DownloadBehaviorPolicy { Song = DownloadBehavior.Manual },
            };
            var healthy = new RemoteFileJob(TestHelpers.CreateAlbumFile("peer", healthyPath, size: 128).Candidate.Target);
            var engineSettings = new EngineSettings
            {
                Username = "u", Password = "p", ConcurrentJobs = 2, ConcurrentExtractors = 2,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            };
            DownloadEngine? observedEngine = null;
            int starts = 0;
            bool admissionCancellationAccepted = false;
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddProvider(new CallbackLogger((id, properties, _) =>
                    {
                        if (id.Id != 3012 || !properties.Any(pair => pair.Key == "JobId" && Equals(pair.Value, song.Id))) return;
                        if (Interlocked.Increment(ref starts) == 2 && cancelOnAdmission)
                            admissionCancellationAccepted = observedEngine!.CancelJob(song.Id);
                    })));
            await using var engine = new DownloadEngine(engineSettings,
                TestHelpers.CreateMockClientManager(client, engineSettings), loggerFactory: loggerFactory, retireTerminalWorkflows: true);
            observedEngine = engine;
            using var releaseDiscovery = new ManualResetEventSlim();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var publishingDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var followingRootPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Events.JobExecutionCompleted += change =>
            {
                if (change.Job.Id != song.Id || change.Job.LifecycleState != JobLifecycleState.AwaitingSelection) return;
                publishingDiscovery.TrySetResult();
                releaseDiscovery.Wait(watchdog.Token);
            };
            // Preparation of the following root proves that the queued resume
            // has been read, while discovery's final publication is still held.
            healthy.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(Job.Config)) followingRootPrepared.TrySetResult();
            };
            engine.Enqueue(song, settings);
            var running = Task.Run(() => engine.RunAsync(CancellationToken.None));
            try
            {
                await publishingDiscovery.Task.WaitAsync(watchdog.Token);
                Assert.IsTrue(engine.TryStartManualSongSelection(song.Id, song.Candidates![0], null, out _));
                engine.Enqueue(healthy, settings);
                await followingRootPrepared.Task.WaitAsync(watchdog.Token);
                if (!cancelOnAdmission) Assert.IsTrue(engine.CancelJob(song.Id));
                releaseDiscovery.Set();
                await WaitForAsync(engine, () => song.IsTerminal && healthy.IsTerminal);
                if (cancelOnAdmission) Assert.IsTrue(admissionCancellationAccepted,
                    "Request cancellation from root-start publication, after queue admission but before registration.");
                Assert.AreEqual(JobTerminalOutcome.Cancelled, song.TerminalOutcome);
                Assert.AreEqual(JobCancellationSource.UserRequestedJob, song.CancellationSource);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, healthy.TerminalOutcome);
                CollectionAssert.AreEqual(new[] { healthyPath }, attempted.ToArray(),
                    "A queued cancelled choice must not replace its token and start downloading.");
            }
            finally
            {
                releaseDiscovery.Set();
                engine.CompleteEnqueue();
                try { await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5))); }
                finally { System.IO.Directory.Delete(outputDir, recursive: true); }
            }
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task IdleManualCancellation_DoesNotWaitForUnrelatedRootCapacity(bool queuedFollower, bool failingCallback)
        {
            string outputDir = System.IO.Directory.CreateTempSubdirectory("sockseek-busy-picker-cancel-").FullName;
            const string unrelatedPath = @"Music\unrelated.bin";
            const string followerPath = @"Music\follower.bin";
            var gate = new TestHelpers.DownloadGate();
            var client = new ClientTests.MockSoulseekClient(
            [
                new SearchResponse("peer", 1, true, 100_000, 0,
                [TestHelpers.CreateSlFile(@"Music\Artist\01 - First.mp3", size: 128),
                 TestHelpers.CreateSlFile(unrelatedPath, size: 128), TestHelpers.CreateSlFile(followerPath, size: 128)]),
            ])
            {
                BeforeDownloadCompletesAsync = (username, path, ct) => path == unrelatedPath
                    ? gate.BlockAsync(username, path, ct) : Task.CompletedTask,
            };
            var settings = new DownloadSettings();
            settings.Output.ParentDir = outputDir;
            settings.Output.WriteIndex = false;
            var picker = new SongJob(new SongQuery { Artist = "Artist", Title = "First" })
            {
                DownloadBehaviorPolicy = new DownloadBehaviorPolicy { Song = DownloadBehavior.Manual },
            };
            var unrelated = new RemoteFileJob(TestHelpers.CreateAlbumFile("peer", unrelatedPath, size: 128).Candidate.Target);
            var follower = new RemoteFileJob(TestHelpers.CreateAlbumFile("peer", followerPath, size: 128).Candidate.Target);
            var engineSettings = new EngineSettings
            {
                Username = "u", Password = "p", ConcurrentJobs = 1, ConcurrentExtractors = 1,
                LogLevel = LogLevel.None,
            };
            var callbackFailure = new InvalidOperationException("A cancellation callback failed.");
            int loggedFailures = 0;
            Exception? loggedFailure = null;
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(
                new CallbackLogger((id, _, exception) =>
                {
                    if (id.Id != 3004) return;
                    loggedFailure = exception;
                    Interlocked.Increment(ref loggedFailures);
                })));
            await using var engine = new DownloadEngine(engineSettings,
                TestHelpers.CreateMockClientManager(client, engineSettings), loggerFactory: loggerFactory,
                retireTerminalWorkflows: true);
            var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Events.WorkflowRetired += change =>
            {
                if (change.WorkflowId == picker.WorkflowId) retired.TrySetResult();
            };
            engine.Enqueue(picker, settings);
            var running = engine.RunAsync(CancellationToken.None);
            try
            {
                await WaitForAsync(engine, () => picker.IsAwaitingSelection);
                engine.Enqueue(unrelated, settings);
                await gate.WaitForStartedCountAsync(1);
                if (queuedFollower) engine.Enqueue(follower, settings);
                using var registration = failingCallback
                    ? picker.Cts!.Token.Register(() => throw callbackFailure) : default;
                long cancellationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                Assert.IsTrue(engine.CancelJob(picker.Id));
                await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine($"Idle picker cancellation, queued follower={queuedFollower}: "
                    + $"{System.Diagnostics.Stopwatch.GetElapsedTime(cancellationStarted).TotalMilliseconds:F1} ms.");
                Assert.AreEqual(JobTerminalOutcome.Cancelled, picker.TerminalOutcome);
                Assert.AreEqual(JobCancellationSource.UserRequestedJob, picker.CancellationSource);
                Assert.IsTrue(unrelated.IsRunning, "Cancelling an idle picker must not wait for unrelated downloads.");
                Assert.IsFalse(unrelated.Cts!.IsCancellationRequested);
                if (queuedFollower) Assert.IsTrue(follower.IsPending);
                gate.Open();
                await WaitForAsync(engine, () => unrelated.IsTerminal && (!queuedFollower || follower.IsTerminal));
                Assert.AreEqual(JobTerminalOutcome.Succeeded, unrelated.TerminalOutcome);
                if (queuedFollower) Assert.AreEqual(JobTerminalOutcome.Succeeded, follower.TerminalOutcome);
            }
            finally
            {
                gate.Open();
                engine.CompleteEnqueue();
                try { await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5))); }
                finally { System.IO.Directory.Delete(outputDir, recursive: true); }
            }
            Assert.AreEqual(failingCallback ? 1 : 0, loggedFailures);
            if (failingCallback)
            {
                var aggregate = Assert.IsInstanceOfType<AggregateException>(loggedFailure);
                Assert.AreSame(callbackFailure, aggregate.InnerExceptions.Single(), "Log the full callback failure once.");
            }
        }

        [TestMethod]
        [DataRow(false, true)]
        [DataRow(true, true)]
        [DataRow(true, false)]
        public async Task ManualMusicExecution_CancellationRetiresButFailedAlbumCanRetry(bool albumMode, bool cancel)
        {
            string outputDir = System.IO.Directory.CreateTempSubdirectory("sockseek-manual-execution-").FullName;
            const string path = @"Music\Artist\Album\01 - First.mp3";
            var gate = new TestHelpers.DownloadGate();
            int attempts = 0;
            var client = new ClientTests.MockSoulseekClient(
            [
                new SearchResponse("first-peer", 1, true, 100_000, 0, [TestHelpers.CreateSlFile(path, size: 128)]),
                new SearchResponse("other-peer", 2, true, 50_000, 0, [TestHelpers.CreateSlFile(path, size: 128)]),
            ])
            {
                BeforeDownloadCompletesAsync = async (username, filename, ct) =>
                {
                    Interlocked.Increment(ref attempts);
                    if (username != "first-peer") return;
                    await gate.BlockAsync(username, filename, ct);
                    if (!cancel) throw new TransferRejectedException("The selected folder rejects this file.");
                },
            };
            var settings = new DownloadSettings();
            settings.Output.ParentDir = outputDir;
            settings.Output.WriteIndex = false;
            settings.Search.NoBrowseFolder = true;
            Job job = albumMode
                ? new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                : new SongJob(new SongQuery { Artist = "Artist", Title = "First" });
            job.DownloadBehaviorPolicy = new DownloadBehaviorPolicy { Default = DownloadBehavior.Manual };
            var engineSettings = new EngineSettings { Username = "u", Password = "p", LogLevel = LogLevel.None };
            await using var engine = new DownloadEngine(engineSettings,
                TestHelpers.CreateMockClientManager(client, engineSettings), retireTerminalWorkflows: true);
            bool closed = false;
            engine.Enqueue(job, settings);
            var running = engine.RunAsync(CancellationToken.None);
            try
            {
                await WaitForAsync(engine, () => job.IsAwaitingSelection);
                if (job is AlbumJob album)
                    Assert.IsTrue(engine.TryStartManualAlbumSelection(job.Id,
                        album.Results.Single(folder => folder.Username == "first-peer"), album.Query, null, out _));
                else
                    Assert.IsTrue(engine.TryStartManualSongSelection(job.Id,
                        ((SongJob)job).Candidates!.Single(file => file.Username == "first-peer"), null, out _));
                await gate.WaitForStartedCountAsync(1);
                if (cancel)
                {
                    Assert.IsTrue(engine.CancelJob(job.Id));
                    await WaitForAsync(engine, () => job.IsTerminal);
                    Assert.AreEqual(JobTerminalOutcome.Cancelled, job.TerminalOutcome);
                    Assert.AreEqual(JobCancellationSource.UserRequestedJob, job.CancellationSource);
                    Assert.AreEqual(1, attempts, "Cancelling a selected source must not try another source.");
                }
                else
                {
                    gate.Open();
                    await WaitForAsync(engine, () => job.IsAwaitingSelection || job.IsUnsuccessfulTerminal);
                    var failedAlbum = (AlbumJob)job;
                    Assert.AreEqual(1, attempts, "A rejected explicit source must return control to the caller.");
                    Assert.IsTrue(engine.TryStartManualAlbumSelection(job.Id,
                        failedAlbum.Results.Single(folder => folder.Username == "other-peer"), failedAlbum.Query, null, out _));
                    await WaitForAsync(engine, () => job.IsTerminal);
                    Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
                    Assert.AreEqual(2, attempts);
                }
                engine.CompleteEnqueue();
                closed = true;
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(0, engine.GetJobsByWorkflow(job.WorkflowId).Count,
                    "A cancelled selection must not remain as a retryable workflow.");
                Assert.AreEqual(0, engine.RetainedStateCounts.Contexts);
                Assert.AreEqual(0, engine.RetainedStateCounts.ManualSelections);
            }
            finally
            {
                gate.Open();
                if (!closed) engine.CompleteEnqueue();
                try { await IgnoreCancellation(running.WaitAsync(TimeSpan.FromSeconds(5))); }
                finally { System.IO.Directory.Delete(outputDir, recursive: true); }
            }
        }

        private sealed class CallbackLogger(Action<EventId,
            IEnumerable<KeyValuePair<string, object?>>, Exception?> observe) : ILoggerProvider, ILogger
        {
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose() { }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;
            public void Log<TState>(LogLevel level, EventId id,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> properties) observe(id, properties, exception);
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

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Id == extractJob.Id && change.ActivityPhase == JobActivityPhase.Extracting)
                        extractJob.Cancel(JobCancellationSource.UserRequestedJob);
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
                engine.Events.JobStateChanged += change =>
                {
                    if (change.ActivityPhase == JobActivityPhase.Downloading
                        && change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.Album
                        && engine.GetJob(change.Job.Id) is AlbumJob aj)
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
        public async Task AlbumGroups_CancelledChildAlbums_MarksParentContainersCancelled()
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

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-cancel-album-groups-" + Guid.NewGuid());
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
                downloadSettings.Search.MinGroupShares = 1;
                downloadSettings.Search.NoBrowseFolder = true;
                downloadSettings.Skip.SkipExisting = false;

                var songGroupsJob = new AlbumGroupsJob(new AlbumQuery { Artist = "ELO" });
                var client = new ClientTests.MockSoulseekClient(index);
                var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));

                JobList? groupList = null;
                engine.Events.JobRegistered += change =>
                {
                    if (change.ParentJobId == songGroupsJob.Id
                        && change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.JobList
                        && engine.GetJob(change.Job.Id) is JobList list)
                        groupList = list;
                };
                engine.Events.JobStateChanged += change =>
                {
                    if (groupList != null
                        && change.Job.Id == groupList.Id
                        && change.ActivityPhase == JobActivityPhase.RunningChildren)
                        groupList.Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(songGroupsJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await IgnoreCancellation(runTask);

                Assert.IsNotNull(groupList, "AlbumGroups should process matching albums through a registered JobList.");
                Assert.IsTrue(groupList!.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, groupList.FailureReason);
                Assert.IsTrue(songGroupsJob.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.Cancelled, songGroupsJob.FailureReason);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
