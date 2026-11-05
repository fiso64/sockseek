import type { JobSummaryDto } from '../../api/types';
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
