using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Sockseek.Persistence.Planning;
using Sockseek.Api;

namespace Sockseek.Server.Planning;

public sealed class SearchViewCursorCodec
{
    private readonly string path;
    private readonly object keyGate = new();
    private byte[]? key;

    public SearchViewCursorCodec(IOptions<ServerOptions> options)
    {
        string dataDirectory = SockseekDataPaths.ResolveDataDirectory(
            options.Value.Persistence.DataDirectory);
        path = Path.Combine(dataDirectory, "planning", "search-view-cursor.key");
    }

    public void Initialize() => _ = RequiredKey();

    public string EncodeFiles(
        Guid viewId,
        long revision,
        SearchViewFilePosition position,
        ServerSearchViewSortOrder? sortOrder = null)
        => Encode(new CursorPayload(
            1, "files", viewId, revision, position, null, null, null) { SortOrder = sortOrder });

    public SearchViewFilePosition DecodeFiles(
        string cursor,
        Guid viewId,
        long revision,
        ServerSearchViewSortOrder? sortOrder = null)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "files"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || payload.SortOrder != sortOrder
            || payload.FilePosition == null)
            throw Invalid();
        return payload.FilePosition;
    }

    public string EncodeDirectories(
        Guid viewId,
        long revision,
        SearchViewDirectoryPosition position,
        ServerSearchViewSortOrder? sortOrder = null)
        => Encode(new CursorPayload(
            1, "directories", viewId, revision, null, position, null, null) { SortOrder = sortOrder });

    public SearchViewDirectoryPosition DecodeDirectories(
        string cursor,
        Guid viewId,
        long revision,
        ServerSearchViewSortOrder? sortOrder = null)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "directories"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || payload.SortOrder != sortOrder
            || payload.DirectoryPosition == null)
            throw Invalid();
        return payload.DirectoryPosition;
    }

    public string EncodeDirectoryFiles(
        Guid viewId,
        string directoryRef,
        long revision,
        SearchViewDirectoryFilePosition position)
        => Encode(new CursorPayload(
            1, "directory-files", viewId, revision, null, null, position, directoryRef));

    public SearchViewDirectoryFilePosition DecodeDirectoryFiles(
        string cursor,
        Guid viewId,
        string directoryRef,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "directory-files"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || !string.Equals(payload.ParentRef, directoryRef, StringComparison.Ordinal)
            || payload.DirectoryFilePosition == null)
            throw Invalid();
        return payload.DirectoryFilePosition;
    }

    public string EncodeAggregateTracks(
        Guid viewId,
        long revision,
        SearchViewAggregateTrackPosition position)
        => Encode(new CursorPayload(
            1, "aggregate-tracks", viewId, revision, null, null, null, null)
        {
            AggregateTrackPosition = position,
        });

    public SearchViewAggregateTrackPosition DecodeAggregateTracks(
        string cursor,
        Guid viewId,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "aggregate-tracks"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || payload.AggregateTrackPosition == null)
            throw Invalid();
        return payload.AggregateTrackPosition;
    }

    public string EncodeAggregateTrackOptions(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewFilePosition position)
        => Encode(new CursorPayload(
            1, "aggregate-track-options", viewId, revision,
            position, null, null, groupRef));

    public SearchViewFilePosition DecodeAggregateTrackOptions(
        string cursor,
        Guid viewId,
        string groupRef,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "aggregate-track-options"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || !string.Equals(payload.ParentRef, groupRef, StringComparison.Ordinal)
            || payload.FilePosition == null)
            throw Invalid();
        return payload.FilePosition;
    }

    public string EncodeAggregateAlbums(
        Guid viewId,
        long revision,
        SearchViewAggregateAlbumPosition position)
        => Encode(new CursorPayload(
            1, "aggregate-albums", viewId, revision, null, null, null, null)
        {
            AggregateAlbumPosition = position,
        });

    public SearchViewAggregateAlbumPosition DecodeAggregateAlbums(
        string cursor,
        Guid viewId,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "aggregate-albums"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || payload.AggregateAlbumPosition == null)
            throw Invalid();
        return payload.AggregateAlbumPosition;
    }

    public string EncodeAggregateAlbumOptions(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewDirectoryPosition position)
        => Encode(new CursorPayload(
            1, "aggregate-album-options", viewId, revision,
            null, position, null, groupRef));

    public SearchViewDirectoryPosition DecodeAggregateAlbumOptions(
        string cursor,
        Guid viewId,
        string groupRef,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "aggregate-album-options"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || !string.Equals(payload.ParentRef, groupRef, StringComparison.Ordinal)
            || payload.DirectoryPosition == null)
            throw Invalid();
        return payload.DirectoryPosition;
    }

    private string Encode(CursorPayload payload)
        => AuthenticatedCursorCodec.Encode(payload, RequiredKey());

    private CursorPayload Decode(string cursor)
    {
        CursorPayload payload = AuthenticatedCursorCodec.Decode<CursorPayload>(
            cursor,
            RequiredKey(),
            "search-view");
        return payload.Version == 1 ? payload : throw Invalid();
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The search-view cursor key has no parent directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.Length != 32)
                throw new InvalidDataException("The search-view cursor key is invalid.");
            return existing;
        }
        byte[] created = RandomNumberGenerator.GetBytes(32);
        using (var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough))
        {
            stream.Write(created);
            stream.Flush(flushToDisk: true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return created;
    }

    private byte[] RequiredKey()
    {
        lock (keyGate)
            return key ??= LoadOrCreateKey(path);
    }

    private static ArgumentException Invalid(Exception? inner = null)
        => new("The search-view cursor is invalid.", "cursor", inner);

    private sealed record CursorPayload(
        int Version,
        string Kind,
        Guid ViewId,
        long Revision,
        SearchViewFilePosition? FilePosition,
        SearchViewDirectoryPosition? DirectoryPosition,
        SearchViewDirectoryFilePosition? DirectoryFilePosition,
        string? ParentRef)
    {
        public ServerSearchViewSortOrder? SortOrder { get; init; }
        public SearchViewAggregateTrackPosition? AggregateTrackPosition { get; init; }
        public SearchViewAggregateAlbumPosition? AggregateAlbumPosition { get; init; }
    }
}
