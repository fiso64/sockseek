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
        Assert.AreEqual(1, transport.StatusCalls);
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
            Status = (_, _) => Task.FromResult(new UserStatus(
                "Peer", Soulseek.UserPresence.Away, false)),
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

        Assert.AreEqual(UserProfilePresence.Away, profile.Presence);
        Assert.AreEqual(ResourceSectionState.Available, profile.Status.State);
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
    public async Task FreshnessRefreshAndExactIdentityHaveDeterministicCacheBehavior()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var transport = new DelegateTransport();
        await using var service = CreateService(transport, utcNow: () => now);

        UserProfileDto first = await service.GetAsync("Peer");
        UserProfileDto reused = await service.GetAsync("Peer");
        Assert.AreEqual(first.ObservedAt, reused.ObservedAt);
        Assert.AreEqual(1, transport.StatusCalls);

        await service.GetAsync("Peer", refresh: true);
        Assert.AreEqual(2, transport.StatusCalls);

        now += UserProfileService.Freshness;
        await service.GetAsync("Peer");
        Assert.AreEqual(3, transport.StatusCalls, "Freshness must be strict at exactly 30 seconds.");

        await service.GetAsync("peer");
        Assert.AreEqual(4, transport.StatusCalls, "Soulseek identities must remain case-sensitive.");
    }

    [TestMethod]
    public async Task PresenceObservationFetchesOnlyStatusAndRetainsExactPeerIdentity()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var transport = new DelegateTransport
        {
            Status = (username, _) => Task.FromResult(new UserStatus(
                username, Soulseek.UserPresence.Away, false)),
        };
        await using var service = CreateService(transport, utcNow: () => now);

        UserPresenceObservationDto presence = await service.ObservePresenceAsync("Peer");

        Assert.AreEqual("Peer", presence.Username);
        Assert.AreEqual(UserProfilePresence.Away, presence.Presence);
        Assert.AreEqual(ResourceSectionState.Available, presence.Status.State);
        Assert.AreEqual(now, presence.ObservedAtUtc);
        Assert.AreEqual(1, transport.StatusCalls);
        Assert.AreEqual(0, transport.InfoCalls);
        Assert.AreEqual(0, transport.StatisticsCalls);

        now = now.AddMinutes(1);
        presence = await service.ObservePresenceAsync("peer");
        Assert.AreEqual("peer", presence.Username);
        Assert.AreEqual(now, presence.ObservedAtUtc);
        Assert.AreEqual(2, transport.StatusCalls);
    }

    [TestMethod]
    public async Task FailedPresenceObservationDoesNotInventAnAgeOrFetchProfileSections()
    {
        var transport = new DelegateTransport
        {
            Status = (_, _) => throw new InvalidOperationException("status unavailable"),
        };
        await using var service = CreateService(transport);

        UserPresenceObservationDto presence = await service.ObservePresenceAsync("Peer");

        Assert.AreEqual(UserProfilePresence.Unknown, presence.Presence);
        Assert.AreEqual(ResourceSectionState.Unavailable, presence.Status.State);
        Assert.IsNull(presence.ObservedAtUtc);
        Assert.AreEqual(0, transport.InfoCalls);
        Assert.AreEqual(0, transport.StatisticsCalls);
    }

    [TestMethod]
    public async Task DisposalDrainsAnActivePresenceObservationBeforeClosingNetworkConcurrency()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new DelegateTransport
        {
            Status = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancelled status request resumed unexpectedly.");
            },
        };
        var service = CreateService(transport);
        Task<UserPresenceObservationDto> observation = service.ObservePresenceAsync("Peer");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.DisposeAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => observation);
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
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ObservePresenceAsync("bad\u0001name"));
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
        Assert.AreEqual(UserProfilePresence.Online, (await active).Presence);
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
        public int StatusCalls;
        public int InfoCalls;
        public int StatisticsCalls;

        public Func<string, CancellationToken, Task<UserStatus>> Status { get; init; } =
            (username, _) => Task.FromResult(new UserStatus(
                username, Soulseek.UserPresence.Online, false));
        public Func<string, CancellationToken, Task<UserInfo>> Info { get; init; } =
            (_, _) => Task.FromResult(new UserInfo("description", 2, 3, true));
        public Func<string, CancellationToken, Task<UserStatistics>> Statistics { get; init; } =
            (username, _) => Task.FromResult(new UserStatistics(username, 100, 5, 10, 2));

        public Task<UserStatus> GetStatusAsync(string username, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StatusCalls);
            return Status(username, cancellationToken);
        }

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
        public int StatusCalls;
        public int InfoCalls;
        public int StatisticsCalls;
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UserStatus> GetStatusAsync(string username, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StatusCalls);
            Started();
            await Release.Task.WaitAsync(cancellationToken);
            return new UserStatus(username, Soulseek.UserPresence.Online, false);
        }

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
            if (Interlocked.Increment(ref started) == 3)
                AllStarted.TrySetResult();
        }
    }
}
