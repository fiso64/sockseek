using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Jobs;
using Sldl.Core.Settings;
using Sldl.Server;
using System.Collections.Concurrent;

namespace Tests.Server;

[TestClass]
public class EngineSupervisorTests
{
    [TestMethod]
    public async Task StartFileDownloadsAsync_ReusesWorkflowAndSetsSourceJob()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);

            var tracks = supervisor.GetFileResults(searchSummary.JobId);
            Assert.IsNotNull(tracks);
            Assert.AreEqual(1, tracks.Items.Count);

            var downloadSummary = await supervisor.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto([tracks.Items[0].Ref]),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(1, downloadSummary.Count);
            var downloadedSummary = downloadSummary[0];
            Assert.AreEqual(searchSummary.WorkflowId, downloadedSummary.WorkflowId);
            Assert.IsNull(downloadedSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadedSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, downloadedSummary.JobId, ServerProtocol.JobStates.Done);

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
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_ReusesWorkflowAndFindsAlbumByFolderPath()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);

            var albums = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);
            Assert.AreEqual("local", albums.Items[0].Username);
            Assert.AreEqual(@"Artist\Album", albums.Items[0].FolderPath);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(albums.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.WorkflowId, downloadSummary.WorkflowId);
            Assert.IsNull(downloadSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ServerProtocol.JobStates.Done);

            var detail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(detail);
            Assert.IsNull(detail.Summary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, detail.Summary.SourceJobId);

            var workflowTree = supervisor.StateStore.GetWorkflowTree(searchSummary.WorkflowId);
            Assert.IsNotNull(workflowTree);
            Assert.AreEqual(2, workflowTree.Jobs.Count);
            CollectionAssert.AreEquivalent(
                new[] { searchSummary.JobId, downloadSummary.JobId },
                workflowTree.Jobs.Select(job => job.Summary.JobId).ToArray());

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Track One.mp3", "02. Track Two.mp3" }, downloaded);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFileDownloadsAsync_CanDownloadSingleFileFromAlbumSearch()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);

            var files = supervisor.GetFileResults(searchSummary.JobId);
            Assert.IsNotNull(files);
            var selected = files.Items.Single(file => file.Filename.EndsWith("02. Track Two.mp3", StringComparison.OrdinalIgnoreCase));

            var downloads = await supervisor.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto([selected.Ref]),
                CancellationToken.None);

            Assert.IsNotNull(downloads);
            Assert.AreEqual(1, downloads.Count);
            Assert.AreEqual(searchSummary.WorkflowId, downloads[0].WorkflowId);

            await WaitForJobStateAsync(supervisor, downloads[0].JobId, ServerProtocol.JobStates.Done);

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
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_DoesNotInheritSearchSubmissionSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Search.NoBrowseFolder = false;
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false),
                    Options: new SubmissionOptionsDto(
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Search: new SearchSettingsPatchDto(NoBrowseFolder: true)))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);
            var searchJob = supervisor.StateStore.GetJob<SearchJob>(searchSummary.JobId);
            Assert.IsNotNull(searchJob);
            Assert.IsTrue(searchJob.Config?.Search.NoBrowseFolder);

            var folders = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(folders);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(folders.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            await WaitForConditionAsync(
                () => supervisor.StateStore.GetJob<AlbumJob>(downloadSummary.JobId)?.Config != null,
                "Timed out waiting for album download settings.");

            var albumJob = supervisor.StateStore.GetJob<AlbumJob>(downloadSummary.JobId);
            Assert.IsNotNull(albumJob);
            Assert.IsFalse(albumJob.Config?.Search.NoBrowseFolder, "Download should use default settings, not the search submission delta.");

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ServerProtocol.JobStates.Done);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_CancelWorkflowMarksUnfinishedPayloadFilesCancelled()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
            File.WriteAllBytes(Path.Combine(albumDir, $"{i:00}. Artist - Track {i:00}.mp3"), new byte[1024]);

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(
                musicRoot,
                outputDir,
                configureDownload: settings => settings.Search.NoBrowseFolder = true,
                configureEngine: settings => settings.MockFilesSlow = true);
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);

            var albums = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(albums.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);

            await WaitForConditionAsync(
                () =>
                {
                    return GetChildSongPayloads(supervisor, downloadSummary.JobId)
                        .Any(file => file.State == ServerProtocol.JobStates.Downloading) == true;
                },
                "Timed out waiting for album file downloads to start.");

            var activeDetail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(activeDetail);
            var activeFiles = GetChildSongPayloads(supervisor, downloadSummary.JobId);
            var activeAlbumPayload = activeDetail.Payload as AlbumJobPayloadDto;
            Assert.IsNotNull(activeAlbumPayload);
            Assert.IsTrue(activeAlbumPayload.SelectedFolderFileCount >= activeFiles.Count);
            var cancellableFile = activeFiles.FirstOrDefault(file =>
                file.AvailableActions?.Any(action => action.Kind == ServerResourceActionKind.Cancel) == true);
            Assert.IsNotNull(cancellableFile, "Active album payload files should expose cancel actions.");

            var childSongJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, ServerJobKind.Song, downloadSummary.WorkflowId, IncludeAll: true))
                .Where(summary => summary.ParentJobId == downloadSummary.JobId)
                .ToList();
            Assert.IsTrue(childSongJobs.Count > 0, "Album payload songs should be registered jobs.");
            Assert.IsFalse(
                supervisor.StateStore.GetJobs(new JobQuery(null, ServerJobKind.Song, downloadSummary.WorkflowId, IncludeAll: false))
                    .Any(summary => summary.ParentJobId == downloadSummary.JobId),
                "Album payload songs should stay out of the default job list.");

            Assert.IsNotNull(cancellableFile.JobId);
            Assert.IsTrue(supervisor.CancelJob(cancellableFile.JobId.Value), "Album payload file should be cancellable by job id.");
            await WaitForConditionAsync(
                () =>
                {
                    return GetChildSongPayloads(supervisor, downloadSummary.JobId)
                        .Any(file => file.JobId == cancellableFile.JobId && file.State == ServerProtocol.JobStates.Failed && file.FailureReason == ServerProtocol.FailureReasons.Cancelled) == true;
                },
                "Timed out waiting for album file cancellation.");

            var cancelledAlbumPayload = supervisor.StateStore.GetJobDetail(downloadSummary.JobId)?.Payload as AlbumJobPayloadDto;
            Assert.IsNotNull(cancelledAlbumPayload);
            Assert.IsTrue(cancelledAlbumPayload.SelectedFolderFileCount >= activeFiles.Count);
            Assert.IsTrue(cancelledAlbumPayload.SelectedFolderCompletedFileCount >= 1);
            Assert.IsTrue(cancelledAlbumPayload.SelectedFolderFailedFileCount >= 1);

            var cancelled = supervisor.CancelWorkflow(downloadSummary.WorkflowId);
            Assert.IsTrue(cancelled > 0, "CancelWorkflow should cancel the active album download job.");

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ServerProtocol.JobStates.Failed);

            var cancelledDetail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(cancelledDetail);
            var files = GetChildSongPayloads(supervisor, downloadSummary.JobId);
            Assert.AreEqual(12, files.Count);
            Assert.IsFalse(
                files.Any(file => file.State is ServerProtocol.JobStates.Pending or ServerProtocol.JobStates.Searching or ServerProtocol.JobStates.Downloading),
                "Cancelled album payload should not expose stale active file states.");
            Assert.IsTrue(
                files.Any(file => file.State == ServerProtocol.JobStates.Failed && file.FailureReason == ServerProtocol.FailureReasons.Cancelled),
                "Cancelled album payload should mark unfinished files as cancelled.");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesJobAndWorkflowUpserts_ForSubmittedJobs()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var seenJobIds = new ConcurrentBag<Guid>();
            var seenWorkflowIds = new ConcurrentBag<Guid>();
            supervisor.StateStore.JobUpserted += summary => seenJobIds.Add(summary.JobId);
            supervisor.StateStore.WorkflowUpserted += summary => seenWorkflowIds.Add(summary.WorkflowId);

            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);

            CollectionAssert.Contains(seenJobIds.ToList(), searchSummary.JobId);
            CollectionAssert.Contains(seenWorkflowIds.ToList(), searchSummary.WorkflowId);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesSearchUpdated_ForSearchJobResultsAndCompletion()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var updates = new ConcurrentBag<SearchUpdatedDto>();
            supervisor.StateStore.SearchUpdated += update => updates.Add(update);

            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);
            await WaitForConditionAsync(
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
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartRetrieveFolderAsync_CompletesQueuedRetrieveJobAndPreservesWorkflow()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Search.NecessaryCond.StrictTitle = true;
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "Track One", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ServerProtocol.JobStates.Done);

            var beforeRetrieve = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: true);
            Assert.IsNotNull(beforeRetrieve);
            Assert.AreEqual(1, beforeRetrieve.Items.Count);
            Assert.AreEqual(1, beforeRetrieve.Items[0].Files?.Count);

            var retrieveSummary = await supervisor.StartRetrieveFolderAsync(
                searchSummary.JobId,
                new RetrieveFolderRequestDto(beforeRetrieve.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(retrieveSummary);
            Assert.AreEqual(searchSummary.WorkflowId, retrieveSummary.WorkflowId);
            Assert.IsNull(retrieveSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, retrieveSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, retrieveSummary.JobId, ServerProtocol.JobStates.Done);

            var retrieveDetail = supervisor.StateStore.GetJobDetail(retrieveSummary.JobId);
            Assert.IsNotNull(retrieveDetail);
            var payload = retrieveDetail.Payload as RetrieveFolderJobPayloadDto;
            Assert.IsNotNull(payload);
            Assert.AreEqual(1, payload.NewFilesFoundCount);

            var workflowTree = supervisor.StateStore.GetWorkflowTree(searchSummary.WorkflowId);
            Assert.IsNotNull(workflowTree);
            Assert.AreEqual(2, workflowTree.Jobs.Count);
            CollectionAssert.AreEquivalent(
                new[] { searchSummary.JobId, retrieveSummary.JobId },
                workflowTree.Jobs.Select(job => job.Summary.JobId).ToArray());

            var afterRetrieve = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: true);
            Assert.IsNotNull(afterRetrieve);
            Assert.AreEqual(2, afterRetrieve.Items[0].Files?.Count);
            Assert.IsTrue(afterRetrieve.Items[0].IsFullyRetrieved);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(afterRetrieve.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ServerProtocol.JobStates.Done);

            var retrieveJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, ServerJobKind.RetrieveFolder, searchSummary.WorkflowId, IncludeAll: true));
            Assert.AreEqual(1, retrieveJobs.Count, "Starting a download from a fully retrieved folder should not browse it again.");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task ExtractJobPayload_ExposesResultDraftForTypedResubmission()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Extraction.IsAlbum = true;
                settings.Search.NoBrowseFolder = true;
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var extractSummary = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    "Artist Album",
                    "String",
                    AutoStartExtractedResult: false),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, extractSummary.JobId, ServerProtocol.JobStates.Done);

            var extractDetail = supervisor.StateStore.GetJobDetail(extractSummary.JobId);
            Assert.IsNotNull(extractDetail);
            var extractPayload = extractDetail.Payload as ExtractJobPayloadDto;
            Assert.IsNotNull(extractPayload);
            var albumDraft = extractPayload.ResultDraft as AlbumJobDraftDto;
            Assert.IsNotNull(albumDraft);

            var started = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    albumDraft.AlbumQuery,
                    new SubmissionOptionsDto(WorkflowId: extractSummary.WorkflowId)),
                CancellationToken.None);

            Assert.AreEqual(ServerJobKind.Search, started.Kind);
            Assert.AreEqual(extractSummary.WorkflowId, started.WorkflowId);

            await WaitForJobStateAsync(supervisor, started.JobId, ServerProtocol.JobStates.Done);

            var workflowTree = supervisor.StateStore.GetWorkflowTree(extractSummary.WorkflowId);
            Assert.IsNotNull(workflowTree);
            Assert.AreEqual(2, workflowTree.Jobs.Count);
            Assert.IsTrue(workflowTree.Jobs.Any(job => job.Summary.JobId == extractSummary.JobId));
            Assert.IsTrue(workflowTree.Jobs.Any(job => job.Summary.JobId == started.JobId));

            var albums = supervisor.GetFolderResults(started.JobId, includeFiles: true);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);
            Assert.AreEqual(@"Artist\Album", albums.Items[0].FolderPath);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesServerAutoProfileFromClientContext()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var profile = CreateProfile("my-interactive", settings => settings.Search.MaxStaleTime = 9999999)
                with { Condition = "interactive && album" };
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false),
                    new SubmissionOptionsDto(ProfileContext: new Dictionary<string, bool>
                    {
                        ["interactive"] = true,
                    })),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ServerProtocol.JobStates.Done);

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(9999999, job.Config?.Search.MaxStaleTime);
            CollectionAssert.Contains(job.Config?.AppliedAutoProfiles?.ToList(), "my-interactive");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ExpandsServerProfileOutputPaths()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var profile = CreateProfile("album-inbox", settings => settings.Output.ParentDir = "~/Music/Inbox")
                with { Condition = "album" };
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ServerProtocol.JobStates.Done);

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
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
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_LaunchDownloadSettingsOverrideServerAutoProfiles()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        string launchOutputDir = Path.Combine(musicRoot, "launch-out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(launchOutputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var profile = CreateProfile("album-profile", settings =>
                {
                    settings.Output.ParentDir = "~/Music/Inbox";
                    settings.Search.MaxStaleTime = 999999;
                })
                with { Condition = "album" };
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
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ServerProtocol.JobStates.Done);

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(Path.GetFullPath(launchOutputDir), job.Config?.Output.ParentDir);
            Assert.AreEqual(222, job.Config?.Search.MaxStaleTime);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesServerNamedProfile()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var named = CreateProfile("long-search", settings => settings.Search.MaxStaleTime = 123456);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var profiles = supervisor.GetProfiles();
            Assert.AreEqual(1, profiles.Count);
            Assert.AreEqual("long-search", profiles[0].Name);

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(ProfileNames: ["long-search"])),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ServerProtocol.JobStates.Done);

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(123456, job.Config?.Search.MaxStaleTime);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesClientDownloadSettingsDeltaAfterProfiles()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var named = CreateProfile("short-search", settings => settings.Search.MaxStaleTime = 111);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var baseline = new DownloadSettings();
            var cliSettings = SettingsCloner.Clone(baseline);
            cliSettings.Search.MaxStaleTime = 222;
            cliSettings.Search.NecessaryCond.Formats = ["flac"];

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["short-search"],
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromDifference(baseline, cliSettings))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ServerProtocol.JobStates.Done);

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(222, job.Config?.Search.MaxStaleTime);
            CollectionAssert.AreEqual(new[] { "flac" }, job.Config?.Search.NecessaryCond.Formats);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ClientDeltaCanSetBuiltInDefaultValueOverProfile()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var named = CreateProfile("no-skip", settings => settings.Skip.SkipExisting = false);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["no-skip"],
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Skip: new SkipSettingsPatchDto(SkipExisting: true)))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ServerProtocol.JobStates.Done);

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.IsTrue(job.Config?.Skip.SkipExisting);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ClientDeltaCanAppendToProfileListSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var named = CreateProfile("base-command", settings => settings.Output.OnComplete = ["first"]);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["base-command"],
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(
                                OnComplete: new CollectionPatchDto<string>(Append: ["second"]))))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ServerProtocol.JobStates.Done);

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            CollectionAssert.AreEqual(new[] { "first", "second" }, job.Config?.Output.OnComplete);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    private static EngineSupervisor CreateSupervisor(
        string musicRoot,
        string outputDir,
        Action<DownloadSettings>? configureDownload = null,
        Action<EngineSettings>? configureEngine = null,
        ProfileCatalog? profiles = null,
        DownloadSettingsPatchDto? launchDownloadSettings = null)
    {
        var engineSettings = new EngineSettings
        {
            MockFilesDir = musicRoot,
            MockFilesReadTags = false,
        };
        configureEngine?.Invoke(engineSettings);

        var defaultDownload = new DownloadSettings
        {
            Output =
            {
                ParentDir = outputDir,
                NameFormat = "{foldername}/{filename}",
            },
        };
        configureDownload?.Invoke(defaultDownload);

        var options = Options.Create(new ServerOptions
        {
            Engine = engineSettings,
            DefaultDownload = defaultDownload,
            LaunchDownloadSettings = launchDownloadSettings,
            Profiles = profiles ?? ProfileCatalog.Empty,
        });

        return new EngineSupervisor(options);
    }

    private static SettingsProfile CreateProfile(string name, Action<DownloadSettings> applyDownload)
    {
        var patch = new DownloadSettingsPatch();
        patch.Add(applyDownload);
        return new SettingsProfile
        {
            Name = name,
            Download = patch,
        };
    }

    private static async Task WaitForJobStateAsync(EngineSupervisor supervisor, Guid jobId, ServerJobState expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var summary = supervisor.StateStore.GetJobSummary(jobId);
            if (summary?.State == expectedState)
                return;

            await Task.Delay(50, timeout.Token);
        }

        Assert.Fail($"Timed out waiting for job {jobId} to reach state '{expectedState}'.");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            if (condition())
                return;

            await Task.Delay(50, timeout.Token);
        }

        Assert.Fail(failureMessage);
    }

    private static List<SongJobPayloadDto> GetChildSongPayloads(EngineSupervisor supervisor, Guid parentJobId)
    {
        var parent = supervisor.StateStore.GetJobDetail(parentJobId);
        return parent?.Children
            .Select(child => supervisor.StateStore.GetJobDetail(child.JobId)?.Payload)
            .OfType<SongJobPayloadDto>()
            .ToList() ?? [];
    }
}
