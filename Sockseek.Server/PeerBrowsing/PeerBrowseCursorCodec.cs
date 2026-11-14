using System.Security.Cryptography;
using Sockseek.Api;
using Sockseek.Core.PeerBrowsing;

namespace Sockseek.Server.PeerBrowsing;

/// <summary>
/// Process-local authenticated cursors. A row cursor carries only the immutable
/// row ID; the corresponding sort value is recovered from the immutable artifact,
/// keeping cursors bounded even when a peer advertises a very long path.
/// </summary>
public sealed class PeerBrowseCursorCodec
{
    private readonly byte[] key;

    public PeerBrowseCursorCodec()
        : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    internal PeerBrowseCursorCodec(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
            throw new ArgumentException("The cursor signing key must contain at least 32 bytes.", nameof(key));
        this.key = key.ToArray();
    }

    public string EncodeRows(
        PeerBrowseCursorKind kind,
        Guid browseId,
        long? parentId,
        bool recursive,
        string? query,
        long lastId)
    {
        if (kind is not (PeerBrowseCursorKind.Directories or PeerBrowseCursorKind.Files))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return Encode(new CursorPayload(1, kind, browseId, parentId, recursive, query, lastId));
    }

    public long DecodeRows(
        string cursor,
        PeerBrowseCursorKind expectedKind,
        Guid expectedBrowseId,
        long? expectedParentId,
        bool expectedRecursive,
        string? expectedQuery)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Version != 1
            || payload.Kind != expectedKind
            || payload.BrowseId != expectedBrowseId
            || payload.ParentId != expectedParentId
            || payload.Recursive != expectedRecursive
            || !string.Equals(payload.Query, expectedQuery, StringComparison.Ordinal)
            || payload.LastId is null or <= 0
            || payload.CreatedAt is not null
            || payload.ResourceId is not null
            || payload.Username is not null
            || payload.State is not null
            || payload.BrowseRevision is not null
            || payload.SearchEntryKind is not null)
        {
            throw InvalidCursor();
        }
        return payload.LastId.Value;
    }

    public string EncodeResources(
        string? username,
        UserBrowseState? state,
        DateTimeOffset createdAt,
        Guid browseId)
        => Encode(new CursorPayload(
            1,
            PeerBrowseCursorKind.Resources,
            Username: username,
            State: state,
            CreatedAt: createdAt,
            ResourceId: browseId));

    public PeerBrowseResourceCursor DecodeResources(
        string cursor,
        string? expectedUsername,
        UserBrowseState? expectedState)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Version != 1
            || payload.Kind != PeerBrowseCursorKind.Resources
            || !string.Equals(payload.Username, expectedUsername, StringComparison.Ordinal)
            || payload.State != expectedState
            || payload.CreatedAt is null
            || payload.ResourceId is null
            || payload.ResourceId == Guid.Empty
            || payload.BrowseId is not null
            || payload.ParentId is not null
            || payload.LastId is not null
            || payload.Query is not null
            || payload.Recursive
            || payload.BrowseRevision is not null
            || payload.SearchEntryKind is not null)
        {
            throw InvalidCursor();
        }
        return new PeerBrowseResourceCursor(payload.CreatedAt.Value, payload.ResourceId.Value);
    }

    public string EncodeSearch(
        Guid browseId,
        long browseRevision,
        string query,
        PeerBrowseSearchEntryKind kind,
        long lastId)
        => Encode(new CursorPayload(
            1,
            PeerBrowseCursorKind.Search,
            BrowseId: browseId,
            Query: query,
            LastId: lastId,
            BrowseRevision: browseRevision,
            SearchEntryKind: kind));

    public PeerBrowseSearchCursor DecodeSearch(
        string cursor,
        Guid expectedBrowseId,
        long expectedBrowseRevision,
        string expectedQuery)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Version != 1
            || payload.Kind != PeerBrowseCursorKind.Search
            || payload.BrowseId != expectedBrowseId
            || payload.BrowseRevision != expectedBrowseRevision
            || !string.Equals(payload.Query, expectedQuery, StringComparison.Ordinal)
            || payload.SearchEntryKind is null
            || payload.LastId is null or <= 0
            || payload.ParentId is not null
            || payload.Recursive
            || payload.Username is not null
            || payload.State is not null
            || payload.CreatedAt is not null
            || payload.ResourceId is not null)
        {
            throw InvalidCursor();
        }
        return new PeerBrowseSearchCursor(payload.SearchEntryKind.Value, payload.LastId.Value);
    }

    private string Encode(CursorPayload payload)
        => AuthenticatedCursorCodec.Encode(payload, key);

    private CursorPayload Decode(string value)
        => AuthenticatedCursorCodec.Decode<CursorPayload>(
            value,
            key,
            "peer browse");

    private static ArgumentException InvalidCursor(Exception? inner = null)
        => new("The peer browse cursor is invalid.", "cursor", inner);

    private sealed record CursorPayload(
        int Version,
        PeerBrowseCursorKind Kind,
        Guid? BrowseId = null,
        long? ParentId = null,
        bool Recursive = false,
        string? Query = null,
        long? LastId = null,
        string? Username = null,
        UserBrowseState? State = null,
        DateTimeOffset? CreatedAt = null,
        Guid? ResourceId = null,
        long? BrowseRevision = null,
        PeerBrowseSearchEntryKind? SearchEntryKind = null);
}

public enum PeerBrowseCursorKind
{
    Resources,
    Directories,
    Files,
    Search,
}

public sealed record PeerBrowseResourceCursor(DateTimeOffset CreatedAt, Guid BrowseId);

public sealed record PeerBrowseSearchCursor(PeerBrowseSearchEntryKind Kind, long EntryId);
