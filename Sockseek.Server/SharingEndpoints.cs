using Sockseek.Api;
using Sockseek.Server.Persistence;
using static Sockseek.Server.ApiErrors;

namespace Sockseek.Server;

internal static class SharingEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sharing", (DaemonRuntime runtime) =>
        {
            SharingRuntime? sharing = runtime.Sharing;
            return sharing is null
                ? Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(sharing.GetSharingState());
        })
            .WithTags("Sharing")
            .WithSummary("Gets bounded sharing and catalog status.")
            .Produces<SharingStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/sharing/scans", (DaemonRuntime runtime) =>
        {
            SharingRuntime? sharing = runtime.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (sharing.GetSharingState().State == DaemonFeatureState.Disabled)
            {
                return Results.Json(
                    new ApiErrorDto("No share roots are configured.", "SharingNotConfigured"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var started = sharing.StartScan();
            if (started is null)
            {
                return Results.Json(
                    new ApiErrorDto("The scan could not be started.", "ScanUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var response = new StartShareScanResponseDto(
                started.Value.Started
                    ? StartShareScanResult.Started
                    : StartShareScanResult.AlreadyRunning,
                started.Value.Scan);
            return started.Value.Started
                ? Results.Json(response, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(response);
        })
            .WithTags("Sharing")
            .WithSummary("Starts a share scan or returns the currently active scan.")
            .Produces<StartShareScanResponseDto>(StatusCodes.Status202Accepted)
            .Produces<StartShareScanResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/sharing/scans/{scanId:guid}", (
            Guid scanId,
            DaemonRuntime runtime) =>
        {
            ShareScanStateDto? scan = runtime.Sharing?.GetScan(scanId);
            return scan is null
                ? Results.NotFound(new ApiErrorDto(
                    "The scan was not found.",
                    "ScanNotFound"))
                : Results.Ok(scan);
        })
            .WithTags("Sharing")
            .WithSummary("Gets an active or recently completed share scan.")
            .Produces<ShareScanStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapPost("/api/sharing/scans/{scanId:guid}/cancel", (
            Guid scanId,
            DaemonRuntime runtime) =>
        {
            SharingRuntime? sharing = runtime.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            ShareScanStateDto? scan = sharing.GetScan(scanId);
            if (scan is null)
                return Results.NotFound(new ApiErrorDto("The scan was not found.", "ScanNotFound"));
            if (!sharing.CancelScan(scanId))
            {
                return Results.Conflict(
                    new ApiErrorDto("The scan is no longer cancellable.", "ScanNotCancellable"));
            }
            return Results.Ok(sharing.GetScan(scanId) ?? scan);
        })
            .WithTags("Sharing")
            .WithSummary("Cancels an active share scan.")
            .Produces<ShareScanStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }
}
