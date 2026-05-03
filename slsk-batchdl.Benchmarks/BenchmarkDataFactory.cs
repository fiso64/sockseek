using System.Collections.Concurrent;
using Sldl.Core.Models;
using Sldl.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace slsk_batchdl.Benchmarks;

internal static class BenchmarkDataFactory
{
    private static readonly string[] Artists =
    [
        "Electric Light Orchestra",
        "Casiopea",
        "KNOWER",
        "Steely Dan",
        "Yellow Magic Orchestra",
        "Herbie Hancock",
        "Tatsuro Yamashita",
        "Weather Report"
    ];

    private static readonly string[] Albums =
    [
        "Time",
        "Mint Jams",
        "Life",
        "Aja",
        "Solid State Survivor",
        "Head Hunters",
        "For You",
        "Heavy Weather"
    ];

    private static readonly string[] Titles =
    [
        "Twilight",
        "Asayake",
        "Overtime",
        "Peg",
        "Rydeen",
        "Chameleon",
        "Sparkle",
        "Birdland"
    ];

    public static SearchSettings CreateSearchSettings()
    {
        var settings = new DownloadSettings().Search;
        settings.IgnoreOn = -1;
        settings.DownrankOn = 2;
        settings.MinSharesAggregate = 2;
        settings.AggregateLengthTol = 3;
        return settings;
    }

    public static SongQuery TrackQuery => new()
    {
        Artist = "Electric Light Orchestra",
        Title = "Twilight",
        Album = "Time",
        Length = 209,
    };

    public static AlbumQuery AlbumQuery => new()
    {
        Artist = "Electric Light Orchestra",
        Album = "Time",
    };

    public static ConcurrentDictionary<string, int> CreateUserSuccessCounts(int userCount)
    {
        var counts = new ConcurrentDictionary<string, int>();
        for (int i = 0; i < userCount; i++)
        {
            if (i % 9 == 0)
                counts[$"user{i:D5}"] = 5;
        }
        return counts;
    }

    public static List<(SearchResponse Response, SlFile File)> CreateTrackResults(int count)
    {
        var results = new List<(SearchResponse, SlFile)>(count);
        for (int i = 0; i < count; i++)
        {
            var artist = Artists[i % Artists.Length];
            var album = Albums[i % Albums.Length];
            var title = i % 3 == 0 ? "Twilight" : Titles[i % Titles.Length];
            var extension = i % 4 == 0 ? ".flac" : ".mp3";
            var filename = $@"Music\{artist}\{album}\{i % 20 + 1:D2}. {artist} - {title}{extension}";
            var file = CreateFile(i + 1, filename, extension, length: 205 + i % 11, bitrate: extension == ".flac" ? 950 : 320);
            var response = CreateResponse(i, file);
            results.Add((response, file));
        }
        return results;
    }

    public static List<(SearchResponse Response, SlFile File)> CreateAlbumResults(int folderCount, int tracksPerFolder)
    {
        var results = new List<(SearchResponse, SlFile)>(folderCount * (tracksPerFolder + 1));
        int fileId = 1;

        for (int folder = 0; folder < folderCount; folder++)
        {
            string user = $"user{folder:D5}";
            string artist = folder % 5 == 0 ? "Electric Light Orchestra" : Artists[folder % Artists.Length];
            string album = folder % 4 == 0 ? "Time" : Albums[folder % Albums.Length];
            string basePath = folder % 7 == 0
                ? $@"Shared\{artist}\{album}\Disc 1"
                : $@"Shared\{artist}\{album}";

            var files = new List<SlFile>(tracksPerFolder + 1);
            for (int track = 1; track <= tracksPerFolder; track++)
            {
                string title = track == 2 ? "Twilight" : $"Track {track:D2}";
                files.Add(CreateFile(
                    fileId++,
                    $@"{basePath}\{track:D2}. {artist} - {title}.flac",
                    ".flac",
                    length: 170 + track + folder % 5,
                    bitrate: 950));
            }

            files.Add(CreateFile(fileId++, $@"{basePath}\Cover.jpg", ".jpg", length: null, bitrate: null));
            var response = new SearchResponse(user, folder, folder % 3 != 0, 80_000 + folder * 10, folder % 6, files);

            foreach (var file in files)
                results.Add((response, file));
        }

        return results;
    }

    private static SearchResponse CreateResponse(int index, SlFile file)
        => new(
            username: $"user{index:D5}",
            token: index,
            hasFreeUploadSlot: index % 3 != 0,
            uploadSpeed: 60_000 + index % 500 * 100,
            queueLength: index % 8,
            fileList: [file]);

    private static SlFile CreateFile(int id, string filename, string extension, int? length, int? bitrate)
    {
        var attributes = new List<FileAttribute>();
        if (length.HasValue)
            attributes.Add(new FileAttribute(FileAttributeType.Length, length.Value));
        if (bitrate.HasValue)
            attributes.Add(new FileAttribute(FileAttributeType.BitRate, bitrate.Value));

        long size = extension == ".jpg" ? 500_000 : (long)(length ?? 1) * 160_000;
        return new SlFile(id, filename, size, extension, attributeList: attributes);
    }
}
