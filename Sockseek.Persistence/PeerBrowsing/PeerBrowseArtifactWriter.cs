using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Sockseek.Core.IO;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.PeerBrowsing;

/// <summary>Streams one browse generation into its private staging database.</summary>
public sealed class PeerBrowseArtifactWriter : IPeerBrowseRowSink
{
    private readonly PeerBrowseArtifactStore store;
    private readonly PeerBrowseResource resource;
    private readonly string stagingPath;
    private SqliteConnection? connection;
    private SqliteTransaction? transaction;
    private long directoryCount;
    private long nextDirectoryId;
    private long fileCount;
    private long totalFileBytes;
    private long? currentDirectoryId;
    private string? currentDirectoryIdentity;
    private string? currentDirectoryWirePath;
    private PeerShareVisibility currentDirectoryVisibility;
    private long? currentFileId;
    private PeerBrowseWireFile? currentFile;
    private PeerBrowseFilePath currentFilePath;
    private string? currentFileExtension;
    private readonly ArrayBufferWriter<byte> currentAttributes = new();
    private int expectedAttributeCount;
    private int actualAttributeCount;
    private SqliteWriteStatement? insertFileStatement;
    private bool completed;
    private bool disposed;

    private PeerBrowseArtifactWriter(
        PeerBrowseArtifactStore store,
        PeerBrowseResource resource,
        string stagingPath,
        SqliteConnection connection)
    {
        this.store = store;
        this.resource = resource;
        this.stagingPath = stagingPath;
        this.connection = connection;
    }

    internal static async ValueTask<PeerBrowseArtifactWriter> CreateAsync(
        PeerBrowseArtifactStore store,
        PeerBrowseResource resource,
        CancellationToken cancellationToken)
    {
        string stagingPath = store.GetStagingPath(resource.BrowseId);
        if (File.Exists(stagingPath))
            File.Delete(stagingPath);
        await using (new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }
        OwnerOnlyFilePermissions.EnsureFile(stagingPath);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = stagingPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            connection.CreateAggregate<long, long>(
                "saturating_sum",
                static (total, value) => SaturatingAdd(total, value),
                isDeterministic: true);
            await ExecuteAsync(
                connection,
                """
                PRAGMA foreign_keys=ON;
                -- This database is private staging until its final durable commit.
                -- A crash before promotion discards it during startup cleanup.
                PRAGMA journal_mode=OFF;
                PRAGMA synchronous=OFF;
                PRAGMA temp_store=FILE;
                PRAGMA busy_timeout=5000;

                CREATE TABLE artifact_metadata (
                    schema_version INTEGER NOT NULL,
                    browse_id TEXT NOT NULL,
                    local_account TEXT NOT NULL,
                    username TEXT NOT NULL,
                    completed_at_utc TEXT NOT NULL,
                    wire_directory_count INTEGER NOT NULL,
                    file_count INTEGER NOT NULL,
                    total_file_bytes INTEGER NOT NULL,
                    completion_marker INTEGER NOT NULL
                );

                CREATE TABLE directories (
                    directory_id INTEGER PRIMARY KEY,
                    parent_id INTEGER,
                    identity_path TEXT NOT NULL UNIQUE COLLATE BINARY,
                    wire_path TEXT NOT NULL,
                    name TEXT NOT NULL,
                    display_path TEXT NOT NULL,
                    search_path TEXT NOT NULL,
                    visibility INTEGER NOT NULL,
                    is_synthetic INTEGER NOT NULL,
                    direct_directory_count INTEGER NOT NULL DEFAULT 0,
                    direct_file_count INTEGER NOT NULL DEFAULT 0,
                    recursive_file_count INTEGER NOT NULL DEFAULT 0,
                    recursive_file_bytes INTEGER NOT NULL DEFAULT 0,
                    total_recursive_file_count INTEGER NOT NULL DEFAULT 0,
                    total_recursive_file_bytes INTEGER NOT NULL DEFAULT 0,
                    locked_descendant_count INTEGER NOT NULL DEFAULT 0,
                    has_children INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (parent_id) REFERENCES directories(directory_id)
                );

                CREATE TABLE files (
                    file_id INTEGER PRIMARY KEY,
                    directory_id INTEGER NOT NULL,
                    visibility INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    search_name TEXT NOT NULL,
                    identity_name TEXT NOT NULL COLLATE BINARY,
                    wire_filename TEXT NOT NULL COLLATE BINARY,
                    size_bytes INTEGER NOT NULL,
                    extension TEXT,
                    protocol_code INTEGER NOT NULL,
                    attributes BLOB,
                    FOREIGN KEY (directory_id) REFERENCES directories(directory_id)
                );

                CREATE INDEX idx_directories_parent_name
                    ON directories(parent_id, name, directory_id);
                CREATE INDEX idx_directories_display_path
                    ON directories(display_path, directory_id);

                -- Each directory/file owns its folded search key. Mixed search
                -- composes paths from these canonical rows instead of building
                -- another copy of every full display path for every browse.
                """,
                cancellationToken).ConfigureAwait(false);
            return new PeerBrowseArtifactWriter(store, resource, stagingPath, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            store.DeleteStagingBestEffort(stagingPath);
            throw;
        }
    }

