using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

/// <summary>
/// Filename-only candidates for resolving one projected album folder. This is
/// deliberately broader than a subtree: an admitted ancestor absorbs the target,
/// including when a sibling disc directory supplies that ancestor's files.
/// Conditions and grouping still run through the ordinary projection plan.
/// </summary>
public readonly struct AlbumFolderInputScope(AlbumQuery query, string folderPath)
{
    private readonly bool combineDiscs = SearchResultProjector.CanCombineDiscDirectories(query);

    public bool Includes(string filename)
    {
        // Descendants always participate; disc collapse can move their folder
        // up to an ancestor, which must participate too. Avoid parsing the
        // directory again when the requested branch contains most of a peer.
        if (IsDescendant(filename, folderPath)) return true;
        ReadOnlySpan<char> directory = SearchResultProjector.AlbumDirectoryPath(filename.AsSpan(), combineDiscs);
        return !directory.IsEmpty && (directory.SequenceEqual(folderPath)
            || IsDescendant(directory, folderPath) || IsDescendant(folderPath, directory));
    }

    private static bool IsDescendant(ReadOnlySpan<char> path, ReadOnlySpan<char> ancestor)
        => path.Length > ancestor.Length && path.StartsWith(ancestor, StringComparison.Ordinal)
            && path[ancestor.Length] is '\\' or '/';
}

internal sealed record EvaluatedAlbumProjectionInput(
    ProjectedFileCandidate File,
    ResultSorter.SortEntry SortEntry,
    string? DirectoryPath)
{
    public SearchProjectionInput Input => File.Input;
}

// Shared album-folder projection policy. One-shot projection and incremental
// projection should differ only in how raw results are collected; filtering,
// grouping, and ranking must stay here to avoid local/remote/result-view drift.
// Quality covers the observations supplied by the caller, including locked files
// in read-only views. Automatic discovery supplies only public files, so a private
// quality match cannot qualify an otherwise nonmatching folder for execution.
internal readonly struct AlbumFolderProjectionPlan
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly ConditionSatisfactionPolicy.AlbumSearchFilter projectionFilter;
    private readonly FolderSortMode sortMode;
    private readonly bool combineDiscs;
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
        combineDiscs = SearchResultProjector.CanCombineDiscDirectories(query);
        projectionFilter = ConditionSatisfactionPolicy.CreateAlbumSearchFilter(query, search);
        aggregateSortKeyContext = ResultSorter.CreateSortKeyContext(
            [],
            projectionFilter.SortQuery,
            search,
            userSuccessCounts ?? new ConcurrentDictionary<string, int>(),
            useBracketCheck: false,
            useInfer: false,
            albumMode: true,
            ignoreStringSortConditions: ignoreStringSortConditions,
            necessaryConditionEvaluator: projectionFilter.Satisfies);
    }

    internal bool Includes((SearchResponse Response, SlFile File) result)
        => projectionFilter.Satisfies(result);

    public bool Includes(SearchProjectionInput result)
        => projectionFilter.Satisfies(result);

    public EvaluatedAlbumProjectionInput? Evaluate(
        SearchProjectionInput input,
        int originalIndex)
    {
        ResultSorter.SortEntry? entry = ResultSorter.CreateSortEntry(
            input,
            aggregateSortKeyContext,
            originalIndex);
        return entry is { } admitted
            && admitted.Key.ConditionFacts.NecessaryConditionsSatisfied
                ? new EvaluatedAlbumProjectionInput(
                    new ProjectedFileCandidate(input, admitted.Key.ConditionFacts, admitted.Key.PersistenceKey), admitted,
                    SearchResultProjector.AlbumDirectoryPath(input.Filename, combineDiscs))
                : null;
    }

    public List<EvaluatedAlbumProjectionInput> EvaluateToList(
        IEnumerable<SearchProjectionInput> results)
    {
        var evaluated = new List<EvaluatedAlbumProjectionInput>();
        int index = 0;
        foreach (SearchProjectionInput input in results)
        {
            if (Evaluate(input, index++) is { } admitted)
                evaluated.Add(admitted);
        }
        return evaluated;
    }

    internal List<(SearchResponse Response, SlFile File)> FilterToList(
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

    public List<SearchProjectionInput> FilterToList(IEnumerable<SearchProjectionInput> results)
        => results.Where(Includes).ToList();

    public List<AlbumFolder> ProjectEvaluatedResults(
        IEnumerable<EvaluatedAlbumProjectionInput> evaluatedResults)
        => SearchResultProjector.AlbumFoldersFromResults(
            evaluatedResults, query, search,
            useAlbumFolderQualityRanking: sortMode == FolderSortMode.AlbumRanked);
}
