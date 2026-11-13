using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Tests.ClientTests;

namespace Tests.EndToEnd
{
    [TestClass]
    public class GroupSkipTests
    {
        [TestMethod]
        [DataRow(false, true, "history")]
        [DataRow(true, true, "history")]
        [DataRow(false, false, "history")]
        [DataRow(false, true, "library")]
        [DataRow(true, true, "library")]
        [DataRow(false, false, "library")]
        [DataRow(false, true, "not-found")]
        [DataRow(true, true, "not-found")]
        [DataRow(false, false, "not-found")]
        public async Task MusicMatchingHonorsSkipSettingAndAcceptedSource(bool exact, bool skipExisting, string evidence)
        {
            string root = Directory.CreateTempSubdirectory("sockseek-skip-policy-").FullName;
            try
            {
                foreach (bool albumMode in new[] { false, true })
                {
                    string output = Path.Combine(root, albumMode ? "album" : "song");
                    string library = Path.Combine(output, "library", "Artist", "Album");
                    Directory.CreateDirectory(library);
                    if (evidence == "library")
                        File.WriteAllBytes(Path.Combine(library, "Artist - Song.mp3"), TestHelpers.EmptyMp3Bytes);
                    var query = new SongQuery { Artist = "Artist", Album = "Album", Title = "Song" };
                    var albumQuery = new AlbumQuery { Artist = "Artist", Album = "Album" };
                    Job Previous() => albumMode ? new AlbumJob(albumQuery) : new SongJob(query);
                    string indexPath = Path.Combine(output, "_index.csv");
                    if (evidence != "library")
                    {
                        Job previous = Previous();
                        if (evidence == "not-found") previous.Fail(JobFailureReason.NoSearchResults);
                        else JobOutcomeCommitter.Commit(previous, JobOutcome.Done(Path.Combine(output, "moved-or-deleted")));
                        new DownloadOutputFile(indexPath, new JobList(null, [previous]), OutputFileKind.Index, false).Refresh();
                    }
                    var remote = TestHelpers.CreateSlFile(@"Artist\Album\Artist - Song.mp3", size: 16, length: 180);
                    var response = new Soulseek.SearchResponse("Peer", 1, true, 100, 0, [remote]);
                    var candidate = SoulseekSearchAdapter.ToFileCandidate(response, remote);
                    var folder = new AlbumFolder("Peer", @"Artist\Album", [new AlbumFile(query, candidate)]) { IsFullyRetrieved = true };
                    Job requested = albumMode
                        ? new AlbumJob(albumQuery) { Results = [folder], ResolvedTarget = exact ? folder : null,
                            ValidationRequirement = exact ? AlbumValidationRequirement.UserAccepted : AlbumValidationRequirement.Standard }
                        : new SongJob(query) { Candidates = [candidate], ExactTarget = exact ? candidate.Target : null };
                    var settings = new DownloadSettings
                    {
                        Output = { ParentDir = output, IndexFilePath = indexPath, WriteIndex = false },
                        Skip = { SkipExisting = skipExisting, SkipMusicDir = Path.Combine(output, "library"), SkipNotFound = true },
                        Search = { NoBrowseFolder = true },
                    };
                    // Accepted intent survives persistence/replay, rather than depending on UI state.
                    requested = Sockseek.Core.Planning.SubmissionSpecificationCodec.Deserialize(
                        Sockseek.Core.Planning.SubmissionSpecificationCodec.Serialize(
                            Sockseek.Core.Planning.SubmissionSpecification.CreateShallow(requested, settings))).MaterializeJob();
                    var client = new MockSoulseekClient([response]);
                    var engineSettings = new EngineSettings { Username = "test", Password = "test" };
                    await using var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));
                    engine.Enqueue(requested, settings);
                    engine.CompleteEnqueue();
                    await engine.RunAsync(CancellationToken.None);
                    bool skipped = !exact && (skipExisting || evidence == "not-found");
                    Assert.AreEqual(skipped ? JobTerminalOutcome.Skipped : JobTerminalOutcome.Succeeded, requested.TerminalOutcome,
                        $"album={albumMode}, exact={exact}, skip={skipExisting}, evidence={evidence}: {requested.FailureMessage}");
                    Assert.AreEqual(skipped ? 0 : 1, client.DownloadCallCount);
                    if (skipped)
                    {
                        Assert.AreEqual(evidence == "not-found" ? JobSkipReason.NotFoundLastTime : JobSkipReason.AlreadyExists, requested.SkipReason);
                        Assert.AreEqual(evidence == "not-found" ? null : evidence == "history"
                            ? ExistingFileSource.CompletionHistory : ExistingFileSource.Library, requested.ExistingFileSource);
                    }
                }
            }
            finally { Directory.Delete(root, true); }
        }

        [TestMethod]
        public async Task GroupDownload_SkipExisting_CorrectlySkipsAfterSearch()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-skip-agg-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-skip-agg-out-" + Guid.NewGuid());
            Directory.CreateDirectory(musicRoot);
            Directory.CreateDirectory(outputDir);

            try
            {
                // File that exists in "Soulseek" (mock) and in our local output dir
                string fileName = "Artist1 - Song1.mp3";
                string existingFilePath = Path.Combine(outputDir, fileName);
                File.WriteAllBytes(Path.Combine(musicRoot, fileName), TestHelpers.EmptyMp3Bytes);
                File.WriteAllBytes(existingFilePath, TestHelpers.EmptyMp3Bytes);

                var testClient = MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);

                var eng = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist1 - Song1";
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = false;
                dl.Output.WritePlaylist = false;
                dl.Skip.SkipExisting = true;
                dl.Skip.SkipMode = SkipMode.Name;
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, eng);
                var app = new DownloadEngine(eng, clientManager);
                app.Enqueue(new ExtractJob(dl.Extraction.Input!, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                // Check the job queue to see if the track was marked as already existing
                Assert.IsNotNull(app.Queue, "Queue should not be null");
                var songGroupsJob = app.Queue.AllJobs().OfType<SongGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(songGroupsJob, "Should have found an SongGroupsJob");
                

                var song = songGroupsJob.Songs.FirstOrDefault(s => s.Query.ToString(true).Contains("Artist1"));
                Assert.IsNotNull(song, "Should have found the song in the group job results");
                
                // This is where it will fail if the regression is present:
                // It will be 'Downloaded' instead of 'AlreadyExists' because it wasn't skipped.
                // (Or it might fail during download if it tries to overwrite or something, 
                // but the goal is to see it skipped before it even tries to download).
                Assert.IsTrue(song.IsSkippedAlreadyExists,
                    $"Song should have been skipped. Outcome: {song.TerminalOutcome}. Skip reason: {song.SkipReason}. Failure reason: {song.FailureReason}");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task SongGroupsJob_SkipsExistingFilesOnRerun_IfAndOnlyIfWithinTolerance()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            // Response A: Length 180
            var responseA = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180)]);
            // Response B: Length 181 (within tol 3 of 180)
            var responseB = new Soulseek.SearchResponse("User2", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 181)]);
            // Response C: Length 185 (outside tol 3 of 180)
            var responseC = new Soulseek.SearchResponse("User3", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 185)]);

            var testClient1 = new ClientTests.MockSoulseekClient([responseA]);
            var testClient2 = new ClientTests.MockSoulseekClient([responseB]);
            var testClient3 = new ClientTests.MockSoulseekClient([responseC]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Extraction.RequestedMode = ExtractionMode.Song;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;
                dl.Search.GroupLengthTol = 3;
                dl.Skip.SkipExisting = true;

                // Run 1: 180s
                var clientManager1 = TestHelpers.CreateMockClientManager(testClient1, eng);
                var app1 = new DownloadEngine(eng, clientManager1);
                app1.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var aggJob1 = app1.Queue.AllJobs().OfType<SongGroupsJob>().FirstOrDefault();
                Assert.AreEqual(JobTerminalOutcome.Succeeded, aggJob1!.Songs[0].TerminalOutcome);
                int linesRun1 = File.ReadAllLines(dl.Output.IndexFilePath).Length;

                // Run 2: 181s (Within tolerance, should be skipped)
                var clientManager2 = TestHelpers.CreateMockClientManager(testClient2, eng);
                var app2 = new DownloadEngine(eng, clientManager2);
                app2.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var aggJob2 = app2.Queue.AllJobs().OfType<SongGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob2);
                Assert.AreEqual(1, aggJob2.Songs.Count);
                Assert.IsTrue(aggJob2.Songs[0].IsSkippedAlreadyExists, "Song Groups job should skip existing files on rerun if within length tolerance.");
                int linesRun2 = File.ReadAllLines(dl.Output.IndexFilePath).Length;
                Assert.AreEqual(linesRun1, linesRun2, "Index file should not duplicate entries when skipping within tolerance.");

                // Remove the payload to test history tolerance independently of
                // destination protection. History still skips run 2 without it.
                File.Delete(aggJob1.Songs[0].DownloadPath!);
                // Run 3: 185s (Outside tolerance, should NOT be skipped)
                var clientManager3 = TestHelpers.CreateMockClientManager(testClient3, eng);
                var app3 = new DownloadEngine(eng, clientManager3);
                app3.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app3.CompleteEnqueue();
                await app3.RunAsync(CancellationToken.None);

                var aggJob3 = app3.Queue.AllJobs().OfType<SongGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob3);
                Assert.AreEqual(1, aggJob3.Songs.Count);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, aggJob3.Songs[0].TerminalOutcome, "Song Groups job should NOT skip if the length is outside the group tolerance.");
                int linesRun3 = File.ReadAllLines(dl.Output.IndexFilePath).Length;
                Assert.IsTrue(linesRun3 > linesRun2, "Index file should append new entry for track outside tolerance.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumGroupsJob_MultipleAlbums_SkipsCorrectlyPerAlbumOnRerun()
        {
            // Regression: when an artist has multiple group albums, all AlbumJobs were
            // created with the original empty-album query, giving every album the same index key.
            // The last album to write would overwrite the others; on rerun every album matched
            // that single path, causing wrong skips and re-downloads.
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            // Two albums with different track lengths so they form distinct group buckets.
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Artist\Album A\01. Artist - Song.mp3", length: 100),
                TestHelpers.CreateSlFile(@"Artist\Album B\01. Artist - Song.mp3", length: 200),
            ]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Extraction.IsAlbum = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;
                dl.Skip.SkipExisting = true;

                // Run 1: download both albums.
                var app1 = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app1.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var aggJob1 = app1.Queue.AllJobs().OfType<AlbumGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob1);
                Assert.AreEqual(2, aggJob1.Albums.Count, "Both albums should be found.");
                Assert.IsTrue(aggJob1.Albums.All(a => a.TerminalOutcome == JobTerminalOutcome.Succeeded), "Both albums should download on first run.");

                // Run 2: both albums should be skipped.
                var app2 = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app2.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var aggJob2 = app2.Queue.AllJobs().OfType<AlbumGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob2);
                Assert.AreEqual(2, aggJob2.Albums.Count, "Both albums should be found on second run.");
                Assert.IsTrue(aggJob2.Albums.All(a => a.IsSkippedAlreadyExists),
                    $"Both albums should be skipped on rerun. Outcomes: {string.Join(", ", aggJob2.Albums.Select(a => $"{a.ItemName}={a.TerminalOutcome}/{a.SkipReason}"))}");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumGroupsJob_SkipDoesNotCorruptIndexState()
        {
            // Regression: JobToIndexState had no case for AlreadyExists, so skipped albums
            // were written as Pending (state=0) to the index. The third run would then see
            // Pending and re-download the album instead of skipping it again.
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Album\01. Artist - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Extraction.IsAlbum = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;
                dl.Skip.SkipExisting = true;

                async Task RunOnce()
                {
                    var app = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                    app.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                    app.CompleteEnqueue();
                    await app.RunAsync(CancellationToken.None);
                }

                await RunOnce(); // Run 1: download
                await RunOnce(); // Run 2: skip — must not corrupt index state

                // Run 3: must also skip (not re-download)
                var app3 = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(testClient, eng));
                app3.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app3.CompleteEnqueue();
                await app3.RunAsync(CancellationToken.None);

                var aggJob3 = app3.Queue.AllJobs().OfType<AlbumGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob3);
                Assert.AreEqual(1, aggJob3.Albums.Count);
                Assert.IsTrue(aggJob3.Albums[0].IsSkippedAlreadyExists,
                    "Album skipped on run 2 must still be skipped on run 3 (index state must not be corrupted).");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumGroupsJob_SkipsExistingAlbumsOnRerun()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            // Direct mock response.
            var file = TestHelpers.CreateSlFile(@"Album\01. Artist - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [file]);
            var testClient = new ClientTests.MockSoulseekClient([response]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "artist=Artist";
                dl.Extraction.IsAlbum = true;
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = true;
                dl.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl.Search.GroupResults = true;
                dl.Search.MinGroupShares = 1;
                dl.Skip.SkipExisting = true;

                var clientManager1 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app1 = new DownloadEngine(eng, clientManager1);
                app1.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var clientManager2 = TestHelpers.CreateMockClientManager(testClient, eng);
                var app2 = new DownloadEngine(eng, clientManager2);
                app2.Enqueue(new ExtractJob(dl.Extraction.Input, dl.Extraction.InputType), dl);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var aggJob = app2.Queue.AllJobs().OfType<AlbumGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob);
                Assert.AreEqual(1, aggJob.Albums.Count);
                Assert.IsTrue(aggJob.Albums[0].IsSkippedAlreadyExists, "AlbumGroups job should skip existing albums on rerun.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumGroupsJob_NotFoundChild_DoesNotReportDone()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var file = TestHelpers.CreateSlFile(@"Album\01. Artist - Song.mp3", length: 180);
            var response = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [file]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                var indexPath = Path.Combine(outputDir, "_index.csv");

                var missingAlbumSettings = new DownloadSettings();
                missingAlbumSettings.Output.ParentDir = outputDir;
                missingAlbumSettings.Output.WriteIndex = true;
                missingAlbumSettings.Output.IndexFilePath = indexPath;

                var missingAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
                var firstRun = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), eng));
                firstRun.Enqueue(missingAlbum, missingAlbumSettings);
                firstRun.CompleteEnqueue();
                await firstRun.RunAsync(CancellationToken.None);
                Assert.IsTrue(missingAlbum.IsUnsuccessfulTerminal);
                Assert.AreEqual(JobFailureReason.NoSearchResults, missingAlbum.FailureReason);

                var groupSettings = new DownloadSettings();
                groupSettings.Extraction.Input = "artist=Artist";
                groupSettings.Extraction.IsAlbum = true;
                groupSettings.Output.ParentDir = outputDir;
                groupSettings.Output.WriteIndex = true;
                groupSettings.Output.IndexFilePath = indexPath;
                groupSettings.Search.GroupResults = true;
                groupSettings.Search.MinGroupShares = 1;
                groupSettings.Skip.SkipNotFound = true;

                var secondRun = new DownloadEngine(eng, TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([response]), eng));
                secondRun.Enqueue(new ExtractJob(groupSettings.Extraction.Input, groupSettings.Extraction.InputType), groupSettings);
                secondRun.CompleteEnqueue();
                await secondRun.RunAsync(CancellationToken.None);

                var aggJob = secondRun.Queue.AllJobs().OfType<AlbumGroupsJob>().FirstOrDefault();
                Assert.IsNotNull(aggJob);
                Assert.AreEqual(1, aggJob.Albums.Count);
                Assert.AreEqual(JobTerminalOutcome.Skipped, aggJob.Albums[0].TerminalOutcome);
                Assert.AreEqual(JobSkipReason.NotFoundLastTime, aggJob.Albums[0].SkipReason);
                Assert.AreEqual(JobTerminalOutcome.Skipped, aggJob.TerminalOutcome, "Parent AlbumGroupsJob should not report Done when its only generated child was skipped.");
                Assert.AreEqual(JobSkipReason.NotFoundLastTime, aggJob.SkipReason);
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task NormalJob_SkipsExistingFiles_RespectsLengthToleranceNotGroupTol()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-test-out-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            var responseA = new Soulseek.SearchResponse("User1", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 180)]);
            var responseB = new Soulseek.SearchResponse("User2", 1, true, 100, 0, [TestHelpers.CreateSlFile(@"Music\Artist - Song.mp3", length: 182)]);
            
            var testClient1 = new ClientTests.MockSoulseekClient([responseA]);
            var testClient2 = new ClientTests.MockSoulseekClient([responseB]);

            try
            {
                var eng = new EngineSettings { Username = "u", Password = "p" };
                
                // RUN 1: Download track with query length 180
                var dl1 = new DownloadSettings();
                dl1.Extraction.Input = "artist=Artist, title=Song, length=180";
                dl1.Extraction.RequestedMode = ExtractionMode.Song;
                dl1.Output.ParentDir = outputDir;
                dl1.Output.WriteIndex = true;
                dl1.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                dl1.Search.GroupResults = false;
                dl1.Skip.SkipExisting = true;

                var clientManager1 = TestHelpers.CreateMockClientManager(testClient1, eng);
                var app1 = new DownloadEngine(eng, clientManager1);
                app1.Enqueue(new ExtractJob(dl1.Extraction.Input, dl1.Extraction.InputType), dl1);
                app1.CompleteEnqueue();
                await app1.RunAsync(CancellationToken.None);

                var songJob1 = app1.Queue.AllJobs().OfType<SongJob>().FirstOrDefault();
                Assert.AreEqual(JobTerminalOutcome.Succeeded, songJob1!.TerminalOutcome);

                // Keep history but remove the payload to isolate the matching
                // tolerance from the independent occupied-destination guard.
                File.Delete(songJob1.DownloadPath!);
                // RUN 2: Try to download with query length 182
                var dl2 = new DownloadSettings();
                dl2.Extraction.Input = "artist=Artist, title=Song, length=182";
                dl2.Extraction.RequestedMode = ExtractionMode.Song;
                dl2.Output.ParentDir = outputDir;
                dl2.Output.WriteIndex = true;
                dl2.Output.IndexFilePath = Path.Combine(outputDir, "_index.csv");
                
                dl2.Search.GroupResults = false;
                dl2.Search.NecessaryCond.LengthTolerance = 1; // Normal tolerance is tight (182 is outside)
                dl2.Search.GroupLengthTol = 5;            // Grouping tolerance is loose (182 is inside)
                dl2.Skip.SkipExisting = true;

                var clientManager2 = TestHelpers.CreateMockClientManager(testClient2, eng);
                var app2 = new DownloadEngine(eng, clientManager2);
                app2.Enqueue(new ExtractJob(dl2.Extraction.Input, dl2.Extraction.InputType), dl2);
                app2.CompleteEnqueue();
                await app2.RunAsync(CancellationToken.None);

                var songJob2 = app2.Queue.AllJobs().OfType<SongJob>().FirstOrDefault();
                Assert.IsNotNull(songJob2);
                
                // It should download again (Done), NOT AlreadyExists, because normal tolerance (1) applies.
                Assert.AreEqual(JobTerminalOutcome.Succeeded, songJob2.TerminalOutcome, "Normal job should NOT use GroupLengthTol to skip.");
            }
            finally
            {
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }
    }
}
