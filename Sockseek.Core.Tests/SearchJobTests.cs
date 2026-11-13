using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using System.Collections.Concurrent;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Unit
{
    [TestClass]
    public class SearchJobTests
    {
        [TestMethod]
        public void SearchSession_AddResponse_TracksRawResultsAndRevision()
        {
            var session = new SearchSession();
            var file = TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180);
            var response = new SearchResponse("User1", 1, true, 100, 2, [file]);
            var rawEvents = 0;
            session.RawResultReceived += _ => rawEvents++;

            session.AddResponse(response);
            session.AddResponse(response);

            Assert.AreEqual(1, session.ResultCount, "Duplicate raw result keys should not be added twice.");
            Assert.AreEqual(1, session.Revision, "Revision should change only when a new raw result is added.");
            Assert.AreEqual(1, rawEvents, "Raw result event should fire only for newly added files.");
        }

        [TestMethod]
        public void SearchSession_DistinguishesPeerAndFilenameComponentsThatContainSeparators()
        {
            var session = new SearchSession();
            session.AddResponse(new SearchResponse("alpha", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"beta\track.mp3", length: 180),
            ]));
            session.AddResponse(new SearchResponse(@"alpha\beta", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile("track.mp3", length: 181),
            ]));

            Assert.AreEqual(2, session.ResultCount,
                "Exact peer/file identities must not collide when either component contains a path separator.");
            CollectionAssert.AreEquivalent(
                new[] { "alpha", @"alpha\beta" },
                session.RawSnapshot().Select(result => result.Username).ToArray());
        }

        [TestMethod]
        public void SearchSession_AddResponse_PublishesImmutableSearchResultChanges()
        {
            var jobId = Guid.NewGuid();
            var session = new SearchSession(jobId);
            var file = TestHelpers.CreateSlFile(@"Music\Artist\Track.flac", bitrate: 1000, length: 180);
            var response = new SearchResponse("User1", 1, true, 123_456, 2, [file]);
            SearchResultsAddedChange? added = null;
            SearchCompletedChange? completed = null;

            session.ResultsAdded += change => added = change;
            session.SearchCompleted += change => completed = change;

            session.AddResponse(response);
            session.Complete();

            Assert.IsNotNull(added);
            Assert.AreEqual(jobId, added.JobId);
            Assert.AreEqual(1, added.Revision);
            Assert.AreEqual(1, added.Results.Count);
            Assert.AreEqual("User1", added.Results[0].Username);
            Assert.AreEqual(@"Music\Artist\Track.flac", added.Results[0].Filename);
            Assert.AreEqual(file.Size, added.Results[0].Size);
            Assert.AreEqual(file.Extension, added.Results[0].Extension);
            Assert.AreEqual(response.UploadSpeed, added.Results[0].UploadSpeed);
            Assert.AreEqual(response.HasFreeUploadSlot, added.Results[0].HasFreeUploadSlot);
            Assert.IsNotNull(completed);
            Assert.AreEqual(jobId, completed.JobId);
            Assert.AreEqual(2, completed.Revision);
        }

        [TestMethod]
        public void SearchSession_RetainsLockedRowsAndOnePeerObservationWithoutMakingThemDownloadCandidates()
        {
            var now = new DateTimeOffset(2034, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var session = new SearchSession(Guid.NewGuid(), new FixedTimeProvider(now));
            var publicFile = TestHelpers.CreateSlFile(@"Music\Artist\Public.flac", length: 180);
            var lockedFile = TestHelpers.CreateSlFile(@"Private\Artist\Locked.flac", length: 181);
            var response = new SearchResponse(
                "ExactPeer",
                1,
                hasFreeUploadSlot: false,
                uploadSpeed: 12_345,
                queueLength: 7,
                fileList: [publicFile],
                lockedFileList: [lockedFile]);

            session.AddResponse(response);
            session.Complete();

            Assert.AreEqual(1, session.PublicFileCount);
            Assert.AreEqual(1, session.LockedFileCount);
            Assert.AreEqual(1, session.ObservedPeerCount);
            SearchRawResult[] rows = session.RawSnapshot().ToArray();
            Assert.AreEqual(2, rows.Length);
            Assert.AreEqual(SearchResultVisibility.Public, rows[0].Visibility);
            Assert.AreEqual(SearchResultVisibility.Locked, rows[1].Visibility);
            Assert.AreEqual(7, rows[0].QueueLength);
            Assert.AreEqual(now, rows[0].ObservedAtUtc);
            Assert.AreEqual(now, rows[1].ObservedAtUtc);
            Assert.AreEqual(1, session.Snapshot().Count);
        }

        [TestMethod]
        public void SearchSession_UsesInjectedClock_AndIsolatesObservers()
        {
            var now = new DateTimeOffset(2033, 5, 6, 7, 8, 9, TimeSpan.Zero);
            var session = new SearchSession(Guid.NewGuid(), new FixedTimeProvider(now));
            var file = TestHelpers.CreateSlFile(@"Music\Artist\Track.flac", length: 180);
            var response = new SearchResponse("User1", 1, true, 100, 0, [file]);
            var successfulObservers = 0;
            var published = new List<CoreChange>();
            var observerFailures = new List<(string Name, Exception Exception)>();

            session.ResultsAdded += _ => throw new InvalidOperationException("observer failed");
            session.ResultsAdded += _ => successfulObservers++;
            session.ChangePublished += published.Add;
            session.ObserverFailed += (name, exception) => observerFailures.Add((name, exception));

            session.AddResponse(response);
            session.Complete();

            Assert.AreEqual(1, successfulObservers);
            Assert.AreEqual(1, observerFailures.Count);
            Assert.AreEqual(nameof(session.ResultsAdded), observerFailures[0].Name);
            Assert.AreEqual("observer failed", observerFailures[0].Exception.Message);
            Assert.AreEqual(2, published.Count);
            Assert.IsTrue(published.All(change => change.OccurredAtUtc == now));
        }

        [TestMethod]
        [DataRow("raw-received")]
        [DataRow("raw-added")]
        [DataRow("results-added")]
        [DataRow("change-published")]
        public void SearchSession_ObserverCompletion_FollowsEveryAdmittedResult(string notification)
        {
            var session = new SearchSession(Guid.NewGuid());
            var changes = new List<CoreChange>();
            var observed = new List<string>();
            SubscribeToResultNotification(session, notification, session.Complete);
            session.RawResultAdded += result => observed.Add($"file-{result.Sequence}");
            session.ChangePublished += changes.Add;
            session.Completed += () => observed.Add("complete");

            session.AddResponse(new SearchResponse("peer", 1, true, 100, 0,
                [TestHelpers.CreateSlFile(@"Share\One.flac"), TestHelpers.CreateSlFile(@"Share\Two.flac")]));

            Assert.IsTrue(session.IsComplete);
            CollectionAssert.AreEqual(new[] { "file-1", "file-2", "complete" }, observed);
            CollectionAssert.AreEqual(new[] { typeof(SearchResultsAddedChange), typeof(SearchCompletedChange) },
                changes.Select(change => change.GetType()).ToArray(),
                "A consumer must receive admitted files before the terminal cursor that covers them.");
            Assert.AreEqual(3, ((SearchCompletedChange)changes[1]).Revision);
        }

        [TestMethod]
        [DataRow("raw-received")]
        [DataRow("raw-added")]
        [DataRow("results-added")]
        [DataRow("change-published")]
        public void SearchSession_ObserverAdmission_PreservesSourceAndEventOrder(string notification)
        {
            var session = new SearchSession(Guid.NewGuid());
            var changes = new List<CoreChange>();
            var observed = new List<long>();
            bool admitted = false;
            SubscribeToResultNotification(session, notification, () =>
            {
                if (admitted) return;
                admitted = true;
                session.AddResponse(new SearchResponse("second-peer", 2, true, 100, 0,
                    [TestHelpers.CreateSlFile(@"Share\Three.flac")]));
            });
            session.RawResultAdded += result => observed.Add(result.Sequence);
            session.ChangePublished += changes.Add;

            session.AddResponse(new SearchResponse("peer", 1, true, 100, 0,
                [TestHelpers.CreateSlFile(@"Share\One.flac"), TestHelpers.CreateSlFile(@"Share\Two.flac")]));
            session.Complete();

            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, observed);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, changes.OfType<SearchResultsAddedChange>()
                .SelectMany(change => change.Results).Select(result => result.Sequence).ToArray(),
                "Persistence and incremental readers must never skip a lower sequence delivered later.");
            CollectionAssert.AreEqual(changes.Select(change => change.Sequence).Order().ToArray(),
                changes.Select(change => change.Sequence).ToArray());
            Assert.IsInstanceOfType<SearchCompletedChange>(changes[^1]);
        }

        private static void SubscribeToResultNotification(SearchSession session, string notification, Action callback)
        {
            switch (notification)
            {
                case "raw-received": session.RawResultReceived += _ => callback(); break;
                case "raw-added": session.RawResultAdded += _ => callback(); break;
                case "results-added": session.ResultsAdded += _ => callback(); break;
                case "change-published": session.ChangePublished += change =>
                    { if (change is SearchResultsAddedChange) callback(); }; break;
                default: throw new ArgumentOutOfRangeException(nameof(notification));
            }
        }

        [TestMethod]
        public async Task SearchSession_CompletionBarrier_RejectsConcurrentLateResults()
        {
            var session = new SearchSession(Guid.NewGuid());
            var completionPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCompletionObserver = new ManualResetEventSlim();
            session.SearchCompleted += _ =>
            {
                completionPublished.TrySetResult();
                releaseCompletionObserver.Wait();
            };
            var completeTask = Task.Factory.StartNew(session.Complete,
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task? addTask = null;
            try
            {
                await completionPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var readable = await Task.Run(() => (Rows: session.RawSnapshot().Count,
                    Peers: session.ObservedPeerCount)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual((0, 0), readable,
                    "Completed session facts remain readable while notification is pending.");
                var file = TestHelpers.CreateSlFile(@"Music\Artist\Late.flac", length: 180);
                var response = new SearchResponse("LateUser", 1, true, 100, 3, [file]);
                addTask = Task.Factory.StartNew(() => session.AddResponse(response),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            finally
            {
                releaseCompletionObserver.Set();
                await completeTask.WaitAsync(TimeSpan.FromSeconds(5));
                if (addTask != null) await addTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual(0, session.ResultCount);
            Assert.AreEqual(1, session.Revision);
            Assert.AreEqual(0, session.LockedFileCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SearchSession_AdmittedResultsRemainReadableWhileObserversAreBlocked(bool rawObserver)
        {
            var session = new SearchSession(Guid.NewGuid());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changes = new ConcurrentQueue<CoreChange>();
            session.ChangePublished += changes.Enqueue;
            void Hold()
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            if (rawObserver) session.RawResultAdded += _ => Hold();
            else session.ResultsAdded += _ => Hold();
            var response = new SearchResponse(" Exact Péér ", 3, true, 100, 0,
                [TestHelpers.CreateSlFile(@"Share\One.flac"), TestHelpers.CreateSlFile(@"Share\Two.flac")],
                [TestHelpers.CreateSlFile(@"Share\Locked.flac")]);
            var producer = Task.Factory.StartNew(() => session.AddResponse(response),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task? completion = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                completion = Task.Factory.StartNew(session.Complete,
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var read = Task.Run(() => (Rows: session.RawSnapshot(),
                    Page: session.RawSnapshot(1, 2), Projection: session.ReadProjectionPage(1, 2), Peers: session.ObservedPeerCount,
                    Public: session.Snapshot().Count));
                var snapshot = await read.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(3, snapshot.Rows.Count,
                    "An admitted response must be readable before its slow observer returns.");
                CollectionAssert.AreEqual(new long[] { 2, 3 }, snapshot.Page.Select(row => row.Sequence).ToArray());
                CollectionAssert.AreEqual(new long[] { 2, 3 }, snapshot.Projection.Items.Select(row => row.Sequence).ToArray());
                Assert.AreEqual(3, snapshot.Projection.Revision);
                Assert.IsFalse(snapshot.Projection.IsComplete);
                Assert.AreEqual(snapshot.Rows.Count, session.RawSnapshot(-1, int.MaxValue).Count);
                Assert.AreEqual(0, session.RawSnapshot(long.MaxValue, 500).Count);
                Assert.AreEqual(1, snapshot.Peers);
                Assert.AreEqual(2, snapshot.Public);
                Assert.IsTrue(snapshot.Rows.All(row => row.Username == " Exact Péér "));
                Assert.IsFalse(session.IsComplete, "Completion cannot overtake accepted result notifications.");
                Assert.IsFalse(changes.Any(change => change is SearchCompletedChange));
            }
            finally
            {
                release.TrySetResult();
                await producer.WaitAsync(TimeSpan.FromSeconds(5));
                if (completion != null) await completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.IsTrue(session.IsComplete);
            CollectionAssert.AreEqual(new[] { typeof(SearchResultsAddedChange), typeof(SearchCompletedChange) },
                changes.Select(change => change.GetType()).ToArray());
            Assert.IsTrue(changes.First().Sequence < changes.Last().Sequence);
            Assert.AreEqual(3, ((SearchResultsAddedChange)changes.First()).Results.Count);
            Assert.AreEqual(4, session.Revision);
            Assert.IsFalse(session.ReadProjectionPage(0, 2).IsComplete, "A complete source can still have a continuation page.");
            var tail = session.ReadProjectionPage(2, 2);
            Assert.AreEqual(4, tail.Revision);
            Assert.IsTrue(tail.IsComplete);
            Assert.AreEqual(3L, tail.Items.Single().Sequence);
        }

        private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => utcNow;
        }

        [TestMethod]
        public void SearchJob_TypedProjectionCache_ReusesSameRevisionAndInvalidatesOnNewRawResult()
        {
            var config = TestHelpers.CreateDefaultSettings().Download;
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180),
            ]));

            var peerFailureCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetSortedTrackCandidates(config.Search, peerFailureCounts);
            var second = job.GetSortedTrackCandidates(config.Search, peerFailureCounts);

            job.Session.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\Artist\Track Alt.mp3", length: 181),
            ]));

            var third = job.GetSortedTrackCandidates(config.Search, peerFailureCounts);

            Assert.AreSame(first, second);
            Assert.AreEqual(1, first.Items.Count);
            Assert.AreEqual(2, third.Items.Count);
            Assert.AreNotSame(first, third);
        }

        [TestMethod]
        public void SearchJob_ProjectionCacheDoesNotAliasDistinctDelimiterBearingQueries()
        {
            var job = new SearchJob("cache identity");
            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var peerFailureCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetSortedTrackCandidates(
                new FileSearchProjection(new SongQuery { Artist = "a\0b", Title = "c" }, true),
                search,
                peerFailureCounts);
            var second = job.GetSortedTrackCandidates(
                new FileSearchProjection(new SongQuery { Artist = "a", Title = "b\0c" }, true),
                search,
                peerFailureCounts);

            Assert.AreNotSame(first, second,
                "Distinct query components must not alias the same incremental projector state.");
        }

        [TestMethod]
        public async Task SearchJob_ReadRawResultsAsync_ReplaysExistingAndStreamsUntilComplete()
        {
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var file1 = TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180);
            var response1 = new SearchResponse("User1", 1, true, 100, 0, [file1]);
            job.Session.AddResponse(response1);

            var results = new List<SearchRawResult>();
            var replayRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readerTask = Task.Run(async () =>
            {
                await foreach (var result in job.ReadRawResultsAsync())
                {
                    results.Add(result);
                    replayRead.TrySetResult();
                }
            });

            await replayRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var file2 = TestHelpers.CreateSlFile(@"Music\Artist\Track Alt.mp3", length: 181);
            var response2 = new SearchResponse("User2", 1, true, 100, 0, [file2]);
            job.Session.AddResponse(response2);
            job.Session.Complete();

            await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(1, results[0].Sequence);
            Assert.AreEqual(2, results[1].Sequence);
            Assert.AreEqual("User1", results[0].Username);
            Assert.AreEqual("User2", results[1].Username);
        }

        [TestMethod]
        public async Task Searcher_SearchJob_UsesPreexistingSessionForLiveRawResults()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180),
                ]),
            };
            var config = TestHelpers.CreateDefaultSettings().Download;
            var registry = TestHelpers.CreatePeerFailureTracker();
            var searcher = new Searcher(new ClientTests.MockSoulseekClient(index), registry, new DownloadEvents(), 10, 10);
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var originalSession = job.Session;
            var streamed = new List<SearchRawResult>();
            var readerTask = Task.Run(async () =>
            {
                await foreach (var result in job.ReadRawResultsAsync())
                    streamed.Add(result);
            });

            await searcher.Search(job, config.Search, new ResponseData(), CancellationToken.None);
            await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreSame(originalSession, job.Session);
            Assert.IsTrue(job.IsComplete);
            Assert.AreEqual(1, streamed.Count);
            Assert.AreEqual(@"Music\Artist\Track.mp3", streamed[0].Filename);
        }

        [TestMethod]
        public void SearchJob_TypedTrackProjection_IsCachedByRevisionAndCompletion()
        {
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", bitrate: 320, length: 180),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var peerFailureCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetSortedTrackCandidates(search, peerFailureCounts);
            var second = job.GetSortedTrackCandidates(search, peerFailureCounts);

            job.Session.Complete();
            var completed = job.GetSortedTrackCandidates(search, peerFailureCounts);

            Assert.AreSame(first, second, "Typed projections should reuse the same snapshot while revision and completion state are unchanged.");
            Assert.AreEqual(1, first.Revision);
            Assert.IsFalse(first.IsComplete);
            Assert.IsTrue(completed.IsComplete);
            Assert.AreEqual(first.Revision + 1, completed.Revision);
            Assert.AreNotSame(first, completed, "Completion changes should invalidate cached snapshots even when no new raw results arrived.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SearchJob_RelaxesAConditionAfterCompletionWithoutAnotherNetworkSearch(bool album)
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Music\Artist\Album\Track.mp3", bitrate: 320, length: 180),
                ]),
            };
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.NecessaryCond.Formats = ["flac"];
            var registry = TestHelpers.CreatePeerFailureTracker();
            var client = new ClientTests.MockSoulseekClient(index);
            var searcher = new Searcher(client, registry, new DownloadEvents(), 10, 10);
            var job = album ? new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                : new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });

            await searcher.Search(job, config.Search, new ResponseData(), CancellationToken.None);
            job.SetDone();

            int Count(SearchSettings search) => album ? job.GetAlbumFolders(search).Items.Count
                : job.GetSortedTrackCandidates(search, new ConcurrentDictionary<string, int>()).Items.Count;
            Assert.AreEqual(1, job.RawSnapshot().Count, "Required conditions must not discard interactive observations.");
            Assert.AreEqual(0, Count(config.Search));
            var relaxed = SettingsCloner.Clone(config.Search);
            relaxed.NecessaryCond.Formats = [];
            Assert.AreEqual(1, Count(relaxed));
            Assert.AreEqual(1, client.SearchCallCount);
            Assert.AreEqual(JobLifecycleState.Terminal, job.LifecycleState);
            Assert.AreEqual(JobActivityPhase.None, job.ActivityPhase);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
        }

        [TestMethod]
        public void SearchJob_TypedAlbumProjection_ReturnsAlbumFolders()
        {
            var job = new SearchJob(new AlbumQuery { Artist = "ELO", Album = "Time" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Yours Truly.flac", length: 200),
                TestHelpers.CreateSlFile(@"ELO\Time\Cover.jpg"),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var folders = job.GetAlbumFolders(search);

            Assert.AreEqual(1, folders.Items.Count);
            Assert.AreEqual("User1", folders.Items[0].Username);
            Assert.AreEqual(@"ELO\Time", folders.Items[0].FolderPath);
            Assert.AreEqual(2, folders.Items[0].SearchAudioFileCount);
        }

        [TestMethod]
        public void SearchJob_TypedGroupProjection_UpdatesIncrementally()
        {
            var job = new SearchJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\ELO - Blue Sky.mp3", length: 180),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinGroupShares = 1;
            var peerFailureCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetTrackGroups(search, peerFailureCounts);
            var second = job.GetTrackGroups(search, peerFailureCounts);

            job.Session.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\ELO - Blue Sky.flac", length: 180),
            ]));
            var updated = job.GetTrackGroups(search, peerFailureCounts);

            Assert.AreSame(first, second);
            Assert.AreEqual(1, first.Items.Count);
            Assert.AreEqual(1, first.Items[0].Candidates!.Count);
            Assert.AreEqual(1, updated.Items.Count);
            Assert.AreEqual(2, updated.Items[0].Candidates!.Count);
            Assert.AreNotSame(first, updated);
        }

        [TestMethod]
        [DataRow("album")]
        [DataRow("song-groups")]
        [DataRow("album-groups")]
        public void SearchJob_ReadOnlyProjectionDoesNotPollutePublicExecutionCache(string mode)
        {
            var query = new SongQuery { Artist = "Artist" };
            var albumQuery = new AlbumQuery { Artist = "Artist", Album = "Album" };
            var job = new SearchJob(query);
            var settings = TestHelpers.CreateDefaultSettings().Download.Search;
            settings.MinGroupShares = 1;
            var reputation = new ConcurrentDictionary<string, int>();
            List<FileCandidate> Read(bool includeLocked) => mode switch
            {
                "album" => job.GetAlbumFolders(new FolderSearchProjection(albumQuery), settings, includeLocked)
                    .Items.SelectMany(folder => folder.Files).Select(file => file.Candidate).ToList(),
                "song-groups" => job.GetTrackGroups(new TrackGroupProjection(query), settings, reputation, includeLocked)
                    .Items.SelectMany(song => song.Candidates!).ToList(),
                "album-groups" => job.GetAlbumGroups(new AlbumGroupProjection(albumQuery), settings, includeLocked)
                    .Items.SelectMany(album => album.Results).SelectMany(folder => folder.Files).Select(file => file.Candidate).ToList(),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            job.Session.AddResponse(new SearchResponse("Peer", 1, true, 100, 0,
                [TestHelpers.CreateSlFile(@"Artist\Album\01 - First.flac", length: 180)],
                [TestHelpers.CreateSlFile(@"Artist\Album\02 - Second.flac", length: 180)]));
            Assert.AreEqual(1, Read(false).Count);
            Assert.AreEqual(2, Read(true).Count);
            Assert.AreEqual(1, Read(false).Count);

            job.Session.AddResponse(new SearchResponse("Peer", 1, true, 100, 0, [],
                [TestHelpers.CreateSlFile(@"Artist\Album\03 - Third.flac", length: 180)]));
            job.Session.Complete();
            var observed = Read(true);
            Assert.AreEqual(3, observed.Count);
            Assert.AreEqual(2, observed.Count(file => file.Visibility == SearchResultVisibility.Locked));
            Assert.IsTrue(observed.All(file => file.ProjectionFacts != null));
            var executable = Read(false);
            Assert.AreEqual(1, executable.Count);
            Assert.AreEqual(SearchResultVisibility.Public, executable[0].Visibility);
            if (mode != "song-groups")
            {
                job.RecordRetrievedDirectory(new PeerDirectorySnapshot(
                    new PeerDirectoryIdentity("Peer", @"Artist\Album"),
                    [executable[0].Target, new PeerFileTarget(
                        new PeerFileIdentity("Peer", @"Artist\Album\04 - Retrieved.flac"), 100, "flac")],
                    isComplete: true));
                Assert.AreEqual(4, Read(true).Count);
                Assert.AreEqual(2, Read(false).Count);
                // A different projection cache must reuse the source-owned
                // retrieval, without depending on a mutated old folder object.
                settings = SettingsCloner.Clone(settings);
                Assert.AreEqual(2, Read(false).Count);
                Assert.AreEqual(4, Read(true).Count);
            }
        }

        [TestMethod]
        public void SearchJob_TypedAlbumGroupsProjection_UpdatesIncrementally()
        {
            var job = new SearchJob(new AlbumQuery { Artist = "ELO", Album = "Time" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"ELO\Time\01. Prologue.flac", length: 60),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Twilight.flac", length: 209),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinGroupShares = 1;
            var first = job.GetAlbumGroups(search);
            var second = job.GetAlbumGroups(search);

            job.Session.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Shared\ELO\Time\01. Prologue.flac", length: 60),
                TestHelpers.CreateSlFile(@"Shared\ELO\Time\02. Twilight.flac", length: 209),
            ]));
            var updated = job.GetAlbumGroups(search);

            Assert.AreSame(first, second);
            Assert.AreEqual(1, first.Items.Count);
            Assert.AreEqual(1, first.Items[0].Results.Count);
            Assert.AreEqual(1, updated.Items.Count);
            Assert.AreEqual(2, updated.Items[0].Results.Count);
            Assert.AreNotSame(first, updated);
        }

        [TestMethod]
        public async Task DownloadEngine_CanRunSearchJobAsRootJob()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180),
                ]),
            };

            var (engineSettings, downloadSettings) = TestHelpers.CreateDefaultSettings();
            engineSettings.Username = "test_user";
            engineSettings.Password = "test_pass";

            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient(index), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });

            engine.Enqueue(job, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            Assert.IsTrue(job.IsComplete);
            Assert.AreEqual(1, job.ResultCount);
        }

        [TestMethod]
        public async Task SearchJob_DisconnectDuringSearch_RetriesWithoutCompletingLiveSession()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180),
                ]),
            };

            var (engineSettings, downloadSettings) = TestHelpers.CreateDefaultSettings();
            engineSettings.Username = "test_user";
            engineSettings.Password = "test_pass";

            var client = new ClientTests.MockSoulseekClient(index);
            client.FailNextSearchWithDisconnect();
            var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var streamed = new List<SearchRawResult>();
            var readerTask = Task.Run(async () =>
            {
                await foreach (var result in job.ReadRawResultsAsync())
                    streamed.Add(result);
            });

            engine.Enqueue(job, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);
            await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            Assert.IsTrue(job.IsComplete);
            Assert.IsTrue(client.SearchCallCount >= 2, "Search should retry after reconnect.");
            Assert.AreEqual(1, streamed.Count, "The live raw-results stream should not complete before the retry succeeds.");
            Assert.AreEqual(@"Music\Artist\Track.mp3", streamed[0].Filename);
        }

        [TestMethod]
        public async Task SearchJob_TerminalSearchFailure_CompletesLiveSession()
        {
            var (engineSettings, downloadSettings) = TestHelpers.CreateDefaultSettings();
            engineSettings.Username = "test_user";
            engineSettings.Password = "test_pass";

            var client = new ClientTests.MockSoulseekClient([]);
            client.FailNextSearch();
            var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var streamed = new List<SearchRawResult>();
            var readerTask = Task.Run(async () =>
            {
                await foreach (var result in job.ReadRawResultsAsync())
                    streamed.Add(result);
            });

            engine.Enqueue(job, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);
            await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(job.IsUnsuccessfulTerminal);
            Assert.AreEqual(JobFailureReason.Other, job.FailureReason);
            Assert.IsTrue(job.IsComplete, "Terminal search failures should close live raw-result streams.");
            Assert.AreEqual(0, streamed.Count);
        }

        [TestMethod]
        public async Task Searcher_SearchJob_TerminalFailure_CompletesLiveSessionForDirectCallers()
        {
            var config = TestHelpers.CreateDefaultSettings().Download;
            var registry = TestHelpers.CreatePeerFailureTracker();
            var client = new ClientTests.MockSoulseekClient([]);
            client.FailNextSearch();
            var searcher = new Searcher(client, registry, new DownloadEvents(), 10, 10);
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var streamed = new List<SearchRawResult>();
            var readerTask = Task.Run(async () =>
            {
                await foreach (var result in job.ReadRawResultsAsync())
                    streamed.Add(result);
            });

            var threw = false;
            try
            {
                await searcher.Search(job, config.Search, new ResponseData(), CancellationToken.None);
            }
            catch (Exception)
            {
                threw = true;
            }

            Assert.IsTrue(threw, "Expected Searcher.Search to throw on terminal search failure.");
            await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(job.IsComplete, "Direct Searcher.Search callers should not leave raw-result streams open after terminal errors.");
            Assert.AreEqual(0, streamed.Count);
        }
    }
}
