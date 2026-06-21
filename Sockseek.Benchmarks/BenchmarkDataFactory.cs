using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Benchmarks;

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

    private static readonly string[] RemixArtists =
    [
        "Four Tet",
        "Madlib",
        "Aphex Twin",
        "Flying Lotus",
    ];

    public static readonly string[] SearchTerms =
    [
        "Electric Light Orchestra",
        "Twilight",
        "Time",
        "Steely Dan",
        "Peg",
        "KNOWER",
        "Overtime",
    ];

    private static readonly string[] PathTemplates =
    [
        @"Music\{artist}\{album}\{track:D2}. {artist} - {title}.{ext}",
        @"Music\{artist}\{album}\{track:D2}. {title}.{ext}",
        @"Music\{artist}\{album}\{track:D2}. {artist} - {title} ({remix} Remix).{ext}",
        @"Music\{artist}\[{year}] {album}\{track:D2}. {artist} - {title}.{ext}",
        @"Music\{artist}\{album} [{year} Remaster]\{track:D2}. {title}.{ext}",
        @"Music\{artist}\{album}\{track:D2}. {title} [Remastered {year}].{ext}",
        @"Shared\Downloads\{artist} - {title} ({year}).{ext}",
        @"Shared\Music\{artist} - {album}\{track:D2}. {title}.{ext}",
    ];

    // Artists that never include "Electric Light Orchestra" — used for miss paths.
    private static readonly string[] OtherArtists =
        [.. Artists.Where(a => a != "Electric Light Orchestra")];

    public static string[] CreateFilePaths(int count)
    {
        var paths = new string[count];
        for (int i = 0; i < count; i++)
        {
            var artist  = Artists[i % Artists.Length];
            var album   = Albums[i % Albums.Length];
            var title   = Titles[i % Titles.Length];
            var remix   = RemixArtists[i % RemixArtists.Length];
            var ext     = i % 4 == 0 ? "flac" : "mp3";
            var track   = (i % 20) + 1;
            var year    = 1975 + (i % 30);
            var tmpl    = PathTemplates[i % PathTemplates.Length];

            paths[i] = tmpl
                .Replace("{artist}", artist)
                .Replace("{album}", album)
                .Replace("{title}", title)
                .Replace("{remix}", remix)
                .Replace("{ext}", ext)
                .Replace("{track:D2}", track.ToString("D2"))
                .Replace("{year}", year.ToString());
        }
        return paths;
    }

    // All paths contain "Electric Light Orchestra" at a word boundary.
    public static string[] CreateHitPaths(int count)
    {
        var paths = new string[count];
        string[] templates =
        [
            @"Music\Electric Light Orchestra\{album}\{track:D2}. Electric Light Orchestra - {title}.flac",
            @"Music\Electric Light Orchestra\{album}\{track:D2}. Electric Light Orchestra - {title}.mp3",
            @"Music\Electric Light Orchestra\{album}\{track:D2}. Electric Light Orchestra - {title} ({remix} Remix).flac",
            @"Music\Electric Light Orchestra\[{year}] {album}\{track:D2}. Electric Light Orchestra - {title}.flac",
            @"Music\Electric Light Orchestra\{album} [{year} Remaster]\{track:D2}. {title}.mp3",
            @"Shared\Music\Electric Light Orchestra - {album}\{track:D2}. {title}.flac",
        ];
        for (int i = 0; i < count; i++)
        {
            paths[i] = templates[i % templates.Length]
                .Replace("{album}",    Albums[i % Albums.Length])
                .Replace("{title}",    Titles[i % Titles.Length])
                .Replace("{remix}",    RemixArtists[i % RemixArtists.Length])
                .Replace("{track:D2}", (i % 20 + 1).ToString("D2"))
                .Replace("{year}",     (1975 + i % 30).ToString());
        }
        return paths;
    }

    // No path contains "Electric Light Orchestra".
    public static string[] CreateMissPaths(int count)
    {
        var paths = new string[count];
        for (int i = 0; i < count; i++)
        {
            var artist = OtherArtists[i % OtherArtists.Length];
            var album  = Albums[i % Albums.Length];
            var title  = Titles[i % Titles.Length];
            var remix  = RemixArtists[i % RemixArtists.Length];
            var tmpl   = PathTemplates[i % PathTemplates.Length];
            paths[i] = tmpl
                .Replace("{artist}",   artist)
                .Replace("{album}",    album)
                .Replace("{title}",    title)
                .Replace("{remix}",    remix)
                .Replace("{ext}",      i % 4 == 0 ? "flac" : "mp3")
                .Replace("{track:D2}", (i % 20 + 1).ToString("D2"))
                .Replace("{year}",     (1975 + i % 30).ToString());
        }
        return paths;
    }

    // Strings that contain Windows-invalid chars — for ReplaceInvalidChars hit case.
    private static readonly string[] InvalidCharTemplates =
    [
        "{artist}: {title}",
        "{artist} - {title}? (Live)",
        "{artist} | {album}",
        "{artist} - \"{title}\"",
        "{artist} <{title}>",
        "{artist} - {title} * Bonus",
        "{artist}/{title}",
        "{artist} - {title}: The {album} Sessions",
    ];

    // Strings that contain special chars for ReplaceSpecialChars hit case.
    private static readonly string[] SpecialCharTemplates =
    [
        "{artist} - {title} (feat. {remix})",
        "{artist} & {remix} - {title}",
        "{artist} - {title} [Remastered]",
        "{artist}: {title}",
        "{artist} - {title}!",
        "{artist} — {title}",
        "{artist} - {title} (Disc 1/2)",
        "「{artist}」{title}",
    ];

    public static string[] CreateInvalidCharHits(int count)
    {
        var r = new string[count];
        for (int i = 0; i < count; i++)
        {
            var tmpl = InvalidCharTemplates[i % InvalidCharTemplates.Length];
            r[i] = tmpl
                .Replace("{artist}", Artists[i % Artists.Length])
                .Replace("{title}",  Titles[i % Titles.Length])
                .Replace("{album}",  Albums[i % Albums.Length])
                .Replace("{remix}",  RemixArtists[i % RemixArtists.Length]);
        }
        return r;
    }

    public static string[] CreateInvalidCharMisses(int count)
    {
        var r = new string[count];
        for (int i = 0; i < count; i++)
            r[i] = $"{Artists[i % Artists.Length]} - {Titles[i % Titles.Length]}";
        return r;
    }

    public static string[] CreateSpecialCharHits(int count)
    {
        var r = new string[count];
        for (int i = 0; i < count; i++)
        {
            var tmpl = SpecialCharTemplates[i % SpecialCharTemplates.Length];
            r[i] = tmpl
                .Replace("{artist}", Artists[i % Artists.Length])
                .Replace("{title}",  Titles[i % Titles.Length])
                .Replace("{remix}",  RemixArtists[i % RemixArtists.Length]);
        }
        return r;
    }

    public static string[] CreateSpecialCharMisses(int count)
    {
        var r = new string[count];
        for (int i = 0; i < count; i++)
            r[i] = $"{Artists[i % Artists.Length]} {Titles[i % Titles.Length]} {Albums[i % Albums.Length]}";
        return r;
    }

    public static string[] CreateLongPathLikeStrings(int count, bool metadataHeavy)
    {
        var r = new string[count];
        for (int i = 0; i < count; i++)
        {
            var artist = Artists[i % Artists.Length];
            var album = Albums[i % Albums.Length];
            var title = Titles[i % Titles.Length];
            var remix = RemixArtists[i % RemixArtists.Length];
            var ext = i % 4 == 0 ? "flac" : "mp3";
            var track = i % 20 + 1;
            var year = 1975 + i % 30;
            var disc = i % 4 + 1;
            var source = i % 2 == 0 ? "CD Rip" : "Vinyl Rip";

            r[i] = metadataHeavy
                ? $@"Shared\Music Archive\{artist}: Studio Collection\{year} - {album} [Deluxe/Remaster]\Disc {disc} <Verified>\{source}\{track:D2}. {artist} - ""{title}""? ({remix} Remix) * Bonus | {album}.{ext}"
                : $@"Shared\Music Archive\Collection\{artist}\{artist} Discography\{year} {album}\Disc {disc}\{source}\Lossless\Original Album\{track:D2}. {artist} - {title} {album} {year}.{ext}";
        }
        return r;
    }

    public static string[] CreateDiacriticStrings(int count, bool hasDiacritics, bool isLong)
    {
        string[] ascii =
        [
            "Cafe Creme a la mode",
            "Electric Light Orchestra - Twilight",
            "Casiopea Mint Jams Remaster",
            "Shared Music Archive Collection Lossless Original Album",
        ];

        string[] diacritics =
        [
            "Café Crème à la mode Ü",
            "Sigur Rós - Ágætis byrjun",
            "Beyoncé - Déjà Vu",
            "Françoise Hardy / João Gilberto / Zoë Keating",
        ];

        var r = new string[count];
        var source = hasDiacritics ? diacritics : ascii;

        for (int i = 0; i < count; i++)
        {
            string value = source[i % source.Length];
            r[i] = isLong
                ? $@"Shared\Music Archive\Collection\{value}\{1975 + i % 30} {value} Deluxe Edition\Disc {i % 4 + 1}\{i % 20 + 1:D2}. {value} - {value} Remaster {value}.flac"
                : value;
        }

        return r;
    }

    public static string[] CreateStrictStringPreprocessStrings(int count, StrictStringPreprocessStringCase stringCase)
    {
        string[] source = stringCase switch
        {
            StrictStringPreprocessStringCase.CleanTitle =>
            [
                "Electric Light Orchestra Twilight",
                "Steely Dan Peg",
                "KNOWER Overtime",
                "Tatsuro Yamashita Sparkle",
            ],

            StrictStringPreprocessStringCase.DirtyTitle =>
            [
                " Electric_Light Orchestra: Twilight  ",
                "Steely  Dan - \"Peg\"?",
                "KNOWER___Overtime",
                "Herbie Hancock | Chameleon * Bonus",
            ],

            StrictStringPreprocessStringCase.DiacriticsTitle =>
            [
                "Beyoncé Déjà Vu",
                "Sigur Rós Ágætis byrjun",
                "Café Crème à la mode",
                "João Gilberto Águas de Março",
            ],

            StrictStringPreprocessStringCase.CleanBaseName =>
            [
                "01 Electric Light Orchestra - Twilight",
                "02 Steely Dan - Peg",
                "03 KNOWER - Overtime",
                "04 Tatsuro Yamashita - Sparkle",
            ],

            StrictStringPreprocessStringCase.DirtyBaseName =>
            [
                " 01._Electric_Light Orchestra - Twilight: Remaster  ",
                "02. Steely__Dan - \"Peg\"?",
                "03. KNOWER___Overtime",
                "04. Herbie Hancock | Chameleon * Bonus",
            ],

            StrictStringPreprocessStringCase.DiacriticsBaseName =>
            [
                "01 Beyoncé - Déjà Vu",
                "02 Sigur Rós - Ágætis byrjun",
                "03 Café Tacvba - Crème à la mode",
                "04 João Gilberto - Águas de Março",
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(stringCase), stringCase, null),
        };

        var r = new string[count];
        for (int i = 0; i < count; i++)
            r[i] = source[i % source.Length];

        return r;
    }

    public static (string Filename, SongQuery Query)[] CreateInferSongQueryCases(int count, InferSongQueryCase queryCase)
    {
        var r = new (string Filename, SongQuery Query)[count];

        for (int i = 0; i < count; i++)
        {
            var artist = Artists[i % Artists.Length];
            var album = Albums[i % Albums.Length];
            var title = Titles[i % Titles.Length];
            var track = i % 20 + 1;
            var year = 1975 + i % 30;

            r[i] = queryCase switch
            {
                InferSongQueryCase.CleanArtistTitle => (
                    $@"Shared\Music\{artist}\{album}\{artist} - {title}.flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.TrackNumStart => (
                    $@"Shared\Music\{artist}\{album}\{track:D2}. {artist} - {title}.flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.TrackNumMiddle => (
                    $@"Shared\Music\{artist}\{album}\{artist} - {track:D2} {title}.flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.TrackNumMiddleDash => (
                    $@"Shared\Music\{artist}\{album}\{artist} - {track:D2} - {title}.flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.ThreePartAlbumArtistTitle => (
                    $@"Shared\Music\{artist}\{album}\{album} - {artist} - {title}.flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.BracketedArtist => (
                    $@"Shared\Music\{artist}\{album}\({track:D2}) [{artist}] {title}.flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.RemixLike => (
                    $@"Shared\Music\{artist}\{album}\{title} ({artist} {RemixArtists[i % RemixArtists.Length]} Remix).flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.DirtyMetadata => (
                    $@"Shared\Music\{artist}\{album}\{track:D2}._{artist}: ""{title}""? [{year} Remaster].flac",
                    new SongQuery { Artist = artist, Title = title, Album = album }),

                InferSongQueryCase.DiacriticsAndFt => (
                    $@"Shared\Music\Beyoncé\B'Day\{track:D2}. Beyoncé feat. Jay-Z - Déjà Vu.flac",
                    new SongQuery { Artist = "Beyoncé", Title = "Déjà Vu", Album = "B'Day" }),

                _ => throw new ArgumentOutOfRangeException(nameof(queryCase), queryCase, null),
            };
        }

        return r;
    }

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

    public static List<(SearchResponse Response, SlFile File)> CreateMixedAlbumQualityResults(int folderCount, int tracksPerFolder)
    {
        var results = new List<(SearchResponse, SlFile)>(folderCount * (tracksPerFolder + 1));
        int fileId = 1;

        for (int folder = 0; folder < folderCount; folder++)
        {
            string user = $"user{folder:D05}";
            string artist = folder % 5 == 0 ? "Electric Light Orchestra" : Artists[folder % Artists.Length];
            string album = folder % 4 == 0 ? "Time" : Albums[folder % Albums.Length];
            string basePath = folder % 7 == 0
                ? $@"Shared\{artist}\{album}\Disc 1"
                : $@"Shared\{artist}\{album}";

            int matchingFlacCount = (folder % 4) switch
            {
                0 => tracksPerFolder,
                1 => Math.Max(1, tracksPerFolder * 2 / 3),
                2 => Math.Max(1, tracksPerFolder / 3),
                _ => 0,
            };

            var files = new List<SlFile>(tracksPerFolder + 1);
            for (int track = 1; track <= tracksPerFolder; track++)
            {
                string title = track == 2 ? "Twilight" : $"Track {track:D2}";
                bool isFlac = track <= matchingFlacCount;
                string extension = isFlac ? ".flac" : ".mp3";
                files.Add(CreateFile(
                    fileId++,
                    $@"{basePath}\{track:D2}. {artist} - {title}{extension}",
                    extension,
                    length: 170 + track + folder % 5,
                    bitrate: isFlac ? 950 : 320));
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
