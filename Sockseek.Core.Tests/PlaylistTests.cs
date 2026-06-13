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
        public void Playlist_PreservesOrderAndHandlesDynamicExpansion()
        {
            var queue = new JobList("Test Queue");

            var song1 = new SongJob(new SongQuery { Artist = "Artist1", Title = "Title1" });
            song1.SetDone();
            song1.DownloadPath = "Artist1/Title1.mp3";
            queue.Add(song1);

            var album1 = new AlbumJob(new AlbumQuery { Artist = "AlbumArtist", Album = "Album1" });
            var songA = new SongJob(new SongQuery { Artist = "AlbumArtist", Title = "TrackA" });
            songA.SetDone();
            songA.DownloadPath = "AlbumArtist/Album1/TrackA.mp3";
            var songB = new SongJob(new SongQuery { Artist = "AlbumArtist", Title = "TrackB" });
            songB.SetDone();
            songB.DownloadPath = "AlbumArtist/Album1/TrackB.mp3";
            var folder = new AlbumFolder("user", "AlbumArtist\\Album1", [songA, songB]);
            album1.ResolvedTarget = folder;
            album1.SetDone();
            queue.Add(album1);

            var song2 = new SongJob(new SongQuery { Artist = "Artist2", Title = "Title2" });
            song2.SetDone();
            song2.DownloadPath = "Artist2/Title2.mp3";
            queue.Add(song2);

            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Playlist, false);
            editor.Update();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(4, lines.Length);
            Assert.AreEqual("Artist1/Title1.mp3", lines[0].Replace('\\', '/'));
            Assert.AreEqual("AlbumArtist/Album1/TrackA.mp3", lines[1].Replace('\\', '/'));
            Assert.AreEqual("AlbumArtist/Album1/TrackB.mp3", lines[2].Replace('\\', '/'));
            Assert.AreEqual("Artist2/Title2.mp3", lines[3].Replace('\\', '/'));
        }

        [TestMethod]
        public void Playlist_AggregateJob_OnlyEmitsSuccessfulVariant()
        {
            var queue = new JobList("Test Queue");

            var agg = new AggregateJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var variant1 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            variant1.Fail(JobFailureReason.NoSuitableFileFound);
            var variant2 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title (Remix)" });
            variant2.SetDone();
            variant2.DownloadPath = "Artist/Title (Remix).mp3";
            var variant3 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title (Live)" });
            variant3.Fail(JobFailureReason.AllDownloadsFailed);

            agg.Songs.AddRange([variant1, variant2, variant3]);
            agg.SetDone();
            queue.Add(agg);

            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Playlist, false);
            editor.Update();

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

            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Playlist, false);
            editor.Update();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(1, lines.Length, "Playlist should only contain the audio file.");
            Assert.IsTrue(lines[0].EndsWith("Track.mp3"));
        }
    }
}