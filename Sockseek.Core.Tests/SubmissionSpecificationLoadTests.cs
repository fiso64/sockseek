using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Planning;

namespace Tests;

[TestClass]
[DoNotParallelize] // Global allocation measurements must exclude other test classes.
public sealed class SubmissionSpecificationLoadTests
{
    [TestMethod]
    [TestCategory("Load")]
    public void SuppliedAlbumFallbackPlan_RoundTripsOneHundredTenTrackOptionsWithoutRediscovery()
    {
        var query = new SongQuery { Artist = "Fixture", Album = "Album", Title = "Track", Length = 180 };
        var album = new AlbumJob(new AlbumQuery { Artist = "Fixture", Album = "Album" })
        {
            Results = Enumerable.Range(0, 100).Select(peer => new AlbumFolder($"Peer-{peer:D3}", @"Music\Album",
                Enumerable.Range(1, 10).Select(track => new AlbumFile(query,
                    new SearchProjectionInput(track, 1, $"Peer-{peer:D3}", 10,
                        $@"Music\Album\{track:D2} - Track.flac", 1000, 800, 24, 48_000, 180, ".flac",
                        1000, true, null, DateTimeOffset.UnixEpoch).ToFileCandidate())).ToList())).ToList(),
        };
        // Warm serializer metadata independently of this populated plan.
        _ = SubmissionSpecificationCodec.Serialize(SubmissionSpecification.Create(new AlbumJob(album.Query), new DownloadSettings()));
        var watch = Stopwatch.StartNew();
        string json = SubmissionSpecificationCodec.Serialize(SubmissionSpecification.Create(album, new DownloadSettings()));
        double captureMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var replay = (AlbumJob)SubmissionSpecificationCodec.Deserialize(json).MaterializeJob();
        double replayMs = watch.Elapsed.TotalMilliseconds;
        Assert.IsNull(replay.ResolvedTarget);
        CollectionAssert.AreEqual(album.Results.Select(folder => folder.Username).ToArray(),
            replay.Results.Select(folder => folder.Username).ToArray());
        Assert.AreEqual(1000, replay.Results.Sum(folder => folder.Files.Count));
        foreach (AlbumFolder folder in replay.Results)
        {
            DirectoryTransferPlan plan = AlbumTransferPlanner.FromSelectedDirectory(folder);
            Assert.AreEqual(10, plan.Entries.Count);
            Assert.AreEqual(10_000L, plan.TotalKnownBytes);
            Assert.IsTrue(plan.Entries.All(entry => entry.Target.Username == folder.Username));
        }
        Console.WriteLine($"PERF_ACCEPTED_PLAN album-options=100 files=1000 json-bytes={System.Text.Encoding.UTF8.GetByteCount(json)} "
            + $"capture-ms={captureMs:F1} replay-ms={replayMs:F1}");
    }

    [TestMethod]
    [TestCategory("Load")]
    public void TenThousandFrozenJobs_RoundTripWithoutRepeatingSettingsOrSharingMutableState()
    {
        var settings = new DownloadSettings { PrintOption = PrintOption.Jobs };
        settings.Search.NecessaryCond.Formats = ["flac"];
        var song = new SongJob(new SongQuery { Artist = "Fixture", Title = "Track" })
        {
            PlannedSettings = JobSettingsSnapshot.Capture(settings),
        };
        SubmissionSpecification leaf = SubmissionSpecification.Create(song);
        _ = leaf.MaterializeJob(); // Warm serializer/JIT metadata, not a settings-value cache.
        var jobs = new JobList("bulk", Enumerable.Range(0, 10_000).Select(index =>
            new SongJob(song.Query)
            {
                ItemNumber = index + 1,
                PlannedSettings = JobSettingsSnapshot.Capture(settings),
            }));
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        SubmissionSpecification root = SubmissionSpecification.Create(jobs, settings);
        double captureMs = watch.Elapsed.TotalMilliseconds;
        long captureBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        root = root with
        {
            Command = root.Command with
            {
                Children = root.Command.Children!.Select(command => command with
                {
                    // Retained JSON creates separate strings with equal contents.
                    PlannedEffectiveSettingsJson = new string(leaf.Command.PlannedEffectiveSettingsJson!.AsSpan()),
                }).ToArray(),
            },
        };

        allocated = GC.GetTotalAllocatedBytes(precise: true);
        watch.Restart();
        root.Validate();
        double validationMs = watch.Elapsed.TotalMilliseconds;
        long validationBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        allocated = GC.GetTotalAllocatedBytes(precise: true);
        watch.Restart();
        var inline = (JobList)root.MaterializeJob();
        double inlineMs = watch.Elapsed.TotalMilliseconds;
        long inlineBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Assert.AreEqual(10_000, inline.Count);
        var inlineFirst = inline[0].PlannedSettings!.ReadSettings();
        inlineFirst.Search.NecessaryCond.Formats = ["inline-only"];
        CollectionAssert.AreEqual(new[] { "flac" }, inline[^1].PlannedSettings!.ReadSettings().Search.NecessaryCond.Formats);
        watch.Restart();
        string json = SubmissionSpecificationCodec.Serialize(root);
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(json) < 8 * 1024 * 1024,
            "Retaining 10k small commands must not repeat tens of MiB of identical settings.");
        double serializationMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        SubmissionSpecification retained = SubmissionSpecificationCodec.Deserialize(json);
        double deserializationMs = watch.Elapsed.TotalMilliseconds;
        allocated = GC.GetTotalAllocatedBytes(precise: true);
        watch.Restart();
        var materialized = (JobList)retained.MaterializeJob();
        double materializationMs = watch.Elapsed.TotalMilliseconds;
        long materializationBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Console.WriteLine($"10k frozen jobs: capture {captureMs:F1} ms / {captureBytes / 1048576.0:F2} MiB; "
            + $"validate {validationMs:F1} ms / {validationBytes / 1048576.0:F2} MiB; "
            + $"inline materialize {inlineMs:F1} ms / {inlineBytes / 1048576.0:F2} MiB; "
            + $"serialize {serializationMs:F1} ms / {System.Text.Encoding.UTF8.GetByteCount(json) / 1048576.0:F2} MiB JSON; "
            + $"deserialize {deserializationMs:F1} ms; "
            + $"materialize {materializationMs:F1} ms / {materializationBytes / 1048576.0:F2} MiB.");

        Assert.AreEqual(10_000, materialized.Count);
        Assert.AreEqual(10_000, materialized.Jobs.Select(job => job.Id).Distinct().Count());
        var first = materialized.Jobs[0].PlannedSettings!.ReadSettings();
        var last = materialized.Jobs[^1].PlannedSettings!.ReadSettings();
        Assert.AreNotSame(first, last);
        Assert.AreNotSame(first.Search.NecessaryCond, last.Search.NecessaryCond);
        first.Search.NecessaryCond.Formats = ["txt"];
        CollectionAssert.AreEqual(new[] { "flac" }, last.Search.NecessaryCond.Formats);
        Assert.IsTrue(validationBytes < 8 * 1024 * 1024,
            $"Validating repeated settings allocated {validationBytes} bytes; each distinct JSON value should be decoded once per operation.");
        Assert.IsTrue(captureBytes < 40 * 1024 * 1024,
            $"Capturing repeated settings allocated {captureBytes} bytes; identical settings must not create a JSON string per job.");
        Assert.IsTrue(inlineBytes < 25 * 1024 * 1024,
            $"Restoring inline settings allocated {inlineBytes} bytes; repeated bindings must reuse frozen values before wire compaction.");
    }
}
