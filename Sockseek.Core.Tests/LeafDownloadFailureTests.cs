using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Tests.ClientTests;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Tests.Core;

[TestClass]
public sealed class LeafDownloadFailureTests
{
    [TestMethod]
    [DataRow("transport cancellation")]
    [DataRow("size mismatch")]
    [DataRow("wrapped missing file")]
    [DataRow("permission denied")]
    public async Task PermanentTransferFailureDoesNotRetryAndFailsOnlyItsRoot(string failureKind)
    {
        string output = CreateTempDirectory();
        var failedTarget = Target("cancel-peer", @"Share\Failed.bin");
        var completedTarget = new PeerFileTarget(new PeerFileIdentity("ok-peer", @"Share\Completed.bin"), null, ".bin");
        var failed = new RemoteFileJob(failedTarget);
        var completed = new RemoteFileJob(completedTarget);
        var client = new MockSoulseekClient([
            Response(failedTarget),
            Response(completedTarget),
        ]);
        client.BeforeDownloadStartsAsync = (username, _, _) =>
            username == failedTarget.Username
                ? Task.FromException(failureKind switch
                {
                    "size mismatch" => new TransferSizeMismatchException(16, 32),
                    "wrapped missing file" => new SoulseekClientException("cannot open file", new FileNotFoundException()),
                    "permission denied" => new UnauthorizedAccessException(),
                    _ => new OperationCanceledException("transport ended early"),
                })
                : Task.CompletedTask;
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var settings = new DownloadSettings
        {
            Output = { ParentDir = output, NameFormat = "" },
            Transfer = { NoIncompleteExt = true, UnknownErrorRetries = 2 },
        };
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings));

        long? reportedSize = null;
        engine.Events.DownloadProgress += progress =>
        {
            if (progress.Transfer.Target?.Username == "ok-peer") reportedSize = progress.TotalBytes;
        };
        try
        {
            engine.Enqueue(failed, settings);
            engine.Enqueue(completed, settings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Failed, failed.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, failed.FailureReason);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, completed.TerminalOutcome);
            Assert.IsTrue(File.Exists(completed.DownloadPath));
            Assert.AreEqual(16, completed.FileSize, "Negotiated size must reach the job even when the target size was unknown.");
            Assert.AreEqual(16, reportedSize);
            Assert.AreEqual(2, client.DownloadCallCount, "Only one attempt per file; permanent failures must not be retried.");
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteFileRequestedCancellation_RemainsCancelled()
    {
        string output = CreateTempDirectory();
        var target = Target("cancel-peer", @"Share\Cancelled.bin");
        var job = new RemoteFileJob(target);
        var client = new MockSoulseekClient([Response(target)]);
        client.BeforeDownloadStartsAsync = (_, _, _) =>
        {
            job.Cancel(JobCancellationSource.UserRequestedJob);
            return Task.FromException(new OperationCanceledException(job.Cts!.Token));
        };
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var settings = new DownloadSettings
        {
            Output = { ParentDir = output, NameFormat = "" },
            Transfer = { NoIncompleteExt = true, UnknownErrorRetries = 0 },
        };
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings));

        try
        {
            engine.Enqueue(job, settings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Cancelled, job.TerminalOutcome);
            Assert.AreEqual(JobCancellationSource.UserRequestedJob, job.CancellationSource);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static PeerFileTarget Target(string username, string filename)
        => new(new PeerFileIdentity(username, filename), 16, ".bin");

    private static SearchResponse Response(PeerFileTarget target)
        => new(
            target.Username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100,
            queueLength: 0,
            fileList: [new Soulseek.File(1, target.Filename, target.Size ?? 16, target.Extension)]);

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "sockseek-leaf-failure-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
