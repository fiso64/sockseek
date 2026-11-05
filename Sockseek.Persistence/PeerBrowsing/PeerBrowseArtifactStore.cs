using System.Globalization;
using Microsoft.Data.Sqlite;
using Sockseek.Core;
using Sockseek.Core.IO;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Persistence;

namespace Sockseek.Persistence.PeerBrowsing;

/// <summary>
/// Owns short-lived peer-browse resources and one immutable compressed artifact per
/// successful generation. It is deliberately separate from Sockseek's domain
/// history database.
/// </summary>
public sealed class PeerBrowseArtifactStore : IAsyncDisposable
{
    internal const int ArtifactFormatVersion = 11;
    public static readonly TimeSpan DefaultResourceRetention = TimeSpan.FromHours(24);
    public const long DefaultArtifactByteBudget = 2L * 1024 * 1024 * 1024;
    public const int DefaultResourceCountTarget = 4_096;

    private readonly string rootDirectory;
    private readonly string stagingDirectory;
    private readonly string artifactDirectory;
    private readonly string registryPath;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan resourceRetention;
    private readonly long artifactByteBudget;
    private readonly int resourceCountTarget;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly Lock leaseGate = new();
    private readonly Dictionary<Guid, int> leases = [];
    private readonly HashSet<Guid> evicting = [];
    private long lastCleanupLogTick = long.MinValue;
    private long lastBudgetLogTick = long.MinValue;
    private const long ArtifactMemoryTarget = 256L * 1024 * 1024;
    private readonly Dictionary<Guid, (PeerBrowseArtifact Artifact, LinkedListNode<Guid> Node)> loadedArtifacts = [];
    private readonly LinkedList<Guid> artifactRecency = new();
    private readonly Dictionary<Guid, Task<PeerBrowseArtifact>> artifactLoads = [];
    private long loadedArtifactBytes;
    private bool initialized;
    private bool disposed;
    private readonly ILogger<PeerBrowseArtifactStore> logger;

    /// <summary>Raised after a browse resource has been removed from the registry.</summary>
    public event Action<Guid>? ResourceRemoved;

    public PeerBrowseArtifactStore(
        string dataDirectory,
        TimeProvider? timeProvider = null,
        TimeSpan? resourceRetention = null,
        long artifactByteBudget = DefaultArtifactByteBudget,
        int resourceCountTarget = DefaultResourceCountTarget,
        ILogger<PeerBrowseArtifactStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (resourceRetention is { } retention && retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(resourceRetention));
        if (artifactByteBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(artifactByteBudget));
        if (resourceCountTarget <= 0)
            throw new ArgumentOutOfRangeException(nameof(resourceCountTarget));

        rootDirectory = Path.GetFullPath(Path.Combine(dataDirectory, "peer-browses"));
        stagingDirectory = Path.Combine(rootDirectory, "staging");
        artifactDirectory = Path.Combine(rootDirectory, "artifacts");
        registryPath = Path.Combine(rootDirectory, "resources.sqlite");
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<PeerBrowseArtifactStore>.Instance;
        this.resourceRetention = resourceRetention ?? DefaultResourceRetention;
        this.artifactByteBudget = artifactByteBudget;
        this.resourceCountTarget = resourceCountTarget;
    }

