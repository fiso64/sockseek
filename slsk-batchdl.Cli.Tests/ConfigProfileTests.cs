using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Cli;

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
            ConfigManager.ApplyAutoProfileCliSettings(file, root, cli, job);
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
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf.conf");
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
        public void AutoProfile_WithEngineSetting_Throws()
        {
            string content =
                "[bad-auto]\n" +
                "profile-cond = download-mode == \"album\"\n" +
                "connect-timeout = 1000";

            var (file, root, cli, args) = Bind(content);

            Assert.ThrowsException<Exception>(() => ConfigManager.CreateJobSettingsResolver(file, args, cli));
        }

        [TestMethod]
        public void Resolver_DoesNotDuplicateAppendableArgs()
        {
            string content =
                "on-complete = + action_default\n" +
                "[auto-profile]\n" +
                "profile-cond = interactive\n" +
                "fast-search = true";

            var (file, root, _, args) = Bind(content, "--interactive", "--on-complete", "+ action_cli");
            var cli = new CliSettings { InteractiveMode = true };
            var result = Resolve(file, root, cli, args, new SongJob(new SongQuery { Title = "test" }));

            Assert.IsTrue(result.Search.FastSearch);
            Assert.AreEqual(2, result.Output.OnComplete!.Count);
            Assert.AreEqual("action_default", result.Output.OnComplete[0]);
            Assert.AreEqual("action_cli", result.Output.OnComplete[1]);
        }
    }

    [TestClass]
    public class ProfilePriorityOrderTests
    {
        private string testConfigPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf_priority.conf");
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

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

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

            var result = Resolve(file, root, new CliSettings { InteractiveMode = true }, args);

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
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf_variation.conf");
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
                "on-complete = + action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + action_profile",
                "--interactive");
            var resolver = ConfigManager.CreateJobSettingsResolver(file, args, new CliSettings { InteractiveMode = true });

            var first = resolver.Resolve(root, new SongJob(new SongQuery { Title = "a" }));
            first.Output.OnComplete!.Add("mutated");
            var second = resolver.Resolve(root, new SongJob(new SongQuery { Title = "b" }));

            CollectionAssert.Contains(second.Output.OnComplete, "action_default");
            CollectionAssert.Contains(second.Output.OnComplete, "action_profile");
            CollectionAssert.DoesNotContain(second.Output.OnComplete, "mutated");
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
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf_edge.conf");
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
            Assert.IsTrue(Satisfied("download-mode == \"song\"", new SongJob(new SongQuery { Title = "test" })));
            Assert.IsFalse(Satisfied("download-mode == \"album\"", new SongJob(new SongQuery { Title = "test" })));
        }
    }
}
