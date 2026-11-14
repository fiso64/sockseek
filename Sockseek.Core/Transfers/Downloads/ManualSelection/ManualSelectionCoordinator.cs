using System.Collections.Concurrent;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.ManualSelection;

internal sealed class ManualSelectionCoordinator
{
    private const byte CancellationQueued = 1;
    private readonly object selectionGate = new();
    private readonly Func<Guid, Job?> getJob;
    private readonly DownloadJobContextStore contexts;
    private readonly Action<Job, Job?, Guid?> registerJob;
    private readonly Action<Job> observePreparedAutoProfiles;
    private readonly Action<Job> resumeJob;
    private readonly Action<Job, JobCancellationSource> cancelSelection;
    private readonly Func<Job, Task> flushTerminalEffects;
    private readonly Func<Job, bool> isSuccessfulTerminal;
    private readonly Action<Job> prepareSelectionSettings;
    private readonly ConcurrentDictionary<Guid, Guid> groupParentByChildId = new();
    private readonly ConcurrentDictionary<Guid, byte> closedGroupSelections = new();
    private readonly ConcurrentDictionary<Guid, byte> closedManualSelections = new();

    public ManualSelectionCoordinator(
        Func<Guid, Job?> getJob,
        DownloadJobContextStore contexts,
        Action<Job, Job?, Guid?> registerJob,
        Action<Job> observePreparedAutoProfiles,
        Action<Job> resumeJob,
        Action<Job, JobCancellationSource> cancelSelection,
        Func<Job, Task> flushTerminalEffects,
        Func<Job, bool> isSuccessfulTerminal,
        Action<Job> prepareSelectionSettings)
    {
        this.getJob = getJob;
        this.contexts = contexts;
        this.registerJob = registerJob;
        this.observePreparedAutoProfiles = observePreparedAutoProfiles;
        this.resumeJob = resumeJob;
        this.cancelSelection = cancelSelection;
        this.flushTerminalEffects = flushTerminalEffects;
        this.isSuccessfulTerminal = isSuccessfulTerminal;
        this.prepareSelectionSettings = prepareSelectionSettings;
    }

    public bool TryStartSong(
        Guid sourceJobId,
        FileCandidate candidate,
        Action<SongJob>? configureSelection,
        out SongJob? selectedJob)
    {
        lock (selectionGate)
        {
            selectedJob = null;
            var source = getJob(sourceJobId);
            SongJob? song;
            if (source is SongJob directSong && directSong.IsAwaitingSelection
                && !closedManualSelections.ContainsKey(directSong.Id))
                song = directSong;
            else if (source is SongGroupsJob group && CanStartGroupSelection(group))
            {
                song = group.Songs.FirstOrDefault(child => child.Candidates?.Any(file =>
                    string.Equals(file.Username, candidate.Username, StringComparison.Ordinal)
                    && string.Equals(file.Filename, candidate.Filename, StringComparison.Ordinal)
                    && file.Visibility == candidate.Visibility) == true);
                if (song == null || !CanStartGroupChild(song))
                    return false;
                EnsureManualGroupChildPrepared(group, song);
            }
            else
                return false;

            song.ResolvedTarget = candidate;
            song.ExactTarget = candidate.Target;
            // Result groups remain a discovery surface while selection is open.
            // Execution pins the explicit manual target without discarding options.
            if (source is not SongGroupsJob)
                song.Candidates = [candidate];
            ApplySelectionSettings(song, configureSelection);
            song.EnsureDisplayId();
            song.ResetToPending();
            resumeJob(song);
            selectedJob = song;
            return true;
        }
    }

    public bool TryStartAlbum(
        Guid sourceJobId,
        AlbumFolder selectedFolder,
        AlbumQuery? albumQuery,
        Action<AlbumJob>? configureSelection,
        out AlbumJob? selectedJob)
    {
        lock (selectionGate)
        {
            selectedJob = null;
            var sourceJob = getJob(sourceJobId);

            if (sourceJob is AlbumJob albumJob && CanStartManualAlbumSelection(albumJob))
            {
                StartExistingAlbumSelection(albumJob, selectedFolder, configureSelection);
                selectedJob = albumJob;
                return true;
            }

            if (sourceJob is AlbumGroupsJob songGroupsJob && CanStartGroupSelection(songGroupsJob))
            {
                var childAlbum = FindAlbumGroupForSelection(songGroupsJob, selectedFolder, albumQuery);
                if (childAlbum == null || !CanStartGroupChild(childAlbum))
                    return false;

                EnsureManualGroupChildPrepared(songGroupsJob, childAlbum);
                StartExistingAlbumSelection(childAlbum, selectedFolder, configureSelection);
                selectedJob = childAlbum;
                return true;
            }

            return false;
        }
    }

