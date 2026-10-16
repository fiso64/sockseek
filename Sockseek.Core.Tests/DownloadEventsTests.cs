using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Soulseek;

namespace Tests.Eventing
{
    [TestClass]
    public class DownloadEventsTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext _)
        {
        }

        private static async Task CompleteRunWithBlockedDownloads(TestHelpers.DownloadGate downloadGate, Task runTask)
        {
            downloadGate.Open();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [TestMethod]
        public void DownloadEvents_DownloadLifecycleChangesShareLogicalTransferId()
        {
            var events = new DownloadEvents();
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            song.EnsureDisplayId();
            var file = TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", size: 10_000, length: 180);
            var response = new SearchResponse("user", 1, true, 100_000, 0, [file]);
            var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
            var transferId = Guid.NewGuid();
            var outputPath = "C:/downloads/Track.mp3";
            var attemptOutputPath = outputPath + ".incomplete";

            DownloadStartedChange? started = null;
            DownloadProgressedChange? progressed = null;
            DownloadStateChangedChange? stateChanged = null;
            DownloadAttemptFailedChange? failed = null;
            TransferCompletedChange? completed = null;
            var progressEventCount = 0;

            events.DownloadStarted += change => started = change;
            events.DownloadProgress += change =>
            {
                progressed = change;
                progressEventCount++;
            };
            events.DownloadStateChanged += change => stateChanged = change;
            events.DownloadAttemptFailed += change => failed = change;
            events.TransferCompleted += change => completed = change;

            Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, outputPath);
            Invoke(events, "RaiseDownloadProgressWithSpeed", transferId, song, candidate.Target, outputPath, 4096L, 10_000L, 1_234d);
            Invoke(events, "RaiseDownloadStateChanged", transferId, song, candidate.Target, outputPath, TransferStates.InProgress, 4096L, 10_000L);
            Invoke(events, "RaiseDownloadAttemptFailed", transferId, song, candidate.Target, outputPath, attemptOutputPath, 1, 3, new InvalidOperationException("boom"));
            Invoke(events, "RaiseTransferCompleted", transferId, song, candidate.Target, outputPath, 10_000L, 2);
            Invoke(events, "RaiseDownloadProgress", transferId, song, candidate.Target, outputPath, 9_000L, 10_000L);

            Assert.IsNotNull(started);
            Assert.IsNotNull(progressed);
            Assert.IsNotNull(stateChanged);
            Assert.IsNotNull(failed);
            Assert.IsNotNull(completed);
            Assert.AreEqual(transferId, started.TransferId);
            Assert.AreEqual(transferId, progressed.TransferId);
            Assert.AreEqual(transferId, stateChanged.TransferId);
            Assert.AreEqual(transferId, failed.TransferId);
            Assert.AreEqual(outputPath, started.Transfer.LocalPath);
            Assert.AreEqual(candidate.Filename, started.Transfer.RemotePath);
            Assert.AreEqual("Track.mp3", started.Transfer.File?.Name);
            Assert.AreEqual(10_000L, started.Transfer.File?.Size);
            Assert.AreEqual(".mp3", started.Transfer.File?.Extension);
            Assert.AreEqual(180, started.Transfer.File?.Length);
            Assert.AreEqual(4096L, progressed.Transfer.BytesTransferred);
            Assert.AreEqual(10_000L, progressed.Transfer.TotalBytes);
            Assert.IsNotNull(progressed.Transfer.RequestedAtUtc);
            Assert.IsNotNull(progressed.Transfer.StartedAtUtc);
            Assert.IsNotNull(progressed.Transfer.LastProgressAtUtc);
            Assert.AreEqual(1_234L, progressed.Transfer.BytesPerSecond);
            Assert.AreEqual(started.Transfer.File, progressed.Transfer.File);
            Assert.AreEqual(outputPath, failed.Transfer.LocalPath);
            Assert.AreEqual(attemptOutputPath, failed.OutputPath);
            Assert.AreEqual(1, failed.Transfer.AttemptCount);
            Assert.AreEqual(outputPath, completed.FinalLocalPath);
            Assert.AreEqual(10_000L, completed.Transfer.BytesTransferred);
            Assert.AreEqual(
                TransferSnapshotTerminalOutcome.Succeeded,
                completed.Transfer.TerminalOutcome);
            Assert.IsTrue(completed.Transfer.Revision > failed.Transfer.Revision);
            Assert.AreEqual(1, progressEventCount, "Late progress must be discarded after the terminal barrier.");
        }

        [TestMethod]
        public void DownloadEvents_UsesInjectedClock_AndIsolatesObservers()
        {
            var now = new DateTimeOffset(2032, 4, 5, 6, 7, 8, TimeSpan.Zero);
            var events = new DownloadEvents(new FixedTimeProvider(now));
            var job = new SearchJob("test");
            var successfulSpecificObservers = 0;
            CoreChange? published = null;

            events.JobRegistered += _ => throw new InvalidOperationException("observer failed");
            events.JobRegistered += _ => successfulSpecificObservers++;
            events.ChangePublished += change => published = change;

            Invoke(events, "RaiseJobRegistered", job, null!, null!);

            Assert.AreEqual(1, successfulSpecificObservers);
            Assert.IsNotNull(published);
            Assert.AreEqual(now, published.OccurredAtUtc);
        }

        [TestMethod]
        public void DownloadEvents_TransferOnlyChanges_DoNotAdvanceJobRevision()
        {
            var events = new DownloadEvents();
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var file = TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", size: 10_000, length: 180);
            var response = new SearchResponse("user", 1, true, 100_000, 0, [file]);
            var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
            var transferId = Guid.NewGuid();
            var observedRevisions = new List<long>();

            events.JobRegistered += change => observedRevisions.Add(change.Job.Revision);
            events.DownloadStarted += change => observedRevisions.Add(change.Song.Revision);
            events.DownloadProgress += change => observedRevisions.Add(change.Song.Revision);
            events.DownloadStateChanged += change => observedRevisions.Add(change.Song.Revision);
            events.DownloadAttemptFailed += change => observedRevisions.Add(change.Song.Revision);

            Invoke(events, "RaiseJobRegistered", song, null!, null!);
            Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");
            Invoke(events, "RaiseDownloadProgress", transferId, song, candidate.Target, "C:/downloads/Track.mp3", 100L, 10_000L);
            Invoke(events, "RaiseDownloadStateChanged", transferId, song, candidate.Target, "C:/downloads/Track.mp3", TransferStates.InProgress, 100L, 10_000L);
            Invoke(events, "RaiseDownloadAttemptFailed", transferId, song, candidate.Target, "C:/downloads/Track.mp3", "C:/downloads/Track.mp3.incomplete", 1, 3, new IOException("failed"));

            CollectionAssert.AreEqual(new long[] { 1, 1, 1, 1, 1 }, observedRevisions);
        }

        [TestMethod]
        public async Task DownloadEvents_ReportGraphStateChangesAndCompletion()
        {
            var listFile = Path.GetTempFileName();
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-events-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                System.IO.File.WriteAllLines(listFile, new[]
                {
                    "\"Artist One - Track One\"",
                    "\"Artist Two - Track Two\"",
                });

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Extraction.Input = listFile;
                downloadSettings.Extraction.InputType = InputType.List;
                downloadSettings.Extraction.RequestedMode = ExtractionMode.Song;
                downloadSettings.Output.ParentDir = outputDir;

                var client = new ClientTests.MockSoulseekClient(new List<Soulseek.SearchResponse>());
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);

                var registered = new List<(Guid JobId, Guid? ParentId)>();
                var stateChanges = new List<(Guid JobId, JobLifecycleState LifecycleState, JobActivityPhase ActivityPhase, JobTerminalOutcome TerminalOutcome, JobSkipReason SkipReason)>();
                var createdResults = new List<(Guid ExtractJobId, Guid ResultId)>();
                var executionCompleted = new List<Guid>();
                Guid? completedQueueId = null;
                object gate = new();

                engine.Events.JobRegistered += change =>
                {
                    lock (gate) registered.Add((change.Job.Id, change.ParentJobId));
                };
                engine.Events.JobStateChanged += change =>
                {
                    lock (gate) stateChanges.Add((change.Job.Id, change.LifecycleState, change.ActivityPhase, change.TerminalOutcome, change.SkipReason));
                };
                engine.Events.JobResultCreated += change =>
                {
                    lock (gate) createdResults.Add((change.ExtractJob.Id, change.ResultJob.Id));
                };
                engine.Events.JobExecutionCompleted += change =>
                {
                    lock (gate) executionCompleted.Add(change.Job.Id);
                };
                engine.Events.EngineCompleted += change => completedQueueId = change.Queue.Id;

                engine.Enqueue(new ExtractJob(downloadSettings.Extraction.Input!, downloadSettings.Extraction.InputType), downloadSettings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.AreEqual(engine.Queue.Id, completedQueueId, "EngineCompleted should publish the completed root queue.");

                var rootExtract = engine.Queue.Jobs.OfType<ExtractJob>().Single();
                Assert.IsInstanceOfType(rootExtract.Result, typeof(JobList));
                var rootList = (JobList)rootExtract.Result!;
                var childExtracts = rootList.Jobs.OfType<ExtractJob>().ToList();
                Assert.AreEqual(2, childExtracts.Count, "List extraction should create child extract jobs.");

                Assert.IsTrue(registered.Any(e => e.JobId == rootExtract.Id && e.ParentId == null),
                    "Root ExtractJob should be registered without a parent.");
                Assert.IsTrue(registered.Any(e => e.JobId == rootList.Id && e.ParentId == null),
                    "The extracted root JobList should be registered as a root-level replacement.");
                Assert.IsTrue(childExtracts.All(child => registered.Any(e => e.JobId == child.Id && e.ParentId == rootList.Id)),
                    "Child ExtractJobs should be registered under the extracted JobList.");

                foreach (var child in childExtracts)
                    Assert.IsInstanceOfType(child.Result, typeof(SongJob));
                var childSongs = childExtracts.Select(e => (SongJob)e.Result!).ToList();
                Assert.IsTrue(childSongs.All(song => registered.Any(e => e.JobId == song.Id && e.ParentId == rootList.Id)),
                    "Results of child ExtractJobs should be registered under the JobList, not under the transient ExtractJob.");

                Assert.IsTrue(createdResults.Any(e => e.ExtractJobId == rootExtract.Id && e.ResultId == rootList.Id),
                    "JobResultCreated should link the root ExtractJob to its extracted JobList.");
                Assert.IsTrue(childExtracts.All(child => createdResults.Any(e => e.ExtractJobId == child.Id && e.ResultId == child.Result!.Id)),
                    "JobResultCreated should link each child ExtractJob to its extracted SongJob.");

                Assert.IsTrue(stateChanges.Any(e => e.JobId == rootExtract.Id && e.ActivityPhase == JobActivityPhase.Extracting),
                    "JobStateChanged should report Extracting for the root ExtractJob.");
                Assert.IsTrue(stateChanges.Any(e => e.JobId == rootExtract.Id && e.TerminalOutcome == JobTerminalOutcome.Succeeded),
                    "JobStateChanged should report Done for the root ExtractJob.");
                Assert.IsTrue(childSongs.All(song => stateChanges.Any(e => e.JobId == song.Id && e.LifecycleState == JobLifecycleState.Terminal && e.TerminalOutcome != JobTerminalOutcome.Succeeded)),
                    "JobStateChanged should report the terminal state for child SongJobs.");
                Assert.IsTrue(executionCompleted.Contains(rootExtract.Id), "Root ExtractJob should raise JobExecutionCompleted.");
                Assert.IsTrue(executionCompleted.Contains(rootList.Id), "Root JobList should raise JobExecutionCompleted.");
                Assert.IsTrue(childSongs.All(song => executionCompleted.Contains(song.Id)), "Leaf song jobs should raise JobExecutionCompleted.");
            }
            finally
            {
                if (System.IO.File.Exists(listFile)) System.IO.File.Delete(listFile);
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task DownloadEvents_RootFollowUpRegistration_PublishesSourceIdentity()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            var client = new ClientTests.MockSoulseekClient([]);
            var engine = new DownloadEngine(
                engineSettings,
                TestHelpers.CreateMockClientManager(client, engineSettings));
            var sourceJobId = Guid.NewGuid();
            var job = new SearchJob("no results expected");
            JobRegisteredChange? registration = null;
            engine.Events.JobRegistered += change =>
            {
                if (change.Job.Id == job.Id)
                    registration = change;
            };

            engine.Enqueue(job, downloadSettings, sourceJobId);
            engine.CompleteEnqueue();
            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(registration);
            Assert.IsNull(registration.ParentJobId);
            Assert.AreEqual(sourceJobId, registration.SourceJobId);
        }

        [TestMethod]
        public async Task DownloadEvents_ReportTrackBatchResolved_ForDirectSongLists()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings
            {
                Preprocess = new PreprocessSettings { ParseTitleTemplate = "" },
            };

            var list = new JobList("test list", new Job[]
            {
                new SongJob(new SongQuery { Artist = "Artist One", Title = "Track One", Album = "" }),
                new SongJob(new SongQuery { Artist = "Artist Two", Title = "Track Two", Album = "" }),
            });

            var client = new ClientTests.MockSoulseekClient(new List<Soulseek.SearchResponse>());
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);

            Guid? ownerId = null;
            IReadOnlyList<Sockseek.Core.Snapshots.JobSnapshot>? pending = null;
            IReadOnlyList<Sockseek.Core.Snapshots.JobSnapshot>? existing = null;
            IReadOnlyList<Sockseek.Core.Snapshots.JobSnapshot>? notFound = null;

            engine.Events.TrackBatchResolved += change =>
            {
                ownerId = change.Owner.Id;
                pending = change.Pending;
                existing = change.Existing;
                notFound = change.NotFound;
            };

            engine.Enqueue(list, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(list.Id, ownerId, "TrackBatchResolved should identify the owning job.");
            Assert.IsNotNull(pending, "TrackBatchResolved should publish the pending songs.");
            Assert.AreEqual(2, pending!.Count);
            Assert.AreEqual(0, existing!.Count);
            Assert.AreEqual(0, notFound!.Count);
        }

        [TestMethod]
        public async Task ConcurrentJobs_LimitsDirectSongWorkAcrossJobList()
        {
            var index = new List<SearchResponse>
            {
                new(
                    username: "user1",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: [new Soulseek.File(1, @"Music\Artist\Track One.mp3", 10_000, ".mp3")]),
                new(
                    username: "user2",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: [new Soulseek.File(2, @"Music\Artist\Track Two.mp3", 10_000, ".mp3")]),
            };

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-concurrent-jobs-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings
                {
                    Username = "test_user",
                    Password = "test_pass",
                    ConcurrentJobs = 1,
                    ConcurrentSearches = 10,
                };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;
                downloadSettings.Output.WriteIndex = false;
                downloadSettings.Output.HasConfiguredIndex = true;
                downloadSettings.Skip.SkipExisting = false;

                var list = new JobList("test list", new Job[]
                {
                    new SongJob(new SongQuery { Artist = "Artist", Title = "Track One" }),
                    new SongJob(new SongQuery { Artist = "Artist", Title = "Track Two" }),
                });

                var downloadGate = new TestHelpers.DownloadGate();
                var client = new ClientTests.MockSoulseekClient(index)
                {
                    BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
                };
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);

                var activeSongs = new HashSet<Guid>();
                int maxActive = 0;
                object gate = new();

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Kind != Sockseek.Core.Snapshots.JobSnapshotKind.Song)
                        return;

                    lock (gate)
                    {
                        if (change.ActivityPhase is JobActivityPhase.Searching or JobActivityPhase.Downloading)
                        {
                            activeSongs.Add(change.Job.Id);
                            maxActive = Math.Max(maxActive, activeSongs.Count);
                        }
                        else if (change.IsTerminal)
                        {
                            activeSongs.Remove(change.Job.Id);
                        }
                    }
                };

                engine.Enqueue(list, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await downloadGate.WaitForStartedCountAsync(1);
                Assert.AreEqual(1, downloadGate.StartedCount, "A second song download must not start while the first leaf job holds the global job slot.");
                await CompleteRunWithBlockedDownloads(downloadGate, runTask);

                Assert.AreEqual(1, maxActive, "--concurrent-jobs=1 should serialize concurrently fanned-out song work.");
                Assert.IsTrue(list.Jobs.OfType<SongJob>().All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task ConcurrentJobs_LimitsAlbumJobsButNotEmbeddedAlbumTracks()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-concurrent-albums-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            SearchResponse Response(string username, int token, params Soulseek.File[] files) =>
                new(
                    username: username,
                    token: token,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: files);

            var album1File1 = new Soulseek.File(1, @"Music\Artist\Album One\01. Artist - One.mp3", 10_000, ".mp3");
            var album1File2 = new Soulseek.File(2, @"Music\Artist\Album One\02. Artist - Two.mp3", 10_000, ".mp3");
            var album2File1 = new Soulseek.File(3, @"Music\Artist\Album Two\01. Artist - Three.mp3", 10_000, ".mp3");
            var album2File2 = new Soulseek.File(4, @"Music\Artist\Album Two\02. Artist - Four.mp3", 10_000, ".mp3");
            var response1 = Response("user1", 1, album1File1, album1File2);
            var response2 = Response("user2", 2, album2File1, album2File2);

            AlbumJob Album(string albumName, SearchResponse response, Soulseek.File file1, Soulseek.File file2)
            {
                var folder = new AlbumFolder(
                    response.Username,
                    Utils.GetDirectoryNameSlsk(file1.Filename),
                    [
                        TestHelpers.CreateAlbumFile(response, file1),
                        TestHelpers.CreateAlbumFile(response, file2),
                    ]);
                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = albumName })
                {
                    Results = [folder],
                    ResolvedTarget = folder,
                };
                album.EnsureTrackJobs(folder);
                return album;
            }

            try
            {
                var engineSettings = new EngineSettings
                {
                    Username = "test_user",
                    Password = "test_pass",
                    ConcurrentJobs = 1,
                    ConcurrentSearches = 10,
                };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;
                downloadSettings.Output.WriteIndex = false;
                downloadSettings.Output.HasConfiguredIndex = true;
                downloadSettings.Skip.SkipExisting = false;

                var album1 = Album("Album One", response1, album1File1, album1File2);
                var album2 = Album("Album Two", response2, album2File1, album2File2);
                var list = new JobList("album list", [album1, album2]);

                var downloadGate = new TestHelpers.DownloadGate();
                var client = new ClientTests.MockSoulseekClient([response1, response2])
                {
                    BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
                };
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);

                var activeAlbums = new HashSet<Guid>();
                int maxActiveAlbums = 0;
                object gate = new();

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Kind != Sockseek.Core.Snapshots.JobSnapshotKind.Album)
                        return;

                    lock (gate)
                    {
                        if (change.ActivityPhase == JobActivityPhase.Downloading)
                        {
                            activeAlbums.Add(change.Job.Id);
                            maxActiveAlbums = Math.Max(maxActiveAlbums, activeAlbums.Count);
                        }
                        else if (change.IsTerminal)
                        {
                            activeAlbums.Remove(change.Job.Id);
                        }
                    }
                };

                engine.Enqueue(list, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await downloadGate.WaitForStartedCountAsync(1);
                Assert.AreEqual(1, downloadGate.StartedCount, "A second album must not start while the first album job holds the global job slot.");
                await CompleteRunWithBlockedDownloads(downloadGate, runTask);

                Assert.AreEqual(1, maxActiveAlbums, "--concurrent-jobs=1 should allow only one album job to download at a time.");
                Assert.IsTrue(new[] { album1, album2 }.All(album => album.TerminalOutcome == JobTerminalOutcome.Succeeded));
                Assert.IsTrue(new[] { album1, album2 }.SelectMany(album => album.TrackJobs).All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task ConcurrentJobs_LimitsSongsWithinAggregateJob()
        {
            var index = new List<SearchResponse>
            {
                new(
                    username: "user1",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: [TestHelpers.CreateSlFile(@"Music\ELO\Time\Blue Sky.mp3", length: 180)]),
                new(
                    username: "user2",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: [TestHelpers.CreateSlFile(@"Shares\Electric Light Orchestra\ELO - Blue Sky.mp3", length: 181)]),
                new(
                    username: "user3",
                    token: 3,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: [TestHelpers.CreateSlFile(@"Live\ELO - Blue Sky (Live).mp3", length: 300)]),
            };

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-concurrent-aggregate-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings
                {
                    Username = "test_user",
                    Password = "test_pass",
                    ConcurrentJobs = 1,
                    ConcurrentSearches = 10,
                };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;
                downloadSettings.Output.WriteIndex = false;
                downloadSettings.Output.HasConfiguredIndex = true;
                downloadSettings.Search.MinSharesAggregate = 1;
                downloadSettings.Skip.SkipExisting = false;

                var aggregateJob = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
                var downloadGate = new TestHelpers.DownloadGate();
                var client = new ClientTests.MockSoulseekClient(index)
                {
                    BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
                };
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);

                var activeSongs = new HashSet<Guid>();
                int maxActiveSongs = 0;
                object gate = new();

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Kind != Sockseek.Core.Snapshots.JobSnapshotKind.Song)
                        return;

                    lock (gate)
                    {
                        if (change.ActivityPhase == JobActivityPhase.Downloading)
                        {
                            activeSongs.Add(change.Job.Id);
                            maxActiveSongs = Math.Max(maxActiveSongs, activeSongs.Count);
                        }
                        else if (change.IsTerminal)
                        {
                            activeSongs.Remove(change.Job.Id);
                        }
                    }
                };

                engine.Enqueue(aggregateJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await downloadGate.WaitForStartedCountAsync(1);
                Assert.AreEqual(1, downloadGate.StartedCount, "A second aggregate song must not start while the first holds the global job slot.");
                await CompleteRunWithBlockedDownloads(downloadGate, runTask);

                Assert.IsTrue(aggregateJob.Songs.Count >= 2, "Aggregate should produce multiple song jobs for this test.");
                Assert.AreEqual(1, maxActiveSongs, "--concurrent-jobs=1 should allow only one aggregate child song to download at a time.");
                Assert.IsTrue(aggregateJob.Songs.All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task ConcurrentJobs_LimitsAlbumsWithinAlbumAggregateJob()
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
                        TestHelpers.CreateSlFile(@"Music\ELO\Album One\01. ELO - One.mp3", length: 180),
                        TestHelpers.CreateSlFile(@"Music\ELO\Album One\02. ELO - Two.mp3", length: 181),
                    ]),
                new(
                    username: "user2",
                    token: 2,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList:
                    [
                        TestHelpers.CreateSlFile(@"Shares\Electric Light Orchestra\Album Two\01. ELO - Three.mp3", length: 240),
                        TestHelpers.CreateSlFile(@"Shares\Electric Light Orchestra\Album Two\02. ELO - Four.mp3", length: 241),
                    ]),
            };

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-concurrent-album-aggregate-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var engineSettings = new EngineSettings
                {
                    Username = "test_user",
                    Password = "test_pass",
                    ConcurrentJobs = 1,
                    ConcurrentSearches = 10,
                };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Output.ParentDir = outputDir;
                downloadSettings.Output.WriteIndex = false;
                downloadSettings.Output.HasConfiguredIndex = true;
                downloadSettings.Search.MinSharesAggregate = 1;
                downloadSettings.Search.NoBrowseFolder = true;
                downloadSettings.Skip.SkipExisting = false;

                var aggregateJob = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
                var downloadGate = new TestHelpers.DownloadGate();
                var client = new ClientTests.MockSoulseekClient(index)
                {
                    BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
                };
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);

                var activeAlbums = new HashSet<Guid>();
                int maxActiveAlbums = 0;
                object gate = new();

                engine.Events.JobStateChanged += change =>
                {
                    if (change.Job.Kind != Sockseek.Core.Snapshots.JobSnapshotKind.Album)
                        return;

                    lock (gate)
                    {
                        if (change.ActivityPhase == JobActivityPhase.Downloading)
                        {
                            activeAlbums.Add(change.Job.Id);
                            maxActiveAlbums = Math.Max(maxActiveAlbums, activeAlbums.Count);
                        }
                        else if (change.IsTerminal)
                        {
                            activeAlbums.Remove(change.Job.Id);
                        }
                    }
                };

                engine.Enqueue(aggregateJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await downloadGate.WaitForStartedCountAsync(1);
                Assert.AreEqual(1, downloadGate.StartedCount, "A second aggregate album must not start while the first album job holds the global job slot.");
                await CompleteRunWithBlockedDownloads(downloadGate, runTask);

                Assert.IsTrue(aggregateJob.Albums.Count >= 2, "Album aggregate should produce multiple album jobs for this test.");
                Assert.AreEqual(1, maxActiveAlbums, "--concurrent-jobs=1 should allow only one album-aggregate child album to download at a time.");
                Assert.IsTrue(aggregateJob.Albums.All(album => album.TerminalOutcome == JobTerminalOutcome.Succeeded));
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumTrackCount_BrowsesAndChecksRequiredCountsBeforeDownloadEvenWhenNoBrowseFolder()
        {
            async Task RunCase(
                string caseName,
                int visibleCount,
                int fullCount,
                int? minTrackCount,
                int? maxTrackCount,
                bool shouldDownload)
            {
                var outputDir = Path.Combine(Path.GetTempPath(), "slsk-track-count-precheck-" + caseName + "-" + Guid.NewGuid());
                System.IO.Directory.CreateDirectory(outputDir);

                try
                {
                    var files = AlbumFiles(fullCount, $@"Music\Artist\{caseName}");
                    var response = new SearchResponse(
                        username: caseName,
                        token: 1,
                        hasFreeUploadSlot: true,
                        uploadSpeed: 100_000,
                        queueLength: 0,
                        fileList: files);
                    var folder = AlbumFolderFromSearch(response, files.Take(visibleCount));
                    var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = caseName })
                    {
                        Results = [folder],
                    };

                    var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                    var downloadSettings = AlbumDownloadSettings(outputDir);
                    downloadSettings.Search.NoBrowseFolder = true;
                    downloadSettings.Search.NecessaryFolderCond.MinTrackCount = minTrackCount;
                    downloadSettings.Search.NecessaryFolderCond.MaxTrackCount = maxTrackCount;

                    var client = new ClientTests.MockSoulseekClient([response]);
                    var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                    var engine = new DownloadEngine(engineSettings, clientManager);
                    var downloadsStarted = 0;
                    engine.Events.DownloadStarted += _ => downloadsStarted++;

                    engine.Enqueue(album, downloadSettings);
                    engine.CompleteEnqueue();

                    await engine.RunAsync(CancellationToken.None);

                    Assert.AreEqual(1, client.BrowseCallCount, $"{caseName}: track-count verification must browse before download even with NoBrowseFolder enabled.");
                    Assert.AreEqual(0, client.DownloadCallCountAtFirstBrowse, $"{caseName}: browse must happen before the first download attempt.");

                    if (shouldDownload)
                    {
                        Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome, $"{caseName}: album should download after browse confirms the track count.");
                        Assert.AreEqual(fullCount, downloadsStarted, $"{caseName}: browse should reveal and download the full matching folder.");
                    }
                    else
                    {
                        Assert.IsTrue(album.IsUnsuccessfulTerminal, $"{caseName}: album should fail track-count verification before any download starts.");
                        Assert.AreEqual(0, downloadsStarted, $"{caseName}: failed track-count verification must prevent downloads.");
                        Assert.AreEqual(0, client.DownloadCallCount, $"{caseName}: failed track-count verification must prevent download calls.");
                    }
                }
                finally
                {
                    if (System.IO.Directory.Exists(outputDir))
                        System.IO.Directory.Delete(outputDir, true);
                }
            }

            await RunCase(
                caseName: "min-needs-browse",
                visibleCount: 1,
                fullCount: 3,
                minTrackCount: 3,
                maxTrackCount: null,
                shouldDownload: true);

            await RunCase(
                caseName: "max-needs-browse",
                visibleCount: 1,
                fullCount: 3,
                minTrackCount: null,
                maxTrackCount: 2,
                shouldDownload: false);
        }

        [TestMethod]
        public async Task AlbumTrackCount_BrowsesBeforeDownloadOnlyWhenCurrentKnowledgeCannotProveCounts()
        {
            async Task RunCase(
                string caseName,
                int visibleCount,
                int fullCount,
                int? minTrackCount,
                int? maxTrackCount,
                bool markFullyRetrieved,
                bool preselect,
                bool expectBrowse,
                bool shouldDownload)
            {
                var outputDir = Path.Combine(Path.GetTempPath(), "slsk-track-count-skip-browse-" + caseName + "-" + Guid.NewGuid());
                System.IO.Directory.CreateDirectory(outputDir);

                try
                {
                    var files = AlbumFiles(fullCount, $@"Music\Artist\{caseName}");
                    var response = new SearchResponse(
                        username: caseName,
                        token: 1,
                        hasFreeUploadSlot: true,
                        uploadSpeed: 100_000,
                        queueLength: 0,
                        fileList: files);
                    var folder = AlbumFolderFromSearch(response, files.Take(visibleCount));
                    folder.IsFullyRetrieved = markFullyRetrieved;
                    var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = caseName })
                    {
                        Results = [folder],
                        ResolvedTarget = preselect ? folder : null,
                    };

                    var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                    var downloadSettings = AlbumDownloadSettings(outputDir);
                    downloadSettings.Search.NoBrowseFolder = true;
                    downloadSettings.Search.NecessaryFolderCond.MinTrackCount = minTrackCount;
                    downloadSettings.Search.NecessaryFolderCond.MaxTrackCount = maxTrackCount;

                    var client = new ClientTests.MockSoulseekClient([response]);
                    var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                    var engine = new DownloadEngine(engineSettings, clientManager);
                    var downloadsStarted = 0;
                    engine.Events.DownloadStarted += _ => downloadsStarted++;

                    engine.Enqueue(album, downloadSettings);
                    engine.CompleteEnqueue();

                    await engine.RunAsync(CancellationToken.None);

                    Assert.AreEqual(expectBrowse ? 1 : 0, client.BrowseCallCount, $"{caseName}: unexpected pre-download browse count.");
                    if (expectBrowse)
                        Assert.AreEqual(0, client.DownloadCallCountAtFirstBrowse, $"{caseName}: browse must happen before downloading.");

                    if (shouldDownload)
                    {
                        Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome, $"{caseName}: album should download.");
                        Assert.AreEqual(visibleCount, downloadsStarted, $"{caseName}: NoBrowseFolder should keep download limited to the known files when no correctness browse is needed.");
                    }
                    else
                    {
                        Assert.IsTrue(album.IsUnsuccessfulTerminal, $"{caseName}: album should fail before download.");
                        Assert.AreEqual(0, downloadsStarted, $"{caseName}: failed track-count verification must prevent downloads.");
                    }
                }
                finally
                {
                    if (System.IO.Directory.Exists(outputDir))
                        System.IO.Directory.Delete(outputDir, true);
                }
            }

            await RunCase(
                caseName: "known-min-passes",
                visibleCount: 3,
                fullCount: 3,
                minTrackCount: 2,
                maxTrackCount: null,
                markFullyRetrieved: false,
                preselect: false,
                expectBrowse: false,
                shouldDownload: true);

            await RunCase(
                caseName: "known-min-passes-but-max-may-fail",
                visibleCount: 2,
                fullCount: 3,
                minTrackCount: 2,
                maxTrackCount: 2,
                markFullyRetrieved: false,
                preselect: false,
                expectBrowse: true,
                shouldDownload: false);

            await RunCase(
                caseName: "already-browsed",
                visibleCount: 2,
                fullCount: 2,
                minTrackCount: 2,
                maxTrackCount: 2,
                markFullyRetrieved: true,
                preselect: true,
                expectBrowse: false,
                shouldDownload: true);
        }

        [TestMethod]
        public async Task StrictAlbumQuality_BrowsesAndRejectsFolderWhenHiddenFilesBreakQuality()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-strict-quality-browse-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var badFiles = new List<Soulseek.File>
                {
                    TestHelpers.CreateSlFile(@"Music\Artist\bad-quality\01. Artist - Track 01.flac", bitrate: 950, length: 181),
                    TestHelpers.CreateSlFile(@"Music\Artist\bad-quality\02. Artist - Track 02.mp3", bitrate: 320, length: 182),
                };
                var goodFiles = new List<Soulseek.File>
                {
                    TestHelpers.CreateSlFile(@"Music\Artist\good-quality\01. Artist - Track 01.flac", bitrate: 950, length: 181),
                    TestHelpers.CreateSlFile(@"Music\Artist\good-quality\02. Artist - Track 02.flac", bitrate: 950, length: 182),
                };
                var badResponse = new SearchResponse("bad-user", 1, true, 100_000, 0, badFiles);
                var goodResponse = new SearchResponse("good-user", 1, true, 100_000, 0, goodFiles);
                var badFolder = AlbumFolderFromSearch(badResponse, badFiles.Take(1));
                var goodFolder = AlbumFolderFromSearch(goodResponse, goodFiles.Take(1));
                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Results = [badFolder, goodFolder],
                };

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var downloadSettings = AlbumDownloadSettings(outputDir);
                downloadSettings.Search.NoBrowseFolder = true;
                downloadSettings.Search.NecessaryCond.Formats = ["flac"];
                downloadSettings.Search.StrictAlbumQuality = true;

                var client = new ClientTests.MockSoulseekClient([badResponse, goodResponse]);
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);
                var downloadsStarted = 0;
                engine.Events.DownloadStarted += _ => downloadsStarted++;

                engine.Enqueue(album, downloadSettings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome, "Album should fall back to the next folder after browse reveals hidden non-matching files.");
                Assert.AreEqual(goodFolder, album.ResolvedTarget, "Strict quality should reject the first folder after full browse and select the next matching folder.");
                Assert.AreEqual(2, client.BrowseCallCount, "Strict quality should browse each candidate before download, even with NoBrowseFolder enabled.");
                Assert.AreEqual(0, client.DownloadCallCountAtFirstBrowse, "Strict quality browse must happen before any download starts.");
                Assert.AreEqual(2, downloadsStarted, "Only the fully matching browsed folder should download.");
                Assert.AreEqual(2, client.DownloadCallCount, "The rejected folder must not start downloads.");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumTrackCount_CancelledVerificationBrowseSkipsOnlyThatFolder()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-track-count-cancel-browse-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var cancelledFiles = AlbumFiles(3, @"Music\Artist\cancelled-folder");
                var matchingFiles = AlbumFiles(2, @"Music\Artist\matching-folder");
                var cancelledResponse = new SearchResponse("cancelled-user", 1, true, 100_000, 0, cancelledFiles);
                var matchingResponse = new SearchResponse("matching-user", 1, true, 100_000, 0, matchingFiles);
                var cancelledFolder = AlbumFolderFromSearch(cancelledResponse, cancelledFiles.Take(2));
                var matchingFolder = AlbumFolderFromSearch(matchingResponse, matchingFiles.Take(2));
                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Results = [cancelledFolder, matchingFolder],
                };

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var downloadSettings = AlbumDownloadSettings(outputDir);
                downloadSettings.Search.NoBrowseFolder = true;
                downloadSettings.Search.NecessaryFolderCond.MaxTrackCount = 2;

                var client = new ClientTests.MockSoulseekClient([cancelledResponse, matchingResponse]);
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);
                var retrieveJobs = new List<RetrieveFolderJob>();
                var completedRetrieveJobs = new List<RetrieveFolderJob>();
                var downloadsStarted = 0;

                engine.Events.JobRegistered += change =>
                {
                    if (change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.RetrieveFolder
                        && engine.GetJob(change.Job.Id) is RetrieveFolderJob retrieveJob)
                        retrieveJobs.Add(retrieveJob);
                };
                engine.Events.JobExecutionCompleted += change =>
                {
                    if (change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.RetrieveFolder
                        && engine.GetJob(change.Job.Id) is RetrieveFolderJob retrieveJob)
                        completedRetrieveJobs.Add(retrieveJob);
                };
                engine.Events.DownloadStarted += _ => downloadsStarted++;
                client.BrowseStarted = () =>
                {
                    if (client.BrowseCallCount == 1)
                        retrieveJobs.Single().Cancel(JobCancellationSource.UserRequestedJob);
                };

                engine.Enqueue(album, downloadSettings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome, "Cancelling one verification browse should not cancel the whole album when another folder can match.");
                Assert.AreEqual(matchingFolder, album.ResolvedTarget, "The cancelled folder must be skipped instead of downloaded without a verified max count.");
                Assert.AreEqual(2, downloadsStarted, "Only the verified matching folder should download.");
                Assert.AreEqual(2, client.DownloadCallCount, "The cancelled folder must not start any downloads.");
                Assert.AreEqual(2, client.BrowseCallCount, "The cancelled folder and then the matching folder should each be browsed.");
                Assert.IsTrue(retrieveJobs[0].IsUnsuccessfulTerminal, "The cancelled browse job should be failed.");
                Assert.AreEqual(JobFailureReason.Cancelled, retrieveJobs[0].FailureReason, "The cancelled browse job should preserve its cancellation reason.");
                Assert.AreEqual(FolderRetrievalOutcome.Cancelled, retrieveJobs[0].RetrievalOutcome, "The cancelled browse job should expose its retrieval outcome.");
                Assert.AreEqual(FolderRetrievalOutcome.Completed, retrieveJobs[1].RetrievalOutcome, "The successful browse job should expose its retrieval outcome.");
                Assert.IsTrue(completedRetrieveJobs.Contains(retrieveJobs[0]), "Embedded retrieve jobs should report execution completion after cancellation.");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AlbumFolderCompletionFailure_PreservesKnownSelectionAndFailsOnlyTheRetrievalJob(bool throwsCancellation)
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-album-failed-folder-completion-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var files = AlbumFiles(1, @"Music\Artist\known-selection");
                var response = new SearchResponse("browse-failure-user", 1, true, 100_000, 0, files);
                var folder = AlbumFolderFromSearch(response, files);
                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Results = [folder],
                };

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var client = new ClientTests.MockSoulseekClient([response]);
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                Exception retrievalFailure = throwsCancellation
                    ? new OperationCanceledException("browse transport ended early")
                    : new IOException("browse transport failed");
                var directorySource = new ThrowingDirectorySource(retrievalFailure);
                var engine = new DownloadEngine(
                    engineSettings,
                    clientManager,
                    directorySource: directorySource);
                RetrieveFolderJob? retrieveJob = null;
                engine.Events.JobRegistered += change =>
                {
                    if (change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.RetrieveFolder)
                        retrieveJob = (RetrieveFolderJob)engine.GetJob(change.Job.Id)!;
                };

                engine.Enqueue(album, AlbumDownloadSettings(outputDir));
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome,
                    "A failed best-effort folder completion must not discard the exact files already found by search.");
                Assert.AreEqual(1, client.DownloadCallCount);
                Assert.IsNotNull(retrieveJob);
                Assert.AreEqual(FolderRetrievalOutcome.Failed, retrieveJob.RetrievalOutcome);
                Assert.AreEqual(JobTerminalOutcome.Failed, retrieveJob.TerminalOutcome);
                Assert.AreNotEqual(JobFailureReason.Cancelled, retrieveJob.FailureReason);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumFolderRetrievalBeforeTrackCountDownload_SetsParentAlbumActivityWhileBrowsing()
            => await AssertAlbumFolderRetrievalSetsParentActivityWhileBrowsing(postDownloadBrowse: false);

        [TestMethod]
        public async Task AlbumFolderCompletionBeforeTransfer_SetsParentAlbumActivityWhileBrowsing()
            => await AssertAlbumFolderRetrievalSetsParentActivityWhileBrowsing(postDownloadBrowse: true);

        private static async Task AssertAlbumFolderRetrievalSetsParentActivityWhileBrowsing(bool postDownloadBrowse)
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-album-retrieving-phase-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            try
            {
                var files = AlbumFiles(2, @"Music\Artist\retrieving-phase");
                var response = new SearchResponse(
                    username: "retrieving-phase-user",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100_000,
                    queueLength: 0,
                    fileList: files);
                var folder = AlbumFolderFromSearch(response, files.Take(1));
                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "retrieving-phase" })
                {
                    Results = [folder],
                };

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var downloadSettings = AlbumDownloadSettings(outputDir);
                downloadSettings.Search.NoBrowseFolder = postDownloadBrowse ? false : true;
                if (!postDownloadBrowse)
                    downloadSettings.Search.NecessaryFolderCond.MinTrackCount = 2;

                var client = new ClientTests.MockSoulseekClient([response]);
                bool parentWasRetrievingAtBrowseStart = false;
                int expectedDownloadsBeforeBrowse = 0;
                client.BrowseStarted = () =>
                    parentWasRetrievingAtBrowseStart =
                        client.DownloadCallCount == expectedDownloadsBeforeBrowse
                        && album.ActivityPhase == JobActivityPhase.RetrievingFolder;

                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);
                var albumActivityPhases = new List<JobActivityPhase>();
                engine.Events.JobActivityChanged += change =>
                {
                    if (change.Job.Id == album.Id)
                        albumActivityPhases.Add(change.Phase);
                };

                engine.Enqueue(album, downloadSettings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.IsTrue(parentWasRetrievingAtBrowseStart, "Album parent should expose RetrievingFolder while its immutable transfer plan is being completed.");
                Assert.IsTrue(albumActivityPhases.Contains(JobActivityPhase.RetrievingFolder), "Album parent should publish a RetrievingFolder activity change.");
                Assert.AreEqual(2, client.DownloadCallCount, "Folder browse should discover and download the hidden track.");
                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                Assert.AreEqual(JobActivityPhase.None, album.ActivityPhase, "Terminal albums should not remain stuck in the retrieval activity.");
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        private static List<Soulseek.File> AlbumFiles(int count, string folder)
        {
            var files = new List<Soulseek.File>();
            for (int i = 1; i <= count; i++)
                files.Add(TestHelpers.CreateSlFile($@"{folder}\{i:D2}. Artist - Track {i:D2}.mp3", bitrate: 320, length: 180 + i));
            return files;
        }

        private static AlbumFolder AlbumFolderFromSearch(SearchResponse response, IEnumerable<Soulseek.File> files)
        {
            var visibleFiles = files.ToList();
            var albumFiles = visibleFiles
                .Select(file => TestHelpers.CreateAlbumFile(
                    response,
                    file,
                    new SongQuery
                    {
                        Artist = "Artist",
                        Album = Utils.GetBaseNameSlsk(Utils.GetDirectoryNameSlsk(file.Filename)),
                        Title = Path.GetFileNameWithoutExtension(file.Filename),
                    }))
                .ToList();

            return new AlbumFolder(response.Username, Utils.GetDirectoryNameSlsk(visibleFiles.First().Filename), albumFiles);
        }

        private static DownloadSettings AlbumDownloadSettings(string outputDir)
        {
            var settings = new DownloadSettings();
            settings.Output.ParentDir = outputDir;
            settings.Output.WriteIndex = false;
            settings.Output.HasConfiguredIndex = true;
            settings.Skip.SkipExisting = false;
            return settings;
        }

        private static void Invoke(DownloadEvents events, string methodName, params object[] args)
            => typeof(DownloadEvents)
                .GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(events, args);

        private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => utcNow;
        }

        private sealed class ThrowingDirectorySource(Exception exception) : IPeerDirectorySource
        {
            public Task<PeerDirectorySnapshot> RetrieveDirectoryAsync(
                PeerDirectoryIdentity directory,
                CancellationToken cancellationToken = default)
                => Task.FromException<PeerDirectorySnapshot>(exception);
        }

        [TestMethod]
        public async Task DownloadEvents_AlbumJob_ExposesResolvedTarget_OnDownloadingState()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            var albumQuery = new AlbumQuery { Artist = "Artist One", Album = "Album One" };
            var albumJob = new AlbumJob(albumQuery);

            var searchResponse = new SearchResponse(
                username: "test_user",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList: new List<Soulseek.File> { new Soulseek.File(1, "C:\\Music\\Album One\\Artist One - Album One - Track One.mp3", 10000, ".mp3") }
            );

            var client = new ClientTests.MockSoulseekClient(new List<SearchResponse> { searchResponse });
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);

            AlbumFolder? capturedFolder = null;

            engine.Events.JobStateChanged += change =>
            {
                if (change.ActivityPhase == JobActivityPhase.Downloading
                    && change.Job.Kind == Sockseek.Core.Snapshots.JobSnapshotKind.Album
                    && engine.GetJob(change.Job.Id) is AlbumJob aj)
                {
                    capturedFolder = aj.ResolvedTarget;
                }
            };

            engine.Enqueue(albumJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(capturedFolder, "ResolvedTarget should be populated when Downloading activity is reported.");
            Assert.AreEqual("C:\\Music\\Album One", capturedFolder.FolderPath);
        }
        [TestMethod]
        public void Job_PopulatesDiscoveryMetadata_BeforeStateChangedFires()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            Sockseek.Core.Snapshots.DiscoverySnapshot? capturedDiscovery = null;
            JobActivityPhase capturedActivity = JobActivityPhase.None;

            var events = new DownloadEvents();
            events.JobStateChanged += j =>
            {
                capturedDiscovery = j.Discovery;
                capturedActivity = j.ActivityPhase;
            };

            song.Discovery = new DiscoverySummary { RawResultCount = 5, LockedFileCount = 2 };
            song.UpdateActivity(JobActivityPhase.Downloading);

            var raiseMethod = typeof(DownloadEvents).GetMethod("RaiseJobStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            raiseMethod?.Invoke(events, [song]);

            Assert.AreEqual(JobActivityPhase.Downloading, capturedActivity);
            Assert.IsNotNull(capturedDiscovery);
            Assert.AreEqual(5, capturedDiscovery.RawResultCount);
            Assert.AreEqual(2, capturedDiscovery.LockedFileCount);
        }

        [TestMethod]
        public void DiscoveryMetadata_PersistsForMultipleSubscribers()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            bool sub1SawIt = false;
            bool sub2SawIt = false;

            var events = new DownloadEvents();
            events.JobStateChanged += j => sub1SawIt = j.Discovery != null;
            events.JobStateChanged += j => sub2SawIt = j.Discovery != null;

            song.Discovery = new DiscoverySummary { RawResultCount = 1 };
            song.UpdateActivity(JobActivityPhase.Downloading);

            var raiseMethod = typeof(DownloadEvents).GetMethod("RaiseJobStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            raiseMethod?.Invoke(events, [song]);

            Assert.IsTrue(sub1SawIt, "First subscriber should see metadata");
            Assert.IsTrue(sub2SawIt, "Second subscriber should see metadata (not consumed)");
        }

        [TestMethod]
        public async Task DownloadEvents_JobStateChanged_ToFailed_ExtractJob_HasFailureReasonPopulated()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            
            // Pointing to a non-existent file will cause ListExtractor to throw FileNotFoundException
            var extractJob = new ExtractJob("invalid-input-that-throws.txt", InputType.List); 
            var client = new ClientTests.MockSoulseekClient([]);
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);

            JobFailureReason capturedReason = JobFailureReason.None;
            string? capturedDetail = null;
            bool failedFired = false;

            engine.Events.JobStateChanged += change =>
            {
                if (change.Job.Id == extractJob.Id && change.IsUnsuccessfulTerminal)
                {
                    failedFired = true;
                    capturedReason = change.FailureReason;
                    capturedDetail = change.FailureDetail;
                }
            };

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsTrue(failedFired, "JobStateChanged should fire for a failed terminal outcome.");
            Assert.AreEqual(JobFailureReason.ExtractionFailed, capturedReason, 
                "FailureReason must be populated BEFORE the JobStateChanged event is fired for ExtractJobs.");
            StringAssert.Contains(capturedDetail, nameof(FileNotFoundException));
        }

        [TestMethod]
        public async Task DownloadEvents_JobStateChanged_ToFailed_NotFound_HasFailureReasonPopulated()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            
            var songJob = new SongJob(new SongQuery { Artist = "Nonexistent", Title = "Track" });
            var client = new ClientTests.MockSoulseekClient([]);
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);

            JobFailureReason capturedReason = JobFailureReason.None;
            bool failedFired = false;

            engine.Events.JobStateChanged += change =>
            {
                if (change.Job.Id == songJob.Id && change.IsUnsuccessfulTerminal)
                {
                    failedFired = true;
                    capturedReason = change.FailureReason;
                }
            };

            engine.Enqueue(songJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsTrue(failedFired, "JobStateChanged should fire for a failed terminal outcome.");
            Assert.AreEqual(JobFailureReason.NoSearchResults, capturedReason, 
                "FailureReason must be populated BEFORE the JobStateChanged event is fired for not found items.");
        }

        [TestMethod]
        public async Task DownloadEvents_JobStateChanged_ToFailed_Download_HasFailureReasonPopulated()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            downloadSettings.Transfer.MaxDownloadRetries = 0; // Fail quickly
            
            var songJob = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            
            // Give it a candidate but make the mock client fail the download
            var file = TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180);
            var response = new Soulseek.SearchResponse("failuser", 1, true, 100, 0, [file]);
            
            var client = new ClientTests.MockSoulseekClient([response], failingUsers: ["failuser"]);
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);

            JobFailureReason capturedReason = JobFailureReason.None;
            string? capturedDetail = "not-captured";
            bool failedFired = false;

            engine.Events.JobStateChanged += change =>
            {
                if (change.Job.Id == songJob.Id && change.IsUnsuccessfulTerminal)
                {
                    failedFired = true;
                    capturedReason = change.FailureReason;
                    capturedDetail = change.FailureDetail;
                }
            };

            engine.Enqueue(songJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsTrue(failedFired, "JobStateChanged should fire for a failed terminal outcome.");
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, capturedReason, 
                "FailureReason must be populated BEFORE the JobStateChanged event is fired for download failures.");
            Assert.IsNull(capturedDetail, "Known download attempt exceptions are reported by DownloadAttemptFailed and should not be duplicated as terminal diagnostic detail.");
        }
    }
}
