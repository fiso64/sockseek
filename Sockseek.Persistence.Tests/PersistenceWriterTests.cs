using Sockseek.Core.Models;
using System.Threading.Channels;
using System.Diagnostics;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Settings;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class PersistenceWriterTests
{
    [TestMethod]
    public async Task SubmissionDeletionSettlesQueuedWritesAndPreservesTransfers()
    {
        await using var database = new WriterDatabase();
        var runtime = (await new PersistenceRuntimeSession(database.Factory).StartAsync("deletion")).Runtime;
        Guid submission = Guid.NewGuid(), jobId = Guid.NewGuid(), transferId = Guid.NewGuid();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Submissions.Add(new SubmissionEntity { Id = submission, SpecificationSchemaVersion = 1,
                Specification = new() { Json = "{}" }, Revision = 1 });
            await context.SaveChangesAsync();
        }
        var options = new PersistenceWriterOptions { MaximumBatchSize = 1 };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var store = new SubmissionStore(database.Factory, inbox);
        var job = Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, terminal: true, kind: "Search")
            with { SubmissionId = submission };
        Assert.IsTrue(inbox.TryEnqueue(job));
        Assert.IsTrue(inbox.TryEnqueue(job with { Revision = 2, Priority = PersistenceMutationPriority.Ordinary }));
        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(runtime.RuntimeId, 3,
            DateTimeOffset.UtcNow, jobId, 3, "query", 0, 0, "Complete")));
        Assert.IsTrue(inbox.TryEnqueue(Transfer(runtime.RuntimeId, transferId, 4) with
        {
            JobId = jobId, Priority = PersistenceMutationPriority.Terminal,
            State = "Completed", TerminalOutcome = "Succeeded", TransferredBytes = 100,
        }));
        Task<SubmissionDeleteResult> deletion = store.DeleteAsync(submission);
        Assert.IsFalse(deletion.IsCompleted, "Deletion must wait for all previously queued lanes.");
        Task writing = new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);
        try
        {
            var result = await deletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, result.AffectedJobCount);
            Assert.AreEqual(0, result.RejectedSubmissionCount);
        }
        finally { inbox.Complete(); await writing; }
        await using var read = await database.Factory.CreateDbContextAsync();
        Assert.AreEqual(0, await read.Submissions.CountAsync());
        Assert.AreEqual(0, await read.Set<SubmissionSpecificationEntity>().CountAsync());
        Assert.AreEqual(0, await read.Jobs.CountAsync());
        Assert.AreEqual(0, await read.SearchJobs.CountAsync());
        var retained = await read.Transfers.SingleAsync();
        Assert.AreEqual(transferId, retained.Id);
        Assert.IsNull(retained.JobId);
        Assert.AreEqual(100L, retained.TransferredBytes);
        Assert.AreEqual(0L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
        var followUp = Job(runtime.RuntimeId, Guid.NewGuid(), 5, PersistenceMutationPriority.Structural)
            with { SourceJobId = jobId };
        var followUpHealth = await RunCompletedWriterAsync(database, [followUp]);
        Assert.AreEqual(0L, followUpHealth.PermanentlyFailedMutationCount);
        Assert.IsNull((await read.Jobs.SingleAsync()).SourceJobId);

    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UploadRetrySupersedesOnlyEarlierMatchingTerminalRows(bool oldArrivesLate)
    {
        await using var database = new WriterDatabase();
        var runtime = (await new PersistenceRuntimeSession(database.Factory).StartAsync("upload-retry")).Runtime;
        var requested = DateTimeOffset.UtcNow.AddSeconds(-1);
        var failed = Transfer(runtime.RuntimeId, Guid.NewGuid(), 2) with
        {
            Direction = "Upload", State = "Failed", TerminalOutcome = "Failed",
            Priority = PersistenceMutationPriority.Terminal, RequestedAtUtc = requested,
            FailureMessage = "Peer disconnected.",
        };
        var retry = failed with { TransferId = Guid.NewGuid(), Sequence = 3, Revision = 1,
            State = "Queued", TerminalOutcome = "None", Priority = PersistenceMutationPriority.Structural,
            RequestedAtUtc = requested, FailureMessage = null };
        var otherPeer = failed with { TransferId = Guid.NewGuid(), Username = "User" };
        var otherPath = failed with { TransferId = Guid.NewGuid(), RemotePath = "Remote" };
        var download = failed with { TransferId = Guid.NewGuid(), Direction = "Download" };
        var observer = new RecordingMutationObserver();
        Assert.AreEqual(0L, (await RunCompletedWriterAsync(database,
            [otherPeer, otherPath, download, oldArrivesLate ? retry : failed], observer)).PermanentlyFailedMutationCount);
        Assert.AreEqual(0L, (await RunCompletedWriterAsync(database,
            [oldArrivesLate ? failed : retry], observer)).PermanentlyFailedMutationCount);
        await using var read = await database.Factory.CreateDbContextAsync();
        var rows = await read.Transfers.ToDictionaryAsync(row => row.Id);
        Assert.IsNotNull(rows[failed.TransferId].RemovedAtUtc);
        Assert.AreEqual("Peer disconnected.", rows[failed.TransferId].FailureMessage);
        Assert.IsTrue(rows.Values.Where(row => row.Id != failed.TransferId).All(row => row.RemovedAtUtc is null));
        CollectionAssert.AreEqual(new[] { failed.TransferId }, observer.SupersededTransfers);
        // A later progress write must not resurrect the superseded row.
        await RunCompletedWriterAsync(database, [failed with { Revision = 4, Sequence = 4 }]);
        Assert.IsNotNull(await read.Transfers.AsNoTracking().Where(row => row.Id == failed.TransferId)
            .Select(row => row.RemovedAtUtc).SingleAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Inbox_ReplacesPendingJobStateButKeepsNewerPostTerminalFacts(bool composite)
    {
        var inbox = new PersistenceInbox(new(), new());
        var job = Job(Guid.NewGuid(), Guid.NewGuid(), 1, PersistenceMutationPriority.Ordinary) with
        {
            PayloadJson = new(() => throw new InvalidOperationException("Replaced detail must not be encoded.")),
        };
        Assert.IsTrue(inbox.TryEnqueue(job with { FailureMessage = "old" }));
        Assert.IsTrue(inbox.TryEnqueue(job with
        {
            Revision = 3, FailureMessage = null, PayloadJson = new(() => "{\"latest\":true}"),
        }));
        Assert.IsTrue(inbox.TryEnqueue(job with { Revision = 2, FailureMessage = "stale" }));
        var latest = (JobPersistenceMutation)inbox.DrainBatch().Single();
        Assert.AreEqual(3L, latest.Revision);
        Assert.IsNull(latest.FailureMessage);
        Assert.IsFalse(latest.PayloadJson!.IsValueCreated);
        Assert.AreEqual("{\"latest\":true}", latest.PayloadJson.Value);
        Assert.IsFalse(job.PayloadJson!.IsValueCreated);

        Assert.IsTrue(inbox.TryEnqueue(job with { Revision = 4 }));
        var terminal = job with
        {
            Revision = 5, Priority = PersistenceMutationPriority.Terminal,
            LifecycleState = "Terminal", TerminalOutcome = "Succeeded",
        };
        PersistenceMutation completed = composite
            ? new TransferTerminalPersistenceMutation(Transfer(job.RuntimeId, Guid.NewGuid(), 5), null, terminal)
            : terminal;
        Assert.IsTrue(inbox.TryEnqueue(completed));
        CollectionAssert.AreEqual(new[] { completed }, inbox.DrainBatch().ToArray());
        // Placement can publish newer final paths after the terminal transition.
        var placed = terminal with { Revision = 6, Priority = PersistenceMutationPriority.Ordinary, PayloadJson = new(() => "{\"moved\":true}") };
        Assert.IsTrue(inbox.TryEnqueue(placed));
        Assert.AreSame(placed, inbox.DrainBatch().Single());
        inbox.Complete();
    }

    [TestMethod]
    public async Task PayloadEncodingSkipsStaleFactsAndKeepsFailuresIndependentWithoutReservingWrites()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("payload-encoding")).Runtime;
        var seeded = Job(runtime.RuntimeId, Guid.NewGuid(), 2, PersistenceMutationPriority.Structural) with
        {
            PayloadJson = new(() => "{\"previous\":true}"),
        };
        var cleared = seeded with { JobId = Guid.NewGuid(), DisplayId = seeded.DisplayId + 1 };
        Assert.AreEqual(0L, (await RunCompletedWriterAsync(database, [seeded, cleared])).PermanentlyFailedMutationCount);
        await using var context = await database.Factory.CreateDbContextAsync();
        string connectionString = context.Database.GetConnectionString()!;
        int goodEncodings = 0, badEncodings = 0;
        bool writeAccessAvailable = false;
        var stale = seeded with
        {
            Sequence = 11, Revision = 1,
            PayloadJson = new(() => throw new InvalidOperationException("Stale detail must not be encoded.")),
        };
        var good = Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural) with
        {
            Sequence = 13,
            PayloadJson = new(() =>
            {
                goodEncodings++;
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA busy_timeout=0; BEGIN IMMEDIATE; ROLLBACK;";
                try { command.ExecuteNonQuery(); writeAccessAvailable = true; }
                catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { }
                return "{\"captured\":true}";
            }),
        };
        var bad = good with
        {
            JobId = Guid.NewGuid(), Sequence = 10,
            PayloadJson = new(() => { badEncodings++; throw new InvalidOperationException("Cannot encode this entry."); }),
        };
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var observer = new RecordingMutationObserver();
        var inbox = new PersistenceInbox(options, health, observer);
        foreach (var mutation in new[] { bad, stale, good, cleared with { Sequence = 12, Revision = 3, PayloadJson = new(() => null) } })
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        Assert.AreEqual(0, goodEncodings + badEncodings, "Admission must not encode captured detail.");
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options, mutationObserver: observer)
            .RunAsync(CancellationToken.None);

        var rows = await context.Jobs.AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual("{\"previous\":true}", rows[seeded.JobId].PayloadJson);
        Assert.IsNull(rows[cleared.JobId].PayloadJson);
        Assert.AreEqual("{\"captured\":true}", rows[good.JobId].PayloadJson);
        Assert.IsTrue(writeAccessAvailable, "Encoding must not reserve SQLite's writer.");
        Assert.AreEqual(1, goodEncodings);
        Assert.AreEqual(1, badEncodings);
        Assert.IsFalse(stale.PayloadJson!.IsValueCreated);
        CollectionAssert.AreEqual(new PersistenceMutation[] { bad }, observer.Failed);
        Assert.AreEqual(1L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
        await session.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Inbox_ReplacesTransferStateAndCarriesAccountingThroughCompletion(bool composite)
    {
        var inbox = new PersistenceInbox(new(), new());
        var job = Job(Guid.NewGuid(), Guid.NewGuid(), 1, PersistenceMutationPriority.Ordinary);
        Guid attemptId = Guid.NewGuid();
        var firstBucket = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var transfer = Transfer(job.RuntimeId, job.JobId, 2) with
        {
            Priority = PersistenceMutationPriority.Ordinary,
            GroupRelativePath = "Disc/Track.flac",
            AccountingObservations = [new(attemptId, 2, firstBucket, 10)],
        };
        Assert.IsTrue(inbox.TryEnqueue(job));
        Assert.IsTrue(inbox.TryEnqueue(transfer));
        Assert.IsTrue(inbox.TryEnqueue(transfer with
        {
            Revision = 4, TransferredBytes = 15,
            AccountingObservations = [new(attemptId, 4, firstBucket.AddMinutes(6), 15)],
        }));
        Assert.IsTrue(inbox.TryEnqueue(transfer with
        {
            Revision = 3, GroupRelativePath = null,
            AccountingObservations = [new(attemptId, 3, firstBucket.AddMinutes(3), 3)],
        }));
        var state = inbox.DrainBatch();
        Assert.AreEqual(2, state.Count, "Job and transfer IDs occupy separate state entries.");
        Assert.AreSame(job, state[0]);
        var latest = (TransferPersistenceMutation)state[1];
        Assert.AreEqual(4L, latest.Revision);
        Assert.AreEqual(15L, latest.TransferredBytes);
        CollectionAssert.AreEqual(new long[] { 10, 3, 15 },
            latest.AccountingObservations!.Select(row => row.CumulativeBytes).ToArray());

        Assert.IsTrue(inbox.TryEnqueue(latest));
        Assert.IsTrue(inbox.TryEnqueue(latest with
        {
            Revision = 5, Priority = PersistenceMutationPriority.Progress,
            AccountingObservations = [new(attemptId, 5, firstBucket.AddMinutes(7), 18)],
        }));
        var terminal = latest with
        {
            Revision = 6, Priority = PersistenceMutationPriority.Terminal,
            State = "Completed", TransferredBytes = 20, GroupRelativePath = null,
            AccountingObservations = [new(attemptId, 6, firstBucket.AddMinutes(8), 20)],
        };
        Assert.IsTrue(inbox.TryEnqueue(composite
            ? new TransferTerminalPersistenceMutation(terminal, null, null) : terminal));
        Assert.AreEqual(0, inbox.ProgressCount);
        var completed = inbox.DrainBatch().Single();
        var captured = completed is TransferTerminalPersistenceMutation wrapper
            ? wrapper.Transfer : (TransferPersistenceMutation)completed;
        Assert.AreEqual("Completed", captured.State);
        Assert.AreEqual("Disc/Track.flac", captured.GroupRelativePath);
        CollectionAssert.AreEqual(new long[] { 10, 3, 20 },
            captured.AccountingObservations!.Select(row => row.CumulativeBytes).ToArray());

        // A newer final path is still valid after completion.
        var placed = captured with { Revision = 7, Priority = PersistenceMutationPriority.Ordinary, LocalPath = "placed" };
        Assert.IsTrue(inbox.TryEnqueue(placed));
        Assert.AreSame(placed, inbox.DrainBatch().Single());
        inbox.Complete();
    }

    [TestMethod]
    public void Inbox_DoesNotCoalesceManifestAttemptsOrAcceptedIntentAsOrdinaryState()
    {
        var inbox = new PersistenceInbox(new(), new());
        var job = Job(Guid.NewGuid(), Guid.NewGuid(), 1, PersistenceMutationPriority.Ordinary);
        DirectoryManifestSnapshot Manifest(int attempt) => new(attempt, new("Folder", [], 0), false, [], []);
        var first = job with { DirectoryManifest = Manifest(1) };
        var second = job with { Revision = 2, DirectoryManifest = Manifest(2) };
        foreach (var mutation in new[] { first, second, job with { Revision = 4 } })
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        var batch = inbox.DrainBatch();
        Assert.AreEqual(3, batch.Count);
        CollectionAssert.AreEquivalent(new[] { first, second }, batch.Where(item => item.Revision < 4).ToArray());
        inbox.Complete();
    }

    [TestMethod]
    [DataRow("full")]
    [DataRow("command")]
    [DataRow("shutdown")]
    [DataRow("deadline")]
    [DataRow("cancel")]
    public async Task Inbox_BulkGatheringEndsWithoutHoldingUpCommandsOrShutdown(string wake)
    {
        var inbox = new PersistenceInbox(new() { MaximumBatchSize = 3 }, new());
        var clock = new BatchClock();
        using var cancellation = new CancellationTokenSource();
        Assert.IsTrue(inbox.TryEnqueue(Transfer(Guid.NewGuid(), Guid.NewGuid(), 1) with
        {
            Priority = PersistenceMutationPriority.Structural,
        }));
        var gathering = inbox.ReadBatchAsync(true, TimeSpan.FromHours(1), clock, cancellation.Token).AsTask();
        Assert.IsFalse(gathering.IsCompleted);
        AwaitablePersistenceCommand<int>? command = null;
        switch (wake)
        {
            case "full":
                Assert.IsTrue(inbox.TryEnqueue(Transfer(Guid.NewGuid(), Guid.NewGuid(), 1) with
                {
                    Priority = PersistenceMutationPriority.Structural,
                }));
                Assert.IsFalse(gathering.IsCompleted);
                Assert.IsTrue(inbox.TryEnqueue(Transfer(Guid.NewGuid(), Guid.NewGuid(), 1) with
                {
                    Priority = PersistenceMutationPriority.Structural,
                }));
                break;
            case "command":
                command = new((_, _) => Task.FromResult(7));
                await inbox.EnqueueCommandAsync(command, CancellationToken.None);
                break;
            case "shutdown": inbox.Complete(); break;
            case "deadline": clock.Expire(); break;
            case "cancel": cancellation.Cancel(); break;
        }
        IReadOnlyList<PersistenceMutation> gathered;
        if (wake == "cancel")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => gathering);
            gathered = inbox.DrainBatch(); // Cancelled gathering retains its captured work.
        }
        else gathered = await gathering.WaitAsync(TimeSpan.FromSeconds(5));
        if (command != null)
        {
            Assert.IsTrue(inbox.TryDequeueCommand(out var ready));
            Assert.AreSame(command, ready);
            await command.ApplyAsync(null!, CancellationToken.None);
            command.Complete();
            Assert.AreEqual(7, await command.Task);
        }
        Assert.AreEqual(wake == "full" ? 3 : 1, gathered.Count);
        inbox.Complete();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Inbox_SearchGatheringFillsBatchesWithoutDelayingFirstOrFinishedResults(bool initial, bool finishEarly)
    {
        var inbox = new PersistenceInbox(new()
        {
            SearchMutationQueueCapacity = 1, SearchResultFlushCount = 6, MaximumBatchSize = 8,
        }, new());
        Guid runtime = Guid.NewGuid(), source = Guid.NewGuid();
        SearchResultsPersistenceMutation Packet(int first) => new(runtime, first, DateTimeOffset.UtcNow,
            source, first + 1, [Result(first), Result(first + 1)]);
        int first = initial ? 1 : 2;
        Assert.IsTrue(await inbox.EnqueueAsync(Packet(first)));
        var producer = finishEarly ? Task.CompletedTask : ProduceRemainingAsync();
        if (!finishEarly) Assert.IsFalse(producer.IsCompleted);
        using var cancellation = new CancellationTokenSource();
        try
        {
            // No clock expiry: draining must release capacity, then gather the
            // producer's next packet rather than committing the queue's prefix.
            var gathering = inbox.ReadBatchAsync(true, TimeSpan.FromHours(1), new BatchClock(), cancellation.Token).AsTask();
            if (initial) Assert.IsTrue(gathering.IsCompleted, "First results bypass the bulk gathering window.");
            if (finishEarly)
            {
                Assert.IsFalse(gathering.IsCompleted);
                Assert.IsTrue(await inbox.EnqueueAsync(new SearchCompletionPersistenceMutation(
                    runtime, 10, DateTimeOffset.UtcNow, source, 10, "finished", 2, 0, "Complete")));
            }
            var batch = await gathering.WaitAsync(TimeSpan.FromSeconds(5));
            await producer.WaitAsync(TimeSpan.FromSeconds(5));
            if (!initial && !finishEarly) Assert.AreEqual(6, batch.OfType<SearchResultsPersistenceMutation>().Sum(item => item.Results.Count));
            if (finishEarly) Assert.IsInstanceOfType<SearchCompletionPersistenceMutation>(batch[^1]);
            CollectionAssert.AreEqual(Enumerable.Range(first, finishEarly ? 2 : 6).Select(i => (long)i).ToArray(),
                batch.Concat(inbox.DrainBatch()).OfType<SearchResultsPersistenceMutation>()
                    .SelectMany(item => item.Results).Select(item => item.Sequence).ToArray());
            Assert.AreEqual(0, inbox.BufferedSearchResultCount);
        }
        finally { cancellation.Cancel(); inbox.Complete(); }

        async Task ProduceRemainingAsync()
        {
            Assert.IsTrue(await inbox.EnqueueAsync(Packet(first + 2)));
            Assert.IsTrue(await inbox.EnqueueAsync(Packet(first + 4)));
        }
    }

    private sealed class BatchClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
        private Action? expire;
        public void Expire() => expire!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            expire = () => callback(state);
            return new BatchTimer();
        }
        private sealed class BatchTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    [DataRow("jobs")]
    [DataRow("search")]
    [DataRow("transfers")]
    public async Task HistorySnapshots_ReadCommittedDataWhileAWriterRemainsActive(string domain)
    {
        await using var database = new WriterDatabase(defaultTimeoutSeconds: 1, busyTimeoutMilliseconds: 1);
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("concurrent-history-read-test")).Runtime;
        Guid jobId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        var job = Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, kind: "Search");
        await RunCompletedWriterAsync(database, [job, Transfer(runtime.RuntimeId, transferId, 1) with
        {
            JobId = jobId,
            WorkflowId = job.WorkflowId,
            GroupRef = "folder",
        }]);
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            await seed.SearchJobs.Where(row => row.JobId == jobId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.ResultCount, 1));
            await TestPersistenceWriter.SeedSearchResultAsync(seed, new SearchResultEntity
            {
                SearchJobId = jobId, Sequence = 1, Revision = 1,
                Username = "user", RemoteFilename = "file.mp3", SizeBytes = 100,
            });
            await seed.SaveChangesAsync();
        }

        await using var writer = await database.Factory.CreateDbContextAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Transfers.Where(row => row.Id == transferId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.TransferredBytes, 99));
        await writer.SearchJobs.Where(row => row.JobId == jobId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ResultCount, 99));
        await writer.Jobs.Where(row => row.Id == jobId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, 99));

        // The writer stays open throughout: readers must neither wait for it nor see its uncommitted changes.
        var elapsed = Stopwatch.StartNew();
        if (domain == "jobs")
        {
            var reader = new JobHistoryReader(database.Factory);
            var listed = (await reader.GetJobsAsync(new JobHistoryQuery())).Items.Single();
            Assert.AreEqual(1L, listed.Revision);
            Assert.AreEqual(1L, listed.DiscoveryPublicFileCount,
                "Job facts and search counters must both come from committed state.");
            Assert.AreEqual(listed, await reader.GetJobAsync(jobId));
            Assert.AreEqual(listed, await reader.GetJobByDisplayIdAsync(job.WorkflowId, job.DisplayId));
            var selected = await reader.GetJobsByIdsAsync([jobId, Guid.NewGuid(), jobId]);
            Assert.AreEqual(listed, selected.Single(),
                "Explicit IDs deduplicate and ignore missing rows without changing retained facts.");
            Assert.AreEqual(1, (await reader.GetWorkflowsAsync()).Items.Count);
            Assert.IsNotNull(await reader.GetWorkflowAsync(job.WorkflowId));
            Assert.IsNull(await reader.GetDirectoryManifestAsync(jobId, null, -1, 1));
        }
        else if (domain == "search")
        {
            var reader = new SearchHistoryReader(database.Factory);
            Assert.AreEqual(1L, (await reader.GetRawResultsAsync(jobId, 0, 1))?.Metadata.ResultCount);
            Assert.AreEqual("file.mp3", (await reader.GetResultAsync(jobId, "user", "file.mp3"))?.Result?.Filename);
        }
        else
        {
            var reader = new TransferHistoryReader(database.Factory);
            var timeline = await reader.GetTransfersAsync(new TransferQuery(Limit: 500));
            Assert.AreEqual(1L, timeline.Items.Single().TransferredBytes);
            var selected = await reader.GetTransfersAsync(new TransferQuery(
                Limit: 500, TransferIds: [transferId]));
            Assert.AreEqual(1L, selected.Items.Single().TransferredBytes);
            var group = await reader.GetGroupProjectionAsync([new("Download", "user", "folder")], [transferId]);
            Assert.AreEqual(1L, group.Summaries.Single().TransferredBytes);
            Assert.AreEqual(1L, group.OverlappingTransfers.Single().TransferredBytes);
            Assert.AreEqual(1L, (await reader.GetTransferAsync(transferId))?.Transfer.TransferredBytes);
            Assert.AreEqual(0, (await reader.GetAttemptsAsync(transferId, 0, 1))?.Items.Count);
        }
        Console.WriteLine($"{domain} snapshots with active writer: {elapsed.Elapsed.TotalMilliseconds:F1} ms");
        await transaction.CommitAsync();
        if (domain == "transfers")
            Assert.AreEqual(99L, (await new TransferHistoryReader(database.Factory)
                .GetTransfersAsync(new TransferQuery(Limit: 500))).Items.Single().TransferredBytes);
        Assert.AreEqual(99L, (await new TransferHistoryReader(database.Factory).GetTransferAsync(transferId))?.Transfer.TransferredBytes);
        Assert.AreEqual(99L, (await new SearchHistoryReader(database.Factory).GetMetadataAsync(jobId))?.ResultCount);
        Assert.AreEqual(99L, (await new JobHistoryReader(database.Factory).GetJobAsync(jobId))?.Revision);
        await session.StopAsync();
    }

    [TestMethod]
    public async Task TransferGroupSnapshot_KeepsTotalsAndOverlapConsistentAcrossWriterCommit()
    {
        await using var database = new WriterDatabase(defaultTimeoutSeconds: 1, busyTimeoutMilliseconds: 1);
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("group-snapshot-test")).Runtime;
        Guid transferId = Guid.NewGuid();
        await RunCompletedWriterAsync(database, [Transfer(runtime.RuntimeId, transferId, 1) with { GroupRef = "folder" }]);
        await using var writer = await database.Factory.CreateDbContextAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Transfers.Where(row => row.Id == transferId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.TransferredBytes, 99));

        // Commit between the overlap SELECT and the aggregate SELECT, not before/after the operation.
        var interceptor = new CommitAfterFirstReadInterceptor(() => transaction.CommitAsync());
        var options = new DbContextOptionsBuilder<SockseekDbContext>()
            .UseSqlite(writer.Database.GetConnectionString()).AddInterceptors(interceptor).Options;
        var reader = new TransferHistoryReader(new SockseekDbContextFactory(options));
        var projection = await reader.GetGroupProjectionAsync([new("Download", "user", "folder")], [transferId]);
        Assert.IsTrue(interceptor.Committed);
        Assert.AreEqual(1L, projection.Summaries.Single().TransferredBytes);
        Assert.AreEqual(1L, projection.OverlappingTransfers.Single().TransferredBytes);
        Assert.AreEqual(99L, (await reader.GetTransferAsync(transferId))?.Transfer.TransferredBytes);
        await session.StopAsync();
    }

    [TestMethod]
    public async Task Analytics_ByteAndPeerTotalsUseOneSnapshotAcrossWriterCommit()
    {
        await using var database = new WriterDatabase(defaultTimeoutSeconds: 1, busyTimeoutMilliseconds: 1);
        await using var writer = await database.Factory.CreateDbContextAsync();
        writer.TransferByteBuckets.Add(new TransferByteBucketEntity
        {
            BucketStartUtc = 0, Direction = "Download", Username = "peer", Bytes = 1,
        });
        await writer.SaveChangesAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.TransferByteBuckets.ExecuteUpdateAsync(update => update.SetProperty(row => row.Bytes, 99));

        var interceptor = new CommitAfterFirstReadInterceptor(() => transaction.CommitAsync());
        var options = new DbContextOptionsBuilder<SockseekDbContext>()
            .UseSqlite(writer.Database.GetConnectionString()).AddInterceptors(interceptor).Options;
        var reader = new TransferAnalyticsReader(new SockseekDbContextFactory(options));
        var query = new TransferAnalyticsQuery(DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5), TimeSpan.FromMinutes(5));
        var analytics = await reader.GetAsync(query);
        Assert.IsTrue(interceptor.Committed);
        Assert.AreEqual(1L, analytics.DownloadBytes);
        Assert.AreEqual(1L, analytics.Peers.Single().Bytes,
            "One response must not mix byte totals from before a commit with peer totals from after it.");
        var fresh = await reader.GetAsync(query);
        Assert.AreEqual(99L, fresh.DownloadBytes);
        Assert.AreEqual(99L, fresh.Peers.Single().Bytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Analytics_CoverageCurrentAndComparisonStayCoherentAcrossRetention(bool allHistory)
    {
        await using var database = new WriterDatabase(defaultTimeoutSeconds: 1, busyTimeoutMilliseconds: 1);
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        DateTimeOffset retainedFrom = start.AddMinutes(-5);
        DateTimeOffset prunedFrom = start.AddMinutes(5);
        await using var writer = await database.Factory.CreateDbContextAsync();
        await writer.TransferAccountingStates.ExecuteUpdateAsync(update => update
            .SetProperty(row => row.CompleteFromUtc, retainedFrom.ToUnixTimeMilliseconds()));
        writer.TransferByteBuckets.AddRange(
            new TransferByteBucketEntity { BucketStartUtc = start.ToUnixTimeMilliseconds(), Direction = "Download", Username = "peer", Bytes = 1 },
            new TransferByteBucketEntity { BucketStartUtc = retainedFrom.ToUnixTimeMilliseconds(), Direction = "Download", Username = "peer", Bytes = 2 });
        await writer.SaveChangesAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.TransferByteBuckets.ExecuteDeleteAsync();
        await writer.TransferAccountingStates.ExecuteUpdateAsync(update => update
            .SetProperty(row => row.CompleteFromUtc, prunedFrom.ToUnixTimeMilliseconds()));
        var interceptor = new CommitAfterFirstReadInterceptor(() => transaction.CommitAsync());
        var options = new DbContextOptionsBuilder<SockseekDbContext>()
            .UseSqlite(writer.Database.GetConnectionString()).AddInterceptors(interceptor).Options;
        var reader = new TransferAnalyticsReader(new SockseekDbContextFactory(options));

        var snapshot = await reader.GetSnapshotAsync(coverage => allHistory
            ? [new(coverage.CompleteFromUtc, prunedFrom, TimeSpan.FromMinutes(5))]
            : [new(start, prunedFrom, TimeSpan.FromMinutes(5)), new(retainedFrom, start, TimeSpan.FromMinutes(5))]);
        var coverage = snapshot.Coverage;
        var selected = snapshot.Ranges[0];
        var comparison = allHistory ? null : snapshot.Ranges[1];
        Assert.IsTrue(interceptor.Committed);
        Assert.AreEqual(retainedFrom, coverage.CompleteFromUtc);
        Assert.AreEqual(allHistory ? 3L : 1L, selected.DownloadBytes,
            "Coverage must not promise complete history after retention removed the data being read.");
        if (comparison != null) Assert.AreEqual(2L, comparison.DownloadBytes);
        var fresh = await reader.GetSnapshotAsync(_ => [new(start, prunedFrom, TimeSpan.FromMinutes(5))]);
        Assert.AreEqual(prunedFrom, fresh.Coverage.CompleteFromUtc);
        Assert.AreEqual(0L, fresh.Ranges.Single().DownloadBytes);
    }

    [TestMethod]
    [DataRow("none")]
    [DataRow("job")]
    [DataRow("manifest")]
    public async Task DirectoryManifests_CoalescedJobUpdatesRetainEachAcceptedAttempt(string failureStage)
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("directory-attempt-batch-test")).Runtime;
        Guid jobId = Guid.NewGuid();
        static DirectoryTransferPlanSnapshot Plan(string name) => new(name, [
            new(new(new("Peer", name + "\\File.bin"), 10, ".bin", null, null, null, null, null), []),
        ], 10);
        var first = Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, kind: "RemoteDirectory") with
        {
            DirectoryManifest = new DirectoryManifestSnapshot(1, Plan("First"), false, [], []),
            PayloadJson = new(() => "{\"Directory\":{\"AttemptNumber\":1}}"),
        };
        var second = first with
        {
            Revision = 2,
            Sequence = 2,
            DirectoryManifest = new DirectoryManifestSnapshot(2, Plan("Second"), false, [], []),
            PayloadJson = new(() => "{\"Directory\":{\"AttemptNumber\":2}}"),
        };
        var sibling = Job(runtime.RuntimeId, Guid.NewGuid(), 3, PersistenceMutationPriority.Structural);
        if (failureStage == "manifest")
        {
            await using var setup = await database.Factory.CreateDbContextAsync();
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_manifest BEFORE INSERT ON directory_manifests
                WHEN NEW.display_root = 'Rejected'
                BEGIN SELECT RAISE(ABORT, 'rejected manifest'); END;
                """);
            sibling = sibling with { DirectoryManifest = new(1, Plan("Rejected"), false, [], []) };
        }
        else sibling = sibling with { DisplayId = 0 };
        var health = await RunCompletedWriterAsync(database, failureStage == "none"
            ? [first, second] : [first, second, sibling]);
        Assert.AreEqual(failureStage == "none" ? 0L : 1L, health.PermanentlyFailedMutationCount);
        var reader = new JobHistoryReader(database.Factory);
        var prior = await reader.GetDirectoryManifestAsync(jobId, 1, -1, 1);
        Assert.IsNotNull(prior);
        Assert.AreEqual("First", prior.DisplayRoot);
        var current = await reader.GetDirectoryManifestAsync(jobId, null, -1, 1);
        Assert.IsNotNull(current);
        Assert.AreEqual(2, current.AttemptNumber);
        Assert.AreEqual("Second", current.DisplayRoot);
        Assert.IsNull(await reader.GetDirectoryManifestAsync(sibling.JobId, null, -1, 1));
        Assert.IsNull(await reader.GetJobAsync(sibling.JobId), "Reject the failing owner atomically with its manifest.");
        await session.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectoryManifest_PagesSupplementalFilesWithMissingChildrenAndAppendOnlyOrdinals(bool retainTargets)
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("supplemental-manifest-test")).Runtime;
        Guid jobId = Guid.NewGuid();
        Guid[] supplemental = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        var plan = new DirectoryTransferPlanSnapshot("Album", [
            new(new(new("Peer", @"Album\Track.flac"), 123, ".flac", null, null, null, null, null), []),
        ], 123);
        PeerFileTarget[] targets = Enumerable.Range(0, 501).Select(index => new PeerFileTarget(
            new("ArtworkPeer", $@"Other\Artwork\{index:D3}.jpg"), 128, ".jpg", null, null, null, null, null)).ToArray();
        var owner = Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, kind: "Album") with
        {
            DirectoryManifest = new DirectoryManifestSnapshot(1, plan, true, [Guid.NewGuid()], supplemental, retainTargets ? targets : null),
            PayloadJson = new(() => "{\"Directory\":{\"AttemptNumber\":1}}"),
        };
        var lastChild = Job(runtime.RuntimeId, supplemental[^1], 2, PersistenceMutationPriority.Terminal,
            terminal: true, kind: "Song") with { ParentJobId = jobId, WorkflowId = owner.WorkflowId };
        await RunCompletedWriterAsync(database, [owner, lastChild]);
        var reader = new JobHistoryReader(database.Factory);
        var first = (await reader.GetDirectoryManifestAsync(jobId, 1, -1, 500))!;
        Assert.AreEqual(1, first.EntryCount);
        Assert.AreEqual(501, first.SupplementalEntryCount);
        Assert.AreEqual(500, first.Items.Count);
        Assert.AreEqual(499, first.NextOrdinal);
        Assert.IsNotNull(first.Items[0].Target);
        Assert.AreEqual(supplemental[0], first.Items[1].ChildJobId);
        Assert.AreEqual(retainTargets ? @"Other\Artwork\000.jpg" : null, first.Items[1].Target?.Identity.Filename);
        Assert.IsNull(first.Items[1].Child);
        var tail = (await reader.GetDirectoryManifestAsync(jobId, 1, first.NextOrdinal!.Value, 500))!;
        CollectionAssert.AreEqual(new[] { 500, 501 }, tail.Items.Select(item => item.Ordinal).ToArray());
        Assert.AreEqual(supplemental[^1], tail.Items[^1].ChildJobId);
        Assert.AreEqual("Succeeded", tail.Items[^1].Child?.TerminalOutcome);
        Assert.IsNull(tail.NextOrdinal);
        await using (var context = await database.Factory.CreateDbContextAsync())
            await context.Jobs.Where(row => row.Id == supplemental[500]).ExecuteDeleteAsync();
        var pruned = (await reader.GetDirectoryManifestAsync(jobId, 1, 499, 500))!;
        Assert.AreEqual(501, pruned.SupplementalEntryCount);
        Assert.AreEqual(supplemental[^1], pruned.Items[^1].ChildJobId);
        Assert.IsNull(pruned.Items[^1].Child);
        Assert.AreEqual(retainTargets ? @"Other\Artwork\500.jpg" : null, pruned.Items[^1].Target?.Identity.Filename);

        Guid appended = Guid.NewGuid();
        await RunCompletedWriterAsync(database, [owner with
        {
            Sequence = 3, Revision = 3,
            DirectoryManifest = owner.DirectoryManifest! with
            {
                SupplementalJobIds = [.. supplemental, appended],
                SupplementalTargets = retainTargets ? [.. targets, new(new("ArtworkPeer", @"Other\New.jpg"), 64, ".jpg", null, null, null, null, null)] : null,
            },
        }]);
        var added = (await reader.GetDirectoryManifestAsync(jobId, 1, 501, 500))!;
        Assert.AreEqual(502, added.SupplementalEntryCount);
        Assert.AreEqual(502, added.Items.Single().Ordinal);
        Assert.AreEqual(appended, added.Items.Single().ChildJobId);
        Assert.IsNull(added.NextOrdinal);
    }

    [TestMethod]
    public async Task DirectoryManifest_PagesAcceptedFilesBeforeChildren_AndAttachesChildIdsWithoutRewritingPlan()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("directory-manifest-test")).Runtime;
        Guid jobId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        var plan = new DirectoryTransferPlanSnapshot("Music/Album", [
            new(new(new("ExactUser", "Music\\Album\\01.flac"), 123, ".flac", null, null, null, null, null), []),
            new(new(new("ExactUser", "Music\\Album\\02.flac"), 456, ".flac", null, null, null, null, null), ["Disc 1"]),
        ], 579);
        var first = Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, kind: "RemoteDirectory") with
        {
            DirectoryManifest = new DirectoryManifestSnapshot(1, plan, false, [], []),
            PayloadJson = new(() => "{\"Directory\":{\"AttemptNumber\":1}}"),
        };
        await RunCompletedWriterAsync(database, [first]);
        var reader = new JobHistoryReader(database.Factory);
        var firstPage = await reader.GetDirectoryManifestAsync(jobId, null, -1, 1);
        Assert.IsNotNull(firstPage);
        Assert.AreEqual(2, firstPage.EntryCount);
        Assert.IsFalse(firstPage.ChildrenMaterialized);
        Assert.AreEqual(0, firstPage.Items[0].Ordinal);
        Assert.AreEqual("Music\\Album\\01.flac", firstPage.Items[0].Target!.Identity.Filename);
        Assert.IsNull(firstPage.Items[0].ChildJobId);
        Assert.AreEqual(0, firstPage.NextOrdinal);

        var second = first with
        {
            Sequence = 2,
            Revision = 2,
            DirectoryManifest = new DirectoryManifestSnapshot(1, plan, true, [childId, Guid.NewGuid()], []),
        };
        var child = Job(runtime.RuntimeId, childId, 1, PersistenceMutationPriority.Terminal, terminal: true, kind: "RemoteFile") with
        {
            WorkflowId = first.WorkflowId,
            ParentJobId = jobId,
        };
        await RunCompletedWriterAsync(database, [second, child]);
        var after = await reader.GetDirectoryManifestAsync(jobId, 1, -1, 2);
        Assert.IsNotNull(after);
        Assert.IsTrue(after.ChildrenMaterialized);
        Assert.AreEqual(childId, after.Items[0].ChildJobId);
        Assert.AreEqual("Succeeded", after.Items[0].Child?.TerminalOutcome);
        Assert.AreEqual("Disc 1", after.Items[1].RelativeDirectoryComponents.Single());
        Assert.IsNull(after.NextOrdinal);
        // Replayed pre-materialization state must not detach the accepted children.
        await RunCompletedWriterAsync(database, [first]);
        var replayed = await reader.GetDirectoryManifestAsync(jobId, 1, -1, 2);
        Assert.IsNotNull(replayed);
        Assert.IsTrue(replayed.ChildrenMaterialized);
        Assert.AreEqual(childId, replayed.Items[0].ChildJobId);
        await using (var context = await database.Factory.CreateDbContextAsync())
            await context.Jobs.Where(job => job.Id == childId).ExecuteDeleteAsync();
        var prunedChild = await reader.GetDirectoryManifestAsync(jobId, 1, -1, 2);
        Assert.IsNotNull(prunedChild);
        Assert.AreEqual(childId, prunedChild.Items[0].ChildJobId);
        Assert.IsNull(prunedChild.Items[0].Child);
        Assert.AreEqual(2, prunedChild.Items.Count);
        await RunCompletedWriterAsync(database, [second with
        {
            Revision = 3,
            Sequence = 3,
            DirectoryManifest = null,
            PayloadJson = new(() => "{\"Directory\":{\"AttemptNumber\":null}}"),
        }]);
        Assert.IsNull(await reader.GetDirectoryManifestAsync(jobId, null, -1, 2));
        Assert.IsNotNull(await reader.GetDirectoryManifestAsync(jobId, 1, -1, 2));
        await using (var context = await database.Factory.CreateDbContextAsync())
            await context.Jobs.Where(job => job.Id == jobId || job.Id == childId).ExecuteDeleteAsync();
        Assert.IsNull(await reader.GetDirectoryManifestAsync(jobId, 1, -1, 2));
        await session.StopAsync();
    }

    [TestMethod]
    [TestCategory("Load")]
    [DataRow(PersistenceMutationPriority.Progress)]
    [DataRow(PersistenceMutationPriority.Ordinary)]
    public async Task OneHundredThousandReplaceableCallbacks_ProduceOneLatestValueWrite(
        PersistenceMutationPriority priority)
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("progress-load-test")).Runtime;
        var options = new PersistenceWriterOptions { TransferProgressFlushInterval = TimeSpan.FromSeconds(3) };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        DateTimeOffset observedAt = DateTimeOffset.UnixEpoch.AddHours(1);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int revision = 1; revision <= 100_000; revision++)
            Assert.IsTrue(inbox.TryEnqueue(Transfer(runtime.RuntimeId, transferId, revision) with
            {
                Priority = priority,
                AccountingObservations = [new(attemptId, revision, observedAt, revision)],
            }));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine($"100k {priority} callbacks with accounting: {allocated} allocated bytes.");
        Assert.IsTrue(allocated < 110_000_000L,
            $"100k coalesced accounting callbacks should allocate less than 110 MB; actual: {allocated} bytes.");

        Assert.AreEqual(1, priority == PersistenceMutationPriority.Progress ? inbox.ProgressCount : inbox.OrdinaryDepth);
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None);

        await using var verify = await database.Factory.CreateDbContextAsync();
        var transfer = await verify.Transfers.SingleAsync(item => item.Id == transferId);
        Assert.AreEqual(100_000L, transfer.Revision);
        Assert.AreEqual(100_000L, transfer.TransferredBytes);
        Assert.AreEqual(100_000L, (await verify.TransferAccountingCheckpoints.SingleAsync()).CumulativeBytes);
        Assert.AreEqual(100_000L, (await verify.TransferByteBuckets.SingleAsync()).Bytes);
        Assert.AreEqual(1L, health.Snapshot(inbox).SuccessfulCommitCount);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public void AccountingProgressPreservesResetsBucketsRetriesAndReorderedCorrections()
    {
        Guid attempt = Guid.NewGuid();
        var first = new TransferAccountingObservation(attempt, 1, DateTimeOffset.UnixEpoch.AddHours(1), 10);
        var next = first with { Revision = 2, OccurredAtUtc = first.OccurredAtUtc.AddSeconds(1), CumulativeBytes = 20 };
        var reset = next with { CumulativeBytes = 5 };
        var correction = next with { Revision = 1 };
        var reordered = next with { OccurredAtUtc = first.OccurredAtUtc.AddSeconds(-1) };
        var retry = next with { AttemptId = Guid.NewGuid() };
        var simultaneous = next with { OccurredAtUtc = first.OccurredAtUtc };
        var olderRevision = simultaneous with { Revision = 0 };
        var nextBucket = next with { OccurredAtUtc = first.OccurredAtUtc.AddMinutes(5) };
        var cases = new[]
        {
            ("progress", next, new[] { next }),
            ("reset", reset, new[] { first, reset }),
            ("correction", correction, new[] { correction }),
            ("reordered time", reordered, new[] { reordered, first }),
            ("retry", retry, new[] { first, retry }),
            ("same time", simultaneous, new[] { simultaneous }),
            ("older revision", olderRevision, new[] { olderRevision, first }),
            ("next bucket", nextBucket, new[] { first, nextBucket }),
        };
        foreach (var (name, current, expected) in cases)
        {
            var inbox = new PersistenceInbox(new(), new());
            Guid runtime = Guid.NewGuid(), transfer = Guid.NewGuid();
            Assert.IsTrue(inbox.TryEnqueue(Transfer(runtime, transfer, 1) with
            {
                GroupRelativePath = "file.bin", AccountingObservations = [first],
            }));
            Assert.IsTrue(inbox.TryEnqueue(Transfer(runtime, transfer, 2) with
            {
                GroupRelativePath = null, AccountingObservations = [current],
            }));
            var admitted = (TransferPersistenceMutation)inbox.DrainBatch().Single();
            Assert.AreEqual(2L, admitted.Revision, name);
            Assert.AreEqual("file.bin", admitted.GroupRelativePath, name);
            CollectionAssert.AreEqual(expected, admitted.AccountingObservations!.ToArray(), name);
        }
    }

    [TestMethod]
    public void TransferTerminalAdmission_CarriesBufferedProgressWithoutReplacingRetainedAttempt()
    {
        var options = new PersistenceWriterOptions
        {
            CommandQueueCapacity = 1,
            MaximumBatchSize = 1,
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
        var attempt = new TransferAttemptPersistenceMutation(runtimeId, 2, DateTimeOffset.UtcNow,
            attemptId, 2, PersistenceMutationPriority.Terminal, transferId, 1, "SoulseekPeer", "Completed",
            "peer", "remote", "/output/file", "None", null);
        Assert.IsTrue(inbox.TryEnqueue(new TransferTerminalPersistenceMutation(terminalTransfer, attempt, null)));

        Assert.AreEqual(0, inbox.ProgressCount);
        var batch = inbox.DrainBatch(includeProgress: false);
        Assert.AreEqual(1, batch.Count);
        Assert.IsInstanceOfType<TransferTerminalPersistenceMutation>(batch[0]);
        var admitted = (TransferTerminalPersistenceMutation)batch[0];
        Assert.AreEqual(1, admitted.Transfer.AccountingObservations?.Count);
        Assert.AreEqual(50L, admitted.Transfer.AccountingObservations?.Single().CumulativeBytes);

        inbox.RetainForRetry(batch);
        var moved = terminalTransfer with { Sequence = 3, Revision = 3, LocalPath = "/output/moved" };
        Assert.IsTrue(inbox.TryEnqueue(new TransferTerminalPersistenceMutation(moved, null, null)));
        Assert.AreEqual(1, inbox.RetryMutationCount,
            "Later state must not consume the failed composite's attempt or accounting.");
        Assert.AreSame(admitted, inbox.DrainBatch(includeProgress: false).Single());
        Assert.AreSame(attempt, admitted.FinalAttempt);
        var latest = (TransferTerminalPersistenceMutation)inbox.DrainBatch(includeProgress: false).Single();
        Assert.AreEqual(moved, latest.Transfer);
    }


    [TestMethod]
    public void RetryDrain_PreservesEveryPacketAndCompletionBeforeNewerState()
    {
        // Larger than the former 1,024-entry projection cache, but deliberately
        // tiny packets: this exercises exact recovery, not ingestion throughput.
        const int packetCount = 1_025;
        var options = new PersistenceWriterOptions
        {
            MaximumBatchSize = 128,
            SearchResultFlushCount = 100,
        };
        var inbox = new PersistenceInbox(options, new PersistenceHealth());
        Guid runtimeId = Guid.NewGuid(), searchId = Guid.NewGuid();
        var packets = Enumerable.Range(1, packetCount).Select(sequence =>
            new SearchResultsPersistenceMutation(runtimeId, sequence + 1, DateTimeOffset.UtcNow,
                searchId, sequence, [Result(sequence)])).ToArray();
        var completion = new SearchCompletionPersistenceMutation(
            runtimeId, packetCount + 2, DateTimeOffset.UtcNow, searchId, packetCount + 1,
            "query", packetCount, 0, "Complete");
        var newer = Job(runtimeId, Guid.NewGuid(), packetCount + 3,
            PersistenceMutationPriority.Terminal, terminal: true);

        inbox.RetainForRetry([completion, .. packets.Reverse()]);
        Assert.IsTrue(inbox.TryEnqueue(newer));
        Assert.AreEqual(packetCount + 1, inbox.RetryMutationCount);
        var replay = new List<PersistenceMutation>();
        while (inbox.RetryMutationCount > 0)
        {
            var batch = inbox.DrainBatch();
            Assert.IsTrue(batch.Count > 0);
            Assert.IsTrue(batch.OfType<SearchResultsPersistenceMutation>().Sum(item => item.Results.Count) <= 100);
            replay.AddRange(batch);
        }
        replay.AddRange(inbox.DrainBatch());
        PersistenceMutation[] expected = [.. packets, completion, newer];
        CollectionAssert.AreEqual(expected, replay.ToArray());
    }

    [TestMethod]
    [DataRow(100)]
    [DataRow(200)]
    [DataRow(1_000)]
    [DataRow(3_000)]
    public void SearchAdmission_SplitsLargeResponseIntoBoundedOrderedBatchesAndRetainsRetryChunks(int flushCount)
    {
        var options = new PersistenceWriterOptions { SearchResultFlushCount = flushCount };
        int firstCount = Math.Min(flushCount, PersistenceWriterOptions.InitialSearchResultFlushCount);
        int resultCount = firstCount + flushCount + 50;
        var inbox = new PersistenceInbox(options, new PersistenceHealth());
        Guid runtimeId = Guid.NewGuid();
        Guid searchId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(new SearchResultsPersistenceMutation(
            runtimeId, 2, DateTimeOffset.UtcNow, searchId, 2,
            Enumerable.Range(1, resultCount).Select(value => Result(value)).ToArray())));
        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtimeId, 3, DateTimeOffset.UtcNow, searchId, 3,
            "query", resultCount, 0, "Complete")));

        var first = (SearchResultsPersistenceMutation)inbox.DrainBatch().Single();
        var second = (SearchResultsPersistenceMutation)inbox.DrainBatch().Single();
        var final = inbox.DrainBatch();
        Assert.AreEqual(firstCount, first.Results.Count);
        Assert.AreEqual(flushCount, second.Results.Count);
        Assert.AreEqual(50, ((SearchResultsPersistenceMutation)final[0]).Results.Count);
        Assert.IsInstanceOfType<SearchCompletionPersistenceMutation>(final[1]);
        CollectionAssert.AreEqual(Enumerable.Range(1, resultCount).Select(value => (long)value).ToArray(),
            first.Results.Concat(second.Results).Concat(((SearchResultsPersistenceMutation)final[0]).Results)
                .Select(row => row.Sequence).ToArray());

        inbox.RetainForRetry([first, second]);
        var replayFirst = (SearchResultsPersistenceMutation)inbox.DrainBatch().Single();
        var replaySecond = (SearchResultsPersistenceMutation)inbox.DrainBatch().Single();
        Assert.AreEqual(firstCount + flushCount, replayFirst.Results.Count + replaySecond.Results.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HistoryAdmission_NeverWaitsForWriterCapacity_AndPreservesFactsOnStop(bool critical)
    {
        var options = new PersistenceWriterOptions
        {
            CommandQueueCapacity = 1, BackgroundCommandQueueCapacity = 1, MaximumBatchSize = 1,
        };
        var health = new PersistenceHealth();
        var observer = new RecordingMutationObserver();
        var inbox = new PersistenceInbox(options, health, observer);
        Guid runtimeId = Guid.NewGuid();
        PersistenceMutation first = critical
            ? Job(runtimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural)
            : Job(runtimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Ordinary) with
            {
                DirectoryManifest = new(1, new("Folder", [], 0), false, [], []),
            };
        PersistenceMutation second = critical
            ? Job(runtimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Terminal, terminal: true)
            : ((JobPersistenceMutation)first) with
            {
                Revision = 2,
                DirectoryManifest = new(2, new("Folder", [], 0), false, [], []),
            };
        Assert.IsTrue(inbox.TryEnqueue(first));
        ValueTask<bool> admission = inbox.EnqueueAsync(second);
        try
        {
            Assert.IsTrue(admission.IsCompletedSuccessfully,
                "Notifications about admitted work cannot park execution inside a synchronous event callback.");
            Assert.IsTrue(await admission);
            inbox.Complete();
            Assert.AreSame(first, inbox.DrainBatch().Single());
            Assert.AreSame(second, inbox.DrainBatch().Single(), "Stop must preserve complete, ordered manifests and terminal facts.");
            Assert.IsFalse(inbox.TryEnqueue(first));
            CollectionAssert.AreEqual(new[] { first }, observer.Failed.ToArray());
            Assert.AreEqual(0, inbox.ActiveAdmissionCount);
        }
        finally
        {
            inbox.Complete();
            if (!admission.IsCompleted) await admission.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task TerminalMutations_PreserveActualJobAndAttemptStartTimesWhenRowsAreMissing()
    {
        await using var database = new WriterDatabase();
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
            PrintOption = (int)(Sockseek.Core.PrintOption.Results | Sockseek.Core.PrintOption.Full),
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
        Assert.AreEqual(job.PrintOption, (await new JobHistoryReader(database.Factory).GetJobAsync(jobId))!.PrintOption);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task TerminalTransfer_PreservesBufferedStateAccountingAcrossBuckets()
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("terminal-accounting-order")).Runtime;
        var options = new PersistenceWriterOptions { MaximumBatchSize = 1 };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        var firstBucket = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nextBucket = firstBucket.AddMinutes(5);
        var transfer = Transfer(runtime.RuntimeId, transferId, 1) with
        {
            Priority = PersistenceMutationPriority.Ordinary,
            OccurredAtUtc = firstBucket,
            TransferredBytes = 10,
            AccountingObservations = [new(attemptId, 1, firstBucket, 10)],
        };
        Assert.IsTrue(inbox.TryEnqueue(transfer));
        var terminal = transfer with
        {
            Revision = 3, Sequence = 3, OccurredAtUtc = nextBucket,
            Priority = PersistenceMutationPriority.Terminal,
            State = "Completed", TerminalOutcome = "Succeeded", TransferredBytes = 20,
            AccountingObservations = null,
        };
        var attempt = new TransferAttemptPersistenceMutation(
            runtime.RuntimeId, 2, nextBucket, attemptId, 2,
            PersistenceMutationPriority.Terminal, transferId, 1, "SoulseekPeer", "Completed",
            "user", "remote", "local", "None", null,
            AccountingObservations: [new(attemptId, 2, nextBucket, 20)]);
        Assert.IsTrue(inbox.TryEnqueue(new TransferTerminalPersistenceMutation(terminal, attempt, null)));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);

        await using var verify = await database.Factory.CreateDbContextAsync();
        var buckets = await verify.TransferByteBuckets.OrderBy(row => row.BucketStartUtc).ToArrayAsync();
        CollectionAssert.AreEqual(new long[] { 10, 10 }, buckets.Select(row => row.Bytes).ToArray(),
            "Terminal priority must not move earlier bytes into the terminal attempt's time bucket.");
        Assert.AreEqual(2L, (await verify.TransferAccountingCheckpoints.SingleAsync()).Revision);
        Assert.AreEqual("Completed", (await verify.Transfers.SingleAsync()).State);
        Assert.AreEqual(0L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
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
            GroupRelativePath = "Disc 1/Track.flac",
        };
        // Updating an existing transfer must survive the same transaction as
        // bulk inserts, even when the initial row lacked plan/progress metadata.
        await RunCompletedWriterAsync(database, [mutation with
        {
            Revision = 0, Sequence = 0, OccurredAtUtc = requested,
            Priority = PersistenceMutationPriority.Structural, State = "Queued", AttemptCount = 0,
            StartedAtUtc = null, LastProgressAtUtc = null, BytesPerSecond = null,
            File = null, GroupRef = null, GroupDisplayPath = null, GroupRelativePath = null,
        }]);
        Assert.IsTrue(inbox.TryEnqueue(mutation));
        // A later observation may omit immutable plan metadata. Coalescing must
        // retain the accepted member path before its first durable write.
        Assert.IsTrue(inbox.TryEnqueue(mutation with
        {
            Revision = 2,
            Sequence = mutation.Sequence + 1,
            GroupRelativePath = null,
        }));
        Guid[] newIds = [Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid id in newIds)
            Assert.IsTrue(inbox.TryEnqueue(Transfer(runtime.RuntimeId, id, revision: 2) with
            {
                Username = "Péér",
                RemotePath = @"Public\Ünicode\" + new string('x', 4_500) + ".bin",
                TotalBytes = -1,
                TransferredBytes = 4_000_000_000,
            }));
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
        Assert.AreEqual("Disc 1/Track.flac", detail.Transfer.GroupRelativePath);
        Assert.IsNull(detail.Transfer.RemovedAtUtc);
        Assert.AreEqual(0L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
        foreach (Guid id in newIds)
        {
            var second = await new TransferHistoryReader(database.Factory).GetTransferAsync(id);
            Assert.IsNotNull(second);
            Assert.AreEqual("Péér", second.Transfer.Username);
            Assert.AreEqual(@"Public\Ünicode\" + new string('x', 4_500) + ".bin", second.Transfer.RemotePath);
            Assert.AreEqual(4_000_000_000L, second.Transfer.TransferredBytes);
            Assert.IsNull(second.Transfer.TotalBytes);
            Assert.IsNull(second.Transfer.File);
            Assert.IsNull(second.Transfer.GroupRef);
            Assert.IsNull(second.Transfer.GroupDisplayPath);
            Assert.IsNull(second.Transfer.GroupRelativePath);
            Assert.IsNull(second.Transfer.BytesPerSecond);
        }
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task TransferAccounting_ReplaysRetriesAndCounterResetsIdempotently_AndQueriesBoundedSemantics()
    {
        await using var database = new WriterDatabase();
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
        var comparison = await new TransferAnalyticsReader(database.Factory).GetAsync(
            new TransferAnalyticsQuery(start, start.AddMinutes(30), TimeSpan.FromMinutes(5), IncludeRankings: false));
        Assert.AreEqual(30L, comparison.DownloadBytes);
        Assert.AreEqual(40L, comparison.UploadBytes);
        Assert.AreEqual(1, comparison.DownloadedFiles);
        Assert.AreEqual(1, comparison.UploadedFiles);
        Assert.AreEqual(2, comparison.DistinctPeers);
        CollectionAssert.AreEqual(analytics.Buckets.ToArray(), comparison.Buckets.ToArray());
        Assert.AreEqual(0, comparison.Peers.Count);
        Assert.AreEqual(0, comparison.Content.Count);
        Assert.AreEqual(0, comparison.Errors.Count);

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
        // A registration and composite terminal can touch the same previously
        // absent job in one transaction; both must use the newly tracked row.
        await RunCompletedWriterAsync(database,
        [
            validComposite.OwningJob! with { Revision = 1, Priority = PersistenceMutationPriority.Structural,
                LifecycleState = "Pending", TerminalOutcome = "None" },
            validComposite,
        ]);
        await RunCompletedWriterAsync(database, [validComposite]);

        await using (var verifyReplay = await database.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(1, await verifyReplay.Jobs.CountAsync(job => job.Id == owningJobId));
            Assert.AreEqual(1, await verifyReplay.Transfers.CountAsync(transfer => transfer.Id == transferId));
            Assert.AreEqual(1, await verifyReplay.TransferAttempts.CountAsync(attempt => attempt.Id == attemptId));
            var owningJob = await verifyReplay.Jobs.SingleAsync(job => job.Id == owningJobId);
            Assert.AreEqual(2L, owningJob.Revision);
            Assert.AreEqual("Terminal", owningJob.LifecycleState);
        }
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task SearchCompletion_RepresentsGenuineZeroResults_AndCannotPromoteIncompleteHistory()
    {
        await using var database = new WriterDatabase();
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
    [TestCategory("CI")]
    [DoNotParallelize]
    [DataRow(10, false)]
    [DataRow(100, false)]
    [DataRow(10_000, false)]
    [DataRow(10, true)]
    public async Task LargeSearchCompletion_BoundsTransactionsWithoutLosingRows(int packetSize, bool interleavedPublication)
    {
        const int resultCount = 20_001;
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("large-search-batch-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        var options = new PersistenceWriterOptions
        {
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        BatchClock? publicationClock = interleavedPublication ? new() : null;
        int lastPublishedCount = 0;
        var publications = new List<Task<int>>();
        var sourceCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<FileAttributeSnapshot> attributes = [
            new("BitRate", 320, 0), new("Length", 180, 1),
            new("SampleRate", 44_100, 4), new("BitDepth", 24, 5),
        ];
        var results = Enumerable.Range(1, resultCount)
            .Select(value => Result(value) with { Attributes = attributes }).ToArray();
        // A pending search burst must yield to an unrelated terminal update.
        Guid urgentJobId = Guid.Parse("e27fb843-7b66-4c01-b5c0-52fd0d79b214");
        bool urgentQueued = false;
        var urgentAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rowsWhenUrgentCommitted = -1;
        Exception? observationFailure = null;
        health.CommitCompleted += () =>
        {
            try
            {
                if (!urgentQueued)
                {
                    using var context = database.Factory.CreateDbContext();
                    if (context.SearchResults.Any(row => row.SearchJobId == searchId))
                    {
                        urgentQueued = inbox.TryEnqueue(Job(runtime.RuntimeId, urgentJobId, 30_000,
                            PersistenceMutationPriority.Structural, terminal: true));
                        if (urgentQueued) urgentAdmission.TrySetResult();
                    }
                }
                else if (rowsWhenUrgentCommitted < 0)
                {
                    using var context = database.Factory.CreateDbContext();
                    if (context.Jobs.Any(row => row.Id == urgentJobId))
                        rowsWhenUrgentCommitted = context.SearchResults.Count(row => row.SearchJobId == searchId);
                }
                if (publicationClock != null)
                {
                    using var context = database.Factory.CreateDbContext();
                    int count = context.SearchResults.Count(row => row.SearchJobId == searchId);
                    if (count == resultCount) sourceCommitted.TrySetResult();
                    if (count > lastPublishedCount && count < resultCount)
                    {
                        lastPublishedCount = count;
                        var publication = new AwaitablePersistenceCommand<int>((_, _) =>
                        {
                            publicationClock.Advance(TimeSpan.FromSeconds(1));
                            return Task.FromResult(1);
                        });
                        var admission = inbox.EnqueueCommandAsync(publication, CancellationToken.None, background: true);
                        Assert.IsTrue(admission.IsCompletedSuccessfully);
                        admission.GetAwaiter().GetResult();
                        publications.Add(publication.Task);
                    }
                }
            }
            catch (Exception exception)
            {
                observationFailure = exception;
                urgentAdmission.TrySetException(exception);
                sourceCommitted.TrySetException(exception);
                inbox.Complete();
            }
        };

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var writeTime = Stopwatch.StartNew();
        Task writing = new PersistenceWriter(database.Factory, inbox, health, options, timeProvider: publicationClock)
            .RunAsync(CancellationToken.None);
        try
        {
            Assert.IsTrue(await inbox.EnqueueAsync(Job(runtime.RuntimeId, searchId, 1,
                PersistenceMutationPriority.Structural, kind: "Search")));
            long sequence = 2;
            foreach (var packet in results.Chunk(packetSize))
                Assert.IsTrue(await inbox.EnqueueAsync(new SearchResultsPersistenceMutation(
                    runtime.RuntimeId, sequence++, DateTimeOffset.UtcNow, searchId, packet[^1].Revision, packet)));
            Assert.IsTrue(await inbox.EnqueueAsync(new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, sequence, DateTimeOffset.UtcNow, searchId, resultCount + 1,
                "large", resultCount, 0, "Complete")));
            await urgentAdmission.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (interleavedPublication) await sourceCommitted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            inbox.Complete();
            await writing;
        }
        writeTime.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"Collect {resultCount} raw files in {packetSize}-file packets, no result View"
            + (interleavedPublication ? ", publication between batches: " : ": ")
            + $"{writeTime.ElapsedMilliseconds} ms, {allocated / 1048576.0:F1} MiB; "
            + $"commits {health.Snapshot(inbox).SuccessfulCommitCount}.");
        Assert.IsTrue(allocated < 24 * 1024 * 1024,
            $"Raw collection allocated {allocated} bytes; response boundaries must not restore per-file preparation copies.");

        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.AreEqual(resultCount, await verify.SearchResults.CountAsync(row => row.SearchJobId == searchId));
        Assert.AreEqual("Complete", (await verify.SearchJobs.SingleAsync(row => row.JobId == searchId)).ResultPersistenceState);
        long commits = health.Snapshot(inbox).SuccessfulCommitCount;
        Assert.IsNull(observationFailure);
        Assert.IsTrue(urgentQueued);
        if (interleavedPublication)
        {
            Assert.IsTrue(publications.Count > 0);
            Assert.IsTrue(publications.All(task => task.IsCompletedSuccessfully));
        }
        Assert.AreEqual("Succeeded", (await verify.Jobs.SingleAsync(row => row.Id == urgentJobId)).TerminalOutcome);
        Assert.IsTrue(rowsWhenUrgentCommitted > 0 && rowsWhenUrgentCommitted < resultCount,
            $"An urgent terminal update must commit before the search finishes (rows then: {rowsWhenUrgentCommitted}).");
        Assert.IsTrue(commits <= 6,
            $"A queued {resultCount}-result search must fill file batches across peer packets, including an urgent unrelated update (commits: {commits}).");
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchPackets_ShareTransactionWithoutSharingFailureOwnership(bool malformedAttributes)
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("search-packet-isolation")).Runtime;
        Guid source = Guid.NewGuid(), other = Guid.NewGuid();
        SearchProjectionInput[] Packet(int first, string peer)
            => Enumerable.Range(first, 100).Select(i => Result(i) with { Username = peer }).ToArray();
        var first = Packet(1, "first");
        var bad = Packet(101, "rejected");
        bad[^1] = malformedAttributes
            ? bad[^1] with { Attributes = [null!] }
            : bad[^1] with { Revision = 0 };
        var last = Packet(201, "last");
        var independent = Packet(1, "independent");
        var outcome = await RunCompletedWriterAsync(database,
        [
            Job(runtime.RuntimeId, source, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            Job(runtime.RuntimeId, other, 2, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 3, DateTimeOffset.UtcNow, source, 100, first),
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 4, DateTimeOffset.UtcNow, source, 200, bad),
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 5, DateTimeOffset.UtcNow, other, 100, independent),
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 6, DateTimeOffset.UtcNow, source, 300, last),
        ]);
        Assert.AreEqual(1L, outcome.PermanentlyFailedMutationCount);
        var history = new SearchHistoryReader(database.Factory);
        var main = (await history.GetRawResultsAsync(source, 0, 500))!;
        CollectionAssert.AreEqual(first.Concat(last).Select(i => i.Sequence).ToArray(), main.Items.Select(i => i.Sequence).ToArray());
        Assert.AreEqual(200L, main.Metadata.ResultCount);
        Assert.IsFalse(main.Items.Any(i => i.Username == "rejected"));
        var sibling = (await history.GetRawResultsAsync(other, 0, 500))!;
        Assert.AreEqual(100L, sibling.Metadata.ResultCount);
        Assert.IsTrue(sibling.Items.All(i => i.Username == "independent"));
        // A later transaction must resolve its own source/statement state after
        // the failed transaction was discarded and its valid siblings replayed.
        await RunCompletedWriterAsync(database,
        [new SearchResultsPersistenceMutation(runtime.RuntimeId, 7, DateTimeOffset.UtcNow, source, 301, [Result(301)])]);
        Assert.AreEqual(201L, (await history.GetMetadataAsync(source))!.ResultCount);
        await session.StopAsync();
    }

    [TestMethod]
    public async Task SearchDuplicatePolicy_FirstSequenceAndFirstPeerPathWinConsistently()
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("duplicate-search-test")).Runtime;
        Guid searchId = Guid.Parse("d5725228-4563-4fbe-b586-2fa28d99ab95");
        var first = Result(1);
        var duplicatePeerPath = first with { Sequence = 2, Revision = 2, Size = 999 };
        var duplicateSequence = Result(1) with
        {
            Username = "another-peer",
            Filename = "another.mp3",
            Size = 888,
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
        Assert.AreEqual(first.Sequence, stored.Sequence);
        Assert.AreEqual(first.Username, stored.Username);
        Assert.AreEqual(first.Filename, stored.RemoteFilename);
        Assert.AreEqual(first.Size, stored.SizeBytes);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task SearchDuplicatePolicy_AcrossTransactionsPreservesVisibilityAndNullableMetadata()
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("search-duplicate-transactions-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        var publicFile = Result(1) with { Filename = "same.mp3" };
        var lockedFile = Result(2) with { Filename = "same.mp3", Visibility = SearchResultVisibility.Locked };
        await RunCompletedWriterAsync(database,
        [
            Job(runtime.RuntimeId, searchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 2, DateTimeOffset.UtcNow,
                searchId, 2, [publicFile, lockedFile]),
        ]);

        var duplicateSequence = Result(1) with { Username = "other-peer", Filename = "other.mp3" };
        var duplicateCandidate = Result(3) with { Filename = "same.mp3", Size = 999 };
        var validAfterRejectedSequence = duplicateSequence with { Sequence = 5 };
        var nullableFile = Result(4) with
        {
            Filename = "nullable.mp3", BitRate = null, BitDepth = null,
            SampleRate = null, Length = null, UploadSpeed = null,
            HasFreeUploadSlot = null, QueueLength = null, Attributes = null,
        };
        await RunCompletedWriterAsync(database,
        [
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 3, DateTimeOffset.UtcNow,
                searchId, 3, [duplicateSequence, .. Enumerable.Repeat(duplicateCandidate, 300),
                    nullableFile, validAfterRejectedSequence]),
        ]);

        await using var verify = await database.Factory.CreateDbContextAsync();
        var stored = await verify.SearchResults.AsNoTracking()
            .Where(row => row.SearchJobId == searchId)
            .OrderBy(row => row.Sequence).ToListAsync();
        CollectionAssert.AreEqual(new long[] { 1, 2, 4, 5 }, stored.Select(row => row.Sequence).ToArray());
        Assert.AreEqual("Public", stored[0].Visibility);
        Assert.AreEqual("Locked", stored[1].Visibility);
        Assert.IsNull(stored[2].BitRate);
        Assert.IsNull(stored[2].HasFreeUploadSlot);
        Assert.AreEqual(validAfterRejectedSequence.Filename, stored[3].RemoteFilename,
            "A rejected sequence collision cannot reserve its peer/path against a later valid row.");
        var search = await verify.SearchJobs.SingleAsync(row => row.JobId == searchId);
        Assert.AreEqual(3L, search.ResultCount);
        Assert.AreEqual(1L, search.LockedFileCount);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    [DataRow(2, 0)]
    [DataRow(1_002, 0)]
    [DataRow(1_002, 1)]
    [DataRow(1_002, 2)]
    public async Task SearchResultInsert_FailingLaterRowRollsBackEarlierRowsInItsBatch(int batchSize, int invalidKind)
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("search-insert-rollback-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        await RunCompletedWriterAsync(database,
            [Job(runtime.RuntimeId, searchId, 1, PersistenceMutationPriority.Structural, kind: "Search")]);

        var good = Result(1);
        int retainedCount = invalidKind == 0 ? 0 : 1;
        if (retainedCount != 0)
            await RunCompletedWriterAsync(database,
            [
                new SearchResultsPersistenceMutation(runtime.RuntimeId, 1, DateTimeOffset.UtcNow,
                    searchId, 1, [good]),
            ]);
        // Replay must still validate constraints before ignoring a collision.
        // The larger case crosses a SQL chunk before rejecting its last row.
        var invalid = invalidKind switch
        {
            1 => good with { Revision = 0 },
            2 => good with { Extension = null! },
            _ => Result(0),
        };
        var rows = Enumerable.Range(1 + retainedCount, batchSize - 1)
            .Select(i => Result(i) with { Filename = $"rollback-{i}.mp3" })
            .Append(invalid).ToArray();
        PersistenceHealthSnapshot failure = await RunCompletedWriterAsync(database,
        [
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 2, DateTimeOffset.UtcNow,
                searchId, 2, rows),
        ]);
        Assert.AreEqual(1L, failure.PermanentlyFailedMutationCount);
        await using (var verify = await database.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(retainedCount, await verify.SearchResults.CountAsync(row => row.SearchJobId == searchId));
            Assert.AreEqual((long)retainedCount, (await verify.SearchJobs.SingleAsync(row => row.JobId == searchId)).ResultCount);
        }

        await RunCompletedWriterAsync(database,
        [
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 3, DateTimeOffset.UtcNow,
                searchId, 3, [Result(1 + retainedCount)]),
        ]);
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.AreEqual(1 + retainedCount, await verify.SearchResults.CountAsync(row => row.SearchJobId == searchId));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public void SearchRowsDrainBeforeCompletionWithoutLoss()
    {
        var options = new PersistenceWriterOptions
        {
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
    }

    [TestMethod]
    public async Task Writer_CoalescesByRevision_AndCannotRegressTerminalJobs_WithBoundedBatchReads()
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("writer-test")).Runtime;
        var jobs = Enumerable.Range(1, 8).Select(index =>
            Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural)
                with { DisplayId = index, PayloadJson = new(() => "{\"Value\":\"original\"}"),
                    ItemName = $"Álbum {index} \"quoted\" \\ 音楽",
                    SourceDetail = index % 2 == 0 ? null : "extractor: café / 音楽",
                    QueryText = index % 2 == 0 ? "" : "a\tb\nc",
                    SemanticRole = "PlannedWork" }).ToArray();
        // Exercise both existing and absent rows in the same batch.
        await RunCompletedWriterAsync(database, jobs.Take(4).ToArray());
        await using var connectionContext = await database.Factory.CreateDbContextAsync();
        // Count actual row updates, including prepared SQL outside EF's command
        // interceptor. New inserts must not be rewritten just to sync tracking.
        await connectionContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE probe_job_updates (count INTEGER NOT NULL);
            INSERT INTO probe_job_updates VALUES (0);
            CREATE TRIGGER probe_job_update AFTER UPDATE ON jobs BEGIN
                UPDATE probe_job_updates SET count = count + 1;
            END;
            """);
        var reads = new SelectCountInterceptor();
        var contextOptions = new DbContextOptionsBuilder<SockseekDbContext>()
            .UseSqlite(connectionContext.Database.GetConnectionString()).AddInterceptors(reads).Options;
        var factory = new SockseekDbContextFactory(contextOptions);

        await WriteBatchAsync(jobs.SelectMany(job => new[]
        {
            job,
            job with { Revision = 2, Priority = PersistenceMutationPriority.Terminal,
                LifecycleState = "Terminal", TerminalOutcome = "Cancelled", CancellationSource = "UserRequestedJob",
                PayloadJson = job.DisplayId % 2 == 0 ? null : new(() => "{\"Value\":\"terminal\"}") },
        }).ToArray(), expectedUpdates: 4);
        await VerifyTerminalRowsAsync();
        // Reject both an old revision and a newer attempt to resurrect a terminal row.
        await WriteBatchAsync(jobs.SelectMany(job => new[] { job, job with { Revision = 3 } }).ToArray(), expectedUpdates: 0);
        await VerifyTerminalRowsAsync();
        await runtimeSession.StopAsync();

        async Task WriteBatchAsync(IReadOnlyList<JobPersistenceMutation> mutations, int expectedUpdates)
        {
            var options = new PersistenceWriterOptions();
            var health = new PersistenceHealth();
            var inbox = new PersistenceInbox(options, health);
            foreach (var mutation in mutations)
                Assert.IsTrue(inbox.TryEnqueue(mutation));
            inbox.Complete();
            reads.Count = 0;
            reads.JobInserts = 0;
            await connectionContext.Database.ExecuteSqlRawAsync("UPDATE probe_job_updates SET count = 0");
            await new PersistenceWriter(factory, inbox, health, options).RunAsync(CancellationToken.None);
            Assert.AreEqual(0L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
            Assert.IsTrue(reads.Count <= 2,
                $"An eight-job batch must not perform a separate database read per job (actual: {reads.Count}).");
            Assert.IsTrue(reads.JobInserts <= 1,
                $"New jobs must share one INSERT (actual: {reads.JobInserts}).");
            int updatedRows = await connectionContext.Database
                .SqlQueryRaw<int>("SELECT count AS Value FROM probe_job_updates").SingleAsync();
            Assert.AreEqual(expectedUpdates, updatedRows,
                "Newly inserted rows must not be updated again just to synchronize EF snapshots.");
        }

        async Task VerifyTerminalRowsAsync()
        {
            await using var context = await database.Factory.CreateDbContextAsync();
            var persisted = await context.Jobs.AsNoTracking().ToListAsync();
            Assert.AreEqual(jobs.Length, persisted.Count);
            foreach (var job in jobs)
            {
                var row = persisted.Single(row => row.Id == job.JobId);
                Assert.AreEqual(2L, row.Revision);
                Assert.AreEqual("Terminal", row.LifecycleState);
                Assert.AreEqual("Cancelled", row.TerminalOutcome);
                Assert.AreEqual("UserRequestedJob", row.CancellationSource);
                Assert.AreEqual(job.ItemName, row.ItemName);
                Assert.AreEqual(job.SourceDetail, row.SourceDetail);
                Assert.AreEqual(job.QueryText, row.QueryText);
                Assert.AreEqual(job.SemanticRole, row.SemanticRole);
                Assert.IsNotNull(row.CompletedAtUtc);
                Assert.AreEqual(row.DisplayId % 2 == 0 ? null : "{\"Value\":\"terminal\"}", row.PayloadJson);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize] // This case gates its own checkpoint Activity, not other writers' diagnostics.
    [DataRow(false)]
    [DataRow(true)]
    public async Task Checkpoint_DoesNotHoldWriterQueue_AndPreservesPinnedReaders(bool pinReader)
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("checkpoint-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Task writing = new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.CommitCompleted += () => committed.TrySetResult();
        var job = Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural);
        Assert.IsTrue(inbox.TryEnqueue(job));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var source = await database.Factory.CreateDbContextAsync();
        await source.Database.OpenConnectionAsync();
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)source.Database.GetDbConnection();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = Channel.CreateUnbounded<(int Frames, int Copied)>();
        int starts = 0;
        using var activities = new ActivityListener
        {
            ShouldListenTo = activity => activity.Name == "Sockseek.Persistence.Write",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "checkpoint" || Interlocked.Increment(ref starts) != 1) return;
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Checkpoint gate was not released.");
            },
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "checkpoint" && activity.GetTagItem("checkpoint.outcome") is not null)
                    completed.Writer.TryWrite((Convert.ToInt32(activity.GetTagItem("checkpoint.frames")),
                        Convert.ToInt32(activity.GetTagItem("checkpoint.copied"))));
            },
        };
        ActivitySource.AddActivityListener(activities);
        Exception? failure = null;
        await using var checkpoint = new SqliteCheckpoint(connection,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, exception => failure = exception);
        SqliteReadSnapshot? pinned = null;
        try
        {
            checkpoint.RequestIfNeeded();
            Assert.IsNull(failure, "Inspecting the WAL must not manufacture a storage failure.");
            if (pinReader)
            {
                pinned = await SqliteReadSnapshot.BeginAsync(source, CancellationToken.None);
                Assert.AreEqual(1L, (await source.Jobs.AsNoTracking().SingleAsync()).Revision);
            }
            checkpoint.Request();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(inbox.TryEnqueue(job with { Revision = 2, Sequence = 2 }));
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2L, (await new JobHistoryReader(database.Factory).GetJobAsync(job.JobId))!.Revision);
            if (!pinReader)
            {
                Task stopping = checkpoint.DisposeAsync().AsTask();
                Assert.IsFalse(stopping.IsCompleted, "Shutdown must join the copy before closing its owner.");
                release.Set();
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                release.Set();
                var partial = await completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(partial.Copied < partial.Frames, "The older reader must keep its snapshot.");
                Assert.AreEqual(1L, (await source.Jobs.AsNoTracking().SingleAsync()).Revision);
                await pinned!.DisposeAsync();
                pinned = null;
                checkpoint.Request();
                var finished = await completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(finished.Frames, finished.Copied);
            }
            Assert.IsNull(failure);
        }
        finally
        {
            release.Set();
            if (pinned is not null) await pinned.DisposeAsync();
            await checkpoint.DisposeAsync();
            inbox.Complete();
            await writing;
            await session.StopAsync();
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Checkpoint_RetriesAfterItsStorageBecomesAvailable()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-checkpoint-test-").FullName;
        string missing = Path.Combine(directory, "missing");
        await using var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(missing, "test.db")};Pooling=False");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int failures = 0;
        using var activities = new ActivityListener
        {
            ShouldListenTo = activity => activity.Name == "Sockseek.Persistence.Write",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "checkpoint" && activity.GetTagItem("checkpoint.outcome") is "Complete")
                    succeeded.TrySetResult();
            },
        };
        ActivitySource.AddActivityListener(activities);
        var checkpoint = new SqliteCheckpoint(source, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            _ => { Interlocked.Increment(ref failures); failed.TrySetResult(); });
        try
        {
            checkpoint.Request();
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(missing);
            await source.OpenAsync();
            await using var setup = source.CreateCommand();
            setup.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE sample (id INTEGER); INSERT INTO sample VALUES (1);";
            await setup.ExecuteNonQueryAsync();
            checkpoint.Request();
            await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, failures);
            setup.CommandText = "SELECT COUNT(*) FROM sample;";
            Assert.AreEqual(1L, await setup.ExecuteScalarAsync());
        }
        finally
        {
            await checkpoint.DisposeAsync();
            await source.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
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
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("search-writer-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.CommitCompleted += () => committed.TrySetResult();
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        using var stop = new CancellationTokenSource();
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

        // Admit the complete batch before starting its reader: the assertion is
        // about one completion barrier, not whichever concurrent commit happens first.
        var runTask = writer.RunAsync(stop.Token);
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
        var neutral = historical.Result;
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
        var newest = await reader.GetJobsAsync(new JobHistoryQuery(Limit: 2, NewestFirst: true));
        Assert.IsNotNull(newest.NextCursor);
        var older = await reader.GetJobsAsync(new JobHistoryQuery(Cursor: newest.NextCursor, Limit: 2, NewestFirst: true));
        CollectionAssert.AreEqual(ids.Reverse().Take(2).ToArray(), newest.Items.Select(job => job.Id).ToArray());
        CollectionAssert.AreEqual(ids.Take(1).ToArray(), older.Items.Select(job => job.Id).ToArray());
        Assert.IsNull(older.NextCursor);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetJobsAsync(
            new JobHistoryQuery(Cursor: newest.NextCursor, Limit: 2)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetJobsAsync(
            new JobHistoryQuery(Cursor: first.NextCursor, Limit: 2, NewestFirst: true)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetWorkflowsAsync(newest.NextCursor));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetJobsAsync(new JobHistoryQuery(Cursor: "bad", Limit: 2)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetJobsAsync(new JobHistoryQuery(
            Cursor: first.NextCursor + new string(' ', 128), Limit: 2)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => reader.GetJobsAsync(new JobHistoryQuery(Limit: 0)));
        CollectionAssert.AreEqual(ids, (await reader.GetJobsAsync(new JobHistoryQuery(Limit: int.MaxValue)))
            .Items.Select(job => job.Id).ToArray());

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
        var first = await reader.GetTransfersAsync(new TransferQuery(Limit: 2, WorkflowId: workflowId));
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
        var second = await reader.GetTransfersAsync(new TransferQuery(first.NextCursor, 2, WorkflowId: workflowId));
        CollectionAssert.AreEqual(ids.Reverse().Take(2).ToArray(), first.Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(ids.Take(1).ToArray(), second.Items.Select(item => item.Id).ToArray());
        Assert.IsNull(second.NextCursor);
        Assert.IsFalse(second.Items.Any(item => item.Id == concurrentlyInsertedId),
            "A row inserted above the moving boundary must not enter an existing traversal.");
        Assert.AreEqual(concurrentlyInsertedId, (await reader.GetTransfersAsync(new TransferQuery(
            Limit: 1, WorkflowId: workflowId))).Items.Single().Id);
        Assert.AreEqual(1, (await reader.GetTransfersAsync(new TransferQuery(
            Limit: 10, WorkflowId: workflowId, Source: "YtDlpFallback"))).Items.Count);
        Assert.AreEqual(4, (await reader.GetTransfersAsync(new TransferQuery(
            Limit: 10, WorkflowId: workflowId, Direction: "download"))).Items.Count);
        Assert.AreEqual(ids[2], (await reader.GetTransfersAsync(new TransferQuery(
            Limit: 10, Username: "special-user", TerminalOutcome: "Succeeded"))).Items.Single().Id);
        var lookup = await reader.GetTransfersAsync(new TransferQuery(
            Limit: 10, TransferIds: [ids[0], ids[0], ids[2], Guid.NewGuid()]));
        CollectionAssert.AreEqual(new[] { ids[2], ids[0] }, lookup.Items.Select(row => row.Id).ToArray());
        Assert.IsNull(lookup.NextCursor);
        Assert.AreEqual(0, (await reader.GetTransfersAsync(new TransferQuery(TransferIds: []))).Items.Count);
        Guid[] requestedIds = [ids[0], ids[0], ids[2], Guid.NewGuid()];
        var lookupFirst = await reader.GetTransfersAsync(new TransferQuery(
            Limit: 1, WorkflowId: workflowId, Direction: "download", Source: "SoulseekPeer",
            TerminalOutcome: "Succeeded", TransferIds: requestedIds));
        Assert.AreEqual(ids[2], lookupFirst.Items.Single().Id);
        Assert.IsNotNull(lookupFirst.NextCursor);
        var lookupSecond = await reader.GetTransfersAsync(new TransferQuery(
            lookupFirst.NextCursor, 1, WorkflowId: workflowId, Direction: "download",
            Source: "SoulseekPeer", TerminalOutcome: "Succeeded", TransferIds: requestedIds));
        Assert.AreEqual(ids[0], lookupSecond.Items.Single().Id);
        Assert.IsNull(lookupSecond.NextCursor);
        Assert.AreEqual(ids[2], (await reader.GetTransfersAsync(new TransferQuery(
            TransferIds: requestedIds, Username: "special-user"))).Items.Single().Id);
        Assert.AreEqual(0, (await reader.GetTransfersAsync(new TransferQuery(
            TransferIds: requestedIds, Username: "SPECIAL-USER"))).Items.Count);
        Assert.AreEqual(0, (await reader.GetTransfersAsync(new TransferQuery(
            TransferIds: requestedIds, WorkflowId: Guid.NewGuid()))).Items.Count);

        var excludingNewest = new TransferQuery(Limit: 1, WorkflowId: workflowId,
            LiveCandidates: [new(concurrentlyInsertedId, 2, 0, false), new(ids[2], 2, 0, false)]);
        var filteredFirst = await reader.GetTransfersAsync(excludingNewest);
        Assert.AreEqual(ids[1], filteredFirst.Items.Single().Id,
            "Excluding live predecessors must happen before the page limit.");
        Assert.IsNotNull(filteredFirst.NextCursor);
        var filteredSecond = await reader.GetTransfersAsync(excludingNewest with { Cursor = filteredFirst.NextCursor });
        Assert.AreEqual(ids[0], filteredSecond.Items.Single().Id);
        Assert.IsNull(filteredSecond.NextCursor);
        var filteredLookup = await reader.GetTransfersAsync(new TransferQuery(
            Limit: 1, TransferIds: requestedIds, LiveCandidates: [new(ids[2], 2, 0, false)]));
        Assert.AreEqual(ids[0], filteredLookup.Items.Single().Id);
        Assert.IsNull(filteredLookup.NextCursor);
        var newerLookup = await reader.GetTransfersAsync(new TransferQuery(
            TransferIds: [ids[2]], LiveCandidates: [new(ids[2], 1, 0, false)]));
        Assert.AreEqual(ids[2], newerLookup.Items.Single().Id,
            "A newer retained revision is not superseded by an older live snapshot.");
        var matchingLive = await reader.GetTransfersAsync(new TransferQuery(
            State: "InProgress", LiveCandidates: [new(ids[2], 1, 0, true)]));
        Assert.AreEqual(concurrentlyInsertedId, matchingLive.Items.Single().Id);
        CollectionAssert.AreEqual(new[] { concurrentlyInsertedId }, matchingLive.TransferIds.ToArray(),
            "A newer nonmatching retained successor must exclude its older matching live copy before paging.");
        var nullOwner = await reader.GetTransfersAsync(new TransferQuery(
            JobId: Guid.NewGuid(), TransferIds: requestedIds,
            LiveCandidates: [new(ids[0], 1, 0, true), new(ids[2], 1, 0, true)]));
        Assert.AreEqual(0, nullOwner.Items.Count);
        Assert.AreEqual(0, nullOwner.TransferIds.Count,
            "A retained NULL owner cannot satisfy a requested owner or resurrect its older live copy.");
        Guid sourceOnlyId = Guid.NewGuid();
        var sourceOnly = await reader.GetTransfersAsync(new TransferQuery(
            FromUtc: DateTimeOffset.FromUnixTimeMilliseconds(102),
            ToUtc: DateTimeOffset.FromUnixTimeMilliseconds(102),
            LiveCandidates: [new(sourceOnlyId, 1, 102, true)]));
        Assert.AreEqual(0, sourceOnly.Items.Count);
        CollectionAssert.AreEqual(new[] { sourceOnlyId }, sourceOnly.TransferIds.ToArray(),
            "A live-only transfer keeps its page identity without fabricated retained metadata.");

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

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetTransfersAsync(new TransferQuery("bad", 2)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => reader.GetTransfersAsync(new TransferQuery(
            first.NextCursor + new string(' ', 129), 2)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => reader.GetAttemptsAsync(ids[0], 0, 0));
        CollectionAssert.AreEqual(Enumerable.Range(1, 225).ToArray(),
            (await reader.GetAttemptsAsync(ids[0], 0, int.MaxValue))!.Items.Select(item => item.AttemptNumber).ToArray());
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task TransferGroups_PreserveExactKeysAndUnknownSizeAccounting()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("group-accounting-test")).Runtime;
        long sequence = 0;
        const string folder = @"Root\é/δ";
        TransferEntity Row(string? user, string group, string direction, string state, string outcome,
            long total, long bytes, long? size = null, long? speed = null, bool removed = false) => new()
        {
            Id = Guid.NewGuid(), LastRuntimeId = runtime.RuntimeId, LastSequence = ++sequence,
            Direction = direction, Source = "SoulseekPeer", Username = user, GroupRef = group,
            State = state, TerminalOutcome = outcome, TotalBytes = total, TransferredBytes = bytes,
            FileSizeBytes = size, BytesPerSecond = speed, CreatedAtUtc = 0,
            CompletedAtUtc = outcome == "None" ? null : 1,
            RemovedAtUtc = removed ? 1 : null, FailureReason = "None", Revision = 1,
        };
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Transfers.AddRange(
                Row(null, folder, "Download", "Queued, Remotely", "None", long.MaxValue, 0, speed: 4),
                Row(null, folder, "Download", "Queued", "None", long.MaxValue, 0, size: 0),
                Row(null, folder, "Download", "InProgress", "None", long.MaxValue, 64, size: 200, speed: 10),
                Row(null, folder, "Download", "InProgress", "None", 100, 120, speed: 20),
                Row(null, folder, "Download", "Completed", "Succeeded", 5_000_000_000, 5_000_000_000, speed: 999),
                Row(null, folder, "Download", "Failed", "Failed", long.MaxValue, 2),
                Row(null, folder, "Download", "Cancelled", "Cancelled", long.MaxValue, 10, size: 56),
                Row(null, folder, "Download", "Interrupted", "Interrupted", 0, 0),
                Row(null, folder, "Download", "Completed", "Succeeded", 999, 999, removed: true),
                Row("null", folder, "Download", "Completed", "Succeeded", 10, 10),
                Row("Peer", folder, "Download", "Completed", "Succeeded", 20, 20),
                Row("peer", folder, "Download", "Completed", "Succeeded", 30, 30),
                Row(null, folder, "Upload", "Completed", "Succeeded", 40, 40),
                Row(null, "different", "Download", "Completed", "Succeeded", 50, 50),
                Row(null, "unknown", "Download", "Queued", "None", long.MaxValue, 0));
            await context.SaveChangesAsync();
        }
        var queued = await new TransferHistoryReader(database.Factory).GetTransfersAsync(
            new(Direction: "Download", State: "Queued", GroupRef: folder));
        CollectionAssert.AreEquivalent(new[] { "Queued", "Queued, Remotely" },
            queued.Items.Select(row => row.State).ToArray());
        var key = new TransferGroupKey("Download", null, folder);
        var projection = await new TransferHistoryReader(database.Factory).GetGroupProjectionAsync(
            [key, key, new("Download", "null", folder), new("Download", "peer", folder),
                new("Download", "Peer", "different"), new("Download", null, "unknown")], []);
        Assert.AreEqual(4, projection.Summaries.Count);
        Assert.AreEqual(new PersistedTransferGroupSummary(key, 8, 5_000_000_356, 5_000_000_196,
            34, 2, 136, 1, 2, 2, 1, 1, 1, 1), projection.Summaries.Single(group => group.Key == key));
        Assert.AreEqual(10, projection.Summaries.Single(group => group.Key.Username == "null").KnownTotalBytes);
        Assert.AreEqual(30, projection.Summaries.Single(group => group.Key.Username == "peer").KnownTotalBytes);
        var terminalKey = new TransferGroupKey("Download", "null", folder);
        Assert.AreEqual(new PersistedTransferGroupSummary(terminalKey, 1, 10, 10,
            0, 0, 0, 0, 0, 0, 1, 0, 0, 0), projection.Summaries.Single(group => group.Key == terminalKey));
        var unknownKey = new TransferGroupKey("Download", null, "unknown");
        Assert.AreEqual(new PersistedTransferGroupSummary(unknownKey, 1, 0, 0,
            0, 1, 0, 1, 1, 0, 0, 0, 0, 0), projection.Summaries.Single(group => group.Key == unknownKey));
        await session.StopAsync();
    }

    [TestMethod]
    public async Task TransferRemovalPreservesHistoryAndRejectsNonterminalRows()
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("transfer-removal-test")).Runtime;
        Guid terminalId = Guid.NewGuid();
        Guid activeId = Guid.NewGuid();
        Guid otherGroupId = Guid.NewGuid();
        Guid otherPeerId = Guid.NewGuid();
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
                    GroupRef = "folder",
                    State = "Failed",
                    TerminalOutcome = "Failed",
                    TotalBytes = 100,
                    TransferredBytes = 20,
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
                    GroupRef = "folder",
                    State = "InProgress",
                    TerminalOutcome = "None",
                    TotalBytes = 100,
                    TransferredBytes = 50,
                    AttemptCount = 1,
                    CreatedAtUtc = 101,
                    StartedAtUtc = 101,
                    FailureReason = "None",
                    Revision = 1,
                },
                new TransferEntity
                {
                    Id = otherGroupId,
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = 3,
                    Direction = "Upload",
                    Source = "SoulseekPeer",
                    Username = "peer",
                    GroupRef = "other-folder",
                    State = "Completed",
                    TerminalOutcome = "Succeeded",
                    TotalBytes = 100,
                    TransferredBytes = 100,
                    CreatedAtUtc = 102,
                    CompletedAtUtc = 200,
                    FailureReason = "None",
                    Revision = 1,
                },
                new TransferEntity
                {
                    Id = otherPeerId,
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = 4,
                    Direction = "Upload",
                    Source = "SoulseekPeer",
                    Username = "other-peer",
                    GroupRef = "folder",
                    State = "Completed",
                    TerminalOutcome = "Succeeded",
                    TotalBytes = 100,
                    TransferredBytes = 100,
                    CreatedAtUtc = 103,
                    CompletedAtUtc = 200,
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

        var folderKey = new TransferGroupKey("Upload", "peer", "folder");
        var projection = await reader.GetGroupProjectionAsync([
            folderKey,
            new TransferGroupKey("Upload", "peer", "other-folder"),
            new TransferGroupKey("Upload", "other-peer", "folder"),
        ], [terminalId, otherGroupId, Guid.NewGuid()]);
        var summaries = projection.Summaries;
        Assert.AreEqual(3, summaries.Count);
        var folderSummary = summaries.Single(summary => summary.Key == folderKey);
        Assert.AreEqual(2, folderSummary.FileCount);
        Assert.AreEqual(200, folderSummary.KnownTotalBytes);
        Assert.AreEqual(70, folderSummary.TransferredBytes);
        Assert.AreEqual(50, folderSummary.KnownRemainingActiveBytes);
        Assert.AreEqual(0, folderSummary.UnknownActiveTotalCount);
        Assert.AreEqual(1, folderSummary.InProgressCount);
        Assert.AreEqual(1, folderSummary.FailedCount);
        CollectionAssert.AreEquivalent(
            new[] { terminalId, otherGroupId },
            projection.OverlappingTransfers
                .Select(transfer => transfer.Id).ToArray());

        TransferRemovalResult removed = await reader.RemoveAsync(
            new TransferRemovalFilter(Direction: "Upload", Username: "peer", GroupRef: "folder"));
        Assert.AreEqual(2, removed.ResolvedCount);
        Assert.AreEqual(1, removed.ChangedCount);
        Assert.AreEqual(1, removed.RejectedCount);
        Assert.AreEqual(1, removed.Reasons["nonterminal"]);
        Assert.AreEqual(1, (await reader.GetGroupProjectionAsync([folderKey], [])).Summaries.Single().FileCount);
        CollectionAssert.AreEquivalent(
            new[] { activeId, otherGroupId, otherPeerId },
            (await reader.GetTransfersAsync(new TransferQuery(Limit: 10)))
                .Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { terminalId, activeId, otherGroupId, otherPeerId },
            (await reader.GetTransfersAsync(new TransferQuery(Limit: 10, IncludeRemoved: true))).Items.Select(row => row.Id).ToArray());
        Guid[] requestedIds = [terminalId, terminalId, activeId, otherGroupId, Guid.NewGuid()];
        CollectionAssert.AreEquivalent(new[] { activeId, otherGroupId },
            (await reader.GetTransfersAsync(new(TransferIds: requestedIds))).Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { terminalId, activeId, otherGroupId },
            (await reader.GetTransfersAsync(new(TransferIds: requestedIds, IncludeRemoved: true))).Items.Select(row => row.Id).ToArray());

        TransferRemovalResult missing = await reader.RemoveAsync(
            new TransferRemovalFilter(TransferId: Guid.NewGuid()));
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
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("busy-recovery-test")).Runtime;
        var options = new PersistenceWriterOptions
        {
            BusyRetryCount = 0,
            FailureRetryDelay = TimeSpan.FromMilliseconds(25),
        };
        var health = new PersistenceHealth();
        var observer = new RecordingMutationObserver();
        var inbox = new PersistenceInbox(options, health, observer);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.FailureRecorded += () => failure.TrySetResult();
        health.CommitCompleted += () => committed.TrySetResult();

        await using var lockingContext = await database.Factory.CreateDbContextAsync();
        await lockingContext.Database.OpenConnectionAsync();
        await using var lockCommand = lockingContext.Database.GetDbConnection().CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE;";
        await lockCommand.ExecuteNonQueryAsync();

        Guid[] jobIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (var jobId in jobIds)
            Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural)));
        // All accepted entries share the first failed transaction. Recovery must
        // retain every entry without an evicting intermediate projection.
        var writer = new PersistenceWriter(database.Factory, inbox, health, options, mutationObserver: observer);
        var runTask = writer.RunAsync(CancellationToken.None);
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
            CollectionAssert.AreEquivalent(jobIds, await verify.Jobs.Select(job => job.Id).ToArrayAsync());
        Assert.AreEqual(0, observer.Failed.Count);
        Assert.AreEqual(3, observer.CommittedMutations.Count);
        Assert.AreEqual(0L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
        Assert.AreEqual(PersistenceHealthState.Healthy, health.Snapshot(inbox).State);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_StopsRetryingRecoverableFailure_AfterConfiguredRecoveryLimit()
    {
        await using var database = new WriterDatabase();
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
    [DataRow("apply")]
    [DataRow("save")]
    [DataRow("composite")]
    [DataRow("composite-overwrite")]
    [DataRow("composite-merged")]
    [DataRow("search-flush")]
    [DataRow("checkpoint")]
    [DataRow("bucket")]
    [DataRow("bucket-ignored")]
    [DataRow("overflow")]
    public async Task Writer_IsolatesPermanentMutationWithinTheSameBatch(string failureStage)
    {
        await using var database = new WriterDatabase();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("permanent-failure-test")).Runtime;
        var options = new PersistenceWriterOptions { FailureRetryDelay = TimeSpan.FromMilliseconds(25) };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var outcomes = new RecordingMutationObserver();
        Guid badJobId = Guid.NewGuid();
        Guid badTransferId = Guid.NewGuid();
        Guid badAttemptId = Guid.NewGuid();
        Guid validTransferId = Guid.NewGuid();
        Guid validAttemptId = Guid.NewGuid();
        Guid parentId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        bool overwrite = failureStage == "composite-overwrite";
        bool merged = failureStage == "composite-merged";
        bool searchFlush = failureStage == "search-flush";
        bool badCheckpoint = failureStage == "checkpoint";
        bool badBucket = failureStage is "bucket" or "bucket-ignored";
        bool badOverflow = failureStage == "overflow";
        var observedAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (badOverflow)
        {
            await using var setup = await database.Factory.CreateDbContextAsync();
            setup.TransferByteBuckets.Add(new() { BucketStartUtc = observedAt.ToUnixTimeMilliseconds(),
                Direction = "Download", Username = "overflow", Bytes = long.MaxValue - 25 });
            await setup.SaveChangesAsync();
        }
        if (badBucket)
        {
            await using var setup = await database.Factory.CreateDbContextAsync();
            string rejection = failureStage == "bucket-ignored" ? "RAISE(IGNORE)" : "RAISE(ABORT, 'rejected accounting peer')";
            string trigger = $"""
                CREATE TRIGGER reject_accounting_peer BEFORE INSERT ON transfer_byte_buckets
                WHEN NEW.username = 'rejected'
                BEGIN SELECT {rejection}; END;
                """;
            await setup.Database.ExecuteSqlRawAsync(trigger);
        }
        await RunCompletedWriterAsync(database,
            [Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural) with { DisplayId = 700 }]);
        var badJob = Job(runtime.RuntimeId, badJobId, 2, PersistenceMutationPriority.Terminal, terminal: true)
            with { DisplayId = failureStage == "composite" || merged || badCheckpoint || badBucket || badOverflow ? 704 : 700 };
        var badTransfer = Transfer(runtime.RuntimeId, badTransferId, 2) with
        {
            JobId = badJobId, State = "Completed", TerminalOutcome = "Succeeded",
            Username = badBucket ? "rejected" : badOverflow ? "overflow" : "user",
            Priority = PersistenceMutationPriority.Terminal,
            AccountingObservations = [new(badAttemptId, badCheckpoint ? -1 : 2, observedAt, 50)],
        };
        PersistenceMutation bad = failureStage switch
        {
            "apply" => new SearchCompletionPersistenceMutation(runtime.RuntimeId, 1, DateTimeOffset.UtcNow,
                badJobId, 1, "missing job", 0, 0, "Complete"),
            "save" or "search-flush" => badJob,
            _ => new TransferTerminalPersistenceMutation(badTransfer,
                new TransferAttemptPersistenceMutation(runtime.RuntimeId, 2, DateTimeOffset.UtcNow, badAttemptId, 2,
                    PersistenceMutationPriority.Terminal, badTransferId, failureStage == "composite" || merged ? 0 : 1,
                    "SoulseekPeer", "Completed", "peer", "remote", "local", "None", null), badJob),
        };
        // All inputs precede writer start, so the bad entry cannot land in a
        // different transaction. Forward references require the healthy graph
        // to remain together (a one-at-a-time retry would reject the child).
        var parent = Job(runtime.RuntimeId, parentId, 2, PersistenceMutationPriority.Structural, kind: "Extract")
            with { DisplayId = 701, ResultJobId = resultId };
        var child = Job(runtime.RuntimeId, childId, 1, PersistenceMutationPriority.Structural)
            with { DisplayId = 702, WorkflowId = parent.WorkflowId, ParentJobId = parentId,
                SourceJobId = overwrite ? badJobId : resultId };
        var result = Job(runtime.RuntimeId, resultId, 3, PersistenceMutationPriority.Structural, kind: searchFlush ? "Search" : "JobList")
            with { DisplayId = 703, WorkflowId = parent.WorkflowId };
        var validComposite = new TransferTerminalPersistenceMutation(
            Transfer(runtime.RuntimeId, validTransferId, 5) with
            {
                JobId = childId, State = "Completed", TerminalOutcome = "Succeeded",
                Priority = PersistenceMutationPriority.Terminal,
                TransferredBytes = 25,
                AccountingObservations = [new(validAttemptId, 5, observedAt, 25)],
            },
            new TransferAttemptPersistenceMutation(runtime.RuntimeId, 5, DateTimeOffset.UtcNow, validAttemptId, 5,
                PersistenceMutationPriority.Terminal, validTransferId, 1,
                "SoulseekPeer", "Completed", "Péér", "remote", "local", "None", null), null);
        Assert.IsTrue(inbox.TryEnqueue(validComposite));
        if (overwrite)
            Assert.IsTrue(inbox.TryEnqueue(badJob with
            {
                Revision = 1, Sequence = 1, DisplayId = 704, LifecycleState = "Pending", TerminalOutcome = "None",
                Priority = PersistenceMutationPriority.Structural,
            }));
        foreach (var mutation in new PersistenceMutation[] { child, bad, parent, result })
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        if (merged)
            Assert.IsTrue(inbox.TryEnqueue(badTransfer with
            {
                Revision = 1, Sequence = 1, Priority = PersistenceMutationPriority.Structural,
                State = "Started", TerminalOutcome = "None", AccountingObservations = null,
            }));
        if (searchFlush)
            Assert.IsTrue(inbox.TryEnqueue(new SearchResultsPersistenceMutation(runtime.RuntimeId, 4, DateTimeOffset.UtcNow,
                resultId, 1, [Result(1)])));
        inbox.Complete();
        var writeElapsed = Stopwatch.StartNew();
        await new PersistenceWriter(database.Factory, inbox, health, options, mutationObserver: outcomes)
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"Mixed-batch isolation ({failureStage}): {writeElapsed.Elapsed.TotalMilliseconds:F1} ms.");
        var snapshot = health.Snapshot(inbox);
        Assert.AreEqual(merged ? 2L : 1L, snapshot.PermanentlyFailedMutationCount);
        Assert.AreEqual(PersistenceHealthState.Degraded, snapshot.State,
            "The loss must remain visible without disabling reads of independently committed rows.");
        await using (var verify = await database.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(parentId, (await verify.Jobs.SingleAsync(job => job.Id == childId)).ParentJobId);
            Assert.AreEqual(resultId, (await verify.Jobs.SingleAsync(job => job.Id == parentId)).ResultJobId);
            Assert.IsTrue(await verify.Jobs.AnyAsync(job => job.Id == resultId));
            Assert.AreEqual(overwrite ? 5 : 4, await verify.Jobs.CountAsync());
            Assert.AreEqual(overwrite, await verify.Jobs.AnyAsync(job => job.Id == badJobId));
            if (overwrite)
            {
                var registration = await verify.Jobs.SingleAsync(job => job.Id == badJobId);
                Assert.AreEqual(1L, registration.Revision);
                Assert.AreEqual(704L, registration.DisplayId);
            }
            Assert.AreEqual(validTransferId, (await verify.Transfers.SingleAsync()).Id);
            Assert.AreEqual(validAttemptId, (await verify.TransferAttempts.SingleAsync()).Id);
            Assert.AreEqual("Péér", (await verify.TransferAttempts.SingleAsync()).SourceUsername);
            Assert.AreEqual(validAttemptId, (await verify.TransferAccountingCheckpoints.SingleAsync()).AttemptId);
            Assert.AreEqual(25L, await verify.TransferByteBuckets
                .Where(bucket => !badOverflow || bucket.Username != "overflow").SumAsync(bucket => bucket.Bytes));
            if (badOverflow)
                Assert.AreEqual(long.MaxValue - 25, await verify.TransferByteBuckets
                    .Where(bucket => bucket.Username == "overflow").Select(bucket => bucket.Bytes).SingleAsync());
            Assert.AreEqual(searchFlush ? 1 : 0, await verify.SearchResults.CountAsync());
        }
        CollectionAssert.AreEqual(merged ? new[] { bad.EntityId, bad.EntityId } : new[] { bad.EntityId },
            outcomes.Failed.Select(mutation => mutation.EntityId).ToArray());
        Assert.IsTrue(outcomes.CommittedMutations.Any(mutation => mutation.EntityId == parentId));
        Assert.IsTrue(outcomes.CommittedMutations.Any(mutation => mutation.EntityId == childId));
        Assert.IsFalse(outcomes.CommittedMutations.Contains(bad));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task RejectedJobMutationDoesNotDiscardValidCompositeWithTheSameRevision()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("same-revision-owner-test")).Runtime;
        await RunCompletedWriterAsync(database,
            [Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural) with { DisplayId = 700 }]);
        Guid jobId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        var bad = Job(runtime.RuntimeId, jobId, 2, PersistenceMutationPriority.Terminal, terminal: true)
            with { DisplayId = 700 };
        var valid = new TransferTerminalPersistenceMutation(
            Transfer(runtime.RuntimeId, transferId, 2) with
            {
                JobId = jobId, State = "Completed", TerminalOutcome = "Succeeded",
                Priority = PersistenceMutationPriority.Terminal,
            }, null, bad with { DisplayId = 701 });
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var outcomes = new RecordingMutationObserver();
        var inbox = new PersistenceInbox(options, health);
        Assert.IsTrue(inbox.TryEnqueue(bad));
        Assert.IsTrue(inbox.TryEnqueue(valid));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options, mutationObserver: outcomes)
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.AreEqual(701L, (await verify.Jobs.SingleAsync(job => job.Id == jobId)).DisplayId);
        Assert.AreEqual("Succeeded", (await verify.Transfers.SingleAsync(transfer => transfer.Id == transferId)).TerminalOutcome);
        CollectionAssert.AreEqual(new PersistenceMutation[] { bad }, outcomes.Failed.ToArray());
        Assert.IsTrue(outcomes.CommittedMutations.Contains(valid));
        await session.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OutputRelocationPreservesCompletionTimeWhenTerminalSnapshotsCoalesce(bool coalesced)
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("moved-output-time-test")).Runtime;
        var completedAt = DateTimeOffset.Parse("2026-10-31T12:00:05Z");
        Guid jobId = Guid.NewGuid(), transferId = Guid.NewGuid();
        var job = Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural);
        var completed = Transfer(runtime.RuntimeId, transferId, 2) with
        {
            JobId = jobId, Priority = PersistenceMutationPriority.Terminal, State = "Completed",
            TerminalOutcome = "Succeeded", TransferredBytes = 100, LocalPath = "/output/Track.bin",
            OccurredAtUtc = completedAt, CompletedAtUtc = completedAt,
        };
        var moved = completed with
        {
            Sequence = 3, Revision = 3, OccurredAtUtc = completedAt.AddMinutes(1), LocalPath = "/output/failed/Track.bin",
        };
        Guid attemptId = Guid.NewGuid();
        var attempt = new TransferAttemptPersistenceMutation(runtime.RuntimeId, 2, completedAt,
            attemptId, 2, PersistenceMutationPriority.Terminal, transferId, 1, "SoulseekPeer", "Completed",
            "peer", "remote", completed.LocalPath, "None", null);
        var owningCompletion = job with
        {
            Sequence = 2, Revision = 2, Priority = PersistenceMutationPriority.Terminal,
            LifecycleState = "Terminal", TerminalOutcome = "Succeeded",
        };
        var completion = new TransferTerminalPersistenceMutation(completed, attempt, owningCompletion);
        if (coalesced) await RunCompletedWriterAsync(database,
            [job, completion, new TransferTerminalPersistenceMutation(moved, null, null)]);
        else
        {
            await RunCompletedWriterAsync(database, [job, completion]);
            await RunCompletedWriterAsync(database, [moved]);
        }
        await using var context = await database.Factory.CreateDbContextAsync();
        var retained = await context.Transfers.SingleAsync(row => row.Id == transferId);
        Assert.AreEqual(moved.LocalPath, retained.LocalPath);
        Assert.AreEqual(completedAt.ToUnixTimeMilliseconds(), retained.CompletedAtUtc);
        Assert.AreEqual("Succeeded", retained.TerminalOutcome);
        Assert.AreEqual(100L, retained.TransferredBytes);
        var retainedAttempt = await context.TransferAttempts.SingleAsync(row => row.Id == attemptId);
        Assert.AreEqual("Completed", retainedAttempt.State);
        Assert.AreEqual(completedAt.ToUnixTimeMilliseconds(), retainedAttempt.CompletedAtUtc);
        var retainedJob = await context.Jobs.SingleAsync(row => row.Id == jobId);
        Assert.AreEqual(2L, retainedJob.Revision);
        Assert.AreEqual("Succeeded", retainedJob.TerminalOutcome);
        await session.StopAsync();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task RejectedCompletionIsReportedAfterValidRawSiblingsSettle(int recoverableFailures)
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("search-outcome-order-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        await RunCompletedWriterAsync(database,
        [
            Job(runtime.RuntimeId, searchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchResultsPersistenceMutation(runtime.RuntimeId, 2, DateTimeOffset.UtcNow, searchId, 1, [Result(1)]),
        ]);
        await using var setup = await database.Factory.CreateDbContextAsync();
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_search_completion BEFORE UPDATE OF is_complete ON search_jobs
            WHEN NEW.is_complete=1 BEGIN SELECT RAISE(ABORT, 'Injected completion rejection'); END;
            """);
        var reads = new FailOnceReaderInterceptor();
        var factory = new SockseekDbContextFactory(new DbContextOptionsBuilder<SockseekDbContext>(
            SockseekDbContextOptions.Create(new SockseekSqliteOptions(setup.Database.GetDbConnection().DataSource)))
            .AddInterceptors(reads).Options);
        var options = new PersistenceWriterOptions
        { FailureRetryDelay = TimeSpan.FromMilliseconds(1), MaximumRecoveryAttempts = 2 };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        bool armed = false;
        var reportedLossCounts = new List<long>();
        health.FailureRecorded += () =>
        {
            reportedLossCounts.Add(health.Snapshot(inbox).PermanentlyFailedMutationCount);
            if (!armed) { reads.FailuresRemaining = recoverableFailures; armed = true; }
        };
        var rowsAtFailure = new List<int>();
        var observer = new RecordingMutationObserver
        {
            OnFailure = () =>
            {
                using var context = database.Factory.CreateDbContext();
                rowsAtFailure.Add(context.SearchResults.Count(result => result.SearchJobId == searchId));
            },
        };
        var rejected = new SearchCompletionPersistenceMutation(runtime.RuntimeId, 4, DateTimeOffset.UtcNow,
            searchId, 3, "search", 2, 0, "Complete");
        Assert.IsTrue(inbox.TryEnqueue(new SearchResultsPersistenceMutation(runtime.RuntimeId,
            3, DateTimeOffset.UtcNow, searchId, 2, [Result(2)])));
        Assert.IsTrue(inbox.TryEnqueue(rejected));
        inbox.Complete();
        await new PersistenceWriter(factory, inbox, health, options, mutationObserver: observer)
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { recoverableFailures < 2 ? 2 : 1 }, rowsAtFailure,
            "Report source loss after siblings commit or exhaust recovery, never while their outcome is pending.");
        Assert.IsTrue(observer.Failed.Contains(rejected));
        Assert.AreEqual(recoverableFailures < 2 ? 1 : 2, observer.Failed.Count);
        Assert.AreEqual((long)observer.Failed.Count, health.Snapshot(inbox).PermanentlyFailedMutationCount);
        Assert.AreEqual((long)observer.Failed.Count, reportedLossCounts.Last(),
            "Health readers must observe final loss without waiting for an unrelated commit.");
        Assert.AreEqual(recoverableFailures, reads.Failures);
        await session.StopAsync();
    }

    [TestMethod]
    public async Task CoalescedWake_DoesNotParkQueuedMutationsAfterDequeuingACommand()
    {
        foreach (var priority in new[] { PersistenceMutationPriority.Structural, PersistenceMutationPriority.Ordinary })
        {
            var inbox = new PersistenceInbox(new(), new());
            Assert.IsTrue(inbox.TryEnqueue(Job(Guid.NewGuid(), Guid.NewGuid(), 1, priority)));
            var command = new AwaitablePersistenceCommand<bool>((_, _) => Task.FromResult(true));
            await inbox.EnqueueCommandAsync(command, CancellationToken.None);
            await inbox.WaitForWorkAsync(CancellationToken.None);
            Assert.IsTrue(inbox.TryDequeueCommand(out _));
            using var stop = new CancellationTokenSource();
            Task ready = inbox.WaitForWorkAsync(stop.Token);
            try
            {
                Assert.IsTrue(ready.IsCompletedSuccessfully,
                    "A command can consume the coalesced wake, but admitted mutations must remain immediately runnable.");
            }
            finally
            {
                stop.Cancel();
                try { await ready; } catch (OperationCanceledException) { }
                inbox.Complete();
            }
        }

        var commandInbox = new PersistenceInbox(new(), new());
        await commandInbox.EnqueueCommandAsync(new AwaitablePersistenceCommand<bool>((_, _) => Task.FromResult(true)), CancellationToken.None);
        await commandInbox.EnqueueCommandAsync(new AwaitablePersistenceCommand<bool>((_, _) => Task.FromResult(true)), CancellationToken.None);
        await commandInbox.WaitForWorkAsync(CancellationToken.None);
        Assert.IsTrue(commandInbox.TryDequeueCommand(out _));
        // A fairness turn can consume the remaining wake without dequeuing a
        // command. The next turn must still see the admitted command as ready.
        await commandInbox.WaitForWorkAsync(CancellationToken.None);
        using var commandStop = new CancellationTokenSource();
        Task commandReady = commandInbox.WaitForWorkAsync(commandStop.Token);
        try
        {
            Assert.IsTrue(commandReady.IsCompletedSuccessfully,
                "A queued command must not wait for the search flush timer after a coalesced wake is consumed.");
            Assert.IsTrue(commandInbox.TryDequeueCommand(out _));
        }
        finally
        {
            commandStop.Cancel();
            try { await commandReady; } catch (OperationCanceledException) { }
            commandInbox.Complete();
        }
    }

    [TestMethod]
    public async Task BufferedProgressAlone_DoesNotMakeTheWriterBusySpin()
    {
        var inbox = new PersistenceInbox(new(), new());
        Assert.IsTrue(inbox.TryEnqueue(Transfer(Guid.NewGuid(), Guid.NewGuid(), 1)));
        await inbox.WaitForWorkAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task waiting = inbox.WaitForWorkAsync(stop.Token);
        try
        {
            Assert.IsFalse(waiting.IsCompleted, "Buffered progress must retain its normal flush cadence.");
        }
        finally
        {
            stop.Cancel();
            try { await waiting; } catch (OperationCanceledException) { }
            inbox.Complete();
        }
    }

    [TestMethod]
    [DoNotParallelize] // Admission is gated by this writer's preparation Activity.
    public async Task Writer_ServesCommandsArrivingDuringEncodingBeforeLoadingComparisonFacts()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("encoding-admission-test")).Runtime;
        var job = Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural, kind: "Search");
        await RunCompletedWriterAsync(database, [job]);
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var commands = new List<Task<int>>();
        Task<int>? failed = null;
        Task<int>? cancelled = null;
        using var cancellation = new CancellationTokenSource();
        int admitted = 0;
        int completedAtHistoryCommit = -1;
        long revisionAtHistoryCommit = -1;
        using var activities = new ActivityListener
        {
            ShouldListenTo = activity => activity.Name == "Sockseek.Persistence.Write",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "prepare-search" || Interlocked.Exchange(ref admitted, 1) != 0) return;
                failed = inbox.ExecuteAsync<int>((_, _) => throw new InvalidOperationException("Rejected command"),
                    CancellationToken.None);
                cancelled = inbox.ExecuteAsync<int>((_, _) => throw new AssertFailedException("Cancelled command ran"),
                    cancellation.Token);
                cancellation.Cancel();
                for (int i = 0; i < 40; i++)
                {
                    long revision = i + 3;
                    commands.Add(inbox.ExecuteAsync(async (context, ct) =>
                        await context.Jobs.Where(row => row.Id == job.JobId).ExecuteUpdateAsync(
                            update => update.SetProperty(row => row.Revision, revision), ct), CancellationToken.None));
                }
                inbox.Complete();
            },
        };
        ActivitySource.AddActivityListener(activities);
        var observer = new RecordingMutationObserver
        {
            OnCommit = () =>
            {
                completedAtHistoryCommit = commands.Count(command => command.IsCompletedSuccessfully);
                using var context = database.Factory.CreateDbContext();
                revisionAtHistoryCommit = context.Jobs.Single(row => row.Id == job.JobId).Revision;
            },
        };
        Assert.IsTrue(inbox.TryEnqueue(job with { Revision = 2, Sequence = 2 }));
        Assert.IsTrue(inbox.TryEnqueue(new SearchResultsPersistenceMutation(runtime.RuntimeId, 3,
            DateTimeOffset.UtcNow, job.JobId, 1, [Result(1)])));
        await new PersistenceWriter(database.Factory, inbox, health, options, mutationObserver: observer)
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNotNull(failed);
        Assert.IsNotNull(cancelled);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failed);
        await AssertCanceledAsync(cancelled);
        await Task.WhenAll(commands);
        Assert.IsTrue(completedAtHistoryCommit > 0 && completedAtHistoryCommit < commands.Count,
            "Interactive admission must yield back to history during a sustained burst.");
        Assert.AreEqual(completedAtHistoryCommit + 2L, revisionAtHistoryCommit,
            "History must compare against the command's committed revision, not overwrite it with stale loaded facts.");
        Assert.AreEqual(1L, (await new SearchHistoryReader(database.Factory).GetMetadataAsync(job.JobId))!.ResultCount);
        Assert.AreEqual(PersistenceHealthState.Healthy, health.Snapshot(inbox).State);
        await session.StopAsync();
    }

    [TestMethod]
    public async Task Writer_PrioritizesAcceptanceAfterCurrentBackgroundTransaction()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("late-acceptance-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task publication = inbox.ExecuteAsync(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, CancellationToken.None, background: true);
        Task writer = new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);
        await entered.Task;
        Guid historyJobId = Guid.NewGuid();
        Guid submissionId = Guid.NewGuid();
        var snapshots = new List<(bool Accepted, bool History)>();
        health.CommitCompleted += () =>
        {
            using var context = database.Factory.CreateDbContext();
            snapshots.Add((context.Submissions.Any(row => row.Id == submissionId),
                context.Jobs.Any(row => row.Id == historyJobId)));
        };
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, historyJobId, 1,
            PersistenceMutationPriority.Structural)));

        // The existing background transaction is held explicitly; queue both
        // history and acceptance before allowing its commit.
        string specification = SubmissionSpecificationCodec.Serialize(
            SubmissionSpecification.Create(new SearchJob("interactive"), new DownloadSettings()));
        Task accepted = new SubmissionStore(database.Factory, inbox).CreateAsync(
            new SubmissionRegistration(submissionId, DateTimeOffset.UtcNow,
                SubmissionSpecification.CurrentSchemaVersion, specification,
                null, null, null, "fingerprint", "{\"accepted\":true}"));
        inbox.Complete();
        release.SetResult();
        await Task.WhenAll(writer, accepted, publication).WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { (false, false), (true, false), (true, true) }, snapshots.ToArray(),
            "Acceptance queued during database contention must commit before unrelated history.");
        var receipt = await new SubmissionStore(database.Factory, inbox).GetCommitReceiptAsync(submissionId);
        Assert.AreEqual("fingerprint", receipt?.CommitFingerprint);
        Assert.AreEqual("{\"accepted\":true}", receipt?.CommitReceiptJson);
        Assert.AreEqual(PersistenceHealthState.Healthy, health.Snapshot(inbox).State);
        await session.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Writer_DrainsAwaitableCommandsBeyondFairnessBurstOnShutdown(bool retryWithinAttempt)
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("command-fairness-test")).Runtime;
        var options = new PersistenceWriterOptions
        {
            BusyRetryCount = retryWithinAttempt ? 1 : 0,
            BusyRetryDelay = TimeSpan.Zero,
            FailureRetryDelay = TimeSpan.FromMilliseconds(1),
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        int firstCommandAttempts = 0;
        AwaitablePersistenceCommand<int>[] commands = Enumerable.Range(1, 40)
            .Select(value => new AwaitablePersistenceCommand<int>(
                (_, _) =>
                {
                    if (value == 1 && Interlocked.Increment(ref firstCommandAttempts) == 1)
                        throw new Microsoft.Data.Sqlite.SqliteException("busy", 5);
                    return Task.FromResult(value);
                }))
            .ToArray();
        foreach (AwaitablePersistenceCommand<int> command in commands)
            await inbox.EnqueueCommandAsync(command, CancellationToken.None);
        int completedBeforeBackground = -1;
        var background = new AwaitablePersistenceCommand<int>((_, _) =>
        {
            completedBeforeBackground = commands.Count(command => command.Task.IsCompletedSuccessfully);
            return Task.FromResult(42);
        });
        await inbox.EnqueueCommandAsync(background, CancellationToken.None, background: true);
        Guid jobId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural)));
        int completedBeforeHistory = -1;
        var observer = new RecordingMutationObserver
        {
            OnCommit = () => completedBeforeHistory = commands.Count(command => command.Task.IsCompletedSuccessfully),
        };
        inbox.Complete();

        await new PersistenceWriter(database.Factory, inbox, health, options,
            mutationObserver: observer)
            .RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(
            Enumerable.Range(1, 40).ToArray(),
            await Task.WhenAll(commands.Select(command => command.Task)));
        Assert.AreEqual(0, inbox.CommandDepth);
        Assert.AreEqual(2, firstCommandAttempts);
        Assert.AreEqual(42, await background.Task);
        Assert.IsTrue(completedBeforeBackground > 0 && completedBeforeBackground < commands.Length,
            "Background publication must progress during sustained interactive traffic.");
        Assert.IsTrue(completedBeforeHistory > 0 && completedBeforeHistory < commands.Length,
            "A sustained command queue must not starve unrelated history.");
        Assert.AreEqual(PersistenceHealthState.Healthy, health.Snapshot(inbox).State);
        await session.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Writer_CancellationCompletesCommandAndCapacityWaiters(bool background)
    {
        await using var database = new WriterDatabase();
        var options = new PersistenceWriterOptions { CommandQueueCapacity = 1, BackgroundCommandQueueCapacity = 1 };
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
        await inbox.EnqueueCommandAsync(command, CancellationToken.None, background);
        using var stop = new CancellationTokenSource();
        Task writer = new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = new AwaitablePersistenceCommand<int>((_, _) => Task.FromResult(2));
        var waiting = new AwaitablePersistenceCommand<int>((_, _) => Task.FromResult(3));
        await inbox.EnqueueCommandAsync(queued, CancellationToken.None, background);
        Task pendingAdmission = inbox.EnqueueCommandAsync(waiting, CancellationToken.None, background);
        Assert.IsFalse(pendingAdmission.IsCompleted);

        stop.Cancel();
        try
        {
            await AssertCanceledAsync(writer.WaitAsync(TimeSpan.FromSeconds(5)));
            await AssertCanceledAsync(command.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await AssertCanceledAsync(queued.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await pendingAdmission.WaitAsync(TimeSpan.FromSeconds(5)),
                "Stopping the only reader must release asynchronous command capacity waiters.");
            Assert.AreEqual(0, inbox.ActiveAdmissionCount);
        }
        finally
        {
            inbox.Complete();
        }
    }

    [TestMethod]
    [TestCategory("Load")]
    [TestCategory("CI")]
    [DoNotParallelize]
    public async Task TenThousandEntryManifestPreservesPagedTargetsAndMaterializedChildren()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("manifest-load")).Runtime;
        Guid jobId = Guid.NewGuid();
        var entries = Enumerable.Range(0, 10_000).Select(index => new DirectoryTransferEntrySnapshot(
            new(new("Péér", $@"Music\曲\Track{index:D5}.flac"), index == 9999 ? 9_007_199_254_740_993L : index + 1,
                ".flac", 320, 24, 96000, 123, index == 9999 ? [new("Unknown", int.MaxValue, 65535)] : null),
            ["曲", $"Disc {index / 100}"])).ToArray();
        var plan = new DirectoryTransferPlanSnapshot("曲", entries, entries.Sum(entry => entry.Target.Size!.Value));
        var accepted = Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, kind: "RemoteDirectory") with
        {
            DirectoryManifest = new DirectoryManifestSnapshot(1, plan, false, [], []),
            PayloadJson = new(() => "{\"Directory\":{\"AttemptNumber\":1}}"),
        };
        var watch = Stopwatch.StartNew();
        Assert.AreEqual(0L, (await RunCompletedWriterAsync(database, [accepted])).PermanentlyFailedMutationCount);
        TimeSpan publication = watch.Elapsed;
        var reader = new JobHistoryReader(database.Factory);
        watch.Restart();
        var page = (await reader.GetDirectoryManifestAsync(jobId, 1, 8999, 500))!;
        TimeSpan pageTime = watch.Elapsed;
        Assert.AreEqual(10_000, page.EntryCount);
        Assert.AreEqual(500, page.Items.Count);
        Assert.AreEqual(9499, page.NextOrdinal);
        Assert.IsTrue(page.Items.All(item => item.ChildJobId == null));

        Guid[] children = Enumerable.Range(0, entries.Length).Select(_ => Guid.NewGuid()).ToArray();
        var materialized = accepted with
        {
            Sequence = 2, Revision = 2,
            DirectoryManifest = new DirectoryManifestSnapshot(1, plan, true, children, []),
        };
        watch.Restart();
        Assert.AreEqual(0L, (await RunCompletedWriterAsync(database, [materialized])).PermanentlyFailedMutationCount);
        TimeSpan mapping = watch.Elapsed;
        await RunCompletedWriterAsync(database, [accepted]);
        watch.Restart();
        var tail = (await reader.GetDirectoryManifestAsync(jobId, 1, 9499, 500))!;
        TimeSpan tailTime = watch.Elapsed;
        Assert.IsTrue(tail.ChildrenMaterialized);
        Assert.AreEqual(500, tail.Items.Count);
        Assert.IsNull(tail.NextOrdinal);
        foreach (var item in tail.Items)
        {
            Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(entries[item.Ordinal].Target),
                System.Text.Json.JsonSerializer.Serialize(item.Target));
            CollectionAssert.AreEqual(entries[item.Ordinal].RelativeDirectoryComponents.ToArray(), item.RelativeDirectoryComponents.ToArray());
            Assert.AreEqual(children[item.Ordinal], item.ChildJobId);
        }
        Console.WriteLine($"Manifest10k: publish={publication.TotalMilliseconds:F1} ms; child mapping={mapping.TotalMilliseconds:F1} ms; "
            + $"500-entry pages={pageTime.TotalMilliseconds:F1}/{tailTime.TotalMilliseconds:F1} ms.");
        Assert.IsTrue(mapping < TimeSpan.FromSeconds(2),
            $"Mapping10k children took {mapping.TotalMilliseconds:F1} ms; each ID must use an indexed lookup, not rescan the JSON list.");
        await session.StopAsync();
    }

    [TestMethod]
    [TestCategory("Load")]
    [TestCategory("CI")]
    [DoNotParallelize]
    public async Task AccountingBurstPreservesCounterResetsAndReplayWithoutDoubleCounting()
    {
        await using var database = new WriterDatabase();
        var session = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await session.StartAsync("accounting-load")).Runtime;
        DateTimeOffset start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mutations = Enumerable.Range(0, 256).Select(index =>
        {
            Guid attempt = Guid.NewGuid();
            return Transfer(runtime.RuntimeId, Guid.NewGuid(), 101) with
            {
                Priority = PersistenceMutationPriority.Terminal, State = "Completed", TerminalOutcome = "Succeeded",
                Direction = index % 2 == 0 ? "Download" : "Upload", Username = $"Péér{index}",
                OccurredAtUtc = start.AddMinutes(11), RequestedAtUtc = start, TransferredBytes = 50 * 1024,
                AccountingObservations = Enumerable.Range(1, 100).Select(revision =>
                    new TransferAccountingObservation(attempt, revision, start.AddSeconds(revision * 6),
                        ((revision - 1) % 50 + 1) * 1024)).ToArray(),
            };
        }).ToArray();
        await using var context = await database.Factory.CreateDbContextAsync();
        var reads = new SelectCountInterceptor();
        var factory = new SockseekDbContextFactory(new DbContextOptionsBuilder<SockseekDbContext>(
            SockseekDbContextOptions.Create(new SockseekSqliteOptions(context.Database.GetDbConnection().DataSource)))
            .AddInterceptors(reads).Options);
        async Task WriteAsync()
        {
            var options = new PersistenceWriterOptions();
            var health = new PersistenceHealth();
            var inbox = new PersistenceInbox(options, health);
            foreach (var mutation in mutations) Assert.IsTrue(inbox.TryEnqueue(mutation));
            inbox.Complete();
            await new PersistenceWriter(factory, inbox, health, options).RunAsync(CancellationToken.None);
            Assert.AreEqual(0L, health.Snapshot(inbox).PermanentlyFailedMutationCount);
        }
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        await WriteAsync();
        TimeSpan publish = watch.Elapsed;
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        int firstReads = reads.Count;
        reads.Count = 0;
        watch.Restart();
        await WriteAsync();
        TimeSpan replay = watch.Elapsed;
        int replayReads = reads.Count;
        var query = new TransferAnalyticsQuery(start, start.AddMinutes(15), TimeSpan.FromMinutes(5));
        watch.Restart();
        var totals = await new TransferAnalyticsReader(database.Factory).GetAsync(query);
        TimeSpan analytics = watch.Elapsed;
        Assert.AreEqual(128L * 100 * 1024, totals.DownloadBytes);
        Assert.AreEqual(totals.DownloadBytes, totals.UploadBytes);
        Assert.AreEqual(128, totals.DownloadedFiles);
        Assert.AreEqual(128, totals.UploadedFiles);
        Assert.AreEqual(256, await context.TransferAccountingCheckpoints.CountAsync());
        Assert.AreEqual(256 * 3, await context.TransferByteBuckets.CountAsync());
        // Each peer owns its counter resets and three time buckets, including
        // after replay. A many-peer batch must not collapse totals across users.
        Assert.AreEqual(100L * 1024, await context.TransferByteBuckets
            .Where(bucket => bucket.Username == "Péér255").SumAsync(bucket => bucket.Bytes));
        watch.Restart();
        var repeated = await new TransferAnalyticsReader(database.Factory).GetAsync(query);
        TimeSpan repeatedAnalytics = watch.Elapsed;
        Assert.AreEqual(totals.DownloadBytes, repeated.DownloadBytes);
        Assert.AreEqual(totals.UploadBytes, repeated.UploadBytes);
        Console.WriteLine($"Accounting256peersx100: publish={publish.TotalMilliseconds:F1} ms/{allocated} B/{firstReads} SELECTs; "
            + $"replay={replay.TotalMilliseconds:F1} ms/{replayReads} SELECTs; "
            + $"analytics first/repeat={analytics.TotalMilliseconds:F1}/{repeatedAnalytics.TotalMilliseconds:F1} ms; "
            + $"largest SELECT parameter count={reads.LargestParameterCount}.");
        Assert.IsTrue(firstReads < 8 && replayReads < 8,
            $"Accounting reads must remain batched for fresh observations and replay: {firstReads}/{replayReads} SELECTs.");
        Assert.IsTrue(reads.LargestParameterCount < 16,
            "Queued attempt identities must not expand into one SQL parameter each and hit SQLite's variable limit.");
        Assert.IsTrue(allocated < 8_000_000,
            $"256 peers with 100 accounting observations each allocated {allocated} bytes; "
            + "bucket persistence must not construct tracked commands for every peer/time bucket.");
        await session.StopAsync();
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

    private static SearchProjectionInput Result(long sequence)
        => new(sequence, checked((int)sequence), "user", 1, $"file-{sequence}.mp3", 100,
            320, null, 44_100, 180, ".mp3", 1_000, true, null, DateTimeOffset.UtcNow);

    private static async Task<PersistenceHealthSnapshot> RunCompletedWriterAsync(
        WriterDatabase database,
        IReadOnlyList<PersistenceMutation> mutations,
        IPersistenceMutationObserver? observer = null)
    {
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        foreach (var mutation in mutations)
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options, mutationObserver: observer).RunAsync(CancellationToken.None);
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

    private sealed class CommitAfterFirstReadInterceptor(Func<Task> commit) : DbCommandInterceptor
    {
        public bool Committed { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!Committed)
            {
                await commit();
                Committed = true;
            }
            return result;
        }
    }

    private sealed class SelectCountInterceptor : DbCommandInterceptor
    {
        public int Count { get; set; }
        public int LargestParameterCount { get; private set; }
        public int JobInserts { get; set; }
        private void CountJobWrites(DbCommand command)
        {
            if (command.CommandText.StartsWith("INSERT INTO jobs", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.StartsWith("INSERT INTO \"jobs\"", StringComparison.OrdinalIgnoreCase))
                JobInserts++;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CountJobWrites(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountJobWrites(command);
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Count++;
                LargestParameterCount = Math.Max(LargestParameterCount, command.Parameters.Count);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailOnceReaderInterceptor : DbCommandInterceptor
    {
        public int FailuresRemaining { get; set; }
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                Failures++;
                throw new IOException("Injected transient failure during survivor replay.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingMutationObserver : IPersistenceMutationObserver
    {
        public List<PersistenceMutation> CommittedMutations { get; } = [];
        public List<Guid> SupersededTransfers { get; } = [];
        public List<PersistenceMutation> Failed { get; } = [];
        public Action? OnCommit { get; init; }
        public Action? OnFailure { get; init; }
        public void Committed(IReadOnlyList<PersistenceMutation> mutations,
        IReadOnlyList<(Guid TransferId, long Revision)>? supersededTransfers = null)
        {
            CommittedMutations.AddRange(mutations);
            SupersededTransfers.AddRange((supersededTransfers ?? []).Select(item => item.TransferId));
            OnCommit?.Invoke();
        }
        public void PermanentlyFailed(IReadOnlyList<PersistenceMutation> mutations, Exception exception)
        {
            Failed.AddRange(mutations);
            OnFailure?.Invoke();
        }
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
            global::Tests.TestDatabase.Create(sqliteOptions.DatabasePath);
            owner = SqliteDatabaseOwner.Acquire(sqliteOptions);
            Factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(sqliteOptions));
        }

        public SockseekDbContextFactory Factory { get; }

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            using var context = Factory.CreateDbContext();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                (Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection());
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
