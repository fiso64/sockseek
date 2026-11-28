/// <summary>
/// Invented but natural-looking network for documentation screenshots (the
/// "showcase" scenario): artists, albums and peers are fictional, and differ
/// the way real ones do in format, folder naming, speed, queue and slots.
/// </summary>
static class Showcase
{
    private sealed record Album(string Artist, string Title, int Year, string[] Tracks);

    private static readonly int[] Lengths = [214, 247, 198, 263, 231, 289, 205, 242, 176, 301, 268, 223, 255];

    private static readonly Album Featured = new("Lake Vesper", "Static Bloom", 2021,
        ["Undertow", "Static Bloom", "Glass Harbour", "Low Light", "Paper Moons", "Northbound",
         "Salt & Ember", "Quiet Engines", "Afterglow"]);

    private static readonly Album[] Library =
    [
        Featured,
        new("Lake Vesper", "Weather Systems", 2018,
            ["Pressure Front", "Coastal", "Kitchen Radio", "Overcast", "Tidal", "Small Hours", "Barometer", "Last Ferry"]),
        new("Juniper Hall", "Long Way Down", 2019,
            ["Gravel Road", "Long Way Down", "Ivy", "Second Hand Coat", "Lanterns", "Old Machines", "Westerly", "Sleeper Train", "Home Again"]),
        new("Juniper Hall", "Sundays in Colour", 2022,
            ["Sundays in Colour", "Polaroid", "Fig Tree", "Matchstick", "Open Window", "Easy Now", "Red Kite"]),
        new("Odessa Fields", "Harvest Moon Radio", 2016,
            ["Prairie", "Harvest Moon Radio", "Wildflower", "County Line", "Slow Burn", "Dust & Diesel", "Evening Song", "Fences", "Windmill", "Goodnight Wichita"]),
        new("Kite Theory", "Parallax", 2020,
            ["Lift", "Parallax", "Thermals", "Ground Speed", "Ascent", "Crosswind", "Vanishing Point", "Soft Landing"]),
        new("North Lantern", "Ember & Ash", 2017,
            ["Kindling", "Ember & Ash", "Hearth", "Smoke Signals", "Black Pine", "Winterlight", "Coals"]),
        new("Pale Cartography", "Maps for Nowhere", 2023,
            ["Legend", "Contour Lines", "Maps for Nowhere", "Scale 1:1", "Terra Incognita", "Compass Rose", "Borders", "True North", "Index"]),
        new("Ana Ribeira", "Casa Azul", 2015,
            ["Casa Azul", "Saudade", "Lisbon Rain", "Azulejo", "Maré", "Porto Alegre", "Fado for Two"]),
        new("The Saltmarsh Band", "Low Tide Hymns", 2019,
            ["Mudflats", "Low Tide Hymn", "Heron", "Reed Bed", "Estuary", "Brine", "Driftwood Choir", "Spring Tide"]),
    ];

    private enum Format { Flac, HiRes, Mp3, Mp3Vbr }

    private sealed record Peer(string Name, bool FreeSlot, int Speed, int Queue, Format Format,
        Func<Album, string> Folder, Func<Album, int, string> File, string? Extra = null, int? TrackLimit = null, bool Locked = false);

    private static string Number(int track) => (track + 1).ToString("00");

