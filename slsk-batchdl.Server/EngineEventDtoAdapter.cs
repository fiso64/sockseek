using Soulseek;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Server;

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
        events.JobStateChanged += (job, state) =>
        {
            if (job is SongJob song)
            {
                if (state == JobState.Searching)
                    publish("song.searching", new SongSearchingEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
                else if (state is JobState.Done or JobState.Failed or JobState.AlreadyExists or JobState.Skipped or JobState.NotFoundLastTime)
                    publish("song.state-changed", new SongStateChangedEventDto(
                        song.Id,
                        song.DisplayId,
                        song.WorkflowId,
                        ToSongQueryDto(song.Query),
                        EngineStateStore.ToServerJobState(song.State),
                        EngineStateStore.ToServerFailureReason(song.FailureReason),
                        song.DownloadPath,
                        song.ChosenCandidate != null ? ToFileCandidateDto(song.ChosenCandidate) : null,
                        song.Discovery?.ResultCount,
                        song.Discovery?.LockedFileCount));
            }
            else if (job is AlbumJob albumJob)
            {
                if (state == JobState.Searching)
                    publish("job.started", new JobStartedEventDto(getSummary(job)));
                else if (state == JobState.Downloading && albumJob.ResolvedTarget != null)
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
                else if (state == JobState.Done)
                    publish("album.download-completed", new AlbumDownloadCompletedEventDto(getSummary(job), albumJob.DownloadPath));
            }
            else if (job is ExtractJob extractJob)
            {
                if (state == JobState.Extracting)
                    publish("extraction.started", new ExtractionStartedEventDto(getSummary(extractJob), extractJob.Input, extractJob.InputType?.ToString()));
                else if (state == JobState.Failed)
                    publish("extraction.failed", new ExtractionFailedEventDto(getSummary(extractJob), extractJob.FailureMessage ?? "Extraction failed"));
            }
            else if (job is AggregateJob ag && state == JobState.Running)
            {
                publish("job.status", new JobStatusEventDto(getSummary(job), "running"));
                var pending   = ag.Songs.Where(s => s.State == JobState.Pending).ToList();
                var existing  = ag.Songs.Where(s => s.State == JobState.AlreadyExists).ToList();
                var notFound  = ag.Songs.Where(s => s.FailureReason == FailureReason.NoSuitableFileFound).ToList();
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
            else if (job is AggregateJob && state == JobState.Done)
            {
                publish("job.status", new JobStatusEventDto(getSummary(job), "done"));
            }
            else
            {
                if (state == JobState.Searching)
                    publish("job.started", new JobStartedEventDto(getSummary(job)));
            }
        };
        events.DownloadStarted += (song, candidate) => publish("download.started", new DownloadStartedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query), ToFileCandidateDto(candidate)));
        events.DownloadProgress += (song, transferred, total) => publish("download.progress", new DownloadProgressEventDto(song.Id, song.WorkflowId, transferred, total));
        events.DownloadStateChanged += (song, state) => publish("download.state-changed", new DownloadStateChangedEventDto(song.Id, song.WorkflowId, state.ToString()));
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
            EngineStateStore.ToServerJobState(song.State),
            EngineStateStore.ToServerFailureReason(song.FailureReason),
            song.FailureMessage);

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
