using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.TrackSkipperTests
{
    internal static class AudioTestFixtures
    {
        public static int ReadAudioBitrate(string path)
        {
            using var file = TagLib.File.Create(path);
            return file.Properties.AudioBitrate;
        }

        public static void WriteSilentWav(string path, int sampleRate = 44_100, short bitsPerSample = 16, short channels = 2)
        {
            int dataSize = sampleRate * channels * bitsPerSample / 8;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);

            File.WriteAllBytes(path, stream.ToArray());
        }
    }

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
        private string _tempDir = "";
        private string _tempPath = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"Sockseek_skip_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);
            _tempPath = Path.Combine(_tempDir, "_index.csv");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
            else if (File.Exists(_tempPath))
                File.Delete(_tempPath);
        }

        private DownloadOutputFile CreateEditorWithSong(SongJob song)
        {
            var slj = new JobList();
            slj.Add(song);
            var queue = new JobList();
            queue.Add(slj);
            File.WriteAllText(_tempPath, "");
            var editor = new DownloadOutputFile(_tempPath, queue, OutputFileKind.Index, true);
            editor.Refresh();

            // Load back via fresh editor so previousRunData is populated
            var slj2 = new JobList();
            slj2.Add(new SongJob(new SongQuery { Artist = song.Query.Artist, Title = song.Query.Title }));
            var queue2 = new JobList();
            queue2.Add(slj2);
            return new DownloadOutputFile(_tempPath, queue2, OutputFileKind.Index, true);
        }

        private DownloadOutputFile CreateEditorWithDoneSong(string artist, string title, string downloadPath)
        {
            var original = new SongJob(new SongQuery { Artist = artist, Title = title });
            original.SetDone();
            original.DownloadPath = downloadPath;
            return CreateEditorWithSong(original);
        }

        private DownloadOutputFile CreateEditorWithDoneAlbum(string artist, string album, string downloadPath)
        {
            var original = new AlbumJob(new AlbumQuery { Artist = artist, Album = album });
            original.SetDone(downloadPath);
            var queue = new JobList("albums", [original]);
            File.WriteAllText(_tempPath, "");
            var editor = new DownloadOutputFile(_tempPath, queue, OutputFileKind.Index, true);
            editor.NotifyJobDownloadPath(original.Id, downloadPath);
            editor.Refresh();

            var query = new AlbumJob(new AlbumQuery { Artist = artist, Album = album });
            var queue2 = new JobList("albums", [query]);
            return new DownloadOutputFile(_tempPath, queue2, OutputFileKind.Index, true);
        }

        private static TrackSkipperContext CreateContext(
            DownloadOutputFile editor,
            SearchSettings search,
            bool skipCheckCond = false,
            bool skipCheckPrefCond = false)
        {
            return TrackSkipperContext.From(
                new JobContext { IndexEditor = editor },
                new SkipSettings
                {
                    SkipCheckCond = skipCheckCond,
                    SkipCheckPrefCond = skipCheckPrefCond,
                },
                search);
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
            original.SetDone();
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
            original.Fail(JobFailureReason.NoMatchingResults);
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
            queue.Add(slj);
            File.WriteAllText(_tempPath, "");
            var editor = new DownloadOutputFile(_tempPath, queue, OutputFileKind.Index, true);

            var skipper = new IndexSkipper();
            var query = new SongJob(new SongQuery { Artist = "Nobody", Title = "Nothing" });
            var context = new TrackSkipperContext { indexEditor = editor, checkFileExists = false };

            bool result = skipper.SongExists(query, context, out _);
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckCond_MissingIndexedFile_SkipsWithoutFlagButNotWithFlag()
        {
            string missingPath = Path.Combine(_tempDir, "missing.mp3");
            var editor = CreateEditorWithDoneSong("Artist", "Title", missingPath);
            var query = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var search = new SearchSettings();

            var uncheckedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: false);
            Assert.IsTrue(
                uncheckedSkipper.SongExists(query, CreateContext(editor, search), out _),
                "Without skip-check-cond, the index entry alone should skip the song even if the file is gone.");

            var checkedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                checkedSkipper.SongExists(query, CreateContext(editor, search, skipCheckCond: true), out _),
                "With skip-check-cond, a stale index entry must not skip the song when its file no longer exists.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckCond_FileFailingRequiredConditions_SkipsWithoutFlagButNotWithFlag()
        {
            string mp3Path = Path.Combine(_tempDir, "Artist - Title.mp3");
            File.WriteAllBytes(mp3Path, TestHelpers.EmptyMp3Bytes);
            var editor = CreateEditorWithDoneSong("Artist", "Title", mp3Path);
            var query = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["flac"] },
                PreferredCond = new FileConditions(),
            };

            var uncheckedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: false);
            Assert.IsTrue(
                uncheckedSkipper.SongExists(query, CreateContext(editor, search), out _),
                "Without skip-check-cond, the existing index entry should skip regardless of current required conditions.");

            var checkedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                checkedSkipper.SongExists(query, CreateContext(editor, search, skipCheckCond: true), out _),
                "With skip-check-cond, an indexed MP3 must not skip when the required condition only accepts FLAC.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckCond_FileFailingRequiredBitrateCondition_SkipsWithoutFlagButNotWithFlag()
        {
            string mp3Path = Path.Combine(_tempDir, "Artist - Title.mp3");
            File.WriteAllBytes(mp3Path, TestHelpers.EmptyMp3Bytes);
            int actualBitrate = AudioTestFixtures.ReadAudioBitrate(mp3Path);
            Assert.IsTrue(actualBitrate > 0, "The MP3 fixture must expose a bitrate for this test to exercise metadata conditions.");

            var editor = CreateEditorWithDoneSong("Artist", "Title", mp3Path);
            var query = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { MinBitrate = actualBitrate + 1 },
                PreferredCond = new FileConditions(),
            };

            var uncheckedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: false);
            Assert.IsTrue(
                uncheckedSkipper.SongExists(query, CreateContext(editor, search), out _),
                "Without skip-check-cond, the existing index entry should skip regardless of current bitrate conditions.");

            var checkedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                checkedSkipper.SongExists(query, CreateContext(editor, search, skipCheckCond: true), out _),
                "With skip-check-cond, an indexed file must not skip when it fails the required bitrate condition.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckPrefCond_FileFailingPreferredConditions_SkipsWithRequiredOnlyButNotWithPreferred()
        {
            string mp3Path = Path.Combine(_tempDir, "Artist - Title.mp3");
            File.WriteAllBytes(mp3Path, TestHelpers.EmptyMp3Bytes);
            var editor = CreateEditorWithDoneSong("Artist", "Title", mp3Path);
            var query = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["mp3"] },
                PreferredCond = new FileConditions { Formats = ["flac"] },
            };

            var requiredOnlySkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsTrue(
                requiredOnlySkipper.SongExists(query, CreateContext(editor, search, skipCheckCond: true), out _),
                "skip-check-cond should still skip when the existing file satisfies the required conditions.");

            var preferredSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                preferredSkipper.SongExists(query, CreateContext(editor, search, skipCheckPrefCond: true), out _),
                "skip-check-pref-cond should keep searching when the existing file fails the preferred conditions.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckPrefCond_FileFailingPreferredBitrateCondition_SkipsWithRequiredOnlyButNotWithPreferred()
        {
            string mp3Path = Path.Combine(_tempDir, "Artist - Title.mp3");
            File.WriteAllBytes(mp3Path, TestHelpers.EmptyMp3Bytes);
            int actualBitrate = AudioTestFixtures.ReadAudioBitrate(mp3Path);
            Assert.IsTrue(actualBitrate > 0, "The MP3 fixture must expose a bitrate for this test to exercise metadata conditions.");

            var editor = CreateEditorWithDoneSong("Artist", "Title", mp3Path);
            var query = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { MinBitrate = actualBitrate },
                PreferredCond = new FileConditions { MinBitrate = actualBitrate + 1 },
            };

            var requiredOnlySkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsTrue(
                requiredOnlySkipper.SongExists(query, CreateContext(editor, search, skipCheckCond: true), out _),
                "skip-check-cond should still skip when the existing file satisfies the required bitrate condition.");

            var preferredSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                preferredSkipper.SongExists(query, CreateContext(editor, search, skipCheckPrefCond: true), out _),
                "skip-check-pref-cond should keep searching when the existing file fails the preferred bitrate condition.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckCond_MissingIndexedAlbumDirectory_SkipsWithoutFlagButNotWithFlag()
        {
            string missingAlbumDir = Path.Combine(_tempDir, "Missing Album");
            var editor = CreateEditorWithDoneAlbum("Artist", "Album", missingAlbumDir);
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            var search = new SearchSettings();

            var uncheckedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: false);
            Assert.IsTrue(
                uncheckedSkipper.AlbumExists(album, CreateContext(editor, search), out _),
                "Without skip-check-cond, the index entry alone should skip the album even if the folder is gone.");

            var checkedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                checkedSkipper.AlbumExists(album, CreateContext(editor, search, skipCheckCond: true), out _),
                "With skip-check-cond, a stale album index entry must not skip when its folder no longer exists.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckCond_AlbumFailingFormatCondition_SkipsWithoutFlagButNotWithFlag()
        {
            string albumDir = Path.Combine(_tempDir, "Artist", "Album");
            Directory.CreateDirectory(albumDir);
            File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Track.mp3"), TestHelpers.EmptyMp3Bytes);

            var editor = CreateEditorWithDoneAlbum("Artist", "Album", albumDir);
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["wav"] },
                PreferredCond = new FileConditions(),
            };

            var uncheckedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: false);
            Assert.IsTrue(
                uncheckedSkipper.AlbumExists(album, CreateContext(editor, search), out _),
                "Without skip-check-cond, the existing album index entry should skip regardless of current format conditions.");

            var checkedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                checkedSkipper.AlbumExists(album, CreateContext(editor, search, skipCheckCond: true), out _),
                "With skip-check-cond, an album with no matching audio files must not skip when the required format is WAV.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckCond_MixedAlbumFormatAndBitrateUsesCoverageForSkipping()
        {
            string albumDir = Path.Combine(_tempDir, "Artist", "Album");
            Directory.CreateDirectory(albumDir);
            string mp3Path = Path.Combine(albumDir, "01. Artist - Low.mp3");
            string wavPath = Path.Combine(albumDir, "02. Artist - High.wav");
            File.WriteAllBytes(mp3Path, TestHelpers.EmptyMp3Bytes);
            AudioTestFixtures.WriteSilentWav(wavPath);
            int mp3Bitrate = AudioTestFixtures.ReadAudioBitrate(mp3Path);
            int wavBitrate = AudioTestFixtures.ReadAudioBitrate(wavPath);
            Assert.IsTrue(wavBitrate > mp3Bitrate, "The WAV fixture must expose a higher bitrate than the MP3 fixture.");

            var editor = CreateEditorWithDoneAlbum("Artist", "Album", albumDir);
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            var nonStrictSearch = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["wav"], MinBitrate = wavBitrate },
                PreferredCond = new FileConditions(),
            };
            var strictSearch = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["wav"], MinBitrate = wavBitrate },
                PreferredCond = new FileConditions(),
                StrictAlbumQuality = true,
            };

            var checkedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsTrue(
                checkedSkipper.AlbumExists(album, CreateContext(editor, nonStrictSearch, skipCheckCond: true), out _),
                "Non-strict album quality should skip when coverage finds at least one file satisfying the active format and bitrate conditions.");

            Assert.IsFalse(
                checkedSkipper.AlbumExists(album, CreateContext(editor, strictSearch, skipCheckCond: true), out _),
                "Strict album quality should not skip unless every audio file satisfies the active format and bitrate conditions.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckCond_AlbumMissingRequiredTrackTitle_SkipsWithoutFlagButNotWithFlag()
        {
            string albumDir = Path.Combine(_tempDir, "Artist", "Album");
            Directory.CreateDirectory(albumDir);
            File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Track One.mp3"), TestHelpers.EmptyMp3Bytes);

            var editor = CreateEditorWithDoneAlbum("Artist", "Album", albumDir);
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions { RequiredTrackTitles = ["Track Two"] },
            };

            var uncheckedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: false);
            Assert.IsTrue(
                uncheckedSkipper.AlbumExists(album, CreateContext(editor, search), out _),
                "Without skip-check-cond, the existing album index entry should skip without inspecting folder title coverage.");

            var checkedSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                checkedSkipper.AlbumExists(album, CreateContext(editor, search, skipCheckCond: true), out _),
                "With skip-check-cond, an indexed album must not skip when required source tracks are missing from the folder.");
        }

        [TestMethod]
        public void IndexSkipper_SkipCheckPrefCond_AlbumMissingPreferredRequiredTrackTitle_SkipsWithRequiredOnlyButNotWithPreferred()
        {
            string albumDir = Path.Combine(_tempDir, "Artist", "Album");
            Directory.CreateDirectory(albumDir);
            File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Track One.mp3"), TestHelpers.EmptyMp3Bytes);

            var editor = CreateEditorWithDoneAlbum("Artist", "Album", albumDir);
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions { RequiredTrackTitles = ["Track One"] },
                PreferredFolderCond = new FolderConditions { RequiredTrackTitles = ["Track Two"] },
            };

            var requiredOnlySkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsTrue(
                requiredOnlySkipper.AlbumExists(album, CreateContext(editor, search, skipCheckCond: true), out _),
                "skip-check-cond should still skip when the indexed album satisfies required folder title coverage.");

            var preferredSkipper = TrackSkipperRegistry.GetSkipper(SkipMode.Index, _tempDir, useConditions: true);
            Assert.IsFalse(
                preferredSkipper.AlbumExists(album, CreateContext(editor, search, skipCheckPrefCond: true), out _),
                "skip-check-pref-cond should keep searching when the indexed album fails preferred folder title coverage.");
        }

    }

    [TestClass]
    public class NameSkipperTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"Sockseek_name_skip_{Guid.NewGuid()}");
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
        public void NameSkipper_MatchingPdf_ReturnsTrue()
        {
            string pdfPath = Path.Combine(_tempDir, "Cool Artist - Great Booklet.pdf");
            File.WriteAllText(pdfPath, "pdf payload");

            var skipper = new NameSkipper(_tempDir);
            skipper.BuildIndex();

            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Booklet" });
            Assert.IsTrue(skipper.SongExists(song, new TrackSkipperContext(), out string? foundPath));
            Assert.AreEqual(pdfPath, foundPath);
        }

        [TestMethod]
        public void NameConditionalSkipper_MatchingPdfHonorsFormatCondition()
        {
            string pdfPath = Path.Combine(_tempDir, "Cool Artist - Great Booklet.pdf");
            File.WriteAllText(pdfPath, "pdf payload");
            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Booklet" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["pdf"] },
                PreferredCond = new FileConditions(),
            };

            var skipper = new NameConditionalSkipper(_tempDir);
            skipper.BuildIndex();
            var context = TrackSkipperContext.From(
                new JobContext(),
                new SkipSettings { SkipCheckCond = true },
                search);

            Assert.IsTrue(skipper.SongExists(song, context, out string? foundPath));
            Assert.AreEqual(pdfPath, foundPath);

            search.NecessaryCond.Formats = ["xyz"];
            context = TrackSkipperContext.From(
                new JobContext(),
                new SkipSettings { SkipCheckCond = true },
                search);
            Assert.IsFalse(skipper.SongExists(song, context, out _));
        }

        [TestMethod]
        public void NameConditionalSkipper_PdfFailsRequiredAudioPropertyWhenMissingPropertiesAreRejected()
        {
            File.WriteAllText(Path.Combine(_tempDir, "Cool Artist - Great Booklet.pdf"), "pdf payload");
            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Booklet" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions
                {
                    Formats = ["pdf"],
                    MinBitrate = 128,
                    AcceptMissingProps = false,
                },
                PreferredCond = new FileConditions(),
            };

            var skipper = new NameConditionalSkipper(_tempDir);
            skipper.BuildIndex();
            var context = TrackSkipperContext.From(
                new JobContext(),
                new SkipSettings { SkipCheckCond = true },
                search);

            Assert.IsFalse(skipper.SongExists(song, context, out _));
        }

        [TestMethod]
        public void NameSkipper_IgnoresSockseekStagingFiles()
        {
            var staging = Path.Combine(_tempDir, ".sockseek-staging", "job");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "Cool Artist - Great Booklet.pdf"), "staged payload");

            var skipper = new NameSkipper(_tempDir);
            skipper.BuildIndex();

            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Booklet" });
            Assert.IsFalse(skipper.SongExists(song, new TrackSkipperContext(), out _));
        }

        [TestMethod]
        public void NameSkipper_SkipCheckCond_FileFailingRequiredBitrateCondition_SkipsWithoutFlagButNotWithFlag()
        {
            string mp3Path = Path.Combine(_tempDir, "Cool Artist - Great Song.mp3");
            File.WriteAllBytes(mp3Path, TestHelpers.EmptyMp3Bytes);
            int actualBitrate = AudioTestFixtures.ReadAudioBitrate(mp3Path);
            Assert.IsTrue(actualBitrate > 0, "The MP3 fixture must expose a bitrate for this test to exercise metadata conditions.");

            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Song" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { MinBitrate = actualBitrate + 1 },
                PreferredCond = new FileConditions(),
            };

            var uncheckedSkipper = new NameSkipper(_tempDir);
            uncheckedSkipper.BuildIndex();
            Assert.IsTrue(
                uncheckedSkipper.SongExists(song, new TrackSkipperContext { checkFileExists = false }, out _),
                "Without skip-check-cond, name-mode skipping should ignore the current bitrate condition.");

            var checkedSkipper = new NameConditionalSkipper(_tempDir);
            checkedSkipper.BuildIndex();
            var checkedContext = TrackSkipperContext.From(
                new JobContext(),
                new SkipSettings { SkipCheckCond = true },
                search);
            Assert.IsFalse(
                checkedSkipper.SongExists(song, checkedContext, out _),
                "With skip-check-cond, name-mode skipping must not skip when the matching local file fails the bitrate condition.");
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
        public void NameSkipper_AlbumQueryArtistAndAlbum_ReturnsTrue()
        {
            var albumDir = Path.Combine(_tempDir, "Library Artist", "Library Album");
            Directory.CreateDirectory(albumDir);
            File.WriteAllBytes(Path.Combine(albumDir, "01. Library Artist - First Track.mp3"), TestHelpers.EmptyMp3Bytes);

            var skipper = new NameSkipper(_tempDir);
            skipper.BuildIndex();

            var album = new AlbumJob(new AlbumQuery { Artist = "Library Artist", Album = "Library Album" });
            var context = new TrackSkipperContext { checkFileExists = false };

            bool result = skipper.AlbumExists(album, context, out string? foundPath);
            Assert.IsTrue(result);
            Assert.AreEqual(albumDir, foundPath);
        }

        [TestMethod]
        public void NameSkipper_AlbumQueryAlbumOnly_ReturnsTrue()
        {
            var albumDir = Path.Combine(_tempDir, "Library Artist", "Library Album");
            Directory.CreateDirectory(albumDir);
            File.WriteAllBytes(Path.Combine(albumDir, "01. Library Artist - First Track.mp3"), TestHelpers.EmptyMp3Bytes);

            var skipper = new NameSkipper(_tempDir);
            skipper.BuildIndex();

            var album = new AlbumJob(new AlbumQuery { Album = "Library Album" });
            var context = new TrackSkipperContext { checkFileExists = false };

            bool result = skipper.AlbumExists(album, context, out string? foundPath);
            Assert.IsTrue(result);
            Assert.AreEqual(albumDir, foundPath);
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
    public class TagSkipperTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"Sockseek_tag_skip_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public void TagSkipper_SkipCheckCond_FileFailingRequiredBitrateCondition_SkipsWithoutFlagButNotWithFlag()
        {
            string mp3Path = Path.Combine(_tempDir, "tagged.mp3");
            File.WriteAllBytes(mp3Path, TestHelpers.EmptyMp3Bytes);
            using (var tagFile = TagLib.File.Create(mp3Path))
            {
                tagFile.Tag.Performers = ["Cool Artist"];
                tagFile.Tag.Title = "Great Song";
                tagFile.Save();
            }

            int actualBitrate = AudioTestFixtures.ReadAudioBitrate(mp3Path);
            Assert.IsTrue(actualBitrate > 0, "The MP3 fixture must expose a bitrate for this test to exercise metadata conditions.");

            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Song" });
            var search = new SearchSettings
            {
                NecessaryCond = new FileConditions { MinBitrate = actualBitrate + 1 },
                PreferredCond = new FileConditions(),
            };

            var uncheckedSkipper = new TagSkipper(_tempDir);
            uncheckedSkipper.BuildIndex();
            Assert.IsTrue(
                uncheckedSkipper.SongExists(song, new TrackSkipperContext { checkFileExists = false }, out _),
                "Without skip-check-cond, tag-mode skipping should ignore the current bitrate condition.");

            var checkedSkipper = new TagConditionalSkipper(_tempDir);
            checkedSkipper.BuildIndex();
            var checkedContext = TrackSkipperContext.From(
                new JobContext(),
                new SkipSettings { SkipCheckCond = true },
                search);
            Assert.IsFalse(
                checkedSkipper.SongExists(song, checkedContext, out _),
                "With skip-check-cond, tag-mode skipping must not skip when the matching local file fails the bitrate condition.");
        }

        [TestMethod]
        public void TagSkipper_UnreadablePdfIsIgnoredWithoutCrashing()
        {
            File.WriteAllText(Path.Combine(_tempDir, "Cool Artist - Great Booklet.pdf"), "not a TagLib file");

            var skipper = new TagSkipper(_tempDir);
            skipper.BuildIndex();

            var song = new SongJob(new SongQuery { Artist = "Cool Artist", Title = "Great Booklet" });
            Assert.IsFalse(skipper.SongExists(song, new TrackSkipperContext(), out _));
            Assert.IsTrue(skipper.IndexIsBuilt);
        }
    }

    [TestClass]
    public class LocalAlbumDirectorySatisfiesTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"Sockseek_count_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public void NoConstraints_ReturnsTrue()
        {
            Assert.IsTrue(ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(new FolderConditions(), _tempDir));
        }

        [TestMethod]
        public void MinMet_ReturnsTrue()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "a.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(_tempDir, "b.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsTrue(ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(
                new FolderConditions { MinTrackCount = 2 },
                _tempDir));
        }

        [TestMethod]
        public void MinNotMet_ReturnsFalse()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "a.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsFalse(ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(
                new FolderConditions { MinTrackCount = 3 },
                _tempDir));
        }

        [TestMethod]
        public void MaxExceeded_ReturnsFalse()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "a.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(_tempDir, "b.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(_tempDir, "c.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsFalse(ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(
                new FolderConditions { MaxTrackCount = 2 },
                _tempDir));
        }

        [TestMethod]
        public void RequiredTrackTitlePresent_ReturnsTrue()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "01. Artist - Track One.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsTrue(ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(
                new FolderConditions { RequiredTrackTitles = ["Track One"] },
                _tempDir));
        }

        [TestMethod]
        public void RequiredTrackTitleMissing_ReturnsFalse()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "01. Artist - Track One.mp3"), TestHelpers.EmptyMp3Bytes);

            Assert.IsFalse(ConditionSatisfactionPolicy.LocalAlbumDirectorySatisfies(
                new FolderConditions { RequiredTrackTitles = ["Track Two"] },
                _tempDir));
        }
    }
}
