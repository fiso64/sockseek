using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

internal sealed record InitialDownloadTarget(string Path, bool PublishToDuplicateCache);

internal sealed record OutputFinalizationResult(JobOutcome Outcome, FileOrganizationException? OrganizationException)
{
    public static OutputFinalizationResult Completed(JobOutcome outcome)
        => new(outcome, null);

    public static OutputFinalizationResult Failed(FileOrganizationException exception)
        => new(
            JobOutcome.Failed(
                JobFailureReason.Other,
                exception.Message,
                SockseekLog.ExceptionDetail(exception)),
            exception);
}

// Owns the post-download boundary where a temporary/staged path becomes the
// user-visible final path. A job should not commit success until this layer has
// either published the final duplicate-cache entry or returned a failure outcome.
internal sealed class OutputFinalizer
{
    private readonly IDownloadRegistry registry;

    public OutputFinalizer(IDownloadRegistry registry)
    {
        this.registry = registry;
    }

    public InitialDownloadTarget GetInitialDownloadTarget(
        DownloadSettings config,
        SongJob song,
        FileManager organizer,
        FileCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(config.Output.NameFormat))
            return new(organizer.GetSavePath(candidate.Filename), PublishToDuplicateCache: true);

        var parentDir = string.IsNullOrWhiteSpace(config.Output.ParentDir)
            ? Directory.GetCurrentDirectory()
            : config.Output.ParentDir;
        var sourceFileName = Utils.GetFileNameSlsk(candidate.Filename).CleanPath(config.Output.InvalidReplaceStr);
        var stagingPath = Path.Join(parentDir, ".sockseek-staging", song.Id.ToString("N"), sourceFileName);

        return new(stagingPath, PublishToDuplicateCache: false);
    }

    public OutputFinalizationResult FinalizeSongPlacement(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        FileManager organizer,
        bool organize)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded || !organize)
            return OutputFinalizationResult.Completed(outcome);

        lock (registry.DownloadedFiles)
        {
            song.UpdateActivity(JobActivityPhase.Organizing);
            try
            {
                organizer.OrganizeSong(song);
                PublishDownloadedFileCache(song, outcome);
                return OutputFinalizationResult.Completed(outcome);
            }
            catch (FileOrganizationException ex)
            {
                SockseekLog.Jobs.Error($"[{song.DisplayId}] SongJob: {ex.Message} {SockseekLog.ExceptionSummary(ex.InnerException ?? ex)}");
                CleanupStagedDownloadAfterOrganizationFailure(song, parentJob.Config.Output);
                return OutputFinalizationResult.Failed(ex);
            }
        }
    }

    public OutputFinalizationResult FinalizeAlbumPlacement(
        AlbumJob album,
        FileManager organizer,
        List<SongJob>? chosenFiles,
        List<SongJob>? additionalImages,
        JobOutcome outcome)
    {
        if (chosenFiles == null || string.IsNullOrEmpty(album.DownloadPath))
        {
            PublishDownloadedFileCache(chosenFiles);
            PublishDownloadedFileCache(additionalImages);
            return OutputFinalizationResult.Completed(outcome);
        }

        lock (registry.DownloadedFiles)
        {
            try
            {
                organizer.OrganizeAlbum(album, chosenFiles, additionalImages);
                PublishDownloadedFileCache(chosenFiles);
                PublishDownloadedFileCache(additionalImages);
                return OutputFinalizationResult.Completed(outcome);
            }
            catch (FileOrganizationException ex)
            {
                SockseekLog.Jobs.Error($"[{album.DisplayId}] AlbumJob: {ex.Message} {SockseekLog.ExceptionSummary(ex.InnerException ?? ex)}");
                return OutputFinalizationResult.Failed(ex);
            }
        }
    }

    public void PublishDownloadedFileCache(SongJob song)
    {
        if (song.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        PublishDownloadedFileCache(song, JobOutcome.Done(song.DownloadPath, song.ChosenCandidate));
    }

    public void PublishDownloadedFileCache(SongJob song, JobOutcome outcome)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        var candidate = song.ChosenCandidate;
        if (candidate == null || string.IsNullOrEmpty(song.DownloadPath))
            return;

        var fileKey = candidate.Username + '\\' + candidate.Filename;
        registry.DownloadedFiles[fileKey] = new FileDownloadResult(song.DownloadPath, candidate);
    }

    public void PublishDownloadedFileCache(IEnumerable<SongJob>? songs)
    {
        if (songs == null)
            return;

        foreach (var song in songs)
            PublishDownloadedFileCache(song);
    }

    private static void CleanupStagedDownloadAfterOrganizationFailure(SongJob song, OutputSettings output)
    {
        if (string.IsNullOrWhiteSpace(song.DownloadPath))
            return;

        var parentDir = string.IsNullOrWhiteSpace(output.ParentDir)
            ? Directory.GetCurrentDirectory()
            : output.ParentDir;
        var stagingRoot = Path.Join(parentDir, ".sockseek-staging");
        if (!Utils.IsInDirectory(song.DownloadPath, stagingRoot, strict: true))
            return;

        try
        {
            Utils.DeleteFileAndParentsIfEmpty(song.DownloadPath, parentDir);
            song.DownloadPath = null;
        }
        catch (Exception ex)
        {
            SockseekLog.Jobs.Warn($"[{song.DisplayId}] SongJob: failed to clean staged file '{song.DownloadPath}' after organization failure: {SockseekLog.ExceptionSummary(ex)}");
        }
    }
}
