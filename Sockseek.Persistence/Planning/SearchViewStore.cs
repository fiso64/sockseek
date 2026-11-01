using System.Text.Json;
using System.Text.Json.Nodes;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sockseek.Persistence.Write;
using SQLitePCL;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Planning;

public sealed record StoredSearchView(
    Guid Id,
    Guid SourceJobId,
    string ProjectionKind,
    string DefinitionJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    string RetentionState,
    SearchViewCounters Counters,
    SearchViewSortOrder SortOrder = SearchViewSortOrder.Relevance)
{
    // The database key stays inside Persistence; Id remains the public reference.
    internal long StorageId { get; init; }
    internal long? SourceId { get; init; }
}

public sealed record StoredSearchViewRevision(
    Guid ViewId,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    string RetentionState,
    SearchViewCounters Counters);

public sealed record StoredSearchViewFile(
    string Ref,
    long AdmittedRevision,
    SearchProjectionInput Input,
    SearchPreferenceTier PreferenceTier,
    bool NecessaryConditionsSatisfied,
    IReadOnlyList<SearchPreferenceCondition> SatisfiedPreferredConditions,
    IReadOnlyList<SearchPreferenceCondition> ConfiguredPreferredConditions);

public sealed record SearchViewFilePosition(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker,
    long Sequence,
    string Ref,
    long? PrimaryNumber = null,
    long? SecondaryNumber = null);

public sealed record StoredSearchViewFilePage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewFile> Items,
    SearchViewFilePosition? NextPosition);

public sealed record StoredSearchViewDirectory(
    string Ref,
    string Username,
    string FolderPath,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    bool IsFullyRetrieved,
    long? RetrievedFileCount,
    long? RetrievedBytes,
    StoredSearchViewFile BestChild);

public sealed record SearchViewDirectoryPosition(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker,
    long Sequence,
    string Username,
    string FolderPath,
    string Ref,
    long? PrimaryNumber = null,
    long? SecondaryNumber = null,
    string? PrimaryText = null);

public sealed record StoredSearchViewDirectoryPage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewDirectory> Items,
    SearchViewDirectoryPosition? NextPosition,
    IReadOnlyDictionary<string, StoredSearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record StoredSearchViewDirectoryFile(
    string Ref,
    string RelativePath,
    StoredSearchViewFile File);

public sealed record SearchViewDirectoryFilePosition(
    string RelativePath,
    string Ref);

public sealed record StoredSearchViewDirectoryFilePage(
    StoredSearchViewRevision Revision,
    string DirectoryRef,
    string Username,
    string FolderPath,
    IReadOnlyList<StoredSearchViewDirectoryFile> Items,
    SearchViewDirectoryFilePosition? NextPosition,
    StoredSearchViewDirectory DirectorySummary);

public sealed record StoredSearchViewDirectoryFilePreview(
    IReadOnlyList<StoredSearchViewDirectoryFile> Items,
    SearchViewDirectoryFilePosition? NextPosition);

public sealed record StoredSearchViewDirectoryPublishResult(
    StoredSearchView View,
    int NewFileCount);

public sealed record StoredSearchDirectoryObservation(
    Guid Id,
    Guid SourceJobId,
    int SourceRevision,
    SearchProjectionInput PeerObservation,
    PeerDirectorySnapshot Snapshot,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record StoredSearchDirectoryObservationTarget(
    string DirectoryRef,
    PeerDirectoryIdentity Identity,
    Guid ObservationId);

public sealed record StoredSearchViewAggregateTrackGroup(
    string Ref,
    int Index,
    string QueryJson,
    int ShareCount,
    long SelectableOptionCount,
    StoredSearchViewFile Representative);

public sealed record SearchViewAggregateTrackPosition(
    int ShareCount,
    int Index,
    string Ref);

public sealed record StoredSearchViewAggregateTrackPage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewAggregateTrackGroup> Items,
    SearchViewAggregateTrackPosition? NextPosition);

public sealed record StoredSearchViewAggregateTrackOptionPage(
    StoredSearchViewRevision Revision,
    string GroupRef,
    IReadOnlyList<StoredSearchViewFile> Items,
    SearchViewFilePosition? NextPosition);

public sealed record StoredSearchViewAggregateAlbumGroup(
    string Ref,
    int Index,
    string QueryJson,
    int ShareCount,
    long SelectableOptionCount,
    StoredSearchViewDirectory Representative);

public sealed record SearchViewAggregateAlbumPosition(
    int ShareCount,
    int Index,
    string Ref);

