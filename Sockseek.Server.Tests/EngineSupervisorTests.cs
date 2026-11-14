using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Tests.Server;

[TestClass]
public class EngineSupervisorTests
{
    [TestMethod]
    public async Task ProfileReloadDoesNotChangeAnInProgressPreview()
    {
        static ProfileCatalog Profiles(string format) => new()
        {
            DefaultProfile = new SettingsProfile
            {
                Download = new SettingsPatch([new("Search.PreferredCond.Formats", new[] { format })]),
            },
        };
        using var supervisor = CreateSupervisor(Path.GetTempPath(), Path.GetTempPath(), profiles: Profiles("mp3"));
        var draft = new JobListJobDraftDto("preview", [new GenericSearchJobDraftDto("first"), new GenericSearchJobDraftDto("second")]);
        await using var preview = supervisor.Submissions.PlanDraftAsync(draft, null, null, CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await preview.MoveNextAsync());
        var reviewedRoot = preview.Current;
        supervisor.Submissions.ReloadProfiles(Profiles("flac"));
        int children = 0;
        while (await preview.MoveNextAsync())
        {
            children++;
            CollectionAssert.AreEqual(new[] { "mp3" }, preview.Current.ReadSettings()!.Search.PreferredCond.Formats);
        }
        Assert.AreEqual(2, children);
        CollectionAssert.AreEqual(new[] { "mp3" }, reviewedRoot.ReadSettings()!.Search.PreferredCond.Formats);
        var current = supervisor.Submissions.ResolveEffectiveSettings(new(new GenericSearchJobDraftDto("new")));
        CollectionAssert.AreEqual(new[] { "flac" }, current.Settings.Values.Search!.PreferredCond!.Formats!.Replace!.ToArray());
    }

    [TestMethod]
    public void BulkTransferCancellation_IsolatesRacesRejectionsAndFailures()
    {
        Guid succeeded = Guid.NewGuid();
        Guid terminalRace = Guid.NewGuid();
        Guid rejected = Guid.NewGuid();
        Guid failed = Guid.NewGuid();
        TransferStateDto Active(Guid id) => new(
            id,
            1,
            new TransferIdentityFieldsDto(null, null, "Download", "SoulseekPeer", "peer", "file", null),
            new TransferStatusFieldsDto("InProgress", null, 1, false),
            new TransferProgressFieldsDto(0, 100));
        TransferStateDto Terminal(Guid id) => Active(id) with
        {
            Status = new TransferStatusFieldsDto(
                "Completed", null, 1, true, TransferTerminalOutcome.Succeeded),
        };
        var current = new Dictionary<Guid, TransferStateDto>
        {
            [succeeded] = Active(succeeded),
            [terminalRace] = Terminal(terminalRace),
            [rejected] = Active(rejected),
            [failed] = Active(failed),
        };
        var logged = new List<Guid>();

        TransferCommandReceiptDto receipt = EngineSupervisor.CancelTransferSnapshot(
            TransferCommandDirection.Download,
            [Active(succeeded), Active(terminalRace), Active(rejected), Active(failed)],
            id => current.GetValueOrDefault(id),
            id => id == succeeded
                ? true
                : id == failed
                    ? throw new InvalidOperationException("fixture")
                    : false,
            _ => throw new AssertFailedException("Upload owner must not be called."),
            (id, _) => logged.Add(id));

        Assert.AreEqual(4, receipt.ResolvedCount);
        Assert.AreEqual(1, receipt.SucceededCount);
        Assert.AreEqual(1, receipt.NoOpCount);
        Assert.AreEqual(1, receipt.RejectedCount);
        Assert.AreEqual(1, receipt.FailedCount);
        CollectionAssert.AreEquivalent(new[] { failed }, logged);
        Assert.AreEqual(1, receipt.Reasons.Single(reason => reason.Reason == "already-terminal").Count);
        Assert.AreEqual(1, receipt.Reasons.Single(reason => reason.Reason == "not-cancellable").Count);
        Assert.AreEqual(1, receipt.Reasons.Single(reason => reason.Reason == "internal-failure").Count);
    }

