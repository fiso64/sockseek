using System.Text.Json;
using System.Text.Json.Serialization;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Api;
using Sockseek.Server;

namespace Sockseek.Cli;

/// <summary>
/// Writes NDJSON (newline-delimited JSON) progress events to a TextWriter (typically stdout).
/// Each line is a JSON object with { type, timestamp, data }.
/// </summary>
public class JsonStreamProgressReporter
{
    private readonly TextWriter _writer;
    private readonly Lock _lock = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly HashSet<Guid> _seenTransfers = [];
    private DateTime _lastDownloadProgressReport = DateTime.MinValue;
    private readonly TimeSpan _downloadProgressThrottle = TimeSpan.FromMilliseconds(500);

    public JsonStreamProgressReporter(TextWriter writer)
    {
        _writer = writer;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
    }

    public void Attach(DownloadEvents events)
    {
        events.TrackListReady     += change => ReportTrackList(change.Songs);
        events.JobStateChanged    += change =>
        {
            if (change.Job.Payload is SongJobSnapshotPayload song)
            {
                if (change.ActivityPhase == JobActivityPhase.Searching)
                    ReportSearchStart(song.Query);
                else if (change.IsTerminal)
                    ReportStateChanged(change.Job, song);
            }
        };
        events.DownloadStarted    += ReportDownloadStart;
        events.DownloadProgress   += ReportDownloadProgress;
        events.OverallProgress    += change => ReportOverallProgress(change.Done, change.Failed, change.Total);
        events.ListProgress       += ReportListProgress;
        events.JobStateChanged    += change =>
        {
            if (change.Job.Payload is ExtractJobSnapshotPayload extract && change.IsUnsuccessfulTerminal)
                ReportExtractionFailed(extract, change.FailureMessage ?? "Extraction failed");
        };
    }

    internal void Attach(ICliBackend backend)
    {
        backend.StateUpdated += update =>
        {
            if (update.Status != DaemonClientApplyStatus.Applied)
                return;

            foreach (var job in update.ChangedJobs.Where(job => job.Kind == ServerJobKind.Song))
            {
                if (job.ActivityPhase == ServerJobActivityPhase.Searching)
                    ReportSearchStart(job);
                if (job.LifecycleState == ServerJobLifecycleState.Terminal)
                    ReportStateChanged(job);
            }

            foreach (var transfer in update.ChangedTransfers)
            {
                bool first;
                lock (_lock)
                    first = _seenTransfers.Add(transfer.TransferId);
                if (first && transfer.Identity.JobId is { } jobId)
                    ReportDownloadStart(backend.ClientStore.GetJob(jobId), transfer);
                ReportDownloadProgress(transfer);
            }
        };

        backend.ActivityReceived += activity =>
        {
            if (activity.Payload is TrackBatchResolvedActivityDto batch)
                ReportTrackBatchResolved(batch);
        };
    }

    private void ReportSearchStart(JobSummaryDto song)
    {
        WriteEvent("search_start", new
        {
            jobId = song.JobId,
            query = song.QueryText ?? song.ItemName,
        });
    }

    private void ReportDownloadStart(JobSummaryDto? song, TransferStateDto transfer)
    {
        WriteEvent("download_start", new
        {
            jobId = transfer.Identity.JobId,
            query = song?.QueryText ?? song?.ItemName,
            username = transfer.Identity.Username,
            filename = transfer.Identity.RemotePath,
            size = transfer.Progress.TotalBytes,
            extension = GetExtension(transfer.Identity.RemotePath ?? ""),
            transferId = transfer.TransferId,
        });
    }

