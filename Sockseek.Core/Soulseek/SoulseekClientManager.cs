using Soulseek;
using System.Security.Cryptography;
using System.Net.Sockets;
using Sockseek.Core.Settings;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.UserProfiles;

namespace Sockseek.Core.Services;

public static class SockseekSoulseekClientIdentity
{
    // Soulseek.NET requires each application to use a unique minor version.
    // Sockseek uses the 800850000-800859999 range.
    public const int MinorVersion = 800850000;
}

public sealed class SoulseekConnectionUnavailableException : InvalidOperationException
{
    public SoulseekConnectionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class SoulseekClientManager : IDisposable, IAsyncDisposable
{
    // Bounds peer writes that have already consumed a browse stream but have
    // stalled in the library's connection writer. Per-download stale detection
    // normally fires sooner.
    internal const int PeerConnectionInactivityTimeoutMilliseconds = 300_000;
    private const string KickedFromServerMessage =
        "Soulseek server kicked this client, probably because the same account logged in elsewhere.";

    private readonly EngineSettings _initialSettings;
    private readonly ISoulseekInboundRequestRouter? inboundRouter;
    private readonly LocalUserProfile? localProfile;
    private readonly Func<TimeSpan, CancellationToken, Task> monitorDelay;
    private readonly ILogger<SoulseekClientManager> logger;
    private readonly RepeatedWarningGate reconnectWarningGate = new();
    private ISoulseekClient? _client;
    private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim connectionStateChanged = new(0, 1);
    private TaskCompletionSource _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object stateLock = new();
    private Exception? _fatalException;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _disposeState;
    private string? loggedInUsername;

    public event Action<SoulseekClientStates>? StateChanged;
    public event Action<ISoulseekClient>? ClientCreated;

    public ISoulseekClient? Client => _client;

    /// <summary>The concrete account name used by the active login, including random logins.</summary>
    public string? LoggedInUsername
    {
        get
        {
            lock (stateLock)
                return loggedInUsername;
        }
    }

    public SoulseekClientStates State => _client?.State ?? SoulseekClientStates.None;

    public bool IsConnectedAndLoggedIn =>
        _client != null &&
        _client.State.HasFlag(SoulseekClientStates.Connected) &&
        _client.State.HasFlag(SoulseekClientStates.LoggedIn);

    public bool HasFatalError
    {
        get
        {
            lock (stateLock)
                return _fatalException != null;
        }
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            if (_fatalException != null)
                return Task.FromException(_fatalException);
        }

        if (IsConnectedAndLoggedIn) return Task.CompletedTask;

        return _readyTcs.Task.WaitAsync(cancellationToken);
    }

    public SoulseekClientManager(
        EngineSettings initialSettings,
        ISoulseekClient? client = null,
        ISoulseekInboundRequestRouter? inboundRouter = null,
        LocalUserProfile? localProfile = null,
        ILogger<SoulseekClientManager>? logger = null)
        : this(initialSettings, client, inboundRouter, Task.Delay, localProfile, logger)
    {
    }

    internal SoulseekClientManager(
        EngineSettings initialSettings,
        ISoulseekClient? client,
        ISoulseekInboundRequestRouter? inboundRouter,
        Func<TimeSpan, CancellationToken, Task> monitorDelay,
        LocalUserProfile? localProfile = null,
        ILogger<SoulseekClientManager>? logger = null)
    {
        _initialSettings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        this.monitorDelay = monitorDelay ?? throw new ArgumentNullException(nameof(monitorDelay));
        this.inboundRouter = inboundRouter;
        this.localProfile = localProfile;
        this.logger = logger ?? NullLogger<SoulseekClientManager>.Instance;
        if (client != null)
        {
            _client = client;
            AttachClientEvents(_client);
            if (IsConnectedAndLoggedIn)
                _readyTcs.TrySetResult();
            StartMonitoring();
        }
    }

    private void AttachClientEvents(ISoulseekClient client)
    {
        client.KickedFromServer += OnKickedFromServer;
        client.StateChanged += OnStateChanged;
        client.ExcludedSearchPhrasesReceived += OnExcludedSearchPhrasesReceived;
    }

    private void OnExcludedSearchPhrasesReceived(
        object? sender,
        IReadOnlyCollection<string> phrases)
    {
        try
        {
            inboundRouter?.TryUpdateExcludedSearchPhrases(phrases);
        }
        catch (Exception ex)
        {
            // A malformed server resource may disable search serving, but must
            // never escape the library event callback and destabilize session
            // reconnect or unrelated transfers.
            SoulseekLogMessages.ExcludedPhrasesFailed(logger, ex);
        }
    }

