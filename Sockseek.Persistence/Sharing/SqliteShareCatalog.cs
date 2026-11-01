using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Sockseek.Core.IO;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Sharing;

public sealed class RemotePathCollisionException(string message, Exception innerException)
    : ShareCatalogEntryCollisionException(message, innerException);

/// <summary>
/// Builds one unpublished generation. Intermediate batches retain rollback
/// semantics; CompleteAsync validates and makes the result durable before the
/// catalog manager can publish it. An interrupted build is discarded.
/// </summary>
public sealed class SqliteShareCatalogBuilder : IShareCatalogGenerationWriter
{
    private const int BatchSize = 1_000;

    private readonly SqliteConnection connection;
    private WriteBatch? batch;
    private SqliteTransaction? transaction;
    private int pendingWrites;
    private bool searchIndexDirty;
    private bool completed;
    private bool disposed;

    private SqliteShareCatalogBuilder(string databasePath, SqliteConnection connection)
    {
        DatabasePath = databasePath;
        this.connection = connection;
    }

    public string DatabasePath { get; }

    public static async ValueTask<SqliteShareCatalogBuilder> CreateAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (!File.Exists(fullPath))
        {
            using (new FileStream(
                       fullPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
            }
        }
        OwnerOnlyFilePermissions.EnsureFile(fullPath);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                PRAGMA foreign_keys=ON;
                -- Keep per-entry/batch rollback while avoiding durable writes
                -- for an unpublished generation that cannot be recovered.
                PRAGMA journal_mode=MEMORY;
                PRAGMA synchronous=OFF;
                PRAGMA temp_store=FILE;
                PRAGMA busy_timeout=5000;

                CREATE TABLE catalog_metadata (
                    schema_version INTEGER NOT NULL,
                    generation_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    settings_hash TEXT NOT NULL,
                    directory_count INTEGER NOT NULL,
                    file_count INTEGER NOT NULL,
                    total_bytes INTEGER NOT NULL,
                    browse_status TEXT NOT NULL,
                    browse_wire_version INTEGER,
                    browse_length_bytes INTEGER,
                    browse_sha256 TEXT
                );

                CREATE TABLE roots (
                    root_id INTEGER PRIMARY KEY,
                    alias TEXT NOT NULL,
                    local_path TEXT NOT NULL,
                    comparison_alias BLOB NOT NULL UNIQUE
                );

                CREATE TABLE path_identities (
                    comparison_path BLOB PRIMARY KEY,
                    kind TEXT NOT NULL
                ) WITHOUT ROWID;

                CREATE TABLE directories (
                    directory_id INTEGER PRIMARY KEY,
                    root_id INTEGER NOT NULL,
                    relative_path TEXT NOT NULL,
                    remote_path TEXT NOT NULL,
                    comparison_path BLOB NOT NULL UNIQUE,
                    FOREIGN KEY (root_id) REFERENCES roots(root_id)
                );

                CREATE TABLE files (
                    file_id INTEGER PRIMARY KEY,
                    root_id INTEGER NOT NULL,
                    directory_id INTEGER NOT NULL,
                    relative_path TEXT NOT NULL,
                    remote_path TEXT NOT NULL,
                    comparison_path BLOB NOT NULL UNIQUE,
                    search_text TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_at_utc TEXT NOT NULL,
                    protocol_code INTEGER NOT NULL,
                    extension TEXT NOT NULL,
                    attributes_json TEXT NOT NULL,
                    FOREIGN KEY (root_id) REFERENCES roots(root_id),
                    FOREIGN KEY (directory_id) REFERENCES directories(directory_id)
                );

                CREATE INDEX idx_directories_remote_path
                    ON directories(remote_path);
                CREATE INDEX idx_files_directory
                    ON files(directory_id, remote_path);
                CREATE INDEX idx_files_root_relative
                    ON files(root_id, relative_path);

                -- File facts live only in files. The contentless FTS index owns
                -- tokens and aligned row IDs, so matching never reads a second
                -- content table merely to find the authoritative file row.
                CREATE VIRTUAL TABLE file_search USING fts5(
                    search_text,
                    content='',
                    tokenize='unicode61 remove_diacritics 2'
                );
                """,
                cancellationToken).ConfigureAwait(false);

            return new SqliteShareCatalogBuilder(fullPath, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask AddRootAsync(
        ShareCatalogRoot root,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        (await GetBatchAsync(cancellationToken).ConfigureAwait(false)).AddRoot(root, cancellationToken);
        await WrittenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddDirectoryAsync(
        ShareCatalogDirectory directory,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        try
        {
            (await GetBatchAsync(cancellationToken).ConfigureAwait(false)).AddDirectory(directory, cancellationToken);
            await WrittenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new RemotePathCollisionException(
                $"Remote directory path collision at '{directory.RemotePath}'.", ex);
        }
    }

    public async ValueTask AddFileAsync(
        ShareCatalogFile file,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        try
        {
            (await GetBatchAsync(cancellationToken).ConfigureAwait(false)).AddFile(file, cancellationToken);
            searchIndexDirty = true;
            await WrittenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new RemotePathCollisionException(
                $"Remote file path collision at '{file.RemotePath}'.", ex);
        }
    }

    public async ValueTask CompleteAsync(
        ShareCatalogMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await PrepareForReadAsync(cancellationToken).ConfigureAwait(false);

        // All scan/index changes are still private. Flush them with the final
        // metadata commit, using the same durable-publication boundary as browse
        // artifacts. Runtime history keeps its independent FULL WAL policy.
        await ExecuteNonQueryAsync(connection, """
            PRAGMA optimize;
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            """, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO catalog_metadata(
                    schema_version, generation_id,
                    created_at_utc, settings_hash, directory_count, file_count,
                    total_bytes, browse_status, browse_wire_version,
                    browse_length_bytes, browse_sha256)
                VALUES(
                    $schema_version, $generation_id,
                    $created_at_utc, $settings_hash, $directory_count, $file_count,
                    $total_bytes, $browse_status, $browse_wire_version,
                    $browse_length_bytes, $browse_sha256);
                """;
            Add(command, "$schema_version", ShareCatalogVersions.Schema);
            Add(command, "$generation_id", metadata.GenerationId.ToString("D"));
            Add(command, "$created_at_utc", Format(metadata.CreatedAtUtc));
            Add(command, "$settings_hash", metadata.SettingsHash);
            Add(command, "$directory_count", metadata.DirectoryCount);
            Add(command, "$file_count", metadata.FileCount);
            Add(command, "$total_bytes", metadata.TotalBytes);
            Add(command, "$browse_status", metadata.BrowseStatus.ToString());
            Add(command, "$browse_wire_version", metadata.BrowseWireVersion);
            Add(command, "$browse_length_bytes", metadata.BrowseLengthBytes);
            Add(command, "$browse_sha256", metadata.BrowseSha256);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        object? result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Share catalog quick_check failed: {result}");

        completed = true;
    }

