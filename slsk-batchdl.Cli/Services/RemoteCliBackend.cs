using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Sldl.Server;

namespace Sldl.Cli;

internal sealed class RemoteCliBackend : ICliBackend, IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly HubConnection connection;
    private readonly JsonSerializerOptions jsonOptions;

    public event Action<ServerEventEnvelopeDto>? EventReceived;

    public RemoteCliBackend(string serverUrl)
    {
        var baseUri = NormalizeServerUrl(serverUrl);
        http = new HttpClient { BaseAddress = baseUri };
        jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "api/events"))
            .WithAutomaticReconnect()
            .Build();

        connection.On<ServerEventEnvelopeDto>("serverEvent", envelope =>
        {
            EventReceived?.Invoke(RehydrateEnvelope(envelope));
        });
    }

    internal static Uri NormalizeServerUrl(string serverUrl)
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

    public Task StartAsync(CancellationToken ct = default)
        => connection.StartAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        http.Dispose();
    }

    public async Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/extract", request, ct);

    public async Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/search", request, ct);

    public async Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/search/tracks", request, ct);

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

    public Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => connection.InvokeAsync("SubscribeWorkflow", workflowId, ct);

    public Task SubscribeAllAsync(CancellationToken ct = default)
        => connection.InvokeAsync("SubscribeAll", ct);

    private async Task<JobSummaryDto> PostJobAsync<TRequest>(string url, TRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        var summary = await ReadRequiredAsync<JobSummaryDto>(response, ct);
        await SubscribeWorkflowAsync(summary.WorkflowId, ct);
        return summary;
    }

    public async Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        var url = "api/jobs"
            + $"?includeAll={query.IncludeAll.ToString().ToLowerInvariant()}"
            + QueryPart("state", query.State?.ToString())
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

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/workflows/{workflowId}", ct);
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

    public async Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<FileCandidateDto>, FileSearchProjectionRequestDto>($"api/jobs/{jobId}/results/files/project", request, ct);

    public async Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/folders?includeFiles={includeFiles.ToString().ToLowerInvariant()}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AlbumFolderDto>>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<AlbumFolderDto>, FolderSearchProjectionRequestDto>($"api/jobs/{jobId}/results/folders/project", request, ct);

    public async Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/aggregate-tracks", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AggregateTrackCandidateDto>>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<AggregateTrackCandidateDto>, AggregateTrackProjectionRequestDto>($"api/jobs/{jobId}/results/aggregate-tracks/project", request, ct);

    public async Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/aggregate-albums", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AggregateAlbumCandidateDto>>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<SearchResultSnapshotDto<AggregateAlbumCandidateDto>, AggregateAlbumProjectionRequestDto>($"api/jobs/{jobId}/results/aggregate-albums/project", request, ct);

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
            if (detail == null || IsActiveState(detail.Summary.State))
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

        var jobs = await GetJobsAsync(new JobQuery(null, null, null, IncludeAll: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await CancelJobAsync(match.JobId, ct);
    }

    public async Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/workflows/{workflowId}/cancel", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return 0;

        await EnsureSuccessAsync(response, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("cancelled", out var cancelled) ? cancelled.GetInt32() : 0;
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

        var jobs = await GetJobsAsync(new JobQuery(null, null, null, IncludeAll: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await TryNextCandidateAsync(match.JobId, ct);
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

    private ServerEventEnvelopeDto RehydrateEnvelope(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not JsonElement payload)
            return envelope;

        object typedPayload = envelope.Type switch
        {
            "job.upserted" => Deserialize<JobSummaryDto>(payload),
            "workflow.upserted" => Deserialize<WorkflowSummaryDto>(payload),
            "search.updated" => Deserialize<SearchUpdatedDto>(payload),
            "extraction.started" => Deserialize<ExtractionStartedEventDto>(payload),
            "extraction.failed" => Deserialize<ExtractionFailedEventDto>(payload),
            "job.started" => Deserialize<JobStartedEventDto>(payload),
            "job.status" => Deserialize<JobStatusEventDto>(payload),
            "job.folder-retrieving" => Deserialize<JobFolderRetrievingEventDto>(payload),
            "song.searching" => Deserialize<SongSearchingEventDto>(payload),
            "download.started" => Deserialize<DownloadStartedEventDto>(payload),
            "download.progress" => Deserialize<DownloadProgressEventDto>(payload),
            "download.state-changed" => Deserialize<DownloadStateChangedEventDto>(payload),
            "song.state-changed" => Deserialize<SongStateChangedEventDto>(payload),
            "album.download-started" => Deserialize<AlbumDownloadStartedEventDto>(payload),
            "album.track-download-started" => Deserialize<AlbumTrackDownloadStartedEventDto>(payload),
            "album.download-completed" => Deserialize<AlbumDownloadCompletedEventDto>(payload),
            "on-complete.started" => Deserialize<OnCompleteStartedEventDto>(payload),
            "on-complete.ended" => Deserialize<OnCompleteEndedEventDto>(payload),
            "track-batch.resolved" => Deserialize<TrackBatchResolvedEventDto>(payload),
            _ => payload,
        };

        return envelope with { Payload = typedPayload };
    }

    private T Deserialize<T>(JsonElement payload)
        => payload.Deserialize<T>(jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize server event payload as {typeof(T).Name}.");

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync<T>(jsonOptions, ct)
            ?? throw new InvalidOperationException($"Server returned an empty {typeof(T).Name} response.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = TryReadApiError(body) ?? body;
        throw new InvalidOperationException($"Server request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static string? TryReadApiError(string body)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ApiErrorDto>(body)?.Error;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string QueryPart(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : $"&{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private static bool IsActiveState(ServerJobState state)
        => state is ServerJobState.Pending
            or ServerJobState.Searching
            or ServerJobState.Downloading
            or ServerJobState.Extracting;
}
