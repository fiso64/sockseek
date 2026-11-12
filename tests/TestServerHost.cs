using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Sockseek.Api;
using Sockseek.Server;
using Sockseek.Server.Authentication;

namespace Tests;

internal static class TestServerHost
{
    internal const string Password = "test-only-password";
    private const string PasswordHash = "AQAAAAIAAYagAAAAEXNvY2tzZWVrLWZpeHR1cmUhLYKb2YbUDRN/4hvcRClorEeUEsqeimyd/peWr4DCr5Q=";
    private static readonly ConditionalWeakTable<WebApplication, Lazy<Task<string>>> Keys = new();

    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null)
    {
        options ??= new();
        if (options.Authentication.Enabled && options.Authentication.PasswordFile is null && options.Authentication.PasswordHash is null)
            options.Authentication = new() { PasswordHash = PasswordHash, AllowHttp = true };
        // Ordinary host fixtures have already created their isolated data root.
        // Reuse schema construction only; startup still validates/opens the copy.
        // Missing roots and existing/corrupt databases retain real startup behavior.
        if (options.Persistence is { Enabled: true, DataDirectory: { } directory }
            && Directory.Exists(directory))
        {
            string path = SockseekDataPaths.ResolveDatabasePath(directory);
            if (!File.Exists(path)) TestDatabase.Create(path);
        }
        // Engine and host share this app's logger factory; do not install its console output.
        return ServerHost.Build(args, options, url, logToConsole: false);
    }

    public static Task<string> GetApiKeyAsync(WebApplication app)
        => Keys.GetValue(app, host => new(async () =>
            (await host.Services.GetRequiredService<OperatorAuthentication>()
                .CreateKeyAsync("Integration fixture", CancellationToken.None)).Secret)).Value;

    public static async Task<HttpClient> CreateHttpClientAsync(WebApplication app)
    {
        string url = app.Urls.Single();
        if (app.Services.GetRequiredService<OperatorAuthentication>().KeyStoreAvailable)
            return SockseekApiClient.CreateHttpClient(url, await GetApiKeyAsync(app));

        // Hosts that deliberately omit persistence still exercise real browser authentication.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var login = new HttpClient(handler) { BaseAddress = new Uri(url) };
        var session = (await login.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
        login.DefaultRequestHeaders.Add("X-CSRF-Token", session.AntiforgeryToken);
        string username = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Authentication.Username;
        using var response = await login.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, Password));
        response.EnsureSuccessStatusCode();
        session = (await login.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
        var http = SockseekApiClient.CreateHttpClient(url);
        http.DefaultRequestHeaders.Add("Cookie", handler.CookieContainer.GetCookieHeader(new Uri(url)));
        http.DefaultRequestHeaders.Add("X-CSRF-Token", session.AntiforgeryToken);
        return http;
    }
}
