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
    private readonly object sync = new();

    public ActivityLogEntry? Format(JobSummaryDto summary)
    {
        lock (sync)
        {
            RememberStructure(summary);
            if (summary.LifecycleState != ServerJobLifecycleState.Terminal)
                return null;
            if (!loggedTerminalJobs.Add(summary.JobId))
                return null;

            var displayKind = DisplayKind(summary);
            var level = displayKind is ActivityLogDisplayKind.Failed
                or ActivityLogDisplayKind.Partial
                or ActivityLogDisplayKind.Cancelled
                ? LogLevel.Error
                : LogLevel.Information;
            string status = StatusLabel(summary);
            string detail = summary.QueryText ?? summary.ItemName ?? "";
            string message = string.IsNullOrWhiteSpace(detail)
                ? status
                : $"{status}: {detail}";
            if (!string.IsNullOrWhiteSpace(summary.FailureMessage))
                message += $"\n    Error: {summary.FailureMessage}";

            bool albumChild = IsAlbumChild(summary);
            if (albumChild)
                displayKind = AlbumTrackDisplayKind(displayKind);

            return JobEntry(
                summary.WorkflowId,
                summary.DisplayId,
                albumChild ? AlbumFileJobType : JobTypeLabel(summary.Kind),
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
                ExtractionStartedActivityDto extraction when summary != null
                    && !string.IsNullOrWhiteSpace(extraction.InputType) =>
                    JobEntry(
                        summary.WorkflowId,
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
            summary.WorkflowId,
            message.DisplayId,
            JobTypeLabel(summary.Kind),
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
                summary.WorkflowId,
                diagnostic.DisplayId ?? summary.DisplayId,
                JobTypeLabel(summary.Kind),
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
            new ActivityLogDisplay(
                displayId,
                jobType,
                message,
                kind,
                source,
                highlight,
                ShowInLive: kind != ActivityLogDisplayKind.Status || level >= LogLevel.Error));
    }

    private void RememberStructure(JobSummaryDto summary)
    {
        jobKinds[summary.JobId] = summary.Kind;
        if (summary.ParentJobId is Guid parentId)
            parentJobIds[summary.JobId] = parentId;
        else
            parentJobIds.Remove(summary.JobId);
    }

    private bool IsAlbumChild(JobSummaryDto summary)
        => summary.Kind == ServerJobKind.Song
            && summary.ParentJobId is Guid parentId
            && jobKinds.GetValueOrDefault(parentId) == ServerJobKind.Album;

    private static ActivityLogDisplayKind DisplayKind(JobSummaryDto summary)
        => summary.TerminalOutcome switch
        {
            ServerJobTerminalOutcome.Succeeded => ActivityLogDisplayKind.Succeeded,
            ServerJobTerminalOutcome.Skipped
                when summary.SkipReason == ServerJobSkipReason.AlreadyExists =>
                ActivityLogDisplayKind.AlreadyExists,
            ServerJobTerminalOutcome.Skipped => ActivityLogDisplayKind.Skipped,
            ServerJobTerminalOutcome.Cancelled => ActivityLogDisplayKind.Cancelled,
            ServerJobTerminalOutcome.PartialSuccess => ActivityLogDisplayKind.Partial,
            ServerJobTerminalOutcome.Failed => ActivityLogDisplayKind.Failed,
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
        => summary.TerminalOutcome switch
        {
            ServerJobTerminalOutcome.Succeeded => "succeeded",
            ServerJobTerminalOutcome.Skipped
                when summary.SkipReason == ServerJobSkipReason.AlreadyExists =>
                "already exists",
            ServerJobTerminalOutcome.Skipped
                when summary.SkipReason == ServerJobSkipReason.NotFoundLastTime =>
                "not found",
            ServerJobTerminalOutcome.Skipped => "skipped",
            ServerJobTerminalOutcome.Cancelled => "cancelled",
            ServerJobTerminalOutcome.PartialSuccess => "partial",
            ServerJobTerminalOutcome.Failed =>
                ServerFailureReasonDisplay.FailedLabel(summary.FailureReason),
            _ => "terminal",
        };

    private static string JobTypeLabel(ServerJobKind kind)
        => kind switch
        {
            ServerJobKind.RetrieveFolder => "Retrieve Folder",
            ServerJobKind.JobList => "Job List",
            ServerJobKind.AlbumAggregate => "Album Aggregate",
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
