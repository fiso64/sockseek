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
    public async Task ArtifactRetentionMigrationDiscardsDeadSessionPinsAndPreservesUploads()
    {
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>()
            .MigrateAsync("20261101110000_CompactSearchAttributes");
        string id = Guid.NewGuid().ToString("N");
        context.InputArtifacts.Add(new()
        {
            Id = id, Sha256 = new string('a', 64), Length = 123,
            CreatedAtUtc = 10, ExpiresAtUtc = 20, OriginalName = "音楽.csv",
        });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO input_artifact_pins (artifact_id, owner_kind, owner_id, created_at_utc)
            VALUES ({id}, 'preview', {Guid.NewGuid()}, 10),
                   ({id}, 'submission', {Guid.NewGuid()}, 10);
            """);

        await database.Initializer.InitializeAsync();

        var artifact = await context.InputArtifacts.AsNoTracking().SingleAsync();
        Assert.AreEqual(id, artifact.Id);
        Assert.AreEqual(123L, artifact.Length);
        Assert.AreEqual("音楽.csv", artifact.OriginalName);
        Assert.AreEqual(new string('a', 64), artifact.Sha256);
        Assert.AreEqual(10L, artifact.CreatedAtUtc);
        Assert.AreEqual(20L, artifact.ExpiresAtUtc);
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_schema WHERE name='input_artifact_pins'").SingleAsync());
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [TestMethod]
    public async Task SearchAttributeMigrationPreservesExactMetadataAndObservationIdentities()
    {
        const string previous = "20261101090000_RemoveUnusedJobCreationIndexes";
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        Guid runtime = Guid.NewGuid(), job = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        context.RuntimeSessions.Add(new() { Id = runtime, Version = "attributes" });
        context.Jobs.Add(JobRow(job, runtime, 1, "Terminal", now, now, "Search"));
        context.SearchJobs.Add(new() { JobId = job, Revision = 4, ResultCount = 4,
            IsComplete = true, ResultPersistenceState = "Complete" });
        await context.SaveChangesAsync();
        var expected = new Sockseek.Core.Snapshots.FileAttributeSnapshot[]
        {
            new("曲\0", int.MinValue, -7), new("BitRate", 320, 0),
            new("duplicate", int.MaxValue, -7),
        };
        string named = System.Text.Json.JsonSerializer.Serialize(expected.Select(attribute => new
            { Code = attribute.StableCode, Name = attribute.Type, attribute.Value }));
        string?[] stored = [named, "[]", null, "null"];
        for (int index = 0; index < stored.Length; index++)
        {
            await TestPersistenceWriter.SeedSearchResultAsync(context, new()
            {
                SearchJobId = job, Sequence = index + 1, Revision = index + 1,
                Username = "Péér", RemoteFilename = $@"共有\{index}.flac", SizeBytes = 128,
                Extension = "flac", AttributesJson = "[]", Visibility = "Public",
            });
            // Seed the previous database format explicitly; current writers
            // deliberately understand only the migrated representation.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE search_observations SET attributes_json={stored[index]} WHERE source_sequence={index + 1}");
        }
        Guid directory = Guid.NewGuid();
        var directoryInput = new SearchProjectionInput(1, 1, "Péér", 1, @"共有\directory.flac",
            128, 320, 24, 96000, 123, "flac", 1000, true, expected, DateTimeOffset.UnixEpoch);
        long directoryFact;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await using var observations = await SearchObservationWriter.ForDirectoryAsync(
                (Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection(),
                (Microsoft.Data.Sqlite.SqliteTransaction)transaction.GetDbTransaction(), job, directory, CancellationToken.None);
            directoryFact = await observations.StoreDirectoryAsync(directoryInput, CancellationToken.None, 0);
            await transaction.CommitAsync();
        }
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE search_observations SET attributes_json={named} WHERE id={directoryFact}");
        long[] identities = await context.Database.SqlQueryRaw<long>(
            "SELECT id AS Value FROM search_observations ORDER BY id").ToArrayAsync();

        async Task VerifyCurrentAsync()
        {
            var page = (await new SearchHistoryReader(database.Factory).GetRawResultsAsync(job, 0, 500))!;
            Assert.AreEqual(4, page.Items.Count);
            CollectionAssert.AreEqual(expected, page.Items[0].Attributes!.ToArray());
            Assert.AreEqual(0, page.Items[1].Attributes!.Count);
            Assert.IsNull(page.Items[2].Attributes);
            Assert.IsNull(page.Items[3].Attributes);
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await using var observations = await SearchObservationWriter.ForDirectoryAsync(
                    (Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection(),
                    (Microsoft.Data.Sqlite.SqliteTransaction)transaction.GetDbTransaction(), job, directory, CancellationToken.None);
                Assert.AreEqual(directoryFact, await observations.StoreDirectoryAsync(directoryInput, CancellationToken.None, 0),
                    "Reusing the same frozen directory must survive migration's JSON escaping changes.");
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => observations.StoreDirectoryAsync(
                    directoryInput with { Attributes = [expected[0] with { Value = 0 }, .. expected.Skip(1)] },
                    CancellationToken.None, 0).AsTask());
                await transaction.CommitAsync();
            }
            CollectionAssert.AreEqual(identities, await context.Database.SqlQueryRaw<long>(
                "SELECT id AS Value FROM search_observations ORDER BY id").ToArrayAsync());
            Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        }

        await context.Database.MigrateAsync();
        await VerifyCurrentAsync();
        await migrator.MigrateAsync(previous);
        string downgraded = await context.Database.SqlQueryRaw<string>(
            "SELECT attributes_json AS Value FROM search_observations WHERE source_sequence=1").SingleAsync();
        using (var document = System.Text.Json.JsonDocument.Parse(downgraded))
        {
            CollectionAssert.AreEqual(expected, document.RootElement.EnumerateArray().Select(attribute =>
                new Sockseek.Core.Snapshots.FileAttributeSnapshot(attribute.GetProperty("Name").GetString()!,
                    attribute.GetProperty("Value").GetInt32(), attribute.GetProperty("Code").GetInt32())).ToArray());
        }
        await context.Database.MigrateAsync();
        await VerifyCurrentAsync();
    }

    [TestMethod]
    public async Task UploadGroupMigrationSeparatesAmbiguousPathsAndPreservesHistoryAndExistingRunIds()
    {
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync("20261026100000_ShareSearchObservations");
        Guid runtime = Guid.NewGuid();
        context.RuntimeSessions.Add(new RuntimeSessionEntity { Id = runtime, Version = "upload-groups" });
        const string path = @"Public\音楽";
        string run = "opaque-current-run";
        var rows = new[]
        {
            Row("Upload", path, path, "Peer"),
            Row("upload", path, null, "peer"),
            Row("Upload", path, path, "Peer"),
            Row("Upload", run, path, "Peer"),
            Row("Upload", run, path, "Peer"),
            Row("Download", path, path, "Peer"),
            Row("Upload", null, null, "Peer"),
        };
        rows[2].ArchivedAtUtc = 789;
        context.Transfers.AddRange(rows);
        var attempt = new TransferAttemptEntity
        {
            Id = Guid.NewGuid(), TransferId = rows[0].Id, LastRuntimeId = runtime,
            AttemptNumber = 1, Source = "SoulseekPeer", State = "Completed", Revision = 1,
            SourceUsername = "Peer", SourcePath = path + @"\File.bin",
            StartedAtUtc = 110, CompletedAtUtc = 120,
        };
        context.TransferAttempts.Add(attempt);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.MigrateAsync();
        var actual = await context.Transfers.AsNoTracking().ToDictionaryAsync(row => row.Id);
        var normalized = rows.Take(3).Select(row => actual[row.Id]).ToArray();
        Assert.AreEqual(3, normalized.Select(row => row.GroupRef).Distinct().Count());
        Assert.IsTrue(normalized.All(row => row.GroupRef != null && row.GroupRef != path && row.GroupDisplayPath == path));
        Assert.AreEqual("Peer", normalized[0].Username);
        Assert.AreEqual("peer", normalized[1].Username);
        Assert.AreEqual(789L, normalized[2].ArchivedAtUtc);
        foreach (var expected in rows)
        {
            var row = actual[expected.Id];
            Assert.AreEqual(expected.RemotePath, row.RemotePath);
            Assert.AreEqual(128L, row.TransferredBytes);
            Assert.AreEqual(1, row.AttemptCount);
            Assert.AreEqual("Succeeded", row.TerminalOutcome);
            Assert.AreEqual(expected.Revision, row.Revision);
        }
        Assert.AreEqual(run, actual[rows[3].Id].GroupRef);
        Assert.AreEqual(run, actual[rows[4].Id].GroupRef);
        Assert.AreEqual(path, actual[rows[5].Id].GroupRef);
        Assert.IsNull(actual[rows[6].Id].GroupRef);
        Assert.AreEqual(attempt.SourcePath, (await context.TransferAttempts.SingleAsync()).SourcePath);
        await context.Database.MigrateAsync();
        CollectionAssert.AreEquivalent(actual.Values.Select(row => row.GroupRef).ToArray(),
            await context.Transfers.Select(row => row.GroupRef).ToArrayAsync());

        TransferEntity Row(string direction, string? group, string? display, string username) => new()
        {
            Id = Guid.NewGuid(), LastRuntimeId = runtime, Direction = direction, Source = "SoulseekPeer",
            Username = username, RemotePath = path + @"\File.bin", GroupRef = group, GroupDisplayPath = display,
            State = "Completed", TerminalOutcome = "Succeeded", TotalBytes = 128, TransferredBytes = 128,
            CreatedAtUtc = 100, StartedAtUtc = 110, CompletedAtUtc = 120, AttemptCount = 1, Revision = 2,
        };
    }

    [TestMethod]
    public async Task ObsoleteDirectoryViewMigrationExpiresOnlyOldCachesAndPreservesSourcesAndAcceptedJobs()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        await using var context = await database.Factory.CreateDbContextAsync();
        Guid runtime = Guid.NewGuid(), source = Guid.NewGuid(), accepted = Guid.NewGuid();
        context.RuntimeSessions.Add(new() { Id = runtime, Version = "view-migration" });
        context.Jobs.AddRange(
            new JobEntity { Id = source, WorkflowId = source, LastRuntimeId = runtime, DisplayId = 1,
                Kind = "Search", LifecycleState = "Terminal", TerminalOutcome = "Succeeded", CompletedAtUtc = 1 },
            new JobEntity { Id = accepted, WorkflowId = accepted, LastRuntimeId = runtime, DisplayId = 2,
                Kind = "Song", LifecycleState = "Queued" });
        context.SearchJobs.Add(new() { JobId = source, Revision = 1, ResultCount = 1, IsComplete = true,
            ResultPersistenceState = "Complete" });
        await context.SaveChangesAsync();
        var input = new SearchProjectionInput(1, 1, "Péér", 1, @"共有\Track.flac", 128,
            320, 16, 44100, 180, ".flac", 1000, true, null, DateTimeOffset.UnixEpoch);
        await TestPersistenceWriter.SeedSearchResultAsync(context, new()
        {
            SearchJobId = source, Sequence = 1, Revision = 1,
            Username = input.Username, RemoteFilename = input.Filename, SizeBytes = input.Size,
            BitRate = input.BitRate, BitDepth = input.BitDepth, SampleRate = input.SampleRate,
            DurationSeconds = input.Length, Extension = input.Extension, UploadSpeed = input.UploadSpeed,
            HasFreeUploadSlot = input.HasFreeUploadSlot, ResponseFileCount = 1, Visibility = "Public",
            ObservedAtUtc = input.ObservedAtUtc.ToUnixTimeMilliseconds(),
        });
        await using var store = new SearchViewStore(database.Options.GetFullDatabasePath(), database.Writes);
        await store.InitializeAsync();
        var kernel = new SearchViewKernel(new SearchViewProjectionDefinition(
            SearchViewProjectionKind.GenericDirectories, new SongQuery { Title = "Track" }, IncludeFullResults: true),
            new SearchSettings { NecessaryCond = new FileConditions(), PreferredCond = new FileConditions() });
        var update = kernel.Apply([input], 1, true);
        var current = await store.CreateAsync(source, SearchViewProjectionKind.GenericDirectories,
            "{\"genericDirectoryGroupingVersion\":2}");
        current = await store.PublishAsync(current.Id, update, "Complete");
        string currentPage = System.Text.Json.JsonSerializer.Serialize(
            await store.GetDirectoriesAsync(current.Id, current.Revision, null, 100, initialFileLimit: 24));
        var obsolete = new List<Guid>();
        foreach (string definition in new[] { "{}", "{\"genericDirectoryGroupingVersion\":1}", "{\"genericDirectoryGroupingVersion\":\"2\"}", "{\"genericDirectoryGroupingVersion\":0}" })
        {
            var old = await store.CreateAsync(source, SearchViewProjectionKind.GenericDirectories, definition);
            await store.PublishAsync(old.Id, update, "Complete");
            obsolete.Add(old.Id);
        }
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE search_views SET definition_json={"broken"} WHERE id={obsolete[^1].ToString("D")}");
        var files = await store.CreateAsync(source, SearchViewProjectionKind.Files, "{}");
        await context.Database.GetService<IMigrator>().MigrateAsync("20261027140000_CompactSearchFileIdentity");
        await database.Initializer.InitializeAsync();
        foreach (Guid id in obsolete) Assert.IsNull(await store.GetAsync(id));
        Assert.IsNotNull(await store.GetAsync(files.Id));
        Assert.AreEqual(current.Revision, (await store.GetAsync(current.Id))!.Revision);
        Assert.AreEqual(currentPage, System.Text.Json.JsonSerializer.Serialize(
            await store.GetDirectoriesAsync(current.Id, current.Revision, null, 100, initialFileLimit: 24)));
        Assert.AreEqual(1, (await new SearchHistoryReader(database.Factory).GetRawResultsAsync(source, 0, 500))!.Items.Count);
        Assert.AreEqual("Queued", (await context.Jobs.AsNoTracking().SingleAsync(row => row.Id == accepted)).LifecycleState);
        Assert.AreEqual(1L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM search_observations").SingleAsync());
        Assert.AreEqual(0L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
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
    public async Task HistoryIndexMigrationPreservesPagingExactPeersArchivesAndAttempts()
    {
        const string previous = "20261025140000_AddTransferGroupRelativePath";
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        Guid runtime = Guid.NewGuid(), parent = Guid.NewGuid(), child = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        context.RuntimeSessions.Add(new RuntimeSessionEntity { Id = runtime, Version = "history-index-migration" });
        var root = JobRow(parent, runtime, 1, "Terminal", now, now, "JobList");
        var song = JobRow(child, runtime, 2, "Terminal", now, now, "Song");
        song.ParentJobId = parent;
        context.Jobs.AddRange(root, song);
        var ids = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid()).ToArray();
        for (int index = 0; index < ids.Length; index++)
        {
            context.Transfers.Add(new TransferEntity
            {
                Id = ids[index], JobId = child, WorkflowId = root.WorkflowId,
                LastRuntimeId = runtime, Direction = "Download", Source = "SoulseekPeer",
                Username = index % 2 == 0 ? "Péér" : "péér", RemotePath = @"Share\曲\Track.flac",
                LocalPath = "accepted.flac", State = "Completed", TerminalOutcome = "Succeeded",
                TotalBytes = 128, TransferredBytes = 128, AttemptCount = 2,
                CreatedAtUtc = 100 + index, StartedAtUtc = 1000 - index, CompletedAtUtc = 2000 + index,
                ArchivedAtUtc = index >= 2 ? 3000 : null, Revision = 2,
            });
            for (int number = 1; number <= 2; number++)
                context.TransferAttempts.Add(new TransferAttemptEntity
                {
                    Id = Guid.NewGuid(), TransferId = ids[index], LastRuntimeId = runtime,
                    AttemptNumber = number, Source = "SoulseekPeer", State = "Completed",
                    SourceUsername = "Péér", SourcePath = @"Share\曲\Track.flac",
                    StartedAtUtc = 1000 - number, CompletedAtUtc = 2000 + number, Revision = 2,
                });
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var reader = new TransferHistoryReader(database.Factory);
        var query = new TransferHistoryQuery(Limit: 1, Direction: "dOwNlOaD");
        var original = await reader.GetTransfersAsync(query);
        Assert.AreEqual(ids[1], original.Items.Single().Id);

        async Task VerifyAsync()
        {
            var first = await reader.GetTransfersAsync(query);
            Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(original),
                System.Text.Json.JsonSerializer.Serialize(first));
            Assert.AreEqual(ids[0], (await reader.GetTransfersAsync(query with { Cursor = first.NextCursor })).Items.Single().Id);
            CollectionAssert.AreEqual(new[] { ids[3], ids[2] },
                (await reader.GetTransfersAsync(query with { Archived = true, Limit = 500 })).Items.Select(item => item.Id).ToArray());
            Assert.AreEqual(ids[0], (await reader.GetTransfersAsync(query with { Username = "Péér" })).Items.Single().Id);
            Assert.AreEqual(ids[1], (await reader.GetTransfersAsync(query with { Username = "péér" })).Items.Single().Id);
            Assert.AreEqual(child, (await new JobHistoryReader(database.Factory).GetJobsAsync(
                new(ParentJobId: parent))).Items.Single().Id);
            var attempts = (await reader.GetAttemptsAsync(ids[0], 0, 1))!;
            Assert.AreEqual(1, attempts.Items.Single().AttemptNumber);
            Assert.AreEqual(2, (await reader.GetAttemptsAsync(ids[0], attempts.NextAttemptNumber!.Value, 1))!.Items.Single().AttemptNumber);
            Assert.AreEqual(2, (await reader.GetTransferAsync(ids[0]))!.LatestAttempt!.AttemptNumber);
            await context.Database.OpenConnectionAsync();
            Assert.AreEqual("ok", Scalar(context, "PRAGMA integrity_check;"));
            Assert.AreEqual(0, QueryStrings(context, "PRAGMA foreign_key_check;").Count);
        }

        await context.Database.MigrateAsync();
        await context.Database.OpenConnectionAsync();
        Assert.AreEqual(0, QueryStrings(context,
            "SELECT name FROM sqlite_master WHERE type='index' AND name IN ('IX_jobs_created_at_utc_id','IX_jobs_workflow_id_created_at_utc_id');").Count);
        Assert.IsFalse(context.Database.HasPendingModelChanges());
        await VerifyAsync();
        await migrator.MigrateAsync(previous);
        Assert.AreEqual(2, QueryStrings(context,
            "SELECT name FROM sqlite_master WHERE type='index' AND name IN ('IX_jobs_created_at_utc_id','IX_jobs_workflow_id_created_at_utc_id');").Count);
        await VerifyAsync();
        await context.Database.MigrateAsync();
        await VerifyAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM transfers WHERE id = {ids[0]};");
        Assert.AreEqual(6, await context.TransferAttempts.CountAsync());
        Assert.AreEqual(3, await context.Transfers.CountAsync());
    }

    [TestMethod]
    public async Task SearchHistoryMigrationsPreserveFactsVisibilityAndConstraints()
    {
        const string previous = "20261025110000_ConsolidateSearchViewMembershipIndexes";
        await using var database = new TemporaryDatabase();
        await using var context = await database.Factory.CreateDbContextAsync();
        var migrator = context.Database.GetService<IMigrator>();
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
        var original = (await reader.GetRawResultsAsync(job, 0, 500))!;
        string logicalRows = System.Text.Json.JsonSerializer.Serialize(
            await context.SearchResults.AsNoTracking().OrderBy(row => row.Sequence).ToArrayAsync());

        async Task VerifyAsync(bool legacy = false, bool currentSchema = true)
        {
            Assert.AreEqual(logicalRows, System.Text.Json.JsonSerializer.Serialize(
                await context.SearchResults.AsNoTracking().OrderBy(row => row.Sequence).ToArrayAsync()));
            // Upgrade/downgrade must preserve the old schema's complete logical
            // rows too. Runtime readers only operate after startup migration.
            if (currentSchema)
            {
                var page = (await reader.GetRawResultsAsync(job, 0, 500))!;
                Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(original.Items), System.Text.Json.JsonSerializer.Serialize(page.Items));
                Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(original.Items[0]),
                    System.Text.Json.JsonSerializer.Serialize((await reader.GetResultAsync(job, "Péér", @"Share\曲\Track.flac"))!.Result));
            }
            // Both replay identity and exact peer/path/visibility uniqueness survive.
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(legacy
                    ? "UPDATE search_results SET sequence = 1 WHERE sequence = 2;"
                    : "UPDATE search_observations SET source_sequence = 1 WHERE source_sequence = 2;"));
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(legacy
                    ? "UPDATE search_results SET visibility = 'Public' WHERE sequence = 2;"
                    : "UPDATE search_observations SET visibility = 'Public' WHERE source_sequence = 2;"));
            await context.Database.OpenConnectionAsync();
            Assert.AreEqual("ok", Scalar(context, "PRAGMA integrity_check;"));
            Assert.AreEqual(0, QueryStrings(context, "PRAGMA foreign_key_check;").Count);
        }

        await migrator.MigrateAsync("20261031020000_ExpireObsoleteConditionFacts");
        // Older projection could have retained a fact before raw ingestion. Its
        // marker is null even while this source has confirmed raw history.
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO search_observations(source_id,origin_kind,origin_id,source_sequence,source_revision,
                username,response_file_count,filename,size_bytes,bit_rate,bit_depth,sample_rate,duration_seconds,
                extension,upload_speed,has_free_upload_slot,queue_length,attributes_json,observed_at_utc,visibility)
            SELECT source_id,0,'',3,3,username,response_file_count,filename||'.uncommitted',size_bytes,
                bit_rate,bit_depth,sample_rate,duration_seconds,extension,upload_speed,has_free_upload_slot,
                queue_length,attributes_json,observed_at_utc,visibility FROM search_observations WHERE source_sequence=1;
            """);
        long viewOnlyId = await context.Database.SqlQueryRaw<long>(
            "SELECT id AS Value FROM search_observations WHERE source_sequence=3").SingleAsync();
        await VerifyAsync(currentSchema: false);
        await context.Database.MigrateAsync();
        Assert.IsFalse(context.Database.HasPendingModelChanges());
        await VerifyAsync();
        Assert.AreEqual(viewOnlyId, await context.Database.SqlQueryRaw<long>(
            "SELECT id AS Value FROM search_observations WHERE source_sequence=3 AND origin_kind=2").SingleAsync());
        await migrator.MigrateAsync(previous);
        await VerifyAsync(legacy: true, currentSchema: false);
        await context.Database.MigrateAsync();
        await VerifyAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM search_jobs;");
        Assert.AreEqual(0, await context.SearchResults.CountAsync());
    }

    [TestMethod]
    public async Task SubmissionPayloadMove_RollsBackOnFailureAndPreservesIntentThroughUpgradeAndDowngrade()
    {
        const string previousMigration = "20261006100000_AddSearchDirectoryObservations";
        await using var database = new TemporaryDatabase();
        Guid sourceId = Guid.NewGuid();
        Guid rerunId = Guid.NewGuid();
        Guid futureId = Guid.NewGuid();
        Guid previewId = Guid.NewGuid();
        var settings = new DownloadSettings();
        settings.Output.ParentDir = "/operator/Exact \u03bc path";
        string sourceJson = SubmissionSpecificationCodec.Serialize(
            SubmissionSpecification.Create(new SearchJob("Migration Exact"), settings));
        string rerunJson = SubmissionSpecificationCodec.Serialize(
            SubmissionSpecification.Create(new SearchJob("Migration Rerun"), settings));
        const string receiptJson = "{\"accepted\":true}";
        const string futureJson = "{\"futureIntent\":\"ExactUser\\\\Odd\\\\\u03bc.wav\"}";
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync(previousMigration);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO submissions (
                id, submitted_at_utc, specification_schema_version, specification_json,
                rerun_of_submission_id, preview_id, artifact_id,
                revision, archived_at_utc, commit_fingerprint, commit_receipt_json)
            VALUES ({sourceId}, 1000, {SubmissionSpecification.CurrentSchemaVersion}, {sourceJson},
                    NULL, {previewId}, 'artifact', 7, 2000, 'fingerprint', {receiptJson}),
                   ({rerunId}, 1001, {SubmissionSpecification.CurrentSchemaVersion}, {rerunJson},
                    {sourceId}, NULL, NULL, 1, NULL, NULL, NULL),
                   ({futureId}, 1002, 99, {futureJson}, NULL, NULL, NULL, 9, NULL, NULL, NULL);
            """);
        // A failed migration must not leave half-moved intent. This operator
        // index deliberately prevents native DROP COLUMN after the copy.
        await context.Database.ExecuteSqlRawAsync("CREATE INDEX migration_guard ON submissions(specification_json);");
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => context.Database.MigrateAsync());
        Assert.AreEqual("3", Scalar(context, "SELECT count(*) FROM submissions;"));
        Assert.AreEqual("0", Scalar(context,
            "SELECT count(*) FROM sqlite_master WHERE name = 'submission_specifications';"));
        CollectionAssert.AreEqual(new[] { sourceJson, rerunJson, futureJson }, await context.Database
            .SqlQueryRaw<string>("SELECT specification_json AS Value FROM submissions ORDER BY submitted_at_utc")
            .ToArrayAsync());
        await context.Database.ExecuteSqlRawAsync("DROP INDEX migration_guard;");
        await context.Database.MigrateAsync();
        Assert.IsFalse(context.Database.HasPendingModelChanges());

        async Task VerifyAcceptedDataAsync()
        {
            var rows = await context.Submissions.AsNoTracking().ToDictionaryAsync(row => row.Id);
            Assert.AreEqual(3, rows.Count);
            var source = rows[sourceId];
            Assert.AreEqual(sourceJson, source.SpecificationJson);
            Assert.AreEqual(SubmissionSpecification.CurrentSchemaVersion, source.SpecificationSchemaVersion);
            Assert.AreEqual(1000L, source.SubmittedAtUtc);
            Assert.AreEqual(7L, source.Revision);
            Assert.AreEqual(2000L, source.ArchivedAtUtc);
            Assert.AreEqual(previewId, source.PreviewId);
            Assert.AreEqual("artifact", source.ArtifactId);
            Assert.AreEqual("fingerprint", source.CommitFingerprint);
            Assert.AreEqual("{\"accepted\":true}", source.CommitReceiptJson);
            Assert.AreEqual(sourceId, rows[rerunId].RerunOfSubmissionId);
            Assert.AreEqual(rerunJson, rows[rerunId].SpecificationJson);
            Assert.AreEqual(99, rows[futureId].SpecificationSchemaVersion);
            Assert.AreEqual(futureJson, rows[futureId].SpecificationJson);
            Assert.AreEqual(9L, rows[futureId].Revision);
            var restored = SubmissionSpecificationCodec.Deserialize(source.SpecificationJson);
            Assert.IsInstanceOfType<SearchJob>(restored.MaterializeJob());
            Assert.AreEqual(settings.Output.ParentDir, restored.MaterializeSettings().Output.ParentDir);
            Assert.AreEqual("ok", Scalar(context, "PRAGMA integrity_check;"));
            Assert.AreEqual(0, QueryStrings(context, "PRAGMA foreign_key_check;").Count);
        }

        await VerifyAcceptedDataAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync(previousMigration);
        CollectionAssert.AreEqual(new[] { sourceJson, rerunJson, futureJson }, await context.Database
            .SqlQueryRaw<string>("SELECT specification_json AS Value FROM submissions ORDER BY submitted_at_utc")
            .ToArrayAsync());
        await context.Database.MigrateAsync();
        await VerifyAcceptedDataAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM submissions WHERE id = {rerunId};");
        Assert.AreEqual("2", Scalar(context, "SELECT count(*) FROM submission_specifications;"),
            "Payload storage must share the submission's lifetime, not leave orphaned plans.");
    }

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

        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        Assert.AreEqual((await context.Database.GetAppliedMigrationsAsync()).Last(), result.SchemaVersion);
        Assert.AreEqual("1", Scalar(context, "PRAGMA foreign_keys;"));
        Assert.AreEqual("2", Scalar(context, "PRAGMA synchronous;"));
        Assert.AreEqual("1", Scalar(context,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_search_view_directory_files_path';"));
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
                "search_views", "search_view_revision_records", "search_view_files",
                "search_view_directory_records", "search_view_directory_version_records",
                "search_view_directory_files", "search_view_aggregate_tracks",
                "search_view_aggregate_track_versions", "search_view_aggregate_track_files",
                "search_view_album_records", "search_view_album_version_records",
                "search_view_album_directory_records", "search_view_peers",
                "search_directory_observations", "search_directory_observation_files",
                "search_view_directory_retrievals",
                "directory_manifests", "directory_manifest_entries",
            },
            tables.ToArray());
    }

    [TestMethod]
    public async Task PreviousSchemaFixture_UpgradesInPlace_AndPreservesRows()
    {
        await using var database = new TemporaryDatabase();
        Guid runtimeId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid aggregateId = Guid.NewGuid(), executionId = Guid.NewGuid(), albumId = Guid.NewGuid();
        Guid ordinaryListId = Guid.NewGuid(), ordinaryAlbumId = Guid.NewGuid(), otherWorkflowAlbumId = Guid.NewGuid();
        const string payload = "{\"retained\":\"Exact 共有 path\"}";
        await using (var previous = await database.Factory.CreateDbContextAsync())
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync("20260711200436_InitialPersistence");
            Assert.AreEqual(0L, Convert.ToInt64(await previous.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_jobs_workflow_id_display_id_id'").SingleAsync()));
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
            long displayId = 1;
            foreach (var row in new (Guid Id, Guid? Parent, string Kind, Guid Workflow)[]
            {
                (aggregateId, null, "AlbumAggregate", workflowId),
                (executionId, aggregateId, "JobList", workflowId),
                (albumId, executionId, "Album", workflowId),
                (ordinaryListId, jobId, "JobList", workflowId),
                (ordinaryAlbumId, ordinaryListId, "Album", workflowId),
                (otherWorkflowAlbumId, executionId, "Album", Guid.NewGuid()),
            })
            {
                await previous.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO jobs (
                        id, workflow_id, parent_job_id, last_runtime_id, last_sequence, display_id, kind,
                        lifecycle_state, activity_phase, terminal_outcome, skip_reason,
                        cancellation_source, failure_reason, created_at_utc, updated_at_utc,
                        completed_at_utc, revision, payload_schema_version, payload_json)
                    VALUES (
                        {row.Id}, {row.Workflow}, {row.Parent}, {runtimeId}, 1, {++displayId}, {row.Kind},
                        'Terminal', 'None', 'Succeeded', 'None',
                        'None', 'None', 0, 0, 0, 1, 1, {payload});
                    """);
            }
        }

        await using (var upgraded = await database.Factory.CreateDbContextAsync())
        {
            await upgraded.Database.MigrateAsync();
            Assert.AreEqual(1L, Convert.ToInt64(await upgraded.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_jobs_workflow_id_display_id_id'").SingleAsync()));
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
            var rows = await upgraded.Jobs.AsNoTracking().ToDictionaryAsync(job => job.Id);
            Assert.AreEqual(7, rows.Count);
            Assert.AreEqual(aggregateId, rows[albumId].ParentJobId);
            Assert.AreEqual(aggregateId, rows[executionId].ParentJobId);
            Assert.AreEqual(ordinaryListId, rows[ordinaryAlbumId].ParentJobId);
            Assert.AreEqual(executionId, rows[otherWorkflowAlbumId].ParentJobId,
                "Do not reinterpret unrelated ownership across workflows.");
            Assert.IsTrue(rows.Values.Where(job => job.Id != jobId).All(job => job.PayloadJson == payload
                && job.TerminalOutcome == "Succeeded" && job.Revision == 1));
            await upgraded.Database.OpenConnectionAsync();
            Assert.AreEqual(0, QueryStrings(upgraded, "PRAGMA foreign_key_check;").Count);
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
