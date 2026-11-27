using Sockseek.Api;
using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

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
    private readonly bool enforceMinimumTrackCount;
    private readonly ResultSorter.SortKeyContext groupSortKeyContext;

    public AlbumFolderProjectionPlan(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? peerFailureCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked,
        bool enforceMinimumTrackCount = true)
    {
        this.query = query;
        this.search = search;
        this.sortMode = sortMode;
        this.enforceMinimumTrackCount = enforceMinimumTrackCount;
        combineDiscs = SearchResultProjector.CanCombineDiscDirectories(query);
        projectionFilter = ConditionSatisfactionPolicy.CreateAlbumSearchFilter(query, search);
        groupSortKeyContext = new ResultSorter.SortKeyContext(
            projectionFilter.SortQuery,
            search,
            peerFailureCounts ?? new ConcurrentDictionary<string, int>(),
            albumMode: true,
            ignoreStringSortConditions: ignoreStringSortConditions,
            necessaryConditionEvaluator: projectionFilter.Rejection);
    }

    public EvaluatedAlbumProjectionInput? Evaluate(
        SearchProjectionInput input,
        int originalIndex)
    {
        ResultSorter.SortEntry? entry = ResultSorter.CreateSortEntry(
            input,
            groupSortKeyContext,
            originalIndex);
        return entry is { } admitted
            && admitted.Key.ConditionFacts.NecessaryConditionsSatisfied
                ? new EvaluatedAlbumProjectionInput(
                    new ProjectedFileCandidate(input, admitted.Key.ConditionFacts, admitted.Key.Rank), admitted,
                    SearchResultProjector.AlbumDirectoryPath(input.Filename, combineDiscs))
                : null;
    }

    public List<EvaluatedAlbumProjectionInput> EvaluateToList(
        IEnumerable<SearchProjectionInput> results,
        RejectionTally? rejections = null)
    {
        var evaluated = new List<EvaluatedAlbumProjectionInput>();
        int index = 0;
        foreach (SearchProjectionInput input in results)
        {
            if (Evaluate(input, index++) is { } admitted)
                evaluated.Add(admitted);
            else
                rejections?.Add(Rejection(input));
        }
        return evaluated;
    }

    // Names the reason Evaluate dropped a file, from the same two checks.
    private ResultRejection Rejection(SearchProjectionInput input)
    {
        int failures = groupSortKeyContext.PeerFailureCounts.GetValueOrDefault(input.Username, 0);
        if (groupSortKeyContext.AcceptsPeerAndVisibility(input, failures))
            return projectionFilter.Rejection(input);
        return failures >= search.EffectiveIgnoreOn ? ResultRejection.IgnoredPeer : ResultRejection.Locked;
    }

    public List<AlbumFolder> ProjectEvaluatedResults(
        IEnumerable<EvaluatedAlbumProjectionInput> evaluatedResults,
        RejectionTally? rejections = null)
        => SearchResultProjector.AlbumFoldersFromResults(
            evaluatedResults, query, search,
            useAlbumFolderQualityRanking: sortMode == FolderSortMode.AlbumRanked,
            enforceMinimumTrackCount: enforceMinimumTrackCount,
            rejections: rejections);
}

// Peers often share a few tracks of an album under the album's name. The track
// count most matching folders share is the reference: a folder below it ranks
// after the identity checks and before quality. Only folders within the
// requested track-count bounds vote, so the reference never leaves them.
internal sealed class AlbumTrackCountConsensus
{
    private readonly FolderConditions bounds;
    private readonly bool enabled;
    private readonly Dictionary<int, int> votes = [];

    public AlbumTrackCountConsensus(AlbumQuery query, SearchSettings search, FolderSortMode sortMode)
    {
        bounds = search.NecessaryFolderCond;
        // A search that returns only some of a folder's tracks says nothing
        // about how many it holds.
        enabled = sortMode == FolderSortMode.AlbumRanked
            && ConditionSatisfactionPolicy.SearchResultsLikelyContainCompleteAlbumFolders(query, search);
    }

    public void Add(AlbumFolder folder)
    {
        if (Votes(folder))
            votes[folder.SearchAudioFileCount] = votes.GetValueOrDefault(folder.SearchAudioFileCount) + 1;
    }

    public void Remove(AlbumFolder folder)
    {
        if (Votes(folder) && --votes[folder.SearchAudioFileCount] == 0)
            votes.Remove(folder.SearchAudioFileCount);
    }

    public void Clear() => votes.Clear();

    // A tie goes to the smaller count, so it cannot mark a complete folder.
    public int Reference
    {
        get
        {
            int reference = 0, most = 0;
            foreach (var (count, folders) in votes)
                if (folders > most || folders == most && count < reference)
                    (reference, most) = (count, folders);
            return reference;
        }
    }

    public static bool IsBelow(AlbumFolder folder, int reference)
        => folder.SearchAudioFileCount < reference;

    public static AlbumFolder Rank(AlbumFolder folder, int reference)
        => IsBelow(folder, reference) ? folder.AsIncomplete() : folder;

    // One-shot projection: the same reference and order as the incremental projector.
    public static List<AlbumFolder> Rank(List<AlbumFolder> folders, AlbumQuery query, SearchSettings search)
    {
        var consensus = new AlbumTrackCountConsensus(query, search, FolderSortMode.AlbumRanked);
        foreach (AlbumFolder folder in folders)
            consensus.Add(folder);
        int reference = consensus.Reference;
        if (!folders.Any(folder => IsBelow(folder, reference)))
            return folders;

        bool compareCoverage = AlbumQualityPolicy.ActiveConditions(search.NecessaryCond).IsActive;
        return folders
            .Select(folder => Rank(folder, reference))
            .Order(Comparer<AlbumFolder>.Create((x, y) =>
            {
                int rank = AlbumFolderGroupComparer.CompareRank(x.SearchGroupSortEntry, y.SearchGroupSortEntry,
                    x.SearchAudioQualityCoverage, y.SearchAudioQualityCoverage, compareCoverage);
                if (rank == 0) rank = string.Compare(x.Username, y.Username, StringComparison.Ordinal);
                return rank != 0 ? rank : string.Compare(x.FolderPath, y.FolderPath, StringComparison.Ordinal);
            }))
            .ToList();
    }

    private bool Votes(AlbumFolder folder)
    {
        int count = folder.SearchAudioFileCount;
        if (!enabled || count == 0
            || bounds.MinTrackCount is int min and > 0 && count < min
            || bounds.MaxTrackCount is int max and > 0 && count > max)
            return false;
        // The folder must be the requested album by name, when names are compared at all.
        var facts = folder.SearchGroupSortEntry?.Key.ConditionFacts;
        return facts?.ConfiguredPreferredConditions?.Contains(SearchPreferenceCondition.Album) != true
            || facts.Value.SatisfiedPreferredConditions?.Contains(SearchPreferenceCondition.Album) == true;
    }
}
