import type { JobSummaryDto } from '../../api/types';

/** Whole-submission rerun is safe as a single-search action only for a search root. */
export function canReplaySearchRoot(
  job: JobSummaryDto | null | undefined,
): boolean {
  return Boolean(job?.jobId && job.submissionId && !job.parentJobId
    && job.role === 'user-root' && job.kind === 'search');
}

export function canReplaySearch(
  job: JobSummaryDto | null | undefined,
): job is JobSummaryDto & { jobId: string; submissionId: string } {
  return Boolean(job?.jobId && job.submissionId && job.kind === 'search');
}
