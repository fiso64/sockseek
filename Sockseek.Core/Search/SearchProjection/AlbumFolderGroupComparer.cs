using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

internal sealed class AlbumFolderGroupComparer : IComparer<AlbumFolder>
{
    private readonly ResultSorter.SortKeyContext keyContext;
    private readonly IReadOnlyDictionary<PeerPathKey, int> folderOrder;
    private readonly Dictionary<PeerPathKey, ResultSorter.SortEntry?> bestEntries = [];

    public AlbumFolderGroupComparer(
        AlbumQuery query,
        SearchSettings search,
        IReadOnlyDictionary<PeerPathKey, int> folderOrder,
        ConcurrentDictionary<string, int>? peerFailureCounts = null)
    {
        this.folderOrder = folderOrder;
        keyContext = new ResultSorter.SortKeyContext(
            SearchResultProjector.AlbumFileMatchQuery(query),
            search,
            peerFailureCounts ?? new ConcurrentDictionary<string, int>(),
            albumMode: true,
            ignoreStringSortConditions: true);
    }

    public void ClearCache()
        => bestEntries.Clear();

    public int Compare(AlbumFolder? x, AlbumFolder? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x == null)
            return 1;
        if (y == null)
            return -1;

        int rank = CompareRank(BestEntry(x), BestEntry(y),
            x.SearchAudioQualityCoverage, y.SearchAudioQualityCoverage, compareCoverage: true);
        if (rank != 0) return rank;

        int orderComparison = GetFolderOrder(x).CompareTo(GetFolderOrder(y));
        if (orderComparison != 0)
            return orderComparison;

        int usernameComparison = string.Compare(x.Username, y.Username, StringComparison.Ordinal);
        return usernameComparison != 0
            ? usernameComparison
            : string.Compare(x.FolderPath, y.FolderPath, StringComparison.Ordinal);
    }

    private ResultSorter.SortEntry? BestEntry(AlbumFolder folder)
    {
        if (folder.SearchGroupSortEntry.HasValue)
            return folder.SearchGroupSortEntry;

        PeerPathKey key = FolderKey(folder);
        if (bestEntries.TryGetValue(key, out var cached))
            return cached;

        if (folder.HasSearchMetadata)
        {
            bestEntries[key] = null;
            return null;
        }

        ResultSorter.SortEntry? best = null;
        foreach (var file in folder.Files)
        {
            var entry = ResultSorter.CreateSortEntry(
                file.Candidate,
                keyContext,
                originalIndex: 0);
            if (!entry.HasValue)
                continue;

            if (!best.HasValue)
            {
                best = entry;
                continue;
            }

            int comparison = ResultSorter.SortEntryComparer.Instance.Compare(entry.Value, best.Value);
            if (comparison < 0)
                best = entry;
        }

        bestEntries[key] = best;
        return best;
    }

    private int GetFolderOrder(AlbumFolder folder)
        => folderOrder.TryGetValue(FolderKey(folder), out int order)
            ? order
            : int.MaxValue;

    private static PeerPathKey FolderKey(AlbumFolder folder)
        => new(folder.Username, folder.FolderPath);

    // Compare identity and length before folder-wide quality. Remaining file
    // criteria follow coverage, so an unrelated FLAC cannot outrank the requested album.
    internal static int CompareRank(
        ResultSorter.SortEntry? x, ResultSorter.SortEntry? y,
        in AlbumAudioQualityCoverage xQuality, in AlbumAudioQualityCoverage yQuality,
        bool compareCoverage)
    {
        if (x.HasValue != y.HasValue) return x.HasValue ? -1 : 1;
        int rank = 0;
        if (x is { } first && y is { } second)
            rank = ResultSorter.AlbumBeforeQualitySortEntryComparer.Instance.Compare(first, second);
        if (rank == 0 && compareCoverage)
        {
            rank = yQuality.Format.Bucket.CompareTo(xQuality.Format.Bucket);
            if (rank == 0) rank = yQuality.Bitrate.Bucket.CompareTo(xQuality.Bitrate.Bucket);
            if (rank == 0) rank = yQuality.SampleRate.Bucket.CompareTo(xQuality.SampleRate.Bucket);
            if (rank == 0) rank = yQuality.BitDepth.Bucket.CompareTo(xQuality.BitDepth.Bucket);
        }
        return rank == 0 && x.HasValue && y.HasValue
            ? ResultSorter.SortEntryComparer.Instance.Compare(x.Value, y.Value) : rank;
    }
}
