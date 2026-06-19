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
        "diagnostic.error",
        "song.state-changed",
        "extraction.started",
        "extraction.failed",
        "job.started",
        "job.folder-retrieving",
        "job.message",
        "job.activity-changed",
        "song.searching",
    };

    private readonly Dictionary<Guid, ServerJobKind> jobKinds = [];
    private readonly Dictionary<Guid, Guid> parentJobIds = [];
    private readonly Dictionary<Guid, JobSummaryDto> albumSummaries = [];
    private readonly Dictionary<Guid, string> albumTrackDownloadFolders = [];
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
                "diagnostic.error" when envelope.Payload is DiagnosticErrorEventDto payload => HandleDiagnosticError(payload),
                "song.state-changed" when envelope.Payload is SongStateChangedEventDto payload => HandleSongStateChanged(payload),
                "extraction.started" when envelope.Payload is ExtractionStartedEventDto payload => HandleExtractionStart(payload),
                "extraction.failed" when envelope.Payload is ExtractionFailedEventDto payload => HandleExtractionFailed(payload),
                "job.started" when envelope.Payload is JobStartedEventDto payload => HandleJobStarted(payload),
                "job.folder-retrieving" when envelope.Payload is JobFolderRetrievingEventDto payload => HandleJobFolderRetrieving(payload),
                "job.message" when envelope.Payload is JobMessageEventDto payload => HandleJobMessage(payload),
                "job.activity-changed" when envelope.Payload is JobActivityChangedEventDto payload => HandleJobActivityChanged(payload),
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
        return null;
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

        if (summary.Kind == ServerJobKind.Album)
            albumSummaries[summary.JobId] = summary;

        bool isTerminal = IsTerminal(summary);
        string label = StatusLabel(summary);
        var kind = DisplayKindForSummary(summary);

        if (!isTerminal)
        {
            if (summary.LifecycleState == ServerJobLifecycleState.Pending)
                return LogJob(summary, label, level: LogLevel.Debug, showInLive: false);
            if (summary.LifecycleState == ServerJobLifecycleState.AwaitingSelection)
                return LogJob(summary, label, level: LogLevelForNonTerminalSummary(summary), showInLive: false);
            return null;
        }

        if (summary.Kind == ServerJobKind.Extract)
            return null;
        if (summary.Kind == ServerJobKind.Song)
            return null;
        if (IsInlineChild(summary.JobId, summary.Kind))
            return null;

        if (isTerminal)
        {
            if (summary.Kind == ServerJobKind.Album)
            {
                if (IsSuccessfulTerminalOutcome(summary.TerminalOutcome, summary.SkipReason))
                    return null;
                albumSummaries.Remove(summary.JobId);
                if (!loggedTerminalAlbumIds.Add(summary.JobId))
                    return null;
            }

            return LogJob(
                summary,
                label,
                level: LogLevelForTerminalSummary(summary),
                kind: kind,
                showInLive: ShowTerminalKindInLive(kind));
        }

        return LogJob(summary, label, level: LogLevelForNonTerminalSummary(summary), showInLive: false);
    }

    private ActivityLogEntry? HandleJobActivityChanged(JobActivityChangedEventDto activity)
    {
        var summary = activity.Summary;
        RememberStructure(summary);

        if (IsTerminal(summary))
            return HandleJobUpserted(summary);
        if (!ShouldLogActivityChanged(summary))
            return null;

        return LogJob(
            summary,
            StatusLabel(summary),
            level: LogLevelForNonTerminalSummary(summary),
            showInLive: false);
    }

    private ActivityLogEntry? HandleAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        RememberStructure(job.Summary);
        albumSummaries[job.Summary.JobId] = job.Summary;
        return null;
    }

    private ActivityLogEntry? HandleAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        RememberStructure(job.Summary);
        albumSummaries[job.Summary.JobId] = job.Summary;
        string folderName = string.IsNullOrWhiteSpace(job.Folder.FolderPath)
            ? job.Summary.QueryText ?? ""
            : job.Folder.FolderPath;

        if (albumTrackDownloadFolders.TryGetValue(job.Summary.JobId, out var previousFolder)
            && string.Equals(previousFolder, folderName, StringComparison.Ordinal))
        {
            return null;
        }
        albumTrackDownloadFolders[job.Summary.JobId] = folderName;

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
        var status = StatusLabel(summary);
        var kind = DisplayKindForSummary(summary);
        var entry = LogJob(
            summary.JobId,
            summary.DisplayId,
            JobTypeLabel(summary.Kind),
            AlbumCompletedLogMessage(summary, null, job.DownloadPath),
            kind: kind,
            highlight: status,
            showInLive: ShowTerminalKindInLive(kind));
        albumSummaries.Remove(job.Summary.JobId);
        albumTrackDownloadFolders.Remove(job.Summary.JobId);
        return entry;
    }

    private ActivityLogEntry? HandleDownloadStart(DownloadStartedEventDto song)
        => LogJob(song.JobId, song.DisplayId, "SongJob", $"downloading: {WithName(SongQueryText(song.Query), CandidateDisplayShort(song.Candidate.Ref))}", showInLive: false);

    private ActivityLogEntry? HandleSongStateChanged(SongStateChangedEventDto song)
    {
        string label = StatusLabel(song);
        var kind = DisplayKindForSong(song);
        string detail = SongQueryText(song.Query);
        if (IsTerminal(song) && song.ChosenCandidate is FileCandidateDto candidate)
            detail = WithName(detail, CandidateDisplayShort(candidate.Ref));

        string prefix = "SongJob: ";
        if (IsInlineChild(song.JobId, ServerJobKind.Song)
            && parentJobIds.TryGetValue(song.JobId, out var parentId)
            && albumSummaries.TryGetValue(parentId, out var album))
        {
            prefix = "AlbumJob: ";
            if (IsTerminal(song))
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
                    level: LogLevelForTerminalSong(song),
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
            level: LogLevelForTerminalSong(song),
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

    private static bool IsTerminal(JobSummaryDto summary)
        => summary.LifecycleState == ServerJobLifecycleState.Terminal;

    private static bool IsTerminal(SongStateChangedEventDto song)
        => song.LifecycleState == ServerJobLifecycleState.Terminal;

    private static bool IsSuccessfulTerminalOutcome(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason = ServerJobSkipReason.None)
        => outcome == ServerJobTerminalOutcome.Succeeded
            || (outcome == ServerJobTerminalOutcome.Skipped && skipReason == ServerJobSkipReason.AlreadyExists);

    private static bool IsErrorLevel(LogLevel level)
        => level is LogLevel.Error or LogLevel.Critical;

    private static LogLevel? LogLevelForTerminalSummary(JobSummaryDto summary)
        => IsCascadeCancellation(summary.TerminalOutcome, summary.FailureReason, summary.CancellationSource)
            ? LogLevel.Debug
            : null;

    private static LogLevel? LogLevelForTerminalSong(SongStateChangedEventDto song)
        => IsCascadeCancellation(song.TerminalOutcome, song.FailureReason, song.CancellationSource)
            ? LogLevel.Debug
            : null;

    private static bool IsCascadeCancellation(
        ServerJobTerminalOutcome outcome,
        ServerJobFailureReason? failureReason,
        ServerJobCancellationSource source)
        => IsCancellationOutcome(outcome, failureReason)
            && source is ServerJobCancellationSource.ParentJob
                or ServerJobCancellationSource.UserRequestedWorkflow
                or ServerJobCancellationSource.UserRequestedAllJobs
                or ServerJobCancellationSource.InternalEngine;

    private static bool IsCancellationOutcome(ServerJobTerminalOutcome outcome, ServerJobFailureReason? failureReason)
        => outcome == ServerJobTerminalOutcome.Cancelled
            || (outcome == ServerJobTerminalOutcome.Failed && failureReason == ServerProtocol.FailureReasons.Cancelled);

    private static LogLevel LogLevelForNonTerminalSummary(JobSummaryDto summary)
        => IsDebugOnlyLifecycleActivity(summary) ? LogLevel.Debug : LogLevel.Information;

    private static bool IsDebugOnlyLifecycleActivity(JobSummaryDto summary)
        => summary.LifecycleState == ServerJobLifecycleState.Pending
            || summary.ActivityPhase is ServerJobActivityPhase.WaitingForSearchConcurrency
                or ServerJobActivityPhase.SearchRateLimited;

    private bool ShouldLogActivityChanged(JobSummaryDto summary)
    {
        if (summary.ActivityPhase == ServerJobActivityPhase.RunningOnComplete)
            return true;
        if (IsDebugOnlyLifecycleActivity(summary))
            return true;
        if (summary.Kind == ServerJobKind.Extract)
            return false;
        if (summary.Kind == ServerJobKind.Song)
            return false;
        if (summary.Kind == ServerJobKind.Album && summary.ActivityPhase == ServerJobActivityPhase.Downloading)
            return false;
        if (IsInlineChild(summary.JobId, summary.Kind))
            return false;

        return true;
    }

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
        if (status.StartsWith("cancelled", StringComparison.OrdinalIgnoreCase))
            return ActivityLogDisplayKind.Cancelled;

        return ActivityLogDisplayKind.Status;
    }

    private static ActivityLogDisplayKind DisplayKindForSummary(JobSummaryDto summary)
        => DisplayKindForTerminal(summary.TerminalOutcome, summary.SkipReason, summary.FailureReason);

    private static ActivityLogDisplayKind DisplayKindForSong(SongStateChangedEventDto song)
        => DisplayKindForTerminal(song.TerminalOutcome, song.SkipReason, song.FailureReason);

    private static ActivityLogDisplayKind DisplayKindForTerminal(
        ServerJobTerminalOutcome outcome,
        ServerJobSkipReason skipReason,
        ServerJobFailureReason? failureReason)
        => outcome switch
        {
            ServerJobTerminalOutcome.Succeeded => ActivityLogDisplayKind.Succeeded,
            ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.AlreadyExists => ActivityLogDisplayKind.AlreadyExists,
            ServerJobTerminalOutcome.Skipped => ActivityLogDisplayKind.Skipped,
            ServerJobTerminalOutcome.Cancelled => ActivityLogDisplayKind.Cancelled,
            ServerJobTerminalOutcome.Failed when failureReason == ServerProtocol.FailureReasons.Cancelled => ActivityLogDisplayKind.Cancelled,
            ServerJobTerminalOutcome.Failed => ActivityLogDisplayKind.Failed,
            ServerJobTerminalOutcome.PartialSuccess => ActivityLogDisplayKind.Failed,
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

        return song.TerminalOutcome != ServerJobTerminalOutcome.Skipped;
    }

    private bool IsJobListChild(Guid jobId)
        => parentJobIds.TryGetValue(jobId, out var parentId)
            && jobKinds.TryGetValue(parentId, out var parentKind)
            && parentKind == ServerJobKind.JobList;

    private static LogLevel ParseLogLevel(string level)
        => Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

    private static string StatusLabel(JobSummaryDto summary)
        => StatusLabel(summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason);

    private static string StatusLabel(SongStateChangedEventDto song)
        => StatusLabel(song.LifecycleState, song.ActivityPhase, song.TerminalOutcome, song.SkipReason, song.FailureReason);

    private static string StatusLabel(
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity,
        ServerJobTerminalOutcome outcome,
        ServerJobSkipReason skipReason,
        ServerJobFailureReason? reason)
    {
        if (lifecycle == ServerJobLifecycleState.Terminal)
            return TerminalStatusLabel(outcome, skipReason, reason);

        return activity switch
        {
            ServerJobActivityPhase.WaitingForSearchConcurrency => "waiting search",
            ServerJobActivityPhase.SearchRateLimited => "rate limited",
            ServerJobActivityPhase.Searching => "searching",
            ServerJobActivityPhase.ProcessingSearchResults => "processing results",
            ServerJobActivityPhase.Extracting => "extracting",
            ServerJobActivityPhase.Downloading => "downloading",
            ServerJobActivityPhase.RetrievingFolder => "retrieving folder",
            ServerJobActivityPhase.RunningChildren => "running",
            ServerJobActivityPhase.Organizing => "organizing",
            ServerJobActivityPhase.RunningOnComplete => "on-complete",
            _ => lifecycle switch
            {
                ServerJobLifecycleState.Pending => "pending",
                ServerJobLifecycleState.AwaitingSelection => "awaiting selection",
                _ => "running",
            },
        };
    }

    private static string TerminalStatusLabel(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason, ServerJobFailureReason? reason)
        => outcome switch
        {
            ServerJobTerminalOutcome.Succeeded => "succeeded",
            ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.AlreadyExists => "already exists",
            ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.NotFoundLastTime => "not found",
            ServerJobTerminalOutcome.Skipped => "skipped",
            ServerJobTerminalOutcome.Cancelled => "cancelled",
            ServerJobTerminalOutcome.PartialSuccess => "partial",
            ServerJobTerminalOutcome.Failed => ServerFailureReasonDisplay.FailedLabel(reason),
            _ => "failed",
        };

    private static string JobStatusBody(JobSummaryDto summary, string status)
    {
        var name = summary.ItemName ?? "";
        var detail = summary.QueryText ?? name;
        var line = $"{status}: {WithName(name, detail)}" + ProfileSuffix(summary);

        if (summary.TerminalOutcome == ServerJobTerminalOutcome.Succeeded
            && summary.Kind == ServerJobKind.Search
            && summary.DiscoveryResultCount.HasValue)
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

    private static string AlbumCompletedLogMessage(JobSummaryDto summary, string? remoteFolderDisplay, string? completedPath)
    {
        var albumName = summary.QueryText ?? summary.ItemName ?? "";
        var status = StatusLabel(summary);
        bool succeeded = IsSuccessfulTerminalOutcome(summary.TerminalOutcome, summary.SkipReason);

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
