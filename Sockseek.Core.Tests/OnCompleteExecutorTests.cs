using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core;
using System.Reflection;
using System.Diagnostics;

namespace Tests.OnCompleteExecutorTests
{
    // OnCompleteExecutor has private methods tested via reflection,
    // following the established pattern in this test suite.

    [TestClass]
    public class ParseCommandFlagsTests
    {
        private static dynamic InvokeParseCommandFlags(string rawCommand)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ParseCommandFlags", BindingFlags.NonPublic | BindingFlags.Static)!;
            return method.Invoke(null, new object[] { rawCommand })!;
        }

        private static T Get<T>(object obj, string prop) =>
            (T)obj.GetType().GetProperty(prop)!.GetValue(obj)!;

        [TestMethod]
        public void ParseCommandFlags_NoFlags_ReturnsCommandUnchanged()
        {
            var result = InvokeParseCommandFlags("mycommand arg1");
            Assert.AreEqual("mycommand arg1", Get<string>(result, "Command"));
            Assert.IsFalse(Get<bool>(result, "UseShellExecute"));
        }

        [TestMethod]
        public void ParseCommandFlags_ShellExecuteFlag_SetsUseShellExecute()
        {
            var result = InvokeParseCommandFlags("s:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommandFlags_TrackOnlyFlag_SetsOnlyTrack()
        {
            var result = InvokeParseCommandFlags("t:mycommand");
            Assert.IsTrue(Get<bool>(result, "OnlyTrackOnComplete"));
            Assert.IsFalse(Get<bool>(result, "OnlyAlbumOnComplete"));
        }

        [TestMethod]
        public void ParseCommandFlags_AlbumOnlyFlag_SetsOnlyAlbum()
        {
            var result = InvokeParseCommandFlags("a:mycommand");
            Assert.IsTrue(Get<bool>(result, "OnlyAlbumOnComplete"));
        }

        [TestMethod]
        public void ParseCommandFlags_MultipleFlags_AllParsed()
        {
            var result = InvokeParseCommandFlags("s:h:t:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.IsTrue(Get<bool>(result, "CreateNoWindow"));
            Assert.IsTrue(Get<bool>(result, "OnlyTrackOnComplete"));
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommandFlags_ResultAndAlbumFlags_AllParsed()
        {
            var result = InvokeParseCommandFlags("1:a:s:mycommand");
            Assert.AreEqual(1, Get<int?>(result, "RequiredResultCode"));
            Assert.IsTrue(Get<bool>(result, "OnlyAlbumOnComplete"));
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommandFlags_UpdateIndexFlag_SetsUseOutputToUpdateIndex()
        {
            var result = InvokeParseCommandFlags("u:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseOutputToUpdateIndex"));
        }

        [TestMethod]
        public void ParseCommandFlags_ReadOutputFlag_SetsReadOutput()
        {
            var result = InvokeParseCommandFlags("r:mycommand");
            Assert.IsTrue(Get<bool>(result, "ReadOutput"));
        }

        [TestMethod]
        public void ParseCommandFlags_LockFlag_SetsUseLocking()
        {
            var result = InvokeParseCommandFlags("l:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseLocking"));
        }

        // Helper for nullable property
        private static object? obj_get(object obj, string prop) =>
            obj.GetType().GetProperty(prop)!.GetValue(obj);
    }

    [TestClass]
    public class ShouldExecuteCommandTests
    {
        private static bool InvokeShouldExecute(
            bool onlyTrack,
            bool onlyAlbum,
            bool isAlbum,
            JobTerminalOutcome terminalOutcome = JobTerminalOutcome.Succeeded,
            JobSkipReason skipReason = JobSkipReason.None,
            int? requiredResultCode = null)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ShouldExecuteCommand", BindingFlags.NonPublic | BindingFlags.Static)!;

            // Build a CommandConfig struct via reflection
            var configType = typeof(OnCompleteExecutor).GetNestedType("CommandConfig", BindingFlags.NonPublic)!;
            var config = Activator.CreateInstance(configType)!;
            configType.GetProperty("OnlyTrackOnComplete")!.SetValue(config, onlyTrack);
            configType.GetProperty("OnlyAlbumOnComplete")!.SetValue(config, onlyAlbum);
            configType.GetProperty("RequiredResultCode")!.SetValue(config, requiredResultCode);

            var outcome = terminalOutcome switch
            {
                JobTerminalOutcome.Succeeded => JobOutcome.Done(),
                JobTerminalOutcome.Failed => JobOutcome.Failed(JobFailureReason.Other),
                JobTerminalOutcome.Skipped => JobOutcome.Skipped(skipReason),
                JobTerminalOutcome.Cancelled => JobOutcome.Cancelled(JobCancellationSource.UserRequestedJob),
                JobTerminalOutcome.PartialSuccess => JobOutcome.PartialSuccess(),
                _ => JobOutcome.NoChange(),
            };

            return (bool)method.Invoke(null, new object[] { config, outcome, isAlbum })!;
        }

        [TestMethod]
        public void ShouldExecute_NoFlags_AlwaysTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, false, false));
            Assert.IsTrue(InvokeShouldExecute(false, false, true));
        }

        [TestMethod]
        public void ShouldExecute_TrackOnly_OnAlbum_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(true, false, true));
        }

        [TestMethod]
        public void ShouldExecute_TrackOnly_OnTrack_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(true, false, false));
        }

        [TestMethod]
        public void ShouldExecute_AlbumOnly_OnTrack_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(false, true, false));
        }

        [TestMethod]
        public void ShouldExecute_AlbumOnly_OnAlbum_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, true, true));
        }

        [TestMethod]
        public void ShouldExecute_AlbumOnly_OnAlbumTrackCompletion_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(false, true, false));
        }

        [TestMethod]
        public void ShouldExecute_RequiredSuccess_OnSucceededJob_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, false, false, requiredResultCode: 1));
        }

        [TestMethod]
        public void ShouldExecute_RequiredSuccess_OnFailedJob_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(false, false, false, JobTerminalOutcome.Failed, requiredResultCode: 1));
        }

        [TestMethod]
        public void ShouldExecute_RequiredFailure_OnFailedJob_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, false, false, JobTerminalOutcome.Failed, requiredResultCode: 2));
        }

        [TestMethod]
        public void ShouldExecute_RequiredFailure_OnPartialSuccessJob_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, false, false, JobTerminalOutcome.PartialSuccess, requiredResultCode: 2));
        }

        [TestMethod]
        public void ShouldExecute_NoResultPrefix_OnSkippedTrack_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(false, false, false, JobTerminalOutcome.Skipped, JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_NoResultPrefix_OnSkippedAlbum_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(false, false, true, JobTerminalOutcome.Skipped, JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_ExplicitSkippedPrefix_OnSkippedTrack_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, false, false, JobTerminalOutcome.Skipped, JobSkipReason.AlreadyExists, requiredResultCode: 5));
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
                    $"1:a:h:{AppendMarkerCommand(markerPath, "album")}",
                    $"1:t:h:{AppendMarkerCommand(markerPath, "track")}",
                    $"2:a:h:{AppendMarkerCommand(markerPath, "failed")}",
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
                    $"1:a:h:{AppendMarkerCommand(markerPath, "album")}",
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
        public void HasApplicableCommand_AlbumOnlyCommand_DoesNotApplyToAlbumTrackCompletion()
        {
            var settings = new DownloadSettings();
            settings.Output.OnComplete = ["1:a:notify"];

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
                    $"h:{AppendMarkerCommand(markerPath, "ran")}",
                    $"a:h:{AppendMarkerCommand(markerPath, "album")}",
                    $"t:h:{AppendMarkerCommand(markerPath, "track")}",
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
                    $"1:a:h:{AppendMarkerCommand(markerPath, "{title}|{artist}|{album}|{sartist}|{salbum}|{path}")}",
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
                    $"TagTitle|TagArtist|TagAlbum|AlbumSourceArtist|AlbumSourceAlbum|{albumPath}",
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
            return ((string)fields[0].GetValue(result)!, (string)fields[2].GetValue(result)!);
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
        public void FormatProcessArgumentsForLog_NonShellExecute_UsesArgumentList()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("win-notify-send.cmd");
            startInfo.ArgumentList.Add("Downloaded: Sonic Youth C:/Music");

            Assert.AreEqual(
                "ArgumentList=['/d', '/c', 'win-notify-send.cmd', 'Downloaded: Sonic Youth C:/Music']",
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
