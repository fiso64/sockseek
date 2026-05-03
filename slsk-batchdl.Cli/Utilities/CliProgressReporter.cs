using System.Collections.Concurrent;
using Soulseek;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Konsole;
using ProgressBar = Sldl.Cli.IProgressBar;
using Sldl.Core.Settings;
using Sldl.Server;

namespace Sldl.Cli;

public class CliProgressReporter
{
    private readonly CliSettings _cli;

    private readonly ConcurrentDictionary<SongJob, BarData> _bars = new();
    private readonly ConcurrentDictionary<Job, ProgressBar?> _jobBars = new();
    private readonly ConcurrentDictionary<AlbumJob, AlbumBlock> _albumBlocks = new();
    private readonly ConcurrentDictionary<Job, string> _jobStatuses = new();
    private readonly ConcurrentDictionary<Job, int> _jobSpinIndexes = new();
    private readonly ConcurrentDictionary<SongJob, (string text, int pos)> _savedState = new();
    private readonly ConcurrentDictionary<Job, string> _plainJobStatusLines = new();
    private readonly ConcurrentDictionary<Guid, BarData> _backendBars = new();
    private readonly ConcurrentDictionary<Guid, ProgressBar?> _backendJobBars = new();
    private readonly ConcurrentDictionary<Guid, BackendAlbumBlock> _backendAlbumBlocks = new();
    private readonly ConcurrentDictionary<Guid, string> _backendJobStatuses = new();
    private readonly ConcurrentDictionary<Guid, (string text, int pos)> _backendSavedState = new();
    private readonly ConcurrentDictionary<Guid, string> _backendPlainJobStatusLines = new();
    private readonly ConcurrentDictionary<Guid, ServerJobKind> _backendJobKinds = new();
    private readonly ConcurrentDictionary<Guid, byte> _backendInlineChildJobs = new();
    private readonly ConcurrentDictionary<SongJob, AlbumJob> _songToAlbum = new();
    private readonly ConcurrentDictionary<Guid, Guid> _backendSongToAlbum = new();

    static readonly char[] SpinFrames = { '|', '/', '—', '\\' };

    private bool PlainMode => _cli.NoProgress;

    sealed class BarData
    {
        public ProgressBar? Bar;
        public string       BaseText   = "";
        public string       StateLabel = "";
        public string?      JobPrefix;
        public int          SpinIndex  = 0;
        public int          Pct        = 0;
    }

    sealed class AlbumBlock
    {
        public List<SongJob> Songs = new();
        public SongJob?      LastUpdatedSong;
        public ProgressBar?  CompactBar;
        public int           LastDone = -1;
        public int           LastTotal = -1;
        public string?       LastStatus;
    }

    sealed class BackendAlbumBlock
    {
        public JobSummaryDto Summary = default!;
        public List<SongJobPayloadDto> Songs = new();
        public ConcurrentDictionary<Guid, byte> CompletedSongIds = new();
        public Guid?         LastUpdatedSongId;
        public ProgressBar?  CompactBar;
        public int           LastDone = -1;
        public int           LastTotal = -1;
        public string?       LastStatus;
        public int           LastCompactSpin = -1;
    }

    private readonly CancellationTokenSource _tickCts = new();

    public bool IsPaused { get; set; } = false;

    public CliProgressReporter(CliSettings cli)
    {
        _cli = cli;
        _ = TickLoopAsync(_tickCts.Token);
    }

    public void Stop() => _tickCts.Cancel();

    public void Attach(EngineEvents events)
    {
        events.DownloadStarted        += ReportDownloadStart;
        events.JobStatus              += ReportJobStatus;
        events.DownloadStateChanged   += (song, state) => ReportDownloadStateChanged(song, GetStateLabel(state));
        events.OnCompleteStart        += ReportOnCompleteStart;
        events.OnCompleteEnd          += ReportOnCompleteEnd;
        events.JobStatus              += ReportJobStatus;
        events.JobStateChanged        += OnJobStateChanged;
    }

    private void OnJobStateChanged(Job job, JobState state)
    {
        if (job is SongJob song)
        {
            if (state == JobState.Searching)
                ReportSongSearching(song);
            else if (state is JobState.Done or JobState.Failed or JobState.AlreadyExists or JobState.Skipped or JobState.NotFoundLastTime)
                ReportStateChanged(song);
        }
        else if (job is AlbumJob albumJob)
        {
            if (state == JobState.Searching)
                ReportJobSearching(albumJob);
            else if (state == JobState.Downloading && albumJob.ResolvedTarget != null)
                ReportAlbumTrackDownloadStarted(albumJob, albumJob.ResolvedTarget);
            else if (state == JobState.Done)
                ReportAlbumDownloadCompleted(albumJob);
            else if (state == JobState.Failed)
                ReportJobStatus(albumJob, TerminalStatusLabel(state, albumJob.FailureReason));
        }
        else if (job is ExtractJob extractJob)
        {
            if (state == JobState.Extracting)
                ReportExtractionStarted(extractJob);
            else if (state == JobState.Done && extractJob.Result != null)
                ReportExtractionCompleted(extractJob, extractJob.Result);
            else if (state == JobState.Failed)
            {
                if (extractJob.FailureMessage != null)
                    ReportExtractionFailed(extractJob, extractJob.FailureMessage);
                ReportJobStatus(extractJob, TerminalStatusLabel(state, extractJob.FailureReason, "extraction failed"));
            }
        }
        else
        {
            if (state == JobState.Searching)
                ReportJobSearching(job);
            else if (state == JobState.Downloading)
                ReportJobDownloading(job);
            else if (state is JobState.Done or JobState.AlreadyExists)
                ReportJobStatus(job, "done");
            else if (state == JobState.Failed)
                ReportJobStatus(job, TerminalStatusLabel(state, job.FailureReason));
        }
    }

