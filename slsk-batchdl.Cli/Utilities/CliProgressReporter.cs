using System.Collections.Concurrent;
using Spectre.Console;
using Soulseek;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using ProgressBar = Sldl.Cli.IProgressBar;
using Sldl.Core.Settings;
using Sldl.Server;

namespace Sldl.Cli;

public class CliProgressReporter
{
    private readonly CliSettings _cli;
    private readonly TerminalLiveRenderer? _live;

    private readonly ConcurrentDictionary<Guid, BarData> _bars = new();
    private readonly ConcurrentDictionary<Guid, ProgressBar?> _jobBars = new();
    private readonly ConcurrentDictionary<Guid, AlbumBlock> _albumBlocks = new();
    private readonly ConcurrentDictionary<Guid, string> _jobStatuses = new();
    private readonly ConcurrentDictionary<Guid, (string text, int pos)> _savedState = new();
    private readonly ConcurrentDictionary<Guid, string> _plainJobStatusLines = new();
    private readonly ConcurrentDictionary<Guid, ServerJobKind> _jobKinds = new();
    private readonly ConcurrentDictionary<Guid, Guid> _parentJobIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _inlineChildJobs = new();
    private readonly ConcurrentDictionary<Guid, (int DisplayId, string Name, TerminalFileMetadata? Metadata)> _liveSongInfo = new();
    private readonly ConcurrentDictionary<Guid, byte> _liveTerminalParentLogs = new();
    private readonly ConcurrentDictionary<Guid, Guid> _songToAlbum = new();

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
        public long         LastBytes;
        public long         LastSpeedTicks;
        public long?        SpeedBps;
        public TerminalFileMetadata? Metadata;
    }

    sealed class AlbumBlock
    {
        public JobSummaryDto Summary = default!;
        public List<SongJobPayloadDto> Songs = new();
        public ConcurrentDictionary<Guid, byte> CompletedSongIds = new();
        public Guid?         LastUpdatedSongId;
        public int           LastDone = -1;
        public int           LastTotal = -1;
        public string?       LastStatus;
        public string?       RemoteFolderDisplay;
        public string?       RemoteFolderPrefix;
    }

    private readonly CancellationTokenSource _tickCts = new();

    private bool _isPaused;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            if (_live != null)
                _live.IsPaused = value;
        }
    }

    private bool LiveMode => _live != null;

    public bool UsesLiveRendering => LiveMode;

    public CliProgressReporter(CliSettings cli)
    {
        _cli = cli;
        if (!cli.NoProgress && !Console.IsOutputRedirected)
            _live = new TerminalLiveRenderer();
        _ = TickLoopAsync(_tickCts.Token);
    }

    public void Stop()
    {
        _tickCts.Cancel();
        _live?.Dispose();
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
                case "download.attempt-failed" when envelope.Payload is DownloadAttemptFailedEventDto e:
                    ReportDownloadAttemptFailed(e);
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
                case "search.rate-limited" when envelope.Payload is SearchRateLimitedEventDto rl:
                    if (LiveMode)
                        _live!.SetRateLimited(rl.ResetsAt);
                    else
                    {
                        int secs = Math.Max(0, (int)Math.Ceiling((rl.ResetsAt - DateTimeOffset.UtcNow).TotalSeconds));
                        Printing.WriteLine($"Search rate limit reached, resuming in {secs}s", ConsoleColor.DarkGray);
                    }
                    break;
                case "search.resumed":
                    _live?.SetRateLimited(null);
                    break;
            }
        };
    }

    private void ReportJobUpserted(JobSummaryDto summary)
    {
        RememberStructure(summary);
        if (!IsInfrastructureJobKind(summary.Kind))
            _live?.UpsertJob(new TerminalJobRecord(
                summary.JobId.ToString(),
                summary.DisplayId,
                GetJobTypeLabel(summary.Kind),
                summary.State.ToString(),
                summary.ParentJobId?.ToString()));

        if (LiveMode && IsTerminalJobState(summary.State))
        {
            if (summary.Kind == ServerJobKind.Album && IsSuccessfulTerminalState(summary.State))
                return;

            RemoveLiveJob(summary.JobId);
            if (summary.Kind == ServerJobKind.Album && _albumBlocks.TryRemove(summary.JobId, out var liveBlock))
            {
                foreach (var song in liveBlock.Songs)
                    if (song.JobId is Guid songJobId)
                    {
                        _songToAlbum.TryRemove(songJobId, out _);
                    }
            }

            if (summary.Kind != ServerJobKind.Song && _liveTerminalParentLogs.TryAdd(summary.JobId, 0))
            {
                var kind = TerminalKind(summary);
                var label = TerminalStatusLabel(summary.State, summary.FailureReason);
                var name = summary.ItemName ?? "";
                var detail = summary.QueryText ?? name;
                LogLive(kind, summary, $"{label}: {WithName(name, detail)}");
            }

            return;
        }

        if (LiveMode && IsContainerJobKind(summary.Kind))
        {
            if (IsTransparentContainer(summary.JobId, summary.Kind))
                return;

            _jobStatuses.TryGetValue(summary.JobId, out var containerStatus);
            UpsertLiveJob(summary, containerStatus ?? summary.State.ToString().ToLowerInvariant());
            return;
        }

        if (summary.State == ServerProtocol.JobStates.Failed && summary.Kind != ServerJobKind.Song && summary.Kind != ServerJobKind.Extract)
            ReportJobStatus(new JobStatusEventDto(summary, TerminalStatusLabel(summary.State, summary.FailureReason)));

        if (PlainMode)
            return;

        if (summary.Kind == ServerJobKind.Album
            && IsTerminalJobState(summary.State)
            && _albumBlocks.TryRemove(summary.JobId, out var block))
        {
            CompleteAlbumBlock(summary.JobId, block, summary);
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

                foreach (var (jobId, block) in _albumBlocks)
                {
                    if (!_jobBars.TryGetValue(jobId, out var headerBar) || headerBar == null) continue;
                    int done  = AlbumDoneCount(block);
                    int total = block.Songs.Count;
                    _jobStatuses.TryGetValue(jobId, out var status);

                    if (done != block.LastDone || total != block.LastTotal || status != block.LastStatus)
                    {
                        try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(block.Summary, done, total, status)); } catch { }
                        block.LastDone = done;
                        block.LastTotal = total;
                        block.LastStatus = status;
                    }

                }
            }
        }
        catch (OperationCanceledException) { }
    }


    // ── bar text construction ────────────────────────────────────────────

    static string BuildText(BarData d)
    {
        string prefix = d.StateLabel == "InProgress"
            ? $"{SpinFrames[d.SpinIndex % SpinFrames.Length]} "
            : "  ";
        var stateLabel = d.JobPrefix != null
            ? $"{d.JobPrefix}{d.StateLabel.ToLowerInvariant()}"
            : d.StateLabel;
        string label = (stateLabel + ":").PadRight(d.JobPrefix != null ? 0 : 12);
        return $"{prefix}{label} {d.BaseText}";
    }

    private int AlbumDoneCount(AlbumBlock block)
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
            if (_bars.TryGetValue(jobId, out var data) && IsTerminalBar(data))
                done++;
        }
        return done;
    }

    private static bool IsTerminalBar(BarData data)
        => data.StateLabel is "Succeeded" or "Already exists" or "Not found"
            || data.StateLabel.StartsWith("Failed", StringComparison.Ordinal);

    private void MarkAlbumTrackCompleted(Guid songJobId)
    {
        if (_songToAlbum.TryGetValue(songJobId, out var albumId) && _albumBlocks.TryGetValue(albumId, out var block))
            block.CompletedSongIds.TryAdd(songJobId, 0);
    }

    private bool TryGetAlbumParent(Guid songJobId, out Guid albumId)
    {
        if (_songToAlbum.TryGetValue(songJobId, out albumId))
            return true;

        if (_parentJobIds.TryGetValue(songJobId, out albumId)
            && _jobKinds.TryGetValue(albumId, out var parentKind)
            && parentKind == ServerJobKind.Album
            && _albumBlocks.ContainsKey(albumId))
            return true;

        albumId = default;
        return false;
    }

    private bool TryRegisterAlbumChild(
        Guid songJobId,
        int displayId,
        SongQueryDto query,
        FileCandidateDto? candidate,
        out Guid albumId,
        out AlbumBlock block,
        out BarData data)
    {
        if (!TryGetAlbumParent(songJobId, out albumId) || !_albumBlocks.TryGetValue(albumId, out var foundBlock))
        {
            block = default!;
            data = default!;
            return false;
        }

        block = foundBlock;
        _songToAlbum[songJobId] = albumId;

        if (!block.Songs.Any(song => song.JobId == songJobId))
        {
            block.Songs.Add(new SongJobPayloadDto(
                query,
                CandidateCount: candidate != null ? 1 : null,
                DownloadPath: null,
                ResolvedUsername: candidate?.Username,
                ResolvedFilename: candidate?.Filename,
                ResolvedHasFreeUploadSlot: candidate?.Peer.HasFreeUploadSlot,
                ResolvedUploadSpeed: candidate?.Peer.UploadSpeed,
                ResolvedSize: candidate?.Size,
                ResolvedSampleRate: candidate?.SampleRate,
                ResolvedExtension: candidate?.Extension,
                ResolvedAttributes: candidate?.Attributes,
                JobId: songJobId,
                DisplayId: displayId,
                State: ServerJobState.Pending));
        }

        var baseText = candidate != null
            ? AlbumChildDisplay(block, CandidateDisplayLive(candidate))
            : SongQueryText(query);
        data = _bars.GetOrAdd(songJobId, _ => new BarData
        {
            BaseText = baseText,
            StateLabel = "pending",
            Pct = 0,
            Metadata = candidate != null ? CandidateMetadata(candidate) : null,
        });
        if (candidate != null)
            data.Metadata = CandidateMetadata(candidate);
        return true;
    }

    private static string AlbumHeaderText(JobSummaryDto summary, int done, int total, string? status = null)
    {
        string statusStr = !string.IsNullOrEmpty(status) ? $" ({status})" : "";
        return $"[{summary.DisplayId}] AlbumJob: {summary.QueryText}{statusStr}  [{done}/{total}]";
    }

    private static string WithName(string name, string detail)
    {
        if (string.IsNullOrWhiteSpace(name))
            return detail;

        return detail == name ? detail : $"{name}: {detail}";
    }

    private static bool IsInfrastructureJobKind(ServerJobKind kind)
        => kind is ServerJobKind.Extract or ServerJobKind.JobList or ServerJobKind.RetrieveFolder
            or ServerJobKind.Aggregate or ServerJobKind.AlbumAggregate;

    private static bool IsContainerJobKind(ServerJobKind kind)
        => kind is ServerJobKind.JobList or ServerJobKind.Aggregate or ServerJobKind.AlbumAggregate;

    private bool IsTransparentContainer(Guid jobId, ServerJobKind kind)
        => kind == ServerJobKind.JobList
            && _parentJobIds.TryGetValue(jobId, out var parentId)
            && _jobKinds.TryGetValue(parentId, out var parentKind)
            && parentKind is ServerJobKind.Aggregate or ServerJobKind.AlbumAggregate;

    private string? GetContainerParentId(Guid jobId)
    {
        if (!_parentJobIds.TryGetValue(jobId, out var parentId)
            || !_jobKinds.TryGetValue(parentId, out var parentKind))
            return null;

        if (IsTransparentContainer(parentId, parentKind)
            && _parentJobIds.TryGetValue(parentId, out var grandParentId)
            && _jobKinds.TryGetValue(grandParentId, out var grandParentKind)
            && IsContainerJobKind(grandParentKind))
            return grandParentId.ToString();

        return IsContainerJobKind(parentKind) ? parentId.ToString() : null;
    }


    private static string GetJobTypePrefix(ServerJobKind kind)
        => $"{GetJobTypeLabel(kind)}: ";

    private static string GetJobTypeLabel(ServerJobKind kind)
    {
        if (kind == ServerJobKind.RetrieveFolder)
            return "Retrieve Folder";
        if (kind == ServerJobKind.JobList)
            return "Job List";
        if (kind == ServerJobKind.AlbumAggregate)
            return "Album Aggregate";

        string kindText = kind.ToWireString();
        return $"{char.ToUpperInvariant(kindText[0])}{kindText[1..]}";
    }

    private static string ProfileSuffix(JobSummaryDto summary)
        => summary.AppliedAutoProfiles.Count > 0 ? $" [{string.Join(", ", summary.AppliedAutoProfiles)}]" : "";

    private static string TextWithProfileSuffix(JobSummaryDto summary, string text)
        => text + ProfileSuffix(summary);

    private static string JobStatusLine(JobSummaryDto summary, string status, string? detail = null)
    {
        var name = summary.ItemName ?? "";
        var d = detail ?? summary.QueryText ?? name;
        return $"[{summary.DisplayId}] {GetJobTypePrefix(summary.Kind)}{status}: {WithName(name, d)}";
    }

    private void UpsertLiveJob(
        JobSummaryDto summary,
        string state,
        int? percent = null,
        int? done = null,
        int? total = null,
        IReadOnlyList<JobChildView>? children = null,
        string? displayName = null,
        TerminalFileMetadata? metadata = null)
    {
        if (_live == null) return;
        _live.Upsert(new JobView(
            summary.JobId.ToString(),
            summary.DisplayId,
            GetJobTypeLabel(summary.Kind),
            displayName ?? summary.QueryText ?? summary.ItemName ?? "",
            state,
            Percent: percent,
            DoneChildren: done,
            TotalChildren: total,
            Children: children,
            Metadata: metadata,
            ParentId: GetContainerParentId(summary.JobId)));
    }

    private void UpsertLiveSong(Guid jobId, int displayId, string name, string state, int? percent = null, long? speedBps = null, TerminalFileMetadata? metadata = null)
    {
        _live?.Upsert(new JobView(jobId.ToString(), displayId, "Song", name, state,
            Percent: percent,
            SpeedBytesPerSecond: speedBps,
            Metadata: metadata,
            ParentId: GetContainerParentId(jobId)));
    }

    private void RemoveLiveJob(Guid jobId) => _live?.Remove(jobId.ToString());

    private void LogLive(TerminalLogKind kind, JobSummaryDto summary, string message)
        => _live?.Log(new TerminalLogLine(kind, summary.JobId.ToString(), summary.DisplayId, GetJobTypeLabel(summary.Kind), message));

    private void LogLiveSong(TerminalLogKind kind, Guid jobId, int displayId, string message)
        => _live?.Log(new TerminalLogLine(kind, jobId.ToString(), displayId, "Song", message));

    private void LogLiveAlbumTrack(TerminalLogKind kind, JobSummaryDto summary, string message)
        => _live?.Log(new TerminalLogLine(kind, summary.JobId.ToString(), summary.DisplayId, "Album Track", message));

    private static TerminalLogKind TerminalKind(SongStateChangedEventDto song, bool albumTrack = false)
        => song.State switch
        {
            ServerProtocol.JobStates.Done => albumTrack ? TerminalLogKind.AlbumTrackDownloaded : TerminalLogKind.SongDownloaded,
            ServerProtocol.JobStates.AlreadyExists => albumTrack ? TerminalLogKind.AlbumTrackDownloaded : TerminalLogKind.SongAlreadyExists,
            ServerProtocol.JobStates.Skipped or ServerProtocol.JobStates.NotFoundLastTime => albumTrack ? TerminalLogKind.AlbumTrackSkipped : TerminalLogKind.SongSkipped,
            ServerProtocol.JobStates.Failed when song.FailureReason == ServerProtocol.FailureReasons.Cancelled => albumTrack ? TerminalLogKind.AlbumTrackSkipped : TerminalLogKind.JobCancelled,
            _ => albumTrack ? TerminalLogKind.AlbumTrackFailed : TerminalLogKind.SongFailed,
        };

    private static TerminalLogKind TerminalKind(JobSummaryDto summary)
        => summary.Kind switch
        {
            ServerJobKind.JobList when summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists
                => TerminalLogKind.PlaylistCompleted,
            ServerJobKind.Aggregate when summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists
                => TerminalLogKind.AggregateCompleted,
            _ when summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists
                => TerminalLogKind.JobSucceeded,
            _ when summary.FailureReason == ServerProtocol.FailureReasons.Cancelled
                => TerminalLogKind.JobCancelled,
            _ => TerminalLogKind.JobFailed,
        };

    private bool ShouldLogLiveSongTerminal(SongStateChangedEventDto song)
    {
        if (!IsJobListChild(song.JobId))
            return true;

        return song.State is not (
            ServerProtocol.JobStates.AlreadyExists
            or ServerProtocol.JobStates.Skipped
            or ServerProtocol.JobStates.NotFoundLastTime);
    }

    private void UpsertLiveAlbum(Guid albumId, AlbumBlock block)
    {
        if (_live == null) return;

        int done = AlbumDoneCount(block);
        int total = block.Songs.Count;
        _jobStatuses.TryGetValue(albumId, out var status);

        var children = block.Songs
            .Where(song => song.JobId.HasValue && !block.CompletedSongIds.ContainsKey(song.JobId.Value))
            .Select(song =>
            {
                var jobId = song.JobId!.Value;
                _bars.TryGetValue(jobId, out var data);
                return new JobChildView(
                    jobId.ToString(),
                    song.DisplayId ?? 0,
                    LiveAlbumChildState(data?.StateLabel ?? song.State?.ToString() ?? "pending"),
                    AlbumChildDisplay(block, data?.BaseText ?? song.ResolvedFilename ?? SongQueryText(song.Query)),
                    data?.Pct,
                    SpeedBytesPerSecond: data?.SpeedBps,
                    Metadata: data?.Metadata ?? SongPayloadMetadata(song),
                    IsMostRecent: block.LastUpdatedSongId == jobId);
            })
            .Where(child => child.State is not "Pending")
            .ToList();

        var albumName = block.Summary.QueryText ?? block.Summary.ItemName ?? "";
        UpsertLiveJob(block.Summary, status ?? "downloading", total > 0 ? done * 100 / total : null, done, total, children, AlbumDisplayName(albumName, block.RemoteFolderDisplay));
    }

    private static void RefreshOrPrintJobLineWithProfileSuffix(ProgressBar? progress, int current, JobSummaryDto summary, string text, bool print = false, bool refreshIfOffscreen = false)
    {
        var textWithSuffix = TextWithProfileSuffix(summary, text);
        Printing.RefreshOrPrint(progress, current, textWithSuffix, print, refreshIfOffscreen);
    }

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

    private static string LiveAlbumChildState(string state)
        => state switch
        {
            "InProgress" => "downloading",
            "Queued" => "queued",
            "Queued (R)" => "queued (r)",
            "Queued (L)" => "queued (l)",
            "Initialising" => "initialising",
            "Requested" => "requested",
            "Searching" => "searching",
            "Pending" => "pending",
            _ => state,
        };

    private static bool IsTerminalJobState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done
            or ServerProtocol.JobStates.AlreadyExists
            or ServerProtocol.JobStates.Failed
            or ServerProtocol.JobStates.Skipped;

    private static bool IsSuccessfulTerminalState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done
            or ServerProtocol.JobStates.AlreadyExists;

    private void RememberStructure(JobSummaryDto summary)
    {
        _jobKinds[summary.JobId] = summary.Kind;
        if (summary.ParentJobId is Guid parentJobId)
            _parentJobIds[summary.JobId] = parentJobId;
        else
            _parentJobIds.TryRemove(summary.JobId, out _);

        if (IsInlineChild(summary))
            _inlineChildJobs[summary.JobId] = 0;
        else
            _inlineChildJobs.TryRemove(summary.JobId, out _);
    }

    private bool IsInlineChild(Guid jobId)
        => _inlineChildJobs.ContainsKey(jobId);

    private bool IsJobListChild(Guid jobId)
        => _parentJobIds.TryGetValue(jobId, out var parentJobId)
            && _jobKinds.TryGetValue(parentJobId, out var parentKind)
            && parentKind == ServerJobKind.JobList;

    private bool IsInlineChild(JobSummaryDto summary)
        => summary.Kind == ServerJobKind.Song
            && summary.ParentJobId is Guid parentJobId
            && _jobKinds.TryGetValue(parentJobId, out var parentKind)
            && parentKind is ServerJobKind.Album;

    private static string SongDisplay(SongStateChangedEventDto song, bool shortPath = false)
    {
        var chosen = song.ChosenCandidate;
        if (chosen != null)
            return shortPath ? CandidateDisplayShort(chosen) : CandidateDisplay(chosen);
        if (!string.IsNullOrEmpty(song.DownloadPath))
            return $"{SongQueryText(song.Query)} at {song.DownloadPath}";
        return SongQueryText(song.Query);
    }

    private static string WithLocalPath(string display, string? localPath)
        => string.IsNullOrWhiteSpace(localPath) ? display : $"{display}\n    -> {localPath}";

    private static string WithFailureMessage(string display, string? failureMessage)
        => string.IsNullOrWhiteSpace(failureMessage) ? display : $"{display}\n    Error: {failureMessage}";

    private static string IndentContinuationLines(string text, string indent)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return string.Join('\n', normalized.Split('\n').Select(line => indent + line));
    }

    private static string SongCompletedDisplay(SongStateChangedEventDto song)
        => WithLocalPath(SongDisplay(song, shortPath: true), song.DownloadPath);

    private static string SongTerminalDisplay(SongStateChangedEventDto song)
        => song.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists
            ? SongCompletedDisplay(song)
            : WithFailureMessage(SongDisplay(song, shortPath: true), song.FailureMessage);


    private string AlbumTrackLogMessage(AlbumBlock block, SongStateChangedEventDto song)
    {
        var albumName = block.Summary.QueryText ?? block.Summary.ItemName ?? "";
        return $"{TerminalStatusLabel(song)}: {WithName(albumName, SongTerminalDisplay(song))}";
    }

    private static string CandidateDisplay(FileCandidateRefDto candidate)
        => $"{candidate.Username}\\..\\{candidate.Filename.Replace('/', '\\').TrimStart('\\')}";

    private static string CandidateDisplay(FileCandidateDto candidate)
        => CandidateDisplay(candidate.Ref);

    private static string CandidateDisplayLive(FileCandidateRefDto candidate)
        => $"{candidate.Username}\\{candidate.Filename.Replace('/', '\\').TrimStart('\\')}";

    private static string CandidateDisplayLive(FileCandidateDto candidate)
        => CandidateDisplayLive(candidate.Ref);

    private static TerminalFileMetadata CandidateMetadata(FileCandidateDto candidate)
        => new(
            candidate.Size,
            candidate.Length ?? AttributeValue(candidate.Attributes, "Length"),
            candidate.BitRate ?? AttributeValue(candidate.Attributes, "BitRate"),
            candidate.SampleRate ?? AttributeValue(candidate.Attributes, "SampleRate"),
            AttributeValue(candidate.Attributes, "BitDepth"));

    private static TerminalFileMetadata? SongPayloadMetadata(SongJobPayloadDto song)
    {
        if (song.ResolvedSize == null
            && song.ResolvedSampleRate == null
            && AttributeValue(song.ResolvedAttributes, "Length") == null
            && AttributeValue(song.ResolvedAttributes, "BitRate") == null
            && AttributeValue(song.ResolvedAttributes, "BitDepth") == null)
            return null;

        return new TerminalFileMetadata(
            song.ResolvedSize,
            AttributeValue(song.ResolvedAttributes, "Length"),
            AttributeValue(song.ResolvedAttributes, "BitRate"),
            song.ResolvedSampleRate ?? AttributeValue(song.ResolvedAttributes, "SampleRate"),
            AttributeValue(song.ResolvedAttributes, "BitDepth"));
    }

    private static int? AttributeValue(IReadOnlyList<FileAttributeDto>? attributes, string type)
        => attributes?
            .FirstOrDefault(attribute => string.Equals(attribute.Type, type, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static string ShortRemotePath(string username, string normalizedPath)
    {
        var parts = normalizedPath.Split('\\');
        bool truncated = parts.Length > 3;
        var displayed = truncated ? string.Join('\\', parts[^3..]) : normalizedPath;
        return truncated ? $"{username}\\..\\{displayed}" : $"{username}\\{displayed}";
    }

    private static string CandidateDisplayShort(FileCandidateRefDto candidate)
    {
        var filename = candidate.Filename.Replace('/', '\\').TrimStart('\\');
        return ShortRemotePath(candidate.Username, filename);
    }

    private static string CandidateDisplayShort(FileCandidateDto candidate)
        => CandidateDisplayShort(candidate.Ref);

    private static string? ResolvedSongDisplay(SongJobPayloadDto song, bool shortPath = false)
    {
        if (string.IsNullOrWhiteSpace(song.ResolvedFilename))
            return null;
        var filename = song.ResolvedFilename.Replace('/', '\\').TrimStart('\\');
        if (string.IsNullOrWhiteSpace(song.ResolvedUsername))
        {
            if (shortPath) { var p = filename.Split('\\'); filename = string.Join('\\', p[^Math.Min(3, p.Length)..]); }
            return filename;
        }
        return shortPath ? ShortRemotePath(song.ResolvedUsername, filename) : $"{song.ResolvedUsername}\\..\\{filename}";
    }

    private static string? ResolvedSongDisplayLive(SongJobPayloadDto song)
    {
        if (string.IsNullOrWhiteSpace(song.ResolvedFilename))
            return null;
        var filename = song.ResolvedFilename.Replace('/', '\\').TrimStart('\\');
        return string.IsNullOrWhiteSpace(song.ResolvedUsername)
            ? filename
            : $"{song.ResolvedUsername}\\{filename}";
    }

    private static string AlbumFolderDisplay(AlbumFolderDto folder, bool shortPath = false)
    {
        if (string.IsNullOrWhiteSpace(folder.FolderPath))
            return "";
        var path = folder.FolderPath.Replace('/', '\\').TrimStart('\\');
        return shortPath ? ShortRemotePath(folder.Ref.Username, path) : $"{folder.Ref.Username}\\..\\{path}";
    }

    private static string AlbumDisplayName(string albumName, string? folderDisplay)
    {
        if (string.IsNullOrWhiteSpace(folderDisplay))
            return albumName;
        if (string.IsNullOrWhiteSpace(albumName))
            return folderDisplay;

        return $"{albumName}: {folderDisplay}";
    }

    private static string AlbumChildDisplay(AlbumBlock block, string display)
    {
        if (string.IsNullOrWhiteSpace(block.RemoteFolderPrefix))
            return display;

        var folder = NormalizeSlskDisplayPath(block.RemoteFolderPrefix);
        var child = NormalizeSlskDisplayPath(display);
        var prefix = folder.EndsWith('\\') ? folder : folder + "\\";

        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? child[prefix.Length..]
            : display;
    }

    private static string NormalizeSlskDisplayPath(string path)
        => path.Replace('/', '\\').TrimStart('\\');

    private static string SongQueryText(SongQueryDto query)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(query.Artist);
        bool hasTitle  = !string.IsNullOrWhiteSpace(query.Title);
        if (hasArtist && hasTitle) return $"{query.Artist} - {query.Title}";
        if (hasArtist) return query.Artist!;
        if (hasTitle)  return query.Title!;
        return query.Album ?? query.Uri ?? "";
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

    private void WritePlainJobStatus(JobSummaryDto summary, string status, string? detail = null)
    {
        _jobStatuses[summary.JobId] = status;
        var line = JobStatusLine(summary, status, detail);
        if (_plainJobStatusLines.TryGetValue(summary.JobId, out var previous) && previous == line)
            return;

        _plainJobStatusLines[summary.JobId] = line;
        RefreshOrPrintJobLineWithProfileSuffix(null, 0, summary, line, print: true);
    }

    private void WritePlainSongStatus(
        Guid jobId,
        int displayId,
        SongQueryDto query,
        string status,
        string? detail = null)
    {
        var name = SongQueryText(query);
        var line = $"[{displayId}] SongJob: {status}: {WithName(name, detail ?? name)}";
        if (_plainJobStatusLines.TryGetValue(jobId, out var previous) && previous == line)
            return;

        _jobStatuses[jobId] = status;
        _plainJobStatusLines[jobId] = line;
        Logger.Info(line);
    }

    private void UpdateLastUpdated(Guid songJobId)
    {
        if (_songToAlbum.TryGetValue(songJobId, out var albumId) && _albumBlocks.TryGetValue(albumId, out var block))
            block.LastUpdatedSongId = songJobId;
    }

    private static void UpdateSpeed(BarData d, long bytesTransferred)
    {
        long now = DateTimeOffset.UtcNow.UtcTicks;
        if (d.LastSpeedTicks == 0)
        {
            d.LastBytes = bytesTransferred;
            d.LastSpeedTicks = now;
            return;
        }

        long elapsed = now - d.LastSpeedTicks;
        if (elapsed < TimeSpan.TicksPerMillisecond * 500)
            return;

        long instantBps = (bytesTransferred - d.LastBytes) * TimeSpan.TicksPerSecond / elapsed;
        d.SpeedBps = d.SpeedBps is long prev
            ? (long)(0.4 * instantBps + 0.6 * prev)
            : instantBps;
        d.LastBytes = bytesTransferred;
        d.LastSpeedTicks = now;
    }


    // ── event handlers ───────────────────────────────────────────────────

    private void ReportDownloadStart(DownloadStartedEventDto song)
    {
        if (PlainMode)
        {
            WritePlainSongStatus(song.JobId, song.DisplayId, song.Query, "downloading", CandidateDisplayShort(song.Candidate));
            return;
        }

        if (LiveMode)
        {
            if (IsInlineChild(song.JobId))
            {
                if (TryRegisterAlbumChild(song.JobId, song.DisplayId, song.Query, song.Candidate, out var albumId, out var block, out var childData))
                {
                    childData.StateLabel = "queued";
                    childData.BaseText = AlbumChildDisplay(block, CandidateDisplayLive(song.Candidate));
                    childData.Pct = 0;
                    childData.Metadata = CandidateMetadata(song.Candidate);
                    UpdateLastUpdated(song.JobId);
                    UpsertLiveAlbum(albumId, block);
                }
                return;
            }

            var songName = WithName(SongQueryText(song.Query), CandidateDisplayLive(song.Candidate));
            var metadata = CandidateMetadata(song.Candidate);
            _liveSongInfo[song.JobId] = (song.DisplayId, songName, metadata);
            _bars[song.JobId] = new BarData { StateLabel = "queued", Metadata = metadata };
            UpsertLiveSong(song.JobId, song.DisplayId, songName, "queued", 0, metadata: metadata);
            return;
        }

        if (IsInlineChild(song.JobId) && !_bars.ContainsKey(song.JobId))
        {
            if (!TryRegisterAlbumChild(song.JobId, song.DisplayId, song.Query, song.Candidate, out _, out _, out var childData))
                return;
            childData.Bar ??= Printing.GetProgressBar();
        }

        var d = _bars.GetOrAdd(song.JobId, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = IsInlineChild(song.JobId) ? null : $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Queued";
        d.BaseText = CandidateDisplayShort(song.Candidate);
        UpdateLastUpdated(song.JobId);
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: !IsInlineChild(song.JobId) && d.Bar != null);
    }

    private void ReportDownloadProgress(DownloadProgressEventDto progress)
    {
        if (PlainMode) return;

        if (LiveMode)
        {
            int pct = progress.TotalBytes > 0 ? (int)(progress.BytesTransferred * 100 / progress.TotalBytes) : 0;
            if (_songToAlbum.TryGetValue(progress.JobId, out var albumId) && _albumBlocks.TryGetValue(albumId, out var block))
            {
                if (_bars.TryGetValue(progress.JobId, out var childData))
                {
                    childData.Pct = pct;
                    UpdateSpeed(childData, progress.BytesTransferred);
                }
                UpdateLastUpdated(progress.JobId);
                UpsertLiveAlbum(albumId, block);
            }
            else if (!IsInlineChild(progress.JobId) && _liveSongInfo.TryGetValue(progress.JobId, out var info))
            {
                _bars.TryGetValue(progress.JobId, out var songData);
                if (songData != null) UpdateSpeed(songData, progress.BytesTransferred);
                UpsertLiveSong(progress.JobId, info.DisplayId, info.Name, "downloading", pct, songData?.SpeedBps, info.Metadata);
            }
            return;
        }

        if (!_bars.TryGetValue(progress.JobId, out var d)) return;
        d.Pct = progress.TotalBytes > 0 ? (int)(progress.BytesTransferred * 100 / progress.TotalBytes) : 0;
        UpdateLastUpdated(progress.JobId);
    }

    private void ReportDownloadAttemptFailed(DownloadAttemptFailedEventDto failure)
    {
        var candidate = CandidateDisplayShort(failure.Candidate);
        var message =
            $"download error: {WithName(SongQueryText(failure.Query), candidate)}\n" +
            $"    Output: {failure.OutputPath}\n" +
            $"    Attempt: {failure.Attempt}/{failure.MaxAttempts}\n" +
            $"    {failure.ExceptionType}: {failure.ExceptionMessage}\n" +
            IndentContinuationLines(failure.Exception, "    ");

        if (PlainMode)
        {
            Logger.Error($"[{failure.DisplayId}] SongJob: {message}");
            return;
        }

        if (LiveMode)
        {
            LogLiveSong(TerminalLogKind.JobFailed, failure.JobId, failure.DisplayId, message);
            return;
        }

        Logger.LogNonConsole(Logger.LogLevel.Error, $"[{failure.DisplayId}] SongJob: {message}");
    }

    private void ReportStateChanged(SongStateChangedEventDto song)
    {
        bool songSucceeded = song.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;

        if (PlainMode)
        {
            WritePlainSongStatus(song.JobId, song.DisplayId, song.Query, TerminalStatusLabel(song), SongTerminalDisplay(song));
            return;
        }

        if (LiveMode)
        {
            MarkAlbumTrackCompleted(song.JobId);
            var candidate = song.ChosenCandidate;
            if ((TryGetAlbumParent(song.JobId, out var albumId) && _albumBlocks.TryGetValue(albumId, out var block))
                || TryRegisterAlbumChild(song.JobId, song.DisplayId, song.Query, candidate, out albumId, out block, out _))
            {
                block.CompletedSongIds.TryAdd(song.JobId, 0);
                UpdateLastUpdated(song.JobId);
                UpsertLiveAlbum(albumId, block);
                LogLiveAlbumTrack(TerminalKind(song, albumTrack: true),
                    block.Summary,
                    AlbumTrackLogMessage(block, song));
            }
            else
            {
                RemoveLiveJob(song.JobId);
                _liveSongInfo.TryRemove(song.JobId, out _);
                if (ShouldLogLiveSongTerminal(song))
                {
                    var songDisplay = SongTerminalDisplay(song);
                    LogLiveSong(TerminalKind(song), song.JobId, song.DisplayId, $"{TerminalStatusLabel(song)}: {WithName(SongQueryText(song.Query), songDisplay)}");
                }
            }
            _bars.TryRemove(song.JobId, out _);
            _savedState.TryRemove(song.JobId, out _);
            return;
        }

        if (!IsInlineChild(song.JobId))
            Logger.LogNonConsole(Logger.LogLevel.Info, $"[{song.DisplayId}] SongJob: {TerminalStatusLabel(song)}: {SongTerminalDisplay(song)}");

        if (_bars.TryGetValue(song.JobId, out var d))
        {
            UpdateLastUpdated(song.JobId);
            d.StateLabel = song.State switch
            {
                ServerProtocol.JobStates.Done => "Succeeded",
                ServerProtocol.JobStates.AlreadyExists => "Already exists",
                _ => "Failed",
            };
            if (songSucceeded)
            {
                d.Pct = 100;
                var display = SongDisplay(song, shortPath: true);
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
            MarkAlbumTrackCompleted(song.JobId);
            if (d.Bar != null)
            {
                Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
            }
        }
        _bars.TryRemove(song.JobId, out _);
        _savedState.TryRemove(song.JobId, out _);
    }


    // ── display event handlers ───────────────────────────────────────────

    private void ReportExtractionStarted(ExtractionStartedEventDto job)
    {
        if (LiveMode)
        {
            UpsertLiveJob(job.Summary, "extracting");
            return;
        }

        if (!string.IsNullOrWhiteSpace(job.InputType))
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine();
                RefreshOrPrintJobLineWithProfileSuffix(
                    null, 0, job.Summary,
                    $"[{job.Summary.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}",
                    print: true);
            }
        }
    }

    private void ReportExtractionFailed(ExtractionFailedEventDto job)
    {
        if (LiveMode)
        {
            RemoveLiveJob(job.Summary.JobId);
            LogLive(TerminalLogKind.JobFailed, job.Summary, $"failed: {job.Reason}");
            return;
        }

        Logger.Error($"[{job.Summary.DisplayId}] ExtractJob: Failed: {job.Summary.QueryText}\n  Reason:    {job.Reason}");
        _jobBars.TryRemove(job.Summary.JobId, out _);
    }

    private void ReportJobStarted(JobStartedEventDto job)
    {
        RememberStructure(job.Summary);
        if (IsInlineChild(job.Summary))
            return;

        string status = job.Summary.Kind == ServerJobKind.RetrieveFolder ? "retrieving folder" : "searching";

        if (PlainMode)
        {
            WritePlainJobStatus(job.Summary, status);
            return;
        }

        if (job.Summary.Kind == ServerJobKind.Song)
            return;

        if (LiveMode)
        {
            UpsertLiveJob(job.Summary, status);
            return;
        }

        var bar = Printing.GetProgressBar();
        _jobBars[job.Summary.JobId] = bar;
        _jobStatuses[job.Summary.JobId] = status;
        RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job.Summary, JobStatusLine(job.Summary, status), print: true);
    }

    private void ReportJobFolderRetrieving(JobFolderRetrievingEventDto job)
    {
        if (PlainMode)
        {
            RefreshOrPrintJobLineWithProfileSuffix(null, 0, job.Summary, JobStatusLine(job.Summary, "retrieving folder"), print: true);
            return;
        }

        if (LiveMode)
        {
            UpsertLiveJob(job.Summary, "retrieving folder");
            return;
        }

        _jobBars.TryGetValue(job.Summary.JobId, out var bar);
        Printing.RefreshOrPrint(bar, 0, "Getting all files in folder..", print: true);
    }

    private void ReportSongSearching(SongSearchingEventDto song)
    {
        if (PlainMode)
        {
            WritePlainSongStatus(song.JobId, song.DisplayId, song.Query, "searching");
            return;
        }

        if (LiveMode)
        {
            var name = SongQueryText(song.Query);
            if (TryRegisterAlbumChild(song.JobId, song.DisplayId, song.Query, candidate: null, out var albumId, out var block, out var data))
            {
                data.StateLabel = "searching";
                data.BaseText = name;
                UpdateLastUpdated(song.JobId);
                UpsertLiveAlbum(albumId, block);
            }
            else if (!IsInlineChild(song.JobId))
            {
                _liveSongInfo[song.JobId] = (song.DisplayId, name, null);
                UpsertLiveSong(song.JobId, song.DisplayId, name, "searching");
            }
            return;
        }

        if (_bars.TryGetValue(song.JobId, out var existing))
        {
            existing.StateLabel = "Searching";
            existing.JobPrefix = IsInlineChild(song.JobId) ? null : $"[{song.DisplayId}] SongJob: ";
            existing.BaseText = $"{song.Query.Artist} - {song.Query.Title}";
            Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
            return;
        }

        if (IsInlineChild(song.JobId))
            return;

        bool isFirst = !_bars.ContainsKey(song.JobId);
        var d = _bars.GetOrAdd(song.JobId, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Searching";
        d.BaseText = $"{song.Query.Artist} - {song.Query.Title}";
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: isFirst);
    }

    private void ReportDownloadStateChanged(DownloadStateChangedEventDto song)
    {
        if (PlainMode) return;

        if (LiveMode)
        {
            string stateLabel = GetStateLabel(Enum.TryParse<TransferStates>(song.State, out var transferState) ? transferState : TransferStates.None);
            if (_bars.TryGetValue(song.JobId, out var data))
                data.StateLabel = stateLabel;
            if (_songToAlbum.TryGetValue(song.JobId, out var albumId) && _albumBlocks.TryGetValue(albumId, out var block))
            {
                UpdateLastUpdated(song.JobId);
                UpsertLiveAlbum(albumId, block);
            }
            else if (_liveSongInfo.TryGetValue(song.JobId, out var info))
                UpsertLiveSong(song.JobId, info.DisplayId, info.Name, stateLabel.ToLowerInvariant(), metadata: info.Metadata);
            return;
        }

        if (!_bars.TryGetValue(song.JobId, out var d)) return;
        d.StateLabel = GetStateLabel(Enum.TryParse<TransferStates>(song.State, out var state) ? state : TransferStates.None);
        Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
    }

    private void ReportOnCompleteStart(OnCompleteStartedEventDto song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete start: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
            return;
        }

        if (LiveMode)
        {
            var name = SongQueryText(song.Query);
            _liveSongInfo[song.JobId] = (song.DisplayId, name, null);
            UpsertLiveSong(song.JobId, song.DisplayId, name, "on-complete");
            return;
        }

        if (!_bars.TryGetValue(song.JobId, out var d) || d.Bar == null) return;
        _savedState[song.JobId] = (d.Bar.Line1 ?? "", d.Bar.Current);
        Printing.RefreshOrPrint(d.Bar, d.Bar.Current, "  OnComplete:  " + $"[{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
    }

    private void ReportOnCompleteEnd(OnCompleteEndedEventDto song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete end: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
            return;
        }

        if (LiveMode)
        {
            if (_liveSongInfo.TryGetValue(song.JobId, out var info))
                UpsertLiveSong(song.JobId, info.DisplayId, info.Name, "downloading", metadata: info.Metadata);
            return;
        }

        if (!_bars.TryGetValue(song.JobId, out var d) || d.Bar == null) return;
        if (_savedState.TryGetValue(song.JobId, out var saved))
            Printing.RefreshOrPrint(d.Bar, saved.pos, saved.text);
    }

    private void ReportJobStatus(JobStatusEventDto job)
    {
        RememberStructure(job.Summary);
        if (IsInlineChild(job.Summary))
            return;

        if (PlainMode)
        {
            WritePlainJobStatus(job.Summary, job.Status);
            return;
        }

        if (LiveMode)
        {
            _jobStatuses[job.Summary.JobId] = job.Status;
            if (job.Summary.Kind == ServerJobKind.Album && _albumBlocks.TryGetValue(job.Summary.JobId, out var block))
                UpsertLiveAlbum(job.Summary.JobId, block);
            else
                UpsertLiveJob(job.Summary, job.Status);
            return;
        }

        _jobStatuses[job.Summary.JobId] = job.Status;
        if (_jobBars.TryGetValue(job.Summary.JobId, out var bar) && bar != null)
        {
            if (job.Summary.Kind == ServerJobKind.Album && _albumBlocks.TryGetValue(job.Summary.JobId, out var block))
            {
                int total = block.Songs.Count;
                int done = AlbumDoneCount(block);
                try { bar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job.Summary, done, total, job.Status)); } catch { }
            }
            else
            {
                Printing.RefreshOrPrint(bar, 0, JobStatusLine(job.Summary, job.Status), print: false);
            }
        }
    }

    private void ReportAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        if (PlainMode)
        {
            _jobStatuses[job.Summary.JobId] = "downloading";
            RefreshOrPrintJobLineWithProfileSuffix(null, 0, job.Summary, JobStatusLine(job.Summary, "downloading"), print: true);
            return;
        }

        if (Console.IsOutputRedirected)
        {
            Printing.WriteLine();
            return;
        }

        if (LiveMode)
        {
            _jobStatuses[job.Summary.JobId] = "downloading";
            InitializeAlbumBlock(job.Summary, job.Tracks, AlbumFolderDisplay(job.Folder, shortPath: true), AlbumFolderDisplay(job.Folder));
            UpsertLiveAlbum(job.Summary.JobId, _albumBlocks[job.Summary.JobId]);
            return;
        }

        int total = job.Tracks?.Count ?? job.Folder.Files?.Count ?? 0;
        lock (Printing.ConsoleLock)
        {
            if (_albumBlocks.TryGetValue(job.Summary.JobId, out var oldBlock))
            {
                _albumBlocks.TryRemove(job.Summary.JobId, out _);
                _jobBars.TryRemove(job.Summary.JobId, out _);
                foreach (var s in oldBlock.Songs)
                {
                    if (s.JobId.HasValue)
                    {
                        _songToAlbum.TryRemove(s.JobId.Value, out _);
                        _bars.TryRemove(s.JobId.Value, out _);
                    }
                }
            }

            var headerBar = _jobBars.GetOrAdd(job.Summary.JobId, _ => Printing.GetProgressBar());
            _jobStatuses[job.Summary.JobId] = "downloading";
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job.Summary, AlbumHeaderText(job.Summary, 0, total, "downloading"), print: true); } catch { }

            Printing.PrintAlbumHeader(ToAlbumFolder(job.Folder));
            InitializeAlbumBlock(job.Summary, job.Tracks, AlbumFolderDisplay(job.Folder, shortPath: true), AlbumFolderDisplay(job.Folder));
        }
    }

    private void ReportAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        if (_albumBlocks.ContainsKey(job.Summary.JobId))
            return;

        string folderName = string.IsNullOrWhiteSpace(job.Folder.FolderPath)
            ? job.Summary.QueryText ?? ""
            : job.Folder.FolderPath;

        if (PlainMode)
        {
            RefreshOrPrintJobLineWithProfileSuffix(
                null, 0, job.Summary,
                $"[{job.Summary.DisplayId}] AlbumJob: downloading tracks: {job.Summary.QueryText} - {folderName}",
                print: true);
            return;
        }

        if (LiveMode)
        {
            _jobStatuses[job.Summary.JobId] = "downloading tracks";
            InitializeAlbumBlock(job.Summary, job.Tracks, AlbumFolderDisplay(job.Folder, shortPath: true), AlbumFolderDisplay(job.Folder));
            UpsertLiveAlbum(job.Summary.JobId, _albumBlocks[job.Summary.JobId]);
            return;
        }

        _jobStatuses[job.Summary.JobId] = "downloading tracks";
        InitializeAlbumBlock(job.Summary, job.Tracks, AlbumFolderDisplay(job.Folder, shortPath: true), AlbumFolderDisplay(job.Folder));

        if (_jobBars.TryGetValue(job.Summary.JobId, out var headerBar) && headerBar != null)
        {
            int total = job.Tracks?.Count ?? 0;
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job.Summary, AlbumHeaderText(job.Summary, 0, total, "downloading tracks"), print: true); } catch { }
        }
        else
        {
            RefreshOrPrintJobLineWithProfileSuffix(null, 0, job.Summary, JobStatusLine(job.Summary, "downloading tracks"), print: true);
        }

        Printing.WriteLine($"Folder: {folderName}", ConsoleColor.DarkGray);
    }

    private void ReportAlbumDownloadCompleted(AlbumDownloadCompletedEventDto job)
    {
        if (PlainMode)
        {
            _jobStatuses.TryRemove(job.Summary.JobId, out _);
            return;
        }

        if (LiveMode)
        {
            string? remoteFolderDisplay = null;
            if (_albumBlocks.TryRemove(job.Summary.JobId, out var liveBlock))
            {
                remoteFolderDisplay = liveBlock.RemoteFolderDisplay;
                foreach (var s in liveBlock.Songs)
                    if (s.JobId.HasValue)
                    {
                        _songToAlbum.TryRemove(s.JobId.Value, out _);
                    }
            }
            RemoveLiveJob(job.Summary.JobId);
            _jobStatuses.TryRemove(job.Summary.JobId, out _);
            LogLive(job.Summary.State == ServerProtocol.JobStates.Done ? TerminalLogKind.JobSucceeded : TerminalLogKind.JobFailed,
                job.Summary,
                AlbumCompletedLogMessage(job.Summary, remoteFolderDisplay, job.DownloadPath));
            return;
        }

        if (_albumBlocks.TryRemove(job.Summary.JobId, out var block))
        {
            foreach (var s in block.Songs)
                if (s.JobId.HasValue) _songToAlbum.TryRemove(s.JobId.Value, out _);

            CompleteAlbumBlock(job.Summary.JobId, block, job.Summary);
        }
        _jobBars.TryRemove(job.Summary.JobId, out _);
        _jobStatuses.TryRemove(job.Summary.JobId, out _);

        if (!Console.IsOutputRedirected && !_cli.NoProgress)
            Printing.WriteLine();
    }

    private void CompleteAlbumBlock(Guid albumJobId, AlbumBlock block, JobSummaryDto summary)
    {
        CompleteRemainingAlbumBars(block, summary);
        int total = block.Songs.Count;
        if (_jobBars.TryGetValue(albumJobId, out var headerBar) && headerBar != null)
        {
            _jobStatuses.TryGetValue(albumJobId, out var status);
            try { headerBar.Refresh(100, AlbumHeaderText(summary, total, total, status)); } catch { }
        }
        _jobBars.TryRemove(albumJobId, out _);
        _jobStatuses.TryRemove(albumJobId, out _);
    }

    private string AlbumCompletedLogMessage(JobSummaryDto summary, string? remoteFolderDisplay, string? completedPath)
    {
        var albumName = summary.QueryText ?? summary.ItemName ?? "";
        var status = TerminalStatusLabel(summary.State, summary.FailureReason);
        bool succeeded = summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;

        if (succeeded && !string.IsNullOrWhiteSpace(remoteFolderDisplay))
            return $"{status}: {WithName(albumName, WithLocalPath(remoteFolderDisplay, completedPath))}";

        if (!string.IsNullOrWhiteSpace(completedPath))
            return $"{status}: {WithName(albumName, $"completed at {completedPath}")}";

        return $"{status}: {albumName}";
    }


    private void InitializeAlbumBlock(
        JobSummaryDto summary,
        IReadOnlyList<SongJobPayloadDto>? tracks,
        string? remoteFolderDisplay = null,
        string? remoteFolderPrefix = null)
    {
        var block = new AlbumBlock
        {
            Summary = summary,
            Songs = tracks?.ToList() ?? [],
            RemoteFolderDisplay = string.IsNullOrWhiteSpace(remoteFolderDisplay) ? null : remoteFolderDisplay,
            RemoteFolderPrefix = string.IsNullOrWhiteSpace(remoteFolderPrefix) ? null : remoteFolderPrefix,
        };

        foreach (var song in block.Songs.Where(s => s.JobId.HasValue))
        {
            var songJobId = song.JobId!.Value;
            _songToAlbum[songJobId] = summary.JobId;
            string filename = song.ResolvedFilename ?? $"{song.Query.Artist} - {song.Query.Title}";
            string shortName = LiveMode
                ? AlbumChildDisplay(block, ResolvedSongDisplayLive(song) ?? filename)
                : System.IO.Path.GetFileName(filename);
            var bar = LiveMode ? null : Printing.GetProgressBar();
            var data = ToBarData(song, bar, shortName);
            _bars[songJobId] = data;
            if (bar != null)
                try { bar.Refresh(data.Pct, BuildText(data)); } catch { }
        }

        _albumBlocks[summary.JobId] = block;
    }

    private void CompleteRemainingAlbumBars(AlbumBlock block, JobSummaryDto summary)
    {
        foreach (var song in block.Songs.Where(song => song.JobId.HasValue))
        {
            block.CompletedSongIds.TryAdd(song.JobId!.Value, 0);

            if (!_bars.TryGetValue(song.JobId!.Value, out var data))
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
            _bars.TryRemove(song.JobId.Value, out _);
            _savedState.TryRemove(song.JobId.Value, out _);
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
}
