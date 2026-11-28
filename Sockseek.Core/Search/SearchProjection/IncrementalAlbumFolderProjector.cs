using Sockseek.Api;
using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public enum FolderSortMode
{
    AlbumRanked,
    DeterministicUnranked,
}

public sealed class IncrementalAlbumFolderProjector
{
    private readonly AlbumFolderProjectionPlan projectionPlan;
    private readonly FolderSortMode sortMode;
    private readonly bool rankQualityCoverage;
    // Roots own connected album branches: an advertised ancestor absorbs its
    // descendants. Independent albums from the same peer never rebuild each other.
    private readonly Dictionary<PeerPathKey, List<EvaluatedAlbumProjectionInput>> rowsByRoot = [];
    private readonly Dictionary<string, SortedSet<string>> rootsByUser = new(StringComparer.Ordinal);
    private readonly HashSet<PeerPathKey> dirtyRoots = [];
    private readonly HashSet<PeerPathKey> removedRoots = [];
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)> seen = [];
    private readonly Dictionary<PeerPathKey, OrderedFolderEntry> orderEntries = [];
    private readonly SortedSet<OrderedFolderEntry> orderedFolders;
    private readonly AlbumTrackCountConsensus trackCounts;
    private int trackCountReference;
    private int nextOriginalIndex;

    public IncrementalAlbumFolderProjector(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? peerFailureCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked,
        bool enforceMinimumTrackCount = true)
    {
        this.sortMode = sortMode;
        rankQualityCoverage = AlbumQualityPolicy.ActiveConditions(search.NecessaryCond).IsActive;
        orderedFolders = new SortedSet<OrderedFolderEntry>(
            Comparer<OrderedFolderEntry>.Create(CompareOrdered));
        trackCounts = new AlbumTrackCountConsensus(query, search, sortMode);
        projectionPlan = new AlbumFolderProjectionPlan(
            query,
            search,
            peerFailureCounts,
            ignoreStringSortConditions,
            sortMode,
            enforceMinimumTrackCount);
    }

    internal int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
        => AddRange(results.Select((result, index) => SearchProjectionInput.FromLive(
            index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)));

    public int AddRange(IEnumerable<SearchProjectionInput> results)
    {
        int added = 0;
        foreach (SearchProjectionInput input in results)
        {
            EvaluatedAlbumProjectionInput? evaluated = projectionPlan.Evaluate(
                input,
                nextOriginalIndex++);
            if (evaluated == null)
                continue;
            var key = (
                new PeerPathKey(input.Username, input.Filename),
                input.Visibility);
            if (!seen.Add(key))
                continue;

            if (evaluated.DirectoryPath is { } directory)
                AddToBranch(new(input.Username, directory), evaluated);
            added++;
        }

        return added;
    }

    internal AlbumFolderProjectionChanges AddRangeAndGetChanges(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        AddRange(results);
        return GetChanges();
    }

    public AlbumFolderProjectionChanges AddRangeAndGetChanges(IEnumerable<SearchProjectionInput> results)
    {
        AddRange(results);
        return GetChanges();
    }

    public void Clear()
    {
        rowsByRoot.Clear();
        rootsByUser.Clear();
        dirtyRoots.Clear();
        removedRoots.Clear();
        seen.Clear();
        orderEntries.Clear();
        orderedFolders.Clear();
        trackCounts.Clear();
        trackCountReference = 0;
        nextOriginalIndex = 0;
    }

    public List<AlbumFolder> Snapshot()
    {
        RefreshDirtyRoots(null, null, null);
        return OrderedFolders();
    }

    public AlbumFolderProjectionChanges GetChanges()
    {
        var added = new List<AlbumFolder>();
        var updated = new List<AlbumFolder>();
        var removed = new List<AlbumFolder>();
        RefreshDirtyRoots(added, updated, removed);
        return new AlbumFolderProjectionChanges(
            OrderedFolders(),
            added,
            updated,
            removed);
    }

    private void AddToBranch(PeerPathKey directory, EvaluatedAlbumProjectionInput input)
    {
        PeerPathKey ancestor = directory;
        while (true)
        {
            if (rowsByRoot.TryGetValue(ancestor, out var existing))
            {
                existing.Add(input);
                dirtyRoots.Add(ancestor);
                return;
            }
            int separator = Math.Max(ancestor.RemotePath.LastIndexOf('\\'), ancestor.RemotePath.LastIndexOf('/'));
            if (separator <= 0) break;
            ancestor = ancestor with { RemotePath = ancestor.RemotePath[..separator] };
        }

        if (!rootsByUser.TryGetValue(directory.Username, out var roots))
            rootsByUser[directory.Username] = roots = new(StringComparer.Ordinal);
        var rows = new List<EvaluatedAlbumProjectionInput> { input };
        // A late parent joins only existing descendant roots. Seek each wire
        // separator independently; prefix neighbours (Album/Album2) stay separate.
        foreach (char separator in "/\\")
        {
            string prefix = directory.RemotePath + separator;
            string upper = directory.RemotePath + (char)(separator + 1);
            foreach (string child in roots.GetViewBetween(prefix, upper).TakeWhile(path =>
                path.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                var childKey = new PeerPathKey(directory.Username, child);
                rows.AddRange(rowsByRoot[childKey]);
                rowsByRoot.Remove(childKey);
                roots.Remove(child);
                dirtyRoots.Remove(childKey);
                removedRoots.Add(childKey);
            }
        }
        // A late parent joins earlier arrivals; keep that branch in admission
        // order once, instead of sorting its growing prefix at every publication.
        if (rows.Count > 1)
            rows.Sort((x, y) => x.SortEntry.OriginalIndex.CompareTo(y.SortEntry.OriginalIndex));
        rowsByRoot.Add(directory, rows);
        roots.Add(directory.RemotePath);
        dirtyRoots.Add(directory);
    }

    private void RefreshDirtyRoots(
        List<AlbumFolder>? added,
        List<AlbumFolder>? updated,
        List<AlbumFolder>? removed)
    {
        if (dirtyRoots.Count == 0) return;
        // Publication reports each folder as it is finally ranked.
        var addedKeys = new List<PeerPathKey>();
        var updatedKeys = new List<PeerPathKey>();
        var removedByUser = removedRoots.ToLookup(key => key.Username, StringComparer.Ordinal);
        foreach (var user in dirtyRoots.GroupBy(key => key.Username).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            bool singleBranch = user.Count() == 1;
            var rows = singleBranch ? rowsByRoot[user.First()]
                : user.SelectMany(key => rowsByRoot[key]).ToList();
            // Equal filenames (public/locked) retain the same input order as a
            // one-shot projection. A single branch is already in that order.
            if (!singleBranch)
                rows.Sort((x, y) => x.SortEntry.OriginalIndex.CompareTo(y.SortEntry.OriginalIndex));
            List<AlbumFolder> projected = projectionPlan.ProjectEvaluatedResults(rows);
            var previous = user.Concat(removedByUser[user.Key]).ToHashSet();
            foreach (var key in previous)
                if (orderEntries.TryGetValue(key, out var prior))
                {
                    orderedFolders.Remove(prior);
                    trackCounts.Remove(prior.Source);
                }
            foreach (AlbumFolder folder in projected)
            {
                PeerPathKey key = FolderKey(folder);
                previous.Remove(key);
                if (!orderEntries.TryGetValue(key, out var prior)) addedKeys.Add(key);
                else if (!SameSearchMetadata(prior.Folder, folder)) updatedKeys.Add(key);
                IReadOnlyList<ProjectedFileCandidate> projectionFiles = folder.ProjectionFiles
                    ?? throw new InvalidDataException("An album projection has no admitted file facts.");
                trackCounts.Add(folder);
                var entry = new OrderedFolderEntry(key, AlbumTrackCountConsensus.Rank(folder, trackCountReference), folder,
                    projectionFiles.Count == 0 ? long.MaxValue : projectionFiles.Min(file => file.Input.Sequence));
                orderEntries[key] = entry;
                if (!orderedFolders.Add(entry))
                    throw new InvalidDataException("Distinct album folders have the same order identity.");
            }
            foreach (PeerPathKey key in previous)
            {
                if (orderEntries.Remove(key, out var prior)) removed?.Add(prior.Folder);
            }
        }
        dirtyRoots.Clear();
        removedRoots.Clear();

        // A moved reference re-ranks only the folders between the old and new count.
        int reference = trackCounts.Reference;
        if (reference != trackCountReference)
        {
            trackCountReference = reference;
            var reported = addedKeys.Concat(updatedKeys).ToHashSet();
            foreach (OrderedFolderEntry entry in orderEntries.Values.ToArray())
            {
                bool incomplete = !ReferenceEquals(entry.Folder, entry.Source);
                if (AlbumTrackCountConsensus.IsBelow(entry.Source, reference) == incomplete) continue;
                orderedFolders.Remove(entry);
                var ranked = entry with { Folder = AlbumTrackCountConsensus.Rank(entry.Source, reference) };
                orderEntries[entry.Key] = ranked;
                orderedFolders.Add(ranked);
                if (reported.Add(entry.Key)) updatedKeys.Add(entry.Key);
            }
        }
        added?.AddRange(addedKeys.Select(key => orderEntries[key].Folder));
        updated?.AddRange(updatedKeys.Select(key => orderEntries[key].Folder));
    }

    private List<AlbumFolder> OrderedFolders()
        => orderedFolders.Select(entry => entry.Folder).ToList();

    private int CompareOrdered(OrderedFolderEntry? x, OrderedFolderEntry? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x == null)
            return 1;
        if (y == null)
            return -1;
        if (sortMode == FolderSortMode.DeterministicUnranked)
        {
            int user = string.Compare(x.Key.Username, y.Key.Username, StringComparison.Ordinal);
            return user != 0
                ? user
                : string.Compare(x.Key.RemotePath, y.Key.RemotePath, StringComparison.Ordinal);
        }
        return CompareRanked(x.Folder, y.Folder, x.FirstSequence, y.FirstSequence);
    }

    private int CompareRanked(AlbumFolder x, AlbumFolder y, long xFirstSequence, long yFirstSequence)
    {
        int rank = AlbumFolderGroupComparer.CompareRank(x.SearchGroupSortEntry, y.SearchGroupSortEntry,
            x.SearchAudioQualityCoverage, y.SearchAudioQualityCoverage, rankQualityCoverage);
        if (rank != 0) return rank;
        rank = xFirstSequence.CompareTo(yFirstSequence);
        if (rank != 0)
            return rank;
        int username = string.Compare(x.Username, y.Username, StringComparison.Ordinal);
        return username != 0
            ? username
            : string.Compare(x.FolderPath, y.FolderPath, StringComparison.Ordinal);
    }

    private static PeerPathKey FolderKey(AlbumFolder folder)
        => new(folder.Username, folder.FolderPath);

    // Folder is Source as currently ranked against the track-count reference.
    private sealed record OrderedFolderEntry(
        PeerPathKey Key,
        AlbumFolder Folder,
        AlbumFolder Source,
        long FirstSequence);

    private static bool SameSearchMetadata(AlbumFolder a, AlbumFolder b)
        => a.SearchFileCount == b.SearchFileCount && a.SearchAudioFileCount == b.SearchAudioFileCount
            && a.SearchRepresentativeAudioFilename == b.SearchRepresentativeAudioFilename
            && a.SortedAudioLengths.Span.SequenceEqual(b.SortedAudioLengths.Span);
}

public sealed record AlbumFolderProjectionChanges(
    IReadOnlyList<AlbumFolder> Folders,
    IReadOnlyList<AlbumFolder> Added,
    IReadOnlyList<AlbumFolder> Updated,
    IReadOnlyList<AlbumFolder> Removed)
{
    public bool HasChanges => Added.Count > 0 || Updated.Count > 0 || Removed.Count > 0;
}
