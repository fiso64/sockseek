using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;
using Sockseek.Server;
using System.Collections.Concurrent;
using Sockseek.Api;
using Tests.ClientTests;

namespace Tests.Cli;

[TestClass]
public class CliBackendParityTests
{
    private const string DynamicLoopbackUrl = "http://127.0.0.1:0";

    [TestMethod]
    public async Task CliBackendParity_LiveTerminalJobUpdates_AreEquivalent()
    {
        var projections = new ConcurrentBag<string[]>();
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string trackDir = Path.Combine(musicRoot, "Artist");
                Directory.CreateDirectory(trackDir);
                File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");
            },
            scenario: async ctx =>
            {
                var terminalJobs = new ConcurrentDictionary<Guid, JobSummaryDto>();
                void CaptureTerminal(DaemonClientUpdate update)
                {
                    foreach (JobSummaryDto job in update.ChangedJobs.Where(job =>
                        job.LifecycleState == ServerJobLifecycleState.Terminal))
                        terminalJobs[job.JobId] = job;
                }
                ctx.Backend.StateUpdated += CaptureTerminal;
                var summary = await ctx.Backend.SubmitTrackSearchJobAsync(
                    new SubmitTrackSearchJobRequestDto(
                        new SongQueryDto("Artist", "Track One", "", "", -1, false),
                        AggregatePresentation: true),
                    ctx.Token);
                Assert.AreEqual(ServerSearchDefaultProjectionKind.Track, summary.SearchDefaultProjection, ctx.Name);
                Assert.AreEqual(true, summary.SearchAggregatePresentation, ctx.Name);
                try
                {
                    JobSummaryDto? observedTerminal = await BackendTestWaiter.UntilAsync(
                        ctx.Backend,
                        _ => Task.FromResult(terminalJobs.GetValueOrDefault(summary.JobId)),
                        job => job != null,
                        $"{ctx.Name} did not publish a live terminal update for job {summary.JobId}.",
                        job => job == null ? "<missing>" : FormatState(job));
                    Assert.IsNotNull(observedTerminal);
                    JobSummaryDto terminal = observedTerminal;
                    Assert.AreEqual(summary.SearchDefaultProjection, terminal.SearchDefaultProjection, ctx.Name);
                    Assert.AreEqual(summary.SearchAggregatePresentation, terminal.SearchAggregatePresentation, ctx.Name);
                    projections.Add(
                    [
                        $"{terminal.Kind}:{terminal.LifecycleState}:{terminal.ActivityPhase}:" +
                        $"{terminal.TerminalOutcome}:{terminal.ParentJobId.HasValue}:" +
                        $"{terminal.SearchDefaultProjection}:{terminal.SearchAggregatePresentation}",
                    ]);
                }
                finally
                {
                    ctx.Backend.StateUpdated -= CaptureTerminal;
                }
            });

        Assert.AreEqual(2, projections.Count);
        var projectionArray = projections.ToArray();
        CollectionAssert.AreEqual(projectionArray[0], projectionArray[1]);
    }

    [TestMethod]
    public async Task RemoteBackend_RetiredSearchHistoryMatchesItsLiveTerminalUpdate()
    {
        await RunAsync(
            () => ParityBackendContext.CreateRemoteAsync(musicRoot =>
            {
                string trackDir = Path.Combine(musicRoot, "Artist");
                Directory.CreateDirectory(trackDir);
                File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");
            }),
            async ctx =>
            {
                var terminalJobs = new ConcurrentDictionary<Guid, JobSummaryDto>();
                void CaptureTerminal(DaemonClientUpdate update)
                {
                    foreach (JobSummaryDto job in update.ChangedJobs.Where(job =>
                        job.LifecycleState == ServerJobLifecycleState.Terminal))
                        terminalJobs[job.JobId] = job;
                }
                ctx.Backend.StateUpdated += CaptureTerminal;
                try
                {
                    var summary = await ctx.Backend.SubmitTrackSearchJobAsync(
                        new SubmitTrackSearchJobRequestDto(
                            new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                        ctx.Token);
                    JobSummaryDto? observedTerminal = await BackendTestWaiter.UntilAsync(
                        ctx.Backend,
                        _ => Task.FromResult(terminalJobs.GetValueOrDefault(summary.JobId)),
                        job => job != null,
                        $"Remote live state did not publish terminal job {summary.JobId}.",
                        job => job == null ? "<missing>" : FormatState(job));
                    Assert.IsNotNull(observedTerminal);
                    JobSummaryDto liveTerminal = observedTerminal;
                    await BackendTestWaiter.UntilAsync(
                        ctx.Backend,
                        _ => Task.FromResult(
                            ctx.Backend.ClientStore.GetWorkflowJobs(summary.WorkflowId).Count == 0),
                        "Remote workflow did not retire from live state.");

                    var retainedJobs = await ctx.Backend.GetJobsAsync(
                        new JobQuery(null, null, null, summary.WorkflowId, IncludeAll: true),
                        ctx.Token);
                    JobSummaryDto retained = retainedJobs.Single(job => job.JobId == summary.JobId);
                    Assert.AreEqual(liveTerminal.LifecycleState, retained.LifecycleState);
                    Assert.AreEqual(liveTerminal.TerminalOutcome, retained.TerminalOutcome);
                    Assert.AreEqual(liveTerminal.ActivityPhase, retained.ActivityPhase);
                    Assert.AreEqual(ServerSearchDefaultProjectionKind.Track, retained.SearchDefaultProjection);
                    Assert.AreEqual(false, retained.SearchAggregatePresentation);

                    var projection = await ctx.Backend.GetFileResultsAsync(summary.JobId, ctx.Token);
                    Assert.IsNotNull(projection);
                    Assert.IsTrue(projection.IsComplete);
                    Assert.AreEqual("Complete", projection.PersistenceState);
                    Assert.AreEqual(1, projection.Items.Count);
                }
                finally
                {
                    ctx.Backend.StateUpdated -= CaptureTerminal;
                }
            });
    }

    [TestMethod]
    public async Task CliBackendParity_LockedRowsRemainVisibleInEverySearchProjectionButAreNotDownloaded()
    {
        const string publicPath = @"Music\Artist\Album\01 - Track One.flac";
        const string lockedPath = @"Music\Artist\Album\02 - Track Two.flac";
        const string lockedPeerPath = @"Other\Artist\Album\01 - Track One.flac";
        var projections = new ConcurrentBag<string[]>();
        var printed = new ConcurrentBag<string[]>();
        var downloaded = new ConcurrentBag<string[]>();
        static string Files(IEnumerable<FileCandidateDto> files) => string.Join(";", files
            .Select(file => $"{file.Username}|{file.Filename}|{file.Visibility}|{file.PreferenceTier}|{file.NecessaryConditionsSatisfied}")
            .Order(StringComparer.Ordinal));
        static string Folders(IEnumerable<AlbumFolderDto> folders) => string.Join(";", folders
            .Select(folder => $"{folder.Username}|{folder.FolderPath}|{folder.FileCount}|{folder.AudioFileCount}|{Files(folder.Files!)}")
            .Order(StringComparer.Ordinal));
        await RunForEachInjectedClientBackendAsync(() =>
        {
            var publicResponse = SearchResponse("ExactPeer", publicPath);
            var responses = new[]
            {
                new Soulseek.SearchResponse("ExactPeer", 1, true, 100, 0, publicResponse.Files,
                    SearchResponse("ExactPeer", lockedPath).Files),
                new Soulseek.SearchResponse("LockedPeer", 1, true, 100, 0, [],
                    SearchResponse("LockedPeer", lockedPeerPath).Files),
            };
            var client = new MockSoulseekClient([publicResponse])
            {
                SearchHandler = (query, arrived, _, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var response in responses) arrived(response);
                    return Task.FromResult(new Soulseek.Search(query: query, token: 1, state: Soulseek.SearchStates.Completed,
                        responseCount: 2, fileCount: 1, lockedFileCount: 2,
                        scope: new Soulseek.SearchScope(Soulseek.SearchScopeType.Network)));
                },
            };
            return (client, new DownloadGate());
        }, async ctx =>
        {
            var source = await ctx.Backend.SubmitSearchJobAsync(new SubmitSearchJobRequestDto("Artist Album"), ctx.Token);
            await WaitForJobStateAsync(ctx.Backend, source.JobId, ExpectedJobStatus.Succeeded);
            void AssertFiles(IEnumerable<FileCandidateDto> values)
            {
                var files = values.ToArray();
                CollectionAssert.AreEquivalent(new[] { publicPath, lockedPath, lockedPeerPath },
                    files.Select(file => file.Filename).ToArray(), ctx.Name);
                Assert.AreEqual(1, files.Count(file => file.Visibility == Sockseek.Core.Models.SearchResultVisibility.Public), ctx.Name);
                Assert.AreEqual(2, files.Count(file => file.Visibility == Sockseek.Core.Models.SearchResultVisibility.Locked), ctx.Name);
                Assert.IsTrue(files.All(file => file.PreferenceTier != null), ctx.Name);
            }
            var query = new SongQueryDto(Artist: "Artist");
            var albumQuery = new AlbumQueryDto(Artist: "Artist", Album: "Album");
            var files = await ctx.Backend.GetFileResultsAsync(source.JobId,
                new FileSearchProjectionRequestDto(query, IncludeFullResults: true), ctx.Token);
            Assert.IsNotNull(files, ctx.Name);
            AssertFiles(files.Items);
            var directories = await ctx.Backend.GetFolderResultsAsync(source.JobId,
                new FolderSearchProjectionRequestDto(albumQuery, IncludeFiles: true), ctx.Token);
            Assert.IsNotNull(directories, ctx.Name);
            AssertFiles(directories.Items.SelectMany(folder => folder.Files!));
            var tracks = await ctx.Backend.GetAggregateTrackResultsAsync(source.JobId,
                new AggregateTrackProjectionRequestDto(query, IncludeCandidates: true), ctx.Token);
            Assert.IsNotNull(tracks, ctx.Name);
            AssertFiles(tracks.Items.SelectMany(group => group.Candidates!));
            var albums = await ctx.Backend.GetAggregateAlbumResultsAsync(source.JobId,
                new AggregateAlbumProjectionRequestDto(albumQuery, IncludeFolders: true), ctx.Token);
            Assert.IsNotNull(albums, ctx.Name);
            AssertFiles(albums.Items.SelectMany(group => group.Folders!).SelectMany(folder => folder.Files!));
            projections.Add(
            [
                Files(files.Items),
                Folders(directories.Items),
                string.Join(";", tracks.Items.Select(group =>
                    $"{group.Query.Artist}|{group.Query.Title}|{group.Query.Album}|{group.Query.Length}|{Files(group.Candidates!)}")
                    .Order(StringComparer.Ordinal)),
                string.Join(";", albums.Items.Select(group =>
                    $"{group.Query.Artist}|{group.Query.Album}|{Folders(group.Folders!)}").Order(StringComparer.Ordinal)),
            ]);

            var printOptions = new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                PrintOption: ServerPrintOption.Results | ServerPrintOption.Full));
            var printSettings = CreateDownloadSettings(ctx.OutputDir);
            printSettings.PrintOption = PrintOption.Results | PrintOption.Full;
            Task<JobSummaryDto> Submit(string mode, SubmissionOptionsDto options) => mode switch
            {
                "song" => ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(query, options), ctx.Token),
                "album" => ctx.Backend.SubmitAlbumJobAsync(new SubmitAlbumJobRequestDto(albumQuery, options), ctx.Token),
                "aggregate" => ctx.Backend.SubmitAggregateJobAsync(new SubmitAggregateJobRequestDto(query, options), ctx.Token),
                _ => ctx.Backend.SubmitAlbumAggregateJobAsync(new SubmitAlbumAggregateJobRequestDto(albumQuery, options), ctx.Token),
            };
            string[] modes = ["song", "album", "aggregate", "album-aggregate"];
            var outputs = new List<string>();
            foreach (string mode in modes)
            {
                var requested = await Submit(mode, printOptions);
                await WaitForWorkflowStateAsync(ctx.Backend, requested.WorkflowId, ServerWorkflowState.Completed);
                string output = await ctx.PrintRequestedOutputAsync(requested, printSettings);
                Assert.AreEqual(3, output.Split("(nec:", StringSplitOptions.None).Length - 1, ctx.Name + ":" + mode + "\n" + output);
                Assert.AreEqual(2, output.Split("[locked]", StringSplitOptions.None).Length - 1, ctx.Name + ":" + mode + "\n" + output);
                Assert.IsFalse(output.Contains("conditions:unavailable", StringComparison.Ordinal), output);
                outputs.Add(output);
            }
            printed.Add(outputs.ToArray());
            Assert.AreEqual(0, ctx.DownloadedRelativePaths().Length, "Print-only discovery must not download public or locked files.");

            foreach (string mode in modes)
            {
                var output = Path.Combine(ctx.OutputDir, "auto-" + mode);
                var download = await Submit(mode, new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                    Output: new OutputSettingsPatchDto(ParentDir: output))));
                await WaitForJobStateAsync(ctx.Backend, download.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, download.WorkflowId, ServerWorkflowState.Completed);
            }
            var paths = ctx.DownloadedRelativePaths();
            Assert.AreEqual(4, paths.Length, ctx.Name);
            foreach (string mode in modes)
                Assert.AreEqual(1, paths.Count(path => path.StartsWith("auto-" + mode + "/", StringComparison.Ordinal)
                    && path.EndsWith("/01 - Track One.flac", StringComparison.Ordinal)), ctx.Name + ":" + mode);
            downloaded.Add(paths);
        });
        Assert.AreEqual(2, projections.Count);
        var both = projections.ToArray();
        CollectionAssert.AreEqual(both[0], both[1], "Local and remote grouping, counts and condition facts must match.");
        var bothOutputs = printed.ToArray();
        Assert.AreEqual(2, bothOutputs.Length);
        CollectionAssert.AreEqual(bothOutputs[0], bothOutputs[1], "Full result presentation must match both CLI modes.");
        var bothDownloads = downloaded.ToArray();
        Assert.AreEqual(2, bothDownloads.Length);
        CollectionAssert.AreEqual(bothDownloads[0], bothDownloads[1], "Download placement must match both CLI modes.");
    }

    [TestMethod]
    public async Task CliBackendParity_AllSearchProjectionKindsUseTheSameSemanticResults()
    {
        var projections = new ConcurrentBag<string[]>();
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumDir = Path.Combine(musicRoot, "Artist", "Album");
                Directory.CreateDirectory(albumDir);
                File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
                File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "bb");
            },
            scenario: async ctx =>
            {
                var options = new SubmissionOptionsDto(
                    DownloadSettings: new DownloadSettingsPatchDto(
                        Search: new SearchSettingsPatchDto(
                            Relax: true,
                            MinSharesAggregate: 1)));
                JobSummaryDto search = await ctx.Backend.SubmitSearchJobAsync(
                    new SubmitSearchJobRequestDto("Artist Album", options),
                    ctx.Token);
                await WaitForJobStateAsync(
                    ctx.Backend,
                    search.JobId,
                    ExpectedJobStatus.Succeeded);

                SearchResultSnapshotDto<FileCandidateDto> files = (await ctx.Backend
                    .GetFileResultsAsync(
                        search.JobId,
                        new FileSearchProjectionRequestDto(
                            new SongQueryDto(Artist: "Artist"),
                            IncludeFullResults: true),
                        ctx.Token))!;
                SearchResultSnapshotDto<AlbumFolderDto> directories = (await ctx.Backend
                    .GetFolderResultsAsync(
                        search.JobId,
                        new FolderSearchProjectionRequestDto(
                            new AlbumQueryDto(Artist: "Artist", Album: "Album"),
                            IncludeFiles: true),
                        ctx.Token))!;
                SearchResultSnapshotDto<AggregateTrackCandidateDto> tracks = (await ctx.Backend
                    .GetAggregateTrackResultsAsync(
                        search.JobId,
                        new AggregateTrackProjectionRequestDto(
                            new SongQueryDto(Artist: "Artist"),
                            IncludeCandidates: true),
                        ctx.Token))!;
                SearchResultSnapshotDto<AggregateAlbumCandidateDto> albums = (await ctx.Backend
                    .GetAggregateAlbumResultsAsync(
                        search.JobId,
                        new AggregateAlbumProjectionRequestDto(
                            new AlbumQueryDto(Artist: "Artist", Album: "Album"),
                            IncludeFolders: true),
                        ctx.Token))!;

                string[] expectedPaths = [@"Artist\Album\01. Artist - Track One.mp3", @"Artist\Album\02. Artist - Track Two.mp3"];
                Assert.IsTrue(files.IsComplete, ctx.Name);
                CollectionAssert.AreEquivalent(expectedPaths, files.Items.Select(file => file.Filename).ToArray(), ctx.Name);
                CollectionAssert.AreEquivalent(new long[] { 1, 2 }, files.Items.Select(file => file.File.Size).ToArray(), ctx.Name);
                Assert.IsTrue(files.Items.All(file => file.Username == "local"), ctx.Name);
                Assert.IsTrue(directories.IsComplete, ctx.Name);
                Assert.AreEqual(1, directories.Items.Count, ctx.Name);
                Assert.AreEqual(@"Artist\Album", directories.Items[0].FolderPath, ctx.Name);
                Assert.AreEqual(2, directories.Items[0].FileCount, ctx.Name);
                Assert.AreEqual(2, directories.Items[0].AudioFileCount, ctx.Name);
                CollectionAssert.AreEquivalent(expectedPaths, directories.Items[0].Files!.Select(file => file.Filename).ToArray(), ctx.Name);
                Assert.IsTrue(tracks.IsComplete, ctx.Name);
                Assert.AreEqual(2, tracks.Items.Count, ctx.Name);
                CollectionAssert.AreEquivalent(new[] { "Artist:Track One", "Artist:Track Two" },
                    tracks.Items.Select(track => $"{track.Query.Artist}:{track.Query.Title}").ToArray(), ctx.Name);
                Assert.IsTrue(tracks.Items.All(track => track.Candidates?.Count == 1), ctx.Name);
                CollectionAssert.AreEquivalent(expectedPaths, tracks.Items.SelectMany(track => track.Candidates!).Select(file => file.Filename).ToArray(), ctx.Name);
                Assert.IsTrue(albums.IsComplete, ctx.Name);
                Assert.AreEqual(1, albums.Items.Count, ctx.Name);
                Assert.AreEqual("Artist", albums.Items[0].Query.Artist, ctx.Name);
                Assert.AreEqual("Album", albums.Items[0].Query.Album, ctx.Name);
                Assert.AreEqual(1, albums.Items[0].Folders!.Count, ctx.Name);
                CollectionAssert.AreEquivalent(expectedPaths, albums.Items[0].Folders![0].Files!.Select(file => file.Filename).ToArray(), ctx.Name);

                // A default projection may reject every raw hit. Full diagnostic
                // results must still expose those hits in either CLI backend.
                var flacOnly = new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                    Search: new SearchSettingsPatchDto(NecessaryCond: new FileConditionsPatchDto(
                        Formats: new CollectionPatchDto<string>(Replace: ["flac"])))));
                var filteredSearch = await ctx.Backend.SubmitSearchJobAsync(
                    new SubmitSearchJobRequestDto("Artist Album", flacOnly), ctx.Token);
                await WaitForJobStateAsync(ctx.Backend, filteredSearch.JobId, ExpectedJobStatus.Succeeded);
                var matching = await ctx.Backend.GetFileResultsAsync(filteredSearch.JobId, ctx.Token);
                Assert.IsNotNull(matching, ctx.Name);
                Assert.AreEqual(0, matching.Items.Count, ctx.Name);
                var full = await ctx.Backend.GetFileResultsAsync(filteredSearch.JobId,
                    new FileSearchProjectionRequestDto(IncludeFullResults: true), ctx.Token);
                Assert.IsNotNull(full, ctx.Name);
                CollectionAssert.AreEquivalent(expectedPaths, full.Items.Select(file => file.Filename).ToArray(), ctx.Name);
                Assert.IsTrue(full.Items.All(file => file.NecessaryConditionsSatisfied == false), ctx.Name);
                var matchingAfterFull = await ctx.Backend.GetFileResultsAsync(filteredSearch.JobId, ctx.Token);
                Assert.IsNotNull(matchingAfterFull, ctx.Name);
                Assert.AreEqual(0, matchingAfterFull.Items.Count, ctx.Name);

                // Full-results is a projection request, not inherited matching
                // policy. In particular false must also override a source that
                // was originally submitted for full diagnostic presentation.
                var fullSearch = await ctx.Backend.SubmitTrackSearchJobAsync(new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto(Artist: "Artist"), IncludeFullResults: true, Options: flacOnly), ctx.Token);
                await WaitForJobStateAsync(ctx.Backend, fullSearch.JobId, ExpectedJobStatus.Succeeded);
                var matchingAgain = await ctx.Backend.GetFileResultsAsync(fullSearch.JobId, ctx.Token);
                Assert.IsNotNull(matchingAgain, ctx.Name);
                Assert.AreEqual(0, matchingAgain.Items.Count, ctx.Name);
                var fullAgain = await ctx.Backend.GetFileResultsAsync(fullSearch.JobId,
                    new FileSearchProjectionRequestDto(IncludeFullResults: true), ctx.Token);
                Assert.IsNotNull(fullAgain, ctx.Name);
                CollectionAssert.AreEquivalent(expectedPaths, fullAgain.Items.Select(file => file.Filename).ToArray(), ctx.Name);

                projections.Add(
                [
                    "files:" + string.Join('|', files.Items.Select(item =>
                        $"{item.Username}:{item.Filename}:{item.PreferenceTier}")),
                    "directories:" + string.Join('|', directories.Items.Select(item =>
                        $"{item.Username}:{item.FolderPath}:{item.FileCount}:" +
                        string.Join(',', item.Files?.Select(file => file.Filename) ?? []))),
                    "tracks:" + string.Join('|', tracks.Items.Select(item =>
                        $"{item.Query.Artist}:{item.Query.Title}:" +
                        string.Join(',', item.Candidates?.Select(file => file.Filename) ?? []))),
                    "albums:" + string.Join('|', albums.Items.Select(item =>
                        $"{item.Query.Artist}:{item.Query.Album}:" +
                        string.Join(',', item.Folders?.Select(folder => folder.FolderPath) ?? []))),
                ]);
            });

        Assert.AreEqual(2, projections.Count);
        string[][] rows = projections.ToArray();
        CollectionAssert.AreEqual(rows[0], rows[1]);
        Assert.IsTrue(rows[0].All(row => !row.EndsWith(':')),
            "Every projection kind should produce a non-empty semantic result.");
    }

    [TestCleanup]
    public void Cleanup()
    {
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CliBackendParity_SongModes_DownloadExpectedFiles(bool aggregate)
    {
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumDir = Path.Combine(musicRoot, "Artist", "Album");
                Directory.CreateDirectory(albumDir);
                File.WriteAllText(Path.Combine(albumDir, "Artist - First.mp3"), "first");
                File.WriteAllText(Path.Combine(albumDir, "Artist - Second.mp3"), "second");
                if (aggregate)
                {
                    string alternative = Path.Combine(musicRoot, "Alternate", "Album");
                    Directory.CreateDirectory(alternative);
                    File.WriteAllText(Path.Combine(alternative, "Artist - First.mp3"), "first");
                    File.WriteAllText(Path.Combine(alternative, "Artist - Second.mp3"), "second");
                }
                else
                {
                    string volatileDir = Path.Combine(musicRoot, "PeerDrop");
                    Directory.CreateDirectory(volatileDir);
                    File.WriteAllText(Path.Combine(volatileDir, "PeerDrop - Missing.mp3"), "missing");
                    File.WriteAllText(Path.Combine(volatileDir, "PeerDrop - Survivor.mp3"), "survivor");
                }
            },
            scenario: async ctx =>
            {
                if (!aggregate)
                {
                    var automatic = await ctx.Backend.SubmitSongJobAsync(
                        new SubmitSongJobRequestDto(new SongQueryDto("Artist", "First", "", "", -1, false)), ctx.Token);
                    await WaitForJobStateAsync(ctx.Backend, automatic.JobId, ExpectedJobStatus.Succeeded);
                    await WaitForWorkflowStateAsync(ctx.Backend, automatic.WorkflowId, ServerWorkflowState.Completed);
                    CollectionAssert.AreEqual(new[] { "Album/Artist - First.mp3" }, ctx.DownloadedRelativePaths(), ctx.Name);
                }
                var query = new SongQueryDto("Artist", "", "", "", -1, false);
                var summary = aggregate
                    ? await ctx.Backend.SubmitAggregateJobAsync(new SubmitAggregateJobRequestDto(query), ctx.Token)
                    : await ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(query,
                        DownloadBehavior: new DownloadBehaviorPolicyDto(Song: ServerDownloadBehavior.Manual)), ctx.Token);

                string? selectedFilename = null;
                if (!aggregate)
                {
                    await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.AwaitingSelection);
                    var candidates = await ctx.Backend.GetFileResultsAsync(summary.JobId, ctx.Token);
                    Assert.IsNotNull(candidates, ctx.Name);
                    Assert.AreEqual(2, candidates.Items.Count, ctx.Name);
                    // A manual choice must beat the previously ranked first candidate.
                    var chosen = candidates.Items[^1];
                    selectedFilename = chosen.Filename.Split('\\', '/')[^1];
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var selected = await ctx.Backend.StartFileDownloadsAsync(summary.JobId,
                        new StartFileDownloadsRequestDto([chosen.Ref],
                            new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                                Output: new OutputSettingsPatchDto(
                                    ParentDir: Path.Combine(ctx.OutputDir, "song-output"),
                                    NameFormat: "picked/{filename}")))), ctx.Token);
                    Console.WriteLine($"CLI song selection {ctx.Name}: acceptance-ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
                    Assert.IsNotNull(selected, ctx.Name);
                    Assert.AreEqual(summary.JobId, selected.Single().JobId, ctx.Name);
                }

                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(aggregate
                    ? new[] { "Album/Artist - First.mp3", "Album/Artist - Second.mp3" }
                    : new[] { "Album/Artist - First.mp3", $"song-output/picked/{selectedFilename}" }, ctx.DownloadedRelativePaths(),
                    $"{ctx.Name}: actual outputs {string.Join(", ", ctx.DownloadedRelativePaths())}");
                if (!aggregate)
                    Assert.AreEqual(selectedFilename!.EndsWith("Second.mp3", StringComparison.Ordinal) ? "second" : "first",
                        await File.ReadAllTextAsync(Path.Combine(ctx.OutputDir, "song-output", "picked", selectedFilename)), ctx.Name);
                if (aggregate) await AssertManualAggregateSongSelectionAsync(ctx);
                else await AssertManualSongFailureAndRecoveryAsync(ctx);
            }, profiles: SongLayoutProfileCatalog());
    }

    [TestMethod]
    public async Task CliBackendParity_AlbumModes_PreserveOutputOptions()
    {
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumDir = Path.Combine(musicRoot, "Artist", "Album");
                Directory.CreateDirectory(albumDir);
                File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
                File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");
            },
            scenario: async ctx =>
            {
                var summary = await ctx.Backend.SubmitAlbumJobAsync(
                    new SubmitAlbumJobRequestDto(
                        new AlbumQueryDto("Artist", "Album", "", "", false),
                        DownloadBehavior: new DownloadBehaviorPolicyDto(Album: ServerDownloadBehavior.Manual)),
                    ctx.Token);

                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.AwaitingSelection);

                var folders = await ctx.Backend.GetFolderResultsAsync(summary.JobId, includeFiles: true, ctx.Token);
                Assert.IsNotNull(folders, ctx.Name);
                Assert.AreEqual(1, folders.Items.Count, ctx.Name);
                Assert.AreEqual(2, folders.Items[0].Files?.Count, ctx.Name);

                var download = await ctx.Backend.StartFolderDownloadAsync(
                    summary.JobId,
                    new StartFolderDownloadRequestDto(folders.Items[0].Ref,
                        Options: new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(
                                ParentDir: Path.Combine(ctx.OutputDir, "selected-output"),
                                NameFormat: "{foldername}/chosen/{filename}"),
                            Search: new SearchSettingsPatchDto(NoBrowseFolder: false)))),
                    ctx.Token);
                Assert.IsNotNull(download, ctx.Name);
                Assert.AreEqual(summary.JobId, download.JobId, ctx.Name);

                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "selected-output/Album/chosen/01. Artist - Track One.mp3",
                        "selected-output/Album/chosen/02. Artist - Track Two.mp3",
                    },
                    ctx.DownloadedRelativePaths(),
                    ctx.Name);
                await AssertAutomaticAlbumDownloadAsync(ctx, aggregate: false, expectedTransferredBytes: 0, new Dictionary<string, string>
                {
                    ["automatic-output/Album/from-profile/01. Artist - Track One.mp3"] = "a",
                    ["automatic-output/Album/from-profile/02. Artist - Track Two.mp3"] = "b",
                });
            }, profiles: AutomaticAlbumProfileCatalog());
    }

    [TestMethod]
    public async Task CliBackendParity_AlbumAggregateModes_PreserveChildAndOutputOptions()
    {
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumOneDir = Path.Combine(musicRoot, "Artist", "Album One");
                string albumTwoDir = Path.Combine(musicRoot, "Artist", "Album Two");
                Directory.CreateDirectory(albumOneDir);
                Directory.CreateDirectory(albumTwoDir);
                File.WriteAllText(Path.Combine(albumOneDir, "01. Artist - First.mp3"), "a");
                File.WriteAllText(Path.Combine(albumTwoDir, "01. Artist - Second.mp3"), "bb");
                File.WriteAllText(Path.Combine(albumTwoDir, "02. Artist - Third.mp3"), "ccc");
            },
            scenario: async ctx =>
            {
                var summary = await ctx.Backend.SubmitAlbumAggregateJobAsync(
                    new SubmitAlbumAggregateJobRequestDto(
                        new AlbumQueryDto("Artist", "", "", "", false),
                        DownloadBehavior: new DownloadBehaviorPolicyDto(AlbumAggregate: ServerDownloadBehavior.Manual)),
                    ctx.Token);

                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.AwaitingSelection);

                var aggregate = await ctx.Backend.GetAggregateAlbumResultsAsync(
                    summary.JobId,
                    new AggregateAlbumProjectionRequestDto(IncludeFolders: true),
                    ctx.Token);
                Assert.IsNotNull(aggregate, ctx.Name);
                Assert.AreEqual(2, aggregate.Items.Count, ctx.Name);

                int exactPlans = 0;
                foreach (var bucket in aggregate.Items)
                {
                    Assert.IsNotNull(bucket.Folders, ctx.Name);
                    Assert.IsTrue(bucket.Folders.Count > 0, ctx.Name);
                    bool exactSelection = bucket.Query.Album == "Album Two";
                    if (exactSelection) exactPlans++;

                    long selectionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    var download = await ctx.Backend.StartFolderDownloadAsync(
                        summary.JobId,
                        new StartFolderDownloadRequestDto(
                            bucket.Folders[0].Ref,
                            Options: exactSelection
                                ? new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                                    Output: new OutputSettingsPatchDto(
                                        ParentDir: Path.Combine(ctx.OutputDir, "exact-output"),
                                        NameFormat: "picked/{filename}"),
                                    Search: new SearchSettingsPatchDto(NoBrowseFolder: false)))
                                : null,
                            AlbumQuery: bucket.Query,
                            Selection: exactSelection
                                ? new AlbumFolderDownloadSelectionDto(
                                    Files: [bucket.Folders[0].Files!.Single(file =>
                                        file.Filename.EndsWith("Third.mp3", StringComparison.Ordinal)).Ref],
                                    ExactFiles: true, SkipTrackCountVerification: true)
                                : null),
                        ctx.Token);
                    Console.WriteLine($"CLI album selection {ctx.Name}: exact={exactSelection}, "
                        + $"acceptance-ms={System.Diagnostics.Stopwatch.GetElapsedTime(selectionStarted).TotalMilliseconds:F1}.");
                    Assert.IsNotNull(download, ctx.Name);
                    Assert.AreEqual(summary.WorkflowId, download.WorkflowId, ctx.Name);
                    Assert.AreEqual(summary.JobId, download.SourceJobId, ctx.Name);

                    await WaitForJobStateAsync(ctx.Backend, download.JobId, ExpectedJobStatus.Succeeded);
                    if (exactSelection)
                    {
                        var detail = await ctx.Backend.GetJobDetailAsync(download.JobId, ctx.Token);
                        Assert.IsNotNull(detail, ctx.Name);
                        Assert.IsInstanceOfType<AlbumJobPayloadDto>(detail.Payload);
                        var state = ((AlbumJobPayloadDto)detail.Payload!).Directory;
                        Assert.AreEqual(1, state.FileCount, ctx.Name);
                        Assert.AreEqual(1, state.SuccessfulFileCount, ctx.Name);
                        Assert.AreEqual(3L, state.TotalKnownBytes, ctx.Name);
                        Assert.AreEqual(3L, state.BytesTransferred, ctx.Name);
                        Assert.IsTrue(File.Exists(Path.Combine(ctx.OutputDir,
                            "exact-output", "picked", "02. Artist - Third.mp3")),
                            $"{ctx.Name}: expected the explicit exact-child output, found {string.Join(", ", ctx.DownloadedRelativePaths())}; directory={state.DownloadPath}.");
                        Assert.AreEqual("ccc", await File.ReadAllTextAsync(Path.Combine(ctx.OutputDir,
                            "exact-output", "picked", "02. Artist - Third.mp3")), ctx.Name);

                        // A further choice without new options must reuse the
                        // child's prepared settings, not reset Config to the
                        // aggregate while retaining an incompatible context.
                        var repeated = await ctx.Backend.StartFolderDownloadAsync(summary.JobId,
                            new StartFolderDownloadRequestDto(bucket.Folders[0].Ref,
                                AlbumQuery: bucket.Query,
                                Selection: new AlbumFolderDownloadSelectionDto(
                                    Files: [bucket.Folders[0].Files!.Single(file =>
                                        file.Filename.EndsWith("Third.mp3", StringComparison.Ordinal)).Ref],
                                    ExactFiles: true, SkipTrackCountVerification: true)), ctx.Token);
                        Assert.IsNotNull(repeated, ctx.Name);
                        Assert.AreEqual(download.JobId, repeated.JobId, ctx.Name);
                        var repeatedDetail = await BackendTestWaiter.UntilAsync(ctx.Backend,
                            ct => ctx.Backend.GetJobDetailAsync(download.JobId, ct),
                            current => current?.Summary.LifecycleState == ServerJobLifecycleState.Terminal
                                && (current.Summary.SkipReason == ServerJobSkipReason.AlreadyExists
                                    || current.Payload is AlbumJobPayloadDto album
                                        && album.Directory.AttemptNumber > state.AttemptNumber),
                            $"{ctx.Name}: repeated selection did not produce a fresh terminal outcome.");
                        var repeatedChildren = await ctx.Backend.GetJobsAsync(
                            new JobQuery(null, null, null, null, ParentJobId: download.JobId, IncludeAll: true), ctx.Token);
                        Assert.IsTrue(repeatedChildren.Any(child =>
                            child.TerminalOutcome == ServerJobTerminalOutcome.Skipped
                            && child.SkipReason == ServerJobSkipReason.AlreadyExists),
                            $"{ctx.Name}: repeat must exercise existing-file completion.");
                        Assert.IsTrue(repeatedDetail!.Summary.TerminalOutcome == ServerJobTerminalOutcome.Succeeded
                            || repeatedDetail.Summary.TerminalOutcome == ServerJobTerminalOutcome.Skipped
                                && repeatedDetail.Summary.SkipReason == ServerJobSkipReason.AlreadyExists,
                            $"{ctx.Name}: repeat {FormatState(repeatedDetail.Summary)} "
                            + $"{repeatedDetail.Summary.FailureReason}: {repeatedDetail.Summary.FailureMessage}; "
                            + $"{repeatedDetail.Summary.FailureDetail}; outputs={string.Join(", ", ctx.DownloadedRelativePaths())}; "
                            + $"children={string.Join("; ", repeatedChildren.Select(child => $"{child.TerminalOutcome}:{child.SkipReason}:{child.FailureReason}:{child.FailureMessage}:{child.FailureDetail}"))}.");
                    }
                }
                Assert.AreEqual(1, exactPlans, ctx.Name);

                Assert.IsTrue(await ctx.Backend.CompleteManualSelectionAsync(summary.JobId, ctx.Token), ctx.Name);
                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "Album One/01. Artist - First.mp3",
                        "exact-output/picked/02. Artist - Third.mp3",
                    },
                    ctx.DownloadedRelativePaths(),
                    ctx.Name);
                await AssertAutomaticAlbumDownloadAsync(ctx, aggregate: true, expectedTransferredBytes: 2, new Dictionary<string, string>
                {
                    ["automatic-output/Album One/from-profile/01. Artist - First.mp3"] = "a",
                    ["automatic-output/Album Two/from-profile/01. Artist - Second.mp3"] = "bb",
                    ["automatic-output/Album Two/from-profile/02. Artist - Third.mp3"] = "ccc",
                });
            }, profiles: AutomaticAlbumProfileCatalog());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task CliBackendParity_SearchFolderFollowUp_DownloadsSameFiles(bool exactSelectionWithOutputOverride, bool generalMode)
    {
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumDir = Path.Combine(musicRoot, "Artist", "Album");
                Directory.CreateDirectory(albumDir);
                File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
                File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");
                File.WriteAllText(Path.Combine(albumDir, "notes.txt"), "notes");
            },
            scenario: async ctx =>
            {
                var albumQuery = new AlbumQueryDto("Artist", "Album", "", "", false);
                var search = await ctx.Backend.SubmitAlbumSearchJobAsync(
                    new SubmitAlbumSearchJobRequestDto(albumQuery),
                    ctx.Token);

                await WaitForJobStateAsync(ctx.Backend, search.JobId, ExpectedJobStatus.Succeeded);

                var folders = await ctx.Backend.GetFolderResultsAsync(
                    search.JobId,
                    new FolderSearchProjectionRequestDto(albumQuery, IncludeFiles: true),
                    ctx.Token);
                Assert.IsNotNull(folders, ctx.Name);
                Assert.AreEqual(1, folders.Items.Count, ctx.Name);
                Assert.AreEqual(3, folders.Items[0].Files?.Count, ctx.Name);

                var download = await ctx.Backend.StartFolderDownloadAsync(
                    search.JobId,
                    new StartFolderDownloadRequestDto(folders.Items[0].Ref,
                        Options: exactSelectionWithOutputOverride
                            ? new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                                Output: new OutputSettingsPatchDto(
                                    ParentDir: Path.Combine(ctx.OutputDir, "exact-output"),
                                    NameFormat: "picked/{filename}"),
                                Search: generalMode ? null : new SearchSettingsPatchDto(NoBrowseFolder: false)))
                            : null,
                        AlbumQuery: albumQuery,
                        Selection: exactSelectionWithOutputOverride
                            ? new AlbumFolderDownloadSelectionDto(
                                Files: [folders.Items[0].Files!.Single(file => file.Filename.EndsWith("Track Two.mp3", StringComparison.Ordinal)).Ref],
                                ExactFiles: true, SkipTrackCountVerification: true)
                            : null,
                        RequestedMode: generalMode ? ServerExtractionMode.General : null),
                    ctx.Token);
                Assert.IsNotNull(download, ctx.Name);
                Assert.AreEqual(search.JobId, download.SourceJobId, ctx.Name);
                Assert.AreEqual(generalMode ? ServerJobKind.RemoteDirectory : ServerJobKind.Album, download.Kind, ctx.Name);

                await WaitForJobStateAsync(ctx.Backend, download.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, search.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(
                    exactSelectionWithOutputOverride
                    ? new[] { "exact-output/picked/02. Artist - Track Two.mp3" }
                    : new[] { "Album/01. Artist - Track One.mp3", "Album/02. Artist - Track Two.mp3", "Album/notes.txt" },
                    ctx.DownloadedRelativePaths(),
                    ctx.Name);
                if (exactSelectionWithOutputOverride)
                {
                    Assert.AreEqual("b", await File.ReadAllTextAsync(Path.Combine(ctx.OutputDir,
                        "exact-output", "picked", "02. Artist - Track Two.mp3")), ctx.Name);
                }
                var detail = await ctx.Backend.GetJobDetailAsync(download.JobId, ctx.Token);
                Assert.IsNotNull(detail, ctx.Name);
                var directory = generalMode
                    ? Assert.IsInstanceOfType<RemoteDirectoryJobPayloadDto>(detail.Payload).Directory
                    : Assert.IsInstanceOfType<AlbumJobPayloadDto>(detail.Payload).Directory;
                int expectedFiles = exactSelectionWithOutputOverride ? 1 : 3;
                Assert.AreEqual(expectedFiles, directory.FileCount, ctx.Name);
                Assert.AreEqual(expectedFiles, directory.SuccessfulFileCount, ctx.Name);
                Assert.AreEqual(exactSelectionWithOutputOverride ? 1L : 7L, directory.TotalKnownBytes, ctx.Name);

                if (exactSelectionWithOutputOverride && generalMode)
                {
                    // Reuse completed search results for a separate nonmusic
                    // file selection; General must preserve the exact target.
                    var notes = folders.Items[0].Files!.Single(file => file.Filename.EndsWith("notes.txt", StringComparison.Ordinal));
                    var selected = await ctx.Backend.StartFileDownloadsAsync(search.JobId,
                        new StartFileDownloadsRequestDto([notes.Ref],
                            new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                                Output: new OutputSettingsPatchDto(
                                    ParentDir: Path.Combine(ctx.OutputDir, "file-output"),
                                    NameFormat: "loose/{filename}"))),
                            RequestedMode: ServerExtractionMode.General), ctx.Token);
                    Assert.IsNotNull(selected, ctx.Name);
                    var fileJob = selected.Single();
                    Assert.AreEqual(ServerJobKind.RemoteFile, fileJob.Kind, ctx.Name);
                    Assert.AreEqual(search.JobId, fileJob.SourceJobId, ctx.Name);
                    Assert.AreEqual(search.WorkflowId, fileJob.WorkflowId, ctx.Name);
                    await WaitForJobStateAsync(ctx.Backend, fileJob.JobId, ExpectedJobStatus.Succeeded);
                    await WaitForWorkflowStateAsync(ctx.Backend, search.WorkflowId, ServerWorkflowState.Completed);
                    CollectionAssert.AreEqual(new[] {
                        "exact-output/picked/02. Artist - Track Two.mp3", "file-output/loose/notes.txt",
                    }, ctx.DownloadedRelativePaths(), ctx.Name);
                    Assert.AreEqual("notes", await File.ReadAllTextAsync(Path.Combine(ctx.OutputDir,
                        "file-output", "loose", "notes.txt")), ctx.Name);
                    var fileDetail = await ctx.Backend.GetJobDetailAsync(fileJob.JobId, ctx.Token);
                    Assert.IsNotNull(fileDetail, ctx.Name);
                    var filePayload = Assert.IsInstanceOfType<RemoteFileJobPayloadDto>(fileDetail.Payload);
                    Assert.AreEqual(notes.Username, filePayload.Target.Username, ctx.Name);
                    Assert.AreEqual(notes.Filename, filePayload.Target.Filename, ctx.Name);
                    Assert.AreEqual(5L, filePayload.File.BytesTransferred, ctx.Name);
                }
            });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CliBackendParity_SongAttemptEvidenceSurvivesCancellationOrCandidateChange(bool cancel)
    {
        await RunForEachInjectedClientBackendAsync(
            createClient: () =>
            {
                var gate = new DownloadGate();
                var client = new MockSoulseekClient(
                [
                    SearchResponse("slowuser", @"Music\Artist\Album\01. Artist - Track.mp3"),
                    SearchResponse("fastuser", @"Music\Artist\Album\01. Artist - Track.mp3"),
                ])
                {
                    BeforeDownloadCompletesAsync = gate.BlockMatchingUserAsync,
                };
                return (client, gate);
            },
            scenario: async ctx =>
            {
                var summary = await ctx.Backend.SubmitSongJobAsync(
                    new SubmitSongJobRequestDto(new SongQueryDto("Artist", "Track", "", "", -1, false)),
                    ctx.Token);

                await ctx.Gate!.WaitForStartedAsync();
                var activeDetail = await BackendTestWaiter.UntilAsync(ctx.Backend,
                    ct => ctx.Backend.GetJobDetailAsync(summary.JobId, ct),
                    detail => detail?.Payload is SongJobPayloadDto { LastAttemptedFile: not null },
                    "Active Song must publish the actual attempted source.");
                var activePayload = Assert.IsInstanceOfType<SongJobPayloadDto>(activeDetail!.Payload);
                Assert.AreEqual(ctx.Gate.BlockedUsername, activePayload.LastAttemptedFile!.Target.Username, ctx.Name);
                Assert.IsNull(activePayload.ResolvedUsername, ctx.Name);
                Assert.IsNull(activePayload.ExactTarget, ctx.Name);
                if (cancel)
                    Assert.IsTrue(await ctx.Backend.CancelJobAsync(summary.JobId, ctx.Token), ctx.Name);
                else
                    Assert.IsTrue(
                        await ctx.Backend.TryNextCandidateByDisplayIdAsync(summary.DisplayId, summary.WorkflowId, ctx.Token),
                        ctx.Name);

                await BackendTestWaiter.UntilAsync(ctx.Backend,
                    ct => ctx.Backend.GetJobDetailAsync(summary.JobId, ct),
                    detail => detail?.Summary.LifecycleState == ServerJobLifecycleState.Terminal
                        && detail.Summary.TerminalOutcome == (cancel
                            ? ServerJobTerminalOutcome.Cancelled : ServerJobTerminalOutcome.Succeeded),
                    "Song must retain its actual cancellation/success outcome.");
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId,
                    cancel ? ServerWorkflowState.Failed : ServerWorkflowState.Completed);

                var detail = await ctx.Backend.GetJobDetailAsync(summary.JobId, ctx.Token);
                var payload = Assert.IsInstanceOfType<SongJobPayloadDto>(detail?.Payload);
                Assert.IsNotNull(payload.LastAttemptedFile, ctx.Name);
                if (cancel)
                {
                    Assert.IsNull(payload.ResolvedUsername, ctx.Name);
                    Assert.IsNull(payload.ExactTarget, ctx.Name);
                    Assert.AreEqual(activePayload.LastAttemptedFile.Target.Username, payload.LastAttemptedFile.Target.Username, ctx.Name);
                    Assert.AreEqual(activePayload.LastAttemptedFile.Target.Filename, payload.LastAttemptedFile.Target.Filename, ctx.Name);
                    Assert.AreEqual(activePayload.LastAttemptedFile.Target.Size, payload.LastAttemptedFile.Target.Size, ctx.Name);
                    Assert.AreEqual(activePayload.LastAttemptedFile.Peer.UploadSpeed, payload.LastAttemptedFile.Peer.UploadSpeed, ctx.Name);
                }
                else
                {
                    Assert.AreNotEqual(ctx.Gate.BlockedUsername, payload.ResolvedUsername, ctx.Name);
                    Assert.AreEqual(payload.ResolvedUsername, payload.LastAttemptedFile.Target.Username, ctx.Name);
                }
            });
    }

    [TestMethod]
    public async Task CliBackendParity_TryNextCandidateByParentJobId_SkipsActiveDescendantDownload()
    {
        await RunForEachInjectedClientBackendAsync(
            createClient: () =>
            {
                var gate = new DownloadGate();
                var client = new MockSoulseekClient(
                [
                    SearchResponse("slowuser", @"Music\Artist\Album\01. Artist - Track.mp3"),
                    SearchResponse("fastuser", @"Music\Artist\Album\01. Artist - Track.mp3"),
                ])
                {
                    BeforeDownloadCompletesAsync = gate.BlockMatchingUserAsync,
                };
                return (client, gate);
            },
            scenario: async ctx =>
            {
                var summary = await ctx.Backend.SubmitJobListAsync(
                    new SubmitJobListRequestDto(
                        "try-next-parent",
                        [
                            new SongJobDraftDto(new SongQueryDto("Artist", "Track", "", "", -1, false)),
                        ]),
                    ctx.Token);

                await ctx.Gate!.WaitForStartedAsync();
                Assert.IsTrue(
                    await ctx.Backend.TryNextCandidateAsync(summary.JobId, ctx.Token),
                    ctx.Name);

                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);
                var jobs = await ctx.Backend.GetJobsAsync(new JobQuery(null, null, null, summary.WorkflowId, IncludeAll: true), ctx.Token);
                var song = jobs.Single(job => job.Kind == ServerJobKind.Song);
                var detail = await ctx.Backend.GetJobDetailAsync(song.JobId, ctx.Token);
                var payload = Assert.IsInstanceOfType<SongJobPayloadDto>(detail?.Payload);
                Assert.AreNotEqual(ctx.Gate.BlockedUsername, payload.ResolvedUsername, ctx.Name);
            });
    }

    [TestMethod]
    public async Task CliBackendParity_WorkflowMessages_AreDeliveredThroughEventStream()
    {
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumDir = Path.Combine(musicRoot, "Artist", "Album");
                Directory.CreateDirectory(albumDir);
                File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
            },
            scenario: async ctx =>
            {
                var messages = new ConcurrentBag<string>();
                var observed = new ConcurrentBag<string>();
                ctx.Backend.ActivityReceived += activity =>
                {
                    observed.Add($"{activity.Type}:{activity.Payload.GetType().Name}");
                    if (activity.Payload is WorkflowMessageActivityDto message)
                    {
                        messages.Add(message.Message);
                    }
                };

                var summary = await ctx.Backend.SubmitAlbumJobAsync(
                    new SubmitAlbumJobRequestDto(new AlbumQueryDto("Artist", "Album", "", "", false)),
                    ctx.Token);

                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);
                await WaitForConditionAsync(
                    ctx.Backend,
                    () => messages.Contains("Auto profiles active: album-auto"),
                    $"Timed out waiting for workflow message on {ctx.Name}. Observed: {string.Join(", ", observed)}");
            },
            profiles: AlbumAutoProfileCatalog());
    }

    [TestMethod]
    public async Task CliBackendParity_ManualSkipFromIndexedList_DoesNotBecomeAlreadyExistsOnRerun()
    {
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumDir = Path.Combine(musicRoot, "Artist", "Album");
                Directory.CreateDirectory(albumDir);
                File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
                File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");
            },
            scenario: async ctx =>
            {
                string listPath = Path.Combine(ctx.OutputDir, $"albums-{Guid.NewGuid()}.txt");
                File.WriteAllLines(listPath, ["a:\"Artist - Album\""]);

                var first = await SubmitManualListAlbumWorkflowAsync(ctx, listPath);
                var firstAlbum = await WaitForWorkflowJobAsync(
                    ctx.Backend,
                    first.WorkflowId,
                    job => job.Kind == ServerJobKind.Album
                        && job.LifecycleState == ServerJobLifecycleState.AwaitingSelection);

                var visibleRoots = await ctx.Backend.GetJobsAsync(new JobQuery(
                    null, null, null, first.WorkflowId, IncludeAll: false, ExcludeExtractResults: true), ctx.Token);
                CollectionAssert.AreEqual(new[] { first.JobId }, visibleRoots.Select(job => job.JobId).ToArray(), ctx.Name);

                Assert.IsTrue(await ctx.Backend.SkipManualSelectionAsync(firstAlbum.JobId, ctx.Token), ctx.Name);
                await WaitForJobStateAsync(ctx.Backend, firstAlbum.JobId, ExpectedJobStatus.Skipped);

                var second = await SubmitManualListAlbumWorkflowAsync(ctx, listPath);
                var secondAlbum = await WaitForWorkflowJobAsync(
                    ctx.Backend,
                    second.WorkflowId,
                    job => job.Kind == ServerJobKind.Album
                        && job.LifecycleState is ServerJobLifecycleState.AwaitingSelection
                            or ServerJobLifecycleState.Terminal);

                Assert.AreEqual(
                    ExpectedJobStatus.AwaitingSelection,
                    ProjectState(secondAlbum),
                    $"{ctx.Name}: manual skip should not be persisted as already-exists.");

                Assert.IsTrue(await ctx.Backend.SkipManualSelectionAsync(secondAlbum.JobId, ctx.Token), ctx.Name);
            });
    }

    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    public async Task CliBackendParity_ManualMusicSelectionClosesOrCancelsSelectedWork(bool albumMode, bool cancel, bool aggregate)
    {
        await RunForEachInjectedClientBackendAsync(() =>
        {
            var gate = new DownloadGate();
            var client = new MockSoulseekClient(
            [
                SearchResponse("first-peer", @"Music\Artist\Album\01 - Track.mp3"),
                SearchResponse("second-peer", albumMode ? @"Other\Artist\Album\01 - Track.mp3" : "Artist - Track.mp3"),
            ]) { BeforeDownloadCompletesAsync = gate.BlockMatchingUserAsync };
            return (client, gate);
        }, async ctx =>
        {
            JobSummaryDto source, child;
            string selectedUsername;
            Func<Task> chooseAlternative;
            if (albumMode)
            {
                var query = new AlbumQueryDto("Artist", "Album", "", "", false);
                source = aggregate
                    ? await ctx.Backend.SubmitAlbumAggregateJobAsync(new SubmitAlbumAggregateJobRequestDto(query,
                        DownloadBehavior: new DownloadBehaviorPolicyDto(AlbumAggregate: ServerDownloadBehavior.Manual)), ctx.Token)
                    : await ctx.Backend.SubmitAlbumJobAsync(new SubmitAlbumJobRequestDto(query,
                        DownloadBehavior: new DownloadBehaviorPolicyDto(Album: ServerDownloadBehavior.Manual)), ctx.Token);
                await WaitForJobStateAsync(ctx.Backend, source.JobId, ExpectedJobStatus.AwaitingSelection);
                IReadOnlyList<AlbumFolderDto> folders;
                if (aggregate)
                {
                    var groups = await ctx.Backend.GetAggregateAlbumResultsAsync(source.JobId,
                        new AggregateAlbumProjectionRequestDto(IncludeFolders: true), ctx.Token);
                    Assert.IsNotNull(groups, ctx.Name);
                    folders = groups.Items.Single().Folders!;
                }
                else
                {
                    var results = await ctx.Backend.GetFolderResultsAsync(source.JobId, includeFiles: true, ctx.Token);
                    Assert.IsNotNull(results, ctx.Name);
                    folders = results.Items;
                }
                Assert.AreEqual(2, folders.Count, ctx.Name);
                var folder = folders[^1];
                child = (await ctx.Backend.StartFolderDownloadAsync(source.JobId,
                    new StartFolderDownloadRequestDto(folder.Ref, AlbumQuery: query), ctx.Token))!;
                selectedUsername = folder.Username;
                chooseAlternative = async () => { await ctx.Backend.StartFolderDownloadAsync(source.JobId,
                    new StartFolderDownloadRequestDto(folders[0].Ref, AlbumQuery: query), ctx.Token); };
            }
            else
            {
                var query = new SongQueryDto("Artist", "Track", "", "", -1, false);
                source = aggregate
                    ? await ctx.Backend.SubmitAggregateJobAsync(new SubmitAggregateJobRequestDto(query,
                        DownloadBehavior: new DownloadBehaviorPolicyDto(Aggregate: ServerDownloadBehavior.Manual)), ctx.Token)
                    : await ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(query,
                        DownloadBehavior: new DownloadBehaviorPolicyDto(Song: ServerDownloadBehavior.Manual)), ctx.Token);
                await WaitForJobStateAsync(ctx.Backend, source.JobId, ExpectedJobStatus.AwaitingSelection);
                IReadOnlyList<FileCandidateDto> candidates;
                if (aggregate)
                {
                    var groups = await ctx.Backend.GetAggregateTrackResultsAsync(source.JobId,
                        new AggregateTrackProjectionRequestDto(IncludeCandidates: true), ctx.Token);
                    Assert.IsNotNull(groups, ctx.Name);
                    candidates = groups.Items.Single().Candidates!;
                }
                else
                {
                    var results = await ctx.Backend.GetFileResultsAsync(source.JobId, ctx.Token);
                    Assert.IsNotNull(results, ctx.Name);
                    candidates = results.Items;
                }
                Assert.AreEqual(2, candidates.Count, ctx.Name);
                var candidate = candidates[^1];
                child = (await ctx.Backend.StartFileDownloadsAsync(source.JobId,
                    new StartFileDownloadsRequestDto([candidate.Ref]), ctx.Token))!.Single();
                selectedUsername = candidate.Username;
                chooseAlternative = async () => { await ctx.Backend.StartFileDownloadsAsync(source.JobId,
                    new StartFileDownloadsRequestDto([candidates[0].Ref]), ctx.Token); };
            }
            Assert.IsNotNull(child, ctx.Name);
            if (!aggregate) Assert.AreEqual(source.JobId, child.JobId, ctx.Name);
            await ctx.Gate!.WaitForStartedAsync();
            Assert.AreEqual(selectedUsername, ctx.Gate.BlockedUsername, ctx.Name);
            async Task AssertRejected()
            {
                if (ctx.Name == "remote")
                {
                    var error = await Assert.ThrowsAsync<SockseekApiRequestException>(chooseAlternative);
                    Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, error.StatusCode);
                }
                else await Assert.ThrowsAsync<ArgumentException>(chooseAlternative);
            }
            if (aggregate) await AssertRejected();
            if (cancel)
            {
                Assert.IsTrue(await ctx.Backend.CancelJobAsync(source.JobId, ctx.Token), ctx.Name);
                // Keep the transfer gate closed: owner cancellation must stop
                // selected work, rather than waiting for the peer to finish it.
                var cancellations = new List<(Guid, ServerJobCancellationSource)>
                    { (source.JobId, ServerJobCancellationSource.UserRequestedJob) };
                if (aggregate) cancellations.Add((child.JobId, ServerJobCancellationSource.ParentJob));
                foreach (var (id, cause) in cancellations)
                {
                    var detail = await BackendTestWaiter.UntilAsync(ctx.Backend,
                        ct => ctx.Backend.GetJobDetailAsync(id, ct),
                        value => value?.Summary.LifecycleState == ServerJobLifecycleState.Terminal,
                        "Cancelling a manual selection must finish its selected work.");
                    Assert.AreEqual(ServerJobTerminalOutcome.Cancelled, detail!.Summary.TerminalOutcome, ctx.Name);
                    Assert.AreEqual(cause, detail.Summary.CancellationSource, ctx.Name);
                }
                await WaitForWorkflowStateAsync(ctx.Backend, source.WorkflowId, ServerWorkflowState.Failed);
                Assert.AreEqual(0, ctx.DownloadedRelativePaths().Length, ctx.Name);
            }
            else
            {
                Assert.IsTrue(await ctx.Backend.CompleteManualSelectionAsync(source.JobId, ctx.Token), ctx.Name);
                await AssertRejected();
                ctx.Gate.Open();
                await WaitForJobStateAsync(ctx.Backend, source.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, source.WorkflowId, ServerWorkflowState.Completed);
            }
            var jobs = await ctx.Backend.GetJobsAsync(new JobQuery(null, null, null, source.WorkflowId, IncludeAll: true), ctx.Token);
            Assert.AreEqual(aggregate ? 1 : 0, jobs.Count(job => job.SourceJobId == source.JobId
                && job.Kind == (albumMode ? ServerJobKind.Album : ServerJobKind.Song)), ctx.Name);
        });
    }

    [TestMethod]
    public async Task CliBackendParity_CancelsIdlePickerWhileAnUnrelatedDownloadHoldsTheOnlySlot()
    {
        await RunForEachInjectedClientBackendAsync(() =>
        {
            var gate = new DownloadGate();
            var client = new MockSoulseekClient(
            [
                new Soulseek.SearchResponse("peer", 1, true, 100_000, 0,
                    SearchResponse("peer", @"Music\Artist\Album\01 - First.mp3").Files.Concat(
                        SearchResponse("peer", @"Music\Artist\Album\02 - Second.mp3").Files)),
            ]) { BeforeDownloadCompletesAsync = gate.BlockMatchingUserAsync };
            return (client, gate);
        }, async ctx =>
        {
            var picker = await ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(
                new SongQueryDto("Artist", "First", "", "", -1, false),
                DownloadBehavior: new DownloadBehaviorPolicyDto(Song: ServerDownloadBehavior.Manual)), ctx.Token);
            await WaitForJobStateAsync(ctx.Backend, picker.JobId, ExpectedJobStatus.AwaitingSelection);
            var unrelated = await ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(
                new SongQueryDto("Artist", "Second", "", "", -1, false)), ctx.Token);
            await ctx.Gate!.WaitForStartedAsync();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.IsTrue(await ctx.Backend.CancelJobAsync(picker.JobId, ctx.Token), ctx.Name);
            var cancelled = await BackendTestWaiter.UntilAsync(ctx.Backend,
                ct => ctx.Backend.GetJobDetailAsync(picker.JobId, ct),
                detail => detail?.Summary.TerminalOutcome == ServerJobTerminalOutcome.Cancelled,
                "An idle picker must cancel while unrelated work holds the execution slot.");
            Console.WriteLine($"Busy-picker cancellation {ctx.Name}: "
                + $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
            Assert.AreEqual(ServerJobCancellationSource.UserRequestedJob, cancelled!.Summary.CancellationSource, ctx.Name);
            var active = await ctx.Backend.GetJobDetailAsync(unrelated.JobId, ctx.Token);
            Assert.AreEqual(ServerJobLifecycleState.Running, active!.Summary.LifecycleState, ctx.Name);
            Assert.AreEqual(ServerJobCancellationSource.None, active.Summary.CancellationSource, ctx.Name);
            var pickerPayload = Assert.IsInstanceOfType<SongJobPayloadDto>(cancelled.Payload);
            Assert.IsNull(pickerPayload.LastAttemptedFile, "Cancelling the picker must not start its candidates.");
            ctx.Gate.Open();
            await WaitForJobStateAsync(ctx.Backend, unrelated.JobId, ExpectedJobStatus.Succeeded);
            Assert.AreEqual(1, ctx.DownloadedRelativePaths().Length, ctx.Name);
        }, concurrency: 1);
    }

    private static async Task RunForEachBackendAsync(
        Action<string> seedMusic,
        Func<ParityBackendContext, Task> scenario,
        ProfileCatalog? profiles = null)
    {
        await Task.WhenAll(
            RunAsync(() => ParityBackendContext.CreateLocalAsync(seedMusic, profiles), scenario),
            RunAsync(() => ParityBackendContext.CreateRemoteAsync(seedMusic, profiles), scenario));
    }

    private static async Task RunForEachInjectedClientBackendAsync(
        Func<(MockSoulseekClient Client, DownloadGate Gate)> createClient,
        Func<ParityBackendContext, Task> scenario,
        int? concurrency = null)
    {
        var localClient = createClient();
        var remoteClient = createClient();
        await Task.WhenAll(
            RunAsync(() => ParityBackendContext.CreateLocalAsync(localClient.Client, localClient.Gate, concurrency), scenario),
            RunAsync(() => ParityBackendContext.CreateRemoteAsync(remoteClient.Client, remoteClient.Gate, concurrency), scenario));
    }

    private static async Task RunAsync(
        Func<Task<ParityBackendContext>> createContext,
        Func<ParityBackendContext, Task> scenario)
    {
        await using var context = await createContext();
        try { await scenario(context); }
        finally { context.Gate?.Open(); }
    }

    private static ProfileCatalog AlbumAutoProfileCatalog()
        => new()
        {
            AutoProfiles =
            [
                new SettingsProfile
                {
                    Name = "album-auto",
                    Condition = "album",
                },
            ],
        };

    private static ProfileCatalog SongLayoutProfileCatalog()
    {
        var profile = new SettingsProfile { Name = "song-layout", Download = new SettingsPatch([new("Output.NameFormat", "from-profile/{filename}")]) };
        return new ProfileCatalog { NamedProfiles = [profile] };
    }

    private static async Task AssertManualAggregateSongSelectionAsync(ParityBackendContext ctx)
    {
        string[] before = ctx.DownloadedRelativePaths();
        var source = await ctx.Backend.SubmitAggregateJobAsync(new SubmitAggregateJobRequestDto(
            new SongQueryDto("Artist", "", "", "", -1, false),
            Options: new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                Skip: new SkipSettingsPatchDto(SkipExisting: true))),
            DownloadBehavior: new DownloadBehaviorPolicyDto(Aggregate: ServerDownloadBehavior.Manual)), ctx.Token);
        await WaitForJobStateAsync(ctx.Backend, source.JobId, ExpectedJobStatus.AwaitingSelection);
        var groups = await ctx.Backend.GetAggregateTrackResultsAsync(source.JobId,
            new AggregateTrackProjectionRequestDto(IncludeCandidates: true), ctx.Token);
        Assert.IsNotNull(groups, ctx.Name);
        Assert.AreEqual(2, groups.Items.Count, ctx.Name);
        var chosenGroup = groups.Items[^1];
        Assert.IsNotNull(chosenGroup.Candidates, ctx.Name);
        Assert.AreEqual(2, chosenGroup.Candidates.Count, ctx.Name);
        var candidate = chosenGroup.Candidates[^1];
        string filename = Path.GetFileName(Utils.GetAsPathSlsk(candidate.Filename));
        string output = Path.Combine(ctx.OutputDir, "manual-aggregate");
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var selected = await ctx.Backend.StartFileDownloadsAsync(source.JobId,
            new StartFileDownloadsRequestDto([candidate.Ref], new SubmissionOptionsDto(
                ProfileNames: ["song-layout"], DownloadSettings: new DownloadSettingsPatchDto(
                    Output: new OutputSettingsPatchDto(ParentDir: output)))), ctx.Token);
        Console.WriteLine($"CLI aggregate song selection {ctx.Name}: acceptance-ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
        Assert.IsNotNull(selected, ctx.Name);
        var child = selected.Single();
        Assert.AreEqual(source.WorkflowId, child.WorkflowId, ctx.Name);
        Assert.AreEqual(source.JobId, child.SourceJobId, ctx.Name);
        Assert.AreEqual(source.JobId, child.ParentJobId, ctx.Name);
        Assert.AreEqual(source.SubmissionId, child.SubmissionId, ctx.Name);
        await WaitForJobStateAsync(ctx.Backend, child.JobId, ExpectedJobStatus.Succeeded);
        var childDetail = await ctx.Backend.GetJobDetailAsync(child.JobId, ctx.Token);
        var song = Assert.IsInstanceOfType<SongJobPayloadDto>(childDetail?.Payload);
        Assert.AreEqual(candidate.Filename, song.ResolvedFilename, ctx.Name);
        Assert.AreEqual(candidate.Username, song.ResolvedUsername, ctx.Name);
        var remainingGroups = await ctx.Backend.GetAggregateTrackResultsAsync(source.JobId,
            new AggregateTrackProjectionRequestDto(IncludeCandidates: true), ctx.Token);
        Assert.IsNotNull(remainingGroups, ctx.Name);
        Assert.AreEqual(2, remainingGroups.Items.Count, ctx.Name);
        Assert.IsTrue(remainingGroups.Items.All(group => group.Candidates?.Count == 2),
            $"{ctx.Name}: executing a choice must preserve the open aggregate's discovery alternatives.");
        var siblingGroup = remainingGroups.Items.Single(group => group.Query.Title != chosenGroup.Query.Title);
        var siblingCandidate = siblingGroup.Candidates![0];
        var siblingSelection = await ctx.Backend.StartFileDownloadsAsync(source.JobId,
            new StartFileDownloadsRequestDto([siblingCandidate.Ref]), ctx.Token);
        Assert.IsNotNull(siblingSelection, ctx.Name);
        var sibling = siblingSelection.Single();
        await WaitForJobStateAsync(ctx.Backend, sibling.JobId, ExpectedJobStatus.AlreadyExists);
        var siblingDetail = await ctx.Backend.GetJobDetailAsync(sibling.JobId, ctx.Token);
        var siblingSong = Assert.IsInstanceOfType<SongJobPayloadDto>(siblingDetail?.Payload);
        string siblingFilename = Path.GetFileName(Utils.GetAsPathSlsk(siblingCandidate.Filename));
        Assert.AreEqual(Path.Combine(ctx.OutputDir, "Album", siblingFilename), siblingSong.File.DownloadPath,
            $"{ctx.Name}: selection-specific profiles and paths must not leak to another group.");
        Assert.IsTrue(await ctx.Backend.CompleteManualSelectionAsync(source.JobId, ctx.Token), ctx.Name);
        var completed = await BackendTestWaiter.UntilAsync(ctx.Backend,
            ct => ctx.Backend.GetJobDetailAsync(source.JobId, ct),
            detail => detail?.Summary.LifecycleState == ServerJobLifecycleState.Terminal,
            $"{ctx.Name}: manual aggregate selection never completed.");
        Assert.IsNotNull(completed, ctx.Name);
        Assert.AreEqual(ServerJobTerminalOutcome.Succeeded, completed.Summary.TerminalOutcome,
            $"{ctx.Name}: selected aggregate child succeeded but root is {completed.Summary.TerminalOutcome}: {completed.Summary.FailureReason}.");
        await WaitForWorkflowStateAsync(ctx.Backend, source.WorkflowId, ServerWorkflowState.Completed);
        string expectedContent = filename.EndsWith("First.mp3", StringComparison.Ordinal) ? "first" : "second";
        Assert.AreEqual(expectedContent, await File.ReadAllTextAsync(Path.Combine(output, "from-profile", filename)), ctx.Name);
        CollectionAssert.AreEqual(before.Append($"manual-aggregate/from-profile/{filename}")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray(), ctx.DownloadedRelativePaths(), ctx.Name);
    }

    private static async Task AssertManualSongFailureAndRecoveryAsync(ParityBackendContext ctx)
    {
        string[] before = ctx.DownloadedRelativePaths();
        string output = Path.Combine(ctx.OutputDir, "manual-options");
        var options = new SubmissionOptionsDto(ProfileNames: ["song-layout"],
            DownloadSettings: new DownloadSettingsPatchDto(
                Output: new OutputSettingsPatchDto(ParentDir: output),
                Transfer: new TransferSettingsPatchDto(MaxDownloadRetries: 1, UnknownErrorRetries: 0)));

        async Task<(JobSummaryDto Job, IReadOnlyList<FileCandidateDto> Candidates)> AwaitChoice(int? expectedCount)
        {
            var job = await ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(
                new SongQueryDto("PeerDrop", "", "", "", -1, false),
                DownloadBehavior: new DownloadBehaviorPolicyDto(Song: ServerDownloadBehavior.Manual)), ctx.Token);
            await WaitForJobStateAsync(ctx.Backend, job.JobId, ExpectedJobStatus.AwaitingSelection);
            var candidates = await ctx.Backend.GetFileResultsAsync(job.JobId, ctx.Token);
            Assert.IsNotNull(candidates, ctx.Name);
            if (expectedCount is int count) Assert.AreEqual(count, candidates.Items.Count, ctx.Name);
            return (job, candidates.Items);
        }

        var missing = await AwaitChoice(2);
        var chosen = missing.Candidates[^1];
        string chosenName = Path.GetFileName(Utils.GetAsPathSlsk(chosen.Filename));
        CollectionAssert.Contains(new[] { "PeerDrop - Missing.mp3", "PeerDrop - Survivor.mp3" }, chosenName);
        // The non-first advertised file vanishes before selection. A healthy
        // first-ranked alternative remains; an explicit choice cannot fall back.
        File.Delete(Path.Combine(ctx.MusicRoot, "PeerDrop", chosenName));
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var selected = await ctx.Backend.StartFileDownloadsAsync(missing.Job.JobId,
            new StartFileDownloadsRequestDto([chosen.Ref], options), ctx.Token);
        Console.WriteLine($"CLI manual song {ctx.Name}: selection=failing, ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
        Assert.IsNotNull(selected, ctx.Name);
        Assert.AreEqual(missing.Job.JobId, selected.Single().JobId, ctx.Name);
        await WaitForJobStateAsync(ctx.Backend, missing.Job.JobId, ExpectedJobStatus.Failed);
        await WaitForWorkflowStateAsync(ctx.Backend, missing.Job.WorkflowId, ServerWorkflowState.Failed);
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        var failed = await ctx.Backend.GetJobDetailAsync(missing.Job.JobId, ctx.Token);
        Console.WriteLine($"CLI manual song {ctx.Name}: read=failed-detail, ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
        Assert.IsNotNull(failed, ctx.Name);
        Assert.IsInstanceOfType<SongJobPayloadDto>(failed.Payload);
        var failedSong = (SongJobPayloadDto)failed.Payload!;
        Assert.AreEqual(chosen.Filename, failedSong.ResolvedFilename, ctx.Name);
        Assert.AreEqual(chosen.Username, failedSong.ResolvedUsername, ctx.Name);
        Assert.AreEqual(0L, failedSong.File.BytesTransferred, ctx.Name);
        CollectionAssert.AreEqual(before, ctx.DownloadedRelativePaths(), ctx.Name);

        // Discovery may still advertise the vanished file from its cache.
        // A new workflow can explicitly choose the original healthy alternative.
        var survivor = await AwaitChoice(null);
        var remaining = survivor.Candidates.Single(candidate => candidate.Ref == missing.Candidates[0].Ref);
        Assert.AreNotEqual(chosen.Ref, remaining.Ref, ctx.Name);
        string remainingName = Path.GetFileName(Utils.GetAsPathSlsk(remaining.Filename));
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        selected = await ctx.Backend.StartFileDownloadsAsync(survivor.Job.JobId,
            new StartFileDownloadsRequestDto([remaining.Ref], options), ctx.Token);
        Console.WriteLine($"CLI manual song {ctx.Name}: selection=healthy, ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
        Assert.IsNotNull(selected, ctx.Name);
        Assert.AreEqual(survivor.Job.JobId, selected.Single().JobId, ctx.Name);
        await WaitForJobStateAsync(ctx.Backend, survivor.Job.JobId, ExpectedJobStatus.Succeeded);
        await WaitForWorkflowStateAsync(ctx.Backend, survivor.Job.WorkflowId, ServerWorkflowState.Completed);
        string expected = Path.Combine(output, "from-profile", remainingName);
        string expectedContents = remainingName == "PeerDrop - Missing.mp3" ? "missing" : "survivor";
        Assert.AreEqual(expectedContents, await File.ReadAllTextAsync(expected), ctx.Name);
        CollectionAssert.AreEqual(before.Append($"manual-options/from-profile/{remainingName}")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray(), ctx.DownloadedRelativePaths(), ctx.Name);

        // Selection-specific profiles/paths cannot become the daemon/CLI defaults.
        var unchanged = await ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(
            new SongQueryDto("Artist", "First", "", "", -1, false)), ctx.Token);
        await WaitForWorkflowStateAsync(ctx.Backend, unchanged.WorkflowId, ServerWorkflowState.Completed);
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        var unchangedDetail = await ctx.Backend.GetJobDetailAsync(unchanged.JobId, ctx.Token);
        Console.WriteLine($"CLI manual song {ctx.Name}: read=default-detail, ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
        Assert.IsNotNull(unchangedDetail, ctx.Name);
        Assert.IsInstanceOfType<SongJobPayloadDto>(unchangedDetail.Payload);
        var unchangedSong = (SongJobPayloadDto)unchangedDetail.Payload!;
        string defaultOutput = Path.Combine(ctx.OutputDir, "Album", "Artist - First.mp3");
        Assert.AreEqual(defaultOutput, unchangedSong.File.DownloadPath, ctx.Name);
        Assert.AreEqual("first", await File.ReadAllTextAsync(defaultOutput), ctx.Name);
        CollectionAssert.AreEqual(before.Append($"manual-options/from-profile/{remainingName}")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray(), ctx.DownloadedRelativePaths(), ctx.Name);
    }

    private static ProfileCatalog AutomaticAlbumProfileCatalog()
    {
        var profile = new SettingsProfile { Name = "album-layout", Download = new SettingsPatch([new("Output.NameFormat", "{foldername}/from-profile/{filename}")]) };
        return new ProfileCatalog { NamedProfiles = [profile] };
    }

    private static async Task AssertAutomaticAlbumDownloadAsync(
        ParityBackendContext ctx, bool aggregate, long expectedTransferredBytes,
        IReadOnlyDictionary<string, string> expectedFiles)
    {
        string[] before = ctx.DownloadedRelativePaths();
        var options = new SubmissionOptionsDto(ProfileNames: ["album-layout"],
            DownloadSettings: new DownloadSettingsPatchDto(Output: new OutputSettingsPatchDto(
                ParentDir: Path.Combine(ctx.OutputDir, "automatic-output"))));
        var query = new AlbumQueryDto("Artist", aggregate ? "" : "Album", "", "", false);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        JobSummaryDto submitted = aggregate
            ? await ctx.Backend.SubmitAlbumAggregateJobAsync(new SubmitAlbumAggregateJobRequestDto(query, options), ctx.Token)
            : await ctx.Backend.SubmitAlbumJobAsync(new SubmitAlbumJobRequestDto(query, options), ctx.Token);
        Console.WriteLine($"CLI automatic album {ctx.Name}: aggregate={aggregate}, "
            + $"acceptance-ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
        Assert.IsNull(submitted.SourceJobId, ctx.Name);
        Assert.IsNull(submitted.ParentJobId, ctx.Name);
        await WaitForJobStateAsync(ctx.Backend, submitted.JobId, ExpectedJobStatus.Succeeded);
        await WaitForWorkflowStateAsync(ctx.Backend, submitted.WorkflowId, ServerWorkflowState.Completed);

        long metadataStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var detail = await ReadMetadataAsync("root detail",
            () => ctx.Backend.GetJobDetailAsync(submitted.JobId, ctx.Token));
        Assert.IsNotNull(detail, ctx.Name);
        var directories = new List<DirectoryDownloadStateDto>();
        if (aggregate)
        {
            Assert.IsInstanceOfType<AlbumAggregateJobPayloadDto>(detail.Payload);
            var albums = (AlbumAggregateJobPayloadDto)detail.Payload!;
            Assert.AreEqual(2, albums.ResultCount, ctx.Name);
            Assert.AreEqual(2, albums.CompletedAlbumCount, ctx.Name);
            Assert.AreEqual(2, albums.SucceededAlbumCount, ctx.Name);
            Assert.AreEqual(0, albums.FailedAlbumCount, ctx.Name);
            var children = await ReadMetadataAsync("root children", () => ctx.Backend.GetJobsAsync(
                new JobQuery(null, null, null, null, ParentJobId: submitted.JobId, IncludeAll: true), ctx.Token));
            Assert.AreEqual(3, children.Count, ctx.Name);
            var group = children.Single(child => child.Kind == ServerJobKind.JobList);
            Assert.AreEqual(submitted.SubmissionId, group.SubmissionId, ctx.Name);
            Assert.AreEqual(ServerJobRole.Orchestration, group.Role, ctx.Name);
            children = await ReadMetadataAsync("generated albums", () => ctx.Backend.GetJobsAsync(
                new JobQuery(null, null, ServerJobKind.Album, null, ParentJobId: submitted.JobId, IncludeAll: true), ctx.Token));
            Assert.AreEqual(2, children.Count, ctx.Name);
            foreach (var child in children)
            {
                Assert.AreEqual(ServerJobKind.Album, child.Kind, ctx.Name);
                Assert.AreEqual(submitted.JobId, child.ParentJobId, ctx.Name);
                Assert.AreEqual(submitted.WorkflowId, child.WorkflowId, ctx.Name);
                Assert.AreEqual(submitted.SubmissionId, child.SubmissionId, ctx.Name);
                Assert.AreEqual(ServerJobRole.ExecutionChild, child.Role, ctx.Name);
                var album = await ReadMetadataAsync("album detail",
                    () => ctx.Backend.GetJobDetailAsync(child.JobId, ctx.Token));
                Assert.IsNotNull(album, ctx.Name);
                Assert.IsInstanceOfType<AlbumJobPayloadDto>(album.Payload);
                directories.Add(((AlbumJobPayloadDto)album.Payload!).Directory);
            }
        }
        else
        {
            Assert.IsInstanceOfType<AlbumJobPayloadDto>(detail.Payload);
            directories.Add(((AlbumJobPayloadDto)detail.Payload!).Directory);
        }
        Console.WriteLine($"CLI automatic album {ctx.Name}: aggregate={aggregate}, "
            + $"metadata-ms={System.Diagnostics.Stopwatch.GetElapsedTime(metadataStarted).TotalMilliseconds:F1}.");
        Assert.AreEqual(expectedFiles.Count, directories.Sum(directory => directory.FileCount), ctx.Name);
        Assert.AreEqual(expectedFiles.Count, directories.Sum(directory => directory.SuccessfulFileCount), ctx.Name);
        Assert.AreEqual(expectedFiles.Count, directories.Sum(directory => directory.TerminalFileCount), ctx.Name);
        Assert.AreEqual(0, directories.Sum(directory => directory.FailedFileCount), ctx.Name);
        Assert.AreEqual((long)expectedFiles.Sum(file => file.Value.Length), directories.Sum(directory => directory.TotalKnownBytes), ctx.Name);
        // Earlier manual outputs can be reused. Cached copies are successful
        // files, not additional network bytes; only the previously unselected
        // aggregate child needs a new transfer.
        Assert.AreEqual(expectedTransferredBytes, directories.Sum(directory => directory.BytesTransferred), ctx.Name);
        Assert.IsTrue(directories.All(directory => directory.ProgressPercent == 100), ctx.Name);
        CollectionAssert.AreEqual(before.Concat(expectedFiles.Keys).Order(StringComparer.Ordinal).ToArray(),
            ctx.DownloadedRelativePaths(), ctx.Name);
        foreach (var (path, content) in expectedFiles)
            Assert.AreEqual(content, await File.ReadAllTextAsync(Path.Combine(ctx.OutputDir, path)), ctx.Name);

        async Task<T> ReadMetadataAsync<T>(string operation, Func<Task<T>> read)
        {
            long readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double gcBefore = GC.GetTotalPauseDuration().TotalMilliseconds;
            T result = await read();
            Console.WriteLine($"CLI automatic album {ctx.Name}: aggregate={aggregate}, "
                + $"read={operation}, ms={System.Diagnostics.Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds:F1}, "
                + $"issued-at={issuedAt}, gc-ms={GC.GetTotalPauseDuration().TotalMilliseconds - gcBefore:F1}.");
            return result;
        }
    }

    private static Task<JobSummaryDto> SubmitManualListAlbumWorkflowAsync(ParityBackendContext ctx, string listPath)
        => ctx.Backend.SubmitExtractJobAsync(
            new SubmitExtractJobRequestDto(
                listPath,
                InputType: "List",
                Options: new SubmissionOptionsDto(),
                ResultDownloadBehavior: new DownloadBehaviorPolicyDto(
                    Album: ServerDownloadBehavior.Manual,
                    AlbumAggregate: ServerDownloadBehavior.Manual)),
            ctx.Token);

    private static SubmissionOptionsJobSettingsResolver CreateLocalResolver(
        DownloadSettings downloadSettings,
        ProfileCatalog? profiles)
    {
        IJobSettingsResolver inner = profiles == null
            ? DefaultJobSettingsResolver.Instance
            : new ProfileJobSettingsResolver(
                downloadSettings,
                profiles,
                namedProfiles: [],
                cliProfile: null,
                context: new ProfileContext());

        return new SubmissionOptionsJobSettingsResolver(
            inner,
            normalize: settings => SettingsNormalizer.NormalizeDownloadPaths(settings, settings.RuntimePathContext));
    }

    private sealed class DownloadGate
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int blocked;

        public string? BlockedUsername { get; private set; }

        public async Task BlockMatchingUserAsync(string candidateUsername, string _, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref blocked, 1, 0) != 0)
                return;

            BlockedUsername = candidateUsername;
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        }

        public Task WaitForStartedAsync()
            => started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Open() => release.TrySetResult();
    }

    private sealed class ParityBackendContext : IAsyncDisposable
    {
        private readonly CancellationTokenSource cts;
        private readonly DownloadEngine? engine;
        private readonly Task? engineTask;
        private readonly Func<Task>? stopAppAsync;
        private readonly IAsyncDisposable? appDisposable;
        private readonly RemoteCliBackend? remoteBackend;
        private bool engineCompleted;

        private ParityBackendContext(
            string name,
            string musicRoot,
            string outputDir,
            ICliBackend backend,
            DownloadEngine? engine = null,
            Task? engineTask = null,
            Func<Task>? stopAppAsync = null,
            IAsyncDisposable? appDisposable = null,
            RemoteCliBackend? remoteBackend = null,
            DownloadGate? gate = null,
            CancellationTokenSource? cts = null)
        {
            this.cts = cts ?? new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Name = name;
            MusicRoot = musicRoot;
            OutputDir = outputDir;
            Backend = backend;
            this.engine = engine;
            this.engineTask = engineTask;
            this.stopAppAsync = stopAppAsync;
            this.appDisposable = appDisposable;
            this.remoteBackend = remoteBackend;
            Gate = gate;
        }

        public string Name { get; }
        public string MusicRoot { get; }
        public string OutputDir { get; }
        public ICliBackend Backend { get; }
        public DownloadGate? Gate { get; }
        public CancellationToken Token => cts.Token;

        public static Task<ParityBackendContext> CreateLocalAsync(Action<string> seedMusic, ProfileCatalog? profiles = null)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-local-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-local-out-");
            seedMusic(musicRoot);

            var engineSettings = CreateEngineSettings(musicRoot);
            var downloadSettings = CreateDownloadSettings(outputDir);
            var resolver = CreateLocalResolver(downloadSettings, profiles);
            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings), resolver);
            var backend = new LocalCliBackend(engine, downloadSettings, resolver);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var engineTask = engine.RunAsync(cts.Token);

            return Task.FromResult(new ParityBackendContext("local", musicRoot, outputDir, backend, engine, engineTask, cts: cts));
        }

        public static Task<ParityBackendContext> CreateLocalAsync(MockSoulseekClient client, DownloadGate gate, int? concurrency = null)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-local-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-local-out-");

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            if (concurrency is int localConcurrency)
                engineSettings.ConcurrentJobs = engineSettings.ConcurrentExtractors = localConcurrency;
            var downloadSettings = CreateDownloadSettings(outputDir);
            downloadSettings.Output.NameFormat = "{filename}";
            var resolver = CreateLocalResolver(downloadSettings, profiles: null);
            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings, client), resolver);
            var backend = new LocalCliBackend(engine, downloadSettings, resolver);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var engineTask = engine.RunAsync(cts.Token);

            return Task.FromResult(new ParityBackendContext("local", musicRoot, outputDir, backend, engine, engineTask, gate: gate, cts: cts));
        }

        public static async Task<ParityBackendContext> CreateRemoteAsync(Action<string> seedMusic, ProfileCatalog? profiles = null)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-remote-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-remote-out-");
            seedMusic(musicRoot);

            var app = ServerHost.Build([], new ServerOptions
            {
                Engine = CreateEngineSettings(musicRoot),
                DefaultDownload = CreateDownloadSettings(outputDir),
                Profiles = profiles ?? ProfileCatalog.Empty,
                Persistence = new ServerPersistenceOptions
                {
                    Enabled = true,
                    DataDirectory = Path.Combine(outputDir, ".server-data"),
                },
            }, DynamicLoopbackUrl);

            await app.StartAsync();
            string url = GetBoundUrl(app);
            var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            return new ParityBackendContext(
                "remote",
                musicRoot,
                outputDir,
                backend,
                stopAppAsync: () => app.StopAsync(),
                appDisposable: app,
                remoteBackend: backend);
        }

        public static async Task<ParityBackendContext> CreateRemoteAsync(MockSoulseekClient client, DownloadGate gate, int? concurrency = null)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-remote-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-remote-out-");

            var downloadSettings = CreateDownloadSettings(outputDir);
            downloadSettings.Output.NameFormat = "{filename}";
            var engineSettings = new EngineSettings
            {
                Username = "test_user", Password = "test_pass", LogLevel = LogLevel.None,
            };
            if (concurrency is int remoteConcurrency)
                engineSettings.ConcurrentJobs = engineSettings.ConcurrentExtractors = remoteConcurrency;
            var app = TestServerHost.Build([], new ServerOptions
            {
                Engine = engineSettings,
                DefaultDownload = downloadSettings,
                Profiles = ProfileCatalog.Empty,
                ClientFactory = _ => client,
                Persistence = new ServerPersistenceOptions
                {
                    Enabled = true,
                    DataDirectory = Path.Combine(outputDir, ".server-data"),
                },
            }, DynamicLoopbackUrl);

            await app.StartAsync();
            string url = GetBoundUrl(app);
            var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            return new ParityBackendContext(
                "remote",
                musicRoot,
                outputDir,
                backend,
                stopAppAsync: () => app.StopAsync(),
                appDisposable: app,
                remoteBackend: backend,
                gate: gate);
        }

        public async Task<string> PrintRequestedOutputAsync(JobSummaryDto submitted, DownloadSettings settings)
        {
            using var output = new StringWriter();
            if (engine == null)
                await Sockseek.Cli.Program.PrintRemoteRequestedOutputAsync(Backend, submitted.WorkflowId, settings, Token, output);
            else
            {
                var root = engine.GetJob(submitted.JobId)
                    ?? throw new AssertFailedException("The completed local job is missing.");
                using (Printing.RedirectOutput(output))
                    Assert.IsTrue(PrintOutputRenderer.PrintRequestedOutput(new JobList("print", [root]),
                        workflowId => engine.GetUserSuccessCounts(workflowId)));
            }
            return output.ToString();
        }

        public string[] DownloadedRelativePaths()
            => Directory.GetFiles(OutputDir, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}failed{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.server-data{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(OutputDir, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (engine != null && engineTask != null)
                {
                    if (!engineCompleted)
                    {
                        try { engine.CompleteEnqueue(); }
                        catch (InvalidOperationException) { }
                        engineCompleted = true;
                    }

                    // Assertions have already observed the terminal workflow.
                    // Cancellation is the test harness's prompt teardown signal;
                    // waiting for the production idle loop adds seconds of
                    // contention across the parallel parity fixtures.
                    cts.Cancel();
                    try { await engineTask; }
                    catch (OperationCanceledException) { }
                }

                if (remoteBackend != null)
                    await remoteBackend.DisposeAsync();
                else if (Backend is IAsyncDisposable localBackend)
                    await localBackend.DisposeAsync();

                if (stopAppAsync != null)
                    await stopAppAsync();

                if (appDisposable != null)
                    await appDisposable.DisposeAsync();
            }
            finally
            {
                cts.Cancel();
                cts.Dispose();
                DeleteDirectory(MusicRoot);
                DeleteDirectory(OutputDir);
            }
        }

    }

    private static EngineSettings CreateEngineSettings(string musicRoot)
        => new()
        {
            MockFilesDir = musicRoot,
            MockFilesReadTags = false,
            LogLevel = LogLevel.None,
        };

    private static DownloadSettings CreateDownloadSettings(string outputDir)
        => new()
        {
            Output =
            {
                ParentDir = outputDir,
                NameFormat = "{foldername}/{filename}",
                IncompleteAlbumAction = { Kind = IncompleteAlbumActionKind.Move, Path = Path.Combine(outputDir, "failed") },
            },
            Search =
            {
                NoBrowseFolder = true,
                MinSharesAggregate = 1,
            },
        };

    private static Soulseek.SearchResponse SearchResponse(string username, string filename)
        => new(
            username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100,
            queueLength: 0,
            fileList:
            [
                new Soulseek.File(
                    1,
                    filename,
                    100,
                    Path.GetExtension(filename),
                    attributeList:
                    [
                        new Soulseek.FileAttribute(Soulseek.FileAttributeType.Length, 60),
                    ]),
            ]);

    private static Task WaitForJobStateAsync(
        ICliBackend backend,
        Guid jobId,
        ExpectedJobStatus expectedState,
        int timeoutMs = 5_000)
        => BackendTestWaiter.UntilAsync(
            backend,
            ct => backend.GetJobDetailAsync(jobId, ct),
            detail => detail?.Summary is { } summary && ProjectState(summary) == expectedState,
            $"Timed out waiting for job {jobId} to reach state '{expectedState}'.",
            detail => FormatState(detail?.Summary),
            timeoutMs);

    private static Task WaitForWorkflowStateAsync(
        ICliBackend backend,
        Guid workflowId,
        ServerWorkflowState expectedState,
        int timeoutMs = 5_000)
        => BackendTestWaiter.UntilAsync(
            backend,
            ct => backend.GetWorkflowAsync(workflowId, ct),
            detail => detail?.Summary.State == expectedState,
            $"Timed out waiting for workflow {workflowId} to reach state '{expectedState}'.",
            detail => detail?.Summary.State.ToString() ?? "<missing>",
            timeoutMs);

    private static async Task<JobSummaryDto> WaitForWorkflowJobAsync(
        ICliBackend backend, Guid workflowId, Func<JobSummaryDto, bool> predicate, int timeoutMs = 5000)
    {
        var jobs = await BackendTestWaiter.UntilAsync(
            backend,
            ct => backend.GetJobsAsync(
                new JobQuery(null, null, null, workflowId, IncludeAll: true),
                ct),
            items => items.Any(predicate),
            "Timed out waiting for matching workflow job.",
            items => string.Join(", ", items.Select(job =>
                $"[{job.DisplayId}] {job.Kind}:{ProjectState(job)}")),
            timeoutMs);
        return jobs.First(predicate);
    }

    private static Task WaitForConditionAsync(
        ICliBackend backend,
        Func<bool> condition,
        string failureMessage,
        int timeoutMs = 5_000)
        => BackendTestWaiter.UntilAsync(
            backend,
            _ => Task.FromResult(condition()),
            failureMessage,
            timeoutMs);

    private static ExpectedJobStatus ProjectState(JobSummaryDto summary)
        => ProjectState(summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason);

    private static ExpectedJobStatus ProjectState(
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity,
        ServerJobTerminalOutcome outcome,
        ServerJobSkipReason skipReason = ServerJobSkipReason.None)
        => lifecycle switch
        {
            ServerJobLifecycleState.Pending => ExpectedJobStatus.Pending,
            ServerJobLifecycleState.AwaitingSelection => ExpectedJobStatus.AwaitingSelection,
            ServerJobLifecycleState.Terminal => outcome switch
            {
                ServerJobTerminalOutcome.Succeeded => ExpectedJobStatus.Succeeded,
                ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.AlreadyExists => ExpectedJobStatus.AlreadyExists,
                ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.NotFoundLastTime => ExpectedJobStatus.NotFoundLastTime,
                ServerJobTerminalOutcome.Skipped => ExpectedJobStatus.Skipped,
                _ => ExpectedJobStatus.Failed,
            },
            _ => activity switch
            {
                ServerJobActivityPhase.Extracting => ExpectedJobStatus.Extracting,
                ServerJobActivityPhase.Downloading => ExpectedJobStatus.Downloading,
                ServerJobActivityPhase.RunningChildren => ExpectedJobStatus.RunningChildren,
                ServerJobActivityPhase.None => ExpectedJobStatus.RunningChildren,
                _ => ExpectedJobStatus.Searching,
            },
        };

    private static string FormatState(JobSummaryDto? summary)
        => summary == null ? "<missing>" : ProjectState(summary).ToString();

    private static string CreateTempDir(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static string GetBoundUrl(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        return addresses?.SingleOrDefault(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The test server did not publish its bound HTTP address.");
    }
}
