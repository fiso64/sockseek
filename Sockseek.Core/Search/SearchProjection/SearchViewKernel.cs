using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed record SearchViewCounters(
    long PublicFileCount,
    long LockedFileCount,
    long PublicBytes,
    long LockedBytes,
    int ObservedPeerCount,
    long ProjectedFileCount,
    long ProjectedPublicFileCount,
    long ProjectedLockedFileCount,
    long PreferredFileCount,
    long OtherFileCount,
    long TopLevelItemCount = 0,
    long SelectableOptionCount = 0);

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewProjectionKind>))]
public enum SearchViewProjectionKind
{
    Files,
    GenericDirectories,
    AlbumDirectories,
    AggregateTracks,
    AggregateAlbums,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewSortOrder>))]
public enum SearchViewSortOrder
{
    Relevance,
    UploadSpeed,
    QueueDepth,
    ItemSizeAscending,
    ItemSizeDescending,
    DirectorySizeAscending,
    DirectorySizeDescending,
    FileCountAscending,
    FileCountDescending,
    DirectoryNameAscending,
    DirectoryNameDescending,
}

public sealed record SearchViewProjectionDefinition(
    SearchViewProjectionKind Kind,
    SongQuery? SongQuery = null,
    AlbumQuery? AlbumQuery = null,
    bool IncludeFullResults = false,
    string? FilterText = null,
    SearchViewSortOrder SortOrder = SearchViewSortOrder.Relevance)
{
    public static bool MatchesFileInput(SearchProjectionInput input, string? normalizedFilter)
    {
        ArgumentNullException.ThrowIfNull(input);
        return string.IsNullOrEmpty(normalizedFilter)
            || input.Username.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase)
            || input.Filename.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    public void Validate()
    {
        if (Kind is SearchViewProjectionKind.Files or SearchViewProjectionKind.GenericDirectories
            && SongQuery == null)
            throw new ArgumentException("A file-based Search View requires a song query.");
        if (Kind is SearchViewProjectionKind.AlbumDirectories or SearchViewProjectionKind.AggregateAlbums
            && AlbumQuery == null)
            throw new ArgumentException("An album Search View requires an album query.");
        if (Kind == SearchViewProjectionKind.AggregateTracks && SongQuery == null)
            throw new ArgumentException("An aggregate-track Search View requires a song query.");
        bool validOrder = Kind switch
        {
            SearchViewProjectionKind.Files or SearchViewProjectionKind.AlbumDirectories =>
                SortOrder is SearchViewSortOrder.Relevance
                    or SearchViewSortOrder.UploadSpeed
                    or SearchViewSortOrder.QueueDepth
                    or SearchViewSortOrder.ItemSizeAscending
                    or SearchViewSortOrder.ItemSizeDescending,
            SearchViewProjectionKind.GenericDirectories =>
                SortOrder is SearchViewSortOrder.Relevance
                    or SearchViewSortOrder.UploadSpeed
                    or SearchViewSortOrder.DirectorySizeAscending
                    or SearchViewSortOrder.DirectorySizeDescending
                    or SearchViewSortOrder.FileCountAscending
                    or SearchViewSortOrder.FileCountDescending
                    or SearchViewSortOrder.DirectoryNameAscending
                    or SearchViewSortOrder.DirectoryNameDescending,
            SearchViewProjectionKind.AggregateTracks or SearchViewProjectionKind.AggregateAlbums =>
                SortOrder == SearchViewSortOrder.Relevance,
            _ => false,
        };
        if (!validOrder)
            throw new ArgumentException($"Sort order '{SortOrder}' is not available for {Kind}.");
    }
}

public sealed record SearchViewProjectedDirectory(
    PeerDirectoryIdentity Directory,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    ProjectedFileCandidate BestChild,
    IReadOnlyList<ProjectedFileCandidate> NewChildren,
    bool IsFullyRetrieved = false,
    long? RetrievedFileCount = null,
    long? RetrievedBytes = null,
    IReadOnlyList<PeerDirectoryIdentity>? AbsorbedDirectories = null);

public sealed record SearchViewKernelUpdate(
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewCounters Counters,
    IReadOnlyList<ProjectedFileCandidate> ChangedFiles,
    // Peer totals include filtered inputs, without carrying their file graphs
    // through publication or retaining them between counter-only updates.
    IReadOnlyList<string>? ObservedUsernames = null,
    IReadOnlyList<SearchViewProjectedDirectory>? ChangedDirectories = null,
    IReadOnlyList<PeerDirectoryIdentity>? RemovedDirectories = null,
    IReadOnlyList<SearchViewProjectedAggregateTrackGroup>? ChangedAggregateTrackGroups = null,
    IReadOnlyList<SearchViewProjectedAggregateAlbumGroup>? ChangedAggregateAlbumGroups = null,
    IReadOnlyList<PeerDirectoryIdentity>? RemovedAggregateAlbumGroups = null)
{
    public bool HasProjectionChanges => ChangedFiles.Count > 0
        || (ChangedDirectories?.Count ?? 0) > 0
        || (RemovedDirectories?.Count ?? 0) > 0
        || (ChangedAggregateTrackGroups?.Count ?? 0) > 0
        || (ChangedAggregateAlbumGroups?.Count ?? 0) > 0
        || (RemovedAggregateAlbumGroups?.Count ?? 0) > 0;
}

