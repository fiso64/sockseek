using Sockseek.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Storage;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Planning;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

// Store tests use the production transaction owner, not a direct-write fallback.
internal sealed class TestPersistenceWriter : IAsyncDisposable
{
    private readonly Task running;
    public PersistenceInbox Inbox { get; }
    public PersistenceHealth Health { get; }

    public TestPersistenceWriter(IDbContextFactory<SockseekDbContext> factory, PersistenceHealth? health = null)
    {
        Health = health ?? new();
        var options = new PersistenceWriterOptions();
        Inbox = new PersistenceInbox(options, Health);
        running = new PersistenceWriter(factory, Inbox, Health, options).RunAsync(CancellationToken.None);
    }

    internal static async Task SeedSearchResultAsync(SockseekDbContext context, SearchResultEntity result)
    {
        // Only fixture construction bypasses the queue; use the production raw
        // writer so tests cannot accidentally seed a second fact representation.
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await using var observations = await SearchObservationWriter.ForRawHistoryAsync(
            (SqliteConnection)context.Database.GetDbConnection(),
            (SqliteTransaction)transaction.GetDbTransaction(), result.SearchJobId, CancellationToken.None);
        var input = new SearchProjectionInput(result.Sequence, checked((int)result.Revision),
            result.Username, result.ResponseFileCount, result.RemoteFilename, result.SizeBytes,
            result.BitRate, result.BitDepth, result.SampleRate, result.DurationSeconds,
            result.Extension, result.UploadSpeed, result.HasFreeUploadSlot,
            SearchObservationWriter.ReadAttributes(result.AttributesJson),
            DateTimeOffset.FromUnixTimeMilliseconds(result.ObservedAtUtc), result.QueueLength,
            Enum.Parse<SearchResultVisibility>(result.Visibility));
        observations.StoreRaw(new SearchObservationWriter.Prepared(input, result.AttributesJson),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Inbox.Complete();
        await running;
    }
}

// Most store tests start from retained facts rather than an executing search.
// Seed those facts explicitly; publication itself must never create originals.
internal static class SearchViewFixtures
{
    internal static async Task<StoredSearchView> PublishSeededAsync(this SearchViewStore store,
        PersistenceInbox writes, Guid viewId, Sockseek.Core.Services.SearchViewKernelUpdate update,
        string retentionState, CancellationToken cancellationToken = default)
    {
        var view = await store.GetAsync(viewId, cancellationToken)
            ?? throw new InvalidOperationException("Fixture View was not found.");
        await writes.ExecuteAsync(async (context, ct) =>
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            var transaction = (SqliteTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
            await using var owner = connection.CreateCommand();
            owner.Transaction = transaction;
            owner.CommandText = """
                INSERT INTO search_observation_sources(source_job_id) VALUES ($source) ON CONFLICT DO NOTHING;
                SELECT id FROM search_observation_sources WHERE source_job_id=$source;
                """;
            owner.Parameters.AddWithValue("$source", view.SourceJobId.ToString("D").ToUpperInvariant());
            long source = (long)(await owner.ExecuteScalarAsync(ct))!;
            using var insert = new Sockseek.Persistence.Sqlite.SqliteWriteStatement(connection, transaction, """
                INSERT INTO search_observations(source_id,origin_kind,origin_id,source_sequence,source_revision,
                    username,response_file_count,filename,size_bytes,bit_rate,bit_depth,sample_rate,duration_seconds,
                    extension,upload_speed,has_free_upload_slot,queue_length,attributes_json,observed_at_utc,visibility)
                VALUES ($source,0,'',$sequence,$revision,$username,$count,$filename,$size,$bitrate,$depth,$rate,$duration,
                    $extension,$speed,$free,$queue,$attributes,$observed,$visibility)
                ON CONFLICT DO NOTHING;
                """);
            insert.Bind(0, source);
            foreach (var file in update.ChangedFiles)
            {
                var input = file.Input;
                insert.BindValues([input.Sequence, input.Revision, input.Username, input.ResponseFileCount,
                    input.Filename, input.Size, input.BitRate, input.BitDepth, input.SampleRate, input.Length,
                    input.Extension, input.UploadSpeed, input.HasFreeUploadSlot is bool free ? (free ? 1 : 0) : null,
                    input.QueueLength, input.Attributes == null ? null : SearchObservationWriter.SerializeAttributes(input.Attributes),
                    input.ObservedAtUtc.ToUnixTimeMilliseconds(), input.Visibility.ToString()], 1);
                insert.Execute(ct);
            }
            return 0;
        }, cancellationToken);
        return await store.PublishAsync(viewId, update, retentionState, cancellationToken);
    }
}
