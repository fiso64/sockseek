using System.Collections.Concurrent;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class UploadCoordinatorTests
{
    [TestMethod]
    public async Task AdjacentUploadDirectoryRunsHaveDistinctOpaqueGroupRefs()
    {
        await using var fixture = await CatalogFixture.CreateAsync(
            ("One.bin", "one"), ("Two.bin", "two"), ("Three.bin", "three"));
        var protocol = new BlockingProtocolInvoker();
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);

        var first = await coordinator.AdmitAsync("alice", null, @"Public\One.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await coordinator.AdmitAsync("alice", null, @"Public\Two.bin");
        var otherPeer = await coordinator.AdmitAsync("bob", null, @"Public\One.bin");
        var later = await coordinator.AdmitAsync("alice", null, @"Public\Three.bin");

        var snapshots = coordinator.Snapshot().ToDictionary(snapshot => snapshot.TransferId);
        string firstRef = snapshots[first.TransferId!.Value].GroupRef!;
        Assert.IsFalse(string.IsNullOrWhiteSpace(firstRef));
        Assert.AreEqual(firstRef, snapshots[second.TransferId!.Value].GroupRef);
        Assert.AreNotEqual(firstRef, snapshots[otherPeer.TransferId!.Value].GroupRef);
        Assert.AreNotEqual(firstRef, snapshots[later.TransferId!.Value].GroupRef);
        Assert.IsTrue(snapshots.Values.All(snapshot => snapshot.GroupDisplayPath == @"Public"));

        protocol.Release.TrySetResult();
        await WaitForStateAsync(coordinator, later.TransferId!.Value, UploadTransferState.Completed);
    }

    [TestMethod]
    public async Task QueuedCancellation_TerminalizesWithZeroAttempts()
    {
        await using var fixture = await CatalogFixture.CreateAsync(
            ("One.bin", "one"),
            ("Two.bin", "two"));
        var protocol = new BlockingProtocolInvoker();
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);

        var first = await coordinator.AdmitAsync(
            "alice",
            new IPEndPoint(IPAddress.Loopback, 1),
            @"Public\One.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await coordinator.AdmitAsync(
            "bob",
            new IPEndPoint(IPAddress.Loopback, 2),
            @"Public\Two.bin");

        UploadTransferSnapshot queued = coordinator.Snapshot()
            .Single(item => item.TransferId == second.TransferId);
        Assert.AreEqual(UploadTransferState.Queued, queued.State);
        Assert.IsNull(queued.Attempt);

        Assert.IsTrue(coordinator.Cancel(second.TransferId!.Value));
        UploadTransferSnapshot cancelled = coordinator.Snapshot()
            .Single(item => item.TransferId == second.TransferId);
        Assert.AreEqual(UploadTransferState.Cancelled, cancelled.State);
        Assert.IsNull(cancelled.Attempt);

        protocol.Release.TrySetResult();
        await WaitForStateAsync(coordinator, first.TransferId!.Value, UploadTransferState.Completed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueuedFileChangesFailOnlyTheAffectedUpload(bool deleted)
    {
        await using var fixture = await CatalogFixture.CreateAsync(
            ("Active.bin", "active"), ("Changed.bin", "changed"), ("Healthy.bin", "healthy"));
        var protocol = new BlockingProtocolInvoker();
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);
        await coordinator.AdmitAsync("alice", null, @"Public\Active.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = await coordinator.AdmitAsync("bob", null, @"Public\Changed.bin");
        var healthy = await coordinator.AdmitAsync("carol", null, @"Public\Healthy.bin");
        var file = (await fixture.ResolveFileAsync(RemotePathKey.Create(@"Public\Changed.bin")))!;
        string path = Path.Combine(file.Root.LocalPath, file.File.RelativePath);
        if (deleted) File.Delete(path);
        else await File.WriteAllTextAsync(path, "replacement has a different size");

        protocol.Release.TrySetResult();
        var failed = await WaitForStateAsync(coordinator, changed.TransferId!.Value, UploadTransferState.Failed);
        await WaitForStateAsync(coordinator, healthy.TransferId!.Value, UploadTransferState.Completed);

        Assert.AreEqual(UploadFailureReason.Unavailable, failed.FailureReason);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failed.FailureMessage));
        Assert.AreEqual(0, failed.BytesTransferred);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(6)]
    public async Task ResumeFactory_ReturnsExactlyRemainingBytesAndCreatesOneAttempt(int startOffset)
    {
        await using var fixture = await CatalogFixture.CreateAsync(("Track.bin", "abcdef"));
        var protocol = new ReadingProtocolInvoker(startOffset);
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);

        var admission = await coordinator.AdmitAsync(
            "alice",
            new IPEndPoint(IPAddress.Loopback, 1),
            @"public\track.BIN");
        UploadTransferSnapshot completed = await WaitForStateAsync(
            coordinator,
            admission.TransferId!.Value,
            UploadTransferState.Completed);

        CollectionAssert.AreEqual("abcdef"u8.ToArray()[startOffset..], protocol.Bytes);
        Assert.AreEqual(@"public\track.BIN", protocol.RemotePath);
        Assert.AreEqual(protocol.RemotePath, completed.RemotePath);
        Assert.IsNotNull(completed.Attempt);
        Assert.AreEqual(1, completed.Attempt.Number);
        Assert.AreEqual(6, completed.BytesTransferred);
        Assert.AreEqual(6 - startOffset, completed.Attempt.BytesTransferred);
        Assert.AreEqual(startOffset < 6 ? 100 : 0, coordinator.MeasuredUploadSpeed,
            "An EOF-only resume provides no new speed sample.");
    }

    [TestMethod]
    [DataRow(UploadFailureReason.Unavailable)]
    [DataRow(UploadFailureReason.Denied)]
    [DataRow(UploadFailureReason.ConnectionFailed)]
    [DataRow(UploadFailureReason.TimedOut)]
    [DataRow(UploadFailureReason.InternalFailure)]
    public async Task WrappedFailureRetainsOriginalReasonAndMessage(UploadFailureReason expected)
    {
        await using var fixture = await CatalogFixture.CreateAsync(("Track.bin", "abcdef"));
        Exception cause = expected switch
        {
            UploadFailureReason.Unavailable => new SharedFileOpenException(SharedFileOpenFailureReason.MissingOrInaccessible,
                "The file cannot be opened.", new FileNotFoundException("The shared file was removed.")),
            UploadFailureReason.Denied => new Soulseek.TransferRejectedException("Transfer rejected: Cancelled"),
            UploadFailureReason.ConnectionFailed => new Soulseek.ConnectionException("Peer disconnected"),
            UploadFailureReason.TimedOut => new Soulseek.ConnectionException("Connection failed", new TimeoutException("Peer timed out")),
            _ => new InvalidOperationException("Unexpected implementation failure"),
        };
        var protocol = new ReadingProtocolInvoker(0, new IOException("Protocol upload failed.", cause));
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);
        var admission = await coordinator.AdmitAsync("alice", null, @"Public\Track.bin");
        var failed = await WaitForStateAsync(coordinator, admission.TransferId!.Value, UploadTransferState.Failed);
        Assert.AreEqual(expected, failed.FailureReason);
        Assert.AreEqual(cause.GetBaseException().Message, failed.FailureMessage);
    }

    [TestMethod]
    public async Task ZeroByteFile_CompletesThroughExactEofOffset()
    {
        await using var fixture = await CatalogFixture.CreateAsync(("Empty.bin", ""));
        var protocol = new ReadingProtocolInvoker(startOffset: 0);
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);

        var admission = await coordinator.AdmitAsync(
            "alice",
            new IPEndPoint(IPAddress.Loopback, 1),
            @"Public\Empty.bin");
        UploadTransferSnapshot completed = await WaitForStateAsync(
            coordinator,
            admission.TransferId!.Value,
            UploadTransferState.Completed);

        Assert.AreEqual(0, protocol.Bytes.Length);
        Assert.AreEqual(0, completed.BytesTransferred);
        Assert.IsNotNull(completed.Attempt);
    }

    [TestMethod]
    public async Task DuplicateRequest_CoalescesWithoutNewTransfer()
    {
        await using var fixture = await CatalogFixture.CreateAsync(("Track.bin", "abcdef"));
        var protocol = new BlockingProtocolInvoker();
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);

        var first = await coordinator.AdmitAsync(
            "Alice",
            null,
            @"Public\Track.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await coordinator.AdmitAsync(
            "Alice",
            null,
            @"Public\Track.bin");

        Assert.AreEqual(UploadAdmissionResultKind.Duplicate, duplicate.Kind);
        Assert.AreEqual(first.TransferId, duplicate.TransferId);
        Assert.AreEqual(1, coordinator.Snapshot().Count);

        protocol.Release.TrySetResult();
        await WaitForStateAsync(coordinator, first.TransferId!.Value, UploadTransferState.Completed);
    }

    [TestMethod]
    public async Task ForcedShutdownInterruptsActiveAndQueuedAndReleasesSchedulerOnce()
    {
        await using var fixture = await CatalogFixture.CreateAsync(
            ("One.bin", "one"),
            ("Two.bin", "two"));
        var protocol = new NonCooperativeProtocolInvoker();
        var coordinator = CreateCoordinator(
            fixture,
            protocol,
            slots: 1,
            shutdownGrace: TimeSpan.FromMilliseconds(50));

        var active = await coordinator.AdmitAsync("alice", null, @"Public\One.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = await coordinator.AdmitAsync("bob", null, @"Public\Two.bin");

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        UploadTransferSnapshot activeSnapshot =
            coordinator.GetTransfer(active.TransferId!.Value)!;
        UploadTransferSnapshot queuedSnapshot =
            coordinator.GetTransfer(queued.TransferId!.Value)!;
        Assert.AreEqual(UploadTransferState.Interrupted, activeSnapshot.State);
        Assert.AreEqual(
            UploadCancellationSource.DaemonShutdown,
            activeSnapshot.CancellationSource);
        Assert.IsNotNull(activeSnapshot.Attempt);
        Assert.AreEqual(UploadTransferState.Interrupted, queuedSnapshot.State);
        Assert.IsNull(queuedSnapshot.Attempt);
        Assert.AreEqual(0, coordinator.GetQueueSnapshot().ActiveSlots);
        Assert.AreEqual(0, coordinator.GetQueueSnapshot().QueuedFiles);

        protocol.Release.TrySetResult();
        await Task.Delay(25);
        Assert.AreEqual(
            UploadTransferState.Interrupted,
            coordinator.GetTransfer(active.TransferId.Value)!.State);
    }

    [TestMethod]
    public async Task ObserverFailureCannotAffectTransferLifecycle()
    {
        await using var fixture = await CatalogFixture.CreateAsync(("Track.bin", "abcdef"));
        var protocol = new ReadingProtocolInvoker(startOffset: 0);
        await using var coordinator = CreateCoordinator(fixture, protocol, slots: 1);
        int observed = 0;
        coordinator.TransferChanged += _ => throw new InvalidOperationException("observer");
        coordinator.TransferChanged += _ => Interlocked.Increment(ref observed);

        var admission = await coordinator.AdmitAsync("alice", null, @"Public\Track.bin");
        UploadTransferSnapshot completed = await WaitForStateAsync(
            coordinator,
            admission.TransferId!.Value,
            UploadTransferState.Completed);

        Assert.AreEqual(UploadTransferState.Completed, completed.State);
        Assert.IsTrue(observed >= 2);
    }

    [TestMethod]
    public async Task AdmissionPropagatesCallerCancellation()
    {
        var catalogs = new BlockingCatalogProvider();
        await using var coordinator = new UploadCoordinator(
            catalogs,
            new ReadingProtocolInvoker(startOffset: 0),
            new PeerRestrictionPolicy(new PeerRestrictionSettings()),
            new UploadScheduler(new UploadSettings { Slots = 1 }));
        using var cancellation = new CancellationTokenSource();

        Task<UploadCoordinatorAdmission> admission = coordinator.AdmitAsync(
            "alice",
            endpoint: null,
            @"Public\Track.bin",
            cancellation.Token).AsTask();
        await catalogs.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => admission);
    }

    private static UploadCoordinator CreateCoordinator(
        CatalogFixture fixture,
        IUploadProtocolInvoker protocol,
        int slots,
        TimeSpan? shutdownGrace = null)
        => new(
            fixture,
            protocol,
            new PeerRestrictionPolicy(new PeerRestrictionSettings()),
            new UploadScheduler(new UploadSettings { Slots = slots }),
            shutdownGrace);

    private static async Task<UploadTransferSnapshot> WaitForStateAsync(
        UploadCoordinator coordinator,
        Guid transferId,
        UploadTransferState state)
    {
        var reached = new TaskCompletionSource<UploadTransferSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Observe(UploadTransferSnapshot snapshot)
        {
            if (snapshot.TransferId == transferId && snapshot.State == state)
                reached.TrySetResult(snapshot);
        }

        coordinator.TransferChanged += Observe;
        try
        {
            UploadTransferSnapshot? current = coordinator.GetTransfer(transferId);
            if (current is not null)
                Observe(current);
            return await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            coordinator.TransferChanged -= Observe;
        }
    }

    private sealed class ReadingProtocolInvoker(long startOffset, Exception? failure = null) : IUploadProtocolInvoker
    {
        public byte[] Bytes { get; private set; } = [];
        public string? RemotePath { get; private set; }

        public async Task<UploadProtocolOutcome> UploadAsync(
            string username,
            string remotePath,
            long sizeBytes,
            Func<long, Task<Stream>> streamFactory,
            Action initializingCompleted,
            Action<long, double> progress,
            CancellationToken cancellationToken)
        {
            RemotePath = remotePath;
            if (failure is not null) throw failure;
            await using Stream stream = await streamFactory(startOffset);
            initializingCompleted();
            progress(startOffset, 0);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, cancellationToken);
            Bytes = output.ToArray();
            // Match the library's final update, not just bytes read this attempt.
            progress(stream.Position, 100);
            return UploadProtocolOutcome.Completed;
        }
    }

    private sealed class BlockingProtocolInvoker : IUploadProtocolInvoker
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The test owns this handoff; completing it inline avoids making the
        // asserted terminal transition depend on CI thread-pool availability.
        public TaskCompletionSource Release { get; } = new();

        public async Task<UploadProtocolOutcome> UploadAsync(
            string username,
            string remotePath,
            long sizeBytes,
            Func<long, Task<Stream>> streamFactory,
            Action initializingCompleted,
            Action<long, double> progress,
            CancellationToken cancellationToken)
        {
            await using Stream stream = await streamFactory(0);
            initializingCompleted();
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await stream.CopyToAsync(Stream.Null, cancellationToken);
            progress(sizeBytes, 100);
            return UploadProtocolOutcome.Completed;
        }
    }

    private sealed class NonCooperativeProtocolInvoker : IUploadProtocolInvoker
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UploadProtocolOutcome> UploadAsync(
            string username,
            string remotePath,
            long sizeBytes,
            Func<long, Task<Stream>> streamFactory,
            Action initializingCompleted,
            Action<long, double> progress,
            CancellationToken cancellationToken)
        {
            await using Stream stream = await streamFactory(0);
            initializingCompleted();
            Started.TrySetResult();
            await Release.Task;
            return UploadProtocolOutcome.Cancelled;
        }
    }

    private sealed class CatalogFixture :
        IShareCatalogProvider,
        IShareCatalogReader,
        IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ConcurrentDictionary<RemotePathKey, ShareCatalogResolvedFile> files;

        private CatalogFixture(
            TemporaryDirectory temporary,
            ConcurrentDictionary<RemotePathKey, ShareCatalogResolvedFile> files)
        {
            this.temporary = temporary;
            this.files = files;
        }

        public ShareCatalogMetadata Metadata { get; } = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "settings",
            1,
            0,
            0,
            ShareBrowseStatus.Ready,
            ShareCatalogVersions.BrowseWire,
            1,
            new string('A', 64));

        public static async ValueTask<CatalogFixture> CreateAsync(
            params (string Name, string Contents)[] entries)
        {
            var temporary = new TemporaryDirectory();
            string rootPath = Path.Combine(temporary.Path, "root");
            Directory.CreateDirectory(rootPath);
            var root = new ShareCatalogRoot(
                1,
                "Public",
                rootPath,
                RemotePathKey.CreateAlias("Public"));
            var directory = new ShareCatalogDirectory(
                1,
                1,
                "",
                "Public",
                RemotePathKey.Create("Public"));
            var files = new ConcurrentDictionary<RemotePathKey, ShareCatalogResolvedFile>();
            long id = 0;
            foreach (var entry in entries)
            {
                string localPath = Path.Combine(rootPath, entry.Name);
                await File.WriteAllTextAsync(localPath, entry.Contents);
                await using var opened = SafeSharedFileOpener.Open(rootPath, entry.Name);
                string remotePath = $@"Public\{entry.Name}";
                var file = new ShareCatalogFile(
                    ++id,
                    1,
                    1,
                    entry.Name,
                    remotePath,
                    RemotePathKey.Create(remotePath),
                    entry.Name,
                    opened.Fingerprint.SizeBytes,
                    opened.Fingerprint.LastWriteTimeUtc,
                    1,
                    "bin",
                    []);
                files[file.ComparisonPath] = new ShareCatalogResolvedFile(root, file);
            }
            return new CatalogFixture(temporary, files);
        }

        public bool TryAcquire(out IShareCatalogLease? lease)
        {
            lease = new Lease(this);
            return true;
        }

        public ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                files.TryGetValue(remotePath, out var file) ? file : null);

        public ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ShareCatalogFile>>([]);

        public ValueTask<ShareCatalogBrowseDirectory?> GetDirectoryAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ShareCatalogBrowseDirectory?>(null);

        public async IAsyncEnumerable<ShareCatalogBrowseDirectory> EnumerateBrowseAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => temporary.DisposeAsync();

        private sealed class Lease(IShareCatalogReader reader) : IShareCatalogLease
        {
            public IShareCatalogReader Reader { get; } = reader;
            public ShareCatalogMetadata Metadata => Reader.Metadata;
            public ShareBrowseStream OpenBrowseStream(
                TimeSpan idleTimeout,
                Action? releasePermit = null) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingCatalogProvider : IShareCatalogProvider
    {
        public TaskCompletionSource ResolveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryAcquire(out IShareCatalogLease? lease)
        {
            lease = new BlockingLease(this);
            return true;
        }

        private sealed class BlockingLease(BlockingCatalogProvider owner) :
            IShareCatalogLease,
            IShareCatalogReader
        {
            public IShareCatalogReader Reader => this;
            public ShareCatalogMetadata Metadata => throw new NotSupportedException();

            public async ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
                RemotePathKey remotePath,
                CancellationToken cancellationToken = default)
            {
                owner.ResolveStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }

            public ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
                string query,
                int limit,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public ValueTask<ShareCatalogBrowseDirectory?> GetDirectoryAsync(
                RemotePathKey remotePath,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public async IAsyncEnumerable<ShareCatalogBrowseDirectory> EnumerateBrowseAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public ShareBrowseStream OpenBrowseStream(
                TimeSpan idleTimeout,
                Action? releasePermit = null) => throw new NotSupportedException();

            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sockseek-upload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
