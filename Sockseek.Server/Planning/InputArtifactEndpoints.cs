using Sockseek.Api;

namespace Sockseek.Server.Planning;

internal static class InputArtifactEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/input-artifacts", UploadAsync)
            .RequireOperator()
            .WithTags("Input Artifacts")
            .WithSummary("Streams an immutable browser input into daemon-owned expiring storage.")
            .WithDescription("The browser filename is retained only as safe presentation metadata and is never used as a daemon path. No internal file-size cap is imposed.")
            .Accepts<byte[]>("application/octet-stream", "text/csv", "text/plain")
            .Produces<InputArtifactDto>(StatusCodes.Status201Created)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/input-artifacts/{artifactId}", GetAsync)
            .RequireOperator()
            .WithTags("Input Artifacts")
            .WithSummary("Gets immutable artifact metadata.")
            .Produces<InputArtifactDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        InputArtifactCoordinator artifacts,
        CancellationToken cancellationToken,
        string? fileName = null)
    {
        try
        {
            fileName ??= request.Headers["X-File-Name"].FirstOrDefault();
            InputArtifactDto artifact = await artifacts.UploadAsync(
                request.Body,
                fileName,
                cancellationToken).ConfigureAwait(false);
            return Results.Created(
                $"/api/input-artifacts/{artifact.ArtifactId}",
                artifact);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> GetAsync(
        string artifactId,
        InputArtifactCoordinator artifacts,
        CancellationToken cancellationToken)
    {
        try
        {
            InputArtifactDto? artifact = await artifacts.GetAsync(
                artifactId,
                cancellationToken).ConfigureAwait(false);
            return artifact == null
                ? Results.NotFound(new ApiErrorDto(
                    "The input artifact was not found or has expired.",
                    "artifact-not-found"))
                : Results.Ok(artifact);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static IResult Failure(Exception exception)
        => exception switch
        {
            InputArtifactUnavailableException => Results.Json(
                new ApiErrorDto(exception.Message, "artifact-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            ArgumentException => Results.BadRequest(new ApiErrorDto(
                exception.Message,
                "invalid-artifact-id")),
            _ => Results.Json(
                new ApiErrorDto("Input artifact storage failed.", "artifact-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
}
