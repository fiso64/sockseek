using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

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
        string fingerprint = SubmissionCommitCoordinator.Fingerprint(
            "search-view", viewId, 4, selection, options: first);
        Assert.AreEqual(fingerprint, SubmissionCommitCoordinator.Fingerprint(
            "search-view", viewId, 4, selection, options: reordered));
        Assert.AreNotEqual(fingerprint, SubmissionCommitCoordinator.Fingerprint(
            "search-view", viewId, 4, selection,
            options: first with
            {
                DownloadSettings = new DownloadSettingsPatchDto(
                    Skip: new SkipSettingsPatchDto(SkipExisting: true)),
            }));
        Assert.AreNotEqual(fingerprint, SubmissionCommitCoordinator.Fingerprint(
            "search-view", viewId, 4, selection, options: first,
            useRepresentativeWhenUnchosen: true));
    }

    [TestMethod]
    public void DirectoryRetrievalFingerprint_BindsExactIdentityAndIssuingRevision()
    {
        Guid viewId = Guid.NewGuid();
        var directory = new PeerDirectoryRefDto("opaque-ref", "Peer", @"Music\Album");
        string fingerprint = SubmissionCommitCoordinator.FingerprintDirectoryRetrieval(
            viewId, 7, directory);
        Assert.AreEqual(fingerprint,
            SubmissionCommitCoordinator.FingerprintDirectoryRetrieval(viewId, 7, directory));
        Assert.AreNotEqual(fingerprint,
            SubmissionCommitCoordinator.FingerprintDirectoryRetrieval(viewId, 8, directory));
        Assert.AreNotEqual(fingerprint,
            SubmissionCommitCoordinator.FingerprintDirectoryRetrieval(viewId, 7,
                directory with { Username = "peer" }));
        Assert.AreNotEqual(fingerprint,
            SubmissionCommitCoordinator.FingerprintDirectoryRetrieval(viewId, 7,
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

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
