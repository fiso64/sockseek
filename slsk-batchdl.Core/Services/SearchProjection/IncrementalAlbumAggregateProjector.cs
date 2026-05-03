using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;

public sealed class IncrementalAlbumAggregateProjector
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly int maxDiff;
    private readonly Dictionary<int, Dictionary<int, List<AlbumAggregateBucket>>> byTrackCountAndFirstLength = [];
    private readonly Dictionary<AlbumFolder, SongQuery?> representativeQueries = [];
    private readonly List<AlbumAggregateBucket> buckets = [];
    private readonly HashSet<string> seenFolders = new(StringComparer.Ordinal);

    public IncrementalAlbumAggregateProjector(AlbumQuery query, SearchSettings search)
    {
        this.query = query;
        this.search = search;
        maxDiff = search.AggregateLengthTol;
    }

    public int Count => seenFolders.Count;

    public void Clear()
    {
        byTrackCountAndFirstLength.Clear();
        representativeQueries.Clear();
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

    public int ApplyChanges(AlbumFolderProjectionChanges changes)
    {
        if (changes.Updated.Count > 0 || changes.Removed.Count > 0)
        {
            Reset(changes.Folders);
            return changes.Folders.Count;
        }

        return AddRange(changes.Added);
    }

    public void Reset(IEnumerable<AlbumFolder> albums)
    {
        Clear();
        AddRange(albums);
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

                if (sortedLengths.Length == 1 && !SingleTrackAlbumsMatch(bucket.Versions[0], folder))
                    continue;

                if (matchingBucket == null || bucket.Index < matchingBucket.Index)
                    matchingBucket = bucket;
            }
        }

        if (matchingBucket != null)
        {
            matchingBucket.Versions.Add(folder);
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
        public HashSet<string> Users { get; }

        public AlbumAggregateBucket(int index, int[] lengths, AlbumFolder folder)
        {
            Index = index;
            Lengths = lengths;
            Versions = [folder];
            Users = [folder.Username];
        }
    }
}
