using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Server;
using Soulseek;

namespace Sldl.Cli;

internal sealed class InteractiveCliCoordinator
{
    private readonly DownloadEngine _engine;
    private readonly ICliBackend _backend;
    private readonly CliSettings _cliSettings;
    private readonly CancellationToken _appToken;
    private readonly Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? _promptOverride;
    private readonly SemaphoreSlim _promptSemaphore = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _rootJobIds = [];
    private readonly Dictionary<Guid, InteractiveAlbumSession> _interactiveAlbumSessions = [];

    private int _pendingRootJobs;
    private bool _enqueueCompleted;
    private bool _interactiveEnabled;

    public InteractiveCliCoordinator(
        DownloadEngine engine,
        CliSettings cliSettings,
        CancellationToken appToken,
        Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride)
        : this(engine, cliSettings, appToken, null, promptOverride)
    {
    }

    public InteractiveCliCoordinator(
        DownloadEngine engine,
        CliSettings cliSettings,
        CancellationToken appToken,
        ICliBackend? backend = null,
        Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride = null)
    {
        _engine = engine;
        _backend = backend ?? new LocalCliBackend(engine);
        _cliSettings = cliSettings;
        _appToken = appToken;
        _promptOverride = promptOverride;
        _interactiveEnabled = cliSettings.InteractiveMode;

        _engine.Events.JobResultCreated += OnJobResultCreated;
        _engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
    }

    public void Start(ExtractJob rootJob, DownloadSettings settings)
    {
        rootJob.AutoProcessResult = false;
        EnqueueRoot(rootJob, settings);
    }

    private void OnJobResultCreated(ExtractJob extractJob, Job result)
    {
        if (extractJob.AutoProcessResult)
            return;

        if (_appToken.IsCancellationRequested)
            return;

        PrepareDetachedExtractions(result);
        var inheritedSettings = extractJob.Config
            ?? throw new InvalidOperationException("Detached extracted jobs require the extract job config.");

        if (_interactiveEnabled
            && result is AlbumJob albumJob
            && albumJob.ResolvedTarget == null)
        {
            var searchJob = new SearchJob(albumJob.Query);
            searchJob.CopySharedFieldsFrom(albumJob);
            EnqueueRoot(searchJob, inheritedSettings);
            return;
        }

        if (_interactiveEnabled
            && result is AlbumAggregateJob albumAggregateJob)
        {
            // Null out DefaultFolderProjection so OnJobExecutionCompleted can distinguish
            // aggregate searches (only DefaultAggregateAlbumProjection set) from single-album
            // searches (DefaultFolderProjection set), without a side-channel tracking set.
            var searchJob = new SearchJob(albumAggregateJob.Query) { DefaultFolderProjection = null };
            searchJob.CopySharedFieldsFrom(albumAggregateJob);
            EnqueueRoot(searchJob, inheritedSettings);
            return;
        }

        EnqueueRoot(result, inheritedSettings);
    }

    private void OnJobExecutionCompleted(Job job)
    {
        if (!IsRootJob(job.Id))
            return;

        try
        {
            if (_appToken.IsCancellationRequested)
                return;

            if (_interactiveEnabled && job is SearchJob { DefaultFolderProjection: null, DefaultAggregateAlbumProjection: not null } aggregateSearchJob)
            {
                HandleCompletedAggregateAlbumSearch(aggregateSearchJob);
            }
            else if (_interactiveEnabled && job is SearchJob { DefaultFolderProjection: not null } folderSearchJob)
            {
                HandleCompletedAlbumSearch(folderSearchJob);
            }
            else if (_interactiveAlbumSessions.TryGetValue(job.Id, out var session))
            {
                _interactiveAlbumSessions.Remove(job.Id);
                HandleCompletedInteractiveAlbum((AlbumJob)job, session);
            }
        }
        finally
        {
            CompleteRoot(job.Id);
        }
    }

    private void HandleCompletedAggregateAlbumSearch(SearchJob searchJob)
    {
        if (searchJob.State != JobState.Done || searchJob.DefaultAggregateAlbumProjection == null)
            return;

        var aggregateResult = _backend.GetAggregateAlbumResultsAsync(
            searchJob.Id,
            new AggregateAlbumProjectionRequestDto(IncludeFolders: true),
            _appToken).GetAwaiter().GetResult();
        if (aggregateResult == null || aggregateResult.Items.Count == 0)
            return;

        foreach (var albumCandidate in aggregateResult.Items)
        {
            if (_appToken.IsCancellationRequested || !_interactiveEnabled)
                break;

            var folders = albumCandidate.Folders?.Select(ToAlbumFolder).ToList() ?? [];
            if (folders.Count == 0)
                continue;

            var query   = ToAlbumQuery(albumCandidate.Query);
            var session = new InteractiveAlbumSession(searchJob.Id, searchJob, query, folders);
            var selected = PromptForAlbumSelectionAsync(session).GetAwaiter().GetResult();
            if (selected == null)
                continue;

            EnqueueInteractiveAlbumJob(session, selected, searchJob.Config);
        }
    }

