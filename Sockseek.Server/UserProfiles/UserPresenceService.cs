using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Sharing;
using Soulseek;

namespace Sockseek.Server.UserProfiles;

/// <summary>
/// Owns server watches for active readers, not for cached profiles or chat history.
/// The last reader releases the watch; a replacement waits for that release so a
/// late Unwatch can never remove the replacement's subscription.
/// </summary>
public sealed class UserPresenceService(
    Func<ISoulseekClient?> clientProvider,
    Func<CancellationToken, Task> ensureSessionStarted,
    ILogger<UserPresenceService> logger) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Watch> watches = new(StringComparer.Ordinal);
    private CancellationTokenSource connection = new();
    private TaskCompletionSource connectionChanged = NewSignal();
    private ISoulseekClient? client;
    private bool connected = IsConnected(clientProvider()?.State ?? SoulseekClientStates.None);
    private bool disposed;

    public async IAsyncEnumerable<UserPresenceDto> ObserveAsync(string username,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        username = PeerUsername.Validate(username);
        var reader = Channel.CreateBounded<UserPresenceDto>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        Watch watch;
        while (true)
        {
            Task? retiring = null;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (watches.TryGetValue(username, out watch!) && watch.Stop.IsCancellationRequested)
                    retiring = watch.Work;
                else
                {
                    if (watch is null)
                    {
                        watch = new Watch(username);
                        watches.Add(username, watch);
                        watch.Work = Task.Run(() => RunAsync(watch));
                    }
                    watch.Readers.Add(reader.Writer);
                    reader.Writer.TryWrite(watch.Current);
                }
            }
            if (retiring is null) break;
            await retiring.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await foreach (var value in reader.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return value;
        }
        finally
        {
            bool last;
            lock (sync)
            {
                watch.Readers.Remove(reader.Writer);
                last = watch.Readers.Count == 0;
                if (last) watch.Stop.Cancel();
            }
            if (last)
            {
                await watch.Work.ConfigureAwait(false);
                watch.Stop.Dispose();
            }
        }
    }

    // HTTP/CLI snapshots use the same owner; they do not fetch pictures or create
    // a second status cache. An existing live watch makes this a local read.
    public async Task<UserPresenceDto> GetAsync(string username, CancellationToken cancellationToken = default)
    {
        await foreach (var value in ObserveAsync(username, cancellationToken).ConfigureAwait(false))
            if (value.State != UserPresenceState.Checking) return value;
        throw new ObjectDisposedException(nameof(UserPresenceService));
    }

    public void OnSoulseekStateChanged(SoulseekClientStates state)
    {
        bool ready = IsConnected(state);
        lock (sync)
        {
            if (disposed || ready == connected) return;
            connected = ready;
            if (!ready)
            {
                connection.Cancel();
                connection.Dispose();
                connection = new CancellationTokenSource();
                foreach (var watch in watches.Values) Publish(watch, UserPresenceState.Disconnected);
            }
            connectionChanged.TrySetResult();
            connectionChanged = NewSignal();
        }
    }

    private async Task RunAsync(Watch watch)
    {
        try
        {
            try
            {
                // Closing a view cancels only its wait, never the daemon's shared login.
                await ensureSessionStarted(CancellationToken.None).WaitAsync(watch.Stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (watch.Stop.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Presence waiting for Soulseek for {PeerHash}", LogIdentity.PeerHash(watch.Username));
                // Keep the reader attached: a later login restores its watch too.
            }
            while (!watch.Stop.IsCancellationRequested)
            {
                ISoulseekClient? session;
                CancellationToken sessionToken;
                Task changed;
                long revision;
                lock (sync)
                {
                    changed = connectionChanged.Task;
                    sessionToken = connection.Token;
                    session = connected ? clientProvider() : null;
                    if (session is not null && !ReferenceEquals(client, session))
                    {
                        if (client is not null) client.UserStatusChanged -= OnStatusChanged;
                        client = session;
                        client.UserStatusChanged += OnStatusChanged;
                    }
                    watch.Client = session;
                    revision = watch.StatusRevision;
                    Publish(watch, session is null ? UserPresenceState.Disconnected : UserPresenceState.Checking);
                }
                if (session is null)
                {
                    await changed.WaitAsync(watch.Stop.Token).ConfigureAwait(false);
                    continue;
                }
                using var request = CancellationTokenSource.CreateLinkedTokenSource(watch.Stop.Token, sessionToken);
                try
                {
                    UserData data = await session.WatchUserAsync(watch.Username, request.Token).ConfigureAwait(false);
                    lock (sync)
                    {
                        // A pushed update may beat the initial AddUser response.
                        if (!request.IsCancellationRequested && revision == watch.StatusRevision)
                            Publish(watch, ToState(data.Status));
                    }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    lock (sync)
                        if (!request.IsCancellationRequested) Publish(watch, UserPresenceState.Unavailable);
                    logger.LogDebug(exception, "Presence watch failed for {PeerHash}", LogIdentity.PeerHash(watch.Username));
                }
                try
                {
                    if (!request.IsCancellationRequested)
                        await changed.WaitAsync(watch.Stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (watch.Stop.IsCancellationRequested) { }
                finally
                {
                    // Cancellation/timeout does not prove that AddUser wasn't sent.
                    // Balance every attempt on this session, including failed ones.
                    if (!sessionToken.IsCancellationRequested)
                    {
                        try { await session.UnwatchUserAsync(watch.Username).ConfigureAwait(false); }
                        catch (Exception exception)
                        {
                            logger.LogDebug(exception, "Presence release failed for {PeerHash}", LogIdentity.PeerHash(watch.Username));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (watch.Stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (sync) Publish(watch, UserPresenceState.Unavailable);
            logger.LogDebug(exception, "Presence startup failed for {PeerHash}", LogIdentity.PeerHash(watch.Username));
        }
        finally
        {
            lock (sync)
            {
                watches.Remove(watch.Username);
                foreach (var reader in watch.Readers) reader.TryComplete();
            }
            // Stop remains readable by iterators that are finishing concurrently.
        }
    }

    private void OnStatusChanged(object? sender, UserStatus status)
    {
        lock (sync)
        {
            if (!connected || disposed || !watches.TryGetValue(status.Username, out var watch)
                || !ReferenceEquals(sender, watch.Client) || watch.Stop.IsCancellationRequested) return;
            watch.StatusRevision++;
            Publish(watch, ToState(status.Presence));
        }
    }

    private static void Publish(Watch watch, UserPresenceState state)
    {
        var next = new UserPresenceDto(watch.Username, state);
        if (next == watch.Current) return;
        watch.Current = next;
        foreach (var reader in watch.Readers) reader.TryWrite(next);
    }

    private static UserPresenceState ToState(UserPresence presence) => presence switch
    {
        UserPresence.Online => UserPresenceState.Online,
        UserPresence.Away => UserPresenceState.Away,
        UserPresence.Offline => UserPresenceState.Offline,
        _ => UserPresenceState.Unknown,
    };

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (client is not null) client.UserStatusChanged -= OnStatusChanged;
            foreach (var watch in watches.Values) watch.Stop.Cancel();
            pending = watches.Values.Select(watch => watch.Work).ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        connection.Dispose();
    }

    private static bool IsConnected(SoulseekClientStates state)
        => state.HasFlag(SoulseekClientStates.Connected) && state.HasFlag(SoulseekClientStates.LoggedIn);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Watch(string username)
    {
        public string Username { get; } = username;
        public UserPresenceDto Current { get; set; } = new(username, UserPresenceState.Checking);
        public HashSet<ChannelWriter<UserPresenceDto>> Readers { get; } = [];
        public CancellationTokenSource Stop { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public ISoulseekClient? Client { get; set; }
        public long StatusRevision { get; set; }
    }
}
