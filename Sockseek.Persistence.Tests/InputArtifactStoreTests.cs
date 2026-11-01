using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Planning;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class InputArtifactStoreTests
{
    [TestMethod]
    public async Task ActiveReadersProtectExpiredInputsWithoutDurableWrites()
    {
        await using var database = await SharedDatabase.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        byte[] content = Encoding.UTF8.GetBytes("artist,title\nA,T\n");
        await using var store = database.CreateStore(clock);
        await store.InitializeAsync();
        StoredInputArtifact artifact = await store.CreateAsync(
            new MemoryStream(content, writable: false), "../../unsafe.csv", TimeSpan.FromMinutes(1));
        Assert.AreEqual("unsafe.csv", artifact.OriginalName);
        Assert.AreEqual(content.LongLength, artifact.Length);
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))
            .ToLowerInvariant(), artifact.Sha256);
        Guid preview = Guid.NewGuid();
        Guid submission = Guid.NewGuid();
        long commits = database.Health.Snapshot(database.Writes).SuccessfulCommitCount;
        var inputs = await store.RetainAsync([artifact.Id, artifact.Id], preview);
        Assert.AreEqual(1, inputs.Count);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(inputs[artifact.Id].Path));
        await store.RetainAsync([artifact.Id], preview);
        clock.Advance(TimeSpan.FromHours(1));
        await store.RetainAsync([artifact.Id], submission);
        Assert.AreEqual(0, await store.ReleaseAsync(Guid.NewGuid()));
        Assert.AreEqual(1, await store.ReleaseAsync(preview));
        Assert.AreEqual(0, await store.ReleaseAsync(preview));
        Assert.IsNotNull(await store.ResolveAsync(artifact.Id), "The other reader still owns the input.");
        Assert.AreEqual(commits, database.Health.Snapshot(database.Writes).SuccessfulCommitCount,
            "Session-only retention must never create durable commits.");
        Assert.AreEqual(0, await store.PruneExpiredAsync());
        commits = database.Health.Snapshot(database.Writes).SuccessfulCommitCount;
        Assert.AreEqual(1, await store.ReleaseAsync(submission));
        Assert.AreEqual(commits, database.Health.Snapshot(database.Writes).SuccessfulCommitCount);
        Assert.IsNull(await store.ResolveAsync(artifact.Id));
        string path = inputs[artifact.Id].Path;
        Assert.IsTrue(File.Exists(path), "Release must not run global blob pruning.");
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task RestartRetainsUploadsButNotSessionReferences()
    {
        await using var database = await SharedDatabase.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        string artifactId;
        await using (var store = database.CreateStore(clock))
        {
            await store.InitializeAsync();
            var artifact = await store.CreateAsync(new MemoryStream([1, 2, 3]), "input.csv", TimeSpan.FromMinutes(1));
            artifactId = artifact.Id;
            await store.RetainAsync([artifactId], Guid.NewGuid());
        }
        await using var reopened = database.CreateStore(clock);
        await reopened.InitializeAsync();
        var input = (await reopened.ResolveAsync(artifactId))!;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(input.Path));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsNull(await reopened.ResolveAsync(artifactId));
        Assert.AreEqual(1, await reopened.PruneExpiredAsync());
        Assert.IsFalse(File.Exists(input.Path));
        Assert.IsFalse(File.Exists(Path.Combine(database.BlobDirectory, "artifacts.db")),
            "Artifact metadata must use the main persistence database.");
    }

    [TestMethod]
    public async Task FailedBatchRetentionDoesNotKeepOtherInputsAlive()
    {
        await using var database = await SharedDatabase.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        await using var store = database.CreateStore(clock);
        await store.InitializeAsync();
        var artifact = await store.CreateAsync(new MemoryStream([1]), "input.csv", TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RetainAsync([artifact.Id, Guid.NewGuid().ToString("N")], Guid.NewGuid()));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsNull(await store.ResolveAsync(artifact.Id));
        Assert.AreEqual(1, await store.PruneExpiredAsync());
    }

    [TestMethod]
    public async Task ArtifactWritesShareCancellableWriterAdmissionWithoutBlockingReads()
    {
        await using var database = await SharedDatabase.CreateAsync();
        await using (var store = database.CreateStore())
        {
            await store.InitializeAsync();
            StoredInputArtifact artifact = await store.CreateAsync(new MemoryStream([1, 2, 3]), "input.csv");
            Guid preview = Guid.NewGuid();
            await store.RetainAsync([artifact.Id], preview);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task held = database.Writes.ExecuteAsync(async (_, _) =>
            {
                entered.SetResult();
                await releaseWriter.Task;
                return true;
            }, CancellationToken.None);
            await entered.Task;
            try
            {
                using var cancellation = new CancellationTokenSource();
                Guid cancelledOwner = Guid.NewGuid();
                Task<int> release = store.ReleaseAsync(preview, cancellation.Token);
                var acquire = store.RetainAsync([artifact.Id], cancelledOwner, cancellation.Token);
                Assert.IsFalse(release.IsCompleted);
                Assert.IsFalse(acquire.IsCompleted);
                Assert.IsNotNull(await store.ResolveAsync(artifact.Id), "Reads do not wait for the writer.");
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => release);
                await Assert.ThrowsAsync<OperationCanceledException>(() => acquire);
            }
            finally
            {
                releaseWriter.TrySetResult();
                await held;
            }
            Assert.AreEqual(1, await store.ReleaseAsync(preview), "Cancelled queued work must not release a reference.");
        }
        Assert.IsTrue(await database.Writes.ExecuteAsync((_, _) => Task.FromResult(true), CancellationToken.None),
            "A feature store does not own the writer's lifetime.");
    }

    [TestMethod]
    public async Task StartupRemovesCrashOrphanedTemporaryAndBlobFiles()
    {
        await using var database = await SharedDatabase.CreateAsync();
        string orphanId = Guid.NewGuid().ToString("N");
        string orphanBlob = Path.Combine(database.BlobDirectory, orphanId + ".blob");
        string temporary = Path.Combine(
            database.BlobDirectory,
            Guid.NewGuid().ToString("N") + ".uploading");
        await File.WriteAllTextAsync(orphanBlob, "orphan");
        await File.WriteAllTextAsync(temporary, "partial");

        await using var store = database.CreateStore();
        await store.InitializeAsync();

        Assert.IsFalse(File.Exists(orphanBlob));
        Assert.IsFalse(File.Exists(temporary));
    }

    private sealed class SharedDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly SqliteDatabaseOwner owner;

        private SharedDatabase(
            string directory,
            SqliteDatabaseOwner owner,
            SockseekDbContextFactory factory)
        {
            this.directory = directory;
            this.owner = owner;
            Factory = factory;
            BlobDirectory = Path.Combine(directory, "planning", "input-artifacts");
            Directory.CreateDirectory(BlobDirectory);
        }

        public string BlobDirectory { get; }
        public SockseekDbContextFactory Factory { get; }
        public PersistenceHealth Health { get; } = new();

        public static async Task<SharedDatabase> CreateAsync()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "sockseek-input-artifact-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
            global::Tests.TestDatabase.Create(options.DatabasePath);
            SqliteDatabaseOwner owner = SqliteDatabaseOwner.Acquire(options);
            var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
            try
            {
                await new SqliteInitializer(factory, options, owner).InitializeAsync();
                return new SharedDatabase(directory, owner, factory);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        private TestPersistenceWriter? writer;
        public PersistenceInbox Writes => (writer ??= new TestPersistenceWriter(Factory, Health)).Inbox;
        public InputArtifactStore CreateStore(TimeProvider? clock = null)
            => new(BlobDirectory, Factory, Health, Writes, clock);

        public async ValueTask DisposeAsync()
        {
            if (writer != null) await writer.DisposeAsync();
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current += elapsed;
    }
}
