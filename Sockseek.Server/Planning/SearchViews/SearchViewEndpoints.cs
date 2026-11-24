using Sockseek.Api;

namespace Sockseek.Server.Planning;

internal static class SearchViewEndpoints
{
    // Nullable query parameters keep generated request delegates on standard
    // Func signatures; each handler applies its collection's default below.
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/jobs/{jobId:guid}/search-views", CreateViewAsync)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Creates a revisioned projection over a live or retained search.")
            .WithDescription("Returns revision zero immediately. Poll the latest summary revision while the search is running, then page its typed collections from one immutable revision.")
            .Produces<SearchViewSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/search-views/{viewId:guid}", Get)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Gets fixed-size counters and the latest immutable revision.")
            .Produces<SearchViewSummaryDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/search-views/{viewId:guid}/revisions/{revision:long}/lifetime",
            (Guid viewId, long revision, SearchViewCoordinator coordinator, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                var remaining = coordinator.Cache.RemainingLifetime(viewId, revision);
                return remaining is { } time
                    ? Results.Ok(new SearchViewRevisionLifetimeDto((long)Math.Ceiling(time.TotalMilliseconds)))
                    : NotFound();
            })
            .WithTags("Search Views")
            .WithSummary("Checks how long a result revision remains available without extending its lifetime.")
            .Produces<SearchViewRevisionLifetimeDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapGet("/api/search-views/{viewId:guid}/definition", GetDefinition)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Gets the exact projection and effective search settings retained for this view.")
            .Produces<SearchViewDefinitionDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/search-views/{viewId:guid}/files", GetFiles)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Pages file rows from one immutable search-view revision.")
            .WithDescription("sortOrder reorders the existing projected rows; omitting it uses the view default. Keep the same sortOrder value (or omission) when continuing a cursor. Cursors bind the view, revision and requested order; counters describe the same revision as the rows.")
            .Produces<SearchViewFilePageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/search-views/{viewId:guid}/directories", GetDirectories)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Pages exact peer-directory summaries from one immutable revision.")
            .WithDescription("sortOrder reorders existing directory summaries; omitting it uses the view default. Keep the same sortOrder value (or omission) when continuing a cursor. Membership, counters and child-file ordering retain their revision semantics.")
            .Produces<SearchViewDirectoryPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet(
                "/api/search-views/{viewId:guid}/directories/{directoryRef}/files",
                GetDirectoryFiles)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Pages exact relative child paths under a directory in one immutable revision.")
            .Produces<SearchViewDirectoryFilePageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost(
                "/api/search-views/{viewId:guid}/directories/retrieve",
                RetrieveDirectoryAsync)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Retrieves an exact peer directory issued by a Search View revision.")
            .WithDescription("Runs the generic folder-retrieval job. Completion publishes retrieved totals and child refs as a new immutable Search View revision; the issuing revision remains unchanged.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/search-views/{viewId:guid}/track-groups", GetTrackGroups)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Pages track group summaries from one immutable revision.")
            .Produces<SearchViewTrackGroupPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet(
                "/api/search-views/{viewId:guid}/track-groups/{groupRef}/alternatives",
                GetTrackGroupOptions)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Pages one track group's ordered alternatives.")
            .WithDescription("Filter by peer or path, or use previousTo or nextTo with an exact option ref to read one unfiltered neighbor and its one-based position.")
            .Produces<SearchViewTrackGroupOptionPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/search-views/{viewId:guid}/album-groups", GetAlbumGroups)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Pages album group summaries from one immutable revision.")
            .Produces<SearchViewAlbumGroupPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet(
                "/api/search-views/{viewId:guid}/album-groups/{groupRef}/alternatives",
                GetAlbumGroupOptions)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Pages one album group's directory alternatives.")
            .WithDescription("Filter by peer or path, or use previousTo or nextTo with an exact option ref to read one unfiltered neighbor and its one-based position.")
            .Produces<SearchViewAlbumGroupOptionPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/search-views/{viewId:guid}/chosen-options/resolve", ResolveChosenOptions)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Reads exact browser-held group choices in one Search View revision.")
            .WithDescription("Returns a current option DTO or an explicit miss for each requested group. Choices remain client-owned and are never saved by this read.")
            .Produces<ResolveSearchViewChosenOptionsResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/search-views/{viewId:guid}/updates", GetUpdatesAsync)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Gets the latest fixed-size summary for inexpensive live refresh.")
            .WithDescription("When HasNewRevision is true, refetch only visible pages and expanded groups at Summary.Revision. Optional waitForChange waits up to one second for a new revision; completed Views return immediately.")
            .Produces<SearchViewUpdateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/search-views/{viewId:guid}/commit", CommitSelectionAsync)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Commits a client-owned revision-bound selection as one submission.")
            .WithDescription("The fixed-size receipt reports independent resolution outcomes; runtime jobs are traversed through the jobs API.")
            .Produces<CommitSearchViewSelectionResponseDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/search-views/{viewId:guid}/library-matches", LibraryMatches)
            .WithTags("Search Views")
            .WithSummary("Returns song or album group references with a matching local copy.")
            .Produces<string[]>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/search-views/{viewId:guid}/selection-settings", ResolveSelectionSettings)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Resolves the effective download defaults for a client-owned selection.")
            .Produces<ResolveSearchViewSelectionSettingsResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/search-views/{viewId:guid}/selection-counts", CountSelection)
            .MapErrors(Failure)
            .WithTags("Search Views")
            .WithSummary("Counts observed public and locked files in a client-owned selection.")
            .Produces<CountSearchViewSelectionResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> CreateViewAsync(
        Guid jobId,
        CreateSearchViewRequestDto request,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        SearchViewSummaryDto? created = await coordinator.CreateAsync(
            jobId,
            request,
            cancellationToken).ConfigureAwait(false);
        return created == null
            ? NotFound("The search job was not found.")
            : Results.Accepted($"/api/search-views/{created.ViewId}", created);
    }

    private static async Task<IResult> CommitSelectionAsync(
        Guid viewId,
        CommitSearchViewSelectionRequestDto request,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        CommitSearchViewSelectionResponseDto? receipt = await coordinator.Selections
            .CommitSelectionAsync(viewId, request, cancellationToken)
            .ConfigureAwait(false);
        return receipt == null
            ? NotFound()
            : Results.Accepted($"/api/submissions/{receipt.SubmissionId}", receipt);
    }

    private static async Task<IResult> LibraryMatches(Guid viewId, SearchViewLibraryMatchesRequestDto request,
        SearchViewCoordinator coordinator, CancellationToken cancellationToken)
    {
        try
        {
            var matches = await coordinator.Selections.LibraryMatchesAsync(viewId, request, cancellationToken);
            return matches == null ? NotFound() : Results.Ok(matches);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> ResolveSelectionSettings(
        Guid viewId,
        ResolveSearchViewSelectionSettingsRequestDto request,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var settings = coordinator.Selections.ResolveSelectionSettings(viewId, request, cancellationToken);
        return settings == null ? NotFound() : Results.Ok(settings);
    }

    private static async Task<IResult> CountSelection(
        Guid viewId,
        CountSearchViewSelectionRequestDto request,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var counts = coordinator.Selections.CountSelection(viewId, request, cancellationToken);
        return counts == null ? NotFound() : Results.Ok(counts);
    }

    private static async Task<IResult> ResolveChosenOptions(
        Guid viewId,
        ResolveSearchViewChosenOptionsRequestDto request,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var resolved = coordinator.ResolveChosenOptions(viewId, request, cancellationToken);
        return resolved == null ? NotFound() : Results.Ok(resolved);
    }

    private static async Task<IResult> GetDirectories(
        Guid viewId,
        long revision,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? cursor,
        int? limit,
        SearchViewSortOrder? sortOrder)
    {
        SearchViewDirectoryPageDto? page = coordinator.GetDirectories(viewId, revision, cursor, limit ?? ServerProtocol.DefaultNestedPageSize, sortOrder, cancellationToken: cancellationToken);
        return page == null
            ? NotFound("The search view directory revision was not found.")
            : Results.Ok(page);
    }

    private static async Task<IResult> GetDirectoryFiles(
        Guid viewId,
        string directoryRef,
        long revision,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? cursor,
        int? limit)
    {
        SearchViewDirectoryFilePageDto? page = coordinator.GetDirectoryFiles(viewId, directoryRef, revision, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
        return page == null
            ? NotFound("The search view directory revision was not found.")
            : Results.Ok(page);
    }

    private static async Task<IResult> RetrieveDirectoryAsync(
        Guid viewId,
        RetrieveSearchViewDirectoryRequestDto request,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        JobSummaryDto? job = await coordinator.StartDirectoryRetrievalAsync(
            viewId,
            request,
            cancellationToken).ConfigureAwait(false);
        return job == null
            ? NotFound("The Search View or source workflow was not found.")
            : Results.Accepted($"/api/jobs/{job.JobId}", job);
    }

    private static async Task<IResult> GetTrackGroups(
        Guid viewId,
        long revision,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? cursor,
        int? limit)
    {
        SearchViewTrackGroupPageDto? page = coordinator.GetTrackGroups(viewId, revision, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
        return page == null
            ? NotFound("The track-group revision was not found.")
            : Results.Ok(page);
    }

    private static async Task<IResult> GetTrackGroupOptions(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? cursor,
        int? limit,
        string? filterText,
        string? previousTo,
        string? nextTo)
    {
        SearchViewTrackGroupOptionPageDto? page = coordinator.GetTrackGroupOptions(viewId, groupRef, revision, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken, new(filterText, previousTo, nextTo));
        return page == null
            ? NotFound("The track group revision was not found.")
            : Results.Ok(page);
    }

    private static async Task<IResult> GetAlbumGroups(
        Guid viewId,
        long revision,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? cursor,
        int? limit)
    {
        SearchViewAlbumGroupPageDto? page = coordinator.GetAlbumGroups(viewId, revision, cursor, limit ?? ServerProtocol.DefaultNestedPageSize, cancellationToken);
        return page == null
            ? NotFound("The album-group revision was not found.")
            : Results.Ok(page);
    }

    private static IResult GetAlbumGroupOptions(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? cursor,
        int? limit,
        string? filterText,
        string? previousTo,
        string? nextTo)
    {
        SearchViewAlbumGroupOptionPageDto? page = coordinator.GetAlbumGroupOptions(viewId, groupRef, revision, cursor, limit ?? ServerProtocol.DefaultNestedPageSize, cancellationToken, new(filterText, previousTo, nextTo));
        return page == null
            ? NotFound("The album group revision was not found.")
            : Results.Ok(page);
    }

    private static IResult Get(
        Guid viewId,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        SearchViewSummaryDto? view = coordinator.Get(viewId, cancellationToken);
        return view == null ? NotFound() : Results.Ok(view);
    }

    private static IResult GetDefinition(
        Guid viewId,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        SearchViewDefinitionDto? definition = coordinator.GetDefinition(viewId, cancellationToken);
        return definition == null ? NotFound() : Results.Ok(definition);
    }

    private static IResult GetFiles(
        Guid viewId,
        long revision,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken,
        string? cursor,
        int? limit,
        SearchViewSortOrder? sortOrder)
    {
        SearchViewFilePageDto? page = coordinator.GetFiles(viewId, revision, cursor, limit ?? ServerProtocol.DefaultPageSize, sortOrder, cancellationToken: cancellationToken);
        return page == null ? NotFound("The search view revision was not found.") : Results.Ok(page);
    }

    private static async Task<IResult> GetUpdatesAsync(
        Guid viewId,
        long afterRevision,
        bool? waitForChange,
        SearchViewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        SearchViewUpdateDto? update = await coordinator.GetUpdatesAsync(
            viewId,
            afterRevision,
            waitForChange ?? false,
            cancellationToken).ConfigureAwait(false);
        return update == null ? NotFound() : Results.Ok(update);
    }

    private static IResult Failure(Exception exception)
        => exception switch
        {
            KeyNotFoundException => NotFound(),
            ArgumentException or ArgumentOutOfRangeException or InvalidDataException
                => Results.BadRequest(new ApiErrorDto(
                    exception.Message,
                    "invalid-search-view-request")),
            InvalidOperationException => Results.Json(
                new ApiErrorDto(exception.Message, "search-view-conflict"),
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(
                new ApiErrorDto("Search View failed.", "search-view-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };

    private static IResult NotFound(string message = "The search view was not found.")
        => Results.NotFound(new ApiErrorDto(message, "search-view-not-found"));
}