    internal void Attach(ICliBackend backend)
    {
        backend.EventReceived += envelope =>
        {
            switch (envelope.Type)
            {
                case "job.upserted" when envelope.Payload is JobSummaryDto e:
                    ReportJobUpserted(e);
                    break;
                case "extraction.started" when envelope.Payload is ExtractionStartedEventDto e:
                    ReportExtractionStarted(e);
                    break;
                case "extraction.failed" when envelope.Payload is ExtractionFailedEventDto e:
                    ReportExtractionFailed(e);
                    break;
                case "job.started" when envelope.Payload is JobStartedEventDto e:
                    ReportJobStarted(e);
                    break;
                case "job.status" when envelope.Payload is JobStatusEventDto e:
                    ReportJobStatus(e);
                    break;
                case "job.folder-retrieving" when envelope.Payload is JobFolderRetrievingEventDto e:
                    ReportJobFolderRetrieving(e);
                    break;
                case "song.searching" when envelope.Payload is SongSearchingEventDto e:
                    ReportSongSearching(e);
                    break;
                case "download.started" when envelope.Payload is DownloadStartedEventDto e:
                    ReportDownloadStart(e);
                    break;
                case "download.progress" when envelope.Payload is DownloadProgressEventDto e:
                    ReportDownloadProgress(e);
                    break;
                case "download.state-changed" when envelope.Payload is DownloadStateChangedEventDto e:
                    ReportDownloadStateChanged(e);
                    break;
                case "song.state-changed" when envelope.Payload is SongStateChangedEventDto e:
                    ReportStateChanged(e);
                    break;
                case "album.download-started" when envelope.Payload is AlbumDownloadStartedEventDto e:
                    ReportAlbumDownloadStarted(e);
                    break;
                case "album.track-download-started" when envelope.Payload is AlbumTrackDownloadStartedEventDto e:
                    ReportAlbumTrackDownloadStarted(e);
                    break;
                case "album.download-completed" when envelope.Payload is AlbumDownloadCompletedEventDto e:
                    ReportAlbumDownloadCompleted(e);
                    break;
                case "on-complete.started" when envelope.Payload is OnCompleteStartedEventDto e:
                    ReportOnCompleteStart(e);
                    break;
                case "on-complete.ended" when envelope.Payload is OnCompleteEndedEventDto e:
                    ReportOnCompleteEnd(e);
                    break;
            }
        };
    }

    private void ReportJobUpserted(JobSummaryDto summary)
    {
        RememberBackendStructure(summary);

        if (summary.State == ServerProtocol.JobStates.Failed && summary.Kind != ServerJobKind.Song && summary.Kind != ServerJobKind.Extract)
        {
            ReportJobStatus(new JobStatusEventDto(summary, TerminalStatusLabel(summary.State, summary.FailureReason)));
        }

        if (PlainMode)
            return;

        if (summary.Kind == ServerJobKind.Album
            && IsTerminalJobState(summary.State)
            && _backendAlbumBlocks.TryRemove(summary.JobId, out var block))
        {
            CompleteBackendAlbumBlock(summary.JobId, block, summary);
        }
    }

    async Task TickLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                if (IsPaused) continue;

                foreach (var (_, d) in _bars)
                {
                    if (d.StateLabel != "InProgress" || d.Bar == null) continue;
                    d.SpinIndex++;
                    try { d.Bar.Refresh(d.Pct, BuildText(d)); } catch { }
                }

                foreach (var (_, d) in _backendBars)
                {
                    if (d.StateLabel != "InProgress" || d.Bar == null) continue;
                    d.SpinIndex++;
                    try { d.Bar.Refresh(d.Pct, BuildText(d)); } catch { }
                }

