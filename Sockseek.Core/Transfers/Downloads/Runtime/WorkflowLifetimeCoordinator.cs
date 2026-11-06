using Sockseek.Core.Jobs;

namespace Sockseek.Core.Transfers.Downloads.Runtime;

/// <summary>
/// Serializes root admission and terminal retirement for each logical workflow.
/// A successor generation with the same workflow ID waits until the prior
/// generation's final retirement event and cleanup have completed.
/// </summary>
internal sealed class WorkflowLifetimeCoordinator
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, WorkflowGeneration> currentByWorkflow = [];
    private readonly Func<Guid, IReadOnlyList<Job>> getJobs;
    private readonly Func<IReadOnlyCollection<Job>, bool> hasResumableState;
    private readonly Func<Guid, long> captureSettingsVersion;
    private readonly Action<Guid, IReadOnlyList<Job>, long> retire;

    public WorkflowLifetimeCoordinator(
        Func<Guid, IReadOnlyList<Job>> getJobs,
        Func<IReadOnlyCollection<Job>, bool> hasResumableState,
        Func<Guid, long> captureSettingsVersion,
        Action<Guid, IReadOnlyList<Job>, long> retire)
    {
        this.getJobs = getJobs;
        this.hasResumableState = hasResumableState;
        this.captureSettingsVersion = captureSettingsVersion;
        this.retire = retire;
    }

    public WorkflowRootLease QueueRoot(Job root)
    {
        lock (gate)
        {
            if (!currentByWorkflow.TryGetValue(root.WorkflowId, out var generation)
                || generation.Retiring)
            {
                generation = new WorkflowGeneration(
                    root.WorkflowId,
                    generation?.RetirementCompleted.Task ?? Task.CompletedTask);
                currentByWorkflow[root.WorkflowId] = generation;
            }

            generation.PendingRoots++;
            generation.SettingsVersion = captureSettingsVersion(root.WorkflowId);
            return new WorkflowRootLease(root, generation);
        }
    }

    public Task WaitUntilReadyAsync(WorkflowRootLease lease, CancellationToken cancellationToken)
        => lease.Generation.Ready.WaitAsync(cancellationToken);

    public void RootCompleted(WorkflowRootLease lease)
    {
        WorkflowGeneration? retirement;
        lock (gate)
        {
            if (lease.Generation.PendingRoots <= 0)
                throw new InvalidOperationException("Workflow root completion was reported more than once.");
            lease.Generation.PendingRoots--;
            retirement = TryBeginRetirement(lease.Generation);
        }
        if (retirement != null)
            CompleteRetirement(retirement);
    }

    public void Reevaluate(Guid workflowId)
    {
        WorkflowGeneration? retirement;
        lock (gate)
        {
            retirement = currentByWorkflow.TryGetValue(workflowId, out var generation)
                ? TryBeginRetirement(generation)
                : null;
        }
        if (retirement != null)
            CompleteRetirement(retirement);
    }

    private WorkflowGeneration? TryBeginRetirement(WorkflowGeneration generation)
    {
        if (generation.Retiring || generation.PendingRoots != 0)
            return null;

        var jobs = getJobs(generation.WorkflowId);
        if (jobs.Count == 0
            || jobs.Any(job => !job.IsTerminal)
            || hasResumableState(jobs))
        {
            return null;
        }

        generation.Retiring = true;
        generation.RetiringJobs = jobs;
        return generation;
    }

    private void CompleteRetirement(WorkflowGeneration generation)
    {
        try
        {
            retire(
                generation.WorkflowId,
                generation.RetiringJobs!,
                generation.SettingsVersion);
        }
        finally
        {
            generation.RetirementCompleted.TrySetResult();
            lock (gate)
            {
                if (currentByWorkflow.TryGetValue(generation.WorkflowId, out var current)
                    && ReferenceEquals(current, generation))
                {
                    currentByWorkflow.Remove(generation.WorkflowId);
                }
            }
        }
    }

    internal int RetainedGenerationCount
    {
        get
        {
            lock (gate)
                return currentByWorkflow.Count;
        }
    }

    internal sealed class WorkflowRootLease
    {
        internal WorkflowRootLease(Job root, WorkflowGeneration generation)
        {
            Root = root;
            Generation = generation;
        }

        public Job Root { get; }
        internal WorkflowGeneration Generation { get; }
    }

    internal sealed class WorkflowGeneration
    {
        public WorkflowGeneration(Guid workflowId, Task ready)
        {
            WorkflowId = workflowId;
            Ready = ready;
        }

        public Guid WorkflowId { get; }
        public Task Ready { get; }
        public TaskCompletionSource RetirementCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int PendingRoots { get; set; }
        public long SettingsVersion { get; set; }
        public bool Retiring { get; set; }
        public IReadOnlyList<Job>? RetiringJobs { get; set; }
    }
}
