using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Persistence.Write;
using Sockseek.Server.Planning;

namespace Sockseek.Server.Persistence;

/// <summary>
/// Bridges the short interval between engine-owned live state retirement and
/// visibility of its terminal job and search-projection state in SQLite.
/// Tracking is per workflow generation so reusing a workflow ID does not mix
/// independent retirements.
/// </summary>
internal sealed class PersistenceHandoffTracker : IPersistenceMutationObserver
{
    private readonly Lock gate = new();
    private readonly ILogger<PersistenceHandoffTracker> logger;
    private readonly Dictionary<Guid, Generation> activeByWorkflow = [];
    private readonly Dictionary<Guid, Generation> generationByJob = [];
    private readonly Dictionary<Guid, List<Generation>> generationsByWorkflow = [];
    private readonly Dictionary<Guid, TransferHandoff> terminalTransfers = [];

    public event Action<IReadOnlyList<(Guid TransferId, long Revision)>>? TerminalTransfersCommitted;
    public event Action<Guid>? SearchSourceChanged;
    public event Action<Guid, IReadOnlyCollection<Guid>>? JobProjectionHandoffCompleted;

    public PersistenceHandoffTracker(ILogger<PersistenceHandoffTracker>? logger = null)
    {
        this.logger = logger ?? NullLogger<PersistenceHandoffTracker>.Instance;
    }

    public void RegisterJob(Guid workflowId, Guid jobId)
    {
        lock (gate)
        {
            if (generationByJob.ContainsKey(jobId))
                return;

            if (!activeByWorkflow.TryGetValue(workflowId, out var generation))
            {
                generation = CreateGenerationLocked(workflowId);
                activeByWorkflow.Add(workflowId, generation);
            }

            generation.JobIds.Add(jobId);
            generationByJob.Add(jobId, generation);
        }
    }

    public void BeginRetirement(
        Guid workflowId,
        IReadOnlyDictionary<Guid, long> terminalJobRevisions,
        IReadOnlyDictionary<Guid, long> searchCompletionRevisions,
        IReadOnlyDictionary<Guid, SearchViewSource>? searchSources = null)
    {
        Generation? released = null;
        lock (gate)
        {
            if (!activeByWorkflow.Remove(workflowId, out var generation))
            {
                if (terminalJobRevisions.Count == 0 && searchCompletionRevisions.Count == 0)
                    return;
                generation = CreateGenerationLocked(workflowId);
                generation.IsRetiring = true;
                foreach (Guid jobId in terminalJobRevisions.Keys.Concat(searchCompletionRevisions.Keys))
                {
                    generation.JobIds.Add(jobId);
                    generationByJob.TryAdd(jobId, generation);
                }
                FailGenerationLocked(generation, new InvalidOperationException(
                    $"Persistence handoff for workflow {workflowId:N} had no registered generation."));
                return;
            }

            generation.IsRetiring = true;
            foreach (var (jobId, revision) in terminalJobRevisions)
            {
                if (!ReferenceEquals(generationByJob.GetValueOrDefault(jobId), generation))
                {
                    FailGenerationLocked(generation, new InvalidOperationException(
                        $"Persistence handoff for workflow {workflowId:N} did not own terminal job {jobId:N}."));
                    return;
                }
                generation.RequiredJobRevisions[jobId] = revision;
            }
            foreach (var (jobId, revision) in searchCompletionRevisions)
            {
                if (!ReferenceEquals(generationByJob.GetValueOrDefault(jobId), generation))
                {
                    FailGenerationLocked(generation, new InvalidOperationException(
                        $"Persistence handoff for workflow {workflowId:N} did not own completed search {jobId:N}."));
                    return;
                }
                generation.RequiredSearchRevisions[jobId] = revision;
                if (searchSources?.GetValueOrDefault(jobId) is { } source)
                    generation.SearchSources[jobId] = source;
            }

            if (generation.JobIds.Count != generation.RequiredJobRevisions.Count)
            {
                FailGenerationLocked(generation, new InvalidOperationException(
                    $"Persistence handoff for workflow {workflowId:N} captured " +
                    $"{generation.RequiredJobRevisions.Count} terminal revisions for {generation.JobIds.Count} jobs."));
                return;
            }

            TryFailForLostRequiredMutationLocked(generation);
            if (TryCompleteLocked(generation)) released = generation;
        }
        if (released is not null) NotifyJobProjectionCommitted(released);
    }

