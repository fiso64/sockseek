using System.Text.Json;
using System.Text.Json.Serialization;
using Sockseek.Core.IO;
using Sockseek.Core.Sharing;

namespace Sockseek.Persistence.Sharing;

public sealed record ShareCatalogPublication(
    Guid GenerationId,
    string DatabasePath,
    string BrowseArtifactPath,
    ShareCatalogMetadata Metadata);

public sealed record ShareCatalogPublicationTiming(
    TimeSpan ValidationElapsed,
    TimeSpan PublicationElapsed);

public sealed class ShareCatalogLease : IShareCatalogLease
{
    private readonly ShareCatalogManager manager;
    private readonly ShareCatalogManager.GenerationHandle handle;
    private int released;
    private int browseOwnershipTransferred;

    internal ShareCatalogLease(
        ShareCatalogManager manager,
        ShareCatalogManager.GenerationHandle handle)
    {
        this.manager = manager;
        this.handle = handle;
    }

    public IShareCatalogReader Reader
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref released) != 0, this);
            return handle.Reader;
        }
    }

    public ShareCatalogMetadata Metadata => Reader.Metadata;

    public ShareBrowseStream OpenBrowseStream(
        TimeSpan idleTimeout,
        Action? releasePermit = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref released) != 0, this);
        if (Interlocked.Exchange(ref browseOwnershipTransferred, 1) != 0)
            throw new InvalidOperationException("This catalog lease already owns a browse stream.");
        if (handle.Metadata.BrowseStatus != ShareBrowseStatus.Ready
            || handle.Metadata.BrowseLengthBytes is not { } length)
        {
            throw new InvalidOperationException("The current catalog has no browse artifact.");
        }

        try
        {
            var file = new FileStream(
                handle.ArtifactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1_024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != length)
            {
                file.Dispose();
                throw new InvalidDataException("Browse artifact length changed after publication.");
            }

            var exact = new ExactLengthReadStream(file, length);
            // Soulseek.NET 10.0.2 disposes RawBrowseResponse.Stream only after a
            // successful network write. Keep this lease-expiry workaround
            // isolated here and remove it when the pinned dependency disposes
            // the raw stream in a finally block.
            var expiring = new SelfExpiringReadStream(
                exact,
                idleTimeout,
                () =>
                {
                    try
                    {
                        releasePermit?.Invoke();
                    }
                    finally
                    {
                        Release();
                    }
                });
            return new ShareBrowseStream(length, expiring);
        }
        catch
        {
            Interlocked.Exchange(ref browseOwnershipTransferred, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref browseOwnershipTransferred) == 0)
            Release();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
            manager.Release(handle);
    }
}

/// <summary>
/// Owns immutable SQLite/artifact generations and atomically publishes one
/// current manifest while allowing outstanding leases to drain.
/// </summary>
public sealed class ShareCatalogManager : IAsyncDisposable, IShareCatalogProvider
{
    private const string ManifestFileName = "current.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object sync = new();
    private readonly string directory;
    private readonly string manifestPath;
    private GenerationHandle? current;
    private ManifestGeneration? rollback;
    private readonly HashSet<GenerationHandle> retired = [];
    private bool disposed;

    public ShareCatalogManager(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        manifestPath = Path.Combine(this.directory, ManifestFileName);
    }

    public string DirectoryPath => directory;

    public bool IsReady
    {
        get
        {
            lock (sync)
                return current is not null;
        }
    }

    public ShareCatalogMetadata? CurrentMetadata
    {
        get
        {
            lock (sync)
                return current?.Metadata;
        }
    }

    public (string DatabasePath, string ArtifactPath) GetGenerationPaths(Guid generationId)
        => (
            Path.Combine(directory, $"share-index-{generationId:D}.sqlite3"),
            Path.Combine(directory, $"browse-{generationId:D}.bin"));

    public async ValueTask<bool> InitializeAsync(
        string expectedSettingsHash,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        OwnerOnlyFilePermissions.EnsureDirectory(directory);
        if (!File.Exists(manifestPath))
        {
            await CleanupOrphansAsync([], cancellationToken).ConfigureAwait(false);
            return false;
        }
        OwnerOnlyFilePermissions.EnsureFile(manifestPath);

        ShareCatalogManifest manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1_024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer
                .DeserializeAsync<ShareCatalogManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Share catalog manifest is empty.");
            ValidateManifestVersion(manifest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await CleanupOrphansAsync([], cancellationToken).ConfigureAwait(false);
            return false;
        }

        GenerationHandle? loaded = await TryLoadAsync(
            manifest.Current,
            expectedSettingsHash,
            cancellationToken).ConfigureAwait(false);
        ManifestGeneration? selectedManifest = manifest.Current;

        if (loaded is null && manifest.Previous is not null)
        {
            loaded = await TryLoadAsync(
                manifest.Previous,
                expectedSettingsHash,
                cancellationToken).ConfigureAwait(false);
            selectedManifest = loaded is null ? null : manifest.Previous;
        }

        lock (sync)
        {
            current = loaded;
            rollback = selectedManifest == manifest.Current ? manifest.Previous : null;
        }

        var retained = new List<Guid>();
        if (selectedManifest is not null)
            retained.Add(selectedManifest.GenerationId);
        if (rollback is not null)
            retained.Add(rollback.GenerationId);
        await CleanupOrphansAsync(retained, cancellationToken).ConfigureAwait(false);
        return loaded is not null;
    }

