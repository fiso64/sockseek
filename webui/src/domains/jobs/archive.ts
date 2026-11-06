import type { JobSummaryDto, Schema } from '../../api/types';

export function canArchiveRoot(job: JobSummaryDto): boolean {
  return canArchiveSubmission(job) && !job.parentJobId && job.role === 'user-root';
}

export function canArchiveSubmission(job: JobSummaryDto): boolean {
  // Remove targets the owning submission even when invoked from a terminal
  // child. The daemon validates that the whole submission is terminal.
  return Boolean(job.submissionId && job.jobId && job.lifecycleState === 'Terminal');
}

export function requireArchived(receipt: Schema<'SubmissionArchiveResponseDto'>): void {
  if (Number(receipt.rejectedSubmissionCount) > 0 || !receipt.archived) {
    const reasons = receipt.rejectionReasons.map((reason) => reason.reason).join(', ');
    throw new Error(`Submission was not archived${reasons ? `: ${reasons}` : '.'}`);
  }
}
