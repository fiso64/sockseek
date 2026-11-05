using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.PeerRestrictions;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class PeerRestrictionOverrideStoreTests
{
    [TestMethod]
    public async Task ExactOverridesUpsertRemoveAndSurviveRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-peer-restriction-store-tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "sockseek.db");
        try
        {
            Directory.CreateDirectory(directory);
            var options = new SockseekSqliteOptions(path);
            var first = CreateHost(options);
            await first.StartAsync();
            PeerRestrictionOverrideStore store = first.PeerRestrictions!;
            await store.SetAsync(
                PeerRestrictionKind.UploadAccess,
                "ExactUser",
                PeerUsernameRestrictionOverride.Blocked);
            await store.SetAsync(
                PeerRestrictionKind.PrivateMessages,
                "ExactUser",
                PeerUsernameRestrictionOverride.Allowed);
            await store.SetAsync(
                PeerRestrictionKind.UploadAccess,
                "ExactUser",
                PeerUsernameRestrictionOverride.Allowed);
            await store.SetAsync(
                PeerRestrictionKind.UploadAccess,
                "RemovedUser",
                PeerUsernameRestrictionOverride.Blocked);
            await store.SetAsync(PeerRestrictionKind.UploadAccess, "RemovedUser", null);
            await first.StopAsync(TimeSpan.FromSeconds(5));

            var restarted = CreateHost(options);
            await restarted.StartAsync();
            IReadOnlyList<StoredPeerRestrictionOverride> rows =
                await restarted.PeerRestrictions!.ReadAllAsync();
            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.All(row => row.Username == "ExactUser"));
            Assert.AreEqual(
                PeerUsernameRestrictionOverride.Allowed,
                rows.Single(row => row.Kind == PeerRestrictionKind.UploadAccess).Value);
            Assert.AreEqual(
                PeerUsernameRestrictionOverride.Allowed,
                rows.Single(row => row.Kind == PeerRestrictionKind.PrivateMessages).Value);
            await restarted.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static PersistenceRuntimeHost CreateHost(SockseekSqliteOptions options)
        => new(
            options,
            new PersistenceWriterOptions(),
            new PersistenceRetentionOptions(),
            "test");
}
