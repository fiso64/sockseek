using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core;
using System.Reflection;
using System.IO;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Diagnostics;

namespace Tests.FileManagerTests
{
    [TestClass]
    public class GetSavePathTests
    {
        private static FileManager MakeManager(DownloadSettings? config = null, Job? job = null)
        {
            config ??= TestHelpers.CreateDefaultSettings().Download;
            job ??= new JobList();
            return new FileManager(
                job,
                config.Output,
                config.Extraction,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileManager>.Instance);
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
            var manager = new FileManager(
                job,
                config.Output,
                config.Extraction,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileManager>.Instance);

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
            return (string?)method!.Invoke(null, new object?[] { slfile?.Filename, remoteBaseDir });
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
                ? SoulseekSearchAdapter.ToFileCandidate(response, slFile)
                : null;
            return new FileManagerContext
            {
                Job = job,
                Query = query,
                Candidate = candidate,
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

        [TestMethod]
        public void ReplaceVariables_OutputDirAndConfigDir_ReplacesLiteralPaths()
        {
            var ctx = MakeCtx() with
            {
                OutputDir = @"C:\Music\Output",
                ConfigDir = @"C:\Users\me\.config\sockseek",
            };

            string result = FileManager.ReplaceVariables("{outputdir}|{configdir}", ctx, null);

            Assert.AreEqual(@"C:\Music\Output|C:\Users\me\.config\sockseek", result);
        }

        [TestMethod]
        public void ReplaceVariables_LocalPathVariables_UseNativePathSeparators()
        {
            var nativePath = Path.Combine(Path.GetTempPath(), "sockseek path test", "01. Track.flac");
            var storedPath = nativePath.Replace('\\', '/');
            var ctx = MakeCtx() with
            {
                DownloadPath = storedPath,
            };

            string result = FileManager.ReplaceVariables("{path}|{path-noext}|{ext}", ctx, null);

            var expectedPath = Path.GetFullPath(storedPath).TrimEnd('/').TrimEnd('\\');
            var expectedNoExt = Path.Combine(Path.GetDirectoryName(expectedPath) ?? "", Path.GetFileNameWithoutExtension(expectedPath));
            Assert.AreEqual($"{expectedPath}|{expectedNoExt}|.flac", result);
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
    public class OutputScopeTests
    {
        [TestMethod]
        public void ForPreparedJob_AddsFolderSegmentsForListAndAggregateContainers()
        {
            var output = new OutputSettings();
            var list = new JobList("Wishlist");
            var aggregate = new AggregateJob(new SongQuery { Artist = "Artist1" }) { ItemName = "Artist1" };
            var albumAggregate = new AlbumAggregateJob(new AlbumQuery { Artist = "Artist1" }) { ItemName = "Artist1 Albums" };

            var scope = OutputScope.Empty;
            scope = OutputScope.ForPreparedJob(list, scope, output);
            scope = OutputScope.ForPreparedJob(aggregate, scope, output);
            scope = OutputScope.ForPreparedJob(albumAggregate, scope, output);

            Assert.AreEqual(Path.Join("Wishlist", "Artist1", "Artist1 Albums"), scope.DefaultFolder);
        }

        [TestMethod]
        public void ForPreparedJob_DoesNotAddLeafSongOrAlbumSegments()
        {
            var output = new OutputSettings();
            var inherited = OutputScope.Empty.WithDefaultFolder("Wishlist", output.InvalidReplaceStr);
            var song = new SongJob(new SongQuery { Artist = "Artist1", Title = "Track1" });
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });

            var songScope = OutputScope.ForPreparedJob(song, inherited, output);
            var albumScope = OutputScope.ForPreparedJob(album, inherited, output);

            Assert.AreEqual(inherited.DefaultFolder, songScope.DefaultFolder);
            Assert.AreEqual(inherited.DefaultFolder, albumScope.DefaultFolder);
        }
    }

    [TestClass]
    public class OrganizationTests
    {
        private string testRoot = "";
        private DownloadSettings config = null!;
        private const string AlbumRemoteDir = @"User1\Artist1\Album1";
        private const string AlbumRemoteTrack = AlbumRemoteDir + @"\01. Track1.mp3";

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "slsk-org-tests-" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(testRoot);
            config = TestHelpers.CreateDefaultSettings().Download;
            config.Output.ParentDir = testRoot;
            config.Output.IncompleteAlbumAction.Kind = IncompleteAlbumActionKind.Move;
            config.Output.IncompleteAlbumAction.Path = Path.Join(testRoot, "failed");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }

        [TestMethod]
        public void SingleInputSong_DefaultOrganization_PlacesSongDirectlyInOutputDir()
        {
            var job = new SongJob(new SongQuery { Artist = "Artist1", Title = "Track1" });
            var contexts = Prepare(job);
            var manager = PreparedManager(job, contexts);

            string target = manager.GetSavePath(@"User1\Artist1 - Track1.mp3");

            AssertSamePath(Path.Combine(testRoot, "Artist1 - Track1.mp3"), target);
        }

        [TestMethod]
        public void SingleInputAlbum_DefaultOrganization_PlacesAlbumFolderDirectlyInOutputDir()
        {
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            var contexts = Prepare(job);
            var manager = PreparedManager(job, contexts);
            manager.SetremoteBaseDir(AlbumRemoteDir);

            string target = manager.GetSavePath(AlbumRemoteTrack);

            AssertSamePath(Path.Combine(testRoot, "Album1", "01. Track1.mp3"), target);
        }

        [TestMethod]
        public void JobList_DefaultOrganization_NestsSongsAndAlbumsInsideListFolder()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist1", Title = "Single" });
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            var list = new JobList("MyPlaylist", [song, album]);

            var contexts = Prepare(list);
            var songManager = PreparedManager(song, contexts);
            var albumManager = PreparedManager(album, contexts);
            albumManager.SetremoteBaseDir(AlbumRemoteDir);

            string songTarget = songManager.GetSavePath(@"User1\Artist1 - Single.mp3");
            string albumTarget = albumManager.GetSavePath(AlbumRemoteTrack);

            AssertSamePath(Path.Combine(testRoot, "MyPlaylist", "Artist1 - Single.mp3"), songTarget);
            AssertSamePath(Path.Combine(testRoot, "MyPlaylist", "Album1", "01. Track1.mp3"), albumTarget);
        }

