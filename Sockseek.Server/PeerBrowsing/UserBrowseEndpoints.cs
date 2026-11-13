using Sockseek.Api;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Services;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.PeerBrowsing;

internal static class UserBrowseEndpoints
{
    private const int MaximumQueryLength = 256;

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/user-browses/download-settings", (SubmissionOptionsDto options, JobSubmissions submissions) =>
            Results.Ok(submissions.ResolveUserShareDownloadSettings(options)))
            .WithTags("User Browsing")
            .WithSummary("Resolves settings for ordinary downloads from peer shares.")
            .Produces<ResolveEffectiveSettingsResponseDto>();
        app.MapPost("/api/users/{username}/browses", StartAsync)
            .WithTags("User Browsing")
            .WithSummary("Starts, joins, or reuses a remote user's share browse.")
            .Produces<UserBrowseDto>(StatusCodes.Status200OK)
            .Produces<UserBrowseDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/user-browses", ListAsync)
            .WithTags("User Browsing")
            .WithSummary("Lists recent remote-user browse resources.")
            .Produces<PageDto<UserBrowseDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/user-browses/{browseId:guid}", GetAsync)
            .WithTags("User Browsing")
            .WithSummary("Gets one remote-user browse resource.")
            .Produces<UserBrowseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/user-browses/{browseId:guid}/snapshot", SnapshotAsync)
            .WithTags("Live State")
            .WithSummary("Gets one user-browse replication snapshot and stream position.")
            .Produces<StateSnapshotDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/user-browses/{browseId:guid}/cancel", CancelAsync)
            .WithTags("User Browsing")
            .WithSummary("Globally cancels a shared browse acquisition.")
            .Produces<UserBrowseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/user-browses/{browseId:guid}/directories", DirectoriesAsync)
            .WithTags("User Browsing")
            .WithSummary("Gets a stable page of share directories.")
            .Produces<PageDto<BrowseDirectoryEntryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status500InternalServerError)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/user-browses/{browseId:guid}/directories/{directoryId:long}", DirectoryAsync)
            .WithTags("User Browsing")
            .WithSummary("Gets one share directory.")
            .Produces<BrowseDirectoryEntryDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status500InternalServerError)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/user-browses/{browseId:guid}/directories/{directoryId:long}/files", FilesAsync)
            .WithTags("User Browsing")
            .WithSummary("Gets a stable page of files in one share directory.")
            .Produces<PageDto<BrowseFileEntryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status500InternalServerError)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/user-browses/{browseId:guid}/search", SearchAsync)
            .WithTags("User Browsing")
            .WithSummary("Searches one immutable share artifact as flat directory/file rows.")
            .Produces<BrowseSearchPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status500InternalServerError)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/user-browses/{browseId:guid}/downloads", StartDownloadsAsync)
            .WithTags("User Browsing")
            .WithSummary("Starts ordinary remote downloads from a browse selection.")
            .Produces<StartUserShareDownloadsResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status410Gone)
            .Produces<ApiErrorDto>(StatusCodes.Status502BadGateway)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> StartAsync(
        string username,
        StartUserBrowseRequestDto request,
        DaemonRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
        {
            ServerLogMessages.FeatureRequestUnavailable(
                loggerFactory.CreateLogger("Sockseek.Server.PeerBrowsing.UserBrowseEndpoints"),
                "peer browsing");
            return Unavailable();
        }
        try
        {
            PeerBrowseResource resource = await service.StartAsync(
                username, request.Refresh, cancellationToken).ConfigureAwait(false);
            UserBrowseDto dto = UserBrowseDtoMapper.ToDto(resource);
            return resource.State == PeerBrowseState.Complete
                ? Results.Ok(dto)
                : Results.Accepted($"/api/user-browses/{resource.BrowseId}", dto);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StartFailure(exception);
        }
    }

    private static async Task<IResult> ListAsync(
        string? username,
        string? state,
        string? cursor,
        int? limit,
        DaemonRuntime runtime,
        PeerBrowseCursorCodec cursors,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        try
        {
            int pageSize = PageSize(limit, ServerProtocol.DefaultPageSize);
            UserBrowseState? dtoState = ParseState(state);
            PeerBrowseState? coreState = dtoState is null ? null : ToCore(dtoState.Value);
            PeerBrowseResourceCursor? after = cursor is null
                ? null
                : cursors.DecodeResources(cursor, username, dtoState);
            PeerBrowseResourcePage page = await service.ListAsync(
                username,
                coreState,
                after?.CreatedAt,
                after?.BrowseId,
                pageSize,
                cancellationToken).ConfigureAwait(false);
            string? nextCursor = page.NextBrowseId is { } nextBrowseId
                ? cursors.EncodeResources(username, dtoState, page.NextCreatedAt!.Value, nextBrowseId)
                : null;
            return Results.Ok(new PageDto<UserBrowseDto>(page.Items.Select(UserBrowseDtoMapper.ToDto).ToArray(), nextCursor));
        }
        catch (ArgumentException exception)
        {
            return exception.ParamName == "cursor"
                ? InvalidCursor()
                : InvalidRequest(exception.Message);
        }
        catch (InvalidOperationException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> GetAsync(
        Guid browseId,
        DaemonRuntime runtime,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        try
        {
            PeerBrowseResource? resource = await service.GetAccessibleAsync(browseId, cancellationToken).ConfigureAwait(false);
            return resource is null ? Expired() : Results.Ok(UserBrowseDtoMapper.ToDto(resource));
        }
        catch (InvalidOperationException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> CancelAsync(
        Guid browseId,
        DaemonRuntime runtime,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        PeerBrowseResource? resource = await service.CancelAsync(
            browseId, cancellationToken).ConfigureAwait(false);
        return resource is null ? Expired() : Results.Ok(UserBrowseDtoMapper.ToDto(resource));
    }

    private static async Task<IResult> SnapshotAsync(
        Guid browseId,
        DaemonRuntime runtime,
        EngineStateStore stateStore,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        PeerBrowseResource? resource = await service.GetAccessibleAsync(
            browseId, cancellationToken).ConfigureAwait(false);
        return resource is null
            ? Expired()
            : Results.Ok(stateStore.GetUserBrowseSnapshot(UserBrowseDtoMapper.ToDto(resource)));
    }

    private static async Task<IResult> DirectoriesAsync(
        Guid browseId,
        long? parentId,
        string? query,
        bool recursive,
        string? cursor,
        int? limit,
        DaemonRuntime runtime,
        PeerBrowseCursorCodec cursors,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        try
        {
            ValidateQuery(query);
            int pageSize = PageSize(limit, ServerProtocol.DefaultNestedPageSize);
            long? afterId = cursor is null
                ? null
                : cursors.DecodeRows(
                    cursor,
                    PeerBrowseCursorKind.Directories,
                    browseId,
                    parentId,
                    recursive,
                    query);
            string? afterSortKey = null;
            if (afterId is { } directoryId)
            {
                PeerBrowseDirectoryEntry? entry = await service.ReadDirectoryEntryAsync(
                    browseId, directoryId, cancellationToken).ConfigureAwait(false);
                if (entry is null)
                    return InvalidCursor();
                afterSortKey = entry.DisplayPath;
            }
            PeerBrowsePage<PeerBrowseDirectoryEntry> page = await service.ReadDirectoriesAsync(
                browseId,
                parentId,
                query,
                recursive,
                afterSortKey,
                afterId,
                pageSize,
                cancellationToken).ConfigureAwait(false);
            string? nextCursor = page.NextId is { } nextId
                ? cursors.EncodeRows(
                    PeerBrowseCursorKind.Directories, browseId, parentId, recursive, query, nextId)
                : null;
            return Results.Ok(new PageDto<BrowseDirectoryEntryDto>(
                page.Items.Select(UserBrowseDtoMapper.ToDto).ToArray(), nextCursor));
        }
        catch (Exception exception)
        {
            return ReadFailure(exception);
        }
    }

    private static async Task<IResult> DirectoryAsync(
        Guid browseId,
        long directoryId,
        DaemonRuntime runtime,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        try
        {
            PeerBrowseDirectoryEntry? entry = await service.ReadDirectoryEntryAsync(
                browseId, directoryId, cancellationToken).ConfigureAwait(false);
            return entry is null
                ? Results.NotFound(new ApiErrorDto("The share directory was not found.", "directory-not-found"))
                : Results.Ok(UserBrowseDtoMapper.ToDto(entry));
        }
        catch (Exception exception)
        {
            return ReadFailure(exception);
        }
    }

    private static async Task<IResult> FilesAsync(
        Guid browseId,
        long directoryId,
        string? query,
        string? cursor,
        int? limit,
        DaemonRuntime runtime,
        PeerBrowseCursorCodec cursors,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        try
        {
            ValidateQuery(query);
            int pageSize = PageSize(limit, ServerProtocol.DefaultPageSize);
            long? afterId = cursor is null
                ? null
                : cursors.DecodeRows(
                    cursor,
                    PeerBrowseCursorKind.Files,
                    browseId,
                    directoryId,
                    false,
                    query);
            string? afterSortKey = null;
            if (afterId is { } fileId)
            {
                PeerBrowseFileEntry? entry = await service.ReadFileEntryAsync(
                    browseId, fileId, cancellationToken).ConfigureAwait(false);
                if (entry is null || entry.DirectoryId != directoryId)
                    return InvalidCursor();
                afterSortKey = entry.Name;
            }
            PeerBrowsePage<PeerBrowseFileEntry> page = await service.ReadFilesAsync(
                browseId,
                directoryId,
                query,
                afterSortKey,
                afterId,
                pageSize,
                cancellationToken).ConfigureAwait(false);
            string? nextCursor = page.NextId is { } nextId
                ? cursors.EncodeRows(
                    PeerBrowseCursorKind.Files, browseId, directoryId, false, query, nextId)
                : null;
            return Results.Ok(new PageDto<BrowseFileEntryDto>(page.Items.Select(UserBrowseDtoMapper.ToDto).ToArray(), nextCursor));
        }
        catch (Exception exception)
        {
            return ReadFailure(exception);
        }
    }

    private static async Task<IResult> SearchAsync(
        Guid browseId,
        string query,
        string? cursor,
        int? limit,
        DaemonRuntime runtime,
        PeerBrowseCursorCodec cursors,
        CancellationToken cancellationToken)
    {
        PeerBrowseService? service = runtime.PeerBrowses;
        if (service is null)
            return Unavailable();
        try
        {
            string normalizedQuery = NormalizeSearchQuery(query);
            int pageSize = PageSize(limit, ServerProtocol.DefaultNestedPageSize);
            PeerBrowseResource? resource = await service.GetAccessibleAsync(
                browseId, cancellationToken).ConfigureAwait(false);
            if (resource is null)
                return Expired();
            if (resource.State != PeerBrowseState.Complete)
                throw new PeerBrowseNotReadyException(resource);

            PeerBrowseSearchCursor? after = cursor is null
                ? null
                : cursors.DecodeSearch(
                    cursor,
                    browseId,
                    resource.Revision,
                    normalizedQuery);
            string? afterSortKey = null;
            if (after is not null)
            {
                afterSortKey = await service.ReadSearchSortKeyAsync(
                    browseId,
                    after.Kind,
                    after.EntryId,
                    cancellationToken).ConfigureAwait(false);
                if (afterSortKey is null)
                    return InvalidCursor();
            }

            PeerBrowseSearchPage page = await service.SearchAsync(
                browseId,
                normalizedQuery,
                afterSortKey,
                after?.Kind,
                after?.EntryId,
                pageSize,
                cancellationToken).ConfigureAwait(false);
            string? nextCursor = page.NextId is { } nextId
                ? cursors.EncodeSearch(
                    browseId,
                    resource.Revision,
                    normalizedQuery,
                    page.NextKind!.Value,
                    nextId)
                : null;
            return Results.Ok(new BrowseSearchPageDto(
                browseId,
                resource.Revision,
                normalizedQuery,
                page.Items.Select(UserBrowseDtoMapper.ToDto).ToArray(),
                page.PublicMatchingFileCount,
                page.PublicMatchingBytes,
                page.LockedMatchingFileCount,
                page.LockedMatchingBytes,
                nextCursor));
        }
        catch (Exception exception)
        {
            return ReadFailure(exception);
        }
    }

    private static async Task<IResult> StartDownloadsAsync(
        Guid browseId,
        StartUserShareDownloadsRequestDto request,
        DaemonRuntime runtime,
        JobSubmissions submissions,
        SubmissionCommitCoordinator commits,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateShareOptions(request.Options);
            if (request.RequestId == Guid.Empty)
                throw new PeerBrowseSelectionException("RequestId cannot be empty.");
            string fingerprint = Fingerprint(browseId, request);
            StartUserShareDownloadsResponseDto response = await commits.ExecuteAsync(
                request.RequestId,
                fingerprint,
                async operationToken =>
                {
                    (PeerBrowseResource resource, PeerBrowseDownloadResolution resolution) =
                        await ResolveSelectionAsync(
                            browseId,
                            request.Selections,
                            runtime,
                            operationToken,
                            request.ExcludedFileIds,
                            request.ExcludedDirectoryIds).ConfigureAwait(false);
                    return await submissions.SubmitUserShareDownloadsAsync(
                        resource.Username, resolution, request.Options,
                        request.RequestId, fingerprint, operationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (Exception exception)
        {
            return DownloadFailure(exception);
        }
    }

    internal static string Fingerprint(Guid browseId, StartUserShareDownloadsRequestDto request)
        => SubmissionFingerprint.Create("peer-share", new
        {
            BrowseId = browseId,
            Request = request with
            {
                RequestId = Guid.Empty,
                Options = SubmissionFingerprint.Options(request.Options),
                ExcludedFileIds = request.ExcludedFileIds?.Distinct().Order().ToArray() ?? [],
                ExcludedDirectoryIds = request.ExcludedDirectoryIds?.Distinct().Order().ToArray() ?? [],
            },
        });

    private static async Task<(PeerBrowseResource Resource, PeerBrowseDownloadResolution Resolution)>
        ResolveSelectionAsync(
            Guid browseId,
            IReadOnlyList<UserShareSelectionDto> selections,
            DaemonRuntime runtime,
            CancellationToken cancellationToken,
            IReadOnlyList<long>? excludedFileIds,
            IReadOnlyList<long>? excludedDirectoryIds)
    {
        if (selections is null || selections.Count == 0)
            throw new PeerBrowseSelectionException("At least one folder or file must be selected.");
        PeerBrowseService service = runtime.PeerBrowses
            ?? throw new InvalidOperationException("Soulseek browsing is unavailable.");
        PeerBrowseResource? resource = await service.GetAccessibleAsync(
            browseId,
            cancellationToken).ConfigureAwait(false);
        if (resource is null)
            throw new KeyNotFoundException("The browse has expired.");

        var directoryIds = new List<long>();
        var fileIds = new List<long>();
        foreach (UserShareSelectionDto? selection in selections)
        {
            switch (selection)
            {
                case UserShareDirectorySelectionDto directory:
                    directoryIds.Add(directory.DirectoryId);
                    break;
                case UserShareFileSelectionDto file:
                    fileIds.Add(file.FileId);
                    break;
                default:
                    throw new PeerBrowseSelectionException("The share selection kind is invalid.");
            }
        }
        PeerBrowseDownloadResolution resolution = await service.ResolveDownloadSelectionAsync(
            browseId,
            directoryIds,
            fileIds,
            cancellationToken,
            excludedFileIds,
            excludedDirectoryIds).ConfigureAwait(false);
        return (resource, resolution);
    }

    private static UserShareResolutionSummaryDto ToSummary(
        PeerBrowseDownloadResolution resolution,
        string outputParent)
        => new(
            resolution.CanonicalDirectoryRoots,
            resolution.StandaloneFiles,
            resolution.TotalPublicFiles,
            resolution.TotalPublicBytes,
            resolution.RedundantSelectionsRemoved,
            resolution.LockedBranchesSkipped,
            outputParent);

    private static void ValidateShareOptions(SubmissionOptionsDto? options)
    {
        if (options?.WorkflowId is not null)
        {
            throw new ArgumentException(
                "WorkflowId does not apply to a browse download submission.",
                nameof(options));
        }
    }

    private static IResult DownloadFailure(Exception exception)
        => exception switch
        {
            PeerBrowseNotReadyException => Results.Conflict(
                new ApiErrorDto("The peer browse is not complete.", "browse-not-ready")),
            KeyNotFoundException => Expired(),
            IdempotencyConflictException => Results.Conflict(
                new ApiErrorDto(
                    "RequestId was already used for a different submission.",
                    "idempotency-conflict")),
            UnsupportedNameFormatVariableException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "invalid-name-format-variable")),
            PeerBrowseSelectionException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "invalid-selection")),
            ArgumentException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "invalid-remote-transfer-option")),
            InvalidOperationException => Unavailable(),
            _ => Results.Json(
                new ApiErrorDto("The share selection could not be resolved.", "peer-response-invalid"),
                statusCode: StatusCodes.Status502BadGateway),
        };

    private static int PageSize(int? limit, int defaultValue)
    {
        int value = limit ?? defaultValue;
        if (value < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        return value;
    }

    private static void ValidateQuery(string? query)
    {
        if (query?.Length > MaximumQueryLength)
            throw new ArgumentException($"The browse query cannot exceed {MaximumQueryLength} characters.", nameof(query));
    }

    private static string NormalizeSearchQuery(string? query)
    {
        ValidateQuery(query);
        string normalized = query?.Trim() ?? "";
        if (normalized.Length == 0)
            throw new ArgumentException("The browse search query cannot be empty.", nameof(query));
        return normalized;
    }

    private static UserBrowseState? ParseState(string? state)
        => state switch
        {
            null => null,
            "queued" => UserBrowseState.Queued,
            "running" => UserBrowseState.Running,
            "complete" => UserBrowseState.Complete,
            "failed" => UserBrowseState.Failed,
            "cancelled" => UserBrowseState.Cancelled,
            _ => throw new ArgumentException("The browse state filter is invalid.", nameof(state)),
        };

    private static PeerBrowseState ToCore(UserBrowseState state) => (PeerBrowseState)state;

    private static IResult StartFailure(Exception exception)
        => exception switch
        {
            ArgumentException => Results.BadRequest(new ApiErrorDto(exception.Message, "invalid-username")),
            InvalidOperationException => Unavailable(),
            _ => Results.Json(
                new ApiErrorDto("The peer browse could not be started.", "soulseek-unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };

    private static IResult ReadFailure(Exception exception)
        => exception switch
        {
            PeerBrowseNotReadyException => Results.Conflict(
                new ApiErrorDto("The peer browse is not complete.", "browse-not-ready")),
            KeyNotFoundException => Expired(),
            ArgumentException argument when argument.ParamName == "cursor" => InvalidCursor(),
            ArgumentException => InvalidRequest(exception.Message),
            InvalidOperationException => Unavailable(),
            _ => Results.Json(
                new ApiErrorDto("The browse artifact could not be read.", "browse-read-failed"),
                statusCode: StatusCodes.Status500InternalServerError),
        };

    private static IResult InvalidRequest(string message)
        => Results.BadRequest(new ApiErrorDto(message, "invalid-request"));

    private static IResult InvalidCursor()
        => Results.BadRequest(new ApiErrorDto("The browse cursor is invalid.", "invalid-cursor"));

    private static IResult Expired()
        => Results.Json(
            new ApiErrorDto("The peer browse has expired.", "browse-expired"),
            statusCode: StatusCodes.Status410Gone);

    private static IResult Unavailable()
        => Results.Json(
            new ApiErrorDto("Soulseek browsing is unavailable.", "soulseek-unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
