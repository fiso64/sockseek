using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server.PeerRestrictions;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PeerRestrictionCoordinatorTests
{
    [TestMethod]
    public async Task IndependentDurableOverridesMergeWithConfigurationAcrossRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-peer-restriction-coordinator-tests",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Engine = new EngineSettings
            {
                PeerRestrictions = new PeerRestrictionSettings
                {
                    UploadAccess = new UploadAccessSettings
                    {
                        BlockedUsernames = ["Configured", "ResetMe"],
                        BlockedIpAddresses = ["192.0.2.9"],
                    },
                    PrivateMessages = new PrivateMessageAccessSettings
                    {
                        BlockedUsernames = ["Configured"],
                    },
                },
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
        });
        try
        {
            var firstPersistence = CreatePersistence(options);
            await firstPersistence.StartAsync(CancellationToken.None);
            await using (var first = CreateCoordinator(options, firstPersistence))
            {
                await first.StartAsync(CancellationToken.None);
                UserRestrictionsDto configured = first.Get("Configured");
                Assert.IsTrue(configured.UploadAccess.IsBlocked);
                Assert.IsTrue(configured.PrivateMessages.IsBlocked);

                UserRestrictionsDto allowedUpload = await first.SetAsync(
                    "Configured",
                    UserRestrictionKind.UploadAccess,
                    UserRestrictionOverrideState.Allowed,
                    CancellationToken.None);
                Assert.IsFalse(allowedUpload.UploadAccess.IsBlocked);
                Assert.IsTrue(allowedUpload.UploadAccess.ConfiguredUsernameBlocked);
                Assert.AreEqual(
                    UserRestrictionOverrideState.Allowed,
                    allowedUpload.UploadAccess.Override);
                Assert.IsTrue(allowedUpload.PrivateMessages.IsBlocked,
                    "Upload access and private-message restrictions must be independent.");

                UserRestrictionsDto blockedMessages = await first.SetAsync(
                    "ExactUser",
                    UserRestrictionKind.PrivateMessages,
                    UserRestrictionOverrideState.Blocked,
                    CancellationToken.None);
                Assert.IsTrue(blockedMessages.PrivateMessages.IsBlocked);
                Assert.IsFalse(blockedMessages.UploadAccess.IsBlocked);
                Assert.IsFalse(first.Get("exactuser").PrivateMessages.IsBlocked);
                Assert.IsTrue(first.Policy.IsUploadAccessBlocked(
                    "Configured",
                    new IPEndPoint(IPAddress.Parse("192.0.2.9"), 1)));
            }
            await firstPersistence.StopAsync(CancellationToken.None);

            var restartedPersistence = CreatePersistence(options);
            await restartedPersistence.StartAsync(CancellationToken.None);
            await using (var restarted = CreateCoordinator(options, restartedPersistence))
            {
                await restarted.StartAsync(CancellationToken.None);
                Assert.IsFalse(restarted.Get("Configured").UploadAccess.IsBlocked);
                Assert.IsTrue(restarted.Get("Configured").PrivateMessages.IsBlocked);
                Assert.IsTrue(restarted.Get("ExactUser").PrivateMessages.IsBlocked);
                Assert.IsFalse(restarted.Get("ExactUser").UploadAccess.IsBlocked);

                UserRestrictionsDto reset = await restarted.SetAsync(
                    "Configured",
                    UserRestrictionKind.UploadAccess,
                    null,
                    CancellationToken.None);
                Assert.IsTrue(reset.UploadAccess.IsBlocked);
                Assert.IsNull(reset.UploadAccess.Override);
            }
            await restartedPersistence.StopAsync(CancellationToken.None);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DisabledPersistenceLeavesConfiguredPolicyAvailableAndMutationUnapplied()
    {
        var options = Options.Create(new ServerOptions
        {
            Engine = new EngineSettings
            {
                PeerRestrictions = new PeerRestrictionSettings
                {
                    PrivateMessages = new PrivateMessageAccessSettings
                    {
                        BlockedUsernames = ["Configured"],
                    },
                },
            },
            Persistence = new ServerPersistenceOptions { Enabled = false },
        });
        var persistence = CreatePersistence(options);
        await persistence.StartAsync(CancellationToken.None);
        await using var coordinator = CreateCoordinator(options, persistence);
        await coordinator.StartAsync(CancellationToken.None);
        Assert.IsTrue(coordinator.Get("Configured").PrivateMessages.IsBlocked);
        await Assert.ThrowsExactlyAsync<PeerRestrictionPersistenceUnavailableException>(() =>
            coordinator.SetAsync(
                "Configured",
                UserRestrictionKind.PrivateMessages,
                UserRestrictionOverrideState.Allowed,
                CancellationToken.None));
        Assert.IsTrue(coordinator.Get("Configured").PrivateMessages.IsBlocked);
    }

    [TestMethod]
    public async Task ConcurrentMutationsPublishTheSameFinalStateThatWasPersisted()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-peer-restriction-concurrency-tests",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Engine = new EngineSettings(),
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
        });
        try
        {
            UserRestrictionsDto live;
            var firstPersistence = CreatePersistence(options);
            await firstPersistence.StartAsync(CancellationToken.None);
            await using (var coordinator = CreateCoordinator(options, firstPersistence))
            {
                await coordinator.StartAsync(CancellationToken.None);
                Task<UserRestrictionsDto>[] mutations = Enumerable.Range(0, 12)
                    .Select(index => coordinator.SetAsync(
                        "ExactUser",
                        UserRestrictionKind.UploadAccess,
                        (index & 1) == 0
                            ? UserRestrictionOverrideState.Blocked
                            : UserRestrictionOverrideState.Allowed,
                        CancellationToken.None))
                    .ToArray();
                Task<UserRestrictionsDto>[] reads = Enumerable.Range(0, 12)
                    .Select(_ => Task.Run(() => coordinator.Get("ExactUser")))
                    .ToArray();
                await Task.WhenAll(mutations.Cast<Task>().Concat(reads));
                Assert.IsTrue(reads.All(read => read.Result.Username == "ExactUser"));
                live = coordinator.Get("ExactUser");
            }
            await firstPersistence.StopAsync(CancellationToken.None);

            var restartedPersistence = CreatePersistence(options);
            await restartedPersistence.StartAsync(CancellationToken.None);
            await using (var restarted = CreateCoordinator(options, restartedPersistence))
            {
                await restarted.StartAsync(CancellationToken.None);
                UserRestrictionsDto durable = restarted.Get("ExactUser");
                Assert.AreEqual(live.UploadAccess.Override, durable.UploadAccess.Override);
                Assert.AreEqual(live.UploadAccess.IsBlocked, durable.UploadAccess.IsBlocked);
            }
            await restartedPersistence.StopAsync(CancellationToken.None);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static PersistenceCoordinator CreatePersistence(IOptions<ServerOptions> options)
        => new(options, NullLogger<PersistenceCoordinator>.Instance);

    private static PeerRestrictionCoordinator CreateCoordinator(
        IOptions<ServerOptions> options,
        PersistenceCoordinator persistence)
        => new(options, persistence, NullLogger<PeerRestrictionCoordinator>.Instance);
}
