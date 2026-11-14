using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class PersistenceMaintenanceTests
{
    [TestMethod]
    public async Task DurableStoresShareTransactionOwnershipWhileTemporaryViewsRemainAvailable()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var host = fixture.Host;
        var inbox = (PersistenceInbox)host.MutationSink!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = inbox.ExecuteAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await entered.Task;
        Task[] pending = [];
        try
        {
            pending = [
                host.PeerRestrictions!.SetAsync(Sockseek.Core.Sharing.PeerRestrictionKind.UploadAccess,
                    "Peer", Sockseek.Core.Sharing.PeerUsernameRestrictionOverride.Blocked),
                host.InputArtifacts!.CreateAsync(new MemoryStream([1, 2, 3]), "input.csv"),
                host.RunRetentionAsync(CancellationToken.None),
                host.CheckpointAsync(CancellationToken.None),
            ];
            Assert.IsTrue(pending.All(task => !task.IsCompleted), "Durable mutations must share the held writer.");
            Assert.AreEqual(0, (await host.PeerRestrictions.ReadAllAsync()).Count,
                "Independent readers see committed data while commands wait.");
        }
        finally
        {
            release.TrySetResult();
            await held;
            await Task.WhenAll(pending);
        }
        Assert.AreEqual("Peer", (await host.PeerRestrictions!.ReadAllAsync()).Single().Username);
        Assert.AreEqual(PersistenceHealthState.Healthy, host.HealthSnapshot!.State);
    }

    [TestMethod]
    public async Task EveryMaintenanceOperationUsesAndReleasesTheSharedGate()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        SemaphoreSlim gate = MaintenanceGate(fixture.Host);
        var operations = new Func<CancellationToken, Task>[]
        {
            async ct => { _ = await fixture.Host.CheckIntegrityAsync(ct); },
            async ct => { _ = await fixture.Host.BackupAsync(Path.Combine(fixture.Directory, "backup.db"), ct); },
            async ct => { _ = await fixture.Host.CheckpointAsync(ct); },
            async ct => { _ = await fixture.Host.RunRetentionAsync(ct); },
        };

        foreach (var operation in operations)
        {
            await gate.WaitAsync();
            Task pending = operation(CancellationToken.None);
            Assert.IsFalse(pending.IsCompleted, "Maintenance ran while another maintenance operation owned the gate.");
            gate.Release();
            await pending;

            Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(1)),
                "Maintenance did not release the gate after success.");
            gate.Release();
        }
    }

    [TestMethod]
    public async Task CallerCancellationDoesNotDamageHealthAndReleasesNoUnownedGate()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        SemaphoreSlim gate = MaintenanceGate(fixture.Host);
        int failureEvents = 0;
        fixture.Host.Health.FailureRecorded += () => failureEvents++;
        var operations = new Func<CancellationToken, Task>[]
        {
            async ct => { _ = await fixture.Host.CheckIntegrityAsync(ct); },
            async ct => { _ = await fixture.Host.BackupAsync(Path.Combine(fixture.Directory, Guid.NewGuid() + ".db"), ct); },
            async ct => { _ = await fixture.Host.CheckpointAsync(ct); },
            async ct => { _ = await fixture.Host.RunRetentionAsync(ct); },
        };

        foreach (var operation in operations)
        {
            await gate.WaitAsync();
            using var cancellation = new CancellationTokenSource();
            Task pending = operation(cancellation.Token);
            cancellation.Cancel();
            try
            {
                await pending;
                Assert.Fail("Cancelled maintenance unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }

            Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(1)));
            gate.Release();
        }

        Assert.AreEqual(0, failureEvents);
        Assert.AreEqual(PersistenceHealthState.Healthy, fixture.Host.HealthSnapshot?.State);
    }

    [TestMethod]
    public async Task OperationalFailureIsRecordedOnceRethrownAndReleasesTheGate()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        SemaphoreSlim gate = MaintenanceGate(fixture.Host);
        int failureEvents = 0;
        fixture.Host.Health.FailureRecorded += () => failureEvents++;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.Host.BackupAsync(fixture.DatabasePath));

        Assert.AreEqual(1, failureEvents);
        Assert.AreEqual(PersistenceHealthState.Unhealthy, fixture.Host.HealthSnapshot?.State);
        Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(1)),
            "Maintenance did not release the gate after failure.");
        gate.Release();
    }

    [TestMethod]
    public async Task FailedArtifactDeletionDoesNotBlockOtherCleanupAndRetriesWithoutRestart()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var host = fixture.Host;
        var store = host.InputArtifacts!;
        var blocked = await store.CreateAsync(new MemoryStream([1]), "blocked.csv");
        var other = await store.CreateAsync(new MemoryStream([2]), "other.csv");
        string blockedPath = (await store.ResolveAsync(blocked.Id))!.Path;
        string otherPath = (await store.ResolveAsync(other.Id))!.Path;
        File.Delete(blockedPath);
        Directory.CreateDirectory(blockedPath); // File.Delete cannot unlink a directory.
        var inbox = (PersistenceInbox)host.MutationSink!;
        await inbox.ExecuteAsync((context, ct) => context.InputArtifacts
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresAtUtc, 0), ct),
            CancellationToken.None);

        await host.RunRetentionAsync();

        Assert.IsFalse(File.Exists(otherPath), "One failed unlink must not stop unrelated cleanup.");
        Assert.IsTrue(Directory.Exists(blockedPath));
        Assert.AreEqual(1, await CountArtifactsAsync());
        Assert.AreEqual(PersistenceHealthState.Healthy, host.HealthSnapshot!.State,
            "A blob deletion failure must not mark the database unhealthy.");

        Directory.Delete(blockedPath);
        await File.WriteAllTextAsync(blockedPath, "now removable");
        await host.RunRetentionAsync();

        Assert.IsFalse(File.Exists(blockedPath), "The ordinary retention pass must retry failed files.");
        Assert.AreEqual(0, await CountArtifactsAsync());

        Task<int> CountArtifactsAsync() => inbox.ExecuteAsync(
            (context, ct) => context.InputArtifacts.CountAsync(ct), CancellationToken.None,
            requiresTransaction: false);
    }

    private static SemaphoreSlim MaintenanceGate(PersistenceRuntimeHost host)
        => (SemaphoreSlim)typeof(PersistenceRuntimeHost)
            .GetField("maintenanceGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(string directory, string databasePath, PersistenceRuntimeHost host)
        {
            Directory = directory;
            DatabasePath = databasePath;
            Host = host;
        }

        public string Directory { get; }
        public string DatabasePath { get; }
        public PersistenceRuntimeHost Host { get; }

        public static async Task<RuntimeFixture> StartAsync()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "sockseek-maintenance-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            string databasePath = Path.Combine(directory, "sockseek.db");
            var host = new PersistenceRuntimeHost(
                new SockseekSqliteOptions(databasePath),
                new PersistenceWriterOptions(),
                new PersistenceRetentionOptions(),
                "test");
            await host.StartAsync();
            return new RuntimeFixture(directory, databasePath, host);
        }

        public async ValueTask DisposeAsync()
        {
            if (Host.IsStarted)
                await Host.StopAsync(TimeSpan.FromSeconds(5));
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
