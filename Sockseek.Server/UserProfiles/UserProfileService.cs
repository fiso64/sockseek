using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Models;
using Sockseek.Core.Sharing;
using Sockseek.Core.UserProfiles;
using Soulseek;
using ApiPresence = Sockseek.Api.UserProfilePresence;
using SoulseekPresence = Soulseek.UserPresence;

namespace Sockseek.Server.UserProfiles;

public sealed class UserProfileUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public interface IUserProfileTransport
{
    Task<UserStatus> GetStatusAsync(string username, CancellationToken cancellationToken);
    Task<UserInfo> GetInfoAsync(string username, CancellationToken cancellationToken);
    Task<UserStatistics> GetStatisticsAsync(string username, CancellationToken cancellationToken);
}

public sealed class SoulseekUserProfileTransport(Func<ISoulseekClient?> clientProvider)
    : IUserProfileTransport
{
    public Task<UserStatus> GetStatusAsync(string username, CancellationToken cancellationToken)
        => Client().GetUserStatusAsync(username, cancellationToken);

    public Task<UserInfo> GetInfoAsync(string username, CancellationToken cancellationToken)
        => Client().GetUserInfoAsync(username, cancellationToken);

    public Task<UserStatistics> GetStatisticsAsync(string username, CancellationToken cancellationToken)
        => Client().GetUserStatisticsAsync(username, cancellationToken);

    private ISoulseekClient Client()
        => clientProvider() ?? throw new UserProfileUnavailableException("Soulseek is unavailable.");
}

