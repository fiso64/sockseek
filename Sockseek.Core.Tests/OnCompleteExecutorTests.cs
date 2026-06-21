using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Models;
using Sockseek.Core;
using System.Reflection;
using System.Diagnostics;

namespace Tests.OnCompleteExecutorTests
{
    // OnCompleteExecutor has private methods tested via reflection,
    // following the established pattern in this test suite.

    [TestClass]
    public class ParseCommandSyntaxTests
    {
        private static object InvokeParseCommand(string rawCommand)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ParseCommand", BindingFlags.NonPublic | BindingFlags.Static)!;
            try
            {
                return method.Invoke(null, new object[] { rawCommand })!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException!;
            }
        }

        private static T Get<T>(object obj, string prop) =>
            (T)obj.GetType().GetProperty(prop)!.GetValue(obj)!;

        [TestMethod]
        public void ParseCommand_NoOptions_ReturnsCommandAfterDelimiter()
        {
            var result = InvokeParseCommand("-- mycommand arg1");
            Assert.AreEqual("mycommand arg1", Get<string>(result, "Command"));
            Assert.IsFalse(Get<bool>(result, "UseShellExecute"));
        }

        [TestMethod]
        public void ParseCommand_ShellOption_SetsUseShellExecute()
        {
            var result = InvokeParseCommand("shell -- mycommand");
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommand_TrackScope_SetsScope()
        {
            var result = InvokeParseCommand("scope=track -- mycommand");
            Assert.AreEqual("Track", Get<object>(result, "Scope").ToString());
        }

        [TestMethod]
        public void ParseCommand_AlbumScope_SetsScope()
        {
            var result = InvokeParseCommand("scope=album -- mycommand");
            Assert.AreEqual("Album", Get<object>(result, "Scope").ToString());
        }

        [TestMethod]
        public void ParseCommand_MultipleOptions_AreParsed()
        {
            var result = InvokeParseCommand("when=success scope=album shell hidden -- mycommand");
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.IsTrue(Get<bool>(result, "CreateNoWindow"));
            Assert.AreEqual("Album", Get<object>(result, "Scope").ToString());
            Assert.AreEqual("Success", Get<object>(result, "When").ToString());
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommand_UpdateIndexOption_SetsUseOutputToUpdateIndex()
        {
            var result = InvokeParseCommand("update-index -- mycommand");
            Assert.IsTrue(Get<bool>(result, "UseOutputToUpdateIndex"));
        }

        [TestMethod]
        public void ParseCommand_LockOption_SetsUseLocking()
        {
            var result = InvokeParseCommand("lock -- mycommand");
            Assert.IsTrue(Get<bool>(result, "UseLocking"));
        }

        [TestMethod]
        public void ParseCommand_MissingDelimiter_ThrowsHelpfulError()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() => InvokeParseCommand("mycommand"));
            StringAssert.Contains(ex.Message, "Missing `--` command delimiter");
        }

