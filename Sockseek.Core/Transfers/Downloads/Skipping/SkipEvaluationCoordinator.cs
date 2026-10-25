using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.Skipping;

internal sealed class SkipEvaluationCoordinator
{
    private readonly Func<Job, JobContext> getContext;
    private readonly Action<Job> raiseBuildingMusicDirectoryIndex;

    public SkipEvaluationCoordinator(
        Func<Job, JobContext> getContext,
        Action<Job> raiseBuildingMusicDirectoryIndex)
    {
        this.getContext = getContext;
        this.raiseBuildingMusicDirectoryIndex = raiseBuildingMusicDirectoryIndex;
    }

    public bool TrySetAlreadyExists(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        var path = FindExistingSongPath(job, song, skipCtx);

        if (path != null)
        {
            song.SetAlreadyExists(path);
            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: skipped because matching file already exists at '{path}': {song}");
        }

        return path != null;
    }

    public bool TrySetJobAlreadyExists(Job job, JobContext ctx)
    {
        var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.ResolveSearchSettings());
        string? path = null;

        if (job is SongJob song)
        {
            return TrySetAlreadyExists(job, song, skipCtx);
        }
        else if (job is AlbumJob album)
        {
            path = FindExistingAlbumPath(job, album, ctx, skipCtx);
        }
        else
        {
            return false;
        }

        if (path != null)
        {
            if (job is AlbumJob albumJob)
                albumJob.SetAlreadyExists(path);
            else
                job.SetAlreadyExists();
            ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, path);
            SockseekLog.Jobs.Debug($"[{job.DisplayId}] {SockseekLog.JobTypeName(job)}: skipped because matching output already exists at '{path}': {job}");
        }

        return path != null;
    }

    public JobOutcome? TryGetJobAlreadyExistsOutcome(Job job, JobContext ctx)
    {
        var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.ResolveSearchSettings());
        string? path;

        if (job is SongJob song)
            path = FindExistingSongPath(job, song, skipCtx);
        else if (job is AlbumJob album)
            path = FindExistingAlbumPath(job, album, ctx, skipCtx);
        else
            return null;

        if (path == null)
            return null;

        SockseekLog.Jobs.Debug($"[{job.DisplayId}] {SockseekLog.JobTypeName(job)}: skipped because matching output already exists at '{path}': {job}");
        return JobOutcome.AlreadyExists(path);
    }

    public string? FindExistingSongPath(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        string? path = null;
        var jobCtx = getContext(job);

        if (jobCtx.OutputDirSkipper != null)
        {
            if (!jobCtx.OutputDirSkipper.IndexIsBuilt) jobCtx.OutputDirSkipper.BuildIndex();
            jobCtx.OutputDirSkipper.SongExists(song, skipCtx, out path);
        }

        if (path == null && jobCtx.MusicDirSkipper != null)
        {
            if (!jobCtx.MusicDirSkipper.IndexIsBuilt)
            {
                raiseBuildingMusicDirectoryIndex(job);
                jobCtx.MusicDirSkipper.BuildIndex();
            }
            jobCtx.MusicDirSkipper.SongExists(song, skipCtx, out path);
        }

        return path;
    }

    public bool TrySetNotFoundLastTime(SongJob song, M3uEditor? indexEditor)
    {
        if (indexEditor == null) return false;
        var prev = indexEditor.PreviousRunResult(song);
        if (prev == null) return false;
        if (IsNotFoundFailure(prev.FailureReason) || prev.State == JobStateOld.NotFoundLastTime)
        {
            song.SetSkipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason));
            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: skipped because prior index entry was {prev.State}/{prev.FailureReason}: {song}");
            return true;
        }
        return false;
    }

    public bool TrySetNotFoundLastTimeForJob(Job job)
    {
        var jobCtx = getContext(job);
        if (jobCtx.IndexEditor == null) return false;
        var prev = PreviousRunResult(job, jobCtx.IndexEditor);

        if (prev == null) return false;
        if (IsNotFoundFailure(prev.FailureReason) || prev.State == JobStateOld.NotFoundLastTime)
        {
            job.SetSkipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason));
            SockseekLog.Jobs.Debug($"[{job.DisplayId}] {SockseekLog.JobTypeName(job)}: skipped because prior index entry was {prev.State}/{prev.FailureReason}: {job}");
            return true;
        }
        return false;
    }

    public JobOutcome? TryGetNotFoundLastTimeOutcome(Job job)
    {
        var jobCtx = getContext(job);
        if (jobCtx.IndexEditor == null) return null;
        var prev = PreviousRunResult(job, jobCtx.IndexEditor);

        if (prev == null) return null;
        if (!IsNotFoundFailure(prev.FailureReason) && prev.State != JobStateOld.NotFoundLastTime)
            return null;

        SockseekLog.Jobs.Debug($"[{job.DisplayId}] {SockseekLog.JobTypeName(job)}: skipped because prior index entry was {prev.State}/{prev.FailureReason}: {job}");
        return JobOutcome.Skipped(JobSkipReason.NotFoundLastTime, NotFoundFailureReasonOrDefault(prev.FailureReason));
    }

    private string? FindExistingAlbumPath(Job job, AlbumJob album, JobContext ctx, TrackSkipperContext skipCtx)
    {
        string? path = null;

        if (ctx.OutputDirSkipper != null)
        {
            if (!ctx.OutputDirSkipper.IndexIsBuilt) ctx.OutputDirSkipper.BuildIndex();
            ctx.OutputDirSkipper.AlbumExists(album, skipCtx, out path);
        }

        if (path == null && ctx.MusicDirSkipper != null)
        {
            if (!ctx.MusicDirSkipper.IndexIsBuilt)
            {
                raiseBuildingMusicDirectoryIndex(job);
                ctx.MusicDirSkipper.BuildIndex();
            }
            ctx.MusicDirSkipper.AlbumExists(album, skipCtx, out path);
        }

        return path;
    }

    private static IndexEntry? PreviousRunResult(Job job, M3uEditor indexEditor)
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
