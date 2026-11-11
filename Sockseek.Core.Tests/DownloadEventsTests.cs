using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Planning;
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
        public void IndependentFilesGroupBySubmissionPeerAndExactRemoteDirectory()
        {
            var events = new DownloadEvents();
            Guid submission = Guid.NewGuid(), otherSubmission = Guid.NewGuid();
            var captured = new List<TransferSnapshot>();
            events.DownloadStarted += change => captured.Add(change.Transfer);
            void Start(Guid scope, string peer, string path)
            {
                var job = new RemoteFileJob(new PeerFileTarget(new(peer, path), 1, ".bin")) { SubmissionId = scope };
                events.RaiseJobRegistered(job, null, null);
                events.RaiseDownloadStarted(Guid.NewGuid(), job, job.Target, "/unchanged/output.bin");
            }
            Start(submission, "Peer", @"Shares\曲\one.bin");
            Start(submission, "Other", @"Shares\曲\other.bin");
            Start(otherSubmission, "Peer", @"Shares\曲\later.bin");
            Start(submission, "Peer", @"Shares\曲\two.bin");
            Start(submission, "Peer", @"shares\曲\case.bin");
            Start(submission, "Peer", "Shares/曲/slash.bin");
            Start(submission, "Peer", "root.bin");
            Assert.AreEqual(captured[0].GroupRef, captured[3].GroupRef);
            Assert.AreNotEqual((captured[0].Username, captured[0].GroupRef), (captured[1].Username, captured[1].GroupRef));
            foreach (int index in new[] { 2, 4, 5 }) Assert.AreNotEqual(captured[0].GroupRef, captured[index].GroupRef);
            Assert.AreEqual(TransferGroupKind.Files, captured[0].GroupKind);
            Assert.AreEqual(@"Shares\曲", captured[0].GroupDisplayPath);
            Assert.AreEqual("one.bin", captured[0].GroupRelativePath);
            Assert.AreEqual("/unchanged/output.bin", captured[0].LocalPath);
            Assert.IsNull(captured[6].GroupRef);
        }

        [TestMethod]
        public async Task IndependentWorkflowsCanRetireFromConcurrentTerminalObservers()
        {
            var events = new DownloadEvents();
            var jobs = new[]
            {
                new RemoteFileJob(new PeerFileTarget(new("Peer", "First.bin"), 1, ".bin")),
                new RemoteFileJob(new PeerFileTarget(new("Peer", "Second.bin"), 1, ".bin")),
            };
            using var arrived = new CountdownEvent(2);
            int missed = 0;
            events.TransferCompleted += change =>
            {
                arrived.Signal();
                if (!arrived.Wait(TimeSpan.FromSeconds(5)))
                {
                    Interlocked.Increment(ref missed);
                    return;
                }
                events.RaiseWorkflowRetired(change.Job.WorkflowId, 1);
            };
            await Task.WhenAll(jobs.Select(job => Task.Run(() =>
                events.RaiseTransferCompleted(Guid.NewGuid(), job, job.Target, "output", 1, 1))))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, missed, "Both terminal observers must run concurrently.");
            Assert.AreEqual((0, 0, 0, 0, 0, 0), events.RetainedStateCounts);
        }

        [TestMethod]
        public void ConcurrentTransfersKeepIndependentClocksRetriesAndRetirement()
        {
            var events = new DownloadEvents();
            var first = new RemoteFileJob(new PeerFileTarget(new("Peer", @"Root\First.bin"), 128, ".bin"));
            var second = new RemoteFileJob(new PeerFileTarget(new("Peer", @"Root\Second.bin"), 128, ".bin"));
            Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
            Guid failedAttempt = Guid.NewGuid(), successfulAttempt = Guid.NewGuid();
            var captured = new System.Collections.Concurrent.ConcurrentQueue<TransferSnapshot>();
            var attempts = new List<(Guid Id, long Revision)>();
            events.DownloadStarted += change => captured.Enqueue(change.Transfer);
            events.DownloadProgress += change => captured.Enqueue(change.Transfer);
            events.TransferCompleted += change => captured.Enqueue(change.Transfer);
            events.TransferAttemptStarted += change => attempts.Add((change.AttemptId, change.AttemptRevision));
            events.TransferAttemptFailed += change => attempts.Add((change.AttemptId, change.AttemptRevision));
            events.TransferAttemptCompleted += change => attempts.Add((change.AttemptId, change.AttemptRevision));
            events.RaiseJobRegistered(first, null, null);
            events.RaiseJobRegistered(second, null, null);
            events.RaiseDownloadStarted(firstId, first, first.Target, "first");
            events.RaiseDownloadStarted(secondId, second, second.Target, "second");
            events.RaiseTransferAttemptStarted(firstId, failedAttempt, 1, first, first.Target, "first", "first.part");
            Parallel.For(0, 32, index =>
            {
                var job = index % 2 == 0 ? first : second;
                events.RaiseDownloadProgress(index % 2 == 0 ? firstId : secondId,
                    job, job.Target, index % 2 == 0 ? "first" : "second", index, 128);
            });
            events.RaiseTransferAttemptFailed(firstId, failedAttempt, 1, first, first.Target, "first", new IOException("failed"));
            events.RaiseTransferAttemptStarted(firstId, successfulAttempt, 2, first, first.Target, "first", "first.part");
            events.RaiseTransferAttemptCompleted(firstId, successfulAttempt, 2, first, first.Target, "first");
            events.RaiseTransferCompleted(firstId, first, first.Target, "first", 128, 2);
            int terminalCount = captured.Count;
            events.RaiseDownloadProgress(firstId, first, first.Target, "first", 0, 128);
            Assert.AreEqual(terminalCount, captured.Count, "Late progress cannot cross the terminal barrier.");
            events.RaiseWorkflowRetired(first.WorkflowId, 1);
            Assert.AreEqual(1, events.RetainedStateCounts.Transfers);
            var before = captured.Last(row => row.Id == secondId);
            events.RaiseDownloadProgress(secondId, second, second.Target, "second", 64, 128);
            var after = captured.Last();
            Assert.AreEqual(before.Revision + 1, after.Revision);
            Assert.AreEqual(64L, after.BytesTransferred);
            Assert.AreEqual(0L, captured.First(row => row.Id == secondId).BytesTransferred);
            events.RaiseTransferCompleted(secondId, second, second.Target, "second", 128, 1);
            foreach (Guid id in new[] { firstId, secondId })
            {
                var rows = captured.Where(row => row.Id == id).ToArray();
                Assert.IsTrue(rows.Zip(rows.Skip(1)).All(pair => pair.First.Revision < pair.Second.Revision),
                    "Concurrent publishers must deliver each transfer's revisions in order.");
            }
            CollectionAssert.AreEqual(new[] { (failedAttempt, 1L), (failedAttempt, 2L),
                (successfulAttempt, 1L), (successfulAttempt, 2L) }, attempts.ToArray());
            events.RaiseWorkflowRetired(second.WorkflowId, 1);
            Assert.AreEqual((0, 0, 0, 0, 0, 0), events.RetainedStateCounts);
        }

        [TestMethod]
        public void PresentationNotifications_DoNotInspectDescendantJobs()
        {
            var events = new DownloadEvents();
            var changes = new List<CoreChange>();
            events.ChangePublished += changes.Add;
            var list = new JobList("collection", [new UnvisitedJob()]);
            events.RaiseJobStatus(list, "moving");
            events.RaiseJobMessage(list, Microsoft.Extensions.Logging.LogLevel.Information, null, "message");
            events.RaiseListProgress(list, 1, 0, 1);
            events.RaiseTrackBatchResolved(list, [], [], []);
            events.RaiseEngineCompleted(list);

            Assert.AreEqual(5, changes.Count);
            Assert.AreEqual(list.Id, ((JobStatusChange)changes[0]).Job.Id);
            Assert.AreEqual("collection", ((ListProgressChange)changes[2]).List.ItemName);
            Assert.AreEqual(list.Id, ((EngineCompletedChange)changes[4]).Queue.Id);
        }

        private sealed class UnvisitedJob : Job
        {
            protected override bool DefaultCanBeSkipped => false;
            public override string ToString(bool noInfo)
                => throw new InvalidOperationException("A presentation notification must not visit descendants.");
        }

        [TestMethod]
        public void LifecycleCapture_UsesMemberOutcomesWithoutVisitingDescendants()
        {
            var events = new DownloadEvents();
            var changes = new List<JobStateChangedChange>();
            events.JobStateChanged += changes.Add;
            var child = new UnvisitedJob();
            var list = new JobList("collection", [child]);
            events.RaiseJobStateChanged(list);
            child.SetDone();
            events.RaiseJobStateChanged(list);
            Assert.AreEqual(new JobOutcomeSummary(1, 0, 0, 0),
                ((JobListExecutionPayload)changes[0].Job.Payload).Outcomes);
            Assert.AreEqual(new JobOutcomeSummary(1, 1, 1, 0),
                ((JobListExecutionPayload)changes[1].Job.Payload).Outcomes);
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void DirectoryTransfers_KeepPlannedNestedPaths(bool album, bool explicitPlan)
        {
            var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Shares\Album\Artwork\cover.jpg"), 10, ".jpg");
            var plan = new DirectoryTransferPlan(explicitPlan ? "My selection" : "Album", [new DirectoryTransferEntry(target, ["Artwork"])],
                sourceDirectory: explicitPlan ? null : new PeerDirectoryIdentity("Peer", @"Shares\Album"));
            DirectoryDownloadJob parent = album ? new AlbumJob(new AlbumQuery { Album = "Album" })
                : new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));
            if (album) parent.BeginDirectoryAttempt(plan);
            FileDownloadJob child = album ? new SongJob(new SongQuery()) : new RemoteFileJob(target);
            var events = new DownloadEvents();
            TransferSnapshot? observed = null;
            events.DownloadStarted += change => observed = change.Transfer;
            events.RaiseJobRegistered(child, parent, null, plan.Entries[0]);
            events.RaiseDownloadStarted(Guid.NewGuid(), child, target, "/downloads/Album/Artwork/cover.jpg");

            Assert.IsNotNull(observed);
            Assert.AreEqual(explicitPlan ? "My selection" : @"Shares\Album", observed.GroupDisplayPath);
            Assert.AreEqual("Artwork/cover.jpg", observed.GroupRelativePath);
            Assert.AreEqual(target.Filename, observed.RemotePath);
            Assert.AreEqual(parent.Id, Guid.Parse(observed.GroupRef!));
            var first = observed;
            events.DownloadProgress += change => observed = change.Transfer;
            events.UpdateDirectoryGroup(child, null, null);
            events.RaiseDownloadProgress(first.Id, child, target, "/moved/cover.jpg", 5, 10);
            Assert.AreNotEqual(first.GroupRef, observed.GroupRef, "Removed directory ownership must fall back to file grouping.");
            Assert.AreEqual(TransferGroupKind.Files, observed.GroupKind);
            Assert.AreEqual("cover.jpg", observed.GroupRelativePath);
            Assert.AreEqual("/moved/cover.jpg", observed.LocalPath);
            Assert.AreEqual(parent.Id, Guid.Parse(first.GroupRef!));
            Assert.AreEqual("Artwork/cover.jpg", first.GroupRelativePath);
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
            var laterObservers = 0;
            CoreChange? published = null;

            Action<JobRegisteredChange> existing = _ => successfulSpecificObservers++;
            Action<JobRegisteredChange> added = _ => laterObservers++;
            events.JobRegistered += _ =>
            {
                events.JobRegistered -= existing;
                events.JobRegistered += added;
                throw new InvalidOperationException("observer failed");
            };
            events.JobRegistered += existing;
            events.ChangePublished += change => published = change;

            events.RaiseJobRegistered(job, null!, null!);

            Assert.AreEqual(1, successfulSpecificObservers);
            Assert.IsNotNull(published);
            Assert.AreEqual(now, published.OccurredAtUtc);
            Assert.AreEqual(now, job.CreatedAtUtc);
            Assert.AreEqual(now, ((JobRegisteredChange)published).Job.CreatedAtUtc);
            Assert.AreEqual(0, laterObservers, "Subscription changes take effect on the next delivery.");

            events.RaiseJobRegistered(new SearchJob("next"), null!, null!);
            Assert.AreEqual(1, successfulSpecificObservers, "The removed observer still receives the first delivery.");
            Assert.AreEqual(1, laterObservers, "The added observer receives the next delivery despite its failing sibling.");
        }

        [TestMethod]
        public void DownloadEvents_RegistrationPreservesAnAlreadyAcceptedCreationTime()
        {
            var registeredAt = new DateTimeOffset(2032, 4, 5, 6, 7, 8, TimeSpan.Zero);
            var acceptedAt = registeredAt.AddMinutes(-2);
            var events = new DownloadEvents(new FixedTimeProvider(registeredAt));
            var job = new SongJob(new SongQuery { Title = "Track" }) { CreatedAtUtc = acceptedAt };
            JobRegisteredChange? registered = null;
            events.JobRegistered += change => registered = change;

            events.RaiseJobRegistered(job, null!, null!);

            Assert.IsNotNull(registered);
            Assert.AreEqual(registeredAt, registered.OccurredAtUtc);
            Assert.AreEqual(acceptedAt, registered.Job.CreatedAtUtc);
            Assert.AreEqual(acceptedAt, job.CreatedAtUtc);
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
            DownloadProgressedChange? captured = null;
            events.DownloadProgress += change => captured = change;

            events.JobRegistered += change => observedRevisions.Add(change.Job.Revision);
            events.DownloadStarted += change => observedRevisions.Add(change.Job.Revision);
            events.DownloadProgress += change => observedRevisions.Add(change.Job.Revision);
            events.DownloadStateChanged += change => observedRevisions.Add(change.Job.Revision);
            events.DownloadAttemptFailed += change => observedRevisions.Add(change.Job.Revision);

            events.RaiseJobRegistered(song, null!, null!);
            song.DownloadPath = "C:/downloads/final-name.mp3";
            song.FileSize = 700;
            song.BytesTransferred = 32;
            Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");
            Invoke(events, "RaiseDownloadProgress", transferId, song, candidate.Target, "C:/downloads/Track.mp3", 100L, 10_000L);
            song.BytesTransferred = 64;
            Invoke(events, "RaiseDownloadStateChanged", transferId, song, candidate.Target, "C:/downloads/Track.mp3", TransferStates.InProgress, 100L, 10_000L);
            Invoke(events, "RaiseDownloadAttemptFailed", transferId, song, candidate.Target, "C:/downloads/Track.mp3", "C:/downloads/Track.mp3.incomplete", 1, 3, new IOException("failed"));

            CollectionAssert.AreEqual(new long[] { 1, 1, 1, 1, 1 }, observedRevisions);
            Assert.IsNotNull(captured);
            Assert.AreEqual("C:/downloads/final-name.mp3", captured.File.DownloadPath);
            Assert.AreEqual(32L, captured.File.BytesTransferred);
            Assert.AreEqual(700L, captured.File.FileSize);
            Assert.AreEqual("C:/downloads/Track.mp3", captured.Transfer.LocalPath);
            Assert.AreEqual(100L, captured.Transfer.BytesTransferred);
            Assert.AreEqual(10_000L, captured.Transfer.TotalBytes);
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
            IReadOnlyList<JobReferenceSnapshot>? pending = null;
            IReadOnlyList<JobReferenceSnapshot>? existing = null;
            IReadOnlyList<JobReferenceSnapshot>? notFound = null;

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
        public async Task ConcurrentJobs_LimitsSongsWithinSongGroupsJob()
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

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-concurrent-group-" + Guid.NewGuid());
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
                downloadSettings.Search.MinGroupShares = 1;
                downloadSettings.Skip.SkipExisting = false;

                var songGroupsJob = new SongGroupsJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
                SubmissionIdentity.AssignAccepted(songGroupsJob, downloadSettings);
                var downloadGate = new TestHelpers.DownloadGate();
                var client = new ClientTests.MockSoulseekClient(index)
                {
                    BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
                };
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                await using var engine = new DownloadEngine(engineSettings, clientManager, retireTerminalWorkflows: true);

                var activeSongs = new HashSet<Guid>();
                var registered = new List<JobRegisteredChange>();
                int maxActiveSongs = 0;
                object gate = new();

                engine.Events.JobRegistered += change =>
                {
                    lock (gate) registered.Add(change);
                };
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

                engine.Enqueue(songGroupsJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await downloadGate.WaitForStartedCountAsync(1);
                Assert.AreEqual(1, downloadGate.StartedCount, "A second group song must not start while the first holds the global job slot.");
                await CompleteRunWithBlockedDownloads(downloadGate, runTask);

                Assert.IsTrue(songGroupsJob.Songs.Count >= 2, "Song Groups should produce multiple song jobs for this test.");
                Assert.AreEqual(1, maxActiveSongs, "--concurrent-jobs=1 should allow only one group child song to download at a time.");
                Assert.IsTrue(songGroupsJob.Songs.All(song => song.TerminalOutcome == JobTerminalOutcome.Succeeded));
                AssertGroupRegistrationOwnership(songGroupsJob, songGroupsJob.Songs, registered);
                AssertGroupRetired(engine, songGroupsJob, registered);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task ConcurrentJobs_LimitsAlbumsWithinAlbumGroupsJob()
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

            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-concurrent-album-groups-" + Guid.NewGuid());
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
                downloadSettings.Search.MinGroupShares = 1;
                downloadSettings.Search.NoBrowseFolder = true;
                downloadSettings.Skip.SkipExisting = false;

                var songGroupsJob = new AlbumGroupsJob(new AlbumQuery { Artist = "ELO" });
                SubmissionIdentity.AssignAccepted(songGroupsJob, downloadSettings);
                var downloadGate = new TestHelpers.DownloadGate();
                var client = new ClientTests.MockSoulseekClient(index)
                {
                    BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
                };
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                await using var engine = new DownloadEngine(engineSettings, clientManager, retireTerminalWorkflows: true);

                var activeAlbums = new HashSet<Guid>();
                var registered = new List<JobRegisteredChange>();
                int maxActiveAlbums = 0;
                object gate = new();

                engine.Events.JobRegistered += change =>
                {
                    lock (gate) registered.Add(change);
                };
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

                engine.Enqueue(songGroupsJob, downloadSettings);
                engine.CompleteEnqueue();

                var runTask = engine.RunAsync(CancellationToken.None);
                await downloadGate.WaitForStartedCountAsync(1);
                Assert.AreEqual(1, downloadGate.StartedCount, "A second group album must not start while the first album job holds the global job slot.");
                await CompleteRunWithBlockedDownloads(downloadGate, runTask);

                Assert.IsTrue(songGroupsJob.Albums.Count >= 2, "Album group should produce multiple album jobs for this test.");
                Assert.AreEqual(1, maxActiveAlbums, "--concurrent-jobs=1 should allow only one album-groups child album to download at a time.");
                Assert.IsTrue(songGroupsJob.Albums.All(album => album.TerminalOutcome == JobTerminalOutcome.Succeeded));
                AssertGroupRegistrationOwnership(songGroupsJob, songGroupsJob.Albums, registered);
                AssertGroupRetired(engine, songGroupsJob, registered);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }
        }

        private static void AssertGroupRetired(
            DownloadEngine engine, Job root, IReadOnlyList<JobRegisteredChange> registered)
        {
            Assert.AreEqual(0, engine.GetJobsByWorkflow(root.WorkflowId).Count);
            Assert.IsTrue(registered.All(change => engine.GetJob(change.Job.Id) == null),
                "Terminal daemon groups must release their generated jobs.");
            Assert.AreEqual(0, engine.RetainedStateCounts.Contexts,
                "No child context may be left under an unrelated workflow lifetime.");
            Assert.AreEqual(0, engine.RetainedStateCounts.EventTransfers,
                "Retired transfers must release their immutable metadata and lifecycle facts.");
            Assert.IsNull(root.Cts);
        }

        private static void AssertGroupRegistrationOwnership(
            Job root, IEnumerable<Job> children, IReadOnlyList<JobRegisteredChange> registered)
        {
            var group = registered.Single(change => change.ParentJobId == root.Id
                && change.Job.Kind == JobSnapshotKind.JobList);
            Assert.AreEqual(JobSnapshotKind.JobList, group.Job.Kind);
            Assert.AreEqual(root.WorkflowId, group.Job.WorkflowId);
            Assert.AreEqual(root.SubmissionId, group.Job.SubmissionId);
            Assert.AreEqual(JobSemanticRole.Orchestration, group.Job.SemanticRole);
            foreach (var child in children)
            {
                var registration = registered.Single(change => change.Job.Id == child.Id);
                Assert.AreEqual(root.Id, registration.ParentJobId,
                    "Both group modes expose generated members directly; the execution list is orchestration.");
                Assert.AreEqual(root.WorkflowId, registration.Job.WorkflowId,
                    "Children must have their final workflow identity at first publication.");
                Assert.AreEqual(root.SubmissionId, registration.Job.SubmissionId);
                Assert.AreEqual(JobSemanticRole.ExecutionChild, registration.Job.SemanticRole);
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
        [DataRow(false, false, false)]
        [DataRow(true, false, false)]
        [DataRow(true, true, false)]
        [DataRow(true, true, true)]
        public async Task AlbumValidation_RespectsUserAcceptedSelections(bool userAccepted, bool loadFullFolder, bool exactFiles)
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
                var badFolder = AlbumFolderFromSearch(badResponse, userAccepted ? badFiles.Skip(1) : badFiles.Take(1));
                var goodFolder = AlbumFolderFromSearch(goodResponse, goodFiles.Take(1));
                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Results = [badFolder, goodFolder],
                    ResolvedTarget = userAccepted ? badFolder : null,
                    ValidationRequirement = userAccepted ? AlbumValidationRequirement.UserAccepted : AlbumValidationRequirement.Standard,
                    DirectoryResolutionPolicy = exactFiles ? AlbumDirectoryResolutionPolicy.UseSelectedSnapshot : AlbumDirectoryResolutionPolicy.CompleteIfNeeded,
                };

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var downloadSettings = AlbumDownloadSettings(outputDir);
                downloadSettings.Search.NoBrowseFolder = !loadFullFolder;
                downloadSettings.Search.NecessaryCond.Formats = ["flac"];
                downloadSettings.Search.StrictAlbumQuality = true;
                if (userAccepted) downloadSettings.Search.NecessaryFolderCond.MinTrackCount = 3;

                var client = new ClientTests.MockSoulseekClient([badResponse, goodResponse]);
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);
                var downloadsStarted = 0;
                engine.Events.DownloadStarted += _ => downloadsStarted++;

                engine.Enqueue(album, downloadSettings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                if (userAccepted)
                {
                    bool expand = loadFullFolder && !exactFiles;
                    Assert.AreSame(badFolder, album.ResolvedTarget, "Discovery conditions must not override the user's chosen folder.");
                    Assert.AreEqual(expand ? 1 : 0, client.BrowseCallCount, "Only the user's full-folder option may expand a selection.");
                    Assert.AreEqual(expand ? 2 : 1, downloadsStarted);
                    Assert.AreEqual(expand ? 2 : 1, client.DownloadCallCount);
                    Assert.IsTrue(album.TrackJobs.Any(track => track.ResolvedTarget!.Filename.EndsWith(".mp3")), "The explicitly selected MP3 must survive FLAC-only discovery conditions.");
                }
                else
                {
                    Assert.AreSame(goodFolder, album.ResolvedTarget, "Automatic selection must still reject hidden non-matching files and try the next folder.");
                    Assert.AreEqual(2, client.BrowseCallCount);
                    Assert.AreEqual(0, client.DownloadCallCountAtFirstBrowse);
                    Assert.AreEqual(2, downloadsStarted);
                    Assert.AreEqual(2, client.DownloadCallCount);
                }
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

            events.RaiseJobStateChanged(song);

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

            events.RaiseJobStateChanged(song);

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
