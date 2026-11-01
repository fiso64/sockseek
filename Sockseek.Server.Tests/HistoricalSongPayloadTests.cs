using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class HistoricalSongPayloadTests
{
    [TestMethod]
    public void RetainedSongPreservesSelectedIdentityAndUnknownMetadata()
    {
        var selected = Read("""
            {"Query":{"Title":"Track"},"CandidateCount":3,
             "ResolvedTarget":{"Username":"Peer Name","Filename":"Music\\Mix CASE.flac",
               "Size":128,"SampleRate":48000,"Extension":"flac",
               "Peer":{"HasFreeUploadSlot":false,"UploadSpeed":1000000},
               "Attributes":[{"Type":"BitDepth","Value":24}]}}
            """);
        Assert.AreEqual("Peer Name", selected.ResolvedUsername);
        Assert.AreEqual(@"Music\Mix CASE.flac", selected.ResolvedFilename);
        Assert.AreEqual(3, selected.CandidateCount);
        Assert.AreEqual(false, selected.ResolvedHasFreeUploadSlot);
        Assert.AreEqual(1_000_000, selected.ResolvedUploadSpeed);
        Assert.AreEqual(128L, selected.ResolvedSize);
        Assert.AreEqual(24, selected.ResolvedAttributes!.Single().Value);

        var legacy = Read("""{"Query":{"Title":"Track"}}""");
        Assert.IsNull(legacy.ResolvedUsername);
        Assert.IsNull(legacy.ResolvedHasFreeUploadSlot);
        Assert.IsNull(legacy.ResolvedSize);
        Assert.IsNull(legacy.CandidateCount);
        Assert.IsNull(legacy.LastAttemptedFile);

        var attempted = Read("""
            {"Query":{"Title":"Track"},"CandidateCount":2,
             "File":{"BytesTransferred":64,"FileSize":128},
             "LastAttemptedCandidate":{"Username":" Peer CASE ","Filename":"Music\\Mix CASE.flac",
               "Size":128,"BitRate":320,"BitDepth":24,"SampleRate":48000,"Extension":".flac",
               "Peer":{"HasFreeUploadSlot":false,"UploadSpeed":1000}}}
            """);
        Assert.IsNull(attempted.ResolvedUsername);
        Assert.IsNull(attempted.ExactTarget);
        Assert.IsNotNull(attempted.LastAttemptedFile);
        Assert.AreEqual(" Peer CASE ", attempted.LastAttemptedFile.Target.Username);
        Assert.AreEqual(@"Music\Mix CASE.flac", attempted.LastAttemptedFile.Target.Filename);
        Assert.AreEqual(24, attempted.LastAttemptedFile.Target.BitDepth);
        Assert.AreEqual(false, attempted.LastAttemptedFile.Peer.HasFreeUploadSlot);
        Assert.AreEqual(1000, attempted.LastAttemptedFile.Peer.UploadSpeed);
        Assert.AreEqual(50d, attempted.File.ProgressPercent);
    }

    private static SongJobPayloadDto Read(string payload)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new PersistedJob(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
            "Song", "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, null, null, "Track", now, now, now, 1, 1, payload);
        return (SongJobPayloadDto)HistoricalJobDtoMapper.ToPayload(job);
    }

    [TestMethod]
    public void RetainedAggregatePreservesOutcomeCounts()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new PersistedJob(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
            "Aggregate", "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, null, null, "Tracks", now, now, now, 1, 1,
            """{"Query":{},"SongCount":7,"CompletedSongCount":7,"SucceededSongCount":5,"FailedSongCount":2}""");
        var payload = (AggregateJobPayloadDto)HistoricalJobDtoMapper.ToPayload(job);
        Assert.AreEqual(7, payload.SongCount);
        Assert.AreEqual(7, payload.CompletedSongCount);
        Assert.AreEqual(5, payload.SucceededSongCount);
        Assert.AreEqual(2, payload.FailedSongCount);
        JobSummaryStatsDto stats = HistoricalJobDtoMapper.ToSummary(job).Stats!;
        Assert.AreEqual(7, stats.MemberCount);
        Assert.AreEqual(7, stats.CompletedMemberCount);
        Assert.AreEqual(2, stats.FailedMemberCount);
        var legacy = job with { PayloadJson = """{"Query":{},"SongCount":7}""" };
        var legacyPayload = (AggregateJobPayloadDto)HistoricalJobDtoMapper.ToPayload(legacy);
        Assert.IsNull(legacyPayload.CompletedSongCount);
        Assert.IsNull(legacyPayload.SucceededSongCount);
        Assert.IsNull(legacyPayload.FailedSongCount);
        JobSummaryStatsDto legacyStats = HistoricalJobDtoMapper.ToSummary(legacy).Stats!;
        Assert.AreEqual(7, legacyStats.MemberCount);
        Assert.IsNull(legacyStats.CompletedMemberCount);
        Assert.IsNull(legacyStats.FailedMemberCount);
        Assert.IsNull(HistoricalJobDtoMapper.ToSummary(job with { PayloadJson = "{invalid" }).Stats);
    }

    [TestMethod]
    public void RetainedLegacyListKeepsMissingOutcomeCountsUnknown()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new PersistedJob(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
            "JobList", "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, null, null, "List", now, now, now, 1, 1,
            """{"Count":3}""");
        var payload = (JobListPayloadDto)HistoricalJobDtoMapper.ToPayload(job);
        Assert.AreEqual(3, payload.Count);
        Assert.IsNull(payload.ActiveJobCount);
        Assert.IsNull(payload.CompletedJobCount);
        Assert.IsNull(payload.SucceededJobCount);
        Assert.IsNull(payload.FailedJobCount);
    }
}
