using Microsoft.Extensions.Logging;

namespace Sockseek.Server;

internal static partial class ServerLogMessages
{
    [LoggerMessage(4000, LogLevel.Warning, "Live batch observer failed")]
    internal static partial void LiveBatchObserverFailed(ILogger logger, Exception exception);

    [LoggerMessage(4001, LogLevel.Warning, "Live state transport send failed (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void LiveStateSendFailed(ILogger logger, Exception exception, long suppressedCount);

    [LoggerMessage(4002, LogLevel.Warning, "Live state transport send timed out (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void LiveStateSendTimedOut(ILogger logger, long suppressedCount);

    [LoggerMessage(4003, LogLevel.Warning, "Disabled chat callback failed (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void DisabledChatCallbackFailed(ILogger logger, Exception exception, long suppressedCount);

    [LoggerMessage(4004, LogLevel.Warning, "Disabled chat discard queue is full; the message remains replayable (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void DisabledChatQueueFull(ILogger logger, long suppressedCount);

    [LoggerMessage(4005, LogLevel.Warning, "Discarded private-message acknowledgement failed (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void DisabledChatAcknowledgementFailed(ILogger logger, Exception exception, long suppressedCount);

    [LoggerMessage(4006, LogLevel.Error, "Download engine instance failed; restarting supervisor loop (restart {RestartCount})")]
    internal static partial void EngineRestarting(ILogger logger, Exception exception, int restartCount);

    [LoggerMessage(4007, LogLevel.Warning, "Chat retention projection failed")]
    internal static partial void ChatRetentionProjectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(4008, LogLevel.Error, "Persistence drain timed out; runtime {RuntimeId} remains unfinished for startup reconciliation")]
    internal static partial void PersistenceDrainTimedOut(ILogger logger, Guid? runtimeId);

    [LoggerMessage(4009, LogLevel.Information, "Persistence retention pruned {PrunedJobs} jobs, {PrunedSearchResults} raw search results, {PrunedTransfers} transfers, and {PrunedChatMessages} chat messages in {DurationMilliseconds} ms")]
    internal static partial void PersistenceRetentionCompleted(ILogger logger, int prunedJobs, int prunedSearchResults, int prunedTransfers, int prunedChatMessages, long durationMilliseconds);

    [LoggerMessage(4010, LogLevel.Error, "Scheduled persistence retention failed")]
    internal static partial void PersistenceRetentionFailed(ILogger logger, Exception exception);

    [LoggerMessage(4011, LogLevel.Critical, "Unhandled server startup or runtime error")]
    internal static partial void UnhandledServerError(ILogger logger, Exception exception);

    [LoggerMessage(4012, LogLevel.Warning, "Request for {Feature} could not start because the feature is unavailable")]
    internal static partial void FeatureRequestUnavailable(ILogger logger, string feature);

    [LoggerMessage(4013, LogLevel.Error, "Historical state handoff failed for workflow {WorkflowId}; its retained history is unavailable")]
    internal static partial void PersistenceHandoffFailed(ILogger logger, Guid workflowId, Exception exception);

    [LoggerMessage(4014, LogLevel.Error, "Job projection retirement observer failed for workflow {WorkflowId}")]
    internal static partial void JobProjectionRetirementObserverFailed(ILogger logger, Guid workflowId, Exception exception);

    [LoggerMessage(4015, LogLevel.Warning, "Transfer totals read {ReadId} failed after {DurationMs} ms; groups={GroupCount}, suppressed={SuppressedCount}; file rows remain available")]
    internal static partial void TransferGroupReadFailed(ILogger logger, Exception exception,
        string readId, int groupCount, long durationMs, long suppressedCount);

    [LoggerMessage(4500, LogLevel.Warning, "Input artifact storage is unavailable; browser file inputs are disabled while ordinary local inputs remain available")]
    internal static partial void InputArtifactUnavailable(ILogger logger, Exception? exception = null);

    [LoggerMessage(4501, LogLevel.Information, "Input artifact {ArtifactId} uploaded in {DurationMs} ms; bytes={ByteCount}")]
    internal static partial void InputArtifactUploaded(ILogger logger, string artifactId, long durationMs, long byteCount);

    [LoggerMessage(4510, LogLevel.Warning, "Job Preview storage is unavailable; Review is disabled while direct Start remains available")]
    internal static partial void JobPreviewUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(4511, LogLevel.Warning, "Job Preview maintenance was degraded; Review remains available")]
    internal static partial void JobPreviewMaintenanceDegraded(ILogger logger, Exception exception);

    [LoggerMessage(4512, LogLevel.Information, "Job Preview {PreviewId} expired with outcome Expired")]
    internal static partial void JobPreviewExpired(ILogger logger, Guid previewId);

    [LoggerMessage(4513, LogLevel.Information, "Job Preview {PreviewId} created with outcome Planning")]
    internal static partial void JobPreviewCreated(ILogger logger, Guid previewId);

    [LoggerMessage(4514, LogLevel.Error, "Job Preview {PreviewId} planning worker failed to load its durable request")]
    internal static partial void JobPreviewWorkLoadFailed(ILogger logger, Exception exception, Guid previewId);

    [LoggerMessage(4515, LogLevel.Information, "Job Preview {PreviewId} completed with outcome {Outcome} in {DurationMs} ms; nodes={NodeCount}, ready={ReadyCount}, failed={FailedCount}")]
    internal static partial void JobPreviewCompleted(ILogger logger, Guid previewId, string outcome, long durationMs, int nodeCount, int readyCount, int failedCount);

    [LoggerMessage(4516, LogLevel.Error, "Job Preview {PreviewId} failed after {DurationMs} ms; nodes={NodeCount}")]
    internal static partial void JobPreviewFailed(ILogger logger, Exception exception, Guid previewId, long durationMs, int nodeCount);

    [LoggerMessage(4517, LogLevel.Debug, "Job Preview {PreviewId} completion observer failed")]
    internal static partial void JobPreviewObserverFailed(ILogger logger, Exception exception, Guid previewId);

    [LoggerMessage(4518, LogLevel.Warning, "Job Preview {PreviewId} accepted submission {SubmissionId} but its committed state could not be published")]
    internal static partial void JobPreviewCommitPublicationFailed(ILogger logger, Guid previewId, Guid submissionId);

    [LoggerMessage(4519, LogLevel.Information, "Job Preview {PreviewId} committed as submission {SubmissionId} in {DurationMs} ms; requested={RequestedCount}, submitted={SubmittedCount}, rejected={RejectedCount}")]
    internal static partial void JobPreviewCommitted(ILogger logger, Guid previewId, Guid submissionId, long durationMs, int requestedCount, int submittedCount, int rejectedCount);

    [LoggerMessage(4520, LogLevel.Warning, "Job Preview {PreviewId} was released but artifact pin cleanup was degraded")]
    internal static partial void JobPreviewArtifactCleanupDegraded(ILogger logger, Exception exception, Guid previewId);

    [LoggerMessage(4532, LogLevel.Information, "Search View {ViewId} for job {JobId} created with outcome Live")]
    internal static partial void SearchViewCreated(ILogger logger, Guid viewId, Guid jobId);

    [LoggerMessage(4533, LogLevel.Information, "Search View {ViewId} revision {ViewRevision} committed in {DurationMs} ms; requested={RequestedCount}, submitted={SubmittedCount}, rejected={RejectedCount}")]
    internal static partial void SearchViewCommitted(ILogger logger, Guid viewId, long viewRevision, long durationMs, long requestedCount, long submittedCount, long rejectedCount);

    [LoggerMessage(4534, LogLevel.Information, "Search View {ViewId} completed with outcome {Outcome}; activeRuntime={DurationMs} ms, revision={Revision}, public={PublicCount}, locked={LockedCount}, projected={ProjectedCount}")]
    internal static partial void SearchViewCompleted(ILogger logger, Guid viewId, string outcome, long durationMs, long revision, long publicCount, long lockedCount, long projectedCount);

    [LoggerMessage(4535, LogLevel.Error, "Search View {ViewId} completed with outcome Incomplete after projection failure")]
    internal static partial void SearchViewProjectionFailed(ILogger logger, Exception exception, Guid viewId);

    [LoggerMessage(4536, LogLevel.Warning, "Search View {ViewId} incomplete state could not be published; its last snapshot remains readable")]
    internal static partial void SearchViewIncompleteStateFailed(ILogger logger, Exception exception, Guid viewId);

    [LoggerMessage(4537, LogLevel.Debug, "Search View {ViewId} revision {Revision} observer failed")]
    internal static partial void SearchViewObserverFailed(ILogger logger, Exception exception, Guid viewId, long revision);

    [LoggerMessage(4538, LogLevel.Error, "Search View {ViewId} projection restoration failed with outcome Incomplete")]
    internal static partial void SearchViewRestoreFailed(ILogger logger, Exception exception, Guid viewId);

    [LoggerMessage(4541, LogLevel.Warning, "Search View {ViewId} could not publish its restoration outcome; its last snapshot remains readable")]
    internal static partial void SearchViewRestorePublicationFailed(ILogger logger, Exception exception, Guid viewId);

    [LoggerMessage(4542, LogLevel.Error, "Search View {ViewId} could not replay retrieval observation {ObservationId}")]
    internal static partial void SearchViewRetrievalReplayFailed(ILogger logger, Exception exception, Guid viewId, Guid observationId);

    [LoggerMessage(4550, LogLevel.Information, "Peer restriction configured baselines reloaded")]
    internal static partial void PeerRestrictionsReloaded(ILogger logger);

    [LoggerMessage(4551, LogLevel.Warning, "Peer restriction configured baseline reload was rejected; the prior snapshot remains active")]
    internal static partial void PeerRestrictionsReloadRejected(ILogger logger, Exception exception);

    [LoggerMessage(4552, LogLevel.Warning, "Peer restriction override persistence is unavailable; configured restriction baselines remain active")]
    internal static partial void PeerRestrictionsUnavailable(ILogger logger);

    [LoggerMessage(4553, LogLevel.Information, "Peer restrictions initialized with {OverrideCount} durable username overrides")]
    internal static partial void PeerRestrictionsInitialized(ILogger logger, int overrideCount);

    [LoggerMessage(4554, LogLevel.Warning, "Peer restriction overrides are unavailable; configured restriction baselines remain active")]
    internal static partial void PeerRestrictionsInitializationFailed(ILogger logger, Exception exception);

    [LoggerMessage(4555, LogLevel.Error, "Peer restriction mutation {OperationId} for user {UserHash} failed after {DurationMs} ms; runtime policy was not changed")]
    internal static partial void PeerRestrictionMutationFailed(ILogger logger, Exception exception, string operationId, string userHash, long durationMs);

    [LoggerMessage(4556, LogLevel.Information, "Peer restriction mutation {OperationId} for user {UserHash} completed with outcome Applied after {DurationMs} ms; kind={Kind}, override={Override}, blocked={Blocked}")]
    internal static partial void PeerRestrictionMutationApplied(ILogger logger, string operationId, string userHash, long durationMs, string kind, string @override, bool blocked);

    [LoggerMessage(4560, LogLevel.Warning, "Dashboard transfer accounting is unavailable; suppressedWarnings={SuppressedWarnings}")]
    internal static partial void DashboardAccountingUnavailable(ILogger logger, Exception exception, int suppressedWarnings);

    [LoggerMessage(4562, LogLevel.Error, "Bulk transfer cancellation failed for transfer {TransferId}")]
    internal static partial void BulkTransferCancellationFailed(ILogger logger, Exception exception, Guid transferId);

    [LoggerMessage(4563, LogLevel.Warning, "Submission {SubmissionId} could not release its temporary input-artifact references")]
    internal static partial void SubmissionArtifactReleaseFailed(ILogger logger, Exception exception, Guid submissionId);

    [LoggerMessage(4565, LogLevel.Error, "Terminal transfer commit observer failed")]
    internal static partial void TerminalTransferCommitObserverFailed(ILogger logger, Exception exception);

    [LoggerMessage(4566, LogLevel.Information, "Submission commit {OperationId} completed in {DurationMs} ms")]
    internal static partial void SubmissionCommitCompleted(ILogger logger, Guid operationId, long durationMs);

    [LoggerMessage(4567, LogLevel.Information, "Workflow cancellation {WorkflowId} started")]
    internal static partial void WorkflowCancellationStarted(ILogger logger, Guid workflowId);

    [LoggerMessage(4568, LogLevel.Information, "Workflow cancellation {WorkflowId} completed in {DurationMs} ms; cancelled={CancelledCount}")]
    internal static partial void WorkflowCancellationCompleted(ILogger logger, Guid workflowId, long durationMs, int cancelledCount);

    [LoggerMessage(4569, LogLevel.Information, "Submission {SourceSubmissionId} rerun accepted as {SubmissionId} in {DurationMs} ms")]
    internal static partial void SubmissionRerunAccepted(ILogger logger, Guid sourceSubmissionId, Guid submissionId, long durationMs);
    [LoggerMessage(4570, LogLevel.Error, "Search source observer failed for source {SourceJobId}")]
    internal static partial void SearchSourceObserverFailed(ILogger logger, Exception exception, Guid sourceJobId);

    [LoggerMessage(4571, LogLevel.Debug, "Job Preview {PreviewId} timing: planning={PlanningMs} ms, capture={CaptureMs} ms, spool={SpoolMs} ms")]
    internal static partial void JobPreviewPlanningTiming(ILogger logger, Guid previewId, long planningMs, long captureMs, long spoolMs);

    [LoggerMessage(4572, LogLevel.Debug, "Job Preview {PreviewId} commit timing: resolve={ResolveMs} ms, materialize={MaterializeMs} ms, claim={ClaimMs} ms, queue={QueueMs} ms, publish={PublishMs} ms")]
    internal static partial void JobPreviewCommitTiming(ILogger logger, Guid previewId, long resolveMs, long materializeMs, long claimMs, long queueMs, long publishMs);

    [LoggerMessage(4573, LogLevel.Debug, "Search View {ViewId} revision {ViewRevision} commit timing: resolution={ResolutionMs} ms (choice={ChoiceMs} ms, plan={PlanMs} ms), handoff={HandoffMs} ms")]
    internal static partial void SearchViewCommitTiming(ILogger logger, Guid viewId, long viewRevision, long resolutionMs, long choiceMs, long planMs, long handoffMs);

    [LoggerMessage(4574, LogLevel.Debug, "Search View {ViewId} timing: projection={ProjectionMs} ms, publication={PublicationMs} ms")]
    internal static partial void SearchViewCompletionTiming(ILogger logger, Guid viewId, long projectionMs, long publicationMs);

    [LoggerMessage(4575, LogLevel.Debug, "Submission commit {OperationId} timing: lookup={LookupMs} ms, execution={ExecutionMs} ms, receipt-encoding={ReceiptEncodingMs} ms")]
    internal static partial void SubmissionCommitTiming(ILogger logger, Guid operationId, long lookupMs, long executionMs, long receiptEncodingMs);

    [LoggerMessage(4576, LogLevel.Debug, "Submission {SourceSubmissionId} rerun as {SubmissionId} timing: read={ReadMs} ms, materialize={MaterializeMs} ms, admission={AdmissionMs} ms")]
    internal static partial void SubmissionRerunTiming(ILogger logger, Guid sourceSubmissionId, Guid submissionId, long readMs, long materializeMs, long admissionMs);

}
