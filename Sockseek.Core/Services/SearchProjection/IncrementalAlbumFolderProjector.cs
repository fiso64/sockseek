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
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly SongQuery sortQuery;
    private readonly FolderSortMode sortMode;
    private readonly IncrementalResultSorter? sorter;
    private readonly ResultSorter.SortKeyContext? aggregateSortKeyContext;
    private readonly List<(SearchResponse Response, SlFile File)> rawResults = [];
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AlbumFolderSignature> previousSignatures = new(StringComparer.Ordinal);
    private List<AlbumFolder> previousSnapshot = [];

    public IncrementalAlbumFolderProjector(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        this.query = query;
        this.search = search;
        this.sortMode = sortMode;
        sortQuery = SearchResultProjector.AlbumFileMatchQuery(query);
        var successCounts = userSuccessCounts ?? new ConcurrentDictionary<string, int>();
        if (sortMode == FolderSortMode.AlbumRanked)
        {
            sorter = new IncrementalResultSorter(
                sortQuery,
                search,
                successCounts,
                albumMode: true,
                ignoreStringSortConditions: ignoreStringSortConditions);
        }
        else
        {
            aggregateSortKeyContext = ResultSorter.CreateSortKeyContext(
                [],
                sortQuery,
                search,
                successCounts,
                useBracketCheck: false,
                useInfer: false,
                useLevenshtein: false,
                albumMode: true,
                ignoreStringSortConditions: ignoreStringSortConditions);
        }
    }

    public int Count => sortMode == FolderSortMode.AlbumRanked ? sorter!.Count : rawResults.Count;

    public int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        var filtered = results.Where(ProjectionFilter);
        if (sortMode == FolderSortMode.AlbumRanked)
            return sorter!.AddRange(filtered);

        int added = 0;
        foreach (var (response, file) in filtered)
        {
            string key = response.Username + '\\' + file.Filename;
            if (!seen.Add(key))
                continue;

            rawResults.Add((response, file));
            added++;
        }

        return added;
    }

    // TODO: Revisit AlbumQuery.SearchHint semantics. It may be cleaner for the hint
    // to qualify folders that contain a matching track, while still showing all files
    // from matching folders that were present in the search response.
    private bool ProjectionFilter((SearchResponse Response, SlFile File) result)
        => search.NecessaryCond.UserSatisfies(result.Response)
            && (!Utils.IsMusicFile(result.File.Filename)
                || search.NecessaryCond.FileSatisfies(result.File, sortQuery, result.Response));

    public AlbumFolderProjectionChanges AddRangeAndGetChanges(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        AddRange(results);
        return GetChanges();
    }

    public void Clear()
    {
        sorter?.Clear();
        rawResults.Clear();
        seen.Clear();
        previousSignatures.Clear();
        previousSnapshot = [];
    }

    // Ranked mode keeps a stable album-sort order before grouping. Unranked mode
    // is for aggregate projections that only need deterministic grouping order.
    public List<AlbumFolder> Snapshot()
    {
        if (sortMode == FolderSortMode.DeterministicUnranked)
            return SearchResultProjector.AlbumFoldersFromResults(
                rawResults,
                query,
                search,
                rawResults.Count,
                aggregateSortKeyContext: aggregateSortKeyContext);

        return SearchResultProjector.AlbumFoldersFromOrderedResults(
            sorter!.OrderedResults(),
            query,
            search,
            sorter.Count);
    }

    public AlbumFolderProjectionChanges GetChanges()
    {
        var folders = Snapshot();
        var currentSignatures = new Dictionary<string, AlbumFolderSignature>(StringComparer.Ordinal);
        var added = new List<AlbumFolder>();
        var updated = new List<AlbumFolder>();

        foreach (var folder in folders)
        {
            string key = FolderKey(folder);
            var signature = AlbumFolderSignature.Create(folder);
            currentSignatures.Add(key, signature);

            if (!previousSignatures.TryGetValue(key, out var previous))
                added.Add(folder);
            else if (!signature.Equals(previous))
                updated.Add(folder);
        }

        var removed = previousSnapshot
            .Where(folder => !currentSignatures.ContainsKey(FolderKey(folder)))
            .ToList();

        previousSignatures.Clear();
        foreach (var (key, signature) in currentSignatures)
            previousSignatures.Add(key, signature);
        previousSnapshot = folders;

        return new AlbumFolderProjectionChanges(folders, added, updated, removed);
    }

    private static string FolderKey(AlbumFolder folder)
        => folder.Username + '\\' + folder.FolderPath;

    private readonly record struct AlbumFolderSignature(
        int FileCount,
        int AudioFileCount,
        string? RepresentativeAudioFilename,
        string Lengths)
    {
        public static AlbumFolderSignature Create(AlbumFolder folder)
            => new(
                folder.SearchFileCount,
                folder.SearchAudioFileCount,
                folder.SearchRepresentativeAudioFilename,
                string.Join(",", folder.SearchSortedAudioLengths));
    }
}

public sealed record AlbumFolderProjectionChanges(
    IReadOnlyList<AlbumFolder> Folders,
    IReadOnlyList<AlbumFolder> Added,
    IReadOnlyList<AlbumFolder> Updated,
    IReadOnlyList<AlbumFolder> Removed)
{
    public bool HasChanges => Added.Count > 0 || Updated.Count > 0 || Removed.Count > 0;
}