        [TestMethod]
        public void NestedJobLists_DefaultOrganization_NestsSongInsideAllListFolders()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist1", Title = "Nested" });
            var innerList = new JobList("Inner", [song]);
            var outerList = new JobList("Outer", [innerList]);

            var contexts = Prepare(outerList);
            var manager = PreparedManager(song, contexts);

            string target = manager.GetSavePath(@"User1\Artist1 - Nested.mp3");

            Assert.AreEqual(Path.Join("Outer", "Inner"), contexts[song.Id].OutputScope.DefaultFolder);
            AssertSamePath(Path.Combine(testRoot, "Outer", "Inner", "Artist1 - Nested.mp3"), target);
        }

        [TestMethod]
        public void NestedJobLists_NameFormatCanRecoverDefaultOrganization()
        {
            config.Output.NameFormat = "{default-folder}/{filename}";
            var song = new SongJob(new SongQuery { Artist = "Artist1", Title = "Nested" });
            var innerList = new JobList("Inner", [song]);
            var outerList = new JobList("Outer", [innerList]);

            var contexts = Prepare(outerList);
            var manager = PreparedManager(song, contexts);
            MarkDownloaded(song, @"User1\Artist1 - Nested.mp3", Path.Combine(testRoot, ".sockseek-staging", "download.mp3"));

            manager.OrganizeDownloadedFile(song);

            string expected = Path.Combine(testRoot, "Outer", "Inner", "Artist1 - Nested.mp3");
            AssertSamePath(expected, song.DownloadPath!);
            Assert.IsTrue(File.Exists(expected));
        }

