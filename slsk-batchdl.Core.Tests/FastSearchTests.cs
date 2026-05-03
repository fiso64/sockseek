using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Tests.FastSearch
{
    [TestClass]
    public class FastSearchTests
    {
        private static (DownloadEngine app, string outputDir) CreateApp(
            ISoulseekClient client,
            string input,
            string[] extraArgs = null!)
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-fastsearch-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(outputDir);

            var eng = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var dl = new DownloadSettings();
            dl.Extraction.Input = input;
            dl.Output.ParentDir = outputDir;

            extraArgs ??= Array.Empty<string>();
            if (extraArgs.Contains("--fast-search"))
                dl.Search.FastSearch = true;
            var minSpeedIndex = Array.IndexOf(extraArgs, "--fast-search-min-up-speed");
            if (minSpeedIndex >= 0 && minSpeedIndex + 1 < extraArgs.Length)
                dl.Search.FastSearchMinUpSpeed = double.Parse(extraArgs[minSpeedIndex + 1]);

            var clientManager = TestHelpers.CreateMockClientManager(client, eng);
            var app           = new DownloadEngine(eng, clientManager);
            app.Enqueue(new ExtractJob(dl.Extraction.Input!, dl.Extraction.InputType), dl);
            app.CompleteEnqueue();
            return (app, outputDir);
        }

        // A search response whose user qualifies for fast-search:
        // free upload slot, 10 MB/s, a plain mp3 with no bracket decorators.
        private static SearchResponse FastUser(string filename = @"Music\testartist - testsong.mp3") =>
            new("fastuser", 1,
                hasFreeUploadSlot: true,
                uploadSpeed:       10 * 1024 * 1024,  // 10 MB/s
                queueLength:       0,
                fileList: new[] { new Soulseek.File(1, filename, 5000, ".mp3") });

        // ── Test 1: fast-search downloads successfully and cancels background search ──

        [TestMethod]
        public async Task SongDownload_FastSearch_SucceedsAndCancelsBackgroundSearch()
        {
            // Search delay simulates the search still running when the fast download fires.
            // The download is near-instant, so the search should be cancelled mid-delay.
            var client = new ClientTests.MockSoulseekClient(
                new[] { FastUser() }.ToList(),
                searchDelayMs: 30);

            var (app, outputDir) = CreateApp(client,
                "testartist - testsong",
                new[] { "--fast-search", "--fast-search-min-up-speed", "1" });

            try { await app.RunAsync(CancellationToken.None); }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }

            Assert.AreEqual(JobState.Done, app.Queue.AllSongs().Single().State,
                "Song should be downloaded via fast-search");
            Assert.AreEqual(1, client.SearchesCancelledMidDelay,
                "Background search should have been cancelled once the fast download succeeded");
        }

        // ── Test 2: fast-search fallback when provisional download fails ─────

        [TestMethod]
        public async Task SongDownload_FastSearch_FallsBackToFullCandidateListOnFailure()
        {
            // fast-user: qualifies for fast-search but download always fails.
            var fastUserResp = new SearchResponse(
                "fastuser", 1,
                hasFreeUploadSlot: true,
                uploadSpeed:       10 * 1024 * 1024,
                queueLength:       0,
                fileList: new[] { new Soulseek.File(1, @"Music\testartist - testsong.mp3", 5000, ".mp3") });

            // good-user: does not meet fast-search speed threshold, arrives in same search
            // response batch, downloads fine (mock generates fake bytes).
            var goodUserResp = new SearchResponse(
                "gooduser", 2,
                hasFreeUploadSlot: true,
                uploadSpeed:       512 * 1024,   // 0.5 MB/s — below 1 MB/s threshold
                queueLength:       0,
                fileList: new[] { new Soulseek.File(2, @"Music\testartist - testsong.mp3", 5000, ".mp3") });

            var client = new ClientTests.MockSoulseekClient(
                new[] { fastUserResp, goodUserResp }.ToList(),
                searchDelayMs: 30,
                failingUsers: new[] { "fastuser" });

            var (app, outputDir) = CreateApp(client,
                "testartist - testsong",
                new[] { "--fast-search", "--fast-search-min-up-speed", "1" });

            try
            {
                await app.RunAsync(CancellationToken.None);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }

            var song = app.Queue.AllSongs().Single();
            Assert.AreEqual(JobState.Done, song.State,
                "Should fall back to full candidate list and succeed via gooduser");
            Assert.AreEqual("gooduser", song.ChosenCandidate?.Username,
                "Should have downloaded from gooduser after fast-user failed");
        }

        // ── Test 3: fast-search waits for slow provisional download ──────────

        [TestMethod]
        public async Task SongDownload_FastSearch_WaitsForProvisionalDownloadToComplete()
        {
            // Simulate a scenario where the search completes instantly (searchDelayMs = 0),
            // but the provisional download takes some time (slowMode = true).
            // Without the fix, the engine would see the search complete, observe that the 
            // download task wasn't finished yet, assume it failed, and queue a SECOND download.
            var client = new ClientTests.MockSoulseekClient(
                new[] { FastUser() }.ToList(),
                slowMode: true);

            var (app, outputDir) = CreateApp(client,
                "testartist - testsong",
                new[] { "--fast-search", "--fast-search-min-up-speed", "1" });

            try
            {
                await app.RunAsync(CancellationToken.None);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                    System.IO.Directory.Delete(outputDir, true);
            }

            Assert.AreEqual(JobState.Done, app.Queue.AllSongs().Single().State,
                "Song should be downloaded successfully");
            Assert.AreEqual(1, client.DownloadCallCount,
                "Should only call DownloadAsync once. The engine should wait for the provisional download to finish rather than falling through to the fallback list.");
        }
    }
}
