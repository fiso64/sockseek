using System.Collections.Concurrent;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public interface IPeerFailureStats
{
    ConcurrentDictionary<string, int> GetCounts(Guid workflowId);
}

// Failure feedback is local to one workflow, discarded at retirement, and never
// persisted as peer reputation. Successful transfers and local reuse do not reset it.
public sealed class PeerFailureTracker : IPeerFailureStats
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, int>> workflows = new();

    internal void StartWorkflow(Guid workflowId)
        => workflows.GetOrAdd(workflowId, static _ => new(StringComparer.Ordinal));

    public ConcurrentDictionary<string, int> GetCounts(Guid workflowId)
        => workflows.TryGetValue(workflowId, out var counts)
            ? counts : new(StringComparer.Ordinal);

    public void RecordFailure(Guid workflowId, string username)
        => workflows.GetOrAdd(workflowId, static _ => new(StringComparer.Ordinal))
            .AddOrUpdate(username, 1, static (_, count) => count == int.MaxValue ? count : count + 1);

    // Candidates already carry the expensive quality ordering. Failures only
    // increase, so a lazy stable partition can defer unhealthy peers and recheck
    // exclusion before each attempt without sorting or reevaluating conditions.
    // Use only when choosing a source, never to veto an explicitly chosen target
    // or the remaining files of an already selected album folder.
    internal IEnumerable<T> ForAutomaticSelection<T>(Guid workflowId,
        IEnumerable<T> candidates, Func<T, string> username, SearchSettings settings)
    {
        var counts = GetCounts(workflowId);
        List<T>? deferred = null;
        foreach (var candidate in candidates)
        {
            int failures = counts.GetValueOrDefault(username(candidate));
            if (failures >= settings.EffectiveIgnoreOn) continue;
            if (failures >= settings.DownrankOn)
                (deferred ??= []).Add(candidate);
            else
                yield return candidate;
        }
        if (deferred == null) yield break;
        foreach (var candidate in deferred)
            if (counts.GetValueOrDefault(username(candidate)) < settings.EffectiveIgnoreOn)
                yield return candidate;
    }

    internal void RetireWorkflow(Guid workflowId) => workflows.TryRemove(workflowId, out _);
    internal void Clear() => workflows.Clear();
}
