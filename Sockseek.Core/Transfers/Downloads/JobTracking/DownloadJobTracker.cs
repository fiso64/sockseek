using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;

namespace Sockseek.Core.Transfers.Downloads.JobTracking;

internal sealed class DownloadJobTracker
{
    private readonly DownloadEvents events;
    private readonly ConcurrentDictionary<Guid, Job> jobsById = new();
    private readonly ConcurrentDictionary<int, Job> jobsByDisplayId = new();
    private readonly ConcurrentDictionary<Guid, Guid> sourceJobIds = new();
    private readonly ConcurrentDictionary<Guid, Action<Job, JobStateTransition>> stateHandlersByJobId = new();

    public DownloadJobTracker(DownloadEvents events)
    {
        this.events = events;
    }

    public IEnumerable<Job> Jobs => jobsById.Values;

    public Job? GetJob(Guid id) => jobsById.TryGetValue(id, out var job) ? job : null;

    public Job? GetJob(int displayId) => jobsByDisplayId.TryGetValue(displayId, out var job) ? job : null;

    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => jobsById.Values
        .Where(job => job.WorkflowId == workflowId)
        .OrderBy(job => job.DisplayId)
        .ToList();

    public void AssociateSource(Guid jobId, Guid sourceJobId)
        => sourceJobIds[jobId] = sourceJobId;

    public void Register(Job job, Job? parent, Guid? sourceJobId = null, DirectoryTransferEntry? directoryEntry = null)
    {
        if (sourceJobId is Guid sourceId)
            AssociateSource(job.Id, sourceId);

        job.EnsureDisplayId();
        bool firstRegistration = jobsById.TryAdd(job.Id, job);
        jobsByDisplayId[job.DisplayId] = job;

        if (!firstRegistration)
        {
            if (directoryEntry is not null)
                events.UpdateDirectoryGroup(job, parent, directoryEntry);
            return;
        }

        // Direct Core execution need not carry a retained submission. Still assign
        // child roles before publishing registration, preserving extracted result roles.
        if (parent is not null && job.SemanticRole == JobSemanticRole.UserRoot)
            SubmissionIdentity.AssignExecutionChild(parent, job);

        Action<Job, JobStateTransition> stateHandler = (_, transition)
            => events.RaiseJobStateChanged(job, transition);
        stateHandlersByJobId[job.Id] = stateHandler;
        job.StateChanged += stateHandler;
        job.StructureChanged += OnStructureChanged;
        job.ExecutionChanged += OnExecutionChanged;

        events.RaiseJobRegistered(
            job,
            parent,
            sourceJobIds.TryGetValue(job.Id, out var registeredSourceId) ? registeredSourceId : null,
            directoryEntry);
    }

    public IReadOnlyList<Job> Retire(IReadOnlyCollection<Guid> jobIds)
    {
        var retired = new List<Job>(jobIds.Count);
        foreach (Guid jobId in jobIds)
        {
            sourceJobIds.TryRemove(jobId, out _);
            if (!jobsById.TryRemove(jobId, out var job))
                continue;

            jobsByDisplayId.TryRemove(
                new KeyValuePair<int, Job>(job.DisplayId, job));
            if (stateHandlersByJobId.TryRemove(jobId, out var handler))
                job.StateChanged -= handler;
            job.StructureChanged -= OnStructureChanged;
            job.ExecutionChanged -= OnExecutionChanged;
            job.Cts?.Dispose();
            job.Cts = null;
            retired.Add(job);
        }
        return retired;
    }

    internal int Count => jobsById.Count;

    private void OnStructureChanged(Job job)
        => events.RaiseJobStructureChanged(job);

    private void OnExecutionChanged(Job job)
        => events.RaiseJobStateChanged(job);
}
