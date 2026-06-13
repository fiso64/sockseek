using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Server;
using System.Net;
using System.Net.Sockets;
using Sockseek.Api;

namespace Tests.Cli;

[TestClass]
public class CliBackendParityTests
{
    [TestInitialize]
    public void Initialize()
    {
        SockseekLog.RemoveNonFileOutputs();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SockseekLog.RemoveNonFileOutputs();
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
                        DownloadBehavior: new DownloadBehaviorPolicyDto(Album: DownloadBehavior.Manual)),
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
                        DownloadBehavior: new DownloadBehaviorPolicyDto(AlbumAggregate: DownloadBehavior.Manual)),
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

    private static async Task RunForEachBackendAsync(Action<string> seedMusic, Func<ParityBackendContext, Task> scenario)
    {
        await using (var local = await ParityBackendContext.CreateLocalAsync(seedMusic))
            await scenario(local);

        await using (var remote = await ParityBackendContext.CreateRemoteAsync(seedMusic))
            await scenario(remote);
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
        }

        public string Name { get; }
        public string MusicRoot { get; }
        public string OutputDir { get; }
        public ICliBackend Backend { get; }
        public CancellationToken Token => cts.Token;

        public static Task<ParityBackendContext> CreateLocalAsync(Action<string> seedMusic)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-local-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-local-out-");
            seedMusic(musicRoot);

            var engineSettings = CreateEngineSettings(musicRoot);
            var downloadSettings = CreateDownloadSettings(outputDir);
            var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings));
            var backend = new LocalCliBackend(engine, downloadSettings);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var engineTask = engine.RunAsync(cts.Token);

            return Task.FromResult(new ParityBackendContext("local", musicRoot, outputDir, backend, engine, engineTask, cts: cts));
        }

        public static async Task<ParityBackendContext> CreateRemoteAsync(Action<string> seedMusic)
        {
            string musicRoot = CreateTempDir("Sockseek-cli-parity-remote-music-");
            string outputDir = CreateTempDir("Sockseek-cli-parity-remote-out-");
            seedMusic(musicRoot);

            int port = GetFreeTcpPort();
            string url = $"http://127.0.0.1:{port}";
            var app = ServerHost.Build([], new ServerOptions
            {
                Engine = CreateEngineSettings(musicRoot),
                DefaultDownload = CreateDownloadSettings(outputDir),
                Profiles = ProfileCatalog.Empty,
            }, url);

            await app.StartAsync();
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

        public string[] DownloadedRelativePaths()
            => Directory.GetFiles(OutputDir, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}failed{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
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

                    var completed = await Task.WhenAny(engineTask, Task.Delay(TimeSpan.FromSeconds(5)));
                    if (completed != engineTask)
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
        };

    private static DownloadSettings CreateDownloadSettings(string outputDir)
        => new()
        {
            Output =
            {
                ParentDir = outputDir,
                NameFormat = "{foldername}/{filename}",
                FailedAlbumPath = Path.Combine(outputDir, "failed"),
            },
            Search =
            {
                NoBrowseFolder = true,
                MinSharesAggregate = 1,
            },
        };

    private static async Task WaitForJobStateAsync(ICliBackend backend, Guid jobId, ExpectedJobStatus expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var detail = await backend.GetJobDetailAsync(jobId, CancellationToken.None);
            if (detail?.Summary is { } summary && ProjectState(summary) == expectedState)
                return;

            await Task.Delay(50, CancellationToken.None);
        }

        var finalDetail = await backend.GetJobDetailAsync(jobId, CancellationToken.None);
        Assert.Fail($"Timed out waiting for job {jobId} to reach state '{expectedState}'. Final state: {FormatState(finalDetail?.Summary)}.");
    }

    private static async Task WaitForWorkflowStateAsync(ICliBackend backend, Guid workflowId, ServerWorkflowState expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var detail = await backend.GetWorkflowAsync(workflowId, CancellationToken.None);
            if (detail?.Summary.State == expectedState)
                return;

            await Task.Delay(50, CancellationToken.None);
        }

        var finalDetail = await backend.GetWorkflowAsync(workflowId, CancellationToken.None);
        string jobs = finalDetail == null
            ? "<missing>"
            : string.Join(", ", finalDetail.Jobs.Select(job => $"[{job.DisplayId}] {job.Kind}:{ProjectState(job)} parent={job.ParentJobId?.ToString() ?? "-"} result={job.ResultJobId?.ToString() ?? "-"}"));
        Assert.Fail($"Timed out waiting for workflow {workflowId} to reach state '{expectedState}'. Jobs: {jobs}");
    }

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

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
