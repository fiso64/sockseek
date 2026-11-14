using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sockseek.Api;

namespace Sockseek.Server.Authentication;

internal static class AuthenticationEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");
        group.MapGet("/session", Session).AllowAnonymous();
        group.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting("Login")
            .WithDescription("First GET /api/auth/session for the antiforgery cookie and token; send that token in X-CSRF-Token. After login, GET the session again to refresh the token.")
            .Produces(StatusCodes.Status204NoContent).Produces<ApiErrorDto>(400).Produces<ApiErrorDto>(401)
            .Produces(StatusCodes.Status429TooManyRequests);
        group.MapPost("/logout", async (HttpContext context, ILogger<OperatorAuthentication> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await context.SignOutAsync(AuthSetup.CookieScheme);
            ServerLogMessages.Logout(logger, context.Connection.RemoteIpAddress);
            return Results.NoContent();
        });
        group.MapGet("/keys", (OperatorAuthentication owner) => owner.KeyStoreAvailable
            ? Results.Ok(owner.ListKeys()) : KeyStoreUnavailable())
            .Produces<IReadOnlyList<ApiKeyDto>>().Produces<ApiErrorDto>(503);
        group.MapPost("/keys", async (CreateApiKeyRequestDto request, HttpContext context,
            OperatorAuthentication owner, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!owner.KeyStoreAvailable) return KeyStoreUnavailable();
            try { return Results.Ok(await owner.CreateKeyAsync(request.Name ?? "", ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new ApiErrorDto(ex.Message, "InvalidKeyName")); }
        }).Produces<CreatedApiKeyDto>().Produces<ApiErrorDto>(400).Produces<ApiErrorDto>(503);
        group.MapDelete("/keys/{id:guid}", async (Guid id, OperatorAuthentication owner, CancellationToken ct) =>
        {
            if (!owner.KeyStoreAvailable) return KeyStoreUnavailable();
            return await owner.RevokeKeyAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        }).Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces<ApiErrorDto>(503);
    }

    private static SessionDto Session(HttpContext context, IOptions<ServerOptions> server, IAntiforgery antiforgery)
    {
        context.Response.Headers.CacheControl = "no-store";
        // Anonymous bootstrap intentionally exposes only auth state, not daemon status.
        var tokens = antiforgery.GetAndStoreTokens(context);
        return new(server.Value.Authentication.Enabled, context.User.Identity?.IsAuthenticated == true,
            context.User.Identity?.Name, context.Features.Get<IAuthenticateResultFeature>()?
                .AuthenticateResult?.Properties?.ExpiresUtc, tokens.RequestToken!);
    }

    private static async Task<IResult> Login(LoginRequestDto request, HttpContext context,
        IOptions<ServerOptions> server, OperatorAuthentication owner, TimeProvider clock,
        ILogger<OperatorAuthentication> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        var options = server.Value.Authentication;
        if (!options.Enabled) return Results.NoContent();
        if (request.Username is null || request.Password is null || request.Username.Length > 200
            || request.Password.Length is 0 or > 4096 || !owner.VerifyPassword(request.Username, request.Password))
        {
            ServerLogMessages.LoginRejected(logger, context.Connection.RemoteIpAddress);
            return Results.Json(new ApiErrorDto("Invalid username or password.", "InvalidCredentials"), statusCode: 401);
        }
        // Do not renew an existing session handle: copied cookies must never become valid again.
        // To start another fixed lifetime, log out first (a separate request).
        if ((await context.AuthenticateAsync(AuthSetup.CookieScheme)).Succeeded)
            return Results.NoContent();
        await context.SignInAsync(AuthSetup.CookieScheme,
            OperatorAuthentication.Principal(options.Username, AuthSetup.CookieScheme), new AuthenticationProperties
            {
                IsPersistent = true, AllowRefresh = false,
                IssuedUtc = clock.GetUtcNow(), ExpiresUtc = clock.GetUtcNow() + options.SessionLifetime,
            });
        ServerLogMessages.LoginAccepted(logger, context.Connection.RemoteIpAddress);
        // Bootstrap again with the new cookie to obtain the authenticated antiforgery token.
        return Results.NoContent();
    }

    private static IResult KeyStoreUnavailable() => Results.Json(new ApiErrorDto(
        "API-key management requires available daemon persistence.", "KeyStoreUnavailable"), statusCode: 503);
}
