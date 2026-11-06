using Sockseek.Persistence.Write;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Planning;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Settings;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class SqliteInitializationTests
{
    [TestMethod]
    public async Task DownloadGroupingMigrationPreservesSelectionBoundariesAndOutputPaths()
    {
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
        Guid runtime = Guid.NewGuid(), submission = Guid.NewGuid(), workflow = Guid.NewGuid();
        context.RuntimeSessions.Add(new RuntimeSessionEntity { Id = runtime, Version = "migration" });
        context.Submissions.Add(new SubmissionEntity { Id = submission, SpecificationSchemaVersion = 5, Revision = 1,
            Specification = new() { Json = "{}" } });
        var job = JobRow(Guid.NewGuid(), runtime, 1, "Terminal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        job.SubmissionId = submission;
        job.WorkflowId = workflow;
        context.Jobs.Add(job);
        TransferEntity Row(string path, Guid? scopeJob = null, string? group = null, string direction = "Download") => new()
        {
            Id = Guid.NewGuid(), JobId = scopeJob, WorkflowId = workflow, LastRuntimeId = runtime,
            LastSequence = 1, Revision = 1, Direction = direction, Source = "SoulseekPeer", Username = "Péér",
            RemotePath = path, LocalPath = "/unchanged/" + path, State = "Completed", TerminalOutcome = "Succeeded", CompletedAtUtc = 1,
            GroupRef = group, GroupDisplayPath = group is null ? null : "Original root",
            GroupRelativePath = group is null ? null : "Original relative path",
        };
        var rows = new[] {
            Row(@"Shares\曲\one.bin", job.Id), Row(@"Shares\曲\two.bin", job.Id),
            Row(@"Shares\曲\other-submission.bin"), Row(@"shares\曲\case.bin", job.Id),
            Row("Shares/曲/slash.bin", job.Id), Row("root.bin", job.Id),
            Row(@"Shares\曲\directory.bin", job.Id, "explicit-directory"),
            Row(@"Shares\曲\upload.bin", group: "upload-run", direction: "Upload"),
        };
        context.Transfers.AddRange(rows);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        // Exercise the actual upgrade of existing history, not just a fresh schema.
        await context.GetService<IMigrator>().MigrateAsync("20261105183312_TransferDirectionTimeline");
        await context.Database.MigrateAsync();
        var migrated = await context.Transfers.ToDictionaryAsync(row => row.Id);
        string expected = $"files:{submission:D}:Shares\\曲";
        Assert.AreEqual(expected, migrated[rows[0].Id].GroupRef);
        Assert.AreEqual(expected, migrated[rows[1].Id].GroupRef);
        foreach (int index in new[] { 2, 3, 4 }) Assert.AreNotEqual(expected, migrated[rows[index].Id].GroupRef);
        Assert.AreEqual($"files:{workflow:D}:Shares\\曲", migrated[rows[2].Id].GroupRef);
        Assert.AreEqual("Files", migrated[rows[0].Id].GroupKind);
        Assert.AreEqual(@"Shares\曲", migrated[rows[0].Id].GroupDisplayPath);
        Assert.AreEqual("one.bin", migrated[rows[0].Id].GroupRelativePath);
        Assert.IsNull(migrated[rows[5].Id].GroupRef);
        Assert.AreEqual("explicit-directory", migrated[rows[6].Id].GroupRef);
        Assert.AreEqual("Directory", migrated[rows[6].Id].GroupKind);
        Assert.AreEqual("Original relative path", migrated[rows[6].Id].GroupRelativePath);
        Assert.AreEqual("upload-run", migrated[rows[7].Id].GroupRef);
        Assert.AreEqual("Files", migrated[rows[7].Id].GroupKind);
        foreach (var row in rows) Assert.AreEqual(row.LocalPath, migrated[row.Id].LocalPath);
    }

    [TestMethod]
    public async Task FeatureReadConnectionsCannotBypassTheWriter()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var readers = new SockseekDbContextFactory(SockseekDbContextOptions.CreateReadOnly(database.Options));
        await using var context = await readers.CreateDbContextAsync();
        var failure = await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => context.Database.ExecuteSqlRawAsync("DELETE FROM peer_restriction_overrides"));
        Assert.AreEqual(8, failure.SqliteErrorCode, "SQLite must enforce the read-only boundary.");

        var store = new Sockseek.Persistence.PeerRestrictions.PeerRestrictionOverrideStore(
            readers, new PersistenceHealth(), database.Writes);
        await store.SetAsync(Sockseek.Core.Sharing.PeerRestrictionKind.UploadAccess, "ExactPeer",
            Sockseek.Core.Sharing.PeerUsernameRestrictionOverride.Blocked);
        Assert.AreEqual("ExactPeer", (await store.ReadAllAsync()).Single().Username,
            "The same store can still write through the transaction owner's context.");
    }

    [TestMethod]
    public async Task RawSearchFactsPreserveExactIdentitiesAndCascadeWithTheirOwner()
    {
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
        Guid runtime = Guid.NewGuid(), job = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        context.RuntimeSessions.Add(new RuntimeSessionEntity { Id = runtime, Version = "migration" });
        context.Jobs.Add(JobRow(job, runtime, 1, "Terminal", now, now, "Search"));
        context.SearchJobs.Add(new SearchJobEntity { JobId = job, Revision = 2, ResultCount = 1, LockedFileCount = 1 });
        foreach (int sequence in new[] { 1, 2 })
            await TestPersistenceWriter.SeedSearchResultAsync(context, new SearchResultEntity
        {
            SearchJobId = job, Sequence = sequence, Revision = 2,
            Username = "Péér", RemoteFilename = @"Share\曲\Track.flac", SizeBytes = long.MaxValue,
            BitRate = 320, BitDepth = 24, SampleRate = 96000, DurationSeconds = 123,
            ResponseFileCount = 2, Extension = "flac", UploadSpeed = 1234,
            HasFreeUploadSlot = false, QueueLength = 7, AttributesJson = "[]",
            ObservedAtUtc = now.ToUnixTimeMilliseconds(), Visibility = sequence == 1 ? "Public" : "Locked",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var reader = new SearchHistoryReader(database.Factory);
        string logicalRows = System.Text.Json.JsonSerializer.Serialize(
            await context.SearchResults.AsNoTracking().OrderBy(row => row.Sequence).ToArrayAsync());

        var page = (await reader.GetRawResultsAsync(job, 0, 500))!;
        Assert.AreEqual(2, page.Items.Count);
        Assert.AreEqual(long.MaxValue, page.Items[0].Size);
        Assert.AreEqual("Péér", page.Items[0].Username);
        Assert.AreEqual(@"Share\曲\Track.flac", page.Items[0].Filename);
        Assert.AreEqual(SearchResultVisibility.Public, page.Items[0].Visibility);
        Assert.AreEqual(SearchResultVisibility.Locked, page.Items[1].Visibility);
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(page.Items[0]),
            System.Text.Json.JsonSerializer.Serialize((await reader.GetResultAsync(job, "Péér", @"Share\曲\Track.flac"))!.Result));
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync("UPDATE search_observations SET source_sequence = 1 WHERE source_sequence = 2;"));
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync("UPDATE search_observations SET visibility = 'Public' WHERE source_sequence = 2;"));
        Assert.AreEqual(logicalRows, System.Text.Json.JsonSerializer.Serialize(
            await context.SearchResults.AsNoTracking().OrderBy(row => row.Sequence).ToArrayAsync()));
        await context.Database.OpenConnectionAsync();
        Assert.AreEqual("ok", Scalar(context, "PRAGMA integrity_check;"));
        Assert.AreEqual(0, QueryStrings(context, "PRAGMA foreign_key_check;").Count);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM search_jobs;");
        Assert.AreEqual(0, await context.SearchResults.CountAsync());
        Assert.AreEqual("0", Scalar(context, "SELECT count(*) FROM search_observations;"));
        Assert.AreEqual("0", Scalar(context, "SELECT count(*) FROM search_observation_sources;"));
    }

    [TestMethod]
    public async Task FreshFile_MigratesAllTables_AndConfiguresWalFullAndForeignKeys()
    {
        await using var database = new TemporaryDatabase();

        var result = await database.Initializer.InitializeAsync();

        Assert.AreEqual("wal", result.JournalMode.ToLowerInvariant());
        Assert.AreEqual("2", result.SynchronousMode);

        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        Assert.AreEqual((await context.Database.GetAppliedMigrationsAsync()).Last(), result.SchemaVersion);
        Assert.AreEqual("1", Scalar(context, "PRAGMA foreign_keys;"));
        Assert.AreEqual("2", Scalar(context, "PRAGMA synchronous;"));
        Assert.IsFalse(context.Database.HasPendingModelChanges());
        CollectionAssert.AreEqual(context.Database.GetMigrations().ToArray(),
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.AreEqual("0", Scalar(context,
            "SELECT COUNT(*) FROM sqlite_master WHERE name GLOB 'search_view*' OR name GLOB 'search_directory_observation*';"));
        Assert.AreEqual("1", Scalar(context,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_transfers_group_ref_username_direction_archived_at_utc';"));
        Assert.AreEqual("1", Scalar(context,
            "SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name = 'source_detail';"));

        var tables = QueryStrings(context,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "runtime_sessions", "jobs", "search_jobs", "search_observations", "transfers", "transfer_attempts",
                "chat_conversations", "chat_room_subscriptions", "chat_messages", "notifications", "chat_sequences",
                "submissions", "submission_specifications",
                "transfer_accounting_checkpoints", "transfer_byte_buckets", "transfer_accounting_state",
                "peer_restriction_overrides",
                "input_artifacts",
                "search_observation_sources",
                "directory_manifests", "directory_manifest_entries",
            },
            tables.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SubmissionIdentityAndPayloadHaveTheSameForeignKeysInMigrationAndModel(bool generatedModel)
    {
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        if (generatedModel) await context.Database.EnsureCreatedAsync();
        else await context.Database.MigrateAsync();
        await context.Database.OpenConnectionAsync();
        Assert.AreEqual("submissions:id:RESTRICT", Scalar(context,
            "SELECT \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list('submissions') WHERE \"from\" = 'rerun_of_submission_id';"));
        Assert.AreEqual("submissions:id:CASCADE", Scalar(context,
            "SELECT \"table\" || ':' || \"to\" || ':' || on_delete FROM pragma_foreign_key_list('submission_specifications');"));

        Guid original = Guid.NewGuid(), rerun = Guid.NewGuid();
        context.Submissions.Add(new SubmissionEntity
        {
            Id = original, SpecificationSchemaVersion = 5, Revision = 1,
            Specification = new() { Json = "{\"input\":\"original\"}" },
        });
        await context.SaveChangesAsync();
        context.Submissions.Add(new SubmissionEntity
        {
            Id = rerun, RerunOfSubmissionId = original, SpecificationSchemaVersion = 5, Revision = 1,
            Specification = new() { Json = "{\"input\":\"rerun\"}" },
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => context.Submissions.Where(row => row.Id == original).ExecuteDeleteAsync());
        Assert.AreEqual(2, await context.Submissions.CountAsync());
        Assert.AreEqual(2, await context.Set<SubmissionSpecificationEntity>().CountAsync());
        await context.Submissions.Where(row => row.Id == rerun).ExecuteDeleteAsync();
        Assert.AreEqual(original, (await context.Set<SubmissionSpecificationEntity>().SingleAsync()).SubmissionId);
        await context.Submissions.Where(row => row.Id == original).ExecuteDeleteAsync();
        Assert.AreEqual(0, await context.Set<SubmissionSpecificationEntity>().CountAsync());
        Assert.HasCount(0, QueryStrings(context, "PRAGMA foreign_key_check;"));
    }

    [TestMethod]
    [DataRow("20990101000000_FutureSchema")]
    [DataRow("20260711200436_InitialPersistence")]
    [DataRow("20261102011521_InitialPersistence")]
    public async Task DatabaseWithUnknownAppliedMigration_IsRejectedWithoutReplacingData(string unknownMigration)
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                CREATE TABLE operator_data(value TEXT);
                INSERT INTO operator_data VALUES ('keep me');
                INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ({unknownMigration}, '99.0.0');
                """);
        }

        var exception = await Assert.ThrowsExactlyAsync<PersistenceSchemaCompatibilityException>(
            () => database.Initializer.InitializeAsync());

        StringAssert.Contains(exception.Message, unknownMigration);
        await using var preserved = await database.Factory.CreateDbContextAsync();
        await preserved.Database.OpenConnectionAsync();
        Assert.AreEqual("keep me", Scalar(preserved, "SELECT value FROM operator_data;"));
        CollectionAssert.AreEquivalent(preserved.Database.GetMigrations().Append(unknownMigration).ToArray(),
            (await preserved.Database.GetAppliedMigrationsAsync()).ToArray());
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
              AND name IN ('jobs', 'input_artifacts', 'search_observations');
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
              AND name IN ('jobs', 'input_artifacts', 'search_observations');
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
            await TestPersistenceWriter.SeedSearchResultAsync(context, new SearchResultEntity
            {
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
            database.Writes,
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
            await TestPersistenceWriter.SeedSearchResultAsync(context, new SearchResultEntity
            {
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

        var retention = new RetentionService(database.Writes, new PersistenceRetentionOptions
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

        var retention = new RetentionService(database.Writes, new PersistenceRetentionOptions
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
            await TestPersistenceWriter.SeedSearchResultAsync(context, new SearchResultEntity
            {
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
            database.Writes,
            options,
            new FixedTimeProvider(now)).RunBatchAsync();

        Assert.AreEqual(0, first.PrunedJobs);
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            Assert.IsTrue(await context.Jobs.AnyAsync(row => row.Id == searchId));
            Assert.AreEqual(1, await context.SearchResults.CountAsync(row => row.SearchJobId == searchId));
        }

        RetentionResult second = await new RetentionService(
            database.Writes,
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
                await TestPersistenceWriter.SeedSearchResultAsync(context, new SearchResultEntity
                {
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
        var retention = new RetentionService(database.Writes, new PersistenceRetentionOptions
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
        private TestPersistenceWriter? writer;
        public PersistenceInbox Writes => (writer ??= new TestPersistenceWriter(Factory)).Inbox;

        public async ValueTask DisposeAsync()
        {
            if (writer != null) await writer.DisposeAsync();
            Owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
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
