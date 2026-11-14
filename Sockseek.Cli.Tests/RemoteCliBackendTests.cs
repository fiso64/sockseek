using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Settings;
using Sockseek.Server;
using Tests.ClientTests;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Tests.Cli;

[TestClass]
public class RemoteCliBackendTests
{
    private const string DynamicLoopbackUrl = "http://127.0.0.1:0";
    private readonly List<string> serverDataDirectories = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (string directory in serverDataDirectories)
            await DeleteDirectoryIfExistsWithRetryAsync(directory);
    }

    private WebApplication BuildServer(
        ServerOptions options,
        string? url = null,
        bool enablePersistence = true)
    {
        options.Engine.LogLevel = LogLevel.None;
        options.Persistence.Enabled = enablePersistence;
        if (enablePersistence && options.Persistence.DataDirectory is null)
        {
            string dataDirectory = Path.Combine(
                Path.GetTempPath(),
                "sockseek-remote-cli-data",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDirectory);
            options.Persistence.DataDirectory = dataDirectory;
            serverDataDirectories.Add(dataDirectory);
        }
        return global::Tests.TestServerHost.Build([], options, url);
    }

    [TestMethod]
    public void NormalizeServerUrl_AcceptsHostOnlyAndDefaultsDaemonPort()
    {
        Assert.AreEqual(
            "http://127.0.0.1:5030/",
            SockseekApiClient.NormalizeServerUrl("127.0.0.1").ToString());
    }

    [TestMethod]
    public void NormalizeServerUrl_PreservesExplicitSchemeAndPort()
    {
        Assert.AreEqual(
            "http://127.0.0.1:6123/",
            SockseekApiClient.NormalizeServerUrl("http://127.0.0.1:6123").ToString());
        Assert.AreEqual(443, SockseekApiClient.NormalizeServerUrl("https://sockseek.example").Port);
        Assert.AreEqual(80, SockseekApiClient.NormalizeServerUrl("sockseek.example:80").Port);
        Assert.AreEqual(5030, SockseekApiClient.NormalizeServerUrl("[::1]").Port);
        Assert.AreEqual(80, SockseekApiClient.NormalizeServerUrl("[::1]:80").Port);
    }

    [TestMethod]
    public async Task SockseekApiClient_HttpErrorsUseSpecificExceptionType()
    {
        using var http = new HttpClient(new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"Bad request from daemon"}"""),
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5030/"),
        };
        var client = new SockseekApiClient(http);

        var ex = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(
            () => client.GetProfilesAsync(CancellationToken.None));

        StringAssert.Contains(ex.Message, "Bad request from daemon");
    }

    [TestMethod]
    public async Task SockseekLiveClient_InitialSnapshotFailureCanBeRetried()
    {
        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings(),
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, url);

        await app.StartAsync();
        url = GetBoundUrl(app);
        try
        {
            using var http = new HttpClient(new FailFirstDaemonSnapshotHandler())
            {
                BaseAddress = new Uri(url),
            };
            http.DefaultRequestHeaders.Add("X-API-Key", await TestServerHost.GetApiKeyAsync(app));
            await using var live = new SockseekLiveClient(http);

            await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(
                () => live.StartDaemonAsync());
            Assert.AreEqual(LiveSubscriptionMode.None, live.Mode);

            await live.StartDaemonAsync();

            Assert.AreEqual(LiveSubscriptionMode.Daemon, live.Mode);
            Assert.IsNotNull(live.Store.GetPosition(StateStreamScopeDto.Daemon));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [TestMethod]
    public async Task SockseekApiClient_TransferTimeline_ReadsBodyCursorAndCoverage()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"items\":[],\"nextCursor\":\"next-transfer-page\","
                + "\"retainedCoverage\":{\"state\":\"Unavailable\","
                + "\"reason\":\"PersistenceDisabled\"}}"),
        };
        var handler = new CapturingResponseHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030/") };
        var client = new SockseekApiClient(http);

        var page = await client.GetTransfersPageAsync(
            new TransferHistoryFilter(Username: "peer name", State: "Active"),
            cursor: "previous-page",
            limit: 25);

        Assert.AreEqual("next-transfer-page", page.NextCursor);
        Assert.AreEqual(0, page.Items.Count);
        Assert.AreEqual(
            TransferRetainedCoverageState.Unavailable,
            page.RetainedCoverage.State);
        StringAssert.Contains(handler.RequestUri!.Query, "username=peer%20name");
        StringAssert.Contains(handler.RequestUri.Query, "cursor=previous-page");
        StringAssert.Contains(handler.RequestUri.Query, "limit=25");
        StringAssert.Contains(handler.RequestUri.Query, "includeRemoved=false");
    }

    [TestMethod]
    public async Task SockseekApiClient_DashboardAnalytics_ReadsCoverageAndEscapesRange()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "accountingVersion": 1,
                  "range": {
                    "range": "24h",
                    "startUtc": "2026-08-30T00:00:00Z",
                    "endUtc": "2026-08-31T00:00:00Z",
                    "bucketSeconds": 1800,
                    "coverage": {
                      "state": "Available",
                      "completeFromUtc": "2026-08-01T00:00:00Z",
                      "isComplete": true
                    }
                  },
                  "bandwidth": [],
                  "summary": {
                    "downloadedBytes": 10,
                    "downloadedFiles": 1,
                    "uploadedBytes": 20,
                    "uploadedFiles": 2,
                    "distinctPeers": 3,
                    "shareRatio": 2
                  },
                  "downloadPeers": [],
                  "uploadPeers": [],
                  "content": [],
                  "errors": [],
                  "comparison": null
                }
                """),
        };
        var handler = new CapturingResponseHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030/") };
        var client = new SockseekApiClient(http);

        DashboardAnalyticsDto result = await client.GetDashboardAnalyticsAsync("24h");

        Assert.AreEqual(1, result.AccountingVersion);
        Assert.IsTrue(result.Range.Coverage.IsComplete);
        Assert.AreEqual(2d, result.Summary.ShareRatio);
        Assert.AreEqual("?range=24h", handler.RequestUri!.Query);
    }

    [TestMethod]
    public async Task SockseekApiClient_GetJobsAsync_FollowsEveryCursorPage()
    {
        Guid workflowId = Guid.NewGuid();
        var handler = new PagedJobsHandler(workflowId);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030/") };
        var client = new SockseekApiClient(http);

        var jobs = await client.GetJobsAsync(
            new JobQuery(null, null, null, workflowId, IncludeAll: true));

        Assert.AreEqual(201, jobs.Count);
        Assert.AreEqual(201, jobs.Select(job => job.JobId).Distinct().Count());
        Assert.AreEqual(ServerJobTerminalOutcome.Failed, jobs.Single(job => job.DisplayId == 201).TerminalOutcome);
        Assert.AreEqual(2, handler.RequestCount);
        StringAssert.Contains(handler.SecondRequestUri!.Query, "cursor=second-page");
        var page = await client.GetJobsPageAsync(new JobQuery(null, null, null, workflowId, IncludeAll: true));
        Assert.AreEqual(new StateStreamPositionDto(workflowId, 9_007_199_254_740_993), page.ReadPosition);
    }

    [TestMethod]
    public async Task SockseekApiClient_PersistenceIntegrity_UsesTypedOperationRoute()
    {
        var handler = new CapturingResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"isHealthy":true,"result":"ok"}"""),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030/") };
        var client = new SockseekApiClient(http);

        var result = await client.CheckPersistenceIntegrityAsync();

        Assert.IsTrue(result.IsHealthy);
        Assert.AreEqual("ok", result.Result);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/api/persistence/integrity", handler.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task RemoteCliBackend_SearchProjectionAndDownloadFollowUp_Work()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.mp3"), "a");

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            var seenUpdates = new ConcurrentBag<DaemonClientUpdate>();
            backend.StateUpdated += update => seenUpdates.Add(update);
            await backend.StartAsync();

            var searchSummary = await backend.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)));

            await WaitForJobStateAsync(backend, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var projection = await backend.GetFileResultsAsync(searchSummary.JobId);
            Assert.IsNotNull(projection);
            Assert.AreEqual(1, projection.Items.Count);

            var downloadSummary = await backend.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto(
                    [projection.Items[0].Ref],
                    new SubmissionOptionsDto(OutputParentDir: outputDir)));

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(1, downloadSummary.Count);
            var downloadedSummary = downloadSummary[0];
            Assert.AreEqual(searchSummary.WorkflowId, downloadedSummary.WorkflowId);
            Assert.IsNull(downloadedSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadedSummary.SourceJobId);

            await WaitForJobStateAsync(backend, downloadedSummary.JobId, ExpectedJobStatus.Succeeded);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray();
            CollectionAssert.Contains(downloaded, "01. Artist - Track One.mp3");

            await WaitForConditionAsync(
                backend,
                () => Task.FromResult(seenUpdates.Any(update => update.ChangedTransfers.Any())),
                "Timed out waiting for typed transfer state.");
            Assert.IsTrue(seenUpdates.Any(update => update.ChangedJobs.Any(job => job.JobId == searchSummary.JobId)));
            Assert.IsTrue(seenUpdates.Any(update => update.State.Searches.Any(search => search.JobId == searchSummary.JobId)));
            Assert.IsTrue(seenUpdates.Any(update => update.ChangedTransfers.Any(
                transfer => transfer.Identity.JobId == downloadedSummary.JobId)));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_GenericSearchProjectsFilesAndFolders()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-generic-search-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-generic-search-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var searchSummary = await backend.SubmitSearchJobAsync(new SubmitSearchJobRequestDto("Artist Album"));
            await WaitForJobStateAsync(backend, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var files = await backend.GetFileResultsAsync(
                searchSummary.JobId,
                new FileSearchProjectionRequestDto(new SongQueryDto("Artist", "Track One", "", "", -1, false)));
            Assert.IsNotNull(files);
            Assert.IsTrue(files.Items.Any(file => file.Filename.EndsWith("01. Artist - Track One.mp3", StringComparison.Ordinal)));

            var albumQuery = new AlbumQueryDto("Artist", "Album", "", "", false);
            var folders = await backend.GetFolderResultsAsync(
                searchSummary.JobId,
                new FolderSearchProjectionRequestDto(albumQuery, IncludeFiles: true));
            Assert.IsNotNull(folders);
            Assert.AreEqual(1, folders!.Items.Count);
            Assert.AreEqual(2, folders.Items[0].Files!.Count);

            RetrieveFolderJobPayloadDto? retrieval = await backend.RetrieveFolderAndWaitAsync(
                searchSummary.JobId,
                new RetrieveFolderRequestDto(folders.Items[0].Ref, albumQuery));
            Assert.IsNotNull(retrieval);
            Assert.AreEqual(
                ServerFolderRetrievalOutcome.Completed,
                retrieval.RetrievalOutcome);

            // A daemon restart discards temporary Views. A retained directory
            // handle must fail explicitly rather than silently recreate a View.
            app.Services.GetRequiredService<Sockseek.Server.Planning.SearchViewCoordinator>()
                .Cache.Clear();
            var expired = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.StartRetrieveFolderAsync(
                searchSummary.JobId, new RetrieveFolderRequestDto(folders.Items[0].Ref, albumQuery)));
            StringAssert.Contains(expired.Message, "Request the results again");
            var reread = await backend.GetFolderResultsAsync(searchSummary.JobId,
                new FolderSearchProjectionRequestDto(albumQuery, IncludeFiles: true));
            Assert.IsNotNull(reread);
            Assert.AreEqual(2, reread.Items[0].Files!.Count);

            var downloadSummary = await backend.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(folders.Items[0].Ref, AlbumQuery: albumQuery));
            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.SourceJobId);

            await WaitForJobStateAsync(backend, downloadSummary.JobId, ExpectedJobStatus.Succeeded);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Artist - Track One.mp3", "02. Artist - Track Two.mp3" }, downloaded);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_SubmitExtract_UsesClientDownloadSettingsPatch()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-album-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-album-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var workflowId = Guid.NewGuid();
            var summary = await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    "Artist Album",
                    "String",
                    Options: new SubmissionOptionsDto(
                        WorkflowId: workflowId,
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch(["-a", "--no-browse-folder"])))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Track One.mp3", "02. Track Two.mp3" }, downloaded);

            using var output = new StringWriter();
            await Sockseek.Cli.Program.PrintRemoteCompleteAsync(
                backend,
                summary.WorkflowId,
                CancellationToken.None,
                output);
            Assert.AreEqual(
                string.Empty,
                output.ToString(),
                "A single successful user-facing album completion is intentionally not summarized.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveCliCoordinator_FromListSerializesPromptsAndDownloadsSelections()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-" + Guid.NewGuid() + ".txt");
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-out-" + Guid.NewGuid());
        string albumOneDir = Path.Combine(musicRoot, "Artist One", "Album One");
        string albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Album Two");
        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumOneDir, "01. Artist One - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumTwoDir, "01. Artist Two - Track Two.mp3"), "b");
        File.WriteAllLines(inputPath, ["a:\"Artist One - Album One\"", "a:\"Artist Two - Album Two\""]);

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = new ProfileCatalog
            {
                AutoProfiles =
                [
                    new SettingsProfile
                    {
                        Name = "interactive-context",
                        Condition = "interactive",
                    },
                ],
            },
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            int activePickers = 0;
            int maxActivePickers = 0;
            int pickerCalls = 0;
            Guid workflowId = Guid.NewGuid();
            var bothAlbumsReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveAlbumReadiness(DaemonClientUpdate _)
            {
                if (backend.ClientStore.GetWorkflowJobs(workflowId).Count(job =>
                    job.Kind == ServerJobKind.Album
                    && job.LifecycleState == ServerJobLifecycleState.AwaitingSelection) >= 2)
                    bothAlbumsReady.TrySetResult();
            }
            backend.StateUpdated += ObserveAlbumReadiness;
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                CancellationToken.None,
                async request =>
                {
                    var active = Interlocked.Increment(ref activePickers);
                    int observed;
                    do
                    {
                        observed = maxActivePickers;
                        if (active <= observed) break;
                    }
                    while (Interlocked.CompareExchange(ref maxActivePickers, active, observed) != observed);

                    try
                    {
                        int pickerCall = Interlocked.Increment(ref pickerCalls);
                        if (pickerCall == 1)
                            await bothAlbumsReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        var folder = request.Folders.First();
                        return new InteractiveModeManager.RunResult(
                            InteractiveModeManager.RunAction.Accept,
                            0,
                            folder,
                            RetrieveCurrentFolder: true,
                            request.FilterStr);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activePickers);
                    }
                },
                pollInterval: TimeSpan.FromMilliseconds(10));

            var summary = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    inputPath,
                    "List",
                    Options: new SubmissionOptionsDto(
                        WorkflowId: workflowId,
                        OutputParentDir: outputDir,
                        ProfileContext: new Dictionary<string, bool> { ["interactive"] = true },
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch([inputPath, "--input-type", "list", "--no-browse-folder"])))),
                CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await coordinator.RunUntilCompleteAsync(summary.WorkflowId, timeout.Token);

            Assert.AreEqual(2, pickerCalls, "Both extracted album searches should reach the interactive picker.");
            Assert.AreEqual(1, maxActivePickers, "Remote interactive album prompts must not overlap.");
            backend.StateUpdated -= ObserveAlbumReadiness;

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Artist One - Track One.mp3", "01. Artist Two - Track Two.mp3" }, downloaded);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveCliCoordinator_FromListPreservesLineConditionsBeforePrompt()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-cond-" + Guid.NewGuid() + ".txt");
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-cond-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-cond-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Album Artist", "Album Name");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. file1.mp3"), "a");
        File.WriteAllLines(inputPath, ["a:\"Album Name\"                 strict-album=true;format=flac"]);

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            int pickerCalls = 0;
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                CancellationToken.None,
                request =>
                {
                    Interlocked.Increment(ref pickerCalls);
                    var folder = request.Folders.FirstOrDefault();
                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        folder == null
                            ? InteractiveModeManager.RunAction.SkipCurrent
                            : InteractiveModeManager.RunAction.Accept,
                        folder == null ? -1 : 0,
                        folder,
                        RetrieveCurrentFolder: true,
                        request.FilterStr));
                },
                pollInterval: TimeSpan.FromMilliseconds(10));

            var summary = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    inputPath,
                    "List",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch([inputPath, "--input-type", "list", "--no-browse-folder"])))),
                CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await coordinator.RunUntilCompleteAsync(summary.WorkflowId, timeout.Token);

            Assert.AreEqual(0, pickerCalls, "The MP3 folder must be filtered out by the list-line FLAC condition before prompting.");
            Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Count(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task InteractiveCliCoordinator_AlbumGroupsPromptsEachBucket()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-agg-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-interactive-agg-out-" + Guid.NewGuid());
        string timeDir = Path.Combine(musicRoot, "ELO", "Time");
        string discoveryDir = Path.Combine(musicRoot, "ELO", "Discovery");
        Directory.CreateDirectory(timeDir);
        Directory.CreateDirectory(discoveryDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(timeDir, "01. ELO - Prologue.mp3"), "a");
        File.WriteAllText(Path.Combine(timeDir, "02. ELO - Twilight.mp3"), "b");
        File.WriteAllText(Path.Combine(discoveryDir, "01. ELO - Shine a Little Love.mp3"), "c");
        File.WriteAllText(Path.Combine(discoveryDir, "02. ELO - Confusion.mp3"), "d");
        File.WriteAllText(Path.Combine(discoveryDir, "03. ELO - Last Train to London.mp3"), "e");

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                    MinGroupShares = 1,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var promptedBuckets = new List<string>();
            var coordinator = new InteractiveCliCoordinator(
                backend,
                new CliSettings { InteractiveMode = true, NoProgress = true },
                CancellationToken.None,
                request =>
                {
                    promptedBuckets.Add(request.PromptJob.ToString(noInfo: true));
                    var folder = request.Folders.First();
                    return Task.FromResult(new InteractiveModeManager.RunResult(
                        InteractiveModeManager.RunAction.Accept,
                        0,
                        folder,
                        RetrieveCurrentFolder: true,
                        request.FilterStr));
                },
                pollInterval: TimeSpan.FromMilliseconds(10));

            var summary = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    "ELO",
                    "String",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        ProfileContext: new Dictionary<string, bool> { ["interactive"] = true },
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch(["ELO", "--album", "--group", "--min-group-shares", "1", "--no-browse-folder"])))),
                CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await coordinator.RunUntilCompleteAsync(summary.WorkflowId, timeout.Token);

            Assert.AreEqual(2, promptedBuckets.Count, "Each album-groups bucket should reach the interactive picker.");
            Assert.IsTrue(promptedBuckets.Any(x => x.Contains("Time", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(promptedBuckets.Any(x => x.Contains("Discovery", StringComparison.OrdinalIgnoreCase)));

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "01. ELO - Prologue.mp3",
                "01. ELO - Shine a Little Love.mp3",
                "02. ELO - Confusion.mp3",
                "02. ELO - Twilight.mp3",
                "03. ELO - Last Train to London.mp3",
            }, downloaded);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_PrintCompleteCountsCancelledAlbumAsUserFacingFailure()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-cancel-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-cancel-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
            File.WriteAllBytes(Path.Combine(albumDir, $"{i:00}. Artist - Track {i:00}.mp3"), new byte[1024]);

        var client = CreateBlockedDownloadClient(musicRoot);
        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            ClientFactory = _ => client,
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var searchSummary = await backend.SubmitAlbumSearchJobAsync(
            new SubmitAlbumSearchJobRequestDto(
                new AlbumQueryDto("Artist", "Album", "", "", false)));

            await WaitForJobStateAsync(backend, searchSummary.JobId, ExpectedJobStatus.Succeeded);
            await WaitForConditionAsync(
                backend,
                () => Task.FromResult(
                    backend.ClientStore.GetWorkflowJobs(searchSummary.WorkflowId).Count == 0),
                "Timed out waiting for the search workflow to retire from live state.");
            SearchResultSnapshotDto<AlbumFolderDto>? projection =
                await backend.GetFolderResultsAsync(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(projection);
            Assert.IsTrue(projection.IsComplete);
            Assert.AreEqual("Complete", projection.PersistenceState);
            Assert.AreEqual(1, projection.Items.Count);

            var downloadSummary = await backend.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(projection.Items[0].Ref));

            Assert.IsNotNull(downloadSummary);

            await WaitForConditionAsync(
                backend,
                async ct =>
                {
                    return (await GetChildSongPayloadsAsync(backend, downloadSummary.JobId, ct))
                        .Any(file => ProjectState(file) == ExpectedJobStatus.Downloading) == true;
                },
                "Timed out waiting for remote album file downloads to start.");

            Assert.IsTrue(await backend.CancelWorkflowAsync(downloadSummary.WorkflowId) > 0);
            await WaitForJobStateAsync(backend, downloadSummary.JobId, ExpectedJobStatus.Failed);
            await WaitForConditionAsync(
                backend,
                () => Task.FromResult(
                    backend.ClientStore.GetWorkflowJobs(downloadSummary.WorkflowId).Count == 0),
                "Timed out waiting for the cancelled album workflow to retire from live state.");
            var retained = await backend.GetJobsAsync(new JobQuery(
                null, null, null, downloadSummary.WorkflowId, IncludeAll: true));
            Assert.IsTrue(retained.Any(job =>
                job.JobId == downloadSummary.JobId
                && job.LifecycleState == ServerJobLifecycleState.Terminal
                && job.TerminalOutcome == ServerJobTerminalOutcome.Cancelled));

            using var output = new StringWriter();
            await Sockseek.Cli.Program.PrintRemoteCompleteAsync(
                backend,
                downloadSummary.WorkflowId,
                CancellationToken.None,
                output);

            string rendered = output.ToString();
            StringAssert.Contains(
                rendered,
                "Completed: 0 succeeded, 1 failed.",
                "Remote completion output should match the live renderer's user-facing job counts.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_CancelJobByDisplayId_WhenScopedToWorkflow_DoesNotCancelOtherWorkflow()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-scoped-cancel-music-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-scoped-cancel-out-" + Guid.NewGuid());
        string albumOneDir = Path.Combine(musicRoot, "Artist One", "Album One");
        string albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Album Two");
        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
        {
            File.WriteAllBytes(Path.Combine(albumOneDir, $"{i:00}. Artist One - Track {i:00}.mp3"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(albumTwoDir, $"{i:00}. Artist Two - Track {i:00}.mp3"), new byte[1024]);
        }

        var client = CreateBlockedDownloadClient(musicRoot);
        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            ClientFactory = _ => client,
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var firstSearch = await StartAlbumSearchAsync(backend, "Artist One", "Album One");
            var secondSearch = await StartAlbumSearchAsync(backend, "Artist Two", "Album Two");
            await WaitForJobStateAsync(backend, firstSearch.JobId, ExpectedJobStatus.Succeeded);
            await WaitForJobStateAsync(backend, secondSearch.JobId, ExpectedJobStatus.Succeeded);

            var firstDownload = await StartFirstAlbumDownloadAsync(backend, firstSearch.JobId);
            var secondDownload = await StartFirstAlbumDownloadAsync(backend, secondSearch.JobId);

            await WaitForAlbumFileDownloadToStartAsync(backend, firstDownload.JobId);
            await WaitForAlbumFileDownloadToStartAsync(backend, secondDownload.JobId);

            Assert.IsFalse(
                await backend.CancelJobByDisplayIdAsync(secondDownload.DisplayId, firstDownload.WorkflowId),
                "A remote CLI scoped to one workflow must not cancel another workflow's display id.");

            Assert.IsTrue(await backend.CancelJobByDisplayIdAsync(firstDownload.DisplayId, firstDownload.WorkflowId));
            await WaitForJobStateAsync(backend, firstDownload.JobId, ExpectedJobStatus.Failed);

            var secondDetail = await backend.GetJobDetailAsync(secondDownload.JobId);
            var secondState = secondDetail?.Summary is { } summary
                ? ProjectState(summary)
                : (ExpectedJobStatus?)null;
            Assert.AreNotEqual<ExpectedJobStatus?>(
                ExpectedJobStatus.Failed,
                secondState,
                "Cancelling the first workflow by display id must not fail the second workflow's job.");

            Assert.IsTrue(await backend.CancelWorkflowAsync(secondDownload.WorkflowId) > 0);
            await WaitForJobStateAsync(backend, secondDownload.JobId, ExpectedJobStatus.Failed);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    [DataRow("song", "Artist - Track One")]
    [DataRow("album", "Artist - Album")]
    [DataRow("song-groups", "Artist")]
    [DataRow("album-groups", "Artist")]
    public async Task RemoteCliBackend_ResultsFullRendersSharedConditionFactsWithoutDownloading(string mode, string query)
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.flac"), "b");
        File.WriteAllText(Path.Combine(trackDir, "02. Artist - Track Two.mp3"), "c");
        const string lockedName = @"Private\Artist\Album\01. Artist - Track One locked.mp3";
        var client = new MockSoulseekClient(
        [
            new Soulseek.SearchResponse("public", 1, true, 1000, 0,
                Directory.GetFiles(trackDir).Select(path =>
                    new Soulseek.File(1, path, 1, Path.GetExtension(path)))),
            new Soulseek.SearchResponse("private", 2, true, 1000, 0, [],
                lockedFileList: [new Soulseek.File(1, lockedName, 1, ".mp3")]),
        ]);

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            ClientFactory = _ => client,
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
                Search = { PreferredCond = new Sockseek.Core.Models.FileConditions { Formats = ["flac"] } },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var printSettings = new DownloadSettings
            {
                PrintOption = PrintOption.Results | PrintOption.Full,
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            };

            var args = new List<string> { query, "--print-results-full", "--min-group-shares", "1" };
            if (mode == "song") args.Add("--song");
            if (mode is "album" or "album-groups") args.Add("--album");
            if (mode is "song-groups" or "album-groups") args.Add("--group");
            var summary = await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    query,
                    "String",
                    Options: new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch(args.ToArray())))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);

            using var output = new StringWriter();
            await Sockseek.Cli.Program.PrintRemoteRequestedOutputAsync(
                backend,
                summary.WorkflowId,
                printSettings,
                CancellationToken.None,
                output);

            var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            await supervisor.WaitForSubmissionExecutionAsync(summary.SubmissionId!.Value, CancellationToken.None);
            var persistence = app.Services.GetRequiredService<Sockseek.Server.Persistence.PersistenceCoordinator>();
            Assert.IsNull(await persistence.Submissions!.GetSpecificationJsonAsync(summary.SubmissionId.Value));
            Assert.IsNull(await persistence.JobHistory!.GetJobAsync(summary.JobId));
            Assert.IsNull(await persistence.SearchHistory!.GetMetadataAsync(summary.JobId));
            Assert.HasCount(0, await backend.GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true)),
                "Transient print output must not appear in the ordinary Jobs list.");
            string rendered = output.ToString();
            if (mode == "song") StringAssert.Contains(rendered, "Results for Artist - Track One");
            StringAssert.Contains(rendered, "Artist - Track One.mp3");
            StringAssert.Contains(rendered, "Artist - Track One.flac");
            StringAssert.Contains(rendered, "Artist - Track One locked.mp3");
            StringAssert.Contains(rendered, "[locked]");
            int expectedFiles = mode == "song" ? 3 : 4;
            Assert.AreEqual(expectedFiles, rendered.Split("(nec:Satisfied", StringSplitOptions.None).Length - 1, rendered);
            Assert.AreEqual(expectedFiles - 1, rendered.Split("Format fails", StringSplitOptions.None).Length - 1, rendered);
            if (mode == "song")
                Assert.IsFalse(rendered.Contains("Track Two", StringComparison.Ordinal), rendered);
            else
                StringAssert.Contains(rendered, "Artist - Track Two.mp3");
            Assert.IsTrue(rendered.IndexOf("Track One.flac", StringComparison.Ordinal)
                < rendered.IndexOf("Track One.mp3", StringComparison.Ordinal),
                "Preferred track alternatives must precede fallback formats.");
            Assert.IsFalse(rendered.Contains("conditions:unavailable", StringComparison.Ordinal));
            Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                "Remote print-results mode should not download files.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_PrintJobs_WaitsForCompleteRetainedGraphAndRendersInputJobs()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-jobs-" + Guid.NewGuid() + ".txt");
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-print-jobs-out-" + Guid.NewGuid());
        Directory.CreateDirectory(outputDir);
        string existingAlbumDir = Path.Combine(outputDir, "Artist Two", "Album Two");
        Directory.CreateDirectory(existingAlbumDir);
        File.WriteAllText(Path.Combine(outputDir, "Artist One - Track One.mp3"), "already here");
        File.WriteAllText(Path.Combine(existingAlbumDir, "01. Artist Two - Album Track.mp3"), "already here");
        File.WriteAllLines(inputPath, ["s:\"Artist One - Track One\"", "a:\"Artist Two - Album Two\""]);

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                LogLevel = LogLevel.None,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
                Skip =
                {
                    SkipMode = SkipMode.Name,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var printSettings = new DownloadSettings
            {
                PrintOption = PrintOption.Jobs,
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            };

            Guid summaryWorkflowId = Guid.NewGuid();
            var terminalJobs = new TaskCompletionSource<Guid[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            backend.StateUpdated += update =>
            {
                if (update.Status == DaemonClientApplyStatus.Applied
                    && update.ChangedWorkflows.Any(workflow =>
                        workflow.WorkflowId == summaryWorkflowId
                        && workflow.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed))
                {
                    terminalJobs.TrySetResult(
                        backend.ClientStore.GetWorkflowJobs(summaryWorkflowId)
                            .Select(job => job.JobId)
                            .ToArray());
                }
            };

            var summary = await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    inputPath,
                    "List",
                    Options: new SubmissionOptionsDto(
                        WorkflowId: summaryWorkflowId,
                        OutputParentDir: outputDir,
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch([inputPath, "--input-type", "list", "--print", "jobs"])))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);
            Guid[] expectedJobIds = await terminalJobs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var output = new StringWriter();
            await Sockseek.Cli.Program.PrintRemoteRequestedOutputAsync(
                backend,
                summary.WorkflowId,
                printSettings,
                CancellationToken.None,
                output,
                expectedJobIds);

            string rendered = output.ToString();
            StringAssert.Contains(rendered, "2 jobs:");
            StringAssert.Contains(rendered, "Song: Artist One - Track One");
            StringAssert.Contains(rendered, "Album: Artist Two - Album Two");
            Assert.IsFalse(rendered.Contains("already exist", StringComparison.OrdinalIgnoreCase), rendered);
            Assert.AreEqual(2, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                "Remote print-jobs mode should not download files.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemotePrintJobs_UsesSharedExecutionAndReleasesTransientOutput()
    {
        string inputPath = Path.Combine(
            Path.GetTempPath(),
            "Sockseek-remote-preview-" + Guid.NewGuid() + ".csv");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                "artist,title",
                .. Enumerable.Range(0, 205).Select(index => $"Artist {index},Track {index}"),
            ]);

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings { LogLevel = LogLevel.None },
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, url, enablePersistence: false);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();
            using var output = new StringWriter();

            var summary = await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(inputPath, InputType.CSV.ToString(),
                    Options: new SubmissionOptionsDto(
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(ConfigManager.CreateCliDownloadSettingsPatch(
                            [inputPath, "--input-type", "csv", "--print", "jobs-full"])))));
            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);
            await Sockseek.Cli.Program.PrintRemoteRequestedOutputAsync(backend, summary.WorkflowId,
                new DownloadSettings { PrintOption = PrintOption.Jobs | PrintOption.Full },
                CancellationToken.None, output);
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var api = new SockseekApiClient(http);
            var released = await api.DeleteSubmissionAsync(summary.SubmissionId!.Value);
            Assert.IsTrue(released.AffectedJobCount > 205);
            string rendered = output.ToString();
            StringAssert.Contains(rendered, "205 jobs:");
            StringAssert.Contains(rendered, "Artist:             Artist 0");
            StringAssert.Contains(rendered, "Title:              Track 204");
            Assert.HasCount(0, await backend.GetJobsAsync(
                new JobQuery(null, null, null, null, IncludeAll: true)));
            Assert.IsTrue(File.Exists(inputPath),
                "Remote artifacts are immutable and must not mutate the client-owned CSV.");
        }
        finally
        {
            await app.StopAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }


    [TestMethod]
    public async Task RemoteCliBackend_DaemonSubscription_ObservesMultipleSubmittedWorkflows()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-monitor-multi-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var monitor = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            var scopes = new ConcurrentBag<StateStreamScopeDto>();
            monitor.StateUpdated += update => scopes.Add(update.Scope);
            await monitor.SubscribeAllAsync();

            var firstWorkflowId = Guid.NewGuid();
            var secondWorkflowId = Guid.NewGuid();
            var manual = new DownloadBehaviorPolicyDto(Album: ServerDownloadBehavior.Manual);
            var first = await monitor.SubmitAlbumJobAsync(new SubmitAlbumJobRequestDto(
                new AlbumQueryDto("Artist", "Album", "", "", false),
                new SubmissionOptionsDto(firstWorkflowId),
                manual));
            var second = await monitor.SubmitAlbumJobAsync(new SubmitAlbumJobRequestDto(
                new AlbumQueryDto("Artist", "Album", "", "", false),
                new SubmissionOptionsDto(secondWorkflowId),
                manual));

            await WaitForConditionAsync(
                monitor,
                () => Task.FromResult(
                    monitor.ClientStore.GetWorkflowJobs(first.WorkflowId)
                        .Any(job => job.LifecycleState == ServerJobLifecycleState.AwaitingSelection)
                    && monitor.ClientStore.GetWorkflowJobs(second.WorkflowId)
                        .Any(job => job.LifecycleState == ServerJobLifecycleState.AwaitingSelection)),
                "Daemon monitor did not observe both active workflows.");

            Assert.IsTrue(scopes.Count > 0);
            Assert.IsTrue(scopes.All(scope => scope.Kind == StateStreamScopeKind.Daemon));
        }
        finally
        {
            await app.StopAsync();
            await DeleteDirectoryIfExistsWithRetryAsync(musicRoot);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_DaemonAndWorkflowSubscriptionsCannotMix()
    {
        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings(),
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, url);

        await app.StartAsync();
        url = GetBoundUrl(app);
        try
        {
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.SubscribeAllAsync();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => backend.SubscribeWorkflowAsync(Guid.NewGuid()));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_ReconnectsWithSubscribeSnapshotBufferedDeltaHandoff()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-reconnect-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        string url = DynamicLoopbackUrl;

        var options = new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        };
        var firstApp = BuildServer(options, url);
        await firstApp.StartAsync();
        url = GetBoundUrl(firstApp);

        try
        {
            await using var monitor = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(firstApp));
            await monitor.SubscribeAllAsync();
            var firstPosition = monitor.ClientStore.GetPosition(StateStreamScopeDto.Daemon);
            Assert.IsNotNull(firstPosition);

            // A reconnect can arrive after SignalR closes its existing connections
            // but before Kestrel stops accepting requests. Reject that late connection
            // instead of keeping the old host alive until its shutdown timeout.
            firstApp.Lifetime.StopApplication();
            await using (var lateClient = new RemoteCliBackend(
                await global::Tests.TestServerHost.CreateHttpClientAsync(firstApp)))
            {
                var rejected = await Assert.ThrowsAsync<HttpRequestException>(() => lateClient.SubscribeAllAsync());
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            }
            await firstApp.StopAsync();

            await using var secondApp = BuildServer(options, url);
            await secondApp.StartAsync();
            try
            {
                var workflowId = Guid.NewGuid();
                var submitted = await monitor.SubmitAlbumJobAsync(new SubmitAlbumJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false),
                    new SubmissionOptionsDto(workflowId),
                    new DownloadBehaviorPolicyDto(Album: ServerDownloadBehavior.Manual)));

                await WaitForConditionAsync(
                    monitor,
                    () => Task.FromResult(
                        monitor.ClientStore.GetPosition(StateStreamScopeDto.Daemon) is { } position
                        && position.Epoch != firstPosition.Epoch
                        && monitor.ClientStore.GetJob(submitted.JobId)?.LifecycleState
                            == ServerJobLifecycleState.AwaitingSelection),
                    "Monitor did not recover the restarted daemon epoch and active workflow.",
                    timeoutMs: 15000);
            }
            finally
            {
                await secondApp.StopAsync();
            }
        }
        finally
        {
            try
            {
                await firstApp.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            await DeleteDirectoryIfExistsWithRetryAsync(musicRoot);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Program_MonitorWithoutInput_StaysAttachedUntilCancelled()
    {
        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings(),
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, url);

        await app.StartAsync();
        url = GetBoundUrl(app);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var monitorTask = Sockseek.Cli.Program.MainCore(
                ["--no-config", "--remote", url, "--remote-api-key", await TestServerHost.GetApiKeyAsync(app), "--monitor", "--no-progress"],
                cancellation.Token);

            await WaitForConsoleHandlersAsync(
                () => ConsoleInputManager.OnCancelRequested != null,
                "Monitor mode did not finish attaching.");
            Assert.IsFalse(
                monitorTask.IsCompleted,
                "Monitor mode must remain attached when no input is supplied.");

            await cancellation.CancelAsync();
            var exit = await monitorTask;
            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Cancelled, exit);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Program_MonitorControlsOperateAcrossDaemonWorkflows()
    {
        string musicRoot = Path.Combine(
            Path.GetTempPath(),
            "Sockseek-monitor-controls-" + Guid.NewGuid());
        string outputDir = Path.Combine(
            Path.GetTempPath(),
            "Sockseek-monitor-controls-out-" + Guid.NewGuid());
        string albumOneDir = Path.Combine(musicRoot, "Artist One", "Album One");
        string albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Album Two");
        Directory.CreateDirectory(albumOneDir);
        Directory.CreateDirectory(albumTwoDir);
        Directory.CreateDirectory(outputDir);
        for (int i = 1; i <= 12; i++)
        {
            File.WriteAllBytes(
                Path.Combine(albumOneDir, $"{i:00}. Artist One - Track {i:00}.mp3"),
                new byte[1024]);
            File.WriteAllBytes(
                Path.Combine(albumTwoDir, $"{i:00}. Artist Two - Track {i:00}.mp3"),
                new byte[1024]);
        }

        var client = CreateBlockedDownloadClient(musicRoot);
        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            ClientFactory = _ => client,
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{foldername}/{filename}",
                },
                Search =
                {
                    NoBrowseFolder = true,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        var originalIn = Console.In;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.StartAsync();
        url = GetBoundUrl(app);
        try
        {
            var monitorTask = Sockseek.Cli.Program.MainCore(
                ["--no-config", "--remote", url, "--remote-api-key", await TestServerHost.GetApiKeyAsync(app), "--monitor", "--no-progress"],
                cancellation.Token);

            await WaitForConsoleHandlersAsync(
                () =>
                    ConsoleInputManager.OnCancelRequested != null
                    && ConsoleInputManager.OnNextCandidateRequested != null
                    && ConsoleInputManager.OnInfoRequested != null,
                "Monitor mode did not install the normal console controls.");

            await using var controller = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            var firstSearch = await StartAlbumSearchAsync(
                controller,
                "Artist One",
                "Album One");
            var secondSearch = await StartAlbumSearchAsync(
                controller,
                "Artist Two",
                "Album Two");
            await WaitForJobStateAsync(
                controller,
                firstSearch.JobId,
                ExpectedJobStatus.Succeeded);
            await WaitForJobStateAsync(
                controller,
                secondSearch.JobId,
                ExpectedJobStatus.Succeeded);

            var first = await StartFirstAlbumDownloadAsync(
                controller,
                firstSearch.JobId);
            var second = await StartFirstAlbumDownloadAsync(
                controller,
                secondSearch.JobId);
            await WaitForAlbumFileDownloadToStartAsync(controller, first.JobId);
            await WaitForAlbumFileDownloadToStartAsync(controller, second.JobId);

            Console.SetIn(new StringReader(first.DisplayId + Environment.NewLine));
            await ConsoleInputManager.OnCancelRequested!();
            await WaitForJobStateAsync(
                controller,
                first.JobId,
                ExpectedJobStatus.Failed);
            var secondBeforeCancelAll = await controller.GetJobDetailAsync(second.JobId);
            Assert.AreNotEqual(
                ServerJobLifecycleState.Terminal,
                secondBeforeCancelAll?.Summary.LifecycleState,
                "Cancelling one daemon job by display id must not cancel another workflow.");

            Console.SetIn(new StringReader("all" + Environment.NewLine));
            await ConsoleInputManager.OnCancelRequested!();
            await WaitForJobStateAsync(
                controller,
                second.JobId,
                ExpectedJobStatus.Failed);

            var afterCancelAll = await StartAlbumSearchAsync(
                controller,
                "Artist One",
                "Album One");
            await WaitForJobStateAsync(
                controller,
                afterCancelAll.JobId,
                ExpectedJobStatus.Succeeded);

            await cancellation.CancelAsync();
            Assert.AreEqual(
                Sockseek.Cli.Program.CliExitCode.Cancelled,
                await monitorTask);
            Assert.IsNull(ConsoleInputManager.OnCancelRequested);
            Assert.IsNull(ConsoleInputManager.OnNextCandidateRequested);
            Assert.IsNull(ConsoleInputManager.OnInfoRequested);
        }
        finally
        {
            Console.SetIn(originalIn);
            await cancellation.CancelAsync();
            await app.StopAsync();
            await DeleteDirectoryIfExistsWithRetryAsync(musicRoot);
            await DeleteDirectoryIfExistsWithRetryAsync(outputDir);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Program_MonitorWithInput_SubmitsWorkAndRemainsAttached()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-monitor-input-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-monitor-input-out-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(musicRoot, "Artist"));
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(musicRoot, "Artist", "Artist - Track One.mp3"), "a");

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync();
        url = GetBoundUrl(app);
        try
        {
            var monitorTask = Sockseek.Cli.Program.MainCore(
                [
                    "Artist - Track One",
                    "--song",
                    "--no-config",
                    "--remote", url,
                    "--remote-api-key", await TestServerHost.GetApiKeyAsync(app),
                    "--monitor",
                    "--no-progress",
                    "--output-dir", outputDir,
                ],
                cancellation.Token);

            await WaitForFileAsync(
                outputDir,
                "*.mp3",
                "Input supplied with --monitor was not downloaded.",
                timeoutMs: 10000);

            await cancellation.CancelAsync();
            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Cancelled, await monitorTask);
        }
        finally
        {
            await cancellation.CancelAsync();
            await app.StopAsync();
            await DeleteDirectoryIfExistsWithRetryAsync(musicRoot);
            await DeleteDirectoryIfExistsWithRetryAsync(outputDir);
        }
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

    [TestMethod]
    public async Task RemoteCliBackend_SubmitJobList_SerializesTypedChildItems()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-list-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-remote-backend-list-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.mp3"), "a");

        string url = DynamicLoopbackUrl;
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            url = GetBoundUrl(app);
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            await backend.StartAsync();

            var summary = await backend.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "batch",
                    [
                        new TrackSearchJobDraftDto(
                            new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                    ]));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, ServerWorkflowState.Completed);

            IReadOnlyList<JobSummaryDto> jobs = await backend.GetJobsAsync(new JobQuery(
                null, null, null, summary.WorkflowId, IncludeAll: true));
            Assert.IsTrue(jobs.Any(job => job.Kind == ServerJobKind.JobList));
            Assert.IsTrue(jobs.Any(job => job.Kind == ServerJobKind.Search));
            Assert.IsTrue(jobs.All(job => job.WorkflowId == summary.WorkflowId));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_ReusedRetiredWorkflowIdObservesSuccessorGeneration()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-remote-reused-workflow-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        File.WriteAllText(Path.Combine(musicRoot, "Artist - Track.mp3"), "a");
        await using var app = BuildServer(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, DynamicLoopbackUrl, enablePersistence: false);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(await global::Tests.TestServerHost.CreateHttpClientAsync(app));
            Guid workflowId = Guid.NewGuid();
            var observed = new ConcurrentBag<JobSummaryDto>();
            backend.StateUpdated += update =>
            {
                foreach (var job in update.ChangedJobs)
                    observed.Add(job);
            };

            var first = await backend.SubmitTrackSearchJobAsync(new SubmitTrackSearchJobRequestDto(
                new SongQueryDto("Artist", "Track", "", "", -1, false),
                Options: new SubmissionOptionsDto(workflowId)));
            await WaitForConditionAsync(
                backend,
                () => Task.FromResult(
                    observed.Any(job => job.JobId == first.JobId
                        && job.LifecycleState == ServerJobLifecycleState.Terminal)
                    && backend.ClientStore.GetWorkflowJobs(workflowId).Count == 0),
                "The first workflow generation did not retire from live state.");

            var successor = await backend.SubmitTrackSearchJobAsync(new SubmitTrackSearchJobRequestDto(
                new SongQueryDto("Artist", "Track", "", "", -1, false),
                Options: new SubmissionOptionsDto(workflowId)));
            await WaitForConditionAsync(
                backend,
                () => Task.FromResult(observed.Any(job => job.JobId == successor.JobId
                    && job.LifecycleState == ServerJobLifecycleState.Terminal)),
                "The reused workflow subscription did not observe its successor generation.");

            Assert.AreNotEqual(first.JobId, successor.JobId);
            Assert.AreEqual(workflowId, successor.WorkflowId);
        }
        finally
        {
            await app.StopAsync();
            await DeleteDirectoryIfExistsWithRetryAsync(musicRoot);
        }
    }

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
            detail => detail?.Summary is { } summary ? ProjectState(summary).ToString() : "<missing>",
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

    private static async Task WaitForAlbumFileDownloadToStartAsync(ICliBackend backend, Guid albumJobId)
    {
        await WaitForConditionAsync(
                backend,
                async ct =>
                {
                    return (await GetChildSongPayloadsAsync(backend, albumJobId, ct))
                        .Any(file => ProjectState(file) == ExpectedJobStatus.Downloading) == true;
                },
                "Timed out waiting for remote album file downloads to start.");
    }

    private static ExpectedJobStatus ProjectState(JobSummaryDto summary)
        => ProjectState(summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason);

    private static ExpectedJobStatus ProjectState(SongJobPayloadDto song)
        => ProjectState(
            song.LifecycleState ?? ServerJobLifecycleState.Pending,
            song.ActivityPhase ?? ServerJobActivityPhase.None,
            song.TerminalOutcome ?? ServerJobTerminalOutcome.None,
            song.SkipReason ?? ServerJobSkipReason.None);

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

    private static async Task<List<SongJobPayloadDto>> GetChildSongPayloadsAsync(
        ICliBackend backend,
        Guid parentJobId,
        CancellationToken cancellationToken = default)
    {
        var payloads = new List<SongJobPayloadDto>();
        var children = await backend.GetJobsAsync(
            new JobQuery(null, null, null, null, IncludeAll: true, ParentJobId: parentJobId),
            cancellationToken);
        foreach (var child in children)
        {
            var detail = await backend.GetJobDetailAsync(child.JobId, cancellationToken);
            if (detail?.Payload is SongJobPayloadDto song)
                payloads.Add(song);
        }

        return payloads;
    }

    private static async Task<JobSummaryDto> StartAlbumSearchAsync(ICliBackend backend, string artist, string album)
        => await backend.SubmitAlbumSearchJobAsync(
            new SubmitAlbumSearchJobRequestDto(
                new AlbumQueryDto(artist, album, "", "", false)));

    private static MockSoulseekClient CreateBlockedDownloadClient(string musicRoot)
    {
        var client = MockSoulseekClient.FromLocalPaths(useTags: false, musicRoot);
        client.BeforeDownloadCompletesAsync = static (_, _, cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return client;
    }

    private static async Task<JobSummaryDto> StartFirstAlbumDownloadAsync(ICliBackend backend, Guid searchJobId)
    {
        var projection = await backend.GetFolderResultsAsync(searchJobId, includeFiles: false);
        Assert.IsNotNull(projection);
        Assert.IsTrue(projection.Items.Count > 0);

        var summary = await backend.StartFolderDownloadAsync(
            searchJobId,
            new StartFolderDownloadRequestDto(projection.Items[0].Ref));
        Assert.IsNotNull(summary);
        return summary;
    }

    private static Task WaitForConditionAsync(
        ICliBackend backend,
        Func<Task<bool>> condition,
        string failureMessage,
        int timeoutMs = 5_000)
        => WaitForConditionAsync(
            backend,
            _ => condition(),
            failureMessage,
            timeoutMs);

    private static Task WaitForConditionAsync(
        ICliBackend backend,
        Func<CancellationToken, Task<bool>> condition,
        string failureMessage,
        int timeoutMs = 5_000)
        => BackendTestWaiter.UntilAsync(
            backend,
            condition,
            failureMessage,
            timeoutMs);

    private static async Task WaitForConsoleHandlersAsync(
        Func<bool> condition,
        string failureMessage,
        int timeoutMs = 5_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var signals = CreateTestSignal();
        void OnHandlersChanged() => signals.Writer.TryWrite(0);

        ConsoleInputManager.HandlersChanged += OnHandlersChanged;
        try
        {
            while (!condition())
                await signals.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new AssertFailedException(failureMessage);
        }
        finally
        {
            ConsoleInputManager.HandlersChanged -= OnHandlersChanged;
        }
    }

    private static async Task WaitForFileAsync(
        string directory,
        string filter,
        string failureMessage,
        int timeoutMs = 5_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var signals = CreateTestSignal();
        using var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        void OnChanged(object _, FileSystemEventArgs __) => signals.Writer.TryWrite(0);
        void OnRenamed(object _, RenamedEventArgs __) => signals.Writer.TryWrite(0);
        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Renamed += OnRenamed;

        try
        {
            while (Directory.GetFiles(directory, filter, SearchOption.AllDirectories).Length == 0)
                await signals.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new AssertFailedException(failureMessage);
        }
    }

    private static Channel<byte> CreateTestSignal()
        => Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private static async Task DeleteDirectoryIfExistsWithRetryAsync(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
        }
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class CapturingResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            return Task.FromResult(response);
        }
    }

    private sealed class PagedJobsHandler(Guid workflowId) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? SecondRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            bool secondPage = request.RequestUri?.Query.Contains(
                "cursor=second-page",
                StringComparison.Ordinal) == true;
            if (secondPage)
                SecondRequestUri = request.RequestUri;

            IReadOnlyList<JobSummaryDto> jobs = secondPage
                ? [Job(201, ServerJobTerminalOutcome.Failed)]
                : Enumerable.Range(1, 200)
                    .Select(displayId => Job(displayId, ServerJobTerminalOutcome.Succeeded))
                    .ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(jobs),
                RequestMessage = request,
            };
            if (!secondPage)
                response.Headers.Add("X-Next-Cursor", "second-page");
            response.Headers.Add(ServerProtocol.ReadEpochHeader, workflowId.ToString("D"));
            response.Headers.Add(ServerProtocol.ReadSequenceHeader, "9007199254740993");
            return Task.FromResult(response);
        }

        private JobSummaryDto Job(int displayId, ServerJobTerminalOutcome outcome)
            => new()
            {
                JobId = Guid.Parse($"10000000-0000-0000-0000-{displayId:D12}"),
                DisplayId = displayId,
                WorkflowId = workflowId,
                Kind = ServerJobKind.Song,
                LifecycleState = ServerJobLifecycleState.Terminal,
                TerminalOutcome = outcome,
            };
    }

    private sealed class FailFirstDaemonSnapshotHandler()
        : DelegatingHandler(new HttpClientHandler())
    {
        private int remainingFailures = 1;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/daemon/snapshot"
                && Interlocked.Exchange(ref remainingFailures, 0) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"error":"Synthetic snapshot failure"}"""),
                    RequestMessage = request,
                });
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
