using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;
using Tests.ClientTests;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Tests.Core;

[TestClass]
public sealed class ExactFileTransferRunnerTests
{
    private readonly List<DownloadFileOperations> fileWorkers = [];

    [TestCleanup]
    public void CloseFileWorkers()
    {
        foreach (var closer in fileWorkers) closer.Dispose();
    }

    [TestMethod]
    public async Task FinalOutputBarrierPrecedesDeferredDocumentsWithoutOvertakingEarlierFileWork()
    {
        string root = CreateTempDirectory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var files = new DownloadFileOperations(1);
        Task earlier = files.WithCacheAsync(_ => { entered.SetResult(); release.Wait(); return true; }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var song = new SongJob(new SongQuery { Title = "Track" });
            var queue = new JobList("Tracks", [song]);
            var background = new DownloadOutputFile(Path.Combine(root, "background.csv"), queue, OutputFileKind.Index, false);
            var finishing = new DownloadOutputFile(Path.Combine(root, "finishing.csv"), queue, OutputFileKind.Index, false);
            song.SetDone("track.mp3");
            background.Observe(song);
            finishing.Observe(song);
            files.RequestOutputUpdate(background);
            files.RequestOutputUpdate(finishing);
            Task<Exception?> final = files.FlushOutputAsync(finishing);
            Task<bool> later = files.WithCacheAsync(_ =>
                File.Exists(finishing.Path) && !File.Exists(background.Path)).AsTask();
            Assert.IsFalse(final.IsCompleted, "Finalization cannot overtake the earlier serialized operation.");
            release.Set();
            Assert.IsNull(await final.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(await later.WaitAsync(TimeSpan.FromSeconds(5)),
                "A final output must not wait for idle document maintenance or other workflows to finish.");
            Assert.IsNull(await files.FlushOutputAsync(background));
        }
        finally
        {
            release.Set();
            await earlier.WaitAsync(TimeSpan.FromSeconds(5));
            files.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OutputFinalizationIncludesChangesArrivingDuringAnAttemptAndRecoversIndependently()
    {
        string root = CreateTempDirectory();
        string indexPath = Path.Combine(root, "index.csv");
        Directory.CreateDirectory(indexPath); // A real output-publication failure.
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new OutputAttemptLogger(() => { entered.SetResult(); release.Wait(); });
        using var files = new DownloadFileOperations(2, logger);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var queue = new JobList("Tracks", [song]);
        var index = new DownloadOutputFile(indexPath, queue, OutputFileKind.Index, false);
        var playlist = new DownloadOutputFile(Path.Combine(root, "playlist.m3u8"), queue, OutputFileKind.Playlist, false);
        song.SetDone("first.mp3");
        index.Observe(song);
        files.RequestOutputUpdate(index);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // This attempt has started, so its completion cannot acknowledge
            // the later path. Healthy close work still uses the spare worker.
            await files.CloseAsync(new ClosingStream(() => { })).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(indexPath);
            song.DownloadPath = "latest.mp3";
            index.Observe(song);
            playlist.Observe(song);
            files.RequestOutputUpdate(playlist);
            Task<Exception?> finished = files.FlushOutputAsync(index);
            Assert.IsFalse(finished.IsCompleted);
            release.Set();
            Assert.IsNull(await finished.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsNull(await files.FlushOutputAsync(playlist));
            Assert.AreEqual("latest.mp3", new DownloadOutputFile(indexPath, queue,
                OutputFileKind.Index, true, readOnly: true).PreviousRunResult(song)?.DownloadPath);
            CollectionAssert.AreEqual(new[] { "latest.mp3" }, File.ReadAllLines(playlist.Path));
            Assert.AreEqual(1, logger.Warnings);
            Assert.AreEqual(1, logger.Recoveries);
        }
        finally
        {
            release.Set();
            files.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OutputAttemptLogger(Action firstWarning) : ILogger
    {
        public int Warnings;
        public int Recoveries;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning && Interlocked.Increment(ref Warnings) == 1) firstWarning();
            if (level == LogLevel.Information && formatter(state, exception).Contains("recovered")) Recoveries++;
        }
    }

    [TestMethod]
    public async Task FileOperationsRemainIndependentAndDrainAfterFailure()
    {
        using var closer = new DownloadFileOperations(2);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocked = closer.CloseAsync(new ClosingStream(() =>
        {
            Assert.IsFalse(Thread.CurrentThread.IsThreadPoolThread, "Blocking file work must not occupy shared request workers.");
            started.SetResult();
            release.Wait();
        })).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = new IOException("close failed");
            Task failed = closer.CloseAsync(new ClosingStream(() => throw failure)).AsTask();
            Assert.AreSame(failure, await Assert.ThrowsAsync<IOException>(
                () => failed.WaitAsync(TimeSpan.FromSeconds(5))));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await closer.CloseAsync(new ClosingStream(() => closed.SetResult())).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(closed.Task.IsCompletedSuccessfully);
            Assert.IsFalse(blocked.IsCompleted, "Another file's blocked close remains independent.");
        }
        finally
        {
            release.Set();
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task FileOperationShutdownDrainsAlreadyAcceptedWork()
    {
        using var closer = new DownloadFileOperations(1);
        string root = CreateTempDirectory();
        string nested = Path.Combine(root, "partial");
        Directory.CreateDirectory(nested);
        string incomplete = Path.Combine(nested, "file.incomplete");
        await File.WriteAllTextAsync(incomplete, "partial payload");
        string sibling = Path.Combine(root, "keep.bin");
        await File.WriteAllTextAsync(sibling, "independent payload");
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = closer.CloseAsync(new ClosingStream(() =>
        {
            Assert.IsFalse(Thread.CurrentThread.IsThreadPoolThread, "Blocking file work must not occupy shared request workers.");
            started.SetResult();
            release.Wait();
        })).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task queued = closer.CloseAsync(new ClosingStream(() => { })).AsTask();
            Assert.IsFalse(queued.IsCompleted);
            Task cleanup = closer.DeleteIncompleteAsync(incomplete, root).AsTask();
            Assert.IsFalse(cleanup.IsCompleted, "Cleanup shares the owned workers, away from request continuations.");
            Assert.IsTrue(File.Exists(incomplete));
            Task shutdown = Task.Run(closer.Dispose);
            release.Set();
            await Task.WhenAll(first, queued, cleanup, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(File.Exists(incomplete));
            Assert.IsFalse(Directory.Exists(nested));
            Assert.AreEqual("independent payload", await File.ReadAllTextAsync(sibling));
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QueuedOutputChangesDoNotBlockIndependentCloseAndPublishCopiesInOrder()
    {
        using var files = new DownloadFileOperations(2);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string root = CreateTempDirectory();
        string original = Path.Combine(root, "original.bin");
        string copy = Path.Combine(root, "copy.bin");
        var target = Target("Exact Peer", @"Share\File.bin");
        byte[] payload = Enumerable.Range(0, (int)target.Size!.Value).Select(i => (byte)i).ToArray();
        Task first = files.WithCacheAsync(cache =>
        {
            Assert.IsFalse(Thread.CurrentThread.IsThreadPoolThread);
            File.WriteAllBytes(original, payload);
            entered.SetResult();
            release.Wait();
            cache.Publish(original, target, Guid.NewGuid());
            return true;
        }).AsTask();
        Task<ExactFileTransferOutcome>? copying = null;
        Task? failed = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = new IOException("Independent output update failed.");
            failed = files.WithCacheAsync<bool>(_ => throw failure).AsTask();
            var client = new MockSoulseekClient([Response(target.Username, target.Filename)]);
            var (runner, _, _) = CreateRunner(client, files);
            var settings = new TransferSettings { NoIncompleteExt = true };
            copying = runner.DownloadFile(target, copy, new RemoteFileJob(target), settings, root, 30_000);
            Assert.IsFalse(copying.IsCompleted);
            Assert.IsFalse(failed.IsCompleted);
            // Neither queued update may occupy the spare I/O worker while
            // the first operation is blocked on its filesystem work.
            await files.CloseAsync(new ClosingStream(() => { })).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(File.Exists(copy));
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(failure, await Assert.ThrowsAsync<IOException>(() => failed));
            Assert.AreEqual(ExactFileTransferStatus.Completed,
                (await copying.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(copy));
            Assert.AreEqual(0, client.DownloadCallCount, "The queued copy must see the first operation's published source.");
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (failed != null) { try { await failed; } catch (IOException) { } }
            if (copying != null) await copying.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OwnedOutputPreservesBufferedWritesSeeksAndClose()
    {
        using var files = new DownloadFileOperations(2);
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "nested", "output.bin");
        Stream? output = null;
        try
        {
            output = await files.OpenAsync(path);
            byte[] prefix = [1, 2, 3];
            byte[] tail = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
            await output.WriteAsync(prefix.AsMemory());
            Assert.AreEqual(3L, output.Position);
            await output.WriteAsync(tail, 0, tail.Length);
            await output.FlushAsync();
            Assert.AreEqual(8195L, output.Position);
            output.Seek(4096, SeekOrigin.Begin);
            await output.WriteAsync(new byte[] { 201 }.AsMemory());
            await files.CloseAsync(output);
            output = null;
            byte[] expected = prefix.Concat(tail).ToArray();
            expected[4096] = 201;
            CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(path));

            // A shorter retry must discard the previous payload, including its tail.
            output = await files.OpenAsync(path);
            Assert.AreEqual(0L, output.Length);
            Assert.AreEqual(0L, output.Position);
            await output.WriteAsync(new byte[] { 42 }.AsMemory());
            await files.CloseAsync(output);
            output = null;
            CollectionAssert.AreEqual(new byte[] { 42 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (output != null) await files.CloseAsync(output);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ClosingStream(Action close) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) close();
            base.Dispose(disposing);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SongAndRemoteFile_UseTheSameExactWireIdentityAndOutcome(bool noIncompleteExtension)
    {
        const string username = " Peer Case ";
        const string filename = "Share\\Cafe\u0301\\File\u001B\n.bin ";
        var response = Response(username, filename);
        var client = new MockSoulseekClient([response]);
        var (runner, _, events) = CreateRunner(client);
        var target = Target(username, filename);
        var song = new SongJob(new SongQuery { Title = "File" }) { ExactTarget = target };
        var remote = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        var requests = new List<(string Username, string Filename)>();
        var outputStreams = new List<Stream>();
        var closedBeforeCompletion = new List<bool>();
        client.DownloadStreamOpened = outputStreams.Add;
        events.TransferCompleted += _ => closedBeforeCompletion.Add(outputStreams.All(stream => !stream.CanWrite));
        client.BeforeDownloadStartsAsync = (peer, path, _) =>
        {
            requests.Add((peer, path));
            return Task.CompletedTask;
        };

        try
        {
            var settings = new TransferSettings { NoIncompleteExt = noIncompleteExtension };
            var songOutcome = await runner.DownloadFile(
                target, Path.Combine(root, "song.bin"), song, settings, root,
                30_000, publishToDuplicateCache: false);
            var remoteOutcome = await runner.DownloadFile(
                target, Path.Combine(root, "remote.bin"), remote, settings, root,
                30_000, publishToDuplicateCache: false);

            Assert.AreEqual(ExactFileTransferStatus.Completed, songOutcome.Status);
            Assert.AreEqual(ExactFileTransferStatus.Completed, remoteOutcome.Status);
            Assert.AreEqual(target.Identity, songOutcome.Result!.Target.Identity);
            Assert.AreEqual(target.Identity, remoteOutcome.Result!.Target.Identity);
            Assert.AreEqual(2, client.DownloadCallCount);
            Assert.AreEqual(0, client.SearchCallCount);
            Assert.IsTrue(requests.All(request => request == (username, filename)));
            Assert.AreEqual(target.Size, song.BytesTransferred);
            Assert.AreEqual(target.Size, remote.BytesTransferred);
            CollectionAssert.AreEqual(new[] { true, true }, closedBeforeCompletion);
            foreach (string outputPath in new[] { songOutcome.Result.OutputPath, remoteOutcome.Result.OutputPath })
            {
                Assert.AreEqual(target.Size, new FileInfo(outputPath).Length);
                Assert.IsFalse(System.IO.File.Exists(outputPath + ".incomplete"));
                using var exclusive = System.IO.File.Open(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.AreEqual(target.Size, exclusive.Length);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnknownFailure_RetriesOnlyTheConfiguredExtraAttempts()
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        var client = new MockSoulseekClient([Response(username, filename)], failingUsers: [username]);
        var (runner, active, _) = CreateRunner(client);
        var target = Target(username, filename);
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        var settings = new TransferSettings { UnknownErrorRetries = 2, NoIncompleteExt = true };

        try
        {
            await Assert.ThrowsExactlyAsync<SoulseekClientException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner, settings, root,
                30_000, publishToDuplicateCache: false));

            Assert.AreEqual(3, client.DownloadCallCount);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingPeer_IsNotRetriedAndLeavesNoLiveTransfer()
    {
        var client = new MockSoulseekClient([]);
        var (runner, active, _) = CreateRunner(client);
        var target = Target("Missing", @"Share\File.bin");
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        var settings = new TransferSettings { UnknownErrorRetries = 5, NoIncompleteExt = true };

        try
        {
            await Assert.ThrowsExactlyAsync<UserNotFoundException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner, settings, root,
                30_000, publishToDuplicateCache: false));

            Assert.AreEqual(1, client.DownloadCallCount);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransportCancellationWithoutRequestedToken_IsTransferFailure()
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        var client = new MockSoulseekClient([Response(username, filename)]);
        client.BeforeDownloadStartsAsync = (_, _, _) =>
            throw new OperationCanceledException("transport ended early");
        var (runner, active, events) = CreateRunner(client);
        var target = Target(username, filename);
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        TransferFailedChange? failed = null;
        TransferCancelledChange? cancelled = null;
        TransferAttemptFailedChange? failedAttempt = null;
        events.TransferFailed += change => failed = change;
        events.TransferCancelled += change => cancelled = change;
        events.TransferAttemptFailed += change => failedAttempt = change;

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner,
                new TransferSettings { NoIncompleteExt = true }, root,
                30_000, publishToDuplicateCache: false));

            Assert.IsNotNull(failed);
            Assert.AreEqual(TransferFailureReason.PeerFailure, failed.Reason);
            Assert.IsNotNull(failedAttempt);
            Assert.IsNull(cancelled);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequestedTokenCancellation_IsTransferCancellation(bool songOwner)
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        using var cancellation = new CancellationTokenSource();
        var client = new MockSoulseekClient([Response(username, filename)]);
        client.BeforeDownloadStartsAsync = (_, _, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        var (runner, active, events) = CreateRunner(client);
        var target = Target(username, filename);
        FileDownloadJob owner = songOwner
            ? new SongJob(new SongQuery { Title = "File" })
            {
                ActiveCandidate = new FileCandidate(target, new SearchPeerSnapshot(username, 1, 1234, false)),
            }
            : new RemoteFileJob(target);
        string root = CreateTempDirectory();
        TransferFailedChange? failed = null;
        TransferCancelledChange? cancelled = null;
        events.TransferFailed += change => failed = change;
        events.TransferCancelled += change => cancelled = change;

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner,
                new TransferSettings { NoIncompleteExt = true }, root,
                30_000, cancellation.Token,
                publishToDuplicateCache: false));

            Assert.IsNotNull(cancelled);
            Assert.AreEqual(TransferCancellationReason.Requested, cancelled.Reason);
            Assert.IsNull(failed);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
            if (owner is SongJob song)
            {
                song.ActiveCandidate = null;
                Assert.AreEqual(target.Identity, song.LastAttemptedCandidate?.Target.Identity);
                Assert.IsNull(song.ResolvedTarget);
                Assert.IsNull(song.ExactTarget);
                var snapshot = (Sockseek.Core.Snapshots.SongJobSnapshotPayload)
                    Sockseek.Core.Snapshots.CoreSnapshotFactory.CreateJob(song, 1).Payload;
                Assert.AreEqual(filename, snapshot.LastAttemptedCandidate?.Filename);
                Assert.AreEqual(1234, snapshot.LastAttemptedCandidate?.Peer.UploadSpeed);
                Assert.AreEqual(false, snapshot.LastAttemptedCandidate?.Peer.HasFreeUploadSlot);
                Assert.IsNull(snapshot.ResolvedTarget);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailureAfterProgress_ReportsTheCurrentTransferredByteCount()
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        var client = new MockSoulseekClient([Response(username, filename)]);
        client.AfterDownloadProgress = (_, _) => throw new IOException("failed after progress");
        var (runner, _, events) = CreateRunner(client);
        var target = Target(username, filename);
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        TransferFailedChange? failed = null;
        events.TransferFailed += change => failed = change;

        try
        {
            var settings = new TransferSettings { NoIncompleteExt = true, UnknownErrorRetries = 0 };
            await Assert.ThrowsExactlyAsync<IOException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner,
                settings, root,
                30_000, publishToDuplicateCache: false));

            Assert.IsNotNull(failed);
            Assert.AreEqual(target.Size, owner.BytesTransferred);
            Assert.AreEqual(target.Size, failed.Transfer.BytesTransferred);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReusedCopy_RespectsPublicationPolicyBeforeOutputOrganization(bool publishCopy)
    {
        var target = Target("Peer", @"Share\File.bin");
        using var files = new DownloadFileOperations(2);
        var client = new MockSoulseekClient([Response(target.Username, target.Filename)]);
        var (runner, _, _) = CreateRunner(client, files);
        string root = CreateTempDirectory();
        string original = Path.Combine(root, "final.bin");
        string copy = Path.Combine(root, "staged", "copy.bin");
        var settings = new TransferSettings { NoIncompleteExt = true };
        var song = new SongJob(new SongQuery { Title = "File" })
        {
            ActiveCandidate = new FileCandidate(target, new SearchPeerSnapshot(target.Username, 1, 1234, true)),
        };
        try
        {
            System.IO.File.WriteAllBytes(original, new byte[checked((int)target.Size!.Value)]);
            await files.WithCacheAsync(cache => { cache.Publish(original, target, song.WorkflowId); return true; });
            var outcome = await runner.DownloadFile(target, copy, song, settings, root,
                30_000, publishToDuplicateCache: publishCopy);
            Assert.AreEqual(ExactFileTransferStatus.Completed, outcome.Status);
            Assert.AreEqual(0, client.DownloadCallCount);
            Assert.AreEqual(target.Size, new FileInfo(copy).Length);
            Assert.IsNull(song.LastAttemptedCandidate, "A local reused copy is not a new peer attempt.");
            var reusable = await ReusableAsync(files, target);
            Assert.IsNotNull(reusable);
            Assert.AreEqual(publishCopy ? copy : original, reusable.OutputPath,
                "An unpublished staging copy must not replace the stable source used by concurrent duplicates.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PublishingReplacement_InvalidatesEveryPriorIdentityForThatPath()
    {
        string root = CreateTempDirectory();
        string output = Path.Combine(root, "file.bin");
        using var files = new DownloadFileOperations(2);
        var first = Target("first-peer", @"Share\First.bin");
        var second = Target("second-peer", @"Share\Second.bin");
        try
        {
            System.IO.File.WriteAllBytes(output, new byte[16]);
            await files.WithCacheAsync(cache => { cache.Publish(output, first, Guid.NewGuid()); return true; });
            System.IO.File.WriteAllBytes(output, Enumerable.Repeat((byte)1, 16).ToArray());
            await files.WithCacheAsync(cache => { cache.Publish(output, second, Guid.NewGuid()); return true; });

            Assert.IsNull(await ReusableAsync(files, first));
            var retained = await ReusableAsync(files, second);
            Assert.IsNotNull(retained);
            Assert.AreEqual(Path.GetFullPath(output), retained.OutputPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConcurrentDifferentTargets_SharingOutput_DoNotOverwriteWhenSkipExistingApplies()
    {
        var firstTarget = Target("first-peer", @"Share\First.bin");
        var secondTarget = Target("second-peer", @"Share\Second.bin");
        var client = new MockSoulseekClient(
        [
            Response(firstTarget.Username, firstTarget.Filename),
            Response(secondTarget.Username, secondTarget.Filename),
        ]);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeDownloadStartsAsync = async (_, remotePath, cancellationToken) =>
        {
            if (remotePath != firstTarget.Filename)
                return;
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        };
        using var files = new DownloadFileOperations(2);
        var (runner, _, _) = CreateRunner(client, files);
        string root = CreateTempDirectory();
        string output = Path.Combine(root, "file.bin");
        var settings = new TransferSettings { NoIncompleteExt = true };

        try
        {
            Task<ExactFileTransferOutcome> first = runner.DownloadFile(
                firstTarget,
                output,
                new RemoteFileJob(firstTarget),
                settings,
                root,
                30_000,
                allowOverwrite: true,
                protectPublishedOutput: true);
            await firstEntered.Task;
            Task<ExactFileTransferOutcome> second = runner.DownloadFile(
                secondTarget,
                output,
                new RemoteFileJob(secondTarget),
                settings,
                root,
                30_000,
                allowOverwrite: true,
                protectPublishedOutput: true);

            releaseFirst.TrySetResult();
            Assert.AreEqual(ExactFileTransferStatus.Completed, (await first).Status);
            Assert.AreEqual(ExactFileTransferStatus.AlreadyExists, (await second).Status);
            Assert.AreEqual(1, client.DownloadCallCount);
            Assert.IsNotNull(await ReusableAsync(files, firstTarget));
            Assert.IsNull(await ReusableAsync(files, secondTarget));
        }
        finally
        {
            releaseFirst.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SkipExistingDoesNotCountACachedSamePathAsAnotherSuccessfulDownload()
    {
        using var files = new DownloadFileOperations(2);
        var target = Target("Peer", @"Share\File.bin");
        var client = new MockSoulseekClient([Response(target.Username, target.Filename)]);
        var (runner, _, _) = CreateRunner(client, files);
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "output.bin");
        var settings = new TransferSettings();
        try
        {
            Assert.AreEqual(ExactFileTransferStatus.Completed, (await runner.DownloadFile(target, path,
                new RemoteFileJob(target), settings, root, 30_000)).Status);
            Assert.AreEqual(ExactFileTransferStatus.AlreadyExists, (await runner.DownloadFile(target, path,
                new RemoteFileJob(target), settings, root, 30_000, allowOverwrite: false)).Status);
            Assert.AreEqual(1, client.DownloadCallCount);
            Assert.AreEqual(target.Size, new FileInfo(path).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ValueTask<DownloadFileOperations.Cache.CachedDownload?> ReusableAsync(
        DownloadFileOperations files, PeerFileTarget target)
        => files.WithCacheAsync(cache => cache.TryGetReusable(target, out var found) ? found : null);

    private (ExactPeerFileTransferRunner Runner, ActiveDownloadTracker Active, DownloadEvents Events)
        CreateRunner(MockSoulseekClient client, DownloadFileOperations? files = null)
    {
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var active = new ActiveDownloadTracker();
        var events = new DownloadEvents();
        var closer = files ?? new DownloadFileOperations(engineSettings.ConcurrentJobs);
        if (files == null) fileWorkers.Add(closer);
        return (
            new ExactPeerFileTransferRunner(
                client,
                TestHelpers.CreateMockClientManager(client, engineSettings),
                active,
                events,
                new StaleDownloadCoordinator(active), closer),
            active,
            events);
    }

    private static SearchResponse Response(string username, string filename)
        => new(
            username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100,
            queueLength: 0,
            fileList: [new Soulseek.File(1, filename, 16, ".bin")]);

    private static PeerFileTarget Target(string username, string filename)
        => new(new PeerFileIdentity(username, filename), 16, ".bin");

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "sockseek-exact-runner-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
