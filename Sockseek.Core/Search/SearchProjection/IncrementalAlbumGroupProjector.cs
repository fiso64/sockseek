using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

// Folder admission owns exact peer/path/visibility deduplication. Counting its
// current members reuses recorded condition facts and never evaluates policy.
internal readonly record struct AlbumProjectionFileCounts(
    long Total, long Public, long Locked, long Preferred, long Other)
{
    public static AlbumProjectionFileCounts FromFolder(AlbumFolder folder)
    {
        long publicFiles = 0, lockedFiles = 0, preferred = 0;
        if (folder.ProjectionFiles is { } projected)
        {
            foreach (ProjectedFileCandidate file in projected)
            {
                if (file.Input.Visibility == SearchResultVisibility.Locked) lockedFiles++;
                else publicFiles++;
                if (file.ConditionFacts.PreferenceTier == SearchPreferenceTier.Preferred) preferred++;
            }
            return new(projected.Count, publicFiles, lockedFiles, preferred, projected.Count - preferred);
        }
        // Explicitly constructed/browsed folders already own candidate files.
        foreach (AlbumFile file in folder.Files)
        {
            if (file.Candidate.Visibility == SearchResultVisibility.Locked) lockedFiles++;
            else publicFiles++;
            if (file.Candidate.ProjectionFacts?.PreferenceTier == SearchPreferenceTier.Preferred) preferred++;
        }
        long total = publicFiles + lockedFiles;
        return new(total, publicFiles, lockedFiles, preferred, total - preferred);
    }

    public static AlbumProjectionFileCounts operator +(AlbumProjectionFileCounts a, AlbumProjectionFileCounts b)
        => new(a.Total + b.Total, a.Public + b.Public, a.Locked + b.Locked, a.Preferred + b.Preferred, a.Other + b.Other);
    public static AlbumProjectionFileCounts operator -(AlbumProjectionFileCounts a, AlbumProjectionFileCounts b)
        => new(a.Total - b.Total, a.Public - b.Public, a.Locked - b.Locked, a.Preferred - b.Preferred, a.Other - b.Other);
}

public sealed record SearchViewProjectedAlbumGroup(
    int Index,
    PeerDirectoryIdentity StableIdentity,
    AlbumQuery Query,
    int ShareCount,
    long SelectableOptionCount,
    AlbumFolder Representative,
    IReadOnlyList<AlbumFolder> Options)
{
    internal AlbumProjectionFileCounts FileCounts { get; init; }
}

public sealed record AlbumGroupsSearchViewChanges(
    IReadOnlyList<int> AffectedGroupIndexes);

