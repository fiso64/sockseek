import type { JobSummaryDto, Schema } from '../../api/types';

export interface JobSummaryRead {
  summary: JobSummaryDto | null;
  error: Error | null;
}

/** Progress/discovery revisions do not invalidate a root's stable detail. */
export function jobDetailVersion(job: JobSummaryDto | undefined): string {
  return job ? JSON.stringify([job.jobId, job.lifecycle, job.relationships]) : '';
}

/** Active cached detail needs current live corroboration; terminal facts survive retirement. */
export function canReuseJobDetail(cached: JobSummaryDto, live: JobSummaryDto | undefined): boolean {
  return live ? jobDetailVersion(cached) === jobDetailVersion(live) : cached.lifecycle.lifecycleState === 'Terminal';
}

/** The owner handles read errors; keep one read and one trailing invalidation. */
export function startJobDetailRefresh(
  read: () => Promise<void>,
  signal: AbortSignal,
  shouldPoll: () => boolean,
  needsTrailingRefresh: () => boolean = () => true,
): () => void {
  let running = false;
  let pending = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  async function refresh() {
    clearTimeout(timer);
    if (signal.aborted) return;
    if (running) { pending = true; return; }
    running = true;
    try { await read(); }
    finally {
      running = false;
      if (!signal.aborted) {
        const trailing = pending && needsTrailingRefresh();
        pending = false;
        if (trailing) void refresh();
        else if (shouldPoll()) timer = setTimeout(() => void refresh(), 1_000);
      }
    }
  }
  signal.addEventListener('abort', () => clearTimeout(timer), { once: true });
  void refresh();
  return () => { void refresh(); };
}

/** Read linked summaries in one request; missing rows stay independently unavailable. */
export async function readJobSummaries(
  ids: readonly string[],
  getSummaries: (ids: readonly string[], signal: AbortSignal) => Promise<JobSummaryDto[]>,
  signal: AbortSignal,
): Promise<Map<string, JobSummaryRead>> {
  const requested = [...new Set(ids)];
  if (!requested.length || signal.aborted) return new Map();
  try {
    const rows = await getSummaries(requested, signal);
    if (signal.aborted) return new Map();
    const found = new Map(rows.map(row => [row.jobId, row]));
    return new Map(requested.map(id => [id, { summary: found.get(id) ?? null, error: null }]));
  } catch (cause) {
    if (signal.aborted) return new Map();
    const error = cause instanceof Error ? cause : new Error(String(cause));
    return new Map(requested.map(id => [id, { summary: null, error }]));
  }
}

/** One failed batch is one retry; a missing row remains an independent retry. */
export function jobSummaryRetryIds(reads: ReadonlyMap<string, JobSummaryRead>, id: string): string[] {
  const error = reads.get(id)?.error;
  return error ? [...reads].filter(([, read]) => read.error === error).map(([key]) => key) : [id];
}

export async function loadJobAncestors(
  parentId: string,
  fetchJob: (id: string) => Promise<Schema<'JobDetailDto'>>,
  signal: AbortSignal,
): Promise<JobSummaryDto[]> {
  const seen = new Set<string>();
  const ancestors: JobSummaryDto[] = [];
  let id: string | null | undefined = parentId;
  while (id) {
    signal.throwIfAborted();
    if (seen.has(id)) throw new Error('The job parent relationships contain a cycle.');
    seen.add(id);
    const detail = await fetchJob(id);
    signal.throwIfAborted();
    ancestors.push(detail.summary);
    id = detail.summary.relationships.parentJobId;
  }
  return ancestors.reverse();
}
