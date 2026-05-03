using System.Collections.Concurrent;
using Sldl.Core.Models;
using Sldl.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sldl.Core.Services;

public sealed class IncrementalAlbumFolderProjector
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly SongQuery sortQuery;
    private readonly IncrementalResultSorter sorter;
    private readonly Dictionary<string, AlbumFolderSignature> previousSignatures = new(StringComparer.Ordinal);
    private List<AlbumFolder> previousSnapshot = [];

    public IncrementalAlbumFolderProjector(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null)
    {
        this.query = query;
        this.search = search;
        sortQuery = SearchResultProjector.AlbumFileMatchQuery(query);
        sorter = new IncrementalResultSorter(
            sortQuery,
            search,
            userSuccessCounts ?? new ConcurrentDictionary<string, int>(),
            albumMode: true);
    }

    public int Count => sorter.Count;

    public int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
        => sorter.AddRange(results.Where(ProjectionFilter));

    // TODO: Revisit AlbumQuery.SearchHint semantics. It may be cleaner for the hint
    // to qualify folders that contain a matching track, while still showing all files
    // from matching folders that were present in the search response.
    private bool ProjectionFilter((SearchResponse Response, SlFile File) result)
        => search.NecessaryCond.BannedUsersSatisfies(result.Response)
            && (!Utils.IsMusicFile(result.File.Filename)
                || search.NecessaryCond.FileSatisfies(result.File, sortQuery, result.Response));

    public AlbumFolderProjectionChanges AddRangeAndGetChanges(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        AddRange(results);
        return GetChanges();
    }

    public void Clear()
    {
        sorter.Clear();
        previousSignatures.Clear();
        previousSnapshot = [];
    }

    // Incremental here means new raw search results are merged into a stable
    // album-mode sorted list. Snapshot-time folder grouping is still rebuilt
    // from that sorted list so child-directory merge semantics stay identical
    // to SearchResultProjector.AlbumFolders.
    public List<AlbumFolder> Snapshot()
        => SearchResultProjector.AlbumFoldersFromOrderedResults(
            sorter.OrderedResults(),
            query,
            search,
            sorter.Count);

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
