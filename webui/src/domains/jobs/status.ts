import type { JobSummaryDto, Schema } from '../../api/types';
import { humanizeStateValue } from '../../app/format';
import type { TransferPresentation } from '../presentation/items';

/** Terminal file/folder cards use the same outcome colours as transfer history. */
export function jobTransferTone(job: JobSummaryDto | null | undefined): TransferPresentation['tone'] {
  if (job?.lifecycleState !== 'Terminal') return undefined;
  switch (job.terminalOutcome) {
    case 'Succeeded': return 'complete';
    case 'Cancelled': return 'cancelled';
    case 'Failed':
    case 'PartialSuccess': return 'failed';
    default: return undefined;
  }
}

export function jobStatusLabel(job: JobSummaryDto): string {
  if (job.lifecycleState === 'Terminal') {
    if (job.terminalOutcome === 'Succeeded') return 'Complete';
    return humanizeStateValue(job.terminalOutcome ?? 'Terminal');
  }
  if (job.lifecycleState === 'AwaitingSelection') return 'Awaiting selection';
  if (job.kind !== 'search') return job.lifecycleState === 'Running' ? 'Running' : 'Pending';
  return humanizeStateValue(job.activityPhase && job.activityPhase !== 'None'
    ? job.activityPhase : job.lifecycleState ?? 'Pending');
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
