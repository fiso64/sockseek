using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;

namespace Tests.Core;

[TestClass]
public sealed class UploadSchedulerTests
{
    [TestMethod]
    public void Scheduler_IsStrictRoundRobinAndFifoWithinUser()
    {
        var scheduler = CreateScheduler(slots: 1);
        var a1 = Admit(scheduler, "Alice", "a1");
        var a2 = Admit(scheduler, "Alice", "a2");
        var a3 = Admit(scheduler, "Alice", "a3");
        var b1 = Admit(scheduler, "Bob", "b1");
        var b2 = Admit(scheduler, "Bob", "b2");
        var c1 = Admit(scheduler, "Carol", "c1");

        AssertGrant(a1.Result, a1.Id);
        Assert.AreEqual(0, a2.Result.Grants.Count);
        Assert.AreEqual(0, a3.Result.Grants.Count);
        Assert.AreEqual(0, b1.Result.Grants.Count);

        AssertGrant(scheduler.Terminalize(a1.Id), b1.Id);
        AssertGrant(scheduler.Terminalize(b1.Id), c1.Id);
        AssertGrant(scheduler.Terminalize(c1.Id), a2.Id);
        AssertGrant(scheduler.Terminalize(a2.Id), b2.Id);
        AssertGrant(scheduler.Terminalize(b2.Id), a3.Id);
    }

    [TestMethod]
    public void QueueEstimate_FollowsRoundRobinRingRatherThanAdmissionOrder()
    {
        var scheduler = CreateScheduler(slots: 1);
        var a1 = Admit(scheduler, "Alice", "a1");
        var a2 = Admit(scheduler, "Alice", "a2");
        var a3 = Admit(scheduler, "Alice", "a3");
        var b1 = Admit(scheduler, "Bob", "b1");
        _ = Admit(scheduler, "Bob", "b2");
        var c1 = Admit(scheduler, "Carol", "c1");

        Assert.AreEqual(0, scheduler.Estimate(b1.Id).AheadCount);
        Assert.AreEqual(1, scheduler.Estimate(c1.Id).AheadCount);
        Assert.AreEqual(2, scheduler.Estimate(a2.Id).AheadCount);
        Assert.AreEqual(3, scheduler.Estimate(a3.Id).AheadCount);
        Assert.IsTrue(scheduler.TryGet(a1.Id, out _));
    }

    [TestMethod]
    public void QueueEstimate_ReturnsNullForUnknownOrNonQueuedTransfer()
    {
        var scheduler = CreateScheduler(slots: 1);
        var active = Admit(scheduler, "Alice", "active");

        Assert.IsNull(scheduler.Estimate(Guid.NewGuid()).AheadCount);
        Assert.IsNull(scheduler.Estimate(active.Id).AheadCount);
    }

    [TestMethod]
    public void QueueEstimate_ResolvesExactPeerAndPathFromTheSchedulerIndex()
    {
        var scheduler = CreateScheduler(slots: 1);
        _ = Admit(scheduler, "Alice", "active");
        var queued = Admit(scheduler, "Alice", "queued");

        Assert.AreEqual(
            scheduler.Estimate(queued.Id),
            scheduler.Estimate("Alice", @"Music\queued.flac"));
        Assert.IsNull(scheduler.Estimate("alice", @"Music\queued.flac").AheadCount);
        Assert.IsNull(scheduler.Estimate("Alice", @"Music\missing.flac").AheadCount);
    }

    [TestMethod]
    public void Scheduler_TreatsExactUsernameSpellingsAsDistinctPeers()
    {
        var scheduler = CreateScheduler(slots: 4);

        var first = Admit(scheduler, "Alice", "one");
        var second = Admit(scheduler, "alice", "two");
        var other = Admit(scheduler, "Bob", "three");

        AssertGrant(first.Result, first.Id);
        AssertGrant(second.Result, second.Id);
        AssertGrant(other.Result, other.Id);
        Assert.AreEqual(3, scheduler.GetRuntimeSnapshot().ActiveSlots);

        Assert.AreEqual(0, scheduler.Terminalize(first.Id).Grants.Count);
    }

    [TestMethod]
    public void DuplicateAdmission_ReturnsExistingWithoutChangingCountersOrRevision()
    {
        var scheduler = CreateScheduler(slots: 1);
        var first = Admit(scheduler, "Alice", "same");
        var before = scheduler.GetRuntimeSnapshot();

        Guid duplicateId = Guid.NewGuid();
        var duplicate = scheduler.Admit(Request(duplicateId, "Alice", "same"));
        var after = scheduler.GetRuntimeSnapshot();

        Assert.AreEqual(UploadAdmissionResultKind.Duplicate, duplicate.Kind);
        Assert.AreEqual(first.Id, duplicate.Entry!.TransferId);
        Assert.AreEqual(0, duplicate.Grants.Count);
        Assert.AreEqual(before, after);
        Assert.IsFalse(scheduler.TryGet(duplicateId, out _));
    }

