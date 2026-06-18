using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sockseek.Api;

/// <summary>Exception raised for daemon HTTP responses that intentionally return an API error body.</summary>
public sealed class SockseekApiRequestException : InvalidOperationException
{
    public SockseekApiRequestException(string message)
        : base(message)
    {
    }
}

/// <summary>Reusable HTTP client for the daemon API. SignalR clients should pair this with the event DTOs and payload converter in this project.</summary>
public sealed class SockseekApiClient
{
    private readonly HttpClient http;
    private readonly JsonSerializerOptions jsonOptions;

    public SockseekApiClient(HttpClient http, JsonSerializerOptions? jsonOptions = null)
    {
        this.http = http;
        this.jsonOptions = jsonOptions ?? SockseekApiJson.CreateSerializerOptions();
    }

    /// <summary>Normalizes user-entered daemon URLs and applies the default daemon port when none is specified.</summary>
    public static Uri NormalizeServerUrl(string serverUrl)
    {
        var value = serverUrl.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;

        var builder = new UriBuilder(value);
        if (builder.Uri.IsDefaultPort)
            builder.Port = 5030;

        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";

        return builder.Uri;
    }

    /// <summary>Creates an <see cref="HttpClient"/> with a normalized daemon base address.</summary>
    public static HttpClient CreateHttpClient(string serverUrl)
        => new() { BaseAddress = NormalizeServerUrl(serverUrl) };

    public async Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/extract", request, ct);

    /// <summary>Submits a generic search job. Use projection methods to view the same raw results as files, folders, aggregate tracks, or aggregate albums.</summary>
    public async Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/search", request, ct);

    /// <summary>Submits a typed track search job. The default file result endpoint can infer its projection from the stored track query.</summary>
    public async Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/search/tracks", request, ct);

    /// <summary>Submits a typed album search job. The default folder result endpoint can infer its projection from the stored album query.</summary>
    public async Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/search/albums", request, ct);

    public async Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/downloads/song", request, ct);

    public async Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/downloads/album", request, ct);

    public async Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/aggregate/tracks", request, ct);

    public async Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/aggregate/albums", request, ct);

    public async Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/lists", request, ct);