    public bool TryAcquire(out IShareCatalogLease? lease)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is null)
            {
                lease = null;
                return false;
            }

            current.LeaseCount++;
            lease = new ShareCatalogLease(this, current);
            return true;
        }
    }

    public async ValueTask<ShareCatalogPublicationTiming> PublishAsync(
        ShareCatalogPublication publication,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidatePublicationPaths(publication);
        var duration = System.Diagnostics.Stopwatch.StartNew();
        GenerationHandle next = await LoadRequiredAsync(
            ToManifest(publication),
            publication.Metadata.SettingsHash,
            cancellationToken).ConfigureAwait(false);
        duration.Stop();
        TimeSpan validationElapsed = duration.Elapsed;
        duration.Restart();

        ManifestGeneration? oldCurrent;
        lock (sync)
            oldCurrent = current is null ? null : ToManifest(current);

        var manifest = new ShareCatalogManifest(
            ShareCatalogVersions.Schema,
            ToManifest(next),
            oldCurrent);
        string temporaryManifest = manifestPath + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporaryManifest,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1_024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            OwnerOnlyFilePermissions.EnsureFile(temporaryManifest);
            File.Move(temporaryManifest, manifestPath, overwrite: true);
            OwnerOnlyFilePermissions.EnsureFile(manifestPath);
        }
        catch
        {
            await next.Reader.DisposeAsync().ConfigureAwait(false);
            if (File.Exists(temporaryManifest))
                File.Delete(temporaryManifest);
            throw;
        }

        GenerationHandle? oldHandle;
        ManifestGeneration? oldRollback;
        GenerationHandle? oldRollbackHandle;
        lock (sync)
        {
            oldHandle = current;
            oldRollback = rollback;
            current = next;
            rollback = oldCurrent;

            if (oldHandle is not null)
                retired.Add(oldHandle);
            oldRollbackHandle = oldRollback is null
                ? null
                : retired.FirstOrDefault(
                    handle => handle.Metadata.GenerationId == oldRollback.GenerationId);
        }

        if (oldRollback is not null
            && oldRollbackHandle is null
            && (oldCurrent is null || oldRollback.GenerationId != oldCurrent.GenerationId))
        {
            TryDeleteGeneration(oldRollback);
        }

        ReleaseAllRetiredIfDrained();
        duration.Stop();
        return new ShareCatalogPublicationTiming(
            validationElapsed,
            duration.Elapsed);
    }

    internal void Release(GenerationHandle handle)
    {
        lock (sync)
        {
            if (handle.LeaseCount <= 0)
                throw new InvalidOperationException("Catalog generation lease underflow.");
            handle.LeaseCount--;
        }
        ReleaseRetiredIfDrained(handle);
    }

    private void ReleaseRetiredIfDrained(GenerationHandle handle)
    {
        bool shouldDispose;
        lock (sync)
        {
            shouldDispose = retired.Contains(handle)
                            && handle.LeaseCount == 0
                            && rollback?.GenerationId != handle.Metadata.GenerationId;
            if (shouldDispose)
                retired.Remove(handle);
        }

        if (shouldDispose)
        {
            handle.Reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            TryDeleteGeneration(ToManifest(handle));
        }
    }

    private void ReleaseAllRetiredIfDrained()
    {
        GenerationHandle[] snapshot;
        lock (sync)
            snapshot = retired.ToArray();

        foreach (var handle in snapshot)
            ReleaseRetiredIfDrained(handle);
    }

    private async ValueTask<GenerationHandle?> TryLoadAsync(
        ManifestGeneration? generation,
        string expectedSettingsHash,
        CancellationToken cancellationToken)
    {
        if (generation is null)
            return null;
        try
        {
            return await LoadRequiredAsync(
                generation,
                expectedSettingsHash,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async ValueTask<GenerationHandle> LoadRequiredAsync(
        ManifestGeneration generation,
        string expectedSettingsHash,
        CancellationToken cancellationToken)
    {
        string databasePath = ResolveOwnedFile(
            generation.DatabaseFileName,
            $"share-index-{generation.GenerationId:D}.sqlite3");
        string artifactPath = ResolveOwnedFile(
            generation.ArtifactFileName,
            $"browse-{generation.GenerationId:D}.bin");
        OwnerOnlyFilePermissions.EnsureFile(databasePath);

        var reader = await SqliteShareCatalogReader
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (reader.Metadata.GenerationId != generation.GenerationId)
                throw new InvalidDataException("Catalog generation ID does not match its manifest.");
            if (!string.Equals(
                    reader.Metadata.SettingsHash,
                    expectedSettingsHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Catalog settings hash is stale.");
            }
            if (reader.Metadata.BrowseStatus == ShareBrowseStatus.UnavailableOversize)
            {
                if (reader.Metadata.BrowseWireVersion is not null
                    || reader.Metadata.BrowseLengthBytes is not null
                    || reader.Metadata.BrowseSha256 is not null)
                {
                    throw new InvalidDataException(
                        "Unavailable browse metadata must not retain artifact fields.");
                }
                TryDelete(artifactPath);
                return new GenerationHandle(reader, artifactPath);
            }
            if (reader.Metadata.BrowseStatus != ShareBrowseStatus.Ready
                || reader.Metadata.BrowseLengthBytes is not { } expectedLength
                || reader.Metadata.BrowseSha256 is null)
            {
                throw new InvalidDataException("Published catalog browse metadata is incomplete.");
            }

            var artifactInfo = new FileInfo(artifactPath);
            if (!artifactInfo.Exists || artifactInfo.Length != expectedLength)
                throw new InvalidDataException("Browse artifact length does not match catalog metadata.");
            OwnerOnlyFilePermissions.EnsureFile(artifactPath);

            return new GenerationHandle(reader, artifactPath);
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string ResolveOwnedFile(string fileName, string expectedFileName)
    {
        if (!string.Equals(fileName, expectedFileName, StringComparison.Ordinal)
            || Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidDataException("Share catalog manifest contains an invalid file name.");
        }

        return Path.Combine(directory, fileName);
    }

    private void ValidatePublicationPaths(ShareCatalogPublication publication)
    {
        if (publication.GenerationId != publication.Metadata.GenerationId)
            throw new ArgumentException("Publication generation IDs do not match.");

        var expected = GetGenerationPaths(publication.GenerationId);
        if (!string.Equals(
                Path.GetFullPath(publication.DatabasePath),
                expected.DatabasePath,
                LocalPathComparison)
            || !string.Equals(
                Path.GetFullPath(publication.BrowseArtifactPath),
                expected.ArtifactPath,
                LocalPathComparison))
        {
            throw new ArgumentException("Publication files are outside the owned generation paths.");
        }
    }

    private async ValueTask CleanupOrphansAsync(
        IReadOnlyCollection<Guid> retained,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
            return;

        var retainedSet = retained.ToHashSet();
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);

            if (name == ManifestFileName)
                continue;
            if (name == ManifestFileName + ".tmp")
            {
                TryDelete(path);
                continue;
            }

            if (TryParseGenerationFile(name, out Guid generationId)
                && !retainedSet.Contains(generationId))
            {
                TryDelete(path);
            }
        }

        await Task.CompletedTask;
    }

    private static bool TryParseGenerationFile(string name, out Guid generationId)
    {
        generationId = default;
        const string databasePrefix = "share-index-";
        const string artifactPrefix = "browse-";
        string? id = name switch
        {
            _ when name.StartsWith(databasePrefix, StringComparison.Ordinal)
                   && name.EndsWith(".sqlite3", StringComparison.Ordinal)
                => name[databasePrefix.Length..^".sqlite3".Length],
            _ when name.StartsWith(artifactPrefix, StringComparison.Ordinal)
                   && name.EndsWith(".bin", StringComparison.Ordinal)
                => name[artifactPrefix.Length..^".bin".Length],
            _ => null,
        };
        return id is not null && Guid.TryParseExact(id, "D", out generationId);
    }

    private void TryDeleteGeneration(ManifestGeneration generation)
    {
        TryDelete(Path.Combine(directory, generation.DatabaseFileName));
        TryDelete(Path.Combine(directory, generation.ArtifactFileName));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Retried by startup orphan cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Retried by startup orphan cleanup.
        }
    }

    private static ManifestGeneration ToManifest(ShareCatalogPublication publication)
        => new(
            publication.GenerationId,
            Path.GetFileName(publication.DatabasePath),
            Path.GetFileName(publication.BrowseArtifactPath));

    private static ManifestGeneration ToManifest(GenerationHandle handle)
        => new(
            handle.Metadata.GenerationId,
            Path.GetFileName(handle.Reader.DatabasePath),
            Path.GetFileName(handle.ArtifactPath));

    private static void ValidateManifestVersion(ShareCatalogManifest manifest)
    {
        if (manifest.SchemaVersion != ShareCatalogVersions.Schema)
        {
            throw new InvalidDataException("Share catalog manifest version is unsupported.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<GenerationHandle> handles;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            handles = retired.ToList();
            if (current is not null)
                handles.Add(current);
            retired.Clear();
            current = null;
        }

        foreach (var handle in handles.Distinct())
            await handle.Reader.DisposeAsync().ConfigureAwait(false);
    }

    private static StringComparison LocalPathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal sealed class GenerationHandle(
        SqliteShareCatalogReader reader,
        string artifactPath)
    {
        public SqliteShareCatalogReader Reader { get; } = reader;
        public ShareCatalogMetadata Metadata => Reader.Metadata;
        public string ArtifactPath { get; } = artifactPath;
        public int LeaseCount { get; set; }
    }

    private sealed record ShareCatalogManifest(
        int SchemaVersion,
        ManifestGeneration Current,
        ManifestGeneration? Previous);

    private sealed record ManifestGeneration(
        Guid GenerationId,
        string DatabaseFileName,
        string ArtifactFileName);
}
