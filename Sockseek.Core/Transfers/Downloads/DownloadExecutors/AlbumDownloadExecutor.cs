using Microsoft.Extensions.Logging;
using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Sockseek.Core;

internal sealed class AlbumDownloadExecutor
{
    private readonly DownloadExecutionContext context;
    private readonly JobOrchestrator jobs;
    private readonly SongDownloadExecutor songDownloads;
    private readonly AlbumImageDownloadExecutor imageDownloads;
    private readonly IncompleteAlbumActionExecutor incompleteAlbums;

    public AlbumDownloadExecutor(DownloadExecutionContext context, JobOrchestrator jobs, SongDownloadExecutor songDownloads)
    {
        this.context = context;
        this.jobs = jobs;
        this.songDownloads = songDownloads;
        imageDownloads = new AlbumImageDownloadExecutor(songDownloads);
        incompleteAlbums = new IncompleteAlbumActionExecutor(context);
    }

    private static bool SameAlbumFolder(AlbumFolder left, AlbumFolder right)
        => string.Equals(left.Username, right.Username, StringComparison.Ordinal)
            && string.Equals(left.FolderPath, right.FolderPath, StringComparison.Ordinal);

    public async Task<JobOutcome> ProcessAlbumDownload(AlbumJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(job, config.Output, config.Extraction, ctx.OutputScope);
        var audioResult = await TryDownloadAlbumAudio(job, ctx, organizer);
        var completion = PrepareAlbumAudioOutcome(job, audioResult, ctx);
        var chosenFiles = completion.ChosenFiles;

        if (completion.Outcome.LifecycleState == JobLifecycleState.AwaitingSelection)
        {
            JobOutcomeCommitter.Commit(job, completion.Outcome);
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return completion.Outcome;
        }

        MarkCancelledAlbumFiles(job, audioResult, completion.Outcome);
        var images = await DownloadAlbumImagesIfNeeded(job, ctx, organizer, audioResult, chosenFiles);
        var outcome = config.Output.AlbumArtOnly
            ? DeriveAlbumArtOnlyOutcome(job, completion.Outcome, images.ChosenFiles, images.AdditionalImages)
            : completion.Outcome;
        if (!string.IsNullOrEmpty(job.DownloadPath))
            job.UpdateActivity(JobActivityPhase.Organizing);
        var finalization = context.OutputFinalizer.FinalizeAlbumPlacement(
            job,
            organizer,
            images.ChosenFiles,
            images.AdditionalImages,
            outcome);
        outcome = finalization.Outcome;
        if (finalization.OrganizationException != null && job.ResolvedTarget != null)
        {
            HandleIncompleteAlbumIfNeeded(job, job.ResolvedTarget, outcome, config);
        }

        var postProcessOutcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(job, outcome);
        postProcessOutcome = await songDownloads.RunOnCompleteIfApplicable(job, null, ctx, postProcessOutcome);

        var finalOutcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(job, postProcessOutcome);
        JobOutcomeCommitter.Commit(job, finalOutcome);
        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
        return finalOutcome;
    }

