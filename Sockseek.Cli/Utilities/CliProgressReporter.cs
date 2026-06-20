using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Soulseek;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;

namespace Sockseek.Cli;

public class CliProgressReporter
{
    private readonly CliSettings _cli;
    private readonly TerminalLiveRenderer? _live;

    private readonly ConcurrentDictionary<Guid, BarData> _bars = new();
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

    private bool PlainMode => _cli.NoProgress || !LiveMode;

    sealed class BarData
    {
        public string       BaseText   = "";
        public string       StateLabel = "";
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
        public readonly object SongsLock = new();
        public ConcurrentDictionary<Guid, byte> CompletedSongIds = new();
        public Guid?         LastUpdatedSongId;
        public int           LastDone = -1;
        public int           LastTotal = -1;
        public string?       RemoteFolderDisplay;
        public string?       RemoteFolderPrefix;
    }

    private bool _isPaused;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            if (_live != null)
                _live.IsPaused = value;
            else
                Printing.SetBuffering(value);
        }
    }

    private bool LiveMode => _live != null;

    public bool UsesLiveRendering => LiveMode;

    public CliProgressReporter(CliSettings cli)
    {
        _cli = cli;
        if (!cli.NoProgress && !Console.IsOutputRedirected)
            _live = new TerminalLiveRenderer();
    }

    public void Stop()
    {
        _live?.Dispose();
    }

    public void AttachLogSink(LogLevel minimumLevel)
    {
        if (_live == null)
            return;

        SockseekLog.AddStructuredConsoleSink(WriteLiveLog, minimumLevel);
    }

    private void WriteLiveLog(SockseekLog.StructuredLogEntry entry, string message)
    {
        if (_live == null)
            return;

        if (entry.Context is TerminalLogLine line)
        {
            if (!line.ShowInLive)
                return;

            _live.Log(line);
            return;
        }

        _live.Log(entry);
    }

    public void ReportSyntheticJobFailure(int displayId, string jobType, string name, string failureReason)
    {
        string msg = $"failed [{failureReason}]: {name}";
        SockseekLog.Jobs.Info($"[{displayId:000}] {jobType}: {msg}");
    }

    public void ReportClientError(string message)
    {
        SockseekLog.Error(message);
    }

    internal void Attach(ICliBackend backend)
    {
        backend.WorkflowUpdated += update =>
        {
            if (LiveMode)
                ReportWorkflowUpdate(update);
        };

        backend.EventReceived += envelope =>
        {
            if (LiveMode && envelope.WorkflowId.HasValue)
                return;

            HandleEvent(envelope);
        };
    }

    private void ReportWorkflowUpdate(WorkflowClientUpdate update)
    {
        if (update.IsStale)
            return;

        foreach (var summary in update.JobUpserts)
            ReportJobUpserted(summary);

        foreach (var envelope in update.Activity)
            HandleEvent(envelope);

        foreach (var progress in update.Progress)
            ReportDownloadProgress(progress);
    }

    private void HandleEvent(ServerEventEnvelopeDto envelope)
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
            case "job.activity-changed" when envelope.Payload is JobActivityChangedEventDto e:
                ReportJobActivityChanged(e);
                break;
            case "job.message" when envelope.Payload is JobMessageEventDto e:
                ReportJobMessage(e);
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
            case "album.state-changed" when envelope.Payload is AlbumStateChangedEventDto e:
                ReportAlbumStateChanged(e);
                break;
            case "track-batch.resolved" when envelope.Payload is TrackBatchResolvedEventDto e:
                ReportTrackBatchResolved(e);
                break;
            case "search.rate-limited" when envelope.Payload is SearchRateLimitedEventDto rl:
                ReportSearchRateLimited(rl);
                break;
            case "search.resumed":
                _live?.SetRateLimited(null);
                break;
        }
    }

    private void ReportJobUpserted(JobSummaryDto summary)
    {
        RememberStructure(summary);
        var status = CliJobStatusPresenter.ForSummary(summary);
        if (!IsInfrastructureJobKind(summary.Kind))
            _live?.UpsertJob(new TerminalJobRecord(
                summary.JobId.ToString(),
                summary.DisplayId,
                GetJobTypeLabel(summary.Kind),
                status.Label,
                status.Category,
                summary.ParentJobId?.ToString()));

        if (status.IsTerminal)
        {
            if (summary.Kind == ServerJobKind.Album && status.IsSuccessful)
                return;

            RemoveLiveJob(summary.JobId);
            if (summary.Kind == ServerJobKind.Album && _albumBlocks.TryRemove(summary.JobId, out var liveBlock))
            {
                foreach (var song in liveBlock.Songs)
                    if (song.JobId is Guid songJobId)
                    {
                        _songToAlbum.TryRemove(songJobId, out _);
                        _bars.TryRemove(songJobId, out _);
                    }
            }

            if (summary.Kind != ServerJobKind.Song
                && summary.Kind != ServerJobKind.Extract
                && _liveTerminalParentLogs.TryAdd(summary.JobId, 0))
            {
                // Activity logging is handled by EventLogger/SockseekLog. This branch only
                // records that the live terminal line was accounted for.
            }

            return;
        }

        if (IsContainerJobKind(summary.Kind))
        {
            if (IsTransparentContainer(summary.JobId, summary.Kind))
                return;

            _jobStatuses.TryGetValue(summary.JobId, out var containerStatus);
            if (LiveMode)
                UpsertLiveJob(summary, containerStatus ?? status.Label);
            return;
        }

        if (PlainMode)
        {
            return;
        }

        if (ShouldShowStandaloneSummaryInLiveTable(summary, status) && !IsInlineChild(summary))
            UpsertLiveJob(summary, status.Label);
    }

    // ── album/track helpers ─────────────────────────────────────────────

    private int AlbumDoneCount(AlbumBlock block)
    {
        int done = 0;
        foreach (var song in AlbumSongsSnapshot(block))
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

    private static List<SongJobPayloadDto> AlbumSongsSnapshot(AlbumBlock block)
    {
        lock (block.SongsLock)
            return block.Songs.ToList();
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

        lock (block.SongsLock)
        {
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
                    LifecycleState: ServerJobLifecycleState.Pending,
                    ActivityPhase: ServerJobActivityPhase.None,
                    TerminalOutcome: ServerJobTerminalOutcome.None,
                    SkipReason: ServerJobSkipReason.None));
            }
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

    internal static bool ShouldShowStandaloneSummaryInLiveTable(JobSummaryDto summary, CliJobStatus status)
        => !status.IsQueued
            && !status.IsTerminal
            && !IsInfrastructureJobKind(summary.Kind);

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
        string line = $"[{summary.DisplayId}] {GetJobTypePrefix(summary.Kind)}{status}: {WithName(name, d)}";
        
        if ((summary.TerminalOutcome == ServerJobTerminalOutcome.Succeeded
                || summary.TerminalOutcome == ServerJobTerminalOutcome.Skipped && summary.SkipReason == ServerJobSkipReason.AlreadyExists)
            && summary.Kind == ServerJobKind.Search
            && summary.DiscoveryResultCount.HasValue)
            line += $": Found {summary.DiscoveryResultCount.Value} files";
            
        return line;
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

    private void UpsertLiveAlbum(Guid albumId, AlbumBlock block)
    {
        if (_live == null) return;

        int done = AlbumDoneCount(block);
        var songs = AlbumSongsSnapshot(block);
        int total = songs.Count;
        _jobStatuses.TryGetValue(albumId, out var status);

        var children = songs
            .Where(song => song.JobId.HasValue && !block.CompletedSongIds.ContainsKey(song.JobId.Value))
            .Select(song =>
            {
                var jobId = song.JobId!.Value;
                _bars.TryGetValue(jobId, out var data);
                return new JobChildView(
                    jobId.ToString(),
                    song.DisplayId ?? 0,
                    LiveAlbumChildState(data?.StateLabel ?? CliJobStatusPresenter.ForSongPayload(song).Label),
                    AlbumChildDisplay(block, data?.BaseText ?? song.ResolvedFilename ?? SongQueryText(song.Query)),
                    data?.Pct,
                    SpeedBytesPerSecond: data?.SpeedBps,
                    Metadata: data?.Metadata ?? SongPayloadMetadata(song),
                    IsMostRecent: block.LastUpdatedSongId == jobId);
            })
            .Where(child => !string.Equals(child.State, "queued", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var albumName = block.Summary.QueryText ?? block.Summary.ItemName ?? "";
        UpsertLiveJob(block.Summary, status ?? "downloading", total > 0 ? done * 100 / total : null, done, total, children, AlbumDisplayName(albumName, block.RemoteFolderDisplay));
    }

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
        => song.TerminalOutcome == ServerJobTerminalOutcome.Succeeded
            || (song.TerminalOutcome == ServerJobTerminalOutcome.Skipped && song.SkipReason == ServerJobSkipReason.AlreadyExists)
            ? SongCompletedDisplay(song)
            : WithFailureMessage(SongDisplay(song, shortPath: true), song.FailureMessage);

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

    private static string TerminalStatusLabel(JobSummaryDto summary)
    {
        var status = CliJobStatusPresenter.ForSummary(summary);
        return status.Label;
    }

    private static string TerminalStatusLabel(SongStateChangedEventDto song)
    {
        var status = CliJobStatusPresenter.ForSongEvent(song);
        if (song.TerminalOutcome == ServerJobTerminalOutcome.Succeeded
            || (song.TerminalOutcome == ServerJobTerminalOutcome.Skipped && song.SkipReason == ServerJobSkipReason.AlreadyExists)
            || song.TerminalOutcome == ServerJobTerminalOutcome.Cancelled
            || song.TerminalOutcome == ServerJobTerminalOutcome.PartialSuccess)
            return status.Label;

        var reason = ServerFailureReasonDisplay.Label(song.FailureReason);
        if (reason.Length == 0 && (song.TerminalOutcome is ServerJobTerminalOutcome.Failed
                or ServerJobTerminalOutcome.Skipped))
            reason = ServerFailureReasonDisplay.LabelOrUnknown(song.FailureReason);

        var discovery = song.DiscoveryLockedFileCount > 0
            ? $" (Found {song.DiscoveryLockedFileCount} locked files)"
            : "";
        return reason.Length > 0 ? $"failed [{reason}]{discovery}" : $"failed{discovery}";
    }

    private void WritePlainSongStatus(
        Guid jobId,
        int displayId,
        SongQueryDto query,
        string status,
        string? detail = null)
    {
        _jobStatuses[jobId] = status;
        _plainJobStatusLines[jobId] = $"[{displayId}] SongJob: {status}: {WithName(SongQueryText(query), detail ?? SongQueryText(query))}";
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
        }

        if (IsInlineChild(song.JobId))
        {
            if (TryRegisterAlbumChild(song.JobId, song.DisplayId, song.Query, song.Candidate, out var albumId, out var block, out var childData))
            {
                childData.StateLabel = "queued";
                childData.BaseText = AlbumChildDisplay(block, CandidateDisplayLive(song.Candidate));
                childData.Pct = 0;
                childData.Metadata = CandidateMetadata(song.Candidate);
                UpdateLastUpdated(song.JobId);
                if (LiveMode)
                    UpsertLiveAlbum(albumId, block);
            }
            return;
        }

        var songName = WithName(SongQueryText(song.Query), CandidateDisplayLive(song.Candidate));
        var metadata = CandidateMetadata(song.Candidate);
        _liveSongInfo[song.JobId] = (song.DisplayId, songName, metadata);
        _bars[song.JobId] = new BarData { StateLabel = "queued", BaseText = songName, Metadata = metadata };

        if (PlainMode)
        {
            return;
        }

        UpsertLiveSong(song.JobId, song.DisplayId, songName, "queued", 0, metadata: metadata);
    }

    private void ReportDownloadProgress(DownloadProgressEventDto progress)
    {
        if (PlainMode) return;

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
    }

    private void ReportDownloadAttemptFailed(DownloadAttemptFailedEventDto failure)
    {
        var candidate = CandidateDisplayShort(failure.Candidate);
        var message =
            $"download attempt failed: {WithName(SongQueryText(failure.Query), candidate)}\n" +
            $"    Output: {failure.OutputPath}\n" +
            $"    Attempt: {failure.Attempt}/{failure.MaxAttempts}\n" +
            $"    {failure.ExceptionType}: {failure.ExceptionMessage}";

        if (PlainMode)
        {
            SockseekLog.Jobs.Warn($"[{failure.DisplayId}] SongJob: {message}");
            return;
        }

        SockseekLog.Jobs.Warn($"[{failure.DisplayId}] SongJob: {message}");
    }

    private void ReportStateChanged(SongStateChangedEventDto song)
    {
        MarkAlbumTrackCompleted(song.JobId);
        var candidate = song.ChosenCandidate;
        if ((TryGetAlbumParent(song.JobId, out var albumId) && _albumBlocks.TryGetValue(albumId, out var block))
            || TryRegisterAlbumChild(song.JobId, song.DisplayId, song.Query, candidate, out albumId, out block, out _))
        {
            block.CompletedSongIds.TryAdd(song.JobId, 0);
            UpdateLastUpdated(song.JobId);

            if (_bars.TryGetValue(song.JobId, out var data))
                data.StateLabel = TerminalStatusLabel(song);

            if (PlainMode)
            {
                // Refresh internal state but don't log, EventLogger handles it
            }
            else
            {
                UpsertLiveAlbum(albumId, block);
            }
        }
        else
        {
            if (_bars.TryGetValue(song.JobId, out var data))
                data.StateLabel = TerminalStatusLabel(song);

            if (PlainMode)
            {
                WritePlainSongStatus(song.JobId, song.DisplayId, song.Query, TerminalStatusLabel(song), SongTerminalDisplay(song));
            }
            else
            {
                RemoveLiveJob(song.JobId);
                _liveSongInfo.TryRemove(song.JobId, out _);
            }
        }
        _bars.TryRemove(song.JobId, out _);
        _savedState.TryRemove(song.JobId, out _);
    }


    // ── display event handlers ───────────────────────────────────────────

    private void ReportTrackBatchResolved(TrackBatchResolvedEventDto batch)
    {
        const int max = 10;

        void LogGroup(IReadOnlyList<SongJobPayloadDto> songs, string label)
        {
            if (songs.Count == 0) return;
            var shown = songs.Take(max).ToList();
            var more = songs.Count - shown.Count;
            var msg = $"[{batch.Summary.DisplayId}] {GetJobTypeLabel(batch.Summary.Kind)}: {songs.Count} {label}:\n"
                + string.Join('\n', shown.Select(s => $"    {SongQueryText(s.Query)}"))
                + (more > 0 ? $"\n    ... and {more} more" : "");
            SockseekLog.Jobs.Info(msg);
        }

        LogGroup(batch.Existing, "tracks already exist");
        LogGroup(batch.NotFound, "tracks were not found in a prior run");
    }

    private void ReportSearchRateLimited(SearchRateLimitedEventDto rateLimit)
    {
        if (LiveMode)
        {
            _live!.SetRateLimited(rateLimit.ResetsAt);
            return;
        }

        int secs = Math.Max(0, (int)Math.Ceiling((rateLimit.ResetsAt - DateTimeOffset.UtcNow).TotalSeconds));
        Printing.WriteLine($"Search rate limit reached, resuming in {secs}s", ConsoleColor.DarkGray);
    }

    private void ReportExtractionStarted(ExtractionStartedEventDto job)
    {
        if (LiveMode)
        {
            UpsertLiveJob(job.Summary, "extracting");
            return;
        }
    }

    private void ReportExtractionFailed(ExtractionFailedEventDto job)
    {
        if (LiveMode)
        {
            RemoveLiveJob(job.Summary.JobId);
            return;
        }
    }

    private void ReportJobStarted(JobStartedEventDto job)
    {
        RememberStructure(job.Summary);
        if (IsInlineChild(job.Summary))
            return;

        string status = job.Summary.Kind == ServerJobKind.RetrieveFolder ? "retrieving folder" : "searching";

        if (PlainMode)
        {
            _jobStatuses[job.Summary.JobId] = status;
            return;
        }

        if (job.Summary.Kind == ServerJobKind.Song)
            return;

        UpsertLiveJob(job.Summary, status);
        _jobStatuses[job.Summary.JobId] = status;
    }

    private void ReportJobFolderRetrieving(JobFolderRetrievingEventDto job)
    {
        if (PlainMode)
        {
            return;
        }

        UpsertLiveJob(job.Summary, "retrieving folder");
    }

    private void ReportSongSearching(SongSearchingEventDto song)
    {
        if (PlainMode)
        {
            _jobStatuses[song.JobId] = "searching";
            return;
        }

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
    }

    private void ReportDownloadStateChanged(DownloadStateChangedEventDto song)
    {
        if (PlainMode) return;

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
    }

    private void ReportJobStatus(JobStatusEventDto job)
    {
        RememberStructure(job.Summary);
        if (IsInlineChild(job.Summary))
            return;

        if (PlainMode)
        {
            _jobStatuses[job.Summary.JobId] = job.Status;
            return;
        }

        _jobStatuses[job.Summary.JobId] = job.Status;
        if (job.Summary.Kind == ServerJobKind.Album && _albumBlocks.TryGetValue(job.Summary.JobId, out var block))
            UpsertLiveAlbum(job.Summary.JobId, block);
        else
            UpsertLiveJob(job.Summary, job.Status);
    }

    private void ReportJobActivityChanged(JobActivityChangedEventDto job)
        => ReportJobUpserted(job.Summary);

    private void ReportJobMessage(JobMessageEventDto job)
    {
        RememberStructure(job.Summary);
    }

    private void ReportAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        _jobStatuses[job.Summary.JobId] = "downloading";
        InitializeAlbumBlock(job.Summary, job.Tracks, AlbumFolderDisplay(job.Folder, shortPath: true), AlbumFolderDisplay(job.Folder));

        if (PlainMode)
        {
            return;
        }

        UpsertLiveAlbum(job.Summary.JobId, _albumBlocks[job.Summary.JobId]);
    }

    private void ReportAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        if (_albumBlocks.ContainsKey(job.Summary.JobId))
            return;

        _jobStatuses[job.Summary.JobId] = "downloading tracks";
        InitializeAlbumBlock(job.Summary, job.Tracks, AlbumFolderDisplay(job.Folder, shortPath: true), AlbumFolderDisplay(job.Folder));

        if (PlainMode)
        {
            return;
        }

        UpsertLiveAlbum(job.Summary.JobId, _albumBlocks[job.Summary.JobId]);
    }

    private void ReportAlbumStateChanged(AlbumStateChangedEventDto job)
    {
        string? remoteFolderDisplay = null;
        if (_albumBlocks.TryRemove(job.Summary.JobId, out var liveBlock))
        {
            remoteFolderDisplay = liveBlock.RemoteFolderDisplay;
            foreach (var song in AlbumSongsSnapshot(liveBlock))
                if (song.JobId is Guid songJobId)
                {
                    _songToAlbum.TryRemove(songJobId, out _);
                    _bars.TryRemove(songJobId, out _);
                }
        }

        if (PlainMode)
        {
            _jobStatuses.TryRemove(job.Summary.JobId, out _);
            return;
        }
        RemoveLiveJob(job.Summary.JobId);
        _jobStatuses.TryRemove(job.Summary.JobId, out _);
        _liveTerminalParentLogs.TryAdd(job.Summary.JobId, 0);
    }

    private void CompleteAlbumBlock(Guid albumJobId, AlbumBlock block, JobSummaryDto summary)
    {
        CompleteRemainingAlbumBars(block, summary);
        _jobStatuses.TryRemove(albumJobId, out _);
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

        foreach (var song in AlbumSongsSnapshot(block).Where(s => s.JobId.HasValue))
        {
            var songJobId = song.JobId!.Value;
            _songToAlbum[songJobId] = summary.JobId;
            string filename = song.ResolvedFilename ?? $"{song.Query.Artist} - {song.Query.Title}";
            string shortName = AlbumChildDisplay(block, ResolvedSongDisplayLive(song) ?? filename);
            var data = ToBarData(song, shortName);
            _bars[songJobId] = data;
        }

        _albumBlocks[summary.JobId] = block;
    }

    private void CompleteRemainingAlbumBars(AlbumBlock block, JobSummaryDto summary)
    {
        foreach (var song in AlbumSongsSnapshot(block).Where(song => song.JobId.HasValue))
        {
            block.CompletedSongIds.TryAdd(song.JobId!.Value, 0);

            if (!_bars.TryGetValue(song.JobId!.Value, out var data))
                continue;

            var albumStatus = CliJobStatusPresenter.ForSummary(summary);
            data.StateLabel = albumStatus.Label;
            if (albumStatus.IsSuccessful)
            {
                data.Pct = 100;
            }
            else
            {
                var reason = ServerFailureReasonDisplay.Label(summary.FailureReason) is { Length: > 0 } summaryReason
                    ? summaryReason
                    : ServerFailureReasonDisplay.Label(song.FailureReason);
                if (reason.Length == 0) reason = ServerFailureReasonDisplay.LabelOrUnknown(summary.FailureReason ?? song.FailureReason);
                if (!data.BaseText.Contains($"[{reason}]", StringComparison.Ordinal))
                    data.BaseText += $" [{reason}]";
            }

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

    private static BarData ToBarData(SongJobPayloadDto song, string shortName)
    {
        var status = CliJobStatusPresenter.ForSongPayload(song);
        var data = new BarData { BaseText = shortName, StateLabel = status.Label, Pct = 0 };

        if (song.TerminalOutcome == ServerJobTerminalOutcome.Succeeded)
        {
            data.Pct = 100;
        }
        else if (song.TerminalOutcome == ServerJobTerminalOutcome.Skipped && song.SkipReason == ServerJobSkipReason.AlreadyExists)
        {
            data.Pct = 100;
        }
        else if (status.IsFailed)
        {
            var reason = ServerFailureReasonDisplay.Label(song.FailureReason);
            if (reason.Length > 0)
                data.BaseText += $" [{reason}]";
        }

        return data;
    }
}
