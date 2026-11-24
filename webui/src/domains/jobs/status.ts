import type { JobSummaryDto, Schema, TransferStateDto } from '../../api/types';
import { transferPhaseLabel, type TransferPresentation } from '../presentation/items';

const resumeTimeFormat = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** File/folder cards use the same activity and outcome colours as transfer history. */
export function jobTransferTone(job: JobSummaryDto | null | undefined): TransferPresentation['tone'] {
  if (!job || job.lifecycle.lifecycleState === 'Pending') return undefined;
  if (job.lifecycle.lifecycleState !== 'Terminal') return 'active';
  switch (job.lifecycle.terminalOutcome) {
    case 'Succeeded': return 'complete';
    case 'Cancelled': return 'cancelled';
    case 'Failed': return 'failed';
    case 'PartialSuccess': return 'partial';
    default: return undefined;
  }
}

/** A single-file job's own transfer names what its download is doing. */
export function jobStatusLabel(job: JobSummaryDto, transfer?: TransferStateDto): string {
  if (job.lifecycle.lifecycleState === 'Terminal') {
    switch (job.lifecycle.terminalOutcome) {
      case 'Succeeded': return 'Complete';
      case 'PartialSuccess': return 'Partial';
      case 'Failed': return 'Failed';
      case 'Skipped': return 'Skipped';
      case 'Cancelled': return 'Cancelled';
      default: return 'Finished';
    }
  }
  if (job.lifecycle.lifecycleState === 'AwaitingSelection') return 'Awaiting selection';
  if (job.lifecycle.lifecycleState !== 'Running') return 'Pending';
  switch (job.lifecycle.activityPhase) {
    case 'WaitingForSearchConcurrency': return 'Waiting for search capacity';
    case 'SearchRateLimited': return job.lifecycle.activityUntilUtc
      ? `Search resumes at ${resumeTimeFormat.format(new Date(job.lifecycle.activityUntilUtc))}`
      : 'Search rate limited';
    case 'Searching': return 'Searching';
    case 'ProcessingSearchResults': return job.display.kind === 'search' ? 'Preparing results' : 'Choosing results';
    case 'Extracting': return 'Importing';
    case 'Downloading': return transfer && !transfer.status.isTerminal
      ? transferPhaseLabel(transfer.status.phase, 'download', transfer.identity.source === 'Fallback') : 'Downloading';
    case 'RetrievingFolder': return 'Reading folder';
    case 'RunningChildren': return 'Running';
    case 'Organizing': return 'Organizing files';
    case 'RunningOnComplete': return 'Running completion command';
    case 'RunningFallback': return 'Running fallback';
    case 'SearchingFallback': return 'Searching fallback';
    case 'DownloadingFallback': return 'Downloading fallback';
    case 'ProcessingFallback': return 'Processing fallback';
    default: return 'Running';
  }
}

/** Reasons supplement the status badge; stale discovery counts do not describe an outcome. */
export function jobOutcomeDetail(job: JobSummaryDto): string {
  if (job.lifecycle.lifecycleState !== 'Terminal') return '';
  if (job.lifecycle.terminalOutcome === 'Skipped') {
    const reason = jobSkipExplanation(job);
    return reason === 'Skipped' ? '' : reason;
  }
  if (job.lifecycle.terminalOutcome === 'Succeeded') {
    if (job.display.kind === 'extract' && !job.relationships.resultJobId) return 'No job produced';
    return '';
  }
  if (job.lifecycle.terminalOutcome === 'Cancelled') {
    switch (job.lifecycle.cancellationSource) {
      case 'ParentJob': return 'Parent job cancelled';
      case 'UserRequestedJob': return 'Cancelled by you';
      case 'UserRequestedWorkflow': return 'Workflow cancelled';
      case 'UserRequestedAllJobs': return 'All jobs cancelled';
      case 'InternalEngine': return 'Cancelled by the engine';
      default: return '';
    }
  }
  if (job.lifecycle.terminalOutcome !== 'Failed' && job.lifecycle.terminalOutcome !== 'PartialSuccess') return '';
  // Use concise reasons for known failures; preserve the owner's explanation
  // for transfer/extractor failures that cannot be explained by an enum alone.
  switch (job.lifecycle.failureReason) {
    case 'NoSearchResults': return 'No search results';
    case 'NoMatchingResults': return 'No matching results';
    case 'InvalidSearchString': return 'Invalid search query';
    case 'OutOfDownloadRetries': return 'Download retries exhausted';
    case 'Interrupted': return 'Interrupted';
    case 'ChildJobsFailed':
      if (Number(job.stats?.failedMemberCount) > 0) return '';
      break;
  }
  return job.lifecycle.failureMessage?.trim() || (job.lifecycle.failureReason === 'AllDownloadsFailed' ? 'All downloads failed'
    : job.lifecycle.failureReason === 'ExtractionFailed' ? 'Import failed' : '');
}

export function jobStatusClass(job: JobSummaryDto, transfer?: TransferStateDto): string {
  if (job.lifecycle.lifecycleState !== 'Terminal') {
    if (job.lifecycle.lifecycleState === 'Pending') return 'pending';
    if (job.lifecycle.activityPhase === 'Downloading' && !transfer?.status.isTerminal
      && transfer?.status.phase.startsWith('Queued')) return 'pending';
    return job.display.kind === 'search' ? 'searching' : 'receiving';
  }
  switch (job.lifecycle.terminalOutcome) {
    case 'Succeeded': return 'complete';
    case 'Cancelled': return 'cancelled';
    case 'Skipped': return 'skipped';
    case 'PartialSuccess': return 'partial';
    default: return 'failed';
  }
}

/** Describe the execution decision, without claiming an occupied path has equivalent content. */
export function jobSkipExplanation(job: JobSummaryDto | null | undefined,
  source?: Schema<'ServerExistingFileSource'> | null): string {
  if (job?.lifecycle.terminalOutcome !== 'Skipped') return '';
  switch (source) {
    case 'CompletionHistory': return 'Recorded as completed in the download index';
    case 'Library': return 'Matching music found in a local directory';
    case 'Destination': return 'Destination already exists';
  }
  switch (job.lifecycle.skipReason) {
    case 'NotFoundLastTime': return 'Not found during a previous search';
    case 'Manual': return 'Skipped manually';
    case 'Filtered': return 'Excluded by conditions';
    case 'AlreadyExists': return 'Already exists';
    default: return 'Skipped';
  }
}
