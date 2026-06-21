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
    private bool skipRemainingNewAlbumPrompts;

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
                && IsCompleted(summary.TerminalOutcome, summary.SkipReason)
                && handledAlbumSearches.Add(summary.JobId))
            {
                await HandleCompletedSearchAsync(summary.JobId, ct);
                startedFollowUp = true;
            }

            if (summary.LifecycleState == ServerJobLifecycleState.AwaitingSelection
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
                && !IsActive(summary.LifecycleState)
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
        => await backend.GetJobsAsync(new JobQuery(null, null, null, workflowId, IncludeAll: true), ct);

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
                ConsoleInputManager.Reporter.ReportSyntheticJobFailure(detail.Summary.DisplayId, "AlbumJob", search.QueryText, "No matching results");
            return;
        }

        var promptJob = ToSearchJob(search);
        var session = new InteractiveAlbumSession(
            searchJobId,
            promptJob,
            search.DefaultFolderProjection.AlbumQuery,
            folders,
            OptionsForWorkflow(detail.Summary.WorkflowId),
            InteractiveAlbumResultKind.Folder);
        var outcome = await PromptForAlbumSelectionAsync(session, InteractiveAlbumPromptPurpose.NewAlbumPrompt);
        if (outcome.Selection == null)
            return;

        await EnqueueInteractiveAlbumJobAsync(session, outcome.Selection, ct);
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
                ConsoleInputManager.Reporter.ReportSyntheticJobFailure(detail.Summary.DisplayId, "AlbumJob", detail.Summary.QueryText ?? "", "No matching results");
            await backend.CompleteManualSelectionAsync(albumJobId, ct);
            return;
        }

        var purpose = interactiveAlbumSessions.TryGetValue(albumJobId, out var session)
            ? InteractiveAlbumPromptPurpose.RetryAcceptedAlbumPrompt
            : InteractiveAlbumPromptPurpose.NewAlbumPrompt;

        if (session == null)
        {
            session = new InteractiveAlbumSession(
                albumJobId,
                new AlbumJob(ToAlbumQuery(album.Query)) { ItemName = detail.Summary.ItemName, Results = folders },
                album.Query,
                folders,
                OptionsForWorkflow(detail.Summary.WorkflowId),
                InteractiveAlbumResultKind.Folder);
        }
        else
        {
            session.Folders.Clear();
            session.Folders.AddRange(folders);
            if (session.PromptJob is AlbumJob promptAlbum)
                promptAlbum.Results = folders;
        }

        var outcome = interactiveEnabled
            ? await PromptForAlbumSelectionAsync(session, purpose)
            : new InteractiveAlbumPromptOutcome(
                new InteractiveAlbumSelection(folders[0], RetrieveCurrentFolder: true, SkipTrackCountVerification: false),
                WasExplicitSkip: false);

        if (outcome.Selection == null)
        {
            if (outcome.WasExplicitSkip)
                await backend.SkipManualSelectionAsync(albumJobId, ct);
            else
                await backend.CompleteManualSelectionAsync(albumJobId, ct);
            return;
        }

        await EnqueueInteractiveAlbumJobAsync(session, outcome.Selection, ct);
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
                ConsoleInputManager.Reporter.ReportSyntheticJobFailure(detail.Summary.DisplayId, "AlbumAggregateJob", detail.Summary.QueryText ?? "", "No matching results");
            await backend.CompleteManualSelectionAsync(albumAggregateJobId, ct);
            return;
        }

        var selectedAnyBucket = false;
        var skippedAnyBucket = false;

        foreach (var bucket in buckets)
        {
            var folders = bucket.Folders!.Select(ToAlbumFolder).ToList();
            var session = new InteractiveAlbumSession(
                albumAggregateJobId,
                new AlbumJob(ToAlbumQuery(bucket.Query)) { ItemName = bucket.ItemName, Results = folders },
                bucket.Query,
                folders,
                OptionsForWorkflow(detail.Summary.WorkflowId),
                InteractiveAlbumResultKind.AggregateAlbum);

            var outcome = interactiveEnabled
                ? await PromptForAlbumSelectionAsync(session, InteractiveAlbumPromptPurpose.NewAlbumPrompt)
                : new InteractiveAlbumPromptOutcome(
                    new InteractiveAlbumSelection(folders[0], RetrieveCurrentFolder: true, SkipTrackCountVerification: false),
                    WasExplicitSkip: false);

            if (outcome.Selection == null)
            {
                if (outcome.WasExplicitSkip)
                    skippedAnyBucket = true;
                continue;
            }

            selectedAnyBucket = true;
            await EnqueueInteractiveAlbumJobAsync(session, outcome.Selection, ct);
        }

        if (!selectedAnyBucket && skippedAnyBucket)
            await backend.SkipManualSelectionAsync(albumAggregateJobId, ct);
        else
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
        if (detail?.Summary is { } summary && IsCompleted(summary.TerminalOutcome, summary.SkipReason))
            return;

        if (detail?.Summary.FailureReason == ServerProtocol.FailureReasons.Cancelled)
            return;

        if (detail?.Payload is AlbumJobPayloadDto album
            && !string.IsNullOrWhiteSpace(album.ResolvedFolderUsername)
            && !string.IsNullOrWhiteSpace(album.ResolvedFolderPath))
        {
            session.ExcludedFolderKeys.Add(album.ResolvedFolderUsername + "\\" + album.ResolvedFolderPath);
        }

        var outcome = await PromptForAlbumSelectionAsync(session, InteractiveAlbumPromptPurpose.RetryAcceptedAlbumPrompt);
        if (outcome.Selection == null)
            return;

        await EnqueueInteractiveAlbumJobAsync(session, outcome.Selection, ct);
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
                Selection: selection,
                SelectedFolder: selectedFolder.IsFullyRetrieved ? ToAlbumFolderDto(selectedFolder) : null),
            ct);

        if (summary == null)
            throw new InvalidOperationException("Failed to start interactive album download.");

        handledManualSelections.Remove(summary.JobId);
        interactiveAlbumSessions[summary.JobId] = session;
    }

    private async Task<InteractiveModeManager.RetrievedFolder> RunPromptRetrieveFolderAsync(InteractiveAlbumSession session, AlbumFolder folder, CancellationToken ct)
    {
        Printing.WriteLine($"RetrieveFolderJob: retrieving folder: {folder.FolderPath}", ConsoleColor.Gray, force: true);

        var payload = await backend.RetrieveFolderAndWaitAsync(
            session.SourceSearchJobId,
            new RetrieveFolderRequestDto(
                new AlbumFolderRefDto(folder.Username, folder.FolderPath),
                session.Query),
            ct);

        var retrievedFolder = payload?.Folder != null
            ? ToAlbumFolder(payload.Folder)
            : folder;

        await RefreshRetrievedFolderAsync(session, retrievedFolder, ct);
        retrievedFolder.IsFullyRetrieved = true;
        return new InteractiveModeManager.RetrievedFolder(
            retrievedFolder,
            payload?.NewFilesFoundCount ?? 0);
    }

    private async Task RefreshRetrievedFolderAsync(InteractiveAlbumSession session, AlbumFolder folder, CancellationToken ct)
    {
        AlbumFolder? refreshed = session.ResultKind switch
        {
            InteractiveAlbumResultKind.Folder => await RefreshFolderProjectionAsync(session, folder, ct),
            InteractiveAlbumResultKind.AggregateAlbum => await RefreshAggregateAlbumProjectionAsync(session, folder, ct),
            _ => null,
        };

        if (refreshed == null)
            return;

        MergeFolderInPlace(folder, refreshed);
        var idx = session.Folders.FindIndex(candidate => FolderKey(candidate).Equals(FolderKey(folder), StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            session.Folders[idx] = folder;
    }

    private async Task<AlbumFolder?> RefreshFolderProjectionAsync(InteractiveAlbumSession session, AlbumFolder folder, CancellationToken ct)
    {
        var projection = await backend.GetFolderResultsAsync(
            session.SourceSearchJobId,
            new FolderSearchProjectionRequestDto(session.Query, IncludeFiles: true),
            ct);
        return projection?.Items
            .Select(ToAlbumFolder)
            .FirstOrDefault(candidate => FolderKey(candidate).Equals(FolderKey(folder), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<AlbumFolder?> RefreshAggregateAlbumProjectionAsync(InteractiveAlbumSession session, AlbumFolder folder, CancellationToken ct)
    {
        var projection = await backend.GetAggregateAlbumResultsAsync(
            session.SourceSearchJobId,
            new AggregateAlbumProjectionRequestDto(session.Query, IncludeFolders: true),
            ct);
        return projection?.Items
            .SelectMany(album => album.Folders ?? [])
            .Select(ToAlbumFolder)
            .FirstOrDefault(candidate => FolderKey(candidate).Equals(FolderKey(folder), StringComparison.OrdinalIgnoreCase));
    }

    private static void MergeFolderInPlace(AlbumFolder target, AlbumFolder refreshed)
    {
        target.Files.Clear();
        target.Files.AddRange(refreshed.Files);
        target.IsFullyRetrieved = refreshed.IsFullyRetrieved;
    }

    private async Task<InteractiveAlbumPromptOutcome> PromptForAlbumSelectionAsync(
        InteractiveAlbumSession session,
        InteractiveAlbumPromptPurpose purpose)
    {
        if (skipRemainingNewAlbumPrompts && purpose == InteractiveAlbumPromptPurpose.NewAlbumPrompt)
            return new InteractiveAlbumPromptOutcome(null, WasExplicitSkip: true);

        var availableFolders = session.Folders
            .Where(folder => !session.ExcludedFolderKeys.Contains(FolderKey(folder)))
            .ToList();

        if (availableFolders.Count == 0)
            return new InteractiveAlbumPromptOutcome(null, WasExplicitSkip: false);

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
                    session.FilterStr,
                    purpose));
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
                    retrieveFolderCallback: async folder => await RunPromptRetrieveFolderAsync(session, folder, appToken),
                    filterStr: session.FilterStr);

                result = await interactive.Run();
            }

            session.FilterStr = result.FilterStr;
            if (result.Action == InteractiveModeManager.RunAction.ExitInteractiveMode)
            {
                interactiveEnabled = false;
                cliSettings.InteractiveMode = false;
            }

            if (result.Action == InteractiveModeManager.RunAction.SkipRemainingNewPrompts)
                skipRemainingNewAlbumPrompts = true;

            if (result.Action != InteractiveModeManager.RunAction.Accept
                && result.Action != InteractiveModeManager.RunAction.ExitInteractiveMode)
                return new InteractiveAlbumPromptOutcome(
                    null,
                    WasExplicitSkip: result.Action is InteractiveModeManager.RunAction.SkipCurrent or InteractiveModeManager.RunAction.SkipRemainingNewPrompts);

            if (result.Folder == null)
                return new InteractiveAlbumPromptOutcome(null, WasExplicitSkip: false);

            return new InteractiveAlbumPromptOutcome(
                new InteractiveAlbumSelection(result.Folder, result.RetrieveCurrentFolder, SkipTrackCountVerification: true),
                WasExplicitSkip: false);
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

    private static AlbumFolderDto ToAlbumFolderDto(AlbumFolder folder)
        => new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            new PeerInfoDto(
                folder.Username,
                folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.HasFreeUploadSlot,
                folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.UploadSpeed),
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            folder.Files
                .Select(song => song.ResolvedTarget)
                .OfType<FileCandidate>()
                .Select(ToFileCandidateDto)
                .ToList(),
            folder.IsFullyRetrieved);

    private static SongJob ToSongJob(FileCandidateDto file)
    {
        var candidate = new FileCandidate(
            new SearchResponse(file.Username, -1, file.Peer.HasFreeUploadSlot ?? false, file.Peer.UploadSpeed ?? -1, -1, null),
            new Soulseek.File(0, file.Filename, file.Size, file.Extension ?? Path.GetExtension(file.Filename),
                file.Attributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));
        var query = Searcher.InferSongQuery(candidate.Filename, new SongQuery());
        return new SongJob(query) { ResolvedTarget = candidate };
    }

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate)
        => new(
            new FileCandidateRefDto(candidate.Username, candidate.Filename),
            candidate.Username,
            candidate.Filename,
            new PeerInfoDto(candidate.Username, candidate.Response.HasFreeUploadSlot, candidate.Response.UploadSpeed),
            candidate.File.Size,
            candidate.File.BitRate,
            candidate.File.SampleRate,
            candidate.File.Length,
            candidate.File.Extension,
            candidate.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList());

    private static DownloadBehaviorPolicyDto InteractiveDownloadBehavior(DownloadBehaviorPolicyDto? existing)
        => existing == null
            ? new DownloadBehaviorPolicyDto(Album: DownloadBehavior.Manual, AlbumAggregate: DownloadBehavior.Manual)
            : existing with { Album = DownloadBehavior.Manual, AlbumAggregate = DownloadBehavior.Manual };

    private SubmissionOptionsDto OptionsForWorkflow(Guid workflowId)
        => (rootOptions ?? new SubmissionOptionsDto()) with { WorkflowId = workflowId };

    private static bool IsActive(ServerJobLifecycleState state)
        => state != ServerJobLifecycleState.Terminal;

    private static bool IsCompleted(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason = ServerJobSkipReason.None)
        => outcome == ServerJobTerminalOutcome.Succeeded
            || (outcome == ServerJobTerminalOutcome.Skipped && skipReason == ServerJobSkipReason.AlreadyExists);

    private enum InteractiveAlbumResultKind
    {
        Folder,
        AggregateAlbum,
    }

    private sealed class InteractiveAlbumSession
    {
        public Guid SourceSearchJobId { get; }
        public Job PromptJob { get; }
        public AlbumQueryDto Query { get; }
        public List<AlbumFolder> Folders { get; }
        public SubmissionOptionsDto Options { get; }
        public InteractiveAlbumResultKind ResultKind { get; }
        public HashSet<string> ExcludedFolderKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RetrievedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FilterStr { get; set; }

        public InteractiveAlbumSession(
            Guid sourceSearchJobId,
            Job promptJob,
            AlbumQueryDto query,
            List<AlbumFolder> folders,
            SubmissionOptionsDto options,
            InteractiveAlbumResultKind resultKind)
        {
            SourceSearchJobId = sourceSearchJobId;
            PromptJob = promptJob;
            Query = query;
            Folders = folders;
            Options = options;
            ResultKind = resultKind;
        }
    }
}

internal sealed record InteractiveAlbumPromptRequest(
    Job PromptJob,
    List<AlbumFolder> Folders,
    HashSet<string> RetrievedFolders,
    string? FilterStr,
    InteractiveAlbumPromptPurpose Purpose);

internal enum InteractiveAlbumPromptPurpose
{
    NewAlbumPrompt,
    RetryAcceptedAlbumPrompt,
}

internal sealed record InteractiveAlbumSelection(
    AlbumFolder Folder,
    bool RetrieveCurrentFolder,
    bool SkipTrackCountVerification);

internal sealed record InteractiveAlbumPromptOutcome(
    InteractiveAlbumSelection? Selection,
    bool WasExplicitSkip);
