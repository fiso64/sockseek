using Sockseek.Api;
using static Sockseek.Server.Planning.SearchViewCache;
using System.Collections.Immutable;
using System.Globalization;
using Sockseek.Core.Services;

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
    internal SearchViewSelectionCounts Counts = new(0, 0, 0);
    internal readonly Lock IndexGate = new();
    internal readonly Dictionary<SearchViewSortOrder, ImmutableSortedSet<SearchViewFilePosition>> FileIndexes = [];
    internal readonly Dictionary<SearchViewSortOrder, ImmutableSortedSet<SearchViewDirectoryPosition>> DirectoryIndexes = [];
    internal readonly Dictionary<string, ImmutableSortedSet<SearchViewDirectoryPosition>> AlbumOptionIndexes = [];
    internal ImmutableSortedSet<SearchViewTrackGroupPosition> TrackIndex = ImmutableSortedSet.Create(TrackComparer);
    internal ImmutableSortedSet<SearchViewAlbumGroupPosition> AlbumIndex = ImmutableSortedSet.Create(AlbumComparer);

    // Positions carry the exact keys used by their immutable index. Seeking never
    // scans earlier pages, and does not consult the latest projection revision.
    internal static (T[] Items, T? Next) Page<T>(ImmutableSortedSet<T> index, T? after, int limit) where T : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        int start = 0;
        if (after != null)
        {
            int position = index.IndexOf(after);
            start = position >= 0 ? position + 1 : ~position;
        }
        int count = Math.Min(limit, index.Count - start);
        var items = new T[count];
        for (int i = 0; i < count; i++) items[i] = index[start + i];
        return (items, start + count < index.Count && count > 0 ? items[^1] : null);
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
    {
        if (order is not (SearchViewSortOrder.Relevance or SearchViewSortOrder.UploadSpeed
            or SearchViewSortOrder.QueueDepth or SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending))
            throw new ArgumentOutOfRangeException(nameof(order));
        return ImmutableSortedSet.Create<SearchViewFilePosition>(Comparer<SearchViewFilePosition>.Create((a, b) =>
        {
            int result = order == SearchViewSortOrder.Relevance
                ? SearchProjectionSortKeyComparer.Instance.Compare(
                    new(a.HighFlags, a.UploadSpeedFast, a.MidFlags, a.InferredTrackCount, a.UploadSpeedMedium, a.BitRate, a.StableTieBreaker),
                    new(b.HighFlags, b.UploadSpeedFast, b.MidFlags, b.InferredTrackCount, b.UploadSpeedMedium, b.BitRate, b.StableTieBreaker))
                : CompareAlternative(order, a.PrimaryNumber, b.PrimaryNumber, a.SecondaryNumber, b.SecondaryNumber);
            if (result == 0) result = a.Sequence.CompareTo(b.Sequence);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Ref, b.Ref);
        }));
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
                    new(a.HighFlags, a.UploadSpeedFast, a.MidFlags, a.InferredTrackCount, a.UploadSpeedMedium, a.BitRate, a.StableTieBreaker),
                    new(b.HighFlags, b.UploadSpeedFast, b.MidFlags, b.InferredTrackCount, b.UploadSpeedMedium, b.BitRate, b.StableTieBreaker));
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

    internal static ImmutableSortedSet<SearchViewFilePosition> FileIndex(SearchViewSnapshot snapshot, SearchViewSortOrder order)
    {
        lock (snapshot.IndexGate)
        {
            if (!snapshot.FileIndexes.TryGetValue(order, out var index))
            {
                index = FileSet(order).Union(snapshot.Files.Values.Select(row => Position(row.Value, row.Key, order)));
                snapshot.FileIndexes.Add(order, index);
            }
            return index;
        }
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
        var page = Page(FileIndex(this, sortOrder ?? View.SortOrder), after, limit);
        return new(Revision,
            page.Items.Select(position => Files[position.Ref].Value).ToArray(), page.Next);
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
    public SearchViewTrackGroupOptionPage? GetTrackGroupOptions(string groupRef, SearchViewFilePosition? after, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Tracks.TryGetValue(groupRef, out var group))
            return null;
        var page = Page(group.Options, after, limit);
        return new(Revision, groupRef,
            page.Items.Select(position => Files[position.Ref].Value).ToArray(), page.Next);
    }

    internal static SearchViewAlbumGroup Album(SearchViewSnapshot snapshot, AlbumRow row)
        => new(row.Ref, row.Index, row.Query, row.ShareCount, row.OptionCount, snapshot.Directories[row.Representative].Value);
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

    public SearchViewAlbumGroupOptionPage? GetAlbumGroupOptions(string groupRef, SearchViewDirectoryPosition? after, int limit, int initialFileLimit = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Albums.TryGetValue(groupRef, out var group))
            return HiddenAlbums.ContainsKey(groupRef)
                ? new(Revision, groupRef, [], null, new Dictionary<string, SearchViewDirectoryFilePreview>()) : null;
        var index = AlbumOptions(this, group);
        var page = Page(index, after, limit);
        var rows = page.Items.Select(position => Directories[position.Ref].Value).ToArray();
        return new(Revision, groupRef, rows, page.Next,
            Previews(this, rows.Select(row => row.Ref), initialFileLimit));
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