    private void OnStateChanged(object? sender, SoulseekClientStateChangedEventArgs e)
    {
        if (!IsConnectedAndLoggedIn)
        {
            lock (stateLock)
            {
                if (_fatalException is null && _readyTcs.Task.IsCompleted)
                    _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        // Reconnection is driven by the client's state event. The bounded signal
        // coalesces bursts without queuing one monitor wake-up per transition.
        if (Volatile.Read(ref _disposeState) == 0)
        {
            try
            {
                connectionStateChanged.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
                // Disposal can race an in-flight library event callback.
            }
        }

        Action<SoulseekClientStates>? handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (Action<SoulseekClientStates> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(State);
            }
            catch (Exception ex)
            {
                SoulseekLogMessages.StateObserverFailed(logger, ex);
            }
        }
    }

    private void OnKickedFromServer(object? sender, EventArgs e)
    {
        if (_initialSettings.AutoReconnectAfterKickedFromServer)
        {
            SoulseekLogMessages.KickedReconnecting(logger);
            return;
        }

        SoulseekLogMessages.KickedStopping(logger);
        MarkFatal(new SoulseekConnectionUnavailableException(
            KickedFromServerMessage,
            new KickedFromServerException(KickedFromServerMessage)));
    }

    private void StartMonitoring()
    {
        if (HasFatalError) return;
        if (_monitorTask != null) return;
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _monitorTask = Task.Run(() => MonitorConnectionLoopAsync(token));
    }

    private void MarkFatal(Exception exception)
    {
        lock (stateLock)
        {
            _fatalException ??= exception;
            if (_readyTcs.TrySetException(_fatalException))
            {
                // Existing readiness waiters still receive the fatal exception.
                // If there are no waiters, observing this broadcast task here
                // prevents a second, unobserved copy of the already propagated
                // login failure from surfacing later on the finalizer thread.
                _ = _readyTcs.Task.Exception;
            }
        }

        _monitorCts?.Cancel();
    }

    /// <summary>
    /// Ensures the Soulseek client is created, connected, and logged in.
    /// Uses the provided config for login credentials if login is needed.
    /// </summary>
    /// <param name="loginSettings">Configuration containing potentially updated credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if login fails after retries.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public async Task EnsureConnectedAndLoggedInAsync(EngineSettings loginSettings, CancellationToken cancellationToken = default)
    {
        if (IsConnectedAndLoggedIn)
        {
            CaptureExistingLogin(loginSettings);
            return;
        }

        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (IsConnectedAndLoggedIn)
            {
                CaptureExistingLogin(loginSettings);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (_client == null)
            {
                _client = CreateClientInstance(_initialSettings);
                AttachClientEvents(_client);
                PublishClientCreated(_client);
            }

            // Some daemon-owned clients (notably mock-file mode) are already
            // connected when created and therefore never enter LoginInternalAsync.
            // They must still complete the shared readiness signal.
            if (IsConnectedAndLoggedIn)
            {
                CaptureExistingLogin(loginSettings);
                _readyTcs.TrySetResult();
                StartMonitoring();
                return;
            }

            var missingCredentialMessage = GetMissingCredentialMessage(loginSettings);
            if (missingCredentialMessage != null)
                throw new InvalidOperationException(missingCredentialMessage);

            await LoginInternalAsync(_client, loginSettings, cancellationToken);
            _readyTcs.TrySetResult();
            StartMonitoring();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = CreateConnectionFailure(ex);

            if (IsTransient(ex))
            {
                SoulseekLogMessages.SessionStartFailed(logger, ex);
                StartMonitoring(); // Ensure monitoring starts even on transient failure so we can retry
            }
            else
            {
                MarkFatal(failure);
            }

            throw failure;
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    private void CaptureExistingLogin(EngineSettings settings)
    {
        if (_client is null)
            return;
        string? username = _client.Username;
        if (string.IsNullOrWhiteSpace(username) && !settings.UseRandomLogin)
            username = settings.Username;
        if (string.IsNullOrWhiteSpace(username))
            return;
        lock (stateLock)
            loggedInUsername ??= username;
    }

    private static SoulseekConnectionUnavailableException CreateConnectionFailure(Exception ex)
        => new(
            IsKickedFromServer(ex)
                ? KickedFromServerMessage
                : $"Soulseek login failed: {ExceptionText.Summary(ex)}",
            ex);

    private void PublishClientCreated(ISoulseekClient client)
    {
        Action<ISoulseekClient>? handlers = ClientCreated;
        if (handlers is null)
            return;
        foreach (Action<ISoulseekClient> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try { handler(client); }
            catch (Exception ex)
            {
                SoulseekLogMessages.ClientCreatedObserverFailed(logger, ex);
            }
        }
    }

    private bool IsTransient(Exception? e)
    {
        if (IsKickedFromServer(e))
            return _initialSettings.AutoReconnectAfterKickedFromServer;

        while (e != null)
        {
            if (e is OperationCanceledException) return true;
            if (e is Soulseek.AddressException || e is System.TimeoutException || e is System.Net.Sockets.SocketException) return true;
            if (e.GetType().Name.Contains("ConnectionException")) return true;
            if (e.GetType().Name.Contains("SoulseekClientException")) return true;
            e = e.InnerException;
        }
        return false;
    }

    private static bool IsKickedFromServer(Exception? e)
    {
        while (e != null)
        {
            if (e is KickedFromServerException || e.GetType().Name == nameof(KickedFromServerException)) return true;
            e = e.InnerException;
        }
        return false;
    }

    private string? GetMissingCredentialMessage(EngineSettings settings)
    {
        if (settings.UseRandomLogin
            || !string.IsNullOrWhiteSpace(settings.MockFilesDir)
            || !string.IsNullOrWhiteSpace(_initialSettings.MockFilesDir))
        {
            return null;
        }

        var missingUsername = string.IsNullOrWhiteSpace(settings.Username);
        var missingPassword = string.IsNullOrWhiteSpace(settings.Password);

        return (missingUsername, missingPassword) switch
        {
            (true, true) => "Missing Soulseek username and password. Provide --user and --pass, or configure username/password.",
            (true, false) => "Missing Soulseek username. Provide --user, or configure username.",
            (false, true) => "Missing Soulseek password. Provide --pass, or configure password.",
            _ => null,
        };
    }

    private async Task MonitorConnectionLoopAsync(CancellationToken ct)
    {
        int retryDelay = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsConnectedAndLoggedIn)
                {
                    retryDelay = 1;
                    // StateChanged is the normal wake-up path. The timeout is a
                    // low-frequency safety net for an implementation that mutates
                    // State without raising the interface event.
                    await connectionStateChanged.WaitAsync(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                if (HasFatalError)
                    break;

                if (reconnectWarningGate.TryAcquire(out long suppressedCount))
                    SoulseekLogMessages.ConnectionLost(logger, retryDelay, suppressedCount);
                await monitorDelay(TimeSpan.FromSeconds(retryDelay), ct);

                await EnsureConnectedAndLoggedInAsync(_initialSettings, ct);
                retryDelay = 1;
                SoulseekLogMessages.Reconnected(logger);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!IsTransient(ex))
                {
                    MarkFatal(ex);
                    SoulseekLogMessages.PermanentFailure(logger, ex);
                    break;
                }

                SoulseekLogMessages.ReconnectAttemptFailed(logger, ex);
                retryDelay = Math.Min(retryDelay * 2, 8);
            }
        }
    }

    private ISoulseekClient CreateClientInstance(EngineSettings settings)
    {
        SoulseekLogMessages.CreatingClient(logger);
        if (!string.IsNullOrEmpty(settings.MockFilesDir))
        {
            SoulseekLogMessages.UsingLocalClient(logger);
            return LocalFilesSoulseekClient.FromLocalPaths(
                settings.MockFilesReadTags,
                settings.MockFilesSlow,
                settings.MockFilesFailDownloads,
                logger,
                settings.MockFilesDir);
        }
        else
        {
            SoulseekLogMessages.ConfiguringNetworkClient(logger);
            int startingToken = CreateRandomStartingToken();
            return new SoulseekClient(
                SockseekSoulseekClientIdentity.MinorVersion,
                CreateClientOptions(settings, startingToken, inboundRouter, localProfile));
        }
    }

    internal static int CreateRandomStartingToken()
        => RandomNumberGenerator.GetInt32(1, int.MaxValue);

    internal static SoulseekClientOptions CreateClientOptions(
        EngineSettings settings,
        int startingToken,
        ISoulseekInboundRequestRouter? inboundRouter = null,
        LocalUserProfile? localProfile = null)
    {
        var serverConnectionOptions = new ConnectionOptions(
            connectTimeout: settings.ConnectTimeout,
            configureSocket: (socket) =>
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
            });

        var transferConnectionOptions = new ConnectionOptions(
            inactivityTimeout: PeerConnectionInactivityTimeoutMilliseconds,
            configureSocket: (socket) =>
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
            });

