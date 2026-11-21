using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Write;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Authentication;

public sealed record StoredApiKey(Guid Id, string Name, byte[] Hash, DateTimeOffset CreatedAt);
public sealed record CreatedApiKey(StoredApiKey Record, string Secret);

/// <summary>Key facts use the main database and writer; authentication uses a runtime snapshot.</summary>
public sealed class ApiKeyStore(IDbContextFactory<SockseekDbContext> readers, PersistenceInbox writes)
{
    public static async Task<T> OfflineAsync<T>(string databasePath,
        Func<ApiKeyStore, CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        var options = new SockseekSqliteOptions(databasePath);
        using var owner = SqliteDatabaseOwner.Acquire(options);
        try
        {
            var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
            await new SqliteInitializer(factory, options, owner).InitializeAsync(ct).ConfigureAwait(false);
            var health = new PersistenceHealth();
            var writerOptions = new PersistenceWriterOptions();
            var inbox = new PersistenceInbox(writerOptions, health);
            var writer = new PersistenceWriter(factory, inbox, health, writerOptions);
            var running = writer.RunAsync(CancellationToken.None);
            try { return await operation(new ApiKeyStore(factory, inbox), ct).ConfigureAwait(false); }
            finally
            {
                inbox.Complete();
                await running.ConfigureAwait(false);
            }
        }
        finally { SqliteConnectionPool.Clear(SockseekDbContextOptions.CreateConnectionString(options)); }
    }

    public async Task<IReadOnlyList<StoredApiKey>> ReadAllAsync(CancellationToken ct = default)
    {
        await using var db = await readers.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ApiKeys.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToArrayAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(x => new StoredApiKey(x.Id, x.Name, x.Hash,
            DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedAtUtc))).ToArray();
    }

    public async Task<CreatedApiKey> CreateAsync(string name, DateTimeOffset now, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is 0 or > 200)
            throw new ArgumentException("An API key name must contain between 1 and 200 characters.", nameof(name));
        var id = Guid.NewGuid();
        string secret = $"ssk_{Base64Url.EncodeToString(id.ToByteArray(bigEndian: true))}."
            + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var record = new StoredApiKey(id, name, SHA256.HashData(Encoding.UTF8.GetBytes(secret)), now);
        await writes.ExecuteAsync((db, _) =>
        {
            db.ApiKeys.Add(new ApiKeyEntity
            {
                Id = id, Name = name, Hash = record.Hash, CreatedAtUtc = now.ToUnixTimeMilliseconds(),
            });
            return Task.FromResult(true);
        }, ct).ConfigureAwait(false);
        return new(record, secret);
    }

    public Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
        => writes.ExecuteAsync(async (db, token) =>
            await db.ApiKeys.Where(x => x.Id == id).ExecuteDeleteAsync(token).ConfigureAwait(false) != 0, ct);

    public static bool TryGetId(string secret, out Guid id)
    {
        id = default;
        // Prefix + 22-character ID + separator + 43-character, 256-bit secret.
        if (secret.Length != 70 || !secret.StartsWith("ssk_", StringComparison.Ordinal) || secret[26] != '.')
            return false;
        Span<byte> bytes = stackalloc byte[16];
        if (Base64Url.DecodeFromChars(secret.AsSpan(4, 22), bytes, out _, out int written) != OperationStatus.Done
            || written != bytes.Length)
            return false;
        id = new Guid(bytes, bigEndian: true);
        return true;
    }

    public static bool Verify(string secret, StoredApiKey key)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret)), key.Hash);
}
