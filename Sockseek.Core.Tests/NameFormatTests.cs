using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using System.Reflection;
using Sockseek.Core.Settings;
using Sockseek.Core.Services;

namespace Tests.NameFormat
{
    [TestClass]
    public class NameFormatTests
    {
        [TestMethod]
        public void MusicCapabilities_EnrichCanonicalSharedCapabilitiesWithoutRedefiningThem()
        {
            var structural = NameFormatVariableProvider.Capabilities;
            var music = FileManager.GetNameFormatVariableDescriptors();

            Assert.IsTrue(structural.All(variable =>
                variable.Applicability == NameFormatVariableApplicability.Shared
                && variable.Phase == NameFormatEvaluationPhase.Placement));
            Assert.IsTrue(structural.All(variable => music.Any(candidate =>
                candidate.Name == variable.Name
                && candidate == variable)));
            Assert.AreEqual(music.Count, music.Select(variable => variable.Name).Distinct().Count());
            Assert.IsTrue(music.Any(variable =>
                variable.Name == "path"
                && variable.Applicability == NameFormatVariableApplicability.Shared
                && variable.Phase == NameFormatEvaluationPhase.Completion));
            Assert.IsTrue(music.Any(variable =>
                variable.Name == "terminal-outcome"
                && variable.Applicability == NameFormatVariableApplicability.Shared
                && variable.Phase == NameFormatEvaluationPhase.OnComplete));
            Assert.IsTrue(music.Any(variable =>
                variable.Name == "artist"
                && variable.Applicability == NameFormatVariableApplicability.Music
                && variable.Phase == NameFormatEvaluationPhase.MusicFinalization));
            Assert.IsFalse(music.Any(variable =>
                variable.Name is "lifecycle-state" or "activity-phase"));
        }

        [TestMethod]
        public void MusicProvider_DelegatesEverySharedPlacementVariableWithIdenticalSemantics()
        {
            var slFile = new Soulseek.File(0, @"Music\Artist\Album\Disc 2\07. Track.flac", 1, ".flac");
            var ctx = MakeCtx(slFile: slFile, remoteBaseDir: @"Music\Artist\Album") with
            {
                DownloadPath = Path.Combine(Path.GetTempPath(), "07. Track.mp3"),
                DefaultFolder = "Playlist",
                OutputDir = Path.Combine(Path.GetTempPath(), "Output"),
                ExtractorName = "Soulseek",
                InputSource = "slsk://user/Music/Artist/Album/",
                ConfigDir = Path.Combine(Path.GetTempPath(), "Config"),
            };
            var structural = new NameFormatVariableProvider(
                FileManager.GetStructuralNameFormatContext(ctx));

            foreach (string name in NameFormatVariableProvider.Supported)
            {
                Assert.IsTrue(structural.TryResolve(name, out var expected), name);
                Assert.IsTrue(FileManager.TryResolveNameFormatVariable(
                    name,
                    ctx,
                    () => null,
                    out var actual), name);
                Assert.AreEqual(expected, actual, name);
            }
        }

        [TestMethod]
        public void ExtUsesTheActualOutputExtensionAndRemoteExtIsUnsupported()
        {
            var slFile = new Soulseek.File(0, @"Music\Artist\Track.flac", 1, "flac");
            var ctx = MakeCtx(slFile: slFile) with
            {
                DownloadPath = Path.Combine(Path.GetTempPath(), "Track.mp3"),
            };

            Assert.IsTrue(FileManager.TryResolveNameFormatVariable("ext", ctx, () => null, out var output));
            Assert.IsFalse(FileManager.TryResolveNameFormatVariable("remote-ext", ctx, () => null, out _));
            Assert.IsFalse(FileManager.TryResolveNameFormatVariable("extension", ctx, () => null, out _));
            Assert.AreEqual(".mp3", output.Value);
        }

        [TestMethod]
        public void ExplicitOutcomeVariables_AreOnCompleteOnlyAndAmbiguousStateVariablesAreRemoved()
        {
            var ctx = MakeCtx() with
            {
                TerminalOutcome = JobTerminalOutcome.Skipped,
                SkipReason = JobSkipReason.AlreadyExists,
            };

            Assert.IsFalse(FileManager.TryResolveNameFormatVariable(
                "terminal-outcome", ctx, () => null, out _));
            Assert.IsTrue(FileManager.TryResolveNameFormatVariable(
                "terminal-outcome", ctx, () => null, out var outcome, includeOnCompleteVariables: true));
            Assert.AreEqual("Skipped", outcome.Value);
            Assert.AreEqual(
                "Skipped|AlreadyExists|None",
                FileManager.ReplaceVariables(
                    "{terminal-outcome}|{skip-reason}|{failure-reason}",
                    ctx,
                    null));

            foreach (string removed in new[] { "state", "lifecycle-state", "activity-phase" })
            {
                Assert.IsFalse(FileManager.GetAllVariableNames().Contains(removed));
                Assert.IsFalse(FileManager.TryResolveNameFormatVariable(
                    removed, ctx, () => null, out _, includeOnCompleteVariables: true));
            }
        }

        readonly List<TagLib.File> tagLibFiles = new();

