using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;

public enum ExistingFileSource { CompletionHistory, Library, Destination }

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
    public bool ShouldUpdateDownloadPath { get; private init; }
    public string? DownloadPath { get; private init; }
    public FileCandidate? ChosenCandidate { get; private init; }
    public SongDownloadSource DownloadSource { get; private init; }
    public ExistingFileSource? ExistingFileSource { get; private init; }
    public bool Reused { get; private init; }

    internal JobOutcome WithDownloadMetadata(string? path, FileCandidate? candidate = null,
        SongDownloadSource source = SongDownloadSource.None)
        => this with { ShouldUpdateDownloadPath = path != null, DownloadPath = path,
            ChosenCandidate = candidate, DownloadSource = source };

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
        bool shouldUpdateDownloadPath = false,
        string? downloadPath = null,
        FileCandidate? chosenCandidate = null,
        SongDownloadSource downloadSource = SongDownloadSource.None)
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
        ShouldUpdateDownloadPath = shouldUpdateDownloadPath;
        DownloadPath = downloadPath;
        ChosenCandidate = chosenCandidate;
        DownloadSource = downloadSource;
    }

    public bool IsTerminal => TerminalOutcome != JobTerminalOutcome.None;

    public static JobOutcome NoChange()
        => new(shouldCommit: false);

    public static JobOutcome AwaitingSelection()
        => new(shouldCommit: true, lifecycleState: JobLifecycleState.AwaitingSelection);

    public static JobOutcome Activity(JobActivityPhase phase)
        => new(shouldCommit: true, lifecycleState: JobLifecycleState.Running, activityPhase: phase);

    public static JobOutcome Done(
        string? downloadPath = null,
        FileCandidate? chosenCandidate = null,
        SongDownloadSource downloadSource = SongDownloadSource.None,
        bool reused = false)
        => new JobOutcome(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.Succeeded,
            shouldUpdateDownloadPath: downloadPath != null,
            downloadPath: downloadPath,
            chosenCandidate: chosenCandidate,
            downloadSource: downloadSource) { Reused = reused };

    public static JobOutcome Failed(
        JobFailureReason reason,
        string? message = null,
        string? detail = null,
        string? downloadPath = null,
        bool clearDownloadPath = false)
    {
        if (reason == JobFailureReason.Cancelled)
            throw new ArgumentException("Use JobOutcome.Cancelled(source) for cancellation outcomes.", nameof(reason));
        if (downloadPath != null && clearDownloadPath)
            throw new ArgumentException("A failure outcome cannot both set and clear the download path.", nameof(clearDownloadPath));

        return new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.Failed,
            failureReason: reason,
            failureMessage: message,
            failureDetail: detail,
            shouldUpdateDownloadPath: downloadPath != null || clearDownloadPath,
            downloadPath: downloadPath);
    }

    public static JobOutcome Cancelled(JobCancellationSource source, string? message = null, string? detail = null, string? downloadPath = null)
    {
        if (source == JobCancellationSource.None)
            throw new ArgumentException("Cancellation outcomes must include a non-None source.", nameof(source));

        return new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.Cancelled,
            cancellationSource: source,
            failureReason: JobFailureReason.Cancelled,
            failureMessage: message,
            failureDetail: detail,
            shouldUpdateDownloadPath: downloadPath != null,
            downloadPath: downloadPath);
    }

    public static JobOutcome AlreadyExists(string? downloadPath = null,
        ExistingFileSource source = global::Sockseek.Core.Jobs.ExistingFileSource.Destination)
        => Skipped(JobSkipReason.AlreadyExists, JobFailureReason.None, downloadPath) with { ExistingFileSource = source };

    public static JobOutcome PartialSuccess(
        string? message = null,
        JobCancellationSource cancellationSource = JobCancellationSource.None,
        string? downloadPath = null)
        => new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.PartialSuccess,
            cancellationSource: cancellationSource,
            failureReason: JobFailureReason.Other,
            failureMessage: message,
            shouldUpdateDownloadPath: downloadPath != null,
            downloadPath: downloadPath);

    public static JobOutcome Skipped(JobSkipReason skipReason = JobSkipReason.None, JobFailureReason reason = JobFailureReason.None, string? downloadPath = null)
        => new(
            shouldCommit: true,
            terminalOutcome: JobTerminalOutcome.Skipped,
            skipReason: skipReason,
            failureReason: reason,
            shouldUpdateDownloadPath: downloadPath != null,
            downloadPath: downloadPath);

}
