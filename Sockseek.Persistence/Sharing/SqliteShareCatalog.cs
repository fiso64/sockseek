using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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
public sealed partial class SqliteShareCatalogBuilder : IShareCatalogGenerationWriter
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
        connection.CreateFunction<string, bool, string>("share_search_terms", IndexTerms, isDeterministic: true);
    }

    // Both FTS columns are searched together. Only the ASCII
    // column can prove that an ASCII exclusion literally occurs in a path:
    // unicode61 also matches "cafe" against "café".
    private static string IndexTerms(string path, bool ascii)
    {
        if (Ascii.IsValid(path)) return ascii ? path : "";
        var terms = new StringBuilder();
        foreach (var match in IndexTermRegex().EnumerateMatches(path))
        {
            var term = path.AsSpan(match.Index, match.Length);
            if (Ascii.IsValid(term) == ascii) terms.Append(term).Append(' ');
        }
        return terms.ToString();
    }

    // Split only at ASCII separators known to unicode61. It can tokenize the
    // remaining Unicode fragments itself, exactly as it does for queries.
    [GeneratedRegex(@"[a-zA-Z0-9\u0080-\uFFFF]+")]
    private static partial Regex IndexTermRegex();

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

                -- A file and a directory may not share a path. Only the scan
                -- asks, so the answer lives with this connection and is gone
                -- when the generation is published.
                CREATE TEMP TABLE path_identities (
                    remote_path TEXT PRIMARY KEY,
                    kind TEXT NOT NULL
                ) WITHOUT ROWID;

                -- A path is stored once per row. remote_path compares by its
                -- UTF-8 bytes, which is the identity a peer's request carries,
                -- and its unique index answers exact lookups and ordering.
                CREATE TABLE directories (
                    directory_id INTEGER PRIMARY KEY,
                    root_id INTEGER NOT NULL,
                    relative_path TEXT NOT NULL,
                    remote_path TEXT NOT NULL UNIQUE,
                    FOREIGN KEY (root_id) REFERENCES roots(root_id)
                );

                CREATE TABLE files (
                    file_id INTEGER PRIMARY KEY,
                    root_id INTEGER NOT NULL,
                    directory_id INTEGER NOT NULL,
                    -- Null when it is the remote path below its root with
                    -- this system's separators, which is nearly always.
                    relative_path TEXT,
                    remote_path TEXT NOT NULL UNIQUE,
                    size_bytes INTEGER NOT NULL,
                    modified_at_utc INTEGER NOT NULL,
                    protocol_code INTEGER NOT NULL,
                    extension TEXT NOT NULL,
                    attributes_json TEXT NOT NULL,
                    FOREIGN KEY (root_id) REFERENCES roots(root_id),
                    FOREIGN KEY (directory_id) REFERENCES directories(directory_id)
                );

                CREATE INDEX idx_files_directory
                    ON files(directory_id, remote_path);

                -- File facts live only in files. The contentless FTS index owns
                -- tokens and aligned row IDs, so matching never reads a second
                -- content table merely to find the authoritative file row.
                CREATE VIRTUAL TABLE file_search USING fts5(
                    ascii_terms, unicode_terms,
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
                INSERT INTO file_search(rowid, ascii_terms, unicode_terms)
                SELECT file.file_id,
                    share_search_terms(replace(file.remote_path, char(92), ' '), 1),
                    share_search_terms(replace(file.remote_path, char(92), ' '), 0)
                -- Read the table itself, in row order: the path index also holds
                -- every value needed here, but feeding the search index rows in
                -- path order leaves it measurably slower to query.
                FROM files file NOT INDEXED
                WHERE NOT EXISTS (SELECT 1 FROM file_search existing WHERE existing.rowid = file.file_id)
                ORDER BY file.file_id;
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

        private void AddIdentity(string path, string kind, CancellationToken cancellationToken)
        {
            var writer = identities ??= new(connection, transaction, """
                INSERT INTO temp.path_identities(remote_path, kind) VALUES($path, $kind);
                """);
            writer.Bind(0, path);
            writer.Bind(1, kind);
            writer.Execute(cancellationToken);
        }

        public void AddDirectory(ShareCatalogDirectory directory, CancellationToken cancellationToken)
        {
            AddIdentity(directory.RemotePath, "directory", cancellationToken);
            var writer = directories ??= new(connection, transaction, """
                INSERT INTO directories(directory_id, root_id, relative_path, remote_path)
                VALUES($id, $root, $relative, $remote);
                """);
            writer.Bind(0, directory.DirectoryId);
            writer.Bind(1, directory.RootId);
            writer.Bind(2, directory.RelativePath);
            writer.Bind(3, directory.RemotePath);
            writer.Execute(cancellationToken);
        }

        public void AddFile(ShareCatalogFile file, CancellationToken cancellationToken)
        {
            AddIdentity(file.RemotePath, "file", cancellationToken);
            var writer = files ??= new(connection, transaction, """
                INSERT INTO files(file_id, root_id, directory_id, relative_path, remote_path,
                    size_bytes, modified_at_utc, protocol_code, extension, attributes_json)
                VALUES($id, $root, $directory, $relative, $remote,
                    $size, $modified, $code, $extension, $attributes);
                """);
            writer.Bind(0, file.FileId);
            writer.Bind(1, file.RootId);
            writer.Bind(2, file.DirectoryId);
            writer.Bind(3, LocalPathBelowRoot(file.RemotePath) == file.RelativePath ? null : file.RelativePath);
            writer.Bind(4, file.RemotePath);
            writer.Bind(5, file.SizeBytes);
            writer.Bind(6, file.ModifiedAtUtc.UtcTicks);
            writer.Bind(7, (long)file.ProtocolCode);
            writer.Bind(8, file.Extension);
            writer.Bind(9, JsonSerializer.Serialize(file.Attributes));
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

    /// <summary>
    /// The local path a remote path stands for when nothing was renamed on the
    /// way: everything below the share's name, with this system's separators.
    /// </summary>
    internal static string LocalPathBelowRoot(string remotePath)
        => remotePath[(remotePath.IndexOf('\\') + 1)..].Replace('\\', Path.DirectorySeparatorChar);

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
    private readonly string connectionString;
    private readonly Stack<SqliteCommand> idleSearchCommands = [];
    private bool disposed;

    private SqliteShareCatalogReader(string databasePath, string connectionString, ShareCatalogMetadata metadata)
    {
        DatabasePath = databasePath;
        Metadata = metadata;
        this.connectionString = connectionString;
    }

    private static string ReadConnectionString(string path, bool immutable)
    {
        // Only sealed generations are immutable. Staging reads still participate
        // in SQLite locking. Keep native network-share paths on Windows.
        var uri = new Uri(path);
        return new SqliteConnectionStringBuilder
        {
            DataSource = immutable && !uri.IsUnc ? uri.AbsoluteUri + "?immutable=1" : path,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
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
        string connectionString = ReadConnectionString(fullPath, immutable: true);
        var connection = new SqliteConnection(connectionString);
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

            return new SqliteShareCatalogReader(fullPath, connectionString, metadata);
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
        string connectionString = ReadConnectionString(fullPath, immutable: false);
        var connection = new SqliteConnection(connectionString);
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
            return new SqliteShareCatalogReader(fullPath, connectionString, provisionalMetadata);
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
             WHERE f.remote_path = $remote_path
             LIMIT 1;
             """;
        string requested = Encoding.UTF8.GetString(remotePath.ToArray());
        command.Parameters.AddWithValue("$remote_path", requested);

        ShareCatalogResolvedFile? exact = await ReadResolvedAsync(command, remotePath, cancellationToken).ConfigureAwait(false);
        if (exact is not null) return exact;

        // Soulseek NS requests the paths it was given in lower case. Find the
        // one file whose path differs only by case, through the word index so
        // that a miss never scans the catalog.
        if (requested.Any(char.IsUpper)) return null;
        // Every word of the path narrows the candidates to this file's folder.
        string words = string.Join(" AND ", SearchTermRegex().Matches(requested)
            .Take(32).Select(term => $"\"{term.Value}\""));
        if (words.Length == 0) return null;
        command.CommandText =
            $"""
             SELECT {FileColumns("f")},
                    r.alias, r.local_path, r.comparison_alias
             FROM file_search s
             INNER JOIN files f ON f.file_id = s.rowid
             INNER JOIN roots r ON r.root_id = f.root_id
             WHERE file_search MATCH $words AND f.remote_path = $requested COLLATE NOCASE
             LIMIT 2;
             """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$words", words);
        command.Parameters.AddWithValue("$requested", requested);
        return await ReadResolvedAsync(command, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The single file a lookup found; none when it found no file or more than one.</summary>
    private static async ValueTask<ShareCatalogResolvedFile?> ReadResolvedAsync(
        SqliteCommand command, RemotePathKey? comparisonPath, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        ShareCatalogFile file = ReadFile(reader, 0, comparisonPath);
        var root = new ShareCatalogRoot(
            file.RootId,
            reader.GetString(FileColumnCount),
            reader.GetString(FileColumnCount + 1),
            RemotePathKey.CreateAlias(reader.GetString(FileColumnCount)));
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? null : new ShareCatalogResolvedFile(root, file);
    }

    public async ValueTask<IReadOnlyList<Soulseek.File>> SearchAsync(
        string query,
        int limit,
        IReadOnlyList<string> exclusions,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        // "*ello" asks for words ending in "ello". The index holds whole words,
        // so complete words select the candidates and endings filter them. Like
        // other clients, a search needs at least one complete word.
        var complete = new List<string>();
        var endings = new List<string>();
        foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool partial = word.Length > 1 && word[0] == '*';
            foreach (Match term in SearchTermRegex().Matches(word))
            {
                if (partial) endings.Add(FoldTerm(term.Value));
                else complete.Add(term.Value);
                partial = false;
            }
        }
        string match = string.Join(" AND ", complete.Select(term => $"\"{term}\""));
        if (match.Length == 0) return [];
        var excludedTokens = exclusions.Where(term => term.Length > 0 && term.All(char.IsAsciiLetterOrDigit))
            .Select(term => $"\"{term}\"");
        string negative = string.Join(" OR ", excludedTokens);
        if (negative.Length > 0) match += $" NOT ascii_terms : ({negative})";

        cancellationToken.ThrowIfCancellationRequested();
        SqliteCommand? command;
        lock (idleSearchCommands)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            idleSearchCommands.TryPop(out command);
        }
        command ??= await OpenSearchCommandAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadMatches(command, match, limit, exclusions, endings, cancellationToken);
        }
        finally
        {
            lock (idleSearchCommands)
            {
                if (disposed) CloseSearchCommand(command);
                else idleSearchCommands.Push(command);
            }
        }
    }

    private async ValueTask<SqliteCommand> OpenSearchCommandAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = connection.CreateCommand();
            // Map up to 256 MiB of this read-only index on demand. Concurrent
            // searches can read the same OS pages instead of copying each into SQLite.
            command.CommandText = "PRAGMA mmap_size=268435456;";
            command.ExecuteNonQuery();
            command.CommandText = """
                SELECT f.remote_path, f.protocol_code, f.size_bytes, f.extension, f.attributes_json
                FROM file_search s JOIN files f ON f.file_id = s.rowid
                WHERE file_search MATCH $match
                ORDER BY s.rowid;
                """;
            command.Parameters.Add("$match", SqliteType.Text);
            return command;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<Soulseek.File> ReadMatches(SqliteCommand command, string match, int limit,
        IReadOnlyList<string> exclusions, IReadOnlyList<string> endings, CancellationToken cancellationToken)
    {
        command.Parameters["$match"].Value = match;
        var files = new List<Soulseek.File>();
        using var interrupt = cancellationToken.UnsafeRegister(static handle =>
            SQLitePCL.raw.sqlite3_interrupt((SQLitePCL.sqlite3)handle!), command.Connection!.Handle);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            while (files.Count < limit && reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = reader.GetString(0);
                if (IsExcluded(path, exclusions) || !HasWordEndings(path, endings)) continue;
                string attributes = reader.GetString(4);
                files.Add(new Soulseek.File(reader.GetInt32(1), path, reader.GetInt64(2), reader.GetString(3),
                    attributes == "[]" ? null : JsonSerializer.Deserialize<ShareFileAttribute[]>(attributes)?
                        .Select(a => new Soulseek.FileAttribute((Soulseek.FileAttributeType)a.Type, a.Value))));
            }
            return files;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Share search cancelled.", e, cancellationToken);
        }
    }

    private static bool HasWordEndings(string path, IReadOnlyList<string> endings)
    {
        if (endings.Count == 0) return true;
        string folded = FoldTerm(path);
        foreach (string ending in endings)
        {
            bool found = false;
            foreach (ValueMatch word in SearchTermRegex().EnumerateMatches(folded))
                if (found = folded.AsSpan(word.Index, word.Length).EndsWith(ending, StringComparison.Ordinal)) break;
            if (!found) return false;
        }
        return true;
    }

    // Case and accents as the search index folds them.
    private static string FoldTerm(string value)
    {
        if (Ascii.IsValid(value)) return value.ToLowerInvariant();
        var folded = new StringBuilder(value.Length);
        foreach (char c in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
        return folded.ToString().ToLowerInvariant();
    }

    private static bool IsExcluded(string path, IReadOnlyList<string> exclusions)
    {
        for (int i = 0; i < exclusions.Count; i++)
            if (path.Contains(exclusions[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
                SELECT directory_id, root_id, relative_path, remote_path
                FROM directories
                WHERE remote_path = $remote_path
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$remote_path", Encoding.UTF8.GetString(remotePath.ToArray()));
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

    public async IAsyncEnumerable<ShareCatalogBrowseRow> EnumerateBrowseRowsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT d.directory_id, d.root_id, d.relative_path, d.remote_path,
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
                long count = reader.GetInt64(4);
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

            if (!reader.IsDBNull(5))
                yield return new ShareCatalogBrowseFileRow(ReadFile(reader, 5));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (idleSearchCommands)
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            while (idleSearchCommands.TryPop(out var command)) CloseSearchCommand(command);
        }

        // All other readers use short-lived pooled connections. Once a generation
        // is retired, clear only its pool so Windows releases the immutable file
        // before the manager removes it.
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }

    private static void CloseSearchCommand(SqliteCommand command)
    {
        var connection = command.Connection!;
        command.Dispose();
        connection.Dispose();
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static ShareCatalogDirectory ReadDirectory(SqliteDataReader reader, int offset)
        => new(
            reader.GetInt64(offset),
            reader.GetInt64(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            RemotePathKey.Create(reader.GetString(offset + 3)));

    private static ShareCatalogFile ReadFile(SqliteDataReader reader, int offset, RemotePathKey? comparisonPath = null)
    {
        string remotePath = reader.GetString(offset + 4);
        IReadOnlyList<ShareFileAttribute> attributes =
            JsonSerializer.Deserialize<List<ShareFileAttribute>>(reader.GetString(offset + 9))
            ?? [];

        return new ShareCatalogFile(
            reader.GetInt64(offset),
            reader.GetInt64(offset + 1),
            reader.GetInt64(offset + 2),
            reader.IsDBNull(offset + 3) ? SqliteShareCatalogBuilder.LocalPathBelowRoot(remotePath) : reader.GetString(offset + 3),
            remotePath,
            comparisonPath ?? RemotePathKey.Create(remotePath),
            reader.GetInt64(offset + 5),
            new DateTimeOffset(reader.GetInt64(offset + 6), TimeSpan.Zero),
            reader.GetInt32(offset + 7),
            reader.GetString(offset + 8),
            attributes);
    }

    private const int FileColumnCount = 10;

    private static string FileColumns(string alias)
        => $"""
            {alias}.file_id, {alias}.root_id, {alias}.directory_id,
            {alias}.relative_path, {alias}.remote_path,
            {alias}.size_bytes, {alias}.modified_at_utc,
            {alias}.protocol_code, {alias}.extension,
            {alias}.attributes_json
            """;

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();
}
