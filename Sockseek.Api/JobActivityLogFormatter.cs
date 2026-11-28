using Microsoft.Extensions.Logging;

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
    Partial,
    Cancelled,
    AlreadyExists,
    Skipped,
    AlbumTrackSucceeded,
    AlbumTrackFailed,
    AlbumTrackSkipped,
}

/// <summary>
/// Formats reducer state and compact activity into CLI log entries. Durable state
/// is formatted from <see cref="JobSummaryDto"/>; activity never carries a job row.
/// </summary>
public sealed class JobActivityLogFormatter
{
    public const string AlbumFileJobType = "Album File";

    public static readonly IReadOnlySet<string> HandledActivityTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "job.message",
            "workflow.message",
            "diagnostic.error",
            "extraction.started",
        };

    private readonly Dictionary<Guid, ServerJobKind> jobKinds = [];
    private readonly Dictionary<Guid, Guid> parentJobIds = [];
    private readonly HashSet<Guid> loggedTerminalJobs = [];
    // What each job downloaded, kept until its finished line (or its album's) is written.
    private readonly Dictionary<Guid, TransferFacts> transfers = [];
    private readonly Dictionary<Guid, string> albumFolders = [];
    private readonly object sync = new();

    private sealed record TransferFacts(string? Username, string? RemotePath, string? LocalPath);

    /// <summary>
    /// Records where a job's file came from and went, so its finished line can say
    /// so. Call before formatting the jobs of the same update.
    /// </summary>
    public void Remember(TransferStateDto transfer)
    {
        if (transfer.Identity.JobId is not Guid jobId
            || !string.Equals(transfer.Identity.Direction, "Download", StringComparison.OrdinalIgnoreCase))
            return;
        lock (sync)
        {
            if (loggedTerminalJobs.Contains(jobId))
                return;
            // A job may try several candidates; the one that succeeded is the one to show.
            if (!transfers.TryGetValue(jobId, out TransferFacts? known)
                || transfer.Status.TerminalOutcome == TransferTerminalOutcome.Succeeded
                || known.LocalPath == null)
                transfers[jobId] = new(transfer.Identity.Username, transfer.Identity.RemotePath, transfer.Status.LocalPath);
        }
    }

    /// <summary>
    /// Records a job's kind and parent. Call for every job of an update before
    /// formatting any of them: a child formatted first must already know its parent.
    /// </summary>
    public void Remember(JobSummaryDto summary)
    {
        lock (sync)
            RememberStructure(summary);
    }

    public ActivityLogEntry? Format(JobSummaryDto summary)
    {
        lock (sync)
        {
            RememberStructure(summary);
            if (summary.Lifecycle.LifecycleState != JobLifecycleState.Terminal)
                return null;
            if (!loggedTerminalJobs.Add(summary.JobId))
                return null;

            var displayKind = DisplayKind(summary);
            // The input line already says what was read; only a failed extraction needs another.
            if (summary.Display.Kind == ServerJobKind.Extract && displayKind == ActivityLogDisplayKind.Succeeded)
                return null;
            var level = displayKind is ActivityLogDisplayKind.Failed
                or ActivityLogDisplayKind.Partial
                ? LogLevel.Error
                : LogLevel.Information;
            string status = StatusLabel(summary);
            bool albumChild = IsAlbumChild(summary);
            string detail = TerminalDetail(summary, albumChild);
            string message = string.IsNullOrWhiteSpace(detail)
                ? status
                : $"{status}: {detail}";
            if (!string.IsNullOrWhiteSpace(summary.Lifecycle.FailureMessage))
                message += $"\n    Error: {summary.Lifecycle.FailureMessage}";

            if (albumChild)
                displayKind = AlbumTrackDisplayKind(displayKind);

            return JobEntry(
                summary.Display.WorkflowId,
                summary.Display.DisplayId,
                albumChild ? AlbumFileJobType : JobTypeLabel(summary.Display.Kind),
                message,
                level,
                displayKind,
                highlight: status);
        }
    }

    public ActivityLogEntry? Format(ActivityEventDto activity, DaemonClientStore store)
    {
        lock (sync)
        {
            var summary = activity.JobId is Guid jobId ? store.GetJob(jobId) : null;
            if (summary != null)
                RememberStructure(summary);

            return activity.Payload switch
            {
                JobMessageActivityDto message when summary != null =>
                    FormatJobMessage(summary, message),
                WorkflowMessageActivityDto message when activity.WorkflowId is Guid workflowId =>
                    FormatWorkflowMessage(workflowId, message),
                DiagnosticActivityDto diagnostic =>
                    FormatDiagnostic(activity, summary, diagnostic),
                // The input itself gets a line; the items of a list do not.
                ExtractionStartedActivityDto extraction when summary != null
                    && summary.Relationships.ParentJobId == null
                    && !string.IsNullOrWhiteSpace(extraction.InputType) =>
                    JobEntry(
                        summary.Display.WorkflowId,
                        extraction.DisplayId,
                        "ExtractJob",
                        $"Input: {extraction.Input}",
                        LogLevel.Information,
                        ActivityLogDisplayKind.Status,
                        extraction.Source ?? extraction.InputType),
                _ => null,
            };
        }
    }

    private ActivityLogEntry FormatJobMessage(
        JobSummaryDto summary,
        JobMessageActivityDto message)
    {
        var level = ParseLogLevel(message.Level);
        bool error = level >= LogLevel.Error;
        return JobEntry(
            summary.Display.WorkflowId,
            message.DisplayId,
            JobTypeLabel(summary.Display.Kind),
            message.Message,
            level,
            error ? ActivityLogDisplayKind.Failed : ActivityLogDisplayKind.Status,
            message.Source,
            error ? message.Message : null);
    }

    private static ActivityLogEntry FormatWorkflowMessage(
        Guid workflowId,
        WorkflowMessageActivityDto message)
    {
        var level = ParseLogLevel(message.Level);
        return new ActivityLogEntry(
            "Jobs",
            level >= LogLevel.Error ? ActivityLogSeverity.Error : ActivityLogSeverity.Information,
            level,
            $"{SourcePrefix(message.Source)}{message.Message}");
    }

    private ActivityLogEntry FormatDiagnostic(
        ActivityEventDto activity,
        JobSummaryDto? summary,
        DiagnosticActivityDto diagnostic)
    {
        string headline = ShortTypeName(diagnostic.ExceptionType, diagnostic.Scope);
        string body =
            $"diagnostic: {headline}\n  Exception:\n" +
            IndentContinuationLines(diagnostic.Exception, "    ");
        if (summary != null)
        {
            return JobEntry(
                summary.Display.WorkflowId,
                diagnostic.DisplayId ?? summary.Display.DisplayId,
                JobTypeLabel(summary.Display.Kind),
                body,
                LogLevel.Error,
                ActivityLogDisplayKind.Failed,
                diagnostic.Source,
                "diagnostic");
        }

        return new ActivityLogEntry(
            "Jobs",
            ActivityLogSeverity.Error,
            LogLevel.Error,
            $"Diagnostic error ({diagnostic.Scope}): {headline}\n  Exception:\n" +
            IndentContinuationLines(diagnostic.Exception, "    "));
    }

    private static ActivityLogEntry JobEntry(
        Guid workflowId,
        int displayId,
        string jobType,
        string message,
        LogLevel level,
        ActivityLogDisplayKind kind,
        string? source = null,
        string? highlight = null)
    {
        string formatted = $"[{displayId:000}] {jobType}: {SourcePrefix(source)}{message}";
        return new ActivityLogEntry(
            "Jobs",
            level >= LogLevel.Error ? ActivityLogSeverity.Error : ActivityLogSeverity.Information,
            level,
            formatted,
            // Notes about a job belong in the scrollback whether or not a live view
            // runs; the consumer drops Debug ones unless asked for them.
            new ActivityLogDisplay(
                displayId,
                jobType,
                message,
                kind,
                source,
                highlight));
    }

    // What finished: the query, plus the peer file a song came from, the file an
    // album track was, or the folder an album was saved to, when a transfer said so.
    private string TerminalDetail(JobSummaryDto summary, bool albumChild)
    {
        string name = summary.Display.QueryText ?? summary.Display.ItemName ?? "";
        transfers.Remove(summary.JobId, out TransferFacts? facts);
        if (albumChild)
        {
            // Keep the track's folder for its album's own line.
            if (facts?.LocalPath is { } local && summary.Relationships.ParentJobId is Guid albumId
                && Path.GetDirectoryName(local) is { Length: > 0 } folder)
                albumFolders[albumId] = albumFolders.TryGetValue(albumId, out string? known) ? CommonDirectory(known, folder) : folder;
            return facts?.RemotePath is { } remote ? WithName(name, FileName(remote)) : name;
        }
        if (summary.Display.Kind == ServerJobKind.Album)
            return albumFolders.Remove(summary.JobId, out string? saved) && saved.Length > 0 && IsSuccess(summary)
                ? WithName(name, $"completed at {saved}") : name;
        if (summary.Display.Kind == ServerJobKind.Song && IsSuccess(summary)
            && facts is { Username: { } user, RemotePath: { } path })
            return WithName(name, ShortPeerPath(user, path));
        return name;
    }

    private static bool IsSuccess(JobSummaryDto summary)
        => summary.Lifecycle.TerminalOutcome == JobTerminalOutcome.Succeeded;

    private static string WithName(string name, string detail)
        => string.IsNullOrWhiteSpace(name) || name == detail ? detail : $"{name}: {detail}";

    private static string FileName(string remotePath)
        => remotePath[(remotePath.LastIndexOfAny(['\\', '/']) + 1)..];

    // user\..\last two folders\file: peers' paths are long.
    private static string ShortPeerPath(string username, string remotePath)
    {
        string[] parts = remotePath.Replace('/', '\\').TrimStart('\\').Split('\\');
        return parts.Length > 3
            ? $@"{username}\..\{string.Join('\\', parts[^3..])}"
            : $@"{username}\{string.Join('\\', parts)}";
    }

    private static string CommonDirectory(string first, string second)
    {
        while (first.Length > 0
            && !(second + Path.DirectorySeparatorChar).StartsWith(first + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            first = Path.GetDirectoryName(first) ?? "";
        return first;
    }

    private void RememberStructure(JobSummaryDto summary)
    {
        jobKinds[summary.JobId] = summary.Display.Kind;
        if (summary.Relationships.ParentJobId is Guid parentId)
            parentJobIds[summary.JobId] = parentId;
        else
            parentJobIds.Remove(summary.JobId);
    }

    private bool IsAlbumChild(JobSummaryDto summary)
        => summary.Display.Kind == ServerJobKind.Song
            && summary.Relationships.ParentJobId is Guid parentId
            && jobKinds.GetValueOrDefault(parentId) == ServerJobKind.Album;

    private static ActivityLogDisplayKind DisplayKind(JobSummaryDto summary)
        => summary.Lifecycle.TerminalOutcome switch
        {
            JobTerminalOutcome.Succeeded => ActivityLogDisplayKind.Succeeded,
            JobTerminalOutcome.Skipped
                when summary.Lifecycle.SkipReason == JobSkipReason.AlreadyExists =>
                ActivityLogDisplayKind.AlreadyExists,
            JobTerminalOutcome.Skipped => ActivityLogDisplayKind.Skipped,
            JobTerminalOutcome.Cancelled => ActivityLogDisplayKind.Cancelled,
            JobTerminalOutcome.PartialSuccess => ActivityLogDisplayKind.Partial,
            JobTerminalOutcome.Failed => ActivityLogDisplayKind.Failed,
            _ => ActivityLogDisplayKind.Status,
        };

    private static ActivityLogDisplayKind AlbumTrackDisplayKind(ActivityLogDisplayKind kind)
        => kind switch
        {
            ActivityLogDisplayKind.Succeeded or ActivityLogDisplayKind.AlreadyExists =>
                ActivityLogDisplayKind.AlbumTrackSucceeded,
            ActivityLogDisplayKind.Skipped or ActivityLogDisplayKind.Cancelled =>
                ActivityLogDisplayKind.AlbumTrackSkipped,
            ActivityLogDisplayKind.Failed or ActivityLogDisplayKind.Partial =>
                ActivityLogDisplayKind.AlbumTrackFailed,
            _ => kind,
        };

    private static string StatusLabel(JobSummaryDto summary)
        => summary.Lifecycle.TerminalOutcome switch
        {
            JobTerminalOutcome.Succeeded => "succeeded",
            JobTerminalOutcome.Skipped
                when summary.Lifecycle.SkipReason == JobSkipReason.AlreadyExists =>
                "already exists",
            JobTerminalOutcome.Skipped
                when summary.Lifecycle.SkipReason == JobSkipReason.NotFoundLastTime =>
                "not found",
            JobTerminalOutcome.Skipped => "skipped",
            JobTerminalOutcome.Cancelled => "cancelled",
            JobTerminalOutcome.PartialSuccess => "partial",
            JobTerminalOutcome.Failed =>
                ServerFailureReasonDisplay.FailedLabel(summary.Lifecycle.FailureReason),
            _ => "terminal",
        };

    private static string JobTypeLabel(ServerJobKind kind)
        => kind switch
        {
            ServerJobKind.RetrieveFolder => "Retrieve Folder",
            ServerJobKind.JobList => "Job List",
            ServerJobKind.AlbumGroups => "Album Groups",
            ServerJobKind.SongGroups => "Song Groups",
            _ => $"{char.ToUpperInvariant(kind.ToWireString()[0])}{kind.ToWireString()[1..]}Job",
        };

    private static LogLevel ParseLogLevel(string level)
        => Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

    private static string SourcePrefix(string? source)
        => string.IsNullOrWhiteSpace(source) ? "" : $"{source}: ";

    private static string ShortTypeName(string? exceptionType, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(exceptionType) ? fallback : exceptionType;
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
    }

    private static string IndentContinuationLines(string value, string indent)
        => string.Join(
            '\n',
            value.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(line => indent + line));
}
