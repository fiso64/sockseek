using System.Collections.Concurrent;
using Sldl.Core.Models;
using Sldl.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sldl.Core.Services;

public sealed class IncrementalResultSorter
{
    private readonly ResultSorter.SortKeyContext keyContext;
    private readonly SongQuery query;
    private readonly SearchSettings search;
    private readonly bool requireFileSatisfies;
    private readonly List<ResultSorter.SortEntry> entries = [];
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private int nextOriginalIndex;

    public IncrementalResultSorter(
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool albumMode = false,
        bool useInfer = false,
        bool useLevenshtein = false,
        bool requireFileSatisfies = false)
    {
        this.query = query;
        this.search = search;
        this.requireFileSatisfies = requireFileSatisfies;
        keyContext = ResultSorter.CreateSortKeyContext(
            [],
            query,
            search,
            userSuccessCounts,
            useBracketCheck: !albumMode,
            useInfer,
            useLevenshtein,
            albumMode);
    }

    public int Count => entries.Count;

    public void Clear()
    {
        entries.Clear();
        seen.Clear();
        nextOriginalIndex = 0;
    }

    public int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        var newEntries = new List<ResultSorter.SortEntry>();
        foreach (var (response, file) in results)
        {
            string key = response.Username + '\\' + file.Filename;
            if (!seen.Add(key))
                continue;

            if (requireFileSatisfies && !search.NecessaryCond.FileSatisfies(file, query, response))
                continue;

            var entry = ResultSorter.CreateSortEntry(response, file, keyContext, nextOriginalIndex++);
            if (!entry.HasValue)
                continue;

            newEntries.Add(entry.Value);
        }

        if (newEntries.Count == 0)
            return 0;

        newEntries.Sort(ResultSorter.SortEntryComparer.Instance);
        MergeSortedEntries(newEntries);
        return newEntries.Count;
    }

    private void MergeSortedEntries(List<ResultSorter.SortEntry> newEntries)
    {
        if (entries.Count == 0)
        {
            entries.AddRange(newEntries);
            return;
        }

        var merged = new List<ResultSorter.SortEntry>(entries.Count + newEntries.Count);
        int existingIndex = 0;
        int newIndex = 0;
        var comparer = ResultSorter.SortEntryComparer.Instance;

        while (existingIndex < entries.Count && newIndex < newEntries.Count)
        {
            if (comparer.Compare(entries[existingIndex], newEntries[newIndex]) <= 0)
                merged.Add(entries[existingIndex++]);
            else
                merged.Add(newEntries[newIndex++]);
        }

        while (existingIndex < entries.Count)
            merged.Add(entries[existingIndex++]);

        while (newIndex < newEntries.Count)
            merged.Add(newEntries[newIndex++]);

        entries.Clear();
        entries.AddRange(merged);
    }

    internal IEnumerable<(SearchResponse Response, SlFile File)> OrderedResults()
    {
        for (int i = 0; i < entries.Count; i++)
            yield return (entries[i].Response, entries[i].File);
    }

    public List<(SearchResponse Response, SlFile File)> Snapshot()
    {
        var snapshot = new List<(SearchResponse Response, SlFile File)>(entries.Count);
        snapshot.AddRange(OrderedResults());
        return snapshot;
    }
}
