using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using Sockseek.Persistence.Sqlite;
using System.Runtime.CompilerServices;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Persistence.Read;

public sealed record PersistedSearchMetadata(
    Guid JobId,
    string Query,
    long Revision,
    long ResultCount,
    long LockedFileCount,
    long ObservedPeerCount,
    bool IsComplete,
    DateTimeOffset? CompletedAtUtc,
    string ResultPersistenceState,
    DateTimeOffset? ResultsPrunedAtUtc);

public sealed record PersistedSearchResultPage(
    PersistedSearchMetadata Metadata,
    IReadOnlyList<SearchProjectionInput> Items,
    long? NextSequence);

public sealed record PersistedSearchResultLookup(
    PersistedSearchMetadata Metadata,
    SearchProjectionInput? Result);

public interface ISearchHistoryReader
{
    Task<PersistedSearchResultPage?> GetRawResultsAsync(Guid searchJobId, long afterSequence, int limit, CancellationToken cancellationToken = default);
    Task<PersistedSearchResultLookup?> GetResultAsync(Guid searchJobId, string username, string remoteFilename, CancellationToken cancellationToken = default);
    Task<PersistedSearchMetadata?> GetMetadataAsync(Guid searchJobId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SearchProjectionInput> ReadProjectionInputsAsync(Guid searchJobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SearchProjectionInput>> GetFolderResultsAsync(Guid searchJobId,
        PeerDirectoryIdentity directory, AlbumQuery query, SearchResultVisibility visibility,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads retained search facts directly, without building intermediate entities or
/// compiling LINQ queries on the first search. Pages and exact lookups share the
/// same row mapping; metadata and page contents belong to one WAL snapshot.
/// </summary>
public sealed class SearchHistoryReader(IDbContextFactory<SockseekDbContext> contextFactory) : ISearchHistoryReader
{
    private const string ResultColumns = """
        fact.source_sequence, fact.source_revision, fact.username, fact.filename, fact.size_bytes,
        fact.bit_rate, fact.bit_depth, fact.response_file_count, fact.sample_rate, fact.duration_seconds,
        fact.extension, fact.upload_speed, fact.has_free_upload_slot, fact.attributes_json,
        fact.observed_at_utc, fact.queue_length, fact.visibility
        """;

    // Sequence paging and peer lookup need different access paths. An ORDER BY
    // on the logical result view made SQLite prefer a full-source sequence scan
    // even for one exact peer. Keep source ownership/availability here, and make
    // the peer seek explicit before sorting only that peer's observations.
    private static string SourceFacts(bool peerLookup = false) => $"""
        FROM search_observation_sources source
        CROSS JOIN search_observations fact {(peerLookup ? "INDEXED BY ix_search_observations_raw_identity" : "")}
        WHERE source.search_job_id=$job AND fact.source_id=source.id
        """;

    public async Task<PersistedSearchResultPage?> GetRawResultsAsync(
        Guid searchJobId, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var snapshot = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var metadata = await ReadMetadataAsync(context, searchJobId, cancellationToken).ConfigureAwait(false);
        if (metadata == null) return null;

        await using var command = Command(context, searchJobId, $"""
            SELECT {ResultColumns} {SourceFacts()}
            AND fact.source_sequence>$after ORDER BY fact.source_sequence LIMIT $limit;
            """);
        Add(command, "$after", afterSequence);
        Add(command, "$limit", limit == int.MaxValue ? limit : limit + 1);
        var rows = new List<SearchProjectionInput>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(ReadInput(reader));
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        await snapshot.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(metadata, rows, hasMore && rows.Count > 0 ? rows[^1].Sequence : null);
    }

    public async Task<PersistedSearchResultLookup?> GetResultAsync(
        Guid searchJobId,
        string username,
        string remoteFilename,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteFilename);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var snapshot = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var metadata = await ReadMetadataAsync(context, searchJobId, cancellationToken).ConfigureAwait(false);
        if (metadata == null) return null;

        await using var command = Command(context, searchJobId, $"""
            SELECT {ResultColumns} {SourceFacts(peerLookup: true)}
            AND fact.username=$user AND fact.filename=$filename AND fact.visibility='Public';
            """);
        Add(command, "$user", username);
        Add(command, "$filename", remoteFilename);
        SearchProjectionInput? result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadInput(reader) : null;
        // Exact lookup selects one download target. Validate its complete wire
        // metadata now, while bulk replay keeps attribute decoding deferred.
        _ = result?.Attributes?.Count;
        await snapshot.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(metadata, result);
    }

    public async Task<PersistedSearchMetadata?> GetMetadataAsync(Guid searchJobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadMetadataAsync(context, searchJobId, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SearchProjectionInput> ReadProjectionInputsAsync(
        Guid searchJobId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(context, searchJobId, $"""
            SELECT {ResultColumns} {SourceFacts()} ORDER BY fact.source_sequence;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadInput(reader);
    }

    public async Task<IReadOnlyList<SearchProjectionInput>> GetFolderResultsAsync(
        Guid searchJobId, PeerDirectoryIdentity directory, AlbumQuery query, SearchResultVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(query);
        var scope = new AlbumFolderInputScope(query, directory.FolderPath);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(context, searchJobId, $"""
            SELECT {ResultColumns} {SourceFacts(peerLookup: true)}
            AND fact.username=$username AND fact.visibility=$visibility;
            """);
        Add(command, "$username", directory.Username);
        Add(command, "$visibility", visibility.ToString());
        var rows = new List<SearchProjectionInput>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Core owns filename/disc/ancestor policy. Unrelated albums need
                // no complete metadata, attributes or condition/ranking work.
                string filename = reader.GetString(3);
                if (scope.Includes(filename)) rows.Add(ReadInput(reader, filename));
            }
        // Preserve source order without sorting wide SQL rows into temp files.
        rows.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return rows;
    }

    private static async Task<PersistedSearchMetadata?> ReadMetadataAsync(
        SockseekDbContext context, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = Command(context, jobId, """
            SELECT job_id, query, revision, result_count, locked_file_count, observed_peer_count,
                is_complete, completed_at_utc, result_persistence_state, results_pruned_at_utc
            FROM search_jobs WHERE job_id=$job;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetBoolean(6), Date(reader, 7),
                reader.GetString(8), Date(reader, 9)) : null;
    }

    // One immutable representation crosses collection, retained reads and
    // projection. IDs belong to the source/page, not another per-file DTO.
    private static SearchProjectionInput ReadInput(DbDataReader reader)
        => ReadInput(reader, reader.GetString(3));

    private static SearchProjectionInput ReadInput(DbDataReader reader, string filename)
        => new(reader.GetInt64(0), checked((int)reader.GetInt64(1)), reader.GetString(2),
            reader.GetInt32(7), filename, reader.GetInt64(4), Int(reader, 5), Int(reader, 6),
            Int(reader, 8), Int(reader, 9), reader.GetString(10), Int(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetBoolean(12),
            Write.SearchResultWriter.ReadAttributes(reader.IsDBNull(13) ? null : reader.GetString(13), defer: true),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)), Int(reader, 15),
            Enum.TryParse<SearchResultVisibility>(reader.GetString(16), out var visibility) ? visibility : SearchResultVisibility.Public);

    private static int? Int(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static DateTimeOffset? Date(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    private static DbCommand Command(SockseekDbContext context, Guid jobId, string sql)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        Add(command, "$job", jobId);
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
