using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Sockseek.Api;
using Sockseek.Core;

namespace Sockseek.Cli;

internal sealed class RemoteCliBackend : ICliBackend, IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly SockseekApiClient api;
    private readonly HubConnection connection;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly WorkflowClientStore workflowStore = new();
    private readonly ConcurrentDictionary<Guid, byte> subscribedWorkflows = [];
    private volatile bool subscribedAll;

    public event Action<ServerEventEnvelopeDto>? EventReceived;
    public event Action<WorkflowClientUpdate>? WorkflowUpdated;

    internal static JsonSerializerOptions CreateJsonOptions()
        => SockseekApiJson.CreateSerializerOptions();

    public RemoteCliBackend(string serverUrl)
    {
        var baseUri = SockseekApiClient.NormalizeServerUrl(serverUrl);
        http = new HttpClient { BaseAddress = baseUri };
        jsonOptions = CreateJsonOptions();
        api = new SockseekApiClient(http, jsonOptions);

        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "api/events"))
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
                SockseekApiJson.ConfigureSerializerOptions(options.PayloadSerializerOptions);
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<ServerEventEnvelopeDto>("serverEvent", envelope =>
        {
            EventReceived?.Invoke(RehydrateEnvelope(envelope));
        });
        connection.On<WorkflowUpdateBatchDto>("workflowUpdateBatch", batch =>
        {
            var update = workflowStore.Apply(RehydrateBatch(batch));
            WorkflowUpdated?.Invoke(update);
            foreach (var envelope in update.Events)
                EventReceived?.Invoke(envelope);

            if (update.SequenceGapDetected)
                _ = HydrateWorkflowSnapshotAsync(update.WorkflowId);
        });
        connection.Reconnected += async _ => await ResubscribeAsync();
    }

    public Task StartAsync(CancellationToken ct = default)
        => connection.StartAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        http.Dispose();
    }

    public async Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitExtractJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitSearchJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitTrackSearchJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumSearchJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitSongJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAggregateJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumAggregateJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitJobListAsync(request with { Options = options }, ct), ct);

    public Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => SubscribeWorkflowCoreAsync(workflowId, ct);

    public Task SubscribeAllAsync(CancellationToken ct = default)
        => SubscribeAllCoreAsync(ct);

    private async Task<JobSummaryDto> SubmitAndSubscribeAsync(
        SubmissionOptionsDto? options,
        Func<SubmissionOptionsDto, Task<JobSummaryDto>> submit,
        CancellationToken ct)
    {
        var workflowId = options?.WorkflowId ?? Guid.NewGuid();
        var scopedOptions = (options ?? new SubmissionOptionsDto()) with { WorkflowId = workflowId };

        await SubscribeWorkflowAsync(workflowId, ct);
        var summary = await submit(scopedOptions);
        if (summary.WorkflowId != workflowId)
            await SubscribeWorkflowAsync(summary.WorkflowId, ct);
        return summary;
    }

    private async Task SubscribeWorkflowCoreAsync(Guid workflowId, CancellationToken ct = default)
    {
        await connection.InvokeAsync("SubscribeWorkflow", workflowId, ct);
        subscribedWorkflows[workflowId] = 0;
    }

    private async Task SubscribeAllCoreAsync(CancellationToken ct = default)
    {
        await connection.InvokeAsync("SubscribeAll", ct);
        subscribedAll = true;
    }

    private async Task ResubscribeAsync()
    {
        if (subscribedAll)
            await connection.InvokeAsync("SubscribeAll");

        foreach (var workflowId in subscribedWorkflows.Keys)
            await connection.InvokeAsync("SubscribeWorkflow", workflowId);
    }

    public Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
        => api.GetJobsAsync(query, ct);

    public Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
        => api.GetJobDetailAsync(jobId, ct);

    public Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.GetJobDetailByDisplayIdAsync(displayId, workflowId, ct);

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        var workflow = await api.GetWorkflowAsync(workflowId, ct);
        if (workflow != null)
            workflowStore.ApplySnapshot(workflow);
        return workflow;
    }

    private async Task HydrateWorkflowSnapshotAsync(Guid workflowId)
    {
        try
        {
            var workflow = await api.GetWorkflowAsync(workflowId, includeAll: true);
            if (workflow == null)
                return;

            var update = workflowStore.ApplySnapshot(workflow, replaceKnownWorkflowJobs: true);
            WorkflowUpdated?.Invoke(update);
            foreach (var envelope in update.Events)
                EventReceived?.Invoke(envelope);
        }
        catch (Exception ex)
        {
            SockseekLog.Debug($"Failed to hydrate workflow snapshot after event sequence gap: {ex.Message}");
        }
    }

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetFileResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
        => api.GetFileResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
        => api.GetFolderResultsAsync(jobId, includeFiles, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => api.GetFolderResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetAggregateTrackResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
        => api.GetAggregateTrackResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetAggregateAlbumResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => api.GetAggregateAlbumResultsAsync(jobId, request, ct);

    public Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
        => api.StartRetrieveFolderAsync(searchJobId, request, ct);

    public Task<int> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
        => api.RetrieveFolderAndWaitAsync(searchJobId, request, ct);

    public Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
        => api.StartFileDownloadsAsync(searchJobId, request, ct);

    public Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
        => api.StartFolderDownloadAsync(searchJobId, request, ct);

    public Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => api.CompleteManualSelectionAsync(jobId, ct);

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
        => api.CancelJobAsync(jobId, ct);

    public Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.CancelJobByDisplayIdAsync(displayId, workflowId, ct);

    public Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => api.CancelWorkflowAsync(workflowId, ct);

    public Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
        => api.TryNextCandidateAsync(jobId, ct);

    public Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.TryNextCandidateByDisplayIdAsync(displayId, workflowId, ct);

    private ServerEventEnvelopeDto RehydrateEnvelope(ServerEventEnvelopeDto envelope)
        => ServerEventPayloadConverter.RehydrateEnvelope(envelope, jsonOptions);

    private WorkflowUpdateBatchDto RehydrateBatch(WorkflowUpdateBatchDto batch)
        => ServerEventPayloadConverter.RehydrateBatch(batch, jsonOptions);
}