    public bool TryCancel(Guid jobId, JobCancellationSource source)
    {
        lock (selectionGate)
        {
            var job = getJob(jobId);
            if (job == null || !job.IsAwaitingSelection || job.Config == null)
                return false;
            if (IsCancellationQueued(job))
                return true;
            if (job is SongGroupsJob or AlbumGroupsJob)
                closedGroupSelections[job.Id] = CancellationQueued;
            else
                closedManualSelections[job.Id] = CancellationQueued;

            var selectedChildren = groupParentByChildId
                .Where(pair => pair.Value == job.Id)
                .Select(pair => getJob(pair.Key)).OfType<Job>().ToArray();
            cancelSelection(job, source);
            var childSource = source is JobCancellationSource.UserRequestedAllJobs or JobCancellationSource.UserRequestedWorkflow
                ? source : JobCancellationSource.ParentJob;
            foreach (Job child in selectedChildren)
            {
                if (child.IsTerminal) continue;
                if (!TryCancel(child.Id, childSource)) child.Cancel(childSource);
            }
            return true;
        }
    }

    public async Task<bool> CompleteAsync(Guid jobId)
    {
        Job job;
        bool group;
        lock (selectionGate)
        {
            var current = getJob(jobId);
            if (current == null || !current.IsAwaitingSelection || current.Config == null || IsCancellationQueued(current))
                return false;
            job = current;
            group = job is SongGroupsJob or AlbumGroupsJob;
            if (group)
                closedGroupSelections.TryAdd(job.Id, 0);
            else
            {
                closedManualSelections.TryAdd(job.Id, 0);
                JobOutcomeCommitter.Commit(job, JobOutcome.Failed(JobFailureReason.NoMatchingResults));
            }
        }
        if (group)
            await TryFinalizeClosedGroupSelectionAsync(job);
        else
            await flushTerminalEffects(job);
        return true;
    }

    public async Task<bool> SkipAsync(Guid jobId)
    {
        Job job;
        lock (selectionGate)
        {
            var current = getJob(jobId);
            if (current == null || !current.IsAwaitingSelection || current.Config == null || IsCancellationQueued(current))
                return false;
            job = current;
            if (job is SongGroupsJob or AlbumGroupsJob)
                closedGroupSelections.TryAdd(job.Id, 0);
            else
                closedManualSelections.TryAdd(job.Id, 0);
            JobOutcomeCommitter.Commit(job, JobOutcome.Skipped(JobSkipReason.Manual));
        }
        await flushTerminalEffects(job);
        return true;
    }

    public async Task TryFinalizeClosedGroupSelectionForChildAsync(Job child)
    {
        if (groupParentByChildId.TryGetValue(child.Id, out var groupId)
            && getJob(groupId) is { } songGroupsJob)
        {
            await TryFinalizeClosedGroupSelectionAsync(songGroupsJob);
        }
    }

    private void StartExistingAlbumSelection(AlbumJob albumJob, AlbumFolder selectedFolder, Action<AlbumJob>? configureSelection)
    {
        albumJob.ResolvedTarget = selectedFolder;
        albumJob.CanBeSkippedOverride = false;
        ApplySelectionSettings(albumJob, configureSelection);
        if (!albumJob.Results.Any(folder => SameAlbumFolder(folder, selectedFolder)))
            albumJob.Results.Insert(0, selectedFolder);
        albumJob.ResetToPending();
        resumeJob(albumJob);
    }

    private void ApplySelectionSettings<T>(T job, Action<T>? configureSelection) where T : Job
    {
        var previousSettings = job.PlannedSettings;
        configureSelection?.Invoke(job);
        if (job.PlannedSettings != null
            && !ReferenceEquals(previousSettings, job.PlannedSettings))
            prepareSelectionSettings(job);
    }

    public bool HasResumableState(IReadOnlyCollection<Job> workflowJobs)
    {
        if (workflowJobs.Any(job => job.IsAwaitingSelection))
            return true;

        return workflowJobs.OfType<AlbumJob>().Any(CanStartManualAlbumSelection);
    }

    public void Retire(IReadOnlyCollection<Guid> jobIds)
    {
        var ids = jobIds.ToHashSet();
        foreach (Guid jobId in ids)
        {
            closedManualSelections.TryRemove(jobId, out _);
            closedGroupSelections.TryRemove(jobId, out _);
            groupParentByChildId.TryRemove(jobId, out _);
        }

        foreach (var pair in groupParentByChildId.Where(pair => ids.Contains(pair.Value)))
            groupParentByChildId.TryRemove(pair.Key, out _);
    }

    private bool CanStartManualAlbumSelection(AlbumJob albumJob)
        => albumJob.DownloadBehavior == DownloadBehavior.Manual
            && !closedManualSelections.ContainsKey(albumJob.Id)
            && (!groupParentByChildId.TryGetValue(albumJob.Id, out Guid parentId)
                || !closedGroupSelections.ContainsKey(parentId))
            && (albumJob.IsAwaitingSelection
                || albumJob.IsUnsuccessfulTerminal && albumJob.TerminalOutcome != JobTerminalOutcome.Cancelled);