        Task<UserInfo> userInfoResolver(string username, System.Net.IPEndPoint ip) => Task.FromResult(new UserInfo(
            description: localProfile?.Description
                ?? UserProfileText.NormalizeDescription(settings.UserDescription),
            uploadSlots: 1,
            queueLength: 0,
            hasFreeUploadSlot: true,
            picture: localProfile?.Picture?.Bytes
        ));

        int maximumUploadSpeed = settings.Uploads.SpeedLimitKiBPerSecond is { } kib
            ? checked(kib * 1_024)
            : int.MaxValue;
        Func<string, int, SearchQuery, Task<SearchResponse?>>? searchResolver =
            inboundRouter is null ? null : inboundRouter.ResolveSearchAsync;
        Func<string, System.Net.IPEndPoint, Task<BrowseResponse>>? browseResolver =
            inboundRouter is null ? null : inboundRouter.ResolveBrowseAsync;
        Func<string, System.Net.IPEndPoint, int, string, Task<IEnumerable<Soulseek.Directory>>>?
            directoryResolver =
                inboundRouter is null ? null : inboundRouter.ResolveDirectoryAsync;
        Func<string, System.Net.IPEndPoint, Task<UserInfo>> resolvedUserInfo =
            inboundRouter is null ? userInfoResolver : inboundRouter.ResolveUserInfoAsync;
        Func<string, System.Net.IPEndPoint, string, Task>? enqueueUpload =
            inboundRouter is null ? null : inboundRouter.EnqueueUploadAsync;
        Func<string, System.Net.IPEndPoint, string, Task<int?>>? queueResolver =
            inboundRouter is null ? null : inboundRouter.ResolvePlaceInQueueAsync;

