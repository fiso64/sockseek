using Sockseek.Api;

namespace Sockseek.Core.Services;

/// <summary>
/// Counts the results an automatic download could not use, by the first
/// condition each failed. Search callbacks add to it concurrently.
/// </summary>
public sealed class RejectionTally
{
    private readonly int[] counts = new int[Enum.GetValues<ResultRejection>().Length];

    /// <summary>Counts a rejection and reports whether the result passed.</summary>
    public bool Admit(ResultRejection rejection)
    {
        if (rejection == ResultRejection.None) return true;
        Interlocked.Increment(ref counts[(int)rejection]);
        return false;
    }

    public void Add(ResultRejection rejection, int count = 1)
    {
        if (rejection != ResultRejection.None && count > 0)
            Interlocked.Add(ref counts[(int)rejection], count);
    }

    public int Total
    {
        get
        {
            int total = 0;
            for (int i = 0; i < counts.Length; i++) total += Volatile.Read(ref counts[i]);
            return total;
        }
    }

    /// <summary>Non-empty counts, largest first.</summary>
    public IReadOnlyList<ResultRejectionCount> Snapshot()
    {
        var result = new List<ResultRejectionCount>();
        for (int i = 0; i < counts.Length; i++)
            if (Volatile.Read(ref counts[i]) is > 0 and var count)
                result.Add(new((ResultRejection)i, count));
        result.Sort((left, right) => right.Count != left.Count
            ? right.Count.CompareTo(left.Count) : left.Reason.CompareTo(right.Reason));
        return result;
    }
}
