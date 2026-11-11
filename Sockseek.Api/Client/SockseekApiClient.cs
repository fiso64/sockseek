using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Sockseek.Api;

/// <summary>Keep credentialed HTTP and SignalR negotiation requests on the configured server.</summary>
internal sealed class ServerOriginHandler(Uri origin, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is not { } target || target.Scheme != origin.Scheme
            || target.IdnHost != origin.IdnHost || target.Port != origin.Port || target.UserInfo.Length != 0)
            throw new InvalidOperationException("The daemon client cannot send credentials to another origin.");
        return base.SendAsync(request, ct);
    }
}

public sealed record CursorPage<T>(
    IReadOnlyList<T> Items, string? NextCursor, StateStreamPositionDto? ReadPosition = null);
public sealed record SequencePage<T>(IReadOnlyList<T> Items, long? NextSequence);
public sealed record AttemptPage<T>(IReadOnlyList<T> Items, int? NextAttemptNumber);

/// <summary>
/// Owns a streamed picture response. Reading the body is explicit; disposing
/// this value releases the HTTP response and any unread network content.
/// </summary>
public sealed class UserPictureResponse : IDisposable
{
    private readonly HttpResponseMessage response;

    internal UserPictureResponse(HttpResponseMessage response)
        => this.response = response;

    public bool NotModified => response.StatusCode == HttpStatusCode.NotModified;
    public string? MediaType => response.Content.Headers.ContentType?.MediaType;
    public long? ContentLength => response.Content.Headers.ContentLength;
    public string? ETag => response.Headers.ETag?.ToString();
    public HttpResponseMessage HttpResponse => response;

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        => response.Content.ReadAsStreamAsync(cancellationToken);

    public void Dispose() => response.Dispose();
}

public sealed record TransferHistoryFilter(
    Guid? JobId = null,
    Guid? WorkflowId = null,
    string? Direction = null,
    string? Source = null,
    string? State = null,
    string? TerminalOutcome = null,
    string? Username = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    bool IncludeRemoved = false,
    string? GroupRef = null);

/// <summary>Exception raised for daemon HTTP responses that intentionally return an API error body.</summary>
public sealed class SockseekApiRequestException : InvalidOperationException
{
    public SockseekApiRequestException(string message)
        : base(message)
    {
    }

    public SockseekApiRequestException(
        HttpStatusCode statusCode,
        string? code,
        string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? Code { get; }
}

/// <summary>
/// Reusable HTTP client for daemon snapshots, commands, and history queries.
/// Pair it with <see cref="SockseekLiveClient"/> for SignalR live monitoring.
/// </summary>
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
        bool explicitScheme = value.Contains("://", StringComparison.Ordinal);
        if (!explicitScheme)
            value = "http://" + value;

        var builder = new UriBuilder(value);
        if (!explicitScheme)
        {
            var authority = value["http://".Length..].Split('/', 2)[0];
            // A colon after the closing IPv6 bracket (or in a plain host) specifies a port.
            if (authority.LastIndexOf(':') <= authority.LastIndexOf(']')) builder.Port = 5030;
        }

        if (builder.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(builder.UserName)
            || !string.IsNullOrEmpty(builder.Password) || !string.IsNullOrEmpty(builder.Query)
            || !string.IsNullOrEmpty(builder.Fragment))
            throw new ArgumentException("Use an HTTP(S) server URL without credentials, query or fragment.");

        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";