        [TestMethod]
        public void AlbumJob_NameFormat_MovesCoverIntelligently()
        {
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            config.Output.NameFormat = "OrgTest/{sartist}/{salbum}/{filename}";
            var contexts = Prepare(job);
            var manager = PreparedManager(job, contexts);
            manager.SetremoteBaseDir(@"Artist1\Album1");

            string audio1Base = Path.Combine(testRoot, "dl1.mp3");
            string audio2Base = Path.Combine(testRoot, "dl2.mp3");
            string coverBase = Path.Combine(testRoot, "cover.jpg");
            File.WriteAllText(audio1Base, "audio1");
            File.WriteAllText(audio2Base, "audio2");
            File.WriteAllText(coverBase, "jpg");

            var file1 = new SongJob(new SongQuery { Artist = "Artist1", Album = "Album1", Title = "Track1" })
            {
                ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(new Soulseek.SearchResponse("user", 0, false, 0, 0, null),
                                                   new Soulseek.File(0, @"Artist1\Album1\01. Track1.mp3", 0, "mp3")),
                DownloadPath = audio1Base,
            };
            file1.SetDone();

            var file2 = new SongJob(new SongQuery { Artist = "Artist1", Album = "Album1", Title = "Track2" })
            {
                ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(new Soulseek.SearchResponse("user", 0, false, 0, 0, null),
                                                   new Soulseek.File(0, @"Artist1\Album1\02. Track2.mp3", 0, "mp3")),
                DownloadPath = audio2Base,
            };
            file2.SetDone();

            var coverFile = new SongJob(new SongQuery())
            {
                ResolvedTarget = SoulseekSearchAdapter.ToFileCandidate(new Soulseek.SearchResponse("user", 0, false, 0, 0, null),
                                                   new Soulseek.File(0, @"Artist1\Album1\Cover.jpg", 0, "jpg")),
                DownloadPath = coverBase,
            };
            coverFile.SetDone();

            var allFiles = new List<SongJob> { file1, file2, coverFile };

            manager.OrganizeAlbum(job, allFiles, null, remainingOnly: false);

            string expectedAudio1 = Path.Combine(testRoot, "OrgTest", "Artist1", "Album1", "01. Track1.mp3");
            string expectedAudio2 = Path.Combine(testRoot, "OrgTest", "Artist1", "Album1", "02. Track2.mp3");
            Assert.IsTrue(File.Exists(expectedAudio1), "Audio 1 not found at target");
            Assert.IsTrue(File.Exists(expectedAudio2), "Audio 2 not found at target");

            string expectedCover = Path.Combine(testRoot, "OrgTest", "Artist1", "Album1", "cover.jpg");
            Assert.IsTrue(File.Exists(expectedCover), $"Cover not found at {expectedCover}");
        }

        [TestMethod]
        public void AggregateJob_DefaultOrganization_GroupsGeneratedSongsInsideAggregateFolder()
        {
            var aggregate = new AggregateJob(new SongQuery { Artist = "Artist1" }) { ItemName = "Artist1" };
            var generatedSong = new SongJob(new SongQuery { Artist = "Artist1", Title = "Track1" });

            var contexts = Prepare(aggregate);
            generatedSong.Config = aggregate.Config;
            var manager = new FileManager(
                generatedSong,
                aggregate.Config.Output,
                aggregate.Config.Extraction,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileManager>.Instance,
                contexts[aggregate.Id].OutputScope);

            string target = manager.GetSavePath(@"User1\Artist1 - Track1.mp3");

            AssertSamePath(Path.Combine(testRoot, "Artist1", "Artist1 - Track1.mp3"), target);
        }

