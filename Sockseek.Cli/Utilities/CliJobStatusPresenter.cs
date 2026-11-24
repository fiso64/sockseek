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
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity,
        ServerJobTerminalOutcome outcome,
        ServerJobSkipReason skipReason = ServerJobSkipReason.None,
        ServerJobFailureReason? failureReason = null,
        TransferPhase? transferPhase = null,
        int? queuePosition = null)
    {
        if (lifecycle == ServerJobLifecycleState.Terminal)
            return Terminal(outcome, skipReason, failureReason);

        if (activity == ServerJobActivityPhase.Downloading && TryTransferPhase(transferPhase, queuePosition, out var transfer))
            return transfer;

        return activity switch
        {
            ServerJobActivityPhase.WaitingForSearchConcurrency => Active("waiting search", ConsoleColor.DarkGray),
            ServerJobActivityPhase.SearchRateLimited => Active("rate limited", ConsoleColor.DarkGray),
            ServerJobActivityPhase.Searching => Active("searching"),
            ServerJobActivityPhase.ProcessingSearchResults => Active("processing results"),
            ServerJobActivityPhase.Extracting => Active("extracting"),
            ServerJobActivityPhase.Downloading => Active("downloading"),
            ServerJobActivityPhase.RetrievingFolder => Active("retrieving folder"),
            ServerJobActivityPhase.RunningChildren => Active("running"),
            ServerJobActivityPhase.Organizing => Active("organizing"),
            ServerJobActivityPhase.RunningOnComplete => Active("on-complete"),
            ServerJobActivityPhase.RunningFallback => Active("fallback"),
            ServerJobActivityPhase.SearchingFallback => Active("searching fallback"),
            ServerJobActivityPhase.DownloadingFallback => Active("downloading fallback"),
            ServerJobActivityPhase.ProcessingFallback => Active("processing fallback"),
            _ => lifecycle switch
            {
                ServerJobLifecycleState.Pending => new("queued", ConsoleColor.Gray, CliJobStatusCategory.Queued),
                ServerJobLifecycleState.AwaitingSelection => Active("awaiting selection"),
                _ => Active("running"),
            },
        };
    }

    public static string FailureReasonLabel(ServerJobFailureReason? reason)
        => ServerFailureReasonDisplay.Label(reason);

    private static CliJobStatus Active(string label, ConsoleColor color = ConsoleColor.Cyan)
        => new(label, color, CliJobStatusCategory.Active);

    private static CliJobStatus Terminal(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason, ServerJobFailureReason? reason)
        => outcome switch
        {
            ServerJobTerminalOutcome.Succeeded => new("succeeded", ConsoleColor.Green, CliJobStatusCategory.Succeeded),
            ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.AlreadyExists => new("already exists", ConsoleColor.Green, CliJobStatusCategory.Succeeded),
            ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.NotFoundLastTime => new("not found", ConsoleColor.DarkGray, CliJobStatusCategory.Succeeded),
            ServerJobTerminalOutcome.Skipped => new("skipped", ConsoleColor.DarkGray, CliJobStatusCategory.Succeeded),
            ServerJobTerminalOutcome.Cancelled => new("cancelled", ConsoleColor.DarkGray, CliJobStatusCategory.Failed),
            ServerJobTerminalOutcome.PartialSuccess => new("partial", ConsoleColor.Yellow, CliJobStatusCategory.Failed),
            ServerJobTerminalOutcome.Failed => new(FailedLabel(reason), ConsoleColor.Red, CliJobStatusCategory.Failed),
            _ => new("failed", ConsoleColor.Red, CliJobStatusCategory.Failed),
        };

    private static string FailedLabel(ServerJobFailureReason? reason)
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
