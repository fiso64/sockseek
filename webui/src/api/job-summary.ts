import type { JobStateDto, JobSummaryDto } from './types';

/** One public-summary projection for live root and child presentations. */
export function jobStateSummary(job: JobStateDto): JobSummaryDto {
  return {
    jobId: job.jobId,
    displayId: job.display.displayId,
    workflowId: job.display.workflowId,
    kind: job.display.kind,
    lifecycleState: job.lifecycle.lifecycleState,
    activityPhase: job.lifecycle.activityPhase,
    activityUntilUtc: job.lifecycle.activityUntilUtc,
    terminalOutcome: job.lifecycle.terminalOutcome,
    skipReason: job.lifecycle.skipReason,
    itemName: job.display.itemName,
    queryText: job.display.queryText,
    failureReason: job.lifecycle.failureReason,
    failureMessage: job.lifecycle.failureMessage,
    parentJobId: job.relationships.parentJobId,
    resultJobId: job.relationships.resultJobId,
    sourceJobId: job.relationships.sourceJobId,
    discoveryRawResultCount: job.discovery.rawResultCount,
    discoveryLockedFileCount: job.discovery.lockedFileCount,
    discoveryPublicFileCount: job.discovery.publicFileCount,
    discoveryObservedPeerCount: job.discovery.observedPeerCount,
    appliedAutoProfiles: job.display.appliedAutoProfiles,
    availableActions: job.lifecycle.availableActions,
    failureDetail: job.lifecycle.failureDetail,
    cancellationSource: job.lifecycle.cancellationSource,
    printOption: job.display.printOption,
    submissionId: job.display.submissionId,
    role: job.display.role,
    createdAtUtc: job.display.createdAtUtc,
    searchDefaultProjection: job.display.searchDefaultProjection,
    searchAggregatePresentation: job.display.searchAggregatePresentation,
    stats: job.stats,
    sourceDetail: job.display.sourceDetail,
  };
}