    [TestMethod]
    public void DuplicateAdmission_PreservesPeerAndWirePathIdentity()
    {
        var scheduler = CreateScheduler(slots: 2);
        _ = Admit(scheduler, "Alice", "same");

        var distinct = scheduler.Admit(Request(Guid.NewGuid(), "alice", "same"));

        Assert.AreEqual(UploadAdmissionResultKind.Accepted, distinct.Kind);
        Assert.AreEqual("alice", distinct.Entry!.Username);
        var otherSpelling = scheduler.Admit(Request(Guid.NewGuid(), "Alice", "SAME"));
        Assert.AreEqual(UploadAdmissionResultKind.Accepted, otherSpelling.Kind);
        Assert.AreEqual(otherSpelling.Entry!.TransferId,
            scheduler.Admit(Request(Guid.NewGuid(), "Alice", "SAME")).Entry!.TransferId);
        Assert.IsNotNull(scheduler.Estimate("Alice", @"Music\SAME.flac").AheadCount);
        Assert.IsNull(scheduler.Estimate("Alice", @"Music\Same.flac").AheadCount);
    }

    [TestMethod]
    public void MoreThanFormerPerUserCeiling_RemainsAccepted()
    {
        var scheduler = CreateScheduler(slots: 1);
        for (int index = 0; index < 1_001; index++)
        {
            UploadAdmissionResult accepted = scheduler.Admit(
                Request(Guid.NewGuid(), "Alice", $"file-{index}"));
            Assert.AreEqual(UploadAdmissionResultKind.Accepted, accepted.Kind);
        }
        Assert.AreEqual(1_000, scheduler.GetRuntimeSnapshot().QueuedFiles);
    }

    [TestMethod]
    public void CancelQueued_RemovesDuplicateAndAccountingAtomically()
    {
        var scheduler = CreateScheduler(slots: 1);
        var active = Admit(scheduler, "Alice", "active");
        var queued = Admit(scheduler, "Alice", "queued", size: 42);

        var before = scheduler.GetRuntimeSnapshot();
        var cancelled = scheduler.CancelQueued(queued.Id);
        var after = scheduler.GetRuntimeSnapshot();

        Assert.AreEqual(queued.Id, cancelled.Removed!.TransferId);
        Assert.AreEqual(before.QueuedFiles - 1, after.QueuedFiles);
        Assert.AreEqual(before.QueuedBytes - 42, after.QueuedBytes);
        Assert.IsFalse(scheduler.TryGet(queued.Id, out _));

        var replacement = scheduler.Admit(Request(Guid.NewGuid(), "Alice", "queued", 42));
        Assert.AreEqual(UploadAdmissionResultKind.Accepted, replacement.Kind);
        Assert.AreEqual(0, replacement.Grants.Count);
        Assert.IsTrue(scheduler.TryGet(active.Id, out _));
    }

    [TestMethod]
    public void Terminalize_IsIdempotentAndNeverOversubscribesSlots()
    {
        var scheduler = CreateScheduler(slots: 1);
        var active = Admit(scheduler, "Alice", "one");
        var queued = Admit(scheduler, "Bob", "two");

        var first = scheduler.Terminalize(active.Id);
        var late = scheduler.Terminalize(active.Id);

        AssertGrant(first, queued.Id);
        Assert.IsNull(late.Removed);
        Assert.AreEqual(0, late.Grants.Count);
        Assert.AreEqual(1, scheduler.GetRuntimeSnapshot().ActiveSlots);
    }

    [TestMethod]
    public void LivePage_ContinuesBestEffortDuringChurnAndReportsChangeHint()
    {
        var scheduler = CreateScheduler(slots: 1);
        var active = Admit(scheduler, "Active", "active");
        var q1 = Admit(scheduler, "A", "one", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));
        _ = Admit(scheduler, "B", "two", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(2));
        _ = Admit(scheduler, "C", "three", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(3));

        var firstPage = scheduler.GetPage(null, null, 1);
        Assert.AreEqual(q1.Id, firstPage.Items.Single().TransferId);
        Assert.IsNotNull(firstPage.NextTransferId);

        _ = Admit(scheduler, "D", "four", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(4));

        var continued = scheduler.GetPage(
            firstPage.NextRequestedAtUtc,
            firstPage.NextTransferId,
            2,
            firstPage.ObservedQueueRevision);

        Assert.IsTrue(continued.QueueChanged);
        Assert.IsTrue(continued.ObservedQueueRevision > firstPage.ObservedQueueRevision);