    public string RootDirectory => rootDirectory;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Volatile.Read(ref initialized))
            return;

        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;

            OwnerOnlyFilePermissions.EnsureDirectory(rootDirectory);
            OwnerOnlyFilePermissions.EnsureDirectory(stagingDirectory);
            OwnerOnlyFilePermissions.EnsureDirectory(artifactDirectory);
            await using (SqliteConnection connection = await OpenRegistryAsync(
                             SqliteOpenMode.ReadWriteCreate,
                             cancellationToken).ConfigureAwait(false))
            {
                // This registry owns disposable artifacts, not accepted jobs or
                // history. Expire an obsolete format once at startup so every
                // reader can use the current schema without compatibility probes.
                await using (var version = connection.CreateCommand())
                {
                    version.CommandText = "PRAGMA user_version;";
                    if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                            CultureInfo.InvariantCulture) != ArtifactFormatVersion)
                        await ExecuteAsync(connection, "DROP TABLE IF EXISTS browse_resources;", cancellationToken)
                            .ConfigureAwait(false);
                }
                await ExecuteAsync(
                    connection,
                    """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    PRAGMA temp_store=FILE;
                    PRAGMA busy_timeout=5000;

                    CREATE TABLE IF NOT EXISTS browse_resources (
                        browse_id TEXT PRIMARY KEY,
                        local_account TEXT NOT NULL,
                        username TEXT NOT NULL,
                        state INTEGER NOT NULL,
                        phase INTEGER NOT NULL,
                        compressed_bytes_received INTEGER NOT NULL,
                        compressed_bytes_expected INTEGER,
                        directory_count INTEGER NOT NULL,
                        file_count INTEGER NOT NULL,
                        total_file_bytes INTEGER NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        completed_at_utc TEXT,
                        expires_at_utc TEXT NOT NULL,
                        failure_code TEXT,
                        failure_message TEXT,
                        artifact_file TEXT,
                        artifact_bytes INTEGER,
                        revision INTEGER NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_browse_resources_key
                        ON browse_resources(local_account, username, completed_at_utc DESC);
                    CREATE INDEX IF NOT EXISTS idx_browse_resources_expiry
                        ON browse_resources(expires_at_utc);
                    """,
                    cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(connection, $"PRAGMA user_version={ArtifactFormatVersion};", cancellationToken)
                    .ConfigureAwait(false);

                DateTimeOffset now = timeProvider.GetUtcNow();
                await using var interrupted = connection.CreateCommand();
                interrupted.CommandText =
                    """
                    UPDATE browse_resources
                    SET state = $failed,
                        failure_code = 'daemon-restarted',
                        failure_message = 'The daemon restarted before the peer browse completed.',
                        updated_at_utc = $now,
                        expires_at_utc = $expires,
                        revision = revision + 1
                    WHERE state IN ($queued, $running);
                    """;
                Add(interrupted, "$failed", (int)PeerBrowseState.Failed);
                Add(interrupted, "$queued", (int)PeerBrowseState.Queued);
                Add(interrupted, "$running", (int)PeerBrowseState.Running);
                Add(interrupted, "$now", Format(now));
                Add(interrupted, "$expires", Format(now + resourceRetention));
                await interrupted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            OwnerOnlyFilePermissions.EnsureFile(registryPath);
            DeleteFiles(stagingDirectory, "*.staging");
            await RemoveOrphanArtifactsAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }

        await EvictAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowseResource> CreateQueuedAsync(
        string localAccount,
        string username,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        localAccount = PeerIdentityValidator.ValidateUsername(localAccount);
        username = PeerIdentityValidator.ValidateUsername(username);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var resource = new PeerBrowseResource(
            Guid.NewGuid(),
            localAccount,
            username,
            PeerBrowseState.Queued,
            PeerBrowsePhase.WaitingForPeer,
            0,
            null,
            0,
            0,
            0,
            now,
            now,
            null,
            now + resourceRetention,
            null,
            1);

        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO browse_resources(
                browse_id, local_account, username, state, phase,
                compressed_bytes_received, compressed_bytes_expected,
                directory_count, file_count,
                total_file_bytes, created_at_utc, updated_at_utc,
                completed_at_utc, expires_at_utc, failure_code,
                failure_message, artifact_file, artifact_bytes, revision)
            VALUES(
                $id, $account, $username, $state, $phase,
                0, NULL, 0, 0, 0, $created, $updated,
                NULL, $expires, NULL, NULL, NULL, NULL, 1);
            """;
        Add(command, "$id", resource.BrowseId.ToString("D"));
        Add(command, "$account", resource.LocalAccount);
        Add(command, "$username", resource.Username);
        Add(command, "$state", (int)resource.State);
        Add(command, "$phase", (int)resource.Phase);
        Add(command, "$created", Format(resource.CreatedAt));
        Add(command, "$updated", Format(resource.UpdatedAt));
        Add(command, "$expires", Format(resource.ExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return resource;
    }

    public async Task<PeerBrowseResource?> GetAsync(
        Guid browseId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        PeerBrowseResource? resource;
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        {
            resource = await ReadResourceAsync(connection, browseId, cancellationToken).ConfigureAwait(false);
        }
        if (resource is null
            || resource.State is PeerBrowseState.Queued or PeerBrowseState.Running
            || resource.ExpiresAt > timeProvider.GetUtcNow())
            return resource;

        await EvictAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<PeerBrowseResource?> FindFreshAsync(
        string localAccount,
        string username,
        TimeSpan freshness,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (freshness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(freshness));
        localAccount = PeerIdentityValidator.ValidateUsername(localAccount);
        username = PeerIdentityValidator.ValidateUsername(username);

        PeerBrowseResource? resource;
        string? artifactFile;
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                ResourceSelect +
                """
                 WHERE local_account = $account
                   AND username = $username
                   AND state = $complete
                   AND completed_at_utc > $cutoff
                   AND expires_at_utc > $now
                 ORDER BY completed_at_utc DESC
                 LIMIT 1;
                """;
            Add(command, "$account", localAccount);
            Add(command, "$username", username);
            Add(command, "$complete", (int)PeerBrowseState.Complete);
            DateTimeOffset now = timeProvider.GetUtcNow();
            Add(command, "$cutoff", Format(now - freshness));
            Add(command, "$now", Format(now));
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            resource = ReadResource(reader);
            artifactFile = reader.IsDBNull(14) ? null : reader.GetString(14);
        }

        return artifactFile is not null
               && File.Exists(ResolveArtifactPath(artifactFile))
            ? resource
            : null;
    }

    public Task MarkRunningAsync(Guid browseId, CancellationToken cancellationToken = default)
        => UpdateLifecycleAsync(
            browseId,
            PeerBrowseState.Running,
            PeerBrowsePhase.WaitingForPeer,
            null,
            cancellationToken);

    public Task MarkIndexingAsync(Guid browseId, CancellationToken cancellationToken = default)
        => UpdateLifecycleAsync(
            browseId,
            PeerBrowseState.Running,
            PeerBrowsePhase.Indexing,
            null,
            cancellationToken,
            onlyWhileActive: true);

    public async Task UpdateProgressAsync(
        Guid browseId,
        PeerBrowsePhase phase,
        long compressedBytesReceived,
        long? compressedBytesExpected,
        PeerBrowseIndexProgress progress,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        // A restarted acquisition is failed, never resumed from progress. Keep
        // these best-effort counters transactional without forcing an fsync for
        // every display refresh. Acceptance/terminal writes retain FULL durability.
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE browse_resources
            SET phase = CASE WHEN phase < $phase THEN $phase ELSE phase END,
                compressed_bytes_received = MAX(compressed_bytes_received, $compressed),
                compressed_bytes_expected = COALESCE($expected, compressed_bytes_expected),
                directory_count = MAX(directory_count, $directories),
                file_count = MAX(file_count, $files),
                total_file_bytes = MAX(total_file_bytes, $total_bytes),
                updated_at_utc = $updated,
                revision = revision + 1
            WHERE browse_id = $id AND state = $running;
            """;
        Add(command, "$id", browseId.ToString("D"));
        Add(command, "$phase", (int)phase);
        Add(command, "$compressed", compressedBytesReceived);
        Add(command, "$expected", compressedBytesExpected);
        Add(command, "$directories", progress.DirectoryCount);
        Add(command, "$files", progress.FileCount);
        Add(command, "$total_bytes", progress.TotalFileBytes);
        Add(command, "$updated", Format(timeProvider.GetUtcNow()));
        Add(command, "$running", (int)PeerBrowseState.Running);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        Guid browseId,
        string code,
        string message,
        CancellationToken cancellationToken = default)
    {
        await UpdateLifecycleAsync(
                browseId,
                PeerBrowseState.Failed,
                PeerBrowsePhase.Indexing,
                new PeerBrowseFailure(code, message),
                cancellationToken,
                onlyWhileActive: true)
            .ConfigureAwait(false);
        await EvictCoreAsync(browseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkCancelledAsync(Guid browseId, CancellationToken cancellationToken = default)
    {
        await UpdateLifecycleAsync(
                browseId,
                PeerBrowseState.Cancelled,
                PeerBrowsePhase.Indexing,
                new PeerBrowseFailure("browse-cancelled", "The peer browse was cancelled."),
                cancellationToken,
                onlyWhileActive: true)
            .ConfigureAwait(false);
        await EvictCoreAsync(browseId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PeerBrowseArtifactWriter> BeginWriteAsync(
        PeerBrowseResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await PeerBrowseArtifactWriter.CreateAsync(this, resource, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PeerDirectorySnapshot> ReadDirectoryAsync(Guid browseId, PeerDirectoryIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        PeerBrowseResource resource = await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(resource.Username, identity.Username))
            throw new ArgumentException("The directory peer does not match the browse resource.", nameof(identity));
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false)).ReadDirectory(identity, cancellationToken);
    }

    public async Task<PeerBrowseDirectoryEntry?> ReadDirectoryEntryAsync(Guid browseId, long directoryId,
        CancellationToken cancellationToken = default)
    {
        if (directoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(directoryId));
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false)).DirectoryEntry(directoryId);
    }

    public async Task<PeerBrowsePage<PeerBrowseDirectoryEntry>> ReadDirectoriesAsync(Guid browseId, long? parentId,
        string? query, bool recursive, string? afterSortKey, long? afterId, int limit, CancellationToken cancellationToken = default)
    {
        ValidatePage(afterSortKey, afterId, limit);
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false))
            .ReadDirectories(parentId, query, recursive, afterSortKey, afterId, limit, cancellationToken);
    }

    public async Task<PeerBrowsePage<PeerBrowseFileEntry>> ReadFilesAsync(Guid browseId, long directoryId, string? query,
        string? afterSortKey, long? afterId, int limit, CancellationToken cancellationToken = default)
    {
        if (directoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(directoryId));
        ValidatePage(afterSortKey, afterId, limit);
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false))
            .ReadFiles(directoryId, query, afterSortKey, afterId, limit, cancellationToken);
    }

    public async Task<PeerBrowseFileEntry?> ReadFileEntryAsync(Guid browseId, long fileId,
        CancellationToken cancellationToken = default)
    {
        if (fileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(fileId));
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false)).FileEntry(fileId);
    }

    public async Task<PeerBrowseSearchPage> SearchAsync(Guid browseId, string query, string? afterSortKey,
        PeerBrowseSearchEntryKind? afterKind, long? afterId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateSearchPage(afterSortKey, afterKind, afterId, limit);
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false))
            .Search(query.Trim(), afterSortKey, afterKind, afterId, limit, cancellationToken);
    }

    public async Task<string?> ReadSearchSortKeyAsync(Guid browseId, PeerBrowseSearchEntryKind kind, long entryId,
        CancellationToken cancellationToken = default)
    {
        if (entryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(entryId));
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false)).SearchSortKey(kind, entryId);
    }

    public async Task<PeerBrowseDownloadResolution> ResolveDownloadSelectionAsync(Guid browseId,
        IReadOnlyList<long> directoryIds, IReadOnlyList<long> fileIds, CancellationToken cancellationToken = default,
        IReadOnlyList<long>? excludedFileIds = null, IReadOnlyList<long>? excludedDirectoryIds = null)
    {
        ArgumentNullException.ThrowIfNull(directoryIds);
        ArgumentNullException.ThrowIfNull(fileIds);
        var resource = await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return (await ReadArtifactAsync(browseId, cancellationToken).ConfigureAwait(false))
            .Resolve(resource.Username, directoryIds, fileIds, excludedFileIds ?? [], excludedDirectoryIds ?? [], cancellationToken);
    }

    private async Task<PeerBrowseArtifact> ReadArtifactAsync(Guid browseId, CancellationToken cancellationToken)
    {
        await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using ArtifactLease lease = await AcquireLeaseAsync(browseId, cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<PeerBrowseArtifact>? producer = null;
        Task<PeerBrowseArtifact> task;
        lock (leaseGate)
        {
            if (loadedArtifacts.TryGetValue(browseId, out var cached))
            {
                artifactRecency.Remove(cached.Node);
                artifactRecency.AddFirst(cached.Node);
                return cached.Artifact;
            }
            if (!artifactLoads.TryGetValue(browseId, out task!))
            {
                producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                task = producer.Task;
                artifactLoads.Add(browseId, task);
            }
        }
        if (producer is not null)
            _ = Task.Run(() => LoadArtifactAsync(browseId, producer));
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadArtifactAsync(Guid browseId, TaskCompletionSource<PeerBrowseArtifact> completion)
    {
        try
        {
            // The load owns a lease and survives an individual cancelled waiter.
            await using var lease = await AcquireLeaseAsync(browseId).ConfigureAwait(false);
            var artifact = PeerBrowseArtifact.Read(lease.ArtifactPath, CancellationToken.None);
            RememberArtifact(browseId, artifact);
            completion.TrySetResult(artifact);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            _ = completion.Task.Exception; // Observe failure even if every waiter cancelled.
        }
        finally
        {
            lock (leaseGate)
                artifactLoads.Remove(browseId);
        }
    }

    private void RememberArtifact(Guid browseId, PeerBrowseArtifact artifact)
    {
        lock (leaseGate)
        {
            if (disposed)
                return;
            ForgetArtifact(browseId);
            // A working-set target, never an admission limit. Keep one oversized
            // artifact usable; evict idle cache references, not active readers.
            while (artifactRecency.Last is { } last && loadedArtifactBytes + artifact.EstimatedBytes > ArtifactMemoryTarget)
                ForgetArtifact(last.Value);
            var node = artifactRecency.AddFirst(browseId);
            loadedArtifacts.Add(browseId, (artifact, node));
            loadedArtifactBytes += artifact.EstimatedBytes;
        }
    }

    private void ForgetArtifact(Guid browseId)
    {
        lock (leaseGate)
        {
            if (!loadedArtifacts.Remove(browseId, out var old))
                return;
            artifactRecency.Remove(old.Node);
            loadedArtifactBytes -= old.Artifact.EstimatedBytes;
        }
    }

    public async Task<PeerBrowseResourcePage> ListAsync(
        string localAccount,
        string? username,
        PeerBrowseState? state,
        DateTimeOffset? afterCreatedAt,
        Guid? afterBrowseId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateResourcePage(afterCreatedAt, afterBrowseId, limit);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenRegistryAsync(SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ResourceSelect
            + " WHERE local_account = $local_account"
            + " AND (state IN ($queued_state, $running_state) OR expires_at_utc > $now)"
            + " AND ($username IS NULL OR username = $username)"
            + " AND ($state IS NULL OR state = $state)"
            + " AND ($after_created IS NULL OR created_at_utc < $after_created"
            + "      OR (created_at_utc = $after_created AND browse_id > $after_browse_id))"
            + " ORDER BY created_at_utc DESC, browse_id LIMIT $limit;";
        Add(command, "$local_account", localAccount);
        Add(command, "$queued_state", (int)PeerBrowseState.Queued);
        Add(command, "$running_state", (int)PeerBrowseState.Running);
        Add(command, "$now", Format(timeProvider.GetUtcNow()));
        Add(command, "$username", username);
        Add(command, "$state", state is null ? null : (int)state.Value);
        Add(command, "$after_created", afterCreatedAt is null ? null : Format(afterCreatedAt.Value));
        Add(command, "$after_browse_id", afterBrowseId?.ToString("D"));
        Add(command, "$limit", (long)limit + 1);
        var items = new List<PeerBrowseResource>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(ReadResource(reader));
        bool hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        PeerBrowseResource? last = hasMore ? items[^1] : null;
        return new PeerBrowseResourcePage(
            items,
            last?.CreatedAt,
            last?.BrowseId);
    }

    public Task EvictAsync(CancellationToken cancellationToken = default)
        => EvictCoreAsync(preserveBrowseId: null, cancellationToken);

    private async Task EvictCoreAsync(
        Guid? preserveBrowseId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<(
            Guid Id,
            PeerBrowseState State,
            string? ArtifactFile,
            long Bytes,
            DateTimeOffset ExpiresAt)>();
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT browse_id, state, artifact_file, COALESCE(artifact_bytes, 0), expires_at_utc
                FROM browse_resources
                ORDER BY COALESCE(completed_at_utc, created_at_utc), browse_id;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((
                    Guid.Parse(reader.GetString(0)),
                    (PeerBrowseState)reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    Parse(reader.GetString(4))));
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        long totalBytes = 0;
        foreach (var candidate in candidates)
            totalBytes = SaturatingAdd(totalBytes, candidate.Bytes);
        int retainedArtifacts = candidates.Count(static candidate => candidate.ArtifactFile is not null);
        int retainedResources = candidates.Count;
        foreach (var candidate in candidates)
        {
            bool active = candidate.State is PeerBrowseState.Queued or PeerBrowseState.Running;
            bool expired = !active && candidate.ExpiresAt <= now;
            bool overCount = !active
                             && retainedResources > resourceCountTarget
                             && candidate.Id != preserveBrowseId;
            bool overBudget = totalBytes > artifactByteBudget
                              && candidate.ArtifactFile is not null
                              && candidate.Id != preserveBrowseId
                              && retainedArtifacts > 1;
            if (!expired && !overCount && !overBudget)
                continue;
            if (!TryBeginEviction(candidate.Id))
                continue;

            try
            {
                // Remove the registry entry first: a failed registry mutation must
                // never leave a completed resource pointing at a missing artifact.
                await DeleteResourceAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
                retainedResources--;
                if (candidate.ArtifactFile is not null)
                {
                    try
                    {
                        DeleteArtifactFile(candidate.ArtifactFile);
                    }
                    catch (IOException)
                    {
                        // The now-orphaned artifact is retried by startup cleanup.
                        LogCleanupFailure();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // The now-orphaned artifact is retried by startup cleanup.
                        LogCleanupFailure();
                    }
                    totalBytes = Math.Max(0, totalBytes - candidate.Bytes);
                    retainedArtifacts--;
                }
                if (overBudget)
                    LogBudgetEviction();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogCleanupFailure(exception);
            }
            finally
            {
                EndEviction(candidate.Id);
            }
        }
        PeerBrowseTelemetry.UpdateArtifacts(retainedArtifacts, totalBytes);
    }

    private async Task UpdateLifecycleAsync(
        Guid browseId,
        PeerBrowseState state,
        PeerBrowsePhase phase,
        PeerBrowseFailure? failure,
        CancellationToken cancellationToken,
        bool onlyWhileActive = false)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        // Running phases are progress, not a recovery boundary: startup fails
        // both queued and running acquisitions. Acceptance and terminal outcomes
        // remain FULL; losing an intermediate phase cannot change recovery.
        if (state == PeerBrowseState.Running)
            await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken)
                .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE browse_resources
            SET state = $state,
                phase = $phase,
                updated_at_utc = $updated,
                expires_at_utc = CASE WHEN $terminal = 1 THEN $expires ELSE expires_at_utc END,
                failure_code = $failure_code,
                failure_message = $failure_message,
                revision = revision + 1
            WHERE browse_id = $id
              AND ($only_active = 0 OR state IN ($queued, $running));
            """;
        Add(command, "$id", browseId.ToString("D"));
        Add(command, "$state", (int)state);
        Add(command, "$phase", (int)phase);
        Add(command, "$updated", Format(now));
        bool terminal = state is PeerBrowseState.Failed or PeerBrowseState.Cancelled;
        Add(command, "$terminal", terminal ? 1 : 0);
        Add(command, "$expires", Format(now + resourceRetention));
        Add(command, "$failure_code", failure?.Code);
        Add(command, "$failure_message", failure?.Message);
        Add(command, "$only_active", onlyWhileActive ? 1 : 0);
        Add(command, "$queued", (int)PeerBrowseState.Queued);
        Add(command, "$running", (int)PeerBrowseState.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0
            && !onlyWhileActive)
            throw new KeyNotFoundException($"Peer browse '{browseId}' does not exist.");
    }

    internal string GetStagingPath(Guid browseId)
        => Path.Combine(stagingDirectory, browseId.ToString("N") + ".browse.gz.staging");

    internal async Task PromoteAsync(
        PeerBrowseResource resource,
        string stagingPath,
        PeerBrowseIndexProgress progress,
        PeerBrowseArtifact artifact,
        CancellationToken cancellationToken)
    {
        string fileName = resource.BrowseId.ToString("N") + ".browse.gz";
        string finalPath = Path.Combine(artifactDirectory, fileName);
        File.Move(stagingPath, finalPath, overwrite: false);
        OwnerOnlyFilePermissions.EnsureFile(finalPath);
        long artifactBytes = new FileInfo(finalPath).Length;
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Readers require Complete, so install the immutable model before the
        // registry publishes Ready; the first reader must not reload it from disk.
        RememberArtifact(resource.BrowseId, artifact);
        try
        {
            await using SqliteConnection connection = await OpenRegistryAsync(
                SqliteOpenMode.ReadWrite,
                cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE browse_resources
                SET state = $complete,
                    phase = $ready,
                    directory_count = $directories,
                    file_count = $files,
                    total_file_bytes = $total_bytes,
                    updated_at_utc = $completed,
                    completed_at_utc = $completed,
                    expires_at_utc = $expires,
                    failure_code = NULL,
                    failure_message = NULL,
                    artifact_file = $artifact_file,
                    artifact_bytes = $artifact_bytes,
                    revision = revision + 1
                WHERE browse_id = $id AND state = $running;
                """;
            Add(command, "$id", resource.BrowseId.ToString("D"));
            Add(command, "$complete", (int)PeerBrowseState.Complete);
            Add(command, "$ready", (int)PeerBrowsePhase.Ready);
            Add(command, "$running", (int)PeerBrowseState.Running);
            Add(command, "$directories", progress.DirectoryCount);
            Add(command, "$files", progress.FileCount);
            Add(command, "$total_bytes", progress.TotalFileBytes);
            Add(command, "$completed", Format(now));
            Add(command, "$expires", Format(now + resourceRetention));
            Add(command, "$artifact_file", fileName);
            Add(command, "$artifact_bytes", artifactBytes);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new InvalidOperationException("The peer browse was no longer running when its artifact completed.");
        }
        catch
        {
            ForgetArtifact(resource.BrowseId);
            TryDeleteCleanupFile(finalPath);
            throw;
        }

        await EvictCoreAsync(resource.BrowseId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PeerBrowseResource> RequireCompleteAsync(Guid browseId, CancellationToken cancellationToken)
    {
        PeerBrowseResource? resource = await GetAsync(browseId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            throw new KeyNotFoundException($"Peer browse '{browseId}' has expired or does not exist.");
        if (resource.State != PeerBrowseState.Complete)
            throw new InvalidOperationException($"Peer browse '{browseId}' is not complete.");
        return resource;
    }

    internal async ValueTask<ArtifactLease> AcquireLeaseAsync(Guid browseId, CancellationToken cancellationToken = default)
    {
        lock (leaseGate)
        {
            if (evicting.Contains(browseId))
                throw new KeyNotFoundException($"Peer browse '{browseId}' has expired or does not exist.");
            leases[browseId] = leases.GetValueOrDefault(browseId) + 1;
        }

        try
        {
            string? artifactFile = await GetArtifactFileAsync(browseId, cancellationToken).ConfigureAwait(false);
            if (artifactFile is null)
                throw new KeyNotFoundException($"Peer browse '{browseId}' has expired or does not exist.");
            return new ArtifactLease(this, browseId, ResolveArtifactPath(artifactFile));
        }
        catch
        {
            ReleaseLease(browseId);
            throw;
        }
    }

    private void ReleaseLease(Guid browseId)
    {
        lock (leaseGate)
        {
            int count = leases.GetValueOrDefault(browseId);
            if (count <= 1)
                leases.Remove(browseId);
            else
                leases[browseId] = count - 1;
        }
    }

    private bool TryBeginEviction(Guid browseId)
    {
        lock (leaseGate)
        {
            if (leases.ContainsKey(browseId) || !evicting.Add(browseId))
                return false;
            return true;
        }
    }

    private void EndEviction(Guid browseId)
    {
        lock (leaseGate)
            evicting.Remove(browseId);
    }

    private async Task<string?> GetArtifactFileAsync(Guid browseId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_file FROM browse_resources WHERE browse_id = $id;";
        Add(command, "$id", browseId.ToString("D"));
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (string)value;
    }

    private string ResolveArtifactPath(string fileName)
    {
        if (!StringComparer.Ordinal.Equals(fileName, Path.GetFileName(fileName)))
            throw new InvalidDataException("Peer browse registry contains an invalid artifact filename.");
        string fullPath = Path.GetFullPath(Path.Combine(artifactDirectory, fileName));
        string expectedRoot = Path.GetFullPath(artifactDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Peer browse artifact escapes its storage root.");
        return fullPath;
    }

    private async Task DeleteResourceAsync(Guid browseId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM browse_resources WHERE browse_id = $id;";
        Add(command, "$id", browseId.ToString("D"));
        int removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
            PublishResourceRemoved(browseId);
    }

    private void PublishResourceRemoved(Guid browseId)
    {
        ForgetArtifact(browseId);
        Action<Guid>? handlers = ResourceRemoved;
        if (handlers is null)
            return;
        foreach (Action<Guid> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(browseId);
            }
            catch
            {
                // Observers do not own artifact cleanup.
            }
        }
    }

    private void DeleteArtifactFile(string fileName)
    {
        string path = ResolveArtifactPath(fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    private async Task RemoveOrphanArtifactsAsync(CancellationToken cancellationToken)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT artifact_file FROM browse_resources WHERE artifact_file IS NOT NULL;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                retained.Add(reader.GetString(0));
        }

        foreach (string path in Directory.EnumerateFiles(artifactDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!retained.Contains(Path.GetFileName(path)))
                TryDeleteCleanupFile(path);
        }
    }

    private void DeleteFiles(string directory, string pattern)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                TryDeleteCleanupFile(path);
        }
        catch (IOException exception)
        {
            LogCleanupFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogCleanupFailure(exception);
        }
    }

    private void TryDeleteCleanupFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            LogCleanupFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogCleanupFailure(exception);
        }
    }

    internal void DeleteStagingBestEffort(string path)
        => TryDeleteCleanupFile(path);

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (leaseGate)
        {
            if (disposed)
                return;
            disposed = true;
            pending = artifactLoads.Values.Cast<Task>().ToArray();
            loadedArtifacts.Clear();
            artifactRecency.Clear();
            loadedArtifactBytes = 0;
        }
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch { /* Read failures belong to their waiting requests. */ }
        foreach (SqliteOpenMode mode in new[]
                 { SqliteOpenMode.ReadWriteCreate, SqliteOpenMode.ReadWrite, SqliteOpenMode.ReadOnly })
        {
            using var connection = new SqliteConnection(RegistryConnectionString(mode));
            SqliteConnection.ClearPool(connection);
        }
    }

    private string RegistryConnectionString(SqliteOpenMode mode)
        => new SqliteConnectionStringBuilder
        {
            DataSource = registryPath,
            Mode = mode,
            // Keep the mutable registry's WAL open between operations. Closing
            // its last connection otherwise checkpoints and recreates the WAL
            // on subsequent progress writes. Browse artifacts use ordinary files.
            Pooling = true,
        }.ToString();

    private async ValueTask<SqliteConnection> OpenRegistryAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var connection = new SqliteConnection(RegistryConnectionString(mode));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        // A pooled connection may have last served a best-effort progress write.
        // Restore the durable default at the owner boundary for every operation.
        pragma.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<PeerBrowseResource?> ReadResourceAsync(
        SqliteConnection connection,
        Guid browseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ResourceSelect + " WHERE browse_id = $id;";
        Add(command, "$id", browseId.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadResource(reader)
            : null;
    }

    private static PeerBrowseResource ReadResource(SqliteDataReader reader)
    {
        string? failureCode = reader.IsDBNull(16) ? null : reader.GetString(16);
        return new PeerBrowseResource(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            (PeerBrowseState)reader.GetInt32(3),
            (PeerBrowsePhase)reader.GetInt32(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            Parse(reader.GetString(10)),
            Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : Parse(reader.GetString(12)),
            Parse(reader.GetString(13)),
            failureCode is null
                ? null
                : new PeerBrowseFailure(failureCode, reader.IsDBNull(17) ? "" : reader.GetString(17)),
            reader.GetInt64(18));
    }

    private const string ResourceSelect =
        """
        SELECT browse_id, local_account, username, state, phase,
               compressed_bytes_received, compressed_bytes_expected,
               directory_count, file_count,
               total_file_bytes, created_at_utc, updated_at_utc,
               completed_at_utc, expires_at_utc, artifact_file, artifact_bytes,
               failure_code, failure_message, revision
        FROM browse_resources
        """;

    private static void ValidatePage(string? afterSortKey, long? afterId, int limit)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        if ((afterSortKey is null) != (afterId is null))
            throw new ArgumentException("Both cursor sort key and ID must be supplied together.");
    }

    private static void ValidateSearchPage(
        string? afterSortKey,
        PeerBrowseSearchEntryKind? afterKind,
        long? afterId,
        int limit)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        bool hasCursor = afterSortKey is not null || afterKind is not null || afterId is not null;
        if (hasCursor && (afterSortKey is null || afterKind is null || afterId is null or <= 0))
            throw new ArgumentException("All mixed-search cursor values must be supplied together.");
    }

    private static void ValidateResourcePage(
        DateTimeOffset? afterCreatedAt,
        Guid? afterBrowseId,
        int limit)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        if ((afterCreatedAt is null) != (afterBrowseId is null))
            throw new ArgumentException("Both resource cursor values must be supplied together.");
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

    internal static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    internal static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private void LogBudgetEviction()
    {
        if (!TryLog(ref lastBudgetLogTick))
            return;
        PersistenceLogMessages.BrowseArtifactEvicted(logger);
    }

    private void LogCleanupFailure(Exception? exception = null)
    {
        if (!TryLog(ref lastCleanupLogTick))
            return;
        if (exception is null)
            PersistenceLogMessages.BrowseCleanupFailed(logger);
        else
            PersistenceLogMessages.BrowseCleanupFailed(logger, exception);
    }

    private static bool TryLog(ref long previous)
    {
        long now = Environment.TickCount64;
        long observed = Volatile.Read(ref previous);
        if (observed != long.MinValue && now - observed < 60_000)
            return false;
        return Interlocked.CompareExchange(ref previous, now, observed) == observed;
    }

    public sealed class ArtifactLease : IAsyncDisposable
    {
        private PeerBrowseArtifactStore? owner;

        internal ArtifactLease(PeerBrowseArtifactStore owner, Guid browseId, string artifactPath)
        {
            this.owner = owner;
            BrowseId = browseId;
            ArtifactPath = artifactPath;
        }

        public Guid BrowseId { get; }
        internal string ArtifactPath { get; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref owner, null)?.ReleaseLease(BrowseId);
            return ValueTask.CompletedTask;
        }
    }
}
