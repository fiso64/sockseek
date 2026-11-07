using Sockseek.Api;
using Sockseek.Core.UserProfiles;
using Sockseek.Server.PeerRestrictions;

namespace Sockseek.Server.UserProfiles;

internal static class UserProfileEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/users/{username}/profile", GetProfileAsync)
            .WithTags("User Browsing")
            .WithSummary("Gets a composite remote Soulseek user profile.")
            .Produces<UserProfileDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/users/{username}/presence", GetPresenceAsync)
            .WithTags("User Browsing")
            .WithSummary("Observes a peer's presence without fetching profile details.")
            .Produces<UserPresenceObservationDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/users/{username}/picture", GetPictureAsync)
            .WithTags("User Browsing")
            .WithSummary("Gets a validated remote Soulseek user profile picture.")
            .Produces<byte[]>(
                StatusCodes.Status200OK,
                contentType: "image/jpeg",
                additionalContentTypes: ["image/png", "image/gif", "image/webp"])
            .Produces(StatusCodes.Status304NotModified)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> GetProfileAsync(
        string username,
        DaemonRuntime runtime,
        PeerRestrictionCoordinator peerRestrictions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        UserProfileService? service = runtime.UserProfiles;
        if (service is null)
        {
            ServerLogMessages.FeatureRequestUnavailable(
                loggerFactory.CreateLogger("Sockseek.Server.UserProfiles.UserProfileEndpoints"),
                "user profiles");
            return Unavailable();
        }
        try
        {
            UserProfileDto profile = await service.GetAsync(
                username,
                refresh,
                cancellationToken).ConfigureAwait(false);
            UserRestrictionsDto restrictions = peerRestrictions.Get(profile.Username);
            return Results.Ok(profile with
            {
                UploadAccessBlocked = restrictions.UploadAccess.IsBlocked,
                PrivateMessagesBlocked = restrictions.PrivateMessages.IsBlocked,
            });
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> GetPictureAsync(
        string username,
        HttpRequest request,
        HttpResponse response,
        DaemonRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        UserProfileService? service = runtime.UserProfiles;
        if (service is null)
        {
            ServerLogMessages.FeatureRequestUnavailable(
                loggerFactory.CreateLogger("Sockseek.Server.UserProfiles.UserProfileEndpoints"),
                "user profiles");
            return Unavailable();
        }
        try
        {
            UserPicture? picture = await service.GetPictureAsync(
                username,
                cancellationToken).ConfigureAwait(false);
            if (picture is null)
            {
                return Results.NotFound(new ApiErrorDto(
                    "The user has no available profile picture.",
                    "picture-unavailable"));
            }

            response.Headers.ETag = picture.ETag;
            response.Headers.CacheControl = "private, max-age=30, must-revalidate";
            response.Headers.XContentTypeOptions = "nosniff";
            if (MatchesEtag(request.Headers.IfNoneMatch, picture.ETag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            response.ContentLength = picture.Bytes.Length;
            return Results.Bytes(picture.Bytes, picture.MediaType);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> GetPresenceAsync(
        string username,
        DaemonRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        UserProfileService? service = runtime.UserProfiles;
        if (service is null)
        {
            ServerLogMessages.FeatureRequestUnavailable(
                loggerFactory.CreateLogger("Sockseek.Server.UserProfiles.UserProfileEndpoints"),
                "user presence");
            return Unavailable();
        }
        try
        {
            return Results.Ok(await service.ObservePresenceAsync(
                username, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static bool MatchesEtag(
        Microsoft.Extensions.Primitives.StringValues values,
        string etag)
        => values
            .SelectMany(value => (value ?? "").Split(','))
            .Select(value => value.Trim())
            .Any(value => value == "*" || string.Equals(value, etag, StringComparison.Ordinal));

    private static IResult Failure(Exception exception)
        => exception switch
        {
            ArgumentException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "invalid-username")),
            UserProfileUnavailableException or InvalidOperationException => Unavailable(),
            _ => Unavailable(),
        };

    private static IResult Unavailable()
        => Results.Json(
            new ApiErrorDto("Soulseek profiles are unavailable.", "soulseek-unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

}