    private static readonly Peer[] Peers =
    [
        new("mossgarden", true, 12_400_000, 0, Format.Flac,
            a => $@"Music\{a.Artist}\{a.Year} - {a.Title}", (a, t) => $"{Number(t)} - {a.Tracks[t]}.flac", "cover.jpg"),
        new("vinylwolf", true, 6_100_000, 2, Format.HiRes,
            a => $@"Music\Lossless\{a.Artist} - {a.Title} ({a.Year}) [24-96 FLAC]", (a, t) => $"{Number(t)}. {a.Tracks[t]}.flac", "folder.jpg"),
        new("harbormaster", false, 2_300_000, 14, Format.Mp3,
            a => $@"mp3\{a.Artist[0]}\{a.Artist}\{a.Title} ({a.Year})", (a, t) => $"{a.Artist} - {Number(t)} - {a.Tracks[t]}.mp3"),
        new("kettledrum", true, 3_800_000, 1, Format.Flac,
            a => $@"Albums\{a.Artist}\{a.Title} [{a.Year}]", (a, t) => $"{Number(t)} {a.Tracks[t]}.flac", "Front.jpg"),
        new("nightjar88", true, 940_000, 0, Format.Mp3Vbr,
            a => $@"Shared\Indie\{a.Artist}\[{a.Year}] {a.Title}", (a, t) => $"{Number(t)} {a.Tracks[t]}.mp3"),
        new("sparrowhawk", true, 1_600_000, 3, Format.Mp3,
            a => $@"Music\{a.Artist}\{a.Title}", (a, t) => $"{Number(t)} - {a.Tracks[t]}.mp3"),
        new("lowtide", false, 450_000, 31, Format.Flac,
            a => $@"Downloads\{a.Artist} - {a.Title}", (a, t) => $"{Number(t)} - {a.Tracks[t]}.flac", TrackLimit: 7),
        new("quietcorner", true, 5_200_000, 0, Format.Flac,
            a => $@"Buddies\{a.Artist}\{a.Year} - {a.Title}", (a, t) => $"{Number(t)} - {a.Tracks[t]}.flac", Locked: true),
    ];

    public static bool IsPeer(string username) => Peers.Any(peer => peer.Name == username);

    /// <summary>Answers any search with the featured album, plus a second album from one peer.</summary>
    public static IEnumerable<Soulseek.SearchResponse> SearchResponses()
    {
        int token = 1;
        foreach (Peer peer in Peers)
        {
            Soulseek.File[] files = [.. AlbumFiles(peer, Featured, fullPath: true)];
            if (peer.Name == "sparrowhawk")
                files = [.. files, .. AlbumFiles(peer, Library[1], fullPath: true)];
            yield return new Soulseek.SearchResponse(peer.Name, token++, peer.FreeSlot, peer.Speed, peer.Queue,
                fileList: peer.Locked ? [] : files, lockedFileList: peer.Locked ? files : []);
        }
    }

    public static Soulseek.BrowseResponse? Browse(string username)
    {
        Peer? peer = Peers.FirstOrDefault(candidate => candidate.Name == username);
        if (peer is null)
            return null;
        // Every peer shares the whole library in its own layout; one keeps some of it for buddies.
        var directories = Library.Select(album => new Soulseek.Directory(peer.Folder(album), [.. AlbumFiles(peer, album, fullPath: false)]));
        return peer.Locked
            ? new Soulseek.BrowseResponse([], directories.ToList())
            : new Soulseek.BrowseResponse(directories.ToList());
    }

    public static Soulseek.UserInfo? Info(string username) => username switch
    {
        "mossgarden" => new("Mostly indie and folk, all FLAC with cover art.\n\nBe patient with the queue, I'm on a home connection.", 4, 0, true),
        "vinylwolf" => new("Needle drops and hi-res releases. Ask if you're looking for something specific.", 3, 2, true),
        "harbormaster" => new("MP3 320 only. Slots fill up in the evenings.", 2, 14, false),
        _ when IsPeer(username) => new("", 2, 0, true),
        _ => null,
    };

    public static Soulseek.UserStatistics? Statistics(string username)
    {
        Peer? peer = Peers.FirstOrDefault(candidate => candidate.Name == username);
        return peer is null ? null : new(username, peer.Speed, 18_400 + peer.Name.Length * 977,
            Library.Sum(album => album.Tracks.Length), Library.Length);
    }

    public enum Progress { Complete, Transferring, Queued }

    /// <summary>Per-file rate a transferring download appears to have reached.</summary>
    public const int TransferSpeed = 3_050_000;

    /// <summary>
    /// How a showcase download behaves. Most complete; the hi-res peer has sent
    /// the first file, is part way through the second, and queues the rest:
    /// a Soulseek peer sends one file at a time.
    /// </summary>
    public static (Progress State, double Fraction) Download(string username, string filename)
    {
        if (username != "vinylwolf")
            return (Progress.Complete, 1);
        string name = filename[(filename.LastIndexOf('\\') + 1)..];
        return name.StartsWith("01.", StringComparison.Ordinal) ? (Progress.Complete, 1)
            : name.StartsWith("02.", StringComparison.Ordinal) ? (Progress.Transferring, 0.43)
            : (Progress.Queued, 0);
    }

