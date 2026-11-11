import type { JobSummaryDto, Schema } from '../../api/types';

export interface PresentedJob {
  root: JobSummaryDto;
  job: JobSummaryDto;
  missingResultId: string | null;
  error: Error | null;
}

export function completedExtract(job: JobSummaryDto): boolean {
  return job.kind === 'extract' && job.lifecycleState === 'Terminal'
    && job.terminalOutcome === 'Succeeded' && Boolean(job.resultJobId);
}

/** CLI result printing runs discovery without selecting a download target. */
export function showsSearchResults(job: JobSummaryDto): boolean {
  // ServerPrintOption.Results | Link | Json, matching DownloadSettings.PrintResults.
  return job.kind === 'search' || Boolean((job.printOption ?? 0) & (2 | 8 | 16))
    && ['song', 'album', 'aggregate', 'album-aggregate'].includes(job.kind ?? '');
}

/** Presentation only: extraction and its executable result remain distinct jobs. */
export function presentJob(root: JobSummaryDto, jobs: ReadonlyMap<string, JobSummaryDto>): PresentedJob {
  const seen = new Set<string>();
  let job = root;
  while (completedExtract(job)) {
    if (seen.has(job.jobId!)) return { root, job, missingResultId: null,
      error: new Error('The extracted-result relationships contain a cycle.') };
    seen.add(job.jobId!);
    const result = jobs.get(job.resultJobId!);
    if (!result) return { root, job, missingResultId: job.resultJobId!, error: null };
    if (root.submissionId && result.submissionId && root.submissionId !== result.submissionId) {
      return { root, job, missingResultId: null, error: new Error('The extracted result belongs to a different submission.') };
    }
    job = result;
  }
  return { root, job, missingResultId: null, error: null };
}

/** Hide implementation siblings even before extraction finishes; never hide unrelated follow-ups. */
export function presentedJobs(rows: readonly JobSummaryDto[], jobs: ReadonlyMap<string, JobSummaryDto>): PresentedJob[] {
  const results = new Set<string>();
  for (const job of jobs.values()) if (job.kind === 'extract' && job.resultJobId) {
    const result = jobs.get(job.resultJobId);
    if (!job.workflowId || !result?.workflowId || result.workflowId === job.workflowId) results.add(job.resultJobId);
  }
  return rows.filter((job) => !results.has(job.jobId!)
    && !(job.sourceJobId && jobs.get(job.sourceJobId)?.kind === 'extract'
      && (!job.workflowId || !jobs.get(job.sourceJobId)?.workflowId
        || job.workflowId === jobs.get(job.sourceJobId)?.workflowId)))
    .map((job) => presentJob(job, jobs));
}

/** Wire timestamps are instants, not lexically ordered strings: fractional seconds may be omitted. */
export function sortPresentedJobsNewestFirst(rows: PresentedJob[]): PresentedJob[] {
  const created = (entry: PresentedJob): number => {
    const value = Date.parse(entry.root.createdAtUtc ?? '');
    return Number.isFinite(value) ? value : Number.NEGATIVE_INFINITY;
  };
  return rows.sort((left, right) => created(right) - created(left)
    || Number(right.root.displayId ?? 0) - Number(left.root.displayId ?? 0));
}

/** A list contains execution siblings; folding completed Extracts deduplicates their results. */
export function presentedChildren(rows: readonly JobSummaryDto[], jobs: ReadonlyMap<string, JobSummaryDto>): PresentedJob[] {
  const seen = new Set<string>();
  return rows.map((row) => presentJob(row, jobs)).filter((entry) => {
    const id = entry.job.jobId!;
    if (seen.has(id)) return false;
    seen.add(id);
    return true;
  });
}

export async function resolvePresentedJob(
  detail: Schema<'JobDetailDto'>,
  fetchJob: (id: string) => Promise<Schema<'JobDetailDto'>>,
  signal: AbortSignal,
): Promise<Schema<'JobDetailDto'>> {
  const known = new Map<string, JobSummaryDto>([[detail.summary.jobId!, detail.summary]]);
  let current = detail;
  while (true) {
    signal.throwIfAborted();
    const presented = presentJob(detail.summary, known);
    if (presented.error) throw presented.error;
    if (!presented.missingResultId) return current;
    const resultId = presented.missingResultId;
    current = await fetchJob(resultId);
    signal.throwIfAborted();
    if (current.summary.jobId !== resultId) throw new Error('The extracted-result detail has a different job identity.');
    known.set(current.summary.jobId!, current.summary);
  }
}

export function extractSourceLabel(inputType: string | null | undefined): string {
  switch (inputType?.toLowerCase()) {
    case 'spotify': return 'Spotify';
    case 'youtube': return 'YouTube';
    case 'bandcamp': return 'Bandcamp';
    case 'soundcloud': return 'SoundCloud';
    case 'musicbrainz': return 'MusicBrainz';
    case 'soulseek': return 'Soulseek';
    case 'csv': return 'CSV';
    case 'list': return 'List';
    case 'string': return 'Text';
    default: return 'Unknown source';
  }
}