    private void HandleCompletedAlbumSearch(SearchJob searchJob)
    {
        if (searchJob.State != JobState.Done || searchJob.DefaultFolderProjection == null)
            return;

        var projection = _backend.GetFolderResultsAsync(
            searchJob.Id,
            new FolderSearchProjectionRequestDto(
                ToAlbumQueryDto(searchJob.DefaultFolderProjection.Query),
                IncludeFiles: true),
            _appToken).GetAwaiter().GetResult();
        var folders = projection?.Items
            .Select(ToAlbumFolder)
            .ToList()
            ?? [];
        if (folders.Count == 0)
            return;

        var session = new InteractiveAlbumSession(searchJob.Id, searchJob, searchJob.DefaultFolderProjection.Query, folders);
        var selected = PromptForAlbumSelectionAsync(session).GetAwaiter().GetResult();
        if (selected == null)
            return;

        EnqueueInteractiveAlbumJob(session, selected, searchJob.Config);
    }

    private void HandleCompletedInteractiveAlbum(AlbumJob albumJob, InteractiveAlbumSession session)
    {
        if (_appToken.IsCancellationRequested)
            return;

        if (albumJob.State == JobState.Done)
            return;

        if (albumJob.FailureReason == FailureReason.Cancelled)
            return;

        if (albumJob.ResolvedTarget != null)
            session.ExcludedFolderKeys.Add(FolderKey(albumJob.ResolvedTarget));

        var selected = PromptForAlbumSelectionAsync(session).GetAwaiter().GetResult();
        if (selected == null)
            return;

        EnqueueInteractiveAlbumJob(session, selected, albumJob.Config);
    }

    private void EnqueueInteractiveAlbumJob(InteractiveAlbumSession session, AlbumFolder selected, DownloadSettings settings)
    {
        var summary = _backend.StartFolderDownloadAsync(
            session.SourceSearchJobId,
            new StartFolderDownloadRequestDto(
                new AlbumFolderRefDto(selected.Username, selected.FolderPath),
                AlbumQuery: ToAlbumQueryDto(session.Query)),
            _appToken).GetAwaiter().GetResult();

        if (summary == null)
            throw new InvalidOperationException("Failed to start interactive album download.");

        RegisterExternalRoot(summary.JobId);
        _interactiveAlbumSessions[summary.JobId] = session;
    }

