using Sockseek.Api;
using static Sockseek.Server.Planning.SearchViewCache;
using Sockseek.Core.Services;
using Sockseek.Core.Models;

namespace Sockseek.Server.Planning;

public sealed partial class SearchViewSnapshot
{
    internal static SearchViewCommitItem? TopItem(SearchViewSnapshot snapshot, SearchViewProjectionKind projection, string key)
        => projection switch
        {
            SearchViewProjectionKind.Files when snapshot.Files.TryGetValue(key, out var row) => new(key, row.Value, null),
            SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories when snapshot.Directories.TryGetValue(key, out var row)
                => new(key, null, row.Value),
            SearchViewProjectionKind.AggregateTracks when snapshot.Tracks.TryGetValue(key, out var row) => new(key, null, null, row.Value),
            SearchViewProjectionKind.AggregateAlbums when snapshot.Albums.TryGetValue(key, out var row)
                => new(key, null, null, null, Album(snapshot, row)),
            _ => null,
        };

    internal static IEnumerable<string> TopRefs(SearchViewSnapshot snapshot, SearchViewProjectionKind projection) => projection switch
    {
        SearchViewProjectionKind.Files => snapshot.Files.Keys,
        SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories => snapshot.Directories.Keys,
        SearchViewProjectionKind.AggregateTracks => snapshot.Tracks.Keys,
        SearchViewProjectionKind.AggregateAlbums => snapshot.Albums.Keys,
        _ => throw new ArgumentException("Unknown Search View projection.", nameof(projection)),
    };

    internal static SearchViewSelectionCounts ItemCounts(SearchViewCommitItem? item, SearchViewSnapshot snapshot)
    {
        if (item == null) return new(0, 0, 0);
        var file = item.File ?? item.AggregateTrackGroup?.Representative;
        if (file != null) return file.Input.Visibility == SearchResultVisibility.Public ? new(1, 0, 1) : new(0, 1, 1);
        var directory = snapshot.Directories[(item.Directory ?? item.AggregateAlbumGroup!.Representative).Ref];
        return new(directory.PublicCount, directory.LockedCount, 1);
    }

    public SearchViewSelectionCounts CountTopLevelSelection(RefSelectionMode mode, IReadOnlySet<string> refs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSelectionMode(mode);
        var projectionKind = View.ProjectionKind;
        var counts = mode == RefSelectionMode.Only ? new SearchViewSelectionCounts(0, 0) : Counts;
        foreach (string key in refs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = ItemCounts(TopItem(this, projectionKind, key), this);
            int sign = mode == RefSelectionMode.Only ? 1 : -1;
            counts = new(counts.PublicFileCount + sign * item.PublicFileCount,
                counts.LockedFileCount + sign * item.LockedFileCount, counts.SelectedItemCount + sign * item.SelectedItemCount);
        }
        return counts;
    }

    internal static void ValidateSelectionMode(RefSelectionMode mode)
    {
        if (mode is not (RefSelectionMode.Only or RefSelectionMode.AllExcept)) throw new ArgumentException("Selection mode must be Only or AllExcept.", nameof(mode));
    }

    internal static IEnumerable<string> DirectoryParents(SearchViewSnapshot snapshot, string fileRef)
        => (snapshot.Parents.GetValueOrDefault(fileRef) ?? []).Where(snapshot.Directories.ContainsKey);
    internal static IEnumerable<string> AlbumParents(SearchViewSnapshot snapshot, string directoryRef)
        => (snapshot.Parents.GetValueOrDefault(directoryRef) ?? [])
            .Where(key => snapshot.Albums.TryGetValue(key, out var album) && album.Options.Contains(directoryRef));

