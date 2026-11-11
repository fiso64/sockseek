using Sockseek.Api;
using Sockseek.Server.Persistence;
using static Sockseek.Server.ApiErrors;
using Sockseek.Server.Planning;

namespace Sockseek.Server;

internal static class JobEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/workflows/{workflowId:guid}/snapshot", (
            Guid workflowId,
            EngineStateStore stateStore) =>
                Results.Ok(stateStore.GetWorkflowSnapshot(workflowId)))
            .WithTags("Live State")
            .WithSummary("Gets one complete workflow replication snapshot and its workflow-local stream position.")
            .Produces<StateSnapshotDto>();

        app.MapGet("/api/profiles", (JobSubmissions submissions) => Results.Ok(submissions.GetProfiles()))
            .WithTags("Profiles")
            .WithSummary("Lists configured download profiles.")
            .Produces<IReadOnlyList<ProfileSummaryDto>>();

        app.MapGet("/api/submissions", async (
            JobReadService jobs,
            HttpContext httpContext,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await jobs.GetSubmissionsAsync(
                    cursor,
                    limit ?? ServerProtocol.DefaultPageSize,
                    cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Submissions")
            .WithSummary("Lists durable accepted submissions newest first.")
            .WithDescription("Traverse runtime jobs with /api/jobs?submissionId= rather than embedding them here.")
            .Produces<IReadOnlyList<SubmissionSummaryDto>>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/submissions/{submissionId:guid}", async (
            Guid submissionId,
            JobReadService jobs,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await jobs.GetSubmissionAsync(submissionId, cancellationToken);
                return detail == null ? Results.NotFound() : Results.Ok(detail);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .WithTags("Submissions")
            .WithSummary("Gets safe durable submission metadata.")
            .Produces<SubmissionDetailDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapDelete("/api/submissions/{submissionId:guid}", async (
            Guid submissionId,
            JobReadService jobs,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await jobs.DeleteSubmissionAsync(
                    submissionId,
                    cancellationToken));
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .WithTags("Submissions")
            .WithSummary("Permanently deletes one terminal submission and its jobs.")
            .WithDescription("Downloaded files, transfers and their statistics are preserved. Nonterminal submissions are rejected.")
            .Produces<DeleteSubmissionResponseDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/submissions/{submissionId:guid}/rerun", async (
            Guid submissionId,
            JobSubmissions submissions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var submitted = await submissions.RerunSubmissionAsync(
                    submissionId,
                    cancellationToken);
                return submitted == null
                    ? Results.NotFound()
                    : Results.Accepted($"/api/submissions/{submitted.SubmissionId}", submitted);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Submissions")
            .WithSummary("Reruns a submission from its retained normalized command and effective settings.")
            .WithDescription("Current credentials are rebound only for retained credential slots. Current defaults and profiles do not replace the retained root settings.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/jobs/{jobId:guid}/rerun-search", async (
            Guid jobId,
            JobSubmissions submissions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                JobSummaryDto? submitted = await submissions.RerunSearchJobAsync(
                    jobId, cancellationToken);
                return submitted == null
                    ? Results.NotFound()
                    : Results.Accepted($"/api/jobs/{submitted.JobId}", submitted);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Job Submission")
            .WithSummary("Replays only one planned search job from its retained execution settings.")
            .WithDescription("Sibling jobs are not restarted. Older submissions without retained child identities cannot be replayed through this operation.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/jobs", async (
            JobReadService jobs,
            HttpContext httpContext,
            ServerJobLifecycleState? lifecycleState,
            ServerJobTerminalOutcome? terminalOutcome,
            ServerJobSkipReason? skipReason,
            string? kind,
            Guid? workflowId,
            Guid? parentJobId,
            Guid? submissionId,
            string? role,
            bool? includeAll,
            bool? newestFirst,
            bool? excludeExtractResults,
            [Microsoft.AspNetCore.Mvc.FromQuery] string[]? excludeKinds,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                ServerJobKind? parsedKind = null;
                if (kind != null)
                {
                    if (!ServerProtocolEnumExtensions.TryParseJobKind(kind, out var value))
                        throw new ArgumentException($"Unknown job kind '{kind}'.", nameof(kind));
                    parsedKind = value;
                }
                var parsedExcludeKinds = excludeKinds?.Select(kind =>
                    ServerProtocolEnumExtensions.TryParseJobKind(kind, out var value) ? value
                        : throw new ArgumentException($"Unknown job kind '{kind}'.", nameof(excludeKinds))).ToArray();
                ServerJobRole? parsedRole = null;
                if (role != null)
                {
                    if (!ServerProtocolEnumExtensions.TryParseJobRole(role, out var value))
                        throw new ArgumentException($"Unknown job role '{role}'.", nameof(role));
                    parsedRole = value;
                }
                var page = await jobs.GetJobsAsync(
                    new JobQuery(
                        lifecycleState,
                        terminalOutcome,
                        parsedKind,
                        workflowId,
                        includeAll ?? false,
                        skipReason,
                        parentJobId,
                        submissionId,
                        parsedRole,
                            newestFirst ?? false,
                        excludeExtractResults ?? false,
                        parsedExcludeKinds),
                    cursor,
                    limit ?? ServerProtocol.DefaultNestedPageSize,
                    cancellationToken);
                httpContext.Response.Headers[ServerProtocol.ReadEpochHeader] = page.ReadPosition.Epoch.ToString("D");
                httpContext.Response.Headers[ServerProtocol.ReadSequenceHeader] =
                    page.ReadPosition.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Jobs")
            .WithSummary("Lists known jobs.")
            .WithDescription("Default results contain execution roots in ascending display order. Set newestFirst=true for newest-first moving-keyset pages; cursors are bound to that direction. Set parentJobId for direct children, submissionId or role for submission semantics, or includeAll=true for a flat workflow list. Set excludeExtractResults=true to page extraction sources without also counting their generated results.")
            .Produces<IReadOnlyList<JobSummaryDto>>();

        app.MapPost("/api/jobs/snapshot", async (
            JobListScopeDto scope, JobReadService jobs, ServerEventBroadcaster events, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await jobs.GetJobListSnapshotAsync(scope, events, cancellationToken)); }
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
        })
            .WithTags("Jobs")
            .WithSummary("Gets the same displayed job-list snapshot used by live observations.")
            .Produces<JobListUpdateDto>();

        app.MapPost("/api/jobs/lookup", async (
            JobLookupRequestDto request, JobReadService jobs, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await jobs.LookupJobsAsync(request, cancellationToken)); }
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
        })
            .WithTags("Jobs")
            .WithSummary("Reads current live or retained job summaries by ID.")
            .WithDescription("Returns found IDs once in request order; missing IDs are omitted. Does not hydrate job details or count children.")
            .Produces<IReadOnlyList<JobSummaryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/jobs/{jobId:guid}", async (Guid jobId, JobReadService jobs, CancellationToken cancellationToken) =>
        {
            var detail = await jobs.GetJobAsync(jobId, cancellationToken);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/directory-files", async (
            Guid jobId, int? attemptNumber, int? afterOrdinal, int? limit,
            JobReadService jobs, CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await jobs.GetDirectoryManifestAsync(
                    jobId, attemptNumber, afterOrdinal ?? -1, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return page == null ? Results.NotFound() : Results.Ok(page);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Jobs")
            .WithSummary("Pages exact files in the accepted attempt of an album or remote-directory job.")
            .Produces<DirectoryManifestPageDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}", async (
            Guid workflowId, int displayId, JobReadService jobs, CancellationToken cancellationToken) =>
        {
            var detail = await jobs.GetJobByDisplayIdAsync(workflowId, displayId, cancellationToken);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by workflow and display id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/raw", async (
            Guid jobId,
            long afterSequence,
            int? limit,
            JobReadService jobs,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await jobs.GetRawSearchResultsAsync(jobId, afterSequence, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                if (page?.NextSequence != null)
                    httpContext.Response.Headers["X-Next-Sequence"] = page.NextSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return page != null
                    ? Results.Ok(page.Items)
                    : Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"));
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Gets raw search responses for a search job.")
            .WithDescription("Use afterSequence to incrementally fetch raw responses after the last seen sequence.")
            .Produces<IReadOnlyList<SearchRawResultDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

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
            catch (Exception ex) when (IsInvalidRequest(ex))
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
            catch (Exception ex) when (IsInvalidRequest(ex))
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
            catch (Exception ex) when (IsInvalidRequest(ex))
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

        app.MapPost("/api/jobs/{jobId:guid}/manual/complete", async (Guid jobId, EngineSupervisor supervisor) =>
        {
            return await supervisor.CompleteManualSelectionAsync(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Completes a manual-selection job without starting additional downloads.")
            .WithDescription("Use this when a DownloadBehavior.Manual job reached AwaitingSelection and the caller wants to close the manual step without resuming the job.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/manual/skip", async (Guid jobId, EngineSupervisor supervisor) =>
        {
            return await supervisor.SkipManualSelectionAsync(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Skips a manual-selection job without starting additional downloads.")
            .WithDescription("Use this when a DownloadBehavior.Manual job reached AwaitingSelection and the caller wants to record an explicit user skip.")
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

        app.MapPost("/api/jobs/cancel-all", (EngineSupervisor supervisor) =>
        {
            int cancelled = supervisor.CancelAllJobs();
            return Results.Accepted(
                "/api/jobs",
                new CancelJobsResponseDto(cancelled));
        })
            .WithTags("Jobs")
            .WithSummary("Cancels all currently cancellable daemon jobs.")
            .WithDescription("The daemon remains running and can accept later submissions.")
            .Produces<CancelJobsResponseDto>(StatusCodes.Status202Accepted);

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

        app.MapPost("/api/jobs/extract", async (SubmitExtractJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitExtractJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an input extraction job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/effective-settings", async (
            ResolveEffectiveSettingsRequestDto request,
            JobSubmissions submissions,
            JobPreviewCoordinator previews,
            CancellationToken ct) =>
        {
            try
            {
                JobDraftDto resolved = await previews.ResolveArtifactDraftForSettingsAsync(
                    request.Job, ct).ConfigureAwait(false);
                return Results.Ok(submissions.ResolveEffectiveSettings(request with { Job = resolved }));
            }
            catch (InputArtifactUnavailableException ex)
            {
                return Results.Json(
                    new ApiErrorDto(ex.Message, "artifact-unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Job Submission")
            .WithSummary("Resolves the effective settings for a job draft without submitting it.")
            .WithDescription("Uses the same typed settings composer as direct Start. An extract or generic-search draft may omit its not-yet-entered input for provisional settings; actual submission still validates it. Secret and command values are redacted; matched profiles and safe per-field provenance are returned for Review and diagnostics.")
            .Produces<ResolveEffectiveSettingsResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/jobs/search", async (SubmitSearchJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a generic Soulseek search job.")
            .WithDescription("Search jobs are discovery-oriented. They store raw Soulseek results; use projection endpoints to view those results as files, album folders, or group candidates, then use follow-up download endpoints for selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/tracks", async (SubmitTrackSearchJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitTrackSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a track search job.")
            .WithDescription("Track search jobs are suitable for exploratory pick-then-download UIs: inspect projected file candidates from the result endpoints, then start follow-up downloads from selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/albums", async (SubmitAlbumSearchJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitAlbumSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album search job.")
            .WithDescription("Album search jobs are suitable for exploratory pick-then-download clients: create an immutable Search View, page its directory projection, then commit revision-bound refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/song", async (SubmitSongJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitSongJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a single-file download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/album", async (SubmitAlbumJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitAlbumJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album/folder download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/groups/tracks", async (SubmitSongGroupsJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitSongGroupsJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a grouped track search job.")
            .WithDescription("Grouped-song jobs can download automatically or, with DownloadBehavior.Manual, pause after candidate grouping so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/groups/albums", async (SubmitAlbumGroupsJobRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitAlbumGroupsJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a grouped album search job.")
            .WithDescription("Grouped album jobs can download automatically or, with DownloadBehavior.Manual, pause after bucket projection so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/lists", async (SubmitJobListRequestDto request, JobSubmissions submissions, CancellationToken ct) =>
            await SubmitJobAsync(() => submissions.SubmitJobListAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a job list from draft child jobs.")
            .WithDescription("Job drafts are submission payloads only. Submitted children appear as normal runtime jobs in subsequent job/workflow snapshots.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/workflows", async (
            string? cursor,
            int? limit,
            JobReadService jobs,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await jobs.GetWorkflowsAsync(cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (Exception ex) when (IsInvalidRequest(ex))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Workflows")
            .WithSummary("Lists known workflows.")
            .Produces<IReadOnlyList<WorkflowSummaryDto>>();

        app.MapGet("/api/workflows/{workflowId:guid}", async (
            Guid workflowId, JobReadService jobs, CancellationToken cancellationToken) =>
        {
            var workflow = await jobs.GetWorkflowAsync(workflowId, cancellationToken);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Gets a workflow snapshot by id.")
            .WithDescription("Use the cursor-paged jobs collection to list workflow roots or descendants.")
            .Produces<WorkflowDetailDto>()
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
    }

    private static async Task<IResult> SubmitJobAsync(Func<Task<JobSummaryDto>> submit)
    {
        try
        {
            var summary = await submit();
            return Results.Accepted($"/api/jobs/{summary.JobId}", summary);
        }
        catch (Exception ex) when (IsInvalidRequest(ex))
        {
            return BadRequest(ex);
        }
    }

}
