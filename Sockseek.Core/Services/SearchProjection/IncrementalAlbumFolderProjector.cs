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
    // TODO [PERFORMANCE]: This stores raw rows incrementally, but the first
    // snapshot after new results still rebuilds folder grouping/ranking from
    // all raw rows. Large interactive album searches can visibly linger in
    // ProcessingSearchResults. Make grouped folder state truly incremental and
    // feed it while raw results arrive so the terminal snapshot mostly orders
    // and materializes already-built folders.
    private readonly AlbumFolderProjectionPlan projectionPlan;
    private readonly List<(SearchResponse Response, SlFile File)> rawResults = [];
    private readonly HashSet<RawResultKey> seen = [];
    private readonly Dictionary<string, AlbumFolderSignature> previousSignatures = new(StringComparer.Ordinal);
    private List<AlbumFolder> previousSnapshot = [];

    public IncrementalAlbumFolderProjector(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        projectionPlan = new AlbumFolderProjectionPlan(
            query,
            search,
            userSuccessCounts,
            ignoreStringSortConditions,
            sortMode);
    }

    public int Count => rawResults.Count;

    public int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        var filtered = results.Where(ProjectionFilter);
        int added = 0;
        foreach (var (response, file) in filtered)
        {
            var key = new RawResultKey(response.Username, file.Filename);
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
        => projectionPlan.Includes(result);

    public AlbumFolderProjectionChanges AddRangeAndGetChanges(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        AddRange(results);
        return GetChanges();
    }

    public void Clear()
    {
        rawResults.Clear();
        seen.Clear();
        previousSignatures.Clear();
        previousSnapshot = [];
    }

    public List<AlbumFolder> Snapshot()
        => projectionPlan.ProjectFilteredResults(rawResults, rawResults.Count);

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

    private readonly record struct RawResultKey(string Username, string Filename);

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
