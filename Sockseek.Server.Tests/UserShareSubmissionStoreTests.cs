using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public sealed class UserShareSubmissionStoreTests
{
    [TestMethod]
    public async Task SameKeyAndBodySingleFlightWhileDifferentBodyConflicts()
    {
        var store = new UserShareSubmissionStore();
        var gate = new TaskCompletionSource<StartUserShareDownloadsResponseDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = Guid.NewGuid();
        int submissions = 0;

        Task<StartUserShareDownloadsResponseDto> first = store.ExecuteAsync(
            requestId,
            "same",
            () =>
            {
                Interlocked.Increment(ref submissions);
                return gate.Task;
            });
        Task<StartUserShareDownloadsResponseDto> second = store.ExecuteAsync(
            requestId,
            "same",
            () => throw new AssertFailedException("A replay must join the first submission."));

        await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
            store.ExecuteAsync(requestId, "different", () => Task.FromResult(Response())));

        StartUserShareDownloadsResponseDto expected = Response();
        gate.SetResult(expected);
        Assert.AreSame(expected, await first);
        Assert.AreSame(expected, await second);
        Assert.AreEqual(1, submissions);
    }

    [TestMethod]
    public void FingerprintBindsRequestToBrowseGeneration()
    {
        Guid requestId = Guid.NewGuid();
        var request = new StartUserShareDownloadsRequestDto(
            requestId,
            [new UserShareDirectorySelectionDto(42)],
            null);
        Guid firstBrowse = Guid.NewGuid();
        Guid secondBrowse = Guid.NewGuid();

        string first = UserShareSubmissionStore.Fingerprint(firstBrowse, request);

        Assert.AreEqual(first, UserShareSubmissionStore.Fingerprint(firstBrowse, request));
        Assert.AreNotEqual(first, UserShareSubmissionStore.Fingerprint(secondBrowse, request));
        Assert.AreNotEqual(first, UserShareSubmissionStore.Fingerprint(firstBrowse,
            request with { ExcludedFileIds = [7] }));
        Assert.AreNotEqual(first, UserShareSubmissionStore.Fingerprint(firstBrowse,
            request with { ExcludedDirectoryIds = [8] }));
    }

    [TestMethod]
    public async Task CallerCancellationDetachesButFailedSubmissionCanBeRetried()
    {
        var store = new UserShareSubmissionStore();
        var gate = new TaskCompletionSource<StartUserShareDownloadsResponseDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();

        Task<StartUserShareDownloadsResponseDto> detached = store.ExecuteAsync(
            requestId, "body", () => gate.Task, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => detached);

        StartUserShareDownloadsResponseDto expected = Response();
        Task<StartUserShareDownloadsResponseDto> joined = store.ExecuteAsync(
            requestId,
            "body",
            () => throw new AssertFailedException("Cancellation must not forget active work."));
        gate.SetResult(expected);
        Assert.AreSame(expected, await joined);

        Guid failedId = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ExecuteAsync(
            failedId,
            "body",
            () => Task.FromException<StartUserShareDownloadsResponseDto>(new InvalidOperationException("failed"))));

        StartUserShareDownloadsResponseDto retried = await store.ExecuteAsync(
            failedId, "body", () => Task.FromResult(expected));
        Assert.AreSame(expected, retried);
    }

    [TestMethod]
    public async Task SynchronouslyFailedSubmissionCanBeRetried()
    {
        var store = new UserShareSubmissionStore();
        Guid requestId = Guid.NewGuid();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ExecuteAsync(
            requestId,
            "body",
            () => throw new InvalidOperationException("failed before returning a task")));

        StartUserShareDownloadsResponseDto expected = Response();
        StartUserShareDownloadsResponseDto retried = await store.ExecuteAsync(
            requestId,
            "body",
            () => Task.FromResult(expected));

        Assert.AreSame(expected, retried);
    }

    [TestMethod]
    public async Task RetentionAndCapacityEvictCompletedEntriesWithoutRejectingNewWork()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-12T00:00:00Z");
        var store = new UserShareSubmissionStore(
            () => now,
            maximumRetainedRequests: 2,
            retention: TimeSpan.FromMinutes(5));
        Guid oldest = Guid.NewGuid();
        int oldestSubmissions = 0;

        await store.ExecuteAsync(oldest, "body", SubmitOldest);
        now += TimeSpan.FromTicks(1);
        await store.ExecuteAsync(Guid.NewGuid(), "body", () => Task.FromResult(Response()));
        now += TimeSpan.FromTicks(1);
        await store.ExecuteAsync(Guid.NewGuid(), "body", () => Task.FromResult(Response()));

        // The oldest completed key fell outside the two-entry budget and is a
        // new submission when used again. Capacity is never an admission limit.
        await store.ExecuteAsync(oldest, "body", SubmitOldest);
        Assert.AreEqual(2, oldestSubmissions);

        now += TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1);
        await store.ExecuteAsync(oldest, "body", SubmitOldest);
        Assert.AreEqual(3, oldestSubmissions);

        Task<StartUserShareDownloadsResponseDto> SubmitOldest()
        {
            oldestSubmissions++;
            return Task.FromResult(Response());
        }
    }

    private static StartUserShareDownloadsResponseDto Response()
        => new(
            new JobSummaryDto(),
            new UserShareResolutionSummaryDto(1, 0, 1, 1, 0, 0, "downloads"));
}