                foreach (var (job, block) in _albumBlocks)
                {
                    if (!_jobBars.TryGetValue(job, out var headerBar) || headerBar == null) continue;
                    int done  = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                    int total = block.Songs.Count;
                    _jobStatuses.TryGetValue(job, out var status);

                    if (done != block.LastDone || total != block.LastTotal || status != block.LastStatus)
                    {
                        try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job, done, total, status)); } catch { }
                        block.LastDone = done;
                        block.LastTotal = total;
                        block.LastStatus = status;
                    }

                    if (block.CompactBar != null && block.LastUpdatedSong != null && _bars.TryGetValue(block.LastUpdatedSong, out var d))
                    {
                        if (d.StateLabel == "InProgress") d.SpinIndex++;
                        try { block.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
                    }
                }

                foreach (var (jobId, block) in _backendAlbumBlocks)
                {
                    if (!_backendJobBars.TryGetValue(jobId, out var headerBar) || headerBar == null) continue;
                    int done  = BackendAlbumDoneCount(block);
                    int total = block.Songs.Count;
                    _backendJobStatuses.TryGetValue(jobId, out var status);

                    if (done != block.LastDone || total != block.LastTotal || status != block.LastStatus)
                    {
                        try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(block.Summary, done, total, status)); } catch { }
                        block.LastDone = done;
                        block.LastTotal = total;
                        block.LastStatus = status;
                    }

                    if (block.CompactBar != null && block.LastUpdatedSongId != null && _backendBars.TryGetValue(block.LastUpdatedSongId.Value, out var d))
                    {
                        if (d.StateLabel == "InProgress") d.SpinIndex++;
                        try { block.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
                    }
                }



            }
        }
        catch (OperationCanceledException) { }
    }


    // ── bar text construction ────────────────────────────────────────────

    static string BuildText(BarData d, bool indent = false)
    {
        string prefix = d.StateLabel == "InProgress"
            ? (indent ? "  " : "") + $"{SpinFrames[d.SpinIndex % SpinFrames.Length]} "
            : (indent ? "    " : "  ");
        var stateLabel = d.JobPrefix != null
            ? $"{d.JobPrefix}{d.StateLabel.ToLowerInvariant()}"
            : d.StateLabel;
        string label = (stateLabel + ":").PadRight(d.JobPrefix != null ? 0 : 12);
        return $"{prefix}{label} {d.BaseText}";
    }

    private int BackendAlbumDoneCount(BackendAlbumBlock block)
    {
        int done = 0;
        foreach (var song in block.Songs)
        {
            if (song.JobId is not Guid jobId)
                continue;

            if (block.CompletedSongIds.ContainsKey(jobId))
            {
                done++;
                continue;
            }

            if (_backendBars.TryGetValue(jobId, out var data) && IsTerminalBar(data))
                done++;
        }

        return done;
    }

    private static bool IsTerminalBar(BarData data)
        => data.StateLabel is "Succeeded" or "Already exists" or "Not found"
            || data.StateLabel.StartsWith("Failed", StringComparison.Ordinal);

    private void MarkBackendAlbumTrackCompleted(Guid songJobId)
    {
        foreach (var block in _backendAlbumBlocks.Values)
        {
            if (block.Songs.Any(song => song.JobId == songJobId))
                block.CompletedSongIds.TryAdd(songJobId, 0);
        }
    }

    private string AlbumHeaderText(AlbumJob job, int done, int total, string? status = null)
    {
        string statusStr = !string.IsNullOrEmpty(status) ? $" ({status})" : "";
        return $"[{job.DisplayId}] AlbumJob: {job.ToString(true)}{statusStr}  [{done}/{total}]";
    }

    private static string AlbumHeaderText(JobSummaryDto summary, int done, int total, string? status = null)
    {
        string statusStr = !string.IsNullOrEmpty(status) ? $" ({status})" : "";
        return $"[{summary.DisplayId}] AlbumJob: {summary.QueryText}{statusStr}  [{done}/{total}]";
    }

    private static string GetJobTypePrefix(Job job) => job switch
    {
        RetrieveFolderJob => "RetrieveFolderJob: ",
        _                 => job.GetType().Name + ": "
    };

    private static string GetJobTypePrefix(ServerJobKind kind)
    {
        if (kind == ServerJobKind.RetrieveFolder)
            return "RetrieveFolderJob: ";

        string kindText = kind.ToWireString();
        return $"{char.ToUpperInvariant(kindText[0])}{kindText[1..]}Job: ";
    }

    private static string ProfileSuffix(Job job)
    {
        var profiles = job.Config?.AppliedAutoProfiles;
        return profiles?.Count > 0 ? $" [{string.Join(", ", profiles)}]" : "";
    }

    private static string ProfileSuffix(JobSummaryDto summary)
        => summary.AppliedAutoProfiles.Count > 0 ? $" [{string.Join(", ", summary.AppliedAutoProfiles)}]" : "";

    private static string TextWithProfileSuffix(Job job, string text)
        => text + ProfileSuffix(job);

    private static string TextWithProfileSuffix(JobSummaryDto summary, string text)
        => text + ProfileSuffix(summary);

    private static string JobStatusLine(Job job, string status, string? detail = null)
        => $"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {detail ?? job.ToString(true)}";

    private static string JobStatusLine(JobSummaryDto summary, string status, string? detail = null)
        => $"[{summary.DisplayId}] {GetJobTypePrefix(summary.Kind)}{status}: {detail ?? summary.QueryText}";

    private static void WriteJobLineWithProfileSuffix(Job job, string text, ConsoleColor mainColor = ConsoleColor.Gray)
    {
        string suffix = ProfileSuffix(job);
        if (suffix.Length == 0)
        {
            Logger.Info(text, mainColor);
            return;
        }

        Logger.LogNonConsole(Logger.LogLevel.Info, text + suffix);
        Printing.Write(text, mainColor);
            Printing.WriteLine(suffix, ConsoleColor.DarkGray);
    }

    private void WritePlainJobStatus(Job job, string status, string? detail = null)
    {
        _jobStatuses[job] = status;
        var line = JobStatusLine(job, status, detail);
        if (_plainJobStatusLines.TryGetValue(job, out var previous) && previous == line)
            return;

        _plainJobStatusLines[job] = line;
        WriteJobLineWithProfileSuffix(job, line);
    }

    private void WritePlainJobStatus(JobSummaryDto summary, string status, string? detail = null)
    {
        _backendJobStatuses[summary.JobId] = status;
        var line = JobStatusLine(summary, status, detail);
        if (_backendPlainJobStatusLines.TryGetValue(summary.JobId, out var previous) && previous == line)
            return;

        _backendPlainJobStatusLines[summary.JobId] = line;
        RefreshOrPrintJobLineWithProfileSuffix(null, 0, summary, line, print: true);
    }

    private void WritePlainBackendSongStatus(
        Guid jobId,
        int displayId,
        SongQueryDto query,
        string status,
        string? detail = null)
    {
        var line = $"[{displayId}] SongJob: {status}: {detail ?? SongQueryText(query)}";
        if (_backendPlainJobStatusLines.TryGetValue(jobId, out var previous) && previous == line)
            return;

        _backendJobStatuses[jobId] = status;
        _backendPlainJobStatusLines[jobId] = line;
        Logger.Info(line);
    }

    private void ClearPlainJobStatus(Job job)
    {
        _jobStatuses.TryRemove(job, out _);
        _plainJobStatusLines.TryRemove(job, out _);
    }

    private void ClearPlainJobStatus(JobSummaryDto summary)
    {
        _backendJobStatuses.TryRemove(summary.JobId, out _);
        _backendPlainJobStatusLines.TryRemove(summary.JobId, out _);
    }

    private static void RefreshOrPrintJobLineWithProfileSuffix(ProgressBar? progress, int current, Job job, string text, bool print = false, bool refreshIfOffscreen = false)
    {
        var textWithSuffix = TextWithProfileSuffix(job, text);
        Printing.RefreshOrPrint(progress, current, textWithSuffix, print, refreshIfOffscreen);
    }

    private static void RefreshOrPrintJobLineWithProfileSuffix(ProgressBar? progress, int current, JobSummaryDto summary, string text, bool print = false, bool refreshIfOffscreen = false)
    {
        var textWithSuffix = TextWithProfileSuffix(summary, text);
        Printing.RefreshOrPrint(progress, current, textWithSuffix, print, refreshIfOffscreen);
    }

    static string FailureReasonLabel(FailureReason reason) => reason switch
    {
        FailureReason.NoSuitableFileFound  => "No suitable file found",
        FailureReason.InvalidSearchString  => "Invalid search string",
        FailureReason.OutOfDownloadRetries => "Out of download retries",
        FailureReason.AllDownloadsFailed   => "All downloads failed",
        FailureReason.ExtractionFailed     => "Extraction failed",
        FailureReason.Cancelled            => "Cancelled",
        FailureReason.Other                => "Unknown error",
        _                                  => "",
    };

    static string FailureReasonLabel(ServerFailureReason? reason) => reason switch
    {
        ServerFailureReason.NoSuitableFileFound  => "No suitable file found",
        ServerFailureReason.InvalidSearchString  => "Invalid search string",
        ServerFailureReason.OutOfDownloadRetries => "Out of download retries",
        ServerFailureReason.AllDownloadsFailed   => "All downloads failed",
        ServerFailureReason.ExtractionFailed     => "Extraction failed",
        ServerFailureReason.Cancelled            => "Cancelled",
        ServerFailureReason.Other                => "Unknown error",
        _                                        => "",
    };

    private static string GetStateLabel(TransferStates s)
    {
        if (s.HasFlag(TransferStates.InProgress))   return "InProgress";
        if (s.HasFlag(TransferStates.Queued))
            return s.HasFlag(TransferStates.Remotely) ? "Queued (R)" :
                   s.HasFlag(TransferStates.Locally)  ? "Queued (L)" : "Queued";
        if (s.HasFlag(TransferStates.Initializing)) return "Initialising";
        return "Requested";
    }

    private static bool IsTerminalJobState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done
            or ServerProtocol.JobStates.AlreadyExists
            or ServerProtocol.JobStates.Failed
            or ServerProtocol.JobStates.Skipped;

    private void RememberBackendStructure(JobSummaryDto summary)
    {
        _backendJobKinds[summary.JobId] = summary.Kind;
        if (IsBackendInlineChild(summary))
            _backendInlineChildJobs[summary.JobId] = 0;
        else
            _backendInlineChildJobs.TryRemove(summary.JobId, out _);
    }

    private bool IsBackendInlineChild(Guid jobId)
        => _backendInlineChildJobs.ContainsKey(jobId);

    private bool IsBackendInlineChild(JobSummaryDto summary)
        => summary.Kind == ServerJobKind.Song
            && summary.ParentJobId is Guid parentJobId
            && _backendJobKinds.TryGetValue(parentJobId, out var parentKind)
            && parentKind is ServerJobKind.Album;

    private static string SongDisplay(SongJob song)
    {
        var chosen = song.ChosenCandidate;
        if (chosen != null)
            return Printing.DisplayString(song.Query, chosen.File, chosen.Response, infoFirst: false);
        if (!string.IsNullOrEmpty(song.DownloadPath))
            return $"{song.ToString(true)} at {song.DownloadPath}";
        return song.ToString(true);
    }

    private static string TerminalLabel(SongJob song)
    {
        if (song.State == JobState.Done)
            return "Succeeded";
        if (song.State == JobState.AlreadyExists)
            return "Already exists";

        var reason = FailureReasonLabel(song.FailureReason);
        if (reason.Length == 0) reason = "Unknown error";
        return $"Failed[{reason}]";
    }

    private static string SongDisplay(SongStateChangedEventDto song)
    {
        var chosen = song.ChosenCandidate;
        if (chosen != null)
            return $"{chosen.Username}\\..\\{Path.GetFileName(chosen.Filename)}";
        if (!string.IsNullOrEmpty(song.DownloadPath))
            return $"{SongQueryText(song.Query)} at {song.DownloadPath}";
        return SongQueryText(song.Query);
    }

    private static string SongQueryText(SongQueryDto query)
    {
        var parts = new[] { query.Artist, query.Title }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length > 0)
            return string.Join(" - ", parts);

        return query.Album ?? query.Uri ?? "";
    }

    private static string TerminalLabel(SongStateChangedEventDto song)
    {
        if (song.State == ServerProtocol.JobStates.Done)
            return "Succeeded";
        if (song.State == ServerProtocol.JobStates.AlreadyExists)
            return "Already exists";

        var reason = FailureReasonLabel(song.FailureReason);
        if (reason.Length == 0) reason = "Unknown error";
        return $"Failed [{reason}]";
    }

    private static string TerminalStatusLabel(JobState state, FailureReason reason, string fallbackStatus = "failed")
    {
        if (state == JobState.Done)
            return "succeeded";
        if (state == JobState.AlreadyExists)
            return "already exists";

        var reasonLabel = FailureReasonLabel(reason);
        if (reasonLabel.Length == 0) reasonLabel = "Unknown error";
        return $"{fallbackStatus}[{reasonLabel}]";
    }

    private static string TerminalStatusLabel(SongJob song)
    {
        if (song.State == JobState.Done)
            return "succeeded";
        if (song.State == JobState.AlreadyExists)
            return "already exists";

        var reason = FailureReasonLabel(song.FailureReason);
        if (reason.Length == 0 && song.State is JobState.Failed or JobState.Skipped or JobState.NotFoundLastTime)
            reason = "Unknown error";

        var discovery = song.Discovery != null && song.Discovery.LockedFileCount > 0 
            ? $" (Found {song.Discovery.LockedFileCount} locked files)" 
            : "";
        return reason.Length > 0 ? $"failed [{reason}]{discovery}" : $"failed{discovery}";
    }

    private static string TerminalStatusLabel(ServerJobState state, ServerFailureReason? reason, string fallbackStatus = "failed")
    {
        if (state == ServerJobState.Done)
            return "succeeded";
        if (state == ServerJobState.AlreadyExists)
            return "already exists";

        var reasonLabel = FailureReasonLabel(reason);
        if (reasonLabel.Length == 0) reasonLabel = "Unknown error";
        return $"{fallbackStatus} [{reasonLabel}]";
    }

    private static string TerminalStatusLabel(SongStateChangedEventDto song)
    {
        if (song.State == ServerProtocol.JobStates.Done)
            return "succeeded";
        if (song.State == ServerProtocol.JobStates.AlreadyExists)
            return "already exists";

        var reason = FailureReasonLabel(song.FailureReason);
        if (reason.Length == 0 && song.State is ServerProtocol.JobStates.Failed or ServerProtocol.JobStates.Skipped or ServerProtocol.JobStates.NotFoundLastTime)
            reason = "Unknown error";

        var discovery = song.DiscoveryLockedFileCount > 0 
            ? $" (Found {song.DiscoveryLockedFileCount} locked files)" 
            : "";
        return reason.Length > 0 ? $"failed [{reason}]{discovery}" : $"failed{discovery}";
    }


    // ── event handlers ───────────────────────────────────────────────────

    private void ReportDownloadStart(SongJob song, FileCandidate candidate)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(
                song,
                "downloading",
                Printing.DisplayString(song.Query, candidate.File, candidate.Response, infoFirst: false));
            return;
        }

        var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Queued";
        d.BaseText   = Printing.DisplayString(song.Query, candidate.File, candidate.Response, infoFirst: false);
        bool isCompact = _cli.AlbumCompactProgress && _songToAlbum.ContainsKey(song);
        UpdateLastUpdated(song);
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d, indent: isCompact), print: d.Bar != null);
    }

    private void ReportDownloadStart(DownloadStartedEventDto song)
    {
        if (PlainMode)
        {
            WritePlainBackendSongStatus(
                song.JobId,
                song.DisplayId,
                song.Query,
                "downloading",
                $"{song.Candidate.Username}\\..\\{System.IO.Path.GetFileName(song.Candidate.Filename)}");
            return;
        }

        if (IsBackendInlineChild(song.JobId) && !_backendBars.ContainsKey(song.JobId))
            return;

        var d = _backendBars.GetOrAdd(song.JobId, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = IsBackendInlineChild(song.JobId) ? null : $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Queued";
        d.BaseText = $"{song.Candidate.Username}\\..\\{System.IO.Path.GetFileName(song.Candidate.Filename)}";
        bool isCompact = _cli.AlbumCompactProgress && _backendSongToAlbum.ContainsKey(song.JobId);
        UpdateLastUpdatedBackend(song.JobId);
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d, indent: isCompact), print: !IsBackendInlineChild(song.JobId) && d.Bar != null);
    }

    private void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes)
    {
        if (PlainMode) return;

        if (!_bars.TryGetValue(song, out var d)) return;
        d.Pct = totalBytes > 0 ? (int)(bytesTransferred * 100 / totalBytes) : 0;
        UpdateLastUpdated(song);
    }

    private void ReportDownloadProgress(DownloadProgressEventDto progress)
    {
        if (PlainMode) return;

        if (!_backendBars.TryGetValue(progress.JobId, out var d)) return;
        d.Pct = progress.TotalBytes > 0 ? (int)(progress.BytesTransferred * 100 / progress.TotalBytes) : 0;
        UpdateLastUpdatedBackend(progress.JobId);
    }

    private void ReportStateChanged(SongJob song)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(song, TerminalStatusLabel(song), SongDisplay(song));
            return;
        }

        Logger.LogNonConsole(Logger.LogLevel.Info, $"[{song.DisplayId}] SongJob: {TerminalLabel(song)}: {SongDisplay(song)}");

        if (_bars.TryGetValue(song, out var d))
        {
            UpdateLastUpdated(song);
            bool succeeded = song.State is JobState.Done or JobState.AlreadyExists;
            d.StateLabel = song.State switch
            {
                JobState.Done => "Succeeded",
                JobState.AlreadyExists => "Already exists",
                _ => "Failed",
            };
            if (succeeded)
            {
                d.Pct = 100;
                var display = SongDisplay(song);
                if (display != d.BaseText)
                    d.BaseText = display;
            }
            else
            {
                var reason = FailureReasonLabel(song.FailureReason);
                if (reason.Length == 0) reason = "Unknown error";
                if (!d.BaseText.Contains($"[{reason}]"))
                    d.BaseText += $"[{reason}]";
            }
            if (d.Bar != null)
            {
                Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
            }
            else if (_cli.AlbumCompactProgress && _songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var b) && b.CompactBar != null)
            {
                try { b.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
            }
        }
        _bars.TryRemove(song, out _);
        _savedState.TryRemove(song, out _);
    }

    private void ReportStateChanged(SongStateChangedEventDto song)
    {
        if (PlainMode)
        {
            WritePlainBackendSongStatus(
                song.JobId,
                song.DisplayId,
                song.Query,
                TerminalStatusLabel(song),
                SongDisplay(song));
            return;
        }

        if (!IsBackendInlineChild(song.JobId))
            Logger.LogNonConsole(Logger.LogLevel.Info, $"[{song.DisplayId}] SongJob: {TerminalLabel(song)}: {SongDisplay(song)}");

        if (_backendBars.TryGetValue(song.JobId, out var d))
        {
            UpdateLastUpdatedBackend(song.JobId);
            bool succeeded = song.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;
            d.StateLabel = song.State switch
            {
                ServerProtocol.JobStates.Done => "Succeeded",
                ServerProtocol.JobStates.AlreadyExists => "Already exists",
                _ => "Failed",
            };
            if (succeeded)
            {
                d.Pct = 100;
                var display = SongDisplay(song);
                if (display != d.BaseText)
                    d.BaseText = display;
            }
            else
            {
                var reason = FailureReasonLabel(song.FailureReason);
                if (reason.Length == 0) reason = "Unknown error";
                if (!d.BaseText.Contains($"[{reason}]"))
                    d.BaseText += $" [{reason}]";
            }
            MarkBackendAlbumTrackCompleted(song.JobId);
            if (d.Bar != null)
            {
                Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
            }
            else if (_cli.AlbumCompactProgress && _backendSongToAlbum.TryGetValue(song.JobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var b) && b.CompactBar != null)
            {
                try { b.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
            }
        }
        _backendBars.TryRemove(song.JobId, out _);
        _backendSavedState.TryRemove(song.JobId, out _);
    }


    // ── display event handlers ───────────────────────────────────────────

    private void ReportExtractionStarted(ExtractJob job)
    {
        if (job.InputType.HasValue)
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine();
                WriteJobLineWithProfileSuffix(job, $"[{job.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}");
            }
        }
    }

    private void ReportExtractionStarted(ExtractionStartedEventDto job)
    {
        if (!string.IsNullOrWhiteSpace(job.InputType))
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine();
                RefreshOrPrintJobLineWithProfileSuffix(
                    null,
                    0,
                    job.Summary,
                    $"[{job.Summary.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}",
                    print: true);
            }
        }
    }

    private void ReportExtractionCompleted(ExtractJob job, Job result) { }

    private void ReportExtractionFailed(ExtractJob job, string reason)
    {
        Logger.Error($"[{job.DisplayId}] ExtractJob: Failed: {job.Input}\n  Reason:    {reason}");
        _jobBars.TryRemove(job, out _);
    }

    private void ReportExtractionFailed(ExtractionFailedEventDto job)
    {
        Logger.Error($"[{job.Summary.DisplayId}] ExtractJob: Failed: {job.Summary.QueryText}\n  Reason:    {job.Reason}");
        _backendJobBars.TryRemove(job.Summary.JobId, out _);
    }

    private void ReportJobStarted(Job job)
    {
        if (PlainMode)
        {
            string plainStatus = job is RetrieveFolderJob ? "retrieving folder" : "searching";
            WritePlainJobStatus(job, plainStatus);
            return;
        }

        if (job is SongJob)
            return;

        var bar = Printing.GetProgressBar();
        _jobBars[job] = bar;
        string status = job switch
        {
            RetrieveFolderJob => "retrieving folder",
            AlbumJob aj when aj.ResolvedTarget != null => "downloading",
            _ => "searching"
        };
        _jobStatuses[job] = status;
        RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job, $"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {job.ToString(true)}", print: true);
    }

    private void ReportJobStarted(JobStartedEventDto job)
    {
        RememberBackendStructure(job.Summary);
        if (IsBackendInlineChild(job.Summary))
            return;

        string status = job.Summary.Kind == ServerJobKind.RetrieveFolder ? "retrieving folder" : "searching";

        if (PlainMode)
        {
            WritePlainJobStatus(job.Summary, status);
            return;
        }

        if (job.Summary.Kind == ServerJobKind.Song)
            return;

        var bar = Printing.GetProgressBar();
        _backendJobBars[job.Summary.JobId] = bar;
        _backendJobStatuses[job.Summary.JobId] = status;
        RefreshOrPrintJobLineWithProfileSuffix(
            bar,
            0,
            job.Summary,
            $"[{job.Summary.DisplayId}] {GetJobTypePrefix(job.Summary.Kind)}{status}: {job.Summary.QueryText}",
            print: true);
    }

    private void ReportJobSearching(Job job)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(job, "searching");
            return;
        }

        _jobStatuses[job] = "searching";
        if (_jobBars.TryGetValue(job, out var bar) && bar != null)
        {
            RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job, $"[{job.DisplayId}] {GetJobTypePrefix(job)}searching: {job.ToString(true)}", print: false);
        }
    }

    private void ReportJobDownloading(Job job)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(job, "downloading");
            return;
        }

        _jobStatuses[job] = "downloading";
        if (_jobBars.TryGetValue(job, out var bar) && bar != null)
        {
            RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job, $"[{job.DisplayId}] {GetJobTypePrefix(job)}downloading: {job.ToString(true)}", print: false);
        }
    }

    private void ReportAlbumDownloadStarted(AlbumJob job, AlbumFolder folder)
    {
        if (PlainMode)
        {
            _jobStatuses[job] = "downloading";
            WriteJobLineWithProfileSuffix(job, $"[{job.DisplayId}] AlbumJob: downloading: {job.ToString(true)}");
            return;
        }

        if (Console.IsOutputRedirected)
        {
            Printing.PrintAlbum(folder);
            return;
        }

        int total = folder.Files.Count;

        string ancestor = Utils.GreatestCommonDirectorySlsk(folder.Files
            .Where(f => f.ResolvedTarget != null)
            .Select(f => f.ResolvedTarget!.Filename));

        lock (Printing.ConsoleLock)
        {
            var headerBar = _jobBars.GetOrAdd(job, _ => Printing.GetProgressBar());
            _jobStatuses[job] = "downloading";
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job, AlbumHeaderText(job, 0, total, "downloading"), print: true); } catch { }

            if (!_cli.AlbumCompactProgress)
                Printing.PrintAlbumHeader(folder);

            var block = new AlbumBlock { Songs = folder.Files.ToList() };
            if (_cli.AlbumCompactProgress)
                block.CompactBar = Printing.GetProgressBar();

            foreach (var song in block.Songs)
            {
                _songToAlbum[song] = job;
                string filename  = song.ResolvedTarget?.Filename ?? song.Query.ToString();
                string shortName = ancestor.Length > 0
                    ? filename.Replace(ancestor, "").TrimStart('\\')
                    : System.IO.Path.GetFileName(filename);
                string baseText = song.ResolvedTarget != null
                    ? Printing.DisplayString(song.Query, song.ResolvedTarget.File, song.ResolvedTarget.Response, customPath: shortName, showUser: false)
                    : shortName;

                var bar = _cli.AlbumCompactProgress ? null : Printing.GetProgressBar();
                var d   = new BarData { Bar = bar, BaseText = baseText, StateLabel = "Pending", Pct = 0 };
                _bars[song] = d;
                if (bar != null)
                    try { bar.Refresh(0, BuildText(d)); } catch { }
            }

            if (block.CompactBar != null && block.Songs.Count > 0 && _bars.TryGetValue(block.Songs[0], out var d0))
            {
                try { block.CompactBar.Refresh(d0.Pct, BuildText(d0, indent: true)); } catch { }
            }

            _albumBlocks[job] = block;
        }
    }

    private void ReportAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        if (PlainMode)
        {
            _backendJobStatuses[job.Summary.JobId] = "downloading";
            RefreshOrPrintJobLineWithProfileSuffix(
                null,
                0,
                job.Summary,
                $"[{job.Summary.DisplayId}] AlbumJob: downloading: {job.Summary.QueryText}",
                print: true);
            return;
        }

        if (Console.IsOutputRedirected)
        {
            Printing.WriteLine();
            return;
        }

        int total = job.Tracks?.Count ?? job.Folder.Files?.Count ?? 0;
        lock (Printing.ConsoleLock)
        {
            var headerBar = _backendJobBars.GetOrAdd(job.Summary.JobId, _ => Printing.GetProgressBar());
            _backendJobStatuses[job.Summary.JobId] = "downloading";
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job.Summary, AlbumHeaderText(job.Summary, 0, total, "downloading"), print: true); } catch { }

            if (!_cli.AlbumCompactProgress)
                Printing.PrintAlbumHeader(ToAlbumFolder(job.Folder));
            InitializeBackendAlbumBlock(job.Summary, job.Tracks);
        }
    }

    private void ReportAlbumTrackDownloadStarted(AlbumJob job, AlbumFolder folder)
    {
        if (_albumBlocks.ContainsKey(job))
            return;

        string folderName = string.IsNullOrWhiteSpace(folder.FolderPath)
            ? job.ToString(true)
            : folder.FolderPath;

        if (PlainMode)
        {
            WriteJobLineWithProfileSuffix(job, $"[{job.DisplayId}] AlbumJob: downloading tracks: {job.ToString(true)} - {folderName}");
            return;
        }

        Printing.WriteLine();
        WriteJobLineWithProfileSuffix(job, $"[{job.DisplayId}] AlbumJob: downloading tracks: {job.ToString(true)}");
        Printing.WriteLine($"Folder: {folderName}", ConsoleColor.DarkGray);
    }

    private void ReportAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        if (_backendAlbumBlocks.ContainsKey(job.Summary.JobId))
            return;

        string folderName = string.IsNullOrWhiteSpace(job.Folder.FolderPath)
            ? job.Summary.QueryText ?? ""
            : job.Folder.FolderPath;

        if (PlainMode)
        {
            RefreshOrPrintJobLineWithProfileSuffix(
                null,
                0,
                job.Summary,
                $"[{job.Summary.DisplayId}] AlbumJob: downloading tracks: {job.Summary.QueryText} - {folderName}",
                print: true);
            return;
        }

        _backendJobStatuses[job.Summary.JobId] = "downloading tracks";
        InitializeBackendAlbumBlock(job.Summary, job.Tracks);

        if (_backendJobBars.TryGetValue(job.Summary.JobId, out var headerBar) && headerBar != null)
        {
            int total = job.Tracks?.Count ?? 0;
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job.Summary, AlbumHeaderText(job.Summary, 0, total, "downloading tracks"), print: true); } catch { }
        }
        else
        {
            RefreshOrPrintJobLineWithProfileSuffix(
                null,
                0,
                job.Summary,
                $"[{job.Summary.DisplayId}] AlbumJob: downloading tracks: {job.Summary.QueryText}",
                print: true);
        }

        Printing.WriteLine($"Folder: {folderName}", ConsoleColor.DarkGray);
    }

    private void ReportAlbumDownloadCompleted(AlbumJob job)
    {
        if (PlainMode)
        {
            _jobStatuses.TryRemove(job, out _);
            return;
        }

        if (_albumBlocks.TryGetValue(job, out var block))
        {
            foreach (var s in block.Songs)
                _songToAlbum.TryRemove(s, out _);

            int total = block.Songs.Count;
            if (_jobBars.TryGetValue(job, out var headerBar) && headerBar != null)
            {
                _jobStatuses.TryGetValue(job, out var status);
                try { headerBar.Refresh(100, AlbumHeaderText(job, total, total, status)); } catch { }
            }
            _albumBlocks.TryRemove(job, out _);
        }
        _jobBars.TryRemove(job, out _);
        _jobStatuses.TryRemove(job, out _);
        _jobSpinIndexes.TryRemove(job, out _);

        if (!Console.IsOutputRedirected && !_cli.NoProgress)
        {
            Printing.WriteLine();
        }
    }

    private void ReportAlbumDownloadCompleted(AlbumDownloadCompletedEventDto job)
    {
        if (PlainMode)
        {
            _backendJobStatuses.TryRemove(job.Summary.JobId, out _);
            return;
        }

        if (_backendAlbumBlocks.TryRemove(job.Summary.JobId, out var block))
        {
            foreach (var s in block.Songs)
                if (s.JobId.HasValue) _backendSongToAlbum.TryRemove(s.JobId.Value, out _);

            CompleteBackendAlbumBlock(job.Summary.JobId, block, job.Summary);
        }
        _backendJobBars.TryRemove(job.Summary.JobId, out _);
        _backendJobStatuses.TryRemove(job.Summary.JobId, out _);

        if (!Console.IsOutputRedirected && !_cli.NoProgress)
            Printing.WriteLine();
    }

    private void CompleteBackendAlbumBlock(Guid albumJobId, BackendAlbumBlock block, JobSummaryDto summary)
    {
        CompleteRemainingBackendAlbumBars(block, summary);
        int total = block.Songs.Count;
        if (_backendJobBars.TryGetValue(albumJobId, out var headerBar) && headerBar != null)
        {
            _backendJobStatuses.TryGetValue(albumJobId, out var status);
            try { headerBar.Refresh(100, AlbumHeaderText(summary, total, total, status)); } catch { }
        }
        _backendJobBars.TryRemove(albumJobId, out _);
        _backendJobStatuses.TryRemove(albumJobId, out _);
    }

    private void InitializeBackendAlbumBlock(JobSummaryDto summary, IReadOnlyList<SongJobPayloadDto>? tracks)
    {
        var block = new BackendAlbumBlock { Summary = summary, Songs = tracks?.ToList() ?? [] };
        if (_cli.AlbumCompactProgress)
        {
            block.CompactBar = Printing.GetProgressBar();
            if (block.Songs.Count > 0 && block.Songs[0].JobId.HasValue && _backendBars.TryGetValue(block.Songs[0].JobId!.Value, out var d0))
            {
                try { block.CompactBar.Refresh(d0.Pct, BuildText(d0, indent: true)); } catch { }
            }
        }

        foreach (var song in block.Songs.Where(s => s.JobId.HasValue))
        {
            _backendSongToAlbum[song.JobId!.Value] = summary.JobId;
            string filename = song.ResolvedFilename ?? $"{song.Query.Artist} - {song.Query.Title}";
            string shortName = System.IO.Path.GetFileName(filename);
            var bar = _cli.AlbumCompactProgress ? null : Printing.GetProgressBar();
            var data = ToBarData(song, bar, shortName);
            _backendBars[song.JobId!.Value] = data;
            if (bar != null)
                try { bar.Refresh(data.Pct, BuildText(data)); } catch { }
        }

        _backendAlbumBlocks[summary.JobId] = block;
    }

    private void CompleteRemainingBackendAlbumBars(BackendAlbumBlock block, JobSummaryDto summary)
    {
        foreach (var song in block.Songs.Where(song => song.JobId.HasValue))
        {
            block.CompletedSongIds.TryAdd(song.JobId!.Value, 0);

            if (!_backendBars.TryGetValue(song.JobId!.Value, out var data))
                continue;

            bool albumSucceeded = summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;
            data.StateLabel = summary.State switch
            {
                ServerProtocol.JobStates.Done => "Succeeded",
                ServerProtocol.JobStates.AlreadyExists => "Already exists",
                _ => "Failed",
            };
            if (albumSucceeded)
            {
                data.Pct = 100;
            }
            else
            {
                var reason = FailureReasonLabel(summary.FailureReason) is { Length: > 0 } summaryReason
                    ? summaryReason
                    : FailureReasonLabel(song.FailureReason);
                if (reason.Length == 0) reason = "Unknown error";
                if (!data.BaseText.Contains($"[{reason}]", StringComparison.Ordinal))
                    data.BaseText += $" [{reason}]";
            }

            if (data.Bar != null)
                Printing.RefreshOrPrint(data.Bar, data.Pct, BuildText(data), print: false);
            _backendBars.TryRemove(song.JobId.Value, out _);
            _backendSavedState.TryRemove(song.JobId.Value, out _);
        }
    }

    private void ReportJobFolderRetrieving(Job job)
    {
        if (PlainMode)
        {
            WriteJobLineWithProfileSuffix(job, $"[{job.DisplayId}] {GetJobTypePrefix(job)}retrieving folder: {job.ToString(true)}");
            return;
        }

        _jobBars.TryGetValue(job, out var bar);
        Printing.RefreshOrPrint(bar, 0, "Getting all files in folder..", print: true);
    }

    private void ReportJobFolderRetrieving(JobFolderRetrievingEventDto job)
    {
        if (PlainMode)
        {
            RefreshOrPrintJobLineWithProfileSuffix(
                null,
                0,
                job.Summary,
                $"[{job.Summary.DisplayId}] {GetJobTypePrefix(job.Summary.Kind)}retrieving folder: {job.Summary.QueryText}",
                print: true);
            return;
        }

        _backendJobBars.TryGetValue(job.Summary.JobId, out var bar);
        Printing.RefreshOrPrint(bar, 0, "Getting all files in folder..", print: true);
    }


    private void ReportSongSearching(SongJob song)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(song, "searching");
            return;
        }

        if (_bars.TryGetValue(song, out var existing))
        {
            existing.StateLabel = "Searching";
            existing.JobPrefix = $"[{song.DisplayId}] SongJob: ";
            existing.BaseText   = song.ToString();
            Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
            return;
        }

        bool isFirst = !_bars.ContainsKey(song);
        var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Searching";
        d.BaseText   = song.ToString();
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: isFirst);
    }

    private void ReportSongSearching(SongSearchingEventDto song)
    {
        if (PlainMode)
        {
            WritePlainBackendSongStatus(
                song.JobId,
                song.DisplayId,
                song.Query,
                "searching");
            return;
        }

        if (_backendBars.TryGetValue(song.JobId, out var existing))
        {
            existing.StateLabel = "Searching";
            existing.JobPrefix = IsBackendInlineChild(song.JobId) ? null : $"[{song.DisplayId}] SongJob: ";
            existing.BaseText = $"{song.Query.Artist} - {song.Query.Title}";
            Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
            return;
        }

        if (IsBackendInlineChild(song.JobId))
            return;

        bool isFirst = !_backendBars.ContainsKey(song.JobId);
        var d = _backendBars.GetOrAdd(song.JobId, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Searching";
        d.BaseText = $"{song.Query.Artist} - {song.Query.Title}";
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: isFirst);
    }

    private void ReportDownloadStateChanged(SongJob song, string stateLabel)
    {
        if (PlainMode) return;

        if (!_bars.TryGetValue(song, out var d)) return;
        d.StateLabel = stateLabel;
        bool isCompact = _cli.AlbumCompactProgress && _songToAlbum.ContainsKey(song);
        Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d, indent: isCompact), print: false);
    }

    private void ReportDownloadStateChanged(DownloadStateChangedEventDto song)
    {
        if (PlainMode) return;

        if (!_backendBars.TryGetValue(song.JobId, out var d)) return;
        d.StateLabel = GetStateLabel(Enum.TryParse<TransferStates>(song.State, out var state) ? state : TransferStates.None);
        bool isCompact = _cli.AlbumCompactProgress && _backendSongToAlbum.ContainsKey(song.JobId);
        Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d, indent: isCompact), print: false);
    }

    private void ReportOnCompleteStart(SongJob song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete start: {song}");
            return;
        }

        if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
        _savedState[song] = (d.Bar.Line1 ?? "", d.Bar.Current);
        Printing.RefreshOrPrint(d.Bar, d.Bar.Current, "  OnComplete:  " + $"{song}");
    }

    private void ReportOnCompleteStart(OnCompleteStartedEventDto song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete start: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
            return;
        }

        if (!_backendBars.TryGetValue(song.JobId, out var d) || d.Bar == null) return;
        _backendSavedState[song.JobId] = (d.Bar.Line1 ?? "", d.Bar.Current);
        Printing.RefreshOrPrint(d.Bar, d.Bar.Current, "  OnComplete:  " + $"[{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
    }

    private void ReportOnCompleteEnd(SongJob song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete end: {song}");
            return;
        }

        if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
        if (_savedState.TryGetValue(song, out var saved))
            Printing.RefreshOrPrint(d.Bar, saved.pos, saved.text);
    }

    private void ReportOnCompleteEnd(OnCompleteEndedEventDto song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete end: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
            return;
        }

        if (!_backendBars.TryGetValue(song.JobId, out var d) || d.Bar == null) return;
        if (_backendSavedState.TryGetValue(song.JobId, out var saved))
            Printing.RefreshOrPrint(d.Bar, saved.pos, saved.text);
    }

    private void ReportJobStatus(Job job, string status)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(job, status);
            return;
        }

        _jobStatuses[job] = status;
        if (_jobBars.TryGetValue(job, out var bar) && bar != null)
        {
            if (job is AlbumJob aj && _albumBlocks.TryGetValue(aj, out var block))
            {
                int done = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                int total = block.Songs.Count;
                try { bar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(aj, done, total, status)); } catch { }
            }
            else
            {
                Printing.RefreshOrPrint(bar, 0, $"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {job.ToString(true)}", print: false);
            }
        }
    }

    private void ReportJobStatus(JobStatusEventDto job)
    {
        RememberBackendStructure(job.Summary);
        if (IsBackendInlineChild(job.Summary))
            return;

        if (PlainMode)
        {
            WritePlainJobStatus(job.Summary, job.Status);
            return;
        }

        _backendJobStatuses[job.Summary.JobId] = job.Status;
        if (_backendJobBars.TryGetValue(job.Summary.JobId, out var bar) && bar != null)
        {
            if (job.Summary.Kind == ServerJobKind.Album && _backendAlbumBlocks.TryGetValue(job.Summary.JobId, out var block))
            {
                int total = block.Songs.Count;
                int done = BackendAlbumDoneCount(block);
                try { bar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job.Summary, done, total, job.Status)); } catch { }
            }
            else
            {
                Printing.RefreshOrPrint(bar, 0, $"[{job.Summary.DisplayId}] {GetJobTypePrefix(job.Summary.Kind)}{job.Status}: {job.Summary.QueryText}", print: false);
            }
        }
    }

    private static AlbumFolder ToAlbumFolder(AlbumFolderDto folder)
        => new(
            folder.Username,
            folder.FolderPath,
            folder.Files?.Select(ToSongJob).ToList() ?? [])
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };

    private static SongJob ToSongJob(FileCandidateDto file)
    {
        var candidate = ToFileCandidate(file);
        var query = Searcher.InferSongQuery(candidate.Filename, new SongQuery());
        return new SongJob(query) { ResolvedTarget = candidate };
    }

    private static FileCandidate ToFileCandidate(FileCandidateDto candidate)
        => new(
            new SearchResponse(
                candidate.Username,
                -1,
                candidate.Peer.HasFreeUploadSlot ?? false,
                candidate.Peer.UploadSpeed ?? -1,
                -1,
                null),
            new Soulseek.File(
                0,
                candidate.Filename,
                candidate.Size,
                candidate.Extension ?? Path.GetExtension(candidate.Filename),
                candidate.Attributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));

    private static SongJob ToSongJob(SongJobPayloadDto dto)
    {
        var job = new SongJob(new SongQuery
        {
            Artist = dto.Query.Artist ?? "",
            Title = dto.Query.Title ?? "",
            Album = dto.Query.Album ?? "",
            URI = dto.Query.Uri ?? "",
            Length = dto.Query.Length ?? -1,
            ArtistMaybeWrong = dto.Query.ArtistMaybeWrong,
        })
        {
            ResolvedTarget = dto.ResolvedUsername != null && dto.ResolvedFilename != null
                ? new FileCandidate(
                    new SearchResponse(
                        dto.ResolvedUsername,
                        -1,
                        dto.ResolvedHasFreeUploadSlot ?? false,
                        dto.ResolvedUploadSpeed ?? -1,
                        -1,
                        null),
                    new Soulseek.File(
                        1,
                        dto.ResolvedFilename,
                        dto.ResolvedSize ?? 0,
                        dto.ResolvedExtension ?? System.IO.Path.GetExtension(dto.ResolvedFilename),
                        dto.ResolvedAttributes?.Select(x => new FileAttribute(Enum.Parse<FileAttributeType>(x.Type), x.Value))))
                : null
        };

        if (dto.State != null && Enum.TryParse<JobState>(dto.State.Value.ToString(), out var state))
        {
            if (state == JobState.Failed)
            {
                Enum.TryParse<FailureReason>(dto.FailureReason?.ToString(), out var failureReason);
                job.Fail(failureReason, dto.FailureMessage);
            }
            else if (state is JobState.Skipped or JobState.AlreadyExists or JobState.NotFoundLastTime)
            {
                Enum.TryParse<FailureReason>(dto.FailureReason?.ToString(), out var failureReason);
                job.SetSkipped(state, failureReason);
            }
            else
            {
                job.UpdateState(state);
            }
        }
        job.DownloadPath = dto.DownloadPath;

        return job;
    }

    private static BarData ToBarData(SongJobPayloadDto song, ProgressBar? bar, string shortName)
    {
        var data = new BarData { Bar = bar, BaseText = shortName, StateLabel = "Pending", Pct = 0 };

        if (song.State == ServerProtocol.JobStates.Done)
        {
            data.StateLabel = "Succeeded";
            data.Pct = 100;
        }
        else if (song.State == ServerProtocol.JobStates.AlreadyExists)
        {
            data.StateLabel = "Already exists";
            data.Pct = 100;
        }
        else if (song.State == ServerProtocol.JobStates.Failed)
        {
            data.StateLabel = "Failed";
            if (song.FailureReason != null)
                data.BaseText += $" [{FailureReasonLabel(song.FailureReason)}]";
        }
        else if (song.State == ServerProtocol.JobStates.Downloading)
        {
            data.StateLabel = "InProgress";
        }
        else if (song.State == ServerProtocol.JobStates.Searching)
        {
            data.StateLabel = "Searching";
        }

        return data;
    }

    private void UpdateLastUpdated(SongJob song)
    {
        if (_cli.AlbumCompactProgress && _songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var block))
            block.LastUpdatedSong = song;
    }

    private void UpdateLastUpdatedBackend(Guid songJobId)
    {
        if (_cli.AlbumCompactProgress && _backendSongToAlbum.TryGetValue(songJobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var block))
            block.LastUpdatedSongId = songJobId;
    }
}