    // People who download from the operator's own library.
    private static readonly string[] Downloaders = ["fernwood", "tapeloop", "orchard_owl", "dovetail", "slowcar", "brightwater"];

    /// <summary>
    /// A month of past activity for the dashboard: evening download sessions from
    /// the showcase peers, uploads to people browsing the operator's library, and
    /// a few failed attempts. Written directly, as the daemon would have recorded it.
    /// </summary>
    public static async Task SeedHistoryAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string runtimeId)
    {
        var random = new Random(4);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long day = 86_400_000;
        var buckets = new Dictionary<(long Start, string Direction, string User), long>();
        await using var transaction = await connection.BeginTransactionAsync();
        int sequence = 0;
        async Task Transfer(string direction, string username, string folder, Soulseek.File file, long completed, string? failure)
        {
            string id = Guid.NewGuid().ToString().ToUpperInvariant();
            string name = file.Filename[(file.Filename.LastIndexOf('\\') + 1)..];
            sequence++;
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO transfers (id, last_runtime_id, last_sequence, direction, source, username, remote_path, local_path,
                    state, terminal_outcome, total_bytes, transferred_bytes, attempt_count, created_at_utc, started_at_utc,
                    completed_at_utc, failure_reason, cancellation_source, file_name, file_size_bytes, file_extension,
                    group_ref, group_display_path, revision)
                VALUES ($id, $runtime, $sequence, $direction, 'SoulseekPeer', $username, $path, NULL, 'Ended', $outcome,
                    $size, $transferred, 1, $started, $started, $completed, $reason, 'None', $name, $size, $extension,
                    $group, $folder, 2);
                INSERT INTO transfer_attempts (id, transfer_id, last_runtime_id, last_sequence, attempt_number, source, state,
                    source_username, source_path, output_path, started_at_utc, completed_at_utc, failure_reason, failure_message,
                    revision, cancellation_source)
                VALUES ($attempt, $id, $runtime, $sequence, 1, 'SoulseekPeer', $state, $username, $path, NULL, $started,
                    $completed, $attemptReason, NULL, 1, 'None');
                """;
            long size = file.Size;
            long transferred = failure is null ? size : size / 3;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$attempt", Guid.NewGuid().ToString().ToUpperInvariant());
            insert.Parameters.AddWithValue("$runtime", runtimeId);
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$direction", direction);
            insert.Parameters.AddWithValue("$username", username);
            insert.Parameters.AddWithValue("$path", file.Filename);
            insert.Parameters.AddWithValue("$outcome", failure is null ? "Succeeded" : "Failed");
            insert.Parameters.AddWithValue("$state", failure is null ? "Completed" : "Failed");
            insert.Parameters.AddWithValue("$reason", failure is null ? "None" : "PeerFailure");
            insert.Parameters.AddWithValue("$attemptReason", failure ?? "None");
            insert.Parameters.AddWithValue("$size", size);
            insert.Parameters.AddWithValue("$transferred", transferred);
            insert.Parameters.AddWithValue("$started", completed - 60_000);
            insert.Parameters.AddWithValue("$completed", completed);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$extension", file.Extension);
            insert.Parameters.AddWithValue("$group", folder);
            insert.Parameters.AddWithValue("$folder", folder);
            await insert.ExecuteNonQueryAsync();
            var key = (completed / 300_000 * 300_000, direction, username);
            buckets[key] = buckets.GetValueOrDefault(key) + transferred;
        }

        string[] failures = ["PeerDisconnected", "TransferTimedOut", "FileNoLongerShared", "Denied"];
        Peer own = Peers[0];
        for (int daysAgo = 29; daysAgo >= 0; daysAgo--)
        {
            long midnight = (now - daysAgo * day) / day * day;
            // Downloads: a few albums every day, at different times. Hi-res albums are
            // left out: at a gigabyte each they would dwarf everything else.
            int albums = 1 + random.Next(3);
            for (int index = 0; index < albums; index++)
            {
                long time = midnight + (9 + random.Next(14)) * 3_600_000L + random.Next(3_600_000);
                {
                    Peer peer = Peers[new[] { 0, 2, 3, 4, 5, 6 }[random.Next(6)]];
                    Album album = Library[random.Next(Library.Length)];
                    foreach (Soulseek.File file in AlbumFiles(peer, album, fullPath: true))
                    {
                        time += Math.Max(20_000, file.Size * 1000 / peer.Speed);
                        string? failure = random.NextDouble() < 0.025 ? failures[random.Next(failures.Length)] : null;
                        if (time < now)
                            await Transfer("Download", peer.Name, peer.Folder(album), file, time, failure);
                    }
                }
            }
            // Uploads: people browsing the operator's library, spread through the day.
            int uploads = 1 + random.Next(3);
            for (int index = 0; index < uploads; index++)
            {
                long time = midnight + random.Next(24) * 3_600_000L + random.Next(3_600_000);
                Album album = Library[random.Next(Library.Length)];
                string downloader = Downloaders[random.Next(Downloaders.Length)];
                foreach (Soulseek.File file in AlbumFiles(own, album, fullPath: true).Where(file => file.Extension == ".flac"))
                {
                    time += file.Size * 1000 / 1_400_000;
                    if (time < now)
                        await Transfer("Upload", downloader, own.Folder(album), file, time, random.NextDouble() < 0.04 ? "PeerDisconnected" : null);
                }
            }
        }
        foreach (var ((start, direction, username), bytes) in buckets)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO transfer_byte_buckets (bucket_start_utc, direction, username, bytes) VALUES ($start, $direction, $username, $bytes);";
            insert.Parameters.AddWithValue("$start", start);
            insert.Parameters.AddWithValue("$direction", direction);
            insert.Parameters.AddWithValue("$username", username);
            insert.Parameters.AddWithValue("$bytes", bytes);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var coverage = connection.CreateCommand())
        {
            coverage.CommandText = "UPDATE transfer_accounting_state SET complete_from_utc = $from WHERE state_id = 1;";
            coverage.Parameters.AddWithValue("$from", now - 31 * day);
            await coverage.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static IEnumerable<Soulseek.File> AlbumFiles(Peer peer, Album album, bool fullPath)
    {
        string folder = peer.Folder(album);
        int count = Math.Min(album.Tracks.Length, peer.TrackLimit ?? int.MaxValue);
        for (int track = 0; track < count; track++)
        {
            int length = Lengths[(track + album.Year) % Lengths.Length];
            // Lossless bitrates vary with the music; a stable pseudo-random spread looks like it.
            int spread = (album.Title.Length * 31 + track * 47 + album.Year) % 97;
            (long size, int bitRate, int? sampleRate, int? bitDepth, string extension) = peer.Format switch
            {
                Format.Flac => (length * (780 + spread * 3) * 125L, 780 + spread * 3, 44_100, 16, ".flac"),
                Format.HiRes => (length * (3_420 + spread * 11) * 125L, 3_420 + spread * 11, 96_000, 24, ".flac"),
                Format.Mp3 => (length * 40_000L, 320, (int?)null, (int?)null, ".mp3"),
                _ => (length * 31_000L, 245, (int?)null, (int?)null, ".mp3"),
            };
            List<Soulseek.FileAttribute> attributes =
            [
                new(Soulseek.FileAttributeType.BitRate, bitRate),
                new(Soulseek.FileAttributeType.Length, length),
            ];
            if (sampleRate is { } rate) attributes.Add(new(Soulseek.FileAttributeType.SampleRate, rate));
            if (bitDepth is { } depth) attributes.Add(new(Soulseek.FileAttributeType.BitDepth, depth));
            if (peer.Format == Format.Mp3Vbr) attributes.Add(new(Soulseek.FileAttributeType.VariableBitRate, 1));
            string name = peer.File(album, track);
            yield return new Soulseek.File(1, fullPath ? $@"{folder}\{name}" : name, size, extension, attributes);
        }
        if (peer.Extra is { } extra)
            yield return new Soulseek.File(1, fullPath ? $@"{folder}\{extra}" : extra, 184_000 + album.Year * 37, ".jpg", []);
    }
}
