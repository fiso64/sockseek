using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Services;

namespace Tests.TrackSkipperTests
{
    [TestClass]
    public class TrackSkipperRegistryTests
    {
        [TestMethod]
        public void GetSkipper_NameMode_NoConditions_ReturnsNameSkipper()
        {
            var skipper = TrackSkipperRegistry.GetSkipper(SkipMode.Name, "/tmp", false);
            Assert.IsInstanceOfType(skipper, typeof(NameSkipper));
        }

        [TestMethod]
        public void GetSkipper_NameMode_WithConditions_ReturnsNameConditionalSkipper()
        {
            var skipper = TrackSkipperRegistry.GetSkipper(SkipMode.Name, "/tmp", true);
            Assert.IsInstanceOfType(skipper, typeof(NameConditionalSkipper));
        }

        [TestMethod]
        public void GetSkipper_TagMode_NoConditions_ReturnsTagSkipper()
        {
            var skipper = TrackSkipperRegistry.GetSkipper(SkipMode.Tag, "/tmp", false);
            Assert.IsInstanceOfType(skipper, typeof(TagSkipper));
        }

        [TestMethod]
        public void GetSkipper_TagMode_WithConditions_ReturnsTagConditionalSkipper()
        {
            var skipper = TrackSkipperRegistry.GetSkipper(SkipMode.Tag, "/tmp", true);
            Assert.IsInstanceOfType(skipper, typeof(TagConditionalSkipper));
        }

        [TestMethod]
        public void GetSkipper_IndexMode_NoConditions_ReturnsIndexSkipper()
        {
            var skipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, "/tmp", false);
            Assert.IsInstanceOfType(skipper, typeof(IndexSkipper));
        }

        [TestMethod]
        public void GetSkipper_IndexMode_WithConditions_ReturnsIndexConditionalSkipper()
        {
            var skipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, "/tmp", true);
            Assert.IsInstanceOfType(skipper, typeof(IndexConditionalSkipper));
        }
    }

    [TestClass]
    public class IndexSkipperTests
    {
        private string _tempPath = "";

        [TestInitialize]
        public void Setup()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), $"sldl_skip_test_{Guid.NewGuid()}.m3u8");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(_tempPath)) File.Delete(_tempPath);
        }

        private M3uEditor CreateEditorWithSong(SongJob song)
        {
            var slj = new JobList();
            slj.Jobs.Add(song);
            var queue = new JobList();
            queue.Jobs.Add(slj);
            File.WriteAllText(_tempPath, "");
            var editor = new M3uEditor(_tempPath, queue, M3uOption.Index, true);
            editor.Update();

            // Load back via fresh editor so previousRunData is populated
            var slj2 = new JobList();
            slj2.Jobs.Add(new SongJob(new SongQuery { Artist = song.Query.Artist, Title = song.Query.Title }));
            var queue2 = new JobList();
            queue2.Jobs.Add(slj2);
            return new M3uEditor(_tempPath, queue2, M3uOption.Index, true);
        }

        [TestMethod]
        public void IndexSkipper_IndexIsBuilt_True()
        {
            var skipper = new IndexSkipper();
            Assert.IsTrue(skipper.IndexIsBuilt);
        }

        [TestMethod]
        public void IndexSkipper_DownloadedTrack_ReturnsTrue()
        {
            var original = new SongJob(new SongQuery { Artist = "Artist1", Title = "Title1" });
            original.UpdateState(JobState.Done);
            original.DownloadPath = "fake/path/file.mp3";
            var editor = CreateEditorWithSong(original);

            var skipper = new IndexSkipper();
            var query = new SongJob(new SongQuery { Artist = "Artist1", Title = "Title1" });
            var context = new TrackSkipperContext { indexEditor = editor, checkFileExists = false };

            bool result = skipper.SongExists(query, context, out _);
            Assert.IsTrue(result);
        }

        [TestMethod]
        public void IndexSkipper_FailedTrack_ReturnsFalse()
        {
            var original = new SongJob(new SongQuery { Artist = "Artist2", Title = "Title2" });
            original.Fail(FailureReason.NoSuitableFileFound);
            var editor = CreateEditorWithSong(original);

            var skipper = new IndexSkipper();
            var query = new SongJob(new SongQuery { Artist = "Artist2", Title = "Title2" });
            var context = new TrackSkipperContext { indexEditor = editor, checkFileExists = false };

            bool result = skipper.SongExists(query, context, out _);
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void IndexSkipper_UnknownTrack_ReturnsFalse()
        {
            var slj = new JobList();
            var queue = new JobList();
            queue.Jobs.Add(slj);
            File.WriteAllText(_tempPath, "");
            var editor = new M3uEditor(_tempPath, queue, M3uOption.Index, true);

            var skipper = new IndexSkipper();
            var query = new SongJob(new SongQuery { Artist = "Nobody", Title = "Nothing" });
            var context = new TrackSkipperContext { indexEditor = editor, checkFileExists = false };

            bool result = skipper.SongExists(query, context, out _);
            Assert.IsFalse(result);
        }

    }

    [TestClass]
    public class NameSkipperTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"sldl_name_skip_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public void NameSkipper_MatchingFile_ReturnsTrue()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "Cool Artist - Great Song.mp3"), TestHelpers.EmptyMp3Bytes);

            var skipper = new NameSkipper(_tempDir);
            skipper.BuildIndex();

            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Song" });
            var context = new TrackSkipperContext { checkFileExists = false };

            bool result = skipper.SongExists(song, context, out _);
            Assert.IsTrue(result);
        }

        [TestMethod]
        public void NameSkipper_NoMatchingFile_ReturnsFalse()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "Other Artist - Other Song.mp3"), TestHelpers.EmptyMp3Bytes);

            var skipper = new NameSkipper(_tempDir);
            skipper.BuildIndex();

            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Song" });
            var context = new TrackSkipperContext { checkFileExists = false };

            bool result = skipper.SongExists(song, context, out _);
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void NameSkipper_EmptyDirectory_ReturnsFalse()
        {
            var skipper = new NameSkipper(_tempDir);
            skipper.BuildIndex();

            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var context = new TrackSkipperContext { checkFileExists = false };

            bool result = skipper.SongExists(song, context, out _);
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void NameSkipper_NonExistentDirectory_IndexIsBuilt()
        {
            var skipper = new NameSkipper("/definitely/does/not/exist");
            skipper.BuildIndex();
            Assert.IsTrue(skipper.IndexIsBuilt);
        }
    }

    [TestClass]
    public class DirectoryHasGoodCountTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"sldl_count_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public void DirectoryHasGoodCount_NoConstraints_ReturnsTrue()
        {
            Assert.IsTrue(FileBasedSkipper<object>.DirectoryHasGoodCount(_tempDir));
        }

        [TestMethod]
        public void DirectoryHasGoodCount_MinMet_ReturnsTrue()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "a.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(_tempDir, "b.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsTrue(FileBasedSkipper<object>.DirectoryHasGoodCount(_tempDir, min: 2));
        }

        [TestMethod]
        public void DirectoryHasGoodCount_MinNotMet_ReturnsFalse()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "a.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsFalse(FileBasedSkipper<object>.DirectoryHasGoodCount(_tempDir, min: 3));
        }

        [TestMethod]
        public void DirectoryHasGoodCount_MaxExceeded_ReturnsFalse()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "a.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(_tempDir, "b.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(_tempDir, "c.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsFalse(FileBasedSkipper<object>.DirectoryHasGoodCount(_tempDir, max: 2));
        }
    }
}
