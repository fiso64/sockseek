using Soulseek;
using System.Net.Sockets;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed class SoulseekConnectionUnavailableException : InvalidOperationException
{
    public SoulseekConnectionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class SoulseekClientManager : IDisposable
{
    private readonly EngineSettings _initialSettings;
    private ISoulseekClient? _client;
    private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);
    private TaskCompletionSource _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object stateLock = new();
    private Exception? _fatalException;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public ISoulseekClient? Client => _client;

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
        if (IsConnectedAndLoggedIn) return Task.CompletedTask;

        lock (stateLock)
        {
            if (_fatalException != null)
                return Task.FromException(_fatalException);
        }

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
        if (HasFatalError) return;
        if (_monitorTask != null) return;
        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorConnectionLoopAsync(_monitorCts.Token));
    }

    private void MarkFatal(Exception exception)
    {
        lock (stateLock)
        {
            _fatalException ??= exception;
            _readyTcs.TrySetException(_fatalException);
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
                    SockseekLog.Soulseek.Error("No soulseek username or password provided for login.");
                }

                await LoginInternalAsync(_client, loginSettings, cancellationToken);
                _readyTcs.TrySetResult();
                StartMonitoring();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new SoulseekConnectionUnavailableException(
                $"Soulseek login failed: {SockseekLog.ExceptionSummary(ex)}",
                ex);

            if (IsTransient(ex))
            {
                SockseekLog.Soulseek.Error(ex, "Failed to ensure Soulseek connection and login");
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

    private static bool IsTransient(Exception? e)
    {
        while (e != null)
        {
            if (e is Soulseek.AddressException || e is System.TimeoutException || e is System.Net.Sockets.SocketException) return true;
            if (e.GetType().Name.Contains("ConnectionException")) return true;
            if (e.GetType().Name.Contains("SoulseekClientException")) return true;
            e = e.InnerException;
        }
        return false;
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
                    if (HasFatalError)
                        break;

                    if (_readyTcs.Task.IsCompleted)
                    {
                        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    SockseekLog.Soulseek.Warn($"Connection lost. Retrying in {retryDelay}s...");
                    await Task.Delay(retryDelay * 1000, ct);
                    
                    await EnsureConnectedAndLoggedInAsync(_initialSettings, ct);
                    retryDelay = 1; // Reset on success
                    SockseekLog.Soulseek.Info("Reconnected successfully.");
                }
                else
                {
                    retryDelay = 1;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!IsTransient(ex))
                {
                    MarkFatal(ex);
                    SockseekLog.Soulseek.Fatal(ex, "Permanent Soulseek error. Stopping reconnection attempts");
                    break;
                }

                SockseekLog.Soulseek.Debug(SockseekLog.FormatException("Reconnection attempt failed", ex));
                retryDelay = Math.Min(retryDelay * 2, 8);
            }

            await Task.Delay(1000, ct);
        }
    }

    private ISoulseekClient CreateClientInstance(EngineSettings settings)
    {
        SockseekLog.Soulseek.Debug("Creating Soulseek client instance...");
        if (!string.IsNullOrEmpty(settings.MockFilesDir))
        {
            SockseekLog.Soulseek.Info("Using local files Soulseek client.");
            return LocalFilesSoulseekClient.FromLocalPaths(
                settings.MockFilesReadTags,
                settings.MockFilesSlow,
                settings.MockFilesFailDownloads,
                settings.MockFilesDir);
        }
        else
        {
            SockseekLog.Soulseek.Debug("Configuring Soulseek Client connection options.");
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
    private async Task LoginInternalAsync(ISoulseekClient client, EngineSettings settings, CancellationToken cancellationToken)
    {
        string user = settings.Username;
        string pass = settings.Password;

        if (settings.UseRandomLogin)
        {
            var r = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            pass = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            SockseekLog.Soulseek.Debug($"Generated random username: {user}");
        }

        string displayUser = settings.UseRandomLogin ? "[Random]" : user;
        SockseekLog.Soulseek.Info($"Logging in as {displayUser}..");

        cancellationToken.ThrowIfCancellationRequested();
        await client.ConnectAsync(user, pass);

        if (!settings.NoModifyShareCount)
        {
            SockseekLog.Soulseek.Debug($"Setting share count for {displayUser}");
            await client.SetSharedCountsAsync(settings.SharedFiles, settings.SharedFolders, cancellationToken);
        }
        SockseekLog.Soulseek.Debug($"Logged in as {displayUser}");
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _client?.Dispose();
    }
}
