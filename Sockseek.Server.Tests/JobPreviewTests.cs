using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Planning;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Settings;
using Sockseek.Server.Planning;
using Sockseek.Persistence.Planning;
using System.Text;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class JobPreviewTests
{
    [TestMethod]
    public async Task StoredSpotifyPlansExposeSourceContextAndCountTheReviewedExecutionShape()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-source-preview-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions { DataDirectory = directory },
        });
        try
        {
            await using var coordinator = NewCoordinator(options);
            await coordinator.StartAsync(CancellationToken.None);
            try
            {
                // Seed completed extraction output through the planning store;
                // these checks need neither Spotify credentials nor network I/O.
                string database = Directory.EnumerateFiles(directory, "preview.db", SearchOption.AllDirectories).Single();
                await using var store = new JobPreviewStore(database);
                await store.InitializeAsync();
                foreach (var scenario in new[]
                {
                    (Input: "https://open.spotify.com/playlist/source", Album: false, SongGroups: false, Label: "Spotify playlist · 2 songs"),
                    (Input: "https://open.spotify.com/playlist/source", Album: true, SongGroups: false, Label: "Spotify playlist · 2 albums"),
                    (Input: "https://open.spotify.com/playlist/source", Album: false, SongGroups: true, Label: "Spotify playlist · 2 jobs"),
                    (Input: "https://open.spotify.com/playlist/source", Album: true, SongGroups: true, Label: "Spotify playlist · 2 jobs"),
                    (Input: "spotify-likes", Album: false, SongGroups: false, Label: "Spotify liked songs · 2 songs"),
                    (Input: "spotify-albums", Album: false, SongGroups: false, Label: "Spotify liked albums · 2 albums"),
                    (Input: "https://open.spotify.com/album/source", Album: false, SongGroups: false, Label: "Spotify album"),
                })
                {
                    var settings = new DownloadSettings();
                    settings.Extraction.UpgradeToAlbum = scenario.Album;
                    settings.Search.GroupResults = scenario.SongGroups;
                    var extract = new ExtractJob(scenario.Input, InputType.Spotify) { Config = settings };
                    Job extracted = scenario.Input.Contains("/album/", StringComparison.Ordinal)
                        ? new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
                        : new JobList("An editable title", Enumerable.Range(1, 2).Select<int, Job>(index =>
                            scenario.Input == "spotify-albums"
                                ? new AlbumJob(new AlbumQuery { Artist = "Artist", Album = $"Album {index}" }) { ItemNumber = index }
                                : new SongJob(new SongQuery { Artist = "Artist", Title = "Track", Album = $"Album {index}" }) { ItemNumber = index }));
                    Job result = JobExtraction.Normalize(extract, extracted, scenario.Album, new Sockseek.Core.Extractors.SpotifyExtractor(settings.Spotify));
                    var preview = await store.CreateAsync("{}");
                    var nodes = new List<StoredJobPreviewNode>();
                    void Add(Job job, string reference, string? parent)
                    {
                        job.ExtractorInfo = "Catalog entry: A12";
                        var spec = SubmissionSpecification.CreateShallow(job, settings);
                        nodes.Add(new(preview.Id, 0, reference, parent, "Orchestration", "Ready",
                            job is not (ExtractJob or JobList), ServerSnapshotMapper.ToServerJobKind(job).ToString(),
                            CoreSnapshotFactory.JobTitle(job), null, job is JobList list ? list.Count : job is ExtractJob ? 1 : 0,
                            "[]", SubmissionSpecificationCodec.Serialize(spec with { EffectiveSettingsJson = "{}" }),
                            "source-settings", spec.EffectiveSettingsJson, "[]", null, null));
                        if (job is JobList children)
                            for (int index = 0; index < children.Count; index++) Add(children[index], reference + "/" + index, reference);
                    }
                    Add(extract, "0", null);
                    Add(result, "0/result", "0");
                    await store.AppendNodesAsync(preview.Id, nodes);
                    await store.CompleteAsync(preview.Id);
                    var page = (await coordinator.GetNodesAsync(preview.Id, null, null, 100, CancellationToken.None))!;
                    Assert.AreEqual(scenario.Label, page.Items.Single().SourceLabel, scenario.Input);
                    Assert.AreEqual(scenario.Input, page.Items.Single().SourceDetail);
                    Assert.AreEqual("Catalog entry: A12", page.Items.Single().ExtractorInfo);
                }
            }
            finally { await coordinator.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReviewedAndRerunSearchChildrenReplayTheirOwnAcceptedSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-reviewed-search-replay", Guid.NewGuid().ToString("N"));
        var options = new ServerOptions
        {
            ConfigDir = directory,
            Engine = new EngineSettings
            {
                MockFilesDir = Path.Combine(directory, "mock"),
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions { Enabled = true, DataDirectory = Path.Combine(directory, "data") },
        };
        Directory.CreateDirectory(options.Engine.MockFilesDir!);
        try
        {
            await using var app = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
                var previews = app.Services.GetRequiredService<JobPreviewCoordinator>();
                var persistence = app.Services.GetRequiredService<Persistence.PersistenceCoordinator>();
                var facade = app.Services.GetRequiredService<Persistence.JobReadService>();
                var terminals = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<JobSummaryDto>>();
                TaskCompletionSource<JobSummaryDto> Terminal(Guid id) => terminals.GetOrAdd(id,
                    _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                void OnJob(JobSummaryDto job)
                {
                    if (job.LifecycleState == ServerJobLifecycleState.Terminal)
                        Terminal(job.JobId).TrySetResult(job);
                }
                supervisor.StateStore.JobUpserted += OnJob;
                var planned = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnPreview(Guid id) => planned.TrySetResult(id);
                previews.PreviewCompleted += OnPreview;
                try
                {
                    DownloadSettingsPatchDto WithFormat(string format) => new(Search: new(
                        NecessaryCond: new(Formats: new(Replace: [format]))));
                    var created = await previews.CreateAsync(new(new JobListJobDraftDto("Replay batch",
                    [
                        new GenericSearchJobDraftDto("identical query", WithFormat("txt")),
                        new GenericSearchJobDraftDto("identical query", WithFormat("flac")),
                    ])), CancellationToken.None);
                    Assert.AreEqual(created.Preview.PreviewId,
                        await planned.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                    var preview = (await previews.GetAsync(created.Preview.PreviewId, CancellationToken.None))!;
                    var receipt = (await previews.CommitAsync(preview.PreviewId, CommitAll(preview), CancellationToken.None))!;
                    Assert.AreEqual(2, receipt.SubmittedCount);
                    Assert.IsNotNull(receipt.SubmissionId);
                    Assert.IsNull(await previews.GetAsync(preview.PreviewId, CancellationToken.None));

                    async Task AssertChildReplay(JobSummaryDto root)
                    {
                        await Terminal(root.JobId).Task.WaitAsync(TimeSpan.FromSeconds(5));
                        var children = (await facade.GetJobsAsync(new JobQuery(
                            null, null, ServerJobKind.Search, null, IncludeAll: true, SubmissionId: root.SubmissionId), null, 100)).Items;
                        Assert.AreEqual(2, children.Count);
                        var rootRetained = (await persistence.Submissions!.GetSubmissionAsync(root.SubmissionId!.Value))!;
                        var rootSpecification = SubmissionSpecificationCodec.Deserialize(rootRetained.SpecificationJson);
                        var child = children.Single(item => rootSpecification.ForSearchJob(item.JobId)!
                            .MaterializeSettings().Search.NecessaryCond.Formats.SequenceEqual(["txt"]));
                        var replay = await supervisor.Submissions.RerunSearchJobAsync(child.JobId, CancellationToken.None);
                        Assert.IsNotNull(replay);
                        Assert.AreNotEqual(child.JobId, replay.JobId);
                        Assert.AreNotEqual(root.SubmissionId, replay.SubmissionId);
                        await Terminal(replay.JobId).Task.WaitAsync(TimeSpan.FromSeconds(5));
                        var page = await facade.GetJobsAsync(new JobQuery(
                            null, null, null, null, IncludeAll: true, SubmissionId: replay.SubmissionId), null, 100);
                        Assert.AreEqual(1, page.Items.Count, "Replaying one child must not restart its sibling or container.");
                        var retained = (await persistence.Submissions!.GetSubmissionAsync(replay.SubmissionId!.Value))!;
                        Assert.AreEqual(root.SubmissionId, retained.RerunOfSubmissionId);
                        var specification = SubmissionSpecificationCodec.Deserialize(retained.SpecificationJson);
                        Assert.AreEqual(replay.JobId, specification.Command.AcceptedJobId);
                        Assert.AreEqual("identical query", specification.Command.QueryText);
                        CollectionAssert.AreEqual(new[] { "txt" }, specification.MaterializeSettings().Search.NecessaryCond.Formats);
                    }

                    var accepted = (await persistence.Submissions!.GetSubmissionAsync(receipt.SubmissionId.Value))!;
                    Guid acceptedRootId = SubmissionSpecificationCodec.Deserialize(accepted.SpecificationJson)
                        .Command.AcceptedJobId!.Value;
                    var reviewedRoot = await Terminal(acceptedRootId).Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await AssertChildReplay(reviewedRoot);
                    var rerun = await supervisor.Submissions.RerunSubmissionAsync(receipt.SubmissionId.Value, CancellationToken.None);
                    Assert.IsNotNull(rerun);
                    await AssertChildReplay(rerun);
                }
                finally
                {
                    previews.PreviewCompleted -= OnPreview;
                    supervisor.StateStore.JobUpserted -= OnJob;
                }
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DaemonSessionRestartInvalidatesUncommittedPreview()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-preview-restart-tests",
            Guid.NewGuid().ToString("N"));
        Guid previewId;
        try
        {
            var options = Options.Create(new ServerOptions
            {
                DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
                Persistence = new ServerPersistenceOptions { DataDirectory = directory },
            });
            await using (var first = NewCoordinator(options))
            {
                await first.StartAsync(CancellationToken.None);
                var completed = new TaskCompletionSource<Guid>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                first.PreviewCompleted += id => completed.TrySetResult(id);
                CreateJobPreviewResponseDto created = await first.CreateAsync(
                    new CreateJobPreviewRequestDto(
                        new SongJobDraftDto(
                            new SongQueryDto("Restart Artist", "Restart Title"))),
                    CancellationToken.None);
                previewId = created.Preview.PreviewId;
                Assert.AreEqual(
                    previewId,
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsNotNull(await first.GetAsync(previewId, CancellationToken.None));
                await first.StopAsync(CancellationToken.None);
            }

            // A temporary spool may be corrupt after power loss. Startup must
            // discard abandoned session data, never recover it as reviewed work.
            string abandoned = Path.Combine(directory, "planning", "job-preview-spools", "interrupted");
            Directory.CreateDirectory(abandoned);
            await File.WriteAllTextAsync(Path.Combine(abandoned, "preview.db"), "incomplete database");

            await using (var restarted = NewCoordinator(options))
            {
                await restarted.StartAsync(CancellationToken.None);
                Assert.IsNull(await restarted.GetAsync(previewId, CancellationToken.None));
                var completed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
                restarted.PreviewCompleted += id => completed.TrySetResult(id);
                CreateJobPreviewResponseDto fresh = await restarted.CreateAsync(
                    new CreateJobPreviewRequestDto(new SongJobDraftDto(new SongQueryDto("New", "Session"))),
                    CancellationToken.None);
                Assert.AreEqual(fresh.Preview.PreviewId, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(JobPreviewState.Ready,
                    (await restarted.GetAsync(fresh.Preview.PreviewId, CancellationToken.None))!.State);
                await restarted.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PartialPreviewCommitsValidSiblingAndReportsFailedEntry()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-preview-partial-tests",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions { DataDirectory = directory },
        });
        var supervisor = new EngineSupervisor(options);
        var commits = new Persistence.SubmissionCommitCoordinator(
            new Persistence.PersistenceCoordinator(options));
        await using var coordinator = new JobPreviewCoordinator(
            options,
            supervisor.Submissions,
            commits,
            new JobPreviewCursorCodec(),
            NullLogger<JobPreviewCoordinator>.Instance);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);
            // An unrelated pending plan must neither wake on this plan's
            // completion nor be cancelled when its observer stops waiting.
            string database = Directory.EnumerateFiles(directory, "preview.db", SearchOption.AllDirectories).Single();
            await using var store = new JobPreviewStore(database);
            await store.InitializeAsync();
            StoredJobPreview pending = await store.CreateAsync("{}");
            using var observer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<JobPreviewSummaryDto?> waiting = coordinator.GetAsync(pending.Id, observer.Token, waitForCompletion: true);
            Assert.IsFalse(waiting.IsCompleted);
            CreateJobPreviewResponseDto created = await coordinator.CreateAsync(
                new CreateJobPreviewRequestDto(new JobListJobDraftDto(
                    "mixed",
                    [
                        new ExtractJobDraftDto(
                            Path.Combine(directory, "missing.csv"),
                            InputType.CSV.ToString()),
                        new SongJobDraftDto(new SongQueryDto("Valid Artist", "Valid Title")),
                    ])),
                CancellationToken.None);
            JobPreviewSummaryDto preview = (await coordinator.GetAsync(
                created.Preview.PreviewId,
                CancellationToken.None, waitForCompletion: true).WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.AreEqual(JobPreviewState.PartiallyReady, preview.State);
            Assert.IsFalse(waiting.IsCompleted);
            observer.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting);
            Assert.AreEqual(JobPreviewState.Planning,
                (await coordinator.GetAsync(pending.Id, CancellationToken.None))!.State);
            await store.CompleteAsync(pending.Id);
            Assert.AreEqual(JobPreviewState.Failed,
                (await coordinator.GetAsync(pending.Id, CancellationToken.None, waitForCompletion: true))!.State);
            Assert.IsNull(await coordinator.GetAsync(Guid.NewGuid(), CancellationToken.None, waitForCompletion: true));
            Assert.AreEqual(1, preview.FailedNodeCount);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                coordinator.CommitAsync(preview.PreviewId,
                    new CommitJobPreviewRequestDto(preview.Revision,
                        new RefSelectionExpressionDto((RefSelectionMode)123, []),
                        Guid.NewGuid()),
                    CancellationToken.None));

            CommitJobPreviewResponseDto receipt = (await coordinator.CommitAsync(
                preview.PreviewId,
                CommitAll(preview),
                CancellationToken.None))!;
            Assert.AreEqual(1, receipt.SubmittedCount);
            Assert.AreEqual(1, receipt.RejectedCount);
            Assert.AreEqual("planning-failed", receipt.RejectionReasons.Single().Reason);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UploadedArtifactPreviewCommitsDigestWithoutSourceMutationOrPathTrust()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-artifact-preview-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "mock"));
        var options = new ServerOptions
        {
            ConfigDir = directory,
            Engine = new EngineSettings
            {
                MockFilesDir = Path.Combine(directory, "mock"),
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = Path.Combine(directory, "data"),
            },
        };
        try
        {
            await using var app = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var artifacts = app.Services.GetRequiredService<InputArtifactCoordinator>();
                InputArtifactDto artifact = await artifacts.UploadAsync(
                    new MemoryStream(Encoding.UTF8.GetBytes(
                        "artist,title\nArtifact Artist,Artifact Title\n")),
                    "../../browser-name.csv",
                    CancellationToken.None);
                Assert.AreEqual("browser-name.csv", artifact.OriginalName);

                var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
                JobSummaryDto direct = await supervisor.Submissions.SubmitExtractJobAsync(
                    new SubmitExtractJobRequestDto(
                        Input: "browser-path-is-not-used",
                        ArtifactId: artifact.ArtifactId),
                    CancellationToken.None);
                var persistence = app.Services.GetRequiredService<Persistence.PersistenceCoordinator>();
                var directRetained = await persistence.Submissions!.GetSubmissionAsync(
                    direct.SubmissionId!.Value,
                    CancellationToken.None);
                SubmissionSpecification directSpecification = SubmissionSpecificationCodec.Deserialize(
                    directRetained!.SpecificationJson);
                Assert.AreEqual("input-artifact", directSpecification.SourceRevision?.Kind);
                Assert.AreEqual(artifact.Sha256, directSpecification.SourceRevision?.Digest);
                Assert.AreEqual(0, await persistence.InputArtifacts!.ReleaseAsync(direct.SubmissionId.Value),
                    "Direct acceptance releases its input once the extracted specification is durable.");

                var previews = app.Services.GetRequiredService<JobPreviewCoordinator>();
                var completed = new TaskCompletionSource<Guid>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(Guid id) => completed.TrySetResult(id);
                previews.PreviewCompleted += OnCompleted;
                CreateJobPreviewResponseDto created;
                try
                {
                    created = await previews.CreateAsync(
                        new CreateJobPreviewRequestDto(
                            new ExtractJobDraftDto(
                                Input: "browser-path-is-not-used",
                                ArtifactId: artifact.ArtifactId,
                                DownloadSettings: new DownloadSettingsPatchDto(
                                    Extraction: new ExtractionSettingsPatchDto(
                                        RemoveTracksFromSource: true)))),
                        CancellationToken.None);
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    previews.PreviewCompleted -= OnCompleted;
                }

                CommitJobPreviewResponseDto receipt = (await previews.CommitAsync(
                    created.Preview.PreviewId,
                    CommitAll((await previews.GetAsync(
                        created.Preview.PreviewId,
                        CancellationToken.None))!),
                    CancellationToken.None))!;
                var retained = await persistence.Submissions!.GetSubmissionAsync(
                    receipt.SubmissionId!.Value,
                    CancellationToken.None);
                SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(
                    retained!.SpecificationJson);
                Assert.AreEqual(artifact.ArtifactId, specification.Command.ArtifactId);
                Assert.AreEqual("input-artifact", specification.SourceRevision?.Kind);
                Assert.AreEqual(artifact.ArtifactId, specification.SourceRevision?.Identity);
                Assert.AreEqual(artifact.Sha256, specification.SourceRevision?.Digest);
                Assert.IsNull(specification.Command.SourceMutation);
                Assert.AreEqual(JobSnapshotKind.Extract, specification.Command.Kind);
                Assert.AreEqual("browser-name.csv", specification.Command.Input);
                Assert.AreEqual(InputType.CSV, specification.Command.InputType);
                Assert.IsTrue(specification.Command.HasPlannedExtraction);
                Assert.IsNotNull(specification.MaterializeJob().PlannedSettings);
                NormalizedJobCommand artifactSong = FindSong(specification.Command);
                Assert.AreEqual("Artifact Artist", artifactSong.SongQuery?.Artist);
                Assert.IsNull(artifactSong.SourceMutation);
                var artifactStore = persistence.InputArtifacts!;
                Assert.AreEqual(1, (await artifactStore.RetainAsync([artifact.ArtifactId], created.Preview.PreviewId)).Count,
                    "The committed preview retains its inputs until maintenance.");
                CollectionAssert.AreEqual(new[] { created.Preview.PreviewId },
                    (await previews.RunMaintenanceOnceAsync(CancellationToken.None)).ToArray());
                Assert.AreEqual(0, await artifactStore.ReleaseAsync(created.Preview.PreviewId),
                    "Maintenance must release the reclaimed preview's input references too.");
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PreviewCommitUsesStoredPlanAfterMutableSourceChanges()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-job-preview-server-tests",
            Guid.NewGuid().ToString("N"));
        string input = Path.Combine(directory, "input.csv");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(input,
            "artist,title\nOriginal Artist,Original Title\n,\nExcluded Artist,Excluded Title\n");
        var options = new ServerOptions
        {
            ConfigDir = directory,
            Engine = new EngineSettings
            {
                MockFilesDir = Path.Combine(directory, "mock"),
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = Path.Combine(directory, "data"),
            },
        };
        Directory.CreateDirectory(options.Engine.MockFilesDir!);

        try
        {
            await using var app = global::Tests.TestServerHost.Build([], options, "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var coordinator = app.Services.GetRequiredService<JobPreviewCoordinator>();
                var completed = new TaskCompletionSource<Guid>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(Guid id) => completed.TrySetResult(id);
                coordinator.PreviewCompleted += OnCompleted;
                CreateJobPreviewResponseDto created;
                try
                {
                    created = await coordinator.CreateAsync(
                        new CreateJobPreviewRequestDto(
                            new ExtractJobDraftDto(input, InputType.CSV.ToString())),
                        CancellationToken.None);
                    Assert.AreEqual(JobPreviewState.Planning, created.Preview.State);
                    Assert.AreEqual(
                        created.Preview.PreviewId,
                        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    coordinator.PreviewCompleted -= OnCompleted;
                }

                JobPreviewSummaryDto preview = (await coordinator.GetAsync(
                    created.Preview.PreviewId,
                    CancellationToken.None))!;
                Assert.IsTrue(preview.State is JobPreviewState.Ready or JobPreviewState.PartiallyReady);
                Assert.AreEqual(2, preview.SelectableNodeCount);

                CursorPage<JobPreviewNodeDto> listPage = (await coordinator.GetNodesAsync(
                    preview.PreviewId,
                    "0",
                    null,
                    10,
                    CancellationToken.None))!;
                Assert.AreEqual("Mixed jobs", listPage.Items.Single().SourceDetail);

                CursorPage<JobPreviewNodeDto> resultPage = (await coordinator.GetNodesAsync(
                    preview.PreviewId,
                    "0/result",
                    null,
                    10,
                    CancellationToken.None))!;
                Assert.AreEqual(2, resultPage.Items.Count);
                Assert.AreEqual("CSV row 2", resultPage.Items[0].SourceDetail);
                Assert.AreEqual("CSV row 4", resultPage.Items[1].SourceDetail);

                byte[] originalBytes = await File.ReadAllBytesAsync(input);
                string originalDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalBytes))
                    .ToLowerInvariant();
                await File.WriteAllTextAsync(input, "artist,title\nChanged Artist,Changed Title\n");
                CommitJobPreviewRequestDto commit = new(
                    preview.Revision,
                    new RefSelectionExpressionDto(
                        RefSelectionMode.Only,
                        [resultPage.Items[0].Ref]),
                    Guid.NewGuid());
                CommitJobPreviewResponseDto receipt = (await coordinator.CommitAsync(
                    preview.PreviewId,
                    commit,
                    CancellationToken.None))!;
                Assert.AreEqual(1, receipt.SubmittedCount);
                Assert.IsNotNull(receipt.SubmissionId);
                CommitJobPreviewResponseDto repeated = (await coordinator.CommitAsync(
                    preview.PreviewId,
                    commit,
                    CancellationToken.None))!;
                Assert.AreEqual(receipt.SubmissionId, repeated.SubmissionId);

                var persistence = app.Services.GetRequiredService<Persistence.PersistenceCoordinator>();
                var retained = await persistence.Submissions!.GetSubmissionAsync(
                    receipt.SubmissionId.Value,
                    CancellationToken.None);
                SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(
                    retained!.SpecificationJson);
                Assert.AreEqual("local-file", specification.SourceRevision?.Kind);
                Assert.AreEqual(input, specification.SourceRevision?.Identity);
                Assert.AreEqual(originalDigest, specification.SourceRevision?.Digest);
                Assert.AreEqual(originalBytes.LongLength, specification.SourceRevision?.Length);
                Assert.AreEqual(JobSnapshotKind.Extract, specification.Command.Kind);
                Assert.IsTrue(specification.Command.HasPlannedExtraction);
                Assert.AreEqual(JobSnapshotKind.JobList, specification.Command.ExtractedResult?.Kind);
                Assert.AreEqual(1, specification.Command.ExtractedResult?.Children?.Count);
                NormalizedJobCommand selectedSong = FindSong(specification.Command);
                Assert.AreEqual("Original Artist", selectedSong.SongQuery?.Artist);
                Assert.AreEqual("Original Title", selectedSong.SongQuery?.Title);
                Assert.AreNotEqual("Changed Artist", selectedSong.SongQuery?.Artist);
                var executable = (ExtractJob)specification.MaterializeJob();
                var executableSong = ((JobList)executable.Result!).Jobs.OfType<SongJob>().Single();
                Assert.AreEqual("Original Artist", executableSong.Query.Artist);
                Assert.AreEqual("Original Title", executableSong.Query.Title);
                Assert.IsNotNull(executableSong.PlannedSettings);

                Assert.IsNull(await coordinator.GetAsync(
                    preview.PreviewId,
                    CancellationToken.None));
                Assert.IsNull(await coordinator.GetNodesAsync(preview.PreviewId, null, null, 10, CancellationToken.None));
                CollectionAssert.AreEqual(new[] { preview.PreviewId },
                    (await coordinator.RunMaintenanceOnceAsync(CancellationToken.None)).ToArray(),
                    "Maintenance, not admission, must reclaim the committed temporary plan.");
                repeated = (await coordinator.CommitAsync(preview.PreviewId, commit, CancellationToken.None))!;
                Assert.AreEqual(receipt.SubmissionId, repeated.SubmissionId,
                    "Cleanup must not remove the accepted retry receipt.");
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnavailablePreviewStoreDoesNotDisableDirectStart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-job-preview-unavailable-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string unavailableDataPath = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(unavailableDataPath, "occupied");
        var options = Options.Create(new ServerOptions
        {
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = false,
                DataDirectory = unavailableDataPath,
            },
        });
        var supervisor = new EngineSupervisor(options);
        var commits = new Persistence.SubmissionCommitCoordinator(
            new Persistence.PersistenceCoordinator(options));
        await using var coordinator = new JobPreviewCoordinator(
            options,
            supervisor.Submissions,
            commits,
            new JobPreviewCursorCodec(),
            NullLogger<JobPreviewCoordinator>.Instance);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);
            await Assert.ThrowsExactlyAsync<JobPreviewUnavailableException>(() =>
                coordinator.CreateAsync(
                    new CreateJobPreviewRequestDto(
                        new ExtractJobDraftDto("Artist - Title", InputType.String.ToString())),
                    CancellationToken.None));

            JobSummaryDto direct = await supervisor.Submissions.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    "Artist - Title",
                    InputType.String.ToString()),
                CancellationToken.None);
            Assert.AreEqual(ServerJobKind.Extract, direct.Kind);
            Assert.IsNotNull(direct.SubmissionId);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static CommitJobPreviewRequestDto CommitAll(JobPreviewSummaryDto preview)
        => new(
            preview.Revision,
            new RefSelectionExpressionDto(RefSelectionMode.AllExcept, []),
            Guid.NewGuid());

    private static NormalizedJobCommand FindSong(NormalizedJobCommand command)
    {
        if (command.SongQuery != null)
            return command;
        if (command.ExtractedResult != null)
            return FindSong(command.ExtractedResult);
        foreach (NormalizedJobCommand child in command.Children ?? [])
        {
            try
            {
                return FindSong(child);
            }
            catch (InvalidDataException)
            {
                // Another selected branch may be a different job kind.
            }
        }
        throw new InvalidDataException("The accepted plan has no song command.");
    }

    private static JobPreviewCoordinator NewCoordinator(
        IOptions<ServerOptions> options)
    {
        var supervisor = new EngineSupervisor(options);
        var commits = new Persistence.SubmissionCommitCoordinator(
            new Persistence.PersistenceCoordinator(options));
        return new JobPreviewCoordinator(
            options,
            supervisor.Submissions,
            commits,
            new JobPreviewCursorCodec(),
            NullLogger<JobPreviewCoordinator>.Instance);
    }
}