        return builder.Uri;
    }

    /// <summary>Creates an <see cref="HttpClient"/> with a normalized daemon base address.</summary>
    public static HttpClient CreateHttpClient(string serverUrl, string? apiKey = null)
    {
        // Never forward a credential to a redirect target. The operator supplies the canonical URL.
        var origin = NormalizeServerUrl(serverUrl);
        var http = new HttpClient(new ServerOriginHandler(origin, new HttpClientHandler { AllowAutoRedirect = false }))
            { BaseAddress = origin };
        if (apiKey is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            {
                http.Dispose();
                throw new ArgumentException("The remote API key is empty or contains invalid characters.");
            }
            http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
        return http;
    }

    public Task<IReadOnlyList<ApiKeyDto>> GetApiKeysAsync(CancellationToken ct = default)
        => GetRequiredAsync<IReadOnlyList<ApiKeyDto>>("api/auth/keys", ct);

    public Task<CreatedApiKeyDto> CreateApiKeyAsync(string name, CancellationToken ct = default)
        => PostRequiredAsync<CreatedApiKeyDto, CreateApiKeyRequestDto>("api/auth/keys", new(name), ct);

    public Task RevokeApiKeyAsync(Guid id, CancellationToken ct = default)
        => DeleteRequiredAsync($"api/auth/keys/{id}", ct);

    public Task<ServerInfoDto> GetServerInfoAsync(CancellationToken ct = default)
        => GetRequiredAsync<ServerInfoDto>("api/server/info", ct);

    public Task<StateSnapshotDto> GetDaemonSnapshotAsync(CancellationToken ct = default, DaemonStateDetail detail = DaemonStateDetail.Full)
        => GetRequiredAsync<StateSnapshotDto>($"api/daemon/snapshot?detail={detail}", ct);

    public Task<StateSnapshotDto> GetWorkflowSnapshotAsync(
        Guid workflowId,
        CancellationToken ct = default)
        => GetRequiredAsync<StateSnapshotDto>($"api/workflows/{workflowId}/snapshot", ct);

    public Task<StateSnapshotDto> GetConversationSnapshotAsync(
        Guid conversationId, CancellationToken ct = default)
        => GetRequiredAsync<StateSnapshotDto>(
            $"api/chat/conversations/{conversationId}/snapshot", ct);

    public Task<StateSnapshotDto> GetRoomSnapshotAsync(
        Guid roomId, CancellationToken ct = default)
        => GetRequiredAsync<StateSnapshotDto>($"api/chat/rooms/{roomId}/snapshot", ct);

    public async Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/extract", request, ct);

    /// <summary>Submits a generic search job. Use projection methods to view the same raw results as files, folders, group tracks, or group albums.</summary>
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

    public async Task<JobSummaryDto> SubmitSongGroupsJobAsync(SubmitSongGroupsJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/groups/tracks", request, ct);

    public async Task<JobSummaryDto> SubmitAlbumGroupsJobAsync(SubmitAlbumGroupsJobRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/groups/albums", request, ct);

    public async Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => await PostJobAsync("api/jobs/lists", request, ct);

    /// <summary>Resolves UI-safe effective settings without creating a workflow or runtime job.</summary>
    public async Task<ResolveEffectiveSettingsResponseDto> ResolveEffectiveSettingsAsync(
        ResolveEffectiveSettingsRequestDto request,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/jobs/effective-settings",
            request,
            jsonOptions,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ResolveEffectiveSettingsResponseDto>(
            jsonOptions,
            ct) ?? throw new SockseekApiRequestException("The daemon returned an empty effective-settings response.");
    }

    public Task<CreateJobPreviewResponseDto> CreateJobPreviewAsync(
        CreateJobPreviewRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<CreateJobPreviewResponseDto, CreateJobPreviewRequestDto>(
            "api/job-previews",
            request,
            ct);

    public async Task<InputArtifactDto> UploadInputArtifactAsync(
        Stream content,
        string? originalName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/input-artifacts"
                + (string.IsNullOrEmpty(originalName)
                    ? ""
                    : "?fileName=" + Uri.EscapeDataString(originalName)));
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<InputArtifactDto>(response, ct);
    }

    public Task<InputArtifactDto?> GetInputArtifactAsync(
        string artifactId,
        CancellationToken ct = default)
        => GetOptionalAsync<InputArtifactDto>(
            $"api/input-artifacts/{Uri.EscapeDataString(artifactId)}",
            ct);

    public Task<JobPreviewSummaryDto?> GetJobPreviewAsync(
        Guid previewId,
        CancellationToken ct = default,
        bool waitForCompletion = false)
        => GetOptionalAsync<JobPreviewSummaryDto>(
            $"api/job-previews/{previewId}" + (waitForCompletion ? "?waitForCompletion=true" : ""), ct);

    public async Task<CursorPage<JobPreviewNodeDto>?> GetJobPreviewNodesPageAsync(
        Guid previewId,
        string? parentRef = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        CancellationToken ct = default)
    {
        string url = $"api/job-previews/{previewId}/nodes?limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("parentRef", parentRef)
            + QueryPart("cursor", cursor);
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return new CursorPage<JobPreviewNodeDto>(
            await ReadRequiredAsync<IReadOnlyList<JobPreviewNodeDto>>(response, ct),
            Header(response, "X-Next-Cursor"));
    }

    public Task<CommitJobPreviewResponseDto> CommitJobPreviewAsync(
        Guid previewId,
        CommitJobPreviewRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<CommitJobPreviewResponseDto, CommitJobPreviewRequestDto>(
            $"api/job-previews/{previewId}/commit",
            request,
            ct);

    /// <summary>
    /// Creates a disk-backed projection that can be read while its source
    /// search is still running. The returned revision is immutable.
    /// </summary>
    public Task<SearchViewSummaryDto> CreateSearchViewAsync(
        Guid jobId,
        CreateSearchViewRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<SearchViewSummaryDto, CreateSearchViewRequestDto>(
            $"api/jobs/{jobId}/search-views",
            request,
            ct);

    public Task<SearchViewSummaryDto?> GetSearchViewAsync(
        Guid viewId,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewSummaryDto>($"api/search-views/{viewId}", ct);

    public Task<SearchViewDefinitionDto?> GetSearchViewDefinitionAsync(
        Guid viewId,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewDefinitionDto>(
            $"api/search-views/{viewId}/definition", ct);

    public Task<SearchViewFilePageDto?> GetSearchViewFilesAsync(
        Guid viewId,
        long revision,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        ServerSearchViewSortOrder? sortOrder = null,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewFilePageDto>(
            $"api/search-views/{viewId}/files"
            + $"?revision={revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor)
            + QueryPart("sortOrder", sortOrder?.ToString()),
            ct);

    public Task<SearchViewDirectoryPageDto?> GetSearchViewDirectoriesAsync(
        Guid viewId,
        long revision,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        ServerSearchViewSortOrder? sortOrder = null,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewDirectoryPageDto>(
            $"api/search-views/{viewId}/directories"
            + $"?revision={revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor)
            + QueryPart("sortOrder", sortOrder?.ToString()),
            ct);

    public Task<SearchViewDirectoryFilePageDto?> GetSearchViewDirectoryFilesAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewDirectoryFilePageDto>(
            $"api/search-views/{viewId}/directories/{Uri.EscapeDataString(directoryRef)}/files"
            + $"?revision={revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor),
            ct);

    public Task<JobSummaryDto> RetrieveSearchViewDirectoryAsync(
        Guid viewId,
        RetrieveSearchViewDirectoryRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<JobSummaryDto, RetrieveSearchViewDirectoryRequestDto>(
            $"api/search-views/{viewId}/directories/retrieve",
            request,
            ct);

    public Task<SearchViewTrackGroupPageDto?> GetSearchViewTrackGroupsAsync(
        Guid viewId,
        long revision,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewTrackGroupPageDto>(
            $"api/search-views/{viewId}/track-groups"
            + $"?revision={revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor),
            ct);

    public Task<SearchViewTrackGroupOptionPageDto?> GetSearchViewTrackGroupOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewTrackGroupOptionPageDto>(
            $"api/search-views/{viewId}/track-groups/{Uri.EscapeDataString(groupRef)}/alternatives"
            + $"?revision={revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor),
            ct);

    public Task<SearchViewAlbumGroupPageDto?> GetSearchViewAlbumGroupsAsync(
        Guid viewId,
        long revision,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewAlbumGroupPageDto>(
            $"api/search-views/{viewId}/album-groups"
            + $"?revision={revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor),
            ct);

    public Task<SearchViewAlbumGroupOptionPageDto?> GetSearchViewAlbumGroupOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewAlbumGroupOptionPageDto>(
            $"api/search-views/{viewId}/album-groups/{Uri.EscapeDataString(groupRef)}/alternatives"
            + $"?revision={revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor),
            ct);

    public Task<SearchViewUpdateDto?> GetSearchViewUpdatesAsync(
        Guid viewId,
        long afterRevision,
        bool waitForChange = false,
        CancellationToken ct = default)
        => GetOptionalAsync<SearchViewUpdateDto>(
            $"api/search-views/{viewId}/updates"
            + $"?afterRevision={afterRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + (waitForChange ? "&waitForChange=true" : ""),
            ct);

    public Task<ResolveSearchViewChosenOptionsResponseDto> ResolveSearchViewChosenOptionsAsync(
        Guid viewId,
        ResolveSearchViewChosenOptionsRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<ResolveSearchViewChosenOptionsResponseDto, ResolveSearchViewChosenOptionsRequestDto>(
            $"api/search-views/{viewId}/chosen-options/resolve",
            request,
            ct);

    public Task<CommitSearchViewSelectionResponseDto> CommitSearchViewSelectionAsync(
        Guid viewId,
        CommitSearchViewSelectionRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<CommitSearchViewSelectionResponseDto, CommitSearchViewSelectionRequestDto>(
            $"api/search-views/{viewId}/commit",
            request,
            ct);

    public Task<ResolveSearchViewSelectionSettingsResponseDto> ResolveSearchViewSelectionSettingsAsync(
        Guid viewId,
        ResolveSearchViewSelectionSettingsRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<ResolveSearchViewSelectionSettingsResponseDto, ResolveSearchViewSelectionSettingsRequestDto>(
            $"api/search-views/{viewId}/selection-settings",
            request,
            ct);

    public Task<CountSearchViewSelectionResponseDto> CountSearchViewSelectionAsync(
        Guid viewId,
        CountSearchViewSelectionRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<CountSearchViewSelectionResponseDto, CountSearchViewSelectionRequestDto>(
            $"api/search-views/{viewId}/selection-counts",
            request,
            ct);

    public Task<UserRestrictionsDto?> GetUserRestrictionsAsync(
        string username,
        CancellationToken ct = default)
        => GetOptionalAsync<UserRestrictionsDto>(
            $"api/users/{Uri.EscapeDataString(username)}/restrictions",
            ct);

    public async Task<UserRestrictionsDto> SetUserRestrictionAsync(
        string username,
        SetUserRestrictionOverrideRequestDto request,
        CancellationToken ct = default)
    {
        using HttpResponseMessage response = await http.PutAsJsonAsync(
            $"api/users/{Uri.EscapeDataString(username)}/restrictions",
            request,
            jsonOptions,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<UserRestrictionsDto>(response, ct);
    }

    /// <summary>Returns available daemon profiles.</summary>
    public async Task<IReadOnlyList<ProfileSummaryDto>> GetProfilesAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/profiles", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ProfileSummaryDto>>(jsonOptions, ct) ?? [];
    }

    public async Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(
        JobQuery query,
        CancellationToken ct = default)
    {
        var jobs = new List<JobSummaryDto>();
        string? cursor = null;
        do
        {
            var page = await GetJobsPageAsync(query, cursor, limit: 200, ct);
            jobs.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return jobs;
    }

    public async Task<CursorPage<JobSummaryDto>> GetJobsPageAsync(
        JobQuery query,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        CancellationToken ct = default)
    {
        var url = "api/jobs"
            + $"?includeAll={query.IncludeAll.ToString().ToLowerInvariant()}"
            + QueryPart("lifecycleState", query.LifecycleState?.ToString())
            + QueryPart("terminalOutcome", query.TerminalOutcome?.ToString())
            + QueryPart("skipReason", query.SkipReason?.ToString())
            + QueryPart("kind", query.Kind?.ToWireString())
            + string.Concat(query.ExcludeKinds?.Select(kind => QueryPart("excludeKinds", kind.ToWireString())) ?? [])
            + QueryPart("workflowId", query.WorkflowId?.ToString())
            + QueryPart("parentJobId", query.ParentJobId?.ToString())
            + QueryPart("submissionId", query.SubmissionId?.ToString())
            + QueryPart("role", query.Role?.ToWireString())
            + QueryPart("newestFirst", query.NewestFirst.ToString().ToLowerInvariant())
            + QueryPart("excludeExtractResults", query.ExcludeExtractResults.ToString().ToLowerInvariant())
            + QueryPart("cursor", cursor)
            + QueryPart("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return await GetCursorPageAsync<JobSummaryDto>(url, ct);
    }

    public async Task<JobListUpdateDto> GetJobListSnapshotAsync(JobListScopeDto scope, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/jobs/snapshot", scope, jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobListUpdateDto>(response, ct);
    }

    public Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
        => GetOptionalAsync<JobDetailDto>($"api/jobs/{jobId}", ct);

    public async Task<IReadOnlyList<JobSummaryDto>> LookupJobsAsync(JobLookupRequestDto request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/jobs/lookup", request, jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<IReadOnlyList<JobSummaryDto>>(response, ct);
    }

    public Task<DirectoryManifestPageDto?> GetDirectoryManifestAsync(
        Guid jobId,
        int? attemptNumber = null,
        int afterOrdinal = -1,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
        => GetOptionalAsync<DirectoryManifestPageDto>(
            $"api/jobs/{jobId}/directory-files?afterOrdinal={afterOrdinal}&limit={limit}"
            + (attemptNumber is { } attempt ? $"&attemptNumber={attempt}" : ""), ct);

    public async Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        if (workflowId is not Guid id)
            return null;

        return await GetOptionalAsync<JobDetailDto>(
            $"api/workflows/{id}/jobs/display/{displayId}", ct);
    }

    public Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => GetOptionalAsync<WorkflowDetailDto>($"api/workflows/{workflowId}", ct);

    public async Task<CursorPage<SubmissionSummaryDto>> GetSubmissionsPageAsync(
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
        => await GetCursorPageAsync<SubmissionSummaryDto>(
            $"api/submissions?limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + QueryPart("cursor", cursor),
            ct);

    public Task<SubmissionDetailDto?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken ct = default)
        => GetOptionalAsync<SubmissionDetailDto>($"api/submissions/{submissionId}", ct);

    public Task<DeleteSubmissionResponseDto> DeleteSubmissionAsync(Guid submissionId, CancellationToken ct = default)
        => DeleteRequiredAsync<DeleteSubmissionResponseDto>($"api/submissions/{submissionId}", ct);

    public async Task<JobSummaryDto?> RerunSubmissionAsync(
        Guid submissionId,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/submissions/{submissionId}/rerun", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobSummaryDto>(response, ct);
    }

    public async Task<CursorPage<WorkflowSummaryDto>> GetWorkflowsPageAsync(
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
        => await GetCursorPageAsync<WorkflowSummaryDto>(
            "api/workflows?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + QueryPart("cursor", cursor),
            ct);

    public async Task<SequencePage<SearchRawResultDto>?> GetRawSearchResultsPageAsync(
        Guid jobId,
        long afterSequence = 0,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            $"api/jobs/{jobId}/raw?afterSequence={afterSequence}&limit={limit}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return new SequencePage<SearchRawResultDto>(
            await ReadRequiredAsync<IReadOnlyList<SearchRawResultDto>>(response, ct),
            HeaderLong(response, "X-Next-Sequence"));
    }

    public async Task<TransferTimelinePageDto> GetTransfersPageAsync(
        TransferHistoryFilter? query = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        query ??= new TransferHistoryFilter();
        string url = "api/transfers?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + QueryPart("jobId", query.JobId?.ToString())
            + QueryPart("workflowId", query.WorkflowId?.ToString())
            + QueryPart("direction", query.Direction)
            + QueryPart("source", query.Source)
            + QueryPart("state", query.State)
            + QueryPart("terminalOutcome", query.TerminalOutcome)
            + QueryPart("username", query.Username)
            + QueryPart("groupRef", query.GroupRef)
            + QueryPart("fromUtc", query.FromUtc?.ToString("O"))
            + QueryPart("toUtc", query.ToUtc?.ToString("O"))
            + QueryPart("includeRemoved", query.IncludeRemoved.ToString().ToLowerInvariant())
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<TransferTimelinePageDto>(url, ct);
    }

    public Task<TransferTimelineUpdateDto> GetTransferTimelineAsync(
        TransferTimelineScopeDto scope, CancellationToken ct = default)
        => PostRequiredAsync<TransferTimelineUpdateDto, TransferTimelineScopeDto>(
            "api/transfers/timeline", scope, ct);

    public Task<TransferTimelinePageDto> LookupTransfersAsync(
        TransferLookupRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<TransferTimelinePageDto, TransferLookupRequestDto>(
            "api/transfers/lookup", request, ct);

    public Task<DashboardAnalyticsDto> GetDashboardAnalyticsAsync(
        string range = "24h",
        CancellationToken ct = default)
        => GetRequiredAsync<DashboardAnalyticsDto>(
            "api/dashboard/analytics?range=" + Uri.EscapeDataString(range),
            ct);

    public Task<TransferDetailDto?> GetTransferAsync(
        Guid transferId,
        CancellationToken ct = default)
        => GetOptionalAsync<TransferDetailDto>(
            $"api/transfers/{transferId}", ct);

    public Task<TransferCommandReceiptDto> CancelTransfersAsync(
        BulkCancelTransfersRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<TransferCommandReceiptDto, BulkCancelTransfersRequestDto>(
            "api/transfers/cancel",
            request,
            ct);

    public Task<TransferGroupSummariesResponseDto> GetTransferGroupSummariesAsync(
        TransferGroupSummariesRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<TransferGroupSummariesResponseDto, TransferGroupSummariesRequestDto>(
            "api/transfers/group-summaries",
            request,
            ct);

    public Task<TransferCommandReceiptDto> RemoveTransferAsync(Guid transferId, CancellationToken ct = default)
        => DeleteRequiredAsync<TransferCommandReceiptDto>($"api/transfers/{transferId}", ct);

    public Task<TransferCommandReceiptDto> RemoveTransfersAsync(
        RemoveTransfersRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<TransferCommandReceiptDto, RemoveTransfersRequestDto>(
            "api/transfers/remove",
            request,
            ct);

    public Task<SharingStateDto> GetSharingAsync(CancellationToken ct = default)
        => GetRequiredAsync<SharingStateDto>("api/sharing", ct);

    public async Task<StartShareScanResponseDto> StartShareScanAsync(
        CancellationToken ct = default)
        => await PostWithoutBodyAsync<StartShareScanResponseDto>(
            "api/sharing/scans",
            ct);

    public Task<ShareScanStateDto?> GetShareScanAsync(
        Guid scanId,
        CancellationToken ct = default)
        => GetOptionalAsync<ShareScanStateDto>($"api/sharing/scans/{scanId}", ct);

    public async Task<ShareScanStateDto> CancelShareScanAsync(
        Guid scanId,
        CancellationToken ct = default)
        => await PostWithoutBodyAsync<ShareScanStateDto>(
            $"api/sharing/scans/{scanId}/cancel",
            ct);

    public async Task<TransferStateDto> CancelTransferAsync(
        Guid transferId,
        CancellationToken ct = default)
        => await PostWithoutBodyAsync<TransferStateDto>(
            $"api/transfers/{transferId}/cancel",
            ct);

    public async Task<LiveTransferPageDto> LoadLiveTransferPageAsync(
        LiveTransferFilter? filter = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        filter ??= new LiveTransferFilter();
        string url = "api/transfers/live?limit="
            + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + QueryPart("direction", filter.Direction)
            + QueryPart("state", filter.State)
            + QueryPart("username", filter.Username)
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<LiveTransferPageDto>(url, ct);
    }

    public async Task<AttemptPage<TransferAttemptHistoryDto>?> GetTransferAttemptsPageAsync(
        Guid transferId,
        int afterAttemptNumber = 0,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            $"api/transfers/{transferId}/attempts?afterAttemptNumber={afterAttemptNumber}&limit={limit}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return new AttemptPage<TransferAttemptHistoryDto>(
            await ReadRequiredAsync<IReadOnlyList<TransferAttemptHistoryDto>>(response, ct),
            HeaderInt(response, "X-Next-Attempt-Number"));
    }

    public async Task<PersistenceIntegrityResultDto> CheckPersistenceIntegrityAsync(CancellationToken ct = default)
        => await PostWithoutBodyAsync<PersistenceIntegrityResultDto>("api/persistence/integrity", ct);

    public async Task<PersistenceBackupResultDto> BackupPersistenceAsync(
        PersistenceBackupRequestDto request,
        CancellationToken ct = default)
        => await PostRequiredAsync<PersistenceBackupResultDto, PersistenceBackupRequestDto>(
            "api/persistence/backup", request, ct);

    public async Task<PersistenceCheckpointResultDto> CheckpointPersistenceAsync(CancellationToken ct = default)
        => await PostWithoutBodyAsync<PersistenceCheckpointResultDto>("api/persistence/checkpoint", ct);

    public async Task<PersistenceRetentionResultDto> RunPersistenceRetentionAsync(CancellationToken ct = default)
        => await PostWithoutBodyAsync<PersistenceRetentionResultDto>("api/persistence/retention", ct);

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
        => await PostOptionalSummaryAsync($"api/jobs/{searchJobId}/retrieve-folder", request, ct);

    public async Task<RetrieveFolderJobPayloadDto?> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        var summary = await StartRetrieveFolderAsync(searchJobId, request, ct);
        if (summary == null)
            return null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var detail = await GetJobDetailAsync(summary.JobId, ct);
            if (detail == null || IsActiveLifecycle(detail.Summary.LifecycleState))
            {
                await Task.Delay(100, ct);
                continue;
            }

            return detail.Payload as RetrieveFolderJobPayloadDto;
        }
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
        => await PostOptionalAsync<IReadOnlyList<JobSummaryDto>, StartFileDownloadsRequestDto>($"api/jobs/{searchJobId}/downloads/files", request, ct);

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
        => await PostOptionalSummaryAsync($"api/jobs/{searchJobId}/downloads/folder", request, ct);

    public Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => PostIfFoundAsync($"api/jobs/{jobId}/manual/complete", ct);

    public Task<bool> SkipManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => PostIfFoundAsync($"api/jobs/{jobId}/manual/skip", ct);

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
        => PostIfFoundAsync($"api/jobs/{jobId}/cancel", ct);

    public async Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        if (workflowId is Guid id)
        {
            return await PostIfFoundAsync(
                $"api/workflows/{id}/jobs/display/{displayId}/cancel", ct);
        }

        var jobs = await GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await CancelJobAsync(match.JobId, ct);
    }

    public async Task<int> CancelAllJobsAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("api/jobs/cancel-all", null, ct);
        await EnsureSuccessAsync(response, ct);
        var result = await ReadRequiredAsync<CancelJobsResponseDto>(response, ct);
        return result.Cancelled;
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

    public Task<ChatRuntimeStateDto> GetChatStatusAsync(CancellationToken ct = default)
        => GetRequiredAsync<ChatRuntimeStateDto>("api/chat", ct);

    public Task<UserProfileDto> GetUserProfileAsync(
        string username,
        bool refresh = false,
        CancellationToken ct = default)
        => GetRequiredAsync<UserProfileDto>(
            $"api/users/{Uri.EscapeDataString(username)}/profile?refresh={refresh.ToString().ToLowerInvariant()}",
            ct);

    public Task<UserPresenceObservationDto> GetUserPresenceAsync(
        string username,
        CancellationToken ct = default)
        => GetRequiredAsync<UserPresenceObservationDto>(
            $"api/users/{Uri.EscapeDataString(username)}/presence",
            ct);

    public async Task<UserPictureResponse> GetUserPictureAsync(
        string username,
        string? ifNoneMatch = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/users/{Uri.EscapeDataString(username)}/picture");
        if (!string.IsNullOrEmpty(ifNoneMatch))
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        HttpResponseMessage response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new UserPictureResponse(response);
        try
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return new UserPictureResponse(response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public Task<UserBrowseDto> StartUserBrowseAsync(
        string username,
        bool refresh = false,
        CancellationToken ct = default)
        => PostRequiredAsync<UserBrowseDto, StartUserBrowseRequestDto>(
            $"api/users/{Uri.EscapeDataString(username)}/browses",
            new StartUserBrowseRequestDto(refresh),
            ct);

    public async Task<PageDto<UserBrowseDto>> GetUserBrowsesAsync(
        string? username = null,
        UserBrowseState? state = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        string url = $"api/user-browses?limit={limit}"
            + QueryPart("username", username)
            + QueryPart("state", state is null ? null : UserBrowseStateWire(state.Value))
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<PageDto<UserBrowseDto>>(url, ct);
    }

    public Task<UserBrowseDto> GetUserBrowseAsync(
        Guid browseId,
        CancellationToken ct = default)
        => GetRequiredAsync<UserBrowseDto>($"api/user-browses/{browseId}", ct);

    public Task<StateSnapshotDto> GetUserBrowseSnapshotAsync(
        Guid browseId,
        CancellationToken ct = default)
        => GetRequiredAsync<StateSnapshotDto>($"api/user-browses/{browseId}/snapshot", ct);

    public Task<UserBrowseDto> CancelUserBrowseAsync(
        Guid browseId,
        CancellationToken ct = default)
        => PostWithoutBodyAsync<UserBrowseDto>($"api/user-browses/{browseId}/cancel", ct);

    public async Task<PageDto<BrowseDirectoryEntryDto>> GetUserShareDirectoriesAsync(
        Guid browseId,
        long? parentId = null,
        string? query = null,
        bool recursive = false,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        CancellationToken ct = default)
    {
        string url = $"api/user-browses/{browseId}/directories?limit={limit}&recursive={recursive.ToString().ToLowerInvariant()}"
            + QueryPart("parentId", parentId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + QueryPart("query", query)
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<PageDto<BrowseDirectoryEntryDto>>(url, ct);
    }

    public Task<BrowseDirectoryEntryDto> GetUserShareDirectoryAsync(
        Guid browseId,
        long directoryId,
        CancellationToken ct = default)
        => GetRequiredAsync<BrowseDirectoryEntryDto>(
            $"api/user-browses/{browseId}/directories/{directoryId}", ct);

    public async Task<PageDto<BrowseFileEntryDto>> GetUserShareFilesAsync(
        Guid browseId,
        long directoryId,
        string? query = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        string url = $"api/user-browses/{browseId}/directories/{directoryId}/files?limit={limit}"
            + QueryPart("query", query)
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<PageDto<BrowseFileEntryDto>>(url, ct);
    }

    public async Task<BrowseSearchPageDto> SearchUserSharesAsync(
        Guid browseId,
        string query,
        string? cursor = null,
        int limit = ServerProtocol.DefaultNestedPageSize,
        CancellationToken ct = default)
    {
        string url = $"api/user-browses/{browseId}/search?limit={limit}"
            + QueryPart("query", query)
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<BrowseSearchPageDto>(url, ct);
    }

    public Task<StartUserShareDownloadsResponseDto> StartUserShareDownloadsAsync(
        Guid browseId,
        StartUserShareDownloadsRequestDto request,
        CancellationToken ct = default)
        => PostRequiredAsync<StartUserShareDownloadsResponseDto, StartUserShareDownloadsRequestDto>(
            $"api/user-browses/{browseId}/downloads",
            request,
            ct);

    public async Task<ConversationPageDto> GetConversationsAsync(
        bool? unread = null,
        bool? archived = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        string url = "api/chat/conversations?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + QueryPart("unread", unread?.ToString().ToLowerInvariant())
            + QueryPart("archived", archived?.ToString().ToLowerInvariant())
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<ConversationPageDto>(url, ct);
    }

    public Task<ConversationSummaryDto?> GetConversationAsync(
        Guid conversationId, CancellationToken ct = default)
        => GetOptionalAsync<ConversationSummaryDto>(
            $"api/chat/conversations/{conversationId}", ct);

    public Task<ChatMessageDto> SendPrivateMessageAsync(
        SendPrivateMessageRequestDto request, CancellationToken ct = default)
        => PostRequiredAsync<ChatMessageDto, SendPrivateMessageRequestDto>(
            "api/chat/private-messages", request, ct);

    public Task<ConversationSummaryDto?> GetConversationByPeerAsync(
        string username, CancellationToken ct = default)
        => GetOptionalAsync<ConversationSummaryDto>(
            $"api/chat/conversations/by-peer?username={Uri.EscapeDataString(username)}", ct);

    public Task<ChatMessageDto> SendConversationMessageAsync(
        Guid conversationId, SendChatMessageRequestDto request, CancellationToken ct = default)
        => PostRequiredAsync<ChatMessageDto, SendChatMessageRequestDto>(
            $"api/chat/conversations/{conversationId}/messages", request, ct);

    public Task<ChatMessagePageDto> GetConversationMessagesAsync(
        Guid conversationId, string? cursor = null, int limit = ServerProtocol.DefaultPageSize, CancellationToken ct = default)
        => GetRequiredAsync<ChatMessagePageDto>(
            $"api/chat/conversations/{conversationId}/messages?limit={limit}"
            + QueryPart("cursor", cursor), ct);

    public Task<ConversationSummaryDto> MarkConversationReadAsync(
        Guid conversationId, Guid throughMessageId, CancellationToken ct = default)
        => PostRequiredAsync<ConversationSummaryDto, MarkChatReadRequestDto>(
            $"api/chat/conversations/{conversationId}/read",
            new MarkChatReadRequestDto(throughMessageId), ct);

    public Task<ConversationSummaryDto> ArchiveConversationAsync(
        Guid conversationId, bool archived = true, CancellationToken ct = default)
        => PostRequiredAsync<ConversationSummaryDto, ArchiveConversationRequestDto>(
            $"api/chat/conversations/{conversationId}/archive",
            new ArchiveConversationRequestDto(archived), ct);

    public Task DeleteConversationHistoryAsync(Guid conversationId, CancellationToken ct = default)
        => DeleteRequiredAsync($"api/chat/conversations/{conversationId}/history", ct);

    public async Task<AvailableRoomPageDto> GetAvailableRoomsAsync(
        ServerChatRoomKind? kind = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        bool refresh = false,
        CancellationToken ct = default)
    {
        string url = $"api/chat/rooms/available?limit={limit}&refresh={refresh.ToString().ToLowerInvariant()}"
            + QueryPart("kind", kind?.ToString())
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<AvailableRoomPageDto>(url, ct);
    }

    public async Task<ChatRoomPageDto> GetRoomsAsync(
        string? state = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        string url = $"api/chat/rooms?limit={limit}"
            + QueryPart("state", state)
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<ChatRoomPageDto>(url, ct);
    }

    public Task<ChatRoomSummaryDto> JoinRoomAsync(
        string roomName, bool remember = true, CancellationToken ct = default)
        => PostRequiredAsync<ChatRoomSummaryDto, JoinRoomRequestDto>(
            "api/chat/rooms", new JoinRoomRequestDto(roomName, remember), ct);

    public Task<ChatRoomDetailDto?> GetRoomAsync(Guid roomId, CancellationToken ct = default)
        => GetOptionalAsync<ChatRoomDetailDto>($"api/chat/rooms/{roomId}", ct);

    public Task<ChatRoomSummaryDto> LeaveRoomAsync(Guid roomId, CancellationToken ct = default)
        => DeleteRequiredAsync<ChatRoomSummaryDto>($"api/chat/rooms/{roomId}", ct);

    public Task<ChatMessagePageDto> GetRoomMessagesAsync(
        Guid roomId, string? cursor = null, int limit = ServerProtocol.DefaultPageSize, CancellationToken ct = default)
        => GetRequiredAsync<ChatMessagePageDto>(
            $"api/chat/rooms/{roomId}/messages?limit={limit}" + QueryPart("cursor", cursor), ct);

    public Task<ChatMessageDto> SendRoomMessageAsync(
        Guid roomId, SendChatMessageRequestDto request, CancellationToken ct = default)
        => PostRequiredAsync<ChatMessageDto, SendChatMessageRequestDto>(
            $"api/chat/rooms/{roomId}/messages", request, ct);

    public Task<ChatRoomSummaryDto> MarkRoomReadAsync(
        Guid roomId, Guid throughMessageId, CancellationToken ct = default)
        => PostRequiredAsync<ChatRoomSummaryDto, MarkChatReadRequestDto>(
            $"api/chat/rooms/{roomId}/read", new MarkChatReadRequestDto(throughMessageId), ct);

    public async Task<RoomMemberPageDto> GetRoomMembersAsync(
        Guid roomId,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        long? revision = null,
        CancellationToken ct = default)
    {
        string url = $"api/chat/rooms/{roomId}/members?limit={limit}"
            + QueryPart("cursor", cursor)
            + QueryPart("revision", revision?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await GetRequiredAsync<RoomMemberPageDto>(url, ct);
    }

    public Task<ChatRoomDetailDto> AddPrivateRoomMemberAsync(
        Guid roomId, string username, CancellationToken ct = default)
        => PostRequiredAsync<ChatRoomDetailDto, AddRoomMemberRequestDto>(
            $"api/chat/rooms/{roomId}/members", new AddRoomMemberRequestDto(username), ct);

    public Task DeleteRoomHistoryAsync(Guid roomId, CancellationToken ct = default)
        => DeleteRequiredAsync($"api/chat/rooms/{roomId}/history", ct);

    public async Task<NotificationPageDto> GetNotificationsAsync(
        bool? unread = null,
        ServerUserNotificationKind? kind = null,
        string? cursor = null,
        int limit = ServerProtocol.DefaultPageSize,
        CancellationToken ct = default)
    {
        string url = $"api/notifications?limit={limit}"
            + QueryPart("unread", unread?.ToString().ToLowerInvariant())
            + QueryPart("kind", kind?.ToString())
            + QueryPart("cursor", cursor);
        return await GetRequiredAsync<NotificationPageDto>(url, ct);
    }

    public Task<UserNotificationDto?> GetNotificationAsync(Guid id, CancellationToken ct = default)
        => GetOptionalAsync<UserNotificationDto>($"api/notifications/{id}", ct);

    public Task<UserNotificationDto> MarkNotificationReadAsync(Guid id, CancellationToken ct = default)
        => PostWithoutBodyAsync<UserNotificationDto>($"api/notifications/{id}/read", ct);

    public Task<NotificationSummaryDto> MarkNotificationsReadAsync(
        MarkNotificationsReadRequestDto request, CancellationToken ct = default)
        => PostRequiredAsync<NotificationSummaryDto, MarkNotificationsReadRequestDto>(
            "api/notifications/read", request, ct);

    public Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
        => PostIfFoundAsync($"api/jobs/{jobId}/next-candidate", ct);

    public async Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        if (workflowId is Guid id)
        {
            return await PostIfFoundAsync(
                $"api/workflows/{id}/jobs/display/{displayId}/next-candidate", ct);
        }

        var jobs = await GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await TryNextCandidateAsync(match.JobId, ct);
    }

    private Task<JobSummaryDto> PostJobAsync<TRequest>(string url, TRequest request, CancellationToken ct)
        => PostRequiredAsync<JobSummaryDto, TRequest>(url, request, ct);

    private Task<JobSummaryDto?> PostOptionalSummaryAsync<T>(string url, T request, CancellationToken ct)
        => PostOptionalAsync<JobSummaryDto, T>(url, request, ct);

    private async Task<TResponse?> PostOptionalAsync<TResponse, TRequest>(string url, TRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, jsonOptions, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<TResponse>(response, ct);
    }

    private async Task<TResponse> PostRequiredAsync<TResponse, TRequest>(string url, TRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<TResponse>(response, ct);
    }

    private async Task<TResponse> PostWithoutBodyAsync<TResponse>(string url, CancellationToken ct)
    {
        using var response = await http.PostAsync(url, content: null, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<TResponse>(response, ct);
    }

    private async Task<T> GetRequiredAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<T>(response, ct);
    }

    private async Task<T?> GetOptionalAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<T>(response, ct);
    }

    private async Task<bool> PostIfFoundAsync(string url, CancellationToken ct)
    {
        using var response = await http.PostAsync(url, content: null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    private async Task DeleteRequiredAsync(string url, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<T> DeleteRequiredAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<T>(response, ct);
    }

    private async Task<CursorPage<T>> GetCursorPageAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return new CursorPage<T>(
            await ReadRequiredAsync<IReadOnlyList<T>>(response, ct),
            Header(response, "X-Next-Cursor"),
            Header(response, ServerProtocol.ReadEpochHeader) is { } epoch
                && Header(response, ServerProtocol.ReadSequenceHeader) is { } sequence
                ? new StateStreamPositionDto(Guid.Parse(epoch), long.Parse(sequence,
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture))
                : null);
    }

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync<T>(jsonOptions, ct)
            ?? throw new InvalidOperationException($"Server returned an empty {typeof(T).Name} response.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        ApiErrorDto? error = TryReadApiError(body);
        string detail = error?.Error ?? body;
        throw new SockseekApiRequestException(
            response.StatusCode,
            error?.Code,
            $"Server request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static ApiErrorDto? TryReadApiError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ApiErrorDto>(
                body,
                SockseekApiJson.CreateSerializerOptions());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string QueryPart(string name, string? value)
        => string.IsNullOrEmpty(value) ? "" : $"&{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static long? HeaderLong(HttpResponseMessage response, string name)
        => long.TryParse(Header(response, name), out long value) ? value : null;

    private static int? HeaderInt(HttpResponseMessage response, string name)
        => int.TryParse(Header(response, name), out int value) ? value : null;

    private static bool IsActiveLifecycle(ServerJobLifecycleState state)
        => state != ServerJobLifecycleState.Terminal;

    private static string UserBrowseStateWire(UserBrowseState state)
        => state switch
        {
            UserBrowseState.Queued => "queued",
            UserBrowseState.Running => "running",
            UserBrowseState.Complete => "complete",
            UserBrowseState.Failed => "failed",
            UserBrowseState.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
}