    /// <summary>Returns available daemon profiles.</summary>
    public async Task<IReadOnlyList<ProfileSummaryDto>> GetProfilesAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/profiles", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ProfileSummaryDto>>(jsonOptions, ct) ?? [];
    }

    public async Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        var url = "api/jobs"
            + $"?includeAll={query.IncludeAll.ToString().ToLowerInvariant()}"
            + QueryPart("lifecycleState", query.LifecycleState?.ToString())
            + QueryPart("terminalOutcome", query.TerminalOutcome?.ToString())
            + QueryPart("skipReason", query.SkipReason?.ToString())
            + QueryPart("kind", query.Kind?.ToWireString())
            + QueryPart("workflowId", query.WorkflowId?.ToString());

        return await http.GetFromJsonAsync<IReadOnlyList<JobSummaryDto>>(url, jsonOptions, ct) ?? [];
    }

    public async Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobDetailDto>(response, ct);
    }

    public async Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        if (workflowId is not Guid id)
            return null;

        using var response = await http.GetAsync($"api/workflows/{id}/jobs/display/{displayId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobDetailDto>(response, ct);
    }

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => await GetWorkflowAsync(workflowId, includeAll: false, ct);

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, bool includeAll, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/workflows/{workflowId}?includeAll={includeAll.ToString().ToLowerInvariant()}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<WorkflowDetailDto>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/files", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<FileCandidateDto>>(response, ct);
    }

    /// <summary>Projects a search job's raw results into file candidates using an explicit projection request.</summary>
    public async Task<SearchResultSnapshotDto<FileCandidateDto>?> ProjectFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<FileCandidateDto>, FileSearchProjectionRequestDto>($"api/jobs/{jobId}/results/files/project", request, ct);

    /// <summary>Alias for <see cref="ProjectFileResultsAsync"/> kept for compatibility with earlier client code.</summary>
    public async Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
        => await ProjectFileResultsAsync(jobId, request, ct);

    public async Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/folders?includeFiles={includeFiles.ToString().ToLowerInvariant()}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AlbumFolderDto>>(response, ct);
    }

    /// <summary>Projects a search job's raw results into album folders using an explicit projection request.</summary>
    public async Task<SearchResultSnapshotDto<AlbumFolderDto>?> ProjectFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<AlbumFolderDto>, FolderSearchProjectionRequestDto>($"api/jobs/{jobId}/results/folders/project", request, ct);

    /// <summary>Alias for <see cref="ProjectFolderResultsAsync"/> kept for compatibility with earlier client code.</summary>
    public async Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => await ProjectFolderResultsAsync(jobId, request, ct);

    public async Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/aggregate-tracks", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AggregateTrackCandidateDto>>(response, ct);
    }

    /// <summary>Projects a search job's raw results into aggregate track candidates using an explicit projection request.</summary>
    public async Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> ProjectAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<AggregateTrackCandidateDto>, AggregateTrackProjectionRequestDto>($"api/jobs/{jobId}/results/aggregate-tracks/project", request, ct);

    /// <summary>Alias for <see cref="ProjectAggregateTrackResultsAsync"/> kept for compatibility with earlier client code.</summary>
    public async Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
        => await ProjectAggregateTrackResultsAsync(jobId, request, ct);

    public async Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/aggregate-albums", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AggregateAlbumCandidateDto>>(response, ct);
    }

    /// <summary>Projects a search job's raw results into aggregate album candidates using an explicit projection request.</summary>
    public async Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> ProjectAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<AggregateAlbumCandidateDto>, AggregateAlbumProjectionRequestDto>($"api/jobs/{jobId}/results/aggregate-albums/project", request, ct);

    /// <summary>Alias for <see cref="ProjectAggregateAlbumResultsAsync"/> kept for compatibility with earlier client code.</summary>
    public async Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => await ProjectAggregateAlbumResultsAsync(jobId, request, ct);

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
        => await PostOptionalSummaryAsync($"api/jobs/{searchJobId}/retrieve-folder", request, ct);

    public async Task<int> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        var summary = await StartRetrieveFolderAsync(searchJobId, request, ct);
        if (summary == null)
            return 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var detail = await GetJobDetailAsync(summary.JobId, ct);
            if (detail == null || IsActiveLifecycle(detail.Summary.LifecycleState))
            {
                await Task.Delay(100, ct);
                continue;
            }

            return detail.Payload is RetrieveFolderJobPayloadDto payload
                ? payload.NewFilesFoundCount
                : 0;
        }
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<IReadOnlyList<JobSummaryDto>, StartFileDownloadsRequestDto>($"api/jobs/{searchJobId}/downloads/files", request, ct);

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
        => await PostOptionalSummaryAsync($"api/jobs/{searchJobId}/downloads/folder", request, ct);

    public async Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/jobs/{jobId}/manual/complete", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/jobs/{jobId}/cancel", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    public async Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        if (workflowId is Guid id)
        {
            using var response = await http.PostAsync($"api/workflows/{id}/jobs/display/{displayId}/cancel", null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;
            await EnsureSuccessAsync(response, ct);
            return true;
        }

        var jobs = await GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await CancelJobAsync(match.JobId, ct);
    }

    public async Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/workflows/{workflowId}/cancel", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return 0;

        await EnsureSuccessAsync(response, ct);
        var result = await ReadRequiredAsync<CancelWorkflowResponseDto>(response, ct);
        return result.Cancelled;
    }

    public async Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/jobs/{jobId}/next-candidate", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    public async Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        if (workflowId is Guid id)
        {
            using var response = await http.PostAsync($"api/workflows/{id}/jobs/display/{displayId}/next-candidate", null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;
            await EnsureSuccessAsync(response, ct);
            return true;
        }

        var jobs = await GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await TryNextCandidateAsync(match.JobId, ct);
    }

    private async Task<JobSummaryDto> PostJobAsync<TRequest>(string url, TRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobSummaryDto>(response, ct);
    }

    private async Task<JobSummaryDto?> PostOptionalSummaryAsync<T>(string url, T request, CancellationToken ct)
        => await PostOptionalAsync<JobSummaryDto, T>(url, request, ct);

    private async Task<TResponse?> PostOptionalAsync<TResponse, TRequest>(string url, TRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, jsonOptions, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<TResponse>(response, ct);
    }

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync<T>(jsonOptions, ct)
            ?? throw new InvalidOperationException($"Server returned an empty {typeof(T).Name} response.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = TryReadApiError(body) ?? body;
        throw new SockseekApiRequestException($"Server request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static string? TryReadApiError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ApiErrorDto>(body, SockseekApiJson.CreateSerializerOptions())?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string QueryPart(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : $"&{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private static bool IsActiveLifecycle(ServerJobLifecycleState state)
        => state != ServerJobLifecycleState.Terminal;
}