    public async ValueTask PrepareForReadAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await CommitBatchAsync(cancellationToken).ConfigureAwait(false);
        if (searchIndexDirty)
        {
            // A generation is private while scanning. Build its search index at
            // the read boundary in one transaction, rather than creating FTS
            // segments and merging them in every file-write batch. Explicit
            // row IDs also let later staging reads index only newly added files,
            // even when callers supply file IDs out of order.
            await using var indexing = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = indexing;
            command.CommandText = """
                INSERT INTO file_search(rowid, search_text)
                SELECT file.file_id, file.search_text
                FROM files file
                WHERE NOT EXISTS (SELECT 1 FROM file_search existing WHERE existing.rowid = file.file_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await indexing.CommitAsync(cancellationToken).ConfigureAwait(false);
            searchIndexDirty = false;
        }
    }

    private async ValueTask<WriteBatch> GetBatchAsync(CancellationToken cancellationToken)
    {
        transaction ??= (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return batch ??= new WriteBatch(connection, transaction);
    }

    private async ValueTask WrittenAsync(CancellationToken cancellationToken)
    {
        if (++pendingWrites >= BatchSize)
            await CommitBatchAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CommitBatchAsync(CancellationToken cancellationToken)
    {
        if (transaction is null)
            return;

        batch?.Dispose();
        batch = null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        transaction = null;
        pendingWrites = 0;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
            throw new InvalidOperationException("Share catalog generation is already complete.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        batch?.Dispose();
        batch = null;

        if (transaction is not null)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearPool(connection);
    }

    // A transaction owns its prepared statements. Bind each row once by ordinal;
    // a provider command containing multiple INSERTs rebinds all parameters for
    // every statement, including parameters that statement does not use.
    private sealed class WriteBatch(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        private SqliteWriteStatement? roots;
        private SqliteWriteStatement? identities;
        private SqliteWriteStatement? directories;
        private SqliteWriteStatement? files;

        public void AddRoot(ShareCatalogRoot root, CancellationToken cancellationToken)
        {
            var writer = roots ??= new(connection, transaction, """
                INSERT INTO roots(root_id, alias, local_path, comparison_alias)
                VALUES($id, $alias, $path, $comparison);
                """);
            writer.Bind(0, root.RootId);
            writer.Bind(1, root.Alias);
            writer.Bind(2, root.LocalPath);
            writer.Bind(3, root.ComparisonAlias.ToArray());
            writer.Execute(cancellationToken);
        }

        private void AddIdentity(byte[] path, string kind, CancellationToken cancellationToken)
        {
            var writer = identities ??= new(connection, transaction, """
                INSERT INTO path_identities(comparison_path, kind) VALUES($path, $kind);
                """);
            writer.Bind(0, path);
            writer.Bind(1, kind);
            writer.Execute(cancellationToken);
        }

        public void AddDirectory(ShareCatalogDirectory directory, CancellationToken cancellationToken)
        {
            byte[] path = directory.ComparisonPath.ToArray();
            AddIdentity(path, "directory", cancellationToken);
            var writer = directories ??= new(connection, transaction, """
                INSERT INTO directories(directory_id, root_id, relative_path, remote_path, comparison_path)
                VALUES($id, $root, $relative, $remote, $comparison);
                """);
            writer.Bind(0, directory.DirectoryId);
            writer.Bind(1, directory.RootId);
            writer.Bind(2, directory.RelativePath);
            writer.Bind(3, directory.RemotePath);
            writer.Bind(4, path);
            writer.Execute(cancellationToken);
        }

        public void AddFile(ShareCatalogFile file, CancellationToken cancellationToken)
        {
            byte[] path = file.ComparisonPath.ToArray();
            AddIdentity(path, "file", cancellationToken);
            var writer = files ??= new(connection, transaction, """
                INSERT INTO files(file_id, root_id, directory_id, relative_path, remote_path,
                    comparison_path, search_text, size_bytes, modified_at_utc, protocol_code, extension, attributes_json)
                VALUES($id, $root, $directory, $relative, $remote, $comparison,
                    $search, $size, $modified, $code, $extension, $attributes);
                """);
            writer.Bind(0, file.FileId);
            writer.Bind(1, file.RootId);
            writer.Bind(2, file.DirectoryId);
            writer.Bind(3, file.RelativePath);
            writer.Bind(4, file.RemotePath);
            writer.Bind(5, path);
            writer.Bind(6, file.SearchText);
            writer.Bind(7, file.SizeBytes);
            writer.Bind(8, Format(file.ModifiedAtUtc));
            writer.Bind(9, (long)file.ProtocolCode);
            writer.Bind(10, file.Extension);
            writer.Bind(11, JsonSerializer.Serialize(file.Attributes));
            writer.Execute(cancellationToken);
        }

        public void Dispose()
        {
            roots?.Dispose();
            identities?.Dispose();
            directories?.Dispose();
            files?.Dispose();
        }
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async ValueTask ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed partial class SqliteShareCatalogReader : IShareCatalogReader
{
    // Protocol responses remain capped at 500. The larger internal bound is
    // only for deterministic over-fetch when exclusions remove top-ranked
    // candidates.
    private const int MaximumSearchLimit = 2_000;
    private readonly string connectionString;
    private bool disposed;

    private SqliteShareCatalogReader(string databasePath, ShareCatalogMetadata metadata)
    {
        DatabasePath = databasePath;
        Metadata = metadata;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }
    public ShareCatalogMetadata Metadata { get; }

    public static async ValueTask<SqliteShareCatalogReader> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(databasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT schema_version, generation_id,
                       created_at_utc, settings_hash, directory_count, file_count,
                       total_bytes, browse_status, browse_wire_version,
                       browse_length_bytes, browse_sha256
                FROM catalog_metadata
                LIMIT 2;
                """;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Share catalog metadata is missing.");

            int schemaVersion = reader.GetInt32(0);
            if (schemaVersion != ShareCatalogVersions.Schema)
                throw new InvalidDataException($"Unsupported share catalog schema {schemaVersion}.");

            var metadata = new ShareCatalogMetadata(
                Guid.Parse(reader.GetString(1)),
                ParseDate(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                Enum.Parse<ShareBrowseStatus>(reader.GetString(7), ignoreCase: false),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10));

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Share catalog has multiple metadata rows.");

            return new SqliteShareCatalogReader(fullPath, metadata);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens committed rows before the final metadata row is written. This is
    /// limited to generation construction and must never be published.
    /// </summary>
    public static async ValueTask<SqliteShareCatalogReader> OpenStagingAsync(
        string databasePath,
        ShareCatalogMetadata provisionalMetadata,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(databasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM directories),
                    (SELECT COUNT(*) FROM files),
                    (SELECT COALESCE(SUM(size_bytes), 0) FROM files);
                """;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetInt64(0) != provisionalMetadata.DirectoryCount
                || reader.GetInt64(1) != provisionalMetadata.FileCount
                || reader.GetInt64(2) != provisionalMetadata.TotalBytes)
            {
                throw new InvalidDataException(
                    "Staging catalog counts do not match the completed scan.");
            }
            return new SqliteShareCatalogReader(fullPath, provisionalMetadata);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
        RemotePathKey remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {FileColumns("f")},
                    r.alias, r.local_path, r.comparison_alias
             FROM files f
             INNER JOIN roots r ON r.root_id = f.root_id
             WHERE f.comparison_path = $comparison_path
             LIMIT 1;
             """;
        command.Parameters.AddWithValue("$comparison_path", remotePath.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        ShareCatalogFile file = ReadFile(reader, 0);
        var root = new ShareCatalogRoot(
            file.RootId,
            reader.GetString(12),
            reader.GetString(13),
            RemotePathKey.CreateAlias(reader.GetString(12)));
        return new ShareCatalogResolvedFile(root, file);
    }

    public async ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
        => await SearchAsync(query, [], limit, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
        string query,
        IReadOnlyCollection<string> exclusions,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumSearchLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));
        string match = BuildFtsMatch(query, exclusions);
        if (match.Length == 0)
            return [];

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {FileColumns("f")}
             FROM file_search s
             INNER JOIN files f ON f.file_id = s.rowid
             WHERE file_search MATCH $match
             ORDER BY bm25(file_search), f.remote_path
             LIMIT $limit;
             """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);

        var files = new List<ShareCatalogFile>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            files.Add(ReadFile(reader, 0));
        return files;
    }

    public async ValueTask<ShareCatalogBrowseDirectory?> GetDirectoryAsync(
        RemotePathKey remotePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ShareCatalogDirectory? directory;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT directory_id, root_id, relative_path, remote_path, comparison_path
                FROM directories
                WHERE comparison_path = $comparison_path
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$comparison_path", remotePath.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            directory = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadDirectory(reader, 0)
                : null;
        }

        if (directory is null)
            return null;

        await using var filesCommand = connection.CreateCommand();
        filesCommand.CommandText =
            $"""
             SELECT {FileColumns("f")}
             FROM files f
             WHERE f.directory_id = $directory_id
             ORDER BY f.remote_path;
             """;
        filesCommand.Parameters.AddWithValue("$directory_id", directory.DirectoryId);
        var files = new List<ShareCatalogFile>();
        await using var filesReader = await filesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await filesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            files.Add(ReadFile(filesReader, 0));

        return new ShareCatalogBrowseDirectory(directory, files);
    }

    public async IAsyncEnumerable<ShareCatalogBrowseDirectory> EnumerateBrowseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT d.directory_id, d.root_id, d.relative_path, d.remote_path,
                    d.comparison_path,
                    {FileColumns("f")}
             FROM directories d
             LEFT JOIN files f ON f.directory_id = d.directory_id
             ORDER BY d.remote_path, f.remote_path;
             """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        ShareCatalogDirectory? current = null;
        List<ShareCatalogFile>? files = null;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long directoryId = reader.GetInt64(0);
            if (current?.DirectoryId != directoryId)
            {
                if (current is not null)
                    yield return new ShareCatalogBrowseDirectory(current, files!);
                current = ReadDirectory(reader, 0);
                files = [];
            }

            if (!reader.IsDBNull(5))
                files!.Add(ReadFile(reader, 5));
        }

