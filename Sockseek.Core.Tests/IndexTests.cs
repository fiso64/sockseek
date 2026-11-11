using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

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
                slj.Add(s);
            var queue = new JobList();
            queue.Add(slj);
            return (queue, slj, slj.Jobs.OfType<SongJob>().ToList());
        }

        [TestMethod]
        public void IncrementalIndexFactsAreImmediateImmutableAndRoundTripMultilineCsvWithoutAliasDuplicates()
        {
            var song = new SongJob(new SongQuery { Artist = "Björk, \"Artist\"", Album = "Album\r\nTwo", Title = "Track\nOne" });
            var queue = new JobList("Tracks", [song]);
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, false);
            song.SetDone("Music/Track.mp3");
            editor.Observe(song);
            var first = editor.PreviousRunResult(song);
            Assert.IsNotNull(first);
            Assert.AreEqual(JobStateOld.Done, first.State);
            Assert.IsFalse(File.Exists(testM3uPath), "Skip decisions must see accepted facts before publication.");
            Assert.IsTrue(editor.PublishPending());
            // Earlier writers emitted a row again for its album alias. Load
            // repeated identities into the same logical row at the file boundary.
            string written = File.ReadAllText(testM3uPath);
            File.AppendAllText(testM3uPath, written[(written.IndexOf('\n') + 1)..].Replace("Music/Track.mp3", "Music/Latest.mp3"));

            var reloaded = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            var saved = reloaded.PreviousRunResult(song);
            Assert.IsNotNull(saved);
            Assert.AreEqual(song.Query.Album, saved.Album);
            Assert.AreEqual(song.Query.Title, saved.Title);
            Assert.AreEqual(song.Query.Artist, saved.Artist);
            Assert.AreEqual("Music/Latest.mp3", saved.DownloadPath);
            Assert.AreEqual(1, reloaded.GetPreviousRunData().Count);
            var album = new AlbumJob(new AlbumQuery { Artist = song.Query.Artist, Album = song.Query.Album });
            Assert.AreEqual(saved, reloaded.PreviousRunResult(album));

            song.Fail(JobFailureReason.AllDownloadsFailed);
            song.DownloadPath = "";
            reloaded.Observe(song);
            Assert.AreEqual(JobStateOld.Done, first.State, "Returned snapshots cannot mutate under readers.");
            Assert.AreEqual(JobStateOld.Failed, reloaded.PreviousRunResult(album)?.State);
            Assert.IsTrue(reloaded.PublishPending());
            Assert.IsFalse(reloaded.PublishPending(), "Unchanged facts must not rewrite the output.");
            var final = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true, readOnly: true);
            Assert.AreEqual(1, final.GetPreviousRunData().Count, "An album lookup alias must not duplicate persisted rows.");
            Assert.AreEqual(JobStateOld.Failed, final.PreviousRunResult(song)?.State);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RepeatedTrackRowsDoNotOverwriteAnExplicitAlbumOutcome(bool albumFirst)
        {
            const string header = "filepath,artist,album,title,length,tracktype,state,failurereason\n";
            const string track = "song.mp3,Artist,Album,Track,180,0,1,0\n";
            const string album = "album-folder,Artist,Album,,-1,1,2,0\n";
            File.WriteAllText(testM3uPath, header + (albumFirst ? album + track : track + album) + track);
            var editor = new DownloadOutputFile(testM3uPath, new JobList(), OutputFileKind.Index, true, readOnly: true);
            var savedTrack = editor.PreviousRunResult(new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track", Length = 180 }));
            var savedAlbum = editor.PreviousRunResult(new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }));
            Assert.AreEqual(2, editor.GetPreviousRunData().Count);
            Assert.AreEqual("song.mp3", savedTrack?.DownloadPath);
            Assert.AreEqual(JobStateOld.Done, savedTrack?.State);
            Assert.AreEqual("album-folder", savedAlbum?.DownloadPath);
            Assert.AreEqual(JobStateOld.Failed, savedAlbum?.State);
        }

        [TestMethod]
        public void IncrementalGroupIndexPreservesDurationToleranceAndUnknownLength()
        {
            var original = new SongJob(new SongQuery { Artist = "Artist", Title = "Track", Length = 180 });
            var different = new SongJob(new SongQuery { Artist = "Other", Title = "Track", Length = 182 });
            var unknown = new SongJob(new SongQuery { Artist = "Artist", Title = "Track", Length = -1 });
            var variant = new SongJob(new SongQuery { Artist = "ARTIST", Title = "TRACK", Length = 182 });
            var settings = new DownloadSettings { Search = new SearchSettings { GroupResults = true, GroupLengthTol = 3 } };
            var group = new SongGroupsJob(new SongQuery { Artist = "Artist" }) { Config = settings, Songs = [variant] };
            var queue = new JobList("Tracks", [original, different, unknown, group]);
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, false);
            foreach (var song in new[] { original, different, unknown, variant })
            {
                song.SetDone(song.Query.Artist + song.Query.Length + ".mp3");
                editor.Observe(song);
            }
            var matched = editor.PreviousRunResult(variant, settings.Search);
            Assert.IsNotNull(matched);
            Assert.AreEqual(180, matched.Length);
            Assert.AreEqual(variant.DownloadPath, matched.DownloadPath);
            Assert.AreEqual(unknown.DownloadPath, editor.PreviousRunResult(unknown)?.DownloadPath);
            Assert.AreEqual(different.DownloadPath, editor.PreviousRunResult(different)?.DownloadPath);
            Assert.AreEqual(3, editor.GetPreviousRunData().Count);
        }

        [TestMethod]
        [TestCategory("Load")]
        [TestCategory("CI")]
        public void TenThousandIncrementalMusicCompletionsHaveBoundedAllocation()
        {
            var songs = Enumerable.Range(0, 10_000).Select(i => new SongJob(
                new SongQuery { Artist = "Artist", Title = "Track " + i })
                { Config = new DownloadSettings { Search = new SearchSettings { GroupResults = true } } }).ToArray();
            var queue = new JobList("Tracks", songs);
            var index = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, false);
            var playlist = new DownloadOutputFile(testM3uPath + ".playlist", queue, OutputFileKind.Playlist, false);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            foreach (var song in songs)
            {
                song.SetDone("Music/" + song.Query.Title + ".mp3");
                index.Observe(song);
                playlist.Observe(song);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.IsLessThan(25_000_000L, allocated, "Changed-entry work must not scan/encode every sibling.");
            Assert.AreEqual(10_000, index.GetPreviousRunData().Count);
            Assert.IsFalse(File.Exists(testM3uPath));
            try
            {
                index.PublishPending();
                playlist.PublishPending();
                Assert.AreEqual(10_000, File.ReadAllLines(testM3uPath + ".playlist").Length);
                allocated = GC.GetAllocatedBytesForCurrentThread();
                var retained = new DownloadOutputFile(testM3uPath, new JobList(), OutputFileKind.Index, true, readOnly: true);
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.IsLessThan(20_000_000L, allocated, "Ordinary index reads must not prepare group lookup state.");
                Assert.AreEqual(10_000, retained.GetPreviousRunData().Count);
                Assert.AreEqual(songs[^1].DownloadPath, retained.PreviousRunResult(songs[^1])?.DownloadPath);
                Assert.IsFalse(retained.PublishPending());
            }
            finally { File.Delete(testM3uPath + ".playlist"); }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PreparationSharesSiblingIndexDefaultsAndReevaluatesEachTraversal(bool subtree)
        {
            string directory = Path.ChangeExtension(testM3uPath, null);
            try
            {
                var songs = Enumerable.Range(0, 3)
                    .Select(i => new SongJob(new SongQuery { Artist = "Artist", Title = "Track " + i })).ToArray();
                var owner = new JobList("Tracks", songs);
                var unrelated = new SongJob(new SongQuery { Artist = "Other", Title = "Separate" });
                var root = new JobList("Root", [unrelated, owner]);
                var settings = new DownloadSettings();
                settings.Output.ParentDir = directory;
                settings.Skip.SkipExisting = false;
                Dictionary<Guid, JobContext> Prepare() => subtree
                    ? JobPreparer.PrepareSubtree(root, settings)
                    : JobPreparer.PrepareJobs(root, settings);

                var contexts = Prepare();
                Assert.IsTrue(songs.All(song => contexts[song.Id].IndexEditor is null));

                // A default on the last sibling applies to earlier siblings too.
                songs[^1].EnablesIndexByDefault = true;
                contexts = Prepare();
                var editor = contexts[songs[0].Id].IndexEditor;
                Assert.IsNotNull(editor);
                Assert.IsNull(contexts[unrelated.Id].IndexEditor);
                foreach (var song in songs)
                {
                    Assert.AreSame(editor, contexts[song.Id].IndexEditor);
                    song.SetDone(Path.Combine(directory, song.Query.Title + ".mp3"));
                }
                editor.Refresh();
                var retained = new DownloadOutputFile(editor.Path, owner, OutputFileKind.Index, true);
                foreach (var song in songs)
                {
                    retained.TryGetPreviousRunResult(song, out var previous);
                    Assert.AreEqual(song.DownloadPath, previous?.DownloadPath);
                }

                songs[^1].EnablesIndexByDefault = false;
                contexts = Prepare();
                Assert.IsTrue(songs.All(song => contexts[song.Id].IndexEditor is null));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
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
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);

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
            Assert.AreEqual(JobFailureReason.NoMatchingResults, prev3.FailureReason);
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
            songs[1].Fail(JobFailureReason.NoMatchingResults);
            // songs[2] stays Pending

            var (queue, _, _) = MakeSongQueue(songs);
            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            // Load back with a fresh editor
            var lookupSongs = songs.Select(s => new SongJob(new SongQuery { Artist = s.Query.Artist, Title = s.Query.Title })).ToList();
            var (queue2, _, _) = MakeSongQueue(lookupSongs);
            var editor2 = new DownloadOutputFile(testM3uPath, queue2, OutputFileKind.Index, true);

            // Verify downloaded track round-tripped
            editor2.TryGetPreviousRunResult(lookupSongs[0], out var prev1);
            Assert.IsNotNull(prev1);
            Assert.AreEqual(JobStateOld.Done, prev1.State);
            Assert.AreEqual("path/to/file1", prev1.DownloadPath);

            // Verify failed track round-tripped
            editor2.TryGetPreviousRunResult(lookupSongs[1], out var prev2);
            Assert.IsNotNull(prev2);
            Assert.AreEqual(JobStateOld.Failed, prev2.State);
            Assert.AreEqual(JobFailureReason.NoMatchingResults, prev2.FailureReason);

            // Pending track should not be in previous run data (state is Pending, it was skipped)
            editor2.TryGetPreviousRunResult(lookupSongs[2], out var prev3);
            Assert.IsNull(prev3);
        }

        [TestMethod]
        public void Index_FailedJobWithClearedPath_UpdatesExistingPathToEmpty()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            song.SetDone();
            song.DownloadPath = "path/to/file.mp3";

            var (queue, _, _) = MakeSongQueue([song]);
            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            JobOutcomeCommitter.Commit(song, JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, clearDownloadPath: true));
            editor.Refresh();

            var lookup = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var (queue2, _, _) = MakeSongQueue([lookup]);
            var editor2 = new DownloadOutputFile(testM3uPath, queue2, OutputFileKind.Index, true);

            editor2.TryGetPreviousRunResult(lookup, out var prev);
            Assert.IsNotNull(prev);
            Assert.AreEqual(JobStateOld.Failed, prev.State);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, prev.FailureReason);
            Assert.AreEqual("", prev.DownloadPath);
        }

        [TestMethod]
        public void Index_SerializesFilePathsWithForwardSlashes()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            song.SetDone();
            song.DownloadPath = Path.Combine(Path.GetDirectoryName(testM3uPath)!, "nested", "song.mp3");

            var (queue, _, _) = MakeSongQueue([song]);
            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

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
                queue.Add(j);

            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);

            // Update album states
            albumJobs[0].SetDone();
            albumJobs[0].DownloadPath = "download/path";
            albumJobs[1].Fail(JobFailureReason.NoMatchingResults);
            albumJobs[2].SetSkipped(JobSkipReason.Manual);

            editor.Refresh();

            // Read back with new editor using fresh AlbumJobs
            var lookupJobs = albumJobs.Select(j => new AlbumJob(new AlbumQuery { Artist = j.Query.Artist, Album = j.Query.Album })).ToList();
            var queue2 = new JobList();
            foreach (var j in lookupJobs)
                queue2.Add(j);
            var editor2 = new DownloadOutputFile(testM3uPath, queue2, OutputFileKind.Index, true);

            for (int i = 0; i < 2; i++)
            {
                var prev = editor2.PreviousRunResult((AlbumJob)lookupJobs[i]);
                Assert.IsNotNull(prev, $"Previous run result not found for {lookupJobs[i].Query.Artist} - {lookupJobs[i].Query.Album}");
                Assert.AreEqual(albumJobs[i].Query.Artist, prev.Artist);
                Assert.AreEqual(albumJobs[i].Query.Album, prev.Album);
                Assert.AreEqual(albumJobs[i].DownloadPath ?? "", prev.DownloadPath);

                // Verify prev is a separate object from the job
                string originalPath = albumJobs[i].DownloadPath ?? "";
                albumJobs[i].DownloadPath = "this should not change prev.DownloadPath";
                Assert.AreNotEqual(albumJobs[i].DownloadPath, prev.DownloadPath);
                albumJobs[i].DownloadPath = originalPath;
            }

            Assert.IsNull(editor2.PreviousRunResult((AlbumJob)lookupJobs[2]),
                "A manual interactive skip must not poison the index as already-exists.");
        }

        [TestMethod]
        public void Index_ManualSkippedSong_IsNotPersistedAsAlreadyExists()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            song.SetSkipped(JobSkipReason.Manual);

            var (queue, _, _) = MakeSongQueue([song]);
            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            var lookup = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var (queue2, _, _) = MakeSongQueue([lookup]);
            var editor2 = new DownloadOutputFile(testM3uPath, queue2, OutputFileKind.Index, true);

            Assert.IsFalse(editor2.TryGetPreviousRunResult(lookup, out _));
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
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            // Load back
            var lookupSongs = songs.Select(s => new SongJob(new SongQuery { Artist = s.Query.Artist, Title = s.Query.Title })).ToList();
            var (queue2, _, _) = MakeSongQueue(lookupSongs);
            var editor2 = new DownloadOutputFile(testM3uPath, queue2, OutputFileKind.Index, true);

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
            songs[0].Fail(JobFailureReason.NoMatchingResults);
            songs[1].SetDone();
            songs[1].DownloadPath = "p";

            var (queue, _, _) = MakeSongQueue(songs);
            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            // Reload
            var lookupSongs = songs.Select(s => new SongJob(new SongQuery { Artist = s.Query.Artist, Title = s.Query.Title })).ToList();
            var (queue2, _, _) = MakeSongQueue(lookupSongs);
            var editor2 = new DownloadOutputFile(testM3uPath, queue2, OutputFileKind.Index, true);

            Assert.IsTrue(editor2.TryGetFailureReason(lookupSongs[0], out var reason));
            Assert.AreEqual(JobFailureReason.NoMatchingResults, reason);

            Assert.IsFalse(editor2.TryGetFailureReason(lookupSongs[1], out _));
        }

        [TestMethod]
        public void Index_DoesNotIncludeAlbumChildFiles()
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

            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.IsTrue(lines.Any(l => l.Contains(",Artist,Album,,-1,1,")), "Index should contain the album entry.");
            Assert.IsFalse(lines.Any(l => l.Contains("Track.mp3")), "Index should not contain individual album child audio files.");
            Assert.IsFalse(lines.Any(l => l.Contains("Cover.jpg")), "Index should not contain non-audio files.");
        }

        [TestMethod]
        public void Index_PersistsStandaloneNonAudioFile()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Booklet" });
            song.ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(
                new Soulseek.SearchResponse("user", 1, true, 100, 0, []),
                TestHelpers.CreateSlFile(@"Documents\Artist - Booklet.pdf"));
            song.SetDone();
            song.DownloadPath = "Documents/Artist - Booklet.pdf";

            var (queue, _, _) = MakeSongQueue([song]);
            File.WriteAllText(testM3uPath, "");
            var editor = new DownloadOutputFile(testM3uPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            var lookup = new SongJob(new SongQuery { Artist = "Artist", Title = "Booklet" });
            var (lookupQueue, _, _) = MakeSongQueue([lookup]);
            var reloaded = new DownloadOutputFile(testM3uPath, lookupQueue, OutputFileKind.Index, true);

            Assert.IsTrue(reloaded.TryGetPreviousRunResult(lookup, out var previous));
            Assert.IsNotNull(previous);
            Assert.AreEqual(JobStateOld.Done, previous.State);
            Assert.AreEqual(song.DownloadPath, previous.DownloadPath);
        }
    }
}