    private void ReportDownloadProgress(TransferStateDto transfer)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDownloadProgressReport < _downloadProgressThrottle)
            return;
        _lastDownloadProgressReport = now;

        WriteEvent("download_progress", new
        {
            jobId = transfer.Identity.JobId,
            transferId = transfer.TransferId,
            bytesTransferred = transfer.Progress.BytesTransferred,
            totalBytes = transfer.Progress.TotalBytes,
            percent = transfer.Progress.TotalBytes > 0
                ? Math.Round((double)transfer.Progress.BytesTransferred / transfer.Progress.TotalBytes * 100, 1)
                : 0,
        });
    }

    private void ReportStateChanged(JobSummaryDto song)
    {
        WriteEvent("track_state", new
        {
            jobId = song.JobId,
            query = song.QueryText ?? song.ItemName,
            lifecycleState = song.LifecycleState,
            activityPhase = song.ActivityPhase,
            terminalOutcome = song.TerminalOutcome,
            skipReason = song.SkipReason,
            failureReason = song.FailureReason,
            rawResultCount = song.DiscoveryRawResultCount,
            lockedCount = song.DiscoveryLockedFileCount,
        });
    }

    private void ReportTrackBatchResolved(TrackBatchResolvedActivityDto batch)
    {
        WriteEvent("track_list", new
        {
            total = batch.PendingCount + batch.ExistingCount + batch.NotFoundCount,
            pending = batch.PendingCount,
            existing = batch.ExistingCount,
            notFound = batch.NotFoundCount,
        });
    }

    private void ReportTrackList(IEnumerable<SongJob> songs)
    {
        var list = songs.ToList();
        var data = new
        {
            total = list.Count,
            tracks = list.Select((s, i) => new
            {
                index  = i,
                artist = s.Query.Artist,
                title  = s.Query.Title,
                album  = s.Query.Album,
                length = s.Query.Length,
                lifecycleState  = s.LifecycleState.ToString(),
                activityPhase   = s.ActivityPhase.ToString(),
                terminalOutcome = s.TerminalOutcome.ToString(),
            }).ToList(),
        };
        WriteEvent("track_list", data);
    }

    private void ReportTrackList(IEnumerable<JobSnapshot> songs)
    {
        var list = songs
            .Where(song => song.Payload is SongJobSnapshotPayload)
            .ToList();
        var data = new
        {
            total = list.Count,
            tracks = list.Select((job, i) =>
            {
                var song = (SongJobSnapshotPayload)job.Payload;
                return new
                {
                    index = i,
                    artist = song.Query.Artist,
                    title = song.Query.Title,
                    album = song.Query.Album,
                    length = song.Query.Length,
                    lifecycleState = job.LifecycleState.ToString(),
                    activityPhase = job.ActivityPhase.ToString(),
                    terminalOutcome = job.TerminalOutcome.ToString(),
                };
            }).ToList(),
        };
        WriteEvent("track_list", data);
    }

    private void ReportSearchStart(SongJob song)
    {
        WriteEvent("search_start", new
        {
            artist = song.Query.Artist,
            title  = song.Query.Title,
            album  = song.Query.Album,
        });
    }

    private void ReportSearchStart(SongQuerySnapshot song)
    {
        WriteEvent("search_start", new
        {
            artist = song.Artist,
            title = song.Title,
            album = song.Album,
        });
    }

    private void ReportDownloadStart(SongJob song, FileCandidate candidate)
    {
        WriteEvent("download_start", new
        {
            artist    = song.Query.Artist,
            title     = song.Query.Title,
            username  = candidate.Username,
            filename  = candidate.Filename,
            size      = candidate.Size,
            extension = GetExtension(candidate.Filename),
        });
    }

    private void ReportDownloadStart(DownloadStartedChange change)
    {
        var song = (SongJobSnapshotPayload)change.Job.Payload;
        WriteEvent("download_start", new
        {
            artist = song.Query.Artist,
            title = song.Query.Title,
            username = change.Target.Identity.Username,
            filename = change.Target.Identity.Filename,
            size = change.Target.Size,
            extension = GetExtension(change.Target.Identity.Filename),
        });
    }

    private void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDownloadProgressReport < _downloadProgressThrottle)
            return;
        _lastDownloadProgressReport = now;

        WriteEvent("download_progress", new
        {
            artist           = song.Query.Artist,
            title            = song.Query.Title,
            bytesTransferred,
            totalBytes,
            percent = totalBytes > 0 ? Math.Round((double)bytesTransferred / totalBytes * 100, 1) : 0,
        });
    }

    private void ReportDownloadProgress(DownloadProgressedChange progress)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDownloadProgressReport < _downloadProgressThrottle)
            return;
        _lastDownloadProgressReport = now;

        WriteEvent("download_progress", new
        {
            jobId = progress.Job.Id,
            bytesTransferred = progress.BytesTransferred,
            totalBytes = progress.TotalBytes,
            percent = progress.TotalBytes > 0 ? Math.Round((double)progress.BytesTransferred / progress.TotalBytes * 100, 1) : 0,
        });
    }

    private void ReportStateChanged(SongJob song)
    {
        var chosen = song.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.AlreadyExists)
                ? song.ResolvedTarget
                : null;
        WriteEvent("track_state", new
        {
            artist          = song.Query.Artist,
            title           = song.Query.Title,
            lifecycleState  = song.LifecycleState.ToString(),
            activityPhase   = song.ActivityPhase.ToString(),
            terminalOutcome = song.TerminalOutcome.ToString(),
            skipReason      = song.SkipReason != JobSkipReason.None ? song.SkipReason.ToString() : null,
            failureReason   = song.FailureReason != JobFailureReason.None ? song.FailureReason.ToString() : null,
            downloadPath    = !string.IsNullOrEmpty(song.DownloadPath) ? song.DownloadPath : null,
            username        = chosen?.Username,
            filename        = chosen?.Filename,
            size            = chosen?.Size,
            bitRate         = chosen?.BitRate,
            extension       = chosen != null ? GetExtension(chosen.Filename) : null,
            rawResultCount  = song.Discovery?.RawResultCount,
            lockedCount     = song.Discovery?.LockedFileCount,
        });
    }

    private void ReportStateChanged(JobSnapshotHeader job, SongJobSnapshotPayload song)
    {
        var chosen = job.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason == JobSkipReason.AlreadyExists)
                ? song.ResolvedTarget
                : null;
        WriteEvent("track_state", new
        {
            artist = song.Query.Artist,
            title = song.Query.Title,
            lifecycleState = job.LifecycleState.ToString(),
            activityPhase = job.ActivityPhase.ToString(),
            terminalOutcome = job.TerminalOutcome.ToString(),
            skipReason = job.SkipReason != JobSkipReason.None ? job.SkipReason.ToString() : null,
            failureReason = job.FailureReason != JobFailureReason.None ? job.FailureReason.ToString() : null,
            downloadPath = !string.IsNullOrEmpty(song.File.DownloadPath) ? song.File.DownloadPath : null,
            username = chosen?.Username ?? song.ExactTarget?.Identity.Username,
            filename = chosen?.Filename ?? song.ExactTarget?.Identity.Filename,
            size = chosen?.Size ?? song.ExactTarget?.Size,
            bitRate = chosen?.BitRate ?? song.ExactTarget?.BitRate,
            extension = chosen != null
                ? GetExtension(chosen.Filename)
                : song.ExactTarget?.Extension,
            rawResultCount = job.Discovery?.RawResultCount,
            lockedCount = job.Discovery?.LockedFileCount,
        });
    }

    private void ReportOverallProgress(int downloaded, int failed, int total)
    {
        WriteEvent("progress", new
        {
            downloaded,
            failed,
            total,
            percent = total > 0 ? Math.Round((double)(downloaded + failed) / total * 100, 1) : 0,
        });
    }

    private void ReportListProgress(JobList list, int downloaded, int failed, int total)
    {
        WriteEvent("list_progress", new { name = list.ItemName, downloaded, failed, total });
    }

    private void ReportListProgress(ListProgressChange progress)
    {
        WriteEvent("list_progress", new { name = progress.List.ItemName, downloaded = progress.Done, failed = progress.Failed, total = progress.Total });
    }

    private void ReportExtractionFailed(ExtractJob job, string reason)
    {
        WriteEvent("extraction_failed", new
        {
            input  = job.Input,
            reason,
        });
    }

    private void ReportExtractionFailed(ExtractJobSnapshotPayload job, string reason)
    {
        WriteEvent("extraction_failed", new
        {
            input = job.Input,
            reason,
        });
    }

    private void WriteEvent(string type, object data)
    {
        var envelope = new
        {
            type,
            timestamp = DateTime.UtcNow.ToString("O"),
            data,
        };

        var json = JsonSerializer.Serialize(envelope, _jsonOptions);

        lock (_lock)
        {
            _writer.WriteLine(json);
            _writer.Flush();
        }
    }

    private static string? GetExtension(string filename)
    {
        var ext = Path.GetExtension(filename);
        return string.IsNullOrEmpty(ext) ? null : ext.TrimStart('.').ToLower();
    }
}
