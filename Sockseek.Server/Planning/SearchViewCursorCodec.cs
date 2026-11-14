using System.Security.Cryptography;
using Sockseek.Persistence.Planning;
using Sockseek.Api;

namespace Sockseek.Server.Planning;

public sealed class SearchViewCursorCodec
{
    // Cursors belong to this process's temporary snapshots. Restart discards
    // both; no disk key or independent initialization lifecycle is needed.
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

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

    public string EncodeTrackGroups(
        Guid viewId,
        long revision,
        SearchViewTrackGroupPosition position)
        => Encode(new CursorPayload(
            1, "track-groups", viewId, revision, null, null, null, null)
        {
            TrackGroupPosition = position,
        });

    public SearchViewTrackGroupPosition DecodeTrackGroups(
        string cursor,
        Guid viewId,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "track-groups"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || payload.TrackGroupPosition == null)
            throw Invalid();
        return payload.TrackGroupPosition;
    }

    public string EncodeTrackGroupOptions(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewFilePosition position)
        => Encode(new CursorPayload(
            1, "track-group-options", viewId, revision,
            position, null, null, groupRef));

    public SearchViewFilePosition DecodeTrackGroupOptions(
        string cursor,
        Guid viewId,
        string groupRef,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "track-group-options"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || !string.Equals(payload.ParentRef, groupRef, StringComparison.Ordinal)
            || payload.FilePosition == null)
            throw Invalid();
        return payload.FilePosition;
    }

    public string EncodeAlbumGroups(
        Guid viewId,
        long revision,
        SearchViewAlbumGroupPosition position)
        => Encode(new CursorPayload(
            1, "album-groups", viewId, revision, null, null, null, null)
        {
            AlbumGroupPosition = position,
        });

    public SearchViewAlbumGroupPosition DecodeAlbumGroups(
        string cursor,
        Guid viewId,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "album-groups"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || payload.AlbumGroupPosition == null)
            throw Invalid();
        return payload.AlbumGroupPosition;
    }

    public string EncodeAlbumGroupOptions(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewDirectoryPosition position)
        => Encode(new CursorPayload(
            1, "album-group-options", viewId, revision,
            null, position, null, groupRef));

    public SearchViewDirectoryPosition DecodeAlbumGroupOptions(
        string cursor,
        Guid viewId,
        string groupRef,
        long revision)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Kind != "album-group-options"
            || payload.ViewId != viewId
            || payload.Revision != revision
            || !string.Equals(payload.ParentRef, groupRef, StringComparison.Ordinal)
            || payload.DirectoryPosition == null)
            throw Invalid();
        return payload.DirectoryPosition;
    }

    private string Encode(CursorPayload payload)
        => AuthenticatedCursorCodec.Encode(payload, key);

    private CursorPayload Decode(string cursor)
    {
        CursorPayload payload = AuthenticatedCursorCodec.Decode<CursorPayload>(
            cursor,
            key,
            "search-view");
        return payload.Version == 1 ? payload : throw Invalid();
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
        public SearchViewTrackGroupPosition? TrackGroupPosition { get; init; }
        public SearchViewAlbumGroupPosition? AlbumGroupPosition { get; init; }
    }
}
