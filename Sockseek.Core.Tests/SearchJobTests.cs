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
        public async Task SearchSession_CompletionBarrier_RejectsConcurrentLateResults()
        {
            var session = new SearchSession(Guid.NewGuid());
            using var completionPublished = new ManualResetEventSlim();
            using var releaseCompletionObserver = new ManualResetEventSlim();
            session.SearchCompleted += _ =>
            {
                completionPublished.Set();
                releaseCompletionObserver.Wait();
            };

            var completeTask = Task.Run(session.Complete);
            Assert.IsTrue(completionPublished.Wait(TimeSpan.FromSeconds(2)), "Completion publication did not reach the deterministic barrier.");

            var file = TestHelpers.CreateSlFile(@"Music\Artist\Late.flac", length: 180);
            var response = new SearchResponse("LateUser", 1, true, 100, 3, [file]);
            var addTask = Task.Run(() => session.AddResponse(response));
            Assert.IsFalse(addTask.IsCompleted, "Late result admission should wait for the in-flight completion barrier.");

            releaseCompletionObserver.Set();
            await Task.WhenAll(completeTask, addTask);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual(0, session.ResultCount);
            Assert.AreEqual(1, session.Revision);
            Assert.AreEqual(0, session.LockedFileCount);
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

            var userSuccessCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetSortedTrackCandidates(config.Search, userSuccessCounts);
            var second = job.GetSortedTrackCandidates(config.Search, userSuccessCounts);

            job.Session.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\Artist\Track Alt.mp3", length: 181),
            ]));

            var third = job.GetSortedTrackCandidates(config.Search, userSuccessCounts);

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
            var userSuccessCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetSortedTrackCandidates(
                new FileSearchProjection(new SongQuery { Artist = "a\0b", Title = "c" }, true),
                search,
                userSuccessCounts);
            var second = job.GetSortedTrackCandidates(
                new FileSearchProjection(new SongQuery { Artist = "a", Title = "b\0c" }, true),
                search,
                userSuccessCounts);

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
            var registry = TestHelpers.CreateUserSuccessTracker();
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
            var userSuccessCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetSortedTrackCandidates(search, userSuccessCounts);
            var second = job.GetSortedTrackCandidates(search, userSuccessCounts);

            job.Session.Complete();
            var completed = job.GetSortedTrackCandidates(search, userSuccessCounts);

            Assert.AreSame(first, second, "Typed projections should reuse the same snapshot while revision and completion state are unchanged.");
            Assert.AreEqual(1, first.Revision);
            Assert.IsFalse(first.IsComplete);
            Assert.IsTrue(completed.IsComplete);
            Assert.AreEqual(first.Revision + 1, completed.Revision);
            Assert.AreNotSame(first, completed, "Completion changes should invalidate cached snapshots even when no new raw results arrived.");
        }

        [TestMethod]
        public async Task SearchJob_LazyProjectionAfterTerminal_DoesNotMutateActivity()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", bitrate: 320, length: 180),
                ]),
            };
            var config = TestHelpers.CreateDefaultSettings().Download;
            var registry = TestHelpers.CreateUserSuccessTracker();
            var searcher = new Searcher(new ClientTests.MockSoulseekClient(index), registry, new DownloadEvents(), 10, 10);
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });

            await searcher.Search(job, config.Search, new ResponseData(), CancellationToken.None);
            job.SetDone();

            var snapshot = job.GetSortedTrackCandidates(config.Search, new ConcurrentDictionary<string, int>());

            Assert.AreEqual(1, snapshot.Items.Count);
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
        public void SearchJob_TypedAggregateProjection_UpdatesIncrementally()
        {
            var job = new SearchJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\ELO - Blue Sky.mp3", length: 180),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            var userSuccessCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetAggregateTracks(search, userSuccessCounts);
            var second = job.GetAggregateTracks(search, userSuccessCounts);

            job.Session.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\ELO - Blue Sky.flac", length: 180),
            ]));
            var updated = job.GetAggregateTracks(search, userSuccessCounts);

            Assert.AreSame(first, second);
            Assert.AreEqual(1, first.Items.Count);
            Assert.AreEqual(1, first.Items[0].Candidates!.Count);
            Assert.AreEqual(1, updated.Items.Count);
            Assert.AreEqual(2, updated.Items[0].Candidates!.Count);
            Assert.AreNotSame(first, updated);
        }

        [TestMethod]
        public void SearchJob_TypedAlbumAggregateProjection_UpdatesIncrementally()
        {
            var job = new SearchJob(new AlbumQuery { Artist = "ELO", Album = "Time" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"ELO\Time\01. Prologue.flac", length: 60),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Twilight.flac", length: 209),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            search.MinSharesAggregate = 1;
            var first = job.GetAggregateAlbums(search);
            var second = job.GetAggregateAlbums(search);

            job.Session.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Shared\ELO\Time\01. Prologue.flac", length: 60),
                TestHelpers.CreateSlFile(@"Shared\ELO\Time\02. Twilight.flac", length: 209),
            ]));
            var updated = job.GetAggregateAlbums(search);

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
            var registry = TestHelpers.CreateUserSuccessTracker();
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
