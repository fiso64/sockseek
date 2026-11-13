using System.Text.Json;
using System.Collections.Concurrent;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Services;
using System.Runtime.CompilerServices;

namespace Sockseek.Cli;

internal sealed class RemoteCliBackend : ICliBackend, IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly SockseekApiClient api;
    private readonly SockseekLiveClient live;
    private readonly TaskCompletionSource<Exception> connectionFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, byte> workflowSubscriptions = [];
    private readonly ConcurrentDictionary<(
        Guid SourceJobId,
        string Username,
        string FolderPath), SearchViewDirectoryHandle> directoryHandles = [];

    public event Action<DaemonClientUpdate>? StateUpdated;
    public event Action<ActivityEventDto>? ActivityReceived;
    public event Action<StateSnapshotDto>? LiveSnapshotApplied;

    public DaemonClientStore ClientStore => live.Store;

    internal static JsonSerializerOptions CreateJsonOptions()
        => SockseekApiJson.CreateSerializerOptions();

    public RemoteCliBackend(HttpClient http)
    {
        this.http = http;
        var jsonOptions = CreateJsonOptions();
        api = new SockseekApiClient(http, jsonOptions);
        live = new SockseekLiveClient(http, ownsHttp: false, jsonOptions);
        live.Updated += HandleStateUpdate;
        live.ActivityReceived += HandleActivity;
        live.SnapshotApplied += snapshot => LiveSnapshotApplied?.Invoke(snapshot);
        live.ConnectionFailed += error => connectionFailure.TrySetResult(error);
    }

    public async Task AwaitLiveAsync(Task operation)
    {
        if (await Task.WhenAny(operation, connectionFailure.Task).ConfigureAwait(false) == connectionFailure.Task)
            throw await connectionFailure.Task.ConfigureAwait(false);
        await operation.ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await live.DisposeAsync();
        http.Dispose();
    }

    public async Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
    {
        SubmitExtractJobRequestDto prepared = await PrepareExtractRequestAsync(api, request, ct)
            .ConfigureAwait(false);
        return await SubmitAndSubscribeAsync(
            prepared.Options,
            options => api.SubmitExtractJobAsync(prepared with { Options = options }, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<CreateJobPreviewResponseDto> CreateJobPreviewAsync(
        SubmitExtractJobRequestDto request,
        CancellationToken ct = default)
    {
        SubmitExtractJobRequestDto prepared = await PrepareExtractRequestAsync(api, request, ct)
            .ConfigureAwait(false);
        return await api.CreateJobPreviewAsync(
            new CreateJobPreviewRequestDto(
                new ExtractJobDraftDto(
                    prepared.Input,
                    prepared.InputType,
                    ResultDownloadBehavior: prepared.ResultDownloadBehavior,
                    ArtifactId: prepared.ArtifactId),
                prepared.Options),
            ct).ConfigureAwait(false);
    }

    public async Task<JobPreviewSummaryDto> WaitForJobPreviewAsync(
        Guid previewId,
        CancellationToken ct = default)
    {
        while (true)
        {
            JobPreviewSummaryDto preview = await api.GetJobPreviewAsync(previewId, ct, waitForCompletion: true)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The Job Preview disappeared before planning completed.");
            if (preview.State != JobPreviewState.Planning)
                return preview;
        }
    }

    public async IAsyncEnumerable<JobPreviewNodeDto> GetJobPreviewNodesAsync(
        Guid previewId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (JobPreviewNodeDto node in GetJobPreviewChildrenAsync(
            previewId,
            parentRef: null,
            ct).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    private async IAsyncEnumerable<JobPreviewNodeDto> GetJobPreviewChildrenAsync(
        Guid previewId,
        string? parentRef,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            CursorPage<JobPreviewNodeDto> page = await api.GetJobPreviewNodesPageAsync(
                previewId,
                parentRef,
                cursor,
                limit: 200,
                ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The Job Preview disappeared while its nodes were being paged.");
            foreach (JobPreviewNodeDto node in page.Items)
            {
                yield return node;
                if (node.DirectChildCount <= 0)
                    continue;
                await foreach (JobPreviewNodeDto child in GetJobPreviewChildrenAsync(
                    previewId,
                    node.Ref,
                    ct).ConfigureAwait(false))
                {
                    yield return child;
                }
            }
            cursor = page.NextCursor;
        }
        while (cursor != null);
    }

    internal static async Task<SubmitExtractJobRequestDto> PrepareExtractRequestAsync(
        SockseekApiClient api, SubmitExtractJobRequestDto request,
        CancellationToken ct)
    {
        if (request.Input == null || request.ArtifactId != null || !ShouldUploadLocalInput(request))
            return request;

        await using var input = new FileStream(
            request.Input,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        InputArtifactDto artifact = await api.UploadInputArtifactAsync(
            input,
            Path.GetFileName(request.Input),
            ct).ConfigureAwait(false);
        string? inputType = request.InputType;
        if (string.IsNullOrWhiteSpace(inputType)
            || string.Equals(inputType, InputType.None.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            inputType = InputType.CSV.ToString();
        }
        return request with
        {
            ArtifactId = artifact.ArtifactId,
            InputType = inputType,
        };
    }

    private static bool ShouldUploadLocalInput(SubmitExtractJobRequestDto request)
    {
        if (!File.Exists(request.Input))
            return false;
        if (string.IsNullOrWhiteSpace(request.InputType)
            || string.Equals(
                request.InputType,
                InputType.None.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(
                Path.GetExtension(request.Input),
                ".csv",
                StringComparison.OrdinalIgnoreCase);
        }
        return InputTypes.TryParse(request.InputType, out InputType inputType)
            && inputType is InputType.CSV or InputType.List;
    }

    public Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitSearchJobAsync(request with { Options = options }, ct), ct);

    public Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitTrackSearchJobAsync(request with { Options = options }, ct), ct);

    public Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumSearchJobAsync(request with { Options = options }, ct), ct);

    public Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitSongJobAsync(request with { Options = options }, ct), ct);

    public Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumJobAsync(request with { Options = options }, ct), ct);

    public Task<JobSummaryDto> SubmitSongGroupsJobAsync(SubmitSongGroupsJobRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitSongGroupsJobAsync(request with { Options = options }, ct), ct);

    public Task<JobSummaryDto> SubmitAlbumGroupsJobAsync(SubmitAlbumGroupsJobRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumGroupsJobAsync(request with { Options = options }, ct), ct);

    public Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => SubmitAndSubscribeAsync(request.Options, options => api.SubmitJobListAsync(request with { Options = options }, ct), ct);

    public async Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        await live.StartWorkflowAsync(workflowId, ct);
        workflowSubscriptions.TryAdd(workflowId, 0);
    }

    public Task SubscribeAllAsync(CancellationToken ct = default)
        => live.StartDaemonAsync(ct);

    private async Task<JobSummaryDto> SubmitAndSubscribeAsync(
        SubmissionOptionsDto? options,
        Func<SubmissionOptionsDto, Task<JobSummaryDto>> submit,
        CancellationToken ct)
    {
        var workflowId = options?.WorkflowId ?? Guid.NewGuid();
        var scopedOptions = (options ?? new SubmissionOptionsDto()) with { WorkflowId = workflowId };

        if (live.Mode != LiveSubscriptionMode.Daemon)
        {
            bool reusingSubscription = workflowSubscriptions.ContainsKey(workflowId);
            await SubscribeWorkflowAsync(workflowId, ct);
            if (reusingSubscription)
                await live.RefreshWorkflowAsync(workflowId, ct);
        }
        var summary = await submit(scopedOptions);
        if (live.Mode != LiveSubscriptionMode.Daemon && summary.WorkflowId != workflowId)
            await SubscribeWorkflowAsync(summary.WorkflowId, ct);
        return summary;
    }

    public Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
        => api.GetJobsAsync(query, ct);

    public Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
        => api.GetJobDetailAsync(jobId, ct);

    public Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.GetJobDetailByDisplayIdAsync(displayId, workflowId, ct);

    public Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => api.GetWorkflowAsync(workflowId, ct);

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(
        Guid jobId,
        CancellationToken ct = default)
        => GetFileResultsAsync(jobId, new FileSearchProjectionRequestDto(), ct);

    public async Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(
        Guid jobId,
        FileSearchProjectionRequestDto request,
        CancellationToken ct = default)
    {
        SearchViewSummaryDto view = await CreateReadySearchViewAsync(
            jobId,
            new CreateSearchViewRequestDto(
                ServerSearchViewProjectionKind.Files,
                request.SongQuery,
                IncludeFullResults: request.IncludeFullResults),
            ct).ConfigureAwait(false);
        var items = new List<FileCandidateDto>();
        string? cursor = null;
        SearchViewRevisionDto? revision = null;
        do
        {
            SearchViewFilePageDto page = await api.GetSearchViewFilesAsync(
                view.ViewId,
                view.Revision,
                cursor,
                SearchViewPageSize,
                ct: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ResultsExpiredMessage);
            revision ??= page.Revision;
            items.AddRange(page.Items.Select(CliSearchProjectionMapper.ToDto));
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return Snapshot(revision ?? Revision(view), items);
    }

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(
        Guid jobId,
        bool includeFiles,
        CancellationToken ct = default)
        => GetFolderResultsCoreAsync(jobId, null, includeFiles, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(
        Guid jobId,
        FolderSearchProjectionRequestDto request,
        CancellationToken ct = default)
        => GetFolderResultsCoreAsync(jobId, request.AlbumQuery, request.IncludeFiles, ct);

    public Task<SearchResultSnapshotDto<TrackGroupCandidateDto>?> GetTrackGroupResultsAsync(
        Guid jobId,
        CancellationToken ct = default)
        => GetTrackGroupResultsAsync(jobId, new TrackGroupProjectionRequestDto(), ct);

    public async Task<SearchResultSnapshotDto<TrackGroupCandidateDto>?> GetTrackGroupResultsAsync(
        Guid jobId,
        TrackGroupProjectionRequestDto request,
        CancellationToken ct = default)
    {
        SearchViewSummaryDto view = await CreateReadySearchViewAsync(
            jobId,
            new CreateSearchViewRequestDto(
                ServerSearchViewProjectionKind.TrackGroups,
                request.SongQuery),
            ct).ConfigureAwait(false);
        var items = new List<TrackGroupCandidateDto>();
        string? cursor = null;
        SearchViewRevisionDto? revision = null;
        do
        {
            SearchViewTrackGroupPageDto page = await api.GetSearchViewTrackGroupsAsync(
                view.ViewId,
                view.Revision,
                cursor,
                SearchViewPageSize,
                ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ResultsExpiredMessage);
            revision ??= page.Revision;
            foreach (SearchViewTrackGroupDto group in page.Items)
            {
                List<FileCandidateDto>? candidates = request.IncludeCandidates
                    ? await ReadTrackGroupOptionsAsync(view, group.Ref, ct).ConfigureAwait(false)
                    : null;
                items.Add(new TrackGroupCandidateDto(group.Query, null, candidates));
            }
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return Snapshot(revision ?? Revision(view), items);
    }

    public Task<SearchResultSnapshotDto<AlbumGroupCandidateDto>?> GetAlbumGroupResultsAsync(
        Guid jobId,
        CancellationToken ct = default)
        => GetAlbumGroupResultsAsync(jobId, new AlbumGroupProjectionRequestDto(), ct);

    public async Task<SearchResultSnapshotDto<AlbumGroupCandidateDto>?> GetAlbumGroupResultsAsync(
        Guid jobId,
        AlbumGroupProjectionRequestDto request,
        CancellationToken ct = default)
    {
        SearchViewSummaryDto view = await CreateReadySearchViewAsync(
            jobId,
            new CreateSearchViewRequestDto(
                ServerSearchViewProjectionKind.AlbumGroups,
                AlbumQuery: request.AlbumQuery),
            ct).ConfigureAwait(false);
        var items = new List<AlbumGroupCandidateDto>();
        string? cursor = null;
        SearchViewRevisionDto? revision = null;
        do
        {
            SearchViewAlbumGroupPageDto page = await api.GetSearchViewAlbumGroupsAsync(
                view.ViewId,
                view.Revision,
                cursor,
                SearchViewPageSize,
                ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ResultsExpiredMessage);
            revision ??= page.Revision;
            foreach (SearchViewAlbumGroupDto group in page.Items)
            {
                List<AlbumFolderDto>? folders = request.IncludeFolders
                    ? await ReadAlbumGroupOptionsAsync(view, group.Ref, ct).ConfigureAwait(false)
                    : null;
                items.Add(new AlbumGroupCandidateDto(group.Query, null, folders));
            }
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return Snapshot(revision ?? Revision(view), items);
    }

    private async Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsCoreAsync(
        Guid jobId,
        AlbumQueryDto? query,
        bool includeFiles,
        CancellationToken ct)
    {
        SearchViewSummaryDto view = await CreateReadySearchViewAsync(
            jobId,
            new CreateSearchViewRequestDto(
                ServerSearchViewProjectionKind.AlbumDirectories,
                AlbumQuery: query),
            ct).ConfigureAwait(false);
        var items = new List<AlbumFolderDto>();
        string? cursor = null;
        SearchViewRevisionDto? revision = null;
        do
        {
            SearchViewDirectoryPageDto page = await api.GetSearchViewDirectoriesAsync(
                view.ViewId,
                view.Revision,
                cursor,
                SearchViewPageSize,
                ct: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ResultsExpiredMessage);
            revision ??= page.Revision;
            foreach (SearchViewDirectoryDto directory in page.Items)
                items.Add(await ToCliDirectoryAsync(view, directory, includeFiles, ct).ConfigureAwait(false));
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return Snapshot(revision ?? Revision(view), items);
    }

    private async Task<SearchViewSummaryDto> CreateReadySearchViewAsync(
        Guid jobId,
        CreateSearchViewRequestDto request,
        CancellationToken ct)
    {
        SearchViewSummaryDto view = await api.CreateSearchViewAsync(jobId, request, ct)
            .ConfigureAwait(false);
        await foreach (SearchViewSummaryDto summary in live.ObserveSearchViewAsync(view.ViewId, ct).ConfigureAwait(false))
            view = summary;
        return view;
    }

    private async Task<List<FileCandidateDto>> ReadDirectoryFilesAsync(
        SearchViewSummaryDto view,
        string directoryRef,
        CancellationToken ct)
    {
        var files = new List<FileCandidateDto>();
        string? cursor = null;
        do
        {
            SearchViewDirectoryFilePageDto page = await api.GetSearchViewDirectoryFilesAsync(
                view.ViewId,
                directoryRef,
                view.Revision,
                cursor,
                SearchViewPageSize,
                ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ResultsExpiredMessage);
            files.AddRange(page.Items.Select(item => CliSearchProjectionMapper.ToDto(item.File)));
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return files;
    }

    private async Task<List<FileCandidateDto>> ReadTrackGroupOptionsAsync(
        SearchViewSummaryDto view,
        string groupRef,
        CancellationToken ct)
    {
        var files = new List<FileCandidateDto>();
        string? cursor = null;
        do
        {
            SearchViewTrackGroupOptionPageDto page = await api
                .GetSearchViewTrackGroupOptionsAsync(
                    view.ViewId,
                    groupRef,
                    view.Revision,
                    cursor,
                    SearchViewPageSize,
                    ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ResultsExpiredMessage);
            files.AddRange(page.Items.Select(CliSearchProjectionMapper.ToDto));
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return files;
    }

    private async Task<List<AlbumFolderDto>> ReadAlbumGroupOptionsAsync(
        SearchViewSummaryDto view,
        string groupRef,
        CancellationToken ct)
    {
        var folders = new List<AlbumFolderDto>();
        string? cursor = null;
        do
        {
            SearchViewAlbumGroupOptionPageDto page = await api
                .GetSearchViewAlbumGroupOptionsAsync(
                    view.ViewId,
                    groupRef,
                    view.Revision,
                    cursor,
                    SearchViewPageSize,
                    ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ResultsExpiredMessage);
            foreach (SearchViewDirectoryDto directory in page.Items)
                folders.Add(await ToCliDirectoryAsync(view, directory, includeFiles: true, ct).ConfigureAwait(false));
            cursor = page.NextCursor;
        }
        while (cursor != null);
        return folders;
    }

    private async Task<AlbumFolderDto> ToCliDirectoryAsync(
        SearchViewSummaryDto view,
        SearchViewDirectoryDto directory,
        bool includeFiles,
        CancellationToken ct)
    {
        directoryHandles[(
            view.SourceJobId,
            directory.Ref.Username,
            directory.Ref.FolderPath)] = new(
                view.ViewId,
                view.Revision,
                directory.Ref);
        // The old CLI model exposes an audio count even when it omits children.
        // Read the revision-bound child pages to preserve that behavior while
        // the public whole-array contract is removed.
        List<FileCandidateDto> children = await ReadDirectoryFilesAsync(
            view,
            directory.Ref.Ref,
            ct).ConfigureAwait(false);
        return CliSearchProjectionMapper.ToDto(directory, children, includeFiles);
    }

    private static SearchResultSnapshotDto<T> Snapshot<T>(
        SearchViewRevisionDto revision,
        IReadOnlyList<T> items)
        => new(
            revision.SourceRevision,
            revision.IsComplete,
            items,
            revision.RetentionState.ToString());

    private static SearchViewRevisionDto Revision(SearchViewSummaryDto view)
        => new(
            view.ViewId,
            view.Revision,
            view.SourceRevision,
            view.ConsumedSequence,
            view.IsComplete,
            view.RetentionState,
            view.Counters);

    private const string ResultsExpiredMessage =
        "The results snapshot is no longer available (expiry or daemon restart). Request the results again and review the new selection.";
    private const int SearchViewPageSize = 200;
    private sealed record SearchViewDirectoryHandle(
        Guid ViewId,
        long Revision,
        PeerDirectoryRefDto Directory);

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        await PrepareFollowUpSubscriptionAsync(searchJobId, ct);
        SearchViewDirectoryHandle handle = ResolveDirectoryHandle(
            searchJobId,
            request.Folder.Username,
            request.Folder.FolderPath);
        try
        {
            return await api.RetrieveSearchViewDirectoryAsync(
                handle.ViewId,
                new RetrieveSearchViewDirectoryRequestDto(
                    handle.Revision,
                    handle.Directory,
                    Guid.NewGuid()),
                ct).ConfigureAwait(false);
        }
        catch (SockseekApiRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(ResultsExpiredMessage, exception);
        }
    }

    public async Task<RetrieveFolderJobPayloadDto?> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        JobSummaryDto? summary = await StartRetrieveFolderAsync(searchJobId, request, ct)
            .ConfigureAwait(false);
        if (summary == null)
            return null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            JobDetailDto? detail = await api.GetJobDetailAsync(summary.JobId, ct)
                .ConfigureAwait(false);
            if (detail != null && detail.Summary.LifecycleState is not (
                ServerJobLifecycleState.Pending or ServerJobLifecycleState.Running))
            {
                return detail.Payload as RetrieveFolderJobPayloadDto;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private SearchViewDirectoryHandle ResolveDirectoryHandle(
        Guid sourceJobId,
        string username,
        string folderPath)
        => directoryHandles.TryGetValue((sourceJobId, username, folderPath), out var handle)
            ? handle
            : throw new InvalidOperationException(
                "The remote directory must first be read from a revision-bound Search View.");

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
    {
        await PrepareFollowUpSubscriptionAsync(searchJobId, ct);
        return await api.StartFileDownloadsAsync(searchJobId, request, ct);
    }

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
    {
        await PrepareFollowUpSubscriptionAsync(searchJobId, ct);
        return await api.StartFolderDownloadAsync(searchJobId, request, ct);
    }

    private async Task PrepareFollowUpSubscriptionAsync(Guid sourceJobId, CancellationToken ct)
    {
        if (live.Mode != LiveSubscriptionMode.Workflow)
            return;

        var source = await api.GetJobDetailAsync(sourceJobId, ct);
        if (source != null)
            await live.RefreshWorkflowAsync(source.Summary.WorkflowId, ct);
    }

    public Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => api.CompleteManualSelectionAsync(jobId, ct);

    public Task<bool> SkipManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => api.SkipManualSelectionAsync(jobId, ct);

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
        => api.CancelJobAsync(jobId, ct);

    public Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.CancelJobByDisplayIdAsync(displayId, workflowId, ct);

    public Task<int> CancelAllJobsAsync(CancellationToken ct = default)
        => api.CancelAllJobsAsync(ct);

    public Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => api.CancelWorkflowAsync(workflowId, ct);

    public Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
        => api.TryNextCandidateAsync(jobId, ct);

    public Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.TryNextCandidateByDisplayIdAsync(displayId, workflowId, ct);

    private void HandleStateUpdate(DaemonClientUpdate update)
        => StateUpdated?.Invoke(update);

    private void HandleActivity(ActivityEventDto activity)
        => ActivityReceived?.Invoke(activity);
}
