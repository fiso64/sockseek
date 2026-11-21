using Sockseek.Core;
using Sockseek.Api;
using static Sockseek.Server.Planning.SearchViewCache;
using System.Collections.Immutable;
using System.Globalization;
using Sockseek.Core.Services;
using Sockseek.Core.Models;

namespace Sockseek.Server.Planning;

/// <summary>
/// One published result set. Requests retain this reference through paging or
/// selection resolution; cache expiry cannot invalidate an acquired snapshot.
/// Membership is immutable after publication. Only expiry metadata and derived
/// indexes may change, under their respective cache/index locks.
/// </summary>
public sealed partial class SearchViewSnapshot
{
    public SearchViewInfo View { get; }
    public SearchViewRevision Revision { get; }

    internal SearchViewSnapshot(SearchViewInfo view)
    {
        View = view;
        Revision = SearchViewCache.Revision(view);
        Expires = view.ExpiresAtUtc;
    }
    internal DateTimeOffset Expires;
    internal ImmutableDictionary<string, FileRow> Files = ImmutableDictionary<string, FileRow>.Empty;
    internal ImmutableDictionary<string, DirectoryRow> Directories = ImmutableDictionary<string, DirectoryRow>.Empty;
    // A group may disappear below the sharing/filter threshold and later
    // return. Retain its last members without exposing them in that interval.
    internal ImmutableDictionary<string, DirectoryRow> HiddenDirectories = ImmutableDictionary<string, DirectoryRow>.Empty;
    internal ImmutableDictionary<string, AlbumRow> HiddenAlbums = ImmutableDictionary<string, AlbumRow>.Empty;
    internal ImmutableDictionary<string, TrackRow> Tracks = ImmutableDictionary<string, TrackRow>.Empty;
    internal ImmutableDictionary<string, AlbumRow> Albums = ImmutableDictionary<string, AlbumRow>.Empty;
    internal ImmutableDictionary<string, ImmutableHashSet<string>> Parents = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;
    internal ImmutableHashSet<string> Peers = ImmutableHashSet<string>.Empty;
    // Only text-filtered File Search views retain omitted public files. Index folders
    // by their exact peer paths so a promotion never needs to browse the peer.
    internal ImmutableDictionary<PeerPathKey, ImmutableDictionary<string, SearchProjectionInput>> FilteredFiles = ImmutableDictionary<PeerPathKey, ImmutableDictionary<string, SearchProjectionInput>>.Empty;
    internal ImmutableDictionary<PeerPathKey, ImmutableHashSet<PeerPathKey>> FilteredFolders = ImmutableDictionary<PeerPathKey, ImmutableHashSet<PeerPathKey>>.Empty;

    internal void AddFilteredFiles(IEnumerable<SearchProjectionInput> files, CancellationToken token)
    {
        var byFolder = FilteredFiles.ToBuilder();
        var byAncestor = FilteredFolders.ToBuilder();
        foreach (var group in files.GroupBy(file => new PeerPathKey(file.Username, ParentPath(file.Filename) ?? "")))
        {
            token.ThrowIfCancellationRequested();
            var key = group.Key;
            bool existing = byFolder.TryGetValue(key, out var prior);
            var bucket = (prior ?? ImmutableDictionary<string, SearchProjectionInput>.Empty).ToBuilder();
            foreach (var file in group)
            {
                token.ThrowIfCancellationRequested();
                if (key.RemotePath.Length != 0 || file.Filename.IndexOfAny(['\\', '/']) < 0)
                    bucket[file.Filename] = file;
            }
            if (bucket.Count == 0) continue;
            byFolder[key] = bucket.ToImmutable();
            if (existing) continue;
            for (string? path = key.RemotePath; path != null; path = ParentPath(path))
            {
                var ancestor = new PeerPathKey(key.Username, path);
                var children = byAncestor.GetValueOrDefault(ancestor) ?? ImmutableHashSet<PeerPathKey>.Empty;
                byAncestor[ancestor] = children.Add(key);
            }
        }
        FilteredFiles = byFolder.ToImmutable();
        FilteredFolders = byAncestor.ToImmutable();
    }

    private static string? ParentPath(string path)
    {
        int last = path.LastIndexOfAny(['\\', '/']);
        return last <= 0 ? null : path[..last];
    }

    internal bool HasFilteredFiles(SearchViewDirectory directory)
        => FilteredFolders.ContainsKey(new(directory.Username, directory.FolderPath));