    [TestMethod]
    public async Task SubmitSearchJobAsync_RejectsOversizedQueryWithoutCreatingWorkflow()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        var outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(outputDir);

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var oversized = new string('x', JobRequestMapper.MaxSearchTextLength + 1);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.Submissions.SubmitSearchJobAsync(new SubmitSearchJobRequestDto(oversized), CancellationToken.None));

            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitSearchJobAsync_UsesNeutralGenericBaseline()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(root, "manual.pdf"), "test");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(root, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            JobSummaryDto summary = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("manual"),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            SearchJob? job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job?.Config);
            Assert.AreEqual(0, job.Config.Search.NecessaryCond.Formats.Length);
            Assert.IsNull(job.Config.Search.NecessaryCond.LengthTolerance);
            Assert.AreEqual(0, job.Config.Search.PreferredCond.Formats.Length);
            Assert.IsNull(job.Config.Search.PreferredCond.MinBitrate);
            Assert.IsFalse(job.Config.Search.PreferredCond.StrictTitle);
            Assert.IsFalse(job.Config.Search.PreferredCond.StrictAlbum);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartFileDownloadsAsync_ReusesWorkflowAndSetsSourceJob()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var downloadSummary = await supervisor.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto([
                    new FileCandidateRefDto(
                        "local",
                        @"Artist\Album\01. Artist - Track One.mp3"),
                ]),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(1, downloadSummary.Count);
            var downloadedSummary = downloadSummary[0];
            Assert.AreEqual(searchSummary.WorkflowId, downloadedSummary.WorkflowId);
            Assert.IsNull(downloadedSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadedSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, downloadedSummary.JobId, ExpectedJobStatus.Succeeded);

            var detail = supervisor.StateStore.GetJobDetail(downloadedSummary.JobId);
            Assert.IsNotNull(detail);
            Assert.IsNull(detail.Summary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, detail.Summary.SourceJobId);

            var downloaded = Directory.GetFiles(outputDir, "*.mp3", SearchOption.AllDirectories);
            Assert.AreEqual(1, downloaded.Length);
            Assert.IsTrue(downloaded[0].EndsWith("01. Artist - Track One.mp3", StringComparison.OrdinalIgnoreCase));

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_ReusesWorkflowAndFindsAlbumByFolderPath()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(
                    new AlbumFolderRefDto("local", @"Artist\Album")),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.WorkflowId, downloadSummary.WorkflowId);
            Assert.IsNull(downloadSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Succeeded);

            var detail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(detail);
            Assert.IsNull(detail.Summary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, detail.Summary.SourceJobId);

            var workflowJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, null, null, searchSummary.WorkflowId, IncludeAll: false));
            Assert.AreEqual(2, workflowJobs.Count);
            CollectionAssert.AreEquivalent(
                new[] { searchSummary.JobId, downloadSummary.JobId },
                workflowJobs.Select(job => job.JobId).ToArray());

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { "01. Track One.mp3", "02. Track Two.mp3" },
                downloaded,
                "Actual output files: " + string.Join(", ", downloaded));

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFileDownloadsAsync_CanDownloadSingleFileFromAlbumSearch()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var downloads = await supervisor.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto([
                    new FileCandidateRefDto("local", @"Artist\Album\02. Track Two.mp3"),
                ]),
                CancellationToken.None);

            Assert.IsNotNull(downloads);
            Assert.AreEqual(1, downloads.Count);
            Assert.AreEqual(searchSummary.WorkflowId, downloads[0].WorkflowId);

            await WaitForJobStateAsync(supervisor, downloads[0].JobId, ExpectedJobStatus.Succeeded);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "02. Track Two.mp3" }, downloaded);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_DoesNotInheritSearchSubmissionSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Search.NoBrowseFolder = false;
            });
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false),
                    Options: new SubmissionOptionsDto(
                        DownloadSettings: new DownloadSettingsPatchDto(Search: new SearchSettingsPatchDto(NoBrowseFolder: true)))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);
            var searchJob = supervisor.GetRuntimeJob<SearchJob>(searchSummary.JobId);
            Assert.IsNotNull(searchJob);
            Assert.IsTrue(searchJob.Config?.Search.NoBrowseFolder);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(
                    new AlbumFolderRefDto("local", @"Artist\Album")),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            await WaitForConditionAsync(supervisor,
                () => supervisor.GetRuntimeJob<AlbumJob>(downloadSummary.JobId)?.Config != null,
                "Timed out waiting for album download settings.");

            var albumJob = supervisor.GetRuntimeJob<AlbumJob>(downloadSummary.JobId);
            Assert.IsNotNull(albumJob);
            Assert.IsFalse(albumJob.Config?.Search.NoBrowseFolder, "Download should use default settings, not the search submission delta.");

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Succeeded);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobListAsync_AppliesChildDraftDownloadSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(trackDir, "Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.Submissions.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "configured children",
                    [
                        new TrackSearchJobDraftDto(
                            new SongQueryDto("Artist", "Track One", "", "", -1, false),
                            DownloadSettings: new DownloadSettingsPatchDto(
                                Search: new SearchSettingsPatchDto(NoBrowseFolder: true, MaxStaleTime: 111))),
                        new TrackSearchJobDraftDto(
                            new SongQueryDto("Artist", "Track Two", "", "", -1, false),
                            DownloadSettings: new DownloadSettingsPatchDto(
                                Search: new SearchSettingsPatchDto(NoBrowseFolder: false, MaxStaleTime: 222))),
                    ]),
                CancellationToken.None);

            await WaitForConditionAsync(supervisor,
                () =>
                {
                    var children = SearchChildren(supervisor, summary.WorkflowId);
                    return children.Count == 2
                        && children.All(child => supervisor.GetRuntimeJob<SearchJob>(child.JobId)?.Config != null);
                },
                "Timed out waiting for job-list child settings.");

            var children = SearchChildren(supervisor, summary.WorkflowId).ToList();
            Assert.AreEqual(2, children.Count);

            var first = SearchChildByQuery(supervisor, children, "Track One");
            var second = SearchChildByQuery(supervisor, children, "Track Two");

            Assert.AreEqual(111, first.Config.Search.MaxStaleTime);
            Assert.IsTrue(first.Config.Search.NoBrowseFolder);
            Assert.AreEqual(222, second.Config.Search.MaxStaleTime);
            Assert.IsFalse(second.Config.Search.NoBrowseFolder);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_CancelWorkflowMarksUnfinishedPayloadFilesCancelled()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
            File.WriteAllBytes(Path.Combine(albumDir, $"{i:00}. Artist - Track {i:00}.mp3"), new byte[1024]);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedClient = Tests.ClientTests.MockSoulseekClient.FromLocalPaths(false, musicRoot);
        blockedClient.BeforeDownloadCompletesAsync = (_, _, ct) => release.Task.WaitAsync(ct);
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(
                musicRoot,
                outputDir,
                configureDownload: settings => settings.Search.NoBrowseFolder = true,
                client: blockedClient);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(
                    new AlbumFolderRefDto("local", albumDir.Replace('/', '\\'))),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);

            await WaitForConditionAsync(supervisor,
                () =>
                {
                    return GetChildSongPayloads(supervisor, downloadSummary.JobId)
                        .Any(file => ProjectState(file) == ExpectedJobStatus.Downloading) == true;
                },
                "Timed out waiting for album file downloads to start.");

            var activeDetail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(activeDetail);
            var activeFiles = GetChildSongPayloads(supervisor, downloadSummary.JobId);
            var activeAlbumPayload = activeDetail.Payload as AlbumJobPayloadDto;
            Assert.IsNotNull(activeAlbumPayload);
            Assert.IsTrue(activeAlbumPayload.Directory.FileCount >= activeFiles.Count);
            var cancellableFile = activeFiles.FirstOrDefault(file =>
                file.AvailableActions?.Any(action => action.Kind == ServerResourceActionKind.Cancel) == true);
            Assert.IsNotNull(cancellableFile, "Active album payload files should expose cancel actions.");

            var childSongJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, null, ServerJobKind.Song, downloadSummary.WorkflowId, IncludeAll: true))
                .Where(summary => summary.ParentJobId == downloadSummary.JobId)
                .ToList();
            Assert.IsTrue(childSongJobs.Count > 0, "Album payload songs should be registered jobs.");
            Assert.IsFalse(
                supervisor.StateStore.GetJobs(new JobQuery(null, null, ServerJobKind.Song, downloadSummary.WorkflowId, IncludeAll: false))
                    .Any(summary => summary.ParentJobId == downloadSummary.JobId),
                "Album payload songs should stay out of the default job list.");

            Assert.IsNotNull(cancellableFile.JobId);
            Assert.IsTrue(supervisor.CancelJob(cancellableFile.JobId.Value), "Album payload file should be cancellable by job id.");
            await WaitForConditionAsync(supervisor,
                () =>
                {
                    return GetChildSongPayloads(supervisor, downloadSummary.JobId)
                        .Any(file => file.JobId == cancellableFile.JobId
                            && file.TerminalOutcome == ServerJobTerminalOutcome.Cancelled
                            && file.FailureReason == ServerProtocol.FailureReasons.Cancelled) == true;
                },
                "Timed out waiting for album file cancellation.");

            var cancelledAlbumPayload = supervisor.StateStore.GetJobDetail(downloadSummary.JobId)?.Payload as AlbumJobPayloadDto;
            Assert.IsNotNull(cancelledAlbumPayload);
            Assert.IsTrue(cancelledAlbumPayload.Directory.FileCount >= activeFiles.Count);
            Assert.IsTrue(cancelledAlbumPayload.Directory.TerminalFileCount >= 1);
            Assert.IsTrue(cancelledAlbumPayload.Directory.FailedFileCount >= 1);

            var cancelled = supervisor.CancelWorkflow(downloadSummary.WorkflowId);
            Assert.IsTrue(cancelled > 0, "CancelWorkflow should cancel the active album download job.");

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Failed);

            var cancelledDetail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(cancelledDetail);
            var files = GetChildSongPayloads(supervisor, downloadSummary.JobId);
            Assert.AreEqual(12, files.Count);
            Assert.IsFalse(
                files.Any(IsActive),
                "Cancelled album payload should not expose stale active file states.");
            Assert.IsTrue(
                files.Any(file => file.TerminalOutcome == ServerJobTerminalOutcome.Cancelled
                    && file.FailureReason == ServerProtocol.FailureReasons.Cancelled),
                "Cancelled album payload should mark unfinished files as cancelled.");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesJobAndWorkflowUpserts_ForSubmittedJobs()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var seenJobIds = new ConcurrentBag<Guid>();
            var seenWorkflowIds = new ConcurrentBag<Guid>();
            supervisor.StateStore.JobUpserted += summary => seenJobIds.Add(summary.JobId);
            supervisor.StateStore.WorkflowUpserted += summary => seenWorkflowIds.Add(summary.WorkflowId);

            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            CollectionAssert.Contains(seenJobIds.ToList(), searchSummary.JobId);
            CollectionAssert.Contains(seenWorkflowIds.ToList(), searchSummary.WorkflowId);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesSearchUpdated_ForSearchJobResultsAndCompletion()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var updates = new ConcurrentBag<SearchStateDto>();
            supervisor.StateStore.SearchUpdated += update => updates.Add(update);

            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);
            await WaitForConditionAsync(supervisor,
                () => updates.Any(update => update.JobId == searchSummary.JobId && update.IsComplete),
                "Timed out waiting for a completed search update.");

            var matching = updates.Where(update => update.JobId == searchSummary.JobId).ToList();
            Assert.IsTrue(matching.Any(update => update.Revision > 0 && !update.IsComplete));
            Assert.IsTrue(matching.Any(update => update.IsComplete));

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartRetrieveFolderAsync_CompletesQueuedRetrieveJobAndPreservesWorkflow()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Search.NecessaryCond.StrictTitle = true;
            });
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "Track One", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var retrieveSummary = await supervisor.StartRetrieveFolderAsync(
                searchSummary.JobId,
                new RetrieveFolderRequestDto(
                    new AlbumFolderRefDto("local", @"Artist\Album")),
                CancellationToken.None);

            Assert.IsNotNull(retrieveSummary);
            Assert.AreEqual(searchSummary.WorkflowId, retrieveSummary.WorkflowId);
            Assert.IsNull(retrieveSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, retrieveSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, retrieveSummary.JobId, ExpectedJobStatus.Succeeded);

            var retrieveDetail = supervisor.StateStore.GetJobDetail(retrieveSummary.JobId);
            Assert.IsNotNull(retrieveDetail);
            var payload = retrieveDetail.Payload as RetrieveFolderJobPayloadDto;
            Assert.IsNotNull(payload);
            Assert.AreEqual(
                1,
                payload.NewFilesFoundCount);

            var workflowJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, null, null, searchSummary.WorkflowId, IncludeAll: false));
            Assert.AreEqual(2, workflowJobs.Count);
            CollectionAssert.AreEquivalent(
                new[] { searchSummary.JobId, retrieveSummary.JobId },
                workflowJobs.Select(job => job.JobId).ToArray());

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(
                    new AlbumFolderRefDto("local", @"Artist\Album")),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Succeeded);

            var retrieveJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, null, ServerJobKind.RetrieveFolder, searchSummary.WorkflowId, IncludeAll: true));
            Assert.AreEqual(1, retrieveJobs.Count, "Starting a download from a fully retrieved folder should not browse it again.");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesServerAutoProfileFromClientContext()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var profile = CreateProfile("my-interactive", settings => settings.Search.MaxStaleTime = 9999999)
                with
            { Condition = "interactive && album" };
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false),
                    new SubmissionOptionsDto(ProfileContext: new Dictionary<string, bool>
                    {
                        ["interactive"] = true,
                    })),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(9999999, job.Config?.Search.MaxStaleTime);
            CollectionAssert.Contains(job.Config?.AppliedAutoProfiles?.ToList(), "my-interactive");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_DeclaredSearchModeParticipatesInAutoProfileMatching()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(root, "Artist - Track.mp3"), "test");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            SettingsProfile profile = CreateProfile(
                "group-request",
                settings => settings.Search.MaxStaleTime = 7654) with
            { Condition = "group" };
            var supervisor = CreateSupervisor(root, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            runTask = supervisor.RunAsync(cts.Token);

            JobSummaryDto summary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track", "", "", -1, false),
                    GroupResults: true,
                    Options: new SubmissionOptionsDto(
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Search: new SearchSettingsPatchDto(GroupResults: true)))),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            SearchJob? job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job?.Config);
            CollectionAssert.Contains(job.Config.AppliedAutoProfiles.ToList(), "group-request");
            Assert.AreEqual(7654, job.Config.Search.MaxStaleTime);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ExpandsServerProfileOutputPaths()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var profile = CreateProfile("album-inbox", settings => settings.Output.ParentDir = "~/Music/Inbox")
                with
            { Condition = "album" };
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Inbox")),
                job.Config?.Output.ParentDir);
            Assert.IsFalse(job.Config?.Output.ParentDir?.Contains('~') == true);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_LaunchDownloadSettingsOverrideServerAutoProfiles()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        string launchOutputDir = Path.Combine(musicRoot, "launch-out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(launchOutputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var profile = CreateProfile("album-profile", settings =>
                {
                    settings.Output.ParentDir = "~/Music/Inbox";
                    settings.Search.MaxStaleTime = 999999;
                })
                with
            { Condition = "album" };
            var supervisor = CreateSupervisor(
                musicRoot,
                outputDir,
                configureDownload: settings => settings.Search.MaxStaleTime = 111,
                profiles: new ProfileCatalog
                {
                    AutoProfiles = [profile],
                    NamedProfiles = [profile],
                },
                launchDownloadSettings: new DownloadSettingsPatchDto(
                    Output: new OutputSettingsPatchDto(ParentDir: launchOutputDir),
                    Search: new SearchSettingsPatchDto(MaxStaleTime: 222)));
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(Path.GetFullPath(launchOutputDir), job.Config?.Output.ParentDir);
            Assert.AreEqual(222, job.Config?.Search.MaxStaleTime);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesServerNamedProfile()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("long-search", settings => settings.Search.MaxStaleTime = 123456);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var profiles = supervisor.Submissions.GetProfiles();
            Assert.AreEqual(1, profiles.Count);
            Assert.AreEqual("long-search", profiles[0].Name);

            var summary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(ProfileNames: ["long-search"])),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(123456, job.Config?.Search.MaxStaleTime);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesClientDownloadSettingsPatchAfterProfiles()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("short-search", settings => settings.Search.MaxStaleTime = 111);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var baseline = new DownloadSettings();
            var cliSettings = SettingsCloner.Clone(baseline);
            cliSettings.Search.MaxStaleTime = 222;
            cliSettings.Search.NecessaryCond.Formats = ["flac"];

            var summary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["short-search"],
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromCore(SettingsPatch.Difference(baseline, cliSettings)))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(222, job.Config?.Search.MaxStaleTime);
            CollectionAssert.AreEqual(new[] { "flac" }, job.Config?.Search.NecessaryCond.Formats);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ClientDeltaCanSetBuiltInDefaultValueOverProfile()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("no-skip", settings => settings.Skip.SkipExisting = false);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["no-skip"],
                        DownloadSettings: new DownloadSettingsPatchDto(Skip: new SkipSettingsPatchDto(SkipExisting: true)))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.IsTrue(job.Config?.Skip.SkipExisting);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ClientDeltaCanAppendToProfileListSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("base-command", settings => settings.Output.OnComplete = ["-- first"]);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["base-command"],
                        DownloadSettings: new DownloadSettingsPatchDto(Output: new OutputSettingsPatchDto(OnComplete: new CollectionPatchDto<string>(Append: ["-- second"]))))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            CollectionAssert.AreEqual(new[] { "-- first", "-- second" }, job.Config?.Output.OnComplete);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResolveEffectiveSettings_IsSideEffectFreeRedactedAndMatchesAcceptedSubmission(bool generic)
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-effective-settings-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(root, "Artist - Track.mp3"), "data");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            SettingsProfile profile = CreateProfile("review", settings =>
            {
                settings.Extraction.Input = "https://example.com/profile-input";
                settings.Extraction.InputType = InputType.List;
                settings.Skip.SkipExisting = false;
                settings.Search.MaxStaleTime = 4321;
            });
            EngineSupervisor supervisor = CreateSupervisor(
                root,
                output,
                configureDownload: settings =>
                {
                    settings.Output.OnComplete = ["-- secret-command-value"];
                    settings.Spotify.ClientSecret = "secret-spotify-value";
                    settings.YouTube.ApiKey = "secret-youtube-value";
                },
                profiles: new ProfileCatalog { NamedProfiles = [profile] });
            var query = new SongQueryDto("Artist", "Track", "", "", -1, false);
            var options = new SubmissionOptionsDto(
                ProfileNames: ["review"],
                DownloadSettings: new DownloadSettingsPatchDto(
                    Skip: new SkipSettingsPatchDto(SkipExisting: true)));

            ResolveEffectiveSettingsResponseDto preview = supervisor.Submissions.ResolveEffectiveSettings(
                new ResolveEffectiveSettingsRequestDto(
                    generic ? new GenericSearchJobDraftDto("Artist Track") : new TrackSearchJobDraftDto(query),
                    options));

            Assert.AreEqual(generic ? ServerSearchSettingsBaselineKind.Generic : ServerSearchSettingsBaselineKind.Music, preview.Baseline);
            Assert.AreEqual(4321, preview.Settings.Values.Search?.MaxStaleTime);
            Assert.AreEqual(true, preview.Settings.Values.Skip?.SkipExisting);
            Assert.AreEqual("request", preview.Provenance["Skip.SkipExisting"]);
            CollectionAssert.AreEqual(new[] { "review" }, preview.AppliedProfiles.Where(profile => profile.Source == "selected").Select(profile => profile.Name).ToArray());
            Assert.AreEqual(1, preview.Settings.OnCompleteCommandCount);
            Assert.IsTrue(preview.Settings.SpotifyClientSecretConfigured);
            Assert.IsTrue(preview.Settings.YouTubeApiKeyConfigured);
            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
            Assert.IsTrue(supervisor.Submissions.GetProfiles().Single().HasInputSettings);

            string json = JsonSerializer.Serialize(
                preview,
                SockseekApiJson.CreateSerializerOptions());
            Assert.IsFalse(json.Contains("secret-command-value", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("secret-spotify-value", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("secret-youtube-value", StringComparison.Ordinal));

            runTask = supervisor.RunAsync(cts.Token);
            JobSummaryDto summary = generic
                ? await supervisor.Submissions.SubmitSearchJobAsync(new SubmitSearchJobRequestDto("Artist Track", Options: options), CancellationToken.None)
                : await supervisor.Submissions.SubmitTrackSearchJobAsync(new SubmitTrackSearchJobRequestDto(query, Options: options), CancellationToken.None);
            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            SearchJob? accepted = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(accepted?.Config);
            Assert.IsNotNull(query.Artist);
            Assert.IsNotNull(query.Title);
            Assert.AreEqual(generic ? "Artist Track" : new SongQuery { Artist = query.Artist, Title = query.Title }.ToString(noInfo: true), accepted.QueryText);
            CollectionAssert.AreEqual(preview.AppliedProfiles.ToArray(), summary.AppliedProfiles.ToArray());
            Assert.AreEqual(preview.Settings.Values.Search?.MaxStaleTime, accepted.Config.Search.MaxStaleTime);
            Assert.AreEqual(preview.Settings.Values.Skip?.SkipExisting, accepted.Config.Skip.SkipExisting);
            CollectionAssert.AreEqual(preview.Settings.Values.Search?.NecessaryCond?.Formats?.Replace?.ToArray(), accepted.Config.Search.NecessaryCond.Formats);
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveEffectiveSettings_AcceptsIncompleteIntentWithoutWeakeningSubmissionValidation()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-incomplete-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "out"));
        try
        {
            EngineSupervisor supervisor = CreateSupervisor(root, Path.Combine(root, "out"));
            var incomplete = new ExtractJobDraftDto("", "csv");
            ResolveEffectiveSettingsResponseDto provisional = supervisor.Submissions.ResolveEffectiveSettings(
                new ResolveEffectiveSettingsRequestDto(incomplete));
            ResolveEffectiveSettingsResponseDto concrete = supervisor.Submissions.ResolveEffectiveSettings(
                new ResolveEffectiveSettingsRequestDto(incomplete with
                {
                    Input = Path.Combine(root, "actual.csv"),
                }));

            Assert.IsTrue(provisional.IsProvisional);
            Assert.IsFalse(concrete.IsProvisional);
            Assert.AreEqual(concrete.Baseline, provisional.Baseline);
            Assert.AreEqual(concrete.Settings.Values.Search?.NecessaryCond?.StrictTitle,
                provisional.Settings.Values.Search?.NecessaryCond?.StrictTitle);
            Assert.ThrowsExactly<ArgumentException>(() => JobRequestMapper.CreateJob(incomplete));

            ResolveEffectiveSettingsResponseDto blankSearch = supervisor.Submissions.ResolveEffectiveSettings(
                new ResolveEffectiveSettingsRequestDto(new GenericSearchJobDraftDto("")));
            Assert.IsTrue(blankSearch.IsProvisional);
            Assert.AreEqual(ServerSearchSettingsBaselineKind.Generic, blankSearch.Baseline);
            Assert.ThrowsExactly<ArgumentException>(() =>
                JobRequestMapper.CreateJob(new GenericSearchJobDraftDto("")));
            Assert.ThrowsExactly<ArgumentException>(() => supervisor.Submissions.ResolveEffectiveSettings(
                new ResolveEffectiveSettingsRequestDto(new JobListJobDraftDto("incomplete child",
                    [new GenericSearchJobDraftDto("")]))));
            Assert.ThrowsExactly<ArgumentException>(() => supervisor.Submissions.ResolveEffectiveSettings(
                new ResolveEffectiveSettingsRequestDto(new SongJobDraftDto(new("Artist", "Track"),
                    Provenance: new(SourceMutation: new("unknown", "source"))))));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartFileDownloadsAsync_GeneralIntentCreatesRemoteFileJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-general-file-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(Path.Combine(root, "Artist"));
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(root, "Artist", "Track.mp3"), "data");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(root, output);
            runTask = supervisor.RunAsync(cts.Token);
            var search = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("Track.mp3"),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, search.JobId, ExpectedJobStatus.Succeeded);
            var downloads = await supervisor.StartFileDownloadsAsync(
                search.JobId,
                new StartFileDownloadsRequestDto(
                    [new FileCandidateRefDto("local", @"Artist\Track.mp3")],
                    RequestedMode: ServerExtractionMode.General),
                CancellationToken.None);

            Assert.IsNotNull(downloads);
            Assert.AreEqual(1, downloads.Count);
            Assert.AreEqual(ServerJobKind.RemoteFile, downloads[0].Kind);
            await WaitForJobStateAsync(supervisor, downloads[0].JobId, ExpectedJobStatus.Succeeded);
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_OrdinaryRemoteIntentCreatesRemoteDirectoryJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-remote-directory-" + Guid.NewGuid());
        string album = Path.Combine(root, "Organization", "Folder");
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(album, "One.mp3"), "1");
        File.WriteAllText(Path.Combine(album, "Two.mp3"), "2");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(root, output);
            runTask = supervisor.RunAsync(cts.Token);
            var search = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Organization", "Folder", "", "", false)),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, search.JobId, ExpectedJobStatus.Succeeded);
            var download = await supervisor.StartFolderDownloadAsync(
                search.JobId,
                new StartFolderDownloadRequestDto(
                    new AlbumFolderRefDto("local", @"Organization\Folder"),
                    RequestedMode: ServerExtractionMode.General),
                CancellationToken.None);

            Assert.IsNotNull(download);
            await WaitForJobStateAsync(supervisor, download.JobId, ExpectedJobStatus.Succeeded);
            Assert.IsNotNull(supervisor.GetRuntimeJob<RemoteDirectoryJob>(download.JobId));
            Assert.IsNull(supervisor.GetRuntimeJob<AlbumJob>(download.JobId));
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SubmitJobListAsync_RemoteDraftRejectsMusicOnlyOverrideBeforeAdmission()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-remote-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            var request = new SubmitJobListRequestDto(
                "invalid remote settings",
                [
                    new RemoteFileJobDraftDto(
                        new PeerFileTargetDto("Peer", @"Share\File.bin", 4, ".bin"),
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(WritePlaylist: true))),
                ]);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.Submissions.SubmitJobListAsync(request, CancellationToken.None));
            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SubmitJobListAsync_RemoteDraftAllowsPathBasedSkipExistingOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-remote-skip-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            var target = new PeerFileTargetDto("Peer", @"Share\File.bin", 4, ".bin");

            JobSummaryDto accepted = await supervisor.Submissions.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "valid remote settings",
                    [new RemoteFileJobDraftDto(
                        target,
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Skip: new SkipSettingsPatchDto(SkipExisting: false)))]),
                CancellationToken.None);

            Assert.AreEqual(ServerJobKind.JobList, accepted.Kind);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => supervisor.Submissions.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "invalid remote settings",
                    [new RemoteFileJobDraftDto(
                        target,
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Skip: new SkipSettingsPatchDto(SkipMode: ServerSkipMode.Name)))]),
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReviewOptionsCannotChangeOtherSubmissionsInTheSameWorkflow()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Sockseek-review-options-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var supervisor = CreateSupervisor(directory, directory);
            Guid workflowId = Guid.NewGuid();
            var query = new SongQueryDto("Artist", "Track", "", "", -1, false);
            SubmissionOptionsDto Options(int? stale = null) => new(WorkflowId: workflowId,
                DownloadSettings: stale is null ? null
                    : new DownloadSettingsPatchDto(Search: new SearchSettingsPatchDto(MaxStaleTime: stale)));
            await supervisor.Submissions.SubmitSongJobAsync(new(query, Options(111)), CancellationToken.None);
            await using (var planning = supervisor.Submissions.PlanDraftAsync(
                new JobListJobDraftDto("review",
                [new SongJobDraftDto(query, DownloadSettings: new DownloadSettingsPatchDto(
                    Search: new SearchSettingsPatchDto(MaxStaleTime: 333)))]),
                Options(222), null, CancellationToken.None).GetAsyncEnumerator())
            {
                Assert.IsTrue(await planning.MoveNextAsync());
                Assert.AreEqual(222, planning.Current.ReadSettings()!.Search.MaxStaleTime);
                var during = await supervisor.Submissions.SubmitSongJobAsync(new(query, Options()), CancellationToken.None);
                Assert.AreEqual(111, supervisor.GetRuntimeJob<SongJob>(during.JobId)!.Config.Search.MaxStaleTime);
                Assert.IsTrue(await planning.MoveNextAsync());
                Assert.AreEqual(333, planning.Current.ReadSettings()!.Search.MaxStaleTime);
            }
            var after = await supervisor.Submissions.SubmitSongJobAsync(new(query, Options()), CancellationToken.None);
            Assert.AreEqual(111, supervisor.GetRuntimeJob<SongJob>(after.JobId)!.Config.Search.MaxStaleTime);
            await foreach (var node in supervisor.Submissions.PlanDraftAsync(
                new SongJobDraftDto(query), Options(), null, CancellationToken.None))
                Assert.AreEqual(111, node.ReadSettings()!.Search.MaxStaleTime,
                    "Review still inherits the workflow's accepted options when it supplies no override.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task SoulseekDraftSettingsValidation_UsesTheEffectiveInterpretation()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => supervisor.Submissions.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "ordinary",
                    [new ExtractJobDraftDto(
                        "slsk://Peer/Share/File.bin",
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(WritePlaylist: true)))]),
                CancellationToken.None));

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await foreach (var _ in supervisor.Submissions.PlanDraftAsync(
                    new JobListJobDraftDto("ordinary",
                    [new RemoteFileJobDraftDto(new PeerFileTargetDto("Peer", @"Share\valid.txt", 4, ".txt")),
                     new JobListJobDraftDto("nested",
                    [new ExtractJobDraftDto("slsk://Peer/Share/File.bin",
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(WritePlaylist: true)))])]),
                    null, null, CancellationToken.None)) { }
            });

            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
            JobDraftDto[] music =
            [new ExtractJobDraftDto(
                "slsk://Peer/Share/File.mp3",
                DownloadSettings: new DownloadSettingsPatchDto(
                    Output: new OutputSettingsPatchDto(NameFormat: "{artist}/{title}", WritePlaylist: true),
                    Extraction: new ExtractionSettingsPatchDto(RequestedMode: ServerExtractionMode.Song)))];
            JobSummaryDto accepted = await supervisor.Submissions.SubmitJobListAsync(
                new SubmitJobListRequestDto("music", music), CancellationToken.None);

            Assert.AreEqual(ServerJobKind.JobList, accepted.Kind);
            Assert.AreNotEqual(Guid.Empty, accepted.WorkflowId);
            await foreach (var node in supervisor.Submissions.PlanDraftAsync(
                new JobListJobDraftDto("music", music), null, null, CancellationToken.None))
                Assert.AreEqual(Sockseek.Core.Planning.PlannedJobState.Ready, node.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SoulseekLink_InheritedMusicNameFormatFallsBackToRemoteFilename()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-inherited-format-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        string source = Path.Combine(root, "Share", "File.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(source, "data");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(
                root,
                output,
                settings => settings.Output.NameFormat = "{artist}/{filename}");
            runTask = supervisor.RunAsync(cts.Token);

            var submitted = await supervisor.Submissions.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(ToSoulseekFileUri(source), "soulseek-link"),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, submitted.JobId, ExpectedJobStatus.Succeeded);

            var extract = supervisor.GetRuntimeJob<ExtractJob>(submitted.JobId);
            var remote = extract?.Result as RemoteFileJob;
            Assert.IsNotNull(remote);
            await WaitForJobStateAsync(supervisor, remote.Id, ExpectedJobStatus.Succeeded);
            Assert.AreEqual("", remote.Config.Output.NameFormat);
            Assert.IsTrue(File.Exists(Path.Combine(output, "File.bin")));
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SoulseekLink_GenericAutoProfileOverridesInheritedMusicNameFormat()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-auto-format-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        string source = Path.Combine(root, "Share", "File.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(source, "data");
        var remotePatch = new SettingsPatch([new("Output.NameFormat", "{peer-username}/{filename}")]);
        var profiles = new ProfileCatalog
        {
            AutoProfiles =
            [
                new SettingsProfile
                {
                    Name = "generic-layout",
                    Condition = "download-mode == \"generic\"",
                    Download = remotePatch,
                },
            ],
        };
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(
                root,
                output,
                settings => settings.Output.NameFormat = "{artist}/{filename}",
                profiles: profiles);
            runTask = supervisor.RunAsync(cts.Token);

            var submitted = await supervisor.Submissions.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(ToSoulseekFileUri(source), "soulseek-link"),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, submitted.JobId, ExpectedJobStatus.Succeeded);

            var extract = supervisor.GetRuntimeJob<ExtractJob>(submitted.JobId);
            var remote = extract?.Result as RemoteFileJob;
            Assert.IsNotNull(remote);
            await WaitForJobStateAsync(supervisor, remote.Id, ExpectedJobStatus.Succeeded);
            Assert.AreEqual("{peer-username}/{filename}", remote.Config.Output.NameFormat);
            CollectionAssert.Contains(remote.Config.AppliedAutoProfiles.ToList(), "generic-layout");
            string expectedPath = Path.Combine(output, "local", "File.bin");
            Assert.IsTrue(
                File.Exists(expectedPath),
                $"Expected '{expectedPath}'. Actual files: {string.Join(", ", Directory.GetFiles(output, "*", SearchOption.AllDirectories))}");
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SoulseekLink_ExplicitMusicNameFormatIsRejectedBeforeAdmission()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-explicit-format-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);

            var exception = await Assert.ThrowsExactlyAsync<UnsupportedNameFormatVariableException>(() =>
                supervisor.Submissions.SubmitExtractJobAsync(
                    new SubmitExtractJobRequestDto(
                        "slsk://Peer/Share/File.bin",
                        Options: new SubmissionOptionsDto(
                            DownloadSettings: new DownloadSettingsPatchDto(
                                Output: new OutputSettingsPatchDto(
                                    NameFormat: "{artist|filename}")))),
                    CancellationToken.None));

            Assert.AreEqual("artist", exception.Variable);
            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolvedDirectoryDraft_DoesNotRejectLargeKnownByteCount()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-directory-admission-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            const long largeKnownBytes = 3L * 1024 * 1024 * 1024 * 1024;
            var supervisor = CreateSupervisor(root, root);
            var plan = new DirectoryTransferPlanDto(
                "Root",
                [new DirectoryTransferEntryDto(
                    new PeerFileTargetDto("Peer", @"Root\Huge.bin", largeKnownBytes, ".bin"),
                    [])],
                largeKnownBytes);

            var submitted = await supervisor.Submissions.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "large directory",
                    [new RemoteDirectoryJobDraftDto(Plan: plan)]),
                CancellationToken.None);

            Assert.AreNotEqual(Guid.Empty, submitted.JobId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FollowUpSelection_RejectsIncompatibleInterpretationBeforeSourceLookup()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-selection-mode-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.StartFileDownloadsAsync(
                    Guid.NewGuid(),
                    new StartFileDownloadsRequestDto(
                        [new FileCandidateRefDto("Peer", @"Share\File.bin")],
                        RequestedMode: ServerExtractionMode.Album),
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.StartFolderDownloadAsync(
                    Guid.NewGuid(),
                    new StartFolderDownloadRequestDto(
                        new AlbumFolderRefDto("Peer", "Share"),
                        RequestedMode: ServerExtractionMode.Song),
                    CancellationToken.None));

            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TerminalDaemonWorkflowWithoutPersistencePublishesFinalRemovalAndHasNoHistoryFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-retirement-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(output);
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(
                root,
                output,
                retireTerminalWorkflows: true);
            var engineCreated = new TaskCompletionSource<DownloadEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.EngineCreated += engine => engineCreated.TrySetResult(engine);
            var finalJobs = new ConcurrentBag<JobSummaryDto>();
            var retired = new TaskCompletionSource<StateUpdateBatchDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.StateStore.JobUpserted += summary =>
            {
                if (summary.LifecycleState == ServerJobLifecycleState.Terminal)
                    finalJobs.Add(summary);
            };
            supervisor.StateStore.StateBatchPublished += source =>
            {
                var batch = source.ToDto();
                // The daemon's compact active partition disappears at terminal
                // state, before the workflow-owned read projection is retired.
                if (batch.Scope.Kind == StateStreamScopeKind.Workflow
                    && batch.State.RemovedWorkflowIds.Contains(batch.Scope.WorkflowId!.Value))
                    retired.TrySetResult(batch);
            };
            runTask = supervisor.RunAsync(cts.Token);

            var submitted = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("missing"),
                CancellationToken.None);
            var engine = await engineCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var removal = await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(StateStreamScopeDto.Workflow(submitted.WorkflowId), removal.Scope);
            CollectionAssert.Contains(removal.State.RemovedJobIds.ToArray(), submitted.JobId);

            Assert.IsTrue(finalJobs.Any(job => job.JobId == submitted.JobId));
            Assert.IsNull(supervisor.StateStore.GetJobSummary(submitted.JobId));
            Assert.IsNull(supervisor.StateStore.GetWorkflowSummary(submitted.WorkflowId));
            // WorkflowRetired is published before Core releases its execution
            // indexes. Drain the finite queue before asserting that cleanup.
            engine.CompleteEnqueue();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(engine.GetJob(submitted.JobId));
            Assert.IsFalse(supervisor.CancelJob(submitted.JobId));
            Assert.IsNull(await supervisor.StartFileDownloadsAsync(
                submitted.JobId,
                new StartFileDownloadsRequestDto([
                    new FileCandidateRefDto("peer", @"Share\missing.mp3"),
                ]),
                CancellationToken.None));
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Restart_RedispatchesAcceptedRootsUntilRegistrationWithoutReplayingRegisteredWork()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-restart-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        // Keep uncaught preparation failures observable by the supervisor. The
        // normal daemon isolates ordinary per-root failures inside the engine.
        using var supervisor = CreateSupervisor(root, root, retireTerminalWorkflows: false);
        var accepted = new[]
        {
            await supervisor.Submissions.SubmitSearchJobAsync(new("registered first"), CancellationToken.None),
            await supervisor.Submissions.SubmitSearchJobAsync(new("preparation interrupted"), CancellationToken.None),
            await supervisor.Submissions.SubmitSearchJobAsync(new("still queued"), CancellationToken.None),
        };
        var interrupted = supervisor.GetRuntimeJob<SearchJob>(accepted[1].JobId)!;
        var originalSettings = interrupted.PlannedSettings!;
        var invalidSettings = originalSettings.ReadSettings();
        invalidSettings.Skip.SkipExisting = true;
        invalidSettings.Skip.SkipMode = (SkipMode)(-1);
        interrupted.PlannedSettings = JobSettingsSnapshot.Capture(invalidSettings);

        var completed = accepted.ToDictionary(job => job.JobId,
            _ => new TaskCompletionSource<JobSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously));
        var registrations = new ConcurrentDictionary<Guid, int>();
        var engines = new List<DownloadEngine>();
        object? session = null, shares = null, browses = null, profiles = null;
        supervisor.StateStore.JobUpserted += job =>
        {
            if (job.LifecycleState == ServerJobLifecycleState.Terminal)
                completed[job.JobId].TrySetResult(job);
        };
        supervisor.EngineCreated += engine =>
        {
            engines.Add(engine);
            engine.Events.JobRegistered += change =>
                registrations.AddOrUpdate(change.Job.Id, 1, (_, count) => count + 1);
            if (engines.Count == 1)
            {
                session = supervisor.Runtime.SoulseekSession;
                shares = supervisor.Runtime.Sharing;
                browses = supervisor.Runtime.PeerBrowses;
                profiles = supervisor.Runtime.UserProfiles;
            }
            else
            {
                Assert.AreEqual(2, engines.Count);
                Assert.AreSame(session, supervisor.Runtime.SoulseekSession);
                Assert.AreSame(shares, supervisor.Runtime.Sharing);
                Assert.AreSame(browses, supervisor.Runtime.PeerBrowses);
                Assert.AreSame(profiles, supervisor.Runtime.UserProfiles);
                Assert.AreEqual(ServerJobLifecycleState.Pending,
                    supervisor.StateStore.GetJobSummary(interrupted.Id)!.LifecycleState);
                interrupted.PlannedSettings = originalSettings;
            }
        };

        Task running = supervisor.RunAsync(cancellation.Token);
        try
        {
            var terminal = await Task.WhenAll(completed.Values.Select(completion => completion.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, engines.Count);
            Assert.IsTrue(terminal.All(job => job.TerminalOutcome == ServerJobTerminalOutcome.Succeeded));
            Assert.IsTrue(accepted.All(job => registrations.GetValueOrDefault(job.JobId) == 1),
                "Only unregistered roots may be redispatched; each accepted root runs once.");
            engines[^1].CompleteEnqueue();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Shutdown_JoinsRootCleanupBeforeDetachingState()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-stop-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaningUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Tests.ClientTests.MockSoulseekClient.FromLocalPaths(false, root);
        client.BeforeSearchAsync = async (_, ct) =>
        {
            started.TrySetResult();
            try { await never.Task.WaitAsync(ct); }
            finally
            {
                cleaningUp.TrySetResult();
                await releaseCleanup.Task;
            }
        };
        using var cancellation = new CancellationTokenSource();
        using var supervisor = CreateSupervisor(root, root, client: client);
        var stoppedJob = new TaskCompletionSource<JobSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.StateStore.JobUpserted += job =>
        {
            if (job.TerminalOutcome == ServerJobTerminalOutcome.Cancelled)
                stoppedJob.TrySetResult(job);
        };
        var running = supervisor.RunAsync(cancellation.Token);
        try
        {
            var accepted = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("held search"), CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await cleaningUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(running.IsCompleted, "The daemon must wait for roots still using the engine.");
            releaseCleanup.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(stoppedJob.Task.IsCompletedSuccessfully,
                "Terminal cancellation must reach the state owner before the engine detaches.");
            Assert.AreEqual(accepted.JobId, stoppedJob.Task.Result.JobId);
            Assert.AreEqual(ServerJobCancellationSource.InternalEngine,
                stoppedJob.Task.Result.CancellationSource);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    private static EngineSupervisor CreateSupervisor(
        string musicRoot,
        string outputDir,
        Action<DownloadSettings>? configureDownload = null,
        Action<EngineSettings>? configureEngine = null,
        ProfileCatalog? profiles = null,
        DownloadSettingsPatchDto? launchDownloadSettings = null,
        bool retireTerminalWorkflows = false,
        Soulseek.ISoulseekClient? client = null)
    {
        var engineSettings = new EngineSettings
        {
            MockFilesDir = musicRoot,
            MockFilesReadTags = false,
        };
        configureEngine?.Invoke(engineSettings);

        var baselineDownload = new DownloadSettings();
        var configuredDownload = SettingsCloner.Clone(baselineDownload);
        configuredDownload.Output.ParentDir = outputDir;
        configuredDownload.Output.NameFormat = "{foldername}/{filename}";
        configureDownload?.Invoke(configuredDownload);
        var operatorSettings = DownloadSettingsPatchDtoMapper.FromDifference(
            baselineDownload,
            configuredDownload);

        var options = Options.Create(new ServerOptions
        {
            Engine = engineSettings,
            OperatorDownloadSettings = operatorSettings,
            ClientFactory = client == null ? null : _ => client,
            LaunchDownloadSettings = launchDownloadSettings,
            Profiles = profiles ?? ProfileCatalog.Empty,
            Persistence = new ServerPersistenceOptions
            {
                DataDirectory = Path.Combine(musicRoot, ".sockseek-test-data"),
            },
        });

        // Most tests in this class exercise submission/execution mechanics and
        // inspect terminal runtime objects directly. Production daemon retirement
        // is covered separately; keeping it off here avoids turning those tests
        // into persistence-history integration tests.
        return new EngineSupervisor(
            options,
            persistence: null,
            loggerFactory: null,
            retireTerminalWorkflows: retireTerminalWorkflows);
    }

    private static SettingsProfile CreateProfile(string name, Action<DownloadSettings> applyDownload)
    {
        var before = new DownloadSettings();
        var after = new DownloadSettings();
        applyDownload(after);
        var patch = SettingsPatch.Difference(before, after) ?? SettingsPatch.Empty;
        return new SettingsProfile
        {
            Name = name,
            Download = patch,
        };
    }

    private static string ToSoulseekFileUri(string path)
        => "slsk://local/" + path.Replace('\\', '/');

    private static async Task WaitForJobStateAsync(
        EngineSupervisor supervisor,
        Guid jobId,
        ExpectedJobStatus expectedState,
        int timeoutMs = 15000)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobSummaryDto? lastSummary = null;

        void Observe(JobSummaryDto summary)
        {
            if (summary.JobId != jobId)
                return;
            lastSummary = summary;
            if (ProjectState(summary) == expectedState)
                reached.TrySetResult();
        }

        supervisor.StateStore.JobUpserted += Observe;
        try
        {
            JobSummaryDto? current = supervisor.StateStore.GetJobSummary(jobId);
            if (current != null)
                Observe(current);
            await reached.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return;
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            supervisor.StateStore.JobUpserted -= Observe;
        }

        var last = lastSummary == null
            ? "<missing>"
            : $"{ProjectState(lastSummary)} lifecycle={lastSummary.LifecycleState} activity={lastSummary.ActivityPhase} outcome={lastSummary.TerminalOutcome} skip={lastSummary.SkipReason} failure={lastSummary.FailureReason} message={lastSummary.FailureMessage} detail={lastSummary.FailureDetail}";
        Assert.Fail($"Timed out waiting for job {jobId} to reach state '{expectedState}'. Last summary: {last}.");
    }

    private static bool IsActive(SongJobPayloadDto song)
        => song.LifecycleState != ServerJobLifecycleState.Terminal;

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

    private static async Task WaitForConditionAsync(EngineSupervisor supervisor, Func<bool> condition, string failureMessage, int timeoutMs = 5000)
    {
        using var signal = new TestSignal()
            .Subscribe<JobSummaryDto>(handler => supervisor.StateStore.JobUpserted += handler, handler => supervisor.StateStore.JobUpserted -= handler)
            .Subscribe<WorkflowSummaryDto>(handler => supervisor.StateStore.WorkflowUpserted += handler, handler => supervisor.StateStore.WorkflowUpserted -= handler)
            .Subscribe<SearchStateDto>(handler => supervisor.StateStore.SearchUpdated += handler, handler => supervisor.StateStore.SearchUpdated -= handler);
        await signal.WaitUntilAsync(condition, failureMessage, timeoutMs);
    }

    private static List<SongJobPayloadDto> GetChildSongPayloads(EngineSupervisor supervisor, Guid parentJobId)
    {
        return supervisor.StateStore
            .GetJobs(new JobQuery(null, null, null, null, IncludeAll: true, ParentJobId: parentJobId))
            .Select(child => supervisor.StateStore.GetJobDetail(child.JobId)?.Payload)
            .OfType<SongJobPayloadDto>()
            .ToList();
    }

    private static List<JobSummaryDto> SearchChildren(EngineSupervisor supervisor, Guid workflowId)
        => supervisor.StateStore.GetJobs(new JobQuery(null, null, ServerJobKind.Search, workflowId, IncludeAll: true))
            .Where(summary => summary.ParentJobId != null)
            .OrderBy(summary => summary.DisplayId)
            .ToList();

    private static SearchJob SearchChildByQuery(EngineSupervisor supervisor, IReadOnlyList<JobSummaryDto> children, string queryText)
    {
        var summary = children.Single(child => child.QueryText?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true);
        var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
        Assert.IsNotNull(job);
        return job;
    }
}
