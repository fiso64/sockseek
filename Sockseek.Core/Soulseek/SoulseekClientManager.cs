using Soulseek;
using Soulseek.Diagnostics;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Diagnostics;
using Sockseek.Core.Settings;
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
    private readonly Func<ISoulseekClient, ISoulseekClient>? clientDecorator;
    private int? maximumUploadSpeed;
    private readonly ISoulseekInboundRequestRouter? inboundRouter;
    private readonly LocalUserProfile? localProfile;
    private readonly Func<TimeSpan, CancellationToken, Task> monitorDelay;
    private readonly ILogger<SoulseekClientManager> logger;
    private readonly RepeatedWarningGate reconnectWarningGate = new();
    private readonly RepeatedWarningGate protocolWarningGate = new();
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
    private long unavailableSince;
    private volatile SoulseekClientStates loginPhase;
    private SoulseekConnectionUnavailableException? lastTransientFailure;
    private int loginAttempts; // Serialized by the initialization semaphore.

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
        State.HasFlag(SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);

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
            if (!IsConnectedAndLoggedIn && _readyTcs.Task.IsCompletedSuccessfully)
                _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _readyTcs.Task.WaitAsync(cancellationToken);
        }
    }

    public SoulseekClientManager(
        EngineSettings initialSettings,
        ISoulseekClient? client = null,
        ISoulseekInboundRequestRouter? inboundRouter = null,
        LocalUserProfile? localProfile = null,
        ILogger<SoulseekClientManager>? logger = null,
        Func<ISoulseekClient, ISoulseekClient>? clientDecorator = null)
        : this(initialSettings, client, inboundRouter, Task.Delay, localProfile, logger, clientDecorator)
    {
    }

    internal SoulseekClientManager(
        EngineSettings initialSettings,
        ISoulseekClient? client,
        ISoulseekInboundRequestRouter? inboundRouter,
        Func<TimeSpan, CancellationToken, Task> monitorDelay,
        LocalUserProfile? localProfile = null,
        ILogger<SoulseekClientManager>? logger = null,
        Func<ISoulseekClient, ISoulseekClient>? clientDecorator = null)
    {
        this.clientDecorator = clientDecorator;
        _initialSettings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        maximumUploadSpeed = initialSettings.Uploads.SpeedLimitKiBPerSecond ?? int.MaxValue;
        this.monitorDelay = monitorDelay ?? throw new ArgumentNullException(nameof(monitorDelay));
        this.inboundRouter = inboundRouter;
        this.localProfile = localProfile;
        this.logger = logger ?? NullLogger<SoulseekClientManager>.Instance;
        if (client != null)
        {
            _client = clientDecorator?.Invoke(client) ?? client;
            AttachClientEvents(_client);
            if (IsConnectedAndLoggedIn)
                _readyTcs.TrySetResult();
            StartMonitoring();
        }
    }

    private void AttachClientEvents(ISoulseekClient client)
    {
        client.DiagnosticGenerated += OnProtocolDiagnostic;
        client.KickedFromServer += OnKickedFromServer;
        client.StateChanged += OnStateChanged;
        client.ExcludedSearchPhrasesReceived += OnExcludedSearchPhrasesReceived;
    }

    private void OnProtocolDiagnostic(object? sender, DiagnosticEventArgs diagnostic)
    {
        string message = diagnostic.Message;
        if (message.Length > 2_048) message = message[..2_048] + "…";
        string source = sender?.GetType().Name ?? "Soulseek";
        if (diagnostic.Level == DiagnosticLevel.Warning && protocolWarningGate.TryAcquire(out long suppressed))
            SoulseekLogMessages.ProtocolWarning(logger, diagnostic.Exception, source, message, suppressed);
        else
            SoulseekLogMessages.ProtocolDiagnostic(logger, source, message);
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
        bool lostLogin = e.PreviousState.HasFlag(SoulseekClientStates.LoggedIn) && !e.State.HasFlag(SoulseekClientStates.LoggedIn);
        if (Volatile.Read(ref _disposeState) == 0)
        {
            if (e.State is not (SoulseekClientStates.Disconnecting or SoulseekClientStates.Disconnected or SoulseekClientStates.None))
                loginPhase = e.State;
            if (e.State.HasFlag(SoulseekClientStates.LoggingIn))
                SoulseekLogMessages.ServerConnected(logger, _client?.IPEndPoint);
        }

        if (lostLogin && Volatile.Read(ref _disposeState) == 0)
        {
            lock (stateLock)
            {
                Interlocked.CompareExchange(ref unavailableSince, Stopwatch.GetTimestamp(), 0);
                if (_fatalException is null && _readyTcs.Task.IsCompletedSuccessfully)
                    _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            SoulseekLogMessages.Disconnected(logger, e.Exception, _client?.IPEndPoint,
                BoundedReason(e.Message ?? e.Exception?.Message ?? "No reason supplied"));
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
    /// <exception cref="InvalidOperationException">Thrown if login fails or automatic recovery is in progress.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public Task EnsureConnectedAndLoggedInAsync(EngineSettings loginSettings, CancellationToken cancellationToken = default)
        => ConnectAsync(loginSettings, retry: false, cancellationToken);

    private async Task ConnectAsync(EngineSettings loginSettings, bool retry, CancellationToken cancellationToken)
    {
        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            lock (stateLock)
            {
                if (_fatalException is not null) throw _fatalException;
            }
            if (IsConnectedAndLoggedIn)
            {
                CaptureExistingLogin(loginSettings);
                return;
            }
            // Chat, sharing and downloads can start together. Once an attempt
            // fails, the monitor owns retries and all callers see the same failure.
            if (!retry && lastTransientFailure is not null) throw lastTransientFailure;
            await StartSessionAsync(loginSettings, cancellationToken);
        }
        finally { _initializationSemaphore.Release(); }
    }

    private async Task StartSessionAsync(EngineSettings loginSettings, CancellationToken cancellationToken)
    {
        long attemptStarted = Stopwatch.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_client == null)
            {
                var created = CreateClientInstance(_initialSettings);
                _client = clientDecorator?.Invoke(created) ?? created;
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

            long since = Interlocked.CompareExchange(ref unavailableSince, attemptStarted, 0);
            if (since == 0) since = attemptStarted;
            loginAttempts++;
            loginPhase = SoulseekClientStates.None;
            await LoginInternalAsync(_client, loginSettings, cancellationToken);
            lock (stateLock)
            {
                // The library raises LoggedIn before it finishes session setup.
                if (!IsConnectedAndLoggedIn)
                    throw new SoulseekClientException("Disconnected before Soulseek session setup finished.");
                Interlocked.CompareExchange(ref unavailableSince, 0, since);
                _readyTcs.TrySetResult();
            }
            lastTransientFailure = null;
            SoulseekLogMessages.LoginCompleted(logger, _client.IPEndPoint, loginAttempts, (long)Stopwatch.GetElapsedTime(since).TotalMilliseconds);
            loginAttempts = 0;
            StartMonitoring();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (IsTransient(ex))
            {
                string reason = BoundedReason(ex.GetBaseException().Message);
                string phase = loginPhase switch
                {
                    SoulseekClientStates.None => "preparing the connection",
                    SoulseekClientStates.Connecting => "opening the server connection",
                    _ when loginPhase.HasFlag(SoulseekClientStates.LoggedIn) => "sending session configuration",
                    _ => "logging in after TCP connected",
                };
                long elapsed = (long)Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds;
                if (reconnectWarningGate.TryAcquire(out long suppressedCount))
                    SoulseekLogMessages.ConnectionLost(logger, loginAttempts, phase, elapsed, reason, suppressedCount);
                else
                    SoulseekLogMessages.SessionStartFailed(logger, loginAttempts, phase, elapsed, reason);
                lastTransientFailure = CreateConnectionFailure(ex);
                StartMonitoring();
                throw lastTransientFailure;
            }

            var failure = CreateConnectionFailure(ex);
            if (!HasFatalError) SoulseekLogMessages.PermanentFailure(logger, failure);
            MarkFatal(failure);
            throw failure;
        }
    }

    private static string BoundedReason(string reason) => reason.Length <= 512 ? reason : reason[..512] + "…";

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

        bool transient = false;
        while (e != null)
        {
            // Inspect the full chain: a generic connection wrapper must not turn
            // rejected credentials into an endless background reconnect loop.
            if (e is LoginRejectedException) return false;
            transient |= e is OperationCanceledException or SoulseekClientException
                or TimeoutException or System.Net.Sockets.SocketException;
            e = e.InnerException;
        }
        return transient;
    }

    private static bool IsKickedFromServer(Exception? e)
    {
        while (e != null)
        {
            if (e is KickedFromServerException) return true;
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
                    // StateChanged is the normal wake-up path. The timeout is a
                    // low-frequency safety net for an implementation that mutates
                    // State without raising the interface event.
                    if (!await connectionStateChanged.WaitAsync(TimeSpan.FromSeconds(30), ct) && IsConnectedAndLoggedIn)
                        retryDelay = 1; // A short-lived login should not reset outage backoff.
                    continue;
                }

                if (HasFatalError)
                    break;

                var delay = TimeSpan.FromMilliseconds(retryDelay * 1000 + Random.Shared.Next(1000));
                SoulseekLogMessages.ReconnectScheduled(logger, delay);
                await monitorDelay(delay, ct);
                await ConnectAsync(_initialSettings, retry: true, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!IsTransient(ex))
                {
                    MarkFatal(ex);
                    break;
                }
            }
            retryDelay = Math.Min(retryDelay * 2, 300);
        }
    }

    public async Task UpdateUploadSpeedLimitAsync(int? kibPerSecond, CancellationToken cancellationToken = default)
    {
        // Soulseek.NET accepts KiB/s here, unlike search responses (bytes/s).
        int speed = kibPerSecond ?? int.MaxValue;
        await _initializationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (maximumUploadSpeed == speed) return;
            if (_client is not null)
                await _client.ReconfigureOptionsAsync(new SoulseekClientOptionsPatch(
                    maximumUploadSpeed: speed), cancellationToken).ConfigureAwait(false);
            // Also applies when the network client has not been created yet.
            maximumUploadSpeed = speed;
        }
        catch
        {
            // The library may apply the limit before a server notification fails.
            // Force the next reload to reconcile it instead of assuming no change.
            maximumUploadSpeed = null;
            throw;
        }
        finally { _initializationSemaphore.Release(); }
    }

    private ISoulseekClient CreateClientInstance(EngineSettings settings)
    {
        logger.LogDebug("Creating Soulseek client instance");
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
                CreateClientOptions(settings, startingToken, inboundRouter, localProfile, maximumUploadSpeed));
        }
    }

    internal static int CreateRandomStartingToken()
        => RandomNumberGenerator.GetInt32(1, int.MaxValue);

    internal static SoulseekClientOptions CreateClientOptions(
        EngineSettings settings,
        int startingToken,
        ISoulseekInboundRequestRouter? inboundRouter = null,
        LocalUserProfile? localProfile = null,
        int? maximumUploadSpeedOverride = null)
    {
        // Soulseek.NET 10.0.2 races direct and indirect peer connections. If the direct
        // connection wins while the indirect request is being written to the server,
        // cancellation can disconnect the whole session. Sockseek and slskd both
        // observed "Write error: A task was canceled." in Connection.WriteInternalAsync
        // under peer load.
        // TODO: Track the upstream fix for cancellation of indirect connection requests.
        // Verify the server stays connected under search-reply and queued-upload load after upgrading.
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
            description: localProfile?.Description ?? "",
            uploadSlots: 0,
            queueLength: 0,
            hasFreeUploadSlot: false,
            picture: localProfile?.Picture?.Bytes
        ));

        int maximumUploadSpeed = maximumUploadSpeedOverride ?? settings.Uploads.SpeedLimitKiBPerSecond ?? int.MaxValue;
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

        // Per-route peer connection errors are emitted at Debug by Soulseek.NET.
        var diagnosticLevel = settings.LogLevel == LogLevel.Trace ? DiagnosticLevel.Debug : DiagnosticLevel.Info;
        return new SoulseekClientOptions(
            enableListener: settings.ListenPort.HasValue,
            minimumDiagnosticLevel: diagnosticLevel,
            transferConnectionOptions: transferConnectionOptions,
            serverConnectionOptions: serverConnectionOptions,
            listenPort: settings.ListenPort ?? 49998,
            maximumConcurrentSearches: int.MaxValue, // this is limited later in the searcher code
            maximumConcurrentUploads: int.MaxValue, // UploadScheduler owns admission, including live slot changes.
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
            await client.ConnectAsync(user, pass, cancellationToken);
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
            _client.DiagnosticGenerated -= OnProtocolDiagnostic;
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
