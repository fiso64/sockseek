using Microsoft.Extensions.Logging;
using Sockseek.Core;

namespace Sockseek.Api;

public enum ActivityLogSeverity
{
    Information,
    Error,
}

public sealed record ActivityLogEntry(
    string CategoryName,
    ActivityLogSeverity Severity,
    LogLevel Level,
    string Message,
    ActivityLogDisplay? Display = null);

public sealed record ActivityLogDisplay(
    int DisplayId,
    string JobType,
    string Message,
    ActivityLogDisplayKind Kind = ActivityLogDisplayKind.Status,
    string? Source = null,
    string? Highlight = null,
    bool ShowInLive = true);

public enum ActivityLogDisplayKind
{
    Status,
    Succeeded,
    Failed,
    Cancelled,
    AlreadyExists,
    Skipped,
    AlbumTrackSucceeded,
    AlbumTrackFailed,
    AlbumTrackSkipped,
}

public sealed class JobActivityLogFormatter
{
    public static readonly IReadOnlySet<string> HandledEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "job.upserted",
        "album.download-started",
        "album.track-download-started",
        "album.state-changed",
        "download.started",
        "on-complete.started",
        "on-complete.ended",
        "diagnostic.error",
        "song.state-changed",
        "extraction.started",
        "extraction.failed",
        "job.started",
        "job.folder-retrieving",
        "job.message",
        "song.searching",
    };

    private readonly Dictionary<Guid, ServerJobKind> jobKinds = [];
    private readonly Dictionary<Guid, Guid> parentJobIds = [];
    private readonly Dictionary<Guid, JobSummaryDto> albumSummaries = [];
    private readonly HashSet<Guid> loggedTerminalAlbumIds = [];
    private readonly Dictionary<Guid, string> lastMessages = [];
    private readonly object sync = new();

    public ActivityLogEntry? Format(ServerEventEnvelopeDto envelope)
    {
        lock (sync)
        {
            return envelope.Type switch
            {
                "job.upserted" when envelope.Payload is JobSummaryDto payload => HandleJobUpserted(payload),
                "album.download-started" when envelope.Payload is AlbumDownloadStartedEventDto payload => HandleAlbumDownloadStarted(payload),
                "album.track-download-started" when envelope.Payload is AlbumTrackDownloadStartedEventDto payload => HandleAlbumTrackDownloadStarted(payload),
                "album.state-changed" when envelope.Payload is AlbumStateChangedEventDto payload => HandleAlbumStateChanged(payload),
                "download.started" when envelope.Payload is DownloadStartedEventDto payload => HandleDownloadStart(payload),
                "on-complete.started" when envelope.Payload is OnCompleteStartedEventDto payload => Log(payload.JobId, $"OnComplete start: [{payload.DisplayId}] {SongQueryText(payload.Query)}"),
                "on-complete.ended" when envelope.Payload is OnCompleteEndedEventDto payload => Log(payload.JobId, $"OnComplete end: [{payload.DisplayId}] {SongQueryText(payload.Query)}"),
                "diagnostic.error" when envelope.Payload is DiagnosticErrorEventDto payload => HandleDiagnosticError(payload),
                "song.state-changed" when envelope.Payload is SongStateChangedEventDto payload => HandleSongStateChanged(payload),
                "extraction.started" when envelope.Payload is ExtractionStartedEventDto payload => HandleExtractionStart(payload),
                "extraction.failed" when envelope.Payload is ExtractionFailedEventDto payload => HandleExtractionFailed(payload),
                "job.started" when envelope.Payload is JobStartedEventDto payload => HandleJobStarted(payload),
                "job.folder-retrieving" when envelope.Payload is JobFolderRetrievingEventDto payload => HandleJobFolderRetrieving(payload),
                "job.message" when envelope.Payload is JobMessageEventDto payload => HandleJobMessage(payload),
                "song.searching" when envelope.Payload is SongSearchingEventDto payload => LogJob(payload.JobId, payload.DisplayId, "SongJob", $"searching: {SongQueryText(payload.Query)}", showInLive: false),
                _ => null,
            };
        }
    }

    private ActivityLogEntry? HandleExtractionStart(ExtractionStartedEventDto job)
    {
        if (string.IsNullOrWhiteSpace(job.InputType))
            return null;

        return LogJob(job.Summary.JobId, job.Summary.DisplayId, "ExtractJob", $"Input: {job.Input}" + ProfileSuffix(job.Summary), source: job.Source ?? job.InputType);
    }

    private ActivityLogEntry? HandleExtractionFailed(ExtractionFailedEventDto job)
    {
        return LogJob(
            job.Summary.JobId,
            job.Summary.DisplayId,
            "ExtractJob",
            $"Failed: {job.Summary.QueryText}\n  Reason:    {job.Reason}",
            ActivityLogSeverity.Error,
            kind: ActivityLogDisplayKind.Failed,
            source: job.Source,
            highlight: "Failed");
    }

    private ActivityLogEntry? HandleDiagnosticError(DiagnosticErrorEventDto diagnostic)
    {
        if (diagnostic.Summary is { } summary)
        {
            return LogJob(
                summary.JobId,
                summary.DisplayId,
                JobTypeLabel(summary.Kind),
                $"diagnostic: {DiagnosticHeadline(diagnostic)}\n  Exception:\n{IndentContinuationLines(diagnostic.Exception, "    ")}",
                ActivityLogSeverity.Error,
                kind: ActivityLogDisplayKind.Failed,
                source: diagnostic.Source,
                highlight: "diagnostic");
        }

        return Log(
            diagnostic.WorkflowId ?? Guid.Empty,
            $"Diagnostic error ({diagnostic.Scope}): {DiagnosticHeadline(diagnostic)}\n  Exception:\n{IndentContinuationLines(diagnostic.Exception, "    ")}",
            ActivityLogSeverity.Error);
    }

    private ActivityLogEntry? HandleJobStarted(JobStartedEventDto job)
    {
        RememberStructure(job.Summary);
        if (IsInlineChild(job.Summary.JobId, job.Summary.Kind))
            return null;
        if (job.Summary.Kind == ServerJobKind.Song)
            return null;

        string status = job.Summary.Kind == ServerJobKind.RetrieveFolder ? "retrieving folder" : "searching";
        return LogJob(job.Summary, status, showInLive: false);
    }

    private ActivityLogEntry? HandleJobFolderRetrieving(JobFolderRetrievingEventDto job)
        => LogJob(job.Summary, "retrieving folder", showInLive: false);

    private ActivityLogEntry? HandleJobMessage(JobMessageEventDto job)
    {
        var level = ParseLogLevel(job.Level);

        return LogJob(
            job.Summary.JobId,
            job.Summary.DisplayId,
            JobTypeLabel(job.Summary.Kind),
            job.Message,
            IsErrorLevel(level) ? ActivityLogSeverity.Error : ActivityLogSeverity.Information,
            level,
            IsErrorLevel(level) ? ActivityLogDisplayKind.Failed : ActivityLogDisplayKind.Status,
            job.Source,
            highlight: IsErrorLevel(level) ? job.Message : null);
    }

    private ActivityLogEntry? HandleJobUpserted(JobSummaryDto summary)
    {
        RememberStructure(summary);

        if (summary.Kind == ServerJobKind.Extract)
            return null;
        if (summary.Kind == ServerJobKind.Song)
            return null;
        if (IsInlineChild(summary.JobId, summary.Kind))
            return null;
        if (summary.Kind == ServerJobKind.Album)
            albumSummaries[summary.JobId] = summary;

        bool isTerminal = IsTerminalJobState(summary.State);
        string label = TerminalStatusLabel(summary.State, summary.FailureReason);
        var kind = DisplayKindForState(summary.State, summary.FailureReason);

        if (isTerminal)
        {
            if (summary.Kind == ServerJobKind.Album)
            {
                if (IsSuccessfulTerminalState(summary.State))
                    return null;
                albumSummaries.Remove(summary.JobId);
                if (!loggedTerminalAlbumIds.Add(summary.JobId))
                    return null;
            }

            return LogJob(summary, label, kind: kind, showInLive: ShowTerminalKindInLive(kind));
        }

        return LogJob(summary, label, showInLive: false);
    }

    private ActivityLogEntry? HandleAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        RememberStructure(job.Summary);
        albumSummaries[job.Summary.JobId] = job.Summary;
        return LogJob(job.Summary, "downloading", showInLive: false);
    }

    private ActivityLogEntry? HandleAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        RememberStructure(job.Summary);
        albumSummaries[job.Summary.JobId] = job.Summary;
        string folderName = string.IsNullOrWhiteSpace(job.Folder.FolderPath)
            ? job.Summary.QueryText ?? ""
            : job.Folder.FolderPath;

        if (job.Tracks != null)
        {
            foreach (var track in job.Tracks)
            {
                if (track.JobId is Guid childId)
                {
                    jobKinds[childId] = ServerJobKind.Song;
                    parentJobIds[childId] = job.Summary.JobId;
                }
            }
        }

        return LogJob(job.Summary.JobId, job.Summary.DisplayId, "AlbumJob", $"downloading tracks: {job.Summary.QueryText} - {folderName}", showInLive: false);
    }

    private ActivityLogEntry? HandleAlbumStateChanged(AlbumStateChangedEventDto job)
    {
        if (!loggedTerminalAlbumIds.Add(job.Summary.JobId))
        {
            albumSummaries.Remove(job.Summary.JobId);
            return null;
        }

        albumSummaries.TryGetValue(job.Summary.JobId, out var album);
        var summary = album ?? job.Summary;
        var status = TerminalStatusLabel(summary.State, summary.FailureReason);
        var kind = DisplayKindForState(summary.State, summary.FailureReason);
        var entry = LogJob(
            summary.JobId,
            summary.DisplayId,
            JobTypeLabel(summary.Kind),
            AlbumCompletedLogMessage(summary, null, job.DownloadPath),
            kind: kind,
            highlight: status,
            showInLive: ShowTerminalKindInLive(kind));
        albumSummaries.Remove(job.Summary.JobId);
        return entry;
    }

    private ActivityLogEntry? HandleDownloadStart(DownloadStartedEventDto song)
        => LogJob(song.JobId, song.DisplayId, "SongJob", $"downloading: {WithName(SongQueryText(song.Query), CandidateDisplayShort(song.Candidate.Ref))}", showInLive: false);

    private ActivityLogEntry? HandleSongStateChanged(SongStateChangedEventDto song)
    {
        string label = TerminalStatusLabel(song.State, song.FailureReason);
        var kind = DisplayKindForState(song.State, song.FailureReason);
        string detail = SongQueryText(song.Query);
        if (IsTerminalJobState(song.State) && song.ChosenCandidate is FileCandidateDto candidate)
            detail = WithName(detail, CandidateDisplayShort(candidate.Ref));

        string prefix = "SongJob: ";
        if (IsInlineChild(song.JobId, ServerJobKind.Song)
            && parentJobIds.TryGetValue(song.JobId, out var parentId)
            && albumSummaries.TryGetValue(parentId, out var album))
        {
            if (IsTerminalJobState(song.State))
            {
                string itemName = song.ChosenCandidate?.Ref.Filename != null
                    ? Utils.GetFileNameSlsk(song.ChosenCandidate.Ref.Filename)
                    : detail;
                var albumName = album.QueryText ?? album.ItemName ?? "";
                return LogJob(
                    song.JobId,
                    album.DisplayId,
                    "Album Track",
                    $"{label}: {WithName(albumName, itemName)}",
                    kind: AlbumTrackDisplayKind(kind),
                    highlight: label,
                    showInLive: ShowTerminalKindInLive(kind));
            }
        }

        var jobType = prefix.TrimEnd().TrimEnd(':');
        string line = $"{label}: {detail}";
        if (!string.IsNullOrEmpty(song.FailureMessage))
            line += "\n" + $"    Error: {song.FailureMessage}";

        return LogJob(
            song.JobId,
            song.DisplayId,
            jobType,
            line,
            kind: kind,
            highlight: label,
            showInLive: ShowSongTerminalStatusInLive(song));
    }

    private ActivityLogEntry? LogJob(
        JobSummaryDto summary,
        string status,
        ActivityLogSeverity severity = ActivityLogSeverity.Information,
        LogLevel? level = null,
        ActivityLogDisplayKind? kind = null,
        bool showInLive = true)
    {
        var body = JobStatusBody(summary, status);
        var displayKind = kind ?? DisplayKindForStatus(status);
        return LogJob(summary.JobId, summary.DisplayId, JobTypeLabel(summary.Kind), body, severity, level, displayKind, highlight: status, showInLive: showInLive);
    }

    private ActivityLogEntry? LogJob(
        Guid jobId,
        int displayId,
        string jobType,
        string body,
        ActivityLogSeverity severity = ActivityLogSeverity.Information,
        LogLevel? level = null,
        ActivityLogDisplayKind kind = ActivityLogDisplayKind.Status,
        string? source = null,
        string? highlight = null,
        bool showInLive = true)
        => Log(
            jobId,
            $"[{displayId}] {jobType}: {SourcePrefix(source)}{body}",
            severity,
            level,
            new ActivityLogDisplay(displayId, jobType, body, kind, source, highlight, showInLive));

    private ActivityLogEntry? Log(
        Guid jobId,
        string message,
        ActivityLogSeverity severity = ActivityLogSeverity.Information,
        LogLevel? level = null,
        ActivityLogDisplay? display = null)
    {
        if (lastMessages.TryGetValue(jobId, out var last) && last == message)
            return null;

        lastMessages[jobId] = message;
        return new ActivityLogEntry(
            SockseekLog.Categories.Jobs,
            severity,
            level ?? (severity == ActivityLogSeverity.Error ? LogLevel.Error : LogLevel.Information),
            message,
            display);
    }

    private void RememberStructure(JobSummaryDto summary)
    {
        jobKinds[summary.JobId] = summary.Kind;
        if (summary.ParentJobId is Guid parentId)
            parentJobIds[summary.JobId] = parentId;
    }

    private bool IsInlineChild(Guid jobId, ServerJobKind kind)
        => kind == ServerJobKind.Song
            && parentJobIds.TryGetValue(jobId, out var parentId)
            && jobKinds.TryGetValue(parentId, out var parentKind)
            && parentKind == ServerJobKind.Album;

    private static bool IsTerminalJobState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists or ServerProtocol.JobStates.Failed or ServerProtocol.JobStates.Skipped or ServerProtocol.JobStates.NotFoundLastTime;

    private static bool IsSuccessfulTerminalState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;

    private static bool IsErrorLevel(LogLevel level)
        => level is LogLevel.Error or LogLevel.Critical;

    private static ActivityLogDisplayKind DisplayKindForStatus(string status)
    {
        if (status.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
            return ActivityLogDisplayKind.Failed;
        if (status.StartsWith("succeeded", StringComparison.OrdinalIgnoreCase))
            return ActivityLogDisplayKind.Succeeded;
        if (status.StartsWith("already exists", StringComparison.OrdinalIgnoreCase))
            return ActivityLogDisplayKind.AlreadyExists;
        if (status.StartsWith("skipped", StringComparison.OrdinalIgnoreCase))
            return ActivityLogDisplayKind.Skipped;

        return ActivityLogDisplayKind.Status;
    }

    private static ActivityLogDisplayKind DisplayKindForState(ServerJobState state, ServerFailureReason? failureReason)
        => state switch
        {
            ServerProtocol.JobStates.Done => ActivityLogDisplayKind.Succeeded,
            ServerProtocol.JobStates.AlreadyExists => ActivityLogDisplayKind.AlreadyExists,
            ServerProtocol.JobStates.Skipped or ServerProtocol.JobStates.NotFoundLastTime => ActivityLogDisplayKind.Skipped,
            ServerProtocol.JobStates.Failed when failureReason == ServerProtocol.FailureReasons.Cancelled => ActivityLogDisplayKind.Cancelled,
            ServerProtocol.JobStates.Failed => ActivityLogDisplayKind.Failed,
            _ => ActivityLogDisplayKind.Status,
        };

    private static bool ShowTerminalKindInLive(ActivityLogDisplayKind kind)
        => kind != ActivityLogDisplayKind.Status;

    private static ActivityLogDisplayKind AlbumTrackDisplayKind(ActivityLogDisplayKind kind)
        => kind switch
        {
            ActivityLogDisplayKind.Succeeded or ActivityLogDisplayKind.AlreadyExists => ActivityLogDisplayKind.AlbumTrackSucceeded,
            ActivityLogDisplayKind.Skipped or ActivityLogDisplayKind.Cancelled => ActivityLogDisplayKind.AlbumTrackSkipped,
            ActivityLogDisplayKind.Failed => ActivityLogDisplayKind.AlbumTrackFailed,
            _ => kind,
        };

    private bool ShowSongTerminalStatusInLive(SongStateChangedEventDto song)
    {
        if (!IsJobListChild(song.JobId))
            return true;

        return song.State is not (
            ServerProtocol.JobStates.AlreadyExists
            or ServerProtocol.JobStates.Skipped
            or ServerProtocol.JobStates.NotFoundLastTime);
    }

    private bool IsJobListChild(Guid jobId)
        => parentJobIds.TryGetValue(jobId, out var parentId)
            && jobKinds.TryGetValue(parentId, out var parentKind)
            && parentKind == ServerJobKind.JobList;

    private static LogLevel ParseLogLevel(string level)
        => Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

    private static string TerminalStatusLabel(ServerJobState state, ServerFailureReason? reason)
        => state switch
        {
            ServerProtocol.JobStates.Done => "succeeded",
            ServerProtocol.JobStates.AlreadyExists => "already exists",
            ServerProtocol.JobStates.Pending => "pending",
            ServerProtocol.JobStates.Searching => "searching",
            ServerProtocol.JobStates.Downloading => "downloading",
            ServerProtocol.JobStates.Extracting => "extracting",
            ServerProtocol.JobStates.Running => "running",
            ServerProtocol.JobStates.Skipped => "skipped",
            _ => reason != null ? $"failed [{FailureReasonLabel(reason)}]" : "failed",
        };

    private static string FailureReasonLabel(ServerFailureReason? reason) => reason switch
    {
        ServerFailureReason.NoSuitableFileFound => "No suitable file found",
        ServerFailureReason.InvalidSearchString => "Invalid search string",
        ServerFailureReason.OutOfDownloadRetries => "Out of download retries",
        ServerFailureReason.AllDownloadsFailed => "All downloads failed",
        ServerFailureReason.ExtractionFailed => "Extraction failed",
        ServerFailureReason.Cancelled => "Cancelled",
        ServerFailureReason.Other => "Unknown error",
        _ => "",
    };

    private static string JobStatusBody(JobSummaryDto summary, string status)
    {
        var name = summary.ItemName ?? "";
        var detail = summary.QueryText ?? name;
        var line = $"{status}: {WithName(name, detail)}" + ProfileSuffix(summary);

        if (summary.State == ServerProtocol.JobStates.Done && summary.Kind == ServerJobKind.Search && summary.DiscoveryResultCount.HasValue)
            line += $": Found {summary.DiscoveryResultCount.Value} files";

        if (!string.IsNullOrEmpty(summary.FailureMessage))
            line += "\n" + $"    Error: {summary.FailureMessage}";

        return line;
    }

    private static string JobTypeLabel(ServerJobKind kind)
        => kind switch
        {
            ServerJobKind.RetrieveFolder => "Retrieve Folder",
            ServerJobKind.JobList => "Job List",
            ServerJobKind.AlbumAggregate => "Album Aggregate",
            _ => $"{char.ToUpperInvariant(kind.ToWireString()[0])}{kind.ToWireString()[1..]}Job",
        };

    private static string JobStatusPrefix(ServerJobKind kind)
        => $"{JobTypeLabel(kind)}: ";

    private static string AlbumCompletedLogMessage(JobSummaryDto summary, string? remoteFolderDisplay, string? completedPath)
    {
        var albumName = summary.QueryText ?? summary.ItemName ?? "";
        var status = TerminalStatusLabel(summary.State, summary.FailureReason);
        bool succeeded = summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;

        if (succeeded && !string.IsNullOrWhiteSpace(remoteFolderDisplay))
            return $"{status}: {WithName(albumName, remoteFolderDisplay)}" + ProfileSuffix(summary);

        if (!string.IsNullOrWhiteSpace(completedPath))
            return $"{status}: {WithName(albumName, $"completed at {completedPath}")}" + ProfileSuffix(summary);

        return $"{status}: {albumName}" + ProfileSuffix(summary);
    }

    private static string SongQueryText(SongQueryDto query)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(query.Artist);
        bool hasTitle = !string.IsNullOrWhiteSpace(query.Title);
        if (hasArtist && hasTitle) return $"{query.Artist} - {query.Title}";
        if (hasArtist) return query.Artist!;
        if (hasTitle) return query.Title!;
        return query.Album ?? query.Uri ?? "";
    }

    private static string WithName(string name, string detail)
        => string.IsNullOrWhiteSpace(name) || name == detail ? detail : $"{name}: {detail}";

    private static string DiagnosticHeadline(DiagnosticErrorEventDto diagnostic)
    {
        var headline = !string.IsNullOrWhiteSpace(diagnostic.ExceptionType)
            ? diagnostic.ExceptionType
            : diagnostic.Scope;
        var lastDot = headline.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < headline.Length - 1)
            headline = headline[(lastDot + 1)..];

        return string.IsNullOrWhiteSpace(headline) ? diagnostic.Message : headline;
    }

    private static string SourcePrefix(string? source)
        => string.IsNullOrWhiteSpace(source) ? "" : $"{source}: ";

    private static string ProfileSuffix(JobSummaryDto summary)
        => summary.AppliedAutoProfiles.Count > 0 ? $" [{string.Join(", ", summary.AppliedAutoProfiles)}]" : "";

    private static string CandidateDisplayShort(FileCandidateRefDto candidate)
    {
        var filename = candidate.Filename.Replace('/', '\\').TrimStart('\\');
        var parts = filename.Split('\\');
        bool truncated = parts.Length > 3;
        var displayed = truncated ? string.Join('\\', parts[^3..]) : filename;
        return truncated ? $"{candidate.Username}\\..\\{displayed}" : $"{candidate.Username}\\{displayed}";
    }

    private static string IndentContinuationLines(string value, string indent)
        => string.Join('\n', value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => indent + line));

}
