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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
