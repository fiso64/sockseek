using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;

namespace Sockseek.Server;

// Print output belongs to its client, not durable history. Keep completed
// execution available while the client reads it; abandoned output expires.
internal sealed class TransientJobResults(
    Action<Guid, IReadOnlyCollection<Guid>> release,
    TimeProvider? timeProvider = null) : IDisposable
{
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> submissions = [];
    private readonly Dictionary<Guid, Entry> jobs = [];
    private ITimer? timer;

    private sealed class Entry(Guid submissionId, Guid workflowId, Job[] values, DateTimeOffset accessed)
    {
        internal readonly Guid SubmissionId = submissionId;
        internal readonly Guid WorkflowId = workflowId;
        internal readonly Dictionary<Guid, Job> Jobs = values.ToDictionary(job => job.Id);
        internal DateTimeOffset Accessed = accessed;
    }

    internal void Capture(IEnumerable<Job> completed)
    {
        lock (gate)
        {
            foreach (var group in completed.Where(job => job.Retention == JobRetention.Memory && job.SubmissionId != null)
                .GroupBy(job => job.SubmissionId!.Value))
            {
                var entry = new Entry(group.Key, group.First().WorkflowId, group.ToArray(), clock.GetUtcNow());
                submissions.Add(group.Key, entry);
                foreach (Guid id in entry.Jobs.Keys) jobs.Add(id, entry);
            }
            if (submissions.Count > 0)
                timer ??= clock.CreateTimer(_ => Prune(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    internal Job? Find(Guid id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var entry)) return null;
            entry.Accessed = clock.GetUtcNow();
            return entry.Jobs[id];
        }
    }

    internal bool Release(Guid submissionId, out int count)
    {
        Entry? entry;
        lock (gate)
        {
            if (!submissions.Remove(submissionId, out entry)) { count = 0; return false; }
            foreach (Guid id in entry.Jobs.Keys) jobs.Remove(id);
            count = entry.Jobs.Count;
        }
        release(entry.WorkflowId, entry.Jobs.Keys);
        // A transient child must not hide its durable parent from deletion.
        return entry.Jobs.Values.Any(job => job.SemanticRole == JobSemanticRole.UserRoot);
    }

    internal void Prune()
    {
        // Check and detach under the same gate as readers refreshing the lease.
        List<Entry> expired;
        lock (gate)
        {
            var cutoff = clock.GetUtcNow() - IdleLifetime;
            expired = submissions.Values.Where(entry => entry.Accessed <= cutoff).ToList();
            foreach (var entry in expired)
            {
                submissions.Remove(entry.SubmissionId);
                foreach (Guid id in entry.Jobs.Keys) jobs.Remove(id);
            }
        }
        foreach (var entry in expired) release(entry.WorkflowId, entry.Jobs.Keys);
    }

    public void Dispose()
    {
        timer?.Dispose();
        lock (gate) { submissions.Clear(); jobs.Clear(); }
    }
}