/// <summary>
/// Daemon-lifetime composite profile coordinator. Identical requests share one
/// set of protocol calls; retained pictures are bounded by count and byte-budget
/// eviction, never by rejecting otherwise valid peer data.
/// </summary>
public sealed class UserProfileService : IAsyncDisposable
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SectionTimeout = TimeSpan.FromSeconds(10);
    public const int DefaultNetworkConcurrency = 4;
    public const int MaximumCachedProfiles = 128;
    public const long PictureCacheByteTarget = 32L * 1024 * 1024;

    private readonly IUserProfileTransport transport;
    private readonly Func<CancellationToken, Task> ensureSessionStarted;
    private readonly Func<string?> localAccountProvider;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeSpan sectionTimeout;
    private readonly ILogger<UserProfileService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ProfileKey, Task<CachedProfile>> active = [];
    private readonly Dictionary<ProfileKey, CachedProfile> cache = [];
    private readonly SemaphoreSlim networkGate;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sessionSync = new();
    private readonly object connectionSync = new();
    private readonly object presenceSync = new();
    private CancellationTokenSource connectionLifetime = new();
    private Task? sessionStartup;
    private TaskCompletionSource? presenceDrained;
    private int activePresenceOperations;
    private int observedLoggedInSession;
    private int disposeState;

    public UserProfileService(
        IUserProfileTransport transport,
        Func<CancellationToken, Task> ensureSessionStarted,
        Func<string?> localAccountProvider,
        ILogger<UserProfileService> logger,
        int networkConcurrency = DefaultNetworkConcurrency,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? sectionTimeout = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.ensureSessionStarted = ensureSessionStarted ?? throw new ArgumentNullException(nameof(ensureSessionStarted));
        this.localAccountProvider = localAccountProvider ?? throw new ArgumentNullException(nameof(localAccountProvider));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.sectionTimeout = sectionTimeout ?? SectionTimeout;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (networkConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(networkConcurrency));
        if (this.sectionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sectionTimeout));

        networkGate = new SemaphoreSlim(networkConcurrency, networkConcurrency);
    }

    public async Task<UserProfileDto> GetAsync(
        string username,
        bool refresh = false,
        CancellationToken cancellationToken = default)
        => (await GetEntryAsync(username, refresh, cancellationToken).ConfigureAwait(false)).Profile;

    public Task<UserPresenceObservationDto> ObservePresenceAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        lock (presenceSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
            activePresenceOperations++;
        }
        return ObservePresenceTrackedAsync(username, cancellationToken);
    }

    private async Task<UserPresenceObservationDto> ObservePresenceTrackedAsync(
        string username,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ObservePresenceCoreAsync(username, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (presenceSync)
            {
                activePresenceOperations--;
                if (activePresenceOperations == 0)
                    presenceDrained?.TrySetResult();
            }
        }
    }

    private async Task<UserPresenceObservationDto> ObservePresenceCoreAsync(
        string username,
        CancellationToken cancellationToken)
    {
        username = PeerUsername.Validate(username);
        string peerHash = LogIdentity.PeerHash(username);
        using OperationLogScope operationLog = OperationLogScope.Start(
            logger, "user-presence.observe", peerHash);
        bool networkAcquired = false;
        try
        {
            await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            _ = PeerUsername.Validate(localAccountProvider()
                ?? throw new UserProfileUnavailableException("Soulseek is not logged in."));
            CancellationToken connectionToken;
            lock (connectionSync)
                connectionToken = connectionLifetime.Token;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lifetime.Token, connectionToken);
            await networkGate.WaitAsync(operation.Token).ConfigureAwait(false);
            networkAcquired = true;
            SectionResult<UserStatus> status = await CaptureAsync(
                token => transport.GetStatusAsync(username, token),
                operation.Token,
                sectionTimeout,
                logger,
                peerHash,
                "status").ConfigureAwait(false);
            DateTimeOffset? observedAt = status.Section.State == ResourceSectionState.Available
                ? utcNow() : null;
            var observation = new UserPresenceObservationDto(
                username, Presence(status), status.Section, observedAt);
            if (observedAt is not null)
                operationLog.Succeeded("available", itemCount: 1);
            else
                operationLog.Degraded(status.Section.State.ToString(), itemCount: 0);
            return observation;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            operationLog.Failed(exception, "connection-lost");
            throw new UserProfileUnavailableException("Soulseek connection was lost.", exception);
        }
        catch (OperationCanceledException)
        {
            operationLog.Cancelled();
            throw;
        }
        catch (Exception exception)
        {
            operationLog.Failed(exception);
            throw;
        }
        finally
        {
            if (networkAcquired)
                networkGate.Release();
        }
    }

    public async Task<UserPicture?> GetPictureAsync(
        string username,
        CancellationToken cancellationToken = default)
        => (await GetEntryAsync(username, refresh: false, cancellationToken).ConfigureAwait(false)).Picture;

    public void OnSoulseekStateChanged(SoulseekClientStates state)
    {
        if (Volatile.Read(ref disposeState) != 0)
            return;
        if (state.HasFlag(SoulseekClientStates.Connected)
            && state.HasFlag(SoulseekClientStates.LoggedIn))
        {
            Volatile.Write(ref observedLoggedInSession, 1);
            return;
        }
        if (Interlocked.Exchange(ref observedLoggedInSession, 0) == 0)
            return;

        CancellationTokenSource previous;
        lock (connectionSync)
        {
            previous = connectionLifetime;
            connectionLifetime = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
        lock (sessionSync)
            sessionStartup = null;
    }

    private async Task<CachedProfile> GetEntryAsync(
        string username,
        bool refresh,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        username = PeerUsername.Validate(username);
        string peerHash = LogIdentity.PeerHash(username);

        try
        {
            await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UserProfileLogMessages.SessionUnavailable(logger, exception, peerHash);
            throw;
        }
        string localAccount = PeerUsername.Validate(
            localAccountProvider()
            ?? throw new UserProfileUnavailableException("Soulseek is not logged in."));
        var key = new ProfileKey(localAccount, username);

        Task<CachedProfile> operation;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active.TryGetValue(key, out Task<CachedProfile>? running))
            {
                UserProfileLogMessages.JoinedActive(logger, peerHash);
                operation = running;
            }
            else if (!refresh
                && cache.TryGetValue(key, out CachedProfile? existing)
                && utcNow() - existing.Profile.ObservedAt < Freshness)
            {
                existing.LastAccess = utcNow();
                UserProfileLogMessages.ReusedCached(
                    logger,
                    peerHash,
                    (long)(utcNow() - existing.Profile.ObservedAt).TotalMilliseconds);
                return existing;
            }
            else
            {
                CancellationToken connectionToken;
                lock (connectionSync)
                    connectionToken = connectionLifetime.Token;
                operation = RunAndStoreAsync(key, peerHash, connectionToken);
                active.Add(key, operation);
            }
        }
        finally
        {
            gate.Release();
        }

        // The daemon owns the operation; cancelling one HTTP/CLI waiter detaches.
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        Task startup;
        lock (sessionSync)
            startup = sessionStartup ??= ensureSessionStarted(lifetime.Token);

        try
        {
            await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (sessionSync)
            {
                if (ReferenceEquals(sessionStartup, startup))
                    sessionStartup = null;
            }
            throw new UserProfileUnavailableException("Soulseek is unavailable.", ex);
        }
    }

    private async Task<CachedProfile> RunAndStoreAsync(
        ProfileKey key,
        string peerHash,
        CancellationToken connectionToken)
    {
        using OperationLogScope operationLog = OperationLogScope.Start(
            logger, "user-profile.fetch", peerHash);
        bool networkAcquired = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            connectionToken);
        try
        {
            await networkGate.WaitAsync(operation.Token).ConfigureAwait(false);
            networkAcquired = true;
            Task<SectionResult<UserStatus>> statusTask = CaptureAsync(
                token => transport.GetStatusAsync(key.Username, token),
                operation.Token,
                sectionTimeout,
                logger,
                peerHash,
                "status");
            Task<SectionResult<UserInfo>> infoTask = CaptureAsync(
                token => transport.GetInfoAsync(key.Username, token),
                operation.Token,
                sectionTimeout,
                logger,
                peerHash,
                "info");
            Task<SectionResult<UserStatistics>> statisticsTask = CaptureAsync(
                token => transport.GetStatisticsAsync(key.Username, token),
                operation.Token,
                sectionTimeout,
                logger,
                peerHash,
                "statistics");
            await Task.WhenAll(statusTask, infoTask, statisticsTask).ConfigureAwait(false);

            SectionResult<UserStatus> status = await statusTask.ConfigureAwait(false);
            SectionResult<UserInfo> info = await infoTask.ConfigureAwait(false);
            SectionResult<UserStatistics> statistics = await statisticsTask.ConfigureAwait(false);

            (UserProfileSectionDto PictureSection, UserPicture? Picture) picture =
                await ResolvePictureAsync(info, operation.Token, logger, peerHash).ConfigureAwait(false);
            DateTimeOffset observedAt = utcNow();
            var profile = new UserProfileDto(
                key.Username,
                false,
                false,
                Presence(status),
                status.Section,
                info.Section,
                statistics.Section,
                picture.PictureSection,
                info.Value is { } infoValue
                    ? UserProfileText.NormalizeDescription(infoValue.Description)
                    : null,
                NonNegative(statistics.Value?.FileCount),
                NonNegative(statistics.Value?.DirectoryCount),
                NonNegative(statistics.Value?.AverageSpeed),
                UploadCount(statistics.Value?.UploadCount),
                NonNegativeInt(info.Value?.UploadSlots),
                NonNegativeInt(info.Value?.QueueLength),
                info.Value?.HasFreeUploadSlot,
                picture.Picture is { } image
                    ? new UserPictureRefDto(
                        $"/api/users/{Uri.EscapeDataString(key.Username)}/picture",
                        image.MediaType,
                        image.Bytes.Length,
                        image.ETag)
                    : null,
                observedAt);
            var cached = new CachedProfile(profile, picture.Picture, observedAt);

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                cache[key] = cached;
                EvictCache(key);
            }
            finally
            {
                gate.Release();
            }
            ResourceSectionState[] states =
            [
                status.Section.State,
                info.Section.State,
                statistics.Section.State,
                picture.PictureSection.State,
            ];
            int availableSections = states.Count(state => state == ResourceSectionState.Available);
            string outcome = availableSections == states.Length
                ? "available"
                : $"partial-{availableSections}-of-{states.Length}";
            if (availableSections == states.Length)
            {
                operationLog.Succeeded(
                    outcome,
                    itemCount: availableSections,
                    byteCount: picture.Picture?.Bytes.Length);
            }
            else
            {
                operationLog.Degraded(
                    outcome,
                    itemCount: availableSections,
                    byteCount: picture.Picture?.Bytes.Length);
            }
            return cached;
        }
        catch (OperationCanceledException ex) when (!lifetime.IsCancellationRequested)
        {
            operationLog.Failed(ex, "connection-lost");
            throw new UserProfileUnavailableException("Soulseek connection was lost.", ex);
        }
        catch (OperationCanceledException)
        {
            operationLog.Cancelled();
            throw;
        }
        catch (Exception exception)
        {
            operationLog.Failed(exception);
            throw;
        }
        finally
        {
            if (networkAcquired)
                networkGate.Release();
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                active.Remove(key);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static async Task<SectionResult<T>> CaptureAsync<T>(
        Func<CancellationToken, Task<T>> request,
        CancellationToken operationToken,
        TimeSpan timeoutDuration,
        ILogger logger,
        string peerHash,
        string section)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        timeout.CancelAfter(timeoutDuration);
        try
        {
            T value = await request(timeout.Token).ConfigureAwait(false);
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.Available, null),
                value);
        }
        catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
        {
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.TimedOut, "timeout"),
                null);
        }
        catch (UserOfflineException)
        {
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.Unavailable, "offline"),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            UserProfileLogMessages.SectionFailed(
                logger, exception, peerHash, section);
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.Unavailable, "request-failed"),
                null);
        }
    }

    private static async Task<(UserProfileSectionDto PictureSection, UserPicture? Picture)>
        ResolvePictureAsync(
            SectionResult<UserInfo> info,
            CancellationToken cancellationToken,
            ILogger logger,
            string peerHash)
    {
        if (info.Value is null)
            return (info.Section, null);
        if (!info.Value.HasPicture || info.Value.Picture is null)
            return (new UserProfileSectionDto(ResourceSectionState.Available, null), null);

        try
        {
            UserPicture picture = await UserPictureCodec.ValidateRemoteAsync(
                info.Value.Picture,
                cancellationToken).ConfigureAwait(false);
            return (new UserProfileSectionDto(ResourceSectionState.Available, null), picture);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new UserProfileSectionDto(ResourceSectionState.TimedOut, "decode-timeout"), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            UserProfileLogMessages.PictureRejected(logger, exception, peerHash);
            return (new UserProfileSectionDto(ResourceSectionState.Unavailable, "invalid-image"), null);
        }
    }

    private void EvictCache(ProfileKey newest)
    {
        long pictureBytes = cache.Values.Sum(value => (long)(value.Picture?.Bytes.Length ?? 0));
        while (cache.Count > MaximumCachedProfiles || pictureBytes > PictureCacheByteTarget)
        {
            KeyValuePair<ProfileKey, CachedProfile>[] victims = cache
                .Where(pair => !pair.Key.Equals(newest))
                .OrderBy(pair => pair.Value.LastAccess)
                .Take(1)
                .ToArray();
            if (victims.Length == 0)
                break;
            pictureBytes -= victims[0].Value.Picture?.Bytes.Length ?? 0;
            cache.Remove(victims[0].Key);
        }
    }

    private static ApiPresence Presence(SectionResult<UserStatus> status)
        => status.Value?.Presence switch
        {
            SoulseekPresence.Online => ApiPresence.Online,
            SoulseekPresence.Away => ApiPresence.Away,
            SoulseekPresence.Offline => ApiPresence.Offline,
            _ => ApiPresence.Unknown,
        };

    private static long? NonNegative(int? value) => value >= 0 ? value.Value : null;
    private static int? NonNegativeInt(int? value) => value >= 0 ? value : null;
    private static int? UploadCount(long? value)
        => value is >= 0 and <= int.MaxValue ? (int)value.Value : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;
        lifetime.Cancel();
        CancellationTokenSource connection;
        lock (connectionSync)
        {
            connection = connectionLifetime;
            connectionLifetime = new CancellationTokenSource();
        }
        connection.Cancel();
        Task? pendingPresence;
        lock (presenceSync)
            pendingPresence = activePresenceOperations == 0
                ? null
                : (presenceDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        Task[] pending;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            pending = active.Values.ToArray();
            cache.Clear();
        }
        finally
        {
            gate.Release();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* disposal observes and suppresses daemon-lifetime cancellation */ }
        if (pendingPresence is not null)
            await pendingPresence.ConfigureAwait(false);
        connection.Dispose();
        connectionLifetime.Dispose();
        lifetime.Dispose();
        networkGate.Dispose();
        gate.Dispose();
    }

    private sealed record SectionResult<T>(UserProfileSectionDto Section, T? Value)
        where T : class;

    private sealed class CachedProfile(
        UserProfileDto profile,
        UserPicture? picture,
        DateTimeOffset lastAccess)
    {
        public UserProfileDto Profile { get; } = profile;
        public UserPicture? Picture { get; } = picture;
        public DateTimeOffset LastAccess { get; set; } = lastAccess;
    }

    private readonly record struct ProfileKey(string LocalAccount, string Username);
}

