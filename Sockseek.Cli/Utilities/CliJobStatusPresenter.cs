using Sockseek.Api;

namespace Sockseek.Cli;

internal enum CliJobStatusCategory
{
    Queued,
    Active,
    Succeeded,
    Failed,
}

internal sealed record CliJobStatus(
    string Label,
    ConsoleColor Color,
    CliJobStatusCategory Category)
{
    public bool IsTerminal => Category is CliJobStatusCategory.Succeeded or CliJobStatusCategory.Failed;
    public bool IsSuccessful => Category == CliJobStatusCategory.Succeeded;
    public bool IsFailed => Category == CliJobStatusCategory.Failed;
    public bool IsQueued => Category == CliJobStatusCategory.Queued;
    public bool IsActive => Category == CliJobStatusCategory.Active;
}

internal static class CliJobStatusPresenter
{
    public static CliJobStatus ForSummary(JobSummaryDto summary, TransferStatusFieldsDto? transfer = null)
        => ForSplit(summary.Lifecycle.LifecycleState, summary.Lifecycle.ActivityPhase, summary.Lifecycle.TerminalOutcome, summary.Lifecycle.SkipReason, summary.Lifecycle.FailureReason, transfer?.Phase, transfer?.QueuePosition);

    public static CliJobStatus ForSplit(
        JobLifecycleState lifecycle,
        JobActivityPhase activity,
        JobTerminalOutcome outcome,
        JobSkipReason skipReason = JobSkipReason.None,
        JobFailureReason? failureReason = null,
        TransferPhase? transferPhase = null,
        int? queuePosition = null)
    {
        if (lifecycle == JobLifecycleState.Terminal)
            return Terminal(outcome, skipReason, failureReason);

        if (activity == JobActivityPhase.Downloading && TryTransferPhase(transferPhase, queuePosition, out var transfer))
            return transfer;

        return activity switch
        {
            JobActivityPhase.WaitingForSearchConcurrency => Active("waiting search", ConsoleColor.DarkGray),
            JobActivityPhase.SearchRateLimited => Active("rate limited", ConsoleColor.DarkGray),
            JobActivityPhase.Searching => Active("searching"),
            JobActivityPhase.ProcessingSearchResults => Active("processing results"),
            JobActivityPhase.Extracting => Active("extracting"),
            JobActivityPhase.Downloading => Active("downloading"),
            JobActivityPhase.RetrievingFolder => Active("retrieving folder"),
            JobActivityPhase.RunningChildren => Active("running"),
            JobActivityPhase.Organizing => Active("organizing"),
            JobActivityPhase.RunningOnComplete => Active("on-complete"),
            JobActivityPhase.RunningFallback => Active("fallback"),
            JobActivityPhase.SearchingFallback => Active("searching fallback"),
            JobActivityPhase.DownloadingFallback => Active("downloading fallback"),
            JobActivityPhase.ProcessingFallback => Active("processing fallback"),
            _ => lifecycle switch
            {
                JobLifecycleState.Pending => new("queued", ConsoleColor.Gray, CliJobStatusCategory.Queued),
                JobLifecycleState.AwaitingSelection => Active("awaiting selection"),
                _ => Active("running"),
            },
        };
    }

    public static string FailureReasonLabel(JobFailureReason? reason)
        => ServerFailureReasonDisplay.Label(reason);

    private static CliJobStatus Active(string label, ConsoleColor color = ConsoleColor.Cyan)
        => new(label, color, CliJobStatusCategory.Active);

    private static CliJobStatus Terminal(JobTerminalOutcome outcome, JobSkipReason skipReason, JobFailureReason? reason)
        => outcome switch
        {
            JobTerminalOutcome.Succeeded => new("succeeded", ConsoleColor.Green, CliJobStatusCategory.Succeeded),
            JobTerminalOutcome.Skipped when skipReason == JobSkipReason.AlreadyExists => new("already exists", ConsoleColor.Green, CliJobStatusCategory.Succeeded),
            JobTerminalOutcome.Skipped when skipReason == JobSkipReason.NotFoundLastTime => new("not found", ConsoleColor.DarkGray, CliJobStatusCategory.Succeeded),
            JobTerminalOutcome.Skipped => new("skipped", ConsoleColor.DarkGray, CliJobStatusCategory.Succeeded),
            JobTerminalOutcome.Cancelled => new("cancelled", ConsoleColor.DarkGray, CliJobStatusCategory.Failed),
            JobTerminalOutcome.PartialSuccess => new("partial", ConsoleColor.Yellow, CliJobStatusCategory.Failed),
            JobTerminalOutcome.Failed => new(FailedLabel(reason), ConsoleColor.Red, CliJobStatusCategory.Failed),
            _ => new("failed", ConsoleColor.Red, CliJobStatusCategory.Failed),
        };

    private static string FailedLabel(JobFailureReason? reason)
    {
        return ServerFailureReasonDisplay.FailedLabel(reason);
    }

    /// <summary>A transfer describes its job only while the job is downloading.</summary>
    private static bool TryTransferPhase(TransferPhase? phase, int? queuePosition, out CliJobStatus status)
    {
        status = phase switch
        {
            TransferPhase.QueuedRemotely => Active(queuePosition > 0 ? $"queued (R) #{queuePosition}" : "queued (R)"),
            TransferPhase.QueuedLocally => Active("queued (L)"),
            TransferPhase.QueuedForPeer => Active("waiting for peer"),
            TransferPhase.Starting or TransferPhase.Requested or TransferPhase.Initializing => Active("connecting"),
            TransferPhase.InProgress => Active("downloading"),
            TransferPhase.Finalizing => Active("finalizing"),
            TransferPhase.Retrying => Active("retrying"),
            _ => null!,
        };
        return status is not null;
    }
}
