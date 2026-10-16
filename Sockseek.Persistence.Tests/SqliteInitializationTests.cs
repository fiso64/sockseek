using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Read;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class SqliteInitializationTests
{
    [TestMethod]
    public async Task EveryCheckedInMigration_HasAnExplicitAutomaticSafetyClassification()
    {
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();

        var migrations = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(
            migrations.OrderBy(value => value).ToArray(),
            SqliteInitializer.SafeAutomaticMigrations.OrderBy(value => value).ToArray());
    }

    [TestMethod]
    public async Task FreshFile_MigratesAllTables_AndConfiguresWalFullAndForeignKeys()
    {
        await using var database = new TemporaryDatabase();

        var result = await database.Initializer.InitializeAsync();

        Assert.AreEqual("wal", result.JournalMode.ToLowerInvariant());
        Assert.AreEqual("2", result.SynchronousMode);
        StringAssert.Contains(result.SchemaVersion, "AddSearchViews");

        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        Assert.AreEqual("1", Scalar(context, "PRAGMA foreign_keys;"));
        Assert.AreEqual("2", Scalar(context, "PRAGMA synchronous;"));

        var tables = QueryStrings(context,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "runtime_sessions", "jobs", "search_jobs", "search_results", "transfers", "transfer_attempts",
                "chat_conversations", "chat_room_subscriptions", "chat_messages", "notifications", "chat_sequences",
                "submissions",
                "transfer_accounting_checkpoints", "transfer_byte_buckets", "transfer_accounting_state",
                "peer_restriction_overrides",
                "input_artifacts", "input_artifact_pins",
                "search_views", "search_view_revisions", "search_view_files",
                "search_view_directories", "search_view_directory_versions",
                "search_view_directory_files", "search_view_aggregate_tracks",
                "search_view_aggregate_track_versions", "search_view_aggregate_track_files",
                "search_view_aggregate_albums", "search_view_aggregate_album_versions",
                "search_view_aggregate_album_directory_versions", "search_view_peers",
            },
            tables.ToArray());
    }

    [TestMethod]
    public async Task PreviousSchemaFixture_UpgradesInPlace_AndPreservesRows()
    {
        await using var database = new TemporaryDatabase();
        Guid runtimeId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        await using (var previous = await database.Factory.CreateDbContextAsync())
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync("20260711200436_InitialPersistence");
            Assert.AreEqual(0L, Convert.ToInt64(await previous.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_jobs_workflow_id_created_at_utc_id'").SingleAsync()));
            previous.RuntimeSessions.Add(new RuntimeSessionEntity
            {
                Id = runtimeId,
                StartedAtUtc = 1,
                Version = "previous-schema-fixture",
            });
            await previous.SaveChangesAsync();
            Guid workflowId = Guid.NewGuid();
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO jobs (
                    id, workflow_id, last_runtime_id, last_sequence, display_id, kind,
                    lifecycle_state, activity_phase, terminal_outcome, skip_reason,
                    cancellation_source, failure_reason, created_at_utc, updated_at_utc,
                    completed_at_utc, revision, payload_schema_version)
                VALUES (
                    {jobId}, {workflowId}, {runtimeId}, 1, 1, 'Generic',
                    'Terminal', 'None', 'Succeeded', 'None',
                    'None', 'None', 0, 0, 0, 1, 1);
                """);
        }

        await using (var upgraded = await database.Factory.CreateDbContextAsync())
        {
            await upgraded.Database.MigrateAsync();
            Assert.AreEqual(1L, Convert.ToInt64(await upgraded.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_jobs_workflow_id_created_at_utc_id'").SingleAsync()));
            Assert.IsTrue(await upgraded.Jobs.AnyAsync(job => job.Id == jobId));
            var applied = await upgraded.Database.GetAppliedMigrationsAsync();
            CollectionAssert.Contains(applied.ToArray(), "20260712090000_AddHistoryQueryIndexes");
            CollectionAssert.Contains(applied.ToArray(), "20260712170220_AddTransferAttemptSourceIdentity");
            CollectionAssert.Contains(applied.ToArray(), "20260829230000_AddSubmissions");
            CollectionAssert.Contains(applied.ToArray(), "20260831120000_AddPeerRestrictions");
            CollectionAssert.Contains(applied.ToArray(), "20260901010000_AddInputArtifacts");
            CollectionAssert.Contains(applied.ToArray(), "20260901020000_AddSearchViews");
            var upgradedJob = await upgraded.Jobs.SingleAsync(job => job.Id == jobId);
            Assert.IsNull(upgradedJob.SubmissionId);
            Assert.AreEqual("Legacy", upgradedJob.SemanticRole);
        }
    }

    [TestMethod]
    public async Task DatabaseWithUnknownAppliedMigration_IsRejectedBeforeStartup()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        const string unknownMigration = "20990101000000_FutureSchema";
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ({unknownMigration}, '99.0.0');
                """);
        }

        var exception = await Assert.ThrowsExactlyAsync<PersistenceSchemaCompatibilityException>(
            () => database.Initializer.InitializeAsync());

        StringAssert.Contains(exception.Message, unknownMigration);
        StringAssert.Contains(exception.Message, "newer or incompatible");
    }

    [TestMethod]
    public async Task NewlyOpenedContext_EnforcesForeignKeys()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();

        await using var context = await database.Factory.CreateDbContextAsync();
        context.Jobs.Add(new JobEntity
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            LastRuntimeId = Guid.NewGuid(),
            LastSequence = 1,
            DisplayId = 1,
            Kind = "Search",
            LifecycleState = "Pending",
            ActivityPhase = "None",
            TerminalOutcome = "None",
            SkipReason = "None",
            CancellationSource = "None",
            FailureReason = "None",
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Revision = 1,
            PayloadSchemaVersion = 1,
        });

        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [TestMethod]
    public async Task SecondOwnerForSameDatabase_IsRejectedClearly()
    {
        await using var database = new TemporaryDatabase();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => SqliteDatabaseOwner.Acquire(database.Options));

        StringAssert.Contains(exception.Message, "already owned");
    }

    [TestMethod]
    public async Task RuntimeStopPropagatesCallerCancellationAndReleasesOwnership()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-runtime-stop-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
        var host = new PersistenceRuntimeHost(
            options,
            new Persistence.Write.PersistenceWriterOptions(),
            new PersistenceRetentionOptions(),
            "test");
        try
        {
            await host.StartAsync();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                host.StopAsync(TimeSpan.FromSeconds(1), cancellation.Token));

            Assert.IsFalse(host.IsStarted);
            using SqliteDatabaseOwner reacquired = SqliteDatabaseOwner.Acquire(options);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Restart_ReconcilesUnfinishedRuntime_AndCleanRestartDoesNotReinterruptRows()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var firstRuntime = new PersistenceRuntimeSession(database.Factory, clock);
        var first = await firstRuntime.StartAsync("test-1");

        Guid jobId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Jobs.Add(new JobEntity
            {
                Id = jobId,
                WorkflowId = Guid.NewGuid(),
                LastRuntimeId = first.Runtime.RuntimeId,
                LastSequence = 10,
                DisplayId = 1,
                Kind = "Search",
                LifecycleState = "Running",
                ActivityPhase = "Searching",
                TerminalOutcome = "None",
                SkipReason = "None",
                CancellationSource = "None",
                FailureReason = "None",
                CreatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                StartedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                UpdatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                Revision = 1,
                PayloadSchemaVersion = 1,
            });
            context.SearchJobs.Add(new SearchJobEntity
            {
                JobId = jobId,
                Query = "unfinished",
                Revision = 1,
                ResultPersistenceState = "Incomplete",
            });
            context.Transfers.Add(new TransferEntity
            {
                Id = transferId,
                JobId = jobId,
                LastRuntimeId = first.Runtime.RuntimeId,
                LastSequence = 11,
                Direction = "Download",
                Source = "SoulseekPeer",
                State = "InProgress",
                TerminalOutcome = "None",
                FailureReason = "None",
                CreatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                StartedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                Revision = 1,
            });
            context.TransferAttempts.Add(new TransferAttemptEntity
            {
                Id = attemptId,
                TransferId = transferId,
                LastRuntimeId = first.Runtime.RuntimeId,
                LastSequence = 12,
                AttemptNumber = 1,
                Source = "SoulseekPeer",
                State = "InProgress",
                FailureReason = "None",
                StartedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                Revision = 1,
            });
            await context.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        var secondRuntime = new PersistenceRuntimeSession(database.Factory, clock);
        var reconciled = await secondRuntime.StartAsync("test-2");

        Assert.AreEqual(1, reconciled.UnfinishedRuntimeCount);
        Assert.AreEqual(1, reconciled.InterruptedJobCount);
        Assert.AreEqual(1, reconciled.InterruptedSearchCount);
        Assert.AreEqual(1, reconciled.InterruptedTransferCount);
        Assert.AreEqual(1, reconciled.InterruptedAttemptCount);

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var job = await context.Jobs.SingleAsync(x => x.Id == jobId);
            var search = await context.SearchJobs.SingleAsync(x => x.JobId == jobId);
            var transfer = await context.Transfers.SingleAsync(x => x.Id == transferId);
            var attempt = await context.TransferAttempts.SingleAsync(x => x.Id == attemptId);
            var priorRuntime = await context.RuntimeSessions.SingleAsync(x => x.Id == first.Runtime.RuntimeId);
            var accounting = await context.TransferAccountingStates.SingleAsync(x => x.Id == 1);

            Assert.AreEqual("Terminal", job.LifecycleState);
            Assert.AreEqual("Interrupted", job.FailureReason);
            Assert.AreEqual(2L, job.Revision);
            Assert.AreEqual(secondRuntime.Current!.RuntimeId, job.LastRuntimeId);
            Assert.AreEqual(0L, job.LastSequence);
            Assert.AreEqual("Interrupted", search.ResultPersistenceState);
            Assert.AreEqual("Interrupted", transfer.State);
            Assert.AreEqual("Interrupted", attempt.State);
            Assert.AreEqual("Unclean", priorRuntime.ShutdownKind);
            Assert.AreEqual(clock.GetUtcNow().ToUnixTimeMilliseconds(), accounting.CompleteFromUtc);
        }

        long coverageAfterUncleanRestart = clock.GetUtcNow().ToUnixTimeMilliseconds();
        clock.Advance(TimeSpan.FromMinutes(1));
        await secondRuntime.StopAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        var thirdRuntime = new PersistenceRuntimeSession(database.Factory, clock);
        var cleanRestart = await thirdRuntime.StartAsync("test-3");
        Assert.AreEqual(0, cleanRestart.UnfinishedRuntimeCount);
        Assert.AreEqual(0, cleanRestart.InterruptedJobCount);
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(
                coverageAfterUncleanRestart,
                (await context.TransferAccountingStates.SingleAsync(x => x.Id == 1)).CompleteFromUtc);
        }
        await thirdRuntime.StopAsync();
    }

    [TestMethod]
    public async Task Restart_MarksIncompleteSearchInterruptedEvenWhenItsJobWasAlreadyTerminal()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2035, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var firstRuntime = new PersistenceRuntimeSession(database.Factory, clock);
        var first = await firstRuntime.StartAsync("test-1");
        Guid searchId = Guid.NewGuid();

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Jobs.Add(JobRow(
                searchId,
                first.Runtime.RuntimeId,
                1,
                "Terminal",
                clock.GetUtcNow(),
                clock.GetUtcNow(),
                "Search"));
            context.SearchJobs.Add(new SearchJobEntity
            {
                JobId = searchId,
                Query = "partially persisted",
                Revision = 2,
                ResultCount = 1,
                IsComplete = false,
                ResultPersistenceState = "Incomplete",
            });
            await context.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        var secondRuntime = new PersistenceRuntimeSession(database.Factory, clock);
        StartupReconciliationResult result = await secondRuntime.StartAsync("test-2");

        Assert.AreEqual(0, result.InterruptedJobCount);
        Assert.AreEqual(1, result.InterruptedSearchCount);
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var search = await context.SearchJobs.SingleAsync(row => row.JobId == searchId);
            Assert.IsTrue(search.IsComplete);
            Assert.AreEqual("Interrupted", search.ResultPersistenceState);
        }
        await secondRuntime.StopAsync();
    }

    [TestMethod]
    public async Task Restart_ReconcilesUnfinishedRowsWithConstantDatabaseCommands()
    {
        await using var database = new TemporaryDatabase(countCommands: true);
        await database.Initializer.InitializeAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var firstRuntime = new PersistenceRuntimeSession(database.Factory, clock);
        var first = await firstRuntime.StartAsync("test-1");

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            for (int index = 0; index < 50; index++)
            {
                Guid jobId = Guid.NewGuid();
                Guid transferId = Guid.NewGuid();
                context.Jobs.Add(new JobEntity
                {
                    Id = jobId,
                    WorkflowId = Guid.NewGuid(),
                    LastRuntimeId = first.Runtime.RuntimeId,
                    LastSequence = index + 1,
                    DisplayId = index + 1,
                    Kind = "Search",
                    LifecycleState = "Running",
                    ActivityPhase = "Searching",
                    TerminalOutcome = "None",
                    SkipReason = "None",
                    CancellationSource = "None",
                    FailureReason = "None",
                    CreatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    StartedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    UpdatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    Revision = 1,
                    PayloadSchemaVersion = 1,
                });
                context.SearchJobs.Add(new SearchJobEntity
                {
                    JobId = jobId,
                    Query = $"unfinished-{index}",
                    Revision = 1,
                    ResultPersistenceState = "Incomplete",
                });
                context.Transfers.Add(new TransferEntity
                {
                    Id = transferId,
                    JobId = jobId,
                    LastRuntimeId = first.Runtime.RuntimeId,
                    LastSequence = index + 1,
                    Direction = "Download",
                    Source = "SoulseekPeer",
                    State = "InProgress",
                    TerminalOutcome = "None",
                    FailureReason = "None",
                    CreatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    StartedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    Revision = 1,
                });
                context.TransferAttempts.Add(new TransferAttemptEntity
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    LastRuntimeId = first.Runtime.RuntimeId,
                    LastSequence = index + 1,
                    AttemptNumber = 1,
                    Source = "SoulseekPeer",
                    State = "InProgress",
                    FailureReason = "None",
                    StartedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    Revision = 1,
                });
            }
            await context.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        database.CommandCounter!.Reset();
        var secondRuntime = new PersistenceRuntimeSession(database.Factory, clock);
        StartupReconciliationResult reconciled = await secondRuntime.StartAsync("test-2");

        Assert.AreEqual(50, reconciled.InterruptedJobCount);
        Assert.AreEqual(50, reconciled.InterruptedSearchCount);
        Assert.AreEqual(50, reconciled.InterruptedTransferCount);
        Assert.AreEqual(50, reconciled.InterruptedAttemptCount);
        Assert.IsTrue(
            database.CommandCounter.Executed <= 12,
            $"Startup reconciliation executed {database.CommandCounter.Executed} database commands.");
    }

    [TestMethod]
    public async Task Backup_OpensIndependently_AndPassesIntegrityCheck()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        string backupPath = Path.Combine(Path.GetDirectoryName(database.Options.DatabasePath)!, "backup", "sockseek-backup.db");
        var maintenance = new SqliteMaintenanceService(database.Factory, database.Options);

        var result = await maintenance.BackupAsync(backupPath);

        Assert.IsTrue(result.SizeBytes > 0);
        Assert.IsTrue(result.Integrity.IsHealthy);
        Assert.IsFalse(File.Exists(backupPath + "-wal"));
        Assert.IsFalse(File.Exists(backupPath + "-shm"));
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('jobs', 'input_artifacts', 'search_views');
            """;
        Assert.AreEqual(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        string restorePath = Path.Combine(Path.GetDirectoryName(database.Options.DatabasePath)!, "restored-app-data", "sockseek.db");
        var restored = await SqliteMaintenanceService.RestoreOfflineAsync(backupPath, restorePath);
        Assert.IsTrue(restored.Integrity.IsHealthy);
        Assert.IsTrue(restored.SizeBytes > 0);
        Assert.AreEqual(0, Directory.GetFiles(
            Path.GetDirectoryName(restorePath)!,
            Path.GetFileName(restorePath) + ".restore-*",
            SearchOption.TopDirectoryOnly).Length);
        await using var restoredConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = restorePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await restoredConnection.OpenAsync();
        await using var restoredCommand = restoredConnection.CreateCommand();
        restoredCommand.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('jobs', 'input_artifacts', 'search_views');
            """;
        Assert.AreEqual(3L, Convert.ToInt64(await restoredCommand.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task Retention_PrunesOnlyTerminalJobs_AndMarksSearchResultsPruned()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var now = new DateTimeOffset(2035, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var runtimeId = Guid.NewGuid();
        Guid oldTerminalId = Guid.NewGuid();
        Guid activeId = Guid.NewGuid();
        Guid searchId = Guid.NewGuid();
        Guid oldTransferId = Guid.NewGuid();
        Guid activeTransferId = Guid.NewGuid();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.RuntimeSessions.Add(new RuntimeSessionEntity { Id = runtimeId, StartedAtUtc = now.AddDays(-120).ToUnixTimeMilliseconds(), Version = "test" });
            context.Jobs.Add(JobRow(oldTerminalId, runtimeId, 1, "Terminal", now.AddDays(-100), now.AddDays(-100)));
            context.Jobs.Add(JobRow(activeId, runtimeId, 2, "Running", now.AddDays(-100), null));
            context.Jobs.Add(JobRow(searchId, runtimeId, 3, "Terminal", now.AddDays(-40), now.AddDays(-40), "Search"));
            context.SearchJobs.Add(new SearchJobEntity
            {
                JobId = searchId,
                Query = "retained parent",
                Revision = 2,
                ResultCount = 1,
                IsComplete = true,
                CompletedAtUtc = now.AddDays(-40).ToUnixTimeMilliseconds(),
                ResultPersistenceState = "Complete",
            });
            context.SearchResults.Add(new SearchResultEntity
            {
                Id = Guid.NewGuid(),
                SearchJobId = searchId,
                Sequence = 1,
                Revision = 1,
                Username = "user",
                RemoteFilename = "file.mp3",
                SizeBytes = 1,
                Extension = ".mp3",
                ObservedAtUtc = now.AddDays(-40).ToUnixTimeMilliseconds(),
            });
            context.Transfers.Add(new TransferEntity
            {
                Id = oldTransferId,
                LastRuntimeId = runtimeId,
                LastSequence = 4,
                Direction = "Download",
                Source = "SoulseekPeer",
                State = "Completed",
                TerminalOutcome = "Succeeded",
                FailureReason = "None",
                CreatedAtUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                CompletedAtUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                Revision = 2,
            });
            context.TransferAttempts.Add(new TransferAttemptEntity
            {
                Id = Guid.NewGuid(),
                TransferId = oldTransferId,
                LastRuntimeId = runtimeId,
                LastSequence = 5,
                AttemptNumber = 1,
                Source = "SoulseekPeer",
                State = "Completed",
                FailureReason = "None",
                StartedAtUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                CompletedAtUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                Revision = 2,
            });
            context.Transfers.Add(new TransferEntity
            {
                Id = activeTransferId,
                LastRuntimeId = runtimeId,
                LastSequence = 6,
                Direction = "Download",
                Source = "SoulseekPeer",
                State = "InProgress",
                TerminalOutcome = "None",
                FailureReason = "None",
                CreatedAtUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                Revision = 1,
            });
            context.TransferByteBuckets.Add(new TransferByteBucketEntity
            {
                BucketStartUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                Direction = "Download",
                Username = "peer",
                Bytes = 123,
            });
            await context.SaveChangesAsync();
        }

        var retention = new RetentionService(
            database.Factory,
            new PersistenceRetentionOptions { BatchSize = 10 },
            new FixedTimeProvider(now));
        var result = await retention.RunBatchAsync();

        Assert.AreEqual(1, result.PrunedJobs);
        Assert.AreEqual(1, result.PrunedSearchResults);
        Assert.AreEqual(1, result.SearchesMarkedPruned);
        Assert.AreEqual(1, result.PrunedTransfers);
        Assert.AreEqual(1, result.PrunedTransferAttempts);
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            Assert.IsFalse(await context.Jobs.AnyAsync(job => job.Id == oldTerminalId));
            Assert.IsTrue(await context.Jobs.AnyAsync(job => job.Id == activeId));
            Assert.IsTrue(await context.Jobs.AnyAsync(job => job.Id == searchId));
            Assert.AreEqual("Pruned", (await context.SearchJobs.SingleAsync(search => search.JobId == searchId)).ResultPersistenceState);
            Assert.AreEqual(0, await context.SearchResults.CountAsync());
            Assert.IsFalse(await context.Transfers.AnyAsync(transfer => transfer.Id == oldTransferId));
            Assert.IsTrue(await context.Transfers.AnyAsync(transfer => transfer.Id == activeTransferId));
            Assert.AreEqual(0, await context.TransferByteBuckets.CountAsync());
            Assert.AreEqual(
                now.AddDays(-90).ToUnixTimeMilliseconds(),
                (await context.TransferAccountingStates.SingleAsync(state => state.Id == 1)).CompleteFromUtc);
        }
    }

    [TestMethod]
    public async Task DeletingSearchParent_CascadesSearchMetadataAndRawResultsExplicitly()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        Guid runtimeId = Guid.NewGuid();
        Guid searchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.RuntimeSessions.Add(new RuntimeSessionEntity
            {
                Id = runtimeId,
                StartedAtUtc = now.ToUnixTimeMilliseconds(),
                Version = "test",
            });
            context.Jobs.Add(JobRow(searchId, runtimeId, 1, "Terminal", now, now, "Search"));
            context.SearchJobs.Add(new SearchJobEntity
            {
                JobId = searchId,
                Query = "query",
                Revision = 2,
                ResultCount = 1,
                IsComplete = true,
                CompletedAtUtc = now.ToUnixTimeMilliseconds(),
                ResultPersistenceState = "Complete",
            });
            context.SearchResults.Add(new SearchResultEntity
            {
                Id = Guid.NewGuid(),
                SearchJobId = searchId,
                Sequence = 1,
                Revision = 1,
                Username = "peer",
                RemoteFilename = "file.mp3",
                SizeBytes = 1,
                Extension = ".mp3",
                ObservedAtUtc = now.ToUnixTimeMilliseconds(),
            });
            await context.SaveChangesAsync();
        }

        await using (var context = await database.Factory.CreateDbContextAsync())
            await context.Jobs.Where(job => job.Id == searchId).ExecuteDeleteAsync();

        await using (var verify = await database.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(0, await verify.SearchJobs.CountAsync());
            Assert.AreEqual(0, await verify.SearchResults.CountAsync());
        }
    }

    [TestMethod]
    public async Task Retention_MaximumCount_PrunesOldestTerminalRowsButNeverActiveRows()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var now = new DateTimeOffset(2035, 7, 1, 0, 0, 0, TimeSpan.Zero);
        Guid runtimeId = Guid.NewGuid();
        var terminalIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        Guid activeId = Guid.NewGuid();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.RuntimeSessions.Add(new RuntimeSessionEntity
            {
                Id = runtimeId,
                StartedAtUtc = now.AddDays(-10).ToUnixTimeMilliseconds(),
                Version = "test",
            });
            for (int index = 0; index < terminalIds.Length; index++)
            {
                var completed = now.AddDays(-10 + index);
                context.Jobs.Add(JobRow(
                    terminalIds[index], runtimeId, index + 1, "Terminal", completed, completed));
            }
            context.Jobs.Add(JobRow(activeId, runtimeId, 100, "Running", now.AddDays(-20), null));
            await context.SaveChangesAsync();
        }

        var retention = new RetentionService(database.Factory, new PersistenceRetentionOptions
        {
            CompletedJobHistoryAge = null,
            UnsuccessfulJobHistoryAge = null,
            MaximumRetainedJobs = 4,
            SearchResultAge = null,
            TransferHistoryAge = null,
            BatchSize = 10,
        }, new FixedTimeProvider(now));
        var result = await retention.RunBatchAsync();

        Assert.AreEqual(2, result.PrunedJobs);
        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.IsTrue(await verify.Jobs.AnyAsync(job => job.Id == activeId));
        Assert.IsFalse(await verify.Jobs.AnyAsync(job => job.Id == terminalIds[0]));
        Assert.IsFalse(await verify.Jobs.AnyAsync(job => job.Id == terminalIds[1]));
        Assert.AreEqual(4, await verify.Jobs.CountAsync());
    }

    [TestMethod]
    public async Task Retention_AgeSelectionReducesTheRemainingMaximumCountDeficit()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var now = new DateTimeOffset(2035, 7, 1, 0, 0, 0, TimeSpan.Zero);
        Guid runtimeId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 110).Select(_ => Guid.NewGuid()).ToArray();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.RuntimeSessions.Add(new RuntimeSessionEntity
            {
                Id = runtimeId,
                StartedAtUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                Version = "test",
            });
            for (int index = 0; index < ids.Length; index++)
            {
                DateTimeOffset completed = index < 10
                    ? now.AddDays(-100 + index)
                    : now.AddDays(-10).AddMinutes(index);
                context.Jobs.Add(JobRow(
                    ids[index], runtimeId, index + 1, "Terminal", completed, completed));
            }
            await context.SaveChangesAsync();
        }

        var retention = new RetentionService(database.Factory, new PersistenceRetentionOptions
        {
            CompletedJobHistoryAge = TimeSpan.FromDays(30),
            UnsuccessfulJobHistoryAge = TimeSpan.FromDays(30),
            MaximumRetainedJobs = 100,
            SearchResultAge = null,
            TransferHistoryAge = null,
            BatchSize = 100,
        }, new FixedTimeProvider(now));

        RetentionResult result = await retention.RunBatchAsync();

        Assert.AreEqual(10, result.PrunedJobs);
        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.AreEqual(100, await verify.Jobs.CountAsync());
        foreach (Guid id in ids.Take(10))
            Assert.IsFalse(await verify.Jobs.AnyAsync(job => job.Id == id));
    }

    [TestMethod]
    public async Task Retention_PreservesRawSearchResultsUntilTheirIndependentAgeExpires()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var now = new DateTimeOffset(2035, 7, 1, 0, 0, 0, TimeSpan.Zero);
        Guid runtimeId = Guid.NewGuid();
        Guid searchId = Guid.NewGuid();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.RuntimeSessions.Add(new RuntimeSessionEntity
            {
                Id = runtimeId,
                StartedAtUtc = now.AddDays(-100).ToUnixTimeMilliseconds(),
                Version = "test",
            });
            DateTimeOffset completed = now.AddDays(-40);
            context.Jobs.Add(JobRow(searchId, runtimeId, 1, "Terminal", completed, completed, "Search"));
            context.SearchJobs.Add(new SearchJobEntity
            {
                JobId = searchId,
                Query = "query",
                Revision = 2,
                ResultCount = 1,
                IsComplete = true,
                CompletedAtUtc = completed.ToUnixTimeMilliseconds(),
                ResultPersistenceState = "Complete",
            });
            context.SearchResults.Add(new SearchResultEntity
            {
                Id = Guid.NewGuid(),
                SearchJobId = searchId,
                Sequence = 1,
                Revision = 1,
                Username = "peer",
                RemoteFilename = "file.mp3",
                SizeBytes = 1,
                Extension = ".mp3",
                ObservedAtUtc = completed.ToUnixTimeMilliseconds(),
            });
            await context.SaveChangesAsync();
        }

        var options = new PersistenceRetentionOptions
        {
            CompletedJobHistoryAge = TimeSpan.FromDays(30),
            UnsuccessfulJobHistoryAge = TimeSpan.FromDays(30),
            MaximumRetainedJobs = null,
            SearchResultAge = TimeSpan.FromDays(90),
            TransferHistoryAge = null,
            BatchSize = 10,
        };
        RetentionResult first = await new RetentionService(
            database.Factory,
            options,
            new FixedTimeProvider(now)).RunBatchAsync();

        Assert.AreEqual(0, first.PrunedJobs);
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            Assert.IsTrue(await context.Jobs.AnyAsync(row => row.Id == searchId));
            Assert.AreEqual(1, await context.SearchResults.CountAsync(row => row.SearchJobId == searchId));
        }

        RetentionResult second = await new RetentionService(
            database.Factory,
            options,
            new FixedTimeProvider(now.AddDays(60))).RunBatchAsync();
        Assert.AreEqual(1, second.PrunedJobs);
        Assert.AreEqual(1, second.PrunedSearchResults);
        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.IsFalse(await verify.Jobs.AnyAsync(row => row.Id == searchId));
    }

    [TestMethod]
    public async Task RetentionPrunesSearchBatchWithConstantDatabaseCommands()
    {
        await using var database = new TemporaryDatabase(countCommands: true);
        await database.Initializer.InitializeAsync();
        var now = new DateTimeOffset(2035, 8, 1, 0, 0, 0, TimeSpan.Zero);
        Guid runtimeId = Guid.NewGuid();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.RuntimeSessions.Add(new RuntimeSessionEntity
            {
                Id = runtimeId,
                StartedAtUtc = now.AddDays(-60).ToUnixTimeMilliseconds(),
                Version = "test",
            });
            for (int index = 0; index < 50; index++)
            {
                Guid searchId = Guid.NewGuid();
                context.Jobs.Add(JobRow(
                    searchId,
                    runtimeId,
                    index + 1,
                    "Terminal",
                    now.AddDays(-40),
                    now.AddDays(-40),
                    "Search"));
                context.SearchJobs.Add(new SearchJobEntity
                {
                    JobId = searchId,
                    Query = $"query-{index}",
                    Revision = 2,
                    ResultCount = 1,
                    IsComplete = true,
                    CompletedAtUtc = now.AddDays(-40).ToUnixTimeMilliseconds(),
                    ResultPersistenceState = "Complete",
                });
                context.SearchResults.Add(new SearchResultEntity
                {
                    Id = Guid.NewGuid(),
                    SearchJobId = searchId,
                    Sequence = 1,
                    Revision = 1,
                    Username = "peer",
                    RemoteFilename = $"file-{index}.mp3",
                    SizeBytes = 1,
                    Extension = ".mp3",
                    ObservedAtUtc = now.AddDays(-40).ToUnixTimeMilliseconds(),
                });
            }
            await context.SaveChangesAsync();
        }

        database.CommandCounter!.Reset();
        var retention = new RetentionService(database.Factory, new PersistenceRetentionOptions
        {
            CompletedJobHistoryAge = null,
            UnsuccessfulJobHistoryAge = null,
            MaximumRetainedJobs = null,
            SearchResultAge = TimeSpan.FromDays(30),
            TransferHistoryAge = null,
            BatchSize = 50,
        }, new FixedTimeProvider(now));

        RetentionResult result = await retention.RunBatchAsync();

        Assert.AreEqual(50, result.PrunedSearchResults);
        Assert.AreEqual(50, result.SearchesMarkedPruned);
        Assert.IsTrue(
            database.CommandCounter.Executed <= 8,
            $"Search retention executed {database.CommandCounter.Executed} database commands.");
    }

    private static string Scalar(SockseekDbContext context, string sql)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static List<string> QueryStrings(SockseekDbContext context, string sql)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    private static JobEntity JobRow(
        Guid id,
        Guid runtimeId,
        long displayId,
        string lifecycle,
        DateTimeOffset created,
        DateTimeOffset? completed,
        string kind = "Song")
        => new()
        {
            Id = id,
            WorkflowId = Guid.NewGuid(),
            LastRuntimeId = runtimeId,
            LastSequence = 1,
            DisplayId = displayId,
            Kind = kind,
            LifecycleState = lifecycle,
            ActivityPhase = lifecycle == "Terminal" ? "None" : "Downloading",
            TerminalOutcome = lifecycle == "Terminal" ? "Succeeded" : "None",
            SkipReason = "None",
            CancellationSource = "None",
            FailureReason = "None",
            CreatedAtUtc = created.ToUnixTimeMilliseconds(),
            StartedAtUtc = created.ToUnixTimeMilliseconds(),
            UpdatedAtUtc = (completed ?? created).ToUnixTimeMilliseconds(),
            CompletedAtUtc = completed?.ToUnixTimeMilliseconds(),
            Revision = 1,
            PayloadSchemaVersion = 1,
        };

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sockseek-persistence-tests", Guid.NewGuid().ToString("N"));

        public TemporaryDatabase(bool countCommands = false)
        {
            Directory.CreateDirectory(directory);
            var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
            Options = options;
            Owner = SqliteDatabaseOwner.Acquire(options);
            DbContextOptions<SockseekDbContext> contextOptions = SockseekDbContextOptions.Create(options);
            if (countCommands)
            {
                CommandCounter = new CountingCommandInterceptor();
                contextOptions = new DbContextOptionsBuilder<SockseekDbContext>(contextOptions)
                    .AddInterceptors(CommandCounter)
                    .Options;
            }
            Factory = new SockseekDbContextFactory(contextOptions);
            Initializer = new SqliteInitializer(Factory, options, Owner);
        }

        public SockseekSqliteOptions Options { get; }
        public SqliteDatabaseOwner Owner { get; }
        public SockseekDbContextFactory Factory { get; }
        public SqliteInitializer Initializer { get; }
        public CountingCommandInterceptor? CommandCounter { get; }

        public ValueTask DisposeAsync()
        {
            Owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int executed;

        public int Executed => Volatile.Read(ref executed);

        public void Reset() => Volatile.Write(ref executed, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref executed);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Interlocked.Increment(ref executed);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Interlocked.Increment(ref executed);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
