using System.Buffers;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Planning;

public sealed record StoredInputArtifact(
    string Id,
    string Sha256,
    long Length,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? OriginalName);

public sealed record ResolvedInputArtifact(
    StoredInputArtifact Artifact,
    string Path);

/// <summary>
/// Immutable disk-spooled browser input. Blob files remain owner-managed, while
/// metadata uses the main database. Active readers retain blobs only for this
/// daemon session; accepted submissions contain their extracted input already.
/// </summary>
public sealed class InputArtifactStore(
    string directory,
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceHealth health,
    PersistenceInbox writes,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    private const int MaintenanceBatchSize = 256;
    private readonly string root = Path.GetFullPath(directory);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim uploadSlots = new(2, 2);
    // The existing writer serializes retention changes with pruning. Counts also
    // support independent metadata reads without queuing them behind the writer.
    private readonly Dictionary<Guid, HashSet<string>> owners = [];
    private readonly ConcurrentDictionary<string, int> retained = new(StringComparer.Ordinal);
    private int initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
            return;
        try
        {
            Directory.CreateDirectory(root);
            PersistenceFilePrivacy.RestrictDirectory(root);
            foreach (string temporary in Directory.EnumerateFiles(root, "*.uploading"))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await DeleteOrphanBlobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref initialized, 0);
            throw;
        }
    }

    public async Task<StoredInputArtifact> CreateAsync(
        Stream source,
        string? originalName,
        TimeSpan? retention = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(source);
        TimeSpan effectiveRetention = retention ?? DefaultRetention;
        if (effectiveRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));

        await uploadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        string id = Guid.NewGuid().ToString("N");
        string temporaryPath = Path.Combine(root, id + ".uploading");
        string finalPath = BlobPath(id);
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                await using var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    length = checked(length + read);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            File.Move(temporaryPath, finalPath);
            PersistenceFilePrivacy.RestrictFile(finalPath);
            DateTimeOffset created = clock.GetUtcNow();
            var artifact = new StoredInputArtifact(
                id,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                length,
                created,
                created + effectiveRetention,
                SafeName(originalName));
            try
            {
                await WithWriteAsync((context, _) =>
                {
                    context.InputArtifacts.Add(ToEntity(artifact));
                    return Task.CompletedTask;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { File.Delete(finalPath); } catch { }
                throw;
            }
            return artifact;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }
            uploadSlots.Release();
        }
    }

    public async Task<ResolvedInputArtifact?> ResolveAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(artifactId);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        InputArtifactEntity? row = await ObserveAsync(async () =>
        {
            await using SockseekDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await context.InputArtifacts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    artifact => artifact.Id == artifactId,
                    cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (row is null || row.ExpiresAtUtc <= now && !retained.ContainsKey(artifactId))
            return null;
        return Resolve(row);
    }

    /// <summary>
    /// Atomically retains the requested inputs against expiry/pruning. No durable
    /// write is needed: previews and in-flight admission cannot survive restart.
    /// Repeated acquisition by the same owner is idempotent.
    /// </summary>
    public Task<IReadOnlyDictionary<string, ResolvedInputArtifact>> RetainAsync(
        IEnumerable<string> artifactIds,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        string[] ids = artifactIds.Distinct(StringComparer.Ordinal).ToArray();
        foreach (string id in ids)
            ValidateId(id);
        return writes.ExecuteAsync<IReadOnlyDictionary<string, ResolvedInputArtifact>>(async (context, ct) =>
        {
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            InputArtifactEntity[] rows = await context.InputArtifacts.AsNoTracking()
                .Where(artifact => ids.Contains(artifact.Id))
                .ToArrayAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<string, ResolvedInputArtifact>(StringComparer.Ordinal);
            foreach (InputArtifactEntity row in rows)
            {
                if (row.ExpiresAtUtc > now || retained.ContainsKey(row.Id))
                    result.Add(row.Id, Resolve(row));
            }
            if (result.Count != ids.Length)
                throw new ArgumentException("An input artifact was not found or has expired.");
            ct.ThrowIfCancellationRequested();
            if (ids.Length > 0)
            {
                if (!owners.TryGetValue(ownerId, out HashSet<string>? owned))
                    owners.Add(ownerId, owned = new(StringComparer.Ordinal));
                foreach (string id in ids)
                    if (owned.Add(id))
                        retained.AddOrUpdate(id, 1, (_, count) => checked(count + 1));
            }
            return result;
        }, cancellationToken, requiresTransaction: false);
    }

    public Task<int> ReleaseAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return writes.ExecuteAsync((_, _) =>
        {
            if (!owners.Remove(ownerId, out HashSet<string>? ids))
                return Task.FromResult(0);
            foreach (string id in ids)
            {
                if (retained[id] == 1)
                    retained.TryRemove(id, out _);
                else
                    retained[id]--;
            }
            return Task.FromResult(ids.Count);
        }, cancellationToken, requiresTransaction: false);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        int total = 0;
        while (true)
        {
            string[] expired = await WithWriteAsync(async (context, ct) =>
            {
                long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                string[] active = retained.Keys.ToArray();
                string[] ids = await context.InputArtifacts
                    .Where(artifact => artifact.ExpiresAtUtc <= now
                        && !active.Contains(artifact.Id))
                    .OrderBy(artifact => artifact.ExpiresAtUtc)
                    .ThenBy(artifact => artifact.Id)
                    .Select(artifact => artifact.Id)
                    .Take(MaintenanceBatchSize)
                    .ToArrayAsync(ct).ConfigureAwait(false);
                if (ids.Length > 0)
                {
                    await context.InputArtifacts
                        .Where(artifact => ids.Contains(artifact.Id))
                        .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }
                return ids;
            }, cancellationToken, background: true).ConfigureAwait(false);
            if (expired.Length == 0)
                return total;
            total += expired.Length;
            foreach (string id in expired)
            {
                try { File.Delete(BlobPath(id)); }
                catch (FileNotFoundException) { }
            }
        }
    }

    private async Task DeleteOrphanBlobsAsync(CancellationToken cancellationToken)
    {
        foreach (string[] paths in Directory.EnumerateFiles(root, "*.blob")
                     .Chunk(MaintenanceBatchSize))
        {
            string[] ids = paths
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .ToArray();
            string[] retained = await ObserveAsync(async () =>
            {
                await using SockseekDbContext context = await contextFactory
                    .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                return await context.InputArtifacts
                    .Where(artifact => ids.Contains(artifact.Id))
                    .Select(artifact => artifact.Id)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var retainedSet = retained.ToHashSet(StringComparer.Ordinal);
            for (int index = 0; index < paths.Length; index++)
            {
                if (retainedSet.Contains(ids[index]))
                    continue;
                try { File.Delete(paths[index]); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task WithWriteAsync(
        Func<SockseekDbContext, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => await WithWriteAsync(async (context, ct) =>
        {
            await action(context, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private Task<T> WithWriteAsync<T>(
        Func<SockseekDbContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        bool background = false)
        => writes.ExecuteAsync(action, cancellationToken, background);

    private async Task<T> ObserveAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is DbException or DbUpdateException)
                health.RecordOperationalFailure(clock.GetUtcNow(), exception);
            throw;
        }
    }

    private string BlobPath(string id) => Path.Combine(root, id + ".blob");

    private static InputArtifactEntity ToEntity(StoredInputArtifact artifact)
        => new()
        {
            Id = artifact.Id,
            Sha256 = artifact.Sha256,
            Length = artifact.Length,
            CreatedAtUtc = artifact.CreatedAtUtc.ToUnixTimeMilliseconds(),
            ExpiresAtUtc = artifact.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            OriginalName = artifact.OriginalName,
        };

    private static StoredInputArtifact FromEntity(InputArtifactEntity artifact)
        => new(
            artifact.Id,
            artifact.Sha256,
            artifact.Length,
            DateTimeOffset.FromUnixTimeMilliseconds(artifact.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(artifact.ExpiresAtUtc),
            artifact.OriginalName);

    private static string? SafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string name = Path.GetFileName(value.Trim());
        name = new string(name.Where(character => !char.IsControl(character)).ToArray());
        if (name.Length > 255)
            name = name[..255];
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static void ValidateId(string id)
    {
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("The input artifact ID is invalid.", nameof(id));
    }

    private ResolvedInputArtifact Resolve(InputArtifactEntity row)
    {
        string path = BlobPath(row.Id);
        if (!File.Exists(path))
            throw new InvalidDataException(
                "The input artifact metadata exists but its immutable content is missing.");
        return new(FromEntity(row), path);
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new InvalidOperationException("The input-artifact store is unavailable.");
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref initialized, 0);
        uploadSlots.Dispose();
        return ValueTask.CompletedTask;
    }
}
