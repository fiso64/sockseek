using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Playlist
{
    [TestClass]
    public class PlaylistTests
    {
        private string testM3uPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testM3uPath = Path.Join(Path.GetTempPath(), $"test_playlist_{Guid.NewGuid()}.m3u8");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testM3uPath))
                File.Delete(testM3uPath);
        }

        [TestMethod]
        public void IncrementalPlaylistKeepsSourceOrderAcrossLateAlbumsGroupsAndSharedOwners()
        {
            File.WriteAllText(testM3uPath, "# Existing playlist\nexisting.mp3\n");
            var first = new SongJob(new SongQuery { Title = "First" });
            var album = new AlbumJob(new AlbumQuery { Album = "Late Album" });
            var failed = new SongJob(new SongQuery { Title = "Failed variant" });
            var successful = new SongJob(new SongQuery { Title = "Good variant" });
            var group = new SongGroupsJob(new SongQuery { Title = "Variants" }) { Songs = [failed, successful] };
            var extraction = new ExtractJob("late source");
            var queue = new JobList("Tracks", [first, album, group, first, extraction]);
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Playlist, false);
            failed.Fail(JobFailureReason.NoMatchingResults);
            editor.Observe(failed);
            editor.PublishPending();
            Assert.IsTrue(File.ReadAllLines(testM3uPath)[^1].StartsWith("#FAIL:"));
            successful.SetDone("Music/variant.mp3");
            editor.Observe(successful);
            var child = new SongJob(new SongQuery { Title = "Album track" });
            album.TrackJobs.Add(child);
            editor.RegisterChildren(album);
            child.SetDone("Music/album.mp3");
            editor.Observe(child);
            first.SetDone("Music/first.mp3");
            editor.Observe(first);
            // A resume wrapper has another identity but the same memberships.
            editor.AttachOwner(new JobList("Resume", [first, album, group]), synthetic: true);
            var last = new SongJob(new SongQuery { Title = "Last" });
            extraction.Result = last;
            editor.AttachOwner(new JobList("More tracks", [last]), synthetic: true);
            editor.AttachOwner(new JobList("Explicit repeat", [first]));
            editor.Observe(first);
            last.SetDone("Music/last.mp3");
            editor.Observe(last);
            editor.PublishPending();
            CollectionAssert.AreEqual(new[] { "# Existing playlist", "existing.mp3", "Music/first.mp3",
                "Music/album.mp3", "Music/variant.mp3", "Music/first.mp3", "Music/last.mp3", "Music/first.mp3" }, File.ReadAllLines(testM3uPath));
        }

        [TestMethod]
        public void Playlist_PreservesOrderAndHandlesDynamicExpansion()
        {
            var queue = new JobList("Test Queue");

            var song1 = new SongJob(new SongQuery { Artist = "Artist1", Title = "Title1" });
            song1.SetDone();
            song1.DownloadPath = "Artist1/Title1.mp3";
            queue.Add(song1);

            var album1 = new AlbumJob(new AlbumQuery { Artist = "AlbumArtist", Album = "Album1" });
            var folder = new AlbumFolder("user", "AlbumArtist\\Album1",
            [
                TestHelpers.CreateAlbumFile("user", "AlbumArtist\\Album1\\TrackA.mp3"),
                TestHelpers.CreateAlbumFile("user", "AlbumArtist\\Album1\\TrackB.mp3"),
            ]);
            album1.ResolvedTarget = folder;
            var tracks = album1.EnsureTrackJobs(folder);
            tracks[0].SetDone();
            tracks[0].DownloadPath = "AlbumArtist/Album1/TrackA.mp3";
            tracks[1].SetDone();
            tracks[1].DownloadPath = "AlbumArtist/Album1/TrackB.mp3";
            album1.SetDone();
            queue.Add(album1);

            var song2 = new SongJob(new SongQuery { Artist = "Artist2", Title = "Title2" });
            song2.SetDone();
            song2.DownloadPath = "Artist2/Title2.mp3";
            queue.Add(song2);

            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Playlist, false);
            editor.Refresh();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(4, lines.Length);
            Assert.AreEqual("Artist1/Title1.mp3", lines[0].Replace('\\', '/'));
            Assert.AreEqual("AlbumArtist/Album1/TrackA.mp3", lines[1].Replace('\\', '/'));
            Assert.AreEqual("AlbumArtist/Album1/TrackB.mp3", lines[2].Replace('\\', '/'));
            Assert.AreEqual("Artist2/Title2.mp3", lines[3].Replace('\\', '/'));
        }

        [TestMethod]
        public void Playlist_SongGroupsJob_OnlyEmitsSuccessfulVariant()
        {
            var queue = new JobList("Test Queue");

            var agg = new SongGroupsJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var variant1 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            variant1.Fail(JobFailureReason.NoMatchingResults);
            var variant2 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title (Remix)" });
            variant2.SetDone();
            variant2.DownloadPath = "Artist/Title (Remix).mp3";
            var variant3 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title (Live)" });
            variant3.Fail(JobFailureReason.AllDownloadsFailed);

            agg.Songs = [variant1, variant2, variant3];
            agg.SetDone();
            queue.Add(agg);

            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Playlist, false);
            editor.Refresh();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(1, lines.Length);
            Assert.AreEqual("Artist/Title (Remix).mp3", lines[0].Replace('\\', '/'));
        }

        [TestMethod]
        public void Playlist_DoesNotIncludeNonAudioFiles()
        {
            var queue = new JobList("Test Queue");
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            
            var audioSong = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
            audioSong.ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(new Soulseek.SearchResponse("user", 1, true, 100, 0, []), new Soulseek.File(1, "Track.mp3", 100, ".mp3"));
            audioSong.SetDone();
            audioSong.DownloadPath = "Artist/Album/Track.mp3";

            var imageSong = new SongJob(new SongQuery());
            imageSong.ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(new Soulseek.SearchResponse("user", 1, true, 100, 0, []), new Soulseek.File(2, "Cover.jpg", 100, ".jpg"));
            imageSong.SetDone();
            imageSong.DownloadPath = "Artist/Album/Cover.jpg";

            var folder = new AlbumFolder("user", "Artist\\Album",
            [
                TestHelpers.CreateAlbumFile(new Soulseek.SearchResponse("user", 1, true, 100, 0, []), new Soulseek.File(1, "Track.mp3", 100, ".mp3")),
                TestHelpers.CreateAlbumFile(new Soulseek.SearchResponse("user", 1, true, 100, 0, []), new Soulseek.File(2, "Cover.jpg", 100, ".jpg")),
            ]);
            album.ResolvedTarget = folder;
            album.TrackJobs.AddRange([audioSong, imageSong]);
            album.SetDone();
            queue.Add(album);

            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Playlist, false);
            editor.Refresh();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(1, lines.Length, "Playlist should only contain the audio file.");
            Assert.IsTrue(lines[0].EndsWith("Track.mp3"));
        }

        [TestMethod]
        public void Playlist_IncludesAiffButExcludesStandalonePdf()
        {
            var queue = new JobList("Test Queue");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, []);

            var aiff = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
            {
                ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(response, TestHelpers.CreateSlFile(@"Music\Track.aiff")),
                DownloadPath = "Music/Track.aiff",
            };
            aiff.SetDone();
            queue.Add(aiff);

            var pdf = new SongJob(new SongQuery { Artist = "Artist", Title = "Booklet" })
            {
                DownloadPath = "Music/Booklet.pdf",
            };
            pdf.SetDone();
            queue.Add(pdf);

            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Playlist, false);
            editor.Refresh();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(1, lines.Length);
            Assert.IsTrue(lines[0].EndsWith("Track.aiff"));
            Assert.IsFalse(lines.Any(line => line.Contains("Booklet.pdf")));
        }
    }
}
