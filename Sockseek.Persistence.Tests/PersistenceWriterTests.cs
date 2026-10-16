using System.Threading.Channels;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;
using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class PersistenceWriterTests
{
    [TestMethod]
    [TestCategory("Load")]
    public async Task OneHundredThousandProgressCallbacks_ProduceOneLatestValueWrite()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("progress-load-test")).Runtime;
        var options = new PersistenceWriterOptions { TransferProgressFlushInterval = TimeSpan.FromSeconds(3) };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid transferId = Guid.NewGuid();

        for (int revision = 1; revision <= 100_000; revision++)
            Assert.IsTrue(inbox.TryEnqueue(Transfer(runtime.RuntimeId, transferId, revision)));

        Assert.AreEqual(1, inbox.ProgressCount);
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None);

        await using var verify = await database.Factory.CreateDbContextAsync();
        var transfer = await verify.Transfers.SingleAsync(item => item.Id == transferId);
        Assert.AreEqual(100_000L, transfer.Revision);
        Assert.AreEqual(100_000L, transfer.TransferredBytes);
        Assert.AreEqual(1L, health.Snapshot(inbox).SuccessfulCommitCount);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public void TransferTerminalAdmission_RemovesOlderBufferedAndDegradedProgress()
    {
        var options = new PersistenceWriterOptions
        {
            CriticalQueueCapacity = 1,
            DegradedProjectionCapacity = 2,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid runtimeId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Transfer(runtimeId, transferId, revision: 1) with
        {
            AccountingObservations =
            [
                new(attemptId, 1, DateTimeOffset.UtcNow, 50),
            ],
        }));

        var terminalTransfer = Transfer(runtimeId, transferId, revision: 2) with
        {
            Priority = PersistenceMutationPriority.Terminal,
            State = "Completed",
            TerminalOutcome = "Succeeded",
            TransferredBytes = 100,
        };
        Assert.IsTrue(inbox.TryEnqueue(new TransferTerminalPersistenceMutation(terminalTransfer, null, null)));

        Assert.AreEqual(0, inbox.ProgressCount);
        var batch = inbox.DrainBatch(includeProgress: false);
        Assert.AreEqual(1, batch.Count);
        Assert.IsInstanceOfType<TransferTerminalPersistenceMutation>(batch[0]);
        var admitted = (TransferTerminalPersistenceMutation)batch[0];
        Assert.AreEqual(1, admitted.Transfer.AccountingObservations?.Count);
        Assert.AreEqual(50L, admitted.Transfer.AccountingObservations?.Single().CumulativeBytes);
    }

    [TestMethod]
    public void RetryDrain_PreservesSearchResultBeforeCompletionSequenceAcrossBatchBoundaries()
    {
        var options = new PersistenceWriterOptions
        {
            MaximumBatchSize = 1,
            DegradedProjectionCapacity = 4,
        };
        var inbox = new PersistenceInbox(options, new PersistenceHealth());
        Guid runtimeId = Guid.NewGuid();
        Guid searchId = Guid.NewGuid();
        var results = new SearchResultsPersistenceMutation(
            runtimeId,
            Sequence: 2,
            DateTimeOffset.UtcNow,
            searchId,
            Revision: 2,
            [Result(1)]);
        var completion = new SearchCompletionPersistenceMutation(
            runtimeId,
            Sequence: 3,
            DateTimeOffset.UtcNow,
            searchId,
            Revision: 3,
            "query",
            ResultCount: 1,
            LockedFileCount: 0,
            "Complete");

        inbox.RequeueAfterFailure([completion, results]);

        Assert.IsInstanceOfType<SearchResultsPersistenceMutation>(inbox.DrainBatch().Single());
        Assert.IsInstanceOfType<SearchCompletionPersistenceMutation>(inbox.DrainBatch().Single());
    }

    [TestMethod]
    public void CriticalOverflow_ReturnsAcceptedWhenTheDegradedStoreRetainsIt()
    {
        var options = new PersistenceWriterOptions
        {
            CriticalQueueCapacity = 1,
            DegradedProjectionCapacity = 1,
        };
        var inbox = new PersistenceInbox(options, new PersistenceHealth());
        Guid runtimeId = Guid.NewGuid();

        Assert.IsTrue(inbox.TryEnqueue(Job(
            runtimeId,
            Guid.NewGuid(),
            revision: 1,
            PersistenceMutationPriority.Structural)));
        Assert.IsTrue(inbox.TryEnqueue(Job(
            runtimeId,
            Guid.NewGuid(),
            revision: 1,
            PersistenceMutationPriority.Terminal,
            terminal: true)));
        Assert.AreEqual(1, inbox.DegradedCount);

        inbox.Complete();
        Assert.IsFalse(inbox.TryEnqueue(Job(
            runtimeId,
            Guid.NewGuid(),
            revision: 1,
            PersistenceMutationPriority.Terminal,
            terminal: true)));
        Assert.AreEqual(0, inbox.ActiveAdmissionCount);
    }

    [TestMethod]
    public async Task TerminalMutations_PreserveActualJobAndAttemptStartTimesWhenRowsAreMissing()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("timestamp-test")).Runtime;
        Guid jobId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        DateTimeOffset registered = DateTimeOffset.UnixEpoch.AddHours(1);
        DateTimeOffset jobStarted = registered.AddSeconds(2);
        DateTimeOffset attemptStarted = jobStarted.AddSeconds(2);
        DateTimeOffset completed = attemptStarted.AddSeconds(10);
        JobPersistenceMutation job = Job(
            runtime.RuntimeId,
            jobId,
            revision: 2,
            PersistenceMutationPriority.Terminal,
            terminal: true) with
        {
            OccurredAtUtc = completed,
            RegisteredAtUtc = registered,
            StartedAtUtc = jobStarted,
        };
        TransferPersistenceMutation transfer = Transfer(runtime.RuntimeId, transferId, revision: 2) with
        {
            Priority = PersistenceMutationPriority.Terminal,
            OccurredAtUtc = completed,
            JobId = jobId,
            State = "Completed",
            TerminalOutcome = "Succeeded",
        };
        var attempt = new TransferAttemptPersistenceMutation(
            runtime.RuntimeId,
            Sequence: 3,
            completed,
            attemptId,
            Revision: 2,
            PersistenceMutationPriority.Terminal,
            transferId,
            AttemptNumber: 1,
            Source: "SoulseekPeer",
            State: "Completed",
            SourceUsername: "peer",
            SourcePath: "remote",
            OutputPath: "local",
            FailureReason: "None",
            FailureMessage: null,
            StartedAtUtc: attemptStarted);

        await RunCompletedWriterAsync(database,
        [
            job,
            new TransferTerminalPersistenceMutation(transfer, attempt, OwningJob: null),
        ]);

        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.AreEqual(
            jobStarted.ToUnixTimeMilliseconds(),
            (await verify.Jobs.SingleAsync(row => row.Id == jobId)).StartedAtUtc);
        Assert.AreEqual(
            attemptStarted.ToUnixTimeMilliseconds(),
            (await verify.TransferAttempts.SingleAsync(row => row.Id == attemptId)).StartedAtUtc);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public void AccountingProgress_UsesActiveTransferConcurrencyBeyondLegacyProjectionCapacity()
    {
        var options = new PersistenceWriterOptions { ProgressEntityCapacity = 1 };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid runtimeId = Guid.NewGuid();
        foreach (Guid transferId in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            Assert.IsTrue(inbox.TryEnqueue(Transfer(runtimeId, transferId, 1) with
            {
                AccountingObservations =
                [
                    new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, 1),
                ],
            }));
        }

        Assert.AreEqual(2, inbox.ProgressCount);
        Assert.AreEqual(0L, health.Snapshot(inbox).DroppedProgressCount);
    }

    [TestMethod]
    public async Task UnknownTransferTotal_RoundTripsAsExplicitNullHistoryValue()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("unknown-total-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid transferId = Guid.NewGuid();
        var mutation = Transfer(runtime.RuntimeId, transferId, revision: 1) with
        {
            TotalBytes = -1,
            TransferredBytes = 42,
        };
        Assert.IsTrue(inbox.TryEnqueue(mutation));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);

        var detail = await new TransferHistoryReader(database.Factory).GetTransferAsync(transferId);
        Assert.IsNotNull(detail);
        Assert.IsNull(detail.Transfer.TotalBytes);
        Assert.AreEqual(42L, detail.Transfer.TransferredBytes);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task TransferTimelineFields_RoundTripAuthoritativeTimesSpeedMetadataAndGroup()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("transfer-projection-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid transferId = Guid.NewGuid();
        DateTimeOffset requested = DateTimeOffset.UnixEpoch.AddHours(1);
        DateTimeOffset started = requested.AddSeconds(2);
        DateTimeOffset progressed = started.AddSeconds(3);
        var mutation = Transfer(runtime.RuntimeId, transferId, revision: 1) with
        {
            OccurredAtUtc = progressed.AddMinutes(1),
            RequestedAtUtc = requested,
            StartedAtUtc = started,
            LastProgressAtUtc = progressed,
            BytesPerSecond = 12_345,
            File = new TransferFileMetadataSnapshot(
                "Track.flac", 100, "flac", 900, 24, 96_000, 180,
                [new FileAttributeSnapshot("BitDepth", 24, 4)]),
            GroupRef = @"Music\Artist\Album",
            GroupDisplayPath = @"Public Music\Artist\Album",
        };
        Assert.IsTrue(inbox.TryEnqueue(mutation));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None);

        PersistedTransferDetail? detail = await new TransferHistoryReader(database.Factory)
            .GetTransferAsync(transferId);
        Assert.IsNotNull(detail);
        Assert.AreEqual(requested, detail.Transfer.CreatedAtUtc);
        Assert.AreEqual(started, detail.Transfer.StartedAtUtc);
        Assert.AreEqual(progressed, detail.Transfer.LastProgressAtUtc);
        Assert.AreEqual(12_345L, detail.Transfer.BytesPerSecond);
        Assert.AreEqual("Track.flac", detail.Transfer.File?.Name);
        Assert.AreEqual(24, detail.Transfer.File?.BitDepth);
        Assert.AreEqual("BitDepth", detail.Transfer.File?.Attributes?.Single().Type);
        Assert.AreEqual(@"Music\Artist\Album", detail.Transfer.GroupRef);
        Assert.AreEqual(@"Public Music\Artist\Album", detail.Transfer.GroupDisplayPath);
        Assert.IsNull(detail.Transfer.ArchivedAtUtc);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task TransferAccounting_ReplaysRetriesAndCounterResetsIdempotently_AndQueriesBoundedSemantics()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("transfer-accounting-test")).Runtime;
        DateTimeOffset start = new(
            DateTimeOffset.UtcNow.AddHours(-1).Ticks / TimeSpan.FromMinutes(5).Ticks
                * TimeSpan.FromMinutes(5).Ticks,
            TimeSpan.Zero);

        Guid downloadId = Guid.NewGuid();
        Guid downloadAttempt = Guid.NewGuid();
        var download = Transfer(runtime.RuntimeId, downloadId, revision: 4) with
        {
            OccurredAtUtc = start.AddMinutes(16),
            Priority = PersistenceMutationPriority.Terminal,
            State = "Completed",
            TerminalOutcome = "Succeeded",
            TransferredBytes = 25,
            Username = "ExactPeer",
            RequestedAtUtc = start,
            AccountingObservations =
            [
                new(downloadAttempt, 1, start.AddMinutes(1), 10),
                new(downloadAttempt, 2, start.AddMinutes(6), 25),
                // A lower cumulative value is a new counter epoch, not a
                // negative delta or a reason to double-count earlier bytes.
                new(downloadAttempt, 3, start.AddMinutes(11), 5),
            ],
        };

        Guid uploadId = Guid.NewGuid();
        Guid uploadAttempt = Guid.NewGuid();
        var upload = Transfer(runtime.RuntimeId, uploadId, revision: 2) with
        {
            OccurredAtUtc = start.AddMinutes(17),
            Priority = PersistenceMutationPriority.Terminal,
            Direction = "Upload",
            State = "Completed",
            TerminalOutcome = "Succeeded",
            TransferredBytes = 40,
            Username = "OtherPeer",
            GroupRef = @"Music\Artist\Album",
            GroupDisplayPath = @"Public Music\Artist\Album",
            RequestedAtUtc = start,
            AccountingObservations =
            [
                new(uploadAttempt, 2, start.AddMinutes(7), 40),
            ],
        };

        Guid failedId = Guid.NewGuid();
        Guid failedAttemptId = Guid.NewGuid();
        var failed = Transfer(runtime.RuntimeId, failedId, revision: 2) with
        {
            OccurredAtUtc = start.AddMinutes(12),
            Priority = PersistenceMutationPriority.Terminal,
            Direction = "Upload",
            State = "Failed",
            TerminalOutcome = "Failed",
            Username = "OtherPeer",
            RequestedAtUtc = start,
        };
        var failedAttempt = new TransferAttemptPersistenceMutation(
            runtime.RuntimeId,
            Sequence: 3,
            start.AddMinutes(12),
            failedAttemptId,
            Revision: 2,
            PersistenceMutationPriority.Terminal,
            failedId,
            AttemptNumber: 1,
            Source: "SoulseekPeer",
            State: "Failed",
            SourceUsername: "OtherPeer",
            SourcePath: "remote",
            OutputPath: null,
            FailureReason: "NotShared",
            FailureMessage: null,
            Direction: "Upload");
        PersistenceMutation[] mutations =
        [
            new TransferTerminalPersistenceMutation(download, null, null),
            new TransferTerminalPersistenceMutation(upload, null, null),
            new TransferTerminalPersistenceMutation(failed, failedAttempt, null),
        ];

        await RunCompletedWriterAsync(database, mutations);
        await RunCompletedWriterAsync(database, mutations);

        var analytics = await new TransferAnalyticsReader(database.Factory).GetAsync(
            new TransferAnalyticsQuery(start, start.AddMinutes(30), TimeSpan.FromMinutes(5)));
        Assert.AreEqual(30L, analytics.DownloadBytes);
        Assert.AreEqual(40L, analytics.UploadBytes);
        Assert.AreEqual(1, analytics.DownloadedFiles);
        Assert.AreEqual(1, analytics.UploadedFiles);
        Assert.AreEqual(2, analytics.DistinctPeers);
        Assert.AreEqual(6, analytics.Buckets.Count);
        Assert.AreEqual(30L, analytics.Peers.Single(peer => peer.Username == "ExactPeer").Bytes);
        Assert.AreEqual(1, analytics.Content.Single().DownloadCount);
        Assert.AreEqual(@"Music\Artist\Album", analytics.Content.Single().Identity);
        Assert.AreEqual(@"Public Music\Artist\Album", analytics.Content.Single().DisplayPath);
        Assert.AreEqual("NotShared", analytics.Errors.Single().Reason);
        Assert.AreEqual(1, analytics.Errors.Single().Count);

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            for (int index = 0; index < 15; index++)
            {
                context.TransferByteBuckets.Add(new TransferByteBucketEntity
                {
                    BucketStartUtc = start.AddMinutes(20).ToUnixTimeMilliseconds(),
                    Direction = "Download",
                    Username = $"peer-{index:D2}",
                    Bytes = index + 1,
                });
            }
            await context.SaveChangesAsync();
        }
        var bounded = await new TransferAnalyticsReader(database.Factory).GetAsync(
            new TransferAnalyticsQuery(
                start,
                start.AddMinutes(30),
                TimeSpan.FromMinutes(5),
                TopCount: 3));
        Assert.AreEqual(3, bounded.Peers.Count(peer => peer.Direction == "Download"));
        CollectionAssert.AreEqual(
            new[] { "ExactPeer", "peer-14", "peer-13" },
            bounded.Peers.Where(peer => peer.Direction == "Download")
                .Select(peer => peer.Username)
                .ToArray());
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task CompositeTransferTerminal_RollsBackCompletely_ThenRetriesAndReplaysIdempotently()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("composite-terminal-test")).Runtime;
        Guid existingJobId = Guid.NewGuid();
        Guid owningJobId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        await RunCompletedWriterAsync(database, [Job(runtime.RuntimeId, existingJobId, 1,
            PersistenceMutationPriority.Structural) with { DisplayId = 700 }]);

        var terminalTransfer = Transfer(runtime.RuntimeId, transferId, 2) with
        {
            Priority = PersistenceMutationPriority.Terminal,
            JobId = owningJobId,
            State = "Completed",
            TerminalOutcome = "Succeeded",
            TotalBytes = 100,
            TransferredBytes = 100,
        };
        var finalAttempt = new TransferAttemptPersistenceMutation(
            runtime.RuntimeId, 3, DateTimeOffset.UtcNow, attemptId, 2,
            PersistenceMutationPriority.Terminal, transferId, 1, "SoulseekPeer", "Completed",
            "user", "remote", "local", "None", null);
        var conflictingJob = Job(runtime.RuntimeId, owningJobId, 2,
            PersistenceMutationPriority.Terminal, terminal: true) with { DisplayId = 700 };
        var badComposite = new TransferTerminalPersistenceMutation(terminalTransfer, finalAttempt, conflictingJob);

        var failedHealth = await RunCompletedWriterAsync(database, [badComposite]);
        Assert.AreEqual(1L, failedHealth.PermanentlyFailedMutationCount);
        await using (var verifyRollback = await database.Factory.CreateDbContextAsync())
        {
            Assert.IsFalse(await verifyRollback.Jobs.AnyAsync(job => job.Id == owningJobId));
            Assert.IsFalse(await verifyRollback.Transfers.AnyAsync(transfer => transfer.Id == transferId));
            Assert.IsFalse(await verifyRollback.TransferAttempts.AnyAsync(attempt => attempt.Id == attemptId));
        }

        var validComposite = badComposite with
        {
            OwningJob = conflictingJob with { DisplayId = 701 },
        };
        await RunCompletedWriterAsync(database, [validComposite]);
        await RunCompletedWriterAsync(database, [validComposite]);

        await using (var verifyReplay = await database.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(1, await verifyReplay.Jobs.CountAsync(job => job.Id == owningJobId));
            Assert.AreEqual(1, await verifyReplay.Transfers.CountAsync(transfer => transfer.Id == transferId));
            Assert.AreEqual(1, await verifyReplay.TransferAttempts.CountAsync(attempt => attempt.Id == attemptId));
        }
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task SearchCompletion_RepresentsGenuineZeroResults_AndCannotPromoteIncompleteHistory()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("search-state-test")).Runtime;
        Guid zeroSearchId = Guid.NewGuid();
        Guid incompleteSearchId = Guid.NewGuid();

        await RunCompletedWriterAsync(database,
        [
            Job(runtime.RuntimeId, zeroSearchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, 2, DateTimeOffset.UtcNow, zeroSearchId, 2,
                "zero", 0, 0, "Complete"),
            Job(runtime.RuntimeId, incompleteSearchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, 3, DateTimeOffset.UtcNow, incompleteSearchId, 2,
                "incomplete", 7, 3, "Incomplete", ObservedPeerCount: 2),
        ]);
        await RunCompletedWriterAsync(database,
        [
            new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, 4, DateTimeOffset.UtcNow, incompleteSearchId, 3,
                "incomplete", 7, 3, "Complete", ObservedPeerCount: 2),
        ]);

        var reader = new SearchHistoryReader(database.Factory);
        var zero = await reader.GetMetadataAsync(zeroSearchId);
        Assert.IsNotNull(zero);
        Assert.AreEqual("Complete", zero.ResultPersistenceState);
        Assert.AreEqual(0L, zero.ResultCount);
        Assert.AreEqual(0, (await reader.GetRawResultsAsync(zeroSearchId, 0, 10))!.Items.Count);

        var incomplete = await reader.GetMetadataAsync(incompleteSearchId);
        Assert.IsNotNull(incomplete);
        Assert.AreEqual("Incomplete", incomplete.ResultPersistenceState);
        Assert.AreEqual(7L, incomplete.ResultCount);
        Assert.AreEqual(3L, incomplete.LockedFileCount);
        Assert.AreEqual(2L, incomplete.ObservedPeerCount);

        var jobs = new JobHistoryReader(database.Factory);
        var listed = (await jobs.GetJobsAsync(new JobHistoryQuery(
            Limit: 10,
            IncludeAll: true))).Items.Single(job => job.Id == incompleteSearchId);
        Assert.AreEqual(7L, listed.DiscoveryPublicFileCount);
        Assert.AreEqual(3L, listed.DiscoveryLockedFileCount);
        Assert.AreEqual(2L, listed.DiscoveryObservedPeerCount);

        var byId = await jobs.GetJobAsync(incompleteSearchId);
        Assert.AreEqual(7L, byId?.DiscoveryPublicFileCount);
        Assert.AreEqual(3L, byId?.DiscoveryLockedFileCount);
        Assert.AreEqual(2L, byId?.DiscoveryObservedPeerCount);

        long displayId = byId!.DisplayId;
        var byDisplayId = await jobs.GetJobByDisplayIdAsync(byId.WorkflowId, displayId);
        Assert.AreEqual(7L, byDisplayId?.DiscoveryPublicFileCount);
        Assert.AreEqual(3L, byDisplayId?.DiscoveryLockedFileCount);
        Assert.AreEqual(2L, byDisplayId?.DiscoveryObservedPeerCount);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task LargeSearchCompletion_BatchesTenThousandRowsIntoOneTerminalTransaction()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("large-search-batch-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        var options = new PersistenceWriterOptions
        {
            SearchResultCapacityPerSearch = 10_000,
            SearchResultGlobalCapacity = 10_000,
            SearchResultFlushCount = 200,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, searchId, 1,
            PersistenceMutationPriority.Structural, kind: "Search")));
        for (int offset = 0; offset < 10_000; offset += 200)
        {
            var results = Enumerable.Range(offset + 1, 200).Select(value => Result(value)).ToArray();
            Assert.IsTrue(inbox.TryEnqueue(new SearchResultsPersistenceMutation(
                runtime.RuntimeId, offset + 2L, DateTimeOffset.UtcNow, searchId, offset + 201L, results)));
        }
        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtime.RuntimeId, 20_000, DateTimeOffset.UtcNow, searchId, 10_002,
            "large", 10_000, 0, "Complete")));
        inbox.Complete();

        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None);

        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.AreEqual(10_000, await verify.SearchResults.CountAsync(row => row.SearchJobId == searchId));
        Assert.AreEqual("Complete", (await verify.SearchJobs.SingleAsync(row => row.JobId == searchId)).ResultPersistenceState);
        Assert.IsTrue(health.Snapshot(inbox).SuccessfulCommitCount <= 2,
            "Search persistence must commit batches/barriers, not one transaction per result.");
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task SearchDuplicatePolicy_FirstSequenceAndFirstPeerPathWinConsistently()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("duplicate-search-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        var first = Result(1);
        var duplicatePeerPath = first with { Id = Guid.NewGuid(), Sequence = 2, Revision = 2, SizeBytes = 999 };
        var duplicateSequence = Result(1) with
        {
            Id = Guid.NewGuid(),
            Username = "another-peer",
            RemoteFilename = "another.mp3",
            SizeBytes = 888,
        };
        await RunCompletedWriterAsync(database,
        [
            Job(runtime.RuntimeId, searchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchResultsPersistenceMutation(
                runtime.RuntimeId, 2, DateTimeOffset.UtcNow, searchId, 2,
                [first, duplicatePeerPath, duplicateSequence]),
            new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, 4, DateTimeOffset.UtcNow, searchId, 3, "duplicates", 1, 0, "Complete"),
        ]);

        await using var verify = await database.Factory.CreateDbContextAsync();
        var stored = await verify.SearchResults.SingleAsync(row => row.SearchJobId == searchId);
        Assert.AreEqual(first.Id, stored.Id);
        Assert.AreEqual(first.SizeBytes, stored.SizeBytes);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public void SearchRowsAreNotRejectedByLegacyCountSettings()
    {
        var options = new PersistenceWriterOptions
        {
            SearchResultCapacityPerSearch = 1,
            SearchResultGlobalCapacity = 1,
            SearchMutationQueueCapacity = 4,
            MaximumBatchSize = 4,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid runtimeId = Guid.NewGuid();
        Guid searchJobId = Guid.NewGuid();
        var rows = new SearchResultsPersistenceMutation(
            runtimeId,
            10,
            DateTimeOffset.UtcNow,
            searchJobId,
            2,
            [Result(1), Result(2)]);
        Assert.IsTrue(inbox.TryEnqueue(rows));
        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtimeId,
            11,
            DateTimeOffset.UtcNow,
            searchJobId,
            3,
            "query",
            2,
            0,
            "Complete")));

        IReadOnlyList<PersistenceMutation> drained = inbox.DrainBatch();
        Assert.AreEqual(2, drained.Count);
        Assert.IsInstanceOfType<SearchResultsPersistenceMutation>(drained[0]);
        Assert.IsInstanceOfType<SearchCompletionPersistenceMutation>(drained[1]);
        Assert.AreEqual(0L, health.Snapshot(inbox).DroppedSearchResultCount);
    }

    [TestMethod]
    public async Task Writer_CoalescesByRevision_AndCannotRegressTerminalJob()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("writer-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var commits = Channel.CreateUnbounded<bool>();
        health.CommitCompleted += () => commits.Writer.TryWrite(true);
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        using var stop = new CancellationTokenSource();
        var runTask = writer.RunAsync(stop.Token);
        Guid jobId = Guid.NewGuid();

        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, revision: 1, PersistenceMutationPriority.Structural)));
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, revision: 2, PersistenceMutationPriority.Terminal, terminal: true)));
        await commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var persisted = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
            Assert.AreEqual(2L, persisted.Revision);
            Assert.AreEqual("Terminal", persisted.LifecycleState);
        }

        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, revision: 1, PersistenceMutationPriority.Structural)));
        await commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var persisted = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
            Assert.AreEqual(2L, persisted.Revision);
            Assert.AreEqual("Terminal", persisted.LifecycleState);
        }

        stop.Cancel();
        await AssertCanceledAsync(runTask);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task WalReadersRemainAvailableWhileSingleWriterCommits()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("concurrent-reader-test")).Runtime;
        var options = new PersistenceWriterOptions { CriticalQueueCapacity = 2_000 };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var writerTask = new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);
        var reader = new JobHistoryReader(database.Factory);
        var readerErrors = new List<Exception>();

        var reading = Task.Run(async () =>
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                try
                {
                    var page = await reader.GetJobsAsync(new JobHistoryQuery(Limit: 50, IncludeAll: true));
                    Assert.IsTrue(page.Items.Count <= 50);
                }
                catch (Exception ex)
                {
                    lock (readerErrors) readerErrors.Add(ex);
                }
            }
        });

        for (int index = 1; index <= 1_000; index++)
        {
            var mutation = Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural)
                with { DisplayId = index };
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        }
        inbox.Complete();
        await Task.WhenAll(reading, writerTask);

        Assert.AreEqual(0, readerErrors.Count,
            readerErrors.Count > 0 ? readerErrors[0].ToString() : null);
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.AreEqual(1_000, await verify.Jobs.CountAsync());
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task UnavailableWriterEntersUnhealthyWithoutBlockingMutationProducers()
    {
        var options = new PersistenceWriterOptions
        {
            MaximumRecoveryAttempts = 2,
            FailureRetryDelay = TimeSpan.FromMilliseconds(1),
            ProgressEntityCapacity = 8,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var runTask = new PersistenceWriter(new UnavailableContextFactory(), inbox, health, options)
            .RunAsync(CancellationToken.None);
        Guid runtimeId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Job(runtimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural)));

        var stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < 10_000; index++)
            inbox.TryEnqueue(Transfer(runtimeId, Guid.NewGuid(), index + 1L));
        stopwatch.Stop();
        inbox.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.AreEqual(PersistenceHealthState.Unhealthy, health.Snapshot(inbox).State);
        Assert.IsTrue(health.Snapshot(inbox).PermanentlyFailedMutationCount > 0);
    }

    [TestMethod]
    public async Task SearchCompletion_CommitsPendingResultsAndMetadataInOneBarrier()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("search-writer-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.CommitCompleted += () => committed.TrySetResult();
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        using var stop = new CancellationTokenSource();
        var runTask = writer.RunAsync(stop.Token);
        Guid jobId = Guid.NewGuid();

        inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, kind: "Search"));
        inbox.TryEnqueue(new SearchResultsPersistenceMutation(
            runtime.RuntimeId,
            2,
            DateTimeOffset.UtcNow,
            jobId,
            2,
            [Result(1), Result(2)]));
        inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtime.RuntimeId,
            3,
            DateTimeOffset.UtcNow,
            jobId,
            3,
            "query",
            2,
            4,
            "Complete"));

        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var search = await context.SearchJobs.AsNoTracking().SingleAsync(row => row.JobId == jobId);
            var results = await context.SearchResults.AsNoTracking().OrderBy(row => row.Sequence).ToListAsync();
            Assert.IsTrue(search.IsComplete);
            Assert.AreEqual("Complete", search.ResultPersistenceState);
            Assert.AreEqual(2L, search.ResultCount);
            Assert.AreEqual(4L, search.LockedFileCount);
            Assert.AreEqual(2, results.Count);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, results.Select(result => result.Sequence).ToArray());
        }

        var historical = await new SearchHistoryReader(database.Factory)
            .GetResultAsync(jobId, "user", "file-1.mp3");
        Assert.IsNotNull(historical?.Result);
        var neutral = historical.Result.ToProjectionInput();
        Assert.AreEqual("user", neutral.Username);
        Assert.AreEqual("file-1.mp3", neutral.Filename);
        Assert.AreEqual(1, neutral.ResponseFileCount);
        Assert.AreEqual(320, neutral.BitRate);

        stop.Cancel();
        await AssertCanceledAsync(runTask);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task JobHistory_UsesStableCursorAndEnforcesLimits()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("reader-test")).Runtime;
        Guid[] ids = [Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000003")];
        Guid[] workflowIds = [Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("10000000-0000-0000-0000-000000000002"), Guid.Parse("10000000-0000-0000-0000-000000000003")];
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            for (int i = 0; i < ids.Length; i++)
            {
                context.Jobs.Add(new JobEntity
                {
                    Id = ids[i],
                    WorkflowId = workflowIds[i],
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = i + 1,
                    DisplayId = i + 1,
                    Kind = "Song",
                    LifecycleState = "Terminal",
                    ActivityPhase = "None",
                    TerminalOutcome = "Succeeded",
                    SkipReason = "None",
                    CancellationSource = "None",
                    FailureReason = "None",
                    CreatedAtUtc = 100,
                    UpdatedAtUtc = 200,
                    CompletedAtUtc = 200,
                    Revision = 2,
                    PayloadSchemaVersion = 1,
                });
            }
            await context.SaveChangesAsync();
        }

        var reader = new JobHistoryReader(database.Factory);
        var first = await reader.GetJobsAsync(new JobHistoryQuery(Limit: 2));
        Assert.AreEqual(2, first.Items.Count);
        Assert.IsNotNull(first.NextCursor);
        var second = await reader.GetJobsAsync(new JobHistoryQuery(Cursor: first.NextCursor, Limit: 2));
        CollectionAssert.AreEqual(ids.Take(2).ToArray(), first.Items.Select(job => job.Id).ToArray());
        CollectionAssert.AreEqual(ids.Skip(2).ToArray(), second.Items.Select(job => job.Id).ToArray());
        Assert.IsNull(second.NextCursor);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetJobsAsync(new JobHistoryQuery(Cursor: "bad", Limit: 2)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetJobsAsync(new JobHistoryQuery(
            Cursor: first.NextCursor + new string(' ', 128), Limit: 2)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => reader.GetJobsAsync(new JobHistoryQuery(Limit: JobHistoryReader.MaximumPageSize + 1)));

        // Give the last workflow a second job. Deleting its former first job
        // between pages must not move the workflow behind an already-issued
        // cursor.
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Jobs.Add(new JobEntity
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000004"),
                WorkflowId = workflowIds[2],
                LastRuntimeId = runtime.RuntimeId,
                LastSequence = 4,
                DisplayId = 4,
                Kind = "Song",
                LifecycleState = "Terminal",
                ActivityPhase = "None",
                TerminalOutcome = "Succeeded",
                SkipReason = "None",
                CancellationSource = "None",
                FailureReason = "None",
                CreatedAtUtc = 100,
                UpdatedAtUtc = 200,
                CompletedAtUtc = 200,
                Revision = 2,
                PayloadSchemaVersion = 1,
            });
            await context.SaveChangesAsync();
        }

        var workflowPage1 = await reader.GetWorkflowsAsync(limit: 2);
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            await context.Jobs
                .Where(job => job.Id == ids[2])
                .ExecuteDeleteAsync();
        }
        var workflowPage2 = await reader.GetWorkflowsAsync(workflowPage1.NextCursor, limit: 2);
        CollectionAssert.AreEqual(workflowIds.Take(2).ToArray(), workflowPage1.Items.Select(item => item.WorkflowId).ToArray());
        CollectionAssert.AreEqual(workflowIds.Skip(2).ToArray(), workflowPage2.Items.Select(item => item.WorkflowId).ToArray());
        Assert.IsNull(workflowPage2.NextCursor);
        Assert.AreEqual(ids[1], (await reader.GetJobByDisplayIdAsync(workflowIds[1], 2))?.Id);
        Assert.AreEqual(1, (await reader.GetWorkflowAsync(workflowIds[0]))?.RootJobCount);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetWorkflowsAsync("bad", 2));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetWorkflowsAsync(
            workflowPage1.NextCursor + new string(' ', 128), 2));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task LargeWorkflow_PagesRootsDirectChildrenAndAllJobsInDisplayOrder()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("large-workflow-reader-test")).Runtime;
        Guid workflowId = Guid.NewGuid();
        Guid rootId = Guid.NewGuid();
        Guid firstChildId = Guid.NewGuid();
        var jobs = new List<JobEntity>(230);

        jobs.Add(CreateJob(rootId, displayId: 1, parentJobId: null, itemName: null));
        for (int displayId = 2; displayId <= 151; displayId++)
        {
            Guid id = displayId == 2 ? firstChildId : Guid.NewGuid();
            jobs.Add(CreateJob(
                id,
                displayId,
                rootId,
                itemName: displayId == 11 ? "Large workflow" : null));
        }
        for (int displayId = 152; displayId <= 230; displayId++)
            jobs.Add(CreateJob(Guid.NewGuid(), displayId, firstChildId, itemName: null));

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Jobs.AddRange(jobs);
            await context.SaveChangesAsync();
        }

        var reader = new JobHistoryReader(database.Factory);
        var summary = await reader.GetWorkflowAsync(workflowId);
        Assert.IsNotNull(summary);
        Assert.AreEqual("Large workflow", summary.Title);
        Assert.AreEqual(1, summary.RootJobCount);
        Assert.AreEqual(230, summary.CompletedJobCount);
        Assert.AreEqual(1, summary.FailedJobCount);

        var roots = await reader.GetJobsAsync(new JobHistoryQuery(
            Limit: 100,
            WorkflowId: workflowId,
            IncludeAll: false));
        CollectionAssert.AreEqual(new[] { rootId }, roots.Items.Select(job => job.Id).ToArray());

        var directPage1 = await reader.GetJobsAsync(new JobHistoryQuery(
            Limit: 100,
            WorkflowId: workflowId,
            IncludeAll: true,
            ParentJobId: rootId));
        var directPage2 = await reader.GetJobsAsync(new JobHistoryQuery(
            Cursor: directPage1.NextCursor,
            Limit: 100,
            WorkflowId: workflowId,
            IncludeAll: true,
            ParentJobId: rootId));
        Assert.AreEqual(150, directPage1.Items.Count + directPage2.Items.Count);
        Assert.IsNull(directPage2.NextCursor);
        CollectionAssert.AreEqual(
            Enumerable.Range(2, 150).Select(value => (long)value).ToArray(),
            directPage1.Items.Concat(directPage2.Items).Select(job => job.DisplayId).ToArray());
        Assert.AreEqual(150, await reader.GetChildCountAsync(rootId));

        var all = new List<PersistedJob>();
        string? cursor = null;
        do
        {
            var page = await reader.GetJobsAsync(new JobHistoryQuery(
                Cursor: cursor,
                Limit: 100,
                WorkflowId: workflowId,
                IncludeAll: true));
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor != null);
        Assert.AreEqual(230, all.Count);
        Assert.AreEqual(230, all.Select(job => job.Id).Distinct().Count());
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 230).Select(value => (long)value).ToArray(),
            all.Select(job => job.DisplayId).ToArray());

        await runtimeSession.StopAsync();

        JobEntity CreateJob(Guid id, int displayId, Guid? parentJobId, string? itemName)
            => new()
            {
                Id = id,
                WorkflowId = workflowId,
                ParentJobId = parentJobId,
                LastRuntimeId = runtime.RuntimeId,
                LastSequence = displayId,
                DisplayId = displayId,
                Kind = displayId == 1 ? "JobList" : "Song",
                LifecycleState = "Terminal",
                ActivityPhase = "None",
                TerminalOutcome = displayId == 225 ? "Failed" : "Succeeded",
                SkipReason = "None",
                CancellationSource = "None",
                FailureReason = displayId == 225 ? "Other" : "None",
                ItemName = itemName,
                QueryText = displayId == 1 ? "fallback title" : null,
                CreatedAtUtc = displayId,
                UpdatedAtUtc = displayId,
                CompletedAtUtc = displayId,
                Revision = 1,
                PayloadSchemaVersion = 1,
            };
    }

    [TestMethod]
    public async Task TransferHistory_PagesTransfersAndAttemptsWithoutLoadingAllRows()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("transfer-reader-test")).Runtime;
        Guid workflowId = Guid.NewGuid();
        Guid[] ids =
        [
            Guid.Parse("00000000-0000-0000-0000-000000000011"),
            Guid.Parse("00000000-0000-0000-0000-000000000012"),
            Guid.Parse("00000000-0000-0000-0000-000000000013"),
        ];
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            foreach (var (id, index) in ids.Select((id, index) => (id, index)))
            {
                context.Transfers.Add(new TransferEntity
                {
                    Id = id,
                    WorkflowId = workflowId,
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = index + 1,
                    Direction = "Download",
                    Source = index == 1 ? "YtDlpFallback" : "SoulseekPeer",
                    Username = index == 2 ? "special-user" : "user",
                    State = index == 0 ? "Failed" : "Completed",
                    TerminalOutcome = "Succeeded",
                    TotalBytes = 100,
                    TransferredBytes = 100,
                    AttemptCount = index == 0 ? 225 : 0,
                    CreatedAtUtc = 100,
                    CompletedAtUtc = 200,
                    FailureReason = "None",
                    Revision = 2,
                });
            }
            for (int attempt = 1; attempt <= 225; attempt++)
            {
                context.TransferAttempts.Add(new TransferAttemptEntity
                {
                    Id = Guid.NewGuid(),
                    TransferId = ids[0],
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = attempt,
                    AttemptNumber = attempt,
                    Source = "SoulseekPeer",
                    State = "Completed",
                    StartedAtUtc = 100 + attempt,
                    CompletedAtUtc = 200 + attempt,
                    FailureReason = "None",
                    Revision = 2,
                });
            }
            await context.SaveChangesAsync();
        }

        var reader = new TransferHistoryReader(database.Factory);
        var first = await reader.GetTransfersAsync(new TransferHistoryQuery(Limit: 2, WorkflowId: workflowId));
        Guid concurrentlyInsertedId = Guid.Parse("00000000-0000-0000-0000-000000000014");
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Transfers.Add(new TransferEntity
            {
                Id = concurrentlyInsertedId,
                WorkflowId = workflowId,
                LastRuntimeId = runtime.RuntimeId,
                LastSequence = 10,
                Direction = "Download",
                Source = "SoulseekPeer",
                Username = "new-user",
                State = "InProgress",
                TerminalOutcome = "None",
                TotalBytes = 100,
                TransferredBytes = 25,
                AttemptCount = 1,
                CreatedAtUtc = 101,
                FailureReason = "None",
                Revision = 1,
            });
            TransferEntity changed = await context.Transfers.SingleAsync(item => item.Id == ids[0]);
            changed.State = "ArchivedDisplayChange";
            changed.Revision++;
            await context.SaveChangesAsync();
        }
        var second = await reader.GetTransfersAsync(new TransferHistoryQuery(first.NextCursor, 2, WorkflowId: workflowId));
        CollectionAssert.AreEqual(ids.Reverse().Take(2).ToArray(), first.Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(ids.Take(1).ToArray(), second.Items.Select(item => item.Id).ToArray());
        Assert.IsNull(second.NextCursor);
        Assert.IsFalse(second.Items.Any(item => item.Id == concurrentlyInsertedId),
            "A row inserted above the moving boundary must not enter an existing traversal.");
        Assert.AreEqual(concurrentlyInsertedId, (await reader.GetTransfersAsync(new TransferHistoryQuery(
            Limit: 1, WorkflowId: workflowId))).Items.Single().Id);
        Assert.AreEqual(1, (await reader.GetTransfersAsync(new TransferHistoryQuery(
            Limit: 10, WorkflowId: workflowId, Source: "YtDlpFallback"))).Items.Count);
        Assert.AreEqual(4, (await reader.GetTransfersAsync(new TransferHistoryQuery(
            Limit: 10, WorkflowId: workflowId, Direction: "download"))).Items.Count);
        Assert.AreEqual(ids[2], (await reader.GetTransfersAsync(new TransferHistoryQuery(
            Limit: 10, Username: "special-user", TerminalOutcome: "Succeeded"))).Items.Single().Id);

        var detail = await reader.GetTransferAsync(ids[0]);
        Assert.IsNotNull(detail);
        Assert.AreEqual(225, detail.Transfer.AttemptCount);
        Assert.AreEqual(225, detail.LatestAttempt?.AttemptNumber);

        var attemptNumbers = new List<int>();
        int afterAttemptNumber = 0;
        int? nextAttemptNumber;
        do
        {
            var page = await reader.GetAttemptsAsync(ids[0], afterAttemptNumber, 100);
            Assert.IsNotNull(page);
            attemptNumbers.AddRange(page.Items.Select(item => item.AttemptNumber));
            nextAttemptNumber = page.NextAttemptNumber;
            afterAttemptNumber = nextAttemptNumber ?? 0;
        }
        while (nextAttemptNumber != null);
        CollectionAssert.AreEqual(Enumerable.Range(1, 225).ToArray(), attemptNumbers.ToArray());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetTransfersAsync(new TransferHistoryQuery("bad", 2)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetTransfersAsync(new TransferHistoryQuery(
            first.NextCursor + new string(' ', 129), 2)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => reader.GetAttemptsAsync(ids[0], 0, TransferHistoryReader.MaximumPageSize + 1));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task TransferArchive_IsReversibleAndRejectsNonterminalRows()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("transfer-archive-test")).Runtime;
        Guid terminalId = Guid.NewGuid();
        Guid activeId = Guid.NewGuid();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Transfers.AddRange(
                new TransferEntity
                {
                    Id = terminalId,
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = 1,
                    Direction = "Upload",
                    Source = "SoulseekPeer",
                    Username = "peer",
                    State = "Completed",
                    TerminalOutcome = "Succeeded",
                    TotalBytes = 100,
                    TransferredBytes = 100,
                    CreatedAtUtc = 100,
                    CompletedAtUtc = 200,
                    FailureReason = "None",
                    Revision = 1,
                },
                new TransferEntity
                {
                    Id = activeId,
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = 2,
                    Direction = "Upload",
                    Source = "SoulseekPeer",
                    Username = "peer",
                    State = "InProgress",
                    TerminalOutcome = "None",
                    TotalBytes = 100,
                    TransferredBytes = 50,
                    AttemptCount = 1,
                    CreatedAtUtc = 101,
                    StartedAtUtc = 101,
                    FailureReason = "None",
                    Revision = 1,
                });
            await context.SaveChangesAsync();
        }

        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        Task writerTask = writer.RunAsync(CancellationToken.None);
        var reader = new TransferHistoryReader(database.Factory, inbox);

        TransferArchiveResult archived = await reader.SetArchivedAsync(
            new TransferArchiveFilter(Direction: "Upload", Username: "peer"),
            archived: true);
        Assert.AreEqual(2, archived.ResolvedCount);
        Assert.AreEqual(1, archived.ChangedCount);
        Assert.AreEqual(1, archived.RejectedCount);
        Assert.AreEqual(1, archived.Reasons["nonterminal"]);
        CollectionAssert.AreEqual(
            new[] { activeId },
            (await reader.GetTransfersAsync(new TransferHistoryQuery(Limit: 10)))
                .Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(
            terminalId,
            (await reader.GetTransfersAsync(new TransferHistoryQuery(Limit: 10, Archived: true)))
                .Items.Single().Id);

        TransferArchiveResult restored = await reader.SetArchivedAsync(
            new TransferArchiveFilter(TransferId: terminalId),
            archived: false);
        Assert.AreEqual(1, restored.ChangedCount);
        Assert.AreEqual(2, (await reader.GetTransfersAsync(new TransferHistoryQuery(Limit: 10))).Items.Count);

        TransferArchiveResult missing = await reader.SetArchivedAsync(
            new TransferArchiveFilter(TransferId: Guid.NewGuid()),
            archived: true);
        Assert.AreEqual(1, missing.RejectedCount);
        Assert.AreEqual(1, missing.Reasons["not-found"]);

        inbox.Complete();
        await writerTask;
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_RecoversAfterExhaustedSqliteBusy_WithoutStoppingProducer()
    {
        await using var database = new WriterDatabase(
            defaultTimeoutSeconds: 1,
            busyTimeoutMilliseconds: 25);
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("busy-recovery-test")).Runtime;
        var options = new PersistenceWriterOptions
        {
            BusyRetryCount = 0,
            FailureRetryDelay = TimeSpan.FromMilliseconds(25),
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.FailureRecorded += () => failure.TrySetResult();
        health.CommitCompleted += () => committed.TrySetResult();
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        var runTask = writer.RunAsync(CancellationToken.None);

        await using var lockingContext = await database.Factory.CreateDbContextAsync();
        await lockingContext.Database.OpenConnectionAsync();
        await using var lockCommand = lockingContext.Database.GetDbConnection().CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE;";
        await lockCommand.ExecuteNonQueryAsync();

        Guid jobId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural)));
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsFalse(runTask.IsCompleted);
        await using (var release = lockingContext.Database.GetDbConnection().CreateCommand())
        {
            release.CommandText = "ROLLBACK;";
            await release.ExecuteNonQueryAsync();
        }
        await lockingContext.Database.CloseConnectionAsync();

        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inbox.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.IsTrue(await verify.Jobs.AnyAsync(job => job.Id == jobId));
        Assert.AreEqual(PersistenceHealthState.Healthy, health.Snapshot(inbox).State);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_StopsRetryingRecoverableFailure_AfterConfiguredRecoveryLimit()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("bounded-recovery-test")).Runtime;
        var options = new PersistenceWriterOptions
        {
            BusyRetryCount = 0,
            FailureRetryDelay = TimeSpan.FromMilliseconds(10),
            MaximumRecoveryAttempts = 2,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid jobId = Guid.NewGuid();

        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural)));
        inbox.Complete();

        await new PersistenceWriter(new UnavailableContextFactory(), inbox, health, options)
            .RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = health.Snapshot(inbox);
        Assert.AreEqual(PersistenceHealthState.Unhealthy, snapshot.State);
        Assert.AreEqual(1L, snapshot.PermanentlyFailedMutationCount);
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.IsFalse(await verify.Jobs.AnyAsync(job => job.Id == jobId));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_DropsPermanentMutationOnce_AndContinuesWithLaterWork()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("permanent-failure-test")).Runtime;
        var options = new PersistenceWriterOptions { FailureRetryDelay = TimeSpan.FromMilliseconds(25) };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.FailureRecorded += () => failed.TrySetResult();
        health.CommitCompleted += () => committed.TrySetResult();
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        var runTask = writer.RunAsync(CancellationToken.None);

        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtime.RuntimeId, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), 1,
            "missing job", 0, 0, "Complete")));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Guid validJobId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, validJobId, 2, PersistenceMutationPriority.Structural)));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inbox.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = health.Snapshot(inbox);
        Assert.AreEqual(1L, snapshot.PermanentlyFailedMutationCount);
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.IsTrue(await verify.Jobs.AnyAsync(job => job.Id == validJobId));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_DrainsAwaitableCommandsBeyondFairnessBurstOnShutdown()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        AwaitablePersistenceCommand<int>[] commands = Enumerable.Range(1, 40)
            .Select(value => new AwaitablePersistenceCommand<int>(
                (_, _) => Task.FromResult(value)))
            .ToArray();
        foreach (AwaitablePersistenceCommand<int> command in commands)
            await inbox.EnqueueCommandAsync(command, CancellationToken.None);
        inbox.Complete();

        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(
            Enumerable.Range(1, 40).ToArray(),
            await Task.WhenAll(commands.Select(command => command.Task)));
        Assert.AreEqual(0, inbox.CommandDepth);
    }

    [TestMethod]
    public async Task Writer_CancellationCompletesInFlightCommandWaiter()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AwaitablePersistenceCommand<int>(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        });
        await inbox.EnqueueCommandAsync(command, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task writer = new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => writer.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => command.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static JobPersistenceMutation Job(
        Guid runtimeId,
        Guid jobId,
        long revision,
        PersistenceMutationPriority priority,
        bool terminal = false,
        string kind = "Song")
        => new(
            runtimeId,
            revision,
            DateTimeOffset.UtcNow,
            jobId,
            revision,
            priority,
            Guid.NewGuid(),
            null,
            null,
            null,
            DisplayId: Math.Abs(jobId.GetHashCode()) + 1L,
            kind,
            terminal ? "Terminal" : "Pending",
            "None",
            null,
            terminal ? "Succeeded" : "None",
            "None",
            "None",
            "None",
            null,
            null,
            null,
            kind == "Search" ? "query" : null,
            1,
            null);

    private static TransferPersistenceMutation Transfer(Guid runtimeId, Guid transferId, long revision)
        => new(
            runtimeId,
            revision,
            DateTimeOffset.UtcNow,
            transferId,
            revision,
            PersistenceMutationPriority.Progress,
            null,
            null,
            "Download",
            "SoulseekPeer",
            "user",
            "remote",
            "local",
            "InProgress",
            "None",
            100,
            revision,
            1,
            "None",
            null);

    private static SearchResultPersistenceRecord Result(long sequence)
        => new(
            Guid.NewGuid(),
            sequence,
            sequence,
            "user",
            $"file-{sequence}.mp3",
            100,
            320,
            null,
            1,
            44_100,
            180,
            ".mp3",
            1_000,
            true,
            null,
            DateTimeOffset.UtcNow);

    private static async Task<PersistenceHealthSnapshot> RunCompletedWriterAsync(
        WriterDatabase database,
        IReadOnlyList<PersistenceMutation> mutations)
    {
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        foreach (var mutation in mutations)
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);
        return health.Snapshot(inbox);
    }

    private static async Task AssertCanceledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected the persistence writer to stop by cancellation.");
    }

    private sealed class WriterDatabase : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sockseek-writer-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabaseOwner owner;

        public WriterDatabase(
            int defaultTimeoutSeconds = 5,
            int busyTimeoutMilliseconds = 5_000)
        {
            Directory.CreateDirectory(directory);
            var sqliteOptions = new SockseekSqliteOptions(
                Path.Combine(directory, "sockseek.db"),
                DefaultTimeoutSeconds: defaultTimeoutSeconds,
                BusyTimeoutMilliseconds: busyTimeoutMilliseconds);
            owner = SqliteDatabaseOwner.Acquire(sqliteOptions);
            Factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(sqliteOptions));
            Initializer = new SqliteInitializer(Factory, sqliteOptions, owner);
        }

        public SockseekDbContextFactory Factory { get; }
        public SqliteInitializer Initializer { get; }

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnavailableContextFactory : IDbContextFactory<SockseekDbContext>
    {
        public SockseekDbContext CreateDbContext()
            => throw new IOException("Simulated unavailable persistence volume.");

        public Task<SockseekDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromException<SockseekDbContext>(new IOException("Simulated unavailable persistence volume."));
    }
}
