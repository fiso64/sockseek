using Sockseek.Api;
using Sockseek.Server.Persistence;
using static Sockseek.Server.ApiErrors;

namespace Sockseek.Server;

internal static class TransferEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/transfers", async (
            TransferReadService transfers,
            HttpContext httpContext,
            Guid? jobId,
            Guid? workflowId,
            string? direction,
            string? source,
            string? state,
            string? terminalOutcome,
            string? username,
            string? groupRef,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            bool? archived,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await transfers.GetTransfersAsync(new(
                    Cursor: cursor, Limit: limit ?? ServerProtocol.DefaultPageSize,
                    JobId: jobId, WorkflowId: workflowId, Direction: direction, Source: source,
                    State: state, TerminalOutcome: terminalOutcome, Username: username,
                    FromUtc: fromUtc, ToUtc: toUtc, Archived: archived ?? false, GroupRef: groupRef),
                    cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Pages the combined newest-first live and retained transfer timeline.")
            .WithDescription("The cursor is a moving keyset over stable creation time and transfer ID. New transfers may appear above an existing traversal; status changes do not reorder rows.")
            .Produces<TransferTimelinePageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/dashboard/analytics", async (
            string? range,
            DashboardAnalyticsFacade analytics,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await analytics.GetAsync(range, cancellationToken));
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Dashboard")
            .WithSummary("Gets bounded transfer analytics for a dashboard range.")
            .WithDescription("Ranges are 24h, 7d, 30d, 90d, 1y, or all. Coverage explicitly reports retention and persistence degradation; byte activity is cumulative-attempt accounting, not transfer creation size.")
            .Produces<DashboardAnalyticsDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/transfers/live", (
            EngineSupervisor supervisor,
            EngineStateStore stateStore,
            LiveTransferCursorCodec cursors,
            string? direction,
            string? state,
            string? username,
            string? cursor,
            int? limit) =>
        {
            try
            {
                SharingRuntime? sharing = supervisor.Runtime.Sharing;
                if (sharing is null)
                {
                    return Results.Json(
                        new ApiErrorDto("Upload runtime is unavailable.", "UploadsUnavailable"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (direction is not null
                    && !direction.Equals("upload", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only live upload queue rows are available.");
                if (state is not null
                    && !state.Equals("queued", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only queued live transfer rows are pageable.");

                LiveTransferCursor? decoded = cursor is null
                    ? null
                    : cursors.Decode(cursor);
                var page = sharing.Uploads.GetQueuePage(
                    decoded?.RequestedAtUtc,
                    decoded?.TransferId,
                    limit ?? ServerProtocol.DefaultPageSize,
                    decoded?.ObservedQueueRevision,
                    username);
                string? next = page.NextRequestedAtUtc is { } nextTime
                               && page.NextTransferId is { } nextId
                    ? cursors.Encode(
                        nextTime,
                        nextId,
                        page.ObservedQueueRevision)
                    : null;
                var items = page.Items
                    .Select(item => stateStore.GetLiveTransfer(item.TransferId))
                    .Where(item => item is not null)
                    .Cast<TransferStateDto>()
                    .ToArray();
                return Results.Ok(new LiveTransferPageDto(
                    items,
                    next,
                    page.ObservedQueueRevision,
                    page.QueueChanged));
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Pages the bounded live transfer queue.")
            .Produces<LiveTransferPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/transfers/lookup", async (
            TransferLookupRequestDto request,
            TransferReadService transfers,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await transfers.LookupTransfersAsync(request, cancellationToken)); }
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
        })
            .WithTags("Transfers")
            .WithSummary("Looks up current unarchived transfer timeline rows by ID.")
            .WithDescription("Uses the same live, queued and retained projection as the timeline. Returns all found IDs in newest-first order without a continuation. Missing IDs are omitted; retained availability is explicit.")
            .Produces<TransferTimelinePageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/transfers/timeline", async (
            TransferTimelineScopeDto scope,
            TransferReadService transfers,
            ServerEventBroadcaster events,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await transfers.GetTransferTimelineSnapshotAsync(scope, events, cancellationToken));
            }
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
        })
            .RequireOperator()
            .WithTags("Transfers")
            .WithSummary("Snapshots a timeline extent, expanded files and requested folder totals.")
            .WithDescription("Uses the same scoped read as ObserveTransferTimeline. Window membership and boundary describe the moving prefix independently of explicitly requested additional files.")
            .Produces<TransferTimelineUpdateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/transfers/group-summaries", async (
            TransferGroupSummariesRequestDto request,
            TransferReadService transfers,
            CancellationToken cancellationToken) =>
        {
            if (request.Groups is null || request.Groups.Any(group => group is null || string.IsNullOrEmpty(group.GroupRef)))
                return Results.BadRequest(new ApiErrorDto(
                    "Each transfer group needs an exact group reference.",
                    "InvalidTransferGroup"));
            return Results.Ok(await transfers.GetTransferGroupSummariesAsync(
                request.Groups,
                cancellationToken));
        })
            .WithTags("Transfers")
            .WithSummary("Gets moving whole-group totals for the requested transfer groups.")
            .WithDescription("Uses the retained transfer ledger with a live overlay; group refs are opaque, and an unavailable retained resource does not yield authoritative totals.")
            .Produces<TransferGroupSummariesResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/transfers/{transferId:guid}", async (
            Guid transferId,
            TransferReadService transfers,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await transfers.GetTransferDetailAsync(transferId, cancellationToken);
                return detail != null
                    ? Results.Ok(detail)
                    : Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"));
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Gets one transfer with its latest attempt and optional queue estimate.")
            .Produces<TransferDetailDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapPost("/api/transfers/{transferId:guid}/cancel", async (
            Guid transferId,
            EngineSupervisor supervisor,
            TransferReadService transfers,
            CancellationToken cancellationToken) =>
        {
            TransferStateDto? live = supervisor.StateStore.GetLiveTransfer(transferId);
            if (live?.Identity.Direction.Equals("Download", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!supervisor.TryCancelDownloadTransfer(transferId))
                {
                    return Results.Conflict(new ApiErrorDto(
                        "The download transfer is no longer cancellable.",
                        "TransferNotCancellable"));
                }
                return Results.Ok(supervisor.StateStore.GetLiveTransfer(transferId) ?? live);
            }

            SharingRuntime? sharing = supervisor.Runtime.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Transfer runtime is unavailable.", "TransfersUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var current = sharing.Uploads.GetTransfer(transferId);
            if (current is null)
            {
                TransferDetailDto? retained = await transfers.GetTransferDetailAsync(
                    transferId,
                    cancellationToken);
                return retained is null
                    ? Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"))
                    : Results.Conflict(new ApiErrorDto(
                        "The retained transfer is no longer cancellable.",
                        "TransferNotCancellable"));
            }
            if (!sharing.Uploads.Cancel(transferId))
            {
                return Results.Conflict(
                    new ApiErrorDto("The transfer is no longer cancellable.", "TransferNotCancellable"));
            }
            return Results.Ok(supervisor.StateStore.GetLiveTransfer(transferId));
        })
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Cancels a queued or active download or upload transfer.")
            .Produces<TransferStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/transfers/cancel", (
            BulkCancelTransfersRequestDto request,
            EngineSupervisor supervisor) =>
            Results.Ok(supervisor.CancelTransfers(request)))
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Cancels a snapshot of transfers by direction, queue state, and optional exact peer/group.")
            .WithDescription("Targets are resolved once, then cancelled independently. Races and per-transfer failures are reported in a fixed-size receipt.")
            .Produces<TransferCommandReceiptDto>();

        app.MapPost("/api/transfers/{transferId:guid}/archive", async (
            Guid transferId,
            SetTransferArchivedRequestDto request,
            TransferReadService transfers,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await transfers.SetTransfersArchivedAsync(
                    new Sockseek.Persistence.Read.TransferArchiveFilter(TransferId: transferId),
                    request.Archived,
                    cancellationToken));
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Archives or restores one terminal retained transfer.")
            .WithDescription("Archive is reversible and separate from cancellation; a nonterminal transfer is rejected.")
            .Produces<TransferCommandReceiptDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/transfers/archive", async (
            ArchiveTransfersRequestDto request,
            TransferReadService transfers,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await transfers.SetTransfersArchivedAsync(
                    new Sockseek.Persistence.Read.TransferArchiveFilter(
                        Direction: request.Direction,
                        TerminalOutcome: request.TerminalOutcome,
                        Username: request.Username,
                        FromUtc: request.FromUtc,
                        ToUtc: request.ToUtc,
                        GroupRef: request.GroupRef),
                    request.Archived,
                    cancellationToken));
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Archives or restores terminal transfer history, optionally scoped to an exact peer/group.")
            .WithDescription("The command is reversible, excludes active transfers, and returns bounded outcome counts.")
            .Produces<TransferCommandReceiptDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/transfers/{transferId:guid}/attempts", async (
            Guid transferId,
            int afterAttemptNumber,
            int? limit,
            TransferReadService transfers,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await transfers.GetTransferAttemptsAsync(
                    transferId, afterAttemptNumber, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                if (page?.NextAttemptNumber != null)
                    httpContext.Response.Headers["X-Next-Attempt-Number"] = page.NextAttemptNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return page != null ? Results.Ok(page.Items) : Results.NotFound();
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Lists current and retained attempts for one transfer.")
            .Produces<IReadOnlyList<TransferAttemptHistoryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }
}
