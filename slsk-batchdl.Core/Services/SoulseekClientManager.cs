using Soulseek;
using System.Net.Sockets;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;

public class SoulseekClientManager : IDisposable
{
    private readonly EngineSettings _initialSettings;
    private ISoulseekClient? _client;
    private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);
    private TaskCompletionSource _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public ISoulseekClient? Client => _client;

    public SoulseekClientStates State => _client?.State ?? SoulseekClientStates.None;

    public bool IsConnectedAndLoggedIn =>
        _client != null &&
        _client.State.HasFlag(SoulseekClientStates.Connected) &&
        _client.State.HasFlag(SoulseekClientStates.LoggedIn);

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnectedAndLoggedIn) return Task.CompletedTask;
        return _readyTcs.Task.WaitAsync(cancellationToken);
    }

    public SoulseekClientManager(EngineSettings initialSettings, ISoulseekClient? client = null)
    {
        _initialSettings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        if (client != null)
        {
            _client = client;
            if (IsConnectedAndLoggedIn)
                _readyTcs.TrySetResult();
            StartMonitoring();
        }
    }

    private void StartMonitoring()
    {
        if (_monitorTask != null) return;
        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorConnectionLoopAsync(_monitorCts.Token));
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
        if (IsConnectedAndLoggedIn) return;

        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (IsConnectedAndLoggedIn) return;
            cancellationToken.ThrowIfCancellationRequested();

            if (_client == null)
            {
                _client = CreateClientInstance(_initialSettings);
            }

            if (!IsConnectedAndLoggedIn)
            {
                if (!loginSettings.UseRandomLogin && (string.IsNullOrEmpty(loginSettings.Username) || string.IsNullOrEmpty(loginSettings.Password)))
                {
                    Logger.Fatal("No soulseek username or password provided for login.");
                }

                await LoginInternalAsync(_client, loginSettings, cancellationToken);
                _readyTcs.TrySetResult();
                StartMonitoring();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error($"Failed to ensure Soulseek connection and login: {ex.Message}");
            throw new InvalidOperationException($"Soulseek login failed: {ex.Message}", ex);
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    private async Task MonitorConnectionLoopAsync(CancellationToken ct)
    {
        int retryDelay = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsConnectedAndLoggedIn)
                {
                    if (_readyTcs.Task.IsCompleted)
                    {
                        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    Logger.Warn($"Connection lost. Retrying in {retryDelay}s...");
                    await Task.Delay(retryDelay * 1000, ct);
                    
                    await EnsureConnectedAndLoggedInAsync(_initialSettings, ct);
                    retryDelay = 1; // Reset on success
                    Logger.Info("Reconnected successfully.");
                }
                else
                {
                    retryDelay = 1;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.DebugError($"Reconnection attempt failed: {ex.Message}");
                retryDelay = Math.Min(retryDelay * 2, 8);
            }

            await Task.Delay(1000, ct);
        }
    }

    private ISoulseekClient CreateClientInstance(EngineSettings settings)
    {
        Logger.Debug("Creating Soulseek client instance...");
        if (!string.IsNullOrEmpty(settings.MockFilesDir))
        {
            Logger.Info("Using local files Soulseek client.");
            return LocalFilesSoulseekClient.FromLocalPaths(settings.MockFilesReadTags, settings.MockFilesSlow, settings.MockFilesDir);
        }
        else
        {
            Logger.Debug("Configuring real Soulseek Client connection options.");
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
                inactivityTimeout: int.MaxValue, // this is handled by --max-stale-time
                configureSocket: (socket) =>
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
                });

            Task<UserInfo> userInfoResolver(string username, System.Net.IPEndPoint ip) => Task.FromResult(new UserInfo(
                description: settings.UserDescription ?? "",
                uploadSlots: 1,
                queueLength: 0,
                hasFreeUploadSlot: true
            ));

            var clientOptionsBuilder = new SoulseekClientOptions(
                transferConnectionOptions: transferConnectionOptions,
                serverConnectionOptions: serverConnectionOptions,
                listenPort: settings.ListenPort ?? 49998,
                maximumConcurrentSearches: int.MaxValue, // this is limited later in the searcher code
                userInfoResolver: userInfoResolver
            );

            if (settings.ListenPort == null)
            {
                // No listen port: create client without listener to avoid bind failures
                clientOptionsBuilder = new SoulseekClientOptions(
                    transferConnectionOptions: transferConnectionOptions,
                    serverConnectionOptions: serverConnectionOptions,
                    enableListener: false,
                    maximumConcurrentSearches: int.MaxValue,
                    userInfoResolver: userInfoResolver
                );
            }

            return new SoulseekClient(clientOptionsBuilder);
        }
    }

    /// <summary>
    /// Internal login logic extracted from DownloaderApplication.
    /// </summary>
    private async Task LoginInternalAsync(ISoulseekClient client, EngineSettings settings, CancellationToken cancellationToken, int tries = 3)
    {
        string user = settings.Username;
        string pass = settings.Password;

        if (settings.UseRandomLogin)
        {
            var r = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            pass = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            Logger.Debug($"Generated random username: {user}");
        }

        string displayUser = settings.UseRandomLogin ? "[Random]" : user;
        Logger.Info($"Login {displayUser}");

        int attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                Logger.Debug($"Connecting {displayUser} (Attempt {attempt}/{tries})");
                // Pass cancellation token to ConnectAsync if the library supports it (check Soulseek library version)
                // Assuming it doesn't directly, we rely on the loop's cancellation check.
                await client.ConnectAsync(user, pass);

                if (!settings.NoModifyShareCount)
                {
                    Logger.Debug($"Setting share count for {displayUser}");
                    await client.SetSharedCountsAsync(settings.SharedFiles, settings.SharedFolders, cancellationToken);
                }
                Logger.Debug($"Logged in {displayUser}");
                break;
            }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check again after potential long operation
                Logger.DebugError($"Exception during login attempt {attempt}/{tries} for {displayUser}: {e}");
                if (!(e is Soulseek.AddressException || e is System.TimeoutException || e is System.Net.Sockets.SocketException) || attempt >= tries)
                {
                    Logger.Error($"Login failed definitively for {displayUser} after {attempt} attempts.");
                    throw; // Retries exhausted or non-transient error
                }

                Logger.Warn($"Login attempt {attempt}/{tries} failed for {displayUser}, retrying...");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _client?.Dispose();
    }
}
