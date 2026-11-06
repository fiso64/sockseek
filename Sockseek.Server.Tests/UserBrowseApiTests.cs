using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class UserBrowseApiTests
{
    [TestMethod]
    public async Task TypedApi_ReusesBrowsesAndPagesImmutableArtifact()
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-user-browse-api", Guid.NewGuid().ToString("N"));
        string mockFiles = Path.Combine(root, "shares");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(mockFiles, "Alpha", "Disc"));
        Directory.CreateDirectory(Path.Combine(mockFiles, "Beta"));
        await File.WriteAllTextAsync(Path.Combine(mockFiles, "Alpha", "Disc", "two.flac"), "22");
        await File.WriteAllTextAsync(Path.Combine(mockFiles, "Alpha", "Disc", "one.flac"), "1");
        await File.WriteAllTextAsync(Path.Combine(mockFiles, "Beta", "other.mp3"), "333");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        var serverOptions = new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = mockFiles,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = data,
            },
        };
        await using var app = global::Tests.TestServerHost.Build([], serverOptions, url);
        bool stopped = false;

        try
        {
            await app.StartAsync();
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var api = new SockseekApiClient(http);
            // An immediate first request must work; waiting for an internal
            // service here used to hide a race with BackgroundService startup.
            var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            Assert.IsNotNull(supervisor.Runtime.PeerBrowses);
            Assert.IsNotNull(supervisor.Runtime.UserProfiles);

            UserBrowseDto started = await api.StartUserBrowseAsync("local");
            UserBrowseDto complete = await WaitForCompleteAsync(api, started.BrowseId);
            Assert.AreEqual(3, complete.FileCount);

            UserBrowseDto reused = await api.StartUserBrowseAsync("local");
            Assert.AreEqual(complete.BrowseId, reused.BrowseId);
            Assert.AreEqual(UserBrowseState.Complete, reused.State);

            await using (var live = new SockseekLiveClient(http))
            {
                await live.StartUserBrowseAsync(complete.BrowseId);
                Assert.AreEqual(complete, live.Store.GetUserBrowse(complete.BrowseId));
                await live.StopUserBrowseAsync(complete.BrowseId);
                Assert.IsNull(live.Store.GetUserBrowse(complete.BrowseId));
            }

            PageDto<BrowseDirectoryEntryDto> roots1 = await api.GetUserShareDirectoriesAsync(
                complete.BrowseId, limit: 1);
            PageDto<BrowseDirectoryEntryDto> roots2 = await api.GetUserShareDirectoriesAsync(
                complete.BrowseId, cursor: roots1.NextCursor, limit: 1);
            CollectionAssert.AreEqual(
                new[] { "Alpha", "Beta" },
                roots1.Items.Concat(roots2.Items).Select(item => item.Name).ToArray());

            BrowseDirectoryEntryDto alpha = roots1.Items.Single();
            SockseekApiRequestException invalidLimit =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.GetUserShareDirectoriesAsync(
                        complete.BrowseId, cursor: roots1.NextCursor, limit: 0));
            Assert.AreEqual("invalid-request", invalidLimit.Code);

            SockseekApiRequestException invalidCursor =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.GetUserShareDirectoriesAsync(
                        complete.BrowseId, cursor: " ", limit: 1));
            Assert.AreEqual("invalid-cursor", invalidCursor.Code);

            BrowseDirectoryEntryDto disc = (await api.GetUserShareDirectoriesAsync(
                complete.BrowseId, parentId: alpha.DirectoryId)).Items.Single();
            PageDto<BrowseFileEntryDto> files1 = await api.GetUserShareFilesAsync(
                complete.BrowseId, disc.DirectoryId, limit: 1);
            PageDto<BrowseFileEntryDto> files2 = await api.GetUserShareFilesAsync(
                complete.BrowseId, disc.DirectoryId, cursor: files1.NextCursor, limit: 1);
            CollectionAssert.AreEqual(
                new[] { "one.flac", "two.flac" },
                files1.Items.Concat(files2.Items).Select(item => item.File.Name).ToArray());
            PageDto<BrowseFileEntryDto> allFiles = await api.GetUserShareFilesAsync(
                complete.BrowseId, disc.DirectoryId, limit: int.MaxValue);
            CollectionAssert.AreEqual(
                new[] { "one.flac", "two.flac" },
                allFiles.Items.Select(item => item.File.Name).ToArray());
            Assert.IsNull(allFiles.NextCursor);

            BrowseSearchPageDto searchFirst = await api.SearchUserSharesAsync(
                complete.BrowseId, "FLAC", limit: 1);
            var searchRows = new List<BrowseSearchEntryDto>(searchFirst.Items);
            string? searchCursor = searchFirst.NextCursor;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            while (searchCursor is not null)
            {
                Assert.IsTrue(cursors.Add(searchCursor), "Browse search paging must advance, not repeat a cursor.");
                BrowseSearchPageDto page = await api.SearchUserSharesAsync(
                    complete.BrowseId, "FLAC", searchCursor, limit: 1);
                Assert.AreEqual(searchFirst.BrowseRevision, page.BrowseRevision);
                Assert.AreEqual(searchFirst.PublicMatchingFileCount, page.PublicMatchingFileCount);
                searchRows.AddRange(page.Items);
                Assert.AreEqual(searchRows.Count,
                    searchRows.Select(row => (row.Kind, row.EntryId)).Distinct().Count(),
                    "Browse search pages must not overlap.");
                searchCursor = page.NextCursor;
            }
            Assert.AreEqual(2, searchFirst.MatchingFileCount);
            Assert.AreEqual(3, searchFirst.MatchingBytes);
            Assert.AreEqual(2, searchRows.Count(row => row.Kind == BrowseSearchEntryKind.File));
            CollectionAssert.AreEqual(
                new[] { "one.flac", "two.flac" },
                searchRows.Where(row => row.Kind == BrowseSearchEntryKind.File)
                    .Select(row => row.Name).Order().ToArray());

            BrowseDirectoryEntryDto beta = roots2.Items.Single();
            BrowseFileEntryDto betaFile = (await api.GetUserShareFilesAsync(
                complete.BrowseId, beta.DirectoryId)).Items.Single();
            string output = Path.Combine(root, "downloads");
            string existingPath = Path.Combine(output, "Alpha", "Disc", "one.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
            await File.WriteAllTextAsync(existingPath, "keep-existing");
            UserShareSelectionDto[] selections =
            [
                new UserShareDirectorySelectionDto(alpha.DirectoryId),
                new UserShareDirectorySelectionDto(disc.DirectoryId),
                new UserShareFileSelectionDto(files1.Items[0].FileId),
                new UserShareFileSelectionDto(betaFile.FileId),
            ];
            var submissionOptions = new SubmissionOptionsDto(OutputParentDir: output);
            Guid requestId = Guid.NewGuid();
            var downloadRequest = new StartUserShareDownloadsRequestDto(
                requestId,
                selections,
                submissionOptions);
            StartUserShareDownloadsResponseDto submitted = await api.StartUserShareDownloadsAsync(
                complete.BrowseId,
                downloadRequest);
            StartUserShareDownloadsResponseDto repeated = await api.StartUserShareDownloadsAsync(
                complete.BrowseId,
                downloadRequest);
            Assert.AreEqual(1, submitted.Resolution.CanonicalDirectoryRoots);
            Assert.AreEqual(1, submitted.Resolution.StandaloneFiles);
            Assert.AreEqual(3, submitted.Resolution.TotalPublicFiles);
            Assert.AreEqual(6, submitted.Resolution.TotalPublicBytes);
            Assert.AreEqual(2, submitted.Resolution.RedundantSelectionsRemoved);
            Assert.AreEqual(Path.GetFullPath(output), submitted.Resolution.OutputParent);
            Assert.AreEqual(requestId, submitted.Workflow.SubmissionId);
            var accepted = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(requestId);
            Assert.IsNotNull(accepted);
            Assert.IsNotNull(accepted.CommitReceiptJson);
            Assert.IsNotNull(accepted.CommitFingerprint);
            Assert.AreEqual(submitted.Workflow.JobId, repeated.Workflow.JobId);
            Assert.AreEqual(submitted.Resolution, repeated.Resolution);

            SockseekApiRequestException idempotencyConflict =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.StartUserShareDownloadsAsync(
                        complete.BrowseId,
                        downloadRequest with
                        {
                            Selections = [new UserShareFileSelectionDto(betaFile.FileId)],
                        }));
            Assert.AreEqual(HttpStatusCode.Conflict, idempotencyConflict.StatusCode);
            Assert.AreEqual("idempotency-conflict", idempotencyConflict.Code);

            await WaitUntilAsync(() =>
                File.Exists(Path.Combine(output, "Alpha", "Disc", "one.flac"))
                && File.Exists(Path.Combine(output, "Alpha", "Disc", "two.flac"))
                && File.Exists(Path.Combine(output, "Beta", "other.mp3")));
            Assert.AreEqual("keep-existing", await File.ReadAllTextAsync(existingPath));

            string overwriteOutput = Path.Combine(root, "overwrite-downloads");
            string overwritePath = Path.Combine(overwriteOutput, "Beta", "other.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(overwritePath)!);
            await File.WriteAllTextAsync(overwritePath, "replace-me");
            var overwriteOptions = new SubmissionOptionsDto(
                OutputParentDir: overwriteOutput,
                DownloadSettings: new DownloadSettingsPatchDto(
                    Skip: new SkipSettingsPatchDto(SkipExisting: false)));
            var stateStore = app.Services.GetRequiredService<EngineStateStore>();
            using var overwriteChanged = new global::Tests.TestSignal()
                .Subscribe<JobSummaryDto>(handler => stateStore.JobUpserted += handler,
                    handler => stateStore.JobUpserted -= handler);
            var overwrite = await api.StartUserShareDownloadsAsync(
                complete.BrowseId,
                new StartUserShareDownloadsRequestDto(
                    Guid.NewGuid(),
                    [new UserShareFileSelectionDto(betaFile.FileId)],
                    overwriteOptions));
            JobDetailDto? overwriteJob = null;
            await overwriteChanged.WaitUntilAsync(async () =>
            {
                overwriteJob = await api.GetJobDetailAsync(overwrite.Workflow.JobId);
                return overwriteJob?.Summary.LifecycleState == ServerJobLifecycleState.Terminal;
            }, "The overwrite workflow did not finish.", timeoutMs: 10_000);
            Assert.AreEqual(ServerJobTerminalOutcome.Succeeded, overwriteJob!.Summary.TerminalOutcome,
                overwriteJob.Summary.FailureMessage);
            Assert.AreEqual("333", await File.ReadAllTextAsync(overwritePath));

            await WaitUntilAsync(async () =>
            {
                IReadOnlyList<JobSummaryDto> workflowJobs = await api.GetJobsAsync(
                    new JobQuery(null, null, null, submitted.Workflow.WorkflowId, IncludeAll: true));
                return workflowJobs.Count(job => job.Kind == ServerJobKind.RemoteDirectory) == 2;
            });

            PageDto<UserBrowseDto> resources = await api.GetUserBrowsesAsync(
                username: "local", state: UserBrowseState.Complete, limit: 1);
            Assert.AreEqual(complete.BrowseId, resources.Items.Single().BrowseId);

            UserBrowseDto refreshed = await api.StartUserBrowseAsync("local", refresh: true);
            refreshed = await WaitForCompleteAsync(api, refreshed.BrowseId);
            Assert.AreNotEqual(complete.BrowseId, refreshed.BrowseId);

            SockseekApiRequestException crossBrowseIdempotency =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.StartUserShareDownloadsAsync(refreshed.BrowseId, downloadRequest));
            Assert.AreEqual(HttpStatusCode.Conflict, crossBrowseIdempotency.StatusCode);
            Assert.AreEqual("idempotency-conflict", crossBrowseIdempotency.Code);

            SockseekApiRequestException crossGeneration =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.GetUserShareDirectoriesAsync(
                        refreshed.BrowseId, cursor: roots1.NextCursor, limit: 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, crossGeneration.StatusCode);
            Assert.AreEqual("invalid-cursor", crossGeneration.Code);

            SockseekApiRequestException crossSearchGeneration =
                await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    api.SearchUserSharesAsync(
                        refreshed.BrowseId,
                        "FLAC",
                        searchFirst.NextCursor,
                        limit: 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, crossSearchGeneration.StatusCode);
            Assert.AreEqual("invalid-cursor", crossSearchGeneration.Code);

            // Model a lost response followed by restart and removal of temporary
            // browse artifacts. Only accepted intent/receipt can satisfy this retry.
            await app.StopAsync();
            stopped = true;
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.Combine(data, "peer-browses"), recursive: true);
            await using var restarted = global::Tests.TestServerHost.Build([], serverOptions, url);
            try
            {
                await restarted.StartAsync();
                using var restartedHttp = await global::Tests.TestServerHost.CreateHttpClientAsync(restarted);
                var restartedApi = new SockseekApiClient(restartedHttp);
                var missing = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    restartedApi.GetUserBrowseAsync(complete.BrowseId));
                Assert.AreEqual(HttpStatusCode.Gone, missing.StatusCode);
                var restored = await restartedApi.StartUserShareDownloadsAsync(complete.BrowseId, downloadRequest);
                Assert.AreEqual(submitted.Workflow.JobId, restored.Workflow.JobId);
                Assert.AreEqual(submitted.Workflow.SubmissionId, restored.Workflow.SubmissionId);
                Assert.AreEqual(submitted.Resolution, restored.Resolution);
                var conflict = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                    restartedApi.StartUserShareDownloadsAsync(complete.BrowseId, downloadRequest with { ExcludedFileIds = [betaFile.FileId] }));
                Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
            }
            finally { await restarted.StopAsync(); }

        }
        finally
        {
            if (!stopped) await app.StopAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<UserBrowseDto> WaitForCompleteAsync(
        SockseekApiClient api,
        Guid browseId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        UserBrowseDto resource;
        do
        {
            resource = await api.GetUserBrowseAsync(browseId);
            if (resource.State == UserBrowseState.Complete)
                return resource;
            if (resource.State is UserBrowseState.Failed or UserBrowseState.Cancelled)
                Assert.Fail($"Browse ended as {resource.State}: {resource.Failure?.Code} {resource.Failure?.Error}");
            await Task.Delay(20);
        }
        while (DateTimeOffset.UtcNow < deadline);
        Assert.Fail($"The browse did not complete; last state was {resource.State}/{resource.Phase} "
                    + $"revision {resource.Revision}, failure {resource.Failure?.Code}: {resource.Failure?.Error}.");
        throw new InvalidOperationException();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsTrue(condition(), "The daemon did not initialize peer browsing.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsTrue(await condition(), "The expected persisted job state did not become available.");
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
