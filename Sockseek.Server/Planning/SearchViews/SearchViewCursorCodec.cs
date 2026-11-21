using System.Security.Cryptography;
using Sockseek.Api;

namespace Sockseek.Server.Planning;

public sealed class SearchViewCursorCodec
{
    // A restart discards both the temporary snapshots and their cursor key.
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    internal Page<SearchViewFilePosition> Files(Guid viewId, long revision, ServerSearchViewSortOrder? sortOrder = null)
        => new(key, new("files", viewId, revision, SortOrder: sortOrder));

    internal Page<SearchViewDirectoryPosition> Directories(Guid viewId, long revision, ServerSearchViewSortOrder? sortOrder = null)
        => new(key, new("directories", viewId, revision, SortOrder: sortOrder));

    internal Page<SearchViewDirectoryFilePosition> DirectoryFiles(Guid viewId, long revision, string parentRef)
        => new(key, new("directory-files", viewId, revision, ParentRef: parentRef));

    internal Page<SearchViewTrackGroupPosition> TrackGroups(Guid viewId, long revision)
        => new(key, new("track-groups", viewId, revision));

    internal Page<SearchViewFilePosition> TrackGroupOptions(Guid viewId, long revision, string parentRef)
        => new(key, new("track-group-options", viewId, revision, ParentRef: parentRef));

    internal Page<SearchViewAlbumGroupPosition> AlbumGroups(Guid viewId, long revision)
        => new(key, new("album-groups", viewId, revision));

    internal Page<SearchViewDirectoryPosition> AlbumGroupOptions(Guid viewId, long revision, string parentRef)
        => new(key, new("album-group-options", viewId, revision, ParentRef: parentRef));

    internal readonly record struct Scope(string Kind, Guid ViewId, long Revision,
        string? ParentRef = null, ServerSearchViewSortOrder? SortOrder = null);

    private sealed record Cursor<T>(Scope Scope, T Position);

    internal readonly struct Page<T>(byte[] key, Scope scope) where T : class
    {
        public string? Encode(T? position) => position is null ? null
            : AuthenticatedCursorCodec.Encode(new Cursor<T>(scope, position), key);

        public T? Decode(string? cursor)
        {
            if (cursor is null) return null;
            var payload = AuthenticatedCursorCodec.Decode<Cursor<T>>(cursor, key, "search-view");
            if (payload.Scope != scope || payload.Position is null)
                throw new ArgumentException("The search-view cursor is invalid.", nameof(cursor));
            return payload.Position;
        }
    }
}
