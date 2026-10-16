using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Services;
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
                        new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                    ctx.Token);
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
                    projections.Add(
                    [
                        $"{terminal.Kind}:{terminal.LifecycleState}:{terminal.ActivityPhase}:" +
                        $"{terminal.TerminalOutcome}:{terminal.ParentJobId.HasValue}",
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
    public async Task CliBackendParity_AutomaticSongModes_DownloadExactFiles(bool aggregate)
    {
        await RunForEachBackendAsync(
            seedMusic: musicRoot =>
            {
                string albumDir = Path.Combine(musicRoot, "Artist", "Album");
                Directory.CreateDirectory(albumDir);
                File.WriteAllText(Path.Combine(albumDir, "Artist - First.mp3"), "first");
                File.WriteAllText(Path.Combine(albumDir, "Artist - Second.mp3"), "second");
            },
            scenario: async ctx =>
            {
                var query = new SongQueryDto("Artist", aggregate ? "" : "First", "", "", -1, false);
                var summary = aggregate
                    ? await ctx.Backend.SubmitAggregateJobAsync(new SubmitAggregateJobRequestDto(query), ctx.Token)
                    : await ctx.Backend.SubmitSongJobAsync(new SubmitSongJobRequestDto(query), ctx.Token);

                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(aggregate
                    ? new[] { "Album/Artist - First.mp3", "Album/Artist - Second.mp3" }
                    : new[] { "Album/Artist - First.mp3" }, ctx.DownloadedRelativePaths(), ctx.Name);
            });
    }

    [TestMethod]
    public async Task CliBackendParity_ManualAlbumSelection_DownloadsSameFiles()
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
                    new StartFolderDownloadRequestDto(folders.Items[0].Ref),
                    ctx.Token);
                Assert.IsNotNull(download, ctx.Name);
                Assert.AreEqual(summary.JobId, download.JobId, ctx.Name);

                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "Album/01. Artist - Track One.mp3",
                        "Album/02. Artist - Track Two.mp3",
                    },
                    ctx.DownloadedRelativePaths(),
                    ctx.Name);
            });
    }

    [TestMethod]
    public async Task CliBackendParity_ManualAlbumAggregateSelection_DownloadsSameFiles()
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

                foreach (var bucket in aggregate.Items)
                {
                    Assert.IsNotNull(bucket.Folders, ctx.Name);
                    Assert.IsTrue(bucket.Folders.Count > 0, ctx.Name);

                    var download = await ctx.Backend.StartFolderDownloadAsync(
                        summary.JobId,
                        new StartFolderDownloadRequestDto(
                            bucket.Folders[0].Ref,
                            AlbumQuery: bucket.Query),
                        ctx.Token);
                    Assert.IsNotNull(download, ctx.Name);
                    Assert.AreEqual(summary.WorkflowId, download.WorkflowId, ctx.Name);
                    Assert.AreEqual(summary.JobId, download.SourceJobId, ctx.Name);

                    await WaitForJobStateAsync(ctx.Backend, download.JobId, ExpectedJobStatus.Succeeded);
                }

                Assert.IsTrue(await ctx.Backend.CompleteManualSelectionAsync(summary.JobId, ctx.Token), ctx.Name);
                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "Album One/01. Artist - First.mp3",
                        "Album Two/01. Artist - Second.mp3",
                        "Album Two/02. Artist - Third.mp3",
                    },
                    ctx.DownloadedRelativePaths(),
                    ctx.Name);
            });
    }

    [TestMethod]
    public async Task CliBackendParity_SearchFolderFollowUp_DownloadsSameFiles()
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
                Assert.AreEqual(2, folders.Items[0].Files?.Count, ctx.Name);

                var download = await ctx.Backend.StartFolderDownloadAsync(
                    search.JobId,
                    new StartFolderDownloadRequestDto(folders.Items[0].Ref, AlbumQuery: albumQuery),
                    ctx.Token);
                Assert.IsNotNull(download, ctx.Name);
                Assert.AreEqual(search.JobId, download.SourceJobId, ctx.Name);

                await WaitForJobStateAsync(ctx.Backend, download.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, search.WorkflowId, ServerWorkflowState.Completed);

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "Album/01. Artist - Track One.mp3",
                        "Album/02. Artist - Track Two.mp3",
                    },
                    ctx.DownloadedRelativePaths(),
                    ctx.Name);
            });
    }

    [TestMethod]
    public async Task CliBackendParity_TryNextCandidateByDisplayId_SkipsActiveSongCandidate()
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
                Assert.IsTrue(
                    await ctx.Backend.TryNextCandidateByDisplayIdAsync(summary.DisplayId, summary.WorkflowId, ctx.Token),
                    ctx.Name);

                await WaitForJobStateAsync(ctx.Backend, summary.JobId, ExpectedJobStatus.Succeeded);
                await WaitForWorkflowStateAsync(ctx.Backend, summary.WorkflowId, ServerWorkflowState.Completed);

                var detail = await ctx.Backend.GetJobDetailAsync(summary.JobId, ctx.Token);
                var payload = Assert.IsInstanceOfType<SongJobPayloadDto>(detail?.Payload);
                Assert.AreNotEqual(ctx.Gate.BlockedUsername, payload.ResolvedUsername, ctx.Name);
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
        Func<ParityBackendContext, Task> scenario)
    {
        var localClient = createClient();
        var remoteClient = createClient();
        await Task.WhenAll(
            RunAsync(() => ParityBackendContext.CreateLocalAsync(localClient.Client, localClient.Gate), scenario),
            RunAsync(() => ParityBackendContext.CreateRemoteAsync(remoteClient.Client, remoteClient.Gate), scenario));
    }

    private static async Task RunAsync(
        Func<Task<ParityBackendContext>> createContext,
        Func<ParityBackendContext, Task> scenario)
    {
        await using var context = await createContext();
        await scenario(context);
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
                profiles.DefaultProfile,
                profiles.AutoProfiles,
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

        public static Task<ParityBackendContext> CreateLocalAsync(MockSoulseekClient client, DownloadGate gate)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-local-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-local-out-");

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var downloadSettings = CreateDownloadSettings(outputDir);
            downloadSettings.Output.NameFormat = "{filename}";
            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings, client));
            var backend = new LocalCliBackend(engine, downloadSettings);
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

        public static async Task<ParityBackendContext> CreateRemoteAsync(MockSoulseekClient client, DownloadGate gate)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-remote-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-remote-out-");

            var downloadSettings = CreateDownloadSettings(outputDir);
            downloadSettings.Output.NameFormat = "{filename}";
            var app = TestServerHost.Build([], new ServerOptions
            {
                Engine = new EngineSettings
                {
                    Username = "test_user",
                    Password = "test_pass",
                    LogLevel = LogLevel.None,
                },
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
