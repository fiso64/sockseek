import type { JobSummaryDto, Schema } from '../../api/types';
import type { TransferPresentation } from '../presentation/items';

const resumeTimeFormat = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** File/folder cards use the same activity and outcome colours as transfer history. */
export function jobTransferTone(job: JobSummaryDto | null | undefined): TransferPresentation['tone'] {
  if (!job || job.lifecycleState === 'Pending') return undefined;
  if (job.lifecycleState !== 'Terminal') return 'active';
  switch (job.terminalOutcome) {
    case 'Succeeded': return 'complete';
    case 'Cancelled': return 'cancelled';
    case 'Failed': return 'failed';
    case 'PartialSuccess': return 'partial';
    default: return undefined;
  }
}

export function jobStatusLabel(job: JobSummaryDto): string {
  if (job.lifecycleState === 'Terminal') {
    switch (job.terminalOutcome) {
      case 'Succeeded': return 'Complete';
      case 'PartialSuccess': return 'Partially complete';
      case 'Failed': return 'Failed';
      case 'Skipped': return 'Skipped';
      case 'Cancelled': return 'Cancelled';
      default: return 'Finished';
    }
  }
  if (job.lifecycleState === 'AwaitingSelection') return 'Awaiting selection';
  if (job.lifecycleState !== 'Running') return 'Pending';
  switch (job.activityPhase) {
    case 'WaitingForSearchConcurrency': return 'Waiting for search capacity';
    case 'SearchRateLimited': return job.activityUntilUtc
      ? `Search resumes at ${resumeTimeFormat.format(new Date(job.activityUntilUtc))}`
      : 'Search rate limited';
    case 'Searching': return 'Searching';
    case 'ProcessingSearchResults': return job.kind === 'search' ? 'Preparing results' : 'Choosing results';
    case 'Extracting': return 'Importing';
    case 'Downloading': return 'Downloading';
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
  if (job.lifecycleState !== 'Terminal') return '';
  if (job.terminalOutcome === 'Skipped') {
    const reason = jobSkipExplanation(job);
    return reason === 'Skipped' ? '' : reason;
  }
  if (job.terminalOutcome === 'Succeeded') {
    if (job.kind === 'song' || job.kind === 'remote-file') return 'Downloaded';
    if (job.kind === 'extract' && !job.resultJobId) return 'No job produced';
    return '';
  }
  if (job.terminalOutcome === 'Cancelled') {
    switch (job.cancellationSource) {
      case 'ParentJob': return 'Parent job cancelled';
      case 'UserRequestedJob': return 'Cancelled by you';
      case 'UserRequestedWorkflow': return 'Workflow cancelled';
      case 'UserRequestedAllJobs': return 'All jobs cancelled';
      case 'InternalEngine': return 'Cancelled by the engine';
      default: return '';
    }
  }
  if (job.terminalOutcome !== 'Failed' && job.terminalOutcome !== 'PartialSuccess') return '';
  // Use concise reasons for known failures; preserve the owner's explanation
  // for transfer/extractor failures that cannot be explained by an enum alone.
  switch (job.failureReason) {
    case 'NoSearchResults': return 'No search results';
    case 'NoMatchingResults': return 'No matching results';
    case 'InvalidSearchString': return 'Invalid search query';
    case 'OutOfDownloadRetries': return 'Download retries exhausted';
    case 'ChildJobsFailed':
      if (Number(job.stats?.failedMemberCount) > 0) return '';
      break;
  }
  return job.failureMessage?.trim() || (job.failureReason === 'AllDownloadsFailed' ? 'All downloads failed'
    : job.failureReason === 'ExtractionFailed' ? 'Import failed' : '');
}

export function jobStatusClass(job: JobSummaryDto): string {
  if (job.lifecycleState !== 'Terminal') {
    if (job.lifecycleState === 'Pending') return 'pending';
    return job.kind === 'search' ? 'searching' : 'receiving';
  }
  switch (job.terminalOutcome) {
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
  if (job?.terminalOutcome !== 'Skipped') return '';
  switch (source) {
    case 'CompletionHistory': return 'Recorded as completed in the download index';
    case 'Library': return 'Matching music found in a local directory';
    case 'Destination': return 'Destination already exists';
  }
  switch (job.skipReason) {
    case 'NotFoundLastTime': return 'Not found during a previous search';
    case 'Manual': return 'Skipped manually';
    case 'Filtered': return 'Excluded by conditions';
    case 'AlreadyExists': return 'Already exists';
    default: return 'Skipped';
  }
}