    private async Task<AlbumFolder?> PromptForAlbumSelectionAsync(InteractiveAlbumSession session)
    {
        var availableFolders = session.Folders
            .Where(folder => !session.ExcludedFolderKeys.Contains(FolderKey(folder)))
            .ToList();

        if (availableFolders.Count == 0)
            return null;

        await _promptSemaphore.WaitAsync(_appToken);
        try
        {
            InteractiveModeManager.RunResult result;
            if (_promptOverride != null)
            {
                result = await _promptOverride(new InteractiveAlbumPromptRequest(
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
                        _engine.Queue,
                        availableFolders,
                        canRetrieve: true,
                        retrievedFolders: session.RetrievedFolders,
                        retrieveFolderCallback: async folder => await RunPromptRetrieveFolderAsync(
                            folder,
                            async () => await _backend.RetrieveFolderAndWaitAsync(
                                session.SourceSearchJobId,
                                new RetrieveFolderRequestDto(
                                    new AlbumFolderRefDto(folder.Username, folder.FolderPath),
                                    ToAlbumQueryDto(session.Query)),
                                _appToken)),
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
                _interactiveEnabled = false;
                _cliSettings.InteractiveMode = false;
            }

            if (result.Index < 0 || result.Folder == null)
                return null;

            return result.Folder;
        }
        finally
        {
            _promptSemaphore.Release();
        }
    }

    private void EnqueueRoot(Job job, DownloadSettings settings)
    {
        RegisterExternalRoot(job.Id);

        _engine.Enqueue(job, settings);
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

    private void RegisterExternalRoot(Guid jobId)
    {
        lock (_gate)
        {
            if (_enqueueCompleted)
                throw new InvalidOperationException("Cannot enqueue more jobs after completion was signaled.");

            _rootJobIds.Add(jobId);
            _pendingRootJobs++;
        }
    }

    private bool IsRootJob(Guid jobId)
    {
        lock (_gate)
            return _rootJobIds.Contains(jobId);
    }

    private void CompleteRoot(Guid jobId)
    {
        bool shouldComplete = false;

        lock (_gate)
        {
            if (!_rootJobIds.Remove(jobId))
                return;

            _pendingRootJobs--;
            if (_pendingRootJobs == 0 && !_enqueueCompleted)
            {
                _enqueueCompleted = true;
                shouldComplete = true;
            }
        }

        if (shouldComplete)
            _engine.CompleteEnqueue();
    }

    private static void PrepareDetachedExtractions(Job job)
    {
        switch (job)
        {
            case ExtractJob extractJob:
                extractJob.AutoProcessResult = false;
                break;

            case JobList jobList:
                foreach (var child in jobList.Jobs)
                    PrepareDetachedExtractions(child);
                break;
        }
    }

    internal static string FolderKey(AlbumFolder folder)
        => folder.Username + "\\" + folder.FolderPath;

    private sealed class InteractiveAlbumSession
    {
        public Guid SourceSearchJobId { get; }
        public Job PromptJob { get; }
        public AlbumQuery Query { get; }
        public List<AlbumFolder> Folders { get; }
        public HashSet<string> ExcludedFolderKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RetrievedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FilterStr { get; set; }

        public InteractiveAlbumSession(Guid sourceSearchJobId, Job promptJob, AlbumQuery query, List<AlbumFolder> folders)
        {
            SourceSearchJobId = sourceSearchJobId;
            PromptJob = promptJob;
            Query = query;
            Folders = folders;
        }
    }

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
        var candidate = ToFileCandidate(file);
        var query = Searcher.InferSongQuery(candidate.Filename, new SongQuery());
        return new SongJob(query) { ResolvedTarget = candidate };
    }

    private static FileCandidate ToFileCandidate(FileCandidateDto candidate)
        => new(
            new SearchResponse(
                candidate.Username,
                -1,
                candidate.Peer.HasFreeUploadSlot ?? false,
                candidate.Peer.UploadSpeed ?? -1,
                -1,
                null),
            new Soulseek.File(
                0,
                candidate.Filename,
                candidate.Size,
                candidate.Extension ?? Path.GetExtension(candidate.Filename),
                candidate.Attributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));

    private static AlbumQuery ToAlbumQuery(AlbumQueryDto dto)
        => new AlbumQuery
        {
            Artist           = dto.Artist ?? "",
            Album            = dto.Album  ?? "",
            SearchHint       = dto.SearchHint ?? "",
            URI              = dto.Uri    ?? "",
            ArtistMaybeWrong = dto.ArtistMaybeWrong,
        };

    private static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query)
        => new(
            query.Artist,
            query.Album,
            query.SearchHint,
            query.URI,
            query.ArtistMaybeWrong);

    internal static SongJob ToSongJob(SongJobPayloadDto song)
    {
        var job = new SongJob(new SongQuery
        {
            Artist = song.Query.Artist ?? "",
            Title = song.Query.Title ?? "",
            Album = song.Query.Album ?? "",
            URI = song.Query.Uri ?? "",
            Length = song.Query.Length ?? -1,
            ArtistMaybeWrong = song.Query.ArtistMaybeWrong,
        })
        {
            DownloadPath = song.DownloadPath,
        };

        if (!string.IsNullOrWhiteSpace(song.ResolvedUsername)
            && !string.IsNullOrWhiteSpace(song.ResolvedFilename))
        {
            // Interactive album prompts still reason about per-file selection using
            // SongJob.ResolvedTarget. Reconstruct the minimal candidate identity here
            // so the prompt can stay backend-agnostic.
            job.ResolvedTarget = new FileCandidate(
                new SearchResponse(song.ResolvedUsername, -1, false, -1, -1, null),
                new Soulseek.File(
                    0,
                    song.ResolvedFilename,
                    0,
                    Path.GetExtension(song.ResolvedFilename),
                    attributeList: []));
        }

        return job;
    }
}

internal sealed record InteractiveAlbumPromptRequest(
    Job PromptJob,
    List<AlbumFolder> Folders,
    HashSet<string> RetrievedFolders,
    string? FilterStr);
