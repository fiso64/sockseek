using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

// Shared album-folder projection policy. One-shot projection and incremental
// projection should differ only in how raw results are collected; filtering,
// grouping, and ranking must stay here to avoid local/remote/result-view drift.
internal readonly struct AlbumFolderProjectionPlan
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly SearchResultProjector.AlbumProjectionFilter projectionFilter;
    private readonly FolderSortMode sortMode;
    private readonly ResultSorter.SortKeyContext aggregateSortKeyContext;

    public AlbumFolderProjectionPlan(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        this.query = query;
        this.search = search;
        this.sortMode = sortMode;
        projectionFilter = SearchResultProjector.CreateAlbumProjectionFilter(query, search);
        aggregateSortKeyContext = ResultSorter.CreateSortKeyContext(
            [],
            projectionFilter.SortQuery,
            search,
            userSuccessCounts ?? new ConcurrentDictionary<string, int>(),
            useBracketCheck: false,
            useInfer: false,
            useLevenshtein: false,
            albumMode: true,
            ignoreStringSortConditions: ignoreStringSortConditions);
    }

    public bool Includes((SearchResponse Response, SlFile File) result)
        => projectionFilter.Satisfies(result);

    public List<(SearchResponse Response, SlFile File)> FilterToList(
        IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        var filtered = new List<(SearchResponse Response, SlFile File)>();

        foreach (var result in results)
        {
            if (Includes(result))
                filtered.Add(result);
        }

        return filtered;
    }

    public List<AlbumFolder> ProjectFilteredResults(
        IEnumerable<(SearchResponse Response, SlFile File)> filteredResults,
        int capacity)
        => SearchResultProjector.AlbumFoldersFromResults(
            filteredResults,
            query,
            search,
            capacity,
            aggregateSortKeyContext: aggregateSortKeyContext,
            useAlbumFolderQualityRanking: sortMode == FolderSortMode.AlbumRanked);
}
