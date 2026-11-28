using Microsoft.EntityFrameworkCore;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence;
using Sockseek.Persistence.Sqlite;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class HistoricalSongPayloadTests
{
    // Stored payloads are read strictly, so a member added to a persisted record
    // needs a migration. These rows have the shape written before the search
    // settings gained PreferCommonTrackCount.
    [TestMethod]
    public async Task PayloadsStoredBeforeASearchSettingWasAddedAreReadableAfterMigration()
    {
        const string storedSettings = """
            {"NecessaryFile":{"LengthTolerance":null,"MinBitrate":null,"MaxBitrate":null,"MinSampleRate":null,"MaxSampleRate":null,"MinBitDepth":null,"MaxBitDepth":null,"StrictTitle":false,"StrictArtist":false,"StrictAlbum":false,"Formats":[],"BannedUsers":[],"AllowedUsers":[],"AcceptNoLength":true,"AcceptMissingProps":true},"PreferredFile":{"LengthTolerance":null,"MinBitrate":null,"MaxBitrate":null,"MinSampleRate":null,"MaxSampleRate":null,"MinBitDepth":null,"MaxBitDepth":null,"StrictTitle":false,"StrictArtist":false,"StrictAlbum":false,"Formats":[],"BannedUsers":[],"AllowedUsers":[],"AcceptNoLength":true,"AcceptMissingProps":true},"NecessaryFolder":{"MinTrackCount":null,"MaxTrackCount":null,"RequiredTrackTitles":[]},"PreferredFolder":{"MinTrackCount":null,"MaxTrackCount":null,"RequiredTrackTitles":[]},"StrictAlbumQuality":false,"DownrankOn":1,"IgnoreOn":2,"MinGroupShares":2,"GroupLengthTolerance":3}
            """;
        string storedPayload = """{"QueryText":"query","DefaultFileProjection":null,"DefaultFolderProjection":null,"ResultCount":7,"Revision":9,"IsComplete":true,"Definition":{"SchemaVersion":1,"Baseline":"Generic","DefaultProjection":"GenericFile","NetworkQuery":"query","FileQuery":{"Artist":"","Title":"query","Album":"","Uri":"","Length":-1,"ArtistMaybeWrong":false},"AlbumQuery":null,"IncludeFullFileResults":false,"ProjectionSettings":"""
            + storedSettings.Trim() + ""","GroupResults":false}}""";
        // A specification holds a definition for the search and for commands at any depth.
        const string storedSpecification = """{"search":{"projectionSettings":{"strictAlbumQuality":false}},"command":{"searchDefinition":{"projectionSettings":{"strictAlbumQuality":false}},"children":[{"searchDefinition":{"projectionSettings":{"strictAlbumQuality":false}}}]}}""";
        // Some stored payloads separate members with spaces.
        string spacedPayload = storedPayload.Replace("\":", "\": ").Replace(",\"", ", \"");
        string currentPayload = storedPayload.Replace("\"ProjectionSettings\":{", "\"ProjectionSettings\":{\"PreferCommonTrackCount\":true,");

        string directory = Directory.CreateTempSubdirectory("sockseek-payload-migration-").FullName;
        var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
        var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
        try
        {
            using var owner = SqliteDatabaseOwner.Acquire(options);
            await using var context = await factory.CreateDbContextAsync();
            await context.Database.MigrateAsync("20261124120000_TransferPhase");
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            int displayId = 0;
            foreach (var (id, payload) in new[] { ("stored", storedPayload), ("spaced", spacedPayload), ("current", currentPayload) })
                await context.Database.ExecuteSqlAsync($"""
                    INSERT INTO jobs (id, workflow_id, semantic_role, last_runtime_id, last_sequence, display_id, kind,
                        lifecycle_state, activity_phase, terminal_outcome, skip_reason, cancellation_source, failure_reason,
                        created_at_utc, updated_at_utc, completed_at_utc, revision, payload_schema_version, payload_json, print_option,
                        applied_profiles_json)
                    VALUES ({id}, {id}, 'UserRoot', 'runtime', 1, {++displayId}, 'Search', 'Terminal', 'None', 'Succeeded', 'None', 'None',
                        'None', 0, 0, 0, 1, 1, {payload}, 0, '[]');
                    """);
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO submission_specifications (submission_id, specification_json) VALUES ('submission', {storedSpecification});");

            await context.Database.MigrateAsync();

            foreach (var (id, expected) in new[] { ("stored", false), ("spaced", false), ("current", true) })
            {
                string json = await context.Database.SqlQuery<string>($"SELECT payload_json AS Value FROM jobs WHERE id = {id}").SingleAsync();
                var payload = (SearchJobSnapshotPayload)RetainedJobPayloadCodec.Read(
                    RetainedJobFixture.Create("Search") with { PayloadJson = json })!;
                Assert.AreEqual(expected, payload.Definition!.ProjectionSettings.PreferCommonTrackCount, id);
            }
            string specification = await context.Database.SqlQuery<string>(
                $"SELECT specification_json AS Value FROM submission_specifications").SingleAsync();
            Assert.AreEqual(3, specification.Split("\"preferCommonTrackCount\":false").Length - 1);
        }
        finally
        {
            global::Tests.TestDatabase.ClearPool(factory);
            Directory.Delete(directory, recursive: true);
        }
    }

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

        var printJob = RetainedJobFixture.Create("Song", song) with { PrintOption = (int)(PrintOption.Results | PrintOption.Full) };
        Assert.AreEqual(ServerPrintOption.Results | ServerPrintOption.Full, HistoricalJobDtoMapper.ToSummary(printJob).Display.PrintOption);
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
    public void RetainedGroupPreservesOutcomeCountsAndInvalidRowsDoNotHideNeighbors()
    {
        var job = RetainedJobFixture.Create("SongGroups", new SongGroupsExecutionPayload(new(null, null, null, null, null, false), null, new(7, 7, 5, 2)));
        var payload = (SongGroupsJobPayloadDto)HistoricalJobDtoMapper.ToPayload(job);
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
        => (SongJobPayloadDto)HistoricalJobDtoMapper.ToPayload(RetainedJobFixture.Create("Song", payload));

}
