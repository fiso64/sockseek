using Sockseek.Api;

namespace Sockseek.Server.PeerRestrictions;

internal static class PeerRestrictionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/users/{username}/restrictions", Get)
            .RequireOperator()
            .WithTags("User Restrictions")
            .WithSummary("Gets independent upload-access and private-message restrictions for one exact username.")
            .Produces<UserRestrictionsDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPut("/api/users/{username}/restrictions", SetAsync)
            .RequireOperator()
            .WithTags("User Restrictions")
            .WithSummary("Sets or removes one durable exact-username restriction override.")
            .WithDescription("Upload-access blocking denies future search, browse, directory, and upload admissions from the peer. Private-message blocking discards future incoming DMs only. Allowed supersedes that kind's configured username block. Null removes only that override. Configured upload IP denial remains independent.")
            .Produces<UserRestrictionsDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Get(
        string username,
        PeerRestrictionCoordinator coordinator)
    {
        try
        {
            return Results.Ok(coordinator.Get(username));
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> SetAsync(
        string username,
        SetUserRestrictionOverrideRequestDto request,
        PeerRestrictionCoordinator coordinator,
        EngineSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            UserRestrictionsDto result = await coordinator.SetAsync(
                username,
                request.Kind,
                request.Override,
                cancellationToken).ConfigureAwait(false);
            if (request.Kind == UserRestrictionKind.PrivateMessages
                && supervisor.Runtime.Chat is { } chat)
            {
                await chat.PublishPeerRestrictionsChangedAsync(
                    result.Username,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (request.Kind == UserRestrictionKind.UploadAccess)
            {
                supervisor.Runtime.Sharing?.PublishPeerRestrictionsChanged();
            }
            return Results.Ok(result);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static IResult Failure(Exception exception)
        => exception switch
        {
            PeerRestrictionPersistenceUnavailableException => Results.Json(
                new ApiErrorDto(exception.Message, "peer-restriction-persistence-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            ArgumentException => Results.BadRequest(new ApiErrorDto(
                exception.Message,
                "invalid-peer-username")),
            _ => Results.Json(
                new ApiErrorDto("Peer restriction mutation failed.", "peer-restriction-persistence-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
}
