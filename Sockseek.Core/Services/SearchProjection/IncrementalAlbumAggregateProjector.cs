using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed class IncrementalAlbumAggregateProjector
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly int maxDiff;
    private readonly Dictionary<int, Dictionary<int, List<AlbumAggregateBucket>>> byTrackCountAndFirstLength = [];
    private readonly Dictionary<AlbumFolder, SongQuery?> representativeQueries = [];
    private readonly Dictionary<string, int> folderOrder = new(StringComparer.Ordinal);
    private readonly AlbumFolderAggregateComparer folderComparer;
    private readonly List<AlbumAggregateBucket> buckets = [];
    private readonly HashSet<string> seenFolders = new(StringComparer.Ordinal);

    public IncrementalAlbumAggregateProjector(AlbumQuery query, SearchSettings search)
    {
        this.query = query;
        this.search = search;
        maxDiff = search.AggregateLengthTol;
        folderComparer = new AlbumFolderAggregateComparer(query, search, folderOrder);
    }

    public int Count => seenFolders.Count;

    public void Clear()
    {
        byTrackCountAndFirstLength.Clear();
        representativeQueries.Clear();
        folderOrder.Clear();
        folderComparer.ClearCache();
        buckets.Clear();
        seenFolders.Clear();
    }

    public int AddRange(IEnumerable<AlbumFolder> albums)
    {
        int added = 0;
        foreach (var folder in albums)
        {
            string key = folder.Username + '\\' + folder.FolderPath;
            if (!seenFolders.Add(key))
                continue;

            Add(folder);
            added++;
        }

        return added;
    }

    // TODO [ARCHITECTURE] [Low priority]: Implement true incremental updates for album aggregates.
    // Currently, if a single album folder is updated or removed, this method drops to O(N) 
    // and rebuilds the entire aggregate state from scratch via Reset().
    // Add explicit logic to remove old AlbumFolder references from their respective 
    // buckets, delete empty buckets, and then AddRange the updated folders, allowing the 
    // UI/Server to actually benefit from incremental performance.
    public int ApplyChanges(AlbumFolderProjectionChanges changes)
    {
        UpdateFolderOrder(changes.Folders);

        if (changes.Updated.Count > 0 || changes.Removed.Count > 0)
        {
            Reset(changes.Folders);
            return changes.Folders.Count;
        }

        return AddRange(changes.Added);
    }

    public void Reset(IEnumerable<AlbumFolder> albums)
    {
        var albumList = albums as IReadOnlyList<AlbumFolder> ?? albums.ToList();
        Clear();
        UpdateFolderOrder(albumList);
        AddRange(albumList);
    }

    public List<AlbumJob> Snapshot()
        => buckets
            .Where(x => x.Users.Count >= search.MinSharesAggregate)
            .OrderByDescending(x => x.Users.Count)
            .ThenBy(x => x.Index)
            .Select(x =>
            {
                var newJob = new AlbumJob(query);
                newJob.Results = x.Versions.ToList();
                var repFolder = x.Versions.FirstOrDefault()?.FolderPath;
                if (!string.IsNullOrWhiteSpace(repFolder))
                    newJob.ItemName = Utils.GetBaseNameSlsk(repFolder);
                return newJob;
            })
            .ToList();

    private void Add(AlbumFolder folder)
    {
        var sortedLengths = GetSearchSortedAudioLengths(folder);
        if (sortedLengths.Length == 0)
            return;

        if (!byTrackCountAndFirstLength.TryGetValue(sortedLengths.Length, out var byFirstLength))
        {
            byFirstLength = [];
            byTrackCountAndFirstLength.Add(sortedLengths.Length, byFirstLength);
        }

        AlbumAggregateBucket? matchingBucket = null;
        int firstLengthBand = LengthBand(sortedLengths[0]);
        for (int bandOffset = -1; bandOffset <= 1; bandOffset++)
        {
            if (!byFirstLength.TryGetValue(firstLengthBand + bandOffset, out var candidates))
                continue;

            for (int i = 0; i < candidates.Count; i++)
            {
                var bucket = candidates[i];
                if (!LengthsAreSimilar(sortedLengths, bucket.Lengths))
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
            matchingBucket.Users.Add(folder.Username);
            return;
        }

        var newBucket = new AlbumAggregateBucket(buckets.Count, sortedLengths, folder);
        buckets.Add(newBucket);
        if (!byFirstLength.TryGetValue(firstLengthBand, out var byLength))
        {
            byLength = [];
            byFirstLength.Add(firstLengthBand, byLength);
        }

        byLength.Add(newBucket);
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

    private static string FolderKey(AlbumFolder folder)
        => folder.Username + '\\' + folder.FolderPath;

    private bool LengthsAreSimilar(int[] s1, int[] s2)
    {
        for (int i = 0; i < s1.Length; i++)
            if (Math.Abs(s1[i] - s2[i]) > maxDiff)
                return false;
        return true;
    }

    private int LengthBand(int length)
    {
        int bandSize = Math.Max(1, maxDiff + 1);
        return (int)Math.Floor(length / (double)bandSize);
    }

    private static int[] GetSearchSortedAudioLengths(AlbumFolder folder)
        => folder.HasSearchMetadata
            ? folder.SearchSortedAudioLengths
            : folder.Files
                .Where(f => !f.IsNotAudio)
                .Select(f => f.ResolvedTarget!.File.Length ?? -1)
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
            : folder.Files.FirstOrDefault(f => !f.IsNotAudio)?.ResolvedTarget?.Filename;

    private sealed class AlbumAggregateBucket
    {
        public int Index { get; }
        public int[] Lengths { get; }
        public List<AlbumFolder> Versions { get; }
        public AlbumFolder RepresentativeFolder { get; }
        public HashSet<string> Users { get; }

        public AlbumAggregateBucket(int index, int[] lengths, AlbumFolder folder)
        {
            Index = index;
            Lengths = lengths;
            Versions = [folder];
            RepresentativeFolder = folder;
            Users = [folder.Username];
        }

        public void AddVersion(AlbumFolder folder, Comparison<AlbumFolder> comparison)
        {
            int index = Versions.BinarySearch(folder, Comparer<AlbumFolder>.Create(comparison));
            if (index < 0)
                index = ~index;
            Versions.Insert(index, folder);
        }
    }
}
