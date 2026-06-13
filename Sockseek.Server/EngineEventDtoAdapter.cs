using Soulseek;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Api;

namespace Sockseek.Server;

public sealed class EngineEventDtoAdapter
{
    private readonly Func<Job, JobSummaryDto> getSummary;
    private readonly Action<string, object> publish;

    public EngineEventDtoAdapter(Func<Job, JobSummaryDto> getSummary, Action<string, object> publish)
    {
        this.getSummary = getSummary;
        this.publish = publish;
    }

    public void Attach(EngineEvents events)
    {
        events.JobStatus += (job, status) => publish("job.status", new JobStatusEventDto(getSummary(job), status));
        events.JobMessage += (job, level, source, message) => publish("job.message", new JobMessageEventDto(getSummary(job), level.ToString(), source, message));
        events.JobActivityChanged += (job, _, _) => publish("job.activity-changed", new JobActivityChangedEventDto(getSummary(job)));
        events.JobStateChanged += job =>
        {
            if (job is SongJob song)
            {
                if (song.ActivityPhase == JobActivityPhase.Searching)
                    publish("song.searching", new SongSearchingEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
                else if (song.IsTerminal)
                    publish("song.state-changed", new SongStateChangedEventDto(
                        song.Id,
                        song.DisplayId,
                        song.WorkflowId,
                        ToSongQueryDto(song.Query),
                        EngineStateStore.ToServerJobLifecycleState(song.LifecycleState),
                        EngineStateStore.ToServerJobActivityPhase(song.ActivityPhase),
                        song.ActivityUntilUtc,
                        EngineStateStore.ToServerJobTerminalOutcome(song.TerminalOutcome),
                        EngineStateStore.ToServerJobSkipReason(song.SkipReason),
                        EngineStateStore.ToServerFailureReason(song.FailureReason),
                        song.DownloadPath,
                        song.ChosenCandidate != null ? ToFileCandidateDto(song.ChosenCandidate) : null,
                        song.Discovery?.ResultCount,
                        song.Discovery?.LockedFileCount,
                        song.FailureMessage,
                        EngineStateStore.ToServerJobCancellationSource(song.CancellationSource)));
            }
            else if (job is AlbumJob albumJob)
            {
                if (albumJob.ActivityPhase == JobActivityPhase.Searching)
                    publish("job.started", new JobStartedEventDto(getSummary(job)));
                else if (albumJob.ActivityPhase == JobActivityPhase.Downloading && albumJob.ResolvedTarget != null)
                {
                    var folder = albumJob.ResolvedTarget;
                    publish("album.download-started", new AlbumDownloadStartedEventDto(
                        getSummary(job),
                        ToAlbumFolderDto(folder, includeFiles: false),
                        folder.Files.Select(ToSongJobPayloadDto).ToList()));
                    publish("album.track-download-started", new AlbumTrackDownloadStartedEventDto(
                        getSummary(job),
                        ToAlbumFolderDto(folder, includeFiles: false),
                        folder.Files.Select(ToSongJobPayloadDto).ToList()));
                }
                else if (albumJob.IsTerminal)
                    publish("album.state-changed", new AlbumStateChangedEventDto(getSummary(job), albumJob.DownloadPath));
            }
            else if (job is ExtractJob extractJob)
            {
                if (extractJob.ActivityPhase == JobActivityPhase.Extracting)
                    publish("extraction.started", new ExtractionStartedEventDto(
                        getSummary(extractJob),
                        extractJob.Input,
                        extractJob.InputType?.ToString(),
                        ExtractionSource(extractJob)));
                else if (extractJob.IsUnsuccessfulTerminal)
                    publish("extraction.failed", new ExtractionFailedEventDto(
                        getSummary(extractJob),
                        extractJob.FailureMessage ?? "Extraction failed",
                        ExtractionSource(extractJob)));
            }
            else if (job is AggregateJob ag && ag.ActivityPhase == JobActivityPhase.RunningChildren)
            {
                publish("job.status", new JobStatusEventDto(getSummary(job), "running"));
                var pending   = ag.Songs.Where(s => s.IsPending).ToList();
                var existing  = ag.Songs.Where(s => s.TerminalOutcome == JobTerminalOutcome.Skipped && s.SkipReason == JobSkipReason.AlreadyExists).ToList();
                var notFound  = ag.Songs.Where(s => s.FailureReason == JobFailureReason.NoSuitableFileFound).ToList();
                publish("track-batch.resolved", new TrackBatchResolvedEventDto(
                    getSummary(job),
                    false,
                    job.Config.PrintOption,
                    pending.Count,
                    existing.Count,
                    notFound.Count,
                    [.. SelectTrackBatchRows(pending,  job.Config.PrintOption, limit: 20)],
                    [.. SelectTrackBatchRows(existing, job.Config.PrintOption, limit: 20)],
                    [.. SelectTrackBatchRows(notFound, job.Config.PrintOption, limit: 20)]));
            }
            else if (job is AggregateJob && job.TerminalOutcome == JobTerminalOutcome.Succeeded)
            {
                publish("job.status", new JobStatusEventDto(getSummary(job), "done"));
            }
            else
            {
                if (job.ActivityPhase == JobActivityPhase.Searching)
                    publish("job.started", new JobStartedEventDto(getSummary(job)));
            }

            PublishDiagnosticErrorIfNeeded(job);
        };
        events.DownloadStarted += (song, candidate) => publish("download.started", new DownloadStartedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query), ToFileCandidateDto(candidate)));
        events.DownloadProgress += (song, transferred, total) => publish("download.progress", new DownloadProgressEventDto(song.Id, song.WorkflowId, transferred, total));
        events.DownloadStateChanged += (song, state) => publish("download.state-changed", new DownloadStateChangedEventDto(song.Id, song.WorkflowId, state.ToString()));
        events.DownloadAttemptFailed += (song, candidate, outputPath, attempt, maxAttempts, ex) => publish("download.attempt-failed", new DownloadAttemptFailedEventDto(
            song.Id,
            song.DisplayId,
            song.WorkflowId,
            ToSongQueryDto(song.Query),
            ToFileCandidateDto(candidate),
            outputPath,
            attempt,
            maxAttempts,
            ex.GetType().Name,
            SockseekLog.ExceptionSummary(ex),
            SockseekLog.ExceptionDetail(ex)));
        events.OnCompleteStart += song => publish("on-complete.started", new OnCompleteStartedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        events.OnCompleteEnd += song => publish("on-complete.ended", new OnCompleteEndedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        events.SearchRateLimited += resetsAt => publish("search.rate-limited", new SearchRateLimitedEventDto(resetsAt));
        events.SearchResumed += () => publish("search.resumed", new SearchResumedEventDto());
        events.TrackBatchResolved += (job, pending, existing, notFound) => publish("track-batch.resolved", new TrackBatchResolvedEventDto(
            getSummary(job),
            job is JobList,
            job.Config.PrintOption,
            pending.Count,
            existing.Count,
            notFound.Count,
            [.. SelectTrackBatchRows(pending,  job.Config.PrintOption, limit: 20)],
            [.. SelectTrackBatchRows(existing, job.Config.PrintOption, limit: 20)],
            [.. SelectTrackBatchRows(notFound, job.Config.PrintOption, limit: 20)]));
    }

    private void PublishDiagnosticErrorIfNeeded(Job job)
    {
        if (job.TerminalOutcome != JobTerminalOutcome.Failed || string.IsNullOrWhiteSpace(job.FailureDetail))
            return;

        var summary = getSummary(job);
        publish("diagnostic.error", new DiagnosticErrorEventDto(
            "job",
            job.FailureMessage ?? "Job failed",
            ExceptionType(job.FailureDetail),
            job.FailureDetail,
            summary,
            job.WorkflowId,
            job is ExtractJob extractJob ? ExtractionSource(extractJob) : null));
    }

    private static string? ExtractionSource(ExtractJob job)
        => job.InputType?.ToString();

    private static string ExceptionType(string exceptionDetail)
    {
        var firstLine = exceptionDetail
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', 2)[0];
        var separatorIndex = firstLine.IndexOf(':');
        return separatorIndex > 0 ? firstLine[..separatorIndex] : firstLine;
    }

    private static IEnumerable<SongJobPayloadDto> SelectTrackBatchRows(
        IReadOnlyList<SongJob> songs, PrintOption printOption, int limit = int.MaxValue)
    {
        bool needsFullRows = printOption.HasFlag(PrintOption.Tracks)
            || (printOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
        int effectiveLimit = needsFullRows ? int.MaxValue : limit;
        return songs.Take(effectiveLimit).Select(ToSongJobPayloadDto);
    }

    public static SongQueryDto ToSongQueryDto(SongQuery query)
        => new(Optional(query.Artist), Optional(query.Title), Optional(query.Album), Optional(query.URI), Optional(query.Length), query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    public static FileCandidateDto ToFileCandidateDto(FileCandidate candidate)
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

    public static SongJobPayloadDto ToSongJobPayloadDto(SongJob song)
        => new(
            ToSongQueryDto(song.Query),
            song.Candidates?.Count,
            song.DownloadPath,
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.Response.HasFreeUploadSlot,
            song.ResolvedTarget?.Response.UploadSpeed,
            song.ResolvedTarget?.File.Size,
            song.ResolvedTarget?.File.SampleRate,
            song.ResolvedTarget?.File.Extension,
            song.ResolvedTarget?.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList(),
            song.Id,
            song.DisplayId,
            null,
            EngineStateStore.ToServerJobLifecycleState(song.LifecycleState),
            EngineStateStore.ToServerJobActivityPhase(song.ActivityPhase),
            song.ActivityUntilUtc,
            EngineStateStore.ToServerJobTerminalOutcome(song.TerminalOutcome),
            EngineStateStore.ToServerJobSkipReason(song.SkipReason),
            EngineStateStore.ToServerFailureReason(song.FailureReason),
            song.FailureMessage,
            CancellationSource: EngineStateStore.ToServerJobCancellationSource(song.CancellationSource));

    public static AlbumFolderDto ToAlbumFolderDto(AlbumFolder folder, bool includeFiles)
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
            includeFiles
                ? folder.Files
                    .Where(song => song.ResolvedTarget != null)
                    .Select(song => ToFileCandidateDto(song.ResolvedTarget!))
                    .ToList()
                : null,
            folder.IsFullyRetrieved);
}
