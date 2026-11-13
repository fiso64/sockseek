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

    public AlbumDownloadExecutor(DownloadExecutionContext context, JobOrchestrator jobs, SongDownloadExecutor songDownloads)
    {
        this.context = context;
        this.jobs = jobs;
        this.songDownloads = songDownloads;
        imageDownloads = new AlbumImageDownloadExecutor(context, songDownloads);
    }

    private static bool SameAlbumFolder(AlbumFolder left, AlbumFolder right)
        => string.Equals(left.Username, right.Username, StringComparison.Ordinal)
            && string.Equals(left.FolderPath, right.FolderPath, StringComparison.Ordinal);

    public async Task<JobOutcome> ProcessAlbumDownload(AlbumJob job, JobContext ctx)
    {
        try
        {
            return await DownloadAndFinalizeAlbum(job, ctx);
        }
        finally
        {
            // Files placed by the album owner must publish their current paths,
            // including retained payloads when album organization fails.
            await songDownloads.CompleteAlbumTransfersAsync(job.TrackJobs);
        }
    }

    private async Task<JobOutcome> DownloadAndFinalizeAlbum(AlbumJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(
            job,
            config.Output,
            config.Extraction,
            context.LoggerFactory.CreateLogger<FileManager>(),
            ctx.OutputScope);
        var audioResult = await TryDownloadAlbumAudio(job, ctx, organizer);
        var completion = PrepareAlbumAudioOutcome(job, audioResult, ctx);
        var chosenFiles = completion.ChosenFiles;

        if (completion.Outcome.LifecycleState == JobLifecycleState.AwaitingSelection)
        {
            JobOutcomeCommitter.Commit(job, completion.Outcome);
            context.UpdateOutputs(job, ctx);
            return completion.Outcome;
        }

        MarkCancelledAlbumFiles(job, audioResult, completion.Outcome);
        var images = await DownloadAlbumImagesIfNeeded(job, ctx, organizer, audioResult, chosenFiles);
        var outcome = config.Output.AlbumArtOnly
            ? DeriveAlbumArtOnlyOutcome(job, completion.Outcome, images.ChosenFiles, images.AdditionalImages)
            : completion.Outcome;
        if (!string.IsNullOrEmpty(job.DownloadPath))
            job.UpdateActivity(JobActivityPhase.Organizing);
        var finalization = await context.OutputFinalizer.FinalizeAlbumPlacementAsync(
            job,
            organizer,
            images.ChosenFiles,
            images.AdditionalImages,
            outcome,
            allowOverwrite: !config.Skip.SkipExisting);
        outcome = finalization.Outcome;
        if (finalization.OrganizationException != null && job.ResolvedTarget != null)
        {
            await HandleIncompleteAlbumIfNeededAsync(job, job.ResolvedTarget, outcome, config);
        }

        var postProcessOutcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(job, outcome);
        postProcessOutcome = await songDownloads.RunOnCompleteIfApplicable(job, null, ctx, postProcessOutcome);

        var finalOutcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(job, postProcessOutcome);
        JobOutcomeCommitter.Commit(job, finalOutcome);
        context.UpdateOutputs(job, ctx);
        return finalOutcome;
    }

    AlbumDownloadCompletion PrepareAlbumAudioOutcome(AlbumJob job, AlbumAudioDownloadResult audioResult, JobContext ctx)
    {
        var chosenFiles = audioResult.ChosenFiles;
        JobOutcome outcome;

        if (audioResult.Succeeded && chosenFiles != null)
        {
            // Existing audio still anchors ancillary-file placement in the album folder.
            var completedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.IsSuccessfulTerminal && !string.IsNullOrEmpty(af.DownloadPath));

            if (completedAudio.Any())
            {
                var downloadPath = Utils.GreatestCommonDirectory(completedAudio.Select(af => af.DownloadPath!));
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

        Info(job, "downloading additional images");
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
        bool verifyStrictAlbumQuality = job.ValidationRequirement != AlbumValidationRequirement.UserAccepted
            && config.Search.StrictAlbumQuality && activeQuality.IsActive;

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
            // Completed files from the discarded candidate still own transfer
            // histories, even though the next candidate replaces these children.
            await songDownloads.CompleteAlbumTransfersAsync(job.TrackJobs);
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

    AlbumCandidateSelection? SelectAlbumCandidate(AlbumJob job, AlbumAudioDownloadState state)
    {
        bool wasPreselected = job.ResolvedTarget != null;
        bool retrieveCurrent = !wasPreselected
            || job.DirectoryResolutionPolicy == AlbumDirectoryResolutionPolicy.CompleteIfNeeded;
        state.Index = 0;

        if (wasPreselected)
        {
            var chosenFolder = job.ResolvedTarget!;
            state.Index = job.Results.Contains(chosenFolder) ? job.Results.IndexOf(chosenFolder) : 0;
            return new(chosenFolder, wasPreselected, retrieveCurrent);
        }

        var candidates = Enumerable.Range(0, job.Results.Count)
            .Where(index => string.IsNullOrWhiteSpace(state.FilterText)
                || job.Results[index].Files.Any(file => file.Filename.ContainsIgnoreCase(state.FilterText)));
        state.Index = context.PeerFailures.ForAutomaticSelection(job.WorkflowId, candidates,
            index => job.Results[index].Username, job.Config.Search).FirstOrDefault(-1);
        return state.Index < 0 ? null : new(job.Results[state.Index], wasPreselected, retrieveCurrent);
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
        bool verifyTrackCount = !selection.WasPreselected
            || job.ValidationRequirement == AlbumValidationRequirement.Standard;
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
                Info(job, "album track count verification was cancelled; skipping folder");
                if (selection.WasPreselected)
                    return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.NoMatchingResults));

                job.Results.RemoveAt(state.Index);
                if (--state.TrackCountRetries <= 0)
                {
                    Info(job, $"failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times; skipping album");
                    return new(false, new(false, DownloadOutcomes.NoMatchingCandidates(), null, state.LastChosenFolder));
                }

                return new(true, null);
            }

            knownCount = KnownAudioCount();
        }

        var trackCountCheck = ConditionSatisfactionPolicy.CheckAlbumTrackCount(folderCond, knownCount);
        if (trackCountCheck.FailedAboveMaximum && trackCountCheck.Maximum is { } maximum)
            Info(job, $"file count ({trackCountCheck.AudioFileCount}) above maximum ({maximum}); skipping folder");
        if (trackCountCheck.FailedBelowMinimum && trackCountCheck.Minimum is { } minimum)
            Info(job, $"file count ({trackCountCheck.AudioFileCount}) below minimum ({minimum}); skipping folder");

        if (trackCountCheck.Satisfied)
            return new(false, null);

        if (selection.WasPreselected)
        {
            Info(job, "preselected folder failed album track count condition; skipping album");
            return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.NoMatchingResults));
        }

        job.Results.RemoveAt(state.Index);
        if (--state.TrackCountRetries <= 0)
        {
            Info(job, $"failed album track count condition {config.Transfer.AlbumTrackCountMaxRetries} times; skipping album");
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
                Info(job, "strict album quality verification was cancelled; skipping folder");
                if (selection.WasPreselected)
                    return new(false, ReturnSelectedFolderToManualPicker(job, ctx, organizer, state, chosenFolder, JobFailureReason.NoMatchingResults));

                job.Results.RemoveAt(state.Index);
                return new(true, null);
            }
        }

        var qualityCoverage = AlbumQualityPolicy.Evaluate(chosenFolder, job.Config.Search.NecessaryCond, activeQuality);
        if (ConditionSatisfactionPolicy.AlbumQualityIsAcceptable(qualityCoverage, strictAlbumQuality: true))
            return new(false, null);

        Info(job, $"strict album quality failed ({qualityCoverage.MatchingFileCount}/{qualityCoverage.AudioFileCount} matching audio files); skipping folder");
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

        // Resolve optional full-folder contents before freezing this candidate's
        // immutable transfer attempt. A failed retrieval leaves the known search
        // selection intact and still exact.
        if (!config.Search.NoBrowseFolder
            && selection.RetrieveCurrent
            && !chosenFolder.IsFullyRetrieved
            && !state.RetrievedFolders.Contains(chosenFolder.FolderPath))
        {
            var retrieval = await jobs.ProcessFolderRetrieval(chosenFolder, job, consumeJobSlot: false);
            if (retrieval.RetrievalCompleted)
                state.RetrievedFolders.Add(chosenFolder.FolderPath);
        }

        job.BeginAlbumTransferAttempt(chosenFolder);
        job.UpdateActivity(JobActivityPhase.Downloading);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        state.Tried++;

        try
        {
            await RunAlbumDownloads(job, config, organizer, chosenFolder, cts);
            if (TryGetInterruptedAlbumOutcome(job, chosenFolder) is { } interruptedOutcome)
            {
                await HandleIncompleteAlbumIfNeededAsync(job, chosenFolder, interruptedOutcome, config);
                return new(false, new(false, interruptedOutcome, null, state.LastChosenFolder));
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

            await context.OutputFinalizer.HandleIncompleteAlbumAsync(job, chosenFolder, config.ResolveIncompleteAlbumAction(), config);

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
        await BoundedAsync.ForEachAsync(
            job.EnsureTrackJobs(folder),
            context.Runtime.ConcurrentJobLimit,
            async af =>
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
        job.DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.CompleteIfNeeded;
        job.ValidationRequirement = AlbumValidationRequirement.Standard;
        organizer.SetremoteBaseDir(null);

        if (job.Results.Count == 0)
            return new(false, JobOutcome.Failed(finalReason), null, failedFolder ?? state.LastChosenFolder);

        context.UpdateOutputs(job, ctx);
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

    async Task HandleIncompleteAlbumIfNeededAsync(
        AlbumJob job,
        AlbumFolder folder,
        JobOutcome outcome,
        DownloadSettings config)
    {
        if (outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled)
            await context.OutputFinalizer.HandleIncompleteAlbumAsync(job, folder, config.ResolveIncompleteAlbumAction(), config);
    }

    void MarkUnfinishedAlbumFilesCancelled(AlbumJob job, AlbumFolder folder)
    {
        foreach (var song in job.EnsureTrackJobs(folder).Where(song => song.LifecycleState != JobLifecycleState.Terminal))
        {
            JobOutcomeCommitter.Commit(song, JobOutcome.Cancelled(JobCancellationSource.ParentJob));
        }
    }

    private void Info(Job job, string message)
        => context.Events.RaiseJobMessage(
            job,
            LogLevel.Information,
            null,
            message);


}
