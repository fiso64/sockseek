using Sockseek.Api;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Persistence.PeerBrowsing;
using Sockseek.Server.PeerBrowsing;

namespace Sockseek.Server;

/// <summary>Reads the published local catalog using the same tree and filter as peer shares.</summary>
internal static class SharingBrowseEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/sharing/catalogs/{generationId:guid}").WithTags("Sharing");
        group.MapGet("/directories", (Guid generationId, long? parentId, string? cursor, int? limit,
            DaemonRuntime runtime, PeerBrowseCursorCodec cursors, CancellationToken ct) =>
            ReadAsync(runtime, generationId, tree =>
            {
                long? after = cursor is null ? null : cursors.DecodeRows(cursor,
                    PeerBrowseCursorKind.Directories, generationId, parentId, false, null);
                var previous = after is null ? null : tree.DirectoryEntry(after.Value);
                if (after.HasValue && (previous is null || previous.ParentId != parentId))
                    throw new ArgumentException("Invalid directory cursor.");
                var page = tree.ReadDirectories(parentId, null, false, previous?.DisplayPath, after,
                    PageSize(limit, ServerProtocol.DefaultNestedPageSize), ct);
                return Results.Ok(new PageDto<BrowseDirectoryEntryDto>(
                    page.Items.Select(UserBrowseDtoMapper.ToDto).ToArray(), page.NextId is { } id
                        ? cursors.EncodeRows(PeerBrowseCursorKind.Directories, generationId, parentId, false, null, id) : null));
            }, ct))
            .Produces<PageDto<BrowseDirectoryEntryDto>>()
            .Produces<ApiErrorDto>(400).Produces<ApiErrorDto>(409).Produces<ApiErrorDto>(503);

        group.MapGet("/directories/{directoryId:long}/files", (Guid generationId, long directoryId,
            string? cursor, int? limit, DaemonRuntime runtime, PeerBrowseCursorCodec cursors, CancellationToken ct) =>
            ReadAsync(runtime, generationId, tree =>
            {
                if (tree.DirectoryEntry(directoryId) is null)
                    return Results.NotFound(new ApiErrorDto("Folder not found.", "DirectoryNotFound"));
                long? after = cursor is null ? null : cursors.DecodeRows(cursor,
                    PeerBrowseCursorKind.Files, generationId, directoryId, false, null);
                var previous = after is null ? null : tree.FileEntry(after.Value);
                if (after.HasValue && (previous is null || previous.DirectoryId != directoryId))
                    throw new ArgumentException("Invalid file cursor.");
                var page = tree.ReadFiles(directoryId, null, previous?.Name, after,
                    PageSize(limit, ServerProtocol.DefaultPageSize), ct);
                return Results.Ok(new PageDto<BrowseFileEntryDto>(page.Items.Select(UserBrowseDtoMapper.ToDto).ToArray(),
                    page.NextId is { } id
                        ? cursors.EncodeRows(PeerBrowseCursorKind.Files, generationId, directoryId, false, null, id) : null));
            }, ct))
            .Produces<PageDto<BrowseFileEntryDto>>()
            .Produces<ApiErrorDto>(400).Produces<ApiErrorDto>(404).Produces<ApiErrorDto>(409).Produces<ApiErrorDto>(503);

        group.MapGet("/search", (Guid generationId, string query, string? cursor, int? limit,
            DaemonRuntime runtime, PeerBrowseCursorCodec cursors, CancellationToken ct) =>
            ReadAsync(runtime, generationId, tree =>
            {
                string normalized = query.Trim();
                if (normalized.Length is 0 or > 256) throw new ArgumentException("Enter a filter between 1 and 256 characters.");
                var after = cursor is null ? null : cursors.DecodeSearch(cursor, generationId, 1, normalized);
                string? key = after is null ? null : tree.SearchSortKey(after.Kind, after.EntryId);
                if (after is not null && key is null) throw new ArgumentException("Invalid filter cursor.");
                var page = tree.Search(normalized, key, after?.Kind, after?.EntryId,
                    PageSize(limit, ServerProtocol.DefaultNestedPageSize), ct);
                return Results.Ok(new BrowseSearchPageDto(generationId, 1, normalized,
                    page.Items.Select(UserBrowseDtoMapper.ToDto).ToArray(), page.PublicMatchingFileCount,
                    page.PublicMatchingBytes, page.LockedMatchingFileCount, page.LockedMatchingBytes,
                    page.NextId is { } id ? cursors.EncodeSearch(generationId, 1, normalized, page.NextKind!.Value, id) : null));
            }, ct))
            .Produces<BrowseSearchPageDto>()
            .Produces<ApiErrorDto>(400).Produces<ApiErrorDto>(409).Produces<ApiErrorDto>(503);
    }

    private static int PageSize(int? requested, int fallback)
    {
        int size = requested ?? fallback;
        if (size <= 0) throw new ArgumentException("Page size must be positive.");
        return size;
    }

    private static async Task<IResult> ReadAsync(DaemonRuntime runtime, Guid generation,
        Func<PeerBrowseArtifact, IResult> read, CancellationToken ct)
    {
        if (runtime.Sharing is not { } sharing)
            return Results.Json(new ApiErrorDto("Sharing is unavailable.", "SharingUnavailable"), statusCode: 503);
        var tree = await sharing.GetBrowserAsync(generation, ct).ConfigureAwait(false);
        if (tree is null)
            return Results.Conflict(new ApiErrorDto("Shares have changed. Refresh the share browser.", "ShareCatalogChanged"));
        try { return read(tree); }
        catch (ArgumentException ex) { return ApiErrors.BadRequest(ex); }
    }
}
