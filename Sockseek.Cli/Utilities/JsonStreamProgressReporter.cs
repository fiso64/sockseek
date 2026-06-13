using System.Text.Json;
using System.Text.Json.Serialization;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
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

    public void Attach(EngineEvents events)
    {
        events.TrackListReady     += songs => ReportTrackList(songs);
        events.JobStateChanged    += job =>
        {
            if (job is SongJob song)
            {
                if (song.ActivityPhase == JobActivityPhase.Searching)
                    ReportSearchStart(song);
                else if (song.IsTerminal)
                    ReportStateChanged(song);
            }
        };
        events.DownloadStarted    += ReportDownloadStart;
        events.DownloadProgress   += ReportDownloadProgress;
        events.OverallProgress    += ReportOverallProgress;
        events.ListProgress       += ReportListProgress;
        events.JobStateChanged    += job =>
        {
            if (job is ExtractJob ej && ej.IsUnsuccessfulTerminal)
                ReportExtractionFailed(ej, ej.FailureMessage ?? "Extraction failed");
        };
    }

    internal void Attach(ICliBackend backend)
    {
        backend.EventReceived += envelope =>
        {
            switch (envelope.Type)
            {
                case "song.searching" when envelope.Payload is SongSearchingEventDto e:
                    ReportSearchStart(e);
                    break;

                case "download.started" when envelope.Payload is DownloadStartedEventDto e:
                    ReportDownloadStart(e);
                    break;

                case "download.progress" when envelope.Payload is DownloadProgressEventDto e:
                    ReportDownloadProgress(e);
                    break;

                case "song.state-changed" when envelope.Payload is SongStateChangedEventDto e:
                    ReportStateChanged(e);
                    break;

                case "track-batch.resolved" when envelope.Payload is TrackBatchResolvedEventDto e:
                    ReportTrackBatchResolved(e);
                    break;
            }
        };
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

    private void ReportSearchStart(SongJob song)
    {
        WriteEvent("search_start", new
        {
            artist = song.Query.Artist,
            title  = song.Query.Title,
            album  = song.Query.Album,
        });
    }

    private void ReportSearchStart(SongSearchingEventDto song)
    {
        WriteEvent("search_start", new
        {
            artist = song.Query.Artist,
            title  = song.Query.Title,
            album  = song.Query.Album,
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
            size      = candidate.File.Size,
            extension = GetExtension(candidate.Filename),
        });
    }

    private void ReportDownloadStart(DownloadStartedEventDto song)
    {
        WriteEvent("download_start", new
        {
            artist = song.Query.Artist,
            title = song.Query.Title,
            username = song.Candidate.Username,
            filename = song.Candidate.Filename,
            size = song.Candidate.Size,
            extension = GetExtension(song.Candidate.Filename),
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

    private void ReportDownloadProgress(DownloadProgressEventDto progress)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDownloadProgressReport < _downloadProgressThrottle)
            return;
        _lastDownloadProgressReport = now;

        WriteEvent("download_progress", new
        {
            jobId = progress.JobId,
            bytesTransferred = progress.BytesTransferred,
            totalBytes = progress.TotalBytes,
            percent = progress.TotalBytes > 0 ? Math.Round((double)progress.BytesTransferred / progress.TotalBytes * 100, 1) : 0,
        });
    }

    private void ReportStateChanged(SongJob song)
    {
        var chosen = song.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.AlreadyExists)
                ? song.ChosenCandidate
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
            size            = chosen?.File.Size,
            bitRate         = chosen?.File.BitRate,
            extension       = chosen != null ? GetExtension(chosen.Filename) : null,
            resultCount     = song.Discovery?.ResultCount,
            lockedCount     = song.Discovery?.LockedFileCount,
        });
    }

    private void ReportStateChanged(SongStateChangedEventDto song)
    {
        WriteEvent("track_state", new
        {
            artist = song.Query.Artist,
            title = song.Query.Title,
            lifecycleState = song.LifecycleState,
            activityPhase = song.ActivityPhase,
            terminalOutcome = song.TerminalOutcome,
            skipReason = song.SkipReason,
            failureReason = song.FailureReason,
            downloadPath = song.DownloadPath,
            username = song.ChosenCandidate?.Username,
            filename = song.ChosenCandidate?.Filename,
            size = song.ChosenCandidate?.Size,
            bitRate = song.ChosenCandidate?.BitRate,
            extension = song.ChosenCandidate != null ? GetExtension(song.ChosenCandidate.Filename) : null,
            resultCount = song.DiscoveryResultCount,
            lockedCount = song.DiscoveryLockedFileCount,
        });
    }

    private void ReportTrackBatchResolved(TrackBatchResolvedEventDto batch)
    {
        var pending = batch.Pending.ToList();
        var existing = batch.Existing.ToList();
        var notFound = batch.NotFound.ToList();
        var tracks = pending.Concat(existing).Concat(notFound).ToList();

        var data = new
        {
            total = batch.PendingCount + batch.ExistingCount + batch.NotFoundCount,
            pending = batch.PendingCount,
            existing = batch.ExistingCount,
            notFound = batch.NotFoundCount,
            tracks = tracks.Select((s, i) => new
            {
                index = i,
                artist = s.Query.Artist,
                title = s.Query.Title,
                album = s.Query.Album,
                length = s.Query.Length,
                lifecycleState = s.LifecycleState,
                activityPhase = s.ActivityPhase,
                terminalOutcome = pending.Contains(s)
                    ? ServerJobTerminalOutcome.None
                    : existing.Contains(s)
                        ? ServerJobTerminalOutcome.Skipped
                        : ServerJobTerminalOutcome.Skipped,
                skipReason = pending.Contains(s)
                    ? ServerJobSkipReason.None
                    : existing.Contains(s)
                        ? ServerJobSkipReason.AlreadyExists
                        : ServerJobSkipReason.NotFoundLastTime,
            }).ToList(),
        };
        WriteEvent("track_list", data);
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

    private void ReportExtractionFailed(ExtractJob job, string reason)
    {
        WriteEvent("extraction_failed", new
        {
            input  = job.Input,
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
