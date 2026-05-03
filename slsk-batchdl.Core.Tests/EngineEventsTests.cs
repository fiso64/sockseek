using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Soulseek;

namespace Tests.Eventing
{
    [TestClass]
    public class EngineEventsTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext _)
        {
            Logger.AddConsole(Logger.LogLevel.Fatal);
        }

        [TestMethod]
        public async Task EngineEvents_ReportGraphStateChangesAndCompletion()
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
                downloadSettings.Output.ParentDir = outputDir;

                var client = new ClientTests.MockSoulseekClient(new List<Soulseek.SearchResponse>());
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);

                var registered = new List<(Job Job, Job? Parent)>();
                var stateChanges = new List<(Job Job, JobState State)>();
                var createdResults = new List<(ExtractJob ExtractJob, Job Result)>();
                var executionCompleted = new List<Job>();
                JobList? completedQueue = null;
                object gate = new();

                engine.Events.JobRegistered += (job, parent) =>
                {
                    lock (gate) registered.Add((job, parent));
                };
                engine.Events.JobStateChanged += (job, state) =>
                {
                    lock (gate) stateChanges.Add((job, state));
                };
                engine.Events.JobResultCreated += (extractJob, result) =>
                {
                    lock (gate) createdResults.Add((extractJob, result));
                };
                engine.Events.JobExecutionCompleted += job =>
                {
                    lock (gate) executionCompleted.Add(job);
                };
                engine.Events.EngineCompleted += queue => completedQueue = queue;

                engine.Enqueue(new ExtractJob(downloadSettings.Extraction.Input!, downloadSettings.Extraction.InputType), downloadSettings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.AreSame(engine.Queue, completedQueue, "EngineCompleted should publish the completed root queue.");

                var rootExtract = engine.Queue.Jobs.OfType<ExtractJob>().Single();
                Assert.IsInstanceOfType(rootExtract.Result, typeof(JobList));
                var rootList = (JobList)rootExtract.Result!;
                var childExtracts = rootList.Jobs.OfType<ExtractJob>().ToList();
                Assert.AreEqual(2, childExtracts.Count, "List extraction should create child extract jobs.");

                Assert.IsTrue(registered.Any(e => ReferenceEquals(e.Job, rootExtract) && e.Parent == null),
                    "Root ExtractJob should be registered without a parent.");
                Assert.IsTrue(registered.Any(e => ReferenceEquals(e.Job, rootList) && e.Parent == null),
                    "The extracted root JobList should be registered as a root-level replacement.");
                Assert.IsTrue(childExtracts.All(child => registered.Any(e => ReferenceEquals(e.Job, child) && ReferenceEquals(e.Parent, rootList))),
                    "Child ExtractJobs should be registered under the extracted JobList.");

                foreach (var child in childExtracts)
                    Assert.IsInstanceOfType(child.Result, typeof(SongJob));
                var childSongs = childExtracts.Select(e => (SongJob)e.Result!).ToList();
                Assert.IsTrue(childSongs.All(song => registered.Any(e => ReferenceEquals(e.Job, song) && ReferenceEquals(e.Parent, rootList))),
                    "Results of child ExtractJobs should be registered under the JobList, not under the transient ExtractJob.");

                Assert.IsTrue(createdResults.Any(e => ReferenceEquals(e.ExtractJob, rootExtract) && ReferenceEquals(e.Result, rootList)),
                    "JobResultCreated should link the root ExtractJob to its extracted JobList.");
                Assert.IsTrue(childExtracts.All(child => createdResults.Any(e => ReferenceEquals(e.ExtractJob, child) && ReferenceEquals(e.Result, child.Result))),
                    "JobResultCreated should link each child ExtractJob to its extracted SongJob.");

                Assert.IsTrue(stateChanges.Any(e => ReferenceEquals(e.Job, rootExtract) && e.State == JobState.Extracting),
                    "JobStateChanged should report Extracting for the root ExtractJob.");
                Assert.IsTrue(stateChanges.Any(e => ReferenceEquals(e.Job, rootExtract) && e.State == JobState.Done),
                    "JobStateChanged should report Done for the root ExtractJob.");
                Assert.IsTrue(childSongs.All(song => stateChanges.Any(e => ReferenceEquals(e.Job, song) && e.State == JobState.Failed)),
                    "JobStateChanged should report the terminal state for child SongJobs.");
                Assert.IsTrue(executionCompleted.Contains(rootExtract), "Root ExtractJob should raise JobExecutionCompleted.");
                Assert.IsTrue(executionCompleted.Contains(rootList), "Root JobList should raise JobExecutionCompleted.");
                Assert.IsTrue(childSongs.All(executionCompleted.Contains), "Leaf song jobs should raise JobExecutionCompleted.");
            }
            finally
            {
                if (System.IO.File.Exists(listFile)) System.IO.File.Delete(listFile);
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task EngineEvents_ReportTrackBatchResolved_ForDirectSongLists()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings
            {
                PrintOption = PrintOption.Tracks,
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

            Job? owner = null;
            IReadOnlyList<SongJob>? pending = null;
            IReadOnlyList<SongJob>? existing = null;
            IReadOnlyList<SongJob>? notFound = null;

            engine.Events.TrackBatchResolved += (job, batchPending, batchExisting, batchNotFound) =>
            {
                owner = job;
                pending = batchPending;
                existing = batchExisting;
                notFound = batchNotFound;
            };

            engine.Enqueue(list, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreSame(list, owner, "TrackBatchResolved should identify the owning job.");
            Assert.IsNotNull(pending, "TrackBatchResolved should publish the pending songs.");
            Assert.AreEqual(2, pending!.Count);
            Assert.AreEqual(0, existing!.Count);
            Assert.AreEqual(0, notFound!.Count);
        }

        [TestMethod]
        public async Task EngineEvents_AlbumJob_ExposesResolvedTarget_OnDownloadingState()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            var albumQuery = new AlbumQuery { Artist = "Artist One", Album = "Album One" };
            var albumJob = new AlbumJob(albumQuery);

            var songJob = new SongJob(new SongQuery { Artist = "Artist One", Title = "Track One", Album = "Album One" });
            var folder = new AlbumFolder("test_user", "C:\\Music\\Album One", new List<SongJob> { songJob });

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

            engine.Events.JobStateChanged += (job, state) =>
            {
                if (state == JobState.Downloading && job is AlbumJob aj)
                {
                    capturedFolder = aj.ResolvedTarget;
                }
            };

            engine.Enqueue(albumJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(capturedFolder, "ResolvedTarget should be populated when JobState.Downloading is reported.");
            Assert.AreEqual("C:\\Music\\Album One", capturedFolder.FolderPath);
        }
        [TestMethod]
        public void Job_PopulatesDiscoveryMetadata_BeforeStateChangedFires()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            DiscoverySummary? capturedDiscovery = null;
            JobState? capturedState = null;

            var events = new EngineEvents();
            events.JobStateChanged += (j, s) =>
            {
                capturedDiscovery = j.Discovery;
                capturedState = s;
            };

            song.Discovery = new DiscoverySummary { ResultCount = 5, LockedFileCount = 2 };
            song.UpdateState(JobState.Downloading);

            var raiseMethod = typeof(EngineEvents).GetMethod("RaiseJobStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            raiseMethod?.Invoke(events, [song, JobState.Downloading]);

            Assert.AreEqual(JobState.Downloading, capturedState);
            Assert.IsNotNull(capturedDiscovery);
            Assert.AreEqual(5, capturedDiscovery.ResultCount);
            Assert.AreEqual(2, capturedDiscovery.LockedFileCount);
        }

        [TestMethod]
        public void DiscoveryMetadata_PersistsForMultipleSubscribers()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            bool sub1SawIt = false;
            bool sub2SawIt = false;

            var events = new EngineEvents();
            events.JobStateChanged += (j, s) => sub1SawIt = j.Discovery != null;
            events.JobStateChanged += (j, s) => sub2SawIt = j.Discovery != null;

            song.Discovery = new DiscoverySummary { ResultCount = 1 };
            song.UpdateState(JobState.Downloading);

            var raiseMethod = typeof(EngineEvents).GetMethod("RaiseJobStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            raiseMethod?.Invoke(events, [song, JobState.Downloading]);

            Assert.IsTrue(sub1SawIt, "First subscriber should see metadata");
            Assert.IsTrue(sub2SawIt, "Second subscriber should see metadata (not consumed)");
        }

        [TestMethod]
        public async Task EngineEvents_JobStateChanged_ToFailed_ExtractJob_HasFailureReasonPopulated()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            
            // Pointing to a non-existent file will cause ListExtractor to throw FileNotFoundException
            var extractJob = new ExtractJob("invalid-input-that-throws.txt", InputType.List); 
            var client = new ClientTests.MockSoulseekClient([]);
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);

            FailureReason capturedReason = FailureReason.None;
            bool failedFired = false;

            engine.Events.JobStateChanged += (job, state) =>
            {
                if (ReferenceEquals(job, extractJob) && state == JobState.Failed)
                {
                    failedFired = true;
                    capturedReason = job.FailureReason;
                }
            };

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsTrue(failedFired, "JobStateChanged should fire with JobState.Failed.");
            Assert.AreEqual(FailureReason.ExtractionFailed, capturedReason, 
                "FailureReason must be populated BEFORE the JobStateChanged event is fired for ExtractJobs.");
        }

        [TestMethod]
        public async Task EngineEvents_JobStateChanged_ToFailed_NotFound_HasFailureReasonPopulated()
        {
            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = new DownloadSettings();
            
            var songJob = new SongJob(new SongQuery { Artist = "Nonexistent", Title = "Track" });
            var client = new ClientTests.MockSoulseekClient([]);
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);

            FailureReason capturedReason = FailureReason.None;
            bool failedFired = false;

            engine.Events.JobStateChanged += (job, state) =>
            {
                if (ReferenceEquals(job, songJob) && state == JobState.Failed)
                {
                    failedFired = true;
                    capturedReason = job.FailureReason;
                }
            };

            engine.Enqueue(songJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsTrue(failedFired, "JobStateChanged should fire with JobState.Failed.");
            Assert.AreEqual(FailureReason.NoSuitableFileFound, capturedReason, 
                "FailureReason must be populated BEFORE the JobStateChanged event is fired for not found items.");
        }

        [TestMethod]
        public async Task EngineEvents_JobStateChanged_ToFailed_Download_HasFailureReasonPopulated()
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

            FailureReason capturedReason = FailureReason.None;
            bool failedFired = false;

            engine.Events.JobStateChanged += (job, state) =>
            {
                if (ReferenceEquals(job, songJob) && state == JobState.Failed)
                {
                    failedFired = true;
                    capturedReason = job.FailureReason;
                }
            };

            engine.Enqueue(songJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsTrue(failedFired, "JobStateChanged should fire with JobState.Failed.");
            Assert.AreEqual(FailureReason.AllDownloadsFailed, capturedReason, 
                "FailureReason must be populated BEFORE the JobStateChanged event is fired for download failures.");
        }
    }
}

