using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Server.Planning;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PersistenceHandoffTrackerTests
{
    [TestMethod]
    public void CommittedSearchChunksWakeReadersIndependentlyOfRevisionAndObserverFailure()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid source = Guid.NewGuid(), other = Guid.NewGuid();
        var observed = new List<Guid>();
        tracker.SearchSourceChanged += _ => throw new InvalidOperationException("Injected observer failure");
        tracker.SearchSourceChanged += observed.Add;
        SearchResultsPersistenceMutation Chunk(Guid id)
            => new(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, id, 7, []);
        tracker.Committed([Chunk(source), Chunk(source), Chunk(other)]);
        CollectionAssert.AreEquivalent(new[] { source, other }, observed);
        observed.Clear();
        tracker.Committed([Chunk(source)]);
        CollectionAssert.AreEqual(new[] { source }, observed,
            "Another committed chunk with the same source revision must wake pending readers.");
        observed.Clear();
        tracker.Committed([SearchCompletion(source, revision: 7)]);
        CollectionAssert.AreEqual(new[] { source }, observed,
            "Completion without new files must wake a View waiting at the committed end.");
    }

    [TestMethod]
    public void SearchCompletionLossWakesReadersAfterStateChangesAndKeepsOtherSourcesHealthy()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid source = Guid.NewGuid(), other = Guid.NewGuid();
        tracker.RegisterJob(Guid.NewGuid(), source);
        tracker.RegisterJob(Guid.NewGuid(), other);
        var states = new List<(Guid Source, bool Lost)>();
        tracker.SearchSourceChanged += _ => throw new InvalidOperationException("Injected observer failure");
        tracker.SearchSourceChanged += id => states.Add((id, tracker.IsSearchCompletionLost(id)));
        tracker.PermanentlyFailed([SearchCompletion(source, revision: 3)], new IOException("Lost completion"));
        Assert.IsFalse(tracker.IsSearchCompletionLost(other));
        tracker.Committed([SearchCompletion(source, revision: 2)]);
        tracker.Committed([SearchCompletion(source, revision: 3)]);
        tracker.PermanentlyFailed([SearchCompletion(source, revision: 2)], new IOException("Stale failure"));
        CollectionAssert.AreEqual(new[] { (source, true), (source, true), (source, false), (source, false) }, states,
            "Observers must see the updated state; older commits/failures cannot supersede newer revisions.");
    }

    [TestMethod]
    public async Task RetirementWaitsForExactTerminalJobAndSearchRevisions()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        SearchViewSource source = SearchSource(jobId);
        var retired = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.JobProjectionHandoffCompleted += (_, _) => throw new InvalidOperationException("failed observer");
        tracker.JobProjectionHandoffCompleted += (_, ids) => retired.TrySetResult(ids);
        tracker.RegisterJob(workflowId, jobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 5 },
            new Dictionary<Guid, long> { [jobId] = 3 },
            new Dictionary<Guid, SearchViewSource> { [jobId] = source });
        Assert.AreSame(source, tracker.GetRetiringSearchSource(jobId));

        Task handoff = tracker.WaitForJobAsync(jobId, CancellationToken.None);
        Task[] jobProjections =
        [
            tracker.WaitForJobAsync(jobId, CancellationToken.None, includeSearchResults: false),
            tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None, includeSearchResults: false),
        ];
        tracker.Committed([JobMutation(workflowId, jobId, revision: 4)]);
        Assert.IsTrue(jobProjections.All(task => !task.IsCompleted),
            "An older job revision must not satisfy metadata readers.");
        Assert.IsFalse(retired.Task.IsCompleted);
        tracker.EnsureJobProjectionsAvailable();
        tracker.Committed([JobMutation(workflowId, jobId, revision: 5)]);
        // Await the event-driven release, including WhenAll's continuations;
        // do not depend on their thread scheduling at the exact commit return.
        await Task.WhenAll(jobProjections).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { jobId }, (await retired.Task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray());
        Assert.IsFalse(handoff.IsCompleted, "The job row alone must not expose incomplete search history.");
        Assert.AreSame(source, tracker.GetRetiringSearchSource(jobId),
            "Metadata acknowledgement must not discard still-needed raw read inputs.");

        tracker.Committed([SearchCompletion(jobId, revision: 2)]);
        Assert.IsFalse(handoff.IsCompleted, "An older search completion must not satisfy the handoff.");

        tracker.Committed([SearchCompletion(jobId, revision: 3)]);
        await handoff;
        Assert.IsNull(tracker.GetRetiringSearchSource(jobId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RawSearchLossDoesNotPoisonCommittedJobMetadata(bool failureBeforeRetirement)
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.JobProjectionHandoffCompleted += (_, _) => retired.TrySetResult();
        tracker.RegisterJob(workflowId, jobId);
        var completion = SearchCompletion(jobId, revision: 3);
        if (failureBeforeRetirement)
            tracker.PermanentlyFailed([completion], new IOException("raw history unavailable"));
        tracker.BeginRetirement(workflowId, new Dictionary<Guid, long> { [jobId] = 5 },
            new Dictionary<Guid, long> { [jobId] = 3 },
            new Dictionary<Guid, SearchViewSource> { [jobId] = SearchSource(jobId) });
        Task metadata = tracker.WaitForJobAsync(jobId, CancellationToken.None, includeSearchResults: false);
        if (!failureBeforeRetirement)
            tracker.PermanentlyFailed([completion], new IOException("raw history unavailable"));
        Assert.IsNull(tracker.GetRetiringSearchSource(jobId), "A failed handoff must release raw read inputs.");

        Assert.IsFalse(metadata.IsCompleted, "Raw history failure must not complete or fail a pending job projection.");
        tracker.EnsureJobProjectionsAvailable(workflowId);
        tracker.Committed([JobMutation(workflowId, jobId, revision: 5)]);
        Assert.IsTrue(metadata.IsCompletedSuccessfully);
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tracker.EnsureJobProjectionsAvailable();
        await tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None, includeSearchResults: false);
        await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(
            () => tracker.WaitForJobAsync(jobId, CancellationToken.None));
    }

    [TestMethod]
    public void ShutdownReleasesPendingSearchInputsWithoutAcknowledgingHistory()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, jobId);
        tracker.BeginRetirement(workflowId, new Dictionary<Guid, long> { [jobId] = 5 },
            new Dictionary<Guid, long> { [jobId] = 3 },
            new Dictionary<Guid, SearchViewSource> { [jobId] = SearchSource(jobId) });
        Assert.IsNotNull(tracker.GetRetiringSearchSource(jobId));

        tracker.ReleaseSearchSources();

        Assert.IsNull(tracker.GetRetiringSearchSource(jobId));
        Assert.IsFalse(tracker.WaitForJobAsync(jobId, CancellationToken.None).IsCompleted,
            "Releasing memory during shutdown cannot report missing history as committed.");
    }

    private static SearchViewSource SearchSource(Guid jobId)
    {
        var settings = new SearchSettings();
        return new(new SearchSession(jobId), SearchDefinition.Create(new SearchJob("query"), settings),
            settings, new Dictionary<string, int>(StringComparer.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CommitBeforeRetirementSatisfiesTheLaterHandoff(bool rawSearchPending)
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid firstJobId = Guid.NewGuid();
        var retired = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.JobProjectionHandoffCompleted += (_, ids) => retired.TrySetResult(ids);
        tracker.RegisterJob(workflowId, firstJobId);
        tracker.Committed([JobMutation(workflowId, firstJobId, revision: 4)]);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [firstJobId] = 4 },
            rawSearchPending ? new Dictionary<Guid, long> { [firstJobId] = 3 } : new Dictionary<Guid, long>());
        Task metadata = tracker.WaitForJobAsync(firstJobId, CancellationToken.None, includeSearchResults: false);
        Assert.IsTrue(metadata.IsCompletedSuccessfully);
        CollectionAssert.AreEqual(new[] { firstJobId }, (await retired.Task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray());
        if (rawSearchPending)
        {
            Assert.IsFalse(tracker.WaitForJobAsync(firstJobId, CancellationToken.None).IsCompleted);
            tracker.Committed([SearchCompletion(firstJobId, revision: 3)]);
        }
        await tracker.WaitForJobAsync(firstJobId, CancellationToken.None);
    }

    [TestMethod]
    public async Task CancellingOneWaiterDoesNotCancelTheSharedHandoff()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, jobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 2 },
            new Dictionary<Guid, long>());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        OperationCanceledException? cancellation = null;
        try
        {
            await tracker.WaitForJobAsync(jobId, cancelled.Token);
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }
        Assert.IsNotNull(cancellation);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tracker.WaitForJobAsync(jobId, cancelled.Token, includeSearchResults: false));

        Task survivingWaiter = tracker.WaitForJobAsync(jobId, CancellationToken.None);
        Assert.IsFalse(survivingWaiter.IsCompleted);
        tracker.Committed([JobMutation(workflowId, jobId, revision: 2)]);
        await survivingWaiter;
        await tracker.WaitForJobAsync(jobId, CancellationToken.None, includeSearchResults: false);
    }

    [TestMethod]
    public async Task ReusedWorkflowIdDoesNotMixPendingAndActiveGenerations()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid firstJobId = Guid.NewGuid();
        Guid secondJobId = Guid.NewGuid();
        var firstRetired = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRetired = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.JobProjectionHandoffCompleted += (_, ids) =>
        {
            if (ids.Contains(firstJobId)) firstRetired.TrySetResult(ids);
            else secondRetired.TrySetResult(ids);
        };
        tracker.RegisterJob(workflowId, firstJobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [firstJobId] = 4 },
            new Dictionary<Guid, long> { [firstJobId] = 3 });

        tracker.RegisterJob(workflowId, secondJobId);
        Task firstHandoff = tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None);
        Assert.IsFalse(firstHandoff.IsCompleted);
        tracker.Committed([JobMutation(workflowId, firstJobId, revision: 4)]);
        CollectionAssert.AreEqual(new[] { firstJobId }, (await firstRetired.Task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray());
        Assert.IsFalse(secondRetired.Task.IsCompleted);
        Assert.IsTrue(tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None, includeSearchResults: false).IsCompletedSuccessfully,
            "An active later generation must not hold the retired generation's committed metadata.");
        Assert.IsFalse(firstHandoff.IsCompleted);
        tracker.Committed([SearchCompletion(firstJobId, revision: 3)]);
        await firstHandoff;

        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [secondJobId] = 7 },
            new Dictionary<Guid, long>());
        Task secondHandoff = tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None);
        Assert.IsFalse(secondHandoff.IsCompleted);
        tracker.Committed([JobMutation(workflowId, secondJobId, revision: 7)]);
        await secondHandoff;
        CollectionAssert.AreEqual(new[] { secondJobId }, (await secondRetired.Task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray());
    }

    [TestMethod]
    public async Task PermanentMutationLossFailsOnlyTheAffectedWorkflowHandoff()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid otherWorkflowId = Guid.NewGuid();
        Guid otherJobId = Guid.NewGuid();
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.JobProjectionHandoffCompleted += (id, _) =>
        {
            if (id == workflowId) retired.TrySetResult();
            else otherRetired.TrySetResult();
        };
        var mutation = JobMutation(workflowId, jobId, revision: 2);
        tracker.RegisterJob(workflowId, jobId);
        tracker.RegisterJob(otherWorkflowId, otherJobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 2 },
            new Dictionary<Guid, long>());
        tracker.BeginRetirement(
            otherWorkflowId,
            new Dictionary<Guid, long> { [otherJobId] = 3 },
            new Dictionary<Guid, long>());

        tracker.PermanentlyFailed([mutation], new IOException("disk unavailable"));
        tracker.Committed([JobMutation(otherWorkflowId, otherJobId, revision: 3)]);
        await Task.WhenAll(retired.Task, otherRetired.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<PersistenceHandoffException>(() => tracker.EnsureJobProjectionsAvailable(workflowId));
        Assert.ThrowsExactly<PersistenceHandoffException>(() => tracker.EnsureJobProjectionsAvailable());
        tracker.EnsureJobProjectionsAvailable(otherWorkflowId);

        await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(
            () => tracker.WaitForJobAsync(jobId, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(
            () => tracker.WaitForJobAsync(jobId, CancellationToken.None, includeSearchResults: false));
        await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(
            () => tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None));
        await tracker.WaitForJobAsync(otherJobId, CancellationToken.None);
        await tracker.WaitForWorkflowAsync(otherWorkflowId, CancellationToken.None);
        await tracker.WaitForWorkflowAsync(Guid.NewGuid(), CancellationToken.None);
    }

    [TestMethod]
    public async Task SupersededTerminalMutationLossDoesNotFailTheFinalHandoff()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, jobId);
        tracker.PermanentlyFailed(
            [JobMutation(workflowId, jobId, revision: 2)],
            new IOException("temporary disk failure"));
        tracker.Committed([JobMutation(workflowId, jobId, revision: 3)]);

        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 3 },
            new Dictionary<Guid, long>());

        await tracker.WaitForJobAsync(jobId, CancellationToken.None);
        await tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompositeTerminalPublishesItsOwningJobOutcome(bool failed)
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        var terminal = TransferTerminal(Guid.NewGuid(), revision: 4)
            with { OwningJob = JobMutation(workflowId, jobId, revision: 3) with { Kind = "Song" } };
        tracker.RegisterJob(workflowId, jobId);
        tracker.BeginRetirement(workflowId, new Dictionary<Guid, long> { [jobId] = 3 }, new Dictionary<Guid, long>());
        if (failed)
        {
            tracker.PermanentlyFailed([terminal], new IOException("composite unavailable"));
            var metadata = tracker.WaitForJobAsync(jobId, CancellationToken.None, includeSearchResults: false);
            Assert.IsTrue(metadata.IsFaulted, "Losing the atomic composite must fail its owning job's handoff too.");
            await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(() => metadata);
        }
        else
        {
            tracker.Committed([terminal with { OwningJob = terminal.OwningJob! with { Revision = 2 } }]);
            Assert.IsFalse(tracker.WaitForJobAsync(jobId, CancellationToken.None).IsCompleted);
            tracker.Committed([terminal]);
            Assert.IsTrue(tracker.WaitForJobAsync(jobId, CancellationToken.None).IsCompletedSuccessfully,
                "The owning job's committed row must release its handoff, not wait for a separate duplicate mutation.");
        }
    }

    [TestMethod]
    public async Task UploadRetirementWaitsForExactTerminalTransferCommitWithoutPolling()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid transferId = Guid.NewGuid();
        tracker.BeginTransferTerminal(transferId, revision: 3);

        Task handoff = tracker.WaitForTransferAsync(
            transferId,
            revision: 3,
            CancellationToken.None);
        var retiredAtNotification = new List<bool>();
        tracker.TerminalTransferCommitted += () => retiredAtNotification.Add(handoff.IsCompleted);
        tracker.Committed([TransferTerminal(transferId, revision: 2)]);
        Assert.IsFalse(handoff.IsCompleted);

        tracker.Committed([TransferTerminal(transferId, revision: 3)]);
        await handoff;
        // A live snapshot must see the commit revision before the handoff
        // permits that transfer to disappear from live presentation.
        CollectionAssert.AreEqual(new[] { false, false }, retiredAtNotification);
    }

    [TestMethod]
    public async Task TransferCommitBeforeWaitStillSatisfiesRetirement()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid transferId = Guid.NewGuid();
        tracker.BeginTransferTerminal(transferId, revision: 4);
        tracker.Committed([TransferTerminal(transferId, revision: 4)]);

        await tracker.WaitForTransferAsync(
            transferId,
            revision: 4,
            CancellationToken.None);
    }

    [TestMethod]
    public void TerminalTransferNotification_IsPostCommitOnlyAndIsolatesObservers()
    {
        var tracker = new PersistenceHandoffTracker();
        int successfulNotifications = 0;
        tracker.TerminalTransferCommitted += () => throw new InvalidOperationException("observer failed");
        tracker.TerminalTransferCommitted += () => successfulNotifications++;

        tracker.Committed([JobMutation(Guid.NewGuid(), Guid.NewGuid(), revision: 1)]);
        Assert.AreEqual(0, successfulNotifications);

        tracker.Committed([TransferTerminal(Guid.NewGuid(), revision: 1)]);
        Assert.AreEqual(1, successfulNotifications);
    }

    private static JobPersistenceMutation JobMutation(
        Guid workflowId,
        Guid jobId,
        long revision)
        => new(
            Guid.NewGuid(),
            Sequence: revision,
            DateTimeOffset.UtcNow,
            jobId,
            revision,
            PersistenceMutationPriority.Terminal,
            workflowId,
            ParentJobId: null,
            SourceJobId: null,
            ResultJobId: null,
            DisplayId: 1,
            Kind: "Search",
            LifecycleState: "Terminal",
            ActivityPhase: "None",
            ActivityUntilUtc: null,
            TerminalOutcome: "Succeeded",
            SkipReason: "None",
            CancellationSource: "None",
            FailureReason: "None",
            FailureMessage: null,
            FailureDetail: null,
            ItemName: null,
            QueryText: "query",
            PayloadSchemaVersion: 1,
            PayloadJson: null);

    private static SearchCompletionPersistenceMutation SearchCompletion(Guid jobId, long revision)
        => new(
            Guid.NewGuid(),
            Sequence: revision,
            DateTimeOffset.UtcNow,
            jobId,
            revision,
            Query: "query",
            ResultCount: 1,
            LockedFileCount: 0,
            ResultPersistenceState: "Complete");

    private static TransferTerminalPersistenceMutation TransferTerminal(
        Guid transferId,
        long revision)
        => new(
            new TransferPersistenceMutation(
                Guid.NewGuid(),
                Sequence: revision,
                DateTimeOffset.UtcNow,
                transferId,
                revision,
                PersistenceMutationPriority.Terminal,
                JobId: null,
                WorkflowId: null,
                Direction: "Upload",
                Source: "SoulseekPeer",
                Username: "peer",
                RemotePath: "file",
                LocalPath: null,
                State: "Completed",
                TerminalOutcome: "Succeeded",
                TotalBytes: 100,
                TransferredBytes: 100,
                AttemptCount: 1,
                FailureReason: "None",
                FailureMessage: null),
            FinalAttempt: null,
            OwningJob: null);
}
