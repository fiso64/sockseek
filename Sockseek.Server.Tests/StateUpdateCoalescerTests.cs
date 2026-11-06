using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Sharing;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class StateUpdateCoalescerTests
{
    // Exercise the same capture/flush boundary as the owner using compact fixture inputs.
    internal static void CaptureUpdate(StateUpdateCoalescer coalescer, StateUpdate change)
    {
        coalescer.Begin(change.Scope, change.Epoch, change.PreviousSequence, change.Sequence, change.OccurredAtUtc)
            .Merge(change.State, change.Activity);
        coalescer.FlushIfRequired();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TransferFactsPreserveChangedBackComponentsMetadataAndEarlierReads(bool upload)
    {
        Guid id = Guid.NewGuid(), epoch = Guid.NewGuid();
        TransferReadSnapshot Capture(long revision, long bytes, bool initializing, int attribute, TransferReadSnapshot? previous)
        {
            var file = new TransferFileMetadataSnapshot("Exact.bin", 100, ".bin", null, null, null, null,
                [new FileAttributeSnapshot("7", attribute, (int)revision)]);
            var started = DateTimeOffset.UnixEpoch.AddSeconds(initializing ? 1 : 0);
            if (upload) return new(new UploadTransferSnapshot(id, revision, "Peer", "Folder\\Exact.bin", 100,
                DateTimeOffset.UnixEpoch, initializing ? UploadTransferState.Initializing : UploadTransferState.InProgress,
                UploadFailureReason.None, UploadCancellationSource.None, bytes, 12.9, DateTimeOffset.UnixEpoch,
                new(Guid.Empty, 1, started, null, bytes, 12.9), null, file, "Group", "Folder"), previous);
            return new(new TransferSnapshot(id, TransferSnapshotDirection.Download, TransferSnapshotSource.SoulseekPeer,
                null, null, revision, "Peer", "Folder\\Exact.bin", "output.bin", "candidate",
                initializing ? "Initializing" : "InProgress", bytes, 100, 1, null,
                RequestedAtUtc: DateTimeOffset.UnixEpoch, StartedAtUtc: started,
                LastProgressAtUtc: DateTimeOffset.UnixEpoch, BytesPerSecond: 12, File: file,
                GroupRef: "Group", GroupDisplayPath: "Folder", GroupRelativePath: "Exact.bin"), previous: previous);
        }
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        void Publish(TransferReadSnapshot fact) => CaptureUpdate(coalescer, new(StateStreamScopeDto.Daemon,
            epoch, fact.Revision - 1, fact.Revision, DateTimeOffset.UnixEpoch,
            StateDelta.Empty with { Transfers = [new TransferChange(fact)] }, []));
        var initial = Capture(1, 10, false, 1, null);
        Publish(initial);
        coalescer.Flush();
        var earlier = published.Single().State.Transfers.Single().Added!;
        var second = Capture(2, 80, true, 1, initial);
        Publish(second);
        // A detail reader may project an intermediate revision while transport
        // is still coalescing. Its captured row must remain independent.
        var detail = second.ToDto();
        var third = Capture(3, 10, false, 1, second);
        Publish(third);
        coalescer.Flush();
        var changedBack = published[1].State.Transfers.Single();
        Assert.IsNull(changedBack.Added);
        Assert.AreEqual("InProgress", changedBack.Status?.State);
        Assert.AreEqual(10L, changedBack.Progress?.BytesTransferred);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, changedBack.Scheduling?.StartedAtUtc);
        var fourth = Capture(4, 20, false, 7, third);
        Publish(fourth);
        var fifth = Capture(5, 77, false, 7, fourth);
        Publish(fifth);
        coalescer.Flush();
        var replacement = published[2].State.Transfers.Single().Added;
        Assert.IsNotNull(replacement, "Changed file metadata replaces the row even before its first wire projection.");
        Assert.AreEqual(7, replacement.File!.Attributes!.Single().Value);
        Assert.AreEqual(77L, replacement.Progress.BytesTransferred);
        Assert.AreEqual(1, earlier.File!.Attributes!.Single().Value);
        Assert.AreEqual(10L, earlier.Progress.BytesTransferred);
        Assert.AreEqual(80L, detail.Progress.BytesTransferred);
        Assert.AreEqual("Initializing", detail.Status.State);
    }

    [TestMethod]
    public void GlobalTransferTotalsCoalesceAndRecoverIndependentlyOfCachedRows()
    {
        StateUpdateBatchDto? published = null;
        var coalescer = new StateUpdateCoalescer(batches => published = batches.Single().ToDto());
        var epoch = Guid.NewGuid();
        var client = new DaemonClientStore();
        Assert.IsNull(client.GetTransferSummary());
        var first = new LiveTransferSummaryDto(12, 10, 2, 3, 0, 100, 20);
        var latest = first with { Queued = 2, DownloadRate = 200 };
        client.ApplySnapshot(new(StateStreamScopeDto.Daemon, new(epoch, 0), DateTimeOffset.UnixEpoch,
            null, [], [], [], [], TransferSummary: first));
        CaptureUpdate(coalescer, new(StateStreamScopeDto.Daemon, epoch, 0, 1, DateTimeOffset.UnixEpoch,
            StateDelta.Empty with { TransferSummary = first }, []));
        CaptureUpdate(coalescer, new(StateStreamScopeDto.Daemon, epoch, 1, 2, DateTimeOffset.UnixEpoch,
            StateDelta.Empty with { TransferSummary = latest }, []));
        coalescer.Flush();
        Assert.IsNotNull(published);
        Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(published).Status);
        Assert.AreEqual(latest, client.GetTransferSummary());
        Assert.AreEqual(latest, client.GetLiveStateView().TransferSummary);
        Assert.AreEqual(0, client.GetTransfers().Count);
        client.ApplySnapshot(new(StateStreamScopeDto.Daemon, new(Guid.NewGuid(), 0), DateTimeOffset.UnixEpoch,
            null, [], [], [], [], TransferSummary: LiveTransferSummaryDto.Empty));
        Assert.AreEqual(LiveTransferSummaryDto.Empty, client.GetTransferSummary());
    }

    [TestMethod]
    public void Flush_FoldsLaterComponentsIntoUnflushedEntityAdd()
    {
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var transfer = Transfer(transferId, jobId, workflowId, 1, 10, terminal: false);
        var job = new JobSummaryDto
        {
            JobId = jobId, WorkflowId = workflowId, Kind = ServerJobKind.Song,
            LifecycleState = ServerJobLifecycleState.Pending,
        };
        var running = job with
        {
            LifecycleState = ServerJobLifecycleState.Running,
            ActivityPhase = ServerJobActivityPhase.Searching,
            DiscoveryRawResultCount = 5,
        };

        CaptureUpdate(coalescer, Batch(
            epoch,
            0,
            1,
            workflowId,
            StateDelta.Empty with
            {
                Jobs = [new(new JobReadState(job, 1, false))],
                Transfers = [new TransferChange(new(transfer))],
            }));
        CaptureUpdate(coalescer, Batch(
            epoch,
            1,
            2,
            workflowId,
            StateDelta.Empty with
            {
                Jobs = [new(new JobReadState(running, 2, false), Added: false,
                    Fields: JobFields.Lifecycle | JobFields.Discovery)],
                Transfers =
                [
                    new TransferChange(new(transfer with
                    {
                        Revision = 2, BytesTransferred = 80,
                        StartedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                    }, previous: new(transfer))),
                ],
            }));
        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(0, published[0].PreviousSequence);
        Assert.AreEqual(2, published[0].Sequence);
        var addedJob = published[0].State.Jobs.Single().Added;
        Assert.IsNotNull(addedJob);
        Assert.AreEqual(2, addedJob.Revision);
        Assert.AreEqual(ServerJobLifecycleState.Running, addedJob.Lifecycle.LifecycleState);
        Assert.AreEqual(5, addedJob.Discovery.RawResultCount);
        var added = published[0].State.Transfers.Single().Added;
        Assert.IsNotNull(added);
        Assert.AreEqual(2, added.Revision);
        Assert.AreEqual(80, added.Progress.BytesTransferred);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            added.Scheduling?.StartedAtUtc);
        CaptureUpdate(coalescer, Batch(epoch, 2, 3, workflowId, StateDelta.Empty with
        {
            Jobs = [new(new JobReadState(running with
                { ActivityPhase = ServerJobActivityPhase.Downloading, DiscoveryRawResultCount = 7 }, 3, false),
                Added: false, Fields: JobFields.Lifecycle | JobFields.Discovery)],
            Transfers = [Change(transferId, 3, TransferFields.Progress, bytes: 90)],
        }));
        CaptureUpdate(coalescer, Batch(epoch, 3, 4, workflowId, StateDelta.Empty with
        {
            Jobs = [new(new JobReadState(running with { DiscoveryRawResultCount = 7 }, 4, false),
                Added: false, Fields: JobFields.Lifecycle)],
        }));
        coalescer.Flush();
        Assert.AreEqual(80, published[0].State.Transfers.Single().Added?.Progress.BytesTransferred,
            "A later flush must not mutate a batch already handed to transport.");
        Assert.AreEqual(2, published[1].PreviousSequence);
        Assert.AreEqual(4, published[1].Sequence);
        Assert.AreEqual(90, published[1].State.Transfers.Single().Progress?.BytesTransferred);
        var updatedJob = published[1].State.Jobs.Single();
        Assert.IsNull(updatedJob.Added);
        Assert.AreEqual(4, updatedJob.Revision);
        Assert.AreEqual(ServerJobActivityPhase.Searching, updatedJob.Lifecycle?.ActivityPhase,
            "A component changed back before flush still carries its latest value.");
        Assert.AreEqual(7, updatedJob.Discovery?.RawResultCount);
        Assert.AreEqual(5, addedJob.Discovery.RawResultCount,
            "A later flush must not mutate the job already handed to transport.");
    }

    [TestMethod]
    public void Flush_PreservesFinalTransferProgressAndRemovalTogether()
    {
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var transferId = Guid.NewGuid();

        CaptureUpdate(coalescer, Batch(
            epoch,
            0,
            1,
            workflowId,
            StateDelta.Empty with
            {
                Transfers =
                [
                    Change(transferId, 3, TransferFields.Status | TransferFields.Progress,
                        bytes: 100, terminal: true),
                ],
                RemovedTransferIds = [transferId],
            }));

        Assert.AreEqual(0, published.Count, "A file retirement uses the normal bounded publication cadence.");
        coalescer.Flush();
        Assert.AreEqual(1, published.Count);
        var delta = published[0].State.Transfers.Single();
        Assert.AreEqual(100, delta.Progress?.BytesTransferred);
        Assert.IsTrue(delta.Status?.IsTerminal);
        CollectionAssert.Contains(published[0].State.RemovedTransferIds.ToList(), transferId);
    }

    [TestMethod]
    public void Flush_PreservesActivityOrderAfterState()
    {
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        CaptureUpdate(coalescer, new StateUpdate(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDelta.Empty,
            [Activity(1, workflowId, "first")]));
        CaptureUpdate(coalescer, new StateUpdate(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            1,
            2,
            DateTimeOffset.UtcNow,
            StateDelta.Empty,
            [Activity(2, workflowId, "second")]));
        coalescer.Flush();

        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            published.Single().Activity.Select(activity => activity.Type).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TerminalChildBurst_CoalescesWithoutLosingOutcomesOrLatestParentCounts(bool parentAdded)
        => VerifyTerminalBurst(32, parentAdded);

    [TestMethod]
    [TestCategory("Load")]
    [DoNotParallelize]
    public void LargeTerminalBurst_DoesNotRepeatedlyCopyUnflushedState()
        => VerifyTerminalBurst(5_000, parentAdded: true, maximumAllocation: 32 * 1024 * 1024);

    private static void VerifyTerminalBurst(int children, bool parentAdded, long? maximumAllocation = null)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var parent = new JobSummaryDto
        {
            JobId = Guid.NewGuid(), WorkflowId = workflowId, Kind = ServerJobKind.JobList,
            LifecycleState = ServerJobLifecycleState.Running,
            Stats = new JobSummaryStatsDto(MemberCount: children, CompletedMemberCount: 0),
        };
        CaptureUpdate(coalescer, Batch(epoch, 0, 1, workflowId, StateDelta.Empty with
        {
            Jobs = [new(new JobReadState(parent, 1, false), Added: parentAdded, Fields: JobFields.Stats)],
        }));
        for (int index = 1; index <= children; index++)
        {
            var transferId = Guid.NewGuid();
            var child = new JobSummaryDto
            {
                JobId = Guid.NewGuid(), WorkflowId = workflowId, Kind = ServerJobKind.Song,
                LifecycleState = ServerJobLifecycleState.Terminal, TerminalOutcome = ServerJobTerminalOutcome.Cancelled,
            };
            CaptureUpdate(coalescer, Batch(epoch, index, index + 1, workflowId, StateDelta.Empty with
            {
                Jobs = [new(new JobReadState(child, 1, false), Added: false, Fields: JobFields.Lifecycle),
                    new(new JobReadState(parent with { Stats = new JobSummaryStatsDto(
                        MemberCount: children, CompletedMemberCount: index, FailedMemberCount: index) }, index + 1, false),
                        Added: false, Fields: JobFields.Stats)],
                Transfers = [Change(transferId, 1, TransferFields.Status | TransferFields.Progress, bytes: 50, terminal: true)],
                RemovedTransferIds = [transferId],
            }));
        }
        Assert.AreEqual(0, published.Count, "Terminal child bursts must use the existing bounded publication cadence.");
        coalescer.Flush();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (maximumAllocation is long limit)
        {
            Console.WriteLine($"{children} terminal children: {allocated / 1048576.0:F2} MiB allocated.");
            Assert.IsTrue(allocated < limit,
                $"A {children}-file burst allocated {allocated} bytes; accumulate changes instead of recopying its entire prefix.");
        }
        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(0, published[0].PreviousSequence);
        Assert.AreEqual(children + 1, published[0].Sequence);
        Assert.AreEqual(children, published[0].State.Jobs.Count(job => job.Lifecycle?.TerminalOutcome == ServerJobTerminalOutcome.Cancelled));
        Assert.AreEqual(children, published[0].State.Transfers.Count(transfer => transfer.Status?.IsTerminal == true
            && transfer.Progress?.BytesTransferred == 50));
        CollectionAssert.AreEquivalent(published[0].State.Transfers.Select(transfer => transfer.TransferId).ToArray(),
            published[0].State.RemovedTransferIds.ToArray());
        var result = published[0].State.Jobs.Single(job => job.JobId == parent.JobId);
        Assert.AreEqual(children, (result.Added?.Stats ?? result.Stats)?.CompletedMemberCount);
        Assert.AreEqual(children, (result.Added?.Stats ?? result.Stats)?.FailedMemberCount);
        CaptureUpdate(coalescer, Batch(epoch, children + 1, children + 2, workflowId,
            StateDelta.Empty with { RemovedWorkflowIds = [workflowId] }));
        Assert.AreEqual(2, published.Count, "Whole-workflow removal must still publish immediately.");
    }

    [TestMethod]
    public void QueueSummaryBurst_PublishesOneLatestDaemonDelta()
    {
        const int mutations = 128;
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        var epoch = Guid.NewGuid();

        for (int sequence = 1; sequence <= mutations; sequence++)
        {
            CaptureUpdate(coalescer, new StateUpdate(
                StateStreamScopeDto.Daemon,
                epoch,
                sequence - 1,
                sequence,
                DateTimeOffset.UnixEpoch,
                StateDelta.Empty with
                {
                    Daemon = DaemonState(sequence),
                },
                []));
        }
        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(0, published[0].PreviousSequence);
        Assert.AreEqual(mutations, published[0].Sequence);
        Assert.AreEqual(
            mutations,
            published[0].State.Daemon?.Uploads.QueueRevision);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DiscontinuousBatch_DoesNotCorruptPendingPrefix(bool changedEpoch)
    {
        var published = new List<StateUpdateBatchDto>();
        var coalescer = new StateUpdateCoalescer(batches => published.AddRange(batches.Select(batch => batch.ToDto())));
        Guid epoch = Guid.NewGuid(), workflow = Guid.NewGuid(), transfer = Guid.NewGuid();
        CaptureUpdate(coalescer, Batch(epoch, 0, 1, workflow, StateDelta.Empty with
        {
            Transfers = [Change(transfer, 1, TransferFields.Progress, bytes: 10)],
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => CaptureUpdate(coalescer, Batch(
            changedEpoch ? Guid.NewGuid() : epoch, changedEpoch ? 1 : 9, 10, workflow,
            StateDelta.Empty with { Transfers = [Change(transfer, 2, TransferFields.Progress, bytes: 99)] })));
        coalescer.Flush();
        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(0, published[0].PreviousSequence);
        Assert.AreEqual(1, published[0].Sequence);
        Assert.AreEqual(10, published[0].State.Transfers.Single().Progress?.BytesTransferred);
    }

    private static StateUpdate Batch(
        Guid epoch,
        long previous,
        long sequence,
        Guid workflowId,
        StateDelta state)
        => new(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            previous,
            sequence,
            DateTimeOffset.UtcNow,
            state,
            []);

    private static ActivityEventDto Activity(long sequence, Guid workflowId, string type)
        => new(
            sequence,
            DateTimeOffset.UtcNow,
            type,
            workflowId,
            null,
            null,
            new WorkflowMessageActivityDto("Information", null, type));

    private static DaemonStateDto DaemonState(long revision)
        => new(
            revision,
            new SoulseekClientStatusDto("Connected", ["Connected", "LoggedIn"], true),
            0,
            null,
            new SharingStateDto(
                DaemonFeatureState.Disabled,
                "NotConfigured",
                [],
                0,
                0,
                new ShareCatalogStateDto(
                    null,
                    0,
                    0,
                    0,
                    false,
                    null,
                    null),
                null,
                null),
            new UploadRuntimeStateDto(
                DaemonFeatureState.Disabled,
                "NotConfigured",
                false,
                10,
                0,
                checked((int)Math.Min(revision, int.MaxValue)),
                revision,
                revision,
                null),
            new ChatRuntimeStateDto(
                DaemonFeatureState.Disabled,
                "NotConfigured",
                0,
                0,
                0,
                0,
                revision),
            new NotificationSummaryDto(0, revision));

    private static TransferSnapshot Transfer(
        Guid transferId, Guid jobId, Guid workflowId, long revision, long bytes, bool terminal)
        => new(transferId, TransferSnapshotDirection.Download, TransferSnapshotSource.SoulseekPeer,
            jobId, workflowId, revision, "user", "track.mp3", "track.mp3", "candidate",
            terminal ? "Completed" : "InProgress", bytes, 100, 1, null,
            RequestedAtUtc: DateTimeOffset.UnixEpoch,
            TerminalOutcome: terminal ? TransferSnapshotTerminalOutcome.Succeeded : TransferSnapshotTerminalOutcome.None);

    private static TransferChange Change(Guid transferId, long revision, TransferFields fields, long bytes, bool terminal = false)
        => new(new(Transfer(transferId, Guid.Empty, Guid.Empty, revision, bytes, terminal)), false, fields);
}
