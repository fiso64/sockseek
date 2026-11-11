using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class HistoricalSongPayloadTests
{
    [TestMethod]
    public void RetainedSongPreservesSelectedIdentityAndUnknownMetadata()
    {
        var song = new SongJobSnapshotPayload(new(null, "Track", null, null, null, false),
            null, null, null, SongDownloadSource.None, new(null, 0, null), null);
        var candidate = new FileCandidateSnapshot("Peer Name", @"Music\Mix CASE.flac",
            new("Peer Name", false, 1_000_000), 128, null, 24, 48000, null, "flac", [new("BitDepth", 24)]);
        var selected = Read(song with { CandidateCount = 3, ResolvedTarget = candidate });
        Assert.AreEqual("Peer Name", selected.ResolvedUsername);
        Assert.AreEqual(@"Music\Mix CASE.flac", selected.ResolvedFilename);
        Assert.AreEqual(3, selected.CandidateCount);
        Assert.AreEqual(false, selected.ResolvedHasFreeUploadSlot);
        Assert.AreEqual(1_000_000, selected.ResolvedUploadSpeed);
        Assert.AreEqual(128L, selected.ResolvedSize);
        Assert.AreEqual(24, selected.ResolvedAttributes!.Single().Value);

        var printJob = Job("Song", song) with { PrintOption = (int)(PrintOption.Results | PrintOption.Full) };
        Assert.AreEqual(ServerPrintOption.Results | ServerPrintOption.Full, HistoricalJobDtoMapper.ToSummary(printJob).PrintOption);
        Assert.AreEqual(PrintOption.Results | PrintOption.Full, HistoricalJobDtoMapper.ReadSnapshot(printJob).PrintOption);

        var unresolved = Read(song);
        Assert.IsNull(unresolved.ResolvedUsername);
        Assert.IsNull(unresolved.ResolvedHasFreeUploadSlot);
        Assert.IsNull(unresolved.ResolvedSize);
        Assert.IsNull(unresolved.CandidateCount);
        Assert.IsNull(unresolved.LastAttemptedFile);

        var attempted = Read(song with
        {
            CandidateCount = 2, File = new(null, 64, 128),
            LastAttemptedCandidate = candidate with { Username = " Peer CASE ", Extension = ".flac",
                BitRate = 320, Peer = new(" Peer CASE ", false, 1000) },
        });
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

    [TestMethod]
    public void RetainedAggregatePreservesOutcomeCountsAndInvalidRowsDoNotHideNeighbors()
    {
        var job = Job("Aggregate", new AggregateExecutionPayload(new(null, null, null, null, null, false), null, new(7, 7, 5, 2)));
        var payload = (AggregateJobPayloadDto)HistoricalJobDtoMapper.ToPayload(job);
        Assert.AreEqual(7, payload.SongCount);
        Assert.AreEqual(7, payload.CompletedSongCount);
        Assert.AreEqual(5, payload.SucceededSongCount);
        Assert.AreEqual(2, payload.FailedSongCount);
        JobSummaryStatsDto stats = HistoricalJobDtoMapper.ToSummary(job).Stats!;
        Assert.AreEqual(7, stats.MemberCount);
        Assert.AreEqual(7, stats.CompletedMemberCount);
        Assert.AreEqual(2, stats.FailedMemberCount);
        var invalid = job with { PayloadJson = "{invalid" };
        var unsupported = job with { PayloadSchemaVersion = RetainedJobPayloadCodec.SchemaVersion + 1 };
        var summaries = new[] { invalid, unsupported, job }.Select(HistoricalJobDtoMapper.ToSummary).ToArray();
        Assert.IsNull(summaries[0].Stats);
        Assert.IsNull(summaries[1].Stats);
        Assert.AreEqual(stats, summaries[2].Stats);
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => HistoricalJobDtoMapper.ToPayload(invalid));
        Assert.ThrowsExactly<InvalidDataException>(() => HistoricalJobDtoMapper.ToPayload(unsupported));
    }

    private static SongJobPayloadDto Read(SongJobSnapshotPayload payload)
        => (SongJobPayloadDto)HistoricalJobDtoMapper.ToPayload(Job("Song", payload));

    private static PersistedJob Job(string kind, JobSnapshotPayload payload)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
            kind, "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, null, null, "Track", now, now, now, 1, RetainedJobPayloadCodec.SchemaVersion,
            RetainedJobPayloadCodec.Capture(payload).Value);
    }
}
