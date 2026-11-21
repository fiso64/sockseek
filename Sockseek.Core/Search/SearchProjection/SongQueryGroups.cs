using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

// Each group's first offer supplies its reference duration. With a 3-second
// tolerance, if 12 arrives first, both 10 and 14 join it. If 10 and 14 arrive
// first, they form separate groups and 12 joins the more recently created one.
// Arrival order can therefore affect which groups reach the minimum peer count.
internal sealed class SongQueryGroups<T>(int lengthTolerance, Func<SongQuery, int, T> create)
{
    private readonly Dictionary<SongQuery, LengthGroups> identities =
        new(new SongQueryComparer(ignoreCase: true));
    private readonly List<T> values = [];

    public IReadOnlyList<T> Values => values;

    public void Clear()
    {
        identities.Clear();
        values.Clear();
    }

    public T GetOrAdd(SongQuery query)
    {
        if (!identities.TryGetValue(query, out var groups))
        {
            var entry = Create(query);
            identities.Add(query, new LengthGroups(entry));
            return entry.Value;
        }
        if (lengthTolerance == -1) return groups.First.Value;

        // Most titles have only one duration root. Allocate a tree only when
        // another genuinely distinct duration (or unknown duration) arrives.
        if (groups.Lengths is null)
        {
            if (Matches(groups.First.Length, query.Length)) return groups.First.Value;
            var entry = Create(query);
            groups.Lengths = new(EntryComparer) { groups.First, entry };
            return entry.Value;
        }

        if (query.Length == -1 || lengthTolerance >= 0)
        {
            int lower = query.Length == -1 ? -1 : (int)Math.Max(int.MinValue, (long)query.Length - lengthTolerance);
            int upper = query.Length == -1 ? -1 : (int)Math.Min(int.MaxValue, (long)query.Length + lengthTolerance);
            Entry? match = null;
            // Distinct numeric roots are farther apart than the tolerance, so
            // at most two can match. Unknown length is always its own group.
            foreach (var entry in groups.Lengths.GetViewBetween(
                new(lower, int.MinValue, default!), new(upper, int.MaxValue, default!)))
            {
                if (Matches(entry.Length, query.Length) && (match is null || entry.Index > match.Value.Index))
                    match = entry;
            }
            if (match is { } found) return found.Value;
        }

        var added = Create(query);
        groups.Lengths.Add(added);
        return added.Value;
    }

    private bool Matches(int left, int right)
        => left == -1 || right == -1
            ? left == right
            : Math.Abs((long)left - right) <= lengthTolerance;

    private Entry Create(SongQuery query)
    {
        int index = values.Count;
        T value = create(query, index);
        values.Add(value);
        return new(query.Length, index, value);
    }

    private readonly record struct Entry(int Length, int Index, T Value);
    private sealed class LengthGroups(Entry first)
    {
        public Entry First { get; } = first;
        public SortedSet<Entry>? Lengths { get; set; }
    }

    private static readonly IComparer<Entry> EntryComparer = Comparer<Entry>.Create(static (left, right) =>
    {
        int comparison = left.Length.CompareTo(right.Length);
        return comparison != 0 ? comparison : left.Index.CompareTo(right.Index);
    });
}
