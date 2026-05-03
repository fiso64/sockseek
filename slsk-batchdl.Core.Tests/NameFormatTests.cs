using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using System.Reflection;
using Sldl.Core.Settings;
using Sldl.Core.Services;

namespace Tests.NameFormat
{
    [TestClass]
    public class NameFormatTests
    {
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
                ? new FileCandidate(response, slFile)
                : null;
            return new FileManagerContext
            {
                Job          = job,
                Query        = query,
                Candidate    = candidate,
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
