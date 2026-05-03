using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using System.Reflection;
using System.IO;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Tests.FileManagerTests
{
    [TestClass]
    public class GetSavePathTests
    {
        private static FileManager MakeManager(DownloadSettings? config = null, Job? job = null)
        {
            config ??= TestHelpers.CreateDefaultSettings().Download;
            job ??= new JobList();
            return new FileManager(job, config.Output, config.Extraction);
        }

        [TestMethod]
        public void GetSavePath_NormalTrack_ReturnsParentDirPlusFilename()
        {
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Output.ParentDir = "/music";
            var manager = MakeManager(config);

            string path = manager.GetSavePath("Music\\Artist\\Song.mp3");

            Assert.IsTrue(path.EndsWith("Song.mp3"));
            Assert.IsTrue(path.Contains("music"));
        }

        [TestMethod]
        public void GetSavePath_PreservesExtension()
        {
            var config = TestHelpers.CreateDefaultSettings().Download;
            var manager = MakeManager(config);

            string path = manager.GetSavePath("folder\\track.flac");

            Assert.IsTrue(path.EndsWith(".flac"));
        }

        [TestMethod]
        public void GetSavePath_AlbumTrack_WithRemoteBaseDir_PreservesRelativePath()
        {
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Output.ParentDir = "/music";
            var job = new AlbumJob(new AlbumQuery());
            var manager = new FileManager(job, config.Output, config.Extraction);

            manager.SetremoteBaseDir("Music\\Artist\\Album");

            string path = manager.GetSavePath("Music\\Artist\\Album\\SubDir\\track.mp3");

            Assert.IsTrue(path.Contains("SubDir") || path.Contains(Path.DirectorySeparatorChar + "SubDir" + Path.DirectorySeparatorChar));
        }

        [TestMethod]
        public void GetSavePathNoExt_NoExtension_OmitsExtension()
        {
            var config = TestHelpers.CreateDefaultSettings().Download;
            var manager = MakeManager(config);

            string path = manager.GetSavePathNoExt("folder\\track.mp3");

            Assert.IsFalse(path.EndsWith(".mp3"));
        }
    }

    [TestClass]
    public class GetFolderNameTests
    {
        private static string? InvokeGetFolderName(Soulseek.File? slfile, string? remoteBaseDir)
        {
            var method = typeof(FileManager).GetMethod("GetFolderName", BindingFlags.NonPublic | BindingFlags.Static);
            return (string?)method!.Invoke(null, new object?[] { slfile, remoteBaseDir });
        }

        [TestMethod]
        public void GetFolderName_BothNull_ReturnsEmpty()
        {
            var result = InvokeGetFolderName(null, null);
            Assert.AreEqual("", result);
        }

        [TestMethod]
        public void GetFolderName_OnlyRemoteBaseDir_ReturnsBasename()
        {
            var result = InvokeGetFolderName(null, "Music\\Artist\\Album");
            Assert.AreEqual("Album", result);
        }

        [TestMethod]
        public void GetFolderName_OnlySlFile_ReturnsParentDirName()
        {
            var file = TestHelpers.CreateSlFile("Music\\Artist\\Album\\track.mp3");
            var result = InvokeGetFolderName(file, null);
            Assert.AreEqual("Album", result);
        }

        [TestMethod]
        public void GetFolderName_BothSet_ReturnsRelativePath()
        {
            var file = TestHelpers.CreateSlFile("Music\\Artist\\Album\\SubDir\\track.mp3");
            var result = InvokeGetFolderName(file, "Music\\Artist\\Album");
            Assert.IsTrue(result!.Contains("Album"));
            Assert.IsTrue(result.Contains("SubDir"));
        }
    }

    [TestClass]
    public class TryGetCleanVarValueTests
    {
        private static FileManagerContext MakeCtx(
            string title = "",
            string artist = "",
            string album = "",
            Soulseek.File? slFile = null,
            string? downloadPath = null,
            DownloadSettings? config = null)
        {
            config ??= TestHelpers.CreateDefaultSettings().Download;
            var job = new JobList();
            var query = new SongQuery { Artist = artist, Title = title, Album = album };
            Soulseek.SearchResponse? response = slFile != null
                ? new Soulseek.SearchResponse("user", 1, true, 100, 0, new List<Soulseek.File> { slFile })
                : null;
            FileCandidate? candidate = slFile != null && response != null
                ? new FileCandidate(response, slFile)
                : null;
            return new FileManagerContext
            {
                Job          = job,
                Query        = query,
                Candidate    = candidate,
                DownloadPath = downloadPath,
            };
        }

        [TestMethod]
        public void TryGetCleanVarValue_KnownNonTagVar_ReturnsTrue()
        {
            var ctx = MakeCtx(title: "MyTitle", downloadPath: "/music/file.mp3");

            bool found = FileManager.TryGetCleanVarValue("stitle", ctx, () => null, " ", out string res);

            Assert.IsTrue(found);
            Assert.AreEqual("MyTitle", res);
        }

        [TestMethod]
        public void TryGetCleanVarValue_UnknownVar_ReturnsFalse()
        {
            var ctx = MakeCtx();

            bool found = FileManager.TryGetCleanVarValue("nonexistent", ctx, () => null, " ", out string res);

            Assert.IsFalse(found);
        }

        [TestMethod]
        public void TryGetCleanVarValue_SlskFilename_ReturnsFilenameWithoutExt()
        {
            var file = TestHelpers.CreateSlFile("Music\\Artist\\Album\\My Track.mp3");
            var ctx = MakeCtx(slFile: file);

            bool found = FileManager.TryGetCleanVarValue("slsk-filename", ctx, () => null, " ", out string res);

            Assert.IsTrue(found);
            Assert.AreEqual("My Track", res);
        }
    }

    [TestClass]
    public class HasTagVariablesTests
    {
        [TestMethod]
        public void HasTagVariables_ContainsArtist_ReturnsTrue()
        {
            Assert.IsTrue(FileManager.HasTagVariables("{artist}"));
        }

        [TestMethod]
        public void HasTagVariables_ContainsAlbum_ReturnsTrue()
        {
            Assert.IsTrue(FileManager.HasTagVariables("{albumartist}/{album}/{title}"));
        }

        [TestMethod]
        public void HasTagVariables_OnlyNonTagVars_ReturnsFalse()
        {
            Assert.IsFalse(FileManager.HasTagVariables("{slsk-filename}/{foldername}"));
        }

        [TestMethod]
        public void HasTagVariables_EmptyFormat_ReturnsFalse()
        {
            Assert.IsFalse(FileManager.HasTagVariables(""));
        }

        [TestMethod]
        public void HasTagVariables_PlainText_ReturnsFalse()
        {
            Assert.IsFalse(FileManager.HasTagVariables("just a plain string"));
        }
    }

    [TestClass]
    public class OrganizationTests
    {
        private string testRoot = "";
        private DownloadSettings config = null!;

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "slsk-org-tests-" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(testRoot);
            config = TestHelpers.CreateDefaultSettings().Download;
            config.Output.ParentDir = testRoot;
            config.Output.FailedAlbumPath = Path.Join(testRoot, "failed");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }

        [TestMethod]
        public void AlbumJob_DefaultOrganization_UsesRemoteFolderName()
        {
            // Setup
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            var manager = new FileManager(job, config.Output, config.Extraction);
            manager.SetremoteBaseDir(@"Artist1\Album1"); // slsk-style path

            // File paths
            string source = Path.Combine(testRoot, "temp_download.mp3");
            File.WriteAllText(source, "dummy");

            // Execute
            string target = manager.GetSavePath(source);

            // Verify
            // Should be {testRoot}/Album1/temp_download.mp3
            string expected = Path.Combine(testRoot, "Album1", "temp_download.mp3");
            Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(target));
        }

        [TestMethod]
        public void SongListJob_DefaultOrganization_UsesItemName()
        {
            // Setup
            var job = new JobList();
            job.ItemName = "MyPlaylist";
            var manager = new FileManager(job, config.Output, config.Extraction);

            // File paths
            string source = Path.Combine(testRoot, "temp_song.mp3");
            File.WriteAllText(source, "dummy");

            // Execute
            string target = manager.GetSavePath(source);

            // Verify
            // Should be {testRoot}/MyPlaylist/temp_song.mp3
            string expected = Path.Combine(testRoot, "MyPlaylist", "temp_song.mp3");
            Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(target));
        }

        [TestMethod]
        public void AlbumJob_NameFormat_MovesCoverIntelligently()
        {
            // Setup
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            config.Output.NameFormat = "OrgTest/{sartist}/{salbum}/{filename}";
            var manager = new FileManager(job, config.Output, config.Extraction);
            manager.SetremoteBaseDir(@"Artist1\Album1");

            // Create some "downloaded" files
            string audio1Base = Path.Combine(testRoot, "dl1.mp3");
            string audio2Base = Path.Combine(testRoot, "dl2.mp3");
            string coverBase = Path.Combine(testRoot, "cover.jpg");
            File.WriteAllText(audio1Base, "audio1");
            File.WriteAllText(audio2Base, "audio2");
            File.WriteAllText(coverBase, "jpg");

            var file1 = new SongJob(new SongQuery { Artist = "Artist1", Album = "Album1", Title = "Track1" })
            {
                ResolvedTarget = new FileCandidate(new Soulseek.SearchResponse("user", 0, false, 0, 0, null),
                                                   new Soulseek.File(0, @"Artist1\Album1\01. Track1.mp3", 0, "mp3")),
                DownloadPath = audio1Base,
            };
            file1.UpdateState(JobState.Done);

            var file2 = new SongJob(new SongQuery { Artist = "Artist1", Album = "Album1", Title = "Track2" })
            {
                ResolvedTarget = new FileCandidate(new Soulseek.SearchResponse("user", 0, false, 0, 0, null),
                                                   new Soulseek.File(0, @"Artist1\Album1\02. Track2.mp3", 0, "mp3")),
                DownloadPath = audio2Base,
            };
            file2.UpdateState(JobState.Done);

            var coverFile = new SongJob(new SongQuery())
            {
                ResolvedTarget = new FileCandidate(new Soulseek.SearchResponse("user", 0, false, 0, 0, null),
                                                   new Soulseek.File(0, @"Artist1\Album1\Cover.jpg", 0, "jpg")),
                DownloadPath = coverBase,
            };
            coverFile.UpdateState(JobState.Done);

            var allFiles = new List<SongJob> { file1, file2, coverFile };

            // Execute
            manager.OrganizeAlbum(job, allFiles, null, remainingOnly: false);

            // Verify audio files are moved to NF location
            // NF is "OrgTest/{sartist}/{salbum}/{filename}". {filename} for file1 is "01. Track1"
            string expectedAudio1 = Path.Combine(testRoot, "OrgTest", "Artist1", "Album1", "01. Track1.mp3");
            string expectedAudio2 = Path.Combine(testRoot, "OrgTest", "Artist1", "Album1", "02. Track2.mp3");
            Assert.IsTrue(File.Exists(expectedAudio1), "Audio 1 not found at target");
            Assert.IsTrue(File.Exists(expectedAudio2), "Audio 2 not found at target");

            // Verify cover is moved specifically to the common parent of the audio files
            string expectedCover = Path.Combine(testRoot, "OrgTest", "Artist1", "Album1", "cover.jpg");
            Assert.IsTrue(File.Exists(expectedCover), $"Cover not found at {expectedCover}");
        }

        [TestMethod]
        public void AlbumAggregate_ArtistOnly_CreatesSubfoldersForAlbums()
        {
            // main use case for -ag: find all albums by an artist.
            var aggJob = new AlbumAggregateJob(new AlbumQuery { Artist = "Artist1" });
            aggJob.ItemName = "Artist1"; // Set by JobList
            
            // Simulating an AlbumJob spawned from this aggregate (e.g. for 'Album1')
            var albumJob = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            albumJob.ItemName = aggJob.ItemName; // Inherited from AlbumAggregateJob
            
            var manager = new FileManager(albumJob, config.Output, config.Extraction);
            manager.SetremoteBaseDir(@"User1\Artist1\Album1"); // Remote path
            
            string source = Path.Combine(testRoot, "track.mp3");
            File.WriteAllText(source, "data");
            
            // Should be {testRoot}/Artist1/Album1/track.mp3
            string target = manager.GetSavePath(source);
            string expected = Path.Combine(testRoot, "Artist1", "Album1", "track.mp3");
            Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(target));
        }

        [TestMethod]
        public void SongAggregate_ArtistOnly_GroupsIntoArtistFolder()
        {
            // main use case for -g: find all songs by an artist.
            var job = new AggregateJob(new SongQuery { Artist = "Artist1" });
            job.ItemName = "Artist1"; // Set by JobList
            
            var manager = new FileManager(job, config.Output, config.Extraction);
            
            string source = Path.Combine(testRoot, "temp.mp3");
            File.WriteAllText(source, "data");
            
            // Should be {testRoot}/Artist1/temp.mp3
            string target = manager.GetSavePath(source);
            string expected = Path.Combine(testRoot, "Artist1", "temp.mp3");
            Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(target));
        }
    }
}
