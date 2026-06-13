using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core;

namespace Tests.Index
{
    [TestClass]
    public class IndexTests
    {
        private string testM3uPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testM3uPath = Path.Join(Path.GetTempPath(), $"test_m3u_{Guid.NewGuid()}.m3u8");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testM3uPath))
                File.Delete(testM3uPath);
        }

        private static (JobList queue, JobList slj, List<SongJob> songs) MakeSongQueue(IEnumerable<SongJob> initialSongs)
        {
            var slj = new JobList();
            foreach (var s in initialSongs)
                slj.Jobs.Add(s);
            var queue = new JobList();
            queue.Jobs.Add(slj);
            return (queue, slj, slj.Jobs.OfType<SongJob>().ToList());
        }

        [TestMethod]
        public void Index_LoadsOldFormat_PreviousRunData()
        {
            string initialContent =
                "#Sockseek:" +
                "path/to/file1,Artist1,,Title1,-1,0,1,0;" +
                "path/to/file2,Artist2,,Title2,-1,0,3,0;" +
                ",Artist3,,Title3,-1,0,2,3;";

            File.WriteAllText(testM3uPath, initialContent);

            var songs = new[]
            {
                new SongJob(new SongQuery { Artist = "Artist1", Title = "Title1" }),
                new SongJob(new SongQuery { Artist = "Artist2", Title = "Title2" }),
                new SongJob(new SongQuery { Artist = "Artist3", Title = "Title3" }),
            };
            var (queue, _, _) = MakeSongQueue(songs);
            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Index, true);

            // Verify downloaded track
            editor.TryGetPreviousRunResult(songs[0], out var prev1);
            Assert.IsNotNull(prev1);
            Assert.AreEqual(JobStateOld.Done, prev1.State);
            Assert.AreEqual("path/to/file1", prev1.DownloadPath);

            // Verify already-exists track
            editor.TryGetPreviousRunResult(songs[1], out var prev2);
            Assert.IsNotNull(prev2);
            Assert.AreEqual(JobStateOld.AlreadyExists, prev2.State);

            // Verify failed track
            editor.TryGetPreviousRunResult(songs[2], out var prev3);
            Assert.IsNotNull(prev3);
            Assert.AreEqual(JobStateOld.Failed, prev3.State);
            Assert.AreEqual(JobFailureReason.NoSuitableFileFound, prev3.FailureReason);
        }

        [TestMethod]
        public void Index_IndexRoundTrip_PreservesData()
        {
            var songs = new List<SongJob>
            {
                new SongJob(new SongQuery { Artist = "Artist1", Title = "Title1" }),
                new SongJob(new SongQuery { Artist = "Artist2", Title = "Title2" }),
                new SongJob(new SongQuery { Artist = "Artist3", Title = "Title3" }),
            };
            songs[0].SetDone();
            songs[0].DownloadPath = "path/to/file1";
            songs[1].Fail(JobFailureReason.NoSuitableFileFound);
            // songs[2] stays Pending

            var (queue, _, _) = MakeSongQueue(songs);
            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Index, true);
            editor.Update();

            // Load back with a fresh editor
            var lookupSongs = songs.Select(s => new SongJob(new SongQuery { Artist = s.Query.Artist, Title = s.Query.Title })).ToList();
            var (queue2, _, _) = MakeSongQueue(lookupSongs);
            var editor2 = new M3uEditor(testM3uPath, queue2, M3uOption.Index, true);

            // Verify downloaded track round-tripped
            editor2.TryGetPreviousRunResult(lookupSongs[0], out var prev1);
            Assert.IsNotNull(prev1);
            Assert.AreEqual(JobStateOld.Done, prev1.State);
            Assert.AreEqual("path/to/file1", prev1.DownloadPath);

            // Verify failed track round-tripped
            editor2.TryGetPreviousRunResult(lookupSongs[1], out var prev2);
            Assert.IsNotNull(prev2);
            Assert.AreEqual(JobStateOld.Failed, prev2.State);
            Assert.AreEqual(JobFailureReason.NoSuitableFileFound, prev2.FailureReason);

            // Pending track should not be in previous run data (state is Pending, it was skipped)
            editor2.TryGetPreviousRunResult(lookupSongs[2], out var prev3);
            Assert.IsNull(prev3);
        }

        [TestMethod]
        public void Index_SerializesFilePathsWithForwardSlashes()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            song.SetDone();
            song.DownloadPath = Path.Combine(Path.GetDirectoryName(testM3uPath)!, "nested", "song.mp3");

            var (queue, _, _) = MakeSongQueue([song]);
            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Index, true);
            editor.Update();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.IsTrue(lines.Any(line => line.StartsWith("./nested/song.mp3,")),
                string.Join("\n", lines));
            Assert.IsFalse(lines.Any(line => line.StartsWith(@".\nested\song.mp3,")),
                "Index paths should use forward slashes even on Windows.");
        }

        [TestMethod]
        public void Index_WithAlbumJobs_RoundTripsCorrectly()
        {
            var albumJobs = new List<AlbumJob>
            {
                new AlbumJob(new AlbumQuery { Artist = "ArtistA", Album = "AlbumA" }),
                new AlbumJob(new AlbumQuery { Artist = "ArtistB", Album = "AlbumB" }),
                new AlbumJob(new AlbumQuery { Artist = "ArtistC", Album = "AlbumC" }),
            };

            var queue = new JobList();
            foreach (var j in albumJobs)
                queue.Jobs.Add(j);

            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Index, true);

            // Update album states
            albumJobs[0].SetDone();
            albumJobs[0].DownloadPath = "download/path";
            albumJobs[1].Fail(JobFailureReason.NoSuitableFileFound);
            albumJobs[2].SetSkipped(JobSkipReason.Manual);

            editor.Update();

            // Read back with new editor using fresh AlbumJobs
            var lookupJobs = albumJobs.Select(j => new AlbumJob(new AlbumQuery { Artist = j.Query.Artist, Album = j.Query.Album })).ToList();
            var queue2 = new JobList();
            foreach (var j in lookupJobs)
                queue2.Jobs.Add(j);
            var editor2 = new M3uEditor(testM3uPath, queue2, M3uOption.Index, true);

            for (int i = 0; i < albumJobs.Count; i++)
            {
                var prev = editor2.PreviousRunResult((AlbumJob)lookupJobs[i]);
                Assert.IsNotNull(prev, $"Previous run result not found for {lookupJobs[i].Query.Artist} - {lookupJobs[i].Query.Album}");
                Assert.AreEqual(albumJobs[i].Query.Artist, prev.Artist);
                Assert.AreEqual(albumJobs[i].Query.Album, prev.Album);

                // Verify prev is a separate object from the job
                string originalPath = albumJobs[i].DownloadPath ?? "";
                albumJobs[i].DownloadPath = "this should not change prev.DownloadPath";
                Assert.AreNotEqual(albumJobs[i].DownloadPath, prev.DownloadPath);
                albumJobs[i].DownloadPath = originalPath;
            }
        }

        [TestMethod]
        public void Index_SpecialCharacters_RoundTripCorrectly()
        {
            var songs = new List<SongJob>
            {
                new SongJob(new SongQuery { Artist = "Artist, with commas", Title = "Title \"with\" quotes" }),
                new SongJob(new SongQuery { Artist = "Artist; semi", Title = "Title; semi" }),
            };
            songs[0].SetDone();
            songs[0].DownloadPath = "path/file.mp3";
            songs[1].Fail(JobFailureReason.AllDownloadsFailed);

            var (queue, _, _) = MakeSongQueue(songs);
            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Index, true);
            editor.Update();

            // Load back
            var lookupSongs = songs.Select(s => new SongJob(new SongQuery { Artist = s.Query.Artist, Title = s.Query.Title })).ToList();
            var (queue2, _, _) = MakeSongQueue(lookupSongs);
            var editor2 = new M3uEditor(testM3uPath, queue2, M3uOption.Index, true);

            editor2.TryGetPreviousRunResult(lookupSongs[0], out var prev1);
            Assert.IsNotNull(prev1);
            Assert.AreEqual("Artist, with commas", prev1.Artist);
            Assert.AreEqual("Title \"with\" quotes", prev1.Title);

            editor2.TryGetPreviousRunResult(lookupSongs[1], out var prev2);
            Assert.IsNotNull(prev2);
            Assert.AreEqual("Artist; semi", prev2.Artist);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, prev2.FailureReason);
        }

        [TestMethod]
        public void Index_TryGetFailureReason_ReturnsCorrectReason()
        {
            var songs = new List<SongJob>
            {
                new SongJob(new SongQuery { Artist = "A1", Title = "T1" }),
                new SongJob(new SongQuery { Artist = "A2", Title = "T2" }),
            };
            songs[0].Fail(JobFailureReason.NoSuitableFileFound);
            songs[1].SetDone();
            songs[1].DownloadPath = "p";

            var (queue, _, _) = MakeSongQueue(songs);
            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Index, true);
            editor.Update();

            // Reload
            var lookupSongs = songs.Select(s => new SongJob(new SongQuery { Artist = s.Query.Artist, Title = s.Query.Title })).ToList();
            var (queue2, _, _) = MakeSongQueue(lookupSongs);
            var editor2 = new M3uEditor(testM3uPath, queue2, M3uOption.Index, true);

            Assert.IsTrue(editor2.TryGetFailureReason(lookupSongs[0], out var reason));
            Assert.AreEqual(JobFailureReason.NoSuitableFileFound, reason);

            Assert.IsFalse(editor2.TryGetFailureReason(lookupSongs[1], out _));
        }

        [TestMethod]
        public void Index_DoesNotIncludeAlbumChildFiles()
        {
            var queue = new JobList("Test Queue");
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            
            var audioSong = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            audioSong.ResolvedTarget = new FileCandidate(new Soulseek.SearchResponse("user", 1, true, 100, 0, []), new Soulseek.File(1, "Track.mp3", 100, ".mp3"));
            audioSong.SetDone();
            audioSong.DownloadPath = "Artist/Album/Track.mp3";

            var imageSong = new SongJob(new SongQuery());
            imageSong.ResolvedTarget = new FileCandidate(new Soulseek.SearchResponse("user", 1, true, 100, 0, []), new Soulseek.File(2, "Cover.jpg", 100, ".jpg"));
            imageSong.SetDone();
            imageSong.DownloadPath = "Artist/Album/Cover.jpg";

            var folder = new AlbumFolder("user", "Artist\\Album", [audioSong, imageSong]);
            album.ResolvedTarget = folder;
            album.SetDone();
            queue.Add(album);

            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Index, true);
            editor.Update();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.IsTrue(lines.Any(l => l.Contains(",Artist,Album,,-1,1,")), "Index should contain the album entry.");
            Assert.IsFalse(lines.Any(l => l.Contains("Track.mp3")), "Index should not contain individual album child audio files.");
            Assert.IsFalse(lines.Any(l => l.Contains("Cover.jpg")), "Index should not contain non-audio files.");
        }
    }
}