    internal string? DeepestFilteredFolder(SearchViewDirectory directory, string filename)
    {
        if (FilteredFolders.Count == 0) return null;
        for (string? path = ParentPath(filename); path != null && path.Length > directory.FolderPath.Length; path = ParentPath(path))
            if (FilteredFolders.ContainsKey(new(directory.Username, path)))
                return RelativePath(directory.FolderPath, path).Replace('\\', '/');
        return null;
    }

    internal IEnumerable<SearchProjectionInput> ReadFilteredFiles(SearchViewDirectory directory, CancellationToken token)
    {
        if (!FilteredFolders.TryGetValue(new(directory.Username, directory.FolderPath), out var buckets)) yield break;
        foreach (var key in buckets)
            foreach (var file in FilteredFiles[key].Values)
            {
                token.ThrowIfCancellationRequested();
                yield return file;
            }
    }

    internal SearchViewSelectionCounts Counts = new(0, 0, 0);
    internal readonly Lock IndexGate = new();
    internal readonly Dictionary<SearchViewSortOrder, ImmutableSortedSet<SearchViewFilePosition>> FileIndexes = [];
    private readonly Dictionary<SearchViewSortOrder, SearchViewFilePosition[]> completedFileOrders = [];
    internal readonly Dictionary<SearchViewSortOrder, ImmutableSortedSet<SearchViewDirectoryPosition>> DirectoryIndexes = [];
    internal readonly Dictionary<string, ImmutableSortedSet<SearchViewDirectoryPosition>> AlbumOptionIndexes = [];
    internal ImmutableSortedSet<SearchViewTrackGroupPosition> TrackIndex = ImmutableSortedSet.Create(TrackComparer);
    internal ImmutableSortedSet<SearchViewAlbumGroupPosition> AlbumIndex = ImmutableSortedSet.Create(AlbumComparer);

