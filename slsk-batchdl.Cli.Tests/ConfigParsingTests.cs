using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Cli;
using Sldl.Server;

namespace Tests.ConfigParsingTests
{
    [TestClass]
    public class DefaultValuesTests
    {
        private static DownloadSettings Cfg()
        {
            var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
            return ConfigManager.Bind(file, ["some input"]).Download;
        }

        [TestMethod]
        public void Defaults_NecessaryCondFormats_AreSet()
        {
            var config = Cfg();
            CollectionAssert.IsSubsetOf(
                new[] { "mp3", "flac", "ogg" },
                config.Search.NecessaryCond.Formats);
        }

        [TestMethod]
        public void Defaults_PreferredCondBitrate_IsSet()
        {
            var config = Cfg();
            Assert.IsNotNull(config.Search.PreferredCond.MinBitrate);
            Assert.IsNotNull(config.Search.PreferredCond.MaxBitrate);
            Assert.IsTrue(config.Search.PreferredCond.MinBitrate > 0);
        }

        [TestMethod]
        public void Defaults_PreferredCondLengthTolerance_IsSet()
        {
            var config = Cfg();
            Assert.IsNotNull(config.Search.PreferredCond.LengthTolerance);
            Assert.IsTrue(config.Search.PreferredCond.LengthTolerance >= 0);
        }

        [TestMethod]
        public void Defaults_NecessaryCondLengthTolerance_IsSet()
        {
            var config = Cfg();
            // A set required tolerance is inert for queries without a known length, but
            // prevents known wrong-length candidates from being accepted when length is provided.
            Assert.IsTrue(config.Search.NecessaryCond.LengthTolerance > 0);
        }

        [TestMethod]
        public void Defaults_AlbumFalse_AggregateFalse()
        {
            var config = Cfg();
            Assert.IsFalse(config.Extraction.IsAlbum);
            Assert.IsFalse(config.Search.IsAggregate);
        }

        [TestMethod]
        public void Defaults_SkipExistingTrue()
        {
            var config = Cfg();
            Assert.IsTrue(config.Skip.SkipExisting);
        }

        [TestMethod]
        public void Defaults_DoNotDownload_FalseByDefault()
        {
            var config = Cfg();
            Assert.IsFalse(config.DoNotDownload);
        }
    }

    [TestClass]
    public class ArgumentParsingTests
    {
        static DownloadSettings Cfg(params string[] args)
        {
            var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
            return ConfigManager.Bind(file, args).Download;
        }

        [TestMethod]
        public void Album_Flag_SetsAlbumTrue()
        {
            var config = Cfg("--album", "some input");
            Assert.IsTrue(config.Extraction.IsAlbum);
        }

        [TestMethod]
        public void Aggregate_Flag_SetsAggregateTrue()
        {
            var config = Cfg("--aggregate", "some input");
            Assert.IsTrue(config.Search.IsAggregate);
        }

        [TestMethod]
        public void NameFormat_SetsValue()
        {
            var config = Cfg("--name-format", "{artist}/{title}", "some input");
            Assert.AreEqual("{artist}/{title}", config.Output.NameFormat);
        }

