using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server;
using Soulseek;

namespace Tests.Server;

[TestClass]
public sealed class UserProfileApiTests
{
    [TestMethod]
    public async Task HostStart_PublishesLocalServicesWithoutWaitingForLogin()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "sockseek-profile-startup", Guid.NewGuid().ToString("N"));
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = SoulseekClientProxy.Create();
        fake.ClientState = SoulseekClientStates.Disconnected;
        fake.Connect = () =>
        {
            connecting.TrySetResult();
            return connected.Task;
        };
        string url = $"http://127.0.0.1:{GetFreeTcpPort()}";
        await using var app = global::Tests.TestServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                Username = "local", Password = "password", ListenPort = null,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            Persistence = new ServerPersistenceOptions { Enabled = true, DataDirectory = dataDirectory },
            ClientFactory = _ => fake.Client,
        }, url);

        try
        {
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(connected.Task.IsCompleted);
            var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            Assert.IsNotNull(supervisor.Runtime.PeerBrowses);
            Assert.IsNotNull(supervisor.Runtime.UserProfiles);
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var api = new SockseekApiClient(http);
            await api.GetServerInfoAsync();
            UserBrowseDto browse = await api.StartUserBrowseAsync("Peer");
            Assert.AreEqual("Peer", browse.Username);
        }
        finally
        {
            fake.RaiseState(SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);
            connected.TrySetResult();
            await app.StopAsync();
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(dataDirectory))
                System.IO.Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TypedApiReturnsCompositeAndStreamsPictureWithConditionalGet()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "sockseek-profile-api", Guid.NewGuid().ToString("N"));
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var fake = SoulseekClientProxy.Create();
        fake.ProfileInfo = new UserInfo("hello\r\npeer", 2, 3, true, png);
        fake.ProfileStatus = new UserStatus("Peer", UserPresence.Away, false);
        fake.ProfileStatistics = new UserStatistics("Peer", 400, 12, 30, 4);
        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = global::Tests.TestServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                Username = "local",
                Password = "password",
                ListenPort = null,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = false,
                DataDirectory = dataDirectory,
            },
            ClientFactory = _ => fake.Client,
        }, url);

        try
        {
            await app.StartAsync();
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var api = new SockseekApiClient(http);

            UserPresenceDto presence = await api.GetUserPresenceAsync("Peer");
            Assert.AreEqual("Peer", presence.Username);
            Assert.AreEqual(UserPresenceState.Away, presence.State);

            await using var live = new SockseekLiveClient(http);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var observed = live.ObserveUserPresenceAsync("Peer", deadline.Token).GetAsyncEnumerator();
            while (await observed.MoveNextAsync() && observed.Current.State == UserPresenceState.Checking) { }
            Assert.AreEqual(UserPresenceState.Away, observed.Current.State);
            int watches = fake.WatchCount;
            fake.RaiseUserStatus("Peer", UserPresence.Online);
            Assert.IsTrue(await observed.MoveNextAsync());
            Assert.AreEqual(UserPresenceState.Online, observed.Current.State);
            Assert.AreEqual(observed.Current, await api.GetUserPresenceAsync("Peer"));
            Assert.AreEqual(watches, fake.WatchCount);

            UserProfileDto profile = await api.GetUserProfileAsync("Peer");
            Assert.AreEqual("hello\npeer", profile.Description);
            Assert.AreEqual(30L, profile.SharedFileCount);
            Assert.AreEqual(4L, profile.SharedDirectoryCount);
            Assert.AreEqual(12, profile.UploadCount);
            Assert.AreEqual(ResourceSectionState.Available, profile.PictureSection.State);
            Assert.AreEqual("image/png", profile.Picture?.MediaType);

            using UserPictureResponse picture = await api.GetUserPictureAsync("Peer");
            Assert.IsFalse(picture.NotModified);
            Assert.AreEqual("image/png", picture.MediaType);
            Assert.AreEqual(png.Length, picture.ContentLength);
            Assert.IsNotNull(picture.ETag);
            Assert.AreEqual("nosniff", picture.HttpResponse.Headers.GetValues("X-Content-Type-Options").Single());
            await using Stream body = await picture.OpenReadAsync();
            using var copy = new MemoryStream();
            await body.CopyToAsync(copy);
            CollectionAssert.AreEqual(png, copy.ToArray());

            using UserPictureResponse unchanged = await api.GetUserPictureAsync(
                "Peer", picture.ETag);
            Assert.IsTrue(unchanged.NotModified);

            fake.ProfileInfo = new UserInfo("refreshed", 2, 3, true, png);
            Assert.AreEqual("hello\npeer", (await api.GetUserProfileAsync("Peer")).Description);
            Assert.AreEqual("refreshed", (await api.GetUserProfileAsync("Peer", refresh: true)).Description);
        }
        finally
        {
            await app.StopAsync();
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(dataDirectory))
                System.IO.Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