    // Positions carry the exact keys used by their immutable index. Seeking never
    // scans earlier pages, and does not consult the latest projection revision.
    internal static (T[] Items, T? Next) Page<T>(ImmutableSortedSet<T> index, T? after, int limit) where T : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        int start = PageStart(index, after);
        int count = Math.Min(limit, index.Count - start);
        var items = new T[count];
        for (int i = 0; i < count; i++) items[i] = index[start + i];
        return (items, start + count < index.Count && count > 0 ? items[^1] : null);
    }

    private static int PageStart<T>(ImmutableSortedSet<T> index, T? after) where T : class
    {
        if (after == null) return 0;
        int position = index.IndexOf(after);
        return position >= 0 ? position + 1 : ~position;
    }

    private static (T[] Items, T? Next, int[]? Positions) OptionPage<T>(
        ImmutableSortedSet<T> index, T? after, int limit, SearchViewGroupOptionQuery? query,
        Func<string, T?> positionOf, Func<T, string, bool> matches, CancellationToken cancellationToken) where T : class
    {
        if (query != null && (query.PreviousTo ?? query.NextTo) is { } anchor)
        {
            if (after != null || !string.IsNullOrWhiteSpace(query.FilterText) || (query.PreviousTo != null && query.NextTo != null))
                throw new ArgumentException("An adjacent option requires a single anchor and no cursor or filter.");
            int current = positionOf(anchor) is { } key ? index.IndexOf(key) : -1;
            if (current < 0) throw new ArgumentException("The anchor does not belong to this group revision.");
            int neighbor = current + (query.PreviousTo != null ? -1 : 1);
            return neighbor >= 0 && neighbor < index.Count ? ([index[neighbor]], null, [neighbor + 1]) : ([], null, []);
        }
        if (string.IsNullOrWhiteSpace(query?.FilterText))
        {
            var page = Page(index, after, limit);
            return (page.Items, page.Next, null);
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        string filter = query.FilterText.Trim();
        List<T> items = [];
        List<int> positions = [];
        for (int i = PageStart(index, after); i < index.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            T item = index[i];
            if (!matches(item, filter)) continue;
            if (items.Count == limit) return ([.. items], items[^1], [.. positions]);
            items.Add(item);
            positions.Add(i + 1);
        }
        return ([.. items], null, [.. positions]);
    }

    internal static readonly IComparer<SearchViewDirectoryFilePosition> ChildComparer =
        Comparer<SearchViewDirectoryFilePosition>.Create((a, b) =>
        {
            int result = StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Ref, b.Ref);
        });
    internal static readonly IComparer<SearchViewTrackGroupPosition> TrackComparer =
        Comparer<SearchViewTrackGroupPosition>.Create((a, b) => CompareGroup(a.ShareCount, a.Index, a.Ref, b.ShareCount, b.Index, b.Ref));
    internal static readonly IComparer<SearchViewAlbumGroupPosition> AlbumComparer =
        Comparer<SearchViewAlbumGroupPosition>.Create((a, b) => CompareGroup(a.ShareCount, a.Index, a.Ref, b.ShareCount, b.Index, b.Ref));

    internal static int CompareGroup(int aCount, int aIndex, string aRef, int bCount, int bIndex, string bRef)
    {
        int result = bCount.CompareTo(aCount);
        if (result == 0) result = aIndex.CompareTo(bIndex);
        return result != 0 ? result : StringComparer.Ordinal.Compare(aRef, bRef);
    }

    internal static ImmutableSortedSet<SearchViewDirectoryFilePosition> ChildSet() => ImmutableSortedSet.Create(ChildComparer);
    internal static ImmutableSortedSet<SearchViewFilePosition> FileSet(SearchViewSortOrder order)
        => ImmutableSortedSet.Create<SearchViewFilePosition>(FileComparer(order));

    private static IComparer<SearchViewFilePosition> FileComparer(SearchViewSortOrder order)
    {
        if (order is not (SearchViewSortOrder.Relevance or SearchViewSortOrder.UploadSpeed
            or SearchViewSortOrder.QueueDepth or SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending))
            throw new ArgumentOutOfRangeException(nameof(order));
        return Comparer<SearchViewFilePosition>.Create((a, b) =>
        {
            int result = order switch
            {
                SearchViewSortOrder.ItemSizeAscending => a.Size.CompareTo(b.Size),
                SearchViewSortOrder.ItemSizeDescending => b.Size.CompareTo(a.Size),
                SearchViewSortOrder.UploadSpeed => b.UploadSpeed.CompareTo(a.UploadSpeed),
                SearchViewSortOrder.QueueDepth => a.QueueLength.CompareTo(b.QueueLength),
                _ => SearchProjectionSortKeyComparer.Instance.Compare(a.Rank, b.Rank),
            };
            if (result == 0 && order == SearchViewSortOrder.QueueDepth)
                result = b.UploadSpeed.CompareTo(a.UploadSpeed);
            if (result == 0) result = a.Sequence.CompareTo(b.Sequence);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Ref, b.Ref);
        });
    }
    internal static ImmutableSortedSet<SearchViewDirectoryPosition> DirectorySet(SearchViewSortOrder order)
    {
        if (!Enum.IsDefined(order)) throw new ArgumentOutOfRangeException(nameof(order));
        return ImmutableSortedSet.Create<SearchViewDirectoryPosition>(Comparer<SearchViewDirectoryPosition>.Create((a, b) =>
        {
            int result;
            if (order == SearchViewSortOrder.Relevance)
            {
                result = SearchProjectionSortKeyComparer.Instance.Compare(
                    a.Rank,
                    b.Rank);
                if (result == 0) result = a.Sequence.CompareTo(b.Sequence);
            }
            else if (order is SearchViewSortOrder.DirectoryNameAscending or SearchViewSortOrder.DirectoryNameDescending)
            {
                result = CultureInfo.InvariantCulture.CompareInfo.Compare(a.PrimaryText, b.PrimaryText,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.NumericOrdering);
                if (order == SearchViewSortOrder.DirectoryNameDescending) result = -result;
            }
            else result = CompareAlternative(order, a.PrimaryNumber, b.PrimaryNumber, a.SecondaryNumber, b.SecondaryNumber);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Username, b.Username);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.FolderPath, b.FolderPath);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Ref, b.Ref);
        }));
    }

    internal static int CompareAlternative(SearchViewSortOrder order, long? a, long? b, long? aSecondary, long? bSecondary)
    {
        bool ascending = order is SearchViewSortOrder.QueueDepth or SearchViewSortOrder.ItemSizeAscending
            or SearchViewSortOrder.DirectorySizeAscending or SearchViewSortOrder.FileCountAscending;
        int result = ascending ? Nullable.Compare(a, b) : Nullable.Compare(b, a);
        return result == 0 && order == SearchViewSortOrder.QueueDepth ? Nullable.Compare(bSecondary, aSecondary) : result;
    }

    internal static ImmutableSortedSet<SearchViewDirectoryPosition> DirectoryIndex(SearchViewSnapshot snapshot, SearchViewSortOrder order)
    {
        lock (snapshot.IndexGate)
        {
            if (!snapshot.DirectoryIndexes.TryGetValue(order, out var index))
            {
                index = DirectorySet(order).Union(snapshot.Directories.Values.Select(row =>
                    DirectoryPosition(row.Value, row.Key, order)));
                snapshot.DirectoryIndexes.Add(order, index);
            }
            return index;
        }
    }

    public SearchViewFilePage GetFiles(SearchViewFilePosition? after, int limit, SearchViewSortOrder? sortOrder = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = sortOrder ?? View.SortOrder;
        var page = FilePage(order, after, limit);
        return new(Revision,
            page.Items.Select(position => Files[position.Ref].Value).ToArray(), page.Next);
    }

    private (SearchViewFilePosition[] Items, SearchViewFilePosition? Next) FilePage(
        SearchViewSortOrder order, SearchViewFilePosition? after, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ImmutableSortedSet<SearchViewFilePosition>? index;
        lock (IndexGate)
        {
            if (!FileIndexes.TryGetValue(order, out index))
            {
                FileIndexes.TryGetValue(View.SortOrder, out var primary);
                // Live orders advance by changed keys on each publication. Once a
                // file search finishes, new orders only need arrays of existing keys.
                if (View.IsComplete && primary != null)
                {
                    var comparer = FileComparer(order);
                    if (!completedFileOrders.TryGetValue(order, out var sorted))
                    {
                        sorted = primary.ToArray();
                        Array.Sort(sorted, comparer);
                        completedFileOrders.Add(order, sorted);
                    }
                    int position = after == null ? -1 : Array.BinarySearch(sorted, after, comparer);
                    int start = position < 0 ? ~position : position + 1;
                    var items = sorted.AsSpan(start, Math.Min(limit, sorted.Length - start)).ToArray();
                    return (items, items.Length < sorted.Length - start ? items[^1] : null);
                }
                IEnumerable<SearchViewFilePosition> positions = primary != null
                    ? primary : Files.Values.Select(row => Position(row.Value, row.Key));
                index = FileSet(order).Union(positions);
                FileIndexes.Add(order, index);
            }
        }
        return Page(index, after, limit);
    }

    public SearchViewDirectoryPage GetDirectories(SearchViewDirectoryPosition? after, int limit, int initialFileLimit = 0,
        SearchViewSortOrder? sortOrder = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Page(DirectoryIndex(this, sortOrder ?? View.SortOrder), after, limit);
        var rows = page.Items.Select(position => Directories[position.Ref].Value).ToArray();
        return new(Revision, rows, page.Next,
            Previews(this, rows.Select(row => row.Ref), initialFileLimit));
    }

    internal static IReadOnlyDictionary<string, SearchViewDirectoryFilePreview> Previews(SearchViewSnapshot snapshot,
        IEnumerable<string> refs, int limit)
    {
        var previews = new Dictionary<string, SearchViewDirectoryFilePreview>(StringComparer.Ordinal);
        if (limit <= 0) return previews;
        foreach (string key in refs.Distinct(StringComparer.Ordinal))
        {
            var page = Page(snapshot.Directories[key].Children, null, limit);
            previews.Add(key, new(page.Items.Select(position => DirectoryFile(snapshot, position)).ToArray(), page.Next));
        }
        return previews;
    }
    internal static SearchViewDirectoryFile DirectoryFile(SearchViewSnapshot snapshot, SearchViewDirectoryFilePosition position)
        => new(position.Ref, position.RelativePath, snapshot.Files[position.Ref].Value);

    public SearchViewDirectoryFilePage? GetDirectoryFiles(string directoryRef, SearchViewDirectoryFilePosition? after, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directories.TryGetValue(directoryRef, out var directory))
            return null;
        var page = Page(directory.Children, after, limit);
        return new(Revision, directoryRef,
            directory.Value.Username, directory.Value.FolderPath,
            page.Items.Select(position => DirectoryFile(this, position)).ToArray(), page.Next, directory.Value);
    }

    public SearchViewTrackGroupPage GetTrackGroups(SearchViewTrackGroupPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Page(TrackIndex, after, limit);
        return new(Revision,
            page.Items.Select(position => Tracks[position.Ref].Value).ToArray(), page.Next);
    }
    public SearchViewTrackGroupOptionPage? GetTrackGroupOptions(string groupRef, SearchViewFilePosition? after, int limit, CancellationToken cancellationToken = default, SearchViewGroupOptionQuery? query = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Tracks.TryGetValue(groupRef, out var group))
            return null;
        var page = OptionPage(group.Options, after, limit, query,
            key => Files.TryGetValue(key, out var row) ? Position(row.Value, row.Key) : null,
            (position, filter) => SearchViewProjectionDefinition.MatchesFileInput(Files[position.Ref].Value.Input, filter), cancellationToken);
        return new(Revision, groupRef,
            page.Items.Select(position => Files[position.Ref].Value).ToArray(), page.Next, page.Positions);
    }

    internal static SearchViewAlbumGroup Album(SearchViewSnapshot snapshot, AlbumRow row)
        => new(row.Ref, row.Index, row.Query, row.ShareCount, row.SelectableOptionCount, row.Options.Count, snapshot.Directories[row.Representative].Value);
    public SearchViewAlbumGroupPage GetAlbumGroups(SearchViewAlbumGroupPosition? after, int limit, int initialFileLimit = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Page(AlbumIndex, after, limit);
        var rows = page.Items.Select(position => Album(this, Albums[position.Ref])).ToArray();
        return new(Revision, rows, page.Next,
            Previews(this, rows.Select(row => row.Representative.Ref), initialFileLimit));
    }
    internal static ImmutableSortedSet<SearchViewDirectoryPosition> AlbumOptions(SearchViewSnapshot snapshot, AlbumRow group)
    {
        lock (snapshot.IndexGate)
        {
            if (!snapshot.AlbumOptionIndexes.TryGetValue(group.Ref, out var index))
            {
                index = DirectorySet(SearchViewSortOrder.Relevance).Union(group.Options.Select(key =>
                    DirectoryPosition(snapshot.Directories[key].Value, snapshot.Directories[key].Key)));
                snapshot.AlbumOptionIndexes.Add(group.Ref, index);
            }
            return index;
        }
    }

    public SearchViewAlbumGroupOptionPage? GetAlbumGroupOptions(string groupRef, SearchViewDirectoryPosition? after, int limit, int initialFileLimit = 0, CancellationToken cancellationToken = default, SearchViewGroupOptionQuery? query = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Albums.TryGetValue(groupRef, out var group))
            return HiddenAlbums.ContainsKey(groupRef)
                ? new(Revision, groupRef, [], null, new Dictionary<string, SearchViewDirectoryFilePreview>()) : null;
        var index = AlbumOptions(this, group);
        var page = OptionPage(index, after, limit, query,
            key => Directories.TryGetValue(key, out var row) ? DirectoryPosition(row.Value, row.Key) : null,
            Matches, cancellationToken);
        var rows = page.Items.Select(position => Directories[position.Ref].Value).ToArray();
        return new(Revision, groupRef, rows, page.Next,
            Previews(this, rows.Select(row => row.Ref), initialFileLimit), page.Positions);

        bool Matches(SearchViewDirectoryPosition position, string filter)
        {
            if (Utils.ContainsDisplayText(position.Username, filter)
                || Utils.ContainsDisplayText(position.FolderPath, filter)) return true;
            foreach (var child in Directories[position.Ref].Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Utils.ContainsDisplayText(Files[child.Ref].Value.Input.Filename, filter)) return true;
            }
            return false;
        }
    }

    public SearchViewChosenOptions ResolveChosenOptions(IReadOnlyDictionary<string, string> choices, int initialFileLimit = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (View.ProjectionKind is not (SearchViewProjectionKind.TrackGroups or SearchViewProjectionKind.AlbumGroups))
            throw new ArgumentException("Chosen options require a grouped Search View.", nameof(choices));
        var tracks = new Dictionary<string, SearchViewFile>(StringComparer.Ordinal);
        var albums = new Dictionary<string, SearchViewDirectory>(StringComparer.Ordinal);
        foreach (var (groupRef, optionRef) in choices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Tracks.TryGetValue(groupRef, out var track) && Files.TryGetValue(optionRef, out var file)
                && track.Options.Contains(Position(file.Value, file.Key))) tracks.Add(groupRef, file.Value);
            else if (Albums.TryGetValue(groupRef, out var album) && album.Options.Contains(optionRef)
                && Directories.TryGetValue(optionRef, out var directory)) albums.Add(groupRef, directory.Value);
        }
        return new(Revision, tracks, albums,
            Previews(this, albums.Values.Select(row => row.Ref), initialFileLimit));
    }

    public bool HasNewerDirectorySourceFile(string directoryRef, int sourceRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Directories.TryGetValue(directoryRef, out var directory)
            && directory.Children.Any(child => Files[child.Ref].Value.Input.Revision > sourceRevision);
    }
}
