using Sockseek.Api;

namespace Sockseek.Server.Planning;

internal static class JobPreviewEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/job-previews", CreateAsync)
            .WithTags("Job Preview")
            .WithSummary("Creates an asynchronous preview from the shared Core planner.")
            .WithDescription("Returns the Planning resource immediately. Wait for completion through its summary, then page the reviewed nodes.")
            .Produces<CreateJobPreviewResponseDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/job-previews/{previewId:guid}", GetAsync)
            .WithTags("Job Preview")
            .WithSummary("Gets the fixed-size preview summary and current revision.")
            .WithDescription("With waitForCompletion=true, waits up to 30 seconds while Planning. Returns immediately on completion; request cancellation stops waiting without cancelling planning.")
            .Produces<JobPreviewSummaryDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/job-previews/{previewId:guid}/nodes", GetNodesAsync)
            .WithTags("Job Preview")
            .WithSummary("Pages preview roots or the direct children of one stable planner ref.")
            .Produces<IReadOnlyList<JobPreviewNodeDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/job-previews/{previewId:guid}/commit", CommitAsync)
            .WithTags("Job Preview")
            .WithSummary("Commits a client-owned revision-bound selection without replanning or rereading its source.")
            .Produces<CommitJobPreviewResponseDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> CreateAsync(
        CreateJobPreviewRequestDto request,
        JobPreviewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        try
        {
            CreateJobPreviewResponseDto created = await coordinator.CreateAsync(
                request, cancellationToken).ConfigureAwait(false);
            return Results.Accepted(
                $"/api/job-previews/{created.Preview.PreviewId}",
                created);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> GetAsync(
        Guid previewId,
        JobPreviewCoordinator coordinator,
        CancellationToken cancellationToken,
        bool waitForCompletion = false)
    {
        try
        {
            JobPreviewSummaryDto? preview = await coordinator.GetAsync(
                previewId, cancellationToken, waitForCompletion).ConfigureAwait(false);
            return preview == null
                ? NotFound()
                : Results.Ok(preview);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> GetNodesAsync(
        Guid previewId,
        HttpResponse response,
        JobPreviewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? parentRef = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize)
    {
        try
        {
            CursorPage<JobPreviewNodeDto>? page = await coordinator.GetNodesAsync(
                previewId,
                parentRef,
                cursor,
                limit,
                cancellationToken).ConfigureAwait(false);
            if (page == null)
                return NotFound();
            if (page.NextCursor != null)
                response.Headers["X-Next-Cursor"] = page.NextCursor;
            return Results.Ok(page.Items);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> CommitAsync(
        Guid previewId,
        CommitJobPreviewRequestDto request,
        JobPreviewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        try
        {
            CommitJobPreviewResponseDto? receipt = await coordinator.CommitAsync(
                previewId,
                request,
                cancellationToken).ConfigureAwait(false);
            return receipt == null
                ? NotFound()
                : Results.Accepted(
                    receipt.SubmissionId is Guid submissionId
                        ? $"/api/submissions/{submissionId}"
                        : $"/api/job-previews/{previewId}",
                    receipt);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static IResult Failure(Exception exception)
        => exception switch
        {
            JobPreviewUnavailableException => Results.Json(
                new ApiErrorDto(exception.Message, "preview-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            KeyNotFoundException => NotFound(),
            InvalidOperationException => Results.Json(
                new ApiErrorDto(exception.Message, "preview-conflict"),
                statusCode: StatusCodes.Status409Conflict),
            ArgumentException or InvalidDataException or NotSupportedException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "invalid-preview-request")),
            _ => Results.Json(
                new ApiErrorDto("Job Preview failed.", "preview-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };

    private static IResult NotFound()
        => Results.NotFound(new ApiErrorDto(
            "The job preview was not found.",
            "preview-not-found"));
}