    AlbumDownloadCompletion PrepareAlbumAudioOutcome(AlbumJob job, AlbumAudioDownloadResult audioResult, JobContext ctx)
    {
        var chosenFiles = audioResult.ChosenFiles;
        JobOutcome outcome;

        if (audioResult.Succeeded && chosenFiles != null)
        {
            var downloadedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.TerminalOutcome == JobTerminalOutcome.Succeeded && !string.IsNullOrEmpty(af.DownloadPath));

            if (downloadedAudio.Any())
            {
                var downloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(af => af.DownloadPath!));
                outcome = JobOutcome.Done(downloadPath);
                job.DownloadPath = downloadPath;
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, downloadPath);
                // Note: album jobs have no parent extractor reference here; RemoveTrackFromSource
                // for albums is handled at the JobList fan-out level if needed.
            }
            else
            {
                outcome = JobOutcome.Done();
            }
        }
        else if (audioResult.Outcome != null)
        {
            outcome = audioResult.Outcome;
            DownloadExecutorCoordinator.ApplyPreCommitOutcomeMetadata(job, outcome);
        }
        else
        {
            outcome = DownloadOutcomes.NoMatchingCandidates();
        }

        return new(outcome, chosenFiles);
    }

    void MarkCancelledAlbumFiles(AlbumJob job, AlbumAudioDownloadResult audioResult, JobOutcome outcome)
    {
        if (outcome.FailureReason == JobFailureReason.Cancelled)
        {
            var cancelledFolder = job.ResolvedTarget
                ?? audioResult.LastChosenFolder;

            if (cancelledFolder != null)
                MarkUnfinishedAlbumFilesCancelled(job, cancelledFolder);
        }
    }

    async Task<AlbumImageDownloadResult> DownloadAlbumImagesIfNeeded(
        AlbumJob job,
        JobContext ctx,
        FileManager organizer,
        AlbumAudioDownloadResult audioResult,
        List<SongJob>? chosenFiles)
    {
        var config = job.Config;
        if (!config.Output.AlbumArtOnly && (!audioResult.Succeeded || config.Output.EffectiveAlbumArtOption == AlbumArtOption.Default))
            return new(chosenFiles, null);

        SockseekLog.Jobs.Info(job, $"downloading additional images: {job}");
        var additionalImages = await imageDownloads.DownloadImages(job, ctx, organizer, job.ResolvedTarget);

        if (chosenFiles != null && additionalImages.Count > 0)
        {
            var addedPaths = additionalImages
                .Select(af => Utils.NormalizedPath(af.DownloadPath ?? ""))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToHashSet();

            chosenFiles.RemoveAll(af => af.IsNotAudio
                && !string.IsNullOrEmpty(af.DownloadPath)
                && addedPaths.Contains(Utils.NormalizedPath(af.DownloadPath)));

            chosenFiles.AddRange(additionalImages);
        }

        return new(chosenFiles, additionalImages);
    }

    JobOutcome DeriveAlbumArtOnlyOutcome(
        AlbumJob job,
        JobOutcome fallbackOutcome,
        List<SongJob>? chosenFiles,
        List<SongJob>? additionalImages)
    {
        var imageFiles = (chosenFiles ?? [])
            .Concat(additionalImages ?? [])
            .Where(file => file.IsNotAudio)
            .Distinct()
            .ToList();

        if (job.Cts?.IsCancellationRequested == true
            || imageFiles.Any(file => file.FailureReason == JobFailureReason.Cancelled))
            return JobOutcome.Cancelled(JobOrchestrator.CancellationSourceForDerivedCancellation(job, imageFiles.Cast<Job>().ToArray()));

        var downloadedImages = imageFiles
            .Where(file => file.TerminalOutcome == JobTerminalOutcome.Succeeded && !string.IsNullOrEmpty(file.DownloadPath))
            .ToList();

        if (downloadedImages.Count > 0)
        {
            var downloadPath = Utils.GreatestCommonDirectory(downloadedImages.Select(file => file.DownloadPath!));
            job.DownloadPath = downloadPath;
            return JobOutcome.Done(downloadPath);
        }

        var existingImages = imageFiles
            .Where(file => file.IsSkippedAlreadyExists && !string.IsNullOrEmpty(file.DownloadPath))
            .ToList();

        if (existingImages.Count > 0)
        {
            var downloadPath = Utils.GreatestCommonDirectory(existingImages.Select(file => file.DownloadPath!));
            job.DownloadPath = downloadPath;
            return JobOutcome.AlreadyExists(downloadPath);
        }

        var failedImage = imageFiles.FirstOrDefault(file => file.IsUnsuccessfulTerminal);
        if (failedImage != null)
        {
            return JobOutcome.Failed(
                failedImage.FailureReason == JobFailureReason.None ? JobFailureReason.AllDownloadsFailed : failedImage.FailureReason,
                failedImage.FailureMessage);
        }

        return fallbackOutcome.TerminalOutcome == JobTerminalOutcome.Failed
            ? fallbackOutcome
            : DownloadOutcomes.NoMatchingCandidates();
    }

    sealed record AlbumDownloadCompletion(JobOutcome Outcome, List<SongJob>? ChosenFiles);
    sealed record AlbumImageDownloadResult(List<SongJob>? ChosenFiles, List<SongJob>? AdditionalImages);

    sealed record AlbumAudioDownloadResult(
        bool Succeeded,
        JobOutcome? Outcome,
        List<SongJob>? ChosenFiles,
        AlbumFolder? LastChosenFolder);

    sealed class AlbumAudioDownloadState
    {
        public AlbumAudioDownloadState(int trackCountRetries)
        {
            TrackCountRetries = trackCountRetries;
        }

        public HashSet<string> RetrievedFolders { get; } = new();
        public string? FilterText { get; set; }
        public int Index { get; set; }
        public int Tried { get; set; }
        public int TrackCountRetries { get; set; }
        public bool FailedDownloadCandidate { get; set; }
        public AlbumFolder? LastChosenFolder { get; set; }
    }

    sealed record AlbumCandidateSelection(AlbumFolder Folder, bool WasPreselected, bool RetrieveCurrent);
    sealed record AlbumCandidateStepResult(bool Continue, AlbumAudioDownloadResult? Result);
    sealed record AlbumCandidateAttemptResult(bool Succeeded, AlbumAudioDownloadResult? Result);

    async Task<AlbumAudioDownloadResult> TryDownloadAlbumAudio(AlbumJob job, JobContext ctx, FileManager organizer)
    {
        var config = job.Config;
        var state = new AlbumAudioDownloadState(config.Transfer.AlbumTrackCountMaxRetries);
        var activeQuality = AlbumQualityPolicy.ActiveConditions(config.Search.NecessaryCond);
        bool verifyStrictAlbumQuality = config.Search.StrictAlbumQuality && activeQuality.IsActive;

        while (job.Results.Count > 0 && !config.Output.AlbumArtOnly)
        {
            var selection = SelectAlbumCandidate(job, state);
            if (selection == null)
                break;

            var trackCountCheck = await VerifyAlbumTrackCountCandidateAsync(job, ctx, organizer, state, selection);
            if (trackCountCheck.Result != null)
                return trackCountCheck.Result;
            if (trackCountCheck.Continue)
                continue;

            var qualityCheck = await VerifyStrictAlbumQualityCandidateAsync(job, ctx, organizer, state, selection, activeQuality, verifyStrictAlbumQuality);
            if (qualityCheck.Result != null)
                return qualityCheck.Result;
            if (qualityCheck.Continue)
                continue;

            var attempt = await TryDownloadAlbumCandidateAsync(job, ctx, organizer, state, selection);
            if (attempt.Result != null)
                return attempt.Result;
            if (attempt.Succeeded)
                return new(true, null, job.EnsureTrackJobs(selection.Folder), state.LastChosenFolder);

            organizer.SetremoteBaseDir(null);
            if (selection.WasPreselected || state.Tried >= config.Transfer.MaxDownloadRetries)
                return selection.WasPreselected
                    ? ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, state.LastChosenFolder, JobFailureReason.AllDownloadsFailed)
                    : new(false, JobOutcome.Failed(JobFailureReason.AllDownloadsFailed), null, state.LastChosenFolder);

            job.ResolvedTarget = null;
            job.ClearTrackJobs();
            job.Results.RemoveAt(state.Index);
            if (job.Results.Count == 0 && state.FailedDownloadCandidate)
                return new(false, JobOutcome.Failed(JobFailureReason.AllDownloadsFailed), null, state.LastChosenFolder);

            // Reset state so the next iteration transitions to Downloading naturally
            job.ResetToPending();
        }

        return state.FailedDownloadCandidate
            ? new(false, JobOutcome.Failed(JobFailureReason.AllDownloadsFailed), null, state.LastChosenFolder)
            : new(false, null, null, state.LastChosenFolder);
    }

    static AlbumCandidateSelection? SelectAlbumCandidate(AlbumJob job, AlbumAudioDownloadState state)
    {
        bool wasPreselected = job.ResolvedTarget != null;
        bool retrieveCurrent = wasPreselected ? job.AllowBrowseResolvedTarget : true;
        state.Index = 0;

        if (wasPreselected)
        {
            var chosenFolder = job.ResolvedTarget!;
            state.Index = job.Results.Contains(chosenFolder) ? job.Results.IndexOf(chosenFolder) : 0;
            return new(chosenFolder, wasPreselected, retrieveCurrent);
        }

        if (!string.IsNullOrWhiteSpace(state.FilterText))
        {
            state.Index = job.Results.FindIndex(f => f.Files.Any(af => af.Filename.ContainsIgnoreCase(state.FilterText)));
            if (state.Index == -1)
                return null;
        }

        return new(job.Results[state.Index], wasPreselected, retrieveCurrent);
    }

    async Task<AlbumCandidateStepResult> VerifyAlbumTrackCountCandidateAsync(
        AlbumJob job,
        JobContext ctx,
        FileManager organizer,
        AlbumAudioDownloadState state,
        AlbumCandidateSelection selection)
    {
        var config = job.Config;
        var chosenFolder = selection.Folder;
        var folderCond = config.Search.NecessaryFolderCond;
        bool verifyTrackCount = !selection.WasPreselected || !job.SkipResolvedTargetTrackCountVerification;
        if (!verifyTrackCount
            || config.Transfer.AlbumTrackCountMaxRetries <= 0
            || !ConditionSatisfactionPolicy.HasAlbumTrackCountConditions(folderCond))
        {
            return new(false, null);
        }

        int KnownAudioCount() => chosenFolder.Files.Count(af => !af.IsNotAudio);
        int knownCount = KnownAudioCount();
        bool mustBrowseBeforeDownload = ConditionSatisfactionPolicy.ShouldRetrieveFullAlbumForTrackCount(
            folderCond,
            knownCount,
            chosenFolder.IsFullyRetrieved);

        if (mustBrowseBeforeDownload && !state.RetrievedFolders.Contains(chosenFolder.FolderPath))
        {
            var retrieval = await jobs.ProcessFolderRetrieval(chosenFolder, job,
                "Verifying album track count.\n    Retrieving full folder contents...",
                consumeJobSlot: false);
            if (retrieval.RetrievalCompleted)
                state.RetrievedFolders.Add(chosenFolder.FolderPath);
            else
            {
                SockseekLog.Jobs.Info(job, $"album track count verification was cancelled, skipping folder: {chosenFolder.FolderPath}");
                if (selection.WasPreselected)
                    return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.NoMatchingResults));

                job.Results.RemoveAt(state.Index);
                if (--state.TrackCountRetries <= 0)
                {
                    SockseekLog.Jobs.Info(job, $"failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times, skipping album: {job}");
                    return new(false, new(false, DownloadOutcomes.NoMatchingCandidates(), null, state.LastChosenFolder));
                }

                return new(true, null);
            }

            knownCount = KnownAudioCount();
        }

        var trackCountCheck = ConditionSatisfactionPolicy.CheckAlbumTrackCount(folderCond, knownCount);
        if (trackCountCheck.FailedAboveMaximum && trackCountCheck.Maximum is { } maximum)
            SockseekLog.Jobs.Info(job, $"file count ({trackCountCheck.AudioFileCount}) above maximum ({maximum}), skipping folder: {chosenFolder.FolderPath}");
        if (trackCountCheck.FailedBelowMinimum && trackCountCheck.Minimum is { } minimum)
            SockseekLog.Jobs.Info(job, $"file count ({trackCountCheck.AudioFileCount}) below minimum ({minimum}), skipping folder: {chosenFolder.FolderPath}");

        if (trackCountCheck.Satisfied)
            return new(false, null);

        if (selection.WasPreselected)
        {
            SockseekLog.Jobs.Info(job, $"preselected folder failed album track count condition, skipping album: {chosenFolder.FolderPath}");
            return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.NoMatchingResults));
        }

        job.Results.RemoveAt(state.Index);
        if (--state.TrackCountRetries <= 0)
        {
            SockseekLog.Jobs.Info(job, $"failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times, skipping album: {job}");
            return new(false, new(false, DownloadOutcomes.NoMatchingCandidates(), null, state.LastChosenFolder));
        }

        return new(true, null);
    }

    async Task<AlbumCandidateStepResult> VerifyStrictAlbumQualityCandidateAsync(
        AlbumJob job,
        JobContext ctx,
        FileManager organizer,
        AlbumAudioDownloadState state,
        AlbumCandidateSelection selection,
        ActiveAlbumTrackConditions activeQuality,
        bool verifyStrictAlbumQuality)
    {
        if (!verifyStrictAlbumQuality)
            return new(false, null);

        var chosenFolder = selection.Folder;
        if (!chosenFolder.IsFullyRetrieved
            && selection.RetrieveCurrent
            && !state.RetrievedFolders.Contains(chosenFolder.FolderPath))
        {
            var retrieval = await jobs.ProcessFolderRetrieval(chosenFolder, job,
                "Verifying strict album quality.\n    Retrieving full folder contents...",
                consumeJobSlot: false);
            if (retrieval.RetrievalCompleted)
                state.RetrievedFolders.Add(chosenFolder.FolderPath);
            else
            {
                SockseekLog.Jobs.Info(job, $"strict album quality verification was cancelled, skipping folder: {chosenFolder.FolderPath}");
                if (selection.WasPreselected)
                    return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.NoMatchingResults));

                job.Results.RemoveAt(state.Index);
                return new(true, null);
            }
        }

        var qualityCoverage = AlbumQualityPolicy.Evaluate(chosenFolder, job.Config.Search.NecessaryCond, activeQuality);
        if (ConditionSatisfactionPolicy.AlbumQualityIsAcceptable(qualityCoverage, strictAlbumQuality: true))
            return new(false, null);

        SockseekLog.Jobs.Info(job, $"strict album quality failed ({qualityCoverage.MatchingFileCount}/{qualityCoverage.AudioFileCount} matching audio files), skipping folder: {chosenFolder.FolderPath}");
        if (selection.WasPreselected)
            return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.NoMatchingResults));

        job.Results.RemoveAt(state.Index);
        return new(true, null);
    }

    async Task<AlbumCandidateAttemptResult> TryDownloadAlbumCandidateAsync(
        AlbumJob job,
        JobContext ctx,
        FileManager organizer,
        AlbumAudioDownloadState state,
        AlbumCandidateSelection selection)
    {
        var config = job.Config;
        var chosenFolder = selection.Folder;
        state.LastChosenFolder = chosenFolder;
        organizer.SetremoteBaseDir(chosenFolder.FolderPath);
        job.ResolvedTarget = chosenFolder;
        job.EnsureTrackJobs(chosenFolder);
        job.UpdateActivity(JobActivityPhase.Downloading);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        state.Tried++;

        try
        {
            await RunAlbumDownloads(job, config, organizer, chosenFolder, cts);
            if (TryGetInterruptedAlbumOutcome(job, chosenFolder) is { } interruptedOutcome)
            {
                HandleIncompleteAlbumIfNeeded(job, chosenFolder, interruptedOutcome, config);
                return new(false, new(false, interruptedOutcome, null, state.LastChosenFolder));
            }

            if (!config.Search.NoBrowseFolder
                && selection.RetrieveCurrent
                && !chosenFolder.IsFullyRetrieved
                && !state.RetrievedFolders.Contains(chosenFolder.FolderPath))
            {
                var retrieval = await jobs.ProcessFolderRetrieval(chosenFolder, job, consumeJobSlot: false);
                if (retrieval.RetrievalCompleted)
                    state.RetrievedFolders.Add(chosenFolder.FolderPath);
                if (retrieval.NewFilesFoundCount > 0)
                {
                    await RunAlbumDownloads(job, config, organizer, chosenFolder, cts);
                    if (TryGetInterruptedAlbumOutcome(job, chosenFolder) is { } interruptedOutcomeAfterRetrieval)
                    {
                        HandleIncompleteAlbumIfNeeded(job, chosenFolder, interruptedOutcomeAfterRetrieval, config);
                        return new(false, new(false, interruptedOutcomeAfterRetrieval, null, state.LastChosenFolder));
                    }
                }
            }

            job.ResolvedTarget = chosenFolder;
            return new(true, null);
        }
        catch (OperationCanceledException)
        {
            var willTryNextFolder = !selection.WasPreselected
                && state.Tried < config.Transfer.MaxDownloadRetries
                && job.Results.Count > 1;
            MarkUnfinishedAlbumFilesCancelled(job, chosenFolder);
            ReportStaleAlbumCandidateIfNeeded(job, chosenFolder, selection.WasPreselected, willTryNextFolder);

            incompleteAlbums.HandleIncompleteAlbum(job, chosenFolder, config.ResolveIncompleteAlbumAction(), config);

            if (job.Cts != null && job.Cts.IsCancellationRequested)
            {
                var outcome = JobOutcome.Cancelled(JobOrchestrator.CancellationSourceForDerivedCancellation(job, job.EnsureTrackJobs(chosenFolder).Cast<Job>().ToArray()));
                return new(false, new(false, outcome, null, state.LastChosenFolder));
            }

            if (selection.WasPreselected)
                return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.AllDownloadsFailed));

            state.FailedDownloadCandidate = true;
            return new(false, null);
        }
    }

    async Task RunAlbumDownloads(
        AlbumJob job,
        DownloadSettings config,
        FileManager organizer,
        AlbumFolder folder,
        CancellationTokenSource cts)
    {
        var tasks = job.EnsureTrackJobs(folder).Select(async af =>
        {
            if (af.LifecycleState != JobLifecycleState.Pending) return;
            if (af.ResolvedTarget != null && af.Candidates == null)
                af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
            await songDownloads.DownloadEmbeddedSong(
                af,
                job,
                config,
                organizer,
                cts,
                cancelGroupOnFail: !af.IsNotAudio,
                finalizePlacement: !af.IsNotAudio);
        });
        await Task.WhenAll(tasks);
    }

    AlbumAudioDownloadResult ReturnSelectedFolderToManualPicker(
        AlbumJob job,
        JobContext ctx,
        FileManager organizer,
        AlbumAudioDownloadState state,
        AlbumFolder? failedFolder,
        JobFailureReason finalReason)
    {
        if (job.DownloadBehavior != DownloadBehavior.Manual || job.Cts?.IsCancellationRequested == true)
            return new(false, JobOutcome.Failed(finalReason), null, failedFolder ?? state.LastChosenFolder);

        if (failedFolder != null)
            job.Results.RemoveAll(folder => SameAlbumFolder(folder, failedFolder));

        job.ResolvedTarget = null;
        job.AllowBrowseResolvedTarget = true;
        job.SkipResolvedTargetTrackCountVerification = false;
        organizer.SetremoteBaseDir(null);

        if (job.Results.Count == 0)
            return new(false, JobOutcome.Failed(finalReason), null, failedFolder ?? state.LastChosenFolder);

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
        return new(false, JobOutcome.AwaitingSelection(), null, failedFolder ?? state.LastChosenFolder);
    }

    void ReportStaleAlbumCandidateIfNeeded(AlbumJob job, AlbumFolder folder, bool wasPreselected, bool willTryNextFolder)
    {
        var staleTrack = job.EnsureTrackJobs(folder)
            .FirstOrDefault(song => StaleDownloadException.IsStaleFailureMessage(song.FailureMessage));
        if (staleTrack == null)
            return;

        var action = willTryNextFolder
            ? "trying next album candidate"
            : wasPreselected && job.DownloadBehavior == DownloadBehavior.Manual
                ? "returning to album selection"
                : "no more album candidates will be tried";
        var folderDisplay = $"{folder.Username}\\{folder.FolderPath}";
        context.Events.RaiseJobMessage(
            job,
            LogLevel.Warning,
            null,
            $"album candidate became stale; {action}: {folderDisplay}\n    Error: {staleTrack.FailureMessage}");
    }

    JobOutcome? TryGetInterruptedAlbumOutcome(AlbumJob job, AlbumFolder folder)
    {
        var tracks = job.EnsureTrackJobs(folder);
        if (job.Cts?.IsCancellationRequested == true)
        {
            var source = JobOrchestrator.CancellationSourceForDerivedCancellation(job, tracks.Cast<Job>().ToArray());
            MarkUnfinishedAlbumFilesCancelled(job, folder);
            return JobOutcome.Cancelled(source);
        }

        var cancelledTracks = tracks
            .Where(song => song.FailureReason == JobFailureReason.Cancelled)
            .ToList();
        if (cancelledTracks.Count > 0)
        {
            var source = JobOrchestrator.CancellationSourceForDerivedCancellation(job, cancelledTracks.Cast<Job>().ToArray());
            MarkUnfinishedAlbumFilesCancelled(job, folder);
            return JobOutcome.Cancelled(source);
        }

        var failedSong = tracks.FirstOrDefault(song =>
            !song.IsNotAudio
            &&
            song.LifecycleState == JobLifecycleState.Terminal
            && !JobOrchestrator.IsSuccessfulTerminal(song));

        return failedSong == null
            ? null
            : JobOutcome.Failed(
                failedSong.FailureReason == JobFailureReason.None ? JobFailureReason.AllDownloadsFailed : failedSong.FailureReason,
                failedSong.FailureMessage);
    }

    void HandleIncompleteAlbumIfNeeded(
        AlbumJob job,
        AlbumFolder folder,
        JobOutcome outcome,
        DownloadSettings config)
    {
        if (outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled)
            incompleteAlbums.HandleIncompleteAlbum(job, folder, config.ResolveIncompleteAlbumAction(), config);
    }

    void MarkUnfinishedAlbumFilesCancelled(AlbumJob job, AlbumFolder folder)
    {
        foreach (var song in job.EnsureTrackJobs(folder).Where(song => song.LifecycleState != JobLifecycleState.Terminal))
        {
            song.MarkCancellationSource(JobCancellationSource.ParentJob);
            song.SetCancelled(JobCancellationSource.ParentJob);
        }
    }


}
