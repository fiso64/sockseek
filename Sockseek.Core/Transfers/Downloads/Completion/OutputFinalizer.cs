using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Core.Services;

internal sealed record InitialDownloadTarget(string Path, bool PublishToDuplicateCache);

internal sealed record OutputFinalizationResult(JobOutcome Outcome, FileOrganizationException? OrganizationException)
{
    public static OutputFinalizationResult Completed(JobOutcome outcome)
        => new(outcome, null);

    public static OutputFinalizationResult Failed(FileOrganizationException exception, string? retainedPath = null)
        => new(
            JobOutcome.Failed(
                JobFailureReason.Other,
                retainedPath == null
                    ? exception.Message
                    : $"{exception.Message}{Environment.NewLine}Downloaded payload retained at: {retainedPath}",
                Diagnostics.ExceptionText.Detail(exception)),
            exception);
}

// Owns the post-download boundary where a temporary/staged path becomes the
// user-visible final path. A job should not commit success until this layer has
// either published the final duplicate-cache entry or returned a failure outcome.
internal sealed class OutputFinalizer
{
    private readonly DownloadFileOperations files;
    private readonly DownloadEvents? events;
    private readonly ILogger<OutputFinalizer> logger;

    public OutputFinalizer(
        DownloadFileOperations files,
        DownloadEvents? events = null,
        ILogger<OutputFinalizer>? logger = null)
    {
        this.files = files;
        this.events = events;
        this.logger = logger ?? NullLogger<OutputFinalizer>.Instance;
    }

    public ValueTask<InitialDownloadTarget> GetInitialDownloadTargetAsync(
        DownloadSettings config, SongJob song, FileManager organizer, FileCandidate candidate)
        => GetInitialDownloadTargetAsync(config, song, organizer, candidate.Filename);

    public ValueTask<InitialDownloadTarget> GetInitialDownloadTargetAsync(
        DownloadSettings config, SongJob song, FileManager organizer, PeerFileTarget target)
        => GetInitialDownloadTargetAsync(config, song, organizer, target.Filename);

    private ValueTask<InitialDownloadTarget> GetInitialDownloadTargetAsync(
        DownloadSettings config, SongJob song, FileManager organizer, string filename)
        => files.WithCacheAsync(_ =>
        {
            if (string.IsNullOrWhiteSpace(config.Output.NameFormat))
                return new InitialDownloadTarget(organizer.GetSavePath(filename), PublishToDuplicateCache: true);
            string sourceName = Utils.GetFileNameSlsk(filename).CleanPath(config.Output.InvalidReplaceStr);
            return new InitialDownloadTarget(Path.Join(OutputStaging.Root(config.Output), song.Id.ToString("N"), sourceName),
                PublishToDuplicateCache: false);
        });

