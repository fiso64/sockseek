import type { JobSummaryDto } from '../../api/types';

/** Whole-submission rerun is safe as a single-search action only for a search root. */
export function canReplaySearchRoot(
  job: JobSummaryDto | null | undefined,
): boolean {
  return Boolean(job?.jobId && job.display.submissionId && !job.relationships.parentJobId
    && job.display.role === 'user-root' && job.display.kind === 'search');
}

export function canReplaySearch(
  job: JobSummaryDto | null | undefined,
): job is JobSummaryDto & { display: { submissionId: string } } {
  return Boolean(job?.jobId && job.display.submissionId && job.display.kind === 'search');
}