    private bool CanStartGroupSelection(Job group)
        => group.IsAwaitingSelection && !closedGroupSelections.ContainsKey(group.Id);

    private bool IsCancellationQueued(Job job)
        => closedManualSelections.TryGetValue(job.Id, out byte manual) && manual == CancellationQueued
            || closedGroupSelections.TryGetValue(job.Id, out byte group) && group == CancellationQueued;

    private bool CanStartGroupChild(Job child)
        => !contexts.ContainsKey(child.Id)
            || child.LifecycleState is JobLifecycleState.AwaitingSelection or JobLifecycleState.Terminal;

    internal int RetainedStateCount => groupParentByChildId.Count
        + closedGroupSelections.Count
        + closedManualSelections.Count;

    private static AlbumJob? FindAlbumGroupForSelection(AlbumGroupsJob songGroupsJob, AlbumFolder selectedFolder, AlbumQuery? albumQuery)
        => songGroupsJob.Albums.FirstOrDefault(album =>
            (albumQuery == null || AlbumQueriesEqual(album.Query, albumQuery))
            && album.Results.Any(folder => SameAlbumFolder(folder, selectedFolder)));

    private void EnsureManualGroupChildPrepared(Job songGroupsJob, Job child)
    {
        bool prepared = contexts.ContainsKey(child.Id);
        child.WorkflowId = songGroupsJob.WorkflowId;
        // Retrying a prepared child must keep its settings paired with its
        // context, including any selection-specific output override.
        if (!prepared)
            child.Config = songGroupsJob.Config;
        child.ItemName ??= child.ToString(noInfo: true);
        child.DownloadBehaviorPolicy = child is AlbumJob
            ? child.DownloadBehaviorPolicy with { Album = DownloadBehavior.Manual }
            : child.DownloadBehaviorPolicy with { Song = DownloadBehavior.Manual };

        groupParentByChildId[child.Id] = songGroupsJob.Id;
        registerJob(child, songGroupsJob, songGroupsJob.Id);

        if (prepared)
            return;

        var parentCtx = contexts.Get(songGroupsJob);
        contexts.Set(child, new JobContext
        {
            PreparationOwner = parentCtx.PreparationOwner,
            IndexEditor = parentCtx.IndexEditor,
            PlaylistEditor = parentCtx.PlaylistEditor,
            OutputDirSkipper = parentCtx.OutputDirSkipper,
            MusicDirSkipper = parentCtx.MusicDirSkipper,
            OutputScope = parentCtx.OutputScope,
            PreprocessTracks = false,
        });

        observePreparedAutoProfiles(child);
    }

    private async Task TryFinalizeClosedGroupSelectionAsync(Job songGroupsJob)
    {
        List<Job> closedChildren;
        bool completedRoot = false;
        lock (selectionGate)
        {
            if (!closedGroupSelections.ContainsKey(songGroupsJob.Id) || IsCancellationQueued(songGroupsJob))
                return;

            var selectedChildren = groupParentByChildId
                .Where(pair => pair.Value == songGroupsJob.Id)
                .Select(pair => getJob(pair.Key))
                .OfType<Job>()
                .ToList();
            if (selectedChildren.Any(child => child.LifecycleState is JobLifecycleState.Pending or JobLifecycleState.Running))
                return;

            // A failed manual album can return to its picker. Once the owning
            // selection is closed, that child can no longer wait for a choice.
            closedChildren = selectedChildren.Where(child => child.IsAwaitingSelection).ToList();
            foreach (Job child in closedChildren)
                JobOutcomeCommitter.Commit(child, JobOutcome.Failed(JobFailureReason.NoMatchingResults));

            if (songGroupsJob.LifecycleState != JobLifecycleState.Terminal)
            {
                var outcome = selectedChildren.Count > 0 && selectedChildren.All(isSuccessfulTerminal)
                    ? JobOutcome.Done()
                    : JobOutcome.Failed(JobFailureReason.NoMatchingResults);
                JobOutcomeCommitter.Commit(songGroupsJob, outcome);
                completedRoot = true;
            }
        }
        foreach (Job child in closedChildren)
            await flushTerminalEffects(child);
        if (completedRoot)
            await flushTerminalEffects(songGroupsJob);
    }

    private static bool SameAlbumFolder(AlbumFolder left, AlbumFolder right)
        => string.Equals(left.Username, right.Username, StringComparison.Ordinal)
            && string.Equals(left.FolderPath, right.FolderPath, StringComparison.Ordinal);

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.URI, right.URI, StringComparison.OrdinalIgnoreCase);
}
