using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sldl.Api;

namespace Sldl.Server;

public static class ServerHost
{
    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(consoleOptions =>
        {
            consoleOptions.SingleLine = false;
            consoleOptions.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        if (!string.IsNullOrWhiteSpace(url))
            builder.WebHost.UseUrls(url);

        if (options != null)
            builder.Services.AddSingleton<IOptions<ServerOptions>>(Options.Create(options));
        else
            builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("SldlServer"));

        builder.Services.Configure<JsonOptions>(jsonOptions =>
        {
            jsonOptions.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            SldlApiJson.ConfigureSerializerOptions(jsonOptions.SerializerOptions);
        });

        builder.Services.AddSignalR()
            .AddJsonProtocol(jsonOptions =>
            {
                jsonOptions.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                SldlApiJson.ConfigureSerializerOptions(jsonOptions.PayloadSerializerOptions);
            });
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new()
                {
                    Title = "sldl daemon API",
                    Version = GetInformationalVersion(),
                    Description = "HTTP API for the sldl daemon."
                };

                return Task.CompletedTask;
            });
        });
        builder.Services.AddSingleton<EngineSupervisor>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().StateStore);
        builder.Services.AddSingleton<ServerEventBroadcaster>();
        builder.Services.AddSingleton<ServerProgressLogReporter>();
        builder.Services.AddHostedService<EngineRuntimeHostedService>();

        var app = builder.Build();
        CoreLoggerBridge.Configure(app.Services, (options ?? app.Services.GetRequiredService<IOptions<ServerOptions>>().Value).Engine.LogLevel);
        _ = app.Services.GetRequiredService<ServerEventBroadcaster>();
        _ = app.Services.GetRequiredService<ServerProgressLogReporter>();

        app.MapOpenApi("/api/openapi.json");
        MapEndpoints(app);
        return app;
    }

    private static string GetInformationalVersion()
    {
        var assembly = typeof(ServerHost).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/api/server/info"))
            .ExcludeFromDescription();

        app.MapGet("/api/server/info", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetInfo()))
            .WithTags("Server")
            .WithSummary("Gets server identity and protocol information.")
            .Produces<ServerInfoDto>();
        app.MapGet("/api/server/status", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetStatus()))
            .WithTags("Server")
            .WithSummary("Gets current daemon and Soulseek client status.")
            .Produces<ServerStatusDto>();
        app.MapGet("/api/profiles", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetProfiles()))
            .WithTags("Profiles")
            .WithSummary("Lists configured download profiles.")
            .Produces<IReadOnlyList<ProfileSummaryDto>>();
        app.MapGet("/api/events/catalog", () => Results.Ok(ServerEventCatalog.All))
            .WithTags("Events")
            .WithSummary("Lists SignalR event types and their snapshot invalidation behavior.")
            .Produces<IReadOnlyList<ServerEventDescriptorDto>>();

        app.MapGet("/api/jobs", (
            EngineStateStore stateStore,
            ServerJobState? state,
            ServerJobKind? kind,
            Guid? workflowId,
            bool includeAll = false) =>
        {
            var jobs = stateStore.GetJobs(new JobQuery(state, kind, workflowId, includeAll));
            return Results.Ok(jobs);
        })
            .WithTags("Jobs")
            .WithSummary("Lists known jobs.")
            .WithDescription("Default results contain only execution roots where ParentJobId is null. Set includeAll=true for a flat list of every matching job.")
            .Produces<IReadOnlyList<JobSummaryDto>>();

        app.MapGet("/api/jobs/{jobId:guid}", (Guid jobId, EngineStateStore stateStore) =>
        {
            var detail = stateStore.GetJobDetail(jobId);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}", (Guid workflowId, int displayId, EngineSupervisor supervisor) =>
        {
            var detail = supervisor.GetJobDetailByDisplayId(workflowId, displayId);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by workflow and display id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/raw", (Guid jobId, long afterSequence, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetSearchRawResults(jobId, afterSequence);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Gets raw search responses for a search job.")
            .WithDescription("Use afterSequence to incrementally fetch raw responses after the last seen sequence.")
            .Produces<IReadOnlyList<SearchRawResultDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/files", (Guid jobId, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetFileResults(jobId);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Gets file candidates for a search-like job.")
            .Produces<SearchResultSnapshotDto<FileCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/files/project", (Guid jobId, FileSearchProjectionRequestDto request, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetFileResults(jobId, request);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as file candidates.")
            .Produces<SearchResultSnapshotDto<FileCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/folders", (Guid jobId, bool includeFiles, EngineSupervisor supervisor) =>
        {
            try
            {
                var results = supervisor.GetFolderResults(jobId, includeFiles);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Gets folder candidates for an album search-like job.")
            .WithDescription("Set includeFiles=true only when the client needs selectable files. Folder file counts can come from search results and may not represent a full browse of the remote folder.")
            .Produces<SearchResultSnapshotDto<AlbumFolderDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/folders/project", (Guid jobId, FolderSearchProjectionRequestDto request, EngineSupervisor supervisor) =>
        {
            try
            {
                var results = supervisor.GetFolderResults(jobId, request);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as album folders.")
            .Produces<SearchResultSnapshotDto<AlbumFolderDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/aggregate-tracks", (Guid jobId, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetAggregateTrackResults(jobId);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Gets aggregate track candidates.")
            .Produces<SearchResultSnapshotDto<AggregateTrackCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/aggregate-tracks/project", (Guid jobId, AggregateTrackProjectionRequestDto request, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetAggregateTrackResults(jobId, request);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as aggregate track candidates.")
            .Produces<SearchResultSnapshotDto<AggregateTrackCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/aggregate-albums", (Guid jobId, EngineSupervisor supervisor) =>
        {
            try
            {
                var results = supervisor.GetAggregateAlbumResults(jobId);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Gets aggregate album candidates.")
            .Produces<SearchResultSnapshotDto<AggregateAlbumCandidateDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/aggregate-albums/project", (Guid jobId, AggregateAlbumProjectionRequestDto request, EngineSupervisor supervisor) =>
        {
            try
            {
                var results = supervisor.GetAggregateAlbumResults(jobId, request);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as aggregate album candidates.")
            .Produces<SearchResultSnapshotDto<AggregateAlbumCandidateDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/retrieve-folder", async (
            Guid jobId,
            RetrieveFolderRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartRetrieveFolderAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts a folder retrieval job for a selected album result folder.")
            .WithDescription("Retrieves the full remote folder contents for a selected folder result. Search responses can omit child items that did not match the original query.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/downloads/files", async (
            Guid jobId,
            StartFileDownloadsRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summaries = await supervisor.StartFileDownloadsAsync(jobId, request, ct);
                return summaries != null
                    ? Results.Accepted($"/api/jobs/{jobId}", summaries)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts one or more file download jobs from selected search result files.")
            .WithDescription("The source search job identifies where the candidate refs came from. Per-download settings belong in the request options, not in the original search job.")
            .Produces<IReadOnlyList<JobSummaryDto>>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/downloads/folder", async (
            Guid jobId,
            StartFolderDownloadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartFolderDownloadAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts an album/folder download job from a selected folder result.")
            .WithDescription("The source search job identifies where the folder ref came from. Per-download settings belong in the request options, not in the original search job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/manual/complete", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.CompleteManualSelection(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Completes a manual-selection job without starting additional downloads.")
            .WithDescription("Use this when a DownloadBehavior.Manual job reached AwaitingSelection and the caller wants to close the manual step without resuming the job.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/cancel", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.CancelJob(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Cancels a job when cancellation is available.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}/cancel", (Guid workflowId, int displayId, EngineSupervisor supervisor) =>
        {
            return supervisor.CancelJobByDisplayId(workflowId, displayId)
                ? Results.Accepted($"/api/workflows/{workflowId}")
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Cancels a workflow job by display id.")
            .WithDescription("Convenience endpoint for CLI-style cancellation prompts. Normal GUI clients should prefer AvailableActions on known job ids.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/next-candidate", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.TryNextCandidate(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Tries the next candidate for an active job download.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}/next-candidate", (Guid workflowId, int displayId, EngineSupervisor supervisor) =>
        {
            return supervisor.TryNextCandidateByDisplayId(workflowId, displayId)
                ? Results.Accepted($"/api/workflows/{workflowId}")
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Tries the next candidate for an active job download by display id.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/extract", async (SubmitExtractJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitExtractJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an input extraction job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search", async (SubmitSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a generic Soulseek search job.")
            .WithDescription("Search jobs are discovery-oriented. They store raw Soulseek results; use projection endpoints to view those results as files, album folders, or aggregate candidates, then use follow-up download endpoints for selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/tracks", async (SubmitTrackSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitTrackSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a track search job.")
            .WithDescription("Track search jobs are suitable for exploratory pick-then-download UIs: inspect projected file candidates from the result endpoints, then start follow-up downloads from selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/albums", async (SubmitAlbumSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album search job.")
            .WithDescription("Album search jobs are suitable for exploratory pick-then-download UIs: inspect projected folder candidates from the result endpoints, then start a follow-up folder download from the selected ref.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/song", async (SubmitSongJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitSongJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a single-file download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/album", async (SubmitAlbumJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album/folder download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/aggregate/tracks", async (SubmitAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAggregateJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an aggregate track search job.")
            .WithDescription("Aggregate jobs can download automatically or, with DownloadBehavior.Manual, pause after candidate grouping so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/aggregate/albums", async (SubmitAlbumAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumAggregateJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an aggregate album search job.")
            .WithDescription("Aggregate album jobs can download automatically or, with DownloadBehavior.Manual, pause after bucket projection so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/lists", async (SubmitJobListRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitJobListAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a job list from draft child jobs.")
            .WithDescription("Job drafts are submission payloads only. Submitted children appear as normal runtime jobs in subsequent job/workflow snapshots.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/workflows", (EngineStateStore stateStore) => Results.Ok(stateStore.GetWorkflows()))
            .WithTags("Workflows")
            .WithSummary("Lists known workflows.")
            .Produces<IReadOnlyList<WorkflowSummaryDto>>();

        app.MapGet("/api/workflows/{workflowId:guid}", (Guid workflowId, bool? includeAll, EngineStateStore stateStore) =>
        {
            var workflow = stateStore.GetWorkflow(workflowId, includeAll == true);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Gets a workflow snapshot by id.")
            .WithDescription("Default results contain only execution roots where ParentJobId is null. Set includeAll=true for a flat list of every workflow job. Use /tree for the same jobs grouped by ParentJobId.")
            .Produces<WorkflowDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/workflows/{workflowId:guid}/tree", (Guid workflowId, EngineStateStore stateStore) =>
        {
            var workflow = stateStore.GetWorkflowTree(workflowId);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Gets the execution tree for a workflow.")
            .WithDescription("This tree is built only from ParentJobId relationships. Follow-up jobs started from search results remain workflow roots and expose SourceJobId instead.")
            .Produces<WorkflowTreeDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/workflows/{workflowId:guid}/cancel", (Guid workflowId, EngineSupervisor supervisor) =>
        {
            int cancelled = supervisor.CancelWorkflow(workflowId);
            return cancelled > 0
                ? Results.Accepted($"/api/workflows/{workflowId}", new CancelWorkflowResponseDto(cancelled))
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Cancels cancellable jobs in a workflow.")
            .Produces<CancelWorkflowResponseDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapHub<ServerEventHub>("/api/events");
    }

    private static async Task<IResult> SubmitJobAsync(Func<Task<JobSummaryDto>> submit)
    {
        try
        {
            var summary = await submit();
            return Results.Accepted($"/api/jobs/{summary.JobId}", summary);
        }
        catch (Exception ex) when (TryCreateBadRequest(ex, out _))
        {
            return BadRequest(ex);
        }
    }

    private static IResult BadRequest(Exception ex)
    {
        TryCreateBadRequest(ex, out var error);
        Sldl.Core.Logger.LogNonConsole(Sldl.Core.Logger.LogLevel.Warn, $"Bad request: {error}");
        return Results.BadRequest(new ApiErrorDto(error));
    }

    private static bool TryCreateBadRequest(Exception ex, out string error)
    {
        error = ex.Message;
        return ex is ArgumentException
            || ex.Message.StartsWith("Input error:", StringComparison.Ordinal);
    }
}
