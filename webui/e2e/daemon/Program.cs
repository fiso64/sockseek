using System.Text.Json;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Sockseek.Core.Settings;
using Sockseek.Persistence;
using Sockseek.Persistence.Chat;
using Sockseek.Persistence.Sqlite;
using Sockseek.Server;
using Sockseek.Server.Authentication;
using Tests.Server;

if (args.Length != 1 || !File.Exists(Path.Combine(args[0], "index.html")))
    throw new ArgumentException("Pass the absolute path to a built webui/dist directory.");

// Reuse only the empty migrated schema; each scenario owns a fresh application,
// database, peer, output tree and set of out-of-band controls.
try
{
    await using var input = ReadConsoleLines().GetAsyncEnumerator();
    while (await input.MoveNextAsync())
    {
        ScenarioOptions options = JsonSerializer.Deserialize<ScenarioOptions>(input.Current)
            ?? throw new ArgumentException("Pass scenario options before starting a test.");
        await RunScenarioAsync(args[0], options, input);
        Console.WriteLine("E2E_STOPPED " + JsonSerializer.Serialize(new
        {
            workingSetBytes = Environment.WorkingSet,
            managedBytes = GC.GetTotalMemory(forceFullCollection: false),
        }));
    }
}
finally
{
    SqliteConnection.ClearAllPools();
}

static async IAsyncEnumerable<string> ReadConsoleLines()
{
    // Console.In.ReadLineAsync is synchronous on SyncTextReader. It must not
    // park a pool worker (and its local continuations) between commands.
    var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false,
    });
    new Thread(() =>
    {
        try
        {
            while (Console.ReadLine() is { } line)
                lines.Writer.WriteAsync(line).AsTask().GetAwaiter().GetResult();
            lines.Writer.TryComplete();
        }
        catch (Exception exception) { lines.Writer.TryComplete(exception); }
    }) { IsBackground = true, Name = "e2e-control-input" }.Start();
    await foreach (string line in lines.Reader.ReadAllAsync()) yield return line;
}

