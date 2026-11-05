using System.Runtime.ExceptionServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Tests.ClientTests;

namespace Tests.Unit;

[TestClass]
public class SearchCompletionTests
{
    [TestMethod]
    public async Task Search_MalformedEntriesAreIsolatedBeforeCandidateConversion()
    {
        var validFile = new Soulseek.File(1, @"Artist\Album\Zero.flac", 0, "flac");
        var validLocked = new Soulseek.File(1, @"Artist\Album\Locked.flac", 42, "flac");
        var healthy = Response(2);
        var responses = new[]
        {
            new SearchResponse("Bad\nPeer", 1, true, 1000, 0, [validFile]),
            new SearchResponse("Exact Peer", 1, true, 1000, 3,
                [new Soulseek.File(1, "", 1, "flac"), validFile, new Soulseek.File(1, "negative.flac", -1, "flac")],
                [new Soulseek.File(1, "invalid\ud800.flac", 1, "flac"), validLocked]),
            healthy,
        };
        var client = new MockSoulseekClient([])
        {
            SearchHandler = (query, callback, options, ct) =>
            {
                foreach (var response in responses)
                    callback(response);
                SignalState(query, options, SearchStates.Completed);
                return Task.FromResult(new Search(query, new SearchScope(SearchScopeType.Network), 1, SearchStates.Completed, 3, 7, 0));
            },
        };
        var accepted = new List<SearchResponse>();
        await SoulseekSearchRunner.RunAsync(client, new SearchQuery("Artist Album"), new SearchOptions(), response =>
        {
            // This is also the fast-search boundary: every admitted entry must
            // be usable immediately, before a Search View has been requested.
            foreach (var file in response.Files.Concat(response.LockedFiles))
                _ = new PeerFileTarget(new PeerFileIdentity(response.Username, file.Filename), file.Size, file.Extension);
            accepted.Add(response);
        }, CancellationToken.None);

        Assert.HasCount(2, accepted);
        Assert.AreEqual("Exact Peer", accepted[0].Username);
        Assert.AreEqual(3, accepted[0].QueueLength);
        CollectionAssert.AreEqual(new[] { validFile }, accepted[0].Files.ToArray());
        CollectionAssert.AreEqual(new[] { validLocked }, accepted[0].LockedFiles.ToArray());
        Assert.AreSame(healthy, accepted[1], "Healthy responses should not be copied.");
    }

