using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;

// JobOutcome describes the job state transition produced by execution.
// It should not carry post-commit side effects such as index updates,
// playlist writes, file organization, event emission, or on-complete hooks.
// Those belong in orchestration after the outcome is committed.
public sealed record JobOutcome
{
    public bool ShouldCommit { get; }
    public JobLifecycleState? LifecycleState { get; }
    public JobActivityPhase? ActivityPhase { get; }
    public JobTerminalOutcome TerminalOutcome { get; }
    public JobSkipReason SkipReason { get; }
    public JobCancellationSource CancellationSource { get; }
    public JobFailureReason FailureReason { get; }
    public string? FailureMessage { get; }
    public string? FailureDetail { get; }
    public string? DownloadPath { get; }
    public FileCandidate? ChosenCandidate { get; }

    private JobOutcome(
        bool shouldCommit,
        JobLifecycleState? lifecycleState = null,
        JobActivityPhase? activityPhase = null,
        JobTerminalOutcome terminalOutcome = JobTerminalOutcome.None,
        JobSkipReason skipReason = JobSkipReason.None,
        JobCancellationSource cancellationSource = JobCancellationSource.None,
        JobFailureReason failureReason = JobFailureReason.None,
        string? failureMessage = null,
        string? failureDetail = null,
        string? downloadPath = null,
        FileCandidate? chosenCandidate = null)
    {
        ShouldCommit = shouldCommit;
        LifecycleState = lifecycleState;
        ActivityPhase = activityPhase;
        TerminalOutcome = terminalOutcome;
        SkipReason = skipReason;
        CancellationSource = cancellationSource;
        FailureReason = failureReason;
        FailureMessage = failureMessage;
        FailureDetail = failureDetail;
        DownloadPath = downloadPath;
        ChosenCandidate = chosenCandidate;
    }

    public bool IsTerminal => TerminalOutcome != JobTerminalOutcome.None;

    public static JobOutcome NoChange()
        => new(shouldCommit: false);

    public static JobOutcome AwaitingSelection()
        => new(shouldCommit: true, lifecycleState: JobLifecycleState.AwaitingSelection);

    public static JobOutcome Activity(JobActivityPhase phase)
        => new(shouldCommit: true, lifecycleState: JobLifecycleState.Running, activityPhase: phase);

    public static JobOutcome Done(string? downloadPath = null, FileCandidate? chosenCandidate = null)
        => new(shouldCommit: true, terminalOutcome: JobTerminalOutcome.Succeeded, downloadPath: downloadPath, chosenCandidate: chosenCandidate);

    public static JobOutcome Failed(JobFailureReason reason, string? message = null, string? detail = null)
    {
        if (reason == JobFailureReason.Cancelled)
            throw new ArgumentException("Use JobOutcome.Cancelled(source) for cancellation outcomes.", nameof(reason));

        return new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.Failed,
            failureReason: reason,
            failureMessage: message,
            failureDetail: detail);
    }

    public static JobOutcome Cancelled(JobCancellationSource source, string? message = null, string? detail = null)
    {
        if (source == JobCancellationSource.None)
            throw new ArgumentException("Cancellation outcomes must include a non-None source.", nameof(source));

        return new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.Cancelled,
            cancellationSource: source,
            failureReason: JobFailureReason.Cancelled,
            failureMessage: message,
            failureDetail: detail);
    }

    public static JobOutcome AlreadyExists(string? downloadPath = null)
        => Skipped(JobSkipReason.AlreadyExists, JobFailureReason.None, downloadPath);

    public static JobOutcome PartialSuccess(
        string? message = null,
        JobCancellationSource cancellationSource = JobCancellationSource.None)
        => new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.PartialSuccess,
            cancellationSource: cancellationSource,
            failureReason: JobFailureReason.Other,
            failureMessage: message);

    public static JobOutcome Skipped(JobSkipReason skipReason = JobSkipReason.None, JobFailureReason reason = JobFailureReason.None, string? downloadPath = null)
        => new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.Skipped,
            skipReason: skipReason,
            failureReason: reason,
            downloadPath: downloadPath);

}
