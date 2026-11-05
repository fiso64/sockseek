using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class LogicalJobProgressTests
{
    [TestMethod]
    public void MembershipOutcomesFollowLateExtractionResultsAndDetachReplacedMembers()
    {
        var extract = new ExtractJob("outer");
        var list = new JobList("sources", [extract, extract]);
        var originalMembers = list.Jobs;
        extract.SetDone();
        Assert.AreEqual(new JobOutcomeSummary(2, 0, 0, 0), list.MemberOutcomes);
        var nested = new ExtractJob("inner");
        var song = new SongJob(new() { Title = "Track" });
        extract.Result = nested;
        nested.Result = song;
        nested.SetDone();
        song.SetDone();
        Assert.AreEqual(new JobOutcomeSummary(2, 2, 2, 0), list.MemberOutcomes);

        var replacement = new SongJob(new() { Title = "Replacement" });
        replacement.Fail(JobFailureReason.Other);
        nested.Result = replacement;
        Assert.AreEqual(new JobOutcomeSummary(2, 2, 0, 2), list.MemberOutcomes);
        song.Fail(JobFailureReason.Other);
        extract.AutoProcessResult = false;
        Assert.AreEqual(new JobOutcomeSummary(2, 2, 2, 0), list.MemberOutcomes);
        extract.AutoProcessResult = true;
        Assert.AreEqual(new JobOutcomeSummary(2, 2, 0, 2), list.MemberOutcomes);

        list.RemoveRange(0, 1);
        Assert.AreEqual(2, originalMembers.Count);
        Assert.AreEqual(new JobOutcomeSummary(1, 1, 0, 1), list.MemberOutcomes);
        list.Clear();
        replacement.SetDone();
        Assert.AreEqual(default(JobOutcomeSummary), list.MemberOutcomes);
    }

    [TestMethod]
    public void AggregateMembershipCountsConcurrentOutcomesAndReplacements()
    {
        var songs = Enumerable.Range(0, 16).Select(i => new SongJob(new() { Title = i.ToString() })).ToArray();
        var aggregate = new AggregateJob(new()) { Songs = songs };
        Parallel.For(0, songs.Length, i =>
        {
            if (i % 2 == 0) songs[i].SetDone();
            else songs[i].Fail(JobFailureReason.Other);
        });
        Assert.AreEqual(new JobOutcomeSummary(16, 16, 8, 8), aggregate.MemberOutcomes);
        aggregate.Songs = [];
        songs[1].SetDone();
        Assert.AreEqual(default(JobOutcomeSummary), aggregate.MemberOutcomes);
    }

    [TestMethod]
    public void FailedOrNonAutoProcessedExtractsKeepTheirOwnOutcome()
    {
        var failed = new ExtractJob("failed", InputType.String);
        failed.Fail(JobFailureReason.Other);
        var standalone = new ExtractJob("standalone", InputType.String)
        {
            AutoProcessResult = false,
        };
        standalone.SetDone();

        var list = new JobList("sources", [failed, standalone]);
        var payload = (JobListSnapshotPayload)CoreSnapshotFactory.CreateJob(list, 1).Payload;

        Assert.AreEqual(new JobOutcomeSummary(2, 2, 1, 1), payload.LogicalOutcomes);
    }
}
