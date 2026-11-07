using System.Net;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Sockseek.Api;

namespace Sockseek.Server.Authentication;

internal static class AuthSetup
{
    internal const string ApiKeyScheme = "ApiKey";
    internal const string CookieScheme = "Browser";
    internal const string ApiKeyHeader = "X-API-Key";
    internal const string AntiforgeryHeader = "X-CSRF-Token";

    public static void AddAuthenticationContract(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            {
                [ApiKeyScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = ApiKeyHeader,
                    Description = "A named operator API key. Explicit invalid keys never fall back to cookies.",
                },
                [CookieScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = "sockseek-session",
                    Description = "WebUI session; mutations additionally require X-CSRF-Token from /api/auth/session.",
                },
            };
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            bool anonymous = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any();
            operation.Security = anonymous ? [] :
            [
                new() { [new OpenApiSecuritySchemeReference(ApiKeyScheme, context.Document)] = [] },
                new() { [new OpenApiSecuritySchemeReference(CookieScheme, context.Document)] = [] },
            ];
            if (!anonymous)
            {
                operation.Responses ??= new();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication required or credential expired/revoked." });
                operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Access denied." });
            }
            return Task.CompletedTask;
        });
    }

    public static void AddOperatorAuthentication(this IServiceCollection services, bool schemaOnly)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OperatorAuthentication>();
        if (!schemaOnly) services.AddHostedService(sp => sp.GetRequiredService<OperatorAuthentication>());
        services.AddAuthentication("Operator")
            .AddPolicyScheme("Operator", null, scheme => scheme.ForwardDefaultSelector = context =>
                !context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value.Authentication.Enabled
                || context.Request.Headers.ContainsKey(ApiKeyHeader) ? ApiKeyScheme : CookieScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyScheme, _ => { })
            .AddCookie(CookieScheme);
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<OperatorAuthentication, IOptions<ServerOptions>, TimeProvider>((cookie, owner, server, clock) =>
            {
                cookie.Cookie.Name = "sockseek-session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = server.Value.Authentication.Enabled && !server.Value.Authentication.AllowHttp
                    ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = false;
                cookie.SessionStore = owner;
                cookie.TimeProvider = clock;
                cookie.Events.OnRedirectToLogin = context => Reject(context.HttpContext, 401, "AuthenticationRequired");
                cookie.Events.OnRedirectToAccessDenied = context => Reject(context.HttpContext, 403, "AccessDenied");
            });
        services.AddAuthorizationBuilder().SetFallbackPolicy(
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddAntiforgery(options => options.HeaderName = AntiforgeryHeader);
        services.AddOptions<AntiforgeryOptions>().Configure<IOptions<ServerOptions>>((antiforgery, server) =>
        {
            antiforgery.Cookie.Name = "sockseek-csrf";
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = server.Value.Authentication.Enabled && !server.Value.Authentication.AllowHttp
                ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1;
        });
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ServerOptions>>((forwarded, server) =>
        {
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            // Empty framework allowlists mean "trust everyone", not "trust nobody".
            if (server.Value.Authentication.TrustedProxies.Length == 0)
                forwarded.ForwardedHeaders = ForwardedHeaders.None;
            foreach (string address in server.Value.Authentication.TrustedProxies)
                forwarded.KnownProxies.Add(IPAddress.Parse(address));
        });
        // One bounded, application-wide limiter protects the single account without per-IP state growth.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) => new ValueTask(Reject(context.HttpContext, 429, "LoginRateLimited"));
            options.AddPolicy("Login", _ => RateLimitPartition.GetTokenBucketLimiter("operator", _ => new()
            {
                TokenLimit = 10, TokensPerPeriod = 5, ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true, QueueLimit = 0,
            }));
        });
        services.AddSingleton<AuthenticationHubFilter>();
        services.Configure<HubOptions>(hub => hub.AddFilter<AuthenticationHubFilter>());
    }

    public static void UseOperatorAuthentication(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseRouting();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
            var options = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value.Authentication;
            if (options.Enabled && !options.AllowHttp && !context.Request.IsHttps)
            {
                await Reject(context, 400, "HttpsRequired");
                return;
            }
            // This also protects WebSocket upgrades; CORS does not apply to WebSockets.
            var origin = context.Request.Headers.Origin;
            if (origin.Count > 0 && (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var uri)
                || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out var server)
                || uri.Scheme != server.Scheme || uri.IdnHost != server.IdnHost || uri.Port != server.Port))
            {
                await Reject(context, 403, "OriginRejected");
                return;
            }
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
            {
                await Reject(context, 403, "OriginRejected");
                return;
            }
            bool safeMethod = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                || HttpMethods.IsOptions(context.Request.Method);
            bool apiKey = context.User.Identity?.AuthenticationType == ApiKeyScheme;
            // SignalR browser transports use origin checks; native clients authenticate with a header.
            bool hub = context.Request.Path.StartsWithSegments("/api/events");
            bool browser = context.User.Identity?.AuthenticationType == CookieScheme || origin.Count > 0
                || context.Request.Headers.ContainsKey("Sec-Fetch-Site");
            if (!safeMethod && !apiKey && !hub && (browser || context.Request.Path == "/api/auth/login"))
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    await Reject(context, 400, "AntiforgeryRejected");
                    return;
                }
            }
            await next(context);
        });
        app.UseAuthorization();
        app.UseRateLimiter();
    }

    internal static Task Reject(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ApiErrorDto(code switch
        {
            "HttpsRequired" => "HTTPS is required. Configure a TLS endpoint or trusted reverse proxy.",
            "AuthenticationRequired" => "Authentication is required.",
            "AntiforgeryRejected" => "The browser security token is missing or invalid. Refresh and retry.",
            "LoginRateLimited" => "Too many login attempts. Try again shortly.",
            _ => "Access denied.",
        }, code));
    }
}

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemes, ILoggerFactory logger, UrlEncoder encoder,
    IOptions<ServerOptions> server, OperatorAuthentication owner)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!server.Value.Authentication.Enabled)
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                OperatorAuthentication.Principal("Operator", "NoAuth"), Scheme.Name)));
        var keys = Request.Headers[AuthSetup.ApiKeyHeader];
        var user = keys.Count == 1 ? owner.AuthenticateKey(keys[0]!) : null;
        return Task.FromResult(user is null ? AuthenticateResult.Fail("Invalid API key.")
            : AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => AuthSetup.Reject(Context, 401, "AuthenticationRequired");
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => AuthSetup.Reject(Context, 403, "AccessDenied");
}

internal sealed class AuthenticationHubFilter(OperatorAuthentication owner) : IHubFilter
{
    private static readonly object RegistrationKey = new();

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var registration = owner.ObserveRevocation(context.Context.User!, context.Context.Abort);
        context.Context.Items[RegistrationKey] = registration;
        try { await next(context); }
        catch { registration.Dispose(); throw; }
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (context.Context.Items.Remove(RegistrationKey, out var item)
            && item is CancellationTokenRegistration registration) registration.Dispose();
        await next(context, exception);
    }
}
