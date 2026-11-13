using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Transfers.Downloads.State;
using Directory = System.IO.Directory;
using System.Collections.Concurrent;

namespace Tests.Core
{
    [TestClass]
    public class DownloadFallbackTests
    {
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task MusicOutputsAreSettledBeforeRetirementAndIndexFailureDoesNotLoseDownloads(bool failIndex)
        {
            string root = Path.Combine(Path.GetTempPath(), "sockseek-output-finalization-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string indexPath = Path.Combine(root, "index.csv");
            string playlistPath = Path.Combine(root, "playlist.m3u8");
            if (failIndex) Directory.CreateDirectory(indexPath);
            try
            {
                var engineSettings = new EngineSettings { Username = "u", Password = "p", ConcurrentJobs = 1 };
                var settings = new DownloadSettings();
                settings.Output.ParentDir = root;
                settings.Output.WriteIndex = true;
                settings.Output.IndexFilePath = indexPath;
                settings.Output.WritePlaylist = true;
                settings.Output.M3uFilePath = playlistPath;
                settings.YtDlp.UseYtdlp = true;
                var first = new SongJob(new SongQuery { Artist = "Artist", Title = "First" });
                var second = new SongJob(new SongQuery { Artist = "Artist", Title = "Second" });
                var workflow = new JobList("Tracks", [first, second]);
                var messages = new ConcurrentBag<JobMessageChange>();
                bool retiredWithOutputs = false;
                await using var engine = new DownloadEngine(engineSettings,
                    TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings),
                    songDownloadFallback: new FakeSongDownloadFallback(), retireTerminalWorkflows: true);
                engine.Events.JobMessage += change => { if (change.Source == "output-files") messages.Add(change); };
                engine.Events.WorkflowRetired += _ =>
                {
                    retiredWithOutputs = System.IO.File.Exists(playlistPath)
                        && System.IO.File.ReadAllLines(playlistPath).Length == 2
                        && (failIndex ? messages.Count == 1 : System.IO.File.Exists(indexPath));
                };
                engine.Enqueue(workflow, settings);
                engine.CompleteEnqueue();
                await engine.RunAsync(CancellationToken.None);
                Assert.IsTrue(retiredWithOutputs, "Output success/failure must settle before workflow retirement.");
                Assert.AreEqual(JobTerminalOutcome.Succeeded, first.TerminalOutcome);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, second.TerminalOutcome);
                Assert.IsTrue(System.IO.File.Exists(first.DownloadPath));
                Assert.IsTrue(System.IO.File.Exists(second.DownloadPath));
                Assert.AreEqual(failIndex ? 1 : 0, messages.Count);
                if (failIndex)
                {
                    Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Error, messages.Single().Level);
                    Assert.AreEqual(workflow.Id, messages.Single().Job.Id);
                }
                else
                {
                    var saved = new DownloadOutputFile(indexPath, workflow, OutputFileKind.Index, true, readOnly: true);
                    Assert.AreEqual(first.DownloadPath, saved.PreviousRunResult(first)?.DownloadPath);
                    Assert.AreEqual(second.DownloadPath, saved.PreviousRunResult(second)?.DownloadPath);
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [TestMethod]
        [DataRow("automatic")]
        [DataRow("exhausted")]
        [DataRow("song")]
        [DataRow("file")]
        [DataRow("directory")]
        public async Task FailureFeedback_RechecksAutomaticChoicesButHonorsExactTargets(string mode)
        {
            string output = Path.Combine(Path.GetTempPath(), "sockseek-failure-selection-" + Guid.NewGuid().ToString("N"));
            var files = Enumerable.Range(0, 4).Select(i =>
                TestHelpers.CreateSlFile($@"Music\{i}\Artist - Track.mp3", size: 512, length: 180)).ToArray();
            var bad = new SearchResponse("peer", 1, true, 10_000_000, 0, files);
            var good = new SearchResponse("other", 1, true, 100, 0, [files[0]]);
            var client = new ClientTests.MockSoulseekClient([bad, good],
                failingUsers: mode == "exhausted" ? ["peer", "other"] : mode == "automatic" ? ["peer"] : []);
            var candidate = SoulseekSearchAdapter.ToFileCandidate(bad, files[0]);
            Job job = mode switch
            {
                "file" => new RemoteFileJob(candidate.Target),
                "directory" => new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
                    new DirectoryTransferPlan("Music", [new DirectoryTransferEntry(candidate.Target, [])]))),
                "song" => new SongJob(new SongQuery { Artist = "Artist", Title = "Track" }) { ExactTarget = candidate.Target },
                _ => new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
                {
                    Candidates = files.Select(file => SoulseekSearchAdapter.ToFileCandidate(bad, file))
                        .Append(SoulseekSearchAdapter.ToFileCandidate(good, files[0])).ToList(),
                },
            };
            try
            {
                var settings = new DownloadSettings();
                settings.Output.ParentDir = output;
                settings.Transfer.UnknownErrorRetries = 0;
                settings.Transfer.MaxDownloadRetries = 10;
                var engineSettings = new EngineSettings { Username = "u", Password = "p" };
                await using var engine = new DownloadEngine(engineSettings,
                    TestHelpers.CreateMockClientManager(client, engineSettings));
                var attempts = new List<string>();
                engine.Events.DownloadStarted += change => attempts.Add(change.Target.Username);
                engine.Events.JobRegistered += change =>
                {
                    if (mode is not ("automatic" or "exhausted") && change.Job.Id == job.Id)
                        engine.GetPeerFailureCounts(job.WorkflowId)["peer"] = 3;
                };
                engine.Enqueue(job, settings);
                engine.CompleteEnqueue();
                await engine.RunAsync(CancellationToken.None);
                Assert.AreEqual(mode == "exhausted" ? JobTerminalOutcome.Failed : JobTerminalOutcome.Succeeded, job.TerminalOutcome, job.FailureMessage);
                string[] expected = mode switch
                {
                    "automatic" => ["peer", "peer", "other"],
                    "exhausted" => ["peer", "peer", "other", "peer"],
                    _ => ["peer"],
                };
                CollectionAssert.AreEqual(expected, attempts);
                Assert.AreEqual(mode == "automatic" ? 2 : 3, engine.GetPeerFailureCounts(job.WorkflowId)["peer"],
                    "Success must not reduce prior failures.");
            }
            finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
        }

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
                var transfersStarted = new List<DownloadStartedChange>();
                app.Events.DownloadStarted += transfersStarted.Add;
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, songJob.TerminalOutcome);
                Assert.AreEqual("gooduser", songJob.ResolvedTarget?.Username, "SongJob should have fallen back to gooduser after failuser failed.");
                Assert.AreEqual(SongDownloadSource.Soulseek, songJob.DownloadSource);
                Assert.AreEqual(2, transfersStarted.Count);
                CollectionAssert.AreEqual(
                    new[] { "failuser", "gooduser" },
                    transfersStarted.Select(change => change.Target.Identity.Username).ToArray());
                Assert.AreEqual(2, transfersStarted.Select(change => change.TransferId).Distinct().Count());
                Assert.AreEqual(1, app.GetPeerFailureCounts(songJob.WorkflowId)["failuser"],
                    "Exhausting a peer's transport retries counts as one failed transfer.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        [TestCategory("Load")]
        [DataRow(false)]
        [DataRow(true)]
        public async Task HundredCandidateSongFallbackPreservesAttemptsAndExactOutput(bool group)
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "sockseek-long-song-fallback-" + Guid.NewGuid().ToString("N"));
            const int candidateCount = 100;
            const int size = 1_024;
            var responses = Enumerable.Range(0, candidateCount).Select(index => new SearchResponse(
                $"Péér {index:000}", 1, true, 100_000_000 - index * 700_000, 0,
                [TestHelpers.CreateSlFile($@"共有\Option {index:000}\Artist - Song.mp3", size: size, length: 180)]))
                .ToList();
            var client = new ClientTests.MockSoulseekClient(responses,
                failingUsers: responses.Take(candidateCount - 1).Select(response => response.Username));
            try
            {
                var settings = new DownloadSettings();
                settings.Extraction.Input = "Artist - Song";
                settings.Extraction.RequestedMode = ExtractionMode.Song;
                settings.Search.GroupResults = group;
                settings.Search.MinGroupShares = 1;
                settings.Transfer.MaxDownloadRetries = candidateCount;
                settings.Transfer.UnknownErrorRetries = 0;
                settings.Output.ParentDir = outputDir;
                var engineSettings = new EngineSettings { Username = "u", Password = "p" };
                await using var engine = new DownloadEngine(engineSettings,
                    TestHelpers.CreateMockClientManager(client, engineSettings));
                var started = new List<DownloadStartedChange>();
                var failed = new List<DownloadAttemptFailedChange>();
                var completed = new List<TransferCompletedChange>();
                engine.Events.DownloadStarted += started.Add;
                engine.Events.DownloadAttemptFailed += failed.Add;
                engine.Events.TransferCompleted += completed.Add;
                var root = new ExtractJob(settings.Extraction.Input, settings.Extraction.InputType);
                engine.Enqueue(root, settings);
                engine.CompleteEnqueue();

                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                await engine.RunAsync(CancellationToken.None);
                Console.WriteLine($"100-candidate {(group ? "song-groups" : "ordinary")} Song fallback: {elapsed.Elapsed.TotalMilliseconds:F1} ms.");

                SongJob[] songs = root.Result is SongGroupsJob grouped
                    ? grouped.Songs.ToArray() : engine.Queue.AllSongs().ToArray();
                Assert.AreEqual(1, songs.Length, $"Root {root.TerminalOutcome}: {root.FailureMessage}; result {root.Result?.GetType().Name}: {root.Result?.TerminalOutcome}, {root.Result?.FailureMessage}");
                SongJob song = songs.Single();
                Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
                Assert.AreEqual(candidateCount, client.DownloadCallCount);
                CollectionAssert.AreEqual(responses.Select(response => response.Username).ToArray(),
                    started.Select(change => change.Target.Identity.Username).ToArray());
                Assert.AreEqual(candidateCount, started.Select(change => change.TransferId).Distinct().Count());
                Assert.AreEqual(candidateCount - 1, failed.Count);
                Assert.AreEqual(1, completed.Count);
                Assert.AreEqual(responses[^1].Username, song.ResolvedTarget!.Username);
                Assert.AreEqual(responses[^1].Files.Single().Filename, song.ResolvedTarget.Filename);
                Assert.AreEqual(song.DownloadPath, completed[0].FinalLocalPath);
                CollectionAssert.AreEqual(Enumerable.Range(0, size).Select(index => (byte)(index % 256)).ToArray(),
                    await System.IO.File.ReadAllBytesAsync(song.DownloadPath!));
                Assert.AreEqual(1, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                    "Failed choices must not leave final or staging files behind.");
                Assert.IsTrue(responses.Take(candidateCount - 1).All(response =>
                    engine.GetPeerFailureCounts(song.WorkflowId).GetValueOrDefault(response.Username) > 0),
                    "Failed peers must be downranked within this workflow.");
                Assert.AreEqual(0, engine.GetPeerFailureCounts(song.WorkflowId).GetValueOrDefault(responses[^1].Username),
                    "Successful transfers must not create failure credit.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FailedPeerIsDownrankedOnlyWithinItsWorkflow(bool retireWorkflows)
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "sockseek-workflow-downrank-" + Guid.NewGuid().ToString("N"));
            var files = new[] { "First", "Second", "Third", "Fourth" }.Select(title =>
                TestHelpers.CreateSlFile($@"Music\Artist - {title}.mp3", size: 512, length: 180)).ToArray();
            var client = new ClientTests.MockSoulseekClient(
                [new SearchResponse("failed peer", 1, true, 10_000_000, 0, files),
                 new SearchResponse("good peer", 1, true, 100, 0, files)], failingUsers: ["failed peer"]);
            try
            {
                var settings = new DownloadSettings();
                settings.Output.ParentDir = outputDir;
                settings.Search.NecessaryCond.StrictArtist = true;
                settings.Search.NecessaryCond.StrictTitle = true;
                settings.Transfer.UnknownErrorRetries = 0;
                var engineSettings = new EngineSettings
                {
                    Username = "u", Password = "p", ConcurrentJobs = 1, ConcurrentExtractors = 1,
                };
                await using var engine = new DownloadEngine(engineSettings,
                    TestHelpers.CreateMockClientManager(client, engineSettings), retireTerminalWorkflows: retireWorkflows);
                var first = new SongJob(new SongQuery { Artist = "Artist", Title = "First" });
                var second = new SongJob(new SongQuery { Artist = "Artist", Title = "Second" });
                var third = new SongJob(new SongQuery { Artist = "Artist", Title = "Third" });
                var fourth = new SongJob(new SongQuery { Artist = "Artist", Title = "Fourth" });
                var workflow = new JobList(jobs: [first, second, third]);
                var attempts = new ConcurrentBag<DownloadStartedChange>();
                var terminalCounts = new ConcurrentDictionary<Guid, Dictionary<string, int>>();
                engine.Events.DownloadStarted += attempts.Add;
                engine.Events.WorkflowRetired += change => terminalCounts[change.WorkflowId] =
                    new Dictionary<string, int>(engine.GetPeerFailureCounts(change.WorkflowId), StringComparer.Ordinal);
                engine.Enqueue(workflow, settings);
                engine.Enqueue(fourth, settings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                foreach (SongJob song in new[] { first, second, third, fourth })
                {
                    Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome, song.FailureMessage);
                    Assert.AreEqual(512L, new FileInfo(song.DownloadPath!).Length);
                }
                Assert.AreEqual(first.WorkflowId, second.WorkflowId);
                Assert.AreEqual(first.WorkflowId, third.WorkflowId);
                Assert.AreNotEqual(first.WorkflowId, fourth.WorkflowId);
                string[] Peers(Guid workflowId) => attempts.OrderBy(change => change.Sequence)
                    .Where(change => change.Job.WorkflowId == workflowId)
                    .Select(change => change.Target.Identity.Username).ToArray();
                CollectionAssert.AreEqual(new[] { "failed peer", "good peer", "failed peer", "good peer", "good peer" }, Peers(first.WorkflowId),
                    "Two failures downrank the peer for the third song.");
                CollectionAssert.AreEqual(new[] { "failed peer", "good peer" }, Peers(fourth.WorkflowId),
                    "A separate workflow must start without another workflow's downranking.");
                foreach (var (id, failures) in new[] { (first.WorkflowId, 2), (fourth.WorkflowId, 1) })
                {
                    IReadOnlyDictionary<string, int> counts = retireWorkflows
                        ? terminalCounts[id] : engine.GetPeerFailureCounts(id);
                    Assert.AreEqual(failures, counts["failed peer"]);
                    Assert.AreEqual(0, counts.GetValueOrDefault("good peer"));
                    if (retireWorkflows)
                        Assert.AreEqual(0, engine.GetPeerFailureCounts(id).Count,
                            "Workflow retirement releases counters after readers capture their terminal inputs.");
                }
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
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
                dl.Output.NameFormat = "Fallback/{stitle}";

                var fakeFallback = new FakeSongDownloadFallback();
                var song = new SongJob(new SongQuery
                {
                    Artist = "Lavish Life",
                    Title = "TEXAS HOLD 'EM",
                });
                var app = new DownloadEngine(eng, new SoulseekClientManager(eng), songDownloadFallback: fakeFallback);
                FallbackTransferStartedChange? transferStarted = null;
                TransferCompletedChange? transferCompleted = null;
                TransferAttemptStartedChange? attemptStarted = null;
                TransferAttemptCompletedChange? attemptCompleted = null;
                JobMessageChange? fallbackMessage = null;
                bool finalPathExistedAtTerminalPublication = false;
                app.Events.FallbackTransferStarted += change => transferStarted = change;
                app.Events.TransferAttemptStarted += change => attemptStarted = change;
                app.Events.TransferAttemptCompleted += change => attemptCompleted = change;
                app.Events.JobMessage += change =>
                {
                    if (change.Job.Id == song.Id && change.Message == "running fallback downloader")
                        fallbackMessage = change;
                };
                app.Events.TransferCompleted += change =>
                {
                    transferCompleted = change;
                    finalPathExistedAtTerminalPublication = System.IO.File.Exists(change.FinalLocalPath);
                };
                app.Enqueue(song, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(1, fakeFallback.Calls, "Song fallback should run after the mock Soulseek search returns no results.");
                Assert.AreEqual(JobActivityPhase.RunningFallback, fakeFallback.ObservedPhase, "Song fallback should run under the fallback activity phase.");
                Assert.IsNotNull(fallbackMessage, "Fallback should publish an info-level job activity message.");
                Assert.AreEqual(LogLevel.Information, fallbackMessage.Level);
                Assert.IsNull(fallbackMessage.Source);
                Assert.AreEqual(JobLifecycleState.Terminal, song.LifecycleState);
                Assert.AreEqual(JobActivityPhase.None, song.ActivityPhase);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
                Assert.AreEqual(SongDownloadSource.Fallback, song.DownloadSource);
                Assert.IsNull(song.ResolvedTarget);
                Assert.IsTrue(System.IO.File.Exists(song.DownloadPath), $"Expected fallback output at {song.DownloadPath}");
                Assert.IsNotNull(transferStarted);
                Assert.IsNotNull(transferCompleted);
                Assert.AreEqual(transferStarted.Transfer.Id, transferCompleted.Transfer.Id);
                Assert.AreEqual(TransferSnapshotSource.Fallback, transferCompleted.Transfer.Source);
                Assert.IsNull(transferCompleted.Transfer.Candidate);
                Assert.AreEqual(song.DownloadPath, transferCompleted.FinalLocalPath);
                Assert.IsTrue(finalPathExistedAtTerminalPublication, "Fallback completion must be published only after the final path exists.");
                Assert.IsNotNull(attemptStarted);
                Assert.IsNotNull(attemptCompleted);
                Assert.AreEqual(TransferAttemptSource.Fallback, attemptStarted.Source);
                Assert.AreEqual(attemptStarted.AttemptId, attemptCompleted.AttemptId);
                Assert.AreEqual(transferStarted.Transfer.Id, attemptStarted.Transfer.Id);
            }
            finally
            {
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
                var index = new DownloadOutputFile(dl.Output.IndexFilePath, queue, OutputFileKind.Index, true);

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

                var clientManager = new SoulseekClientManager(
                    eng,
                    testClient,
                    inboundRouter: null,
                    monitorDelay: static (_, _) => Task.CompletedTask);
                var app = new DownloadEngine(eng, clientManager);
                var attemptsStarted = new List<TransferAttemptStartedChange>();
                var attemptsFailed = new List<TransferAttemptFailedChange>();
                var attemptsCompleted = new List<TransferAttemptCompletedChange>();
                TransferCompletedChange? transferCompleted = null;
                app.Events.TransferAttemptStarted += attemptsStarted.Add;
                app.Events.TransferAttemptFailed += attemptsFailed.Add;
                app.Events.TransferAttemptCompleted += attemptsCompleted.Add;
                app.Events.TransferCompleted += change => transferCompleted = change;
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songJob = app.Queue.AllSongs().FirstOrDefault();
                Assert.IsNotNull(songJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, songJob.TerminalOutcome);
                Assert.AreEqual("flakyuser", songJob.ResolvedTarget?.Username);
                Assert.IsTrue(testClient.DownloadCallCount >= 2, "Disconnect retry should attempt the same candidate again after reconnect.");
                Assert.IsTrue(attemptsStarted.Count >= 2);
                Assert.AreEqual(attemptsStarted.Count - 1, attemptsFailed.Count);
                Assert.AreEqual(1, attemptsCompleted.Count);
                Assert.AreEqual(attemptsStarted.Count, attemptsStarted.Select(attempt => attempt.AttemptId).Distinct().Count());
                Assert.IsTrue(attemptsStarted.All(attempt => attempt.Transfer.Id == attemptsStarted[0].Transfer.Id));
                Assert.AreEqual(attemptsStarted[0].AttemptId, attemptsFailed[0].AttemptId);
                Assert.AreEqual(attemptsStarted[^1].AttemptId, attemptsCompleted[0].AttemptId);
                Assert.IsNotNull(transferCompleted);
                Assert.AreEqual(attemptsStarted[0].Transfer.Id, transferCompleted.Transfer.Id);
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
        [DataRow(true)]
        [DataRow(false)]
        public async Task DuplicateDownloadCache_UsesOrganizedPathOnlyWhenSkippingEnabled(bool skipExisting)
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
                dl.Output.NameFormat = "{snum}/{artist}/{title}";
                dl.Output.WriteIndex = false;
                dl.Skip.SkipExisting = skipExisting;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                var completedTransfers = new ConcurrentQueue<TransferCompletedChange>();
                app.Events.TransferCompleted += completedTransfers.Enqueue;
                app.Enqueue(new ExtractJob(listPath, InputType.List), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var songs = app.Queue.AllSongs().ToList();
                Assert.AreEqual(2, songs.Count);
                Assert.IsTrue(songs.All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
                Assert.AreEqual(skipExisting ? 1 : 2, testClient.DownloadCallCount);
                Assert.IsTrue(System.IO.File.Exists(songs[0].DownloadPath), "First song should point at the organized file.");
                Assert.IsTrue(System.IO.File.Exists(songs[1].DownloadPath), "Second song should copy from the organized cache path.");
                Assert.AreEqual(skipExisting ? 1 : 2, completedTransfers.Count);
                foreach (var transfer in completedTransfers)
                    Assert.AreEqual(songs.Single(song => song.Id == transfer.Job.Id).DownloadPath, transfer.FinalLocalPath);
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
                using var changes = new TestSignal().Subscribe<JobStateChangedChange>(
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
        public async Task IndependentAlbumRequestsDownloadAudioAndAncillaryFilesWhenSkippingDisabled()
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
                Assert.AreEqual(4, testClient.DownloadCallCount);
                Assert.IsTrue(firstAlbum.TrackJobs.Any(file => file.IsNotAudio && System.IO.File.Exists(file.DownloadPath)));
                Assert.IsTrue(secondAlbum.TrackJobs.Any(file => file.IsNotAudio && System.IO.File.Exists(file.DownloadPath)));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(4)]
        public async Task AlbumNameFormat_FinalizesFilesAndPreservesOccupiedDestinations(int existingCount)
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
                dl.Skip.SkipExisting = true;
                var names = new[] { "01. Artist - Song.flac", "cover.jpg", "booklet.pdf", "rip.log" };
                var finalDir = Path.Combine(outputDir, "Organized", "Album");
                Directory.CreateDirectory(finalDir);
                foreach (var name in names.Take(existingCount))
                    await System.IO.File.WriteAllTextAsync(Path.Combine(finalDir, name), "keep");

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                var completedTransfers = new ConcurrentQueue<TransferCompletedChange>();
                app.Events.TransferCompleted += completedTransfers.Enqueue;
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome, album.FailureMessage + "\n" + string.Join("\n", album.TrackJobs.Select(file => $"{file.TerminalOutcome}: {file.FailureMessage} {file.FailureDetail}")));
                Assert.AreEqual(4, album.TrackJobs.Count);
                Assert.IsTrue(album.TrackJobs.All(file => file.IsSuccessfulTerminal));
                Assert.AreEqual(existingCount, album.ProgressSummary.SkippedFileCount);
                Assert.AreEqual(4 - existingCount, album.ProgressSummary.DownloadedFileCount);
                Assert.AreEqual(0, album.ProgressSummary.FailedFileCount);
                foreach (var name in names.Take(existingCount))
                    Assert.AreEqual("keep", await System.IO.File.ReadAllTextAsync(Path.Combine(finalDir, name)));
                foreach (var filename in new[] { "01. Artist - Song.flac", "cover.jpg", "booklet.pdf", "rip.log" })
                    Assert.IsTrue(System.IO.File.Exists(Path.Combine(outputDir, "Organized", "Album", filename)), $"Missing finalized album file '{filename}'.");

                Assert.IsTrue(album.TrackJobs.All(file =>
                    !string.IsNullOrEmpty(file.DownloadPath)
                    && !Utils.IsInDirectory(file.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true)));
                Assert.AreEqual(4, completedTransfers.Count);
                foreach (var transfer in completedTransfers)
                    Assert.AreEqual(album.TrackJobs.Single(file => file.Id == transfer.Job.Id).DownloadPath,
                        transfer.FinalLocalPath, "Transfer history must use each file's organized destination.");
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
            var completedTransfers = new ConcurrentQueue<TransferCompletedChange>();
            var outputUpdates = new ConcurrentQueue<TransferOutputChangedChange>();

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
                app.Events.TransferCompleted += completedTransfers.Enqueue;
                app.Events.TransferOutputChanged += outputUpdates.Enqueue;
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
                Assert.AreEqual(2, completedTransfers.Count,
                    "Organization failure must still finish the downloaded payloads' transfer histories.");
                Assert.AreEqual(1, outputUpdates.Count, "Only the organized audio moves during incomplete-album cleanup.");
                foreach (var transfer in completedTransfers)
                {
                    var update = outputUpdates.SingleOrDefault(change => change.Transfer.Id == transfer.Transfer.Id);
                    var current = update?.Transfer ?? transfer.Transfer;
                    Assert.AreEqual(album.TrackJobs.Single(file => file.Id == transfer.Job.Id).DownloadPath, current.LocalPath);
                    Assert.IsTrue(System.IO.File.Exists(current.LocalPath));
                    Assert.AreEqual(transfer.Transfer.TerminalOutcome, current.TerminalOutcome);
                    Assert.AreEqual(transfer.Transfer.StartedAtUtc, current.StartedAtUtc);
                    Assert.AreEqual(transfer.Transfer.CompletedAtUtc, current.CompletedAtUtc);
                    Assert.AreEqual(transfer.Transfer.BytesTransferred, current.BytesTransferred);
                    if (update != null) Assert.IsTrue(update.Transfer.Revision > transfer.Transfer.Revision);
                }
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
                var completedTransfers = new ConcurrentQueue<TransferCompletedChange>();
                app.Events.TransferCompleted += completedTransfers.Enqueue;
                app.Enqueue(album, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                var image = album.TrackJobs.Single(file => file.IsNotAudio);
                Assert.AreEqual(Path.GetFullPath(outputDir), Path.GetFullPath(album.DownloadPath!));
                Assert.AreEqual(Path.Combine(outputDir, "cover.jpg"), image.DownloadPath);
                Assert.IsTrue(System.IO.File.Exists(image.DownloadPath));
                Assert.IsFalse(Utils.IsInDirectory(image.DownloadPath, Path.Combine(outputDir, ".sockseek-staging"), strict: true));
                Assert.AreEqual(image.DownloadPath, completedTransfers.Single().FinalLocalPath);
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AlbumJob_SucceedsWhenOptionalAlbumArtDownloadFails(bool unrelatedExtremeDuration)
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-album-art-optional-fail-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var audio = TestHelpers.CreateSlFile(@"Music\Artist\Album\01. Artist - Song.mp3", size: 18000,
                length: unrelatedExtremeDuration ? null : 180);
            var cover = TestHelpers.CreateSlFile(@"Music\Artist\Album\cover.jpg", size: 4096);
            var files = new List<Soulseek.File> { audio, cover };
            if (unrelatedExtremeDuration)
                files.Add(TestHelpers.CreateSlFile(@"Music\Artist\Album\02. Artist - Second.mp3", size: 18000));
            var responses = new List<SearchResponse> { new("user1", 1, true, 100, 0, files) };
            if (unrelatedExtremeDuration)
                responses.Add(new SearchResponse("unrelated", 2, true, 1, 0,
                [
                    TestHelpers.CreateSlFile(@"Other\Artist\Album\01. Artist - Song.mp3"),
                    TestHelpers.CreateSlFile(@"Other\Artist\Album\02. Artist - Second.mp3", length: int.MaxValue),
                ]));
            var testClient = new ClientTests.MockSoulseekClient(responses)
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
                Assert.AreEqual("user1", album.ResolvedTarget?.Username);
                Assert.AreEqual(audio.Size, new FileInfo(Path.Combine(outputDir, "Album", "01. Artist - Song.mp3")).Length);
                if (unrelatedExtremeDuration)
                {
                    Assert.IsTrue(album.Results.Any(folder => folder.Username == "unrelated"));
                    Assert.AreEqual(18_000L, new FileInfo(Path.Combine(outputDir, "Album", "02. Artist - Second.mp3")).Length);
                }

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
            var booklet = TestHelpers.CreateSlFile(@"Music\Album\Artist - booklet.pdf", size: 128);

            var resp1 = new SearchResponse("failuser", 1, true, 10000000, 0, [file1, booklet]);
            var resp2 = new SearchResponse("gooduser", 1, true, 100, 0, [file2]);

            var testClient = new ClientTests.MockSoulseekClient([resp1, resp2]);
            var bookletDownloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            testClient.BeforeDownloadStartsAsync = async (username, filename, ct) =>
            {
                if (username != "failuser" || !filename.EndsWith(".mp3", StringComparison.Ordinal)) return;
                await bookletDownloaded.Task.WaitAsync(ct);
                throw new TransferRejectedException("The audio disappeared after the booklet finished.");
            };

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist, album=Album";
                dl.Extraction.IsAlbum = true;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "Organized/{filename}";

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                var completedTransfers = new ConcurrentQueue<TransferCompletedChange>();
                var failedTransfers = new ConcurrentQueue<TransferFailedChange>();
                app.Events.TransferCompleted += completedTransfers.Enqueue;
                app.Events.TransferFailed += failedTransfers.Enqueue;
                app.Events.JobExecutionCompleted += change =>
                {
                    if (change.Job.Payload is SongJobSnapshotPayload { File.DownloadPath: { } path }
                        && path.EndsWith(".pdf", StringComparison.Ordinal))
                        bookletDownloaded.TrySetResult();
                };
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.RunAsync(watchdog.Token);

                var albumJob = app.Queue.AllJobs().OfType<AlbumJob>().FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, albumJob.TerminalOutcome);
                Assert.AreEqual("gooduser", albumJob.ResolvedTarget?.Username, "AlbumJob should have fallen back to gooduser's folder after failuser failed.");
                Assert.AreEqual(2, completedTransfers.Count,
                    "The downloaded booklet still needs a terminal transfer when its album candidate is replaced.");
                Assert.AreEqual(1, failedTransfers.Count);
                foreach (var transfer in completedTransfers)
                    Assert.IsTrue(System.IO.File.Exists(transfer.FinalLocalPath), transfer.FinalLocalPath);
                Assert.AreEqual(1, completedTransfers.Count(change => change.FinalLocalPath.EndsWith(".pdf", StringComparison.Ordinal)));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SongGroupsJob_FallsBackToNextCandidate_OnDownloadFailure()
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
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var aggJob = app.Queue.AllJobs().OfType<SongGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob);
                var song = aggJob.Songs.FirstOrDefault();
                Assert.IsNotNull(song);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
                Assert.AreEqual("gooduser", song.ResolvedTarget?.Username, "SongGroups song bucket should have fallen back to gooduser.");
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
                app.Events.DownloadAttemptFailed += change =>
                {
                    attemptException = change.Exception.Detail;
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
                Assert.IsTrue(app.GetPeerFailureCounts(songJob.WorkflowId).GetValueOrDefault("user1") == 0,
                    "A blocked local destination must not downrank the remote peer.");
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
                app.Events.JobStatus += change =>
                {
                    if (change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.Album)
                        albumStatuses.Add(change.Status);
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
        public async Task OutputFinalizer_FailsIfOrganizerLeavesOwnedFileInsideStaging()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-name-format-staging-invariant-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "";
                var candidate = SoulseekSearchAdapter.ToFileCandidate(
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
                var organizer = new FileManager(
                    song,
                    dl.Output,
                    dl.Extraction,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<FileManager>.Instance);
                using var files = new DownloadFileOperations(2);
                var finalizer = new OutputFinalizer(files);

                var result = await finalizer.FinalizeSongPlacementAsync(
                    song,
                    song,
                    JobOutcome.Done(stagingPath, candidate),
                    organizer,
                    finalizePlacement: true,
                    allowOverwrite: !dl.Skip.SkipExisting);

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
        public async Task OutputFinalizer_DoesNotReplaceAPathCreatedAfterSkipEvaluation()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-name-format-collision-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                var dl = new DownloadSettings();
                dl.Output.ParentDir = outputDir;
                dl.Output.NameFormat = "{stitle}";
                dl.Skip.SkipExisting = true;
                var candidate = SoulseekSearchAdapter.ToFileCandidate(
                    new SearchResponse("user1", 1, true, 100, 0, []),
                    TestHelpers.CreateSlFile(@"Music\Artist - Payload.xyz", size: 10_000));
                string stagingPath = Path.Combine(
                    outputDir,
                    ".sockseek-staging",
                    Guid.NewGuid().ToString("N"),
                    "Artist - Payload.xyz");
                string finalPath = Path.Combine(outputDir, "Payload.xyz");
                Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
                System.IO.File.WriteAllText(stagingPath, "new payload");
                System.IO.File.WriteAllText(finalPath, "existing payload");
                var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Payload" })
                {
                    Config = dl,
                    ResolvedTarget = candidate,
                    DownloadPath = stagingPath,
                };
                using var files = new DownloadFileOperations(2);
                var finalizer = new OutputFinalizer(files);
                var organizer = new FileManager(
                    song,
                    dl.Output,
                    dl.Extraction,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<FileManager>.Instance);

                var result = await finalizer.FinalizeSongPlacementAsync(
                    song,
                    song,
                    JobOutcome.Done(stagingPath, candidate),
                    organizer,
                    finalizePlacement: true,
                    allowOverwrite: !dl.Skip.SkipExisting);

                Assert.AreEqual(JobTerminalOutcome.Skipped, result.Outcome.TerminalOutcome);
                Assert.AreEqual(JobSkipReason.AlreadyExists, result.Outcome.SkipReason);
                Assert.AreEqual(finalPath, result.Outcome.DownloadPath);
                Assert.AreEqual("existing payload", System.IO.File.ReadAllText(finalPath));
                Assert.IsFalse(System.IO.File.Exists(stagingPath));
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

                using var changes = new TestSignal().Subscribe<JobStateChangedChange>(
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
                app.Events.JobStatus += change =>
                {
                    if (change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.Album)
                        albumStatuses.Add(change.Status);
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
        public async Task AlbumGroupsJob_FallsBackToNextFolder_OnDownloadFailure()
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
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;
                dl.Search.NoBrowseFolder = true;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var aggAlbumJob = app.Queue.AllJobs().OfType<AlbumGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggAlbumJob);
                var albumJob = aggAlbumJob.Albums.FirstOrDefault();
                Assert.IsNotNull(albumJob);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, albumJob.TerminalOutcome);
                Assert.AreEqual("gooduser", albumJob.ResolvedTarget?.Username, "Grouped album bucket should have fallen back to gooduser.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumGroupsJob_FailsWhenGeneratedAlbumDownloadFails()
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
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;
                dl.Search.NoBrowseFolder = true;
                dl.Transfer.MaxDownloadRetries = 1;
                dl.Output.ParentDir = outputDir;

                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var aggAlbumJob = app.Queue.AllJobs().OfType<AlbumGroupsJob>().FirstOrDefault();
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
        public async Task SongGroupsJob_MixedChildOutcomes_CompletesWithPartialSuccess()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-partial-group-" + Guid.NewGuid());
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
                dl.Search.MinGroupShares = 1;
                dl.Transfer.MaxDownloadRetries = 1;
                dl.Output.ParentDir = outputDir;

                var group = new SongGroupsJob(new SongQuery { Artist = "Artist" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(group, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.PartialSuccess, group.TerminalOutcome);
                Assert.IsTrue(group.Songs.Any(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
                Assert.IsTrue(group.Songs.Any(song => song.TerminalOutcome == JobTerminalOutcome.Failed));
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumGroupsJob_MixedChildOutcomes_CompletesWithPartialSuccess()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-partial-albumgroup-" + Guid.NewGuid());
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
                dl.Search.MinGroupShares = 1;
                dl.Search.NoBrowseFolder = true;
                dl.Transfer.MaxDownloadRetries = 1;
                dl.Output.ParentDir = outputDir;

                var group = new AlbumGroupsJob(new AlbumQuery { Artist = "Artist" });
                var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app.Enqueue(group, dl);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.PartialSuccess, group.TerminalOutcome);
                Assert.IsTrue(group.Albums.Any(album => album.TerminalOutcome == JobTerminalOutcome.Succeeded));
                Assert.IsTrue(group.Albums.Any(album => album.TerminalOutcome == JobTerminalOutcome.Failed));
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
                CancellationToken ct,
                Action<FallbackTransferDescriptor>? transferStarting = null)
            {
                Calls++;
                ObservedPhase = song.ActivityPhase;
                var path = outputPath ?? organizer.GetSavePathNoExt($"{song.Query.Artist} - {song.Query.Title}.mp3") + ".mp3";
                transferStarting?.Invoke(new FallbackTransferDescriptor("fake", song.Query.ToString(), path));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllBytes(path, TestHelpers.EmptyMp3Bytes);
                return Task.FromResult<JobOutcome?>(JobOutcome.Done(path, downloadSource: SongDownloadSource.Fallback));
            }
        }
    }
}