    public SearchViewSource? GetRetiringSearchSource(Guid jobId)
    {
        lock (gate)
            return generationByJob.GetValueOrDefault(jobId)?.SearchSources.GetValueOrDefault(jobId);
    }

    public void ReleaseSearchSources()
    {
        lock (gate)
            foreach (Generation generation in generationByJob.Values.Distinct())
                generation.SearchSources.Clear();
    }

    public Task WaitForJobAsync(Guid jobId, CancellationToken cancellationToken, bool includeSearchResults = true)
    {
        Task completion;
        lock (gate)
        {
            if (!generationByJob.TryGetValue(jobId, out var generation) || !generation.IsRetiring)
                return Task.CompletedTask;
            else
                completion = includeSearchResults ? generation.Completion.Task : generation.JobProjectionCompletion.Task;
        }
        return completion.WaitAsync(cancellationToken);
    }

    public Task WaitForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken, bool includeSearchResults = true)
    {
        Task completion;
        lock (gate)
        {
            if (!generationsByWorkflow.TryGetValue(workflowId, out var generations))
                return Task.CompletedTask;
            else
            {
                Task[] pending = generations
                    .Where(generation => generation.IsRetiring)
                    .Select(generation => includeSearchResults ? generation.Completion.Task : generation.JobProjectionCompletion.Task)
                    .ToArray();
                if (pending.Length == 0)
                    return Task.CompletedTask;
                completion = Task.WhenAll(pending);
            }
        }
        return completion.WaitAsync(cancellationToken);
    }

    public void EnsureJobProjectionsAvailable(Guid? workflowId = null)
    {
        lock (gate)
        {
            IEnumerable<Generation> generations = workflowId is { } id
                ? generationsByWorkflow.GetValueOrDefault(id) ?? []
                : generationsByWorkflow.Values.SelectMany(items => items);
            foreach (var generation in generations)
            {
                // Pending metadata remains readable in its existing projection.
                // A failed handoff must still report unavailable history.
                var ready = generation.JobProjectionCompletion.Task;
                if (ready.IsCompleted) ready.GetAwaiter().GetResult();
            }
        }
    }

    public void BeginTransferTerminal(Guid transferId, long revision)
    {
        lock (gate)
        {
            if (!terminalTransfers.TryGetValue(transferId, out TransferHandoff? handoff)
                || revision > handoff.RequiredRevision && handoff.Completion.Task.IsCompleted)
            {
                terminalTransfers[transferId] = new TransferHandoff(revision);
                return;
            }
            handoff.RequiredRevision = Math.Max(handoff.RequiredRevision, revision);
        }
    }

    public void FailTransferTerminalAdmission(Guid transferId, long revision)
    {
        lock (gate)
        {
            if (terminalTransfers.TryGetValue(transferId, out TransferHandoff? handoff)
                && handoff.RequiredRevision <= revision)
            {
                handoff.Completion.TrySetException(new PersistenceHandoffException(
                    $"Historical state for transfer {transferId:N} is unavailable because its terminal mutation was not admitted.",
                    new InvalidOperationException("The persistence terminal queue rejected the transfer mutation.")));
            }
        }
    }

    public Task WaitForTransferAsync(
        Guid transferId,
        long revision,
        CancellationToken cancellationToken)
    {
        Task completion;
        lock (gate)
        {
            if (!terminalTransfers.TryGetValue(transferId, out TransferHandoff? handoff)
                || handoff.RequiredRevision < revision)
            {
                return Task.CompletedTask;
            }
            completion = handoff.Completion.Task;
        }
        return completion.WaitAsync(cancellationToken);
    }

    public void Committed(IReadOnlyList<PersistenceMutation> mutations)
    {
        List<Generation> released = [];
        var committed = mutations.Select(TerminalTransfer).OfType<TransferPersistenceMutation>()
            .Select(transfer => (transfer.TransferId, transfer.Revision)).ToArray();
        // Publish exact durable revisions before releasing command waiters. The
        // state owner retires a batch directly; no task per completed transfer.
        if (committed.Length > 0 && TerminalTransfersCommitted is { } observers)
        {
            foreach (var observer in Delegate.EnumerateInvocationList(observers))
            {
                try { observer(committed); }
                catch (Exception exception)
                {
                    // Observer failure cannot turn an already-committed batch into
                    // a writer retry or make the persistence handoff fail.
                    ServerLogMessages.TerminalTransferCommitObserverFailed(logger, exception);
                }
            }
        }
        lock (gate)
        {
            var touched = new HashSet<Generation>();
            foreach (var mutation in mutations)
            {
                if (TerminalTransfer(mutation) is { } transfer
                    && terminalTransfers.TryGetValue(transfer.TransferId, out TransferHandoff? transferHandoff)
                    && transfer.Revision >= transferHandoff.RequiredRevision)
                {
                    transferHandoff.Completion.TrySetResult();
                    terminalTransfers.Remove(transfer.TransferId);
                }
                // A composite's owning job is committed atomically with its
                // transfer; it need not arrive as a duplicate standalone row.
                switch (mutation is TransferTerminalPersistenceMutation { OwningJob: { } owningJob } ? owningJob : mutation)
                {
                    case JobPersistenceMutation job
                        when generationByJob.TryGetValue(job.JobId, out var generation):
                        SetMaximum(generation.CommittedJobRevisions, job.JobId, job.Revision);
                        touched.Add(generation);
                        break;
                    case SearchCompletionPersistenceMutation search
                        when generationByJob.TryGetValue(search.SearchJobId, out var generation):
                        SetMaximum(
                            generation.CommittedSearchRevisions,
                            search.SearchJobId,
                            search.Revision);
                        touched.Add(generation);
                        break;
                }
            }

            foreach (var generation in touched)
                if (TryCompleteLocked(generation)) released.Add(generation);
        }
        foreach (var generation in released) NotifyJobProjectionCommitted(generation);
        NotifySearchSourceChanges(mutations, completionFailed: false);
    }

    public bool IsSearchCompletionLost(Guid jobId)
    {
        lock (gate)
            return generationByJob.GetValueOrDefault(jobId) is { } generation
                && generation.FailedSearchMutations.GetValueOrDefault(jobId) is { } failed
                && failed.Revision > generation.CommittedSearchRevisions.GetValueOrDefault(jobId);
    }

    private void NotifySearchSourceChanges(IReadOnlyList<PersistenceMutation> mutations, bool completionFailed)
    {
        if (SearchSourceChanged is not { } observers) return;
        var sources = new HashSet<Guid>();
        foreach (PersistenceMutation mutation in mutations)
            if (mutation is SearchCompletionPersistenceMutation
                || !completionFailed && mutation is (SearchResultsPersistenceMutation or SearchIncompletePersistenceMutation))
                sources.Add(mutation.EntityId);
        // A changed source means committed rows OR a known permanent completion
        // loss. Update handoff state first; readers decide from that state and a
        // SQL snapshot. Never invent a successful commit notification on failure.
        foreach (Guid source in sources)
            foreach (Action<Guid> observer in Delegate.EnumerateInvocationList(observers))
                try { observer(source); }
                catch (Exception exception)
                {
                    ServerLogMessages.SearchSourceObserverFailed(logger, exception, source);
                }
    }

    public void PermanentlyFailed(
        IReadOnlyList<PersistenceMutation> mutations,
        Exception exception)
    {
        lock (gate)
        {
            var affected = new HashSet<Generation>();
            foreach (PersistenceMutation mutation in mutations)
            {
                if (TerminalTransfer(mutation) is { } transfer
                    && terminalTransfers.TryGetValue(transfer.TransferId, out TransferHandoff? transferHandoff)
                    && transfer.Revision >= transferHandoff.RequiredRevision)
                {
                    transferHandoff.Completion.TrySetException(new PersistenceHandoffException(
                        $"Historical state for transfer {transfer.TransferId:N} is unavailable because its terminal mutation was permanently lost.",
                        exception));
                }
                switch (mutation is TransferTerminalPersistenceMutation { OwningJob: { } owningJob } ? owningJob : mutation)
                {
                    case JobPersistenceMutation { Priority: PersistenceMutationPriority.Terminal } job
                        when generationByJob.TryGetValue(job.JobId, out var generation):
                        SetLatestFailure(
                            generation.FailedJobMutations,
                            job.JobId,
                            new FailedMutation(job.Revision, Describe(job), exception));
                        affected.Add(generation);
                        break;
                    case SearchCompletionPersistenceMutation search
                        when generationByJob.TryGetValue(search.SearchJobId, out var generation):
                        SetLatestFailure(
                            generation.FailedSearchMutations,
                            search.SearchJobId,
                            new FailedMutation(search.Revision, Describe(search), exception));
                        affected.Add(generation);
                        break;
                }
            }

            foreach (Generation generation in affected)
                TryFailForLostRequiredMutationLocked(generation);
        }
        NotifySearchSourceChanges(mutations, completionFailed: true);
    }

    private void TryFailForLostRequiredMutationLocked(Generation generation)
    {
        if (!generation.IsRetiring
            || generation.Completion.Task.IsCompleted && generation.JobProjectionCompletion.Task.IsCompleted)
            return;

        var requiredLosses = new List<FailedMutation>();
        foreach (var (jobId, requiredRevision) in generation.RequiredJobRevisions)
        {
            if (generation.CommittedJobRevisions.GetValueOrDefault(jobId) < requiredRevision
                && generation.FailedJobMutations.GetValueOrDefault(jobId) is { } failed
                && failed.Revision >= requiredRevision)
                requiredLosses.Add(failed);
        }
        bool jobProjectionUnavailable = requiredLosses.Count > 0;
        foreach (var (jobId, requiredRevision) in generation.RequiredSearchRevisions)
        {
            if (generation.CommittedSearchRevisions.GetValueOrDefault(jobId) < requiredRevision
                && generation.FailedSearchMutations.GetValueOrDefault(jobId) is { } failed
                && failed.Revision >= requiredRevision)
                requiredLosses.Add(failed);
        }
        if (requiredLosses.Count == 0)
            return;

        string items = string.Join(", ", requiredLosses.Take(3).Select(failure => failure.Description));
        if (requiredLosses.Count > 3)
            items += $", +{requiredLosses.Count - 3} more";
        FailGenerationLocked(generation, new PersistenceHandoffException(
            $"Historical state for workflow {generation.WorkflowId:N} is unavailable because " +
            $"persistence permanently lost required terminal mutation batch [{items}].",
            requiredLosses[0].Exception), jobProjectionUnavailable);
    }

    private bool TryCompleteLocked(Generation generation)
    {
        if (!generation.IsRetiring)
            return false;
        bool jobsCommitted = generation.RequiredJobRevisions.All(required =>
            generation.CommittedJobRevisions.GetValueOrDefault(required.Key) >= required.Value);
        bool released = jobsCommitted && generation.JobProjectionCompletion.TrySetResult();

        if (generation.Completion.Task.IsCompleted || !jobsCommitted
            || generation.RequiredSearchRevisions.Any(required =>
                generation.CommittedSearchRevisions.GetValueOrDefault(required.Key) < required.Value))
            return released;

        generation.Completion.TrySetResult();
        generation.SearchSources.Clear();
        foreach (Guid jobId in generation.JobIds)
            generationByJob.Remove(jobId);
        if (generationsByWorkflow.TryGetValue(generation.WorkflowId, out var generations))
        {
            generations.Remove(generation);
            if (generations.Count == 0)
                generationsByWorkflow.Remove(generation.WorkflowId);
        }
        return released;
    }

    private void NotifyJobProjectionCommitted(Generation generation)
    {
        // A failed durable write must leave the current facts readable. Only
        // successful exact-revision acknowledgement can release this generation.
        // Run outside the handoff lock: the state owner takes its own gate.
        if (JobProjectionHandoffCompleted is not { } observers) return;
        Guid[] jobIds = generation.JobIds.ToArray();
        foreach (var observer in Delegate.EnumerateInvocationList(observers))
        {
            try { observer(generation.WorkflowId, jobIds); }
            catch (Exception exception)
            {
                ServerLogMessages.JobProjectionRetirementObserverFailed(logger, generation.WorkflowId, exception);
            }
        }
    }

    private Generation CreateGenerationLocked(Guid workflowId)
    {
        var generation = new Generation(workflowId);
        if (!generationsByWorkflow.TryGetValue(workflowId, out var generations))
        {
            generations = [];
            generationsByWorkflow.Add(workflowId, generations);
        }
        generations.Add(generation);
        return generation;
    }

    private void FailGenerationLocked(Generation generation, Exception exception, bool jobProjectionUnavailable = true)
    {
        Exception failure = exception is PersistenceHandoffException
            ? exception
            : new PersistenceHandoffException(
                $"Historical state for workflow {generation.WorkflowId:N} is unavailable because " +
                "its live-to-persistent handoff invariant failed.",
                exception);
        if (jobProjectionUnavailable)
            generation.JobProjectionCompletion.TrySetException(failure);
        if (generation.Completion.TrySetException(failure))
        {
            generation.SearchSources.Clear();
            ServerLogMessages.PersistenceHandoffFailed(logger, generation.WorkflowId, failure);
        }
    }

    private static void SetMaximum(Dictionary<Guid, long> revisions, Guid id, long revision)
    {
        if (revision > revisions.GetValueOrDefault(id))
            revisions[id] = revision;
    }

    // Output finalization may publish a newer terminal header without repeating
    // its already-committed attempt. It participates in the same durability boundary.
    private static TransferPersistenceMutation? TerminalTransfer(PersistenceMutation mutation)
        => mutation switch
        {
            TransferTerminalPersistenceMutation terminal => terminal.Transfer,
            TransferPersistenceMutation { Priority: PersistenceMutationPriority.Terminal } terminal => terminal,
            _ => null,
        };

    private static void SetLatestFailure(
        Dictionary<Guid, FailedMutation> failures,
        Guid id,
        FailedMutation failure)
    {
        if (failures.GetValueOrDefault(id) is not { } current || failure.Revision > current.Revision)
            failures[id] = failure;
    }

    private static string Describe(PersistenceMutation mutation)
        => $"{mutation.GetType().Name}:{mutation.EntityId:N}@r{mutation.Revision}";

    private sealed class Generation(Guid workflowId)
    {
        public Guid WorkflowId { get; } = workflowId;
        public HashSet<Guid> JobIds { get; } = [];
        public Dictionary<Guid, SearchViewSource> SearchSources { get; } = [];
        public Dictionary<Guid, long> RequiredJobRevisions { get; } = [];
        public Dictionary<Guid, long> RequiredSearchRevisions { get; } = [];
        public Dictionary<Guid, long> CommittedJobRevisions { get; } = [];
        public Dictionary<Guid, long> CommittedSearchRevisions { get; } = [];
        public Dictionary<Guid, FailedMutation> FailedJobMutations { get; } = [];
        public Dictionary<Guid, FailedMutation> FailedSearchMutations { get; } = [];
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // Same retirement generation, distinct readiness predicate: metadata
        // readers need committed job/manifest rows, not every raw search result.
        public TaskCompletionSource JobProjectionCompletion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRetiring { get; set; }
    }

    private sealed record FailedMutation(long Revision, string Description, Exception Exception);

    private sealed class TransferHandoff(long requiredRevision)
    {
        public long RequiredRevision { get; set; } = requiredRevision;
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class PersistenceHandoffException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
