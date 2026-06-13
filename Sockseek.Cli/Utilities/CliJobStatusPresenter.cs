using Sockseek.Api;
using Soulseek;

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
    public static CliJobStatus ForSummary(JobSummaryDto summary, string? transferState = null)
        => ForSplit(summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, transferState);

    public static CliJobStatus ForSongPayload(SongJobPayloadDto song, string? transferStateOverride = null)
    {
        var lifecycle = song.LifecycleState ?? ServerJobLifecycleState.Pending;
        var activity = song.ActivityPhase ?? ServerJobActivityPhase.None;
        var outcome = song.TerminalOutcome ?? ServerJobTerminalOutcome.None;
        var skipReason = song.SkipReason ?? ServerJobSkipReason.None;
        return ForSplit(lifecycle, activity, outcome, skipReason, song.FailureReason, transferStateOverride ?? song.TransferState);
    }

    public static CliJobStatus ForSongEvent(SongStateChangedEventDto song)
        => ForSplit(song.LifecycleState, song.ActivityPhase, song.TerminalOutcome, song.SkipReason, song.FailureReason);

    public static CliJobStatus ForSplit(
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity,
        ServerJobTerminalOutcome outcome,
        ServerJobSkipReason skipReason = ServerJobSkipReason.None,
        ServerJobFailureReason? failureReason = null,
        string? transferState = null)
    {
        if (lifecycle == ServerJobLifecycleState.Terminal)
            return Terminal(outcome, skipReason, failureReason);

        if (TryTransferState(transferState, out var transfer))
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
            ServerJobActivityPhase.RunningOnComplete => Active("running hook"),
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

    private static bool TryTransferState(string? raw, out CliJobStatus status)
    {
        status = default!;
        if (raw == null || !Enum.TryParse<TransferStates>(raw, out var state) || state == TransferStates.None)
            return false;

        status = state switch
        {
            var s when s.HasFlag(TransferStates.InProgress) => Active("downloading"),
            var s when s.HasFlag(TransferStates.Queued) && s.HasFlag(TransferStates.Remotely) => Active("queued (R)"),
            var s when s.HasFlag(TransferStates.Queued) && s.HasFlag(TransferStates.Locally) => Active("queued (L)"),
            var s when s.HasFlag(TransferStates.Initializing) => Active("initialising"),
            var s when s.HasFlag(TransferStates.TimedOut) => new("timed out", ConsoleColor.Red, CliJobStatusCategory.Failed),
            _ => Active(state.ToString()),
        };
        return true;
    }
}