    public async ValueTask BeginDirectoryAsync(
        string wirePath,
        PeerShareVisibility visibility,
        int fileCount,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (currentFileId is not null)
            throw Invalid("a new directory began before the previous file ended");
        if (fileCount < 0)
            throw Invalid("directory file count cannot be negative");

        string identityPath = PeerBrowsePath.NormalizeDirectoryIdentity(wirePath);
        string[] components = identityPath.Split('\\');
        long? parentId = null;
        string prefix = "";
        for (int index = 0; index < components.Length; index++)
        {
            prefix = index == 0 ? components[index] : prefix + "\\" + components[index];
            bool final = index == components.Length - 1;
            parentId = await GetOrCreateDirectoryAsync(
                prefix,
                final ? wirePath : prefix,
                parentId,
                components[index],
                visibility,
                isSynthetic: !final,
                cancellationToken).ConfigureAwait(false);
        }

        currentDirectoryId = parentId;
        currentDirectoryIdentity = identityPath;
        currentDirectoryWirePath = wirePath;
        currentDirectoryVisibility = visibility;
        directoryCount = checked(directoryCount + 1);
    }

    public ValueTask BeginFileAsync(
        PeerBrowseWireFile file,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(file);
        EnsureWritable();
        if (currentDirectoryId is null
            || currentDirectoryIdentity is null
            || currentDirectoryWirePath is null)
            throw Invalid("a file appeared before its directory");
        if (currentFileId is not null)
            throw Invalid("a new file began before the previous file ended");
        if (file.Code is < byte.MinValue or > byte.MaxValue)
            throw Invalid("file code is outside the protocol byte range");
        if (file.Size < 0 || file.AttributeCount < 0)
            throw Invalid("file size or attribute count cannot be negative");
        currentFilePath = PeerBrowsePath.ResolveFile(
            currentDirectoryIdentity,
            currentDirectoryWirePath,
            file.Filename);
        currentFileExtension = file.Extension.Length == 0
            ? null
            : Core.Models.PeerIdentityValidator.ValidateRemotePath(file.Extension);
        currentFileId = checked(fileCount + 1);
        currentFile = file;
        currentAttributes.Clear();
        expectedAttributeCount = file.AttributeCount;
        actualAttributeCount = 0;
        fileCount = checked(fileCount + 1);
        totalFileBytes = SaturatingAdd(totalFileBytes, file.Size);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddAttributeAsync(
        PeerBrowseWireAttribute attribute,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();
        if (currentFileId is null)
            throw Invalid("a file attribute appeared outside a file");
        if (actualAttributeCount >= expectedAttributeCount)
            throw Invalid("a file contained more attributes than declared");

        PeerBrowseAttributes.Append(currentAttributes, attribute);
        actualAttributeCount++;

        return ValueTask.CompletedTask;
    }

    public ValueTask EndFileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();
        if (currentFileId is null)
            throw Invalid("a file ended before it began");
        if (actualAttributeCount != expectedAttributeCount)
            throw Invalid("a file contained fewer attributes than declared");

        InsertCurrentFile(cancellationToken);
        currentFileId = null;
        currentFile = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (currentFileId is not null)
            throw Invalid("the response ended before the current file ended");
        await CommitBatchAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteAsync(
                Connection,
                """
                CREATE UNIQUE INDEX idx_files_directory_identity_name
                    ON files(directory_id, identity_name COLLATE BINARY);
                CREATE INDEX idx_files_directory_name
                    ON files(directory_id, name, file_id);
                """,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw Invalid("the response contains a duplicate path identity", ex);
        }

        string sumBytes = PeerBrowseArtifactStore.SumFunction(totalFileBytes);
        await ExecuteAsync(
            Connection,
            $"""
            UPDATE directories AS target
            SET direct_directory_count = (
                    SELECT COUNT(*) FROM directories child
                    WHERE child.parent_id = target.directory_id),
                direct_file_count = (
                    SELECT COUNT(*) FROM files direct_file
                    WHERE direct_file.directory_id = target.directory_id),
                has_children = CASE WHEN
                    EXISTS(SELECT 1 FROM directories child WHERE child.parent_id = target.directory_id)
                    OR EXISTS(SELECT 1 FROM files direct_file WHERE direct_file.directory_id = target.directory_id)
                    THEN 1 ELSE 0 END;

            WITH RECURSIVE ancestry(descendant_id, ancestor_id) AS (
                SELECT directory_id, directory_id FROM directories
                UNION ALL
                SELECT ancestry.descendant_id, parent.parent_id
                FROM ancestry
                JOIN directories parent ON parent.directory_id = ancestry.ancestor_id
                WHERE parent.parent_id IS NOT NULL
            ),
            file_totals AS MATERIALIZED (
                SELECT ancestry.ancestor_id,
                       COUNT(*) AS all_count,
                       COALESCE({sumBytes}(subtree_file.size_bytes), 0) AS all_bytes,
                       COUNT(CASE WHEN subtree_file.visibility = 0 THEN 1 END) AS public_count,
                       COALESCE({sumBytes}(CASE WHEN subtree_file.visibility = 0
                           THEN subtree_file.size_bytes ELSE 0 END), 0) AS public_bytes
                FROM ancestry
                JOIN files subtree_file ON subtree_file.directory_id = ancestry.descendant_id
                GROUP BY ancestry.ancestor_id
            ),
            locked_directory_totals AS MATERIALIZED (
                SELECT ancestry.ancestor_id, COUNT(*) AS locked_count
                FROM ancestry
                JOIN directories descendant ON descendant.directory_id = ancestry.descendant_id
                WHERE ancestry.descendant_id != ancestry.ancestor_id
                  AND descendant.visibility = 1
                GROUP BY ancestry.ancestor_id
            )
            UPDATE directories AS target
            SET recursive_file_count = COALESCE((SELECT public_count FROM file_totals
                    WHERE ancestor_id = target.directory_id), 0),
                recursive_file_bytes = COALESCE((SELECT public_bytes FROM file_totals
                    WHERE ancestor_id = target.directory_id), 0),
                total_recursive_file_count = COALESCE((SELECT all_count FROM file_totals
                    WHERE ancestor_id = target.directory_id), 0),
                total_recursive_file_bytes = COALESCE((SELECT all_bytes FROM file_totals
                    WHERE ancestor_id = target.directory_id), 0),
                locked_descendant_count = COALESCE((SELECT locked_count FROM locked_directory_totals
                    WHERE ancestor_id = target.directory_id), 0);

            WITH RECURSIVE ancestry(descendant_id, ancestor_id) AS (
                SELECT directory_id, directory_id FROM directories
                UNION ALL
                SELECT ancestry.descendant_id, parent.parent_id
                FROM ancestry
                JOIN directories parent ON parent.directory_id = ancestry.ancestor_id
                WHERE parent.parent_id IS NOT NULL
            )
            UPDATE directories AS target
            SET visibility = CASE
                WHEN EXISTS(
                    SELECT 1 FROM ancestry
                    JOIN directories descendant ON descendant.directory_id = ancestry.descendant_id
                    WHERE ancestry.ancestor_id = target.directory_id AND descendant.visibility = 0)
                 AND EXISTS(
                    SELECT 1 FROM ancestry
                    JOIN directories descendant ON descendant.directory_id = ancestry.descendant_id
                    WHERE ancestry.ancestor_id = target.directory_id AND descendant.visibility = 1)
                    THEN 2
                WHEN EXISTS(
                    SELECT 1 FROM ancestry
                    JOIN directories descendant ON descendant.directory_id = ancestry.descendant_id
                    WHERE ancestry.ancestor_id = target.directory_id AND descendant.visibility = 1)
                    THEN 1
                ELSE 0
            END;


            """,
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        await using (var metadata = Connection.CreateCommand())
        {
            metadata.CommandText =
                """
                INSERT INTO artifact_metadata(
                    schema_version, browse_id, local_account, username,
                    completed_at_utc, wire_directory_count, file_count,
                    total_file_bytes, completion_marker)
                VALUES($format, $id, $account, $username, $completed,
                       $directories, $files, $bytes, 0);
                """;
            PeerBrowseArtifactStore.Add(metadata, "$format", PeerBrowseArtifactStore.ArtifactFormatVersion);
            PeerBrowseArtifactStore.Add(metadata, "$id", resource.BrowseId.ToString("D"));
            PeerBrowseArtifactStore.Add(metadata, "$account", resource.LocalAccount);
            PeerBrowseArtifactStore.Add(metadata, "$username", resource.Username);
            PeerBrowseArtifactStore.Add(metadata, "$completed", PeerBrowseArtifactStore.Format(completedAt));
            PeerBrowseArtifactStore.Add(metadata, "$directories", directoryCount);
            PeerBrowseArtifactStore.Add(metadata, "$files", fileCount);
            PeerBrowseArtifactStore.Add(metadata, "$bytes", totalFileBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(Connection, "PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
        await using (var check = Connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            object? result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Peer browse artifact quick_check failed: {result}");
        }

        // The load above is disposable staging work. Make the validated result
        // durable once, before atomic promotion, instead of fsyncing hundreds of
        // intermediate batches that startup would discard after a crash anyway.
        await ExecuteAsync(
            Connection,
            """
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            BEGIN IMMEDIATE;
            UPDATE artifact_metadata SET completion_marker = 1;
            COMMIT;
            """,
            cancellationToken).ConfigureAwait(false);

        await Connection.DisposeAsync().ConfigureAwait(false);
        connection = null;
        await store.PromoteAsync(
            resource,
            stagingPath,
            new PeerBrowseIndexProgress(directoryCount, fileCount, totalFileBytes),
            cancellationToken).ConfigureAwait(false);
        completed = true;
    }

    private async ValueTask<long> GetOrCreateDirectoryAsync(
        string identityPath,
        string wirePath,
        long? parentId,
        string name,
        PeerShareVisibility visibility,
        bool isSynthetic,
        CancellationToken cancellationToken)
    {
        transaction ??= (SqliteTransaction)await Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var query = Connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT directory_id, is_synthetic, visibility FROM directories WHERE identity_path = $path;";
            PeerBrowseArtifactStore.Add(query, "$path", identityPath);
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long id = reader.GetInt64(0);
                bool existingSynthetic = reader.GetInt32(1) != 0;
                int existingVisibility = reader.GetInt32(2);
                await reader.DisposeAsync().ConfigureAwait(false);
                if (!isSynthetic && !existingSynthetic)
                    throw Invalid("the response contains a duplicate directory identity");

                int nextVisibility = isSynthetic && existingVisibility != (int)visibility
                    ? (int)PeerBrowseEntryVisibility.Mixed
                    : (int)visibility;
                if (!isSynthetic || existingSynthetic)
                {
                    await ExecuteWriteAsync(
                        """
                        UPDATE directories
                        SET wire_path = $wire_path,
                            name = $name,
                            display_path = $display_path,
                            search_path = $search_path,
                            visibility = $visibility,
                            is_synthetic = $synthetic
                        WHERE directory_id = $id;
                        """,
                        [
                            ("$id", id),
                            ("$wire_path", wirePath),
                            ("$name", Core.Models.PeerIdentityValidator.ToDisplayText(name)),
                            ("$display_path", PeerBrowseArtifactStore.DisplayPath(identityPath)),
                            ("$search_path", PeerBrowseArtifactStore.FoldSearchPath(PeerBrowseArtifactStore.DisplayPath(identityPath))),
                            ("$visibility", nextVisibility),
                            ("$synthetic", isSynthetic ? 1 : 0),
                        ],
                        cancellationToken).ConfigureAwait(false);
                }
                return id;
            }
        }

        long createdId = checked(++nextDirectoryId);
        await ExecuteWriteAsync(
            """
            INSERT INTO directories(
                directory_id, parent_id, identity_path, wire_path, name, display_path, search_path,
                visibility, is_synthetic)
            VALUES($directory_id, $parent_id, $identity_path, $wire_path, $name, $display_path, $search_path,
                   $visibility, $synthetic);
            """,
            [
                ("$directory_id", createdId),
                ("$parent_id", parentId),
                ("$identity_path", identityPath),
                ("$wire_path", wirePath),
                ("$name", Core.Models.PeerIdentityValidator.ToDisplayText(name)),
                ("$display_path", PeerBrowseArtifactStore.DisplayPath(identityPath)),
                ("$search_path", PeerBrowseArtifactStore.FoldSearchPath(PeerBrowseArtifactStore.DisplayPath(identityPath))),
                ("$visibility", (int)visibility),
                ("$synthetic", isSynthetic ? 1 : 0),
            ],
            cancellationToken).ConfigureAwait(false);
        return createdId;
    }

    private void InsertCurrentFile(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer = insertFileStatement ??= new SqliteWriteStatement(Connection, EnsureTransaction(),
            """
            INSERT INTO files(
                file_id, directory_id, visibility, name, search_name, identity_name, wire_filename,
                size_bytes, extension, protocol_code, attributes)
            VALUES($file_id, $directory_id, $visibility, $name, $search_name, $identity_name,
                   $wire_filename, $size, $extension, $code, $attributes);
            """);
        PeerBrowseWireFile file = currentFile!;
        writer.Bind(0, currentFileId!.Value);
        writer.Bind(1, currentDirectoryId!.Value);
        writer.Bind(2, (int)currentDirectoryVisibility);
        string displayName = Core.Models.PeerIdentityValidator.ToDisplayText(currentFilePath.LeafName);
        writer.Bind(3, displayName);
        writer.Bind(4, PeerBrowseArtifactStore.FoldSearchPath(displayName));
        // The directory owns the parent identity. Index the exact raw leaf,
        // not another full-path copy; wire spelling remains independently exact.
        writer.Bind(5, currentFilePath.LeafName);
        writer.Bind(6, currentFilePath.WireFilename);
        writer.Bind(7, file.Size);
        writer.Bind(8, currentFileExtension);
        writer.Bind(9, file.Code);
        writer.Bind(10, currentAttributes.WrittenCount == 0 ? null : currentAttributes.WrittenSpan.ToArray());
        ExecutePrepared(writer, cancellationToken);
    }

    private SqliteTransaction EnsureTransaction()
    {
        transaction ??= Connection.BeginTransaction();
        return transaction;
    }

    private static void ExecutePrepared(SqliteWriteStatement statement, CancellationToken cancellationToken)
    {
        try
        {
            statement.Execute(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw Invalid("the response contains a duplicate path identity", ex);
        }
    }

    private async ValueTask ExecuteWriteAsync(
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
    {
        transaction ??= (SqliteTransaction)await Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            PeerBrowseArtifactStore.Add(command, name, value);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw Invalid("the response contains a duplicate path identity", ex);
        }

    }

    private async ValueTask CommitBatchAsync(CancellationToken cancellationToken)
    {
        if (transaction is null)
            return;
        DisposePreparedStatements();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        transaction = null;
    }

    private void DisposePreparedStatements()
    {
        insertFileStatement?.Dispose();
        insertFileStatement = null;
    }

    private SqliteConnection Connection
        => connection ?? throw new ObjectDisposedException(nameof(PeerBrowseArtifactWriter));

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
            throw new InvalidOperationException("The peer browse artifact is already complete.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        DisposePreparedStatements();
        if (transaction is not null)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            transaction = null;
        }
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            connection = null;
        }
        if (!completed)
            store.DeleteStagingBestEffort(stagingPath);
    }

    private static PeerBrowseProtocolException Invalid(string detail, Exception? inner = null)
        => new($"The peer returned an invalid browse response: {detail}.", inner);

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

// Filtering uses paths, sizes and visibility, not attribute-derived metadata.
// Store ordered little-endian int pairs with the file instead
// of one indexed child row per attribute. Unknown/duplicate codes and signed
// values survive exactly; the artifact format version governs both sides.
internal static class PeerBrowseAttributes
{
    private const int PairBytes = 2 * sizeof(int);

    public static void Append(ArrayBufferWriter<byte> buffer, PeerBrowseWireAttribute attribute)
    {
        Span<byte> pair = buffer.GetSpan(PairBytes);
        BinaryPrimitives.WriteInt32LittleEndian(pair, attribute.Type);
        BinaryPrimitives.WriteInt32LittleEndian(pair[sizeof(int)..], attribute.Value);
        buffer.Advance(PairBytes);
    }

    public static PeerBrowseFileMetadata ReadMetadata(SqliteDataReader reader, int ordinal)
        => Read<object>(reader, ordinal, create: null).Metadata;

    public static (PeerBrowseFileMetadata Metadata, List<T>? Attributes) Read<T>(
        SqliteDataReader reader, int ordinal, Func<int, int, T>? create)
    {
        if (reader.IsDBNull(ordinal)) return default;
        byte[] bytes = reader.GetFieldValue<byte[]>(ordinal);
        if (bytes.Length % PairBytes != 0)
            throw new InvalidDataException("The peer browse artifact contains malformed file attributes.");
        PeerBrowseFileMetadata metadata = default;
        List<T>? attributes = create is not null && bytes.Length > 0 ? new(bytes.Length / PairBytes) : null;
        for (int offset = 0; offset < bytes.Length; offset += PairBytes)
        {
            int type = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            int value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + sizeof(int)));
            metadata = metadata.WithAttribute(type, value);
            if (create is not null) attributes!.Add(create(type, value));
        }
        return (metadata, attributes);
    }
}
