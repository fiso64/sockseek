using Sockseek.Api;
using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;


namespace Sockseek.Core.Services;

public readonly record struct SearchConditionFacts(
    bool NecessaryConditionsSatisfied,
    bool PreferredConditionsSatisfied,
    IReadOnlyList<SearchPreferenceCondition>? SatisfiedPreferredConditions = null,
    IReadOnlyList<SearchPreferenceCondition>? ConfiguredPreferredConditions = null)
{
    public SearchPreferenceTier PreferenceTier => PreferredConditionsSatisfied
        ? SearchPreferenceTier.Preferred
        : SearchPreferenceTier.Other;

    internal SearchConditionFacts WithLockedFolder()
        => SatisfiedPreferredConditions?.Contains(SearchPreferenceCondition.ExcludeLocked) == true
            ? this with { PreferredConditionsSatisfied = false,
                SatisfiedPreferredConditions = SatisfiedPreferredConditions.Where(c => c != SearchPreferenceCondition.ExcludeLocked).ToArray() }
            : this;

    public IReadOnlyList<SearchPreferenceCondition> UnsatisfiedPreferredConditions
        => (ConfiguredPreferredConditions ?? [])
            .Except(SatisfiedPreferredConditions ?? [])
            .ToArray();
}

// Read projections retain facts and ranking only. Execution/CLI materialization
// creates a FileCandidate from Input with these already-evaluated conditions.
public sealed record ProjectedFileCandidate(
    SearchProjectionInput Input,
    SearchConditionFacts ConditionFacts,
    SearchProjectionSortKey SortKey)
{
    internal ProjectedFileCandidate ForLockedFolder()
    {
        var facts = ConditionFacts.WithLockedFolder();
        return facts == ConditionFacts ? this : this with { ConditionFacts = facts,
            SortKey = SortKey.WithoutLockedPreference() };
    }
}

public readonly record struct SearchProjectionSortKey(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker) : IComparable<SearchProjectionSortKey>
{
    // Availability follows the sixteen identity/quality flags. Construction and
    // folder adjustment share these positions; neither reevaluates conditions.
    private const uint UnlockedPreference = 1u << 15;
    private const uint AllPreferences = 1u << 14;
    private const uint FreeSlot = 1u << 13;

    internal static uint AvailabilityFlags(bool unlocked, bool allPreferences, bool freeSlot)
        => (unlocked ? UnlockedPreference : 0) | (allPreferences ? AllPreferences : 0) | (freeSlot ? FreeSlot : 0);

    internal SearchProjectionSortKey WithoutLockedPreference()
        => this with { HighFlags = HighFlags & ~(UnlockedPreference | AllPreferences) };

    // Larger keys rank better. Result lists reverse this comparison.
    public int CompareTo(SearchProjectionSortKey other)
    {
        int comparison = HighFlags.CompareTo(other.HighFlags);
        if (comparison != 0) return comparison;
        comparison = UploadSpeedFast.CompareTo(other.UploadSpeedFast);
        if (comparison != 0) return comparison;
        comparison = MidFlags.CompareTo(other.MidFlags);
        if (comparison != 0) return comparison;
        comparison = UploadSpeedMedium.CompareTo(other.UploadSpeedMedium);
        if (comparison != 0) return comparison;
        comparison = BitRate.CompareTo(other.BitRate);
        return comparison != 0 ? comparison : StableTieBreaker.CompareTo(other.StableTieBreaker);
    }
}

public sealed class SearchProjectionSortKeyComparer : IComparer<SearchProjectionSortKey>
{
    public static SearchProjectionSortKeyComparer Instance { get; } = new();

    private SearchProjectionSortKeyComparer() { }

    public int Compare(SearchProjectionSortKey x, SearchProjectionSortKey y) => y.CompareTo(x);
}

