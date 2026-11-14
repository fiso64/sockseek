using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Transfers.Downloads.Skipping;

internal sealed class SkipEvaluationCoordinator
{
    private sealed record Match(string Path, ExistingFileSource Source);
    private readonly Func<Job, JobContext> getContext;
    private readonly Action<Job> raiseBuildingMusicDirectoryIndex;
    private readonly ILogger<SkipEvaluationCoordinator> logger;

    public SkipEvaluationCoordinator(
        Func<Job, JobContext> getContext,
        Action<Job> raiseBuildingMusicDirectoryIndex,
        ILogger<SkipEvaluationCoordinator> logger)
    {
        this.getContext = getContext;
        this.raiseBuildingMusicDirectoryIndex = raiseBuildingMusicDirectoryIndex;
        this.logger = logger;
    }

    public bool TrySetAlreadyExists(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        if (!song.CanBeSkipped) return false;
        var match = FindExistingSong(job, song, skipCtx);

        if (match != null)
        {
            JobOutcomeCommitter.Commit(song, JobOutcome.AlreadyExists(match.Path, match.Source));
            AlreadyExists(song);
        }

        return match != null;
    }

    public bool TrySetJobAlreadyExists(Job job, JobContext ctx)
    {
        if (!job.CanBeSkipped) return false;
        var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.ResolveSearchSettings());
        Match? match = null;

        if (job is SongJob song)
        {
            return TrySetAlreadyExists(job, song, skipCtx);
        }
        else if (job is AlbumJob album)
        {
            match = FindExistingAlbum(job, album, ctx, skipCtx);
        }
        else
        {
            return false;
        }

        if (match != null)
        {
            JobOutcomeCommitter.Commit(job, JobOutcome.AlreadyExists(match.Path, match.Source));
            ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, match.Path);
            AlreadyExists(job);
        }

        return match != null;
    }

    public JobOutcome? TryGetJobAlreadyExistsOutcome(Job job, JobContext ctx)
    {
        if (!job.CanBeSkipped) return null;
        var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.ResolveSearchSettings());
        Match? match;

        if (job is SongJob song)
            match = FindExistingSong(job, song, skipCtx);
        else if (job is AlbumJob album)
            match = FindExistingAlbum(job, album, ctx, skipCtx);
        else
            return null;

        if (match == null)
            return null;

        AlreadyExists(job);
        return JobOutcome.AlreadyExists(match.Path, match.Source);
    }

    private Match? FindExistingSong(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        string? path = null;
        var jobCtx = getContext(job);

        if (jobCtx.OutputDirSkipper != null)
        {
            if (!jobCtx.OutputDirSkipper.IndexIsBuilt) jobCtx.OutputDirSkipper.BuildIndex();
            jobCtx.OutputDirSkipper.SongExists(song, skipCtx, out path);
            if (path != null) return new(path, SourceOf(jobCtx.OutputDirSkipper));
        }

        if (path == null && jobCtx.MusicDirSkipper != null)
        {
            if (!jobCtx.MusicDirSkipper.IndexIsBuilt)
            {
                raiseBuildingMusicDirectoryIndex(job);
                jobCtx.MusicDirSkipper.BuildIndex();
            }
            jobCtx.MusicDirSkipper.SongExists(song, skipCtx, out path);
            if (path != null) return new(path, SourceOf(jobCtx.MusicDirSkipper));
        }

        return null;
    }

    public bool TrySetNotFoundLastTime(SongJob song, DownloadOutputFile? indexEditor)
    {
        if (!song.CanBeSkipped || indexEditor == null) return false;
        var prev = indexEditor.PreviousRunResult(song);
        if (prev == null) return false;
        if (IsNotFoundFailure(prev.FailureReason) || prev.State == JobStateOld.NotFoundLastTime)
        {
            JobOutcomeCommitter.Commit(song, JobOutcome.Skipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason)));
            NotFoundPreviously(song);
            return true;
        }
        return false;
    }

    public bool TrySetNotFoundLastTimeForJob(Job job)
    {
        if (!job.CanBeSkipped) return false;
        var jobCtx = getContext(job);
        if (jobCtx.IndexEditor == null) return false;
        var prev = PreviousRunResult(job, jobCtx.IndexEditor);

        if (prev == null) return false;
        if (IsNotFoundFailure(prev.FailureReason) || prev.State == JobStateOld.NotFoundLastTime)
        {
            JobOutcomeCommitter.Commit(job, JobOutcome.Skipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason)));
            NotFoundPreviously(job);
            return true;
        }
        return false;
    }

    public JobOutcome? TryGetNotFoundLastTimeOutcome(Job job)
    {
        if (!job.CanBeSkipped) return null;
        var jobCtx = getContext(job);
        if (jobCtx.IndexEditor == null) return null;
        var prev = PreviousRunResult(job, jobCtx.IndexEditor);

        if (prev == null) return null;
        if (!IsNotFoundFailure(prev.FailureReason) && prev.State != JobStateOld.NotFoundLastTime)
            return null;

        NotFoundPreviously(job);
        return JobOutcome.Skipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason));
    }

    private void AlreadyExists(Job job)
        => DownloadLogMessages.JobDecision(
            logger,
            job.Id,
            "skipped-existing-output",
            null);

    private void NotFoundPreviously(Job job)
        => DownloadLogMessages.JobDecision(
            logger,
            job.Id,
            "skipped-not-found-in-prior-run",
            null);

    private Match? FindExistingAlbum(Job job, AlbumJob album, JobContext ctx, TrackSkipperContext skipCtx)
    {
        string? path = null;

        if (ctx.OutputDirSkipper != null)
        {
            if (!ctx.OutputDirSkipper.IndexIsBuilt) ctx.OutputDirSkipper.BuildIndex();
            ctx.OutputDirSkipper.AlbumExists(album, skipCtx, out path);
            if (path != null) return new(path, SourceOf(ctx.OutputDirSkipper));
        }

        if (path == null && ctx.MusicDirSkipper != null)
        {
            if (!ctx.MusicDirSkipper.IndexIsBuilt)
            {
                raiseBuildingMusicDirectoryIndex(job);
                ctx.MusicDirSkipper.BuildIndex();
            }
            ctx.MusicDirSkipper.AlbumExists(album, skipCtx, out path);
            if (path != null) return new(path, SourceOf(ctx.MusicDirSkipper));
        }

        return null;
    }

    private static ExistingFileSource SourceOf(TrackSkipper skipper)
        => skipper is IndexSkipper or IndexConditionalSkipper
            ? ExistingFileSource.CompletionHistory : ExistingFileSource.Library;

    private static IndexEntry? PreviousRunResult(Job job, DownloadOutputFile indexEditor)
        => job switch
        {
            SongJob song => indexEditor.PreviousRunResult(song),
            AlbumJob album => indexEditor.PreviousRunResult(album),
            _ => null,
        };

    private static bool IsNotFoundFailure(JobFailureReason reason)
        => reason is JobFailureReason.NoSearchResults or JobFailureReason.NoMatchingResults;

    private static JobFailureReason NotFoundFailureReasonOrDefault(JobFailureReason reason)
        => IsNotFoundFailure(reason) ? reason : JobFailureReason.NoMatchingResults;
}
