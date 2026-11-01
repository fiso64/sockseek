using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Sqlite;

public sealed record SqliteInitializationResult(string JournalMode, string SynchronousMode, string SchemaVersion);

public sealed class SqliteInitializer(
    IDbContextFactory<SockseekDbContext> contextFactory,
    SockseekSqliteOptions options,
    SqliteDatabaseOwner owner)
{
    internal static IReadOnlySet<string> SafeAutomaticMigrations { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "20260711200436_InitialPersistence",
        "20260712090000_AddHistoryQueryIndexes",
        "20260712170220_AddTransferAttemptSourceIdentity",
        "20260729120000_AddUploadTransferHistory",
        "20260806204353_AddChatsAndNotifications",
        "20260806213317_AddChatSequenceAllocator",
        "20260828100858_AddJobNavigationIndexes",
        "20260829230000_AddSubmissions",
        "20260830010000_AddSearchObservations",
        "20260831090000_AddSubmissionCommitReceipts",
        "20260831100000_AddTransferTimelineProjection",
        "20260831110000_AddTransferAccounting",
        "20260831120000_AddPeerRestrictions",
        "20260901010000_AddInputArtifacts",
        "20260901020000_AddSearchViews",
        "20260923120000_AddSearchViewDirectoryPathIndex",
        "20260924120000_AddDirectoryManifests",
        "20261001090000_AddTransferGroupIndex",
        "20261004100000_AddJobSourceDetail",
        "20261006100000_AddSearchDirectoryObservations",
        // Lossless in-transaction payload move; upgrade, rollback and downgrade
        // preserve accepted intent, receipts, metadata and rerun relationships.
        "20261016090000_SeparateSubmissionSpecifications",
        "20261024090000_AddSearchViewFileMembershipIndex",
        // Normalize display ownership only; preserve every job, payload and outcome.
        "20261025090000_NormalizeAggregateAlbumParents",
        // Lossless membership-table rebuild; retain identities and revision history.
        "20261025110000_ConsolidateSearchViewMembershipIndexes",
        // Reuse the existing unique sequence identity; preserve all result IDs/data.
        "20261025120000_UseSearchSequencePrimaryKey",
        // Additive nullable metadata; existing transfer identities and history are unchanged.
        "20261025140000_AddTransferGroupRelativePath",
        // Index-only replacement; every retained row and identity is preserved.
        "20261025160000_AlignHistoryIndexes",
        "20261025170000_CoverSearchViewFileOrdering",
        "20261026100000_ShareSearchObservations",
        // Normalize ambiguous grouping metadata; preserve transfers and attempts.
        "20261027100000_NormalizeUploadGroupRefs",
        // Compact internal links; retain public refs, revisions and row identities.
        "20261027120000_CompactSearchDirectoryMembership",
        "20261027140000_CompactSearchFileIdentity",
        // Discard unsupported derived caches; preserve sources and accepted jobs.
        "20261027150000_ExpireObsoleteGenericSearchViews",
        // Lossless set encoding in one transaction; keep names, tiers and revisions.
        "20261028190000_CompactSearchConditionFacts",
        // Copy immutable best-child ranks once; retain refs, membership and revisions.
        "20261029100000_IndexSearchDirectoryRanking",
        "20261029110000_IndexAggregateAlbumPaging",
        // Lossless group-key rebuild; retain canonical refs and every snapshot edge/version.
        "20261030100000_CompactAggregateTrackMembership",
        // Remove only derived candidate rows with no historical group membership.
        "20261030140000_RemoveUnpublishedTrackCandidates",
        // Keep all historical album links; discard only unreferenced candidates.
        "20261030170000_RemoveUnpublishedAlbumCandidates",
        // Directory versions own child visibility; expire incompatible album caches.
        "20261030200000_UseDirectoryVisibility",
        // Pin facts by source, with database-enforced View/fact ownership.
        "20261030220000_ScopeSearchFactRetention",
        "20261030230000_CompactSearchRevisions",
        "20261031010000_CompactSearchDirectories",
        // Discard outdated projected facts; raw sources and accepted work remain.
        "20261031020000_ExpireObsoleteConditionFacts",
        // Drop an unused hash; retain numeric references, facts and raw visibility.
        "20261031100000_SimplifySearchObservationIdentity",
        "20261101090000_RemoveUnusedJobCreationIndexes",
        "20261101110000_CompactSearchAttributes",
        // Pins only belonged to previews/in-flight admission from a dead session.
        "20261101130000_RemoveTemporaryArtifactPins",
    };

    public async Task<SqliteInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = options.GetFullDatabasePath();
        if (!string.Equals(databasePath, owner.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The database ownership lease does not match the configured database path.");
        string databaseDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(databaseDirectory);
        PersistenceFilePrivacy.RestrictDirectory(databaseDirectory);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var knownMigrations = context.Database.GetMigrations()
            .ToHashSet(StringComparer.Ordinal);
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var unknownApplied = appliedMigrations
            .Where(migration => !knownMigrations.Contains(migration))
            .ToArray();
        if (unknownApplied.Length > 0)
        {
            throw new PersistenceSchemaCompatibilityException(
                "This database was migrated by a newer or incompatible Sockseek release. " +
                "The current binary does not recognize applied migrations: " +
                string.Join(", ", unknownApplied) +
                ". Use a release that supports this database schema.");
        }
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var unapproved = pendingMigrations.Where(migration => !SafeAutomaticMigrations.Contains(migration)).ToArray();
        if (unapproved.Length > 0)
        {
            throw new PersistenceSchemaCompatibilityException(
                "Automatic migration is blocked because these migrations have no reviewed safety classification: "
                + string.Join(", ", unapproved)
                + ". Back up the database and use a release that explicitly supports this schema transition.");
        }
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        PersistenceFilePrivacy.RestrictFile(databasePath);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var connection = context.Database.GetDbConnection();
        string journalMode = ExecuteScalar(connection, "PRAGMA journal_mode=WAL;");
        string synchronousMode = ExecuteScalar(connection, "PRAGMA synchronous;");
        string schemaVersion = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).LastOrDefault()
            ?? "none";

        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite refused WAL mode and returned '{journalMode}'. Use a supported local filesystem.");
        if (synchronousMode != "2")
            throw new InvalidOperationException($"SQLite synchronous mode must be FULL (2), but was '{synchronousMode}'.");

        return new SqliteInitializationResult(journalMode, synchronousMode, schemaVersion);
    }

    private static string ExecuteScalar(System.Data.Common.DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}