        private FileManagerContext MakeCtx(
            string artist = "SourceArtist",
            string title = "SourceTitle",
            string album = "SourceAlbum",
            Soulseek.File? slFile = null,
            string? remoteBaseDir = null)
        {
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
                RemoteBaseDir = remoteBaseDir != null ? remoteBaseDir.Replace('/', '\\') : null,
            };
        }

        [TestMethod]
        public void LongExample_Passes()
        {
            var cfg = new DownloadSettings();
            cfg.Output.NameFormat = "{albumartist(/)album(/)track(. )title|artist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}";

            var slFile = new Soulseek.File(0, "music\\test\\testfile.mp3", 1, ".mp3");
            var ctx = MakeCtx(slFile: slFile, remoteBaseDir: "music\\test");

            var method = typeof(FileManager).GetMethod("ApplyNameFormatInternal", BindingFlags.NonPublic | BindingFlags.Static);

            var tagLibFile = CreateEmptyMP3(
                title: "Title",
                artist: "Artist",
                album: "Album",
                albumArtist: "AlbumArtist",
                track: 1
            );

            var result = (string?)method!.Invoke(null, new object[] {
                cfg.Output.NameFormat,
                cfg.Output.InvalidReplaceStr,
                ctx,
                (Func<TagLib.File?>)(() => tagLibFile),
            });

            Assert.AreEqual("AlbumArtist/Album/01. Title", result!.Replace('\\', '/'));

            var tagLibFile2 = CreateEmptyMP3(
                title: "Title",
                artist: "Artist",
                album: "Album",
                track: 1
            );

            var result2 = (string?)method.Invoke(null, new object[] {
                cfg.Output.NameFormat,
                cfg.Output.InvalidReplaceStr,
                ctx,
                (Func<TagLib.File?>)(() => tagLibFile2),
            });

            Assert.AreEqual("Artist/Album/01. Title", result2!.Replace('\\', '/'));

            var tagLibFile3 = CreateEmptyMP3(
                artist: "Artist",
                album: "Album",
                albumArtist: "AlbumArtist",
                track: 1
            );

            var result3 = (string?)method.Invoke(null, new object[] {
                cfg.Output.NameFormat,
                cfg.Output.InvalidReplaceStr,
                ctx,
                (Func<TagLib.File?>)(() => tagLibFile3),
            });

            Assert.AreEqual("missing-tags/test/testfile", result3!.Replace('\\', '/'));
        }

        [TestMethod]
        public void FolderAndFilenameFormat_PreservesBracesFromRemoteNames()
        {
            var cfg = new DownloadSettings();
            cfg.Output.NameFormat = "{foldername}/{filename}";

            var slFile = new Soulseek.File(0, @"music\Album {CAT001}\Track {DISC1}.flac", 1, ".flac");
            var ctx = MakeCtx(slFile: slFile);

            var method = typeof(FileManager).GetMethod("ApplyNameFormatInternal", BindingFlags.NonPublic | BindingFlags.Static);

            var result = (string?)method!.Invoke(null, new object[] {
                cfg.Output.NameFormat,
                cfg.Output.InvalidReplaceStr,
                ctx,
                (Func<TagLib.File?>)(() => null),
            });

            Assert.AreEqual("Album {CAT001}/Track {DISC1}", result!.Replace('\\', '/'));
        }

        [TestCleanup]
        public void Cleanup()
        {
            foreach (var tagLibFile in tagLibFiles)
            {
                if (File.Exists(tagLibFile.Name))
                {
                    File.Delete(tagLibFile.Name);
                }
                tagLibFile.Dispose();
            }
        }

        // this is easier than figuring out how to programmatically create a TagLib.File
        public TagLib.File CreateEmptyMP3(
            string? title = null,
            string? artist = null,
            string? albumArtist = null,
            string? album = null,
            uint? year = null,
            uint? track = null)
        {
            string tempPath = Path.GetTempFileName() + ".mp3";
            File.WriteAllBytes(tempPath, EmptyMp3Bytes);

            var file = TagLib.File.Create(tempPath);
            tagLibFiles.Add(file);

            if (title != null) file.Tag.Title = title;
            if (artist != null) file.Tag.Performers = new[] { artist };
            if (albumArtist != null) file.Tag.AlbumArtists = new[] { albumArtist };
            if (album != null) file.Tag.Album = album;
            if (year.HasValue) file.Tag.Year = year.Value;
            if (track.HasValue) file.Tag.Track = track.Value;

            file.Save();
            return file;
        }

        private static readonly byte[] EmptyMp3Bytes =
        {
            0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x22, 0x54, 0x53, 0x53, 0x45, 0x00, 0x00,
            0x00, 0x0E, 0x00, 0x00, 0x03, 0x4C, 0x61, 0x76, 0x66, 0x36, 0x31, 0x2E, 0x37, 0x2E, 0x31, 0x30,
            0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x40, 0xC0,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x49, 0x6E, 0x66, 0x6F, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xB6,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4C, 0x61, 0x76, 0x63, 0x36, 0x31, 0x2E,
            0x31, 0x39, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xB6, 0x00,
            0x00, 0x5B, 0xB2, 0x00, 0x00, 0x00, 0x00
        };
    }
}
