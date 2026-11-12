using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server.Authentication;
using Sockseek.Server.Persistence;
using Sockseek.Persistence.Authentication;
using Tests;
using Tests.Server;
using AuthenticationOptions = Sockseek.Server.Authentication.AuthenticationOptions;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class AuthenticationTests
{
    private const string Password = "test-only-password";
    private static readonly string PasswordHash = AuthenticationOptions.HashPassword(Password);

    [TestMethod]
    public async Task BrowserAndApiKeys_ProtectHttpAndLiveConnections_AndRevokeActualCredentials()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-auth-").FullName;
        await using var app = CreateHost(directory);
        try
        {
            await app.StartAsync();
            string url = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var browser = new HttpClient(handler) { BaseAddress = new Uri(url) };
            foreach (string route in new[] { "/api/server/info", "/api/daemon/snapshot", "/api/chat", "/api/openapi.json" })
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync(route)).StatusCode, route);
            Assert.AreEqual(HttpStatusCode.Unauthorized,
                (await browser.PostAsync("/api/persistence/checkpoint", null)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized,
                (await browser.PostAsync("/api/events/negotiate?negotiateVersion=1", null)).StatusCode);

            var authorization = app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
            Assert.IsNotNull(authorization.FallbackPolicy);
            var anonymousRoutes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints)
                .OfType<RouteEndpoint>().Where(x => x.Metadata.GetMetadata<IAllowAnonymous>() is not null
                    && x.RoutePattern.RawText!.StartsWith("/api/"))
                .Select(x => x.RoutePattern.RawText).ToArray();
            CollectionAssert.AreEquivalent(new[] { "/api/auth/login", "/api/auth/session" }, anonymousRoutes);

            var session = await browser.GetFromJsonAsync<SessionDto>("/api/auth/session");
            Assert.IsFalse(session!.Authenticated);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/auth/login",
                new LoginRequestDto("admin", Password))).StatusCode, "Login requires antiforgery too.");
            browser.DefaultRequestHeaders.Add("X-CSRF-Token", session.AntiforgeryToken);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/api/auth/login",
                new LoginRequestDto("admin", "wrong"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await browser.PostAsJsonAsync("/api/auth/login",
                new LoginRequestDto("admin", Password))).StatusCode);
            session = await browser.GetFromJsonAsync<SessionDto>("/api/auth/session");
            Assert.IsTrue(session!.Authenticated);
            Assert.IsNotNull(session.ExpiresAt);
            browser.DefaultRequestHeaders.Remove("X-CSRF-Token");
            browser.DefaultRequestHeaders.Add("X-CSRF-Token", session.AntiforgeryToken);
            Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/server/info")).StatusCode);
            var originalExpiry = session.ExpiresAt;
            await browser.PostAsJsonAsync("/api/auth/login", new LoginRequestDto("admin", Password));
            Assert.AreEqual(originalExpiry, (await browser.GetFromJsonAsync<SessionDto>("/api/auth/session"))!.ExpiresAt,
                "Logging in again must not extend an active fixed session.");

            browser.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            Assert.AreEqual(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/server/info")).StatusCode);
            browser.DefaultRequestHeaders.Remove("Origin");
            browser.DefaultRequestHeaders.Add("X-API-Key", "invalid");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/server/info")).StatusCode,
                "An explicit invalid key must not fall back to a valid cookie.");
            browser.DefaultRequestHeaders.Remove("X-API-Key");

            var response = await browser.PostAsJsonAsync("/api/auth/keys", new CreateApiKeyRequestDto("Laptop"));
            response.EnsureSuccessStatusCode();
            Assert.IsTrue(response.Headers.CacheControl!.NoStore);
            var created = (await response.Content.ReadFromJsonAsync<CreatedApiKeyDto>())!;
            Assert.AreEqual(70, created.Secret.Length);
            using var remote = SockseekApiClient.CreateHttpClient(url, created.Secret);
            Assert.AreEqual(HttpStatusCode.OK, (await remote.GetAsync("/api/server/info")).StatusCode);
            foreach (string malformed in new[] { created.Secret[..^1], "ssk_!" + created.Secret[5..] })
            {
                remote.DefaultRequestHeaders.Remove("X-API-Key");
                remote.DefaultRequestHeaders.Add("X-API-Key", malformed);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await remote.GetAsync("/api/server/info")).StatusCode);
            }
            remote.DefaultRequestHeaders.Remove("X-API-Key");
            remote.DefaultRequestHeaders.Add("X-API-Key", created.Secret);
            string listing = await browser.GetStringAsync("/api/auth/keys");
            Assert.DoesNotContain(created.Secret, listing);
            await using var live = new SockseekLiveClient(remote);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            live.ConnectionFailed += _ => disconnected.TrySetResult();
            await live.StartDaemonAsync();
            Assert.AreEqual(HttpStatusCode.NoContent,
                (await browser.DeleteAsync($"/api/auth/keys/{created.Key.Id}")).StatusCode);
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await remote.GetAsync("/api/server/info")).StatusCode);

            var retained = await new SockseekApiClient(browser).CreateApiKeyAsync("Restart survivor");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ApiKeyStore.OfflineAsync(
                SockseekDataPaths.ResolveDatabasePath(directory), (store, ct) => store.ReadAllAsync(ct)));
            await using var browserLive = new HubConnectionBuilder().WithUrl(url + $"/api/events?liveProtocol={LiveProtocol.Version}",
                o => o.Cookies = handler.CookieContainer).Build();
            var signedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browserLive.Closed += _ => { signedOut.TrySetResult(); return Task.CompletedTask; };
            await browserLive.StartAsync();
            string copiedCookie = handler.CookieContainer.GetCookieHeader(new Uri(url));
            Assert.AreEqual(HttpStatusCode.NoContent, (await browser.PostAsync("/api/auth/logout", null)).StatusCode);
            await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var stolen = new HttpClient { BaseAddress = new Uri(url) };
            stolen.DefaultRequestHeaders.Add("Cookie", copiedCookie);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/api/server/info")).StatusCode,
                "Logout invalidates copied session cookies server-side.");
            session = (await browser.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
            browser.DefaultRequestHeaders.Remove("X-CSRF-Token");
            browser.DefaultRequestHeaders.Add("X-CSRF-Token", session.AntiforgeryToken);
            (await browser.PostAsJsonAsync("/api/auth/login", new LoginRequestDto("admin", Password))).EnsureSuccessStatusCode();
            string activeBeforeRestart = handler.CookieContainer.GetCookieHeader(new Uri(url));
            await app.StopAsync();
            await using var restarted = CreateHost(directory);
            await restarted.StartAsync();
            try
            {
                using var survivingClient = SockseekApiClient.CreateHttpClient(restarted.Urls.Single(), retained.Secret);
                using var revokedClient = SockseekApiClient.CreateHttpClient(restarted.Urls.Single(), created.Secret);
                Assert.AreEqual(HttpStatusCode.OK, (await survivingClient.GetAsync("/api/server/info")).StatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/server/info")).StatusCode);
                using var oldSession = new HttpClient { BaseAddress = new Uri(restarted.Urls.Single()) };
                oldSession.DefaultRequestHeaders.Add("Cookie", activeBeforeRestart);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/server/info")).StatusCode);
                var keys = await restarted.Services.GetRequiredService<PersistenceCoordinator>().ApiKeys!.ReadAllAsync();
                Assert.HasCount(1, keys);
                Assert.HasCount(32, keys[0].Hash);
                Assert.IsTrue(ApiKeyStore.Verify(retained.Secret, keys[0]));
            }
            finally { await restarted.StopAsync(); }
        }
        finally { await app.StopAsync(); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task NoAuth_IsExplicit_AndStillRejectsCrossOriginRequests()
    {
        await using var app = CreateHost(null, enabled: false);
        await app.StartAsync();
        try
        {
            string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/api/server/info")).StatusCode);
            var session = (await http.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
            Assert.IsFalse(session.AuthenticationEnabled);
            Assert.IsTrue(session.Authenticated);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/api/auth/keys")).StatusCode);
            http.DefaultRequestHeaders.Add("Origin", new Uri(url).GetLeftPart(UriPartial.Authority));
            Assert.AreEqual(HttpStatusCode.BadRequest, (await http.PostAsync("/api/auth/logout", null)).StatusCode);
            http.DefaultRequestHeaders.Add("X-CSRF-Token", session.AntiforgeryToken);
            Assert.AreEqual(HttpStatusCode.NoContent, (await http.PostAsync("/api/auth/logout", null)).StatusCode);
            http.DefaultRequestHeaders.Remove("Origin");
            http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            Assert.AreEqual(HttpStatusCode.Forbidden, (await http.GetAsync("/api/server/info")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    [DataRow("loopback")]
    [DataRow("other")]
    [DataRow("none")]
    public async Task HttpsAndOrigins_AcceptForwardingOnlyFromConfiguredProxies(string proxy)
    {
        bool trusted = proxy == "loopback";
        await using var app = CreateHost(null, authentication: new()
        {
            PasswordHash = PasswordHash,
            TrustedProxies = trusted ? ["127.0.0.1", "::1"] : proxy == "other" ? ["192.0.2.1"] : [],
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            Assert.AreEqual(HttpStatusCode.BadRequest, (await http.GetAsync("/api/auth/session")).StatusCode);
            http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            http.DefaultRequestHeaders.Host = "sockseek.example:443";
            http.DefaultRequestHeaders.Add("Origin", "https://sockseek.example");
            using var response = await http.GetAsync("/api/auth/session");
            Assert.AreEqual(trusted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
            if (trusted)
            {
                Assert.IsTrue(response.Headers.GetValues("Set-Cookie").All(value => value.Contains("secure", StringComparison.OrdinalIgnoreCase)));
                http.DefaultRequestHeaders.Remove("Origin");
                http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
                Assert.AreEqual(HttpStatusCode.Forbidden, (await http.GetAsync("/api/auth/session")).StatusCode);
            }
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task LoginAttempts_AreRateLimitedWithoutWaitingOrDisclosingCredentials()
    {
        await using var app = CreateHost(null);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var session = (await http.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
            http.DefaultRequestHeaders.Add("X-CSRF-Token", session.AntiforgeryToken);
            // Invalid lengths keep this test about admission, without eleven expensive password hashes.
            var request = new LoginRequestDto(new string('x', 201), "do-not-echo-this-password");
            for (int attempt = 0; attempt < 10; attempt++)
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
            using var limited = await http.PostAsJsonAsync("/api/auth/login", request);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.DoesNotContain(request.Password, await limited.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task RemoteClient_RejectsCrossOriginHttpAndSignalRNegotiationRedirects()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/api/server/info", () => new ServerInfoDto("Fixture", "test", DateTimeOffset.UtcNow, LiveProtocol.Version));
        bool receivedKey = false;
        app.MapPost("/api/events/negotiate", (HttpContext context) =>
        {
            receivedKey = context.Request.Headers["X-API-Key"] == "fixture-key";
            return Results.Json(new { url = "http://another-origin.invalid/api/events" });
        });
        await app.StartAsync();
        try
        {
            using var http = SockseekApiClient.CreateHttpClient(app.Urls.Single(), "fixture-key");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => http.GetAsync("http://another-origin.invalid/api/server/info"));
            await using var live = new SockseekLiveClient(http);
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => live.StartDaemonAsync());
            StringAssert.Contains(error.Message, "another origin");
            Assert.IsTrue(receivedKey);
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task MissingCredentialsFailStartup_AndMalformedHashesDoNotLeakIntoErrors()
    {
        var peer = SoulseekClientProxy.Create();
        await using var app = Sockseek.Server.ServerHost.Build([], new ServerOptions
        {
            ClientFactory = _ => peer.Client,
            Engine = new EngineSettings { LogLevel = LogLevel.None },
        }, "http://127.0.0.1:0", logToConsole: false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => app.StartAsync());
        const string secret = "not-a-valid-hash-do-not-echo";
        var error = Assert.ThrowsExactly<ArgumentException>(() => new AuthenticationOptions { PasswordHash = secret }.PreparePassword());
        Assert.DoesNotContain(secret, error.Message);
        Assert.ThrowsExactly<ArgumentException>(() => new AuthenticationOptions
        {
            PasswordFile = "unread-file", PasswordHash = secret,
        }.PreparePassword());
    }

    [TestMethod]
    public async Task SessionOwner_ExpiresIdleTicketsAndTheirStreamsWithoutAnHttpRequest()
    {
        await using var app = CreateHost(null);
        var clock = new SessionClock();
        using var owner = new OperatorAuthentication(Options.Create(new ServerOptions
        {
            Authentication = new() { PasswordHash = PasswordHash },
        }), app.Services.GetRequiredService<PersistenceCoordinator>(), clock, NullLogger<OperatorAuthentication>.Instance);
        await owner.StartAsync(CancellationToken.None);
        try
        {
            var ticket = new AuthenticationTicket(OperatorAuthentication.Principal("admin", "Browser"),
                new AuthenticationProperties { ExpiresUtc = clock.GetUtcNow().AddDays(7) }, "Browser");
            string key = await owner.StoreAsync(ticket);
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var observation = owner.ObserveRevocation(ticket.Principal, () => expired.TrySetResult());
            Assert.IsNotNull(await owner.RetrieveAsync(key));
            await clock.TimerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromDays(7));
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(await owner.RetrieveAsync(key));
            bool rejectedConnection = false;
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => owner.ObserveRevocation(ticket.Principal, () => rejectedConnection = true));
            Assert.IsTrue(rejectedConnection);
        }
        finally { await owner.StopAsync(CancellationToken.None); }
    }

    private sealed class SessionClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private TickTimer? timer;
        public TaskCompletionSource TimerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) { now += duration; timer?.Fire(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new TickTimer(callback, state);
            TimerStarted.TrySetResult();
            return timer;
        }
        private sealed class TickTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static WebApplication CreateHost(string? directory, bool enabled = true, AuthenticationOptions? authentication = null)
    {
        var peer = SoulseekClientProxy.Create();
        return TestServerHost.Build([], new ServerOptions
        {
            Authentication = authentication ?? new() { Enabled = enabled, PasswordHash = PasswordHash, AllowHttp = enabled },
            Engine = new EngineSettings { Username = "local", Password = "test", ListenPort = null, LogLevel = LogLevel.None },
            Persistence = new() { Enabled = directory is not null, DataDirectory = directory, RetentionEnabled = false },
            ClientFactory = _ => peer.Client,
        }, "http://127.0.0.1:0");
    }
}
