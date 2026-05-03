using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Server;

namespace Sldl.Cli;

internal sealed class RemoteInteractiveCliCoordinator
{
    private readonly ICliBackend backend;
    private readonly CliSettings cliSettings;
    private readonly CancellationToken appToken;
    private readonly Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride;
    private readonly TimeSpan pollInterval;
    private readonly SemaphoreSlim promptSemaphore = new(1, 1);
    private readonly HashSet<Guid> startedExtractResults = [];
    private readonly HashSet<Guid> handledAlbumSearches = [];
    private readonly Dictionary<Guid, InteractiveAlbumSession> interactiveAlbumSessions = [];
    private SubmissionOptionsDto? rootOptions;
    private bool interactiveEnabled;

    public RemoteInteractiveCliCoordinator(
        ICliBackend backend,
        CliSettings cliSettings,
        CancellationToken appToken,
        Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride = null,
        TimeSpan? pollInterval = null)
    {
        this.backend = backend;
        this.cliSettings = cliSettings;
        this.appToken = appToken;
        this.promptOverride = promptOverride;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(150);
        interactiveEnabled = cliSettings.InteractiveMode;
    }

    public async Task<JobSummaryDto> StartAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        rootOptions = request.Options;
        return await backend.SubmitExtractJobAsync(request with { AutoStartExtractedResult = false }, ct);
    }

    public async Task RunUntilCompleteAsync(Guid workflowId, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool startedFollowUp = await ProcessWorkflowAsync(workflowId, ct);

            var workflow = await backend.GetWorkflowAsync(workflowId, ct);
            if (!startedFollowUp && (workflow?.Summary.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed))
            {
                startedFollowUp = await ProcessWorkflowAsync(workflowId, ct);
                workflow = await backend.GetWorkflowAsync(workflowId, ct);
                if (!startedFollowUp && (workflow?.Summary.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed))
                    return;
            }

            if (pollInterval > TimeSpan.Zero)
                await Task.Delay(pollInterval, ct);
        }
    }

    private async Task<bool> ProcessWorkflowAsync(Guid workflowId, CancellationToken ct)
    {
        bool startedFollowUp = false;
        var summaries = await GetWorkflowJobsAsync(workflowId, ct);

        foreach (var summary in summaries.OrderBy(job => job.DisplayId))
        {
            ct.ThrowIfCancellationRequested();
            if (summary.Kind == ServerJobKind.Extract
                && IsCompleted(summary.State)
                && summary.ResultJobId != null
                && startedExtractResults.Add(summary.JobId))
            {
                var detail = await backend.GetJobDetailAsync(summary.JobId, ct);
                if (detail?.Payload is ExtractJobPayloadDto { ResultDraft: not null } extract)
                {
                    var draft = ToInteractiveDraft(extract.ResultDraft);
                    var options = OptionsForWorkflow(summary.WorkflowId);
                    if (draft is JobListJobDraftDto list)
                    {
                        foreach (var child in list.Jobs)
                            await SubmitDraftAsync(child, options, ct);
                    }
                    else
                    {
                        await SubmitDraftAsync(draft, options, ct);
                    }

                    startedFollowUp = true;
                }
            }

            if (summary.Kind == ServerJobKind.Search
                && IsCompleted(summary.State)
                && handledAlbumSearches.Add(summary.JobId))
            {
                await HandleCompletedSearchAsync(summary.JobId, ct);
                startedFollowUp = true;
            }

            if (summary.Kind == ServerJobKind.Album
                && !IsActive(summary.State)
                && interactiveAlbumSessions.TryGetValue(summary.JobId, out var session))
            {
                interactiveAlbumSessions.Remove(summary.JobId);
                await HandleCompletedInteractiveAlbumAsync(summary.JobId, session, ct);
                startedFollowUp = true;
            }
        }

        return startedFollowUp;
    }

    private async Task<IReadOnlyList<JobSummaryDto>> GetWorkflowJobsAsync(Guid workflowId, CancellationToken ct)
        => await backend.GetJobsAsync(new JobQuery(null, null, workflowId, IncludeAll: true), ct);

    private async Task HandleCompletedSearchAsync(Guid searchJobId, CancellationToken ct)
    {
        if (!interactiveEnabled)
            return;

        var detail = await backend.GetJobDetailAsync(searchJobId, ct);
        if (detail?.Payload is not SearchJobPayloadDto search || search.DefaultFolderProjection == null)
            return;

        var projection = await backend.GetFolderResultsAsync(
            searchJobId,
            search.DefaultFolderProjection with { IncludeFiles = true },
            ct);
        var folders = projection?.Items.Select(InteractiveCliCoordinator.ToAlbumFolder).ToList() ?? [];
        if (folders.Count == 0)
            return;

        var promptJob = ToSearchJob(search);
        var session = new InteractiveAlbumSession(
            searchJobId,
            promptJob,
            search.DefaultFolderProjection.AlbumQuery,
            folders,
            OptionsForWorkflow(detail.Summary.WorkflowId));
        var selected = await PromptForAlbumSelectionAsync(session);
        if (selected == null)
            return;

        await EnqueueInteractiveAlbumJobAsync(session, selected, ct);
    }

    private async Task HandleCompletedInteractiveAlbumAsync(
        Guid albumJobId,
        InteractiveAlbumSession session,
        CancellationToken ct)
    {
        if (appToken.IsCancellationRequested)
            return;

        var detail = await backend.GetJobDetailAsync(albumJobId, ct);
        if (detail?.Summary.State == ServerProtocol.JobStates.Done)
            return;

        if (detail?.Summary.FailureReason == ServerProtocol.FailureReasons.Cancelled)
            return;

        if (detail?.Payload is AlbumJobPayloadDto album
            && !string.IsNullOrWhiteSpace(album.ResolvedFolderUsername)
            && !string.IsNullOrWhiteSpace(album.ResolvedFolderPath))
        {
            session.ExcludedFolderKeys.Add(album.ResolvedFolderUsername + "\\" + album.ResolvedFolderPath);
        }

        var selected = await PromptForAlbumSelectionAsync(session);
        if (selected == null)
            return;

        await EnqueueInteractiveAlbumJobAsync(session, selected, ct);
    }

    private async Task EnqueueInteractiveAlbumJobAsync(
        InteractiveAlbumSession session,
        AlbumFolder selected,
        CancellationToken ct)
    {
        var summary = await backend.StartFolderDownloadAsync(
            session.SourceSearchJobId,
            new StartFolderDownloadRequestDto(
                new AlbumFolderRefDto(selected.Username, selected.FolderPath),
                Options: session.Options,
                AlbumQuery: session.Query),
            ct);

        if (summary == null)
            throw new InvalidOperationException("Failed to start interactive album download.");

        interactiveAlbumSessions[summary.JobId] = session;
    }

    private static async Task<int> RunPromptRetrieveFolderAsync(AlbumFolder folder, Func<Task<int>> retrieve)
    {
        Printing.WriteLine($"RetrieveFolderJob: retrieving folder: {folder.FolderPath}", ConsoleColor.Gray, force: true);
        int newFiles = await retrieve();
        folder.IsFullyRetrieved = true;
        string status = newFiles > 0 ? "found additional files in" : "no additional files found";
        Printing.WriteLine($"RetrieveFolderJob: {status}: {folder.FolderPath}", ConsoleColor.Gray, force: true);
        return newFiles;
    }

    private async Task<AlbumFolder?> PromptForAlbumSelectionAsync(InteractiveAlbumSession session)
    {
        var availableFolders = session.Folders
            .Where(folder => !session.ExcludedFolderKeys.Contains(InteractiveCliCoordinator.FolderKey(folder)))
            .ToList();

        if (availableFolders.Count == 0)
            return null;

        await promptSemaphore.WaitAsync(appToken);
        try
        {
            InteractiveModeManager.RunResult result;
            if (promptOverride != null)
            {
                result = await promptOverride(new InteractiveAlbumPromptRequest(
                    session.PromptJob,
                    availableFolders,
                    session.RetrievedFolders,
                    session.FilterStr));
            }
            else
            {
                if (ConsoleInputManager.Reporter != null)
                    ConsoleInputManager.Reporter.IsPaused = true;

                Printing.SetBuffering(true);
                try
                {
                    var interactive = new InteractiveModeManager(
                        session.PromptJob,
                        new JobList(),
                        availableFolders,
                        canRetrieve: true,
                        retrievedFolders: session.RetrievedFolders,
                        retrieveFolderCallback: async folder => await RunPromptRetrieveFolderAsync(
                            folder,
                            async () => await backend.RetrieveFolderAndWaitAsync(
                                session.SourceSearchJobId,
                                new RetrieveFolderRequestDto(
                                    new AlbumFolderRefDto(folder.Username, folder.FolderPath),
                                    session.Query),
                                appToken)),
                        filterStr: session.FilterStr);

                    result = await interactive.Run();
                }
                finally
                {
                    Printing.SetBuffering(false);
                    Printing.Flush();
                    if (ConsoleInputManager.Reporter != null)
                        ConsoleInputManager.Reporter.IsPaused = false;
                }
            }

            session.FilterStr = result.FilterStr;
            if (result.ExitInteractiveMode)
            {
                interactiveEnabled = false;
                cliSettings.InteractiveMode = false;
            }

            if (result.Index < 0 || result.Folder == null)
                return null;

            return result.Folder;
        }
        finally
        {
            promptSemaphore.Release();
        }
    }

    private static SearchJob ToSearchJob(SearchJobPayloadDto payload)
    {
        var job = payload.DefaultFolderProjection != null
            ? new SearchJob(new AlbumQuery
            {
                Artist = payload.DefaultFolderProjection.AlbumQuery.Artist ?? "",
                Album = payload.DefaultFolderProjection.AlbumQuery.Album ?? "",
                SearchHint = payload.DefaultFolderProjection.AlbumQuery.SearchHint ?? "",
                URI = payload.DefaultFolderProjection.AlbumQuery.Uri ?? "",
                ArtistMaybeWrong = payload.DefaultFolderProjection.AlbumQuery.ArtistMaybeWrong,
            })
            : new SearchJob(new SongQuery
            {
                Artist = payload.DefaultFileProjection?.SongQuery?.Artist ?? "",
                Title = payload.DefaultFileProjection?.SongQuery?.Title ?? payload.QueryText,
                Album = payload.DefaultFileProjection?.SongQuery?.Album ?? "",
                URI = payload.DefaultFileProjection?.SongQuery?.Uri ?? "",
                Length = payload.DefaultFileProjection?.SongQuery?.Length ?? -1,
                ArtistMaybeWrong = payload.DefaultFileProjection?.SongQuery?.ArtistMaybeWrong ?? false,
            });

        return job;
    }

    private async Task<JobSummaryDto> SubmitDraftAsync(JobDraftDto draft, SubmissionOptionsDto options, CancellationToken ct)
        => draft switch
        {
            ExtractJobDraftDto extract => await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(extract.Input, extract.InputType, extract.AutoStartExtractedResult, options),
                ct),
            TrackSearchJobDraftDto search => await backend.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(search.SongQuery, search.IncludeFullResults, options),
                ct),
            AlbumSearchJobDraftDto search => await backend.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(search.AlbumQuery, options),
                ct),
            SongJobDraftDto song => await backend.SubmitSongJobAsync(
                new SubmitSongJobRequestDto(song.SongQuery, options),
                ct),
            AlbumJobDraftDto album => await backend.SubmitAlbumJobAsync(
                new SubmitAlbumJobRequestDto(album.AlbumQuery, options),
                ct),
            AggregateJobDraftDto aggregate => await backend.SubmitAggregateJobAsync(
                new SubmitAggregateJobRequestDto(aggregate.SongQuery, options),
                ct),
            AlbumAggregateJobDraftDto aggregate => await backend.SubmitAlbumAggregateJobAsync(
                new SubmitAlbumAggregateJobRequestDto(aggregate.AlbumQuery, options),
                ct),
            JobListJobDraftDto list => await backend.SubmitJobListAsync(
                new SubmitJobListRequestDto(list.Name, list.Jobs, options),
                ct),
            _ => throw new InvalidOperationException($"Unsupported extracted job draft type '{draft.GetType().Name}'."),
        };

    private static JobDraftDto ToInteractiveDraft(JobDraftDto draft)
        => draft switch
        {
            AlbumJobDraftDto album =>
                new AlbumSearchJobDraftDto(album.AlbumQuery),
            ExtractJobDraftDto extract =>
                extract with { AutoStartExtractedResult = false },
            JobListJobDraftDto list =>
                list with { Jobs = list.Jobs.Select(ToInteractiveDraft).ToList() },
            _ => draft,
        };

    private SubmissionOptionsDto OptionsForWorkflow(Guid workflowId)
        => (rootOptions ?? new SubmissionOptionsDto()) with { WorkflowId = workflowId };

    private static bool IsActive(ServerJobState state)
        => state is ServerProtocol.JobStates.Pending
            or ServerProtocol.JobStates.Extracting
            or ServerProtocol.JobStates.Searching
            or ServerProtocol.JobStates.Downloading;

    private static bool IsCompleted(ServerJobState state)
        => state is ServerProtocol.JobStates.Done
            or ServerProtocol.JobStates.AlreadyExists;

    private sealed class InteractiveAlbumSession
    {
        public Guid SourceSearchJobId { get; }
        public Job PromptJob { get; }
        public AlbumQueryDto Query { get; }
        public List<AlbumFolder> Folders { get; }
        public SubmissionOptionsDto Options { get; }
        public HashSet<string> ExcludedFolderKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RetrievedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FilterStr { get; set; }

        public InteractiveAlbumSession(
            Guid sourceSearchJobId,
            Job promptJob,
            AlbumQueryDto query,
            List<AlbumFolder> folders,
            SubmissionOptionsDto options)
        {
            SourceSearchJobId = sourceSearchJobId;
            PromptJob = promptJob;
            Query = query;
            Folders = folders;
            Options = options;
        }
    }
}