internal static partial class UserProfileLogMessages
{
    [LoggerMessage(
        EventId = 4100,
        EventName = "user-profile.session-unavailable",
        Level = LogLevel.Error,
        Message = "User profile session unavailable for peer hash {PeerHash}")]
    public static partial void SessionUnavailable(
        ILogger logger, Exception exception, string peerHash);

    [LoggerMessage(
        EventId = 4101,
        EventName = "user-profile.joined-active",
        Level = LogLevel.Debug,
        Message = "Joined active user profile acquisition for peer hash {PeerHash}")]
    public static partial void JoinedActive(ILogger logger, string peerHash);

    [LoggerMessage(
        EventId = 4102,
        EventName = "user-profile.reused-cache",
        Level = LogLevel.Debug,
        Message = "Reused cached user profile for peer hash {PeerHash} at age {AgeMs} ms")]
    public static partial void ReusedCached(ILogger logger, string peerHash, long ageMs);

    [LoggerMessage(
        EventId = 4103,
        EventName = "user-profile.section-failed",
        Level = LogLevel.Warning,
        Message = "User profile {Section} section failed for peer hash {PeerHash}")]
    public static partial void SectionFailed(
        ILogger logger, Exception exception, string peerHash, string section);

    [LoggerMessage(
        EventId = 4104,
        EventName = "user-profile.picture-rejected",
        Level = LogLevel.Debug,
        Message = "User profile picture was rejected for peer hash {PeerHash}")]
    public static partial void PictureRejected(
        ILogger logger, Exception exception, string peerHash);
}