        Assert.IsTrue(scheduler.TryGet(active.Id, out _));
    }

    [TestMethod]
    public void NewestPage_UsesStableDescendingTimelineBoundary()
    {
        var scheduler = CreateScheduler(slots: 1);
        _ = Admit(scheduler, "Active", "active");
        var oldest = Admit(scheduler, "A", "one", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));
        var middle = Admit(scheduler, "B", "two", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(2));
        var newest = Admit(scheduler, "C", "three", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(3));

        UploadQueuePage first = scheduler.GetNewestPage(null, null, 2);
        CollectionAssert.AreEqual(
            new[] { newest.Id, middle.Id },
            first.Items.Select(item => item.TransferId).ToArray());
        Assert.AreEqual(middle.Id, first.NextTransferId);

        _ = Admit(scheduler, "D", "later", requestedAt: DateTimeOffset.UnixEpoch.AddSeconds(4));
        UploadQueuePage second = scheduler.GetNewestPage(
            first.NextRequestedAtUtc,
            first.NextTransferId,
            2);

        CollectionAssert.AreEqual(
            new[] { oldest.Id },
            second.Items.Select(item => item.TransferId).ToArray());
        Assert.IsNull(second.NextTransferId);
    }

    [TestMethod]
    public void ConcurrentDuplicateAdmission_CreatesExactlyOneTransfer()
    {
        var scheduler = CreateScheduler(slots: 1);
        var results = new UploadAdmissionResult[32];

        Parallel.For(0, results.Length, i =>
        {
            results[i] = scheduler.Admit(Request(Guid.NewGuid(), "Alice", "same"));
        });

        Assert.AreEqual(1, results.Count(result => result.Kind == UploadAdmissionResultKind.Accepted));
        Assert.AreEqual(31, results.Count(result => result.Kind == UploadAdmissionResultKind.Duplicate));
        Assert.AreEqual(1, scheduler.GetRuntimeSnapshot().ActiveSlots);
        Assert.AreEqual(0, scheduler.GetRuntimeSnapshot().QueuedFiles);
    }

    [TestMethod]
    [TestCategory("Load")]
    public void Scheduler_IndexesRemainUsableBeyondFormerHundredThousandCeiling()
    {
        const int slots = 10;
        var scheduler = CreateScheduler(slots);
        DateTimeOffset origin = DateTimeOffset.UnixEpoch;
        Guid? deepQueued = null;

        for (int index = 0;
             index < slots + 100_001;
             index++)
        {
            Guid id = Guid.NewGuid();
            string remotePath = $@"Music\fixture-{index:D6}.flac";
            UploadAdmissionResult result = scheduler.Admit(new UploadAdmissionRequest(
                id,
                $"user-{index % 1_000:D4}",
                remotePath,
                RemotePathKey.Create(remotePath),
                1,
                origin.AddTicks(index)));
            if (result.Kind != UploadAdmissionResultKind.Accepted)
                Assert.Fail($"Admission {index} was unexpectedly {result.Kind}.");
            if (index == slots + 100_000)
                deepQueued = id;
        }

        UploadQueueRuntimeSnapshot snapshot = scheduler.GetRuntimeSnapshot();
        Assert.AreEqual(slots, snapshot.ActiveSlots);
        Assert.AreEqual(100_001, snapshot.QueuedFiles);
        Assert.IsTrue(snapshot.AcceptingUploads);
        Assert.AreEqual(100, scheduler.GetPage(null, null, 100).Items.Count);
        Assert.IsTrue(scheduler.Estimate(deepQueued!.Value).AheadCount > 0);

    }

    private static UploadScheduler CreateScheduler(int slots)
        => new(new UploadSettings { Slots = slots });

    private static (Guid Id, UploadAdmissionResult Result) Admit(
        UploadScheduler scheduler,
        string username,
        string file,
        long size = 1,
        DateTimeOffset? requestedAt = null)
    {
        Guid id = Guid.NewGuid();
        return (id, scheduler.Admit(Request(id, username, file, size, requestedAt)));
    }

    private static UploadAdmissionRequest Request(
        Guid id,
        string username,
        string file,
        long size = 1,
        DateTimeOffset? requestedAt = null)
    {
        string remotePath = $@"Music\{file}.flac";
        return new UploadAdmissionRequest(
            id,
            username,
            remotePath,
            RemotePathKey.Create(remotePath),
            size,
            requestedAt ?? DateTimeOffset.UtcNow);
    }

    private static void AssertGrant(UploadAdmissionResult result, Guid expected)
    {
        Assert.AreEqual(UploadAdmissionResultKind.Accepted, result.Kind);
        Assert.AreEqual(expected, result.Grants.Single().Entry.TransferId);
    }

    private static void AssertGrant(UploadSchedulerMutationResult result, Guid expected)
        => Assert.AreEqual(expected, result.Grants.Single().Entry.TransferId);
}
