using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Sqlite;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class UploadPersistenceTests
{
    [TestMethod]
    public async Task TimelineObservation_FollowsRequestedExtentAndPreservesContinuation()
    {
        using var extent = new Activity("requested-transfer-extent").SetIdFormat(ActivityIdFormat.W3C).Start();
        int pageReads = 0;
        using var reads = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Sockseek.Server.Transfers",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == extent.TraceId && activity.OperationName == "timeline.page")
                    Interlocked.Increment(ref pageReads);
            },
        };
        ActivitySource.AddActivityListener(reads);
        var options = Options.Create(new ServerOptions { Persistence = new() { Enabled = false } });
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options);
        var state = supervisor.StateStore;
        await using var broadcaster = new ServerEventBroadcaster(state, supervisor,
            new global::Tests.Server.NoOpHubContext<ServerEventHub>());
        var queries = new TransferReadService(state, supervisor.Runtime, persistence);
        var started = DateTimeOffset.UtcNow;
        UploadTransferSnapshot Row(int ordinal) => new(Guid.NewGuid(), 1, "Peer", $@"Public\{ordinal}.bin", 100,
            started.AddSeconds(ordinal), UploadTransferState.InProgress, UploadFailureReason.None,
            UploadCancellationSource.None, 0, 0, null,
            new UploadAttemptSnapshot(Guid.NewGuid(), 1, started.AddSeconds(ordinal), null, 0, 0), null);
        var beforeQueue = await queries.GetTransferGroupSummariesAsync([]);
        var daemonPosition = state.GetReadPosition();
        state.UpdateUploadTransfer(Row(0) with { State = UploadTransferState.Queued, Attempt = null });
        var afterQueue = await queries.GetTransferGroupSummariesAsync([]);
        Assert.AreEqual(daemonPosition, state.GetReadPosition(), "Queued uploads are absent from the compact daemon stream.");
        Assert.IsTrue(afterQueue.ReadPosition!.Sequence > beforeQueue.ReadPosition!.Sequence,
            "Read freshness must still advance for transfer facts outside that stream.");
        var rows = Enumerable.Range(1, 4).Select(Row).ToArray();
        foreach (var row in rows.Take(3)) state.UpdateUploadTransfer(row);
        var full = state.GetDaemonSnapshot();
        var summaryOnly = state.GetDaemonSnapshot(detail: DaemonStateDetail.Overview);
        Assert.AreEqual(3, full.Transfers.Count);
        Assert.AreEqual(0, summaryOnly.Transfers.Count);
        Assert.AreEqual(full.TransferSummary, summaryOnly.TransferSummary);
        Assert.AreEqual(full.Position, summaryOnly.Position);
        var observationTime = Stopwatch.StartNew();
        var phases = new List<(string Name, long Milliseconds)>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var updates = queries.ObserveTransferTimelineAsync(new(
            TransferCommandDirection.Upload, 2,
            new Dictionary<Guid, long> { [rows[0].TransferId] = 1, [rows[1].TransferId] = 1, [rows[2].TransferId] = 1 },
            AdditionalTransferIds: [rows[0].TransferId]),
            broadcaster, cancellation.Token).GetAsyncEnumerator();
        async ValueTask<bool> Next(string phase)
        {
            phases.Add((phase, observationTime.ElapsedMilliseconds));
            // This case checks fetched membership and final handoff. Clock
            // delivery is covered separately; do not spend its deadline waiting
            // for a real timer under parallel test load.
            state.FlushStateUpdates();
            try { return await updates.MoveNextAsync(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Console.WriteLine($"Timeline observation timeout after {observationTime.ElapsedMilliseconds} ms; "
                    + $"phase starts: {string.Join(", ", phases.Select(value => $"{value.Name}={value.Milliseconds}ms"))}; "
                    + $"page reads={pageReads}; pool threads={ThreadPool.ThreadCount}, pending={ThreadPool.PendingWorkItemCount}");
                throw;
            }
        }

        Assert.IsTrue(await Next("initial"));
        CollectionAssert.AreEqual(new[] { rows[2].TransferId, rows[1].TransferId, rows[0].TransferId },
            updates.Current.WindowTransferIds!.ToArray());
        Assert.AreEqual(0, updates.Current.Items.Count, "Known revisions must not be sent again.");
        Assert.IsTrue(updates.Current.Changes is null or { Count: 0 },
            "A known canonical row needs no separate live seed.");
        state.UpdateUploadTransfer(rows[2] with { Revision = 2, BytesTransferred = 25 });
        Assert.IsTrue(await Next("progress"));
        Assert.AreEqual(0, updates.Current.Items.Count, "Byte progress must not replace history rows.");
        var progress = updates.Current.Changes!.Single();
        Assert.AreEqual(rows[2].TransferId, progress.TransferId);
        Assert.IsNull(progress.Added);
        Assert.IsNull(progress.Status);
        Assert.AreEqual(25L, progress.Progress!.BytesTransferred);
        state.UpdateUploadTransfer(rows[3]);
        state.UpdateUploadTransfer(rows[3] with { Revision = 2, BytesTransferred = 50 });
        Assert.IsTrue(await Next("arrival"));
        Assert.AreEqual(50L, updates.Current.Items.Single(row => row.TransferId == rows[3].TransferId).Progress.BytesTransferred,
            "Progress before an arrival's first flush must be included without a later event or another state read.");
        Assert.IsNull(updates.Current.WindowTransferIds, "Subsequent changes must not retransmit the whole extent.");
        CollectionAssert.AreEquivalent(new[] { rows[3].TransferId },
            updates.Current.Items.Select(row => row.TransferId).ToArray());
        Assert.AreEqual(rows[1].TransferId, updates.Current.RemovedTransferIds.Single());
        Assert.AreEqual(rows[2].TransferId, updates.Current.BoundaryTransferId);
        Assert.AreEqual(1, pageReads,
            "New arrivals and progress must reuse the fetched prefix instead of rereading unchanged history.");
        var continuation = await queries.GetTransfersAsync(new(Cursor: updates.Current.NextCursor, Limit: 2, Direction: "Upload"), cancellation.Token);
        CollectionAssert.AreEqual(new[] { rows[1].TransferId, rows[0].TransferId },
            continuation.Items.Select(row => row.TransferId).ToArray());

        // A member moving behind the boundary needs an older successor, which
        // may no longer belong to this reader's cached prefix.
        state.UpdateUploadTransfer(rows[2] with { Revision = 3, RequestedAtUtc = started.AddSeconds(-1) });
        Assert.IsTrue(await Next("moved-boundary"));
        Assert.AreEqual(rows[2].TransferId, updates.Current.RemovedTransferIds.Single());
        Assert.IsTrue(updates.Current.Items.Any(row => row.TransferId == rows[1].TransferId));
        Assert.AreEqual(rows[1].TransferId, updates.Current.BoundaryTransferId);
        Assert.AreEqual(2, pageReads, "A changed boundary must backfill from the canonical sources.");

        // Explicitly fetched older members retain their final facts even if a
        // slow reader misses several newer completions and history is disabled.
        state.UpdateUploadTransfer(rows[0] with { Revision = 2, State = UploadTransferState.Completed,
            BytesTransferred = 100, FinishedAtUtc = started.AddSeconds(8) });
        state.RemoveTerminalTransfer(rows[0].TransferId);
        foreach (int ordinal in Enumerable.Range(5, 3))
        {
            var completed = Row(ordinal) with { State = UploadTransferState.Completed,
                BytesTransferred = 100, FinishedAtUtc = started.AddSeconds(8) };
            state.UpdateUploadTransfer(completed);
            state.RemoveTerminalTransfer(completed.TransferId);
        }
        Assert.IsTrue(await Next("final-eviction"));
        var included = updates.Current.Items.Single(row => row.TransferId == rows[0].TransferId);
        Assert.AreEqual(TransferTerminalOutcome.Succeeded, included.Status.TerminalOutcome);
        Assert.AreEqual(100L, included.Progress.BytesTransferred);
        Assert.IsNull(updates.Current.WindowTransferIds);
        Assert.AreEqual(2, pageReads, "Completion and runtime eviction must not reread unchanged retained rows.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TimelineObservation_RemovesMembersThatLeaveItsFilter(bool changeUsername)
    {
        var options = Options.Create(new ServerOptions { Persistence = new() { Enabled = false } });
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options);
        var state = supervisor.StateStore;
        await using var events = new ServerEventBroadcaster(state, supervisor,
            new global::Tests.Server.NoOpHubContext<ServerEventHub>());
        var queries = new TransferReadService(state, supervisor.Runtime, persistence);
        var row = new UploadTransferSnapshot(Guid.NewGuid(), 1, "Peer", @"Share\One.bin", 100,
            DateTimeOffset.UtcNow, UploadTransferState.InProgress, UploadFailureReason.None,
            UploadCancellationSource.None, 0, 0, null, null, null, GroupRef: "Folder");
        state.UpdateUploadTransfer(row);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var observed = queries.ObserveTransferTimelineAsync(new(
            TransferCommandDirection.Upload, Username: "Peer", GroupRef: "Folder"), events, deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await observed.MoveNextAsync());
        Assert.AreEqual(row.TransferId, observed.Current.Items.Single().TransferId);
        state.UpdateUploadTransfer(row with { Revision = 2,
            Username = changeUsername ? "Other" : "Peer", GroupRef = changeUsername ? "Folder" : "Other" });
        Assert.IsTrue(await observed.MoveNextAsync());
        Assert.AreEqual(row.TransferId, observed.Current.RemovedTransferIds.Single());
        Assert.IsNull(observed.Current.NextCursor);
        state.UpdateUploadTransfer(row with { Revision = 3 });
        Assert.IsTrue(await observed.MoveNextAsync());
        Assert.AreEqual(row.TransferId, observed.Current.Items.Single().TransferId);
        Assert.AreEqual("Peer", observed.Current.Items.Single().Identity.Username);
        Assert.AreEqual("Folder", observed.Current.Items.Single().Identity.GroupRef);
    }

    [TestMethod]
    public async Task TimelineObservation_PreservesFinalFactsWithoutPersistenceAndStopsOnCancellation()
    {
        var options = Options.Create(new ServerOptions { Persistence = new() { Enabled = false } });
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options);
        var state = supervisor.StateStore;
        await using var broadcaster = new ServerEventBroadcaster(state, supervisor,
            new global::Tests.Server.NoOpHubContext<ServerEventHub>());
        var queries = new TransferReadService(state, supervisor.Runtime, persistence);
        using var cancellation = new CancellationTokenSource();
        await using var updates = queries.ObserveTransferTimelineAsync(
            new(TransferCommandDirection.Upload), broadcaster, cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync());
        Assert.AreEqual(TransferRetainedCoverageState.Unavailable, updates.Current.RetainedCoverage.State);
        await using var downloads = queries.ObserveTransferTimelineAsync(
            new(TransferCommandDirection.Download), broadcaster, cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await downloads.MoveNextAsync());
        var nextDownload = downloads.MoveNextAsync().AsTask();
        var started = DateTimeOffset.UtcNow;
        var active = new UploadTransferSnapshot(Guid.NewGuid(), 1, "Peer", @"Public\One.bin", 100,
            started, UploadTransferState.InProgress, UploadFailureReason.None, UploadCancellationSource.None,
            0, 0, null, new UploadAttemptSnapshot(Guid.NewGuid(), 1, started, null, 0, 0), null);
        state.UpdateUploadTransfer(active);
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("InProgress", updates.Current.Items.Single().Status.State);
        var next = updates.MoveNextAsync().AsTask();
        state.UpdateUploadTransfer(active with { Revision = 2, BytesTransferred = 50 });
        state.UpdateUploadTransfer(active with { Revision = 3, State = UploadTransferState.Completed,
            BytesTransferred = 100, FinishedAtUtc = started.AddSeconds(1) });
        state.RemoveTerminalTransfer(active.TransferId);
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        var final = updates.Current.Items.Single();
        Assert.AreEqual(TransferTerminalOutcome.Succeeded, final.Status.TerminalOutcome);
        Assert.AreEqual(100L, final.Progress.BytesTransferred);
        Assert.AreEqual(started.AddSeconds(1).ToUnixTimeMilliseconds(), final.Status.CompletedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.IsNull(state.GetLiveTransfer(active.TransferId));
        Assert.IsFalse(nextDownload.IsCompleted,
            "Upload arrivals and retirement must not produce updates for a Downloads reader.");
        var waiting = updates.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<OperationCanceledException>(() => nextDownload);
    }

    [TestMethod]
    public async Task RecordingAnExistingUploadRetainsItsCurrentAttempt()
    {
        var catalog = new FakeCatalog();
        var protocol = new BlockingProtocol();
        await using var coordinator = new UploadCoordinator(
            catalog,
            protocol,
            new PeerRestrictionPolicy(new PeerRestrictionSettings()),
            new UploadScheduler(new UploadSettings { Slots = 1 }));

        UploadCoordinatorAdmission active = await coordinator.AdmitAsync(
            "alice",
            null,
            @"Public\One.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sink = new RecordingSink();
        var state = new EngineStateStore();
        state.SetTransferPersistence(new TransferPersistence(Guid.NewGuid(), sink));
        coordinator.TransferChanged += state.UpdateUploadTransfer;
        foreach (var snapshot in coordinator.Snapshot()) state.UpdateUploadTransfer(snapshot);

        TransferPersistenceMutation hydrated = sink.Items
            .OfType<TransferPersistenceMutation>()
            .Single(mutation => mutation.TransferId == active.TransferId);
        Assert.AreEqual("InProgress", hydrated.State);
        Assert.AreEqual("One.bin", hydrated.File?.Name);
        Assert.AreEqual(coordinator.Snapshot().Single(row => row.TransferId == active.TransferId).GroupRef,
            hydrated.GroupRef);
        Assert.AreEqual(@"Public", hydrated.GroupDisplayPath);
        Assert.IsNotNull(hydrated.StartedAtUtc);
        Assert.IsNotNull(hydrated.AccountingObservations);
        Assert.AreEqual(0L, hydrated.AccountingObservations.Single().CumulativeBytes);

        coordinator.TransferChanged -= state.UpdateUploadTransfer;
        protocol.Release.TrySetResult();
    }

    [TestMethod]
    public async Task QueuedCancellationPersistsZeroAttemptsAndDispatchPersistsExactlyOne()
    {
        var catalog = new FakeCatalog();
        var protocol = new BlockingProtocol();
        await using var coordinator = new UploadCoordinator(
            catalog,
            protocol,
            new PeerRestrictionPolicy(new PeerRestrictionSettings()),
            new UploadScheduler(new UploadSettings { Slots = 1 }));
        var sink = new RecordingSink();
        var state = new EngineStateStore();
        state.SetTransferPersistence(new TransferPersistence(Guid.NewGuid(), sink));
        coordinator.TransferChanged += state.UpdateUploadTransfer;
        foreach (var snapshot in coordinator.Snapshot()) state.UpdateUploadTransfer(snapshot);

        UploadCoordinatorAdmission active = await coordinator.AdmitAsync(
            "alice",
            null,
            @"Public\One.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        UploadCoordinatorAdmission queued = await coordinator.AdmitAsync(
            "bob",
            null,
            @"Public\Two.bin");

        Assert.IsTrue(coordinator.Cancel(queued.TransferId!.Value));
        TransferTerminalPersistenceMutation queuedTerminal = await sink.WaitForTerminalAsync(
            queued.TransferId.Value);
        Assert.AreEqual(0, queuedTerminal.Transfer.AttemptCount);
        Assert.IsNull(queuedTerminal.FinalAttempt);

        protocol.Release.TrySetResult();
        TransferTerminalPersistenceMutation activeTerminal = await sink.WaitForTerminalAsync(
            active.TransferId!.Value);
        Assert.AreEqual(1, activeTerminal.Transfer.AttemptCount);
        Assert.IsNotNull(activeTerminal.FinalAttempt);
        Assert.AreEqual(1, activeTerminal.FinalAttempt.AttemptNumber);

        coordinator.TransferChanged -= state.UpdateUploadTransfer;
    }

    [TestMethod]
    public async Task QueuedCancellationRemainsConsistentUntilDurableHandoff()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-queued-handoff-" + Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new() { Enabled = true, DataDirectory = directory, RetentionEnabled = false },
        }));
        var protocol = new BlockingProtocol();
        await using var uploads = new UploadCoordinator(new FakeCatalog(), protocol,
            new PeerRestrictionPolicy(new PeerRestrictionSettings()), new UploadScheduler(new UploadSettings { Slots = 1 }));
        var state = new EngineStateStore();
        using var supervisor = new EngineSupervisor(Options.Create(new ServerOptions()));
        await using var broadcaster = new ServerEventBroadcaster(state, supervisor,
            new global::Tests.Server.NoOpHubContext<ServerEventHub>());
        var client = new DaemonClientStore();
        client.ApplySnapshot(state.GetDaemonSnapshot());
        state.StateBatchPublished += batch => client.Apply(batch.ToDto());
        var terminalPublished = new TaskCompletionSource<UploadTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<UploadTransferSnapshot> observe = snapshot =>
        {
            state.UpdateUploadTransfer(snapshot);
            if (snapshot.Attempt is null && snapshot.State == UploadTransferState.Cancelled)
                terminalPublished.TrySetResult(snapshot);
        };
        await persistence.StartAsync(CancellationToken.None);
        state.SetTransferPersistence(persistence.CreateTransferPersistence());
        uploads.TransferChanged += observe;
        var commits = Channel.CreateUnbounded<bool>();
        void Committed() => commits.Writer.TryWrite(true);
        persistence.Health!.CommitCompleted += Committed;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task WaitForStored(Guid id, string expected)
        {
            while ((await persistence.TransferHistory!.GetTransferAsync(id))?.Transfer.State != expected)
                await commits.Reader.ReadAsync(deadline.Token);
        }
        try
        {
            var active = await uploads.AdmitAsync("Peer", null, @"Public\One.bin");
            await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = await uploads.AdmitAsync("Peer", null, @"Public\Two.bin");
            Guid queuedId = queued.TransferId!.Value;
            await WaitForStored(queuedId, "Queued");
            Assert.IsFalse(client.GetTransfers().Any(row => row.TransferId == queuedId),
                "Ordinary queued entries must stay out of the compact daemon stream.");
            var queries = new TransferReadService(state, runtime: null!, persistence);
            await using var updates = queries.ObserveTransferTimelineAsync(
                new(TransferCommandDirection.Upload), broadcaster, deadline.Token).GetAsyncEnumerator();
            Assert.IsTrue(await updates.MoveNextAsync());
            Assert.IsNotNull(updates.Current.ReadPosition);
            Task<TransferTimelinePageDto> Timeline(string? outcome = null, string? filter = null)
                => queries.GetTransfersAsync(new(Limit: 500, Direction: "Upload", State: filter, TerminalOutcome: outcome, Username: "Peer"));
            UploadTransferSnapshot cancelled;
            Task<TransferCommandReceiptDto> archive;
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SockseekDataPaths.ResolveDatabasePath(directory) }.ToString()))
            {
                await connection.OpenAsync();
                await using var held = connection.BeginTransaction(deferred: false);
                Assert.IsTrue(uploads.Cancel(queuedId));
                cancelled = await terminalPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsNull(cancelled.Attempt);
                Assert.AreEqual(UploadTransferState.InProgress, uploads.Snapshot().Single(row => row.TransferId == active.TransferId).State);
                Assert.AreEqual("Queued", (await persistence.TransferHistory!.GetTransferAsync(queuedId))!.Transfer.State);
                var exact = await queries.LookupTransfersAsync(new([queuedId], "Upload"));
                var groups = await queries.GetTransferGroupSummariesAsync([
                    new(TransferCommandDirection.Upload, "Peer", cancelled.GroupRef!),
                ]);
                Assert.AreEqual(1L, groups.Items.Single().CancelledCount);
                Assert.AreEqual("Cancelled", exact.Items.Single().Status.State);
                var timeline = await Timeline();
                var row = timeline.Items.Single(item => item.TransferId == queuedId);
                Assert.AreEqual("Cancelled", row.Status.State, "The timeline must agree with exact lookup and group counts before commit.");
                Assert.AreEqual(0, row.Status.AttemptCount);
                Assert.AreEqual(cancelled.Revision, row.Revision);
                Assert.AreEqual(cancelled.FinishedAtUtc!.Value.ToUnixTimeMilliseconds(), row.Status.CompletedAtUtc!.Value.ToUnixTimeMilliseconds());
                do { Assert.IsTrue(await updates.MoveNextAsync()); }
                while (!updates.Current.Items.Any(item => item.TransferId == queuedId));
                var observed = updates.Current.Items.Single(item => item.TransferId == queuedId);
                Assert.AreEqual(row, observed,
                    "The observation must resolve the terminal row before commit, using the same contract as HTTP.");
                var terminalState = state.GetLiveTransfer(queuedId)!;
                state.UpdateUploadTransfer(cancelled with { Revision = cancelled.Revision - 1, State = UploadTransferState.Queued });
                Assert.AreSame(terminalState, state.GetLiveTransfer(queuedId));
                Assert.AreEqual(1, (await Timeline("Cancelled")).Items.Count);
                Assert.AreEqual(0, (await Timeline(filter: "Queued")).Items.Count,
                    "A retained queued predecessor cannot bypass the current-state filter.");
                state.FlushStateUpdates();
                Assert.AreEqual(TransferTerminalOutcome.Cancelled, client.GetTransfers().Single(item => item.TransferId == queuedId).Status.TerminalOutcome);
                archive = queries.SetTransfersArchivedAsync(new TransferArchiveFilter(Direction: "Upload", TerminalOutcome: "Cancelled"), true);
                Assert.IsFalse(archive.IsCompleted, "Archival must wait for the terminal handoff, without blocking reads.");
                held.Rollback();
            }
            await WaitForStored(queuedId, "Cancelled");
            var receipt = await archive.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, receipt.SucceededCount);
            Assert.AreEqual(0, (await Timeline("Cancelled")).Items.Count);
            var archived = await queries.GetTransfersAsync(new(Limit: 500, Direction: "Upload", TerminalOutcome: "Cancelled", Username: "Peer", Archived: true));
            Assert.AreEqual(queuedId, archived.Items.Single().TransferId);
            Assert.AreEqual(cancelled.Revision, archived.Items.Single().Revision);
            Assert.AreEqual(cancelled.FinishedAtUtc!.Value.ToUnixTimeMilliseconds(), archived.Items.Single().Status.CompletedAtUtc!.Value.ToUnixTimeMilliseconds());
            Assert.IsNull(state.GetLiveTransfer(queuedId));
            state.FlushStateUpdates();
            Assert.IsFalse(client.GetTransfers().Any(row => row.TransferId == queuedId));
            do { Assert.IsTrue(await updates.MoveNextAsync()); }
            while (!updates.Current.RemovedTransferIds.Contains(queuedId));

            // A read can capture the old live snapshot before a newer durable
            // completion commits. The newer revision must win even with filters.
            var stale = uploads.Snapshot().Single(row => row.TransferId == active.TransferId);
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new TransferPersistenceMutation(
                persistence.Runtime!.RuntimeId, 100, DateTimeOffset.UtcNow,
                active.TransferId!.Value, 100, PersistenceMutationPriority.Terminal,
                null, null, "Upload", "SoulseekPeer", "Peer", stale.RemotePath, null,
                "Completed", "Succeeded", stale.SizeBytes, stale.SizeBytes, 1, "None", null,
                GroupRef: stale.GroupRef, GroupDisplayPath: stale.GroupDisplayPath)));
            await WaitForStored(active.TransferId.Value, "Completed");
            Assert.AreEqual("InProgress", state.GetLiveTransfer(active.TransferId.Value)!.Status.State);
            var newer = await Timeline("Succeeded");
            Assert.AreEqual(1, newer.Items.Count, "A newer durable completion must survive the old status filter.");
            Assert.AreEqual(active.TransferId.Value, newer.Items.Single().TransferId,
                "An old captured status cannot exclude a newer retained completion.");
            Assert.AreEqual(100L, newer.Items.Single().Revision);
            Assert.AreEqual(0, (await Timeline(filter: "InProgress")).Items.Count,
                "The old live status must not resurrect a newer durable completion in active filters.");
        }
        finally
        {
            protocol.Release.TrySetResult();
            await uploads.DisposeAsync();
            uploads.TransferChanged -= observe;
            persistence.Health!.CommitCompleted -= Committed;
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task RejectedAttemptAdmission_IsRetriedByTheNextTransferUpdate()
    {
        var catalog = new FakeCatalog();
        var protocol = new BlockingProtocol();
        await using var coordinator = new UploadCoordinator(
            catalog,
            protocol,
            new PeerRestrictionPolicy(new PeerRestrictionSettings()),
            new UploadScheduler(new UploadSettings { Slots = 1 }));
        var sink = new RejectFirstAttemptSink();
        var state = new EngineStateStore();
        state.SetTransferPersistence(new TransferPersistence(Guid.NewGuid(), sink));
        coordinator.TransferChanged += state.UpdateUploadTransfer;
        foreach (var snapshot in coordinator.Snapshot()) state.UpdateUploadTransfer(snapshot);

        await coordinator.AdmitAsync("alice", null, @"Public\One.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, sink.AttemptAdmissions);
        Assert.AreEqual(1, sink.AcceptedAttempts);

        coordinator.TransferChanged -= state.UpdateUploadTransfer;
        protocol.Release.TrySetResult();
    }

    private sealed class RecordingSink : IPersistenceMutationSink
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<TransferTerminalPersistenceMutation>> terminals = [];
        private readonly ConcurrentQueue<PersistenceMutation> items = [];

        public IReadOnlyList<PersistenceMutation> Items => items.ToArray();

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            items.Enqueue(mutation);
            if (mutation is TransferTerminalPersistenceMutation terminal)
            {
                terminals.GetOrAdd(
                        terminal.Transfer.TransferId,
                        static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult(terminal);
            }
            return true;
        }

        public Task<TransferTerminalPersistenceMutation> WaitForTerminalAsync(Guid transferId)
            => terminals.GetOrAdd(
                    transferId,
                    static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task
                .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RejectFirstAttemptSink : IPersistenceMutationSink
    {
        private int attemptAdmissions;
        private int acceptedAttempts;

        public int AttemptAdmissions => Volatile.Read(ref attemptAdmissions);
        public int AcceptedAttempts => Volatile.Read(ref acceptedAttempts);

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            if (mutation is not TransferAttemptPersistenceMutation)
                return true;

            if (Interlocked.Increment(ref attemptAdmissions) == 1)
                return false;

            Interlocked.Increment(ref acceptedAttempts);
            return true;
        }
    }

    private sealed class BlockingProtocol : IUploadProtocolInvoker
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Completion is deliberately synchronous: this fake controls the exact
        // handoff being asserted and must not depend on a saturated CI thread pool
        // scheduling the protocol continuation within an arbitrary timeout.
        public TaskCompletionSource Release { get; } = new();

        public async Task<UploadProtocolOutcome> UploadAsync(
            string username,
            string remotePath,
            long sizeBytes,
            Func<long, Task<Stream>> streamFactory,
            Action initializingCompleted,
            Action<long, double> progress,
            CancellationToken cancellationToken)
        {
            initializingCompleted();
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return UploadProtocolOutcome.Cancelled;
        }
    }

    private sealed class FakeCatalog : IShareCatalogProvider, IShareCatalogReader
    {
        private readonly Dictionary<RemotePathKey, ShareCatalogResolvedFile> files;

        public FakeCatalog()
        {
            var root = new ShareCatalogRoot(
                1,
                "Public",
                Path.GetTempPath(),
                RemotePathKey.CreateAlias("Public"));
            files = new[]
                {
                    CreateFile(root, 1, @"Public\One.bin"),
                    CreateFile(root, 2, @"Public\Two.bin"),
                }
                .ToDictionary(item => item.File.ComparisonPath);
        }

        public ShareCatalogMetadata Metadata { get; } = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "settings",
            1,
            2,
            200,
            ShareBrowseStatus.Ready,
            ShareCatalogVersions.BrowseWire,
            1,
            new string('A', 64));

        public bool TryAcquire(out IShareCatalogLease? lease)
        {
            lease = new FakeLease(this);
            return true;
        }

        public ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                files.TryGetValue(remotePath, out ShareCatalogResolvedFile? file)
                    ? file
                    : null);

        public ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ShareCatalogFile>>([]);

        public ValueTask<ShareCatalogBrowseDirectory?> GetDirectoryAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ShareCatalogBrowseDirectory?>(null);

        public async IAsyncEnumerable<ShareCatalogBrowseDirectory> EnumerateBrowseAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ShareCatalogResolvedFile CreateFile(
            ShareCatalogRoot root,
            long id,
            string remotePath)
        {
            string name = remotePath[(remotePath.LastIndexOf('\\') + 1)..];
            var file = new ShareCatalogFile(
                id,
                root.RootId,
                1,
                name,
                remotePath,
                RemotePathKey.Create(remotePath),
                name,
                100,
                DateTimeOffset.UnixEpoch,
                1,
                "bin",
                []);
            return new ShareCatalogResolvedFile(root, file);
        }

        private sealed class FakeLease(IShareCatalogReader reader) : IShareCatalogLease
        {
            public IShareCatalogReader Reader { get; } = reader;
            public ShareCatalogMetadata Metadata => Reader.Metadata;
            public ShareBrowseStream OpenBrowseStream(
                TimeSpan idleTimeout,
                Action? releasePermit = null) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
