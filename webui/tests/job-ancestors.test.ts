import { expect, it, vi } from 'vitest';
import { loadJobAncestors } from '../src/domains/jobs/ancestors';

it('loads only ancestors in breadcrumb order and reports cyclic relationships', async () => {
  const fetch = vi.fn().mockResolvedValueOnce({ summary: { jobId: 'parent', parentJobId: 'root' } })
    .mockResolvedValueOnce({ summary: { jobId: 'root' } });
  expect((await loadJobAncestors('parent', fetch, new AbortController().signal)).map((job) => job.jobId))
    .toEqual(['root', 'parent']);
  expect(fetch.mock.calls).toEqual([['parent'], ['root']]);
  const cyclic = vi.fn().mockResolvedValue({ summary: { jobId: 'parent', parentJobId: 'parent' } });
  await expect(loadJobAncestors('parent', cyclic, new AbortController().signal)).rejects.toThrow('cycle');
  expect(cyclic).toHaveBeenCalledTimes(1);
});

it('stops before fetching more ancestors after navigation aborts', async () => {
  const controller = new AbortController();
  const fetch = vi.fn(async () => {
    controller.abort();
    return { summary: { jobId: 'parent', parentJobId: 'root' }, payload: null, childCount: 1 };
  });
  await expect(loadJobAncestors('parent', fetch, controller.signal)).rejects.toThrow();
  expect(fetch).toHaveBeenCalledTimes(1);
});