        [TestMethod]
        public void AlbumAggregateJob_DefaultOrganization_GroupsGeneratedAlbumsInsideAggregateFolder()
        {
            var aggregate = new AlbumAggregateJob(new AlbumQuery { Artist = "Artist1" }) { ItemName = "Artist1" };
            var generatedAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });

            var contexts = Prepare(aggregate);
            generatedAlbum.Config = aggregate.Config;
            var manager = new FileManager(
                generatedAlbum,
                aggregate.Config.Output,
                aggregate.Config.Extraction,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileManager>.Instance,
                contexts[aggregate.Id].OutputScope);
            manager.SetremoteBaseDir(AlbumRemoteDir);

            string target = manager.GetSavePath(AlbumRemoteTrack);

            AssertSamePath(Path.Combine(testRoot, "Artist1", "Album1", "01. Track1.mp3"), target);
        }

        [TestMethod]
        public void ExtractJobIndirection_DefaultOrganization_PreservesListAndCsvFolders()
        {
            var (song, contexts) = PrepareNestedExtractedCsvSong();
            var manager = PreparedManager(song, contexts);

            string target = manager.GetSavePath(@"User1\Test Artist - First Song.mp3");

            Assert.AreEqual(Path.Join("wishlist", "songs"), contexts[song.Id].OutputScope.DefaultFolder);
            AssertSamePath(Path.Combine(testRoot, "wishlist", "songs", "Test Artist - First Song.mp3"), target);
        }

        [TestMethod]
        public void ExtractJobIndirection_NameFormatCanRecoverListAndCsvFolders()
        {
            config.Output.NameFormat = "{default-folder}/{filename}";
            var (song, contexts) = PrepareNestedExtractedCsvSong();
            var manager = PreparedManager(song, contexts);
            MarkDownloaded(song, @"User1\Test Artist - First Song.mp3", Path.Combine(testRoot, ".sockseek-staging", "download.mp3"));

            manager.OrganizeDownloadedFile(song);

            string expected = Path.Combine(testRoot, "wishlist", "songs", "Test Artist - First Song.mp3");
            AssertSamePath(expected, song.DownloadPath!);
            Assert.IsTrue(File.Exists(expected));
        }

        [TestMethod]
        public void NameFormat_BypassesDefaultOrganization()
        {
            config.Output.NameFormat = "Custom/{filename}";
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            var list = new JobList("MyPlaylist", [album]);

            var contexts = Prepare(list);
            var manager = PreparedManager(album, contexts);
            manager.SetremoteBaseDir(AlbumRemoteDir);
            string defaultPath = manager.GetSavePath(AlbumRemoteTrack);
            var song = DownloadedSong(AlbumRemoteTrack, defaultPath);

            manager.OrganizeDownloadedFile(song);

            string expected = Path.Combine(testRoot, "Custom", "01. Track1.mp3");
            AssertSamePath(expected, song.DownloadPath!);
            Assert.IsTrue(File.Exists(expected));
            Assert.IsFalse(File.Exists(Path.Combine(testRoot, "MyPlaylist", "Album1", "Custom", "01. Track1.mp3")));
        }

        [TestMethod]
        [DataRow("pdf")]
        [DataRow("aiff")]
        [DataRow("xyz")]
        public void StandaloneNameFormat_OrganizesAnyDownloadedExtension(string extension)
        {
            config.Output.NameFormat = "Formatted/{artist|filename}";
            var song = new SongJob(new SongQuery { Artist = "Artist1", Title = "Document" });
            var contexts = Prepare(song);
            var manager = PreparedManager(song, contexts);
            MarkDownloaded(
                song,
                $@"User1\Original File.{extension}",
                Path.Combine(testRoot, ".sockseek-staging", $"download.{extension}"));

            manager.OrganizeDownloadedFile(song);

            string expected = Path.Combine(testRoot, "Formatted", $"Original File.{extension}");
            AssertSamePath(expected, song.DownloadPath!);
            Assert.IsTrue(File.Exists(expected));
        }

        [TestMethod]
        public void NameFormat_MetadataFailureLogsOnceWithoutPath()
        {
            config.Output.NameFormat = "Formatted/{artist|filename}";
            var song = new SongJob(new SongQuery { Artist = "Artist1", Title = "Track" });
            var contexts = Prepare(song);
            var records = new List<CompactLogRecord>();
            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Trace)
                .AddProvider(new CompactTextLoggerProvider(records.Add, LogLevel.Debug)));
            var manager = new FileManager(
                song,
                song.Config.Output,
                song.Config.Extraction,
                loggerFactory.CreateLogger<FileManager>(),
                contexts[song.Id].OutputScope);
            MarkDownloaded(
                song,
                @"User1\Original File.mp3",
                Path.Combine(testRoot, ".sockseek-staging", "download.mp3"));

            manager.OrganizeDownloadedFile(song);

            CompactLogRecord failure = records.Single(record => record.EventId.Id == 3006);
            Assert.AreEqual(LogLevel.Debug, failure.Level);
            StringAssert.Contains(failure.Message, "File metadata could not be read");
            Assert.IsFalse(failure.Message.Contains(testRoot, StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void NameFormat_OutputExtensionVariableIsNotAppendedTwice()
        {
            config.Output.NameFormat = "Formatted/{filename}{ext}";
            var song = new SongJob(new SongQuery { Artist = "Artist1", Title = "Track" });
            var contexts = Prepare(song);
            var manager = PreparedManager(song, contexts);
            MarkDownloaded(
                song,
                @"User1\Original File.flac",
                Path.Combine(testRoot, ".sockseek-staging", "download.flac"));

            manager.OrganizeDownloadedFile(song);

            string expected = Path.Combine(testRoot, "Formatted", "Original File.flac");
            AssertSamePath(expected, song.DownloadPath!);
            Assert.IsTrue(File.Exists(expected));
            Assert.IsFalse(File.Exists(expected + ".flac"));
        }

        [TestMethod]
        public void IsAudioVariable_UsesKnownAudioClassification()
        {
            var aiff = DownloadedSong(@"User1\Track.aiff", Path.Combine(testRoot, "Track.aiff"));
            var pdf = DownloadedSong(@"User1\Booklet.pdf", Path.Combine(testRoot, "Booklet.pdf"));

            bool aiffFound = FileManager.TryGetCleanVarValue(
                "is-audio",
                FileManagerContext.FromSongJob(aiff, aiff),
                () => null,
                "_",
                out string aiffValue);
            bool pdfFound = FileManager.TryGetCleanVarValue(
                "is-audio",
                FileManagerContext.FromSongJob(pdf, pdf),
                () => null,
                "_",
                out string pdfValue);

            Assert.IsTrue(aiffFound);
            Assert.IsTrue(pdfFound);
            Assert.AreEqual("true", aiffValue);
            Assert.AreEqual("false", pdfValue);
        }

        [TestMethod]
        public void IsAudioVariable_ClassifiesAlreadyExistingFileFromLocalPath()
        {
            var pdf = new SongJob(new SongQuery { Artist = "Artist1", Title = "Booklet" })
            {
                DownloadPath = Path.Combine(testRoot, "Artist1 - Booklet.pdf"),
            };

            FileManager.TryGetCleanVarValue(
                "is-audio",
                FileManagerContext.FromSongJob(pdf, pdf),
                () => null,
                "_",
                out string value);

            Assert.AreEqual("false", value);
        }

        [TestMethod]
        public void NameFormat_CanRecoverDefaultOrganization()
        {
            config.Output.NameFormat = "{default-folder}/{foldername}/{filename}";
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist1", Album = "Album1" });
            var list = new JobList("MyPlaylist", [album]);

            var contexts = Prepare(list);
            var manager = PreparedManager(album, contexts);
            manager.SetremoteBaseDir(AlbumRemoteDir);
            string stagingPath = CreateDownloadedFile(Path.Combine(testRoot, ".sockseek-staging", "download.mp3"));
            var song = DownloadedSong(AlbumRemoteTrack, stagingPath);

            manager.OrganizeDownloadedFile(song);

            string expected = Path.Combine(testRoot, "MyPlaylist", "Album1", "01. Track1.mp3");
            AssertSamePath(expected, song.DownloadPath!);
            Assert.IsTrue(File.Exists(expected));
        }

        private Dictionary<Guid, JobContext> Prepare(params Job[] jobs)
            => JobPreparer.PrepareJobs(new JobList(null, jobs), config);

        private FileManager PreparedManager(Job job, Dictionary<Guid, JobContext> contexts)
            => new(
                job,
                job.Config.Output,
                job.Config.Extraction,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileManager>.Instance,
                contexts[job.Id].OutputScope);

        private (SongJob Song, Dictionary<Guid, JobContext> Contexts) PrepareNestedExtractedCsvSong()
        {
            var contexts = new Dictionary<Guid, JobContext>();
            var rootExtract = new ExtractJob("wishlist.txt", InputType.List);

            foreach (var (id, ctx) in JobPreparer.PrepareJobs(new JobList(null, [rootExtract]), config))
                contexts[id] = ctx;

            var csvExtract = new ExtractJob("songs.csv", InputType.CSV);
            var extractedList = new JobList("wishlist", [csvExtract]);
            rootExtract.Result = extractedList;

            foreach (var (id, ctx) in JobPreparer.PrepareSubtree(extractedList, rootExtract.Config, parentCtx: contexts[rootExtract.Id]))
                contexts[id] = ctx;

            var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "First Song" });
            var extractedCsv = new JobList("songs", [song]);
            csvExtract.Result = extractedCsv;

            foreach (var (id, ctx) in JobPreparer.PrepareSubtree(extractedCsv, csvExtract.Config, explicitOwnerList: extractedList, parentCtx: contexts[csvExtract.Id]))
                contexts[id] = ctx;

            return (song, contexts);
        }

        private static FileCandidate Candidate(string filename)
        {
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, []);
            return SoulseekSearchAdapter.ToFileCandidate(response, TestHelpers.CreateSlFile(filename));
        }

        private SongJob MarkDownloaded(SongJob song, string remoteFilename, string localPath)
        {
            string downloadPath = CreateDownloadedFile(localPath);
            var candidate = Candidate(remoteFilename);
            song.ResolvedTarget = candidate;
            song.Candidates = [candidate];
            song.DownloadPath = downloadPath;
            return song;
        }

        private SongJob DownloadedSong(string remoteFilename, string localPath)
        {
            string downloadPath = CreateDownloadedFile(localPath);
            var candidate = Candidate(remoteFilename);
            return new SongJob(new SongQuery { Artist = "Artist1", Album = "Album1", Title = "Track1" })
            {
                ResolvedTarget = candidate,
                Candidates = [candidate],
                DownloadPath = downloadPath,
            };
        }

        private static string CreateDownloadedFile(string path)
        {
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            File.WriteAllText(path, "data");
            return path;
        }

        private static void AssertSamePath(string expected, string actual)
        {
            Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(actual));
        }
    }
}