public sealed class IncrementalResultSorter
{
    private readonly ResultSorter.SortKeyContext keyContext;
    private readonly bool requireFileSatisfies;
    private readonly bool retainProjectedRows;
    private List<ResultSorter.SortEntry> entries = [];
    private List<ResultSorter.SortEntry> pendingEntries = [];
    private ResultSorter.SortEntry? bestEntry;
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)>? seen;
    private int nextOriginalIndex;

    public IncrementalResultSorter(
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> peerFailureCounts,
        bool albumMode = false,
        bool requireFileSatisfies = false,
        bool ignoreStringSortConditions = false,
        bool retainProjectedRows = true,
        bool deduplicateInputs = true,
        Func<SearchProjectionInput, bool>? necessaryConditionEvaluator = null)
    {
        this.requireFileSatisfies = requireFileSatisfies;
        this.retainProjectedRows = retainProjectedRows;
        seen = deduplicateInputs ? [] : null;
        keyContext = new ResultSorter.SortKeyContext(
            query,
            search,
            peerFailureCounts,
            albumMode,
            ignoreStringSortConditions,
            necessaryConditionEvaluator);
    }

    public int Count => entries.Count + pendingEntries.Count;

    public void Clear()
    {
        entries.Clear();
        pendingEntries.Clear();
        bestEntry = null;
        seen?.Clear();
        nextOriginalIndex = 0;
    }

    public int AddRange(IEnumerable<SearchProjectionInput> results) => RankBatch(results).Count;

    public ProjectedFileCandidate? Add(SearchProjectionInput input)
        => AddWithPeerFailureCount(input, peerFailureCount: null);

    // Deferred group ranking retains the workflow score at admission. It
    // must not re-admit ignored peers or change earlier ranking after failures.
    internal ProjectedFileCandidate? AddWithPeerFailureCount(SearchProjectionInput input, int? peerFailureCount)
    {
        var entry = CreateEntry(input, peerFailureCount);
        if (!entry.HasValue) return null;
        if (retainProjectedRows)
        {
            UpdateBest(entry.Value);
            pendingEntries.Add(entry.Value);
        }
        return Projected(entry.Value);
    }

    public IReadOnlyList<ProjectedFileCandidate> AddRangeAndGetProjected(
        IEnumerable<SearchProjectionInput> results)
        => RankBatch(results).Select(Projected).ToArray();

    internal bool WouldProject(SearchProjectionInput input)
        => keyContext.AcceptsPeerAndVisibility(input, keyContext.PeerFailureCounts.GetValueOrDefault(input.Username, 0))
            && (!requireFileSatisfies || keyContext.NecessaryConditionsSatisfied(input));

    private List<ResultSorter.SortEntry> RankBatch(IEnumerable<SearchProjectionInput> results)
    {
        var newEntries = new List<ResultSorter.SortEntry>();
        foreach (var input in results)
        {
            if (CreateEntry(input) is { } entry) newEntries.Add(entry);
        }
        if (retainProjectedRows && newEntries.Count != 0)
            RetainEntries(newEntries);
        return newEntries;
    }

    private ResultSorter.SortEntry? CreateEntry(SearchProjectionInput input, int? peerFailureCount = null)
    {
        var key = (new PeerPathKey(input.Username, input.Filename), input.Visibility);
        if (seen != null && !seen.Add(key)) return null;
        var entry = ResultSorter.CreateSortEntry(input, keyContext, nextOriginalIndex++, peerFailureCount);
        return requireFileSatisfies && entry.HasValue && !entry.Value.Key.ConditionFacts.NecessaryConditionsSatisfied
            ? null : entry;
    }

    private void UpdateBest(ResultSorter.SortEntry entry)
    {
        if (bestEntry is not { } best || ResultSorter.SortEntryComparer.Instance.Compare(entry, best) < 0)
            bestEntry = entry;
    }

    private void RetainEntries(List<ResultSorter.SortEntry> added)
    {
        foreach (var entry in added)
            UpdateBest(entry);
        // Arrival packet size must not decide ranking cost. In particular,
        // group buckets often receive one option at a time: merging their
        // entire ordered prefix on each arrival was quadratic in copies/bytes.
        if (pendingEntries.Count == 0)
            pendingEntries = added;
        else
            pendingEntries.AddRange(added);
    }

    private void EnsureSorted()
    {
        if (pendingEntries.Count == 0) return;
        var comparer = ResultSorter.SortEntryComparer.Instance;
        pendingEntries.Sort(comparer);
        if (entries.Count == 0)
        {
            entries = pendingEntries;
        }
        else
        {
            // A reader asking for full ordering pays for one merge of the
            // pending rows. Keep them separate until then: appending to the
            // full ordered buffer would grow/copy it before this merge too.
            var merged = new List<ResultSorter.SortEntry>(Count);
            int previous = 0;
            int added = 0;
            while (previous < entries.Count && added < pendingEntries.Count)
                merged.Add(comparer.Compare(entries[previous], pendingEntries[added]) <= 0
                    ? entries[previous++] : pendingEntries[added++]);
            while (previous < entries.Count) merged.Add(entries[previous++]);
            while (added < pendingEntries.Count) merged.Add(pendingEntries[added++]);
            entries = merged;
        }
        pendingEntries = [];
    }

    internal ProjectedFileCandidate BestProjectedFile()
        => Projected(bestEntry ?? throw new InvalidOperationException("The sorter has no retained files."));

    public List<SearchProjectionInput> SnapshotInputs()
    {
        if (!retainProjectedRows)
            throw new InvalidOperationException("This incremental sorter was configured for disk-backed rows.");
        EnsureSorted();
        return entries.Select(entry => entry.Input).ToList();
    }

    public List<ProjectedFileCandidate> SnapshotProjectedFiles()
    {
        if (!retainProjectedRows)
            throw new InvalidOperationException("This incremental sorter was configured for disk-backed rows.");
        EnsureSorted();
        return entries.Select(Projected).ToList();
    }

    private static ProjectedFileCandidate Projected(ResultSorter.SortEntry entry)
        => new(
            entry.Input,
            entry.Key.ConditionFacts,
            entry.Key.Rank);
}