public sealed record SearchViewKernelSeed(
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewCounters Counters);

public sealed record SearchViewKernelSnapshot(
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewCounters Counters,
    IReadOnlyList<ProjectedFileCandidate> Files,
    IReadOnlyList<SearchViewProjectedDirectory>? Directories = null,
    IReadOnlyList<SearchViewProjectedAggregateTrackGroup>? AggregateTrackGroups = null,
    IReadOnlyList<SearchViewProjectedAggregateAlbumGroup>? AggregateAlbumGroups = null);

/// <summary>
/// Storage-neutral incremental file-view owner. Live batches and retained
/// history both enter through <see cref="Apply"/>; completion only publishes
/// completeness and never switches to a second projection implementation.
/// </summary>
public sealed class SearchViewKernel
{
    private readonly object gate = new();
    private readonly IncrementalResultSorter? sorter;
    private readonly IncrementalAlbumFolderProjector? albumFolderProjector;
    private readonly IncrementalAggregateTrackProjector? aggregateTrackProjector;
    private readonly IncrementalAlbumAggregateProjector? aggregateAlbumProjector;
    private readonly SearchViewProjectionKind projectionKind;
    private readonly string? filterText;
    private readonly Dictionary<PeerPathKey, DirectoryState> directories = [];
    private readonly Dictionary<string, SortedSet<string>> directoryPathsByPeer =
        new(StringComparer.Ordinal);
    private readonly Dictionary<PeerPathKey, SearchViewProjectedDirectory> albumDirectories = [];
    private readonly Dictionary<PeerPathKey, AlbumProjectionFileCounts> albumDirectoryCounts = [];
    private readonly Dictionary<PeerPathKey, HashSet<(
        PeerPathKey Path,
        SearchResultVisibility Visibility)>> albumDirectoryChildren = [];
    // Apply admits unique, ordered source sequences. All references to a frozen
    // file retain that sequence, even when its folder disappears or is absorbed.
    // Restoring directory links must not republish its immutable file facts.
    private readonly HashSet<long> publishedAlbumSequences = [];
    private readonly Dictionary<int, SearchViewProjectedAggregateAlbumGroup> visibleAggregateAlbumGroups = [];
    private readonly Dictionary<PeerDirectoryIdentity, int> visibleAggregateAlbumIdentities = [];
    private readonly HashSet<int> visibleAggregateTrackIndexes = [];
    private readonly Dictionary<int, long> aggregateTrackSelectableCounts = [];
    private readonly bool trackPeerIdentities;
    private readonly HashSet<string> observedPeers = new(StringComparer.Ordinal);
    private long publicFileCount;
    private long lockedFileCount;
    private long publicBytes;
    private long lockedBytes;
    private long consumedSequence;
    private long projectedFileCount;
    private long projectedPublicFileCount;
    private long projectedLockedFileCount;
    private long preferredFileCount;
    private long otherFileCount;
    private long topLevelItemCount;
    private long selectableOptionCount;
    private int observedPeerCount;
    private int sourceRevision;
    private bool isComplete;

    public long ConsumedSequence { get { lock (gate) return consumedSequence; } }
    public bool IsComplete { get { lock (gate) return isComplete; } }