        if (current is not null)
            yield return new ShareCatalogBrowseDirectory(current, files!);
    }

    public async IAsyncEnumerable<ShareCatalogBrowseRow> EnumerateBrowseRowsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT d.directory_id, d.root_id, d.relative_path, d.remote_path,
                    d.comparison_path,
                    COUNT(f.file_id) OVER (PARTITION BY d.directory_id) AS file_count,
                    {FileColumns("f")}
             FROM directories d
             LEFT JOIN files f ON f.directory_id = d.directory_id
             ORDER BY d.remote_path, f.remote_path;
             """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        long? currentDirectoryId = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long directoryId = reader.GetInt64(0);
            if (currentDirectoryId != directoryId)
            {
                currentDirectoryId = directoryId;
                long count = reader.GetInt64(5);
                if (count > int.MaxValue)
                {
                    throw new BrowseArtifactOversizeException(
                        count,
                        int.MaxValue);
                }
                yield return new ShareCatalogBrowseDirectoryRow(
                    ReadDirectory(reader, 0),
                    checked((int)count));
            }

            if (!reader.IsDBNull(6))
                yield return new ShareCatalogBrowseFileRow(ReadFile(reader, 6));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;

        // Connections are deliberately short-lived and pooled. Once a generation
        // is retired, clear only its pool so Windows releases the immutable file
        // before the manager removes it.
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static ShareCatalogDirectory ReadDirectory(SqliteDataReader reader, int offset)
        => new(
            reader.GetInt64(offset),
            reader.GetInt64(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            RemotePathKey.Create(reader.GetString(offset + 3)));

    private static ShareCatalogFile ReadFile(SqliteDataReader reader, int offset)
    {
        string remotePath = reader.GetString(offset + 4);
        IReadOnlyList<ShareFileAttribute> attributes =
            JsonSerializer.Deserialize<List<ShareFileAttribute>>(reader.GetString(offset + 11))
            ?? [];

        return new ShareCatalogFile(
            reader.GetInt64(offset),
            reader.GetInt64(offset + 1),
            reader.GetInt64(offset + 2),
            reader.GetString(offset + 3),
            remotePath,
            RemotePathKey.Create(remotePath),
            reader.GetString(offset + 5),
            reader.GetInt64(offset + 6),
            ParseDate(reader.GetString(offset + 7)),
            reader.GetInt32(offset + 8),
            reader.GetString(offset + 9),
            attributes);
    }

    private static string FileColumns(string alias)
        => $"""
            {alias}.file_id, {alias}.root_id, {alias}.directory_id,
            {alias}.relative_path, {alias}.remote_path, {alias}.search_text,
            {alias}.size_bytes, {alias}.modified_at_utc,
            {alias}.protocol_code, {alias}.extension, {alias}.comparison_path,
            {alias}.attributes_json
            """;

    private static string BuildFtsMatch(
        string query,
        IReadOnlyCollection<string>? exclusions = null)
    {
        string[] positive = SearchTermRegex()
            .Matches(query ?? "")
            .Select(match => QuoteFtsTerm(match.Value))
            .ToArray();
        if (positive.Length == 0)
            return "";

        // A NOT prefilter must never remove a row that Soulseek's final
        // substring rule would keep. Restrict it to a single complete FTS
        // token; compound/punctuation-rich exclusions remain bounded
        // post-filters in the adapter.
        string[] negative = (exclusions ?? [])
            .Select(value => value?.Trim() ?? "")
            .Select(value => (
                Value: value,
                Terms: SearchTermRegex().Matches(value)
                    .Select(match => match.Value)
                    .ToArray()))
            .Where(value => value.Terms.Length == 1
                            && string.Equals(
                                value.Value,
                                value.Terms[0],
                                StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Terms[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(QuoteFtsTerm)
            .ToArray();
        string match = string.Join(" AND ", positive);
        return negative.Length == 0
            ? match
            : $"{match} NOT {string.Join(" NOT ", negative)}";
    }

    private static string QuoteFtsTerm(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();
}
