using System.Collections.Concurrent;

namespace Sockseek.Core.Services;

public interface IUserSuccessStats
{
    ConcurrentDictionary<string, int> GetCounts(Guid workflowId);
}

// TODO [V4]: Define the ownership, lifetime, persistence/decay, and ranking
// reproducibility semantics of per-user success counts in a long-running daemon.
// Preserve their existing within-workflow ranking effect for now; do not promote
// them to a shared or durable reputation resource until those semantics exist.
public sealed class UserSuccessTracker : IUserSuccessStats
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, int>> workflows = new();

    internal void StartWorkflow(Guid workflowId)
        => workflows.GetOrAdd(workflowId, static _ => new(StringComparer.Ordinal));

    public ConcurrentDictionary<string, int> GetCounts(Guid workflowId)
        => workflows.TryGetValue(workflowId, out var counts)
            ? counts : new(StringComparer.Ordinal);

    public void RecordSuccess(Guid workflowId, string username)
        => workflows.GetOrAdd(workflowId, static _ => new(StringComparer.Ordinal))
            .AddOrUpdate(username, 1, static (_, count) => count + 1);

    public void RecordFailure(Guid workflowId, string username)
        => workflows.GetOrAdd(workflowId, static _ => new(StringComparer.Ordinal))
            .AddOrUpdate(username, -1, static (_, count) => count - 1);

    internal void RetireWorkflow(Guid workflowId) => workflows.TryRemove(workflowId, out _);
    internal void Clear() => workflows.Clear();
}
