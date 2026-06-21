using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

internal sealed class AlbumFolderAggregateComparer : IComparer<AlbumFolder>
{
    private readonly ResultSorter.SortKeyContext keyContext;
    private readonly IReadOnlyDictionary<string, int> folderOrder;
    private readonly Dictionary<string, ResultSorter.SortEntry?> bestEntries = new(StringComparer.Ordinal);

    public AlbumFolderAggregateComparer(
        AlbumQuery query,
        SearchSettings search,
        IReadOnlyDictionary<string, int> folderOrder)
    {
        this.folderOrder = folderOrder;
        keyContext = ResultSorter.CreateSortKeyContext(
            [],
            SearchResultProjector.AlbumFileMatchQuery(query),
            search,
            new ConcurrentDictionary<string, int>(),
            useBracketCheck: false,
            useInfer: false,
            useLevenshtein: false,
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

        var xBeforeQuality = BestEntry(x);
        var yBeforeQuality = BestEntry(y);
        if (xBeforeQuality.HasValue && yBeforeQuality.HasValue)
        {
            int comparison = ResultSorter.AlbumBeforeQualitySortEntryComparer.Instance.Compare(xBeforeQuality.Value, yBeforeQuality.Value);
            if (comparison != 0)
                return comparison;
        }
        else if (xBeforeQuality.HasValue)
        {
            return -1;
        }
        else if (yBeforeQuality.HasValue)
        {
            return 1;
        }

        int coverageComparison = CompareCoverageBuckets(x.SearchAudioQualityCoverage.Format, y.SearchAudioQualityCoverage.Format);
        if (coverageComparison != 0) return coverageComparison;
        coverageComparison = CompareCoverageBuckets(x.SearchAudioQualityCoverage.Bitrate, y.SearchAudioQualityCoverage.Bitrate);
        if (coverageComparison != 0) return coverageComparison;
        coverageComparison = CompareCoverageBuckets(x.SearchAudioQualityCoverage.SampleRate, y.SearchAudioQualityCoverage.SampleRate);
        if (coverageComparison != 0) return coverageComparison;
        coverageComparison = CompareCoverageBuckets(x.SearchAudioQualityCoverage.BitDepth, y.SearchAudioQualityCoverage.BitDepth);
        if (coverageComparison != 0) return coverageComparison;

        var xBest = BestEntry(x);
        var yBest = BestEntry(y);
        if (xBest.HasValue && yBest.HasValue)
        {
            int comparison = ResultSorter.SortEntryComparer.Instance.Compare(xBest.Value, yBest.Value);
            if (comparison != 0)
                return comparison;
        }
        else if (xBest.HasValue)
        {
            return -1;
        }
        else if (yBest.HasValue)
        {
            return 1;
        }

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
        if (folder.SearchAggregateSortEntry.HasValue)
            return folder.SearchAggregateSortEntry;

        string key = FolderKey(folder);
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
            var candidate = file.ResolvedTarget;
            if (candidate == null)
                continue;

            var entry = ResultSorter.CreateSortEntry(
                candidate.Response,
                candidate.File,
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

    private static string FolderKey(AlbumFolder folder)
        => folder.Username + '\\' + folder.FolderPath;

    private static int CompareCoverageBuckets(AlbumQualityCoverageBucket x, AlbumQualityCoverageBucket y)
        => y.Bucket.CompareTo(x.Bucket);
}
