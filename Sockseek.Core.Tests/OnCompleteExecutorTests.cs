using Sockseek.Api;
using Sockseek.Core.Events;
using System.Reflection;
using System.Diagnostics;

namespace Tests.OnCompleteExecutorTests
{
    // OnCompleteExecutor has private methods tested via reflection,
    // following the established pattern in this test suite.

    [TestClass]
    public class RemoteJobContextTests
    {
        [TestMethod]
        public void RemoteFileContext_ExposesCommonPathAndExactTargetWithoutTagSource()
        {
            var target = new PeerFileTarget(
                new PeerFileIdentity("Peer", @"Share\Documents\notes.txt"),
                size: 12,
                extension: ".txt");
            var job = new RemoteFileJob(target)
            {
                DownloadPath = Path.GetFullPath("notes.txt"),
            };

            var (variables, tagSourcePath) = BuildJobContext(job);

            Assert.AreSame(job, variables.Job);
            Assert.AreSame(target, variables.PeerTarget);
            Assert.AreEqual(job.DownloadPath, variables.DownloadPath);
            Assert.IsNull(tagSourcePath);

            Assert.IsTrue(FileManager.TryResolveNameFormatVariable("path", variables, () => null, out var path));
            Assert.IsTrue(FileManager.TryResolveNameFormatVariable("path-noext", variables, () => null, out var pathNoExt));
            Assert.IsTrue(FileManager.TryResolveNameFormatVariable("is-audio", variables, () => null, out var isAudio));
            Assert.AreEqual(Path.GetFullPath(job.DownloadPath), path.Value);
            Assert.AreEqual(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(job.DownloadPath)) ?? "",
                Path.GetFileNameWithoutExtension(job.DownloadPath)), pathNoExt.Value);
            Assert.AreEqual("false", isAudio.Value);
        }

        private static (FileManagerContext Variables, string? TagSourcePath) BuildJobContext(Job job)
        {
            var method = typeof(OnCompleteExecutor).GetMethod(
                "BuildJobOnCompleteContext",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var context = method.Invoke(null, [job])!;
            var type = context.GetType();
            return (
                (FileManagerContext)type.GetProperty("Variables")!.GetValue(context)!,
                (string?)type.GetProperty("TagSourcePath")!.GetValue(context));
        }
    }

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
        public void ParseCommand_FileScope_SetsScope()
        {
            var result = InvokeParseCommand("scope=file -- mycommand");
            Assert.AreEqual("File", Get<object>(result, "Scope").ToString());
        }

        [TestMethod]
        public void ParseCommand_FolderScope_SetsScope()
        {
            var result = InvokeParseCommand("scope=folder -- mycommand");
            Assert.AreEqual("Folder", Get<object>(result, "Scope").ToString());
        }

        [TestMethod]
        public void ParseCommand_MultipleOptions_AreParsed()
        {
            var result = InvokeParseCommand("when=success scope=folder shell hidden -- mycommand");
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.IsTrue(Get<bool>(result, "CreateNoWindow"));
            Assert.AreEqual("Folder", Get<object>(result, "Scope").ToString());
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
            var ex = Assert.ThrowsExactly<ArgumentException>(() => InvokeParseCommand("mycommand"));
            StringAssert.Contains(ex.Message, "Missing `--` command delimiter");
        }

        [TestMethod]
        public void ParseCommand_LegacyPrefixSyntax_ThrowsMigrationHint()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(() => InvokeParseCommand("1:a:h: mycommand"));
            StringAssert.Contains(ex.Message, "Legacy one-letter prefixes are no longer supported");
        }

        [TestMethod]
        public void ParseCommand_EmptyCommandAfterDelimiter_Throws()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(() => InvokeParseCommand("hidden -- "));
            StringAssert.Contains(ex.Message, "Command after `--` is empty");
        }

        [TestMethod]
        public void ParseCommand_UnknownOption_Throws()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(() => InvokeParseCommand("bogus -- mycommand"));
            StringAssert.Contains(ex.Message, "Unknown option `bogus`");
        }
    }

    [TestClass]
    public class ProcessCommandResultTests
    {
        [TestMethod]
        [DataRow("success;unfinished.opus")]
        [DataRow("failed")]
        public void ProcessCommandResult_NonZeroExitIgnoresIndexDirective(string stdout)
        {
            var result = new OnCompleteExecutor.ProcessResult { ExitCode = 1, Stdout = stdout };
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            var downloaded = JobOutcome.Done("original.flac");

            var processed = Process(result, null, job, downloaded);

            Assert.IsFalse(processed.NeedsIndexUpdate);
            Assert.AreEqual(downloaded, processed.Outcome);
        }

        [TestMethod]
        public void ProcessCommandResult_UpdateIndexWithTruncatedStdout_DoesNotMutateSongPath()
        {
            var result = new OnCompleteExecutor.ProcessResult { ExitCode = 0, Stdout = "ignored;C:/partial/path", StdoutTruncated = true };
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
            {
                DownloadPath = "C:/old/path.flac",
            };
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

            var processed = Process(result, song, job);

            Assert.IsFalse(processed.NeedsIndexUpdate);
            Assert.AreEqual("C:/old/path.flac", song.DownloadPath);
        }

        [TestMethod]
        public void ProcessCommandResult_UpdateIndexWithSuccessStateAndPath_ReturnsOutcomeWithoutMutatingSong()
        {
            var result = new OnCompleteExecutor.ProcessResult { ExitCode = 0, Stdout = "success;C:/new/path.mp3" };
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
            {
                DownloadPath = "C:/old/path.flac",
            };
            song.Fail(JobFailureReason.AllDownloadsFailed);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

            var processed = Process(result, song, job, JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, downloadPath: song.DownloadPath));

            Assert.IsTrue(processed.NeedsIndexUpdate);
            Assert.AreEqual(JobTerminalOutcome.Failed, song.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, song.FailureReason);
            Assert.AreEqual("C:/old/path.flac", song.DownloadPath);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, processed.Outcome.TerminalOutcome);
            Assert.AreEqual("C:/new/path.mp3", processed.Outcome.DownloadPath);

            JobOutcomeCommitter.Commit(song, processed.Outcome);

            Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.None, song.FailureReason);
            Assert.AreEqual("C:/new/path.mp3", song.DownloadPath);
        }

        [TestMethod]
        public void ProcessCommandResult_UpdateIndexWithFailedState_ReturnsFailureOutcomeThatClearsPath()
        {
            var result = new OnCompleteExecutor.ProcessResult { ExitCode = 0, Stdout = "failed" };
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
            {
                DownloadPath = "C:/old/path.flac",
            };
            song.SetDone(song.DownloadPath);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

            var processed = Process(result, song, job, JobOutcome.Done(song.DownloadPath));

            Assert.IsTrue(processed.NeedsIndexUpdate);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
            Assert.AreEqual("C:/old/path.flac", song.DownloadPath);
            Assert.AreEqual(JobTerminalOutcome.Failed, processed.Outcome.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.Other, processed.Outcome.FailureReason);
            Assert.IsTrue(processed.Outcome.ShouldUpdateDownloadPath);
            Assert.IsNull(processed.Outcome.DownloadPath);

            JobOutcomeCommitter.Commit(song, processed.Outcome);

            Assert.AreEqual(JobTerminalOutcome.Failed, song.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.Other, song.FailureReason);
            Assert.IsNull(song.DownloadPath);
        }

        [TestMethod]
        public void ProcessCommandResult_UpdateIndexWithFailedStateAndPath_IgnoresPathAndClearsStoredPath()
        {
            var result = new OnCompleteExecutor.ProcessResult { ExitCode = 0, Stdout = "failed;C:/new/path.mp3" };
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
            {
                DownloadPath = "C:/old/path.flac",
            };
            song.SetDone(song.DownloadPath);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

            var processed = Process(result, song, job, JobOutcome.Done(song.DownloadPath));

            Assert.IsTrue(processed.NeedsIndexUpdate);
            Assert.AreEqual(JobTerminalOutcome.Failed, processed.Outcome.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.Other, processed.Outcome.FailureReason);
            Assert.IsTrue(processed.Outcome.ShouldUpdateDownloadPath);
            Assert.IsNull(processed.Outcome.DownloadPath);
            Assert.AreEqual("C:/old/path.flac", song.DownloadPath);

            JobOutcomeCommitter.Commit(song, processed.Outcome);

            Assert.AreEqual(JobTerminalOutcome.Failed, song.TerminalOutcome);
            Assert.IsNull(song.DownloadPath);
        }

        [TestMethod]
        public void ProcessCommandResult_UpdateIndexWithIgnoredState_ReturnsPathOnlyOutcome()
        {
            var result = new OnCompleteExecutor.ProcessResult { ExitCode = 0, Stdout = "ignored;C:/new/path.mp3" };
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
            {
                DownloadPath = "C:/old/path.flac",
            };
            song.Fail(JobFailureReason.AllDownloadsFailed);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });

            var processed = Process(result, song, job, JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, downloadPath: song.DownloadPath));

            Assert.IsTrue(processed.NeedsIndexUpdate);
            Assert.AreEqual(JobTerminalOutcome.Failed, song.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, song.FailureReason);
            Assert.AreEqual("C:/old/path.flac", song.DownloadPath);
            Assert.AreEqual(JobTerminalOutcome.Failed, processed.Outcome.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, processed.Outcome.FailureReason);
            Assert.AreEqual("C:/new/path.mp3", processed.Outcome.DownloadPath);

            JobOutcomeCommitter.Commit(song, processed.Outcome);

            Assert.AreEqual(JobTerminalOutcome.Failed, song.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, song.FailureReason);
            Assert.AreEqual("C:/new/path.mp3", song.DownloadPath);
        }

        [TestMethod]
        public void ProcessCommandResult_UpdateIndexWithUnsupportedState_DoesNotApplyStateOrPath()
        {
            var result = new OnCompleteExecutor.ProcessResult { ExitCode = 0, Stdout = "already-exists;C:/new/path.mp3" };
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
            {
                DownloadPath = "C:/old/path.flac",
            };
            song.Fail(JobFailureReason.AllDownloadsFailed);
            var job = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
            var failed = JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, downloadPath: song.DownloadPath);

            var processed = Process(result, song, job, failed);

            Assert.IsFalse(processed.NeedsIndexUpdate);
            Assert.AreEqual(JobTerminalOutcome.Failed, processed.Outcome.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, processed.Outcome.FailureReason);
            Assert.AreEqual("C:/old/path.flac", processed.Outcome.DownloadPath);
            Assert.AreEqual(JobTerminalOutcome.Failed, song.TerminalOutcome);
            Assert.AreEqual("C:/old/path.flac", song.DownloadPath);
        }

        private static OnCompleteExecutor.CommandProcessingResult Process(
            OnCompleteExecutor.ProcessResult result, SongJob? song, Job job, JobOutcome? outcome = null,
            bool updateIndex = true)
            => OnCompleteExecutor.ProcessCommandResult(result, updateIndex, song, job,
                outcome ?? JobOutcome.Done(song?.DownloadPath),
                new(song ?? job, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }

    [TestClass]
    public class ShouldExecuteCommandTests
    {
        private static bool InvokeShouldExecute(
            string command,
            bool folder,
            JobTerminalOutcome terminalOutcome = JobTerminalOutcome.Succeeded,
            JobSkipReason skipReason = JobSkipReason.None)
        {
            var outcome = terminalOutcome switch
            {
                JobTerminalOutcome.Succeeded => JobOutcome.Done(),
                JobTerminalOutcome.Failed => JobOutcome.Failed(JobFailureReason.Other),
                JobTerminalOutcome.Skipped => JobOutcome.Skipped(skipReason),
                JobTerminalOutcome.Cancelled => JobOutcome.Cancelled(JobCancellationSource.UserRequestedJob),
                JobTerminalOutcome.PartialSuccess => JobOutcome.PartialSuccess(),
                _ => JobOutcome.NoChange(),
            };

            Job job = folder ? new AlbumJob(new AlbumQuery()) : new SongJob(new SongQuery());
            job.Config = new DownloadSettings { Output = { OnComplete = [command] } };
            return OnCompleteExecutor.HasApplicableCommand(job, job as SongJob, outcome);
        }

        [TestMethod]
        public void ShouldExecute_Default_RunsForNonSkippedOutcomes()
        {
            Assert.IsTrue(InvokeShouldExecute("-- cmd", folder: false));
            Assert.IsTrue(InvokeShouldExecute("-- cmd", folder: true, terminalOutcome: JobTerminalOutcome.Failed));
            Assert.IsFalse(InvokeShouldExecute("-- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_FileScope_OnlyRunsForFileContext()
        {
            Assert.IsTrue(InvokeShouldExecute("scope=file -- cmd", folder: false));
            Assert.IsFalse(InvokeShouldExecute("scope=file -- cmd", folder: true));
        }

        [TestMethod]
        public void ShouldExecute_FolderScope_OnlyRunsForFolderContext()
        {
            Assert.IsTrue(InvokeShouldExecute("scope=folder -- cmd", folder: true));
            Assert.IsFalse(InvokeShouldExecute("scope=folder -- cmd", folder: false));
        }

        [TestMethod]
        public void ShouldExecute_SuccessWhen_OnlyRunsForSucceeded()
        {
            Assert.IsTrue(InvokeShouldExecute("when=success -- cmd", folder: false));
            Assert.IsFalse(InvokeShouldExecute("when=success -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Failed));
        }

        [TestMethod]
        public void ShouldExecute_FailureWhen_RunsForFailedAndPartialSuccess()
        {
            Assert.IsTrue(InvokeShouldExecute("when=failure -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Failed));
            Assert.IsTrue(InvokeShouldExecute("when=failure -- cmd", folder: true, terminalOutcome: JobTerminalOutcome.PartialSuccess));
            Assert.IsFalse(InvokeShouldExecute("when=failure -- cmd", folder: false));
        }

        [TestMethod]
        public void ShouldExecute_SkippedWhen_RunsForAnySkippedOutcome()
        {
            Assert.IsTrue(InvokeShouldExecute("when=skipped -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_AlreadyExistsWhen_RunsOnlyForAlreadyExistsSkippedOutcome()
        {
            Assert.IsTrue(InvokeShouldExecute("when=already-exists -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
            Assert.IsFalse(InvokeShouldExecute("when=already-exists -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.NotFoundLastTime));
        }

        [TestMethod]
        public void ShouldExecute_NotFoundLastTimeWhen_RunsOnlyForNotFoundLastTimeSkippedOutcome()
        {
            Assert.IsTrue(InvokeShouldExecute("when=not-found-last-time -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.NotFoundLastTime));
            Assert.IsFalse(InvokeShouldExecute("when=not-found-last-time -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }

        [TestMethod]
        public void ShouldExecute_CancelledWhen_RunsOnlyForCancelled()
        {
            Assert.IsTrue(InvokeShouldExecute("when=cancelled -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Cancelled));
            Assert.IsFalse(InvokeShouldExecute("when=cancelled -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Failed));
        }

        [TestMethod]
        public void ShouldExecute_AnyWhen_RunsForSkippedToo()
        {
            Assert.IsTrue(InvokeShouldExecute("when=any -- cmd", folder: false, terminalOutcome: JobTerminalOutcome.Skipped, skipReason: JobSkipReason.AlreadyExists));
        }
    }

    [TestClass]
    public class ExecuteAsyncTests
    {
        [TestMethod]
        public async Task ExecuteAsync_NonZeroExitPublishesBoundedActivityWithoutProcessOutput()
        {
            var settings = new DownloadSettings();
            settings.Output.OnComplete = [$"hidden -- {FailingOutputCommand()}"];
            var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
            {
                Config = settings,
            };
            var events = new DownloadEvents();
            var messages = new List<JobMessageChange>();
            events.JobMessage += messages.Add;

            await OnCompleteExecutor.ExecuteAsync(
                album,
                null,
                new JobContext(),
                JobOutcome.Done(),
                events);

            JobMessageChange warning = messages.Single();
            Assert.AreEqual(LogLevel.Warning, warning.Level);
            Assert.AreEqual("on-complete command exited with code 7", warning.Message);
            Assert.IsFalse(warning.Message.Contains("PRIVATE-STDOUT", StringComparison.Ordinal));
            Assert.IsFalse(warning.Message.Contains("PRIVATE-STDERR", StringComparison.Ordinal));
        }

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
                    $"when=success scope=folder hidden -- {AppendMarkerCommand(markerPath, "album")}",
                    $"when=success scope=file hidden -- {AppendMarkerCommand(markerPath, "track")}",
                    $"when=failure scope=folder hidden -- {AppendMarkerCommand(markerPath, "failed")}",
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
        [DataRow(false)]
        [DataRow(true)]
        public async Task Engine_FolderCompletionRespectsExplicitAndOmittedScope(bool genericFolder)
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
                settings.Extraction.Input = genericFolder ? "slsk://local/Main/TestArtist/TestAlbum/" : "TestArtist TestAlbum";
                if (!genericFolder) settings.Extraction.RequestedMode = ExtractionMode.Album;
                settings.Output.ParentDir = outputDir;
                string scope = genericFolder ? "" : "scope=folder";
                settings.Output.OnComplete =
                [
                    $"when=success {scope} lock hidden -- {AppendMarkerCommand(markerPath, "{path}")}",
                ];

                var client = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);
                var app = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));
                var albumActivityPhases = new List<JobActivityPhase>();
                app.Events.JobActivityChanged += change =>
                {
                    if (change.Job.Kind is Sockseek.Core.Snapshots.JobSnapshotKind.Album or Sockseek.Core.Snapshots.JobSnapshotKind.RemoteDirectory)
                        albumActivityPhases.Add(change.Phase);
                };
                app.Enqueue(new ExtractJob(settings.Extraction.Input, settings.Extraction.InputType), settings);
                app.CompleteEnqueue();

                await app.RunAsync(CancellationToken.None);

                var folder = app.Queue.AllJobs().OfType<DirectoryDownloadJob>().Single();
                Assert.AreEqual(JobTerminalOutcome.Succeeded, folder.TerminalOutcome);
                CollectionAssert.Contains(albumActivityPhases, JobActivityPhase.RunningOnComplete);
                var calls = File.ReadAllLines(markerPath);
                Assert.AreEqual(folder.DownloadPath, calls[^1]);
                var files = genericFolder ? folder.FileJobs.Select(file => file.DownloadPath).ToArray() : [];
                CollectionAssert.AreEquivalent(files, calls[..^1]);
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
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task AlbumRetryPreservesNestedPlacementAndRunsOnlyNewCompletionHooks(bool finishFolder, bool lastFile)
        {
            var output = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var names = new[] { @"PeerRoot\Album\Disc 1\one.mp3", @"PeerRoot\Album\Disc 2\two.mp3", @"PeerRoot\Album\Disc 2\three.mp3" };
                var files = names.Select(name => TestHelpers.CreateSlFile(name, length: 180)).ToList();
                var response = new Soulseek.SearchResponse("alice", 1, true, 100_000, 0, files);
                var folder = new AlbumFolder("alice", @"PeerRoot\Album", files.Select(f => TestHelpers.CreateAlbumFile(response, f)).ToList());
                var kept = Path.Join(output, "exported-one.mp3");
                File.WriteAllText(kept, "already exported");
                var marker = Path.Join(output, "calls.txt");
                var previousOutput = Path.Join(output, "failed", "Album", "three.mp3");
                var restoredOutput = Path.Join(output, "Album", "three.mp3");
                if (lastFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(previousOutput)!);
                    File.WriteAllText(previousOutput, "retained audio");
                }
                var album = new AlbumJob(new AlbumQuery { Album = "Album" })
                {
                    ResolvedTarget = folder,
                    ValidationRequirement = AlbumValidationRequirement.UserAccepted,
                    InheritedFolders = ["My list"],
                    Retry = new(Guid.NewGuid(), 1, finishFolder ? null : new HashSet<string> { names[1] },
                        new Dictionary<string, string> { [names[0]] = kept, [names[2]] = previousOutput },
                        new Dictionary<string, string> { [names[2]] = restoredOutput }),
                };
                var settings = new DownloadSettings();
                settings.Output.ParentDir = output;
                settings.Output.NameFormat = "{default-folder}/{foldername}/{filename}";
                settings.Skip.SkipExisting = false;
                settings.Output.OnComplete = [
                    $"when=success scope=file lock hidden -- {AppendMarkerCommand(marker, "{path}")}",
                    $"when=success scope=folder hidden -- {AppendMarkerCommand(marker, "folder")}",
                ];
                var engine = new EngineSettings { Username = "test", Password = "test" };
                var client = new ClientTests.MockSoulseekClient([response]);
                var app = new DownloadEngine(engine, TestHelpers.CreateMockClientManager(client, engine));
                app.Enqueue(album, settings);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                var two = Path.Join(output, "My list", "Album", "Disc 2", "two.mp3");
                var three = Path.Join(Path.GetDirectoryName(two), "three.mp3");
                Assert.AreEqual(finishFolder || lastFile ? JobTerminalOutcome.Succeeded : JobTerminalOutcome.PartialSuccess, album.TerminalOutcome, album.FailureMessage);
                Assert.AreEqual("already exported", File.ReadAllText(kept));
                Assert.IsTrue(File.Exists(two), two);
                Assert.AreEqual(finishFolder, File.Exists(three));
                if (lastFile) Assert.AreEqual("retained audio", File.ReadAllText(restoredOutput));
                CollectionAssert.AreEquivalent(finishFolder ? new[] { two, three, "folder" } : lastFile ? [two, "folder"] : [two], File.ReadAllLines(marker));
            }
            finally { Directory.Delete(output, true); }
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
                    $"when=already-exists scope=file hidden -- {AppendMarkerCommand(markerPath, "already-exists")}",
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
            settings.Output.OnComplete = ["when=success scope=folder -- notify"];

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
                    $"scope=folder hidden -- {AppendMarkerCommand(markerPath, "album")}",
                    $"scope=file hidden -- {AppendMarkerCommand(markerPath, "track")}",
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
        public async Task ExecuteAsync_UpdateIndexStateAndPath_ReturnsAdjustedOutcomeWithoutMutatingSong()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var oldPath = Path.Combine(tempDir, "old.flac").Replace('\\', '/');
                var newPath = Path.Combine(tempDir, "new.mp3").Replace('\\', '/');
                var settings = new DownloadSettings();
                settings.Output.ParentDir = tempDir;
                settings.Output.OnComplete =
                [
                    $"when=failure update-index hidden -- {WriteStdoutCommand($"success;{newPath}")}",
                ];

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Config = settings,
                };
                var track = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track" })
                {
                    Config = settings,
                    DownloadPath = oldPath,
                };
                var failed = JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, downloadPath: oldPath);

                var outcome = await OnCompleteExecutor.ExecuteAsync(album, track, new JobContext(), failed);

                Assert.AreEqual(JobTerminalOutcome.None, track.TerminalOutcome);
                Assert.AreEqual(oldPath, track.DownloadPath);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, outcome.TerminalOutcome);
                Assert.AreEqual(newPath, outcome.DownloadPath);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_ChainedConditionsAndVariablesSeePriorUpdateIndexOutcome()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                string markerPath = Path.Combine(tempDir, "marker.txt");
                string oldPath = Path.Combine(tempDir, "old.flac").Replace('\\', '/');
                string newPath = Path.Combine(tempDir, "new.mp3").Replace('\\', '/');
                var settings = new DownloadSettings();
                settings.Output.ParentDir = tempDir;
                settings.Output.OnComplete =
                [
                    $"when=failure update-index hidden -- {WriteStdoutCommand($"success;{newPath}")}",
                    $"when=success hidden -- {AppendMarkerCommand(markerPath, "{terminal-outcome}|{path}")}",
                ];
                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Config = settings,
                };
                var track = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track" })
                {
                    Config = settings,
                    DownloadPath = oldPath,
                };

                JobOutcome outcome = await OnCompleteExecutor.ExecuteAsync(
                    album,
                    track,
                    new JobContext(),
                    JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, downloadPath: oldPath));

                Assert.AreEqual(JobTerminalOutcome.Succeeded, outcome.TerminalOutcome);
                Assert.AreEqual(newPath, outcome.DownloadPath);
                Assert.AreEqual(
                    $"Succeeded|{Path.GetFullPath(newPath)}",
                    File.ReadAllText(markerPath).Trim());
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_UpdateIndexStateAndPath_ReturnsAdjustedAlbumOutcome()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-oncomplete-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var oldPath = Path.Combine(tempDir, "old-album").Replace('\\', '/');
                var newPath = Path.Combine(tempDir, "new-album").Replace('\\', '/');
                var settings = new DownloadSettings();
                settings.Output.ParentDir = tempDir;
                settings.Output.OnComplete =
                [
                    $"when=failure scope=folder update-index hidden -- {WriteStdoutCommand($"success;{newPath}")}",
                ];

                var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                {
                    Config = settings,
                    DownloadPath = oldPath,
                };
                var failed = JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, downloadPath: oldPath);

                var outcome = await OnCompleteExecutor.ExecuteAsync(album, null, new JobContext(), failed);

                Assert.AreEqual(JobTerminalOutcome.None, album.TerminalOutcome);
                Assert.AreEqual(oldPath, album.DownloadPath);
                Assert.AreEqual(JobTerminalOutcome.Succeeded, outcome.TerminalOutcome);
                Assert.AreEqual(newPath, outcome.DownloadPath);

                JobOutcomeCommitter.Commit(album, outcome);

                Assert.AreEqual(JobTerminalOutcome.Succeeded, album.TerminalOutcome);
                Assert.AreEqual(newPath, album.DownloadPath);
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
                    $"when=success scope=folder hidden -- {AppendMarkerCommand(markerPath, "{title}|{artist}|{album}|{sartist}|{salbum}|{path}")}",
                ];

                var album = new AlbumJob(new AlbumQuery { Artist = "AlbumSourceArtist", Album = "AlbumSourceAlbum" })
                {
                    Config = settings,
                };
                album.SetDone(albumPath);

                var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, []);
                var candidate = SoulseekSearchAdapter.ToFileCandidate(response, new Soulseek.File(1, @"remote\Artist\Album\01.mp3", 100, ".mp3"));
                var firstAudio = new SongJob(new SongQuery { Artist = "TrackSourceArtist", Album = "TrackSourceAlbum", Title = "TrackSourceTitle" })
                {
                    Config = settings,
                    ResolvedTarget = candidate,
                };
                firstAudio.SetDone(tagPath, candidate);

                album.ResolvedTarget = new AlbumFolder(
                    "user",
                    @"remote\Artist\Album",
                    [new AlbumFile(firstAudio.Query, candidate)]);
                album.TrackJobs.Add(firstAudio);

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

        private static string FailingOutputCommand()
            => OperatingSystem.IsWindows()
                ? "cmd /d /c \"echo PRIVATE-STDOUT & echo PRIVATE-STDERR 1>&2 & exit /b 7\""
                : "/bin/sh -c \"echo PRIVATE-STDOUT; echo PRIVATE-STDERR >&2; exit 7\"";

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

    }
}
