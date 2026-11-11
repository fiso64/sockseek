using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

using System.Text.Json.Serialization;

namespace Sockseek.Core.Services;

[JsonConverter(typeof(JsonStringEnumConverter<SearchPreferenceTier>))]
public enum SearchPreferenceTier
{
    Preferred,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchPreferenceCondition>))]
public enum SearchPreferenceCondition
{
    // Stable identities: retained projections encode sets using these values.
    Format = 0,
    Length = 1,
    Bitrate = 2,
    SampleRate = 3,
    BitDepth = 4,
    Title = 5,
    Artist = 6,
    Album = 7,
    Username = 8,
    ExcludeLocked = 9,
}

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
            SortKey = SortKey with { HighFlags = ResultSorter.WithoutLockedPreference(SortKey.HighFlags) } };
    }
}

public readonly record struct SearchProjectionSortKey(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker);

public sealed class SearchProjectionSortKeyComparer : IComparer<SearchProjectionSortKey>
{
    public static SearchProjectionSortKeyComparer Instance { get; } = new();

    private SearchProjectionSortKeyComparer() { }

    public int Compare(SearchProjectionSortKey x, SearchProjectionSortKey y)
    {
        int comparison = y.HighFlags.CompareTo(x.HighFlags);
        if (comparison != 0) return comparison;
        comparison = y.UploadSpeedFast.CompareTo(x.UploadSpeedFast);
        if (comparison != 0) return comparison;
        comparison = y.MidFlags.CompareTo(x.MidFlags);
        if (comparison != 0) return comparison;
        comparison = y.InferredTrackCount.CompareTo(x.InferredTrackCount);
        if (comparison != 0) return comparison;
        comparison = y.UploadSpeedMedium.CompareTo(x.UploadSpeedMedium);
        if (comparison != 0) return comparison;
        comparison = y.BitRate.CompareTo(x.BitRate);
        return comparison != 0
            ? comparison
            : y.StableTieBreaker.CompareTo(x.StableTieBreaker);
    }
}

public sealed class IncrementalResultSorter
{
    private readonly ResultSorter.SortKeyContext keyContext;
    private readonly SongQuery query;
    private readonly SearchSettings search;
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
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool albumMode = false,
        bool useInfer = false,
        bool requireFileSatisfies = false,
        bool ignoreStringSortConditions = false,
        bool retainProjectedRows = true,
        bool deduplicateInputs = true,
        Func<SearchProjectionInput, bool>? necessaryConditionEvaluator = null)
    {
        this.query = query;
        this.search = search;
        this.requireFileSatisfies = requireFileSatisfies;
        this.retainProjectedRows = retainProjectedRows;
        seen = deduplicateInputs ? [] : null;
        keyContext = ResultSorter.CreateSortKeyContext(
            [],
            query,
            search,
            userSuccessCounts,
            useBracketCheck: !albumMode,
            useInfer,
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

    public int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        var newEntries = new List<ResultSorter.SortEntry>();
        foreach (var (response, file) in results)
        {
            var key = (new PeerPathKey(response.Username, file.Filename), SearchResultVisibility.Public);
            if (seen != null && !seen.Add(key))
                continue;

            var entry = ResultSorter.CreateSortEntry(response, file, keyContext, nextOriginalIndex++);
            if (!entry.HasValue)
                continue;
            if (requireFileSatisfies && !entry.Value.Key.ConditionFacts.NecessaryConditionsSatisfied)
                continue;

            newEntries.Add(entry.Value);
        }

        if (newEntries.Count == 0)
            return 0;

        if (retainProjectedRows)
        {
            RetainEntries(newEntries);
        }
        return newEntries.Count;
    }

    public int AddRange(IEnumerable<SearchProjectionInput> results)
        => AddRangeAndGetProjected(results).Count;

    public ProjectedFileCandidate? Add(SearchProjectionInput input)
        => AddWithUserSuccessCount(input, userSuccessCount: null);

    // Deferred aggregate ranking retains the workflow score at admission. It
    // must not re-admit ignored peers or change earlier ranking after failures.
    internal ProjectedFileCandidate? AddWithUserSuccessCount(SearchProjectionInput input, int? userSuccessCount)
    {
        var entry = CreateEntry(input, userSuccessCount);
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
    {
        var newEntries = new List<ResultSorter.SortEntry>();
        foreach (var input in results)
        {
            if (CreateEntry(input) is { } entry) newEntries.Add(entry);
        }
        if (newEntries.Count == 0)
            return [];
        var added = newEntries.Select(Projected).ToArray();
        if (!retainProjectedRows)
            return added;
        RetainEntries(newEntries);
        return added;
    }

    private ResultSorter.SortEntry? CreateEntry(SearchProjectionInput input, int? userSuccessCount = null)
    {
        var key = (new PeerPathKey(input.Username, input.Filename), input.Visibility);
        if (seen != null && !seen.Add(key)) return null;
        var entry = ResultSorter.CreateSortEntry(input, keyContext, nextOriginalIndex++, userSuccessCount);
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
        // aggregate buckets often receive one option at a time: merging their
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
            entries.AddRange(pendingEntries);
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

    internal IEnumerable<(SearchResponse Response, SlFile File)> OrderedResults()
    {
        EnsureSorted();
        for (int i = 0; i < entries.Count; i++)
            yield return (entries[i].Response!, entries[i].File!);
    }

    public List<(SearchResponse Response, SlFile File)> Snapshot()
    {
        var snapshot = new List<(SearchResponse Response, SlFile File)>(Count);
        snapshot.AddRange(OrderedResults());
        return snapshot;
    }

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
            entry.Key.PersistenceKey);
}