    public SearchViewKernel(
        FileSearchProjection projection,
        SearchSettings settings,
        IReadOnlyDictionary<string, int>? reputationSnapshot = null,
        bool retainProjectedRows = true,
        SearchViewKernelSeed? seed = null,
        bool trackPeerIdentities = true)
        : this(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.Files,
                projection?.Query,
                IncludeFullResults: projection?.IncludeFullResults ?? false),
            settings,
            reputationSnapshot,
            retainProjectedRows,
            seed,
            trackPeerIdentities)
    {
        ArgumentNullException.ThrowIfNull(projection);
    }

    public SearchViewKernel(
        SearchViewProjectionDefinition projection,
        SearchSettings settings,
        IReadOnlyDictionary<string, int>? reputationSnapshot = null,
        bool retainProjectedRows = true,
        SearchViewKernelSeed? seed = null,
        bool trackPeerIdentities = true)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();
        ArgumentNullException.ThrowIfNull(settings);
        if (seed != null && trackPeerIdentities && seed.Counters.ObservedPeerCount != 0)
            throw new ArgumentException(
                "A seeded kernel must use disk-backed peer identity tracking.",
                nameof(trackPeerIdentities));
        this.trackPeerIdentities = trackPeerIdentities;
        projectionKind = projection.Kind;
        filterText = string.IsNullOrWhiteSpace(projection.FilterText)
            ? null : projection.FilterText.Trim();
        if (projectionKind is not (SearchViewProjectionKind.Files
            or SearchViewProjectionKind.GenericDirectories
            or SearchViewProjectionKind.AlbumDirectories
            or SearchViewProjectionKind.AggregateTracks
            or SearchViewProjectionKind.AggregateAlbums))
            throw new NotSupportedException($"Search View projection '{projectionKind}' is not implemented yet.");
        var reputation = reputationSnapshot == null
            ? new ConcurrentDictionary<string, int>()
            : new ConcurrentDictionary<string, int>(
                reputationSnapshot,
                StringComparer.Ordinal);
        if (projectionKind is SearchViewProjectionKind.AlbumDirectories
            or SearchViewProjectionKind.AggregateAlbums)
        {
            albumFolderProjector = new IncrementalAlbumFolderProjector(
                projection.AlbumQuery!,
                SettingsCloner.Clone(settings),
                reputation,
                ignoreStringSortConditions:
                    projectionKind == SearchViewProjectionKind.AggregateAlbums,
                sortMode: projectionKind == SearchViewProjectionKind.AggregateAlbums
                    ? FolderSortMode.DeterministicUnranked
                    : FolderSortMode.AlbumRanked);
            if (projectionKind == SearchViewProjectionKind.AggregateAlbums)
            {
                aggregateAlbumProjector = new IncrementalAlbumAggregateProjector(
                    projection.AlbumQuery!,
                    SettingsCloner.Clone(settings));
            }
        }
        else if (projectionKind == SearchViewProjectionKind.AggregateTracks)
        {
            aggregateTrackProjector = new IncrementalAggregateTrackProjector(
                projection.SongQuery!,
                SettingsCloner.Clone(settings),
                reputation,
                filterText);
        }
        else
        {
            sorter = new IncrementalResultSorter(
                projection.SongQuery!,
                SettingsCloner.Clone(settings),
                reputation,
                useInfer: false,
                requireFileSatisfies: !projection.IncludeFullResults,
                retainProjectedRows: projectionKind == SearchViewProjectionKind.Files
                    && retainProjectedRows,
                // Durable raw admission already owns exact duplicate rejection.
                // Avoid retaining another result-sized identity set in daemon views.
                deduplicateInputs: trackPeerIdentities);
        }
        if (seed != null)
        {
            sourceRevision = seed.SourceRevision;
            consumedSequence = seed.ConsumedSequence;
            isComplete = seed.IsComplete;
            publicFileCount = seed.Counters.PublicFileCount;
            lockedFileCount = seed.Counters.LockedFileCount;
            publicBytes = seed.Counters.PublicBytes;
            lockedBytes = seed.Counters.LockedBytes;
            observedPeerCount = seed.Counters.ObservedPeerCount;
            projectedFileCount = seed.Counters.ProjectedFileCount;
            projectedPublicFileCount = seed.Counters.ProjectedPublicFileCount;
            projectedLockedFileCount = seed.Counters.ProjectedLockedFileCount;
            preferredFileCount = seed.Counters.PreferredFileCount;
            otherFileCount = seed.Counters.OtherFileCount;
            topLevelItemCount = seed.Counters.TopLevelItemCount;
            selectableOptionCount = seed.Counters.SelectableOptionCount;
        }
    }

    public SearchViewKernelUpdate Apply(
        IEnumerable<SearchProjectionInput> inputs,
        int sourceRevision,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        lock (gate)
        {
            if (sourceRevision < this.sourceRevision)
                throw new ArgumentOutOfRangeException(
                    nameof(sourceRevision),
                    "A search view source revision cannot regress.");
            if (this.isComplete && !isComplete)
                throw new InvalidOperationException("A completed search view cannot become live again.");

            var fresh = new List<SearchProjectionInput>();
            long nextSequence = consumedSequence;
            foreach (SearchProjectionInput input in inputs)
            {
                if (input.Sequence <= consumedSequence)
                    continue;
                if (input.Sequence <= nextSequence)
                    throw new InvalidDataException(
                        "Fresh search-view inputs must be strictly ordered by sequence.");
                nextSequence = input.Sequence;
                fresh.Add(input);
            }
            foreach (SearchProjectionInput input in fresh)
            {
                if (trackPeerIdentities && observedPeers.Add(input.Username))
                    observedPeerCount = observedPeers.Count;
                long bytes = Math.Max(0, input.Size);
                if (input.Visibility == SearchResultVisibility.Locked)
                {
                    lockedFileCount++;
                    lockedBytes = checked(lockedBytes + bytes);
                }
                else
                {
                    publicFileCount++;
                    publicBytes = checked(publicBytes + bytes);
                }
            }
            consumedSequence = nextSequence;

            IReadOnlyList<ProjectedFileCandidate> changed;
            IReadOnlyList<SearchViewProjectedDirectory>? changedDirectories = null;
            IReadOnlyList<PeerDirectoryIdentity>? removedDirectories = null;
            IReadOnlyList<SearchViewProjectedAggregateTrackGroup>? changedTrackGroups = null;
            IReadOnlyList<SearchViewProjectedAggregateAlbumGroup>? changedAlbumGroups = null;
            IReadOnlyList<PeerDirectoryIdentity>? removedAlbumGroups = null;
            if (projectionKind == SearchViewProjectionKind.AggregateAlbums)
            {
                AlbumFolderProjectionChanges albumChanges = albumFolderProjector!
                    .AddRangeAndGetChanges(fresh);
                // Touched roots retain their admission order. Group changes can
                // also reveal/retire untouched folders when greedy buckets regroup.
                var affectedFolders = albumChanges.Added.Concat(albumChanges.Updated)
                    .Select(folder => new PeerPathKey(folder.Username, folder.FolderPath)).ToHashSet();
                AlbumAggregateSearchViewChanges aggregateChanges = aggregateAlbumProjector!
                    .ApplyChangesForSearchView(albumChanges);
                (changedAlbumGroups, removedAlbumGroups) = ApplyAggregateAlbumGroups(
                    aggregateChanges.AffectedGroupIndexes, affectedFolders);
                (changedDirectories, removedDirectories) = ApplyVisibleAggregateAlbumDirectories(affectedFolders);
                changed = NewAlbumFiles(changedDirectories);
            }
            else if (projectionKind == SearchViewProjectionKind.AggregateTracks)
            {
                var aggregateChanges = aggregateTrackProjector!
                    .AddRangeForSearchView(fresh);
                var changedVisible = new List<SearchViewProjectedAggregateTrackGroup>();
                var changedFiles = new List<ProjectedFileCandidate>();
                foreach (SearchViewProjectedAggregateTrackGroup change in aggregateChanges)
                {
                    bool alreadyVisible = visibleAggregateTrackIndexes.Contains(change.Index);
                    // A late filename match may reveal a group that was already
                    // eligible upstream. Include its earlier options as well.
                    SearchViewProjectedAggregateTrackGroup group = alreadyVisible
                        ? change
                        : aggregateTrackProjector.SnapshotGroupForSearchView(change.Index);
                    if (!alreadyVisible)
                    {
                        visibleAggregateTrackIndexes.Add(group.Index);
                        topLevelItemCount++;
                    }
                    aggregateTrackSelectableCounts.TryGetValue(group.Index, out long priorSelectable);
                    selectableOptionCount = checked(selectableOptionCount
                        + group.SelectableOptionCount - priorSelectable);
                    aggregateTrackSelectableCounts[group.Index] = group.SelectableOptionCount;
                    foreach (ProjectedFileCandidate file in group.NewOptions)
                    {
                        CountProjectedFile(file);
                        changedFiles.Add(file);
                    }
                    changedVisible.Add(group);
                }
                // A newly visible group supplies its complete retained options;
                // later changes supply just additions. Unpublished candidates
                // stay in their bucket, without writing unusable View rows.
                changed = changedFiles;
                changedTrackGroups = changedVisible;
            }
            else if (projectionKind == SearchViewProjectionKind.AlbumDirectories)
            {
                AlbumFolderProjectionChanges albumChanges = albumFolderProjector!
                    .AddRangeAndGetChanges(fresh);
                (changedDirectories, removedDirectories) = ApplyAlbumDirectories(
                    albumChanges.Added.Concat(albumChanges.Updated),
                    albumChanges.Removed.Select(folder => folder.DirectoryIdentity));
                changed = NewAlbumFiles(changedDirectories);
                topLevelItemCount = albumDirectories.Count;
                selectableOptionCount = projectedPublicFileCount;
            }
            else
            {
                changed = sorter!.AddRangeAndGetProjected(
                    fresh.Where(MatchesFileInput));
                foreach (ProjectedFileCandidate file in changed)
                {
                    CountProjectedFile(file);
                }
                if (projectionKind == SearchViewProjectionKind.GenericDirectories)
                {
                    (changedDirectories, removedDirectories) = ApplyDirectories(changed);
                    topLevelItemCount = directories.Count;
                    selectableOptionCount = projectedPublicFileCount;
                }
                else
                {
                    topLevelItemCount = projectedFileCount;
                    selectableOptionCount = projectedPublicFileCount;
                }
            }
            this.sourceRevision = sourceRevision;
            this.isComplete = isComplete;
            return new SearchViewKernelUpdate(
                sourceRevision,
                consumedSequence,
                isComplete,
                Counters(),
                changed,
                fresh.Select(input => input.Username).Distinct(StringComparer.Ordinal).ToArray(),
                changedDirectories,
                removedDirectories,
                changedTrackGroups,
                changedAlbumGroups,
                removedAlbumGroups);
        }
    }

    public SearchViewKernelSnapshot Snapshot()
    {
        lock (gate)
        {
            IReadOnlyList<ProjectedFileCandidate> files = projectionKind == SearchViewProjectionKind.Files
                ? sorter!.SnapshotProjectedFiles()
                : [];
            return new(
                sourceRevision,
                consumedSequence,
                isComplete,
                Counters(),
                files,
                projectionKind switch
                {
                    SearchViewProjectionKind.GenericDirectories
                        => directories.Values.Select(state => state.Snapshot([])).ToArray(),
                    SearchViewProjectionKind.AlbumDirectories
                        => albumDirectories.Values.ToArray(),
                    _ => null,
                },
                projectionKind == SearchViewProjectionKind.AggregateTracks
                    ? aggregateTrackProjector!.SnapshotForSearchView()
                    : null,
                projectionKind == SearchViewProjectionKind.AggregateAlbums
                    ? aggregateAlbumProjector!.SnapshotForSearchView()
                        .Where(MatchesAggregateAlbumGroup).ToArray()
                    : null);
        }
    }

    private SearchViewCounters Counters()
        => new(
            publicFileCount,
            lockedFileCount,
            publicBytes,
            lockedBytes,
            observedPeerCount,
            projectedFileCount,
            projectedPublicFileCount,
            projectedLockedFileCount,
            preferredFileCount,
            otherFileCount,
            topLevelItemCount,
            selectableOptionCount);

    private bool ContainsFilter(string? value)
        => filterText != null
            && value?.Contains(filterText, StringComparison.OrdinalIgnoreCase) == true;

    private bool MatchesFileInput(SearchProjectionInput input)
        => SearchViewProjectionDefinition.MatchesFileInput(input, filterText);

    private bool MatchesAlbumFolder(AlbumFolder folder)
        => filterText == null
            || ContainsFilter(folder.Username)
            || ContainsFilter(folder.FolderPath)
            || (folder.ProjectionFiles ?? throw new InvalidDataException("A search folder has no projection facts."))
                .Any(file => ContainsFilter(file.Input.Filename));

    private bool MatchesAggregateAlbumGroup(SearchViewProjectedAggregateAlbumGroup group)
        => filterText == null
            || ContainsFilter(group.Query.Artist)
            || ContainsFilter(group.Query.Album)
            || group.Options.Any(option => ContainsFilter(option.FolderPath));

    // A generic card root must itself have a direct hit. Sibling subfolders
    // stay separate until that ancestor arrives; the store reparents old
    // memberships at the new revision without retaining child files here.
    private (
        IReadOnlyList<SearchViewProjectedDirectory> Changed,
        IReadOnlyList<PeerDirectoryIdentity> Removed) ApplyDirectories(
        IReadOnlyList<ProjectedFileCandidate> changed)
    {
        var touched = new Dictionary<PeerPathKey, List<ProjectedFileCandidate>>();
        var absorbed = new Dictionary<PeerPathKey, List<PeerDirectoryIdentity>>();
        var newRoots = new HashSet<PeerPathKey>();
        var removed = new List<PeerDirectoryIdentity>();
        foreach (ProjectedFileCandidate file in changed)
        {
            string directoryPath = RemoteContainingDirectory(file.Input.Filename);
            if (directoryPath.Length == 0 && file.Input.Filename.IndexOfAny(['\\', '/']) >= 0)
                continue;
            string username = file.Input.Username;
            var key = FindDirectoryRoot(username, directoryPath);
            if (key == null)
            {
                key = new PeerPathKey(username, directoryPath);
                var state = new DirectoryState(new PeerDirectoryIdentity(username, directoryPath));
                if (!directoryPathsByPeer.TryGetValue(username, out SortedSet<string>? paths))
                {
                    paths = new SortedSet<string>(StringComparer.Ordinal);
                    directoryPathsByPeer.Add(username, paths);
                }
                string? upperBound = PrefixUpperBound(directoryPath);
                IEnumerable<string> candidates = directoryPath.Length == 0 ? [] : upperBound == null
                    ? paths
                    : paths.GetViewBetween(directoryPath, upperBound);
                foreach (string descendant in candidates
                    .Where(path => IsRemoteDescendant(path, directoryPath)).ToArray())
                {
                    var descendantKey = new PeerPathKey(username, descendant);
                    state.Absorb(directories[descendantKey]);
                    directories.Remove(descendantKey);
                    paths.Remove(descendant);
                    if (touched.Remove(descendantKey, out List<ProjectedFileCandidate>? children))
                    {
                        if (!touched.TryGetValue(key.Value, out List<ProjectedFileCandidate>? moved))
                            touched.Add(key.Value, moved = []);
                        moved.AddRange(children);
                    }
                    if (absorbed.Remove(descendantKey, out List<PeerDirectoryIdentity>? prior))
                    {
                        if (!absorbed.TryGetValue(key.Value, out List<PeerDirectoryIdentity>? moved))
                            absorbed.Add(key.Value, moved = []);
                        moved.AddRange(prior);
                    }
                    if (!newRoots.Remove(descendantKey))
                    {
                        removed.Add(new PeerDirectoryIdentity(username, descendant));
                        if (!absorbed.TryGetValue(key.Value, out List<PeerDirectoryIdentity>? moved))
                            absorbed.Add(key.Value, moved = []);
                        moved.Add(new PeerDirectoryIdentity(username, descendant));
                    }
                }
                directories.Add(key.Value, state);
                paths.Add(directoryPath);
                newRoots.Add(key.Value);
            }
            directories[key.Value].Add(file);
            if (!touched.TryGetValue(key.Value, out List<ProjectedFileCandidate>? newChildren))
            {
                newChildren = [];
                touched.Add(key.Value, newChildren);
            }
            newChildren.Add(file);
        }
        return (
            touched.Select(pair => directories[pair.Key].Snapshot(
                pair.Value,
                absorbed.TryGetValue(pair.Key, out List<PeerDirectoryIdentity>? identities)
                    ? identities : [])).ToArray(),
            removed);
    }

    private PeerPathKey? FindDirectoryRoot(string username, string path)
    {
        var exact = new PeerPathKey(username, path);
        if (directories.ContainsKey(exact)) return exact;
        for (int separator = 0; separator < path.Length; separator++)
        {
            if (separator == 0 || path[separator] is not ('\\' or '/')) continue;
            var ancestor = new PeerPathKey(username, path[..separator]);
            if (directories.ContainsKey(ancestor)) return ancestor;
        }
        return null;
    }

    private static bool IsRemoteDescendant(string candidate, string root)
        => candidate.Length > root.Length
            && candidate.StartsWith(root, StringComparison.Ordinal)
            && candidate[root.Length] is '\\' or '/';

    private static string? PrefixUpperBound(string prefix)
    {
        for (int index = prefix.Length - 1; index >= 0; index--)
        {
            if (prefix[index] == char.MaxValue) continue;
            return prefix[..index] + (char)(prefix[index] + 1);
        }
        return null;
    }

    private (
        IReadOnlyList<SearchViewProjectedDirectory> Changed,
        IReadOnlyList<PeerDirectoryIdentity> Removed) ApplyAlbumDirectories(
        IEnumerable<AlbumFolder> changedFolders,
        IEnumerable<PeerDirectoryIdentity> removedFolders)
    {
        var changed = new List<SearchViewProjectedDirectory>();
        var removed = new List<PeerDirectoryIdentity>();
        foreach (PeerDirectoryIdentity directory in removedFolders)
            RemoveAlbumDirectory(directory, removed);
        foreach (AlbumFolder folder in changedFolders)
        {
            // Visibility belongs to this directory owner. Only changed folders
            // need checking; do not rescan/copy the complete folder snapshot or
            // maintain a second map of the same visible membership.
            if (projectionKind == SearchViewProjectionKind.AlbumDirectories && !MatchesAlbumFolder(folder))
            {
                RemoveAlbumDirectory(folder.DirectoryIdentity, removed);
                continue;
            }
            var directoryKey = new PeerPathKey(folder.Username, folder.FolderPath);
            IReadOnlyList<ProjectedFileCandidate> currentFiles = folder.ProjectionFiles
                ?? throw new InvalidDataException("A search folder has no projection facts.");
            IReadOnlyList<ProjectedFileCandidate> newChildren;
            if (!albumDirectoryChildren.TryGetValue(directoryKey, out var currentKeys))
            {
                currentKeys = new(currentFiles.Count);
                foreach (ProjectedFileCandidate file in currentFiles)
                    currentKeys.Add((new(file.Input.Username, file.Input.Filename), file.Input.Visibility));
                newChildren = currentFiles;
            }
            else
            {
                // The folder projector only appends facts to a surviving root.
                // Absorbed/rejected roots retire their entire set below; reusing
                // this owner avoids copying the growing prefix on every update.
                var added = new List<ProjectedFileCandidate>();
                foreach (ProjectedFileCandidate file in currentFiles)
                    if (currentKeys.Add((new(file.Input.Username, file.Input.Filename), file.Input.Visibility)))
                        added.Add(file);
                newChildren = added;
            }
            ProjectedFileCandidate best = folder.BestProjectionFile
                ?? throw new InvalidDataException("An album directory has no retained best-child evidence.");
            long publicCount = 0;
            long lockedCount = 0;
            long publicSize = 0;
            long lockedSize = 0;
            long preferred = 0;
            foreach (ProjectedFileCandidate file in currentFiles)
            {
                long size = Math.Max(0, file.Input.Size);
                if (file.ConditionFacts.PreferenceTier == SearchPreferenceTier.Preferred) preferred++;
                if (file.Input.Visibility == SearchResultVisibility.Locked)
                {
                    lockedCount++;
                    lockedSize = checked(lockedSize + size);
                }
                else
                {
                    publicCount++;
                    publicSize = checked(publicSize + size);
                }
            }
            var projectedDirectory = new SearchViewProjectedDirectory(
                folder.DirectoryIdentity,
                publicCount,
                lockedCount,
                publicSize,
                lockedSize,
                best,
                newChildren,
                folder.IsFullyRetrieved,
                folder.IsFullyRetrieved ? currentFiles.Count : null,
                folder.IsFullyRetrieved
                    ? currentFiles.Sum(file => Math.Max(0, file.Input.Size))
                    : null);
            if (projectionKind == SearchViewProjectionKind.AlbumDirectories)
            {
                if (albumDirectoryCounts.TryGetValue(directoryKey, out var before))
                    ChangeAlbumProjectionFileCounts(before, -1);
                var counts = new AlbumProjectionFileCounts(currentFiles.Count, publicCount, lockedCount,
                    preferred, currentFiles.Count - preferred);
                albumDirectoryCounts[directoryKey] = counts;
                ChangeAlbumProjectionFileCounts(counts, 1);
            }
            albumDirectories[directoryKey] = projectedDirectory;
            albumDirectoryChildren[directoryKey] = currentKeys;
            changed.Add(projectedDirectory);
        }

        return (changed, removed);
    }

    private void RemoveAlbumDirectory(PeerDirectoryIdentity directory, List<PeerDirectoryIdentity> removed)
    {
        var key = new PeerPathKey(directory.Username, directory.FolderPath);
        if (!albumDirectories.Remove(key)) return;
        albumDirectoryChildren.Remove(key);
        if (albumDirectoryCounts.Remove(key, out var counts))
            ChangeAlbumProjectionFileCounts(counts, -1);
        removed.Add(directory);
    }

    private (
        IReadOnlyList<SearchViewProjectedDirectory> Changed,
        IReadOnlyList<PeerDirectoryIdentity> Removed) ApplyVisibleAggregateAlbumDirectories(
        IEnumerable<PeerPathKey> affectedFolders)
    {
        var changed = new List<AlbumFolder>();
        var removed = new List<PeerDirectoryIdentity>();
        foreach (PeerPathKey key in affectedFolders)
        {
            if (aggregateAlbumProjector!.FindFolderForSearchView(key) is { } current
                && visibleAggregateAlbumGroups.ContainsKey(current.GroupIndex))
            {
                // A surviving root only appends immutable facts. Unchanged
                // options need no directory rewrite when other peers arrive.
                if (!albumDirectories.TryGetValue(key, out var prior)
                    || prior.PublicMatchingFileCount + prior.LockedMatchingFileCount
                        != current.Folder.ProjectionFiles!.Count)
                    changed.Add(current.Folder);
            }
            else if (albumDirectories.TryGetValue(key, out var prior))
                removed.Add(prior.Directory);
        }
        return ApplyAlbumDirectories(changed, removed);
    }

    private IReadOnlyList<ProjectedFileCandidate> NewAlbumFiles(
        IReadOnlyList<SearchViewProjectedDirectory> directories)
    {
        var files = new List<ProjectedFileCandidate>();
        foreach (var directory in directories)
            foreach (var file in directory.NewChildren)
                if (publishedAlbumSequences.Add(file.Input.Sequence))
                    files.Add(file);
        return files;
    }

    private void CountProjectedFile(ProjectedFileCandidate file)
    {
        projectedFileCount++;
        if (file.Input.Visibility == SearchResultVisibility.Locked)
            projectedLockedFileCount++;
        else
            projectedPublicFileCount++;
        if (file.ConditionFacts.PreferenceTier == SearchPreferenceTier.Preferred)
            preferredFileCount++;
        else
            otherFileCount++;
    }

    private (
        IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> Changed,
        IReadOnlyList<PeerDirectoryIdentity> Removed) ApplyAggregateAlbumGroups(
        IReadOnlyList<int> affectedIndexes,
        HashSet<PeerPathKey> affectedFolders)
    {
        var changed = new List<SearchViewProjectedAggregateAlbumGroup>();
        var removed = new List<PeerDirectoryIdentity>();
        foreach (int index in affectedIndexes)
        {
            visibleAggregateAlbumGroups.TryGetValue(index, out var previous);
            SearchViewProjectedAggregateAlbumGroup? group = aggregateAlbumProjector!
                .SnapshotGroupForSearchView(index);
            if (group != null && !MatchesAggregateAlbumGroup(group))
                group = null;
            if (previous != null)
            {
                foreach (AlbumFolder folder in previous.Options)
                    affectedFolders.Add(new(folder.Username, folder.FolderPath));
                topLevelItemCount--;
                selectableOptionCount -= previous.SelectableOptionCount;
                ChangeAlbumProjectionFileCounts(previous.FileCounts, -1);
                visibleAggregateAlbumGroups.Remove(index);
                int priorIdentityCount = visibleAggregateAlbumIdentities[previous.StableIdentity];
                if (priorIdentityCount == 1)
                    visibleAggregateAlbumIdentities.Remove(previous.StableIdentity);
                else
                    visibleAggregateAlbumIdentities[previous.StableIdentity] = priorIdentityCount - 1;
            }
            if (group == null)
            {
                if (previous != null)
                    removed.Add(previous.StableIdentity);
                continue;
            }

            foreach (AlbumFolder folder in group.Options)
                affectedFolders.Add(new(folder.Username, folder.FolderPath));
            AlbumProjectionFileCounts counts = group.FileCounts;
            topLevelItemCount++;
            selectableOptionCount = checked(selectableOptionCount + group.SelectableOptionCount);
            ChangeAlbumProjectionFileCounts(counts, 1);
            // Retain the immutable publication itself: its old membership is
            // needed when a later change hides or regroups untouched options.
            visibleAggregateAlbumGroups[index] = group;
            visibleAggregateAlbumIdentities[group.StableIdentity] =
                visibleAggregateAlbumIdentities.GetValueOrDefault(group.StableIdentity) + 1;
            if (previous == null
                || previous.StableIdentity != group.StableIdentity
                || !SameAggregateAlbumMembership(previous, group))
            {
                if (previous != null && previous.StableIdentity != group.StableIdentity)
                    removed.Add(previous.StableIdentity);
                changed.Add(group);
            }
        }
        return (changed
            .OrderByDescending(group => group.ShareCount)
            .ThenBy(group => group.Index)
            .ToArray(), removed
            .Where(identity => !visibleAggregateAlbumIdentities.ContainsKey(identity))
            .Distinct()
            .ToArray());
    }

    private static bool SameAggregateAlbumMembership(
        SearchViewProjectedAggregateAlbumGroup left,
        SearchViewProjectedAggregateAlbumGroup right)
    {
        if (left.ShareCount != right.ShareCount
            || left.SelectableOptionCount != right.SelectableOptionCount
            || left.Representative.Username != right.Representative.Username
            || left.Representative.FolderPath != right.Representative.FolderPath
            || (left.Query.Artist ?? "") != (right.Query.Artist ?? "")
            || (left.Query.Album ?? "") != (right.Query.Album ?? "")
            || (left.Query.SearchHint ?? "") != (right.Query.SearchHint ?? "")
            || left.Options.Count != right.Options.Count)
            return false;
        for (int index = 0; index < left.Options.Count; index++)
            if (left.Options[index].Username != right.Options[index].Username
                || left.Options[index].FolderPath != right.Options[index].FolderPath)
                return false;
        return true;
    }

    private void ChangeAlbumProjectionFileCounts(AlbumProjectionFileCounts counts, int direction)
    {
        projectedFileCount += direction * counts.Total;
        projectedPublicFileCount += direction * counts.Public;
        projectedLockedFileCount += direction * counts.Locked;
        preferredFileCount += direction * counts.Preferred;
        otherFileCount += direction * counts.Other;
    }

    private static string RemoteContainingDirectory(string filename)
    {
        int separator = Math.Max(
            filename.LastIndexOf('\\'),
            filename.LastIndexOf('/'));
        return separator <= 0 ? "" : filename[..separator];
    }

    private sealed class DirectoryState(PeerDirectoryIdentity directory)
    {
        private ProjectedFileCandidate? best;
        private long publicCount;
        private long lockedCount;
        private long publicBytes;
        private long lockedBytes;

        public void Add(ProjectedFileCandidate file)
        {
            if (file.Input.Visibility == SearchResultVisibility.Locked)
            {
                lockedCount++;
                lockedBytes = checked(lockedBytes + Math.Max(0, file.Input.Size));
            }
            else
            {
                publicCount++;
                publicBytes = checked(publicBytes + Math.Max(0, file.Input.Size));
            }
            if (best == null
                || SearchProjectionSortKeyComparer.Instance.Compare(
                    file.SortKey,
                    best.SortKey) < 0
                || file.SortKey == best.SortKey && file.Input.Sequence < best.Input.Sequence)
                best = file;
        }

        public void Absorb(DirectoryState child)
        {
            publicCount = checked(publicCount + child.publicCount);
            lockedCount = checked(lockedCount + child.lockedCount);
            publicBytes = checked(publicBytes + child.publicBytes);
            lockedBytes = checked(lockedBytes + child.lockedBytes);
            if (child.best != null
                && (best == null
                    || SearchProjectionSortKeyComparer.Instance.Compare(
                        child.best.SortKey, best.SortKey) < 0
                    || child.best.SortKey == best.SortKey
                        && child.best.Input.Sequence < best.Input.Sequence))
                best = child.best;
        }

        public SearchViewProjectedDirectory Snapshot(
            IReadOnlyList<ProjectedFileCandidate> newChildren,
            IReadOnlyList<PeerDirectoryIdentity>? absorbedDirectories = null)
            => new(
                directory,
                publicCount,
                lockedCount,
                publicBytes,
                lockedBytes,
                (lockedCount > 0 ? best?.ForLockedFolder() : best)
                    ?? throw new InvalidOperationException("A projected directory has no best child."),
                newChildren,
                AbsorbedDirectories: absorbedDirectories);
    }
}
