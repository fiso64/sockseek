using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Transfers.Downloads.Skipping;

internal sealed class SkipEvaluationCoordinator
{
    internal sealed record Match(string Path, ExistingFileSource Source, List<string>? PlaylistTracks = null);
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

    public async ValueTask<bool> TrySetAlreadyExistsAsync(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        if (!song.CanBeSkipped || song.Cts?.IsCancellationRequested == true) return false;
        var ctx = getContext(job);
        if (OnCompleteExecutor.HasUnfinishedExport(job, song, ctx)) return false;
        Match? match;
        try { match = await FindExistingAsync(job, ctx, song, skipCtx); }
        catch (OperationCanceledException) when (job.Cts?.IsCancellationRequested == true) { return false; }
        if (song.Cts?.IsCancellationRequested == true) return false;

        if (match != null)
        {
            JobOutcomeCommitter.Commit(song, JobOutcome.AlreadyExists(match.Path, match.Source));
            AlreadyExists(song);
        }

        return match != null;
    }

    public async ValueTask<JobOutcome?> TryGetJobAlreadyExistsOutcomeAsync(Job job, JobContext ctx)
    {
        // A name or tag match in a separate library must not become the input
        // to a converter that may move or delete files. Use the transfer's own
        // destination when resuming work on an unfinished export.
        if (!job.CanBeSkipped || OnCompleteExecutor.HasUnfinishedExport(job, job as SongJob, ctx))
            return null;
        var skipCtx = TrackSkipperContext.From(ctx, job.Config.Skip, job.Config.ResolveSearchSettings());
        if (job is not (SongJob or AlbumJob) || job is AlbumJob && job.Config.Output.AlbumArtOnly) return null;
        var match = await FindExistingAsync(job, ctx, job, skipCtx);
        if (match == null)
            return null;

        job.Cts!.Token.ThrowIfCancellationRequested();
        if (job is AlbumJob album) album.ExistingPlaylistTracks = match.PlaylistTracks;
        AlreadyExists(job);
        return JobOutcome.AlreadyExists(match.Path, match.Source);
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
            "skipped existing output");

    private void NotFoundPreviously(Job job)
        => DownloadLogMessages.JobDecision(
            logger,
            job.Id,
            "skipped because a previous search found no results");

    private ValueTask<Match?> FindExistingAsync(Job owner, JobContext ctx, Job candidate, TrackSkipperContext skipCtx)
        => FindExistingAsync(ctx, candidate, skipCtx, owner.Cts!.Token, () => raiseBuildingMusicDirectoryIndex(owner));

    internal static async ValueTask<Match?> FindExistingAsync(JobContext ctx, Job candidate,
        TrackSkipperContext skipCtx, CancellationToken token, Action? buildingLibrary = null)
    {
        skipCtx.CancellationToken = token;
        if (ctx.OutputDirSkipper is { } output)
        {
            if (!output.IndexIsBuilt) await output.EnsureIndexAsync(token);
            if (await LookupAsync(output) is { } match) return match;
        }
        if (ctx.MusicDirSkipper is not { } library) return null;
        if (!library.IndexIsBuilt)
            await library.EnsureIndexAsync(token, buildingLibrary);
        return await LookupAsync(library);

        async ValueTask<Match?> LookupAsync(TrackSkipper skipper)
        {
            if (skipper is IndexSkipper)
            {
                // A miss needs no disk access. Keep it synchronous so fresh songs
                // enter the download queue in the order the user selected them.
                var path = IndexSkipper.CompletedPath(candidate, skipCtx);
                if (path == null) return null;
                if (!skipCtx.checkFileExists && (candidate is SongJob || ctx.PlaylistEditor == null))
                    return new(path, ExistingFileSource.CompletionHistory);
            }
            return await Task.Run(() => MatchIn(skipper), token).WaitAsync(token).ConfigureAwait(false);
        }

        Match? MatchIn(TrackSkipper skipper)
        {
            token.ThrowIfCancellationRequested();
            string? path;
            if (candidate is SongJob song)
            {
                if (!skipper.SongExists(song, skipCtx, out path) || path == null) return null;
            }
            else
            {
                var album = (AlbumJob)candidate;
                List<string>? tracks = ctx.PlaylistEditor is null ? null : [];
                if (!skipper.AlbumExists(album, skipCtx, out path, tracks) || path == null) return null;
                return new(path, SourceOf(skipper), tracks);
            }
            return new(path, SourceOf(skipper));
        }
    }

    private static ExistingFileSource SourceOf(TrackSkipper skipper)
        => skipper is IndexSkipper
            ? ExistingFileSource.CompletionHistory : ExistingFileSource.Library;

    private static IndexEntry? PreviousRunResult(Job job, DownloadOutputFile indexEditor)
        => job switch
        {
            SongJob song => indexEditor.PreviousRunResult(song),
            AlbumJob album when !album.Config.Output.AlbumArtOnly => indexEditor.PreviousRunResult(album),
            _ => null,
        };

    private static bool IsNotFoundFailure(JobFailureReason reason)
        => reason is JobFailureReason.NoSearchResults or JobFailureReason.NoMatchingResults;

    private static JobFailureReason NotFoundFailureReasonOrDefault(JobFailureReason reason)
        => IsNotFoundFailure(reason) ? reason : JobFailureReason.NoMatchingResults;
}