public sealed record StoredSearchViewAggregateAlbumPage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewAggregateAlbumGroup> Items,
    SearchViewAggregateAlbumPosition? NextPosition,
    IReadOnlyDictionary<string, StoredSearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record StoredSearchViewAggregateAlbumOptionPage(
    StoredSearchViewRevision Revision,
    string GroupRef,
    IReadOnlyList<StoredSearchViewDirectory> Items,
    SearchViewDirectoryPosition? NextPosition,
    IReadOnlyDictionary<string, StoredSearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record StoredSearchViewChosenOptions(
    StoredSearchViewRevision Revision,
    IReadOnlyDictionary<string, StoredSearchViewFile> Tracks,
    IReadOnlyDictionary<string, StoredSearchViewDirectory> Albums,
    IReadOnlyDictionary<string, StoredSearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record StoredSearchViewCommitItem(
    string Kind,
    string Ref,
    StoredSearchViewFile? File,
    StoredSearchViewDirectory? Directory,
    StoredSearchViewAggregateTrackGroup? AggregateTrackGroup = null,
    StoredSearchViewAggregateAlbumGroup? AggregateAlbumGroup = null,
    string? ParentRef = null,
    IReadOnlyList<string>? ContainerRefs = null);

public sealed record StoredSearchViewSelectionCounts(long PublicFileCount, long LockedFileCount, long SelectedItemCount = 0);

public sealed record StoredSearchViewFileRefInfo(
    string Ref,
    string DirectoryRef,
    SearchResultVisibility Visibility);

public sealed record StoredSearchViewDirectoryFileMembership(
    string Ref,
    string RelativePath,
    SearchResultVisibility Visibility);

/// <summary>
/// Disk-backed immutable search-view revisions. File rows are stored once with
/// their admission revision; revisions retain counters and query the exact
/// observed prefix through that boundary instead of copying the full view.
/// </summary>
public sealed partial class SearchViewStore(
    string databasePath,
    PersistenceInbox writes,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private static readonly ActivitySource Activities = new("Sockseek.Persistence.SearchViews");
    private readonly string path = Path.GetFullPath(databasePath);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private int initialized;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = true,
        DefaultTimeout = 5,
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
            return;
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'search_views';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)) != 1)
            {
                throw new InvalidDataException(
                    "The main persistence schema does not contain Search View tables.");
            }
        }
        catch
        {
            Interlocked.Exchange(ref initialized, 0);
            throw;
        }
    }

    public Task<StoredSearchView> CreateAsync(
        Guid sourceJobId,
        SearchViewProjectionKind projectionKind,
        string definitionJson,
        TimeSpan? retention = null,
        CancellationToken cancellationToken = default)
    {
        // This derived cache stores evaluated facts, not just the source definition.
        // Stamp its semantics at creation so upgrades can discard obsolete facts once,
        // without repairing them on reads or making callers manage storage versions.
        JsonObject definition = JsonNode.Parse(definitionJson)!.AsObject();
        definition["conditionFactsVersion"] = 2;
        definitionJson = definition.ToJsonString();
        return WithWriteAsync(async (connection, transaction, ct) =>
        {
            DateTimeOffset created = clock.GetUtcNow();
            var view = new StoredSearchView(
                Guid.NewGuid(),
                sourceJobId,
                projectionKind.ToString(),
                definitionJson,
                created,
                created + (retention ?? DefaultRetention),
                0,
                0,
                0,
                false,
                "Live",
                EmptyCounters,
                ReadSortOrder(definitionJson));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO search_views (
                    id, source_job_id, projection_kind, definition_json,
                    created_at_utc, expires_at_utc, revision, source_revision,
                    consumed_sequence, is_complete, retention_state,
                    public_file_count, locked_file_count, public_bytes, locked_bytes,
                    observed_peer_count, projected_file_count,
                    projected_public_file_count, projected_locked_file_count,
                    preferred_file_count, other_file_count,
                    top_level_item_count, selectable_option_count)
                VALUES (
                    $id, $source, $projection, $definition, $created, $expires,
                    0, 0, 0, 0, 'Live', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                """;
            Add(command, "$id", view.Id);
            Add(command, "$source", view.SourceJobId);
            Add(command, "$projection", view.ProjectionKind);
            Add(command, "$definition", definitionJson);
            Add(command, "$created", view.CreatedAtUtc);
            Add(command, "$expires", view.ExpiresAtUtc);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            view = view with { StorageId = raw.sqlite3_last_insert_rowid(connection.Handle) };
            await using var revision = connection.CreateCommand();
            revision.Transaction = transaction;
            revision.CommandText = """
                INSERT INTO search_view_revision_records (
                    view_storage_id, view_id, revision, source_revision, consumed_sequence,
                    is_complete, retention_state, public_file_count,
                    locked_file_count, public_bytes, locked_bytes,
                    observed_peer_count, projected_file_count,
                    projected_public_file_count, projected_locked_file_count,
                    preferred_file_count, other_file_count,
                    top_level_item_count, selectable_option_count)
                VALUES ($storage, $view, 0, 0, 0, 0, 'Live', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                """;
            Add(revision, "$view", view.Id);
            Add(revision, "$storage", view.StorageId);
            await revision.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return view;
        }, cancellationToken, background: false);
    }

    public Task<StoredSearchView?> GetAsync(
        Guid viewId,
        CancellationToken cancellationToken = default)
        => ReadOneAsync(viewId, cancellationToken);

    public async Task<StoredSearchViewRevision?> GetRevisionAsync(
        Guid viewId,
        long revision,
        CancellationToken cancellationToken = default)
    {
        using var operation = StartActivity("revision", viewId, revision);
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StoredSearchView> ReadIncompleteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM search_views
            WHERE is_complete = 0 AND expires_at_utc > $now
            ORDER BY created_at_utc, id;
            """;
        Add(command, "$now", clock.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadView(reader);
    }

    public Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
        => WithWriteAsync(async (connection, transaction, ct) =>
        {
            var sources = new List<Guid>();
            await using (var expired = connection.CreateCommand())
            {
                expired.Transaction = transaction;
                expired.CommandText = "SELECT DISTINCT source_job_id FROM search_views WHERE expires_at_utc <= $now;";
                Add(expired, "$now", clock.GetUtcNow());
                await using var reader = await expired.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) sources.Add(reader.GetGuid(0));
            }
            await using var observations = connection.CreateCommand();
            observations.Transaction = transaction;
            observations.CommandText =
                "DELETE FROM search_directory_observations WHERE expires_at_utc <= $now;";
            Add(observations, "$now", clock.GetUtcNow());
            await observations.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM search_views WHERE expires_at_utc <= $now;";
            Add(command, "$now", clock.GetUtcNow());
            int removed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await SearchObservationWriter.PruneUnreferencedAsync(connection, transaction, sources, ct).ConfigureAwait(false);
            return removed;
        }, cancellationToken);

    private async Task<StoredSearchView> PublishInTransactionAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid viewId,
        SearchViewKernelUpdate update, string retentionState, CancellationToken ct)
    {
        StoredSearchView current = await ReadOneAsync(
            connection,
            transaction,
            viewId,
            ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The search view was not found.");
        if (update.SourceRevision < current.SourceRevision
            || update.ConsumedSequence < current.ConsumedSequence)
            throw new InvalidOperationException("A search view publication cannot regress.");

        int peerCount;
        using (StartActivity("publish-peers", viewId, current.Revision + 1))
        {
            // Reuse one statement for the whole peer batch, as for files
            // and memberships; broad responses must not prepare per peer.
            if (update.ObservedUsernames is { Count: > 0 } observed)
            {
                using var peer = new SqliteWriteStatement(connection, transaction, """
                    INSERT OR IGNORE INTO search_view_peers (view_id, username)
                    VALUES ($view, $username);
                    """);
                peer.Bind(0, viewId.ToString("D"));
                foreach (string username in observed)
                {
                    peer.Bind(1, username);
                    peer.Execute(ct);
                }
            }
            await using var peerCountCommand = connection.CreateCommand();
            peerCountCommand.Transaction = transaction;
            peerCountCommand.CommandText =
                "SELECT COUNT(*) FROM search_view_peers WHERE view_id = $view;";
            Add(peerCountCommand, "$view", viewId);
            peerCount = checked(Convert.ToInt32(
                await peerCountCommand.ExecuteScalarAsync(ct).ConfigureAwait(false)));
        }
        SearchViewKernelUpdate effectiveUpdate = update with
        {
            Counters = update.Counters with { ObservedPeerCount = peerCount },
        };

        bool metadataChanged = effectiveUpdate.SourceRevision != current.SourceRevision
            || effectiveUpdate.ConsumedSequence != current.ConsumedSequence
            || effectiveUpdate.IsComplete != current.IsComplete
            || retentionState != current.RetentionState
            || effectiveUpdate.Counters != current.Counters;
        if (!metadataChanged && !effectiveUpdate.HasProjectionChanges)
            return current;

        long revision = checked(current.Revision + 1);
        long revisionId = await InsertRevisionAsync(
            connection, transaction, viewId, revision, effectiveUpdate, retentionState, ct)
            .ConfigureAwait(false);
        var fileRefs = new Dictionary<
            (string Username, string Filename, SearchResultVisibility Visibility),
            FileIdentity>();
        using var fileWriter = new SqliteWriteStatement(connection, transaction, FileInsertSql);
        long sourceId = 0;
        Dictionary<(long Sequence, int Revision), long> observationIds = [];
        if (effectiveUpdate.ChangedFiles.Count > 0)
        {
            using (StartActivity("publish-observations", viewId, revision))
                (sourceId, observationIds) = await SearchObservationWriter.ResolveSearchAsync(
                    connection, transaction, current.SourceJobId, effectiveUpdate.ChangedFiles, ct).ConfigureAwait(false);
            current = await PinSourceAsync(connection, transaction, current, sourceId, ct).ConfigureAwait(false);
        }
        using (StartActivity("publish-files", viewId, revision))
            foreach (ProjectedFileCandidate file in effectiveUpdate.ChangedFiles)
            {
                string itemRef = StableRef(
                    current.SourceJobId,
                    "file",
                    file.Input.Username,
                    file.Input.Filename,
                    file.Input.Visibility.ToString());
                long observationId = observationIds[(file.Input.Sequence, file.Input.Revision)];
                long? id = InsertFile(fileWriter, current.StorageId, sourceId, itemRef, revision, file, observationId, ct);
                fileRefs[(file.Input.Username, file.Input.Filename, file.Input.Visibility)] = new(itemRef, id ?? 0);
            }

        await WriteProjectionRowsAsync(connection, transaction, current, revision, revisionId,
            effectiveUpdate, fileRefs, ct).ConfigureAwait(false);
        await UpdateSummaryAsync(
            connection, transaction, viewId, revision, effectiveUpdate, retentionState, ct)
            .ConfigureAwait(false);

        return current with
        {
            Revision = revision,
            SourceRevision = effectiveUpdate.SourceRevision,
            ConsumedSequence = effectiveUpdate.ConsumedSequence,
            IsComplete = effectiveUpdate.IsComplete,
            RetentionState = retentionState,
            Counters = effectiveUpdate.Counters,
        };
    }

    public Task<StoredSearchViewDirectoryPublishResult> PublishRetrievedDirectoryAsync(
        Guid viewId,
        string directoryRef,
        PeerDirectorySnapshot snapshot,
        IReadOnlyList<ProjectedFileCandidate> files,
        SearchProjectionInput peerObservation,
        CancellationToken cancellationToken = default,
        Guid? existingObservationId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(peerObservation);
        if (!StringComparer.Ordinal.Equals(peerObservation.Username, snapshot.Identity.Username))
            throw new ArgumentException(
                "The peer observation does not match the retrieved directory peer.",
                nameof(peerObservation));
        // A refinement can exclude fetched files. Keep the exact snapshot as
        // retrieval evidence while publishing only this view's visible rows.
        var retrievedFiles = snapshot.Files
            .ToDictionary(file => (file.Username, file.Filename));
        if (files.Any(file => file.Input.Visibility != SearchResultVisibility.Public
            || !retrievedFiles.ContainsKey((file.Input.Username, file.Input.Filename))))
            throw new ArgumentException(
                "Projected directory files must belong to the retrieved snapshot.",
                nameof(files));
        return WithWriteAsync<StoredSearchViewDirectoryPublishResult>(async (connection, transaction, ct) =>
        {
            StoredSearchView current = await ReadOneAsync(
                connection,
                transaction,
                viewId,
                ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The search view was not found.");

            string username;
            string folderPath;
            long directoryId;
            bool directoryVisible;
            await using (var identity = connection.CreateCommand())
            {
                identity.Transaction = transaction;
                identity.CommandText = """
                    SELECT username, folder_path, id,
                           (SELECT is_removed = 0 FROM search_view_directory_versions
                            WHERE view_id = $view AND item_ref = $directory
                            ORDER BY revision DESC LIMIT 1) AS is_visible
                    FROM search_view_directories
                    WHERE view_id = $view AND item_ref = $directory;
                    """;
                Add(identity, "$view", viewId);
                Add(identity, "$directory", directoryRef);
                await using SqliteDataReader reader = await identity.ExecuteReaderAsync(ct)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new KeyNotFoundException("The search-view directory was not found.");
                username = reader.GetString(0);
                folderPath = reader.GetString(1);
                directoryId = reader.GetInt64(2);
                directoryVisible = !reader.IsDBNull(3) && reader.GetBoolean(3);
            }
            if (!StringComparer.Ordinal.Equals(username, snapshot.Identity.Username)
                || !StringComparer.Ordinal.Equals(folderPath, snapshot.Identity.FolderPath))
            {
                throw new InvalidOperationException(
                    "The retrieved directory identity does not match the search-view ref.");
            }

            Guid observationId;
            if (existingObservationId is { } retainedId)
            {
                await using (var applied = connection.CreateCommand())
                {
                    applied.Transaction = transaction;
                    applied.CommandText = """
                        SELECT observation_id
                        FROM search_view_retrieval_publications
                        WHERE view_id = $view AND directory_ref = $directory;
                        """;
                    Add(applied, "$view", viewId);
                    Add(applied, "$directory", directoryRef);
                    if (StringComparer.Ordinal.Equals(
                        await applied.ExecuteScalarAsync(ct).ConfigureAwait(false) as string,
                        retainedId.ToString("D")))
                        return new(current, 0);
                }
                await using var validate = connection.CreateCommand();
                validate.Transaction = transaction;
                validate.CommandText = """
                    SELECT 1 FROM search_directory_observations
                    WHERE id = $id AND source_job_id = $source
                      AND username = $username AND folder_path = $path
                      AND expires_at_utc > $now;
                    """;
                Add(validate, "$id", retainedId);
                Add(validate, "$source", current.SourceJobId);
                Add(validate, "$username", snapshot.Identity.Username);
                Add(validate, "$path", snapshot.Identity.FolderPath);
                Add(validate, "$now", clock.GetUtcNow());
                if (await validate.ExecuteScalarAsync(ct).ConfigureAwait(false) == null)
                    throw new InvalidOperationException(
                        "The retrieved directory observation is no longer available.");
                observationId = retainedId;
            }
            else
            {
                observationId = await SaveDirectoryObservationAsync(
                    connection,
                    transaction,
                    current.SourceJobId,
                    current.SourceRevision,
                    peerObservation,
                    snapshot,
                    ct).ConfigureAwait(false);
            }

            // Arrivals may remove/absorb the folder while its browse is in
            // flight. Keep the reusable snapshot, but do not copy an older
            // visible version back into the current projection.
            if (!directoryVisible)
                return new(current, 0);

            long revision = checked(current.Revision + 1);
            var update = new SearchViewKernelUpdate(
                current.SourceRevision,
                current.ConsumedSequence,
                current.IsComplete,
                current.Counters,
                []);
            long revisionId = await InsertRevisionAsync(
                connection,
                transaction,
                viewId,
                revision,
                update,
                current.RetentionState,
                ct).ConfigureAwait(false);

            int newFileCount = 0;
            var requestedFiles = files.Select(file => (File: file, Ref: StableRef(
                current.SourceJobId, "file", file.Input.Username, file.Input.Filename,
                file.Input.Visibility.ToString()))).ToArray();
            var existingFileIds = await ReadFileIdsAsync(connection, transaction, current.StorageId,
                requestedFiles.Select(file => file.Ref).ToArray(), ct).ConfigureAwait(false);
            await using var observations = await SearchObservationWriter.ForDirectoryAsync(connection, transaction, current.SourceJobId, observationId, ct).ConfigureAwait(false);
            if (files.Count > 0)
                current = await PinSourceAsync(connection, transaction, current, observations.SourceId, ct).ConfigureAwait(false);
            using var fileWriter = new SqliteWriteStatement(connection, transaction, FileInsertSql);
            var ordinals = snapshot.Files.Select((file, ordinal) => (file.Filename, ordinal))
                .ToDictionary(item => item.Filename, item => item.ordinal, StringComparer.Ordinal);
            using var directoryChildWriter = CreateDirectoryChildInsert(connection, transaction, revision);
            foreach (var (file, fileRef) in requestedFiles)
            {
                // Retrieval may include files already admitted by the search.
                // Keep their original observation and ranking; do not store an
                // unreferenced replacement fact for an ignored membership insert.
                if (!existingFileIds.TryGetValue(fileRef, out long fileId))
                {
                    long fact = await observations.StoreDirectoryAsync(file.Input, ct, ordinals[file.Input.Filename]).ConfigureAwait(false);
                    fileId = InsertFile(fileWriter, current.StorageId, observations.SourceId, fileRef, revision, file, fact, ct)
                        ?? throw new InvalidDataException("A retrieved file was not inserted.");
                    existingFileIds.Add(fileRef, fileId);
                    newFileCount++;
                }
                InsertDirectoryChild(
                    directoryChildWriter,
                    directoryId,
                    fileId,
                    RelativePath(folderPath, file.Input.Filename),
                    ct);
            }

            (long retrievedCount, long? retrievedBytes) = await
                ReadVisibleDirectoryTotalsAsync(
                    connection, transaction, directoryId,
                    revision, observationId, ct).ConfigureAwait(false);
            await using (var directory = connection.CreateCommand())
            {
                directory.Transaction = transaction;
                directory.CommandText = $"""
                    INSERT INTO search_view_directory_version_records (
                        view_storage_id, directory_id, revision, best_file_id,
                        public_matching_count, locked_matching_count,
                        public_matching_bytes, locked_matching_bytes,
                        is_fully_retrieved, retrieved_file_count, retrieved_bytes,
                        is_removed, {DirectoryRankingColumns}, revision_id)
                    SELECT view_storage_id, directory_id, $revision, best_file_id,
                        public_matching_count, locked_matching_count,
                        public_matching_bytes, locked_matching_bytes,
                        $complete, $retrieved_count, $retrieved_bytes, 0,
                        {DirectoryRankingColumns}, $owner
                    FROM search_view_directory_version_records
                    WHERE directory_id = $directory
                      AND is_removed = 0
                    ORDER BY revision DESC LIMIT 1;
                    """;
                Add(directory, "$owner", revisionId);
                Add(directory, "$directory", directoryId);
                Add(directory, "$revision", revision);
                Add(directory, "$complete", snapshot.IsComplete);
                Add(directory, "$retrieved_count", retrievedCount);
                Add(directory, "$retrieved_bytes", retrievedBytes);
                if (await directory.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The search-view directory is no longer available.");
                }
            }
            await UpdateSummaryAsync(
                connection,
                transaction,
                viewId,
                revision,
                update,
                current.RetentionState,
                ct).ConfigureAwait(false);
            await using (var publication = connection.CreateCommand())
            {
                publication.Transaction = transaction;
                publication.CommandText = """
                    INSERT INTO search_view_directory_retrievals (
                        directory_id, observation_id)
                    VALUES ($directory, $observation)
                    ON CONFLICT(directory_id) DO UPDATE SET
                        observation_id = excluded.observation_id;
                    """;
                Add(publication, "$directory", directoryId);
                Add(publication, "$observation", observationId);
                await publication.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return new(
                current with { Revision = revision },
                newFileCount);
        }, cancellationToken);
    }

    public async Task<StoredSearchDirectoryObservation?> GetDirectoryObservationAsync(
        Guid sourceJobId,
        PeerDirectoryIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        Guid observationId;
        int sourceRevision;
        SearchProjectionInput peerObservation;
        bool isComplete;
        DateTimeOffset observedAt;
        DateTimeOffset expiresAt;
        int expectedFiles;
        await using (var observation = connection.CreateCommand())
        {
            observation.CommandText = """
                SELECT id, source_revision, peer_input_json, is_complete,
                       observed_at_utc, expires_at_utc, file_count
                FROM search_directory_observations
                WHERE source_job_id = $source AND username = $username
                  AND folder_path = $path AND expires_at_utc > $now;
                """;
            Add(observation, "$source", sourceJobId);
            Add(observation, "$username", identity.Username);
            Add(observation, "$path", identity.FolderPath);
            Add(observation, "$now", clock.GetUtcNow());
            await using SqliteDataReader reader = await observation.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            observationId = Guid.Parse(reader.GetString(0));
            sourceRevision = reader.GetInt32(1);
            peerObservation = JsonSerializer.Deserialize<SearchProjectionInput>(reader.GetString(2))
                ?? throw new InvalidDataException("The retrieved directory lost its peer observation.");
            isComplete = reader.GetInt64(3) != 0;
            observedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
            expectedFiles = reader.GetInt32(6);
        }

        var files = new List<PeerFileTarget>();
        await using (var children = connection.CreateCommand())
        {
            children.CommandText = """
                SELECT filename, size_bytes, extension, bit_rate, bit_depth,
                       sample_rate, duration_seconds, attributes_json
                FROM search_directory_observation_files
                WHERE observation_id = $observation
                ORDER BY ordinal;
                """;
            Add(children, "$observation", observationId);
            await using SqliteDataReader reader = await children.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string? attributes = reader.IsDBNull(7) ? null : reader.GetString(7);
                files.Add(new PeerFileTarget(
                    new PeerFileIdentity(identity.Username, reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    attributes == null
                        ? null : JsonSerializer.Deserialize<FileAttributeSnapshot[]>(attributes)));
            }
        }
        if (files.Count != expectedFiles
            || !StringComparer.Ordinal.Equals(peerObservation.Username, identity.Username))
            throw new InvalidDataException("The retrieved directory observation is incomplete.");
        return new StoredSearchDirectoryObservation(
            observationId,
            sourceJobId,
            sourceRevision,
            peerObservation,
            new PeerDirectorySnapshot(identity, files, isComplete),
            observedAt,
            expiresAt);
    }

    public async IAsyncEnumerable<StoredSearchDirectoryObservationTarget>
        ReadUnappliedDirectoryObservationsAsync(
            Guid viewId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchView? view = await ReadOneAsync(
            connection, null, viewId, cancellationToken).ConfigureAwait(false);
        if (view == null || view.ExpiresAtUtc <= clock.GetUtcNow())
            yield break;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT directory.item_ref, directory.username, directory.folder_path,
                   observation.id
            FROM search_directory_observations observation
            JOIN search_view_directories directory
              ON directory.view_id = $view
             AND directory.username = observation.username
             AND directory.folder_path = observation.folder_path
            JOIN search_view_directory_versions version
              ON version.view_id = directory.view_id
             AND version.item_ref = directory.item_ref
             AND version.revision = (
                 SELECT MAX(latest.revision)
                 FROM search_view_directory_versions latest
                 WHERE latest.view_id = directory.view_id
                   AND latest.item_ref = directory.item_ref
                   AND latest.revision <= $revision)
            LEFT JOIN search_view_retrieval_publications publication
              ON publication.view_id = directory.view_id
             AND publication.directory_ref = directory.item_ref
            WHERE observation.source_job_id = $source
              AND observation.expires_at_utc > $now
              AND version.is_removed = 0
              AND (publication.observation_id IS NULL
                   OR publication.observation_id <> observation.id)
            ORDER BY observation.username COLLATE BINARY,
                     observation.folder_path COLLATE BINARY;
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", view.Revision);
        Add(command, "$source", view.SourceJobId);
        Add(command, "$now", clock.GetUtcNow());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return new StoredSearchDirectoryObservationTarget(
                reader.GetString(0),
                new PeerDirectoryIdentity(reader.GetString(1), reader.GetString(2)),
                Guid.Parse(reader.GetString(3)));
    }

    public async Task<bool> HasNewerDirectorySourceFileAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        int sourceRevision,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 1
            FROM search_view_directory_files membership
            JOIN search_view_directories directory ON directory.id = membership.directory_id
            JOIN search_view_file_rows file
              ON file.id = membership.file_id
            WHERE directory.view_id = $view
              AND directory.item_ref = $directory
              AND membership.admitted_revision <= $revision
              AND {DirectoryVisibleAtRevisionSql("directory")}
              AND file.source_row_revision > $source_revision
            LIMIT 1;
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        Add(command, "$revision", revision);
        Add(command, "$source_revision", sourceRevision);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) != null;
    }

    private async Task<Guid> SaveDirectoryObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceJobId,
        int sourceRevision,
        SearchProjectionInput peerObservation,
        PeerDirectorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using (var replace = connection.CreateCommand())
        {
            replace.Transaction = transaction;
            replace.CommandText = """
                DELETE FROM search_directory_observations
                WHERE source_job_id = $source AND username = $username
                  AND folder_path = $path;
                """;
            Add(replace, "$source", sourceJobId);
            Add(replace, "$username", snapshot.Identity.Username);
            Add(replace, "$path", snapshot.Identity.FolderPath);
            await replace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Guid observationId = Guid.NewGuid();
        DateTimeOffset observedAt = clock.GetUtcNow();
        await using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = """
                INSERT INTO search_directory_observations (
                    id, source_job_id, username, folder_path, source_revision,
                    peer_input_json, is_complete, observed_at_utc, expires_at_utc,
                    file_count)
                VALUES ($id, $source, $username, $path, $source_revision,
                    $peer_input, $complete, $observed, $expires, $file_count);
                """;
            Add(header, "$id", observationId);
            Add(header, "$source", sourceJobId);
            Add(header, "$username", snapshot.Identity.Username);
            Add(header, "$path", snapshot.Identity.FolderPath);
            Add(header, "$source_revision", sourceRevision);
            Add(header, "$peer_input", JsonSerializer.Serialize(peerObservation));
            Add(header, "$complete", snapshot.IsComplete);
            Add(header, "$observed", observedAt);
            Add(header, "$expires", observedAt + DefaultRetention);
            Add(header, "$file_count", snapshot.Files.Count);
            await header.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var child = connection.CreateCommand();
        child.Transaction = transaction;
        child.CommandText = """
            INSERT INTO search_directory_observation_files (
                observation_id, ordinal, filename, size_bytes, extension, bit_rate,
                bit_depth, sample_rate, duration_seconds, attributes_json)
            VALUES ($observation, $ordinal, $filename, $size, $extension, $bit_rate,
                $bit_depth, $sample_rate, $length, $attributes);
            """;
        Add(child, "$observation", observationId);
        Add(child, "$ordinal", 0);
        Add(child, "$filename", "");
        Add(child, "$size", null);
        Add(child, "$extension", null);
        Add(child, "$bit_rate", null);
        Add(child, "$bit_depth", null);
        Add(child, "$sample_rate", null);
        Add(child, "$length", null);
        Add(child, "$attributes", null);
        child.Prepare();
        for (int index = 0; index < snapshot.Files.Count; index++)
        {
            PeerFileTarget file = snapshot.Files[index];
            child.Parameters["$ordinal"].Value = index;
            child.Parameters["$filename"].Value = file.Filename;
            child.Parameters["$size"].Value = file.Size is { } size ? size : DBNull.Value;
            child.Parameters["$extension"].Value = file.Extension is { } extension
                ? extension : DBNull.Value;
            child.Parameters["$bit_rate"].Value = file.BitRate is { } bitRate
                ? bitRate : DBNull.Value;
            child.Parameters["$bit_depth"].Value = file.BitDepth is { } bitDepth
                ? bitDepth : DBNull.Value;
            child.Parameters["$sample_rate"].Value = file.SampleRate is { } sampleRate
                ? sampleRate : DBNull.Value;
            child.Parameters["$length"].Value = file.Length is { } length
                ? length : DBNull.Value;
            child.Parameters["$attributes"].Value = file.Attributes is { } attributes
                ? JsonSerializer.Serialize(attributes) : DBNull.Value;
            await child.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return observationId;
    }

    public async Task<StoredSearchViewFilePage?> GetFilesAsync(
        Guid viewId,
        long revision,
        SearchViewFilePosition? after,
        int limit,
        SearchViewSortOrder? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = StartActivity("files", viewId, revision);
        ValidateLimit(limit);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        (StoredSearchView active, StoredSearchViewRevision bound) = activeRevision.Value;
        SearchViewSortOrder order = sortOrder ?? active.SortOrder;
        var admissions = order == SearchViewSortOrder.Relevance
            ? await ReadFileAdmissionsAsync(connection, active.StorageId, viewId, revision, cancellationToken).ConfigureAwait(false)
            : null;
        await using var command = connection.CreateCommand();
        command.CommandText = FilePageSql(order, after != null, admissions);
        Add(command, "$view", viewId);
        Add(command, "$storage", active.StorageId);
        Add(command, "$revision", revision);
        Add(command, "$limit", (long)limit + 1);
        if (after != null)
        {
            BindPosition(command, after);
            BindAlternativeFilePosition(command, order, after);
        }
        var rows = new List<StoredSearchViewFile>();
        SearchProjectionSortKey boundaryKey = default;
        using var fileRead = StartActivity("file-page", viewId, revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadFile(reader));
            if (rows.Count == limit)
                boundaryKey = ReadSortKey(reader);
        }
        SearchViewFilePosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = Position(rows[^1], boundaryKey, order);
        }
        return new(bound, rows, next);
    }

    private static async Task<IReadOnlyList<long>?> ReadFileAdmissionsAsync(
        SqliteConnection connection, long storageId, Guid viewId, long revision, CancellationToken cancellationToken)
    {
        using var operation = StartActivity("file-admissions", viewId, revision);
        int maximum = Math.Max(1, raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_COMPOUND_SELECT, -1));
        await using var command = connection.CreateCommand();
        // Seek the next distinct prefix rather than walking every file in the
        // admission index. Stop when a compound merge would exceed SQLite's limit;
        // the ordinary range query still handles such long-lived views.
        command.CommandText = """
            WITH RECURSIVE admissions(revision) AS (
                SELECT MIN(admitted_revision) FROM search_view_files
                WHERE view_id = $storage AND admitted_revision <= $revision
                UNION ALL
                SELECT (SELECT MIN(admitted_revision) FROM search_view_files
                        WHERE view_id = $storage AND admitted_revision > admissions.revision
                          AND admitted_revision <= $revision)
                FROM admissions WHERE revision IS NOT NULL)
            SELECT revision FROM admissions WHERE revision IS NOT NULL LIMIT $maximum;
            """;
        Add(command, "$storage", storageId);
        Add(command, "$revision", revision);
        Add(command, "$maximum", (long)maximum + 1);
        var admissions = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) admissions.Add(reader.GetInt64(0));
        return admissions.Count > maximum ? null : admissions;
    }

    public async Task<StoredSearchViewDirectoryPage?> GetDirectoriesAsync(
        Guid viewId,
        long revision,
        SearchViewDirectoryPosition? after,
        int limit,
        int initialFileLimit = 0,
        SearchViewSortOrder? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = StartActivity("directories", viewId, revision);
        ValidateLimit(limit);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        (StoredSearchView active, StoredSearchViewRevision bound) = activeRevision.Value;
        SearchViewSortOrder order = sortOrder ?? active.SortOrder;
        var rows = new List<StoredSearchViewDirectory>();
        SearchProjectionSortKey boundaryKey = default;
        SearchViewDirectoryPosition? next = null;
        using (StartActivity("directory-rows", viewId, revision))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = DirectoryPageSql(order, after != null);
            Add(command, "$view", viewId);
            Add(command, "$revision", revision);
            Add(command, "$limit", (long)limit + 1);
            if (after != null)
            {
                BindDirectoryPosition(command, after);
                BindAlternativeDirectoryPosition(command, order, after);
            }
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(ReadDirectory(reader));
                    if (rows.Count == limit)
                        boundaryKey = ReadSortKey(reader);
                }
            }
            if (rows.Count > limit)
            {
                rows.RemoveAt(rows.Count - 1);
                next = DirectoryPosition(rows[^1], boundaryKey, order);
            }
        }
        var previews = await ReadDirectoryFilePreviewsAsync(
            connection, viewId, revision, activeRevision.Value.View.Revision, rows.Select(item => item.Ref).ToArray(),
            initialFileLimit, cancellationToken).ConfigureAwait(false);
        return new(bound, rows, next, previews);
    }

    public async Task<StoredSearchViewDirectoryFilePage?> GetDirectoryFilesAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        SearchViewDirectoryFilePosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        StoredSearchViewRevision bound = activeRevision.Value.Revision;

        StoredSearchViewDirectory directorySummary;
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT directory.item_ref AS directory_ref,
                       directory.username AS directory_username,
                       directory.folder_path AS directory_folder_path,
                       version.public_matching_count,
                       version.locked_matching_count,
                       version.public_matching_bytes,
                       version.locked_matching_bytes,
                       version.is_fully_retrieved,
                       version.retrieved_file_count,
                       version.retrieved_bytes,
                       file.*
                FROM search_view_directories directory
                JOIN search_view_directory_versions version
                  ON version.view_id = directory.view_id
                 AND version.item_ref = directory.item_ref
                 AND version.revision = (
                     SELECT MAX(latest.revision)
                     FROM search_view_directory_versions latest
                     WHERE latest.view_id = directory.view_id
                       AND latest.item_ref = directory.item_ref
                       AND latest.revision <= $revision)
                JOIN search_view_file_rows file
                  ON file.id = version.best_file_id
                WHERE directory.view_id = $view AND directory.item_ref = $directory
                  AND version.is_removed = 0;
                """;
            Add(identity, "$view", viewId);
            Add(identity, "$directory", directoryRef);
            Add(identity, "$revision", revision);
            await using SqliteDataReader identityReader = await identity.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            if (!await identityReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            directorySummary = ReadDirectory(identityReader);
        }

        await using var command = connection.CreateCommand();
        // Limit lightweight memberships before joining full observation facts.
        // Public refs break equal-path ties; numeric IDs must not change cursors.
        string membershipIndex = DirectoryMembershipIndex(revision, activeRevision.Value.View.Revision);
        command.CommandText = $"""
            WITH page AS MATERIALIZED (
                SELECT child.file_id, child.relative_path, member.ref_key AS item_ref
                FROM search_view_directories directory
                JOIN search_view_directory_files child INDEXED BY {membershipIndex}
                  ON child.directory_id = directory.id
                JOIN search_view_files member ON member.id = child.file_id
                WHERE directory.view_id = $view AND directory.item_ref = $directory
                  AND child.admitted_revision <= $revision
                  AND child.relative_path >= COALESCE($cursor_relative, '')
                  AND ($cursor_relative IS NULL OR child.relative_path > $cursor_relative
                       OR member.ref_key > unhex($cursor_ref))
                ORDER BY child.relative_path COLLATE BINARY, member.ref_key COLLATE BINARY
                LIMIT $limit)
            SELECT page.relative_path AS child_relative_path, file.*
            FROM page JOIN search_view_file_rows file ON file.id = page.file_id
            ORDER BY page.relative_path COLLATE BINARY, page.item_ref COLLATE BINARY;
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        Add(command, "$revision", revision);
        Add(command, "$cursor_relative", after?.RelativePath);
        Add(command, "$cursor_ref", after?.Ref);
        Add(command, "$limit", (long)limit + 1);
        var rows = new List<StoredSearchViewDirectoryFile>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredSearchViewFile file = ReadFile(reader);
            rows.Add(new(file.Ref, Value<string>(reader, "child_relative_path"), file));
        }
        SearchViewDirectoryFilePosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = new(rows[^1].RelativePath, rows[^1].Ref);
        }
        return new(bound, directoryRef, directorySummary.Username, directorySummary.FolderPath,
            rows, next, directorySummary);
    }

    private static string DirectoryMembershipIndex(long revision, long currentRevision)
        // Latest pages stop at a path prefix. Historical pages first restrict
        // admissions, so later arrivals sorting ahead cannot turn a small old
        // snapshot into a scan of the entire current directory.
        => revision == currentRevision
            ? "ix_search_view_directory_files_path"
            : "ix_search_view_directory_files_page";

    private static async Task<IReadOnlyDictionary<string, StoredSearchViewDirectoryFilePreview>>
        ReadDirectoryFilePreviewsAsync(
            SqliteConnection connection,
            Guid viewId,
            long revision,
            long currentRevision,
            IReadOnlyCollection<string> directoryRefs,
            int limit,
            CancellationToken cancellationToken)
    {
        if (limit == 0 || directoryRefs.Count == 0)
            return new Dictionary<string, StoredSearchViewDirectoryFilePreview>(StringComparer.Ordinal);
        using var operation = StartActivity("directory-previews", viewId, revision);
        ValidateLimit(limit);
        string[] refs = directoryRefs.Distinct(StringComparer.Ordinal).ToArray();
        if (refs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Directory refs cannot be empty.", nameof(directoryRefs));

        using var rowsActivity = StartActivity("preview-rows", viewId, revision);
        // Materialize the requested directories once and probe the complete edge
        // key. A file-only correlated IN can be rerun for every member in a folder.
        // Latest reads stop at a path prefix; older snapshots restrict admissions.
        await using var command = connection.CreateCommand();
        string membershipIndex = DirectoryMembershipIndex(revision, currentRevision);
        command.CommandText = $"""
            WITH requested AS MATERIALIZED (
                SELECT id, item_ref AS directory_ref FROM search_view_directories
                WHERE view_id = $view AND item_ref IN (SELECT value FROM json_each($refs))
            )
            SELECT requested.directory_ref,
                   mapping.relative_path AS child_relative_path,
                   file.*
            FROM requested
            JOIN search_view_directory_files mapping
              ON (mapping.directory_id, mapping.file_id) IN (
                  SELECT child.directory_id, child.file_id
                  FROM search_view_directory_files child INDEXED BY {membershipIndex}
                  JOIN search_view_files member ON member.id = child.file_id
                  WHERE child.directory_id = requested.id
                    AND child.admitted_revision <= $revision
                  ORDER BY child.relative_path COLLATE BINARY, member.ref_key COLLATE BINARY
                  LIMIT $limit_plus_one)
            JOIN search_view_file_rows file ON file.id = mapping.file_id
            ORDER BY requested.directory_ref COLLATE BINARY,
                     mapping.relative_path COLLATE BINARY, file.item_ref COLLATE BINARY;
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$refs", JsonSerializer.Serialize(refs));
        Add(command, "$limit_plus_one", (long)limit + 1);
        var grouped = new Dictionary<string, List<StoredSearchViewDirectoryFile>>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string directoryRef = Value<string>(reader, "directory_ref");
            StoredSearchViewFile file = ReadFile(reader);
            if (!grouped.TryGetValue(directoryRef, out List<StoredSearchViewDirectoryFile>? children))
                grouped.Add(directoryRef, children = []);
            children.Add(new(file.Ref, Value<string>(reader, "child_relative_path"), file));
        }
        var previews = new Dictionary<string, StoredSearchViewDirectoryFilePreview>(StringComparer.Ordinal);
        foreach (string directoryRef in refs)
        {
            List<StoredSearchViewDirectoryFile> children = grouped.GetValueOrDefault(directoryRef) ?? [];
            SearchViewDirectoryFilePosition? next = null;
            if (children.Count > limit)
            {
                children.RemoveAt(children.Count - 1);
                next = new(children[^1].RelativePath, children[^1].Ref);
            }
            previews.Add(directoryRef, new(children, next));
        }
        return previews;
    }

    public async Task<StoredSearchViewAggregateTrackPage?> GetAggregateTracksAsync(
        Guid viewId,
        long revision,
        SearchViewAggregateTrackPosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        StoredSearchViewRevision bound = activeRevision.Value.Revision;
        await using var command = connection.CreateCommand();
        // Keep full file/peer hydration outside the materialized group page.
        // A broad search can have thousands of songs; page size must bound the
        // metadata joins for both initial and historical/cursor reads. One
        // indexed latest-version lookup per group avoids scanning/grouping its
        // whole revision history before choosing the requested page.
        command.CommandText = """
            WITH page AS MATERIALIZED (
                SELECT aggregate.item_ref AS aggregate_ref,
                       aggregate.group_index AS aggregate_index,
                       aggregate.query_json AS aggregate_query_json,
                       version.share_count AS aggregate_share_count,
                       version.selectable_option_count AS aggregate_option_count,
                       version.representative_file_id
                FROM search_view_aggregate_tracks aggregate
                CROSS JOIN search_view_aggregate_track_versions version
                WHERE aggregate.view_id = $view
                  AND version.group_id = aggregate.id
                  AND version.revision = (
                    SELECT MAX(latest.revision) FROM search_view_aggregate_track_versions latest
                    WHERE latest.group_id = aggregate.id
                      AND latest.revision <= $revision)
                  AND ($cursor_shares IS NULL
                       OR version.share_count < $cursor_shares
                       OR version.share_count = $cursor_shares
                          AND aggregate.group_index > $cursor_index
                       OR version.share_count = $cursor_shares
                          AND aggregate.group_index = $cursor_index
                          AND aggregate.item_ref > $cursor_ref)
                ORDER BY version.share_count DESC, aggregate.group_index, aggregate.item_ref
                LIMIT $limit)
            SELECT page.*, file.*
            FROM page
            JOIN search_view_file_rows file ON file.id = page.representative_file_id
            ORDER BY aggregate_share_count DESC, aggregate_index, aggregate_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$cursor_shares", after?.ShareCount);
        Add(command, "$cursor_index", after?.Index);
        Add(command, "$cursor_ref", after?.Ref);
        Add(command, "$limit", (long)limit + 1);
        var rows = new List<StoredSearchViewAggregateTrackGroup>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadAggregateTrack(reader));
        SearchViewAggregateTrackPosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            StoredSearchViewAggregateTrackGroup last = rows[^1];
            next = new(last.ShareCount, last.Index, last.Ref);
        }
        return new(bound, rows, next);
    }

    public async Task<StoredSearchViewAggregateTrackOptionPage?> GetAggregateTrackOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewFilePosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        StoredSearchViewRevision bound = activeRevision.Value.Revision;
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = """
                SELECT COUNT(*) FROM search_view_aggregate_tracks aggregate
                WHERE aggregate.view_id = $view AND aggregate.item_ref = $group
                  AND EXISTS (
                    SELECT 1 FROM search_view_aggregate_track_versions version
                    WHERE version.group_id = aggregate.id
                      AND version.revision <= $revision);
                """;
            Add(exists, "$view", viewId);
            Add(exists, "$group", groupRef);
            Add(exists, "$revision", revision);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)) != 1)
                return null;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = AggregateTrackOptionsSql(after != null);
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        Add(command, "$limit", (long)limit + 1);
        if (after != null)
            BindPosition(command, after);
        var rows = new List<StoredSearchViewFile>();
        SearchProjectionSortKey boundaryKey = default;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadFile(reader));
            if (rows.Count == limit)
                boundaryKey = ReadSortKey(reader);
        }
        SearchViewFilePosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = Position(rows[^1], boundaryKey);
        }
        return new(bound, groupRef, rows, next);
    }

    public async IAsyncEnumerable<StoredSearchViewFile> ReadPublicAggregateTrackOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        if (await ReadActiveRevisionAsync(connection, viewId, revision, cancellationToken).ConfigureAwait(false) == null)
            yield break;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file.*
            FROM search_view_aggregate_track_files mapping
            JOIN search_view_file_rows file
              ON file.id = mapping.file_id
            WHERE mapping.group_id = (
                SELECT id FROM search_view_aggregate_tracks WHERE view_id = $view AND item_ref = $group)
              AND mapping.admitted_revision <= $revision
              AND file.visibility = 'Public'
            ORDER BY file.sort_high DESC, file.sort_upload_fast DESC,
                     file.sort_mid DESC, file.sort_inferred DESC,
                     file.sort_upload_medium DESC, file.sort_bitrate DESC,
                     file.sort_tie DESC, file.sequence, file.item_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadFile(reader);
    }

    public async Task<StoredSearchViewAggregateAlbumPage?> GetAggregateAlbumsAsync(
        Guid viewId,
        long revision,
        SearchViewAggregateAlbumPosition? after,
        int limit,
        int initialFileLimit = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        StoredSearchViewRevision bound = activeRevision.Value.Revision;
        await using var command = connection.CreateCommand();
        command.CommandText = AggregateAlbumPageSql;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$cursor_shares", after?.ShareCount);
        Add(command, "$cursor_index", after?.Index);
        Add(command, "$cursor_ref", after?.Ref);
        Add(command, "$limit", (long)limit + 1);
        var rows = new List<StoredSearchViewAggregateAlbumGroup>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadAggregateAlbum(reader));
        }
        SearchViewAggregateAlbumPosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            StoredSearchViewAggregateAlbumGroup last = rows[^1];
            next = new(last.ShareCount, last.Index, last.Ref);
        }
        var previews = await ReadDirectoryFilePreviewsAsync(
            connection, viewId, revision, activeRevision.Value.View.Revision, rows.Select(item => item.Representative.Ref).ToArray(),
            initialFileLimit, cancellationToken).ConfigureAwait(false);
        return new(bound, rows, next, previews);
    }

    public async Task<StoredSearchViewAggregateAlbumOptionPage?> GetAggregateAlbumOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewDirectoryPosition? after,
        int limit,
        int initialFileLimit = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        StoredSearchViewRevision bound = activeRevision.Value.Revision;
        await using var command = connection.CreateCommand();
        command.CommandText = RelevanceDirectoryPageSql(after != null, albumGroup: true);
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        Add(command, "$limit", (long)limit + 1);
        if (after != null)
            BindDirectoryPosition(command, after);
        var rows = new List<StoredSearchViewDirectory>();
        SearchProjectionSortKey boundaryKey = default;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadDirectory(reader));
                if (rows.Count == limit)
                    boundaryKey = ReadSortKey(reader);
            }
        }
        if (rows.Count == 0)
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = """
                SELECT COUNT(*) FROM search_view_aggregate_album_versions
                WHERE view_id = $view AND item_ref = $group
                  AND revision <= $revision;
                """;
            Add(exists, "$view", viewId);
            Add(exists, "$group", groupRef);
            Add(exists, "$revision", revision);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)) == 0)
                return null;
        }
        SearchViewDirectoryPosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = DirectoryPosition(rows[^1], boundaryKey);
        }
        var previews = await ReadDirectoryFilePreviewsAsync(
            connection, viewId, revision, activeRevision.Value.View.Revision, rows.Select(item => item.Ref).ToArray(),
            initialFileLimit, cancellationToken).ConfigureAwait(false);
        return new(bound, groupRef, rows, next, previews);
    }

    public async IAsyncEnumerable<StoredSearchViewDirectory> ReadPublicAggregateAlbumOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        if (await ReadActiveRevisionAsync(connection, viewId, revision, cancellationToken).ConfigureAwait(false) == null)
            yield break;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest_membership AS (
                SELECT directory_id,MAX(revision) AS revision
                FROM search_view_album_directory_records
                WHERE group_id=(SELECT id FROM search_view_album_records
                                WHERE view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view) AND item_ref=$group)
                  AND revision<=$revision
                GROUP BY directory_id)
            SELECT directory.item_ref AS directory_ref,
                   directory.username AS directory_username,
                   directory.folder_path AS directory_folder_path,
                   directory_version.public_matching_count,
                   directory_version.locked_matching_count,
                   directory_version.public_matching_bytes,
                   directory_version.locked_matching_bytes,
                   directory_version.is_fully_retrieved,
                   directory_version.retrieved_file_count,
                   directory_version.retrieved_bytes,
                   file.*
            FROM latest_membership
            CROSS JOIN search_view_album_directory_records membership
              ON membership.group_id=(SELECT id FROM search_view_album_records
                                      WHERE view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view) AND item_ref=$group)
             AND membership.directory_id=latest_membership.directory_id AND membership.revision=latest_membership.revision
            CROSS JOIN search_view_directory_version_records directory_version
              ON directory_version.directory_id=membership.directory_id AND directory_version.revision=(
                  SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
                  WHERE latest.directory_id=membership.directory_id AND latest.revision<=$revision)
            CROSS JOIN search_view_directory_records directory ON directory.id=directory_version.directory_id
            CROSS JOIN search_view_file_rows file ON file.id=directory_version.best_file_id
            WHERE membership.is_present=1 AND directory_version.is_removed=0
              AND directory_version.public_matching_count>0
            ORDER BY file.sort_high DESC, file.sort_upload_fast DESC,
                     file.sort_mid DESC, file.sort_inferred DESC,
                     file.sort_upload_medium DESC, file.sort_bitrate DESC,
                     file.sort_tie DESC, file.sequence,
                     directory.username COLLATE BINARY,
                     directory.folder_path COLLATE BINARY,
                     directory.item_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadDirectory(reader);
    }

    public async Task<StoredSearchViewChosenOptions?> ResolveChosenOptionsAsync(
        Guid viewId,
        long revision,
        IReadOnlyDictionary<string, string> choices,
        int initialFileLimit = 0,
        CancellationToken cancellationToken = default)
    {
        using var operation = StartActivity("chosen-options", viewId, revision);
        await using var connection = await OpenReadConnectionAsync(viewId, revision, cancellationToken).ConfigureAwait(false);
        var activeRevision = await ReadActiveRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (activeRevision == null)
            return null;
        (StoredSearchView active, StoredSearchViewRevision bound) = activeRevision.Value;
        bool tracks = active.ProjectionKind == "AggregateTracks";
        if (!tracks && active.ProjectionKind != "AggregateAlbums")
            throw new ArgumentException("Chosen options require an aggregate Search View.", nameof(viewId));
        var trackMatches = new Dictionary<string, StoredSearchViewFile>(StringComparer.Ordinal);
        var albumMatches = new Dictionary<string, StoredSearchViewDirectory>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = tracks ? ExactAggregateTrackOptionSql : ExactAggregateAlbumOptionSql;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$group", string.Empty);
        Add(command, "$option", string.Empty);
        foreach ((string groupRef, string optionRef) in choices)
        {
            command.Parameters["$group"].Value = groupRef;
            command.Parameters["$option"].Value = optionRef;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                continue;
            if (tracks)
                trackMatches.Add(groupRef, ReadFile(reader));
            else
                albumMatches.Add(groupRef, ReadDirectory(reader));
        }
        var previews = await ReadDirectoryFilePreviewsAsync(
            connection, viewId, revision, activeRevision.Value.View.Revision, albumMatches.Values.Select(item => item.Ref).ToArray(),
            initialFileLimit, cancellationToken).ConfigureAwait(false);
        return new(bound, trackMatches, albumMatches, previews);
    }

    private const string ExactAggregateTrackOptionSql = """
        SELECT file.*
        FROM search_view_file_rows file
        CROSS JOIN search_view_aggregate_tracks aggregate
        CROSS JOIN search_view_aggregate_track_files mapping
        WHERE mapping.file_id = file.id AND file.view_id = $view
          AND aggregate.view_id = $view AND aggregate.item_ref = $group
          AND mapping.group_id = aggregate.id
          AND file.ref_key = unhex($option) AND file.item_ref = $option AND mapping.admitted_revision <= $revision
          AND file.visibility = 'Public'
          AND EXISTS (
            SELECT 1 FROM search_view_aggregate_track_versions version
            WHERE version.group_id = aggregate.id AND version.revision <= $revision);
        """;

    private const string ExactAggregateAlbumOptionSql = """
        SELECT directory.item_ref AS directory_ref,
               directory.username AS directory_username,
               directory.folder_path AS directory_folder_path,
               directory_version.public_matching_count,
               directory_version.locked_matching_count,
               directory_version.public_matching_bytes,
               directory_version.locked_matching_bytes,
               directory_version.is_fully_retrieved,
               directory_version.retrieved_file_count,
               directory_version.retrieved_bytes,
               file.*
        FROM search_view_album_records album
        CROSS JOIN search_view_directory_records directory
        CROSS JOIN search_view_album_directory_records membership
          ON membership.group_id=album.id AND membership.directory_id=directory.id
         AND membership.revision=(
             SELECT MAX(latest.revision) FROM search_view_album_directory_records latest
             WHERE latest.group_id=album.id AND latest.directory_id=directory.id AND latest.revision<=$revision)
        CROSS JOIN search_view_directory_version_records directory_version
          ON directory_version.directory_id=directory.id AND directory_version.revision=(
             SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
             WHERE latest.directory_id=directory.id AND latest.revision<=$revision)
        CROSS JOIN search_view_file_rows file ON file.id=directory_version.best_file_id
        WHERE album.view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view)
          AND album.item_ref=$group AND directory.view_storage_id=album.view_storage_id AND directory.item_ref=$option
          AND membership.is_present=1 AND directory_version.is_removed=0
          AND directory_version.public_matching_count>0
          AND 0=(SELECT version.is_removed FROM search_view_album_version_records version
                 WHERE version.group_id=album.id AND version.revision<=$revision
                 ORDER BY version.revision DESC LIMIT 1);
        """;

    public async IAsyncEnumerable<StoredSearchViewDirectoryFile> ReadPublicDirectoryFilesAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var operation = StartActivity("directory-files", viewId, revision);
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT mapping.relative_path AS child_relative_path, file.*
            FROM search_view_directory_files mapping
            JOIN search_view_directories mapping_directory ON mapping_directory.id = mapping.directory_id
            JOIN search_view_file_rows file
              ON file.id = mapping.file_id
            WHERE mapping_directory.view_id = $view AND mapping_directory.item_ref = $directory
              AND mapping.admitted_revision <= $revision
              AND {DirectoryVisibleAtRevisionSql("mapping_directory")}
              AND file.visibility = 'Public'
            ORDER BY mapping.relative_path COLLATE BINARY, file.item_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        Add(command, "$revision", revision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredSearchViewFile file = ReadFile(reader);
            yield return new(
                file.Ref,
                Value<string>(reader, "child_relative_path"),
                file);
        }
    }

    public async IAsyncEnumerable<StoredSearchViewDirectoryFileMembership> ReadDirectoryFileMembershipsAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT file.item_ref AS file_ref, mapping.relative_path, file.visibility
            FROM search_view_directory_files mapping
            JOIN search_view_directories mapping_directory ON mapping_directory.id = mapping.directory_id
            JOIN search_view_file_rows file
              ON file.id = mapping.file_id
            WHERE mapping_directory.view_id = $view AND mapping_directory.item_ref = $directory
              AND mapping.admitted_revision <= $revision
              AND {DirectoryVisibleAtRevisionSql("mapping_directory")}
            ORDER BY mapping.relative_path COLLATE BINARY, file.item_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        Add(command, "$revision", revision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return new(
                Value<string>(reader, "file_ref"),
                Value<string>(reader, "relative_path"),
                Enum.Parse<SearchResultVisibility>(Value<string>(reader, "visibility")));
    }

    public async Task<StoredSearchViewSelectionCounts> CountTopLevelSelectionAsync(
        Guid viewId,
        long revision,
        string projectionKind,
        string mode,
        IReadOnlySet<string> refs,
        CancellationToken cancellationToken = default)
    {
        using var operation = StartActivity("selection-counts", viewId, revision);
        EnsureInitialized();
        if (mode is not ("Only" or "AllExcept"))
            throw new ArgumentException("Selection mode must be Only or AllExcept.", nameof(mode));
        ArgumentNullException.ThrowIfNull(refs);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = projectionKind switch
        {
            "Files" => $"""
                WITH requested(ref_key) AS ({RequestedFileKeysSql}),
                chosen AS (
                    SELECT COALESCE(SUM(CASE WHEN fact.visibility = 'Public' THEN 1 ELSE 0 END), 0) AS public_count,
                           COALESCE(SUM(CASE WHEN fact.visibility = 'Locked' THEN 1 ELSE 0 END), 0) AS locked_count,
                           COUNT(*) AS selected_item_count
                    -- Drive exact identity lookups from the small selection. An IN
                    -- filter lets SQLite scan the whole admission range instead.
                    FROM requested
                    CROSS JOIN search_views owner ON owner.id = $view
                    CROSS JOIN search_view_files member
                      ON member.view_id = owner.storage_id AND member.ref_key = requested.ref_key
                    CROSS JOIN search_observations fact ON fact.id = member.observation_id
                    WHERE member.admitted_revision <= $revision)
                SELECT CASE WHEN $mode = 'Only' THEN chosen.public_count
                            ELSE bound.projected_public_file_count - chosen.public_count END AS public_count,
                       CASE WHEN $mode = 'Only' THEN chosen.locked_count
                            ELSE bound.projected_locked_file_count - chosen.locked_count END AS locked_count,
                       CASE WHEN $mode = 'Only' THEN chosen.selected_item_count
                            ELSE bound.projected_file_count - chosen.selected_item_count END AS selected_item_count
                FROM search_view_revisions bound CROSS JOIN chosen
                WHERE bound.view_id = $view AND bound.revision = $revision;
                """,
            "GenericDirectories" or "AlbumDirectories" => DirectorySelectionCountsSql(aggregate: false),
            "AggregateTracks" => """
                SELECT COALESCE(SUM(CASE WHEN file.visibility = 'Public' THEN 1 ELSE 0 END), 0) AS public_count,
                       COALESCE(SUM(CASE WHEN file.visibility = 'Locked' THEN 1 ELSE 0 END), 0) AS locked_count,
                       COUNT(*) AS selected_item_count
                FROM search_view_aggregate_tracks aggregate
                CROSS JOIN search_view_aggregate_track_versions version
                JOIN search_view_file_rows file ON file.id = version.representative_file_id
                WHERE aggregate.view_id = $view AND version.group_id = aggregate.id
                  AND version.revision = (
                    SELECT MAX(latest.revision) FROM search_view_aggregate_track_versions latest
                    WHERE latest.group_id = aggregate.id AND latest.revision <= $revision)
                  AND (($mode = 'Only' AND aggregate.item_ref IN (SELECT value FROM json_each($refs)))
                    OR ($mode = 'AllExcept' AND aggregate.item_ref NOT IN (SELECT value FROM json_each($refs))));
                """,
            "AggregateAlbums" => DirectorySelectionCountsSql(aggregate: true),
            _ => throw new InvalidOperationException("Selection is not implemented for this search-view projection."),
        };
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        // Flat-file revision counters already cover every projected row. AllExcept
        // subtracts only real exclusions instead of joining every source fact again.
        Add(command, "$mode", mode);
        Add(command, "$refs", JsonSerializer.Serialize(refs));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new(0, 0);
        return new(Value<long>(reader, "public_count"), Value<long>(reader, "locked_count"),
            Value<long>(reader, "selected_item_count"));
    }

    private static string DirectorySelectionCountsSql(bool aggregate)
    {
        // Filter catalog identities before seeking their latest versions. A
        // normal one-folder selection must not build every folder's history.
        string chosen = aggregate ? """
            SELECT directory_version.directory_id,directory_version.public_matching_count,
                   directory_version.locked_matching_count,directory_version.retrieved_file_count
            FROM search_view_album_records album
            CROSS JOIN search_view_album_version_records group_version
              ON group_version.group_id=album.id
             AND group_version.revision=(
                 SELECT MAX(latest.revision) FROM search_view_album_version_records latest
                 WHERE latest.group_id=album.id AND latest.revision<=$revision)
            CROSS JOIN search_view_directory_version_records directory_version
              ON directory_version.directory_id=group_version.representative_directory_id
             AND directory_version.revision=(
                 SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
                 WHERE latest.directory_id=group_version.representative_directory_id AND latest.revision<=$revision)
            WHERE album.view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view)
              AND group_version.is_removed=0 AND directory_version.is_removed=0
              AND (($mode='Only' AND album.item_ref IN (SELECT value FROM json_each($refs)))
                OR ($mode='AllExcept' AND album.item_ref NOT IN (SELECT value FROM json_each($refs))))
            """ : """
            SELECT version.directory_id,version.public_matching_count,
                   version.locked_matching_count,version.retrieved_file_count
            FROM search_view_directory_records directory
            CROSS JOIN search_view_directory_version_records version
              ON version.directory_id=directory.id
             AND version.revision=(
                 SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
                 WHERE latest.directory_id=directory.id AND latest.revision<=$revision)
            WHERE directory.view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view)
              AND version.is_removed=0
              AND (($mode='Only' AND directory.item_ref IN (SELECT value FROM json_each($refs)))
                OR ($mode='AllExcept' AND directory.item_ref NOT IN (SELECT value FROM json_each($refs))))
            """;
        return $"""
            WITH chosen AS ({chosen})
            SELECT COALESCE(SUM(CASE WHEN chosen.retrieved_file_count IS NULL THEN chosen.public_matching_count
                     ELSE (SELECT COUNT(*) FROM search_view_directory_files mapping
                           JOIN search_view_files member ON member.id=mapping.file_id
                           JOIN search_observations fact ON fact.id=member.observation_id
                           WHERE mapping.directory_id=chosen.directory_id AND mapping.admitted_revision<=$revision
                             AND fact.visibility='Public') END),0) AS public_count,
                   COALESCE(SUM(CASE WHEN chosen.retrieved_file_count IS NULL THEN chosen.locked_matching_count
                     ELSE (SELECT COUNT(*) FROM search_view_directory_files mapping
                           JOIN search_view_files member ON member.id=mapping.file_id
                           JOIN search_observations fact ON fact.id=member.observation_id
                           WHERE mapping.directory_id=chosen.directory_id AND mapping.admitted_revision<=$revision
                             AND fact.visibility='Locked') END),0) AS locked_count,
                   COUNT(*) AS selected_item_count FROM chosen;
            """;
    }

    public async Task<IReadOnlyList<StoredSearchViewFileRefInfo>> ReadDirectoryFileRefsAsync(
        Guid viewId,
        long revision,
        IReadOnlyCollection<string> refs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refs);
        if (refs.Count == 0)
            return [];
        EnsureInitialized();
        // Seek selected files before walking their reverse memberships.
        using var operation = StartActivity("file-membership", viewId, revision);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT file.item_ref AS file_ref, mapping_directory.item_ref AS directory_ref, file.visibility
            FROM {RequestedFilesSql}
            CROSS JOIN search_view_directory_files mapping
            CROSS JOIN search_view_directories mapping_directory
            WHERE mapping.file_id = file.id AND mapping_directory.id = mapping.directory_id
              AND mapping_directory.view_id = $view
              AND mapping.admitted_revision <= $revision
              AND {DirectoryVisibleAtRevisionSql("mapping_directory")}
            ORDER BY file.item_ref COLLATE BINARY, mapping_directory.item_ref COLLATE BINARY;
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$refs", JsonSerializer.Serialize(refs.Distinct(StringComparer.Ordinal)));
        var values = new List<StoredSearchViewFileRefInfo>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(
                Value<string>(reader, "file_ref"),
                Value<string>(reader, "directory_ref"),
                Enum.Parse<SearchResultVisibility>(Value<string>(reader, "visibility"))));
        return values;
    }

    public async Task<StoredSearchViewSelectionCounts> CountObservedDirectoryFilesAsync(
        Guid viewId,
        long revision,
        string directoryRef,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(SUM(CASE WHEN file.visibility = 'Public' THEN 1 ELSE 0 END), 0) AS public_count,
                   COALESCE(SUM(CASE WHEN file.visibility = 'Locked' THEN 1 ELSE 0 END), 0) AS locked_count
            FROM search_view_directory_files mapping
            JOIN search_view_directories mapping_directory ON mapping_directory.id = mapping.directory_id
            JOIN search_view_file_rows file
              ON file.id = mapping.file_id
            WHERE mapping_directory.view_id = $view AND mapping_directory.item_ref = $directory
              AND mapping.admitted_revision <= $revision
              AND {DirectoryVisibleAtRevisionSql("mapping_directory")};
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$directory", directoryRef);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new(0, 0);
        return new(Value<long>(reader, "public_count"), Value<long>(reader, "locked_count"));
    }

    public async IAsyncEnumerable<StoredSearchViewCommitItem> ReadCommitItemsAsync(
        Guid viewId,
        long viewRevision,
        string projectionKind,
        string mode,
        IReadOnlySet<string> refs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (mode is not ("Only" or "AllExcept"))
            throw new ArgumentException("Selection mode must be Only or AllExcept.", nameof(mode));
        ArgumentNullException.ThrowIfNull(refs);
        using var operation = StartActivity("selection-items", viewId, viewRevision);
        string refsJson = mode == "Only" ? JsonSerializer.Serialize(refs) : "[]";
        // Explicit choices should read only their rows, even in a large View.
        // AllExcept streams the top-level result set and filters exclusions below.
        string OnlyRefsFilter(string column) => mode == "Only"
            ? $"AND {column} IN (SELECT value FROM json_each($refs))" : "";
        await using var connection = await OpenReadConnectionAsync(viewId, viewRevision, cancellationToken).ConfigureAwait(false);
        if (await ReadActiveRevisionAsync(connection, viewId, viewRevision, cancellationToken).ConfigureAwait(false) == null)
            yield break;
        await using var command = connection.CreateCommand();
        if (projectionKind == "Files")
        {
            command.CommandText = $$"""
                SELECT file.* FROM {{(mode == "Only" ? RequestedFilesSql : "search_view_file_rows file")}}
                WHERE file.view_id = $view
                  AND file.admitted_revision <= $revision
                ORDER BY file.item_ref COLLATE BINARY;
                """;
        }
        else if (projectionKind is "GenericDirectories" or "AlbumDirectories")
        {
            command.CommandText = $$"""
                SELECT directory.item_ref AS directory_ref,
                       directory.username AS directory_username,
                       directory.folder_path AS directory_folder_path,
                       version.public_matching_count,
                       version.locked_matching_count,
                       version.public_matching_bytes,
                       version.locked_matching_bytes,
                       version.is_fully_retrieved,
                       version.retrieved_file_count,
                       version.retrieved_bytes,
                       file.*
                FROM search_view_directory_records directory
                CROSS JOIN search_view_directory_version_records version
                  ON version.directory_id=directory.id AND version.revision=(
                      SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
                      WHERE latest.directory_id=directory.id AND latest.revision<=$revision)
                CROSS JOIN search_view_file_rows file ON file.id=version.best_file_id
                WHERE directory.view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view)
                  AND version.is_removed=0
                  {{OnlyRefsFilter("directory.item_ref")}}
                ORDER BY directory.item_ref COLLATE BINARY;
                """;
        }
        else if (projectionKind == "AggregateTracks")
        {
            command.CommandText = $$"""
                SELECT aggregate.item_ref AS aggregate_ref,
                       aggregate.group_index AS aggregate_index,
                       aggregate.query_json AS aggregate_query_json,
                       version.share_count AS aggregate_share_count,
                       version.selectable_option_count AS aggregate_option_count,
                       file.*
                FROM search_view_aggregate_tracks aggregate
                CROSS JOIN search_view_aggregate_track_versions version
                JOIN search_view_file_rows file ON file.id = version.representative_file_id
                WHERE aggregate.view_id = $view AND version.group_id = aggregate.id
                  AND version.revision = (
                    SELECT MAX(latest.revision) FROM search_view_aggregate_track_versions latest
                    WHERE latest.group_id = aggregate.id AND latest.revision <= $revision)
                  {{OnlyRefsFilter("aggregate.item_ref")}}
                ORDER BY aggregate.item_ref COLLATE BINARY;
                """;
        }
        else if (projectionKind == "AggregateAlbums")
        {
            command.CommandText = $$"""
                SELECT aggregate.item_ref AS aggregate_ref,
                       group_version.group_index AS aggregate_index,
                       aggregate.query_json AS aggregate_query_json,
                       group_version.share_count AS aggregate_share_count,
                       group_version.selectable_option_count AS aggregate_option_count,
                       directory.item_ref AS directory_ref,
                       directory.username AS directory_username,
                       directory.folder_path AS directory_folder_path,
                       directory_version.public_matching_count,
                       directory_version.locked_matching_count,
                       directory_version.public_matching_bytes,
                       directory_version.locked_matching_bytes,
                       directory_version.is_fully_retrieved,
                       directory_version.retrieved_file_count,
                       directory_version.retrieved_bytes,
                       file.*
                FROM search_view_album_records aggregate
                CROSS JOIN search_view_album_version_records group_version
                  ON group_version.group_id=aggregate.id AND group_version.revision=(
                      SELECT MAX(latest.revision) FROM search_view_album_version_records latest
                      WHERE latest.group_id=aggregate.id AND latest.revision<=$revision)
                CROSS JOIN search_view_directory_version_records directory_version
                  ON directory_version.directory_id=group_version.representative_directory_id AND directory_version.revision=(
                      SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
                      WHERE latest.directory_id=group_version.representative_directory_id AND latest.revision<=$revision)
                CROSS JOIN search_view_directory_records directory ON directory.id=directory_version.directory_id
                CROSS JOIN search_view_file_rows file ON file.id=directory_version.best_file_id
                WHERE aggregate.view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view)
                  AND group_version.is_removed=0 AND directory_version.is_removed=0
                  {{OnlyRefsFilter("aggregate.item_ref")}}
                ORDER BY aggregate.item_ref COLLATE BINARY;
                """;
        }
        else
        {
            throw new InvalidOperationException(
                "Selection is not implemented for this search-view projection.");
        }
        Add(command, "$view", viewId);
        Add(command, "$revision", viewRevision);
        if (mode == "Only")
            Add(command, "$refs", refsJson);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredSearchViewCommitItem item;
            if (projectionKind == "Files")
            {
                StoredSearchViewFile file = ReadFile(reader);
                item = new("File", file.Ref, file, null);
            }
            else if (projectionKind is "GenericDirectories" or "AlbumDirectories")
            {
                StoredSearchViewDirectory directory = ReadDirectory(reader);
                item = new("Directory", directory.Ref, null, directory);
            }
            else if (projectionKind == "AggregateTracks")
            {
                StoredSearchViewAggregateTrackGroup aggregate = ReadAggregateTrack(reader);
                item = new(
                    "AggregateTrack",
                    aggregate.Ref,
                    null,
                    null,
                    aggregate);
            }
            else
            {
                StoredSearchViewAggregateAlbumGroup aggregate = ReadAggregateAlbum(reader);
                item = new(
                    "AggregateAlbum",
                    aggregate.Ref,
                    null,
                    null,
                    null,
                    aggregate);
            }
            bool include = mode == "Only" ? refs.Contains(item.Ref) : !refs.Contains(item.Ref);
            if (include)
                yield return item;
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        if (mode == "Only"
            && projectionKind is "GenericDirectories" or "AlbumDirectories")
        {
            await using var children = connection.CreateCommand();
            children.CommandText = $"""
                SELECT mapping_directory.item_ref AS parent_directory_ref, file.*
                FROM {RequestedFilesSql}
                CROSS JOIN search_view_directory_files mapping
                CROSS JOIN search_view_directories mapping_directory
                WHERE mapping.file_id = file.id AND mapping_directory.id = mapping.directory_id
                  AND mapping_directory.view_id = $view
                  AND mapping.admitted_revision <= $revision
                  AND {DirectoryVisibleAtRevisionSql("mapping_directory")}
                ORDER BY file.item_ref COLLATE BINARY,
                         mapping_directory.item_ref COLLATE BINARY;
                """;
            Add(children, "$view", viewId);
            Add(children, "$revision", viewRevision);
            Add(children, "$refs", refsJson);
            await using SqliteDataReader childReader = await children.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            string? lastRef = null;
            while (await childReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                StoredSearchViewFile file = ReadFile(childReader);
                if (StringComparer.Ordinal.Equals(lastRef, file.Ref))
                    continue;
                lastRef = file.Ref;
                if (!refs.Contains(file.Ref))
                    continue;
                yield return new StoredSearchViewCommitItem(
                    "DirectoryFile",
                    file.Ref,
                    file,
                    null,
                    ParentRef: Value<string>(childReader, "parent_directory_ref"));
            }
        }

        if (mode == "Only" && projectionKind == "AggregateTracks")
        {
            await using var options = connection.CreateCommand();
            options.CommandText = $"""
                SELECT aggregate.item_ref AS parent_group_ref, file.*
                FROM {RequestedFilesSql}
                CROSS JOIN search_view_aggregate_track_files mapping
                JOIN search_view_aggregate_tracks aggregate ON aggregate.id = mapping.group_id
                WHERE mapping.file_id = file.id
                  AND aggregate.view_id = $view
                  AND mapping.admitted_revision <= $revision
                  AND EXISTS (
                    SELECT 1 FROM search_view_aggregate_track_versions version
                    WHERE version.group_id = aggregate.id
                      AND version.revision <= $revision)
                ORDER BY file.item_ref COLLATE BINARY,
                         aggregate.item_ref COLLATE BINARY;
                """;
            Add(options, "$view", viewId);
            Add(options, "$revision", viewRevision);
            Add(options, "$refs", refsJson);
            await using SqliteDataReader optionReader = await options.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            StoredSearchViewFile? selectedFile = null;
            var parentGroups = new List<string>();
            while (await optionReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                StoredSearchViewFile file = ReadFile(optionReader);
                if (selectedFile != null
                    && !StringComparer.Ordinal.Equals(selectedFile.Ref, file.Ref))
                {
                    if (refs.Contains(selectedFile.Ref))
                    {
                        yield return new StoredSearchViewCommitItem(
                            "AggregateTrackFile",
                            selectedFile.Ref,
                            selectedFile,
                            null,
                            ContainerRefs: parentGroups.ToArray());
                    }
                    parentGroups.Clear();
                }
                selectedFile = file;
                parentGroups.Add(Value<string>(optionReader, "parent_group_ref"));
            }
            if (selectedFile != null && refs.Contains(selectedFile.Ref))
            {
                yield return new StoredSearchViewCommitItem(
                    "AggregateTrackFile",
                    selectedFile.Ref,
                    selectedFile,
                    null,
                    ContainerRefs: parentGroups.ToArray());
            }
        }

        if (mode == "Only" && projectionKind == "AggregateAlbums")
        {
            await using (var options = connection.CreateCommand())
            {
                options.CommandText = """
                    SELECT membership.group_ref AS parent_group_ref,
                           directory.item_ref AS directory_ref,
                           directory.username AS directory_username,
                           directory.folder_path AS directory_folder_path,
                           directory_version.public_matching_count,
                           directory_version.locked_matching_count,
                           directory_version.public_matching_bytes,
                           directory_version.locked_matching_bytes,
                           directory_version.is_fully_retrieved,
                           directory_version.retrieved_file_count,
                           directory_version.retrieved_bytes,
                           file.*
                    FROM search_view_directories directory
                    JOIN search_view_directory_versions directory_version
                      ON directory_version.view_id = directory.view_id
                     AND directory_version.item_ref = directory.item_ref
                     AND directory_version.revision = (
                        SELECT MAX(latest.revision) FROM search_view_directory_versions latest
                        WHERE latest.view_id = $view AND latest.item_ref = directory.item_ref
                          AND latest.revision <= $revision)
                    JOIN search_view_aggregate_album_directory_versions membership
                      ON membership.view_id = directory.view_id
                     AND membership.directory_ref = directory.item_ref
                     AND membership.revision = (
                        SELECT MAX(latest.revision) FROM search_view_aggregate_album_directory_versions latest
                        WHERE latest.view_id = $view AND latest.group_ref = membership.group_ref
                          AND latest.directory_ref = directory.item_ref AND latest.revision <= $revision)
                    JOIN search_view_aggregate_album_versions group_version
                      ON group_version.view_id = $view
                     AND group_version.item_ref = membership.group_ref
                     AND group_version.revision = (
                        SELECT MAX(latest.revision) FROM search_view_aggregate_album_versions latest
                        WHERE latest.view_id = $view AND latest.item_ref = membership.group_ref
                          AND latest.revision <= $revision)
                    JOIN search_view_file_rows file
                      ON file.id = directory_version.best_file_id
                    WHERE directory.view_id = $view
                      AND directory.item_ref IN (SELECT value FROM json_each($refs))
                      AND membership.is_present = 1
                      AND group_version.is_removed = 0
                      AND directory_version.is_removed = 0
                    ORDER BY directory.item_ref COLLATE BINARY,
                             membership.group_ref COLLATE BINARY;
                    """;
                Add(options, "$view", viewId);
                Add(options, "$revision", viewRevision);
                Add(options, "$refs", refsJson);
                await using SqliteDataReader optionReader = await options.ExecuteReaderAsync(
                    cancellationToken).ConfigureAwait(false);
                StoredSearchViewDirectory? selectedDirectory = null;
                var parentGroups = new List<string>();
                while (await optionReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    StoredSearchViewDirectory directory = ReadDirectory(optionReader);
                    if (selectedDirectory != null
                        && !StringComparer.Ordinal.Equals(selectedDirectory.Ref, directory.Ref))
                    {
                        if (refs.Contains(selectedDirectory.Ref))
                        {
                            yield return new StoredSearchViewCommitItem(
                                "AggregateAlbumDirectory",
                                selectedDirectory.Ref,
                                null,
                                selectedDirectory,
                                ContainerRefs: parentGroups.ToArray());
                        }
                        parentGroups.Clear();
                    }
                    selectedDirectory = directory;
                    parentGroups.Add(Value<string>(optionReader, "parent_group_ref"));
                }
                if (selectedDirectory != null && refs.Contains(selectedDirectory.Ref))
                {
                    yield return new StoredSearchViewCommitItem(
                        "AggregateAlbumDirectory",
                        selectedDirectory.Ref,
                        null,
                        selectedDirectory,
                        ContainerRefs: parentGroups.ToArray());
                }
            }

            await using var children = connection.CreateCommand();
            children.CommandText = $"""
                SELECT membership.group_ref AS parent_group_ref,
                       mapping_directory.item_ref AS parent_directory_ref,
                       file.*
                FROM {RequestedFilesSql}
                CROSS JOIN search_view_directory_files mapping
                CROSS JOIN search_view_directories mapping_directory
                JOIN search_view_aggregate_album_directory_versions membership
                  ON membership.view_id = mapping_directory.view_id
                 AND membership.directory_ref = mapping_directory.item_ref
                 AND membership.revision = (
                    SELECT MAX(latest.revision) FROM search_view_aggregate_album_directory_versions latest
                    WHERE latest.view_id = $view AND latest.group_ref = membership.group_ref
                      AND latest.directory_ref = mapping_directory.item_ref AND latest.revision <= $revision)
                JOIN search_view_aggregate_album_versions group_version
                  ON group_version.view_id = $view
                 AND group_version.item_ref = membership.group_ref
                 AND group_version.revision = (
                    SELECT MAX(latest.revision) FROM search_view_aggregate_album_versions latest
                    WHERE latest.view_id = $view AND latest.item_ref = membership.group_ref
                      AND latest.revision <= $revision)
                JOIN search_view_directory_versions directory_version
                  ON directory_version.view_id = $view
                 AND directory_version.item_ref = mapping_directory.item_ref
                 AND directory_version.revision = (
                    SELECT MAX(latest.revision) FROM search_view_directory_versions latest
                    WHERE latest.view_id = $view AND latest.item_ref = mapping_directory.item_ref
                      AND latest.revision <= $revision)
                WHERE mapping.file_id = file.id AND mapping_directory.id = mapping.directory_id
                  AND mapping_directory.view_id = $view
                  AND mapping.admitted_revision <= $revision
                  AND membership.is_present = 1
                  AND group_version.is_removed = 0
                  AND directory_version.is_removed = 0
                ORDER BY file.item_ref COLLATE BINARY,
                         mapping_directory.item_ref COLLATE BINARY,
                         membership.group_ref COLLATE BINARY;
                """;
            Add(children, "$view", viewId);
            Add(children, "$revision", viewRevision);
            Add(children, "$refs", refsJson);
            await using SqliteDataReader childReader = await children.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            StoredSearchViewFile? selectedChild = null;
            var containerRefs = new HashSet<string>(StringComparer.Ordinal);
            while (await childReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                StoredSearchViewFile file = ReadFile(childReader);
                if (selectedChild != null
                    && !StringComparer.Ordinal.Equals(selectedChild.Ref, file.Ref))
                {
                    if (refs.Contains(selectedChild.Ref))
                    {
                        yield return new StoredSearchViewCommitItem(
                            "AggregateAlbumFile",
                            selectedChild.Ref,
                            selectedChild,
                            null,
                            ContainerRefs: containerRefs.ToArray());
                    }
                    containerRefs.Clear();
                }
                selectedChild = file;
                containerRefs.Add(Value<string>(childReader, "parent_group_ref"));
                containerRefs.Add(Value<string>(childReader, "parent_directory_ref"));
            }
            if (selectedChild != null && refs.Contains(selectedChild.Ref))
            {
                yield return new StoredSearchViewCommitItem(
                    "AggregateAlbumFile",
                    selectedChild.Ref,
                    selectedChild,
                    null,
                    ContainerRefs: containerRefs.ToArray());
            }
        }
    }

    private async Task<StoredSearchView?> ReadOneAsync(
        Guid viewId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOneAsync(connection, null, viewId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredSearchView?> ReadOneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid viewId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (transaction != null)
            command.Transaction = transaction;
        command.CommandText = "SELECT * FROM search_views WHERE id = $id;";
        Add(command, "$id", viewId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadView(reader)
            : null;
    }

    private async Task<(StoredSearchView View, StoredSearchViewRevision Revision)?> ReadActiveRevisionAsync(
        SqliteConnection connection,
        Guid viewId,
        long revision,
        CancellationToken cancellationToken)
    {
        using var operation = StartActivity("metadata", viewId, revision);
        StoredSearchView? view = await ReadOneAsync(connection, null, viewId, cancellationToken)
            .ConfigureAwait(false);
        if (view == null || view.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        return bound == null ? null : (view, bound);
    }

    private static async Task<StoredSearchViewRevision?> ReadRevisionAsync(
        SqliteConnection connection,
        Guid viewId,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM search_view_revisions
            WHERE view_id = $view AND revision = $revision;
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRevision(reader)
            : null;
    }

    private static async Task<long> InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        long revision,
        SearchViewKernelUpdate update,
        string retentionState,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_view_revision_records (
                view_storage_id, view_id, revision, source_revision, consumed_sequence,
                is_complete, retention_state, public_file_count,
                locked_file_count, public_bytes, locked_bytes,
                observed_peer_count, projected_file_count,
                projected_public_file_count, projected_locked_file_count,
                preferred_file_count, other_file_count,
                top_level_item_count, selectable_option_count)
            VALUES (
                (SELECT storage_id FROM search_views WHERE id=$view),
                $view, $revision, $source_revision, $sequence,
                $complete, $retention, $public_count, $locked_count,
                $public_bytes, $locked_bytes, $peers, $projected,
                $projected_public, $projected_locked, $preferred, $other,
                $top_level, $selectable) RETURNING storage_id;
            """;
        BindRevision(command, viewId, revision, update, retentionState);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task UpdateSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        long revision,
        SearchViewKernelUpdate update,
        string retentionState,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE search_views SET
                revision = $revision, source_revision = $source_revision,
                consumed_sequence = $sequence, is_complete = $complete,
                retention_state = $retention,
                public_file_count = $public_count,
                locked_file_count = $locked_count,
                public_bytes = $public_bytes, locked_bytes = $locked_bytes,
                observed_peer_count = $peers,
                projected_file_count = $projected,
                projected_public_file_count = $projected_public,
                projected_locked_file_count = $projected_locked,
                preferred_file_count = $preferred,
                other_file_count = $other,
                top_level_item_count = $top_level,
                selectable_option_count = $selectable
            WHERE id = $view;
            """;
        BindRevision(command, viewId, revision, update, retentionState);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindRevision(
        SqliteCommand command,
        Guid viewId,
        long revision,
        SearchViewKernelUpdate update,
        string retentionState)
    {
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$source_revision", update.SourceRevision);
        Add(command, "$sequence", update.ConsumedSequence);
        Add(command, "$complete", update.IsComplete);
        Add(command, "$retention", retentionState);
        AddCounters(command, update.Counters);
    }

    private static void AddCounters(SqliteCommand command, SearchViewCounters counters)
    {
        Add(command, "$public_count", counters.PublicFileCount);
        Add(command, "$locked_count", counters.LockedFileCount);
        Add(command, "$public_bytes", counters.PublicBytes);
        Add(command, "$locked_bytes", counters.LockedBytes);
        Add(command, "$peers", counters.ObservedPeerCount);
        Add(command, "$projected", counters.ProjectedFileCount);
        Add(command, "$projected_public", counters.ProjectedPublicFileCount);
        Add(command, "$projected_locked", counters.ProjectedLockedFileCount);
        Add(command, "$preferred", counters.PreferredFileCount);
        Add(command, "$other", counters.OtherFileCount);
        Add(command, "$top_level", counters.TopLevelItemCount);
        Add(command, "$selectable", counters.SelectableOptionCount);
    }

    private static long? InsertFile(
        SqliteWriteStatement writer,
        long storageViewId,
        long sourceId,
        string itemRef,
        long revision,
        ProjectedFileCandidate file,
        long observationId,
        CancellationToken cancellationToken)
    {
        SearchProjectionInput input = file.Input;
        // Match FileInsertSql parameter order. Source facts remain in the
        // observation; these are covering membership and ordering keys.
        writer.Bind(0, storageViewId);
        writer.Bind(1, Convert.FromHexString(itemRef));
        writer.Bind(2, revision);
        writer.Bind(3, input.Sequence);
        writer.Bind(4, input.Revision);
        writer.Bind(5, observationId);
        writer.Bind(6, input.Size);
        writer.Bind(7, input.UploadSpeed);
        writer.Bind(8, input.QueueLength);
        writer.Bind(9, file.ConditionFacts.PreferenceTier.ToString());
        writer.Bind(10, file.ConditionFacts.NecessaryConditionsSatisfied ? 1 : 0);
        writer.Bind(11, EncodeConditions(file.ConditionFacts.SatisfiedPreferredConditions));
        writer.Bind(12, EncodeConditions(file.ConditionFacts.ConfiguredPreferredConditions));
        writer.Bind(13, (long)file.SortKey.HighFlags);
        writer.Bind(14, file.SortKey.UploadSpeedFast);
        writer.Bind(15, (long)file.SortKey.MidFlags);
        writer.Bind(16, file.SortKey.InferredTrackCount);
        writer.Bind(17, file.SortKey.UploadSpeedMedium);
        writer.Bind(18, file.SortKey.BitRate);
        writer.Bind(19, file.SortKey.StableTieBreaker);
        writer.Bind(20, sourceId);
        return writer.Execute(cancellationToken) == 1 ? writer.LastInsertRowId : null;
    }

    private static async Task<StoredSearchView> PinSourceAsync(
        SqliteConnection connection, SqliteTransaction transaction, StoredSearchView view,
        long sourceId, CancellationToken cancellationToken)
    {
        if (view.SourceId == sourceId) return view;
        if (view.SourceId != null)
            throw new InvalidDataException("A Search View cannot change its source facts.");
        // Empty Views need no retention pin. The first file publication attaches
        // it in the same transaction as the file's composite foreign keys.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE search_views SET source_id=$source WHERE storage_id=$view AND source_id IS NULL;";
        Add(command, "$source", sourceId);
        Add(command, "$view", view.StorageId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The Search View source could not be attached.");
        return view with { SourceId = sourceId };
    }

    private readonly record struct FileIdentity(string Ref, long Id);

    private static async Task ResolveFileIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long storageViewId,
        Guid sourceJobId,
        Dictionary<(string Username, string Filename, SearchResultVisibility Visibility), FileIdentity> files,
        SearchViewKernelUpdate update,
        CancellationToken cancellationToken)
    {
        var directories = update.ChangedDirectories ?? [];
        var tracks = update.ChangedAggregateTrackGroups ?? [];
        if (directories.Count == 0 && tracks.Count == 0) return;
        // IDs stay inside this transaction: pruning can reuse rowids. Fresh
        // inserts already have IDs; resolve only this batch's reused targets.
        var missing = new Dictionary<string,
            (string Username, string Filename, SearchResultVisibility Visibility)>(StringComparer.Ordinal);
        void Require(SearchProjectionInput input)
        {
            var key = (input.Username, input.Filename, input.Visibility);
            if (!files.TryGetValue(key, out FileIdentity identity))
            {
                identity = new(StableRef(sourceJobId, "file", input.Username,
                    input.Filename, input.Visibility.ToString()), 0);
                files.Add(key, identity);
            }
            if (identity.Id == 0) missing.TryAdd(identity.Ref, key);
        }
        foreach (var directory in directories)
        {
            Require(directory.BestChild.Input);
            foreach (var child in directory.NewChildren) Require(child.Input);
        }
        foreach (var track in tracks)
        {
            Require(track.Representative.Input);
            foreach (var option in track.NewOptions) Require(option.Input);
        }
        if (missing.Count == 0) return;
        var resolved = await ReadFileIdsAsync(connection, transaction, storageViewId,
            missing.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
        if (resolved.Count != missing.Count)
            throw new InvalidDataException("A projected reference has no retained file.");
        foreach (var (itemRef, id) in resolved)
            files[missing[itemRef]] = new(itemRef, id);
    }

    private static async Task<Dictionary<string, long>> ReadFileIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long storageViewId,
        IReadOnlyCollection<string> refs,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (refs.Count == 0) return result;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Requested identities stay outermost: each resolves through the compact
        // unique key, rather than scanning a view or issuing per-file SQL calls.
        command.CommandText = """
            SELECT requested.value, file.id FROM json_each($refs) requested
            CROSS JOIN search_view_files file
            WHERE file.view_id = $view AND file.ref_key = unhex(requested.value);
            """;
        Add(command, "$view", storageViewId);
        Add(command, "$refs", JsonSerializer.Serialize(refs));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private static long ResolveFileId(
        SearchProjectionInput input,
        IReadOnlyDictionary<(string Username, string Filename, SearchResultVisibility Visibility), FileIdentity> files)
        => files[(input.Username, input.Filename, input.Visibility)].Id;

    private static SqliteWriteStatement CreateDirectoryInsert(
        SqliteConnection connection, SqliteTransaction transaction, long viewStorageId)
    {
        var writer = new SqliteWriteStatement(connection, transaction, """
            INSERT OR IGNORE INTO search_view_directory_records (
                view_storage_id, item_ref, username, folder_path)
            VALUES ($view, $ref, $username, $path);
            """);
        writer.Bind(0, viewStorageId);
        return writer;
    }

    private readonly record struct DirectoryIdentity(string Ref, long Id, Guid? RetrievalObservation);

    private static async Task<Dictionary<PeerDirectoryIdentity, DirectoryIdentity>> PreparePublicationDirectoriesAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteWriteStatement insert, StoredSearchView current,
        SearchViewKernelUpdate update, CancellationToken cancellationToken)
    {
        var result = new Dictionary<PeerDirectoryIdentity, DirectoryIdentity>();
        var requested = new HashSet<PeerDirectoryIdentity>();
        // New rows already yield their numeric IDs. Resolve only existing targets
        // in the batch read, rather than probing the database twice for every new
        // directory or once per aggregate option. All identities stay in this transaction.
        foreach (var directory in update.ChangedDirectories ?? [])
        {
            var target = directory.Directory;
            if (result.ContainsKey(target)) continue;
            string itemRef = StableRef(current.SourceJobId, "directory", target.Username, target.FolderPath);
            insert.BindValues([itemRef, target.Username, target.FolderPath], offset: 1);
            if (insert.Execute(cancellationToken) == 1)
                result.Add(target, new(itemRef, insert.LastInsertRowId, null));
            else requested.Add(target);
        }
        void RequireExisting(PeerDirectoryIdentity target)
        {
            if (!result.ContainsKey(target)) requested.Add(target);
        }
        foreach (var directory in update.ChangedDirectories ?? [])
            foreach (var absorbed in directory.AbsorbedDirectories ?? []) RequireExisting(absorbed);
        foreach (var directory in update.RemovedDirectories ?? []) RequireExisting(directory);
        foreach (var group in update.ChangedAggregateAlbumGroups ?? [])
        {
            RequireExisting(group.Representative.DirectoryIdentity);
            foreach (var option in group.Options) RequireExisting(option.DirectoryIdentity);
        }
        if (requested.Count == 0) return result;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Keep requested identities outermost for indexed point lookups, including
        // small updates to large Views. This transaction owns the map: pruning or
        // another publication cannot invalidate IDs, retrievals or exact spelling.
        command.CommandText = """
            SELECT directory.username, directory.folder_path, directory.item_ref,
                   directory.id, publication.observation_id
            FROM json_each($requested) requested
            CROSS JOIN search_view_directories directory
              ON directory.view_id = $view
             AND directory.username = json_extract(requested.value, '$[0]')
             AND directory.folder_path = json_extract(requested.value, '$[1]')
            LEFT JOIN search_view_retrieval_publications publication
              ON publication.view_id = directory.view_id
             AND publication.directory_ref = directory.item_ref;
            """;
        Add(command, "$view", current.Id);
        Add(command, "$requested", JsonSerializer.Serialize(
            requested.Select(directory => new[] { directory.Username, directory.FolderPath })));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.GetString(1)),
                new(reader.GetString(2), reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4))));
        return result;
    }

    private static DirectoryIdentity RequireDirectory(
        IReadOnlyDictionary<PeerDirectoryIdentity, DirectoryIdentity> directories, PeerDirectoryIdentity directory)
        => directories.TryGetValue(directory, out var identity) ? identity
            : throw new InvalidDataException("A projected directory has no retained identity.");

    private static string StableRef(Guid sourceJobId, string kind, params ReadOnlySpan<string> parts)
    {
        // Preserve the public identity encoding: UTF-16 length + ':' + value,
        // then UTF-8 and the first 128 SHA-256 bits. Build it without allocating
        // the GUID, StringBuilder, complete key, digest and uppercase hex per file.
        int capacity = checked(35 + EncodedPartLength(kind));
        foreach (string part in parts)
            capacity = checked(capacity + EncodedPartLength(part));
        byte[]? rented = null;
        Span<byte> key = capacity <= 1024 ? stackalloc byte[1024] : (rented = ArrayPool<byte>.Shared.Rent(capacity));
        try
        {
            Span<char> source = stackalloc char[32];
            sourceJobId.TryFormat(source, out _, "N");
            int length = AppendPart(key, source);
            length += AppendPart(key[length..], kind);
            foreach (string part in parts)
                length += AppendPart(key[length..], part);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(key[..length], digest);
            return Convert.ToHexStringLower(digest[..16]);
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }

        static int EncodedPartLength(ReadOnlySpan<char> value)
        {
            // Use actual bytes: a UTF-8 upper bound can overflow for valid long inputs.
            Span<char> prefix = stackalloc char[10];
            value.Length.TryFormat(prefix, out int prefixLength);
            return checked(Encoding.UTF8.GetByteCount(prefix[..prefixLength]) + 1
                + Encoding.UTF8.GetByteCount(value));
        }

        static int AppendPart(Span<byte> target, ReadOnlySpan<char> value)
        {
            Span<char> prefix = stackalloc char[10];
            value.Length.TryFormat(prefix, out int prefixLength);
            int written = Encoding.UTF8.GetBytes(prefix[..prefixLength], target);
            target[written++] = (byte)':';
            return written + Encoding.UTF8.GetBytes(value, target[written..]);
        }
    }

    public static bool MatchesStableFileIdentity(
        Guid sourceJobId, string fileRef, string username, string filename)
        => StringComparer.Ordinal.Equals(fileRef,
               StableRef(sourceJobId, "file", username, filename, nameof(SearchResultVisibility.Public)))
           || StringComparer.Ordinal.Equals(fileRef,
               StableRef(sourceJobId, "file", username, filename, nameof(SearchResultVisibility.Locked)));

    private static async Task<bool> ReadLatestDirectoryRetrievedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string directoryRef,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT is_fully_retrieved
            FROM search_view_directory_versions
            WHERE view_id = $view AND item_ref = $directory
            ORDER BY revision DESC LIMIT 1;
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            is long value && value != 0;
    }

    private static async Task<(long FileCount, long? Bytes)> ReadVisibleDirectoryTotalsAsync(
        SqliteConnection connection, SqliteTransaction transaction, long directoryId,
        long revision, Guid? observationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Ordinary absorption needs only compact file sizes. A retrieval can
        // override them with exact snapshot sizes, including unknown values.
        // Aggregate in SQLite instead of hydrating every child into .NET.
        string files = observationId.HasValue ? "search_view_file_rows" : "search_view_files";
        string size = observationId.HasValue
            ? "CASE WHEN fetched.filename IS NOT NULL THEN fetched.size_bytes WHEN file.size_bytes>=0 THEN file.size_bytes END"
            : "CASE WHEN file.size_bytes>=0 THEN file.size_bytes END";
        string fetched = observationId.HasValue ? """
            LEFT JOIN search_directory_observation_files fetched
              ON fetched.observation_id=$observation AND fetched.filename=file.filename
            """ : "";
        command.CommandText = $"""
            SELECT COUNT(*),CASE WHEN COUNT(byte_count)=COUNT(*) THEN COALESCE(SUM(byte_count),0) END
            FROM (
                SELECT {size} AS byte_count
                FROM search_view_directory_files membership
                JOIN {files} file ON file.id=membership.file_id
                {fetched}
                WHERE membership.directory_id=$directory AND membership.admitted_revision<=$revision);
            """;
        Add(command, "$directory", directoryId);
        Add(command, "$revision", revision);
        if (observationId.HasValue) Add(command, "$observation", observationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    private const string DirectoryRankingColumns = """
        sort_high, sort_upload_fast, sort_mid, sort_inferred,
        sort_upload_medium, sort_bitrate, sort_tie, sequence
        """;

    private static SqliteWriteStatement CreateDirectoryVersionInsert(
        SqliteConnection connection, SqliteTransaction transaction, long viewStorageId, long revision, long revisionId)
    {
        var writer = new SqliteWriteStatement(connection, transaction, $"""
            INSERT INTO search_view_directory_version_records (
                view_storage_id, directory_id, revision, best_file_id,
                public_matching_count, locked_matching_count,
                public_matching_bytes, locked_matching_bytes,
                is_fully_retrieved, retrieved_file_count, retrieved_bytes,
                is_removed, {DirectoryRankingColumns}, revision_id)
            VALUES (
                $view, $ref, $revision, $best,
                $public_count, $locked_count, $public_bytes, $locked_bytes,
                $retrieved, $retrieved_count, $retrieved_bytes, 0,
                $high, $fast, $mid, $inferred, $medium, $bitrate, $tie, $sequence, $owner);
            """);
        writer.Bind("$view", viewStorageId);
        writer.Bind("$revision", revision);
        writer.Bind("$owner", revisionId);
        return writer;
    }

    private static void InsertDirectoryVersion(
        SqliteWriteStatement writer,
        long directoryId,
        long bestFileId,
        SearchViewProjectedDirectory directory,
        CancellationToken cancellationToken)
    {
        writer.Bind(1, directoryId);
        writer.Bind(3, bestFileId);
        writer.Bind(4, directory.PublicMatchingFileCount);
        writer.Bind(5, directory.LockedMatchingFileCount);
        writer.Bind(6, directory.PublicMatchingBytes);
        writer.Bind(7, directory.LockedMatchingBytes);
        writer.Bind(8, directory.IsFullyRetrieved ? 1 : 0);
        writer.Bind(9, directory.RetrievedFileCount);
        writer.Bind(10, directory.RetrievedBytes);
        // Paging reads the directory's recorded rank directly. Reuse the
        // evaluated best-child evidence; no condition evaluation or SQL lookup.
        SearchProjectionSortKey key = directory.BestChild.SortKey;
        writer.Bind(11, (long)key.HighFlags);
        writer.Bind(12, key.UploadSpeedFast);
        writer.Bind(13, (long)key.MidFlags);
        writer.Bind(14, key.InferredTrackCount);
        writer.Bind(15, key.UploadSpeedMedium);
        writer.Bind(16, key.BitRate);
        writer.Bind(17, key.StableTieBreaker);
        writer.Bind(18, directory.BestChild.Input.Sequence);
        writer.Execute(cancellationToken);
    }

    // Parent versions own visibility, including hide/restore gaps. An edge only
    // records when its immutable file joined that directory; it never has an
    // independent removal lifecycle. Callers with a validated parent snapshot
    // already hold this check; direct edge readers use the same predicate here.
    private static string DirectoryVisibleAtRevisionSql(string directoryAlias)
        => $"""
            EXISTS (SELECT 1 FROM search_view_revisions bound
                    WHERE bound.view_id={directoryAlias}.view_id AND bound.revision=$revision)
            AND 0 = (SELECT visibility.is_removed FROM search_view_directory_version_records visibility
                 WHERE visibility.directory_id={directoryAlias}.id AND visibility.revision <= $revision
                 ORDER BY visibility.revision DESC LIMIT 1)
            """;

    private static SqliteWriteStatement CreateDirectoryRemoval(
        SqliteConnection connection, SqliteTransaction transaction,
        long revision, long revisionId)
    {
        var writer = new SqliteWriteStatement(connection, transaction, $"""
            INSERT INTO search_view_directory_version_records (
                view_storage_id, directory_id, revision, revision_id, best_file_id,
                public_matching_count, locked_matching_count,
                public_matching_bytes, locked_matching_bytes,
                is_fully_retrieved, retrieved_file_count, retrieved_bytes,
                is_removed, {DirectoryRankingColumns})
            SELECT view_storage_id, directory_id, $revision, $owner, best_file_id,
                public_matching_count, locked_matching_count,
                public_matching_bytes, locked_matching_bytes,
                is_fully_retrieved, retrieved_file_count, retrieved_bytes, 1,
                {DirectoryRankingColumns}
            FROM search_view_directory_version_records
            WHERE directory_id = $directory
            ORDER BY revision DESC LIMIT 1;
            """);
        writer.Bind("$revision", revision);
        writer.Bind("$owner", revisionId);
        return writer;
    }

    private static SqliteWriteStatement CreateDirectoryChildInsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long revision)
    {
        var writer = new SqliteWriteStatement(connection, transaction, """
            INSERT OR IGNORE INTO search_view_directory_files (
                directory_id, file_id, admitted_revision, relative_path)
            VALUES ($directory, $file, $revision, $relative);
            """);
        writer.Bind("$revision", revision);
        return writer;
    }

    private static void InsertDirectoryChild(
        SqliteWriteStatement writer,
        long directoryId,
        long fileId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        writer.Bind(0, directoryId);
        writer.Bind(1, fileId);
        writer.Bind(3, relativePath);
        writer.Execute(cancellationToken);
    }

    private static async Task CopyDirectoryChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        long destinationId,
        string sourceRef,
        long revision,
        string relativePrefix,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO search_view_directory_files (
                directory_id, file_id, admitted_revision, relative_path)
            SELECT $destination, file_id, $revision, $prefix || relative_path
            FROM search_view_directory_files
            WHERE directory_id = (SELECT id FROM search_view_directories
                                  WHERE view_id = $view AND item_ref = $source)
              AND admitted_revision < $revision;
            """;
        Add(command, "$view", viewId);
        Add(command, "$destination", destinationId);
        Add(command, "$source", sourceRef);
        Add(command, "$revision", revision);
        Add(command, "$prefix", relativePrefix);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PublishAggregateTracksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredSearchView view,
        long revision,
        IReadOnlyList<SearchViewProjectedAggregateTrackGroup> groups,
        IReadOnlyDictionary<(string Username, string Filename, SearchResultVisibility Visibility), FileIdentity> files,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0) return;
        // Resolve this batch's canonical keys once. An existing group neither
        // serializes its unchanged query nor retries a catalog INSERT on every
        // arrival. Public group refs belong only to the catalog; high-volume
        // versions/memberships use its compact key, as directory members do.
        var identities = new Dictionary<int, long>(groups.Count);
        // Revision zero is the fresh View: its catalog is empty by construction.
        if (view.Revision > 0)
        {
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT group_index, id FROM search_view_aggregate_tracks
                WHERE view_id = $view AND group_index IN (SELECT value FROM json_each($indices));
                """;
            Add(read, "$view", view.Id);
            Add(read, "$indices", JsonSerializer.Serialize(groups.Select(group => group.Index)));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                identities.Add(reader.GetInt32(0), reader.GetInt64(1));
        }
        using var insert = new SqliteWriteStatement(connection, transaction, """
            INSERT OR IGNORE INTO search_view_aggregate_tracks (
                view_id, item_ref, group_index, query_json)
            VALUES ($view, $ref, $index, $query);
            """);
        using var version = new SqliteWriteStatement(connection, transaction, """
            INSERT INTO search_view_aggregate_track_versions (
                view_id, group_id, revision, share_count,
                selectable_option_count, representative_file_id)
            VALUES ($view, $group, $revision, $shares, $options, $representative);
            """);
        using var option = new SqliteWriteStatement(connection, transaction, """
            INSERT OR IGNORE INTO search_view_aggregate_track_files (
                group_id, file_id, admitted_revision)
            VALUES ($group, $file, $revision);
            """);
        string viewId = view.Id.ToString("D");
        insert.Bind(0, viewId);
        version.Bind(0, viewId);
        version.Bind(2, revision);
        option.Bind(2, revision);
        foreach (SearchViewProjectedAggregateTrackGroup group in groups)
        {
            if (!identities.TryGetValue(group.Index, out long groupId))
            {
                SongQuery identity = group.StableQuery ?? group.Query;
                string groupRef = StableRef(view.SourceJobId, "aggregate-track",
                    identity.Artist, identity.Title, identity.Album, identity.URI,
                    identity.Length.ToString(CultureInfo.InvariantCulture), identity.ArtistMaybeWrong.ToString());
                insert.Bind(1, groupRef);
                insert.Bind(2, group.Index);
                insert.Bind(3, JsonSerializer.Serialize(group.Query));
                if (insert.Execute(cancellationToken) != 1)
                    throw new InvalidDataException("An aggregate-track group reference is missing.");
                identities.Add(group.Index, groupId = insert.LastInsertRowId);
            }
            version.Bind(1, groupId);
            version.Bind(3, group.ShareCount);
            version.Bind(4, group.SelectableOptionCount);
            version.Bind(5, ResolveFileId(group.Representative.Input, files));
            version.Execute(cancellationToken);
            option.Bind(0, groupId);
            foreach (ProjectedFileCandidate file in group.NewOptions)
            {
                option.Bind(1, ResolveFileId(file.Input, files));
                option.Execute(cancellationToken);
            }
        }
    }

    private static async Task PublishAggregateAlbumsAsync(
        SqliteConnection connection, SqliteTransaction transaction, StoredSearchView view,
        long revision, long revisionId, SearchViewKernelUpdate update,
        IReadOnlyDictionary<PeerDirectoryIdentity, DirectoryIdentity> directories,
        CancellationToken cancellationToken)
    {
        var changed = update.ChangedAggregateAlbumGroups ?? [];
        var removed = update.RemovedAggregateAlbumGroups ?? [];
        var identities = new Dictionary<PeerDirectoryIdentity, long>();
        var previous = new Dictionary<long, HashSet<long>>();
        if (view.Revision > 0)
        {
            var requested = changed.Select(group => group.StableIdentity).Concat(removed).Distinct();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT album.stable_username, album.stable_folder_path, album.id
                    FROM json_each($requested) requested
                    CROSS JOIN search_view_album_records album
                      ON album.view_storage_id=$view
                     AND album.stable_username=json_extract(requested.value,'$[0]')
                     AND album.stable_folder_path=json_extract(requested.value,'$[1]');
                    """;
                Add(read, "$view", view.StorageId);
                Add(read, "$requested", JsonSerializer.Serialize(
                    requested.Select(identity => new[] { identity.Username, identity.FolderPath })));
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    identities.Add(new(reader.GetString(0), reader.GetString(1)), reader.GetInt64(2));
            }
            if (identities.Count > 0)
            {
                await using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = """
                    WITH latest AS (
                        SELECT version.group_id,version.directory_id,MAX(version.revision) AS revision
                        FROM json_each($groups) requested
                        CROSS JOIN search_view_album_directory_records version ON version.group_id=requested.value
                        WHERE version.revision<$revision
                        GROUP BY version.group_id,version.directory_id)
                    SELECT version.group_id,version.directory_id
                    FROM latest
                    CROSS JOIN search_view_album_directory_records version
                      ON version.group_id=latest.group_id AND version.directory_id=latest.directory_id
                     AND version.revision=latest.revision
                    WHERE version.is_present=1;
                    """;
                Add(read, "$groups", JsonSerializer.Serialize(identities.Values));
                Add(read, "$revision", revision);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    long groupId = reader.GetInt64(0);
                    if (!previous.TryGetValue(groupId, out var options)) previous.Add(groupId, options = []);
                    options.Add(reader.GetInt64(1));
                }
            }
        }

        // Catalogs own exact public identities/immutable queries; version and
        // option batches bind compact keys without repeating those strings.
        using var insert = new SqliteWriteStatement(connection, transaction, """
            INSERT INTO search_view_album_records(view_storage_id,item_ref,stable_username,stable_folder_path,query_json)
            VALUES ($view,$ref,$username,$path,$query);
            """);
        using var version = new SqliteWriteStatement(connection, transaction, """
            INSERT INTO search_view_album_version_records(
                view_storage_id,group_id,revision,revision_id,group_index,share_count,
                selectable_option_count,representative_directory_id,is_removed)
            VALUES ($view,$group,$revision,$owner,$index,$shares,$options,$representative,0);
            """);
        using var removal = new SqliteWriteStatement(connection, transaction, """
            INSERT INTO search_view_album_version_records(
                view_storage_id,group_id,revision,revision_id,group_index,share_count,
                selectable_option_count,representative_directory_id,is_removed)
            SELECT view_storage_id,group_id,$revision,$owner,group_index,share_count,
                selectable_option_count,representative_directory_id,1
            FROM search_view_album_version_records WHERE group_id=$group
            ORDER BY revision DESC LIMIT 1;
            """);
        using var option = new SqliteWriteStatement(connection, transaction, """
            INSERT INTO search_view_album_directory_records(
                view_storage_id,group_id,directory_id,revision,revision_id,is_present)
            VALUES ($view,$group,$directory,$revision,$owner,$present);
            """);
        insert.Bind("$view", view.StorageId);
        version.Bind("$view", view.StorageId);
        version.Bind("$revision", revision);
        version.Bind("$owner", revisionId);
        removal.Bind("$revision", revision);
        removal.Bind("$owner", revisionId);
        option.Bind("$view", view.StorageId);
        option.Bind("$revision", revision);
        option.Bind("$owner", revisionId);
        foreach (SearchViewProjectedAggregateAlbumGroup group in changed)
        {
            if (!identities.TryGetValue(group.StableIdentity, out long groupId))
            {
                insert.Bind("$ref", StableRef(view.SourceJobId, "aggregate-album",
                    group.StableIdentity.Username, group.StableIdentity.FolderPath));
                insert.Bind("$username", group.StableIdentity.Username);
                insert.Bind("$path", group.StableIdentity.FolderPath);
                insert.Bind("$query", JsonSerializer.Serialize(group.Query));
                insert.Execute(cancellationToken);
                identities.Add(group.StableIdentity, groupId = insert.LastInsertRowId);
            }
            version.Bind("$group", groupId);
            version.Bind("$index", group.Index);
            version.Bind("$shares", group.ShareCount);
            version.Bind("$options", group.SelectableOptionCount);
            version.Bind("$representative", RequireDirectory(directories, group.Representative.DirectoryIdentity).Id);
            version.Execute(cancellationToken);
            ReplaceOptions(groupId, group.Options.Select(folder => RequireDirectory(directories, folder.DirectoryIdentity).Id).ToHashSet());
        }
        foreach (PeerDirectoryIdentity identity in removed)
        {
            if (!identities.TryGetValue(identity, out long groupId))
                throw new InvalidDataException("A removed aggregate-album group has no retained identity.");
            removal.Bind("$group", groupId);
            if (removal.Execute(cancellationToken) != 1)
                throw new InvalidDataException("A removed aggregate-album group has no prior version.");
            ReplaceOptions(groupId, null);
        }

        void ReplaceOptions(long groupId, HashSet<long>? current)
        {
            option.Bind("$group", groupId);
            previous.TryGetValue(groupId, out var prior);
            if (prior != null)
                foreach (long directoryId in prior)
                {
                    if (current?.Contains(directoryId) == true) continue;
                    option.Bind("$directory", directoryId);
                    option.Bind("$present", 0);
                    option.Execute(cancellationToken);
                }
            if (current != null)
                foreach (long directoryId in current)
                {
                    if (prior?.Contains(directoryId) == true) continue;
                    option.Bind("$directory", directoryId);
                    option.Bind("$present", 1);
                    option.Execute(cancellationToken);
                }
        }
    }

    private static string RelativePath(string folderPath, string filename)
    {
        if (folderPath.Length == 0 && filename.IndexOfAny(['\\', '/']) < 0)
            return filename;
        if (!filename.StartsWith(folderPath, StringComparison.Ordinal)
            || filename.Length <= folderPath.Length
            || filename[folderPath.Length] is not ('\\' or '/'))
            throw new InvalidDataException("A projected directory child is outside its exact directory.");
        return filename[(folderPath.Length + 1)..];
    }

    private static string RelativeDirectoryPrefix(string rootPath, string childPath)
    {
        if (!childPath.StartsWith(rootPath, StringComparison.Ordinal)
            || childPath.Length <= rootPath.Length
            || childPath[rootPath.Length] is not ('\\' or '/'))
            throw new InvalidDataException("A merged projected directory is outside its root.");
        return childPath[(rootPath.Length + 1)..] + childPath[rootPath.Length];
    }

    private static StoredSearchView ReadView(SqliteDataReader reader)
        => new(
            Guid.Parse(Value<string>(reader, "id")),
            Guid.Parse(Value<string>(reader, "source_job_id")),
            Value<string>(reader, "projection_kind"),
            Value<string>(reader, "definition_json"),
            Time(reader, "created_at_utc"),
            Time(reader, "expires_at_utc"),
            Value<long>(reader, "revision"),
            Value<int>(reader, "source_revision"),
            Value<long>(reader, "consumed_sequence"),
            Value<long>(reader, "is_complete") != 0,
            Value<string>(reader, "retention_state"),
            ReadCounters(reader),
            ReadSortOrder(Value<string>(reader, "definition_json")))
        { StorageId = Value<long>(reader, "storage_id"), SourceId = Nullable<long>(reader, "source_id") };

    private static SearchViewSortOrder ReadSortOrder(string definitionJson)
    {
        using JsonDocument document = JsonDocument.Parse(definitionJson);
        if (!document.RootElement.TryGetProperty("projection", out JsonElement projection)
            || projection.ValueKind != JsonValueKind.Object
            || !(projection.TryGetProperty("sortOrder", out JsonElement order)
                || projection.TryGetProperty("SortOrder", out order))
            || order.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return SearchViewSortOrder.Relevance;
        if (order.ValueKind == JsonValueKind.String
            && Enum.TryParse(order.GetString(), out SearchViewSortOrder parsed)
            && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidDataException("The stored Search View sort order is invalid.");
    }

    private static StoredSearchViewRevision ReadRevision(SqliteDataReader reader)
        => new(
            Guid.Parse(Value<string>(reader, "view_id")),
            Value<long>(reader, "revision"),
            Value<int>(reader, "source_revision"),
            Value<long>(reader, "consumed_sequence"),
            Value<long>(reader, "is_complete") != 0,
            Value<string>(reader, "retention_state"),
            ReadCounters(reader));

    private static SearchViewCounters ReadCounters(SqliteDataReader reader)
        => new(
            Value<long>(reader, "public_file_count"),
            Value<long>(reader, "locked_file_count"),
            Value<long>(reader, "public_bytes"),
            Value<long>(reader, "locked_bytes"),
            Value<int>(reader, "observed_peer_count"),
            Value<long>(reader, "projected_file_count"),
            Value<long>(reader, "projected_public_file_count"),
            Value<long>(reader, "projected_locked_file_count"),
            Value<long>(reader, "preferred_file_count"),
            Value<long>(reader, "other_file_count"),
            Value<long>(reader, "top_level_item_count"),
            Value<long>(reader, "selectable_option_count"));

    private static StoredSearchViewFile ReadFile(SqliteDataReader reader)
    {
        var input = new SearchProjectionInput(
            Value<long>(reader, "sequence"),
            Value<int>(reader, "source_row_revision"),
            Value<string>(reader, "username"),
            Value<int>(reader, "response_file_count"),
            Value<string>(reader, "filename"),
            Value<long>(reader, "size_bytes"),
            Nullable<int>(reader, "bit_rate"),
            Nullable<int>(reader, "bit_depth"),
            Nullable<int>(reader, "sample_rate"),
            Nullable<int>(reader, "duration_seconds"),
            Value<string>(reader, "extension"),
            Nullable<int>(reader, "upload_speed"),
            NullableBool(reader, "has_free_upload_slot"),
            SearchObservationWriter.ReadAttributes(NullableString(reader, "attributes_json")),
            Time(reader, "observed_at_utc"),
            Nullable<int>(reader, "queue_length"),
            Enum.Parse<SearchResultVisibility>(Value<string>(reader, "visibility")));
        return new(
            Value<string>(reader, "item_ref"),
            Value<long>(reader, "admitted_revision"),
            input,
            Enum.Parse<SearchPreferenceTier>(Value<string>(reader, "preference_tier")),
            Value<long>(reader, "necessary_conditions_satisfied") != 0,
            DecodeConditions(Value<long>(reader, "matched_condition_flags")),
            DecodeConditions(Value<long>(reader, "configured_condition_flags")));
    }

    private static readonly SearchPreferenceCondition[] PreferenceConditions = Enum.GetValues<SearchPreferenceCondition>();
    private static readonly long KnownConditionFlags = PreferenceConditions.Aggregate(0L,
        (flags, condition) => flags | (1L << (int)condition));

    // Persist already-evaluated sets, without serializing repeated enum names
    // per file. Decode only requested rows, in the same stable public order.
    // This does not evaluate conditions or infer a tier from partial evidence.
    private static long EncodeConditions(IReadOnlyList<SearchPreferenceCondition>? conditions)
    {
        long flags = 0;
        foreach (var condition in conditions ?? [])
        {
            int code = (int)condition;
            if ((uint)code >= 64 || !Enum.IsDefined(condition))
                throw new InvalidDataException("A projected preference condition has no supported identity.");
            flags |= 1L << code;
        }
        return flags;
    }

    private static SearchPreferenceCondition[] DecodeConditions(long flags)
    {
        if ((flags & ~KnownConditionFlags) != 0)
            throw new InvalidDataException("The stored preference conditions contain an unsupported identity.");
        if (flags == 0) return [];
        var conditions = new SearchPreferenceCondition[BitOperations.PopCount(unchecked((ulong)flags))];
        int index = 0;
        foreach (var condition in PreferenceConditions)
            if ((flags & (1L << (int)condition)) != 0) conditions[index++] = condition;
        return conditions;
    }

    private static SearchProjectionSortKey ReadSortKey(SqliteDataReader reader)
        => new(
            checked((uint)Value<long>(reader, "sort_high")),
            Value<int>(reader, "sort_upload_fast"),
            checked((uint)Value<long>(reader, "sort_mid")),
            Value<int>(reader, "sort_inferred"),
            Value<int>(reader, "sort_upload_medium"),
            Value<int>(reader, "sort_bitrate"),
            Value<int>(reader, "sort_tie"));

    private static SearchViewFilePosition Position(
        StoredSearchViewFile row,
        SearchProjectionSortKey key,
        SearchViewSortOrder order = SearchViewSortOrder.Relevance)
        => new(
            key.HighFlags,
            key.UploadSpeedFast,
            key.MidFlags,
            key.InferredTrackCount,
            key.UploadSpeedMedium,
            key.BitRate,
            key.StableTieBreaker,
            row.Input.Sequence,
            row.Ref,
            order switch
            {
                SearchViewSortOrder.UploadSpeed => row.Input.UploadSpeed ?? -1,
                SearchViewSortOrder.QueueDepth => row.Input.QueueLength ?? int.MaxValue,
                SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending
                    => Math.Max(0, row.Input.Size),
                _ => null,
            },
            order == SearchViewSortOrder.QueueDepth
                ? row.Input.UploadSpeed ?? -1 : null);

    private static StoredSearchViewDirectory ReadDirectory(SqliteDataReader reader)
        => new(
            Value<string>(reader, "directory_ref"),
            Value<string>(reader, "directory_username"),
            Value<string>(reader, "directory_folder_path"),
            Value<long>(reader, "public_matching_count"),
            Value<long>(reader, "locked_matching_count"),
            Value<long>(reader, "public_matching_bytes"),
            Value<long>(reader, "locked_matching_bytes"),
            Value<long>(reader, "is_fully_retrieved") != 0,
            Nullable<long>(reader, "retrieved_file_count"),
            Nullable<long>(reader, "retrieved_bytes"),
            ReadFile(reader));

    private static SearchViewDirectoryPosition DirectoryPosition(
        StoredSearchViewDirectory row,
        SearchProjectionSortKey key,
        SearchViewSortOrder order = SearchViewSortOrder.Relevance)
        => new(
            key.HighFlags,
            key.UploadSpeedFast,
            key.MidFlags,
            key.InferredTrackCount,
            key.UploadSpeedMedium,
            key.BitRate,
            key.StableTieBreaker,
            row.BestChild.Input.Sequence,
            row.Username,
            row.FolderPath,
            row.Ref,
            // Retrieval expands the card, not the search projection's sort set.
            order switch
            {
                SearchViewSortOrder.UploadSpeed => row.BestChild.Input.UploadSpeed ?? -1,
                SearchViewSortOrder.QueueDepth => row.BestChild.Input.QueueLength ?? int.MaxValue,
                SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending
                    or SearchViewSortOrder.DirectorySizeAscending or SearchViewSortOrder.DirectorySizeDescending
                    => checked(row.PublicMatchingBytes + row.LockedMatchingBytes),
                SearchViewSortOrder.FileCountAscending or SearchViewSortOrder.FileCountDescending
                    => checked(row.PublicMatchingFileCount + row.LockedMatchingFileCount),
                _ => null,
            },
            order == SearchViewSortOrder.QueueDepth
                ? row.BestChild.Input.UploadSpeed ?? -1 : null,
            order is SearchViewSortOrder.DirectoryNameAscending
                or SearchViewSortOrder.DirectoryNameDescending
                ? row.FolderPath : null);

    private static StoredSearchViewAggregateTrackGroup ReadAggregateTrack(
        SqliteDataReader reader)
        => new(
            Value<string>(reader, "aggregate_ref"),
            Value<int>(reader, "aggregate_index"),
            Value<string>(reader, "aggregate_query_json"),
            Value<int>(reader, "aggregate_share_count"),
            Value<long>(reader, "aggregate_option_count"),
            ReadFile(reader));

    private static StoredSearchViewAggregateAlbumGroup ReadAggregateAlbum(
        SqliteDataReader reader)
        => new(
            Value<string>(reader, "aggregate_ref"),
            Value<int>(reader, "aggregate_index"),
            Value<string>(reader, "aggregate_query_json"),
            Value<int>(reader, "aggregate_share_count"),
            Value<long>(reader, "aggregate_option_count"),
            ReadDirectory(reader));

    private static void BindPosition(SqliteCommand command, SearchViewFilePosition position)
    {
        Add(command, "$high", (long)position.HighFlags);
        Add(command, "$fast", position.UploadSpeedFast);
        Add(command, "$mid", (long)position.MidFlags);
        Add(command, "$inferred", position.InferredTrackCount);
        Add(command, "$medium", position.UploadSpeedMedium);
        Add(command, "$bitrate", position.BitRate);
        Add(command, "$tie", position.StableTieBreaker);
        Add(command, "$sequence", position.Sequence);
        Add(command, "$ref", position.Ref);
    }

    private static void BindDirectoryPosition(
        SqliteCommand command,
        SearchViewDirectoryPosition position)
    {
        Add(command, "$high", (long)position.HighFlags);
        Add(command, "$fast", position.UploadSpeedFast);
        Add(command, "$mid", (long)position.MidFlags);
        Add(command, "$inferred", position.InferredTrackCount);
        Add(command, "$medium", position.UploadSpeedMedium);
        Add(command, "$bitrate", position.BitRate);
        Add(command, "$tie", position.StableTieBreaker);
        Add(command, "$sequence", position.Sequence);
        Add(command, "$username", position.Username);
        Add(command, "$path", position.FolderPath);
        Add(command, "$ref", position.Ref);
    }

    private static void BindAlternativeFilePosition(
        SqliteCommand command,
        SearchViewSortOrder order,
        SearchViewFilePosition position)
    {
        if (order == SearchViewSortOrder.Relevance)
            return;
        Add(command, "$primary", position.PrimaryNumber
            ?? throw new ArgumentException("The Search View cursor has no order key."));
        if (order == SearchViewSortOrder.QueueDepth)
            Add(command, "$secondary", position.SecondaryNumber
                ?? throw new ArgumentException("The Search View cursor has no queue tie key."));
    }

    private static void BindAlternativeDirectoryPosition(
        SqliteCommand command,
        SearchViewSortOrder order,
        SearchViewDirectoryPosition position)
    {
        if (order == SearchViewSortOrder.Relevance)
            return;
        if (order is SearchViewSortOrder.DirectoryNameAscending
            or SearchViewSortOrder.DirectoryNameDescending)
            Add(command, "$primary_text", position.PrimaryText
                ?? throw new ArgumentException("The Search View cursor has no name key."));
        else
            Add(command, "$primary", position.PrimaryNumber
                ?? throw new ArgumentException("The Search View cursor has no order key."));
        if (order == SearchViewSortOrder.QueueDepth)
            Add(command, "$secondary", position.SecondaryNumber
                ?? throw new ArgumentException("The Search View cursor has no queue tie key."));
    }

    private static string FilePageSql(SearchViewSortOrder order, bool hasCursor, IReadOnlyList<long>? admissions)
        => order == SearchViewSortOrder.Relevance
            ? RelevanceFilePageSql(hasCursor, admissions)
            : AlternativeFilePageSql(order, hasCursor);

    private static string DirectoryPageSql(SearchViewSortOrder order, bool hasCursor)
        => order == SearchViewSortOrder.Relevance
            ? RelevanceDirectoryPageSql(hasCursor)
            : AlternativeDirectoryPageSql(order, hasCursor);

    private static string AlternativeFilePageSql(
        SearchViewSortOrder order,
        bool hasCursor)
    {
        string primary = order switch
        {
            SearchViewSortOrder.UploadSpeed => "COALESCE(upload_speed, -1)",
            SearchViewSortOrder.QueueDepth => "COALESCE(queue_length, 2147483647)",
            SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending
                => "MAX(size_bytes, 0)",
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };
        bool ascending = order is SearchViewSortOrder.QueueDepth
            or SearchViewSortOrder.ItemSizeAscending;
        string direction = ascending ? "ASC" : "DESC";
        string after = ascending ? ">" : "<";
        const string tie = "(sequence > $sequence OR sequence = $sequence AND ref_key > unhex($ref))";
        string seek = order == SearchViewSortOrder.QueueDepth
            ? $"({primary} > $primary OR {primary} = $primary AND "
                + $"(COALESCE(upload_speed, -1) < $secondary OR "
                + $"COALESCE(upload_speed, -1) = $secondary AND {tie}))"
            : $"({primary} {after} $primary OR {primary} = $primary AND {tie})";
        string secondary = order == SearchViewSortOrder.QueueDepth
            ? ", COALESCE(upload_speed, -1) DESC" : "";
        // As with relevance paging, sort only the requested ordering keys.
        // Hydrate facts for the page after choosing its row IDs, rather than
        // carrying full filenames, metadata and condition JSON through the sort.
        string ordering = $"{primary} {direction}{secondary}, sequence, ref_key";
        return "SELECT file.* FROM search_view_file_rows file\n"
            + "WHERE file.rowid IN (SELECT rowid FROM search_view_files\n"
            + "WHERE view_id = (SELECT storage_id FROM search_views WHERE id = $view) AND admitted_revision <= $revision\n"
            + (hasCursor ? $"AND {seek}\n" : "")
            + $"ORDER BY {ordering} LIMIT $limit)\n"
            + $"ORDER BY {ordering};";
    }

    private static string AlternativeDirectoryPageSql(
        SearchViewSortOrder order,
        bool hasCursor)
    {
        string primary = order switch
        {
            SearchViewSortOrder.UploadSpeed => "COALESCE(file.upload_speed, -1)",
            SearchViewSortOrder.QueueDepth => "COALESCE(file.queue_length, 2147483647)",
            SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending
                or SearchViewSortOrder.DirectorySizeAscending
                or SearchViewSortOrder.DirectorySizeDescending
                => "version.public_matching_bytes + version.locked_matching_bytes",
            SearchViewSortOrder.FileCountAscending or SearchViewSortOrder.FileCountDescending
                => "version.public_matching_count + version.locked_matching_count",
            SearchViewSortOrder.DirectoryNameAscending or SearchViewSortOrder.DirectoryNameDescending
                => "directory.folder_path COLLATE SOCKSEEK_NATURAL",
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };
        bool ascending = order is SearchViewSortOrder.QueueDepth
            or SearchViewSortOrder.ItemSizeAscending
            or SearchViewSortOrder.DirectorySizeAscending
            or SearchViewSortOrder.FileCountAscending
            or SearchViewSortOrder.DirectoryNameAscending;
        string direction = ascending ? "ASC" : "DESC";
        string after = ascending ? ">" : "<";
        string parameter = order is SearchViewSortOrder.DirectoryNameAscending
            or SearchViewSortOrder.DirectoryNameDescending
            ? "$primary_text COLLATE SOCKSEEK_NATURAL" : "$primary";
        const string tie = "(directory.username COLLATE BINARY > $username COLLATE BINARY "
            + "OR directory.username COLLATE BINARY = $username COLLATE BINARY "
            + "AND directory.folder_path COLLATE BINARY > $path COLLATE BINARY "
            + "OR directory.username COLLATE BINARY = $username COLLATE BINARY "
            + "AND directory.folder_path COLLATE BINARY = $path COLLATE BINARY "
            + "AND directory.item_ref > $ref)";
        string seek = order == SearchViewSortOrder.QueueDepth
            ? $"({primary} > $primary OR {primary} = $primary AND "
                + $"(COALESCE(file.upload_speed, -1) < $secondary OR "
                + $"COALESCE(file.upload_speed, -1) = $secondary AND {tie}))"
            : $"({primary} {after} {parameter} OR {primary} = {parameter} AND {tie})";
        string secondary = order == SearchViewSortOrder.QueueDepth
            ? ", COALESCE(file.upload_speed, -1) DESC" : "";
        string ordering = $"{primary} {direction}{secondary}, directory.username COLLATE BINARY, "
            + "directory.folder_path COLLATE BINARY, directory.item_ref";
        return OrderedDirectoryPageSql(ordering, hasCursor ? seek : null,
            needsFileOrdering: order is SearchViewSortOrder.UploadSpeed or SearchViewSortOrder.QueueDepth);
    }

    private static string RelevanceOrdering(string prefix) => $"""
        {prefix}sort_high DESC, {prefix}sort_upload_fast DESC, {prefix}sort_mid DESC,
        {prefix}sort_inferred DESC, {prefix}sort_upload_medium DESC,
        {prefix}sort_bitrate DESC, {prefix}sort_tie DESC, {prefix}sequence
        """;

    private static string FileOrdering(string prefix)
        => RelevanceOrdering(prefix) + $", {prefix}ref_key";

    private static string DirectoryRelevanceOrdering(string prefix)
        => RelevanceOrdering(prefix) + """
            , directory.username COLLATE BINARY,
            directory.folder_path COLLATE BINARY, directory.item_ref
            """;

    private static string FileSeek(string prefix) => $"""
            {prefix}sort_high < $high OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast < $fast OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast = $fast AND {prefix}sort_mid < $mid OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast = $fast AND {prefix}sort_mid = $mid AND {prefix}sort_inferred < $inferred OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast = $fast AND {prefix}sort_mid = $mid AND {prefix}sort_inferred = $inferred AND {prefix}sort_upload_medium < $medium OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast = $fast AND {prefix}sort_mid = $mid AND {prefix}sort_inferred = $inferred AND {prefix}sort_upload_medium = $medium AND {prefix}sort_bitrate < $bitrate OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast = $fast AND {prefix}sort_mid = $mid AND {prefix}sort_inferred = $inferred AND {prefix}sort_upload_medium = $medium AND {prefix}sort_bitrate = $bitrate AND {prefix}sort_tie < $tie OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast = $fast AND {prefix}sort_mid = $mid AND {prefix}sort_inferred = $inferred AND {prefix}sort_upload_medium = $medium AND {prefix}sort_bitrate = $bitrate AND {prefix}sort_tie = $tie AND {prefix}sequence > $sequence OR
            {prefix}sort_high = $high AND {prefix}sort_upload_fast = $fast AND {prefix}sort_mid = $mid AND {prefix}sort_inferred = $inferred AND {prefix}sort_upload_medium = $medium AND {prefix}sort_bitrate = $bitrate AND {prefix}sort_tie = $tie AND {prefix}sequence = $sequence AND {prefix}ref_key > unhex($ref)
            """;

    private static string RelevanceFilePageSql(bool hasCursor, IReadOnlyList<long>? admissions)
    {
        if (admissions is not { Count: > 0 }) return $"""
            SELECT file.* FROM search_view_file_rows file
            WHERE file.id IN (
                SELECT member.id FROM search_view_files member
                WHERE member.view_id = (SELECT storage_id FROM search_views WHERE id = $view)
                  AND member.admitted_revision <= $revision
                  {(hasCursor ? "AND (" + FileSeek("member.") + ")" : "")}
                ORDER BY {FileOrdering("member.")} LIMIT $limit)
            ORDER BY {FileOrdering("file.")};
            """;

        // Keep the admission-first index: a global rank index rewrites many
        // existing pages on each publication. SQLite merges ordered admission
        // runs and stops at the page boundary, before hydrating source facts.
        // Keep ORDER BY/LIMIT on the compound query: wrapping each run in a
        // limited subquery makes SQLite materialize and sort every run again.
        var runs = admissions.Select(admission => $"""
            SELECT member.id, member.sort_high, member.sort_upload_fast, member.sort_mid,
                member.sort_inferred, member.sort_upload_medium, member.sort_bitrate,
                member.sort_tie, member.sequence, member.ref_key
            FROM search_view_files member INDEXED BY ix_search_view_files_page
            WHERE member.view_id = $storage
              AND member.admitted_revision = {admission.ToString(CultureInfo.InvariantCulture)}
              {(hasCursor ? "AND (" + FileSeek("member.") + ")" : "")}
            """);
        return $"""
            WITH page AS MATERIALIZED (
                {string.Join(" UNION ALL ", runs)}
                ORDER BY {FileOrdering("")} LIMIT $limit)
            SELECT file.* FROM page
            CROSS JOIN search_view_file_rows file ON file.id = page.id
            ORDER BY {FileOrdering("file.")};
            """;
    }

    private static string DirectoryRelevanceSeek()
    {
        // Rank descends; sequence and exact directory identities ascend.
        const string rank = """
            (version.sort_high, version.sort_upload_fast, version.sort_mid,
             version.sort_inferred, version.sort_upload_medium, version.sort_bitrate, version.sort_tie)
            """;
        const string boundary = "($high, $fast, $mid, $inferred, $medium, $bitrate, $tie)";
        return $"""
            {rank} <= {boundary}
            AND ({rank} < {boundary}
                 OR (version.sequence, directory.username COLLATE BINARY,
                     directory.folder_path COLLATE BINARY, directory.item_ref)
                    > ($sequence, $username, $path, $ref))
            """;
    }

    private static string RelevanceDirectoryPageSql(bool hasCursor, bool albumGroup = false)
    {
        string seek = hasCursor ? DirectoryRelevanceSeek() : "";
        // Seek directory ranks before hydrating observations/previews. The
        // latest-version check includes tombstones, so removed/absorbed folders
        // cannot reappear through an older indexed version.
        // A membership set lets the ordered index run once. Driving a rank
        // cursor from each individual option can instead cause repeated scans.
        string membership = albumGroup ? """
            selected_group AS MATERIALIZED (
                SELECT id FROM search_view_album_records
                WHERE view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view) AND item_ref=$group),
            latest_membership AS (
                SELECT directory_id,MAX(revision) AS revision
                FROM search_view_album_directory_records
                WHERE group_id=(SELECT id FROM selected_group) AND revision<=$revision
                GROUP BY directory_id),
            """ : "";
        string groupFilter = albumGroup ? """
            AND version.directory_id IN (
                SELECT membership.directory_id FROM latest_membership
                CROSS JOIN search_view_album_directory_records membership
                  ON membership.group_id=(SELECT id FROM selected_group)
                 AND membership.directory_id=latest_membership.directory_id
                 AND membership.revision=latest_membership.revision
                WHERE membership.is_present=1)
            """ : "";
        string page = $"""
            WITH {membership} page AS MATERIALIZED (
                SELECT version.directory_id, version.revision
                FROM search_view_directory_version_records version
                     INDEXED BY ix_search_view_directory_rank
                CROSS JOIN search_view_directory_records directory
                  ON directory.id = version.directory_id
                WHERE version.view_storage_id = (SELECT storage_id FROM search_views WHERE id=$view)
                  AND version.revision <= $revision
                  AND version.is_removed = 0
                  {groupFilter}
                  {(hasCursor ? "AND (" + seek + ")" : "")}
                  AND version.revision = (
                      SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
                      WHERE latest.directory_id = version.directory_id
                        AND latest.revision <= $revision)
                ORDER BY {DirectoryRelevanceOrdering("version.")} LIMIT $limit)
            """;
        return HydrateDirectoryPageSql(page, DirectoryRelevanceOrdering("file."));
    }

    private static string OrderedDirectoryPageSql(string ordering, string? seek, bool needsFileOrdering)
        // Select only directory identities and ordering keys across the View.
        // Full observations/condition JSON belong to the bounded page, rather
        // than every directory considered by the sort (10k+ for large searches).
        // CROSS JOIN keeps the selected identities first: SQLite otherwise
        // scans/hydrates all versions before checking the bounded page.
        => HydrateDirectoryPageSql($"""
            WITH latest AS (
                SELECT directory.id AS directory_id,
                       (SELECT MAX(version.revision) FROM search_view_directory_version_records version
                        WHERE version.directory_id=directory.id AND version.revision<=$revision) AS revision
                FROM search_view_directory_records directory
                WHERE directory.view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view)),
            page AS MATERIALIZED (
                SELECT version.directory_id,version.revision
                FROM latest
                CROSS JOIN search_view_directory_version_records version
                  ON version.directory_id=latest.directory_id AND version.revision=latest.revision
                CROSS JOIN search_view_directory_records directory ON directory.id=version.directory_id
                {(needsFileOrdering ? "CROSS JOIN search_view_files file ON file.id = version.best_file_id" : "")}
                WHERE version.is_removed=0
                {(seek == null ? "" : "AND (" + seek + ")")}
                ORDER BY {ordering} LIMIT $limit)
            """, ordering);

    // Keep numeric keys through page hydration. Rejoining public identity views
    // can scan the version history once for every returned row.
    private static string HydrateDirectoryPageSql(string page, string ordering)
        => $"""
            {page}
            SELECT directory.item_ref AS directory_ref,
                   directory.username AS directory_username,
                   directory.folder_path AS directory_folder_path,
                   version.public_matching_count,
                   version.locked_matching_count,
                   version.public_matching_bytes,
                   version.locked_matching_bytes,
                   version.is_fully_retrieved,
                   version.retrieved_file_count,
                   version.retrieved_bytes,
                   file.*
            FROM page
            CROSS JOIN search_view_directory_version_records version
              ON version.directory_id=page.directory_id AND version.revision=page.revision
            CROSS JOIN search_view_directory_records directory ON directory.id=version.directory_id
            CROSS JOIN search_view_file_rows file ON file.id = version.best_file_id
            ORDER BY {ordering};
            """;

    private static string AggregateTrackOptionsSql(bool hasCursor)
        // Sort memberships before joining full observation facts, including for
        // partial searches and large groups whose page contains only 500 files.
        => $"""
            WITH page AS MATERIALIZED (
                SELECT member.id FROM search_view_aggregate_track_files mapping
                JOIN search_view_files member ON member.id = mapping.file_id
                WHERE mapping.group_id = (
                    SELECT id FROM search_view_aggregate_tracks WHERE view_id = $view AND item_ref = $group)
                  AND mapping.admitted_revision <= $revision
                  {(hasCursor ? "AND (" + FileSeek("member.") + ")" : "")}
                ORDER BY {FileOrdering("member.")} LIMIT $limit)
            SELECT file.* FROM page
            JOIN search_view_file_rows file ON file.id = page.id
            ORDER BY {FileOrdering("file.")};
            """;

    // Select the bounded page before hydrating representative facts. Otherwise
    // sorting thousands of groups materializes full file/attribute rows for all.
    // Latest-version checks include tombstones, although the page index excludes them.
    private const string AggregateAlbumPageSql = """
        WITH page AS MATERIALIZED (
            SELECT group_version.group_id,group_version.revision AS group_revision,
                   directory_version.directory_id,directory_version.revision AS directory_revision
            FROM search_view_album_version_records group_version INDEXED BY ix_search_view_album_page
            CROSS JOIN search_view_album_records album ON album.id=group_version.group_id
            CROSS JOIN search_view_directory_version_records directory_version
              ON directory_version.directory_id=group_version.representative_directory_id
             AND directory_version.revision=(
                 SELECT MAX(latest.revision) FROM search_view_directory_version_records latest
                 WHERE latest.directory_id=group_version.representative_directory_id AND latest.revision<=$revision)
            CROSS JOIN search_view_directory_records directory ON directory.id=directory_version.directory_id
            WHERE group_version.view_storage_id=(SELECT storage_id FROM search_views WHERE id=$view)
              AND group_version.revision<=$revision
              AND group_version.is_removed=0 AND directory_version.is_removed=0
              AND group_version.revision=(
                  SELECT MAX(latest.revision) FROM search_view_album_version_records latest
                  WHERE latest.group_id=group_version.group_id AND latest.revision<=$revision)
              AND ($cursor_shares IS NULL OR group_version.share_count<$cursor_shares
                   OR group_version.share_count=$cursor_shares
                      AND (group_version.group_index,album.item_ref)>($cursor_index,$cursor_ref))
            ORDER BY group_version.share_count DESC,group_version.group_index,album.item_ref
            LIMIT $limit)
        SELECT aggregate.item_ref AS aggregate_ref,
               group_version.group_index AS aggregate_index,
               aggregate.query_json AS aggregate_query_json,
               group_version.share_count AS aggregate_share_count,
               group_version.selectable_option_count AS aggregate_option_count,
               directory.item_ref AS directory_ref,
               directory.username AS directory_username,
               directory.folder_path AS directory_folder_path,
               directory_version.public_matching_count,
               directory_version.locked_matching_count,
               directory_version.public_matching_bytes,
               directory_version.locked_matching_bytes,
               directory_version.is_fully_retrieved,
               directory_version.retrieved_file_count,
               directory_version.retrieved_bytes,
               file.*
        FROM page
        CROSS JOIN search_view_album_version_records group_version
          ON group_version.group_id=page.group_id AND group_version.revision=page.group_revision
        CROSS JOIN search_view_album_records aggregate ON aggregate.id=group_version.group_id
        CROSS JOIN search_view_directory_version_records directory_version
          ON directory_version.directory_id=page.directory_id AND directory_version.revision=page.directory_revision
        CROSS JOIN search_view_directory_records directory ON directory.id=directory_version.directory_id
        CROSS JOIN search_view_file_rows file ON file.id = directory_version.best_file_id
        ORDER BY group_version.share_count DESC, group_version.group_index, aggregate.item_ref;
        """;

    private static readonly SearchViewCounters EmptyCounters = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    // Public refs are case-sensitive opaque strings. Ignore missing/malformed
    // exclusions without allowing NULL to turn NOT IN into an empty selection.
    private const string RequestedFileKeysSql = """
        SELECT unhex(value) AS ref_key FROM json_each($refs)
        WHERE value = lower(hex(unhex(value))) COLLATE BINARY
        """;

    // Explicit selections are usually one or two files. Drive the unique
    // (view_id, ref_key) lookup from those refs before hydrating facts or joining
    // memberships. An IN filter on the row view lets SQLite scan every admitted
    // file instead; use the same seek for counts, commits and child selections.
    private const string RequestedFilesSql = $"""
        ({RequestedFileKeysSql}) requested
        CROSS JOIN search_views selected_view ON selected_view.id = $view
        CROSS JOIN search_view_files selected_file
          ON selected_file.view_id = selected_view.storage_id AND selected_file.ref_key = requested.ref_key
        CROSS JOIN search_view_file_rows file ON file.id = selected_file.id
        """;

    private const string FileInsertSql = """
        INSERT INTO search_view_files (
            view_id, ref_key, admitted_revision, sequence, source_row_revision, observation_id,
            size_bytes, upload_speed, queue_length, preference_tier, necessary_conditions_satisfied,
            matched_condition_flags, configured_condition_flags,
            sort_high, sort_upload_fast, sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie, source_id)
        VALUES (
            $view, $ref, $admitted, $sequence, $source_row_revision, $observation,
            $size, $upload_speed, $queue_length, $tier, $necessary_satisfied, $conditions, $configured_conditions,
            $high, $fast, $mid, $inferred, $medium, $sort_bitrate, $tie, $source)
        ON CONFLICT(view_id, ref_key) DO NOTHING;
        """;

    private static Activity? StartActivity(string operation, Guid viewId, long revision)
    {
        Activity? activity = Activities.StartActivity(operation);
        activity?.SetTag("view.id", viewId);
        activity?.SetTag("view.revision", revision);
        return activity;
    }

    private async Task<SqliteConnection> OpenReadConnectionAsync(
        Guid viewId, long revision, CancellationToken cancellationToken)
    {
        using var operation = StartActivity("open", viewId, revision);
        SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The connection owns the transaction; its commands inherit it and
            // closing the connection releases the snapshot. Deferred reads do
            // not reserve SQLite's writer slot while metadata and rows are read.
            connection.BeginTransaction(deferred: true);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateCollation("SOCKSEEK_NATURAL", static (left, right) =>
            CultureInfo.InvariantCulture.CompareInfo.Compare(
                left,
                right,
                CompareOptions.IgnoreCase
                    | CompareOptions.IgnoreNonSpace
                    | CompareOptions.NumericOrdering));
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken)
            .ConfigureAwait(false);
        return connection;
    }

    private Task<T> WithWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        bool background = true)
    {
        EnsureInitialized();
        return writes.ExecuteAsync((context, ct) => action(
            (SqliteConnection)context.Database.GetDbConnection(),
            (SqliteTransaction)context.Database.CurrentTransaction!.GetDbTransaction(), ct),
            cancellationToken, background);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLimit(int limit)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new InvalidOperationException("The search-view store is unavailable.");
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, SqliteValue(value));

    private static object SqliteValue(object? value)
        => value switch
        {
            null => DBNull.Value,
            Guid id => id.ToString("D"),
            DateTimeOffset time => time.ToUniversalTime().ToUnixTimeMilliseconds(),
            bool flag => flag ? 1 : 0,
            _ => value,
        };

    private static T Value<T>(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        var statement = reader.Handle!;
        return ReadValue<T>(reader, statement, ordinal, raw.sqlite3_column_type(statement, ordinal));
    }

    private static T ReadValue<T>(SqliteDataReader reader, sqlite3_stmt statement, int ordinal, int storageType)
    {
        // These helpers run only after Read() established the current row.
        // Read native typed values without repeating the provider's per-field
        // row checks; preserve its conversions and errors for other storage types.
        if (storageType == raw.SQLITE_INTEGER)
        {
            if (typeof(T) == typeof(long))
                return (T)(object)raw.sqlite3_column_int64(statement, ordinal);
            if (typeof(T) == typeof(int))
                return (T)(object)checked((int)raw.sqlite3_column_int64(statement, ordinal));
        }
        if (storageType == raw.SQLITE_TEXT && typeof(T) == typeof(string))
            return (T)(object)raw.sqlite3_column_text(statement, ordinal).utf8_to_string();
        return reader.GetFieldValue<T>(ordinal);
    }

    private static T? Nullable<T>(SqliteDataReader reader, string name) where T : struct
    {
        int ordinal = reader.GetOrdinal(name);
        var statement = reader.Handle!;
        int storageType = raw.sqlite3_column_type(statement, ordinal);
        return storageType == raw.SQLITE_NULL ? null : ReadValue<T>(reader, statement, ordinal, storageType);
    }

    private static bool? NullableBool(SqliteDataReader reader, string name)
    {
        long? value = Nullable<long>(reader, name);
        return value == null ? null : value != 0;
    }

    private static string? NullableString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        var statement = reader.Handle!;
        int storageType = raw.sqlite3_column_type(statement, ordinal);
        return storageType == raw.SQLITE_NULL ? null : ReadValue<string>(reader, statement, ordinal, storageType);
    }

    private static DateTimeOffset Time(SqliteDataReader reader, string name)
        => DateTimeOffset.FromUnixTimeMilliseconds(Value<long>(reader, name));

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref initialized, 0);
        SqliteConnectionPool.Clear(ConnectionString);
        return ValueTask.CompletedTask;
    }
}
