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
        await using var observations = await SearchResultWriter.ForRawHistoryAsync(
            (SqliteConnection)context.Database.GetDbConnection(),
            (SqliteTransaction)transaction.GetDbTransaction(), result.SearchJobId, CancellationToken.None);
        var input = new SearchProjectionInput(result.Sequence, checked((int)result.Revision),
            result.Username, result.ResponseFileCount, result.RemoteFilename, result.SizeBytes,
            result.BitRate, result.BitDepth, result.SampleRate, result.DurationSeconds,
            result.Extension, result.UploadSpeed, result.HasFreeUploadSlot,
            SearchResultWriter.ReadAttributes(result.AttributesJson),
            DateTimeOffset.FromUnixTimeMilliseconds(result.ObservedAtUtc), result.QueueLength,
            Enum.Parse<SearchResultVisibility>(result.Visibility));
        observations.StoreRaw(new SearchResultWriter.Prepared(input, result.AttributesJson),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Inbox.Complete();
        await running;
    }
}
