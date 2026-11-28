using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads;

internal sealed class DownloadJobContextStore
{
    private readonly record struct Entry(JobContext Context, HashSet<DownloadOutputFile> Outputs);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Entry>> workflows = new();

    public JobContext Get(Job job) => workflows[job.WorkflowId][job.Id].Context;

    public bool TryGetValue(Job job, out JobContext context)
    {
        if (workflows.TryGetValue(job.WorkflowId, out var entries) && entries.TryGetValue(job.Id, out var entry))
        {
            context = entry.Context;
            return true;
        }
        context = null!;
        return false;
    }

    public void SetPrepared(Job root, Dictionary<Guid, JobContext> prepared, Job? inheritFrom = null)
    {
        var entries = workflows.GetOrAdd(root.WorkflowId, static _ => new());
        // Descendants, including an extracted result or a resumed selection, contribute
        // to the same request. Independent roots may use the same workflow.
        HashSet<DownloadOutputFile> outputs = inheritFrom is null ? [] : entries[inheritFrom.Id].Outputs;
        lock (outputs)
        {
            foreach (var (id, context) in prepared)
            {
                if (context.IndexEditor is { IsWritable: true } index) outputs.Add(index);
                if (context.PlaylistEditor is { IsWritable: true } playlist) outputs.Add(playlist);
                entries[id] = new(context, outputs);
            }
        }
    }

    public void SetInherited(Job child, JobContext context, Job parent)
    {
        var entries = workflows[parent.WorkflowId];
        entries[child.Id] = new(context, entries[parent.Id].Outputs);
    }

    public IReadOnlyCollection<DownloadOutputFile> GetOutputFiles(Job root)
    {
        if (!workflows.TryGetValue(root.WorkflowId, out var entries) || !entries.TryGetValue(root.Id, out var entry))
            return [];
        lock (entry.Outputs) return entry.Outputs.ToArray();
    }

    public IReadOnlyList<Guid> RemoveWorkflow(Guid workflowId)
        => workflows.TryRemove(workflowId, out var entries) ? entries.Keys.ToArray() : [];

    internal int Count => workflows.Values.Sum(entries => entries.Count);
}