public sealed class IncrementalAlbumGroupProjector
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly int maxDiff;
    private readonly Dictionary<int, Dictionary<int, List<AlbumGroupsBucket>>> byTrackCountAndFirstLength = [];
    private readonly Dictionary<AlbumFolder, SongQuery?> representativeQueries = [];
    private readonly Dictionary<PeerPathKey, int> folderOrder = [];
    private readonly Dictionary<PeerPathKey, AlbumGroupsBucket> bucketByFolder = [];
    private readonly AlbumFolderGroupComparer folderComparer;
    private readonly List<AlbumGroupsBucket> buckets = [];
    private readonly HashSet<PeerPathKey> seenFolders = [];
    private List<AlbumFolder> orderedFolders = [];

    public IncrementalAlbumGroupProjector(AlbumQuery query, SearchSettings search)
    {
        this.query = query;
        this.search = search;
        maxDiff = search.GroupLengthTol;
        folderComparer = new AlbumFolderGroupComparer(query, search, folderOrder);
    }

    public int Count => seenFolders.Count;

    public void Clear()
    {
        byTrackCountAndFirstLength.Clear();
        representativeQueries.Clear();
        folderOrder.Clear();
        bucketByFolder.Clear();
        folderComparer.ClearCache();
        buckets.Clear();
        seenFolders.Clear();
        orderedFolders.Clear();
    }

    public int AddRange(IEnumerable<AlbumFolder> albums)
    {
        int added = 0;
        foreach (var folder in albums)
        {
            var key = new PeerPathKey(folder.Username, folder.FolderPath);
            if (!seenFolders.Add(key))
                continue;

            folderOrder[key] = orderedFolders.Count;
            orderedFolders.Add(folder);
            Add(folder);
            added++;
        }

        return added;
    }

    public int ApplyChanges(AlbumFolderProjectionChanges changes)
        => ApplyChanges(changes, null);

    public AlbumGroupsSearchViewChanges ApplyChangesForSearchView(
        AlbumFolderProjectionChanges changes)
    {
        var affected = new HashSet<int>();
        ApplyChanges(changes, affected);
        return new(affected.Order().ToArray());
    }

    private int ApplyChanges(
        AlbumFolderProjectionChanges changes,
        HashSet<int>? affected)
    {
        if (!changes.HasChanges)
            return 0;

        if (TryApplyStableUpdates(changes, affected, out int updated))
            return updated;

        int boundary = FindStablePrefixBoundary(changes);
        for (int index = orderedFolders.Count - 1; index >= boundary; index--)
        {
            int? removedIndex = Remove(orderedFolders[index]);
            if (removedIndex.HasValue)
                affected?.Add(removedIndex.Value);
        }
        RemoveEmptySuffixBuckets();

        orderedFolders = changes.Folders.Take(boundary).ToList();
        UpdateFolderOrder(changes.Folders);
        folderComparer.ClearCache();
        int processed = 0;
        for (int index = boundary; index < changes.Folders.Count; index++)
        {
            AlbumFolder folder = changes.Folders[index];
            PeerPathKey key = FolderKey(folder);
            if (!seenFolders.Add(key))
                continue;
            orderedFolders.Add(folder);
            int? addedIndex = Add(folder);
            if (addedIndex.HasValue)
                affected?.Add(addedIndex.Value);
            processed++;
        }
        return processed;
    }

    private bool TryApplyStableUpdates(
        AlbumFolderProjectionChanges changes,
        HashSet<int>? affected,
        out int processed)
    {
        processed = 0;
        if (changes.Added.Count != 0 || changes.Removed.Count != 0
            || changes.Updated.Count == 0
            || changes.Folders.Count != orderedFolders.Count)
            return false;

        // A changed folder can define a bucket for every later folder. Only
        // replace options in place when the complete input order and all
        // matching identities are unchanged; otherwise replay the suffix.
        for (int index = 0; index < orderedFolders.Count; index++)
            if (FolderKey(orderedFolders[index]) != FolderKey(changes.Folders[index]))
                return false;
        foreach (AlbumFolder folder in changes.Updated)
        {
            PeerPathKey key = FolderKey(folder);
            if (!folderOrder.TryGetValue(key, out int index)
                || !bucketByFolder.ContainsKey(key)
                || !GetSearchSortedAudioLengths(orderedFolders[index])
                    .SequenceEqual(GetSearchSortedAudioLengths(folder))
                || !StringComparer.Ordinal.Equals(
                    RepresentativeAudioFilename(orderedFolders[index]),
                    RepresentativeAudioFilename(folder)))
                return false;
        }

        folderComparer.ClearCache();
        foreach (AlbumFolder folder in changes.Updated)
        {
            PeerPathKey key = FolderKey(folder);
            int index = folderOrder[key];
            AlbumFolder oldFolder = orderedFolders[index];
            AlbumGroupsBucket bucket = bucketByFolder[key];
            if (bucket.RemoveVersion(key) == null)
                throw new InvalidDataException("An updated album folder is missing from its group bucket.");
            representativeQueries.Remove(oldFolder);
            bucket.AddVersion(folder, CompareFolders);
            orderedFolders[index] = folder;
            affected?.Add(bucket.Index);
            processed++;
        }
        return true;
    }

    public void Reset(IEnumerable<AlbumFolder> albums)
    {
        var albumList = albums as IReadOnlyList<AlbumFolder> ?? albums.ToList();
        Clear();
        UpdateFolderOrder(albumList);
        foreach (AlbumFolder folder in albumList)
        {
            PeerPathKey key = FolderKey(folder);
            if (!seenFolders.Add(key))
                continue;
            orderedFolders.Add(folder);
            Add(folder);
        }
    }

    internal void ResetBatch(IEnumerable<AlbumFolder> albums)
        => Reset(albums);

    public List<AlbumJob> Snapshot()
        => buckets
            .Where(x => x.UserCount >= search.EffectiveMinGroupShares)
            .OrderByDescending(x => x.UserCount)
            .ThenBy(x => x.Index)
            .Select(x => SearchResultProjector.CreateAlbumGroupJob(query, x.Versions.ToList()))
            .ToList();

    public List<SearchViewProjectedAlbumGroup> SnapshotForSearchView()
        => buckets
            .Where(bucket => bucket.UserCount >= search.EffectiveMinGroupShares)
            .OrderByDescending(bucket => bucket.UserCount)
            .ThenBy(bucket => bucket.Index)
            .Select(ProjectGroup)
            .ToList();

    internal SearchViewProjectedAlbumGroup? SnapshotGroupForSearchView(int index)
        => index < buckets.Count && buckets[index].UserCount >= search.EffectiveMinGroupShares
            ? ProjectGroup(buckets[index])
            : null;

    internal (AlbumFolder Folder, int GroupIndex)? FindFolderForSearchView(PeerPathKey key)
        => bucketByFolder.TryGetValue(key, out var bucket)
            ? (orderedFolders[folderOrder[key]], bucket.Index)
            : null;

    private SearchViewProjectedAlbumGroup ProjectGroup(AlbumGroupsBucket bucket)
    {
        AlbumProjectionFileCounts counts = bucket.FileCounts;
        IReadOnlyList<AlbumFolder> options = bucket.Versions.ToArray();
        AlbumFolder representative = options[0];
        string? itemName = string.IsNullOrWhiteSpace(representative.FolderPath)
            ? null
            : Utils.GetBaseNameSlsk(representative.FolderPath);
        AlbumQuery groupQuery = itemName == null
            ? new AlbumQuery(query)
            : new AlbumQuery(query) { Album = itemName };
        return new SearchViewProjectedAlbumGroup(
            bucket.Index,
            bucket.RepresentativeFolder.DirectoryIdentity,
            groupQuery,
            bucket.UserCount,
            bucket.SelectableOptionCount,
            representative,
            options) { FileCounts = counts };
    }

    private int? Add(AlbumFolder folder)
    {
        var sortedLengths = GetSearchSortedAudioLengths(folder);
        if (sortedLengths.Length == 0)
            return null;

        if (!byTrackCountAndFirstLength.TryGetValue(sortedLengths.Length, out var byFirstLength))
        {
            byFirstLength = [];
            byTrackCountAndFirstLength.Add(sortedLengths.Length, byFirstLength);
        }

        AlbumGroupsBucket? matchingBucket = null;
        int firstLengthBand = LengthBand(sortedLengths[0]);
        for (int bandOffset = -1; bandOffset <= 1; bandOffset++)
        {
            if (!byFirstLength.TryGetValue(firstLengthBand + bandOffset, out var candidates))
                continue;

            for (int i = 0; i < candidates.Count; i++)
            {
                var bucket = candidates[i];
                if (!Searcher.AlbumLengthsAreSimilar(sortedLengths, bucket.Lengths, maxDiff))
                    continue;

                if (sortedLengths.Length == 1 && !SingleTrackAlbumsMatch(bucket.RepresentativeFolder, folder))
                    continue;

                if (matchingBucket == null || bucket.Index < matchingBucket.Index)
                    matchingBucket = bucket;
            }
        }

        if (matchingBucket != null)
        {
            matchingBucket.AddVersion(folder, CompareFolders);
            bucketByFolder[FolderKey(folder)] = matchingBucket;
            return matchingBucket.Index;
        }

        var newBucket = new AlbumGroupsBucket(buckets.Count, sortedLengths, folder);
        buckets.Add(newBucket);
        if (!byFirstLength.TryGetValue(firstLengthBand, out var byLength))
        {
            byLength = [];
            byFirstLength.Add(firstLengthBand, byLength);
        }

        byLength.Add(newBucket);
        bucketByFolder[FolderKey(folder)] = newBucket;
        return newBucket.Index;
    }

    private int FindStablePrefixBoundary(AlbumFolderProjectionChanges changes)
    {
        var changed = changes.Added
            .Concat(changes.Updated)
            .Concat(changes.Removed)
            .Select(FolderKey)
            .ToHashSet();
        int common = Math.Min(orderedFolders.Count, changes.Folders.Count);
        for (int index = 0; index < common; index++)
        {
            PeerPathKey oldKey = FolderKey(orderedFolders[index]);
            PeerPathKey newKey = FolderKey(changes.Folders[index]);
            if (oldKey != newKey || changed.Contains(oldKey))
                return index;
        }
        return common;
    }

    private int? Remove(AlbumFolder folder)
    {
        PeerPathKey key = FolderKey(folder);
        seenFolders.Remove(key);
        representativeQueries.Remove(folder);
        if (!bucketByFolder.Remove(key, out AlbumGroupsBucket? bucket))
            return null;
        AlbumFolder? removed = bucket.RemoveVersion(key);
        if (removed != null && !ReferenceEquals(removed, folder))
            representativeQueries.Remove(removed);
        return bucket.Index;
    }

    private void RemoveEmptySuffixBuckets()
    {
        while (buckets.Count > 0 && buckets[^1].Versions.Count == 0)
        {
            AlbumGroupsBucket bucket = buckets[^1];
            buckets.RemoveAt(buckets.Count - 1);
            int firstLengthBand = LengthBand(bucket.Lengths[0]);
            Dictionary<int, List<AlbumGroupsBucket>> byFirstLength =
                byTrackCountAndFirstLength[bucket.Lengths.Length];
            List<AlbumGroupsBucket> band = byFirstLength[firstLengthBand];
            band.Remove(bucket);
            if (band.Count == 0)
                byFirstLength.Remove(firstLengthBand);
            if (byFirstLength.Count == 0)
                byTrackCountAndFirstLength.Remove(bucket.Lengths.Length);
        }
    }

    private void UpdateFolderOrder(IEnumerable<AlbumFolder> folders)
    {
        folderOrder.Clear();
        int index = 0;
        foreach (var folder in folders)
            folderOrder[FolderKey(folder)] = index++;
    }

    private int CompareFolders(AlbumFolder x, AlbumFolder y)
        => folderComparer.Compare(x, y);

    private static PeerPathKey FolderKey(AlbumFolder folder)
        => new(folder.Username, folder.FolderPath);

    private int LengthBand(int length)
    {
        long bandSize = Math.Max(1L, (long)maxDiff + 1);
        return (int)Math.Floor(length / (double)bandSize);
    }

    private static int[] GetSearchSortedAudioLengths(AlbumFolder folder)
        => folder.HasSearchMetadata
            ? folder.SearchSortedAudioLengths
            : folder.Files
                .Where(f => !f.IsNotAudio)
                .Select(f => f.Candidate.Length ?? -1)
                .OrderBy(x => x)
                .ToArray();

    private bool SingleTrackAlbumsMatch(AlbumFolder a, AlbumFolder b)
    {
        SongQuery? q1 = RepresentativeAudioQuery(a);
        SongQuery? q2 = RepresentativeAudioQuery(b);
        if (q1 == null || q2 == null)
            return true;

        return (q2.Artist.ContainsIgnoreCase(q1.Artist) || q1.Artist.ContainsIgnoreCase(q2.Artist))
            && (q2.Title.ContainsIgnoreCase(q1.Title) || q1.Title.ContainsIgnoreCase(q2.Title));
    }

    private SongQuery? RepresentativeAudioQuery(AlbumFolder folder)
    {
        if (representativeQueries.TryGetValue(folder, out var query))
            return query;

        string? filename = RepresentativeAudioFilename(folder);
        query = filename == null
            ? null
            : Searcher.InferSongQuery(filename, new SongQuery());
        representativeQueries.Add(folder, query);
        return query;
    }

    private static string? RepresentativeAudioFilename(AlbumFolder folder)
        => folder.HasSearchMetadata
            ? folder.SearchRepresentativeAudioFilename
            : folder.Files.FirstOrDefault(f => !f.IsNotAudio)?.Filename;

    private sealed class AlbumGroupsBucket
    {
        public int Index { get; }
        public int[] Lengths { get; }
        public List<AlbumFolder> Versions { get; }
        public AlbumFolder RepresentativeFolder { get; }
        private readonly Dictionary<string, int> userCounts = new(StringComparer.Ordinal);
        public int UserCount => userCounts.Count;
        private AlbumProjectionFileCounts? fileCounts;
        public long SelectableOptionCount { get; private set; }
        public AlbumProjectionFileCounts FileCounts
        {
            get
            {
                // Automatic execution can keep candidate files lazy. Start this
                // summary only when a result view requests it, then maintain it
                // alongside bucket membership instead of rescanning old options.
                if (fileCounts is null)
                {
                    var counts = default(AlbumProjectionFileCounts);
                    SelectableOptionCount = 0;
                    foreach (AlbumFolder folder in Versions)
                    {
                        var contribution = AlbumProjectionFileCounts.FromFolder(folder);
                        counts += contribution;
                        if (contribution.Public > 0) SelectableOptionCount++;
                    }
                    fileCounts = counts;
                }
                return fileCounts.Value;
            }
        }


        public AlbumGroupsBucket(int index, int[] lengths, AlbumFolder folder)
        {
            Index = index;
            Lengths = lengths;
            Versions = [folder];
            RepresentativeFolder = folder;
            userCounts.Add(folder.Username, 1);
        }

        public void AddVersion(AlbumFolder folder, Comparison<AlbumFolder> comparison)
        {
            int index = Versions.BinarySearch(folder, Comparer<AlbumFolder>.Create(comparison));
            if (index < 0)
                index = ~index;
            Versions.Insert(index, folder);
            if (fileCounts is { } counts)
            {
                var contribution = AlbumProjectionFileCounts.FromFolder(folder);
                fileCounts = counts + contribution;
                if (contribution.Public > 0) SelectableOptionCount++;
            }
            userCounts[folder.Username] = userCounts.TryGetValue(
                folder.Username,
                out int count)
                ? checked(count + 1)
                : 1;
        }

        public AlbumFolder? RemoveVersion(PeerPathKey key)
        {
            int index = Versions.FindIndex(folder => FolderKey(folder) == key);
            if (index < 0)
                return null;
            AlbumFolder removed = Versions[index];
            Versions.RemoveAt(index);
            if (fileCounts is { } counts)
            {
                var contribution = AlbumProjectionFileCounts.FromFolder(removed);
                fileCounts = counts - contribution;
                if (contribution.Public > 0) SelectableOptionCount--;
            }
            int count = userCounts[removed.Username] - 1;
            if (count == 0)
                userCounts.Remove(removed.Username);
            else
                userCounts[removed.Username] = count;
            return removed;
        }
    }
}
