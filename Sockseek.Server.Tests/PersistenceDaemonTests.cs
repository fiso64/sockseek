using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Services;
using Sockseek.Core.Events;
using Sockseek.Core.Settings;
using Sockseek.Server.Planning;
using Sockseek.Server.Persistence;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PersistenceDaemonTests
{
    [TestMethod]
    public async Task RetiredJobMetadataRemainsReadableWhileItsDurableCommitIsHeld()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-readable-handoff-" + Guid.NewGuid().ToString("N"));
        var peer = global::Tests.Server.SoulseekClientProxy.Create();
        var searchesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSearches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int searchCount = 0;
        peer.SearchHandler = async (_, ct) =>
        {
            if (Interlocked.Increment(ref searchCount) == 2) searchesStarted.TrySetResult();
            await releaseSearches.Task.WaitAsync(ct);
        };
        await using var app = global::Tests.TestServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings { Username = "test", Password = "test", LogLevel = Microsoft.Extensions.Logging.LogLevel.None },
            OperatorDownloadSettings = new DownloadSettingsPatchDto(Output: new OutputSettingsPatchDto(ParentDir: Path.Combine(directory, "out"))),
            Persistence = new ServerPersistenceOptions { Enabled = true, DataDirectory = directory, RetentionEnabled = false },
            ClientFactory = _ => peer.Client,
        }, "http://127.0.0.1:0");
        var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
        var created = new TaskCompletionSource<Sockseek.Core.DownloadEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.EngineCreated += engine => created.TrySetResult(engine);
        try
        {
            await app.StartAsync();
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var client = new SockseekApiClient(http);
            var engine = await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid workflowId = Guid.Empty;
            // The typed event precedes completion of publication to the persistence
            // adapter. Probe the handoff only after that publication has returned.
            engine.Events.ChangePublished += change =>
            {
                if (change is WorkflowRetiredChange retirement && retirement.WorkflowId == workflowId)
                    retired.TrySetResult();
            };
            supervisor.StateStore.StateBatchPublished += source =>
            {
                var batch = source.ToDto();
                if (workflowId != Guid.Empty && batch.Scope == StateStreamScopeDto.Workflow(workflowId)
                    && batch.State.RemovedWorkflowIds.Contains(workflowId)) removed.TrySetResult();
            };
            await using var fullClient = new SockseekLiveClient(http);
            var fullChildren = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fullClient.Updated += _ =>
            {
                if (fullClient.Store.GetJobs().Count(job => job.ParentJobId != null) == 2) fullChildren.TrySetResult();
            };
            await fullClient.StartDaemonAsync();
            var root = await supervisor.Submissions.SubmitJobListAsync(new SubmitJobListRequestDto("held metadata",
                [new GenericSearchJobDraftDto("held one"), new GenericSearchJobDraftDto("held two")]), CancellationToken.None);
            var expectedStats = new JobSummaryStatsDto(MemberCount: 2, CompletedMemberCount: 2, FailedMemberCount: 0, SkippedMemberCount: 0);
            workflowId = root.WorkflowId;
            await searchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fullChildren.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(3, fullClient.Store.GetJobs().Count, "The full SDK stream still carries children.");
            await using var overviewClient = new SockseekLiveClient(http, detail: DaemonStateDetail.Overview);
            await overviewClient.StartDaemonAsync();
            Assert.AreEqual(root.JobId, overviewClient.Store.GetJobs().Single().JobId);
            Guid observedChild = fullClient.Store.GetJobs().First(job => job.ParentJobId == root.JobId).JobId;
            using var observationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var observed = overviewClient.ObserveJobsAsync(root.JobId, [observedChild], observationDeadline.Token)
                .GetAsyncEnumerator();
            Assert.IsTrue(await observed.MoveNextAsync());
            CollectionAssert.AreEquivalent(new[] { root.JobId, observedChild },
                observed.Current.State.Jobs.Select(job => job.JobId).ToArray());
            Assert.AreEqual(2, observed.Current.ChildCount);
            var listScope = new JobListScopeDto(new(null, null, null, null, IncludeAll: false, NewestFirst: true));
            await using var listed = overviewClient.ObserveJobListAsync(listScope, observationDeadline.Token)
                .GetAsyncEnumerator();
            Assert.IsTrue(await listed.MoveNextAsync());
            CollectionAssert.AreEqual(new[] { root.JobId }, listed.Current.WindowJobIds!.ToArray());
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                Pooling = false,
                DataSource = SockseekDataPaths.ResolveDatabasePath(directory),
            }.ToString());
            await connection.OpenAsync();
            await using (var held = connection.BeginTransaction(deferred: false))
            {
                releaseSearches.TrySetResult();
                await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bool finalObserved = false;
                while (!finalObserved && await observed.MoveNextAsync())
                    finalObserved = observed.Current.State.Jobs.Any(job => job.JobId == observedChild
                        && (job.Lifecycle?.LifecycleState ?? job.Added?.Lifecycle.LifecycleState) == ServerJobLifecycleState.Terminal);
                Assert.IsTrue(finalObserved, "The SDK's scoped final facts do not wait for a held SQL commit.");
                bool finalListed = false;
                while (!finalListed && await listed.MoveNextAsync())
                {
                    Assert.AreEqual(0, listed.Current.RemovedJobIds.Count);
                    finalListed = listed.Current.Jobs.Any(job => job.JobId == root.JobId
                        && job.Lifecycle?.LifecycleState == ServerJobLifecycleState.Terminal);
                }
                Assert.IsTrue(finalListed, "The displayed Jobs list must advance without waiting for SQL.");
                Assert.IsNotNull(supervisor.StateStore.GetJobDetail(root.JobId),
                    "Do not discard the readable final projection before its durable replacement is ready.");
                var metadata = app.Services.GetRequiredService<PersistenceCoordinator>()
                    .WaitForWorkflowHandoffAsync(workflowId, CancellationToken.None, includeSearchResults: false);
                Assert.IsFalse(metadata.IsCompleted, "The writer must still be held before the read probe.");
                var queries = app.Services.GetRequiredService<JobReadService>();
                var detail = await queries.GetJobAsync(root.JobId).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(detail);
                Assert.AreEqual(ServerJobLifecycleState.Terminal, detail.Summary.LifecycleState);
                Assert.AreEqual(2, detail.ChildCount);
                Assert.AreEqual(expectedStats, detail.Summary.Stats);
                var children = await queries.GetJobsAsync(new JobQuery(null, null, null, workflowId,
                    ParentJobId: root.JobId, IncludeAll: true), null, 100).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(2, children.Items.Count);
                var summaries = await client.LookupJobsAsync(new JobLookupRequestDto(
                    [root.JobId, children.Items[0].JobId, Guid.NewGuid(), root.JobId])).WaitAsync(TimeSpan.FromSeconds(5));
                CollectionAssert.AreEqual(new[] { root.JobId, children.Items[0].JobId }, summaries.Select(job => job.JobId).ToArray());
                Assert.AreEqual(expectedStats, summaries[0].Stats);
                Assert.IsTrue(children.Items.All(job => job.LifecycleState == ServerJobLifecycleState.Terminal));
                var global = await queries.GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true), null, 100)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(3, global.Items.Count);
                Assert.IsNotNull(await queries.GetWorkflowAsync(workflowId).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(1, (await queries.GetWorkflowsAsync(null, 100).WaitAsync(TimeSpan.FromSeconds(5))).Items.Count);
                Assert.IsNotNull(await queries.GetJobByDisplayIdAsync(workflowId, root.DisplayId).WaitAsync(TimeSpan.FromSeconds(5)));
                var submission = await queries.GetSubmissionAsync(root.SubmissionId!.Value).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(submission);
                Assert.AreEqual(3, submission.Summary.TotalJobCount);
                Assert.AreEqual(0, submission.Summary.ActiveJobCount);
                Assert.AreEqual(3, (await queries.GetSubmissionsAsync(null, 100).WaitAsync(TimeSpan.FromSeconds(5)))
                    .Items.Single(item => item.SubmissionId == root.SubmissionId).TotalJobCount);
                Assert.IsFalse(removed.Task.IsCompleted,
                    $"Read ownership must last until acknowledgement; metadata={metadata.Status}, error={metadata.Exception?.GetBaseException().Message}.");
                held.Rollback();
            }
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retainedSummaries = await client.LookupJobsAsync(new JobLookupRequestDto([root.JobId]));
            Assert.AreEqual(expectedStats, retainedSummaries.Single().Stats);
            Assert.IsNull(supervisor.StateStore.GetJobDetail(root.JobId));
            var retained = await app.Services.GetRequiredService<JobReadService>().GetJobAsync(root.JobId);
            Assert.IsNotNull(retained);
            Assert.AreEqual(2, retained.ChildCount);
            Assert.AreEqual(ServerJobLifecycleState.Terminal, retained.Summary.LifecycleState);
            Assert.AreEqual(expectedStats, retained.Summary.Stats,
                "Read ownership must not change the public completion counts.");
            var snapshot = await client.GetJobListSnapshotAsync(listScope);
            Assert.AreEqual(root.JobId, snapshot.Jobs.Single().Added!.JobId);
            Assert.AreEqual(expectedStats, snapshot.Jobs.Single().Added!.Stats);
            await using var reconnected = overviewClient.ObserveJobListAsync(listScope, observationDeadline.Token)
                .GetAsyncEnumerator();
            Assert.IsTrue(await reconnected.MoveNextAsync());
            CollectionAssert.AreEqual(snapshot.WindowJobIds!.ToArray(), reconnected.Current.WindowJobIds!.ToArray());
            Assert.AreEqual(expectedStats, reconnected.Current.Jobs.Single().Added!.Stats);
            await client.DeleteSubmissionAsync(root.SubmissionId!.Value);
            Assert.IsTrue(await reconnected.MoveNextAsync());
            CollectionAssert.AreEqual(new[] { root.JobId }, reconnected.Current.RemovedJobIds.ToArray());
        }
        finally
        {
            releaseSearches.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task SubmissionOperationsIgnoreAnotherWorkflowsLostSearchHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-isolated-submission-" + Guid.NewGuid().ToString("N"));
        var peer = global::Tests.Server.SoulseekClientProxy.Create();
        await using var app = global::Tests.TestServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings { Username = "test", Password = "test", LogLevel = Microsoft.Extensions.Logging.LogLevel.None },
            OperatorDownloadSettings = new DownloadSettingsPatchDto(Output: new OutputSettingsPatchDto(ParentDir: Path.Combine(directory, "out"))),
            Persistence = new ServerPersistenceOptions { Enabled = true, DataDirectory = directory, RetentionEnabled = false },
            ClientFactory = _ => peer.Client,
        }, "http://127.0.0.1:0");
        var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
        var created = new TaskCompletionSource<Sockseek.Core.DownloadEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.EngineCreated += engine => created.TrySetResult(engine);
        try
        {
            await app.StartAsync();
            var engine = await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retired = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource>();
            TaskCompletionSource Retirement(Guid id) => retired.GetOrAdd(id,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            engine.Events.ChangePublished += change =>
            {
                if (change is WorkflowRetiredChange retirement) Retirement(retirement.WorkflowId).TrySetResult();
            };
            var persistence = app.Services.GetRequiredService<PersistenceCoordinator>();
            var original = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("independent accepted intent"), CancellationToken.None);
            await Retirement(original.WorkflowId).Task.WaitAsync(TimeSpan.FromSeconds(5));
            await persistence.WaitForWorkflowHandoffAsync(original.WorkflowId, CancellationToken.None);
            string? originalIntent = await persistence.Submissions!.GetSpecificationJsonAsync(original.SubmissionId!.Value);
            Assert.IsNotNull(originalIntent);
            Guid lostSearchId;

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { Pooling = false, DataSource = Path.Combine(directory, "sockseek.db") }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                // Lose only the other workflow's completed search projection;
                // its terminal job metadata and immutable accepted intent survive.
                command.CommandText = """
                    CREATE TRIGGER lose_search_insert BEFORE INSERT ON search_jobs
                    WHEN NEW.query = 'lost search history' AND NEW.is_complete = 1
                    BEGIN SELECT RAISE(FAIL, 'isolated search completion fault'); END;
                    CREATE TRIGGER lose_search_update BEFORE UPDATE ON search_jobs
                    WHEN NEW.query = 'lost search history' AND NEW.is_complete = 1
                    BEGIN SELECT RAISE(FAIL, 'isolated search completion fault'); END;
                    """;
                await command.ExecuteNonQueryAsync();
                var other = await supervisor.Submissions.SubmitSearchJobAsync(
                    new SubmitSearchJobRequestDto("lost search history"), CancellationToken.None);
                lostSearchId = other.JobId;
                await Retirement(other.WorkflowId).Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(() =>
                    persistence.WaitForWorkflowHandoffAsync(other.WorkflowId, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5)));
                await persistence.WaitForWorkflowHandoffAsync(other.WorkflowId, CancellationToken.None, includeSearchResults: false);
                command.CommandText = "DROP TRIGGER lose_search_insert; DROP TRIGGER lose_search_update;";
                await command.ExecuteNonQueryAsync();
            }

            var recoveredSearch = await supervisor.Submissions.RerunSearchJobAsync(lostSearchId, CancellationToken.None);
            Assert.IsNotNull(recoveredSearch);
            Assert.AreEqual("lost search history", recoveredSearch.QueryText);
            Assert.AreNotEqual(lostSearchId, recoveredSearch.JobId);
            await Retirement(recoveredSearch.WorkflowId).Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsNull(await supervisor.Submissions.RerunSubmissionAsync(Guid.NewGuid(), CancellationToken.None),
                "An unrelated history failure must not turn a missing submission into a server error.");
            var replay = await supervisor.Submissions.RerunSubmissionAsync(original.SubmissionId.Value, CancellationToken.None);
            Assert.IsNotNull(replay);
            Assert.AreNotEqual(original.SubmissionId, replay.SubmissionId);
            Assert.AreEqual("independent accepted intent", replay.QueryText);
            await Retirement(replay.WorkflowId).Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retained = await persistence.Submissions.GetSubmissionAsync(replay.SubmissionId!.Value);
            Assert.IsNotNull(retained);
            Assert.AreEqual(original.SubmissionId, retained.RerunOfSubmissionId);
            var deleted = await app.Services.GetRequiredService<JobReadService>().DeleteSubmissionAsync(original.SubmissionId.Value, CancellationToken.None);
            Assert.AreEqual(1, deleted.AffectedSubmissionCount);
            Assert.AreEqual(0, deleted.RejectedSubmissionCount);
            Assert.IsNull(await persistence.Submissions.GetSpecificationJsonAsync(original.SubmissionId.Value));
            Assert.IsNull(await app.Services.GetRequiredService<JobReadService>().GetSubmissionAsync(original.SubmissionId.Value));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task RetentionRunsAtStartupWithoutWaitingForTheDailyInterval()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-startup-retention-").FullName;
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            { Enabled = true, DataDirectory = directory, TransferHistoryAge = TimeSpan.FromDays(90) },
        });
        var persistence = new PersistenceCoordinator(options);
        using var maintenance = new PersistenceMaintenanceHostedService(persistence, options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PersistenceMaintenanceHostedService>.Instance);
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { Pooling = false, DataSource = Path.Combine(directory, "sockseek.db") }.ToString()))
            {
                await connection.OpenAsync();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO transfers (id, last_runtime_id, last_sequence, direction, source,
                        state, terminal_outcome, total_bytes, transferred_bytes, attempt_count, created_at_utc,
                        completed_at_utc, failure_reason, cancellation_source, revision)
                    VALUES ($id, $runtime, 1, 'Upload', 'SoulseekPeer', 'Completed', 'Succeeded',
                        10, 10, 1, $at, $at, 'None', 'None', 1);
                    """;
                seed.Parameters.AddWithValue("$id", Guid.NewGuid());
                seed.Parameters.AddWithValue("$runtime", persistence.Runtime!.RuntimeId);
                seed.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.AddDays(-100).ToUnixTimeMilliseconds());
                await seed.ExecuteNonQueryAsync();
            }
            var pruned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            persistence.TransfersPruned += () => pruned.TrySetResult();
            await maintenance.StartAsync(CancellationToken.None);
            await pruned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, (await persistence.TransferHistory!.GetTransfersAsync(new())).Items.Count);
        }
        finally
        {
            await maintenance.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RetentionBatchCeiling_RequestsBoundedCatchUpWork()
    {
        Assert.IsTrue(PersistenceMaintenanceHostedService.MayHaveMore(
            new PersistenceRetentionResultDto(500, 0, 0, 1),
            batchSize: 500));
        Assert.IsTrue(PersistenceMaintenanceHostedService.MayHaveMore(
            new PersistenceRetentionResultDto(0, 0, 0, 1, PrunedTransfers: 500),
            batchSize: 500));
        Assert.IsFalse(PersistenceMaintenanceHostedService.MayHaveMore(
            new PersistenceRetentionResultDto(499, 499, 499, 1, 499, 500, 499),
            batchSize: 500));
    }

    [TestMethod]
    public async Task CorruptDatabase_AbortsStartupWithoutReplacement_AndReleasesOwnership()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-corrupt-persistence", Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "sockseek.db");
        Directory.CreateDirectory(directory);
        byte[] corruption = "this is not a sqlite database"u8.ToArray();
        await File.WriteAllBytesAsync(databasePath, corruption);
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions { Enabled = true, DataDirectory = directory },
        });
        try
        {
            var coordinator = new PersistenceCoordinator(options);
            await Assert.ThrowsExactlyAsync<PersistenceDatabaseCorruptionException>(
                () => coordinator.StartAsync(CancellationToken.None));
            CollectionAssert.AreEqual(corruption, await File.ReadAllBytesAsync(databasePath));
            using var owner = SqliteDatabaseOwner.Acquire(new SockseekSqliteOptions(databasePath));
            Assert.AreEqual(Path.GetFullPath(databasePath), owner.DatabasePath);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DrainTimeout_IsFinite_ReportsFailure_AndLeavesRuntimeForReconciliation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-drain-timeout", Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "sockseek.db");
        Directory.CreateDirectory(directory);
        var serverOptions = new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
                DrainTimeout = TimeSpan.FromMilliseconds(100),
            },
        };
        var options = Options.Create(serverOptions);
        try
        {
            var first = new PersistenceCoordinator(options);
            await first.StartAsync(CancellationToken.None);
            Guid runtimeId = first.Runtime!.RuntimeId;
            var connectionString = new SqliteConnectionStringBuilder { Pooling = false, DataSource = databasePath }.ToString();
            await using (var lockConnection = new SqliteConnection(connectionString))
            {
                await lockConnection.OpenAsync();
                await using (var begin = lockConnection.CreateCommand())
                {
                    begin.CommandText = "BEGIN IMMEDIATE;";
                    await begin.ExecuteNonQueryAsync();
                }
                Guid jobId = Guid.NewGuid();
                Assert.IsTrue(first.MutationSink!.TryEnqueue(new JobPersistenceMutation(
                    runtimeId, 1, DateTimeOffset.UtcNow, jobId, 1, PersistenceMutationPriority.Structural,
                    Guid.NewGuid(), null, null, null, 1, "Song", "Pending", "None", null,
                    "None", "None", "None", "None", null, null, null, "query", 1, null)));

                await first.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(first.IsStarted);
                Assert.AreEqual(PersistenceHealthState.Unhealthy, first.HealthSnapshot!.State);
                await using var rollback = lockConnection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync();
            }

            var second = new PersistenceCoordinator(options);
            await second.StartAsync(CancellationToken.None);
            try
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { Pooling = false, DataSource = databasePath }.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT shutdown_kind FROM runtime_sessions ORDER BY started_at_utc LIMIT 1;";
                Assert.AreEqual("Unclean", Convert.ToString(await command.ExecuteScalarAsync()));
            }
            finally
            {
                await second.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task Daemon_StartsPersistenceBeforeEngine_ReportsHealth_AndStopsRuntimeCleanly()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-daemon-persistence", Guid.NewGuid().ToString("N"));
        string mockFiles = Path.Combine(directory, "mock-files");
        string databasePath = Path.Combine(directory, "data", "sockseek.db");
        Directory.CreateDirectory(mockFiles);

        try
        {
            var options = new ServerOptions
            {
                ConfigDir = directory,
                Engine = new EngineSettings { MockFilesDir = mockFiles, LogLevel = Microsoft.Extensions.Logging.LogLevel.None },
                Persistence = new ServerPersistenceOptions
                {
                    Enabled = true,
                    DataDirectory = Path.GetDirectoryName(databasePath),
                    RetentionEnabled = false,
                    TransferHistoryAge = TimeSpan.FromDays(90),
                },
            };
            var app = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0");
            try
            {
                await app.StartAsync();
                var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
                var status = supervisor.GetStatus();

                Assert.IsNotNull(status.Persistence);
                Assert.IsTrue(status.Persistence.Enabled);
                Assert.IsTrue(status.Persistence.Initialized);
                Assert.AreEqual("Healthy", status.Persistence.State);
                Assert.IsNotNull(status.Persistence.RuntimeId);
                Assert.IsNotNull(status.Persistence.RuntimeStartedAtUtc);
                Assert.IsFalse(string.IsNullOrWhiteSpace(status.Persistence.SchemaVersion));
                Assert.AreEqual(0, status.Persistence.ReconciledUnfinishedRuntimeCount);

                var coordinator = app.Services.GetRequiredService<PersistenceCoordinator>();
                Assert.IsNotNull(coordinator.InputArtifacts);
                Assert.IsNotNull(app.Services.GetRequiredService<SearchViewCoordinator>().Cache);
                Assert.IsFalse(File.Exists(Path.Combine(
                    Path.GetDirectoryName(databasePath)!,
                    "planning",
                    "input-artifacts",
                    "artifacts.db")));
                Assert.IsFalse(File.Exists(Path.Combine(
                    Path.GetDirectoryName(databasePath)!,
                    "planning",
                    "search-views.db")));
                var integrity = await coordinator.CheckIntegrityAsync(CancellationToken.None);
                Assert.IsTrue(integrity.IsHealthy);
                var backup = await coordinator.BackupAsync(
                    Path.Combine(directory, "backups", "daemon-test.db"),
                    CancellationToken.None);
                Assert.IsTrue(backup.IntegrityHealthy);
                Assert.IsTrue(File.Exists(backup.BackupPath));
                var checkpoint = await coordinator.CheckpointAsync(CancellationToken.None);
                Assert.IsTrue(checkpoint.Busy is 0 or 1);
                Guid expiredTransfer = Guid.NewGuid(), retainedTransfer = Guid.NewGuid();
                await using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder { Pooling = false, DataSource = databasePath }.ToString()))
                {
                    await seed.OpenAsync();
                    await using var insert = seed.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO transfers (id, last_runtime_id, last_sequence, direction, source,
                            username, remote_path, state, terminal_outcome, total_bytes, transferred_bytes,
                            attempt_count, created_at_utc, completed_at_utc, failure_reason,
                            cancellation_source, revision, group_ref, group_display_path)
                        VALUES ($id, $runtime, 1, 'Download', 'SoulseekPeer', 'peer', $path,
                            'Completed', 'Succeeded', 10, 10, 1, $at, $at, 'None', 'None', 1,
                            'retained-folder', 'Share');
                        """;
                    foreach (var (id, age, path) in new[]
                    {
                        (expiredTransfer, TimeSpan.FromDays(100), @"Share\Expired.bin"),
                        (retainedTransfer, TimeSpan.FromMinutes(1), @"Share\Recent.bin"),
                    })
                    {
                        insert.Parameters.Clear();
                        insert.Parameters.AddWithValue("$id", id);
                        insert.Parameters.AddWithValue("$runtime", coordinator.Runtime!.RuntimeId);
                        insert.Parameters.AddWithValue("$path", path);
                        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.Subtract(age).ToUnixTimeMilliseconds());
                        await insert.ExecuteNonQueryAsync();
                    }
                }
                using (var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app))
                await using (var live = new SockseekLiveClient(http))
                {
                    var scope = new TransferTimelineScopeDto(TransferCommandDirection.Download,
                        Limit: 2, IncludeGroupSummaries: true);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await using var observed = live.ObserveTransferTimelineAsync(scope, deadline.Token).GetAsyncEnumerator();
                    Assert.IsTrue(await observed.MoveNextAsync());
                    Assert.AreEqual(2, observed.Current.Items.Count);
                    Assert.AreEqual(2L, observed.Current.GroupSummaries!.Items.Single().FileCount);
                    var initialPosition = observed.Current.ReadPosition!;
                    var retention = await coordinator.RunRetentionAsync(CancellationToken.None);
                    Assert.AreEqual(0, retention.PrunedJobs);
                    Assert.AreEqual(1, retention.PrunedTransfers);
                    var afterPruning = await new SockseekApiClient(http).GetTransferTimelineAsync(scope, deadline.Token);
                    Assert.IsTrue(afterPruning.ReadPosition!.Sequence > initialPosition.Sequence,
                        "Pruning changes the readable timeline and must invalidate existing observations.");
                    Assert.AreEqual(retainedTransfer, afterPruning.Items.Single().TransferId);
                    Assert.IsTrue(await observed.MoveNextAsync());
                    CollectionAssert.AreEqual(new[] { expiredTransfer }, observed.Current.RemovedTransferIds.ToArray());
                    Assert.AreEqual(1L, observed.Current.GroupSummaries!.Items.Single().FileCount,
                        "An open folder must lose pruned members without an unrelated transfer event.");
                }
                var statusAfterMaintenance = supervisor.GetStatus().Persistence!;
                Assert.IsNotNull(statusAfterMaintenance.LastRetentionAtUtc);
                Assert.AreEqual(0, statusAfterMaintenance.LastRetentionPrunedJobs);

                await app.StopAsync();
                Assert.IsFalse(
                    coordinator.IsStarted,
                    "The hosted persistence runtime must release database ownership during host stop.");

                var connectionString = new SqliteConnectionStringBuilder { Pooling = false, DataSource = databasePath }.ToString();
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT shutdown_kind FROM runtime_sessions ORDER BY started_at_utc DESC LIMIT 1;";
                Assert.AreEqual("Clean", Convert.ToString(await command.ExecuteScalarAsync()));
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(directory);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string directory)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (!Directory.Exists(directory))
                return;
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(25);
            }
        }
    }

    [TestMethod]
    public async Task SubmissionResource_RerunsSettingsAndPermanentlyDeletesOnlyTerminalSubmissions()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-submission-resource",
            Guid.NewGuid().ToString("N"));
        string mockFiles = Path.Combine(directory, "mock-files");
        string artistFiles = Path.Combine(mockFiles, "Artist");
        Directory.CreateDirectory(artistFiles);
        await File.WriteAllTextAsync(
            Path.Combine(artistFiles, "Artist - Manual Track.mp3"),
            "audio");
        var options = new ServerOptions
        {
            ConfigDir = directory,
            Engine = new EngineSettings { MockFilesDir = mockFiles, MockFilesReadTags = false },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = Path.Combine(directory, "data"),
            },
        };

        try
        {
            await using var app = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0");
            await app.StartAsync();
            var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            var facade = app.Services.GetRequiredService<JobReadService>();

            var awaitingSelection = new TaskCompletionSource<JobSummaryDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnManualJob(JobSummaryDto job)
            {
                if (job.QueryText?.Contains("Manual Track", StringComparison.Ordinal) == true
                    && job.LifecycleState == ServerJobLifecycleState.AwaitingSelection)
                    awaitingSelection.TrySetResult(job);
            }
            supervisor.StateStore.JobUpserted += OnManualJob;
            var manual = await supervisor.Submissions.SubmitSongJobAsync(
                new SubmitSongJobRequestDto(
                    new SongQueryDto("Artist", "Manual Track"),
                    DownloadBehavior: new DownloadBehaviorPolicyDto(
                        Song: ServerDownloadBehavior.Manual)),
                CancellationToken.None);
            await awaitingSelection.Task.WaitAsync(TimeSpan.FromSeconds(5));
            supervisor.StateStore.JobUpserted -= OnManualJob;
            var rejectedDeletion = await app.Services.GetRequiredService<JobReadService>().DeleteSubmissionAsync(
                manual.SubmissionId!.Value,
                CancellationToken.None);
            Assert.AreEqual(1, rejectedDeletion.RejectedSubmissionCount);
            Assert.AreEqual("nonterminal-jobs", rejectedDeletion.RejectionReasons.Single().Reason);
            Assert.IsTrue(supervisor.CancelJob(manual.JobId));

            var firstTerminal = new TaskCompletionSource<JobSummaryDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFirstJob(JobSummaryDto job)
            {
                if (job.QueryText == "durable submission"
                    && job.LifecycleState == ServerJobLifecycleState.Terminal)
                    firstTerminal.TrySetResult(job);
            }
            supervisor.StateStore.JobUpserted += OnFirstJob;

            var submitted = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto(
                    "durable submission",
                    new SubmissionOptionsDto(
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Search: new SearchSettingsPatchDto(NoBrowseFolder: true)))),
                CancellationToken.None);
            await firstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            supervisor.StateStore.JobUpserted -= OnFirstJob;

            Assert.IsNotNull(submitted.SubmissionId);
            Assert.AreEqual(ServerJobRole.UserRoot, submitted.Role);
            Assert.IsNotNull(submitted.CreatedAtUtc);
            Guid originalSubmissionId = submitted.SubmissionId.Value;

            var page = await facade.GetSubmissionsAsync(null, 100);
            var retained = page.Items.Single(item => item.SubmissionId == originalSubmissionId);
            var persistenceStatus = supervisor.GetStatus().Persistence!;
            Assert.AreEqual("Healthy", persistenceStatus.State, persistenceStatus.LastFailure);
            Assert.AreEqual(1, retained.TotalJobCount);
            Assert.AreEqual(1, retained.UserRootJobCount);
            Assert.AreEqual(0, retained.ActiveJobCount);
            var detail = await facade.GetSubmissionAsync(originalSubmissionId);
            Assert.IsNotNull(detail);
            Assert.AreEqual(ServerJobKind.Search, detail.CommandKind);

            var rerunTerminal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRerunJob(JobSummaryDto job)
            {
                if (job.QueryText != "durable submission"
                    || job.SubmissionId == originalSubmissionId
                    || job.LifecycleState != ServerJobLifecycleState.Terminal)
                    return;
                bool noBrowseFolder = supervisor
                    .GetRuntimeJob<Sockseek.Core.Jobs.SearchJob>(job.JobId)?
                    .Config.Search.NoBrowseFolder == true;
                rerunTerminal.TrySetResult(noBrowseFolder);
            }
            supervisor.StateStore.JobUpserted += OnRerunJob;
            var rerun = await supervisor.Submissions.RerunSubmissionAsync(
                originalSubmissionId,
                CancellationToken.None);
            Assert.IsNotNull(rerun);
            Assert.IsNotNull(rerun.SubmissionId);
            Assert.AreNotEqual(originalSubmissionId, rerun.SubmissionId);
            Assert.IsTrue(await rerunTerminal.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "Rerun must execute the retained request setting, not the current default.");
            supervisor.StateStore.JobUpserted -= OnRerunJob;

            var rerunDetail = await facade.GetSubmissionAsync(rerun.SubmissionId.Value);
            Assert.IsNotNull(rerunDetail);
            Assert.AreEqual(originalSubmissionId, rerunDetail.Summary.RerunOfSubmissionId);

            var deleted = await facade.DeleteSubmissionAsync(originalSubmissionId, CancellationToken.None);
            Assert.AreEqual(1, deleted.AffectedJobCount);
            Assert.AreEqual(0, deleted.RejectedSubmissionCount);
            Assert.IsNull(await facade.GetSubmissionAsync(originalSubmissionId));
            Assert.IsNull(await facade.GetJobAsync(submitted.JobId));
            Assert.IsNull(await supervisor.Submissions.RerunSubmissionAsync(originalSubmissionId, CancellationToken.None));
            Assert.IsNull((await facade.GetSubmissionAsync(rerun.SubmissionId.Value))!.Summary.RerunOfSubmissionId);

            var hierarchyTerminal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHierarchyJob(JobSummaryDto job)
            {
                if (job.ItemName == "deletion hierarchy"
                    && job.LifecycleState == ServerJobLifecycleState.Terminal)
                    hierarchyTerminal.TrySetResult(true);
            }
            supervisor.StateStore.JobUpserted += OnHierarchyJob;
            JobSummaryDto hierarchy = await supervisor.Submissions.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "deletion hierarchy",
                    [
                        new JobListJobDraftDto(
                            "nested orchestration",
                            [
                                new TrackSearchJobDraftDto(
                                    new SongQueryDto("Missing", "Deleted Child")),
                            ]),
                    ]),
                CancellationToken.None);
            await hierarchyTerminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            supervisor.StateStore.JobUpserted -= OnHierarchyJob;

            Guid hierarchySubmissionId = hierarchy.SubmissionId!.Value;
            CombinedJobPage hierarchyJobs = await facade.GetJobsAsync(
                new JobQuery(
                    null,
                    null,
                    null,
                    null,
                    IncludeAll: true,
                    SubmissionId: hierarchySubmissionId),
                null,
                100);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    ServerJobRole.UserRoot,
                    ServerJobRole.Orchestration,
                    ServerJobRole.ExecutionChild,
                },
                hierarchyJobs.Items.Select(job => job.Role).ToArray());
            DeleteSubmissionResponseDto hierarchyDeletion = await app.Services.GetRequiredService<JobReadService>()
                .DeleteSubmissionAsync(
                    hierarchySubmissionId,
                    CancellationToken.None);
            Assert.AreEqual(3, hierarchyDeletion.AffectedJobCount);
            Assert.AreEqual(0, hierarchyDeletion.RejectedSubmissionCount);
            await app.StopAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task HistoricalSearchResult_StartsNewDownloadAfterFullDaemonRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-daemon-history", Guid.NewGuid().ToString("N"));
        string mockFiles = Path.Combine(directory, "mock-files");
        string albumDirectory = Path.Combine(mockFiles, "Artist", "Album");
        string outputDirectory = Path.Combine(directory, "output");
        string databasePath = Path.Combine(directory, "data", "sockseek.db");
        Directory.CreateDirectory(albumDirectory);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(albumDirectory, "01. Artist - Track One.mp3"), "audio");

        var options = new ServerOptions
        {
            Profiles = new ProfileCatalog { AutoProfiles = [new SettingsProfile
            {
                Name = "original-profile", Condition = "input-type == \"manual-search\"",
                Download = new SettingsPatch([new("Skip.SkipExisting", false)]),
            }] },
            ConfigDir = directory,
            Engine = new EngineSettings { MockFilesDir = mockFiles, MockFilesReadTags = false },
            DefaultDownload = new DownloadSettings
            {
                Output = { ParentDir = outputDirectory },
                Search = { MinGroupShares = 1 },
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = Path.GetDirectoryName(databasePath),
            },
        };

        try
        {
            Guid sourceJobId;
            Guid workflowId;
            int sourceDisplayId;
            FileCandidateRefDto candidate;
            Guid fileViewId;
            long fileViewRevision;
            SearchViewFileDto expectedCandidate;
            Guid trackGroupViewId;
            long trackGroupViewRevision;
            SearchViewTrackGroupDto expectedTrackGroup;
            Guid albumSearchJobId;
            Guid albumWorkflowId;
            Guid directoryViewId;
            long directoryViewRevision;
            SearchViewDirectoryDto expectedFolder;
            Guid albumGroupViewId;
            long albumGroupViewRevision;
            SearchViewAlbumGroupDto expectedAlbumGroup;

            await using (var first = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0"))
            {
                await first.StartAsync();
                try
                {
                    var supervisor = first.Services.GetRequiredService<EngineSupervisor>();
                    var persistence = first.Services.GetRequiredService<PersistenceCoordinator>();
                    var views = first.Services.GetRequiredService<SearchViewCoordinator>();
                    var search = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                        new SubmitTrackSearchJobRequestDto(new SongQueryDto("Artist", "Track One")),
                        CancellationToken.None);
                    await WaitForTerminalSuccessAsync(persistence, search.JobId);
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(search.JobId));
                    SearchViewSummaryDto fileView = await CreateCompleteSearchViewAsync(
                        views,
                        search.JobId,
                        new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.Files));
                    SearchViewFilePageDto results = (views.GetFiles(fileView.ViewId, fileView.Revision, null, 10, cancellationToken: CancellationToken.None))!;
                    Assert.AreEqual(1, results.Items.Count);

                    sourceJobId = search.JobId;
                    workflowId = search.WorkflowId;
                    sourceDisplayId = search.DisplayId;
                    fileViewId = fileView.ViewId;
                    fileViewRevision = fileView.Revision;
                    candidate = new FileCandidateRefDto(
                        results.Items[0].Peer.Username,
                        results.Items[0].RemoteFilename);
                    expectedCandidate = results.Items[0];

                    SearchViewSummaryDto trackGroupView = await CreateCompleteSearchViewAsync(
                        views,
                        search.JobId,
                        new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.TrackGroups));
                    SearchViewTrackGroupPageDto trackGroups = (views.GetTrackGroups(trackGroupView.ViewId, trackGroupView.Revision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, trackGroups.Items.Count);
                    trackGroupViewId = trackGroupView.ViewId;
                    trackGroupViewRevision = trackGroupView.Revision;
                    expectedTrackGroup = trackGroups.Items[0];

                    var albumSearch = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                        new SubmitAlbumSearchJobRequestDto(new AlbumQueryDto("Artist", "Album")),
                        CancellationToken.None);
                    await WaitForTerminalSuccessAsync(persistence, albumSearch.JobId);
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(albumSearch.JobId));
                    albumSearchJobId = albumSearch.JobId;
                    albumWorkflowId = albumSearch.WorkflowId;
                    SearchViewSummaryDto directoryView = await CreateCompleteSearchViewAsync(
                        views,
                        albumSearch.JobId,
                        new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.AlbumDirectories));
                    SearchViewDirectoryPageDto folders = (views.GetDirectories(directoryView.ViewId, directoryView.Revision, null, 10, cancellationToken: CancellationToken.None))!;
                    Assert.AreEqual(1, folders.Items.Count);
                    directoryViewId = directoryView.ViewId;
                    directoryViewRevision = directoryView.Revision;
                    expectedFolder = folders.Items[0];
                    SearchViewSummaryDto albumGroupView = await CreateCompleteSearchViewAsync(
                        views,
                        albumSearch.JobId,
                        new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.AlbumGroups));
                    SearchViewAlbumGroupPageDto albumGroups = (views.GetAlbumGroups(albumGroupView.ViewId, albumGroupView.Revision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, albumGroups.Items.Count);
                    albumGroupViewId = albumGroupView.ViewId;
                    albumGroupViewRevision = albumGroupView.Revision;
                    expectedAlbumGroup = albumGroups.Items[0];
                }
                finally
                {
                    await first.StopAsync();
                }
            }

            options.DefaultDownload!.Search.NecessaryCond.Formats = ["ogg"];

            options.Profiles = ProfileCatalog.Empty;
            await using (var second = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0"))
            {
                await second.StartAsync();
                try
                {
                    var supervisor = second.Services.GetRequiredService<EngineSupervisor>();
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(sourceJobId));

                    var views = second.Services.GetRequiredService<SearchViewCoordinator>();
                    foreach (Guid expired in new[] { fileViewId, trackGroupViewId, directoryViewId, albumGroupViewId })
                        Assert.IsNull(views.Get(expired, CancellationToken.None));
                    async Task<(Guid, long)> Rebuild(Guid source, ServerSearchViewProjectionKind kind)
                    {
                        var fresh = await CreateCompleteSearchViewAsync(views, source, new CreateSearchViewRequestDto(kind));
                        return (fresh.ViewId, fresh.Revision);
                    }
                    (fileViewId, fileViewRevision) = await Rebuild(sourceJobId, ServerSearchViewProjectionKind.Files);
                    (trackGroupViewId, trackGroupViewRevision) = await Rebuild(sourceJobId, ServerSearchViewProjectionKind.TrackGroups);
                    (directoryViewId, directoryViewRevision) = await Rebuild(albumSearchJobId, ServerSearchViewProjectionKind.AlbumDirectories);
                    (albumGroupViewId, albumGroupViewRevision) = await Rebuild(albumSearchJobId, ServerSearchViewProjectionKind.AlbumGroups);

                    SearchViewFilePageDto historicalProjection = (views.GetFiles(fileViewId, fileViewRevision, null, 10, cancellationToken: CancellationToken.None))!;
                    Assert.AreEqual(1, historicalProjection.Items.Count);
                    Assert.AreEqual(expectedCandidate.Ref, historicalProjection.Items[0].Ref);
                    Assert.AreEqual(expectedCandidate.File.Size, historicalProjection.Items[0].File.Size);
                    Assert.AreEqual(expectedCandidate.File.BitRate, historicalProjection.Items[0].File.BitRate);
                    Assert.AreEqual(expectedCandidate.File.SampleRate, historicalProjection.Items[0].File.SampleRate);
                    Assert.AreEqual(expectedCandidate.File.Length, historicalProjection.Items[0].File.Length);

                    var facade = second.Services.GetRequiredService<JobReadService>();
                    var workflowPage = await facade.GetWorkflowsAsync(null, 100);
                    Assert.IsTrue(workflowPage.Items.Any(workflow => workflow.WorkflowId == workflowId));
                    Assert.IsTrue(workflowPage.Items.Any(workflow => workflow.WorkflowId == albumWorkflowId));
                    var historicalWorkflow = await facade.GetWorkflowAsync(workflowId);
                    Assert.IsNotNull(historicalWorkflow);
                    Assert.AreEqual(1, historicalWorkflow.Summary.RootJobCount);
                    var historicalJobs = await facade.GetJobsAsync(
                        new JobQuery(null, null, null, workflowId, IncludeAll: true),
                        cursor: null,
                        limit: 100);
                    CollectionAssert.Contains(historicalJobs.Items.Select(job => job.JobId).ToArray(), sourceJobId);
                    Assert.IsNull(historicalJobs.NextCursor);
                    var byDisplay = await facade.GetJobByDisplayIdAsync(workflowId, sourceDisplayId);
                    Assert.IsNotNull(byDisplay);
                    Assert.AreEqual(sourceJobId, byDisplay.Summary.JobId);
                    CollectionAssert.AreEqual(new[] { new AppliedProfileDto("original-profile", "automatic") }, byDisplay.Summary.AppliedProfiles.ToArray());
                    CollectionAssert.AreEqual(byDisplay.Summary.AppliedProfiles.ToArray(), historicalJobs.Items.Single(job => job.JobId == sourceJobId).AppliedProfiles.ToArray());
                    Assert.IsFalse(supervisor.CancelJob(sourceJobId));
                    Assert.IsFalse(supervisor.TryNextCandidate(sourceJobId));
                    Assert.IsFalse(await supervisor.CompleteManualSelectionAsync(sourceJobId));
                    Assert.IsFalse(await supervisor.SkipManualSelectionAsync(sourceJobId));

                    SearchViewTrackGroupPageDto historicalTracks = (views.GetTrackGroups(trackGroupViewId, trackGroupViewRevision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, historicalTracks.Items.Count);
                    Assert.AreEqual(expectedTrackGroup.Query, historicalTracks.Items[0].Query);
                    Assert.AreEqual(
                        expectedTrackGroup.Representative.Ref,
                        historicalTracks.Items[0].Representative.Ref);
                    var trackOptions = (views.GetTrackGroupOptions(trackGroupViewId, expectedTrackGroup.Ref, trackGroupViewRevision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, trackOptions.Items.Count);
                    var chosenTrack = new CommitSearchViewSelectionRequestDto(
                        trackGroupViewRevision,
                        new RefSelectionExpressionDto(RefSelectionMode.Only, [expectedTrackGroup.Ref]),
                        Guid.NewGuid(),
                        ChosenOptions: new Dictionary<string, string>
                        {
                            [expectedTrackGroup.Ref] = trackOptions.Items[0].Ref,
                        });
                    var chosenReceipt = (await views.Selections.CommitSelectionAsync(
                        trackGroupViewId, chosenTrack, CancellationToken.None))!;
                    Assert.AreEqual(1L, chosenReceipt.SubmittedCount);
                    var chosenSubmission = await second.Services.GetRequiredService<PersistenceCoordinator>()
                        .Submissions!.GetSubmissionAsync(chosenReceipt.SubmissionId!.Value, CancellationToken.None);
                    var chosenSong = (Sockseek.Core.Jobs.SongJob)Sockseek.Core.Planning.SubmissionSpecificationCodec
                        .Deserialize(chosenSubmission!.SpecificationJson).MaterializeJob();
                    Assert.IsFalse(chosenSong.CanBeSkipped);
                    Assert.IsNotNull(chosenSong.ExactTarget);
                    Assert.AreEqual(trackOptions.Items[0].RemoteFilename, chosenSong.ExactTarget.Filename);
                    await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                        views.Selections.CommitSelectionAsync(trackGroupViewId,
                            chosenTrack with { ChosenOptions = new Dictionary<string, string>() },
                            CancellationToken.None));
                    var unavailableChoice = (await views.Selections.CommitSelectionAsync(
                        trackGroupViewId,
                        chosenTrack with
                        {
                            IdempotencyKey = Guid.NewGuid(),
                            ChosenOptions = new Dictionary<string, string>
                            {
                                [expectedTrackGroup.Ref] = "missing-option",
                            },
                        }, CancellationToken.None))!;
                    Assert.AreEqual(0L, unavailableChoice.SubmittedCount);
                    Assert.AreEqual("unavailable-option", unavailableChoice.RejectionReasons.Single().Reason);

                    SearchViewSummaryDto hiddenTrackView = await CreateCompleteSearchViewAsync(
                        views,
                        sourceJobId,
                        new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.TrackGroups,
                            FilterText: "no-such-track"));
                    Assert.AreEqual(0, (views.GetTrackGroups(hiddenTrackView.ViewId, hiddenTrackView.Revision, null, 10, CancellationToken.None))!.Items.Count);
                    var hiddenChoiceCount = (views.Selections.CountSelection(hiddenTrackView.ViewId, new CountSearchViewSelectionRequestDto(hiddenTrackView.Revision,
                            new RefSelectionExpressionDto(RefSelectionMode.Only,
                                [expectedTrackGroup.Ref]),
                            ChosenOptions: chosenTrack.ChosenOptions), CancellationToken.None))!;
                    Assert.AreEqual(0L, hiddenChoiceCount.SelectedItemCount);
                    var hiddenChoiceReceipt = (await views.Selections.CommitSelectionAsync(
                        hiddenTrackView.ViewId,
                        chosenTrack with
                        {
                            Revision = hiddenTrackView.Revision,
                            IdempotencyKey = Guid.NewGuid(),
                        }, CancellationToken.None))!;
                    Assert.AreEqual(0L, hiddenChoiceReceipt.SubmittedCount);
                    Assert.AreEqual("missing-ref", hiddenChoiceReceipt.RejectionReasons.Single().Reason);

                    SearchViewDirectoryPageDto historicalFolders = (views.GetDirectories(directoryViewId, directoryViewRevision, null, 10, cancellationToken: CancellationToken.None))!;
                    Assert.AreEqual(1, historicalFolders.Items.Count);
                    Assert.AreEqual(expectedFolder.Ref, historicalFolders.Items[0].Ref);
                    SearchViewDirectoryFilePageDto historicalChildren = (views.GetDirectoryFiles(directoryViewId, expectedFolder.Ref.Ref, directoryViewRevision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, historicalChildren.Items.Count);
                    Assert.AreEqual(expectedFolder.BestChild.Ref, historicalChildren.Items[0].Ref);

                    SearchViewAlbumGroupPageDto historicalAlbums = (views.GetAlbumGroups(albumGroupViewId, albumGroupViewRevision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, historicalAlbums.Items.Count);
                    Assert.AreEqual(expectedAlbumGroup.Query, historicalAlbums.Items[0].Query);
                    Assert.AreEqual(1, historicalAlbums.Items[0].Representative.InitialFiles?.Items.Count);
                    Assert.AreEqual(
                        expectedAlbumGroup.Representative.Ref,
                        historicalAlbums.Items[0].Representative.Ref);
                    var albumOptions = (views.GetAlbumGroupOptions(albumGroupViewId, expectedAlbumGroup.Ref, albumGroupViewRevision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, albumOptions.Items.Count);
                    Assert.AreEqual(1, albumOptions.Items[0].InitialFiles?.Items.Count);
                    var chosenAlbum = new CommitSearchViewSelectionRequestDto(
                        albumGroupViewRevision,
                        new RefSelectionExpressionDto(RefSelectionMode.Only, [expectedAlbumGroup.Ref]),
                        Guid.NewGuid(),
                        ChosenOptions: new Dictionary<string, string>
                        {
                            [expectedAlbumGroup.Ref] = albumOptions.Items[0].Ref.Ref,
                        });
                    var chosenAlbumCount = (views.Selections.CountSelection(albumGroupViewId, new CountSearchViewSelectionRequestDto(
                            chosenAlbum.Revision, chosenAlbum.Selection,
                            ChosenOptions: chosenAlbum.ChosenOptions), CancellationToken.None))!;
                    Assert.AreEqual(albumOptions.Items[0].PublicMatchingFileCount,
                        chosenAlbumCount.PublicFileCount);
                    Assert.AreEqual(albumOptions.Items[0].LockedMatchingFileCount,
                        chosenAlbumCount.LockedFileCount);
                    var albumChoiceReceipt = (await views.Selections.CommitSelectionAsync(
                        albumGroupViewId, chosenAlbum, CancellationToken.None))!;
                    Assert.AreEqual(1L, albumChoiceReceipt.SubmittedCount);
                    var chosenAlbumFiles = (views.GetDirectoryFiles(albumGroupViewId, albumOptions.Items[0].Ref.Ref, albumGroupViewRevision, null, 10, CancellationToken.None))!;
                    Assert.AreEqual(1, chosenAlbumFiles.Items.Count);
                    var oneAlbumFile = chosenAlbum with
                    {
                        IdempotencyKey = Guid.NewGuid(),
                        AlbumFileSelections = new Dictionary<string, RefSelectionExpressionDto>
                        {
                            [expectedAlbumGroup.Ref] = new(
                                RefSelectionMode.Only, [chosenAlbumFiles.Items[0].Ref]),
                        },
                    };
                    var oneAlbumFileCount = (views.Selections.CountSelection(albumGroupViewId, new CountSearchViewSelectionRequestDto(
                            oneAlbumFile.Revision, oneAlbumFile.Selection,
                            ChosenOptions: oneAlbumFile.ChosenOptions,
                            AlbumFileSelections: oneAlbumFile.AlbumFileSelections), CancellationToken.None))!;
                    Assert.AreEqual(1L, oneAlbumFileCount.PublicFileCount);
                    Assert.AreEqual(0L, oneAlbumFileCount.LockedFileCount);
                    var oneFileReceipt = (await views.Selections.CommitSelectionAsync(
                        albumGroupViewId, oneAlbumFile, CancellationToken.None))!;
                    Assert.AreEqual(1L, oneFileReceipt.SubmittedCount);
                    await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                        views.Selections.CommitSelectionAsync(albumGroupViewId,
                            oneAlbumFile with { AlbumFileSelections = null },
                            CancellationToken.None));
                    var wrongAlbumFile = (await views.Selections.CommitSelectionAsync(
                        albumGroupViewId,
                        oneAlbumFile with
                        {
                            IdempotencyKey = Guid.NewGuid(),
                            AlbumFileSelections = new Dictionary<string, RefSelectionExpressionDto>
                            {
                                [expectedAlbumGroup.Ref] = new(RefSelectionMode.Only, ["missing-file"]),
                            },
                        }, CancellationToken.None))!;
                    Assert.AreEqual(0L, wrongAlbumFile.SubmittedCount);
                    Assert.AreEqual("invalid-item", wrongAlbumFile.RejectionReasons.Single().Reason);
                    var wrongAlbumChoice = (await views.Selections.CommitSelectionAsync(
                        albumGroupViewId,
                        chosenAlbum with
                        {
                            IdempotencyKey = Guid.NewGuid(),
                            ChosenOptions = new Dictionary<string, string>
                            {
                                [expectedAlbumGroup.Ref] = "missing-option",
                            },
                        }, CancellationToken.None))!;
                    Assert.AreEqual(0L, wrongAlbumChoice.SubmittedCount);
                    Assert.AreEqual("unavailable-option", wrongAlbumChoice.RejectionReasons.Single().Reason);

                    var retrieved = await views.StartDirectoryRetrievalAsync(
                        directoryViewId,
                        new RetrieveSearchViewDirectoryRequestDto(
                            directoryViewRevision,
                            expectedFolder.Ref,
                            Guid.NewGuid()),
                        CancellationToken.None);
                    Assert.IsNotNull(retrieved);
                    Assert.AreEqual(albumSearchJobId, retrieved.SourceJobId);
                    Assert.AreEqual(albumWorkflowId, retrieved.WorkflowId);
                    Assert.IsTrue(retrieved.DisplayId > 2);
                    await WaitForTerminalSuccessAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        retrieved.JobId);

                    var albumDownload = await supervisor.StartFolderDownloadAsync(
                        albumSearchJobId,
                        new StartFolderDownloadRequestDto(new AlbumFolderRefDto(
                            expectedFolder.Ref.Username,
                            expectedFolder.Ref.FolderPath)),
                        CancellationToken.None);
                    Assert.IsNotNull(albumDownload);
                    Assert.AreEqual(albumSearchJobId, albumDownload.SourceJobId);
                    Assert.AreEqual(albumWorkflowId, albumDownload.WorkflowId);
                    Assert.IsTrue(albumDownload.DisplayId > 2);
                    await WaitForTerminalSuccessAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        albumDownload.JobId);

                    var downloads = await supervisor.StartFileDownloadsAsync(
                        sourceJobId,
                        new StartFileDownloadsRequestDto([candidate], new SubmissionOptionsDto(
                            DownloadSettings: new DownloadSettingsPatchDto(Skip: new SkipSettingsPatchDto(SkipExisting: false)))),
                        CancellationToken.None);

                    Assert.IsNotNull(downloads);
                    Assert.AreEqual(1, downloads.Count);
                    Assert.AreNotEqual(sourceJobId, downloads[0].JobId);
                    Assert.AreEqual(sourceJobId, downloads[0].SourceJobId);
                    Assert.AreEqual(workflowId, downloads[0].WorkflowId);
                    Assert.IsTrue(downloads[0].DisplayId > 2);
                    Assert.AreEqual(3, new[] { retrieved.DisplayId, albumDownload.DisplayId, downloads[0].DisplayId }.Distinct().Count());
                    var acceptedDownload = await second.Services.GetRequiredService<PersistenceCoordinator>()
                        .Submissions!.GetSubmissionAsync(downloads[0].SubmissionId!.Value, CancellationToken.None);
                    var acceptedSong = (Sockseek.Core.Jobs.SongJob)Sockseek.Core.Planning.SubmissionSpecificationCodec
                        .Deserialize(acceptedDownload!.SpecificationJson).MaterializeJob();
                    Assert.IsFalse(acceptedSong.CanBeSkipped);
                    Assert.IsNotNull(acceptedSong.ExactTarget);
                    Assert.IsNotNull(acceptedSong.ResolvedTarget);
                    Assert.AreEqual(expectedCandidate.Peer.QueueLength, acceptedSong.ResolvedTarget.QueueLength,
                        "Restart must preserve the observed peer queue instead of rebuilding partial search metadata.");
                    Assert.AreEqual(expectedCandidate.Peer.ObservedAtUtc, acceptedSong.ResolvedTarget.ObservedAtUtc);
                    Assert.IsTrue(acceptedSong.ResolvedTarget.Evidence.Sequence > 0);
                    Assert.IsTrue(acceptedSong.ResolvedTarget.Evidence.Revision > 0);
                    await WaitForTerminalSuccessAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        downloads[0].JobId);
                    var persistedDownload = await WaitForHistoricalJobAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        downloads[0].JobId);
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(downloads[0].JobId));
                    var retainedDetail = await facade.GetJobAsync(downloads[0].JobId);
                    Assert.IsNotNull(retainedDetail);
                    Assert.AreEqual(persistedDownload.SourceJobId, retainedDetail.Summary.SourceJobId);
                    var combinedWorkflow = await facade.GetWorkflowAsync(workflowId);
                    Assert.IsNotNull(combinedWorkflow);
                    var combinedJobs = await facade.GetJobsAsync(
                        new JobQuery(null, null, null, workflowId, IncludeAll: true),
                        cursor: null,
                        limit: 100);
                    CollectionAssert.Contains(combinedJobs.Items.Select(job => job.JobId).ToArray(), sourceJobId);
                    CollectionAssert.Contains(combinedJobs.Items.Select(job => job.JobId).ToArray(), downloads[0].JobId);
                    Assert.IsTrue(Directory.GetFiles(outputDirectory, "*.mp3", SearchOption.AllDirectories).Length >= 1);
                }
                finally
                {
                    await second.StopAsync();
                }
            }

            await using (var third = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0"))
            {
                await third.StartAsync();
                try
                {
                    var facade = third.Services.GetRequiredService<TransferReadService>();
                    var transfers = await facade.GetTransfersAsync(new(Limit: 100));
                    Assert.IsTrue(transfers.Items.Count > 0);

                    var completedTransfer = transfers.Items.First(transfer =>
                        transfer.Status.TerminalOutcome == TransferTerminalOutcome.Succeeded && transfer.Status.AttemptCount > 0);
                    Assert.IsNotNull(completedTransfer.Identity.JobId);
                    Assert.IsNotNull(completedTransfer.Identity.WorkflowId);

                    var detail = await facade.GetTransferDetailAsync(completedTransfer.TransferId);
                    Assert.IsNotNull(detail);
                    Assert.AreEqual(completedTransfer.TransferId, detail.Transfer.TransferId);
                    Assert.AreEqual(completedTransfer.Status.AttemptCount, detail.Transfer.Status.AttemptCount);
                    Assert.IsNotNull(detail.LatestAttempt);
                    Assert.AreEqual(completedTransfer.TransferId, detail.LatestAttempt.TransferId);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(detail.LatestAttempt.SourceUsername));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(detail.LatestAttempt.SourcePath));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(detail.LatestAttempt.OutputPath));

                    var attemptPage = await facade.GetTransferAttemptsAsync(
                        completedTransfer.TransferId,
                        afterAttemptNumber: 0,
                        limit: 1);
                    Assert.IsNotNull(attemptPage);
                    Assert.AreEqual(1, attemptPage.Items.Count);
                    Assert.AreEqual(completedTransfer.TransferId, attemptPage.Items[0].TransferId);
                }
                finally
                {
                    await third.StopAsync();
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task<SearchViewSummaryDto> CreateCompleteSearchViewAsync(
        SearchViewCoordinator views,
        Guid sourceJobId,
        CreateSearchViewRequestDto request)
    {
        var publications = System.Threading.Channels.Channel.CreateUnbounded<(Guid Id, long Revision)>();
        void OnPublished(Sockseek.Server.Planning.SearchViewInfo view) => publications.Writer.TryWrite((view.Id, view.Revision));
        views.ViewPublished += OnPublished;
        try
        {
            SearchViewSummaryDto summary = (await views.CreateAsync(
                sourceJobId,
                request,
                CancellationToken.None))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!summary.IsComplete)
            {
                (Guid id, _) = await publications.Reader.ReadAsync(timeout.Token);
                if (id != summary.ViewId)
                    continue;
                summary = (views.Get(summary.ViewId, timeout.Token))!;
            }
            return summary;
        }
        finally
        {
            views.ViewPublished -= OnPublished;
        }
    }

    private static async Task WaitForTerminalSuccessAsync(
        PersistenceCoordinator persistence,
        Guid jobId)
    {
        var history = persistence.JobHistory
            ?? throw new AssertFailedException("Persistence job history is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        PersistedJob? job = null;
        while (!timeout.IsCancellationRequested)
        {
            job = await history.GetJobAsync(jobId, timeout.Token);
            if (job?.LifecycleState == "Terminal")
                break;
            try { await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }

        Assert.IsNotNull(job, $"Timed out waiting for persisted job {jobId}.");
        Assert.AreEqual("Terminal", job.LifecycleState);
        Assert.AreEqual("Succeeded", job.TerminalOutcome,
            $"Job terminated with {job.TerminalOutcome}: {job.FailureMessage}");
    }

    private static async Task<PersistedJob> WaitForHistoricalJobAsync(
        PersistenceCoordinator coordinator,
        Guid jobId)
    {
        var history = coordinator.JobHistory ?? throw new AssertFailedException("Persistence job history is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var job = await history.GetJobAsync(jobId, timeout.Token);
            if (job != null)
                return job;
            try { await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }

        Assert.Fail($"Timed out waiting for persisted job {jobId}.");
        throw new InvalidOperationException("Assert.Fail did not throw.");
    }
}