    public IReadOnlyList<SearchViewFileRefInfo> ReadDirectoryFileRefs(IReadOnlyCollection<string> refs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<SearchViewFileRefInfo>();
        foreach (string key in refs.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Files.TryGetValue(key, out var file))
                foreach (string directory in DirectoryParents(this, key))
                    rows.Add(new(key, directory, file.Value.Input.Visibility));
        }
        return rows;
    }

    public SearchViewSelectionCounts CountObservedDirectoryFiles(string directoryRef, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Directories.TryGetValue(directoryRef, out var directory)
            ? new SearchViewSelectionCounts(directory.PublicCount, directory.LockedCount)
            : new(0, 0);
    }

    public IEnumerable<SearchViewCommitItem> ReadCommitItems(RefSelectionMode mode, IReadOnlySet<string> refs,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSelectionMode(mode);
        var projectionKind = View.ProjectionKind;
        // Resolve containers first so acceptance can suppress separately selected
        // children. Ordinary explicit choices look up only the requested identities.
        foreach (string key in (mode == RefSelectionMode.Only ? refs : TopRefs(this, projectionKind)).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mode == RefSelectionMode.AllExcept && refs.Contains(key)) continue;
            if (TopItem(this, projectionKind, key) is { } item) yield return item;
        }
        if (mode != RefSelectionMode.Only || projectionKind == SearchViewProjectionKind.Files) yield break;
        if (projectionKind == SearchViewProjectionKind.AggregateAlbums)
            foreach (string key in refs.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directories.TryGetValue(key, out var directory)) continue;
                var parents = AlbumParents(this, key).Order(StringComparer.Ordinal).ToArray();
                if (parents.Length > 0)
                    yield return new(key, null, directory.Value, ContainerRefs: parents);
            }
        foreach (string key in refs.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Files.TryGetValue(key, out var file)) continue;
            if (projectionKind is SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories)
            {
                string? parent = DirectoryParents(this, key).Order(StringComparer.Ordinal).FirstOrDefault();
                if (parent != null) yield return new(key, file.Value, null, ParentRef: parent);
            }
            else if (projectionKind == SearchViewProjectionKind.AggregateTracks)
            {
                var parents = (Parents.GetValueOrDefault(key) ?? []).Where(Tracks.ContainsKey)
                    .Order(StringComparer.Ordinal).ToArray();
                if (parents.Length > 0) yield return new(key, file.Value, null, ContainerRefs: parents);
            }
            else
            {
                var containers = new HashSet<string>(StringComparer.Ordinal);
                foreach (string directory in DirectoryParents(this, key))
                    foreach (string parent in AlbumParents(this, directory))
                    {
                        containers.Add(directory);
                        containers.Add(parent);
                    }
                if (containers.Count > 0)
                    yield return new(key, file.Value, null, ContainerRefs: containers.Order(StringComparer.Ordinal).ToArray());
            }
        }
    }

    public IEnumerable<SearchViewFile> ReadPublicAggregateTrackOptions(string groupRef, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Tracks.TryGetValue(groupRef, out var group)) yield break;
        foreach (var position in group.Options)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Files[position.Ref].Value;
            if (file.Input.Visibility == SearchResultVisibility.Public) yield return file;
        }
    }
    public IEnumerable<SearchViewDirectory> ReadPublicAggregateAlbumOptions(string groupRef, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Albums.TryGetValue(groupRef, out var group)) yield break;
        foreach (var position in AlbumOptions(this, group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Directories[position.Ref];
            if (directory.Value.PublicMatchingFileCount > 0) yield return directory.Value;
        }
    }
    public IEnumerable<SearchViewDirectoryFile> ReadPublicDirectoryFiles(string directoryRef, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = StartActivity("directory-files", View.Id, Revision.Revision);
        if (!Directories.TryGetValue(directoryRef, out var directory)) yield break;
        foreach (var position in directory.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = DirectoryFile(this, position);
            if (file.File.Input.Visibility == SearchResultVisibility.Public) yield return file;
        }
    }
    public IEnumerable<SearchViewDirectoryFileMembership> ReadDirectoryFileMemberships(string directoryRef, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directories.TryGetValue(directoryRef, out var directory)) yield break;
        foreach (var position in directory.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(position.Ref, position.RelativePath, Files[position.Ref].Value.Input.Visibility);
        }
    }
}
