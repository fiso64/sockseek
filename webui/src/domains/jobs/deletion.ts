import type { JobSummaryDto, Schema } from '../../api/types';

export function canDeleteSubmission(job: JobSummaryDto): boolean {
  return Boolean(job.submissionId && job.jobId && !job.parentJobId
    && job.role === 'user-root' && job.lifecycleState === 'Terminal');
}

export function requireDeleted(receipt: Schema<'DeleteSubmissionResponseDto'>): void {
  if (Number(receipt.rejectedSubmissionCount) > 0) {
    const reasons = receipt.rejectionReasons.map((reason) => reason.reason).join(', ');
    throw new Error(`Submission could not be deleted${reasons ? `: ${reasons}` : '.'}`);
  }
}
