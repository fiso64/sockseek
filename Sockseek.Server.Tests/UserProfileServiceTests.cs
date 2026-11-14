using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Api;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Server.UserProfiles;
using Soulseek;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class UserProfileServiceTests
{
    [TestMethod]
    public async Task AcquisitionAndReuseLogsAreCompleteAndDoNotExposeUsername()
    {
        const string username = "PrivatePeerName";
        var logger = new RecordingLogger<UserProfileService>();
        await using var service = CreateService(new DelegateTransport(), logger: logger);

        await service.GetAsync(username);
        await service.GetAsync(username);

        int[] eventIds = logger.Entries.Select(entry => entry.EventId.Id).ToArray();
        CollectionAssert.Contains(eventIds, SockseekEventIds.OperationStarted);
        CollectionAssert.Contains(eventIds, SockseekEventIds.OperationSucceeded);
        CollectionAssert.Contains(eventIds, 4102);
        Assert.AreEqual(1, eventIds.Count(id => id == SockseekEventIds.OperationSucceeded));
        Assert.IsFalse(logger.Entries.Any(entry =>
            entry.Message.Contains(username, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConcurrentRefreshJoinsAndCallerCancellationOnlyDetaches()
    {
        var transport = new GatedTransport();
        int startupCalls = 0;
        await using var service = CreateService(
            transport,
            _ => { Interlocked.Increment(ref startupCalls); return Task.CompletedTask; });

        Task<UserProfileDto> owner = service.GetAsync("Peer");
        await transport.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var detached = new CancellationTokenSource();
        Task<UserProfileDto> waiter = service.GetAsync("Peer", refresh: true, detached.Token);
        detached.Cancel();

        try
        {
            await waiter;
            Assert.Fail("The detached waiter should observe its own cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
        transport.Release.TrySetResult();
        UserProfileDto profile = await owner;

        Assert.AreEqual("Peer", profile.Username);
        Assert.AreEqual(1, transport.InfoCalls);
        Assert.AreEqual(1, transport.StatisticsCalls);
        Assert.AreEqual(1, startupCalls);
    }

    [TestMethod]
    [DataRow("010203")]
    [DataRow("49492B000800000010000000000000000100000000000000")]
    public async Task PartialSectionsAndInvalidPictureDoNotEraseSuccessfulInfo(string pictureHex)
    {
        var transport = new DelegateTransport
        {
            Info = (_, _) => Task.FromResult(new UserInfo(
                "hello\r\nworld\u202e",
                uploadSlots: -1,
                queueLength: 7,
                hasFreeUploadSlot: false,
                picture: Convert.FromHexString(pictureHex))),
            Statistics = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException();
            },
        };
        await using var service = CreateService(
            transport,
            sectionTimeout: TimeSpan.FromMilliseconds(25));

        UserProfileDto profile = await service.GetAsync("Peer");

        Assert.AreEqual(ResourceSectionState.Available, profile.Info.State);
        Assert.AreEqual(ResourceSectionState.TimedOut, profile.Statistics.State);
        Assert.AreEqual(ResourceSectionState.Unavailable, profile.PictureSection.State);
        Assert.AreEqual("invalid-image", profile.PictureSection.Reason);
        Assert.AreEqual("hello\nworld", profile.Description);
        Assert.IsNull(profile.UploadSlots);
        Assert.AreEqual(7, profile.QueueLength);
        Assert.IsFalse(profile.HasFreeUploadSlot);
        Assert.IsNull(profile.Picture);
    }

    [TestMethod]
    public async Task ProfileDetailsRemainCachedUntilExplicitRefreshAndKeepExactIdentity()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var transport = new DelegateTransport();
        await using var service = CreateService(transport, utcNow: () => now);

        UserProfileDto first = await service.GetAsync("Peer");
        UserProfileDto reused = await service.GetAsync("Peer");
        Assert.AreEqual(first.ObservedAt, reused.ObservedAt);
        Assert.AreEqual(1, transport.InfoCalls);

        await service.GetAsync("Peer", refresh: true);
        Assert.AreEqual(2, transport.InfoCalls);

        now += TimeSpan.FromDays(1);
        await service.GetAsync("Peer");
        Assert.AreEqual(2, transport.InfoCalls);

        await service.GetAsync("peer");
        Assert.AreEqual(3, transport.InfoCalls, "Soulseek identities must remain case-sensitive.");
    }

    [TestMethod]
    public async Task UsernameValidationRunsBeforeSoulseekStartup()
    {
        int startupCalls = 0;
        await using var service = new UserProfileService(
            new DelegateTransport(),
            _ => { startupCalls++; return Task.CompletedTask; },
            () => "account",
            NullLogger<UserProfileService>.Instance);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.GetAsync("bad\u0001name"));
        Assert.AreEqual(0, startupCalls);
    }

    [TestMethod]
    public async Task ConnectionLossFailsActiveCompositeWithStableServiceFailure()
    {
        var transport = new GatedTransport();
        await using var service = CreateService(transport);
        service.OnSoulseekStateChanged(
            SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);
        Task<UserProfileDto> active = service.GetAsync("Peer");
        await transport.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.OnSoulseekStateChanged(SoulseekClientStates.None);

        UserProfileUnavailableException exception =
            await Assert.ThrowsExactlyAsync<UserProfileUnavailableException>(() => active);
        Assert.AreEqual("Soulseek connection was lost.", exception.Message);
    }

    [TestMethod]
    public async Task PreLoginStateTransitionsDoNotCancelCompositeThatIsStartingTheSession()
    {
        var transport = new GatedTransport();
        await using var service = CreateService(transport);
        Task<UserProfileDto> active = service.GetAsync("Peer");
        await transport.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.OnSoulseekStateChanged(SoulseekClientStates.Connecting);
        service.OnSoulseekStateChanged(SoulseekClientStates.Connected);

        Assert.IsFalse(active.IsCompleted);
        transport.Release.TrySetResult();
        Assert.AreEqual(ResourceSectionState.Available, (await active).Info.State);
    }

    [TestMethod]
    public async Task PresenceSharesExactUserWatchesAndRestoresOnlyActiveReadersAfterReconnect()
    {
        var peer = global::Tests.Server.SoulseekClientProxy.Create();
        await using var service = new UserPresenceService(() => peer.Client, _ => Task.CompletedTask,
            NullLogger<UserPresenceService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var first = service.ObserveAsync("Peer", deadline.Token).GetAsyncEnumerator();
        await NextPresence(first, UserPresenceState.Online);
        await using var second = service.ObserveAsync("Peer", deadline.Token).GetAsyncEnumerator();
        await NextPresence(second, UserPresenceState.Online);
        Assert.AreEqual(UserPresenceState.Online, (await service.GetAsync("Peer", deadline.Token)).State);
        Assert.AreEqual(1, peer.WatchCount);
        await using var other = service.ObserveAsync("peer", deadline.Token).GetAsyncEnumerator();
        await NextPresence(other, UserPresenceState.Online);
        Assert.AreEqual(2, peer.WatchCount);
        peer.RaiseUserStatus("Peer", UserPresence.Away);
        await NextPresence(first, UserPresenceState.Away);
        await NextPresence(second, UserPresenceState.Away);
        Assert.AreEqual(UserPresenceState.Online, (await service.GetAsync("peer", deadline.Token)).State);
        await first.DisposeAsync();
        Assert.AreEqual(0, peer.UnwatchCount);
        await other.DisposeAsync();
        Assert.AreEqual(1, peer.UnwatchCount);

        peer.ClientState = SoulseekClientStates.Disconnected;
        service.OnSoulseekStateChanged(peer.ClientState);
        await NextPresence(second, UserPresenceState.Disconnected);
        peer.RaiseUserStatus("Peer", UserPresence.Online);
        Assert.AreEqual(UserPresenceState.Disconnected, (await service.GetAsync("Peer", deadline.Token)).State);
        peer.ClientState = SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn;
        service.OnSoulseekStateChanged(peer.ClientState);
        await NextPresence(second, UserPresenceState.Online);
        Assert.AreEqual(3, peer.WatchCount);
        await second.DisposeAsync();
        Assert.AreEqual(2, peer.UnwatchCount);
    }

    [TestMethod]
    public async Task ReopeningDuringCancelledAcquisitionWaitsForUnwatchBeforeWatchingAgain()
    {
        var peer = global::Tests.Server.SoulseekClientProxy.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        peer.Watch = async (username, ct) =>
        {
            if (peer.WatchCount == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new UserData(username, UserPresence.Online, 0, 0, 0, 0, "");
        };
        peer.Unwatch = async _ =>
        {
            releasing.TrySetResult();
            await released.Task.WaitAsync(deadline.Token);
        };
        await using var service = new UserPresenceService(() => peer.Client, _ => Task.CompletedTask,
            NullLogger<UserPresenceService>.Instance);
        var first = service.ObserveAsync("Peer", deadline.Token).GetAsyncEnumerator();
        await NextPresence(first, UserPresenceState.Checking);
        await started.Task.WaitAsync(deadline.Token);
        Task closing = first.DisposeAsync().AsTask();
        await releasing.Task.WaitAsync(deadline.Token);
        Task<UserPresenceDto> replacement = service.GetAsync("Peer", deadline.Token);
        Assert.AreEqual(1, peer.WatchCount);
        released.SetResult();
        await closing;
        Assert.AreEqual(UserPresenceState.Online, (await replacement).State);
        Assert.AreEqual(2, peer.WatchCount);
        Assert.AreEqual(2, peer.UnwatchCount);
    }

    [TestMethod]
    public async Task FailedPresenceDoesNotPretendOfflineAndReleasesAttemptedWatch()
    {
        var peer = global::Tests.Server.SoulseekClientProxy.Create();
        peer.Watch = (_, _) => throw new TimeoutException();
        await using var service = new UserPresenceService(() => peer.Client, _ => Task.CompletedTask,
            NullLogger<UserPresenceService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.AreEqual(UserPresenceState.Unavailable, (await service.GetAsync("Peer", deadline.Token)).State);
        Assert.AreEqual(1, peer.UnwatchCount);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.GetAsync("bad\u0001name", deadline.Token));
        Assert.AreEqual(1, peer.WatchCount);
    }

    [TestMethod]
    public async Task PresenceReadersRecoverWhenInitialLoginFails()
    {
        var peer = global::Tests.Server.SoulseekClientProxy.Create();
        peer.ClientState = SoulseekClientStates.Disconnected;
        await using var service = new UserPresenceService(() => peer.Client,
            _ => Task.FromException(new IOException("Login unavailable")), NullLogger<UserPresenceService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = service.ObserveAsync("Peer", deadline.Token).GetAsyncEnumerator();
        await NextPresence(reader, UserPresenceState.Disconnected);
        Assert.AreEqual(0, peer.WatchCount);
        peer.ClientState = SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn;
        service.OnSoulseekStateChanged(peer.ClientState);
        await NextPresence(reader, UserPresenceState.Online);
        Assert.AreEqual(1, peer.WatchCount);
    }

    private static async Task NextPresence(IAsyncEnumerator<UserPresenceDto> reader, UserPresenceState expected)
    {
        while (await reader.MoveNextAsync())
        {
            if (reader.Current.State == expected) return;
            Assert.AreEqual(UserPresenceState.Checking, reader.Current.State);
        }
        Assert.Fail("Presence stream ended unexpectedly.");
    }

    private static UserProfileService CreateService(
        IUserProfileTransport transport,
        Func<CancellationToken, Task>? ensureStarted = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? sectionTimeout = null,
        ILogger<UserProfileService>? logger = null)
        => new(
            transport,
            ensureStarted ?? (_ => Task.CompletedTask),
            () => "account",
            logger ?? NullLogger<UserProfileService>.Instance,
            utcNow: utcNow,
            sectionTimeout: sectionTimeout);

    private sealed class DelegateTransport : IUserProfileTransport
    {
        public int InfoCalls;
        public int StatisticsCalls;

        public Func<string, CancellationToken, Task<UserInfo>> Info { get; init; } =
            (_, _) => Task.FromResult(new UserInfo("description", 2, 3, true));
        public Func<string, CancellationToken, Task<UserStatistics>> Statistics { get; init; } =
            (username, _) => Task.FromResult(new UserStatistics(username, 100, 5, 10, 2));

        public Task<UserInfo> GetInfoAsync(string username, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InfoCalls);
            return Info(username, cancellationToken);
        }

        public Task<UserStatistics> GetStatisticsAsync(string username, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StatisticsCalls);
            return Statistics(username, cancellationToken);
        }
    }

    private sealed class GatedTransport : IUserProfileTransport
    {
        private int started;
        public int InfoCalls;
        public int StatisticsCalls;
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UserInfo> GetInfoAsync(string username, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InfoCalls);
            Started();
            await Release.Task.WaitAsync(cancellationToken);
            return new UserInfo("description", 1, 0, true);
        }

        public async Task<UserStatistics> GetStatisticsAsync(string username, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StatisticsCalls);
            Started();
            await Release.Task.WaitAsync(cancellationToken);
            return new UserStatistics(username, 1, 1, 1, 1);
        }

        private void Started()
        {
            if (Interlocked.Increment(ref started) == 2)
                AllStarted.TrySetResult();
        }
    }
}
