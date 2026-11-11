using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class ReadServiceTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TransferTimelineObservesFinalFactsWithoutADaemonBatch(bool evictBeforeFlush)
    {
        var options = Options.Create(new ServerOptions { Persistence = new() { Enabled = false } });
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options);
        var state = supervisor.StateStore;
        await using var events = new ServerEventBroadcaster(state, supervisor,
            new global::Tests.Server.NoOpHubContext<ServerEventHub>());
        int daemonBatches = 0;
        events.BatchPublished += _ => Interlocked.Increment(ref daemonBatches);
        var apply = typeof(EngineStateStore).GetMethod("UpsertTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stateGate = (Lock)typeof(EngineStateStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        var started = DateTimeOffset.UnixEpoch;
        var transfer = new TransferSnapshot(Guid.NewGuid(), TransferSnapshotDirection.Download,
            TransferSnapshotSource.SoulseekPeer, Guid.NewGuid(), Guid.NewGuid(), 1,
            "Peer", @"Share\File.bin", "local", null, "InProgress", 0, 100, 1, null,
            RequestedAtUtc: started);
        lock (stateGate) apply.Invoke(state, [transfer, false, null, null, null]);
        var queries = new TransferReadService(state, supervisor.Runtime, persistence);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var updates = queries.ObserveTransferTimelineAsync(
            new(TransferCommandDirection.Download, IncludeGroupSummaries: true), events, deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual("InProgress", updates.Current.Items.Single().Status.State);
        Assert.AreEqual(TransferRetainedCoverageState.Unavailable, updates.Current.GroupSummaries!.RetainedCoverage.State);
        var jobs = new JobReadService(state, supervisor, persistence);
        await using var jobUpdates = jobs.ObserveJobsAsync(transfer.JobId!.Value,
            [transfer.JobId!.Value], events, deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await jobUpdates.MoveNextAsync());
        lock (stateGate) apply.Invoke(state, [transfer with { Revision = 2, BytesTransferred = 50 }, false, null, null, null]);
        Assert.IsTrue(await jobUpdates.MoveNextAsync());
        Assert.AreEqual(50L, jobUpdates.Current.State.Transfers.Single().Progress?.BytesTransferred);

        // This workflow is outside the compact daemon stream. Its final facts
        // and later runtime eviction must still reach an existing page reader.
        var final = transfer with { Revision = 3, State = "Completed", BytesTransferred = 100,
            TerminalOutcome = TransferSnapshotTerminalOutcome.Succeeded };
        lock (stateGate)
        {
            apply.Invoke(state, [final, true, started.AddSeconds(1), null, null]);
            if (evictBeforeFlush) state.RemoveTerminalTransfer(transfer.Id);
        }
        Assert.IsTrue(await updates.MoveNextAsync());
        var row = updates.Current.Items.Single();
        Assert.AreEqual(TransferTerminalOutcome.Succeeded, row.Status.TerminalOutcome);
        Assert.AreEqual(100L, row.Progress.BytesTransferred);
        Assert.IsFalse(updates.Current.Changes?.Any(delta => delta.TransferId == transfer.Id) is true,
            "A complete final timeline row must not repeat its volatile body.");
        Assert.IsFalse(updates.Current.RemovedTransferIds.Contains(transfer.Id));
        if (!evictBeforeFlush)
        {
            state.RemoveTerminalTransfer(transfer.Id);
            Assert.IsTrue(await updates.MoveNextAsync());
            Assert.AreEqual(0, updates.Current.Items.Count);
            Assert.IsFalse(updates.Current.RemovedTransferIds.Contains(transfer.Id),
                "Persistence retirement must not remove the final row from the reader.");
        }
        Assert.IsNull(state.GetLiveTransfer(transfer.Id));
        Assert.AreEqual(0, Volatile.Read(ref daemonBatches), "Fact delivery must not require a redundant daemon packet.");
    }

    [TestMethod]
    public async Task SourceAvailabilityUpdatesWhenRetentionRemovesTheSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-source-retention-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions { Persistence = new()
        {
            Enabled = true, DataDirectory = directory, RetentionEnabled = false,
            CompletedJobHistoryAge = TimeSpan.FromDays(30), UnsuccessfulJobHistoryAge = null,
            SearchResultAge = null, TransferHistoryAge = null,
        } });
        var persistence = new PersistenceCoordinator(options);
        await persistence.StartAsync(CancellationToken.None);
        using var supervisor = new EngineSupervisor(options);
        await using var events = new ServerEventBroadcaster(supervisor.StateStore, supervisor,
            new global::Tests.Server.NoOpHubContext<ServerEventHub>());
        try
        {
            Guid source = Guid.NewGuid(), root = Guid.NewGuid(), workflow = Guid.NewGuid();
            Guid runtime = persistence.Runtime!.RuntimeId;
            JobPersistenceMutation Row(Guid id, int sequence, DateTimeOffset created, Guid? sourceId) => new(
                runtime, sequence, created, id, 1, PersistenceMutationPriority.Terminal, workflow,
                null, sourceId, null, sequence, "Song", "Terminal", "None", null, "Succeeded", "None", "None",
                "None", null, null, null, "Test", 1, null);
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(Row(source, 1, DateTimeOffset.UtcNow.AddDays(-60), null)));
            Assert.IsTrue(persistence.MutationSink.TryEnqueue(Row(root, 2, DateTimeOffset.UtcNow, source)));
            await WaitForVisibleAsync(persistence, async () => await persistence.JobHistory!.GetJobAsync(root) != null);
            var service = new JobReadService(supervisor.StateStore, supervisor, persistence);
            var detail = await service.GetJobAsync(root);
            Assert.IsNotNull(detail);
            Assert.AreEqual(true, detail.SourceJobAvailable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var updates = service.ObserveJobsAsync(root, [], events, deadline.Token).GetAsyncEnumerator();
            Assert.IsTrue(await updates.MoveNextAsync());
            Assert.AreEqual(source, updates.Current.SourceJobId);
            Assert.AreEqual(true, updates.Current.SourceJobAvailable);
            Assert.AreEqual(1, (await persistence.RunRetentionAsync(deadline.Token)).PrunedJobs);
            Assert.IsTrue(await updates.MoveNextAsync());
            Assert.AreEqual(source, updates.Current.SourceJobId);
            Assert.AreEqual(false, updates.Current.SourceJobAvailable);
            var retained = await service.GetJobAsync(root);
            Assert.IsNotNull(retained);
            Assert.IsNull(retained.Summary.SourceJobId);
            Assert.AreEqual(false, retained.SourceJobAvailable);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransferHistoryRemainsReadableAndExplicitlyDegradedAfterAnIsolatedMutationLoss()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-isolated-history-" + Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new() { Enabled = true, DataDirectory = directory, RetentionEnabled = false },
        }));
        await persistence.StartAsync(CancellationToken.None);
        try
        {
            Guid runtimeId = persistence.Runtime!.RuntimeId;
            Guid transferId = Guid.NewGuid();
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new JobPersistenceMutation(
                runtimeId, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural,
                Guid.NewGuid(), null, null, null, 0, "Song", "Pending", "None", null, "None", "None", "None",
                "None", null, null, null, null, 1, null)));
            Assert.IsTrue(persistence.MutationSink.TryEnqueue(new TransferPersistenceMutation(
                runtimeId, 2, DateTimeOffset.UtcNow, transferId, 1, PersistenceMutationPriority.Terminal,
                null, null, "Download", "SoulseekPeer", "healthy-peer", @"Share\Good.bin", "local",
                "Completed", "Succeeded", 100, 100, 1, "None", null, GroupRef: "healthy-folder")));
            await WaitForVisibleAsync(persistence, async () =>
                persistence.HealthSnapshot!.PermanentlyFailedMutationCount == 1
                && await persistence.TransferHistory!.GetTransferAsync(transferId) != null);

            var facade = new TransferReadService(new EngineStateStore(), runtime: null!, persistence);
            var page = await facade.GetTransfersAsync(new(Limit: 100));
            Assert.AreEqual(1, page.Items.Count,
                "A bad job record must not hide independently committed retained transfers.");
            Assert.AreEqual(transferId, page.Items.Single().TransferId,
                "A bad job record must not hide an independently committed healthy transfer.");
            Assert.AreEqual(TransferRetainedCoverageState.Degraded, page.RetainedCoverage.State);
            Assert.AreEqual(PersistenceHealthState.Degraded, persistence.HealthSnapshot!.State);
            var groups = await facade.GetTransferGroupSummariesAsync([
                new(TransferCommandDirection.Download, "healthy-peer", "healthy-folder"),
            ]);
            Assert.AreEqual(TransferRetainedCoverageState.Degraded, groups.RetainedCoverage.State);
            Assert.AreEqual(1L, groups.Items.Single().FileCount);
            Assert.AreEqual(100L, groups.Items.Single().TransferredBytes);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransferHistory_NormalizesCoreDownloadAndUploadReasons()
    {
        (string Direction, string Outcome, string Failure, string Cancellation,
            string ExpectedFailure, TransferCancellationSource ExpectedCancellation)[] cases =
        [
            ("Download", "Cancelled", "None", "Requested", "None", TransferCancellationSource.User),
            ("Download", "Cancelled", "None", "ManualSkip", "None", TransferCancellationSource.User),
            ("Download", "Cancelled", "None", "Stale", "None", TransferCancellationSource.None),
            ("Download", "Failed", "PeerFailure", "None", "ConnectionFailed", TransferCancellationSource.None),
            ("Download", "Failed", "Stale", "None", "TransferTimedOut", TransferCancellationSource.None),
            ("Download", "Failed", "Finalization", "None", "Unknown", TransferCancellationSource.None),
            ("Upload", "Failed", "InternalFailure", "None", "InternalError", TransferCancellationSource.None),
            ("Upload", "Failed", "Unavailable", "None", "FileUnavailable", TransferCancellationSource.None),
            ("Upload", "Failed", "NotShared", "None", "FileNoLongerShared", TransferCancellationSource.None),
            ("Upload", "Cancelled", "None", "User", "None", TransferCancellationSource.User),
            ("Upload", "Cancelled", "None", "Peer", "None", TransferCancellationSource.Peer),
            ("Upload", "Cancelled", "None", "DaemonShutdown", "None", TransferCancellationSource.DaemonShutdown),
            ("Upload", "Cancelled", "None", "CatalogInvalidation", "None", TransferCancellationSource.CatalogInvalidation),
            ("Download", "Failed", "future-failure", "future-source", "Unknown", TransferCancellationSource.None),
        ];
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-transfer-reasons-" + Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new() { Enabled = true, DataDirectory = directory, RetentionEnabled = false },
        }));
        await persistence.StartAsync(CancellationToken.None);
        try
        {
            var expected = new Dictionary<Guid, (string Failure, TransferCancellationSource Cancellation)>();
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SockseekDataPaths.ResolveDatabasePath(directory) }.ToString()))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                foreach (var item in cases)
                {
                    Guid id = Guid.NewGuid();
                    expected.Add(id, (item.ExpectedFailure, item.ExpectedCancellation));
                    await using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = """
                        INSERT INTO transfers (id, last_runtime_id, last_sequence, direction, source,
                            username, state, terminal_outcome, total_bytes, transferred_bytes, attempt_count,
                            created_at_utc, completed_at_utc, failure_reason, cancellation_source, revision)
                        VALUES ($id, $runtime, 1, $direction, 'SoulseekPeer', 'Peer', $outcome, $outcome,
                            100, 50, 1, 100, 200, $failure, $cancellation, 1);
                        """;
                    command.Parameters.AddWithValue("$id", id);
                    command.Parameters.AddWithValue("$runtime", persistence.Runtime!.RuntimeId);
                    command.Parameters.AddWithValue("$direction", item.Direction);
                    command.Parameters.AddWithValue("$outcome", item.Outcome);
                    command.Parameters.AddWithValue("$failure", item.Failure);
                    command.Parameters.AddWithValue("$cancellation", item.Cancellation);
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }
            var facade = new TransferReadService(new EngineStateStore(), runtime: null!, persistence);
            var page = await facade.GetTransfersAsync(new(Limit: 100));
            Assert.AreEqual(cases.Length, page.Items.Count);
            foreach (var transfer in page.Items)
            {
                var reason = expected[transfer.TransferId];
                Assert.AreEqual(reason.Failure, transfer.Status.FailureReason.ToString());
                Assert.AreEqual(reason.Cancellation, transfer.Status.CancellationSource);
            }
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DashboardAnalytics_WhenPersistenceIsDisabled_IsExplicitlyUnavailable()
    {
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions { Enabled = false },
        }));
        var facade = new DashboardAnalyticsFacade(persistence);

        DashboardAnalyticsDto result = await facade.GetAsync("24h");

        Assert.AreEqual(1, result.AccountingVersion);
        Assert.AreEqual(
            DashboardAnalyticsCoverageState.Unavailable,
            result.Range.Coverage.State);
        Assert.IsFalse(result.Range.Coverage.IsComplete);
        Assert.AreEqual(0, result.Bandwidth.Count);
    }

    [TestMethod]
    public async Task DashboardAnalytics_UsesFixedBoundedBucketsAndIndependentComparisonCoverage()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-dashboard-analytics",
            Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                RetentionEnabled = false,
            },
        }));
        await persistence.StartAsync(CancellationToken.None);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(10);
            long bucketTicks = now.UtcTicks - now.UtcTicks % TimeSpan.FromMinutes(5).Ticks;
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = SockseekDataPaths.ResolveDatabasePath(dataDirectory),
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO transfer_byte_buckets (bucket_start_utc, direction, username, bytes) VALUES ($start, 'Download', 'Peer', 128);";
                command.Parameters.AddWithValue("$start", new DateTimeOffset(bucketTicks, TimeSpan.Zero).ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync();
            }
            var facade = new DashboardAnalyticsFacade(
                persistence,
                new FixedClock(now));

            DashboardAnalyticsDto day = await facade.GetAsync("24h");
            DashboardAnalyticsDto all = await facade.GetAsync("all");

            Assert.AreEqual(48, day.Bandwidth.Count);
            Assert.AreEqual(128, day.Summary.DownloadedBytes,
                "The current partial accounting bucket must be visible without waiting for the next boundary.");
            Assert.AreEqual(1_800, day.Range.BucketSeconds);
            Assert.IsNotNull(day.Comparison);
            Assert.IsFalse(day.Range.Coverage.IsComplete);
            Assert.IsFalse(day.Comparison.Coverage.IsComplete);
            Assert.IsTrue(all.Bandwidth.Count is >= 1 and <= 60);
            Assert.IsTrue(all.Range.Coverage.IsComplete);
            Assert.IsNull(all.Comparison);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransferTimeline_ReturnsLiveRowsWithExplicitUnavailableRetainedCoverage()
    {
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions { Enabled = false },
        }));
        var live = new EngineStateStore();
        Guid transferId = Guid.NewGuid();
        DateTimeOffset requested = DateTimeOffset.UnixEpoch.AddHours(2);
        live.UpdateUploadTransfer(new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
            transferId,
            Revision: 1,
            Username: "peer",
            RemotePath: @"Share\Live.flac",
            SizeBytes: 100,
            RequestedAtUtc: requested,
            State: Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress,
            FailureReason: Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
            CancellationSource: Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
            BytesTransferred: 50,
            BytesPerSecond: 25,
            LastProgressAtUtc: requested.AddSeconds(1),
            Attempt: new Sockseek.Core.Transfers.Uploads.UploadAttemptSnapshot(
                Guid.NewGuid(), 1, requested, null, 50, 25),
            FinishedAtUtc: null,
            File: new TransferFileMetadataSnapshot(
                "Live.flac", 100, "flac", 900, 24, 96_000, 180),
            GroupRef: @"Share"));

        var facade = new TransferReadService(live, runtime: null!, persistence);
        TransferTimelinePageDto page = await facade.GetTransfersAsync(new(Limit: 10));

        Assert.AreEqual(live.GetDaemonSnapshot().Position.Epoch, page.ReadPosition!.Epoch);
        Assert.AreEqual(live.GetReadPosition(), page.ReadPosition);
        live.NotifyTerminalTransferCommitted();
        TransferTimelinePageDto afterCommit = await facade.GetTransfersAsync(new(Limit: 10));
        Assert.AreEqual(page.ReadPosition.Epoch, afterCommit.ReadPosition!.Epoch);
        Assert.AreEqual(page.ReadPosition.Sequence + 1, afterCommit.ReadPosition.Sequence);
        Assert.IsTrue(page.ReadPosition.Sequence < afterCommit.ReadPosition.Sequence);
        Assert.AreEqual(TransferRetainedCoverageState.Unavailable, page.RetainedCoverage.State);
        Assert.AreEqual("PersistenceDisabled", page.RetainedCoverage.Reason);
        TransferStateDto row = page.Items.Single();
        Assert.AreEqual(transferId, row.TransferId);
        Assert.AreEqual(requested, row.CreatedAtUtc);
        Assert.AreEqual("Live.flac", row.File?.Name);
        Assert.AreEqual(@"Share", row.Identity.GroupRef);
        var lookup = await facade.LookupTransfersAsync(new([transferId, transferId, Guid.NewGuid()]));
        Assert.AreEqual(TransferRetainedCoverageState.Unavailable, lookup.RetainedCoverage.State);
        Assert.AreEqual(transferId, lookup.Items.Single().TransferId);
        Assert.IsNull(lookup.NextCursor);
        var attempts = await facade.GetTransferAttemptsAsync(transferId, 0, 100);
        Assert.IsNotNull(attempts);
        Assert.AreEqual(transferId, attempts.Items.Single().TransferId);
        Assert.AreEqual("Started", attempts.Items[0].State);
        Assert.AreEqual(0, (await facade.LookupTransfersAsync(new([], "Upload"))).Items.Count);
        var groupTotals = await facade.GetTransferGroupSummariesAsync([
            new TransferGroupKeyDto(TransferCommandDirection.Upload, "peer", @"Share"),
        ]);
        Assert.AreEqual(TransferRetainedCoverageState.Unavailable, groupTotals.RetainedCoverage.State);
        Assert.AreEqual(0, groupTotals.Items.Count);
    }

    [TestMethod]
    public async Task TransferGroupSummaries_OverlayLiveProgressWithoutDoubleCountingPersistedRows()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(),
            "sockseek-transfer-groups", Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                RetentionEnabled = false,
            },
        }));
        await persistence.StartAsync(CancellationToken.None);
        try
        {
            string groupRef = "opaque upload group";
            Guid persistedId = Guid.NewGuid();
            Guid liveOnlyId = Guid.NewGuid();
            DateTimeOffset requested = DateTimeOffset.UtcNow.AddMinutes(-1);
            var initialMutation = new TransferPersistenceMutation(
                persistence.Runtime!.RuntimeId,
                Sequence: 1,
                requested,
                persistedId,
                Revision: 1,
                // Seed retained facts immediately. This exercises read/overlay
                // semantics, not the writer's three-second progress cadence.
                PersistenceMutationPriority.Structural,
                JobId: null,
                WorkflowId: null,
                Direction: "Upload",
                Source: "SoulseekPeer",
                Username: "peer",
                RemotePath: @"Share\One.bin",
                LocalPath: null,
                State: "InProgress",
                TerminalOutcome: "None",
                TotalBytes: 100,
                TransferredBytes: 20,
                AttemptCount: 1,
                FailureReason: "None",
                FailureMessage: null,
                BytesPerSecond: 5,
                GroupRef: groupRef,
                GroupDisplayPath: "Share");
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(initialMutation));
            await WaitForPersistedTransferAsync(persistence, persistedId);

            var live = new EngineStateStore();
            void AddLive(Guid id, long revision, string path, long total, long transferred,
                Sockseek.Core.Transfers.Uploads.UploadTransferState state, long speed = 0, string? targetGroup = null)
                => live.UpdateUploadTransfer(new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
                    id,
                    revision,
                    "peer",
                    path,
                    total,
                    requested,
                    state,
                    Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
                    Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
                    transferred,
                    speed,
                    null,
                    null,
                    null,
                    GroupRef: targetGroup ?? groupRef,
                    GroupDisplayPath: "Share"));
            AddLive(persistedId, 2, @"Share\One.bin", 100, 60,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress, speed: 25);
            AddLive(liveOnlyId, 1, @"Share\Two.bin", 200, 10,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.Queued);

            var facade = new TransferReadService(live, runtime: null!, persistence);

            // Totals must use the same revision/archive/size fallback rules as
            // file pages, including zero-size metadata and equal revisions.
            const string edgeGroup = "unknown sizes and retained revisions";
            var fallbackId = Guid.NewGuid();
            var newerId = Guid.NewGuid();
            var archivedId = Guid.NewGuid();
            var equalId = Guid.NewGuid();
            var edgeRows = new[]
            {
                initialMutation with { TransferId = fallbackId, RemotePath = "Known.bin", Sequence = 10, TotalBytes = -1,
                    File = new("Known.bin", 400, ".bin", null, null, null, null), GroupRef = edgeGroup },
                initialMutation with { TransferId = newerId, RemotePath = "Newer.bin", Sequence = 11, Revision = 3, TotalBytes = 200,
                    TransferredBytes = 200, State = "Completed", TerminalOutcome = "Succeeded", GroupRef = edgeGroup },
                initialMutation with { TransferId = archivedId, RemotePath = "Archived.bin", Sequence = 12, TransferredBytes = 100,
                    State = "Completed", TerminalOutcome = "Succeeded", GroupRef = edgeGroup },
                initialMutation with { TransferId = equalId, RemotePath = "Empty.bin", Sequence = 13, TotalBytes = -1,
                    File = new("Empty.bin", 0, ".bin", null, null, null, null), GroupRef = edgeGroup },
            };
            foreach (var row in edgeRows) Assert.IsTrue(persistence.MutationSink!.TryEnqueue(row));
            await WaitForVisibleAsync(persistence, async () =>
                (await persistence.TransferHistory!.GetTransfersAsync(new(Limit: 10, GroupRef: edgeGroup))).Items.Count == 4);
            Assert.AreEqual(1, (await persistence.TransferHistory!.RemoveAsync(
                new(TransferId: archivedId))).ChangedCount);
            AddLive(fallbackId, 2, "Known.bin", -1, 60,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress, 25, edgeGroup);
            AddLive(newerId, 2, "Newer.bin", 100, 60,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress, 25, edgeGroup);
            AddLive(archivedId, 2, "Archived.bin", 100, 60,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress, 25, edgeGroup);
            AddLive(equalId, 1, "Empty.bin", -1, 6,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.Queued, targetGroup: edgeGroup);
            AddLive(Guid.NewGuid(), 1, "Unknown.bin", -1, 5,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.Queued, targetGroup: edgeGroup);
            var edgeTotals = (await facade.GetTransferGroupSummariesAsync([
                new(TransferCommandDirection.Upload, "peer", edgeGroup),
            ])).Items.Single();
            Assert.AreEqual(4, edgeTotals.FileCount);
            Assert.AreEqual(600, edgeTotals.KnownTotalBytes);
            Assert.AreEqual(271, edgeTotals.TransferredBytes);
            Assert.AreEqual(25, edgeTotals.SpeedBytesPerSecond);
            Assert.AreEqual(340, edgeTotals.KnownRemainingActiveBytes);
            Assert.AreEqual(1, edgeTotals.UnknownTotalCount);
            Assert.AreEqual(1, edgeTotals.UnknownActiveTotalCount);
            Assert.AreEqual(2, edgeTotals.QueuedCount);
            Assert.AreEqual(1, edgeTotals.InProgressCount);
            Assert.AreEqual(1, edgeTotals.SucceededCount);
            var edgePage = await facade.GetTransfersAsync(new(Limit: 10, Direction: "Upload", Username: "peer", GroupRef: edgeGroup));
            Assert.AreEqual(edgeTotals.FileCount, edgePage.Items.Count);
            Assert.AreEqual(edgeTotals.KnownTotalBytes,
                edgePage.Items.Sum(row => row.Progress.TotalBytes >= 0 ? row.Progress.TotalBytes : row.File?.Size ?? 0));
            Assert.AreEqual(edgeTotals.TransferredBytes, edgePage.Items.Sum(row => row.Progress.BytesTransferred));

            var response = await facade.GetTransferGroupSummariesAsync([
                new TransferGroupKeyDto(TransferCommandDirection.Upload, "peer", groupRef),
            ]);
            Assert.AreEqual(TransferRetainedCoverageState.Available, response.RetainedCoverage.State);
            var group = response.Items.Single();
            Assert.AreEqual(2, group.FileCount);
            Assert.AreEqual(300, group.KnownTotalBytes);
            Assert.AreEqual(70, group.TransferredBytes);
            Assert.AreEqual(25, group.SpeedBytesPerSecond);
            Assert.AreEqual(230, group.KnownRemainingActiveBytes);
            Assert.AreEqual(0, group.UnknownActiveTotalCount);
            Assert.AreEqual(1, group.QueuedCount);
            Assert.AreEqual(1, group.InProgressCount);
            var lookup = await facade.LookupTransfersAsync(new([persistedId, persistedId, Guid.NewGuid()], "upload"));
            Assert.AreEqual(persistedId, lookup.Items.Single().TransferId);
            Assert.AreEqual(60L, lookup.Items.Single().Progress.BytesTransferred, "The lookup must use the canonical live overlay.");
            Assert.IsNull(lookup.NextCursor);
            var queuedLookup = await facade.LookupTransfersAsync(new([liveOnlyId], "Upload"));
            Assert.AreEqual(liveOnlyId, queuedLookup.Items.Single().TransferId, "Queued uploads need no full queue scan.");
            Assert.AreEqual(0, (await facade.LookupTransfersAsync(new([persistedId, liveOnlyId], "Download"))).Items.Count);
            var firstPage = await facade.GetTransfersAsync(new(Limit: 1, Direction: "Upload", Username: "peer", GroupRef: groupRef));
            Assert.AreEqual(1, firstPage.Items.Count);
            Assert.IsNotNull(firstPage.NextCursor);
            var secondPage = await facade.GetTransfersAsync(new(Cursor: firstPage.NextCursor, Limit: 1, Direction: "Upload", Username: "peer", GroupRef: groupRef));
            CollectionAssert.AreEquivalent(
                new[] { persistedId, liveOnlyId },
                firstPage.Items.Concat(secondPage.Items).Select(item => item.TransferId).ToArray());
            Assert.IsNull(secondPage.NextCursor);

            using var eventSupervisor = new EngineSupervisor(Options.Create(new ServerOptions()));
            await using var events = new ServerEventBroadcaster(live, eventSupervisor,
                new global::Tests.Server.NoOpHubContext<ServerEventHub>());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var snapshot = await facade.GetTransferTimelineSnapshotAsync(
                new(TransferCommandDirection.Upload, Limit: 1, Username: "peer", GroupRef: groupRef,
                    IncludeGroupSummaries: true), events, deadline.Token);
            Assert.AreEqual(1, snapshot.Items.Count);
            Assert.AreEqual(group, snapshot.GroupSummaries!.Items.Single(), "Snapshot totals include the unpaged member.");
            var flat = await facade.GetTransferTimelineSnapshotAsync(
                new(TransferCommandDirection.Upload, Limit: 1), events, deadline.Token);
            Assert.IsNull(flat.GroupSummaries, "Flat readers did not request grouping.");
            await using var observation = facade.ObserveTransferTimelineAsync(
                new(TransferCommandDirection.Upload, Limit: 1, Username: "peer", GroupRef: groupRef,
                    IncludeGroupSummaries: true), events, deadline.Token).GetAsyncEnumerator();
            Assert.IsTrue(await observation.MoveNextAsync());
            Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(snapshot),
                System.Text.Json.JsonSerializer.Serialize(observation.Current),
                "HTTP and streaming reads must expose the same initial page, overlays, counts and position.");
            Assert.AreEqual(2, observation.Current.GroupSummaries!.Items.Single().FileCount);
            Guid outside = new[] { persistedId, liveOnlyId }.Except(
                observation.Current.Items.Select(row => row.TransferId)).Single();
            long outsideSize = outside == persistedId ? 100 : 200;
            AddLive(outside, 3, outside == persistedId ? @"Share\One.bin" : @"Share\Two.bin",
                outsideSize, outsideSize, Sockseek.Core.Transfers.Uploads.UploadTransferState.Completed);
            Assert.IsTrue(await observation.MoveNextAsync());
            var observedGroup = observation.Current.GroupSummaries!.Items.Single();
            Assert.AreEqual(2, observedGroup.FileCount);
            Assert.AreEqual(1, observedGroup.SucceededCount);
            Assert.AreEqual(outsideSize + (outside == persistedId ? 10 : 60), observedGroup.TransferredBytes);
            Assert.IsTrue(observation.Current.Items.All(row => row.TransferId != outside),
                "Totals must update without pulling an unpaged member into the file prefix.");

            Guid cancelledId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            AddLive(cancelledId, 1, @"Share\Cancelled.bin", 100, 0,
                Sockseek.Core.Transfers.Uploads.UploadTransferState.Cancelled);
            for (int index = 1; index <= 3; index++)
                AddLive(Guid.Parse($"ffffffff-ffff-ffff-ffff-{index:000000000000}"), 1,
                    $@"Share\Queued{index}.bin", 100, 0,
                    Sockseek.Core.Transfers.Uploads.UploadTransferState.Queued);
            var cancelledPage = await facade.GetTransfersAsync(new(Limit: 1, Direction: "Upload", State: "Cancelled", Username: "peer", GroupRef: groupRef));
            Assert.AreEqual(cancelledId, cancelledPage.Items.Single().TransferId,
                "A nonmatching queue prefix cannot hide a matching live group member.");
            Assert.IsNull(cancelledPage.NextCursor);
            // Valid individual sizes can exceed the group's Int64 range.
            // A failed totals query must not discard independently readable rows.
            Guid hugeId = Guid.NewGuid();
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(initialMutation with
            {
                TransferId = hugeId, Sequence = 2, TransferredBytes = 0,
                TotalBytes = long.MaxValue - 1,
            }));
            await WaitForPersistedTransferAsync(persistence, hugeId);
            var readable = await facade.GetTransferTimelineSnapshotAsync(
                new(TransferCommandDirection.Upload, Limit: 1, Username: "peer", GroupRef: groupRef,
                    IncludeGroupSummaries: true), events, deadline.Token);
            Assert.AreEqual(1, readable.Items.Count);
            Assert.AreEqual(TransferRetainedCoverageState.Available, readable.RetainedCoverage.State);
            Assert.AreEqual(TransferRetainedCoverageState.Unavailable, readable.GroupSummaries!.RetainedCoverage.State);
            Assert.AreEqual(0, readable.GroupSummaries.Items.Count);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void TransferTimelineComposition_OverlaysLiveRowsAndDeduplicatesAcrossSources()
    {
        Guid overlap = Guid.Parse("00000000-0000-0000-0000-000000000003");
        Guid older = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid queued = Guid.Parse("00000000-0000-0000-0000-000000000004");
        DateTimeOffset origin = DateTimeOffset.UnixEpoch;

        DateTimeOffset completedAt = origin.AddSeconds(5);
        TransferStateDto retainedOverlap = TimelineRow(overlap, origin.AddSeconds(3), "Retained") with
        {
            Status = TimelineRow(overlap, origin.AddSeconds(3), "Retained").Status with
            { CompletedAtUtc = completedAt, FailureMessage = "retained-detail" },
        };
        TransferTimelinePageDto page = TransferReadService.ComposeTransferTimeline(
            [retainedOverlap, TimelineRow(older, origin.AddSeconds(2), "Completed")],
            [TimelineRow(overlap, origin.AddSeconds(3), "InProgress")],
            [TimelineRow(queued, origin.AddSeconds(4), "Queued")],
            limit: 2,
            retainedHasMore: false,
            queueHasMore: false,
            new TransferRetainedCoverageDto(TransferRetainedCoverageState.Available));

        CollectionAssert.AreEqual(
            new[] { queued, overlap },
            page.Items.Select(item => item.TransferId).ToArray());
        Assert.AreEqual("InProgress", page.Items.Single(item => item.TransferId == overlap).Status.State);
        Assert.AreEqual(completedAt, page.Items.Single(item => item.TransferId == overlap).Status.CompletedAtUtc);
        Assert.AreEqual("retained-detail", page.Items.Single(item => item.TransferId == overlap).Status.FailureMessage);
        Assert.AreEqual(2, page.Items.Select(item => item.TransferId).Distinct().Count());
        Assert.IsNotNull(page.NextCursor);
    }

    [TestMethod]
    public void TransferTimelineComposition_PrefersNewerRetainedStateAfterLiveCapture()
    {
        var id = Guid.NewGuid();
        var retained = TimelineRow(id, DateTimeOffset.UnixEpoch, "Completed") with { Revision = 8 };
        var captured = retained with { Status = retained.Status with { State = "InProgress" }, Revision = 7 };
        var page = TransferReadService.ComposeTransferTimeline(
            [retained], [captured], [], 10, false, false,
            new TransferRetainedCoverageDto(TransferRetainedCoverageState.Available));
        Assert.AreEqual(retained, page.Items.Single());
    }

    [TestMethod]
    public async Task TransferDetail_MergesRetainedMetadataWithAuthoritativeLiveAttempt()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-combined-transfer-detail",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                RetentionEnabled = false,
            },
        });
        var persistence = new PersistenceCoordinator(options);
        await persistence.StartAsync(CancellationToken.None);

        try
        {
            Guid transferId = Guid.NewGuid();
            Guid retainedAttemptId = Guid.NewGuid();
            DateTimeOffset retainedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            Guid runtimeId = persistence.Runtime!.RuntimeId;
            var retainedTransfer = new TransferPersistenceMutation(
                runtimeId,
                Sequence: 1,
                retainedAt,
                transferId,
                Revision: 1,
                PersistenceMutationPriority.Terminal,
                JobId: null,
                WorkflowId: null,
                Direction: "Upload",
                Source: "SoulseekPeer",
                Username: "retained-peer",
                RemotePath: @"Share\Retained.mp3",
                LocalPath: null,
                State: "Failed",
                TerminalOutcome: "Failed",
                TotalBytes: 100,
                TransferredBytes: 50,
                AttemptCount: 3,
                FailureReason: "Unavailable",
                FailureMessage: null);
            var retainedAttempt = new TransferAttemptPersistenceMutation(
                runtimeId,
                Sequence: 2,
                retainedAt,
                retainedAttemptId,
                Revision: 1,
                PersistenceMutationPriority.Terminal,
                transferId,
                AttemptNumber: 3,
                Source: "SoulseekPeer",
                State: "Failed",
                SourceUsername: "retained-peer",
                SourcePath: @"Share\Retained.mp3",
                OutputPath: null,
                FailureReason: "Unavailable",
                FailureMessage: null);
            var live = new EngineStateStore();
            Guid liveAttemptId = Guid.NewGuid();
            DateTimeOffset liveStartedAt = DateTimeOffset.UtcNow;
            var upload = new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
                transferId,
                Revision: 2,
                Username: "live-peer",
                RemotePath: @"Share\Live.mp3",
                SizeBytes: 100,
                RequestedAtUtc: liveStartedAt,
                State: Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress,
                FailureReason: Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
                CancellationSource: Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
                BytesTransferred: 75,
                BytesPerSecond: 25,
                LastProgressAtUtc: liveStartedAt,
                Attempt: new Sockseek.Core.Transfers.Uploads.UploadAttemptSnapshot(
                    liveAttemptId,
                    Number: 4,
                    StartedAtUtc: liveStartedAt,
                    FinishedAtUtc: null,
                    BytesTransferred: 75,
                    BytesPerSecond: 25),
                FinishedAtUtc: null);
            live.UpdateUploadTransfer(upload with
            {
                Revision = 0,
                Attempt = upload.Attempt! with { AttemptId = retainedAttemptId, Number = 3 },
            });
            live.UpdateUploadTransfer(upload);

            var facade = new TransferReadService(live, runtime: null!, persistence);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = SockseekDataPaths.ResolveDatabasePath(dataDirectory),
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var held = connection.BeginTransaction(deferred: false);
                Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new TransferTerminalPersistenceMutation(
                    retainedTransfer, retainedAttempt, OwningJob: null)));
                var pending = await facade.GetTransferAttemptsAsync(transferId, 0, 10)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(pending, "A visible transfer must not return 404 while its writes are held.");
                CollectionAssert.AreEqual(new[] { retainedAttemptId, liveAttemptId },
                    pending.Items.Select(attempt => attempt.AttemptId).ToArray());
                Assert.AreEqual("Started", pending.Items[0].State);
                Assert.IsNull(pending.NextAttemptNumber);
                Assert.IsNull(await persistence.TransferHistory!.GetTransferAsync(transferId));
                held.Rollback();
            }
            await WaitForPersistedTransferAsync(persistence, transferId);
            TransferDetailDto? detail = await facade.GetTransferDetailAsync(transferId);

            Assert.IsNotNull(detail);
            Assert.AreEqual("live-peer", detail.Transfer.Identity.Username);
            Assert.AreEqual(retainedAt.ToUnixTimeMilliseconds(), detail.Transfer.CreatedAtUtc.ToUnixTimeMilliseconds(),
                "The current record preserves the retained immutable timeline order.");
            Assert.AreEqual(75L, detail.Transfer.Progress.BytesTransferred);
            Assert.AreEqual(4, detail.Transfer.Status.AttemptCount);
            Assert.AreEqual(liveAttemptId, detail.LatestAttempt?.AttemptId);
            Assert.AreEqual("Started", detail.LatestAttempt?.State);

            // A second live retry must not hide the previous uncommitted one.
            live.UpdateUploadTransfer(upload with
            {
                Revision = 3,
                State = Sockseek.Core.Transfers.Uploads.UploadTransferState.Failed,
                FinishedAtUtc = liveStartedAt.AddSeconds(1),
            });
            Guid retryId = Guid.NewGuid();
            live.UpdateUploadTransfer(upload with
            {
                Revision = 4,
                Attempt = upload.Attempt! with { AttemptId = retryId, Number = 5 },
            });
            var first = await facade.GetTransferAttemptsAsync(transferId, 0, 1);
            Assert.IsNotNull(first);
            Assert.AreEqual(retainedAttemptId, first.Items.Single().AttemptId);
            Assert.AreEqual("Failed", first.Items[0].State,
                "The durable successor must beat an older captured attempt revision.");
            Assert.AreEqual(3, first.NextAttemptNumber);
            var second = await facade.GetTransferAttemptsAsync(transferId, first.NextAttemptNumber.GetValueOrDefault(), 1);
            Assert.IsNotNull(second);
            Assert.AreEqual(liveAttemptId, second.Items.Single().AttemptId);
            Assert.AreEqual("Failed", second.Items[0].State);
            Assert.AreEqual(4, second.NextAttemptNumber);
            var third = await facade.GetTransferAttemptsAsync(transferId, second.NextAttemptNumber.GetValueOrDefault(), 1);
            Assert.IsNotNull(third);
            Assert.AreEqual(retryId, third.Items.Single().AttemptId);
            Assert.IsNull(third.NextAttemptNumber);
            CollectionAssert.AreEqual(new[] { 3, 4, 5 },
                (await facade.GetTransferAttemptsAsync(transferId, 0, 100))!.Items.Select(item => item.AttemptNumber).ToArray());
            Assert.AreEqual(0, (await facade.GetTransferAttemptsAsync(transferId, 5, 1))!.Items.Count);
            Assert.IsNull(await facade.GetTransferAttemptsAsync(Guid.NewGuid(), 0, 1));
            live.RemoveTerminalTransfer(transferId);
            Assert.AreEqual(retainedAttemptId,
                (await facade.GetTransferAttemptsAsync(transferId, 0, 100))!.Items.Single().AttemptId);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task JobPages_MergeLiveAndPersistedRowsWithoutGapsOrDuplicates()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-combined-job-pages",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                RetentionEnabled = false,
            },
        });
        var persistence = new PersistenceCoordinator(options);
        await persistence.StartAsync(CancellationToken.None);

        try
        {
            Guid workflowId = Guid.NewGuid();
            var jobs = Enumerable.Range(1, 6)
                .Select(displayOrder => new SongJob(new SongQuery { Title = $"Job {displayOrder}" })
                {
                    WorkflowId = workflowId,
                })
                .ToArray();
            foreach (var job in jobs)
                job.EnsureDisplayId();

            var live = new EngineStateStore();
            Register(live, jobs[1]); // overlaps persisted state
            Register(live, jobs[2]); // live only
            Register(live, jobs[4]); // live only

            Guid runtimeId = persistence.Runtime!.RuntimeId;
            int sequence = 0;
            var retainedMutations = new Dictionary<Guid, JobPersistenceMutation>();
            foreach (int index in new[] { 0, 1, 3, 5 })
            {
                var job = jobs[index];
                var mutation = new JobPersistenceMutation(
                    runtimeId,
                    ++sequence,
                    DateTimeOffset.UtcNow,
                    job.Id,
                    Revision: 1,
                    Priority: PersistenceMutationPriority.Structural,
                    WorkflowId: workflowId,
                    ParentJobId: null,
                    SourceJobId: index == 5 ? jobs[0].Id : null,
                    ResultJobId: null,
                    DisplayId: job.DisplayId,
                    Kind: "Song",
                    LifecycleState: "Terminal",
                    ActivityPhase: "None",
                    ActivityUntilUtc: null,
                    TerminalOutcome: "Succeeded",
                    SkipReason: "None",
                    CancellationSource: "None",
                    FailureReason: "None",
                    FailureMessage: null,
                    FailureDetail: null,
                    ItemName: null,
                    QueryText: job.Query.Title,
                    PayloadSchemaVersion: 1,
                    PayloadJson: null,
                    SourceDetail: index == 0 ? "CSV row 2" : null);
                retainedMutations.Add(job.Id, mutation);
                Assert.IsTrue(persistence.MutationSink!.TryEnqueue(mutation));
            }

            await WaitForPersistedCountAsync(persistence, workflowId, expected: 4);
            var facade = new JobReadService(live, supervisor: null!, persistence: persistence);
            var combined = new List<JobSummaryDto>();
            string? cursor = null;
            do
            {
                CombinedJobPage page = await facade.GetJobsAsync(
                    new JobQuery(null, null, null, workflowId, IncludeAll: true),
                    cursor,
                    limit: 2);
                combined.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor != null);

            CollectionAssert.AreEqual(
                jobs.Select(job => job.Id).ToArray(),
                combined.Select(job => job.JobId).ToArray());
            Assert.AreEqual(6, combined.Select(job => job.JobId).Distinct().Count());
            Assert.AreEqual("CSV row 2", combined.Single(job => job.JobId == jobs[0].Id).SourceDetail);
            Assert.AreEqual(
                ServerJobLifecycleState.Pending,
                combined.Single(job => job.JobId == jobs[1].Id).LifecycleState,
                "The live overlap must replace the persisted terminal row.");

            var newest = new List<JobSummaryDto>();
            cursor = null;
            do
            {
                CombinedJobPage page = await facade.GetJobsAsync(
                    new JobQuery(null, null, null, workflowId, IncludeAll: true, NewestFirst: true),
                    cursor,
                    limit: 2);
                newest.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor != null);
            CollectionAssert.AreEqual(jobs.Reverse().Select(job => job.Id).ToArray(),
                newest.Select(job => job.JobId).ToArray());
            Assert.AreEqual(6, newest.Select(job => job.JobId).Distinct().Count());
            Assert.AreEqual(ServerJobLifecycleState.Pending,
                newest.Single(job => job.JobId == jobs[1].Id).LifecycleState);
            var descendingPage = await facade.GetJobsAsync(
                new JobQuery(null, null, null, workflowId, IncludeAll: true, NewestFirst: true), null, limit: 2);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => facade.GetJobsAsync(
                new JobQuery(null, null, null, workflowId, IncludeAll: true),
                descendingPage.NextCursor,
                limit: 2));

            // A durable predecessor cannot reappear under a filter that the
            // current live row no longer matches. Continue past rejected overlap
            // rows so both directions still fill every page without duplicates.
            foreach (bool descending in new[] { false, true })
                foreach (var state in new[] { ServerJobLifecycleState.Pending, ServerJobLifecycleState.Terminal })
                {
                    var filtered = new List<JobSummaryDto>();
                    cursor = null;
                    do
                    {
                        var page = await facade.GetJobsAsync(new JobQuery(state, null, null, workflowId,
                            IncludeAll: true, NewestFirst: descending), cursor, 2);
                        filtered.AddRange(page.Items);
                        cursor = page.NextCursor;
                    } while (cursor is not null);
                    int[] indexes = state == ServerJobLifecycleState.Pending ? [1, 2, 4] : [0, 3, 5];
                    var expected = indexes.Select(index => jobs[index].Id);
                    if (descending) expected = expected.Reverse();
                    CollectionAssert.AreEqual(expected.ToArray(), filtered.Select(job => job.JobId).ToArray());
                    Assert.IsTrue(filtered.All(job => job.LifecycleState == state));
                }

            // The captured runtime owner remains authoritative until handoff,
            // just as in exact-ID reads, even if history has already advanced.
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(retainedMutations[jobs[1].Id] with
                { Sequence = ++sequence, Revision = 3 }));
            await WaitForVisibleAsync(persistence, async () =>
                (await persistence.JobHistory!.GetJobAsync(jobs[1].Id))?.Revision == 3);
            foreach (var state in new[] { ServerJobLifecycleState.Pending, ServerJobLifecycleState.Terminal })
            {
                var page = await facade.GetJobsAsync(new JobQuery(state, null, null, workflowId, IncludeAll: true), null, 100);
                int[] expected = state == ServerJobLifecycleState.Pending ? [1, 2, 4] : [0, 3, 5];
                CollectionAssert.AreEqual(expected.Select(index => jobs[index].Id).ToArray(), page.Items.Select(job => job.JobId).ToArray());
            }

            // Extractors retain their source and generated result. Presentation paging
            // excludes their generated results, while a linked-summary lookup
            // preserves request order and prefers current live state.
            var liveResult = new SongJob(new SongQuery { Title = "Live extracted song" }) { WorkflowId = workflowId };
            var retainedResult = new SongJob(new SongQuery { Title = "Retained extracted song" }) { WorkflowId = workflowId };
            var liveExtract = new ExtractJob("live", Sockseek.Core.InputType.String) { WorkflowId = workflowId, Result = liveResult };
            var retainedExtract = new ExtractJob("retained", Sockseek.Core.InputType.String) { WorkflowId = workflowId, Result = retainedResult };
            var nestedResult = new SongJob(new SongQuery { Title = "Nested extracted song" }) { WorkflowId = workflowId };
            var nestedExtract = new ExtractJob("nested", Sockseek.Core.InputType.String) { WorkflowId = workflowId, Result = nestedResult };
            Job[] imports = [liveExtract, liveResult, retainedExtract, retainedResult, nestedExtract, nestedResult];
            foreach (var job in imports) job.EnsureDisplayId();
            Register(live, liveExtract);
            Register(live, liveResult, liveExtract.Id);
            foreach (var job in imports)
            {
                var source = job == liveResult ? liveExtract : job == retainedResult ? retainedExtract
                    : job == nestedResult ? nestedExtract : null;
                // The retained source deliberately predates its ResultJobId
                // update; SourceJobId must still keep that result off the page.
                Guid? result = job == liveExtract ? liveResult.Id : job == nestedExtract ? nestedResult.Id : null;
                Guid? parent = job == nestedExtract || job == nestedResult ? jobs[0].Id : null;
                Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new JobPersistenceMutation(
                    runtimeId, ++sequence, DateTimeOffset.UtcNow, job.Id, 1, PersistenceMutationPriority.Structural,
                    workflowId, parent, job == liveResult ? null : source?.Id, result, job.DisplayId, job is ExtractJob ? "Extract" : "Song",
                    "Terminal", "None", null, "Succeeded", "None", "None", "None", null, null,
                    job is ExtractJob ? "Import" : "Song", null, 1,
                    job is ExtractJob ? RetainedJobPayloadCodec.Capture(CoreSnapshotFactory.CreateJob(job, 1).Payload) : null,
                    SourceDetail: job is ExtractJob ? "Text import" : null)));
            }
            await WaitForPersistedCountAsync(persistence, workflowId, expected: 10);
            var roots = new List<JobSummaryDto>();
            cursor = null;
            do
            {
                var page = await facade.GetJobsAsync(new JobQuery(null, null, null, workflowId,
                    IncludeAll: false, ExcludeExtractResults: true), cursor, 2);
                roots.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor != null);
            CollectionAssert.AreEqual(jobs.Select(job => job.Id).Concat(new[] { liveExtract.Id, retainedExtract.Id }).ToArray(),
                roots.Select(job => job.JobId).ToArray());
            Assert.AreEqual("Text import", roots.Single(job => job.JobId == retainedExtract.Id).SourceDetail);
            var onlyImports = await facade.GetJobsAsync(new JobQuery(null, null, null, workflowId,
                IncludeAll: false, ExcludeExtractResults: true, ExcludeKinds: [ServerJobKind.Song, ServerJobKind.RetrieveFolder]), null, 2);
            CollectionAssert.AreEqual(new[] { liveExtract.Id, retainedExtract.Id },
                onlyImports.Items.Select(job => job.JobId).ToArray());
            Assert.IsNull(onlyImports.NextCursor, "Excluded kinds must not consume page slots or create another page.");
            var nestedPage = await facade.GetJobsAsync(new JobQuery(null, null, null, workflowId,
                IncludeAll: false, ParentJobId: jobs[0].Id, ExcludeExtractResults: true), null, 1);
            Assert.AreEqual(nestedExtract.Id, nestedPage.Items.Single().JobId);
            Assert.IsNull(nestedPage.NextCursor, "A hidden generated sibling must not create a spurious extra page.");
            var lookedUp = await facade.LookupJobsAsync(new JobLookupRequestDto(
                [retainedResult.Id, liveResult.Id, Guid.NewGuid(), jobs[0].Id, retainedResult.Id]));
            CollectionAssert.AreEqual(new[] { retainedResult.Id, liveResult.Id, jobs[0].Id }, lookedUp.Select(job => job.JobId).ToArray());
            Assert.AreEqual(ServerJobLifecycleState.Pending, lookedUp.Single(job => job.JobId == liveResult.Id).LifecycleState);
            Assert.AreEqual("CSV row 2", lookedUp.Single(job => job.JobId == jobs[0].Id).SourceDetail);
            var followup = new SongJob(new SongQuery { Title = "Independent follow-up" });
            followup.EnsureDisplayId();
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new JobPersistenceMutation(
                runtimeId, ++sequence, DateTimeOffset.UtcNow, followup.Id, 1, PersistenceMutationPriority.Structural,
                followup.WorkflowId, null, liveExtract.Id, null, followup.DisplayId, "Song", "Terminal", "None", null,
                "Succeeded", "None", "None", "None", null, null, "Follow-up", null, 1, null)));
            await WaitForVisibleAsync(persistence, async () => await persistence.JobHistory!.GetJobAsync(followup.Id) != null);
            var sourceOnly = new JobQuery(null, null, null, null, IncludeAll: false, ExcludeExtractResults: true);
            Assert.IsTrue((await facade.GetJobsAsync(sourceOnly, null, 100)).Items.Any(job => job.JobId == followup.Id));
            Register(live, followup, liveExtract.Id);
            Assert.IsTrue(live.GetJobs(sourceOnly).Any(job => job.JobId == followup.Id),
                "A source reference to an earlier workflow must not hide an independent job.");

            Guid ownedSubmissionId = Guid.NewGuid();
            await persistence.Submissions!.CreateAsync(new SubmissionRegistration(
                ownedSubmissionId, DateTimeOffset.UtcNow, 1, "{}", null, null, null));
            var ownedParent = new JobList("New live parent") { WorkflowId = workflowId, SubmissionId = ownedSubmissionId };
            ownedParent.EnsureDisplayId();
            live.RegisterAcceptedRoot(CoreSnapshotFactory.CreateJob(ownedParent, 0), null);
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(retainedMutations[jobs[0].Id] with
            {
                JobId = ownedParent.Id, Sequence = ++sequence, DisplayId = ownedParent.DisplayId,
                Kind = "JobList", LifecycleState = "Pending", TerminalOutcome = "None", SourceDetail = null,
                SubmissionId = ownedSubmissionId,
            }));
            var children = new[] { new SongJob(new SongQuery { Title = "Owned A" }), new SongJob(new SongQuery { Title = "Owned B" }) };
            foreach (var child in children)
            {
                child.WorkflowId = workflowId;
                child.SubmissionId = ownedSubmissionId;
                child.EnsureDisplayId();
                Register(live, child, parentJobId: ownedParent.Id);
                Assert.IsTrue(persistence.MutationSink!.TryEnqueue(retainedMutations[jobs[0].Id] with
                {
                    JobId = child.Id, Sequence = ++sequence, ParentJobId = ownedParent.Id, DisplayId = child.DisplayId,
                    LifecycleState = "Pending", TerminalOutcome = "None", QueryText = child.Query.Title,
                    SubmissionId = ownedSubmissionId,
                }));
            }
            await WaitForVisibleAsync(persistence, async () =>
                await persistence.JobHistory!.GetJobAsync(children[1].Id) is not null);
            var childQuery = new JobQuery(null, null, ServerJobKind.Song, workflowId, IncludeAll: false, ParentJobId: ownedParent.Id);
            // Make history genuinely unreadable. An authoritative live scope
            // must remain useful without silently attempting a retained lookup.
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = SockseekDataPaths.ResolveDatabasePath(dataDirectory), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE jobs RENAME TO unavailable_jobs";
                await command.ExecuteNonQueryAsync();
                try
                {
                    var firstPage = await facade.GetJobsAsync(childQuery, null, 1);
                    Assert.AreEqual(children[0].Id, firstPage.Items.Single().JobId);
                    var secondPage = await facade.GetJobsAsync(childQuery, firstPage.NextCursor, 1);
                    Assert.AreEqual(children[1].Id, secondPage.Items.Single().JobId);
                    Assert.IsNull(secondPage.NextCursor);
                    var submissionPage = await facade.GetJobsAsync(new JobQuery(null, null, null, workflowId,
                        IncludeAll: true, SubmissionId: ownedSubmissionId), null, 100);
                    CollectionAssert.AreEqual(new[] { ownedParent.Id }.Concat(children.Select(child => child.Id)).ToArray(),
                        submissionPage.Items.Select(job => job.JobId).ToArray());
                    await Assert.ThrowsExactlyAsync<SqliteException>(() => facade.GetJobsAsync(
                        childQuery with { ParentJobId = Guid.NewGuid() }, null, 1));
                }
                finally
                {
                    command.CommandText = "ALTER TABLE unavailable_jobs RENAME TO jobs";
                    await command.ExecuteNonQueryAsync();
                }
            }
            // Unprocessed extractor outputs are retained but not runtime jobs.
            // Submission scope cannot claim complete live coverage of that result.
            var unprocessed = new SongJob(new SongQuery { Title = "Unprocessed result" })
                { WorkflowId = workflowId, SubmissionId = ownedSubmissionId };
            var extractor = new ExtractJob("unprocessed", Sockseek.Core.InputType.String)
                { WorkflowId = workflowId, SubmissionId = ownedSubmissionId, AutoProcessResult = false, Result = unprocessed };
            extractor.EnsureDisplayId();
            unprocessed.EnsureDisplayId();
            Register(live, extractor, parentJobId: ownedParent.Id);
            typeof(EngineStateStore).GetMethod("OnJobResultCreated", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(live, [new JobResultCreatedChange(2, DateTimeOffset.UtcNow,
                    CoreSnapshotFactory.CreateJob(extractor, 1), CoreSnapshotFactory.CreateJob(unprocessed, 1))]);
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(retainedMutations[jobs[0].Id] with
            {
                JobId = unprocessed.Id, Sequence = ++sequence, DisplayId = unprocessed.DisplayId,
                SubmissionId = ownedSubmissionId, LifecycleState = "Pending", TerminalOutcome = "None",
            }));
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(retainedMutations[jobs[0].Id] with
            {
                JobId = extractor.Id, Sequence = ++sequence, DisplayId = extractor.DisplayId,
                SubmissionId = ownedSubmissionId, ParentJobId = ownedParent.Id, ResultJobId = unprocessed.Id,
                Kind = "Extract", LifecycleState = "Pending", TerminalOutcome = "None",
                PayloadJson = RetainedJobPayloadCodec.Capture(CoreSnapshotFactory.CreateJob(extractor, 1).Payload),
            }));
            await WaitForVisibleAsync(persistence, async () => await persistence.JobHistory!.GetJobAsync(extractor.Id) is not null);
            var submissionQuery = new JobQuery(null, null, null, workflowId, IncludeAll: true, SubmissionId: ownedSubmissionId);
            CollectionAssert.AreEqual(new[] { ownedParent.Id }.Concat(children.Select(child => child.Id))
                .Concat(new[] { extractor.Id, unprocessed.Id }).ToArray(),
                (await facade.GetJobsAsync(submissionQuery, null, 100)).Items.Select(job => job.JobId).ToArray());

            // Partial retirement makes the parent's live index incomplete.
            // The retained sibling must still appear on its canonical child page.
            live.RetireJobProjections(workflowId, [children[0].Id]);
            var withRetainedChild = await facade.GetJobsAsync(childQuery, null, 100);
            CollectionAssert.AreEqual(children.Select(child => child.Id).ToArray(),
                withRetainedChild.Items.Select(job => job.JobId).ToArray());
            live.RetireJobProjections(workflowId, [ownedParent.Id, children[1].Id, extractor.Id]);
            Assert.AreEqual(0, live.GetJobs(submissionQuery).Count, "The submission index retires with its final runtime member.");

            // Complete a job after current rows are captured but before retained
            // rows are read. The initial frame and its queued patch must agree
            // on ordering; applying the buffered change must not reread SQL.
            var racing = new SearchJob("Completes during list read");
            racing.EnsureDisplayId();
            Register(live, racing);
            using var supervisor = new EngineSupervisor(options);
            await using var events = new ServerEventBroadcaster(live, supervisor,
                new global::Tests.Server.NoOpHubContext<ServerEventHub>());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var subscriptions = new List<IDisposable>();
            int reads = 0;
            using var diagnostics = DiagnosticListener.AllListeners.Subscribe(new CallbackObserver<DiagnosticListener>(listener =>
            {
                if (listener.Name != "Microsoft.EntityFrameworkCore") return;
                subscriptions.Add(listener.Subscribe(new CallbackObserver<KeyValuePair<string, object?>>(entry =>
                {
                    if (entry.Value is not CommandEventData command || entry.Key != RelationalEventId.CommandExecuting.Name
                        || command.Command.Connection?.DataSource != SockseekDataPaths.ResolveDatabasePath(dataDirectory)
                        || !command.Command.CommandText.Contains("FROM jobs job", StringComparison.Ordinal)) return;
                    if (++reads != 1) return;
                    racing.SetDone();
                    typeof(EngineStateStore).GetMethod("OnJobStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(live, [new JobStateChangedChange(1, DateTimeOffset.UtcNow,
                            CoreSnapshotFactory.CreateExecution(racing, 2))]);
                    live.FlushStateUpdates();
                })));
            }));
            try
            {
                await using var observation = facade.ObserveJobListAsync(new(
                    new(null, null, null, null, IncludeAll: false, NewestFirst: true), 2), events, deadline.Token).GetAsyncEnumerator();
                Assert.IsTrue(await observation.MoveNextAsync());
                Assert.AreEqual(ServerJobLifecycleState.Pending,
                    observation.Current.Jobs.Single(row => row.JobId == racing.Id).Added!.Lifecycle.LifecycleState);
                Assert.IsTrue(await observation.MoveNextAsync());
                Assert.AreEqual(ServerJobTerminalOutcome.Succeeded,
                    observation.Current.Jobs.Single(row => row.JobId == racing.Id).Lifecycle!.TerminalOutcome);
                Assert.AreEqual(1, reads, "Buffered fact changes must not reread retained pages.");
            }
            finally { foreach (var subscription in subscriptions) subscription.Dispose(); }

        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private sealed class CallbackObserver<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }

    private static void Register(EngineStateStore store, Job job, Guid? sourceJobId = null, Guid? parentJobId = null)
        => typeof(EngineStateStore)
            .GetMethod("OnJobRegistered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobRegisteredChange(
                1,
                DateTimeOffset.UtcNow,
                CoreSnapshotFactory.CreateJob(job, revision: 1),
                ParentJobId: parentJobId,
                SourceJobId: sourceJobId)]);

    private static TransferStateDto TimelineRow(
        Guid id,
        DateTimeOffset createdAtUtc,
        string state)
        => new(id, 1,
            new(null, null, "Upload", "SoulseekPeer", "peer", @"Share\File.bin", null),
            new(state, null, 0, false, TransferTerminalOutcome.None, Api.TransferFailureReason.None,
                TransferCancellationSource.None, []),
            new(0, 100, null), new(createdAtUtc, null), CreatedAtUtc: createdAtUtc);

    private static Task WaitForPersistedCountAsync(
        PersistenceCoordinator persistence,
        Guid workflowId,
        int expected)
        => WaitForVisibleAsync(persistence, async () =>
            (await persistence.JobHistory!.GetJobsAsync(new JobHistoryQuery(
                Limit: 100,
                WorkflowId: workflowId,
                IncludeAll: true))).Items.Count == expected);

    private static Task WaitForPersistedTransferAsync(
        PersistenceCoordinator persistence,
        Guid transferId)
        => WaitForVisibleAsync(persistence, async () =>
            await persistence.TransferHistory!.GetTransferAsync(transferId) != null);

    private static async Task WaitForVisibleAsync(PersistenceCoordinator persistence, Func<Task<bool>> isVisible)
    {
        var commits = Channel.CreateUnbounded<bool>();
        void Committed() => commits.Writer.TryWrite(true);
        persistence.HistoryHealthChanged += Committed;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // Subscribe before reading. Rejected-entry coverage is reported
            // after valid siblings commit, so it can change without another
            // commit. Wake on either outcome, never a polling timer.
            while (!await isVisible())
                await commits.Reader.ReadAsync(timeout.Token);
        }
        finally
        {
            persistence.HistoryHealthChanged -= Committed;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
