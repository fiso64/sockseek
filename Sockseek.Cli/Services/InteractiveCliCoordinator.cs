using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;
using Soulseek;

namespace Sockseek.Cli;

internal sealed class InteractiveCliCoordinator
{
    private readonly ICliBackend backend;
    private readonly CliSettings cliSettings;
    private readonly CancellationToken appToken;
    private readonly Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride;
    private readonly TimeSpan pollInterval;
    private readonly SemaphoreSlim promptSemaphore = new(1, 1);
    private readonly HashSet<Guid> handledAlbumSearches = [];
    private readonly HashSet<Guid> handledManualSelections = [];
    private readonly Dictionary<Guid, InteractiveAlbumSession> interactiveAlbumSessions = [];
    private SubmissionOptionsDto? rootOptions;
    private bool interactiveEnabled;

    public InteractiveCliCoordinator(
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
        return await backend.SubmitExtractJobAsync(request with
        {
            AutoStartExtractedResult = true,
            ResultDownloadBehavior = InteractiveDownloadBehavior(request.ResultDownloadBehavior),
        }, ct);
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
            if (summary.Kind == ServerJobKind.Search
                && IsCompleted(summary.State)
                && handledAlbumSearches.Add(summary.JobId))
            {
                await HandleCompletedSearchAsync(summary.JobId, ct);
                startedFollowUp = true;
            }

            if (summary.State == ServerProtocol.JobStates.AwaitingSelection
                && handledManualSelections.Add(summary.JobId))
            {
                if (summary.Kind == ServerJobKind.Album)
                {
                    await HandleManualAlbumJobAsync(summary.JobId, ct);
                    startedFollowUp = true;
                }
                else if (summary.Kind == ServerJobKind.AlbumAggregate)
                {
                    await HandleManualAlbumAggregateJobAsync(summary.JobId, ct);
                    startedFollowUp = true;
                }
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
        var detail = await backend.GetJobDetailAsync(searchJobId, ct);
        if (detail?.Payload is not SearchJobPayloadDto search)
            return;

        if (!interactiveEnabled || search.DefaultFolderProjection == null)
            return;

        var projection = await backend.GetFolderResultsAsync(
            searchJobId,
            search.DefaultFolderProjection with { IncludeFiles = true },
            ct);
        var folders = projection?.Items.Select(ToAlbumFolder).ToList() ?? [];
        if (folders.Count == 0)
        {
            if (ConsoleInputManager.Reporter != null)
                ConsoleInputManager.Reporter.ReportSyntheticJobFailure(detail.Summary.DisplayId, "AlbumJob", search.QueryText, "No suitable file found");
            return;
        }

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

    private async Task HandleManualAlbumJobAsync(Guid albumJobId, CancellationToken ct)
    {
        var detail = await backend.GetJobDetailAsync(albumJobId, ct);
        if (detail?.Payload is not AlbumJobPayloadDto album)
            return;

        var projection = await backend.GetFolderResultsAsync(albumJobId, includeFiles: true, ct);
        var folders = projection?.Items.Select(ToAlbumFolder).ToList() ?? [];
        if (folders.Count == 0)
        {
            if (ConsoleInputManager.Reporter != null)
                ConsoleInputManager.Reporter.ReportSyntheticJobFailure(detail.Summary.DisplayId, "AlbumJob", detail.Summary.QueryText ?? "", "No suitable file found");
            await backend.CompleteManualSelectionAsync(albumJobId, ct);
            return;
        }

        var session = new InteractiveAlbumSession(
            albumJobId,
            new AlbumJob(ToAlbumQuery(album.Query)) { ItemName = detail.Summary.ItemName, Results = folders },
            album.Query,
            folders,
            OptionsForWorkflow(detail.Summary.WorkflowId));

        var selected = interactiveEnabled
            ? await PromptForAlbumSelectionAsync(session)
            : new InteractiveAlbumSelection(folders[0], RetrieveCurrentFolder: true, SkipTrackCountVerification: false);

        if (selected == null)
        {
            await backend.CompleteManualSelectionAsync(albumJobId, ct);
            return;
        }

        await EnqueueInteractiveAlbumJobAsync(session, selected, ct);
    }

    private async Task HandleManualAlbumAggregateJobAsync(Guid albumAggregateJobId, CancellationToken ct)
    {
        var detail = await backend.GetJobDetailAsync(albumAggregateJobId, ct);
        if (detail?.Payload is not AlbumAggregateJobPayloadDto albumAggregate)
            return;

        var projection = await backend.GetAggregateAlbumResultsAsync(
            albumAggregateJobId,
            new AggregateAlbumProjectionRequestDto(albumAggregate.Query, IncludeFolders: true),
            ct);

        var buckets = projection?.Items
            .Where(album => album.Folders is { Count: > 0 })
            .ToList() ?? [];

        if (buckets.Count == 0)
        {
            if (ConsoleInputManager.Reporter != null)
                ConsoleInputManager.Reporter.ReportSyntheticJobFailure(detail.Summary.DisplayId, "AlbumAggregateJob", detail.Summary.QueryText ?? "", "No suitable file found");
            await backend.CompleteManualSelectionAsync(albumAggregateJobId, ct);
            return;
        }

        foreach (var bucket in buckets)
        {
            var folders = bucket.Folders!.Select(ToAlbumFolder).ToList();
            var session = new InteractiveAlbumSession(
                albumAggregateJobId,
                new AlbumJob(ToAlbumQuery(bucket.Query)) { ItemName = bucket.ItemName, Results = folders },
                bucket.Query,
                folders,
                OptionsForWorkflow(detail.Summary.WorkflowId));

            var selected = interactiveEnabled
                ? await PromptForAlbumSelectionAsync(session)
                : new InteractiveAlbumSelection(folders[0], RetrieveCurrentFolder: true, SkipTrackCountVerification: false);

            if (selected == null)
                continue;

            await EnqueueInteractiveAlbumJobAsync(session, selected, ct);
        }

        await backend.CompleteManualSelectionAsync(albumAggregateJobId, ct);
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
        InteractiveAlbumSelection selected,
        CancellationToken ct)
    {
        var selectedFolder = selected.Folder;
        bool exactFiles = !selected.RetrieveCurrentFolder;
        var selectedFiles = !exactFiles
            ? null
            : selectedFolder.Files
                .Select(song => song.ResolvedTarget)
                .OfType<FileCandidate>()
                .Select(candidate => new FileCandidateRefDto(candidate.Username, candidate.Filename))
                .ToList();
        var selection = selected.SkipTrackCountVerification || exactFiles
            ? new AlbumFolderDownloadSelectionDto(
                selectedFiles,
                ExactFiles: exactFiles,
                SkipTrackCountVerification: selected.SkipTrackCountVerification || exactFiles)
            : null;

        var summary = await backend.StartFolderDownloadAsync(
            session.SourceSearchJobId,
            new StartFolderDownloadRequestDto(
                new AlbumFolderRefDto(selectedFolder.Username, selectedFolder.FolderPath),
                Options: session.Options,
                AlbumQuery: session.Query,
                Selection: selection),
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

    private async Task<InteractiveAlbumSelection?> PromptForAlbumSelectionAsync(InteractiveAlbumSession session)
    {
        var availableFolders = session.Folders
            .Where(folder => !session.ExcludedFolderKeys.Contains(FolderKey(folder)))
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
                using var pause = ConsoleInputManager.PauseConsoleOutput();
                using var interaction = await ConsoleInputManager.AcquireConsoleInteractionAsync(appToken);
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

            session.FilterStr = result.FilterStr;
            if (result.ExitInteractiveMode)
            {
                interactiveEnabled = false;
                cliSettings.InteractiveMode = false;
            }

            if (result.Index < 0 || result.Folder == null)
                return null;

            return new InteractiveAlbumSelection(result.Folder, result.RetrieveCurrentFolder, SkipTrackCountVerification: true);
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

    internal static string FolderKey(AlbumFolder folder)
        => folder.Username + "\\" + folder.FolderPath;

    private static AlbumQuery ToAlbumQuery(AlbumQueryDto query) => new()
    {
        Artist = query.Artist ?? "",
        Album = query.Album ?? "",
        SearchHint = query.SearchHint ?? "",
        URI = query.Uri ?? "",
        ArtistMaybeWrong = query.ArtistMaybeWrong,
    };

    internal static AlbumFolder ToAlbumFolder(AlbumFolderDto folder)
        => new AlbumFolder(
            folder.Username,
            folder.FolderPath,
            () => folder.Files?.Select(ToSongJob).ToList() ?? [])
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };

    private static SongJob ToSongJob(FileCandidateDto file)
    {
        var candidate = new FileCandidate(
            new SearchResponse(file.Username, -1, file.Peer.HasFreeUploadSlot ?? false, file.Peer.UploadSpeed ?? -1, -1, null),
            new Soulseek.File(0, file.Filename, file.Size, file.Extension ?? Path.GetExtension(file.Filename),
                file.Attributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));
        var query = Searcher.InferSongQuery(candidate.Filename, new SongQuery());
        return new SongJob(query) { ResolvedTarget = candidate };
    }

    private static DownloadBehaviorPolicyDto InteractiveDownloadBehavior(DownloadBehaviorPolicyDto? existing)
        => existing == null
            ? new DownloadBehaviorPolicyDto(Album: DownloadBehavior.Manual, AlbumAggregate: DownloadBehavior.Manual)
            : existing with { Album = DownloadBehavior.Manual, AlbumAggregate = DownloadBehavior.Manual };

    private SubmissionOptionsDto OptionsForWorkflow(Guid workflowId)
        => (rootOptions ?? new SubmissionOptionsDto()) with { WorkflowId = workflowId };

    private static bool IsActive(ServerJobState state)
        => state is ServerProtocol.JobStates.Pending
            or ServerProtocol.JobStates.Extracting
            or ServerProtocol.JobStates.Searching
            or ServerProtocol.JobStates.Downloading
            or ServerProtocol.JobStates.Running
            or ServerProtocol.JobStates.AwaitingSelection;

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

internal sealed record InteractiveAlbumPromptRequest(
    Job PromptJob,
    List<AlbumFolder> Folders,
    HashSet<string> RetrievedFolders,
    string? FilterStr);

internal sealed record InteractiveAlbumSelection(
    AlbumFolder Folder,
    bool RetrieveCurrentFolder,
    bool SkipTrackCountVerification);