static async Task RunScenarioAsync(string distribution, ScenarioOptions scenario, IAsyncEnumerator<string> input)
{
    var storageBefore = scenario.Metrics ? ReadStorageCounters() : null;
    using var measuredProcess = scenario.Metrics ? Process.GetCurrentProcess() : null;
    TimeSpan cpuBefore = measuredProcess?.TotalProcessorTime ?? default;
    TimeSpan userCpuBefore = measuredProcess?.UserProcessorTime ?? default;
    TimeSpan kernelCpuBefore = measuredProcess?.PrivilegedProcessorTime ?? default;
    long allocatedBefore = scenario.Metrics ? GC.GetTotalAllocatedBytes() : 0;
    long downloadPayloadBytes = 0;
    SqliteConnection? finalCheckpointConnection = null;
    string temporary = Directory.CreateTempSubdirectory("sockseek-webui-e2e-").FullName;
    var startupTimer = Stopwatch.StartNew();
    try
    {
        string dataDirectory = Path.Combine(temporary, "data");
        Directory.CreateDirectory(dataDirectory);
        string databasePath = SockseekDataPaths.ResolveDatabasePath(dataDirectory);
        global::Tests.TestDatabase.Create(databasePath);
        string webRoot = Path.Combine(temporary, "wwwroot");
        foreach (string source in Directory.EnumerateFiles(distribution, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(webRoot, Path.GetRelativePath(distribution, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        if (scenario.Metrics)
            Console.WriteLine($"E2E_METRIC startup-dist-copy-ms={startupTimer.ElapsedMilliseconds}");
        bool uploadShares = scenario.Uploads;
        string sharedDirectory = Path.Combine(temporary, "shared");
        if (uploadShares)
        {
            Directory.CreateDirectory(sharedDirectory);
            // Exercise native nested paths through the actual scanner and
            // upload stream factory, not just files at the share root.
            string albumDirectory = Path.Combine(sharedDirectory, "Album");
            Directory.CreateDirectory(albumDirectory);
            await File.WriteAllBytesAsync(Path.Combine(albumDirectory, "Uploaded.txt"), new byte[128]);
            await File.WriteAllBytesAsync(Path.Combine(albumDirectory, "Queued.txt"), new byte[128]);
        }
        var peer = SoulseekClientProxy.Create();
        byte[] profilePicture = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        peer.ProfileInfoForPeer = username => username switch
        {
            "fixture-peer" => new Soulseek.UserInfo("A small shared library.\n\nMore at https://example.org/fixture", 3, 1, true, profilePicture),
            "info-unavailable-peer" => throw new IOException("Peer info unavailable."),
            _ => new Soulseek.UserInfo("", 0, 0, false),
        };
        peer.ProfileStatisticsForPeer = username => username switch
        {
            "fixture-peer" => new Soulseek.UserStatistics(username, 8_450_000, 48_231, 5, 3),
            "mixed-peer" => new Soulseek.UserStatistics(username, 0, 0, 2, 3),
            "statistics-unavailable-peer" => throw new IOException("Peer statistics unavailable."),
            _ => new Soulseek.UserStatistics(username, 0, 0, 0, 0),
        };
        bool albumGroupSearch = false;
        bool mixedAlbumGroupSearch = false;
        bool pagedAlbumGroupSearch = false;
        bool largePeerShares = false;
        bool largeMusicSearch = false;
        int musicSearchPeerCount = 100;
        int musicSearchAlbumCount = 10;
        bool variedMusicTracks = false;
        bool pacedMusicSearch = false;
        int networkSearches = 0;
        int peerBrowseCount = 0;
        bool mixedRetrievalSearch = false;
        bool concurrentRetrievalSearch = false;
        TaskCompletionSource? directoryBrowseGate = null;
        static Soulseek.BrowseResponse LargeMusicShares()
        {
            return new([
                .. Enumerable.Range(0, 10).Select(album => new Soulseek.Directory(
                    $@"Music\Fixture Artist\Fixture Album-{album:D2}", [.. Enumerable.Range(0, 10).Select(track => new Soulseek.File(1,
                    $@"{track:D2} - Fixture Track-{track:D2}.flac", 8_000_000 + track * 100_000, ".flac",
                    [new(Soulseek.FileAttributeType.BitRate, 320), new(Soulseek.FileAttributeType.Length, 200 + album * 60 + track * 5)])),
                    new Soulseek.File(1, "10 - Fixture Bonus.flac", 128, ".flac", [])])),
                // Exact directory spelling matters even when it differs only by case.
                new Soulseek.Directory(@"MUSIC\Fixture Artist\Fixture Album-00",
                    [new Soulseek.File(1, "Wrong directory.flac", 128, ".flac", [])]),
                new Soulseek.Directory(@"Unrelated\Archive", [.. Enumerable.Range(0, 99_889)
                    .Select(index => new Soulseek.File(1, $"Unrelated {index:D6}.txt", 128, ".txt", []))]),
            ]);
        }
        peer.BrowseForPeer = username => largeMusicSearch && username.StartsWith("fixture-peer-", StringComparison.Ordinal)
            ? LargeMusicShares()
            : largePeerShares && username == "large-peer"
            ? new Soulseek.BrowseResponse([new Soulseek.Directory("Library",
            Enumerable.Range(1, 100_000)
                .Select(index => new Soulseek.File(1, $"Track {index:D6}.flac", 128, ".flac", []))
                .ToArray())])
            : concurrentRetrievalSearch && username == "fixture-peer"
            ? new Soulseek.BrowseResponse([new Soulseek.Directory("Fixture", [
            new Soulseek.File(1, "First song.txt", 128, ".txt", []),
            new Soulseek.File(1, "Bonus song.txt", 128, ".txt", []),
        ])])
            : concurrentRetrievalSearch && username == "second-peer"
            ? new Soulseek.BrowseResponse([new Soulseek.Directory("Second", [
            new Soulseek.File(1, "Second song.txt", 128, ".txt", []),
            new Soulseek.File(1, "Extra song.txt", 128, ".txt", []),
        ])])
            : albumGroupSearch && username == "second-peer"
            ? new Soulseek.BrowseResponse([new Soulseek.Directory(@"Second\Fixture Album", [
            new Soulseek.File(1, "01 - Intro.flac", 128, ".flac", []),
            new Soulseek.File(1, "02 - Finale.flac", 128, ".flac", []),
            new Soulseek.File(1, "03 - Bonus.flac", 128, ".flac", []),
        ])])
            : mixedRetrievalSearch && username == "fixture-peer"
            ? new Soulseek.BrowseResponse([new Soulseek.Directory("Fixture", [
            new Soulseek.File(1, "Selected.txt", 128, ".txt", []),
            new Soulseek.File(1, "Cover.jpg", 256, ".jpg", []),
        ])])
            : username is "paged-peer" or "filtered-paged-peer"
            ? new Soulseek.BrowseResponse([
                new Soulseek.Directory("Library", Enumerable.Range(1, username == "paged-peer" ? 501 : 101)
                .Select(index => new Soulseek.File(1, $"Item {index:D3}.txt", 128, ".txt", []))
                .ToArray()),
            ])
            : username == "unicode-peer"
            ? new Soulseek.BrowseResponse([new Soulseek.Directory("Library", [
                new Soulseek.File(1, "Აbc.txt", 128, ".txt", []),
                new Soulseek.File(1, "აbc.txt", 128, ".txt", []),
                new Soulseek.File(1, "ſound.txt", 128, ".txt", []),
                new Soulseek.File(1, "sound.txt", 128, ".txt", []),
            ]),
                new Soulseek.Directory(@"Library\Empty", []),
                new Soulseek.Directory(@"Library\ſound.txt", []),
            ])
            : username == "mixed-peer"
            ? new Soulseek.BrowseResponse(
                [new Soulseek.Directory(@"Mixture\Public", [new Soulseek.File(1, "visible.bin", 12, ".bin", [])])],
                [new Soulseek.Directory(@"Mixture\Locked", [new Soulseek.File(1, "hidden.bin", 34, ".bin", [])])])
            : username == "second-peer"
            ? new Soulseek.BrowseResponse([new Soulseek.Directory("Second", [new Soulseek.File(1, "Later song.txt", 128, ".txt", [])])])
            : peer.SharedFiles;
        peer.BrowseAsyncForPeer = async (username, cancellationToken) =>
        {
            Interlocked.Increment(ref peerBrowseCount);
            if (directoryBrowseGate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            if (largeMusicSearch) Console.WriteLine("E2E_METRIC large-music-browse files=100000");
            return peer.BrowseForPeer?.Invoke(username) ?? peer.SharedFiles;
        };
        peer.SharedFiles = new Soulseek.BrowseResponse([
            new Soulseek.Directory("Fixture", [
            new Soulseek.File(1, "Selected.txt", 128, ".txt", []),
            new Soulseek.File(1, "Unselected.txt", 128, ".txt", []),
        ]),
        new Soulseek.Directory(@"Fixture\Nested", [
            new Soulseek.File(1, "Nested.txt", 128, ".txt", []),
        ]),
        new Soulseek.Directory(@"Fixture\Nested\Deep", [
            new Soulseek.File(1, "DeepOne.txt", 128, ".txt", []),
            new Soulseek.File(1, "DeepTwo.txt", 128, ".txt", []),
        ]),
    ]);
        TaskCompletionSource? downloadGate = null;
        int rejectNextDownload = 0;
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? artDownloadGate = null;
        var directoryComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool failNextUpload = false;
        peer.UploadHandler = async arguments =>
        {
            if (failNextUpload)
            {
                failNextUpload = false;
                throw new IOException("The fixture peer disconnected during upload.");
            }
            string username = (string)arguments[0]!;
            string filename = (string)arguments[1]!;
            var streamFactory = arguments.OfType<Func<long, Task<Stream>>>().Single();
            var options = arguments.OfType<Soulseek.TransferOptions>().Single();
            var cancellationToken = arguments.OfType<CancellationToken>().LastOrDefault();
            await using Stream input = await streamFactory(0);
            DateTime started = DateTime.UtcNow;
            Soulseek.Transfer Snapshot(Soulseek.TransferStates state, long bytes) => new(
                Soulseek.TransferDirection.Upload, username, filename, 1, state,
                128, 0, bytes, 128, started, state.HasFlag(Soulseek.TransferStates.Completed) ? DateTime.UtcNow : null,
                null, null, null);
            options.StateChanged?.Invoke(new(Soulseek.TransferStates.Queued, Snapshot(Soulseek.TransferStates.InProgress, 0)));
            var buffer = new byte[64];
            await input.ReadExactlyAsync(buffer, cancellationToken);
            options.ProgressUpdated?.Invoke(new(0, Snapshot(Soulseek.TransferStates.InProgress, 64)));
            await uploadGate.Task.WaitAsync(cancellationToken);
            await input.ReadExactlyAsync(buffer, cancellationToken);
            var completed = Snapshot(Soulseek.TransferStates.Succeeded, 128);
            options.ProgressUpdated?.Invoke(new(0, completed));
            return completed;
        };
        TaskCompletionSource? nextSearchBatch = null;
        TaskCompletionSource? finishSearch = null;
        bool emptySearch = false;
        var searchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool musicSearch = false;
        bool ordinaryTrackSearch = false;
        bool conditionedMusicSearch = false;
        bool largeSearch = false;
        bool largeDirectorySearch = false;
        bool largeDistributedSearch = false;
        bool stagedLargeDirectorySearch = false;
        bool mixedVisibilitySearch = false;
        bool directorySelectionSearch = false;
        bool groupSearch = false;
        bool richGroupSearch = false;
        bool longSongFallback = false;
        var fallbackDownloadTargets = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool richAlbumGroupSearch = false;
        bool albumArtSearch = false;
        int albumArtFileCount = 1;
        bool bareFilenameSearch = false;
        peer.SearchHandler = async (receive, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            searchStarted.TrySetResult();
            Interlocked.Increment(ref networkSearches);
            if (emptySearch)
            {
                await finishSearch!.Task.WaitAsync(cancellationToken);
                return;
            }
            if (longSongFallback)
            {
                for (int index = 0; index < 100; index++)
                    receive(new Soulseek.SearchResponse($"Péér {index:000}", index + 1,
                        true, 100_000_000 - index * 700_000, 0,
                        [new Soulseek.File(1, $@"共有\Option {index:000}\Artist - Song.mp3", 1_024, ".mp3",
                            [new(Soulseek.FileAttributeType.BitRate, 320),
                             new(Soulseek.FileAttributeType.Length, 180)])]));
                return;
            }
            if (bareFilenameSearch)
            {
                foreach (string username in new[] { "bare-peer-a", "bare-peer-b" })
                {
                    var files = new List<Soulseek.File>
                    {
                        new(1, "Artist - Bare.flac", 128, ".flac", [new(Soulseek.FileAttributeType.Length, 180)]),
                    };
                    files.Add(new(1, @"Shares\Bare Album\Artist - Named.flac", 128, ".flac",
                            [new(Soulseek.FileAttributeType.Length, 180)]));
                    receive(new Soulseek.SearchResponse(username, 1, true, 1_000_000, 0, files,
                        lockedFileList: [new Soulseek.File(1, "Artist - Bare Locked.flac", 128, ".flac",
                            [new(Soulseek.FileAttributeType.Length, 180)])]));
                }
                return;
            }
            if (largeSearch || largeDirectorySearch || largeDistributedSearch || largeMusicSearch)
            {
                static Soulseek.File[] DirectoryFiles(int start, int count) => [.. Enumerable.Range(start, count)
                    .Select(file => new Soulseek.File(1, $@"Fixture\Album-000\track-000-{file:D6}.txt", 128, ".txt", []))];
                static Soulseek.File[] DistributedFiles(int start, int count) => [.. Enumerable.Range(start, count)
                    .Select(file => new Soulseek.File(1, $@"Fixture\Folder-{file / 200:D3}\File-{file:D6}.txt", 128, ".txt", []))];
                var callbackTimer = System.Diagnostics.Stopwatch.StartNew();
                TimeSpan pausedArrivals = TimeSpan.Zero;
                using var fixtureProcess = Process.GetCurrentProcess();
                var cpuAtStart = fixtureProcess.TotalProcessorTime;
                // Timed arrivals are the opt-in Load workload, not synchronization
                // sleeps in ordinary tests. Release completion explicitly below.
                using var arrivals = pacedMusicSearch ? new PeriodicTimer(TimeSpan.FromMilliseconds(40)) : null;
                int peerCount = largeDistributedSearch ? 20 : largeDirectorySearch ? 1
                    : largeMusicSearch ? musicSearchPeerCount : 100;
                for (int peerIndex = 0; peerIndex < peerCount; peerIndex++)
                {
                    if (largeDistributedSearch && peerIndex == 1)
                    {
                        await nextSearchBatch!.Task.WaitAsync(cancellationToken);
                        receive(new Soulseek.SearchResponse("fixture-peer-000", 21, true, 1_000_000, 0, DistributedFiles(1000, 4000)));
                    }
                    if (pacedMusicSearch && peerIndex == 50)
                    {
                        long pauseStarted = Stopwatch.GetTimestamp();
                        await nextSearchBatch!.Task.WaitAsync(cancellationToken);
                        pausedArrivals += Stopwatch.GetElapsedTime(pauseStarted);
                    }
                    if (peerIndex > 0 && arrivals != null)
                        await arrivals.WaitForNextTickAsync(cancellationToken);
                    Soulseek.File[] files = largeDistributedSearch ? DistributedFiles(0, peerIndex == 0 ? 1000 : 5000)
                        : largeMusicSearch
                        ? Enumerable.Range(0, musicSearchAlbumCount).SelectMany(album => Enumerable.Range(0, 10)
                            .Select(track => new Soulseek.File(1,
                                $@"Music\Fixture Artist\Fixture Album-{album:D2}\{track:D2} - Fixture Track-{(variedMusicTracks ? album * 10 + track : track):D2}.flac",
                                8_000_000 + track * 100_000, ".flac",
                                [new(Soulseek.FileAttributeType.BitRate, 320),
                             new(Soulseek.FileAttributeType.Length, 200 + (variedMusicTracks ? 0 : album * 60) + track * 5)])))
                            .ToArray()
                        : largeDirectorySearch ? DirectoryFiles(0, stagedLargeDirectorySearch ? 1000 : 100000)
                        : Enumerable.Range(0, 100)
                            .Select(file => new Soulseek.File(1,
                                $@"Fixture\Album-{peerIndex:D3}\track-{peerIndex:D3}-{file:D3}.txt",
                                128, ".txt", []))
                            .ToArray();
                    receive(new Soulseek.SearchResponse($"fixture-peer-{peerIndex:D3}", peerIndex + 1,
                        true, 1_000_000, 0, files));
                }
                if (stagedLargeDirectorySearch)
                {
                    await nextSearchBatch!.Task.WaitAsync(cancellationToken);
                    receive(new Soulseek.SearchResponse("fixture-peer-000", 2, true, 1_000_000, 0, DirectoryFiles(1000, 1000)));
                    await finishSearch!.Task.WaitAsync(cancellationToken);
                    receive(new Soulseek.SearchResponse("fixture-peer-000", 3, true, 1_000_000, 0, DirectoryFiles(2000, 98000)));
                    Console.WriteLine($"E2E_METRIC staged-directory-search searches={networkSearches} files=100000");
                }
                Console.WriteLine($"E2E_METRIC {(largeMusicSearch ? "large-music-search" : "large-search")}-receive-ms={callbackTimer.ElapsedMilliseconds}");
                if (pacedMusicSearch)
                {
                    Console.WriteLine($"E2E_METRIC paced-search searches={networkSearches} peers=100 receive-ms={callbackTimer.ElapsedMilliseconds} cpu-ms={(fixtureProcess.TotalProcessorTime - cpuAtStart).TotalMilliseconds:F0} paused-ms={pausedArrivals.TotalMilliseconds:F0} active-ms={(callbackTimer.Elapsed - pausedArrivals).TotalMilliseconds:F0}");
                    await finishSearch!.Task.WaitAsync(cancellationToken);
                }
                return;
            }
            if (mixedVisibilitySearch)
            {
                foreach ((string username, long size) in new[]
                    { ("CasePeer", 128L), ("casepeer", 256L) })
                {
                    receive(new Soulseek.SearchResponse(username, 2, true, 1_000_000, 0,
                        fileList: [new Soulseek.File(1, $@"Fixture\Album\{username}-Public.flac", size, ".flac", [])],
                        lockedFileList: [new Soulseek.File(1, @"Fixture\Album\Locked.flac", 64, ".flac", [])]));
                }
                receive(new Soulseek.SearchResponse("LockedOnly", 1, false, 1_000_000, 0,
                    fileList: [],
                    lockedFileList: [new Soulseek.File(1, @"Fixture\Album\Only-locked.flac", 64, ".flac", [])]));
                return;
            }
            if (conditionedMusicSearch)
            {
                static Soulseek.File[] Tracks(string number, int bitrate) => [.. new[] { "flac", "mp3" }
                .Select(format => new Soulseek.File(1, $@"Music\Artist\Album\{number} - Track.{format}",
                    128, "." + format, [new(Soulseek.FileAttributeType.BitRate, bitrate),
                        new(Soulseek.FileAttributeType.Length, 180),
                        new(Soulseek.FileAttributeType.SampleRate, 44_100)]))];
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0, Tracks("01", 320)));
                if (nextSearchBatch is { } next) await next.Task.WaitAsync(cancellationToken);
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0,
                    fileList: Tracks("02", 128), lockedFileList: Tracks("03 - Locked", 128)));
                if (finishSearch is { } finish) await finish.Task.WaitAsync(cancellationToken);
                return;
            }
            if (ordinaryTrackSearch)
            {
                Soulseek.FileAttribute[] audio =
                [
                    new(Soulseek.FileAttributeType.BitRate, 320),
                new(Soulseek.FileAttributeType.SampleRate, 44_100),
                new(Soulseek.FileAttributeType.Length, 344),
                ];
                receive(new Soulseek.SearchResponse("tape_loop", 1, false, 2_700_000, 7,
                    [new Soulseek.File(1, @"incoming\autechre\gantz graf live.mp3", 13_200_000, ".mp3", audio)]));
                return;
            }
            if (concurrentRetrievalSearch)
            {
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Fixture\First song.txt", 128, ".txt", [])]));
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Second\Second song.txt", 128, ".txt", [])]));
                return;
            }
            if (mixedRetrievalSearch)
            {
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Fixture\Selected.txt", 128, ".txt", [])]));
                return;
            }
            if (albumArtSearch)
            {
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"First\Fixture Album\01 - Intro.flac", 128, ".flac", []),
                 new Soulseek.File(1, @"First\Fixture Album\02 - Finale.flac", 128, ".flac", [])]));
                receive(new Soulseek.SearchResponse("second-peer", 2, false, 100_000, 10,
                    [new Soulseek.File(1, @"Second\Fixture Album\01 - Intro.flac", 128, ".flac", []),
                 new Soulseek.File(1, @"Second\Fixture Album\02 - Finale.flac", 128, ".flac", []),
                 .. Enumerable.Range(0, albumArtFileCount).Select(index => new Soulseek.File(1,
                     albumArtFileCount == 1 ? @"Second\Fixture Album\Cover.jpg"
                         : $@"Second\Fixture Album\Art{index:D4}.jpg", 128, ".jpg", []))]));
                return;
            }
            if (pagedAlbumGroupSearch)
            {
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 3_000_000, 0,
                    Enumerable.Range(1, 25)
                        .Select(index => new Soulseek.File(1, $@"First\Fixture Album\{index:D2} - Track.flac", 128, ".flac", []))
                        .ToArray()));
                // Exercise a real 100-source production page, then the final
                // source. Rank the familiar second peer after these 99 peers.
                for (int peerIndex = 1; peerIndex <= 99; peerIndex++)
                    receive(new Soulseek.SearchResponse($"extra-peer-{peerIndex:D3}", peerIndex + 2, true, 2_000_000, 0,
                        Enumerable.Range(1, 25)
                            .Select(index => new Soulseek.File(1, $@"Extra\Fixture Album\{index:D2} - Track.flac", 128, ".flac", []))
                            .ToArray()));
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0,
                    Enumerable.Range(1, 25)
                        .Select(index => new Soulseek.File(1, $@"Second\Fixture Album\{index:D2} - Track.flac", 128, ".flac", []))
                        .ToArray()));
                return;
            }
            if (albumGroupSearch)
            {
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"First\Fixture Album\01 - Intro.flac", 128, ".flac", []),
                 new Soulseek.File(1, @"First\Fixture Album\02 - Finale.flac", 128, ".flac", [])],
                    lockedFileList: mixedAlbumGroupSearch
                        ? [new Soulseek.File(1, @"First\Fixture Album\03 - Locked.flac", 256, ".flac", [])] : []));
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Second\Fixture Album\01 - Intro.flac", 128, ".flac", []),
                 new Soulseek.File(1, @"Second\Fixture Album\02 - Finale.flac", 128, ".flac", [])],
                    lockedFileList: mixedAlbumGroupSearch
                        ? [new Soulseek.File(1, @"Second\Fixture Album\03 - Locked.flac", 256, ".flac", [])] : []));
                return;
            }
            if (richAlbumGroupSearch)
            {
                // Album aggregation groups by track-length profile, so distinct
                // profiles produce three groups while each peer pair remains equivalent.
                Soulseek.FileAttribute[] AudioFor(string album) =>
                [
                    new(Soulseek.FileAttributeType.BitRate, 320),
                new(Soulseek.FileAttributeType.Length, album switch
                {
                    "Alpha" => 210,
                    "Beta" => 300,
                    _ => 390,
                }),
                ];
                Soulseek.File[] files = [.. new[] { "Alpha", "Beta", "Gamma" }
                .SelectMany(album => Enumerable.Range(1, 4).Select(track =>
                    new Soulseek.File(1,
                        $@"Fixture\Fixture Album {album}\{track:D2} - {album} Track.mp3",
                        8_000_000, ".mp3", AudioFor(album))))];
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0, files));
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0, files));
                return;
            }
            if (groupSearch)
            {
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"First\Fixture song.flac", 128, ".flac", [])]));
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Second\Fixture song.flac", 128, ".flac", [])]));
                return;
            }
            if (richGroupSearch)
            {
                Soulseek.FileAttribute[] audio =
                [
                    new(Soulseek.FileAttributeType.BitRate, 320),
                new(Soulseek.FileAttributeType.Length, 210),
                ];
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true,
                    1_000_000, 0,
                    [new Soulseek.File(1, @"Fixture\Fixture Alpha.mp3", 8_000_000, ".mp3", audio),
                 new Soulseek.File(1, @"Fixture\Fixture Beta.mp3", 8_000_000, ".mp3", audio),
                 new Soulseek.File(1, @"Fixture\Fixture Gamma.mp3", 8_000_000, ".mp3", audio)]));
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Fixture\Fixture Alpha.mp3", 8_000_000, ".mp3", audio),
                 new Soulseek.File(1, @"Fixture\Fixture Beta.mp3", 8_000_000, ".mp3", audio),
                 new Soulseek.File(1, @"Fixture\Fixture Gamma.mp3", 8_000_000, ".mp3", audio)]));
                if (nextSearchBatch is { } groupBatch)
                {
                    await groupBatch.Task.WaitAsync(cancellationToken);
                    receive(new Soulseek.SearchResponse("late-peer", 3, true, 20_000_000, 0,
                        [new Soulseek.File(1, @"Fixture\Fixture Alpha.mp3", 8_000_000, ".mp3", audio),
                     new Soulseek.File(1, @"Fixture\Fixture Gamma.mp3", 8_000_000, ".mp3", audio),
                     new Soulseek.File(1, @"Fixture\Fixture Delta.mp3", 8_000_000, ".mp3", audio)]));
                    receive(new Soulseek.SearchResponse("partner-peer", 4, true, 2_000_000, 0,
                        [new Soulseek.File(1, @"Fixture\Fixture Delta.mp3", 8_000_000, ".mp3", audio)]));
                    if (finishSearch is { } groupFinish)
                        await groupFinish.Task.WaitAsync(cancellationToken);
                }
                return;
            }
            if (directorySelectionSearch)
            {
                receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Fixture\Selected.txt", 128, ".txt", []),
                 new Soulseek.File(1, @"Fixture\Unselected.txt", 128, ".txt", [])]));
                if (nextSearchBatch is { } directoryBatch)
                {
                    await directoryBatch.Task.WaitAsync(cancellationToken);
                    receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                        [new Soulseek.File(1, @"Fixture\Nested\LateNested.txt", 128, ".txt", []),
                         new Soulseek.File(1, @"Fixture\LateRoot.txt", 128, ".txt", [])]));
                    if (finishSearch is { } directoryFinish)
                        await directoryFinish.Task.WaitAsync(cancellationToken);
                }
                return;
            }
            receive(new Soulseek.SearchResponse("fixture-peer", 1, true, 1_000_000, 0,
                [new Soulseek.File(1, musicSearch ? @"Fixture\Fixture song.flac" : @"Fixture\Fixture song.txt",
                128, musicSearch ? ".flac" : ".txt", [])]));
            if (nextSearchBatch is { } batch)
            {
                await batch.Task.WaitAsync(cancellationToken);
                receive(new Soulseek.SearchResponse("second-peer", 2, true, 1_000_000, 0,
                    [new Soulseek.File(1, @"Second\Later song.txt", 128, ".txt",
                        [new(Soulseek.FileAttributeType.Length, int.MinValue)])]));
                if (finishSearch is { } finish) await finish.Task.WaitAsync(cancellationToken);
            }
        };
        ReadOnlyMemory<byte> downloadChunk = new byte[64 * 1024];
        peer.DownloadHandler = async arguments =>
        {
            downloadStarted.TrySetResult();
            string username = (string)arguments[0]!;
            string filename = (string)arguments[1]!;
            if (longSongFallback)
            {
                fallbackDownloadTargets.Enqueue(username);
                if (username != "Péér 099")
                    throw new Soulseek.TransferRejectedException("Fixture candidate no longer shares this file.");
            }
            if (Interlocked.Exchange(ref rejectNextDownload, 0) != 0)
                throw new Soulseek.TransferRejectedException("Fixture rejects the first attempt only.");
            var streamFactory = (Func<Task<Stream>>)arguments[2]!;
            long size = (long?)arguments[3] ?? 128;
            var options = arguments.OfType<Soulseek.TransferOptions>().Single();
            var cancellationToken = arguments.OfType<CancellationToken>().LastOrDefault();
            Stream output = await streamFactory();
            DateTime started = DateTime.UtcNow;
            async Task WriteBytesAsync(long count)
            {
                while (count > 0)
                {
                    int length = (int)Math.Min(count, downloadChunk.Length);
                    await output.WriteAsync(downloadChunk[..length], cancellationToken);
                    if (scenario.Metrics) Interlocked.Add(ref downloadPayloadBytes, length);
                    count -= length;
                }
            }
            Soulseek.Transfer Snapshot(Soulseek.TransferStates state, long bytes) => new(
                Soulseek.TransferDirection.Download, username, filename, 1, state,
                size, 0, bytes, size, started, state.HasFlag(Soulseek.TransferStates.Completed) ? DateTime.UtcNow : null,
                null, null, null);
            var active = Snapshot(Soulseek.TransferStates.InProgress, 0);
            options.StateChanged?.Invoke(new(Soulseek.TransferStates.Queued, active));
            long midpoint = size / 2;
            await WriteBytesAsync(midpoint);
            options.ProgressUpdated?.Invoke(new(0, Snapshot(Soulseek.TransferStates.InProgress, midpoint)));
            if (downloadGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            if (filename == @"Fixture\Fails.txt")
                throw new IOException("Fixture peer failed this independent file transfer.");
            if (filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && artDownloadGate is { } artGate)
                await artGate.Task.WaitAsync(cancellationToken);
            await WriteBytesAsync(size - midpoint);
            var completed = Snapshot(Soulseek.TransferStates.Completed, size);
            options.ProgressUpdated?.Invoke(new(0, completed));
            options.StateChanged?.Invoke(new(Soulseek.TransferStates.InProgress, completed));
            return completed;
        };
        await using var app = ServerHost.Build(
            ["--webroot", webRoot],
            new ServerOptions
            {
                Authentication = new() { PasswordHash = "AQAAAAIAAYagAAAAEXNvY2tzZWVrLWZpeHR1cmUhLYKb2YbUDRN/4hvcRClorEeUEsqeimyd/peWr4DCr5Q=", AllowHttp = true },
                Engine = new EngineSettings
                {
                    Username = "e2e-local",
                    Password = "e2e-only",
                    ListenPort = uploadShares ? 49998 : null,
                    Sharing = new SharingSettings
                    {
                        Roots = uploadShares
                        ? [new ShareRootSettings { LocalPath = sharedDirectory, Alias = "Public" }]
                        : []
                    },
                    Uploads = new UploadSettings { Slots = uploadShares ? 1 : 10 },
                    LogLevel = scenario.ApplicationLogs ? LogLevel.Information : LogLevel.None,
                },
                OperatorDownloadSettings = new Sockseek.Api.DownloadSettingsPatchDto(
                    Output: new Sockseek.Api.OutputSettingsPatchDto(ParentDir: Path.Combine(temporary, "downloads"))),
                Persistence = new() { Enabled = true, DataDirectory = dataDirectory },
                ClientFactory = _ => peer.Client,
            }, "http://127.0.0.1:0", logToConsole: scenario.ApplicationLogs);
        using var readMetrics = scenario.Metrics ? new ReadMetrics() : null;
        if (readMetrics != null) app.Use(readMetrics.MeasureAsync);
        if (scenario.Metrics)
            Console.WriteLine($"E2E_METRIC startup-build-ms={startupTimer.ElapsedMilliseconds}");
        await app.StartAsync();
        // Fixture completion controls observe existing facts. Subscribing to
        // JobUpserted would manufacture full wire summaries/actions for every
        // child even when the actual browser only observes the overview.
        app.Services.GetRequiredService<EngineStateStore>().StateBatchPublished += update =>
        {
            if (update.Scope.Kind != Sockseek.Api.StateStreamScopeKind.Daemon) return;
            foreach (var change in update.State.Jobs)
                if ((change.Current.Kind is Sockseek.Api.ServerJobKind.Album or Sockseek.Api.ServerJobKind.RemoteDirectory)
                    && change.Current.LifecycleState == Sockseek.Api.ServerJobLifecycleState.Terminal)
                    directoryComplete.TrySetResult();
        };
        if (scenario.Metrics)
        {
            // Preserve the WAL index through service disposal. Reopening only
            // for the final drain can add recovery/backfill work to a comparison.
            // No transaction stays open, so this does not pin a reader snapshot.
            finalCheckpointConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            }.ToString());
            await finalCheckpointConnection.OpenAsync();
            await using (var attach = finalCheckpointConnection.CreateCommand())
            {
                attach.CommandText = "PRAGMA schema_version;";
                await attach.ExecuteScalarAsync();
            }
            Console.WriteLine($"E2E_METRIC startup-start-ms={startupTimer.ElapsedMilliseconds}");
            Console.WriteLine("E2E_METRIC startup-database " + readMetrics!.TakeDatabaseInterval());
            var timedPersistence = app.Services.GetRequiredService<Sockseek.Server.Persistence.PersistenceCoordinator>();
            timedPersistence.Health!.CommitCompleted += () =>
            {
                if (timedPersistence.HealthSnapshot is { } commit)
                    Console.WriteLine($"E2E_METRIC persistence-commit elapsed-ms={startupTimer.ElapsedMilliseconds} "
                        + $"duration-ms={commit.LastCommitDurationMilliseconds:F1} mutations={commit.LastBatchMutationCount} "
                        + $"commits-total={commit.SuccessfulCommitCount} "
                        + $"dropped-progress={commit.DroppedProgressCount} failed={commit.PermanentlyFailedMutationCount} "
                        + readMetrics!.TakeDatabaseInterval());
            };
        }
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var probeKey = await app.Services.GetRequiredService<OperatorAuthentication>()
            .CreateKeyAsync("Fixture diagnostics", CancellationToken.None);
        Console.WriteLine("E2E_READY " + JsonSerializer.Serialize(new { url = address, apiKey = probeKey.Secret }));
        // Out-of-band fixture controls never become production HTTP endpoints.
        int incomingChatId = 500;
        while (await input.MoveNextAsync() && input.Current is { Length: > 0 } command)
        {
            if (command == "stop") break;
            switch (command)
            {
                case string preferences when preferences.StartsWith("webui-settings:", StringComparison.Ordinal):
                {
                    string[] parts = preferences.Split(':');
                    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.WebUi = new()
                    {
                        Theme = parts[1], DefaultSearchMode = parts[2],
                    };
                    break;
                }
                case "presence-away": peer.RaiseUserStatus("Presence Peer", Soulseek.UserPresence.Away); break;
                case "terminal-transfer-committed":
                    app.Services.GetRequiredService<EngineStateStore>().NotifyTerminalTransferCommitted();
                    break;
                case "expire-job-previews":
                case "forget-job-previews":
                {
                    // Exercise actual expiry or loss of temporary planning state
                    // through the real commit API, without a clock wait.
                    string previewDatabase = Directory.GetFiles(
                        Path.Combine(temporary, "data", "planning", "job-preview-spools"),
                        "preview.db", SearchOption.AllDirectories).Single();
                    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = previewDatabase,
                        ForeignKeys = true,
                    }.ToString());
                    await connection.OpenAsync();
                    await using var expire = connection.CreateCommand();
                    expire.CommandText = command == "expire-job-previews"
                        ? "UPDATE job_previews SET expires_at_utc = 0 WHERE state IN ('Ready', 'PartiallyReady');"
                        : "DELETE FROM job_previews WHERE state IN ('Ready', 'PartiallyReady');";
                    await expire.ExecuteNonQueryAsync();
                    break;
                }
                case string prune when prune.StartsWith("prune-search-results:", StringComparison.Ordinal):
                {
                    Guid jobId = Guid.Parse(prune["prune-search-results:".Length..]);
                    var persistence = app.Services.GetRequiredService<Sockseek.Server.Persistence.PersistenceCoordinator>();
                    await persistence.WaitForJobHandoffAsync(jobId, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    if (app.Services.GetRequiredService<EngineSupervisor>().StateStore.GetJobSummary(jobId) != null
                        || persistence.GetRetiringSearchSource(jobId) != null)
                        throw new InvalidOperationException("Prune only a retired search in this fixture.");
                    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = SockseekDataPaths.ResolveDatabasePath(Path.Combine(temporary, "data")),
                    }.ToString());
                    await connection.OpenAsync();
                    await using var age = connection.CreateCommand();
                    // Results expire at 30 days; job/submission history remains retained.
                    age.CommandText = "UPDATE jobs SET completed_at_utc = $aged WHERE id = $job;";
                    age.Parameters.AddWithValue("$aged", DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds());
                    age.Parameters.AddWithValue("$job", jobId);
                    if (await age.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Search history was not retained.");
                    var result = await persistence.RunRetentionAsync(CancellationToken.None);
                    if (result.SearchesMarkedPruned != 1 || result.PrunedSearchResults < 1 || result.PrunedJobs != 0)
                        throw new InvalidOperationException("Expected only the target search's raw results to expire.");
                    break;
                }
                case "expire-search-views":
                {
                    // Simulate loss of temporary read state on restart. Accepted
                    // receipts and raw observations remain in durable storage.
                    app.Services.GetRequiredService<Sockseek.Server.Planning.SearchViewCoordinator>()
                        .Cache.Clear();
                    break;
                }
                case "retained-imports":
                {
                    // One hundred retained submissions, each with its source
                    // Extract and generated Song. Avoid 100 live HTTP submissions
                    // just to set up a listing/pagination check.
                    var persistence = app.Services.GetRequiredService<Sockseek.Server.Persistence.PersistenceCoordinator>();
                    int firstDisplayId = Sockseek.Core.Jobs.JobDisplayIds.Next();
                    Sockseek.Core.Jobs.JobDisplayIds.ContinueAfter(firstDisplayId + 200);
                    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = SockseekDataPaths.ResolveDatabasePath(Path.Combine(temporary, "data")),
                        ForeignKeys = true,
                    }.ToString());
                    await connection.OpenAsync();
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        BEGIN;
                        WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100)
                        INSERT INTO submissions(id, submitted_at_utc, specification_schema_version, revision)
                        SELECT upper(printf('51000000-0000-0000-0000-%012x', x)), $created + x, $schema, 1 FROM n;
                        INSERT INTO submission_specifications(submission_id, specification_json)
                        SELECT id, $spec FROM submissions;
                        WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100),
                        entries AS (SELECT x, 0 AS result FROM n UNION ALL SELECT x, 1 FROM n)
                        INSERT INTO jobs (
                            id, workflow_id, submission_id, semantic_role, source_job_id, result_job_id,
                            last_runtime_id, last_sequence, display_id, kind, lifecycle_state, activity_phase,
                            terminal_outcome, skip_reason, cancellation_source, failure_reason,
                            item_name, query_text, source_detail, created_at_utc, updated_at_utc, completed_at_utc,
                            revision, payload_schema_version, payload_json)
                        SELECT upper(printf('%s000000-0000-0000-0000-%012x', CASE result WHEN 0 THEN '52' ELSE '53' END, x)),
                            upper(printf('52000000-0000-0000-0000-%012x', x)),
                            upper(printf('51000000-0000-0000-0000-%012x', x)),
                            CASE result WHEN 0 THEN 'UserRoot' ELSE 'SemanticResult' END,
                            CASE result WHEN 1 THEN upper(printf('52000000-0000-0000-0000-%012x', x)) END,
                            CASE result WHEN 0 THEN upper(printf('53000000-0000-0000-0000-%012x', x)) END,
                            $runtime, x * 2 + result, $first + (x - 1) * 2 + result,
                            CASE result WHEN 0 THEN 'Extract' ELSE 'Song' END,
                            'Terminal', 'None', 'Succeeded', 'None', 'None', 'None',
                            printf('Paging parent %d', x), printf('Paging parent %d', x),
                            CASE result WHEN 0 THEN 'Text import' END,
                            $created + x, $created + x, $created + x,
                            2, 1, CASE result WHEN 0 THEN json_object('Input', 'Fixture song', 'InputType', 'String')
                                ELSE json_object('Query', json_object('Title', printf('Paging parent %d', x)), 'CandidateCount', 1) END
                        FROM entries;
                        COMMIT;
                        """;
                    var specification = Sockseek.Core.Planning.SubmissionSpecification.Create(
                        new Sockseek.Core.Jobs.ExtractJob("Fixture song"), new DownloadSettings());
                    insert.Parameters.AddWithValue("$spec", Sockseek.Core.Planning.SubmissionSpecificationCodec.Serialize(specification));
                    insert.Parameters.AddWithValue("$schema", specification.SchemaVersion);
                    insert.Parameters.AddWithValue("$runtime", persistence.Runtime!.RuntimeId.ToString().ToUpperInvariant());
                    insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
                    insert.Parameters.AddWithValue("$first", firstDisplayId);
                    await insert.ExecuteNonQueryAsync();
                    break;
                }
                case "large-retained-transfers":
                {
                    // Seed retained history only, not 100k network downloads.
                    // All reads, group projections and later UI mutations use
                    // the real daemon and its migrated domain database.
                    var seeded = Stopwatch.StartNew();
                    var persistence = app.Services.GetRequiredService<Sockseek.Server.Persistence.PersistenceCoordinator>();
                    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = SockseekDataPaths.ResolveDatabasePath(Path.Combine(temporary, "data")),
                        ForeignKeys = true,
                    }.ToString());
                    await connection.OpenAsync();
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000)
                        INSERT INTO transfers (
                            id, last_runtime_id, last_sequence, direction, source,
                            username, remote_path, local_path, state, terminal_outcome,
                            total_bytes, transferred_bytes, attempt_count, created_at_utc,
                            started_at_utc, completed_at_utc, failure_reason, cancellation_source,
                            file_name, file_size_bytes, file_extension,
                            group_ref, group_display_path, revision)
                        SELECT upper(printf('60000000-0000-0000-0000-%012x', x)), $runtime, x,
                            'Download', 'SoulseekPeer', 'history-peer',
                            printf('Library\Folder%03d\Track%06d.flac', (x - 1) / 2000, x),
                            printf('/fixture/history/Track%06d.flac', x), 'Completed', 'Succeeded',
                            5000000, 5000000, 1, $created + x, $created + x, $created + x,
                            'None', 'None', printf('Track%06d.flac', x), 5000000, '.flac',
                            printf('70000000-0000-0000-0000-%012x', (x - 1) / 2000),
                            printf('Library/Folder%03d', (x - 1) / 2000), 2
                        FROM n;
                        """;
                    insert.Parameters.AddWithValue("$runtime", persistence.Runtime!.RuntimeId.ToString().ToUpperInvariant());
                    insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
                    await insert.ExecuteNonQueryAsync();
                    if (scenario.Metrics)
                        Console.WriteLine($"E2E_METRIC retained-transfers-seed-ms={seeded.ElapsedMilliseconds}");
                    break;
                }
                case "request-upload":
                case "request-queued-upload":
                    var sharing = app.Services.GetRequiredService<DaemonRuntime>().Sharing
                        ?? throw new InvalidOperationException("Sharing is not ready.");
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnSharingReady(Sockseek.Api.SharingStateDto state, Sockseek.Api.UploadRuntimeStateDto _)
                    {
                        if (state.State == Sockseek.Api.DaemonFeatureState.Ready) ready.TrySetResult();
                    }
                    sharing.StateChanged += OnSharingReady;
                    try
                    {
                        OnSharingReady(sharing.GetSharingState(), sharing.GetUploadRuntimeState());
                        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally { sharing.StateChanged -= OnSharingReady; }
                    await sharing.Adapter.EnqueueUploadAsync("upload-peer", new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 12345),
                        command == "request-upload" ? @"Public\Album\Uploaded.txt" : @"Public\Album\Queued.txt");
                    break;
                case "fail-next-upload": failNextUpload = true; break;
                case "release-upload": uploadGate.TrySetResult(); break;
                case "music-search": musicSearch = true; break;
                case "large-search": largeSearch = true; break;
                case "large-directory-search": largeDirectorySearch = true; break;
                case "large-distributed-search":
                    largeDistributedSearch = true;
                    nextSearchBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "staged-large-directory-search":
                    largeDirectorySearch = stagedLargeDirectorySearch = true;
                    nextSearchBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    finishSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "large-music-search": largeMusicSearch = true; break;
                case "large-music-search-100k":
                    largeMusicSearch = true;
                    musicSearchPeerCount = 1_000;
                    break;
                case "large-varied-music-search":
                    largeMusicSearch = variedMusicTracks = true;
                    musicSearchPeerCount = 10;
                    musicSearchAlbumCount = 1_000;
                    break;
                case "large-shared-album-search":
                    largeMusicSearch = true;
                    musicSearchPeerCount = 10_000;
                    musicSearchAlbumCount = 1;
                    break;
                case "large-single-peer-music-search":
                    largeMusicSearch = true;
                    musicSearchPeerCount = 1;
                    musicSearchAlbumCount = 10_000;
                    break;
                case "paced-music-search":
                    largeMusicSearch = pacedMusicSearch = true;
                    nextSearchBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    finishSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "large-peer-shares": largePeerShares = true; break;
                case "mixed-visibility-search": mixedVisibilitySearch = true; break;
                case "ordinary-track-search": ordinaryTrackSearch = true; break;
                case "staged-conditioned-music-search":
                    conditionedMusicSearch = true;
                    nextSearchBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    finishSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "group-search": groupSearch = true; break;
                case "rich-group-search": richGroupSearch = true; break;
                case "staged-rich-group-search":
                    richGroupSearch = true;
                    nextSearchBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    finishSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "long-song-fallback": longSongFallback = true; break;
                case "report-fallback-downloads":
                    Console.WriteLine("E2E_FALLBACK_DOWNLOADS " + JsonSerializer.Serialize(fallbackDownloadTargets.ToArray()));
                    break;
                case "album-group-search": albumGroupSearch = true; break;
                case "mixed-album-group-search": albumGroupSearch = true; mixedAlbumGroupSearch = true; break;
                case "rich-album-group-search": richAlbumGroupSearch = true; break;
                case "paged-album-group-search": pagedAlbumGroupSearch = true; break;
                case "album-art-search": albumArtSearch = true; break;
                case "large-album-art-search": albumArtSearch = true; albumArtFileCount = 1001; break;
                case "bare-filename-search": bareFilenameSearch = true; break;
                case "hold-art-download": artDownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                case "release-art-download": artDownloadGate?.TrySetResult(); break;
                case "wait-directory-complete": await directoryComplete.Task.WaitAsync(TimeSpan.FromSeconds(120)); break;
                case string wait when wait.StartsWith("wait-job-complete:", StringComparison.Ordinal):
                {
                    Guid jobId = Guid.Parse(wait["wait-job-complete:".Length..]);
                    var state = app.Services.GetRequiredService<EngineStateStore>();
                    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Changed(StateUpdate update)
                    {
                        if (update.Scope.Kind != Sockseek.Api.StateStreamScopeKind.Daemon) return;
                        foreach (var change in update.State.Jobs)
                            if (change.JobId == jobId && change.Current.LifecycleState == Sockseek.Api.ServerJobLifecycleState.Terminal)
                                completed.TrySetResult();
                    }
                    state.StateBatchPublished += Changed;
                    try
                    {
                        if (state.GetJobSummary(jobId)?.LifecycleState == Sockseek.Api.ServerJobLifecycleState.Terminal)
                            completed.TrySetResult();
                        await completed.Task.WaitAsync(TimeSpan.FromSeconds(120));
                    }
                    finally { state.StateBatchPublished -= Changed; }
                    break;
                }
                case "directory-selection-search": directorySelectionSearch = true; break;
                case "concurrent-retrieval-search": concurrentRetrievalSearch = true; break;
                case "mixed-retrieval-search": mixedRetrievalSearch = true; break;
                case "hold-directory-browse": directoryBrowseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); break;
                case "release-directory-browse": directoryBrowseGate?.TrySetResult(); directoryBrowseGate = null; break;
                case "staged-search":
                    nextSearchBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    finishSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "empty-search":
                    emptySearch = true;
                    finishSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "next-search-batch": nextSearchBatch?.TrySetResult(); break;
                case "finish-search": finishSearch?.TrySetResult(); break;
                case "wait-search-started": await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); break;
                case "hold-download":
                    downloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "wait-download-started": await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)); break;
                case string report when report.StartsWith("report-search-projection:", StringComparison.Ordinal):
                {
                    // Read the temporary snapshot directly: the coordinator's
                    // summary API would request more projection work.
                    Guid viewId = Guid.Parse(report["report-search-projection:".Length..]);
                    var store = app.Services.GetRequiredService<Sockseek.Server.Planning.SearchViewCoordinator>().Cache;
                    var view = store.Get(viewId)
                        ?? throw new InvalidOperationException("Search View is unavailable.");
                    Console.WriteLine("E2E_PROJECTION_STATE " + JsonSerializer.Serialize(new
                    {
                        viewId = view.Id, revision = view.Revision,
                        publicFileCount = view.Counters.PublicFileCount, isComplete = view.IsComplete,
                    }));
                    break;
                }
                case "report-peer-browses":
                    Console.WriteLine($"E2E_METRIC peer-browses count={Volatile.Read(ref peerBrowseCount)}");
                    break;
                case "release-download": downloadGate?.TrySetResult(); break;
                case "reject-next-download": Interlocked.Exchange(ref rejectNextDownload, 1); break;
                case "incoming-chat":
                    incomingChatId++;
                    peer.RaisePrivateMessage(new Soulseek.PrivateMessageReceivedEventArgs(
                        incomingChatId, DateTime.UtcNow, "Scroll Peer", $"Incoming while reading {incomingChatId}", replayed: false));
                    break;
                case "seed-chat-history":
                case "seed-chat-outage":
                {
                    // Retained-history setup uses the existing domain batch,
                    // rather than hundreds of independently committed HTTP sends.
                    var store = app.Services.GetRequiredService<Sockseek.Server.Persistence.PersistenceCoordinator>().Chat
                        ?? throw new InvalidOperationException("Chat persistence is unavailable.");
                    bool outage = input.Current == "seed-chat-outage";
                    DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-10);
                    await store.AcceptIncomingMessagesAsync(Enumerable.Range(0, 503)
                        .Select(index => (ChatInboundMessage)new PrivateChatInboundMessage(
                            "e2e-local", outage ? "Reconnect Peer" : "History Peer",
                            $"{(outage ? "Offline" : "History")} {index:D3}", 20_000 + index, started.AddSeconds(index)))
                        .ToArray());
                    break;
                }
                case "incoming-room-chat":
                    peer.RaiseRoomMessage(new Soulseek.RoomMessageReceivedEventArgs(
                        "room", "neonrain", "anyone heard the new Broadcast archival release?"));
                    break;
                default: throw new ArgumentException($"Unknown fixture command: {command}");
            }
            Console.WriteLine("E2E_ACK " + command);
        }
        downloadGate?.TrySetCanceled();
        artDownloadGate?.TrySetCanceled();
        uploadGate.TrySetResult();
        nextSearchBatch?.TrySetCanceled();
        finishSearch?.TrySetCanceled();
        await app.StopAsync();
        readMetrics?.WriteSummary();
        if (scenario.Metrics && app.Services.GetRequiredService<Sockseek.Server.Persistence.PersistenceCoordinator>()
            .HealthSnapshot is { } finalPersistence)
            Console.WriteLine("E2E_PERSISTENCE_SUMMARY " + JsonSerializer.Serialize(new
            {
                droppedProgress = finalPersistence.DroppedProgressCount,
                permanentlyFailed = finalPersistence.PermanentlyFailedMutationCount,
            }));
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (scenario.Metrics)
        {
            // A last read-only connection can leave a durable WAL after shutdown.
            // Materialize it explicitly so delayed copying cannot look like a
            // reduction in total writes. Report this extra drain separately.
            string database = SockseekDataPaths.ResolveDatabasePath(Path.Combine(temporary, "data"));
            var beforeFinalCheckpoint = ReadStorageCounters();
            var finalCheckpoint = Stopwatch.StartNew();
            await using (var connection = finalCheckpointConnection ?? new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            }.ToString()))
            {
                if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var result = await command.ExecuteReaderAsync();
                if (!await result.ReadAsync() || result.GetInt32(0) != 0)
                    throw new InvalidOperationException("The fixture's final checkpoint remained blocked after shutdown.");
            }
            finalCheckpoint.Stop();
            var afterFinalCheckpoint = ReadStorageCounters();
            long? finalCheckpointWrites = beforeFinalCheckpoint.HasValue && afterFinalCheckpoint.HasValue
                ? afterFinalCheckpoint.Value.Written - beforeFinalCheckpoint.Value.Written : null;
            // Include final backfill before deleting fixture files. Linux
            // attributes storage writes to this process; this is neither a count
            // of SQLite logical writes nor a whole-device traffic measurement.
            measuredProcess!.Refresh();
            var storageAfter = ReadStorageCounters();
            long? written = storageBefore.HasValue && storageAfter.HasValue
                ? storageAfter.Value.Written - storageBefore.Value.Written : null;
            long? cancelled = storageBefore.HasValue && storageAfter.HasValue
                ? storageAfter.Value.Cancelled - storageBefore.Value.Cancelled : null;
            Console.WriteLine("E2E_STORAGE_SUMMARY " + JsonSerializer.Serialize(new
            {
                processCpuMilliseconds = (measuredProcess.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                processUserCpuMilliseconds = (measuredProcess.UserProcessorTime - userCpuBefore).TotalMilliseconds,
                processKernelCpuMilliseconds = (measuredProcess.PrivilegedProcessorTime - kernelCpuBefore).TotalMilliseconds,
                processAllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore,
                // The fixture can reuse its process across scenarios. A peak
                // cannot be subtracted/reset like the per-scenario CPU counter.
                processLifetimePeakWorkingSetBytes = measuredProcess.PeakWorkingSet64,
                processStorageWriteBytes = written,
                processCancelledWriteBytes = cancelled,
                finalCheckpointMilliseconds = finalCheckpoint.Elapsed.TotalMilliseconds,
                finalCheckpointWriteBytes = finalCheckpointWrites,
                retainedDatabaseBytes = File.Exists(database) ? new FileInfo(database).Length : 0,
                retainedDataBytes = DirectoryBytes(Path.Combine(temporary, "data")),
                // Tests can choose an output path outside the daemon fixture.
                // Count actual payload writes, including failed partial attempts.
                downloadPayloadBytes,
                fixtureFileBytes = DirectoryBytes(Path.Combine(temporary, "shared"))
                    + DirectoryBytes(Path.Combine(temporary, "wwwroot")),
            }));
        }
        // Opt-in offline database inspection, after workload metrics are taken.
        if (Environment.GetEnvironmentVariable("SOCKSEEK_KEEP_FIXTURE") == "1")
            Console.WriteLine("E2E_RETAINED_FIXTURE " + temporary);
        else
            Directory.Delete(temporary, recursive: true);
    }
}

static (long Written, long Cancelled)? ReadStorageCounters()
{
    if (!OperatingSystem.IsLinux()) return null;
    try
    {
        long? written = null, cancelled = null;
        foreach (string line in File.ReadLines("/proc/self/io"))
        {
            int separator = line.IndexOf(':');
            if (separator < 0 || !long.TryParse(line.AsSpan(separator + 1), out long value)) continue;
            if (line.AsSpan(0, separator).SequenceEqual("write_bytes")) written = value;
            if (line.AsSpan(0, separator).SequenceEqual("cancelled_write_bytes")) cancelled = value;
        }
        return written.HasValue && cancelled.HasValue ? (written.Value, cancelled.Value) : null;
    }
    catch (IOException) { return null; }
    catch (UnauthorizedAccessException) { return null; }
}

static long DirectoryBytes(string path)
    => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
        : 0;

sealed record ScenarioOptions(bool Uploads, bool Metrics, bool ApplicationLogs);