    public ValueTask<OutputFinalizationResult> FinalizeSongPlacementAsync(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        FileManager organizer,
        bool finalizePlacement,
        bool allowOverwrite,
        bool allowUnownedReplacement = false)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded || !finalizePlacement)
            return ValueTask.FromResult(OutputFinalizationResult.Completed(outcome));

        return files.WithCacheAsync(cache =>
        {
            song.UpdateActivity(JobActivityPhase.Organizing);
            try
            {
                organizer.OrganizeDownloadedFile(song, CreateReplacementGuard(cache, allowOverwrite, allowUnownedReplacement));
                EnsureFileLeftStaging(song, parentJob.Config.Output);
                PublishDownloadedFileCache(cache, song, outcome);
                return OutputFinalizationResult.Completed(outcome);
            }
            catch (Exception ex)
            {
                var organizationException = AsOrganizationException(ex, song.DownloadPath, targetPath: null);
                if (FindExistingOutput(ex) is { } existing)
                {
                    try
                    {
                        if (OutputStaging.Contains(song.DownloadPath, parentJob.Config.Output))
                        {
                            Utils.DeleteFileAndParentsIfEmpty(
                                song.DownloadPath!,
                                OutputStaging.Root(parentJob.Config.Output));
                        }
                        song.DownloadPath = existing.OutputPath;
                        return OutputFinalizationResult.Completed(
                            JobOutcome.AlreadyExists(existing.OutputPath));
                    }
                    catch (Exception cleanupException)
                    {
                        organizationException = AsOrganizationException(
                            cleanupException,
                            song.DownloadPath,
                            existing.OutputPath);
                    }
                }
                LogOrganizationFailure(song, organizationException);
                return OutputFinalizationResult.Failed(
                    organizationException,
                    RetainedStagedPayload(song.DownloadPath, parentJob.Config.Output));
            }
        });
    }

    public ValueTask<OutputFinalizationResult> FinalizeAlbumPlacementAsync(
        AlbumJob album,
        FileManager organizer,
        List<SongJob>? chosenFiles,
        List<SongJob>? additionalImages,
        JobOutcome outcome,
        bool allowOverwrite,
        bool allowUnownedReplacement = false)
    {
        var filesToOrganize = (chosenFiles ?? [])
            .Concat(additionalImages ?? [])
            .Distinct()
            .ToList();
        if (filesToOrganize.Count == 0)
            return ValueTask.FromResult(OutputFinalizationResult.Completed(outcome));

        return files.WithCacheAsync(cache =>
        {
            var previousPaths = filesToOrganize.Select(file => (File: file, Path: file.DownloadPath)).ToArray();
            try
            {
                if (!string.IsNullOrEmpty(album.DownloadPath))
                    organizer.OrganizeAlbum(album, filesToOrganize, additionalImages,
                        beforeReplace: CreateReplacementGuard(cache, allowOverwrite, allowUnownedReplacement));
                EnsureAlbumFilesLeftStaging(album, chosenFiles, additionalImages, outcome);
                RefreshSuccessfulAlbumPath(album, filesToOrganize, outcome);
                PublishDownloadedFileCache(cache, chosenFiles);
                PublishDownloadedFileCache(cache, additionalImages);
                return OutputFinalizationResult.Completed(outcome);
            }
            catch (Exception ex)
            {
                var strandedPath = SuccessfulFiles(chosenFiles, additionalImages)
                    .Select(file => file.DownloadPath)
                    .FirstOrDefault(path => OutputStaging.Contains(path, album.Config.Output));
                var organizationException = AsOrganizationException(ex, strandedPath, album.DownloadPath);
                LogOrganizationFailure(album, organizationException);
                return OutputFinalizationResult.Failed(
                    organizationException,
                    RetainedStagedPayload(strandedPath, album.Config.Output));
            }
            finally
            {
                // Album-owned placement can move children that are already
                // terminal. Their live/retained metadata still needs the new
                // path, including files moved before a later sibling failed.
                foreach (var (file, previousPath) in previousPaths)
                    if (!string.Equals(previousPath, file.DownloadPath, StringComparison.Ordinal))
                    {
                        if (file.DownloadPath is { } path) events?.RaiseTransferOutputChanged(file, path);
                        events?.RaiseJobStateChanged(file);
                    }
            }
        });
    }

    private void LogOrganizationFailure(Job job, FileOrganizationException exception)
    {
        DownloadLogMessages.ComponentFailed(
            logger,
            exception,
            "output-organization",
            job.Id);
        events?.RaiseJobMessage(
            job,
            LogLevel.Error,
            null,
            "download completed, but organizing the output failed");
    }

    public ValueTask<bool> PublishDownloadedFileCacheAsync(SongJob song, JobOutcome outcome)
        => files.WithCacheAsync(cache => { PublishDownloadedFileCache(cache, song, outcome); return true; });

    private static void PublishDownloadedFileCache(DownloadFileOperations.Cache cache, SongJob song, JobOutcome outcome)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded) return;
        PeerFileTarget? target = song.ResolvedPeerTarget;
        if (target == null || string.IsNullOrEmpty(song.DownloadPath)) return;
        if (song.Config != null && OutputStaging.Contains(song.DownloadPath, song.Config.Output)) return;
        cache.Publish(song.DownloadPath, target, song.WorkflowId);
    }

    private static void PublishDownloadedFileCache(DownloadFileOperations.Cache cache, IEnumerable<SongJob>? songs)
    {
        if (songs == null) return;
        foreach (var song in songs)
            if (song.TerminalOutcome == JobTerminalOutcome.Succeeded)
                PublishDownloadedFileCache(cache, song, JobOutcome.Done(song.DownloadPath, song.ResolvedTarget, song.DownloadSource));
    }

    private static Action<string> CreateReplacementGuard(
        DownloadFileOperations.Cache cache, bool allowOverwrite, bool allowUnownedReplacement = false)
        => path =>
        {
            if (!allowOverwrite && File.Exists(path)
                && (!allowUnownedReplacement || cache.HasPublishedPathOwner(path)))
                throw new OutputPathAlreadyExistsException(path);
            cache.InvalidatePath(path);
        };

    public async Task HandleIncompleteAlbumAsync(AlbumJob job, AlbumFolder folder, ResolvedIncompleteAlbumAction action, DownloadSettings config)
    {
        if (action.Kind == IncompleteAlbumActionKind.Keep)
            return;

        var failedAlbumPath = action.Path;
        var outputParentDir = config.Output.ParentDir;
        var tracks = job.EnsureTrackJobs(folder);
        var filesToHandle = await files.WithCacheAsync(_ => tracks
            .Where(song => IsIncompleteAlbumActionFile(song, config.Output)).ToList());

        if (filesToHandle.Count == 0)
        {
            DownloadLogMessages.JobDecision(
                logger,
                job.Id,
                "incomplete-album-action-not-needed",
                0);
            return;
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
        {
            events?.RaiseJobStatus(job, "deleting files");
        }
        else if (action.Kind == IncompleteAlbumActionKind.Move)
        {
            if (string.IsNullOrEmpty(outputParentDir))
                throw new InvalidOperationException("Cannot move incomplete album files because Output.ParentDir is not set.");
            if (string.IsNullOrEmpty(failedAlbumPath))
                throw new InvalidOperationException("Cannot move incomplete album files because incomplete album action path is not set.");

            events?.RaiseJobStatus(job, $"moving to {failedAlbumPath}");
        }

        foreach (var af in filesToHandle)
        {
            var downloadPath = af.DownloadPath!;
            try
            {
                await files.WithCacheAsync(cache =>
                {
                    if (action.Kind == IncompleteAlbumActionKind.Delete)
                    {
                        cache.InvalidatePath(downloadPath);
                        File.Delete(downloadPath);
                    }
                    else if (action.Kind == IncompleteAlbumActionKind.Move)
                    {
                        var relativeBase = outputParentDir
                            ?? throw new InvalidOperationException("Cannot move incomplete album files because Output.ParentDir is not set.");
                        var targetBase = failedAlbumPath
                            ?? throw new InvalidOperationException("Cannot move incomplete album files because incomplete album action path is not set.");
                        var newPath = Path.Join(targetBase, Path.GetRelativePath(relativeBase, downloadPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                        CreateReplacementGuard(cache, allowOverwrite: !config.Skip.SkipExisting)(newPath);
                        cache.InvalidatePath(downloadPath);
                        Utils.Move(downloadPath, newPath);
                        af.DownloadPath = newPath;
                        events?.RaiseTransferOutputChanged(af, newPath);
                        events?.RaiseJobStateChanged(af);
                    }

                    var downloadParent = Path.GetDirectoryName(downloadPath);
                    if (!string.IsNullOrEmpty(downloadParent) && !string.IsNullOrEmpty(outputParentDir))
                        Utils.DeleteAncestorsIfEmpty(downloadParent, outputParentDir);
                    return true;
                });
            }
            catch (Exception e)
            {
                DownloadLogMessages.ComponentFailed(
                    logger,
                    e,
                    "incomplete-album-action",
                    job.Id);
                events?.RaiseJobMessage(
                    job,
                    LogLevel.Error,
                    null,
                    "unable to move or delete a completed file after the album failed");
            }
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
            events?.RaiseJobStatus(job, "deleted files");
        else if (action.Kind == IncompleteAlbumActionKind.Move)
            events?.RaiseJobStatus(job, $"moved to {failedAlbumPath}");
    }

    static bool IsIncompleteAlbumActionFile(SongJob song, OutputSettings output)
        => song.TerminalOutcome == JobTerminalOutcome.Succeeded
            && !string.IsNullOrEmpty(song.DownloadPath)
            && !OutputStaging.Contains(song.DownloadPath, output)
            && !song.DownloadPath.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)
            && File.Exists(song.DownloadPath);

    private static void EnsureFileLeftStaging(SongJob song, OutputSettings output)
    {
        if (!OutputStaging.Contains(song.DownloadPath, output))
            return;

        throw new FileOrganizationException(
            $"Finalization left the downloaded file in Sockseek staging: '{song.DownloadPath}'.",
            song.DownloadPath!,
            "",
            new InvalidOperationException("A leaf that owns final placement cannot commit from staging."));
    }

    private static void EnsureAlbumFilesLeftStaging(
        AlbumJob album,
        IEnumerable<SongJob>? chosenFiles,
        IEnumerable<SongJob>? additionalImages,
        JobOutcome outcome)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        var stranded = SuccessfulFiles(chosenFiles, additionalImages)
            .FirstOrDefault(file => OutputStaging.Contains(file.DownloadPath, album.Config.Output));
        if (stranded == null)
            return;

        throw new FileOrganizationException(
            $"Album finalization left a downloaded file in Sockseek staging: '{stranded.DownloadPath}'.",
            stranded.DownloadPath!,
            album.DownloadPath ?? "",
            new InvalidOperationException("A successful album cannot retain a successfully downloaded child in staging."));
    }

    private static IEnumerable<SongJob> SuccessfulFiles(
        IEnumerable<SongJob>? chosenFiles,
        IEnumerable<SongJob>? additionalImages)
        => (chosenFiles ?? [])
            .Concat(additionalImages ?? [])
            .Where(file => file.TerminalOutcome == JobTerminalOutcome.Succeeded)
            .Distinct();

    private static void RefreshSuccessfulAlbumPath(
        AlbumJob album,
        IEnumerable<SongJob> files,
        JobOutcome outcome)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        var finalizedPaths = files
            .Where(file => file.TerminalOutcome == JobTerminalOutcome.Succeeded)
            .Select(file => file.DownloadPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !OutputStaging.Contains(path, album.Config.Output))
            .Cast<string>()
            .ToList();
        if (finalizedPaths.Count > 0)
            album.DownloadPath = Utils.GreatestCommonDirectory(finalizedPaths);
    }

    private static FileOrganizationException AsOrganizationException(
        Exception exception,
        string? sourcePath,
        string? targetPath)
        => exception as FileOrganizationException
            ?? new FileOrganizationException(
                "Failed to finalize downloaded file placement.",
                sourcePath ?? "",
                targetPath ?? "",
                exception);

    private static OutputPathAlreadyExistsException? FindExistingOutput(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is OutputPathAlreadyExistsException existing)
                return existing;
        }
        return null;
    }

    private static string? RetainedStagedPayload(string? path, OutputSettings output)
        => OutputStaging.Contains(path, output) && File.Exists(path)
            ? path
            : null;
}