        [TestMethod]
        public void MaxStaleTime_SetsValue()
        {
            var config = Cfg("--max-stale-time", "60000", "some input");
            Assert.AreEqual(60000, config.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Format_SetsNecessaryCondFormats()
        {
            var config = Cfg("--format", "mp3,flac", "some input");
            CollectionAssert.AreEquivalent(new[] { "mp3", "flac" }, config.Search.NecessaryCond.Formats);
        }

        [TestMethod]
        public void MinBitrate_SetsNecessaryCondMinBitrate()
        {
            var config = Cfg("--min-bitrate", "200", "some input");
            Assert.AreEqual(200, config.Search.NecessaryCond.MinBitrate);
        }

        [TestMethod]
        public void MaxBitrate_SetsNecessaryCondMaxBitrate()
        {
            var config = Cfg("--max-bitrate", "320", "some input");
            Assert.AreEqual(320, config.Search.NecessaryCond.MaxBitrate);
        }

        [TestMethod]
        public void PrefFormat_SetsPreferredCondFormats()
        {
            var config = Cfg("--pref-format", "flac", "some input");
            CollectionAssert.AreEquivalent(new[] { "flac" }, config.Search.PreferredCond.Formats);
        }

        [TestMethod]
        public void PrefLengthTol_SetsPreferredCondTolerance()
        {
            var config = Cfg("--pref-length-tol", "5", "some input");
            Assert.AreEqual(5, config.Search.PreferredCond.LengthTolerance);
        }

        [TestMethod]
        public void StrictConditions_DisablesAcceptMissingProps()
        {
            var config = Cfg("--strict-conditions", "some input");
            Assert.AreEqual(false, config.Search.NecessaryCond.AcceptMissingProps);
            Assert.AreEqual(false, config.Search.PreferredCond.AcceptMissingProps);
        }

        [TestMethod]
        public void Path_SetsParentDir()
        {
            var config = Cfg("--path", "/tmp/music", "some input");
            Assert.AreEqual(Path.GetFullPath("/tmp/music"), config.Output.ParentDir);
        }
    }

    [TestClass]
    public class ComputedPropertiesTests
    {
        static DownloadSettings Cfg(params string[] args)
        {
            var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
            return ConfigManager.Bind(file, args).Download;
        }

        [TestMethod]
        public void DoNotDownload_TrueWhenPrintTracks()
        {
            var config = Cfg("--print-tracks", "some input");
            Assert.IsTrue(config.DoNotDownload);
            Assert.IsTrue(config.PrintTracks);
        }

        [TestMethod]
        public void DoNotDownload_TrueWhenPrintResults()
        {
            var config = Cfg("--print-results", "some input");
            Assert.IsTrue(config.DoNotDownload);
            Assert.IsTrue(config.PrintResults);
        }

        [TestMethod]
        public void NeedLogin_FalseWhenPrintIndex()
        {
            var config = Cfg("--print", "index", "some input");
            Assert.IsFalse(config.NeedLogin);
        }
    }

    // Confirms that --cond album-track-count=N flows from CLI parsing all the way through
    // the real Spotify/CSV + -a execution path: SongJob (created by extractor) → Upgrade() →
    // JobPreparer.PrepareSubtree() → Preprocessor.PreprocessAlbum() → AlbumQuery.
    [TestClass]
    public class FolderConditionCliPathTests
    {
        static DownloadSettings Cfg(params string[] args)
        {
            var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
            return ConfigManager.Bind(file, args).Download;
        }

        // Simulates: spotify input → SongJob created by extractor → Upgrade(album:true) →
        // PrepareSubtree on upgraded AlbumJob → PreprocessAlbum.
        static AlbumJob UpgradeAndPrepare(DownloadSettings startConfig,
            SongQuery? query = null, bool aggregate = false)
        {
            var songJob = new SongJob(query ?? new SongQuery { Title = "Some Song", Artist = "Some Artist" });
            var albumJob = (AlbumJob)((IUpgradeable)songJob).Upgrade(album: true, aggregate: aggregate).First();
            JobPreparer.PrepareSubtree(albumJob, startConfig);
            Preprocessor.PreprocessAlbum(albumJob, albumJob.Config.Preprocess);
            JobPreparer.ApplySearchSettings(albumJob, albumJob.Config.Search);
            return albumJob;
        }

        [TestMethod]
        public void CondAlbumTrackCountExact_SetsFolderConditions()
        {
            var config = Cfg("--cond", "album-track-count=10", "x");

            Assert.AreEqual(10, config.Search.NecessaryFolderCond.MinTrackCount, "CLI --cond must set NecessaryFolderCond.MinTrackCount");
            Assert.AreEqual(10, config.Search.NecessaryFolderCond.MaxTrackCount, "CLI --cond must set NecessaryFolderCond.MaxTrackCount");

            var albumJob = UpgradeAndPrepare(config);
            Assert.AreEqual("Some Artist", albumJob.Query.Artist);
        }

        [TestMethod]
        public void CondAlbumTrackCountGe_SetsOnlyMin()
        {
            var config = Cfg("--cond", "album-track-count>=8", "x");

            Assert.AreEqual(8,  config.Search.NecessaryFolderCond.MinTrackCount);
            Assert.IsNull(config.Search.NecessaryFolderCond.MaxTrackCount);

            var albumJob = UpgradeAndPrepare(config);
            Assert.AreEqual("Some Artist", albumJob.Query.Artist);
        }

        [TestMethod]
        public void CondAlbumTrackCount_RemainsInFolderConditionsAfterUpgrade()
        {
            var config = Cfg("--cond", "album-track-count=12", "x");

            var albumJob = UpgradeAndPrepare(config,
                query: new SongQuery { Title = "T", Artist = "A" });

            Assert.AreEqual("A", albumJob.Query.Artist);
            Assert.AreEqual(12, albumJob.Config.Search.NecessaryFolderCond.MinTrackCount);
            Assert.AreEqual(12, albumJob.Config.Search.NecessaryFolderCond.MaxTrackCount);
        }
    }


    [TestClass]
    public class ConfigManagerBindingTests
    {
        private static (EngineSettings eng, DownloadSettings dl, CliSettings cli)
            Bind(params string[] args)
        {
            var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
            return ConfigManager.Bind(file, args);
        }

        private static (EngineSettings eng, DownloadSettings dl, CliSettings cli, DaemonSettings daemon, RemoteSettings remote)
            BindAll(params string[] args)
        {
            var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
            return ConfigManager.BindAll(file, args);
        }

        // ── Scalar types ──────────────────────────────────────────────────────

        [TestMethod]
        public void String_LongFlag()
        {
            var (eng, _, _) = Bind("--username", "alice");
            Assert.AreEqual("alice", eng.Username);
        }

        [TestMethod]
        public void Int_LongFlag()
        {
            var (eng, _, _) = Bind("--connect-timeout", "5000");
            Assert.AreEqual(5000, eng.ConnectTimeout);
        }

        [TestMethod]
        public void Double_LongFlag()
        {
            var (_, dl, _) = Bind("--fast-search-min-up-speed", "2.5");
            Assert.AreEqual(2.5, dl.Search.FastSearchMinUpSpeed);
        }

        [TestMethod]
        public void Enum_SkipMode_ParsedCaseInsensitive()
        {
            var (_, dl, _) = Bind("--skip-mode-output-dir", "name");
            Assert.AreEqual(SkipMode.Name, dl.Skip.SkipMode);
        }

        // ── Bool flags ────────────────────────────────────────────────────────

        [TestMethod]
        public void Bool_BareFlagDefaultsToTrue()
        {
            var (_, dl, _) = Bind("x", "--fast-search");
            Assert.IsTrue(dl.Search.FastSearch);
        }

        [TestMethod]
        public void Bool_ExplicitFalse()
        {
            var (_, dl, _) = Bind("x", "--fast-search", "false");
            Assert.IsFalse(dl.Search.FastSearch);
        }

        [TestMethod]
        public void Bool_InvertedFlag_NoSkipExisting()
        {
            var (_, dl, _) = Bind("x", "--no-skip-existing");
            Assert.IsFalse(dl.Skip.SkipExisting);
        }

        [TestMethod]
        public void Bool_InvertedFlag_MockFilesNoReadTags()
        {
            var (eng, _, _) = Bind("--mock-files-no-read-tags");
            Assert.IsFalse(eng.MockFilesReadTags);
        }

        [TestMethod]
        public void RemotePatch_ExplicitDefaultBool_IsRepresented()
        {
            var patch = ConfigManager.CreateCliDownloadSettingsPatch(["x", "--skip-existing", "true"]);
            Assert.IsNotNull(patch);
            Assert.AreEqual(true, patch.Skip?.SkipExisting);
        }

        [TestMethod]
        public void RemotePatch_OnCompleteAppend_IsRepresentedAsAppend()
        {
            var patch = ConfigManager.CreateCliDownloadSettingsPatch(["x", "--on-complete", "+ second"]);
            Assert.IsNotNull(patch);
            CollectionAssert.AreEqual(new[] { "second" }, patch.Output?.OnComplete?.Append?.ToArray());
            Assert.IsNull(patch.Output?.OnComplete?.Replace);
        }

        // ── Inline = form ─────────────────────────────────────────────────────

        [TestMethod]
        public void InlineEquals_SetsValue()
        {
            var (eng, _, _) = Bind("--username=bob");
            Assert.AreEqual("bob", eng.Username);
        }

        // ── Positional ────────────────────────────────────────────────────────

        [TestMethod]
        public void Positional_SetsInput()
        {
            var (_, dl, _) = Bind("https://open.spotify.com/playlist/x");
            Assert.AreEqual("https://open.spotify.com/playlist/x", dl.Extraction.Input);
        }

        [TestMethod]
        public void Positional_DuplicateThrows()
        {
            Assert.ThrowsException<Exception>(() => Bind("url1", "url2"));
        }

        // ── Special cases ─────────────────────────────────────────────────────

        [TestMethod]
        public void Login_SplitsOnSemicolon()
        {
            var (eng, _, _) = Bind("--login", "user;pass");
            Assert.AreEqual("user", eng.Username);
            Assert.AreEqual("pass", eng.Password);
        }

        [TestMethod]
        public void NoListen_SetsListenPortNull()
        {
            var (eng, _, _) = Bind("--no-listen");
            Assert.IsNull(eng.ListenPort);
        }

        [TestMethod]
        public void Verbose_SetsLogLevelDebug()
        {
            var (eng, _, _) = Bind("-v");
            Assert.AreEqual(Logger.LogLevel.Debug, eng.LogLevel);
        }

        [TestMethod]
        public void FailsToDownrank_StoredNegated()
        {
            var (_, dl, _) = Bind("x", "--fails-to-downrank", "3");
            Assert.AreEqual(-3, dl.Search.DownrankOn);
        }

        [TestMethod]
        public void OnComplete_AppendMode()
        {
            var (_, dl, _) = Bind("x", "--on-complete", "cmd1", "--on-complete", "+ cmd2");
            CollectionAssert.AreEqual(new[] { "cmd1", "cmd2" }, dl.Output.OnComplete);
        }

        [TestMethod]
        public void OnComplete_OverwriteMode()
        {
            var (_, dl, _) = Bind("x", "--on-complete", "cmd1", "--on-complete", "cmd2");
            CollectionAssert.AreEqual(new[] { "cmd2" }, dl.Output.OnComplete);
        }

        [TestMethod]
        public void AlbumTrackCount_RangeMin()
        {
            var (_, dl, _) = Bind("x", "--album-track-count", "8+");
            Assert.AreEqual(8,  dl.Search.NecessaryFolderCond.MinTrackCount);
            Assert.IsNull(dl.Search.NecessaryFolderCond.MaxTrackCount);
        }

        [TestMethod]
        public void AlbumTrackCount_RangeMax()
        {
            var (_, dl, _) = Bind("x", "--album-track-count", "12-");
            Assert.IsNull(dl.Search.NecessaryFolderCond.MinTrackCount);
            Assert.AreEqual(12, dl.Search.NecessaryFolderCond.MaxTrackCount);
        }

        [TestMethod]
        public void AlbumArtOnly_ClearsConditions()
        {
            var (_, dl, _) = Bind("x", "--album-art-only");
            Assert.AreEqual(0, dl.Search.NecessaryCond.Formats?.Length ?? 0);
            Assert.AreEqual(0, dl.Search.PreferredCond.Formats?.Length ?? 0);
        }

        [TestMethod]
        public void WriteIndex_SetsHasConfiguredIndex()
        {
            var (_, dl, _) = Bind("x", "--write-index");
            Assert.IsTrue(dl.Output.HasConfiguredIndex);
        }

        [TestMethod]
        public void NoWriteIndex_SetsHasConfiguredIndexAndFalse()
        {
            var (_, dl, _) = Bind("x", "--no-write-index");
            Assert.IsTrue(dl.Output.HasConfiguredIndex);
            Assert.IsFalse(dl.Output.WriteIndex);
        }

        [TestMethod]
        public void NoProgress_SetsCli()
        {
            var (_, _, cli) = Bind("--no-progress");
            Assert.IsTrue(cli.NoProgress);
        }

        [TestMethod]
        public void ServerIp_SetsCli()
        {
            var (_, _, _, daemon, _) = BindAll("--server-ip", "0.0.0.0");
            Assert.AreEqual("0.0.0.0", daemon.ListenIp);
        }

        [TestMethod]
        public void ServerPort_SetsCli()
        {
            var (_, _, _, daemon, _) = BindAll("--server-port", "5055");
            Assert.AreEqual(5055, daemon.ListenPort);
        }

        [TestMethod]
        public void Remote_SetsRemoteUrl()
        {
            var (_, _, _, _, remote) = BindAll("--remote", "http://127.0.0.1:5030");
            Assert.AreEqual("http://127.0.0.1:5030", remote.ServerUrl);
        }

        [TestMethod]
        public void RemoteSubmissionOptions_DoNotOverrideServerPathByDefault()
        {
            var options = Sldl.Cli.Program.BuildRemoteSubmissionOptions(
                ["some input", "--remote", "127.0.0.1"],
                new CliSettings());

            Assert.IsNull(options.OutputParentDir);
            Assert.IsNull(options.DownloadSettings?.Output?.ParentDir);
        }

        [TestMethod]
        public void RemoteSubmissionOptions_SendExplicitPathAsDownloadPatch()
        {
            var options = Sldl.Cli.Program.BuildRemoteSubmissionOptions(
                ["some input", "--remote", "127.0.0.1", "-p", "C:\\Downloads"],
                new CliSettings());

            Assert.IsNull(options.OutputParentDir);
            Assert.AreEqual("C:\\Downloads", options.DownloadSettings?.Output?.ParentDir);
        }

        [TestMethod]
        public void Progress_ClearsNoProgress()
        {
            var (_, _, cli) = Bind("--no-progress", "--progress");
            Assert.IsFalse(cli.NoProgress);
        }

        // ── Profile merging ───────────────────────────────────────────────────

        [TestMethod]
        public void Profile_DefaultAppliedFirst()
        {
            var profiles = new Dictionary<string, ProfileEntry>
            {
                ["default"] = new(["--connect-timeout", "1000"], null),
            };
            var file = new ConfigFile("none", profiles);
            var (eng, _, _) = ConfigManager.Bind(file, []);
            Assert.AreEqual(1000, eng.ConnectTimeout);
        }

        [TestMethod]
        public void Profile_CliOverridesDefault()
        {
            var profiles = new Dictionary<string, ProfileEntry>
            {
                ["default"] = new(["--connect-timeout", "1000"], null),
            };
            var file = new ConfigFile("none", profiles);
            var (eng, _, _) = ConfigManager.Bind(file, ["--connect-timeout", "9000"]);
            Assert.AreEqual(9000, eng.ConnectTimeout);
        }

        [TestMethod]
        public void Profile_NamedAppliedBetweenDefaultAndCli()
        {
            var profiles = new Dictionary<string, ProfileEntry>
            {
                ["default"] = new(["--connect-timeout", "1000"], null),
                ["fast"]    = new(["--connect-timeout", "500"], null),
            };
            var file = new ConfigFile("none", profiles);
            var (eng, _, _) = ConfigManager.Bind(file, [], profileName: "fast");
            Assert.AreEqual(500, eng.ConnectTimeout);
        }

        [TestMethod]
        public void Profile_NameExtractedFromCliArgs()
        {
            var profiles = new Dictionary<string, ProfileEntry>
            {
                ["fast"] = new(["--connect-timeout", "500"], null),
            };
            var file = new ConfigFile("none", profiles);
            var (eng, _, _) = ConfigManager.Bind(file, ["--profile", "fast"]);
            Assert.AreEqual(500, eng.ConnectTimeout);
        }

        [TestMethod]
        public void UnknownFlag_Throws()
        {
            Assert.ThrowsException<Exception>(() => Bind("--not-a-real-flag"));
        }
    }
}
