using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core;

internal static partial class DownloadLogMessages
{
    [LoggerMessage(3000, LogLevel.Error, "Download event observer {ObserverName} failed")]
    internal static partial void ObserverFailed(ILogger logger, Exception exception, string observerName);

    [LoggerMessage(3001, LogLevel.Error, "Search event observer {ObserverName} failed for job {JobId}")]
    internal static partial void SearchObserverFailed(ILogger logger, Exception exception, string observerName, Guid jobId);

    [LoggerMessage(3002, LogLevel.Debug, "Download engine {EngineId}: {Stage}")]
    internal static partial void EngineStage(ILogger logger, string engineId, string stage);

    [LoggerMessage(3003, LogLevel.Debug, "Job {JobId}: {Decision} (count: {Count})")]
    internal static partial void JobDecision(ILogger logger, Guid jobId, string decision, int count);

    [LoggerMessage(3020, LogLevel.Debug, "Job {JobId}: {Decision}")]
    internal static partial void JobDecision(ILogger logger, Guid jobId, string decision);

    [LoggerMessage(3021, LogLevel.Debug, "Download {TransferId} for job {JobId}: {Outcome} ({BytesTransferred} bytes)")]
    internal static partial void TransferEnded(ILogger logger, Guid transferId, Guid jobId, TransferSnapshotTerminalOutcome outcome, long bytesTransferred);

    [LoggerMessage(3004, LogLevel.Error, "Download component {Component} failed for job {JobId}")]
    internal static partial void ComponentFailed(ILogger logger, Exception exception, string component, Guid? jobId);

    [LoggerMessage(3005, LogLevel.Debug, "Download cleanup {CleanupKind} failed ({FailureKind})")]
    internal static partial void CleanupFailed(ILogger logger, string cleanupKind, string failureKind);

    [LoggerMessage(3006, LogLevel.Debug, "File metadata could not be read ({FailureKind})")]
    internal static partial void MetadataReadFailed(ILogger logger, string failureKind);

    [LoggerMessage(3007, LogLevel.Warning, "Initial Soulseek session attempt failed; background reconnection remains active")]
    internal static partial void SessionInitializationDegraded(ILogger logger);

    [LoggerMessage(3008, LogLevel.Debug, "Download services initialized")]
    internal static partial void ServicesInitialized(ILogger logger);

    [LoggerMessage(3009, LogLevel.Debug, "Search completed for job {JobId} with {ResultCount} candidate files")]
    internal static partial void SearchCompleted(ILogger logger, Guid jobId, int resultCount);

    [LoggerMessage(3010, LogLevel.Error, "Remote folder completion failed for directory hash {DirectoryHash}")]
    internal static partial void FolderCompletionFailed(ILogger logger, Exception exception, string directoryHash);

    [LoggerMessage(3011, LogLevel.Debug, "Transfer {TransferId} attempt {AttemptCount}/{MaximumAttempts} failed for job {JobId}")]
    internal static partial void TransferAttemptFailed(ILogger logger, Exception exception, Guid transferId, int attemptCount, int maximumAttempts, Guid jobId);

    [LoggerMessage(3012, LogLevel.Information, "Started job {JobId} ({JobType})")]
    internal static partial void RootJobStarted(ILogger logger, Guid jobId, JobSnapshotKind jobType);

    internal static void RootJobFinished(ILogger logger, Job job, long durationMilliseconds)
    {
        var state = job.StateSnapshot;
        if (state.LifecycleState == JobLifecycleState.Running)
            RootJobUnsuccessful(logger, job.Id, durationMilliseconds, "execution stopped while the job was still running");
        else if (state.TerminalOutcome == JobTerminalOutcome.Failed)
            RootJobUnsuccessful(logger, job.Id, durationMilliseconds, state.FailureReason switch
            {
                JobFailureReason.ChildJobsFailed => "one or more child jobs failed",
                JobFailureReason.AllDownloadsFailed => "all downloads failed",
                JobFailureReason.NoSearchResults => "no search results",
                JobFailureReason.NoMatchingResults => "no matching results",
                JobFailureReason.OutOfDownloadRetries => "download retries exhausted",
                JobFailureReason.InvalidSearchString => "invalid search query",
                JobFailureReason.ExtractionFailed => "input extraction failed",
                _ => state.FailureReason.ToString(),
            });
        else if (job is SearchJob search && state.TerminalOutcome == JobTerminalOutcome.Succeeded)
            SearchJobCompleted(logger, job.Id, durationMilliseconds, search.ResultCount, search.Session.LockedFileCount, search.Session.ObservedPeerCount);
        else
            RootJobCompleted(logger, job.Id, durationMilliseconds, state.LifecycleState switch
            {
                JobLifecycleState.Terminal => state.TerminalOutcome.ToString(),
                JobLifecycleState.AwaitingSelection => "waiting for selection",
                _ => state.LifecycleState.ToString(),
            });
    }

    [LoggerMessage(3022, LogLevel.Information, "Search {JobId} finished in {DurationMilliseconds} ms: {PublicCount} public files, {LockedCount} locked files, {PeerCount} peers")]
    private static partial void SearchJobCompleted(ILogger logger, Guid jobId, long durationMilliseconds, int publicCount, int lockedCount, int peerCount);

    [LoggerMessage(3013, LogLevel.Information, "Job {JobId}: {Outcome} after {DurationMilliseconds} ms")]
    private static partial void RootJobCompleted(ILogger logger, Guid jobId, long durationMilliseconds, string outcome);

    [LoggerMessage(3014, LogLevel.Error, "Job {JobId} failed after {DurationMilliseconds} ms ({FailureReason})")]
    private static partial void RootJobUnsuccessful(ILogger logger, Guid jobId, long durationMilliseconds, string failureReason);

    [LoggerMessage(3015, LogLevel.Debug, "Retired terminal download workflow {WorkflowId} ({JobCount} jobs)")]
    internal static partial void WorkflowRetired(ILogger logger, Guid workflowId, int jobCount);

    [LoggerMessage(3016, LogLevel.Warning, "Output file {OperationId} publication failed; finalization will retry and report the outcome")]
    internal static partial void OutputPublicationDegraded(ILogger logger, Guid operationId);

    [LoggerMessage(3017, LogLevel.Information, "Output file {OperationId} publication recovered")]
    internal static partial void OutputPublicationRecovered(ILogger logger, Guid operationId);

    [LoggerMessage(3018, LogLevel.Information, "Output file {OperationId} finalized in {DurationMilliseconds} ms ({PublicationCount} writes)")]
    internal static partial void OutputPublicationCompleted(ILogger logger, Guid operationId, long durationMilliseconds, int publicationCount);

    [LoggerMessage(3019, LogLevel.Error, "Output file {OperationId} final publication failed for root {JobId}")]
    internal static partial void OutputPublicationFailed(ILogger logger, Exception exception, Guid operationId, Guid jobId);
}