    [TestMethod]
    public async Task Search_CompletedSearchKeepsConcurrentResponsesWhenLibraryCleanupFails()
    {
        var cleanupFailure = SearchTestFaults.CreateDisposalFailure();
        var client = new MockSoulseekClient([])
        {
            SearchHandler = async (query, callback, options, ct) =>
            {
                await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => callback(Response(i)), ct)));
                SignalState(query, options, SearchStates.Completed | SearchStates.ResponseLimitReached);
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                return null!;
            },
        };
        using var searcher = CreateSearcher(client);
        var job = new SearchJob("Artist Album");
        int completedCount = -1;
        job.Session.Completed += () => completedCount = job.Session.RawSnapshot().Count;

        await searcher.Search(job, new SearchSettings(), new ResponseData(), CancellationToken.None);

        Assert.IsTrue(job.IsComplete);
        Assert.AreEqual(16, job.ResultCount);
        Assert.AreEqual(16, completedCount, "All accepted responses must be available before completion is published.");
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 16).Select(i => $"User{i}").ToArray(),
            job.RawSnapshot().Select(r => r.Username).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Search_CancellationIsPreservedWhenLibraryCleanupFails(bool receiveResponseBeforeCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupFailure = SearchTestFaults.CreateDisposalFailure();
        var responseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MockSoulseekClient([])
        {
            SearchHandler = async (query, callback, options, ct) =>
            {
                if (receiveResponseBeforeCancellation)
                {
                    callback(Response(1));
                    await responseReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                cancellation.Cancel();
                SignalState(query, options, SearchStates.Completed | SearchStates.Cancelled);
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                return null!;
            },
        };
        using var searcher = CreateSearcher(client);
        var job = new SearchJob("Artist Album");
        job.Session.RawResultAdded += _ => responseReceived.TrySetResult();

        // Cancellation while waiting for channel data may surface as the derived
        // TaskCanceledException. Both paths must retain the caller's cancellation.
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            searcher.Search(job, new SearchSettings(), new ResponseData(), cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(job.IsComplete);
        Assert.AreEqual(receiveResponseBeforeCancellation ? 1 : 0, job.ResultCount);
    }

    [TestMethod]
    public async Task Search_DoesNotTreatFailedSearchAsSuccessfulWhenCleanupAlsoFails()
    {
        var cleanupFailure = SearchTestFaults.CreateDisposalFailure();
        var client = new MockSoulseekClient([])
        {
            SearchHandler = (query, callback, options, ct) =>
            {
                SignalState(query, options, SearchStates.Completed | SearchStates.Errored);
                return Task.FromException<Search>(cleanupFailure);
            },
        };
        using var searcher = CreateSearcher(client);

        var error = await Assert.ThrowsExactlyAsync<SynchronizationLockException>(() =>
            searcher.Search(new SearchJob("Artist Album"), new SearchSettings(), new ResponseData(), CancellationToken.None));

        Assert.AreSame(cleanupFailure, error);
    }

    [TestMethod]
    public async Task Search_DoesNotSuppressUnrelatedLockErrors()
    {
        var failure = new SynchronizationLockException("An unrelated lock failed.");
        var client = new MockSoulseekClient([])
        {
            SearchHandler = (query, callback, options, ct) =>
            {
                SignalState(query, options, SearchStates.Completed);
                return Task.FromException<Search>(failure);
            },
        };
        using var searcher = CreateSearcher(client);

        var error = await Assert.ThrowsExactlyAsync<SynchronizationLockException>(() =>
            searcher.Search(new SearchJob("Artist Album"), new SearchSettings(), new ResponseData(), CancellationToken.None));

        Assert.AreSame(failure, error);
    }

    [TestMethod]
    public async Task Search_ResponseObserverFailureDoesNotEscapeIntoNetworkCallback()
    {
        Exception? callbackFailure = null;
        var observerFailure = new InvalidOperationException("Observer failed.");
        var client = new MockSoulseekClient([])
        {
            SearchHandler = (query, callback, options, ct) =>
            {
                try { callback(Response(1)); }
                catch (Exception error) { callbackFailure = error; }
                SignalState(query, options, SearchStates.Completed);
                return Task.FromResult(new Search(query, new SearchScope(SearchScopeType.Network), 1, SearchStates.Completed, 1, 1, 0));
            },
        };
        using var searcher = CreateSearcher(client);
        var job = new SearchJob("Artist Album");
        Exception? reportedFailure = null;
        job.Session.ObserverFailed += (_, error) => reportedFailure = error;
        job.Session.RawResultAdded += _ => throw observerFailure;

        await searcher.Search(job, new SearchSettings(), new ResponseData(), CancellationToken.None);

        Assert.AreSame(observerFailure, reportedFailure);
        Assert.AreEqual(1, job.ResultCount);
        Assert.IsNull(callbackFailure, "Application observers must not run inside the library's network callback.");
    }

    [TestMethod]
    public async Task Search_ResponseProcessingFailureCancelsNetworkAndPreservesOriginalError()
    {
        var failure = new InvalidOperationException("Result processing failed.");
        bool networkCancelled = false;
        var client = new MockSoulseekClient([])
        {
            SearchHandler = async (_, callback, _, ct) =>
            {
                callback(Response(1));
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    networkCancelled = true;
                    throw;
                }
                return null!;
            },
        };

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SoulseekSearchRunner.RunAsync(client, new SearchQuery("Artist Album"), new SearchOptions(),
                _ => throw failure, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(failure, error);
        Assert.IsTrue(networkCancelled);
    }

    [TestMethod]
    public async Task Search_BlockedResultConsumer_DoesNotBlockNetworkCompletionOrLoseQueuedResponses()
    {
        var consumerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var networkCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MockSoulseekClient([])
        {
            SearchHandler = async (query, callback, options, ct) =>
            {
                callback(Response(0));
                await consumerEntered.Task.WaitAsync(ct);
                for (int i = 1; i < 8; i++)
                    callback(Response(i));
                SignalState(query, options, SearchStates.Completed);
                networkCompleted.TrySetResult();
                return new Search(query, new SearchScope(SearchScopeType.Network), 1, SearchStates.Completed, 8, 8, 0);
            },
        };
        using var searcher = CreateSearcher(client);
        var job = new SearchJob("Artist Album");
        job.Session.RawResultAdded += _ =>
        {
            consumerEntered.TrySetResult();
            // A synchronous application observer, intentionally held independently of
            // the network. The test releases it explicitly, never after a timed delay.
            releaseConsumer.Task.GetAwaiter().GetResult();
        };
        int completedCount = -1;
        job.Session.Completed += () => completedCount = job.Session.RawSnapshot().Count;
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var search = Task.Run(() => searcher.Search(job, new SearchSettings(), new ResponseData(), watchdog.Token));
        try
        {
            await networkCompleted.Task.WaitAsync(watchdog.Token);
            Assert.IsFalse(search.IsCompleted, "Search completion must wait for accepted results to drain.");
            Assert.IsFalse(job.IsComplete);
        }
        finally
        {
            releaseConsumer.TrySetResult();
            await search.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(job.IsComplete);
        Assert.AreEqual(8, completedCount);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 8).Select(i => $"User{i}").ToArray(),
            job.RawSnapshot().Select(result => result.Username).ToArray());
    }

    [TestMethod]
    public async Task Search_LargeResponseCoalescesDiscoveryWithoutLosingRawResults()
    {
        const int fileCount = 2_000;
        var events = new DownloadEvents();
        var counts = new List<int>();
        events.JobDiscoveryChanged += job => counts.Add(job.Discovery!.RawResultCount);
        var client = new MockSoulseekClient([new SearchResponse("User", 1, true, 1000, 0,
            Enumerable.Range(0, fileCount).Select(i => TestHelpers.CreateSlFile($@"Artist\Album\Track {i}.mp3", length: 180)).ToArray())]);
        using var searcher = CreateSearcher(client, events);
        var job = new SearchJob("Artist Album");
        int rawEvents = 0;
        job.Session.RawResultAdded += _ => rawEvents++;

        await searcher.Search(job, new SearchSettings(), new ResponseData(), CancellationToken.None);

        Assert.AreEqual(fileCount, job.ResultCount);
        Assert.AreEqual(fileCount, job.RawSnapshot().Count);
        Assert.AreEqual(fileCount, rawEvents, "Raw result consumers must still receive every file.");
        Assert.IsTrue(counts.Count is > 0 and <= 10, $"A single response generated {counts.Count} discovery updates.");
        Assert.AreEqual(fileCount, counts[^1], "The final discovery update must be exact.");
    }

    private static Searcher CreateSearcher(MockSoulseekClient client, DownloadEvents? events = null)
        => new(client, TestHelpers.CreateUserSuccessTracker(), events ?? new DownloadEvents(), 10, 10);

    private static SearchResponse Response(int i)
        => new($"User{i}", 1, true, 1000, 0, [TestHelpers.CreateSlFile($@"Artist\Album\Track {i}.mp3", length: 180)]);

    private static void SignalState(SearchQuery query, SearchOptions options, SearchStates state)
        => options.StateChanged?.Invoke((SearchStates.InProgress,
            new Search(query, new SearchScope(SearchScopeType.Network), 1, state, 0, 0, 0)));
}