        var clientOptionsBuilder = new SoulseekClientOptions(
            transferConnectionOptions: transferConnectionOptions,
            serverConnectionOptions: serverConnectionOptions,
            listenPort: settings.ListenPort ?? 49998,
            maximumConcurrentSearches: int.MaxValue, // this is limited later in the searcher code
            maximumConcurrentUploads: settings.Uploads.Slots,
            maximumUploadSpeed: maximumUploadSpeed,
            searchResponseResolver: searchResolver,
            browseResponseResolver: browseResolver,
            directoryContentsResolver: directoryResolver,
            userInfoResolver: resolvedUserInfo,
            enqueueDownload: enqueueUpload,
            placeInQueueResolver: queueResolver,
            autoAcknowledgePrivateMessages: false,
            acceptPrivateRoomInvitations: true,
            startingToken: startingToken
        );

        if (settings.ListenPort == null)
        {
            // No listen port: create client without listener to avoid bind failures
            clientOptionsBuilder = new SoulseekClientOptions(
                transferConnectionOptions: transferConnectionOptions,
                serverConnectionOptions: serverConnectionOptions,
                enableListener: false,
                maximumConcurrentSearches: int.MaxValue,
                maximumConcurrentUploads: settings.Uploads.Slots,
                maximumUploadSpeed: maximumUploadSpeed,
                searchResponseResolver: searchResolver,
                browseResponseResolver: browseResolver,
                directoryContentsResolver: directoryResolver,
                userInfoResolver: resolvedUserInfo,
                enqueueDownload: enqueueUpload,
                placeInQueueResolver: queueResolver,
                autoAcknowledgePrivateMessages: false,
                acceptPrivateRoomInvitations: true,
                startingToken: startingToken
            );
        }

        return clientOptionsBuilder;
    }

    /// <summary>
    /// Internal login logic extracted from DownloaderApplication.
    /// </summary>
    private async Task LoginInternalAsync(ISoulseekClient client, EngineSettings settings, CancellationToken cancellationToken)
    {
        string user = settings.Username ?? "";
        string pass = settings.Password ?? "";

        if (settings.UseRandomLogin)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = RandomNumberGenerator.GetString(chars, 10);
            pass = RandomNumberGenerator.GetString(chars, 10);
        }

        if (settings.UseRandomLogin)
            SoulseekLogMessages.RandomLoginStarting(logger);
        else
            SoulseekLogMessages.LoginStarting(logger);

        cancellationToken.ThrowIfCancellationRequested();
        string? previousUser;
        lock (stateLock)
        {
            previousUser = loggedInUsername;
            loggedInUsername = user;
        }
        try
        {
            // Protocol events can be raised as ConnectAsync completes. Publish
            // the account first so those callbacks are partitioned correctly,
            // including random-login reconnects.
            await client.ConnectAsync(user, pass);
        }
        catch
        {
            lock (stateLock)
            {
                if (string.Equals(loggedInUsername, user, StringComparison.Ordinal))
                    loggedInUsername = previousUser;
            }
            throw;
        }
        SoulseekLogMessages.LoginCompleted(logger);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        MarkFatal(new ObjectDisposedException(nameof(SoulseekClientManager)));
        if (_client != null)
        {
            _client.KickedFromServer -= OnKickedFromServer;
            _client.StateChanged -= OnStateChanged;
            _client.ExcludedSearchPhrasesReceived -= OnExcludedSearchPhrasesReceived;
        }
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SoulseekLogMessages.MonitorDisposeFailed(logger, ex);
            }
        }
        _client?.Dispose();
        _monitorCts?.Dispose();
        connectionStateChanged.Dispose();
        _initializationSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
