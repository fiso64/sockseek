using System.Text.Json;
using System.Buffers;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Write;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Planning;

/// <summary>
/// Immutable source facts, independent of a View's filters and paging sequence.
/// Raw observations use their source sequence; retrieved files use the frozen
/// directory snapshot's ordinal. Neither identity depends on projection order.
/// </summary>
internal sealed class SearchObservationWriter : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly long sourceId;
    private readonly string originId;
    private readonly SqliteWriteStatement statement;
    private readonly bool directory;
    internal long SourceId => sourceId;
    private SqliteCommand? existing;
    private SqliteWriteStatement? rawBatch;
    private static readonly JsonSerializerOptions AttributeJson = new()
        { Converters = { new StoredAttributeConverter() } };
    private static readonly string[] Columns = [
        "source_id", "origin_kind", "origin_id", "source_sequence", "source_revision",
        "username", "response_file_count", "filename", "size_bytes", "bit_rate", "bit_depth",
        "sample_rate", "duration_seconds", "extension", "upload_speed", "has_free_upload_slot",
        "queue_length", "attributes_json", "observed_at_utc", "visibility",
    ];
    private static readonly int AttributesColumn = Array.IndexOf(Columns, "attributes_json");

    private SearchObservationWriter(SqliteConnection connection, SqliteTransaction transaction,
        long sourceId, Guid? directoryObservationId, bool rawHistory)
    {
        directory = directoryObservationId.HasValue;
        this.connection = connection;
        this.transaction = transaction;
        this.sourceId = sourceId;
        originId = directoryObservationId?.ToString("D") ?? "";
        string insert = $"INSERT INTO search_observations ({string.Join(", ", Columns)}";
        string values = string.Join(", ", Columns.Select((_, i) => "$p" + i));
        string sql = rawHistory
            ? insert + $"""
                ) VALUES ({values})
                ON CONFLICT(source_id, source_sequence) WHERE origin_kind=0 DO NOTHING
                ON CONFLICT(source_id, username, filename, visibility) WHERE origin_kind=0 DO NOTHING
                RETURNING visibility;
                """
            : insert + $"""
                ) VALUES ({values})
                ON CONFLICT(source_id, origin_kind, origin_id, source_sequence, source_revision) WHERE origin_kind<>0 DO NOTHING;
                """;
        statement = new SqliteWriteStatement(connection, transaction, sql);
        statement.Bind(0, sourceId);
        statement.Bind(1, directory ? 1 : 0);
        statement.Bind(2, originId);
    }

    public static Task<SearchObservationWriter> ForDirectoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid sourceJobId, Guid observationId, CancellationToken cancellationToken)
        => CreateAsync(connection, transaction, sourceJobId, observationId, false, cancellationToken);

    private static async Task<SearchObservationWriter> CreateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid sourceJobId, Guid? directoryObservationId, bool rawHistory, CancellationToken cancellationToken)
    {
        await using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        // Raw availability belongs to the source, and only raw ingestion may
        // attach it. Publishing a retained View cannot resurrect pruned history.
        owner.CommandText = """
            INSERT INTO search_observation_sources(source_job_id, raw_search_job_id) VALUES ($source, $raw)
            ON CONFLICT(source_job_id) DO UPDATE SET raw_search_job_id=excluded.raw_search_job_id
                WHERE search_observation_sources.raw_search_job_id IS NULL AND excluded.raw_search_job_id IS NOT NULL;
            SELECT id FROM search_observation_sources WHERE source_job_id=$source;
            """;
        string key = sourceJobId.ToString("D").ToUpperInvariant();
        owner.Parameters.AddWithValue("$source", key);
        owner.Parameters.AddWithValue("$raw", rawHistory ? key : DBNull.Value);
        long id = (long)(await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new(connection, transaction, id, directoryObservationId, rawHistory);
    }

    public async ValueTask<long> StoreDirectoryAsync(SearchProjectionInput input, CancellationToken cancellationToken, int directoryOrdinal)
    {
        if (!directory)
            throw new InvalidOperationException("Only raw ingestion can store original observations.");
        // Bind the hot insert path directly: an object array boxes every numeric
        // fact for every arrival. Only the uncommon immutable-fact check below
        // needs provider parameter values.
        statement.Bind(3, directoryOrdinal);
        statement.Bind(4, input.Revision);
        statement.Bind(5, input.Username);
        statement.Bind(6, input.ResponseFileCount);
        statement.Bind(7, input.Filename);
        statement.Bind(8, input.Size);
        statement.Bind(9, (long?)input.BitRate);
        statement.Bind(10, (long?)input.BitDepth);
        statement.Bind(11, (long?)input.SampleRate);
        statement.Bind(12, (long?)input.Length);
        statement.Bind(13, input.Extension);
        statement.Bind(14, (long?)input.UploadSpeed);
        statement.Bind(15, input.HasFreeUploadSlot is bool free ? free ? 1L : 0L : (long?)null);
        statement.Bind(16, (long?)input.QueueLength);
        string? attributes = input.Attributes == null ? null : SerializeAttributes(input.Attributes);
        statement.Bind(17, attributes);
        statement.Bind(18, input.ObservedAtUtc.ToUnixTimeMilliseconds());
        statement.Bind(19, input.Visibility.ToString());
        // Only an actual insert owns last_insert_rowid on this writer connection.
        // Reuse must resolve the existing identity, without rewriting its facts.
        if (statement.Execute(cancellationToken) != 0)
            return statement.LastInsertRowId;
        if (existing == null)
        {
            existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id, attributes_json FROM search_observations WHERE origin_kind<>0 AND "
                + string.Join(" AND ", Columns.Select((column, index) => (column, index))
                    .Where(item => item.index != AttributesColumn)
                    .Select(item => item.column + " IS $p" + item.index)) + ";";
            for (int i = 0; i < Columns.Length; i++)
                if (i != AttributesColumn) existing.Parameters.AddWithValue("$p" + i, DBNull.Value);
            existing.Prepare();
        }
        existing.Parameters[0].Value = sourceId;
        existing.Parameters[1].Value = directory ? 1 : 0;
        existing.Parameters[2].Value = originId;
        object?[] values = FactValues(input, attributes, directoryOrdinal);
        int binding = 3;
        for (int i = 0; i < values.Length; i++)
            if (i + 3 != AttributesColumn) existing.Parameters[binding++].Value = values[i] ?? DBNull.Value;
        await using var prior = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await prior.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string? stored = prior.IsDBNull(1) ? null : prior.GetString(1);
            // SQL migrations and JSON encoders can escape identical names
            // differently. Immutable facts compare values, never JSON spelling.
            // Ordinary reuse keeps the cheap exact-text path.
            if (string.Equals(stored, attributes, StringComparison.Ordinal))
                return prior.GetInt64(0);
            var decoded = ReadAttributes(stored);
            if (decoded == null ? input.Attributes == null
                : input.Attributes != null && decoded.SequenceEqual(input.Attributes))
                return prior.GetInt64(0);
        }
        throw new InvalidDataException("A search observation identity was reused with different facts.");
    }

    private static object?[] FactValues(SearchProjectionInput input, string? attributes, int? directoryOrdinal)
        => [directoryOrdinal.HasValue ? directoryOrdinal.Value : input.Sequence, input.Revision,
            input.Username, input.ResponseFileCount, input.Filename, input.Size, input.BitRate, input.BitDepth,
            input.SampleRate, input.Length, input.Extension, input.UploadSpeed,
            input.HasFreeUploadSlot is bool free ? free ? 1 : 0 : null,
            input.QueueLength, attributes,
            input.ObservedAtUtc.ToUnixTimeMilliseconds(), input.Visibility.ToString()];

    // Publication only resolves committed originals. It has no insert path for
    // them: ingestion owns both immutable facts and raw-history membership.
    public static async Task<(long SourceId, Dictionary<(long Sequence, int Revision), long> Ids)> ResolveSearchAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid sourceJobId,
        IReadOnlyList<ProjectedFileCandidate> files, CancellationToken cancellationToken)
    {
        var found = new Dictionary<(long, int), long>();
        var requested = new Dictionary<(long Sequence, int Revision), SearchProjectionInput>(files.Count);
        foreach (var file in files)
        {
            var key = (file.Input.Sequence, file.Input.Revision);
            if (!requested.TryAdd(key, file.Input) && requested[key] != file.Input)
                throw new InvalidDataException("A search observation identity was reused with different facts.");
        }
        long sourceId = 0;
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        // Resolve just the requested immutable identities in one statement. Do
        // not hydrate/serialize file facts again for each refinement, or scan an
        // entire source when a small, sparse batch is being projected. CROSS
        // JOIN keeps requested IDs outermost: SQLite otherwise chooses the
        // source prefix and scans the JSON array once per source row (quadratic).
        lookup.CommandText = """
            SELECT fact.source_sequence, fact.source_revision, fact.id, fact.source_id
            FROM json_each($requested) requested CROSS JOIN search_observations fact
                ON fact.source_id=(SELECT id FROM search_observation_sources WHERE source_job_id=$source)
                AND fact.origin_kind=0 AND fact.origin_id=''
                AND fact.source_sequence=json_extract(requested.value, '$[0]')
                AND fact.source_revision=json_extract(requested.value, '$[1]');
            """;
        lookup.Parameters.AddWithValue("$source", sourceJobId.ToString("D").ToUpperInvariant());
        lookup.Parameters.AddWithValue("$requested", JsonSerializer.Serialize(
            requested.Keys.Select(key => new long[] { key.Sequence, key.Revision })));
        await using (var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found[(reader.GetInt64(0), reader.GetInt32(1))] = reader.GetInt64(2);
                sourceId = reader.GetInt64(3);
            }
        int requestedCount = requested.Count;
        if (Activity.Current is { IsAllDataRequested: true } activity)
            activity.AddEvent(new("search.observations", tags: new()
            {
                ["requested.count"] = requestedCount,
                ["known.count"] = found.Count,
                ["missing.count"] = requestedCount - found.Count,
            }));
        if (found.Count != requestedCount)
            throw new InvalidOperationException("Search View publication requires committed source observations.");
        return (sourceId, found);
    }

    public static Task<SearchObservationWriter> ForRawHistoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid sourceJobId, CancellationToken cancellationToken)
        => CreateAsync(connection, transaction, sourceJobId, null, true, cancellationToken);

    // One SQLite transaction owns these source identities and statements. Do not
    // retain them between transactions: pruning can reuse numeric source IDs.
    internal sealed class RawHistoryBatch(SqliteConnection connection, SqliteTransaction transaction) : IAsyncDisposable
    {
        private readonly Dictionary<Guid, SearchObservationWriter> sources = [];

        public async ValueTask<(int Public, int Locked)> StoreAsync(
            Guid sourceJobId, PreparedBatch prepared, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(sourceJobId, out var source))
            {
                source = await ForRawHistoryAsync(connection, transaction, sourceJobId, cancellationToken).ConfigureAwait(false);
                sources.Add(sourceJobId, source);
            }
            return source.StoreRawBatch(prepared, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var source in sources.Values) await source.DisposeAsync().ConfigureAwait(false);
            sources.Clear();
        }
    }

    // A writer batch owns these storage-only values. All callers must prepare
    // them before reserving SQLite; facts remain the original immutable input.
    internal readonly record struct Prepared(SearchProjectionInput Input, string? AttributesJson);

    // Small first transactions avoid initializing the bulk encoder. Large ones
    // prepare one source append, even when peers send small responses. Original
    // packet identities remain with the owner for acknowledgement and retry.
    // Both paths prepare before write access and accept all supplied input.
    private const int SmallRawBatchSize = 256;
    private const int EncodedRawBatchSize = 1_000;

    internal sealed class PreparedBatch
    {
        internal Prepared[] Rows { get; } = [];
        internal byte[][] Chunks { get; } = [];
        internal PreparedBatch(Prepared[] rows) => Rows = rows;
        internal PreparedBatch(byte[][] chunks) => Chunks = chunks;
    }

    private static readonly JsonSerializerOptions RawBatchJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new RawBatchConverter() },
    };

    internal static PreparedBatch Prepare(IReadOnlyList<SearchProjectionInput> inputs)
    {
        if (inputs.Count <= SmallRawBatchSize)
        {
            var rows = new Prepared[inputs.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                var input = inputs[i];
                rows[i] = new(input,
                    input.Attributes == null ? null : SerializeAttributes(input.Attributes));
            }
            return new(rows);
        }
        var chunks = new byte[(inputs.Count - 1) / EncodedRawBatchSize + 1][];
        for (int chunk = 0; chunk < chunks.Length; chunk++)
        {
            int offset = chunk * EncodedRawBatchSize;
            chunks[chunk] = JsonSerializer.SerializeToUtf8Bytes(new RawChunk(
                inputs, offset, Math.Min(EncodedRawBatchSize, inputs.Count - offset)), RawBatchJson);
        }
        return new(chunks);
    }

    public (int Public, int Locked) StoreRawBatch(PreparedBatch prepared, CancellationToken cancellationToken)
    {
        int added = 0, locked = 0;
        foreach (var row in prepared.Rows)
        {
            string? visibility = StoreRaw(row, cancellationToken);
            if (visibility == null) continue;
            added++;
            if (visibility == "Locked") locked++;
        }
        foreach (byte[] chunk in prepared.Chunks)
        {
            if (rawBatch == null)
            {
                rawBatch = new(connection, transaction, RawBatchSql);
                rawBatch.Bind(0, sourceId);
            }
            rawBatch.BindUtf8Text(1, chunk);
            var counts = rawBatch.ExecuteSum(cancellationToken);
            added = checked(added + counts.Count);
            locked = checked(locked + (int)counts.Sum);
        }
        return (added - locked, locked);
    }

    // Keep array order for first-winner deduplication. Every tuple reaches the
    // INSERT constraints before collision handling: filtering known sequences
    // in SELECT would silently hide invalid metadata in a repeated observation.
    // jsonb_each retains binary tuple values; json_each would convert them back
    // to text and make each json_extract parse the same tuple again.
    private static readonly string RawBatchSql = $"""
        INSERT INTO search_observations({string.Join(",", Columns)})
        SELECT $source,0,'',{string.Join(",", Enumerable.Range(0, 17).Select(i => $"json_extract(input.value,'$[{i}]')"))}
        FROM jsonb_each(jsonb($rows)) input
        WHERE 1
        ORDER BY input.key
        ON CONFLICT(source_id,source_sequence) WHERE origin_kind=0 DO NOTHING
        ON CONFLICT(source_id,username,filename,visibility) WHERE origin_kind=0 DO NOTHING
        RETURNING visibility='Locked';
        """;

    private readonly record struct RawChunk(IReadOnlyList<SearchProjectionInput> Inputs, int Offset, int Count);

    private sealed class RawBatchConverter : JsonConverter<RawChunk>
    {
        public override RawChunk Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => throw new NotSupportedException("Prepared observations are write-only.");

        public override void Write(Utf8JsonWriter writer, RawChunk chunk, JsonSerializerOptions options)
        {
            // Encode canonical facts directly. Source/sequence and exact peer/path
            // uniqueness belong to SQLite; no second per-file identity is needed.
            // Scratch belongs to this bounded chunk, not individual file DTOs.
            var attributes = new ArrayBufferWriter<byte>();
            using var attributeWriter = new Utf8JsonWriter(attributes);
            writer.WriteStartArray();
            for (int i = chunk.Offset; i < chunk.Offset + chunk.Count; i++)
            {
                var input = chunk.Inputs[i];
                string visibility = input.Visibility.ToString();

                writer.WriteStartArray();
                writer.WriteNumberValue(input.Sequence);
                writer.WriteNumberValue(input.Revision);
                writer.WriteStringValue(input.Username);
                writer.WriteNumberValue(input.ResponseFileCount);
                writer.WriteStringValue(input.Filename);
                writer.WriteNumberValue(input.Size);
                Number(input.BitRate);
                Number(input.BitDepth);
                Number(input.SampleRate);
                Number(input.Length);
                writer.WriteStringValue(input.Extension);
                Number(input.UploadSpeed);
                if (input.HasFreeUploadSlot is bool free) writer.WriteBooleanValue(free);
                else writer.WriteNullValue();
                Number(input.QueueLength);
                if (input.Attributes == null) writer.WriteNullValue();
                else
                {
                    attributes.Clear();
                    attributeWriter.Reset(attributes);
                    attributeWriter.WriteStartArray();
                    foreach (var attribute in input.Attributes)
                        WriteAttribute(attributeWriter, attribute);
                    attributeWriter.WriteEndArray();
                    attributeWriter.Flush();
                    writer.WriteStringValue(attributes.WrittenSpan);
                }
                writer.WriteNumberValue(input.ObservedAtUtc.ToUnixTimeMilliseconds());
                writer.WriteStringValue(visibility);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            void Number(int? value)
            {
                if (value is int number) writer.WriteNumberValue(number);
                else writer.WriteNullValue();
            }
        }
    }

    public string? StoreRaw(Prepared prepared, CancellationToken cancellationToken)
    {
        var result = prepared.Input;
        statement.Bind(3, result.Sequence);
        statement.Bind(4, result.Revision);
        statement.Bind(5, result.Username);
        statement.Bind(6, result.ResponseFileCount);
        statement.Bind(7, result.Filename);
        statement.Bind(8, result.Size);
        statement.Bind(9, (long?)result.BitRate);
        statement.Bind(10, (long?)result.BitDepth);
        statement.Bind(11, (long?)result.SampleRate);
        statement.Bind(12, (long?)result.Length);
        statement.Bind(13, result.Extension);
        statement.Bind(14, (long?)result.UploadSpeed);
        statement.Bind(15, result.HasFreeUploadSlot is bool free ? free ? 1L : 0L : (long?)null);
        statement.Bind(16, (long?)result.QueueLength);
        statement.Bind(17, prepared.AttributesJson);
        statement.Bind(18, result.ObservedAtUtc.ToUnixTimeMilliseconds());
        statement.Bind(19, result.Visibility.ToString());
        return statement.ExecuteScalarText(cancellationToken);
    }

    // One storage codec serves raw batches, retrieved directories and retained
    // projection reads. Positional triples avoid repeating property names for
    // every attribute; names, signed codes/values, duplicates and order remain
    // exact. Public DTOs are built from FileAttributeSnapshot, not this format.
    internal static string SerializeAttributes(IReadOnlyList<FileAttributeSnapshot> attributes)
        => JsonSerializer.Serialize(attributes, AttributeJson);

    internal static IReadOnlyList<FileAttributeSnapshot>? ReadAttributes(string? json, bool defer = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (!defer) return DecodeAttributes(json);
        ReadOnlySpan<char> value = json.AsSpan().Trim();
        if (value.SequenceEqual("null")) return null;
        if (value.SequenceEqual("[]")) return Array.Empty<FileAttributeSnapshot>();
        return new RetainedAttributes(json);
    }

    private static FileAttributeSnapshot[]? DecodeAttributes(string json)
        => JsonSerializer.Deserialize<FileAttributeSnapshot[]>(json, AttributeJson);

    private static void WriteAttribute(Utf8JsonWriter writer, FileAttributeSnapshot attribute)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(attribute.StableCode);
        writer.WriteStringValue(attribute.Type);
        writer.WriteNumberValue(attribute.Value);
        writer.WriteEndArray();
    }

    private sealed class StoredAttributeConverter : JsonConverter<FileAttributeSnapshot>
    {
        public override bool HandleNull => true;

        public override FileAttributeSnapshot Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Invalid retained file attribute.");
            reader.Read();
            int code = reader.GetInt32();
            reader.Read();
            string? name = reader.GetString();
            reader.Read();
            int value = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("Invalid retained file attribute.");
            return new(name!, value, code);
        }

        public override void Write(Utf8JsonWriter writer, FileAttributeSnapshot value, JsonSerializerOptions options)
            => WriteAttribute(writer, value);
    }

    // Historical projection uses the scalar facts above. Keep the complete wire
    // attributes available without decoding every file's JSON during refinement.
    // This owns its string, not a database reader; selected output can safely
    // enumerate it after the read snapshot closes or the source is pruned.
    private sealed class RetainedAttributes(string json) : IReadOnlyList<FileAttributeSnapshot>
    {
        // Replace the encoded string with the array: accessing metadata must
        // not retain both representations for the rest of the input's lifetime.
        private object contents = json;

        private FileAttributeSnapshot[] Values
        {
            get
            {
                object current = Volatile.Read(ref contents);
                if (current is FileAttributeSnapshot[] values) return values;
                FileAttributeSnapshot[] parsed = DecodeAttributes((string)current)!;
                return Interlocked.CompareExchange(ref contents, parsed, current)
                    is FileAttributeSnapshot[] existing ? existing : parsed;
            }
        }

        public int Count => Values.Length;
        public FileAttributeSnapshot this[int index] => Values[index];
        public IEnumerator<FileAttributeSnapshot> GetEnumerator()
            => ((IEnumerable<FileAttributeSnapshot>)Values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static async Task PruneUnreferencedAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyCollection<Guid> sourceJobIds, CancellationToken cancellationToken)
    {
        if (sourceJobIds.Count == 0) return;
        // A nonempty View pins its source, including facts excluded by its
        // current projection. Delete a source's facts only after all pins are
        // gone. Composite file/source FKs then probe an empty source prefix,
        // rather than maintaining a randomly ordered reverse index per file.
        await using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM search_observations
            WHERE source_id IN (SELECT id FROM search_observation_sources WHERE source_job_id IN (SELECT value FROM json_each($sources)))
                AND (origin_kind<>0 OR NOT EXISTS (SELECT 1 FROM search_observation_sources source
                    WHERE source.id=search_observations.source_id AND source.raw_search_job_id IS NOT NULL))
                AND NOT EXISTS (SELECT 1 FROM search_views owner WHERE owner.source_id=search_observations.source_id);
            DELETE FROM search_observation_sources
            WHERE source_job_id IN (SELECT value FROM json_each($sources)) AND raw_search_job_id IS NULL
                AND NOT EXISTS (SELECT 1 FROM search_observations fact WHERE fact.source_id=search_observation_sources.id);
            """;
        prune.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(sourceJobIds.Select(id => id.ToString("D").ToUpperInvariant())));
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (existing != null) await existing.DisposeAsync().ConfigureAwait(false);
        rawBatch?.Dispose();
        statement.Dispose();
    }
}
