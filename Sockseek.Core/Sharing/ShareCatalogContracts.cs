using System.Text.Json.Serialization;

namespace Sockseek.Core.Sharing;

public static class ShareCatalogVersions
{
    public const int Schema = 7;
    public const int BrowseWire = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<ShareBrowseStatus>))]
public enum ShareBrowseStatus
{
    Ready,
    UnavailableOversize,
}

[JsonConverter(typeof(JsonStringEnumConverter<ShareScanPhase>))]
public enum ShareScanPhase
{
    Idle,
    Preparing,
    Enumerating,
    FinalizingIndex,
    BuildingBrowseArtifact,
    Validating,
    Publishing,
    Completed,
    Cancelling,
    Cancelled,
    Failed,
}

public sealed record ShareCatalogMetadata(
    Guid GenerationId,
    DateTimeOffset CreatedAtUtc,
    string SettingsHash,
    long DirectoryCount,
    long FileCount,
    long TotalBytes,
    ShareBrowseStatus BrowseStatus,
    int? BrowseWireVersion,
    long? BrowseLengthBytes,
    string? BrowseSha256);

public sealed record ShareCatalogRoot(
    long RootId,
    string Alias,
    string LocalPath,
    RemotePathKey ComparisonAlias);

public sealed record ShareCatalogDirectory(
    long DirectoryId,
    long RootId,
    // Native filesystem spelling, relative to the configured root; never a wire path.
    string RelativePath,
    string RemotePath,
    RemotePathKey ComparisonPath);

public sealed record ShareFileAttribute(int Type, int Value);

public sealed record ShareCatalogFile(
    long FileId,
    long RootId,
    long DirectoryId,
    // Native filesystem spelling, relative to the configured root; never a wire path.
    string RelativePath,
    string RemotePath,
    RemotePathKey ComparisonPath,
    string SearchText,
    long SizeBytes,
    DateTimeOffset ModifiedAtUtc,
    int ProtocolCode,
    string Extension,
    IReadOnlyList<ShareFileAttribute> Attributes);

public sealed record ShareCatalogBrowseDirectory(
    ShareCatalogDirectory Directory,
    IReadOnlyList<ShareCatalogFile> Files);

public abstract record ShareCatalogBrowseRow;

public sealed record ShareCatalogBrowseDirectoryRow(
    ShareCatalogDirectory Directory,
    int FileCount) : ShareCatalogBrowseRow;

public sealed record ShareCatalogBrowseFileRow(
    ShareCatalogFile File) : ShareCatalogBrowseRow;

public sealed record ShareCatalogResolvedFile(
    ShareCatalogRoot Root,
    ShareCatalogFile File);

/// <summary>
/// Indicates that one catalog entry cannot be represented because its remote
/// identity collides with an entry already written to the staging generation.
/// Scanners may isolate this entry and continue with unrelated content.
/// </summary>
public class ShareCatalogEntryCollisionException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

public interface IShareCatalogReader : IAsyncDisposable
{
    ShareCatalogMetadata Metadata { get; }

    ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
        RemotePathKey remotePath,
        CancellationToken cancellationToken = default);

    /// <summary>Returns up to limit wire files after excluding ordinal case-insensitive path substrings.</summary>
    ValueTask<IReadOnlyList<Soulseek.File>> SearchAsync(
        string query,
        int limit,
        IReadOnlyList<string> exclusions,
        CancellationToken cancellationToken = default);

    ValueTask<ShareCatalogBrowseDirectory?> GetDirectoryAsync(
        RemotePathKey remotePath,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ShareCatalogBrowseRow> EnumerateBrowseRowsAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ShareBrowseStream(long Length, Stream Stream);

public interface IShareCatalogLease : IAsyncDisposable, IDisposable
{
    IShareCatalogReader Reader { get; }

    ShareCatalogMetadata Metadata { get; }

    ShareBrowseStream OpenBrowseStream(
        TimeSpan idleTimeout,
        Action? releasePermit = null);
}

public interface IShareCatalogProvider
{
    bool TryAcquire(out IShareCatalogLease? lease);
}

public interface IShareCatalogGenerationWriter : IAsyncDisposable
{
    string DatabasePath { get; }

    ValueTask AddRootAsync(
        ShareCatalogRoot root,
        CancellationToken cancellationToken = default);

    ValueTask AddDirectoryAsync(
        ShareCatalogDirectory directory,
        CancellationToken cancellationToken = default);

    ValueTask AddFileAsync(
        ShareCatalogFile file,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all catalog rows so a staging reader can build the immutable
    /// browse artifact. The generation is still unpublished and writable only
    /// for its final metadata row.
    /// </summary>
    ValueTask PrepareForReadAsync(
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        ShareCatalogMetadata metadata,
        CancellationToken cancellationToken = default);
}

public sealed record ShareBrowseArtifact(
    string Path,
    long Length,
    string Sha256,
    int WireVersion);

public sealed class BrowseArtifactOversizeException(long length, long maximumLength)
    : IOException(
        $"Browse artifact length {length} exceeds the serving limit {maximumLength}.");

public interface ISoulseekBrowseArtifactBuilder
{
    ValueTask<ShareBrowseArtifact> BuildAsync(
        IShareCatalogReader catalog,
        string outputPath,
        CancellationToken cancellationToken = default);
}
