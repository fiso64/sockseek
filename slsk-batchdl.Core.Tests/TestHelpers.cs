using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core;
using Soulseek;
using File = Soulseek.File;
using Sldl.Core.Settings;

namespace Tests
{
    public static class TestHelpers
    {
        public static List<SearchResponse> CreateTestIndex()
        {
            var user1SearchResponse = new SearchResponse(
                username: "user1",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList: new List<File>
                {
                    new File(1, "Music\\Artist1\\Album1\\Artist1 - Album1 - 01 - Track1.mp3", 10000, ".mp3"),
                    new File(2, "Music\\Artist1\\Album1\\Artist1 - Album1 - 02 - Track2.mp3", 12000, ".mp3"),
                    new File(3, "Music\\Artist1\\Album1\\Artist1 - Album1 - 03 - Track3.flac", 25000, ".flac"),
                    new File(4, "Music\\Artist1\\Album1\\Artist1 - Album1 - cover.jpg", 500, ".jpg"),
                    new File(5, "Music\\Artist1\\Album1\\Artist1 - Album1.cue", 100, ".cue"),
                    new File(6, "Music\\Artist1\\Album2\\Artist1 - Album2 - 01 - Track1.mp3", 11000, ".mp3"),
                    new File(7, "Music\\Artist1\\Album2\\Artist1 - Album2 - 02 - Track2.flac", 28000, ".flac"),
                    new File(8, "Music\\Artist1\\Album2\\Artist1 - Album2 - cover.jpg", 600, ".jpg")
                }
            );

            var user2SearchResponse = new SearchResponse(
                username: "user2",
                token: 2,
                hasFreeUploadSlot: false,
                uploadSpeed: 50,
                queueLength: 5,
                fileList: new List<File>
                {
                    new File(9,  "Music\\Artist2\\Album1\\Artist2 - Album1 - 01 - Track1.mp3", 9000, ".mp3"),
                    new File(10, "Music\\Artist2\\Album1\\Artist2 - Album1 - 02 - Track2.mp3", 11000, ".mp3"),
                    new File(11, "Music\\Artist2\\Album1\\Artist2 - Album1 - cover.jpg", 400, ".jpg"),
                    new File(12, "Music\\Artist2\\Artist2 - Single.flac", 30000, ".flac"),
                    new File(13, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\01 Track 1.mp3", 8000, ".mp3"),
                    new File(14, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\02 Track 2.mp3", 8000, ".mp3"),
                    new File(15, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\cover.jpg", 300, ".jpg"),
                }
            );

            var user3SearchResponse = new SearchResponse(
                username: "testuser",
                token: 3,
                hasFreeUploadSlot: true,
                uploadSpeed: 75,
                queueLength: 2,
                fileList: new List<File>
                {
                    new File(16, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0101. testartist - testsong.mp3", 15000, ".mp3"),
                    new File(17, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0102. testartist - testsong2.mp3", 16000, ".mp3"),
                    new File(18, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0103. testartist - testsong3.mp3", 17000, ".mp3"),
                    new File(19, "Music\\music\\testartist\\(2011) testalbum [MP3]\\cover.jpg", 700, ".jpg")
                }
            );

            return new List<SearchResponse> { user1SearchResponse, user2SearchResponse, user3SearchResponse };
        }

        public static File CreateSlFile(
            string filename,
            long size = 10000,
            string? extension = null,
            int? bitrate = null,
            int? length = null,
            int? sampleRate = null,
            int? bitDepth = null)
        {
            extension ??= Path.GetExtension(filename);
            var attributes = new List<FileAttribute>();

            if (bitrate.HasValue)
                attributes.Add(new FileAttribute(FileAttributeType.BitRate, bitrate.Value));
            if (length.HasValue)
                attributes.Add(new FileAttribute(FileAttributeType.Length, length.Value));
            if (sampleRate.HasValue)
                attributes.Add(new FileAttribute(FileAttributeType.SampleRate, sampleRate.Value));
            if (bitDepth.HasValue)
                attributes.Add(new FileAttribute(FileAttributeType.BitDepth, bitDepth.Value));

            return new File(1, filename, size, extension, attributeList: attributes);
        }

        public static SongQuery CreateQuery(
            string artist = "",
            string title = "",
            string album = "",
            int length = -1)
        {
            return new SongQuery { Artist = artist, Title = title, Album = album, Length = length };
        }

        public static SongJob CreateSongJob(
            string artist = "",
            string title = "",
            string album = "",
            int length = -1)
        {
            return new SongJob(new SongQuery { Artist = artist, Title = title, Album = album, Length = length });
        }

        public static (EngineSettings Engine, DownloadSettings Download) CreateDefaultSettings()
        {
            var engine = new EngineSettings();
            var download = new DownloadSettings();
            download.Extraction.Input = "some input";
            download.Output.ParentDir = System.IO.Directory.GetCurrentDirectory();
            download.Output.FailedAlbumPath = Path.Join(download.Output.ParentDir, "failed");
            return (engine, download);
        }

        public static readonly byte[] EmptyMp3Bytes =
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

        public static TagLib.File CreateEmptyMP3(
            string? title = null,
            string? artist = null,
            string? albumArtist = null,
            string? album = null,
            uint? year = null,
            uint? track = null)
        {
            string tempPath = Path.GetTempFileName() + ".mp3";
            System.IO.File.WriteAllBytes(tempPath, EmptyMp3Bytes);

            var file = TagLib.File.Create(tempPath);

            if (title != null) file.Tag.Title = title;
            if (artist != null) file.Tag.Performers = new[] { artist };
            if (albumArtist != null) file.Tag.AlbumArtists = new[] { albumArtist };
            if (album != null) file.Tag.Album = album;
            if (year.HasValue) file.Tag.Year = year.Value;
            if (track.HasValue) file.Tag.Track = track.Value;

            file.Save();
            return file;
        }
        
        public static SoulseekClientManager CreateMockClientManager(ISoulseekClient client, EngineSettings engineSettings)
        {
            return new SoulseekClientManager(engineSettings, client);
        }

        public static SessionRegistry CreateSessionRegistry()
        {
            return new SessionRegistry();
        }
    }
}
