using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.JobTracking;

internal sealed class DownloadJobContextStore
{
    private readonly ConcurrentDictionary<Guid, JobContext> contexts = new();
    private readonly ConcurrentDictionary<Guid, Guid> workflowIds = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<DownloadOutputFile, byte>> outputFiles = new();

    public JobContext Get(Job job) => contexts[job.Id];

    public bool ContainsKey(Guid jobId) => contexts.ContainsKey(jobId);

    public bool TryGetValue(Guid jobId, out JobContext context)
        => contexts.TryGetValue(jobId, out context!);

    public void Set(Job job, JobContext context)
    {
        Set(job.Id, job.WorkflowId, context);
    }

    public void Set(Guid jobId, Guid workflowId, JobContext context)
    {
        contexts[jobId] = context;
        workflowIds[jobId] = workflowId;
        if (context.IndexEditor is { IsWritable: true } index)
            outputFiles.GetOrAdd(workflowId, static _ => new()).TryAdd(index, 0);
        if (context.PlaylistEditor is { IsWritable: true } playlist)
            outputFiles.GetOrAdd(workflowId, static _ => new()).TryAdd(playlist, 0);
    }

    public IReadOnlyCollection<DownloadOutputFile> GetOutputFiles(Guid workflowId)
        => outputFiles.TryGetValue(workflowId, out var files) ? files.Keys.ToArray() : [];

    public IReadOnlyList<Guid> RemoveWorkflow(Guid workflowId)
    {
        outputFiles.TryRemove(workflowId, out _);
        var removed = new List<Guid>();
        foreach (var pair in workflowIds.Where(pair => pair.Value == workflowId))
        {
            if (!workflowIds.TryRemove(pair.Key, out _))
                continue;
            contexts.TryRemove(pair.Key, out _);
            removed.Add(pair.Key);
        }
        return removed;
    }

    internal int Count => contexts.Count;
}
