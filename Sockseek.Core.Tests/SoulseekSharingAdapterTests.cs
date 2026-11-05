using System.Net;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Soulseek;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class SoulseekSharingAdapterTests
{
    [TestMethod]
    public async Task SearchCallbackFailureIsReportedAndDoesNotPoisonLaterRequests()
    {
        var records = new List<CompactLogRecord>();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(new CompactTextLoggerProvider(records.Add, LogLevel.Warning)));
        var reader = new FakeCatalogReader([File(@"Public\track.flac")]) { SearchFailure = new IOException("catalog read failed") };
        await using var uploads = CreateUploads(reader);
        using var adapter = new SoulseekSharingAdapter(new FakeProvider(reader), uploads,
            new PeerRestrictionPolicy(new PeerRestrictionSettings()), new UploadSettings { Slots = 1 },
            () => null, logger: logs.CreateLogger("sharing-test"));
        Assert.IsNull(await adapter.ResolveSearchAsync("alice", 1, new SearchQuery("track")));
        Assert.IsNull(await adapter.ResolveSearchAsync("alice", 2, new SearchQuery("track")));
        Assert.AreEqual(1, records.Count);
        StringAssert.Contains(records[0].Message, "search response failed");
        reader.SearchFailure = null;
        Assert.IsNotNull(await adapter.ResolveSearchAsync("alice", 3, new SearchQuery("track")));
    }

    [TestMethod]
    public async Task UploadLookupFailureIsTemporaryAndDoesNotExposeLocalDetails()
    {
        var reader = new FakeCatalogReader([]) { ResolveFailure = new IOException("private storage path") };
        await using var uploads = CreateUploads(reader);
        using var adapter = CreateAdapter(reader, uploads);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 1234);
        var unavailable = await Assert.ThrowsExactlyAsync<DownloadEnqueueException>(
            () => adapter.EnqueueUploadAsync("alice", endpoint, @"Public\missing.flac"));
        Assert.AreEqual("Temporarily unavailable; try again later.", unavailable.Message);
        reader.ResolveFailure = null;
        var missing = await Assert.ThrowsExactlyAsync<DownloadEnqueueException>(
            () => adapter.EnqueueUploadAsync("alice", endpoint, @"Public\missing.flac"));
        Assert.AreEqual("File not shared.", missing.Message);
        adapter.Dispose();
        var stopped = await Assert.ThrowsExactlyAsync<DownloadEnqueueException>(
            () => adapter.EnqueueUploadAsync("alice", endpoint, @"Public\missing.flac"));
        Assert.AreEqual("Pending shutdown.", stopped.Message);
    }

    [TestMethod]
    public async Task DetachedRouterRejectsUploadsAndPreservesAttachedRejection()
    {
        var router = new SoulseekInboundRequestRouter();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 1234);
        var unavailable = await Assert.ThrowsExactlyAsync<DownloadEnqueueException>(
            () => router.EnqueueUploadAsync("alice", endpoint, @"Public\missing.flac"));
        Assert.AreEqual("Pending shutdown.", unavailable.Message);

        var reader = new FakeCatalogReader([]);
        await using var uploads = CreateUploads(reader);
        using var adapter = CreateAdapter(reader, uploads);
        using (router.Attach(adapter))
        {
            var missing = await Assert.ThrowsExactlyAsync<DownloadEnqueueException>(
                () => router.EnqueueUploadAsync("alice", endpoint, @"Public\missing.flac"));
            Assert.AreEqual("File not shared.", missing.Message);
        }
        await Assert.ThrowsExactlyAsync<DownloadEnqueueException>(
            () => router.EnqueueUploadAsync("alice", endpoint, @"Public\missing.flac"));
    }

    [TestMethod]
    public async Task SearchStartsWithEmptyExcludedPhraseSet()
    {
        var reader = new FakeCatalogReader([File(@"Public\allowed track.flac")]);
        await using var uploads = CreateUploads(reader);
        using var adapter = CreateAdapter(reader, uploads);

        SearchResponse? response = await adapter.ResolveSearchAsync(
            "alice",
            1,
            new SearchQuery("allowed"));
        Assert.IsNotNull(response);
        Assert.AreEqual(1, response.Files.Count);
        Assert.AreEqual(0, response.UploadSpeed, "Unknown bandwidth must not be advertised as unlimited speed.");
    }

    [TestMethod]
    public async Task InvalidReplacementKeepsSearchAndLastValidMatcher()
    {
        var reader = new FakeCatalogReader(
        [
            File(@"Public\forbidden track.flac"),
            File(@"Public\allowed track.flac"),
        ]);
        await using var uploads = CreateUploads(reader);
        using var adapter = CreateAdapter(reader, uploads);

        Assert.IsTrue(adapter.TryUpdateExcludedSearchPhrases(["forbidden"]));
        Assert.IsFalse(adapter.TryUpdateExcludedSearchPhrases(
            Enumerable.Range(0, SoulseekSharingAdapter.MaximumExcludedPhraseCount + 1)
                .Select(index => $"phrase-{index}")
                .ToArray()));

        SearchResponse? response = await adapter.ResolveSearchAsync(
            "alice",
            1,
            new SearchQuery("track"));
        Assert.IsNotNull(response);
        CollectionAssert.AreEqual(
            new[] { @"Public\allowed track.flac" },
            response.Files.Select(file => file.Filename).ToArray());
    }

    [TestMethod]
    public async Task InvalidUnicodePhraseReplacementIsIgnoredWithoutEscapingCallback()
    {
        var reader = new FakeCatalogReader([File(@"Public\allowed track.flac")]);
        await using var uploads = CreateUploads(reader);
        using var adapter = CreateAdapter(reader, uploads);
        Assert.IsTrue(adapter.TryUpdateExcludedSearchPhrases([]));

        Assert.IsFalse(adapter.TryUpdateExcludedSearchPhrases(["\uD800"]));
        Assert.IsNotNull(await adapter.ResolveSearchAsync(
            "alice",
            1,
            new SearchQuery("allowed")));
    }

    [TestMethod]
    public async Task SearchUsesBoundedOverfetchAndAppliesBothExclusionSources()
    {
        var reader = new FakeCatalogReader(
        [
            File(@"Public\blocked track.flac"),
            File(@"Public\forbidden track.flac"),
            File(@"Public\allowed one track.flac"),
            File(@"Public\allowed two track.flac"),
            File(@"Public\allowed three track.flac"),
        ]);
        await using var uploads = CreateUploads(reader);
        using var adapter = CreateAdapter(reader, uploads);
        Assert.IsTrue(adapter.TryUpdateExcludedSearchPhrases(["forbidden"]));

        SearchResponse? response = await adapter.ResolveSearchAsync(
            "alice",
            1,
            new SearchQuery(["track"], ["blocked"]));

        Assert.IsNotNull(response);
        Assert.AreEqual(SoulseekSharingAdapter.MaximumSearchCandidates, reader.LastLimit);
        CollectionAssert.AreEquivalent(
            new[] { "blocked" },
            reader.LastExclusions.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                @"Public\allowed one track.flac",
                @"Public\allowed two track.flac",
                @"Public\allowed three track.flac",
            },
            response.Files.Select(file => file.Filename).ToArray());
    }

    [TestMethod]
    public async Task MissingListenerFailsSearchAndUploadCallbacksClosed()
    {
        var reader = new FakeCatalogReader([File(@"Public\allowed track.flac")]);
        await using var uploads = CreateUploads(reader);
        using var adapter = CreateAdapter(
            reader,
            uploads,
            uploadServingEnabled: false);
        Assert.IsTrue(adapter.TryUpdateExcludedSearchPhrases([]));
        var endpoint = new IPEndPoint(IPAddress.Loopback, 1234);

        Assert.IsNull(await adapter.ResolveSearchAsync(
            "alice",
            1,
            new SearchQuery("allowed")));
        Assert.AreEqual(0, reader.SearchCount);
        await Assert.ThrowsExactlyAsync<DownloadEnqueueException>(
            () => adapter.EnqueueUploadAsync(
                "alice",
                endpoint,
                @"Public\allowed track.flac"));
        Assert.IsNull(await adapter.ResolvePlaceInQueueAsync(
            "alice",
            endpoint,
            @"Public\allowed track.flac"));
        UserInfo info = await adapter.ResolveUserInfoAsync("alice", endpoint);
        Assert.AreEqual(0, info.UploadSlots);
        Assert.IsFalse(info.HasFreeUploadSlot);
    }

    [TestMethod]
    public async Task UserInfoAdvertisesPictureOnlyToAllowedPeers()
    {
        var reader = new FakeCatalogReader([]);
        await using var uploads = CreateUploads(reader);
        byte[] picture = [0xff, 0xd8, 0xff, 0xd9];
        using var adapter = new SoulseekSharingAdapter(
            new FakeProvider(reader),
            uploads,
            new PeerRestrictionPolicy(new PeerRestrictionSettings
            {
                UploadAccess = new UploadAccessSettings { BlockedUsernames = ["blocked"] },
            }),
            new UploadSettings { Slots = 1 },
            () => null,
            "hello",
            userPicture: picture);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 1234);

        UserInfo allowed = await adapter.ResolveUserInfoAsync("allowed", endpoint);
        Assert.AreEqual("hello", allowed.Description);
        Assert.IsTrue(allowed.HasPicture);
        CollectionAssert.AreEqual(picture, allowed.Picture);

        UserInfo blocked = await adapter.ResolveUserInfoAsync("blocked", endpoint);
        Assert.AreEqual("", blocked.Description);
        Assert.IsFalse(blocked.HasPicture);
    }

    [TestMethod]
    public async Task BoundedCallbackGateRejectsWorkBeyondOutstandingCapacity()
    {
        var gate = new BoundedCallbackGate(concurrency: 1, capacity: 2);
        await using BoundedCallbackGate.Lease first =
            (await gate.TryEnterAsync(CancellationToken.None))!;
        ValueTask<BoundedCallbackGate.Lease?> secondWait =
            gate.TryEnterAsync(CancellationToken.None);

        Assert.IsNull(await gate.TryEnterAsync(CancellationToken.None));
        await first.DisposeAsync();

        await using BoundedCallbackGate.Lease? second = await secondWait;
        Assert.IsNotNull(second);
    }

    private static SoulseekSharingAdapter CreateAdapter(
        FakeCatalogReader reader,
        UploadCoordinator uploads,
        bool uploadServingEnabled = true)
        => new(
            new FakeProvider(reader),
            uploads,
            new PeerRestrictionPolicy(new PeerRestrictionSettings()),
            new UploadSettings { Slots = 1 },
            () => null,
            uploadServingEnabled: uploadServingEnabled);

    private static UploadCoordinator CreateUploads(FakeCatalogReader reader)
        => new(
            new FakeProvider(reader),
            new UnexpectedProtocolInvoker(),
            new PeerRestrictionPolicy(new PeerRestrictionSettings()),
            new UploadScheduler(new UploadSettings { Slots = 1 }));

    private static ShareCatalogFile File(string remotePath)
        => new(
            Random.Shared.NextInt64(1, long.MaxValue),
            1,
            1,
            remotePath[(remotePath.IndexOf('\\') + 1)..],
            remotePath,
            RemotePathKey.Create(remotePath),
            remotePath,
            100,
            DateTimeOffset.UnixEpoch,
            1,
            "flac",
            []);

    private sealed class FakeProvider(IShareCatalogReader reader) : IShareCatalogProvider
    {
        public bool TryAcquire(out IShareCatalogLease? lease)
        {
            lease = new FakeLease(reader);
            return true;
        }
    }

    private sealed class FakeLease(IShareCatalogReader reader) : IShareCatalogLease
    {
        public IShareCatalogReader Reader { get; } = reader;
        public ShareCatalogMetadata Metadata => Reader.Metadata;

        public ShareBrowseStream OpenBrowseStream(
            TimeSpan idleTimeout,
            Action? releasePermit = null)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCatalogReader(
        IReadOnlyList<ShareCatalogFile> files) : IShareCatalogReader
    {
        public Exception? ResolveFailure { get; set; }
        public Exception? SearchFailure { get; set; }
        public int SearchCount { get; private set; }
        public int LastLimit { get; private set; }
        public IReadOnlyCollection<string> LastExclusions { get; private set; } = [];

        public ShareCatalogMetadata Metadata { get; } = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "settings",
            1,
            files.Count,
            files.Sum(file => file.SizeBytes),
            ShareBrowseStatus.Ready,
            ShareCatalogVersions.BrowseWire,
            1,
            new string('A', 64));

        public ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ResolveFailure is { } failure
                ? ValueTask.FromException<ShareCatalogResolvedFile?>(failure)
                : ValueTask.FromResult<ShareCatalogResolvedFile?>(null);

        public ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default)
            => SearchAsync(query, [], limit, cancellationToken);

        public ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
            string query,
            IReadOnlyCollection<string> exclusions,
            int limit,
            CancellationToken cancellationToken = default)
        {
            SearchCount++;
            if (SearchFailure is { } failure) return ValueTask.FromException<IReadOnlyList<ShareCatalogFile>>(failure);
            LastLimit = limit;
            LastExclusions = exclusions.ToArray();
            return ValueTask.FromResult<IReadOnlyList<ShareCatalogFile>>(
                files.Take(limit).ToArray());
        }

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnexpectedProtocolInvoker : IUploadProtocolInvoker
    {
        public Task<UploadProtocolOutcome> UploadAsync(
            string username,
            string remotePath,
            long sizeBytes,
            Func<long, Task<Stream>> streamFactory,
            Action initializingCompleted,
            Action<long, double> progress,
            CancellationToken cancellationToken)
            => throw new AssertFailedException("Search tests must not dispatch uploads.");
    }
}
