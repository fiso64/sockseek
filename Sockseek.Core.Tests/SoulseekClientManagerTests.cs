using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Tests.ClientTests;

namespace Tests.Core;

[TestClass]
public class SoulseekClientManagerTests
{
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
    public async Task WaitUntilReadyAsync_FaultsAfterPermanentLoginFailure()
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
            ConnectException = new InvalidOperationException("listener port unavailable"),
        };
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            await Assert.ThrowsExceptionAsync<SoulseekConnectionUnavailableException>(
                () => manager.EnsureConnectedAndLoggedInAsync(settings));

            var waitTask = manager.WaitUntilReadyAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.AreSame(waitTask, completed, "Permanent login failures must wake readiness waiters instead of leaving the engine stuck forever.");
            await Assert.ThrowsExceptionAsync<SoulseekConnectionUnavailableException>(() => waitTask);
        }
        finally
        {
            manager.Dispose();
        }
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
            await Assert.ThrowsExceptionAsync<SoulseekConnectionUnavailableException>(
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
}
