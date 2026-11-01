using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Cli;

namespace Tests.ConfigTests
{
    internal static class ProfileTestHelpers
    {
        public static (ConfigFile File, DownloadSettings Root, CliSettings Cli, string[] Args) Bind(
            string path,
            string content,
            params string[] extraArgs)
        {
            File.WriteAllText(path, content);
            var file = ConfigManager.Load(path);
            var args = new[] { "test-input" }.Concat(extraArgs).ToArray();
            var (_, root, cli) = ConfigManager.Bind(file, args);
            return (file, root, cli, args);
        }

        public static DownloadSettings Resolve(ConfigFile file, DownloadSettings root, CliSettings cli, string[] args, Job job)
        {
            ConfigManager.ApplyAutoProfileCliSettings(file, root, cli);
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, cli);
            return resolver.Resolve(root, job);
        }

        public static ProfileContext Context(CliSettings cli)
        {
            var context = new ProfileContext();
            context.Values["interactive"] = cli.InteractiveMode;
            context.Values["progress-json"] = cli.ProgressJson;
            context.Values["no-progress"] = cli.NoProgress;
            return context;
        }
    }

    [TestClass]
    public class AutoProfileTests
    {
        private string testConfigPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Path.GetTempPath(), $"sockseek-profile-{Guid.NewGuid():N}.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath))
                File.Delete(testConfigPath);
        }

        private (ConfigFile File, DownloadSettings Root, CliSettings Cli, string[] Args) Bind(
            string content,
            params string[] extraArgs)
            => ProfileTestHelpers.Bind(testConfigPath, content, extraArgs);

        private static DownloadSettings Resolve(ConfigFile file, DownloadSettings root, CliSettings cli, string[] args, Job job)
            => ProfileTestHelpers.Resolve(file, root, cli, args, job);

        [TestMethod]
        public void Resolver_WithMultipleProfiles_AppliesCorrectSettings()
        {
            string content =
                "max-stale-time = 5\n" +
                "fast-search = true\n" +
                "format = flac\n" +
                "[profile-true-1]\n" +
                "profile-cond = input-type == \"youtube\" && download-mode == \"album\"\n" +
                "max-stale-time = 10\n" +
                "[profile-true-2]\n" +
                "profile-cond = !aggregate\n" +
                "fast-search = false\n" +
                "[profile-false-1]\n" +
                "profile-cond = input-type == \"string\"\n" +
                "format = mp3\n" +
                "[profile-no-cond]\n" +
                "format = opus";

            var (file, root, cli, args) = Bind(content, "--input-type", "youtube");
            var result = Resolve(file, root, cli, args, new AlbumJob(new AlbumQuery()));

            Assert.AreEqual(10, result.Search.MaxStaleTime);
            Assert.IsFalse(result.Search.FastSearch);
            Assert.IsNotNull(result.Search.NecessaryCond.Formats);
            Assert.AreEqual("flac", result.Search.NecessaryCond.Formats[0]);
            CollectionAssert.AreEquivalent(new[] { "profile-true-1", "profile-true-2" }, result.AppliedAutoProfiles.ToList());
        }

        [TestMethod]
        public void Resolver_WithInteractiveAndAlbum_AppliesCorrectStaleTime()
        {
            string content =
                "[no-stale]\n" +
                "profile-cond = interactive && download-mode == \"album\"\n" +
                "max-stale-time = 999999\n" +
                "[youtube]\n" +
                "profile-cond = input-type == \"youtube\"\n" +
                "yt-dlp = true";

            var (file, root, _, args) = Bind(content, "--interactive");
            var cli = new CliSettings { InteractiveMode = true };
            var result = Resolve(file, root, cli, args, new AlbumJob(new AlbumQuery()));

            Assert.AreEqual(999999, result.Search.MaxStaleTime);
            Assert.IsFalse(result.YtDlp.UseYtdlp);
        }

        [TestMethod]
        public void Resolver_WithInteractiveAndAlbum_AppliesCliOnlySettings()
        {
            string content =
                "[my-interactive]\n" +
                "profile-cond = interactive && album\n" +
                "max-stale-time = 9999999\n" +
                "no-progress = true";

            var (file, root, cli, args) = Bind(content, "--interactive", "--album");
            var result = Resolve(file, root, cli, args, new AlbumJob(new AlbumQuery()));

            Assert.AreEqual(9999999, result.Search.MaxStaleTime);
            Assert.IsTrue(cli.NoProgress);
        }

        [TestMethod]
        public void Resolver_ProfileCanEnableInteractiveBeforeInteractiveConditionMatches()
        {
            string content =
                "[album-ui]\n" +
                "profile-cond = album\n" +
                "interactive = true\n" +
                "[my-interactive]\n" +
                "profile-cond = interactive && album\n" +
                "max-stale-time = 9999999\n" +
                "no-progress = true";

            var (file, root, cli, args) = Bind(content, "--album");
            var result = Resolve(file, root, cli, args, new AlbumJob(new AlbumQuery()));

            Assert.IsTrue(cli.InteractiveMode);
            Assert.AreEqual(9999999, result.Search.MaxStaleTime);
            Assert.IsTrue(cli.NoProgress);
        }

        [TestMethod]
        public void Resolver_WithYouTubeInput_EnablesYtDlp()
        {
            string content =
                "[no-stale]\n" +
                "profile-cond = interactive && download-mode == \"album\"\n" +
                "max-stale-time = 999999\n" +
                "[youtube]\n" +
                "profile-cond = input-type == \"youtube\"\n" +
                "yt-dlp = true";

            var (file, root, _, args) = Bind(content, "--input-type", "youtube", "--interactive");
            var cli = new CliSettings { InteractiveMode = true };
            var result = Resolve(file, root, cli, args, new SongJob(new SongQuery { Title = "test" }));

            Assert.AreNotEqual(999999, result.Search.MaxStaleTime);
            Assert.IsTrue(result.YtDlp.UseYtdlp);
        }

        [TestMethod]
        public void AutoDetectedInputType_AppliesClientAndExtractionSettings()
        {
            string content =
                "[youtube]\n" +
                "profile-cond = input-type == \"youtube\"\n" +
                "no-progress = true\n" +
                "yt-dlp = true";

            const string input = "https://www.youtube.com/playlist?list=test";
            File.WriteAllText(testConfigPath, content);
            var file = ConfigManager.Load(testConfigPath);
            string[] args = [input];
            var (_, root, cli) = ConfigManager.Bind(file, args);

            ConfigManager.ApplyAutoProfileCliSettings(file, root, cli);
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, cli);
            var result = resolver.Resolve(root, new ExtractJob(input));

            Assert.AreEqual(InputType.None, root.Extraction.InputType,
                "The test must exercise automatic detection rather than an explicit input-type setting.");
            Assert.IsTrue(cli.NoProgress);
            Assert.IsTrue(result.YtDlp.UseYtdlp);
            CollectionAssert.Contains(result.AppliedAutoProfiles.ToList(), "youtube");
        }

        [TestMethod]
        public void CombinedInputTypeAndDownloadMode_ClassifiesSoulseekBeforeExtraction()
        {
            string content =
                "[generic-file]\n" +
                "profile-cond = input-type == \"soulseek\" && download-mode == \"generic-file\"\n" +
                "no-progress = true\n" +
                "name-format = {peer-username}/{filename}";

            const string input = "slsk://Peer/Share/File.bin";
            File.WriteAllText(testConfigPath, content);
            var file = ConfigManager.Load(testConfigPath);
            string[] args = [input];
            var (_, root, cli) = ConfigManager.Bind(file, args);

            ConfigManager.ApplyAutoProfileCliSettings(file, root, cli);
            var result = ConfigManager.CreateJobSettingsResolver(file, args, cli)
                .Resolve(root, new ExtractJob(input));

            Assert.IsTrue(cli.NoProgress);
            Assert.AreEqual("{peer-username}/{filename}", result.Output.NameFormat);
            CollectionAssert.Contains(result.AppliedAutoProfiles.ToList(), "generic-file");
        }

        [TestMethod]
        public void JobPreparer_WithResolver_AppliesAutoProfileInRuntimePath()
        {
            string content =
                "[album-auto]\n" +
                "profile-cond = download-mode == \"album\"\n" +
                "max-stale-time = 4242";

            var (file, root, cli, args) = Bind(content);
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, cli);
            var job = new AlbumJob(new AlbumQuery());

            JobPreparer.PrepareSubtree(job, root, resolver);

            Assert.AreEqual(4242, job.Config.Search.MaxStaleTime);
            CollectionAssert.Contains(job.Config.AppliedAutoProfiles.ToList(), "album-auto");
        }

        [TestMethod]
        public void JobPreparer_WithResolver_AppliesAutoProfileFormatInRuntimePath()
        {
            string content =
                "[album-auto]\n" +
                "profile-cond = download-mode == \"album\"\n" +
                "format = ogg";

            var (file, root, cli, args) = Bind(content);
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, cli);
            var job = new AlbumJob(new AlbumQuery());

            JobPreparer.PrepareSubtree(job, root, resolver);

            CollectionAssert.AreEqual(new[] { "ogg" }, job.Config.Search.NecessaryCond.Formats);
            CollectionAssert.Contains(job.Config.AppliedAutoProfiles.ToList(), "album-auto");
        }

        [TestMethod]
        public void SoulseekGenericAutoProfile_OverridesInheritedMusicNameFormat()
        {
            string content =
                "name-format = {artist}/{title}\n" +
                "[generic-file-layout]\n" +
                "profile-cond = download-mode == \"generic-file\"\n" +
                "name-format = {peer-username}/{filename}";

            var (file, root, cli, args) = Bind(content);
            var result = Resolve(
                file,
                root,
                cli,
                args,
                new ExtractJob("slsk://Peer/Share/File.bin"));

            Assert.AreEqual("{peer-username}/{filename}", result.Output.NameFormat);
            CollectionAssert.Contains(result.AppliedAutoProfiles.ToList(), "generic-file-layout");
        }

        [TestMethod]
        public void AutoProfile_WithEngineSetting_Throws()
        {
            string content =
                "[bad-auto]\n" +
                "profile-cond = download-mode == \"album\"\n" +
                "connect-timeout = 1000";

            var (file, root, cli, args) = Bind(content);

            Assert.ThrowsExactly<Exception>(() => ConfigManager.CreateJobSettingsResolver(file, args, cli));
        }

        [TestMethod]
        public void Resolver_DoesNotDuplicateAppendableArgs()
        {
            string content =
                "on-complete = + -- action_default\n" +
                "[auto-profile]\n" +
                "profile-cond = interactive\n" +
                "fast-search = true";

            var (file, root, _, args) = Bind(content, "--interactive", "--on-complete", "+ -- action_cli");
            var cli = new CliSettings { InteractiveMode = true };
            var result = Resolve(file, root, cli, args, new SongJob(new SongQuery { Title = "test" }));

            Assert.IsTrue(result.Search.FastSearch);
            Assert.AreEqual(2, result.Output.OnComplete!.Count);
            Assert.AreEqual("-- action_default", result.Output.OnComplete[0]);
            Assert.AreEqual("-- action_cli", result.Output.OnComplete[1]);
        }
    }

    [TestClass]
    public class ProfilePriorityOrderTests
    {
        private string testConfigPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Path.GetTempPath(), $"sockseek-profile-priority-{Guid.NewGuid():N}.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath)) File.Delete(testConfigPath);
        }

        private (ConfigFile File, DownloadSettings Root, CliSettings Cli, string[] Args) Bind(
            string content,
            params string[] extraArgs)
            => ProfileTestHelpers.Bind(testConfigPath, content, extraArgs);

        private static DownloadSettings Resolve(ConfigFile file, DownloadSettings root, CliSettings cli, string[] args)
            => ProfileTestHelpers.Resolve(file, root, cli, args, new SongJob(new SongQuery { Title = "test" }));

        private static DownloadSettings Resolve(ConfigFile file, DownloadSettings root, CliSettings cli, string[] args, Job job)
            => ProfileTestHelpers.Resolve(file, root, cli, args, job);

        [TestMethod]
        public void Priority_DefaultAppliesWhenNoAutoProfileMatches()
        {
            var (file, root, _, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = false }, args);

            Assert.AreEqual(1, result.Search.MaxStaleTime);
            Assert.AreEqual(0, result.AppliedAutoProfiles.Count);
        }

        [TestMethod]
        public void Priority_AutoProfileOverridesDefault()
        {
            var (file, root, _, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2",
                "--interactive");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.AreEqual(2, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_ManualProfileOverridesAutoProfile()
        {
            var (file, root, _, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[manual]\nmax-stale-time = 3",
                "--interactive", "--profile", "manual");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.AreEqual(3, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_CliArgsOverrideManualProfile()
        {
            var (file, root, _, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[manual]\nmax-stale-time = 3",
                "--interactive", "--profile", "manual", "--max-stale-time", "4");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.AreEqual(4, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_CliArgsOverrideAutoProfile()
        {
            var (file, root, _, args) = Bind(
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2",
                "--interactive", "--max-stale-time", "4");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.AreEqual(4, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_MultipleAutoProfilesApplyInOrder()
        {
            var (file, root, _, args) = Bind(
                "[first]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[second]\nprofile-cond = album\nmax-stale-time = 20",
                "--interactive", "--album");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args, new AlbumJob(new AlbumQuery()));

            Assert.AreEqual(20, result.Search.MaxStaleTime);
            CollectionAssert.AreEqual(new[] { "first", "second" }, result.AppliedAutoProfiles.ToList());
        }

        [TestMethod]
        public void Priority_TwoAutoProfiles_EachSetsDistinctSetting()
        {
            var (file, root, _, args) = Bind(
                "[first]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[second]\nprofile-cond = album\nfast-search = true",
                "--interactive", "--album");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args, new AlbumJob(new AlbumQuery()));

            Assert.AreEqual(10, result.Search.MaxStaleTime);
            Assert.IsTrue(result.Search.FastSearch);
        }

        [TestMethod]
        public void Priority_RuntimeFields_PreservedOnResult()
        {
            var (file, root, _, args) = Bind(
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10",
                "--interactive", "--input-type", "youtube", "--album", "--aggregate");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.AreEqual(InputType.YouTube, result.Extraction.InputType);
            Assert.IsTrue(result.Extraction.IsAlbum);
            Assert.IsTrue(result.Search.IsAggregate);
        }
    }

    [TestClass]
    public class ProfileResolveVariationTests
    {
        private string testConfigPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Path.GetTempPath(), $"sockseek-profile-variation-{Guid.NewGuid():N}.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath)) File.Delete(testConfigPath);
        }

        private (ConfigFile File, DownloadSettings Root, CliSettings Cli, string[] Args) Bind(string content)
            => ProfileTestHelpers.Bind(testConfigPath, content);

        [TestMethod]
        public void Resolve_DifferentJobTypes_ReevaluatesAutoProfiles()
        {
            var (file, root, cli, args) = Bind(
                "max-stale-time = 5\n" +
                "[album-auto]\nprofile-cond = download-mode == \"album\"\nmax-stale-time = 10");
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, cli);

            var album = resolver.Resolve(root, new AlbumJob(new AlbumQuery()));
            var song = resolver.Resolve(root, new SongJob(new SongQuery { Title = "test" }));

            Assert.AreEqual(10, album.Search.MaxStaleTime);
            CollectionAssert.Contains(album.AppliedAutoProfiles.ToList(), "album-auto");
            Assert.AreEqual(5, song.Search.MaxStaleTime);
            Assert.AreEqual(0, song.AppliedAutoProfiles.Count);
        }

        [TestMethod]
        public void Resolve_AppendableAutoProfile_DoesNotLeakBetweenJobs()
        {
            var (file, root, _, args) = ProfileTestHelpers.Bind(
                testConfigPath,
                "on-complete = + -- action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + -- action_profile",
                "--interactive");
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, new CliSettings { InteractiveMode = true });

            var first = resolver.Resolve(root, new SongJob(new SongQuery { Title = "a" }));
            first.Output.OnComplete!.Add("-- mutated");
            var second = resolver.Resolve(root, new SongJob(new SongQuery { Title = "b" }));

            CollectionAssert.Contains(second.Output.OnComplete, "-- action_default");
            CollectionAssert.Contains(second.Output.OnComplete, "-- action_profile");
            CollectionAssert.DoesNotContain(second.Output.OnComplete, "-- mutated");
            Assert.AreEqual(2, second.Output.OnComplete!.Count);
        }

        [TestMethod]
        public void Resolve_MultipleProfiles_ActiveProfileSettingRetainedWhenAnotherDoesNotMatch()
        {
            var (file, root, _, args) = ProfileTestHelpers.Bind(
                testConfigPath,
                "max-stale-time = 1\n" +
                "[profile-a]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[profile-b]\nprofile-cond = download-mode == \"album\"\nfast-search = true",
                "--interactive");
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, new CliSettings { InteractiveMode = true });

            var album = resolver.Resolve(root, new AlbumJob(new AlbumQuery()));
            var song = resolver.Resolve(root, new SongJob(new SongQuery { Title = "test" }));

            Assert.AreEqual(10, album.Search.MaxStaleTime);
            Assert.IsTrue(album.Search.FastSearch);
            Assert.AreEqual(10, song.Search.MaxStaleTime);
            Assert.IsFalse(song.Search.FastSearch);
            CollectionAssert.AreEqual(new[] { "profile-a" }, song.AppliedAutoProfiles.ToList());
        }
    }

    [TestClass]
    public class ProfileEdgeCaseTests
    {
        private string testConfigPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Path.GetTempPath(), $"sockseek-profile-edge-{Guid.NewGuid():N}.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath)) File.Delete(testConfigPath);
        }

        private (ConfigFile File, DownloadSettings Root, CliSettings Cli, string[] Args) Bind(
            string content,
            params string[] extraArgs)
            => ProfileTestHelpers.Bind(testConfigPath, content, extraArgs);

        private static DownloadSettings Resolve(ConfigFile file, DownloadSettings root, CliSettings cli, string[] args)
            => ProfileTestHelpers.Resolve(file, root, cli, args, new SongJob(new SongQuery { Title = "test" }));

        [TestMethod]
        public void EdgeCase_DefaultSectionProfileCondIsIgnored()
        {
            var (file, root, _, args) = Bind(
                "[default]\nprofile-cond = interactive\nmax-stale-time = 99");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.IsFalse(file.HasAutoProfiles);
            Assert.AreEqual(99, result.Search.MaxStaleTime);
            Assert.AreEqual(0, result.AppliedAutoProfiles.Count);
        }

        [TestMethod]
        public void EdgeCase_ProfileWithoutCondNotConsideredForAutoResolve()
        {
            var (file, root, _, args) = Bind(
                "max-stale-time = 1\n" +
                "[manual-only]\nmax-stale-time = 5");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.IsFalse(file.HasAutoProfiles);
            Assert.AreEqual(1, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void EdgeCase_ManualProfileNotAppliedUnlessSelected()
        {
            var (file, root, _, args) = Bind(
                "max-stale-time = 1\n" +
                "[named]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[extra]\nmax-stale-time = 99",
                "--interactive");

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

            Assert.AreEqual(2, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void EdgeCase_MultipleManualProfilesAppliedInOrder()
        {
            var (file, root, _, args) = Bind(
                "[p1]\nmax-stale-time = 10\n" +
                "[p2]\nfast-search = true",
                "--profile", "p1,p2");

            var result = Resolve(file, root, new CliSettings(), args);

            Assert.AreEqual(10, result.Search.MaxStaleTime);
            Assert.IsTrue(result.Search.FastSearch);
        }
    }

    [TestClass]
    public class ProfileConditionTests
    {
        private DownloadSettings dl = null!;
        private CliSettings cli = null!;

        [TestInitialize]
        public void Setup()
        {
            dl = new DownloadSettings();
            dl.Extraction.InputType = InputType.YouTube;
            dl.Extraction.IsAlbum = true;
            dl.Search.IsAggregate = false;
            cli = new CliSettings { InteractiveMode = true };
        }

        private bool Satisfied(string condition, Job? job = null)
            => ProfileConditionEvaluator.Satisfied(condition, dl, job, ProfileTestHelpers.Context(cli));

        [TestMethod]
        public void ProfileConditionEvaluator_WithSimpleConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(Satisfied("input-type == \"youtube\""));
            Assert.IsTrue(Satisfied("download-mode == \"album\""));
            Assert.IsFalse(Satisfied("aggregate"));
            Assert.IsTrue(Satisfied("interactive"));
            Assert.IsTrue(Satisfied("album"));
            Assert.IsFalse(Satisfied("!interactive"));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_WithComplexConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(Satisfied("album && input-type == \"youtube\""));
            Assert.IsFalse(Satisfied("album && input-type != \"youtube\""));
            Assert.IsFalse(Satisfied("(interactive && aggregate)"));
            Assert.IsTrue(Satisfied("album && (interactive || aggregate)"));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_WithComplexOrConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(Satisfied("input-type == \"spotify\" || aggregate || input-type == \"csv\" || interactive && album"));
            Assert.IsTrue(Satisfied("input-type!=\"youtube\"||(album&&!interactive||(aggregate||interactive))"));
            Assert.IsFalse(Satisfied("input-type!=\"youtube\"||(album&&!interactive||(aggregate||!interactive))"));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_UsesJobDownloadModeWhenJobIsAvailable()
        {
            (Job Job, string Mode)[] cases =
            [
                (new SongJob(new SongQuery { Title = "test" }), "song"),
                (new AggregateJob(new SongQuery { Title = "test" }), "aggregate"),
                (new AlbumJob(new AlbumQuery { Album = "test" }), "album"),
                (new AlbumAggregateJob(new AlbumQuery { Album = "test" }), "album-aggregate"),
                (new RemoteFileJob(new PeerFileTarget(
                    new PeerFileIdentity("Peer", @"Share\File.bin"),
                    size: null,
                    extension: null)), "generic-file"),
                (new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                    new PeerDirectoryIdentity("Peer", @"Share\Folder"))), "generic-directory"),
            ];
            string[] modes =
            [
                "song", "aggregate", "album", "album-aggregate",
                "generic-file", "generic-directory", "remote-file", "remote-directory",
            ];

            foreach (var (job, expected) in cases)
            {
                foreach (string mode in modes)
                {
                    Assert.AreEqual(mode == expected, Satisfied($"download-mode == \"{mode}\"", job),
                        $"{job.GetType().Name} should report exactly download-mode '{expected}'.");
                }
            }
        }

        [TestMethod]
        public void ProfileConditionEvaluator_AutoDetectsInputType()
        {
            dl.Extraction.InputType = InputType.None;
            dl.Extraction.Input = "https://open.spotify.com/playlist/test";

            Assert.IsTrue(Satisfied("input-type == \"spotify\""));
            Assert.IsTrue(Satisfied(
                "input-type == \"youtube\"",
                new ExtractJob("https://www.youtube.com/playlist?list=test")));
            Assert.IsTrue(Satisfied(
                "input-type == \"soulseek\"",
                new ExtractJob("slsk://Peer/Share/File.bin")));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_UsesPerItemRequestedMode()
        {
            dl.Extraction.RequestedMode = null;
            dl.Extraction.InputType = InputType.List;
            var extract = new ExtractJob("Artist - Track")
            {
                RequestedModeOverride = ExtractionMode.Song,
            };

            Assert.IsTrue(Satisfied("download-mode == \"song\"", extract));
            Assert.IsFalse(Satisfied("download-mode == \"album\"", extract));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_UsesExplicitModeSettingsBeforeExtraction()
        {
            dl.Extraction.InputType = InputType.YouTube;
            dl.Extraction.RequestedMode = ExtractionMode.Song;
            Assert.IsTrue(Satisfied("download-mode == \"song\""));

            dl.Search.IsAggregate = true;
            Assert.IsTrue(Satisfied("download-mode == \"aggregate\""));

            dl.Extraction.RequestedMode = ExtractionMode.Album;
            Assert.IsTrue(Satisfied("download-mode == \"album-aggregate\""));

            dl.Search.IsAggregate = false;
            dl.Extraction.RequestedMode = null;
            dl.Extraction.UpgradeToAlbum = true;
            Assert.IsTrue(Satisfied("download-mode == \"album\""));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_DoesNotInventModeBeforeSourceDecides()
        {
            dl.Extraction.RequestedMode = null;
            dl.Extraction.InputType = InputType.YouTube;

            foreach (string mode in new[]
                     {
                         "normal", "generic-file", "generic-directory", "song", "aggregate", "album", "album-aggregate",
                     })
            {
                Assert.IsFalse(Satisfied($"download-mode == \"{mode}\""),
                    $"Source-decided input unexpectedly reported download mode '{mode}'.");
            }

            Assert.IsTrue(Satisfied("download-mode != \"album\""));
            Assert.IsFalse(Satisfied(
                "download-mode == \"extract\"",
                new ExtractJob("https://www.youtube.com/watch?v=test")));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_AlbumConditionUsesJobShapeWhenJobIsAvailable()
        {
            dl.Extraction.RequestedMode = ExtractionMode.Album;

            Assert.IsFalse(Satisfied("album", new SongJob(new SongQuery { Title = "test" })),
                "An explicit album request must not make a concrete SongJob match album auto-profiles.");
            Assert.IsTrue(Satisfied("album", new AlbumJob(new AlbumQuery())));
        }

        [TestMethod]
        public void ProfileConditionEvaluator_ClassifiesSoulseekIntentBeforeExtraction()
        {
            dl.Extraction.RequestedMode = null;
            Assert.IsTrue(Satisfied(
                "download-mode == \"generic-file\"",
                new ExtractJob("slsk://Peer/Share/File.bin")));
            Assert.IsTrue(Satisfied(
                "download-mode == \"generic-directory\"",
                new ExtractJob("slsk://Peer/Share/Folder/")));
            Assert.IsFalse(Satisfied(
                "download-mode == \"remote-file\"",
                new ExtractJob("slsk://Peer/Share/File.bin")));
            Assert.IsFalse(Satisfied(
                "download-mode == \"remote-directory\"",
                new ExtractJob("slsk://Peer/Share/Folder/")));

            dl.Extraction.RequestedMode = ExtractionMode.Song;
            Assert.IsTrue(Satisfied(
                "download-mode == \"song\"",
                new ExtractJob("slsk://Peer/Share/File.mp3")));

            dl.Extraction.RequestedMode = ExtractionMode.Album;
            Assert.IsTrue(Satisfied(
                "download-mode == \"album\"",
                new ExtractJob("slsk://Peer/Share/Folder/")));
        }
    }
}
