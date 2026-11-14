using System.Text.Json;
using System.Buffers;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Microsoft.Data.Sqlite;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Write;

// Raw search ingestion owns immutable observations. Views and retrieved-directory
// snapshots are temporary read resources and have no database write path here.
internal sealed class SearchResultWriter : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly long sourceId;
    private readonly SqliteWriteStatement statement;
    private SqliteWriteStatement? rawBatch;
    private static readonly JsonSerializerOptions AttributeJson = new()
        { Converters = { new StoredAttributeConverter() } };
    private static readonly string[] Columns = [
        "source_id", "source_sequence", "source_revision", "username", "response_file_count",
        "filename", "size_bytes", "bit_rate", "bit_depth", "sample_rate", "duration_seconds",
        "extension", "upload_speed", "has_free_upload_slot", "queue_length", "attributes_json",
        "observed_at_utc", "visibility",
    ];

    private SearchResultWriter(SqliteConnection connection, SqliteTransaction transaction, long sourceId)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.sourceId = sourceId;
        string values = string.Join(", ", Columns.Select((_, i) => "$p" + i));
        statement = new SqliteWriteStatement(connection, transaction, $"""
            INSERT INTO search_observations ({string.Join(", ", Columns)}) VALUES ({values})
            ON CONFLICT(source_id, source_sequence) DO NOTHING
            ON CONFLICT(source_id, username, filename, visibility) DO NOTHING
            RETURNING visibility;
            """);
        statement.Bind(0, sourceId);
    }

    public static async Task<SearchResultWriter> ForRawHistoryAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid sourceJobId, CancellationToken cancellationToken)
    {
        await using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        owner.CommandText = """
            INSERT INTO search_observation_sources(search_job_id) VALUES ($source)
            ON CONFLICT(search_job_id) DO NOTHING;
            SELECT id FROM search_observation_sources WHERE search_job_id=$source;
            """;
        owner.Parameters.AddWithValue("$source", sourceJobId.ToString("D").ToUpperInvariant());
        long id = (long)(await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new(connection, transaction, id);
    }

    // One SQLite transaction owns these source identities and statements. Do not
    // retain them between transactions: pruning can reuse numeric source IDs.
    internal sealed class RawHistoryBatch(SqliteConnection connection, SqliteTransaction transaction) : IAsyncDisposable
    {
        private readonly Dictionary<Guid, SearchResultWriter> sources = [];

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
        SELECT $source,{string.Join(",", Enumerable.Range(0, 17).Select(i => $"json_extract(input.value,'$[{i}]')"))}
        FROM jsonb_each(jsonb($rows)) input
        WHERE 1
        ORDER BY input.key
        ON CONFLICT(source_id,source_sequence) DO NOTHING
        ON CONFLICT(source_id,username,filename,visibility) DO NOTHING
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
        statement.Bind(1, result.Sequence);
        statement.Bind(2, result.Revision);
        statement.Bind(3, result.Username);
        statement.Bind(4, result.ResponseFileCount);
        statement.Bind(5, result.Filename);
        statement.Bind(6, result.Size);
        statement.Bind(7, (long?)result.BitRate);
        statement.Bind(8, (long?)result.BitDepth);
        statement.Bind(9, (long?)result.SampleRate);
        statement.Bind(10, (long?)result.Length);
        statement.Bind(11, result.Extension);
        statement.Bind(12, (long?)result.UploadSpeed);
        statement.Bind(13, result.HasFreeUploadSlot is bool free ? free ? 1L : 0L : (long?)null);
        statement.Bind(14, (long?)result.QueueLength);
        statement.Bind(15, prepared.AttributesJson);
        statement.Bind(16, result.ObservedAtUtc.ToUnixTimeMilliseconds());
        statement.Bind(17, result.Visibility.ToString());
        return statement.ExecuteScalarText(cancellationToken);
    }

    // One storage codec serves raw batches and retained
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

    public ValueTask DisposeAsync()
    {
        rawBatch?.Dispose();
        statement.Dispose();
        return ValueTask.CompletedTask;
    }
}