        [TestMethod]
        public void ParseCommand_LegacyPrefixSyntax_ThrowsMigrationHint()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() => InvokeParseCommand("1:a:h: mycommand"));
            StringAssert.Contains(ex.Message, "Legacy one-letter prefixes are no longer supported");
        }

        [TestMethod]
        public void ParseCommand_EmptyCommandAfterDelimiter_Throws()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() => InvokeParseCommand("hidden -- "));
            StringAssert.Contains(ex.Message, "Command after `--` is empty");
        }

        [TestMethod]
        public void ParseCommand_UnknownOption_Throws()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() => InvokeParseCommand("bogus -- mycommand"));
            StringAssert.Contains(ex.Message, "Unknown option `bogus`");
        }
    }

    [TestClass]
    public class ProcessCommandResultTests
    {
        [TestMethod]
        public void ProcessCommandResult_NonZeroExit_LogsVisibleBoundedWarning()
        {
            SockseekLog.RemoveNonFileOutputs();
            SockseekLog.RemoveFileOutputs();

            var entries = new List<SockseekLog.StructuredLogEntry>();
            SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry), LogLevel.Information);

            try
            {
                var result = CreateProcessResult(
                    exitCode: 1,
                    stdout: new string('o', 900),
                    stderr: "'win-notify-send.wrong.cmd' is not recognized\r\n" + new string('e', 900));
                var config = CreateCommandConfig();
                var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

                var needsUpdate = InvokeProcessCommandResult(result, config, null, job, "[4] AlbumJob:");

                Assert.IsFalse(needsUpdate);
                var warning = entries.Single(entry => entry.Level == LogLevel.Warning);
                Assert.AreEqual(SockseekLog.Categories.Jobs, warning.CategoryName);
                StringAssert.Contains(warning.Message, "[4] AlbumJob: on-complete command exited with code 1.");
                StringAssert.Contains(warning.Message, "Stdout:");
                StringAssert.Contains(warning.Message, "Stderr:");
                StringAssert.Contains(warning.Message, "\n    Stderr:\n");
                StringAssert.Contains(warning.Message, "\n      'win-notify-send.wrong.cmd' is not recognized\n");
                StringAssert.Contains(warning.Message, "truncated");
                Assert.IsFalse(warning.Message.Contains('\r'));
                Assert.IsFalse(warning.Message.Contains("\\n"));
                Assert.IsTrue(warning.Message.Length < 1600, warning.Message);
            }
            finally
            {
                SockseekLog.RemoveNonFileOutputs();
                SockseekLog.RemoveFileOutputs();
            }
        }

        [TestMethod]
        public void ProcessCommandResult_UpdateIndexWithTruncatedStdout_DoesNotMutateSongPath()
        {
            SockseekLog.RemoveNonFileOutputs();
            SockseekLog.RemoveFileOutputs();

            var entries = new List<SockseekLog.StructuredLogEntry>();
            SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry), LogLevel.Information);

            try
            {
                var result = CreateProcessResult(
                    exitCode: 0,
                    stdout: "ignored;C:/partial/path",
                    stderr: null,
                    stdoutTruncated: true,
                    stdoutCharsRead: 70000);
                var config = CreateCommandConfig(useOutputToUpdateIndex: true);
                var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
                {
                    DownloadPath = "C:/old/path.flac",
                };
                var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

                var needsUpdate = InvokeProcessCommandResult(result, config, song, job, "[4] AlbumJob:");

                Assert.IsFalse(needsUpdate);
                Assert.AreEqual("C:/old/path.flac", song.DownloadPath);
                var warning = entries.Single(entry => entry.Level == LogLevel.Warning);
                StringAssert.Contains(warning.Message, "ignored on-complete stdout for index update because command output exceeded the capture limit");
            }
            finally
            {
                SockseekLog.RemoveNonFileOutputs();
                SockseekLog.RemoveFileOutputs();
            }
        }

        private static object CreateProcessResult(
            int exitCode,
            string? stdout,
            string? stderr,
            int stdoutCharsRead = 0,
            bool stdoutTruncated = false,
            int stderrCharsRead = 0,
            bool stderrTruncated = false)
        {
            var type = typeof(OnCompleteExecutor).GetNestedType("ProcessResult", BindingFlags.NonPublic)!;
            var result = Activator.CreateInstance(type)!;
            type.GetProperty("ExitCode")!.SetValue(result, exitCode);
            type.GetProperty("Stdout")!.SetValue(result, stdout);
            type.GetProperty("Stderr")!.SetValue(result, stderr);
            type.GetProperty("StdoutCharsRead")!.SetValue(result, stdoutCharsRead);
            type.GetProperty("StdoutTruncated")!.SetValue(result, stdoutTruncated);
            type.GetProperty("StderrCharsRead")!.SetValue(result, stderrCharsRead);
            type.GetProperty("StderrTruncated")!.SetValue(result, stderrTruncated);
            return result;
        }

        private static object CreateCommandConfig(bool useOutputToUpdateIndex = false)
        {
            var type = typeof(OnCompleteExecutor).GetNestedType("CommandConfig", BindingFlags.NonPublic)!;
            var config = Activator.CreateInstance(type)!;
            type.GetProperty("UseOutputToUpdateIndex")!.SetValue(config, useOutputToUpdateIndex);
            return config;
        }

        private static bool InvokeProcessCommandResult(object result, object config, SongJob? song, Job job, string logPrefix)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ProcessCommandResult", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (bool)method.Invoke(null, new[] { result, config, song, job, logPrefix })!;
        }
    }

    [TestClass]
    public class ShouldExecuteCommandTests
    {
        private static bool InvokeShouldExecute(
            string command,
            bool isTrack,
            bool isAlbum,
            JobTerminalOutcome terminalOutcome = JobTerminalOutcome.Succeeded,
            JobSkipReason skipReason = JobSkipReason.None)
        {
            var parseMethod = typeof(OnCompleteExecutor).GetMethod("ParseCommand", BindingFlags.NonPublic | BindingFlags.Static)!;
            var config = parseMethod.Invoke(null, new object[] { command })!;

            var outcome = terminalOutcome switch
            {
                JobTerminalOutcome.Succeeded => JobOutcome.Done(),
                JobTerminalOutcome.Failed => JobOutcome.Failed(JobFailureReason.Other),
                JobTerminalOutcome.Skipped => JobOutcome.Skipped(skipReason),
                JobTerminalOutcome.Cancelled => JobOutcome.Cancelled(JobCancellationSource.UserRequestedJob),
                JobTerminalOutcome.PartialSuccess => JobOutcome.PartialSuccess(),
                _ => JobOutcome.NoChange(),
            };

            var method = typeof(OnCompleteExecutor).GetMethod("ShouldExecuteCommand", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (bool)method.Invoke(null, new object[] { config, outcome, isTrack, isAlbum })!;
        }

        [TestMethod]
        public void ShouldExecute_Default_RunsForNonSkippedOutcomes()
        {
            Assert.IsTrue(InvokeShouldExecute("-- cmd", isTrack: true, isAlbum: false));
            Assert.IsTrue(InvokeShouldExecute("-- cmd", isTrack: false, isAlbum: true, terminalOutcome: JobTerminalOutcome.Failed));
            Assert.IsFalse(InvokeShouldExecute("-- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_TrackScope_OnlyRunsForTrackContext()
        {
            Assert.IsTrue(InvokeShouldExecute("scope=track -- cmd", isTrack: true, isAlbum: false));
            Assert.IsFalse(InvokeShouldExecute("scope=track -- cmd", isTrack: false, isAlbum: true));
        }

        [TestMethod]
        public void ShouldExecute_AlbumScope_OnlyRunsForAlbumContext()
        {
            Assert.IsTrue(InvokeShouldExecute("scope=album -- cmd", isTrack: false, isAlbum: true));
            Assert.IsFalse(InvokeShouldExecute("scope=album -- cmd", isTrack: true, isAlbum: false));
        }

        [TestMethod]
        public void ShouldExecute_SuccessWhen_OnlyRunsForSucceeded()
        {
            Assert.IsTrue(InvokeShouldExecute("when=success -- cmd", isTrack: true, isAlbum: false));
            Assert.IsFalse(InvokeShouldExecute("when=success -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Failed));
        }

        [TestMethod]
        public void ShouldExecute_FailureWhen_RunsForFailedAndPartialSuccess()
        {
            Assert.IsTrue(InvokeShouldExecute("when=failure -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Failed));
            Assert.IsTrue(InvokeShouldExecute("when=failure -- cmd", isTrack: false, isAlbum: true, terminalOutcome: JobTerminalOutcome.PartialSuccess));
            Assert.IsFalse(InvokeShouldExecute("when=failure -- cmd", isTrack: true, isAlbum: false));
        }

        [TestMethod]
        public void ShouldExecute_SkippedWhen_RunsForAnySkippedOutcome()
        {
            Assert.IsTrue(InvokeShouldExecute("when=skipped -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_AlreadyExistsWhen_RunsOnlyForAlreadyExistsSkippedOutcome()
        {
            Assert.IsTrue(InvokeShouldExecute("when=already-exists -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
            Assert.IsFalse(InvokeShouldExecute("when=already-exists -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.NotFoundLastTime));
        }

        [TestMethod]
        public void ShouldExecute_NotFoundLastTimeWhen_RunsOnlyForNotFoundLastTimeSkippedOutcome()
        {
            Assert.IsTrue(InvokeShouldExecute("when=not-found-last-time -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.NotFoundLastTime));
            Assert.IsFalse(InvokeShouldExecute("when=not-found-last-time -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_CancelledWhen_RunsOnlyForCancelled()
        {
            Assert.IsTrue(InvokeShouldExecute("when=cancelled -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Cancelled));
            Assert.IsFalse(InvokeShouldExecute("when=cancelled -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Failed));
        }

        [TestMethod]
        public void ShouldExecute_AnyWhen_RunsForSkippedToo()
        {
            Assert.IsTrue(InvokeShouldExecute("when=any -- cmd", isTrack: true, isAlbum: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }
    }

    [TestClass]
    public class ExecuteAsyncTests
    {
        [TestMethod]
        public async Task ExecuteAsync_AlbumOnlySuccess_RunsOnceForAlbumCompletion_NotForAlbumTracks()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var markerPath = Path.Combine(tempDir, "marker.txt");
                var settings = new DownloadSettings();
                settings.Output.ParentDir = tempDir;
                settings.Output.OnComplete =
                [
                    $"when=success scope=album hidden -- {AppendMarkerCommand(markerPath, "album")}",
                    $"when=success scope=track hidden -- {AppendMarkerCommand(markerPath, "track")}",
                    $"when=failure scope=album hidden -- {AppendMarkerCommand(markerPath, "failed")}",
                ];

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Config = settings,
                };
                album.SetDone(tempDir);

                var track = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track" })
                {
                    Config = settings,
                    DownloadPath = Path.Combine(tempDir, "track.flac"),
                };
                track.SetDone(track.DownloadPath);

                await OnCompleteExecutor.ExecuteAsync(album, track, new JobContext(), JobOutcome.Done(track.DownloadPath));
                await OnCompleteExecutor.ExecuteAsync(album, null, new JobContext(), JobOutcome.Done(tempDir));

                CollectionAssert.AreEqual(new[] { "track", "album" }, File.ReadAllLines(markerPath));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task Engine_AlbumOnlySuccessCommand_RunsOnceAfterAlbumCompletion()
        {
            var musicRoot = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-music-" + Guid.NewGuid());
            var albumDir = Path.Combine(musicRoot, "Main", "TestArtist", "TestAlbum");
            var outputDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-out-" + Guid.NewGuid());
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(outputDir);

            try
            {
                File.WriteAllBytes(Path.Combine(albumDir, "01. TestArtist - Track1.mp3"), TestHelpers.EmptyMp3Bytes);
                File.WriteAllBytes(Path.Combine(albumDir, "02. TestArtist - Track2.mp3"), TestHelpers.EmptyMp3Bytes);

                var markerPath = Path.Combine(outputDir, "marker.txt");
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var settings = new DownloadSettings();
                settings.Extraction.Input = "TestArtist TestAlbum";
                settings.Extraction.IsAlbum = true;
                settings.Output.ParentDir = outputDir;
                settings.Output.OnComplete =
                [
                    $"when=success scope=album hidden -- {AppendMarkerCommand(markerPath, "album")}",
                ];

                var client = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, albumDir);
                var app = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));
                var albumActivityPhases = new List<JobActivityPhase>();
                app.Events.JobActivityChanged += (job, phase, _) =>
                {
                    if (job is AlbumJob)
                        albumActivityPhases.Add(phase);
                };
                app.Enqueue(new ExtractJob(settings.Extraction.Input, settings.Extraction.InputType), settings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var album = app.Queue.AllJobs().OfType<AlbumJob>().Single();
                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                CollectionAssert.Contains(albumActivityPhases, JobActivityPhase.RunningOnComplete);
                CollectionAssert.AreEqual(new[] { "album" }, File.ReadAllLines(markerPath));
            }
            finally
            {
                if (Directory.Exists(musicRoot))
                    Directory.Delete(musicRoot, recursive: true);
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task Engine_AlreadyExistsTrackCommand_RunsForSkippedSong()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-existing-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                File.WriteAllBytes(Path.Combine(outputDir, "Artist - Track.mp3"), TestHelpers.EmptyMp3Bytes);
                var markerPath = Path.Combine(outputDir, "marker.txt");

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var settings = new DownloadSettings();
                settings.Output.ParentDir = outputDir;
                settings.Output.OnComplete =
                [
                    $"when=already-exists scope=track hidden -- {AppendMarkerCommand(markerPath, "already-exists")}",
                ];
                settings.Skip.SkipExisting = true;
                settings.Skip.SkipMode = SkipMode.Name;

                var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
                var client = new ClientTests.MockSoulseekClient([]);
                var app = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));
                app.Enqueue(song, settings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                Assert.IsTrue(song.IsSkippedAlreadyExists);
                CollectionAssert.AreEqual(new[] { "already-exists" }, File.ReadAllLines(markerPath));
            }
            finally
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, recursive: true);
            }
        }

        [TestMethod]
        public void HasApplicableCommand_AlbumOnlyCommand_DoesNotApplyToAlbumTrackCompletion()
        {
            var settings = new DownloadSettings();
            settings.Output.OnComplete = ["when=success scope=album -- notify"];

            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
            {
                Config = settings,
            };
            album.SetDone("album-path");

            var track = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track" });
            track.SetDone("track-path");

            Assert.IsFalse(OnCompleteExecutor.HasApplicableCommand(album, track, JobOutcome.Done("track-path")));
            Assert.IsTrue(OnCompleteExecutor.HasApplicableCommand(album, null, JobOutcome.Done("album-path")));
        }

        [TestMethod]
        public async Task ExecuteAsync_UnqualifiedCommand_DoesNotRunForSkippedAlbumOrTrack()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var markerPath = Path.Combine(tempDir, "marker.txt");
                var settings = new DownloadSettings();
                settings.Output.ParentDir = tempDir;
                settings.Output.OnComplete =
                [
                    $"hidden -- {AppendMarkerCommand(markerPath, "ran")}",
                    $"scope=album hidden -- {AppendMarkerCommand(markerPath, "album")}",
                    $"scope=track hidden -- {AppendMarkerCommand(markerPath, "track")}",
                ];

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Config = settings,
                };
                album.SetSkipped(JobSkipReason.AlreadyExists);

                var track = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track" })
                {
                    Config = settings,
                    DownloadPath = Path.Combine(tempDir, "track.flac"),
                };
                track.SetSkipped(JobSkipReason.AlreadyExists);

                var skipped = JobOutcome.Skipped(JobSkipReason.AlreadyExists);
                await OnCompleteExecutor.ExecuteAsync(album, track, new JobContext(), skipped);
                await OnCompleteExecutor.ExecuteAsync(album, null, new JobContext(), skipped);

                Assert.IsFalse(File.Exists(markerPath));
                Assert.IsFalse(OnCompleteExecutor.HasApplicableCommand(album, track, skipped));
                Assert.IsFalse(OnCompleteExecutor.HasApplicableCommand(album, null, skipped));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_StdoutVariables_AreAvailableWithoutReadOutputPrefix()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var markerPath = Path.Combine(tempDir, "marker.txt");
                var settings = new DownloadSettings();
                settings.Output.ParentDir = tempDir;
                settings.Output.OnComplete =
                [
                    $"when=success hidden -- {WriteStdoutCommand("ready")}",
                    $"when=success hidden -- {AppendMarkerCommand(markerPath, "{stdout}")}",
                ];

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Config = settings,
                };
                album.SetDone(tempDir);

                await OnCompleteExecutor.ExecuteAsync(album, null, new JobContext(), JobOutcome.Done(tempDir));

                CollectionAssert.AreEqual(new[] { "ready" }, File.ReadAllLines(markerPath));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_AlbumLevelVariables_UseFirstAudioFileTagsAndAlbumJobContext()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);

            TagLib.File? tagFile = null;
            string? tagPath = null;

            try
            {
                var markerPath = Path.Combine(tempDir, "marker.txt");
                var albumPath = tempDir.Replace('\\', '/');

                tagFile = Tests.TestHelpers.CreateEmptyMP3(
                    title: "TagTitle",
                    artist: "TagArtist",
                    album: "TagAlbum");
                tagPath = tagFile.Name.Replace('\\', '/');
                tagFile.Dispose();
                tagFile = null;

                var settings = new DownloadSettings();
                settings.Output.ParentDir = tempDir;
                settings.Output.OnComplete =
                [
                    $"when=success scope=album hidden -- {AppendMarkerCommand(markerPath, "{title}|{artist}|{album}|{sartist}|{salbum}|{path}")}",
                ];

                var album = new AlbumJob(new AlbumQuery { Artist = "AlbumSourceArtist", Album = "AlbumSourceAlbum" })
                {
                    Config = settings,
                };
                album.SetDone(albumPath);

                var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, []);
                var candidate = new FileCandidate(response, new Soulseek.File(1, @"remote\Artist\Album\01.mp3", 100, ".mp3"));
                var firstAudio = new SongJob(new SongQuery { Artist = "TrackSourceArtist", Album = "TrackSourceAlbum", Title = "TrackSourceTitle" })
                {
                    Config = settings,
                    ResolvedTarget = candidate,
                };
                firstAudio.SetDone(tagPath, candidate);

                album.ResolvedTarget = new AlbumFolder("user", @"remote\Artist\Album", [firstAudio]);

                await OnCompleteExecutor.ExecuteAsync(album, null, new JobContext(), JobOutcome.Done(albumPath));

                Assert.AreEqual(
                    $"TagTitle|TagArtist|TagAlbum|AlbumSourceArtist|AlbumSourceAlbum|{Path.GetFullPath(albumPath)}",
                    File.ReadAllText(markerPath).Trim());
            }
            finally
            {
                tagFile?.Dispose();
                if (tagPath != null && File.Exists(tagPath))
                    File.Delete(tagPath);
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private static string WriteStdoutCommand(string value)
        {
            if (OperatingSystem.IsWindows())
            {
                var cmdValue = value.Replace("\"", "\\\"");
                return $"cmd /d /c echo {cmdValue}";
            }

            var shellValue = value.Replace("'", "'\\''");
            return $"/bin/sh -c \"printf '%s\\n' '{shellValue}'\"";
        }

        private static string AppendMarkerCommand(string markerPath, string marker)
        {
            if (OperatingSystem.IsWindows())
            {
                var powershellPath = markerPath.Replace('\\', '/').Replace("'", "''");
                var powershellMarker = marker.Replace("'", "''");
                return $"powershell -NoProfile -NonInteractive -WindowStyle Hidden -Command \"Add-Content -LiteralPath '{powershellPath}' -Value '{powershellMarker}'\"";
            }

            var shellPath = markerPath.Replace("'", "'\\''");
            var shellMarker = marker.Replace("'", "'\\''");
            return $"/bin/sh -c \"echo '{shellMarker}' >> '{shellPath}'\"";
        }
    }

    [TestClass]
    public class ParseFileNameAndArgumentsTests
    {
        private static (string FileName, string Arguments) InvokeParse(string command)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ParseFileNameAndArguments", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, new object[] { command })!;
            // ValueTuple named fields aren't accessible via dynamic; use positional fields
            var fields = result.GetType().GetFields();
            return ((string)fields[0].GetValue(result)!, (string)fields[1].GetValue(result)!);
        }

        private static ProcessStartInfo InvokeConfigure(string command, bool useShellExecute = false, bool useOutputToUpdateIndex = false)
        {
            var (file, args) = InvokeParse(command);
            var configType = typeof(OnCompleteExecutor).GetNestedType("CommandConfig", BindingFlags.NonPublic)!;
            var config = Activator.CreateInstance(configType)!;
            configType.GetProperty("UseShellExecute")!.SetValue(config, useShellExecute);
            configType.GetProperty("UseOutputToUpdateIndex")!.SetValue(config, useOutputToUpdateIndex);

            var method = typeof(OnCompleteExecutor).GetMethod("ConfigureProcessStartInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (ProcessStartInfo)method.Invoke(null, new[] { file, args, config })!;
        }

        private static string InvokeFormatProcessArgumentsForLog(ProcessStartInfo startInfo)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("FormatProcessArgumentsForLog", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (string)method.Invoke(null, new object[] { startInfo })!;
        }

        [TestMethod]
        public void ParseFileName_SimpleCommand_SplitsOnFirstSpace()
        {
            var (file, args) = InvokeParse("myprogram arg1 arg2");
            Assert.AreEqual("myprogram", file);
            Assert.AreEqual("arg1 arg2", args);
        }

        [TestMethod]
        public void ParseFileName_QuotedPath_ParsedCorrectly()
        {
            var (file, args) = InvokeParse("\"C:\\Program Files\\tool.exe\" --flag value");
            Assert.AreEqual("C:\\Program Files\\tool.exe", file);
            Assert.AreEqual("--flag value", args);
        }

        [TestMethod]
        public void ConfigureProcessStartInfo_NonShellExecute_PreservesRawWindowsArguments()
        {
            var command = "cmd /c if true==true if not exist \"C:\\Users\\fiso\\Temp\\Some Album\\01. Track.mp3\" echo true";
            var startInfo = InvokeConfigure(command);

            Assert.AreEqual("cmd", startInfo.FileName);
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.IsTrue(startInfo.RedirectStandardOutput);
            Assert.AreEqual(
                "/c if true==true if not exist \"C:\\Users\\fiso\\Temp\\Some Album\\01. Track.mp3\" echo true",
                startInfo.Arguments);
            Assert.AreEqual(0, startInfo.ArgumentList.Count);
        }

        [TestMethod]
        public void ParseFileName_NoArgs_ReturnsEmptyArguments()
        {
            var (file, args) = InvokeParse("singlecommand");
            Assert.AreEqual("singlecommand", file);
            Assert.AreEqual("", args);
        }

        [TestMethod]
        public void ParseFileName_EmptyCommand_ReturnsEmpty()
        {
            var (file, args) = InvokeParse("");
            Assert.AreEqual("", file);
            Assert.AreEqual("", args);
        }

        [TestMethod]
        public void FormatProcessArgumentsForLog_NonShellExecute_UsesArgumentsString()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = "/d /c win-notify-send.cmd \"Downloaded: Sonic Youth C:/Music\"",
                UseShellExecute = false,
            };

            Assert.AreEqual(
                "Arguments='/d /c win-notify-send.cmd \"Downloaded: Sonic Youth C:/Music\"'",
                InvokeFormatProcessArgumentsForLog(startInfo));
        }

        [TestMethod]
        public void FormatProcessArgumentsForLog_ShellExecute_UsesArgumentsString()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "win-notify-send.cmd",
                Arguments = "\"Downloaded: Sonic Youth\"",
                UseShellExecute = true,
            };

            Assert.AreEqual(
                "Arguments='\"Downloaded: Sonic Youth\"'",
                InvokeFormatProcessArgumentsForLog(startInfo));
        }
    }
}
