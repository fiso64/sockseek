using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Diagnostics;
using Soulseek;
using Soulseek.Diagnostics;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Tests.ClientTests;
using System.Net;
using Sockseek.Core.UserProfiles;

namespace Tests.Core;

[TestClass]
public class SoulseekClientManagerTests
{
    [TestMethod]
    public void ProtocolWarningsAreBoundedRateLimitedAndDetachWithTheSession()
    {
        var records = new List<CompactLogRecord>();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new CompactTextLoggerProvider(records.Add, LogLevel.Debug)));
        var client = new MockSoulseekClient([]);
        using var manager = new SoulseekClientManager(new EngineSettings(), client,
            logger: factory.CreateLogger<SoulseekClientManager>());
        client.RaiseDiagnostic(new DiagnosticEventArgs(DiagnosticLevel.Warning, new string('x', 10_000), new IOException("bad peer message")));
        client.RaiseDiagnostic(new DiagnosticEventArgs(DiagnosticLevel.Warning, "another warning"));
        Assert.AreEqual(1, records.Count(row => row.Level == LogLevel.Warning));
        Assert.IsTrue(records.All(row => row.Message.Length < 2_200));
        Assert.IsTrue(records.Any(row => row.Level == LogLevel.Debug && row.Message.Contains("another warning")));
        manager.Dispose();
        int afterDispose = records.Count;
        client.RaiseDiagnostic(new DiagnosticEventArgs(DiagnosticLevel.Warning, "after dispose"));
        Assert.AreEqual(afterDispose, records.Count);
    }

    [TestMethod]
    public async Task CancellationDuringLoginReachesTransportAndAllowsRetry()
    {
        var settings = new EngineSettings { Username = "user", Password = "pass" };
        var client = new MockSoulseekClient([], initialState: SoulseekClientStates.None);
        using var cancellation = new CancellationTokenSource();
        client.Connecting = () => cancellation.Cancel();
        await using var manager = new SoulseekClientManager(settings, client, null,
            (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => manager.EnsureConnectedAndLoggedInAsync(settings, cancellation.Token));
        Assert.IsFalse(manager.HasFatalError);
        Assert.IsFalse(manager.IsConnectedAndLoggedIn);
        Assert.IsNull(manager.LoggedInUsername);
        client.Connecting = null;
        await manager.EnsureConnectedAndLoggedInAsync(settings);
        await manager.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(manager.IsConnectedAndLoggedIn);
    }

    [TestMethod]
    public void CreateRandomStartingToken_ReturnsPositiveToken()
    {
        for (int i = 0; i < 20; i++)
        {
            int token = SoulseekClientManager.CreateRandomStartingToken();

            Assert.IsTrue(token > 0);
            Assert.IsTrue(token < int.MaxValue);
        }
    }

    [TestMethod]
    public void CreateClientOptions_UsesProvidedStartingToken_WithListener()
    {
        const int token = 123_456_789;
        var options = SoulseekClientManager.CreateClientOptions(
            new EngineSettings { ListenPort = 49998 },
            token);

        Assert.AreEqual(token, options.StartingToken);
    }

    [TestMethod]
    public void CreateClientOptions_UsesProvidedStartingToken_WithoutListener()
    {
        const int token = 987_654_321;
        var options = SoulseekClientManager.CreateClientOptions(
            new EngineSettings { ListenPort = null },
            token);

        Assert.AreEqual(token, options.StartingToken);
    }

    [TestMethod]
    public void CreateClientOptions_WiresSharingCallbacksSlotsAndSpeed()
    {
        var settings = new EngineSettings
        {
            ListenPort = 49998,
            Uploads = new UploadSettings
            {
                Slots = 7,
                SpeedLimitKiBPerSecond = 123,
            },
        };
        var router = new FakeInboundRouter();

        SoulseekClientOptions options =
            SoulseekClientManager.CreateClientOptions(settings, 1, router);

        Assert.AreEqual(int.MaxValue, options.MaximumConcurrentUploads);
        Assert.AreEqual(123, options.MaximumUploadSpeed);
        Assert.AreEqual(
            SoulseekClientManager.PeerConnectionInactivityTimeoutMilliseconds,
            options.TransferConnectionOptions.InactivityTimeout);
        Assert.IsNotNull(options.SearchResponseResolver);
        Assert.IsNotNull(options.BrowseResponseResolver);
        Assert.IsNotNull(options.DirectoryContentsResolver);
        Assert.IsNotNull(options.EnqueueDownload);
        Assert.IsNotNull(options.PlaceInQueueResolver);
        Assert.IsFalse(options.AutoAcknowledgePrivateMessages);
        Assert.IsTrue(options.AcceptPrivateRoomInvitations);
    }

    [TestMethod]
    public async Task FallbackUserInfoUsesTheStartupLoadedLocalProfile()
    {
        byte[] bytes = [0xff, 0xd8, 0xff, 0xd9];
        var profile = new LocalUserProfile(
            "normalized",
            new UserPicture(bytes, "image/jpeg", 1, 1, "\"etag\""));
        SoulseekClientOptions options = SoulseekClientManager.CreateClientOptions(
            new EngineSettings { UserDescription = "ignored" },
            1,
            localProfile: profile);

        UserInfo info = await options.UserInfoResolver(
            "peer",
            new IPEndPoint(IPAddress.Loopback, 1234));

        Assert.AreEqual("normalized", info.Description);
        CollectionAssert.AreEqual(bytes, info.Picture);
        Assert.AreEqual(0, info.UploadSlots, "A client without the sharing service cannot offer uploads.");
        Assert.IsFalse(info.HasFreeUploadSlot);
    }

    [TestMethod]
    public void ClientManager_ForwardsServerExcludedPhraseUpdates()
    {
        var client = new MockSoulseekClient([]);
        var router = new FakeInboundRouter();
        using var manager = new SoulseekClientManager(
            new EngineSettings(),
            client,
            router);

        client.RaiseExcludedSearchPhrases("forbidden", "blocked");

        CollectionAssert.AreEqual(
            new[] { "forbidden", "blocked" },
            router.ExcludedPhrases.ToArray());
    }

    [TestMethod]
    public void ClientManager_IsolatesStateObservers()
    {
        var client = new MockSoulseekClient([]);
        using var manager = new SoulseekClientManager(
            new EngineSettings(),
            client);
        SoulseekClientStates observed = SoulseekClientStates.None;
        manager.StateChanged += _ => throw new InvalidOperationException("observer");
        manager.StateChanged += state => observed = state;

        client.RaiseStateChanged(
            SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);

        Assert.AreEqual(
            SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn,
            observed);
    }

    [TestMethod]
    public void Dispose_DisposesUnderlyingClient()
    {
        var mockClient = new MockSoulseekClient(new());
        var manager = new SoulseekClientManager(new EngineSettings(), mockClient);

        // Before the fix, Dispose did not exist/do anything. 
        // Now it should tear down the monitor loop and invoke Dispose on the client.
        manager.Dispose();

        Assert.IsTrue(mockClient.IsDisposed, "Underlying ISoulseekClient should be disposed.");
    }

    [TestMethod]
    public async Task DisposeAsync_WakesPendingReadinessWaiters()
    {
        var mockClient = new MockSoulseekClient([], initialState: SoulseekClientStates.None);
        var manager = new SoulseekClientManager(
            new EngineSettings(),
            mockClient,
            inboundRouter: null,
            monitorDelay: static (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        Task waiter = manager.WaitUntilReadyAsync();

        await manager.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => waiter.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task MonitorContinuesAfterCancellationExceptionNotRequestedByItsToken()
    {
        var mockClient = new MockSoulseekClient([], initialState: SoulseekClientStates.None);
        var secondDelayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delayCalls = 0;
        var manager = new SoulseekClientManager(
            new EngineSettings(),
            mockClient,
            inboundRouter: null,
            monitorDelay: (_, cancellationToken) => Interlocked.Increment(ref delayCalls) == 1
                ? Task.FromException(new OperationCanceledException("transport timeout"))
                : WaitForDisposal(cancellationToken));

        async Task WaitForDisposal(CancellationToken cancellationToken)
        {
            secondDelayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        await secondDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(manager.HasFatalError);
        Assert.IsTrue(Volatile.Read(ref delayCalls) >= 2);

        await manager.DisposeAsync();
    }

    [TestMethod]
    [DataRow("listener")]
    [DataRow("credentials")]
    [DataRow("wrapped credentials")]
    public async Task WaitUntilReadyAsync_FaultsAfterPermanentLoginFailure(string failureKind)
    {
        var settings = new EngineSettings
        {
            Username = "user",
            Password = "pass",
        };
        var mockClient = new MockSoulseekClient(
            new(),
            initialState: SoulseekClientStates.None)
        {
            ConnectException = failureKind switch
            {
                "credentials" => new LoginRejectedException("Invalid password"),
                "wrapped credentials" => new SoulseekClientException("Login failed", new LoginRejectedException("Invalid password")),
                _ => new InvalidOperationException("listener port unavailable"),
            },
        };
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            var pendingWait = manager.WaitUntilReadyAsync();
            var failure = await Assert.ThrowsExactlyAsync<SoulseekConnectionUnavailableException>(
                () => manager.EnsureConnectedAndLoggedInAsync(settings));

            var pendingFailure = await Assert.ThrowsExactlyAsync<SoulseekConnectionUnavailableException>(() =>
                pendingWait.WaitAsync(TimeSpan.FromSeconds(5)));
            var subsequentFailure = await Assert.ThrowsExactlyAsync<SoulseekConnectionUnavailableException>(
                () => manager.WaitUntilReadyAsync());

            Assert.AreSame(failure, pendingFailure);
            Assert.AreSame(failure, subsequentFailure);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [TestMethod]
    public async Task CreatedAlreadyConnectedClient_CompletesReadiness()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "sockseek-ready-local-client", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settings = new EngineSettings { MockFilesDir = directory };
        await using var manager = new SoulseekClientManager(settings);
        try
        {
            await manager.EnsureConnectedAndLoggedInAsync(settings);
            await manager.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsTrue(manager.IsConnectedAndLoggedIn);
            Assert.AreEqual("local", manager.LoggedInUsername);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task FatalLoginWithoutReadinessWaiter_DoesNotPublishUnobservedTaskException()
    {
        const string marker = "fatal-login-unobserved-regression";
        var unobserved = new List<AggregateException>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception.ToString().Contains(marker, StringComparison.Ordinal))
            {
                lock (unobserved)
                    unobserved.Add(args.Exception);
                args.SetObserved();
            }
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await CreateAndDisposeFatalManager(marker);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Yield();
            }

            lock (unobserved)
                Assert.AreEqual(0, unobserved.Count, "A handled fatal login must not leave a second faulted readiness task unobserved.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task CreateAndDisposeFatalManager(string marker)
    {
        var settings = new EngineSettings
        {
            Username = "user",
            Password = "pass",
        };
        var mockClient = new MockSoulseekClient(
            new(),
            initialState: SoulseekClientStates.None)
        {
            ConnectException = new InvalidOperationException(marker),
        };
        using var manager = new SoulseekClientManager(settings, mockClient);

        await Assert.ThrowsExactlyAsync<SoulseekConnectionUnavailableException>(
            () => manager.EnsureConnectedAndLoggedInAsync(settings));
    }

    [TestMethod]
    [DataRow(null, null, "Missing Soulseek username and password.")]
    [DataRow("bbbbbbb", null, "Missing Soulseek password.")]
    public async Task EnsureConnectedAndLoggedInAsync_FailsCleanly_WhenCredentialsAreMissing(
        string? username,
        string? password,
        string expectedMessage)
    {
        var settings = new EngineSettings
        {
            Username = username,
            Password = password,
        };
        var mockClient = new MockSoulseekClient(
            new(),
            initialState: SoulseekClientStates.None);
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            var ex = await Assert.ThrowsExactlyAsync<SoulseekConnectionUnavailableException>(
                () => manager.EnsureConnectedAndLoggedInAsync(settings));

            StringAssert.Contains(ex.Message, expectedMessage);
            Assert.IsFalse(ex.Message.Contains("--random-login", StringComparison.Ordinal), "Missing credential errors should not advertise --random-login.");
            Assert.AreEqual(0, mockClient.ConnectCallCount, "Missing credentials should fail before ConnectAsync.");
        }
        finally
        {
            manager.Dispose();
        }
    }

    [TestMethod]
    public async Task LoginAccountIsVisibleBeforeProtocolConnectCallbacks()
    {
        var settings = new EngineSettings
        {
            Username = "chat-account",
            Password = "pass",
        };
        var mockClient = new MockSoulseekClient(
            [],
            initialState: SoulseekClientStates.None);
        using var manager = new SoulseekClientManager(settings, mockClient);
        string? accountObservedDuringConnect = null;
        mockClient.Connecting = () =>
            accountObservedDuringConnect = manager.LoggedInUsername;

        await manager.EnsureConnectedAndLoggedInAsync(settings);

        Assert.AreEqual("chat-account", accountObservedDuringConnect);
        Assert.AreEqual("chat-account", manager.LoggedInUsername);
    }

    [TestMethod]
    public async Task KickedFromServer_MarksFatal_WhenAutoReconnectDisabled()
    {
        var settings = new EngineSettings();
        var mockClient = new MockSoulseekClient(new());
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            mockClient.RaiseKickedFromServer(disconnect: false);

            Assert.IsTrue(manager.HasFatalError);
            await Assert.ThrowsExactlyAsync<SoulseekConnectionUnavailableException>(
                () => manager.WaitUntilReadyAsync());
        }
        finally
        {
            manager.Dispose();
        }
    }

    [TestMethod]
    public void KickedFromServer_DoesNotMarkFatal_WhenAutoReconnectEnabled()
    {
        var settings = new EngineSettings
        {
            AutoReconnectAfterKickedFromServer = true,
        };
        var mockClient = new MockSoulseekClient(new());
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            mockClient.RaiseKickedFromServer();

            Assert.IsFalse(manager.HasFatalError);
        }
        finally
        {
            manager.Dispose();
        }
    }

    private sealed class FakeInboundRouter : ISoulseekInboundRequestRouter
    {
        public IReadOnlyCollection<string> ExcludedPhrases { get; private set; } = [];

        public bool TryUpdateExcludedSearchPhrases(IReadOnlyCollection<string> phrases)
        {
            ExcludedPhrases = phrases.ToArray();
            return true;
        }

        public Task<SearchResponse?> ResolveSearchAsync(
            string username,
            int token,
            SearchQuery query) => Task.FromResult<SearchResponse?>(null);

        public Task<BrowseResponse> ResolveBrowseAsync(
            string username,
            IPEndPoint endpoint) => Task.FromResult(new BrowseResponse());

        public Task<IEnumerable<Soulseek.Directory>> ResolveDirectoryAsync(
            string username,
            IPEndPoint endpoint,
            int token,
            string remotePath)
            => Task.FromResult<IEnumerable<Soulseek.Directory>>([]);

        public Task<UserInfo> ResolveUserInfoAsync(
            string username,
            IPEndPoint endpoint)
            => Task.FromResult(new UserInfo("", 0, 0, false));

        public Task EnqueueUploadAsync(
            string username,
            IPEndPoint endpoint,
            string remotePath) => Task.CompletedTask;

        public Task<int?> ResolvePlaceInQueueAsync(
            string username,
            IPEndPoint endpoint,
            string remotePath) => Task.FromResult<int?>(null);
    }
}
