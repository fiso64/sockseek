import type { JobSummaryDto, Schema } from '../../api/types';

export function canDeleteRoot(job: JobSummaryDto): boolean {
  return canDeleteSubmission(job) && !job.parentJobId && job.role === 'user-root';
}

export function canDeleteSubmission(job: JobSummaryDto): boolean {
  // Remove targets the owning submission even when invoked from a terminal
  // child. The daemon validates that the whole submission is terminal.
  return Boolean(job.submissionId && job.jobId && job.lifecycleState === 'Terminal');
}

export function requireDeleted(receipt: Schema<'DeleteSubmissionResponseDto'>): void {
  if (Number(receipt.rejectedSubmissionCount) > 0) {
    const reasons = receipt.rejectionReasons.map((reason) => reason.reason).join(', ');
    throw new Error(`Submission could not be deleted${reasons ? `: ${reasons}` : '.'}`);
  }
}
