import type { JobSummaryDto, Schema } from '../../api/types';

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
    id = detail.summary.parentJobId;
  }
  return ancestors.reverse();
}
