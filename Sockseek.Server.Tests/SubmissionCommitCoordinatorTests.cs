using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;
using Sockseek.Server.Planning;
using Sockseek.Server.PeerBrowsing;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SubmissionCommitCoordinatorTests
{
    [TestMethod]
    public void SearchViewFingerprint_UsesOptionsAndIgnoresProfileContextMapOrder()
    {
        Guid viewId = Guid.NewGuid();
        var selection = new RefSelectionExpressionDto(RefSelectionMode.Only, ["file-ref"]);
        var first = new SubmissionOptionsDto(
            ProfileContext: new Dictionary<string, bool>
            {
                ["music"] = true,
                ["lossless"] = false,
            },
            DownloadSettings: new DownloadSettingsPatchDto(
                Skip: new SkipSettingsPatchDto(SkipExisting: false)));
        var reordered = first with
        {
            ProfileContext = new Dictionary<string, bool>
            {
                ["lossless"] = false,
                ["music"] = true,
            },
        };
        var request = new CommitSearchViewSelectionRequestDto(4, selection, Guid.NewGuid(), Options: first);
        string fingerprint = SearchViewSelections.Fingerprint(viewId, request);
        Assert.AreEqual(fingerprint, SearchViewSelections.Fingerprint(viewId, request with { Options = reordered }));
        Assert.AreNotEqual(fingerprint, SearchViewSelections.Fingerprint(viewId, request with
        {
            Options = first with { DownloadSettings = new DownloadSettingsPatchDto(Skip: new SkipSettingsPatchDto(SkipExisting: true)) },
        }));
        Assert.AreNotEqual(fingerprint, SearchViewSelections.Fingerprint(viewId, request with { UseRepresentativeWhenUnchosen = true }));
    }

    [TestMethod]
    public void DirectoryRetrievalFingerprint_BindsExactIdentityAndIssuingRevision()
    {
        Guid viewId = Guid.NewGuid();
        var directory = new PeerDirectoryRefDto("opaque-ref", "Peer", @"Music\Album");
        string fingerprint = SearchViewCoordinator.FingerprintDirectoryRetrieval(
            viewId, 7, directory);
        Assert.AreEqual(fingerprint,
            SearchViewCoordinator.FingerprintDirectoryRetrieval(viewId, 7, directory));
        Assert.AreNotEqual(fingerprint,
            SearchViewCoordinator.FingerprintDirectoryRetrieval(viewId, 8, directory));
        Assert.AreNotEqual(fingerprint,
            SearchViewCoordinator.FingerprintDirectoryRetrieval(viewId, 7,
                directory with { Username = "peer" }));
        Assert.AreNotEqual(fingerprint,
            SearchViewCoordinator.FingerprintDirectoryRetrieval(viewId, 7,
                directory with { FolderPath = @"Music\Other" }));
    }

    [TestMethod]
    public async Task AcceptedSubmissionReceiptSurvivesCoordinatorAndPersistenceRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-commit-receipt-tests",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
        });
        Guid idempotencyKey = Guid.NewGuid();
        const string fingerprint = "request-fingerprint";
        var expected = new CommitJobPreviewResponseDto(
            Guid.NewGuid(),
            idempotencyKey,
            Guid.NewGuid(),
            1,
            1,
            1,
            0,
            0,
            []);
        try
        {
            var firstPersistence = new PersistenceCoordinator(options);
            await firstPersistence.StartAsync(CancellationToken.None);
            try
            {
                var first = new SubmissionCommitCoordinator(firstPersistence);
                PersistedSubmission? registered = null;
                SubmissionRegistration? acceptedRegistration = null;
                await Assert.ThrowsExactlyAsync<IOException>(() => first.ExecuteAsync<CommitJobPreviewResponseDto>(
                    idempotencyKey,
                    fingerprint,
                    async cancellationToken =>
                    {
                        acceptedRegistration = new SubmissionRegistration(
                            idempotencyKey,
                            DateTimeOffset.UtcNow,
                            1,
                            "{\"retained\":\"ExactPeer\\\\Music\\\\Track.flac\"}",
                            null,
                            expected.PreviewId,
                            "exact-artifact",
                            fingerprint,
                            System.Text.Json.JsonSerializer.Serialize(expected, SockseekApiJson.CreateSerializerOptions()));
                        await firstPersistence.Submissions!.CreateAsync(acceptedRegistration, cancellationToken);
                        registered = await firstPersistence.Submissions.GetSubmissionAsync(idempotencyKey);
                        Assert.IsNotNull(registered?.CommitReceiptJson,
                            "Acceptance must retain its exact receipt before any execution can be published.");
                        throw new IOException("The accepted response was lost.");
                    }));
                CommitJobPreviewResponseDto actual = await first.ExecuteAsync<CommitJobPreviewResponseDto>(
                    idempotencyKey,
                    fingerprint,
                    _ => throw new AssertFailedException("An accepted retry must not run the mutation again."));
                AssertReceipt(expected, actual);
                var store = firstPersistence.Submissions!;
                PersistedSubmission? retained = await store.GetSubmissionAsync(idempotencyKey);
                Assert.IsNotNull(registered);
                Assert.IsNotNull(retained);
                Assert.IsNotNull(retained.CommitReceiptJson);
                Assert.AreEqual(registered, retained, "Returning the receipt must not rewrite accepted data or advance its revision.");
                Assert.AreEqual(1L, retained.Revision);
                Assert.AreEqual(new PersistedSubmissionCommitReceipt(fingerprint, retained.CommitReceiptJson),
                    await store.GetCommitReceiptAsync(idempotencyKey));
                Assert.IsNull(await store.GetCommitReceiptAsync(Guid.NewGuid()));
                Assert.IsNotNull(acceptedRegistration);
                await store.CreateAsync(acceptedRegistration);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    store.CreateAsync(acceptedRegistration with { CommitFingerprint = "different-fingerprint" }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    store.CreateAsync(acceptedRegistration with { CommitReceiptJson = "{}" }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    store.CreateAsync(acceptedRegistration with { SpecificationJson = "{}" }));
                Assert.AreEqual(retained, await store.GetSubmissionAsync(idempotencyKey),
                    "Identical retries and conflicting receipts must not rewrite accepted data or advance its revision.");
            }
            finally
            {
                await firstPersistence.StopAsync(CancellationToken.None);
            }

            var secondPersistence = new PersistenceCoordinator(options);
            await secondPersistence.StartAsync(CancellationToken.None);
            try
            {
                var second = new SubmissionCommitCoordinator(secondPersistence);
                CommitJobPreviewResponseDto restored = await second.ExecuteAsync<CommitJobPreviewResponseDto>(
                    idempotencyKey,
                    fingerprint,
                    _ => throw new AssertFailedException(
                        "A retained receipt must not run the mutation again."));
                AssertReceipt(expected, restored);

                await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                    second.ExecuteAsync<CommitJobPreviewResponseDto>(
                        idempotencyKey,
                        "different-fingerprint",
                        _ => throw new AssertFailedException(
                            "A conflicting request must not run the mutation.")));
            }
            finally
            {
                await secondPersistence.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SameKeyAndBodySingleFlightWhileDifferentBodyConflicts()
    {
        var store = new SubmissionCommitCoordinator(new PersistenceCoordinator(Options.Create(new ServerOptions())));
        var gate = new TaskCompletionSource<StartUserShareDownloadsResponseDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = Guid.NewGuid();
        int submissions = 0;

        Task<StartUserShareDownloadsResponseDto> first = store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            requestId,
            "same",
            _ =>
            {
                Interlocked.Increment(ref submissions);
                return gate.Task;
            });
        Task<StartUserShareDownloadsResponseDto> second = store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            requestId,
            "same",
            _ => throw new AssertFailedException("A replay must join the first submission."));

        await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
            store.ExecuteAsync<StartUserShareDownloadsResponseDto>(requestId, "different", _ => Task.FromResult(Response())));

        StartUserShareDownloadsResponseDto expected = Response();
        gate.SetResult(expected);
        Assert.AreEqual(expected.Resolution, (await first).Resolution);
        Assert.AreEqual(expected.Resolution, (await second).Resolution);
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

        string first = UserBrowseEndpoints.Fingerprint(firstBrowse, request);

        Assert.AreEqual(first, UserBrowseEndpoints.Fingerprint(firstBrowse, request));
        Assert.AreNotEqual(first, UserBrowseEndpoints.Fingerprint(secondBrowse, request));
        Assert.AreNotEqual(first, UserBrowseEndpoints.Fingerprint(firstBrowse,
            request with { ExcludedFileIds = [7] }));
        Assert.AreNotEqual(first, UserBrowseEndpoints.Fingerprint(firstBrowse,
            request with { ExcludedDirectoryIds = [8] }));
    }

    [TestMethod]
    public async Task CallerCancellationDetachesButFailedSubmissionCanBeRetried()
    {
        var store = new SubmissionCommitCoordinator(new PersistenceCoordinator(Options.Create(new ServerOptions())));
        var gate = new TaskCompletionSource<StartUserShareDownloadsResponseDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();

        Task<StartUserShareDownloadsResponseDto> detached = store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            requestId, "body", _ => gate.Task, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => detached);

        StartUserShareDownloadsResponseDto expected = Response();
        Task<StartUserShareDownloadsResponseDto> joined = store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            requestId,
            "body",
            _ => throw new AssertFailedException("Cancellation must not forget active work."));
        gate.SetResult(expected);
        Assert.AreEqual(expected.Resolution, (await joined).Resolution);

        Guid failedId = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            failedId,
            "body",
            _ => Task.FromException<StartUserShareDownloadsResponseDto>(new InvalidOperationException("failed"))));

        StartUserShareDownloadsResponseDto retried = await store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            failedId, "body", _ => Task.FromResult(expected));
        Assert.AreEqual(expected.Resolution, retried.Resolution);
    }

    [TestMethod]
    public async Task SynchronouslyFailedSubmissionCanBeRetried()
    {
        var store = new SubmissionCommitCoordinator(new PersistenceCoordinator(Options.Create(new ServerOptions())));
        Guid requestId = Guid.NewGuid();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            requestId,
            "body",
            _ => throw new InvalidOperationException("failed before returning a task")));

        StartUserShareDownloadsResponseDto expected = Response();
        StartUserShareDownloadsResponseDto retried = await store.ExecuteAsync<StartUserShareDownloadsResponseDto>(
            requestId,
            "body",
            _ => Task.FromResult(expected));

        Assert.AreEqual(expected.Resolution, retried.Resolution);
    }

    private static StartUserShareDownloadsResponseDto Response()
        => new(
            new JobSummaryDto(),
            new UserShareResolutionSummaryDto(1, 0, 1, 1, 0, 0, "downloads"));

    private static void AssertReceipt(
        CommitJobPreviewResponseDto expected,
        CommitJobPreviewResponseDto actual)
    {
        Assert.AreEqual(expected.PreviewId, actual.PreviewId);
        Assert.AreEqual(expected.SubmissionId, actual.SubmissionId);
        Assert.AreEqual(expected.WorkflowId, actual.WorkflowId);
        Assert.AreEqual(expected.SubmittedCount, actual.SubmittedCount);
        Assert.AreEqual(0, actual.RejectionReasons.Count);
    }
}
